using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// OpenAI 兼容 Provider（架构文档 §3.1）：一个类通吃 OpenAI/Ollama/vLLM/LM Studio
/// 等 /chat/completions 端点。视觉消息用 content 数组（text + image_url data URL）。
/// 错误分级：401 → auth；超时 → timeout；网络 → network；响应不可解析 → invalid-response。
/// </summary>
public sealed class OpenAiCompatibleModelProvider : IModelProvider
{
    private readonly ProviderConfig _config;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;

    public string Id => _config.Id;

    public ModelCapabilities Capabilities => _config.Capabilities;

    public OpenAiCompatibleModelProvider(
        ProviderConfig config,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? requestTimeout = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
        };
        foreach (var m in request.Messages)
        {
            var node = new JsonObject { ["role"] = RoleName(m.Role) };
            if (m.ImageDataUrl is null)
            {
                node["content"] = m.Content;
            }
            else
            {
                node["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = m.Content },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = m.ImageDataUrl },
                    });
            }
            messages.Add(node);
        }

        var body = new JsonObject
        {
            ["model"] = _config.ModelName,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
        };
        // 最大输出：配置了 MaxOutputTokens 用配置值（对话可放宽长聊）；
        // 未配置时若请求显式指定（互动/评论短句 120）则用该值；都空 = 不发送（上游默认）。
        var maxTokens = _config.MaxOutputTokens ?? request.MaxTokens;
        if (maxTokens is { } mt)
            body["max_tokens"] = mt;
        // 推理模型开关（如 sensenova-6.7-flash 不带 none 时 token 全被思考耗尽）：
        // 配置了 ReasoningEffort 才发送，兼容不认此参数的普通端点。
        if (!string.IsNullOrEmpty(_config.ReasoningEffort))
            body["reasoning_effort"] = _config.ReasoningEffort;

        var apiKey = ResolveApiKey();
        using var httpReq = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderEndpointPolicy.BuildRequestUri(_config.BaseUrl, "chat/completions", !string.IsNullOrEmpty(apiKey)))
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        ApplyAuth(httpReq, apiKey);
        // 部分网关/CDN 要求 UA（如 newapi.myovo.cc.cd）；统一带上应用标识。
        if (httpReq.Headers.UserAgent.Count == 0)
            httpReq.Headers.UserAgent.ParseAdd("DesktopPet/1.0");

        using var deadline = CreateDeadline(ct);
        var requestCt = deadline?.Token ?? ct;
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(
                httpReq,
                HttpCompletionOption.ResponseHeadersRead,
                requestCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        {
            throw new ProviderException("timeout", $"模型请求超时（{_config.Name}）");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("network", $"无法连接模型服务（{_config.Name}）", ex);
        }

        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new ProviderException("auth", "API Key 无效或无权限（401/403）");
            }
            if (!resp.IsSuccessStatusCode)
            {
                _ = await ReadResponseTextAsync(
                    resp.Content,
                    ct,
                    requestCt,
                    deadline,
                    $"模型请求超时（{_config.Name}）",
                    $"读取模型错误响应失败（{_config.Name}）").ConfigureAwait(false);
                throw CreateHttpFailure(resp.StatusCode, _config.Name);
            }

            JsonDocument doc;
            try
            {
                var text = await ReadResponseTextAsync(
                    resp.Content,
                    ct,
                    requestCt,
                    deadline,
                    $"模型请求超时（{_config.Name}）",
                    $"读取模型响应失败（{_config.Name}）").ConfigureAwait(false);
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new ProviderException("invalid-response", "模型响应无法解析", ex);
            }

            using (doc)
            {
                try
                {
                    var root = doc.RootElement;
                    var choices = root.GetProperty("choices");
                    if (choices.GetArrayLength() == 0)
                        throw new ProviderException("invalid-response", "模型响应为空（无 choices）");
                    var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                    var tokens = root.TryGetProperty("usage", out var usage)
                                 && usage.TryGetProperty("total_tokens", out var t)
                        ? t.GetInt32()
                        : 0;
                    return new ChatResult(content, tokens);
                }
                catch (Exception ex) when (ex is KeyNotFoundException
                                                  or InvalidOperationException
                                                  or FormatException
                                                  or OverflowException)
                {
                    throw new ProviderException("invalid-response", "模型响应缺少或包含无效字段", ex);
                }
            }
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var apiKey = ResolveApiKey();
        using var httpReq = new HttpRequestMessage(
            HttpMethod.Get,
            ProviderEndpointPolicy.BuildRequestUri(_config.BaseUrl, "models", !string.IsNullOrEmpty(apiKey)));
        ApplyAuth(httpReq, apiKey);

        using var deadline = CreateDeadline(ct);
        var requestCt = deadline?.Token ?? ct;
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(
                httpReq,
                HttpCompletionOption.ResponseHeadersRead,
                requestCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        {
            throw new ProviderException("timeout", $"连接测试超时（{_config.Name}）");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("network", $"无法连接模型服务（{_config.Name}）", ex);
        }

        using (resp)
        {
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new ProviderException("auth", "API Key 无效或无权限（401/403）");
            if (!resp.IsSuccessStatusCode)
            {
                _ = await ReadResponseTextAsync(
                    resp.Content,
                    ct,
                    requestCt,
                    deadline,
                    $"连接测试超时（{_config.Name}）",
                    $"读取模型错误响应失败（{_config.Name}）").ConfigureAwait(false);
                throw CreateHttpFailure(resp.StatusCode, _config.Name);
            }

            try
            {
                var text = await ReadResponseTextAsync(
                    resp.Content,
                    ct,
                    requestCt,
                    deadline,
                    $"连接测试超时（{_config.Name}）",
                    $"读取模型列表失败（{_config.Name}）").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                var list = new List<ModelInfo>();
                foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    var id = m.GetProperty("id").GetString() ?? "";
                    list.Add(new ModelInfo(id, id, InferCapabilities(id)));
                }
                return list;
            }
            catch (Exception ex) when (ex is JsonException
                                              or KeyNotFoundException
                                              or InvalidOperationException
                                              or FormatException
                                              or OverflowException)
            {
                throw new ProviderException("invalid-response", "模型列表响应无法解析", ex);
            }
        }
    }

    /// <summary>模型名 → 能力推断（vision 关键词：vl/vision/4o/omni 等）。</summary>
    private static ModelCapabilities InferCapabilities(string id)
    {
        var lower = id.ToLowerInvariant();
        var caps = ModelCapabilities.Chat;
        if (lower.Contains("vision") || lower.Contains("vl") || lower.Contains("4o")
            || lower.Contains("omni") || lower.Contains("gemini") || lower.Contains("claude"))
        {
            caps |= ModelCapabilities.Vision;
        }
        return caps;
    }

    private static async Task<string> ReadResponseTextAsync(
        HttpContent content,
        CancellationToken callerCt,
        CancellationToken requestCt,
        CancellationTokenSource? deadline,
        string timeoutMessage,
        string networkMessage)
    {
        try
        {
            return await content.ReadAsStringAsync(requestCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerCt.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (deadline?.IsCancellationRequested == true)
        {
            throw new ProviderException("timeout", timeoutMessage, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ProviderException("network", networkMessage, ex);
        }
    }

    private CancellationTokenSource? CreateDeadline(CancellationToken ct)
    {
        if (_requestTimeout == Timeout.InfiniteTimeSpan) return null;
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        return deadline;
    }

    private string? ResolveApiKey()
        => string.IsNullOrEmpty(_config.ApiKeyRef) ? null : _credentials.Get(_config.ApiKeyRef);

    private static void ApplyAuth(HttpRequestMessage req, string? key)
    {
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static ProviderException CreateHttpFailure(System.Net.HttpStatusCode status, string name)
    {
        var code = status switch
        {
            System.Net.HttpStatusCode.TooManyRequests => "rate-limit",
            >= System.Net.HttpStatusCode.InternalServerError => "server",
            _ => "http",
        };
        return new ProviderException(code, $"模型服务返回 {(int)status}（{name}）");
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

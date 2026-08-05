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

    public string Id => _config.Id;

    public ModelCapabilities Capabilities => _config.Capabilities;

    public OpenAiCompatibleModelProvider(
        ProviderConfig config,
        ICredentialStore credentials,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        _config = config;
        _credentials = credentials;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
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
            ["max_tokens"] = request.MaxTokens,
        };
        // 推理模型开关（如 sensenova-6.7-flash 不带 none 时 token 全被思考耗尽）：
        // 配置了 ReasoningEffort 才发送，兼容不认此参数的普通端点。
        if (!string.IsNullOrEmpty(_config.ReasoningEffort))
            body["reasoning_effort"] = _config.ReasoningEffort;

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, JoinUrl(_config.BaseUrl, "chat/completions"))
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        ApplyAuth(httpReq);
        // 部分网关/CDN 要求 UA（如 newapi.myovo.cc.cd）；统一带上应用标识。
        if (httpReq.Headers.UserAgent.Count == 0)
            httpReq.Headers.UserAgent.ParseAdd("DesktopPet/1.0");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
                var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new ProviderException("http", $"模型服务返回 {(int)resp.StatusCode}（{_config.Name}）: {errBody}");
            }

            JsonDocument doc;
            try
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                doc = JsonDocument.Parse(text);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                    throw new ProviderException("timeout", $"模型请求超时（{_config.Name}）");
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
                catch (KeyNotFoundException ex)
                {
                    throw new ProviderException("invalid-response", "模型响应缺少必需字段", ex);
                }
            }
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var httpReq = new HttpRequestMessage(HttpMethod.Get, JoinUrl(_config.BaseUrl, "models"));
        ApplyAuth(httpReq);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
                throw new ProviderException("http", $"模型服务返回 {(int)resp.StatusCode}（{_config.Name}）");

            try
            {
                var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                using (doc)
                {
                    var list = new List<ModelInfo>();
                    foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        var id = m.GetProperty("id").GetString() ?? "";
                        list.Add(new ModelInfo(id, id, InferCapabilities(id)));
                    }
                    return list;
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
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

    private void ApplyAuth(HttpRequestMessage req)
    {
        var key = string.IsNullOrEmpty(_config.ApiKeyRef) ? null : _credentials.Get(_config.ApiKeyRef);
        if (!string.IsNullOrEmpty(key))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private static string JoinUrl(string baseUrl, string path)
        => baseUrl.TrimEnd('/') + "/" + path;

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

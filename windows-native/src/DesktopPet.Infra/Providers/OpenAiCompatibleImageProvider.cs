using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// OpenAI 兼容生图 Provider（架构文档 §3.4）：POST {baseUrl}/images/generations，
/// response_format=b64_json；通吃 DALL·E / GPT-Image / Qwen-Image / FLUX 等端点。
/// 总结图开关默认关；生图失败由 App 层降级（不影响总结文本）。
/// </summary>
public sealed class OpenAiCompatibleImageProvider : IImageProvider
{
    private readonly ImageGenConfig _config;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;

    public OpenAiCompatibleImageProvider(
        ImageGenConfig config,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? requestTimeout = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(120);
    }

    public string Id => "openai-image";

    public async Task<ImageResult> GenerateAsync(ImageGenRequest request, CancellationToken ct)
    {
        var apiKey = _credentials.Get(_config.ApiKeyRef);
        // 空 key = 无鉴权（本地 ComfyUI/Ollama 端点；与 ModelProvider 行为一致）

        var body = new Dictionary<string, object>
        {
            ["model"] = _config.ModelName,
            ["prompt"] = request.Prompt,
            ["size"] = request.Size ?? _config.Size,
            ["n"] = 1,
            // 不发送 response_format：OpenAI 系默认返回 url、部分端点（如 agnes/litellm）
            // 直接拒绝顶层该参数（需放 extra_body）。本 Provider 对 b64_json/url 两种
            // 响应均已兼容（见下方解析），不发送最稳。
        };

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderEndpointPolicy.BuildRequestUri(
                _config.BaseUrl,
                "images/generations",
                !string.IsNullOrEmpty(apiKey)));
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        if (httpRequest.Headers.UserAgent.Count == 0)
            httpRequest.Headers.UserAgent.ParseAdd("DesktopPet/1.0"); // 与 ModelProvider 一致：部分网关要求 UA
        httpRequest.Content = JsonContent.Create(body);

        using var deadline = CreateDeadline(ct);
        var requestCt = deadline?.Token ?? ct;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, requestCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        {
            throw new ProviderException("timeout", "生图请求超时");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("network", $"生图网络错误: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new ProviderException("auth", "生图鉴权失败（Key 无效）");
            }
            if (!response.IsSuccessStatusCode)
            {
                // 错误分类与 ModelProvider.CreateHttpFailure 对齐：429→rate-limit、5xx→server
                var code = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.TooManyRequests => "rate-limit",
                    >= System.Net.HttpStatusCode.InternalServerError => "server",
                    _ => "network",
                };
                throw new ProviderException(code, $"生图 HTTP {(int)response.StatusCode}");
            }

            try
            {
                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(requestCt), cancellationToken: requestCt);
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0)
                    throw new ProviderException("invalid-response", "生图响应无数据");
                var first = data[0];
                // 优先 b64_json（OpenAI 系）；缺失时回退 url 字段（如 agnes-image 系），
                // 再发一次 GET 取图片字节。
                if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
                {
                    return new ImageResult(Convert.FromBase64String(b64.GetString()!));
                }
                if (first.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    HttpResponseMessage imageResp;
                    try
                    {
                        imageResp = await _http.GetAsync(urlEl.GetString()!, requestCt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline?.IsCancellationRequested == true)
                    {
                        throw new ProviderException("timeout", "生图图片下载超时");
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new ProviderException("network", $"生图图片下载失败: {ex.Message}", ex);
                    }
                    using (imageResp)
                    {
                        if (!imageResp.IsSuccessStatusCode)
                            throw new ProviderException("network", $"生图图片下载 HTTP {(int)imageResp.StatusCode}");
                        return new ImageResult(await imageResp.Content.ReadAsByteArrayAsync(requestCt));
                    }
                }
                throw new ProviderException("invalid-response", "生图响应缺少 b64_json/url");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline?.IsCancellationRequested == true)
            {
                // deadline 触发：裸 OCE 必须包装为 timeout（与 ModelProvider.ReadResponseTextAsync 一致）
                throw new ProviderException("timeout", "生图响应读取超时");
            }
            catch (JsonException ex)
            {
                throw new ProviderException("invalid-response", $"生图响应解析失败: {ex.Message}", ex);
            }
            catch (FormatException ex)
            {
                throw new ProviderException("invalid-response", $"生图响应 b64_json 解码失败: {ex.Message}", ex);
            }
        }
    }

    private CancellationTokenSource? CreateDeadline(CancellationToken ct)
    {
        if (_requestTimeout == Timeout.InfiniteTimeSpan) return null;
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        return deadline;
    }
}

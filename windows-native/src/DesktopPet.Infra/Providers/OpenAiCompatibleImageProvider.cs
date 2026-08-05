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

    public OpenAiCompatibleImageProvider(
        ImageGenConfig config,
        ICredentialStore credentials,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(30);
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

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _config.BaseUrl.TrimEnd('/') + "/images/generations");
        if (!string.IsNullOrEmpty(apiKey))
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = JsonContent.Create(body);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
                throw new ProviderException("network", $"生图 HTTP {(int)response.StatusCode}");
            }

            try
            {
                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
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
                    using var imageResp = await _http.GetAsync(urlEl.GetString()!, ct).ConfigureAwait(false);
                    imageResp.EnsureSuccessStatusCode();
                    return new ImageResult(await imageResp.Content.ReadAsByteArrayAsync(ct));
                }
                throw new ProviderException("invalid-response", "生图响应缺少 b64_json/url");
            }
            catch (JsonException ex)
            {
                throw new ProviderException("invalid-response", $"生图响应解析失败: {ex.Message}", ex);
            }
        }
    }
}

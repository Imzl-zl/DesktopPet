using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// HTTP 生图适配器基类（模板方法，windows-imagegen-design.md §5.1）。
/// 中转三坑在此兜底：① ProviderException 错误归一化 ② 400/422 参数降级重试 ③ 响应字节解析。
/// 子类只填两个钩子：BuildGenerateBody / ParseResponseAsync。
/// </summary>
public abstract class HttpImageAdapterBase : IImageGenProvider
{
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _strictParams; // true = 关闭参数降级（官方端点）

    protected HttpImageAdapterBase(
        ImageConnection connection,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? requestTimeout = null,
        bool strictParams = false)
    {
        Connection = connection;
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(300); // 实测慢渠道单张 3 分半
        _strictParams = strictParams;
    }

    public abstract string Family { get; }

    protected ImageConnection Connection { get; }

    public virtual Task<ImageGenOutput> GenerateAsync(ImageGenSpec spec, CancellationToken ct)
        => SendAsync(spec, null, ct);

    public virtual Task<ImageGenOutput> EditAsync(ImageGenSpec spec, IReadOnlyList<ReferenceImage> references, CancellationToken ct)
        => SendAsync(spec, references, ct);

    /// <summary>本请求是否携带可降级参数（中转不认识时去掉重试）。默认 true，子类按实际参数收紧。</summary>
    protected virtual bool HasReducibleParameters(ImageGenSpec spec) => true;

    /// <summary>钩子 1：构建生成请求 body（reduced=true 表示降级重试：去掉 background/quality/seed 等高风险参数）。</summary>
    protected abstract JsonObject BuildGenerateBody(ImageGenSpec spec, bool reduced);

    /// <summary>钩子 2：从成功响应解析图片字节（b64 优先 / url 回退下载 / inlineData 等）。</summary>
    protected abstract Task<byte[]> ParseResponseAsync(JsonDocument response, CancellationToken ct);

    /// <summary>编辑请求 body 的 image 字段形态（Grok 单对象 / 通用数组），子类可覆写。</summary>
    protected virtual JsonNode? BuildEditImages(IReadOnlyList<ReferenceImage> references)
        => BuildEditImagesArray(references);

    protected static JsonNode BuildEditImagesArray(IReadOnlyList<ReferenceImage> references)
    {
        var arr = new JsonArray();
        foreach (var r in references)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = ToDataUri(r) },
            });
        }
        return arr;
    }

    protected static JsonNode BuildEditImagesObject(IReadOnlyList<ReferenceImage> references)
    {
        var r = references[0];
        return new JsonObject { ["url"] = ToDataUri(r), ["type"] = "image_url" };
    }

    protected static string ToDataUri(ReferenceImage r)
        => $"data:{r.MimeType};base64,{Convert.ToBase64String(r.Bytes)}";

    /// <summary>鉴权头（Google 族用 x-goog-api-key，OpenAI 族用 Bearer）。</summary>
    protected virtual void ApplyAuth(HttpRequestMessage request, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>端点路径（OpenAI 族 images/*；Google 族 models/*:generateContent）。</summary>
    protected virtual string EndpointPath(bool references)
        => references ? "images/edits" : "images/generations";

    /// <summary>编辑请求 body 整体构建（Gemini 族覆写：参考图嵌 contents parts）。默认 = 生成 body + image 字段。</summary>
    protected virtual JsonObject BuildEditBody(ImageGenSpec spec, IReadOnlyList<ReferenceImage> references)
    {
        var body = BuildGenerateBody(spec, reduced: false);
        body["image"] = BuildEditImages(references);
        return body;
    }

    private async Task<ImageGenOutput> SendAsync(
        ImageGenSpec spec,
        IReadOnlyList<ReferenceImage>? references,
        CancellationToken ct)
    {
        var body = references is null
            ? BuildGenerateBody(spec, reduced: false)
            : BuildEditBody(spec, references);

        using var deadline = CreateDeadline(ct);
        var requestCt = deadline?.Token ?? ct;

        try
        {
            var response = await PostAsync(body, requestCt);
            using (response)
            {
                if (IsRejected(response) && !_strictParams && references is null
                    && HasReducibleParameters(spec))
                {
                    // 参数降级：中转端点不认识 background/quality/seed 等参数时，去掉重试一次
                    var reduced = BuildGenerateBody(spec, reduced: true);
                    var retry = await PostAsync(reduced, requestCt);
                    using (retry)
                    {
                        ThrowForStatus(retry, requestCt);
                        return await ParseSuccessfulAsync(retry, requestCt);
                    }
                }
                ThrowForStatus(response, requestCt);
                return await ParseSuccessfulAsync(response, requestCt);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        {
            throw new ProviderException("timeout", "生图请求超时");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("network", $"生图网络错误: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new ProviderException("invalid-response", $"生图响应解析失败: {ex.Message}", ex);
        }
        catch (FormatException ex)
        {
            throw new ProviderException("invalid-response", $"生图响应解码失败: {ex.Message}", ex);
        }
    }

    private static bool IsRejected(HttpResponseMessage response)
        => response.StatusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.UnprocessableEntity;

    private async Task<HttpResponseMessage> PostAsync(JsonObject body, CancellationToken ct)
    {
        var apiKey = _credentials.Get(Connection.ApiKeyRef); // 空 = 无鉴权（本地端点）
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderEndpointPolicy.BuildRequestUri(
                Connection.BaseUrl,
                EndpointPath(references: body.ContainsKey("image")),
                !string.IsNullOrEmpty(apiKey)));
        ApplyAuth(httpRequest, apiKey);
        if (httpRequest.Headers.UserAgent.Count == 0)
            httpRequest.Headers.UserAgent.ParseAdd("DesktopPet/1.0"); // 部分网关要求 UA
        httpRequest.Content = JsonContent.Create(body);
        return await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
    }

    private static void ThrowForStatus(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new ProviderException("auth", "生图鉴权失败（Key 无效）");
        }
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode switch
            {
                System.Net.HttpStatusCode.TooManyRequests => "rate-limit",
                >= System.Net.HttpStatusCode.InternalServerError => "server",
                _ => "network",
            };
            throw new ProviderException(code, $"生图 HTTP {(int)response.StatusCode}");
        }
    }

    private async Task<ImageGenOutput> ParseSuccessfulAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var bytes = await ParseResponseAsync(doc, ct);
        return new ImageGenOutput(bytes, "image/png");
    }

    /// <summary>子类 url 回退下载图片字节时复用（中转 url 有效期短，必须立即下载）。</summary>
    protected async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        var imageResp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        using (imageResp)
        {
            if (!imageResp.IsSuccessStatusCode)
                throw new ProviderException("network", $"生图图片下载 HTTP {(int)imageResp.StatusCode}");
            return await imageResp.Content.ReadAsByteArrayAsync(ct);
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

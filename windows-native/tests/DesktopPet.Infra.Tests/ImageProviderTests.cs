using System.Net;
using System.Text;
using System.Text.Json;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// Phase 6f：生图 Provider（架构文档 §3.4）——OpenAI 兼容 /images/generations，
/// 通吃 DALL·E / GPT-Image / Qwen-Image 等端点；总结图开关默认关（云端费用+隐私）。
/// </summary>
public class ImageProviderTests
{
    private sealed class MockHandler : HttpMessageHandler
    {
        public MockHandler()
        {
            Client = new HttpClient(this, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public HttpClient Client { get; }

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler { get; set; }
            = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(ct));
            }
            return await Handler(request, ct);
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static readonly ImageGenConfig Config = new(
        BaseUrl: "https://api.openai.com/v1",
        ApiKeyRef: "openai-key",
        ModelName: "gpt-image-1",
        Size: "1024x1024");

    private static OpenAiCompatibleImageProvider MakeProvider(MockHandler handler, string? apiKey = "sk-test")
    {
        var creds = new InMemoryCredentialStore();
        if (apiKey is not null) creds.Set("openai-key", apiKey);
        return new OpenAiCompatibleImageProvider(
            Config, creds, handler.Client, requestTimeout: TimeSpan.FromSeconds(30));
    }

    private static JsonElement BodyOf(MockHandler handler, int index = 0)
        => JsonDocument.Parse(handler.RequestBodies[index]).RootElement;

    [Fact]
    public async Task GenerateAsync_PostsOpenAiCompatibleRequest()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""
                {"data":[{"b64_json":"aGVsbG8tcG5n"}]}
                """)),
        };
        var provider = MakeProvider(handler);

        var result = await provider.GenerateAsync(
            new ImageGenRequest("一只像素桌宠的插画", Size: "1024x1024"), CancellationToken.None);

        Assert.Equal("hello-png"u8.ToArray(), result.PngBytes);
        Assert.Equal("https://api.openai.com/v1/images/generations", handler.Requests[0].RequestUri!.ToString());
        var body = BodyOf(handler);
        Assert.Equal("gpt-image-1", body.GetProperty("model").GetString());
        Assert.Equal("一只像素桌宠的插画", body.GetProperty("prompt").GetString());
        Assert.Equal(1, body.GetProperty("n").GetInt32());
        // 不发送 response_format：部分端点（agnes/litellm）拒绝顶层参数，url/b64 响应均兼容
        Assert.False(body.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task GenerateAsync_Unauthorized_ThrowsAuthError()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized)),
        };
        var provider = MakeProvider(handler);
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None));
        Assert.Equal("auth", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_NetworkFailure_ThrowsNetworkError()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => throw new HttpRequestException("offline"),
        };
        var provider = MakeProvider(handler);
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None));
        Assert.Equal("network", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_EmptyData_ThrowsResponseError()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""{"data":[]}""")),
        };
        var provider = MakeProvider(handler);
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None));
        Assert.Equal("invalid-response", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_UrlResponse_DownloadsImage()
    {
        // agnes-image 系端点：返回 url 而非 b64_json（b64_json=null）
        var handler = new MockHandler
        {
            Handler = (req, _) =>
            {
                if (req.RequestUri!.AbsoluteUri.StartsWith("https://cdn.example.com/"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent("png-bytes"u8.ToArray()),
                    });
                }
                return Task.FromResult(JsonResponse("""
                    {"data":[{"b64_json":null,"url":"https://cdn.example.com/out.png"}]}
                    """));
            },
        };
        var provider = MakeProvider(handler);

        var result = await provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None);

        Assert.Equal("png-bytes"u8.ToArray(), result.PngBytes);
        Assert.Equal(2, handler.Requests.Count); // 生成请求 + 图片下载请求
        Assert.Equal("https://cdn.example.com/out.png", handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GenerateAsync_NoApiKey_SendsUnauthenticatedRequest()
    {
        // 空 key = 无鉴权（本地端点可用，与 ModelProvider 一致）
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""
                {"data":[{"b64_json":"aGVsbG8tcG5n"}]}
                """)),
        };
        var provider = MakeProvider(handler, apiKey: null);

        var result = await provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None);

        Assert.Equal("hello-png"u8.ToArray(), result.PngBytes);
        Assert.Null(handler.Requests[0].Headers.Authorization); // 无 Authorization 头
    }

    [Fact]
    public async Task GenerateAsync_TooManyRequests_ThrowsRateLimitError()
    {
        // 错误分类与 ModelProvider 对齐（Microsoft 契约 §8）：429 → rate-limit，不是 network
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""{"error":{}}""", HttpStatusCode.TooManyRequests)),
        };
        var provider = MakeProvider(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None));
        Assert.Equal("rate-limit", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_ServerError_ThrowsServerError()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""{"error":{}}""", HttpStatusCode.InternalServerError)),
        };
        var provider = MakeProvider(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None));
        Assert.Equal("server", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_UrlDownloadFailure_ThrowsNetworkError()
    {
        // url 回退下载失败必须包装成 ProviderException（与 ModelProvider 的
        // ReadResponseTextAsync 一致），不裸抛 HttpRequestException
        var handler = new MockHandler
        {
            Handler = (req, _) =>
            {
                if (req.RequestUri!.AbsoluteUri.StartsWith("https://cdn.example.com/"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
                return Task.FromResult(JsonResponse("""
                    {"data":[{"b64_json":null,"url":"https://cdn.example.com/out.png"}]}
                    """));
            },
        };
        var provider = MakeProvider(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None));
        Assert.Equal("network", ex.Code);
    }

    [Fact]
    public async Task GenerateAsync_SendsUserAgent()
    {
        // 与 ModelProvider 一致：部分网关/CDN 要求 UA
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""
                {"data":[{"b64_json":"aGVsbG8tcG5n"}]}
                """)),
        };
        var provider = MakeProvider(handler);

        await provider.GenerateAsync(new ImageGenRequest("x"), CancellationToken.None);

        Assert.Contains(handler.Requests[0].Headers.UserAgent, h => h.Product is not null);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

/// <summary>OpenAI 兼容族适配器（windows-imagegen-design.md §5）：像素换算、b64/url 解析、参数降级、编辑。</summary>
public class OpenAiImageGenAdapterTests
{
    private sealed class StubCredentialStore(string? value) : ICredentialStore
    {
        public string? Get(string key) => value;
        public void Set(string key, string value) { }
        public bool Delete(string key) => true;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        public RecordingHandler(Func<string, HttpRequestMessage, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            return await _responder(body, request);
        }
    }

    private static ImageConnection Connection(string model = "gpt-image-2") => new(
        Id: "test", Name: "测试", Family: "openai",
        BaseUrl: "https://api.test.local/v1", ApiKeyRef: "cred:test",
        Models: [model]);

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false);

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static byte[] FakePng()
    {
        // 最小合法 PNG（1x1 透明）：真实 b64 解码路径验证
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    }

    // ── 尺寸换算 ──

    [Theory]
    [InlineData(ImageAspectRatio.R1x1, ImageScale.S1K, "1024x1024")]
    [InlineData(ImageAspectRatio.R3x2, ImageScale.S1K, "1536x1024")]
    [InlineData(ImageAspectRatio.R2x3, ImageScale.S1K, "1024x1536")]
    [InlineData(ImageAspectRatio.R16x9, ImageScale.S1K, "1824x1024")]
    [InlineData(ImageAspectRatio.R16x9, ImageScale.S2K, "3648x2048")]
    [InlineData(ImageAspectRatio.R21x9, ImageScale.S2K, "3840x1648")] // 长边超限 → clamp 3840
    [InlineData(ImageAspectRatio.R16x9, ImageScale.S4K, "3840x2160")] // 官方 4K landscape
    [InlineData(ImageAspectRatio.R9x16, ImageScale.S4K, "2160x3840")] // 官方 4K portrait
    [InlineData(ImageAspectRatio.R1x1, ImageScale.S4K, "2880x2880")]  // 总像素上限兜底
    public void ResolveSize_MatchesConstraints(ImageAspectRatio ratio, ImageScale scale, string expected)
        => Assert.Equal(expected, OpenAiImageGenAdapter.ResolveSize(ratio, scale));

    [Fact]
    public void ResolveSize_AllSizes_Are16Multiples()
    {
        foreach (ImageAspectRatio ratio in Enum.GetValues<ImageAspectRatio>())
        {
            if (ratio == ImageAspectRatio.Auto) continue;
            foreach (ImageScale scale in Enum.GetValues<ImageScale>())
            {
                var (w, h) = OpenAiImageGenAdapter.ResolveSizePx(ratio, scale);
                Assert.Equal(0, w % 16);
                Assert.Equal(0, h % 16);
                Assert.True(w <= 3840 && h <= 3840, $"{ratio}/{scale}: {w}x{h} 超长边");
                Assert.True((long)w * h <= 8_294_400, $"{ratio}/{scale}: {w}x{h} 超总像素");
            }
        }
    }

    // ── 生成 + b64 解析 ──

    [Fact]
    public async Task Generate_B64Response_ReturnsBytes()
    {
        var b64 = Convert.ToBase64String(FakePng());
        var handler = new RecordingHandler((_, __) => Task.FromResult(
            JsonResponse(new { data = new[] { new { b64_json = b64 } } })));
        var adapter = new OpenAiImageGenAdapter(Connection(), new StubCredentialStore("k"), Client(handler));

        var output = await adapter.GenerateAsync(
            new ImageGenSpec("cat", ImageAspectRatio.R1x1, ImageScale.S1K), CancellationToken.None);

        Assert.Equal(FakePng(), output.Bytes);
        Assert.Equal("image/png", output.MimeType);
        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(new Uri("https://api.test.local/v1/images/generations"), req.RequestUri);
        Assert.Equal("Bearer k", req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Generate_UrlResponse_DownloadsImage()
    {
        var png = FakePng();
        var handler = new RecordingHandler((body, req) =>
        {
            if (req.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(png),
                });
            return Task.FromResult(JsonResponse(new { data = new[] { new { url = "https://cdn.test/x.png" } } }));
        });
        var adapter = new OpenAiImageGenAdapter(Connection(), new StubCredentialStore(null), Client(handler));

        var output = await adapter.GenerateAsync(new ImageGenSpec("cat"), CancellationToken.None);

        Assert.Equal(png, output.Bytes);
        Assert.Equal(2, handler.Requests.Count); // POST + 图片下载
    }

    [Fact]
    public async Task Generate_RequestBody_ContainsSizeQualityBackground()
    {
        var handler = new RecordingHandler((_, __) => Task.FromResult(JsonResponse(
            new { data = new[] { new { b64_json = Convert.ToBase64String(FakePng()) } } })));
        var adapter = new OpenAiImageGenAdapter(Connection("gpt-image-1.5"), new StubCredentialStore(null), Client(handler));

        await adapter.GenerateAsync(new ImageGenSpec(
            "cat", ImageAspectRatio.R16x9, ImageScale.S2K, ImageQuality.High, Transparent: true),
            CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.Equal("gpt-image-1.5", body.GetProperty("model").GetString());
        Assert.Equal("3648x2048", body.GetProperty("size").GetString());
        Assert.Equal("high", body.GetProperty("quality").GetString());
        Assert.Equal("transparent", body.GetProperty("background").GetString());
    }

    [Fact]
    public async Task Generate_Transparent_SendsBackground()
    {
        // 适配器不做能力判断（能力过滤在门面层）：Transparent=true 即直传 background
        var handler = new RecordingHandler((_, __) => Task.FromResult(JsonResponse(
            new { data = new[] { new { b64_json = Convert.ToBase64String(FakePng()) } } })));
        var adapter = new OpenAiImageGenAdapter(Connection("gpt-image-2"), new StubCredentialStore(null), Client(handler));

        await adapter.GenerateAsync(new ImageGenSpec("cat", Transparent: true), CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.Equal("transparent", body.GetProperty("background").GetString());
    }

    // ── 错误归一化 ──

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "auth")]
    [InlineData(HttpStatusCode.Forbidden, "auth")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate-limit")]
    [InlineData(HttpStatusCode.InternalServerError, "server")]
    [InlineData(HttpStatusCode.NotFound, "network")]
    public async Task Generate_HttpError_Normalized(HttpStatusCode status, string expectedCode)
    {
        var handler = new RecordingHandler((_, __) => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent("{}") }));
        var adapter = new OpenAiImageGenAdapter(Connection(), new StubCredentialStore(null), Client(handler));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            adapter.GenerateAsync(new ImageGenSpec("cat"), CancellationToken.None));

        Assert.Equal(expectedCode, ex.Code);
    }

    // ── 参数降级 ──

    [Fact]
    public async Task Generate_BadRequestWithQuality_RetriesReducedBody()
    {
        var handler = new RecordingHandler((body, req) =>
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(body);
            if (parsed.TryGetProperty("quality", out _))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("unknown parameter: quality"),
                });
            }
            return Task.FromResult(JsonResponse(
                new { data = new[] { new { b64_json = Convert.ToBase64String(FakePng()) } } }));
        });
        var adapter = new OpenAiImageGenAdapter(Connection(), new StubCredentialStore(null), Client(handler));

        var output = await adapter.GenerateAsync(
            new ImageGenSpec("cat", Quality: ImageQuality.High, Transparent: true), CancellationToken.None);

        Assert.Equal(FakePng(), output.Bytes);
        Assert.Equal(2, handler.Requests.Count);
        var reducedBody = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[1]);
        Assert.False(reducedBody.TryGetProperty("quality", out _));
        Assert.False(reducedBody.TryGetProperty("background", out _));
    }

    [Fact]
    public async Task Generate_BadRequestStrictParams_NoRetry()
    {
        var handler = new RecordingHandler((_, __) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("nope") }));
        var adapter = new OpenAiImageGenAdapter(
            Connection(), new StubCredentialStore(null), Client(handler), strictParams: true);

        await Assert.ThrowsAsync<ProviderException>(() =>
            adapter.GenerateAsync(new ImageGenSpec("cat", Quality: ImageQuality.High), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Generate_BadRequestWithoutExtraParams_NoRetry()
    {
        // 请求本来就没带高风险参数：400 不该盲目重试
        var handler = new RecordingHandler((_, __) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad prompt") }));
        var adapter = new OpenAiImageGenAdapter(Connection(), new StubCredentialStore(null), Client(handler));

        await Assert.ThrowsAsync<ProviderException>(() =>
            adapter.GenerateAsync(new ImageGenSpec("cat"), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    // ── 编辑 ──

    [Fact]
    public async Task Edit_GenericModel_UsesImagesArray()
    {
        var handler = new RecordingHandler((_, __) => Task.FromResult(JsonResponse(
            new { data = new[] { new { b64_json = Convert.ToBase64String(FakePng()) } } })));
        var adapter = new OpenAiImageGenAdapter(Connection("Qwen/Qwen-Image-Edit"), new StubCredentialStore(null), Client(handler));

        await adapter.EditAsync(new ImageGenSpec("make it red"),
            [new ReferenceImage(FakePng(), "image/png")], CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.EndsWith("/images/edits", handler.Requests[0].RequestUri!.AbsolutePath);
        var images = body.GetProperty("image");
        Assert.Equal(JsonValueKind.Array, images.ValueKind);
        Assert.Equal("image_url", images[0].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", images[0].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Edit_GrokModel_UsesSingleObject()
    {
        var handler = new RecordingHandler((_, __) => Task.FromResult(JsonResponse(
            new { data = new[] { new { b64_json = Convert.ToBase64String(FakePng()) } } })));
        var adapter = new OpenAiImageGenAdapter(Connection("grok-imagine-image-quality"), new StubCredentialStore(null), Client(handler));

        await adapter.EditAsync(new ImageGenSpec("sketch it"),
            [new ReferenceImage(FakePng())], CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        var images = body.GetProperty("image");
        Assert.Equal(JsonValueKind.Object, images.ValueKind);
        var url = images.GetProperty("url").GetString();
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal("image_url", images.GetProperty("type").GetString());
    }

    // ── 超时 ──

    [Fact]
    public async Task Generate_Timeout_ThrowsTimeout()
    {
        var handler = new SlowHandler();
        var adapter = new OpenAiImageGenAdapter(
            Connection(), new StubCredentialStore(null), Client(handler),
            requestTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<ProviderException>(async () =>
            await adapter.GenerateAsync(new ImageGenSpec("cat"), CancellationToken.None));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

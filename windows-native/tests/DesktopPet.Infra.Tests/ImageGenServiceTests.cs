using System.Net;
using System.Text.Json;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Providers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DesktopPet.Infra.Tests;

/// <summary>生图门面（windows-imagegen-design.md §3）：能力分流、透明策略编排、适配器缓存。</summary>
public class ImageGenServiceTests
{
    private sealed class StubCredentialStore(string? value) : ICredentialStore
    {
        public string? Get(string key) => value;
        public void Set(string key, string value) { }
        public bool Delete(string key) => true;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpRequestMessage, HttpResponseMessage> _responder;
        public List<string> RequestBodies { get; } = [];
        public List<Uri> RequestUris { get; } = [];

        public RecordingHandler(Func<string, HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            RequestBodies.Add(body);
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(_responder(body, request));
        }
    }

    private static ImageConnection Connection(string model, string family = "openai") => new(
        Id: "test", Name: "测试", Family: family,
        BaseUrl: "https://api.test.local/v1", ApiKeyRef: "cred:test",
        Models: [model]);

    private static byte[] FakePng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static HttpResponseMessage OkB64()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(
                new { data = new[] { new { b64_json = Convert.ToBase64String(FakePng()) } } })),
        };

    private static ImageGenService Service(RecordingHandler handler, string? key = "k")
        => new(ImageModelCatalog.LoadBuiltIn(), new StubCredentialStore(key),
            new HttpClient(handler, disposeHandler: false));

    [Fact]
    public async Task Generate_NonTransparent_PassesThroughWithoutStrategy()
    {
        var handler = new RecordingHandler((_, __) => OkB64());
        var service = Service(handler);

        var output = await service.GenerateAsync(
            Connection("gpt-image-2"), "gpt-image-2",
            new ImageGenSpec("a cat"), CancellationToken.None);

        Assert.Equal(FakePng(), output.Bytes);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.Equal("a cat", body.GetProperty("prompt").GetString()); // 无绿幕增强
        Assert.False(body.TryGetProperty("background", out _));        // 无透明请求不发
    }

    [Fact]
    public async Task Generate_TransparentOnNativeModel_SendsBackgroundDirectly()
    {
        var handler = new RecordingHandler((_, __) => OkB64());
        var service = Service(handler);

        await service.GenerateAsync(
            Connection("gpt-image-1.5"), "gpt-image-1.5",
            new ImageGenSpec("a cat", Transparent: true), CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.Equal("transparent", body.GetProperty("background").GetString()); // 原生直传
        Assert.Equal("a cat", body.GetProperty("prompt").GetString());           // prompt 不增强
    }

    [Fact]
    public async Task Generate_TransparentOnNonNativeModel_UsesChromakeyPipeline()
    {
        // gpt-image-2：无原生透明 → 绿幕两段式：prompt 增强 + 无 background 参数 + 输出经 HSV 键控
        var greenPng = MakeGreenPng(32, 32);
        var handler = new RecordingHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(
                new { data = new[] { new { b64_json = Convert.ToBase64String(greenPng) } } })),
        });
        var service = Service(handler);

        var output = await service.GenerateAsync(
            Connection("gpt-image-2"), "gpt-image-2",
            new ImageGenSpec("a cat", Transparent: true), CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.Contains("CHROMAKEY", body.GetProperty("prompt").GetString());
        Assert.Contains("#00FF00", body.GetProperty("prompt").GetString());
        Assert.False(body.TryGetProperty("background", out _));

        // 输出是键控后的 RGBA PNG（纯绿输入 → 全透明）
        Assert.Equal("image/png", output.MimeType);
        using var result = Image.Load<Rgba32>(output.Bytes);
        Assert.Equal(0, result[16, 16].A);
    }

    [Fact]
    public async Task Generate_AdapterCachedPerConnection()
    {
        var handler = new RecordingHandler((_, __) => OkB64());
        var service = Service(handler);
        var connection = Connection("gpt-image-2");

        await service.GenerateAsync(connection, "gpt-image-2", new ImageGenSpec("a"), CancellationToken.None);
        await service.GenerateAsync(connection, "gpt-image-2", new ImageGenSpec("b"), CancellationToken.None);

        Assert.Equal(2, handler.RequestBodies.Count); // 同连接复用同一适配器（同一 HttpClient）
    }

    [Fact]
    public async Task Generate_UnknownFamily_ThrowsUnsupported()
    {
        var service = Service(new RecordingHandler((_, __) => OkB64()));
        var connection = Connection("x", family: "volcengine"); // Seedream 火山方舟等未接入族

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            service.GenerateAsync(connection, "x", new ImageGenSpec("a"), CancellationToken.None));

        Assert.Equal("unsupported-family", ex.Code);
    }

    [Fact]
    public async Task Generate_GoogleFamily_UsesGeminiAdapter()
    {
        var handler = new RecordingHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new { content = new { parts = new object[]
                    {
                        new { inlineData = new { mimeType = "image/png", data = Convert.ToBase64String(FakePng()) } },
                    } } },
                },
            })),
        });
        var service = Service(handler);
        var connection = Connection("gemini-3.1-flash-image", family: "google");

        var output = await service.GenerateAsync(connection, "gemini-3.1-flash-image",
            new ImageGenSpec("a cat", ImageAspectRatio.R16x9, ImageScale.S2K, Transparent: true), CancellationToken.None);

        // 透明请求走绿幕两段式：输出经 HSV 键控重新编码（不再等于原始字节）
        Assert.Equal("image/png", output.MimeType);
        using var decoded = Image.Load<Rgba32>(output.Bytes);
        Assert.Equal(1, decoded.Width);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.Contains("CHROMAKEY", body.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("16:9", body.GetProperty("generationConfig").GetProperty("imageConfig").GetProperty("aspectRatio").GetString());
    }

    [Fact]
    public async Task Edit_TransparentOnNonNativeModel_AppliesStrategy()
    {
        var greenPng = MakeGreenPng(16, 16);
        var handler = new RecordingHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(
                new { data = new[] { new { b64_json = Convert.ToBase64String(greenPng) } } })),
        });
        var service = Service(handler);

        var output = await service.EditAsync(
            Connection("gpt-image-2"), "gpt-image-2",
            new ImageGenSpec("make it cuter", Transparent: true),
            [new ReferenceImage(FakePng())], CancellationToken.None);

        Assert.EndsWith("/images/edits", handler.RequestUris[0].AbsolutePath);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.Contains("CHROMAKEY", body.GetProperty("prompt").GetString());
        using var result = Image.Load<Rgba32>(output.Bytes);
        Assert.Equal(0, result[8, 8].A);
    }

    [Fact]
    public void CapabilitiesFor_UnknownModel_FallsBackToFamilyDefaults()
    {
        var service = Service(new RecordingHandler((_, __) => OkB64()));
        var caps = service.CapabilitiesFor(Connection("my-relay-model"), "my-relay-model");
        Assert.False(caps.NativeTransparency);
        Assert.Contains(ImageScale.S2K, caps.Scales);
    }

    private static byte[] MakeGreenPng(int w, int h)
    {
        using var image = new Image<Rgba32>(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                image[x, y] = new Rgba32(0, 255, 0, 255);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}

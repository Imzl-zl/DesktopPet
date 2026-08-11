using System.Net;
using System.Text.Json;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

/// <summary>Gemini 族适配器（windows-imagegen-design.md §5.2）：generateContent 请求/响应、编辑多模态。</summary>
public class GeminiImageGenAdapterTests
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
        public List<HttpRequestMessage> Requests { get; } = [];

        public RecordingHandler(Func<string, HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Content is null ? "" : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            RequestBodies.Add(body);
            return Task.FromResult(_responder(body, request));
        }
    }

    private static ImageConnection Connection(string model = "gemini-3.1-flash-image") => new(
        Id: "google-test", Name: "Google", Family: "google",
        BaseUrl: "https://generativelanguage.googleapis.com/v1beta", ApiKeyRef: "cred:g",
        Models: [model]);

    private static byte[] FakePng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static HttpResponseMessage OkInlineData()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new object[]
                            {
                                new { inlineData = new { mimeType = "image/png", data = Convert.ToBase64String(FakePng()) } },
                            },
                        },
                    },
                },
            })),
        };

    private static GeminiImageGenAdapter Adapter(RecordingHandler handler, string? key = "k", string model = "gemini-3.1-flash-image")
        => new(Connection(model), model, new StubCredentialStore(key), new HttpClient(handler, disposeHandler: false));

    [Fact]
    public async Task Generate_UsesXGoogApiKeyHeader()
    {
        var handler = new RecordingHandler((_, __) => OkInlineData());
        var adapter = Adapter(handler);

        await adapter.GenerateAsync(new ImageGenSpec("a cat"), CancellationToken.None);

        var req = handler.Requests[0];
        Assert.True(req.Headers.TryGetValues("x-goog-api-key", out var keyValues));
        Assert.Equal("k", keyValues!.Single());
        Assert.Null(req.Headers.Authorization); // 不是 Bearer
        Assert.EndsWith(":generateContent", req.RequestUri!.AbsolutePath);
        Assert.Contains("/models/gemini-3.1-flash-image:", req.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task Generate_Body_ContainsModalitiesAndImageConfig()
    {
        var handler = new RecordingHandler((_, __) => OkInlineData());
        var adapter = Adapter(handler);

        await adapter.GenerateAsync(new ImageGenSpec(
            "a cat", ImageAspectRatio.R16x9, ImageScale.S2K), CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        Assert.Equal("IMAGE", body.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        var imageConfig = body.GetProperty("generationConfig").GetProperty("imageConfig");
        Assert.Equal("16:9", imageConfig.GetProperty("aspectRatio").GetString());
        Assert.Equal("2K", imageConfig.GetProperty("imageSize").GetString());
        Assert.Equal("a cat", body.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Generate_AutoRatio_OmitsAspectRatio()
    {
        var handler = new RecordingHandler((_, __) => OkInlineData());
        var adapter = Adapter(handler);

        await adapter.GenerateAsync(new ImageGenSpec("a cat", ImageAspectRatio.Auto), CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        var imageConfig = body.GetProperty("generationConfig").GetProperty("imageConfig");
        Assert.False(imageConfig.TryGetProperty("aspectRatio", out _));
        Assert.Equal("1K", imageConfig.GetProperty("imageSize").GetString());
    }

    [Fact]
    public async Task Generate_InlineDataResponse_ReturnsBytes()
    {
        var handler = new RecordingHandler((_, __) => OkInlineData());
        var adapter = Adapter(handler);

        var output = await adapter.GenerateAsync(new ImageGenSpec("a cat"), CancellationToken.None);

        Assert.Equal(FakePng(), output.Bytes);
        Assert.Equal("image/png", output.MimeType);
    }

    [Fact]
    public async Task Generate_TextOnlyResponse_ThrowsInvalidResponse()
    {
        var handler = new RecordingHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                candidates = new[]
                {
                    new { content = new { parts = new object[] { new { text = "I cannot do that" } } } },
                },
            })),
        });
        var adapter = Adapter(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            adapter.GenerateAsync(new ImageGenSpec("a cat"), CancellationToken.None));

        Assert.Equal("invalid-response", ex.Code);
    }

    [Fact]
    public async Task Generate_EmptyCandidates_ThrowsInvalidResponse()
    {
        var handler = new RecordingHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[]}"),
        });
        var adapter = Adapter(handler);

        await Assert.ThrowsAsync<ProviderException>(() =>
            adapter.GenerateAsync(new ImageGenSpec("a cat"), CancellationToken.None));
    }

    [Fact]
    public async Task Edit_ReferenceImages_EmbeddedAsInlineData()
    {
        var handler = new RecordingHandler((_, __) => OkInlineData());
        var adapter = Adapter(handler);

        await adapter.EditAsync(new ImageGenSpec("make it red"),
            [new ReferenceImage(FakePng(), "image/png")], CancellationToken.None);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBodies[0]);
        var parts = body.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("image/png", parts[0].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(FakePng()), parts[0].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("make it red", parts[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Generate_HttpError_Normalized()
    {
        var handler = new RecordingHandler((_, __) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":{\"message\":\"API key not valid\"}}"),
        });
        var adapter = Adapter(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            adapter.GenerateAsync(new ImageGenSpec("a cat"), CancellationToken.None));

        Assert.Equal("auth", ex.Code);
    }

    [Fact]
    public async Task Generate_BadRequest_NoRetry()
    {
        // Gemini 无 quality/background 可降级参数：400 直接抛错，不重试
        var handler = new RecordingHandler((_, __) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid aspect ratio\"}}"),
        });
        var adapter = Adapter(handler);

        await Assert.ThrowsAsync<ProviderException>(() =>
            adapter.GenerateAsync(new ImageGenSpec("a cat"), CancellationToken.None));

        Assert.Single(handler.Requests);
    }
}

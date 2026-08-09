using System.Net;
using System.Text;
using System.Text.Json;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Tts;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// OpenAI 兼容 TTS Provider（windows-tts-design.md §6.3）：/v1/audio/speech + /v1/audio/voices。
/// mock HttpClient 验证请求体/鉴权/错误分类；真机联通性验收见设计 §10 矩阵。
/// </summary>
public class OpenAiCompatibleTtsProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private static readonly TtsEndpointConfig Config =
        new("https://api.example.com/v1", "tts-key", "test-tts-model", "voice-1");

    private sealed class FakeCredentials : ICredentialStore
    {
        public string? Get(string key) => "sk-test-secret";
        public void Set(string key, string value) { }
        public bool Delete(string key) => true;
    }

    private static OpenAiCompatibleTtsProvider Create(FakeHandler handler)
        => new(Config, new FakeCredentials(), new HttpClient(handler));

    [Fact]
    public async Task SynthesizeAsync_PostsOpenAiCompatibleBody()
    {
        var mp3 = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(mp3),
        });
        var provider = Create(handler);

        using var stream = await provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "你好", VoiceId: "voice-1", SpeedPercent: 150), CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.example.com/v1/audio/speech", req.RequestUri!.ToString());
        // Bearer 鉴权（ApiKeyRef → CredentialStore）
        Assert.Equal("Bearer sk-test-secret", req.Headers.Authorization!.ToString());
        var body = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Assert.Single(handler.RequestBodies));
        Assert.Equal("test-tts-model", body!["model"].GetString());
        Assert.Equal("你好", body["input"].GetString());
        Assert.Equal("voice-1", body["voice"].GetString());
        Assert.Equal(1.5, body["speed"].GetDouble()); // SpeedPercent 150 → 1.5
        // 返回 MP3 字节流
        var bytes = ((MemoryStream)stream).ToArray();
        Assert.Equal(mp3, bytes);
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyVoice_UsesConfiguredDefault()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]),
        });
        var provider = Create(handler);
        using var stream = await provider.SynthesizeAsync(
            new TtsSynthesisRequest("hi", ""), CancellationToken.None);
        var body = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            Assert.Single(handler.RequestBodies));
        Assert.Equal("voice-1", body!["voice"].GetString()); // 回落配置默认音色
    }

    [Fact]
    public async Task SynthesizeAsync_Unauthorized_ThrowsProviderAuth()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.SynthesizeAsync(new TtsSynthesisRequest("hi", ""), CancellationToken.None));
        Assert.Equal("auth", ex.Code);
    }

    [Fact]
    public async Task SynthesizeAsync_BadRequest_ThrowsInvalidResponse()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"BadRquestData: voice not found\"}", Encoding.UTF8, "application/json"),
        });
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.SynthesizeAsync(new TtsSynthesisRequest("hi", "bad"), CancellationToken.None));
        Assert.Equal("invalid-response", ex.Code);
        Assert.Contains("voice not found", ex.Message); // 端点原文透传供排查
    }

    [Fact]
    public async Task SynthesizeAsync_NetworkError_ThrowsProviderNetwork()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.SynthesizeAsync(new TtsSynthesisRequest("hi", ""), CancellationToken.None));
        Assert.Equal("network", ex.Code);
    }

    [Fact]
    public async Task ListVoicesAsync_GetsVoicesEndpoint()
    {
        var voicesJson = """{"voices":[{"id":"voice-1","name":"晓晓","language":"zh-CN"},{"id":"voice-2","name":"云希","language":"zh-CN"}]}""";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(voicesJson, Encoding.UTF8, "application/json"),
        });
        var provider = Create(handler);

        var voices = await provider.ListVoicesAsync(CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://api.example.com/v1/audio/voices", req.RequestUri!.ToString());
        Assert.Equal(2, voices.Count);
        Assert.Equal("voice-1", voices[0].Id);
        Assert.Equal("晓晓", voices[0].DisplayName);
    }

    [Fact]
    public async Task ListVoicesAsync_EndpointNotFound_ReturnsEmpty()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = Create(handler);
        Assert.Empty(await provider.ListVoicesAsync(CancellationToken.None)); // 不支持列表 → 空（设置页手动输入）
    }

    [Fact]
    public async Task ListVoicesAsync_Unauthorized_ThrowsProviderAuth()
    {
        // 回归：401 被吞会显示“连接成功但未提供音色列表”假成功；必须显式抛 auth（审查实证 I-2）
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.ListVoicesAsync(CancellationToken.None));
        Assert.Equal("auth", ex.Code);
    }

    [Fact]
    public void Provider_Metadata_IsOnlineOpenAi()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = Create(handler);
        Assert.Equal("openai", provider.Id);
        Assert.True(provider.RequiresNetwork);
    }
}

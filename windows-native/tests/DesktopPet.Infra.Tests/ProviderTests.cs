using System.Net;
using System.Text;
using System.Text.Json;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// Phase 5e：OpenAI 兼容 Provider（架构文档 §3.1/§3.2）——
/// 一个类通吃 OpenAI/Ollama 等 /chat/completions 端点；apiKey 走 Credential Store 引用。
/// </summary>
public class ProviderTests
{
    // ---- Mock HTTP 基础设施 ----

    private sealed class MockHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler { get; set; }
            = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(ct)); // 在 dispose 前预读
            }
            return await Handler(request, ct);
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static readonly ProviderConfig Config = new(
        Id: "openai-default",
        Name: "OpenAI GPT-4o",
        BaseUrl: "https://api.openai.com/v1",
        ApiKeyRef: "openai-key",
        ModelName: "gpt-4o",
        Capabilities: ModelCapabilities.Chat | ModelCapabilities.Vision,
        IsDefault: true);

    private static OpenAiCompatibleModelProvider MakeProvider(MockHandler handler, string? apiKey = "sk-test")
    {
        var creds = new InMemoryCredentialStore();
        if (apiKey is not null) creds.Set("openai-key", apiKey);
        return new OpenAiCompatibleModelProvider(
            Config, creds, handler, timeout: TimeSpan.FromSeconds(30));
    }

    private static JsonElement BodyOf(MockHandler handler, int index = 0)
        => JsonDocument.Parse(handler.RequestBodies[index]).RootElement;

    // ---- 成功路径 ----

    [Fact]
    public async Task CompleteAsync_MapsResponseAndUsage()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""
                {"id":"chatcmpl-1","choices":[{"message":{"role":"assistant","content":"你好呀~"}}],
                 "usage":{"total_tokens":42}}
                """)),
        };
        var provider = MakeProvider(handler);

        var result = await provider.CompleteAsync(
            new ChatRequest("sys", [new ChatMessage(ChatRole.User, "hi")]), CancellationToken.None);

        Assert.Equal("你好呀~", result.Text);
        Assert.Equal(42, result.TokensUsed);
        Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task CompleteAsync_SendsAuthAndRequestShape()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"ok"}}],"usage":{"total_tokens":3}}""")),
        };
        var provider = MakeProvider(handler);

        await provider.CompleteAsync(
            new ChatRequest("sys-prompt", [new ChatMessage(ChatRole.User, "你好")],
                Temperature: 0.7, MaxTokens: 120),
            CancellationToken.None);

        var req = handler.Requests[0];
        Assert.Equal("Bearer sk-test", req.Headers.Authorization!.ToString());
        var body = BodyOf(handler);
        Assert.Equal("gpt-4o", body.GetProperty("model").GetString());
        Assert.Equal(0.7, body.GetProperty("temperature").GetDouble());
        Assert.Equal(120, body.GetProperty("max_tokens").GetInt32());
        var messages = body.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("sys-prompt", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("你好", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_SendsReasoningEffortWhenConfigured()
    {
        // 推理模型（如 sensenova-6.7-flash）：不带 none 时 token 全被思考耗尽
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"ok"}}],"usage":{"total_tokens":3}}""")),
        };
        var creds = new InMemoryCredentialStore();
        creds.Set("openai-key", "sk-test");
        var provider = new OpenAiCompatibleModelProvider(
            Config with { ReasoningEffort = "none" }, creds, handler, timeout: TimeSpan.FromSeconds(30));

        await provider.CompleteAsync(
            new ChatRequest("sys", [new ChatMessage(ChatRole.User, "你好")]),
            CancellationToken.None);

        Assert.Equal("none", BodyOf(handler).GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task CompleteAsync_OmitsReasoningEffortByDefault()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"ok"}}],"usage":{"total_tokens":3}}""")),
        };
        var provider = MakeProvider(handler);

        await provider.CompleteAsync(
            new ChatRequest("sys", [new ChatMessage(ChatRole.User, "你好")]),
            CancellationToken.None);

        Assert.False(BodyOf(handler).TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task CompleteAsync_SendsConfiguredMaxOutputTokens()
    {
        // 模型支持大输出（如 sensenova 64k）时，用户配置 MaxOutputTokens 覆盖短句默认
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"ok"}}],"usage":{"total_tokens":3}}""")),
        };
        var creds = new InMemoryCredentialStore();
        creds.Set("openai-key", "sk-test");
        var provider = new OpenAiCompatibleModelProvider(
            Config with { MaxOutputTokens = 4096 }, creds, handler, timeout: TimeSpan.FromSeconds(30));

        await provider.CompleteAsync(
            new ChatRequest("sys", [new ChatMessage(ChatRole.User, "你好")], MaxTokens: 120),
            CancellationToken.None);

        Assert.Equal(4096, BodyOf(handler).GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_UsesDefaultMaxTokensWhenNotConfigured()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"ok"}}],"usage":{"total_tokens":3}}""")),
        };
        var provider = MakeProvider(handler);

        await provider.CompleteAsync(
            new ChatRequest("sys", [new ChatMessage(ChatRole.User, "你好")], MaxTokens: 120),
            CancellationToken.None);

        Assert.Equal(120, BodyOf(handler).GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_VisionMessage_UsesContentArray()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"这是一张图"}}],"usage":{"total_tokens":5}}""")),
        };
        var provider = MakeProvider(handler);
        var vision = new ChatMessage(ChatRole.User, "描述这张图",
            ImageDataUrl: "data:image/png;base64,AAAA");

        await provider.CompleteAsync(new ChatRequest("sys", [vision]), CancellationToken.None);

        var content = BodyOf(handler).GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AAAA",
            content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task CompleteAsync_NoApiKey_OmitsAuthHeader()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"ok"}}],"usage":{"total_tokens":1}}""")),
        };
        var provider = MakeProvider(handler, apiKey: null);

        await provider.CompleteAsync(new ChatRequest("s", [new ChatMessage(ChatRole.User, "x")]), CancellationToken.None);
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    // ---- 失败路径 ----

    [Fact]
    public async Task CompleteAsync_Unauthorized_ThrowsAuthError()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
        };
        var provider = MakeProvider(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(new ChatRequest("s", [new ChatMessage(ChatRole.User, "x")]), CancellationToken.None));
        Assert.Equal("auth", ex.Code);
    }

    [Fact]
    public async Task CompleteAsync_Timeout_ThrowsTimeoutError()
    {
        var handler = new MockHandler
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return JsonResponse("""{"choices":[{"message":{"content":"慢"}}]}""");
            },
        };
        var provider = new OpenAiCompatibleModelProvider(
            Config, new InMemoryCredentialStore(), handler, timeout: TimeSpan.FromMilliseconds(80));

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(new ChatRequest("s", [new ChatMessage(ChatRole.User, "x")]), CancellationToken.None));
        Assert.Equal("timeout", ex.Code);
    }

    [Fact]
    public async Task CompleteAsync_NetworkFailure_ThrowsNetworkError()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => throw new HttpRequestException("连接被拒绝"),
        };
        var provider = MakeProvider(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(new ChatRequest("s", [new ChatMessage(ChatRole.User, "x")]), CancellationToken.None));
        Assert.Equal("network", ex.Code);
    }

    [Fact]
    public async Task CompleteAsync_InvalidJson_ThrowsResponseError()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("这不是 JSON")),
        };
        var provider = MakeProvider(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(new ChatRequest("s", [new ChatMessage(ChatRole.User, "x")]), CancellationToken.None));
        Assert.Equal("invalid-response", ex.Code);
    }

    [Fact]
    public async Task CompleteAsync_EmptyChoices_ThrowsResponseError()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""{"choices":[],"usage":{"total_tokens":0}}""")),
        };
        var provider = MakeProvider(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            provider.CompleteAsync(new ChatRequest("s", [new ChatMessage(ChatRole.User, "x")]), CancellationToken.None));
        Assert.Equal("invalid-response", ex.Code);
    }

    // ---- 能力发现（设置页"测试连接"）----

    [Fact]
    public async Task ListModelsAsync_ParsesModelCatalog()
    {
        var handler = new MockHandler
        {
            Handler = (_, _) => Task.FromResult(JsonResponse("""
                {"data":[{"id":"gpt-4o","object":"model"},
                         {"id":"qwen2.5-vl:7b","object":"model"},
                         {"id":"gpt-3.5-turbo","object":"model"}]}
                """)),
        };
        var provider = MakeProvider(handler);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(3, models.Count);
        Assert.Equal("https://api.openai.com/v1/models", handler.Requests[0].RequestUri!.ToString());
        Assert.True(models[0].Capabilities.HasFlag(ModelCapabilities.Vision));   // 4o → vision
        Assert.True(models[1].Capabilities.HasFlag(ModelCapabilities.Vision));   // vl → vision
        Assert.True(models[2].Capabilities.HasFlag(ModelCapabilities.Chat));     // 纯文本模型
        Assert.False(models[2].Capabilities.HasFlag(ModelCapabilities.Vision));
    }

    // ---- 连接配置模型 ----

    [Fact]
    public void ProvidersFile_Normalize_KeepsValidAndDefaults()
    {
        var file = new ProvidersFileModel
        {
            Models =
            [
                Config,
                new ProviderConfig("bad", "", "", "", "", ModelCapabilities.None, IsDefault: false),
                new ProviderConfig("ollama-local", "本地", "http://localhost:11434/v1", "", "qwen2.5-vl:7b",
                    ModelCapabilities.Chat | ModelCapabilities.Vision, IsDefault: false),
            ],
        };
        var n = ProvidersFileModel.Normalize(file);
        Assert.Equal(2, n.Models.Count);                       // 无效条目丢弃
        Assert.Equal("openai-default", n.Models[0].Id);
        Assert.Equal("http://localhost:11434/v1", n.Models[1].BaseUrl);
    }

    [Fact]
    public void ProvidersFile_JsonRoundtrip_CamelCase()
    {
        var file = new ProvidersFileModel { Models = [Config] };
        var json = ProvidersFileModel.Serialize(file);
        var back = ProvidersFileModel.Deserialize(json);
        Assert.Single(back.Models);
        Assert.Equal("openai-default", back.Models[0].Id);
        Assert.Equal("gpt-4o", back.Models[0].ModelName);
        Assert.True(back.Models[0].IsDefault);
    }

    [Fact]
    public void CredentialStore_InMemory_Roundtrips()
    {
        var store = new InMemoryCredentialStore();
        Assert.Null(store.Get("missing"));
        store.Set("k1", "v1");
        Assert.Equal("v1", store.Get("k1"));
        store.Set("k1", "v2");
        Assert.Equal("v2", store.Get("k1"));
    }
}

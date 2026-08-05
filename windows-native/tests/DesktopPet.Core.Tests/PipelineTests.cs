using DesktopPet.Core.Ai;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 5d：AI 设置扩展 + 对话管道（架构文档 §4）。
/// 管道：校验 → 人格拼接 → 屏幕上下文（可选）→ Scheduler P0 调用 → token 记账回调。
/// </summary>
public class PipelineTests
{
    // ---- AppSettings AI 扩展 ----

    [Fact]
    public void AiSettings_Defaults_PureDesktopPet()
    {
        var d = AppSettings.Defaults(AppLang.En).Ai;
        Assert.False(d.Enabled);              // AI 总开关默认关 = 纯桌宠
        Assert.False(d.ScreenAnalysis);       // 分析开关默认关（隐私）
        Assert.Equal("silent", d.OutputMode); // 输出模式默认静默
        Assert.False(d.ScreenContextEnabled); // 屏幕上下文默认关（隐私）
        Assert.Equal("", d.ProviderId);
    }

    [Fact]
    public void AiSettings_Normalize_OutputModeFallsBackToSilent()
    {
        var raw = AppSettings.Defaults(AppLang.En) with { Ai = new AiSettings(
            Enabled: true, ScreenAnalysis: true, OutputMode: "banana",
            ScreenContextEnabled: true, ProviderId: "p1",
            MemoryEnabled: true, ActiveInteraction: true, InteractionFrequency: "medium",
            ScreenAwareness: true, IntimacyEnabled: true, DailySummary: true,
            SummaryImage: false, TtsEnabled: false, AllReply: false) };
        var n = AppSettings.Normalize(raw);
        Assert.Equal("silent", n.Ai.OutputMode);
        Assert.True(n.Ai.Enabled);
        Assert.True(n.Ai.ScreenAnalysis);
        Assert.True(n.Ai.ScreenContextEnabled);
        Assert.Equal("p1", n.Ai.ProviderId);
    }

    [Fact]
    public void AiSettings_Normalize_KeepsValidModes()
    {
        foreach (var mode in new[] { "danmaku", "chat", "silent" })
        {
            var raw = AppSettings.Defaults(AppLang.En) with { Ai = new AiSettings(
                Enabled: true, ScreenAnalysis: false, OutputMode: mode,
                ScreenContextEnabled: false, ProviderId: "",
                MemoryEnabled: true, ActiveInteraction: true, InteractionFrequency: "medium",
                ScreenAwareness: true, IntimacyEnabled: true, DailySummary: true,
                SummaryImage: false, TtsEnabled: false, AllReply: false) };
            Assert.Equal(mode, AppSettings.Normalize(raw).Ai.OutputMode);
        }
    }

    [Fact]
    public void AiSettings_Normalize_NullAiGetsDefaults()
    {
        // 旧版 app-settings.json 无 ai 字段 → 反序列化为 null → 归一化给默认（不崩溃）
        var raw = AppSettings.Defaults(AppLang.En) with { Ai = null! };
        var n = AppSettings.Normalize(raw);
        Assert.False(n.Ai.Enabled);
        Assert.Equal("silent", n.Ai.OutputMode);
    }

    // ---- ScreenContextFormatter ----

    [Fact]
    public void ScreenContextFormatter_FormatsRecentEvents()
    {
        var t = new DateTime(2026, 8, 5, 14, 2, 0);
        var events = new[]
        {
            new ScreenEvent(t, ScreenEventKind.Coding, "Visual Studio 窗口"),
            new ScreenEvent(t.AddMinutes(5), ScreenEventKind.Browsing, "浏览器"),
        };
        var text = ScreenContextFormatter.Format(events, maxEvents: 4);
        Assert.Contains("[Coding]", text);
        Assert.Contains("Visual Studio 窗口", text);
        Assert.Contains("[Browsing]", text);
        Assert.Contains("14:02", text);
    }

    [Fact]
    public void ScreenContextFormatter_EmptyEvents_ReturnsEmpty()
    {
        Assert.Equal("", ScreenContextFormatter.Format([], maxEvents: 4));
    }

    [Fact]
    public void ScreenContextFormatter_RespectsMaxEvents()
    {
        var t = new DateTime(2026, 8, 5, 10, 0, 0);
        var events = Enumerable.Range(0, 6)
            .Select(i => new ScreenEvent(t.AddMinutes(i), ScreenEventKind.Idle, $"e{i}"))
            .ToArray();
        var text = ScreenContextFormatter.Format(events, maxEvents: 2);
        Assert.Contains("e4", text);
        Assert.Contains("e5", text);
        Assert.DoesNotContain("e0", text); // 只保留最近 2 条
    }

    // ---- ChatPipeline ----

    private sealed class FakeProvider : IModelProvider
    {
        public string Id => "fake";
        public ModelCapabilities Capabilities => ModelCapabilities.Chat;
        public ChatRequest? LastRequest { get; private set; }
        public Exception? Throw { get; set; }
        public int TokensUsed { get; set; } = 10;

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new ChatResult($"回复:{request.Messages[^1].Content}", TokensUsed));
        }

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }

    private static Persona Persona() => BuiltinPersonas.GetById("warm-guy")!;

    private static (ChatPipeline Pipeline, FakeProvider Provider) MakePipeline(
        bool includeScreen = false, ScreenEventLog? log = null, int maxInput = 500)
    {
        var provider = new FakeProvider();
        var scheduler = new ModelRequestScheduler(provider, concurrency: 1);
        var pipeline = new ChatPipeline(
            scheduler,
            Persona,
            log ?? new ScreenEventLog(),
            new ChatPipelineOptions(MaxInputLength: maxInput, ScreenContextMaxEvents: 4));
        return (pipeline, provider);
    }

    [Fact]
    public async Task Pipeline_EmptyInput_ReturnsError()
    {
        var (pipeline, _) = MakePipeline();
        var result = await pipeline.RunAsync("   ", [], includeScreenContext: false);
        Assert.False(result.Ok);
        Assert.Equal("empty", result.Error);
    }

    [Fact]
    public async Task Pipeline_TooLongInput_ReturnsError()
    {
        var (pipeline, _) = MakePipeline(maxInput: 10);
        var result = await pipeline.RunAsync("这个输入超过十个字符了", [], includeScreenContext: false);
        Assert.False(result.Ok);
        Assert.Equal("too-long", result.Error);
    }

    [Fact]
    public async Task Pipeline_AppendsInputAndBuildsSystemPrompt()
    {
        var (pipeline, provider) = MakePipeline();
        var history = new[] { new ChatMessage(ChatRole.User, "你好"), new ChatMessage(ChatRole.Assistant, "嗨~") };

        var result = await pipeline.RunAsync("今天好累", history, includeScreenContext: false);

        Assert.True(result.Ok);
        Assert.Equal("回复:今天好累", result.Text);
        var req = provider.LastRequest!;
        Assert.Equal(PersonaEngine.BuildSystemPrompt(Persona()), req.SystemPrompt);
        Assert.Equal(PersonaEngine.Temperature, req.Temperature);
        Assert.Equal(PersonaEngine.MaxTokens, req.MaxTokens);
        // 历史 + 当前输入，顺序正确
        Assert.Equal(3, req.Messages.Count);
        Assert.Equal(ChatRole.User, req.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, req.Messages[1].Role);
        Assert.Equal("今天好累", req.Messages[^1].Content);
    }

    [Fact]
    public async Task Pipeline_ScreenContextIncludedWhenEnabled()
    {
        var log = new ScreenEventLog();
        log.Add(new ScreenEvent(new DateTime(2026, 8, 5, 9, 0, 0), ScreenEventKind.Coding, "IDE"));
        var (pipeline, provider) = MakePipeline(log: log);

        await pipeline.RunAsync("我在干嘛", [], includeScreenContext: true);

        var req = provider.LastRequest!;
        Assert.Contains("IDE", req.Messages[^2].Content); // 上下文消息在用户输入前
        Assert.Equal("我在干嘛", req.Messages[^1].Content);
    }

    [Fact]
    public async Task Pipeline_MemoryInjectedBeforeScreenContext()
    {
        // Phase 6：记忆注入（管道第③步，架构文档 §4）——位于屏幕上下文之前、用户输入之前
        var log = new ScreenEventLog();
        log.Add(new ScreenEvent(new DateTime(2026, 8, 5, 9, 0, 0), ScreenEventKind.Coding, "IDE"));
        var (pipeline, provider) = MakePipeline(log: log);

        await pipeline.RunAsync("在忙吗", [new ChatMessage(ChatRole.Assistant, "嗨~")], includeScreenContext: true,
            memoryInjector: () => "[关于用户的记忆]\n称呼：小美");

        var req = provider.LastRequest!;
        Assert.Equal(4, req.Messages.Count);
        Assert.Equal(ChatRole.Assistant, req.Messages[0].Role);   // 历史对话在前
        Assert.Equal(ChatRole.System, req.Messages[1].Role);      // 记忆随后
        Assert.Contains("小美", req.Messages[1].Content);
        Assert.Contains("IDE", req.Messages[2].Content);          // 屏幕上下文再后
        Assert.Equal("在忙吗", req.Messages[^1].Content);          // 用户输入最后
    }

    [Fact]
    public async Task Pipeline_MemoryEmpty_NotInjected()
    {
        var (pipeline, provider) = MakePipeline();

        await pipeline.RunAsync("在忙吗", [], includeScreenContext: false,
            memoryInjector: () => "");

        var req = provider.LastRequest!;
        Assert.Single(req.Messages); // 只有用户输入，无记忆 System 消息
        Assert.Equal(ChatRole.User, req.Messages[0].Role);
    }

    [Fact]
    public async Task Pipeline_SystemPromptSuffix_AppendedForIntimacy()
    {
        // Phase 6c：亲密度档位指令追加到 SystemPrompt（开关关 = 空串不追加）
        var (pipeline, provider) = MakePipeline();

        await pipeline.RunAsync("在忙吗", [], includeScreenContext: false,
            systemPromptSuffix: () => "你们已经非常亲密：使用最亲昵的称呼（如宝贝）。");

        var req = provider.LastRequest!;
        Assert.StartsWith(PersonaEngine.BuildSystemPrompt(Persona()), req.SystemPrompt);
        Assert.EndsWith("你们已经非常亲密：使用最亲昵的称呼（如宝贝）。", req.SystemPrompt);
    }

    [Fact]
    public async Task Pipeline_SystemPromptSuffix_Empty_Unchanged()
    {
        var (pipeline, provider) = MakePipeline();

        await pipeline.RunAsync("在忙吗", [], includeScreenContext: false,
            systemPromptSuffix: () => "");

        var req = provider.LastRequest!;
        Assert.Equal(PersonaEngine.BuildSystemPrompt(Persona()), req.SystemPrompt);
    }

    [Fact]
    public async Task Pipeline_ScreenContextSkippedWhenDisabled()
    {
        var log = new ScreenEventLog();
        log.Add(new ScreenEvent(new DateTime(2026, 8, 5, 9, 0, 0), ScreenEventKind.Coding, "IDE"));
        var (pipeline, provider) = MakePipeline(log: log);

        await pipeline.RunAsync("我在干嘛", [], includeScreenContext: false);

        var req = provider.LastRequest!;
        Assert.Single(req.Messages);
        Assert.DoesNotContain("IDE", req.Messages[0].Content);
    }

    [Fact]
    public async Task Pipeline_ReportsTokensUsedViaCallback()
    {
        var (pipeline, provider) = MakePipeline();
        provider.TokensUsed = 37;
        int reported = -1;
        var result = await pipeline.RunAsync("hi", [], includeScreenContext: false, onTokensUsed: n => reported = n);
        Assert.True(result.Ok);
        Assert.Equal(37, result.TokensUsed);
        Assert.Equal(37, reported);
    }

    [Fact]
    public async Task Pipeline_ProviderFailure_Propagates()
    {
        var (pipeline, provider) = MakePipeline();
        provider.Throw = new TimeoutException("模型超时");
        await Assert.ThrowsAsync<TimeoutException>(
            () => pipeline.RunAsync("hi", [], includeScreenContext: false));
    }

    [Fact]
    public async Task Pipeline_PersonaSwitch_TakesEffectImmediately()
    {
        var provider = new FakeProvider();
        var scheduler = new ModelRequestScheduler(provider, concurrency: 1);
        var current = BuiltinPersonas.GetById("warm-guy")!;
        var pipeline = new ChatPipeline(scheduler, () => current, new ScreenEventLog());

        await pipeline.RunAsync("嗨", [], includeScreenContext: false);
        var first = provider.LastRequest!.SystemPrompt;

        current = BuiltinPersonas.GetById("puppy")!; // 切换立即生效
        await pipeline.RunAsync("嗨", [], includeScreenContext: false);
        var second = provider.LastRequest!.SystemPrompt;

        Assert.NotEqual(first, second);
        Assert.Contains("小奶狗", second);
    }
}

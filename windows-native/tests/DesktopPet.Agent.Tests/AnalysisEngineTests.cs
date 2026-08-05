using DesktopPet.Agent.Analysis;
using DesktopPet.Agent.Capture;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Agent.Tests;

/// <summary>
/// Phase 5f：AnalysisEngine 离线测试（迁移计划 §8：录制帧序列驱动，不依赖真实屏幕）。
/// 帧哈希变化检测 → 节流 → 视觉分析 → 事件；分析关闭 = 不截屏。
/// </summary>
public class AnalysisEngineTests
{
    private sealed class FakeModel : IModelProvider
    {
        public string Id => "fake";
        public ModelCapabilities Capabilities => ModelCapabilities.Chat | ModelCapabilities.Vision;
        public int CallCount { get; private set; }
        public Exception? Throw { get; set; }
        public string Response { get; set; } = "你好像在写代码";

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            CallCount++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new ChatResult(Response, 9));
        }

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }

    private static CapturedFrame Frame(byte v, int w = 32, int h = 32)
        => new(w, h, Enumerable.Repeat(v, w * h).ToArray());

    private static CapturedFrame GradientFrame(int w = 32, int h = 32)
    {
        var buf = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                buf[y * w + x] = (byte)(x * 255 / (w - 1));
        return new CapturedFrame(w, h, buf);
    }

    private static AnalysisEngine MakeEngine(
        IScreenCaptureSource capture, FakeModel? model = null, AgentConfig? config = null)
        => new(capture, model, () => config ?? new AgentConfig(
            ScreenAnalysis: true, AnalysisPersonaPrompt: null,
            ProviderBaseUrl: null, ProviderModel: null, ProviderApiKeyRef: null,
            ProviderReasoningEffort: null,
            MinAnalysisIntervalSeconds: 5));

    private static readonly DateTime T0 = new(2026, 8, 5, 10, 0, 0);

    [Fact]
    public async Task Tick_AnalysisDisabled_DoesNotCapture()
    {
        var source = new OfflineFrameSource([Frame(100)]);
        var engine = MakeEngine(source, config: new AgentConfig(
            ScreenAnalysis: false, AnalysisPersonaPrompt: null,
            ProviderBaseUrl: null, ProviderModel: null, ProviderApiKeyRef: null,
            ProviderReasoningEffort: null,
            MinAnalysisIntervalSeconds: 5));

        var result = await engine.TickAsync(T0, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, source.CaptureCount); // 静默：不截屏
    }

    [Fact]
    public async Task Tick_StaticScreen_NoEvent()
    {
        var source = new OfflineFrameSource([Frame(100), Frame(100)]);
        var engine = MakeEngine(source);

        Assert.Null(await engine.TickAsync(T0, CancellationToken.None));
        Assert.Null(await engine.TickAsync(T0.AddSeconds(1), CancellationToken.None));
        Assert.Equal(2, source.CaptureCount);
    }

    [Fact]
    public async Task Tick_ScreenChange_WithoutModel_EmitsUnknownEvent()
    {
        var source = new OfflineFrameSource([Frame(100), GradientFrame()]);
        var engine = MakeEngine(source);

        var e1 = await engine.TickAsync(T0, CancellationToken.None);
        var e2 = await engine.TickAsync(T0.AddSeconds(1), CancellationToken.None);

        Assert.Null(e1); // 首帧建立基准
        Assert.NotNull(e2);
        Assert.Equal(ScreenEventKind.Unknown, e2!.Kind);
        Assert.Equal("", e2.Summary); // 无模型：降级为纯变化事件
    }

    [Fact]
    public async Task Tick_ScreenChange_WithModel_EmitsClassifiedEvent()
    {
        var model = new FakeModel { Response = "又在写代码，辛苦了" };
        var source = new OfflineFrameSource([Frame(100), GradientFrame()]);
        var engine = MakeEngine(source, model);

        Assert.Null(await engine.TickAsync(T0, CancellationToken.None));
        var evt = await engine.TickAsync(T0.AddSeconds(1), CancellationToken.None);

        Assert.NotNull(evt);
        Assert.Equal(ScreenEventKind.Coding, evt!.Kind);
        Assert.Equal("又在写代码，辛苦了", evt.Summary);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task Tick_ModelFailure_FallsBackToUnknownEvent()
    {
        var model = new FakeModel { Throw = new TimeoutException("模型超时") };
        var source = new OfflineFrameSource([Frame(100), GradientFrame()]);
        var engine = MakeEngine(source, model);

        Assert.Null(await engine.TickAsync(T0, CancellationToken.None));
        var evt = await engine.TickAsync(T0.AddSeconds(1), CancellationToken.None);

        Assert.NotNull(evt);
        Assert.Equal(ScreenEventKind.Unknown, evt!.Kind); // 显式降级，不崩溃
    }

    [Fact]
    public async Task Tick_Throttle_LimitsAnalysisFrequency()
    {
        var model = new FakeModel { Response = "变化" };
        var frames = new List<CapturedFrame> { Frame(100), GradientFrame() };
        var source = new OfflineFrameSource(frames, loop: true);
        var engine = MakeEngine(source, model);

        Assert.Null(await engine.TickAsync(T0, CancellationToken.None));        // 基准
        var e1 = await engine.TickAsync(T0.AddSeconds(1), CancellationToken.None); // 变化 → 分析
        Assert.NotNull(e1);
        var e2 = await engine.TickAsync(T0.AddSeconds(3), CancellationToken.None); // 节流内再次变化 → 跳过
        Assert.Null(e2);
        var e3 = await engine.TickAsync(T0.AddSeconds(6), CancellationToken.None); // 满 5s → 放行
        Assert.NotNull(e3);
        Assert.Equal(2, model.CallCount);
    }

    [Fact]
    public async Task RunLoop_EmitsEventsForRecordedSequence()
    {
        // 录制序列：基准 → 变化 → 静止 → 变化；RunAsync 循环驱动，事件按序发出
        var model = new FakeModel();
        var frames = new List<CapturedFrame>
        {
            Frame(100), GradientFrame(), GradientFrame(),
            Frame(0, 32, 32), // 又一个大变化
        };
        var source = new OfflineFrameSource(frames);
        var engine = new AnalysisEngine(source, model,
            () => new AgentConfig(true, null, null, null, null, null, MinAnalysisIntervalSeconds: 0),
            captureInterval: TimeSpan.FromMilliseconds(5));

        var events = new List<ScreenEvent>();
        engine.EventRaised += events.Add;
        using var cts = new CancellationTokenSource(300);
        await engine.RunAsync(cts.Token);

        Assert.Equal(2, events.Count); // 两次真实变化
        Assert.All(events, e => Assert.Equal(ScreenEventKind.Coding, e.Kind));
    }

    // ---- 分类关键词 ----

    [Theory]
    [InlineData("又在写代码", ScreenEventKind.Coding)]
    [InlineData("打开浏览器看网页", ScreenEventKind.Browsing)]
    [InlineData("在看视频", ScreenEventKind.Video)]
    [InlineData("在打游戏", ScreenEventKind.Gaming)]
    [InlineData("离开了一会儿", ScreenEventKind.Idle)]
    [InlineData("随便说说", ScreenEventKind.AppSwitch)]
    public void Classify_KeywordMapping(string text, ScreenEventKind expected)
        => Assert.Equal(expected, AnalysisEngine.Classify(text));
}

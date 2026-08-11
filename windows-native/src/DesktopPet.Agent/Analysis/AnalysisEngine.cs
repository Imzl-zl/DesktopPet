using DesktopPet.Agent.Capture;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Agent.Analysis;

/// <summary>
/// 分析引擎（迁移计划 §6.4）：每秒截屏 → 帧哈希变化检测 → 变化时（限频 ≥5s）
/// 调视觉模型分类+评论 → ScreenEvent 事件。分析关闭 = 不截屏。
/// 模型失败/未配置 → 降级为纯变化事件（Kind=Unknown，UI 用默认台词，不崩溃）。
/// TickAsync 单步可测；RunAsync 为循环宿主。
/// </summary>
public sealed class AnalysisEngine
{
    private readonly IScreenCaptureSource _capture;
    private readonly IModelProvider? _model;
    private readonly Func<AgentConfig> _config;
    private readonly TimeSpan? _captureIntervalOverride;
    private readonly TimeSpan _analysisTimeout;
    private readonly ChangeDetector _detector = new();
    private ulong? _previousHash;
    private AnalysisThrottle _throttle;

    public event Action<ScreenEvent>? EventRaised;
    public event Action<Exception>? CaptureFaulted;

    public AnalysisEngine(
        IScreenCaptureSource capture,
        IModelProvider? model,
        Func<AgentConfig> config,
        TimeSpan? captureInterval = null,
        TimeSpan? analysisTimeout = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _model = model;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _captureIntervalOverride = captureInterval;
        if (_captureIntervalOverride is { } configuredCaptureInterval
            && configuredCaptureInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(captureInterval));
        _analysisTimeout = analysisTimeout ?? TimeSpan.FromSeconds(30);
        if (_analysisTimeout <= TimeSpan.Zero && _analysisTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(analysisTimeout));
        _throttle = new AnalysisThrottle(TimeSpan.FromSeconds(config().MinAnalysisIntervalSeconds));
    }

    /// <summary>单步：捕获一帧并判断是否需要分析。返回事件或 null。</summary>
    public async Task<ScreenEvent?> TickAsync(DateTime now, CancellationToken ct)
    {
        var cfg = _config();
        if (!cfg.ScreenAnalysis) return null; // 静默：不截屏不分析

        var frame = await _capture.CaptureAsync(ct).ConfigureAwait(false);
        if (frame is null) return null;

        var hash = FrameHasher.HashGrayscale(frame.Gray, frame.Width, frame.Height);
        if (_previousHash is null)
        {
            _previousHash = hash; // 首帧只建立基准，不触发分析
            return null;
        }
        var changed = _detector.HasChanged(_previousHash.Value, hash);
        _previousHash = hash;

        if (!changed) return null;
        SyncThrottle(cfg);
        if (!_throttle.TryTake(now)) return null;

        return await AnalyzeAsync(frame, hash, cfg, ct).ConfigureAwait(false);
    }

    /// <summary>循环宿主：Tick + 捕获间隔；ct 取消退出。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var evt = await TickAsync(DateTime.Now, ct).ConfigureAwait(false);
                if (evt is not null) EventRaised?.Invoke(evt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (CaptureSourceUnavailableException ex)
            {
                CaptureFaulted?.Invoke(ex);
            }
            catch (Exception)
            {
                // 单帧分析失败不拖垮循环（下一拍重试）；错误已由 AnalyzeAsync 降级处理
            }
            try
            {
                await Task.Delay(GetCaptureInterval(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>评论文本 → 屏幕事件分类（关键词粗分类，可单测）。</summary>
    public static ScreenEventKind Classify(string text)
    {
        if (text.Contains("代码") || text.Contains("写码") || text.Contains("编程")
            || text.Contains("开发") || text.Contains("bug") || text.Contains("Bug"))
            return ScreenEventKind.Coding;
        if (text.Contains("视频") || text.Contains("电影") || text.Contains("追剧")
            || text.Contains("直播") || text.Contains("看剧"))
            return ScreenEventKind.Video;
        if (text.Contains("听歌") || text.Contains("音乐") || text.Contains("网易云")
            || text.Contains("酷狗") || text.Contains("Spotify") || text.Contains("music"))
            return ScreenEventKind.Music;
        if (text.Contains("游戏") || text.Contains("打游戏") || text.Contains("对战")
            || text.Contains("副本"))
            return ScreenEventKind.Gaming;
        if (text.Contains("浏览") || text.Contains("网页") || text.Contains("浏览器")
            || text.Contains("购物") || text.Contains("刷"))
            return ScreenEventKind.Browsing;
        if (text.Contains("离开") || text.Contains("空闲") || text.Contains("休息")
            || text.Contains("不在"))
            return ScreenEventKind.Idle;
        return ScreenEventKind.AppSwitch;
    }

    private async Task<ScreenEvent> AnalyzeAsync(
        CapturedFrame frame, ulong hash, AgentConfig cfg, CancellationToken ct)
    {
        if (_model is null)
        {
            return new ScreenEvent(DateTime.Now, ScreenEventKind.Unknown, "", hash);
        }

        try
        {
            var prompt = string.IsNullOrWhiteSpace(cfg.AnalysisPersonaPrompt)
                ? PersonaEngine.BasePrompt
                : cfg.AnalysisPersonaPrompt;
            var request = new ChatRequest(
                SystemPrompt: prompt,
                Messages:
                [
                    new ChatMessage(ChatRole.User,
                        "这是当前屏幕的截图。用一句话简短评论用户在做什么（30 字内，口语化，按你的人格）",
                        ImageDataUrl: frame.ToDataUrl()),
                ],
                Temperature: PersonaEngine.Temperature,
                MaxTokens: PersonaEngine.MaxTokens);

            using var deadline = CreateAnalysisDeadline(ct);
            var requestCt = deadline?.Token ?? ct;
            var operation = _model.CompleteAsync(request, requestCt);
            try
            {
                var result = await operation.WaitAsync(requestCt).ConfigureAwait(false);
                return new ScreenEvent(DateTime.Now, Classify(result.Text), result.Text, hash);
            }
            finally
            {
                if (requestCt.IsCancellationRequested)
                    ObserveDetachedTask(operation);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 分析 deadline 或模型失败显式降级为纯变化事件（限频已挡频次；UI 有默认台词）
            return new ScreenEvent(DateTime.Now, ScreenEventKind.Unknown, "", hash);
        }
    }

    private static void ObserveDetachedTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private CancellationTokenSource? CreateAnalysisDeadline(CancellationToken ct)
    {
        if (_analysisTimeout == Timeout.InfiniteTimeSpan) return null;
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_analysisTimeout);
        return deadline;
    }

    private TimeSpan GetCaptureInterval()
    {
        if (_captureIntervalOverride is { } configured) return configured;
        var seconds = Math.Clamp(_config().CaptureIntervalSeconds, 1, 30);
        return TimeSpan.FromSeconds(seconds);
    }

    private void SyncThrottle(AgentConfig cfg)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(0, cfg.MinAnalysisIntervalSeconds));
        if (interval != _throttle.Interval)
        {
            _throttle = new AnalysisThrottle(interval);
        }
    }
}

using System.Text.Json;
using System.Threading.Channels;
using DesktopPet.Agent.Analysis;
using DesktopPet.Agent.Capture;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Diagnostics;
using DesktopPet.Infra.PipeRpc;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Agent;

/// <summary>
/// Agent 服务编排（AgentHost 的纯逻辑核心，可测）：
/// 管道服务端（App=client）→ 收 Config（构建 provider/引擎）→ 分析事件推送回 App。
/// 断连/单帧失败均不拖垮服务；Shutdown 消息优雅退出。
/// </summary>
public sealed class AgentService : IAsyncDisposable
{
    private readonly PipeRpcServer _server;
    private readonly IScreenCaptureSource _capture;
    private readonly ICredentialStore _credentials;
    private readonly IAppLogger _logger;
    private readonly int _expectedClientProcessId;
    private readonly Func<int, bool> _clientAuthorizer;
    private readonly HttpClient _providerHttp = ProviderHttpClient.Create();
    private readonly TimeSpan? _captureIntervalOverride;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<QueuedScreenEvent> _eventQueue = Channel.CreateBounded<QueuedScreenEvent>(
        new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite=false 时显式计数；事件生产线程绝不等待。
        });
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private Task? _runTask;
    private bool _runStarted;
    private bool _disposeStarted;
    private long _engineGeneration;
    private long _droppedEventCount;

    private readonly object _configLock = new();
    private readonly SemaphoreSlim _engineLifecycle = new(1, 1);
    private AgentConfig _config = AgentConfig.Defaults;
    private AnalysisEngine? _engine;
    private CancellationTokenSource? _engineCts;
    private Task? _engineTask;

    public AgentService(
        string pipeName,
        IScreenCaptureSource capture,
        ICredentialStore credentials,
        int expectedClientProcessId,
        TimeSpan? captureInterval = null,
        Func<int, bool>? clientAuthorizer = null,
        TimeSpan? heartbeatTimeout = null,
        TimeProvider? timeProvider = null,
        IAppLogger? logger = null)
    {
        if (expectedClientProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(expectedClientProcessId));
        _server = new PipeRpcServer(pipeName);
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _logger = logger ?? NullAppLogger.Instance;
        _expectedClientProcessId = expectedClientProcessId;
        _clientAuthorizer = clientAuthorizer ?? (pid => pid == _expectedClientProcessId);
        _captureIntervalOverride = captureInterval;
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(15);
        if (_heartbeatTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(heartbeatTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>运行直到 Shutdown 或 ct 取消。阻塞调用。</summary>
    public Task RunAsync(CancellationToken ct)
    {
        lock (_disposeSync)
        {
            if (_disposeStarted) throw new ObjectDisposedException(nameof(AgentService));
            if (_runStarted) throw new InvalidOperationException("AgentService.RunAsync 只能启动一次");
            _runStarted = true;
            _runTask = RunCoreAsync(ct);
            return _runTask;
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        using var runLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var runCt = runLifetime.Token;
        try
        {
            while (!runCt.IsCancellationRequested)
            {
                await _server.WaitForConnectionAsync(runCt).ConfigureAwait(false);
                int clientProcessId;
                try
                {
                    clientProcessId = _server.GetConnectedClientProcessId();
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                {
                    _logger.Info("Agent", "Reject pipe client: cannot resolve PID: " + ex.Message);
                    await _server.DisconnectAsync().ConfigureAwait(false);
                    continue;
                }

                if (!_clientAuthorizer(clientProcessId))
                {
                    _logger.Info("Agent", $"Reject pipe client pid={clientProcessId}; expected={_expectedClientProcessId}");
                    await _server.DisconnectAsync().ConfigureAwait(false);
                    continue;
                }

                await _server.SendAsync(new RpcMessage(RpcType.Hello,
                    JsonSerializer.SerializeToElement(new { agent = "DesktopPet.Agent", version = 1 })), runCt).ConfigureAwait(false);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(runCt);
                var heartbeatLease = new HeartbeatLease(_heartbeatTimeout, _timeProvider);
                var receiveLoop = ReceiveLoopAsync(heartbeatLease, linked.Token);
                var heartbeatLoop = MonitorHeartbeatAsync(heartbeatLease, linked.Token);
                var eventSendLoop = SendEventsAsync(linked.Token);
                try
                {
                    var completed = await Task.WhenAny(receiveLoop, heartbeatLoop, eventSendLoop).ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                }
                finally
                {
                    linked.Cancel();
                    await ObserveSessionLoopEndAsync(receiveLoop).ConfigureAwait(false);
                    await ObserveSessionLoopEndAsync(heartbeatLoop).ConfigureAwait(false);
                    await ObserveSessionLoopEndAsync(eventSendLoop).ConfigureAwait(false);
                }
                return; // 已认证会话结束后 Agent 无独立工作，宿主退出
            }
        }
        catch (OperationCanceledException) when (runCt.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposeStarted)
        {
        }
        catch (IOException) when (_disposeStarted)
        {
        }
        finally
        {
            await StopEngineAndCaptureAsync().ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(HeartbeatLease heartbeatLease, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            RpcMessage msg;
            try
            {
                msg = await _server.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                _shutdown.Cancel(); // App 客户端已断开，宿主没有独立工作可继续执行
                return;
            }

            switch (msg.Type)
            {
                case RpcType.Config:
                    await ApplyConfigAsync(msg.Payload, ct).ConfigureAwait(false);
                    break;
                case RpcType.Ping:
                    heartbeatLease.Renew();
                    await SafeSendAsync(new RpcMessage(RpcType.Pong, null), ct).ConfigureAwait(false);
                    break;
                case RpcType.Shutdown:
                    _shutdown.Cancel();
                    return;
            }
        }
    }

    private async Task MonitorHeartbeatAsync(HeartbeatLease heartbeatLease, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var remaining = heartbeatLease.Remaining;
            if (remaining == TimeSpan.Zero)
            {
                await HandleHeartbeatExpiredAsync().ConfigureAwait(false);
                return;
            }
            await Task.Delay(remaining, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleHeartbeatExpiredAsync()
    {
        _logger.Info("Agent", "Heartbeat lease expired; stopping capture and Agent service");
        await StopEngineAndCaptureAsync(() => _shutdown.Cancel()).ConfigureAwait(false);
    }

    private async Task StopEngineAndCaptureAsync(Action? beforeRelease = null)
    {
        Interlocked.Increment(ref _engineGeneration);
        await _engineLifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopEngineCoreAsync().ConfigureAwait(false);
            if (_capture is IActivatableScreenCaptureSource activatableCapture)
            {
                activatableCapture.SetEnabled(false);
            }
            beforeRelease?.Invoke();
        }
        finally
        {
            _engineLifecycle.Release();
        }
    }

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private async Task ApplyConfigAsync(JsonElement? payload, CancellationToken ct)
    {
        AgentConfig cfg;
        try
        {
            cfg = payload is null
                ? AgentConfig.Defaults
                : JsonSerializer.Deserialize<AgentConfig>(payload.Value.GetRawText(), ConfigJsonOptions)
                  ?? AgentConfig.Defaults;
        }
        catch (JsonException)
        {
            cfg = AgentConfig.Defaults;
        }

        await _engineLifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_configLock)
            {
                if (cfg.Revision < _config.Revision)
                {
                    _logger.Info("Agent", $"Ignore stale config revision={cfg.Revision}; current={_config.Revision}");
                    return;
                }
                _config = cfg;
                generation = Interlocked.Increment(ref _engineGeneration);
            }
            _logger.Info("Agent", $"ApplyConfig: revision={cfg.Revision} screenAnalysis={cfg.ScreenAnalysis} providerConfigured={!string.IsNullOrWhiteSpace(cfg.ProviderBaseUrl)} analysisInterval={cfg.MinAnalysisIntervalSeconds} captureInterval={cfg.CaptureIntervalSeconds}");
            await StopEngineCoreAsync().ConfigureAwait(false);
            StartEngine(cfg, generation);
        }
        finally
        {
            _engineLifecycle.Release();
        }
    }

    private void StartEngine(AgentConfig config, long generation)
    {
        var captureInterval = _captureIntervalOverride
            ?? TimeSpan.FromSeconds(Math.Clamp(config.CaptureIntervalSeconds, 1, 30));
        if (_capture is ICaptureCadenceSource cadence)
            cadence.SetCaptureInterval(captureInterval);
        if (_capture is IActivatableScreenCaptureSource activatableCapture)
        {
            activatableCapture.SetEnabled(config.ScreenAnalysis);
        }

        IModelProvider? model = null;
        if (!string.IsNullOrEmpty(config.ProviderBaseUrl) && !string.IsNullOrEmpty(config.ProviderModel))
        {
            var pc = new ProviderConfig(
                Id: "agent-analysis",
                Name: "Agent 分析模型",
                BaseUrl: config.ProviderBaseUrl,
                ApiKeyRef: config.ProviderApiKeyRef ?? "",
                ModelName: config.ProviderModel,
                Capabilities: ModelCapabilities.Chat | ModelCapabilities.Vision,
                IsDefault: false,
                ReasoningEffort: config.ProviderReasoningEffort); // 推理模型必须关闭思考，否则 token 全被消耗
            model = new OpenAiCompatibleModelProvider(
                pc, _credentials, _providerHttp, requestTimeout: Timeout.InfiniteTimeSpan);
        }

        var engine = new AnalysisEngine(_capture, model, CurrentConfig, _captureIntervalOverride);
        engine.CaptureFaulted += ex => _logger.Info("Agent", $"capture fault: {FlattenForLog(ex)}");
        engine.EventRaised += e =>
        {
            if (!_eventQueue.Writer.TryWrite(new QueuedScreenEvent(generation, config.Revision, e)))
            {
                var dropped = Interlocked.Increment(ref _droppedEventCount);
                if ((dropped & 0x0F) == 1) _logger.Info("Agent", $"screen event queue full; dropped={dropped}");
            }
        };
        var engineCts = new CancellationTokenSource();
        _engine = engine;
        _engineCts = engineCts;
        _engineTask = engine.RunAsync(engineCts.Token);
    }

    private async Task StopEngineCoreAsync()
    {
        var cts = _engineCts;
        var task = _engineTask;
        _engine = null;
        _engineCts = null;
        _engineTask = null;

        cts?.Cancel();
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
    }

    private AgentConfig CurrentConfig()
    {
        lock (_configLock) return _config;
    }

    private async Task SendEventsAsync(CancellationToken ct)
    {
        await foreach (var queued in _eventQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (queued.Generation != Volatile.Read(ref _engineGeneration)
                || !CurrentConfig().ScreenAnalysis)
            {
                Interlocked.Increment(ref _droppedEventCount);
                continue;
            }
            var screenEvent = queued.Event;
            _logger.Info("Agent", $"push event: kind={screenEvent.Kind} revision={queued.ConfigRevision}");
            var payload = JsonSerializer.SerializeToElement(new ScreenEventPayload(
                screenEvent.Timestamp.ToString("o"),
                screenEvent.Kind.ToString(),
                screenEvent.Summary,
                screenEvent.FrameHash,
                queued.ConfigRevision,
                screenEvent.IsStale), ConfigJsonOptions);
            await _server.SendAsync(new RpcMessage(RpcType.ScreenEvent, payload), ct).ConfigureAwait(false);
        }
    }

    /// <summary>异常链展开（外层包装类型+message 不携带根因，脱敏规则会吃掉全限定类型名）。</summary>
    private static string FlattenForLog(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 4; e = e.InnerException)
        {
            parts.Add($"{e.GetType().Name}: {e.Message}");
        }
        return string.Join(" <-- ", parts);
    }

    private static async Task ObserveSessionLoopEndAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task SafeSendAsync(RpcMessage msg, CancellationToken ct)
    {
        try
        {
            await _server.SendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 对端断连：会话循环或 heartbeat 随后结束。
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposeStarted = true;
            _disposeTask = DisposeCoreAsync(_runTask);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task? runTask)
    {
        var failures = new List<Exception>();
        try { _shutdown.Cancel(); }
        catch (Exception ex) { failures.Add(ex); }
        Interlocked.Increment(ref _engineGeneration);
        _eventQueue.Writer.TryComplete();

        try { await _server.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { failures.Add(ex); }
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); }
        }
        else
        {
            try { await StopEngineAndCaptureAsync().ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); }
        }

        try
        {
            if (_capture is IDisposable disposableCapture) disposableCapture.Dispose();
        }
        catch (Exception ex) { failures.Add(ex); }
        try { _providerHttp.Dispose(); }
        catch (Exception ex) { failures.Add(ex); }
        try { _engineLifecycle.Dispose(); }
        catch (Exception ex) { failures.Add(ex); }
        try { _shutdown.Dispose(); }
        catch (Exception ex) { failures.Add(ex); }

        if (failures.Count > 0) throw new AggregateException("AgentService 释放失败", failures);
    }
}

/// <summary>屏幕事件管道载荷（IPC 契约：明确字段名，枚举转字符串）。</summary>
public sealed record ScreenEventPayload(
    string Timestamp,
    string Kind,
    string Summary,
    ulong FrameHash,
    long ConfigRevision,
    bool IsStale = false);

internal sealed record QueuedScreenEvent(long Generation, long ConfigRevision, ScreenEvent Event);

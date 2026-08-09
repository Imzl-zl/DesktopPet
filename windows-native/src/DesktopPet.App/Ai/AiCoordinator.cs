using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using DesktopPet.App.Windows;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Care;
using DesktopPet.Core.Interaction;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Memory;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;
using DesktopPet.Core.Summary;
using DesktopPet.Core.Tts;
using DesktopPet.Infra.Diagnostics;
using DesktopPet.Infra.Lifecycle;
using DesktopPet.Infra.PipeRpc;
using DesktopPet.Infra.Storage;
using DesktopPet.Infra.Providers;
using DesktopPet.Infra.Tts;

namespace DesktopPet.App.Ai;

/// <summary>
/// AI 编排器（Phase 5 接线核心 + Phase 6 陪伴增强）：
/// · AI 总开关：开 = 启 Agent 进程（PetAgent.exe）+ 管道连接 + 配置下发；关 = 停进程（无后台/无网络）
/// · 看门狗：Agent 崩溃自动重启（总开关仍开时）
/// · 分析事件 → ModeService 路由（弹幕/对话/静默）
/// · 用户对话在 App 进程直连 provider（架构 §4：不走管道），token 记账 → CareEngine
/// · 屏幕事件日志（App 侧维护，对话屏幕上下文 + 主动互动事件驱动用）
/// · Phase 6：记忆画像注入/更新（记忆开关）、亲密度记账与语气指令（亲密度开关）、
///   主动互动（定时 + 事件驱动，多宠物并行分派）、每日总结 + 总结图（开关组）、
///   对话朗读（语音开关 + 仅对话模式）
/// </summary>
public sealed class AiCoordinator : IDisposable, IAsyncDisposable, IModelConnectionTester
{
    private readonly FileJsonStore _store;
    private readonly ModeService _modeService;
    private readonly ChatWindow _chatWindow;
    private readonly Action<string, CareState, int> _recordTokens;
    private readonly string _agentHostPath;
    private readonly ScreenEventLog _eventLog = new();
    private readonly object _lock = new();
    // 对话串行闸：管道执行 + 会话记忆 + token/亲密度/画像记账（读-改-写）整体串行化，
    // 防并发对话互相覆盖（修复：原实现 LoadCare→FeedTokens→SaveCare 并发丢账）。
    private readonly SemaphoreSlim _chatSerial = new(1, 1);

    private AppSettings _settings;
    private PersonasFileModel _personas;
    private ProvidersFileModel _providers;

    private Process? _agent;
    private readonly OwnedResourceSlot<PipeRpcClient> _rpcSlot = new();
    private CancellationTokenSource? _lifeCts;
    private volatile bool _shuttingDown;
    private int _restartFailures;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private int _shutdownRequested;

    // App 侧 provider 运行时代际：请求 lease 固定其管道，配置切换先发布新代际，旧代际 drain 后释放。
    private readonly AsyncGenerationOwner<AiRuntimeGeneration> _runtime = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateLock = new();
    private long _runtimeRevision;
    private long _agentConfigRevision;
    private long _agentRevisionFloor;
    private long _pendingAgentRevision;
    private readonly SemaphoreSlim _agentConfigSend = new(1, 1);
    private readonly WindowsCredentialStore _credentials = new();
    private readonly HttpClient _providerHttp = ProviderHttpClient.Create();
    private readonly I18nService _i18n;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _coordinatorLifetime = new();

    // Phase 6：陪伴增强状态
    private UserProfile _profile;                       // 记忆画像（记忆开关关 = 空画像）
    private IntimacyEngine _intimacy;                   // 亲密度（0-100 双线）
    private readonly PetInteractionDispatcher _dispatcher = new();
    private InteractionEngine _interaction;             // 主动互动（频率/感知随设置更新）
    private DateOnly? _lastDiaryDate;                   // 日记最近生成日期
    private DateOnly? _pendingDiaryDate;
    // L1/L2 分层会话记忆（简洁版）：L1 最近消息按 token 预算保留（预算 = 模型上下文 50%，
    // 256k 配置下几百轮不触发）；L2 真超预算时最早轮次压缩进滚动摘要注入，不静默丢弃；
    // 摘要可合并进 L3 画像（RecordChatSuccess）。
    private readonly ConversationMemory _conversation = new();
    // 语音输出：三级 Provider 栈（windows-tts-design.md §3）——默认 SAPI 离线兜底；
    // 引擎选择/降级由 TtsProviderRegistry 处理；Speak 按设置选引擎，失败降级 sapi
    private readonly ITtsProvider _sapiTts = new SapiTtsProvider();
    private readonly IReadOnlyList<ITtsProvider> _baseTtsProviders;
    private IReadOnlyList<ITtsProvider> _ttsProviders;
    private ITtsProvider _tts;
    private readonly MediaPlayer _ttsPlayer = new();
    // 待清理的 TTS 临时文件（MediaEnded 只订阅一次，防闭包随朗读次数累积）
    private string? _pendingTtsTempPath;
    // 朗读生效状态（会话内）：初始 = 持久设置；对话窗按钮切换；设置页保存重置。
    // 修复：原实现 Speak 只看持久设置，对话窗朗读按钮点击无效。
    private bool _ttsSessionEnabled;
    private System.Threading.Timer? _tickTimer;         // 30s 周期：主动互动 + 每日总结

    public AiCoordinator(
        FileJsonStore store,
        ModeService modeService,
        ChatWindow chatWindow,
        Action<string, CareState, int> recordTokens,
        string agentHostPath,
        I18nService? i18n = null,
        IAppLogger? logger = null,
        IReadOnlyList<ITtsProvider>? ttsProviders = null)
    {
        _store = store;
        _modeService = modeService;
        _chatWindow = chatWindow;
        _recordTokens = recordTokens;
        _agentHostPath = agentHostPath;
        _i18n = i18n ?? new I18nService();
        _logger = logger ?? NullAppLogger.Instance;
        _baseTtsProviders = ttsProviders is { Count: > 0 }
            ? ttsProviders
            : new List<ITtsProvider> { _sapiTts };
        _settings = AppSettings.Normalize(store.LoadSettings() ?? AppSettings.Defaults(Core.I18n.I18nService.Detect()));
        _personas = PersonasFileModel.Normalize(store.LoadPersonasFile() ?? new PersonasFileModel());
        _providers = store.LoadProvidersFile() ?? new ProvidersFileModel();
        RebuildTtsProviders();
        _profile = MemoryProfileExtractor.Normalize(store.LoadMemoryProfile());
        _intimacy = new IntimacyEngine(store.LoadIntimacy() ?? IntimacyState.Defaults);
        _lastDiaryDate = store.LoadDiaryLastGenerated();
        _ = _runtime.ReplaceAsync(BuildRuntime(_settings, _providers, _personas));
        _ttsSessionEnabled = _settings.Ai.TtsEnabled;
        _ttsPlayer.MediaEnded += OnTtsMediaEnded; // 单次订阅（官方模式：一次订阅，字段保存当前状态）
        _chatWindow.TtsToggled += value => _ttsSessionEnabled = value;
        _interaction = new InteractionEngine(
            new InteractionEngineState(null, null),
            _settings.Ai.InteractionFrequency,
            _settings.Ai.ScreenAwareness);
        _interaction.SetEnabled(_settings.Ai.ActiveInteraction);
        _interaction.UpdateQuietHours(
            _settings.Ai.QuietHoursEnabled,
            _settings.Ai.QuietHoursStart,
            _settings.Ai.QuietHoursEnd);
        _tickTimer = new System.Threading.Timer(Tick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public PersonasFileModel Personas => _personas;
    public ProvidersFileModel Providers => _providers;

    /// <summary>可用的 TTS 引擎列表（设置页枚举/试听用；windows-tts-design.md §3）。</summary>
    public IReadOnlyList<ITtsProvider> TtsProviders => _ttsProviders;

    /// <summary>按 providers.json 重建 TTS 引擎列表：基础引擎（sapi/onecore）+
    /// 已配置的在线端点（openai）；随后按设置重选当前引擎。</summary>
    private void RebuildTtsProviders()
    {
        var list = new List<ITtsProvider>(_baseTtsProviders);
        if (_providers.Tts is not null)
        {
            list.Add(new OpenAiCompatibleTtsProvider(_providers.Tts, _credentials, _providerHttp));
        }
        _ttsProviders = list;
        _tts = TtsProviderRegistry.ResolveProvider(_ttsProviders, _settings.Ai.TtsProviderId);
    }

    public Task<ModelConnectionTestResult> TestAsync(ModelConnectionDraft draft, CancellationToken ct)
        => new ModelConnectionTester(_credentials, _providerHttp).TestAsync(draft, ct);
    public bool AiEnabled => _settings.Ai.Enabled;

    public int? AgentProcessId
    {
        get
        {
            lock (_lock)
            {
                var process = _agent;
                if (process is null) return null;
                try { return process.HasExited ? null : process.Id; }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>设置变更：同步更新轻量 UI 状态，异步串行协调 Agent 与 provider 运行时代际。</summary>
    public void ApplySettings(AppSettings settings)
    {
        long revision;
        var shouldRunAgent = settings.Ai.Enabled && settings.Ai.OutputMode != "silent";
        lock (_stateLock)
        {
            _settings = settings;
            revision = ++_runtimeRevision;
            if (!settings.Ai.Enabled)
            {
                using var active = _runtime.Acquire();
                active?.Value.RequestStop();
            }
        }
        if (!shouldRunAgent) RequestAgentStopNow();

        if (shouldRunAgent) _restartFailures = 0;
        _interaction.SetEnabled(settings.Ai.ActiveInteraction);
        _interaction.UpdateFrequency(settings.Ai.InteractionFrequency);
        _interaction.UpdateScreenAwareness(settings.Ai.ScreenAwareness);
        _interaction.UpdateQuietHours(
            settings.Ai.QuietHoursEnabled,
            settings.Ai.QuietHoursStart,
            settings.Ai.QuietHoursEnd);
        _chatWindow.TtsEnabled = settings.Ai.TtsEnabled;
        _ttsSessionEnabled = settings.Ai.TtsEnabled;
        // 引擎切换即时生效（设置页保存 → ApplySettings → 重建选择）
        _tts = TtsProviderRegistry.ResolveProvider(_ttsProviders, settings.Ai.TtsProviderId);
        _chatWindow.ScreenContextEnabled = settings.Ai.ScreenContextEnabled;
        _modeService.SetMode(settings.Ai.OutputMode switch
        {
            "danmaku" => OutputMode.Danmaku,
            "chat" => OutputMode.Chat,
            "bubble" => OutputMode.Bubble,
            _ => OutputMode.Silent,
        });
        QueueRuntimeReconcile(revision);
    }

    public void ApplyPersonas(PersonasFileModel personas)
    {
        var normalized = PersonasFileModel.Normalize(personas);
        _store.SavePersonasFile(normalized);
        long revision;
        lock (_stateLock)
        {
            _personas = normalized;
            revision = ++_runtimeRevision;
        }
        QueueRuntimeReconcile(revision);
    }

    /// <summary>
    /// 初始化引导完成（称呼 + 人格，App 启动 / 设置页开启 AI 两处触发共用此入口）：
    /// 人格落盘 → 称呼写入画像 → Onboarded 标记。保存逻辑收敛一处，调用方不重复实现。
    /// </summary>
    public void CompleteOnboarding(
        string callName,
        string personaId,
        AppSettings? settingsOverride = null)
    {
        var personas = PersonasFileModel.Normalize(_personas);
        personas.SelectedId = personaId;
        var profile = _profile with { CallName = callName.Trim() };
        var baseSettings = AppSettings.Normalize(settingsOverride ?? _settings);
        var nextSettings = baseSettings with
        {
            Ai = baseSettings.Ai with { Onboarded = true },
        };

        // 设置文件作为最后提交点：前两步失败时不会发布新的运行时快照；下次启动仍会重试引导。
        _store.SavePersonasFile(personas);
        if (nextSettings.Ai.MemoryEnabled) _store.SaveMemoryProfile(profile);
        _store.SaveSettings(nextSettings);

        long revision;
        lock (_stateLock)
        {
            _personas = personas;
            _settings = nextSettings;
            revision = ++_runtimeRevision;
        }
        _profile = profile;
        QueueRuntimeReconcile(revision);
    }

    /// <summary>设置称呼（更新记忆画像并落盘；记忆开关关 = 只更新内存画像）。</summary>
    public void SetCallName(string callName)
    {
        var next = _profile with { CallName = callName.Trim() };
        if (_settings.Ai.MemoryEnabled) _store.SaveMemoryProfile(next);
        _profile = next;
    }

    public void ApplyProviders(ProvidersFileModel providers)
    {
        var normalized = ProvidersFileModel.Normalize(providers);
        _store.SaveProvidersFile(normalized);
        long revision;
        lock (_stateLock)
        {
            _providers = normalized;
            revision = ++_runtimeRevision;
        }
        QueueRuntimeReconcile(revision);
        RebuildTtsProviders(); // TTS 在线端点配置变更 → 引擎列表重建（openai 出现/消失）
    }

    /// <summary>用户主动对话（任何模式下可用）：管道 → 输出到对话窗 + token 记账。</summary>
    public async Task SendChatAsync(string text, bool withScreenContext)
    {
        using var runtimeLease = _runtime.Acquire();
        var pipeline = runtimeLease?.Value.Pipeline;
        if (pipeline is null)
        {
            OnUiThread(() => _chatWindow.AppendAssistantAsync(
                _i18n.T("（未配置模型连接，请到设置 → AI 助手 → 模型连接）")));
            return;
        }

        DesktopPet.Core.Ai.PipelineResult result;
        try
        {
            await _chatSerial.WaitAsync();
            try
            {
                // Phase 6：记忆注入（开关开 + 有画像）+ 亲密度语气指令（开关开）
                Func<string>? memoryInjector = _settings.Ai.MemoryEnabled
                    ? () => MemoryProfileExtractor.Inject(_profile)
                    : null;
                Func<string>? suffixFactory = _settings.Ai.IntimacyEnabled
                    ? () => _intimacy.BuildIntimacyDirective()
                    : null;
                result = await pipeline.RunAsync(text, _conversation.BuildContext(CurrentContextTokens()), withScreenContext,
                    memoryInjector: memoryInjector,
                    systemPromptSuffix: suffixFactory,
                    maxTokens: CurrentMaxOutputTokens(),
                    ct: runtimeLease!.Value.LifetimeToken);
                if (result.Ok)
                {
                    // L1 会话窗口维护：成功后追加本轮（预算裁剪与 L2 摘要由 ConversationMemory 负责）
                    _conversation.Append(text, result.Text!);
                    if (result.TokensUsed > 0) RecordTokens(result.TokensUsed);
                    RecordChatSuccess(text, result.TokensUsed);   // 亲密度 + 画像更新
                }
            }
            finally
            {
                _chatSerial.Release();
            }
        }
        catch (ProviderException ex)
        {
            OnUiThread(() => _chatWindow.AppendAssistantAsync(ex.Code switch
            {
                "auth" => _i18n.T("（API Key 无效，请检查模型连接）"),
                "timeout" => _i18n.T("（模型响应超时了，让我歇口气~）"),
                _ => _i18n.T("（模型连接出错了）"),
            }));
            return;
        }
        catch (Exception)
        {
            OnUiThread(() => _chatWindow.AppendAssistantAsync(_i18n.T("（出错了，请稍后再试）")));
            return;
        }

        // UI 输出在串行段外（不延长锁持有；Speech 等同样移出）
        if (!result.Ok)
        {
            OnUiThread(() => _chatWindow.AppendAssistantAsync(result.Error switch
            {
                "empty" => _i18n.T("（消息不能为空）"),
                "too-long" => _i18n.T("（消息太长了）"),
                _ => _i18n.T("（出错了）"),
            }));
            return;
        }
        OnUiThread(() => _chatWindow.AppendAssistantAsync(result.Text!));
        Speak(result.Text!);                          // 语音开关 + 对话模式
    }

    /// <summary>打开对话窗（用户主动对话入口，任何模式可用）。</summary>
    public void EnsureChatWindow() => OnUiThread(_chatWindow.Show);

    private void QueueRuntimeReconcile(long revision)
        => ObserveTask(ReconcileRuntimeAsync(revision), "runtime reconcile");

    private async Task ReconcileRuntimeAsync(long revision)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            AppSettings settings;
            ProvidersFileModel providers;
            PersonasFileModel personas;
            Task retirement;
            lock (_stateLock)
            {
                if (_shuttingDown || revision != _runtimeRevision) return;
                settings = _settings;
                providers = _providers;
                personas = _personas;

                if (!settings.Ai.Enabled)
                {
                    using var active = _runtime.Acquire();
                    active?.Value.RequestStop();
                }
                retirement = _runtime.ReplaceAsync(BuildRuntime(settings, providers, personas));
            }
            ObserveTask(retirement, "retired runtime disposal");

            lock (_stateLock)
            {
                if (_shuttingDown || revision != _runtimeRevision) return;
            }

            var shouldRun = settings.Ai.Enabled && settings.Ai.OutputMode != "silent";
            bool running;
            lock (_lock) running = _agent is not null;
            var rpc = _rpcSlot.Current;

            if (shouldRun && !running) StartAgent();
            else if (!shouldRun && running) await StopAgentAsync().ConfigureAwait(false);
            else if (shouldRun) await PushConfigAsync(rpc, _coordinatorLifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ObserveTask(Task task, string operation)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = ObserveTaskCoreAsync(task, operation);
    }

    private async Task ObserveTaskCoreAsync(Task task, string operation)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DebugLog($"{operation} failed: {ex}"); }
    }

    // ---- Agent 生命周期 ----

    /// <summary>UI 线程封送（事件接收循环在线程池，WPF 窗口必须 UI 线程操作）。
    /// 用 BeginInvoke 异步投递：Dispatcher 关闭后 BeginInvoke 不抛异常，仅返回 Aborted
    /// （Microsoft Learn Dispatcher.BeginInvoke Remarks），避免退出竞态下同步 Invoke
    /// 因队列 abort 抛 TaskCanceledException 导致池线程崩溃。</summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void DebugLog(string msg) => _logger.Info("AiCoordinator", msg);

    private void StartAgent()
    {
        CancellationToken lifeToken = default;
        AgentLaunchContract? launch = null;
        var started = false;
        var launchFailed = false;
        lock (_stateLock)
        {
            if (_shuttingDown || !_settings.Ai.Enabled || _settings.Ai.OutputMode == "silent") return;
            lock (_lock)
            {
                DebugLog($"StartAgent: enabled={_settings.Ai.Enabled} agent={_agent is not null} hostExists={File.Exists(_agentHostPath)}");
                if (_agent is not null || _shuttingDown) return;
                _lifeCts = new CancellationTokenSource();
                using var currentProcess = Process.GetCurrentProcess();
                launch = AgentLaunchContract.Create(currentProcess);
                _agent = LaunchAgentProcess(launch);
                DebugLog($"LaunchAgentProcess -> {(_agent is null ? "null" : "pid=" + _agent.Id)}");
                if (_agent is null)
                {
                    _lifeCts.Dispose();
                    _lifeCts = null;
                    launchFailed = true;
                }
                else
                {
                    var launched = _agent;
                    launched.EnableRaisingEvents = true;
                    launched.Exited += (_, _) => OnAgentExited(launched);
                    lifeToken = _lifeCts.Token;
                    started = true;
                }
            }
        }

        if (launchFailed)
        {
            OnUiThread(() => _chatWindow.AppendAssistantAsync(_i18n.T("（Agent 进程启动失败）")));
        }
        if (started) ObserveTask(ConnectAndRunAsync(launch!.PipeName, lifeToken), "agent connection");
    }

    private Process? LaunchAgentProcess(AgentLaunchContract launch)
    {
        if (!File.Exists(_agentHostPath)) return null;
        var psi = new ProcessStartInfo(_agentHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_agentHostPath)!,
        };
        launch.ApplyTo(psi);
        return Process.Start(psi);
    }

    private void RequestAgentStopNow()
    {
        lock (_lock)
        {
            _lifeCts?.Cancel();
            try
            {
                if (_agent is { HasExited: false }) _agent.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                DebugLog("agent immediate stop failed: " + ex.Message);
            }
        }
    }

    private async Task StopAgentAsync()
    {
        CancellationTokenSource? life;
        PipeRpcClient? rpc;
        Process? agent;
        lock (_lock)
        {
            life = _lifeCts;
            _lifeCts = null;
            agent = _agent;
            _agent = null;
        }
        rpc = _rpcSlot.Take();

        life?.Cancel();
        if (rpc is not null)
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await rpc.SendAsync(new RpcMessage(RpcType.Shutdown, null), shutdownCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                DebugLog("agent shutdown message failed: " + ex.Message);
            }
        }

        if (agent is not null)
        {
            try
            {
                if (!agent.HasExited)
                {
                    using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await agent.WaitForExitAsync(exitCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        agent.Kill(entireProcessTree: true);
                        await agent.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                DebugLog("agent process stop failed: " + ex.Message);
            }
            finally
            {
                agent.Dispose();
            }
        }

        await DisposeOwnedRpcAsync(rpc).ConfigureAwait(false);
        life?.Dispose();
    }

    private static async Task DisposeOwnedRpcAsync(PipeRpcClient? rpc)
    {
        if (rpc is null) return;
        try { await rpc.DisposeAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    private void OnAgentExited(Process exited)
        => ObserveTask(RestartAgentAfterExitAsync(exited), "agent watchdog");

    private async Task RestartAgentAfterExitAsync(Process exited)
    {
        CancellationTokenSource? life;
        lock (_lock)
        {
            if (!ReferenceEquals(_agent, exited)) return;
            _agent = null;
            life = _lifeCts;
            _lifeCts = null;
        }
        life?.Cancel();
        life?.Dispose();
        var rpc = _rpcSlot.Take();
        await DisposeOwnedRpcAsync(rpc).ConfigureAwait(false);
        exited.Dispose();

        lock (_stateLock)
        {
            if (_shuttingDown || !_settings.Ai.Enabled || _settings.Ai.OutputMode == "silent") return;
        }
        var delaySeconds = Math.Min(30, 3 * (1 << Math.Min(3, _restartFailures)));
        _restartFailures++;
        DebugLog($"agent exited, restart in {delaySeconds}s (failures={_restartFailures})");
        if (_restartFailures > 5)
        {
            DebugLog("watchdog: too many failures, stopping until settings change");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
        StartAgent();
    }

    private async Task ConnectAndRunAsync(string pipeName, CancellationToken ct)
    {
        PipeRpcClient? rpc = null;
        var published = false;
        try
        {
            rpc = new PipeRpcClient(pipeName);
            await rpc.ConnectAsync(ct);
            if (ct.IsCancellationRequested || !_rpcSlot.TryPublish(rpc)) return;
            published = true;
            DebugLog("pipe connected");
            // 握手
            var hello = await rpc.ReceiveAsync(ct);
            DebugLog("hello received: " + hello.Type);
            if (hello.Type != RpcType.Hello) return;
            await PushConfigAsync(rpc, ct);

            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = new AgentHeartbeatMonitor(
                pingInterval: TimeSpan.FromSeconds(3),
                pongTimeout: TimeSpan.FromSeconds(10));
            var receiveLoop = ReceiveAgentMessagesAsync(rpc, heartbeat, connectionCts.Token);
            var heartbeatLoop = heartbeat.RunAsync(
                token => rpc.SendAsync(new RpcMessage(RpcType.Ping, null), token),
                connectionCts.Token);
            try
            {
                var completed = await Task.WhenAny(receiveLoop, heartbeatLoop).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
            }
            finally
            {
                connectionCts.Cancel();
                await ObserveConnectionLoopEndAsync(receiveLoop).ConfigureAwait(false);
                await ObserveConnectionLoopEndAsync(heartbeatLoop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            DebugLog("pipe disconnected");
            // 管道断（Agent 崩溃）→ 看门狗负责重启
        }
        catch (Exception ex)
        {
            DebugLog("receive loop error: " + ex.Message);
        }
        finally
        {
            if (rpc is not null
                && (!published || _rpcSlot.TryTake(rpc, out _)))
            {
                await DisposeOwnedRpcAsync(rpc).ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveAgentMessagesAsync(
        PipeRpcClient rpc,
        AgentHeartbeatMonitor heartbeat,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await rpc.ReceiveAsync(ct).ConfigureAwait(false);
            if (msg.Type == RpcType.Pong)
            {
                heartbeat.RecordPong();
            }
            else if (msg.Type == RpcType.ScreenEvent && msg.Payload is { } payload)
            {
                DebugLog("screen event received");
                OnAgentEvent(payload);
            }
        }
    }

    private static async Task ObserveConnectionLoopEndAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task PushConfigAsync(PipeRpcClient? rpc, CancellationToken ct)
    {
        if (rpc is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _coordinatorLifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await _agentConfigSend.WaitAsync(deadline.Token).ConfigureAwait(false);
        long revision = 0;
        try
        {
            AgentConfig cfg;
            lock (_stateLock)
            {
                if (!_settings.Ai.Enabled) return;
                revision = Interlocked.Increment(ref _agentConfigRevision);
                Volatile.Write(ref _pendingAgentRevision, revision);
                cfg = AgentConfigBuilder.Build(_settings, _personas, _providers, revision);
            }

            await rpc.SendAsync(new RpcMessage(RpcType.Config,
                JsonSerializer.SerializeToElement(cfg, JsonOpts)), deadline.Token).ConfigureAwait(false);
            Volatile.Write(ref _agentRevisionFloor, revision);
            Interlocked.CompareExchange(ref _pendingAgentRevision, 0, revision);
        }
        catch
        {
            if (revision != 0) Interlocked.CompareExchange(ref _pendingAgentRevision, 0, revision);
            await InvalidateAgentConnectionAsync(rpc).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _agentConfigSend.Release();
        }
    }

    private async Task InvalidateAgentConnectionAsync(PipeRpcClient rpc)
    {
        if (_rpcSlot.TryTake(rpc, out var owned))
            await DisposeOwnedRpcAsync(owned).ConfigureAwait(false);
    }

    private void OnAgentEvent(JsonElement payload)
    {
        try
        {
            var configRevision = payload.TryGetProperty("configRevision", out var revisionElement)
                ? revisionElement.GetInt64()
                : 0;
            var floor = Math.Max(
                Volatile.Read(ref _agentRevisionFloor),
                Volatile.Read(ref _pendingAgentRevision));
            if (configRevision < floor)
            {
                DebugLog($"drop stale screen event revision={configRevision}");
                return;
            }

            var kind = Enum.TryParse<ScreenEventKind>(payload.GetProperty("kind").GetString(), out var k)
                ? k : ScreenEventKind.Unknown;
            var summary = payload.GetProperty("summary").GetString() ?? "";
            var timestamp = DateTime.TryParse(payload.GetProperty("timestamp").GetString(), out var t)
                ? t : DateTime.Now;
            var hash = payload.TryGetProperty("frameHash", out var h) ? h.GetUInt64() : 0ul;
            var evt = new ScreenEvent(timestamp, kind, summary, hash);
            _eventLog.Add(evt); // 对话屏幕上下文用（最近 N 条）
            // 无模型/分析失败时事件降级（summary 空）→ 默认台词（UI 有反馈，不静默）
            var text = string.IsNullOrWhiteSpace(summary)
                ? _i18n.T("（看到你的屏幕有变化~）")
                : summary;
            DebugLog($"[p6] screen event kind={kind} revision={configRevision}");
            OnUiThread(() => _modeService.RouteOutput(new AiOutput(text, FromAnalysis: true)));
        }
        catch (Exception ex)
        {
            DebugLog("OnAgentEvent error: " + ex);
        }
    }

    private AiRuntimeGeneration? BuildRuntime(
        AppSettings settings,
        ProvidersFileModel providers,
        PersonasFileModel personas)
    {
        if (!settings.Ai.Enabled) return null;

        ModelRequestScheduler? scheduler = null;
        ChatPipeline? pipeline = null;
        var provider = AgentConfigBuilder.SelectProvider(providers, settings.Ai.ProviderId);
        if (provider is not null)
        {
            var model = new OpenAiCompatibleModelProvider(
                provider, _credentials, _providerHttp, requestTimeout: Timeout.InfiniteTimeSpan);
            scheduler = new ModelRequestScheduler(model, concurrency: 3);
            pipeline = new ChatPipeline(scheduler, personas.ResolveSelected, _eventLog);
        }

        IImageProvider? imageProvider = null;
        if (providers.Image is not null)
        {
            imageProvider = new OpenAiCompatibleImageProvider(
                providers.Image, _credentials, _providerHttp, requestTimeout: TimeSpan.FromSeconds(120));
        }

        return scheduler is null && imageProvider is null
            ? null
            : new AiRuntimeGeneration(scheduler, pipeline, imageProvider);
    }

    private void RecordTokens(int tokens)
    {
        try
        {
            // 记到选中宠物（对话对象 = 浮球/选中实例），无选中回退第一只。
            // 修复：原实现恒取 states.FirstOrDefault()，多宠物时 XP 全记给第一只。
            var states = _store.LoadCare();
            var store = _store.LoadPetStore();
            var targetId = store is null
                ? states.Keys.FirstOrDefault()
                : PetStoreModel.SelectedPetInstance(store)?.Id
                  ?? store.Instances.FirstOrDefault()?.Id
                  ?? states.Keys.FirstOrDefault();
            if (targetId is null || !states.TryGetValue(targetId, out var care)) return;
            _recordTokens(targetId, care, tokens);
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex);
        }
    }

    // ---- Phase 6：陪伴增强 ----

    /// <summary>对话成功后：亲密度记账（开关开）+ 画像更新（记忆开关开）+ 持久化。</summary>
    private void RecordChatSuccess(string userText, int tokensUsed)
    {
        try
        {
            if (_settings.Ai.IntimacyEnabled)
            {
                var nextIntimacy = new IntimacyEngine(_intimacy.State);
                nextIntimacy.RecordConversation(tokensUsed, DateTime.Now);
                _store.SaveIntimacy(nextIntimacy.State);
                _intimacy = nextIntimacy;
            }
            if (_settings.Ai.MemoryEnabled)
            {
                var nextProfile = MergeProfile(_profile, userText);
                // L2 会话摘要合并进 L3 画像（“总结存记忆”：超预算压缩的会话内容落画像，不丢）
                if (_conversation.Summary.Length > 0)
                    nextProfile = nextProfile with { Summary = _conversation.Summary };
                _store.SaveMemoryProfile(nextProfile);
                _profile = nextProfile;
            }
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex);
        }
        catch (Exception ex)
        {
            DebugLog("RecordChatSuccess error: " + ex.Message);
        }
    }

    /// <summary>画像合并：新轮次提取（称呼/作息优先新值，话题并集 top3，摘要滚动追加 ≤200 字）。</summary>
    private static UserProfile MergeProfile(UserProfile current, string userText)
    {
        var extracted = MemoryProfileExtractor.Extract(
            [(new ChatMessage(ChatRole.User, userText), DateTime.Now)]);
        var topics = current.Topics
            .Concat(extracted.Topics)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var summary = string.IsNullOrEmpty(extracted.Summary)
            ? current.Summary
            : string.IsNullOrEmpty(current.Summary)
                ? extracted.Summary
                : current.Summary + "；" + extracted.Summary;
        if (summary.Length > 200) summary = summary[..200] + "…";
        return new UserProfile(
            extracted.CallName.Length > 0 ? extracted.CallName : current.CallName,
            topics,
            extracted.Routine.Length > 0 ? extracted.Routine : current.Routine,
            summary);
    }

    /// <summary>周期 tick（30s）：每日总结检查 + 主动互动。AI 总开关关 = 全部失效。</summary>
    private void Tick(object? state)
    {
        if (_shuttingDown || !_settings.Ai.Enabled) return;
        TryDailySummary();
        TryProactiveInteraction();
    }

    /// <summary>主动互动：定时/事件触发 → 多宠物分派 → 并行独立请求（P1）→ 当前模式输出。</summary>
    private void TryProactiveInteraction()
    {
        if (!_settings.Ai.ActiveInteraction) return;
        var now = DateTime.Now;
        if (!_interaction.TryNextTrigger(now, _eventLog.Recent(), out var trigger) || trigger is null) return;
        DebugLog($"[p6] interaction triggered: {trigger.Reason} at {now:HH:mm:ss}");

        var petIds = (_store.LoadPetStore()?.Instances ?? [])
            .Where(i => i.Visible)
            .Where(i => !IsEventDriven(trigger.Reason) || i.ReactsToActivity)
            .Select(i => i.Id)
            .ToArray();
        if (petIds.Length == 0) return;

        // 多宠物分派：round-robin 竞争 1-2 只，或全员回应（设置页开关；同一事件各自表达 = 并行独立请求）
        var speakers = _dispatcher.SelectSpeakers(petIds, allReply: _settings.Ai.AllReply);
        _ = Task.Run(async () =>
        {
            try
            {
                var tasks = speakers.Select(petId => GenerateInteractionLineAsync(petId, trigger));
                var lines = await Task.WhenAll(tasks); // 并行独立请求：一次等待而非 N 倍延迟
                foreach (var line in lines)
                {
                    if (_shuttingDown || _coordinatorLifetime.IsCancellationRequested) return;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    DebugLog($"[p6] route output length={line.Length}");
                    OnUiThread(() =>
                    {
                        if (!_shuttingDown) _modeService.RouteOutput(new AiOutput(line, FromAnalysis: true));
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DebugLog($"[p6] interaction failed: {ex.Message}"); }
        });
    }

    /// <summary>事件驱动评论（屏幕事件触发）只分派给「对活动做出反应」的宠物；定时问候全部分派。</summary>
    private static bool IsEventDriven(string reason)
        => reason is not ("morning" or "evening" or "late-night");

    /// <summary>单只宠物的主动互动台词（P1 优先级，8s 超时；失败跳过本轮）。
    /// 记忆注入（"隔天主动提起"）+ 亲密度指令 + 每宠物独立人格（PersonaId 覆盖全局）。</summary>
    private async Task<string?> GenerateInteractionLineAsync(string petId, InteractionTrigger trigger)
    {
        try
        {
            using var runtimeLease = _runtime.Acquire();
            var scheduler = runtimeLease?.Value.Scheduler;
            if (scheduler is null) return null;
            var persona = ResolvePetPersona(petId);
            var petName = (_store.LoadPetStore()?.Instances ?? [])
                .FirstOrDefault(i => i.Id == petId)?.Name ?? petId;
            var systemPrompt = PersonaEngine.BuildSystemPrompt(persona)
                + $"\n\n你是宠物「{petName}」，现在用户没有主动找你。";
            if (_settings.Ai.MemoryEnabled)
            {
                var memory = MemoryProfileExtractor.Inject(_profile);
                if (memory.Length > 0) systemPrompt += "\n\n" + memory;
            }
            if (_settings.Ai.IntimacyEnabled)
                systemPrompt += "\n\n" + _intimacy.BuildIntimacyDirective();
            var request = new ChatRequest(
                systemPrompt,
                [new ChatMessage(ChatRole.User, trigger.PromptContext + "（请用一句简短的话主动说，不超过 30 字）")],
                PersonaEngine.Temperature,
                PersonaEngine.MaxTokens);
            var result = await scheduler.EnqueueAsync(
                RequestPriority.Interactive, request, runtimeLease!.Value.LifetimeToken);
            return result.Text;
        }
        catch (Exception ex)
        {
            DebugLog("interaction line failed: " + ex.Message);
            return null; // 互动失败静默跳过（下轮轮换补偿）
        }
    }

    /// <summary>宠物人格解析：实例 PersonaId 覆盖全局（内置/自定义）；空 = 全局人格。</summary>
    private Persona ResolvePetPersona(string petId)
    {
        var global = _personas.ResolveSelected();
        var personaId = (_store.LoadPetStore()?.Instances ?? [])
            .FirstOrDefault(i => i.Id == petId)?.PersonaId;
        if (string.IsNullOrEmpty(personaId)) return global;
        var builtin = BuiltinPersonas.GetById(personaId);
        if (builtin is not null) return builtin;
        return _personas.CustomPersonas.FirstOrDefault(p => p.Id == personaId) ?? global;
    }

    /// <summary>每日总结：次日补昨日（全局一份）；总结图开关开 + 有生图连接时生成，失败不影响文本。</summary>
    private void TryDailySummary()
    {
        if (!_settings.Ai.DailySummary) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly dueDay;
        lock (_stateLock)
        {
            if (_pendingDiaryDate is not null) return;
            var due = DailySummaryTrigger.GetDueDate(_lastDiaryDate, today);
            if (due is null) return;
            dueDay = due.Value;
            _pendingDiaryDate = dueDay;
        }
        _ = Task.Run(() => GenerateDailySummaryAsync(dueDay, today));
    }

    private async Task GenerateDailySummaryAsync(DateOnly day, DateOnly completionDate)
    {
        var completed = false;
        try
        {
            using var runtimeLease = _runtime.Acquire();
            var runtime = runtimeLease?.Value;
            if (runtime?.Scheduler is null) return;
            var petName = FirstPetName();
            var data = new DailySummaryData(
                day,
                _profile.Summary,
                ScreenContextFormatter.Format(_eventLog.Recent(), 4),
                InferMood(),
                petName);
            var request = new ChatRequest(
                SummaryPromptBuilder.Build(data),
                [new ChatMessage(ChatRole.User, "请生成今天的总结")],
                Temperature: 0.8,
                MaxTokens: 300);
            var result = await runtime.Scheduler.EnqueueAsync(
                RequestPriority.Background, request, runtime.LifetimeToken);

            var text = result.Text ?? "";
            var txtPath = DiaryStore.TextPath(_store.DirectoryPath, day);
            Directory.CreateDirectory(Path.GetDirectoryName(txtPath)!);
            AtomicFileWriter.WriteAllText(txtPath, text);

            if (_settings.Ai.SummaryImage && runtime.ImageProvider is not null)
            {
                try
                {
                    var image = await runtime.ImageProvider.GenerateAsync(
                        new ImageGenRequest(ImagePromptBuilder.Build(text, petName)), runtime.LifetimeToken);
                    AtomicFileWriter.WriteAllBytes(
                        DiaryStore.ImagePath(_store.DirectoryPath, day),
                        image.PngBytes);
                }
                catch (Exception ex)
                {
                    DebugLog("summary image failed (text kept): " + ex.Message); // 降级：文本照常
                }
            }

            _store.SaveDiaryLastGenerated(completionDate);
            completed = true;
            OnUiThread(() => _modeService.RouteOutput(new AiOutput(
                _i18n.T("今天的总结出炉啦~（日记已保存）"),
                FromAnalysis: true)));
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex);
        }
        catch (Exception ex)
        {
            DebugLog("daily summary failed: " + ex.Message);
        }
        finally
        {
            lock (_stateLock)
            {
                _pendingDiaryDate = null;
                if (completed) _lastDiaryDate = completionDate;
            }
        }
    }

    /// <summary>朗读（语音开关 + 仅对话模式；合成流 → 临时文件 → MediaPlayer）。</summary>
    /// <summary>当前选中模型连接的最大输出配置（空 = 不发送 max_tokens，上游默认）。
    /// 对话路径与互动/评论路径分离：互动/评论固定内置短句 120，不受此配置影响。</summary>
    private int? CurrentMaxOutputTokens()
        => _providers.Models.FirstOrDefault(m => m.Id == _settings.Ai.ProviderId)?.MaxOutputTokens
           ?? _providers.Models.FirstOrDefault()?.MaxOutputTokens;

    /// <summary>重开对话（ChatWindow“从这里重新开始”）：清空 L1/L2 会话记忆；记忆画像/亲密度保留。</summary>
    public void ClearChatHistory() => _conversation.Clear();

    /// <summary>当前选中模型连接的上下文长度（未配置 = 默认 32k 估算）。</summary>
    private int CurrentContextTokens()
        => _providers.Models.FirstOrDefault(m => m.Id == _settings.Ai.ProviderId)?.ContextWindowTokens
           ?? _providers.Models.FirstOrDefault()?.ContextWindowTokens
           ?? ConversationMemory.DefaultContextTokens;

    public void Speak(string text)
    {
        if (!_ttsSessionEnabled || _settings.Ai.OutputMode != "chat") return;
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = Task.Run(async () =>
        {
            // 捕获当前引擎快照：设置变更可能在后台朗读期间切换 _tts
            var active = _tts;
            try
            {
                await SpeakCoreAsync(text, active);
            }
            catch (Exception ex)
            {
                // 非默认引擎失败（端点不可用/网络/认证）→ 降级 SAPI 兜底一次，不打断对话
                if (!ReferenceEquals(active, _sapiTts))
                {
                    DebugLog($"tts {_tts.Id} failed ({ex.GetType().Name}: {ex.Message}), fallback to sapi");
                    try
                    {
                        await SpeakCoreAsync(text, force: _sapiTts);
                    }
                    catch (Exception fallbackEx)
                    {
                        DebugLog("tts fallback failed: " + fallbackEx.Message);
                    }
                }
                else
                {
                    DebugLog("tts failed: " + ex.Message); // 朗读失败不影响对话
                }
            }
        });
    }

    private async Task SpeakCoreAsync(string text, ITtsProvider? force = null)
    {
        var provider = force ?? _tts;
        // 朗读声音：设置页可选；空 = 自动（各引擎内部解析：SAPI 语言回退 / OneCore 默认语音 /
        // 在线端点配置默认音色）。不在运行时额外调 ListVoicesAsync（在线引擎会多一次网络调用）。
        using var stream = await provider.SynthesizeAsync(
            new TtsSynthesisRequest(text, _settings.Ai.TtsVoiceName, _settings.Ai.TtsSpeedPercent), CancellationToken.None);
        var bytes = ((MemoryStream)stream).ToArray();
        var tmp = Path.Combine(Path.GetTempPath(), $"desktoppet-tts-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(tmp, bytes);
        OnUiThread(() =>
        {
            _pendingTtsTempPath = tmp;
            _ttsPlayer.Open(new Uri(tmp));
            _ttsPlayer.Play();
        });
    }

    /// <summary>播放结束后清理当前临时文件（单次订阅，防闭包累积）。</summary>
    private void OnTtsMediaEnded(object? sender, EventArgs e)
    {
        var path = _pendingTtsTempPath;
        _pendingTtsTempPath = null;
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("AiCoordinator", $"TTS temp cleanup failed: {ex.Message}");
        }
    }

    /// <summary>总结图心情推断：从 CareState 饥饿度推导（简单规则，不引入新状态）。</summary>
    private string InferMood()
    {
        var state = _store.LoadCare().Values.FirstOrDefault();
        if (state is null) return "平和";
        var hunger = CareEngine.HungerAt(state, DateTime.Now);
        return hunger is Hunger.Hungry or Hunger.Starving
            ? "有点饿（但见到你就开心）"
            : state.Xp >= 100 ? "元气满满" : "平和";
    }

    private string FirstPetName()
        => (_store.LoadPetStore()?.Instances.FirstOrDefault()?.Name) ?? "桌宠";

    public void Dispose()
    {
        BeginShutdown();
        ObserveTask(GetOrStartDisposeTask(), "AI coordinator disposal");
    }

    public ValueTask DisposeAsync()
    {
        BeginShutdown();
        return new ValueTask(GetOrStartDisposeTask());
    }

    private void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
        _shuttingDown = true;
        _coordinatorLifetime.Cancel();
        _tickTimer?.Dispose();
        _tickTimer = null;
        _ttsPlayer.Close();
        using var active = _runtime.Acquire();
        active?.Value.RequestStop();
        RequestAgentStopNow();
    }

    private Task GetOrStartDisposeTask()
    {
        lock (_disposeSync) return _disposeTask ??= DisposeCoreAsync();
    }

    private async Task DisposeCoreAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAgentAsync().ConfigureAwait(false);
            await _runtime.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
        _providerHttp.Dispose();
        await _agentConfigSend.WaitAsync().ConfigureAwait(false);
        _agentConfigSend.Release();
        _agentConfigSend.Dispose();
        _coordinatorLifetime.Dispose();
        _chatSerial.Dispose();
        _lifecycle.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

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
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Memory;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;
using DesktopPet.Core.SpriteSkill;
using DesktopPet.Core.Summary;
using DesktopPet.Core.Tts;
using DesktopPet.Infra.Diagnostics;
using DesktopPet.Infra.Lifecycle;
using DesktopPet.Infra.Providers;
using DesktopPet.Infra.PipeRpc;
using DesktopPet.Infra.Storage;
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
    // 总结图失败补试（渠道慢/抖动：文本照常，图片当天自动补，防当天图永远缺失）
    private readonly SummaryImageRetryPolicy _imageRetry = new();
    // L1/L2 分层会话记忆（简洁版）：L1 最近消息按 token 预算保留（预算 = 模型上下文 50%，
    // 256k 配置下几百轮不触发）；L2 真超预算时最早轮次压缩进滚动摘要注入，不静默丢弃；
    // 摘要可合并进 L3 画像（RecordChatSuccess）。
    private readonly ConversationMemory _conversation = new();
    // 语音输出：三级 Provider 栈（windows-tts-design.md §3）——默认 SAPI 离线兜底；
    // 引擎选择/降级由 TtsProviderRegistry 处理；Speak 按设置选引擎，失败降级 sapi
    private readonly ITtsProvider _sapiTts = new SapiTtsProvider();
    private readonly IReadOnlyList<ITtsProvider> _baseTtsProviders;
    private IReadOnlyList<ITtsProvider> _ttsProviders = [];
    private ITtsProvider _tts = null!; // 构造函数 RebuildTtsProviders 赋值
    private readonly MediaPlayer _ttsPlayer = new();
    // 待清理的 TTS 临时文件（MediaEnded 只订阅一次，防闭包随朗读次数累积）
    private string? _pendingTtsTempPath;
    // 朗读生效状态（会话内）：初始 = 持久设置；对话窗按钮切换；设置页保存重置。
    // 修复：原实现 Speak 只看持久设置，对话窗朗读按钮点击无效。
    private bool _ttsSessionEnabled;
    private System.Threading.Timer? _tickTimer;         // 30s 周期：主动互动 + 每日总结

    // ---- 分析活性看门狗：心跳只证明进程活着，不证明分析在产事件。
    // capture 死锁/引擎故障时事件流停滞但心跳正常——超过阈值强制重启 Agent。
    // 阈值 10min：静止屏幕（无变化无事件）属正常，10min 一次重启成本极低（对用户透明）。
    private const long AnalysisStallThresholdMs = 10 * 60 * 1000;
    private const long AnalysisRestartMinIntervalMs = 5 * 60 * 1000;
    private long _lastScreenEventTick;
    private long _lastWatchdogRestartTick;

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
        CleanupOldScreenEventJournals();
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

    /// <summary>
    /// 生图页入口（阶段 5）：按连接 + 模型生成（透明请求由门面按模型能力自动分流：
    /// 原生直传 / 绿幕两段式）。无 runtime（AI 关 / 未配置连接）或连接不存在时抛 ProviderException。
    /// </summary>
    public async Task<ImageGenOutput> GenerateImageAsync(
        string connectionId, string modelId, ImageGenSpec spec, CancellationToken ct)
    {
        using var runtimeLease = _runtime.Acquire();
        var runtime = runtimeLease?.Value;
        if (runtime?.ImageGen is null)
            throw new ProviderException("invalid-request", "生图未配置（无可用生图连接）");
        var connection = FindImageConnection(connectionId);
        if (connection is null)
            throw new ProviderException("invalid-request", $"生图连接不存在: {connectionId}");
        return await runtime.ImageGen.GenerateAsync(connection, modelId, spec, ct);
    }

    /// <summary>图生图/编辑入口（v2 阶段 D）：参考图 + 提示词，透明请求同样由门面分流。</summary>
    public async Task<ImageGenOutput> EditImageAsync(
        string connectionId, string modelId, ImageGenSpec spec,
        IReadOnlyList<ReferenceImage> references, CancellationToken ct)
    {
        using var runtimeLease = _runtime.Acquire();
        var runtime = runtimeLease?.Value;
        if (runtime?.ImageGen is null)
            throw new ProviderException("invalid-request", "生图未配置（无可用生图连接）");
        var connection = FindImageConnection(connectionId);
        if (connection is null)
            throw new ProviderException("invalid-request", $"生图连接不存在: {connectionId}");
        return await runtime.ImageGen.EditAsync(connection, modelId, spec, references, ct);
    }

    private ImageConnection? FindImageConnection(string connectionId)
        => _providers.Image?.Connections.FirstOrDefault(c =>
            string.Equals(c.Id, connectionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 创建动作精灵图技能会话：复用当前对话 Provider（Ai.ProviderId）+ 生图连接/模型。
    /// 生图连接解析优先级：显式参数 &gt; SummaryImageModelRef &gt; 首个连接；
    /// 模型同理。未配置对话模型或生图连接时返回 null（调用方提示配置）。
    /// </summary>
    public SpriteSkillSession? CreateSpriteSkillSession(
        string? connectionId = null, string? imageModelId = null,
        SpriteSkillOptions? options = null)
    {
        var modelConfig = _providers.Models.FirstOrDefault(m => m.Id == _settings.Ai.ProviderId);
        if (modelConfig is null) return null;
        var connections = _providers.Image?.Connections;
        if (connections is null || connections.Count == 0) return null;

        var (refConnId, refModelId) = ParseSummaryImageRef(_settings.Ai.SummaryImageModelRef);
        var connection = connections.FirstOrDefault(c =>
                             string.Equals(c.Id, connectionId ?? refConnId, StringComparison.OrdinalIgnoreCase))
                         ?? connections[0];
        var modelId = imageModelId ?? refModelId ?? connection.Models.FirstOrDefault() ?? "";
        if (modelId.Length == 0) return null;

        var model = new OpenAiCompatibleModelProvider(modelConfig, _credentials, _providerHttp);
        return new SpriteSkillSession(this, model, connection.Id, modelId,
            SpriteSkillCatalog.SpritePet, new CellSpec(192, 208), options);
    }

    private static (string? ConnectionId, string? ModelId) ParseSummaryImageRef(string reference)
    {
        if (string.IsNullOrEmpty(reference)) return (null, null);
        var parts = reference.Split('/');
        return parts.Length >= 2 ? (parts[0], parts[1]) : (null, null);
    }

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
            Task retirement = Task.CompletedTask;
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

                // 签名相同 = 关键输入未变（改气泡外观/TTS 音色等无关设置）→ 跳过重建。
                // 修复：原实现无条件 ReplaceAsync，每次保存设置都重建 provider/scheduler/worker 池，
                // 旧代际 drain 会让退出偶发阻塞（对话租约最长 30s）。
                // 注意：签名短路只跳过 runtime 重建；Agent 启停/配置推送必须继续执行——
                // 冷启动时构造函数已发布同签名 runtime，若在此 return 则 StartAgent 永不执行
                // （看门狗两条路径也不会启动新 Agent：退出重启需进程先存在，活性看门狗有 !running 闸）。
                var current = _runtime.Current;
                var nextSignature = AiRuntimeGeneration.SignatureOf(settings, providers, personas);
                if (current is null || current.Signature != nextSignature)
                {
                    retirement = _runtime.ReplaceAsync(BuildRuntime(settings, providers, personas));
                }
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
        // 等待写锁：事件/心跳 Pong 可能占用管道，宽松超时。
        // 修复：超时 = 拥塞不是断连——原实现 3s 统一截止并销毁连接，
        // 偶发拥塞会误杀健康连接触发整轮重启（含指数退避）。
        try
        {
            await _agentConfigSend.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            DebugLog("config push skipped: pipe write busy");
            return; // 放弃本次推送；下次设置变更/重连会重新下发
        }

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

            using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                ct, _coordinatorLifetime.Token);
            sendDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            await rpc.SendAsync(new RpcMessage(RpcType.Config,
                JsonSerializer.SerializeToElement(cfg, JsonOpts)), sendDeadline.Token).ConfigureAwait(false);
            Volatile.Write(ref _agentRevisionFloor, revision);
            Interlocked.CompareExchange(ref _pendingAgentRevision, 0, revision);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 真断连/对端关闭：连接失效，看门狗负责重启
            if (revision != 0) Interlocked.CompareExchange(ref _pendingAgentRevision, 0, revision);
            await InvalidateAgentConnectionAsync(rpc).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
                                                   && !_coordinatorLifetime.IsCancellationRequested)
        {
            // 发送超时（对端 3s 无响应）：连接疑似已坏，失效走看门狗重启
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
            var stale = payload.TryGetProperty("isStale", out var staleElement)
                && staleElement.GetBoolean();
            var evt = new ScreenEvent(timestamp, kind, summary, hash, stale);
            _lastScreenEventTick = Environment.TickCount64; // 活性看门狗信号
            _eventLog.Add(evt); // 对话屏幕上下文用（最近 N 条）
            AppendScreenEvent(evt); // journal 落盘（按天，重启不丢，总结/回顾用）
            if (stale)
            {
                // 分析期间屏幕已又变化：内容描述的是过去的屏幕，弹幕会滞后于当前画面。
                // 事件已记录 journal；跳过主动输出（下次分析新帧后再弹）。
                DebugLog($"[p6] stale screen event kind={kind} revision={configRevision}; journal only");
                return;
            }
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

        ImageGenService? imageGen = null;
        SummaryImageTarget? summaryImageTarget = null;
        if (providers.Image is not null)
        {
            // 生图门面（windows-imagegen-design.md §8）：连接列表 + 能力分流 + 绿幕透明管线；
            // 超时 300s：实测慢渠道单张需 3 分半（210s），120s 必然超时；
            // 再慢由 SummaryImageRetryPolicy 当天补试兜底。
            imageGen = new ImageGenService(
                ImageModelCatalog.LoadBuiltIn(), _credentials, _providerHttp,
                requestTimeout: TimeSpan.FromSeconds(300),
                modelCapabilities: providers.Image.ModelCapabilities);
            summaryImageTarget = SummaryImageTargetResolver.Resolve(
                providers.Image.Connections, settings.Ai.SummaryImageModelRef);
        }

        return scheduler is null && imageGen is null
            ? null
            : new AiRuntimeGeneration(
                scheduler, pipeline, imageGen, summaryImageTarget,
                AiRuntimeGeneration.SignatureOf(settings, providers, personas));
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

    /// <summary>周期 tick（30s）：每日总结检查 + 总结图补试 + 主动互动 + 分析活性看门狗。
    /// AI 总开关关 = 全部失效。</summary>
    private void Tick(object? state)
    {
        if (_shuttingDown || !_settings.Ai.Enabled) return;
        TryDailySummary();
        TryRetrySummaryImage();
        TryProactiveInteraction();
        CheckAnalysisLiveness();
    }

    /// <summary>分析事件停滞检测：Agent 心跳正常但长时间无任何事件（capture 死锁/引擎故障）→ 强制重启。</summary>
    private void CheckAnalysisLiveness()
    {
        if (!_settings.Ai.ScreenAnalysis || _settings.Ai.OutputMode == "silent") return;
        bool running;
        lock (_lock) running = _agent is not null;
        if (!running) return;

        var now = Environment.TickCount64;
        if (now - _lastScreenEventTick < AnalysisStallThresholdMs) return;
        if (now - _lastWatchdogRestartTick < AnalysisRestartMinIntervalMs) return;

        _lastWatchdogRestartTick = now;
        DebugLog($"analysis watchdog: no screen events for {AnalysisStallThresholdMs / 60000}min, restarting agent");
        RequestAgentStopNow();
        StartAgent();
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
                LoadActivityHighlights(day),
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

            if (_settings.Ai.SummaryImage && runtime.SummaryImageTarget is not null && runtime.ImageGen is not null)
            {
                try
                {
                    // 总结图：16:9 横版配图 + 1K 档（配图够用省钱）；不透明（非精灵图，跳过绿幕管线）；
                    // 多模型容错：首选失败自动换同连接下一模型（GenerateWithFallbackAsync）
                    var image = await runtime.ImageGen.GenerateWithFallbackAsync(
                        runtime.SummaryImageTarget.Connection,
                        runtime.SummaryImageTarget.ModelId,
                        new ImageGenSpec(
                            ImagePromptBuilder.Build(text, petName),
                            ImageAspectRatio.R16x9,
                            ImageScale.S1K),
                        runtime.LifetimeToken);
                    AtomicFileWriter.WriteAllBytes(
                        DiaryStore.ImagePath(_store.DirectoryPath, day),
                        image.Bytes);
                }
                catch (Exception ex)
                {
                    DebugLog("summary image failed (text kept): " + ex.Message); // 降级：文本照常
                    _imageRetry.RecordFailure(day, DateTime.Now);               // 当天补试
                }
            }

            _store.SaveDiaryLastGenerated(completionDate);
            completed = true;
            OnUiThread(() => _modeService.RouteOutput(new AiOutput(
                _i18n.T("总结出炉啦~（日记已保存）"),
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
    /// 对话路径与互动/评论路径分离：互动/评论固定内置短句 120，不受此配置影响。
    /// 修复：原实现未命中选中连接时回退到第一个连接——多连接时 A 的 max_tokens 会串到 B。</summary>
    private int? CurrentMaxOutputTokens()
        => _providers.Models.FirstOrDefault(m => m.Id == _settings.Ai.ProviderId)?.MaxOutputTokens;

    /// <summary>重开对话（ChatWindow“从这里重新开始”）：清空 L1/L2 会话记忆；记忆画像/亲密度保留。</summary>
    public void ClearChatHistory() => _conversation.Clear();

    /// <summary>当前选中模型连接的上下文长度（未配置 = 默认 32k 估算）。
    /// 修复：原实现同样回退到第一个连接（串配置），未命中时只用默认估算。</summary>
    private int CurrentContextTokens()
        => _providers.Models.FirstOrDefault(m => m.Id == _settings.Ai.ProviderId)?.ContextWindowTokens
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

    // ---- 屏幕事件 journal（按天 jsonl 落盘：总结/回顾的"当天活动"素材）----

    /// <summary>屏幕事件按天追加到 journal；写失败静默降级（不阻塞事件流）。</summary>
    private void AppendScreenEvent(ScreenEvent evt)
    {
        try
        {
            var path = ScreenEventStore.Path(_store.DirectoryPath, DateOnly.FromDateTime(evt.Timestamp));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(evt, JournalJsonOpts) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            DebugLog("screen event journal append failed: " + ex.Message);
        }
    }

    /// <summary>当天活动回顾（journal 读取 → 会话归并 → 格式化；文件缺失/损坏降级空串）。</summary>
    private string LoadActivityHighlights(DateOnly day)
    {
        try
        {
            var path = ScreenEventStore.Path(_store.DirectoryPath, day);
            if (!File.Exists(path)) return "";
            // FileShare.ReadWrite：与 AppendScreenEvent 的追加写入并发（总结触发时事件流仍在写）
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var events = new List<ScreenEvent>();
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<ScreenEvent>(line, JournalJsonOpts);
                    if (evt is not null) events.Add(evt);
                }
                catch (JsonException)
                {
                    // 坏行跳过（半行写入/文件损坏），不影响其余事件
                }
            }
            return ActivitySummaryFormatter.Format(ActivitySessionBuilder.Build(events));
        }
        catch (Exception ex)
        {
            DebugLog("screen event journal read failed: " + ex.Message);
            return "";
        }
    }

    /// <summary>清理 30 天前的 journal 文件（只删本命名规则的文件；单文件失败不中断）。</summary>
    private void CleanupOldScreenEventJournals()
    {
        try
        {
            var diaryDir = Path.Combine(_store.DirectoryPath, "diary");
            if (!Directory.Exists(diaryDir)) return;
            var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(diaryDir, ScreenEventStore.FilePrefix + "*"))
            {
                if (ScreenEventStore.ParseDateFromFileName(Path.GetFileName(file)) is { } day && day < cutoff)
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        DebugLog("screen event journal cleanup failed: " + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog("screen event journal cleanup error: " + ex.Message);
        }
    }

    /// <summary>总结图补试检查（tick 调用）：到点且有额度则后台重试当天/昨日失败的图。</summary>
    private void TryRetrySummaryImage()
    {
        if (!_settings.Ai.DailySummary || !_settings.Ai.SummaryImage) return;
        if (!_imageRetry.TryConsumeRetry(DateOnly.FromDateTime(DateTime.Now), DateTime.Now, out var day)) return;
        _ = Task.Run(() => RetrySummaryImageAsync(day));
    }

    /// <summary>补试：读已落盘的总结文本 → 重新生图 → 写 png；成功清状态，失败等下一窗口。</summary>
    private async Task RetrySummaryImageAsync(DateOnly day)
    {
        try
        {
            using var runtimeLease = _runtime.Acquire();
            var runtime = runtimeLease?.Value;
            if (runtime?.SummaryImageTarget is null || runtime.ImageGen is null)
            {
                _imageRetry.Reset(); // 生图连接已不可用（配置变更）→ 放弃补试
                return;
            }
            var txtPath = DiaryStore.TextPath(_store.DirectoryPath, day);
            if (!File.Exists(txtPath))
            {
                _imageRetry.Reset(); // 文本缺失（异常状态）→ 放弃补试
                return;
            }
            var text = await File.ReadAllTextAsync(txtPath);
            var image = await runtime.ImageGen.GenerateWithFallbackAsync(
                runtime.SummaryImageTarget.Connection,
                runtime.SummaryImageTarget.ModelId,
                new ImageGenSpec(
                    ImagePromptBuilder.Build(text, FirstPetName()),
                    ImageAspectRatio.R16x9,
                    ImageScale.S1K),
                runtime.LifetimeToken);
            AtomicFileWriter.WriteAllBytes(
                DiaryStore.ImagePath(_store.DirectoryPath, day),
                image.Bytes);
            _imageRetry.Reset();
            DebugLog($"summary image retry succeeded for {day:yyyy-MM-dd}");
        }
        catch (Exception ex)
        {
            _imageRetry.RecordRetryFailure(DateTime.Now);
            DebugLog("summary image retry failed: " + ex.Message);
        }
    }

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

    /// <summary>journal 编解码选项：枚举存字符串（文件可读，兼容 kind 枚举演进）；
    /// 中文不转义（本地数据文件，非 HTML 上下文，无注入面）。</summary>
    private static readonly JsonSerializerOptions JournalJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

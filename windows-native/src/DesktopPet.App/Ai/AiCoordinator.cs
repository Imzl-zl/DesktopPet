using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using DesktopPet.App.Windows;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Care;
using DesktopPet.Core.Interaction;
using DesktopPet.Core.Memory;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;
using DesktopPet.Core.Summary;
using DesktopPet.Infra.PipeRpc;
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
///   对话朗读（Edge TTS，语音开关 + 仅对话模式）
/// </summary>
public sealed class AiCoordinator : IDisposable
{
    private readonly FileJsonStore _store;
    private readonly ModeService _modeService;
    private readonly ChatWindow _chatWindow;
    private readonly Action<string, CareState, int> _recordTokens;
    private readonly string _agentHostPath;
    private readonly ScreenEventLog _eventLog = new();
    private readonly object _lock = new();

    private AppSettings _settings;
    private PersonasFileModel _personas;
    private ProvidersFileModel _providers;

    private Process? _agent;
    private PipeRpcClient? _rpc;
    private CancellationTokenSource? _lifeCts;
    private bool _shuttingDown;
    private int _restartFailures;

    // App 侧对话管道（对话直连 provider，不占 Agent）
    private ModelRequestScheduler? _chatScheduler;
    private ChatPipeline? _pipeline;
    private readonly WindowsCredentialStore _credentials = new();

    // Phase 6：陪伴增强状态
    private UserProfile _profile;                       // 记忆画像（记忆开关关 = 空画像）
    private IntimacyEngine _intimacy;                   // 亲密度（0-100 双线）
    private readonly PetInteractionDispatcher _dispatcher = new();
    private InteractionEngine _interaction;             // 主动互动（频率/感知随设置更新）
    private DateOnly? _lastDiaryDate;                   // 日记最近生成日期
    // L1/L2 分层会话记忆（简洁版）：L1 最近消息按 token 预算保留（预算 = 模型上下文 50%，
    // 256k 配置下几百轮不触发）；L2 真超预算时最早轮次压缩进滚动摘要注入，不静默丢弃；
    // 摘要可合并进 L3 画像（RecordChatSuccess）。
    private readonly ConversationMemory _conversation = new();
    // 语音输出：SAPI 离线合成（默认；Edge TTS 对 SChannel 风控不可用，见 EdgeTtsProvider 注释）
    private readonly ITtsProvider _tts = new SapiTtsProvider();
    private readonly MediaPlayer _ttsPlayer = new();
    private IImageProvider? _imageProvider;             // 总结图（providers.json image 段）
    private System.Threading.Timer? _tickTimer;         // 30s 周期：主动互动 + 每日总结

    public AiCoordinator(
        FileJsonStore store,
        ModeService modeService,
        ChatWindow chatWindow,
        Action<string, CareState, int> recordTokens,
        string agentHostPath)
    {
        _store = store;
        _modeService = modeService;
        _chatWindow = chatWindow;
        _recordTokens = recordTokens;
        _agentHostPath = agentHostPath;
        _settings = AppSettings.Normalize(store.LoadSettings() ?? AppSettings.Defaults(Core.I18n.I18nService.Detect()));
        _personas = PersonasFileModel.Normalize(store.LoadPersonasFile() ?? new PersonasFileModel());
        _providers = store.LoadProvidersFile() ?? new ProvidersFileModel();
        _profile = MemoryProfileExtractor.Normalize(store.LoadMemoryProfile());
        _intimacy = new IntimacyEngine(store.LoadIntimacy() ?? IntimacyState.Defaults);
        _lastDiaryDate = store.LoadDiaryLastGenerated();
        _imageProvider = BuildImageProvider(_providers);
        _interaction = new InteractionEngine(
            new InteractionEngineState(null, null),
            _settings.Ai.InteractionFrequency,
            _settings.Ai.ScreenAwareness);
        _interaction.SetEnabled(_settings.Ai.ActiveInteraction);
        _tickTimer = new System.Threading.Timer(Tick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public PersonasFileModel Personas => _personas;
    public ProvidersFileModel Providers => _providers;
    public bool AiEnabled => _settings.Ai.Enabled;

    /// <summary>设置变更（App 保存后调用）：总开关切换启停 Agent；其余同步配置。</summary>
    public void ApplySettings(AppSettings settings)
    {
        var shouldRun = settings.Ai.Enabled;
        var running = _agent is not null; // 以实际进程状态为准（启动时构造已读到旧设置）
        _settings = settings;
        if (shouldRun) _restartFailures = 0; // 设置变更重置看门狗计数
        _interaction.SetEnabled(settings.Ai.ActiveInteraction);
        _interaction.UpdateFrequency(settings.Ai.InteractionFrequency);
        _interaction.UpdateScreenAwareness(settings.Ai.ScreenAwareness);
        _chatWindow.TtsEnabled = settings.Ai.TtsEnabled; // 语音开关同步到对话窗按钮
        _modeService.SetMode(settings.Ai.OutputMode switch
        {
            "danmaku" => OutputMode.Danmaku,
            "chat" => OutputMode.Chat,
            "bubble" => OutputMode.Bubble,
            _ => OutputMode.Silent,
        });
        if (shouldRun && !running) StartAgent();
        else if (!shouldRun && running) StopAgent();
        else if (shouldRun) PushConfig(); // 分析/模式/人格/模型变化同步
        RebuildChatPipeline();
    }

    public void ApplyPersonas(PersonasFileModel personas)
    {
        _personas = PersonasFileModel.Normalize(personas);
        _store.SavePersonasFile(_personas);
        RebuildChatPipeline(); // 人格切换立即生效（下一轮请求）
        if (_settings.Ai.Enabled) PushConfig();
    }

    /// <summary>
    /// 初始化引导完成（称呼 + 人格，App 启动 / 设置页开启 AI 两处触发共用此入口）：
    /// 人格落盘 → 称呼写入画像 → Onboarded 标记。保存逻辑收敛一处，调用方不重复实现。
    /// </summary>
    public void CompleteOnboarding(string callName, string personaId)
    {
        var personas = PersonasFileModel.Normalize(_personas);
        personas.SelectedId = personaId;
        ApplyPersonas(personas);
        SetCallName(callName);
        _settings = AppSettings.Normalize(_settings) with { Ai = _settings.Ai with { Onboarded = true } };
        _store.SaveSettings(_settings);
    }

    /// <summary>设置称呼（更新记忆画像并落盘；记忆开关关 = 只更新内存画像）。</summary>
    public void SetCallName(string callName)
    {
        _profile = _profile with { CallName = callName.Trim() };
        if (_settings.Ai.MemoryEnabled) _store.SaveMemoryProfile(_profile);
    }

    public void ApplyProviders(ProvidersFileModel providers)
    {
        _providers = ProvidersFileModel.Normalize(providers);
        _store.SaveProvidersFile(_providers);
        _imageProvider = BuildImageProvider(_providers); // 生图连接变更立即生效（总结图）
        RebuildChatPipeline();
        if (_settings.Ai.Enabled) PushConfig();
    }

    /// <summary>用户主动对话（任何模式下可用）：管道 → 输出到对话窗 + token 记账。</summary>
    public async Task SendChatAsync(string text, bool withScreenContext)
    {
        if (_pipeline is null)
        {
            OnUiThread(() => _chatWindow.AppendAssistantAsync("（未配置模型连接，请到设置 → AI 助手 → 模型连接）"));
            return;
        }
        try
        {
            // Phase 6：记忆注入（开关开 + 有画像）+ 亲密度语气指令（开关开）
            Func<string>? memoryInjector = _settings.Ai.MemoryEnabled
                ? () => MemoryProfileExtractor.Inject(_profile)
                : null;
            Func<string>? suffixFactory = _settings.Ai.IntimacyEnabled
                ? () => _intimacy.BuildIntimacyDirective()
                : null;
            var result = await _pipeline.RunAsync(text, _conversation.BuildContext(CurrentContextTokens()), withScreenContext,
                memoryInjector: memoryInjector,
                systemPromptSuffix: suffixFactory,
                maxTokens: CurrentMaxOutputTokens());
            if (!result.Ok)
            {
                OnUiThread(() => _chatWindow.AppendAssistantAsync(result.Error switch
                {
                    "empty" => "（消息不能为空）",
                    "too-long" => "（消息太长了）",
                    _ => "（出错了）",
                }));
                return;
            }
            // L1 会话窗口维护：成功后追加本轮（预算裁剪与 L2 摘要由 ConversationMemory 负责）
            _conversation.Append(text, result.Text!);
            OnUiThread(() => _chatWindow.AppendAssistantAsync(result.Text!));
            if (result.TokensUsed > 0) RecordTokens(result.TokensUsed);
            RecordChatSuccess(text, result.TokensUsed);   // Phase 6：亲密度 + 画像更新
            Speak(result.Text!);                          // Phase 6：语音开关 + 对话模式
        }
        catch (ProviderException ex)
        {
            OnUiThread(() => _chatWindow.AppendAssistantAsync(ex.Code switch
            {
                "auth" => "（API Key 无效，请检查模型连接）",
                "timeout" => "（模型响应超时了，让我歇口气~）",
                _ => "（模型连接出错了）",
            }));
        }
        catch (Exception)
        {
            OnUiThread(() => _chatWindow.AppendAssistantAsync("（出错了，请稍后再试）"));
        }
    }

    /// <summary>打开对话窗（用户主动对话入口，任何模式可用）。</summary>
    public void EnsureChatWindow() => OnUiThread(_chatWindow.Show);

    // ---- Agent 生命周期 ----

    /// <summary>UI 线程封送（事件接收循环在线程池，WPF 窗口必须 UI 线程操作）。</summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private static void DebugLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "desktoppet-ai.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [T{Thread.CurrentThread.ManagedThreadId}] {msg}" + System.Environment.NewLine);
        }
        catch (Exception) { }
    }

    private void StartAgent()
    {
        lock (_lock)
        {
            DebugLog($"StartAgent: enabled={_settings.Ai.Enabled} agent={_agent is not null} path={_agentHostPath} exists={File.Exists(_agentHostPath)}");
            if (_agent is not null || _shuttingDown) return;
            _lifeCts = new CancellationTokenSource();
            _agent = LaunchAgentProcess();
            DebugLog($"LaunchAgentProcess -> {(_agent is null ? "null" : "pid=" + _agent.Id)}");
            if (_agent is null)
            {
                OnUiThread(() => _chatWindow.AppendAssistantAsync("（Agent 进程启动失败）"));
                return;
            }
            _agent.EnableRaisingEvents = true;
            _agent.Exited += (_, _) => OnAgentExited();
        }
        _ = ConnectAndRunAsync(_lifeCts.Token); // 连接 + 接收循环（含重连）
    }

    private Process? LaunchAgentProcess()
    {
        if (!File.Exists(_agentHostPath)) return null;
        var psi = new ProcessStartInfo(_agentHostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_agentHostPath)!,
        };
        return Process.Start(psi);
    }

    private void StopAgent()
    {
        _lifeCts?.Cancel();
        _lifeCts?.Dispose();
        _lifeCts = null;
        try
        {
            _rpc?.SendAsync(new RpcMessage(RpcType.Shutdown, null), CancellationToken.None)
                .Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // 管道可能已断：直接杀
        }
        KillAgent();
        DisposeRpc();
    }

    private void KillAgent()
    {
        try
        {
            if (_agent is { HasExited: false })
            {
                _agent.Kill(entireProcessTree: true);
                _agent.WaitForExit(2000);
            }
        }
        catch (Exception)
        {
            // 进程已退出
        }
        _agent = null;
    }

    private void DisposeRpc()
    {
        lock (_lock)
        {
            var rpc = _rpc;
            _rpc = null;
            rpc?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
    }

    private void OnAgentExited()
    {
        // 看门狗：总开关仍开 → 退避后重启（指数退避 3s→10s→30s 封顶；
        // 连续 5 次失败停止，防崩溃风暴；设置变更时重置计数）
        if (_shuttingDown || !_settings.Ai.Enabled) return;
        var delaySeconds = Math.Min(30, 3 * (1 << Math.Min(3, _restartFailures)));
        _restartFailures++;
        DebugLog($"agent exited, restart in {delaySeconds}s (failures={_restartFailures})");
        Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
        lock (_lock)
        {
            if (_shuttingDown || !_settings.Ai.Enabled || _agent is not null) return;
            if (_restartFailures > 5)
            {
                DebugLog("watchdog: too many failures, stopping until settings change");
                return;
            }
            _agent = LaunchAgentProcess();
            if (_agent is null) return;
            _agent.EnableRaisingEvents = true;
            _agent.Exited += (_, _) => OnAgentExited();
        }
        _ = ConnectAndRunAsync(_lifeCts?.Token ?? CancellationToken.None);
    }

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        try
        {
            var rpc = new PipeRpcClient(AgentService_DefaultPipeName);
            await rpc.ConnectAsync(ct);
            lock (_lock) _rpc = rpc;
            DebugLog("pipe connected");
            // 握手
            var hello = await rpc.ReceiveAsync(ct);
            DebugLog("hello received: " + hello.Type);
            if (hello.Type != RpcType.Hello) return;
            await PushConfigAsync(rpc, ct);
            // 接收循环：事件推送
            while (!ct.IsCancellationRequested)
            {
                var msg = await rpc.ReceiveAsync(ct);
                if (msg.Type == RpcType.ScreenEvent && msg.Payload is { } p)
                {
                    DebugLog("screen event received");
                    OnAgentEvent(p);
                }
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
            DisposeRpc();
        }
    }

    private void PushConfig()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PushConfigAsync(_rpc, CancellationToken.None);
            }
            catch (Exception ex)
            {
                DebugLog("PushConfig failed: " + ex.Message); // 管道可能正被 StopAgent 关闭
            }
        });
    }

    private Task PushConfigAsync(PipeRpcClient? rpc, CancellationToken ct)
    {
        if (rpc is null || !_settings.Ai.Enabled) return Task.CompletedTask;
        var cfg = AgentConfigBuilder.Build(_settings, _personas, _providers);
        return rpc.SendAsync(new RpcMessage(RpcType.Config,
            JsonSerializer.SerializeToElement(cfg, JsonOpts)), ct);
    }

    private void OnAgentEvent(JsonElement payload)
    {
        try
        {

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
                ? "（看到你的屏幕有变化~）"
                : summary;
            DebugLog($"[p6] screen event kind={kind} summary={text}");
            OnUiThread(() => _modeService.RouteOutput(new AiOutput(text, FromAnalysis: true)));
        }
        catch (Exception ex)
        {
            DebugLog("OnAgentEvent error: " + ex);
        }
    }

    private void RebuildChatPipeline()
    {
        var provider = AgentConfigBuilder.SelectProvider(_providers, _settings.Ai.ProviderId);
        _chatScheduler?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _chatScheduler = null;
        _pipeline = null;
        if (!_settings.Ai.Enabled || provider is null) return;

        var model = new OpenAiCompatibleModelProvider(provider, _credentials);
        // 并发闸 3（架构 §3.3）：对话 P0 / 主动互动 P1 / 每日总结 P2 共用——多宠物并行独立请求不被串行化
        _chatScheduler = new ModelRequestScheduler(model, concurrency: 3);
        _pipeline = new ChatPipeline(_chatScheduler, () => _personas.ResolveSelected(), _eventLog);
    }

    private void RecordTokens(int tokens)
    {
        // 必须把 key 与 care 实例一起传给记账回调（App 侧不能再 LoadCare 找引用）
        var states = _store.LoadCare();
        var first = states.FirstOrDefault();
        if (first.Key is null) return;
        _recordTokens(first.Key, first.Value, tokens);
    }

    // ---- Phase 6：陪伴增强 ----

    /// <summary>对话成功后：亲密度记账（开关开）+ 画像更新（记忆开关开）+ 持久化。</summary>
    private void RecordChatSuccess(string userText, int tokensUsed)
    {
        try
        {
            if (_settings.Ai.IntimacyEnabled)
            {
                _intimacy.RecordConversation(tokensUsed, DateTime.Now);
                _store.SaveIntimacy(_intimacy.State);
            }
            if (_settings.Ai.MemoryEnabled)
            {
                _profile = MergeProfile(_profile, userText);
                // L2 会话摘要合并进 L3 画像（“总结存记忆”：超预算压缩的会话内容落画像，不丢）
                if (_conversation.Summary.Length > 0)
                    _profile = _profile with { Summary = _conversation.Summary };
                _store.SaveMemoryProfile(_profile);
            }
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
            .Select(i => i.Id)
            .ToArray();
        if (petIds.Length == 0) return;

        // 多宠物分派：round-robin 竞争 1-2 只，或全员回应（设置页开关；同一事件各自表达 = 并行独立请求）
        var speakers = _dispatcher.SelectSpeakers(petIds, allReply: _settings.Ai.AllReply);
        _ = Task.Run(async () =>
        {
            var tasks = speakers.Select(petId => GenerateInteractionLineAsync(petId, trigger));
            var lines = await Task.WhenAll(tasks); // 并行独立请求：一次等待而非 N 倍延迟
            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                DebugLog($"[p6] route output: {line[..Math.Min(20, line.Length)]}");
                OnUiThread(() => _modeService.RouteOutput(new AiOutput(line!, FromAnalysis: true)));
            }
        });
    }

    /// <summary>单只宠物的主动互动台词（P1 优先级，8s 超时；失败跳过本轮）。
    /// 记忆注入（"隔天主动提起"）+ 亲密度指令 + 每宠物独立人格（PersonaId 覆盖全局）。</summary>
    private async Task<string?> GenerateInteractionLineAsync(string petId, InteractionTrigger trigger)
    {
        try
        {
            if (_chatScheduler is null) return null;
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
            var result = await _chatScheduler.EnqueueAsync(
                RequestPriority.Interactive, request, CancellationToken.None);
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
        var due = DailySummaryTrigger.GetDueDate(_lastDiaryDate, today);
        if (due is null) return;
        var dueDay = due.Value; // lambda 捕获不做 null 提升，先解包
        _lastDiaryDate = today; // 先标记：失败当日不重复尝试
        _store.SaveDiaryLastGenerated(today);
        _ = Task.Run(() => GenerateDailySummaryAsync(dueDay));
    }

    private async Task GenerateDailySummaryAsync(DateOnly day)
    {
        try
        {
            if (_chatScheduler is null) return;
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
            var result = await _chatScheduler.EnqueueAsync(
                RequestPriority.Background, request, CancellationToken.None);

            var text = result.Text ?? "";
            var txtPath = DiaryStore.TextPath(_store.DirectoryPath, day);
            Directory.CreateDirectory(Path.GetDirectoryName(txtPath)!);
            File.WriteAllText(txtPath, text);

            if (_settings.Ai.SummaryImage && _imageProvider is not null)
            {
                try
                {
                    var image = await _imageProvider.GenerateAsync(
                        new ImageGenRequest(ImagePromptBuilder.Build(text, petName)), CancellationToken.None);
                    File.WriteAllBytes(DiaryStore.ImagePath(_store.DirectoryPath, day), image.PngBytes);
                }
                catch (Exception ex)
                {
                    DebugLog("summary image failed (text kept): " + ex.Message); // 降级：文本照常
                }
            }

            OnUiThread(() => _modeService.RouteOutput(new AiOutput("今天的总结出炉啦~（日记已保存）", FromAnalysis: true)));
        }
        catch (Exception ex)
        {
            DebugLog("daily summary failed: " + ex.Message);
        }
    }

    /// <summary>朗读（语音开关 + 仅对话模式；Edge TTS MP3 → 临时文件 → MediaPlayer）。</summary>
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
        if (!_settings.Ai.TtsEnabled || _settings.Ai.OutputMode != "chat") return;
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var stream = await _tts.SynthesizeAsync(
                    text, new TtsVoice("zh-CN-XiaoxiaoNeural", "zh-CN"), CancellationToken.None);
                var bytes = ((MemoryStream)stream).ToArray();
                var tmp = Path.Combine(Path.GetTempPath(), $"desktoppet-tts-{Guid.NewGuid():N}.wav");
                File.WriteAllBytes(tmp, bytes);
                OnUiThread(() =>
                {
                    _ttsPlayer.MediaEnded += (_, _) =>
                    {
                        try { File.Delete(tmp); } catch (Exception) { }
                    };
                    _ttsPlayer.Open(new Uri(tmp));
                    _ttsPlayer.Play();
                });
            }
            catch (Exception ex)
            {
                DebugLog("tts failed: " + ex.Message); // 朗读失败不影响对话
            }
        });
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

    private static IImageProvider? BuildImageProvider(ProvidersFileModel providers)
    {
        if (providers.Image is null) return null;
        try
        {
            // 生图超时 120s：云端 T2I（DALL·E/agnes 等）通常需 20-60s，默认 30s 会误超时。
            return new OpenAiCompatibleImageProvider(
                providers.Image, new WindowsCredentialStore(), timeout: TimeSpan.FromSeconds(120));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _shuttingDown = true;
        _tickTimer?.Dispose();
        _tickTimer = null;
        _ttsPlayer.Close();
        StopAgent();
        _chatScheduler?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _chatScheduler = null;
    }

    private const string AgentService_DefaultPipeName = "DesktopPet.Agent";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

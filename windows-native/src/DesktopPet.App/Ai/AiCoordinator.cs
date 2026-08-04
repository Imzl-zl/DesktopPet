using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopPet.App.Windows;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Care;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.PipeRpc;
using DesktopPet.Infra.Providers;

namespace DesktopPet.App.Ai;

/// <summary>
/// AI 编排器（Phase 5 接线核心）：
/// · AI 总开关：开 = 启 Agent 进程（PetAgent.exe）+ 管道连接 + 配置下发；关 = 停进程（无后台/无网络）
/// · 看门狗：Agent 崩溃自动重启（总开关仍开时）
/// · 分析事件 → ModeService 路由（弹幕/对话/静默）
/// · 用户对话在 App 进程直连 provider（架构 §4：不走管道），token 记账 → CareEngine
/// · 屏幕事件日志（App 侧维护，对话屏幕上下文用）
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
        _modeService.SetMode(settings.Ai.OutputMode switch
        {
            "danmaku" => OutputMode.Danmaku,
            "chat" => OutputMode.Chat,
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

    public void ApplyProviders(ProvidersFileModel providers)
    {
        _providers = ProvidersFileModel.Normalize(providers);
        _store.SaveProvidersFile(_providers);
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
            var result = await _pipeline.RunAsync(text, [], withScreenContext);
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
            OnUiThread(() => _chatWindow.AppendAssistantAsync(result.Text!));
            if (result.TokensUsed > 0) RecordTokens(result.TokensUsed);
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
        _chatScheduler = new ModelRequestScheduler(model, concurrency: 1);
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

    public void Dispose()
    {
        _shuttingDown = true;
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

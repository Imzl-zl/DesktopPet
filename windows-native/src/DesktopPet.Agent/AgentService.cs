using System.Text.Json;
using DesktopPet.Agent.Analysis;
using DesktopPet.Agent.Capture;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Scheduling;
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
    public const string DefaultPipeName = "DesktopPet.Agent";

    private readonly PipeRpcServer _server;
    private readonly IScreenCaptureSource _capture;
    private readonly ICredentialStore _credentials;
    private readonly TimeSpan _captureInterval;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly object _configLock = new();
    private AgentConfig _config = AgentConfig.Defaults;
    private AnalysisEngine? _engine;
    private CancellationTokenSource? _engineCts;
    private Task? _engineTask;

    public AgentService(
        string pipeName,
        IScreenCaptureSource capture,
        ICredentialStore credentials,
        TimeSpan? captureInterval = null)
    {
        _server = new PipeRpcServer(pipeName);
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _captureInterval = captureInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>运行直到 Shutdown 或 ct 取消。阻塞调用。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await _server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            await _server.SendAsync(new RpcMessage(RpcType.Hello,
                JsonSerializer.SerializeToElement(new { agent = "DesktopPet.Agent", version = 1 })), ct).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            var receiveLoop = Task.Run(() => ReceiveLoopAsync(linked.Token), CancellationToken.None);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常退出路径：ct 或 Shutdown
            }
            try { await receiveLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
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
                return; // 对端断连：等下一次连接（简化：本轮退出）
            }

            switch (msg.Type)
            {
                case RpcType.Config:
                    ApplyConfig(msg.Payload);
                    break;
                case RpcType.Ping:
                    await SafeSendAsync(new RpcMessage(RpcType.Pong, null), ct).ConfigureAwait(false);
                    break;
                case RpcType.Shutdown:
                    _shutdown.Cancel();
                    return;
            }
        }
    }

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private void ApplyConfig(JsonElement? payload)
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

        lock (_configLock)
        {
            _config = cfg;
            AgentLog($"ApplyConfig: screenAnalysis={cfg.ScreenAnalysis} provider={cfg.ProviderBaseUrl} interval={cfg.MinAnalysisIntervalSeconds}");
            RebuildEngineLocked();
        }
    }

    private void RebuildEngineLocked()
    {
        _engineCts?.Cancel();
        _engineCts?.Dispose();
        _engineCts = null;

        IModelProvider? model = null;
        if (!string.IsNullOrEmpty(_config.ProviderBaseUrl) && !string.IsNullOrEmpty(_config.ProviderModel))
        {
            var pc = new ProviderConfig(
                Id: "agent-analysis",
                Name: "Agent 分析模型",
                BaseUrl: _config.ProviderBaseUrl,
                ApiKeyRef: _config.ProviderApiKeyRef ?? "",
                ModelName: _config.ProviderModel,
                Capabilities: ModelCapabilities.Chat | ModelCapabilities.Vision,
                IsDefault: false,
                ReasoningEffort: _config.ProviderReasoningEffort); // 推理模型必须关闭思考，否则 token 全被消耗
            model = new OpenAiCompatibleModelProvider(pc, _credentials);
        }

        _engine = new AnalysisEngine(_capture, model, () => CurrentConfig(), _captureInterval);
        _engine.EventRaised += e => _ = PushEventAsync(e);
        _engineCts = new CancellationTokenSource();
        _engineTask = _engine.RunAsync(_engineCts.Token);
    }

    private AgentConfig CurrentConfig()
    {
        lock (_configLock) return _config;
    }

    internal static void AgentLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "desktoppet-agent.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}" + Environment.NewLine);
        }
        catch (Exception) { }
    }

    private async Task PushEventAsync(ScreenEvent e)
    {
        AgentLog($"push event: kind={e.Kind} summary={e.Summary}");
        var payload = JsonSerializer.SerializeToElement(new ScreenEventPayload(
            e.Timestamp.ToString("o"), e.Kind.ToString(), e.Summary, e.FrameHash), ConfigJsonOptions);
        await SafeSendAsync(new RpcMessage(RpcType.ScreenEvent, payload), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SafeSendAsync(RpcMessage msg, CancellationToken ct)
    {
        try
        {
            await _server.SendAsync(msg, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 对端断连：事件丢弃（App 重启会重连新 Agent）
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        lock (_configLock)
        {
            _engineCts?.Cancel();
            _engineCts?.Dispose();
            _engineCts = null;
        }
        if (_engineTask is not null)
        {
            try { await _engineTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await _server.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}

/// <summary>屏幕事件管道载荷（IPC 契约：明确字段名，枚举转字符串）。</summary>
public sealed record ScreenEventPayload(string Timestamp, string Kind, string Summary, ulong FrameHash);

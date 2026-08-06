using System.Text.Json;
using DesktopPet.Agent.Capture;
using DesktopPet.Infra.PipeRpc;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Agent.Tests;

/// <summary>
/// Phase 5f 冒烟：AgentService 端到端——录制帧序列驱动分析，事件经命名管道推回 client。
/// </summary>
public class AgentServiceTests
{
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

    private static string PipeName() => "DesktopPet.Agent.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task WrongExpectedProcessId_IsRejectedWithoutHello()
    {
        var pipe = PipeName();
        using var serviceCts = new CancellationTokenSource();
        await using var service = new AgentService(
            pipe,
            new OfflineFrameSource([]),
            new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId + 100_000);
        var runTask = service.RunAsync(serviceCts.Token);

        await using var rejected = new PipeRpcClient(pipe);
        await rejected.ConnectAsync(CancellationToken.None);
        using var rejectedCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<IOException>(() => rejected.ReceiveAsync(rejectedCts.Token));

        serviceCts.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task UnauthorizedFirstClient_IsRejectedBeforeHello_ThenServerReaccepts()
    {
        var pipe = PipeName();
        var authorizationAttempts = 0;
        await using var service = new AgentService(
            pipe,
            new OfflineFrameSource([]),
            new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId,
            clientAuthorizer: _ => Interlocked.Increment(ref authorizationAttempts) > 1);
        var runTask = service.RunAsync(CancellationToken.None);

        await using (var rejected = new PipeRpcClient(pipe))
        {
            await rejected.ConnectAsync(CancellationToken.None);
            using var rejectedCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<IOException>(() => rejected.ReceiveAsync(rejectedCts.Token));
        }

        await using var accepted = new PipeRpcClient(pipe);
        await accepted.ConnectAsync(CancellationToken.None);
        var hello = await accepted.ReceiveAsync(CancellationToken.None);
        Assert.Equal(RpcType.Hello, hello.Type);
        Assert.Equal(2, Volatile.Read(ref authorizationAttempts));

        await accepted.SendAsync(new RpcMessage(RpcType.Shutdown, null), CancellationToken.None);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HeartbeatTimeout_StopsCaptureBeforeServiceReturns()
    {
        var pipe = PipeName();
        var created = 0;
        var inner = new DisposableCaptureSource();
        using var source = new SwitchableScreenCaptureSource(() =>
        {
            Interlocked.Increment(ref created);
            return inner;
        });
        await using var service = new AgentService(
            pipe,
            source,
            new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId,
            captureInterval: TimeSpan.FromMilliseconds(10),
            heartbeatTimeout: TimeSpan.FromMilliseconds(150));
        var runTask = service.RunAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None);
        await client.SendAsync(new RpcMessage(RpcType.Config, ConfigPayload(screenAnalysis: true)), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref created) == 1);

        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public async Task Ping_RenewsHeartbeatLeaseUntilExplicitShutdown()
    {
        var pipe = PipeName();
        await using var service = new AgentService(
            pipe,
            new OfflineFrameSource([]),
            new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId,
            heartbeatTimeout: TimeSpan.FromMilliseconds(180));
        var runTask = service.RunAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(60);
            await client.SendAsync(new RpcMessage(RpcType.Ping, null), CancellationToken.None);
            Assert.Equal(RpcType.Pong, (await client.ReceiveAsync(CancellationToken.None)).Type);
        }
        Assert.False(runTask.IsCompleted);

        await client.SendAsync(new RpcMessage(RpcType.Shutdown, null), CancellationToken.None);
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EndToEnd_ConfigEnablesAnalysis_EventsFlowOverPipe()
    {
        var pipe = PipeName();
        var source = new OfflineFrameSource([Frame(100), GradientFrame()]);
        await using var service = new AgentService(pipe, source, new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId,
            captureInterval: TimeSpan.FromMilliseconds(10));
        var runTask = service.RunAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);

        // 握手
        var hello = await client.ReceiveAsync(CancellationToken.None);
        Assert.Equal(RpcType.Hello, hello.Type);

        // 下发配置：分析开、无模型（降级事件）
        var cfg = JsonSerializer.SerializeToElement(new
        {
            screenAnalysis = true,
            analysisPersonaPrompt = (string?)null,
            providerBaseUrl = (string?)null,
            providerModel = (string?)null,
            providerApiKeyRef = (string?)null,
            minAnalysisIntervalSeconds = 0,
            revision = 42L,
        });
        await client.SendAsync(new RpcMessage(RpcType.Config, cfg), CancellationToken.None);

        // 引擎启动：首帧基准 → 变化帧 → 事件推送
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await client.ReceiveAsync(cts.Token);
        Assert.Equal(RpcType.ScreenEvent, received.Type);
        Assert.Equal("Unknown", received.Payload!.Value.GetProperty("kind").GetString());
        Assert.Equal(42L, received.Payload!.Value.GetProperty("configRevision").GetInt64());
    }

    [Fact]
    public async Task EndToEnd_AnalysisDisabled_NoEvents()
    {
        var pipe = PipeName();
        var source = new OfflineFrameSource([Frame(100), GradientFrame()]);
        await using var service = new AgentService(pipe, source, new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId,
            captureInterval: TimeSpan.FromMilliseconds(10));
        var runTask = service.RunAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None); // Hello

        var cfg = JsonSerializer.SerializeToElement(new
        {
            screenAnalysis = false, // 静默：不截屏
            analysisPersonaPrompt = (string?)null,
            providerBaseUrl = (string?)null,
            providerModel = (string?)null,
            providerApiKeyRef = (string?)null,
            minAnalysisIntervalSeconds = 5,
        });
        await client.SendAsync(new RpcMessage(RpcType.Config, cfg), CancellationToken.None);

        // Ping/Pong 正常，但 300ms 内无任何事件推送
        await client.SendAsync(new RpcMessage(RpcType.Ping, null), CancellationToken.None);
        var pong = await client.ReceiveAsync(CancellationToken.None);
        Assert.Equal(RpcType.Pong, pong.Type);

        using var cts = new CancellationTokenSource(300);
        var ex = await Record.ExceptionAsync(() => client.ReceiveAsync(cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex); // 超时无事件
    }

    [Fact]
    public async Task Config_DisablesAnActiveSwitchableCaptureSource()
    {
        var pipe = PipeName();
        var created = 0;
        var inner = new DisposableCaptureSource();
        using var source = new SwitchableScreenCaptureSource(() =>
        {
            Interlocked.Increment(ref created);
            return inner;
        });
        await using var service = new AgentService(pipe, source, new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId,
            captureInterval: TimeSpan.FromMilliseconds(10));
        var runTask = service.RunAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None); // Hello

        await client.SendAsync(new RpcMessage(RpcType.Config, ConfigPayload(screenAnalysis: true)), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref created) == 1);

        await client.SendAsync(new RpcMessage(RpcType.Config, ConfigPayload(screenAnalysis: false)), CancellationToken.None);
        await WaitUntilAsync(() => inner.DisposeCount == 1);

        await client.SendAsync(new RpcMessage(RpcType.Shutdown, null), CancellationToken.None);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Config_Rebuild_WaitsForPriorEngineBeforeStartingReplacement()
    {
        var pipe = PipeName();
        var source = new SlowCancellationCaptureSource();
        await using var service = new AgentService(pipe, source, new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId,
            captureInterval: TimeSpan.FromMilliseconds(10));
        var runTask = service.RunAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None);

        await client.SendAsync(new RpcMessage(RpcType.Config, ConfigPayload(screenAnalysis: true)), CancellationToken.None);
        await WaitUntilAsync(() => source.CallCount >= 1);
        await client.SendAsync(new RpcMessage(RpcType.Config, ConfigPayload(screenAnalysis: true)), CancellationToken.None);
        await WaitUntilAsync(() => source.CallCount >= 2);

        Assert.Equal(1, source.MaxInFlight);

        await client.SendAsync(new RpcMessage(RpcType.Shutdown, null), CancellationToken.None);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static JsonElement ConfigPayload(bool screenAnalysis)
        => JsonSerializer.SerializeToElement(new
        {
            screenAnalysis,
            analysisPersonaPrompt = (string?)null,
            providerBaseUrl = (string?)null,
            providerModel = (string?)null,
            providerApiKeyRef = (string?)null,
            minAnalysisIntervalSeconds = 0,
        });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class SlowCancellationCaptureSource : IScreenCaptureSource
    {
        private int _callCount;
        private int _inFlight;
        private int _maxInFlight;

        public int CallCount => Volatile.Read(ref _callCount);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            var inFlight = Interlocked.Increment(ref _inFlight);
            UpdateMax(ref _maxInFlight, inFlight);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(150);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void UpdateMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }

    private sealed class DisposableCaptureSource : IScreenCaptureSource, IDisposable
    {
        public int DisposeCount { get; private set; }

        public Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
            => Task.FromResult<CapturedFrame?>(null);

        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public async Task DisposeAsync_WaitsForRunLoopThatIsWaitingForFirstClient()
    {
        var service = new AgentService(
            PipeName(), new OfflineFrameSource([]), new InMemoryCredentialStore(), Environment.ProcessId);
        var runTask = service.RunAsync(CancellationToken.None);
        await Task.Delay(30);

        await service.DisposeAsync();

        Assert.True(runTask.IsCompleted);
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(runTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentForConcurrentCallers()
    {
        var source = new DisposableCaptureSource();
        var service = new AgentService(
            PipeName(), source, new InMemoryCredentialStore(), Environment.ProcessId);

        await Task.WhenAll(service.DisposeAsync().AsTask(), service.DisposeAsync().AsTask());

        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task Dispose_ReleasesDisposableCaptureSource()
    {
        var source = new DisposableCaptureSource();
        var service = new AgentService(
            PipeName(), source, new InMemoryCredentialStore(), Environment.ProcessId);

        await service.DisposeAsync();

        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task ClientDisconnect_StopsActiveCaptureBeforeRunReturns()
    {
        var pipe = PipeName();
        var created = 0;
        var inner = new DisposableCaptureSource();
        using var source = new SwitchableScreenCaptureSource(() =>
        {
            Interlocked.Increment(ref created);
            return inner;
        });
        await using var service = new AgentService(
            pipe,
            source,
            new InMemoryCredentialStore(),
            expectedClientProcessId: Environment.ProcessId,
            captureInterval: TimeSpan.FromMilliseconds(10));
        var runTask = service.RunAsync(CancellationToken.None);
        var client = new PipeRpcClient(pipe);

        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None);
        await client.SendAsync(new RpcMessage(RpcType.Config, ConfigPayload(screenAnalysis: true)), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref created) == 1);
        await client.DisposeAsync();

        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, inner.DisposeCount);
    }

    [Fact]
    public async Task EndToEnd_ClientDisconnect_TerminatesService()
    {
        var pipe = PipeName();
        await using var service = new AgentService(
            pipe, new OfflineFrameSource([]), new InMemoryCredentialStore(), Environment.ProcessId);
        var runTask = service.RunAsync(CancellationToken.None);
        var client = new PipeRpcClient(pipe);

        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None); // Hello
        await client.DisposeAsync();

        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EndToEnd_Shutdown_TerminatesService()
    {
        var pipe = PipeName();
        var source = new OfflineFrameSource([]);
        await using var service = new AgentService(
            pipe, source, new InMemoryCredentialStore(), Environment.ProcessId);
        var runTask = service.RunAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None); // Hello

        await client.SendAsync(new RpcMessage(RpcType.Shutdown, null), CancellationToken.None);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5)); // 服务优雅退出
    }
}

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
    public async Task EndToEnd_ConfigEnablesAnalysis_EventsFlowOverPipe()
    {
        var pipe = PipeName();
        var source = new OfflineFrameSource([Frame(100), GradientFrame()]);
        await using var service = new AgentService(pipe, source, new InMemoryCredentialStore(),
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
        });
        await client.SendAsync(new RpcMessage(RpcType.Config, cfg), CancellationToken.None);

        // 引擎启动：首帧基准 → 变化帧 → 事件推送
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await client.ReceiveAsync(cts.Token);
        Assert.Equal(RpcType.ScreenEvent, received.Type);
        Assert.Equal("Unknown", received.Payload!.Value.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task EndToEnd_AnalysisDisabled_NoEvents()
    {
        var pipe = PipeName();
        var source = new OfflineFrameSource([Frame(100), GradientFrame()]);
        await using var service = new AgentService(pipe, source, new InMemoryCredentialStore(),
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
    public async Task EndToEnd_Shutdown_TerminatesService()
    {
        var pipe = PipeName();
        var source = new OfflineFrameSource([]);
        await using var service = new AgentService(pipe, source, new InMemoryCredentialStore());
        var runTask = service.RunAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        _ = await client.ReceiveAsync(CancellationToken.None); // Hello

        await client.SendAsync(new RpcMessage(RpcType.Shutdown, null), CancellationToken.None);
        await runTask.WaitAsync(TimeSpan.FromSeconds(5)); // 服务优雅退出
    }
}

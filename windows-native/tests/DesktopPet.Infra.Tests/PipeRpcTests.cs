using System.Text.Json;
using DesktopPet.Infra.PipeRpc;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// Phase 5f：命名管道 IPC（架构文档 §4.1）。长度前缀 JSON 帧；
/// App=client，Agent=server；配置下发 + 事件推送双向。
/// </summary>
public class PipeRpcTests
{
    private static string PipeName()
        => "DesktopPet.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Roundtrip_ClientHello_ServerReceives()
    {
        var pipe = PipeName();
        await using var server = new PipeRpcServer(pipe);
        var connectTask = server.WaitForConnectionAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        await connectTask;

        var payload = JsonSerializer.SerializeToElement(new { hello = "DesktopPet.App" });
        await client.SendAsync(new RpcMessage(RpcType.Hello, payload), CancellationToken.None);
        var received = await server.ReceiveAsync(CancellationToken.None);

        Assert.Equal(RpcType.Hello, received.Type);
        Assert.Equal("DesktopPet.App", received.Payload!.Value.GetProperty("hello").GetString());
    }

    [Fact]
    public async Task Roundtrip_ServerPushesEvent_ClientReceives()
    {
        var pipe = PipeName();
        await using var server = new PipeRpcServer(pipe);
        var connectTask = server.WaitForConnectionAsync(CancellationToken.None);

        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        await connectTask;

        var evt = JsonSerializer.SerializeToElement(new
        {
            timestamp = "2026-08-05T10:00:00",
            kind = "Coding",
            summary = "正在写代码",
            frameHash = 123ul,
        });
        await server.SendAsync(new RpcMessage(RpcType.ScreenEvent, evt), CancellationToken.None);
        var received = await client.ReceiveAsync(CancellationToken.None);

        Assert.Equal(RpcType.ScreenEvent, received.Type);
        Assert.Equal("Coding", received.Payload!.Value.GetProperty("kind").GetString());
        Assert.Equal(123ul, received.Payload!.Value.GetProperty("frameHash").GetUInt64());
    }

    [Fact]
    public async Task Roundtrip_LargePayload_SurvivesFraming()
    {
        // 截图 base64（~256KB）验证长度前缀帧在大消息下不串帧
        var pipe = PipeName();
        await using var server = new PipeRpcServer(pipe);
        var connectTask = server.WaitForConnectionAsync(CancellationToken.None);
        await using var client = new PipeRpcClient(pipe);
        await client.ConnectAsync(CancellationToken.None);
        await connectTask;

        var big = new string('A', 256 * 1024);
        var payload = JsonSerializer.SerializeToElement(new { image = big });
        // 消息 > 管道缓冲（64KB）：必须对端先读、本端边写边消费，否则写完再读必死锁。
        var receiveTask = server.ReceiveAsync(CancellationToken.None);
        await client.SendAsync(new RpcMessage(RpcType.Config, payload), CancellationToken.None);

        var received = await receiveTask;
        Assert.Equal(big, received.Payload!.Value.GetProperty("image").GetString());

        // 再收一条确认帧边界未错位
        await client.SendAsync(new RpcMessage(RpcType.Ping, null), CancellationToken.None);
        var ping = await server.ReceiveAsync(CancellationToken.None);
        Assert.Equal(RpcType.Ping, ping.Type);
    }

    [Fact]
    public async Task Server_ReportsConnectedClientProcessId()
    {
        var pipe = PipeName();
        await using var server = new PipeRpcServer(pipe);
        var connectTask = server.WaitForConnectionAsync(CancellationToken.None);
        await using var client = new PipeRpcClient(pipe);

        await client.ConnectAsync(CancellationToken.None);
        await connectTask;

        Assert.Equal(Environment.ProcessId, server.GetConnectedClientProcessId());
    }

    [Fact]
    public async Task ClientDisconnect_ServerReceiveThrows()
    {
        var pipe = PipeName();
        await using var server = new PipeRpcServer(pipe);
        var connectTask = server.WaitForConnectionAsync(CancellationToken.None);

        await using (var client = new PipeRpcClient(pipe))
        {
            await client.ConnectAsync(CancellationToken.None);
            await connectTask;
        } // client 释放 → 断连

        await Assert.ThrowsAnyAsync<IOException>(
            () => server.ReceiveAsync(CancellationToken.None));
    }
}

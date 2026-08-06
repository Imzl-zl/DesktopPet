using System.IO;
using DesktopPet.App.Ai;

namespace DesktopPet.App.Tests;

public class AgentHeartbeatMonitorTests
{
    [Fact]
    public async Task RunAsync_PongDuringBlockedSendExtendsTheDeadline()
    {
        var monitor = new AgentHeartbeatMonitor(
            pingInterval: TimeSpan.FromMilliseconds(20),
            pongTimeout: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(140));
        var runTask = monitor.RunAsync(
            ct => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            cts.Token);

        await Task.Delay(50);
        monitor.RecordPong();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public async Task RunAsync_PongDeadlineExpiresEvenWhenPingSendIsBlocked()
    {
        var monitor = new AgentHeartbeatMonitor(
            pingInterval: TimeSpan.FromMilliseconds(20),
            pongTimeout: TimeSpan.FromMilliseconds(80));

        var error = await Assert.ThrowsAsync<IOException>(() => monitor.RunAsync(
                ct => Task.Delay(Timeout.InfiniteTimeSpan, ct),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(500)));

        Assert.Contains("Pong", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenAgentStopsReturningPong()
    {
        var monitor = new AgentHeartbeatMonitor(
            pingInterval: TimeSpan.FromMilliseconds(20),
            pongTimeout: TimeSpan.FromMilliseconds(80));
        var sends = 0;

        var error = await Assert.ThrowsAsync<IOException>(() => monitor.RunAsync(
            _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.CompletedTask;
            },
            CancellationToken.None));

        Assert.Contains("Pong", error.Message, StringComparison.Ordinal);
        Assert.True(sends >= 2);
    }
}

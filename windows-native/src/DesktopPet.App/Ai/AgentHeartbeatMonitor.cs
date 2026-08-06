using System.IO;
using DesktopPet.Core.Ai;

namespace DesktopPet.App.Ai;

internal sealed class AgentHeartbeatMonitor
{
    private readonly TimeSpan _pingInterval;
    private readonly TimeProvider _timeProvider;
    private readonly HeartbeatLease _pongLease;

    public AgentHeartbeatMonitor(
        TimeSpan pingInterval,
        TimeSpan pongTimeout,
        TimeProvider? timeProvider = null)
    {
        if (pingInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pingInterval));
        if (pongTimeout <= pingInterval) throw new ArgumentOutOfRangeException(nameof(pongTimeout));
        _pingInterval = pingInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pongLease = new HeartbeatLease(pongTimeout, _timeProvider);
    }

    public void RecordPong() => _pongLease.Renew();

    public async Task RunAsync(
        Func<CancellationToken, Task> sendPingAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sendPingAsync);
        while (!ct.IsCancellationRequested)
        {
            var remaining = _pongLease.Remaining;
            if (remaining == TimeSpan.Zero) throw new IOException("Agent Pong 心跳超时");

            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sendTask = sendPingAsync(sendCts.Token);
            var deadlineTask = Task.Delay(remaining, _timeProvider, ct);
            var completed = await Task.WhenAny(sendTask, deadlineTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, deadlineTask))
            {
                sendCts.Cancel();
                _ = ObserveCanceledSendAsync(sendTask);
                ct.ThrowIfCancellationRequested();
                if (!_pongLease.IsExpired) continue;
                throw new IOException("Agent Pong 心跳超时");
            }
            await sendTask.ConfigureAwait(false);

            var delay = _pongLease.Remaining < _pingInterval
                ? _pongLease.Remaining
                : _pingInterval;
            if (delay == TimeSpan.Zero) throw new IOException("Agent Pong 心跳超时");
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private static async Task ObserveCanceledSendAsync(Task sendTask)
    {
        try { await sendTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Canceled heartbeat send failed: {ex}"); }
    }
}

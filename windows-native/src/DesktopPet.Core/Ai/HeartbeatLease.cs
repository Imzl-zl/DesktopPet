namespace DesktopPet.Core.Ai;

/// <summary>Monotonic renewable lease used to stop Agent work when PetApp is no longer healthy.</summary>
public sealed class HeartbeatLease
{
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;
    private long _lastRenewedTimestamp;

    public HeartbeatLease(TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastRenewedTimestamp = _timeProvider.GetTimestamp();
    }

    public bool IsExpired => Remaining == TimeSpan.Zero;

    public TimeSpan Remaining
    {
        get
        {
            var renewedAt = Interlocked.Read(ref _lastRenewedTimestamp);
            var elapsed = _timeProvider.GetElapsedTime(renewedAt, _timeProvider.GetTimestamp());
            return elapsed >= _timeout ? TimeSpan.Zero : _timeout - elapsed;
        }
    }

    public void Renew()
        => Interlocked.Exchange(ref _lastRenewedTimestamp, _timeProvider.GetTimestamp());
}

namespace DesktopPet.Agent.Capture;

/// <summary>Monotonic gate for the expensive GPU surface copy.</summary>
public sealed class CaptureCadenceGate
{
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private TimeSpan _interval;
    private long _nextAllowedTimestamp;

    public CaptureCadenceGate(TimeSpan interval, TimeProvider? timeProvider = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Interval
    {
        get { lock (_lock) return _interval; }
    }

    public void UpdateInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_lock)
        {
            _interval = interval;
            _nextAllowedTimestamp = 0;
        }
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            if (_nextAllowedTimestamp != 0
                && _timeProvider.GetElapsedTime(_nextAllowedTimestamp, now) < TimeSpan.Zero)
            {
                return false;
            }

            var ticks = Math.Max(
                1,
                (long)Math.Ceiling(_interval.TotalSeconds * _timeProvider.TimestampFrequency));
            _nextAllowedTimestamp = now + ticks;
            return true;
        }
    }
}

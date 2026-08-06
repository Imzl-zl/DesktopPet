using DesktopPet.Core.Ai;

namespace DesktopPet.Core.Tests;

public class HeartbeatLeaseTests
{
    [Fact]
    public void Renew_ExtendsDeadlineUsingMonotonicTime()
    {
        var time = new ManualTimeProvider();
        var lease = new HeartbeatLease(TimeSpan.FromSeconds(10), time);

        time.Advance(TimeSpan.FromSeconds(8));
        Assert.False(lease.IsExpired);
        lease.Renew();
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.False(lease.IsExpired);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(lease.IsExpired);
    }

    [Fact]
    public void Remaining_NeverReturnsNegativeDuration()
    {
        var time = new ManualTimeProvider();
        var lease = new HeartbeatLease(TimeSpan.FromSeconds(1), time);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.Zero, lease.Remaining);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan duration)
            => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}

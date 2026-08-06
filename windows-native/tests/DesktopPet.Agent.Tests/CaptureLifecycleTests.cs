using DesktopPet.Agent.Capture;

namespace DesktopPet.Agent.Tests;

public sealed class CaptureCadenceGateTests
{
    [Fact]
    public void Gate_UsesMonotonicCadence_AndRuntimeUpdateResetsDeadline()
    {
        var time = new ManualTimeProvider();
        var gate = new CaptureCadenceGate(TimeSpan.FromSeconds(3), time);

        Assert.True(gate.TryAcquire());
        Assert.False(gate.TryAcquire());
        time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.False(gate.TryAcquire());
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(gate.TryAcquire());

        gate.UpdateInterval(TimeSpan.FromSeconds(30));
        Assert.True(gate.TryAcquire());
        Assert.False(gate.TryAcquire());
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

public sealed class RecoveringScreenCaptureSourceTests
{
    [Fact]
    public async Task FaultedSource_IsDisposedOnce_AndRecreatedAfterBoundedBackoff()
    {
        var time = new ManualTimeProvider();
        var first = new FaultingCaptureSource();
        var second = new FaultingCaptureSource();
        var calls = 0;
        using var source = new SwitchableScreenCaptureSource(
            () => ++calls == 1 ? first : second,
            timeProvider: time);
        source.SetEnabled(true);

        Assert.NotNull(await source.CaptureAsync(CancellationToken.None));
        first.TriggerFault(new InvalidOperationException("device removed"));
        await Assert.ThrowsAsync<CaptureSourceUnavailableException>(
            () => source.CaptureAsync(CancellationToken.None));
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await source.CaptureAsync(CancellationToken.None));
        Assert.Equal(2, calls);
        Assert.Equal(0, second.DisposeCount);

        source.SetEnabled(false);
        Assert.Equal(1, second.DisposeCount);
        source.Dispose();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    private sealed class FaultingCaptureSource :
        IScreenCaptureSource,
        ICaptureFaultSource,
        ICaptureCadenceSource,
        IDisposable
    {
        public int DisposeCount { get; private set; }
        public event Action<Exception>? Faulted;

        public Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
            => Task.FromResult<CapturedFrame?>(new CapturedFrame(1, 1, [1]));

        public void SetCaptureInterval(TimeSpan interval) { }

        public void TriggerFault(Exception exception) => Faulted?.Invoke(exception);

        public void Dispose() => DisposeCount++;
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

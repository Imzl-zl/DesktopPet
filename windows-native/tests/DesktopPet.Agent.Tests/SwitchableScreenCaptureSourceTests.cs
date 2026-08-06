using DesktopPet.Agent.Capture;

namespace DesktopPet.Agent.Tests;

public sealed class SwitchableScreenCaptureSourceTests
{
    [Fact]
    public async Task DisabledSource_DoesNotCreateCapture_AndReleasesItWhenDisabled()
    {
        var factoryCalls = 0;
        var inner = new TrackingCaptureSource();
        using var source = new SwitchableScreenCaptureSource(() =>
        {
            factoryCalls++;
            return inner;
        });

        Assert.Null(await source.CaptureAsync(CancellationToken.None));
        Assert.Equal(0, factoryCalls);

        source.SetEnabled(true);
        Assert.NotNull(await source.CaptureAsync(CancellationToken.None));
        Assert.Equal(1, factoryCalls);

        source.SetEnabled(false);
        Assert.Equal(1, inner.DisposeCount);
        Assert.Null(await source.CaptureAsync(CancellationToken.None));
        Assert.Equal(1, factoryCalls);
    }

    private sealed class TrackingCaptureSource : IScreenCaptureSource, IDisposable
    {
        public int DisposeCount { get; private set; }

        public Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
            => Task.FromResult<CapturedFrame?>(new CapturedFrame(1, 1, new byte[] { 1 }));

        public void Dispose() => DisposeCount++;
    }
}

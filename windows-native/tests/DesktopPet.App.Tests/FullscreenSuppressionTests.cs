using DesktopPet.App.Fullscreen;

namespace DesktopPet.App.Tests;

public sealed class FullscreenSuppressionTests
{
    [Fact]
    public void ExactBorderlessWindow_IsFullscreenOnNegativeCoordinateMonitor()
    {
        var monitor = new ScreenRect(-1920, 0, 0, 1080);
        var window = new ScreenRect(-1920, 0, 0, 1080);

        Assert.True(FullscreenBoundsEvaluator.CoversMonitor(window, monitor, maximized: false));
    }

    [Fact]
    public void PartialWindow_IsNotFullscreen_ButMaximizedWindowIsSuppressed()
    {
        var monitor = new ScreenRect(0, 0, 2560, 1440);
        var workArea = new ScreenRect(0, 0, 2560, 1400);

        Assert.False(FullscreenBoundsEvaluator.CoversMonitor(workArea, monitor, maximized: false));
        Assert.True(FullscreenBoundsEvaluator.CoversMonitor(workArea, monitor, maximized: true));
    }

    [Fact]
    public void DpiTolerance_AllowsSmallPhysicalRoundingDifference()
    {
        var monitor = new ScreenRect(2560, 0, 4480, 1440);
        var window = new ScreenRect(2561, 0, 4479, 1440);

        Assert.True(FullscreenBoundsEvaluator.CoversMonitor(window, monitor, maximized: false, tolerance: 2));
        Assert.False(FullscreenBoundsEvaluator.CoversMonitor(window, monitor, maximized: false, tolerance: 0));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void OutputPolicy_SuppressesOnlyProactiveDelivery(
        bool proactive,
        bool monitored,
        bool detected,
        bool expected)
        => Assert.Equal(expected, FullscreenOutputPolicy.ShouldSuppress(proactive, monitored, detected));

    [Fact]
    public void SuppressionMonitor_PublishesOnlyTransitionsAndStops()
    {
        var detected = false;
        var published = new List<bool>();
        using var monitor = new FullscreenSuppressionMonitor(
            () => detected,
            value => published.Add(value));

        monitor.Poll();
        monitor.Poll();
        detected = true;
        monitor.Poll();
        monitor.Poll();
        monitor.Stop();

        Assert.Equal([false, true], published);
        Assert.False(monitor.IsRunning);
    }
}

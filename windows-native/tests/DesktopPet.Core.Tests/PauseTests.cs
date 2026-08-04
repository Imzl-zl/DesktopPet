using DesktopPet.Core.Pets;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 1:1 移植自 windows/src/roam/pause.test.ts（vitest）。
/// </summary>
public class PauseTests
{
    [Fact]
    public void PreservesTheEstablishedDefaultsWhenPersistedValuesAreAbsent()
    {
        var range = Pause.NormalizeWanderPauseRange(null, null);

        Assert.Equal(Pause.DefaultWanderPauseMinMs, range.MinMs);
        Assert.Equal(Pause.DefaultWanderPauseMaxMs, range.MaxMs);
    }

    [Fact]
    public void OrdersAReversedRangeWithoutImposingAnArbitraryUpperLimit()
    {
        var reversed = Pause.NormalizeWanderPauseRange(9000, 1200);
        Assert.Equal(1200, reversed.MinMs);
        Assert.Equal(9000, reversed.MaxMs);

        var wide = Pause.NormalizeWanderPauseRange(1000, 120_000);
        Assert.Equal(1000, wide.MinMs);
        Assert.Equal(120_000, wide.MaxMs);
    }

    [Fact]
    public void SamplesInclusivelyFromTheNormalizedPauseRange()
    {
        var range = Pause.NormalizeWanderPauseRange(1200, 3500);

        Assert.Equal(1200, Pause.SampleWanderPauseMs(range, () => 0.0));
        Assert.Equal(3500, Pause.SampleWanderPauseMs(range, () => 1.0));
    }
}

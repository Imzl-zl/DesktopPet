using DesktopPet.Core.Roaming;

namespace DesktopPet.Core.Tests;

public sealed class RoamTimingTests
{
    [Fact]
    public void ActiveRoam_PreservesThirtyThreeFramesPerSecond()
    {
        Assert.Equal(30, RoamConstants.TickMs);
        Assert.Equal(0.03, RoamConstants.DtSec, precision: 3);
    }

    [Fact]
    public void ThrowDuration_RemainsNormalizedToTheReferenceFrameRate()
    {
        var position = new RoamPoint(1_000, 1_000);
        var physics = new RoamPhysics(() => position, next => position = next);
        var velocityX = 300d;
        var velocityY = 0d;
        var elapsedMs = 0;
        var bounds = new RoamRect(0, 0, 10_000, 10_000);

        while (physics.StepThrow(ref velocityX, ref velocityY, bounds, pet: null, elapsedMs))
        {
            elapsedMs += RoamConstants.TickMs;
        }

        Assert.InRange(elapsedMs, 850, 950);
    }
}

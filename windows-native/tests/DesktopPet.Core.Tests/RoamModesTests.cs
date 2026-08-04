using DesktopPet.Core.Pets;
using DesktopPet.Core.Roaming;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>1:1 移植自 windows/src/roam/types.test.ts。</summary>
public class RoamTypesTests
{
    [Fact]
    public void Normalize_KeepsPerInstanceWanderPauseRange()
    {
        var normalized = RoamConfigOps.Normalize(new RoamConfig(
            true, RoamMode.Wander, 5, 9000, 9000));

        Assert.Equal(9000, normalized.WanderPauseMinMs);
        Assert.Equal(9000, normalized.WanderPauseMaxMs);
    }

    [Fact]
    public void Normalize_ClampsSpeedAndFallsBackInvalidMode()
    {
        var normalized = RoamConfigOps.Normalize(new RoamConfig(true, (RoamMode)99, 0, 1200, 3500));

        Assert.Equal(RoamMode.Wander, normalized.Mode);
        Assert.Equal(1, normalized.Speed);
    }

    [Fact]
    public void PxPerSec_And_ClampToBounds_MatchTsSemantics()
    {
        Assert.Equal(265, RoamConfigOps.PxPerSec(5));
        Assert.Equal(40, RoamConfigOps.PxPerSec(0));

        var clamped = RoamConfigOps.ClampToBounds(new RoamPoint(-100, 5000), new RoamRect(0, 0, 1400, 1000));
        Assert.Equal(new RoamPoint(0, 1000 - RoamConstants.WinH), clamped);
    }
}

/// <summary>1:1 移植自 windows/src/roam/modes.test.ts + user-desktop.test.ts。</summary>
public class RoamModesTests
{
    private static RoamModes Modes(FakeRoamClock clock, SequenceRandom random, Func<RoamConfig> config)
        => new(random.ToFunc(), clock, config);

    [Fact]
    public void Climb_LandsOnTargetWindow_InsteadOfStoppingInsideArrivalMargin()
    {
        var clock = new FakeRoamClock();
        var modes = Modes(clock, new SequenceRandom(), () => RoamTestData.Config(RoamMode.Climb));
        var pos = new RoamPoint(900, 680);
        var pet = new FakeRoamPet();

        for (var tick = 0; tick < 120; tick++)
        {
            clock.Now += RoamConstants.TickMs;
            pos = modes.RunMode(RoamMode.Climb, RoamTestData.Environment, pos, pet);
        }

        Assert.Equal(400 - RoamConstants.WinH, pos.Y); // 窗口顶 400 - 320
    }

    [Fact]
    public void Climb_KeepsMoving_WhenNoWindowHasUsableTopEdge()
    {
        var clock = new FakeRoamClock();
        var random = new SequenceRandom().Steady(0.5);
        var modes = Modes(clock, random, () => RoamTestData.Config(RoamMode.Climb));
        var start = new RoamPoint(0, 0);
        var pet = new FakeRoamPet();

        modes.RunMode(RoamMode.Stay, RoamTestData.Environment, start, pet); // 重置模块状态
        var next = modes.RunMode(RoamMode.Climb,
            RoamTestData.Environment with { Windows = [] }, start, pet);

        Assert.NotEqual(start, next);
    }

    [Fact]
    public void Wander_UsesInstancePauseRange_AfterReachingTarget()
    {
        var clock = new FakeRoamClock();
        // randomTarget x/y + sampleWanderPause 各一次 0，之后 0.5
        var random = new SequenceRandom().Once(0, 0, 0).Steady(0.5);
        var modes = Modes(clock, random, () => RoamTestData.Config(RoamMode.Wander, pauseMin: 9000, pauseMax: 9000));
        var start = new RoamPoint(40, 40);
        var pet = new FakeRoamPet();

        modes.RunMode(RoamMode.Stay, RoamTestData.Environment, start, pet);
        modes.RunMode(RoamMode.Wander, RoamTestData.Environment, start, pet); // 到达 → restUntil = now+9000

        clock.Now += 1201; // 休息中
        Assert.Equal(start, modes.RunMode(RoamMode.Wander, RoamTestData.Environment, start, pet));

        clock.Now += 9001 - 1201; // 休息结束
        Assert.NotEqual(start, modes.RunMode(RoamMode.Wander, RoamTestData.Environment, start, pet));
    }

    [Fact]
    public void Climb_OnMaximizedDesktop_WalksToScreenTopAndPaces()
    {
        var desktop = new RoamEnvironment(
            new RoamRect(0, 0, 1707, 1067),
            [
                new SystemWindowInfo("W", new RoamRect(0, 0, 1707, 1067)),
                new SystemWindowInfo("P", new RoamRect(0, 0, 1707, 1067)),
                new SystemWindowInfo("[", new RoamRect(-7, -7, 1714, 1026)),
                new SystemWindowInfo("L", new RoamRect(-7, -7, 1714, 1026)),
            ]);
        var clock = new FakeRoamClock();
        var random = new SequenceRandom().Steady(0.5);
        var modes = Modes(clock, random, () => RoamTestData.Config(RoamMode.Climb));
        var pos = new RoamPoint(1200, 800);
        var pet = new FakeRoamPet();
        var t0 = clock.Now;

        modes.RunMode(RoamMode.Wander, desktop, pos, pet); // 重置模块状态

        // 走到窗口边缘：y 到达屏顶（climbTopY = 0）
        for (var i = 0; i < 300; i++)
        {
            clock.Now = t0 + i * 30;
            pos = modes.RunMode(RoamMode.Climb, desktop, pos, pet);
            if (pos.Y < 10) break;
        }
        Assert.InRange(pos.Y, 0, 10);

        // 沿顶边踱步：x 持续变化（含休息停顿）
        var xs = new HashSet<double>();
        for (var i = 0; i < 600; i++)
        {
            clock.Now = t0 + (300 + i) * 30;
            pos = modes.RunMode(RoamMode.Climb, desktop, pos, pet);
            if (pos.Y > 10) break;
            xs.Add(Math.Round(pos.X));
            if (xs.Count > 20) break;
        }
        Assert.InRange(pos.Y, 0, 10);
        Assert.True(xs.Count > 3, "must actually pace along the edge");
    }
}

using DesktopPet.Core.Roaming;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 1:1 移植自 windows/src/roam/fuzz.test.ts + engine-fuzz.test.ts：
/// climb 模式随机布局 × 随机起点 300 seeds 永不永久卡死（确定性 PRNG 可复现）。
/// </summary>
public class RoamFuzzTests
{
    private static readonly RoamRect WorkArea = new(0, 0, 1920, 1040);

    private static SystemWindowInfo RandomWindow(Func<double> rnd, int i)
    {
        var w = 300 + rnd() * 1400;
        var h = 200 + rnd() * 700;
        var top = Math.Round((rnd() - 0.35) * 1600);
        var left = Math.Round((rnd() - 0.2) * 2100);
        return new SystemWindowInfo($"W{i}", new RoamRect(left, top, left + w, top + h));
    }

    /// <summary>mode 级 fuzz（fuzz.test.ts）：900 ticks @30ms，静止 >280 ticks 判定卡死。</summary>
    private static bool SimulateModes(RoamEnvironment env, RoamPoint start, FakeRoamClock clock, Func<double> random)
    {
        var pet = new FakeRoamPet();
        var modes = new RoamModes(random, clock, () => RoamTestData.Config(RoamMode.Climb));
        modes.RunMode(RoamMode.Wander, env, start, pet); // 重置模块状态
        var pos = start;
        var stallRun = 0;
        var finalStuck = false;
        var t0 = clock.Now;
        for (var i = 0; i < 900; i++)
        {
            clock.Now = t0 + i * 30;
            var next = modes.RunMode(RoamMode.Climb, env, pos, pet);
            var moved = next.X != pos.X || next.Y != pos.Y;
            stallRun = moved ? 0 : stallRun + 1;
            if (i > 900 - 300 && stallRun > 280) finalStuck = true;
            pos = next;
        }
        return finalStuck;
    }

    /// <summary>engine 级 fuzz（engine-fuzz.test.ts）：clamp + <0.5px 视为未动，静止时 200ms tick。</summary>
    private static bool SimulateEngine(RoamEnvironment env, RoamPoint start, FakeRoamClock clock, Func<double> random)
    {
        var pet = new FakeRoamPet();
        var modes = new RoamModes(random, clock, () => RoamTestData.Config(RoamMode.Climb));
        modes.RunMode(RoamMode.Wander, env, start, pet);
        var pos = start;
        var stallRun = 0;
        var finalStuck = false;
        var t0 = clock.Now;
        for (var i = 0; i < 900; i++)
        {
            clock.Now = t0 + i * (stallRun > 0 ? 200 : 30);
            var next = modes.RunMode(RoamMode.Climb, env, pos, pet);
            var clamped = RoamConfigOps.ClampToBounds(next, env.WorkArea);
            var moved = Math.Abs(clamped.X - pos.X) >= 0.5 || Math.Abs(clamped.Y - pos.Y) >= 0.5;
            stallRun = moved ? 0 : stallRun + 1;
            if (i > 900 - 300 && stallRun > 60) finalStuck = true;
            pos = clamped;
        }
        return finalStuck;
    }

    private static void AssertNoStuck(Func<RoamEnvironment, RoamPoint, FakeRoamClock, Func<double>, bool> simulate)
    {
        var stuck = new List<(uint Seed, RoamPoint Start)>();
        for (uint seed = 0; seed < 300; seed++)
        {
            var layoutRng = Mulberry32.Create(seed);
            var nWin = 1 + (int)(layoutRng() * 5);
            var windows = Enumerable.Range(0, nWin).Select(i => RandomWindow(layoutRng, i)).ToList();
            var env = new RoamEnvironment(WorkArea, windows);
            var start = new RoamPoint(
                Math.Round(layoutRng() * 1800),
                Math.Round(layoutRng() * 900));
            var clock = new FakeRoamClock();
            var stuckNow = simulate(env, start, clock, Mulberry32.Create(7777));
            if (stuckNow)
            {
                stuck.Add((seed, start));
                if (stuck.Count >= 5) break;
            }
        }
        Assert.Empty(stuck);
    }

    [Fact]
    public void Climb_NeverGetsPermanentlyStuck_300Seeds()
        => AssertNoStuck(SimulateModes);

    [Fact]
    public void EngineLevelClimb_NeverGetsPermanentlyStuck_300Seeds()
        => AssertNoStuck(SimulateEngine);
}

using DesktopPet.Core.Roaming;

namespace DesktopPet.Core.Tests;

/// <summary>可控制时钟（对齐 vi.useFakeTimers + setSystemTime）。</summary>
public sealed class FakeRoamClock : IRoamClock
{
    public long Now { get; set; } = 1_734_566_400_000; // 2025-01-01 UTC

    long IRoamClock.NowMs() => Now;
}

/// <summary>确定性 PRNG（对齐 fuzz 测试的 mulberry32，保证失败可复现）。</summary>
public static class Mulberry32
{
    public static Func<double> Create(uint seed)
    {
        var a = seed;
        return () =>
        {
            a += 0x6D2B79F5;
            var t = a;
            t = (t ^ (t >>> 15)) * (t | 1);
            t ^= t + (t ^ (t >>> 7)) * (t | 61);
            return ((t ^ (t >>> 14)) >>> 0) / 4294967296.0;
        };
    }
}

/// <summary>预置随机序列（对齐 mockReturnValueOnce + mockReturnValue）。</summary>
public sealed class SequenceRandom
{
    private readonly Queue<double> _once = new();
    private double _steady = 0.5;

    public SequenceRandom Once(params double[] values)
    {
        foreach (var v in values) _once.Enqueue(v);
        return this;
    }

    public SequenceRandom Steady(double value)
    {
        _steady = value;
        return this;
    }

    public double Next() => _once.Count > 0 ? _once.Dequeue() : _steady;

    public Func<double> ToFunc() => Next;
}

public sealed class FakeRoamPet : IRoamPet
{
    public List<int> Rows { get; } = [];
    public int ClearCount { get; private set; }

    public void SetRow(int row) => Rows.Add(row);
    public void ClearRow() => ClearCount++;
}

public static class RoamTestData
{
    public static readonly RoamEnvironment Environment = new(
        new RoamRect(0, 0, 1400, 1000),
        [new SystemWindowInfo("Editor", new RoamRect(100, 400, 1000, 850))]);

    public static readonly RoamRect WorkArea1920 = new(0, 0, 1920, 1040);

    public static RoamConfig Config(RoamMode mode = RoamMode.Wander, int speed = 5,
        double pauseMin = 1200, double pauseMax = 3500)
        => new(true, mode, speed, pauseMin, pauseMax);
}

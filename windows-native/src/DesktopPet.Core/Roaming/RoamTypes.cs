using DesktopPet.Core.Pets;

namespace DesktopPet.Core.Roaming;

public enum RoamMode
{
    Stay,
    Wander,
    Cursor,
    Climb,
}

public readonly record struct RoamPoint(double X, double Y);

public readonly record struct RoamRect(double Left, double Top, double Right, double Bottom);

public sealed record SystemWindowInfo(string Title, RoamRect Rect);

public sealed record RoamEnvironment(RoamRect WorkArea, IReadOnlyList<SystemWindowInfo> Windows);

/// <summary>对齐 roam/types.ts 的 Config（归一化后）。</summary>
public sealed record RoamConfig(
    bool Enabled,
    RoamMode Mode,
    int Speed,
    double WanderPauseMinMs,
    double WanderPauseMaxMs);

/// <summary>1:1 移植自 windows/src/roam/types.ts 的常量与纯函数。</summary>
public static class RoamConstants
{
    public const double WinW = 260;
    public const double WinH = 320;
    public const double Margin = 40;

    public const int PhysicsReferenceTickMs = 30;
    public const double PhysicsFrictionAtReferenceTick = 0.9;
    public static readonly double PhysicsFriction = Math.Pow(
        PhysicsFrictionAtReferenceTick,
        (double)TickMs / PhysicsReferenceTickMs);
    public const double PhysicsMinSpeed = 15;
    public const double PhysicsGravity = 1800;
    public const double SampleWindowMs = 120;

    public const int TickMs = 30;
    /// <summary>固定 dt（秒），由 TICK_MS 推导（对齐 DT_SEC）。</summary>
    public const double DtSec = TickMs / 1000.0;

    /// <summary>拖拽释放速度低于此值（px/s）不抛掷。</summary>
    public const double ThrowMinSpeed = 15;

    /// <summary>静止 SLEEP_AFTER_MS 后入睡（Oneko 风格）。</summary>
    public const double SleepAfterMs = 30_000;
    /// <summary>睡眠姿态默认行（行 5 Failed 无 mood 使用）。</summary>
    public const int SleepRowDefault = 5;

    public static readonly RoamMode[] ValidModes = [RoamMode.Stay, RoamMode.Wander, RoamMode.Cursor, RoamMode.Climb];
}

public static class RoamConfigOps
{
    /// <summary>对齐 normalizeConfig：mode 非法回退 wander、speed clamp 1-10、pause 归一化。</summary>
    public static RoamConfig Normalize(RoamConfig raw)
    {
        var wanderPause = Pause.NormalizeWanderPauseRange(raw.WanderPauseMinMs, raw.WanderPauseMaxMs);
        var mode = Array.IndexOf(RoamConstants.ValidModes, raw.Mode) >= 0 ? raw.Mode : RoamMode.Wander;
        return new RoamConfig(
            raw.Enabled,
            mode,
            Math.Max(1, Math.Min(10, (int)Math.Round((double)raw.Speed))),
            wanderPause.MinMs,
            wanderPause.MaxMs);
    }

    public static double PxPerSec(int speed) => 40 + speed * 45;

    public static RoamPoint ClampToBounds(RoamPoint pos, RoamRect bounds) => new(
        Math.Max(bounds.Left, Math.Min(bounds.Right - RoamConstants.WinW, pos.X)),
        Math.Max(bounds.Top, Math.Min(bounds.Bottom - RoamConstants.WinH, pos.Y)));
}

/// <summary>引擎时钟（Date.now() 注入点，测试可控）。</summary>
public interface IRoamClock
{
    long NowMs();
}

public sealed class SystemRoamClock : IRoamClock
{
    public long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>动画行控制（PetRenderer 适配）。</summary>
public interface IRoamPet
{
    void SetRow(int row);
    void ClearRow();
}

public sealed class NullRoamPet : IRoamPet
{
    public static readonly NullRoamPet Instance = new();
    public void SetRow(int row) { }
    public void ClearRow() { }
}

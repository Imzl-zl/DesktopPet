namespace DesktopPet.Core.Roaming;

/// <summary>
/// 拖拽释放物理与重力下落：1:1 移植 windows/src/roam/physics.ts。全部在
/// 逻辑像素/秒下运算，与窗口 API 解耦（宿主接口注入），可单测。
/// </summary>
public sealed class RoamPhysics
{
    private readonly List<(long T, RoamPoint Pos)> _samples = [];

    private readonly Func<RoamPoint?> _readPosition;
    private readonly Action<RoamPoint> _writePosition;

    /// <param name="readPosition">当前逻辑位置（null = 窗口不可读）。</param>
    /// <param name="writePosition">移动到逻辑位置。</param>
    public RoamPhysics(Func<RoamPoint?> readPosition, Action<RoamPoint> writePosition)
    {
        _readPosition = readPosition;
        _writePosition = writePosition;
    }

    public void RecordSample(RoamPoint pos, long nowMs)
    {
        _samples.Add((nowMs, pos));
        while (_samples.Count > 0 && nowMs - _samples[0].T > RoamConstants.SampleWindowMs)
        {
            _samples.RemoveAt(0);
        }
    }

    /// <summary>窗口内最近 SAMPLE_WINDOW_MS 的释放速度（px/s）；样本不足返回 null。</summary>
    public (double Vx, double Vy)? ReleaseVelocity(long nowMs)
    {
        if (_samples.Count < 2) return null;
        var first = _samples[0];
        var last = _samples[^1];
        var dt = last.T - first.T;
        if (dt < 20) return null;
        return (
            (last.Pos.X - first.Pos.X) / dt * 1000,
            (last.Pos.Y - first.Pos.Y) / dt * 1000);
    }

    public void ClearSamples() => _samples.Clear();

    // ---- 抛掷（惯性 + 摩擦 + 边缘反弹）----

    /// <summary>推进抛掷一步：摩擦衰减 → 积分 → 边缘反弹。返回是否仍在飞行。</summary>
    public bool StepThrow(ref double vx, ref double vy, RoamRect bounds, IRoamPet? pet, long nowMs)
    {
        if (Math.Sqrt(vx * vx + vy * vy) < RoamConstants.PhysicsMinSpeed) return false;

        vx *= RoamConstants.PhysicsFriction;
        vy *= RoamConstants.PhysicsFriction;

        var pos = _readPosition();
        if (pos is null) return false;
        var nx = pos.Value.X + vx * RoamConstants.DtSec;
        var ny = pos.Value.Y + vy * RoamConstants.DtSec;
        (vx, nx) = BounceX(vx, nx, bounds);
        (vy, ny) = BounceY(vy, ny, bounds);

        _writePosition(new RoamPoint(nx, ny));
        pet?.SetRow(vx > 0 ? 1 : 2);
        return true;
    }

    public static (double Vx, double Nx) BounceX(double vx, double nx, RoamRect bounds)
    {
        if (nx < bounds.Left || nx > bounds.Right - RoamConstants.WinW)
        {
            return (-vx * 0.5, Math.Max(bounds.Left, Math.Min(bounds.Right - RoamConstants.WinW, nx)));
        }
        return (vx, nx);
    }

    public static (double Vy, double Ny) BounceY(double vy, double ny, RoamRect bounds)
    {
        if (ny < bounds.Top || ny > bounds.Bottom - RoamConstants.WinH)
        {
            return (-vy * 0.5, Math.Max(bounds.Top, Math.Min(bounds.Bottom - RoamConstants.WinH, ny)));
        }
        return (vy, ny);
    }

    // ---- 下落（重力 + 表面落地）----

    /// <summary>推进下落一步：重力加速 → 找地面 → 落地停。返回是否仍在坠落。</summary>
    public bool StepFall(double vx, ref double vy, RoamRect bounds, IReadOnlyList<RoamRect> surfaces, IRoamPet? pet)
    {
        var pos = _readPosition();
        if (pos is null) return false;

        vy += RoamConstants.PhysicsGravity * RoamConstants.DtSec;
        var nx = pos.Value.X + vx * RoamConstants.DtSec;
        var ny = pos.Value.Y + vy * RoamConstants.DtSec;

        var floorY = FindFloor(Math.Min(pos.Value.X, nx), Math.Max(pos.Value.X, nx), bounds, surfaces);
        if (ny >= floorY)
        {
            ny = floorY;
            _writePosition(new RoamPoint(
                Math.Max(bounds.Left, Math.Min(bounds.Right - RoamConstants.WinW, nx)), ny));
            pet?.ClearRow();
            return false;
        }

        _writePosition(new RoamPoint(
            Math.Max(bounds.Left, Math.Min(bounds.Right - RoamConstants.WinW, nx)), ny));
        pet?.SetRow(vx > 0 ? 1 : 2);
        return true;
    }

    /// <summary>
    /// 宠物横向范围 [x1, x2] 下的最高表面（窗口顶边或 work area 底部），返回宠物
    /// 停在该表面时的 TOP-Y。窗口顶太高站不上（宠物会出屏）时钳制到 work area 顶，
    /// 对齐 modes.ts 的 climbTopY。
    /// </summary>
    public static double FindFloor(double x1, double x2, RoamRect bounds, IReadOnlyList<RoamRect> surfaces)
    {
        var floor = bounds.Bottom - RoamConstants.WinH;
        foreach (var s in surfaces)
        {
            if (x2 < s.Left || x1 > s.Right) continue;
            var top = Math.Max(s.Top - RoamConstants.WinH, bounds.Top);
            if (top < floor) floor = top;
        }
        return floor;
    }
}

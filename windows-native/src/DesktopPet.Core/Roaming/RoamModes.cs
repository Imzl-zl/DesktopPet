using DesktopPet.Core.Pets;

namespace DesktopPet.Core.Roaming;

/// <summary>
/// 漫游模式策略：1:1 移植 windows/src/roam/modes.ts。每个模式是纯函数式步进：
/// 给定当前位置与环境，返回下一位置（并驱动动画行）。模块级状态
/// （wanderTarget/restUntil/climbDirection/activeMode）实例化为类字段，
/// 随机与时钟可注入（fuzz 测试确定性复现）。
/// </summary>
public sealed class RoamModes
{
    private const int RowRight = 1;
    private const int RowLeft = 2;
    private const double ArrivalDistance = 8;

    private readonly Func<double> _random;
    private readonly IRoamClock _clock;
    private readonly Func<RoamConfig> _configSource;
    private readonly Func<RoamPoint?>? _cursorProvider;

    private RoamMode? _activeMode;
    private int _climbDirection;            // -1 | 1，0 = 未定
    private RoamPoint? _wanderTarget;
    private double _restUntil;

    public RoamModes(Func<double> random, IRoamClock clock, Func<RoamConfig> configSource,
        Func<RoamPoint?>? cursorProvider = null)
    {
        _random = random;
        _clock = clock;
        _configSource = configSource;
        _cursorProvider = cursorProvider;
    }

    /// <summary>运行当前模式一步，返回下一位置（未移动则返回原位置）。</summary>
    public RoamPoint RunMode(RoamMode mode, RoamEnvironment env, RoamPoint pos, IRoamPet? pet)
    {
        if (mode != _activeMode)
        {
            if (mode != RoamMode.Climb) _climbDirection = 0;
            _wanderTarget = null;
            _restUntil = 0;
            _activeMode = mode;
        }
        return mode switch
        {
            RoamMode.Stay => pos,
            RoamMode.Cursor => FollowCursor(env, pos, pet),
            RoamMode.Climb => Climb(env, pos, pet),
            _ => Wander(env, pos, pet),
        };
    }

    // ---- cursor ----

    /// <summary>追鼠标光标，停在它旁边不遮挡指针（光标由 App 宿主注入）。</summary>
    private RoamPoint FollowCursor(RoamEnvironment env, RoamPoint pos, IRoamPet? pet)
    {
        if (_cursorProvider is null) return pos;
        var cursor = _cursorProvider();
        if (cursor is null) return pos;
        // 窗口中心对准光标：x = cursor - WIN_W/2，y = cursor - WIN_H + 20
        var target = new RoamPoint(cursor.Value.X - RoamConstants.WinW / 2, cursor.Value.Y - RoamConstants.WinH + 20);
        return MoveToward(target, pos, env.WorkArea, pet);
    }

    // ---- wander ----

    private bool InBounds(RoamPoint p, RoamRect bounds)
        => p.X >= bounds.Left && p.X <= bounds.Right - RoamConstants.WinW &&
           p.Y >= bounds.Top && p.Y <= bounds.Bottom - RoamConstants.WinH;

    private RoamPoint Wander(RoamEnvironment env, RoamPoint pos, IRoamPet? pet)
    {
        var config = _configSource();
        if (config.Mode != RoamMode.Wander)
        {
            _wanderTarget = null;
            _restUntil = 0;
            return pos;
        }
        return AdvanceWander(env, pos, pet,
            new Pause.WanderPauseRange(config.WanderPauseMinMs, config.WanderPauseMaxMs));
    }

    private RoamPoint FallbackWander(RoamEnvironment env, RoamPoint pos, IRoamPet? pet)
    {
        _restUntil = 0;
        return AdvanceWander(env, pos, pet,
            new Pause.WanderPauseRange(RoamConstants.IdleMsMin, RoamConstants.IdleMsMax));
    }

    private RoamPoint AdvanceWander(RoamEnvironment env, RoamPoint pos, IRoamPet? pet, Pause.WanderPauseRange pauseRange)
    {
        if (_clock.NowMs() < _restUntil) return pos;
        if (_wanderTarget is not { } target || !InBounds(target, env.WorkArea))
        {
            target = RandomTarget(env.WorkArea);
            _wanderTarget = target;
        }

        var dx = target.X - pos.X;
        var dy = target.Y - pos.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        // 到达判定与 moveToward 的停止阈值一致（ARRIVAL_DISTANCE）。
        if (dist < ArrivalDistance)
        {
            _wanderTarget = null;
            _restUntil = _clock.NowMs() + Pause.SampleWanderPauseMs(pauseRange, _random);
            pet?.ClearRow();
            return pos;
        }
        return MoveToward(target, pos, env.WorkArea, pet);
    }

    // ---- climb ----

    private RoamPoint Climb(RoamEnvironment env, RoamPoint pos, IRoamPet? pet)
    {
        if (env.Windows.Count == 0)
        {
            _climbDirection = 0;
            return FallbackWander(env, pos, pet);
        }
        if (_clock.NowMs() < _restUntil) return pos;

        var surface = FindSurfaceBelow(pos, env);
        if (!surface.IsWindow || !IsStandingOnSurface(pos, surface, env.WorkArea))
        {
            _climbDirection = 0;
            var target = NearestClimbTarget(pos, env);
            return target is { } t ? MoveToward(t, pos, env.WorkArea, pet) : FallbackWander(env, pos, pet);
        }

        var support = SurfaceSupportRange(surface.Rect, env.WorkArea);
        if (support is null)
        {
            _climbDirection = 0;
            return FallbackWander(env, pos, pet);
        }
        var standY = ClimbTopY(surface.Rect, env.WorkArea);

        var dir = _climbDirection != 0 ? _climbDirection : (_random() < 0.5 ? -1 : 1);
        _climbDirection = dir;
        var step = RoamConfigOps.PxPerSec(_configSource().Speed) * RoamConstants.DtSec;
        var nextX = pos.X + dir * step;
        var onEdge = nextX < support.Value.Left - 2 || nextX > support.Value.Right + 2;

        if (onEdge)
        {
            _restUntil = _clock.NowMs() + RoamConstants.IdleMsMin +
                         _random() * (RoamConstants.IdleMsMax - RoamConstants.IdleMsMin);
            _climbDirection = dir == 1 ? -1 : 1;
            pet?.ClearRow();
            return new RoamPoint(
                Math.Max(support.Value.Left, Math.Min(support.Value.Right, pos.X)),
                standY);
        }

        var next = RoamConfigOps.ClampToBounds(new RoamPoint(nextX, standY), env.WorkArea);
        pet?.SetRow(dir > 0 ? RowRight : RowLeft);
        return next;
    }

    // ---- shared ----

    private RoamPoint MoveToward(RoamPoint target, RoamPoint pos, RoamRect bounds, IRoamPet? pet)
    {
        var dx = target.X - pos.X;
        var dy = target.Y - pos.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < ArrivalDistance)
        {
            pet?.ClearRow();
            return pos;
        }
        var speed = RoamConfigOps.PxPerSec(_configSource().Speed);
        var move = Math.Min(dist, speed * RoamConstants.DtSec);
        var next = RoamConfigOps.ClampToBounds(
            new RoamPoint(pos.X + (dx / dist) * move, pos.Y + (dy / dist) * move), bounds);
        pet?.SetRow(dx > 0 ? RowRight : RowLeft);
        return next;
    }

    private RoamPoint RandomTarget(RoamRect bounds) => new(
        bounds.Left + RoamConstants.Margin +
        _random() * Math.Max(1, bounds.Right - bounds.Left - RoamConstants.WinW - RoamConstants.Margin * 2),
        bounds.Top + RoamConstants.Margin +
        _random() * Math.Max(1, bounds.Bottom - bounds.Top - RoamConstants.WinH - RoamConstants.Margin * 2));

    private readonly record struct SurfaceInfo(RoamRect Rect, bool IsWindow);

    private static (double Left, double Right)? SurfaceSupportRange(RoamRect rect, RoamRect bounds)
    {
        var left = Math.Max(bounds.Left, rect.Left - RoamConstants.WinW / 2);
        var right = Math.Min(bounds.Right - RoamConstants.WinW, rect.Right - RoamConstants.WinW / 2);
        return left <= right ? (left, right) : null;
    }

    private static double ClimbTopY(RoamRect rect, RoamRect workArea)
        => Math.Max(rect.Top - RoamConstants.WinH, workArea.Top);

    private static bool IsStandingOnSurface(RoamPoint pos, SurfaceInfo surface, RoamRect workArea)
        => Math.Abs(pos.Y - ClimbTopY(surface.Rect, workArea)) < ArrivalDistance;

    private RoamPoint? NearestClimbTarget(RoamPoint pos, RoamEnvironment env)
    {
        RoamPoint? nearest = null;
        var nearestDistance = double.PositiveInfinity;

        foreach (var window in env.Windows)
        {
            var support = SurfaceSupportRange(window.Rect, env.WorkArea);
            var y = ClimbTopY(window.Rect, env.WorkArea);
            if (support is null || y > env.WorkArea.Bottom - RoamConstants.WinH) continue;

            var target = new RoamPoint(
                Math.Max(support.Value.Left, Math.Min(support.Value.Right, pos.X)), y);
            var distance = Math.Sqrt(Math.Pow(target.X - pos.X, 2) + Math.Pow(target.Y - pos.Y, 2));
            if (distance < nearestDistance)
            {
                nearest = target;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    private SurfaceInfo FindSurfaceBelow(RoamPoint pos, RoamEnvironment env)
    {
        foreach (var w in env.Windows)
        {
            var support = SurfaceSupportRange(w.Rect, env.WorkArea);
            if (support is null ||
                pos.X < support.Value.Left - ArrivalDistance ||
                pos.X > support.Value.Right + ArrivalDistance) continue;
            if (Math.Abs(pos.Y - ClimbTopY(w.Rect, env.WorkArea)) < ArrivalDistance)
            {
                return new SurfaceInfo(w.Rect, true);
            }
        }
        return new SurfaceInfo(env.WorkArea, false);
    }
}

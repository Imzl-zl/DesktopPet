namespace DesktopPet.Core.Roaming;

/// <summary>窗口宿主（App 实现：物理/逻辑位置换算 + SetWindowPos）。</summary>
public interface IRoamHost
{
    /// <summary>当前逻辑位置（DPI 换算后）；不可读返回 null。</summary>
    RoamPoint? CurrentLogicalPos();

    void SetLogical(RoamPoint pos);

    /// <summary>移动到物理位置，返回对应逻辑位置。</summary>
    RoamPoint SetPhysical(RoamPoint physicalPos);
}

/// <summary>环境源（App 实现：work area + 系统窗口枚举）。</summary>
public interface IRoamEnvironmentSource
{
    RoamEnvironment? Fetch(bool includeSystemWindows);
}

/// <summary>
/// 漫游引擎：1:1 移植 windows/src/roam/engine.ts 的 tick 决策。App 用定时器
/// 驱动 Step()；返回 true 表示活跃（30ms 快速 tick），false 表示静止
/// （200ms 慢 tick）。抛掷/下落物理在 Step 内逐步推进（同步宿主调用）。
/// </summary>
public sealed class RoamEngine
{
    /// <summary>静止/睡眠/停留时的慢 tick（对齐 IDLE_TICK_MS）。</summary>
    public const int IdleTickMs = 200;

    private readonly RoamModes _modes;
    private readonly RoamPhysics _physics;
    private readonly IRoamHost _host;
    private readonly IRoamEnvironmentSource _environmentSource;
    private readonly IRoamClock _clock;
    private readonly IRoamPet? _pet;
    private readonly Func<RoamConfig> _config;
    private readonly int? _sleepRowOverride;

    private bool _dragging;
    private bool _releasePending;
    private bool _throwing;
    private bool _falling;
    private double _throwVx;
    private double _throwVy;
    private double _fallVx;
    private double _fallVy;

    private string _mood = "idle";
    private long _lastMoveTs;
    private bool _sleeping;
    private RoamRect? _lastBounds;
    private IReadOnlyList<RoamRect> _lastSurfaces = [];

    private static readonly HashSet<string> MoodPausesRoam = new() { "waiting", "celebrate" };

    public RoamEngine(
        IRoamHost host,
        IRoamEnvironmentSource environmentSource,
        Func<RoamConfig> configSource,
        IRoamClock clock,
        Func<double> random,
        IRoamPet? pet = null,
        int? sleepRowOverride = null,
        Func<RoamPoint?>? cursorProvider = null)
    {
        _host = host;
        _environmentSource = environmentSource;
        _config = () => RoamConfigOps.Normalize(configSource());
        _clock = clock;
        _pet = pet;
        _sleepRowOverride = sleepRowOverride;
        _physics = new RoamPhysics(host.CurrentLogicalPos, host.SetLogical);
        _modes = new RoamModes(random, clock, _config, cursorProvider);
        _lastMoveTs = clock.NowMs();
    }

    public bool IsDragging => _dragging;

    public void SetMood(string mood)
    {
        _mood = mood;
        if (mood != "idle") Wake();
    }

    public void SetDragging(bool isDragging)
    {
        _dragging = isDragging;
        if (isDragging)
        {
            _throwing = false;
            _falling = false;
            _physics.ClearSamples();
            Wake();
            _releasePending = false;
        }
        else
        {
            _releasePending = true;
        }
    }

    /// <summary>手动拖拽开始：显式采样起点（快速拖拽不等两个 tick）。</summary>
    public void BeginManualDrag()
    {
        SetDragging(true);
        var position = _host.CurrentLogicalPos();
        if (position is not null && _dragging) _physics.RecordSample(position.Value, _clock.NowMs());
    }

    /// <summary>手动拖拽移动：物理坐标移动 + 逻辑采样。</summary>
    public void MoveManualDrag(RoamPoint physicalPos)
    {
        var logical = _host.SetPhysical(physicalPos);
        if (_dragging) _physics.RecordSample(logical, _clock.NowMs());
    }

    public void FinishManualDrag() => SetDragging(false);

    /// <summary>系统夺走鼠标捕获或窗口取消时，终止拖拽且不产生抛掷/点击。</summary>
    public void CancelManualDrag()
    {
        _dragging = false;
        _releasePending = false;
        _physics.ClearSamples();
        _pet?.ClearRow();
        Wake();
    }

    /// <summary>一个 tick。返回 true = 活跃（30ms 快 tick）。</summary>
    public bool Step(long nowMs)
    {
        if (_throwing)
        {
            if (!_physics.StepThrow(ref _throwVx, ref _throwVy, _lastBounds ?? DefaultBounds(), _pet, nowMs))
            {
                _throwing = false;
                _pet?.ClearRow();
            }
            return true;
        }
        if (_falling)
        {
            if (!_physics.StepFall(_fallVx, ref _fallVy, _lastBounds ?? DefaultBounds(), _lastSurfaces, _pet))
            {
                _falling = false;
            }
            return true;
        }
        if (_dragging)
        {
            var pos = _host.CurrentLogicalPos();
            if (pos is not null) _physics.RecordSample(pos.Value, nowMs);
            return true;
        }
        if (_releasePending)
        {
            _releasePending = false;
            var vel = _physics.ReleaseVelocity(nowMs);
            _physics.ClearSamples();
            if (vel is { } v && Math.Sqrt(v.Vx * v.Vx + v.Vy * v.Vy) > RoamConstants.ThrowMinSpeed)
            {
                Wake();
                HandleDragRelease(v);
                return true;
            }
        }

        if (MoodPausesRoam.Contains(_mood))
        {
            Wake();
            _pet?.ClearRow();
            return false;
        }

        var cfg = _config();
        if (!cfg.Enabled || cfg.Mode == RoamMode.Stay)
        {
            HandleStationary(nowMs);
            return false;
        }

        Wake();
        var moved = StepMode(cfg.Mode, nowMs);
        if (!moved && _mood == "idle" && nowMs - _lastMoveTs > RoamConstants.SleepAfterMs)
        {
            EnterSleep();
        }
        return moved;
    }

    private RoamRect DefaultBounds() => new(0, 0, 1920, 1080);

    private bool StepMode(RoamMode mode, long nowMs)
    {
        var env = _environmentSource.Fetch(mode == RoamMode.Climb);
        if (env is null) return false;
        _lastBounds = env.WorkArea;
        _lastSurfaces = env.Windows.Select(w => w.Rect).ToList();

        var pos = _host.CurrentLogicalPos();
        if (pos is null) return false;

        var next = _modes.RunMode(mode, env, pos.Value, _pet);
        var clamped = RoamConfigOps.ClampToBounds(next, env.WorkArea);
        var moved = Math.Abs(clamped.X - pos.Value.X) >= 0.5 || Math.Abs(clamped.Y - pos.Value.Y) >= 0.5;
        if (!moved) return false;
        _lastMoveTs = nowMs;
        _host.SetLogical(clamped);
        return true;
    }

    private void HandleStationary(long nowMs)
    {
        if (_mood == "idle" && nowMs - _lastMoveTs > RoamConstants.SleepAfterMs) EnterSleep();
        else
        {
            Wake();
            _pet?.ClearRow();
        }
    }

    private void EnterSleep()
    {
        if (_sleeping) return;
        _sleeping = true;
        _pet?.SetRow(_sleepRowOverride ?? RoamConstants.SleepRowDefault);
    }

    private void Wake()
    {
        if (!_sleeping) return;
        _sleeping = false;
        _pet?.ClearRow();
    }

    /// <summary>拖拽释放：climb 模式且有窗口 → 下落，否则抛掷（对齐 handleDragRelease）。</summary>
    private void HandleDragRelease((double Vx, double Vy) vel)
    {
        var context = ResolveReleaseContext();
        if (context is null) return;
        var (cfg, env) = context.Value;
        _lastBounds = env.WorkArea;
        _lastSurfaces = env.Windows.Select(w => w.Rect).ToList();

        if (cfg.Mode == RoamMode.Climb && env.Windows.Count > 0)
        {
            _falling = true;
            _fallVx = vel.Vx;
            _fallVy = 0;
        }
        else
        {
            _throwing = true;
            _throwVx = vel.Vx;
            _throwVy = vel.Vy;
        }
    }

    /// <summary>
    /// 解析释放上下文（对齐 resolveReleaseContext）：环境获取期间 mode 可能变化
    /// （climb 需要窗口枚举）——重读 config，必要时重取含窗口的环境；最终决策用最新 config。
    /// </summary>
    private (RoamConfig Config, RoamEnvironment Env)? ResolveReleaseContext()
    {
        var cfg = _config();
        var includedSystemWindows = cfg.Mode == RoamMode.Climb;
        var env = _environmentSource.Fetch(includedSystemWindows);
        if (env is null) return null;

        cfg = _config();
        if (cfg.Mode == RoamMode.Climb && !includedSystemWindows)
        {
            env = _environmentSource.Fetch(true);
            if (env is null) return null;
        }

        return (_config(), env);
    }
}

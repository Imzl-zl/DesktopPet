namespace DesktopPet.Core.Danmaku;

/// <summary>
/// 弹幕条目（渲染热路径：public 字段避免属性开销）。
/// 实例由对象池复用：Enqueue 重置字段，出屏后回池。
/// </summary>
public sealed class DanmakuItem
{
    public string Text = "";
    public double X;       // 当前 x（屏幕左边缘起点；负值 = 正在入场）
    public int Track;      // 轨道号（0..trackCount-1），决定垂直位置
    public double Speed;   // px/s
}

/// <summary>
/// 弹幕引擎（纯逻辑）：条目池复用 + 轨道分配（防同轨道追尾）+ 出屏回收。
/// 轨道分配策略：选"尾部 x 最大"的轨道（尾部条目走得最远 = 最空）；空轨道优先；
/// 若最空轨道的尾部条目距入场点仍不足 minGap → 丢弃该条（限流，返回 null）。
/// 修复：原实现 _trackTailX 恒存入场 x（-0.3w），minGap 检查在 width ≥ minGap/0.3
/// （≈733px）时永不成立 → 限流死代码，且全部弹幕挤进轨道 0（尾部全相等时恒选 0）。
/// 渲染层（Win2D）每帧 Tick + 画 Active。
/// </summary>
public sealed class DanmakuEngine
{
    private readonly double _width;
    private readonly int _trackCount;
    private readonly double _minGap;
    private readonly double _minSpeed;
    private readonly double _maxSpeed;
    private readonly Random _random = new();

    private readonly List<DanmakuItem> _active = [];
    private readonly Stack<DanmakuItem> _pool = [];
    // 每轨道最后入场的条目（出屏 = 轨道空）；Enqueue 更新引用，Tick 更新其当前 x。
    private readonly DanmakuItem?[] _trackTail;
    // Enqueue（UI 线程）与 Tick/Active（Win2D 渲染线程）并发访问：必须互斥。
    // 修复：原实现无锁，List/Stack 并发损坏 → 偶发渲染崩溃。
    private readonly object _lock = new();

    public IReadOnlyList<DanmakuItem> Active
    {
        get
        {
            lock (_lock) return _active.ToArray(); // 快照：渲染帧内不受 Enqueue 干扰
        }
    }

    public int PoolSize
    {
        get
        {
            lock (_lock) return _pool.Count;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_lock) return _active.Count;
        }
    }

    public DanmakuEngine(
        double width,
        int trackCount = 8,
        double minSpeed = 200,
        double maxSpeed = 400,
        double minGap = 150)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (trackCount < 1 || trackCount > 32) throw new ArgumentOutOfRangeException(nameof(trackCount));
        if (minSpeed <= 0 || maxSpeed < minSpeed) throw new ArgumentOutOfRangeException(nameof(minSpeed));
        if (minGap <= 0) throw new ArgumentOutOfRangeException(nameof(minGap));
        _width = width;
        _trackCount = trackCount;
        _minSpeed = minSpeed;
        _maxSpeed = maxSpeed;
        _minGap = minGap;
        _trackTail = new DanmakuItem[trackCount];
    }

    /// <summary>入场位置（屏幕外左侧，防闪现）。</summary>
    private double EntryX => -_width * 0.3;

    /// <summary>入队一条弹幕；全部轨道都有追尾风险时返回 null（限流丢弃）。</summary>
    public DanmakuItem? Enqueue(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        lock (_lock)
        {
            // 最空轨道 = 尾部条目走得最远（x 最大）；空轨道（无尾部）恒优先
            var best = 0;
            for (var t = 1; t < _trackCount; t++)
            {
                if (TailX(t) > TailX(best)) best = t;
            }

            // 追尾检查：尾部条目当前 x 距入场点必须 ≥ minGap（否则新条目会贴上它）
            var entry = EntryX;
            if (TailX(best) < entry + _minGap) return null;

            var item = _pool.Count > 0 ? _pool.Pop() : new DanmakuItem();
            item.Text = text;
            item.X = entry;
            item.Track = best;
            item.Speed = _minSpeed + _random.NextDouble() * (_maxSpeed - _minSpeed);
            _trackTail[best] = item; // 本轨道新尾部
            _active.Add(item);
            return item;
        }
    }

    /// <summary>轨道尾部条目的当前 x；空轨道 = +∞（最空，优先选择）。</summary>
    private double TailX(int track)
        => _trackTail[track] is { } tail ? tail.X : double.PositiveInfinity;

    /// <summary>推进一帧：移动 + 出屏回收；返回是否仍有活动条目。</summary>
    public bool Tick(double deltaSeconds)
    {
        lock (_lock)
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                var item = _active[i];
                item.X += item.Speed * deltaSeconds;
                if (item.X > _width)
                {
                    _active.RemoveAt(i);
                    _pool.Push(item); // 回池复用
                    if (ReferenceEquals(_trackTail[item.Track], item))
                    {
                        _trackTail[item.Track] = null; // 尾部出屏 = 轨道空
                    }
                }
            }
            return _active.Count > 0;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var item in _active) item.Text = "";
            foreach (var item in _pool) item.Text = "";
            _active.Clear();
            _pool.Clear();
            Array.Clear(_trackTail);
        }
    }
}

namespace DesktopPet.Core.Ai;

/// <summary>
/// 帧哈希（dHash：水平差分感知哈希）。
/// 输入任意尺寸灰度图（≥9×8），最近邻采样到 9×8 网格，每行 8 个
/// "左像素 ≥ 右像素 → 0 / 左像素 &lt; 右像素 → 1" 差分位，共 64 bit。
/// 成本：O(1) 采样量，1fps 截屏下开销可忽略（迁移计划 §6.4）。
/// </summary>
public static class FrameHasher
{
    private const int SampleWidth = 9;   // 8 个水平差分
    private const int SampleHeight = 8;  // 8 行 → 64 bits

    public static ulong HashGrayscale(ReadOnlySpan<byte> gray, int width, int height)
    {
        if (gray.Length < width * height)
            throw new ArgumentException("灰度数据长度与尺寸不符", nameof(gray));
        if (width < SampleWidth || height < SampleHeight)
            throw new ArgumentException($"图像至少需要 {SampleWidth}×{SampleHeight}", nameof(width));

        ulong hash = 0;
        var bit = 0;
        for (var sy = 0; sy < SampleHeight; sy++)
        {
            var py = SampleCoord(sy, SampleHeight, height);
            var prev = SamplePixel(gray, width, height, 0, py);
            for (var sx = 1; sx < SampleWidth; sx++)
            {
                var px = SampleCoord(sx, SampleWidth, width);
                var cur = SamplePixel(gray, width, height, px, py);
                if (cur > prev) hash |= 1ul << bit;
                prev = cur;
                bit++;
            }
        }
        return hash;
    }

    /// <summary>把采样坐标映射回源图坐标（最近邻）。</summary>
    private static int SampleCoord(int sampleIndex, int sampleCount, int sourceSize)
        => (int)Math.Round(sampleIndex * (sourceSize - 1.0) / (sampleCount - 1));

    private static byte SamplePixel(ReadOnlySpan<byte> gray, int width, int height, int x, int y)
        => gray[y * width + x];

    /// <summary>汉明距离：两哈希不同 bit 数。</summary>
    public static int HammingDistance(ulong a, ulong b)
        => System.Numerics.BitOperations.PopCount(a ^ b);
}

/// <summary>
/// 变化检测：帧哈希汉明距离超过阈值才算"屏幕变化"（默认 5 bits，
/// 过滤轻微噪声/光标闪烁，只有真实布局变化才触发分析）。
/// </summary>
public sealed class ChangeDetector
{
    public const int DefaultThresholdBits = 5;

    private readonly int _thresholdBits;

    public ChangeDetector(int thresholdBits = DefaultThresholdBits)
    {
        if (thresholdBits < 1 || thresholdBits > 64)
            throw new ArgumentOutOfRangeException(nameof(thresholdBits));
        _thresholdBits = thresholdBits;
    }

    public bool HasChanged(ulong previousHash, ulong currentHash)
        => FrameHasher.HammingDistance(previousHash, currentHash) > _thresholdBits;
}

/// <summary>
/// 分析限频（API 成本护栏）：默认 ≥5s/次云端调用（迁移计划 §6.4）。
/// 失败的尝试不推进时间戳（保持"最后一次放行"语义）。
/// </summary>
public sealed class AnalysisThrottle
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _minInterval;
    private DateTime _lastTaken;

    public TimeSpan Interval => _minInterval;

    public AnalysisThrottle(TimeSpan? minInterval = null)
    {
        _minInterval = minInterval ?? DefaultInterval;
        if (_minInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minInterval));
        _lastTaken = DateTime.MinValue;
    }

    /// <summary>距上次放行满间隔则放行并更新时间戳，否则拒绝。间隔 0 = 不限频。</summary>
    public bool TryTake(DateTime now)
    {
        if (_minInterval <= TimeSpan.Zero) return true; // 不限频
        if (now - _lastTaken < _minInterval) return false;
        _lastTaken = now;
        return true;
    }
}

/// <summary>
/// 最近 N 条屏幕事件队列（对话上下文用；容量满时丢弃最旧）。
/// </summary>
public sealed class ScreenEventLog
{
    public const int DefaultCapacity = 8;

    private readonly int _capacity;
    private readonly Queue<ScreenEvent> _events = new();
    // RPC 接收线程（Add）与 Timer/UI 线程（Recent）并发访问：必须互斥。
    // 修复：原实现无锁，Queue 并发损坏 → 偶发事件丢失/异常被外层 catch 吞掉。
    private readonly object _lock = new();

    public ScreenEventLog(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Add(ScreenEvent e)
    {
        lock (_lock)
        {
            _events.Enqueue(e);
            while (_events.Count > _capacity) _events.Dequeue();
        }
    }

    /// <summary>最近事件（旧 → 新）。</summary>
    public IReadOnlyList<ScreenEvent> Recent()
    {
        lock (_lock) return _events.ToArray();
    }
}

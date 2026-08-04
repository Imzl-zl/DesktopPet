namespace DesktopPet.Agent.Capture;

/// <summary>捕获帧（灰度缩略图，320×180 量级；迁移计划 §6.4 成本护栏）。</summary>
public sealed record CapturedFrame(int Width, int Height, byte[] Gray);

/// <summary>
/// 屏幕捕获源抽象：真实实现 = Windows.Graphics.Capture（GraphicsCaptureSource）；
/// 离线测试/冒烟 = OfflineFrameSource（录制帧序列注入）。
/// 返回 null 表示本次无帧（不可用/失败），调用方静默跳过。
/// </summary>
public interface IScreenCaptureSource
{
    Task<CapturedFrame?> CaptureAsync(CancellationToken ct);
}

/// <summary>录制帧序列回放（迁移计划 §8：截屏模块离线测试，不依赖真实屏幕）。</summary>
public sealed class OfflineFrameSource : IScreenCaptureSource
{
    private readonly IReadOnlyList<CapturedFrame> _frames;
    private readonly bool _loop;
    private int _index;
    private readonly object _lock = new();

    public int CaptureCount { get; private set; }

    public OfflineFrameSource(IReadOnlyList<CapturedFrame> frames, bool loop = false)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _loop = loop;
    }

    public Task<CapturedFrame?> CaptureAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            CaptureCount++;
            if (_index >= _frames.Count)
            {
                if (!_loop || _frames.Count == 0) return Task.FromResult<CapturedFrame?>(null);
                _index = 0;
            }
            return Task.FromResult<CapturedFrame?>(_frames[_index++]);
        }
    }
}

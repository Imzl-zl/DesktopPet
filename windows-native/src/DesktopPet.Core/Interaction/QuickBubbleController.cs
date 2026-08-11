namespace DesktopPet.Core.Interaction;

/// <summary>快速气泡时钟（now/schedule/cancel 注入，测试可控）。</summary>
public interface IQuickBubbleClock
{
    long Now();
    IDisposable Schedule(Action callback, long delayMs);
}

/// <summary>
/// 快速气泡控制器：1:1 移植 windows/src/quick-bubble.ts —— show/current/expire
/// 状态机（新消息取消旧定时器、到期回调一次、剩余时间重调度）。
/// </summary>
public sealed class QuickBubbleController
{
    private readonly IQuickBubbleClock _clock;
    private readonly Action _onExpire;

    private string? _text;
    private long _expiresAt;
    private IDisposable? _timer;
    private int _generation;

    public QuickBubbleController(IQuickBubbleClock clock, Action onExpire)
    {
        _clock = clock;
        _onExpire = onExpire;
    }

    public void Show(string text, long durationMs)
    {
        CancelTimer();
        _text = text;
        _expiresAt = _clock.Now() + Math.Max(0, durationMs);
        _generation += 1;
        ScheduleExpiry(_generation);
    }

    public string? Current()
        => _text is not null && _clock.Now() < _expiresAt ? _text : null;

    private void ScheduleExpiry(int generation)
    {
        var delay = Math.Max(0, _expiresAt - _clock.Now());
        _timer = _clock.Schedule(() => Expire(generation), delay);
    }

    private void Expire(int generation)
    {
        if (generation != _generation || _text is null) return;

        var remaining = _expiresAt - _clock.Now();
        if (remaining > 0)
        {
            ScheduleExpiry(generation);
            return;
        }

        _timer = null;
        _text = null;
        _expiresAt = 0;
        _onExpire();
    }

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

/// <summary>时长归一化：1:1 移植 normalizeQuickBubbleDurationSeconds（生产走 AppSettings.QuickBubbleDurationSeconds）。</summary>
public static class QuickBubbleDuration
{
    public const double DefaultDurationSeconds = 4;

    public static double NormalizeSeconds(double? value)
        => value is { } seconds && double.IsFinite(seconds) && seconds >= 1
            ? seconds
            : DefaultDurationSeconds;
}

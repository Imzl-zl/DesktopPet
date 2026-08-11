namespace DesktopPet.Core.Summary;

/// <summary>
/// 总结图失败补试策略（纯逻辑，可单测）：
/// 生图渠道慢/抖动时，文本总结照常完成，图片在当天晚些时候自动补试，
/// 避免渠道慢（实测单张 3 分半，超过超时）导致当天图永远缺失。
/// 重试额度有限（默认 2 次、间隔 30 分钟）：渠道真挂时最多多花 2 次生图请求，不无限烧钱。
/// </summary>
public sealed class SummaryImageRetryPolicy
{
    private readonly int _maxRetries;
    private readonly TimeSpan _retryDelay;
    private int _remaining;
    private DateOnly? _day;
    private DateTime? _nextAttemptAt;

    public SummaryImageRetryPolicy(int maxRetries = 2, TimeSpan? retryDelay = null)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _retryDelay = retryDelay ?? TimeSpan.FromMinutes(30);
    }

    public bool HasPendingRetry => _remaining > 0;

    /// <summary>生图失败：记录失败目标日并安排补试。返回是否还有补试额度。</summary>
    public bool RecordFailure(DateOnly day, DateTime now)
    {
        _day = day;
        _remaining = _maxRetries;
        _nextAttemptAt = now + _retryDelay;
        return _remaining > 0;
    }

    /// <summary>到点且还有额度：消耗一次补试并返回目标日；未到点/无额度/目标日过期返回 false。
    /// 目标日超过昨天（App 多日未运行后补图无意义）视为放弃并清空状态。</summary>
    public bool TryConsumeRetry(DateOnly today, DateTime now, out DateOnly day)
    {
        day = default;
        if (_remaining <= 0 || _day is null || _nextAttemptAt is null) return false;
        if (_day.Value < today.AddDays(-1))
        {
            Reset();
            return false;
        }
        if (now < _nextAttemptAt.Value) return false;
        day = _day.Value;
        _remaining--;
        _nextAttemptAt = null;
        return true;
    }

    /// <summary>补试再失败：仍有额度则安排下一次（间隔重新计时），否则不再补试。</summary>
    public void RecordRetryFailure(DateTime now)
    {
        _nextAttemptAt = _remaining > 0 ? now + _retryDelay : null;
    }

    /// <summary>补试成功或放弃：清空全部状态。</summary>
    public void Reset()
    {
        _remaining = 0;
        _day = null;
        _nextAttemptAt = null;
    }
}

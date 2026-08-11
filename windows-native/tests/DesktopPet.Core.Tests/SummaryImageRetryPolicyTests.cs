using DesktopPet.Core.Summary;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 总结图失败补试策略（2026-08-11：慢渠道单张需 3 分半，超时后当天图永远缺失——
/// 文本总结照常，图片在当天晚些时候自动补试，额度有限不无限烧钱）。
/// </summary>
public class SummaryImageRetryPolicyTests
{
    private static readonly DateOnly Day = new(2026, 8, 11);
    private static readonly DateTime Now = new(2026, 8, 11, 9, 0, 0);

    [Fact]
    public void RecordFailure_SchedulesFirstRetry()
    {
        var policy = new SummaryImageRetryPolicy();
        Assert.True(policy.RecordFailure(Day, Now));
        Assert.True(policy.HasPendingRetry);

        // 未到点：不可取
        Assert.False(policy.TryConsumeRetry(Day, Now.AddMinutes(29), out _));
        // 到点：可取且目标日正确
        Assert.True(policy.TryConsumeRetry(Day, Now.AddMinutes(30), out var day));
        Assert.Equal(Day, day);
    }

    [Fact]
    public void RetryFailure_ReschedulesUntilBudgetExhausted()
    {
        var policy = new SummaryImageRetryPolicy(maxRetries: 2, retryDelay: TimeSpan.FromMinutes(30));
        policy.RecordFailure(Day, Now);

        // 第 1 次补试失败 → 重新安排
        Assert.True(policy.TryConsumeRetry(Day, Now.AddMinutes(30), out _));
        policy.RecordRetryFailure(Now.AddMinutes(31));
        Assert.True(policy.TryConsumeRetry(Day, Now.AddMinutes(61), out _));

        // 第 2 次补试失败 → 额度耗尽
        policy.RecordRetryFailure(Now.AddMinutes(62));
        Assert.False(policy.TryConsumeRetry(Day, Now.AddMinutes(120), out _));
        Assert.False(policy.HasPendingRetry);
    }

    [Fact]
    public void RetrySuccess_ClearsState()
    {
        var policy = new SummaryImageRetryPolicy();
        policy.RecordFailure(Day, Now);
        Assert.True(policy.TryConsumeRetry(Day, Now.AddMinutes(30), out _));

        policy.Reset(); // 成功路径
        Assert.False(policy.HasPendingRetry);
        Assert.False(policy.TryConsumeRetry(Day, Now.AddMinutes(60), out _));
    }

    [Fact]
    public void ZeroBudget_NeverRetries()
    {
        var policy = new SummaryImageRetryPolicy(maxRetries: 0);
        Assert.False(policy.RecordFailure(Day, Now));
        Assert.False(policy.TryConsumeRetry(Day, Now.AddDays(1), out _));
    }

    [Fact]
    public void StaleDay_ExpiredWithoutRetry()
    {
        var policy = new SummaryImageRetryPolicy();
        policy.RecordFailure(new DateOnly(2026, 8, 8), Now); // 3 天前失败

        // App 多日未运行：补超过昨天的图无意义 → 放弃并清空
        Assert.False(policy.TryConsumeRetry(Day, Now, out _));
        Assert.False(policy.HasPendingRetry);
    }

    [Fact]
    public void Defaults_AreTwoRetriesAtThirtyMinutes()
    {
        var policy = new SummaryImageRetryPolicy();
        policy.RecordFailure(Day, Now);
        Assert.True(policy.TryConsumeRetry(Day, Now.AddMinutes(30), out _));
        policy.RecordRetryFailure(Now.AddMinutes(31));
        Assert.True(policy.TryConsumeRetry(Day, Now.AddMinutes(61), out _));
        policy.RecordRetryFailure(Now.AddMinutes(62));
        Assert.False(policy.TryConsumeRetry(Day, Now.AddMinutes(120), out _));
    }
}

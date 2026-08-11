using DesktopPet.Core.Ai;
using DesktopPet.Core.Summary;

namespace DesktopPet.Core.Tests;

public class ActivitySummaryFormatterTests
{
    private static ActivitySession Session(DateTime start, ScreenEventKind kind, string summary, int minutes = 30)
        => new(start, start.AddMinutes(minutes), kind, summary, 1);

    [Fact]
    public void Format_Empty_ReturnsEmpty()
    {
        Assert.Equal("", ActivitySummaryFormatter.Format([]));
    }

    [Fact]
    public void Format_ListsSessionsWithTimeRangeAndKind()
    {
        var start = new DateTime(2026, 8, 11, 9, 0, 0);
        var text = ActivitySummaryFormatter.Format(
        [
            Session(start, ScreenEventKind.Coding, "用户在写代码"),
            Session(start.AddHours(2), ScreenEventKind.Video, "在看视频"),
        ]);

        Assert.Contains("09:00-09:30 [Coding] 用户在写代码", text);
        Assert.Contains("11:00-11:30 [Video] 在看视频", text);
    }

    [Fact]
    public void Format_EmptySummary_OmitsComment()
    {
        var start = new DateTime(2026, 8, 11, 9, 0, 0);
        var text = ActivitySummaryFormatter.Format([Session(start, ScreenEventKind.Coding, "")]);
        Assert.Contains("[Coding]", text);
        Assert.DoesNotContain(" [Coding] ", text);
    }

    [Fact]
    public void Format_OverBudget_KeepsRecentAndMentionsTotal()
    {
        var start = new DateTime(2026, 8, 11, 0, 0, 0);
        var sessions = Enumerable.Range(0, 5)
            .Select(i => Session(start.AddHours(i), ScreenEventKind.Coding, $"第{i}段"))
            .ToList();
        var text = ActivitySummaryFormatter.Format(sessions, maxSessions: 2);

        Assert.Contains("共 5 段", text);
        Assert.Contains("第3段", text);
        Assert.Contains("第4段", text);
        Assert.DoesNotContain("第0段", text);
    }

    [Fact]
    public void Format_ZeroBudget_ReturnsEmpty()
    {
        var start = new DateTime(2026, 8, 11, 9, 0, 0);
        Assert.Equal("", ActivitySummaryFormatter.Format([Session(start, ScreenEventKind.Coding, "x")], maxSessions: 0));
    }
}

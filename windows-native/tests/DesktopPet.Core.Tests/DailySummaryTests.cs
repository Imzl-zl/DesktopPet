using DesktopPet.Core.Summary;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 6f：每日总结 + 总结图（feature-research P1 ⑥；架构文档 §3.4）。
/// 总结全局一份（记录的是"你的一天"，多宠物共享）；文本 → ImagePromptBuilder →
/// IImageProvider 生图 → diary/yyyy-MM-dd.png；生图失败不影响文本。
/// </summary>
public class DailySummaryTests
{
    private static readonly DateOnly Day = new(2026, 8, 5);

    private static DailySummaryData Data() => new(
        Day: Day,
        UserHighlights: "项目上线顺利；晚上吃了火锅",
        ScreenHighlights: "持续编码；切换浏览器",
        Mood: "开心",
        PetName: "Miso");

    // ---- 总结 prompt 构建 ----

    [Fact]
    public void SummaryPromptBuilder_IncludesDayData()
    {
        var prompt = SummaryPromptBuilder.Build(Data());
        Assert.Contains("2026-08-05", prompt);
        Assert.Contains("项目上线顺利；晚上吃了火锅", prompt);
        Assert.Contains("持续编码；切换浏览器", prompt);
        Assert.Contains("开心", prompt);
        Assert.Contains("Miso", prompt);
    }

    [Fact]
    public void SummaryPromptBuilder_RequestsConciseWarmDiary()
    {
        var prompt = SummaryPromptBuilder.Build(Data());
        Assert.Contains("日记", prompt);
        Assert.Contains("口语", prompt);
        Assert.Contains("150", prompt); // 长度约束
    }

    // ---- 总结图 prompt 构建 ----

    [Fact]
    public void ImagePromptBuilder_BuildsLumenStylePrompt()
    {
        var summary = "项目上线顺利的一天，晚上吃了火锅很开心";
        var prompt = ImagePromptBuilder.Build(summary, petName: "Miso");
        Assert.Contains(summary, prompt);
        Assert.Contains("Miso", prompt);
        Assert.Contains("轻盈", prompt);
        Assert.Contains("柔和光感", prompt);
        Assert.Contains("像素", prompt);
    }

    // ---- 日记文件路径 ----

    [Fact]
    public void DiaryStore_PathsUseDateNames()
    {
        Assert.EndsWith("diary\\2026-08-05.txt", DiaryStore.TextPath(@"C:\appdata", Day));
        Assert.EndsWith("diary\\2026-08-05.png", DiaryStore.ImagePath(@"C:\appdata", Day));
    }

    // ---- 生成时机 ----

    [Fact]
    public void ShouldGenerate_FirstRun_GeneratesYesterday()
    {
        Assert.Equal(Day.AddDays(-1), DailySummaryTrigger.GetDueDate(null, Day));
    }

    [Fact]
    public void ShouldGenerate_AlreadyGeneratedToday_NothingDue()
    {
        Assert.Null(DailySummaryTrigger.GetDueDate(Day, Day));
    }

    [Fact]
    public void ShouldGenerate_YesterdayDone_NothingDue()
    {
        Assert.Null(DailySummaryTrigger.GetDueDate(Day.AddDays(-1), Day));
    }

    [Fact]
    public void ShouldGenerate_MultipleDaysMissing_OnlyLatestDue()
    {
        Assert.Equal(Day.AddDays(-1), DailySummaryTrigger.GetDueDate(Day.AddDays(-3), Day));
    }

    [Fact]
    public void ShouldGenerate_FutureDate_ClampedToYesterday()
    {
        Assert.Null(DailySummaryTrigger.GetDueDate(Day.AddDays(1), Day));
    }
}

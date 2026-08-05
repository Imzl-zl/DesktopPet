namespace DesktopPet.Core.Summary;

/// <summary>当日总结的输入数据（"你的一天"：做了什么/聊了什么/心情）。</summary>
public sealed record DailySummaryData(
    DateOnly Day,
    string UserHighlights,     // 用户当天消息要点（MemoryProfileExtractor.Compress 输出）
    string ScreenHighlights,   // 当天屏幕事件要点（ScreenContextFormatter 输出）
    string Mood,               // 当天心情（CareState 推导）
    string PetName);           // 宠物名（写入日记口吻）

/// <summary>
/// 总结文本 prompt 构建（架构文档 §3.4：每日总结流程）。
/// 模型按此生成 100-150 字温暖口语化日记；总结全局一份（记录"你的一天"）。
/// </summary>
public static class SummaryPromptBuilder
{
    public static string Build(DailySummaryData data)
    {
        return
            $"请为用户的桌宠日记写一份当天的总结（\"你的一天\"），日期 {data.Day:yyyy-MM-dd}。\n" +
            $"用户今天：{data.UserHighlights}\n" +
            $"屏幕活动：{data.ScreenHighlights}\n" +
            $"心情：{data.Mood}\n" +
            $"宠物：{data.PetName}\n\n" +
            "要求：100-150 字，温暖口语化，像朋友帮用户记日记；涵盖用户今天做了什么、聊了什么、心情如何；" +
            "以宠物 {data.PetName} 的视角写，用\"你\"称呼用户；不要列清单，连贯叙述。";
    }
}

/// <summary>
/// 总结图 prompt 构建（架构文档 §3.4）：总结摘要 + 宠物形象描述 + Lumen 画风约束。
/// </summary>
public static class ImagePromptBuilder
{
    public static string Build(string summaryText, string petName)
    {
        return
            $"为这段日记生成一张插画封面：{summaryText}\n" +
            $"画面主角：一只像素桌宠「{petName}」的拟人化形象。\n" +
            "画风：轻盈简约、柔和光感、暖色调、留白充足（Lumen 风格）；" +
            "温馨治愈，像一本日记的插图；不包含文字。";
    }
}

/// <summary>日记文件路径规则：{dir}/diary/yyyy-MM-dd.txt 与 .png（全局一份）。</summary>
public static class DiaryStore
{
    public static string TextPath(string appDataDir, DateOnly day)
        => System.IO.Path.Combine(appDataDir, "diary", $"{day:yyyy-MM-dd}.txt");

    public static string ImagePath(string appDataDir, DateOnly day)
        => System.IO.Path.Combine(appDataDir, "diary", $"{day:yyyy-MM-dd}.png");
}

/// <summary>
/// 总结生成时机：每日结束生成当日总结。实现为次日首次检查时补昨天
/// （避免午夜进程状态问题；多天缺失只补最近一天，YAGNI）。
/// </summary>
public static class DailySummaryTrigger
{
    /// <summary>返回需要生成总结的日期（null = 无）；未来日期视为已完成。</summary>
    public static DateOnly? GetDueDate(DateOnly? lastGenerated, DateOnly now)
    {
        if (lastGenerated is null) return now.AddDays(-1);
        var yesterday = now.AddDays(-1);
        return lastGenerated < yesterday ? yesterday : null;
    }
}

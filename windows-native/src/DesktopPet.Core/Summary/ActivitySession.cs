using DesktopPet.Core.Ai;

namespace DesktopPet.Core.Summary;

/// <summary>
/// 一段连续同类屏幕活动（行为会话化）：
/// 同一行为（写代码/看视频…）连续发生会合并为一段，记录起止时间与最后一条评论。
/// 总结/回顾时按段还原"一天做了什么"，避免逐条事件噪音。
/// </summary>
public sealed record ActivitySession(
    DateTime Start,
    DateTime End,
    ScreenEventKind Kind,
    string Summary,
    int EventCount);

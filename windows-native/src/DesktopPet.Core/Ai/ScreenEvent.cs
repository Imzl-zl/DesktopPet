namespace DesktopPet.Core.Ai;

/// <summary>屏幕事件类型（PetAgent 分析分类；Phase 6 主动互动按此触发）。</summary>
public enum ScreenEventKind
{
    Unknown,
    AppSwitch,
    Coding,
    Browsing,
    Video,
    Music,
    Gaming,
    Idle,
}

/// <summary>
/// 一条屏幕事件（迁移计划 §6.4 / 架构文档 §4 对话管道第 ④ 步）。
/// 记录时间、粗分类、文本摘要与来源帧哈希（去重用）。
/// IsStale = 分析期间屏幕又已变化（事件仍记 journal，但不应作为弹幕/主动输出呈现）。
/// </summary>
public sealed record ScreenEvent(
    DateTime Timestamp,
    ScreenEventKind Kind,
    string Summary,
    ulong FrameHash = 0,
    bool IsStale = false);

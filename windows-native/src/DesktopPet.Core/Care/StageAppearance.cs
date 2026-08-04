namespace DesktopPet.Core.Care;

/// <summary>成长表现参数（迁移计划 §3.7：阶段 × 三层表现，精灵图不变靠叠加）。</summary>
public sealed record StageAppearance(
    int StageIndex,
    string StageName,
    bool GlowUnder,          // 脚下光晕
    bool GlowOutline,        // 轮廓辉光
    bool Crown,              // 头顶皇冠
    bool StarParticles,      // idle 星点粒子
    string? GlowColor,       // hex 颜色：mint / sky / gold / null
    double BubbleStyleLevel); // 气泡样式升级档（0-2）

public static class StageAppearances
{
    public static readonly string[] Colors = [null!, "mint", "sky", "gold", "gold"];

    /// <summary>阶段视觉表现（对齐 §3.7 表格：Hatchling 朴素 → Legend 皇冠金辉光）。</summary>
    public static StageAppearance For(int stageIndex) => stageIndex switch
    {
        0 => new(0, "Hatchling", GlowUnder: false, GlowOutline: false, Crown: false, StarParticles: false, null, 0),
        1 => new(1, "Companion", GlowUnder: true, GlowOutline: false, Crown: false, StarParticles: false, "mint", 0),
        2 => new(2, "Scout", GlowUnder: true, GlowOutline: true, Crown: false, StarParticles: false, "sky", 1),
        3 => new(3, "Hero", GlowUnder: true, GlowOutline: true, Crown: false, StarParticles: true, "gold", 1),
        _ => new(4, "Legend", GlowUnder: true, GlowOutline: true, Crown: true, StarParticles: true, "gold", 2),
    };
}

/// <summary>阶段行为解锁（对齐 §3.7：Hatchling 仅 stay+wander 慢速，逐步解锁）。</summary>
public sealed record StageCapabilities(
    int StageIndex,
    bool CursorMode,     // 追鼠标（Companion+）
    bool ClimbMode,      // 爬窗口边缘（Scout+）
    double SpeedFactor,  // 漫游速度倍率（Scout+ 升一档）
    double ClickResponseFactor, // 点击响应速度（Hero+ 更快）
    double BubbleFrequency);    // 互动气泡频率（Hero+ 更频繁）

public static class StageCapabilitiesFor
{
    public static StageCapabilities For(int stageIndex) => stageIndex switch
    {
        0 => new(0, CursorMode: false, ClimbMode: false, SpeedFactor: 0.8, ClickResponseFactor: 1, BubbleFrequency: 1),
        1 => new(1, CursorMode: true, ClimbMode: false, SpeedFactor: 0.9, ClickResponseFactor: 1, BubbleFrequency: 1),
        2 => new(2, CursorMode: true, ClimbMode: true, SpeedFactor: 1, ClickResponseFactor: 1, BubbleFrequency: 1),
        3 => new(3, CursorMode: true, ClimbMode: true, SpeedFactor: 1, ClickResponseFactor: 0.7, BubbleFrequency: 1.5),
        _ => new(4, CursorMode: true, ClimbMode: true, SpeedFactor: 1, ClickResponseFactor: 0.5, BubbleFrequency: 2),
    };
}

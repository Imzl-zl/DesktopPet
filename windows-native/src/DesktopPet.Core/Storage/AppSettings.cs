using DesktopPet.Core.Hotkeys;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Roaming;

namespace DesktopPet.Core.Storage;

/// <summary>
/// 应用设置（对齐 Tauri 版 ap_theme/ap_opacity/ap_font_size/ap_font_family/ap_idle/
/// ap_fx/ap_quick_bubbles/ap_quick_bubble_duration/ap_left_click_action/ap_roam/ap_lang）。
/// 归一化与默认值在 Core（可单测），持久化由 IJsonStore 负责。
/// </summary>
public sealed record AppSettings(
    string Theme,                    // system | light | dark
    int BubbleOpacity,               // 0-100，默认 92
    int FontSize,                    // 默认 12
    string FontFamily,               // system | rounded | mono
    bool ShowIdleChatter,            // ap_idle != "0"
    int IdleChatterIntervalSeconds,  // 闲谈台词重选间隔 5-120s，默认 15
    bool AnimationEnabled,           // 精灵帧动画总开关（关 = 静态显示首帧；漫游/气泡独立）
    bool BobAnimation,               // ap_fx == "1"
    int PetSizePercent,              // 70-130，默认 100
    string LeftClickAction,          // none | self | all
    int QuickBubbleDurationSeconds,  // 1-10，默认 4
    string[] QuickBubblePresets,
    string[] IdleChatterLines,       // 闲谈台词池（每行一句；空数组 = 不显示闲谈）
    string[] HungryLines,            // 饥饿台词池（空数组 = 不显示饥饿台词）
    RoamConfig Roam,
    AppLang Lang,
    AiSettings Ai,                  // Phase 5：AI 设置（旧 JSON 无此字段 → 归一化给默认）
    HotkeySettings Hotkeys = null!) // 全局快捷键；旧 JSON 缺字段时 Normalize 恢复历史默认
{
    /// <summary>闲谈台词池默认值（旧 JSON 缺字段时回退；用户清空 [] 则保留空 = 不显示）。</summary>
    public static readonly string[] DefaultIdleChatterLines =
        ["…", "♪", "Zzz…", "(*´∀`*)", "呼~", "盯——"];

    /// <summary>饥饿台词池默认值（同上语义）。</summary>
    public static readonly string[] DefaultHungryLines =
        ["饿了…", "想吃小鱼干~", "好饿哦…", "投喂时间到！"];

    public static AppSettings Defaults(AppLang detectedLang) => new(
        Theme: "system",
        BubbleOpacity: 92,
        FontSize: 12,
        FontFamily: "system",
        ShowIdleChatter: true,
        IdleChatterIntervalSeconds: 15,
        AnimationEnabled: true,
        BobAnimation: false,
        PetSizePercent: 100,
        LeftClickAction: "none",
        QuickBubbleDurationSeconds: 4,
        QuickBubblePresets: ["辛苦了~", "摸摸头", "加油！", "休息一下吧", "盯——", "(*´∀`*)"],
        IdleChatterLines: DefaultIdleChatterLines,
        HungryLines: DefaultHungryLines,
        Roam: new RoamConfig(true, RoamMode.Wander, 5, 1200, 3500),
        Lang: detectedLang,
        Ai: AiSettings.Defaults,
        Hotkeys: HotkeySettings.Defaults);

    public static AppSettings Normalize(AppSettings raw)
    {
        var theme = raw.Theme switch { "light" => "light", "dark" => "dark", _ => "system" };
        var fontFamily = raw.FontFamily switch { "rounded" => "rounded", "mono" => "mono", _ => "system" };
        var clickAction = raw.LeftClickAction switch { "self" => "self", "all" => "all", _ => "none" };
        var roam = RoamConfigOps.Normalize(raw.Roam);
        return new AppSettings(
            theme,
            Math.Clamp(raw.BubbleOpacity, 0, 100),
            Math.Clamp(raw.FontSize, 8, 24),
            fontFamily,
            raw.ShowIdleChatter,
            Math.Clamp(raw.IdleChatterIntervalSeconds, 5, 120),
            raw.AnimationEnabled,
            raw.BobAnimation,
            Math.Clamp(raw.PetSizePercent, 70, 130),
            clickAction,
            Math.Clamp(raw.QuickBubbleDurationSeconds, 1, 10),
            raw.QuickBubblePresets ?? [],
            // null = 旧数据（无此字段）→ 默认台词；[] = 用户显式清空 → 保留（不显示闲谈）
            raw.IdleChatterLines ?? DefaultIdleChatterLines,
            raw.HungryLines ?? DefaultHungryLines,
            roam with { Speed = roam.Speed },
            raw.Lang,
            AiSettings.Normalize(raw.Ai),
            raw.Hotkeys ?? HotkeySettings.Defaults);
    }
}

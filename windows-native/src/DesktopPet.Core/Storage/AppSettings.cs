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
    bool BobAnimation,               // ap_fx == "1"
    int PetSizePercent,              // 70-130，默认 100
    string LeftClickAction,          // none | self | all
    int QuickBubbleDurationSeconds,  // 1-10，默认 4
    string[] QuickBubblePresets,
    RoamConfig Roam,
    AppLang Lang)
{
    public static AppSettings Defaults(AppLang detectedLang) => new(
        Theme: "system",
        BubbleOpacity: 92,
        FontSize: 12,
        FontFamily: "system",
        ShowIdleChatter: true,
        BobAnimation: false,
        PetSizePercent: 100,
        LeftClickAction: "none",
        QuickBubbleDurationSeconds: 4,
        QuickBubblePresets: ["辛苦了~", "摸摸头", "加油！", "休息一下吧", "盯——", "(*´∀`*)"],
        Roam: new RoamConfig(true, RoamMode.Wander, 5, 1200, 3500),
        Lang: detectedLang);

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
            raw.BobAnimation,
            Math.Clamp(raw.PetSizePercent, 70, 130),
            clickAction,
            Math.Clamp(raw.QuickBubbleDurationSeconds, 1, 10),
            raw.QuickBubblePresets ?? [],
            roam with { Speed = roam.Speed },
            raw.Lang);
    }
}

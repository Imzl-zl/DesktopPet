using DesktopPet.Core.Hotkeys;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;
using Xunit;

namespace DesktopPet.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_UseDetectedLanguage()
    {
        var defaults = AppSettings.Defaults(AppLang.ZhHans);

        Assert.Equal("system", defaults.Theme);
        Assert.Equal(92, defaults.BubbleOpacity);
        Assert.Equal(4, defaults.QuickBubbleDurationSeconds);
        Assert.Equal(AppLang.ZhHans, defaults.Lang);
        Assert.Equal(RoamMode.Wander, defaults.Roam.Mode);
    }

    [Fact]
    public void Normalize_ClampsAndFallsBack()
    {
        var raw = new AppSettings(
            Theme: "neon", BubbleOpacity: 500, FontSize: 0, FontFamily: "comic",
            ShowIdleChatter: false, IdleChatterIntervalSeconds: 999, AnimationEnabled: false,
            BobAnimation: true, PetSizePercent: 999,
            LeftClickAction: "all", QuickBubbleDurationSeconds: 99,
            QuickBubblePresets: ["x"], IdleChatterLines: [], HungryLines: [],
            Roam: new RoamConfig(true, (RoamMode)42, 0, 100, 99_999),
            Lang: AppLang.En,
            Ai: AiSettings.Defaults);

        var n = AppSettings.Normalize(raw);

        Assert.Equal("system", n.Theme);
        Assert.Equal(100, n.BubbleOpacity);
        Assert.Equal(8, n.FontSize);
        Assert.Equal("system", n.FontFamily);
        Assert.Equal(130, n.PetSizePercent);
        Assert.Equal(120, n.IdleChatterIntervalSeconds); // 上限钳制
        Assert.False(n.AnimationEnabled);
        Assert.Equal("all", n.LeftClickAction);
        Assert.Equal(10, n.QuickBubbleDurationSeconds);
        Assert.Empty(n.IdleChatterLines); // 用户显式清空 [] → 保留（不显示闲谈）
        Assert.Empty(n.HungryLines);
        Assert.Equal(RoamMode.Wander, n.Roam.Mode); // 非法 mode 回退
        Assert.Equal(1, n.Roam.Speed);              // 速度下限
        Assert.Equal(1200, n.Roam.WanderPauseMinMs); // 低于下限回退默认
    }

    [Fact]
    public void Normalize_MissingChatterLines_FallBackToDefaults()
    {
        // 旧 JSON 无台词池字段（null）→ 默认台词；显式空数组 → 保留空（用户关闭闲谈内容）
        var raw = AppSettings.Defaults(AppLang.En) with
        {
            IdleChatterLines = null!,
            HungryLines = null!,
        };

        var n = AppSettings.Normalize(raw);

        Assert.Equal(AppSettings.DefaultIdleChatterLines, n.IdleChatterLines);
        Assert.Equal(AppSettings.DefaultHungryLines, n.HungryLines);
    }

    [Fact]
    public void Defaults_HaveChatterAndHungryLines()
    {
        var defaults = AppSettings.Defaults(AppLang.ZhHans);

        Assert.Equal(6, defaults.IdleChatterLines.Length);
        Assert.Equal(4, defaults.HungryLines.Length);
    }

    [Fact]
    public void Normalize_KeepsValidValues()
    {
        var raw = AppSettings.Defaults(AppLang.En) with
        {
            Theme = "dark",
            BubbleOpacity = 70,
            FontSize = 13,
            FontFamily = "rounded",
            LeftClickAction = "self",
            QuickBubbleDurationSeconds = 6,
        };

        var n = AppSettings.Normalize(raw);

        Assert.Equal("dark", n.Theme);
        Assert.Equal(70, n.BubbleOpacity);
        Assert.Equal(13, n.FontSize);
        Assert.Equal("rounded", n.FontFamily);
        Assert.Equal("self", n.LeftClickAction);
        Assert.Equal(6, n.QuickBubbleDurationSeconds);
    }

    [Fact]
    public void Defaults_HotkeysUseLegacyPresets()
    {
        var hotkeys = AppSettings.Defaults(AppLang.En).Hotkeys;

        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'H'), hotkeys.TogglePets);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'M'), hotkeys.ToggleMode);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'S'), hotkeys.OpenSettings);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'Q'), hotkeys.Quit);
    }

    [Fact]
    public void Normalize_MissingHotkeysUsesDefaults_ButPreservesExplicitUnbound()
    {
        var legacy = AppSettings.Defaults(AppLang.En) with { Hotkeys = null! };
        Assert.Equal(HotkeySettings.Defaults, AppSettings.Normalize(legacy).Hotkeys);

        var allUnbound = new HotkeySettings(null, null, null, null);
        var normalized = AppSettings.Normalize(
            AppSettings.Defaults(AppLang.En) with { Hotkeys = allUnbound });
        Assert.Equal(allUnbound, normalized.Hotkeys);
    }

    [Fact]
    public void HotkeyValidation_RejectsDuplicatesAndUnsafeGestures()
    {
        var duplicate = HotkeySettings.Defaults with
        {
            ToggleMode = HotkeySettings.Defaults.TogglePets,
        };
        Assert.Contains(duplicate.Validate(), issue => issue.Code == "duplicate");

        var noModifier = HotkeySettings.Defaults with
        {
            TogglePets = new HotkeyGesture(HotkeyModifiers.None, 'H'),
        };
        Assert.Contains(noModifier.Validate(), issue => issue.Code == "missing-modifier");

        var unbound = new HotkeySettings(null, null, null, null);
        Assert.Empty(unbound.Validate());
    }

    [Fact]
    public void Normalize_ClampsIdleChatterIntervalFloor()
    {
        var raw = AppSettings.Defaults(AppLang.En) with { IdleChatterIntervalSeconds = 1 };
        Assert.Equal(5, AppSettings.Normalize(raw).IdleChatterIntervalSeconds);
    }
}

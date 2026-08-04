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
            ShowIdleChatter: false, BobAnimation: true, PetSizePercent: 999,
            LeftClickAction: "all", QuickBubbleDurationSeconds: 99,
            QuickBubblePresets: ["x"], Roam: new RoamConfig(true, (RoamMode)42, 0, 100, 99_999),
            Lang: AppLang.En);

        var n = AppSettings.Normalize(raw);

        Assert.Equal("system", n.Theme);
        Assert.Equal(100, n.BubbleOpacity);
        Assert.Equal(8, n.FontSize);
        Assert.Equal("system", n.FontFamily);
        Assert.Equal(130, n.PetSizePercent);
        Assert.Equal("all", n.LeftClickAction);
        Assert.Equal(10, n.QuickBubbleDurationSeconds);
        Assert.Equal(RoamMode.Wander, n.Roam.Mode); // 非法 mode 回退
        Assert.Equal(1, n.Roam.Speed);              // 速度下限
        Assert.Equal(1200, n.Roam.WanderPauseMinMs); // 低于下限回退默认
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
}

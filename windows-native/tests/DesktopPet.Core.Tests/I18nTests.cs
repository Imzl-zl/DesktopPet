using DesktopPet.Core.I18n;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>i18n 测试：词条完整性与 t() 语义（对照 windows/src/i18n.ts 抽取的 JSON）。</summary>
public class I18nTests
{
    [Fact]
    public void English_ReturnsKeyItself()
    {
        var i18n = new I18nService(AppLang.En);

        Assert.Equal("Today", i18n.T("Today"));
        Assert.Equal("DesktopPet", i18n.T("DesktopPet")); // 永不翻译
    }

    [Theory]
    [InlineData(AppLang.ZhHans, "Today", "今天")]
    [InlineData(AppLang.ZhHant, "Today", "今天")]
    [InlineData(AppLang.Vi, "Today", "Hôm nay")]
    public void KnownKeys_Translate(AppLang lang, string key, string expected)
    {
        var i18n = new I18nService(lang);

        Assert.Equal(expected, i18n.T(key));
    }

    [Fact]
    public void MissingKey_FallsBackToEnglish()
    {
        var i18n = new I18nService(AppLang.ZhHans);

        Assert.Equal("no-such-key-xyz", i18n.T("no-such-key-xyz"));
    }

    [Fact]
    public void CoreSettingsKeys_AreCompleteInAllLanguages()
    {
        var zh = new I18nService(AppLang.ZhHans);
        var zhTw = new I18nService(AppLang.ZhHant);
        var vi = new I18nService(AppLang.Vi);

        // 设置页核心词条必须在三种语言都有翻译（回退 key 即缺失）
        var keys = new[]
        {
            "General", "Care", "About", "Language", "Theme", "Bubble",
            "Roam", "Mode", "Stay", "Wander", "Follow cursor", "Climb windows",
            "Speed", "Wander pause", "Show idle chatter", "Pet size",
            "Custom messages (one per line, leave empty for default)",
            "Temporarily hide or show every pet without removing it from your desktop.",
            "Quit DesktopPet", "Show desktop pets", "Version", "Remove", "Add to desktop",
        };
        foreach (var key in keys)
        {
            Assert.NotEqual(key, zh.T(key));
            Assert.NotEqual(key, zhTw.T(key));
            Assert.NotEqual(key, vi.T(key));
        }
    }

    [Fact]
    public void Detect_MatchesSystemLanguageShape()
    {
        // Detect 只依赖 CurrentCulture 前缀判断，不抛异常即可
        var lang = I18nService.Detect();
        Assert.True(Enum.IsDefined(lang));
    }
}

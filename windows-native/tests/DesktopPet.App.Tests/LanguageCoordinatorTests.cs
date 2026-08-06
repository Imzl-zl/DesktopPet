using System.IO;
using DesktopPet.App.Localization;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Storage;

namespace DesktopPet.App.Tests;

public sealed class LanguageCoordinatorTests
{
    [Fact]
    public async Task ChangeLanguage_PersistsBeforePublishing()
    {
        var current = AppSettings.Defaults(AppLang.En);
        var i18n = new I18nService(AppLang.En);
        var events = new List<string>();
        var coordinator = new LanguageCoordinator(
            () => current,
            settings =>
            {
                Assert.Equal(AppLang.En, i18n.Lang);
                current = settings;
                events.Add("saved");
            },
            i18n,
            settings =>
            {
                Assert.Equal(AppLang.Vi, i18n.Lang);
                Assert.Equal(AppLang.Vi, settings.Lang);
                events.Add("published");
            },
            AppLang.En);

        var result = await coordinator.ChangeLanguageAsync(AppLang.Vi);

        Assert.True(result.Success);
        Assert.Equal(["saved", "published"], events);
        Assert.Equal(AppLang.Vi, current.Lang);
    }

    [Fact]
    public async Task SaveFailure_DoesNotChangeLanguageOrPublish()
    {
        var current = AppSettings.Defaults(AppLang.En);
        var i18n = new I18nService(AppLang.En);
        var publishes = 0;
        var failure = new JsonStoreException("写入", "settings.json", new IOException("disk"));
        var coordinator = new LanguageCoordinator(
            () => current,
            _ => throw failure,
            i18n,
            _ => publishes++,
            AppLang.En);

        var result = await coordinator.ChangeLanguageAsync(AppLang.ZhHant);

        Assert.False(result.Success);
        Assert.Same(failure, result.PersistenceError);
        Assert.Equal(AppLang.En, i18n.Lang);
        Assert.Equal(0, publishes);
    }
}

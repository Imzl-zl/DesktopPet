using DesktopPet.Core.I18n;
using DesktopPet.Core.Storage;

namespace DesktopPet.App.Localization;

public sealed record LanguageChangeResult(
    bool Success,
    AppSettings? Settings,
    JsonStoreException? PersistenceError = null);

/// <summary>Single language transition path: persist first, then publish UI state.</summary>
public sealed class LanguageCoordinator
{
    private readonly Func<AppSettings?> _load;
    private readonly Action<AppSettings> _save;
    private readonly I18nService _i18n;
    private readonly Action<AppSettings> _publish;
    private readonly AppLang _fallback;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LanguageCoordinator(
        IJsonStore store,
        I18nService i18n,
        Action<AppSettings> publish,
        AppLang fallback)
        : this(store.LoadSettings, store.SaveSettings, i18n, publish, fallback)
    {
    }

    public LanguageCoordinator(
        Func<AppSettings?> load,
        Action<AppSettings> save,
        I18nService i18n,
        Action<AppSettings> publish,
        AppLang fallback)
    {
        _load = load;
        _save = save;
        _i18n = i18n;
        _publish = publish;
        _fallback = fallback;
    }

    public async Task<LanguageChangeResult> ChangeLanguageAsync(
        AppLang language,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await Task.Run(
                () => AppSettings.Normalize(_load() ?? AppSettings.Defaults(_fallback)),
                ct);
            var next = current with { Lang = language };
            try
            {
                await Task.Run(() => _save(next), ct);
            }
            catch (JsonStoreException ex)
            {
                return new LanguageChangeResult(false, current, ex);
            }

            _i18n.SetLang(language);
            _publish(next);
            return new LanguageChangeResult(true, next);
        }
        finally
        {
            _gate.Release();
        }
    }
}

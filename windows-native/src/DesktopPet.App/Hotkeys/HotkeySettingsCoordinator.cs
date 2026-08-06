using DesktopPet.Core.Hotkeys;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Hotkey;

namespace DesktopPet.App.Hotkeys;

public sealed record HotkeySettingsUpdateResult(
    bool Success,
    AppSettings? Settings,
    string Message,
    bool RollbackComplete,
    HotkeyApplyResult RuntimeResult,
    JsonStoreException? PersistenceError = null);

/// <summary>
/// Coordinates the runtime replacement and JSON commit as one compensating transaction.
/// It reloads settings at commit time so an open settings window cannot overwrite newer fields.
/// </summary>
public sealed class HotkeySettingsCoordinator
{
    private readonly Func<AppSettings?> _load;
    private readonly Action<AppSettings> _save;
    private readonly IHotkeyRuntime _runtime;
    private readonly AppLang _fallbackLanguage;
    private readonly I18nService? _i18n;

    public HotkeySettingsCoordinator(
        IJsonStore store,
        IHotkeyRuntime runtime,
        AppLang fallbackLanguage,
        I18nService? i18n = null)
        : this(store.LoadSettings, store.SaveSettings, runtime, fallbackLanguage, i18n)
    {
    }

    public HotkeySettingsCoordinator(
        Func<AppSettings?> load,
        Action<AppSettings> save,
        IHotkeyRuntime runtime,
        AppLang fallbackLanguage,
        I18nService? i18n = null)
    {
        _load = load;
        _save = save;
        _runtime = runtime;
        _fallbackLanguage = fallbackLanguage;
        _i18n = i18n;
    }

    public HotkeySettingsUpdateResult Apply(HotkeySettings candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var current = AppSettings.Normalize(_load() ?? AppSettings.Defaults(_fallbackLanguage));
        var runtimeResult = _runtime.TryReplaceAll(candidate);
        if (!runtimeResult.Success)
        {
            return new HotkeySettingsUpdateResult(
                false,
                current,
                DescribeRuntimeFailure(runtimeResult, _i18n),
                runtimeResult.RollbackComplete,
                runtimeResult);
        }

        var next = current with { Hotkeys = candidate };
        try
        {
            _save(next);
            return new HotkeySettingsUpdateResult(
                true, next, Translate("快捷键已应用"), true, runtimeResult);
        }
        catch (JsonStoreException persistenceError)
        {
            var rollback = _runtime.TryReplaceAll(current.Hotkeys);
            var rollbackComplete = rollback.Success;
            var message = rollbackComplete
                ? Translate("设置保存失败，运行时快捷键已恢复")
                : Translate("设置保存失败，运行时快捷键未能完整恢复");
            return new HotkeySettingsUpdateResult(
                false,
                current,
                message,
                rollbackComplete,
                rollback,
                persistenceError);
        }
    }

    private string Translate(string key) => _i18n?.T(key) ?? key;

    public static string DescribeRuntimeFailure(HotkeyApplyResult result, I18nService? i18n = null)
    {
        string Translate(string key) => i18n?.T(key) ?? key;
        if (result.Phase == "validation")
        {
            if (result.ValidationIssues.Any(issue => issue.Code == "duplicate"))
                return Translate("快捷键重复，请为每个动作设置不同组合");
            return Translate("快捷键组合无效，主键必须搭配 Ctrl、Alt、Shift 或 Win");
        }

        var action = result.FailedAction switch
        {
            HotkeyAction.TogglePets => Translate("显示或隐藏宠物"),
            HotkeyAction.ToggleMode => Translate("切换输出模式"),
            HotkeyAction.OpenSettings => Translate("打开设置"),
            HotkeyAction.Quit => Translate("退出应用"),
            _ => Translate("快捷键"),
        };
        var reason = result.NativeError == 1409
            ? Translate("已被其他程序占用")
            : i18n?.Format("系统错误 {0}", result.NativeError) ?? $"系统错误 {result.NativeError}";
        var rollback = result.RollbackComplete
            ? Translate("旧快捷键保持不变")
            : Translate("旧快捷键未能完整恢复");
        return i18n?.Format("{0}注册失败：{1}；{2}", action, reason, rollback)
               ?? $"{action}注册失败：{reason}；{rollback}";
    }
}

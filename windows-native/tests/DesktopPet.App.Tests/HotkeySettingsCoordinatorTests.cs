using System.IO;
using DesktopPet.App.Hotkeys;
using DesktopPet.Core.Hotkeys;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Hotkey;

namespace DesktopPet.App.Tests;

public sealed class HotkeySettingsCoordinatorTests
{
    private sealed class FakeRuntime : IHotkeyRuntime
    {
        public Queue<HotkeyApplyResult> Results { get; } = new();
        public List<HotkeySettings> Applied { get; } = [];

        public HotkeyApplyResult TryReplaceAll(HotkeySettings candidate)
        {
            Applied.Add(candidate);
            return Results.Count > 0
                ? Results.Dequeue()
                : Success(candidate);
        }
    }

    [Fact]
    public void RuntimeFailure_DoesNotSave()
    {
        var old = AppSettings.Defaults(AppLang.En);
        var runtime = new FakeRuntime();
        runtime.Results.Enqueue(HotkeyApplyResult.NativeFailure(
            "register", HotkeyAction.Quit, 1409, true, old.Hotkeys));
        var saved = 0;
        var coordinator = new HotkeySettingsCoordinator(
            () => old, _ => saved++, runtime, AppLang.En);

        var result = coordinator.Apply(old.Hotkeys with { Quit = null });

        Assert.False(result.Success);
        Assert.Equal(0, saved);
        Assert.Single(runtime.Applied);
    }

    [Fact]
    public void RuntimeAndSaveSuccess_PublishCandidate()
    {
        var old = AppSettings.Defaults(AppLang.En);
        var candidate = old.Hotkeys with { Quit = null };
        var runtime = new FakeRuntime();
        AppSettings? saved = null;
        var coordinator = new HotkeySettingsCoordinator(
            () => old, settings => saved = settings, runtime, AppLang.En);

        var result = coordinator.Apply(candidate);

        Assert.True(result.Success);
        Assert.Equal(candidate, saved!.Hotkeys);
        Assert.Equal(candidate, result.Settings!.Hotkeys);
        Assert.Single(runtime.Applied);
    }

    [Fact]
    public void SaveFailure_RestoresOldRuntimeAndReportsRollback()
    {
        var old = AppSettings.Defaults(AppLang.En);
        var candidate = old.Hotkeys with { Quit = null };
        var runtime = new FakeRuntime();
        var failure = new JsonStoreException("写入", "app-settings.json", new IOException("disk"));
        var coordinator = new HotkeySettingsCoordinator(
            () => old, _ => throw failure, runtime, AppLang.En);

        var result = coordinator.Apply(candidate);

        Assert.False(result.Success);
        Assert.Same(failure, result.PersistenceError);
        Assert.True(result.RollbackComplete);
        Assert.Equal([candidate, old.Hotkeys], runtime.Applied);
    }

    [Fact]
    public void SaveFailureAndRuntimeRestoreFailure_IsExplicitlyDegraded()
    {
        var old = AppSettings.Defaults(AppLang.En);
        var candidate = old.Hotkeys with { Quit = null };
        var runtime = new FakeRuntime();
        runtime.Results.Enqueue(Success(candidate));
        runtime.Results.Enqueue(HotkeyApplyResult.NativeFailure(
            "register", HotkeyAction.ToggleMode, 5, false, new HotkeySettings(null, null, null, null)));
        var coordinator = new HotkeySettingsCoordinator(
            () => old,
            _ => throw new JsonStoreException("写入", "app-settings.json", new IOException("disk")),
            runtime,
            AppLang.En);

        var result = coordinator.Apply(candidate);

        Assert.False(result.Success);
        Assert.False(result.RollbackComplete);
        Assert.Contains("运行时快捷键未能完整恢复", result.Message);
    }

    private static HotkeyApplyResult Success(HotkeySettings settings)
        => new(true, "committed", null, 0, true, [], settings);
}

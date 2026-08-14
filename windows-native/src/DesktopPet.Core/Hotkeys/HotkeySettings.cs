namespace DesktopPet.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public enum HotkeyAction
{
    TogglePets,
    ToggleMode,
    OpenSettings,
    Quit,
}

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey);

public sealed record HotkeyValidationIssue(
    string Code,
    HotkeyAction Action,
    HotkeyAction? ConflictingAction = null);

/// <summary>Persisted complete set of global hotkey bindings. Null means explicitly unbound.</summary>
public sealed record HotkeySettings(
    HotkeyGesture? TogglePets,
    HotkeyGesture? ToggleMode,
    HotkeyGesture? OpenSettings,
    HotkeyGesture? Quit)
{
    private const HotkeyModifiers AllowedModifiers =
        HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows;

    // 默认用 Win+Ctrl 而非 Ctrl+Alt：Ctrl+Alt 系列被腾讯会议（Ctrl+Alt+M/S/V）、
    // QQ 截图（Ctrl+Alt+A）等常用软件大范围占用；而 Win+Ctrl 系列里 M/S 也常被
    // 其他软件占用、Q 是 Win11「快速助手」系统保留（任何程序注册都失败），
    // 实测 H/T/U/X 空闲（2026-08-14）。字母语义：H=隐藏宠物、T=切换模式、U=设置、X=退出。
    public static HotkeySettings Defaults => new(
        new HotkeyGesture(HotkeyModifiers.Windows | HotkeyModifiers.Control, 'H'),
        new HotkeyGesture(HotkeyModifiers.Windows | HotkeyModifiers.Control, 'T'),
        new HotkeyGesture(HotkeyModifiers.Windows | HotkeyModifiers.Control, 'U'),
        new HotkeyGesture(HotkeyModifiers.Windows | HotkeyModifiers.Control, 'X'));

    public IEnumerable<(HotkeyAction Action, HotkeyGesture? Gesture)> Enumerate()
    {
        yield return (HotkeyAction.TogglePets, TogglePets);
        yield return (HotkeyAction.ToggleMode, ToggleMode);
        yield return (HotkeyAction.OpenSettings, OpenSettings);
        yield return (HotkeyAction.Quit, Quit);
    }

    public IReadOnlyList<HotkeyValidationIssue> Validate()
    {
        var issues = new List<HotkeyValidationIssue>();
        var seen = new Dictionary<(HotkeyModifiers, uint), HotkeyAction>();
        foreach (var (action, gesture) in Enumerate())
        {
            if (gesture is null) continue;
            if ((gesture.Modifiers & ~AllowedModifiers) != 0)
            {
                issues.Add(new HotkeyValidationIssue("invalid-modifier", action));
                continue;
            }
            if (gesture.Modifiers == HotkeyModifiers.None)
                issues.Add(new HotkeyValidationIssue("missing-modifier", action));
            if (!IsValidPrimaryKey(gesture.VirtualKey))
                issues.Add(new HotkeyValidationIssue("invalid-key", action));

            var normalized = (gesture.Modifiers, gesture.VirtualKey);
            if (seen.TryGetValue(normalized, out var conflicting))
                issues.Add(new HotkeyValidationIssue("duplicate", action, conflicting));
            else
                seen[normalized] = action;
        }
        return issues;
    }

    public HotkeyGesture? Get(HotkeyAction action) => action switch
    {
        HotkeyAction.TogglePets => TogglePets,
        HotkeyAction.ToggleMode => ToggleMode,
        HotkeyAction.OpenSettings => OpenSettings,
        HotkeyAction.Quit => Quit,
        _ => null,
    };

    private static bool IsValidPrimaryKey(uint virtualKey)
        => virtualKey is > 0 and <= 0xFE
           && virtualKey is not 0x10 and not 0x11 and not 0x12 and not 0x5B and not 0x5C;
}

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

    public static HotkeySettings Defaults => new(
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'H'),
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'M'),
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'S'),
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'Q'));

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

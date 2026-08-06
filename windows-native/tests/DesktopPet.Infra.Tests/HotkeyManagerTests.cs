using DesktopPet.Core.Hotkeys;
using DesktopPet.Infra.Hotkey;

namespace DesktopPet.Infra.Tests;

public sealed class HotkeyManagerTests
{
    private sealed class FakeHotkeyRegistration : IHotkeyRegistration
    {
        public Dictionary<int, (uint Mods, uint Key)> Active { get; } = [];
        public List<int> Unregistered { get; } = [];
        public Dictionary<int, int> RegisterErrorsByCall { get; } = [];
        public Dictionary<int, int> UnregisterErrorsByCall { get; } = [];
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }

        public HotkeyNativeResult Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
        {
            RegisterCalls++;
            if (RegisterErrorsByCall.TryGetValue(RegisterCalls, out var error))
                return HotkeyNativeResult.Failed(error);
            Active[id] = (modifiers, virtualKey);
            return HotkeyNativeResult.Ok;
        }

        public HotkeyNativeResult Unregister(IntPtr hwnd, int id)
        {
            UnregisterCalls++;
            if (UnregisterErrorsByCall.TryGetValue(UnregisterCalls, out var error))
                return HotkeyNativeResult.Failed(error);
            Active.Remove(id);
            Unregistered.Add(id);
            return HotkeyNativeResult.Ok;
        }
    }

    [Fact]
    public void TryReplaceAll_RegistersCompleteSetAndResolvesActions()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);

        var result = manager.TryReplaceAll(HotkeySettings.Defaults);

        Assert.True(result.Success);
        Assert.Equal(4, fake.Active.Count);
        Assert.Equal(HotkeySettings.Defaults, manager.CurrentSettings);
        Assert.Equal(
            Enum.GetValues<HotkeyAction>().Order(),
            fake.Active.Keys.Select(id => manager.Resolve(id)!.Value).Order());
        Assert.All(fake.Active.Values, value => Assert.NotEqual(0u, value.Mods & HotkeyManager.ModNoRepeat));
    }

    [Fact]
    public void TryReplaceAll_UnboundActionsAreNotRegistered()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);
        Assert.True(manager.TryReplaceAll(HotkeySettings.Defaults).Success);

        var candidate = HotkeySettings.Defaults with { ToggleMode = null, Quit = null };
        var result = manager.TryReplaceAll(candidate);

        Assert.True(result.Success);
        Assert.Equal(2, fake.Active.Count);
        Assert.Equal(candidate, manager.CurrentSettings);
    }

    [Fact]
    public void TryReplaceAll_DuplicateDoesNotTouchCurrentRegistrations()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);
        Assert.True(manager.TryReplaceAll(HotkeySettings.Defaults).Success);
        var activeBefore = fake.Active.ToArray();
        var callsBefore = (fake.RegisterCalls, fake.UnregisterCalls);
        var duplicate = HotkeySettings.Defaults with { Quit = HotkeySettings.Defaults.TogglePets };

        var result = manager.TryReplaceAll(duplicate);

        Assert.False(result.Success);
        Assert.Equal("validation", result.Phase);
        Assert.Contains(result.ValidationIssues, issue => issue.Code == "duplicate");
        Assert.Equal(callsBefore, (fake.RegisterCalls, fake.UnregisterCalls));
        Assert.Equal(activeBefore.OrderBy(x => x.Key), fake.Active.OrderBy(x => x.Key));
        Assert.Equal(HotkeySettings.Defaults, manager.CurrentSettings);
    }

    [Fact]
    public void TryReplaceAll_CandidateFailureRestoresOldCompleteSet()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);
        Assert.True(manager.TryReplaceAll(HotkeySettings.Defaults).Success);
        fake.RegisterErrorsByCall[6] = 1409; // second candidate binding
        var candidate = HotkeySettings.Defaults with
        {
            TogglePets = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'P'),
            ToggleMode = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'M'),
        };

        var result = manager.TryReplaceAll(candidate);

        Assert.False(result.Success);
        Assert.Equal("register", result.Phase);
        Assert.Equal(HotkeyAction.ToggleMode, result.FailedAction);
        Assert.Equal(1409, result.NativeError);
        Assert.True(result.RollbackComplete);
        Assert.Equal(HotkeySettings.Defaults, manager.CurrentSettings);
        Assert.Equal(4, fake.Active.Count);
    }

    [Fact]
    public void TryReplaceAll_RollbackFailureReportsDegradedActualSet()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);
        Assert.True(manager.TryReplaceAll(HotkeySettings.Defaults).Success);
        fake.RegisterErrorsByCall[6] = 1409; // candidate ToggleMode fails
        fake.RegisterErrorsByCall[8] = 5;    // old ToggleMode restore fails
        var candidate = HotkeySettings.Defaults with
        {
            TogglePets = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'P'),
            ToggleMode = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 'M'),
        };

        var result = manager.TryReplaceAll(candidate);

        Assert.False(result.Success);
        Assert.False(result.RollbackComplete);
        Assert.Null(manager.CurrentSettings.ToggleMode);
        Assert.Equal(3, fake.Active.Count);
        Assert.All(fake.Active.Keys, id => Assert.NotNull(manager.Resolve(id)));
    }

    [Fact]
    public void Dispose_UnregistersActualActiveSet()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);
        Assert.True(manager.TryReplaceAll(HotkeySettings.Defaults).Success);

        manager.Dispose();

        Assert.Empty(fake.Active);
        Assert.All(fake.Unregistered, id => Assert.Null(manager.Resolve(id)));
    }
}

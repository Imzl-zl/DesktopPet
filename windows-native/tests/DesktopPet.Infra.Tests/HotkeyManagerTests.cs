using DesktopPet.Infra.Hotkey;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// Phase 6h：全局快捷键（bongo-cat-next 借鉴；迁移计划 §5 Phase 6）。
/// Ctrl+Alt+H 显隐 / Ctrl+Alt+M 切换模式 / Ctrl+Alt+S 设置 / Ctrl+Alt+Q 退出。
/// RegisterHotKey P/Invoke；WndProc 接线在 App 层（WM_HOTKEY 分发）。
/// </summary>
public class HotkeyManagerTests
{
    private sealed class FakeHotkeyRegistration : IHotkeyRegistration
    {
        public List<(int Id, uint Mods, uint Key)> Registered { get; } = [];
        public List<int> Unregistered { get; } = [];
        public bool FailNext { get; set; }

        public bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
        {
            if (FailNext) { FailNext = false; return false; }
            Registered.Add((id, modifiers, virtualKey));
            return true;
        }

        public bool Unregister(IntPtr hwnd, int id)
        {
            Unregistered.Add(id);
            return true;
        }
    }

    private static readonly (HotkeyAction Action, uint Mods, uint Key)[] Presets =
    [
        (HotkeyAction.TogglePets, HotkeyManager.ModControlAlt, 'H'),
        (HotkeyAction.ToggleMode, HotkeyManager.ModControlAlt, 'M'),
        (HotkeyAction.OpenSettings, HotkeyManager.ModControlAlt, 'S'),
        (HotkeyAction.Quit, HotkeyManager.ModControlAlt, 'Q'),
    ];

    [Fact]
    public void Register_AssignsIds_AndResolvesActions()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);

        foreach (var (action, mods, key) in Presets)
            Assert.True(manager.Register(action, mods, key));

        Assert.Equal(4, fake.Registered.Count);
        // 每个注册 id 都映射回对应 action
        foreach (var (id, _, _) in fake.Registered)
            Assert.NotNull(manager.Resolve(id));
        Assert.Equal(HotkeyAction.Quit, manager.Resolve(fake.Registered[3].Id));
        Assert.Equal(HotkeyAction.TogglePets, manager.Resolve(fake.Registered[0].Id));
    }

    [Fact]
    public void Register_ReRegisterSameAction_UnregistersOldId()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);

        Assert.True(manager.Register(HotkeyAction.TogglePets, HotkeyManager.ModControlAlt, 'H'));
        var firstId = fake.Registered[0].Id;
        Assert.True(manager.Register(HotkeyAction.TogglePets, HotkeyManager.ModControlAlt, 'H'));

        Assert.Contains(firstId, fake.Unregistered);          // 旧 id 已注销
        Assert.Equal(2, fake.Registered.Count);
        Assert.Equal(HotkeyAction.TogglePets, manager.Resolve(fake.Registered[1].Id));
    }

    [Fact]
    public void Register_Failure_LeavesNoMapping()
    {
        var fake = new FakeHotkeyRegistration { FailNext = true };
        var manager = new HotkeyManager(IntPtr.Zero, fake);

        Assert.False(manager.Register(HotkeyAction.Quit, HotkeyManager.ModControlAlt, 'Q'));
        Assert.Empty(fake.Registered);
        Assert.Null(manager.Resolve(0xC000));
    }

    [Fact]
    public void UnregisterAll_ClearsMappings()
    {
        var fake = new FakeHotkeyRegistration();
        var manager = new HotkeyManager(IntPtr.Zero, fake);
        manager.Register(HotkeyAction.Quit, HotkeyManager.ModControlAlt, 'Q');

        manager.UnregisterAll();

        Assert.Single(fake.Unregistered);
        Assert.Null(manager.Resolve(fake.Registered[0].Id));
    }

    [Fact]
    public void Resolve_UnknownId_ReturnsNull()
    {
        var manager = new HotkeyManager(IntPtr.Zero, new FakeHotkeyRegistration());
        Assert.Null(manager.Resolve(0x1234));
    }

    [Fact]
    public void Presets_UseExpectedModifiersAndKeys()
    {
        Assert.All(Presets, p =>
        {
            Assert.Equal(HotkeyManager.ModControlAlt, p.Mods);
            Assert.InRange(p.Key, 'A', 'Z');
        });
        Assert.Equal('H', Presets[0].Key);
        Assert.Equal('M', Presets[1].Key);
        Assert.Equal('S', Presets[2].Key);
        Assert.Equal('Q', Presets[3].Key);
    }
}

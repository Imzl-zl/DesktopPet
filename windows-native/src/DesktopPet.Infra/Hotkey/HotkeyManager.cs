using System.Runtime.InteropServices;

namespace DesktopPet.Infra.Hotkey;

/// <summary>全局快捷键动作（迁移计划 §5 Phase 6 ④）。</summary>
public enum HotkeyAction
{
    TogglePets,    // Ctrl+Alt+H：全局显隐宠物
    ToggleMode,    // Ctrl+Alt+M：切换输出模式
    OpenSettings,  // Ctrl+Alt+S：打开设置
    Quit,          // Ctrl+Alt+Q：退出应用
}

/// <summary>RegisterHotKey 底层抽象（测试注入内存实现；真注册走 Win32）。</summary>
public interface IHotkeyRegistration
{
    bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    bool Unregister(IntPtr hwnd, int id);
}

/// <summary>Win32 实现（user32 RegisterHotKey/UnregisterHotKey）。</summary>
public sealed class Win32HotkeyRegistration : IHotkeyRegistration
{
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
        => RegisterHotKey(hwnd, id, modifiers, virtualKey);

    public bool Unregister(IntPtr hwnd, int id)
        => UnregisterHotKey(hwnd, id);
}

/// <summary>
/// 全局快捷键管理器：id 分配（0xC000-0xFFFF 私有范围）、幂等重注册、WM_HOTKEY 分发解析。
/// WPF 侧在消息钩子中收到 WM_HOTKEY 后调用 <see cref="Resolve"/> 得到动作。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModControlAlt = ModControl | ModAlt;

    private const int FirstId = 0xC000;

    private readonly IntPtr _hwnd;
    private readonly IHotkeyRegistration _registration;
    private readonly Dictionary<int, HotkeyAction> _byId = new();
    private readonly Dictionary<HotkeyAction, int> _byAction = new();
    private int _nextId = FirstId;

    public HotkeyManager(IntPtr hwnd, IHotkeyRegistration? registration = null)
    {
        _hwnd = hwnd;
        _registration = registration ?? new Win32HotkeyRegistration();
    }

    /// <summary>注册动作（同一动作重注册 = 先注销旧 id 再注册，幂等）。失败返回 false 且不留映射。</summary>
    public bool Register(HotkeyAction action, uint modifiers, uint virtualKey)
    {
        if (_byAction.TryGetValue(action, out var oldId))
        {
            _registration.Unregister(_hwnd, oldId);
            _byId.Remove(oldId);
            _byAction.Remove(action);
        }

        var id = _nextId++;
        if (!_registration.Register(_hwnd, id, modifiers, virtualKey)) return false;

        _byId[id] = action;
        _byAction[action] = id;
        return true;
    }

    public void Unregister(HotkeyAction action)
    {
        if (!_byAction.TryGetValue(action, out var id)) return;
        _registration.Unregister(_hwnd, id);
        _byId.Remove(id);
        _byAction.Remove(action);
    }

    public void UnregisterAll()
    {
        foreach (var id in _byId.Keys.ToArray())
            _registration.Unregister(_hwnd, id);
        _byId.Clear();
        _byAction.Clear();
    }

    /// <summary>WM_HOTKEY 的 wParam（热键 id）→ 动作；未注册返回 null。</summary>
    public HotkeyAction? Resolve(int wParam)
        => _byId.TryGetValue(wParam, out var action) ? action : null;

    public void Dispose() => UnregisterAll();
}

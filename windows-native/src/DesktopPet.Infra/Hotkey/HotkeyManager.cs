using System.Runtime.InteropServices;
using DesktopPet.Core.Hotkeys;

namespace DesktopPet.Infra.Hotkey;

/// <summary>结果包含 Win32 错误码，避免把 RegisterHotKey 失败降级成静默部分启用。</summary>
public readonly record struct HotkeyNativeResult(bool Success, int ErrorCode)
{
    public static HotkeyNativeResult Ok => new(true, 0);
    public static HotkeyNativeResult Failed(int errorCode) => new(false, errorCode);
}

public interface IHotkeyRegistration
{
    HotkeyNativeResult Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    HotkeyNativeResult Unregister(IntPtr hwnd, int id);
}

public interface IHotkeyRuntime
{
    HotkeyApplyResult TryReplaceAll(HotkeySettings candidate);
}

public sealed record HotkeyApplyResult(
    bool Success,
    string Phase,
    HotkeyAction? FailedAction,
    int NativeError,
    bool RollbackComplete,
    IReadOnlyList<HotkeyValidationIssue> ValidationIssues,
    HotkeySettings ActiveSettings)
{
    public static HotkeyApplyResult ValidationFailure(
        IReadOnlyList<HotkeyValidationIssue> issues,
        HotkeySettings active)
        => new(false, "validation", null, 0, true, issues, active);

    public static HotkeyApplyResult NativeFailure(
        string phase,
        HotkeyAction? action,
        int error,
        bool rollbackComplete,
        HotkeySettings active)
        => new(false, phase, action, error, rollbackComplete, [], active);
}

/// <summary>Win32 实现（user32 RegisterHotKey/UnregisterHotKey）。</summary>
public sealed class Win32HotkeyRegistration : IHotkeyRegistration
{
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public HotkeyNativeResult Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
    {
        var success = RegisterHotKey(hwnd, id, modifiers, virtualKey);
        return success
            ? HotkeyNativeResult.Ok
            : HotkeyNativeResult.Failed(Marshal.GetLastWin32Error());
    }

    public HotkeyNativeResult Unregister(IntPtr hwnd, int id)
    {
        var success = UnregisterHotKey(hwnd, id);
        return success
            ? HotkeyNativeResult.Ok
            : HotkeyNativeResult.Failed(Marshal.GetLastWin32Error());
    }
}

/// <summary>
/// 全局快捷键管理器。变更单位是四动作完整集合，使用补偿事务替换；
/// Win32 没有批量原子 API，因此回滚不完整必须被调用方观察到。
/// </summary>
public sealed class HotkeyManager : IHotkeyRuntime, IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWindows = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    private const int FirstId = 0xC000;
    private const int IdRange = 0x4000;

    private sealed record ActiveBinding(int Id, HotkeyAction Action, HotkeyGesture Gesture);

    private readonly object _sync = new();
    private readonly IntPtr _hwnd;
    private readonly IHotkeyRegistration _registration;
    private readonly Dictionary<int, ActiveBinding> _byId = new();
    private int _nextId = FirstId;
    private bool _disposed;

    public HotkeyManager(IntPtr hwnd, IHotkeyRegistration? registration = null)
    {
        _hwnd = hwnd;
        _registration = registration ?? new Win32HotkeyRegistration();
    }

    public HotkeySettings CurrentSettings
    {
        get { lock (_sync) return SnapshotSettings_NoLock(); }
    }

    public HotkeyApplyResult TryReplaceAll(HotkeySettings candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_sync)
        {
            if (_disposed)
                return HotkeyApplyResult.NativeFailure("disposed", null, 0, false, SnapshotSettings_NoLock());

            var issues = candidate.Validate();
            if (issues.Count > 0)
                return HotkeyApplyResult.ValidationFailure(issues, SnapshotSettings_NoLock());

            var oldBindings = _byId.Values.ToArray();
            var removedOld = new List<ActiveBinding>();
            foreach (var binding in oldBindings)
            {
                var native = _registration.Unregister(_hwnd, binding.Id);
                if (native.Success)
                {
                    _byId.Remove(binding.Id);
                    removedOld.Add(binding);
                    continue;
                }

                var restored = RestoreBindings_NoLock(removedOld);
                return HotkeyApplyResult.NativeFailure(
                    "unregister", binding.Action, native.ErrorCode,
                    restored, SnapshotSettings_NoLock());
            }

            var added = new List<ActiveBinding>();
            foreach (var (action, gesture) in candidate.Enumerate())
            {
                if (gesture is null) continue;
                var id = AllocateId_NoLock();
                var native = _registration.Register(
                    _hwnd,
                    id,
                    ToNativeModifiers(gesture.Modifiers) | ModNoRepeat,
                    gesture.VirtualKey);
                if (!native.Success)
                {
                    var cleanup = UnregisterBindings_NoLock(added);
                    var restored = RestoreBindings_NoLock(oldBindings);
                    return HotkeyApplyResult.NativeFailure(
                        "register", action, native.ErrorCode,
                        cleanup && restored, SnapshotSettings_NoLock());
                }

                var binding = new ActiveBinding(id, action, gesture);
                _byId[id] = binding;
                added.Add(binding);
            }

            return new HotkeyApplyResult(true, "committed", null, 0, true, [], SnapshotSettings_NoLock());
        }
    }

    public HotkeyAction? Resolve(int id)
    {
        lock (_sync)
        {
            return _byId.TryGetValue(id, out var binding) ? binding.Action : null;
        }
    }

    public void UnregisterAll()
    {
        lock (_sync)
        {
            foreach (var binding in _byId.Values.ToArray())
            {
                var native = _registration.Unregister(_hwnd, binding.Id);
                if (native.Success) _byId.Remove(binding.Id);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            UnregisterAll_NoLock();
            _disposed = true;
        }
    }

    private int AllocateId_NoLock()
    {
        for (var attempt = 0; attempt < IdRange; attempt++)
        {
            var offset = (_nextId - FirstId + attempt) % IdRange;
            var id = FirstId + offset;
            if (_byId.ContainsKey(id)) continue;
            _nextId = FirstId + ((offset + 1) % IdRange);
            return id;
        }
        throw new InvalidOperationException("No global hotkey ids available");
    }

    private bool UnregisterBindings_NoLock(IEnumerable<ActiveBinding> bindings)
    {
        var complete = true;
        foreach (var binding in bindings)
        {
            var native = _registration.Unregister(_hwnd, binding.Id);
            if (native.Success) _byId.Remove(binding.Id);
            else complete = false;
        }
        return complete;
    }

    private bool RestoreBindings_NoLock(IEnumerable<ActiveBinding> bindings)
    {
        var complete = true;
        var activeActions = _byId.Values.Select(binding => binding.Action).ToHashSet();
        foreach (var old in bindings)
        {
            // A failed cleanup may leave a candidate binding for this action alive;
            // do not create a duplicate, and report degraded rollback instead.
            if (activeActions.Contains(old.Action)) continue;
            var id = AllocateId_NoLock();
            var native = _registration.Register(
                _hwnd,
                id,
                ToNativeModifiers(old.Gesture.Modifiers) | ModNoRepeat,
                old.Gesture.VirtualKey);
            if (!native.Success)
            {
                complete = false;
                continue;
            }
            _byId[id] = new ActiveBinding(id, old.Action, old.Gesture);
            activeActions.Add(old.Action);
        }
        return complete && bindings.All(old => activeActions.Contains(old.Action));
    }

    private void UnregisterAll_NoLock()
    {
        foreach (var binding in _byId.Values.ToArray())
        {
            var native = _registration.Unregister(_hwnd, binding.Id);
            if (native.Success) _byId.Remove(binding.Id);
        }
    }

    private HotkeySettings SnapshotSettings_NoLock()
        => new(
            FindGesture_NoLock(HotkeyAction.TogglePets),
            FindGesture_NoLock(HotkeyAction.ToggleMode),
            FindGesture_NoLock(HotkeyAction.OpenSettings),
            FindGesture_NoLock(HotkeyAction.Quit));

    private HotkeyGesture? FindGesture_NoLock(HotkeyAction action)
        => _byId.Values.FirstOrDefault(binding => binding.Action == action)?.Gesture;

    private static uint ToNativeModifiers(HotkeyModifiers modifiers)
    {
        var result = 0u;
        if ((modifiers & HotkeyModifiers.Alt) != 0) result |= ModAlt;
        if ((modifiers & HotkeyModifiers.Control) != 0) result |= ModControl;
        if ((modifiers & HotkeyModifiers.Shift) != 0) result |= ModShift;
        if ((modifiers & HotkeyModifiers.Windows) != 0) result |= ModWindows;
        return result;
    }
}

using System.Runtime.InteropServices;

namespace DesktopPet.App.Interop;

/// <summary>原生窗口 API 薄封装（P/Invoke）。</summary>
internal static partial class NativeMethods
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public static void MoveWindow(nint hwnd, int x, int y)
        => SetWindowPos(hwnd, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    public static void ActivateWindow(nint hwnd)
    {
        if (hwnd != 0) SetForegroundWindow(hwnd);
    }

    /// <summary>窗口消息的时间戳（系统 ms），用于拖拽延迟采样。</summary>
    [LibraryImport("user32.dll")]
    private static partial int GetMessageTime();

    public static int MessageTime() => GetMessageTime();

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    private const int SmCxWorkArea = 0;
    private const int SmCyWorkArea = 1;

    /// <summary>主屏 work area 物理尺寸（对齐 Rust primary_work_area）。</summary>
    public static (int Width, int Height) PrimaryWorkAreaSize()
        => (GetSystemMetrics(SmCxWorkArea), GetSystemMetrics(SmCyWorkArea));

    /// <summary>窗口左上角物理像素位置。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, ref RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    // ---- 系统窗口枚举（climb 漫游环境，对齐 Rust sys_windows.rs）----

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder buffer, int maxCount);

    /// <summary>枚举可见顶层窗口（非空标题、尺寸 >40x40、排除自身进程），物理坐标。</summary>
    public static List<(string Title, int X, int Y, int Width, int Height)> EnumerateVisibleWindows(uint excludeProcessId)
    {
        var result = new List<(string, int, int, int, int)>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var rect = new RECT();
            if (!GetWindowRect(hWnd, ref rect)) return true;
            var w = rect.Right - rect.Left;
            var h = rect.Bottom - rect.Top;
            if (w < 40 || h < 40) return true; // 跳过零尺寸/最小化

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == excludeProcessId) return true; // 排除自己的窗口（不爬自己）

            var title = new System.Text.StringBuilder(512);
            var len = GetWindowText(hWnd, title, title.Capacity);
            if (len <= 0) return true; // 仅保留非空标题（过滤工具提示/IME 栏）

            result.Add((title.ToString(), rect.Left, rect.Top, w, h));
            return true;
        }, 0);
        return result;
    }
}

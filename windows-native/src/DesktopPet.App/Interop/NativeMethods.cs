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
}

using System;
using System.Runtime.InteropServices;

public static class BenchInput
{
    static BenchInput()
    {
        // 与 App 统一物理像素坐标系（PowerShell 默认 DPI-unaware，坐标会被缩放）
        SetProcessDPIAware();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    static void Send(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flags } };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void Down() { Send(MOUSEEVENTF_LEFTDOWN); }
    public static void Up() { Send(MOUSEEVENTF_LEFTUP); }
    public static void Move(int x, int y)
    {
        // 桌面上下文瞬时不可用时重试一次（远程/锁屏会话偶发）
        var attempt = 0;
        while (true)
        {
            if (SetCursorPos(x, y)) return;
            attempt++;
            if (attempt > 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            System.Threading.Thread.Sleep(500);
        }
    }

    /// <summary>
    /// 精确节流的拖拽 move 序列（PowerShell 循环调用开销会注入抖动，伪造队列积压）。
    /// intervalMs 默认 2ms ≈ 高轮询游戏鼠标的输入节奏。
    /// </summary>
    public static void DragMoves(int startX, int startY, int steps, int stepDx, int stepDy, int intervalMs = 2)
    {
        for (var i = 1; i <= steps; i++)
        {
            if (!SetCursorPos(startX + i * stepDx, startY + i * stepDy))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            System.Threading.Thread.Sleep(intervalMs);
        }
    }
}

using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DesktopPet.App.Fullscreen;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class FullscreenBoundsEvaluator
{
    public static bool CoversMonitor(
        ScreenRect window,
        ScreenRect monitor,
        bool maximized,
        int tolerance = 2)
    {
        if (window.Width <= 0 || window.Height <= 0) return false;
        if (maximized) return true;
        return Math.Abs(window.Left - monitor.Left) <= tolerance
            && Math.Abs(window.Top - monitor.Top) <= tolerance
            && Math.Abs(window.Right - monitor.Right) <= tolerance
            && Math.Abs(window.Bottom - monitor.Bottom) <= tolerance;
    }
}

public static class FullscreenOutputPolicy
{
    public static bool ShouldSuppress(
        bool isProactive,
        bool monitorSuppressed,
        bool currentlyFullscreen)
        => isProactive && (monitorSuppressed || currentlyFullscreen);
}

public sealed class FullscreenWindowDetector
{
    private const uint MonitorDefaultToNearest = 2;
    private readonly int _ownProcessId;

    public FullscreenWindowDetector(int? ownProcessId = null)
    {
        _ownProcessId = ownProcessId ?? Environment.ProcessId;
    }

    public bool IsSuppressed()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == _ownProcessId) return false;

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        if (!GetWindowRect(foreground, out var windowRect)) return false;
        var dpi = GetDpiForWindow(foreground);
        var tolerance = Math.Max(2, (int)Math.Round(2d * dpi / 96d));
        var window = new ScreenRect(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
        var bounds = new ScreenRect(
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right,
            info.Monitor.Bottom);
        return FullscreenBoundsEvaluator.CoversMonitor(window, bounds, IsZoomed(foreground), tolerance);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}

public sealed class FullscreenSuppressionMonitor : IDisposable
{
    private readonly Func<bool> _detect;
    private readonly Action<bool> _publish;
    private readonly TimeSpan _interval;
    private DispatcherTimer? _timer;
    private bool? _lastValue;
    private bool _disposed;

    public FullscreenSuppressionMonitor(
        Func<bool> detect,
        Action<bool> publish,
        TimeSpan? interval = null)
    {
        _detect = detect;
        _publish = publish;
        _interval = interval ?? TimeSpan.FromMilliseconds(250);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public bool IsRunning => _timer?.IsEnabled == true;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = _interval };
        _timer.Tick += OnTick;
        _timer.Start();
        Poll();
    }

    public void Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var value = _detect();
        if (_lastValue == value) return;
        _lastValue = value;
        _publish(value);
    }

    public void Stop()
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    private void OnTick(object? sender, EventArgs e) => Poll();
}

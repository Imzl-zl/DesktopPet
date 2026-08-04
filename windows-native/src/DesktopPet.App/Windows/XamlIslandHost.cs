using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.UI.Xaml.Hosting;

namespace DesktopPet.App.Windows;

/// <summary>
/// WPF HwndHost 承载 WinUI3 XAML Island（弹幕层 Win2D 渲染宿主）。
/// PoC 已验证：透明置顶窗口 + CanvasAnimatedControl 正常合成（WinAppSDK 1.6）。
/// 注意：必须在 STA + 消息泵线程创建（WPF 主线程满足）。
/// </summary>
public sealed class XamlIslandHost : HwndHost
{
    private readonly Func<Microsoft.UI.Xaml.UIElement> _contentFactory;
    private DesktopWindowXamlSource? _source;

    public XamlIslandHost(Func<Microsoft.UI.Xaml.UIElement> contentFactory)
    {
        _contentFactory = contentFactory;
    }

    public void AttachAndInitialize()
    {
        if (_source is not null) return;
        _source = new DesktopWindowXamlSource();
        _source.Content = _contentFactory();
        _source.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(Handle));
        ResizeIsland();
    }

    public void ResizeIsland()
    {
        if (_source?.SiteBridge is null) return;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        _source.SiteBridge.MoveAndResize(new global::Windows.Graphics.RectInt32(
            0, 0,
            (int)(ActualWidth * scale),
            (int)(ActualHeight * scale)));
    }

    public void DetachAndDispose()
    {
        _source?.Dispose();
        _source = null;
    }

    protected override void OnRenderSizeChanged(System.Windows.SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ResizeIsland();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int wsChild = 0x40000000;
        const int wsVisible = 0x10000000;
        const int wsClipSiblings = 0x04000000;
        var hwnd = CreateWindowEx(0, "static", "", wsChild | wsVisible | wsClipSiblings,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
        => DestroyWindow(hwnd.Handle);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}

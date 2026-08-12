using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace DesktopPet.App.Tray;

/// <summary>
/// Opens the tray menu from the current WPF mouse position instead of the
/// coordinate packed into the Shell notification callback.
/// </summary>
internal sealed class TrayContextMenuPresenter : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly ContextMenu _menu;
    private readonly Action<nint> _activateWindow;
    private bool _disposed;

    public TrayContextMenuPresenter(
        TaskbarIcon icon,
        ContextMenu menu,
        Action<nint> activateWindow)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(activateWindow);

        _icon = icon;
        _menu = menu;
        _activateWindow = activateWindow;

        _icon.MenuActivation = PopupActivationMode.None;
        _icon.TrayRightMouseUp += OnTrayRightMouseUp;
    }

    private void OnTrayRightMouseUp(object sender, RoutedEventArgs e)
    {
        _menu.Placement = PlacementMode.MousePoint;
        _menu.HorizontalOffset = 0;
        _menu.VerticalOffset = 0;
        _menu.IsOpen = true;

        var handle = (PresentationSource.FromVisual(_menu) as HwndSource)?.Handle
            ?? _icon.TrayIcon.WindowHandle;
        _activateWindow(handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.TrayRightMouseUp -= OnTrayRightMouseUp;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DesktopPet.App.Tray;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace DesktopPet.App.Tests;

public sealed class TrayContextMenuPresenterTests
{
    [Fact]
    public void RightClickUsesCurrentMousePositionInsteadOfLibraryCallbackCoordinates()
        => RunSta(() =>
        {
            using var icon = new TaskbarIcon();
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Settings" });
            var activationCount = 0;
            nint lastHandle = 0;
            using var presenter = new TrayContextMenuPresenter(
                icon,
                menu,
                handle =>
                {
                    activationCount++;
                    lastHandle = handle;
                });

            Assert.Equal(PopupActivationMode.None, icon.MenuActivation);

            icon.RaiseEvent(new RoutedEventArgs(TaskbarIcon.TrayRightMouseUpEvent));

            Assert.Equal(PlacementMode.MousePoint, menu.Placement);
            Assert.Equal(0, menu.HorizontalOffset);
            Assert.Equal(0, menu.VerticalOffset);
            Assert.True(menu.IsOpen);
            Assert.Equal(1, activationCount);
            Assert.NotEqual(0, lastHandle);

            menu.IsOpen = false;
        });

    [Fact]
    public void DisposeStopsHandlingTrayRightClicks()
        => RunSta(() =>
        {
            using var icon = new TaskbarIcon();
            var menu = new ContextMenu();
            var activationCount = 0;
            var presenter = new TrayContextMenuPresenter(
                icon,
                menu,
                _ => activationCount++);
            presenter.Dispose();

            icon.RaiseEvent(new RoutedEventArgs(TaskbarIcon.TrayRightMouseUpEvent));

            Assert.False(menu.IsOpen);
            Assert.Equal(0, activationCount);
        });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}

using System.Windows;
using System.Windows.Controls;
using DesktopPet.App.Interop;
using DesktopPet.App.Localization;
using DesktopPet.App.Windows;
using DesktopPet.Core.I18n;
using H.NotifyIcon;

namespace DesktopPet.App.Tray;

/// <summary>
/// 托盘（H.NotifyIcon）：全局显示/隐藏宠物（check 项，对齐 Rust set_desktop_pets_visible
/// 的菜单同步语义）+ 退出。图标为程序生成的像素猫头（Fluent 风格简化版）。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _toggleItem;
    private readonly ContextMenu _menu;
    private readonly TrayContextMenuPresenter _menuPresenter;

    public TrayController(PetWindowManager manager, I18nService? i18n = null)
    {
        _icon = new TaskbarIcon
        {
            Icon = CreateIcon(),
            ToolTipText = "DesktopPet",
        };

        _toggleItem = new MenuItem { Header = "显示/隐藏宠物", IsCheckable = true, IsChecked = manager.GloballyVisible };
        _toggleItem.Click += (_, _) => manager.SetGlobalVisible(_toggleItem.IsChecked);

        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => manager.OpenSettings();

        var quitItem = new MenuItem { Header = "退出" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);
        _menu = menu;
        // ContextMenu 属性仅作库侧资源引用（MenuActivation=None 后库不再自动打开）；
        // 菜单的实际打开由 TrayContextMenuPresenter 在 TrayRightMouseUp 时按 MousePoint 负责。
        _icon.ContextMenu = menu;
        _menuPresenter = new TrayContextMenuPresenter(_icon, menu, NativeMethods.ActivateWindow);
        // H.NotifyIcon 2.1.4：代码方式创建（非 XAML 视觉树）不会自动创建托盘图标——
        // Shell_NotifyIcon 只由 ForceCreate 触发（Loaded 事件只在加入视觉树后发生）。
        // 缺失时托盘入口（设置/显示隐藏/退出）整体失效。
        _icon.ForceCreate(false);
        WpfLocalizer.ApplyNew(menu, i18n ?? new I18nService());

        manager.GlobalVisibilityChanged += OnGlobalVisibilityChanged;
    }

    public void ApplyLocalization(I18nService i18n)
        => WpfLocalizer.RefreshTracked(_menu, i18n);

    private void OnGlobalVisibilityChanged(bool visible)
    {
        // 勾选状态同步（含非托盘触发的全局显隐变化）
        _toggleItem.IsChecked = visible;
    }

    /// <summary>托盘图标：嵌入资源 app.ico（与 exe 同源，系统按 DPI 选帧）。</summary>
    private static System.Drawing.Icon CreateIcon() => AppIcons.TrayIcon();

    public void Dispose()
    {
        _menuPresenter.Dispose();
        _icon.Dispose();
    }
}

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
        _icon.ContextMenu = menu;
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

    /// <summary>程序生成的像素猫头托盘图标（H.NotifyIcon 需要 System.Drawing.Icon）。</summary>
    private static System.Drawing.Icon CreateIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var head = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x5B, 0x8D, 0xE0));
            g.FillEllipse(head, 2, 2, 28, 28);
            using var eye = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.FillEllipse(eye, 9, 10, 5, 7);
            g.FillEllipse(eye, 19, 10, 5, 7);
        }
        var handle = bitmap.GetHicon();
        using var fromHandle = System.Drawing.Icon.FromHandle(handle);
        var icon = (System.Drawing.Icon)fromHandle.Clone(); // 独立句柄，可安全 Dispose
        // FromHandle 不接管所有权：官方要求用 DestroyIcon 释放原句柄（Icon.FromHandle Remarks）
        NativeMethods.DestroyIconHandle(handle);
        return icon;
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}

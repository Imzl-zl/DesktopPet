using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopPet.App.Localization;
using DesktopPet.Core.Danmaku;
using DesktopPet.Core.I18n;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;

// CS8305：CanvasAnimatedControl 被 Win2D 标记为实验性（evaluation only）。
// PoC 已验证其在 WPF XAML Island 中工作正常（透明窗口 + GPU 渲染 + Paused 停转），
// 且自带渲染循环优于 CanvasControl + 外部驱动，故定点抑制。
#pragma warning disable CS8305

namespace DesktopPet.App.Windows;

/// <summary>
/// 弹幕层（迁移计划 §6.5 / 验收：60fps）：独立全屏透明置顶窗口，
/// IsHitTestVisible=false + WS_EX_TRANSPARENT 点击穿透；Win2D GPU 合成滚动文本。
/// 无弹幕时暂停渲染循环（CPU 归零）；帧计数暴露给验收冒烟。
/// 模式切换即销毁（ModeService 管理生命周期）。
/// </summary>
public sealed class DanmakuWindow : Window
{
    private readonly DanmakuEngine _engine;
    private readonly double _trackHeight;
    private readonly CanvasTextFormat _textFormat = new()
    {
        FontSize = 30,
        FontFamily = "Microsoft YaHei UI",
        WordWrapping = CanvasWordWrapping.NoWrap,
    };
    private XamlIslandHost? _island;
    private CanvasAnimatedControl? _canvas;
    private long _frameCount;
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private I18nService _i18n;
    private double _fps;

    /// <summary>最近 1s 帧率（验收 60fps 采样）。</summary>
    public double Fps => _fps;

    public DanmakuWindow(
        double width,
        double height,
        int trackCount = 10,
        I18nService? i18n = null)
    {
        _i18n = i18n ?? new I18nService();
        Title = _i18n.T("DesktopPet Danmaku");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Left = 0;
        Top = 0;
        Width = width;
        Height = height;
        _engine = new DanmakuEngine(width, trackCount, minSpeed: 220, maxSpeed: 420, minGap: 220);
        _trackHeight = height / Math.Max(1, trackCount);

        _island = new XamlIslandHost(BuildCanvas);
        Content = _island;
        WpfLocalizer.ApplyNew(this, _i18n);
        // XAML Island 必须在窗口稳定（Loaded）后 Attach（Show 中途初始化报窗口线程归属错误）
        Loaded += (_, _) =>
        {
            EnsureWinUiInitialized(); // 必须先于 DesktopWindowXamlSource 创建
            _island.AttachAndInitialize();
            _fpsTimer.Tick -= OnFpsTimerTick;
            _fpsTimer.Tick += OnFpsTimerTick;
            _fpsTimer.Start();
        };
    }

    private static bool _winuiInitialized;
    private static readonly object WinUiInitLock = new();

    /// <summary>WinAppSDK 初始化（DispatcherQueue + XAML manager；每线程一次）。</summary>
    private static void EnsureWinUiInitialized()
    {
        if (_winuiInitialized) return;
        lock (WinUiInitLock)
        {
            if (_winuiInitialized) return;
            _ = Microsoft.UI.Dispatching.DispatcherQueueController.CreateOnCurrentThread();
            _ = Microsoft.UI.Xaml.Hosting.WindowsXamlManager.InitializeForCurrentThread();
            _winuiInitialized = true;
        }
    }

    public void ApplyLocalization(I18nService i18n)
    {
        _i18n = i18n;
        WpfLocalizer.RefreshTracked(this, i18n);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 点击穿透：透明 + 不激活（不挡鼠标、不抢焦点）
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */);
        SetWindowLongPtr(hwnd, -20, exStyle | 0x00000020 /* WS_EX_TRANSPARENT */ | 0x08000000 /* WS_EX_NOACTIVATE */);
    }

    private Microsoft.UI.Xaml.UIElement BuildCanvas()
    {
        var grid = new Microsoft.UI.Xaml.Controls.Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
        };
        _canvas = new CanvasAnimatedControl
        {
            Width = (float)ActualWidth,
            Height = (float)ActualHeight,
            Paused = true, // 无弹幕不渲染（CPU 归零）
        };
        _canvas.Update += (_, args) =>
        {
            if (!_canvas.Paused
                && !_engine.Tick(args.Timing.ElapsedTime.TotalSeconds))
            {
                _canvas.Paused = true;
            }
        };
        _canvas.Draw += (_, args) =>
        {
            args.DrawingSession.Clear(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            var ds = args.DrawingSession;
            foreach (var item in _engine.Active)
            {
                ds.DrawText(item.Text, (float)item.X, (float)(item.Track * _trackHeight),
                    global::Microsoft.UI.Colors.White, _textFormat);
            }
            _frameCount++;
        };
        grid.Children.Add(_canvas);
        return grid;
    }

    /// <summary>推送一条弹幕（事件驱动；唤醒渲染循环）。</summary>
    public void ShowDanmaku(string text)
    {
        if (_engine.Enqueue(text, DateTime.Now) is not null && _canvas is not null)
        {
            _canvas.Paused = false;
        }
    }

    private void OnFpsTimerTick(object? sender, EventArgs e)
    {
        _fps = _frameCount;
        _frameCount = 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        _fpsTimer.Stop();
        _fpsTimer.Tick -= OnFpsTimerTick;
        var canvas = _canvas;
        _canvas = null;
        if (canvas is not null)
        {
            canvas.Paused = true;
            canvas.RemoveFromVisualTree();
        }
        _engine.Clear();
        _textFormat.Dispose();
        _island?.DetachAndDispose();
        _island = null;
        base.OnClosed(e);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle);
}

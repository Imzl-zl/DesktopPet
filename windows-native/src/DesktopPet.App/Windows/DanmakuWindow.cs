using System.Numerics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopPet.App.Interop;
using DesktopPet.App.Localization;
using DesktopPet.Core.Danmaku;
using DesktopPet.Core.I18n;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
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
/// 帧计数暴露给验收冒烟。模式切换即销毁（ModeService 管理生命周期）。
///
/// 渲染循环生命周期（2026-08 架构决策）：**不写 CanvasAnimatedControl.Paused**。
/// 证据链：Win2D 1.3.0 在 WinUI 3 下 Paused 暂停→恢复不可靠（微软 Win2D#973
/// "once paused could no longer be unpaused"；microsoft-ui-reactor 实测切换 Paused
/// 会唤醒游戏线程一个 tick 后永久停驻；官方 API 标注 CanvasAnimatedControl 在
/// WinUI3 不受支持）。旧实现"出屏即 Paused=true、下一条再唤醒"导致第二次弹幕
/// 卡住（恢复失败），极端情况下恢复尝试触发进程无日志崩溃。
/// 现在：游戏循环常跑（Update 空转成本≈0，Draw 空时只清空透明），
/// CPU 归零由窗口级空闲回收承担——超过 IdleCloseTimeoutMs 无新弹幕且无活跃条目
/// → 窗口自关（canvas Unloaded → 游戏线程停止）；下一次输出由 ModeService 按需重建。
///
/// 文本绘制（2026-08 修正）：原文本阴影实现号称「文本只 shaping 一次进 CommandList」，
/// 实际是 Draw 回调内每条弹幕都重建 CanvasCommandList + DrawText——每帧 N 条弹幕
/// 就是 N 次 shaping，随弹幕量线性逼近 16ms 帧预算。现改为按文本缓存
/// (CommandList + ShadowEffect)，仅首次出现的一次 shaping；缓存只由 Win2D 渲染回调
/// 线程访问（无锁），超过容量时最久未用条目批量销毁（同线程，无并发释放风险）。
/// </summary>
public sealed class DanmakuWindow : Window
{
    /// <summary>空闲回收：超过该时长无新弹幕且无活跃条目 → 窗口自关（CPU 归零）。</summary>
    private const int IdleCloseTimeoutMs = 15_000;
    /// <summary>单帧 delta 上限：防系统卡顿/长时间挂起恢复后弹幕瞬移出屏。</summary>
    private const double MaxFrameDeltaSeconds = 0.1;
    /// <summary>文本绘制缓存上限（key=文本）。超额时一次淘汰到容量以下；弹幕文本量级小，128 上限足够。</summary>
    private const int TextCacheCapacity = 128;

    private readonly DanmakuEngine _engine;
    private readonly double _trackHeight;
    private readonly CanvasTextFormat _textFormat;
    private XamlIslandHost? _island;
    private CanvasAnimatedControl? _canvas;
    /// <summary>按文本缓存的绘制资源（CommandList + ShadowEffect）。仅渲染线程访问，防跨线程 Dispose/复用冲突。</summary>
    private readonly Dictionary<string, CachedTextDraw> _textCache = new();
    private long _frameCount;
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastEnqueueMs; // 最近一次入队（UI 线程写，idle 定时器 UI 线程读）
    private I18nService _i18n;
    private double _fps;

    /// <summary>最近 1s 帧率（验收 60fps 采样）。</summary>
    public double Fps => _fps;

    public DanmakuWindow(
        double width,
        double height,
        int trackCount = 10,
        I18nService? i18n = null,
        int fontSize = 30,
        int speedPercent = 100)
    {
        _i18n = i18n ?? new I18nService();
        Title = _i18n.T("DesktopPet Danmaku");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // 多屏覆盖：副屏在主屏左/上时 VirtualScreen* 为负，(0,0) 起锚会让那一侧完全没有弹幕。
        // VirtualScreen* 是 WPF DIP 值（主屏 DPI 基准），与窗口 Left/Top 单位一致。
        Left = System.Windows.SystemParameters.VirtualScreenLeft;
        Top = System.Windows.SystemParameters.VirtualScreenTop;
        Width = width;
        Height = height;
        // 设置页气泡页弹幕参数：字号 16-48、速度 50-200%（乘到引擎速度区间）、轨道数 4-20
        var speedScale = Math.Clamp(speedPercent, 50, 200) / 100.0;
        _textFormat = new CanvasTextFormat
        {
            FontSize = Math.Clamp(fontSize, 16, 48),
            FontFamily = "Microsoft YaHei UI",
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        var clampedTracks = Math.Clamp(trackCount, 4, 20);
        _engine = new DanmakuEngine(
            width,
            clampedTracks,
            minSpeed: 220 * speedScale,
            maxSpeed: 420 * speedScale,
            minGap: 220);
        _trackHeight = height / Math.Max(1, clampedTracks);

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
            _idleTimer.Tick -= OnIdleTimerTick;
            _idleTimer.Tick += OnIdleTimerTick;
            _idleTimer.Start();
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
        const int gwlExStyle = -20;
        const nint wsExTransparent = 0x00000020;
        const nint wsExNoActivate = 0x08000000;
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, gwlExStyle);
        NativeMethods.SetWindowLongPtr(hwnd, gwlExStyle, exStyle | wsExTransparent | wsExNoActivate);
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
        };
        // 官方（Win2D «Handling device lost»）：设备丢失（GPU 驱动重置/远程桌面/全屏独占切换等）后，
        // 控件会重建 CanvasDevice 并触发 CreateResources(Reason=NewDevice)；旧 device 上的缓存资源全部失效，
        // 必须在此清空。下次 Draw 会按新 device 惰性重建（与「缓存只在渲染线程访问」保持一致）。
        _canvas.CreateResources += (_, _) =>
        {
            foreach (var entry in _textCache.Values) entry.Dispose();
            _textCache.Clear();
        };
        _canvas.Update += (_, args) =>
        {
            // 常跑循环：引擎空转（无活跃条目时 Tick 零成本）；
            // delta clamp 防系统卡顿/挂起恢复后弹幕瞬移出屏。
            _engine.Tick(Math.Min(args.Timing.ElapsedTime.TotalSeconds, MaxFrameDeltaSeconds));
        };
        _canvas.Draw += (_, args) =>
        {
            args.DrawingSession.Clear(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            var ds = args.DrawingSession;
            foreach (var item in _engine.Active)
            {
                var cached = GetOrCreateCachedText(item.Text);
                var pos = new Vector2((float)item.X, (float)(item.Track * _trackHeight));
                ds.DrawImage(cached.Shadow, pos);
                ds.DrawImage(cached.Layer, pos);
            }
            Interlocked.Increment(ref _frameCount);
        };
        grid.Children.Add(_canvas);
        return grid;
    }

    /// <summary>推送一条弹幕（事件驱动；刷新空闲回收计时）。</summary>
    public void ShowDanmaku(string text)
    {
        if (_engine.Enqueue(text, DateTime.Now) is not null)
        {
            _lastEnqueueMs = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 空闲回收：超过 IdleCloseTimeoutMs 无新弹幕且无活跃条目 → 窗口自关。
    /// 窗口关闭 → canvas Unloaded → Win2D 游戏线程停止 → CPU 归零（替代旧的
    /// Paused 暂停方案，Win2D 1.3.0 WinUI3 下 Paused 恢复不可靠，见类注释）。
    /// </summary>
    private void OnIdleTimerTick(object? sender, EventArgs e)
    {
        if (Environment.TickCount64 - _lastEnqueueMs >= IdleCloseTimeoutMs
            && _engine.ActiveCount == 0)
        {
            Close();
        }
    }

    /// <summary>
    /// 白字 + 黑阴影（Win2D 官方 TextShadows 模式）：浅色背景（如论坛页面）下仍可读。
    /// 每条文本只 shaping 一次：CommandList + ShadowEffect 按文本缓存，渲染帧只做 2 次
    /// DrawImage 合成（GPU 成本与弹幕条数成正比，与频繁程度无关）。缓存由渲染线程独占，
    /// 超额时一次淘汰最久未用条目（同线程 dispose，无并发释放风险）。
    /// </summary>
    private CachedTextDraw GetOrCreateCachedText(string text)
    {
        if (_textCache.TryGetValue(text, out var cached))
        {
            cached.LastUsedMs = Environment.TickCount64;
            return cached;
        }

        var layer = new CanvasCommandList(_canvas!);
        using (var layerDs = layer.CreateDrawingSession())
        {
            layerDs.DrawText(text, 0, 0, global::Microsoft.UI.Colors.White, _textFormat);
        }
        var entry = new CachedTextDraw
        {
            Layer = layer,
            Shadow = new ShadowEffect
            {
                Source = layer,
                BlurAmount = 3f,
                ShadowColor = global::Microsoft.UI.Colors.Black,
            },
            LastUsedMs = Environment.TickCount64,
        };
        _textCache[text] = entry;
        EvictStaleTextCache();
        return entry;
    }

    /// <summary>容量超额时一次性淘汰到 3/4 水位（摊还成本；弹幕为低热度缓存）。</summary>
    private void EvictStaleTextCache()
    {
        if (_textCache.Count <= TextCacheCapacity) return;
        var target = TextCacheCapacity * 3 / 4;
        foreach (var key in _textCache
                     .OrderBy(pair => pair.Value.LastUsedMs)
                     .Take(_textCache.Count - target)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _textCache[key].Dispose();
            _textCache.Remove(key);
        }
    }

    /// <summary>按文本缓存的 GPU 绘制资源（仅渲染线程创建/销毁）。</summary>
    private sealed class CachedTextDraw : IDisposable
    {
        public required CanvasCommandList Layer { get; init; }
        public required ShadowEffect Shadow { get; init; }
        public long LastUsedMs;

        public void Dispose()
        {
            Shadow.Dispose();
            Layer.Dispose();
        }
    }

    private void OnFpsTimerTick(object? sender, EventArgs e)
    {
        // Win2D Draw 回调（渲染线程）与 UI 线程同时访问 _frameCount：
        // ++ 是非原子 RMW（Microsoft Learn Interlocked Remarks），必须用原子操作
        _fps = Interlocked.Exchange(ref _frameCount, 0);
    }

    protected override void OnClosed(EventArgs e)
    {
        _fpsTimer.Stop();
        _fpsTimer.Tick -= OnFpsTimerTick;
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdleTimerTick;
        var canvas = _canvas;
        _canvas = null;
        if (canvas is not null)
        {
            canvas.RemoveFromVisualTree();
        }
        _engine.Clear();
        // 文本绘制缓存（GPU 资源）；RemoveFromVisualTree 已停渲染循环，此处销毁安全。
        foreach (var entry in _textCache.Values) entry.Dispose();
        _textCache.Clear();
        _textFormat.Dispose();
        _island?.DetachAndDispose();
        _island = null;
        base.OnClosed(e);
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Interop;
using DesktopPet.App.Rendering;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Rendering;

namespace DesktopPet.App.Windows;

/// <summary>
/// 宠物窗口：透明 + 置顶 + 无边框，WriteableBitmap 直写像素 + 帧率自适应
/// 渲染循环（对齐迁移计划 §6.1/§6.2）。拖拽为原生实现：alpha hitTest 命中
/// 才按下 → CaptureMouse → SetWindowPos 直移（无 WPF 布局开销，无 Tauri
/// hit-rect 补丁层）。位置持久化由 manager 回调（物理像素，对齐 Rust
/// pet-positions.json 语义）。
/// </summary>
public sealed class PetWindow : Window
{
    private const double DragThresholdPx = 4;

    private readonly PetInstance _instance;
    private readonly Action<PetWindow, int, int> _onDragFinished;
    private readonly SpriteLoader _spriteLoader;
    private readonly Image _image = new();
    private readonly WriteableBitmap _bitmap;
    private readonly double _dpiScale;
    private readonly int _bufferWidth;
    private readonly int _bufferHeight;

    private PetRenderer? _renderer;
    private bool _spriteLoading;
    private readonly DispatcherTimer _animationTimer;
    private int _frameIndex;
    private bool _animationEnabled = true;

    // 拖拽状态（对齐 windows/src/window-drag.ts 语义：阈值区分点击/拖拽）
    private bool _pressed;
    private bool _dragging;
    private (int X, int Y) _pressPoint;
    private int _pressMessageTime;
    private long _pressTickMs;
    private (int X, int Y) _grabOffset;
    private nint _hwnd;

    // 拖拽延迟采样（bench 用）：处理耗时（消息到达 → SetWindowPos 完成）+
    // 端到端（消息时间戳 → 完成，含系统队列等待）
    private readonly List<double> _processingLatencyMs = [];
    private readonly List<double> _endToEndLatencyMs = [];

    public string PetId => _instance.Id;

    public IReadOnlyList<double> ProcessingLatencySamples => _processingLatencyMs;

    public IReadOnlyList<double> EndToEndLatencySamples => _endToEndLatencyMs;

    /// <summary>静止时停掉渲染循环（bench-idle / 全局隐藏时置 false，CPU 归零）。</summary>
    public bool AnimationEnabled
    {
        get => _animationEnabled;
        set
        {
            if (_animationEnabled == value) return;
            _animationEnabled = value;
            if (value) RestartAnimation();
            else _animationTimer.Stop();
        }
    }

    public PetWindow(PetInstance instance, SpriteLoader spriteLoader, Action<PetWindow, int, int> onDragFinished)
    {
        _instance = instance;
        _spriteLoader = spriteLoader;
        _onDragFinished = onDragFinished;

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        ShowActivated = false;
        Width = 260;
        Height = 320;

        _dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _bufferWidth = (int)(260 * _dpiScale);
        _bufferHeight = (int)(320 * _dpiScale);
        _bitmap = new WriteableBitmap(
            _bufferWidth, _bufferHeight, 96 * _dpiScale, 96 * _dpiScale,
            PixelFormats.Bgra32, null);

        _image.Source = _bitmap;
        _image.Stretch = Stretch.Fill;
        _image.SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        Content = _image;

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 3), // idle 3fps，对齐 pet.ts STATE_FPS
        };
        _animationTimer.Tick += (_, _) => AdvanceFrame();

        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _animationTimer.Stop();
            else RestartAnimation();
        };
    }

    /// <summary>导入自定义精灵：拖 PNG/WebP 文件到宠物窗口 → 切片预览 → 确认导入。</summary>
    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is null || files.Length == 0) return;
        var path = files[0];

        byte[] bytes;
        try
        {
            bytes = await System.IO.File.ReadAllBytesAsync(path);
        }
        catch (Exception)
        {
            return;
        }

        var sheet = SpriteSheet.Decode(bytes, System.IO.Path.GetFileName(path));
        if (sheet is null)
        {
            MessageBox.Show(this, "无法解析精灵图（需要带透明通道的 PNG/WebP，且能检测到帧间隙）", "DesktopPet",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = new SpritePreviewWindow(sheet, bytes, System.IO.Path.GetFileNameWithoutExtension(path))
        {
            Owner = this,
        };
        if (preview.ShowDialog() == true)
        {
            _onImportRequested?.Invoke(bytes, System.IO.Path.GetFileNameWithoutExtension(path));
        }
    }

    private Action<byte[], string>? _onImportRequested;

    /// <summary>导入确认回调（由 PetWindowManager 注入）。</summary>
    public void SetImportHandler(Action<byte[], string> onImportRequested)
    {
        _onImportRequested = onImportRequested;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProcHook);
        DrawFrame(0);
        RestartAnimation();
        LoadSpriteAsync();
    }

    /// <summary>异步加载真实精灵（本地缓存/CDN），完成后切渲染器；失败保持占位。</summary>
    private async void LoadSpriteAsync()
    {
        if (_spriteLoading) return;
        _spriteLoading = true;
        try
        {
            var sheet = await Task.Run(() => _spriteLoader.LoadAsync(_instance.SpriteSlug));
            if (sheet is not null && _hwnd != 0 && IsVisible)
            {
                _renderer = new PetRenderer(sheet);
                _renderer.SetState("idle");
                _animationTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _renderer.Fps);
                DrawFrame(0);
                RestartAnimation();
            }
        }
        catch (Exception)
        {
            // 加载失败保持占位精灵，不影响窗口
        }
        finally
        {
            _spriteLoading = false;
        }
    }

    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int wmMouseMove = 0x0200;
        const int wmLeftDown = 0x0201;
        const int wmLeftUp = 0x0202;
        const nint mkLeftButton = 0x0001;
        switch (msg)
        {
            case wmLeftDown:
                BenchTrace($"raw msg down client={ClientPointOfMessage(lParam)} cursor={NativeMethods.CursorPosition()}");
                OnRawLeftDown(lParam);
                handled = true;
                break;
            case wmMouseMove when _pressed && (wParam.ToInt64() & mkLeftButton) != 0:
                OnRawMove(lParam);
                handled = true;
                break;
            case wmLeftUp:
                BenchTrace($"raw msg up pressed={_pressed}");
                if (_pressed) OnRawLeftUp();
                handled = true;
                break;
        }
        return 0;
    }

    /// <summary>
    /// 消息级拖拽（绕过 WPF 事件系统——WPF 会合并丢弃捕获后的 WM_MOUSEMOVE，
    /// 且事件分发有额外开销）。语义对齐 windows/src/window-drag.ts：
    /// alpha hitTest 命中才按下；4px 阈值区分点击/拖拽；直接 SetWindowPos。
    /// </summary>
    private void OnRawLeftDown(nint lParam)
    {
        if (_dragging || _pressed) return;
        var client = ClientPointOfMessage(lParam);
        if (!HitTestSprite(client)) return;

        _pressed = true;
        _pressPoint = client;
        _pressMessageTime = NativeMethods.MessageTime();
        _pressTickMs = Environment.TickCount64;
        var cursor = NativeMethods.CursorPosition();
        var windowPos = PhysicalPosition();
        _grabOffset = (cursor.X - windowPos.X, cursor.Y - windowPos.Y);
        CaptureMouse();
        BenchTrace($"raw down hit at {client.X},{client.Y}");
    }

    private void OnRawMove(nint lParam)
    {
        if (!_pressed) return;
        var client = ClientPointOfMessage(lParam);
        if (!_dragging && MovedBeyondThreshold(client))
        {
            _dragging = true;
            BenchTrace("raw drag started");
        }
        if (!_dragging) return;

        var cursor = NativeMethods.CursorPosition();
        var targetX = cursor.X - _grabOffset.X;
        var targetY = cursor.Y - _grabOffset.Y;
        var messageTime = NativeMethods.MessageTime();
        var sw = Stopwatch.StartNew();
        NativeMethods.MoveWindow(_hwnd, targetX, targetY);
        sw.Stop();
        _processingLatencyMs.Add(sw.Elapsed.TotalMilliseconds);
        // 端到端：消息生成 → 窗口位移完成（含系统输入管线/队列等待）
        _endToEndLatencyMs.Add(Math.Max(0, Environment.TickCount64 - messageTime));
    }

    private void OnRawLeftUp()
    {
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();

        if (_dragging)
        {
            _dragging = false;
            var (x, y) = PhysicalPosition();
            _onDragFinished(this, x, y);
            BenchTrace($"raw drag finished at {x},{y}");
        }
    }

    /// <summary>WM 消息 lParam 的客户区坐标（物理像素，低 16 位 x，高 16 位 y）。</summary>
    private static (int X, int Y) ClientPointOfMessage(nint lParam)
    {
        var raw = lParam.ToInt64();
        var x = (short)(raw & 0xFFFF);
        var y = (short)((raw >> 16) & 0xFFFF);
        return (x, y);
    }

    private void RestartAnimation()
    {
        _animationTimer.Stop();
        if (_animationEnabled && IsVisible)
        {
            _animationTimer.Start();
        }
    }

    private void AdvanceFrame()
    {
        if (!_animationEnabled || !IsVisible) return;
        if (_renderer is not null)
        {
            _renderer.AdvanceFrame();
            DrawFrame(0);
            var fps = _renderer.Fps;
            var target = TimeSpan.FromMilliseconds(1000.0 / fps);
            if (Math.Abs(_animationTimer.Interval.TotalMilliseconds - target.TotalMilliseconds) > 1)
            {
                _animationTimer.Interval = target; // 帧率随状态行变化（fps 3-8）
            }
        }
        else
        {
            _frameIndex++;
            DrawFrame(_frameIndex % PlaceholderPet.Frames.Count);
        }
    }

    /// <summary>精灵中心物理坐标（bench 拖拽按下点；buffer 即物理像素）。</summary>
    public (int X, int Y) SpriteCenterPhysical
    {
        get
        {
            if (_renderer is not null)
            {
                var (x, y, w, h) = _renderer.SpriteRect;
                return (x + w / 2, y + h / 2);
            }
            var (scale, dx, dy) = SpritePlacement();
            var (pw, ph) = (PlaceholderPet.FrameWidth * scale, PlaceholderPet.FrameHeight * scale);
            return (dx + pw / 2, dy + ph / 2);
        }
    }

    private (int Scale, int Dx, int Dy) SpritePlacement()
    {
        var scale = Math.Max(1, Math.Min(
            _bufferWidth / PlaceholderPet.FrameWidth,
            _bufferHeight / PlaceholderPet.FrameHeight));
        return (scale, (_bufferWidth - PlaceholderPet.FrameWidth * scale) / 2, _bufferHeight - PlaceholderPet.FrameHeight * scale);
    }

    /// <summary>把当前精灵帧绘制到帧缓冲（renderer）或占位精灵（回退）。</summary>
    private void DrawFrame(int frameIndex)
    {
        var buffer = new byte[_bufferWidth * _bufferHeight * 4];
        if (_renderer is not null)
        {
            _renderer.DrawFrame(buffer, _bufferWidth, _bufferHeight);
        }
        else
        {
            DrawPlaceholderFrame(buffer, frameIndex);
        }
        _bitmap.WritePixels(
            new Int32Rect(0, 0, _bufferWidth, _bufferHeight),
            buffer, _bufferWidth * 4, 0);
    }

    private void DrawPlaceholderFrame(byte[] buffer, int frameIndex)
    {
        var frame = PlaceholderPet.Frames[frameIndex % PlaceholderPet.Frames.Count];
        var (scale, dx, dy) = SpritePlacement();

        for (var fy = 0; fy < PlaceholderPet.FrameHeight; fy++)
        {
            for (var fx = 0; fx < PlaceholderPet.FrameWidth; fx++)
            {
                var src = (fy * PlaceholderPet.FrameWidth + fx) * 4;
                if (frame.Rgba[src + 3] == 0) continue;
                for (var sy = 0; sy < scale; sy++)
                {
                    var y = dy + fy * scale + sy;
                    for (var sx = 0; sx < scale; sx++)
                    {
                        var dst = ((y * _bufferWidth) + dx + fx * scale + sx) * 4;
                        buffer[dst] = frame.Rgba[src];
                        buffer[dst + 1] = frame.Rgba[src + 1];
                        buffer[dst + 2] = frame.Rgba[src + 2];
                        buffer[dst + 3] = frame.Rgba[src + 3];
                    }
                }
            }
        }
    }

    /// <summary>alpha hitTest：命中精灵（不透明区）才允许按下拖窗；
    /// 掩码查询 O(1)，对齐 pet.ts hitTest 语义。输入为窗口客户区物理像素。</summary>
    private bool HitTestSprite((int X, int Y) p)
    {
        if (_renderer is not null)
        {
            return _renderer.HitTest(p.X, p.Y);
        }
        var frame = PlaceholderPet.Frames[0];
        var (scale, dx, dy) = SpritePlacement();
        var dw = PlaceholderPet.FrameWidth * scale;
        var dh = PlaceholderPet.FrameHeight * scale;

        if (p.X < dx || p.X >= dx + dw || p.Y < dy || p.Y >= dy + dh) return false;
        var fx = (p.X - dx) / scale;
        var fy = (p.Y - dy) / scale;
        return frame.Mask[fy * PlaceholderPet.FrameWidth + fx] == 1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // 拖拽走消息级处理（WndProcHook），WPF 事件仅用于兜底点击判定
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
    }

    private bool MovedBeyondThreshold((int X, int Y) current)
        => Math.Abs(current.X - _pressPoint.X) > DragThresholdPx ||
           Math.Abs(current.Y - _pressPoint.Y) > DragThresholdPx;

    private void BenchTrace(string message)
    {
        if (!BenchLogEnabled) return;
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "desktoppet-drag.log"),
                $"{Environment.TickCount64}: {message}{Environment.NewLine}");
        }
        catch (System.IO.IOException) { }
    }

    /// <summary>仅 bench 模式启用拖拽诊断日志（平时零 IO）。</summary>
    public static bool BenchLogEnabled { get; set; }

    /// <summary>窗口左上角物理像素位置（GetWindowRect 直读，不依赖 WPF DPI 转换）。</summary>
    private (int X, int Y) PhysicalPosition()
    {
        var rect = new NativeMethods.RECT();
        NativeMethods.GetWindowRect(_hwnd, ref rect);
        return (rect.Left, rect.Top);
    }

    public void ShowAt(int physicalX, int physicalY)
    {
        Show();
        if (_hwnd == 0) _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MoveWindow(_hwnd, physicalX, physicalY);
        RestartAnimation();
        var rect = new NativeMethods.RECT();
        NativeMethods.GetWindowRect(_hwnd, ref rect);
        BenchTrace($"showat requested=({physicalX},{physicalY}) actual=({rect.Left},{rect.Top})");
    }

    protected override void OnClosed(EventArgs e)
    {
        _animationTimer.Stop();
        base.OnClosed(e);
    }
}

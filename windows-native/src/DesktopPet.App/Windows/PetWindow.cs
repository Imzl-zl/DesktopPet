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
using DesktopPet.Core.Care;
using DesktopPet.Core.Interaction;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Rendering;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;

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
    private readonly IJsonStore _store;
    private CareState _careState = null!;
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

    // ---- 漫游引擎（Phase 2）----
    private readonly RoamEngine _roamEngine = null!; // 构造中初始化
    private readonly DispatcherTimer _roamTimer;
    private readonly BubbleView _bubble = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly QuickBubbleController _quickBubble;
    private readonly SystemRoamClock _roamClock = new();
    private string _clickAction = "none"; // ap_left_click_action：none/self/all
    private string? _quickPresetPool;
    private Action<string>? _broadcastQuickBubble;
    private string? _moodLine;
    private string? _renderSignature;
    private long _celebrateUntil;
    private bool _wasCelebrating;
    private string _celebrateText = "";

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

    public nint Hwnd => _hwnd;

    public double DpiScale => _dpiScale;

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

    public PetWindow(PetInstance instance, SpriteLoader spriteLoader, IJsonStore store, Action<PetWindow, int, int> onDragFinished)
    {
        _instance = instance;
        _spriteLoader = spriteLoader;
        _store = store;
        _onDragFinished = onDragFinished;
        var care = _store.LoadCare();
        _careState = care.TryGetValue(instance.Id, out var state) ? state : CareEngine.EmptyState(DateTime.Now);

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

        // 气泡层叠在精灵上方，顶部对齐（headroom 由 SnugToHeadroom 控制）
        var root = new Grid();
        root.Children.Add(_image);
        root.Children.Add(_bubble);
        _bubble.HorizontalAlignment = HorizontalAlignment.Center;
        _bubble.VerticalAlignment = VerticalAlignment.Top;
        _bubble.Margin = new Thickness(0, 8, 0, 0);
        Content = root;

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 3), // idle 3fps，对齐 pet.ts STATE_FPS
        };
        _animationTimer.Tick += (_, _) => AdvanceFrame();

        // 漫游 tick（对齐 roam engine：活跃 30ms / 静止 200ms）
        _roamTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RoamEngine.IdleTickMs) };
        _roamTimer.Tick += (_, _) =>
        {
            var active = _roamEngine.Step(_roamClock.NowMs());
            var target = active ? RoamConstants.TickMs : RoamEngine.IdleTickMs;
            if (Math.Abs(_roamTimer.Interval.TotalMilliseconds - target) > 1)
            {
                _roamTimer.Interval = TimeSpan.FromMilliseconds(target);
            }
        };

        // 气泡渲染（对齐 pet-window render() 500ms 循环）
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _renderTimer.Tick += (_, _) => RenderBubble();

        _quickBubble = new QuickBubbleController(new DispatcherBubbleClock(), () =>
        {
            _renderSignature = null; // 过期后强制重渲染（对齐 renderSig 失效）
            RenderBubble();
        });


        var environmentSource = new PetWindowEnvironmentSource();
        environmentSource.SetDpiScale(_dpiScale);
        _roamEngine = new RoamEngine(
            new PetWindowRoamHost(this),
            environmentSource,
            () =>
            {
                var instance = _instance;
                var stage = CareEngine.StageIndex(CareEngine.LevelForXp(_careState.Xp));
                var caps = StageCapabilitiesFor.For(stage);
                var mode = instance.RoamMode;
                if (mode == RoamMode.Cursor && !caps.CursorMode) mode = RoamMode.Wander;
                if (mode == RoamMode.Climb && !caps.ClimbMode) mode = RoamMode.Wander;
                return new RoamConfig(
                    instance.RoamEnabled,
                    mode,
                    (int)Math.Max(1, instance.RoamSpeed * caps.SpeedFactor),
                    instance.WanderPauseMinMs,
                    instance.WanderPauseMaxMs);
            },
            _roamClock,
            () => Random.Shared.NextDouble(),
            pet: null, // 动画行由 render 循环管理，Phase 2 漫游行控制通过 SetRow
            sleepRowOverride: null,
            cursorProvider: () =>
            {
                var (x, y) = NativeMethods.CursorPosition();
                return new RoamPoint(x / _dpiScale, y / _dpiScale);
            });
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _animationTimer.Stop();
                _roamTimer.Stop();
                _renderTimer.Stop();
            }
            else
            {
                RestartAnimation();
                _roamTimer.Start();
                _renderTimer.Start();
            }
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

    /// <summary>快速气泡广播出口（manager 注入：浮球/点击 → 全部窗口）。</summary>
    public void SetBroadcastQuickBubble(Action<string> broadcast)
    {
        _broadcastQuickBubble = broadcast;
    }

    /// <summary>点击行为配置（ap_left_click_action：none/self/all）。</summary>
    public void SetClickAction(string action)
    {
        _clickAction = action switch { "self" => "self", "all" => "all", _ => "none" };
    }

    /// <summary>快速气泡预设池（ap_quick_bubbles JSON 数组）。</summary>
    public void SetQuickPresetPool(string json)
    {
        _quickPresetPool = json;
    }

    /// <summary>收到广播的快速气泡（对齐 listen&lt;quick-bubble&gt;）。</summary>
    public void ShowBroadcastQuickBubble(string text)
    {
        _quickBubble.Show(text, QuickBubbleDuration.ReadDurationMs(_ => null));
        RenderBubble();
    }

    private string? RandomPreset()
    {
        if (_quickPresetPool is null) return null;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<string[]>(_quickPresetPool);
            var presets = list?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
            if (presets.Count == 0) return null;
            return presets[Random.Shared.Next(presets.Count)];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>渲染气泡（对齐 pet-window render()：快速气泡优先 > celebrate/done/idle，签名去重）。</summary>
    private void RenderBubble()
    {
        var now = _roamClock.NowMs();
        var quickText = _quickBubble.Current();
        if (quickText is not null)
        {
            _renderSignature = null;
            _bubble.RenderLine(quickText);
            SnugBubble();
            return;
        }

        // Phase 2 无 activity 生产者：resolved 恒 idle；celebrate 供 Phase 3 升级触发
        const string resolved = "idle";
        var celebrating = now < _celebrateUntil;
        var mood = celebrating ? "celebrate" : resolved;
        _renderer?.SetState(mood);
        if (celebrating && _wasCelebrating && now >= _celebrateUntil)
        {
            PickMoodLine(resolved);
        }
        _wasCelebrating = celebrating;

        var signature = $"{mood}|{_moodLine}|{(celebrating ? _celebrateText : "")}";
        if (signature != _renderSignature)
        {
            _renderSignature = signature;
            if (celebrating)
            {
                _bubble.RenderLine(_celebrateText.Length > 0 ? _celebrateText : "Done!");
            }
            else if (_moodLine is { Length: > 0 })
            {
                _bubble.RenderLine(_moodLine);
            }
            else
            {
                _bubble.Hide();
            }
        }
        SnugBubble();
    }

    private static readonly string[] DefaultIdleLines =
        ["…", "♪", "Zzz…", "(*´∀`*)", "呼~", "盯——"];

    private static readonly string[] HungryLines =
        ["饿了…", "想吃小鱼干~", "好饿哦…", "投喂时间到！"];

    private void PickMoodLine(string mood)
    {
        // Phase 3：饥饿感知台词（对齐 care 饥饿状态影响气泡文案）
        var hunger = CareEngine.HungerAt(_careState, DateTime.Now);
        if (hunger >= Hunger.Peckish && Random.Shared.Next(3) == 0)
        {
            _moodLine = HungryLines[Random.Shared.Next(HungryLines.Length)];
            return;
        }
        // Phase 2 内置台词池；Phase 4 接 i18n / activity.ts 台词
        _moodLine = DefaultIdleLines[Random.Shared.Next(DefaultIdleLines.Length)];
    }

    private void SnugBubble()
    {
        if (_renderer is not null)
        {
            // 气泡坐在精灵头顶：headroom 占 buffer 比例 × 窗口高度
            _bubble.SnugToHeadroom(_renderer.Headroom * Height);
        }
    }

    /// <summary>升级/成就庆祝爆发（Phase 3 care 接入后调用）。</summary>
    public void FlashCelebrate(string line)
    {
        _celebrateText = line;
        _celebrateUntil = _roamClock.NowMs() + 3000;
        RenderBubble();
    }

    private sealed class DispatcherBubbleClock : IQuickBubbleClock
    {
        private sealed class OneShot(Action callback, long delayMs) : IDisposable
        {
            private readonly DispatcherTimer _timer = new()
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)),
            };

            public void Start()
            {
                _timer.Tick += (_, _) =>
                {
                    _timer.Stop();
                    callback();
                };
                _timer.Start();
            }

            public void Dispose() => _timer.Stop();
        }

        public long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public IDisposable Schedule(Action callback, long delayMs)
        {
            var timer = new OneShot(callback, delayMs);
            timer.Start();
            return timer;
        }
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
        _roamEngine.BeginManualDrag(); // 采样起点 + 取消抛掷（对齐 beginManualDrag）
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
        _roamEngine.MoveManualDrag(new RoamPoint(targetX, targetY)); // 移动 + 物理采样
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
            _roamEngine.FinishManualDrag(); // releasePending → 引擎 tick 抛掷/下落
            var (x, y) = PhysicalPosition();
            _onDragFinished(this, x, y);
            BenchTrace($"raw drag finished at {x},{y}");
        }
        else if (Environment.TickCount64 - _pressTickMs <= 280)
        {
            // 未超阈值 = 点击（对齐 WindowDragController clickMaxMs + onClick）
            OnPetClick();
        }
    }

    /// <summary>点击宠物（对齐 onPetClick：LEFT_CLICK_KEY none/self/all）。</summary>
    private void OnPetClick()
    {
        if (_clickAction == "none") return;
        var text = RandomPreset();
        if (text is null) return;
        if (_clickAction == "self")
        {
            _quickBubble.Show(text, QuickBubbleDuration.ReadDurationMs(_ => null));
            RenderBubble();
        }
        else
        {
            _broadcastQuickBubble?.Invoke(text);
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
            ApplyStageOverlay(buffer);
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

    /// <summary>成长表现叠加：与宠物渲染器同帧绘制（§3.7 光晕/辉光/皇冠/星点）。</summary>
    private void ApplyStageOverlay(byte[] buffer)
    {
        var frame = _renderer!.CurrentFrame();
        if (frame is null) return;
        var (x, y, w, h) = _renderer.SpriteRect;
        var scale = Math.Max(1, w / frame.Width);
        var stage = CareEngine.StageIndex(CareEngine.LevelForXp(_careState.Xp));
        OverlayRenderer.Apply(buffer, _bufferWidth, _bufferHeight, frame,
            StageAppearances.For(stage), x, y, scale, _roamClock.NowMs());
    }

    /// <summary>喂 token（对齐 feedPet：升级 → 进化气泡）。</summary>
    public void FeedTokens(double tokens)
    {
        var before = CareEngine.LevelForXp(_careState.Xp);
        CareEngine.FeedTokens(_careState, tokens, DateTime.Now);
        var after = CareEngine.LevelForXp(_careState.Xp);
        if (after > before) FlashCelebrate($"进化！{CareEngine.StageName(after)}");
        PersistCare();
    }

    /// <summary>记录一次会话（25 XP）。</summary>
    public void RecordMeal()
    {
        var before = CareEngine.LevelForXp(_careState.Xp);
        CareEngine.RecordMeal(_careState, DateTime.Now);
        var after = CareEngine.LevelForXp(_careState.Xp);
        if (after > before) FlashCelebrate($"进化！{CareEngine.StageName(after)}");
        PersistCare();
    }

    public CareState CurrentCareState => _careState;

    private void PersistCare()
    {
        var care = _store.LoadCare();
        care[_instance.Id] = _careState;
        _store.SaveCare(care);
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
    internal (int X, int Y) PhysicalPosition()
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
        _roamTimer.Stop();
        _renderTimer.Stop();
        base.OnClosed(e);
    }
}

/// <summary>PetWindow 的漫游宿主适配：物理/逻辑位置换算（对齐 roam/window.ts）。</summary>
internal sealed class PetWindowRoamHost : IRoamHost
{
    private readonly PetWindow _window;

    public PetWindowRoamHost(PetWindow window) => _window = window;

    public RoamPoint? CurrentLogicalPos()
    {
        var (x, y) = _window.PhysicalPosition();
        return new RoamPoint(x / _window.DpiScale, y / _window.DpiScale);
    }

    public void SetLogical(RoamPoint pos)
    {
        var (x, y) = _window.PhysicalPosition();
        NativeMethods.MoveWindow(_window.Hwnd,
            (int)Math.Round(pos.X * _window.DpiScale),
            (int)Math.Round(pos.Y * _window.DpiScale));
        // 仅当确实移动才持久化（对齐 engine 的移动判定在 StepMode）
        _ = x; _ = y;
    }

    public RoamPoint SetPhysical(RoamPoint physicalPos)
    {
        NativeMethods.MoveWindow(_window.Hwnd,
            (int)Math.Round(physicalPos.X),
            (int)Math.Round(physicalPos.Y));
        return new RoamPoint(physicalPos.X / _window.DpiScale, physicalPos.Y / _window.DpiScale);
    }
}

/// <summary>漫游环境源：work area + 系统窗口枚举（150ms TTL 缓存，对齐 Rust WIN_CACHE_TTL）。</summary>
internal sealed class PetWindowEnvironmentSource : IRoamEnvironmentSource
{
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private long _cacheUntil;
    private RoamEnvironment? _cache;
    private readonly SystemRoamClock _clock = new();
    private double _dpiScale = 1;

    public void SetDpiScale(double dpiScale) => _dpiScale = dpiScale;

    public RoamEnvironment? Fetch(bool includeSystemWindows)
    {
        var now = _clock.NowMs();
        if (_cache is not null && now < _cacheUntil && includeSystemWindows)
        {
            return _cache;
        }

        var (waW, waH) = NativeMethods.PrimaryWorkAreaSize();
        var workArea = new RoamRect(0, 0, waW / _dpiScale, waH / _dpiScale);

        var windows = new List<SystemWindowInfo>();
        if (includeSystemWindows)
        {
            foreach (var (title, x, y, w, h) in NativeMethods.EnumerateVisibleWindows(_ownProcessId))
            {
                windows.Add(new SystemWindowInfo(title, new RoamRect(
                    x / _dpiScale, y / _dpiScale,
                    (x + w) / _dpiScale, (y + h) / _dpiScale)));
            }
        }

        var env = new RoamEnvironment(workArea, windows);
        if (includeSystemWindows)
        {
            _cache = env;
            _cacheUntil = now + 150;
        }
        return env;
    }
}

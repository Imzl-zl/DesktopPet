using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Interop;
using DesktopPet.Core.I18n;
using DesktopPet.App.Rendering;
using DesktopPet.Core.Care;
using DesktopPet.Core.Input;
using DesktopPet.Core.Interaction;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Rendering;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Diagnostics;

namespace DesktopPet.App.Windows;

/// <summary>
/// 宠物窗口：透明 + 置顶 + 无边框。普通精灵帧使用冻结 `BitmapSource` 缓存，
/// 动态成长叠加回退至可复用的 `WriteableBitmap` 帧缓冲；动画和漫游保持状态机
/// 定义的节奏。拖拽为原生实现：alpha hitTest 命中才按下 → CaptureMouse → SetWindowPos
/// 直移（无 Tauri hit-rect 补丁）。位置持久化由 manager 回调（物理像素，对齐 Rust
/// pet-positions.json 语义）。
/// </summary>
public sealed class PetWindow : Window
{
    private const double DragThresholdPx = 4;

    private PetInstance _instance;
    private readonly Action<PetWindow, int, int> _onDragFinished;
    private readonly SpriteLoader _spriteLoader;
    private readonly IJsonStore _store;
    private readonly IAppLogger _logger;
    private I18nService _i18n = new();
    private CareState _careState = null!;
    private readonly Image _image = new();
    private readonly WriteableBitmap _bitmap;
    private readonly ReusablePixelBuffer _frameBuffer;
    private readonly SpriteFrameBitmapSourceCache _frameSourceCache = new();
    private readonly double _dpiScale;
    private readonly int _bufferWidth;
    private readonly int _bufferHeight;

    private PetRenderer? _renderer;
    private bool _spriteLoading;
    private readonly DispatcherTimer _animationTimer;
    private int _frameIndex;
    private bool _animationEnabled = true;
    private bool _desktopInteractionSuspended;

    // ---- 漫游引擎（Phase 2）----
    private readonly RoamEngine _roamEngine = null!; // 构造中初始化
    private readonly DispatcherTimer _roamTimer;
    private readonly BubbleView _bubble = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly QuickBubbleController _quickBubble;
    private readonly SystemRoamClock _roamClock = new();

    /// <summary>精灵加载完成（PetWindowManager 用于刷新浮球球体）。</summary>
    public event Action? SpriteLoaded;
    private string _clickAction = "none"; // ap_left_click_action：none/self/all
    private string? _quickPresetPool;
    private int _quickBubbleDurationSeconds = 4;
    private Action<string>? _broadcastQuickBubble;
    private string? _moodLine;
    private string? _renderSignature;
    private long _celebrateUntil;
    private bool _wasCelebrating;
    private string _celebrateText = "";

    // ---- 外观/漫游设置（ApplySettings 全量下发；renderer 未就绪时暂存，精灵加载后套用）----
    private AppSettings _settings = null!;
    private bool _showIdleChatter = true;
    private RoamConfig? _roamConfig;

    // ---- 待机动作轮播（列表/间隔/开关由实例动作配置解析，renderer 持有；窗口只计时）----
    private long _lastIdleSwitchMs;
    // ---- 点击动作行播放（时长由实例动作配置解析，超时自动释放；拖拽可打断）----
    private long _clickRowUntilMs;
    // ---- 用户设置：精灵帧动画开关 / 闲谈台词重选间隔 ----
    private bool _userAnimationEnabled = true;
    private long _idleChatterIntervalMs = 15_000;

    // 拖拽状态（对齐 windows/src/window-drag.ts 语义：阈值区分点击/拖拽）
    private readonly DragInteractionState _dragState = new();
    private (int X, int Y) _pressPoint;
    private long _pressTickMs;
    private (int X, int Y) _grabOffset;
    private nint _hwnd;
    private HwndSource? _hwndSource; // AddHook 的承载源（OnClosed 时 RemoveHook，管理钩子生命周期）

    // 拖拽延迟采样（bench 用）：处理耗时（消息到达 → SetWindowPos 完成）+
    // 端到端（消息时间戳 → 完成，含系统队列等待）
    private readonly List<double> _processingLatencyMs = [];
    private readonly List<double> _endToEndLatencyMs = [];

    public string PetId => _instance.Id;
    public string SpriteSlug => _instance.SpriteSlug;
    public bool PetVisible => _instance.Visible;

    public nint Hwnd => _hwnd;

    public double DpiScale => _dpiScale;

    public IReadOnlyList<double> ProcessingLatencySamples => _processingLatencyMs;

    public IReadOnlyList<double> EndToEndLatencySamples => _endToEndLatencyMs;

    public void ApplyLocalization(I18nService i18n) => _i18n = i18n;

    /// <summary>静止或前台窗口交互时停止桌宠计时器，避免后台工作争用资源。</summary>
    public bool AnimationEnabled
    {
        get => _animationEnabled;
        set
        {
            if (_animationEnabled == value) return;
            _animationEnabled = value;
            if (value) UpdateTimerState();
            else _animationTimer.Stop();
        }
    }

    /// <summary>前台窗口交互期间暂停桌宠计时器，避免后台绘制和漫游争用资源。</summary>
    public void SetDesktopInteractionSuspended(bool suspended)
    {
        if (_desktopInteractionSuspended == suspended) return;
        _desktopInteractionSuspended = suspended;
        UpdateTimerState();
    }

    public PetWindow(
        PetInstance instance,
        SpriteLoader spriteLoader,
        IJsonStore store,
        Action<PetWindow, int, int> onDragFinished,
        IAppLogger? logger = null)
    {
        _instance = instance;
        _spriteLoader = spriteLoader;
        _store = store;
        _logger = logger ?? NullAppLogger.Instance;
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
        _frameBuffer = new ReusablePixelBuffer(_bufferWidth * _bufferHeight * 4);

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

        // 漫游 tick（活跃 30ms / 静止 200ms）
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
                // 全局漫游设置优先（设置页漫游页）；未下发时回退实例字段（导入默认）
                if (_roamConfig is not null) return _roamConfig;
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
            pet: new RoamPetAdapter(this), // 行走/睡眠行 → 实例动作绑定（roamLeft/roamRight；无绑定回退语义行）
            sleepRowOverride: null,
            cursorProvider: () =>
            {
                var (x, y) = NativeMethods.CursorPosition();
                return new RoamPoint(x / _dpiScale, y / _dpiScale);
            });
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => UpdateTimerState();
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
            MessageBox.Show(this, _i18n.T("无法解析精灵图（需要带透明通道的 PNG/WebP，且能检测到帧间隙）"), "DesktopPet",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = new SpritePreviewWindow(
            sheet,
            bytes,
            System.IO.Path.GetFileNameWithoutExtension(path),
            _i18n)
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

    /// <summary>气泡时长（设置页 1-10s，对齐 readQuickBubbleDurationMs）。</summary>
    public void ApplyQuickBubbleDuration(int seconds)
    {
        _quickBubbleDurationSeconds = Math.Clamp(seconds, 1, 10);
    }

    /// <summary>
    /// 全量下发设置（对齐 Tauri 版 listen/emit 语义）：点击动作/气泡池/时长 +
    /// 外观（主题/不透明度/字号/字体）、宠物尺寸、待机浮动、闲谈气泡、漫游。
    /// renderer 未就绪时暂存，精灵加载后套用。
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _clickAction = settings.LeftClickAction switch { "self" => "self", "all" => "all", _ => "none" };
        _quickPresetPool = System.Text.Json.JsonSerializer.Serialize(settings.QuickBubblePresets);
        _quickBubbleDurationSeconds = Math.Clamp(settings.QuickBubbleDurationSeconds, 1, 10);
        _showIdleChatter = settings.ShowIdleChatter;
        _bubble.ApplyAppearance(settings.Theme, settings.BubbleOpacity, settings.FontSize, settings.FontFamily);
        _roamConfig = settings.Roam;
        _userAnimationEnabled = settings.AnimationEnabled;
        _idleChatterIntervalMs = settings.IdleChatterIntervalSeconds * 1000L;
        // 台词池替换（空数组 = 不显示闲谈/饥饿台词）
        _idleChatterLines = settings.IdleChatterLines is { Length: > 0 } lines ? lines : [];
        _hungryLines = settings.HungryLines is { Length: > 0 } hungry ? hungry : [];

        if (_renderer is not null)
        {
            _renderer.SetSizePercent(settings.PetSizePercent / 100.0);
            _renderer.SetBob(settings.BobAnimation);
            // 播放列表完整替换（间隔/模式/开关即时生效；未配置 → 默认策略）
            _renderer.SetIdlePlaylist(PetAnimationResolver.ResolveIdle(_instance.Actions, _renderer.ClipCount));
        }

        // 动画总开关即时生效（关 = 静态显示当前帧）
        if (_userAnimationEnabled) UpdateTimerState();
        else _animationTimer.Stop();

        // 闲谈开关变化 → 强制重选台词（ShowIdleChatter 关时 PickMoodLine 返回空）
        _moodLine = null;
        _renderSignature = null;
        RenderBubble();
    }

    /// <summary>精灵就绪后套用已保存的外观设置（加载发生在 ApplySettings 之后的场景）。</summary>
    private void ApplyPendingRenderSettings()
    {
        if (_settings is null || _renderer is null) return;
        _renderer.SetSizePercent(_settings.PetSizePercent / 100.0);
        _renderer.SetBob(_settings.BobAnimation);
        _renderer.SetIdlePlaylist(PetAnimationResolver.ResolveIdle(_instance.Actions, _renderer.ClipCount));
    }

    private long QuickBubbleDurationMs => _quickBubbleDurationSeconds * 1000L;

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
        _quickBubble.Show(text, QuickBubbleDurationMs);
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
        if (celebrating)
        {
            // 庆祝行绑定（无绑定/越界 → 默认 celebrate 行）
            var bound = PetAnimationResolver.ResolveBind(
                _instance.Actions, PetActionTriggers.Celebrate, _renderer?.ClipCount ?? 0);
            _renderer?.SetState(mood, bound);
        }
        else
        {
            _renderer?.SetState(mood);
        }
        // 闲谈台词节奏：庆祝结束过渡 / 首次 / 每 _idleChatterIntervalMs 换一句
        //（PickMoodLine 内部遵守"显示闲谈"开关）
        if (celebrating && _wasCelebrating && now >= _celebrateUntil)
        {
            PickMoodLine(resolved);
            _moodLinePickedAtMs = now;
        }
        else if (!celebrating && (_moodLine is null || now - _moodLinePickedAtMs > _idleChatterIntervalMs))
        {
            PickMoodLine(resolved);
            _moodLinePickedAtMs = now;
        }
        _wasCelebrating = celebrating;

        var signature = $"{mood}|{_moodLine}|{(celebrating ? _celebrateText : "")}";
        if (signature != _renderSignature)
        {
            _renderSignature = signature;
            if (celebrating)
            {
                _bubble.RenderLine(_celebrateText.Length > 0 ? _celebrateText : _i18n.T("Done!"));
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

    /// <summary>闲谈台词池（设置页「气泡」可编辑；空数组 = 不显示闲谈）。</summary>
    private string[] _idleChatterLines = AppSettings.DefaultIdleChatterLines;
    /// <summary>饥饿台词池（同上；空数组 = 饥饿时不额外提示）。</summary>
    private string[] _hungryLines = AppSettings.DefaultHungryLines;

    /// <summary>闲谈台词重选间隔（设置页可调 5-120s，默认 15s）。</summary>
    private long _moodLinePickedAtMs;

    /// <summary>
    /// 选闲谈台词：遵守设置页"显示闲谈气泡"开关；饥饿时有概率说饿话（对齐 care）。
    /// 调用方负责节奏（首次/定期/庆祝结束过渡）。
    /// </summary>
    private void PickMoodLine(string mood)
    {
        if (!_showIdleChatter)
        {
            _moodLine = null; // 设置页"显示闲谈气泡"关闭 → 无闲谈台词
            return;
        }
        // Phase 3：饥饿感知台词（对齐 care 饥饿状态影响气泡文案）
        var hunger = CareEngine.HungerAt(_careState, DateTime.Now);
        if (_hungryLines.Length > 0 && hunger >= Hunger.Peckish && Random.Shared.Next(3) == 0)
        {
            _moodLine = _hungryLines[Random.Shared.Next(_hungryLines.Length)];
            return;
        }
        // 台词池（设置页可编辑）；空池 = 不显示闲谈
        _moodLine = _idleChatterLines.Length > 0
            ? _idleChatterLines[Random.Shared.Next(_idleChatterLines.Length)]
            : null;
    }

    private void SnugBubble()
    {
        if (_renderer is not null)
        {
            // 气泡坐在精灵头顶：headroom 占 buffer 比例 × 窗口高度
            _bubble.SnugToHeadroom(_renderer.Headroom * Height);
        }
    }

    /// <summary>升级/成就庆祝爆发（Phase 3 care 接入后调用；时长由实例动作配置解析）。</summary>
    public void FlashCelebrate(string line)
    {
        _celebrateText = line;
        _celebrateUntil = _roamClock.NowMs() +
                          (long)PetAnimationResolver.ResolveCelebrateDurationMs(_instance.Actions);
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
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProcHook);
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
                // 待机动作轮播：由实例动作配置解析（未配置 = 全行随机 5s；关闭 = null）
                var idle = PetAnimationResolver.ResolveIdle(_instance.Actions, sheet.Clips.Count);
                _renderer = new PetRenderer(sheet, idle);
                _lastIdleSwitchMs = _roamClock.NowMs();
                ApplyPendingRenderSettings(); // 尺寸/浮动/播放列表设置（可能先于精灵加载到达）
                _renderer.SetState("idle");
                _animationTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _renderer.Fps);
                DrawFrame(0);
                RestartAnimation();
                SpriteLoaded?.Invoke(); // 浮球球体刷新（启动时序：浮球创建早于精灵缓存就绪）
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
        const int wmCancelMode = 0x001F;
        const int wmCaptureChanged = 0x0215;
        const nint mkLeftButton = 0x0001;
        switch (msg)
        {
            case wmLeftDown:
                BenchTrace($"raw msg down client={ClientPointOfMessage(lParam)} cursor={NativeMethods.CursorPosition()}");
                OnRawLeftDown(lParam);
                handled = true;
                break;
            case wmMouseMove when _dragState.IsPressed && (wParam.ToInt64() & mkLeftButton) != 0:
                OnRawMove(lParam);
                handled = true;
                break;
            case wmLeftUp:
                BenchTrace($"raw msg up pressed={_dragState.IsPressed}");
                if (_dragState.IsPressed) OnRawLeftUp();
                handled = true;
                break;
            case wmCancelMode:
            case wmCaptureChanged:
                CancelPointerInteraction();
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
        if (_dragState.IsDragging || _dragState.IsPressed) return;
        var client = ClientPointOfMessage(lParam);
        if (!HitTestSprite(client)) return;

        _dragState.Begin();
        _pressPoint = client;
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
        if (!_dragState.IsPressed) return;
        var client = ClientPointOfMessage(lParam);
        var crossedThreshold = !_dragState.IsDragging && MovedBeyondThreshold(client);
        if (crossedThreshold)
        {
            _dragState.StartDragging();
            ApplyDragRow(true); // 拖拽动作行（最高优先级，无绑定则保持当前动作）
            BenchTrace("raw drag started");
        }
        if (!_dragState.IsDragging) return;

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
        if (!_dragState.IsPressed) return;
        var terminalAction = _dragState.Complete();
        ReleaseMouseCapture();

        if (terminalAction == DragTerminalAction.CommitPosition)
        {
            ApplyDragRow(false);
            _roamEngine.FinishManualDrag();
            var (x, y) = PhysicalPosition();
            _onDragFinished(this, x, y);
            BenchTrace($"raw drag finished at {x},{y}");
        }
        else if (terminalAction == DragTerminalAction.Click
                 && Environment.TickCount64 - _pressTickMs <= 280)
        {
            // 未超阈值 = 点击（对齐 WindowDragController clickMaxMs + onClick）
            OnPetClick();
        }
    }

    private void CancelPointerInteraction()
    {
        if (!_dragState.IsPressed && !_dragState.IsDragging) return;
        var wasDragging = _dragState.IsDragging;
        _dragState.Cancel();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDragging) ApplyDragRow(false);
        _roamEngine.CancelManualDrag();
        BenchTrace("raw pointer interaction cancelled");
    }

    /// <summary>拖拽动作行：越过 4px 阈值后激活（最高优先级），松开释放；无绑定保持当前动作。</summary>
    private void ApplyDragRow(bool active)
    {
        if (_renderer is null) return;
        if (active)
        {
            var row = PetAnimationResolver.ResolveBind(_instance.Actions, PetActionTriggers.Drag, _renderer.ClipCount);
            if (row is { } dragRow) _renderer.SetRow(dragRow, "drag", PetRenderer.PriorityDrag);
        }
        else
        {
            _renderer.ClearRow("drag");
        }
    }

    /// <summary>点击宠物（对齐 onPetClick：LEFT_CLICK_KEY none/self/all）。</summary>
    private void OnPetClick()
    {
        // 点击动作行：播放一轮（时长由实例动作配置解析）后自动释放；拖拽可打断（优先级更高）
        if (_renderer is not null)
        {
            var row = PetAnimationResolver.ResolveBind(_instance.Actions, PetActionTriggers.Click, _renderer.ClipCount);
            if (row is { } clickRow)
            {
                _renderer.SetRow(clickRow, "click", PetRenderer.PriorityClick);
                _clickRowUntilMs = _roamClock.NowMs() +
                                   (long)PetAnimationResolver.ResolveClickDurationMs(_instance.Actions);
            }
        }

        if (_clickAction == "none") return;
        var text = RandomPreset();
        if (text is null) return;
        if (_clickAction == "self")
        {
            _quickBubble.Show(text, QuickBubbleDurationMs);
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

    private void UpdateTimerState()
    {
        if (!IsVisible || _desktopInteractionSuspended)
        {
            _animationTimer.Stop();
            _roamTimer.Stop();
            _renderTimer.Stop();
            return;
        }

        RestartAnimation();
        _roamTimer.Start();
        _renderTimer.Start();
    }

    private void RestartAnimation()
    {
        _animationTimer.Stop();
        if (_animationEnabled && _userAnimationEnabled && IsVisible && !_desktopInteractionSuspended)
        {
            _animationTimer.Start();
        }
    }

    private void AdvanceFrame()
    {
        if (!_animationEnabled || !IsVisible) return;
        if (_renderer is not null)
        {
            // 点击动作行播放超时 → 释放（owner 匹配才清除，不影响拖拽/漫游持有）
            if (_clickRowUntilMs > 0 && _roamClock.NowMs() >= _clickRowUntilMs)
            {
                _renderer.ClearRow("click");
                _clickRowUntilMs = 0;
            }
            // 待机轮播节奏：间隔来自 renderer 的播放列表（设置页即时生效，唯一计时来源）
            if (_renderer.IsIdleCycling && _renderer.IdleIntervalMs is { } intervalMs)
            {
                var now = _roamClock.NowMs();
                if (now - _lastIdleSwitchMs >= intervalMs)
                {
                    _renderer.AdvanceIdleClip();
                    _lastIdleSwitchMs = now;
                }
            }
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

    /// <summary>Displays cached retained frames when no dynamic stage overlay is active.</summary>
    private bool TryPresentCachedSpriteFrame()
    {
        var stage = CareEngine.StageIndex(CareEngine.LevelForXp(_careState.Xp));
        if (StageAppearances.For(stage).GlowColor is not null) return false;

        var frame = _renderer!.PrepareFrame(_bufferWidth, _bufferHeight);
        if (frame is null) return false;

        var (x, y, width, height) = _renderer.SpriteRect;
        _image.Source = _frameSourceCache.GetOrCreate(frame);
        _image.Width = width / _dpiScale;
        _image.Height = height / _dpiScale;
        _image.Margin = new Thickness(x / _dpiScale, y / _dpiScale, 0, 0);
        _image.HorizontalAlignment = HorizontalAlignment.Left;
        _image.VerticalAlignment = VerticalAlignment.Top;
        return true;
    }

    private void RestoreWriteableBitmapPresentation()
    {
        _image.Source = _bitmap;
        _image.Width = double.NaN;
        _image.Height = double.NaN;
        _image.Margin = new Thickness();
        _image.HorizontalAlignment = HorizontalAlignment.Stretch;
        _image.VerticalAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>把当前精灵帧绘制到帧缓冲（renderer）或占位精灵（回退）。</summary>
    private void DrawFrame(int frameIndex)
    {
        if (_renderer is not null && TryPresentCachedSpriteFrame()) return;

        RestoreWriteableBitmapPresentation();
        var buffer = _frameBuffer.Clear();
        if (_renderer is not null)
        {
            _renderer.DrawFrame(buffer, _bufferWidth, _bufferHeight);
            ApplyStageOverlay(buffer);
        }
        else
        {
            DrawPlaceholderFrame(buffer, frameIndex);
        }
        PixelBuffer.RgbaToBgra(buffer); // Core 输出 RGBA，WriteableBitmap 是 Bgra32（防 R/B 错位）
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
        var next = _careState.Clone();
        var before = CareEngine.LevelForXp(next.Xp);
        CareEngine.FeedTokens(next, tokens, DateTime.Now);
        var after = CareEngine.LevelForXp(next.Xp);
        PersistCare(next);
        _careState.CopyFrom(next);
        if (after > before) FlashCelebrate(_i18n.Format("进化！{0}", CareEngine.StageName(after)));
    }

    /// <summary>记录一次会话（25 XP）。</summary>
    public void RecordMeal()
    {
        var next = _careState.Clone();
        var before = CareEngine.LevelForXp(next.Xp);
        CareEngine.RecordMeal(next, DateTime.Now);
        var after = CareEngine.LevelForXp(next.Xp);
        PersistCare(next);
        _careState.CopyFrom(next);
        if (after > before) FlashCelebrate(_i18n.Format("进化！{0}", CareEngine.StageName(after)));
    }

    public CareState CurrentCareState => _careState;

    private void PersistCare(CareState next)
    {
        var care = _store.LoadCare();
        care[_instance.Id] = next;
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
        _logger.Info("BenchDrag", $"tick={Environment.TickCount64} {message}");
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

    /// <summary>漫游动画行适配：引擎固定行号（1 右 / 2 左）→ 实例动作绑定；无绑定回退语义行。</summary>
    internal void ApplyRoamRow(int? fixedRow)
    {
        if (_renderer is null) return;
        if (fixedRow is not 1 and not 2)
        {
            _renderer.ClearRow("roam"); // sleep 等行无绑定 → 保持 idle
            return;
        }
        var trigger = fixedRow == 1 ? PetActionTriggers.RoamRight : PetActionTriggers.RoamLeft;
        var row = PetAnimationResolver.ResolveBind(_instance.Actions, trigger, _renderer.ClipCount);
        if (row is { } roamRow) _renderer.SetRow(roamRow, "roam", PetRenderer.PriorityRoam);
        else _renderer.ClearRow("roam");
    }

    /// <summary>实例配置更新（设置页动作保存后即时生效，无需重建窗口）。</summary>
    public void ApplyInstance(PetInstance instance)
    {
        _instance = instance;
        if (_renderer is not null)
        {
            _renderer.SetIdlePlaylist(PetAnimationResolver.ResolveIdle(instance.Actions, _renderer.ClipCount));
        }
    }

    /// <summary>漫游引擎行控制适配（IRoamPet → renderer owner 覆盖）。</summary>
    private sealed class RoamPetAdapter : IRoamPet
    {
        private readonly PetWindow _window;
        public RoamPetAdapter(PetWindow window) => _window = window;
        public void SetRow(int row) => _window.ApplyRoamRow(row);
        public void ClearRow() => _window.ApplyRoamRow(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelPointerInteraction();
        _animationTimer.Stop();
        _roamTimer.Stop();
        _renderTimer.Stop();
        _hwndSource?.RemoveHook(WndProcHook); // 钩子由弱引用持有（官方文档），窗口关闭显式摘除
        _hwndSource = null;
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

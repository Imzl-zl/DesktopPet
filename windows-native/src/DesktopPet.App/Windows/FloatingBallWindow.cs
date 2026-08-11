using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Interop;
using DesktopPet.App.Localization;
using DesktopPet.App.Rendering;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Rendering;
using DesktopPet.Infra.Diagnostics;

namespace DesktopPet.App.Windows;

/// <summary>
/// 浮球（对齐 floating-ball.ts 语义）：48px 视觉球体（Lumen 光感）内嵌选中
/// 宠物活体动画；左键 → 气泡菜单（输入 + 预设胶囊 + 发送 → 全员广播）；
/// 右键 → 设置（Phase 4 占位）；拖拽 + 位置持久化（ball-pos 文件语义）。
/// </summary>
public sealed class FloatingBallWindow : Window
{
    private const double BallSize = 56;

    private readonly Action<string> _sendQuickBubble;
    private readonly Func<string> _readPresetPool;
    private readonly Func<SpriteSheet?> _selectedSprite;
    private readonly Action _openSettings;
    private readonly Action<string>? _setOutputMode; // bubble/danmaku/chat/silent（AI 输出模式）
    private Action? _openChat;                        // 打开对话窗（用户主动聊天入口；回调后置需可写）
    private I18nService _i18n;
    private readonly IAppLogger _logger;
    private readonly Image _petImage = new();
    private WriteableBitmap? _petBitmap;
    private ReusablePixelBuffer? _petBuffer;
    private PetRenderer? _petRenderer;
    private readonly DispatcherTimer _petTimer;
    private bool _desktopInteractionSuspended;

    private bool _pressed;
    private bool _dragging;
    private (int X, int Y) _pressPoint;
    private (int X, int Y) _grabOffset;
    private nint _hwnd;
    private HwndSource? _hwndSource; // AddHook 的承载源（OnClosed 时 RemoveHook，管理钩子生命周期）
    private readonly string _positionFilePath;
    private bool _menuOpen;

    public FloatingBallWindow(
        Action<string> sendQuickBubble,
        Func<string> readPresetPool,
        Func<SpriteSheet?> selectedSprite,
        Action openSettings,
        string dataDirectory,
        Action<string>? setOutputMode = null,
        Action? openChat = null,
        I18nService? i18n = null,
        IAppLogger? logger = null)
    {
        _sendQuickBubble = sendQuickBubble;
        _readPresetPool = readPresetPool;
        _selectedSprite = selectedSprite;
        _openSettings = openSettings;
        _setOutputMode = setOutputMode;
        _openChat = openChat;
        _i18n = i18n ?? new I18nService();
        _logger = logger ?? NullAppLogger.Instance;
        _positionFilePath = Path.Combine(dataDirectory, "ball-pos");

        Width = 80;
        Height = 80;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;

        // 球体：玻璃质感 + 顶部高光（Lumen 光感）
        var ball = new Grid { Width = BallSize, Height = BallSize };
        ball.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x21, 0x1C, 0x20, 0x28)),
            StrokeThickness = 1,
        });
        ball.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Fill = new RadialGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0),
                new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.5),
            }),
            Margin = new Thickness(8, 6, 0, 0),
        });
        ball.Children.Add(_petImage);
        _petImage.Margin = new Thickness(10);
        _petImage.Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(_petImage, BitmapScalingMode.NearestNeighbor);

        var root = new Grid();
        root.Children.Add(ball);
        Content = root;

        _petTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 3) };
        _petTimer.Tick += (_, _) => AdvanceBallPet();

        Loaded += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProcHook);
            RestorePosition();
            LoadBallPet();
            UpdatePetTimer();
        };
        IsVisibleChanged += (_, _) => UpdatePetTimer();
    }

    public void ApplyLocalization(I18nService i18n)
    {
        _i18n = i18n;
        if (_menu?.Child is DependencyObject content)
            WpfLocalizer.RefreshTracked(content, i18n);
    }

    /// <summary>前台窗口交互期间暂停浮球内宠物的动画计时器。</summary>
    public void SetDesktopInteractionSuspended(bool suspended)
    {
        if (_desktopInteractionSuspended == suspended) return;
        _desktopInteractionSuspended = suspended;
        UpdatePetTimer();
    }

    private void UpdatePetTimer()
    {
        if (IsVisible && !_desktopInteractionSuspended) _petTimer.Start();
        else _petTimer.Stop();
    }

    // ---- 球内活体宠物 ----

    private void LoadBallPet()
    {
        var sheet = _selectedSprite();
        if (sheet is null) return;
        _petRenderer = new PetRenderer(sheet);
        _petRenderer.SetState("idle");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _petBitmap = new WriteableBitmap((int)(56 * dpi), (int)(56 * dpi), 96 * dpi, 96 * dpi, PixelFormats.Bgra32, null);
        _petBuffer = new ReusablePixelBuffer(_petBitmap.PixelWidth * _petBitmap.PixelHeight * 4);
        _petImage.Source = _petBitmap;
        DrawBallPet();
    }

    /// <summary>宠物窗精灵异步加载完成后刷新球体（启动时序：宠物窗先建、加载完成后球体才有内容；
    /// 无球体 = 全透明窗口 = 左键点击穿透，菜单打不开）。</summary>
    public void ReloadPet() => LoadBallPet();

    /// <summary>聊天回调后置（App 启动顺序：浮球创建早于回调注册）。null = 不显示聊天按钮。</summary>
    public void SetOpenChat(Action? openChat) => _openChat = openChat;

    private void AdvanceBallPet()
    {
        _petRenderer?.AdvanceFrame();
        DrawBallPet();
    }

    private void DrawBallPet()
    {
        if (_petBitmap is null || _petBuffer is null || _petRenderer is null) return;
        var buffer = _petBuffer.Clear();
        _petRenderer.DrawFrame(buffer, _petBitmap.PixelWidth, _petBitmap.PixelHeight);
        PixelBuffer.RgbaToBgra(buffer); // Core 输出 RGBA，WriteableBitmap 是 Bgra32
        _petBitmap.WritePixels(new Int32Rect(0, 0, _petBitmap.PixelWidth, _petBitmap.PixelHeight), buffer, _petBitmap.PixelWidth * 4, 0);
    }

    // ---- 拖拽（对齐浮球拖拽语义：阈值后移动，释放持久化 ball-pos）----

    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int wmMouseMove = 0x0200;
        const int wmLeftDown = 0x0201;
        const int wmLeftUp = 0x0202;
        const int wmRightDown = 0x0204;
        const int wmNcHitTest = 0x0084;
        const nint htClient = 1;
        const nint mkLeftButton = 0x0001;
        if (msg == wmNcHitTest)
        {
            // WPF 透明窗口默认对透明像素返回 HTTRANSPARENT → 点击穿透 → 左键菜单/拖拽失效。
            // 浮球仅 80x80，整体可点即可（对齐“点球开菜单”预期）。
            handled = true;
            return htClient;
        }
        switch (msg)
        {
            case wmLeftDown:
                if (_menuOpen) { CloseMenu(); return 0; }
                _pressed = true;
                _pressPoint = ClientPoint(lParam);
                var cursor = NativeMethods.CursorPosition();
                var (wx, wy) = PhysicalPosition();
                _grabOffset = (cursor.X - wx, cursor.Y - wy);
                handled = true;
                break;
            case wmMouseMove when _pressed && (wParam.ToInt64() & mkLeftButton) != 0:
                if (!_dragging && MovedBeyondThreshold(ClientPoint(lParam)))
                {
                    _dragging = true;
                }
                if (_dragging)
                {
                    var c = NativeMethods.CursorPosition();
                    NativeMethods.MoveWindow(_hwnd, c.X - _grabOffset.X, c.Y - _grabOffset.Y);
                }
                handled = true;
                break;
            case wmLeftUp when _pressed:
                _pressed = false;
                if (_dragging)
                {
                    _dragging = false;
                    PersistPosition();
                }
                else
                {
                    OpenMenu();
                }
                handled = true;
                break;
            case wmRightDown:
                _pressed = false;
                ShowSettingsPlaceholder();
                handled = true;
                break;
        }
        return 0;
    }

    private static (int X, int Y) ClientPoint(nint lParam)
    {
        var raw = lParam.ToInt64();
        return ((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
    }

    private bool MovedBeyondThreshold((int X, int Y) current)
        => Math.Abs(current.X - _pressPoint.X) > 4 || Math.Abs(current.Y - _pressPoint.Y) > 4;

    private (int X, int Y) PhysicalPosition()
    {
        var rect = new NativeMethods.RECT();
        NativeMethods.GetWindowRect(_hwnd, ref rect);
        return (rect.Left, rect.Top);
    }

    private void PersistPosition()
    {
        try
        {
            var (x, y) = PhysicalPosition();
            AtomicFileWriter.WriteAllText(_positionFilePath, $"{x},{y}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("FloatingBall", $"position save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RestorePosition()
    {
        try
        {
            if (!File.Exists(_positionFilePath)) return;
            var parts = File.ReadAllText(_positionFilePath).Trim().Split(',');
            if (parts.Length != 2) return;
            var x = int.Parse(parts[0]);
            var y = int.Parse(parts[1]);
            NativeMethods.MoveWindow(_hwnd, x, y);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            _logger.Error("FloatingBall", $"position restore failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- 左键菜单（快速气泡：输入 + 预设胶囊 + 发送）----

    private Popup? _menu;
    private TextBox? _input;

    private void OpenMenu()
    {
        if (_menuOpen) return;
        _menuOpen = true;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x1C, 0x20, 0x28)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16),
            Width = 272,
            Effect = (Effect)Application.Current.FindResource("ShadowFloat"),
        };
        var stack = new StackPanel();

        var header = new TextBlock
        {
            Text = "跟宠物说点什么…",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x26)),
            Margin = new Thickness(0, 0, 0, 8),
        };
        stack.Children.Add(header);

        _input = new TextBox
        {
            FontSize = 13,
            Padding = new Thickness(10, 6, 10, 6),
            Height = 32,
        };
        stack.Children.Add(_input);

        var presets = ReadPresets();
        if (presets.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var preset in presets)
            {
                var chip = new Button
                {
                    FontSize = 11.5,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(10, 4, 10, 4),
                    Height = 26,
                    Style = (Style)Application.Current.FindResource("ButtonGhostStyle"),
                };
                WpfLocalizer.SetDynamicContent(chip, preset);
                chip.Click += (_, _) => SendAndClose(preset);
                wrap.Children.Add(chip);
            }
            stack.Children.Add(wrap);
        }

        // 主操作行：聊天（主）+ 发送（次）
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (_openChat is not null)
        {
            var chatButton = new Button
            {
                Content = "打开对话",
                FontSize = 12.5,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.FindResource("ButtonPrimaryStyle"),
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(chatButton, "ball-chat-btn");
            chatButton.Click += (_, _) =>
            {
                CloseMenu();
                _openChat();
            };
            actionRow.Children.Add(chatButton);
        }
        var send = new Button
        {
            Content = "发送",
            FontSize = 12.5,
            Height = 30,
            Style = (Style)Application.Current.FindResource("ButtonDefaultStyle"),
        };
        send.Click += (_, _) => SendAndClose(_input.Text);
        actionRow.Children.Add(send);
        stack.Children.Add(actionRow);

        // AI 输出模式行（Phase 5）：弹幕 / 对话 / 静默；静默 = 停 Agent 无主动输出
        if (_setOutputMode is not null)
        {
            var divider = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x1C, 0x20, 0x28)),
                Margin = new Thickness(0, 10, 0, 10),
            };
            stack.Children.Add(divider);

            var modeRow = new WrapPanel();
            var label = new TextBlock
            {
                Text = "AI 输出",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 6),
            };
            modeRow.Children.Add(label);
            foreach (var (id, name) in new[] { ("bubble", "气泡"), ("danmaku", "弹幕"), ("chat", "对话"), ("silent", "仅聊天") })
            {
                var modeButton = new Button
                {
                    Content = name,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(10, 3, 10, 3),
                    Height = 25,
                    Style = (Style)Application.Current.FindResource("ButtonDefaultStyle"),
                };
                modeButton.Click += (_, _) =>
                {
                    CloseMenu();
                    _setOutputMode(id);
                };
                modeRow.Children.Add(modeButton);
            }
            stack.Children.Add(modeRow);
        }

        card.Child = stack;
        WpfLocalizer.ApplyNew(card, _i18n);
        _menu = new Popup
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            AllowsTransparency = true,
            IsOpen = true,
            StaysOpen = false,
            Child = card,
        };
        _menu.Closed += (_, _) => { _menuOpen = false; _menu = null; };
        _input.Focus();
    }

    private void CloseMenu()
    {
        if (_menu is not null) _menu.IsOpen = false;
        _menuOpen = false;
    }

    private void SendAndClose(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { CloseMenu(); return; }
        CloseMenu();
        _sendQuickBubble(text.Trim());
    }

    private List<string> ReadPresets()
    {
        try
        {
            var raw = _readPresetPool();
            var list = System.Text.Json.JsonSerializer.Deserialize<string[]>(raw);
            return list?.Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>右键：设置 / 退出（退出入口兜底——即使托盘异常也必须能退出应用）。</summary>
    private void ShowSettingsPlaceholder()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var settingsItem = new System.Windows.Controls.MenuItem { Header = _i18n.T("Settings") };
        settingsItem.Click += (_, _) => _openSettings();
        var quitItem = new System.Windows.Controls.MenuItem { Header = _i18n.T("Quit") };
        quitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(quitItem);
        menu.PlacementTarget = this; // 默认 Placement=MousePoint：在鼠标位置弹出
        menu.IsOpen = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _petTimer.Stop();
        _hwndSource?.RemoveHook(WndProcHook); // 钩子由弱引用持有（官方文档），窗口关闭显式摘除
        base.OnClosed(e);
    }
}

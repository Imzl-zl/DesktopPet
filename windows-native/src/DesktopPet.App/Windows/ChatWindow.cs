using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopPet.App.Windows;

/// <summary>
/// 桌宠对话窗口：清晰的会话层级、可伸缩消息流与不干扰输入区。
/// </summary>
public sealed class ChatWindow : Window
{
    private const double BubbleGap = 10;
    private const double MessageSideInset = 64;

    private readonly StackPanel _messages = new() { Margin = new Thickness(16, 18, 16, 14) };
    private readonly ScrollViewer _scroll = new();
    private readonly TextBox _input = new()
    {
        FontSize = 13,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 32,
        MaxHeight = 84,
        VerticalContentAlignment = VerticalAlignment.Center,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Padding = new Thickness(10, 5, 8, 5),
    };
    private readonly TextBlock _placeholder = new()
    {
        Text = "和桌宠说点什么…",
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA8, 0xB4)),
        Margin = new Thickness(10, 5, 8, 5),
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _sendButton = new()
    {
        Width = 36,
        Height = 36,
        Content = CreateIconPath(SendIcon, Brushes.White),
        Padding = new Thickness(0),
        Style = (Style)Application.Current.FindResource("ButtonCircleStyle"),
        ToolTip = "发送消息",
    };
    private readonly Button _personaButton = new()
    {
        Height = 24,
        FontSize = 11.5,
        Padding = new Thickness(0),
        Style = (Style)Application.Current.FindResource("ButtonGhostStyle"),
        ToolTip = "切换人格",
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private static readonly Geometry SendIcon = Geometry.Parse("M 2,2 L 15,8 L 2,14 L 5,8 Z");
    private static readonly Geometry ScreenContextIcon = Geometry.Parse("M 2,3 L 14,3 L 14,11 L 2,11 Z M 5,14 L 11,14 M 8,11 L 8,14");
    private static readonly Geometry RestartIcon = Geometry.Parse("M 13,5 C 12,2 9,1 6,2 C 2,3 1,7 2,10 C 3,14 7,15 10,13 C 12,12 13,10 13,8 M 13,1 L 13,5 L 9,5");
    private static readonly Geometry SpeakerIcon = Geometry.Parse("M 2,7 L 5,7 L 9,3 L 9,13 L 5,9 L 2,9 Z M 11,6 C 13,7 13,9 11,10");
    private static readonly Geometry MutedIcon = Geometry.Parse("M 2,7 L 5,7 L 9,3 L 9,13 L 5,9 L 2,9 Z M 11,5 L 14,11 M 14,5 L 11,11");

    private readonly Button _screenContextButton = IconButton(ScreenContextIcon, "切换屏幕上下文");
    private readonly Button _restartButton = IconButton(RestartIcon, "重新开始对话");
    private readonly Button _ttsButton = IconButton(MutedIcon, "切换朗读回复");
    private readonly Ellipse _statusDot = new()
    {
        Width = 7,
        Height = 7,
        Fill = new SolidColorBrush(Color.FromRgb(0x5B, 0x9D, 0xF0)),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly DispatcherTimer _typewriterTimer;
    private string _pendingAssistantText = "";
    private int _typewriterIndex;
    private TextBlock? _typingBlock;
    private string _currentPersonaName = "桌宠";
    private bool _screenContextEnabled;
    private bool _ttsEnabled;

    /// <summary>发送消息（App 接线：走 ChatPipeline）。参数：文本 + 是否带屏幕上下文。</summary>
    public event Action<string, bool>? SendRequested;

    /// <summary>切换人格（App 接线：写 personas.json selectedId）。</summary>
    public event Action<string>? PersonaSwitchRequested;

    /// <summary>从此重新开始（清空会话上下文；记忆/亲密度不受影响）。</summary>
    public event Action? RestartRequested;

    public string CurrentPersonaName
    {
        get => _currentPersonaName;
        set
        {
            _currentPersonaName = string.IsNullOrWhiteSpace(value) ? "桌宠" : value;
            _personaButton.Content = _currentPersonaName + "  v";
        }
    }

    public bool ScreenContextEnabled
    {
        get => _screenContextEnabled;
        set
        {
            _screenContextEnabled = value;
            UpdateScreenContextButton();
        }
    }

    /// <summary>AI 助手页注入的朗读偏好；窗口内按钮只切换当前会话状态。</summary>
    public bool TtsEnabled
    {
        get => _ttsEnabled;
        set
        {
            _ttsEnabled = value;
            UpdateTtsButton();
        }
    }

    public ChatWindow()
    {
        Title = "DesktopPet 对话";
        Width = 400;
        Height = 540;
        MinWidth = 320;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        Background = Brush("WindowBgBrush");
        WindowStyle = WindowStyle.ToolWindow;

        AutomationProperties.SetAutomationId(_sendButton, "chat-send");
        AutomationProperties.SetAutomationId(_personaButton, "chat-persona");
        AutomationProperties.SetName(_screenContextButton, "切换屏幕上下文");
        AutomationProperties.SetName(_restartButton, "重新开始对话");
        AutomationProperties.SetName(_ttsButton, "切换朗读回复");
        AutomationProperties.SetName(_sendButton, "发送消息");
        CurrentPersonaName = _currentPersonaName;
        UpdateScreenContextButton();
        UpdateTtsButton();

        var toolbar = BuildToolbar();
        _scroll.Content = _messages;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Focusable = false;

        var composer = BuildComposer();
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(composer, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(composer);
        root.Children.Add(_scroll);
        Content = root;

        _sendButton.Click += (_, _) => Submit();
        _input.TextChanged += (_, _) => _placeholder.Visibility = _input.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                Submit();
                e.Handled = true;
            }
        };
        _personaButton.Click += (_, _) => PersonaSwitchRequested?.Invoke(_currentPersonaName);
        _screenContextButton.Click += (_, _) => ScreenContextEnabled = !ScreenContextEnabled;
        _restartButton.Click += (_, _) =>
        {
            ClearMessages();
            RestartRequested?.Invoke();
        };
        _ttsButton.Click += (_, _) => TtsEnabled = !TtsEnabled;

        _typewriterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _typewriterTimer.Tick += (_, _) => TypewriterStep();
        SizeChanged += (_, _) => UpdateBubbleMaxWidths();
    }

    protected override void OnClosed(EventArgs e)
    {
        _typewriterTimer.Stop();
        base.OnClosed(e);
    }

    /// <summary>清空全部消息；记忆和亲密度保留。</summary>
    public void ClearMessages()
    {
        _typewriterTimer.Stop();
        _typingBlock = null;
        _messages.Children.Clear();
        _pendingAssistantText = "";
        _typewriterIndex = 0;
    }

    public void AppendUser(string text) => AddBubble(text, isUser: true);

    /// <summary>追加一条 AI 回复（保留打字机效果）。</summary>
    public void AppendAssistantAsync(string text)
    {
        SetThinking(false);
        if (text.Length == 0) return;
        _pendingAssistantText = text;
        _typewriterIndex = 0;
        _typingBlock = AddBubble("", isUser: false);
        _typewriterTimer.Start();
    }

    public void SetThinking(bool thinking)
    {
        _statusDot.Fill = Brush(thinking ? "ThinkingBrush" : "InfoBrush");
        _statusDot.ToolTip = thinking ? "正在思考" : "在线";
    }

    private Border BuildToolbar()
    {
        var toolbar = new Grid { Margin = new Thickness(16, 12, 16, 10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            Background = Brush("AccentSoftBrush"),
            CornerRadius = new CornerRadius(10),
            Child = new TextBlock
            {
                Text = "\uE8BD",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = Brush("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var title = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock
        {
            Text = "桌宠对话",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
        });
        title.Children.Add(_personaButton);
        left.Children.Add(title);
        Grid.SetColumn(left, 0);
        toolbar.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_statusDot);
        _screenContextButton.Margin = new Thickness(7, 0, 0, 0);
        actions.Children.Add(_screenContextButton);
        actions.Children.Add(_restartButton);
        actions.Children.Add(_ttsButton);
        Grid.SetColumn(actions, 1);
        toolbar.Children.Add(actions);

        return new Border
        {
            BorderBrush = Brush("DividerBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbar,
        };
    }

    private Border BuildComposer()
    {
        var editorLayer = new Grid();
        editorLayer.Children.Add(_input);
        editorLayer.Children.Add(_placeholder);

        var composer = new Grid();
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.Children.Add(editorLayer);
        _sendButton.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_sendButton, 1);
        composer.Children.Add(_sendButton);

        return new Border
        {
            Background = Brush("CardBgBrush"),
            BorderBrush = Brush("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(4),
            Margin = new Thickness(16, 8, 16, 16),
            Child = composer,
        };
    }

    private void Submit()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Clear();
        AppendUser(text);
        SendRequested?.Invoke(text, ScreenContextEnabled);
        SetThinking(true);
    }

    private void UpdateBubbleMaxWidths()
    {
        var max = MessageMaxWidth();
        foreach (var child in _messages.Children)
        {
            if (child is Border bubble) bubble.MaxWidth = max;
        }
    }

    private double MessageMaxWidth() => Math.Max(180, ActualWidth - 32 - MessageSideInset);

    private void TypewriterStep()
    {
        if (_typingBlock is null) return;
        _typewriterIndex = Math.Min(_typewriterIndex + 2, _pendingAssistantText.Length);
        _typingBlock.Text = _pendingAssistantText[.._typewriterIndex];
        _scroll.ScrollToEnd();
        if (_typewriterIndex >= _pendingAssistantText.Length)
        {
            _typewriterTimer.Stop();
            _typingBlock = null;
        }
    }

    private TextBlock AddBubble(string text, bool isUser)
    {
        var bubble = new Border
        {
            Background = Brush(isUser ? "UserBubbleBrush" : "AiBubbleBrush"),
            BorderBrush = isUser ? Brush("UserBubbleBrush") : Brush("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = isUser
                ? new CornerRadius(16, 16, 5, 16)
                : new CornerRadius(16, 16, 16, 5),
            Padding = new Thickness(13, 9, 13, 9),
            Margin = new Thickness(isUser ? MessageSideInset : 0, 0, isUser ? 0 : MessageSideInset, BubbleGap),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = MessageMaxWidth(),
        };
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isUser ? Brushes.White : Brush("TextPrimaryBrush"),
        };
        bubble.Child = block;
        _messages.Children.Add(bubble);
        _scroll.ScrollToEnd();
        return block;
    }

    private void UpdateScreenContextButton()
    {
        _screenContextButton.Background = _screenContextEnabled ? Brush("AccentSoftBrush") : Brushes.Transparent;
        SetIconBrush(_screenContextButton, _screenContextEnabled ? Brush("AccentBrush") : Brush("TextSecondaryBrush"));
    }

    private void UpdateTtsButton()
    {
        SetIcon(_ttsButton, _ttsEnabled ? SpeakerIcon : MutedIcon,
            _ttsEnabled ? Brush("AccentBrush") : Brush("TextSecondaryBrush"));
    }

    private static Button IconButton(Geometry icon, string tooltip) => new()
    {
        Width = 30,
        Height = 30,
        Content = CreateIconPath(icon, Brush("TextSecondaryBrush")),
        Padding = new Thickness(0),
        Style = (Style)Application.Current.FindResource("ButtonIconStyle"),
        ToolTip = tooltip,
    };

    private static Path CreateIconPath(Geometry data, Brush stroke) => new()
    {
        Data = data,
        Width = 16,
        Height = 16,
        Stretch = Stretch.Uniform,
        Stroke = stroke,
        StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static void SetIcon(Button button, Geometry data, Brush stroke)
    {
        button.Content = CreateIconPath(data, stroke);
    }

    private static void SetIconBrush(Button button, Brush stroke)
    {
        if (button.Content is Path icon) icon.Stroke = stroke;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

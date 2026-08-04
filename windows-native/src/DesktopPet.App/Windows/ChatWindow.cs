using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet.App.Windows;

/// <summary>
/// 对话气泡窗口（Phase 5）：打字机效果 + 顶部人格快捷切换 + 屏幕上下文开关 + 思考状态。
/// 用户主动对话随时可开（不受输出模式限制）；AI 输出经 <see cref="AppendAssistantAsync"/> 流入。
/// Lumen 风格：浅色毛玻璃卡片、圆角、状态点呼吸。
/// </summary>
public sealed class ChatWindow : Window
{
    private readonly StackPanel _messages = new() { Margin = new Thickness(16) };
    private readonly ScrollViewer _scroll = new();
    private readonly TextBox _input = new()
    {
        Height = 28,
        FontSize = 13,
        AcceptsReturn = false,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };
    private readonly Button _sendButton = new() { Content = "发送", Width = 56, Height = 28 };
    private readonly Button _personaButton = new() { Height = 28, FontSize = 12 };
    private readonly CheckBox _screenContextCheck = new() { Content = "屏幕上下文", FontSize = 12 };
    private readonly TextBlock _statusDot = new() { Text = "●", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE0)) };
    private readonly DispatcherTimer _typewriterTimer;
    private string _pendingAssistantText = "";
    private int _typewriterIndex;
    private TextBlock? _typingBlock;

    /// <summary>发送消息（App 接线：走 ChatPipeline）。参数：文本 + 是否带屏幕上下文。</summary>
    public event Action<string, bool>? SendRequested;

    /// <summary>切换人格（App 接线：写 personas.json selectedId）。</summary>
    public event Action<string>? PersonaSwitchRequested;

    private string _currentPersonaName = "";
    public string CurrentPersonaName
    {
        get => _currentPersonaName;
        set
        {
            _currentPersonaName = value;
            _personaButton.Content = "人格：" + value + " ▾";
        }
    }

    public bool ScreenContextEnabled
    {
        get => _screenContextCheck.IsChecked == true;
        set => _screenContextCheck.IsChecked = value;
    }

    public ChatWindow()
    {
        Title = "DesktopPet 对话";
        Width = 360;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 300;
        MinHeight = 360;
        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFA, 0xF7, 0xF2)); // Lumen 浅色
        WindowStyle = WindowStyle.ToolWindow;

        // 顶部状态条：状态点 + 人格切换
        var statusBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 4) };
        statusBar.Children.Add(_statusDot);
        statusBar.Children.Add(_personaButton);
        statusBar.Children.Add(_screenContextCheck);

        _scroll.Content = _messages;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 12, 12) };
        inputRow.Children.Add(_input);
        inputRow.Children.Add(_sendButton);

        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Top);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        root.Children.Add(statusBar);
        root.Children.Add(inputRow);
        root.Children.Add(_scroll);
        Content = root;

        _sendButton.Click += (_, _) => Submit();
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };
        _personaButton.Click += (_, _) => PersonaSwitchRequested?.Invoke(_currentPersonaName);

        _typewriterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _typewriterTimer.Tick += (_, _) => TypewriterStep();
    }

    protected override void OnClosed(EventArgs e)
    {
        _typewriterTimer.Stop(); // 窗口销毁后不再驱动打字机
        base.OnClosed(e);
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

    public void AppendUser(string text)
        => AddBubble(text, isUser: true);

    /// <summary>追加一条 AI 回复（打字机效果）。</summary>
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
        => _statusDot.Foreground = new SolidColorBrush(thinking
            ? Color.FromRgb(0xF2, 0xA6, 0x3C) // 思考中：琥珀呼吸
            : Color.FromRgb(0x4A, 0x90, 0xE0));

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
            Background = new SolidColorBrush(isUser
                ? Color.FromArgb(0xFF, 0x4A, 0x90, 0xE0)
                : Color.FromArgb(0xFF, 0xE8, 0xF0, 0xEA)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(isUser ? 48 : 0, 4, isUser ? 0 : 48, 4),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 280,
        };
        var block = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(isUser
                ? Colors.White
                : Color.FromRgb(0x33, 0x33, 0x33)),
        };
        bubble.Child = block;
        _messages.Children.Add(bubble);
        _scroll.ScrollToEnd();
        return block;
    }
}

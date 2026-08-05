namespace DesktopPet.App.Ai;

/// <summary>输出模式四选一（迁移计划 §5 + 体验优化）：模式只决定 AI 主动输出形式；
/// 用户主动对话随时可开（对话窗不受模式限制）。气泡 = 宠物头上气泡文字（默认，
/// 不打断工作）；静默 = 停 Agent + 无主动输出。</summary>
public enum OutputMode
{
    Danmaku,
    Chat,
    Bubble,
    Silent,
}

/// <summary>AI 主动输出（来自 Agent 分析事件或主动回复）。</summary>
public sealed record AiOutput(string Text, bool FromAnalysis);

/// <summary>
/// 输出模式服务：三模式切换（创建/销毁弹幕窗；AI 输出路由到弹幕/对话/丢弃）。
/// 模式切换 <300ms 且关闭后无窗口残留（窗口实例即建即毁）。
/// </summary>
public sealed class ModeService
{
    private readonly Func<Windows.DanmakuWindow> _danmakuFactory;
    private readonly Action<AiOutput> _routeToChat;
    private readonly Action<string> _routeToBubble;

    private Windows.DanmakuWindow? _danmakuWindow;
    private OutputMode _mode = OutputMode.Silent;

    public OutputMode Mode => _mode;

    public event Action<OutputMode>? ModeChanged;

    public ModeService(Func<Windows.DanmakuWindow> danmakuFactory, Action<AiOutput> routeToChat, Action<string> routeToBubble)
    {
        _danmakuFactory = danmakuFactory;
        _routeToChat = routeToChat;
        _routeToBubble = routeToBubble;
    }

    /// <summary>切换模式：立即生效（旧窗口关闭即销毁，新窗口按需创建）。</summary>
    public void SetMode(OutputMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        if (mode != OutputMode.Danmaku)
        {
            CloseDanmakuWindow();
        }
        ModeChanged?.Invoke(mode);
    }

    /// <summary>路由 AI 输出（按当前模式；silent = 丢弃）。</summary>
    public void RouteOutput(AiOutput output)
    {
        switch (_mode)
        {
            case OutputMode.Danmaku:
                if (_danmakuWindow is null || !_danmakuWindow.IsVisible)
                {
                    _danmakuWindow = _danmakuFactory();
                    _danmakuWindow.Show();
                }
                _danmakuWindow.ShowDanmaku(output.Text);
                break;
            case OutputMode.Chat:
                _routeToChat(output); // ChatWindow 按需出现（EnsureVisible 由 App 接线）
                break;
            case OutputMode.Bubble:
                _routeToBubble(output.Text); // 宠物头上气泡（全员），不打断工作
                break;
            case OutputMode.Silent:
                break; // 静默：无主动输出
        }
    }

    public void Shutdown()
    {
        CloseDanmakuWindow();
    }

    private void CloseDanmakuWindow()
    {
        if (_danmakuWindow is not null)
        {
            _danmakuWindow.Close(); // Close 即销毁（模式切换无残留）
            _danmakuWindow = null;
        }
    }
}

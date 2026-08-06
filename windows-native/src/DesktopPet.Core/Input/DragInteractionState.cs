namespace DesktopPet.Core.Input;

public enum DragTerminalAction
{
    None,
    Click,
    CommitPosition,
}

/// <summary>纯输入状态机：系统取消只能回到 Idle，不产生点击或位置提交。</summary>
public sealed class DragInteractionState
{
    public bool IsPressed { get; private set; }
    public bool IsDragging { get; private set; }

    public bool Begin()
    {
        if (IsPressed || IsDragging) return false;
        IsPressed = true;
        return true;
    }

    public bool StartDragging()
    {
        if (!IsPressed || IsDragging) return false;
        IsDragging = true;
        return true;
    }

    public DragTerminalAction Complete()
    {
        if (!IsPressed) return DragTerminalAction.None;
        var action = IsDragging ? DragTerminalAction.CommitPosition : DragTerminalAction.Click;
        IsPressed = false;
        IsDragging = false;
        return action;
    }

    public bool Cancel()
    {
        if (!IsPressed && !IsDragging) return false;
        IsPressed = false;
        IsDragging = false;
        return true;
    }
}

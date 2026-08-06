using DesktopPet.Core.Input;

namespace DesktopPet.App.Tests;

public sealed class DragInteractionStateTests
{
    [Fact]
    public void CancelledPress_ProducesNoClickOrPositionCommit()
    {
        var state = new DragInteractionState();
        Assert.True(state.Begin());

        Assert.True(state.Cancel());

        Assert.False(state.IsPressed);
        Assert.False(state.IsDragging);
        Assert.Equal(DragTerminalAction.None, state.Complete());
    }

    [Fact]
    public void CancelledDrag_ReturnsToIdleAndNextDragCanCommit()
    {
        var state = new DragInteractionState();
        state.Begin();
        state.StartDragging();
        Assert.True(state.Cancel());

        Assert.True(state.Begin());
        Assert.True(state.StartDragging());
        Assert.Equal(DragTerminalAction.CommitPosition, state.Complete());
        Assert.False(state.IsPressed);
        Assert.False(state.IsDragging);
    }

    [Fact]
    public void PressWithoutDrag_CompletesAsClick()
    {
        var state = new DragInteractionState();
        state.Begin();

        Assert.Equal(DragTerminalAction.Click, state.Complete());
        Assert.Equal(DragTerminalAction.None, state.Complete());
    }
}

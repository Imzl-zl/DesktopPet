using DesktopPet.Infra.Lifecycle;

namespace DesktopPet.Infra.Tests;

public class OwnedResourceSlotTests
{
    [Fact]
    public void TakeAndConditionalTake_AllowExactlyOneDisposalOwner()
    {
        var slot = new OwnedResourceSlot<object>();
        var resource = new object();

        Assert.True(slot.TryPublish(resource));
        Assert.Same(resource, slot.Current);
        Assert.Same(resource, slot.Take());
        Assert.False(slot.TryTake(resource, out var duplicate));
        Assert.Null(duplicate);
        Assert.Null(slot.Current);
    }

    [Fact]
    public void ConditionalTake_DoesNotStealAReplacement()
    {
        var slot = new OwnedResourceSlot<object>();
        var first = new object();
        var second = new object();

        Assert.True(slot.TryPublish(first));
        Assert.Same(first, slot.Take());
        Assert.True(slot.TryPublish(second));
        Assert.False(slot.TryTake(first, out _));
        Assert.Same(second, slot.Current);
    }
}

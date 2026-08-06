using DesktopPet.App.Rendering;
using DesktopPet.Core.Rendering;

namespace DesktopPet.App.Tests;

public sealed class SpriteSheetCacheTests
{
    [Fact]
    public void Lru_EvictsLeastRecentlyUsedWithinByteBudget()
    {
        var cache = new SpriteSheetCache(100);
        var a = Sheet(60);
        var b = Sheet(40);
        var c = Sheet(40);

        cache.Put("a", a);
        cache.Put("b", b);
        Assert.Same(a, cache.Get("a")); // a becomes most recently used
        cache.Put("c", c);

        Assert.Same(a, cache.Get("a"));
        Assert.Null(cache.Get("b"));
        Assert.Same(c, cache.Get("c"));
        Assert.Equal(100, cache.CurrentBytes);
    }

    [Fact]
    public void ReplacementAndOversizedEntryRespectBudget()
    {
        var cache = new SpriteSheetCache(100);
        cache.Put("same", Sheet(60));
        cache.Put("same", Sheet(40));
        Assert.Equal(40, cache.CurrentBytes);
        Assert.Equal(1, cache.Count);

        cache.Put("too-large", Sheet(101));
        Assert.Null(cache.Get("too-large"));
        Assert.Equal(40, cache.CurrentBytes);
    }

    [Fact]
    public void ConcurrentPutAndGet_PreservesBudgetInvariant()
    {
        var cache = new SpriteSheetCache(1000);
        Parallel.For(0, 64, index =>
        {
            cache.Put($"sprite-{index}", Sheet(37));
            _ = cache.Get($"sprite-{index / 2}");
        });

        Assert.InRange(cache.CurrentBytes, 0, 1000);
        Assert.InRange(cache.Count, 0, 27);
    }

    private static SpriteSheet Sheet(int bytes)
        => new()
        {
            SourceName = "test",
            SourceWidth = 1,
            SourceHeight = Math.Max(1, bytes / 4),
            SourceRgba = new byte[bytes],
            Clips = [],
            ClipMaxWidths = [],
        };
}

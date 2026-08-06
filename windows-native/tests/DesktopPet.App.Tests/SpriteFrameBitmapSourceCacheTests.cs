using DesktopPet.App.Rendering;
using DesktopPet.Core.Rendering;

namespace DesktopPet.App.Tests;

public sealed class SpriteFrameBitmapSourceCacheTests
{
    [Fact]
    public void GetOrCreate_ConvertsRgbaOnceAndReturnsTheFrozenCachedSource()
    {
        var frame = new SpriteFrame(
            [0x11, 0x22, 0x33, 0x44],
            [1],
            1,
            1);
        var cache = new SpriteFrameBitmapSourceCache();

        var first = cache.GetOrCreate(frame);
        var second = cache.GetOrCreate(frame);
        var pixels = new byte[4];
        first.CopyPixels(pixels, 4, 0);

        Assert.Same(first, second);
        Assert.True(first.IsFrozen);
        Assert.Equal([0x33, 0x22, 0x11, 0x44], pixels);
        Assert.Equal([0x11, 0x22, 0x33, 0x44], frame.Rgba);
    }

    [Fact]
    public void Clear_EvictsCachedSources()
    {
        var frame = new SpriteFrame([1, 2, 3, 4], [1], 1, 1);
        var cache = new SpriteFrameBitmapSourceCache();
        var first = cache.GetOrCreate(frame);

        cache.Clear();
        var next = cache.GetOrCreate(frame);

        Assert.NotSame(first, next);
    }
}

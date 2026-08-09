using DesktopPet.Core.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// SpriteSheet 解码/裁剪：帧内内容顶部（ContentTop）计算 —— 气泡锚点修正用。
/// 切片行带从第一个有内容行开始，但多帧行带内帧顶可能错位（帧 A 顶部像素
/// 只在帧 B 的列段），ContentTop 记录每帧实际可见顶部。
/// </summary>
public class SpriteSheetTests
{
    private static byte[] EncodePng(int width, int height, Func<int, int, byte> alphaAt)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(200, 100, 50, alphaAt(x, y));
            }
        }
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Decode_ComputesContentTop_FromFirstOpaqueRowPerFrame()
    {
        // 40x30 一行带两帧（中间 18-22 透明 gutter 分隔列段）：
        // 左帧（x<18）从 y=5 开始，右帧（x>=22）从 y=0 开始
        // → 行带 [0,30)，左帧帧内前 5 行全透明（内容顶 5），右帧内容顶 0
        var bytes = EncodePng(40, 30, (x, y) =>
            x is >= 18 and < 22 ? (byte)0
            : x < 18 ? (y >= 5 ? (byte)255 : (byte)0)
            : (byte)255);

        var sheet = SpriteSheet.Decode(bytes, "test.png");

        Assert.NotNull(sheet);
        Assert.NotNull(sheet!.ClipContentTops);
        Assert.Single(sheet.Clips);
        Assert.Equal(2, sheet.Clips[0].Count);
        Assert.Equal(5, sheet.Clips[0][0].ContentTop);   // 左帧：顶部 5 行透明
        Assert.Equal(0, sheet.Clips[0][1].ContentTop);   // 右帧：帧顶即内容顶
        Assert.Equal(0, sheet.ClipContentTops![0]);      // clip 取行内最小（帧间气泡不跳）
    }

    [Fact]
    public void Decode_AllOpaqueFrame_HasZeroContentTop()
    {
        // 单帧全实心：帧顶即内容顶
        var bytes = EncodePng(32, 36, (_, _) => (byte)255);

        var sheet = SpriteSheet.Decode(bytes, "test.png");

        Assert.NotNull(sheet);
        Assert.NotNull(sheet!.ClipContentTops);
        Assert.Single(sheet.Clips);
        Assert.Equal(0, sheet.Clips[0][0].ContentTop);
        Assert.Equal(0, sheet.ClipContentTops![0]);
    }
}

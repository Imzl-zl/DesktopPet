using DesktopPet.Core.Care;
using DesktopPet.Core.Rendering;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>成长表现叠加层测试：五阶段视觉逐级验证（§3.7）。</summary>
public class OverlayRendererTests
{
    private static SpriteFrame MakeSolidFrame(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        var mask = new byte[w * h];
        for (var i = 0; i < w * h; i++)
        {
            rgba[i * 4] = 0x50;
            rgba[i * 4 + 1] = 0x50;
            rgba[i * 4 + 2] = 0x50;
            rgba[i * 4 + 3] = 255;
            mask[i] = 1;
        }
        return new SpriteFrame(rgba, mask, w, h);
    }

    private static byte AlphaAt(byte[] buffer, int w, int x, int y) => buffer[(y * w + x) * 4 + 3];

    [Fact]
    public void Hatchling_NoOverlay()
    {
        var buffer = new byte[260 * 320 * 4];
        var frame = MakeSolidFrame(32, 36);

        OverlayRenderer.Apply(buffer, 260, 320, frame, StageAppearances.For(0), 2, 32, 8, 0);

        // 脚下光晕区（精灵底部下方）应无任何叠加像素
        Assert.Equal(0, AlphaAt(buffer, 260, 130, 319));
        // 精灵外轮廓邻域无染色
        Assert.Equal(0, AlphaAt(buffer, 260, 0, 32));
    }

    [Fact]
    public void Companion_GrowsGlowUnderFeet()
    {
        var buffer = new byte[260 * 320 * 4];
        var frame = MakeSolidFrame(32, 36);

        OverlayRenderer.Apply(buffer, 260, 320, frame, StageAppearances.For(1), 2, 32, 8, 0);

        // 精灵底部下方应有 mint 光晕（alpha > 0）
        Assert.True(AlphaAt(buffer, 260, 130, 318) > 0, "glow under feet expected");
    }

    [Fact]
    public void Scout_GainsOutlineGlow()
    {
        var buffer = new byte[260 * 320 * 4];
        var frame = MakeSolidFrame(32, 36);

        OverlayRenderer.Apply(buffer, 260, 320, frame, StageAppearances.For(2), 2, 32, 8, 0);

        // 精灵左侧外扩 1px 处应有 sky 描边
        var x = 2 - 1; // spriteX-1（scale=8，精灵占 2..257）
        Assert.True(x >= 0 && AlphaAt(buffer, 260, x, 100) > 0, "outline glow expected left of sprite");
    }

    [Fact]
    public void Hero_GainsStarParticles()
    {
        var buffer = new byte[260 * 320 * 4];
        var frame = MakeSolidFrame(32, 36);

        OverlayRenderer.Apply(buffer, 260, 320, frame, StageAppearances.For(3), 2, 32, 8, 500);

        // 星点位置随时间变化：不同时间戳输出应有差异（粒子在动）
        var buffer2 = new byte[260 * 320 * 4];
        OverlayRenderer.Apply(buffer2, 260, 320, frame, StageAppearances.For(3), 2, 32, 8, 500 + 1500);

        Assert.NotEqual(buffer, buffer2);
    }

    [Fact]
    public void Legend_DrawsCrownAboveHead()
    {
        var buffer = new byte[260 * 320 * 4];
        var frame = MakeSolidFrame(32, 36);

        OverlayRenderer.Apply(buffer, 260, 320, frame, StageAppearances.For(4), 2, 32, 8, 0);

        // 皇冠在精灵头顶上方（spriteY=32，皇冠 top = 32 - 6*8 = -16 → 画在顶部裁剪区；
        // 验证精灵顶部与皇冠重叠区有金色像素）
        var goldFound = false;
        for (var y = 0; y < 32; y++)
        {
            for (var x = 100; x < 160; x++)
            {
                var off = (y * 260 + x) * 4;
                if (buffer[off] > 200 && buffer[off + 1] > 180 && buffer[off + 3] > 100)
                {
                    goldFound = true;
                    break;
                }
            }
        }
        Assert.True(goldFound, "crown gold pixels expected above sprite head");
    }
}

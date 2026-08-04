using DesktopPet.Core.Rendering;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// PetRenderer 绘制语义测试（1:1 对照 windows/src/pet.ts 的 Pet.draw()）：
/// anchored bottom-center、整数缩放、per-CLIP 缩放（帧间不抖动）、行切换帧号
/// 归零、帧循环取模、override/idle 行优先级、alpha 掩码 hitTest。
/// </summary>
public class PetRendererTests
{
    private static SpriteFrame MakeFrame(int w, int h, byte r, byte g, byte b, bool solid = true)
    {
        var rgba = new byte[w * h * 4];
        var mask = new byte[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var alpha = solid ? (byte)255 : (byte)0;
            if (i % 2 == 0 && !solid) alpha = 0;
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = alpha;
            if (alpha > 16) mask[i] = 1;
        }
        return new SpriteFrame(rgba, mask, w, h);
    }

    private static SpriteSheet MakeSheet(params (int Row, int Frames, int FrameW, int FrameH)[] rows)
    {
        var clips = rows.Select(row =>
            (IReadOnlyList<SpriteFrame>)Enumerable.Range(0, row.Frames)
                .Select(f => MakeFrame(row.FrameW, row.FrameH, (byte)(40 * (row.Row + 1)), (byte)(80 * f), (byte)(120 * row.Row)))
                .ToList()).ToList();
        return new SpriteSheet
        {
            SourceName = "test",
            SourceWidth = 0,
            SourceHeight = 0,
            Clips = clips,
            ClipMaxWidths = clips.Select(c => c.Max(f => f.Width)).ToList(),
        };
    }

    private static byte[] DrawBuffer(PetRenderer renderer, int w = 260, int h = 320)
    {
        var buffer = new byte[w * h * 4];
        renderer.DrawFrame(buffer, w, h);
        return buffer;
    }

    private static byte AlphaAt(byte[] buffer, int bufferW, int x, int y)
        => buffer[(y * bufferW + x) * 4 + 3];

    [Fact]
    public void DrawFrame_AnchorsBottomCenter_WithIntegerScale()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36)));
        renderer.SetState("idle");

        var buffer = DrawBuffer(renderer);

        // scale = floor(min(260/32, 320/36)) = floor(8.125) = 8 → 256x288，底部居中
        Assert.Equal((2, 32, 256, 288), renderer.SpriteRect);
        Assert.Equal((320 - 288) / (double)320, renderer.Headroom);
        Assert.Equal(0, AlphaAt(buffer, 260, 0, 0));       // 左上角透明
        Assert.Equal(0, AlphaAt(buffer, 260, 130, 10));    // 精灵上方透明
        Assert.Equal(255, AlphaAt(buffer, 260, 130, 200)); // 精灵身体不透明
        Assert.Equal(255, AlphaAt(buffer, 260, 10, 315));  // 底部精灵内
    }

    [Fact]
    public void DrawFrame_ClampsFramesToClipLength_WithModulo()
    {
        // 行 0 只有 1 帧 → 循环永远帧 0
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36)));
        renderer.SetState("idle");
        renderer.AdvanceFrame();
        renderer.AdvanceFrame();
        var buffer = DrawBuffer(renderer);
        Assert.Equal(255, AlphaAt(buffer, 260, 130, 200)); // 仍绘制帧 0
    }

    [Fact]
    public void RowSwitch_ResetsFrameToZero()
    {
        // 行 0 帧 0 红色、行 1 帧 0 蓝色（不同 r 值）→ setRow 后立即绘制行 1 帧 0
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (1, 1, 32, 36)));
        renderer.SetState("idle");
        DrawBuffer(renderer);

        renderer.SetRow(1);
        var buffer = DrawBuffer(renderer);

        var idx = (200 * 260 + 130) * 4;
        Assert.Equal(80, buffer[idx]);      // 行 1 帧 0 的 r = 40*(1+1) = 80
        Assert.Equal(255, buffer[idx + 3]);
    }

    [Fact]
    public void Scale_ComesFromWidestFrameOfClip_NotTheCurrentFrame()
    {
        // 行 0：帧 0 宽 20、帧 1 宽 30；maxW=30 → 两帧渲染同尺寸
        var clips = new List<IReadOnlyList<SpriteFrame>>
        {
            new List<SpriteFrame> { MakeFrame(20, 20, 255, 0, 0), MakeFrame(30, 20, 0, 255, 0) },
        };
        var sheet = new SpriteSheet
        {
            SourceName = "test",
            SourceWidth = 0,
            SourceHeight = 0,
            Clips = clips,
            ClipMaxWidths = new List<int> { 30 },
        };
        var renderer = new PetRenderer(sheet);
        renderer.SetState("idle");
        DrawBuffer(renderer);

        // fit = min(260/30, 320/20) = min(8.67, 16) → floor = 8；帧 0 宽 20 → 160px
        Assert.Equal((50, 160, 160, 160), renderer.SpriteRect);

        renderer.AdvanceFrame();
        DrawBuffer(renderer);
        // 帧 1 宽 30 → 240px，两帧底部对齐
        Assert.Equal((10, 160, 240, 160), renderer.SpriteRect);
    }

    [Fact]
    public void HitTest_UsesAlphaMask_WithCurrentFrame()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36)));
        renderer.SetState("idle");
        DrawBuffer(renderer);

        Assert.True(renderer.HitTest(130, 200));  // 精灵体内
        Assert.False(renderer.HitTest(130, 10));  // 精灵上方透明
        Assert.False(renderer.HitTest(0, 0));     // 角落
    }

    [Fact]
    public void OverrideRow_TakesPrecedence_OverIdlePlaylist()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (2, 1, 32, 36), (3, 1, 32, 36)),
            new IdlePlaylistOptions([2], 5000, Random: false));
        renderer.SetState("idle");   // playlist → 行 2
        Assert.Equal(2, renderer.ActiveRow);

        renderer.SetRow(3);          // 漫游覆盖 > playlist
        Assert.Equal(3, renderer.ActiveRow);

        renderer.ClearRow();         // 清除覆盖 → 回到 playlist 行
        Assert.Equal(2, renderer.ActiveRow);
    }

    [Fact]
    public void IdlePlaylist_AdvancesToConfiguredClips()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (2, 1, 32, 36), (3, 1, 32, 36)),
            new IdlePlaylistOptions([2, 3], 5000, Random: false));
        renderer.SetState("idle");
        Assert.Equal(2, renderer.ActiveRow);

        renderer.AdvanceIdleClip();
        Assert.Equal(3, renderer.ActiveRow);

        renderer.AdvanceIdleClip();
        Assert.Equal(2, renderer.ActiveRow); // 顺序循环回第一项
    }

    [Fact]
    public void NonIdleState_StopsIdleCycling()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (2, 1, 32, 36), (7, 1, 32, 36)),
            new IdlePlaylistOptions([2], 5000, Random: false));
        renderer.SetState("idle");
        Assert.Equal(2, renderer.ActiveRow);

        renderer.SetState("working");
        Assert.Equal(7, renderer.ActiveRow);
        Assert.Equal(8, renderer.Fps); // working = 8fps
    }
}

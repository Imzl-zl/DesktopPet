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
    public void PrepareFrame_UpdatesPlacementAndHitTestWithoutRasterizing()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36)));
        renderer.SetState("idle");

        var frame = renderer.PrepareFrame(260, 320);

        Assert.Equal((2, 32, 256, 288), renderer.SpriteRect);
        Assert.Equal((320 - 288) / (double)320, renderer.Headroom);
        Assert.True(renderer.HitTest(130, 200));
        Assert.Same(renderer.CurrentFrame(), frame);
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

    [Fact]
    public void SizePercent_ShrinksSpriteRect_AndKeepsBottomAnchor()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36)));
        renderer.SetState("idle");
        renderer.PrepareFrame(260, 320);
        Assert.Equal((2, 32, 256, 288), renderer.SpriteRect); // 100%：floor(8.125)=8 → 256x288

        renderer.SetSizePercent(0.7); // 70%：floor(8.125*0.7)=floor(5.6875)=5 → 160x180
        renderer.PrepareFrame(260, 320);
        Assert.Equal((50, 140, 160, 180), renderer.SpriteRect);
        Assert.Equal((320 - 180) / (double)320, renderer.Headroom);

        renderer.SetSizePercent(1.3); // 130%：fit=8.125*1.3=10.5625 → floor=10 → 320x360 溢出顶部
        renderer.PrepareFrame(260, 320);
        var (_, y, w, h) = renderer.SpriteRect;
        Assert.Equal(320, w);
        Assert.Equal(360, h);
        Assert.Equal(-40, y);  // 顶部溢出，但仍贴底（bottom-center 锚定）
    }

    [Fact]
    public void SizePercent_ClampsOutOfRange()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36)));
        renderer.SetSizePercent(0.1);
        renderer.SetSizePercent(9.0);
        renderer.SetState("idle");
        renderer.PrepareFrame(260, 320);
        // 钳制到 [0.7, 1.3] → 1.3 分支：fit = 8.125*1.3 = 10.5625 → 非整数缩放
        Assert.NotEqual(256, renderer.SpriteRect.W);
    }

    [Fact]
    public void Bob_MovesSpriteUpFromGround_AndHitsBounds()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36)));
        renderer.SetState("idle");
        renderer.PrepareFrame(260, 320);
        Assert.Equal(32, renderer.SpriteRect.Y); // 无 bob：贴底

        renderer.SetBob(true);
        var minY = int.MaxValue;
        var maxY = int.MinValue;
        for (var i = 0; i < 60; i++) // 60 帧覆盖多个正弦周期（0.35 rad/帧）
        {
            renderer.AdvanceFrame();
            renderer.PrepareFrame(260, 320);
            minY = Math.Min(minY, renderer.SpriteRect.Y);
            maxY = Math.Max(maxY, renderer.SpriteRect.Y);
        }
        Assert.True(minY <= 31 && minY >= 26, $"bob 上浮应在 0-6px：minY={minY}");
        Assert.Equal(32, maxY); // 最低回到贴地（不穿底）

        renderer.SetBob(false);
        renderer.PrepareFrame(260, 320);
        Assert.Equal(32, renderer.SpriteRect.Y); // 关闭后回贴底
    }

    [Fact]
    public void SetIdlePlaylist_EnablesFromScratch_WithoutRecreatingRenderer()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (1, 1, 32, 36), (2, 1, 32, 36), (3, 1, 32, 36)));
        renderer.SetState("idle");
        Assert.False(renderer.IsIdleCycling);
        Assert.Equal(0, renderer.ActiveRow); // 无 playlist：idle 行

        renderer.SetIdlePlaylist(new IdlePlaylistOptions([2, 3], 5000, Random: false));

        Assert.True(renderer.IsIdleCycling);
        Assert.Equal(2, renderer.ActiveRow); // 立即进入第一项
        renderer.AdvanceIdleClip();
        Assert.Equal(3, renderer.ActiveRow);
    }

    [Fact]
    public void SetIdlePlaylist_Disable_ReturnsToIdleRowFirstFrame()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (2, 1, 32, 36), (3, 1, 32, 36)),
            new IdlePlaylistOptions([2], 5000, Random: false));
        renderer.SetState("idle");
        Assert.Equal(2, renderer.ActiveRow);

        renderer.SetIdlePlaylist(null); // 0 = 关闭（运行时即时）

        Assert.False(renderer.IsIdleCycling);
        Assert.Equal(0, renderer.ActiveRow);
    }

    [Fact]
    public void SetIdlePlaylist_FiltersOutOfRangeAndDuplicates()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (2, 1, 32, 36), (3, 1, 32, 36)));

        renderer.SetIdlePlaylist(new IdlePlaylistOptions([2, 2, 99, -1], 5000, Random: false));

        Assert.Equal(2, renderer.ActiveRow);
        renderer.AdvanceIdleClip();
        Assert.Equal(2, renderer.ActiveRow); // 去重后只有一项，顺序循环回自身
    }

    [Fact]
    public void SetIdlePlaylist_InvalidatesCurrentRow_WhenRemovedFromClips()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (1, 1, 32, 36), (2, 1, 32, 36), (3, 1, 32, 36)),
            new IdlePlaylistOptions([2, 3], 5000, Random: false));
        renderer.SetState("idle");
        renderer.AdvanceIdleClip();
        Assert.Equal(3, renderer.ActiveRow);

        renderer.SetIdlePlaylist(new IdlePlaylistOptions([2], 5000, Random: false)); // 移除行 3

        Assert.Equal(2, renderer.ActiveRow); // 当前行失效 → 重置回第一项
    }

    [Fact]
    public void SetIdlePlaylist_KeepsCurrentRow_WhenStillInClips()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (1, 1, 32, 36), (2, 1, 32, 36), (3, 1, 32, 36)),
            new IdlePlaylistOptions([2, 3], 5000, Random: false));
        renderer.SetState("idle");
        renderer.AdvanceIdleClip();
        Assert.Equal(3, renderer.ActiveRow);

        renderer.SetIdlePlaylist(new IdlePlaylistOptions([2, 3], 10_000, Random: true)); // 仅改间隔/模式

        Assert.Equal(3, renderer.ActiveRow); // 当前行仍有效 → 不跳变
    }

    [Fact]
    public void DrawFrame_ClipsOutOfBoundsPixels_WhenSizeExceedsBuffer()
    {
        // 130% 尺寸 + 成长叠加路径（写帧缓冲而非 Image 缓存）：顶部溢出（负 Y）/右侧溢出
        // 必须被裁剪，不能 IndexOutOfRangeException（回归：DrawFrame 直写 buffer）
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36)));
        renderer.SetState("idle");
        renderer.SetSizePercent(1.3);
        renderer.PrepareFrame(260, 320);
        Assert.Equal(-40, renderer.SpriteRect.Y); // 顶部溢出 40px

        var buffer = DrawBuffer(renderer); // 不应抛异常（裁剪保证不写穿缓冲）

        Assert.Equal(255, AlphaAt(buffer, 260, 130, 315)); // 底部可见区域仍绘制
        Assert.Equal(255, AlphaAt(buffer, 260, 10, 100));  // 精灵顶部溢出部分仍绘制在可见区内
    }

    [Fact]
    public void RowOverride_OwnerPriority_BlocksLowerPriority()
    {
        var renderer = new PetRenderer(MakeSheet(
            (0, 1, 32, 36), (1, 1, 32, 36), (2, 1, 32, 36), (3, 1, 32, 36),
            (4, 1, 32, 36), (5, 1, 32, 36), (6, 1, 32, 36), (7, 1, 32, 36)));
        renderer.SetState("idle");

        renderer.SetRow(3, "click", PetRenderer.PriorityClick);
        renderer.SetRow(2, "roam", PetRenderer.PriorityRoam); // 低优先级被拒
        Assert.Equal(3, renderer.ActiveRow);

        renderer.SetRow(7, "drag", PetRenderer.PriorityDrag); // 高优先级覆盖
        Assert.Equal(7, renderer.ActiveRow);

        renderer.ClearRow("roam"); // 非持有者清除被拒
        Assert.Equal(7, renderer.ActiveRow);
        renderer.ClearRow("drag");
        Assert.Equal(0, renderer.ActiveRow); // drag 释放 → 回 idle（被覆盖的 click 不恢复，roam 由 tick 重设）
        renderer.ClearRow("click");
        Assert.Equal(0, renderer.ActiveRow); // 已空，清除无副作用
    }

    [Fact]
    public void RowOverride_NoOwnerApi_StillClearsAnything()
    {
        var renderer = new PetRenderer(MakeSheet((0, 1, 32, 36), (2, 1, 32, 36)));
        renderer.SetRow(2, "roam", PetRenderer.PriorityRoam);
        Assert.Equal(2, renderer.ActiveRow);

        renderer.ClearRow(); // 旧 API：清除任意
        Assert.Equal(0, renderer.ActiveRow);
    }
}

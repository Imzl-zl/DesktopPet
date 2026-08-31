using DesktopPet.Core.SpriteSkill;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DesktopPet.Core.Tests.SpriteSkill;

/// <summary>精灵图流水线：切帧/拼图/校验（生成端，与消费端 alpha-gutter 语义对齐）。</summary>
public class SpritePipelineTests
{
    // ---- Helpers（全部使用项目已验证的 ImageSharp 3.1.11 API）----

    /// <summary>构造单行贴图：frameCount 个纯色格子，格子间 gutter 全透明。</summary>
    private static Image<Rgba32> CreateStrip(int frameCount, int cellW, int cellH, int gutter, int pad = 0)
    {
        var w = pad * 2 + frameCount * cellW + (frameCount - 1) * gutter;
        var h = pad * 2 + cellH;
        var img = new Image<Rgba32>(w, h);
        for (var i = 0; i < frameCount; i++)
        {
            var x = pad + i * (cellW + gutter);
            for (var yy = 0; yy < cellH; yy++)
                for (var xx = 0; xx < cellW; xx++)
                    img[x + xx, pad + yy] = new Rgba32((byte)(40 + i * 40), 80, 200, 255);
        }
        return img;
    }

    /// <summary>构造整图：每行一个动作（可能行帧数不同）。</summary>
    private static Image<Rgba32> CreateRaggedSheet(int[] rowFrameCounts, int cellW, int cellH, int gutter)
    {
        var maxCols = rowFrameCounts.Max();
        var w = maxCols * cellW + Math.Max(0, maxCols - 1) * gutter;
        var h = rowFrameCounts.Length * cellH + Math.Max(0, rowFrameCounts.Length - 1) * gutter;
        var img = new Image<Rgba32>(w, h);
        for (var row = 0; row < rowFrameCounts.Length; row++)
        {
            var y = row * (cellH + gutter);
            for (var col = 0; col < rowFrameCounts[row]; col++)
            {
                var x = col * (cellW + gutter);
                for (var yy = 0; yy < cellH; yy++)
                    for (var xx = 0; xx < cellW; xx++)
                        img[x + xx, y + yy] = new Rgba32(60, (byte)(100 + col * 40), 150, 255);
            }
        }
        return img;
    }

    /// <summary>从整图裁出一行带（行高 bandH），用于 roundtrip 验证。</summary>
    private static Image<Rgba32> CropRowBand(Image<Rgba32> sheet, int y, int bandH)
    {
        var w = sheet.Width;
        var src = new byte[w * sheet.Height * 4];
        sheet.CopyPixelDataTo(src);
        var outBytes = new byte[w * bandH * 4];
        for (var row = 0; row < bandH; row++)
        {
            var srcStart = ((y + row) * w) * 4;
            var dstStart = row * w * 4;
            Array.Copy(src, srcStart, outBytes, dstStart, w * 4);
        }
        return Image.LoadPixelData<Rgba32>(outBytes, w, bandH);
    }

    // ---- SliceStrip ----

    [Fact]
    public void SliceStrip_SplitsStripByAlphaGutter()
    {
        using var strip = CreateStrip(frameCount: 3, cellW: 20, cellH: 24, gutter: 8);
        var frames = SpritePipeline.SliceStrip(strip, frameCount: 3);

        Assert.Equal(3, frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(20, frames[i].Width);
            Assert.Equal(24, frames[i].Height);
            frames[i].Dispose();
        }
    }

    [Fact]
    public void SliceStrip_Throws_WhenFrameCountMismatchesBands()
    {
        using var strip = CreateStrip(frameCount: 3, cellW: 20, cellH: 24, gutter: 8);
        var ex = Assert.Throws<SpritePipelineException>(() => SpritePipeline.SliceStrip(strip, frameCount: 5));
        Assert.Contains("3", ex.Message);
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void SliceStrip_ReturnsEmpty_ForFullyTransparentStrip()
    {
        using var strip = new Image<Rgba32>(60, 24); // 全透明
        var frames = SpritePipeline.SliceStrip(strip, frameCount: 2);
        Assert.Empty(frames);
    }

    // ---- ComposeSheet ----

    [Fact]
    public void ComposeSheet_BuildsRaggedRows_Roundtrip()
    {
        var cell = new CellSpec(20, 24);
        using var strip3 = CreateStrip(3, 20, 24, 8);
        using var strip2 = CreateStrip(2, 20, 24, 8);
        var row3 = SpritePipeline.SliceStrip(strip3, 3);
        var row2 = SpritePipeline.SliceStrip(strip2, 2);

        using var sheet = SpritePipeline.ComposeSheet(new[] { row3, row2 }, cell, gutter: 1);
        var bandH = cell.Height;
        var stride = cell.Height + 1;

        var band0 = CropRowBand(sheet, 0, bandH);
        var band1 = CropRowBand(sheet, stride, bandH);
        var back0 = SpritePipeline.SliceStrip(band0, 3);
        var back1 = SpritePipeline.SliceStrip(band1, 2);

        Assert.Equal(3, back0.Count);
        Assert.Equal(2, back1.Count);
        foreach (var f in back0.Concat(back1)) f.Dispose();
    }

    [Fact]
    public void ComposeSheet_CellsAreCentered_WithinTheirSlot()
    {
        var cell = new CellSpec(40, 40);
        using var small = CreateStrip(1, 20, 20, 0);
        using var sheet = SpritePipeline.ComposeSheet(new[] { new[] { small } }, cell, gutter: 0);

        Assert.Equal(40, sheet.Width);
        Assert.Equal(40, sheet.Height);
        // 帧居中：左上角必须透明（而非贴左上角）。
        Assert.Equal(0, sheet[0, 0].A);
    }

    // ---- ValidateSheet ----

    [Fact]
    public void ValidateSheet_Ok_ForMatchingSheet()
    {
        var cell = new CellSpec(20, 24);
        using var sheet = CreateRaggedSheet(new[] { 3, 2 }, 20, 24, 1);
        var actions = new[]
        {
            new ActionSpec("idle", 3, Loop: true),
            new ActionSpec("jump", 2, Loop: false),
        };
        var report = SpritePipeline.ValidateSheet(sheet, actions, cell);
        Assert.True(report.Ok, string.Join("; ", report.Issues));
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void ValidateSheet_ReportsMissingFrames_WithoutCrashing()
    {
        var cell = new CellSpec(20, 24);
        // 只有 1 行，却声明 2 个动作 —— 校验必须报告失败，而不是越界崩溃。
        using var sheet = CreateRaggedSheet(new[] { 3 }, 20, 24, 1);
        var actions = new[]
        {
            new ActionSpec("idle", 3, Loop: true),
            new ActionSpec("jump", 2, Loop: false),
        };
        var report = SpritePipeline.ValidateSheet(sheet, actions, cell);
        Assert.False(report.Ok);
        Assert.NotEmpty(report.Issues);
    }

    // MARK: 大帧缩放（AI 高分辨率行贴图切出的帧远大于 cell）

    [Fact]
    public void ComposeSheet_FitsOversizedFrames_IntoCell()
    {
        // 帧远大于 cell（模拟 1312x736 行贴图切出 350x330 的帧）
        using var big = new Image<Rgba32>(350, 330);
        for (var y = 0; y < 330; y++)
            for (var x = 0; x < 350; x++)
                big[x, y] = new Rgba32(200, 100, 50, 255);

        var cell = new CellSpec(192, 208);
        var rows = new[] { new[] { big, big, big } };
        using var sheet = SpritePipeline.ComposeSheet(rows, cell);
        var report = SpritePipeline.ValidateSheet(sheet, new[] { new ActionSpec("idle", 3) }, cell);
        Assert.True(report.Ok, $"大帧未缩放到 cell 内: {string.Join("; ", report.Issues)}");
        Assert.Equal(3 * 192 + 2, sheet.Width);
    }
}

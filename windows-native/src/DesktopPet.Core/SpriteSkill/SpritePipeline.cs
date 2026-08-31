using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using DesktopPet.Core.Slicing;

namespace DesktopPet.Core.SpriteSkill;

/// <summary>
/// 精灵图确定性流水线（生成端）：切帧 / 拼图 / 校验。
/// 与消费端语义对齐（DesktopPet.Core.Slicing.SpriteSlicer 的 alpha-gutter 检测）：
/// 生成的图集必须带透明 gutter，消费端才能按同一算法切回同样的行与帧。
/// 全部操作基于 ImageSharp 3.1.11（项目已锁定版本，API 用法与
/// ChromakeyTransparencyStrategy / SpriteSheet 保持一致）。
/// </summary>
public static class SpritePipeline
{
    /// <summary>
    /// 把单行贴图切成 frameCount 帧（复用 SpriteSlicer 的 alpha-gutter 语义）。
    /// 段数与 frameCount 不一致时抛 SpritePipelineException（不静默回退）。
    /// 全透明返回空列表。
    /// </summary>
    public static IReadOnlyList<Image<Rgba32>> SliceStrip(Image<Rgba32> strip, int frameCount)
    {
        var w = strip.Width;
        var h = strip.Height;
        var rgba = new byte[w * h * 4];
        strip.CopyPixelDataTo(rgba);

        var clips = SpriteSlicer.Slice(rgba, w, h);
        if (clips.Count == 0) return [];
        var row = clips[0];
        if (row.Count != frameCount)
        {
            throw new SpritePipelineException(
                $"strip has {row.Count} alpha bands, expected {frameCount}");
        }
        return row.Select(rect => CropFrame(rgba, w, rect)).ToList();
    }

    /// <summary>拼图：每行一个动作，帧在 cell 槽内居中，cell 之间/行之间留透明 gutter。</summary>
    public static Image<Rgba32> ComposeSheet(
        IReadOnlyList<IReadOnlyList<Image<Rgba32>>> rows,
        CellSpec cell,
        int gutter = 1)
    {
        var maxCols = rows.Count > 0 ? rows.Max(r => r.Count) : 0;
        var width = maxCols * cell.Width + Math.Max(0, maxCols - 1) * gutter;
        var height = rows.Count * cell.Height + Math.Max(0, rows.Count - 1) * gutter;
        var sheet = new Image<Rgba32>(width, height);

        for (var row = 0; row < rows.Count; row++)
        {
            var y = row * (cell.Height + gutter);
            for (var col = 0; col < rows[row].Count; col++)
            {
                var x = col * (cell.Width + gutter);
                // AI 行贴图切出的帧常远大于 cell（如 1312x736 → 350x330），先等比缩放到 cell 内再居中。
                using var fit = ResizeFit(rows[row][col], cell.Width, cell.Height);
                DrawCentered(sheet, fit, x, y, cell.Width, cell.Height);
            }
        }
        return sheet;
    }

    /// <summary>校验：行数/每行帧数对齐 ActionSpec，尺寸对齐 cell 布局，gutter 无残留像素。</summary>
    public static SheetReport ValidateSheet(
        Image<Rgba32> sheet,
        IReadOnlyList<ActionSpec> actions,
        CellSpec cell,
        int gutter = 1)
    {
        var issues = new List<string>();

        var expectedRows = actions.Count;
        var maxFrames = actions.Count > 0 ? actions.Max(a => a.FrameCount) : 0;
        var expectedWidth = maxFrames * cell.Width + Math.Max(0, maxFrames - 1) * gutter;
        var expectedHeight = expectedRows * cell.Height + Math.Max(0, expectedRows - 1) * gutter;

        if (sheet.Width != expectedWidth)
            issues.Add($"sheet width {sheet.Width} != expected {expectedWidth}");
        if (sheet.Height != expectedHeight)
            issues.Add($"sheet height {sheet.Height} != expected {expectedHeight}");

        var w = sheet.Width;
        var h = sheet.Height;
        var rgba = new byte[w * h * 4];
        sheet.CopyPixelDataTo(rgba);

        var stride = cell.Height + gutter;
        for (var i = 0; i < actions.Count; i++)
        {
            var y = i * stride;
            var frames = CountColumnBands(rgba, w, h, y, cell.Height);
            if (frames != actions[i].FrameCount)
            {
                issues.Add($"row {i} ({actions[i].Id}): {frames} frames, expected {actions[i].FrameCount}");
            }
        }

        if (expectedRows > 1)
        {
            for (var i = 0; i < expectedRows - 1; i++)
            {
                var gutterY = (i + 1) * cell.Height + i * gutter;
                if (RowHasAlpha(rgba, w, h, gutterY, gutter))
                    issues.Add($"gutter above row {i + 1} contains opaque pixels");
            }
        }

        return new SheetReport(issues.Count == 0, issues);
    }

    // ---- Helpers ----

    /// <summary>从源 RGBA 按 rect 裁出独立帧（CopyPixelDataTo + LoadPixelData，均为项目已验证 API）。</summary>
    private static Image<Rgba32> CropFrame(byte[] source, int sourceWidth, SliceRect rect)
    {
        var outBytes = new byte[rect.W * rect.H * 4];
        for (var y = 0; y < rect.H; y++)
        {
            var srcStart = ((rect.Y + y) * sourceWidth + rect.X) * 4;
            var dstStart = y * rect.W * 4;
            Array.Copy(source, srcStart, outBytes, dstStart, rect.W * 4);
        }
        return Image.LoadPixelData<Rgba32>(outBytes, rect.W, rect.H);
    }

    /// <summary>等比缩放到 maxW×maxH 内（保持纵横比；不放大）。手写双线性，避开 ImageSharp 3.1.11 的 Resize API 版本差异。</summary>
    private static Image<Rgba32> ResizeFit(Image<Rgba32> src, int maxW, int maxH)
    {
        var scale = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
        if (scale >= 1.0) return src.Clone();
        var tw = Math.Max(1, (int)Math.Round(src.Width * scale));
        var th = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new Image<Rgba32>(tw, th);
        for (var y = 0; y < th; y++)
        {
            var sy = (y + 0.5) * src.Height / th - 0.5;
            for (var x = 0; x < tw; x++)
            {
                var sx = (x + 0.5) * src.Width / tw - 0.5;
                dst[x, y] = BilinearSample(src, sx, sy);
            }
        }
        return dst;
    }

    private static Rgba32 BilinearSample(Image<Rgba32> src, double sx, double sy)
    {
        var x0 = (int)Math.Floor(sx);
        var y0 = (int)Math.Floor(sy);
        var x1 = x0 + 1;
        var y1 = y0 + 1;
        var fx = (float)(sx - x0);
        var fy = (float)(sy - y0);
        x0 = Math.Clamp(x0, 0, src.Width - 1); x1 = Math.Clamp(x1, 0, src.Width - 1);
        y0 = Math.Clamp(y0, 0, src.Height - 1); y1 = Math.Clamp(y1, 0, src.Height - 1);
        var p00 = src[x0, y0]; var p10 = src[x1, y0];
        var p01 = src[x0, y1]; var p11 = src[x1, y1];
        static byte L(byte a, byte b, float t) => (byte)(a + (b - a) * t);
        return new Rgba32(
            L(L(p00.R, p10.R, fx), L(p01.R, p11.R, fx), fy),
            L(L(p00.G, p10.G, fx), L(p01.G, p11.G, fx), fy),
            L(L(p00.B, p10.B, fx), L(p01.B, p11.B, fx), fy),
            L(L(p00.A, p10.A, fx), L(p01.A, p11.A, fx), fy));
    }

    /// <summary>把 src 居中画到 dst 的 (ox,oy) 槽位；越界像素裁剪，透明像素跳过。</summary>
    private static void DrawCentered(Image<Rgba32> dst, Image<Rgba32> src, int ox, int oy, int cellW, int cellH)
    {
        var dx = ox + (cellW - src.Width) / 2;
        var dy = oy + (cellH - src.Height) / 2;
        for (var sy = 0; sy < src.Height; sy++)
        {
            var ty = dy + sy;
            if (ty < 0 || ty >= dst.Height) continue;
            for (var sx = 0; sx < src.Width; sx++)
            {
                var tx = dx + sx;
                if (tx < 0 || tx >= dst.Width) continue;
                var p = src[sx, sy];
                if (p.A == 0) continue;
                dst[tx, ty] = p;
            }
        }
    }

    /// <summary>统计 y 范围内按列 alpha 检测出的帧段数（与 SpriteSlicer 列段语义一致）。</summary>
    private static int CountColumnBands(byte[] rgba, int width, int height, int yStart, int bandHeight)
    {
        var colHas = new bool[width];
        for (var x = 0; x < width; x++)
        {
            for (var y = yStart; y < yStart + bandHeight; y++)
            {
                if (y < 0 || y >= height) continue;
                if (rgba[(y * width + x) * 4 + 3] > SpriteSlicer.AlphaThreshold)
                {
                    colHas[x] = true;
                    break;
                }
            }
        }
        return Segments(colHas).Count;
    }

    private static bool RowHasAlpha(byte[] rgba, int width, int height, int yStart, int bandHeight)
    {
        for (var y = yStart; y < yStart + bandHeight; y++)
        {
            if (y < 0 || y >= height) continue;
            for (var x = 0; x < width; x++)
            {
                if (rgba[(y * width + x) * 4 + 3] > SpriteSlicer.AlphaThreshold) return true;
            }
        }
        return false;
    }

    /// <summary>布尔占用数组 → 连续段 [start, end)（与 SpriteSlicer.Segments 同算法）。</summary>
    private static List<(int Start, int End)> Segments(ReadOnlySpan<bool> occupancy)
    {
        var outList = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i < occupancy.Length; i++)
        {
            if (occupancy[i] && start < 0) start = i;
            else if (!occupancy[i] && start >= 0)
            {
                outList.Add((start, i));
                start = -1;
            }
        }
        if (start >= 0) outList.Add((start, occupancy.Length));
        return outList;
    }
}

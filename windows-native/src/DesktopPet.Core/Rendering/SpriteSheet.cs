using DesktopPet.Core.Rendering;
using DesktopPet.Core.Slicing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DesktopPet.Core.Rendering;

/// <summary>
/// 解码后的精灵图 + alpha-gutter 切片。切片结果预裁为每帧独立位图（对比 TS 版
/// 每帧从大图裁剪是净优化，迁移计划 §6.2）；掩码缓存供 alpha hitTest。
/// </summary>
public sealed class SpriteSheet
{
    public required string SourceName { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
    public required IReadOnlyList<IReadOnlyList<SpriteFrame>> Clips { get; init; }
    public required IReadOnlyList<int> ClipMaxWidths { get; init; }

    /// <summary>每行 clip 的内容顶部偏移（px，取行内最小，帧间气泡不跳）；null = 无修正（测试构造）。</summary>
    public IReadOnlyList<int>? ClipContentTops { get; init; }

    /// <summary>解码后的源图 RGBA 像素（导入预览等用途；测试构造可不填）。</summary>
    public byte[]? SourceRgba { get; init; }

    public bool IsEmpty => Clips.Count == 0;

    /// <summary>解码（ImageSharp：PNG/WebP）→ 切片 → 预裁剪帧。失败返回 null（调用方回退占位）。</summary>
    public static SpriteSheet? Decode(byte[] bytes, string sourceName)
    {
        try
        {
            using var image = Image.Load<Rgba32>(bytes);
            var width = image.Width;
            var height = image.Height;
            if (width <= 0 || height <= 0) return null;

            var rgba = new byte[width * height * 4];
            image.CopyPixelDataTo(rgba);
            var clips = SpriteSlicer.Slice(rgba, width, height);
            if (clips.Count == 0) return null;

            var frames = clips.Select(row => (IReadOnlyList<SpriteFrame>)row
                .Select(rect => CropFrame(rgba, width, rect))
                .ToList()).ToList();
            var maxWidths = frames
                .Select(row => row.Count > 0 ? row.Max(f => f.Width) : 0)
                .ToList();
            var contentTops = frames
                .Select(row => row.Count > 0 ? row.Min(f => f.ContentTop) : 0)
                .ToList();

            return new SpriteSheet
            {
                SourceName = sourceName,
                SourceWidth = width,
                SourceHeight = height,
                SourceRgba = rgba, // 保留解码像素（预览构建位图用；切片只读不修改）
                Clips = frames,
                ClipMaxWidths = maxWidths,
                ClipContentTops = contentTops,
            };
        }
        catch (Exception)
        {
            return null; // 解码失败（非图片/损坏）→ 调用方回退占位精灵
        }
    }

    private static SpriteFrame CropFrame(byte[] source, int sourceWidth, SliceRect rect)
    {
        var rgba = new byte[rect.W * rect.H * 4];
        var mask = new byte[rect.W * rect.H];
        var contentTop = rect.H; // 首个不透明行（无内容兜底 = 帧高）
        for (var y = 0; y < rect.H; y++)
        {
            var srcRow = (rect.Y + y) * sourceWidth + rect.X;
            var dstRow = y * rect.W;
            for (var x = 0; x < rect.W; x++)
            {
                var src = (srcRow + x) * 4;
                var dst = (dstRow + x) * 4;
                rgba[dst] = source[src];
                rgba[dst + 1] = source[src + 1];
                rgba[dst + 2] = source[src + 2];
                rgba[dst + 3] = source[src + 3];
                if (source[src + 3] > SpriteSlicer.AlphaThreshold)
                {
                    mask[dstRow + x] = 1;
                    if (y < contentTop) contentTop = y;
                }
            }
        }
        return new SpriteFrame(rgba, mask, rect.W, rect.H, rect.X, rect.Y, contentTop);
    }
}

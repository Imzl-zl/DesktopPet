namespace DesktopPet.Core.Slicing;

public sealed record SliceRect(int X, int Y, int W, int H);

/// <summary>
/// 1:1 移植自 windows/src/pet.ts 的 slice()（alpha-gutter 检测，与 macOS
/// SpriteSlicer.swift 同算法）：透明间隙把图切成行带，行带内再切帧；稀疏行
/// 的空格不产生帧，ragged/AI 生成的图也能正确切片。像素不可读时由调用方
/// 回退固定 8×9 网格（本类只做 alpha-gutter 切片）。
/// </summary>
public static class SpriteSlicer
{
    public const int Cols = 8;
    public const int Rows = 9;
    public const int AlphaThreshold = 16;

    /// <summary>Contiguous runs of true in an occupancy array → [start, end) pairs.</summary>
    private static List<(int Start, int End)> Segments(ReadOnlySpan<bool> occupancy)
    {
        var outList = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i < occupancy.Length; i++)
        {
            if (occupancy[i] && start < 0) start = i;
            else if (!occupancy[i] && start >= 0) { outList.Add((start, i)); start = -1; }
        }
        if (start >= 0) outList.Add((start, occupancy.Length));
        return outList;
    }

    /// <summary>输入为 RGBA 像素（同 canvas getImageData().data），输出行帧 Rect。</summary>
    public static List<List<SliceRect>> Slice(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) return [];

        var rowHas = new bool[height];
        for (var y = 0; y < height; y++)
        {
            var off = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (rgba[off + x * 4 + 3] > AlphaThreshold) { rowHas[y] = true; break; }
            }
        }

        var clips = new List<List<SliceRect>>();
        foreach (var (y0, y1) in Segments(rowHas))
        {
            var colHas = new bool[width];
            for (var y = y0; y < y1; y++)
            {
                var off = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    if (rgba[off + x * 4 + 3] > AlphaThreshold) colHas[x] = true;
                }
            }
            var clip = Segments(colHas)
                .Select(segment => new SliceRect(segment.Start, y0, segment.End - segment.Start, y1 - y0))
                .ToList();
            if (clip.Count > 0) clips.Add(clip);
        }
        return clips;
    }
}

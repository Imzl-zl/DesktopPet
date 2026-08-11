namespace DesktopPet.Core.ImageGen;

/// <summary>
/// 画廊条目（生图历史，windows-imagegen-design.md §7）：gallery/ 目录下
/// 「图片文件 + index.json 索引」的索引行。Id = 文件名主键（无扩展名）。
/// </summary>
public sealed record GalleryEntry(
    string Id,                 // 主键（文件名无扩展名部分，如 "20260811-153000-3f9a2c"）
    DateTimeOffset CreatedAt,  // 生成时间（UTC；展示层转本地）
    string ConnectionId,       // 生图连接 id（快照：连接删除后历史仍可读）
    string ModelId,            // 模型 id
    string Prompt,             // 提示词原文
    string AspectRatio,        // 显示字符串 "1:1" / "16:9" ...
    string Scale,              // "1K" / "2K" / "4K"
    string Quality,            // "auto" / "low" / "medium" / "high"
    bool Transparent,          // 是否请求透明（含绿幕管线）
    string? SeedUsed = null,   // 服务端返回的 seed（若有）
    int Width = 0,             // 实际像素宽（加载时探测）
    int Height = 0)
{
    /// <summary>落盘文件名（生图输出统一 PNG；与 Id 一一对应）。</summary>
    public string FileName => Id + ".png";
}

/// <summary>
/// 画廊索引（gallery/index.json，camelCase 序列化）。纯数据容器：
/// Normalize 负责排序（新→旧）与上限修剪；文件 IO 在 App 层（GalleryStore）。
/// </summary>
public sealed class GalleryIndex
{
    /// <summary>索引上限：超出删最旧（图片文件同时删除，由 GalleryStore 执行）。</summary>
    public const int MaxEntries = 200;

    public List<GalleryEntry> Entries { get; set; } = [];

    /// <summary>归一化：过滤非法行、按生成时间新→旧排序、裁剪到上限。</summary>
    public static GalleryIndex Normalize(GalleryIndex? raw)
    {
        if (raw is null) return new GalleryIndex();
        var entries = (raw.Entries ?? [])
            .Where(e => e is not null
                        && !string.IsNullOrWhiteSpace(e.Id))
            .OrderByDescending(e => e.CreatedAt)
            .Take(MaxEntries)
            .ToList();
        return new GalleryIndex { Entries = entries };
    }
}

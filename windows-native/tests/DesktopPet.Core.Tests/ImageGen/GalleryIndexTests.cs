using System.Text.Json;
using DesktopPet.Core.ImageGen;

namespace DesktopPet.Core.Tests.ImageGen;

/// <summary>
/// 生图历史画廊索引契约（阶段 5，windows-imagegen-design.md §7）：
/// 序列化 round-trip / Normalize 排序与上限修剪 / 文件名校验。
/// </summary>
public class GalleryIndexTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static GalleryEntry Entry(string id, int day)
        => new(
            Id: id,
            CreatedAt: new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero).AddDays(day),
            ConnectionId: "c1",
            ModelId: "gpt-image-1.5",
            Prompt: "一只橘猫",
            AspectRatio: "1:1",
            Scale: "1K",
            Quality: "auto",
            Transparent: true,
            SeedUsed: "42",
            Width: 1024,
            Height: 1024);

    [Fact]
    public void Serialize_Deserialize_Roundtrips()
    {
        var index = new GalleryIndex { Entries = [Entry("a", 1), Entry("b", 2)] };
        var json = JsonSerializer.Serialize(index, JsonOptions);
        Assert.Contains("\"entries\"", json);
        Assert.Contains("\"connectionId\":\"c1\"", json);
        Assert.Contains("\"transparent\":true", json);
        Assert.Contains("\"seedUsed\":\"42\"", json);

        var back = JsonSerializer.Deserialize<GalleryIndex>(json, JsonOptions)!;
        Assert.Equal(2, back.Entries.Count);
        Assert.Equal(index.Entries[0], back.Entries[0]);
    }

    [Fact]
    public void FileName_DerivesFromId_WithPngExtension()
    {
        Assert.Equal("20260811-153000-abc.png", Entry("20260811-153000-abc", 1).FileName);
    }

    [Fact]
    public void Normalize_OrdersNewestFirst()
    {
        var raw = new GalleryIndex { Entries = [Entry("old", 1), Entry("new", 2), Entry("mid", 1)] };
        // mid 与 old 同天 → 稳定序（按原序）；new 最新在前
        var normalized = GalleryIndex.Normalize(raw);
        Assert.Equal("new", normalized.Entries[0].Id);
        Assert.Equal(3, normalized.Entries.Count);
    }

    [Fact]
    public void Normalize_TrimsToMaxEntries_KeepingNewest()
    {
        var entries = Enumerable.Range(0, GalleryIndex.MaxEntries + 5)
            .Select(i => Entry($"id-{i:000}", i))
            .ToList();
        var normalized = GalleryIndex.Normalize(new GalleryIndex { Entries = entries });
        Assert.Equal(GalleryIndex.MaxEntries, normalized.Entries.Count);
        // 保留最新（id 最大 = CreatedAt 最晚）
        Assert.Equal($"id-{GalleryIndex.MaxEntries + 4:000}", normalized.Entries[0].Id);
        Assert.DoesNotContain(normalized.Entries, e => e.Id == "id-000");
    }

    [Fact]
    public void Normalize_DropsInvalidRows()
    {
        var raw = new GalleryIndex
        {
            Entries =
            [
                Entry("ok", 1),
                Entry("", 2),              // 空 id
                Entry("   ", 3),           // 空白 id
                null!,                      // null 行
            ],
        };
        var normalized = GalleryIndex.Normalize(raw);
        Assert.Single(normalized.Entries);
        Assert.Equal("ok", normalized.Entries[0].Id);
    }

    [Fact]
    public void Normalize_Null_ReturnsEmptyIndex()
    {
        var normalized = GalleryIndex.Normalize(null);
        Assert.NotNull(normalized);
        Assert.Empty(normalized.Entries);
    }

    [Fact]
    public void Normalize_KeepsEntryOrderWithinSameTimestamp_AndDropsNull()
    {
        // 同时间戳两条：保留两者（OrderByDescending 稳定）
        var raw = new GalleryIndex
        {
            Entries = [Entry("x", 1), Entry("y", 1)],
        };
        var normalized = GalleryIndex.Normalize(raw);
        Assert.Equal(2, normalized.Entries.Count);
        Assert.Contains(normalized.Entries, e => e.Id == "x");
        Assert.Contains(normalized.Entries, e => e.Id == "y");
    }
}

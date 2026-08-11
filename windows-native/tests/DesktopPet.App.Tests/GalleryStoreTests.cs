using System.IO;
using System.Text.Json;
using DesktopPet.Infra.Storage;
using DesktopPet.Core.ImageGen;

namespace DesktopPet.App.Tests;

/// <summary>
/// 生图历史画廊落盘（阶段 5）：PNG 文件 + index.json 索引的读写/删除/修剪/损坏容错。
/// 全部走临时目录，不碰真实 %APPDATA%。
/// </summary>
public sealed class GalleryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "DesktopPet.App.Tests", "gallery", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static GalleryEntry Entry(string id, DateTimeOffset createdAt) => new(
        Id: id,
        CreatedAt: createdAt,
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

    private static byte[] PngBytes(string marker)
        => System.Text.Encoding.UTF8.GetBytes("fake-png-" + marker); // 落盘仅按字节透传

    [Fact]
    public async Task Save_AddsEntry_WritesFileAndIndex()
    {
        var store = new GalleryStore(_directory);
        var entry = Entry("20260811-153000-abc", new DateTimeOffset(2026, 8, 11, 15, 30, 0, TimeSpan.Zero));

        await store.SaveAsync(entry, PngBytes("a"));

        Assert.True(File.Exists(Path.Combine(_directory, "20260811-153000-abc.png")));
        Assert.True(File.Exists(Path.Combine(_directory, "index.json")));
        var loaded = store.Load();
        var stored = Assert.Single(loaded.Entries);
        Assert.Equal(entry, stored);
    }

    [Fact]
    public async Task Save_NewestFirst_AndRoundtripsThroughJson()
    {
        var store = new GalleryStore(_directory);
        var older = Entry("older", new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var newer = Entry("newer", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        await store.SaveAsync(older, PngBytes("old"));
        await store.SaveAsync(newer, PngBytes("new"));

        var entries = store.Load().Entries;
        Assert.Equal(["newer", "older"], entries.Select(e => e.Id).ToArray());

        // 重新实例化（模拟重启）后索引仍完整
        var reopened = new GalleryStore(_directory).Load().Entries;
        Assert.Equal(["newer", "older"], reopened.Select(e => e.Id).ToArray());
        Assert.True(File.Exists(Path.Combine(_directory, "older.png")));
        Assert.True(File.Exists(Path.Combine(_directory, "newer.png")));
    }

    [Fact]
    public async Task Save_TrimsOldestBeyondLimit_DeletesFiles()
    {
        var store = new GalleryStore(_directory);
        for (var i = 0; i < GalleryIndex.MaxEntries + 3; i++)
        {
            await store.SaveAsync(
                Entry($"id-{i:000}", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i)),
                PngBytes(i.ToString()));
        }

        var entries = store.Load().Entries;
        Assert.Equal(GalleryIndex.MaxEntries, entries.Count);
        Assert.Equal("id-202", entries[0].Id);          // 最新保留
        Assert.DoesNotContain(entries, e => e.Id == "id-000"); // 最旧被修剪
        Assert.False(File.Exists(Path.Combine(_directory, "id-000.png")));
        Assert.True(File.Exists(Path.Combine(_directory, "id-202.png")));
    }

    [Fact]
    public async Task Delete_RemovesEntryAndFile()
    {
        var store = new GalleryStore(_directory);
        var entry = Entry("to-delete", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        var keep = Entry("keep", new DateTimeOffset(2026, 8, 11, 13, 0, 0, TimeSpan.Zero));
        await store.SaveAsync(entry, PngBytes("d"));
        await store.SaveAsync(keep, PngBytes("k"));

        await store.DeleteAsync("to-delete");

        var entries = store.Load().Entries;
        var id = Assert.Single(entries).Id;
        Assert.Equal("keep", id);
        Assert.False(File.Exists(Path.Combine(_directory, "to-delete.png")));
        Assert.True(File.Exists(Path.Combine(_directory, "keep.png")));
    }

    [Fact]
    public async Task Delete_UnknownId_IsNoOp()
    {
        var store = new GalleryStore(_directory);
        var entry = Entry("known", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        await store.SaveAsync(entry, PngBytes("k"));

        await store.DeleteAsync("unknown");

        Assert.Single(store.Load().Entries);
        Assert.True(File.Exists(Path.Combine(_directory, "known.png")));
    }

    [Fact]
    public void Load_MissingIndex_ReturnsEmpty()
    {
        var store = new GalleryStore(_directory);
        Assert.Empty(store.Load().Entries);
    }

    [Fact]
    public async Task Load_CorruptedIndex_ReturnsEmpty_KeepsImageFiles()
    {
        var store = new GalleryStore(_directory);
        var entry = Entry("orphan", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        await store.SaveAsync(entry, PngBytes("o"));
        File.WriteAllText(Path.Combine(_directory, "index.json"), "{ not json !!");

        var loaded = store.Load();
        Assert.Empty(loaded.Entries);                       // 索引损坏 → 空画廊
        Assert.True(File.Exists(Path.Combine(_directory, "orphan.png"))); // 图片文件保留

        // 损坏索引被下一次保存原子覆盖，画廊恢复
        var next = Entry("next", new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        await store.SaveAsync(next, PngBytes("n"));
        Assert.Equal(["next"], store.Load().Entries.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task FilePathFor_ReturnsPathOnlyWhenFileExists()
    {
        var store = new GalleryStore(_directory);
        var entry = Entry("present", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        await store.SaveAsync(entry, PngBytes("p"));

        Assert.NotNull(store.FilePathFor(entry));
        Assert.Null(store.FilePathFor(Entry("missing", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public async Task Index_IsAtomicAndCamelCase()
    {
        var store = new GalleryStore(_directory);
        var entry = Entry("atomic", new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        await store.SaveAsync(entry, PngBytes("a"));

        var text = File.ReadAllText(Path.Combine(_directory, "index.json"));
        var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("entries", out var entries));
        Assert.True(entries[0].TryGetProperty("connectionId", out _));
        Assert.True(entries[0].TryGetProperty("createdAt", out _));
        // 原子写不留临时文件
        Assert.Empty(Directory.GetFiles(_directory, ".*.tmp"));
    }
}

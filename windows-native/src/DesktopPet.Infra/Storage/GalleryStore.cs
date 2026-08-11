using System.Text.Json;
using DesktopPet.Core.ImageGen;
using DesktopPet.Infra.Diagnostics;

namespace DesktopPet.Infra.Storage;

/// <summary>
/// 生图历史画廊落盘（阶段 5，windows-imagegen-design.md §7）：%APPDATA%/DesktopPet/gallery/
/// 下「PNG 文件 + index.json 索引」。索引原子写（AtomicFileWriter），损坏时回退空索引
/// （图片文件保留）；超上限（GalleryIndex.MaxEntries）删最旧条目及其文件。
/// </summary>
public sealed class GalleryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _directory;
    private readonly string _indexPath;
    // 索引读-改-写串行化：多入口（生图页/设置页）并发保存互不覆盖
    private readonly SemaphoreSlim _serial = new(1, 1);

    public GalleryStore(string directory)
    {
        _directory = directory;
        _indexPath = Path.Combine(directory, "index.json");
    }

    /// <summary>读取索引（损坏/缺失 → 空索引，不抛异常）。</summary>
    public GalleryIndex Load()
        => GalleryIndex.Normalize(ReadIndex());

    /// <summary>保存一张生成图：写 PNG + 更新索引（新→旧） + 超限修剪。</summary>
    public async Task SaveAsync(GalleryEntry entry, byte[] imageBytes, CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = Load();
            index.Entries.RemoveAll(e => e.Id == entry.Id); // 同 id 覆盖（理论上不会发生）
            index.Entries.Insert(0, entry);

            // 超限修剪：索引先落（新状态），再删最旧图片文件（孤儿文件比缺失索引安全）
            var removed = new List<GalleryEntry>();
            while (index.Entries.Count > GalleryIndex.MaxEntries)
            {
                var last = index.Entries[^1];
                index.Entries.RemoveAt(index.Entries.Count - 1);
                removed.Add(last);
            }

            AtomicFileWriter.WriteAllBytes(Path.Combine(_directory, entry.FileName), imageBytes);
            WriteIndex(index);
            foreach (var old in removed)
            {
                TryDeleteFile(old.FileName);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>删除一条：索引先更新，再删图片文件。</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = Load();
            var entry = index.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;
            index.Entries.Remove(entry);
            WriteIndex(index);
            TryDeleteFile(entry.FileName);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>图片文件完整路径（不存在时返回 null）。</summary>
    public string? FilePathFor(GalleryEntry entry)
    {
        var path = Path.Combine(_directory, entry.FileName);
        return File.Exists(path) ? path : null;
    }

    private GalleryIndex? ReadIndex()
    {
        if (!File.Exists(_indexPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<GalleryIndex>(File.ReadAllText(_indexPath), JsonOptions);
        }
        catch (JsonException)
        {
            return null; // 索引损坏 = 空画廊（图片文件保留）
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteIndex(GalleryIndex index)
        => AtomicFileWriter.WriteAllText(
            _indexPath,
            JsonSerializer.Serialize(index, JsonOptions));

    private void TryDeleteFile(string fileName)
    {
        try
        {
            File.Delete(Path.Combine(_directory, fileName));
        }
        catch (IOException)
        {
            // 文件被占用等：孤儿文件可接受（索引已更新，不再展示）
        }
    }
}

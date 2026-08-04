using System.IO;
using System.Net.Http;
using System.Text.Json;

using DesktopPet.Core.Rendering;

namespace DesktopPet.App.Rendering;

/// <summary>
/// 精灵图加载：本地缓存 %APPDATA%/DesktopPet/sprites/{slug}.png 优先；缺失时从
/// CDN 目录（pets.thenightwatcher.online，同 windows/src/catalog.ts）下载并缓存。
/// 全部失败返回 null（调用方回退占位精灵）。异步执行，不阻塞 UI 线程。
/// </summary>
public sealed class SpriteLoader
{
    private const string ManifestUrl = "https://pets.thenightwatcher.online/manifest.json";

    private readonly string _spritesDir;
    private readonly string _manifestPath;
    private readonly HttpClient _http;

    private readonly Dictionary<string, SpriteSheet> _sheetCache = new();

    public SpriteLoader(string dataDirectory)
    {
        _spritesDir = Path.Combine(dataDirectory, "sprites");
        _manifestPath = Path.Combine(dataDirectory, "catalog.json");
        Directory.CreateDirectory(_spritesDir);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // CDN 拒绝无 UA 请求（curl/浏览器 UA 可过）
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopPet/0.1 (Windows; .NET 8)");
    }

    public string SpritesDirectory => _spritesDir;

    /// <summary>同步加载已缓存的精灵（浮球等 UI 线程路径），无缓存返回 null。</summary>
    public SpriteSheet? TryGetCached(string slug)
        => _sheetCache.TryGetValue(slug, out var sheet) ? sheet : null;

    /// <summary>导入的本地精灵写入缓存目录（slug 为实例 id）。</summary>
    public void SaveLocal(string slug, byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(_spritesDir, $"{slug}.png"), bytes);
    }

    public async Task<SpriteSheet?> LoadAsync(string slug, CancellationToken ct = default)
    {
        // 共享缓存：同 slug 只解码一次（多窗口/浮球共用，内存关键）
        if (_sheetCache.TryGetValue(slug, out var cached)) return cached;

        var localPath = Path.Combine(_spritesDir, $"{slug}.png");
        if (File.Exists(localPath))
        {
            var sheet = SpriteSheet.Decode(await File.ReadAllBytesAsync(localPath, ct), slug);
            if (sheet is not null) _sheetCache[slug] = sheet;
            return sheet;
        }

        try
        {
            var url = await ResolveSheetUrlAsync(slug, ct);
            if (url is null)
            {
                Log($"no spritesheet URL for slug '{slug}' in catalog");
                return null;
            }
            var bytes = await _http.GetByteArrayAsync(url, ct);
            await File.WriteAllBytesAsync(localPath, bytes, ct);
            var sheet = SpriteSheet.Decode(bytes, slug);
            if (sheet is not null) _sheetCache[slug] = sheet;
            return sheet;
        }
        catch (Exception ex)
        {
            Log($"sprite download failed for {slug}: {ex.GetType().Name}: {ex.Message}");
            return null; // 网络失败 → 占位回退
        }
    }

    /// <summary>从目录 manifest 解析 slug 的 spritesheet URL（manifest 本地缓存）。</summary>
    private async Task<string?> ResolveSheetUrlAsync(string slug, CancellationToken ct)
    {
        var manifest = await LoadManifestAsync(ct);
        using var doc = JsonDocument.Parse(manifest);
        if (doc.RootElement.TryGetProperty("pets", out var pets))
        {
            foreach (var pet in pets.EnumerateArray())
            {
                if (pet.TryGetProperty("slug", out var s) && s.GetString() == slug &&
                    pet.TryGetProperty("spritesheetUrl", out var url))
                {
                    return url.GetString();
                }
            }
        }
        return null;
    }

    private async Task<string> LoadManifestAsync(CancellationToken ct)
    {
        if (File.Exists(_manifestPath))
        {
            return await File.ReadAllTextAsync(_manifestPath, ct);
        }
        try
        {
            var json = await _http.GetStringAsync(ManifestUrl, ct);
            await File.WriteAllTextAsync(_manifestPath, json, ct);
            return json;
        }
        catch (Exception ex)
        {
            Log($"catalog download failed: {ex.GetType().Name}: {ex.Message}");
            return "{\"pets\":[]}"; // 离线：目录为空，实例回退占位
        }
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "desktoppet-sprite.log");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}

using System.IO;
using System.Net.Http;
using System.Text.Json;

using DesktopPet.Core.Rendering;
using DesktopPet.Infra.Diagnostics;
using DesktopPet.Infra.Providers;

namespace DesktopPet.App.Rendering;

/// <summary>
/// 精灵图加载：本地缓存 %APPDATA%/DesktopPet/sprites/{slug}.png 优先；缺失时从
/// CDN 目录（pets.thenightwatcher.online，同 windows/src/catalog.ts）下载并缓存。
/// 全部失败返回 null（调用方回退占位精灵）。异步执行，不阻塞 UI 线程。
/// </summary>
public sealed class SpriteLoader : IDisposable
{
    private const string ManifestUrl = "https://pets.thenightwatcher.online/manifest.json";
    public const long DefaultDecodedCacheBytes = 32L * 1024 * 1024;

    private readonly string _spritesDir;
    private readonly string _manifestPath;
    private readonly HttpClient _http;
    private readonly SpriteSheetCache _sheetCache;
    private readonly IAppLogger _logger;
    private bool _disposed;

    public SpriteLoader(
        string dataDirectory,
        long maxDecodedCacheBytes = DefaultDecodedCacheBytes,
        IAppLogger? logger = null)
    {
        _spritesDir = Path.Combine(dataDirectory, "sprites");
        _manifestPath = Path.Combine(dataDirectory, "catalog.json");
        Directory.CreateDirectory(_spritesDir);
        _sheetCache = new SpriteSheetCache(maxDecodedCacheBytes);
        _logger = logger ?? NullAppLogger.Instance;
        _http = ProviderHttpClient.Create();
        _http.Timeout = TimeSpan.FromSeconds(20);
        // CDN 拒绝无 UA 请求（curl/浏览器 UA 可过）
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopPet/0.1 (Windows; .NET 8)");
    }

    public string SpritesDirectory => _spritesDir;

    /// <summary>同步读取已解码的精灵；不会触发磁盘或网络 I/O。</summary>
    public SpriteSheet? TryGetCached(string slug) => _sheetCache.Get(slug);

    /// <summary>导入的本地精灵写入缓存目录（slug 为实例 id）。</summary>
    public void SaveLocal(string slug, byte[] bytes)
    {
        AtomicFileWriter.WriteAllBytes(LocalPath(slug), bytes);
        _sheetCache.Remove(slug);
    }

    public void DeleteLocal(string slug)
    {
        File.Delete(LocalPath(slug));
        _sheetCache.Remove(slug);
    }

    /// <summary>
    /// 加载已落盘的精灵，但绝不触发网络请求。需要离线检查或避免主动下载的调用方
    /// 使用此入口；常规界面通过 <see cref="LoadAsync"/> 在线补全缺失资源。
    /// </summary>
    public async Task<SpriteSheet?> LoadLocalAsync(string slug, CancellationToken ct = default)
    {
        var cached = TryGetCached(slug);
        if (cached is not null) return cached;

        var localPath = LocalPath(slug);
        if (!File.Exists(localPath)) return null;

        var sheet = SpriteSheet.Decode(await File.ReadAllBytesAsync(localPath, ct), slug);
        if (sheet is not null) Cache(sheet, slug);
        return sheet;
    }

    public async Task<SpriteSheet?> LoadAsync(string slug, CancellationToken ct = default)
    {
        var cached = TryGetCached(slug);
        if (cached is not null) return cached;

        var local = await LoadLocalAsync(slug, ct);
        if (local is not null) return local;

        try
        {
            var url = await ResolveSheetUrlAsync(slug, ct);
            if (url is null)
            {
                Log($"no spritesheet URL for slug '{slug}' in catalog");
                return null;
            }
            var bytes = await _http.GetByteArrayAsync(url, ct);
            await Task.Run(() => AtomicFileWriter.WriteAllBytes(LocalPath(slug), bytes), ct);
            var sheet = SpriteSheet.Decode(bytes, slug);
            if (sheet is not null) Cache(sheet, slug);
            return sheet;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"sprite download failed for {slug}: {ex.GetType().Name}: {ex.Message}");
            return null; // 网络失败 → 占位回退
        }
    }

    private void Cache(SpriteSheet sheet, string slug) => _sheetCache.Put(slug, sheet);

    public void Evict(string slug) => _sheetCache.Remove(slug);

    public long CachedBytes => _sheetCache.CurrentBytes;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sheetCache.Clear();
        _http.Dispose();
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
            await Task.Run(() => AtomicFileWriter.WriteAllText(_manifestPath, json), ct);
            return json;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"catalog download failed: {ex.GetType().Name}: {ex.Message}");
            return "{\"pets\":[]}"; // 离线：目录为空，实例回退占位
        }
    }

    private string LocalPath(string slug) => Path.Combine(_spritesDir, $"{slug}.png");

    private void Log(string message) => _logger.Info("SpriteLoader", message);
}

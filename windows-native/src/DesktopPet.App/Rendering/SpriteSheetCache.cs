using DesktopPet.Core.Rendering;

namespace DesktopPet.App.Rendering;

/// <summary>Thread-safe byte-budget LRU for decoded sprite sheets.</summary>
internal sealed class SpriteSheetCache
{
    private sealed record Entry(string Slug, SpriteSheet Sheet, long Bytes);

    private readonly object _lock = new();
    private readonly long _maxBytes;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = new();
    private long _currentBytes;

    public SpriteSheetCache(long maxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public long CurrentBytes
    {
        get { lock (_lock) return _currentBytes; }
    }

    public SpriteSheet? Get(string slug)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(slug, out var node)) return null;
            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value.Sheet;
        }
    }

    public void Put(string slug, SpriteSheet sheet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(sheet);
        var bytes = EstimateBytes(sheet);

        lock (_lock)
        {
            Remove_NoLock(slug);
            if (bytes > _maxBytes) return;

            var node = _recency.AddFirst(new Entry(slug, sheet, bytes));
            _entries[slug] = node;
            _currentBytes += bytes;
            while (_currentBytes > _maxBytes && _recency.Last is { } oldest)
                Remove_NoLock(oldest.Value.Slug);
        }
    }

    public bool Remove(string slug)
    {
        lock (_lock) return Remove_NoLock(slug);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _recency.Clear();
            _currentBytes = 0;
        }
    }

    internal static long EstimateBytes(SpriteSheet sheet)
    {
        long bytes = sheet.SourceRgba?.LongLength ?? 0;
        foreach (var clip in sheet.Clips)
        foreach (var frame in clip)
            bytes += frame.Rgba.LongLength + frame.Mask.LongLength;
        return bytes;
    }

    private bool Remove_NoLock(string slug)
    {
        if (!_entries.Remove(slug, out var node)) return false;
        _recency.Remove(node);
        _currentBytes -= node.Value.Bytes;
        return true;
    }
}

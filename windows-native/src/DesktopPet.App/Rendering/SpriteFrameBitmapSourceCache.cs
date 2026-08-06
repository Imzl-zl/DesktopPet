using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.Core.Rendering;

namespace DesktopPet.App.Rendering;

/// <summary>
/// Keeps immutable WPF image sources for decoded sprite frames. The source RGBA data
/// is converted once when a frame enters the cache rather than on every animation tick.
/// </summary>
internal sealed class SpriteFrameBitmapSourceCache
{
    private const int MaxCachedBytes = 8 * 1024 * 1024;

    private readonly Dictionary<SpriteFrame, Entry> _entries = [];
    private readonly LinkedList<SpriteFrame> _recency = [];
    private int _cachedBytes;

    public BitmapSource GetOrCreate(SpriteFrame frame)
    {
        if (_entries.TryGetValue(frame, out var entry))
        {
            Touch(entry);
            return entry.Source;
        }

        var pixels = frame.Rgba.ToArray();
        PixelBuffer.RgbaToBgra(pixels);
        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            frame.Width * 4);
        source.Freeze();

        var byteCount = pixels.Length;
        if (byteCount <= MaxCachedBytes)
        {
            EvictUntilFits(byteCount);
            var node = _recency.AddFirst(frame);
            _entries.Add(frame, new Entry(source, byteCount, node));
            _cachedBytes += byteCount;
        }

        return source;
    }

    public void Clear()
    {
        _entries.Clear();
        _recency.Clear();
        _cachedBytes = 0;
    }

    private void Touch(Entry entry)
    {
        _recency.Remove(entry.Node);
        _recency.AddFirst(entry.Node);
    }

    private void EvictUntilFits(int incomingBytes)
    {
        while (_cachedBytes + incomingBytes > MaxCachedBytes && _recency.Last is { } node)
        {
            var frame = node.Value;
            var entry = _entries[frame];
            _cachedBytes -= entry.ByteCount;
            _entries.Remove(frame);
            _recency.Remove(node);
        }
    }

    private sealed record Entry(BitmapSource Source, int ByteCount, LinkedListNode<SpriteFrame> Node);
}

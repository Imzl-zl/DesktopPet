namespace DesktopPet.App.Rendering;

/// <summary>Reusable RGBA frame storage for WriteableBitmap rendering.</summary>
internal sealed class ReusablePixelBuffer
{
    private readonly byte[] _pixels;

    public ReusablePixelBuffer(int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        _pixels = new byte[length];
    }

    public byte[] Clear()
    {
        Array.Clear(_pixels);
        return _pixels;
    }
}

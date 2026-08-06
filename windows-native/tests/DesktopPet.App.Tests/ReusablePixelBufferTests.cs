using DesktopPet.App.Rendering;

namespace DesktopPet.App.Tests;

public sealed class ReusablePixelBufferTests
{
    [Fact]
    public void Clear_ReturnsTheSameZeroedBuffer()
    {
        var storage = new ReusablePixelBuffer(16);
        var buffer = storage.Clear();
        buffer[0] = 0xFF;
        buffer[^1] = 0x7F;

        var cleared = storage.Clear();

        Assert.Same(buffer, cleared);
        Assert.All(cleared, value => Assert.Equal((byte)0, value));
    }
}

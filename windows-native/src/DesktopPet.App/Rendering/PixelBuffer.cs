namespace DesktopPet.App.Rendering;

/// <summary>
/// WPF WriteableBitmap 默认 PixelFormats.Bgra32（内存序 B,G,R,A），而 Core
/// 渲染管线输出 RGBA（对齐 pet.ts canvas getImageData 语义）。写位图前必须
/// 就地交换 R/B，否则颜色错位（曾导致自定义精灵渲染成蓝色脸）。
/// </summary>
public static class PixelBuffer
{
    public static void RgbaToBgra(byte[] buffer)
    {
        for (var i = 0; i + 3 < buffer.Length; i += 4)
        {
            (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
        }
    }
}

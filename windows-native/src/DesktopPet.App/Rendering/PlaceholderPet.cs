namespace DesktopPet.App.Rendering;

/// <summary>
/// Phase 0 占位精灵：程序生成的像素猫（2 帧眨眼循环），用于验证
/// WriteableBitmap 直写渲染管线、alpha hitTest 与帧率自适应。
/// Phase 1 由 SpriteSlicer + 真实精灵图替换。
/// </summary>
public static class PlaceholderPet
{
    public const int FrameWidth = 32;
    public const int FrameHeight = 36;

    /// <summary>每帧 RGBA 像素 + 同尺寸 alpha 掩码（1 = 不透明，hitTest 用）。</summary>
    public sealed record Frame(byte[] Rgba, byte[] Mask);

    private static readonly byte[] Body = [0x5B, 0x8D, 0xE0];   // 蓝灰身体
    private static readonly byte[] Belly = [0xF2, 0xF6, 0xFC];  // 浅肚皮
    private static readonly byte[] Ears = [0x4A, 0x74, 0xC4];
    private static readonly byte[] Eyes = [0x1B, 0x1F, 0x26];
    private static readonly byte[] Nose = [0xE5, 0x54, 0x4B];
    private static readonly byte[] Stripes = [0x45, 0x6B, 0xB5];

    public static IReadOnlyList<Frame> Frames { get; } = [BuildFrame(eyesOpen: true), BuildFrame(eyesOpen: false)];

    private static Frame BuildFrame(bool eyesOpen)
    {
        var rgba = new byte[FrameWidth * FrameHeight * 4];
        var mask = new byte[FrameWidth * FrameHeight];

        void Pixel(int x, int y, byte[] rgb, byte alpha = 255)
        {
            if (x < 0 || x >= FrameWidth || y < 0 || y >= FrameHeight) return;
            var off = (y * FrameWidth + x) * 4;
            rgba[off] = rgb[0];
            rgba[off + 1] = rgb[1];
            rgba[off + 2] = rgb[2];
            rgba[off + 3] = alpha;
            if (alpha > 16) mask[y * FrameWidth + x] = 1;
        }

        void Rect(int x0, int y0, int w, int h, byte[] rgb, byte alpha = 255)
        {
            for (var y = y0; y < y0 + h; y++)
                for (var x = x0; x < x0 + w; x++)
                    Pixel(x, y, rgb, alpha);
        }

        // 尾巴
        Rect(3, 22, 4, 3, Stripes);
        Rect(2, 19, 3, 4, Stripes);
        // 耳朵（左、右）
        Rect(5, 2, 5, 5, Ears);
        Rect(9, 1, 5, 4, Ears);
        Rect(22, 2, 5, 5, Ears);
        Rect(18, 1, 5, 4, Ears);
        // 头（含两耳之间）
        Rect(5, 4, 22, 12, Body);
        // 身体
        Rect(7, 16, 18, 14, Body);
        // 肚皮
        Rect(11, 19, 10, 10, Belly);
        // 条纹
        Rect(7, 17, 4, 2, Stripes);
        Rect(21, 17, 4, 2, Stripes);
        Rect(7, 26, 4, 2, Stripes);
        Rect(21, 26, 4, 2, Stripes);
        // 眼睛
        if (eyesOpen)
        {
            Pixel(11, 9, Eyes);
            Pixel(12, 9, Eyes);
            Pixel(20, 9, Eyes);
            Pixel(21, 9, Eyes);
        }
        else
        {
            Pixel(11, 10, Eyes);
            Pixel(12, 10, Eyes);
            Pixel(20, 10, Eyes);
            Pixel(21, 10, Eyes);
        }
        // 鼻子
        Pixel(15, 12, Nose);
        Pixel(16, 12, Nose);

        return new Frame(rgba, mask);
    }
}

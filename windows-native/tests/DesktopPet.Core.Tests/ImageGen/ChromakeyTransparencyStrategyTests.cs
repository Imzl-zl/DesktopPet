using DesktopPet.Core.ImageGen;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DesktopPet.Core.Tests.ImageGen;

/// <summary>绿幕透明策略（windows-imagegen-design.md §4.3）：prompt 增强 + HSV 键控。</summary>
public class ChromakeyTransparencyStrategyTests
{
    private static byte[] EncodePng(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static Image<Rgba32> CreateTestImage(
        int width, int height,
        Func<int, int, Rgba32> pixelAt)
    {
        var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                image[x, y] = pixelAt(x, y);
        return image;
    }

    private static Rgba32 Rgb(byte r, byte g, byte b) => new(r, g, b, 255);

    [Fact]
    public void RequiresPromptEnhancement_True()
        => Assert.True(new ChromakeyTransparencyStrategy().RequiresPromptEnhancement);

    [Fact]
    public void EnhancePrompt_WrapsChromakeySpec()
    {
        var strategy = new ChromakeyTransparencyStrategy();
        var prompt = strategy.EnhancePrompt("a cute cat");
        Assert.Contains("a cute cat", prompt);
        Assert.Contains("#00FF00", prompt);
        Assert.Contains("chromakey", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("white outline", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostProcess_PureGreenBackground_BecomesFullyTransparent()
    {
        var strategy = new ChromakeyTransparencyStrategy();
        var input = CreateTestImage(64, 64, (_, _) => Rgb(0, 255, 0));
        var output = await strategy.PostProcessAsync(
            new ImageGenOutput(EncodePng(input), "image/png"), CancellationToken.None);

        using var result = Image.Load<Rgba32>(output.Bytes);
        Assert.Equal(64, result.Width);
        Assert.Equal(64, result.Height);
        Assert.Equal("image/png", output.MimeType);
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                Assert.Equal(0, result[x, y].A); // 纯绿背景全部抠掉
    }

    [Fact]
    public async Task PostProcess_RedSubjectOnGreen_SubjectPreserved()
    {
        var strategy = new ChromakeyTransparencyStrategy();
        var input = CreateTestImage(100, 100, (x, y) =>
            x is >= 30 and < 70 && y is >= 30 and < 70 ? Rgb(255, 0, 0) : Rgb(0, 255, 0));
        var output = await strategy.PostProcessAsync(
            new ImageGenOutput(EncodePng(input), "image/png"), CancellationToken.None);

        using var result = Image.Load<Rgba32>(output.Bytes);
        // 主体中心保留不透明
        Assert.Equal(255, result[50, 50].A);
        Assert.Equal(255, result[50, 50].R);
        // 四角背景抠掉
        Assert.Equal(0, result[5, 5].A);
        Assert.Equal(0, result[95, 5].A);
        Assert.Equal(0, result[5, 95].A);
        Assert.Equal(0, result[95, 95].A);
    }

    [Fact]
    public async Task PostProcess_GreenSubjectLowSaturation_Preserved()
    {
        // 主体内低饱和绿（如橄榄绿）不应被误抠：饱和度低于阈值时保留
        var strategy = new ChromakeyTransparencyStrategy();
        var input = CreateTestImage(50, 50, (x, y) =>
            x is >= 20 and < 30 && y is >= 20 and < 30 ? Rgb(90, 110, 60) : Rgb(0, 255, 0));
        var output = await strategy.PostProcessAsync(
            new ImageGenOutput(EncodePng(input), "image/png"), CancellationToken.None);

        using var result = Image.Load<Rgba32>(output.Bytes);
        Assert.True(result[25, 25].A > 0, "低饱和橄榄绿不应被误抠");
    }

    [Fact]
    public async Task PostProcess_EdgePixelAdjacentToKeyed_HalvedAlpha()
    {
        // 主体与背景交界像素 alpha 应被削半（去绿边 halo）
        var strategy = new ChromakeyTransparencyStrategy();
        var input = CreateTestImage(40, 40, (x, y) =>
            x < 20 ? Rgb(255, 0, 0) : Rgb(0, 255, 0));
        var output = await strategy.PostProcessAsync(
            new ImageGenOutput(EncodePng(input), "image/png"), CancellationToken.None);

        using var result = Image.Load<Rgba32>(output.Bytes);
        // 边界列 (x=19) 与被抠像素相邻 → alpha < 255
        Assert.True(result[19, 20].A < 255, "边界像素 alpha 应被削半");
        // 主体内部 (x=10) 不受影响
        Assert.Equal(255, result[10, 20].A);
    }

    [Fact]
    public async Task PostProcess_NonPngInput_JpegDecodesAndOutputsPng()
    {
        var strategy = new ChromakeyTransparencyStrategy();
        var input = CreateTestImage(32, 32, (_, _) => Rgb(0, 255, 0));
        using var ms = new MemoryStream();
        input.SaveAsJpeg(ms);
        var output = await strategy.PostProcessAsync(
            new ImageGenOutput(ms.ToArray(), "image/jpeg"), CancellationToken.None);

        Assert.Equal("image/png", output.MimeType);
        using var result = Image.Load<Rgba32>(output.Bytes);
        Assert.Equal(0, result[16, 16].A);
    }
}

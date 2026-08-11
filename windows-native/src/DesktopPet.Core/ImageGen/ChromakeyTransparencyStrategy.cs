using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DesktopPet.Core.ImageGen;

/// <summary>
/// 绿幕透明策略（windows-imagegen-design.md §4.3）：两段式——
/// 请求前把 prompt 包装成 chromakey 规范（纯绿 #00FF00 + 白描边 + 主体无绿 + 居中留白），
/// 响应后用 HSV 键控去除背景并清理边缘，输出 RGBA PNG。
/// 适用于无原生透明的模型（gpt-image-2 / Grok / Nano Banana 全系 / Qwen / FLUX）。
/// 方案来源：社区 chromakey 贴纸管线（philschmid），本实现为纯本地计算，无网络依赖。
/// </summary>
public sealed class ChromakeyTransparencyStrategy : ITransparencyStrategy
{
    public const string ChromakeyHex = "#00FF00";

    // HSV 键控阈值（调参见测试）：纯绿 hue=120°，±25 容差；饱和度/亮度下限过滤主体内低饱和绿。
    private const float HueCenter = 120f;
    private const float HueToleranceCore = 25f;
    private const float HueToleranceEdge = 40f;
    private const float MinSaturation = 0.25f;
    private const float MinSaturationEdge = 0.18f;
    private const float MinValue = 0.25f;

    public bool RequiresPromptEnhancement => true;

    public string EnhancePrompt(string prompt)
    {
        var trimmed = prompt.Trim();
        return $"""
            {trimmed}

            CRITICAL CHROMAKEY REQUIREMENTS:
            1. BACKGROUND: Solid, flat, uniform chromakey green color. Use EXACTLY hex color {ChromakeyHex} (RGB 0, 255, 0).
               The entire background must be this single pure green color with NO variation, NO gradients, NO shadows, NO lighting effects.
            2. WHITE OUTLINE: The subject MUST have a clean white outline/border (2-3 pixels wide) separating it from the green background.
               This white border prevents color bleeding between the subject and background.
            3. NO GREEN ON SUBJECT: The subject itself should NOT contain green colors to avoid confusion with the chromakey.
               If the subject needs green (like leaves), use a distinctly different shade like dark forest green or teal.
            4. SHARP EDGES: The subject should have crisp, sharp, well-defined edges - no soft or blurry boundaries.
            5. CENTERED: Subject should be centered with padding around all sides.
            6. STYLE: Vibrant, clean, sticker-style illustration with bold colors.
            This is for chromakey extraction - the green background will be removed programmatically.
            """;
    }

    /// <summary>HSV 键控：绿色背景像素 alpha 置 0；边缘色晕降 alpha；输出 RGBA PNG。</summary>
    public Task<ImageGenOutput> PostProcessAsync(ImageGenOutput output, CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(output.Bytes);
        ApplyChromaKey(image, ct);

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return Task.FromResult(new ImageGenOutput(ms.ToArray(), "image/png", output.SeedUsed));
    }

    private static void ApplyChromaKey(Image<Rgba32> image, CancellationToken ct)
    {
        var width = image.Width;
        var height = image.Height;
        var alpha = new byte[width * height]; // 0 = 抠掉, 255 = 保留
        var isKeyed = new bool[width * height];

        // Pass 1：核心键控 + 色晕区标记
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var pixel = image[x, y];
                var (hue, sat, val) = RgbToHsv(pixel.R, pixel.G, pixel.B);
                var idx = y * width + x;
                var dist = HueDistance(hue, HueCenter);
                if (dist <= HueToleranceCore && sat >= MinSaturation && val >= MinValue)
                {
                    alpha[idx] = 0;
                    isKeyed[idx] = true;
                }
                else if (dist <= HueToleranceEdge && sat >= MinSaturationEdge && val >= MinValue)
                {
                    // 边缘色晕（抗锯齿绿边）：半透明
                    alpha[idx] = 40;
                    isKeyed[idx] = true;
                }
                else
                {
                    alpha[idx] = pixel.A;
                }
            }
        }

        // Pass 2：主体边缘去晕——与被抠像素相邻的保留像素，alpha 削半（消除绿边 halo）
        var final = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var idx = y * width + x;
                if (isKeyed[idx] || alpha[idx] == 0)
                {
                    final[idx] = alpha[idx];
                    continue;
                }
                if (HasKeyedNeighbor(image, isKeyed, width, height, x, y))
                    final[idx] = (byte)(alpha[idx] / 2);
                else
                    final[idx] = alpha[idx];
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = y * width + x;
                var p = image[x, y];
                p.A = final[idx];
                image[x, y] = p;
            }
        }
    }

    private static bool HasKeyedNeighbor(Image<Rgba32> image, bool[] isKeyed, int width, int height, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            var ny = y + dy;
            if (ny < 0 || ny >= height) continue;
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx;
                if (nx < 0 || nx >= width) continue;
                if (isKeyed[ny * width + nx]) return true;
            }
        }
        return false;
    }

    /// <summary>RGB → HSV（hue 0..360；sat/val 0..1）。</summary>
    private static (float Hue, float Sat, float Val) RgbToHsv(byte r, byte g, byte b)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        var h = 0f;
        if (delta > 0f)
        {
            if (max == rf) h = 60f * (((gf - bf) / delta) % 6f);
            else if (max == gf) h = 60f * (((bf - rf) / delta) + 2f);
            else h = 60f * (((rf - gf) / delta) + 4f);
            if (h < 0f) h += 360f;
        }
        var s = max == 0f ? 0f : delta / max;
        return (h, s, max);
    }

    private static float HueDistance(float a, float b)
    {
        var d = Math.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}

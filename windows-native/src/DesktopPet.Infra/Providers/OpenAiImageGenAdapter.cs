using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// OpenAI 兼容族生图适配器（windows-imagegen-design.md §5.2）：
/// POST {baseUrl}/images/generations（+ /images/edits），通吃 gpt-image 全系 /
/// Grok Imagine / Qwen-Image / FLUX / Kolors 及用户自配 OpenAI 兼容中转端点。
/// 尺寸统一「宽高比 + 档位」，在此换算像素（短边=档位、按 ratio、对齐 16 倍数、长边 ≤3840）。
/// </summary>
public sealed class OpenAiImageGenAdapter : HttpImageAdapterBase
{
    public const string OpenAiFamily = "openai";

    private const int MaxEdgePx = 3840;
    private const long MaxTotalPixels = 8_294_400; // gpt-image-2 上限
    private const int ShortSideS1K = 1024;
    private const int ShortSideS2K = 2048;
    private const int LongSideS4K = 3840;

    private readonly string _modelId;

    public OpenAiImageGenAdapter(
        ImageConnection connection,
        string modelId,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? requestTimeout = null,
        bool strictParams = false)
        : base(connection, credentials, httpClient, requestTimeout, strictParams)
    {
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
    }

    public override string Family => OpenAiFamily;

    protected override bool HasReducibleParameters(ImageGenSpec spec)
        => spec.Quality != ImageQuality.Auto || spec.Transparent || spec.Seed is not null;

    protected override JsonObject BuildGenerateBody(ImageGenSpec spec, bool reduced)
    {
        var body = new JsonObject
        {
            ["model"] = _modelId,
            ["prompt"] = spec.Prompt,
            ["size"] = ResolveSize(spec.AspectRatio, spec.Scale),
            ["n"] = 1,
            // 不发送 response_format：部分端点直接拒绝顶层该参数（需放 extra_body），
            // 本适配器对 b64_json/url 两种响应均已兼容（见 ParseResponseAsync），不发送最稳。
        };

        // quality：OpenAI 系支持 low/medium/high；中转不认识时由基类降级重试去掉
        if (!reduced && spec.Quality != ImageQuality.Auto)
            body["quality"] = QualityName(spec.Quality);

        // 透明：仅模型能力允许时直传（gpt-image-1 系列）；gpt-image-2 等由门面走绿幕策略
        if (!reduced && spec.Transparent)
            body["background"] = "transparent";

        if (spec.Seed is { } seed)
            body["seed"] = seed;

        if (spec.ExtraParams is not null)
            foreach (var (key, value) in spec.ExtraParams)
                body[key] = JsonSerializer.SerializeToNode(value);

        return body;
    }

    protected override async Task<byte[]> ParseResponseAsync(JsonDocument response, CancellationToken ct)
    {
        var root = response.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            throw new ProviderException("invalid-response", "生图响应无数据");
        var first = data[0];
        if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            return Convert.FromBase64String(b64.GetString()!);
        if (first.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            return await DownloadAsync(urlEl.GetString()!, ct);
        throw new ProviderException("invalid-response", "生图响应缺少 b64_json/url");
    }

    /// <summary>编辑 body：Grok 用单对象（官方 JSON 形态），其余用通用数组（中转主流）。</summary>
    protected override JsonNode? BuildEditImages(IReadOnlyList<ReferenceImage> references)
    {
        if (references.Count == 0)
            throw new ProviderException("invalid-request", "编辑至少需要一张参考图");
        return _modelId.StartsWith("grok-", StringComparison.OrdinalIgnoreCase)
            ? BuildEditImagesObject(references)
            : BuildEditImagesArray(references);
    }

    /// <summary>宽高比 + 档位 → 像素尺寸（gpt-image-2 约束：16 倍数、长边 ≤3840、总像素 ≤8.29M）。</summary>
    public static string ResolveSize(ImageAspectRatio ratio, ImageScale scale)
    {
        var (w, h) = ResolveSizePx(ratio, scale);
        return $"{w}x{h}";
    }

    public static (int Width, int Height) ResolveSizePx(ImageAspectRatio ratio, ImageScale scale)
    {
        var ratioF = AspectRatioFactor(ratio);
        var maxRatio = Math.Max(ratioF, 1f / ratioF); // 长边/短边，≥1
        var minRatio = Math.Min(ratioF, 1f / ratioF); // 短边/长边，≤1

        // 档位语义：1K/2K = 短边像素；4K = 长边 3840（官方 4K landscape 3840x2160 同源）
        var (minSide, maxSide) = scale switch
        {
            ImageScale.S1K => (ShortSideS1K, Round16(ShortSideS1K * maxRatio)),
            ImageScale.S2K => (ShortSideS2K, Round16(ShortSideS2K * maxRatio)),
            _ => (Round16(LongSideS4K * minRatio), LongSideS4K),
        };

        if (maxSide > MaxEdgePx)
        {
            maxSide = MaxEdgePx;
            minSide = Round16(MaxEdgePx * minRatio);
        }

        // 总像素上限兜底（如 1:1 的 4K：3840² 超限 → 缩到 ≤8.29M 的最大 16 倍数正方形）
        if ((long)minSide * maxSide > MaxTotalPixels)
        {
            var s = (float)Math.Sqrt((double)MaxTotalPixels / (maxRatio * minRatio));
            minSide = Round16(s * minRatio);
            maxSide = Round16(s * maxRatio);
        }

        // 兜底后再 clamp：像素收缩可能把长边重新推超 3840（如 3:2 的 4K）
        if (maxSide > MaxEdgePx)
        {
            maxSide = MaxEdgePx;
            minSide = Round16(MaxEdgePx * minRatio);
        }
        if ((long)minSide * maxSide > MaxTotalPixels)
        {
            // 双约束交集：maxSide ≤ sqrt(总像素 / minRatio)（长边 × 短边 = maxSide² × minRatio）
            maxSide = Math.Min(MaxEdgePx, Round16(MathF.Sqrt(MaxTotalPixels / minRatio)));
            minSide = Round16(maxSide * minRatio);
        }

        return ratioF >= 1f ? (maxSide, minSide) : (minSide, maxSide);
    }

    private static int Round16(float v) => Math.Max(16, (int)MathF.Round(v / 16f) * 16);

    private static float AspectRatioFactor(ImageAspectRatio ratio) => ratio switch
    {
        ImageAspectRatio.R3x2 => 3f / 2f,
        ImageAspectRatio.R2x3 => 2f / 3f,
        ImageAspectRatio.R4x3 => 4f / 3f,
        ImageAspectRatio.R3x4 => 3f / 4f,
        ImageAspectRatio.R16x9 => 16f / 9f,
        ImageAspectRatio.R9x16 => 9f / 16f,
        ImageAspectRatio.R21x9 => 21f / 9f,
        _ => 1f,
    };

    private static string QualityName(ImageQuality quality) => quality switch
    {
        ImageQuality.Low => "low",
        ImageQuality.Medium => "medium",
        ImageQuality.High => "high",
        _ => "auto",
    };
}

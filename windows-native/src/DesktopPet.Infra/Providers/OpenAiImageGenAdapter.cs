using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// OpenAI 兼容族生图适配器（windows-imagegen-design.md §5.2 + v2 设计 §3）：
/// POST {baseUrl}/images/generations（+ /images/edits），通吃 gpt-image 全系 /
/// Grok Imagine / Qwen-Image / FLUX / Kolors / SenseNova 及用户自配 OpenAI 兼容中转端点。
/// v2：尺寸三形态（PixelCalc 换算 / FixedTable 查表 / Grok aspect_ratio+resolution）与
/// 编辑三形态（image 数组 / images 数组 / 单对象）均由注入的能力驱动，零模型特判。
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
    private readonly ImageGenCapabilities _capabilities;

    public OpenAiImageGenAdapter(
        ImageConnection connection,
        string modelId,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? requestTimeout = null,
        bool strictParams = false,
        ImageGenCapabilities? capabilities = null)
        : base(connection, credentials, httpClient, requestTimeout, strictParams)
    {
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        _capabilities = capabilities ?? DefaultCapabilities();
    }

    public override string Family => OpenAiFamily;

    /// <summary>缺省能力（适配器直接构造/测试用；等价目录未命中的 family 推断）。</summary>
    private static ImageGenCapabilities DefaultCapabilities() => new(
        NativeTransparency: false,
        [ImageAspectRatio.R1x1, ImageAspectRatio.R3x2, ImageAspectRatio.R2x3,
         ImageAspectRatio.R4x3, ImageAspectRatio.R3x4, ImageAspectRatio.R16x9, ImageAspectRatio.R9x16],
        [ImageScale.S1K, ImageScale.S2K],
        Editing: true, MaxReferenceImages: 1, Seed: false);

    protected override bool HasReducibleParameters(ImageGenSpec spec)
        => spec.Quality != ImageQuality.Auto || spec.Transparent || spec.Seed is not null;

    protected override JsonObject BuildGenerateBody(ImageGenSpec spec, bool reduced)
    {
        var body = new JsonObject
        {
            ["model"] = _modelId,
            ["prompt"] = spec.Prompt,
            ["n"] = 1,
            // 不发送 response_format：部分端点直接拒绝顶层该参数（需放 extra_body），
            // 本适配器对 b64_json/url 两种响应均已兼容（见 ParseResponseAsync），不发送最稳。
        };

        // v2 尺寸三形态（能力驱动，windows-imagegen-v2-design.md §3）
        switch (_capabilities.SizeStyle)
        {
            case ImageSizeStyle.FixedTable:
                body["size"] = ResolveFixedSize(spec.AspectRatio);
                break;
            case ImageSizeStyle.AspectRatioResolution:
                // Grok 官方形态：aspect_ratio + resolution 枚举（不发 size）
                body["aspect_ratio"] = ImageAspectRatioParser.ToDisplay(spec.AspectRatio);
                body["resolution"] = ResolutionName(spec.Scale);
                break;
            default:
                body["size"] = ResolveSize(spec.AspectRatio, spec.Scale);
                break;
        }

        // quality：OpenAI 系支持 low/medium/high；能力关闭（SenseNova 等）或降级重试时不发
        if (!reduced && _capabilities.QualityLevels && spec.Quality != ImageQuality.Auto)
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

    /// <summary>固定尺寸表查表（比例归类匹配；未命中取表第一项兜底）。</summary>
    private string ResolveFixedSize(ImageAspectRatio ratio)
        => ImageSizeTable.FindSize(_capabilities.FixedSizes, ratio)
           ?? ResolveSize(ratio, ImageScale.S1K); // 空表防御（正常由门面/目录保证非空）

    private static string ResolutionName(ImageScale scale) => scale switch
    {
        ImageScale.S2K => "2k",
        ImageScale.S4K => "4k",
        _ => "1k",
    };

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

    /// <summary>multipart 编辑（newapi 类中转 gpt-image-2）：图片走文件字段，body 只含生成参数。</summary>
    protected override bool UsesMultipartEdit => EffectiveEditStyle() == ImageEditStyle.MultipartFormData;

    /// <summary>编辑 body：形态由能力驱动（gpt-image-2 新 images 数组 / Grok 单对象 / agnes extra_body.image / 其余 image 数组）。</summary>
    protected override JsonNode? BuildEditImages(IReadOnlyList<ReferenceImage> references)
    {
        if (references.Count == 0)
            throw new ProviderException("invalid-request", "编辑至少需要一张参考图");
        return EffectiveEditStyle() switch
        {
            ImageEditStyle.SingleObject => BuildEditImagesObject(references),
            ImageEditStyle.ImagesArray => BuildEditImagesImagesArray(references),
            ImageEditStyle.ExtraBodyImageArray => BuildEditImagesDataUriArray(references),
            _ => BuildEditImagesArray(references),
        };
    }

    /// <summary>agnes 家族：image 参数 = data URI/URL 字符串数组（官方文档核实，非对象数组）。</summary>
    private static JsonNode BuildEditImagesDataUriArray(IReadOnlyList<ReferenceImage> references)
    {
        var arr = new JsonArray();
        foreach (var r in references)
        {
            arr.Add(ToDataUri(r));
        }
        return arr;
    }

    /// <summary>编辑 body 整体构建：ImagesArray 形态的键名是 images（其余 image）；multipart 形态不含图（走文件字段）。</summary>
    protected override JsonObject BuildEditBody(ImageGenSpec spec, IReadOnlyList<ReferenceImage> references)
    {
        var body = BuildGenerateBody(spec, reduced: false);
        if (EffectiveEditStyle() == ImageEditStyle.MultipartFormData)
            return body; // 图片由 BuildMultipartEditContent 以文件字段发送
        if (EffectiveEditStyle() == ImageEditStyle.ExtraBodyImageArray)
        {
            // agnes：image 数组放在 extra_body.image（非顶层），且仍走 /images/generations。
            body["extra_body"] = new JsonObject { ["image"] = BuildEditImages(references) };
            return body;
        }
        body[EffectiveEditStyle() == ImageEditStyle.ImagesArray ? "images" : "image"] = BuildEditImages(references);
        return body;
    }

    /// <summary>agnes 图生图复用 /images/generations 端点（非 /images/edits）。</summary>
    protected override string EndpointPath(bool references)
        => EffectiveEditStyle() == ImageEditStyle.ExtraBodyImageArray
            ? "images/generations"
            : base.EndpointPath(references);

    /// <summary>gpt-image-2 官方新形态：images: [{image_url}]（无 type，2026 文档核实）。</summary>
    private static JsonNode BuildEditImagesImagesArray(IReadOnlyList<ReferenceImage> references)
    {
        var arr = new JsonArray();
        foreach (var r in references)
        {
            arr.Add(new JsonObject { ["image_url"] = ToDataUri(r) });
        }
        return arr;
    }

    /// <summary>Auto 按模型 id 推断：grok-* → 单对象；gpt-image-2* → images 数组；其余 image 数组。</summary>
    private ImageEditStyle EffectiveEditStyle()
        => _capabilities.EditStyle != ImageEditStyle.Auto ? _capabilities.EditStyle
         : _modelId.StartsWith("grok-", StringComparison.OrdinalIgnoreCase) ? ImageEditStyle.SingleObject
         : _modelId.StartsWith("gpt-image-2", StringComparison.OrdinalIgnoreCase) ? ImageEditStyle.ImagesArray
         : ImageEditStyle.ImageArray;

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

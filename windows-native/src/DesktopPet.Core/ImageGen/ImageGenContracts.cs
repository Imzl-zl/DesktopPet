using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Core.ImageGen;

/// <summary>宽高比（统一枚举；像素换算在各适配器内完成，见 windows-imagegen-design.md §4.1）。</summary>
public enum ImageAspectRatio
{
    R1x1, R3x2, R2x3, R4x3, R3x4, R5x4, R4x5, R16x9, R9x16, R21x9, R9x21, Auto,
}

/// <summary>分辨率档位（OpenAI 适配器换算像素：短边=档位像素并对齐 16 倍数）。</summary>
public enum ImageScale
{
    S1K, S2K, S4K,
}

/// <summary>尺寸参数形态（驱动 OpenAI 兼容适配器 body 构造；UI 不感知，见 windows-imagegen-v2-design.md §2）。</summary>
public enum ImageSizeStyle
{
    PixelCalc,                // 默认：宽高比+档位 → 像素 size（16 倍数约束）
    FixedTable,               // 固定尺寸表：按比例查表发 size（SenseNova）
    AspectRatioResolution,    // aspect_ratio + resolution 枚举（Grok）
}

/// <summary>编辑请求 image 字段形态（驱动适配器 EditBody；UI 不感知，见 v2 设计 §2）。</summary>
public enum ImageEditStyle
{
    Auto,               // 按模型 id 推断：gpt-image-2* → ImagesArray；grok-* → SingleObject；其余 ImageArray
    ImageArray,         // image: [{type:"image_url", image_url:{url}}]（gpt-image-1.x / 中转主流）
    ImagesArray,        // images: [{image_url}]（gpt-image-2 官方新形态，无 type）
    SingleObject,       // image: {url, type:"image_url"}（Grok）
    MultipartFormData,  // multipart/form-data（image=文件字段；newapi 类中转按 OpenAI SDK 行为实现，gpt-image-2 编辑必须）
}

/// <summary>质量档位（OpenAI 族有；Google 族忽略）。</summary>
public enum ImageQuality
{
    Auto, Low, Medium, High,
}

/// <summary>参考图（图生图/编辑）；Name 为 UI 显示用（文件名/URL），可空。</summary>
public sealed record ReferenceImage(byte[] Bytes, string MimeType = "image/png", string? Name = null);

/// <summary>统一生成请求（跨协议族）。ReferenceImages 非空 = 编辑模式。</summary>
public sealed record ImageGenSpec(
    string Prompt,
    ImageAspectRatio AspectRatio = ImageAspectRatio.R1x1,
    ImageScale Scale = ImageScale.S1K,
    ImageQuality Quality = ImageQuality.Auto,
    bool Transparent = false,
    IReadOnlyList<ReferenceImage>? ReferenceImages = null,
    long? Seed = null,
    IReadOnlyDictionary<string, object>? ExtraParams = null);

/// <summary>统一输出（字节 + 元数据）。</summary>
public sealed record ImageGenOutput(byte[] Bytes, string MimeType, string? SeedUsed = null);

/// <summary>能力描述（驱动 UI 参数面板渲染与策略选择）。v2 新增：质量/尺寸表/尺寸形态/编辑形态。</summary>
public sealed record ImageGenCapabilities(
    bool NativeTransparency,
    IReadOnlyList<ImageAspectRatio> AspectRatios,
    IReadOnlyList<ImageScale> Scales,
    bool Editing,
    int MaxReferenceImages,
    bool Seed,
    bool QualityLevels = true,                          // 新增：false = 隐藏质量下拉且不发 quality 参数
    IReadOnlyList<string>? FixedSizes = null,           // 新增：固定尺寸表（非空 ⇒ SizeStyle=FixedTable）
    ImageSizeStyle SizeStyle = ImageSizeStyle.PixelCalc,// 新增
    ImageEditStyle EditStyle = ImageEditStyle.Auto);    // 新增

/// <summary>模型目录条目（内置 JSON 数据，随应用分发；新增模型 = 改数据零代码）。</summary>
public sealed record ImageModelDescriptor(
    string Id,     // 真实模型 id：gpt-image-2 / grok-imagine-image / gemini-3.1-flash-image ...
    string Family, // "openai" | "google"
    string Name,   // 显示名
    ImageGenCapabilities Capabilities,
    string? PriceHint = null,
    string? Note = null);

/// <summary>
/// 连接配置（providers.json image.connections[]；ApiKeyRef 引用 Credential Manager）。
/// Channel = 渠道模板 id（v2 修订：用户显式选渠道而非依赖推断；空 = 自定义端点）。
/// 模型级能力声明在 ImageConnectionsConfig.ModelCapabilities（顶层共享，键=模型 id），由门面注入。
/// </summary>
public sealed record ImageConnection(
    string Id,
    string Name,
    string Family,                // "openai" | "google"
    string BaseUrl,
    string ApiKeyRef,             // 空 = 无鉴权（本地 ComfyUI 等）
    IReadOnlyList<string> Models, // 模型白名单（真实 id）；空 = family 目录全量
    string Channel = "");         // v2 修订：渠道模板 id（ImageChannelCatalog）；空 = 自定义

/// <summary>
/// 渠道模板（v2 修订：用户显式选择厂家/渠道，渠道行为进数据不进推断）。
/// Capabilities = 渠道级默认能力（作用于该渠道全部模型；模型级声明可覆盖）。
/// </summary>
public sealed record ImageChannelTemplate(
    string Id,
    string Name,
    string Family,
    string BaseUrl,
    CustomImageCapabilities? Capabilities = null,
    string? Note = null);

/// <summary>
/// 自定义模型能力声明（v2 设计 §2/§3.3：providers.json modelCapabilities[modelId]）。
/// 全部字段可选：null/缺省 = 继承目录或 family 推断；仅覆盖声明过的维度。
/// </summary>
public sealed record CustomImageCapabilities(
    bool? NativeTransparency = null,
    List<string>? AspectRatios = null,
    List<string>? Scales = null,
    bool? Editing = null,
    int? MaxReferenceImages = null,
    bool? Seed = null,
    bool? Quality = null,
    List<string>? FixedSizes = null,   // 非空 ⇒ SizeStyle=FixedTable，比例/档位由表推导
    string? SizeStyle = null,          // pixelCalc | fixedTable | aspectRatioResolution
    string? EditStyle = null);         // auto | imageArray | imagesArray | singleObject

/// <summary>协议族端口（适配器实现：OpenAI 兼容族 / Gemini 族）。</summary>
public interface IImageGenProvider
{
    string Family { get; }
    Task<ImageGenOutput> GenerateAsync(ImageGenSpec spec, CancellationToken ct);
    Task<ImageGenOutput> EditAsync(ImageGenSpec spec, IReadOnlyList<ReferenceImage> references, CancellationToken ct);
}

/// <summary>透明处理策略。两段式：请求前可能增强 prompt，响应后可能做像素级后处理。</summary>
public interface ITransparencyStrategy
{
    /// <summary>是否需要请求前 prompt 增强（原生透明 = false）。</summary>
    bool RequiresPromptEnhancement { get; }

    /// <summary>请求前：包装 chromakey 规范（纯绿 #00FF00 + 白描边 + 主体无绿 + 居中留白）。</summary>
    string EnhancePrompt(string prompt);

    /// <summary>响应后：HSV 键控 → 边缘清理 → RGBA PNG。原生透明策略为 no-op。</summary>
    Task<ImageGenOutput> PostProcessAsync(ImageGenOutput output, CancellationToken ct);
}

/// <summary>原生透明策略（gpt-image-1 系列）：两段均 no-op，适配器直传 background=transparent。</summary>
public sealed class NativeTransparencyStrategy : ITransparencyStrategy
{
    public bool RequiresPromptEnhancement => false;
    public string EnhancePrompt(string prompt) => prompt;

    public Task<ImageGenOutput> PostProcessAsync(ImageGenOutput output, CancellationToken ct)
        => Task.FromResult(output);
}

/// <summary>宽高比解析（目录 JSON 字符串 ⇄ 枚举）。</summary>
public static class ImageAspectRatioParser
{
    public static bool TryParse(string value, out ImageAspectRatio ratio)
    {
        switch (value)
        {
            case "1:1": ratio = ImageAspectRatio.R1x1; return true;
            case "3:2": ratio = ImageAspectRatio.R3x2; return true;
            case "2:3": ratio = ImageAspectRatio.R2x3; return true;
            case "4:3": ratio = ImageAspectRatio.R4x3; return true;
            case "3:4": ratio = ImageAspectRatio.R3x4; return true;
            case "5:4": ratio = ImageAspectRatio.R5x4; return true;
            case "4:5": ratio = ImageAspectRatio.R4x5; return true;
            case "16:9": ratio = ImageAspectRatio.R16x9; return true;
            case "9:16": ratio = ImageAspectRatio.R9x16; return true;
            case "21:9": ratio = ImageAspectRatio.R21x9; return true;
            case "9:21": ratio = ImageAspectRatio.R9x21; return true;
            case "auto": ratio = ImageAspectRatio.Auto; return true;
            default: ratio = default; return false;
        }
    }

    public static string ToDisplay(ImageAspectRatio ratio) => ratio switch
    {
        ImageAspectRatio.R1x1 => "1:1",
        ImageAspectRatio.R3x2 => "3:2",
        ImageAspectRatio.R2x3 => "2:3",
        ImageAspectRatio.R4x3 => "4:3",
        ImageAspectRatio.R3x4 => "3:4",
        ImageAspectRatio.R5x4 => "5:4",
        ImageAspectRatio.R4x5 => "4:5",
        ImageAspectRatio.R16x9 => "16:9",
        ImageAspectRatio.R9x16 => "9:16",
        ImageAspectRatio.R21x9 => "21:9",
        ImageAspectRatio.R9x21 => "9:21",
        _ => "auto",
    };

    /// <summary>像素尺寸 → 最近邻标称比例（相对偏差 ≤10%；固定尺寸表匹配用，SenseNova 最大偏差约 4.3%）。</summary>
    public static bool TryFromPixels(int width, int height, out ImageAspectRatio ratio)
    {
        ratio = default;
        if (width <= 0 || height <= 0) return false;
        var target = (double)width / height;
        var best = ImageAspectRatio.R1x1;
        var bestDiff = double.MaxValue;
        foreach (var candidate in Enum.GetValues<ImageAspectRatio>())
        {
            if (candidate == ImageAspectRatio.Auto) continue;
            var diff = Math.Abs(target - NominalRatio(candidate)) / NominalRatio(candidate);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = candidate;
            }
        }
        if (bestDiff > 0.10) return false;
        ratio = best;
        return true;
    }

    private static double NominalRatio(ImageAspectRatio ratio) => ratio switch
    {
        ImageAspectRatio.R3x2 => 3.0 / 2.0,
        ImageAspectRatio.R2x3 => 2.0 / 3.0,
        ImageAspectRatio.R4x3 => 4.0 / 3.0,
        ImageAspectRatio.R3x4 => 3.0 / 4.0,
        ImageAspectRatio.R5x4 => 5.0 / 4.0,
        ImageAspectRatio.R4x5 => 4.0 / 5.0,
        ImageAspectRatio.R16x9 => 16.0 / 9.0,
        ImageAspectRatio.R9x16 => 9.0 / 16.0,
        ImageAspectRatio.R21x9 => 21.0 / 9.0,
        ImageAspectRatio.R9x21 => 9.0 / 21.0,
        _ => 1.0,
    };
}

/// <summary>分辨率档位解析。</summary>
public static class ImageScaleParser
{
    public static bool TryParse(string value, out ImageScale scale)
    {
        switch (value)
        {
            case "1K": scale = ImageScale.S1K; return true;
            case "2K": scale = ImageScale.S2K; return true;
            case "4K": scale = ImageScale.S4K; return true;
            default: scale = default; return false;
        }
    }

    public static string ToDisplay(ImageScale scale) => scale switch
    {
        ImageScale.S1K => "1K",
        ImageScale.S2K => "2K",
        ImageScale.S4K => "4K",
        _ => "1K",
    };
}

/// <summary>能力 JSON 序列化（目录文件用可读字符串，枚举仅在内存中使用）。</summary>
public static class ImageGenCapabilitiesSerializer
{
    public static List<ImageAspectRatio> ParseAspectRatios(List<string>? values)
        => values is null ? [ImageAspectRatio.R1x1]
           : values.Select(v => ImageAspectRatioParser.TryParse(v, out var r) ? r : ImageAspectRatio.R1x1).Distinct().ToList();

    public static List<ImageScale> ParseScales(List<string>? values)
        => values is null ? [ImageScale.S1K]
           : values.Select(v => ImageScaleParser.TryParse(v, out var s) ? s : ImageScale.S1K).Distinct().ToList();
}

/// <summary>尺寸形态字符串解析（目录 JSON / 自定义能力声明）。</summary>
public static class ImageSizeStyleParser
{
    public static ImageSizeStyle? TryParse(string? value)
        => string.Equals(value, "pixelCalc", StringComparison.OrdinalIgnoreCase) ? ImageSizeStyle.PixelCalc
         : string.Equals(value, "fixedTable", StringComparison.OrdinalIgnoreCase) ? ImageSizeStyle.FixedTable
         : string.Equals(value, "aspectRatioResolution", StringComparison.OrdinalIgnoreCase) ? ImageSizeStyle.AspectRatioResolution
         : null;
}

/// <summary>固定尺寸表工具（v2 设计 §2/§3）：解析 "WxH"、按比例查表。</summary>
public static class ImageSizeTable
{
    /// <summary>解析 "WxH" / "W×H" 像素串。</summary>
    public static bool TryParse(string size, out int width, out int height)
    {
        width = height = 0;
        var parts = size.Split('x', '×');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), out width)
            && int.TryParse(parts[1].Trim(), out height)
            && width > 0 && height > 0;
    }

    /// <summary>按比例查表：比例归类匹配优先；未命中取第一项兜底；空表返回 null。</summary>
    public static string? FindSize(IReadOnlyList<string>? sizes, ImageAspectRatio ratio)
    {
        if (sizes is null || sizes.Count == 0) return null;
        foreach (var size in sizes)
        {
            if (TryParse(size, out var w, out var h)
                && ImageAspectRatioParser.TryFromPixels(w, h, out var r)
                && r == ratio)
            {
                return size;
            }
        }
        return sizes[0];
    }
}

/// <summary>编辑形态字符串解析（目录 JSON / 自定义能力声明）。</summary>
public static class ImageEditStyleParser
{
    public static ImageEditStyle? TryParse(string? value)
        => string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase) ? ImageEditStyle.Auto
         : string.Equals(value, "imageArray", StringComparison.OrdinalIgnoreCase) ? ImageEditStyle.ImageArray
         : string.Equals(value, "imagesArray", StringComparison.OrdinalIgnoreCase) ? ImageEditStyle.ImagesArray
         : string.Equals(value, "singleObject", StringComparison.OrdinalIgnoreCase) ? ImageEditStyle.SingleObject
         : string.Equals(value, "multipartFormData", StringComparison.OrdinalIgnoreCase) ? ImageEditStyle.MultipartFormData
         : null;
}

/// <summary>目录文件中间模型（JSON 反序列化用）。</summary>
public sealed record ImageModelCatalogEntry(
    string Id,
    string Family,
    string Name,
    ImageModelCapabilitiesJson Capabilities,
    string? PriceHint = null,
    string? Note = null);

public sealed record ImageModelCapabilitiesJson(
    bool NativeTransparency,
    List<string>? AspectRatios = null,
    List<string>? Scales = null,
    bool Editing = false,
    int MaxReferenceImages = 0,
    bool Seed = false,
    bool Quality = true,                    // v2：false = 无质量参数
    List<string>? FixedSizes = null,        // v2：固定尺寸表（非空 ⇒ SizeStyle=FixedTable）
    string? SizeStyle = null,               // v2：pixelCalc | fixedTable | aspectRatioResolution
    string? EditStyle = null);              // v2：auto | imageArray | imagesArray | singleObject

public sealed record ImageModelCatalogFile(List<ImageModelCatalogEntry>? Models);

/// <summary>
/// providers.json image 段（windows-imagegen-design.md §6：连接列表 + 总结图模型引用）。
/// 旧版单连接格式（baseUrl/apiKeyRef/modelName/size 平铺）由专用 converter 读入 Legacy* 字段，
/// Normalize 时迁移为 Connections[0]；序列化只输出连接列表（不含 Legacy*）。
/// </summary>
[JsonConverter(typeof(ImageConnectionsConfigConverter))]
public sealed class ImageConnectionsConfig
{
    public List<ImageConnection> Connections { get; set; } = [];

    /// <summary>总结图模型引用 "{connectionId}/{modelId}"；空 = 自动（首连接首模型）。</summary>
    public string SummaryModelRef { get; set; } = "";

    /// <summary>v2：自定义模型能力声明（modelId → 能力覆盖；models 白名单外的条目视为失效）。</summary>
    public IReadOnlyDictionary<string, CustomImageCapabilities>? ModelCapabilities { get; set; }

    // ── 旧版单连接格式（仅反序列化读取，迁移期消费；序列化永不输出）──
    [JsonIgnore]
    public string? LegacyBaseUrl { get; set; }

    [JsonIgnore]
    public string? LegacyApiKeyRef { get; set; }

    [JsonIgnore]
    public string? LegacyModelName { get; set; }

    public static ImageConnectionsConfig? Normalize(ImageConnectionsConfig? raw)
    {
        if (raw is null) return null;

        var connections = (raw.Connections ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Id)
                        && !string.IsNullOrWhiteSpace(c.BaseUrl))
            .Select(c => c with
            {
                Name = string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name.Trim(),
                BaseUrl = c.BaseUrl.Trim(),
                Models = (c.Models ?? [])
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim())
                    .ToList(),
            })
            .ToList();

        // 旧版单连接迁移（模型 id 以旧 modelName 为准；family 默认 openai——旧实现只有这一族）
        if (connections.Count == 0
            && !string.IsNullOrWhiteSpace(raw.LegacyBaseUrl)
            && !string.IsNullOrWhiteSpace(raw.LegacyModelName))
        {
            connections.Add(new ImageConnection(
                Id: "legacy",
                Name: raw.LegacyModelName,
                Family: ImageModelCatalog.FamilyOpenAi,
                BaseUrl: raw.LegacyBaseUrl.Trim(),
                ApiKeyRef: raw.LegacyApiKeyRef ?? "",
                Models: [raw.LegacyModelName.Trim()]));
        }

        if (connections.Count == 0)
            return null;

        // 能力声明只保留白名单内的模型（键不在任何连接 Models 中的条目失效）
        var validModels = connections.SelectMany(c => c.Models)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capabilities = (raw.ModelCapabilities ?? new Dictionary<string, CustomImageCapabilities>())
            .Where(kv => validModels.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        return new ImageConnectionsConfig
        {
            Connections = connections,
            SummaryModelRef = raw.SummaryModelRef ?? "",
            ModelCapabilities = capabilities.Count == 0 ? null : capabilities,
        };
    }
}

/// <summary>
/// 兼容读写：新格式（connections 数组）正常读；旧平铺格式（baseUrl/apiKeyRef/modelName/size）
/// 读入 Legacy* 字段供 Normalize 迁移。写入永远输出新格式。
/// </summary>
public sealed class ImageConnectionsConfigConverter : JsonConverter<ImageConnectionsConfig>
{
    private sealed class RawConfig
    {
        public List<ImageConnection>? Connections { get; set; }
        public string? SummaryModelRef { get; set; }
        public Dictionary<string, CustomImageCapabilities>? ModelCapabilities { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKeyRef { get; set; }
        public string? ModelName { get; set; }
    }

    public override ImageConnectionsConfig? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = JsonSerializer.Deserialize<RawConfig>(ref reader, options);
        if (raw is null) return null;
        var cfg = new ImageConnectionsConfig
        {
            Connections = raw.Connections ?? [],
            SummaryModelRef = raw.SummaryModelRef ?? "",
            ModelCapabilities = raw.ModelCapabilities,
        };
        // 旧平铺格式字段（存在 connections 时忽略旧字段）
        if ((raw.Connections is null || raw.Connections.Count == 0) && !string.IsNullOrWhiteSpace(raw.BaseUrl))
        {
            cfg.LegacyBaseUrl = raw.BaseUrl;
            cfg.LegacyApiKeyRef = raw.ApiKeyRef;
            cfg.LegacyModelName = raw.ModelName;
        }
        return cfg;
    }

    public override void Write(
        Utf8JsonWriter writer, ImageConnectionsConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("connections");
        JsonSerializer.Serialize(writer, value.Connections, options);
        if (!string.IsNullOrEmpty(value.SummaryModelRef))
        {
            writer.WritePropertyName("summaryModelRef");
            writer.WriteStringValue(value.SummaryModelRef);
        }
        if (value.ModelCapabilities is { Count: > 0 })
        {
            writer.WritePropertyName("modelCapabilities");
            JsonSerializer.Serialize(writer, value.ModelCapabilities, options);
        }
        writer.WriteEndObject();
    }
}

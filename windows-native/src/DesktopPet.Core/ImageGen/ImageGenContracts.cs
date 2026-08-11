using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Core.ImageGen;

/// <summary>宽高比（统一枚举；像素换算在各适配器内完成，见 windows-imagegen-design.md §4.1）。</summary>
public enum ImageAspectRatio
{
    R1x1, R3x2, R2x3, R4x3, R3x4, R16x9, R9x16, R21x9, Auto,
}

/// <summary>分辨率档位（OpenAI 适配器换算像素：短边=档位像素并对齐 16 倍数）。</summary>
public enum ImageScale
{
    S1K, S2K, S4K,
}

/// <summary>质量档位（OpenAI 族有；Google 族忽略）。</summary>
public enum ImageQuality
{
    Auto, Low, Medium, High,
}

/// <summary>参考图（图生图/编辑）。</summary>
public sealed record ReferenceImage(byte[] Bytes, string MimeType = "image/png");

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

/// <summary>能力描述（驱动 UI 参数面板渲染与策略选择）。</summary>
public sealed record ImageGenCapabilities(
    bool NativeTransparency,
    IReadOnlyList<ImageAspectRatio> AspectRatios,
    IReadOnlyList<ImageScale> Scales,
    bool Editing,
    int MaxReferenceImages,
    bool Seed);

/// <summary>模型目录条目（内置 JSON 数据，随应用分发；新增模型 = 改数据零代码）。</summary>
public sealed record ImageModelDescriptor(
    string Id,     // 真实模型 id：gpt-image-2 / grok-imagine-image / gemini-3.1-flash-image ...
    string Family, // "openai" | "google"
    string Name,   // 显示名
    ImageGenCapabilities Capabilities,
    string? PriceHint = null,
    string? Note = null);

/// <summary>连接配置（providers.json image.connections[]；ApiKeyRef 引用 Credential Manager）。</summary>
public sealed record ImageConnection(
    string Id,
    string Name,
    string Family,                // "openai" | "google"
    string BaseUrl,
    string ApiKeyRef,             // 空 = 无鉴权（本地 ComfyUI 等）
    IReadOnlyList<string> Models); // 模型白名单（真实 id）；空 = family 目录全量

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
            case "16:9": ratio = ImageAspectRatio.R16x9; return true;
            case "9:16": ratio = ImageAspectRatio.R9x16; return true;
            case "21:9": ratio = ImageAspectRatio.R21x9; return true;
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
        ImageAspectRatio.R16x9 => "16:9",
        ImageAspectRatio.R9x16 => "9:16",
        ImageAspectRatio.R21x9 => "21:9",
        _ => "auto",
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
    bool Seed = false);

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

        return connections.Count == 0
            ? null
            : new ImageConnectionsConfig
            {
                Connections = connections,
                SummaryModelRef = raw.SummaryModelRef ?? "",
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
        writer.WriteEndObject();
    }
}

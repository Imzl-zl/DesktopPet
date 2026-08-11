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

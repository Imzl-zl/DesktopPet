using System.Reflection;
using System.Text.Json;

namespace DesktopPet.Core.ImageGen;

/// <summary>
/// 模型目录（windows-imagegen-design.md §4.2）：内置 embedded JSON，随应用分发。
/// 查找规则：先精确匹配 Id；未命中按 Family 默认能力 + id 前缀推断。
/// 新增模型 = 改 Resources/image-models.json，零代码。
/// </summary>
public sealed class ImageModelCatalog
{
    public const string ResourceName = "DesktopPet.Core.Resources.image-models.json";
    public const string FamilyOpenAi = "openai";
    public const string FamilyGoogle = "google";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyList<ImageModelDescriptor> _models;
    private readonly Dictionary<string, ImageModelDescriptor> _byId;

    public ImageModelCatalog(IReadOnlyList<ImageModelDescriptor> models)
    {
        _models = models;
        _byId = models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ImageModelDescriptor> All => _models;

    /// <summary>某 family 的全部目录模型。</summary>
    public IReadOnlyList<ImageModelDescriptor> ForFamily(string family)
        => _models.Where(m => string.Equals(m.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>精确匹配目录条目。</summary>
    public ImageModelDescriptor? Find(string modelId)
        => _byId.TryGetValue(modelId, out var m) ? m : null;

    /// <summary>
    /// 目录查找（连接模型用）：先精确匹配；未命中按 family 默认能力 + id 前缀推断
    /// （grok- / qwen / flux / kolors 等第三方端点常见 id 不在目录中）。
    /// </summary>
    public ImageModelDescriptor Resolve(string modelId, string family)
    {
        if (Find(modelId) is { } exact) return exact;
        var cap = DefaultCapabilitiesForFamily(family);
        // 前缀推断：编辑类模型（id 含 edit）赋予编辑能力
        var editing = modelId.Contains("edit", StringComparison.OrdinalIgnoreCase);
        var resolved = cap with { Editing = cap.Editing || editing, MaxReferenceImages = editing ? 3 : 0 };
        return new ImageModelDescriptor(modelId, family, modelId, resolved);
    }

    /// <summary>从 embedded JSON 加载内置目录。</summary>
    public static ImageModelCatalog LoadBuiltIn()
    {
        var assembly = typeof(ImageModelCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺失内置资源 {ResourceName}");
        return Load(stream);
    }

    public static ImageModelCatalog Load(Stream json)
    {
        var file = JsonSerializer.Deserialize<ImageModelCatalogFile>(json, JsonOptions)
            ?? throw new InvalidOperationException("image-models.json 解析失败");
        var models = (file.Models ?? []).Select(e =>
        {
            var c = e.Capabilities;
            return new ImageModelDescriptor(
                e.Id,
                e.Family,
                e.Name,
                new ImageGenCapabilities(
                    c.NativeTransparency,
                    ImageGenCapabilitiesSerializer.ParseAspectRatios(c.AspectRatios),
                    ImageGenCapabilitiesSerializer.ParseScales(c.Scales),
                    c.Editing,
                    Math.Max(0, c.MaxReferenceImages),
                    c.Seed),
                e.PriceHint,
                e.Note);
        }).ToList();
        return new ImageModelCatalog(models);
    }

    /// <summary>family 默认能力（目录未命中时兜底；自建端点/中转模型按此推断）。</summary>
    private static ImageGenCapabilities DefaultCapabilitiesForFamily(string family)
    {
        if (string.Equals(family, FamilyGoogle, StringComparison.OrdinalIgnoreCase))
        {
            return new ImageGenCapabilities(
                NativeTransparency: false,
                [ImageAspectRatio.R1x1, ImageAspectRatio.R3x2, ImageAspectRatio.R2x3,
                 ImageAspectRatio.R4x3, ImageAspectRatio.R3x4, ImageAspectRatio.R16x9, ImageAspectRatio.R9x16],
                [ImageScale.S1K, ImageScale.S2K],
                Editing: true, MaxReferenceImages: 4, Seed: false);
        }
        return new ImageGenCapabilities(
            NativeTransparency: false,
            [ImageAspectRatio.R1x1, ImageAspectRatio.R3x2, ImageAspectRatio.R2x3,
             ImageAspectRatio.R4x3, ImageAspectRatio.R3x4, ImageAspectRatio.R16x9, ImageAspectRatio.R9x16],
            [ImageScale.S1K, ImageScale.S2K],
            Editing: true, MaxReferenceImages: 1, Seed: false);
    }
}

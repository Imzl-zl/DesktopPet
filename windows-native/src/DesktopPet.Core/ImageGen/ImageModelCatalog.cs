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
        => Resolve(modelId, family, declared: null);

    /// <summary>
    /// v2：带自定义能力声明的查找（providers.json modelCapabilities）。
    /// 声明覆盖目录/推断的对应维度；未声明字段继承。尺寸表声明会重新推导比例/档位并置 FixedTable。
    /// </summary>
    public ImageModelDescriptor Resolve(string modelId, string family, CustomImageCapabilities? declared)
        => Resolve(modelId, family, channelCapabilities: null, declared);

    /// <summary>
    /// v2 修订：能力解析四级优先级——模型级声明(declared) > 渠道模板(channelCapabilities) > 目录/推断。
    /// 渠道默认能力（如 newapi 中转的 multipart 编辑形态）作用于该渠道全部模型，模型级声明可覆盖。
    /// </summary>
    public ImageModelDescriptor Resolve(
        string modelId, string family, CustomImageCapabilities? channelCapabilities, CustomImageCapabilities? declared)
    {
        var baseDescriptor = Find(modelId) is { } exact ? exact : Infer(modelId, family);
        var afterChannel = channelCapabilities is null
            ? baseDescriptor
            : baseDescriptor with { Capabilities = ApplyDeclared(baseDescriptor.Capabilities, channelCapabilities) };
        return declared is null
            ? afterChannel
            : afterChannel with { Capabilities = ApplyDeclared(afterChannel.Capabilities, declared) };
    }

    private static ImageModelDescriptor Infer(string modelId, string family)
    {
        var cap = DefaultCapabilitiesForFamily(family);
        // 前缀推断：编辑类模型（id 含 edit）赋予编辑能力
        var editing = modelId.Contains("edit", StringComparison.OrdinalIgnoreCase);
        var resolved = cap with { Editing = cap.Editing || editing, MaxReferenceImages = editing ? 3 : 0 };
        return new ImageModelDescriptor(modelId, family, modelId, resolved);
    }

    /// <summary>声明覆盖合并：尺寸表声明 → 比例/档位重新推导 + FixedTable；其余字段逐项覆盖。</summary>
    private static ImageGenCapabilities ApplyDeclared(ImageGenCapabilities baseCaps, CustomImageCapabilities d)
    {
        var fixedSizes = (d.FixedSizes ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        if (fixedSizes.Count > 0)
        {
            return baseCaps with
            {
                NativeTransparency = d.NativeTransparency ?? baseCaps.NativeTransparency,
                AspectRatios = DeriveAspectRatios(fixedSizes),
                Scales = DeriveScales(fixedSizes),
                Editing = d.Editing ?? baseCaps.Editing,
                MaxReferenceImages = d.MaxReferenceImages ?? baseCaps.MaxReferenceImages,
                Seed = d.Seed ?? baseCaps.Seed,
                QualityLevels = d.Quality ?? baseCaps.QualityLevels,
                FixedSizes = fixedSizes,
                SizeStyle = ImageSizeStyle.FixedTable,
                EditStyle = ImageEditStyleParser.TryParse(d.EditStyle) ?? baseCaps.EditStyle,
            };
        }

        return baseCaps with
        {
            NativeTransparency = d.NativeTransparency ?? baseCaps.NativeTransparency,
            AspectRatios = d.AspectRatios is { Count: > 0 }
                ? ImageGenCapabilitiesSerializer.ParseAspectRatios(d.AspectRatios)
                : baseCaps.AspectRatios,
            Scales = d.Scales is { Count: > 0 }
                ? ImageGenCapabilitiesSerializer.ParseScales(d.Scales)
                : baseCaps.Scales,
            Editing = d.Editing ?? baseCaps.Editing,
            MaxReferenceImages = d.MaxReferenceImages ?? baseCaps.MaxReferenceImages,
            Seed = d.Seed ?? baseCaps.Seed,
            QualityLevels = d.Quality ?? baseCaps.QualityLevels,
            FixedSizes = null,
            SizeStyle = ImageSizeStyleParser.TryParse(d.SizeStyle) ?? baseCaps.SizeStyle,
            EditStyle = ImageEditStyleParser.TryParse(d.EditStyle) ?? baseCaps.EditStyle,
        };
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
            var fixedSizes = (c.FixedSizes ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();

            // v2 尺寸表推导：FixedSizes 非空 ⇒ SizeStyle=FixedTable，比例/档位由表推导（见 v2 设计 §2）
            ImageSizeStyle sizeStyle;
            List<ImageAspectRatio> aspectRatios;
            List<ImageScale> scales;
            if (fixedSizes.Count > 0)
            {
                sizeStyle = ImageSizeStyle.FixedTable;
                aspectRatios = DeriveAspectRatios(fixedSizes);
                scales = DeriveScales(fixedSizes);
            }
            else
            {
                sizeStyle = ImageSizeStyleParser.TryParse(c.SizeStyle) ?? ImageSizeStyle.PixelCalc;
                aspectRatios = ImageGenCapabilitiesSerializer.ParseAspectRatios(c.AspectRatios);
                scales = ImageGenCapabilitiesSerializer.ParseScales(c.Scales);
            }

            return new ImageModelDescriptor(
                e.Id,
                e.Family,
                e.Name,
                new ImageGenCapabilities(
                    c.NativeTransparency,
                    aspectRatios,
                    scales,
                    c.Editing,
                    Math.Max(0, c.MaxReferenceImages),
                    c.Seed,
                    c.Quality,
                    fixedSizes.Count > 0 ? fixedSizes : null,
                    sizeStyle,
                    ImageEditStyleParser.TryParse(c.EditStyle) ?? ImageEditStyle.Auto),
                e.PriceHint,
                e.Note);
        }).ToList();
        return new ImageModelCatalog(models);
    }

    // ── 固定尺寸表推导（v2 设计 §2）──

    /// <summary>固定尺寸表 → 去重比例（标称比最近邻，相对偏差 ≤10%，SenseNova 最大偏差约 4.3%）。</summary>
    private static List<ImageAspectRatio> DeriveAspectRatios(IReadOnlyList<string> fixedSizes)
    {
        var result = new List<ImageAspectRatio>();
        var seen = new HashSet<ImageAspectRatio>();
        foreach (var size in fixedSizes)
        {
            if (!ImageSizeTable.TryParse(size, out var w, out var h)) continue;
            if (ImageAspectRatioParser.TryFromPixels(w, h, out var ratio) && seen.Add(ratio))
                result.Add(ratio);
        }
        return result.Count > 0 ? result : [ImageAspectRatio.R1x1];
    }

    /// <summary>固定尺寸表 → 档位（最大长边 ≥3500 → 4K；≥2000 → 2K；否则 1K；SenseNova 3136 → 2K）。</summary>
    private static List<ImageScale> DeriveScales(IReadOnlyList<string> fixedSizes)
    {
        var maxLong = 0;
        foreach (var size in fixedSizes)
        {
            if (ImageSizeTable.TryParse(size, out var w, out var h))
                maxLong = Math.Max(maxLong, Math.Max(w, h));
        }
        return [maxLong >= 3500 ? ImageScale.S4K : maxLong >= 2000 ? ImageScale.S2K : ImageScale.S1K];
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

using DesktopPet.Core.ImageGen;
using System.Text.Json;

namespace DesktopPet.Core.Tests.ImageGen;

/// <summary>模型目录（windows-imagegen-design.md §4.2）：内置 JSON 加载、精确匹配、前缀推断。</summary>
public class ImageModelCatalogTests
{
    [Fact]
    public void LoadBuiltIn_ContainsBothFamilies()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        Assert.True(catalog.All.Count >= 10, $"内置目录应 ≥10 个模型，实际 {catalog.All.Count}");
        Assert.Contains(catalog.All, m => m.Family == ImageModelCatalog.FamilyOpenAi);
        Assert.Contains(catalog.All, m => m.Family == ImageModelCatalog.FamilyGoogle);
    }

    [Fact]
    public void LoadBuiltIn_GptImage2_NoNativeTransparency()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var m = catalog.Find("gpt-image-2");
        Assert.NotNull(m);
        Assert.False(m!.Capabilities.NativeTransparency); // gpt-image-2 官方不支持透明（调研结论）
        Assert.Contains(ImageScale.S2K, m.Capabilities.Scales);
        Assert.Contains(ImageScale.S4K, m.Capabilities.Scales);
        Assert.True(m.Capabilities.Editing);
    }

    [Fact]
    public void LoadBuiltIn_GptImage15_NativeTransparency()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var m = catalog.Find("gpt-image-1.5");
        Assert.NotNull(m);
        Assert.True(m!.Capabilities.NativeTransparency); // 唯一原生透明前排模型
    }

    [Fact]
    public void LoadBuiltIn_Gemini_NoTransparency_AllRatios()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var m = catalog.Find("gemini-3.1-flash-image");
        Assert.NotNull(m);
        Assert.False(m!.Capabilities.NativeTransparency);
        Assert.Contains(ImageAspectRatio.R21x9, m.Capabilities.AspectRatios);
        Assert.True(m.Capabilities.Editing);
        Assert.Equal(14, m.Capabilities.MaxReferenceImages);
    }

    [Fact]
    public void Resolve_ExactMatch_UsesCatalogCapabilities()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var resolved = catalog.Resolve("gpt-image-2", ImageModelCatalog.FamilyOpenAi);
        Assert.Equal("GPT Image 2", resolved.Name);
        Assert.False(resolved.Capabilities.NativeTransparency);
    }

    [Fact]
    public void Resolve_UnknownId_FallsBackToFamilyDefaults()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        // 自建/中转端点常见：模型 id 不在目录
        var resolved = catalog.Resolve("my-relay-model-7b", ImageModelCatalog.FamilyOpenAi);
        Assert.Equal("my-relay-model-7b", resolved.Id);
        Assert.Equal(ImageModelCatalog.FamilyOpenAi, resolved.Family);
        Assert.False(resolved.Capabilities.NativeTransparency);
        Assert.Contains(ImageAspectRatio.R16x9, resolved.Capabilities.AspectRatios);
        Assert.Contains(ImageScale.S2K, resolved.Capabilities.Scales);
    }

    [Fact]
    public void Resolve_UnknownIdWithEditKeyword_InfersEditing()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var resolved = catalog.Resolve("my-relay-edit-v2", ImageModelCatalog.FamilyOpenAi);
        Assert.True(resolved.Capabilities.Editing);
        Assert.Equal(3, resolved.Capabilities.MaxReferenceImages);
    }

    [Fact]
    public void Load_FromJsonStream_ParsesAllFields()
    {
        const string json = """
        {
          "models": [
            { "id": "x-model", "family": "openai", "name": "X Model",
              "capabilities": { "nativeTransparency": true,
                                "aspectRatios": ["1:1", "16:9"],
                                "scales": ["1K"],
                                "editing": true, "maxReferenceImages": 2, "seed": true },
              "priceHint": "$0.01" }
          ]
        }
        """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var catalog = ImageModelCatalog.Load(stream);
        var m = catalog.Find("x-model");
        Assert.NotNull(m);
        Assert.True(m!.Capabilities.NativeTransparency);
        Assert.Equal(2, m.Capabilities.AspectRatios.Count);
        Assert.Equal(2, m.Capabilities.MaxReferenceImages);
        Assert.True(m.Capabilities.Seed);
        Assert.Equal("$0.01", m.PriceHint);
    }

    [Theory]
    [InlineData("1:1", ImageAspectRatio.R1x1)]
    [InlineData("16:9", ImageAspectRatio.R16x9)]
    [InlineData("9:16", ImageAspectRatio.R9x16)]
    [InlineData("21:9", ImageAspectRatio.R21x9)]
    [InlineData("9:21", ImageAspectRatio.R9x21)]
    [InlineData("5:4", ImageAspectRatio.R5x4)]
    [InlineData("4:5", ImageAspectRatio.R4x5)]
    [InlineData("auto", ImageAspectRatio.Auto)]
    public void AspectRatioParser_RoundTrip(string display, ImageAspectRatio ratio)
    {
        Assert.True(ImageAspectRatioParser.TryParse(display, out var parsed));
        Assert.Equal(ratio, parsed);
        Assert.Equal(display, ImageAspectRatioParser.ToDisplay(parsed));
    }

    [Fact]
    public void LoadBuiltIn_SenseNova_FixedSizesDerived()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var m = catalog.Find("sensenova-u1-fast");
        Assert.NotNull(m);
        Assert.Equal(ImageSizeStyle.FixedTable, m!.Capabilities.SizeStyle);
        Assert.Equal(11, m.Capabilities.FixedSizes!.Count);
        Assert.False(m.Capabilities.QualityLevels);
        Assert.False(m.Capabilities.Editing);
        Assert.Contains(ImageAspectRatio.R9x21, m.Capabilities.AspectRatios);
    }

    [Fact]
    public void LoadBuiltIn_Grok_UsesAspectRatioResolutionStyle()
    {
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var m = catalog.Find("grok-imagine-image");
        Assert.NotNull(m);
        Assert.Equal(ImageSizeStyle.AspectRatioResolution, m!.Capabilities.SizeStyle);
        Assert.Equal(ImageEditStyle.SingleObject, m.Capabilities.EditStyle); // 目录显式声明（不再靠 id 推断）
    }

    [Fact]
    public void Resolve_WithDeclaredFixedSizes_OverridesInference()
    {
        // v2：自定义能力声明（providers.json modelCapabilities）——尺寸表声明覆盖推断
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var declared = new CustomImageCapabilities(
            Editing: true, MaxReferenceImages: 2, Quality: false,
            FixedSizes: ["2048x2048", "1024x1024"]);

        var resolved = catalog.Resolve("my-relay-edit-v2", ImageModelCatalog.FamilyOpenAi, declared);

        Assert.Equal(ImageSizeStyle.FixedTable, resolved.Capabilities.SizeStyle);
        Assert.Equal(2, resolved.Capabilities.FixedSizes!.Count);
        Assert.False(resolved.Capabilities.QualityLevels);
        Assert.True(resolved.Capabilities.Editing);
        Assert.Equal(2, resolved.Capabilities.MaxReferenceImages);
        // 比例/档位由表推导（2048 正方 + 1024 正方 → 1:1；最大长边 2048 → 2K）
        Assert.Equal([ImageAspectRatio.R1x1], resolved.Capabilities.AspectRatios);
        Assert.Equal([ImageScale.S2K], resolved.Capabilities.Scales);
    }

    [Fact]
    public void Resolve_WithDeclaredPartialFields_KeepsInferenceDefaults()
    {
        // 声明只覆盖编辑能力，其余继承推断（family 默认：PixelCalc / QualityLevels=true）
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var resolved = catalog.Resolve(
            "my-relay-model", ImageModelCatalog.FamilyOpenAi, new CustomImageCapabilities(Editing: false));

        Assert.False(resolved.Capabilities.Editing);
        Assert.Equal(ImageSizeStyle.PixelCalc, resolved.Capabilities.SizeStyle);
        Assert.True(resolved.Capabilities.QualityLevels);
        Assert.Null(resolved.Capabilities.FixedSizes);
    }

    [Fact]
    public void Resolve_DeclaredOverridesBuiltInEntry()
    {
        // 声明可覆盖内置目录条目（如给 gpt-image-2 强制声明质量关）
        var catalog = ImageModelCatalog.LoadBuiltIn();
        var resolved = catalog.Resolve(
            "gpt-image-2", ImageModelCatalog.FamilyOpenAi, new CustomImageCapabilities(Quality: false));

        Assert.False(resolved.Capabilities.QualityLevels);
        Assert.True(resolved.Capabilities.Editing); // 未声明字段保持目录值
        Assert.True(resolved.Capabilities.NativeTransparency == false);
    }

    [Fact]
    public void Load_LegacyCapabilitiesWithoutNewFields_UseDefaults()
    {
        // v1 时代目录 JSON：无 quality/fixedSizes/sizeStyle/editStyle → 默认值（向后兼容）
        const string json = """
        {
          "models": [
            { "id": "legacy-model", "family": "openai", "name": "Legacy",
              "capabilities": { "nativeTransparency": false } }
          ]
        }
        """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var cap = ImageModelCatalog.Load(stream).Find("legacy-model")!.Capabilities;
        Assert.True(cap.QualityLevels);
        Assert.Null(cap.FixedSizes);
        Assert.Equal(ImageSizeStyle.PixelCalc, cap.SizeStyle);
        Assert.Equal(ImageEditStyle.Auto, cap.EditStyle);
    }

    [Fact]
    public void Load_FixedSizes_DerivesFixedTableStyleRatiosAndScales()
    {
        // SenseNova U1 Fast 官方 11 种固定 2K 尺寸（v2 设计 §2/§5）
        const string json = """
        {
          "models": [
            { "id": "sensenova-u1-fast", "family": "openai", "name": "SenseNova U1 Fast",
              "capabilities": {
                "nativeTransparency": false,
                "fixedSizes": ["2752x1536","1536x2752","2048x2048","2496x1664","1664x2496",
                               "2368x1760","1760x2368","2272x1824","1824x2272","3072x1376","1344x3136"],
                "editing": false, "maxReferenceImages": 0, "seed": false, "quality": false
              } }
          ]
        }
        """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var m = ImageModelCatalog.Load(stream).Find("sensenova-u1-fast");
        Assert.NotNull(m);
        var cap = m!.Capabilities;

        Assert.Equal(ImageSizeStyle.FixedTable, cap.SizeStyle);
        Assert.Equal(ImageEditStyle.Auto, cap.EditStyle);
        Assert.False(cap.QualityLevels);
        Assert.Equal(11, cap.FixedSizes!.Count);
        Assert.Equal("2752x1536", cap.FixedSizes[0]);
        Assert.Equal("1344x3136", cap.FixedSizes[^1]);

        // 尺寸表 → 去重比例（含 5:4 / 4:5 / 21:9 / 9:21，像素微调按最近邻匹配）
        Assert.Equal(11, cap.AspectRatios.Count);
        Assert.Contains(ImageAspectRatio.R16x9, cap.AspectRatios);
        Assert.Contains(ImageAspectRatio.R9x16, cap.AspectRatios);
        Assert.Contains(ImageAspectRatio.R1x1, cap.AspectRatios);
        Assert.Contains(ImageAspectRatio.R5x4, cap.AspectRatios);
        Assert.Contains(ImageAspectRatio.R4x5, cap.AspectRatios);
        Assert.Contains(ImageAspectRatio.R21x9, cap.AspectRatios);
        Assert.Contains(ImageAspectRatio.R9x21, cap.AspectRatios);

        // 档位：最大长边 3136 → 2K
        Assert.Equal([ImageScale.S2K], cap.Scales);
    }

    [Fact]
    public void Load_ExplicitSizeStyleAndEditStyle_Parsed()
    {
        const string json = """
        {
          "models": [
            { "id": "grok-imagine-image-quality", "family": "openai", "name": "Grok",
              "capabilities": {
                "nativeTransparency": false,
                "aspectRatios": ["1:1", "16:9"], "scales": ["1K", "2K"],
                "editing": true, "maxReferenceImages": 3,
                "sizeStyle": "aspectRatioResolution",
                "editStyle": "singleObject"
              } }
          ]
        }
        """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var cap = ImageModelCatalog.Load(stream).Find("grok-imagine-image-quality")!.Capabilities;
        Assert.Equal(ImageSizeStyle.AspectRatioResolution, cap.SizeStyle);
        Assert.Equal(ImageEditStyle.SingleObject, cap.EditStyle);
        Assert.True(cap.QualityLevels);
        Assert.Null(cap.FixedSizes);
    }

    [Theory]
    [InlineData("1K", ImageScale.S1K)]
    [InlineData("2K", ImageScale.S2K)]
    [InlineData("4K", ImageScale.S4K)]
    public void ScaleParser_RoundTrip(string display, ImageScale scale)
    {
        Assert.True(ImageScaleParser.TryParse(display, out var parsed));
        Assert.Equal(scale, parsed);
        Assert.Equal(display, ImageScaleParser.ToDisplay(parsed));
    }
}

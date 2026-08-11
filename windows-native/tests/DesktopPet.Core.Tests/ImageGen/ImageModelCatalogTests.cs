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
    [InlineData("auto", ImageAspectRatio.Auto)]
    public void AspectRatioParser_RoundTrip(string display, ImageAspectRatio ratio)
    {
        Assert.True(ImageAspectRatioParser.TryParse(display, out var parsed));
        Assert.Equal(ratio, parsed);
        Assert.Equal(display, ImageAspectRatioParser.ToDisplay(parsed));
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

using DesktopPet.Core.ImageGen;

namespace DesktopPet.Core.Tests.ImageGen;

/// <summary>渠道模板目录（v2 修订，windows-imagegen-v2-design.md §2/§4）：用户显式选渠道，行为进数据。</summary>
public class ImageChannelCatalogTests
{
    [Fact]
    public void LoadBuiltIn_ContainsExpectedChannels()
    {
        var catalog = ImageChannelCatalog.LoadBuiltIn();
        Assert.True(catalog.All.Count >= 6, $"内置渠道应 ≥6，实际 {catalog.All.Count}");
        Assert.NotNull(catalog.Find("openai-official"));
        Assert.NotNull(catalog.Find("google-official"));
        Assert.NotNull(catalog.Find("sensenova-official"));
        Assert.NotNull(catalog.Find("xai-official"));
        Assert.NotNull(catalog.Find("siliconflow"));
        Assert.NotNull(catalog.Find("newapi-relay"));
        Assert.NotNull(catalog.Find(ImageChannelCatalog.CustomChannel));
    }

    [Fact]
    public void Find_UnknownOrEmpty_ReturnsNull()
    {
        var catalog = ImageChannelCatalog.LoadBuiltIn();
        Assert.Null(catalog.Find(""));
        Assert.Null(catalog.Find("no-such-channel"));
    }

    [Fact]
    public void CapabilitiesFor_NewApiRelay_MultipartEditDefault()
    {
        // 实测（2026-08-13）：newapi 中转 gpt-image-2 编辑必须 multipart；渠道默认覆盖模型目录声明
        var catalog = ImageChannelCatalog.LoadBuiltIn();
        var caps = catalog.CapabilitiesFor("newapi-relay");
        Assert.NotNull(caps);
        Assert.Equal("multipartFormData", caps!.EditStyle);
    }

    [Fact]
    public void CapabilitiesFor_XaiOfficial_ProtocolDefaults()
    {
        var catalog = ImageChannelCatalog.LoadBuiltIn();
        var caps = catalog.CapabilitiesFor("xai-official");
        Assert.NotNull(caps);
        Assert.Equal("singleObject", caps!.EditStyle);
        Assert.Equal("aspectRatioResolution", caps.SizeStyle);
    }

    [Fact]
    public void CapabilitiesFor_CustomOrUnknown_ReturnsNull()
    {
        var catalog = ImageChannelCatalog.LoadBuiltIn();
        Assert.Null(catalog.CapabilitiesFor(ImageChannelCatalog.CustomChannel));
        Assert.Null(catalog.CapabilitiesFor(""));
    }

    [Fact]
    public void SensenovaChannel_PointsAtOfficialEndpoint()
    {
        var catalog = ImageChannelCatalog.LoadBuiltIn();
        var tpl = catalog.Find("sensenova-official");
        Assert.NotNull(tpl);
        Assert.Equal("https://token.sensenova.cn/v1", tpl!.BaseUrl);
        Assert.Equal(ImageModelCatalog.FamilyOpenAi, tpl.Family);
    }
}

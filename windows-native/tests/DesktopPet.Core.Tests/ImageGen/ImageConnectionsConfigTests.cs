using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Tests.ImageGen;

/// <summary>providers.json image 段：连接列表 + 旧单连接格式迁移（windows-imagegen-design.md §6）。</summary>
public class ImageConnectionsConfigTests
{
    private static ImageConnection Conn(string id, string model) => new(
        Id: id, Name: id, Family: "openai",
        BaseUrl: "https://x.test/v1", ApiKeyRef: "cred:" + id,
        Models: [model]);

    [Fact]
    public void Normalize_Null_ReturnsNull()
        => Assert.Null(ImageConnectionsConfig.Normalize(null));

    [Fact]
    public void Normalize_EmptyConnections_ReturnsNull()
        => Assert.Null(ImageConnectionsConfig.Normalize(new ImageConnectionsConfig()));

    [Fact]
    public void Normalize_ValidConnections_TrimsAndKeeps()
    {
        var cfg = new ImageConnectionsConfig
        {
            Connections =
            [
                new ImageConnection("c1", " 我的端点 ", "openai", " https://x.test/v1 ", "cred:1", [" m1 "]),
            ],
            SummaryModelRef = "c1/m1",
        };

        var normalized = ImageConnectionsConfig.Normalize(cfg);

        Assert.NotNull(normalized);
        Assert.Single(normalized!.Connections);
        Assert.Equal("我的端点", normalized.Connections[0].Name);
        Assert.Equal("https://x.test/v1", normalized.Connections[0].BaseUrl);
        Assert.Equal("m1", normalized.Connections[0].Models[0]);
        Assert.Equal("c1/m1", normalized.SummaryModelRef);
    }

    [Fact]
    public void Normalize_InvalidConnections_Dropped()
    {
        var cfg = new ImageConnectionsConfig
        {
            Connections =
            [
                new ImageConnection("bad", "bad", "openai", " ", "cred:1", ["m"]),   // baseUrl 空
                Conn("ok", "m2"),
            ],
        };

        var normalized = ImageConnectionsConfig.Normalize(cfg);

        Assert.NotNull(normalized);
        Assert.Single(normalized!.Connections);
        Assert.Equal("ok", normalized.Connections[0].Id);
    }

    [Fact]
    public void Normalize_LegacySingleConnection_MigratesToConnections()
    {
        // 旧格式：image 段平铺 baseUrl/apiKeyRef/modelName/size（反序列化进 Legacy* 字段）
        var cfg = new ImageConnectionsConfig
        {
            LegacyBaseUrl = "https://legacy.test/v1",
            LegacyApiKeyRef = "cred:old",
            LegacyModelName = "gpt-image-1.5",
        };

        var normalized = ImageConnectionsConfig.Normalize(cfg);

        Assert.NotNull(normalized);
        var conn = Assert.Single(normalized!.Connections);
        Assert.Equal("legacy", conn.Id);
        Assert.Equal("openai", conn.Family); // 旧实现只有 OpenAI 兼容族
        Assert.Equal("https://legacy.test/v1", conn.BaseUrl);
        Assert.Equal("cred:old", conn.ApiKeyRef);
        Assert.Equal(["gpt-image-1.5"], conn.Models);
        // 迁移后返回新对象：旧字段自然不携带（Serialize 测试另行验证不输出）
    }

    [Fact]
    public void Deserialize_LegacyJson_Migrates()
    {
        const string legacy = """
        {
          "models": [],
          "image": {
            "baseUrl": "https://legacy.test/v1",
            "apiKeyRef": "cred:old",
            "modelName": "gpt-image-1.5",
            "size": "1024x1024"
          }
        }
        """;

        var file = ProvidersFileModel.Deserialize(legacy);

        Assert.NotNull(file.Image);
        var conn = Assert.Single(file.Image!.Connections);
        Assert.Equal("https://legacy.test/v1", conn.BaseUrl);
        Assert.Equal("gpt-image-1.5", conn.Models[0]);
    }

    [Fact]
    public void Serialize_DoesNotEmitLegacyFields()
    {
        var cfg = new ImageConnectionsConfig
        {
            Connections = [Conn("c1", "m1")],
            SummaryModelRef = "c1/m1",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            new ProvidersFileModel { Image = ImageConnectionsConfig.Normalize(cfg) },
            DesktopPet.Core.Storage.JsonOptions.CamelCase);

        Assert.Contains("\"connections\"", json);
        Assert.Contains("\"summaryModelRef\":\"c1/m1\"", json);
        Assert.DoesNotContain("legacyBaseUrl", json);
        Assert.DoesNotContain("legacyApiKeyRef", json);
        Assert.DoesNotContain("legacyModelName", json);
    }

    // ── v2：自定义模型能力声明（modelCapabilities，windows-imagegen-v2-design.md §3.3）──

    [Fact]
    public void Normalize_ModelCapabilities_FiltersToWhitelist()
    {
        // 能力声明只保留白名单内模型的条目；白名单外（删了模型/残留）失效
        var cfg = new ImageConnectionsConfig
        {
            Connections = [new ImageConnection("c1", "c1", "openai", "https://x.test/v1", "cred:1", ["m1", "m2"])],
            ModelCapabilities = new Dictionary<string, CustomImageCapabilities>
            {
                ["m1"] = new(Editing: false),
                ["stale-model"] = new(Editing: true), // 不在白名单 → 剔除
            },
        };

        var normalized = ImageConnectionsConfig.Normalize(cfg);

        Assert.NotNull(normalized!.ModelCapabilities);
        Assert.Single(normalized.ModelCapabilities);
        Assert.True(normalized.ModelCapabilities!.ContainsKey("m1"));
        Assert.False(normalized.ModelCapabilities.ContainsKey("stale-model"));
    }

    [Fact]
    public void Serialize_ModelCapabilities_RoundTrips()
    {
        var cfg = new ImageConnectionsConfig
        {
            Connections = [Conn("c1", "my-relay-model")],
            ModelCapabilities = new Dictionary<string, CustomImageCapabilities>
            {
                ["my-relay-model"] = new(
                    Editing: true, MaxReferenceImages: 2, Quality: false,
                    FixedSizes: ["2048x2048", "1024x1024"], EditStyle: "imageArray"),
            },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            new ProvidersFileModel { Image = ImageConnectionsConfig.Normalize(cfg) },
            DesktopPet.Core.Storage.JsonOptions.CamelCase);
        Assert.Contains("\"modelCapabilities\":{\"my-relay-model\":", json);

        var back = ProvidersFileModel.Deserialize(json);
        var caps = back.Image!.ModelCapabilities!["my-relay-model"];
        Assert.True(caps.Editing);
        Assert.Equal(2, caps.MaxReferenceImages);
        Assert.False(caps.Quality);
        Assert.Equal(2, caps.FixedSizes!.Count);
    }

    [Fact]
    public void Deserialize_WithoutModelCapabilities_Null()
    {
        // v1 时代文件（无 modelCapabilities 字段）→ null，不报错
        var cfg = new ImageConnectionsConfig { Connections = [Conn("c1", "m1")] };
        var json = System.Text.Json.JsonSerializer.Serialize(
            new ProvidersFileModel { Image = ImageConnectionsConfig.Normalize(cfg) },
            DesktopPet.Core.Storage.JsonOptions.CamelCase);

        var back = ProvidersFileModel.Deserialize(json);

        Assert.Null(back.Image!.ModelCapabilities);
    }
}

using System.Text.Json;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 6 修复：providers.json 的 capabilities 数组格式（架构文档 §3.1 规范
/// ["chat","vision"]）此前无法被 JsonStringEnumConverter 反序列化 → 整个文件解析失败
/// → 模型连接配置永远读不到。自定义 converter 支持数组/单字符串/数字三种输入。
/// </summary>
public class ModelCapabilitiesJsonTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Deserialize_ArrayFormat_ParsesFlags()
    {
        const string json = """
        {"id":"p1","name":"x","baseUrl":"http://localhost/v1","apiKeyRef":"","modelName":"m",
         "capabilities":["chat","vision"],"isDefault":true}
        """;
        var cfg = JsonSerializer.Deserialize<ProviderConfig>(json, Options)!;
        Assert.Equal(ModelCapabilities.Chat | ModelCapabilities.Vision, cfg.Capabilities);
    }

    [Fact]
    public void Deserialize_SingleString_ParsesOneFlag()
    {
        const string json = """
        {"id":"p1","name":"x","baseUrl":"http://localhost/v1","apiKeyRef":"","modelName":"m",
         "capabilities":"chat","isDefault":true}
        """;
        var cfg = JsonSerializer.Deserialize<ProviderConfig>(json, Options)!;
        Assert.Equal(ModelCapabilities.Chat, cfg.Capabilities);
    }

    [Fact]
    public void Deserialize_Number_KeepsLegacyCompatibility()
    {
        const string json = """
        {"id":"p1","name":"x","baseUrl":"http://localhost/v1","apiKeyRef":"","modelName":"m",
         "capabilities":3,"isDefault":true}
        """;
        var cfg = JsonSerializer.Deserialize<ProviderConfig>(json, Options)!;
        Assert.Equal(ModelCapabilities.Chat | ModelCapabilities.Vision, cfg.Capabilities);
    }

    [Fact]
    public void Deserialize_EmptyArray_IsNone()
    {
        const string json = """
        {"id":"p1","name":"x","baseUrl":"http://localhost/v1","apiKeyRef":"","modelName":"m",
         "capabilities":[],"isDefault":true}
        """;
        var cfg = JsonSerializer.Deserialize<ProviderConfig>(json, Options)!;
        Assert.Equal(ModelCapabilities.None, cfg.Capabilities);
    }

    [Fact]
    public void Roundtrip_WritesDocumentedArrayFormat()
    {
        var cfg = new ProviderConfig(
            "p1", "x", "http://localhost/v1", "", "m",
            ModelCapabilities.Chat | ModelCapabilities.Vision, IsDefault: true);
        var json = JsonSerializer.Serialize(cfg, Options);
        Assert.Contains("\"capabilities\":[\"chat\",\"vision\"]", json);

        var back = JsonSerializer.Deserialize<ProviderConfig>(json, Options)!;
        Assert.Equal(cfg.Capabilities, back.Capabilities);
    }

    [Fact]
    public void ProvidersFileModel_DeserializesDocumentedShape()
    {
        // 架构文档 §3.1 的完整 providers.json 形状
        const string json = """
        {
          "models": [
            {
              "id": "openai-default",
              "name": "OpenAI GPT-4o",
              "baseUrl": "https://api.openai.com/v1",
              "apiKeyRef": "openai-key",
              "modelName": "gpt-4o",
              "capabilities": ["chat", "vision"],
              "isDefault": true
            }
          ],
          "image": {
            "baseUrl": "https://api.openai.com/v1",
            "apiKeyRef": "image-key",
            "modelName": "gpt-image-1",
            "size": "1024x1024"
          }
        }
        """;
        var file = ProvidersFileModel.Deserialize(json);
        Assert.Single(file.Models);
        Assert.Equal(ModelCapabilities.Chat | ModelCapabilities.Vision, file.Models[0].Capabilities);
        Assert.NotNull(file.Image);
        // 旧单连接格式迁移为连接列表（windows-imagegen-design.md §6）
        var conn = Assert.Single(file.Image!.Connections);
        Assert.Equal("gpt-image-1", Assert.Single(conn.Models));
        Assert.Equal("image-key", conn.ApiKeyRef);
    }
}

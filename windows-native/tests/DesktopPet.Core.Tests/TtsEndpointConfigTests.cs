using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;

namespace DesktopPet.Core.Tests;

/// <summary>
/// providers.json tts 段（windows-tts-design.md §5.2）：TtsEndpointConfig 归一化
/// + ProvidersFileModel 序列化/迁移判定（对齐 image 段语义）。
/// </summary>
public class TtsEndpointConfigTests
{
    [Fact]
    public void Normalize_ValidConfig_IsKept()
    {
        var file = new ProvidersFileModel
        {
            Tts = new TtsEndpointConfig("https://api.siliconflow.cn/v1", "tts-key", "FunAudioLLM/CosyVoice2-0.5B"),
        };
        var norm = ProvidersFileModel.Normalize(file);
        Assert.NotNull(norm.Tts);
        Assert.Equal("https://api.siliconflow.cn/v1", norm.Tts!.BaseUrl);
        Assert.Equal("tts-key", norm.Tts.ApiKeyRef);
        Assert.Equal("FunAudioLLM/CosyVoice2-0.5B", norm.Tts.ModelName);
        Assert.Equal("", norm.Tts.Voice); // 默认音色空 = 自动
    }

    [Theory]
    [InlineData("", "model")]   // 缺 BaseUrl → 未配置
    [InlineData("http://x", "")] // 缺 ModelName → 未配置
    public void Normalize_MissingRequiredFields_DropsConfig(string baseUrl, string model)
    {
        var file = new ProvidersFileModel { Tts = new TtsEndpointConfig(baseUrl, "k", model) };
        Assert.Null(ProvidersFileModel.Normalize(file).Tts);
    }

    [Fact]
    public void Normalize_NullTts_StaysNull()
    {
        Assert.Null(ProvidersFileModel.Normalize(new ProvidersFileModel()).Tts);
    }

    [Fact]
    public void InspectForMigration_TtsPresent_IsLossless()
    {
        var json = """{"models":[],"tts":{"baseUrl":"https://api.siliconflow.cn/v1","apiKeyRef":"k","modelName":"CosyVoice2-0.5B"}}""";
        var source = ProvidersFileModel.InspectForMigration(json);
        Assert.True(source.IsLossless);
        Assert.NotNull(source.Providers.Tts);
    }

    [Fact]
    public void InspectForMigration_InvalidTts_IsNotLossless()
    {
        // tts 段存在但缺必需字段 → 归一化丢弃 → 迁移判定 lossless=false（调用方需提示）
        var json = """{"models":[],"tts":{"baseUrl":"","apiKeyRef":"k","modelName":"m"}}""";
        var source = ProvidersFileModel.InspectForMigration(json);
        Assert.False(source.IsLossless);
        Assert.Null(source.Providers.Tts);
    }

    [Fact]
    public void Serialize_RoundTripsTtsConfig()
    {
        var file = new ProvidersFileModel
        {
            Tts = new TtsEndpointConfig("https://fishaudio.org/v1", "fish-key", "fishaudio-s21pro-flash", "00a1b221"),
        };
        var json = ProvidersFileModel.Serialize(file);
        var back = ProvidersFileModel.Deserialize(json);
        Assert.NotNull(back.Tts);
        Assert.Equal("https://fishaudio.org/v1", back.Tts!.BaseUrl);
        Assert.Equal("00a1b221", back.Tts.Voice);
    }

    [Fact]
    public void Deserialize_LegacyJsonWithoutTts_IsNull()
    {
        var json = """{"models":[]}""";
        Assert.Null(ProvidersFileModel.Deserialize(json).Tts);
    }
}

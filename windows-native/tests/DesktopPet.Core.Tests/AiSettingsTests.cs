using System.Text.Json;
using DesktopPet.Core.Storage;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 6a：AI 设置 Phase 6 扩展——陪伴功能独立开关组。
/// 开关层级：AI 总开关（Enabled）→ 各功能独立开关，全部集中在设置页 AI 助手页。
/// </summary>
public class AiSettingsTests
{
    [Fact]
    public void Defaults_Phase6Switches_HaveDocumentedValues()
    {
        var d = AiSettings.Defaults;
        Assert.True(d.MemoryEnabled);       // 记忆开关默认开
        Assert.True(d.ActiveInteraction);   // 主动互动开关默认开
        Assert.Equal("medium", d.InteractionFrequency); // 频率档默认中
        Assert.True(d.ScreenAwareness);     // 屏幕感知默认开
        Assert.True(d.IntimacyEnabled);     // 亲密度默认开
        Assert.True(d.DailySummary);        // 每日总结默认开
        Assert.False(d.SummaryImage);       // 总结图默认关（云端费用+隐私）
        Assert.False(d.TtsEnabled);         // 语音默认关（不打扰）
        Assert.False(d.AllReply);           // 全员回应默认关
    }

    [Fact]
    public void Normalize_InvalidFrequency_FallsBackToMedium()
    {
        var raw = AiSettings.Defaults with { InteractionFrequency = "every-minute" };
        Assert.Equal("medium", AiSettings.Normalize(raw).InteractionFrequency);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("high")]
    public void Normalize_KeepsValidFrequencies(string frequency)
    {
        var raw = AiSettings.Defaults with { InteractionFrequency = frequency };
        Assert.Equal(frequency, AiSettings.Normalize(raw).InteractionFrequency);
    }

    [Fact]
    public void Deserialize_Phase5Json_MissingPhase6Fields_GetsDefaults()
    {
        // Phase 5 旧 app-settings.json 的 ai 段（无 Phase 6 字段）
        const string json = """
        {
          "enabled": true,
          "screenAnalysis": true,
          "outputMode": "danmaku",
          "screenContextEnabled": false,
          "providerId": "openai-default"
        }
        """;
        var ai = JsonSerializer.Deserialize<AiSettings>(json, TestJsonOptions)!;
        Assert.True(ai.Enabled);
        Assert.Equal("danmaku", ai.OutputMode);
        Assert.True(ai.MemoryEnabled);       // 缺失 → 默认开
        Assert.True(ai.ActiveInteraction);   // 缺失 → 默认开
        Assert.Equal("medium", ai.InteractionFrequency);
        Assert.True(ai.ScreenAwareness);
        Assert.True(ai.IntimacyEnabled);
        Assert.True(ai.DailySummary);
        Assert.False(ai.SummaryImage);
        Assert.False(ai.TtsEnabled);
        Assert.False(ai.AllReply);
    }

    [Fact]
    public void Deserialize_ExplicitFalse_IsNotOverriddenByDefaults()
    {
        const string json = """
        {
          "enabled": true,
          "screenAnalysis": false,
          "outputMode": "chat",
          "screenContextEnabled": false,
          "providerId": "",
          "memoryEnabled": false,
          "activeInteraction": false,
          "interactionFrequency": "low",
          "screenAwareness": false,
          "intimacyEnabled": false,
          "dailySummary": false,
          "summaryImage": true,
          "ttsEnabled": true,
          "allReply": true
        }
        """;
        var ai = JsonSerializer.Deserialize<AiSettings>(json, TestJsonOptions)!;
        Assert.False(ai.MemoryEnabled);      // 显式关 → 保留
        Assert.False(ai.ActiveInteraction);
        Assert.Equal("low", ai.InteractionFrequency);
        Assert.False(ai.ScreenAwareness);
        Assert.False(ai.IntimacyEnabled);
        Assert.False(ai.DailySummary);
        Assert.True(ai.SummaryImage);
        Assert.True(ai.TtsEnabled);
        Assert.True(ai.AllReply);
    }

    [Fact]
    public void Serialize_Deserialize_RoundtripsAllPhase6Fields()
    {
        var ai = AiSettings.Defaults with
        {
            MemoryEnabled = false,
            ActiveInteraction = false,
            InteractionFrequency = "high",
            ScreenAwareness = false,
            IntimacyEnabled = false,
            DailySummary = false,
            SummaryImage = true,
            TtsEnabled = true,
            AllReply = true,
            Onboarded = true,
        };
        var json = JsonSerializer.Serialize(ai, TestJsonOptions);
        var back = JsonSerializer.Deserialize<AiSettings>(json, TestJsonOptions)!;
        Assert.Equal(ai, back);
    }

    [Fact]
    public void NullAi_Normalize_ReturnsDefaults()
    {
        Assert.Equal(AiSettings.Defaults, AiSettings.Normalize(null));
    }

    private static readonly JsonSerializerOptions TestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

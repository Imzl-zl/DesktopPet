using DesktopPet.Core.Tts;

namespace DesktopPet.Core.Tests;

/// <summary>
/// TTS 契约测试（架构文档 windows-tts-design.md §4）。
/// ITtsProvider 契约下沉 Core：引擎无关的请求/音色模型，保证 Core 零 IO 零 UI 可单测。
/// </summary>
public class TtsContractsTests
{
    [Fact]
    public void SynthesisRequest_DefaultsToNormalSpeed()
    {
        var req = new TtsSynthesisRequest(Text: "你好", VoiceId: "zh-CN-XiaoxiaoNeural");
        Assert.Equal(100, req.SpeedPercent);
    }

    [Fact]
    public void SynthesisRequest_EmptyVoiceMeansEngineDefault()
    {
        var req = new TtsSynthesisRequest(Text: "你好", VoiceId: "");
        Assert.Equal("", req.VoiceId);
    }

    [Fact]
    public void VoiceInfo_CarriesEngineAgnosticFields()
    {
        var info = new TtsVoiceInfo(Id: "zh-CN-XiaoxiaoNeural", DisplayName: "晓晓", Language: "zh-CN", Gender: "female");
        Assert.Equal("zh-CN-XiaoxiaoNeural", info.Id);
        Assert.Equal("晓晓", info.DisplayName);
        Assert.Equal("zh-CN", info.Language);
        Assert.Equal("female", info.Gender);
    }
}

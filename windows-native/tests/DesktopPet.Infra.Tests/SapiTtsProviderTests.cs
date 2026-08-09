using System.Runtime.Versioning;
using DesktopPet.Core.Tts;
using DesktopPet.Infra.Tts;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// SAPI 离线 TTS 适配新契约（windows-tts-design.md §6.1）。
/// 依赖系统 zh-CN 语音；无语音时回退默认。真机路径：枚举/精确选中/语言回退/合成 WAV。
/// </summary>
[SupportedOSPlatform("windows")]
public class SapiTtsProviderTests
{
    [Fact]
    public async Task SynthesizeAsync_ReturnsWavStream()
    {
        var provider = new SapiTtsProvider();
        using var stream = await provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语音测试", VoiceId: "zh-CN-XiaoxiaoNeural"), CancellationToken.None);
        var bytes = ((MemoryStream)stream).ToArray();
        Assert.True(bytes.Length > 100, $"音频过短: {bytes.Length}");
        // RIFF/WAVE 头
        Assert.Equal(0x52, bytes[0]); // R
        Assert.Equal(0x49, bytes[1]); // I
        Assert.Equal(0x46, bytes[2]); // F
        Assert.Equal(0x46, bytes[3]); // F
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyText_Throws()
    {
        var provider = new SapiTtsProvider();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.SynthesizeAsync(new TtsSynthesisRequest(Text: "   ", VoiceId: "x"), CancellationToken.None));
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyVoice_FallsBackToSystemDefault()
    {
        // 空 VoiceId = 自动（跟随界面语言语义由上层解析）；Provider 必须兜底不抛
        var provider = new SapiTtsProvider();
        using var stream = await provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语音测试", VoiceId: ""), CancellationToken.None);
        var bytes = ((MemoryStream)stream).ToArray();
        Assert.True(bytes.Length > 100, $"音频过短: {bytes.Length}");
        Assert.Equal(0x52, bytes[0]);
    }

    [Fact]
    public async Task SynthesizeAsync_UnknownVoice_FallsBackByLanguage()
    {
        // SAPI 不存在的声音名 → 语言回退（SelectVoice 失败 → SelectVoiceByHints）；不应抛
        var provider = new SapiTtsProvider();
        using var stream = await provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语音测试", VoiceId: "zh-CN-XiaoxiaoNeural"), CancellationToken.None);
        var bytes = ((MemoryStream)stream).ToArray();
        Assert.True(bytes.Length > 100, $"音频过短: {bytes.Length}");
    }

    [Fact]
    public async Task SynthesizeAsync_SpeedMapping_DoesNotThrow()
    {
        // 语速 50/150 映射到 SAPI Rate（-10..10），不能抛
        var provider = new SapiTtsProvider();
        using var slow = await provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语音测试", VoiceId: "", SpeedPercent: 50), CancellationToken.None);
        using var fast = await provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语音测试", VoiceId: "", SpeedPercent: 150), CancellationToken.None);
        Assert.True(((MemoryStream)slow).ToArray().Length > 100);
        Assert.True(((MemoryStream)fast).ToArray().Length > 100);
    }

    [Fact]
    public async Task SynthesizeAsync_MaxSpeed200_ClampsRate_DoesNotThrow()
    {
        // 回归：200% 映射 Rate=+20 超出 SAPI 合法范围（-10..+10）抛 ArgumentOutOfRangeException（审查实证）；
        // 必须 clamp 到 +10 不抛。滑条上限 200% 用户可达。
        var provider = new SapiTtsProvider();
        using var stream = await provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语音测试", VoiceId: "", SpeedPercent: 200), CancellationToken.None);
        Assert.True(((MemoryStream)stream).ToArray().Length > 100);
    }

    [Fact]
    public void ListVoicesAsync_ReturnsInstalledVoices()
    {
        // 真机枚举：至少应返回一个可用语音（系统必有默认）
        var voices = SapiTtsProvider.GetInstalledVoices();
        Assert.NotEmpty(voices);
        Assert.All(voices, v => Assert.False(string.IsNullOrWhiteSpace(v.Name)));
    }

    [Fact]
    public void Provider_IsOffline()
    {
        Assert.False(new SapiTtsProvider().RequiresNetwork);
        Assert.Equal("sapi", new SapiTtsProvider().Id);
    }
}

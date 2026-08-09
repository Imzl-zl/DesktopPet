using System.IO;
using DesktopPet.App.Tts;
using DesktopPet.Core.Tts;

namespace DesktopPet.App.Tests;

/// <summary>
/// OneCore TTS（Windows.Media.SpeechSynthesis，windows-tts-design.md §6.2）。
/// 真机路径：枚举系统 OneCore 语音（含自然语音）/ 合成 WAV / 语速（SSML prosody）。
/// 注意：CI 无 Windows 语音时 ListVoices 可能为空，合成走系统默认语音兜底。
/// </summary>
public class OneCoreTtsProviderTests
{
    private static readonly OneCoreTtsProvider Provider = new();

    [Fact]
    public void Provider_Metadata_IsOfflineOneCore()
    {
        Assert.False(Provider.RequiresNetwork);
        Assert.Equal("onecore", Provider.Id);
    }

    [Fact]
    public async Task ListVoices_ReturnsInstalledVoices()
    {
        var voices = await Provider.ListVoicesAsync(CancellationToken.None);
        // 系统必有默认语音（无任何语音时 Windows 也会注册一个）；这里允许空但至少不抛
        Assert.All(voices, v =>
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Id));
            Assert.False(string.IsNullOrWhiteSpace(v.DisplayName));
        });
    }

    [Fact]
    public async Task SynthesizeAsync_ReturnsWavStream()
    {
        using var stream = await Provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语音测试", VoiceId: ""), CancellationToken.None);
        var bytes = ((MemoryStream)stream).ToArray();
        Assert.True(bytes.Length > 100, $"音频过短: {bytes.Length}");
        // RIFF/WAVE 头（OneCore 默认输出 WAV）
        Assert.Equal(0x52, bytes[0]); // R
        Assert.Equal(0x49, bytes[1]); // I
        Assert.Equal(0x46, bytes[2]); // F
        Assert.Equal(0x46, bytes[3]); // F
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyText_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Provider.SynthesizeAsync(new TtsSynthesisRequest(Text: "   ", VoiceId: ""), CancellationToken.None));
    }

    [Fact]
    public async Task SynthesizeAsync_SpeedVariants_DoNotThrow()
    {
        using var slow = await Provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语速测试", VoiceId: "", SpeedPercent: 50), CancellationToken.None);
        using var fast = await Provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语速测试", VoiceId: "", SpeedPercent: 150), CancellationToken.None);
        Assert.True(((MemoryStream)slow).ToArray().Length > 100);
        Assert.True(((MemoryStream)fast).ToArray().Length > 100);
    }

    [Fact]
    public async Task SynthesizeAsync_UnknownVoice_FallsBackToDefault()
    {
        // 不存在的语音 Id → 系统默认语音，不抛
        using var stream = await Provider.SynthesizeAsync(
            new TtsSynthesisRequest(Text: "语音测试", VoiceId: "zh-CN-NotARealVoiceNeural"), CancellationToken.None);
        Assert.True(((MemoryStream)stream).ToArray().Length > 100);
    }
}

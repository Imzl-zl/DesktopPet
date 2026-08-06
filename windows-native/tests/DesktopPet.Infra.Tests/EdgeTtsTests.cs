using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using DesktopPet.Infra.Tts;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// Phase 6g：Edge TTS 语音输出（feature-research P1 ⑦；架构文档 §3.2）。
/// Edge TTS 免费协议（speech.platform.bing.com readaloud）：speech.config + SSML → audio 帧 → turn.end。
/// 对话模式可朗读（默认关），弹幕模式不朗读（App 层控制）。
/// </summary>
public class EdgeTtsTests
{
    private static readonly TtsVoice Xiaoxiao = new("zh-CN-XiaoxiaoNeural", "zh-CN");

    // ---- SSML 构建 ----

    [Fact]
    public void SsmlBuilder_EscapesXmlCharacters()
    {
        var ssml = EdgeTtsProtocol.BuildSsmlMessage("a & b <c> \"d\"", Xiaoxiao, "req-1");
        Assert.Contains("a &amp; b &lt;c&gt; \"d\"", ssml);
    }

    [Fact]
    public void SsmlBuilder_ContainsVoiceAndText()
    {
        var ssml = EdgeTtsProtocol.BuildSsmlMessage("今天也要加油哦", Xiaoxiao, "req-2");
        Assert.Contains("zh-CN-XiaoxiaoNeural", ssml);
        Assert.Contains("今天也要加油哦", ssml);
        Assert.Contains("zh-CN", ssml);
    }

    [Fact]
    public void SpeechConfig_SpecifiesMp3Output()
    {
        var config = EdgeTtsProtocol.BuildSpeechConfigMessage();
        Assert.Contains("audio-24khz-48kbitrate-mono-mp3", config);
        Assert.Contains("speech.config", config);
    }

    // ---- 帧解析 ----

    [Fact]
    public void FrameParser_ExtractsAudioFrame()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var frame = Encoding.UTF8.GetBytes("X-RequestId:r\r\nContent-Type:audio/mpeg\r\nPath:audio\r\n\r\n")
            .Concat(payload).ToArray();
        Assert.True(EdgeTtsProtocol.TryParseFrame(frame, out var path, out var data));
        Assert.Equal("audio", path);
        Assert.Equal(payload, data.ToArray());
    }

    [Fact]
    public void FrameParser_DetectsTurnEnd()
    {
        var frame = Encoding.UTF8.GetBytes("Path:turn.end\r\n\r\n");
        Assert.True(EdgeTtsProtocol.TryParseFrame(frame, out var path, out _));
        Assert.Equal("turn.end", path);
    }

    [Fact]
    public void FrameParser_MalformedFrame_ReturnsFalse()
    {
        Assert.False(EdgeTtsProtocol.TryParseFrame(Encoding.UTF8.GetBytes("no separator here"), out _, out _));
    }

    // ---- Provider 流程 ----

    [Fact]
    public void GenerateSecMsGec_IsDeterministicForSameWindow()
    {
        var t = new DateTime(2026, 8, 5, 12, 31, 0, DateTimeKind.Utc);
        var a = EdgeTtsProtocol.GenerateSecMsGec(t);
        var b = EdgeTtsProtocol.GenerateSecMsGec(t.AddMinutes(3)); // 12:34 仍在同一 5 分钟窗口（12:30-12:35）
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // SHA256 大写 hex
        Assert.All(a, ch => Assert.True(char.IsAsciiHexDigitUpper(ch) || char.IsAsciiHexDigit(ch)));
    }

    [Fact]
    public void GenerateSecMsGec_ChangesAcrossWindows()
    {
        var t = new DateTime(2026, 8, 5, 12, 34, 56, DateTimeKind.Utc);
        Assert.NotEqual(
            EdgeTtsProtocol.GenerateSecMsGec(t),
            EdgeTtsProtocol.GenerateSecMsGec(t.AddMinutes(6))); // 跨窗口
    }

    [Fact]
    public void BuildEndpoint_IncludesSecurityParams()
    {
        var uri = EdgeTtsProtocol.BuildEndpoint(new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc));
        Assert.Contains("Sec-MS-GEC=", uri.Query);
        Assert.Contains("Sec-MS-GEC-Version=1-143.0.3650.75", uri.Query);
        Assert.Contains("TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4", uri.Query);
        Assert.Contains("ConnectionId=", uri.Query);
    }

    private sealed class FakeEdgeSocket : IEdgeSocket
    {
        public Uri? ConnectedUri { get; private set; }
        public List<string> SentMessages { get; } = [];
        private readonly Queue<byte[]> _responses = new();

        public FakeEdgeSocket(params byte[][] responses) { foreach (var r in responses) _responses.Enqueue(r); }

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            ConnectedUri = uri;
            return Task.CompletedTask;
        }

        public Task SendAsync(string message, CancellationToken ct)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReceiveAsync(CancellationToken ct)
        {
            if (_responses.Count == 0) throw new EndOfStreamException("对端关闭");
            return Task.FromResult(_responses.Dequeue());
        }

        public void Dispose() { }
    }

    private static byte[] AudioFrame(byte[] audio) => Encoding.UTF8.GetBytes("Path:audio\r\n\r\n").Concat(audio).ToArray();
    private static byte[] TurnEndFrame() => Encoding.UTF8.GetBytes("Path:turn.end\r\n\r\n");

    [Fact]
    public async Task SynthesizeAsync_SendsConfigThenSsml_CollectsAudioUntilTurnEnd()
    {
        var audio = new byte[] { 9, 8, 7, 6, 5 };
        var socket = new FakeEdgeSocket(AudioFrame([1, 2]), AudioFrame(audio), TurnEndFrame());
        var provider = new EdgeTtsProvider(() => socket);

        using var stream = await provider.SynthesizeAsync("辛苦了", Xiaoxiao, CancellationToken.None);

        // 两条消息：speech.config + ssml
        Assert.Equal(2, socket.SentMessages.Count);
        Assert.Contains("speech.config", socket.SentMessages[0]);
        Assert.Contains("ssml", socket.SentMessages[1]);
        Assert.Contains("辛苦了", socket.SentMessages[1]);
        // 音频拼装：1,2 + 9,8,7,6,5
        var bytes = ((MemoryStream)stream).ToArray();
        Assert.Equal(new byte[] { 1, 2, 9, 8, 7, 6, 5 }, bytes);
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyText_Throws()
    {
        var provider = new EdgeTtsProvider(() => new FakeEdgeSocket());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.SynthesizeAsync("   ", Xiaoxiao, CancellationToken.None));
    }

    [Fact]
    public async Task SynthesizeAsync_ServerClosesMidway_Throws()
    {
        var socket = new FakeEdgeSocket(AudioFrame([1]));
        var provider = new EdgeTtsProvider(() => socket);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            provider.SynthesizeAsync("hi", Xiaoxiao, CancellationToken.None));
    }
}

/// <summary>
/// Phase 6g：SAPI 离线 TTS（默认语音实现——Edge 端点对 SChannel 风控，见 EdgeTtsProvider 注释）。
/// 依赖系统 zh-CN 语音；无语音时回退默认（断言仅 WAV 结构）。
/// </summary>
[SupportedOSPlatform("windows")]
public class SapiTtsProviderTests
{
    [Fact]
    public async Task SynthesizeAsync_ReturnsWavStream()
    {
        var provider = new SapiTtsProvider();
        using var stream = await provider.SynthesizeAsync(
            "语音测试", new TtsVoice("zh-CN-XiaoxiaoNeural", "zh-CN"), CancellationToken.None);
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
            provider.SynthesizeAsync("   ", new TtsVoice("x", "zh-CN"), CancellationToken.None));
    }
}

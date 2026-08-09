using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Xml;
using DesktopPet.Core.Tts;
using Windows.Media.SpeechSynthesis;

namespace DesktopPet.App.Tts;

/// <summary>
/// OneCore TTS（Windows.Media.SpeechSynthesis，windows-tts-design.md §6.2）。
/// 系统 OneCore 语音池（Win10 1803+）：SAPI 5 枚举不到，只能走本 Provider——
/// 用户安装「自然语音」（系统设置 → 时间和语言 → 语音）后音质直接升级为 Neural。
/// 零网络零依赖；输出 WAV。语音选择按 Id 精确；语速经 SSML prosody rate（50-200%）。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class OneCoreTtsProvider : ITtsProvider
{
    public string Id => "onecore";
    public bool RequiresNetwork => false;

    public Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var voices = SpeechSynthesizer.AllVoices
            .Select(v => new TtsVoiceInfo(
                Id: v.Id,
                DisplayName: v.DisplayName,
                Language: v.Language,
                Gender: v.Gender.ToString().ToLowerInvariant()))
            .ToList();
        return Task.FromResult<IReadOnlyList<TtsVoiceInfo>>(voices);
    }

    public async Task<Stream> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct)
    {
        var trimmed = request.Text?.Trim() ?? "";
        if (trimmed.Length == 0) throw new ArgumentException("语音文本不能为空", nameof(request.Text));

        using var synth = new SpeechSynthesizer();
        var target = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == request.VoiceId);
        if (target is not null) synth.Voice = target;

        // 语速经 SSML prosody rate（percent 语义）：OneCore 直接 API 无语速，SSML 是唯一入口
        var speed = Math.Clamp(request.SpeedPercent, 50, 200);
        var lang = target?.Language ?? "zh-CN"; // xml:lang 跟随选中语音，避免 SSML 语言与语音不符
        var ssml = BuildSsml(trimmed, speed, lang);

        using var stream = await synth.SynthesizeSsmlToStreamAsync(ssml).AsTask(ct);
        var ms = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    /// <summary>SSML 包装：文本 XML 转义 + prosody rate（如 "120%"）+ 语音语言。</summary>
    internal static string BuildSsml(string text, int speedPercent, string language = "zh-CN")
    {
        var sb = new StringBuilder();
        using var writer = XmlWriter.Create(sb, new XmlWriterSettings { ConformanceLevel = ConformanceLevel.Fragment });
        writer.WriteStartElement("speak", "http://www.w3.org/2001/10/synthesis");
        writer.WriteAttributeString("version", "1.0");
        writer.WriteAttributeString("xml", "lang", null, language);
        writer.WriteStartElement("prosody");
        writer.WriteAttributeString("rate", $"{speedPercent}%");
        writer.WriteString(text);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.Flush();
        return sb.ToString();
    }
}

using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Synthesis;

namespace DesktopPet.Infra.Tts;

/// <summary>
/// Windows SAPI 离线 TTS（架构文档 §3.2 的 SapiTtsProvider 选项）。
/// 零网络零依赖、隐私友好；中文系统自带 zh-CN 语音。输出 WAV（非 MP3）。
/// 注意：合成中途不可取消（synth.Speak 为同步阻塞；ct 仅在开始前检查）。
/// 默认实现理由：Edge TTS 免费端点对 Windows SChannel 的 TLS 指纹风控
/// （OpenSSL 路径可用但生产环境不可依赖；EdgeTtsProvider 保留作未来 OpenSSL 集成基础）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SapiTtsProvider : ITtsProvider
{
    public async Task<Stream> SynthesizeAsync(string text, TtsVoice voice, CancellationToken ct)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) throw new ArgumentException("语音文本不能为空", nameof(text));

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var stream = new MemoryStream();
            using var synth = new SpeechSynthesizer();
            synth.SetOutputToWaveStream(stream);
            // 优先按 voice.Name 选择；失败回退语言匹配；再失败用系统默认
            try
            {
                synth.SelectVoice(voice.Name);
            }
            catch (Exception)
            {
                try
                {
                    synth.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, new CultureInfo(voice.Language));
                }
                catch (Exception)
                {
                    // 系统默认语音（不抛：无匹配语音也应有默认）
                }
            }
            synth.Speak(trimmed);
            synth.SetOutputToNull();
            stream.Position = 0;
            return (Stream)stream;
        }, ct).ConfigureAwait(false);
    }
}

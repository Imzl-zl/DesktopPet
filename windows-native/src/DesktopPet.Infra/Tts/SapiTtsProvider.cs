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
/// 语音选择语义：voice.Name 为 SAPI 语音名（如 "Microsoft Huihui Desktop"）时精确选中；
/// 为 Edge 风格名/空串时 SelectVoice 失败 → 按 voice.Language 语言回退；再失败用系统默认。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SapiTtsProvider : ITtsProvider
{
    /// <summary>系统已安装且可用的 SAPI 语音（Enabled=true，官方文档：不可选择 Enabled=false 的语音）。</summary>
    public static IReadOnlyList<SapiVoiceInfo> GetInstalledVoices()
    {
        using var synth = new SpeechSynthesizer();
        var list = new List<SapiVoiceInfo>();
        foreach (var voice in synth.GetInstalledVoices())
        {
            if (!voice.Enabled) continue;
            var info = voice.VoiceInfo;
            var gender = info.Gender switch
            {
                VoiceGender.Female => "female",
                VoiceGender.Male => "male",
                _ => "",
            };
            list.Add(new SapiVoiceInfo(info.Name, info.Culture?.Name ?? "", gender));
        }
        return list;
    }

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
                if (TryParseCulture(voice.Language) is { } culture)
                {
                    try
                    {
                        synth.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, culture);
                    }
                    catch (Exception)
                    {
                        // 系统默认语音（不抛：无匹配语音也应有默认）
                    }
                }
            }
            synth.Speak(trimmed);
            synth.SetOutputToNull();
            stream.Position = 0;
            return (Stream)stream;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>解析语言标签；非法（如 SAPI 语音名被误截的前缀）返回 null，跳过语言回退。</summary>
    private static CultureInfo? TryParseCulture(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        try { return CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { return null; }
    }
}

/// <summary>SAPI 已安装语音的展示信息（设置页枚举用）。</summary>
public sealed record SapiVoiceInfo(string Name, string Language, string Gender);

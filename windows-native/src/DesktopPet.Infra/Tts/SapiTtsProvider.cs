using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using DesktopPet.Core.Tts;

namespace DesktopPet.Infra.Tts;

/// <summary>
/// Windows SAPI 离线 TTS（windows-tts-design.md §6.1，默认兜底引擎）。
/// 零网络零依赖、隐私友好；中文系统自带 zh-CN 语音。输出 WAV（非 MP3）。
/// 注意：合成中途不可取消（synth.Speak 为同步阻塞；ct 仅在开始前检查）。
/// 语音选择语义：voice.Name 为 SAPI 语音名（如 "Microsoft Huihui Desktop"）时精确选中；
/// 为 Edge 风格名/空串时 SelectVoice 失败 → 按 voice.Language 语言回退；再失败用系统默认。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SapiTtsProvider : ITtsProvider
{
    public string Id => "sapi";
    public bool RequiresNetwork => false;

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

    public Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TtsVoiceInfo>>(
            GetInstalledVoices()
                .Select(v => new TtsVoiceInfo(v.Name, v.Name, v.Language, v.Gender))
                .ToList());
    }

    public async Task<Stream> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct)
    {
        var trimmed = request.Text?.Trim() ?? "";
        if (trimmed.Length == 0) throw new ArgumentException("语音文本不能为空", nameof(request.Text));

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var stream = new MemoryStream();
            using var synth = new SpeechSynthesizer();
            synth.SetOutputToWaveStream(stream);
            // 语速映射：SpeedPercent 100 → Rate 0；50 → -10；200 → +10（线性，超界 clamp）
            // SAPI Rate 合法范围 -10..+10（超出抛 ArgumentOutOfRangeException，实测确认）
            synth.Rate = Math.Clamp((int)Math.Round((request.SpeedPercent - 100) / 5.0), -10, 10);
            // 优先按 voice.Name 选择；失败回退语言匹配；再失败用系统默认
            try
            {
                synth.SelectVoice(request.VoiceId);
            }
            catch (Exception)
            {
                if (TryParseCulture(VoiceLanguageHint(request.VoiceId)) is { } culture)
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

    /// <summary>从声音名推导语言标签：Edge 风格名（zh-CN-XiaoxiaoNeural）→ "zh-CN"；SAPI 名返回 null（由 Culture 解析失败自然跳过）。</summary>
    private static string? VoiceLanguageHint(string voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName)) return null;
        // Edge 风格名 = 语言标签 + 名字（如 zh-CN-XiaoxiaoNeural / en-US-JennyNeural）
        if (voiceName.Length >= 5 && voiceName[2] == '-' && voiceName[5] == '-')
            return voiceName[..5];
        return voiceName; // SAPI 名（"Microsoft Huihui Desktop"）交给 TryParseCulture，失败即跳过
    }

    /// <summary>解析语言标签；非法（如 SAPI 语音名被误截的前缀）返回 null，跳过语言回退。</summary>
    private static CultureInfo? TryParseCulture(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        try { return CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { return null; }
    }
}

/// <summary>SAPI 已安装语音的展示信息（设置页枚举用）。</summary>
public sealed record SapiVoiceInfo(string Name, string Language, string Gender);

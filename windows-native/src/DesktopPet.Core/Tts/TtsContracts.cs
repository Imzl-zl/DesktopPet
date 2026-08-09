using System.Text.Json.Serialization;

namespace DesktopPet.Core.Tts;

/// <summary>音色信息（引擎无关的展示模型；windows-tts-design.md §4.1）。</summary>
public sealed record TtsVoiceInfo(
    string Id,          // 引擎内唯一标识：SAPI=VoiceInfo.Name / OneCore=Voice.Id / 端点=voices[].id
    string DisplayName,
    string Language,    // 如 zh-CN（空 = 未知）
    string Gender = ""); // male | female | ""（未知）

/// <summary>合成请求（引擎无关；windows-tts-design.md §4.1）。</summary>
public sealed record TtsSynthesisRequest(
    string Text,
    string VoiceId,           // 空 = 引擎默认（按界面语言自动）
    int SpeedPercent = 100);  // 50-200，各引擎内部换算（SAPI Rate / SSML prosody / 端点 speed）

/// <summary>TTS Provider 不可用（如端点风控/未配置）：上层降级到默认引擎。</summary>
public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException(string message) : base(message) { }
}

/// <summary>
/// TTS Provider 契约（下沉 Core，对齐 IModelProvider/IImageProvider）。
/// 实现：SapiTtsProvider（默认，离线）/ OneCoreTtsProvider（App，系统自然语音）/
///       OpenAiCompatibleTtsProvider（在线端点，用户自配）。
/// 完整设计见 docs/windows-tts-design.md。
/// </summary>
public interface ITtsProvider
{
    string Id { get; }                       // "sapi" | "onecore" | "openai"
    bool RequiresNetwork { get; }            // 在线端点=true；AI 总开关关闭时禁用

    /// <summary>枚举可用音色（设置页下拉 + 试听用；端点不支持列表时返回空）。</summary>
    Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct);

    /// <summary>合成语音，返回音频流（SAPI/OneCore=WAV；端点=端点决定，多为 MP3）。</summary>
    Task<Stream> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct);
}

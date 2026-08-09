namespace DesktopPet.Core.Tts;

/// <summary>
/// TTS Provider 注册表（windows-tts-design.md §4.2）：引擎选择 + 音色解析/回退。
/// 纯逻辑：引擎不存在回退默认；音色空/失效回退「自动」（按界面语言匹配）。
/// 界面语言用 I18nService.AppLang 的语义（en/zhHans/zhHant/vi）。
/// </summary>
public static class TtsProviderRegistry
{
    public const string DefaultProviderId = "sapi";

    /// <summary>按 id 选引擎；未知/空 id 回退默认引擎；默认也不存在时取第一个可用。</summary>
    public static ITtsProvider ResolveProvider(IReadOnlyList<ITtsProvider> available, string providerId)
    {
        var exact = available.FirstOrDefault(p => p.Id == providerId);
        if (exact is not null) return exact;
        var fallback = available.FirstOrDefault(p => p.Id == DefaultProviderId);
        return fallback ?? available.First();
    }

    /// <summary>
    /// 音色解析：VoiceId 精确命中 → 返回；否则（空/失效）按界面语言匹配，无语言匹配取第一个；空列表返回 null（引擎默认）。
    /// 语义：返回 null 表示「用引擎默认」，非错误。
    /// </summary>
    public static TtsVoiceInfo? ResolveVoice(IReadOnlyList<TtsVoiceInfo> voices, string voiceId, string uiLang)
    {
        if (voices.Count == 0) return null;
        if (!string.IsNullOrEmpty(voiceId))
        {
            var exact = voices.FirstOrDefault(v => v.Id == voiceId);
            if (exact is not null) return exact;
        }
        var language = UiLangToBcp47(uiLang);
        if (language is not null)
        {
            var match = voices.FirstOrDefault(v =>
                v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return voices[0];
    }

    /// <summary>界面语言 → BCP-47 前缀（用于音色语言匹配）。未知返回 null。</summary>
    private static string? UiLangToBcp47(string uiLang) => uiLang switch
    {
        "en" => "en",
        "zhHans" => "zh-CN",
        "zhHant" => "zh-TW",
        "vi" => "vi",
        _ => null,
    };
}

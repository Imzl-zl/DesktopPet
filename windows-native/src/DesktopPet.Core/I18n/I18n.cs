using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DesktopPet.Core.I18n;

public enum AppLang
{
    En,
    ZhHans,
    ZhHant,
    Vi,
}

/// <summary>
/// 客户端 i18n（对齐 windows/src/i18n.ts）：字符串以英文文本为 key，
/// t() 对英文返回 key 本身；自动检测系统语言并记住选择。
/// "DesktopPet" 永不翻译。
/// </summary>
public sealed class I18nService
{
    private static readonly Dictionary<AppLang, Dictionary<string, string>> Dictionaries = LoadDictionaries();

    public AppLang Lang { get; private set; } = AppLang.En;

    /// <summary>系统语言检测（对齐 i18n.ts detect()）。</summary>
    public static AppLang Detect()
    {
        var name = CultureInfo.CurrentCulture.Name.ToLowerInvariant();
        if (name.StartsWith("zh"))
        {
            return name.Contains("hant") || name.Contains("tw") || name.Contains("hk") || name.Contains("mo")
                ? AppLang.ZhHant
                : AppLang.ZhHans;
        }
        if (name.StartsWith("vi")) return AppLang.Vi;
        return AppLang.En;
    }

    public I18nService(AppLang lang = AppLang.En)
    {
        Lang = lang;
    }

    /// <summary>切换语言并返回是否变化。</summary>
    public bool SetLang(AppLang lang)
    {
        if (Lang == lang) return false;
        Lang = lang;
        return true;
    }

    /// <summary>翻译：en 返回 key 本身；其他语言查字典，缺失回退 key。</summary>
    public string T(string key)
    {
        if (Lang == AppLang.En) return key;
        return Dictionaries.TryGetValue(Lang, out var dict) && dict.TryGetValue(key, out var value)
            ? value
            : key;
    }

    private static Dictionary<AppLang, Dictionary<string, string>> LoadDictionaries()
    {
        var assembly = typeof(I18nService).Assembly;
        var result = new Dictionary<AppLang, Dictionary<string, string>>();
        foreach (var (lang, resourceName) in new[]
        {
            (AppLang.ZhHans, "DesktopPet.Core.Resources.i18n.zh.json"),
            (AppLang.ZhHant, "DesktopPet.Core.Resources.i18n.zh-TW.json"),
            (AppLang.Vi, "DesktopPet.Core.Resources.i18n.vi.json"),
        })
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (entries is not null) result[lang] = entries;
        }
        return result;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Core.Hotkeys;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Roaming;

namespace DesktopPet.Core.Storage;

/// <summary>
/// 应用设置（对齐 Tauri 版 ap_theme/ap_opacity/ap_font_size/ap_font_family/ap_idle/
/// ap_fx/ap_quick_bubbles/ap_quick_bubble_duration/ap_left_click_action/ap_roam/ap_lang）。
/// 归一化与默认值在 Core（可单测），持久化由 IJsonStore 负责。
///
/// 反序列化注意：旧 JSON（Phase 5 及更早）缺失新增字段时，由
/// <see cref="AppSettingsJsonConverter"/> 填文档默认值（而非 0/下限），保证升级后
/// "弹幕字号 30"等默认语义成立；显式写入的值不被覆盖。业务代码只消费
/// Normalize 后的实例（FileJsonStore.LoadSettings 保证）。
/// </summary>
[JsonConverter(typeof(AppSettingsJsonConverter))]
public sealed record AppSettings(
    string Theme,                    // system | light | dark
    int BubbleOpacity,               // 0-100，默认 92
    int FontSize,                    // 默认 12
    string FontFamily,               // system | rounded | mono
    bool ShowIdleChatter,            // ap_idle != "0"
    int IdleChatterIntervalSeconds,  // 闲谈台词重选间隔 5-120s，默认 15
    bool AnimationEnabled,           // 精灵帧动画总开关（关 = 静态显示首帧；漫游/气泡独立）
    bool BobAnimation,               // ap_fx == "1"
    int PetSizePercent,              // 70-130，默认 100
    string LeftClickAction,          // none | self | all
    int QuickBubbleDurationSeconds,  // 1-10，默认 4
    string[] QuickBubblePresets,
    string[] IdleChatterLines,       // 闲谈台词池（每行一句；空数组 = 不显示闲谈）
    string[] HungryLines,            // 饥饿台词池（空数组 = 不显示饥饿台词）
    RoamConfig Roam,
    AppLang Lang,
    AiSettings Ai,                  // Phase 5：AI 设置（旧 JSON 无此字段 → 归一化给默认）
    HotkeySettings Hotkeys = null!, // 全局快捷键；旧 JSON 缺字段时 Normalize 恢复历史默认
    int DanmakuFontSize = 30,       // 弹幕字号 16-48px，默认 30（旧 JSON 缺字段 → converter 填默认）
    int DanmakuSpeedPercent = 100,  // 弹幕速度 50-200%，默认 100
    int DanmakuTrackCount = 10)     // 弹幕轨道数 4-20，默认 10
{
    /// <summary>闲谈台词池默认值（旧 JSON 缺字段时回退；用户清空 [] 则保留空 = 不显示）。</summary>
    public static readonly string[] DefaultIdleChatterLines =
        ["…", "♪", "Zzz…", "(*´∀`*)", "呼~", "盯——"];

    /// <summary>饥饿台词池默认值（同上语义）。</summary>
    public static readonly string[] DefaultHungryLines =
        ["饿了…", "想吃小鱼干~", "好饿哦…", "投喂时间到！"];

    public static AppSettings Defaults(AppLang detectedLang) => new(
        Theme: "system",
        BubbleOpacity: 92,
        FontSize: 12,
        FontFamily: "system",
        ShowIdleChatter: true,
        IdleChatterIntervalSeconds: 15,
        AnimationEnabled: true,
        BobAnimation: false,
        PetSizePercent: 100,
        LeftClickAction: "none",
        QuickBubbleDurationSeconds: 4,
        QuickBubblePresets: ["辛苦了~", "摸摸头", "加油！", "休息一下吧", "盯——", "(*´∀`*)"],
        IdleChatterLines: DefaultIdleChatterLines,
        HungryLines: DefaultHungryLines,
        Roam: new RoamConfig(true, RoamMode.Wander, 5, 1200, 3500),
        Lang: detectedLang,
        Ai: AiSettings.Defaults,
        Hotkeys: HotkeySettings.Defaults,
        DanmakuFontSize: 30,
        DanmakuSpeedPercent: 100,
        DanmakuTrackCount: 10);

    public static AppSettings Normalize(AppSettings raw)
    {
        var theme = raw.Theme switch { "light" => "light", "dark" => "dark", _ => "system" };
        var fontFamily = raw.FontFamily switch { "rounded" => "rounded", "mono" => "mono", _ => "system" };
        var clickAction = raw.LeftClickAction switch { "self" => "self", "all" => "all", _ => "none" };
        var roam = RoamConfigOps.Normalize(raw.Roam);
        return new AppSettings(
            theme,
            Math.Clamp(raw.BubbleOpacity, 0, 100),
            Math.Clamp(raw.FontSize, 8, 24),
            fontFamily,
            raw.ShowIdleChatter,
            Math.Clamp(raw.IdleChatterIntervalSeconds, 5, 120),
            raw.AnimationEnabled,
            raw.BobAnimation,
            Math.Clamp(raw.PetSizePercent, 70, 130),
            clickAction,
            Math.Clamp(raw.QuickBubbleDurationSeconds, 1, 10),
            raw.QuickBubblePresets ?? [],
            // null = 旧数据（无此字段）→ 默认台词；[] = 用户显式清空 → 保留（不显示闲谈）
            raw.IdleChatterLines ?? DefaultIdleChatterLines,
            raw.HungryLines ?? DefaultHungryLines,
            roam,
            raw.Lang,
            AiSettings.Normalize(raw.Ai),
            raw.Hotkeys ?? HotkeySettings.Defaults,
            Math.Clamp(raw.DanmakuFontSize, 16, 48),
            Math.Clamp(raw.DanmakuSpeedPercent, 50, 200),
            Math.Clamp(raw.DanmakuTrackCount, 4, 20));
    }
}

/// <summary>
/// AppSettings 反序列化：JSON 缺失的字段填文档默认值（旧版文件升级兼容），
/// 显式写入的字段原样保留。序列化走 record 默认（全字段输出）。
/// </summary>
public sealed class AppSettingsJsonConverter : JsonConverter<AppSettings>
{
    public override AppSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var defaults = AppSettings.Defaults(I18nService.Detect());
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("AppSettings 必须是 JSON 对象");

        var theme = defaults.Theme;
        var bubbleOpacity = defaults.BubbleOpacity;
        var fontSize = defaults.FontSize;
        var fontFamily = defaults.FontFamily;
        var showIdleChatter = defaults.ShowIdleChatter;
        var idleChatterIntervalSeconds = defaults.IdleChatterIntervalSeconds;
        var animationEnabled = defaults.AnimationEnabled;
        var bobAnimation = defaults.BobAnimation;
        var petSizePercent = defaults.PetSizePercent;
        var leftClickAction = defaults.LeftClickAction;
        var quickBubbleDurationSeconds = defaults.QuickBubbleDurationSeconds;
        var quickBubblePresets = defaults.QuickBubblePresets;
        var idleChatterLines = defaults.IdleChatterLines;
        var hungryLines = defaults.HungryLines;
        var roam = defaults.Roam;
        var lang = defaults.Lang;
        var ai = defaults.Ai;
        var hotkeys = defaults.Hotkeys;
        var danmakuFontSize = defaults.DanmakuFontSize;
        var danmakuSpeedPercent = defaults.DanmakuSpeedPercent;
        var danmakuTrackCount = defaults.DanmakuTrackCount;

        var camel = options.PropertyNamingPolicy ?? JsonNamingPolicy.CamelCase;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("AppSettings 属性名解析失败");
            var name = camel.ConvertName(reader.GetString()!);
            reader.Read();
            switch (name)
            {
                case "theme": theme = ReadString(ref reader) ?? defaults.Theme; break;
                case "bubbleOpacity": bubbleOpacity = ReadInt(ref reader) ?? defaults.BubbleOpacity; break;
                case "fontSize": fontSize = ReadInt(ref reader) ?? defaults.FontSize; break;
                case "fontFamily": fontFamily = ReadString(ref reader) ?? defaults.FontFamily; break;
                case "showIdleChatter": showIdleChatter = ReadBool(ref reader); break;
                case "idleChatterIntervalSeconds": idleChatterIntervalSeconds = ReadInt(ref reader) ?? defaults.IdleChatterIntervalSeconds; break;
                case "animationEnabled": animationEnabled = ReadBool(ref reader); break;
                case "bobAnimation": bobAnimation = ReadBool(ref reader); break;
                case "petSizePercent": petSizePercent = ReadInt(ref reader) ?? defaults.PetSizePercent; break;
                case "leftClickAction": leftClickAction = ReadString(ref reader) ?? defaults.LeftClickAction; break;
                case "quickBubbleDurationSeconds": quickBubbleDurationSeconds = ReadInt(ref reader) ?? defaults.QuickBubbleDurationSeconds; break;
                case "quickBubblePresets": quickBubblePresets = ReadStringArray(ref reader) ?? defaults.QuickBubblePresets; break;
                // null = 旧数据（无此字段）→ 默认台词；[] = 用户显式清空 → 保留（不显示闲谈）
                case "idleChatterLines": idleChatterLines = ReadStringArray(ref reader) ?? defaults.IdleChatterLines; break;
                case "hungryLines": hungryLines = ReadStringArray(ref reader) ?? defaults.HungryLines; break;
                case "roam": roam = reader.TokenType == JsonTokenType.Null
                        ? defaults.Roam
                        : JsonSerializer.Deserialize<RoamConfig>(ref reader, options) ?? defaults.Roam; break;
                case "lang": lang = ParseLang(ReadString(ref reader)) ?? defaults.Lang; break;
                case "ai": ai = reader.TokenType == JsonTokenType.Null
                        ? defaults.Ai
                        : JsonSerializer.Deserialize<AiSettings>(ref reader, options) ?? defaults.Ai; break;
                case "hotkeys": hotkeys = reader.TokenType == JsonTokenType.Null
                        ? null!
                        : JsonSerializer.Deserialize<HotkeySettings>(ref reader, options); break;
                case "danmakuFontSize": danmakuFontSize = ReadInt(ref reader) ?? defaults.DanmakuFontSize; break;
                case "danmakuSpeedPercent": danmakuSpeedPercent = ReadInt(ref reader) ?? defaults.DanmakuSpeedPercent; break;
                case "danmakuTrackCount": danmakuTrackCount = ReadInt(ref reader) ?? defaults.DanmakuTrackCount; break;
                default: reader.Skip(); break; // 未知字段容忍（前向兼容）
            }
        }

        return new AppSettings(
            theme, bubbleOpacity, fontSize, fontFamily, showIdleChatter,
            idleChatterIntervalSeconds, animationEnabled, bobAnimation, petSizePercent,
            leftClickAction, quickBubbleDurationSeconds, quickBubblePresets,
            idleChatterLines, hungryLines, roam, lang, ai, hotkeys,
            danmakuFontSize, danmakuSpeedPercent, danmakuTrackCount);
    }

    public override void Write(Utf8JsonWriter writer, AppSettings value, JsonSerializerOptions options)
    {
        // 类型级 [JsonConverter] 特性优先于 options 集合：Write 内再次 Serialize 同一类型
        // 必然解析回本 converter → 无限递归（StackOverflow）。官方推荐 = 序列化 DTO 投影：
        // 匿名类型无特性 converter，反射输出与默认行为一致（命名策略照常应用）。
        JsonSerializer.Serialize(writer, ToDto(value), options);
    }

    private static object ToDto(AppSettings v) => new
    {
        v.Theme,
        v.BubbleOpacity,
        v.FontSize,
        v.FontFamily,
        v.ShowIdleChatter,
        v.IdleChatterIntervalSeconds,
        v.AnimationEnabled,
        v.BobAnimation,
        v.PetSizePercent,
        v.LeftClickAction,
        v.QuickBubbleDurationSeconds,
        v.QuickBubblePresets,
        v.IdleChatterLines,
        v.HungryLines,
        v.Roam,
        v.Lang,
        v.Ai,
        v.Hotkeys,
        v.DanmakuFontSize,
        v.DanmakuSpeedPercent,
        v.DanmakuTrackCount,
    };

    private static AppLang? ParseLang(string? value) => value switch
    {
        "en" => AppLang.En,
        "zhHans" => AppLang.ZhHans,
        "zhHant" => AppLang.ZhHant,
        "vi" => AppLang.Vi,
        _ => null,
    };

    private static bool ReadBool(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False
            ? reader.GetBoolean()
            : throw new JsonException("AppSettings 布尔字段解析失败");

    private static string? ReadString(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : reader.TokenType == JsonTokenType.Null ? null
            : throw new JsonException("AppSettings 字符串字段解析失败");

    private static int? ReadInt(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)
            ? value
            : throw new JsonException("AppSettings 整数字段解析失败");

    private static string[]? ReadStringArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("AppSettings 数组字段解析失败");
        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("AppSettings 数组元素必须为字符串");
            list.Add(reader.GetString()!);
        }
        return list.ToArray();
    }
}

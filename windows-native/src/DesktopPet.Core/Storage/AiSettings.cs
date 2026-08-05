using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Core.Storage;

/// <summary>
/// AI 设置（Phase 5 + Phase 6；对齐迁移计划 §5）。
/// Phase 5：AI 总开关 / 分析开关 / 输出模式 / 屏幕上下文开关。
/// Phase 6：陪伴功能独立开关组（记忆/主动互动/频率档/屏幕感知/亲密度/每日总结/总结图/语音）。
/// 开关层级：AI 总开关（Enabled）→ 各功能独立开关，全部集中在设置页 AI 助手页。
/// 人格选择不在此处（唯一真值 = personas.json 的 selectedId，见 PersonasFileModel）。
///
/// 反序列化注意：旧 JSON（Phase 5 格式）缺失 Phase 6 字段时，由
/// <see cref="AiSettingsJsonConverter"/> 填默认值（而非 false），保证升级后
/// "记忆开关默认开"等文档默认语义成立；显式写入的值不被覆盖。
/// 业务代码只消费 Normalize 后的实例（FileJsonStore.LoadSettings 保证）。
/// </summary>
[JsonConverter(typeof(AiSettingsJsonConverter))]
public sealed record AiSettings(
    bool Enabled,               // AI 总开关：关 = 纯桌宠（无截屏/无网络/无后台进程）
    bool ScreenAnalysis,        // 截屏分析开关（默认关，隐私优先）
    string OutputMode,          // danmaku | chat | silent（模式只决定 AI 主动输出形式）
    bool ScreenContextEnabled,  // 对话携带屏幕上下文（默认关，隐私）
    string ProviderId,          // 选中的模型 provider id（空 = 未配置）
    bool MemoryEnabled,         // 记忆开关（默认开；关 = 不记录不注入，画像文件不落盘）
    bool ActiveInteraction,     // 主动互动开关（默认开；定时问候 + 事件驱动评论）
    string InteractionFrequency, // 主动互动频率档：low | medium | high（默认 medium）
    bool ScreenAwareness,       // 屏幕感知开关（默认开；关 = 不再从截屏推断活动，定时问候仍可用）
    bool IntimacyEnabled,       // 亲密度开关（默认开；关 = 固定人格基础档）
    bool DailySummary,          // 每日总结开关（默认开）
    bool SummaryImage,          // 总结图开关（默认关——云端费用+隐私，显式开启）
    bool TtsEnabled,            // 语音朗读开关（默认关；仅对话模式朗读，弹幕不朗读）
    bool AllReply)              // 多宠物全员回应（默认关；开 = 同一事件每只宠物都生成）
{
    public const string FrequencyLow = "low";
    public const string FrequencyMedium = "medium";
    public const string FrequencyHigh = "high";

    public static AiSettings Defaults => new(
        Enabled: false,
        ScreenAnalysis: false,
        OutputMode: "silent",
        ScreenContextEnabled: false,
        ProviderId: "",
        MemoryEnabled: true,
        ActiveInteraction: true,
        InteractionFrequency: FrequencyMedium,
        ScreenAwareness: true,
        IntimacyEnabled: true,
        DailySummary: true,
        SummaryImage: false,
        TtsEnabled: false,
        AllReply: false);

    public static AiSettings Normalize(AiSettings? raw)
    {
        if (raw is null) return Defaults;
        var mode = raw.OutputMode switch
        {
            "danmaku" => "danmaku",
            "chat" => "chat",
            _ => "silent",
        };
        var frequency = raw.InteractionFrequency switch
        {
            FrequencyLow => FrequencyLow,
            FrequencyHigh => FrequencyHigh,
            _ => FrequencyMedium,
        };
        return new AiSettings(
            raw.Enabled,
            raw.ScreenAnalysis,
            mode,
            raw.ScreenContextEnabled,
            raw.ProviderId ?? "",
            raw.MemoryEnabled,
            raw.ActiveInteraction,
            frequency,
            raw.ScreenAwareness,
            raw.IntimacyEnabled,
            raw.DailySummary,
            raw.SummaryImage,
            raw.TtsEnabled,
            raw.AllReply);
    }
}

/// <summary>
/// AiSettings 反序列化：JSON 缺失的字段填文档默认值（旧版文件升级兼容），
/// 显式写入的字段原样保留。序列化走 record 默认（全字段输出）。
/// </summary>
public sealed class AiSettingsJsonConverter : JsonConverter<AiSettings>
{
    public override AiSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var defaults = AiSettings.Defaults;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("AiSettings 必须是 JSON 对象");

        var enabled = defaults.Enabled;
        var screenAnalysis = defaults.ScreenAnalysis;
        var outputMode = defaults.OutputMode;
        var screenContextEnabled = defaults.ScreenContextEnabled;
        var providerId = defaults.ProviderId;
        var memoryEnabled = defaults.MemoryEnabled;
        var activeInteraction = defaults.ActiveInteraction;
        var interactionFrequency = defaults.InteractionFrequency;
        var screenAwareness = defaults.ScreenAwareness;
        var intimacyEnabled = defaults.IntimacyEnabled;
        var dailySummary = defaults.DailySummary;
        var summaryImage = defaults.SummaryImage;
        var ttsEnabled = defaults.TtsEnabled;
        var allReply = defaults.AllReply;

        var camel = options.PropertyNamingPolicy ?? JsonNamingPolicy.CamelCase;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("AiSettings 属性名解析失败");
            var name = camel.ConvertName(reader.GetString()!);
            reader.Read();
            switch (name)
            {
                case "enabled": enabled = ReadBool(ref reader); break;
                case "screenAnalysis": screenAnalysis = ReadBool(ref reader); break;
                case "outputMode": outputMode = ReadString(ref reader) ?? defaults.OutputMode; break;
                case "screenContextEnabled": screenContextEnabled = ReadBool(ref reader); break;
                case "providerId": providerId = ReadString(ref reader) ?? ""; break;
                case "memoryEnabled": memoryEnabled = ReadBool(ref reader); break;
                case "activeInteraction": activeInteraction = ReadBool(ref reader); break;
                case "interactionFrequency": interactionFrequency = ReadString(ref reader) ?? defaults.InteractionFrequency; break;
                case "screenAwareness": screenAwareness = ReadBool(ref reader); break;
                case "intimacyEnabled": intimacyEnabled = ReadBool(ref reader); break;
                case "dailySummary": dailySummary = ReadBool(ref reader); break;
                case "summaryImage": summaryImage = ReadBool(ref reader); break;
                case "ttsEnabled": ttsEnabled = ReadBool(ref reader); break;
                case "allReply": allReply = ReadBool(ref reader); break;
                default: reader.Skip(); break; // 未知字段容忍（前向兼容）
            }
        }

        return new AiSettings(
            enabled, screenAnalysis, outputMode, screenContextEnabled, providerId,
            memoryEnabled, activeInteraction, interactionFrequency, screenAwareness,
            intimacyEnabled, dailySummary, summaryImage, ttsEnabled, allReply);
    }

    public override void Write(Utf8JsonWriter writer, AiSettings value, JsonSerializerOptions options)
    {
        // 全字段输出（默认 record 序列化行为，但走 camelCase 保持一致性）
        writer.WriteStartObject();
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("Enabled") ?? "enabled");
        JsonSerializer.Serialize(writer, value.Enabled, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("ScreenAnalysis") ?? "screenAnalysis");
        JsonSerializer.Serialize(writer, value.ScreenAnalysis, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("OutputMode") ?? "outputMode");
        JsonSerializer.Serialize(writer, value.OutputMode, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("ScreenContextEnabled") ?? "screenContextEnabled");
        JsonSerializer.Serialize(writer, value.ScreenContextEnabled, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("ProviderId") ?? "providerId");
        JsonSerializer.Serialize(writer, value.ProviderId, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("MemoryEnabled") ?? "memoryEnabled");
        JsonSerializer.Serialize(writer, value.MemoryEnabled, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("ActiveInteraction") ?? "activeInteraction");
        JsonSerializer.Serialize(writer, value.ActiveInteraction, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("InteractionFrequency") ?? "interactionFrequency");
        JsonSerializer.Serialize(writer, value.InteractionFrequency, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("ScreenAwareness") ?? "screenAwareness");
        JsonSerializer.Serialize(writer, value.ScreenAwareness, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("IntimacyEnabled") ?? "intimacyEnabled");
        JsonSerializer.Serialize(writer, value.IntimacyEnabled, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("DailySummary") ?? "dailySummary");
        JsonSerializer.Serialize(writer, value.DailySummary, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("SummaryImage") ?? "summaryImage");
        JsonSerializer.Serialize(writer, value.SummaryImage, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("TtsEnabled") ?? "ttsEnabled");
        JsonSerializer.Serialize(writer, value.TtsEnabled, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("AllReply") ?? "allReply");
        JsonSerializer.Serialize(writer, value.AllReply, options);
        writer.WriteEndObject();
    }

    private static bool ReadBool(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False
            ? reader.GetBoolean()
            : throw new JsonException("AiSettings 布尔字段解析失败");

    private static string? ReadString(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : reader.TokenType == JsonTokenType.Null ? null
            : throw new JsonException("AiSettings 字符串字段解析失败");
}

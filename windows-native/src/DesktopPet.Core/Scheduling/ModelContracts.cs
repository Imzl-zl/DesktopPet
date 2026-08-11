using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Core.ImageGen;

namespace DesktopPet.Core.Scheduling;

/// <summary>模型能力标记（设置页按能力显示；架构文档 §3.1）。</summary>
[Flags]
[JsonConverter(typeof(ModelCapabilitiesJsonConverter))]
public enum ModelCapabilities
{
    None = 0,
    Chat = 1,
    Vision = 2,
}

/// <summary>
/// capabilities JSON 兼容 converter：支持文档格式数组（["chat","vision"]）、
/// 单字符串（"chat"）与数字（3）三种输入；序列化统一输出数组格式。
/// 修复：JsonStringEnumConverter 无法解析数组 → providers.json 整体失败（Phase 5 遗留）。
/// </summary>
public sealed class ModelCapabilitiesJsonConverter : JsonConverter<ModelCapabilities>
{
    public override ModelCapabilities Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = ModelCapabilities.None;
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) break;
                result |= ReadOne(ref reader);
            }
            return result;
        }
        return ReadOne(ref reader);
    }

    private static ModelCapabilities ReadOne(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()?.ToLowerInvariant() switch
            {
                "chat" => ModelCapabilities.Chat,
                "vision" => ModelCapabilities.Vision,
                _ => ModelCapabilities.None,
            },
            JsonTokenType.Number => (ModelCapabilities)reader.GetInt32(),
            _ => ModelCapabilities.None,
        };
    }

    public override void Write(Utf8JsonWriter writer, ModelCapabilities value, JsonSerializerOptions options)
    {
        // 输出文档格式：["chat","vision"]（对齐架构文档 §3.1，兼容 Tauri 版配置文件）
        writer.WriteStartArray();
        if (value.HasFlag(ModelCapabilities.Chat)) writer.WriteStringValue("chat");
        if (value.HasFlag(ModelCapabilities.Vision)) writer.WriteStringValue("vision");
        writer.WriteEndArray();
    }
}

public enum ChatRole
{
    System,
    User,
    Assistant,
}

public sealed record ChatMessage(ChatRole Role, string Content, string? ImageDataUrl = null);

/// <summary>
/// 模型请求（OpenAI 兼容 /chat/completions 语义的领域模型）。
/// SystemPrompt 每轮携带完整人格拼接结果（防人格漂移，ai-personas.md §1）。
/// </summary>
public sealed record ChatRequest(
    string SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    double Temperature = 0.7,
    int? MaxTokens = null); // null = 不发送 max_tokens（上游默认）；桌宠互动/评论路径显式传 120

public sealed record ChatResult(string Text, int TokensUsed);

public sealed record ModelInfo(string Id, string Name, ModelCapabilities Capabilities);

/// <summary>
/// Provider 调用错误（可恢复/可提示分级，架构文档 §8）。
/// Code：auth（401/key 无效）| timeout | network | invalid-response。
/// UI 按 Code 映射 i18n 文案与内联错误条。
/// </summary>
public sealed class ProviderException : Exception
{
    public string Code { get; }

    public ProviderException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}

/// <summary>
/// 模型 Provider 契约（架构文档 §3.2）。OpenAiCompatibleProvider 一个类
/// 通吃 OpenAI/Ollama 等兼容端点；测试用内存实现。
/// </summary>
public interface IModelProvider
{
    string Id { get; }
    ModelCapabilities Capabilities { get; }

    /// <summary>补全对话（含视觉请求，图片编码在消息 content 中）。</summary>
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct);

    /// <summary>列出可用模型（设置页"测试连接"用）。</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct);
}

/// <summary>
/// 模型连接配置（架构文档 §3.1 providers.json 的 models 段）。
/// ApiKeyRef 是 Windows Credential Manager 的引用 id，不落明文 JSON。
/// </summary>
public sealed record ProviderConfig(
    string Id,
    string Name,
    string BaseUrl,                 // 如 https://api.openai.com/v1 或 http://localhost:11434/v1
    string ApiKeyRef,               // 凭据引用 id（空 = 无鉴权，如本地 Ollama）
    string ModelName,               // 如 gpt-4o / qwen2.5-vl:7b
    [property: JsonConverter(typeof(ModelCapabilitiesJsonConverter))]
    ModelCapabilities Capabilities, // 能力标记，UI 按能力显示（属性级 converter：优先于 Options 集合）
    bool IsDefault,
    string? ReasoningEffort = null, // 推理模型开关（如 "none" 关闭思考；空 = 不发送）
    int? MaxOutputTokens = null,    // 最大输出 token（空 = 桌宠短句默认 120；如模型支持可填大值）
    int? ContextWindowTokens = null); // 模型上下文长度（空 = L1 会话窗口按最近 5 轮截断）

/// <summary>
/// TTS 端点连接配置（windows-tts-design.md §5.2：providers.json 的 tts 段，对齐 image 段）。
/// ApiKeyRef 是 Windows Credential Manager 的引用 id，不落明文 JSON。
/// </summary>
public sealed record TtsEndpointConfig(
    string BaseUrl,      // 如 https://api.siliconflow.cn/v1（/v1/audio/speech）
    string ApiKeyRef,    // 凭据引用 id（空 = 无鉴权，如本地 GPT-SoVITS）
    string ModelName,    // 如 FunAudioLLM/CosyVoice2-0.5B / tts-1 / gpt-sovits
    string Voice = "");  // 默认音色 id；空 = 自动（列表首个/按语言）

public sealed record ProvidersFileMigrationSource(
    ProvidersFileModel Providers,
    bool IsLossless);

/// <summary>providers.json 存储模型 + 归一化/序列化（camelCase，对齐既有 JSON 存储风格）。</summary>
public sealed class ProvidersFileModel
{
    public List<ProviderConfig> Models { get; set; } = [];

    /// <summary>生图连接（windows-imagegen-design.md §6：连接列表；空 = 未配置）。</summary>
    public ImageConnectionsConfig? Image { get; set; }

    /// <summary>windows-tts-design.md §5.2：TTS 端点连接（空 = 未配置，用本地引擎）。</summary>
    public TtsEndpointConfig? Tts { get; set; }

    public static ProvidersFileModel Normalize(ProvidersFileModel raw)
    {
        var models = (raw.Models ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Id)
                        && !string.IsNullOrWhiteSpace(m.BaseUrl)
                        && !string.IsNullOrWhiteSpace(m.ModelName))
            .ToList();
        var image = ImageConnectionsConfig.Normalize(raw.Image);
        var tts = raw.Tts is null || string.IsNullOrWhiteSpace(raw.Tts.BaseUrl)
                  || string.IsNullOrWhiteSpace(raw.Tts.ModelName)
            ? null
            : raw.Tts with { BaseUrl = raw.Tts.BaseUrl.Trim(), ModelName = raw.Tts.ModelName.Trim() };
        return new ProvidersFileModel { Models = models, Image = image, Tts = tts };
    }


    public static ProvidersFileMigrationSource InspectForMigration(string json)
    {
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<ProvidersFileModel>(json, Storage.JsonOptions.CamelCase);
            if (raw is null)
                return new ProvidersFileMigrationSource(new ProvidersFileModel(), false);
            var rawModelCount = raw.Models?.Count ?? 0;
            var normalized = Normalize(raw);
            var lossless = normalized.Models.Count == rawModelCount
                           && (raw.Image is null || normalized.Image is not null)
                           && (raw.Tts is null || normalized.Tts is not null);
            return new ProvidersFileMigrationSource(normalized, lossless);
        }
        catch (System.Text.Json.JsonException)
        {
            return new ProvidersFileMigrationSource(new ProvidersFileModel(), false);
        }
    }

    public static string Serialize(ProvidersFileModel file)
        => System.Text.Json.JsonSerializer.Serialize(Normalize(file), Storage.JsonOptions.CamelCase);

    public static ProvidersFileModel Deserialize(string json)
    {
        try
        {
            var file = System.Text.Json.JsonSerializer.Deserialize<ProvidersFileModel>(json, Storage.JsonOptions.CamelCase);
            return file is null ? new ProvidersFileModel() : Normalize(file);
        }
        catch (System.Text.Json.JsonException)
        {
            return new ProvidersFileModel();
        }
    }
}

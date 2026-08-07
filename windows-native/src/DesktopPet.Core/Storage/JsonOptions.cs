using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Core.Storage;

/// <summary>
/// 共享 JSON 序列化选项（单一真值：原三处重复定义的 camelCase options 收敛于此）。
/// 均为只读使用约定，调用方不得修改这些实例。
/// </summary>
public static class JsonOptions
{
    /// <summary>camelCase + 枚举字符串：pet-store / settings / memory / intimacy / personas / providers。</summary>
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>纯 camelCase：care.json（对齐 TS ap_care 格式，无枚举）。</summary>
    public static readonly JsonSerializerOptions CamelCasePlain = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>camelCase + 大小写不敏感读取：Tauri 旧数据迁移（容错旧字段大小写）。</summary>
    public static readonly JsonSerializerOptions MigrationRead = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

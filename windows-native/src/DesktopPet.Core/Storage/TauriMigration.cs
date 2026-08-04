using System.Text.Json;
using DesktopPet.Core.Care;
using DesktopPet.Core.Pets;

namespace DesktopPet.Core.Storage;

/// <summary>迁移结果。</summary>
public sealed record TauriMigrationResult(
    PetStore Store,
    Dictionary<string, CareState> Care,
    bool HadData);

/// <summary>
/// Tauri 版 localStorage 数据一次性迁移工具（迁移计划 Phase 3：含 ap_care_* 养成状态）。
/// 输入为导出的 localStorage 键值 JSON（键 → 字符串值）：
/// {
///   "desktoppet.petSlug": "\"cat\"",          // legacy 选中精灵
///   "ap_pet_instances": "{...}",              // PetStore（新版实例优先）
///   "ap_care": "{\"cat\":{...}}"              // 每精灵养成状态
/// }
/// 导出方式：Tauri 版开发控制台执行
/// localStorage 相关键 JSON.stringify 后保存为 tauri-export.json。
/// </summary>
public static class TauriMigration
{
    public static TauriMigrationResult Migrate(JsonElement export, DateTime now)
    {
        var petStoreRaw = ReadString(export, "ap_pet_instances");
        var legacySlugRaw = ReadString(export, "desktoppet.petSlug");
        var careRaw = ReadString(export, "ap_care");

        var care = ParseCare(careRaw);
        var hadData = false;

        // 1. 宠物实例：新版实例 store 优先；否则 legacy slug → 默认实例
        PetStore? store = null;
        if (petStoreRaw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(petStoreRaw);
                store = PetStoreModel.ParsePetStore(doc.RootElement);
            }
            catch (JsonException) { }
        }
        var legacySlug = ParseOptionalString(legacySlugRaw);
        store = PetStoreModel.MigrateLegacyPetStore(store, legacySlug is null ? null : new LegacyPet(legacySlug, legacySlug));
        if (store.Instances.Count > 0) hadData = true;

        // 2. 养成状态：legacy slug → 首个实例 id（对齐 care 迁移语义）
        var instanceId = store.SelectedId ?? store.Instances.FirstOrDefault()?.Id;
        if (instanceId is not null && legacySlug is not null && legacySlug != instanceId)
        {
            care = CareStoreModel.MigrateLegacyCareState(care, legacySlug, instanceId);
        }
        if (care.Count > 0) hadData = true;

        return new TauriMigrationResult(store, care, hadData);
    }

    private static string? ReadString(JsonElement root, string key)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>localStorage 值可能是 JSON 字符串（含引号），也可能是裸字符串。</summary>
    private static string? ParseOptionalString(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed);
            }
            catch (JsonException) { }
        }
        return trimmed;
    }

    private static Dictionary<string, CareState> ParseCare(string? raw)
    {
        if (raw is null) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var result = new Dictionary<string, CareState>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var state = property.Value.Deserialize<CareState>(CareJsonOptions);
                if (state is not null) result[property.Name] = state;
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions CareJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

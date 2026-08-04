using System.Text.Json;

namespace DesktopPet.Core.Storage;

public readonly record struct PetPosition(int X, int Y);

/// <summary>
/// pet-positions.json 语义 1:1 移植自 windows/src-tauri/src/lib.rs：
/// 格式 { "pet-{id}": [x, y] }（物理像素）；坏数据整体回退为空。
/// 本类为纯逻辑（字符串 ↔ 字典），文件 IO 由调用方（App）负责。
/// </summary>
public static class PetPositionsFile
{
    public static Dictionary<string, PetPosition> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            var result = new Dictionary<string, PetPosition>();
            foreach (var property in root.EnumerateObject())
            {
                var value = property.Value;
                if (value.ValueKind != JsonValueKind.Array) continue;
                var items = value.EnumerateArray().ToList();
                if (items.Count < 2) continue;
                if (items[0].ValueKind != JsonValueKind.Number ||
                    items[1].ValueKind != JsonValueKind.Number) continue;
                result[property.Name] = new PetPosition(items[0].GetInt32(), items[1].GetInt32());
            }
            return result;
        }
        catch (JsonException)
        {
            return []; // serde_json::from_str(...).unwrap_or_default()
        }
    }

    public static string Serialize(IReadOnlyDictionary<string, PetPosition> positions)
        => JsonSerializer.Serialize(positions.ToDictionary(
            kv => kv.Key,
            kv => new[] { kv.Value.X, kv.Value.Y }));

    /// <summary>不可变更新：单点替换，返回新快照（对应 Rust 全量写回）。</summary>
    public static Dictionary<string, PetPosition> Update(
        IReadOnlyDictionary<string, PetPosition> positions,
        string id,
        PetPosition position)
    {
        var next = new Dictionary<string, PetPosition>(positions) { [id] = position };
        return next;
    }
}

/// <summary>pets-visible 文件语义：文本 trim 后为 "0" 即隐藏，其余（含缺失）可见。</summary>
public static class PetVisibility
{
    public static bool Parse(string? raw) => raw?.Trim() != "0";
}

/// <summary>窗口语义常量与纯函数，1:1 移植自 Rust（build_desktop_pet_window / default_pet_position / valid_pet_id）。</summary>
public static class WindowPlacement
{
    public const double WindowWidth = 260;
    public const double WindowHeight = 320;
    public const int MaxDesktopPets = 12;
    public const double PositionMargin = 20;

    public static (double X, double Y) DefaultPetPosition(double workAreaWidth, double workAreaHeight, int index)
    {
        var x = Math.Max(PositionMargin, workAreaWidth - 280.0 - index * 48.0);
        var y = Math.Max(PositionMargin, workAreaHeight - 380.0 + index * 32.0);
        return (x, y);
    }

    /// <summary>对齐 Rust valid_pet_id：非空、≤64、仅 ascii 小写/数字/连字符。</summary>
    public static bool IsValidPetId(string? id)
        => !string.IsNullOrEmpty(id) &&
           id.Length <= 64 &&
           id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

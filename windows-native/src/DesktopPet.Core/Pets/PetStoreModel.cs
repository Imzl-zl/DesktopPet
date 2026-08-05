using System.Text.Json;
using DesktopPet.Core.Roaming;

namespace DesktopPet.Core.Pets;

public sealed record PetInstance
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string SpriteSlug { get; init; }
    public bool Visible { get; init; }
    public double Size { get; init; }
    public bool RoamEnabled { get; init; }
    public RoamMode RoamMode { get; init; }
    public double RoamSpeed { get; init; }
    public double WanderPauseMinMs { get; init; }
    public double WanderPauseMaxMs { get; init; }
    public bool ReactsToActivity { get; init; }
    public string? PersonaId { get; init; } = null;   // Phase 6d：每宠物独立人格覆盖（空 = 跟随全局）
}

public sealed record PetStore
{
    public int Version { get; init; }
    public string? SelectedId { get; init; }
    public IReadOnlyList<PetInstance> Instances { get; init; } = [];
}

public sealed record LegacyPet(string Slug, string Name);

/// <summary>可空补丁，对应 TS 的 Partial&lt;Omit&lt;PetInstance, "id"&gt;&gt;。</summary>
public sealed record PetInstancePatch
{
    public string? Name { get; init; }
    public string? SpriteSlug { get; init; }
    public bool? Visible { get; init; }
    public double? Size { get; init; }
    public bool? RoamEnabled { get; init; }
    public RoamMode? RoamMode { get; init; }
    public double? RoamSpeed { get; init; }
    public double? WanderPauseMinMs { get; init; }
    public double? WanderPauseMaxMs { get; init; }
    public bool? ReactsToActivity { get; init; }
    public string? PersonaId { get; init; }   // Phase 6d：null = 不修改
}

/// <summary>
/// 1:1 移植自 windows/src/pets.ts（vitest 已有对照测试）。
/// 归一化语义与 TS 弱类型行为逐字段对齐（见 <see cref="Normalize(RawPetInstance)"/>）。
/// </summary>
public static class PetStoreModel
{
    public const int StoreVersion = 1;
    public const string LegacyInstanceId = "legacy-pet";

    private static readonly string[] ValidRoamModes = ["stay", "wander", "cursor", "climb"];

    public static PetStore EmptyPetStore() => new() { Version = StoreVersion, SelectedId = null, Instances = [] };

    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Max(min, Math.Min(max, Math.Round(value))) : fallback;

    /// <summary>归一化输入：字段缺失 = undefined，JSON null = 0，与 TS 弱类型读取一致。</summary>
    public sealed record RawPetInstance
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public string? SpriteSlug { get; init; }
        public bool? Visible { get; init; }
        public double? Size { get; init; }
        public bool? RoamEnabled { get; init; }
        public RoamMode? RoamMode { get; init; }
        public double? RoamSpeed { get; init; }
        public double? WanderPauseMinMs { get; init; }
        public double? WanderPauseMaxMs { get; init; }
        public bool? ReactsToActivity { get; init; }
        public string? PersonaId { get; init; }

        public static RawPetInstance FromInstance(PetInstance value) => new()
        {
            Id = value.Id,
            Name = value.Name,
            SpriteSlug = value.SpriteSlug,
            Visible = value.Visible,
            Size = value.Size,
            RoamEnabled = value.RoamEnabled,
            RoamMode = value.RoamMode,
            RoamSpeed = value.RoamSpeed,
            WanderPauseMinMs = value.WanderPauseMinMs,
            WanderPauseMaxMs = value.WanderPauseMaxMs,
            ReactsToActivity = value.ReactsToActivity,
            PersonaId = value.PersonaId,
        };
    }

    private static PetInstance? Normalize(RawPetInstance? value)
    {
        if (value is null) return null;
        if (string.IsNullOrEmpty(value.Id) || string.IsNullOrEmpty(value.SpriteSlug)) return null;
        var roamMode = value.RoamMode is { } mode &&
                       Array.IndexOf(ValidRoamModes, mode.ToString().ToLowerInvariant()) >= 0
            ? mode
            : RoamMode.Wander;
        var wanderPause = Pause.NormalizeWanderPauseRange(value.WanderPauseMinMs, value.WanderPauseMaxMs);
        return new PetInstance
        {
            Id = value.Id,
            Name = value.Name is { Length: > 0 } name ? name[..Math.Min(40, name.Length)] : value.SpriteSlug,
            SpriteSlug = value.SpriteSlug,
            Visible = value.Visible != false,
            Size = Clamp(value.Size ?? double.NaN, 70, 130, 100),
            RoamEnabled = value.RoamEnabled != false,
            RoamMode = roamMode,
            RoamSpeed = Clamp(value.RoamSpeed ?? double.NaN, 1, 10, 5),
            WanderPauseMinMs = wanderPause.MinMs,
            WanderPauseMaxMs = wanderPause.MaxMs,
            ReactsToActivity = value.ReactsToActivity == true,
            PersonaId = value.PersonaId,
        };
    }

    // ---- JSON 解析（对齐 TS 的 unknown → normalizeInstance 弱类型读取）----

    private static RawPetInstance? RawFromJson(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;

        string? ReadString(string property)
            => value.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;

        // TS Number(x)：string 可解析 → 数值；null → 0；其他 → NaN。
        double? ReadNumber(string property)
        {
            if (!value.TryGetProperty(property, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.String when double.TryParse(el.GetString(), out var d) => d,
                JsonValueKind.Null => 0,
                _ => double.NaN,
            };
        }

        bool? ReadBool(string property)
        {
            if (!value.TryGetProperty(property, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null, // TS 里 "false"/0 等 !== false → true，由 Visible 等默认逻辑处理
            };
        }

        var roamMode = ReadString("roamMode");
        Roaming.RoamMode? mode = roamMode?.ToLowerInvariant() switch
        {
            "stay" => Roaming.RoamMode.Stay,
            "wander" => Roaming.RoamMode.Wander,
            "cursor" => Roaming.RoamMode.Cursor,
            "climb" => Roaming.RoamMode.Climb,
            _ => null,
        };

        return new RawPetInstance
        {
            Id = ReadString("id"),
            Name = ReadString("name"),
            SpriteSlug = ReadString("spriteSlug"),
            Visible = ReadBool("visible"),
            Size = ReadNumber("size"),
            RoamEnabled = ReadBool("roamEnabled"),
            RoamMode = mode,
            RoamSpeed = ReadNumber("roamSpeed"),
            WanderPauseMinMs = ReadNumber("wanderPauseMinMs"),
            WanderPauseMaxMs = ReadNumber("wanderPauseMaxMs"),
            ReactsToActivity = ReadBool("reactsToActivity"),
            PersonaId = ReadString("personaId"),
        };
    }

    public static PetStore? ParsePetStore(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty("version", out var versionEl) ||
            versionEl.ValueKind != JsonValueKind.Number ||
            versionEl.GetInt32() != StoreVersion ||
            !value.TryGetProperty("instances", out var instancesEl) ||
            instancesEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ids = new HashSet<string>();
        var instances = new List<PetInstance>();
        foreach (var element in instancesEl.EnumerateArray())
        {
            var normalized = Normalize(RawFromJson(element));
            if (normalized is null || !ids.Add(normalized.Id)) continue;
            instances.Add(normalized);
        }

        var selectedId = value.TryGetProperty("selectedId", out var selectedEl) &&
                         selectedEl.ValueKind == JsonValueKind.String &&
                         ids.Contains(selectedEl.GetString()!)
            ? selectedEl.GetString()
            : instances.Count > 0 ? instances[0].Id : null;

        return new PetStore { Version = StoreVersion, SelectedId = selectedId, Instances = instances };
    }

    // ---- 实例操作 ----

    public static PetStore MigrateLegacyPetStore(PetStore? store, LegacyPet? legacy)
    {
        if (store is not null) return store;
        if (legacy is null || string.IsNullOrEmpty(legacy.Slug)) return EmptyPetStore();
        var instance = new PetInstance
        {
            Id = LegacyInstanceId,
            Name = legacy.Name.Trim().Length > 0 ? legacy.Name.Trim()[..Math.Min(40, legacy.Name.Trim().Length)] : legacy.Slug,
            SpriteSlug = legacy.Slug,
            Visible = true,
            Size = 100,
            RoamEnabled = true,
            RoamMode = RoamMode.Wander,
            RoamSpeed = 5,
            WanderPauseMinMs = Pause.DefaultWanderPauseMinMs,
            WanderPauseMaxMs = Pause.DefaultWanderPauseMaxMs,
            ReactsToActivity = true,
        };
        return new PetStore { Version = StoreVersion, SelectedId = instance.Id, Instances = [instance] };
    }

    public static PetStore CreatePetInstance(PetStore store, PetInstance instance)
    {
        if (store.Instances.Any(candidate => candidate.Id == instance.Id))
        {
            throw new InvalidOperationException($"duplicate pet instance id: {instance.Id}");
        }
        var normalized = Normalize(RawPetInstance.FromInstance(instance))
            ?? throw new InvalidOperationException("invalid pet instance");
        return new PetStore
        {
            Version = StoreVersion,
            SelectedId = normalized.Id,
            Instances = [.. store.Instances, normalized],
        };
    }

    public static PetStore UpdatePetInstance(PetStore store, string id, PetInstancePatch patch)
    {
        var index = store.Instances
            .Select((candidate, i) => (candidate, i))
            .FirstOrDefault(x => x.candidate.Id == id);
        if (index.candidate is null) throw new InvalidOperationException($"unknown pet instance id: {id}");

        var current = index.candidate;
        var merged = new RawPetInstance
        {
            Id = current.Id,
            Name = patch.Name ?? current.Name,
            SpriteSlug = patch.SpriteSlug ?? current.SpriteSlug,
            Visible = patch.Visible ?? current.Visible,
            Size = patch.Size ?? current.Size,
            RoamEnabled = patch.RoamEnabled ?? current.RoamEnabled,
            RoamMode = patch.RoamMode ?? current.RoamMode,
            RoamSpeed = patch.RoamSpeed ?? current.RoamSpeed,
            WanderPauseMinMs = patch.WanderPauseMinMs ?? current.WanderPauseMinMs,
            WanderPauseMaxMs = patch.WanderPauseMaxMs ?? current.WanderPauseMaxMs,
            ReactsToActivity = patch.ReactsToActivity ?? current.ReactsToActivity,
            PersonaId = patch.PersonaId ?? current.PersonaId,
        };
        var updated = Normalize(merged) ?? throw new InvalidOperationException("invalid pet instance update");
        var instances = store.Instances.ToList();
        instances[index.i] = updated;
        return new PetStore { Version = StoreVersion, SelectedId = store.SelectedId, Instances = instances };
    }

    public static PetStore RemovePetInstance(PetStore store, string id)
    {
        var instances = store.Instances.Where(instance => instance.Id != id).ToList();
        if (instances.Count == store.Instances.Count) return store;
        return new PetStore
        {
            Version = StoreVersion,
            SelectedId = store.SelectedId == id ? instances.FirstOrDefault()?.Id ?? null : store.SelectedId,
            Instances = instances,
        };
    }

    public static PetStore SelectPetInstance(PetStore store, string? id)
    {
        if (id is not null && !store.Instances.Any(instance => instance.Id == id))
        {
            throw new InvalidOperationException($"unknown pet instance id: {id}");
        }
        return new PetStore { Version = StoreVersion, SelectedId = id, Instances = store.Instances };
    }

    public static PetInstance? SelectedPetInstance(PetStore store)
        => store.Instances.FirstOrDefault(instance => instance.Id == store.SelectedId);

    public static PetInstance? PetInstanceById(PetStore store, string id)
        => store.Instances.FirstOrDefault(instance => instance.Id == id);

    public static string NewPetInstanceId() => $"pet-{Guid.NewGuid():N}";
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Care;
using DesktopPet.Core.Memory;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Diagnostics;

namespace DesktopPet.Infra.Storage;

/// <summary>%APPDATA%/DesktopPet/ 的 JSON 文件实现（迁移计划 §2 持久化决策）。
/// store 文件使用 camelCase + 枚举字符串，与 Tauri localStorage 的 TS 格式一致
/// （Phase 3 迁移工具可直接读取）。
/// 位于 Infra（架构文档 §1/§9：仓储在基础设施层）：Core 只保留 IJsonStore 接口
/// 与 InMemoryJsonStore（零 IO 领域层承诺）。</summary>
public sealed class FileJsonStore : IJsonStore
{
    private static readonly ConcurrentDictionary<string, object> WriteLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _directory;

    /// <summary>JSON 损坏（解析失败）：损坏文件已被隔离保留（.corrupt-*），参数为隔离后路径。
    /// 修复：原实现损坏 = 静默返回 null，后续保存直接覆盖原文件，用户数据无感知丢失。</summary>
    public event Action<string>? FileCorrupted;

    public string DirectoryPath => _directory;

    public FileJsonStore(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JsonStoreException("创建数据目录", directory, ex);
        }
    }

    private string PathFor(string name) => System.IO.Path.Combine(_directory, name);

    private string? ReadFile(string name)
    {
        var path = PathFor(name);
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JsonStoreException("读取", path, ex);
        }
    }

    /// <summary>损坏文件隔离：改名保留现场（.corrupt-时间戳-guid），触发事件供 UI 提示。
    /// 隔离失败不阻断（保持原加载语义），但不再让后续保存覆盖原始数据。</summary>
    private void OnCorruptFile(string name)
    {
        var path = PathFor(name);
        try
        {
            if (!File.Exists(path)) return;
            var quarantine = path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(path, quarantine);
            FileCorrupted?.Invoke(quarantine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 隔离失败：保留原文件（数据不丢），本次仍按无数据加载
        }
    }

    private void WriteFile(string name, string content)
    {
        var path = PathFor(name);
        lock (WriteLocks.GetOrAdd(path, static _ => new object()))
        {
            try
            {
                // 原子写统一走 Infra 的 AtomicFileWriter（唯一实现）：
                // 原 Core 侧 AtomicFilePublisher 是第二套等价实现，已删除。
                AtomicFileWriter.WriteAllText(path, content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new JsonStoreException("写入", path, ex);
            }
        }
    }

    public PetStore? LoadPetStore()
    {
        var raw = ReadFile("pet-store.json");
        if (raw is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return PetStoreModel.ParsePetStore(doc.RootElement);
        }
        catch (JsonException)
        {
            OnCorruptFile("pet-store.json");
            return null;
        }
    }

    public void SavePetStore(PetStore store)
        => WriteFile("pet-store.json", JsonSerializer.Serialize(store, JsonOptions.CamelCase));

    public Dictionary<string, PetPosition> LoadPositions()
        => PetPositionsFile.Parse(ReadFile("pet-positions.json"));

    public void SavePositions(IReadOnlyDictionary<string, PetPosition> positions)
        => WriteFile("pet-positions.json", PetPositionsFile.Serialize(positions));

    public bool LoadGlobalVisibility()
        => PetVisibility.Parse(ReadFile("pets-visible"));

    public void SaveGlobalVisibility(bool visible)
        => WriteFile("pets-visible", visible ? "1" : "0");

    public Dictionary<string, CareState> LoadCare()
    {
        var raw = ReadFile("care.json");
        if (raw is null) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CareState>>(raw, JsonOptions.CamelCasePlain) ?? [];
        }
        catch (JsonException)
        {
            OnCorruptFile("care.json");
            return [];
        }
    }

    public void SaveCare(IReadOnlyDictionary<string, CareState> states)
        => WriteFile("care.json", JsonSerializer.Serialize(states, JsonOptions.CamelCasePlain));

    public AppSettings? LoadSettings()
    {
        var raw = ReadFile("app-settings.json");
        if (raw is null) return null;
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions.CamelCase);
            return settings is null ? null : AppSettings.Normalize(settings);
        }
        catch (JsonException)
        {
            OnCorruptFile("app-settings.json");
            return null;
        }
    }

    public void SaveSettings(AppSettings settings)
        => WriteFile("app-settings.json", JsonSerializer.Serialize(AppSettings.Normalize(settings), JsonOptions.CamelCase));

    public UserProfile? LoadMemoryProfile()
    {
        var raw = ReadFile("memory.json");
        if (raw is null) return null;
        try
        {
            var profile = JsonSerializer.Deserialize<UserProfile>(raw, JsonOptions.CamelCase);
            return profile is null ? null : MemoryProfileExtractor.Normalize(profile);
        }
        catch (JsonException)
        {
            OnCorruptFile("memory.json");
            return null;
        }
    }

    public void SaveMemoryProfile(UserProfile profile)
        => WriteFile("memory.json", JsonSerializer.Serialize(MemoryProfileExtractor.Normalize(profile), JsonOptions.CamelCase));

    public IntimacyState? LoadIntimacy()
    {
        var raw = ReadFile("intimacy.json");
        if (raw is null) return null;
        try
        {
            var state = JsonSerializer.Deserialize<IntimacyState>(raw, JsonOptions.CamelCase);
            return state is null ? null : NormalizeIntimacy(state);
        }
        catch (JsonException)
        {
            OnCorruptFile("intimacy.json");
            return null;
        }
    }

    public void SaveIntimacy(IntimacyState state)
        => WriteFile("intimacy.json", JsonSerializer.Serialize(NormalizeIntimacy(state), JsonOptions.CamelCase));

    private static IntimacyState NormalizeIntimacy(IntimacyState state)
    {
        var value = Math.Clamp(state.Value, 0, 100);
        var last = state.LastInteractionDate == default ? DateTime.Today : state.LastInteractionDate;
        return new IntimacyState(value, last);
    }

    public DateOnly? LoadDiaryLastGenerated()
    {
        var raw = ReadFile("diary-meta.json");
        if (raw is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("lastGenerated", out var p)) return null;
            return DateOnly.TryParse(p.GetString(), out var day) ? day : null;
        }
        catch (JsonException)
        {
            OnCorruptFile("diary-meta.json");
            return null;
        }
    }

    public void SaveDiaryLastGenerated(DateOnly day)
        => WriteFile("diary-meta.json", JsonSerializer.Serialize(new { lastGenerated = day.ToString("yyyy-MM-dd") }));

    public PersonasFileModel? LoadPersonasFile()
    {
        var raw = ReadFile("personas.json");
        if (raw is null) return null;
        try
        {
            var personas = JsonSerializer.Deserialize<PersonasFileModel>(raw, JsonOptions.CamelCase);
            return personas is null ? null : PersonasFileModel.Normalize(personas);
        }
        catch (JsonException)
        {
            OnCorruptFile("personas.json");
            return null;
        }
    }

    public void SavePersonasFile(PersonasFileModel personas)
        => WriteFile("personas.json", JsonSerializer.Serialize(PersonasFileModel.Normalize(personas), JsonOptions.CamelCase));

    public ProvidersFileMigrationSource? LoadProvidersFileForMigration()
    {
        var raw = ReadFile("providers.json");
        return raw is null ? null : ProvidersFileModel.InspectForMigration(raw);
    }

    public ProvidersFileModel? LoadProvidersFile()
    {
        var raw = ReadFile("providers.json");
        if (raw is null) return null;
        return ProvidersFileModel.Deserialize(raw);
    }

    public void SaveProvidersFile(ProvidersFileModel providers)
        => WriteFile("providers.json", ProvidersFileModel.Serialize(providers));
}

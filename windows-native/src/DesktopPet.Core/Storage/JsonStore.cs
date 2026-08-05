using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Core.Ai;
using DesktopPet.Core.Care;
using DesktopPet.Core.Memory;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Pets;

namespace DesktopPet.Core.Storage;

/// <summary>
/// 持久化接口（领域层定义，App 提供文件实现，测试用内存实现）。
/// 文件布局对齐 Tauri 版：%APPDATA%/DesktopPet/ 下的 pet-store.json /
/// pet-positions.json / pets-visible。
/// </summary>
public interface IJsonStore
{
    PetStore? LoadPetStore();
    void SavePetStore(PetStore store);
    Dictionary<string, PetPosition> LoadPositions();
    void SavePositions(IReadOnlyDictionary<string, PetPosition> positions);
    bool LoadGlobalVisibility();
    void SaveGlobalVisibility(bool visible);
    Dictionary<string, CareState> LoadCare();
    void SaveCare(IReadOnlyDictionary<string, CareState> states);
    AppSettings? LoadSettings();
    void SaveSettings(AppSettings settings);
    UserProfile? LoadMemoryProfile();   // Phase 6b：记忆画像（memory.json；开关关 = 不调用 Save）
    void SaveMemoryProfile(UserProfile profile);
    IntimacyState? LoadIntimacy();      // Phase 6c：亲密度（intimacy.json）
    void SaveIntimacy(IntimacyState state);
    DateOnly? LoadDiaryLastGenerated(); // Phase 6f：日记元数据（diary-meta.json）
    void SaveDiaryLastGenerated(DateOnly day);
    PersonasFileModel? LoadPersonasFile();
    void SavePersonasFile(PersonasFileModel personas);
    ProvidersFileModel? LoadProvidersFile();
    void SaveProvidersFile(ProvidersFileModel providers);
}

/// <summary>%APPDATA%/DesktopPet/ 的 JSON 文件实现（迁移计划 §2 持久化决策）。
/// store 文件使用 camelCase + 枚举字符串，与 Tauri localStorage 的 TS 格式一致
/// （Phase 3 迁移工具可直接读取）。</summary>
public sealed class FileJsonStore : IJsonStore
{
    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;

    public string DirectoryPath => _directory;

    public FileJsonStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(string name) => System.IO.Path.Combine(_directory, name);

    private string? ReadFile(string name)
    {
        try
        {
            return File.Exists(PathFor(name)) ? File.ReadAllText(PathFor(name)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteFile(string name, string content)
    {
        try
        {
            File.WriteAllText(PathFor(name), content);
        }
        catch (IOException)
        {
            // 持久化失败不拖垮主进程（对齐 Rust 的 let _ = write(...)）
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
            return null;
        }
    }

    public void SavePetStore(PetStore store)
        => WriteFile("pet-store.json", JsonSerializer.Serialize(store, StoreJsonOptions));

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
            return JsonSerializer.Deserialize<Dictionary<string, CareState>>(raw, CareJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void SaveCare(IReadOnlyDictionary<string, CareState> states)
        => WriteFile("care.json", JsonSerializer.Serialize(states, CareJsonOptions));

    public AppSettings? LoadSettings()
    {
        var raw = ReadFile("app-settings.json");
        if (raw is null) return null;
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(raw, SettingsJsonOptions);
            return settings is null ? null : AppSettings.Normalize(settings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveSettings(AppSettings settings)
        => WriteFile("app-settings.json", JsonSerializer.Serialize(AppSettings.Normalize(settings), SettingsJsonOptions));

    public UserProfile? LoadMemoryProfile()
    {
        var raw = ReadFile("memory.json");
        if (raw is null) return null;
        try
        {
            var profile = JsonSerializer.Deserialize<UserProfile>(raw, SettingsJsonOptions);
            return profile is null ? null : MemoryProfileExtractor.Normalize(profile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveMemoryProfile(UserProfile profile)
        => WriteFile("memory.json", JsonSerializer.Serialize(MemoryProfileExtractor.Normalize(profile), SettingsJsonOptions));

    public IntimacyState? LoadIntimacy()
    {
        var raw = ReadFile("intimacy.json");
        if (raw is null) return null;
        try
        {
            var state = JsonSerializer.Deserialize<IntimacyState>(raw, SettingsJsonOptions);
            return state is null ? null : NormalizeIntimacy(state);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveIntimacy(IntimacyState state)
        => WriteFile("intimacy.json", JsonSerializer.Serialize(NormalizeIntimacy(state), SettingsJsonOptions));

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
            var personas = JsonSerializer.Deserialize<PersonasFileModel>(raw, SettingsJsonOptions);
            return personas is null ? null : PersonasFileModel.Normalize(personas);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SavePersonasFile(PersonasFileModel personas)
        => WriteFile("personas.json", JsonSerializer.Serialize(PersonasFileModel.Normalize(personas), SettingsJsonOptions));

    public ProvidersFileModel? LoadProvidersFile()
    {
        var raw = ReadFile("providers.json");
        if (raw is null) return null;
        return ProvidersFileModel.Deserialize(raw);
    }

    public void SaveProvidersFile(ProvidersFileModel providers)
        => WriteFile("providers.json", ProvidersFileModel.Serialize(providers));

    /// <summary>app-settings.json 序列化：camelCase + 枚举字符串。</summary>
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>care.json 序列化：camelCase（对齐 TS ap_care 格式，迁移工具可直接读）。</summary>
    private static readonly JsonSerializerOptions CareJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>内存实现（单元测试用）。</summary>
public sealed class InMemoryJsonStore : IJsonStore
{
    public PetStore? PetStore { get; set; }
    public Dictionary<string, PetPosition> Positions { get; set; } = [];
    public bool GlobalVisibility { get; set; } = true;
    public Dictionary<string, CareState> Care { get; set; } = [];
    public int SavePetStoreCount { get; private set; }

    public PetStore? LoadPetStore() => PetStore;
    public void SavePetStore(PetStore store) { PetStore = store; SavePetStoreCount++; }
    public Dictionary<string, PetPosition> LoadPositions() => new(Positions);
    public void SavePositions(IReadOnlyDictionary<string, PetPosition> positions) => Positions = new(positions);
    public bool LoadGlobalVisibility() => GlobalVisibility;
    public void SaveGlobalVisibility(bool visible) => GlobalVisibility = visible;
    public Dictionary<string, CareState> LoadCare() => new(Care);
    public void SaveCare(IReadOnlyDictionary<string, CareState> states) => Care = new(states);
    public AppSettings? Settings { get; set; }
    public AppSettings? LoadSettings() => Settings;
    public void SaveSettings(AppSettings settings) => Settings = settings;
    public UserProfile? MemoryProfile { get; set; }
    public UserProfile? LoadMemoryProfile() => MemoryProfile;
    public void SaveMemoryProfile(UserProfile profile) => MemoryProfile = profile;
    public IntimacyState? Intimacy { get; set; }
    public IntimacyState? LoadIntimacy() => Intimacy;
    public void SaveIntimacy(IntimacyState state) => Intimacy = state;
    public DateOnly? DiaryLastGenerated { get; set; }
    public DateOnly? LoadDiaryLastGenerated() => DiaryLastGenerated;
    public void SaveDiaryLastGenerated(DateOnly day) => DiaryLastGenerated = day;
    public PersonasFileModel? Personas { get; set; }
    public PersonasFileModel? LoadPersonasFile() => Personas;
    public void SavePersonasFile(PersonasFileModel personas) => Personas = personas;
    public ProvidersFileModel? Providers { get; set; }
    public ProvidersFileModel? LoadProvidersFile() => Providers;
    public void SaveProvidersFile(ProvidersFileModel providers) => Providers = providers;
}

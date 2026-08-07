using DesktopPet.Core.Ai;
using DesktopPet.Core.Care;
using DesktopPet.Core.Memory;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Scheduling;

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

/// <summary>文件持久化失败；调用方可据此向用户显示明确错误并保留内存状态。</summary>
public sealed class JsonStoreException : IOException
{
    public JsonStoreException(string operation, string filePath, Exception inner)
        : base($"JSON 持久化{operation}失败：{filePath}", inner)
    {
        Operation = operation;
        FilePath = filePath;
    }

    public string Operation { get; }
    public string FilePath { get; }
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

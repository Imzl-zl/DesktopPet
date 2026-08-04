using System.Text.Json;
using System.Text.Json.Serialization;
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
}

/// <summary>内存实现（单元测试用）。</summary>
public sealed class InMemoryJsonStore : IJsonStore
{
    public PetStore? PetStore { get; set; }
    public Dictionary<string, PetPosition> Positions { get; set; } = [];
    public bool GlobalVisibility { get; set; } = true;
    public int SavePetStoreCount { get; private set; }

    public PetStore? LoadPetStore() => PetStore;
    public void SavePetStore(PetStore store) { PetStore = store; SavePetStoreCount++; }
    public Dictionary<string, PetPosition> LoadPositions() => new(Positions);
    public void SavePositions(IReadOnlyDictionary<string, PetPosition> positions) => Positions = new(positions);
    public bool LoadGlobalVisibility() => GlobalVisibility;
    public void SaveGlobalVisibility(bool visible) => GlobalVisibility = visible;
}

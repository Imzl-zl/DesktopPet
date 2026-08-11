using DesktopPet.Infra.Storage;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// FileJsonStore 损坏恢复（修复）：JSON 解析失败不再静默当「无数据」——
/// 损坏文件被隔离保留（.corrupt-*），触发 FileCorrupted 事件供 UI 提示；
/// 调用方仍收到 null（无数据语义不变），但原文件不再被后续保存覆盖丢失。
/// </summary>
public sealed class FileJsonStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "DesktopPet.FileJsonStore.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void LoadPetStore_CorruptFile_IsQuarantinedAndReported()
    {
        var store = new FileJsonStore(_directory);
        var quarantined = new List<string>();
        store.FileCorrupted += path => quarantined.Add(path);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "pet-store.json"), "{not-valid-json!!");

        Assert.Null(store.LoadPetStore()); // 语义不变：损坏 = 无数据
        Assert.Single(quarantined);
        Assert.Contains(".corrupt-", quarantined[0]);
        Assert.True(File.Exists(quarantined[0])); // 原现场保留，未被后续保存覆盖
        Assert.False(File.Exists(Path.Combine(_directory, "pet-store.json"))); // 原文件已移走
    }

    [Fact]
    public void LoadSettings_CorruptFile_IsQuarantined_ValidFileNotReported()
    {
        var store = new FileJsonStore(_directory);
        var quarantined = new List<string>();
        store.FileCorrupted += path => quarantined.Add(path);

        Assert.Null(store.LoadSettings()); // 文件不存在：不触发
        Assert.Empty(quarantined);

        File.WriteAllText(Path.Combine(_directory, "app-settings.json"), "{bad");
        Assert.Null(store.LoadSettings());
        Assert.Single(quarantined);

        // 修复后写回正常文件：不再误报
        store.SaveSettings(Core.Storage.AppSettings.Defaults(Core.I18n.I18nService.Detect()));
        Assert.NotNull(store.LoadSettings());
        Assert.Single(quarantined);
    }

    [Fact]
    public void LoadCare_CorruptFile_IsQuarantined()
    {
        var store = new FileJsonStore(_directory);
        var quarantined = new List<string>();
        store.FileCorrupted += path => quarantined.Add(path);
        File.WriteAllText(Path.Combine(_directory, "care.json"), "[]not-care");

        var care = store.LoadCare();
        Assert.Empty(care);
        Assert.Single(quarantined);
        Assert.Contains(".corrupt-", quarantined[0]);
    }
}

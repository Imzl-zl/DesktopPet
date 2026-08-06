using DesktopPet.Core.Care;
using DesktopPet.Core.Memory;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>持久化 roundtrip：camelCase + 枚举字符串（对齐 Tauri localStorage 格式）。</summary>
public class JsonStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"desktoppet-store-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void PetStore_RoundTrips_WithCamelCaseAndEnumStrings()
    {
        var store = new FileJsonStore(_dir);
        var instance = new PetInstance
        {
            Id = "pet-abc",
            Name = "Miso",
            SpriteSlug = "cat",
            Visible = true,
            Size = 110,
            RoamEnabled = true,
            RoamMode = RoamMode.Climb,
            RoamSpeed = 7,
            WanderPauseMinMs = 1500,
            WanderPauseMaxMs = 4000,
            ReactsToActivity = true,
        };
        store.SavePetStore(PetStoreModel.CreatePetInstance(PetStoreModel.EmptyPetStore(), instance));

        var raw = File.ReadAllText(Path.Combine(_dir, "pet-store.json"));
        var bytes = File.ReadAllBytes(Path.Combine(_dir, "pet-store.json"));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Contains("\"roamMode\":\"climb\"", raw);
        Assert.Contains("\"selectedId\":\"pet-abc\"", raw);

        var loaded = store.LoadPetStore();
        Assert.NotNull(loaded);
        Assert.Equal(RoamMode.Climb, loaded.Instances[0].RoamMode);
        Assert.Equal(110, loaded.Instances[0].Size);
        Assert.Equal("pet-abc", loaded.SelectedId);
    }

    [Fact]
    public void Positions_And_Visibility_RoundTrip()
    {
        var store = new FileJsonStore(_dir);

        store.SavePositions(new Dictionary<string, PetPosition> { ["pet-a"] = new(100, 200) });
        Assert.Equal(new PetPosition(100, 200), store.LoadPositions()["pet-a"]);

        store.SaveGlobalVisibility(false);
        Assert.False(store.LoadGlobalVisibility());
        Assert.Equal("0", File.ReadAllText(Path.Combine(_dir, "pets-visible")));
    }

    [Fact]
    public void MemoryProfile_RoundTrips()
    {
        // Phase 6b：记忆画像持久化 memory.json（记忆开关关 = App 层不调用 Save，不落盘）
        var store = new FileJsonStore(_dir);
        var profile = new UserProfile("小美", ["代码", "加班"], "深夜党", "最近提到项目上线");

        store.SaveMemoryProfile(profile);
        Assert.True(File.Exists(Path.Combine(_dir, "memory.json")));

        var loaded = store.LoadMemoryProfile();
        Assert.NotNull(loaded);
        Assert.Equal(profile.CallName, loaded.CallName);
        Assert.Equal(profile.Topics, loaded.Topics);   // xUnit 数组元素比较
        Assert.Equal(profile.Routine, loaded.Routine);
        Assert.Equal(profile.Summary, loaded.Summary);
    }

    [Fact]
    public void MemoryProfile_MissingFile_ReturnsNull()
    {
        var store = new FileJsonStore(_dir);
        Assert.Null(store.LoadMemoryProfile());
    }

    [Fact]
    public void IntimacyState_RoundTrips()
    {
        // Phase 6c：亲密度持久化 intimacy.json
        var store = new FileJsonStore(_dir);
        var state = new IntimacyState(68, new DateTime(2026, 8, 5, 21, 30, 0));

        store.SaveIntimacy(state);
        Assert.True(File.Exists(Path.Combine(_dir, "intimacy.json")));

        var loaded = store.LoadIntimacy();
        Assert.NotNull(loaded);
        Assert.Equal(state.Value, loaded.Value);
        Assert.Equal(state.LastInteractionDate, loaded.LastInteractionDate);
    }

    [Fact]
    public void IntimacyState_MissingFile_ReturnsNull()
    {
        var store = new FileJsonStore(_dir);
        Assert.Null(store.LoadIntimacy());
    }

    [Fact]
    public void DiaryMeta_RoundTrips()
    {
        // Phase 6f：日记元数据（diary-meta.json：最近生成的总结日期）
        var store = new FileJsonStore(_dir);
        Assert.Null(store.LoadDiaryLastGenerated());

        store.SaveDiaryLastGenerated(new DateOnly(2026, 8, 5));
        Assert.Equal(new DateOnly(2026, 8, 5), store.LoadDiaryLastGenerated());
    }

    [Fact]
    public void SaveFailure_PreservesOldFile_SurfacesError_AndCleansTemporaryFile()
    {
        var store = new FileJsonStore(_dir);
        store.SaveGlobalVisibility(true);
        string? temporaryPath = null;
        var failingStore = new FileJsonStore(
            _dir,
            new AtomicFilePublisher((temporary, _) =>
            {
                temporaryPath = temporary;
                throw new IOException("injected publish failure");
            }));

        var ex = Assert.Throws<JsonStoreException>(
            () => failingStore.SaveGlobalVisibility(false));

        Assert.Equal("写入", ex.Operation);
        Assert.Equal(Path.Combine(_dir, "pets-visible"), ex.FilePath);
        Assert.Equal("1", File.ReadAllText(ex.FilePath));
        Assert.NotNull(temporaryPath);
        Assert.False(File.Exists(temporaryPath));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void FirstSaveFailure_DoesNotPublishPartialFile()
    {
        var failingStore = new FileJsonStore(
            _dir,
            new AtomicFilePublisher((_, _) => throw new IOException("injected first publish failure")));

        Assert.Throws<JsonStoreException>(() =>
            failingStore.SaveDiaryLastGenerated(new DateOnly(2026, 8, 6)));

        Assert.False(File.Exists(Path.Combine(_dir, "diary-meta.json")));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ConcurrentWrites_PublishOneCompleteJsonDocument()
    {
        var stores = Enumerable.Range(0, 4)
            .Select(_ => new FileJsonStore(_dir))
            .ToArray();

        Parallel.For(0, 32, writer =>
        {
            var positions = Enumerable.Range(0, 100).ToDictionary(
                index => $"pet-{writer}-{index}",
                index => new PetPosition(writer, index));
            stores[writer % stores.Length].SavePositions(positions);
        });

        var loaded = stores[0].LoadPositions();
        Assert.Equal(100, loaded.Count);
        var writerPrefix = loaded.Keys.First().Split('-')[1];
        Assert.All(loaded.Keys, key => Assert.StartsWith($"pet-{writerPrefix}-", key));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void ReadIoFailure_IsObservable()
    {
        var store = new FileJsonStore(_dir);
        var settingsPath = Path.Combine(_dir, "app-settings.json");
        Directory.CreateDirectory(settingsPath);

        var ex = Assert.Throws<JsonStoreException>(() => store.LoadSettings());

        Assert.Equal("读取", ex.Operation);
        Assert.Equal(settingsPath, ex.FilePath);
    }

    [Fact]
    public void MalformedJson_StillReturnsDomainDefault()
    {
        var store = new FileJsonStore(_dir);
        File.WriteAllText(Path.Combine(_dir, "app-settings.json"), "{");

        Assert.Null(store.LoadSettings());
    }

    [Fact]
    public void SettingsHotkeys_MissingFieldUsesLegacyDefaults_AndExplicitUnboundRoundTrips()
    {
        var store = new FileJsonStore(_dir);
        var defaults = AppSettings.Defaults(DesktopPet.Core.I18n.AppLang.En);
        store.SaveSettings(defaults);
        var path = Path.Combine(_dir, "app-settings.json");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(legacy.Remove("hotkeys"));
        File.WriteAllText(path, legacy.ToJsonString());

        Assert.Equal(defaults.Hotkeys, store.LoadSettings()!.Hotkeys);

        var unbound = new DesktopPet.Core.Hotkeys.HotkeySettings(null, null, null, null);
        store.SaveSettings(defaults with { Hotkeys = unbound });
        Assert.Equal(unbound, store.LoadSettings()!.Hotkeys);
    }

    [Fact]
    public void MissingFiles_ReturnDefaults()
    {
        var store = new FileJsonStore(_dir);

        Assert.Null(store.LoadPetStore());
        Assert.Empty(store.LoadPositions());
        Assert.True(store.LoadGlobalVisibility());
    }
}

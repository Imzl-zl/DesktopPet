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
    public void MissingFiles_ReturnDefaults()
    {
        var store = new FileJsonStore(_dir);

        Assert.Null(store.LoadPetStore());
        Assert.Empty(store.LoadPositions());
        Assert.True(store.LoadGlobalVisibility());
    }
}

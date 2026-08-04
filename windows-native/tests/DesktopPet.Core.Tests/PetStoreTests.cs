using System.Text.Json;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Roaming;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 1:1 移植自 windows/src/pets.test.ts（vitest）。
/// </summary>
public class PetStoreTests
{
    private static PetInstance Pet(string id, string slug, string name) => new()
    {
        Id = id,
        Name = name,
        SpriteSlug = slug,
        Visible = true,
        Size = 100,
        RoamEnabled = true,
        RoamMode = RoamMode.Wander,
        RoamSpeed = 5,
        WanderPauseMinMs = Pause.DefaultWanderPauseMinMs,
        WanderPauseMaxMs = Pause.DefaultWanderPauseMaxMs,
        ReactsToActivity = false,
    };

    [Fact]
    public void MigratesTheLegacySelectedPetIntoOneNormalDesktopInstance()
    {
        var store = PetStoreModel.MigrateLegacyPetStore(null, new LegacyPet("cat", "Miso"));

        Assert.Equal("legacy-pet", store.SelectedId);
        var instance = Assert.Single(store.Instances);
        Assert.Equal(Pet("legacy-pet", "cat", "Miso") with { ReactsToActivity = true }, instance);
    }

    [Fact]
    public void KeepsAnExplicitlyEmptyInstanceStoreEmptyAfterTheLegacyMigration()
    {
        var empty = PetStoreModel.EmptyPetStore();

        var store = PetStoreModel.MigrateLegacyPetStore(empty, new LegacyPet("cat", "Miso"));

        Assert.Equal(empty, store);
    }

    [Fact]
    public void SelectsANewlyCreatedInstanceForImmediateEditing()
    {
        var store = PetStoreModel.EmptyPetStore();
        store = PetStoreModel.CreatePetInstance(store, Pet("miso", "cat", "Miso"));
        store = PetStoreModel.CreatePetInstance(store, Pet("nori", "cat", "Nori"));

        Assert.Equal("nori", store.SelectedId);
    }

    [Fact]
    public void KeepsIdenticalSpriteInstancesIndependentWhenOneIsRenamedOrRemoved()
    {
        var store = PetStoreModel.EmptyPetStore();
        store = PetStoreModel.CreatePetInstance(store, Pet("miso", "cat", "Miso"));
        store = PetStoreModel.CreatePetInstance(store, Pet("nori", "cat", "Nori"));
        store = PetStoreModel.UpdatePetInstance(store, "miso", new PetInstancePatch { Name = "Miso Prime", Size = 125 });

        Assert.Equal(2, store.Instances.Count);
        Assert.Equal(Pet("miso", "cat", "Miso") with { Name = "Miso Prime", Size = 125 }, store.Instances[0]);
        Assert.Equal(Pet("nori", "cat", "Nori"), store.Instances[1]);

        store = PetStoreModel.RemovePetInstance(store, "miso");
        Assert.Equal("nori", store.SelectedId);
        Assert.Equal(new[] { Pet("nori", "cat", "Nori") }, store.Instances);
    }

    [Fact]
    public void DefaultsALegacyInstancesMissingWanderPauseRangeToTheEstablishedBehavior()
    {
        var raw = JsonSerializer.Deserialize<JsonElement>("""
            {
              "version": 1,
              "selectedId": "miso",
              "instances": [
                {
                  "id": "miso",
                  "name": "Miso",
                  "spriteSlug": "cat",
                  "visible": true,
                  "size": 100,
                  "roamEnabled": true,
                  "roamMode": "wander",
                  "roamSpeed": 5,
                  "reactsToActivity": false
                }
              ]
            }
            """);

        var store = PetStoreModel.ParsePetStore(raw);

        Assert.NotNull(store);
        Assert.Equal(Pause.DefaultWanderPauseMinMs, store.Instances[0].WanderPauseMinMs);
        Assert.Equal(Pause.DefaultWanderPauseMaxMs, store.Instances[0].WanderPauseMaxMs);
    }
}

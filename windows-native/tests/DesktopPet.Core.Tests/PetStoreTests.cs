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
    public void PersonaId_RoundTripsThroughParseAndSerialize()
    {
        // Phase 6d：每宠物独立人格覆盖（空 = 跟随全局；旧 JSON 无 personaId 兼容）
        var store = PetStoreModel.CreatePetInstance(
            PetStoreModel.EmptyPetStore(),
            Pet("pet-a", "cat", "Miso") with { PersonaId = "puppy" });

        var json = System.Text.Json.JsonSerializer.Serialize(
            store, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var parsed = PetStoreModel.ParsePetStore(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal("puppy", parsed.Instances[0].PersonaId);

        // 旧 JSON 无 personaId → null（跟随全局）
        var legacyJson = "{\"version\":1,\"selectedId\":\"pet-a\",\"instances\":[{\"id\":\"pet-a\",\"name\":\"Miso\",\"spriteSlug\":\"cat\"}]}";
        using var legacyDoc = System.Text.Json.JsonDocument.Parse(legacyJson);
        var legacy = PetStoreModel.ParsePetStore(legacyDoc.RootElement);
        Assert.Null(legacy!.Instances[0].PersonaId);
    }

    [Fact]
    public void PersonaId_PatchUpdatesInstance()
    {
        var store = PetStoreModel.CreatePetInstance(
            PetStoreModel.EmptyPetStore(),
            Pet("pet-a", "cat", "Miso"));
        Assert.Null(store.Instances[0].PersonaId);

        var updated = PetStoreModel.UpdatePetInstance(store, "pet-a", new PetInstancePatch { PersonaId = "wolf-cub" });
        Assert.Equal("wolf-cub", updated.Instances[0].PersonaId);
    }

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

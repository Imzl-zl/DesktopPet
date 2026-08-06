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

    [Fact]
    public void Actions_RoundTripsThroughParseAndSerialize()
    {
        var actions = new PetAnimationSettings(
            IdleEnabled: true,
            IdleClips: [0, 2, 1],
            IdleMode: "sequential",
            IdleIntervalSeconds: 10,
            ClickDurationSeconds: 7,
            CelebrateDurationSeconds: 9,
            Bind: new Dictionary<string, int>
            {
                [PetActionTriggers.Click] = 3,
                [PetActionTriggers.Celebrate] = 4,
                [PetActionTriggers.Drag] = 7,
            });
        var store = PetStoreModel.CreatePetInstance(
            PetStoreModel.EmptyPetStore(),
            Pet("pet-a", "cat", "Miso") with { Actions = actions });

        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        using var doc = JsonDocument.Parse(json);
        var parsed = PetStoreModel.ParsePetStore(doc.RootElement);

        Assert.NotNull(parsed);
        var roundTripped = parsed.Instances[0].Actions;
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped.IdleEnabled);
        Assert.Equal([0, 2, 1], roundTripped.IdleClips);
        Assert.Equal("sequential", roundTripped.IdleMode);
        Assert.Equal(10, roundTripped.IdleIntervalSeconds);
        Assert.Equal(7, roundTripped.ClickDurationSeconds);
        Assert.Equal(9, roundTripped.CelebrateDurationSeconds);
        Assert.Equal(3, roundTripped.Bind[PetActionTriggers.Click]);
        Assert.Equal(4, roundTripped.Bind[PetActionTriggers.Celebrate]);
        Assert.Equal(7, roundTripped.Bind[PetActionTriggers.Drag]);
    }

    [Fact]
    public void Actions_MissingInLegacyJson_IsNullAndDefaultsApply()
    {
        var legacyJson = """
            {"version":1,"selectedId":"miso","instances":[
              {"id":"miso","name":"Miso","spriteSlug":"cat"}
            ]}
            """;
        using var doc = JsonDocument.Parse(legacyJson);
        var store = PetStoreModel.ParsePetStore(doc.RootElement);

        Assert.Null(store!.Instances[0].Actions);
        // 解析器默认策略：全 clip 随机 5s（素材 9 行时）
        var idle = PetAnimationResolver.ResolveIdle(null, 9);
        Assert.Equal(9, idle!.Clips.Count);
        Assert.Equal(5000, idle.IntervalMs);
        Assert.Equal(3, PetAnimationResolver.ResolveBind(null, PetActionTriggers.Click, 9));
        // 时长默认：点击 2s / 庆祝 3s
        Assert.Equal(2000, PetAnimationResolver.ResolveClickDurationMs(null));
        Assert.Equal(3000, PetAnimationResolver.ResolveCelebrateDurationMs(null));
    }

    [Fact]
    public void Actions_WithPartialJson_MissingDurationsFallBackToDefaults()
    {
        // 上一会话后的旧 JSON：有 actions 但无时长字段 → 缺省回退默认（2s/3s），不破坏旧文件
        var json = """
            {"version":1,"instances":[
              {"id":"a","name":"A","spriteSlug":"cat","actions":{
                "idleEnabled":true,"idleClips":[0,1],"idleMode":"random",
                "idleIntervalSeconds":10,"bind":{"click":3}}}
            ]}
            """;
        using var doc = JsonDocument.Parse(json);
        var store = PetStoreModel.ParsePetStore(doc.RootElement);

        var actions = store!.Instances[0].Actions;
        Assert.NotNull(actions);
        Assert.Equal(PetAnimationResolver.DefaultClickDurationSeconds, actions.ClickDurationSeconds);
        Assert.Equal(PetAnimationResolver.DefaultCelebrateDurationSeconds, actions.CelebrateDurationSeconds);
        Assert.Equal(2000, PetAnimationResolver.ResolveClickDurationMs(actions));
        Assert.Equal(3000, PetAnimationResolver.ResolveCelebrateDurationMs(actions));
    }

    [Fact]
    public void Actions_PatchUpdatesOnlyTargetInstance()
    {
        var store = PetStoreModel.EmptyPetStore();
        store = PetStoreModel.CreatePetInstance(store, Pet("miso", "cat", "Miso"));
        store = PetStoreModel.CreatePetInstance(store, Pet("nori", "cat", "Nori"));

        var actions = new PetAnimationSettings(
            IdleEnabled: false,
            IdleClips: [],
            IdleMode: "random",
            IdleIntervalSeconds: 5,
            ClickDurationSeconds: 2,
            CelebrateDurationSeconds: 3,
            Bind: new Dictionary<string, int> { [PetActionTriggers.Click] = 2 });
        store = PetStoreModel.UpdatePetInstance(store, "miso", new PetInstancePatch { Actions = actions });

        var updated = store.Instances[0].Actions;
        Assert.NotNull(updated);
        Assert.False(updated.IdleEnabled);
        Assert.Empty(updated.IdleClips);
        Assert.Equal("random", updated.IdleMode);
        Assert.Equal(5, updated.IdleIntervalSeconds);
        Assert.Equal(2, updated.Bind[PetActionTriggers.Click]);
        Assert.Null(store.Instances[1].Actions);
    }

    [Fact]
    public void Actions_InvalidJsonFields_AreSanitizedNotDropped()
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
                  "actions": {
                    "idleEnabled": true,
                    "idleClips": [0, 1, -5, 99],
                    "idleMode": "weird",
                    "idleIntervalSeconds": 1000,
                    "bind": { "click": -1, "drag": 4 }
                  }
                }
              ]
            }
            """);

        var store = PetStoreModel.ParsePetStore(raw);

        Assert.NotNull(store);
        var actions = store.Instances[0].Actions;
        Assert.NotNull(actions);
        // Normalize 只清洗负数；越界索引由解析器按 clipCount 过滤
        Assert.Equal([0, 1, 99], actions.IdleClips);
        Assert.Equal("random", actions.IdleMode);
        Assert.Equal(60, actions.IdleIntervalSeconds);
        Assert.False(actions.Bind.ContainsKey(PetActionTriggers.Click));
        Assert.Equal(4, actions.Bind[PetActionTriggers.Drag]);
        // 解析器按实际 clip 数过滤越界项
        var idle = PetAnimationResolver.ResolveIdle(actions, 3);
        Assert.Equal([0, 1], idle!.Clips);
    }
}

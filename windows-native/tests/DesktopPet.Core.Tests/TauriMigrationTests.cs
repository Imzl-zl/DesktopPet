using System.Text.Json;
using DesktopPet.Core.Care;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Storage;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>迁移工具测试：Tauri localStorage 导出 → 新版 store + care。</summary>
public class TauriMigrationTests
{
    private static readonly DateTime Now = new(2025, 1, 15, 12, 0, 0);

    private static JsonElement ExportJson(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Migrates_LegacySlug_IntoDefaultInstance_WithCareState()
    {
        var export = ExportJson("""
            {
              "desktoppet.petSlug": "\"cat\"",
              "ap_care": "{\"cat\":{\"xp\":75,\"totalMeals\":3,\"tokenCarry\":0,\"tokensToday\":0,\"mealsToday\":0,\"totalTokens\":0,\"lastFedAt\":null,\"dayKey\":\"2025-01-15\",\"streakDays\":0,\"lastFedDayKey\":null,\"days\":{},\"unlockedAchievements\":[]}}"
            }
            """);

        var result = TauriMigration.Migrate(export, Now);

        Assert.True(result.HadData);
        Assert.Equal("legacy-pet", result.Store.SelectedId);
        Assert.Equal("cat", result.Store.Instances[0].SpriteSlug);
        Assert.Equal(75, result.Care["legacy-pet"].Xp);
        Assert.Equal(3, result.Care["legacy-pet"].TotalMeals);
        Assert.False(result.Care.ContainsKey("cat"));
    }

    [Fact]
    public void Migrates_InstanceStore_WhenPresent()
    {
        var export = ExportJson("""
            {
              "desktoppet.petSlug": "\"cat\"",
              "ap_pet_instances": "{\"version\":1,\"selectedId\":\"pet-a\",\"instances\":[{\"id\":\"pet-a\",\"name\":\"Miso\",\"spriteSlug\":\"nori-openpets\",\"visible\":true,\"size\":100,\"roamEnabled\":true,\"roamMode\":\"wander\",\"roamSpeed\":5,\"reactsToActivity\":true}]}",
              "ap_care": "{\"pet-a\":{\"xp\":120}}"
            }
            """);

        var result = TauriMigration.Migrate(export, Now);

        Assert.Equal("pet-a", result.Store.SelectedId);
        Assert.Equal("nori-openpets", result.Store.Instances[0].SpriteSlug);
        Assert.Equal(120, result.Care["pet-a"].Xp);
    }

    [Fact]
    public void Migrate_EmptyExport_ReturnsEmptyStore()
    {
        var result = TauriMigration.Migrate(ExportJson("{}"), Now);

        Assert.False(result.HadData);
        Assert.Empty(result.Store.Instances);
        Assert.Empty(result.Care);
    }

    [Fact]
    public void Migrate_HandlesMalformedCare()
    {
        var export = ExportJson("""{"ap_care": "not json", "desktoppet.petSlug": "\"cat\""}""");

        var result = TauriMigration.Migrate(export, Now);

        Assert.Empty(result.Care);
        Assert.Equal("legacy-pet", result.Store.SelectedId);
    }
}

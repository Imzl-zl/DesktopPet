using DesktopPet.Core.Care;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>1:1 移植自 windows/src/care.test.ts + care.ts 全量语义。</summary>
public class CareEngineTests
{
    private static readonly DateTime Now = new(2025, 1, 15, 12, 0, 0, DateTimeKind.Local);

    private static CareState State(double xp = 0, double totalMeals = 0)
    {
        var s = CareEngine.EmptyState(Now);
        s.Xp = xp;
        s.TotalMeals = totalMeals;
        return s;
    }

    // ---- care.test.ts 1:1 ----

    [Fact]
    public void MigratesLegacySpriteKeyedCareState_ToMigratedInstanceId()
    {
        var legacy = CareEngine.EmptyState(Now);
        legacy.Xp = 75;
        legacy.TotalMeals = 3;
        var states = new Dictionary<string, CareState> { ["cat"] = legacy };

        var migrated = CareStoreModel.MigrateLegacyCareState(states, "cat", "legacy-pet");

        Assert.Equal(75, migrated["legacy-pet"].Xp);
        Assert.Equal(3, migrated["legacy-pet"].TotalMeals);
        Assert.False(migrated.ContainsKey("cat"));
    }

    [Fact]
    public void Migration_DoesNotOverwriteCareAlreadyAccumulatedByNewInstance()
    {
        var legacy = CareEngine.EmptyState(Now);
        legacy.Xp = 75;
        var live = CareEngine.EmptyState(Now);
        live.Xp = 120;
        var states = new Dictionary<string, CareState> { ["cat"] = legacy, ["legacy-pet"] = live };

        var migrated = CareStoreModel.MigrateLegacyCareState(states, "cat", "legacy-pet");

        Assert.Equal(120, migrated["legacy-pet"].Xp);
        Assert.False(migrated.ContainsKey("cat"));
    }

    // ---- 等级数学 ----

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 120)]
    [InlineData(3, 360)]
    [InlineData(5, 1200)]
    [InlineData(10, 5400)]
    public void XpToReach_MatchesFormula(int level, double expected)
    {
        Assert.Equal(expected, CareEngine.XpToReach(level));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(119, 1)]
    [InlineData(120, 2)]
    [InlineData(5400, 10)]
    public void LevelForXp_MapsThresholds(double xp, int expected)
    {
        Assert.Equal(expected, CareEngine.LevelForXp(xp));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0)]
    [InlineData(1200, 4)]   // 展示等级 = 内部等级-1
    public void DisplayLevel_IsInternalMinusOne(double xp, int expected)
    {
        Assert.Equal(expected, CareEngine.DisplayLevel(xp));
    }

    [Theory]
    [InlineData(1, 0, "Hatchling")]
    [InlineData(5, 1, "Companion")]
    [InlineData(10, 2, "Scout")]
    [InlineData(20, 3, "Hero")]
    [InlineData(35, 4, "Legend")]
    public void StageIndex_And_StageName_FiveStages(int level, int expectedIndex, string expectedName)
    {
        Assert.Equal(expectedIndex, CareEngine.StageIndex(level));
        Assert.Equal(expectedName, CareEngine.StageName(level));
    }

    [Fact]
    public void LevelProgress_IsFractionThroughCurrentLevel()
    {
        Assert.Equal(0, CareEngine.LevelProgress(0));
        Assert.Equal(0.5, CareEngine.LevelProgress(60)); // 0→120 的中点
        Assert.Equal(0.9999916667, CareEngine.LevelProgress(119.999), precision: 6);
        Assert.Equal(0, CareEngine.LevelProgress(120)); // 新等级起点
    }

    // ---- 饥饿 ----

    [Fact]
    public void Hunger_ScalesWithHoursSinceLastFeed()
    {
        var s = State();
        Assert.Equal(Hunger.Peckish, CareEngine.HungerAt(s, Now)); // 从未喂过

        s.LastFedAt = Now.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;
        Assert.Equal(Hunger.Full, CareEngine.HungerAt(s, Now.AddHours(3)));
        Assert.Equal(Hunger.Satisfied, CareEngine.HungerAt(s, Now.AddHours(9)));
        Assert.Equal(Hunger.Peckish, CareEngine.HungerAt(s, Now.AddHours(23)));
        Assert.Equal(Hunger.Hungry, CareEngine.HungerAt(s, Now.AddHours(47)));
        Assert.Equal(Hunger.Starving, CareEngine.HungerAt(s, Now.AddHours(72)));
    }

    // ---- token 经济学 ----

    [Fact]
    public void FeedTokens_AccruesXpAt5000PerWithCarry()
    {
        var s = State();
        var gained = CareEngine.FeedTokens(s, 12_000, Now);

        Assert.Equal(2, gained);
        Assert.Equal(2, s.Xp);
        Assert.Equal(2000, s.TokenCarry);
        Assert.Equal(12_000, s.TotalTokens);
    }

    [Fact]
    public void RecordMeal_Grants25Xp()
    {
        var s = State();
        var gained = CareEngine.RecordMeal(s, Now);

        Assert.Equal(25, gained);
        Assert.Equal(25, s.Xp);
        Assert.Equal(1, s.TotalMeals);
        Assert.Equal(1, s.MealsToday);
    }

    [Fact]
    public void Feeding_BuildsStreakAcrossConsecutiveDays()
    {
        var s = State();
        CareEngine.FeedTokens(s, 1000, new DateTime(2025, 1, 13, 10, 0, 0));
        CareEngine.FeedTokens(s, 1000, new DateTime(2025, 1, 14, 10, 0, 0));

        Assert.Equal(2, s.StreakDays);

        CareEngine.FeedTokens(s, 1000, new DateTime(2025, 1, 16, 10, 0, 0)); // 跳过一天

        Assert.Equal(1, s.StreakDays);
    }

    [Fact]
    public void Feeding_ResetsDailyCountersOnRollover_AndKeeps14Days()
    {
        var s = State();
        CareEngine.FeedTokens(s, 1000, new DateTime(2025, 1, 1, 10, 0, 0));
        CareEngine.FeedTokens(s, 2000, new DateTime(2025, 1, 2, 10, 0, 0));

        Assert.Equal(0, s.TokensToday + s.MealsToday - 2000); // 第二天 2000
        Assert.Equal(2, s.Days.Count);

        // 15 天窗口裁剪
        for (var i = 3; i <= 18; i++)
        {
            CareEngine.FeedTokens(s, 100, new DateTime(2025, 1, i, 10, 0, 0));
        }
        Assert.Equal(14, s.Days.Count);
        Assert.False(s.Days.ContainsKey("2025-01-01"));
        Assert.True(s.Days.ContainsKey("2025-01-18"));
    }

    // ---- 成就 ----

    [Fact]
    public void Achievements_UnlockByThresholds()
    {
        var s = State(xp: 0, totalMeals: 1);
        s.TotalTokens = 1_000_000;
        s.StreakDays = 7;

        var newly = CareEngine.UnlockNewAchievements(s, Now);

        Assert.Contains("firstMeal", newly);
        Assert.Contains("tokens1M", newly);
        Assert.Contains("streak7", newly);
        Assert.DoesNotContain("nightOwl", newly); // hour=12 → 不解锁
    }

    [Fact]
    public void NightOwl_UnlocksOnlyBefore6Am()
    {
        var s = State(totalMeals: 1);

        var day = CareEngine.UnlockNewAchievements(s, new DateTime(2025, 1, 15, 12, 0, 0));
        Assert.DoesNotContain("nightOwl", day);

        var night = CareEngine.UnlockNewAchievements(s, new DateTime(2025, 1, 15, 5, 0, 0));
        Assert.Contains("nightOwl", night);
    }

    [Fact]
    public void LevelAchievements_TrackDisplayLevel()
    {
        var s = State(xp: CareEngine.XpToReach(6)); // 展示等级 5
        var newly = CareEngine.UnlockNewAchievements(s, Now);

        Assert.Contains("level5", newly);
        Assert.DoesNotContain("level10", newly);
    }

    // ---- 最近天数 ----

    [Fact]
    public void RecentDays_ReturnsOldestFirst_WithZeroes()
    {
        var s = State();
        s.Days["2025-01-13"] = 500;
        s.Days["2025-01-15"] = 800;

        var days = CareEngine.RecentDays(s, 3, new DateTime(2025, 1, 15, 12, 0, 0));

        Assert.Equal(3, days.Length);
        Assert.Equal(("13", 500.0), days[0]);
        Assert.Equal(("14", 0.0), days[1]);
        Assert.Equal(("15", 800.0), days[2]);
    }

    [Fact]
    public void TokensToNextLevel_AccountsForCarry()
    {
        var s = State(xp: 0);
        Assert.Equal(120 * CareEngine.TokensPerXp, CareEngine.TokensToNextLevel(s));

        s.TokenCarry = 3000;
        Assert.Equal(120 * CareEngine.TokensPerXp - 3000, CareEngine.TokensToNextLevel(s));
    }
}

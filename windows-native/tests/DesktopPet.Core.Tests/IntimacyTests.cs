using DesktopPet.Core.Care;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 6c：亲密度系统（feature-research P0 ③；架构文档 §10 决策点 3）。
/// 0-100 双线并行：XP=外观/行为（CareEngine），亲密度=AI 关系（IntimacyEngine）。
/// 增长：对话轮次加权 + token 少量加成 + 连续天数；长期不互动缓慢下降不归零。
/// 档位 → 称呼/语气修饰指令（开关关 = 固定人格基础档）。
/// </summary>
public class IntimacyTests
{
    private static readonly DateTime Day0 = new(2026, 8, 1, 20, 0, 0);

    private static IntimacyEngine New(int value, DateTime lastInteraction)
        => new(new IntimacyState(value, lastInteraction));

    // ---- 增长 ----

    [Fact]
    public void RecordConversation_AddsBasePointsPerTurn()
    {
        var e = New(0, Day0);
        e.RecordConversation(tokensUsed: 0, now: Day0.AddHours(1));
        Assert.Equal(2, e.State.Value);
        e.RecordConversation(tokensUsed: 0, now: Day0.AddHours(2));
        Assert.Equal(4, e.State.Value);
    }

    [Fact]
    public void RecordConversation_TokenBonus_IsCapped()
    {
        var e = New(0, Day0);
        e.RecordConversation(tokensUsed: 50000, now: Day0.AddHours(1)); // 50000/2500=20 → 封顶 +3
        Assert.Equal(5, e.State.Value); // 2 + 3
    }

    [Fact]
    public void RecordConversation_StreakBonus_OnConsecutiveDay()
    {
        var e = New(10, Day0); // 昨天互动过
        e.RecordConversation(tokensUsed: 0, now: Day0.AddDays(1));
        Assert.Equal(15, e.State.Value); // 10 + 2 + 3(连续天数)
    }

    [Fact]
    public void RecordConversation_NoStreakBonus_AfterGap()
    {
        var e = New(10, Day0); // 上次互动是 3 天前
        e.RecordConversation(tokensUsed: 0, now: Day0.AddDays(3));
        Assert.Equal(10, e.State.Value); // 10 - 2(中间 2 个整天衰减) + 2，无连续加成
    }

    [Fact]
    public void RecordConversation_RepeatedSameDay_NoDoubleStreak()
    {
        var e = New(10, Day0);
        e.RecordConversation(tokensUsed: 0, now: Day0.AddHours(1));
        e.RecordConversation(tokensUsed: 0, now: Day0.AddHours(2)); // 同日第二轮
        Assert.Equal(14, e.State.Value); // 10 + 2 + 2（同日无重复 streak）
    }

    [Fact]
    public void RecordConversation_CapsAt100()
    {
        var e = New(98, Day0);
        e.RecordConversation(tokensUsed: 0, now: Day0.AddHours(1));
        Assert.Equal(100, e.State.Value);
    }

    // ---- 衰减 ----

    [Fact]
    public void Decay_MissingDays_DecreasesButNeverReachesZero()
    {
        var e = New(50, Day0);
        // 3 天未互动（中间 2 个完整整天每天 -1）
        e.RecordConversation(tokensUsed: 0, now: Day0.AddDays(3));
        Assert.Equal(50, e.State.Value); // 50 - 2 + 2
    }

    [Fact]
    public void Decay_LongAbsence_FloorsAt5()
    {
        var e = New(20, Day0);
        e.RecordConversation(tokensUsed: 0, now: Day0.AddDays(30));
        Assert.Equal(7, e.State.Value); // 20 - 29(衰减触底 5) + 2，地板不归零
    }

    [Fact]
    public void Decay_NoInteractionSinceLastTurn_NoChange()
    {
        var e = New(50, Day0);
        // 同一天互动
        e.RecordConversation(tokensUsed: 0, now: Day0.AddHours(1));
        Assert.Equal(52, e.State.Value);
    }

    // ---- 档位 ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(19, 0)]
    [InlineData(20, 1)]
    [InlineData(39, 1)]
    [InlineData(40, 2)]
    [InlineData(69, 2)]
    [InlineData(70, 3)]
    [InlineData(100, 3)]
    public void Level_Boundaries(int value, int expectedLevel)
    {
        Assert.Equal(expectedLevel, new IntimacyEngine(new IntimacyState(value, Day0)).Level);
    }

    [Fact]
    public void LevelNames_AreOrdered()
    {
        var names = new[] { "陌生", "熟悉", "亲近", "亲密" };
        for (var level = 0; level < 4; level++)
        {
            var e = new IntimacyEngine(new IntimacyState(level * 20 + 10, Day0));
            Assert.Equal(names[level], e.LevelName);
        }
    }

    // ---- 语气指令 ----

    [Fact]
    public void BuildDirective_DifferentLevels_Differ()
    {
        var lv0 = new IntimacyEngine(new IntimacyState(0, Day0));
        var lv3 = new IntimacyEngine(new IntimacyState(80, Day0));
        var d0 = lv0.BuildIntimacyDirective();
        var d3 = lv3.BuildIntimacyDirective();
        Assert.NotEqual(d0, d3);
        Assert.Contains("陌生", d0);
        Assert.Contains("亲密", d3);
    }

    [Fact]
    public void BuildDirective_Level3_UsesIntimateTone()
    {
        var e = new IntimacyEngine(new IntimacyState(80, Day0));
        var d = e.BuildIntimacyDirective();
        Assert.Contains("亲昵", d);
        Assert.Contains("称呼", d);
    }

    [Fact]
    public void BuildDirective_Disabled_ReturnsEmpty()
    {
        var e = new IntimacyEngine(new IntimacyState(80, Day0));
        Assert.Equal("", e.BuildIntimacyDirective(enabled: false));
    }
}

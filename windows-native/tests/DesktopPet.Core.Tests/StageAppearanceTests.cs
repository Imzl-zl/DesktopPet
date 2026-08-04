using DesktopPet.Core.Care;
using Xunit;

namespace DesktopPet.Core.Tests;

/// <summary>五阶段表现逐级验证（迁移计划 §3.7，测试宠物直接注入 XP）。</summary>
public class StageAppearanceTests
{
    [Theory]
    [InlineData(0, "Hatchling", false, false, false, false)]
    [InlineData(1, "Companion", true, false, false, false)]
    [InlineData(2, "Scout", true, true, false, false)]
    [InlineData(3, "Hero", true, true, false, true)]
    [InlineData(4, "Legend", true, true, true, true)]
    public void Appearance_EscalatesPerStage(
        int stage, string name, bool glowUnder, bool glowOutline, bool crown, bool stars)
    {
        var a = StageAppearances.For(stage);

        Assert.Equal(name, a.StageName);
        Assert.Equal(glowUnder, a.GlowUnder);
        Assert.Equal(glowOutline, a.GlowOutline);
        Assert.Equal(crown, a.Crown);
        Assert.Equal(stars, a.StarParticles);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "mint")]
    [InlineData(2, "sky")]
    [InlineData(3, "gold")]
    [InlineData(4, "gold")]
    public void GlowColor_StartsWithNeutral(int stage, string? expected)
    {
        Assert.Equal(expected, StageAppearances.For(stage).GlowColor);
    }

    [Theory]
    [InlineData(0, false, false, 0.8)]
    [InlineData(1, true, false, 0.9)]
    [InlineData(2, true, true, 1.0)]
    [InlineData(3, true, true, 1.0)]
    [InlineData(4, true, true, 1.0)]
    public void Capabilities_UnlockModesByStage(int stage, bool cursor, bool climb, double speed)
    {
        var c = StageCapabilitiesFor.For(stage);

        Assert.Equal(cursor, c.CursorMode);
        Assert.Equal(climb, c.ClimbMode);
        Assert.Equal(speed, c.SpeedFactor);
    }

    [Fact]
    public void HeroAndLegend_AreMoreResponsiveAndChatty()
    {
        var hero = StageCapabilitiesFor.For(3);
        var legend = StageCapabilitiesFor.For(4);

        Assert.True(hero.ClickResponseFactor < 1);
        Assert.True(hero.BubbleFrequency > 1);
        Assert.True(legend.ClickResponseFactor < hero.ClickResponseFactor);
        Assert.True(legend.BubbleFrequency > hero.BubbleFrequency);
    }

    [Fact]
    public void XpInjection_DrivesStageAcrossFullRange()
    {
        // 测试宠物直接注入 XP，遍历五阶段
        var now = new DateTime(2025, 1, 15, 12, 0, 0);
        var xpLevels = new[] { 0.0, CareEngine.XpToReach(5), CareEngine.XpToReach(10), CareEngine.XpToReach(20), CareEngine.XpToReach(35) };

        for (var stage = 0; stage < 5; stage++)
        {
            var s = CareEngine.EmptyState(now);
            s.Xp = xpLevels[stage];
            var level = CareEngine.LevelForXp(s.Xp);
            Assert.Equal(stage, CareEngine.StageIndex(level));
        }
    }
}

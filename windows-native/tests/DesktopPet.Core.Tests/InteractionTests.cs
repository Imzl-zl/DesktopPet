using DesktopPet.Core.Ai;
using DesktopPet.Core.Interaction;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 6d：主动互动引擎（feature-research P0 ②；架构文档 §3.3 调度器 + §10 决策点 4）。
/// 定时（早晚问候/深夜）+ 事件驱动（久坐 60min/持续编码 2h/窗口切换/摸鱼）；
/// 频率档少/中/多；屏幕感知开关（关 = 仅定时问候）；多宠物分派 round-robin 竞争 / 全员回应。
/// </summary>
public class InteractionTests
{
    private static readonly DateTime Morning = new(2026, 8, 5, 9, 0, 0);
    private static readonly DateTime Evening = new(2026, 8, 5, 22, 0, 0);

    private static InteractionEngine New(string frequency = "medium", bool screenAwareness = true, DateTime? now = null)
        => new(new InteractionEngineState(LastGreetDate: null, LastEventAt: null), frequency, screenAwareness);

    private static ScreenEvent Ev(ScreenEventKind kind, DateTime at, string summary = "s")
        => new(at, kind, summary);

    // ---- 定时问候 ----

    [Fact]
    public void MorningGreeting_TriggeredInWindow_OncePerDay()
    {
        var e = New();
        Assert.True(e.TryNextTrigger(Morning, [], out var t1));
        Assert.Equal("morning", t1!.Reason);

        // 同一天再 tick 不再触发
        Assert.False(e.TryNextTrigger(Morning.AddHours(1), [], out _));
        // 第二天重新触发
        Assert.True(e.TryNextTrigger(Morning.AddDays(1), [], out var t2));
        Assert.Equal("morning", t2!.Reason);
    }

    [Fact]
    public void EveningGreeting_TriggeredInWindow()
    {
        var e = New();
        Assert.True(e.TryNextTrigger(Evening, [], out var t));
        Assert.Equal("evening", t!.Reason);
    }

    [Fact]
    public void LateNight_TriggeredAfter23()
    {
        var e = New();
        var late = new DateTime(2026, 8, 5, 23, 30, 0);
        Assert.True(e.TryNextTrigger(late, [], out var t));
        Assert.Equal("late-night", t!.Reason);
    }

    [Fact]
    public void NoGreeting_OutsideWindows()
    {
        var e = New();
        var noon = new DateTime(2026, 8, 5, 12, 0, 0);
        Assert.False(e.TryNextTrigger(noon, [], out _));
    }

    // ---- 事件驱动 ----

    [Fact]
    public void SittingReminder_After60MinContinuousActivity()
    {
        var e = New();
        var now = new DateTime(2026, 8, 5, 12, 0, 0); // 中午（无问候窗口，隔离冷却断言）
        var start = now.AddHours(-1);
        var events = new[] { Ev(ScreenEventKind.Coding, start), Ev(ScreenEventKind.Coding, start.AddMinutes(30)) };
        Assert.True(e.TryNextTrigger(now, events, out var t));
        Assert.Equal("sitting", t!.Reason);
        // 触发后冷却期内不重复
        Assert.False(e.TryNextTrigger(now.AddMinutes(5), events, out _));
    }

    [Fact]
    public void SittingReminder_NotBefore60Min()
    {
        var e = New();
        var start = new DateTime(2026, 8, 5, 11, 30, 0); // 中午（无问候窗口，隔离事件判定）
        var events = new[] { Ev(ScreenEventKind.Coding, start) };
        Assert.False(e.TryNextTrigger(start.AddMinutes(30), events, out _));
    }

    [Fact]
    public void CodingComment_After2HoursContinuousCoding()
    {
        var e = New();
        var start = Morning.AddHours(-2);
        var events = new[] { Ev(ScreenEventKind.Coding, start), Ev(ScreenEventKind.Coding, start.AddHours(1)) };
        Assert.True(e.TryNextTrigger(Morning, events, out var t));
        Assert.Equal("coding", t!.Reason);
    }

    [Fact]
    public void AppSwitch_TriggersEventComment()
    {
        var e = New();
        var events = new[] { Ev(ScreenEventKind.AppSwitch, Morning.AddMinutes(-5), "切换到了浏览器") };
        Assert.True(e.TryNextTrigger(Morning, events, out var t));
        Assert.Equal("app-switch", t!.Reason);
    }

    [Fact]
    public void ScreenAwarenessOff_NoEventDriven_ButTimedStillWorks()
    {
        var e = New(screenAwareness: false);
        var events = new[] { Ev(ScreenEventKind.AppSwitch, Morning.AddMinutes(-5)) };
        var noon = new DateTime(2026, 8, 5, 12, 0, 0); // 中午（无问候窗口）
        Assert.False(e.TryNextTrigger(noon, events, out _)); // 事件驱动关闭

        // 定时问候仍可用
        Assert.True(e.TryNextTrigger(Evening, [], out var t));
        Assert.Equal("evening", t!.Reason);
    }

    // ---- 频率档 ----

    [Fact]
    public void FrequencyLow_ThrottlesEventComments()
    {
        var e = New(frequency: "low");
        var now = new DateTime(2026, 8, 5, 12, 0, 0);
        var events = new[] { Ev(ScreenEventKind.AppSwitch, now.AddMinutes(-1)) };
        Assert.True(e.TryNextTrigger(now, events, out _));
        // 冷却期内不重复（low：事件评论 4h 一次）
        Assert.False(e.TryNextTrigger(now.AddMinutes(10), events, out _));
    }

    [Fact]
    public void FrequencyHigh_AllowsMoreFrequentComments()
    {
        var e = New(frequency: "high");
        var now = new DateTime(2026, 8, 5, 12, 0, 0);
        var events = new[] { Ev(ScreenEventKind.AppSwitch, now.AddMinutes(-1)) };
        Assert.True(e.TryNextTrigger(now, events, out _));
        // high：30min 冷却；40min 后新事件可再触发（旧事件已出 10min 窗口）
        var later = now.AddMinutes(40);
        var newEvents = new[] { Ev(ScreenEventKind.AppSwitch, later.AddMinutes(-1)) };
        Assert.True(e.TryNextTrigger(later, newEvents, out _));
    }

    [Fact]
    public void FrequencyHigh_StillThrottledWithinCooldown()
    {
        var e = New(frequency: "high");
        var now = new DateTime(2026, 8, 5, 12, 0, 0);
        var events = new[] { Ev(ScreenEventKind.AppSwitch, now.AddMinutes(-1)) };
        Assert.True(e.TryNextTrigger(now, events, out _));
        Assert.False(e.TryNextTrigger(now.AddMinutes(10), events, out _));
    }

    // ---- 多宠物分派 ----

    [Fact]
    public void Dispatcher_RoundRobin_RotatesAcrossPets()
    {
        var d = new PetInteractionDispatcher();
        var pets = new[] { "a", "b", "c" };
        var first = d.SelectSpeakers(pets, allReply: false, rng: new Random(1));
        var second = d.SelectSpeakers(pets, allReply: false, rng: new Random(1));
        Assert.NotEqual(first, second); // 轮换：两次选择游标不同
    }

    [Fact]
    public void Dispatcher_AllReply_ReturnsAllPets()
    {
        var d = new PetInteractionDispatcher();
        var pets = new[] { "a", "b", "c" };
        var speakers = d.SelectSpeakers(pets, allReply: true, rng: new Random(1));
        Assert.Equal(pets, speakers);
    }

    [Fact]
    public void Dispatcher_Default_SelectsOneOrTwo()
    {
        var d = new PetInteractionDispatcher();
        var pets = new[] { "a", "b", "c", "d", "e" };
        for (var i = 0; i < 20; i++)
        {
            var speakers = d.SelectSpeakers(pets, allReply: false, rng: new Random(i));
            Assert.InRange(speakers.Count, 1, 2);
            Assert.All(speakers, id => Assert.Contains(id, pets));
        }
    }

    [Fact]
    public void Dispatcher_SinglePet_AlwaysSelected()
    {
        var d = new PetInteractionDispatcher();
        var speakers = d.SelectSpeakers(["only"], allReply: false, rng: new Random(1));
        Assert.Equal(["only"], speakers);
    }

    [Fact]
    public void Dispatcher_NoPets_ReturnsEmpty()
    {
        var d = new PetInteractionDispatcher();
        Assert.Empty(d.SelectSpeakers([], allReply: false, rng: new Random(1)));
    }

    // ---- 开关 ----

    [Fact]
    public void ActiveInteractionDisabled_NoTriggersAtAll()
    {
        var e = New();
        e.SetEnabled(false);
        Assert.False(e.TryNextTrigger(Morning, [], out _));
        Assert.False(e.TryNextTrigger(Evening, [], out _));
        var events = new[] { Ev(ScreenEventKind.AppSwitch, Morning.AddMinutes(-1)) };
        Assert.False(e.TryNextTrigger(Morning, events, out _));
    }
}

using DesktopPet.Core.Ai;
using DesktopPet.Core.Summary;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 行为会话化（2026-08-11：屏幕事件按天 journal 落盘 + 归并，总结还原"当天做了什么"）。
/// </summary>
public class ActivitySessionBuilderTests
{
    private static ScreenEvent Evt(DateTime t, ScreenEventKind kind, string summary = "x")
        => new(t, kind, summary);

    [Fact]
    public void Build_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ActivitySessionBuilder.Build([]));
    }

    [Fact]
    public void Build_ConsecutiveSameKind_MergesIntoSingleSession()
    {
        var start = new DateTime(2026, 8, 11, 9, 0, 0);
        var sessions = ActivitySessionBuilder.Build(
        [
            Evt(start, ScreenEventKind.Coding, "开始写代码"),
            Evt(start.AddMinutes(30), ScreenEventKind.Coding, "在写单元测试"),
            Evt(start.AddMinutes(90), ScreenEventKind.Coding, "提交了代码"),
        ]);

        var s = Assert.Single(sessions);
        Assert.Equal(ScreenEventKind.Coding, s.Kind);
        Assert.Equal(start, s.Start);
        Assert.Equal(start.AddMinutes(90), s.End);
        Assert.Equal("提交了代码", s.Summary); // 评论取最新（会话结束状态）
        Assert.Equal(3, s.EventCount);
    }

    [Fact]
    public void Build_KindSwitch_StartsNewSession()
    {
        var start = new DateTime(2026, 8, 11, 14, 0, 0);
        var sessions = ActivitySessionBuilder.Build(
        [
            Evt(start, ScreenEventKind.Coding, "写代码"),
            Evt(start.AddMinutes(10), ScreenEventKind.Video, "在看视频"),
            Evt(start.AddMinutes(20), ScreenEventKind.Coding, "切回写代码"),
        ]);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(ScreenEventKind.Coding, sessions[0].Kind);
        Assert.Equal(ScreenEventKind.Video, sessions[1].Kind);
        Assert.Equal(ScreenEventKind.Coding, sessions[2].Kind); // 不跨 kind 合并
        Assert.Equal(1, sessions[1].EventCount);
    }

    [Fact]
    public void Build_PreservesInputOrder()
    {
        var sessions = ActivitySessionBuilder.Build(
        [
            Evt(new DateTime(2026, 8, 11, 8, 0, 0), ScreenEventKind.Browsing),
            Evt(new DateTime(2026, 8, 11, 9, 0, 0), ScreenEventKind.Coding),
            Evt(new DateTime(2026, 8, 11, 10, 0, 0), ScreenEventKind.Idle),
        ]);

        Assert.Equal(
            [ScreenEventKind.Browsing, ScreenEventKind.Coding, ScreenEventKind.Idle],
            sessions.Select(s => s.Kind));
    }
}

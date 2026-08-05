using DesktopPet.Core.Memory;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 6b：记忆系统（feature-research P0 ①；架构文档 §10 决策点 2）。
/// 结构化画像 4 字段起步（称呼/作息/话题/摘要 ≤200 字），纯规则提取（零模型成本、确定性可测）。
/// </summary>
public class MemoryStoreTests
{
    private static (ChatMessage, DateTime) Turn(ChatRole role, string text, int hour, int minute = 0)
        => (new ChatMessage(role, text), new DateTime(2026, 8, 5, hour, minute, 0));

    private static UserProfile Extract(params (ChatMessage, DateTime)[] turns)
        => MemoryProfileExtractor.Extract(turns);

    // ---- 称呼提取 ----

    [Fact]
    public void Extract_ExplicitCallName_IsCaptured()
    {
        var p = Extract(
            Turn(ChatRole.User, "你可以叫我小美", 20),
            Turn(ChatRole.Assistant, "好的小美~", 20));
        Assert.Equal("小美", p.CallName);
    }

    [Fact]
    public void Extract_NoCallName_IsEmpty()
    {
        var p = Extract(Turn(ChatRole.User, "今天好累", 20));
        Assert.Equal("", p.CallName);
    }

    [Fact]
    public void Extract_LatestCallName_Wins()
    {
        var p = Extract(
            Turn(ChatRole.User, "叫我阿伟吧", 20),
            Turn(ChatRole.User, "还是叫我小伟", 21));
        Assert.Equal("小伟", p.CallName);
    }

    // ---- 话题提取 ----

    [Fact]
    public void Extract_TopicKeywords_AreCountedAndRanked()
    {
        var p = Extract(
            Turn(ChatRole.User, "又在加班写代码，项目要上线了", 22),
            Turn(ChatRole.User, "加班到两点，代码还是没跑通", 23),
            Turn(ChatRole.User, "明天继续加班", 23));
        Assert.Contains("加班", p.Topics);
        Assert.Contains("代码", p.Topics);
        Assert.True(p.Topics.Length <= 3);
        Assert.Equal("加班", p.Topics[0]); // 频次最高排第一
    }

    [Fact]
    public void Extract_NoTopicMatch_IsEmpty()
    {
        var p = Extract(Turn(ChatRole.User, "哈哈哈哈哈", 20));
        Assert.Empty(p.Topics);
    }

    // ---- 作息推断 ----

    [Fact]
    public void Extract_NightOwl_RoutineDetected()
    {
        var p = Extract(
            Turn(ChatRole.User, "又到这个点", 23),
            Turn(ChatRole.User, "深夜写代码", 1),
            Turn(ChatRole.User, "还在", 2));
        Assert.Contains("深夜", p.Routine);
        Assert.Contains("23", p.Routine);
    }

    [Fact]
    public void Extract_MorningPerson_RoutineDetected()
    {
        var p = Extract(
            Turn(ChatRole.User, "早", 7),
            Turn(ChatRole.User, "早上好", 8),
            Turn(ChatRole.User, "开工", 9));
        Assert.Contains("早晨", p.Routine);
    }

    [Fact]
    public void Extract_NoTurns_RoutineEmpty()
    {
        Assert.Equal("", Extract().Routine);
    }

    // ---- 摘要压缩 ----

    [Fact]
    public void Compress_KeepsUserMessages_UnderMaxChars()
    {
        var turns = new[]
        {
            Turn(ChatRole.User, "今天项目上线特别顺利，大家都松了口气", 20),
            Turn(ChatRole.User, "晚上和朋友吃了火锅，很开心", 21),
            Turn(ChatRole.User, "明天打算去健身房", 22),
        };
        var summary = MemoryProfileExtractor.Compress(turns, maxChars: 60);
        Assert.True(summary.Length <= 60, $"摘要超长: {summary.Length}");
        Assert.Contains("上线", summary);
        Assert.Contains("火锅", summary);
    }

    [Fact]
    public void Compress_EmptyHistory_IsEmpty()
    {
        Assert.Equal("", MemoryProfileExtractor.Compress([], maxChars: 200));
    }

    [Fact]
    public void Compress_AssistantOnly_IsEmpty()
    {
        var turns = new[] { Turn(ChatRole.Assistant, "好的呢", 20) };
        Assert.Equal("", MemoryProfileExtractor.Compress(turns, maxChars: 200));
    }

    // ---- 注入 ----

    [Fact]
    public void Inject_FullProfile_FormatsAllFields()
    {
        var p = new UserProfile("小美", ["代码", "加班"], "深夜党", "最近提到项目上线");
        var text = MemoryProfileExtractor.Inject(p);
        Assert.Contains("小美", text);
        Assert.Contains("深夜党", text);
        Assert.Contains("代码", text);
        Assert.Contains("项目上线", text);
    }

    [Fact]
    public void Inject_EmptyProfile_ReturnsEmpty()
    {
        Assert.Equal("", MemoryProfileExtractor.Inject(new UserProfile("", [], "", "")));
    }

    [Fact]
    public void Inject_PartialProfile_OmitsEmptyFields()
    {
        var p = new UserProfile("", ["代码"], "", "");
        var text = MemoryProfileExtractor.Inject(p);
        Assert.DoesNotContain("称呼", text);
        Assert.Contains("代码", text);
    }
}

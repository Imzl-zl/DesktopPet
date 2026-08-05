using DesktopPet.Core.Ai;
using DesktopPet.Core.Memory;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Tests;

/// <summary>
/// 分层会话记忆（L1 工作区 + L2 滚动摘要）：预算内全保留；超预算不丢弃、
/// 压缩进摘要注入；重开对话清空；摘要可被上层合并进画像（L3）。
/// </summary>
public class ConversationMemoryTests
{
    [Fact]
    public void BuildContext_WithinBudget_KeepsAllMessagesNoSummary()
    {
        var mem = new ConversationMemory();
        mem.Append("你好", "嗨～");
        mem.Append("今天好累", "辛苦了，休息下吧");

        var ctx = mem.BuildContext(contextTokens: 32768);

        Assert.Equal(4, ctx.Count);              // 全部保留
        Assert.DoesNotContain(ctx, m => m.Content!.Contains("摘要"));
        Assert.Equal("", mem.Summary);
    }

    [Fact]
    public void BuildContext_OverBudget_CompressesDroppedTurnsIntoSummary()
    {
        var mem = new ConversationMemory();
        for (var i = 0; i < 10; i++)
        {
            // 长消息确保远超预算下限（每轮 ≈ 400 字 ≈ 600 token）
            mem.Append($"第{i}轮：我喜欢喝咖啡和茶，" + new string('聊', 120), "回复：好的记住了～" + new string('好', 60));
        }

        var ctx = mem.BuildContext(contextTokens: 2048); // 预算下限 1024 token

        Assert.True(mem.Summary.Length > 0, "超预算部分应压缩进摘要");
        Assert.True(ctx.Count < 20, "上下文应裁剪");
        Assert.Contains(ctx, m => m.Role == ChatRole.System && m.Content!.Contains("摘要"));
        // 最新一条永远保留
        Assert.Contains(ctx, m => m.Content!.Contains("第9轮"));
    }

    [Fact]
    public void BuildContext_RepeatedOverBudget_RollsSummaryForward()
    {
        var mem = new ConversationMemory();
        for (var i = 0; i < 6; i++) mem.Append($"A{i}" + new string('聊', 100), "B" + new string('好', 50));
        mem.BuildContext(contextTokens: 2048);
        var firstSummary = mem.Summary;
        Assert.NotEqual("", firstSummary);

        for (var i = 6; i < 12; i++) mem.Append($"A{i}" + new string('聊', 100), "B" + new string('好', 50));
        mem.BuildContext(contextTokens: 2048);

        // 摘要滚动合并：新旧内容都在（不再只是旧摘要）
        Assert.NotEqual(firstSummary, mem.Summary);
        Assert.True(mem.Summary.Length <= 200, "摘要 ≤200 字（Compress 上限）");
    }

    [Fact]
    public void Clear_ResetsMessagesAndSummary()
    {
        var mem = new ConversationMemory();
        mem.Append("hi", "hello");
        mem.BuildContext(contextTokens: 1024); // 触发摘要
        mem.Clear();

        Assert.Equal(0, mem.Count);
        Assert.Equal("", mem.Summary);
        Assert.Empty(mem.BuildContext(32768));
    }

    [Fact]
    public void BuildContext_TinyBudget_KeepsAtLeastLatestMessage()
    {
        var mem = new ConversationMemory();
        for (var i = 0; i < 5; i++) mem.Append($"消息{i}内容内容内容", "回复回复回复");

        var ctx = mem.BuildContext(contextTokens: 1); // 预算下限 1024

        Assert.Contains(ctx, m => m.Content!.Contains("消息4")); // 最新保留
        Assert.True(ctx.Count >= 1);
    }
}

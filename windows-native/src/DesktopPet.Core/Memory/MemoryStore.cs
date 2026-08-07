using System.Text.RegularExpressions;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Memory;

/// <summary>
/// 用户画像（feature-research P0 ①；架构文档 §10 决策点 2：4 字段起步，摘要 ≤200 字）。
/// 结构化记忆：称呼 / 作息规律 / 常聊话题 / 最近对话摘要。
/// 存储于 memory.json；记忆开关关闭时 App 层不保存不注入（画像文件不落盘）。
/// </summary>
public sealed record UserProfile(
    string CallName,   // 称呼（空 = 未提取）
    string[] Topics,   // 常聊话题标签（按频次排序，最多 3 个）
    string Routine,    // 作息规律描述（空 = 无足够样本）
    string Summary);   // 最近对话摘要（≤200 字，空 = 无样本）

/// <summary>
/// 画像提取 + 摘要压缩 + 注入（纯规则实现：零模型成本、确定性、可单测）。
/// 提取输入为带时间戳的对话轮次；注入文本随每轮请求携带（架构文档 §4 管道第③步）。
/// </summary>
public static class MemoryProfileExtractor
{
    private const int MaxTopics = 3;
    /// <summary>最近对话摘要长度上限（≤200 字，架构文档 §10 决策点 2；单一真值，
    /// ConversationMemory 引用此处）。</summary>
    internal const int SummaryMaxChars = 200;
    private const int SummaryPerMessageChars = 40;

    /// <summary>预设话题词库（中文短词匹配；覆盖高频闲聊主题，可后续扩充）。</summary>
    private static readonly string[] TopicKeywords =
    [
        "加班", "代码", "工作", "上线", "项目", "开会", "面试", "简历",
        "游戏", "学习", "考试", "读书", "运动", "健身", "减肥",
        "吃饭", "美食", "火锅", "睡觉", "失眠", "电影", "音乐", "追剧",
        "旅行", "宠物", "家人", "朋友", "买房", "房租", "健康",
    ];

    private static readonly Regex CallNamePattern = new(
        @"(?:叫我|喊我|可以叫我|你可以叫我|叫我名字|我叫|名字叫|你可以喊我)\s*[「『""'“]?\s*([\u4e00-\u9fa5A-Za-z0-9]{1,6})",
        RegexOptions.Compiled);

    /// <summary>从带时间戳的对话轮次提取画像（只统计用户消息）。</summary>
    public static UserProfile Extract(IEnumerable<(ChatMessage Message, DateTime Time)> turns)
    {
        var list = turns.ToList();
        var userTurns = list.Where(t => t.Message.Role == ChatRole.User).ToList();

        var callName = ExtractCallName(userTurns);
        var topics = ExtractTopics(userTurns);
        var routine = ExtractRoutine(userTurns);
        var summary = Compress(list, SummaryMaxChars);
        return new UserProfile(callName, topics, routine, summary);
    }

    /// <summary>最近对话压缩摘要（只含用户消息，逐条截断，总长 ≤ maxChars）。</summary>
    public static string Compress(IReadOnlyList<(ChatMessage Message, DateTime Time)> turns, int maxChars = SummaryMaxChars)
    {
        var userMessages = turns
            .Where(t => t.Message.Role == ChatRole.User)
            .Select(t => t.Message.Content.Trim())
            .Where(c => c.Length > 0)
            .ToList();
        if (userMessages.Count == 0) return "";

        var parts = new List<string>();
        foreach (var content in userMessages)
        {
            var line = content.Replace('\n', ' ').Trim();
            if (line.Length > SummaryPerMessageChars) line = line[..SummaryPerMessageChars] + "…";
            parts.Add(line);
        }

        var joined = string.Join("；", parts);
        return joined.Length <= maxChars ? joined : joined[..maxChars] + "…";
    }

    /// <summary>画像 → 注入文本（每轮请求携带；空字段省略；全空返回空串不注入）。</summary>
    public static string Inject(UserProfile profile)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.CallName)) lines.Add($"称呼：{profile.CallName}");
        if (!string.IsNullOrWhiteSpace(profile.Routine)) lines.Add($"作息：{profile.Routine}");
        if (profile.Topics.Length > 0) lines.Add($"常聊话题：{string.Join("、", profile.Topics)}");
        if (!string.IsNullOrWhiteSpace(profile.Summary)) lines.Add($"最近：{profile.Summary}");
        return lines.Count == 0 ? "" : "[关于用户的记忆]\n" + string.Join("\n", lines);
    }

    /// <summary>画像归一化（持久化/加载用）：去空、去重、截断摘要 ≤200 字。</summary>
    public static UserProfile Normalize(UserProfile? raw)
    {
        if (raw is null) return new UserProfile("", [], "", "");
        var topics = (raw.Topics ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTopics)
            .ToArray();
        var summary = raw.Summary ?? "";
        if (summary.Length > SummaryMaxChars) summary = summary[..SummaryMaxChars] + "…";
        return new UserProfile(
            raw.CallName?.Trim() ?? "",
            topics,
            raw.Routine?.Trim() ?? "",
            summary);
    }

    private static string ExtractCallName(IReadOnlyList<(ChatMessage Message, DateTime Time)> userTurns)
    {
        foreach (var turn in userTurns.AsEnumerable().Reverse())
        {
            var match = CallNamePattern.Match(turn.Message.Content);
            if (match.Success) return match.Groups[1].Value;
        }
        return "";
    }

    private static string[] ExtractTopics(IReadOnlyList<(ChatMessage Message, DateTime Time)> userTurns)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var turn in userTurns)
        {
            var text = turn.Message.Content;
            foreach (var keyword in TopicKeywords)
            {
                if (text.Contains(keyword, StringComparison.Ordinal))
                    counts[keyword] = counts.GetValueOrDefault(keyword) + 1;
            }
        }
        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => Array.IndexOf(TopicKeywords, kv.Key))
            .Take(MaxTopics)
            .Select(kv => kv.Key)
            .ToArray();
    }

    private static string ExtractRoutine(IReadOnlyList<(ChatMessage Message, DateTime Time)> userTurns)
    {
        if (userTurns.Count == 0) return "";
        var nightCount = userTurns.Count(t => t.Time.Hour >= 23 || t.Time.Hour < 5);
        if (nightCount * 100 / userTurns.Count >= 40)
            return "深夜党（常活跃于 23 点后）";

        var buckets = new (string Name, int Count)[]
        {
            ("早晨", userTurns.Count(t => t.Time.Hour is >= 5 and < 11)),
            ("下午", userTurns.Count(t => t.Time.Hour is >= 11 and < 17)),
            ("晚上", userTurns.Count(t => t.Time.Hour is >= 17 and < 23)),
        };
        var best = buckets.OrderByDescending(b => b.Count).First();
        return best.Count == 0 ? "" : $"常在{best.Name}活跃";
    }
}

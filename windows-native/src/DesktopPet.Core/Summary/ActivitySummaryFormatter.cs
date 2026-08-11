using System.Text;
using DesktopPet.Core.Ai;

namespace DesktopPet.Core.Summary;

/// <summary>
/// 活动段 → 模型可读文本（每日总结的屏幕部分）。
/// 格式：每段一行 "HH:mm-HH:mm [Kind] 评论"；kind 用枚举英文名（模型能理解，不引入本地化）。
/// 预算截断：超过 maxSessions 段时保留最近段并在开头注明总段数。
/// </summary>
public static class ActivitySummaryFormatter
{
    public static string Format(IReadOnlyList<ActivitySession> sessions, int maxSessions = 40)
    {
        if (sessions.Count == 0 || maxSessions <= 0) return "";

        var sb = new StringBuilder();
        if (sessions.Count > maxSessions)
        {
            sb.Append("（当天共 ").Append(sessions.Count).Append(" 段活动，显示最近 ").Append(maxSessions).Append(" 段）");
        }
        foreach (var s in sessions.Skip(Math.Max(0, sessions.Count - maxSessions)))
        {
            sb.Append('\n').Append(s.Start.ToString("HH:mm")).Append('-')
              .Append(s.End.ToString("HH:mm")).Append(" [").Append(s.Kind).Append(']');
            if (!string.IsNullOrWhiteSpace(s.Summary)) sb.Append(' ').Append(s.Summary);
        }
        return sb.ToString();
    }
}

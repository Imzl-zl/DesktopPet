using System.Text;

namespace DesktopPet.Core.Ai;

/// <summary>
/// 屏幕事件 → 模型可读文本（架构文档 §4 管道第 ④ 步）。
/// 只取最近 N 条；空则返回空串（调用方据此跳过上下文注入）。
/// </summary>
public static class ScreenContextFormatter
{
    public static string Format(IReadOnlyList<ScreenEvent> events, int maxEvents)
    {
        if (events.Count == 0 || maxEvents <= 0) return "";
        var sb = new StringBuilder();
        sb.Append("屏幕上下文（最近 ").Append(Math.Min(maxEvents, events.Count)).Append(" 条）：");
        foreach (var e in events.Skip(Math.Max(0, events.Count - maxEvents)))
        {
            sb.Append('\n').Append("- ").Append(e.Timestamp.ToString("HH:mm"))
              .Append(" [").Append(e.Kind).Append("] ").Append(e.Summary);
        }
        return sb.ToString();
    }
}

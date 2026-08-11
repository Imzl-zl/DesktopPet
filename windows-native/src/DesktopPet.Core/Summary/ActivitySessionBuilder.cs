using DesktopPet.Core.Ai;

namespace DesktopPet.Core.Summary;

/// <summary>
/// 行为会话化（纯逻辑，可单测）：把按时间升序的屏幕事件流压缩为"活动段"。
/// 规则：连续同 kind 合并为一段（结束时间与评论取最新，计数累加）；kind 切换开新段。
/// 事件必须按时间升序输入（journal 按写入顺序读回即升序）。
/// </summary>
public static class ActivitySessionBuilder
{
    public static List<ActivitySession> Build(IEnumerable<ScreenEvent> events)
    {
        var sessions = new List<ActivitySession>();
        foreach (var evt in events)
        {
            if (sessions.Count > 0 && sessions[^1].Kind == evt.Kind)
            {
                var last = sessions[^1];
                sessions[^1] = last with
                {
                    End = evt.Timestamp,
                    Summary = evt.Summary,
                    EventCount = last.EventCount + 1,
                };
            }
            else
            {
                sessions.Add(new ActivitySession(evt.Timestamp, evt.Timestamp, evt.Kind, evt.Summary, 1));
            }
        }
        return sessions;
    }
}

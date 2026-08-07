using DesktopPet.Core.Ai;
using DesktopPet.Core.Storage;

namespace DesktopPet.Core.Interaction;

/// <summary>一次主动互动触发（定时问候或事件驱动评论）。</summary>
public sealed record InteractionTrigger(string Reason, string PromptContext, DateTime At);

/// <summary>引擎状态（会话内记忆；持久化非必需——错过问候不补发）。</summary>
public sealed record InteractionEngineState(DateTime? LastGreetDate, DateTime? LastEventAt);

/// <summary>
/// 主动互动引擎（feature-research P0 ②；架构文档 §10 决策点 4）。
/// 触发顺序：事件驱动（app-switch → sitting → coding）优先于定时（late-night → morning → evening），
/// 因为"此刻发生的事"比例行问候更具体。
/// 频率档冷却：low 4h / medium 2h / high 30min（事件评论）；问候每天一次。
/// 屏幕感知关：跳过全部事件驱动，定时问候仍可用。
/// </summary>
public sealed class InteractionEngine
{
    // 频率档名字符串单一真值在 AiSettings（设置层）；此处引用避免双份定义。
    private static readonly TimeSpan SittingThreshold = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan CodingThreshold = TimeSpan.FromHours(2);
    private static readonly TimeSpan AppSwitchWindow = TimeSpan.FromMinutes(10);

    private string _frequency;
    private bool _screenAwareness;
    private InteractionEngineState _state;
    private bool _enabled = true;
    private bool _quietHoursEnabled;
    private int _quietHoursStart;
    private int _quietHoursEnd;

    public InteractionEngine(InteractionEngineState state, string frequency, bool screenAwareness)
    {
        _state = state ?? new InteractionEngineState(null, null);
        _frequency = NormalizeFrequency(frequency);
        _screenAwareness = screenAwareness;
    }

    /// <summary>设置变更时更新（保留问候/冷却状态）。</summary>
    public void UpdateFrequency(string frequency) => _frequency = NormalizeFrequency(frequency);

    public void UpdateScreenAwareness(bool enabled) => _screenAwareness = enabled;

    /// <summary>免打扰时段（默认关：保持现有问候行为；开启后在时段内不产生任何主动互动）。</summary>
    public void UpdateQuietHours(bool enabled, int start, int end)
    {
        _quietHoursEnabled = enabled;
        _quietHoursStart = Math.Clamp(start, 0, 23);
        _quietHoursEnd = Math.Clamp(end, 0, 23);
    }

    private static string NormalizeFrequency(string frequency) => frequency switch
    {
        Storage.AiSettings.FrequencyLow => Storage.AiSettings.FrequencyLow,
        Storage.AiSettings.FrequencyHigh => Storage.AiSettings.FrequencyHigh,
        _ => Storage.AiSettings.FrequencyMedium,
    };

    public void SetEnabled(bool enabled) => _enabled = enabled;

    private TimeSpan EventCooldown => _frequency switch
    {
        Storage.AiSettings.FrequencyLow => TimeSpan.FromHours(4),
        Storage.AiSettings.FrequencyHigh => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(2),
    };

    /// <summary>尝试产生一次触发；无触发返回 false。</summary>
    public bool TryNextTrigger(DateTime now, IReadOnlyList<ScreenEvent> recentEvents, out InteractionTrigger? trigger)
    {
        trigger = null;
        if (!_enabled) return false;
        // 免打扰时段：不产生任何主动互动（定时问候 + 事件评论）
        if (_quietHoursEnabled && Storage.AiSettings.IsInQuietHours(now.Hour, _quietHoursStart, _quietHoursEnd))
            return false;

        // 1) 事件驱动（需屏幕感知）
        if (_screenAwareness && recentEvents.Count > 0)
        {
            if (TryEventTrigger(now, recentEvents, out trigger)) return true;
        }

        // 2) 定时问候（每天一次）
        return TryTimedGreeting(now, out trigger);
    }

    private bool TryEventTrigger(DateTime now, IReadOnlyList<ScreenEvent> events, out InteractionTrigger? trigger)
    {
        trigger = null;
        if (_state.LastEventAt is { } last && now - last < EventCooldown) return false;

        var recent = events.Where(e => now - e.Timestamp <= AppSwitchWindow).ToList();

        // coding：连续编码 ≥ 2h（最早 Coding 事件 ≥ 阈值前）——最具体先查
        var coding = events.Where(e => e.Kind == ScreenEventKind.Coding).ToList();
        if (coding.Count > 0 && now - coding.Min(e => e.Timestamp) >= CodingThreshold)
        {
            trigger = new InteractionTrigger("coding", "用户已连续编码超过两小时", now);
            _state = _state with { LastEventAt = now };
            return true;
        }

        // sitting：连续活动 ≥ 60min（最早活动事件 ≥ 阈值前）
        var active = events.Where(e => e.Kind is ScreenEventKind.Coding or ScreenEventKind.Browsing
            or ScreenEventKind.Video or ScreenEventKind.Gaming).ToList();
        if (active.Count > 0)
        {
            var earliest = active.Min(e => e.Timestamp);
            if (now - earliest >= SittingThreshold)
            {
                trigger = new InteractionTrigger("sitting", "用户已连续工作/使用电脑超过一小时，提醒休息", now);
                _state = _state with { LastEventAt = now };
                return true;
            }
        }

        // app-switch：最近 10min 内切换窗口
        if (recent.Any(e => e.Kind == ScreenEventKind.AppSwitch))
        {
            var ev = recent.Last(e => e.Kind == ScreenEventKind.AppSwitch);
            trigger = new InteractionTrigger("app-switch", $"用户最近切换到了{ev.Summary}", now);
            _state = _state with { LastEventAt = now };
            return true;
        }

        return false;
    }

    private bool TryTimedGreeting(DateTime now, out InteractionTrigger? trigger)
    {
        trigger = null;
        var today = now.Date;
        if (_state.LastGreetDate == today) return false;

        var hour = now.Hour;
        if (hour >= 23 || hour < 5)
        {
            trigger = new InteractionTrigger("late-night", "深夜了用户还没睡，关心一下", now);
        }
        else if (hour is >= 8 and < 10)
        {
            trigger = new InteractionTrigger("morning", "早上好，向用户道早安", now);
        }
        else if (hour is >= 21 and < 23)
        {
            trigger = new InteractionTrigger("evening", "晚上好，问候用户今天过得怎么样", now);
        }
        else
        {
            return false;
        }

        _state = _state with { LastGreetDate = today };
        return true;
    }
}

/// <summary>
/// 多宠物分派（feature-research §2 ② 关键设计）：
/// 同一事件各自表达——每只被选中的宠物独立生成（并行请求，人格不混淆）。
/// 默认 round-robin 竞争选 1-2 只；全员回应返回全部。
/// </summary>
public sealed class PetInteractionDispatcher
{
    private int _cursor;

    public IReadOnlyList<string> SelectSpeakers(
        IReadOnlyList<string> petIds, bool allReply, Random? rng = null)
    {
        if (petIds.Count == 0) return [];
        if (allReply) return petIds.ToArray();

        var r = rng ?? Random.Shared;
        var count = petIds.Count == 1 ? 1 : r.Next(1, Math.Min(3, petIds.Count + 1)); // 1-2 只
        var selected = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            selected.Add(petIds[(_cursor + i) % petIds.Count]);
        }
        _cursor = (_cursor + count) % petIds.Count;
        return selected;
    }
}

using DesktopPet.Core.Rendering;

namespace DesktopPet.Core.Pets;

/// <summary>动作触发器键（设置页「动作」分段；持久化为 pet-store.json 的 actions.bind 键）。</summary>
public static class PetActionTriggers
{
    public const string Idle = "idle";
    public const string Click = "click";
    public const string Celebrate = "celebrate";
    public const string RoamLeft = "roamLeft";
    public const string RoamRight = "roamRight";
    public const string Drag = "drag";
}

/// <summary>
/// 每宠物动作配置（pet-store.json 的 actions 字段；null = 旧数据/未配置 →
/// <see cref="PetAnimationResolver"/> 按默认策略解析，不破坏旧文件）。
/// 唯一持久化所有者：用户选择优先；无绑定/越界/空列表才回退。
/// </summary>
public sealed record PetAnimationSettings(
    bool IdleEnabled,                    // 待机轮播开关（关 = 只播 idle 绑定行）
    IReadOnlyList<int> IdleClips,        // 待机播放列表（去重；解析时过滤越界）
    string IdleMode,                     // random | sequential
    int IdleIntervalSeconds,             // 1-60
    int ClickDurationSeconds,            // 点击动作行播放时长（1-10s，默认 2）
    int CelebrateDurationSeconds,        // 庆祝动作行/气泡时长（1-10s，默认 3）
    IReadOnlyDictionary<string, int> Bind); // trigger → clip 行（越界在解析时回退）

/// <summary>
/// 动作解析器（纯函数，UI 与运行时共用同一回退语义）：
/// - 未配置（null）→ 默认策略：idle 全 clip 随机 5s；绑定回退语义行表。
/// - 配置越界/空列表 → 过滤后回退，不抛出、不产生空播放列表。
/// </summary>
public static class PetAnimationResolver
{
    public const int DefaultIdleIntervalSeconds = 5;
    public const int MinIdleIntervalSeconds = 1;
    public const int MaxIdleIntervalSeconds = 60;
    public const int MinIdleClips = 1; // 不变量：启用后至少一个有效 clip
    public const int DefaultClickDurationSeconds = 2;
    public const int DefaultCelebrateDurationSeconds = 3;
    public const int MinDurationSeconds = 1;
    public const int MaxDurationSeconds = 10;

    /// <summary>默认绑定（对齐 StateMapping 语义行：idle 0 / done 3 / celebrate 4；行走行 1/2）。</summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultBind = new Dictionary<string, int>
    {
        [PetActionTriggers.Idle] = 0,
        [PetActionTriggers.Click] = 3,
        [PetActionTriggers.Celebrate] = 4,
        [PetActionTriggers.RoamLeft] = 2,
        [PetActionTriggers.RoamRight] = 1,
        // drag 默认无绑定：拖拽时保持当前动作
    };

    /// <summary>解析待机播放列表；clipCount &lt;= 0 → null（无素材不播放）。</summary>
    public static IdlePlaylistOptions? ResolveIdle(PetAnimationSettings? actions, int clipCount)
    {
        if (clipCount <= 0) return null;

        if (actions is null)
        {
            // 旧数据/新导入默认：全部有效行参与随机轮播（对齐现有工作区行为）
            return new IdlePlaylistOptions(
                Clips: Enumerable.Range(0, clipCount).ToList(),
                IntervalMs: DefaultIdleIntervalSeconds * 1000.0,
                Random: true);
        }
        if (!actions.IdleEnabled) return null;

        var clips = actions.IdleClips
            .Where(clip => clip >= 0 && clip < clipCount)
            .Distinct()
            .ToList();
        if (clips.Count < MinIdleClips)
        {
            // 不变量：启用后至少一个有效 clip → 回退 idle 绑定行
            clips = [Clamp(ResolveBind(actions, PetActionTriggers.Idle, clipCount) ?? 0, 0, clipCount - 1)];
        }

        var interval = Clamp(actions.IdleIntervalSeconds, MinIdleIntervalSeconds, MaxIdleIntervalSeconds) * 1000.0;
        return new IdlePlaylistOptions(clips, interval, Random: actions.IdleMode != "sequential");
    }

    /// <summary>解析触发器绑定行；未配置/越界 → null（运行时回退状态默认行）。</summary>
    public static int? ResolveBind(PetAnimationSettings? actions, string trigger, int clipCount)
    {
        if (actions is null)
        {
            if (!DefaultBind.TryGetValue(trigger, out var defaultRow)) return null;
            return defaultRow >= 0 && defaultRow < clipCount ? defaultRow : null;
        }
        if (!actions.Bind.TryGetValue(trigger, out var boundRow) || boundRow < 0 || boundRow >= clipCount) return null;
        return boundRow;
    }

    /// <summary>点击动作行播放时长（毫秒）；未配置/非法 → 默认 2s。</summary>
    public static double ResolveClickDurationMs(PetAnimationSettings? actions)
        => Clamp(actions?.ClickDurationSeconds ?? DefaultClickDurationSeconds,
            MinDurationSeconds, MaxDurationSeconds) * 1000.0;

    /// <summary>庆祝动作行/气泡时长（毫秒）；未配置/非法 → 默认 3s。</summary>
    public static double ResolveCelebrateDurationMs(PetAnimationSettings? actions)
        => Clamp(actions?.CelebrateDurationSeconds ?? DefaultCelebrateDurationSeconds,
            MinDurationSeconds, MaxDurationSeconds) * 1000.0;

    /// <summary>持久化归一化：非法值钳制/回退，保证存储与解析器一致（UI 保存前调用）。</summary>
    public static PetAnimationSettings Normalize(PetAnimationSettings? raw)
    {
        if (raw is null)
        {
            return new PetAnimationSettings(
                IdleEnabled: true,
                IdleClips: [],
                IdleMode: "random",
                IdleIntervalSeconds: DefaultIdleIntervalSeconds,
                ClickDurationSeconds: DefaultClickDurationSeconds,
                CelebrateDurationSeconds: DefaultCelebrateDurationSeconds,
                Bind: new Dictionary<string, int>());
        }
        var bind = raw.Bind
            .Where(pair => pair.Value >= 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return new PetAnimationSettings(
            raw.IdleEnabled,
            raw.IdleClips.Where(clip => clip >= 0).Distinct().ToList(),
            raw.IdleMode == "sequential" ? "sequential" : "random",
            Clamp(raw.IdleIntervalSeconds, MinIdleIntervalSeconds, MaxIdleIntervalSeconds),
            Clamp(raw.ClickDurationSeconds, MinDurationSeconds, MaxDurationSeconds),
            Clamp(raw.CelebrateDurationSeconds, MinDurationSeconds, MaxDurationSeconds),
            bind);
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}

namespace DesktopPet.Core.Care;

/// <summary>
/// 亲密度状态（持久化 intimacy.json）。
/// 双线并行：XP=外观/行为（CareEngine），亲密度=AI 关系（IntimacyEngine）。
/// 衰减只依赖 LastInteractionDate（距上次互动每整天 -1，下限 5 不归零）。
/// </summary>
public sealed record IntimacyState(int Value, DateTime LastInteractionDate)
{
    public static IntimacyState Defaults => new(0, DateTime.Today);
}

/// <summary>
/// 亲密度引擎（feature-research P0 ③；架构文档 §10 决策点 3）。
/// 0-100：对话轮次加权（+2/轮）+ token 少量加成（每 2500 token +1，封顶 +3/轮）
/// + 连续天数（隔天互动 +3）；长期不互动每天 -1（下限 5）。
/// 档位（0-3）→ 称呼/语气修饰指令，注入人格 SystemPrompt（开关关 = 固定人格基础档）。
/// </summary>
public sealed class IntimacyEngine
{
    public const int MaxValue = 100;
    public const int DecayFloor = 5;
    public const int BasePerTurn = 2;
    public const int StreakBonus = 3;
    public const int TokenBonusStep = 2500;
    public const int TokenBonusCap = 3;

    public IntimacyState State { get; private set; }

    public IntimacyEngine(IntimacyState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>对话结算：先衰减（距上次互动每整天 -1，下限 5），再增长（轮次 + token + 连续天数），封顶 100。</summary>
    public void RecordConversation(int tokensUsed, DateTime now)
    {
        var today = now.Date;
        var lastDate = State.LastInteractionDate.Date;

        // 衰减：上次互动后的每个完整整天 -1（昨天互动今天互动不衰减）；
        // 地板仅作用于真实衰减（未衰减的自然低值不被抬升）
        var missedDays = Math.Max(0, (today - lastDate).Days - 1);
        var decayed = State.Value - missedDays;
        var afterDecay = missedDays > 0 ? Math.Max(DecayFloor, decayed) : Math.Max(0, decayed);

        // 连续天数：昨天互动过 → +3（同一天多次互动不重复）
        var streak = lastDate == today.AddDays(-1) ? StreakBonus : 0;

        // 轮次基础 + token 少量加成
        var tokenBonus = Math.Min(TokenBonusCap, tokensUsed / TokenBonusStep);

        var next = Math.Min(MaxValue, afterDecay + BasePerTurn + tokenBonus + streak);
        State = new IntimacyState(next, today);
    }

    /// <summary>档位：0 陌生(0-19) / 1 熟悉(20-39) / 2 亲近(40-69) / 3 亲密(70-100)。</summary>
    public int Level => State.Value switch
    {
        >= 70 => 3,
        >= 40 => 2,
        >= 20 => 1,
        _ => 0,
    };

    public string LevelName => Level switch
    {
        3 => "亲密",
        2 => "亲近",
        1 => "熟悉",
        _ => "陌生",
    };

    /// <summary>
    /// 档位 → 称呼/语气修饰指令（追加到人格 SystemPrompt）。
    /// 开关关（enabled: false）= 返回空串，固定人格基础档。
    /// </summary>
    public string BuildIntimacyDirective(bool enabled = true)
    {
        if (!enabled) return "";
        return Level switch
        {
            3 => "你们已经非常亲密：使用最亲昵的称呼（如宝贝），语气大胆表达爱意、心疼与占有，关心频率最高。",
            2 => "你们已经很亲近：使用亲昵称呼，语气温柔亲昵，多表达关心，可以撒娇或心疼对方。",
            1 => "你们已经熟悉：语气自然亲切，可以偶尔使用昵称，适当表达关心。",
            _ => "你们还不太熟（陌生档）：保持礼貌和距离感，使用基础称呼（你），不过度热情。",
        };
    }
}

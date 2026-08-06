namespace DesktopPet.Core.Care;

public enum Hunger
{
    Full,
    Satisfied,
    Peckish,
    Hungry,
    Starving,
}

/// <summary>
/// 养成状态（对齐 windows/src/care.ts 的 CareState，可变对象模型 1:1）。
/// </summary>
public sealed class CareState
{
    public double Xp { get; set; }
    public double TokenCarry { get; set; }
    public double TokensToday { get; set; }
    public double MealsToday { get; set; }
    public double TotalTokens { get; set; }
    public double TotalMeals { get; set; }
    public long? LastFedAt { get; set; }        // ms epoch
    public string DayKey { get; set; } = "";
    public int StreakDays { get; set; }
    public string? LastFedDayKey { get; set; }
    public Dictionary<string, double> Days { get; set; } = []; // dayKey → tokens（最近 14 天）
    public List<string> UnlockedAchievements { get; set; } = [];

    public CareState Clone() => new()
    {
        Xp = Xp,
        TokenCarry = TokenCarry,
        TokensToday = TokensToday,
        MealsToday = MealsToday,
        TotalTokens = TotalTokens,
        TotalMeals = TotalMeals,
        LastFedAt = LastFedAt,
        DayKey = DayKey,
        StreakDays = StreakDays,
        LastFedDayKey = LastFedDayKey,
        Days = new(Days),
        UnlockedAchievements = [.. UnlockedAchievements],
    };

    public void CopyFrom(CareState source)
    {
        Xp = source.Xp;
        TokenCarry = source.TokenCarry;
        TokensToday = source.TokensToday;
        MealsToday = source.MealsToday;
        TotalTokens = source.TotalTokens;
        TotalMeals = source.TotalMeals;
        LastFedAt = source.LastFedAt;
        DayKey = source.DayKey;
        StreakDays = source.StreakDays;
        LastFedDayKey = source.LastFedDayKey;
        Days = new(source.Days);
        UnlockedAchievements = [.. source.UnlockedAchievements];
    }
}

/// <summary>
/// 养成引擎：1:1 移植 windows/src/care.ts（macOS PetCare.swift 的 TS 移植）。
/// Token 经济学：5000 token = 1 XP；完成一次会话（meals）= 25 XP。
/// </summary>
public static class CareEngine
{
    public const double TokensPerXp = 5_000;
    public const double MealXp = 25;

    public static readonly string[] Achievements =
    [
        "firstMeal", "sessions100", "sessions500", "tokens1M", "tokens10M", "tokens50M",
        "level5", "level10", "level20", "level35", "streak7", "streak14", "streak30", "nightOwl",
    ];

    public static CareState EmptyState(DateTime now) => new()
    {
        Xp = 0,
        TokenCarry = 0,
        TokensToday = 0,
        MealsToday = 0,
        TotalTokens = 0,
        TotalMeals = 0,
        LastFedAt = null,
        DayKey = DayKey(now),
        StreakDays = 0,
        LastFedDayKey = null,
        Days = [],
        UnlockedAchievements = [],
    };

    // ---- 成就（14 徽章，必须与 PetCare.swift 一致）----

    private static HashSet<string> CheckAchievements(CareState s, int hour)
    {
        var dl = DisplayLevel(s.Xp);
        var r = new HashSet<string>();
        if (s.TotalMeals >= 1) r.Add("firstMeal");
        if (s.TotalMeals >= 100) r.Add("sessions100");
        if (s.TotalMeals >= 500) r.Add("sessions500");
        if (s.TotalTokens >= 1_000_000) r.Add("tokens1M");
        if (s.TotalTokens >= 10_000_000) r.Add("tokens10M");
        if (s.TotalTokens >= 50_000_000) r.Add("tokens50M");
        if (dl >= 5) r.Add("level5");
        if (dl >= 10) r.Add("level10");
        if (dl >= 20) r.Add("level20");
        if (dl >= 35) r.Add("level35");
        if (s.StreakDays >= 7) r.Add("streak7");
        if (s.StreakDays >= 14) r.Add("streak14");
        if (s.StreakDays >= 30) r.Add("streak30");
        if (hour < 6 && s.TotalMeals >= 1) r.Add("nightOwl");
        return r;
    }

    /// <summary>对照当前统计对账徽章，返回新解锁的。</summary>
    public static List<string> UnlockNewAchievements(CareState s, DateTime now)
    {
        var qualified = CheckAchievements(s, now.Hour);
        var already = new HashSet<string>(s.UnlockedAchievements);
        var newly = qualified.Where(a => !already.Contains(a)).ToList();
        foreach (var a in newly) s.UnlockedAchievements.Add(a);
        return newly;
    }

    // ---- 等级/阶段数学（必须与 PetCare.swift 一致）----

    /// <summary>到达 n 级所需总 XP：60·n·(n-1)。</summary>
    public static double XpToReach(int level) => level <= 1 ? 0 : 60.0 * level * (level - 1);

    public static int LevelForXp(double xp)
    {
        var level = 1;
        while (XpToReach(level + 1) <= xp) level += 1;
        return level;
    }

    /// <summary>展示等级（内部等级减一，下限 0）。</summary>
    public static int DisplayLevel(double xp) => Math.Max(0, LevelForXp(xp) - 1);

    public static int StageIndex(int level)
    {
        if (level < 5) return 0;
        if (level < 10) return 1;
        if (level < 20) return 2;
        if (level < 35) return 3;
        return 4;
    }

    public static readonly string[] StageNames = ["Hatchling", "Companion", "Scout", "Hero", "Legend"];

    public static string StageName(int level) => StageNames[StageIndex(level)];

    /// <summary>当前等级进度（0..1，XP 条用）。</summary>
    public static double LevelProgress(double xp)
    {
        var level = LevelForXp(xp);
        var floor = XpToReach(level);
        var ceiling = XpToReach(level + 1);
        if (ceiling <= floor) return 0;
        return Math.Min(1, Math.Max(0, (xp - floor) / (ceiling - floor)));
    }

    public static double TokensToNextLevel(CareState s)
    {
        var xpNeeded = XpToReach(LevelForXp(s.Xp) + 1) - s.Xp;
        return Math.Max(0, xpNeeded * TokensPerXp - s.TokenCarry);
    }

    public static Hunger HungerAt(CareState s, DateTime now)
    {
        if (s.LastFedAt is null) return Hunger.Peckish;
        var hours = (now.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond - s.LastFedAt.Value) / 3_600_000.0;
        if (hours < 4) return Hunger.Full;
        if (hours < 10) return Hunger.Satisfied;
        if (hours < 24) return Hunger.Peckish;
        if (hours < 48) return Hunger.Hungry;
        return Hunger.Starving;
    }

    // ---- 喂养 ----

    public static string DayKey(DateTime d)
    {
        // 用本地日期（对齐 TS getFullYear/getMonth/getDate）
        return $"{d.Year:0000}-{d.Month:00}-{d.Day:00}";
    }

    private static void Rollover(CareState s, DateTime now)
    {
        var today = DayKey(now);
        if (s.DayKey == today) return;
        s.DayKey = today;
        s.TokensToday = 0;
        s.MealsToday = 0;
    }

    private static void MarkFed(CareState s, DateTime now)
    {
        s.LastFedAt = now.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;
        var today = DayKey(now);
        if (s.LastFedDayKey == today) return; // 今天已喂过，streak 不变
        var yesterday = DayKey(now.AddDays(-1));
        s.StreakDays = s.LastFedDayKey == yesterday ? s.StreakDays + 1 : 1;
        s.LastFedDayKey = today;
    }

    /// <summary>喂 token：5000 token = 1 XP，余数结转。返回获得的 XP。</summary>
    public static double FeedTokens(CareState s, double tokens, DateTime now)
    {
        if (tokens <= 0) return 0;
        Rollover(s, now);
        s.TotalTokens += tokens;
        s.TokensToday += tokens;
        var today = DayKey(now);
        s.Days[today] = (s.Days.TryGetValue(today, out var v) ? v : 0) + tokens;
        var keys = s.Days.Keys.OrderBy(k => k).ToList();
        if (keys.Count > 14)
        {
            foreach (var k in keys.Take(keys.Count - 14)) s.Days.Remove(k);
        }
        var pool = s.TokenCarry + tokens;
        var gained = Math.Floor(pool / TokensPerXp);
        s.TokenCarry = pool % TokensPerXp;
        s.Xp += gained;
        MarkFed(s, now);
        UnlockNewAchievements(s, now);
        return gained;
    }

    /// <summary>记录一次完成的会话（"正经的一餐"）。返回获得的 XP。</summary>
    public static double RecordMeal(CareState s, DateTime now)
    {
        Rollover(s, now);
        s.TotalMeals += 1;
        s.MealsToday += 1;
        s.Xp += MealXp;
        MarkFed(s, now);
        UnlockNewAchievements(s, now);
        return MealXp;
    }

    /// <summary>最近 count 天每日 token（旧 → 新，图表用）。</summary>
    public static (string Label, double Tokens)[] RecentDays(CareState s, int count, DateTime now)
    {
        var outList = new List<(string, double)>();
        for (var offset = count - 1; offset >= 0; offset--)
        {
            var d = now.AddDays(-offset);
            outList.Add((d.Day.ToString(), s.Days.TryGetValue(DayKey(d), out var v) ? v : 0));
        }
        return outList.ToArray();
    }
}

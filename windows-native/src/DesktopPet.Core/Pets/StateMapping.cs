namespace DesktopPet.Core.Pets;

/// <summary>
/// 1:1 移植自 windows/src/pet.ts 的 STATE_ROW / STATE_FPS 与 setState 行覆盖语义。
/// 行号对应 spritesheet 布局：0 Idle, 1 RunRight, 2 RunLeft, 3 Waving,
/// 4 Jumping, 5 Failed, 6 Waiting, 7 Running, 8 Review。
/// </summary>
public static class StateMapping
{
    private static readonly IReadOnlyDictionary<string, int> StateRow = new Dictionary<string, int>
    {
        ["idle"] = 0,
        ["registered"] = 0,
        ["working"] = 7,
        ["waiting"] = 6,
        ["done"] = 3,       // waving goodbye to the finished task
        ["celebrate"] = 4,  // jumping, the 3s burst when all work completes
    };

    private static readonly IReadOnlyDictionary<string, double> StateFps = new Dictionary<string, double>
    {
        ["working"] = 8,
        ["celebrate"] = 8,
        ["waiting"] = 4,
        ["done"] = 3,
        ["idle"] = 3,
        ["registered"] = 3,
    };

    /// <summary>state → 行号；未知 state 回退 0。boundRow 为设置页绑定覆盖（TS ap_bind_*）。</summary>
    public static int RowFor(string state, int? boundRow = null)
    {
        if (boundRow is >= 0) return boundRow.Value;
        return StateRow.TryGetValue(state, out var row) ? row : 0;
    }

    public static double FpsFor(string state)
        => StateFps.TryGetValue(state, out var fps) ? fps : 3;
}

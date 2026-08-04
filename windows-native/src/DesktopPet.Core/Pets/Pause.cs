namespace DesktopPet.Core.Pets;

/// <summary>
/// 1:1 移植自 windows/src/roam/pause.ts（vitest 已有对照测试）。
/// </summary>
public static class Pause
{
    public const double MinWanderPauseMs = 1000;
    public const double DefaultWanderPauseMinMs = 1200;
    public const double DefaultWanderPauseMaxMs = 3500;

    public readonly record struct WanderPauseRange(double MinMs, double MaxMs);

    /// <summary>TS Number(value)：null → 0；字符串数字 → 解析；否则 NaN。</summary>
    private static double Number(double? value) => value ?? 0;

    private static double NormalizePauseMs(double? value, double fallback)
    {
        var milliseconds = Number(value);
        return double.IsFinite(milliseconds) && milliseconds >= MinWanderPauseMs
            ? Math.Round(milliseconds)
            : fallback;
    }

    public static WanderPauseRange NormalizeWanderPauseRange(double? minValue, double? maxValue)
    {
        var minMs = NormalizePauseMs(minValue, DefaultWanderPauseMinMs);
        var maxMs = NormalizePauseMs(maxValue, DefaultWanderPauseMaxMs);
        return new WanderPauseRange(Math.Min(minMs, maxMs), Math.Max(minMs, maxMs));
    }

    public static double SampleWanderPauseMs(WanderPauseRange range, Func<double> random)
    {
        var factor = Math.Max(0, Math.Min(1, random()));
        return range.MinMs + (range.MaxMs - range.MinMs) * factor;
    }
}

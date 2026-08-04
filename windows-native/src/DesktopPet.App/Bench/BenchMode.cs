using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using DesktopPet.App.Windows;

namespace DesktopPet.App.Bench;

/// <summary>
/// 基准模式（perf-bench.ps1 驱动）：
///  --bench-drag=&lt;ms&gt;  摆 1 只宠物于已知位置，记录拖拽延迟（处理耗时 +
///                      端到端，见 PetWindow），超时后写 JSON 退出。
///  --bench-idle=&lt;ms&gt;  静止模式（动画循环停）运行 N 秒后退出，供脚本采样
///                      CPU/内存。
/// 就绪信号写 %TEMP%\desktoppet-bench-*.ready（含宠物物理坐标），脚本轮询。
/// </summary>
public static class BenchMode
{
    public sealed record DragResult(
        double ProcessingAvgMs,
        double ProcessingMaxMs,
        double EndToEndAvgMs,
        double EndToEndP95Ms,
        double EndToEndMaxMs,
        int SampleCount);

    private static double P95(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return 0;
        var sorted = samples.OrderBy(v => v).ToList();
        return sorted[(int)(sorted.Count * 0.95)];
    }

    public static void RunDrag(PetWindowManager manager, int durationMs)
    {
        // 已知位置：主屏右下角附近固定点，脚本据此移动鼠标
        var (waW, waH) = DesktopPet.App.Interop.NativeMethods.PrimaryWorkAreaSize();
        var posX = waW - 400;
        var posY = waH - 400;

        var window = manager.ShowBenchPet(posX, posY);
        var center = window.SpriteCenterPhysical;
        var readyPath = Path.Combine(Path.GetTempPath(), "desktoppet-bench-drag.ready");
        File.WriteAllText(readyPath, JsonSerializer.Serialize(new
        {
            x = posX,
            y = posY,
            centerX = center.X + posX,
            centerY = center.Y + posY,
        }));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var processing = window.ProcessingLatencySamples;
            var endToEnd = window.EndToEndLatencySamples;
            var result = new DragResult(
                ProcessingAvgMs: processing.Count > 0 ? processing.Average() : 0,
                ProcessingMaxMs: processing.Count > 0 ? processing.Max() : 0,
                EndToEndAvgMs: endToEnd.Count > 0 ? endToEnd.Average() : 0,
                EndToEndP95Ms: P95(endToEnd),
                EndToEndMaxMs: endToEnd.Count > 0 ? endToEnd.Max() : 0,
                SampleCount: endToEnd.Count);
            var outPath = Path.Combine(Path.GetTempPath(), "desktoppet-bench-drag.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(result));
            System.Windows.Application.Current.Shutdown();
        };
        timer.Start();
    }

    public static void RunIdle(PetWindowManager manager, int durationMs)
    {
        foreach (var window in manager.VisibleWindows)
        {
            window.AnimationEnabled = false; // 静止：画完首帧停渲染循环
        }
        var readyPath = Path.Combine(Path.GetTempPath(), "desktoppet-bench-idle.ready");
        File.WriteAllText(readyPath, "ready");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            System.Windows.Application.Current.Shutdown();
        };
        timer.Start();
    }
}

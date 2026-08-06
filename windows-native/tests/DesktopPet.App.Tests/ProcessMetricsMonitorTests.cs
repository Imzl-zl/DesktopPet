using DesktopPet.App.Diagnostics;

namespace DesktopPet.App.Tests;

public sealed class ProcessMetricsMonitorTests
{
    [Fact]
    public void Sample_ComputesProcessorNormalizedCpuAndWorkingSet()
    {
        var samples = new Queue<IReadOnlyList<ProcessSnapshot>>([
            [Snapshot(seconds: 1, cpuSeconds: 2, workingSet: 100)],
            [Snapshot(seconds: 3, cpuSeconds: 4, workingSet: 250)],
        ]);
        using var monitor = new ProcessMetricsMonitor(() => samples.Dequeue(), processorCount: 2);

        monitor.Start();
        var metric = Assert.Single(monitor.Sample());

        Assert.Equal(50, metric.CpuPercent, precision: 3);
        Assert.Equal(250, metric.WorkingSetBytes);
    }

    [Fact]
    public void StopAndDispose_PreventFurtherSampling()
    {
        var captures = 0;
        var monitor = new ProcessMetricsMonitor(() =>
        {
            captures++;
            return [Snapshot(captures, captures, captures)];
        });

        monitor.Start();
        monitor.Stop();
        Assert.Empty(monitor.Sample());
        Assert.Equal(1, captures);

        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => monitor.Sample());
    }

    [Fact]
    public void NewProcessGeneration_StartsAtZeroCpu()
    {
        var samples = new Queue<IReadOnlyList<ProcessSnapshot>>([
            [Snapshot(1, 1, 1, processId: 10)],
            [Snapshot(2, 2, 2, processId: 20)],
        ]);
        using var monitor = new ProcessMetricsMonitor(() => samples.Dequeue(), processorCount: 1);

        monitor.Start();
        var metric = Assert.Single(monitor.Sample());

        Assert.Equal(0, metric.CpuPercent);
        Assert.Equal(20, metric.ProcessId);
    }

    private static ProcessSnapshot Snapshot(
        double seconds,
        double cpuSeconds,
        long workingSet,
        int processId = 10)
        => new(
            "PetApp",
            processId,
            DateTimeOffset.UnixEpoch.AddSeconds(seconds),
            TimeSpan.FromSeconds(cpuSeconds),
            workingSet);
}

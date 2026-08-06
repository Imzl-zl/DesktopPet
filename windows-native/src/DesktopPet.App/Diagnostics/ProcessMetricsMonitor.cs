using System.Diagnostics;

namespace DesktopPet.App.Diagnostics;

public sealed record ProcessSnapshot(
    string Name,
    int ProcessId,
    DateTimeOffset Timestamp,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes);

public sealed record ProcessMetrics(
    string Name,
    int ProcessId,
    double CpuPercent,
    long WorkingSetBytes);

public sealed class ProcessMetricsMonitor : IDisposable
{
    private readonly Func<IReadOnlyList<ProcessSnapshot>> _capture;
    private readonly int _processorCount;
    private Dictionary<(string Name, int ProcessId), ProcessSnapshot> _previous = [];
    private bool _running;
    private bool _disposed;

    public ProcessMetricsMonitor(
        Func<IReadOnlyList<ProcessSnapshot>> capture,
        int? processorCount = null)
    {
        _capture = capture;
        _processorCount = Math.Max(1, processorCount ?? Environment.ProcessorCount);
    }

    public bool IsRunning => _running;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _previous = _capture().ToDictionary(snapshot => (snapshot.Name, snapshot.ProcessId));
        _running = true;
    }

    public IReadOnlyList<ProcessMetrics> Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_running) return [];

        var current = _capture();
        var metrics = new List<ProcessMetrics>(current.Count);
        foreach (var snapshot in current)
        {
            var key = (snapshot.Name, snapshot.ProcessId);
            var cpu = 0d;
            if (_previous.TryGetValue(key, out var previous))
            {
                var elapsed = (snapshot.Timestamp - previous.Timestamp).TotalSeconds;
                var cpuSeconds = (snapshot.TotalProcessorTime - previous.TotalProcessorTime).TotalSeconds;
                if (elapsed > 0 && cpuSeconds >= 0)
                    cpu = Math.Clamp(cpuSeconds / elapsed / _processorCount * 100d, 0d, 100d);
            }
            metrics.Add(new ProcessMetrics(
                snapshot.Name,
                snapshot.ProcessId,
                cpu,
                Math.Max(0, snapshot.WorkingSetBytes)));
        }
        _previous = current.ToDictionary(snapshot => (snapshot.Name, snapshot.ProcessId));
        return metrics;
    }

    public void Stop()
    {
        _running = false;
        _previous.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    public static ProcessSnapshot? CaptureProcess(string name, int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return null;
            return new ProcessSnapshot(
                name,
                processId,
                DateTimeOffset.UtcNow,
                process.TotalProcessorTime,
                process.WorkingSet64);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

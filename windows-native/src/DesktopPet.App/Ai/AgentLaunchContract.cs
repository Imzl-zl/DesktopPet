using System.Diagnostics;
using System.Globalization;

namespace DesktopPet.App.Ai;

internal sealed record AgentLaunchContract(
    string PipeName,
    int ParentProcessId,
    long ParentStartTimeUtcTicks)
{
    private const string PipePrefix = "DesktopPet.Agent.";

    public static AgentLaunchContract Create(Process parentProcess)
    {
        ArgumentNullException.ThrowIfNull(parentProcess);
        if (parentProcess.HasExited) throw new InvalidOperationException("父进程已退出");
        return new AgentLaunchContract(
            PipePrefix + Guid.NewGuid().ToString("N"),
            parentProcess.Id,
            parentProcess.StartTime.ToUniversalTime().Ticks);
    }

    public void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(PipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(ParentProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--parent-start-utc-ticks");
        startInfo.ArgumentList.Add(ParentStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture));
    }
}

using System.Diagnostics;
using DesktopPet.App.Ai;

namespace DesktopPet.App.Tests;

public class AgentLaunchContractTests
{
    [Fact]
    public void Create_UsesUniquePipeAndExplicitParentPidArguments()
    {
        var first = AgentLaunchContract.Create(Process.GetCurrentProcess());
        var second = AgentLaunchContract.Create(Process.GetCurrentProcess());
        var startInfo = new ProcessStartInfo("PetAgent.exe");

        first.ApplyTo(startInfo);

        Assert.StartsWith("DesktopPet.Agent.", first.PipeName, StringComparison.Ordinal);
        Assert.NotEqual(first.PipeName, second.PipeName);
        Assert.Equal(
            [
                "--pipe", first.PipeName,
                "--parent-pid", Environment.ProcessId.ToString(),
                "--parent-start-utc-ticks", first.ParentStartTimeUtcTicks.ToString(),
            ],
            startInfo.ArgumentList);
    }
}

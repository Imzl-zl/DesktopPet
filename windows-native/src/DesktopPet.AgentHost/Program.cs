using System.Windows.Threading;
using DesktopPet.Agent;
using DesktopPet.Agent.Capture;
using DesktopPet.Infra.Providers;

namespace DesktopPet.AgentHost;

/// <summary>
/// Agent 宿主（迁移计划 §4.1 双进程：PetAgent.exe）。
/// 控制台程序：WPF Dispatcher 提供 STA 消息泵（GraphicsCapture 的 FrameArrived 依赖）；
/// AgentService 编排管道（server）+ 分析引擎，App 连上后下发配置。
/// 日志：%TEMP%/desktoppet-agent.log（进程诊断用）。
/// </summary>
internal static class Program
{
    private static void Log(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "desktoppet-agent.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}" + Environment.NewLine);
        }
        catch (Exception) { }
    }

    [STAThread]
    private static void Main()
    {
        Log("AgentHost start");
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            throw;
        }
        Log("AgentHost exit");
    }

    private static void Run()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

        IScreenCaptureSource capture;
        try
        {
            if (GraphicsCaptureSource.IsSupported())
            {
                capture = new GraphicsCaptureSource(); // 必须在 STA + 消息泵线程创建
                Log("GraphicsCapture enabled");
            }
            else
            {
                Log("GraphicsCapture unsupported (Win11 22H2+ required)");
                capture = new OfflineFrameSource([]);
            }
        }
        catch (Exception ex)
        {
            Log($"GraphicsCapture init failed: {ex.Message}");
            capture = new OfflineFrameSource([]);
        }

        var service = new AgentService(AgentService.DefaultPipeName, capture, new WindowsCredentialStore());

        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await service.RunAsync(CancellationToken.None);
                Log("AgentService.RunAsync returned");
            }
            catch (Exception ex)
            {
                Log($"AgentService failed: {ex.Message}");
            }
            dispatcher.InvokeShutdown();
        });

        Log($"Agent ready (pipe: {AgentService.DefaultPipeName})");
        Dispatcher.Run(); // 消息泵：驱动 FrameArrived + 服务任务

        service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

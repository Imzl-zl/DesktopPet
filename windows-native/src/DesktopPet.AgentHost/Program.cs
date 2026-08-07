using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using DesktopPet.Agent;
using DesktopPet.Agent.Capture;
using DesktopPet.Infra.Diagnostics;
using DesktopPet.Infra.Providers;

namespace DesktopPet.AgentHost;

/// <summary>
/// Agent 宿主（迁移计划 §4.1 双进程：PetAgent.exe）。
/// 控制台程序：WPF Dispatcher 提供 STA 消息泵（GraphicsCapture 的 FrameArrived 依赖）；
/// AgentService 编排管道（server）+ 分析引擎，App 连上后下发配置。
/// 日志：%APPDATA%/DesktopPet/logs/agent.log（滚动 + 脱敏）。
/// </summary>
internal static class Program
{
    private static IAppLogger _logger = NullAppLogger.Instance;
    private static IScreenCaptureSource CreateCaptureSource(Dispatcher dispatcher)
    {
        return dispatcher.Invoke(() =>
        {
            if (!GraphicsCaptureSource.IsSupported())
            {
                throw new NotSupportedException("GraphicsCapture is unsupported (Win11 22H2+ required)");
            }

            Log("GraphicsCapture enabled");
            return (IScreenCaptureSource)new GraphicsCaptureSource();
        });
    }

    private static void Log(string msg) => _logger.Info("AgentHost", msg);

    [STAThread]
    private static void Main(string[] args)
    {
        using var logger = new RollingFileLogger(
            AppDataPaths.ForCurrentUser().Logs,
            "agent");
        _logger = logger;
        Log("AgentHost start");
        try
        {
            Run(ParseLaunchOptions(args));
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            throw;
        }
        Log("AgentHost exit");
    }

    private static void Run(LaunchOptions options)
    {
        using var parentProcess = OpenParentProcess(options);
        using var lifetimeCts = new CancellationTokenSource();
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

        var capture = new SwitchableScreenCaptureSource(
            () => CreateCaptureSource(dispatcher),
            source => dispatcher.Invoke(() =>
            {
                if (source is IDisposable disposable) disposable.Dispose();
            }));
        Log("GraphicsCapture deferred until ScreenAnalysis is enabled");

        var service = new AgentService(
            options.PipeName,
            capture,
            new WindowsCredentialStore(),
            options.ParentProcessId,
            logger: _logger);

        dispatcher.InvokeAsync(async () =>
        {
            var serviceTask = service.RunAsync(lifetimeCts.Token);
            var parentExitTask = parentProcess.WaitForExitAsync(lifetimeCts.Token);
            try
            {
                var completed = await Task.WhenAny(serviceTask, parentExitTask);
                if (ReferenceEquals(completed, parentExitTask)) Log("Parent PetApp exited");
            }
            catch (Exception ex)
            {
                Log($"AgentService lifetime failed: {ex.Message}");
            }
            finally
            {
                lifetimeCts.Cancel();
                try { await serviceTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log($"AgentService failed: {ex.Message}"); }
                try { await parentExitTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log($"Parent process monitor failed: {ex.Message}"); }
                // 任何路径都必须关掉消息泵：否则 Dispatcher.Run 永不返回 →
                // service.DisposeAsync 不执行，Agent 进程静默挂死（看门狗感知不到）
                dispatcher.InvokeShutdown();
            }
        });

        Log("Agent ready");
        Dispatcher.Run(); // 消息泵：驱动 FrameArrived + 服务任务

        service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static LaunchOptions ParseLaunchOptions(string[] args)
    {
        string? pipeName = null;
        int? parentProcessId = null;
        long? parentStartTimeUtcTicks = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe" && i + 1 < args.Length)
            {
                pipeName = args[++i];
            }
            else if (args[i] == "--parent-pid" && i + 1 < args.Length
                     && int.TryParse(args[++i], out var parsed))
            {
                parentProcessId = parsed;
            }
            else if (args[i] == "--parent-start-utc-ticks" && i + 1 < args.Length
                     && long.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                parentStartTimeUtcTicks = ticks;
            }
            else
            {
                throw new ArgumentException($"未知或不完整的 AgentHost 参数: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName)
            || !pipeName.StartsWith("DesktopPet.Agent.", StringComparison.Ordinal)
            || parentProcessId is null or <= 0
            || parentStartTimeUtcTicks is null or <= 0)
        {
            throw new ArgumentException("AgentHost 必须由 PetApp 以随机 pipe 和完整父进程标识启动");
        }
        return new LaunchOptions(pipeName, parentProcessId.Value, parentStartTimeUtcTicks.Value);
    }

    private static Process OpenParentProcess(LaunchOptions options)
    {
        var parent = Process.GetProcessById(options.ParentProcessId);
        try
        {
            if (parent.HasExited
                || parent.StartTime.ToUniversalTime().Ticks != options.ParentStartTimeUtcTicks
                || !string.Equals(parent.ProcessName, "DesktopPet.App", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("AgentHost 父进程身份不匹配");
            }
            parent.EnableRaisingEvents = true;
            return parent;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    private sealed record LaunchOptions(
        string PipeName,
        int ParentProcessId,
        long ParentStartTimeUtcTicks);
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using DesktopPet.App.Ai;
using DesktopPet.App.Bench;
using DesktopPet.App.Hotkeys;
using DesktopPet.App.Fullscreen;
using DesktopPet.App.Localization;
using DesktopPet.App.Rendering;
using DesktopPet.App.Tray;
using DesktopPet.App.Windows;
using DesktopPet.Infra.Storage;
using DesktopPet.Core.Care;
using DesktopPet.Core.Hotkeys;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Diagnostics;
using DesktopPet.Infra.Hotkey;
using DesktopPet.Infra.Providers;

namespace DesktopPet.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Global\DesktopPet.Native.SingleInstance";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private PetWindowManager? _manager;
    private TrayController? _tray;
    private FileJsonStore? _store;
    private AiCoordinator? _ai;
    private ModeService? _modeService;
    private ChatWindow? _chatWindow;
    private HotkeyManager? _hotkeys;
    private HotkeySettingsCoordinator? _hotkeyCoordinator;
    private LanguageCoordinator? _languageCoordinator;
    private DesktopPet.Core.I18n.I18nService? _i18n;
    private RollingFileLogger? _logger;
    private FullscreenSuppressionMonitor? _fullscreenMonitor;
    private WelcomeWindow? _welcomeWindow;
    private HwndSource? _hotkeySource;
    private Window? _hotkeyHost;
    private bool _startupInProgress;
    private bool _startupWelcomePending;

    public App()
    {
        DispatcherUnhandledException += HandleDispatcherException;
    }

    private void HandleDispatcherException(
        object? sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is not JsonStoreException storageError) return;
        PersistenceErrorPresenter.Report(storageError);
        e.Handled = true;
        if (_startupInProgress || _startupWelcomePending) Shutdown(-1);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _startupInProgress = true;
        base.OnStartup(e);
        WaitForParentRestart(e.Args);

        // 单实例（对齐 Tauri 版 tauri_plugin_single_instance：第二实例直接退出，
        // Phase 4 接设置窗跳转）
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            _startupInProgress = false;
            Shutdown();
            return;
        }

        var isBench = e.Args.Any(a => a.StartsWith("--bench-"));
        if (isBench) PetWindow.BenchLogEnabled = true;

        var paths = AppDataPaths.ForCurrentUser();
        var dataDir = paths.Root;
        _logger = new RollingFileLogger(paths.Logs, "app");
        _logger.Info("App", "startup");
        _store = new FileJsonStore(dataDir);

        var store = isBench ? PetStoreModel.EmptyPetStore() : InitializeStore();
        _manager = new PetWindowManager(
            _store,
            new SpriteLoader(dataDir, logger: _logger),
            _logger);
        _manager.SetDiagnosticExporter(new DiagnosticExporter(paths.Logs, () => _logger?.Flush()));
        _manager.SetFactoryResetHandler(ExecuteFactoryResetAsync);
        _manager.Reconcile(store, _store.LoadGlobalVisibility());

        if (!isBench)
        {
            // 语言优先读已保存设置（设置页语言页）；首次启动才用系统检测
            var storedSettings = _store.LoadSettings();
            var i18n = new DesktopPet.Core.I18n.I18nService(
                storedSettings?.Lang ?? DesktopPet.Core.I18n.I18nService.Detect());
            _i18n = i18n;
            PersistenceErrorPresenter.Configure(i18n);
            _manager.SetI18n(i18n);
            var settings = AppSettings.Normalize(storedSettings ?? AppSettings.Defaults(i18n.Lang));
            _manager.ApplySettings(settings);
            _manager.CreateFloatingBall(dataDir);
            _tray = new TrayController(_manager, i18n);

            // Phase 5：AI 编排（总开关/Agent 进程/输出模式/对话/记账）
            var chatWindow = new ChatWindow(i18n);
            _chatWindow = chatWindow;
            chatWindow.IsVisibleChanged += (_, _) =>
                _manager?.SetChatVisible(chatWindow.IsVisible && chatWindow.WindowState != WindowState.Minimized);
            chatWindow.StateChanged += (_, _) =>
                _manager?.SetChatVisible(chatWindow.IsVisible && chatWindow.WindowState != WindowState.Minimized);
            _manager.SettingsForegroundChanged += settingsForeground =>
                chatWindow.Topmost = !settingsForeground;
            var fullscreenDetector = new FullscreenWindowDetector();
            _modeService = new ModeService(
                danmakuFactory: () =>
                {
                    // 弹幕参数每次创建窗口时从最新设置读取（设置页气泡页可调）
                    var danmakuSettings = AppSettings.Normalize(
                        _store.LoadSettings() ?? AppSettings.Defaults(i18n.Lang));
                    return new DanmakuWindow(
                        SystemParameters.VirtualScreenWidth,
                        SystemParameters.VirtualScreenHeight,
                        trackCount: danmakuSettings.DanmakuTrackCount,
                        i18n: i18n,
                        fontSize: danmakuSettings.DanmakuFontSize,
                        speedPercent: danmakuSettings.DanmakuSpeedPercent);
                },
                routeToChat: output =>
                {
                    ShowChatWindow();
                    _chatWindow?.AppendAssistantAsync(output.Text);
                },
                routeToBubble: text => _manager.BroadcastQuickBubble(text),
                isFullscreen: fullscreenDetector.IsSuppressed);
            _fullscreenMonitor = new FullscreenSuppressionMonitor(
                fullscreenDetector.IsSuppressed,
                suppressed => _modeService?.SetFullscreenSuppressed(suppressed),
                TimeSpan.FromMilliseconds(250));
            _fullscreenMonitor.Start();
            MigrateProviderCredentials();
            _ai = new AiCoordinator(
                _store,
                _modeService,
                _chatWindow,
                RecordTokens,
                ResolveAgentHostPath(),
                i18n,
                _logger);
            _chatWindow.SendRequested += async (text, ctx) =>
            {
                if (_ai is not null) await _ai.SendChatAsync(text, ctx);
            };
            _chatWindow.PersonaSwitchRequested += _ =>
            {
                // 对话窗顶部人格名点击 → 打开设置 AI 页（人格卡片主入口）
                _manager.OpenSettings();
                _manager.NavigateSettingsToAi();
            };
            _chatWindow.RestartRequested += () => _ai?.ClearChatHistory(); // 重开 = 清 L1 会话窗口
            _manager.SetAiCoordinator(_ai);
            _manager.SetOutputModeHandler(ApplyOutputModeFromBall);
            _manager.SetOpenChatHandler(ShowChatWindow);
            _languageCoordinator = new LanguageCoordinator(
                _store,
                i18n,
                PublishLanguage,
                settings.Lang);
            _manager.SetLanguageChangeHandler(_languageCoordinator.ChangeLanguageAsync);
            _ai.ApplySettings(settings); // 应用已保存 AI 设置（默认关 = 不起 Agent）
            _chatWindow.TtsEnabled = settings.Ai.TtsEnabled; // 朗读按钮初始状态（AI 助手页开关）
            RegisterGlobalHotkeys(settings.Hotkeys);
            if (_hotkeys is not null)
            {
                _hotkeyCoordinator = new HotkeySettingsCoordinator(
                    _store,
                    _hotkeys,
                    settings.Lang,
                    i18n);
                _manager.SetHotkeySettingsHandler(ApplyHotkeySettings);
            }

            // 初始化引导：AI 已开启但未设置称呼/人格 → 弹引导窗（之后设置页可改）
            if (settings.Ai.Enabled && !settings.Ai.Onboarded)
            {
                ShowWelcomeOnboarding();
            }

            if (e.Args.Contains("--settings"))
            {
                _manager.OpenSettings();
            }

            if (e.Args.Contains("--chat"))
            {
                ShowChatWindow();
            }
        }

        var benchIndex = Array.FindIndex(e.Args, a => a.StartsWith("--bench-"));
        if (benchIndex >= 0)
        {
            var arg = e.Args[benchIndex];
            var ms = 8000;
            var eq = arg.IndexOf('=');
            if (eq > 0 && int.TryParse(arg[(eq + 1)..], out var parsed)) ms = parsed;
            if (arg.StartsWith("--bench-drag")) BenchMode.RunDrag(_manager, ms);
            else if (arg.StartsWith("--bench-idle")) BenchMode.RunIdle(_manager, ms);
        }
        _startupInProgress = false;
    }

    /// <summary>
    /// 加载/初始化宠物 store。Phase 3：tauri-export.json（Tauri localStorage 导出）
    /// 存在时一次性迁移（实例 + 养成状态），完成后删除导出文件。
    /// </summary>
    private PetStore InitializeStore()
    {
        var store = _store!.LoadPetStore();
        var exportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPet", "tauri-export.json");
        string? exportJson;
        try
        {
            exportJson = File.ReadAllText(exportPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            exportJson = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JsonStoreException("读取迁移文件", exportPath, ex);
        }

        // 导出文件本身是迁移提交标记：任一目标保存失败时保留，下一次启动幂等重放。
        if (exportJson is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(exportJson);
                var result = TauriMigration.Migrate(doc.RootElement, DateTime.Now);
                if (result.HadData)
                {
                    _store.SavePetStore(result.Store);
                    _store.SaveCare(result.Care);
                    store = result.Store;
                }
                DeleteMigrationMarker(exportPath);
                _logger?.Info(
                    "Migration",
                    $"Tauri migration imported pets={result.Store.Instances.Count} careStates={result.Care.Count}");
            }
            catch (JsonStoreException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                _logger?.Error("Migration", $"Tauri migration rejected: {ex.GetType().Name}: {ex.Message}");
                var language = _store.LoadSettings()?.Lang
                    ?? DesktopPet.Core.I18n.I18nService.Detect();
                var i18n = _i18n ?? new DesktopPet.Core.I18n.I18nService(language);
                MessageBox.Show(
                    i18n.T("迁移文件格式无效，未导入或删除原文件。请检查 tauri-export.json 后重试。"),
                    i18n.T("DesktopPet 数据迁移"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        store ??= PetStoreModel.EmptyPetStore();
        store = PetStoreModel.MigrateLegacyPetStore(store, legacy: null);
        if (store.Instances.Count == 0)
        {
            store = PetStoreModel.CreatePetInstance(store, new PetInstance
            {
                Id = PetStoreModel.NewPetInstanceId(),
                Name = "Desktop Pet",
                SpriteSlug = "placeholder",
                Visible = true,
                Size = 100,
                RoamEnabled = true,
                RoamMode = RoamMode.Wander,
                RoamSpeed = 5,
                WanderPauseMinMs = Pause.DefaultWanderPauseMinMs,
                WanderPauseMaxMs = Pause.DefaultWanderPauseMaxMs,
                ReactsToActivity = true,
            });
            _store.SavePetStore(store);
        }
        return store;
    }

    private static void DeleteMigrationMarker(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JsonStoreException("删除迁移标记", path, ex);
        }
    }

    private void MigrateProviderCredentials()
    {
        try
        {
            var result = new ProviderCredentialMigrator(_store!, new WindowsCredentialStore()).Migrate();
            if (result.SkippedUnsafeSource)
            {
                MessageBox.Show(
                    _i18n?.T("模型连接配置包含无法安全迁移的条目；未修改配置或凭据。")
                        ?? "模型连接配置包含无法安全迁移的条目；未修改配置或凭据。",
                    _i18n?.T("DesktopPet 模型连接") ?? "DesktopPet 模型连接",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else if (result.CleanupErrors.Count > 0)
            {
                MessageBox.Show(
                    _i18n?.T("模型凭据已迁移，但旧凭据清理失败；下次启动会重试。")
                        ?? "模型凭据已迁移，但旧凭据清理失败；下次启动会重试。",
                    _i18n?.T("DesktopPet 模型连接") ?? "DesktopPet 模型连接",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex);
        }
        catch (CredentialStoreException ex)
        {
            MessageBox.Show(
                _i18n?.Format("Windows 凭据操作失败（系统错误 {0}）", ex.NativeError)
                    ?? ex.Message,
                _i18n?.T("DesktopPet 模型连接") ?? "DesktopPet 模型连接",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (CredentialMigrationException ex)
        {
            MessageBox.Show(
                _i18n?.T(ex.Message) ?? ex.Message,
                _i18n?.T("DesktopPet 模型连接") ?? "DesktopPet 模型连接",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task<FactoryResetResult> ExecuteFactoryResetAsync(CancellationToken ct)
    {
        if (_ai is not null)
        {
            await _ai.DisposeAsync().ConfigureAwait(true);
            _ai = null;
        }
        _fullscreenMonitor?.Dispose();
        _fullscreenMonitor = null;
        _modeService?.Shutdown();
        _chatWindow?.Close();
        _tray?.Dispose();
        _tray = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
        if (_hotkeySource is not null) _hotkeySource.RemoveHook(HotkeyHook);
        _hotkeyHost?.Close();
        _hotkeyHost = null;
        _manager?.Shutdown();
        _manager = null;

        _logger?.Flush();
        _logger?.Dispose();
        _logger = null;

        FactoryResetResult result;
        try
        {
            var paths = AppDataPaths.ForCurrentUser();
            result = await Task.Run(
                () => new FactoryResetService(paths.Root, new WindowsCredentialStore()).Reset(),
                ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var message = ex is FactoryResetException resetError
                ? DescribeFactoryResetError(resetError)
                : _i18n?.Format("恢复出厂失败：{0}", ex.Message) ?? ex.Message;
            MessageBox.Show(
                message,
                _i18n?.T("恢复出厂设置") ?? "Factory reset",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            if (!TryRestartApplication(out var restartError))
            {
                ReportRestartFailure(restartError!);
                Shutdown(-1);
            }
            throw;
        }

        if (!TryRestartApplication(out var completedRestartError))
        {
            ReportRestartFailure(completedRestartError!);
            Shutdown(-1);
            throw new IOException(
                "Factory reset completed, but the application could not restart",
                completedRestartError);
        }
        return result;
    }

    private string DescribeFactoryResetError(FactoryResetException error)
    {
        var stage = error.Stage switch
        {
            "stage-data" => _i18n?.T("暂存应用数据") ?? error.Stage,
            "delete-credentials" => _i18n?.T("删除 API 凭据") ?? error.Stage,
            "delete-data" => _i18n?.T("删除应用数据") ?? error.Stage,
            "delete-residual-data" => _i18n?.T("清理残留数据") ?? error.Stage,
            _ => error.Stage,
        };
        var recovery = error.RollbackComplete
            ? _i18n?.T("原数据已保留") ?? "Original data was preserved"
            : error.Stage == "delete-credentials" && error.ResidualPath is null
                ? _i18n?.T("应用数据已恢复，但部分 API 凭据可能已删除；请重新启动后重试")
                    ?? "Application data was restored, but some API credentials may have been deleted"
                : _i18n?.T("部分数据可能已暂存，请保留现场并重试")
                    ?? "Some data may be staged; preserve the current state and retry";
        return _i18n?.Format("恢复出厂失败：{0}；{1}", stage, recovery)
            ?? $"Factory reset failed: {stage}; {recovery}";
    }

    private bool TryRestartApplication(out Exception? error)
    {
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to determine the application path");
            using var current = Process.GetCurrentProcess();
            var startTicks = current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true };
            startInfo.ArgumentList.Add("--wait-for-parent");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(startTicks);
            using var restarted = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The restart process could not be created");
            error = null;
            Shutdown(0);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or IOException)
        {
            error = ex;
            return false;
        }
    }

    private void ReportRestartFailure(Exception error)
        => MessageBox.Show(
            _i18n?.Format(
                "应用无法自动重启。请手动启动 DesktopPet。\n\n{0}",
                error.Message)
                ?? $"DesktopPet could not restart automatically. Start it manually.\n\n{error.Message}",
            _i18n?.T("恢复出厂设置") ?? "Factory reset",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static void WaitForParentRestart(IReadOnlyList<string> args)
    {
        if (args.Count != 3 || !string.Equals(args[0], "--wait-for-parent", StringComparison.Ordinal)) return;
        if (!int.TryParse(args[1], out var parentId)
            || !long.TryParse(args[2], out var expectedStartTicks)
            || parentId == Environment.ProcessId)
        {
            throw new InvalidOperationException("Invalid restart parent identity");
        }

        Process parent;
        try { parent = Process.GetProcessById(parentId); }
        catch (ArgumentException)
        {
            return; // The expected parent already exited before the restart process observed it.
        }
        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != expectedStartTicks)
                {
                    return; // PID was reused after the expected parent exited.
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return;
            }
            if (!parent.HasExited && !parent.WaitForExit(15_000))
                throw new TimeoutException("Restart parent did not exit");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ai is not null)
            _ai.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _fullscreenMonitor?.Dispose();
        _fullscreenMonitor = null;
        _modeService?.Shutdown();
        _chatWindow?.Close();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        if (_hotkeySource is not null) _hotkeySource.RemoveHook(HotkeyHook);
        _hotkeyHost?.Close();
        _manager?.Shutdown();
        _logger?.Info("App", "shutdown");
        _logger?.Dispose();
        _logger = null;
        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // ---- Phase 5：AI 接线辅助 ----

    /// <summary>浮球菜单模式切换（danmaku/chat/silent）：立即生效 + 持久化。</summary>
    /// <summary>初始化引导窗（App 启动触发；保存走 AiCoordinator.CompleteOnboarding 单一入口）。</summary>
    private void ShowWelcomeOnboarding()
    {
        var ai = _ai;
        var store = _store;
        if (ai is null || store is null) return;
        var personas = ai.Personas;
        var profile = store.LoadMemoryProfile()
            ?? new DesktopPet.Core.Memory.UserProfile("", [], "", "");
        _startupWelcomePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            var welcome = new DesktopPet.App.Windows.WelcomeWindow(
                builtinPersonas: DesktopPet.Core.Personas.BuiltinPersonas.GetAll(),
                initialCallName: profile.CallName,
                selectedPersonaId: personas.SelectedId,
                onComplete: (callName, personaId) =>
                {
                    ai.CompleteOnboarding(callName, personaId);
                    return true;
                },
                i18n: _i18n);
            _welcomeWindow = welcome;
            welcome.Closed += (_, _) =>
            {
                _startupWelcomePending = false;
                _welcomeWindow = null;
            };
            welcome.Show();
        });
    }

    private void PublishLanguage(AppSettings settings)
    {
        _manager?.ApplySettings(settings);
        _manager?.ApplyLocalization();
        if (_i18n is { } i18n)
        {
            _tray?.ApplyLocalization(i18n);
            _chatWindow?.ApplyLocalization(i18n);
            _welcomeWindow?.ApplyLocalization(i18n);
            _modeService?.ApplyLocalization(i18n);
        }
    }

    private void ApplyOutputModeFromBall(string mode)
    {
        var parsed = mode switch
        {
            "danmaku" => OutputMode.Danmaku,
            "chat" => OutputMode.Chat,
            "bubble" => OutputMode.Bubble,
            _ => OutputMode.Silent,
        };
        if (_ai is null || _store is null)
        {
            _modeService?.SetMode(parsed);
            return;
        }

        var settings = AppSettings.Normalize(_store.LoadSettings()
            ?? AppSettings.Defaults(DesktopPet.Core.I18n.I18nService.Detect()));
        var next = settings with { Ai = settings.Ai with { OutputMode = mode } };
        try
        {
            _store.SaveSettings(next);
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex);
            return;
        }
        _modeService?.SetMode(parsed);
        _ai.ApplySettings(next);
        _manager?.RefreshSettingsWindow(); // 设置窗开着时同步新值（防旧快照回滚）
    }

    private void ShowChatWindow()
    {
        var chatWindow = _chatWindow;
        if (chatWindow is null) return;

        _manager?.SetChatVisible(true);
        var settingsForeground = _manager?.IsSettingsForeground == true;
        if (!chatWindow.IsVisible) chatWindow.Show();
        chatWindow.Topmost = !settingsForeground;
        if (settingsForeground)
        {
            _manager?.OpenSettings();
            return;
        }
        chatWindow.Activate();
    }

    /// <summary>token 记账 → CareEngine（Phase 3 token 经济学：5000 token = 1 XP）。
    /// 注意：care 实例必须与 states 中的引用一致（由 AiCoordinator 传入 key）。</summary>
    private void RecordTokens(string petId, CareState care, int tokens)
    {
        var next = care.Clone();
        CareEngine.FeedTokens(next, tokens, DateTime.Now);
        try
        {
            var states = _store!.LoadCare();
            states[petId] = next;
            _store.SaveCare(states);
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex);
            return;
        }
        care.CopyFrom(next);
    }

    // ---- Phase 6h：全局快捷键 ----

    /// <summary>从持久化完整集合注册全局快捷键；透明隐藏窗口承载 HwndSource。</summary>
    private void RegisterGlobalHotkeys(HotkeySettings settings)
    {
        var host = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Opacity = 0,
            ShowActivated = false,
        };
        host.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(host).Handle;
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource.AddHook(HotkeyHook);
            _hotkeys = new HotkeyManager(hwnd);
            var result = _hotkeys.TryReplaceAll(settings);
            if (!result.Success)
            {
                MessageBox.Show(
                    HotkeySettingsCoordinator.DescribeRuntimeFailure(result, _i18n),
                    _i18n?.T("DesktopPet 快捷键") ?? "DesktopPet 快捷键",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };
        host.Show(); // 透明不可见，仅承载消息钩子
        _hotkeyHost = host;
    }

    private HotkeySettingsUpdateResult ApplyHotkeySettings(HotkeySettings candidate)
    {
        var coordinator = _hotkeyCoordinator
            ?? throw new InvalidOperationException("Global hotkeys are not initialized");
        var result = coordinator.Apply(candidate);
        if (result.PersistenceError is not null)
            PersistenceErrorPresenter.Report(result.PersistenceError);
        if (result.Success && result.Settings is not null)
            _manager?.ApplySettings(result.Settings);
        return result;
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Win32HotkeyRegistration.WM_HOTKEY) return IntPtr.Zero;
        var action = _hotkeys?.Resolve(wParam.ToInt32());
        if (action is null) return IntPtr.Zero;
        handled = true;
        switch (action)
        {
            case HotkeyAction.TogglePets:
                _manager?.SetGlobalVisible(!(_manager?.GloballyVisible ?? true));
                break;
            case HotkeyAction.ToggleMode:
                CycleOutputMode();
                break;
            case HotkeyAction.OpenSettings:
                _manager?.OpenSettings();
                break;
            case HotkeyAction.Quit:
                Shutdown();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Ctrl+Alt+M：弹幕 → 对话 → 静默 循环切换（立即生效 + 持久化）。</summary>
    private void CycleOutputMode()
    {
        var next = _modeService?.Mode switch
        {
            OutputMode.Danmaku => "chat",
            OutputMode.Chat => "silent",
            _ => "danmaku",
        };
        ApplyOutputModeFromBall(next);
    }

    /// <summary>定位 AgentHost 进程：打包并排目录优先，开发期向上找 repo 构建产物。</summary>
    private static string ResolveAgentHostPath()
    {
        const string exeName = "DesktopPet.AgentHost.exe";
        var besideApp = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(besideApp)) return besideApp;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var probe = Path.Combine(dir.FullName, "src", "DesktopPet.AgentHost", "bin",
                "Debug", "net8.0-windows10.0.19041.0", "win-x64", exeName);
            if (File.Exists(probe)) return probe;
            probe = Path.Combine(dir.FullName, "src", "DesktopPet.AgentHost", "bin",
                "Debug", "net8.0-windows10.0.19041.0", exeName);
            if (File.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        return besideApp;
    }
}

using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using DesktopPet.App.Ai;
using DesktopPet.App.Bench;
using DesktopPet.App.Rendering;
using DesktopPet.App.Tray;
using DesktopPet.App.Windows;
using DesktopPet.Core.Care;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Hotkey;

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
    private HwndSource? _hotkeySource;
    private Window? _hotkeyHost;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例（对齐 Tauri 版 tauri_plugin_single_instance：第二实例直接退出，
        // Phase 4 接设置窗跳转）
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        var isBench = e.Args.Any(a => a.StartsWith("--bench-"));
        if (isBench) PetWindow.BenchLogEnabled = true;

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPet");
        _store = new FileJsonStore(dataDir);

        var store = isBench ? PetStoreModel.EmptyPetStore() : InitializeStore();
        _manager = new PetWindowManager(_store, new SpriteLoader(dataDir));
        _manager.Reconcile(store, _store.LoadGlobalVisibility());

        if (!isBench)
        {
            // 语言优先读已保存设置（设置页语言页）；首次启动才用系统检测
            var storedSettings = _store.LoadSettings();
            var i18n = new DesktopPet.Core.I18n.I18nService(
                storedSettings?.Lang ?? DesktopPet.Core.I18n.I18nService.Detect());
            _manager.SetI18n(i18n);
            var settings = AppSettings.Normalize(storedSettings ?? AppSettings.Defaults(i18n.Lang));
            _manager.ApplySettings(settings);
            _manager.CreateFloatingBall(dataDir);
            _tray = new TrayController(_manager);

            // Phase 5：AI 编排（总开关/Agent 进程/输出模式/对话/记账）
            var chatWindow = new ChatWindow();
            _chatWindow = chatWindow;
            chatWindow.IsVisibleChanged += (_, _) =>
                _manager?.SetChatVisible(chatWindow.IsVisible && chatWindow.WindowState != WindowState.Minimized);
            chatWindow.StateChanged += (_, _) =>
                _manager?.SetChatVisible(chatWindow.IsVisible && chatWindow.WindowState != WindowState.Minimized);
            _manager.SettingsForegroundChanged += settingsForeground =>
                chatWindow.Topmost = !settingsForeground;
            _modeService = new ModeService(
                danmakuFactory: () => new DanmakuWindow(
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight),
                routeToChat: output =>
                {
                    ShowChatWindow();
                    _chatWindow?.AppendAssistantAsync(output.Text);
                },
                routeToBubble: text => _manager.BroadcastQuickBubble(text)); // 宠物头上气泡（全员）
            _ai = new AiCoordinator(_store, _modeService, _chatWindow, RecordTokens, ResolveAgentHostPath());
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
            _ai.ApplySettings(settings); // 应用已保存 AI 设置（默认关 = 不起 Agent）
            _chatWindow.TtsEnabled = settings.Ai.TtsEnabled; // 朗读按钮初始状态（AI 助手页开关）
            RegisterGlobalHotkeys(); // Phase 6h：Ctrl+Alt+H/M/S/Q

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
    }

    /// <summary>
    /// 加载/初始化宠物 store。Phase 3：tauri-export.json（Tauri localStorage 导出）
    /// 存在时一次性迁移（实例 + 养成状态），完成后删除导出文件。
    /// </summary>
    private PetStore InitializeStore()
    {
        var store = _store!.LoadPetStore();
        if (store is null)
        {
            var exportPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DesktopPet", "tauri-export.json");
            if (File.Exists(exportPath))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(exportPath));
                    var result = TauriMigration.Migrate(doc.RootElement, DateTime.Now);
                    if (result.HadData)
                    {
                        _store.SavePetStore(result.Store);
                        _store.SaveCare(result.Care);
                        store = result.Store;
                        File.Delete(exportPath);
                        System.Diagnostics.Debug.WriteLine($"Tauri migration imported {result.Store.Instances.Count} pet(s), {result.Care.Count} care state(s)");
                    }
                }
                catch (Exception)
                {
                    // 迁移失败不阻塞启动（下次启动重试）
                }
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

    protected override void OnExit(ExitEventArgs e)
    {
        _ai?.Dispose();
        _modeService?.Shutdown();
        _chatWindow?.Close();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        if (_hotkeySource is not null) _hotkeySource.RemoveHook(HotkeyHook);
        _hotkeyHost?.Close();
        _manager?.Shutdown();
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
        Dispatcher.BeginInvoke(() =>
        {
            var welcome = new DesktopPet.App.Windows.WelcomeWindow(
                builtinPersonas: DesktopPet.Core.Personas.BuiltinPersonas.GetAll(),
                initialCallName: profile.CallName,
                selectedPersonaId: personas.SelectedId,
                onComplete: (callName, personaId) => ai.CompleteOnboarding(callName, personaId));
            welcome.Show();
        });
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
        _modeService?.SetMode(parsed);
        if (_ai is null || _store is null) return;
        var settings = AppSettings.Normalize(_store.LoadSettings()
            ?? AppSettings.Defaults(DesktopPet.Core.I18n.I18nService.Detect()));
        _ai.ApplySettings(settings with { Ai = settings.Ai with { OutputMode = mode } });
        _store.SaveSettings(settings with { Ai = settings.Ai with { OutputMode = mode } });
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
        CareEngine.FeedTokens(care, tokens, DateTime.Now);
        var states = _store!.LoadCare();
        states[petId] = care;
        _store.SaveCare(states);
    }

    // ---- Phase 6h：全局快捷键 ----

    /// <summary>注册 Ctrl+Alt+H/M/S/Q（RegisterHotKey P/Invoke；透明隐藏窗口承载 HwndSource）。</summary>
    private void RegisterGlobalHotkeys()
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
            _hotkeys.Register(HotkeyAction.TogglePets, HotkeyManager.ModControlAlt, (uint)'H');
            _hotkeys.Register(HotkeyAction.ToggleMode, HotkeyManager.ModControlAlt, (uint)'M');
            _hotkeys.Register(HotkeyAction.OpenSettings, HotkeyManager.ModControlAlt, (uint)'S');
            _hotkeys.Register(HotkeyAction.Quit, HotkeyManager.ModControlAlt, (uint)'Q');
        };
        host.Show(); // 透明不可见，仅承载消息钩子
        _hotkeyHost = host;
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

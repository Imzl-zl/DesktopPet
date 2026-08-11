using System.IO;
using System.Windows;
using DesktopPet.App.Hotkeys;
using DesktopPet.App.Interop;
using DesktopPet.App.Localization;
using DesktopPet.App.Rendering;
using DesktopPet.Core.Hotkeys;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Rendering;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Diagnostics;

namespace DesktopPet.App.Windows;

/// <summary>
/// 多实例窗口管理器：1:1 继承 Rust sync_desktop_pet_windows 语义 —— 每宠物一
/// 窗口（label 语义 = pet-{id}）、创建缺失/关闭多余/独立显隐；全局显隐对齐
/// set_desktop_pets_visible（pets-visible 文件 + 全窗口 show/hide）。位置：
/// 已保存（物理像素）优先，否则右下角默认排布。
/// </summary>
public sealed class PetWindowManager
{
    private Ai.AiCoordinator? _aiCoordinator;
    private Action<string>? _setOutputMode;
    private Action? _openChat;
    private Func<HotkeySettings, HotkeySettingsUpdateResult>? _applyHotkeys;
    private Func<AppLang, CancellationToken, Task<LanguageChangeResult>>? _changeLanguage;
    private DiagnosticExporter? _diagnosticExporter;
    private Func<CancellationToken, Task<FactoryResetResult>>? _factoryReset;
    private readonly Dictionary<string, PetWindow> _windows = new();
    private readonly Dictionary<string, PetPosition> _positions;
    private readonly IJsonStore _store;
    private readonly SpriteLoader _spriteLoader;
    private readonly IAppLogger _logger;
    private bool _globallyVisible = true;
    private bool _chatVisible;
    private bool _settingsForeground;
    private AppSettings _settings = null!;
    private FloatingBallWindow? _floatingBall;
    private DesktopPet.App.Settings.SettingsWindow? _settingsWindow;
    private DesktopPet.Core.I18n.I18nService? _i18n;

    /// <summary>内置预设池（ApplySettings 全量下发覆盖；默认值单一真值 = AppSettings）。</summary>
    public string PresetPoolJson { get; set; } =
        System.Text.Json.JsonSerializer.Serialize(
            DesktopPet.Core.Storage.AppSettings.Defaults(
                DesktopPet.Core.I18n.I18nService.Detect()).QuickBubblePresets);

    public bool GloballyVisible => _globallyVisible;
    public bool IsSettingsForeground => _settingsForeground;

    public event Action<bool>? GlobalVisibilityChanged;
    public event Action<bool>? SettingsForegroundChanged;

    public PetWindowManager(IJsonStore store, SpriteLoader spriteLoader, IAppLogger? logger = null)
    {
        _store = store;
        _spriteLoader = spriteLoader;
        _logger = logger ?? NullAppLogger.Instance;
        _positions = store.LoadPositions();
        _globallyVisible = store.LoadGlobalVisibility();
    }

    /// <summary>reconcile：wanted 集合 = 当前 store 实例；多退少补 + 显隐同步。</summary>
    public void Reconcile(PetStore store, bool globallyVisible)
    {
        _globallyVisible = globallyVisible;
        var wantedIds = new HashSet<string>(store.Instances.Select(i => i.Id));
        var retainedSlugs = store.Instances
            .Select(instance => instance.SpriteSlug)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (id, window) in _windows.ToList())
        {
            if (!wantedIds.Contains(id))
            {
                var slug = window.SpriteSlug;
                window.Close();
                _windows.Remove(id);
                if (!retainedSlugs.Contains(slug)) _spriteLoader.Evict(slug);
            }
        }

        var index = 0;
        foreach (var instance in store.Instances)
        {
            if (!_windows.TryGetValue(instance.Id, out var window))
            {
                window = new PetWindow(instance, _spriteLoader, _store, OnDragFinished, _logger);
                window.SetImportHandler(ImportSprite);
                window.SetBroadcastQuickBubble(BroadcastQuickBubble);
                if (_settings is not null) window.ApplySettings(_settings); // 全量（点击/气泡池/外观/漫游等）
                window.SpriteLoaded += () => _floatingBall?.ReloadPet(); // 精灵就绪 → 浮球球体刷新
                _windows[instance.Id] = window;
                PositionAndShow(window, instance, index);
            }
            else
            {
                var visible = instance.Visible && _globallyVisible;
                if (visible != window.IsVisible) window.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            }
            index++;
        }
        UpdateDesktopOverlayZOrder();
    }

    private void PositionAndShow(PetWindow window, PetInstance instance, int index)
    {
        var position = _positions.TryGetValue(instance.Id, out var saved)
            ? (saved.X, saved.Y)
            : DefaultPosition(index);
        window.ShowAt(position.X, position.Y);
        if (!(instance.Visible && _globallyVisible)) window.Hide();
    }

    /// <summary>默认排布：主屏右下角依次向左上堆叠（对齐 Rust default_pet_position）。</summary>
    private static (int X, int Y) DefaultPosition(int index)
    {
        var (width, height) = NativeMethods.PrimaryWorkAreaSize();
        var (x, y) = WindowPlacement.DefaultPetPosition(width, height, index);
        return ((int)x, (int)y);
    }

    /// <summary>全局显隐（托盘）：写 pets-visible 文件 + 全部宠物窗口 show/hide。
    /// 隐藏后恢复位置不漂移（窗口坐标不变，只切 Visibility）。</summary>
    public void SetGlobalVisible(bool visible)
    {
        _store.SaveGlobalVisibility(visible);
        _globallyVisible = visible;
        foreach (var window in _windows.Values)
        {
            window.Visibility = visible && window.PetVisible
                ? Visibility.Visible
                : Visibility.Hidden;
        }
        GlobalVisibilityChanged?.Invoke(visible);
    }

    /// <summary>bench 模式用：摆单只宠物到指定物理坐标并返回窗口。</summary>
    public PetWindow ShowBenchPet(int physicalX, int physicalY)
    {
        var instance = new PetInstance
        {
            Id = PetStoreModel.NewPetInstanceId(),
            Name = "Bench Pet",
            SpriteSlug = "placeholder",
            Visible = true,
            Size = 100,
            RoamEnabled = false,
            RoamMode = RoamMode.Stay,
            RoamSpeed = 5,
            WanderPauseMinMs = Pause.DefaultWanderPauseMinMs,
            WanderPauseMaxMs = Pause.DefaultWanderPauseMaxMs,
            ReactsToActivity = false,
        };
        var window = new PetWindow(instance, _spriteLoader, _store, OnDragFinished, _logger);
        window.SetImportHandler(ImportSprite);
        window.SetBroadcastQuickBubble(BroadcastQuickBubble);
        window.SetClickAction("none");
        window.SetQuickPresetPool(PresetPoolJson);
        window.SpriteLoaded += () => _floatingBall?.ReloadPet();
        _windows[instance.Id] = window;
        window.ShowAt(physicalX, physicalY);
        return window;
    }

    public IReadOnlyList<PetWindow> VisibleWindows => _windows.Values.ToList();

    private void OnDragFinished(PetWindow window, int x, int y)
    {
        var position = new PetPosition(x, y);
        var next = PetPositionsFile.Update(_positions, window.PetId, position);
        _store.SavePositions(next);
        _positions[window.PetId] = position;
    }

    /// <summary>
    /// 导入精灵：保存本地缓存 + 创建宠物实例 + 重建窗口（拖拽文件到宠物窗口触发）。
    /// </summary>
    public void ImportSprite(byte[] bytes, string suggestedName)
    {
        var id = PetStoreModel.NewPetInstanceId();
        var instance = new PetInstance
        {
            Id = id,
            Name = suggestedName.Length > 0 ? suggestedName[..Math.Min(40, suggestedName.Length)] : "New Pet",
            SpriteSlug = id,
            Visible = true,
            Size = 100,
            RoamEnabled = true,
            RoamMode = RoamMode.Wander,
            RoamSpeed = 5,
            WanderPauseMinMs = Pause.DefaultWanderPauseMinMs,
            WanderPauseMaxMs = Pause.DefaultWanderPauseMaxMs,
            ReactsToActivity = true,
        };
        var current = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        var next = PetStoreModel.CreatePetInstance(current, instance);

        _spriteLoader.SaveLocal(id, bytes);
        try
        {
            _store.SavePetStore(next);
        }
        catch (JsonStoreException saveError)
        {
            try { _spriteLoader.DeleteLocal(id); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "Sprite import persistence failed and the staged sprite could not be removed",
                    new AggregateException(saveError, cleanupError));
            }
            throw;
        }
        Reconcile(next, _globallyVisible);
    }

    /// <summary>设置窗口入口（托盘/浮球右键）。</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            var settingsWindow = new DesktopPet.App.Settings.SettingsWindow(
                _store,
                this,
                _spriteLoader,
                _i18n ?? new I18nService(),
                _aiCoordinator,
                _applyHotkeys,
                _changeLanguage,
                () => _aiCoordinator?.AgentProcessId,
                _diagnosticExporter,
                _factoryReset,
                _logger);
            _settingsWindow = settingsWindow;
            settingsWindow.IsVisibleChanged += (_, _) => UpdateDesktopOverlayZOrder();
            settingsWindow.StateChanged += (_, _) => UpdateDesktopOverlayZOrder();
            settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                UpdateDesktopOverlayZOrder();
            };
        }
        if (_settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        SetDesktopOverlaysTopmost(false);
        _settingsWindow.Show();
        UpdateDesktopOverlayZOrder();
    }

    /// <summary>聊天或设置处于前台时，让桌宠留在窗口后方，避免遮挡交互控件。</summary>
    public void SetChatVisible(bool visible)
    {
        _chatVisible = visible;
        UpdateDesktopOverlayZOrder();
    }

    private void UpdateDesktopOverlayZOrder()
    {
        var settingsWindow = _settingsWindow;
        var settingsVisible = settingsWindow is { IsVisible: true }
            && settingsWindow.WindowState != WindowState.Minimized;
        SetDesktopOverlaysTopmost(!settingsVisible && !_chatVisible);
        if (_settingsForeground == settingsVisible) return;

        _settingsForeground = settingsVisible;
        SettingsForegroundChanged?.Invoke(settingsVisible);
    }

    private void SetDesktopOverlaysTopmost(bool topmost)
    {
        foreach (var window in _windows.Values)
        {
            window.Topmost = topmost;
            window.SetDesktopInteractionSuspended(!topmost);
        }
        if (_floatingBall is not null)
        {
            _floatingBall.Topmost = topmost;
            _floatingBall.SetDesktopInteractionSuspended(!topmost);
        }
    }

    /// <summary>注入 i18n，并初始化已创建窗口的静态文案。</summary>
    public void SetI18n(I18nService i18n)
    {
        _i18n = i18n;
        foreach (var window in _windows.Values) window.ApplyLocalization(i18n);
    }

    public void ApplyLocalization()
    {
        if (_i18n is null) return;
        foreach (var window in _windows.Values) window.ApplyLocalization(_i18n);
        _floatingBall?.ApplyLocalization(_i18n);
        _settingsWindow?.ApplyLocalization();
    }

    /// <summary>设置窗跳转 AI 助手页（对话窗人格快捷切换入口）。</summary>
    public void NavigateSettingsToAi()
        => (_settingsWindow as DesktopPet.App.Settings.SettingsWindow)?.NavigateTo("ai");

    /// <summary>外部路径（浮球/热键）改动设置后通知设置窗刷新（防旧快照回滚）。</summary>
    public void RefreshSettingsWindow()
        => (_settingsWindow as DesktopPet.App.Settings.SettingsWindow)?.RefreshFromStore();

    /// <summary>注入 AI 编排器（设置窗口 AI 页用）。</summary>
    public void SetAiCoordinator(Ai.AiCoordinator? coordinator) => _aiCoordinator = coordinator;

    /// <summary>注入完整快捷键集合提交回调。</summary>
    public void SetHotkeySettingsHandler(Func<HotkeySettings, HotkeySettingsUpdateResult>? handler)
        => _applyHotkeys = handler;

    public void SetLanguageChangeHandler(
        Func<AppLang, CancellationToken, Task<LanguageChangeResult>>? handler)
        => _changeLanguage = handler;

    public void SetDiagnosticExporter(DiagnosticExporter? exporter)
        => _diagnosticExporter = exporter;

    public void SetFactoryResetHandler(Func<CancellationToken, Task<FactoryResetResult>>? handler)
        => _factoryReset = handler;

    /// <summary>注入浮球 AI 输出模式切换回调（bubble/danmaku/chat/silent）。</summary>
    public void SetOutputModeHandler(Action<string>? handler) => _setOutputMode = handler;

    /// <summary>对话窗打开回调（浮球“💬 聊天”按钮）。浮球可能已创建（回调后置），需同步更新。</summary>
    public void SetOpenChatHandler(Action? handler)
    {
        _openChat = handler;
        _floatingBall?.SetOpenChat(handler);
    }

    /// <summary>应用设置到所有宠物窗口（对齐 Tauri 版 listen/emit 语义）：
    /// 点击动作/气泡池/时长 + 外观（主题/不透明度/字号/字体）/尺寸/浮动/闲谈/漫游。</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _settingsWindow?.ApplySettingsSnapshot(settings);
        PresetPoolJson = System.Text.Json.JsonSerializer.Serialize(settings.QuickBubblePresets);
        foreach (var window in _windows.Values)
        {
            window.ApplySettings(settings);
        }
    }

    /// <summary>实例配置变化（动作页保存）→ 对应用口即时生效（无需重建窗口）。</summary>
    public void ApplyInstance(PetInstance instance)
    {
        if (_windows.TryGetValue(instance.Id, out var window))
        {
            window.ApplyInstance(instance);
        }
    }

    /// <summary>快速气泡广播：浮球发送 → 全员同时说（对齐 emit(&quot;quick-bubble&quot; target all）。</summary>
    public void BroadcastQuickBubble(string text)
    {
        foreach (var window in _windows.Values)
        {
            window.ShowBroadcastQuickBubble(text);
        }
    }

    /// <summary>创建浮球窗口（球内活体宠物 = 选中实例精灵）。</summary>
    public void CreateFloatingBall(string dataDirectory)
    {
        if (_floatingBall is not null) return;
        _floatingBall = new FloatingBallWindow(
            BroadcastQuickBubble,
            () => PresetPoolJson,
            SelectedSpriteSheet,
            OpenSettings,
            dataDirectory,
            _setOutputMode,
            _openChat,
            _i18n,
            _logger);
        _floatingBall.Show();
        UpdateDesktopOverlayZOrder();
    }

    /// <summary>选中实例的共享精灵（浮球内活体宠物，复用窗口缓存不重复解码）。</summary>
    private SpriteSheet? SelectedSpriteSheet()
    {
        var store = _store.LoadPetStore();
        var selected = store is null ? null : PetStoreModel.SelectedPetInstance(store);
        if (selected is null && store is { Instances.Count: > 0 }) selected = store.Instances[0];
        return selected is null ? null : _spriteLoader.TryGetCached(selected.SpriteSlug);
    }

    public void Shutdown()
    {
        _floatingBall?.Close();
        _floatingBall = null;
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }
        _windows.Clear();
        _spriteLoader.Dispose();
    }
}

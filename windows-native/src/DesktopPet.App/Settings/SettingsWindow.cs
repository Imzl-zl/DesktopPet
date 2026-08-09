using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Hotkeys;
using DesktopPet.App.Diagnostics;
using DesktopPet.App.Localization;
using DesktopPet.App.Rendering;
using DesktopPet.App.Windows;
using DesktopPet.App.Tts;
using DesktopPet.Core.Tts;
using DesktopPet.Core.Care;
using DesktopPet.Core.Hotkeys;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Rendering;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;
using DesktopPet.Infra.Diagnostics;
using DesktopPet.Infra.Providers;
using DesktopPet.Infra.Tts;

namespace DesktopPet.App.Settings;

/// <summary>
/// 设置窗口（Lumen 2.0 设计语言：浅色精致、左侧图标导航 + 页头 + 分组卡片流）。
/// 页面：宠物（实时动画卡片/显隐/移除/导入）、外观、气泡、漫游、AI 助手、语言、关于。
/// 设置变更即保存并广播应用（对齐 Tauri 版 listen/emit 语义）。
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly IJsonStore _store;
    private readonly PetWindowManager _manager;
    private readonly SpriteLoader _spriteLoader;
    private readonly I18nService _i18n;
    private AppSettings _settings;

    private readonly List<PetPreviewCard> _previewCards = [];
    private readonly DispatcherTimer _previewTimer;
    private string _currentPage = "pets";
    private readonly Dictionary<string, Button> _navButtons = [];

    // ---- 动作页状态（会话内；hover 预览用单一 timer，离开页面即停）----
    private string? _actionsPetId;
    private PetAnimationSettings _actionsDraft = PetAnimationResolver.Normalize(null);
    private SpriteSheet? _actionsSheet;
    private ContentControl? _actionsGridHost;
    private DispatcherTimer? _clipHoverTimer;
    private (Image Image, int ClipIndex)? _hoverTarget;
    private int _hoverFrame;
    private readonly SpriteFrameBitmapSourceCache _frameSourceCache = new();

    private readonly Ai.AiCoordinator? _ai;
    private readonly Func<HotkeySettings, HotkeySettingsUpdateResult>? _applyHotkeys;
    private readonly Func<AppLang, CancellationToken, Task<LanguageChangeResult>>? _changeLanguage;
    private readonly Func<int?> _agentProcessId;
    private readonly DiagnosticExporter? _diagnosticExporter;
    private readonly Func<CancellationToken, Task<FactoryResetResult>>? _factoryReset;
    private readonly IAppLogger _logger;
    private SpritePreviewWindow? _spritePreview;
    private ProcessMetricsMonitor? _metricsMonitor;
    private DispatcherTimer? _metricsTimer;

    public SettingsWindow(
        IJsonStore store,
        PetWindowManager manager,
        SpriteLoader spriteLoader,
        I18nService i18n,
        Ai.AiCoordinator? ai = null,
        Func<HotkeySettings, HotkeySettingsUpdateResult>? applyHotkeys = null,
        Func<AppLang, CancellationToken, Task<LanguageChangeResult>>? changeLanguage = null,
        Func<int?>? agentProcessId = null,
        DiagnosticExporter? diagnosticExporter = null,
        Func<CancellationToken, Task<FactoryResetResult>>? factoryReset = null,
        IAppLogger? logger = null)
    {
        _store = store;
        _manager = manager;
        _spriteLoader = spriteLoader;
        _i18n = i18n;
        Icon = AppIcons.WindowIcon();
        _ai = ai;
        _applyHotkeys = applyHotkeys;
        _changeLanguage = changeLanguage;
        _agentProcessId = agentProcessId ?? (() => null);
        _diagnosticExporter = diagnosticExporter;
        _factoryReset = factoryReset;
        _logger = logger ?? NullAppLogger.Instance;
        _settings = AppSettings.Normalize(store.LoadSettings() ?? AppSettings.Defaults(i18n.Lang));

        Title = "DesktopPet";
        Width = 840;
        Height = 640;
        MinWidth = 680;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("WindowBgBrush");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(BuildNavigation());
        _contentHost = new ContentControl();
        Grid.SetColumn(_contentHost, 1);
        root.Children.Add(_contentHost);

        Content = root;
        WpfLocalizer.ApplyNew(this, _i18n);

        // 共享预览 timer：驱动所有宠物卡片的实时动画（3fps）
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 3) };
        _previewTimer.Tick += (_, _) =>
        {
            foreach (var card in _previewCards) card.Advance();
        };
        _previewTimer.Start();
        ShowPage("pets");
    }

    private readonly ContentControl _contentHost;

    // ---- 设计系统访问（App.xaml 资源）----

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    private static CornerRadius Corner(string key) => (CornerRadius)Application.Current.FindResource(key);
    private static Effect Shadow(string key) => (Effect)Application.Current.FindResource(key);
    private static Style AppStyle(string key) => (Style)Application.Current.FindResource(key);

    // ---- 导航 ----

    private static System.Windows.Shapes.Path NavigationIcon(string id, Brush stroke)
    {
        var data = id switch
        {
            "pets" => "M4,8 L6,4 L9,7 L12,4 L14,8 V14 C14,17.3 11.8,20 9,20 C6.2,20 4,17.3 4,14 Z M7,12 L7.1,12 M11,12 L11.1,12 M8,15 C8.6,15.5 9.4,15.5 10,15",
            "appearance" => "M4,4 H14 V14 H4 Z M7,18 H11 M9,14 V18 M17,5 L20,8 M20,5 L17,8",
            "bubble" => "M3,4 H21 V16 H10 L5,20 V16 H3 Z M7,9 H17 M7,12 H14",
            "roam" => "M12,3 L15,9 L21,12 L15,15 L12,21 L9,15 L3,12 L9,9 Z M10,10 L14,14",
            "actions" => "M13,2 L4,14 H11 L10,22 L19,10 H12 Z",
            "ai" => "M12,3 L14,9 L20,11 L14,14 L12,20 L10,14 L4,11 L10,9 Z M18,3 L18.8,5.2 L21,6 L18.8,6.8 L18,9 L17.2,6.8 L15,6 L17.2,5.2 Z",
            "hotkeys" => "M6,5 H18 A3,3 0 0 1 21,8 V16 A3,3 0 0 1 18,19 H6 A3,3 0 0 1 3,16 V8 A3,3 0 0 1 6,5 Z M7,10 H9 M11,10 H13 M15,10 H17 M7,14 H13 M15,14 H17",
            "language" => "M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 M3,12 H21 M12,3 C15,6 15,18 12,21 M12,3 C9,6 9,18 12,21",
            _ => "M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 M12,10 V16 M12,7 L12.1,7",
        };
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Stroke = stroke,
            StrokeThickness = 1.65,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    private StackPanel BuildNavigation()
    {
        var nav = new StackPanel { Background = Brush("NavBgBrush") };
        var navContent = new StackPanel
        {
            Margin = new Thickness(16, 18, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        nav.Children.Add(navContent);

        var brand = new Border
        {
            Width = 42,
            Height = 42,
            Background = Brush("AccentSoftBrush"),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 0, 0, 20),
            ToolTip = "DesktopPet",
            Child = new TextBlock
            {
                Text = "DP",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        navContent.Children.Add(brand);

        var pages = new (string Id, string Label)[]
        {
            ("pets", "宠物"),
            ("actions", "动作"),
            ("appearance", "外观"),
            ("bubble", "气泡"),
            ("roam", "漫游"),
            ("ai", "AI 助手"),
            ("hotkeys", "快捷键"),
            ("language", "语言"),
            ("about", "关于"),
        };
        foreach (var (id, label) in pages)
        {
            var button = new Button
            {
                Tag = id,
                Width = 44,
                Height = 44,
                Margin = new Thickness(0, 0, 0, 6),
                Style = AppStyle("ButtonGhostStyle"),
                Padding = new Thickness(0),
                ToolTip = label,
                Content = NavigationIcon(id, Brush("TextSecondaryBrush")),
            };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += (_, _) => ShowPage(id);
            _navButtons[id] = button;
            navContent.Children.Add(button);
        }
        return nav;
    }

    private void UpdateNavSelection(string selectedId)
    {
        foreach (var (id, button) in _navButtons)
        {
            var selected = id == selectedId;
            button.Background = selected ? Brush("AccentSoftBrush") : Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            if (button.Content is System.Windows.Shapes.Path icon)
            {
                icon.Stroke = selected ? Brush("AccentBrush") : Brush("TextSecondaryBrush");
            }
        }
    }

    /// <summary>外部跳转（对话窗人格切换入口）。</summary>
    public void NavigateTo(string id)
    {
        ShowPage(id);
        if (!IsVisible) Show();
    }

    /// <summary>
    /// 外部路径（浮球/热键切换输出模式等）改动设置后刷新：重读 store 防旧快照回滚。
    /// 修复：原实现 _settings 为构造时快照，外部改模式后设置页任意保存会回滚旧值。
    /// </summary>
    public void ApplyLocalization()
    {
        WpfLocalizer.RefreshTracked(this, _i18n);
        if (IsVisible) ShowPage(_currentPage);
        foreach (Window owned in OwnedWindows)
        {
            if (owned is SpritePreviewWindow preview) preview.ApplyLocalization(_i18n);
            else WpfLocalizer.RefreshTracked(owned, _i18n);
        }
    }

    public void ApplySettingsSnapshot(AppSettings settings)
        => _settings = AppSettings.Normalize(settings);

    public void RefreshFromStore()
    {
        _settings = AppSettings.Normalize(_store.LoadSettings() ?? AppSettings.Defaults(_i18n.Lang));
        if (IsVisible) ShowPage(_currentPage); // 重建当前页显示新值（动作页会话内选择保留）
    }

    private void ShowPage(string id)
    {
        _currentPage = id;
        UpdateNavSelection(id);
        StopClipHover(); // 离开动作页 → 停止 hover 预览 timer（避免泄漏）
        StopDiagnostics();
        // 预览 timer 只驱动宠物卡片：离开宠物页即停止，避免对不可见卡片持续整帧绘制
        if (id == "pets")
        {
            if (!_previewTimer.IsEnabled) _previewTimer.Start();
        }
        else
        {
            _previewTimer.Stop();
        }
        var content = id switch
        {
            "pets" => BuildPetsPage(),
            "actions" => BuildActionsPage(),
            "appearance" => BuildAppearancePage(),
            "bubble" => BuildBubblePage(),
            "roam" => BuildRoamPage(),
            "ai" => BuildAiPage(),
            "hotkeys" => BuildHotkeysPage(),
            "language" => BuildLanguagePage(),
            _ => BuildAboutPage(),
        };
        _contentHost.Content = content;
        WpfLocalizer.ApplyNew(content, _i18n);
    }

    // ---- 页面骨架 ----

    /// <summary>页头：大标题 + 副标题。</summary>
    private static StackPanel PageHeader(string title, string subtitle)
    {
        var header = new StackPanel { Margin = new Thickness(0, 24, 0, 16) };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
        });
        header.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        return header;
    }

    /// <summary>页面滚动容器。</summary>
    private static ScrollViewer PageScroller(UIElement content, UIElement? header = null)
    {
        var stack = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (header is not null) stack.Children.Add(header);
        stack.Children.Add(content);
        var scroll = new ScrollViewer
        {
            Padding = new Thickness(28, 0, 28, 24),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        scroll.Content = stack;
        return scroll;
    }

    /// <summary>卡片：白底圆角 + 柔和阴影。</summary>
    private static Border Card(UIElement content, double margin = 12, Thickness? padding = null)
    {
        var border = new Border
        {
            Background = Brush("CardBgBrush"),
            BorderBrush = Brush("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = Corner("RadiusCard"),
            Padding = padding ?? new Thickness(18, 16, 18, 16),
            Margin = new Thickness(0, 0, 0, margin),
            Effect = Shadow("ShadowCard"),
            Child = content,
        };
        return border;
    }

    /// <summary>带小标题的卡片。</summary>
    private static Border SectionCard(string title, UIElement content, double margin = 12)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        stack.Children.Add(content);
        return Card(stack, margin);
    }

    /// <summary>表单标签。</summary>
    private static TextBlock FormLabel(string text, Thickness margin = default)
        => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = margin == default ? new Thickness(0, 0, 0, 5) : margin,
        };

    // ---- 动作页 ----

    private UIElement BuildActionsPage()
    {
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        var stack = new StackPanel();
        stack.Children.Add(PageHeader("动作", "为每只宠物选择动画动作与触发行为（每只宠物独立保存）"));

        if (store.Instances.Count == 0)
        {
            stack.Children.Add(Card(new TextBlock
            {
                Text = "还没有宠物。先到「宠物」页导入一张精灵图，再来配置动作。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondaryBrush"),
            }));
            return PageScroller(stack);
        }

        // 宠物选择器（会话内记住；不写入 SelectedId，避免影响浮球/默认宠物语义）
        var targetId = _actionsPetId ?? store.SelectedId ?? store.Instances[0].Id;
        if (store.Instances.All(i => i.Id != targetId)) targetId = store.Instances[0].Id;
        var picker = new ComboBox
        {
            MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 13,
        };
        WpfLocalizer.SetFormattedAutomationName(picker, "动作宠物选择器", _i18n);
        foreach (var pet in store.Instances)
        {
            picker.Items.Add(pet.Name);
        }
        picker.SelectionChanged += (_, _) =>
        {
            var picked = store.Instances[Math.Max(0, picker.SelectedIndex)];
            if (_actionsPetId == picked.Id) return;
            var initializing = _actionsPetId is null; // 页面构建时的初始化赋值：只记录不重建
            _actionsPetId = picked.Id;
            if (!initializing)
            {
                StopClipHover();
                _contentHost.Content = BuildActionsPage(); // 重建编辑器（切换宠物）
            }
        };
        picker.SelectedIndex = store.Instances.TakeWhile(i => i.Id != targetId).Count(); // 赋值触发 handler（先挂后赋）
        var pickerCard = new StackPanel();
        pickerCard.Children.Add(new TextBlock
        {
            Text = "选择宠物",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        pickerCard.Children.Add(picker);
        stack.Children.Add(Card(pickerCard));

        var instance = store.Instances.First(i => i.Id == targetId);
        stack.Children.Add(BuildActionEditor(instance));
        return PageScroller(stack);
    }

    /// <summary>动作编辑器：idle 播放列表（开关/模式/间隔/多选）+ 触发器单选网格。</summary>
    private UIElement BuildActionEditor(PetInstance instance)
    {
        _actionsDraft = PetAnimationResolver.Normalize(instance.Actions);
        _actionsSheet = null;
        _triggerGridHosts.Clear();

        var editor = new StackPanel();

        // ---- 待机播放列表卡 ----
        var idleCard = new StackPanel();
        var idleEnabled = new CheckBox
        {
            Content = "待机动作轮播",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = _actionsDraft.IdleEnabled,
            FontSize = 13,
        };
        idleEnabled.Click += (_, _) =>
        {
            _actionsDraft = _actionsDraft with { IdleEnabled = idleEnabled.IsChecked == true };
            SaveActions(instance.Id);
        };
        idleCard.Children.Add(idleEnabled);
        idleCard.Children.Add(new TextBlock
        {
            Text = "开启后按下面选中的动作循环播放；关闭 = 只播待机默认动作。至少保留一个待机动作。",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        // 模式（随机/顺序）
        var modePanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        modePanel.Children.Add(new TextBlock
        {
            Text = "播放模式",
            FontSize = 12,
            Foreground = Brush("TextPrimaryBrush"),
        });
        var modeRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (value, label, tip) in new[]
        {
            ("random", "随机", "每次随机挑一个动作，可能有重复"),
            ("sequential", "顺序", "按选中顺序循环，每个动作都会轮到"),
        })
        {
            var radio = new RadioButton
            {
                Content = label + "（" + tip + "）",
                GroupName = "idle-mode",
                IsChecked = _actionsDraft.IdleMode == value,
                Margin = new Thickness(0, 0, 20, 0),
            };
            radio.Click += (_, _) =>
            {
                _actionsDraft = _actionsDraft with { IdleMode = value };
                SaveActions(instance.Id);
            };
            modeRow.Children.Add(radio);
        }
        modePanel.Children.Add(modeRow);
        idleCard.Children.Add(modePanel);

        // 间隔
        var interval = new Slider
        {
            Minimum = PetAnimationResolver.MinIdleIntervalSeconds,
            Maximum = PetAnimationResolver.MaxIdleIntervalSeconds,
            Value = _actionsDraft.IdleIntervalSeconds,
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        WpfLocalizer.SetFormattedAutomationName(interval, "待机动作间隔", _i18n);
        var intervalValue = new TextBlock
        {
            Text = $"{_actionsDraft.IdleIntervalSeconds}s",
            Margin = new Thickness(12, 10, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        interval.ValueChanged += (_, e) => intervalValue.Text = $"{e.NewValue:0}s";
        CommitSliderOnRelease(interval, () =>
        {
            _actionsDraft = _actionsDraft with { IdleIntervalSeconds = (int)interval.Value };
            SaveActions(instance.Id);
        });
        idleCard.Children.Add(Row(interval, intervalValue));
        idleCard.Children.Add(new TextBlock
        {
            Text = "多久切换一个待机动作（1-60 秒）",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 2, 0, 0),
        });

        // 网格占位：精灵加载后填充（复用共享解码缓存，不重复下载）
        _actionsGridHost = new ContentControl();
        idleCard.Children.Add(_actionsGridHost);
        editor.Children.Add(SectionCard("待机动作（多选：点选加入轮播）", idleCard));

        // ---- 触发器绑定卡 ----
        var triggerCard = new StackPanel();

        // 触发器行为时长（每宠物独立保存；复用待机间隔滑块的编辑模式）
        foreach (var (property, label, tip) in new[]
        {
            ("click", "点击动作时长", "点击宠物时播放该动作的时长"),
            ("celebrate", "庆祝时长", "升级/成就达成时庆祝动作与气泡的时长"),
        })
        {
            var duration = new Slider
            {
                Minimum = PetAnimationResolver.MinDurationSeconds,
                Maximum = PetAnimationResolver.MaxDurationSeconds,
                Value = property == "click" ? _actionsDraft.ClickDurationSeconds : _actionsDraft.CelebrateDurationSeconds,
                Width = 220,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };
            WpfLocalizer.SetFormattedAutomationName(
                duration,
                property == "click" ? "点击动作时长" : "庆祝时长",
                _i18n);
            var durationValue = new TextBlock
            {
                Text = $"{(property == "click" ? _actionsDraft.ClickDurationSeconds : _actionsDraft.CelebrateDurationSeconds)}s",
                Margin = new Thickness(12, 4, 0, 0),
                FontSize = 12,
                Foreground = Brush("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 40,
            };
            duration.ValueChanged += (_, e) => durationValue.Text = $"{e.NewValue:0}s";
            CommitSliderOnRelease(duration, () =>
            {
                _actionsDraft = property == "click"
                    ? _actionsDraft with { ClickDurationSeconds = (int)duration.Value }
                    : _actionsDraft with { CelebrateDurationSeconds = (int)duration.Value };
                SaveActions(_actionsPetId ?? "");
            });
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            var durationLabel = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush("TextPrimaryBrush"),
            };
            WpfLocalizer.SetFormattedText(
                durationLabel,
                "{0}（{1}，{2}-{3} 秒）",
                _i18n,
                WpfLocalizer.Localize(label),
                WpfLocalizer.Localize(tip),
                PetAnimationResolver.MinDurationSeconds,
                PetAnimationResolver.MaxDurationSeconds);
            block.Children.Add(durationLabel);
            block.Children.Add(Row(duration, durationValue));
            triggerCard.Children.Add(block);
        }

        foreach (var (trigger, label, tip) in new[]
        {
            (PetActionTriggers.Click, "点击", "点击宠物时播放一轮（时长见上方滑块）"),
            (PetActionTriggers.Celebrate, "庆祝", "升级/成就达成时播放（时长见上方滑块）"),
            (PetActionTriggers.RoamRight, "向右走", "漫游向右移动时播放"),
            (PetActionTriggers.RoamLeft, "向左走", "漫游向左移动时播放"),
            (PetActionTriggers.Drag, "拖拽", "按住拖走时播放；无绑定则保持当前动作"),
        })
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var triggerLabel = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush("TextPrimaryBrush"),
                Margin = new Thickness(0, 0, 0, 6),
            };
            WpfLocalizer.SetFormattedText(triggerLabel, "{0} — {1}", _i18n,
                WpfLocalizer.Localize(label),
                WpfLocalizer.Localize(tip));
            block.Children.Add(triggerLabel);
            var reset = new Button
            {
                Content = "恢复默认",
                Style = AppStyle("ButtonDefaultStyle"),
                Height = 24,
                FontSize = 11,
                Padding = new Thickness(10, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
            };
            reset.Click += (_, _) =>
            {
                var bind = new Dictionary<string, int>(_actionsDraft.Bind);
                bind.Remove(trigger);
                _actionsDraft = _actionsDraft with { Bind = bind };
                SaveActions(instance.Id);
                RefreshActionsGrid(instance.Id);
            };
            block.Children.Add(reset);
            var gridHost = new ContentControl();
            block.Children.Add(gridHost);
            triggerCard.Children.Add(block);
            _triggerGridHosts[trigger] = gridHost;
        }
        editor.Children.Add(SectionCard("触发器动作（单选）", triggerCard));

        // 异步加载精灵 → 填充所有网格
        _ = LoadActionsSheetAsync(instance);
        return editor;
    }

    private readonly Dictionary<string, ContentControl> _triggerGridHosts = new();

    private async Task LoadActionsSheetAsync(PetInstance instance)
    {
        var sheet = await _spriteLoader.LoadAsync(instance.SpriteSlug);
        if (sheet is null || _actionsSheet is not null) return;
        _actionsSheet = sheet;
        RefreshActionsGrid(instance.Id);
    }

    /// <summary>重建 clip 网格（选中态/精灵加载后刷新；不重建整个页面）。</summary>
    private void RefreshActionsGrid(string petId)
    {
        StopClipHover(); // 重建前停止 hover 预览（避免 timer 引用已移除的 Image）
        var sheet = _actionsSheet;
        if (sheet is null || _actionsGridHost is null) return;
        _actionsGridHost.Content = BuildClipGrid(sheet, multiple: true);
        foreach (var (trigger, host) in _triggerGridHosts)
        {
            host.Content = BuildClipGrid(sheet, multiple: false, trigger: trigger);
        }
    }

    /// <summary>
    /// clip 网格：每行一个卡片（首帧缩略图 + 行号）；hover 用单一 timer 播放预览；
    /// 点击切换选中（idle 多选至少保留一个；触发器单选点击即绑定）。
    /// </summary>
    private UIElement BuildClipGrid(SpriteSheet sheet, bool multiple, string? trigger = null)
    {
        var wrap = new WrapPanel();
        for (var i = 0; i < sheet.Clips.Count; i++)
        {
            var clip = sheet.Clips[i];
            var selected = multiple
                ? _actionsDraft.IdleClips.Contains(i)
                : _actionsDraft.Bind.TryGetValue(trigger!, out var bound) && bound == i;

            var image = new Image
            {
                Width = 48,
                Height = 48,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            if (clip.Count > 0) image.Source = _frameSourceCache.GetOrCreate(clip[0]);

            var label = new TextBlock
            {
                Text = $"#{i}",
                FontSize = 10,
                Foreground = Brush("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var inner = new StackPanel();
            inner.Children.Add(image);
            inner.Children.Add(label);

            // Button + 自定义模板（圆角卡片外观；Button 保证 UIAutomation 可发现/可点击）
            var template = new ControlTemplate(typeof(Button));
            var frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(presenter);
            template.VisualTree = frame;

            var cellButton = new Button
            {
                Background = selected ? Brush("AccentSoftBrush") : Brush("WindowBgBrush"),
                BorderBrush = selected ? Brush("AccentBrush") : Brush("StrokeBrush"),
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                Width = 64,
                Height = 74,
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(0),
                Template = template,
                Content = inner,
            };
            var clipIndex = i;
            WpfLocalizer.SetFormattedAutomationName(
                cellButton,
                "动作格子 #{0}",
                _i18n,
                clipIndex);
            cellButton.Click += (_, _) => ToggleClip(trigger, clipIndex, multiple);
            cellButton.MouseEnter += (_, _) => StartClipHover(image, clipIndex);
            cellButton.MouseLeave += (_, _) => StopClipHover();
            wrap.Children.Add(cellButton);
        }
        return wrap;
    }

    private void ToggleClip(string? trigger, int clip, bool multiple)
    {
        if (multiple)
        {
            var set = _actionsDraft.IdleClips.ToHashSet();
            if (set.Contains(clip))
            {
                if (set.Count <= 1) return; // 至少保留一个待机动作
                set.Remove(clip);
            }
            else
            {
                set.Add(clip);
            }
            _actionsDraft = _actionsDraft with { IdleClips = set.OrderBy(x => x).ToList() };
        }
        else
        {
            var bind = new Dictionary<string, int>(_actionsDraft.Bind);
            bind[trigger!] = clip;
            _actionsDraft = _actionsDraft with { Bind = bind };
        }
        SaveActions(_actionsPetId ?? "");
        RefreshActionsGrid(_actionsPetId ?? "");
    }

    private void SaveActions(string petId)
    {
        if (string.IsNullOrEmpty(petId)) return;
        UpdateInstance(petId, new PetInstancePatch { Actions = PetAnimationResolver.Normalize(_actionsDraft) });
    }

    // ---- 动作页 hover 预览（单一 125ms timer，约 8 FPS，对齐旧 Tauri hover 节奏）----

    private void StartClipHover(Image image, int clipIndex)
    {
        StopClipHover();
        _hoverTarget = (image, clipIndex);
        _hoverFrame = 0;
        _clipHoverTimer ??= CreateClipHoverTimer(); // Tick 只订阅一次（避免重复订阅叠加）
        _clipHoverTimer.Start();
    }

    private DispatcherTimer CreateClipHoverTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        timer.Tick += (_, _) => OnClipHoverTick();
        return timer;
    }

    private void StopClipHover()
    {
        _hoverTarget = null;
        if (_clipHoverTimer is not null) _clipHoverTimer.Stop();
    }

    private void OnClipHoverTick()
    {
        if (_hoverTarget is not { } target || _actionsSheet is not { } sheet)
        {
            _clipHoverTimer?.Stop();
            return;
        }
        if (target.ClipIndex >= sheet.Clips.Count) return;
        var clip = sheet.Clips[target.ClipIndex];
        if (clip.Count == 0) return;
        target.Image.Source = _frameSourceCache.GetOrCreate(clip[_hoverFrame++ % clip.Count]);
    }

    // ---- 宠物页 ----


    private UIElement BuildPetsPage()
    {
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        _previewCards.Clear();

        var stack = new StackPanel();

        // 页头：标题 + 添加按钮
        var headerRow = new Grid { Margin = new Thickness(28, 22, 28, 14) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "我的宠物",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "管理桌面上的小伙伴",
            FontSize = 12,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        Grid.SetColumn(titleStack, 0);
        headerRow.Children.Add(titleStack);

        var add = new Button
        {
            Content = "添加宠物",
            Style = AppStyle("ButtonPrimaryStyle"),
            Height = 32,
            Padding = new Thickness(16, 4, 16, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        add.Click += async (_, _) => await ImportPetAsync();
        Grid.SetColumn(add, 1);
        headerRow.Children.Add(add);

        var scrollStack = new StackPanel();
        scrollStack.Children.Add(headerRow);

        var listStack = new StackPanel { Margin = new Thickness(28, 0, 28, 24) };
        if (store.Instances.Count == 0)
        {
            listStack.Children.Add(Card(new TextBlock
            {
                Text = "还没有宠物。点击右上角「添加宠物」导入一张精灵图，或直接把 PNG/WebP 拖到桌面宠物上。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondaryBrush"),
            }));
        }
        foreach (var instance in store.Instances)
        {
            listStack.Children.Add(Card(BuildPetCard(instance), margin: 12));
        }
        scrollStack.Children.Add(listStack);

        var scroll = new ScrollViewer
        {
            Content = scrollStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return scroll;
    }

    private Grid BuildPetCard(PetInstance instance)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var preview = new PetPreviewCard(_spriteLoader, instance.SpriteSlug);
        _previewCards.Add(preview);
        var previewHost = new Border
        {
            Background = Brush("WindowBgBrush"),
            BorderBrush = Brush("DividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Width = 96,
            Height = 96,
            Child = preview,
        };
        grid.Children.Add(previewHost);

        var details = new Grid
        {
            Margin = new Thickness(18, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var petNameText = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
        };
        WpfLocalizer.SetDynamicText(petNameText, instance.Name);
        identity.Children.Add(petNameText);
        var care = _store.LoadCare().GetValueOrDefault(instance.Id);
        var level = CareEngine.DisplayLevel(care?.Xp ?? 0);
        var stage = CareEngine.StageName(CareEngine.LevelForXp(care?.Xp ?? 0));
        var stageText = new TextBlock
        {
            FontSize = 10.5,
            Foreground = Brush("SuccessBrush"),
            FontWeight = FontWeights.SemiBold,
        };
        WpfLocalizer.SetFormattedText(
            stageText,
            "{0} · Lv {1}",
            _i18n,
            WpfLocalizer.Localize(stage),
            level);
        identity.Children.Add(new Border
        {
            Background = Brush("SuccessSoftBrush"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Child = stageText,
        });
        header.Children.Add(identity);

        var remove = new Button
        {
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 4,5 L 12,5 M 6,5 L 6,3 L 10,3 L 10,5 M 5,6 L 6,14 L 10,14 L 11,6"),
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Stroke = Brush("DangerBrush"),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            },
            Style = AppStyle("ButtonIconStyle"),
            Foreground = Brush("DangerBrush"),
            Width = 30,
            Height = 30,
            ToolTip = "移除这只宠物",
            VerticalAlignment = VerticalAlignment.Center,
        };
        WpfLocalizer.SetFormattedAutomationName(remove, "移除宠物", _i18n);
        remove.Click += (_, _) => RemoveInstance(instance.Id);
        Grid.SetColumn(remove, 1);
        header.Children.Add(remove);
        details.Children.Add(header);

        var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var visibleToggle = new CheckBox
        {
            Content = "显示在桌面",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = instance.Visible,
            Margin = new Thickness(0, 0, 16, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        visibleToggle.Checked += (_, _) => UpdateInstance(instance.Id, new PetInstancePatch { Visible = true });
        visibleToggle.Unchecked += (_, _) => UpdateInstance(instance.Id, new PetInstancePatch { Visible = false });
        controls.Children.Add(visibleToggle);

        // 屏幕事件评论：AI 主动互动的事件驱动评论（切窗口/久坐/摸鱼）是否分派给这只宠物；定时问候不受影响
        var reactsToggle = new CheckBox
        {
            Content = "屏幕事件评论",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = instance.ReactsToActivity,
            Margin = new Thickness(0, 0, 16, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        reactsToggle.Checked += (_, _) => UpdateInstance(instance.Id, new PetInstancePatch { ReactsToActivity = true });
        reactsToggle.Unchecked += (_, _) => UpdateInstance(instance.Id, new PetInstancePatch { ReactsToActivity = false });
        controls.Children.Add(reactsToggle);

        var persona = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        persona.Children.Add(new TextBlock
        {
            Text = "人格",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var personaCombo = new ComboBox
        {
            Width = 156,
            Height = 30,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var allPersonas = _ai?.Personas.MergeWithBuiltins() ?? [];
        personaCombo.Items.Add(_i18n.T("跟随全局"));
        foreach (var personaItem in allPersonas)
        {
            personaCombo.Items.Add(personaItem.Builtin
                ? _i18n.T(personaItem.Name)
                : personaItem.Name);
        }
        var currentIndex = instance.PersonaId is null ? 0
            : allPersonas.Select((item, index) => (item, index))
                .Where(x => x.item.Id == instance.PersonaId)
                .Select(x => x.index + 1)
                .DefaultIfEmpty(0)
                .First();
        personaCombo.SelectedIndex = currentIndex;
        personaCombo.SelectionChanged += (_, _) =>
        {
            if (personaCombo.SelectedIndex <= 0)
            {
                UpdateInstance(instance.Id, new PetInstancePatch { PersonaId = null });
            }
            else
            {
                var picked = allPersonas[personaCombo.SelectedIndex - 1];
                UpdateInstance(instance.Id, new PetInstancePatch { PersonaId = picked.Id });
            }
        };
        persona.Children.Add(personaCombo);
        controls.Children.Add(persona);

        Grid.SetRow(controls, 1);
        details.Children.Add(controls);
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);
        return grid;
    }

    private void UpdateInstance(string id, PetInstancePatch patch)
    {
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        store = PetStoreModel.UpdatePetInstance(store, id, patch);
        try
        {
            _store.SavePetStore(store);
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex, this);
            ShowPage("pets");
            return;
        }
        _manager.Reconcile(store, _manager.GloballyVisible);
        var updated = PetStoreModel.PetInstanceById(store, id);
        if (updated is not null) _manager.ApplyInstance(updated); // 动作配置即时生效（无需重建窗口）
    }

    private void RemoveInstance(string id)
    {
        var current = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        var removed = PetStoreModel.PetInstanceById(current, id);
        var next = PetStoreModel.RemovePetInstance(current, id);
        try
        {
            _store.SavePetStore(next);
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex, this);
            return;
        }
        _manager.Reconcile(next, _manager.GloballyVisible);
        if (removed is not null
            && next.Instances.All(instance => instance.SpriteSlug != removed.SpriteSlug))
        {
            try { _spriteLoader.DeleteLocal(removed.SpriteSlug); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    this,
                    _i18n.Format("宠物已移除，但精灵缓存清理失败：{0}", ex.Message),
                    "DesktopPet");
            }
        }
        ShowPage("pets");
    }

    private async Task ImportPetAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = _i18n.T("精灵图 (PNG/WebP)|*.png;*.webp"),
            Title = _i18n.T("导入宠物精灵图"),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            var sheet = SpriteSheet.Decode(bytes, Path.GetFileName(dialog.FileName));
            if (sheet is null)
            {
                MessageBox.Show(this, _i18n.T("无法解析精灵图（需要带透明通道的 PNG/WebP）"), "DesktopPet");
                return;
            }
            _spritePreview = new SpritePreviewWindow(
                sheet,
                bytes,
                Path.GetFileNameWithoutExtension(dialog.FileName),
                _i18n)
            {
                Owner = this,
            };
            try
            {
                if (_spritePreview.ShowDialog() == true)
                {
                    var (payload, name) = _spritePreview.ImportPayload;
                    _manager.ImportSprite(payload, name);
                    ShowPage("pets");
                }
            }
            finally
            {
                _spritePreview = null;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, _i18n.Format("导入失败：{0}", ex.Message), "DesktopPet");
        }
    }

    // ---- 外观页 ----

    private UIElement BuildAppearancePage()
    {
        var stack = new StackPanel();

        var theme = new StackPanel();
        var themeRadios = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (value, label) in new[] { ("system", "跟随系统"), ("light", "浅色"), ("dark", "深色") })
        {
            var radio = new RadioButton { Content = label, IsChecked = _settings.Theme == value, Margin = new Thickness(0, 0, 20, 0) };
            radio.Checked += (_, _) => Save(s => s with { Theme = value });
            themeRadios.Children.Add(radio);
        }
        theme.Children.Add(themeRadios);
        theme.Children.Add(new TextBlock
        {
            Text = "作用于宠物头顶的气泡配色（桌面宠物窗口本身透明）",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(SectionCard("主题", theme));

        var opacity = new Slider { Minimum = 30, Maximum = 100, Value = _settings.BubbleOpacity, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var opacityValue = new TextBlock
        {
            Text = $"{_settings.BubbleOpacity}%",
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        opacity.ValueChanged += (_, e) => opacityValue.Text = $"{e.NewValue:0}%";
        CommitSliderOnRelease(opacity, () => Save(s => s with { BubbleOpacity = (int)opacity.Value }));
        stack.Children.Add(SectionCard("气泡不透明度", Row(opacity, opacityValue)));

        var size = new Slider { Minimum = 70, Maximum = 130, Value = _settings.PetSizePercent, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var sizeValue = new TextBlock
        {
            Text = $"{_settings.PetSizePercent}%",
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        size.ValueChanged += (_, e) => sizeValue.Text = $"{e.NewValue:0}%";
        CommitSliderOnRelease(size, () => Save(s => s with { PetSizePercent = (int)size.Value }));
        stack.Children.Add(SectionCard("宠物尺寸", Row(size, sizeValue)));

        var animation = new CheckBox
        {
            Content = "精灵动画",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = _settings.AnimationEnabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        animation.Checked += (_, _) => Save(s => s with { AnimationEnabled = true });
        animation.Unchecked += (_, _) => Save(s => s with { AnimationEnabled = false });
        var animationCard = new StackPanel();
        animationCard.Children.Add(animation);
        animationCard.Children.Add(new TextBlock
        {
            Text = "关闭后宠物保持静止帧（适合低功耗/专注）；不影响点击、对话和漫游位置移动",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        stack.Children.Add(SectionCard("动画", animationCard));

        var idle = new CheckBox
        {
            Content = "显示闲谈气泡",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = _settings.ShowIdleChatter,
            VerticalAlignment = VerticalAlignment.Center,
        };
        idle.Checked += (_, _) => Save(s => s with { ShowIdleChatter = true });
        idle.Unchecked += (_, _) => Save(s => s with { ShowIdleChatter = false });
        stack.Children.Add(Card(idle));

        var chatter = new Slider { Minimum = 5, Maximum = 120, Value = _settings.IdleChatterIntervalSeconds, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var chatterValue = new TextBlock
        {
            Text = $"{_settings.IdleChatterIntervalSeconds}s",
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        chatter.ValueChanged += (_, e) => chatterValue.Text = $"{e.NewValue:0}s";
        CommitSliderOnRelease(chatter, () => Save(s => s with { IdleChatterIntervalSeconds = (int)chatter.Value }));
        var chatterCard = new StackPanel();
        chatterCard.Children.Add(Row(chatter, chatterValue));
        chatterCard.Children.Add(new TextBlock
        {
            Text = "多久换一句闲谈台词；调高减少打扰（仅『显示闲谈气泡』开启时生效）",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(SectionCard("闲谈频率", chatterCard));

        var bob = new CheckBox
        {
            Content = "待机浮动动画",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = _settings.BobAnimation,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bob.Checked += (_, _) => Save(s => s with { BobAnimation = true });
        bob.Unchecked += (_, _) => Save(s => s with { BobAnimation = false });
        stack.Children.Add(Card(bob, margin: 0));

        return PageScroller(stack, PageHeader("外观", "主题、尺寸与气泡显示"));
    }

    // ---- 气泡页 ----

    private UIElement BuildBubblePage()
    {
        var stack = new StackPanel();

        var presets = new TextBox
        {
            Text = string.Join("\n", _settings.QuickBubblePresets),
            Height = 120,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        presets.LostFocus += (_, _) => Save(s => s with
        {
            QuickBubblePresets = presets.Text.Split('\n')
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
        });
        stack.Children.Add(SectionCard("预设气泡池（每行一条）", presets));

        // 闲谈台词池：空数组 = 不显示闲谈（与预设池清空语义一致）
        var chatter = new TextBox
        {
            Text = string.Join("\n", _settings.IdleChatterLines ?? []),
            Height = 120,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        WpfLocalizer.SetFormattedAutomationName(chatter, "闲谈台词池", _i18n);
        chatter.LostFocus += (_, _) => Save(s => s with
        {
            IdleChatterLines = chatter.Text.Split('\n')
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
        });
        stack.Children.Add(SectionCard("闲谈台词池（每行一条，清空 = 不显示闲谈）", chatter));

        var hungry = new TextBox
        {
            Text = string.Join("\n", _settings.HungryLines ?? []),
            Height = 100,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        WpfLocalizer.SetFormattedAutomationName(hungry, "饥饿台词池", _i18n);
        hungry.LostFocus += (_, _) => Save(s => s with
        {
            HungryLines = hungry.Text.Split('\n')
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
        });
        stack.Children.Add(SectionCard("饥饿台词池（每行一条，饥饿时概率说出；清空 = 不提示）", hungry));

        var duration = new Slider { Minimum = 1, Maximum = 10, Value = _settings.QuickBubbleDurationSeconds, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var durationValue = new TextBlock
        {
            Text = $"{_settings.QuickBubbleDurationSeconds}s",
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        duration.ValueChanged += (_, e) => durationValue.Text = $"{e.NewValue:0}s";
        CommitSliderOnRelease(duration, () => Save(s => s with { QuickBubbleDurationSeconds = (int)duration.Value }));
        stack.Children.Add(SectionCard("气泡显示时长", Row(duration, durationValue)));

        var fontSize = new Slider { Minimum = 8, Maximum = 24, Value = _settings.FontSize, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var fontSizeValue = new TextBlock
        {
            Text = $"{_settings.FontSize}px",
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        fontSize.ValueChanged += (_, e) => fontSizeValue.Text = $"{e.NewValue:0}px";
        CommitSliderOnRelease(fontSize, () => Save(s => s with { FontSize = (int)fontSize.Value }));

        // 气泡字体族（system | rounded | mono；BubbleView.ApplyAppearance 消费）
        var font = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (value, label) in new[] { ("system", "系统默认"), ("rounded", "圆体"), ("mono", "等宽") })
        {
            var radio = new RadioButton { Content = label, IsChecked = _settings.FontFamily == value, Margin = new Thickness(0, 0, 20, 0) };
            radio.Checked += (_, _) => Save(s => s with { FontFamily = value });
            font.Children.Add(radio);
        }
        var fontCard = new StackPanel();
        fontCard.Children.Add(font);
        fontCard.Children.Add(new TextBlock
        {
            Text = "气泡文字字体：系统默认（Segoe UI Variable）/ 圆体（微软正黑）/ 等宽（Cascadia Mono）",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(SectionCard("气泡字体", fontCard));
        stack.Children.Add(SectionCard("气泡字体大小", Row(fontSize, fontSizeValue)));

        var click = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (value, label) in new[] { ("none", "无动作"), ("self", "单只随机说"), ("all", "全员随机说") })
        {
            var radio = new RadioButton { Content = label, IsChecked = _settings.LeftClickAction == value, Margin = new Thickness(0, 0, 20, 0) };
            radio.Checked += (_, _) => Save(s => s with { LeftClickAction = value });
            click.Children.Add(radio);
        }
        stack.Children.Add(SectionCard("点击宠物", click, margin: 12));

        // ---- 弹幕参数（仅「弹幕」输出模式生效；模式在 AI 助手页选择）----
        var danmakuCard = new StackPanel();
        var danmakuFontSize = new Slider { Minimum = 16, Maximum = 48, Value = _settings.DanmakuFontSize, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var danmakuFontSizeValue = new TextBlock
        {
            Text = $"{_settings.DanmakuFontSize}px",
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        danmakuFontSize.ValueChanged += (_, e) => danmakuFontSizeValue.Text = $"{e.NewValue:0}px";
        CommitSliderOnRelease(danmakuFontSize, () => Save(s => s with { DanmakuFontSize = (int)danmakuFontSize.Value }));
        danmakuCard.Children.Add(Stacked("弹幕字号", Row(danmakuFontSize, danmakuFontSizeValue)));

        var danmakuSpeed = new Slider { Minimum = 50, Maximum = 200, Value = _settings.DanmakuSpeedPercent, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var danmakuSpeedValue = new TextBlock
        {
            Text = $"{_settings.DanmakuSpeedPercent}%",
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        danmakuSpeed.ValueChanged += (_, e) => danmakuSpeedValue.Text = $"{e.NewValue:0}%";
        CommitSliderOnRelease(danmakuSpeed, () => Save(s => s with { DanmakuSpeedPercent = (int)danmakuSpeed.Value }));
        danmakuCard.Children.Add(Stacked("弹幕速度", Row(danmakuSpeed, danmakuSpeedValue)));

        var danmakuTracks = new Slider { Minimum = 4, Maximum = 20, Value = _settings.DanmakuTrackCount, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var danmakuTracksValue = new TextBlock
        {
            Text = _settings.DanmakuTrackCount.ToString(),
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        danmakuTracks.ValueChanged += (_, e) => danmakuTracksValue.Text = $"{e.NewValue:0}";
        CommitSliderOnRelease(danmakuTracks, () => Save(s => s with { DanmakuTrackCount = (int)danmakuTracks.Value }));
        danmakuCard.Children.Add(Stacked("弹幕密度", Row(danmakuTracks, danmakuTracksValue)));
        danmakuCard.Children.Add(new TextBlock
        {
            Text = "仅「弹幕」输出模式生效（AI 助手页选择）；轨道越多弹幕越密",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(SectionCard("弹幕样式", danmakuCard, margin: 0));

        return PageScroller(stack, PageHeader("气泡", "预设气泡与点击行为"));
    }

    // ---- 漫游页 ----

    private UIElement BuildRoamPage()
    {
        var stack = new StackPanel();
        var roam = _settings.Roam;

        var enabled = new CheckBox
        {
            Content = "启用漫游",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = roam.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        enabled.Checked += (_, _) => SaveRoam(roam with { Enabled = true });
        enabled.Unchecked += (_, _) => SaveRoam(roam with { Enabled = false });
        stack.Children.Add(Card(enabled));

        var mode = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (value, label) in new[]
        {
            (RoamMode.Stay, "待着不动"), (RoamMode.Wander, "四处闲逛"),
            (RoamMode.Cursor, "跟着鼠标"), (RoamMode.Climb, "爬窗口边缘"),
        })
        {
            var radio = new RadioButton { Content = label, IsChecked = roam.Mode == value, Margin = new Thickness(0, 0, 20, 0) };
            radio.Checked += (_, _) => SaveRoam(roam with { Mode = value });
            mode.Children.Add(radio);
        }
        var modeCard = new StackPanel();
        modeCard.Children.Add(mode);
        modeCard.Children.Add(new TextBlock
        {
            Text = "待着不动：留在原地；四处闲逛：随机散步；跟着鼠标：鼠标附近徘徊；爬窗口边缘：沿窗口边缘移动（部分模式受成长阶段限制）",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(SectionCard("漫游模式", modeCard));

        var speed = new Slider { Minimum = 1, Maximum = 10, Value = roam.Speed, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var speedValue = new TextBlock
        {
            Text = roam.Speed.ToString(),
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        speed.ValueChanged += (_, e) => speedValue.Text = $"{e.NewValue:0}";
        // 提交时基于 _settings.Roam 而非页面构建时的快照（否则与下方停顿滑块互相覆盖）
        CommitSliderOnRelease(speed, () => SaveRoam(_settings.Roam with { Speed = (int)speed.Value }));
        var speedCard = new StackPanel();
        speedCard.Children.Add(Row(speed, speedValue));
        speedCard.Children.Add(new TextBlock
        {
            Text = "移动速度（1-10）；调低适合安静桌面，调高更活泼",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(SectionCard("漫游速度", speedCard));

        // 移动停顿：1-30s（引擎下限 1s，对齐 pause.ts）。滑块语义 = 区间起点；
        // 保存时保留原有随机跨度（max-min），只平移区间——避免单滑块把随机范围压扁。
        var pauseSeconds = Math.Max(1, (int)Math.Round(roam.WanderPauseMinMs / 1000.0));
        var pauseSpanMs = Math.Max(500, roam.WanderPauseMaxMs - roam.WanderPauseMinMs);
        var pause = new Slider { Minimum = 1, Maximum = 30, Value = pauseSeconds, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var pauseValue = new TextBlock
        {
            Text = FormatPauseRange(roam.WanderPauseMinMs, roam.WanderPauseMaxMs),
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 80,
        };
        pause.ValueChanged += (_, e) => pauseValue.Text = FormatPauseRange(
            e.NewValue * 1000, e.NewValue * 1000 + pauseSpanMs);
        CommitSliderOnRelease(pause, () =>
        {
            var v = (int)pause.Value;
            var (min, max) = Pause.NormalizeWanderPauseRange(v * 1000.0, v * 1000.0 + pauseSpanMs);
            SaveRoam(_settings.Roam with { WanderPauseMinMs = min, WanderPauseMaxMs = max });
        });
        var pauseCard = new StackPanel();
        pauseCard.Children.Add(Row(pause, pauseValue));
        pauseCard.Children.Add(new TextBlock
        {
            Text = "走一段后休息多久（1-30 秒，区间内随机）；调高让宠物更安静、调低更活跃；「待着不动」模式不生效",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(SectionCard("移动停顿", pauseCard, margin: 0));

        stack.Children.Add(new TextBlock
        {
            Text = "此页为全局漫游设置，应用于所有宠物（导入宠物自带的独立漫游配置以全局为准）。",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        return PageScroller(stack, PageHeader("漫游", "宠物在桌面上的活动方式（应用于所有宠物）"));
    }

    // ---- 快捷键页 ----

    private UIElement BuildHotkeysPage()
    {
        var draft = _settings.Hotkeys;
        var editors = new Dictionary<HotkeyAction, TextBox>();
        var bindings = new StackPanel();
        var actions = new[]
        {
            (HotkeyAction.TogglePets, "显示或隐藏宠物"),
            (HotkeyAction.ToggleMode, "切换输出模式"),
            (HotkeyAction.OpenSettings, "打开设置"),
            (HotkeyAction.Quit, "退出应用"),
        };

        foreach (var (action, label) in actions)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Brush("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(title);

            var editor = new TextBox
            {
                Text = FormatHotkey(draft.Get(action)),
                IsReadOnly = true,
                Height = 30,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            WpfLocalizer.SetFormattedAutomationName(
                editor,
                "{0}快捷键",
                _i18n,
                WpfLocalizer.Localize(label));
            editor.PreviewKeyDown += (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (IsModifierKey(key))
                {
                    e.Handled = true;
                    return;
                }
                var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
                if (virtualKey == 0)
                {
                    e.Handled = true;
                    return;
                }
                var gesture = new HotkeyGesture(ReadHotkeyModifiers(), virtualKey);
                draft = SetHotkey(draft, action, gesture);
                editor.Text = FormatHotkey(gesture);
                editor.BorderBrush = Brush("StrokeBrush");
                e.Handled = true;
            };
            Grid.SetColumn(editor, 1);
            row.Children.Add(editor);
            editors[action] = editor;

            var capture = new Button
            {
                Content = "录入",
                Style = AppStyle("ButtonDefaultStyle"),
                Height = 30,
                MinWidth = 58,
                Margin = new Thickness(0, 0, 6, 0),
            };
            WpfLocalizer.SetFormattedToolTip(
                capture,
                "录入{0}快捷键",
                _i18n,
                WpfLocalizer.Localize(label));
            capture.Click += (_, _) =>
            {
                editor.Focus();
                Keyboard.Focus(editor);
            };
            Grid.SetColumn(capture, 2);
            row.Children.Add(capture);

            var clear = new Button
            {
                Content = "×",
                Style = AppStyle("ButtonGhostStyle"),
                Width = 30,
                Height = 30,
                FontSize = 16,
            };
            WpfLocalizer.SetFormattedToolTip(
                clear,
                "清除{0}快捷键",
                _i18n,
                WpfLocalizer.Localize(label));
            WpfLocalizer.SetFormattedAutomationName(
                clear,
                "清除{0}快捷键",
                _i18n,
                WpfLocalizer.Localize(label));
            clear.Click += (_, _) =>
            {
                draft = SetHotkey(draft, action, null);
                editor.Text = _i18n.T("未绑定");
                editor.BorderBrush = Brush("StrokeBrush");
            };
            Grid.SetColumn(clear, 3);
            row.Children.Add(clear);
            bindings.Children.Add(row);
        }

        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var apply = new Button
        {
            Content = "应用快捷键",
            Style = AppStyle("ButtonPrimaryStyle"),
            Width = 120,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        apply.Click += (_, _) =>
        {
            foreach (var editor in editors.Values) editor.BorderBrush = Brush("StrokeBrush");
            if (_applyHotkeys is null)
            {
                status.Text = _i18n.T("快捷键服务尚未初始化");
                status.Foreground = Brush("DangerBrush");
                status.Visibility = Visibility.Visible;
                return;
            }

            var result = _applyHotkeys(draft);
            status.Text = _i18n.T(result.Message);
            status.Foreground = result.Success ? Brush("SuccessBrush") : Brush("DangerBrush");
            status.Visibility = Visibility.Visible;
            foreach (var issue in result.RuntimeResult.ValidationIssues)
            {
                if (editors.TryGetValue(issue.Action, out var editor))
                    editor.BorderBrush = Brush("DangerBrush");
                if (issue.ConflictingAction is { } conflicting
                    && editors.TryGetValue(conflicting, out var conflictingEditor))
                    conflictingEditor.BorderBrush = Brush("DangerBrush");
            }
            if (result.Success && result.Settings is not null)
                _settings = result.Settings;
        };

        bindings.Children.Add(apply);
        bindings.Children.Add(status);
        var stack = new StackPanel();
        stack.Children.Add(SectionCard("全局快捷键", bindings, margin: 0));
        return PageScroller(stack, PageHeader("快捷键", "全局操作绑定"));
    }

    private static HotkeySettings SetHotkey(
        HotkeySettings settings,
        HotkeyAction action,
        HotkeyGesture? gesture)
        => action switch
        {
            HotkeyAction.TogglePets => settings with { TogglePets = gesture },
            HotkeyAction.ToggleMode => settings with { ToggleMode = gesture },
            HotkeyAction.OpenSettings => settings with { OpenSettings = gesture },
            HotkeyAction.Quit => settings with { Quit = gesture },
            _ => settings,
        };

    private static HotkeyModifiers ReadHotkeyModifiers()
    {
        var current = Keyboard.Modifiers;
        var modifiers = HotkeyModifiers.None;
        if ((current & ModifierKeys.Control) != 0) modifiers |= HotkeyModifiers.Control;
        if ((current & ModifierKeys.Alt) != 0) modifiers |= HotkeyModifiers.Alt;
        if ((current & ModifierKeys.Shift) != 0) modifiers |= HotkeyModifiers.Shift;
        if ((current & ModifierKeys.Windows) != 0) modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private string FormatHotkey(HotkeyGesture? gesture)
    {
        if (gesture is null) return _i18n.T("未绑定");
        var parts = new List<string>();
        if ((gesture.Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((gesture.Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((gesture.Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((gesture.Modifiers & HotkeyModifiers.Windows) != 0) parts.Add("Win");
        parts.Add(KeyInterop.KeyFromVirtualKey((int)gesture.VirtualKey).ToString());
        return string.Join(" + ", parts);
    }

    // ---- 语言页 ----

    private UIElement BuildLanguagePage()
    {
        var stack = new StackPanel();
        var lang = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (value, label) in new[]
        {
            (AppLang.En, "English"), (AppLang.ZhHans, "简体中文"),
            (AppLang.ZhHant, "繁體中文"), (AppLang.Vi, "Tiếng Việt"),
        })
        {
            var radio = new RadioButton { Content = label, IsChecked = _settings.Lang == value, Margin = new Thickness(0, 0, 20, 0) };
            radio.Checked += async (_, _) =>
            {
                if (_settings.Lang == value) return;
                if (_changeLanguage is null)
                {
                    Save(settings => settings with { Lang = value });
                    if (_settings.Lang == value)
                    {
                        _i18n.SetLang(value);
                        ApplyLocalization();
                    }
                    return;
                }
                var result = await _changeLanguage(value, CancellationToken.None);
                if (result.PersistenceError is not null)
                {
                    PersistenceErrorPresenter.Report(result.PersistenceError, this);
                    ShowPage("language");
                    return;
                }
                if (result.Settings is not null) _settings = result.Settings;
            };
            lang.Children.Add(radio);
        }
        stack.Children.Add(SectionCard("语言 / Language", lang, margin: 0));

        return PageScroller(stack, PageHeader("语言", "界面显示语言"));
    }

    // ---- 关于页 ----

    private string DescribeFactoryResetFailure(FactoryResetException error)
    {
        var stage = error.Stage switch
        {
            "stage-data" => _i18n.T("暂存应用数据"),
            "delete-credentials" => _i18n.T("删除 API 凭据"),
            "delete-data" => _i18n.T("删除应用数据"),
            "delete-residual-data" => _i18n.T("清理残留数据"),
            _ => error.Stage,
        };
        var recovery = error.RollbackComplete
            ? _i18n.T("原数据已保留")
            : error.Stage == "delete-credentials" && error.ResidualPath is null
                ? _i18n.T("应用数据已恢复，但部分 API 凭据可能已删除；请重新启动后重试")
                : _i18n.T("部分数据可能已暂存，请保留现场并重试");
        return _i18n.Format("恢复出厂失败：{0}；{1}", stage, recovery);
    }

    private TextBlock? _metricsText;

    private UIElement BuildAboutPage()
    {
        var stack = new StackPanel();
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        var care = _store.LoadCare();
        var totalXp = care.Values.Sum(s => s.Xp);

        var aboutStack = new StackPanel();
        aboutStack.Children.Add(new TextBlock
        {
            Text = "DesktopPet Native",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });
        var versionText = new TextBlock
        {
            FontSize = 12,
            LineHeight = 20,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        WpfLocalizer.SetFormattedText(
            versionText,
            "版本 {0}\n.NET 8 + WPF · Lumen 2.0",
            _i18n,
            typeof(SettingsWindow).Assembly.GetName().Version);
        aboutStack.Children.Add(versionText);
        stack.Children.Add(Card(aboutStack));

        // 开机自启（HKCU Run 键；对齐 macOS LoginItem）
        var autoStart = new CheckBox
        {
            Content = "开机自启",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = Infra.Startup.AutoStart.IsEnabled(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        autoStart.Click += (_, _) =>
        {
            try
            {
                Infra.Startup.AutoStart.SetEnabled(autoStart.IsChecked == true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                autoStart.IsChecked = !(autoStart.IsChecked == true); // 回滚开关
                MessageBox.Show(this, _i18n.Format("开机自启设置失败：{0}", ex.Message), "DesktopPet");
            }
        };
        var autoStartCard = new StackPanel();
        autoStartCard.Children.Add(autoStart);
        autoStartCard.Children.Add(new TextBlock
        {
            Text = "登录 Windows 后自动启动桌面宠物（当前用户）",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        stack.Children.Add(Card(autoStartCard));

        var statsStack = new StackPanel();
        statsStack.Children.Add(new TextBlock
        {
            Text = "桌面宠物数据",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        statsStack.Children.Add(StatRow("宠物数量", store.Instances.Count.ToString()));
        statsStack.Children.Add(StatRow("总养成 XP", $"{totalXp:0}"));
        statsStack.Children.Add(StatRow("数据目录", AppDataPaths.ForCurrentUser().Root));
        stack.Children.Add(Card(statsStack, margin: 0));

        var diagnosticsStack = new StackPanel();
        diagnosticsStack.Children.Add(new TextBlock
        {
            Text = "运行诊断",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _metricsText = new TextBlock
        {
            Text = _i18n.T("正在采样..."),
            FontSize = 12,
            LineHeight = 20,
            Foreground = Brush("TextSecondaryBrush"),
        };
        diagnosticsStack.Children.Add(_metricsText);
        if (_diagnosticExporter is not null)
        {
            var exportStatus = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush("TextSecondaryBrush"),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            var export = new Button
            {
                Content = "导出诊断日志",
                Style = AppStyle("ButtonDefaultStyle"),
            };
            export.Margin = new Thickness(0, 12, 0, 0);
            export.Click += async (_, _) =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = _i18n.T("导出诊断日志"),
                    Filter = _i18n.T("ZIP 文件 (*.zip)|*.zip"),
                    FileName = $"DesktopPet-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                    DefaultExt = ".zip",
                    AddExtension = true,
                };
                if (dialog.ShowDialog(this) != true) return;
                export.IsEnabled = false;
                exportStatus.Text = _i18n.T("正在导出诊断日志...");
                try
                {
                    await Task.Run(() => _diagnosticExporter.Export(dialog.FileName));
                    exportStatus.Text = _i18n.T("诊断日志已导出");
                    exportStatus.Foreground = Brush("SuccessBrush");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    exportStatus.Text = _i18n.Format("导出失败：{0}", ex.Message);
                    exportStatus.Foreground = Brush("DangerBrush");
                }
                finally
                {
                    if (IsLoaded) export.IsEnabled = true;
                }
            };
            diagnosticsStack.Children.Add(export);
            diagnosticsStack.Children.Add(exportStatus);
        }
        if (_factoryReset is not null)
        {
            var resetStatus = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush("TextSecondaryBrush"),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            var reset = new Button
            {
                Content = "恢复出厂设置",
                Style = AppStyle("ButtonDangerStyle"),
                Margin = new Thickness(0, 16, 0, 0),
            };
            reset.Click += async (_, _) =>
            {
                var answer = MessageBox.Show(
                    this,
                    _i18n.T("恢复出厂设置将删除设置、宠物、日记、日志和 API 凭据，且无法撤销。确定继续吗？"),
                    _i18n.T("确认恢复出厂设置"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;

                reset.IsEnabled = false;
                resetStatus.Text = _i18n.T("正在停止服务并清理数据...");
                try
                {
                    await _factoryReset(CancellationToken.None);
                    resetStatus.Text = _i18n.T("恢复出厂设置已完成，应用正在重启");
                }
                catch (FactoryResetException ex)
                {
                    resetStatus.Text = DescribeFactoryResetFailure(ex);
                    resetStatus.Foreground = Brush("DangerBrush");
                    reset.IsEnabled = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    resetStatus.Text = _i18n.Format("恢复出厂失败：{0}", ex.Message);
                    resetStatus.Foreground = Brush("DangerBrush");
                    reset.IsEnabled = true;
                }
            };
            diagnosticsStack.Children.Add(reset);
            diagnosticsStack.Children.Add(resetStatus);
        }
        stack.Children.Add(Card(diagnosticsStack));
        StartDiagnostics();

        return PageScroller(stack, PageHeader("关于", "版本、统计与诊断"));
    }

    private void StartDiagnostics()
    {
        IReadOnlyList<ProcessSnapshot> Capture()
        {
            var snapshots = new List<ProcessSnapshot>();
            var app = ProcessMetricsMonitor.CaptureProcess("PetApp", Environment.ProcessId);
            if (app is not null) snapshots.Add(app);
            var agentId = _agentProcessId();
            if (agentId is { } pid && pid != Environment.ProcessId)
            {
                var agent = ProcessMetricsMonitor.CaptureProcess("PetAgent", pid);
                if (agent is not null) snapshots.Add(agent);
            }
            return snapshots;
        }

        _metricsMonitor = new ProcessMetricsMonitor(Capture);
        _metricsMonitor.Start();
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _metricsTimer.Tick += MetricsTimerOnTick;
        _metricsTimer.Start();
    }

    private void MetricsTimerOnTick(object? sender, EventArgs e)
    {
        if (_metricsMonitor is null || _metricsText is null) return;
        var metrics = _metricsMonitor.Sample();
        var rows = metrics.Select(metric => _i18n.Format(
            "{0}：CPU {1}% · 内存 {2} MB",
            metric.Name,
            metric.CpuPercent.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture),
            (metric.WorkingSetBytes / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)))
            .ToList();
        if (metrics.All(metric => metric.Name != "PetAgent"))
            rows.Add(_i18n.T("PetAgent：未运行"));
        _metricsText.Text = string.Join(Environment.NewLine, rows);
    }

    private void StopDiagnostics()
    {
        if (_metricsTimer is not null)
        {
            _metricsTimer.Stop();
            _metricsTimer.Tick -= MetricsTimerOnTick;
            _metricsTimer = null;
        }
        _metricsMonitor?.Dispose();
        _metricsMonitor = null;
        _metricsText = null;
    }

    private static StackPanel StatRow(string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            Width = 72,
        });
        row.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = Brush("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    // ---- 帮助 ----

    private static StackPanel Stacked(string label, UIElement content)
    {
        var stack = new StackPanel();
        stack.Children.Add(FormLabel(label));
        stack.Children.Add(content);
        return stack;
    }

    private static StackPanel Row(params UIElement[] elements)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var element in elements) panel.Children.Add(element);
        return panel;
    }

    // ---- AI 助手页（Phase 5：总开关 / 分析 / 输出模式 / 人格卡片 / 模型连接 / 屏幕上下文）----

    private UIElement BuildAiPage()
    {
        var stack = new StackPanel();
        var ai = _settings.Ai;
        var personas = _ai?.Personas ?? new Core.Personas.PersonasFileModel();

        // AI 总开关
        var masterToggle = new CheckBox
        {
            Content = "启用 AI",
            Style = AppStyle("ToggleSwitchStyle"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            IsChecked = ai.Enabled,
        };
        masterToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { Enabled = masterToggle.IsChecked == true } });
        var masterCard = new StackPanel();
        masterCard.Children.Add(masterToggle);
        masterCard.Children.Add(new TextBlock
        {
            Text = "开启后启动后台分析进程；关闭 = 纯桌宠模式：无截屏、无网络调用、无后台进程",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        stack.Children.Add(Card(masterCard));

        // 模型连接（AI 第一配置项：小白用户打开 AI 页即见，不用滚动）
        var providers = _ai?.Providers ?? new Core.Scheduling.ProvidersFileModel();
        var providerPanel = new StackPanel();
        if (providers.Models.Count == 0)
        {
            providerPanel.Children.Add(new TextBlock
            {
                Text = "未配置模型连接。对话不可用；屏幕分析仅做变化检测（无评论）。",
                FontSize = 12,
                Foreground = Brush("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            var emptyEditButton = new Button
            {
                Content = "配置模型连接",
                Style = AppStyle("ButtonPrimaryStyle"),
                Width = 140,
                Height = 30,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            emptyEditButton.Click += (_, _) => ShowModelConnectionEditor();
            providerPanel.Children.Add(emptyEditButton);
        }
        else
        {
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 6), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
            var selectedProvider = providers.Models.FirstOrDefault(p => p.Id == ai.ProviderId) ?? providers.Models[0];
            foreach (var p in providers.Models)
            {
                combo.Items.Add(p.Name + "（" + p.ModelName + "）");
            }
            combo.SelectedIndex = providers.Models.IndexOf(selectedProvider);
            combo.SelectionChanged += (_, _) =>
            {
                var picked = providers.Models[Math.Max(0, combo.SelectedIndex)];
                Save(s => s with { Ai = s.Ai with { ProviderId = picked.Id } });
            };
            var comboRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            comboRow.Children.Add(combo);
            var editConnButton = new Button
            {
                Content = "编辑",
                Style = AppStyle("ButtonDefaultStyle"),
                Height = 28,
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(12, 3, 12, 3),
            };
            editConnButton.Click += (_, _) => ShowModelConnectionEditor();
            comboRow.Children.Add(editConnButton);
            providerPanel.Children.Add(comboRow);
            var providerUrl = new TextBlock
            {
                FontSize = 11,
                Foreground = Brush("TextTertiaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            };
            WpfLocalizer.SetDynamicText(providerUrl, selectedProvider.BaseUrl);
            providerPanel.Children.Add(providerUrl);
        }

        // Phase 6f：生图连接（总结图）+ 日记查看入口
        var extraRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var imageConnButton = new Button
        {
            Content = "生图连接" + (providers.Image is null ? "（未配置）" : "（已配置）"),
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 28,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 3, 12, 3),
        };
        imageConnButton.Click += (_, _) => ShowImageConnectionEditor();
        extraRow.Children.Add(imageConnButton);
        var diaryButton = new Button
        {
            Content = "查看日记",
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 28,
            FontSize = 12,
            Padding = new Thickness(12, 3, 12, 3),
        };
        diaryButton.Click += (_, _) => ShowDiaryViewer();
        extraRow.Children.Add(diaryButton);
        providerPanel.Children.Add(extraRow);
        stack.Children.Add(SectionCard("模型连接（OpenAI 兼容：云端 / 本地 Ollama 通吃）", providerPanel));

        // 分析开关
        var analysisToggle = new CheckBox
        {
            Content = "屏幕分析（感知你在做什么）",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = ai.ScreenAnalysis,
        };
        analysisToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { ScreenAnalysis = analysisToggle.IsChecked == true } });
        stack.Children.Add(Card(analysisToggle));

        // 截屏分析频率（隐私/云端费用敏感：间隔越短分析越频繁，费用越高）
        var analysisIntervalPanel = new StackPanel();
        foreach (var (seconds, label) in new[]
        {
            (3, "3 秒（最灵敏）"),
            (5, "5 秒（推荐）"),
            (10, "10 秒"),
            (15, "15 秒"),
            (30, "30 秒（最省）"),
        })
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = "analysis-interval",
                IsChecked = ai.ScreenAnalysisIntervalSeconds == seconds,
                Margin = new Thickness(0, 5, 0, 0),
            };
            radio.Click += (_, _) =>
                Save(s => s with { Ai = s.Ai with { ScreenAnalysisIntervalSeconds = seconds } });
            analysisIntervalPanel.Children.Add(radio);
        }
        stack.Children.Add(SectionCard("屏幕分析频率", new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "多久分析一次屏幕内容；间隔越短越灵敏，云端费用与隐私风险越高。仅「屏幕分析」开启时生效。",
                    FontSize = 11,
                    Foreground = Brush("TextTertiaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                },
                analysisIntervalPanel,
            },
        }));

        // 输出模式
        var modePanel = new StackPanel();
        foreach (var (id, name, desc) in new[]
        {
            ("bubble", "气泡", "宠物头上气泡文字（默认，不打断工作）"),
            ("danmaku", "弹幕", "全屏滚动弹幕（Win2D GPU）"),
            ("chat", "对话", "回复出现在对话气泡窗口"),
            ("silent", "仅聊天", "不主动说话，只在你找它聊天时回应"),
        })
        {
            var radio = new RadioButton
            {
                Content = name + " — " + desc,
                GroupName = "output-mode",
                IsChecked = ai.OutputMode == id,
                Margin = new Thickness(0, 5, 0, 0),
            };
            radio.Click += (_, _) => Save(s => s with { Ai = s.Ai with { OutputMode = id } });
            modePanel.Children.Add(radio);
        }
        stack.Children.Add(SectionCard("AI 主动输出模式", modePanel));

        // 屏幕上下文开关（对话携带最近屏幕事件，隐私默认关）
        var contextToggle = new CheckBox
        {
            Content = "对话携带屏幕上下文（默认关：开启后对话请求才包含屏幕描述）",
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = ai.ScreenContextEnabled,
        };
        contextToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { ScreenContextEnabled = contextToggle.IsChecked == true } });
        stack.Children.Add(Card(contextToggle));

        // ---- Phase 6：陪伴功能开关组（AI 总开关 → 各功能独立开关）----
        var companionPanel = new StackPanel();
        companionPanel.Children.Add(ToggleRow("记忆", "记住你的称呼/作息/话题；关 = 不记录不注入", ai.MemoryEnabled,
            v => Save(s => s with { Ai = s.Ai with { MemoryEnabled = v } }), margin: 8));
        companionPanel.Children.Add(ToggleRow("主动互动", "定时问候 + 屏幕事件评论", ai.ActiveInteraction,
            v => Save(s => s with { Ai = s.Ai with { ActiveInteraction = v } }), margin: 8));
        companionPanel.Children.Add(ToggleRow("全员回应", "多宠物时同一事件每只宠物都生成回应；关 = 仅当前宠物", ai.AllReply,
            v => Save(s => s with { Ai = s.Ai with { AllReply = v } }), margin: 8));
        companionPanel.Children.Add(new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new TextBlock
                {
                    Text = "主动互动频率",
                    FontSize = 12,
                    Foreground = Brush("TextPrimaryBrush"),
                },
                new TextBlock
                {
                    Text = "多久主动找你搭话一次；调低更安静，调高更话痨",
                    FontSize = 11,
                    Foreground = Brush("TextTertiaryBrush"),
                    Margin = new Thickness(0, 2, 0, 4),
                },
            },
        });
        foreach (var (value, label) in new[]
        {
            (Core.Storage.AiSettings.FrequencyLow, "低（偶尔问候）"),
            (Core.Storage.AiSettings.FrequencyMedium, "中（推荐）"),
            (Core.Storage.AiSettings.FrequencyHigh, "高（常来找你）"),
        })
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = "interaction-frequency",
                IsChecked = ai.InteractionFrequency == value,
                Margin = new Thickness(0, 4, 0, 0),
            };
            radio.Click += (_, _) =>
                Save(s => s with { Ai = s.Ai with { InteractionFrequency = value } });
            companionPanel.Children.Add(radio);
        }
        companionPanel.Children.Add(ToggleRow("屏幕感知", "从截屏推断你在做什么；关 = 仅定时问候", ai.ScreenAwareness,
            v => Save(s => s with { Ai = s.Ai with { ScreenAwareness = v } }), margin: 8));
        companionPanel.Children.Add(ToggleRow("亲密度", "随互动成长，称呼/语气分档；关 = 固定人格基础档", ai.IntimacyEnabled,
            v => Save(s => s with { Ai = s.Ai with { IntimacyEnabled = v } }), margin: 8));
        companionPanel.Children.Add(ToggleRow("每日总结", "每天结束生成\"你的一天\"日记", ai.DailySummary,
            v => Save(s => s with { Ai = s.Ai with { DailySummary = v } }), margin: 8));
        companionPanel.Children.Add(ToggleRow("总结图", "默认关：用生图模型给日记配插图，需配置生图连接", ai.SummaryImage,
            v => Save(s => s with { Ai = s.Ai with { SummaryImage = v } }), margin: 8));
        companionPanel.Children.Add(ToggleRow("语音朗读", "对话模式朗读回复；弹幕模式不朗读", ai.TtsEnabled,
            v => Save(s => s with { Ai = s.Ai with { TtsEnabled = v } }), margin: 0));

        // 免打扰时段：时段内不产生任何主动互动（定时问候 + 事件评论）；默认关 = 保持现有行为
        var quietPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        quietPanel.Children.Add(ToggleRow("免打扰时段", "开启后在设定时段内不主动打扰（定时问候和事件评论都暂停）", ai.QuietHoursEnabled,
            v => Save(s => s with { Ai = s.Ai with { QuietHoursEnabled = v } }), margin: 8));
        var hourOptions = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToArray();
        var quietRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        quietRow.Children.Add(new TextBlock
        {
            Text = "从",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var quietStart = new ComboBox
        {
            Width = 88,
            Height = 28,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        foreach (var h in hourOptions) quietStart.Items.Add(h);
        quietStart.SelectedIndex = Math.Clamp(ai.QuietHoursStart, 0, 23);
        quietStart.SelectionChanged += (_, _) =>
            Save(s => s with { Ai = s.Ai with { QuietHoursStart = Math.Max(0, quietStart.SelectedIndex) } });
        quietRow.Children.Add(quietStart);
        quietRow.Children.Add(new TextBlock
        {
            Text = "到",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var quietEnd = new ComboBox
        {
            Width = 88,
            Height = 28,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var h in hourOptions) quietEnd.Items.Add(h);
        quietEnd.SelectedIndex = Math.Clamp(ai.QuietHoursEnd, 0, 23);
        quietEnd.SelectionChanged += (_, _) =>
            Save(s => s with { Ai = s.Ai with { QuietHoursEnd = Math.Max(0, quietEnd.SelectedIndex) } });
        quietRow.Children.Add(quietEnd);
        quietPanel.Children.Add(quietRow);
        quietPanel.Children.Add(new TextBlock
        {
            Text = "默认 23:00-05:00（睡眠时段）；结束小时早于开始小时 = 跨午夜",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        companionPanel.Children.Add(quietPanel);

        // 朗读引擎 + 音色 + 试听 + 语速（windows-tts-design.md §7）：
        // 引擎单选（当前可用 provider 列表，在线引擎在 AI 关闭时置灰）；
        // 音色下拉按引擎异步枚举（ListVoicesAsync）；试听合成固定文案；语速 50-200%。
        var ttsPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        ttsPanel.Children.Add(new TextBlock
        {
            Text = "朗读引擎",
            FontSize = 12,
            Foreground = Brush("TextPrimaryBrush"),
        });
        var ttsProviders = _ai?.TtsProviders ?? [];
        var engineNames = new Dictionary<string, string>
        {
            ["sapi"] = "系统语音（离线）",
            ["onecore"] = "自然语音（离线）",
            ["openai"] = "自配端点（在线）",
        };
        var engineRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var selectedProviderId = ai.TtsProviderId;
        // 保存的引擎不在当前 provider 列表（如未配置在线端点）→ 回落默认 sapi
        if (ttsProviders.All(p => p.Id != selectedProviderId)) selectedProviderId = "sapi";
        ITtsProvider? activeProvider = null;
        var engineRadios = new List<(RadioButton Radio, ITtsProvider Provider)>();
        foreach (var provider in ttsProviders)
        {
            var label = engineNames.TryGetValue(provider.Id, out var n) ? n : provider.Id;
            var radio = new RadioButton
            {
                Content = label,
                IsChecked = provider.Id == selectedProviderId,
                Margin = new Thickness(0, 0, 20, 0),
                FontSize = 12,
                IsEnabled = !provider.RequiresNetwork || ai.Enabled, // 在线引擎需 AI 总开关
            };
            engineRadios.Add((radio, provider));
            engineRow.Children.Add(radio);
            if (provider.Id == selectedProviderId) activeProvider = provider;
        }
        ttsPanel.Children.Add(engineRow);
        ttsPanel.Children.Add(new TextBlock
        {
            Text = "自然语音需在系统设置安装（设置 → 时间和语言 → 语音）；自配端点支持 OpenAI 兼容 TTS",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        // 音色下拉（按当前引擎异步枚举）+ 试听
        var voiceRow = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        voiceRow.Children.Add(new TextBlock
        {
            Text = "朗读声音",
            FontSize = 12,
            Foreground = Brush("TextPrimaryBrush"),
        });
        var voiceCombo = new ComboBox
        {
            Width = 320,
            Height = 30,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var previewButton = new Button
        {
            Content = "试听",
            Width = 64,
            Height = 30,
            FontSize = 12,
            Margin = new Thickness(8, 4, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = activeProvider is not null,
        };
        var voiceWrap = new WrapPanel();
        voiceWrap.Children.Add(voiceCombo);
        voiceWrap.Children.Add(previewButton);
        voiceRow.Children.Add(voiceWrap);

        // 引擎切换 → 重载音色列表；当前选择失效自动回落「自动」
        // ComboBoxItem.Tag 存引擎内音色 Id（"" = 自动），Items 显示名
        var voiceLoadGeneration = 0; // 代际令牌：快速切换引擎时丢弃过期异步结果
        async Task LoadVoicesAsync(ITtsProvider provider)
        {
            var generation = ++voiceLoadGeneration;
            var items = new List<(string Id, string Label)> { ("", "自动（跟随界面语言）") };
            try
            {
                var voices = await provider.ListVoicesAsync(CancellationToken.None);
                foreach (var v in voices)
                {
                    var gender = v.Gender switch { "female" => "女", "male" => "男", _ => "" };
                    var suffix = v.Language.Length > 0 ? $"（{v.Language}{(gender.Length > 0 ? " · " + gender : "")}）" : "";
                    items.Add((v.Id, v.DisplayName + suffix));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ProviderUnavailableException or InvalidOperationException)
            {
                // 引擎不可用/未配置：只保留「自动」项，试听会走运行时降级
                _logger.Error("Settings", $"voice enumeration failed ({provider.Id}): {ex.GetType().Name}: {ex.Message}");
            }
            if (generation != voiceLoadGeneration) return; // 过期结果：已有更新的引擎切换
            var prev = ai.TtsVoiceName;
            voiceCombo.SelectionChanged -= OnVoiceComboChanged; // 程序化填充不触发保存
            voiceCombo.Items.Clear();
            foreach (var (id, label) in items)
            {
                voiceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            }
            // 旧音色不在新引擎列表 → 回落「自动」
            var savedIndex = items.FindIndex(v => v.Id == prev);
            voiceCombo.SelectedIndex = Math.Max(0, savedIndex);
            voiceCombo.SelectionChanged += OnVoiceComboChanged;
        }

        void OnVoiceComboChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (voiceCombo.SelectedItem is ComboBoxItem item && item.Tag is string voiceId)
            {
                Save(s => s with { Ai = s.Ai with { TtsVoiceName = voiceId } });
            }
        }

        foreach (var (radio, provider) in engineRadios)
        {
            radio.Checked += async (_, _) =>
            {
                activeProvider = provider;
                previewButton.IsEnabled = true;
                Save(s => s with { Ai = s.Ai with { TtsProviderId = provider.Id } });
                await LoadVoicesAsync(provider);
            };
        }
        voiceCombo.SelectionChanged += OnVoiceComboChanged;

        // 试听：合成固定文案 → 临时文件 → MediaPlayer 播放
        var previewPlayer = new System.Windows.Media.MediaPlayer();
        string? pendingPreviewPath = null;
        previewPlayer.MediaEnded += (_, _) =>
        {
            if (pendingPreviewPath is not null)
            {
                try { File.Delete(pendingPreviewPath); } catch { /* 临时文件清理失败可忽略 */ }
                pendingPreviewPath = null;
            }
        };
        previewButton.Click += async (_, _) =>
        {
            if (activeProvider is null) return;
            previewButton.IsEnabled = false;
            try
            {
                var voice = TtsProviderRegistry.ResolveVoice(
                    await activeProvider.ListVoicesAsync(CancellationToken.None),
                    ai.TtsVoiceName, _i18n.Lang.ToString());
                using var stream = await activeProvider.SynthesizeAsync(
                    new TtsSynthesisRequest("嗨，我是你的桌面宠物~", voice?.Id ?? "", ai.TtsSpeedPercent),
                    CancellationToken.None);
                var bytes = ((MemoryStream)stream).ToArray();
                pendingPreviewPath = Path.Combine(Path.GetTempPath(), $"desktoppet-tts-preview-{Guid.NewGuid():N}.wav");
                File.WriteAllBytes(pendingPreviewPath, bytes);
                previewPlayer.Open(new Uri(pendingPreviewPath));
                previewPlayer.Play();
            }
            catch (Exception ex)
            {
                _logger.Error("Settings", $"TTS preview failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                previewButton.IsEnabled = true;
            }
        };

        // 语速滑条 50-200%
        var speedRow = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        speedRow.Children.Add(new TextBlock
        {
            Text = "语速",
            FontSize = 12,
            Foreground = Brush("TextPrimaryBrush"),
        });
        var speedSlider = new Slider
        {
            Minimum = 50,
            Maximum = 200,
            Value = ai.TtsSpeedPercent,
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var speedValue = new TextBlock
        {
            Text = $"{ai.TtsSpeedPercent}%",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            Width = 48,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        speedSlider.ValueChanged += (_, e) => speedValue.Text = $"{e.NewValue:0}%";
        var speedWrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        speedWrap.Children.Add(speedSlider);
        speedWrap.Children.Add(speedValue);
        speedRow.Children.Add(speedWrap);
        CommitSliderOnRelease(speedSlider, () =>
            Save(s => s with { Ai = s.Ai with { TtsSpeedPercent = (int)speedSlider.Value } }));

        ttsPanel.Children.Add(voiceRow);
        ttsPanel.Children.Add(speedRow);

        // 在线端点连接（providers.json tts 段）：仅 TTS 相关，独立于模型/生图连接
        var ttsConnButton = new Button
        {
            Content = "自配端点连接" + (providers.Tts is not null ? "（已配置）" : "（未配置）"),
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 28,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 3, 12, 3),
        };
        ttsConnButton.Click += (_, _) => ShowTtsConnectionEditor();
        ttsPanel.Children.Add(ttsConnButton);
        ttsPanel.Children.Add(new TextBlock
        {
            Text = "自配端点需 OpenAI 兼容 TTS（如 SiliconFlow / Fish Audio / 本地 GPT-SoVITS）",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        companionPanel.Children.Add(ttsPanel);
        // 初始加载当前引擎音色列表
        if (activeProvider is not null) _ = LoadVoicesAsync(activeProvider);
        stack.Children.Add(SectionCard("陪伴功能（记忆 / 主动互动 / 亲密度 / 每日总结 / 语音）", companionPanel));

        // 人格卡片网格
        var personaPanel = new StackPanel();
        var grid = new WrapPanel();
        foreach (var persona in personas.MergeWithBuiltins())
        {
            var selected = persona.Id == personas.SelectedId;
            var card = new Border
            {
                Background = selected ? Brush("AccentSoftBrush") : Brush("CardBgBrush"),
                BorderBrush = selected ? Brush("AccentBrush") : Brush("StrokeBrush"),
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 8, 8),
                Width = 168,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var inner = new StackPanel();
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            var personaName = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = selected ? Brush("AccentBrush") : Brush("TextPrimaryBrush"),
            };
            if (persona.Builtin)
            {
                WpfLocalizer.SetText(personaName, persona.Name, _i18n);
            }
            else
            {
                WpfLocalizer.SetFormattedText(
                    personaName,
                    "{0} · 自定义",
                    _i18n,
                    persona.Name);
            }
            nameRow.Children.Add(personaName);
            if (selected)
            {
                nameRow.Children.Add(new TextBlock
                {
                    Text = " 使用中",
                    FontSize = 10,
                    Foreground = Brush("AccentBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                });
            }
            inner.Children.Add(nameRow);
            var personaDescription = new TextBlock
            {
                FontSize = 10.5,
                Foreground = Brush("TextTertiaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            if (persona.Builtin)
                WpfLocalizer.SetText(personaDescription, persona.Description, _i18n);
            else
                WpfLocalizer.SetDynamicText(personaDescription, persona.Description);
            inner.Children.Add(personaDescription);
            if (!persona.Builtin)
            {
                var editLink = new TextBlock
                {
                    Text = "编辑",
                    FontSize = 10.5,
                    Foreground = Brush("AccentBrush"),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                editLink.MouseLeftButtonUp += (_, _) => ShowPersonaEditor(persona);
                inner.Children.Add(editLink);
            }
            card.Child = inner;
            var personaId = persona.Id;
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (_ai is null) return;
                var file = _ai.Personas.Select(personaId);
                _ai.ApplyPersonas(file); // 立即生效 + 持久化
                ShowPage("ai");          // 重建页面：新选中态高亮，卡片不消失
            };
            grid.Children.Add(card);
        }
        personaPanel.Children.Add(grid);

        // Phase 6e：新建/编辑人格（含示例对话输入，C.AI"示例 > 描述"经验）
        var manageRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var newPersonaButton = new Button
        {
            Content = "新建人格",
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 28,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 3, 12, 3),
        };
        newPersonaButton.Click += (_, _) => ShowPersonaEditor(null);
        manageRow.Children.Add(newPersonaButton);
        var editHint = new TextBlock
        {
            Text = "可编辑自定义人格；编辑内置人格会先复制为自定义",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        manageRow.Children.Add(editHint);
        personaPanel.Children.Add(manageRow);

        stack.Children.Add(SectionCard("人格（影响所有 AI 输出）", personaPanel, margin: 0));

        return PageScroller(stack, PageHeader("AI 助手", "模型连接、人格与陪伴功能"));
    }

    /// <summary>开关行：标题 + 说明 + CheckBox。</summary>
    private static Border ToggleRow(string title, string desc, bool isChecked,
        Action<bool> onChanged, double margin = 8)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
        });
        textStack.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(textStack, 0);
        row.Children.Add(textStack);
        var toggle = new CheckBox
        {
            Style = AppStyle("ToggleSwitchStyle"),
            IsChecked = isChecked,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, title); // UI 自动化/无障碍可定位
        toggle.Click += (_, _) => onChanged(toggle.IsChecked == true);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return new Border
        {
            Child = row,
            Padding = new Thickness(0, 4, 0, margin),
            BorderBrush = Brush("DividerBrush"),
            BorderThickness = margin > 0 ? new Thickness(0, 0, 0, 1) : new Thickness(0),
        };
    }

    /// <summary>滑块提交节流：拖动中 ValueChanged 只更新数值显示，松开/点击轨道/键盘调整时才提交保存
    /// （避免拖动过程高频写盘 + ApplySettings + RebuildChatPipeline）。
    /// Thumb.DragStarted/DragCompleted 为 bubbling 路由事件（官方文档），Slider 内部 Thumb 触发后冒泡到 Slider。</summary>
    private static void CommitSliderOnRelease(Slider slider, Action commit)
    {
        var dragging = false;
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            dragging = false;
            commit();
        }));
        // 键盘调整（方向键/PageUp）不产生 DragCompleted → 焦点在滑块上时也提交
        slider.ValueChanged += (_, _) =>
        {
            if (!dragging && slider.IsKeyboardFocusWithin) commit();
        };
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        var before = _settings;
        var next = AppSettings.Normalize(change(before));
        // 首次开启 AI → 引导窗（称呼+人格）；完成标记随本 Save 一并落盘
        var onboardingRequired = false;
        var onboardingCompleted = false;
        if (next.Ai.Enabled && !before.Ai.Enabled && !next.Ai.Onboarded)
        {
            var ai = _ai;
            if (ai is not null)
            {
                onboardingRequired = true;
                var personas = ai.Personas;
                var profile = _store.LoadMemoryProfile()
                    ?? new DesktopPet.Core.Memory.UserProfile("", [], "", "");
                var welcome = new Windows.WelcomeWindow(
                    builtinPersonas: Core.Personas.BuiltinPersonas.GetAll(),
                    initialCallName: profile.CallName,
                    selectedPersonaId: personas.SelectedId,
                    onComplete: (callName, personaId) =>
                    {
                        try
                        {
                            ai.CompleteOnboarding(
                                callName,
                                personaId,
                                next with { Ai = next.Ai with { Onboarded = true } });
                            onboardingCompleted = true;
                            return true;
                        }
                        catch (JsonStoreException ex)
                        {
                            PersistenceErrorPresenter.Report(ex, this);
                            return false;
                        }
                    },
                    i18n: _i18n);
                welcome.Owner = this;
                welcome.ShowDialog();
            }
        }
        if (onboardingRequired && !onboardingCompleted)
            return;
        if (onboardingCompleted)
            next = next with { Ai = next.Ai with { Onboarded = true } };

        try
        {
            _store.SaveSettings(next);
        }
        catch (JsonStoreException ex)
        {
            PersistenceErrorPresenter.Report(ex, this);
            _manager.RefreshSettingsWindow();
            return;
        }

        _settings = next;
        _manager.ApplySettings(next);
        _ai?.ApplySettings(next); // AI 设置同步（总开关启停 Agent / 配置下发）
    }

    // ---- Phase 6e：人格编辑/新建（含示例对话输入）----

    private void ShowPersonaEditor(Core.Personas.Persona? existing)
    {
        if (_ai is null) return;
        var isBuiltinEdit = existing?.Builtin == true;
        var copyId = isBuiltinEdit ? "custom-" + Guid.NewGuid().ToString("N")[..8] : existing!.Id;

        var nameBox = new TextBox { Text = existing?.Name ?? "", FontSize = 12, Height = 30 };
        var descBox = new TextBox { Text = existing?.Description ?? "", FontSize = 12, Height = 30 };
        var promptBox = new TextBox
        {
            Text = existing?.Prompt ?? "",
            FontSize = 12,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            Height = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var exampleBox = new TextBox
        {
            Text = existing?.ExampleDialogs is { Length: > 0 } ? string.Join("\n", existing.ExampleDialogs) : "",
            FontSize = 12,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 0) };
        form.Children.Add(FormLabel("人格名称（必填，≤12 字）"));
        form.Children.Add(nameBox);
        form.Children.Add(FormLabel("一句话描述（选填）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(descBox);
        form.Children.Add(FormLabel("人格提示词（必填，决定性格）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(promptBox);
        form.Children.Add(FormLabel("示例对话（选填，每行一段：\"用户：…\" 和 \"宠物：…\" 成对写，风格示例 > 描述）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(exampleBox);

        var window = new Window
        {
            Title = isBuiltinEdit ? "复制内置人格并编辑" : (existing is null ? "新建人格" : "编辑人格"),
            Width = 440,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = Brush("WindowBgBrush"),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var saveButton = new Button
        {
            Content = "保存",
            Style = AppStyle("ButtonPrimaryStyle"),
            Width = 110,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        saveButton.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var prompt = promptBox.Text.Trim();
            if (name.Length == 0 || prompt.Length == 0)
            {
                MessageBox.Show(window, _i18n.T("名称和提示词不能为空"), "DesktopPet");
                return;
            }
            if (name.Length > 12) name = name[..12];
            var examples = exampleBox.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 0)
                .Take(10)
                .ToArray();
            var persona = new Core.Personas.Persona(
                copyId, name, descBox.Text.Trim(), prompt, Builtin: false,
                ExampleDialogs: examples.Length > 0 ? examples : null);
            var file = _ai.Personas;
            var list = file.CustomPersonas.Where(p => p.Id != copyId).ToList();
            list.Add(persona);
            file.CustomPersonas = list;
            file.SelectedId = copyId; // 保存后即选中，立即生效
            _ai.ApplyPersonas(file);
            window.Close();
            ShowPage("ai"); // 重建页面：新卡片出现 + 选中态更新
        };
        var footer = new Grid { Margin = new Thickness(20, 4, 20, 16) };
        footer.Children.Add(saveButton);
        var root = new DockPanel();
        DockPanel.SetDock(form, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(form);
        window.Content = root;
        WpfLocalizer.ApplyNew(window, _i18n);
        window.ShowDialog();
    }

    // ---- Phase 6 收尾：模型连接编辑（小白用户：全在设置里填，不手改 providers.json）----

    private void ShowModelConnectionEditor()
    {
        try
        {
            ShowModelConnectionEditorCore();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Model connection editor failed: {ex.GetType().Name}");
            MessageBox.Show(this, _i18n.T("模型连接编辑器打开失败"), "DesktopPet");
        }
    }

    private void ShowModelConnectionEditorCore()
    {
        if (_ai is null) return;
        var providers = _ai.Providers;
        var cfg = providers.Models.FirstOrDefault(m => m.Id == _settings.Ai.ProviderId)
                  ?? providers.Models.FirstOrDefault();
        var creds = new Infra.Providers.WindowsCredentialStore();

        var baseBox = new TextBox { Text = cfg?.BaseUrl ?? "", FontSize = 12, Height = 30 };
        var modelBox = new TextBox { Text = cfg?.ModelName ?? "", FontSize = 12, Height = 30 };
        var keyBox = new PasswordBox { FontSize = 12, Height = 30 };
        var maxTokensBox = new TextBox { Text = cfg?.MaxOutputTokens?.ToString() ?? "", FontSize = 12, Height = 30 };
        var contextBox = new TextBox { Text = cfg?.ContextWindowTokens?.ToString() ?? "", FontSize = 12, Height = 30 };
        var reasoningCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
        reasoningCombo.Items.Add(new ComboBoxItem { Content = "关闭思考（推荐，响应更快）" });
        reasoningCombo.Items.Add(new ComboBoxItem { Content = "跟随模型默认" });
        reasoningCombo.SelectedIndex = string.IsNullOrEmpty(cfg?.ReasoningEffort) ? 1 : 0;

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 0) };
        form.Children.Add(FormLabel("接口地址（OpenAI 兼容，如 https://api.openai.com/v1）"));
        form.Children.Add(baseBox);
        form.Children.Add(FormLabel("模型名（如 gpt-4o / sensenova-6.7-flash-lite）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(modelBox);
        form.Children.Add(FormLabel("API Key（存 Windows 凭据管理器，不落明文）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(keyBox);
        if (!string.IsNullOrEmpty(cfg?.ApiKeyRef))
        {
            form.Children.Add(new TextBlock
            {
                Text = "已保存凭据；留空保持不变",
                FontSize = 11,
                Foreground = Brush("TextTertiaryBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        form.Children.Add(FormLabel("最大输出 token（留空 = 短句默认；国产模型一般不用填）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(maxTokensBox);
        form.Children.Add(FormLabel("上下文长度 token（留空 = 会话记住最近 5 轮；如 256000）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(contextBox);
        form.Children.Add(FormLabel("思考模式", new Thickness(0, 12, 0, 5)));
        form.Children.Add(reasoningCombo);

        var window = new Window
        {
            Title = "模型连接",
            Width = 440,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = Brush("WindowBgBrush"),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var testController = new Ai.ModelConnectionTestController(_ai);
        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextSecondaryBrush"),
        };
        ModelConnectionDraft ReadDraft()
        {
            var maxTokens = int.TryParse(maxTokensBox.Text.Trim(), out var mt) && mt > 0 ? (int?)mt : null;
            var contextTokens = int.TryParse(contextBox.Text.Trim(), out var context) && context > 0
                ? (int?)context
                : null;
            return new ModelConnectionDraft(
                baseBox.Text.Trim(),
                modelBox.Text.Trim(),
                cfg?.ApiKeyRef ?? "",
                keyBox.Password,
                cfg?.Capabilities ?? (Core.Scheduling.ModelCapabilities.Chat | Core.Scheduling.ModelCapabilities.Vision),
                reasoningCombo.SelectedIndex == 0 ? "none" : null,
                maxTokens,
                contextTokens);
        }

        var testButton = new Button
        {
            Content = "测试连接",
            Style = AppStyle("ButtonDefaultStyle"),
            Width = 100,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        testButton.Click += async (_, _) =>
        {
            testButton.IsEnabled = false;
            status.Text = _i18n.T("正在测试连接...");
            status.Foreground = Brush("TextSecondaryBrush");
            try
            {
                var result = await testController.TestLatestAsync(ReadDraft());
                if (result is null) return;
                status.Text = DescribeConnectionTest(result);
                status.Foreground = result.Success ? Brush("SuccessBrush") : Brush("DangerBrush");
            }
            finally
            {
                if (window.IsLoaded) testButton.IsEnabled = true;
            }
        };
        void InvalidateTest()
        {
            testController.Cancel();
            status.Text = "";
            testButton.IsEnabled = true;
        }
        baseBox.TextChanged += (_, _) => InvalidateTest();
        modelBox.TextChanged += (_, _) => InvalidateTest();
        keyBox.PasswordChanged += (_, _) => InvalidateTest();
        maxTokensBox.TextChanged += (_, _) => InvalidateTest();
        contextBox.TextChanged += (_, _) => InvalidateTest();
        reasoningCombo.SelectionChanged += (_, _) => InvalidateTest();
        window.Closed += (_, _) => testController.Dispose();

        var saveButton = new Button
        {
            Content = "保存模型连接",
            Style = AppStyle("ButtonPrimaryStyle"),
            Width = 130,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        saveButton.Click += (_, _) =>
        {
            testController.Cancel();
            var draft = ReadDraft();
            if (draft.BaseUrl.Length == 0 || draft.ModelName.Length == 0)
            {
                status.Text = _i18n.T("接口地址和模型名不能为空");
                status.Foreground = Brush("DangerBrush");
                return;
            }

            try
            {
                var connectionId = cfg?.Id ?? ProviderCredentialRefs.NewConnectionId();
                var oldSecret = string.IsNullOrEmpty(cfg?.ApiKeyRef) ? null : creds.Get(cfg.ApiKeyRef);
                if (!string.IsNullOrEmpty(cfg?.ApiKeyRef)
                    && oldSecret is null
                    && string.IsNullOrEmpty(draft.DraftApiKey))
                {
                    status.Text = _i18n.T("已保存的凭据不存在，请重新输入 API Key");
                    status.Foreground = Brush("DangerBrush");
                    return;
                }
                var secret = string.IsNullOrEmpty(draft.DraftApiKey) ? oldSecret : draft.DraftApiKey;
                var keyRef = secret is null ? "" : ProviderCredentialRefs.ForModel(connectionId);
                _ = ProviderEndpointPolicy.BuildRequestUri(draft.BaseUrl, "models", secret is not null);

                var previousTargetSecret = keyRef.Length == 0 ? null : creds.Get(keyRef);
                var credentialChanged = keyRef.Length > 0
                    && !string.Equals(previousTargetSecret, secret, StringComparison.Ordinal);
                if (credentialChanged) creds.Set(keyRef, secret!);

                var newCfg = new Core.Scheduling.ProviderConfig(
                    Id: connectionId,
                    Name: draft.ModelName,
                    BaseUrl: draft.BaseUrl,
                    ApiKeyRef: keyRef,
                    ModelName: draft.ModelName,
                    Capabilities: draft.Capabilities,
                    IsDefault: cfg?.IsDefault ?? true,
                    ReasoningEffort: draft.ReasoningEffort,
                    MaxOutputTokens: draft.MaxOutputTokens,
                    ContextWindowTokens: draft.ContextWindowTokens);
                var models = providers.Models.ToList();
                var index = models.FindIndex(model => model.Id == newCfg.Id);
                if (index >= 0) models[index] = newCfg;
                else models.Add(newCfg);
                var nextProviders = new Core.Scheduling.ProvidersFileModel
                {
                    Models = models,
                    Image = providers.Image,
                };
                try
                {
                    _ai.ApplyProviders(nextProviders);
                }
                catch (JsonStoreException ex)
                {
                    var credentialRollbackComplete = true;
                    if (credentialChanged)
                    {
                        try
                        {
                            if (previousTargetSecret is null) creds.Delete(keyRef);
                            else creds.Set(keyRef, previousTargetSecret);
                        }
                        catch (CredentialStoreException)
                        {
                            credentialRollbackComplete = false;
                        }
                    }
                    PersistenceErrorPresenter.Report(ex, window);
                    status.Text = credentialRollbackComplete
                        ? _i18n.T("保存失败，凭据已恢复")
                        : _i18n.T("保存失败，凭据未能完整恢复");
                    status.Foreground = Brush("DangerBrush");
                    return;
                }

                if (!string.IsNullOrEmpty(cfg?.ApiKeyRef)
                    && !string.Equals(cfg.ApiKeyRef, keyRef, StringComparison.Ordinal)
                    && nextProviders.Models.All(model => model.ApiKeyRef != cfg.ApiKeyRef)
                    && nextProviders.Image?.ApiKeyRef != cfg.ApiKeyRef)
                {
                    try { creds.Delete(cfg.ApiKeyRef); }
                    catch (CredentialStoreException)
                    {
                        MessageBox.Show(window, _i18n.T("模型连接已保存，但旧凭据清理失败。"), "DesktopPet");
                    }
                }

                if (nextProviders.Models.All(model => model.Id != _settings.Ai.ProviderId))
                    Save(settings => settings with { Ai = settings.Ai with { ProviderId = newCfg.Id } });
                window.Close();
                ShowPage("ai");
            }
            catch (CredentialStoreException ex)
            {
                status.Text = _i18n.Format("Windows 凭据操作失败（系统错误 {0}）", ex.NativeError);
                status.Foreground = Brush("DangerBrush");
            }
            catch (Core.Scheduling.ProviderException ex)
            {
                status.Text = ex.Code switch
                {
                    "insecure-transport" => _i18n.T("远程 HTTP 连接不能保存 API Key，请使用 HTTPS"),
                    _ => _i18n.T("模型接口地址无效"),
                };
                status.Foreground = Brush("DangerBrush");
            }
        };
        var footer = new Grid { Margin = new Thickness(20, 10, 20, 16) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(testButton, 0);
        footer.Children.Add(testButton);
        Grid.SetColumn(status, 1);
        status.Margin = new Thickness(10, 0, 10, 0);
        footer.Children.Add(status);
        Grid.SetColumn(saveButton, 2);
        footer.Children.Add(saveButton);
        var root = new DockPanel();
        DockPanel.SetDock(form, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(form);
        window.Content = root;
        WpfLocalizer.ApplyNew(window, _i18n);
        window.ShowDialog();
    }

    private string DescribeConnectionTest(ModelConnectionTestResult result)
    {
        if (result.Success)
        {
            return result.Models.Count == 0
                ? _i18n.T("连接成功，服务未返回模型列表")
                : _i18n.Format("连接成功，可用模型 {0} 个", result.Models.Count);
        }
        return result.Code switch
        {
            "invalid-url" => _i18n.T("模型接口地址无效"),
            "insecure-transport" => _i18n.T("远程 HTTP 连接不能发送 API Key，请使用 HTTPS"),
            "auth" => _i18n.T("鉴权失败，请检查 API Key"),
            "timeout" => _i18n.T("连接测试超时"),
            "network" => _i18n.T("无法连接模型服务"),
            "rate-limit" => _i18n.T("模型服务请求过于频繁"),
            "server" => _i18n.T("模型服务暂时不可用"),
            "invalid-response" => _i18n.T("模型列表响应格式无效"),
            "credential" => _i18n.T("无法读取 Windows 凭据"),
            _ => _i18n.T("模型服务拒绝了连接测试"),
        };
    }

    // ---- Phase 6f：生图连接（providers.json image 段）----

    private void ShowImageConnectionEditor()
    {
        if (_ai is null) return;
        var providers = _ai.Providers;
        var cfg = providers.Image;
        var creds = new Infra.Providers.WindowsCredentialStore();

        var baseBox = new TextBox { Text = cfg?.BaseUrl ?? "", FontSize = 12, Height = 30 };
        var modelBox = new TextBox { Text = cfg?.ModelName ?? "", FontSize = 12, Height = 30 };
        var keyBox = new PasswordBox { FontSize = 12, Height = 30 };

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 0) };
        form.Children.Add(FormLabel("生图 BaseUrl（OpenAI 兼容，如 https://api.openai.com/v1）"));
        form.Children.Add(baseBox);
        form.Children.Add(FormLabel("生图模型（如 gpt-image-1 / qwen-image）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(modelBox);
        form.Children.Add(FormLabel("API Key（存 Windows 凭据管理器，不落明文 JSON）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(keyBox);
        if (!string.IsNullOrEmpty(cfg?.ApiKeyRef))
        {
            form.Children.Add(new TextBlock
            {
                Text = "已保存凭据；留空保持不变",
                FontSize = 11,
                Foreground = Brush("TextTertiaryBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        var window = new Window
        {
            Title = "生图连接（总结图）",
            Width = 440,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = Brush("WindowBgBrush"),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var saveButton = new Button
        {
            Content = "保存生图连接",
            Style = AppStyle("ButtonPrimaryStyle"),
            Width = 130,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        saveButton.Click += (_, _) =>
        {
            var baseUrl = baseBox.Text.Trim();
            var model = modelBox.Text.Trim();
            if (baseUrl.Length == 0 || model.Length == 0)
            {
                MessageBox.Show(window, _i18n.T("BaseUrl 和模型名不能为空"), "DesktopPet");
                return;
            }

            try
            {
                var oldSecret = string.IsNullOrEmpty(cfg?.ApiKeyRef) ? null : creds.Get(cfg.ApiKeyRef);
                if (!string.IsNullOrEmpty(cfg?.ApiKeyRef)
                    && oldSecret is null
                    && string.IsNullOrEmpty(keyBox.Password))
                {
                    MessageBox.Show(window, _i18n.T("已保存的凭据不存在，请重新输入 API Key"), "DesktopPet");
                    return;
                }
                var secret = string.IsNullOrEmpty(keyBox.Password) ? oldSecret : keyBox.Password;
                var keyRef = secret is null ? "" : ProviderCredentialRefs.Image;
                _ = ProviderEndpointPolicy.BuildRequestUri(baseUrl, "images/generations", secret is not null);
                var previousTargetSecret = keyRef.Length == 0 ? null : creds.Get(keyRef);
                var credentialChanged = keyRef.Length > 0
                    && !string.Equals(previousTargetSecret, secret, StringComparison.Ordinal);
                if (credentialChanged) creds.Set(keyRef, secret!);

                var nextProviders = new Core.Scheduling.ProvidersFileModel
                {
                    Models = providers.Models.ToList(),
                    Image = new Core.Scheduling.ImageGenConfig(baseUrl, keyRef, model),
                };
                try
                {
                    _ai.ApplyProviders(nextProviders);
                }
                catch (JsonStoreException ex)
                {
                    if (credentialChanged)
                    {
                        try
                        {
                            if (previousTargetSecret is null) creds.Delete(keyRef);
                            else creds.Set(keyRef, previousTargetSecret);
                        }
                        catch (CredentialStoreException)
                        {
                            MessageBox.Show(window, _i18n.T("配置保存失败，凭据未能完整恢复。"), "DesktopPet");
                        }
                    }
                    PersistenceErrorPresenter.Report(ex, window);
                    return;
                }

                if (!string.IsNullOrEmpty(cfg?.ApiKeyRef)
                    && !string.Equals(cfg.ApiKeyRef, keyRef, StringComparison.Ordinal)
                    && nextProviders.Models.All(connection => connection.ApiKeyRef != cfg.ApiKeyRef))
                {
                    try { creds.Delete(cfg.ApiKeyRef); }
                    catch (CredentialStoreException)
                    {
                        MessageBox.Show(window, _i18n.T("生图连接已保存，但旧凭据清理失败。"), "DesktopPet");
                    }
                }
                window.Close();
                ShowPage("ai");
            }
            catch (CredentialStoreException ex)
            {
                MessageBox.Show(
                    window,
                    _i18n.Format("Windows 凭据操作失败（系统错误 {0}）", ex.NativeError),
                    "DesktopPet");
            }
            catch (Core.Scheduling.ProviderException ex)
            {
                MessageBox.Show(
                    window,
                    ex.Code == "insecure-transport"
                        ? _i18n.T("远程 HTTP 连接不能保存 API Key，请使用 HTTPS")
                        : _i18n.T("生图接口地址无效"),
                    "DesktopPet");
            }
        };
        var footer = new Grid { Margin = new Thickness(20, 4, 20, 16) };
        footer.Children.Add(saveButton);
        var root = new DockPanel();
        DockPanel.SetDock(form, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(form);
        window.Content = root;
        WpfLocalizer.ApplyNew(window, _i18n);
        window.ShowDialog();
    }

    // ---- TTS 在线端点连接（providers.json tts 段；windows-tts-design.md §5.2）----

    private void ShowTtsConnectionEditor()
    {
        if (_ai is null) return;
        var providers = _ai.Providers;
        var cfg = providers.Tts;
        var creds = new Infra.Providers.WindowsCredentialStore();

        var baseBox = new TextBox { Text = cfg?.BaseUrl ?? "", FontSize = 12, Height = 30 };
        var modelBox = new TextBox { Text = cfg?.ModelName ?? "", FontSize = 12, Height = 30 };
        var voiceBox = new TextBox { Text = cfg?.Voice ?? "", FontSize = 12, Height = 30 };
        var keyBox = new PasswordBox { FontSize = 12, Height = 30 };

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 0) };
        form.Children.Add(FormLabel("BaseUrl（OpenAI 兼容 TTS，如 https://api.siliconflow.cn/v1）"));
        form.Children.Add(baseBox);
        form.Children.Add(FormLabel("模型（如 FunAudioLLM/CosyVoice2-0.5B / tts-1）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(modelBox);
        form.Children.Add(FormLabel("默认音色 id（可选；空 = 自动，可在朗读声音下拉中选）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(voiceBox);
        form.Children.Add(FormLabel("API Key（存 Windows 凭据管理器，不落明文 JSON）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(keyBox);
        if (!string.IsNullOrEmpty(cfg?.ApiKeyRef))
        {
            form.Children.Add(new TextBlock
            {
                Text = "已保存凭据；留空保持不变",
                FontSize = 11,
                Foreground = Brush("TextTertiaryBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        var window = new Window
        {
            Title = "自配 TTS 端点",
            Width = 460,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = Brush("WindowBgBrush"),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var saveButton = new Button
        {
            Content = "保存并测试连接",
            Style = AppStyle("ButtonPrimaryStyle"),
            Width = 150,
            Height = 32,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        saveButton.Click += (_, _) =>
        {
            var baseUrl = baseBox.Text.Trim();
            var model = modelBox.Text.Trim();
            if (baseUrl.Length == 0 || model.Length == 0)
            {
                MessageBox.Show(window, _i18n.T("BaseUrl 和模型名不能为空"), "DesktopPet");
                return;
            }

            try
            {
                var oldSecret = string.IsNullOrEmpty(cfg?.ApiKeyRef) ? null : creds.Get(cfg.ApiKeyRef);
                if (!string.IsNullOrEmpty(cfg?.ApiKeyRef)
                    && oldSecret is null
                    && string.IsNullOrEmpty(keyBox.Password))
                {
                    MessageBox.Show(window, _i18n.T("已保存的凭据不存在，请重新输入 API Key"), "DesktopPet");
                    return;
                }
                var secret = string.IsNullOrEmpty(keyBox.Password) ? oldSecret : keyBox.Password;
                var keyRef = secret is null ? "" : Infra.Providers.ProviderCredentialRefs.Tts;
                _ = ProviderEndpointPolicy.BuildRequestUri(baseUrl, "audio/speech", secret is not null);
                var previousTargetSecret = keyRef.Length == 0 ? null : creds.Get(keyRef);
                var credentialChanged = keyRef.Length > 0
                    && !string.Equals(previousTargetSecret, secret, StringComparison.Ordinal);
                if (credentialChanged) creds.Set(keyRef, secret!);

                var nextProviders = new Core.Scheduling.ProvidersFileModel
                {
                    Models = providers.Models.ToList(),
                    Image = providers.Image,
                    Tts = new Core.Scheduling.TtsEndpointConfig(baseUrl, keyRef, model, voiceBox.Text.Trim()),
                };
                try
                {
                    _ai.ApplyProviders(nextProviders);
                }
                catch (JsonStoreException ex)
                {
                    if (credentialChanged)
                    {
                        try
                        {
                            if (previousTargetSecret is null) creds.Delete(keyRef);
                            else creds.Set(keyRef, previousTargetSecret);
                        }
                        catch (CredentialStoreException)
                        {
                            MessageBox.Show(window, _i18n.T("配置保存失败，凭据未能完整恢复。"), "DesktopPet");
                        }
                    }
                    PersistenceErrorPresenter.Report(ex, window);
                    return;
                }

                // 连接测试：拉音色列表（失败按错误分类提示，不阻断保存）
                _ = TestTtsConnectionAsync(window, saveButton, baseUrl, keyRef, model, secret);
                ShowPage("ai"); // 刷新页面：连接按钮文案（已配置）与引擎列表即时更新
            }
            catch (CredentialStoreException ex)
            {
                MessageBox.Show(
                    window,
                    _i18n.Format("Windows 凭据操作失败（系统错误 {0}）", ex.NativeError),
                    "DesktopPet");
            }
            catch (Core.Scheduling.ProviderException ex)
            {
                MessageBox.Show(
                    window,
                    ex.Code == "insecure-transport"
                        ? _i18n.T("远程 HTTP 连接不能保存 API Key，请使用 HTTPS")
                        : _i18n.T("TTS 接口地址无效"),
                    "DesktopPet");
            }
        };

        var footer = new Grid { Margin = new Thickness(20, 4, 20, 16) };
        footer.Children.Add(saveButton);
        var root = new DockPanel();
        DockPanel.SetDock(form, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(form);
        window.Content = root;
        WpfLocalizer.ApplyNew(window, _i18n);
        window.ShowDialog();
    }

    /// <summary>保存后异步测试：拉取 /audio/voices 验证连接；失败按 auth/network/invalid 分类提示。</summary>
    private async Task TestTtsConnectionAsync(
        Window window,
        Button saveButton,
        string baseUrl,
        string keyRef,
        string model,
        string? secret)
    {
        saveButton.IsEnabled = false;
        saveButton.Content = "测试中…";
        try
        {
            var provider = new Infra.Providers.OpenAiCompatibleTtsProvider(
                new Core.Scheduling.TtsEndpointConfig(baseUrl, keyRef, model),
                new Infra.Providers.WindowsCredentialStore(),
                ProviderHttpClient.Create());
            var voices = await provider.ListVoicesAsync(CancellationToken.None);
            if (voices.Count > 0)
            {
                MessageBox.Show(window, $"连接成功：检测到 {voices.Count} 个音色（如 {voices[0].DisplayName}）", "DesktopPet");
            }
            else
            {
                MessageBox.Show(window, "连接成功（端点未提供音色列表，可在朗读声音中手动输入）", "DesktopPet");
            }
        }
        catch (Core.Scheduling.ProviderException ex)
        {
            var message = ex.Code switch
            {
                "auth" => "鉴权失败：请检查 API Key",
                "timeout" => "连接超时：请检查网络或 BaseUrl",
                "network" => "网络错误：请检查 BaseUrl 或网络连接",
                _ => $"连接失败：{ex.Message}",
            };
            MessageBox.Show(window, message, "DesktopPet");
        }
        finally
        {
            saveButton.IsEnabled = true;
            saveButton.Content = "保存并测试连接";
            window.Close();
        }
    }

    private void ShowDiaryViewer()
    {
        var diaryDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPet", "diary");
        var files = Directory.Exists(diaryDir)
            ? Directory.GetFiles(diaryDir, "*.txt").OrderByDescending(f => f).ToList()
            : [];

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        if (files.Count == 0)
        {
            form.Children.Add(new TextBlock
            {
                Text = "还没有日记。每天结束时自动生成\"你的一天\"（需开启每日总结 + 模型连接）。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondaryBrush"),
            });
        }
        else
        {
            foreach (var file in files)
            {
                var day = System.IO.Path.GetFileNameWithoutExtension(file);
                var png = System.IO.Path.Combine(diaryDir, day + ".png");
                var item = new Border
                {
                    Background = Brush("CardBgBrush"),
                    BorderBrush = Brush("StrokeBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14),
                    Margin = new Thickness(0, 0, 0, 10),
                    Effect = Shadow("ShadowCard"),
                };
                var inner = new StackPanel();
                var diaryDay = new TextBlock
                {
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("TextPrimaryBrush"),
                };
                WpfLocalizer.SetDynamicText(diaryDay, day);
                inner.Children.Add(diaryDay);
                try
                {
                    var text = File.ReadAllText(file);
                    var diaryText = new TextBlock
                    {
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("TextSecondaryBrush"),
                        Margin = new Thickness(0, 5, 0, 0),
                    };
                    WpfLocalizer.SetDynamicText(
                        diaryText,
                        text.Length > 200 ? text[..200] + "…" : text);
                    inner.Children.Add(diaryText);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Error("Settings", $"diary read failed: {ex.GetType().Name}: {ex.Message}");
                }
                if (File.Exists(png))
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(png);
                        bitmap.EndInit();
                        inner.Children.Add(new Image
                        {
                            Source = bitmap,
                            MaxWidth = 320,
                            Margin = new Thickness(0, 8, 0, 0),
                            Stretch = Stretch.Uniform,
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Settings", $"diary image load failed: {ex.GetType().Name}: {ex.Message}");
                    } // 图损坏不阻塞文本
                }
                item.Child = inner;
                form.Children.Add(item);
            }
        }

        var window = new Window
        {
            Title = "宠物日记",
            Width = 440,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = Brush("WindowBgBrush"),
            ShowInTaskbar = false,
        };
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.Content = scroll;
        WpfLocalizer.ApplyNew(window, _i18n);
        window.ShowDialog();
    }

    private void SaveRoam(Core.Roaming.RoamConfig roam)
        => Save(s => s with { Roam = roam });

    /// <summary>停顿范围显示：如 "1.2–3.5s"（保留区间信息，避免单值误导）。</summary>
    private static string FormatPauseRange(double minMs, double maxMs)
    {
        var min = (minMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        var max = (maxMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        return $"{min}–{max}s";
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer.Stop();
        StopClipHover(); // hover 预览 timer 随窗口关闭停止（DispatcherTimer 保持目标存活，必须显式停止）
        StopDiagnostics();
        base.OnClosed(e);
    }
}

/// <summary>宠物卡片实时动画预览（共享 timer 驱动，对齐"128px 实时动画预览"）。</summary>
public sealed class PetPreviewCard : FrameworkElement
{
    private const int PreviewSize = 96;

    private readonly Image _image = new();
    private readonly SpriteLoader _loader;
    private readonly string _slug;
    private WriteableBitmap? _bitmap;
    private readonly ReusablePixelBuffer _frameBuffer = new(PreviewSize * PreviewSize * 4);
    private PetRenderer? _renderer;
    private int _fallbackFrame;
    private bool _loading;

    public PetPreviewCard(SpriteLoader loader, string slug)
    {
        _loader = loader;
        _slug = slug;
        Width = PreviewSize;
        Height = PreviewSize;
        _image.Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        AddVisualChild(_image);
        AddLogicalChild(_image);
        _bitmap = new WriteableBitmap(PreviewSize, PreviewSize, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
        DrawFallback();

        if (loader.TryGetCached(slug) is { } cached)
        {
            SetSprite(cached);
        }
        Loaded += async (_, _) => await LoadSpriteAsync();
    }

    public void Advance()
    {
        if (_renderer is null)
        {
            DrawFallback();
            return;
        }
        _renderer.AdvanceFrame();
        Draw();
    }

    private async Task LoadSpriteAsync()
    {
        if (_loading || _renderer is not null) return;
        _loading = true;
        try
        {
            var sheet = await _loader.LoadAsync(_slug);
            if (sheet is not null && IsLoaded) SetSprite(sheet);
        }
        finally
        {
            _loading = false;
        }
    }

    private void SetSprite(SpriteSheet sheet)
    {
        _renderer = new PetRenderer(sheet);
        _renderer.SetState("idle");
        _bitmap = new WriteableBitmap(PreviewSize, PreviewSize, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
        Draw();
    }

    private void DrawFallback()
    {
        if (_bitmap is null) return;
        var frame = PlaceholderPet.Frames[_fallbackFrame++ % PlaceholderPet.Frames.Count];
        var buffer = _frameBuffer.Clear();
        var scale = Math.Max(1, Math.Min(
            PreviewSize / PlaceholderPet.FrameWidth,
            PreviewSize / PlaceholderPet.FrameHeight));
        var width = PlaceholderPet.FrameWidth * scale;
        var height = PlaceholderPet.FrameHeight * scale;
        var dx = (PreviewSize - width) / 2;
        var dy = (PreviewSize - height) / 2;
        for (var y = 0; y < PlaceholderPet.FrameHeight; y++)
        {
            for (var x = 0; x < PlaceholderPet.FrameWidth; x++)
            {
                var source = (y * PlaceholderPet.FrameWidth + x) * 4;
                if (frame.Rgba[source + 3] == 0) continue;
                for (var sy = 0; sy < scale; sy++)
                {
                    for (var sx = 0; sx < scale; sx++)
                    {
                        var destination = ((dy + y * scale + sy) * PreviewSize + dx + x * scale + sx) * 4;
                        Buffer.BlockCopy(frame.Rgba, source, buffer, destination, 4);
                    }
                }
            }
        }
        PixelBuffer.RgbaToBgra(buffer);
        _bitmap.WritePixels(new Int32Rect(0, 0, PreviewSize, PreviewSize), buffer, PreviewSize * 4, 0);
    }

    private void Draw()
    {
        if (_bitmap is null || _renderer is null) return;
        var buffer = _frameBuffer.Clear();
        _renderer.DrawFrame(buffer, PreviewSize, PreviewSize);
        PixelBuffer.RgbaToBgra(buffer);
        _bitmap.WritePixels(new Int32Rect(0, 0, PreviewSize, PreviewSize), buffer, PreviewSize * 4, 0);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _image;

    protected override Size MeasureOverride(Size availableSize)
    {
        _image.Measure(new Size(PreviewSize, PreviewSize));
        return new Size(PreviewSize, PreviewSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _image.Arrange(new Rect(new Point(), finalSize));
        return finalSize;
    }
}

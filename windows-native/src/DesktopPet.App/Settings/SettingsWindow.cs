using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.App.Rendering;
using DesktopPet.App.Windows;
using DesktopPet.Core.Care;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Pets;
using DesktopPet.Core.Rendering;
using DesktopPet.Core.Roaming;
using DesktopPet.Core.Storage;

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

    public SettingsWindow(IJsonStore store, PetWindowManager manager, SpriteLoader spriteLoader, I18nService i18n,
        Ai.AiCoordinator? ai = null)
    {
        _store = store;
        _manager = manager;
        _spriteLoader = spriteLoader;
        _i18n = i18n;
        _ai = ai;
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
        ShowPage("pets");

        // 共享预览 timer：驱动所有宠物卡片的实时动画（3fps）
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 3) };
        _previewTimer.Tick += (_, _) =>
        {
            foreach (var card in _previewCards) card.Advance();
        };
        _previewTimer.Start();
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

    private void ShowPage(string id)
    {
        _currentPage = id;
        UpdateNavSelection(id);
        StopClipHover(); // 离开动作页 → 停止 hover 预览 timer（避免泄漏）
        _contentHost.Content = id switch
        {
            "pets" => BuildPetsPage(),
            "actions" => BuildActionsPage(),
            "appearance" => BuildAppearancePage(),
            "bubble" => BuildBubblePage(),
            "roam" => BuildRoamPage(),
            "ai" => BuildAiPage(),
            "language" => BuildLanguagePage(),
            _ => BuildAboutPage(),
        };
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
        System.Windows.Automation.AutomationProperties.SetName(picker, "动作宠物选择器");
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
        System.Windows.Automation.AutomationProperties.SetName(interval, "待机动作间隔");
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
            System.Windows.Automation.AutomationProperties.SetName(duration, property == "click" ? "点击动作时长" : "庆祝时长");
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
            block.Children.Add(new TextBlock
            {
                Text = $"{label}（{tip}，{PetAnimationResolver.MinDurationSeconds}-{PetAnimationResolver.MaxDurationSeconds} 秒）",
                FontSize = 12,
                Foreground = Brush("TextPrimaryBrush"),
            });
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
            block.Children.Add(new TextBlock
            {
                Text = $"{label} — {tip}",
                FontSize = 12,
                Foreground = Brush("TextPrimaryBrush"),
                Margin = new Thickness(0, 0, 0, 6),
            });
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
            System.Windows.Automation.AutomationProperties.SetName(cellButton, $"动作格子 #{clipIndex}");
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
        identity.Children.Add(new TextBlock
        {
            Text = instance.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 180,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var care = _store.LoadCare().GetValueOrDefault(instance.Id);
        var level = CareEngine.DisplayLevel(care?.Xp ?? 0);
        var stage = CareEngine.StageName(CareEngine.LevelForXp(care?.Xp ?? 0));
        identity.Children.Add(new Border
        {
            Background = Brush("SuccessSoftBrush"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = $"{stage} · Lv {level}",
                FontSize = 10.5,
                Foreground = Brush("SuccessBrush"),
                FontWeight = FontWeights.SemiBold,
            },
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
        System.Windows.Automation.AutomationProperties.SetName(remove, "移除宠物");
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
        personaCombo.Items.Add("跟随全局");
        foreach (var personaItem in allPersonas) personaCombo.Items.Add(personaItem.Name);
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
        _store.SavePetStore(store);
        _manager.Reconcile(store, _manager.GloballyVisible);
        var updated = PetStoreModel.PetInstanceById(store, id);
        if (updated is not null) _manager.ApplyInstance(updated); // 动作配置即时生效（无需重建窗口）
    }

    private void RemoveInstance(string id)
    {
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        store = PetStoreModel.RemovePetInstance(store, id);
        _store.SavePetStore(store);
        _manager.Reconcile(store, _manager.GloballyVisible);
        ShowPage("pets");
    }

    private async Task ImportPetAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "精灵图 (PNG/WebP)|*.png;*.webp",
            Title = "导入宠物精灵图",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            var sheet = SpriteSheet.Decode(bytes, Path.GetFileName(dialog.FileName));
            if (sheet is null)
            {
                MessageBox.Show(this, "无法解析精灵图（需要带透明通道的 PNG/WebP）", "DesktopPet");
                return;
            }
            var preview = new SpritePreviewWindow(sheet, bytes, Path.GetFileNameWithoutExtension(dialog.FileName))
            {
                Owner = this,
            };
            if (preview.ShowDialog() == true)
            {
                var (payload, name) = preview.ImportPayload;
                _manager.ImportSprite(payload, name);
                ShowPage("pets");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败：{ex.Message}", "DesktopPet");
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
        System.Windows.Automation.AutomationProperties.SetName(chatter, "闲谈台词池");
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
        System.Windows.Automation.AutomationProperties.SetName(hungry, "饥饿台词池");
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
            Text = "气泡文字字体：系统默认（随系统）/ 圆体（Segoe UI Variable）/ 等宽（Consolas）",
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
        stack.Children.Add(SectionCard("点击宠物", click, margin: 0));

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

        // 移动停顿：1-30s（引擎下限 1s，对齐 pause.ts）。爱动/不爱动用户都能调；"待着不动"模式下无意义但保留可调
        var pauseSeconds = Math.Max(1, (int)Math.Round(roam.WanderPauseMinMs / 1000.0));
        var pause = new Slider { Minimum = 1, Maximum = 30, Value = pauseSeconds, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var pauseValue = new TextBlock
        {
            Text = $"{pauseSeconds}s",
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 40,
        };
        pause.ValueChanged += (_, e) => pauseValue.Text = $"{(int)e.NewValue}s";
        CommitSliderOnRelease(pause, () =>
        {
            var v = (int)pause.Value;
            var (min, max) = Pause.NormalizeWanderPauseRange(v * 1000.0, v * 1000.0 + 500);
            SaveRoam(_settings.Roam with { WanderPauseMinMs = min, WanderPauseMaxMs = max });
        });
        var pauseCard = new StackPanel();
        pauseCard.Children.Add(Row(pause, pauseValue));
        pauseCard.Children.Add(new TextBlock
        {
            Text = "走一段后休息多久（1-30 秒）；调高让宠物更安静、调低更活跃；「待着不动」模式不生效",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(SectionCard("移动停顿", pauseCard, margin: 0));

        return PageScroller(stack, PageHeader("漫游", "宠物在桌面上的活动方式"));
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
            radio.Checked += (_, _) => Save(s => s with { Lang = value });
            lang.Children.Add(radio);
        }
        stack.Children.Add(SectionCard("语言 / Language", lang, margin: 0));

        return PageScroller(stack, PageHeader("语言", "界面显示语言"));
    }

    // ---- 关于页 ----

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
        aboutStack.Children.Add(new TextBlock
        {
            Text = $"版本 {typeof(SettingsWindow).Assembly.GetName().Version}\n.NET 8 + WPF · Lumen 2.0",
            FontSize = 12,
            LineHeight = 20,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(Card(aboutStack));

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
        statsStack.Children.Add(StatRow("数据目录", $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\DesktopPet"));
        stack.Children.Add(Card(statsStack, margin: 0));

        return PageScroller(stack, PageHeader("关于", "版本与统计"));
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
            providerPanel.Children.Add(new TextBlock
            {
                Text = selectedProvider.BaseUrl,
                FontSize = 11,
                Foreground = Brush("TextTertiaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
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
        companionPanel.Children.Add(ToggleRow("语音朗读", "对话模式朗读回复，Edge TTS；弹幕模式不朗读", ai.TtsEnabled,
            v => Save(s => s with { Ai = s.Ai with { TtsEnabled = v } }), margin: 0));
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
            nameRow.Children.Add(new TextBlock
            {
                Text = persona.Name + (persona.Builtin ? "" : " · 自定义"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = selected ? Brush("AccentBrush") : Brush("TextPrimaryBrush"),
            });
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
            inner.Children.Add(new TextBlock
            {
                Text = persona.Description,
                FontSize = 10.5,
                Foreground = Brush("TextTertiaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
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
        _settings = AppSettings.Normalize(change(_settings));
        // 首次开启 AI → 引导窗（称呼+人格）；完成标记随本 Save 一并落盘
        if (_settings.Ai.Enabled && !before.Ai.Enabled && !_settings.Ai.Onboarded)
        {
            var ai = _ai;
            if (ai is not null)
            {
                var personas = ai.Personas;
                var profile = _store.LoadMemoryProfile()
                    ?? new DesktopPet.Core.Memory.UserProfile("", [], "", "");
                var welcome = new Windows.WelcomeWindow(
                    builtinPersonas: Core.Personas.BuiltinPersonas.GetAll(),
                    initialCallName: profile.CallName,
                    selectedPersonaId: personas.SelectedId,
                    onComplete: (callName, personaId) =>
                    {
                        ai.CompleteOnboarding(callName, personaId);
                        _settings = _settings with { Ai = _settings.Ai with { Onboarded = true } };
                    });
                welcome.Owner = this;
                welcome.ShowDialog();
            }
        }
        _store.SaveSettings(_settings);
        _manager.ApplySettings(_settings);
        _ai?.ApplySettings(_settings); // AI 设置同步（总开关启停 Agent / 配置下发）
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
                MessageBox.Show(window, "名称和提示词不能为空", "DesktopPet");
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
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "desktoppet-ai.log"),
                $"[conn-editor] EXCEPTION: {ex}" + System.Environment.NewLine);
            MessageBox.Show(this, "模型连接编辑器打开失败：" + ex.Message, "DesktopPet");
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
        var keyBox = new TextBox { Text = string.IsNullOrEmpty(cfg?.ApiKeyRef) ? "" : "（已配置，留空不修改）", FontSize = 12, Height = 30 };
        var maxTokensBox = new TextBox { Text = cfg?.MaxOutputTokens?.ToString() ?? "", FontSize = 12, Height = 30 };
        var contextBox = new TextBox { Text = cfg?.ContextWindowTokens?.ToString() ?? "", FontSize = 12, Height = 30 };
        var reasoningCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
        reasoningCombo.Items.Add("关闭思考（推荐，响应更快）");
        reasoningCombo.Items.Add("跟随模型默认");
        reasoningCombo.SelectedIndex = string.IsNullOrEmpty(cfg?.ReasoningEffort) ? 1 : 0;

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 0) };
        form.Children.Add(FormLabel("接口地址（OpenAI 兼容，如 https://api.openai.com/v1）"));
        form.Children.Add(baseBox);
        form.Children.Add(FormLabel("模型名（如 gpt-4o / sensenova-6.7-flash-lite）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(modelBox);
        form.Children.Add(FormLabel("API Key（存 Windows 凭据管理器，不落明文）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(keyBox);
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
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = Brush("WindowBgBrush"),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        var saveButton = new Button
        {
            Content = "保存模型连接",
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
                MessageBox.Show(window, "接口地址和模型名不能为空", "DesktopPet");
                return;
            }
            var keyRef = string.IsNullOrEmpty(cfg?.ApiKeyRef) ? "model-key" : cfg.ApiKeyRef;
            if (keyBox.Text.Length > 0 && keyBox.Text != "（已配置，留空不修改）")
                creds.Set(keyRef, keyBox.Text);
            // 数字字段：留空/非法 = null（走默认），填了才生效
            var maxTokens = int.TryParse(maxTokensBox.Text.Trim(), out var mt) && mt > 0 ? (int?)mt : null;
            var contextTokens = int.TryParse(contextBox.Text.Trim(), out var ct) && ct > 0 ? (int?)ct : null;
            var newCfg = new Core.Scheduling.ProviderConfig(
                Id: cfg?.Id ?? "model",
                Name: model,
                BaseUrl: baseUrl,
                ApiKeyRef: keyRef,
                ModelName: model,
                Capabilities: cfg?.Capabilities ?? (Core.Scheduling.ModelCapabilities.Chat | Core.Scheduling.ModelCapabilities.Vision),
                IsDefault: cfg?.IsDefault ?? true,
                ReasoningEffort: reasoningCombo.SelectedIndex == 0 ? "none" : null,
                MaxOutputTokens: maxTokens,
                ContextWindowTokens: contextTokens);
            var models = providers.Models.ToList();
            var idx = models.FindIndex(m => m.Id == newCfg.Id);
            if (idx >= 0) models[idx] = newCfg;
            else models.Add(newCfg);
            providers.Models = models;
            _ai.ApplyProviders(providers);
            // 新建场景：当前选中 id 不存在 → 指向新连接，AI 立即可用
            if (providers.Models.All(m => m.Id != _settings.Ai.ProviderId))
                Save(s => s with { Ai = s.Ai with { ProviderId = newCfg.Id } });
            window.Close();
            ShowPage("ai");
        };
        var footer = new Grid { Margin = new Thickness(20, 4, 20, 16) };
        footer.Children.Add(saveButton);
        var root = new DockPanel();
        DockPanel.SetDock(form, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(form);
        window.Content = root;
        window.ShowDialog();
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
        var keyBox = new TextBox { Text = string.IsNullOrEmpty(cfg?.ApiKeyRef) ? "" : "（已配置，留空不修改）", FontSize = 12, Height = 30 };

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 0) };
        form.Children.Add(FormLabel("生图 BaseUrl（OpenAI 兼容，如 https://api.openai.com/v1）"));
        form.Children.Add(baseBox);
        form.Children.Add(FormLabel("生图模型（如 gpt-image-1 / qwen-image）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(modelBox);
        form.Children.Add(FormLabel("API Key（存 Windows 凭据管理器，不落明文 JSON）", new Thickness(0, 12, 0, 5)));
        form.Children.Add(keyBox);

        var window = new Window
        {
            Title = "生图连接（总结图）",
            Width = 440,
            Height = 320,
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
                MessageBox.Show(window, "BaseUrl 和模型名不能为空", "DesktopPet");
                return;
            }
            const string keyRef = "image-key";
            if (keyBox.Text.Length > 0 && keyBox.Text != "（已配置，留空不修改）")
                creds.Set(keyRef, keyBox.Text);
            providers.Image = new Core.Scheduling.ImageGenConfig(baseUrl, keyRef, model);
            _ai.ApplyProviders(providers);
            window.Close();
            ShowPage("ai");
        };
        var footer = new Grid { Margin = new Thickness(20, 4, 20, 16) };
        footer.Children.Add(saveButton);
        var root = new DockPanel();
        DockPanel.SetDock(form, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(form);
        window.Content = root;
        window.ShowDialog();
    }

    // ---- Phase 6f：日记查看（文本 + 总结图）----

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
                inner.Children.Add(new TextBlock
                {
                    Text = day,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("TextPrimaryBrush"),
                });
                try
                {
                    var text = File.ReadAllText(file);
                    inner.Children.Add(new TextBlock
                    {
                        Text = text.Length > 200 ? text[..200] + "…" : text,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brush("TextSecondaryBrush"),
                        Margin = new Thickness(0, 5, 0, 0),
                    });
                }
                catch (Exception) { }
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
                    catch (Exception) { } // 图损坏不阻塞文本
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
        window.ShowDialog();
    }

    private void SaveRoam(Core.Roaming.RoamConfig roam)
        => Save(s => s with { Roam = roam });

    protected override void OnClosed(EventArgs e)
    {
        _previewTimer.Stop();
        StopClipHover(); // hover 预览 timer 随窗口关闭停止（DispatcherTimer 保持目标存活，必须显式停止）
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

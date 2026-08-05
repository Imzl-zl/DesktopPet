using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// 设置窗口（Lumen 设计语言：浅色毛玻璃 + 左侧图标导航 + 卡片流）。
/// 页面：宠物（实时动画卡片/显隐/移除/导入）、外观、气泡、漫游、语言、关于。
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
        Width = 680;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
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

    // ---- 导航 ----

    private StackPanel BuildNavigation()
    {
        var nav = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0xED, 0xF0, 0xF4)) };
        var pages = new (string Id, string Label)[]
        {
            ("pets", "宠物"), ("appearance", "外观"), ("bubble", "气泡"),
            ("roam", "漫游"), ("ai", "AI 助手"), ("language", "语言"), ("about", "关于"),
        };
        foreach (var (id, label) in pages)
        {
            var button = new Button
            {
                Content = label,
                Height = 44,
                Margin = new Thickness(6, 4, 6, 0),
                FontSize = 11,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Tag = id,
            };
            button.Click += (_, _) => ShowPage(id);
            nav.Children.Add(button);
        }
        return nav;
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
        _contentHost.Content = id switch
        {
            "pets" => BuildPetsPage(),
            "appearance" => BuildAppearancePage(),
            "bubble" => BuildBubblePage(),
            "roam" => BuildRoamPage(),
            "ai" => BuildAiPage(),
            "language" => BuildLanguagePage(),
            _ => BuildAboutPage(),
        };
    }

    private static Border Card(UIElement content)
        => new()
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0x1C, 0x20, 0x28)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content,
        };

    private static ScrollViewer PageScroller(UIElement content)
    {
        var scroll = new ScrollViewer { Padding = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = content;
        return scroll;
    }

    // ---- 宠物页 ----

    private UIElement BuildPetsPage()
    {
        var stack = new StackPanel();
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        _previewCards.Clear();

        foreach (var instance in store.Instances)
        {
            stack.Children.Add(Card(BuildPetCard(instance)));
        }

        var add = new Button
        {
            Content = "＋ 添加宠物（导入精灵图）",
            Height = 36,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        add.Click += async (_, _) => await ImportPetAsync();
        stack.Children.Add(add);
        return PageScroller(stack);
    }

    private Grid BuildPetCard(PetInstance instance)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 实时动画预览（共享 timer 驱动）
        var preview = new PetPreviewCard(_spriteLoader, instance.SpriteSlug);
        _previewCards.Add(preview);
        Grid.SetColumn(preview, 0);
        grid.Children.Add(preview);

        var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var care = _store.LoadCare().GetValueOrDefault(instance.Id);
        var level = CareEngine.DisplayLevel(care?.Xp ?? 0);
        var stage = CareEngine.StageName(CareEngine.LevelForXp(care?.Xp ?? 0));

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock { Text = instance.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
        nameRow.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x4E, 0xCB, 0xA5)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock { Text = $"{stage} · Lv {level}", FontSize = 10, Foreground = Brushes.White },
        });
        info.Children.Add(nameRow);

        var visibleToggle = new CheckBox
        {
            Content = "在桌面显示",
            IsChecked = instance.Visible,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        visibleToggle.Checked += (_, _) => UpdateInstance(instance.Id, new PetInstancePatch { Visible = true });
        visibleToggle.Unchecked += (_, _) => UpdateInstance(instance.Id, new PetInstancePatch { Visible = false });
        info.Children.Add(visibleToggle);

        // Phase 6d：每宠物独立人格覆盖（空 = 跟随全局；设置页 AI 助手页为全局主入口）
        var personaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        personaRow.Children.Add(new TextBlock
        {
            Text = "人格：",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var personaCombo = new ComboBox { Width = 140, Height = 26, FontSize = 12 };
        var allPersonas = _ai?.Personas.MergeWithBuiltins() ?? [];
        personaCombo.Items.Add("跟随全局");
        foreach (var p in allPersonas) personaCombo.Items.Add(p.Name);
        var currentIndex = instance.PersonaId is null ? 0
            : allPersonas.Select((p, i) => (p, i)).FirstOrDefault(x => x.p.Id == instance.PersonaId).i + 1;
        personaCombo.SelectedIndex = currentIndex >= 0 && currentIndex < personaCombo.Items.Count ? currentIndex : 0;
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
        personaRow.Children.Add(personaCombo);
        info.Children.Add(personaRow);

        var remove = new Button
        {
            Content = "移除",
            Height = 26,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x54, 0x4B)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        remove.Click += (_, _) => RemoveInstance(instance.Id);
        info.Children.Add(remove);

        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        return grid;
    }

    private void UpdateInstance(string id, PetInstancePatch patch)
    {
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        store = PetStoreModel.UpdatePetInstance(store, id, patch);
        _store.SavePetStore(store);
        _manager.Reconcile(store, _manager.GloballyVisible);
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

        var theme = new WrapPanel();
        foreach (var (value, label) in new[] { ("system", "跟随系统"), ("light", "浅色"), ("dark", "深色") })
        {
            var radio = new RadioButton { Content = label, IsChecked = _settings.Theme == value, Margin = new Thickness(0, 0, 16, 0) };
            radio.Checked += (_, _) => Save(s => s with { Theme = value });
            theme.Children.Add(radio);
        }
        stack.Children.Add(Card(Stacked("主题", theme)));

        var opacity = new Slider { Minimum = 30, Maximum = 100, Value = _settings.BubbleOpacity, Width = 220 };
        var opacityValue = new TextBlock { Text = $"{_settings.BubbleOpacity}%", Margin = new Thickness(10, 0, 0, 0) };
        opacity.ValueChanged += (_, e) =>
        {
            opacityValue.Text = $"{e.NewValue:0}%";
            Save(s => s with { BubbleOpacity = (int)e.NewValue });
        };
        stack.Children.Add(Card(Stacked("气泡不透明度", Row(opacity, opacityValue))));

        var size = new Slider { Minimum = 70, Maximum = 130, Value = _settings.PetSizePercent, Width = 220 };
        var sizeValue = new TextBlock { Text = $"{_settings.PetSizePercent}%", Margin = new Thickness(10, 0, 0, 0) };
        size.ValueChanged += (_, e) =>
        {
            sizeValue.Text = $"{e.NewValue:0}%";
            Save(s => s with { PetSizePercent = (int)e.NewValue });
        };
        stack.Children.Add(Card(Stacked("宠物尺寸", Row(size, sizeValue))));

        var idle = new CheckBox { Content = "显示闲谈气泡", IsChecked = _settings.ShowIdleChatter, VerticalAlignment = VerticalAlignment.Center };
        idle.Checked += (_, _) => Save(s => s with { ShowIdleChatter = true });
        idle.Unchecked += (_, _) => Save(s => s with { ShowIdleChatter = false });
        stack.Children.Add(Card(idle));

        var bob = new CheckBox { Content = "待机浮动动画", IsChecked = _settings.BobAnimation, VerticalAlignment = VerticalAlignment.Center };
        bob.Checked += (_, _) => Save(s => s with { BobAnimation = true });
        bob.Unchecked += (_, _) => Save(s => s with { BobAnimation = false });
        stack.Children.Add(Card(bob));

        return PageScroller(stack);
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
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        presets.LostFocus += (_, _) => Save(s => s with
        {
            QuickBubblePresets = presets.Text.Split('\n')
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(),
        });
        stack.Children.Add(Card(Stacked("预设气泡池（每行一条）", presets)));

        var duration = new Slider { Minimum = 1, Maximum = 10, Value = _settings.QuickBubbleDurationSeconds, Width = 220 };
        var durationValue = new TextBlock { Text = $"{_settings.QuickBubbleDurationSeconds}s", Margin = new Thickness(10, 0, 0, 0) };
        duration.ValueChanged += (_, e) =>
        {
            durationValue.Text = $"{e.NewValue:0}s";
            Save(s => s with { QuickBubbleDurationSeconds = (int)e.NewValue });
        };
        stack.Children.Add(Card(Stacked("气泡显示时长", Row(duration, durationValue))));

        var click = new WrapPanel();
        foreach (var (value, label) in new[] { ("none", "无动作"), ("self", "单只随机说"), ("all", "全员随机说") })
        {
            var radio = new RadioButton { Content = label, IsChecked = _settings.LeftClickAction == value, Margin = new Thickness(0, 0, 16, 0) };
            radio.Checked += (_, _) => Save(s => s with { LeftClickAction = value });
            click.Children.Add(radio);
        }
        stack.Children.Add(Card(Stacked("点击宠物", click)));

        return PageScroller(stack);
    }

    // ---- 漫游页 ----

    private UIElement BuildRoamPage()
    {
        var stack = new StackPanel();
        var roam = _settings.Roam;

        var enabled = new CheckBox { Content = "启用漫游", IsChecked = roam.Enabled, VerticalAlignment = VerticalAlignment.Center };
        enabled.Checked += (_, _) => SaveRoam(roam with { Enabled = true });
        enabled.Unchecked += (_, _) => SaveRoam(roam with { Enabled = false });
        stack.Children.Add(Card(enabled));

        var mode = new WrapPanel();
        foreach (var (value, label) in new[]
        {
            (RoamMode.Stay, "Stay"), (RoamMode.Wander, "Wander"),
            (RoamMode.Cursor, "Follow cursor"), (RoamMode.Climb, "Climb windows"),
        })
        {
            var radio = new RadioButton { Content = label, IsChecked = roam.Mode == value, Margin = new Thickness(0, 0, 16, 0) };
            radio.Checked += (_, _) => SaveRoam(roam with { Mode = value });
            mode.Children.Add(radio);
        }
        stack.Children.Add(Card(Stacked("漫游模式", mode)));

        var speed = new Slider { Minimum = 1, Maximum = 10, Value = roam.Speed, Width = 220 };
        var speedValue = new TextBlock { Text = roam.Speed.ToString(), Margin = new Thickness(10, 0, 0, 0) };
        speed.ValueChanged += (_, e) =>
        {
            speedValue.Text = $"{e.NewValue:0}";
            SaveRoam(roam with { Speed = (int)e.NewValue });
        };
        stack.Children.Add(Card(Stacked("漫游速度", Row(speed, speedValue))));

        return PageScroller(stack);
    }

    // ---- 语言页 ----

    private UIElement BuildLanguagePage()
    {
        var stack = new StackPanel();
        var lang = new WrapPanel();
        foreach (var (value, label) in new[]
        {
            (AppLang.En, "English"), (AppLang.ZhHans, "简体中文"),
            (AppLang.ZhHant, "繁體中文"), (AppLang.Vi, "Tiếng Việt"),
        })
        {
            var radio = new RadioButton { Content = label, IsChecked = _settings.Lang == value, Margin = new Thickness(0, 0, 16, 0) };
            radio.Checked += (_, _) => Save(s => s with { Lang = value });
            lang.Children.Add(radio);
        }
        stack.Children.Add(Card(Stacked("语言 / Language", lang)));
        return PageScroller(stack);
    }

    // ---- 关于页 ----

    private UIElement BuildAboutPage()
    {
        var stack = new StackPanel();
        var store = _store.LoadPetStore() ?? PetStoreModel.EmptyPetStore();
        var care = _store.LoadCare();
        var totalXp = care.Values.Sum(s => s.Xp);

        var version = new TextBlock
        {
            Text = $"DesktopPet Native\n版本 {typeof(SettingsWindow).Assembly.GetName().Version}\n.NET 8 + WPF · Lumen",
            FontSize = 12,
            LineHeight = 20,
        };
        stack.Children.Add(Card(version));

        var stats = new TextBlock
        {
            Text = $"宠物数量：{store.Instances.Count}\n总养成 XP：{totalXp:0}\n数据目录：{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\DesktopPet",
            FontSize = 12,
            LineHeight = 20,
        };
        stack.Children.Add(Card(stats));
        return PageScroller(stack);
    }

    // ---- 帮助 ----

    private static StackPanel Stacked(string label, UIElement content)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(content);
        return stack;
    }

    private static Grid Row(params UIElement[] elements)
    {
        var grid = new Grid();
        foreach (var element in elements) grid.Children.Add(element);
        return grid;
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
            Content = "启用 AI（开启后启动后台分析进程）",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            IsChecked = ai.Enabled,
        };
        masterToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { Enabled = masterToggle.IsChecked == true } });
        stack.Children.Add(Card(new StackPanel
        {
            Children =
            {
                masterToggle,
                new TextBlock
                {
                    Text = "关闭 = 纯桌宠模式：无截屏、无网络调用、无后台进程",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
                    Margin = new Thickness(0, 4, 0, 0),
                },
            },
        }));

        // 模型连接（AI 第一配置项：小白用户打开 AI 页即见，不用滚动）
        var providers = _ai?.Providers ?? new Core.Scheduling.ProvidersFileModel();
        var providerPanel = new StackPanel();
        providerPanel.Children.Add(new TextBlock
        {
            Text = "模型连接（OpenAI 兼容：云端 / 本地 Ollama 通吃）",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        if (providers.Models.Count == 0)
        {
            providerPanel.Children.Add(new TextBlock
            {
                Text = "未配置模型连接。对话不可用；屏幕分析仅做变化检测（无评论）。",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
                TextWrapping = TextWrapping.Wrap,
            });
            var emptyEditButton = new Button
            {
                Content = "✏ 配置模型连接",
                Width = 140,
                Height = 28,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
            };
            emptyEditButton.Click += (_, _) => ShowModelConnectionEditor();
            providerPanel.Children.Add(emptyEditButton);
        }
        else
        {
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
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
            // 模型下拉 + 编辑按钮同行（小白用户：不用滚动就能看到配置入口）
            var comboRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            combo.Width = 300;
            comboRow.Children.Add(combo);
            var editConnButton = new Button
            {
                Content = "✏ 编辑",
                Width = 64,
                Height = 26,
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            editConnButton.Click += (_, _) => ShowModelConnectionEditor();
            comboRow.Children.Add(editConnButton);
            providerPanel.Children.Add(comboRow);
            providerPanel.Children.Add(new TextBlock
            {
                Text = selectedProvider.BaseUrl + "　模型连接可在设置中直接修改（接口地址/模型/Key）",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA0)),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Phase 6f：生图连接（总结图）+ 日记查看入口
        var extraRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var imageConnButton = new Button
        {
            Content = "⚙ 生图连接" + (providers.Image is null ? "（未配置）" : "（已配置）"),
            Width = 160,
            Height = 28,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
        };
        imageConnButton.Click += (_, _) => ShowImageConnectionEditor();
        extraRow.Children.Add(imageConnButton);
        var diaryButton = new Button { Content = "📔 日记", Width = 100, Height = 28, FontSize = 12 };
        diaryButton.Click += (_, _) => ShowDiaryViewer();
        extraRow.Children.Add(diaryButton);
        providerPanel.Children.Add(extraRow);
        stack.Children.Add(Card(providerPanel));

        // 分析开关
        var analysisToggle = new CheckBox
        {
            Content = "屏幕分析（感知你在做什么）",
            IsChecked = ai.ScreenAnalysis,
        };
        analysisToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { ScreenAnalysis = analysisToggle.IsChecked == true } });
        stack.Children.Add(Card(analysisToggle));

        // 输出模式三选一
        var modePanel = new StackPanel();
        modePanel.Children.Add(new TextBlock { Text = "AI 主动输出模式", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var (id, name, desc) in new[]
        {
            ("bubble", "气泡", "宠物头上气泡文字（默认，不打断工作）"),
            ("danmaku", "弹幕", "全屏滚动弹幕（Win2D GPU）"),
            ("chat", "对话", "回复出现在对话气泡窗口"),
            ("silent", "静默", "无主动输出，仅应答对话"),
        })
        {
            var radio = new RadioButton
            {
                Content = name + " — " + desc,
                GroupName = "output-mode",
                IsChecked = ai.OutputMode == id,
                Margin = new Thickness(0, 4, 0, 0),
            };
            radio.Click += (_, _) => Save(s => s with { Ai = s.Ai with { OutputMode = id } });
            modePanel.Children.Add(radio);
        }
        stack.Children.Add(Card(modePanel));

        // 屏幕上下文开关（对话携带最近屏幕事件，隐私默认关）
        var contextToggle = new CheckBox
        {
            Content = "对话携带屏幕上下文（默认关：开启后对话请求才包含屏幕描述）",
            IsChecked = ai.ScreenContextEnabled,
            Margin = new Thickness(0, 0, 0, 4),
        };
        contextToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { ScreenContextEnabled = contextToggle.IsChecked == true } });
        stack.Children.Add(Card(contextToggle));

        // ---- Phase 6：陪伴功能开关组（AI 总开关 → 各功能独立开关）----
        stack.Children.Add(Card(new TextBlock
        {
            Text = "陪伴功能（记忆 / 主动互动 / 亲密度 / 每日总结 / 语音）",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        }));

        // 记忆开关（默认开）
        var memoryToggle = new CheckBox
        {
            Content = "记忆（记住你的称呼/作息/话题；关 = 不记录不注入）",
            IsChecked = ai.MemoryEnabled,
        };
        memoryToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { MemoryEnabled = memoryToggle.IsChecked == true } });
        stack.Children.Add(Card(memoryToggle));

        // 主动互动开关 + 频率档
        var interactionPanel = new StackPanel();
        var interactionToggle = new CheckBox
        {
            Content = "主动互动（定时问候 + 屏幕事件评论）",
            IsChecked = ai.ActiveInteraction,
        };
        interactionToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { ActiveInteraction = interactionToggle.IsChecked == true } });
        interactionPanel.Children.Add(interactionToggle);
        var freqRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 6, 0, 0) };
        freqRow.Children.Add(new TextBlock { Text = "频率：", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        foreach (var (id, name) in new[] { ("low", "少"), ("medium", "中"), ("high", "多") })
        {
            var radio = new RadioButton
            {
                Content = name,
                GroupName = "interaction-frequency",
                IsChecked = ai.InteractionFrequency == id,
                FontSize = 12,
                Margin = new Thickness(0, 0, 12, 0),
            };
            radio.Click += (_, _) =>
                Save(s => s with { Ai = s.Ai with { InteractionFrequency = id } });
            freqRow.Children.Add(radio);
        }
        interactionPanel.Children.Add(freqRow);
        var allReplyToggle = new CheckBox
        {
            Content = "全员回应（同一事件每只宠物都发表评论，并行生成）",
            IsChecked = ai.AllReply,
            Margin = new Thickness(24, 6, 0, 0),
        };
        allReplyToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { AllReply = allReplyToggle.IsChecked == true } });
        interactionPanel.Children.Add(allReplyToggle);
        stack.Children.Add(Card(interactionPanel));

        // 屏幕感知开关（默认开）
        var awarenessToggle = new CheckBox
        {
            Content = "屏幕感知（从截屏推断你在做什么；关 = 仅定时问候）",
            IsChecked = ai.ScreenAwareness,
        };
        awarenessToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { ScreenAwareness = awarenessToggle.IsChecked == true } });
        stack.Children.Add(Card(awarenessToggle));

        // 亲密度开关（默认开）
        var intimacyToggle = new CheckBox
        {
            Content = "亲密度（随互动成长，称呼/语气分档；关 = 固定人格基础档）",
            IsChecked = ai.IntimacyEnabled,
        };
        intimacyToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { IntimacyEnabled = intimacyToggle.IsChecked == true } });
        stack.Children.Add(Card(intimacyToggle));

        // 每日总结开关（默认开）
        var summaryToggle = new CheckBox
        {
            Content = "每日总结（每天结束生成\"你的一天\"日记）",
            IsChecked = ai.DailySummary,
        };
        summaryToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { DailySummary = summaryToggle.IsChecked == true } });
        stack.Children.Add(Card(summaryToggle));

        // 总结图开关（默认关：云端费用+隐私）
        var imageToggle = new CheckBox
        {
            Content = "总结图（默认关：用生图模型给日记配插图，需配置生图连接）",
            IsChecked = ai.SummaryImage,
        };
        imageToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { SummaryImage = imageToggle.IsChecked == true } });
        stack.Children.Add(Card(imageToggle));

        // 语音朗读开关（默认关：不打扰）
        var ttsToggle = new CheckBox
        {
            Content = "语音朗读（对话模式朗读回复，Edge TTS；弹幕模式不朗读）",
            IsChecked = ai.TtsEnabled,
        };
        ttsToggle.Click += (_, _) =>
            Save(s => s with { Ai = s.Ai with { TtsEnabled = ttsToggle.IsChecked == true } });
        stack.Children.Add(Card(ttsToggle));

        // 人格卡片网格
        var personaPanel = new StackPanel();
        personaPanel.Children.Add(new TextBlock
        {
            Text = "人格（影响所有 AI 输出）",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var grid = new WrapPanel();
        foreach (var persona in personas.MergeWithBuiltins())
        {
            var selected = persona.Id == personas.SelectedId;
            var card = new Border
            {
                Background = new SolidColorBrush(selected
                    ? Color.FromRgb(0xFF, 0xE8, 0xDF)
                    : Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(selected
                    ? Color.FromRgb(0xFF, 0x8A, 0x65)
                    : Color.FromArgb(0x14, 0x1C, 0x20, 0x28)),
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Width = 150,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock
            {
                Text = persona.Name + (persona.Builtin ? "" : " ✎"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            });
            inner.Children.Add(new TextBlock
            {
                Text = persona.Description,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
                TextWrapping = TextWrapping.Wrap,
            });
            if (!persona.Builtin)
            {
                var editLink = new TextBlock
                {
                    Text = "✎ 编辑",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 4, 0, 0),
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
        var newPersonaButton = new Button { Content = "＋ 新建人格", Width = 100, Height = 28, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
        newPersonaButton.Click += (_, _) => ShowPersonaEditor(null);
        manageRow.Children.Add(newPersonaButton);
        var editHint = new TextBlock
        {
            Text = "点击自定义人格的 ✎ 可编辑（内置人格编辑会复制为自定义）",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        manageRow.Children.Add(editHint);
        personaPanel.Children.Add(manageRow);
        stack.Children.Add(Card(personaPanel));

        return PageScroller(stack);
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        _settings = AppSettings.Normalize(change(_settings));
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

        var nameBox = new TextBox { Text = existing?.Name ?? "", FontSize = 12, Height = 24 };
        var descBox = new TextBox { Text = existing?.Description ?? "", FontSize = 12, Height = 24 };
        var promptBox = new TextBox
        {
            Text = existing?.Prompt ?? "",
            FontSize = 12,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var exampleBox = new TextBox
        {
            Text = existing?.ExampleDialogs is { Length: > 0 } ? string.Join("\n", existing.ExampleDialogs) : "",
            FontSize = 12,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 70,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var form = new StackPanel { Margin = new Thickness(16) };
        form.Children.Add(new TextBlock { Text = "人格名称（必填，≤12 字）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)) });
        form.Children.Add(nameBox);
        form.Children.Add(new TextBlock { Text = "一句话描述（选填）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(descBox);
        form.Children.Add(new TextBlock { Text = "人格提示词（必填，决定性格）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(promptBox);
        form.Children.Add(new TextBlock
        {
            Text = "示例对话（选填，每行一段：\"用户：…\" 和 \"宠物：…\" 成对写，风格示例 > 描述）",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        form.Children.Add(exampleBox);

        var window = new Window
        {
            Title = isBuiltinEdit ? "复制内置人格并编辑" : (existing is null ? "新建人格" : "编辑人格"),
            Width = 420,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
        };
        var saveButton = new Button { Content = "保存", Width = 100, Height = 30, FontSize = 12 };
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
        form.Children.Add(new TextBlock { Text = " ", FontSize = 4 });
        form.Children.Add(saveButton);
        window.Content = form;
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

        var baseBox = new TextBox { Text = cfg?.BaseUrl ?? "", FontSize = 12, Height = 24 };
        var modelBox = new TextBox { Text = cfg?.ModelName ?? "", FontSize = 12, Height = 24 };
        var keyBox = new TextBox { Text = string.IsNullOrEmpty(cfg?.ApiKeyRef) ? "" : "（已配置，留空不修改）", FontSize = 12, Height = 24 };
        var maxTokensBox = new TextBox { Text = cfg?.MaxOutputTokens?.ToString() ?? "", FontSize = 12, Height = 24 };
        var contextBox = new TextBox { Text = cfg?.ContextWindowTokens?.ToString() ?? "", FontSize = 12, Height = 24 };
        var reasoningCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
        reasoningCombo.Items.Add("关闭思考（推荐，响应更快）");
        reasoningCombo.Items.Add("跟随模型默认");
        reasoningCombo.SelectedIndex = string.IsNullOrEmpty(cfg?.ReasoningEffort) ? 1 : 0;

        var form = new StackPanel { Margin = new Thickness(16) };
        form.Children.Add(new TextBlock { Text = "接口地址（OpenAI 兼容，如 https://api.openai.com/v1）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)) });
        form.Children.Add(baseBox);
        form.Children.Add(new TextBlock { Text = "模型名（如 gpt-4o / sensenova-6.7-flash-lite）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(modelBox);
        form.Children.Add(new TextBlock { Text = "API Key（存 Windows 凭据管理器，不落明文）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(keyBox);
        form.Children.Add(new TextBlock { Text = "最大输出 token（留空 = 短句默认；国产模型一般不用填）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(maxTokensBox);
        form.Children.Add(new TextBlock { Text = "上下文长度 token（留空 = 会话记住最近 5 轮；如 256000）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(contextBox);
        form.Children.Add(new TextBlock { Text = "思考模式", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(reasoningCombo);
        form.Children.Add(new TextBlock { Text = " ", FontSize = 4 });

        var saveButton = new Button { Content = "保存模型连接", Width = 120, Height = 30, FontSize = 12 };
        var window = new Window
        {
            Title = "模型连接",
            Width = 420,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
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
        form.Children.Add(saveButton);
        window.Content = form;
        window.ShowDialog();
    }

    // ---- Phase 6f：生图连接（providers.json image 段）----

    private void ShowImageConnectionEditor()
    {
        if (_ai is null) return;
        var providers = _ai.Providers;
        var cfg = providers.Image;
        var creds = new Infra.Providers.WindowsCredentialStore();

        var baseBox = new TextBox { Text = cfg?.BaseUrl ?? "", FontSize = 12, Height = 24 };
        var modelBox = new TextBox { Text = cfg?.ModelName ?? "", FontSize = 12, Height = 24 };
        var keyBox = new TextBox { Text = string.IsNullOrEmpty(cfg?.ApiKeyRef) ? "" : "（已配置，留空不修改）", FontSize = 12, Height = 24 };

        var form = new StackPanel { Margin = new Thickness(16) };
        form.Children.Add(new TextBlock { Text = "生图 BaseUrl（OpenAI 兼容，如 https://api.openai.com/v1）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)) });
        form.Children.Add(baseBox);
        form.Children.Add(new TextBlock { Text = "生图模型（如 gpt-image-1 / qwen-image）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(modelBox);
        form.Children.Add(new TextBlock { Text = "API Key（存 Windows 凭据管理器，不落明文 JSON）", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)), Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(keyBox);
        form.Children.Add(new TextBlock { Text = " ", FontSize = 4 });

        var saveButton = new Button { Content = "保存生图连接", Width = 120, Height = 30, FontSize = 12 };
        var window = new Window
        {
            Title = "生图连接（总结图）",
            Width = 420,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
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
        form.Children.Add(saveButton);
        window.Content = form;
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

        var form = new StackPanel { Margin = new Thickness(16) };
        if (files.Count == 0)
        {
            form.Children.Add(new TextBlock
            {
                Text = "还没有日记。每天结束时自动生成\"你的一天\"（需开启每日总结 + 模型连接）。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
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
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 0, 0, 8),
                };
                var inner = new StackPanel();
                inner.Children.Add(new TextBlock { Text = day, FontSize = 12, FontWeight = FontWeights.SemiBold });
                try
                {
                    var text = File.ReadAllText(file);
                    inner.Children.Add(new TextBlock
                    {
                        Text = text.Length > 200 ? text[..200] + "…" : text,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
                        Margin = new Thickness(0, 4, 0, 0),
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
            Width = 420,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
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
        base.OnClosed(e);
    }
}

/// <summary>宠物卡片实时动画预览（共享 timer 驱动，对齐"128px 实时动画预览"）。</summary>
public sealed class PetPreviewCard : FrameworkElement
{
    private readonly Image _image = new();
    private readonly WriteableBitmap? _bitmap;
    private PetRenderer? _renderer;

    public PetPreviewCard(SpriteLoader loader, string slug)
    {
        Width = 96;
        Height = 96;
        var sheet = loader.TryGetCached(slug);
        if (sheet is null) return;

        _renderer = new PetRenderer(sheet);
        _renderer.SetState("idle");
        _bitmap = new WriteableBitmap(96, 96, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
        _image.Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        AddVisualChild(_image);
        AddLogicalChild(_image);
        Draw();
    }

    public void Advance()
    {
        _renderer?.AdvanceFrame();
        Draw();
    }

    private void Draw()
    {
        if (_bitmap is null || _renderer is null) return;
        var buffer = new byte[96 * 96 * 4];
        _renderer.DrawFrame(buffer, 96, 96);
        PixelBuffer.RgbaToBgra(buffer); // Core 输出 RGBA，WriteableBitmap 是 Bgra32
        _bitmap.WritePixels(new Int32Rect(0, 0, 96, 96), buffer, 96 * 4, 0);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _image;
}

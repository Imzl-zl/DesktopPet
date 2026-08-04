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

    public SettingsWindow(IJsonStore store, PetWindowManager manager, SpriteLoader spriteLoader, I18nService i18n)
    {
        _store = store;
        _manager = manager;
        _spriteLoader = spriteLoader;
        _i18n = i18n;
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
            ("roam", "漫游"), ("language", "语言"), ("about", "关于"),
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

    private void ShowPage(string id)
    {
        _currentPage = id;
        _contentHost.Content = id switch
        {
            "pets" => BuildPetsPage(),
            "appearance" => BuildAppearancePage(),
            "bubble" => BuildBubblePage(),
            "roam" => BuildRoamPage(),
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

    private void Save(Func<AppSettings, AppSettings> change)
    {
        _settings = AppSettings.Normalize(change(_settings));
        _store.SaveSettings(_settings);
        _manager.ApplySettings(_settings);
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
        _bitmap.WritePixels(new Int32Rect(0, 0, 96, 96), buffer, 96 * 4, 0);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _image;
}

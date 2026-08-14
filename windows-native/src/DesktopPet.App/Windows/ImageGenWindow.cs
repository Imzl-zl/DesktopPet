using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Ai;
using DesktopPet.App.Localization;
using DesktopPet.Core.I18n;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;
using DesktopPet.Infra.Storage;

namespace DesktopPet.App.Windows;

/// <summary>
/// 生图页（阶段 5，windows-imagegen-design.md §7）：连接×模型选择 + 提示词 + 参数面板
/// （按模型能力动态渲染）+ 生成（门面异步任务，张数循环）+ 历史画廊（落盘 gallery/）。
/// 生成参数以 ImageGenSpec 直构交给 AiCoordinator.GenerateImageAsync（透明按能力自动分流）。
/// </summary>
public sealed class ImageGenWindow : Window
{
    private readonly AiCoordinator _ai;
    private readonly GalleryStore _gallery;
    private readonly I18nService _i18n;
    private readonly ImageModelCatalog _catalog;
    private static readonly Lazy<ImageChannelCatalog> ChannelCatalog = new(ImageChannelCatalog.LoadBuiltIn);

    // 生成状态
    private CancellationTokenSource? _generationCts;
    private bool _generating;

    // 选中目标
    private ImageConnection? _selectedConnection;
    private string _selectedModelId = "";

    // 参数控件（模型切换时按能力重建）
    private readonly ContentControl _paramsHost = new();
    private ComboBox _ratioCombo = null!;
    private ComboBox _scaleCombo = null!;
    private ComboBox _qualityCombo = null!;
    private CheckBox _transparentToggle = null!;
    private CheckBox _seedToggle = null!;
    private TextBox _seedBox = null!;

    private readonly TextBox _promptBox;
    private readonly TextBlock _promptCount;
    private readonly ComboBox _modelCombo;
    private readonly ComboBox _countCombo;
    private readonly Button _generateButton;
    private readonly Button _cancelButton;
    private readonly TextBlock _status;
    private readonly ComboBox _qualityPlaceholder;

    // v2：图生图参考图（文件/URL）+ 固定尺寸表模式
    private readonly List<ReferenceImage> _imageRefs = [];
    private WrapPanel _refPanel = null!;
    private TextBox _refUrlBox = null!;
    private TextBlock _refCount = null!;
    private Button _pickRefButton = null!;
    private Button _addRefButton = null!;
    private ComboBox _sizeTableCombo = null!;

    // 画廊
    private readonly WrapPanel _thumbPanel;
    private readonly Image _previewImage;
    private readonly TextBlock _previewMeta;
    private readonly Button _deleteButton;
    private GalleryEntry? _previewEntry;

    public ImageGenWindow(AiCoordinator ai, GalleryStore gallery, I18nService i18n)
    {
        _ai = ai;
        _gallery = gallery;
        _i18n = i18n;
        _catalog = ImageModelCatalog.LoadBuiltIn();
        Icon = AppIcons.WindowIcon();
        Title = "生图";
        Width = 960;
        Height = 720;
        MinWidth = 820;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBgBrush");

        // ---- 模型选择 ----
        var modelLabel = new TextBlock
        {
            Text = "模型",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 5),
        };
        _modelCombo = new ComboBox { FontSize = 13, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        _modelCombo.SelectionChanged += (_, _) => OnModelChanged();
        var modelRow = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        modelRow.Children.Add(modelLabel);
        modelRow.Children.Add(_modelCombo);

        // ---- 提示词 ----
        var promptLabel = new TextBlock
        {
            Text = "提示词",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
        };
        _promptCount = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var promptHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        promptHeader.Children.Add(promptLabel);
        promptHeader.Children.Add(_promptCount);
        _promptBox = new TextBox
        {
            FontSize = 13,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 6, 8, 6),
        };
        _promptBox.TextChanged += (_, _) => _promptCount.Text = $"{_promptBox.Text.Length}/2000";
        _promptCount.Text = "0/2000"; // 初始空文本不触发 TextChanged，显式初始化计数显示

        // ---- 参数面板 ----
        var paramsCard = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        paramsCard.Children.Add(new TextBlock
        {
            Text = "参数",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        paramsCard.Children.Add(_paramsHost);

        // 张数（与能力无关，放参数卡片下方）
        _countCombo = new ComboBox { FontSize = 12, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 1; i <= 4; i++) _countCombo.Items.Add(i + " 张");
        _countCombo.SelectedIndex = 0;
        var countRow = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        countRow.Children.Add(new TextBlock
        {
            Text = "张数",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 0, 0, 3),
        });
        countRow.Children.Add(_countCombo);
        paramsCard.Children.Add(countRow);

        // ---- 操作 ----
        _generateButton = new Button
        {
            Content = "生成",
            Style = AppStyle("ButtonPrimaryStyle"),
            Width = 140,
            Height = 36,
            FontSize = 14,
            Margin = new Thickness(0, 14, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _generateButton.Click += async (_, _) => await GenerateAsync();
        _cancelButton = new Button
        {
            Content = "取消",
            Style = AppStyle("ButtonDefaultStyle"),
            Width = 90,
            Height = 36,
            FontSize = 13,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false,
        };
        _cancelButton.Click += (_, _) => _generationCts?.Cancel();
        _status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(12, 14, 0, 0),
        };
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        actionRow.Children.Add(_generateButton);
        actionRow.Children.Add(_cancelButton);
        actionRow.Children.Add(_status);

        // ---- 预览 ----
        _previewImage = new Image
        {
            MaxWidth = 520,
            MaxHeight = 320,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 10, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _previewMeta = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        _deleteButton = new Button
        {
            Content = "从画廊删除",
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 28,
            FontSize = 12,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false,
        };
        _deleteButton.Click += async (_, _) => await DeletePreviewAsync();
        var previewPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        previewPanel.Children.Add(new TextBlock
        {
            Text = "预览",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
        });
        previewPanel.Children.Add(_previewImage);
        previewPanel.Children.Add(_previewMeta);
        previewPanel.Children.Add(_deleteButton);

        // ---- 历史画廊 ----
        _thumbPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        var galleryScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 170,
            Margin = new Thickness(0, 6, 0, 0),
            Content = _thumbPanel,
        };
        var galleryPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        galleryPanel.Children.Add(new TextBlock
        {
            Text = "历史画廊（本地保存 %APPDATA%/DesktopPet/gallery/）",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
        });
        galleryPanel.Children.Add(galleryScroll);

        // ---- 布局 ----
        var content = new ScrollViewer
        {
            Padding = new Thickness(28, 20, 28, 24),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var stack = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };
        stack.Children.Add(new TextBlock
        {
            Text = "生图",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "用你的生图连接生成图片；透明背景自动走原生直传或绿幕键控管线",
            FontSize = 12,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 3, 0, 16),
        });
        stack.Children.Add(modelRow);
        stack.Children.Add(promptHeader);
        stack.Children.Add(_promptBox);
        stack.Children.Add(paramsCard);
        stack.Children.Add(actionRow);
        stack.Children.Add(previewPanel);
        stack.Children.Add(galleryPanel);
        content.Content = stack;
        Content = content;

        _qualityPlaceholder = new ComboBox(); // 供 RebuildParams 占位（避免 null 解引用路径）
        RebuildParams();
        LoadModelOptions();
        RefreshGallery();
        Closed += (_, _) => _generationCts?.Cancel();
    }

    // ---- 模型选择与参数面板 ----

    private void LoadModelOptions()
    {
        _modelCombo.Items.Clear();
        var connections = _ai.Providers.Image?.Connections ?? [];
        foreach (var connection in connections)
        {
            if (string.IsNullOrWhiteSpace(connection.BaseUrl)) continue;
            var modelIds = connection.Models.Count > 0
                ? connection.Models
                : _catalog.ForFamily(connection.Family).Select(m => m.Id).ToList();
            foreach (var modelId in modelIds)
            {
                var descriptor = ResolveModel(connection, modelId);
                var price = string.IsNullOrEmpty(descriptor.PriceHint) ? "" : $" · {descriptor.PriceHint}";
                _modelCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{connection.Name} · {descriptor.Name}（{modelId}{price}）",
                    Tag = (connection, modelId),
                });
            }
        }
        if (_modelCombo.Items.Count == 0)
        {
            _generateButton.IsEnabled = false;
            _status.Text = "未配置生图连接，请先到设置 → AI 助手 → 生图连接添加";
            _status.Foreground = Brush("DangerBrush");
            return;
        }
        _modelCombo.SelectedIndex = 0;
    }

    private void OnModelChanged()
    {
        if (_modelCombo.SelectedItem is not ComboBoxItem { Tag: (ImageConnection connection, string modelId) })
            return;
        _selectedConnection = connection;
        _selectedModelId = modelId;
        _status.Text = "";
        RebuildParams();
    }

    /// <summary>按当前模型能力重建参数面板（尺寸表/宽高比+档位、质量、seed、透明、参考图）。</summary>
    private void RebuildParams()
    {
        var panel = new StackPanel();
        var capabilities = _selectedConnection is null
            ? null
            : CapabilitiesFor(_selectedConnection, _selectedModelId);

        if (capabilities is null)
        {
            _paramsHost.Content = panel;
            return;
        }

        var hasFixedSizes = capabilities.FixedSizes is { Count: > 0 };

        // 尺寸控件：固定尺寸表 → 单个「尺寸」下拉（比例由表推导）；否则 宽高比 + 分辨率双下拉
        if (hasFixedSizes)
        {
            _sizeTableCombo = new ComboBox { FontSize = 12, Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var size in capabilities.FixedSizes)
            {
                var label = size;
                if (ImageSizeTable.TryParse(size, out var w, out var h)
                    && ImageAspectRatioParser.TryFromPixels(w, h, out var ratio))
                {
                    label = $"{w}×{h}（{ImageAspectRatioParser.ToDisplay(ratio)}）";
                }
                _sizeTableCombo.Items.Add(new ComboBoxItem { Content = label, Tag = size });
            }
            _sizeTableCombo.SelectedIndex = 0;
        }
        else
        {
            _ratioCombo = new ComboBox { FontSize = 12, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var ratio in capabilities.AspectRatios)
            {
                _ratioCombo.Items.Add(ImageAspectRatioParser.ToDisplay(ratio));
            }
            _ratioCombo.SelectedIndex = 0;
            _scaleCombo = new ComboBox { FontSize = 12, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var scale in capabilities.Scales)
            {
                _scaleCombo.Items.Add(ImageScaleParser.ToDisplay(scale));
            }
            _scaleCombo.SelectedIndex = 0;
        }

        // 质量：能力关闭（SenseNova 等）时隐藏；Google 族忽略 quality 参数
        _qualityCombo = _qualityPlaceholder;
        var openaiFamily = _selectedConnection is not null
            && string.Equals(_selectedConnection.Family, ImageModelCatalog.FamilyOpenAi, StringComparison.OrdinalIgnoreCase);
        if (openaiFamily && capabilities.QualityLevels)
        {
            _qualityCombo = new ComboBox { FontSize = 12, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var (value, label) in new[]
            {
                ("auto", "自动"), ("low", "低"), ("medium", "中"), ("high", "高"),
            })
            {
                _qualityCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            }
            _qualityCombo.SelectedIndex = 0;
        }

        _transparentToggle = new CheckBox
        {
            Content = "透明背景（精灵图）",
            FontSize = 12,
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _seedToggle = new CheckBox
        {
            Content = "固定种子",
            FontSize = 12,
            IsChecked = false,
            IsEnabled = capabilities.Seed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _seedBox = new TextBox
        {
            FontSize = 12,
            Width = 90,
            Height = 26,
            IsEnabled = false,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _seedToggle.Click += (_, _) => _seedBox.IsEnabled = _seedToggle.IsChecked == true;

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var columnCount = hasFixedSizes ? 5 : 7;
        for (var i = 0; i < columnCount; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        void Add(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            if (element is FrameworkElement fe and not TextBlock)
            {
                // 非标签控件统一右间距 16（标签自带 6DIP 右边距）——防列间紧贴
                fe.Margin = new Thickness(0, 0, 16, 0);
            }
            grid.Children.Add(element);
        }
        if (hasFixedSizes)
        {
            Add(Label("尺寸"), 0);
            Add(_sizeTableCombo, 1);
            Add(Label("质量"), 2);
            Add(_qualityCombo, 3);
            Add(_transparentToggle, 4);
        }
        else
        {
            Add(Label("宽高比"), 0);
            Add(_ratioCombo, 1);
            Add(Label("分辨率"), 2);
            Add(_scaleCombo, 3);
            Add(Label("质量"), 4);
            Add(_qualityCombo, 5);
            Add(_transparentToggle, 6);
        }

        // seed 单独一行（能力支持时才可勾选；输入框随勾选启用）
        var seedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _seedBox.Margin = new Thickness(8, 0, 0, 0);
        seedRow.Children.Add(_seedToggle);
        seedRow.Children.Add(_seedBox);
        panel.Children.Add(grid);
        panel.Children.Add(seedRow);

        // v2：参考图区（能力声明图生图时显示；上限 = MaxReferenceImages）
        if (capabilities.Editing && capabilities.MaxReferenceImages > 0)
        {
            panel.Children.Add(BuildRefsSection(capabilities.MaxReferenceImages));
        }

        _paramsHost.Content = panel;

        // 透明提示（绿幕模型）
        if (capabilities.NativeTransparency == false)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "该模型无原生透明：透明开启时自动用「纯绿背景 + 本地键控」管线生成",
                FontSize = 11,
                Foreground = Brush("TextTertiaryBrush"),
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    // ── v2：参考图（图生图/编辑）──

    private UIElement BuildRefsSection(int maxRefs)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 4) };
        header.Children.Add(new TextBlock
        {
            Text = "参考图（图生图/编辑）",
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _refCount = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(_refCount);
        if (_imageRefs.Count > 0)
        {
            var clear = new Button
            {
                Content = "清空",
                Style = AppStyle("ButtonDefaultStyle"),
                Height = 22,
                FontSize = 11,
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(8, 0, 0, 0),
            };
            clear.Click += (_, _) => { _imageRefs.Clear(); RenderRefs(maxRefs); };
            header.Children.Add(clear);
        }

        _pickRefButton = new Button
        {
            Content = "选择文件…",
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 26,
            FontSize = 11,
            Padding = new Thickness(10, 1, 10, 1),
        };
        _pickRefButton.Click += async (_, _) => await PickRefFilesAsync(maxRefs);
        _refUrlBox = new TextBox
        {
            FontSize = 12,
            Height = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(8, 0, 0, 0),
        };
        _addRefButton = new Button
        {
            Content = "添加 URL",
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 26,
            FontSize = 11,
            Padding = new Thickness(10, 1, 10, 1),
            Margin = new Thickness(8, 0, 0, 0),
        };
        _addRefButton.Click += async (_, _) => await AddUrlRefAsync(maxRefs);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_pickRefButton);
        row.Children.Add(_refUrlBox);
        row.Children.Add(_addRefButton);

        _refPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        RenderRefs(maxRefs);

        var section = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        section.Children.Add(header);
        section.Children.Add(row);
        section.Children.Add(_refPanel);
        return section;
    }

    private void RenderRefs(int maxRefs)
    {
        _refPanel.Children.Clear();
        foreach (var r in _imageRefs)
        {
            var removeButton = new Button
            {
                Content = "✕",
                FontSize = 10,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 0, 0),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var refToRemove = r;
            removeButton.Click += (_, _) => { _imageRefs.Remove(refToRemove); RenderRefs(maxRefs); };
            var chip = new Border
            {
                Background = Brush("CardBgBrush"),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children =
                {
                    new TextBlock
                    {
                        Text = "🖼 " + (r.Name ?? "图片"),
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxWidth = 220,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    removeButton,
                } },
            };
            _refPanel.Children.Add(chip);
        }
        _refCount.Text = $"{_imageRefs.Count}/{maxRefs}";
        _addRefButton.IsEnabled = _imageRefs.Count < maxRefs;
        _pickRefButton.IsEnabled = _imageRefs.Count < maxRefs;
    }

    private async Task PickRefFilesAsync(int maxRefs)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择参考图片",
            Multiselect = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            if (_imageRefs.Count >= maxRefs) break;
            try
            {
                var bytes = await File.ReadAllBytesAsync(file);
                _imageRefs.Add(new ReferenceImage(bytes, RefMimeFor(file), Path.GetFileName(file)));
            }
            catch (IOException) { /* 单个文件失败跳过 */ }
        }
        RenderRefs(maxRefs);
    }

    private async Task AddUrlRefAsync(int maxRefs)
    {
        if (_imageRefs.Count >= maxRefs) return;
        var url = _refUrlBox.Text.Trim();
        if (url.Length == 0) return;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var bytes = await http.GetByteArrayAsync(url);
            _imageRefs.Add(new ReferenceImage(bytes, RefMimeFor(url), url));
            _refUrlBox.Text = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _status.Text = "参考图下载失败：" + ex.Message;
            _status.Foreground = Brush("DangerBrush");
        }
        RenderRefs(maxRefs);
    }

    private static string RefMimeFor(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
    }

    /// <summary>v2 修订：模型能力解析四级优先级——模型级声明(providers.json modelCapabilities) > 渠道模板(connection.Channel) > 目录/推断。</summary>
    private ImageModelDescriptor ResolveModel(ImageConnection connection, string modelId)
    {
        CustomImageCapabilities? declared = null;
        if (_ai.Providers.Image?.ModelCapabilities is { } caps && caps.TryGetValue(modelId, out var d))
            declared = d;
        return _catalog.Resolve(modelId, connection.Family,
            ChannelCatalog.Value.CapabilitiesFor(connection.Channel), declared);
    }

    private ImageGenCapabilities CapabilitiesFor(ImageConnection connection, string modelId)
        => ResolveModel(connection, modelId).Capabilities;

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Brush("TextTertiaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
    };

    // ---- 生成 ----

    private async Task GenerateAsync()
    {
        if (_generating || _selectedConnection is null || _selectedModelId.Length == 0) return;
        var prompt = _promptBox.Text.Trim();
        if (prompt.Length == 0)
        {
            _status.Text = "请输入提示词";
            _status.Foreground = Brush("DangerBrush");
            return;
        }

        var capabilities = CapabilitiesFor(_selectedConnection, _selectedModelId);
        ImageAspectRatio ratio;
        ImageScale scale;
        if (capabilities.FixedSizes is { Count: > 0 }
            && _sizeTableCombo.SelectedItem is ComboBoxItem { Tag: string fixedSize }
            && ImageSizeTable.TryParse(fixedSize, out var fw, out var fh)
            && ImageAspectRatioParser.TryFromPixels(fw, fh, out var fixedRatio))
        {
            // 固定尺寸表模式：比例由选中尺寸推导，档位取表内档位
            ratio = fixedRatio;
            scale = capabilities.Scales.Count > 0 ? capabilities.Scales[0] : ImageScale.S1K;
        }
        else
        {
            ratio = _ratioCombo.SelectedIndex >= 0
                ? capabilities.AspectRatios[Math.Clamp(_ratioCombo.SelectedIndex, 0, capabilities.AspectRatios.Count - 1)]
                : ImageAspectRatio.R1x1;
            scale = _scaleCombo.SelectedIndex >= 0
                ? capabilities.Scales[Math.Clamp(_scaleCombo.SelectedIndex, 0, capabilities.Scales.Count - 1)]
                : ImageScale.S1K;
        }
        var quality = _qualityCombo == _qualityPlaceholder
            ? ImageQuality.Auto
            : ((_qualityCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
            {
                "low" => ImageQuality.Low,
                "medium" => ImageQuality.Medium,
                "high" => ImageQuality.High,
                _ => ImageQuality.Auto,
            };
        var count = Math.Max(1, _countCombo.SelectedIndex + 1);
        long? seed = _seedToggle.IsChecked == true && long.TryParse(_seedBox.Text.Trim(), out var s)
            ? s
            : null;
        var transparent = _transparentToggle.IsChecked == true;

        _generationCts = new CancellationTokenSource();
        _generating = true;
        _generateButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _status.Foreground = Brush("TextSecondaryBrush");
        try
        {
            var completed = 0;
            for (var i = 0; i < count; i++)
            {
                _status.Text = count == 1 ? "生成中…（慢渠道单张可能需数分钟）" : $"生成中…（{i + 1}/{count}）";
                try
                {
                    var spec = new ImageGenSpec(
                        Prompt: prompt,
                        AspectRatio: ratio,
                        Scale: scale,
                        Quality: quality,
                        Transparent: transparent,
                        Seed: seed);
                    ImageGenOutput output;
                    if (_imageRefs.Count > 0)
                    {
                        // v2：有参考图 → 图生图/编辑链路（透明同样由门面分流）
                        output = await _ai.EditImageAsync(
                            _selectedConnection.Id, _selectedModelId, spec,
                            _imageRefs.ToList(), _generationCts.Token);
                    }
                    else
                    {
                        output = await _ai.GenerateImageAsync(
                            _selectedConnection.Id, _selectedModelId, spec, _generationCts.Token);
                    }
                    await SaveToGalleryAsync(output, spec);
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    _status.Text = completed == 0 ? "已取消" : $"已取消（完成 {completed}/{count} 张，已保存）";
                    _status.Foreground = Brush("TextSecondaryBrush");
                    return;
                }
            }
            _status.Text = count == 1 ? "生成完成" : $"生成完成（{completed}/{count} 张已保存到画廊）";
            _status.Foreground = Brush("SuccessBrush");
        }
        catch (ProviderException ex)
        {
            _status.Text = DescribeGenerationError(ex);
            _status.Foreground = Brush("DangerBrush");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "画廊保存失败：" + ex.Message;
            _status.Foreground = Brush("DangerBrush");
        }
        finally
        {
            _generating = false;
            _generateButton.IsEnabled = _modelCombo.Items.Count > 0;
            _cancelButton.IsEnabled = false;
            _generationCts.Dispose();
            _generationCts = null;
        }
    }

    private async Task SaveToGalleryAsync(ImageGenOutput output, ImageGenSpec spec)
    {
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var entry = new GalleryEntry(
            Id: id,
            CreatedAt: DateTimeOffset.UtcNow,
            ConnectionId: _selectedConnection!.Id,
            ModelId: _selectedModelId,
            Prompt: spec.Prompt,
            AspectRatio: ImageAspectRatioParser.ToDisplay(spec.AspectRatio),
            Scale: ImageScaleParser.ToDisplay(spec.Scale),
            Quality: spec.Quality == ImageQuality.Auto ? "auto"
                : spec.Quality == ImageQuality.Low ? "low"
                : spec.Quality == ImageQuality.Medium ? "medium" : "high",
            Transparent: spec.Transparent,
            SeedUsed: output.SeedUsed);
        await _gallery.SaveAsync(entry, output.Bytes);
        RefreshGallery();
        SelectPreview(entry);
    }

    private string DescribeGenerationError(ProviderException ex) => ex.Code switch
    {
        "auth" => "鉴权失败，请检查连接 API Key",
        "timeout" => "生成超时（单张超过 5 分钟）",
        "network" => "无法连接生图服务",
        "rate-limit" => "生图服务请求过于频繁，请稍后再试",
        "server" => "生图服务暂时不可用",
        "invalid-response" => "生图响应解析失败",
        "invalid-request" => "生图请求无效：" + ex.Message,
        _ => "生成失败：" + ex.Message,
    };

    // ---- 历史画廊 ----

    private void RefreshGallery()
    {
        _thumbPanel.Children.Clear();
        foreach (var entry in _gallery.Load().Entries)
        {
            _thumbPanel.Children.Add(BuildThumb(entry));
        }
        if (_thumbPanel.Children.Count == 0)
        {
            _thumbPanel.Children.Add(new TextBlock
            {
                Text = "还没有生成记录。生成后图片会保存在这里。",
                FontSize = 11,
                Foreground = Brush("TextTertiaryBrush"),
                Margin = new Thickness(0, 8, 0, 8),
            });
        }
    }

    private UIElement BuildThumb(GalleryEntry entry)
    {
        var image = new Image
        {
            Width = 96,
            Height = 96,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
        };
        var path = _gallery.FilePathFor(entry);
        if (path is not null) image.Source = LoadBitmap(path, decodeWidth: 96);
        else image.Source = CreatePlaceholder();
        image.MouseLeftButtonUp += (_, _) => SelectPreview(entry);
        System.Windows.Automation.AutomationProperties.SetName(image, entry.Prompt);
        return image;
    }

    private void SelectPreview(GalleryEntry entry)
    {
        _previewEntry = entry;
        var path = _gallery.FilePathFor(entry);
        _previewImage.Source = path is null ? CreatePlaceholder() : LoadBitmap(path, decodeWidth: 640);
        var local = entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        _previewMeta.Text = $"{local} · {entry.ModelId} · {entry.AspectRatio} · {entry.Scale}"
            + (entry.Transparent ? " · 透明" : "")
            + (string.IsNullOrEmpty(entry.SeedUsed) ? "" : $" · seed {entry.SeedUsed}")
            + $"\n{entry.Prompt}";
        _deleteButton.IsEnabled = true;
    }

    private async Task DeletePreviewAsync()
    {
        if (_previewEntry is null) return;
        var confirmed = MessageBox.Show(
            this,
            "从画廊删除这张图？（图片文件与索引记录一并删除）",
            "DesktopPet",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes) return;
        var id = _previewEntry.Id;
        await _gallery.DeleteAsync(id);
        _previewEntry = null;
        _previewImage.Source = null;
        _previewMeta.Text = "";
        _deleteButton.IsEnabled = false;
        RefreshGallery();
    }

    private static BitmapImage LoadBitmap(string path, int decodeWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;  // 立即解码进内存，避免锁文件
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();                                // 跨线程安全（异步生成完成回调）
        return bitmap;
    }

    private static BitmapImage CreatePlaceholder()
    {
        // 文件缺失时的占位（如索引存在但图片被手动删除）
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri("pack://application:,,,/Assets/app.ico");
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ---- 样式辅助（与设置页同源：Application 资源）----

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    private static Style AppStyle(string key) => (Style)Application.Current.FindResource(key);
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.App.Ai;
using DesktopPet.App.Rendering;
using DesktopPet.Core.I18n;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.SpriteSkill;
using Microsoft.Win32;

namespace DesktopPet.App.Windows;

/// <summary>
/// 动作精灵图技能页：选技能 → 描述需求（+参考图）→ LLM 生成动作计划 →
/// 逐行生图 → 切帧/拼图/校验 → 预览图集 → 保存为桌宠精灵。
/// 入口：设置页 AI 区"动作精灵图"按钮。
/// </summary>
public sealed class SpriteSkillWindow : Window
{
    private readonly AiCoordinator _ai;
    private readonly SpriteLoader _spriteLoader;
    private readonly I18nService _i18n;

    private readonly TextBox _requestBox;
    private readonly Button _pickRefButton;
    private readonly TextBlock _refCount;
    private readonly List<ReferenceImage> _refs = [];
    private readonly ComboBox _frameCombo;
    private readonly ComboBox _modeCombo;
    private readonly ComboBox _styleCombo;
    private readonly Button _generateButton;
    private readonly Button _cancelButton;
    private readonly TextBlock _status;
    private readonly Image _preview;
    private readonly Button _saveButton;

    private CancellationTokenSource? _cts;
    private SpriteSkillSession? _session;
    private SpriteSkillOptions? _sessionOptions;
    private byte[]? _resultPng;

    public SpriteSkillWindow(AiCoordinator ai, SpriteLoader spriteLoader, I18nService i18n)
    {
        _ai = ai;
        _spriteLoader = spriteLoader;
        _i18n = i18n;

        Icon = AppIcons.WindowIcon();
        Title = "动作精灵图";
        Width = 760;
        Height = 640;
        MinWidth = 680;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBgBrush");

        // ---- 技能说明 ----
        var skillLabel = new TextBlock
        {
            Text = "技能：动作精灵图 —— 用 LLM 编排生成自定义动作精灵图（每行一个动作），可保存为桌宠精灵。",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        // ---- 需求描述 ----
        var requestLabel = new TextBlock
        {
            Text = "描述你想要的宠物和动作（例如：一只橘猫，做 idle 呼吸和 click 蹦跳两个动作，各 6 帧）",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
        };
        _requestBox = new TextBox
        {
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var requestPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        requestPanel.Children.Add(requestLabel);
        requestPanel.Children.Add(_requestBox);

        // ---- 可选配参数：帧数 / 生成模式 / 风格 ----
        _frameCombo = new ComboBox { FontSize = 13, Width = 90 };
        foreach (var n in new[] { 3, 6, 9 })
            _frameCombo.Items.Add($"{n} 帧");
        _frameCombo.SelectedIndex = 0;
        _modeCombo = new ComboBox { FontSize = 13, Width = 170 };
        _modeCombo.Items.Add("多帧一行（快）");
        _modeCombo.Items.Add("逐帧生成（稳）");
        _modeCombo.SelectedIndex = 0;
        _styleCombo = new ComboBox { FontSize = 13, Width = 140 };
        _styleCombo.Items.Add("简化（稳定）");
        _styleCombo.Items.Add("完整（细节）");
        _styleCombo.SelectedIndex = 0;
        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        optionsRow.Children.Add(OptionBlock("帧数", _frameCombo));
        optionsRow.Children.Add(OptionBlock("生成模式", _modeCombo));
        optionsRow.Children.Add(OptionBlock("风格", _styleCombo));

        // ---- 参考图 ----
        _pickRefButton = new Button
        {
            Content = "选择参考图…",
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 28,
            Padding = new Thickness(12, 3, 12, 3),
        };
        _pickRefButton.Click += (_, _) => PickReferences();
        _refCount = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var refHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        refHeader.Children.Add(_pickRefButton);
        refHeader.Children.Add(_refCount);
        var refLabel = new TextBlock
        {
            Text = "参考图（可选）：用于锁定宠物身份",
            FontSize = 12,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var refPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        refPanel.Children.Add(refLabel);
        refPanel.Children.Add(refHeader);

        // ---- 操作 ----
        _generateButton = new Button
        {
            Content = "生成",
            Style = AppStyle("ButtonPrimaryStyle"),
            Height = 30,
            MinWidth = 96,
        };
        _generateButton.Click += (_, _) => _ = GenerateAsync();
        _cancelButton = new Button
        {
            Content = "取消",
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 30,
            MinWidth = 96,
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = false,
        };
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _saveButton = new Button
        {
            Content = "保存为桌宠精灵",
            Style = AppStyle("ButtonDefaultStyle"),
            Height = 30,
            MinWidth = 120,
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = false,
        };
        _saveButton.Click += (_, _) => SaveToDesktopPet();
        _status = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 16,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal };
        actionRow.Children.Add(_generateButton);
        actionRow.Children.Add(_cancelButton);
        actionRow.Children.Add(_saveButton);

        // ---- 预览 ----
        _preview = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 300,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(skillLabel);
        root.Children.Add(requestPanel);
        root.Children.Add(optionsRow);
        root.Children.Add(refPanel);
        root.Children.Add(actionRow);
        root.Children.Add(_status);
        root.Children.Add(_preview);
        Content = new ScrollViewer { Content = root };
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    private static UIElement OptionBlock(string label, UIElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 20, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(control);
        return panel;
    }

    private SpriteSkillOptions CurrentOptions()
    {
        var frameCount = _frameCombo.SelectedIndex switch
        {
            1 => 6,
            2 => 9,
            _ => 3,
        };
        return new SpriteSkillOptions(
            DefaultFrameCount: frameCount,
            Mode: _modeCombo.SelectedIndex == 1 ? SpriteGenMode.PerFrame : SpriteGenMode.RowStrip,
            Style: _styleCombo.SelectedIndex == 1 ? SpriteStyleLevel.Full : SpriteStyleLevel.Simple);
    }

    private void PickReferences()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择参考图",
            Multiselect = true,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp",
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
        {
            var bytes = File.ReadAllBytes(path);
            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };
            _refs.Add(new ReferenceImage(bytes, mime, Path.GetFileName(path)));
        }
        _refCount.Text = $"已选 {_refs.Count} 张";
    }

    private async Task GenerateAsync()
    {
        var request = _requestBox.Text.Trim();
        if (request.Length == 0)
        {
            SetStatus("请先描述你想要的宠物和动作。", isError: true);
            return;
        }

        _session ??= _ai.CreateSpriteSkillSession(options: CurrentOptions());
        if (_session is null)
        {
            SetStatus("未配置 AI 连接：请先在设置页配置模型连接（对话）与生图连接。", isError: true);
            return;
        }
        // 切换选项后重建会话（选项变化时）
        var nextOptions = CurrentOptions();
        if (_sessionOptions != nextOptions)
        {
            _session = _ai.CreateSpriteSkillSession(options: nextOptions);
            _sessionOptions = nextOptions;
        }

        SetBusy(true);
        SetStatus("正在生成动作计划并逐行生图（通常需要数十秒到数分钟）…");
        _resultPng = null;
        _preview.Source = null;
        _saveButton.IsEnabled = false;
        _cts = new CancellationTokenSource();

        try
        {
            var referenceDescription = _refs.Count > 0
                ? "参考图：" + string.Join("、", _refs.Select(r => r.Name ?? "图片"))
                : null;
            var result = await _session.RunAsync(request, referenceDescription, _refs, _cts.Token);

            if (result.Ok && result.SheetPng is not null)
            {
                _resultPng = result.SheetPng;
                _preview.Source = BitmapFromBytes(result.SheetPng);
                var actions = result.Actions is null ? "" : string.Join(", ", result.Actions.Select(a => $"{a.Id}({a.FrameCount}帧)"));
                SetStatus($"生成完成：{actions}。检查预览，满意后点「保存为桌宠精灵」。");
                _saveButton.IsEnabled = true;
            }
            else
            {
                SetStatus($"生成失败：{result.Error}", isError: true);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消。");
        }
        catch (Exception ex)
        {
            SetStatus($"生成出错：{ex.Message}", isError: true);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SaveToDesktopPet()
    {
        if (_resultPng is null) return;
        var slug = "skill-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _spriteLoader.SaveLocal(slug, _resultPng);
        SetStatus($"已保存为桌宠精灵：{slug}（sprites 目录）。刷新桌宠后可选。");
    }

    private void SetBusy(bool busy)
    {
        _generateButton.IsEnabled = !busy;
        _cancelButton.IsEnabled = busy;
        _pickRefButton.IsEnabled = !busy;
    }

    private void SetStatus(string text, bool isError = false)
    {
        _status.Text = text;
        _status.Foreground = isError ? Brush("DangerBrush") : Brush("TextSecondaryBrush");
    }

    private static BitmapImage BitmapFromBytes(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DesktopPet.App.Localization;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Personas;

namespace DesktopPet.App.Windows;

/// <summary>
/// 初始化引导窗（参考 harness/openclaw 的首次引导：先问称呼 + 选人格，之后设置页可改）。
/// 架构：纯 UI 组件——不直接读/写存储，输入通过 <see cref="OnComplete"/> 回调交给调用方
/// （App 启动 / 设置页开启 AI 两处触发，保存逻辑收敛到调用方一处）。
/// Lumen 2.0：品牌头部 + 表单分组 + 主按钮。
/// </summary>
public sealed class WelcomeWindow : Window
{
    private readonly TextBox _callNameBox = new()
    {
        FontSize = 13,
        Height = 34,
        Padding = new Thickness(10, 4, 10, 4),
        MaxLength = 20,
    };
    private readonly ComboBox _personaCombo = new() { FontSize = 13, Height = 34 };
    private readonly IReadOnlyList<Persona> _builtinPersonas;
    private readonly I18nService _i18n;

    public WelcomeWindow(
        IReadOnlyList<Persona> builtinPersonas,
        string initialCallName,
        string selectedPersonaId,
        Func<string, string, bool> onComplete,
        I18nService? i18n = null)
    {
        var localization = i18n ?? new I18nService();
        _i18n = localization;
        _builtinPersonas = builtinPersonas;
        Title = "欢迎使用 AI 桌宠";
        Width = 440;
        Height = 390;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("WindowBgBrush");
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        foreach (var p in builtinPersonas)
        {
            _personaCombo.Items.Add($"{_i18n.T(p.Name)} — {_i18n.T(p.Description)}");
        }
        var index = builtinPersonas.ToList().FindIndex(p => p.Id == selectedPersonaId);
        _personaCombo.SelectedIndex = index >= 0 ? index : 0;

        _callNameBox.Text = initialCallName;
        _callNameBox.SelectAll();

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 20) };

        // 品牌头部
        var hero = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        hero.Children.Add(new Border
        {
            Width = 52,
            Height = 52,
            Background = (Brush)Application.Current.FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(17),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = "DP",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        hero.Children.Add(new TextBlock
        {
            Text = "欢迎来到 DesktopPet",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });
        hero.Children.Add(new TextBlock
        {
            Text = "先认识一下，之后随时可在 设置 → AI 助手 修改。",
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("TextTertiaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });
        root.Children.Add(hero);

        // 表单分组
        var formCard = new Border
        {
            Background = (Brush)Application.Current.FindResource("CardBgBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusCard"),
            Padding = new Thickness(18, 16, 18, 16),
            Effect = (Effect)Application.Current.FindResource("ShadowCard"),
        };
        var form = new StackPanel();
        form.Children.Add(new TextBlock
        {
            Text = "怎么称呼你？",
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        form.Children.Add(_callNameBox);
        form.Children.Add(new TextBlock
        {
            Text = "选一个它的人格（决定说话风格）：",
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 14, 0, 6),
        });
        form.Children.Add(_personaCombo);
        formCard.Child = form;
        root.Children.Add(formCard);

        var startButton = new Button
        {
            Content = "开始！",
            Style = (Style)Application.Current.FindResource("ButtonPrimaryStyle"),
            Width = 140,
            Height = 36,
            FontSize = 14,
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        startButton.Click += (_, _) =>
        {
            var callName = _callNameBox.Text.Trim();
            if (callName.Length == 0)
            {
                MessageBox.Show(this, _i18n.T("先告诉我怎么称呼你吧～"), "DesktopPet");
                return;
            }
            var personaId = builtinPersonas[Math.Max(0, _personaCombo.SelectedIndex)].Id;
            if (onComplete(callName, personaId))
                Close();
        };
        root.Children.Add(startButton);
        root.Children.Add(new TextBlock
        {
            Text = "称呼会存在记忆里（可改），人格随时可换。",
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.FindResource("TextTertiaryBrush"),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        });

        Content = root;
        WpfLocalizer.ApplyNew(this, _i18n);
    }

    public void ApplyLocalization(I18nService i18n)
    {
        var selected = _personaCombo.SelectedIndex;
        _personaCombo.Items.Clear();
        foreach (var persona in _builtinPersonas)
        {
            _personaCombo.Items.Add($"{i18n.T(persona.Name)} — {i18n.T(persona.Description)}");
        }
        _personaCombo.SelectedIndex = selected;
        WpfLocalizer.RefreshTracked(this, i18n);
    }
}

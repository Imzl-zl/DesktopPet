using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPet.Core.Personas;

namespace DesktopPet.App.Windows;

/// <summary>
/// 初始化引导窗（参考 harness/openclaw 的首次引导：先问称呼 + 选人格，之后设置页可改）。
/// 架构：纯 UI 组件——不直接读/写存储，输入通过 <see cref="OnComplete"/> 回调交给调用方
/// （App 启动 / 设置页开启 AI 两处触发，保存逻辑收敛到调用方一处）。
/// </summary>
public sealed class WelcomeWindow : Window
{
    private readonly TextBox _callNameBox = new()
    {
        FontSize = 13,
        Height = 30,
        Padding = new Thickness(8, 4, 8, 4),
        MaxLength = 20,
    };
    private readonly ComboBox _personaCombo = new() { FontSize = 13, Height = 30 };

    public WelcomeWindow(
        IReadOnlyList<Persona> builtinPersonas,
        string initialCallName,
        string selectedPersonaId,
        Action<string, string> onComplete)
    {
        Title = "欢迎使用 AI 桌宠";
        Width = 420;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        foreach (var p in builtinPersonas)
        {
            _personaCombo.Items.Add($"{p.Name} — {p.Description}");
        }
        var index = builtinPersonas.ToList().FindIndex(p => p.Id == selectedPersonaId);
        _personaCombo.SelectedIndex = index >= 0 ? index : 0;

        _callNameBox.Text = initialCallName;
        _callNameBox.SelectAll();

        var form = new StackPanel { Margin = new Thickness(20) };
        form.Children.Add(new TextBlock
        {
            Text = "欢迎～ 先认识一下，之后随时可在 设置 → AI 助手 修改。",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        form.Children.Add(new TextBlock
        {
            Text = "怎么称呼你？",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
            Margin = new Thickness(0, 14, 0, 4),
        });
        form.Children.Add(_callNameBox);
        form.Children.Add(new TextBlock
        {
            Text = "选一个它的人格（决定说话风格）：",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x80)),
            Margin = new Thickness(0, 10, 0, 4),
        });
        form.Children.Add(_personaCombo);

        var startButton = new Button
        {
            Content = "开始！",
            Width = 120,
            Height = 32,
            FontSize = 13,
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        startButton.Click += (_, _) =>
        {
            var callName = _callNameBox.Text.Trim();
            if (callName.Length == 0)
            {
                MessageBox.Show(this, "先告诉我怎么称呼你吧～", "DesktopPet");
                return;
            }
            var personaId = builtinPersonas[Math.Max(0, _personaCombo.SelectedIndex)].Id;
            onComplete(callName, personaId);
            Close();
        };
        form.Children.Add(startButton);
        form.Children.Add(new TextBlock
        {
            Text = "称呼会存在记忆里（可改），人格随时可换。",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA0)),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        });

        Content = form;
    }
}

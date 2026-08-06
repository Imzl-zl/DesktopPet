using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DesktopPet.App.Windows;

/// <summary>
/// 宠物气泡（对齐 bubble.ts 的 BubbleRenderer）：多行自适应胶囊、文本变化交叉淡入
/// （150ms ease，Lumen 动效规范）、重复文本 no-op、headroom 定位在宠物头顶。
/// 长文本换行显示，最大宽度 240px，避免常驻桌面气泡遮挡工作内容。
/// 外观（主题/不透明度/字号/字体）由设置页驱动：ApplyAppearance 全量刷新。
/// </summary>
public sealed class BubbleView : Border
{
    private readonly TextBlock _text = new()
    {
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x26)),
        TextWrapping = TextWrapping.Wrap, // 长文本换行显示全（对齐 TS 后体验修正）
        MaxWidth = 240,
    };

    private string? _shownText;

    // 当前外观快照（主题/不透明度/字号/字体族），应用时全量重建
    private string _theme = "system";
    private int _opacityPercent = 92;
    private int _fontSize = 13;
    private string _fontFamily = "system";

    public BubbleView()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)); // glass（不透明度提升，文字更清晰）
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x1C, 0x20, 0x28));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(18);
        Padding = new Thickness(14, 8, 14, 8);
        Effect = (Effect)Application.Current.FindResource("ShadowBubble");
        Child = _text;
        Visibility = Visibility.Collapsed;
        RenderTransform = new TranslateTransform();
    }

    /// <summary>
    /// 应用设置页外观：气泡主题（light/dark/system）、不透明度（0-100）、
    /// 字号（8-24）、字体族（system/rounded/mono）。主题变化时重建配色。
    /// </summary>
    public void ApplyAppearance(string theme, int opacityPercent, int fontSize, string fontFamily)
    {
        var newTheme = theme switch { "light" => "light", "dark" => "dark", _ => "system" };
        var changed = newTheme != _theme
            || opacityPercent != _opacityPercent
            || fontSize != _fontSize
            || fontFamily != _fontFamily;
        if (!changed) return;

        _theme = newTheme;
        _opacityPercent = Math.Clamp(opacityPercent, 0, 100);
        _fontSize = Math.Clamp(fontSize, 8, 24);
        _fontFamily = fontFamily switch { "rounded" => "rounded", "mono" => "mono", _ => "system" };

        var isDark = _theme == "dark" || (_theme == "system" && !SystemUsesLightTheme());
        var alpha = (byte)(_opacityPercent * 255 / 100);
        Background = new SolidColorBrush(Color.FromArgb(
            alpha,
            isDark ? (byte)0x1B : (byte)0xFF,
            isDark ? (byte)0x1F : (byte)0xFF,
            isDark ? (byte)0x26 : (byte)0xFF));
        BorderBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(_opacityPercent * 31 / 100),
            isDark ? (byte)0xFF : (byte)0x1C,
            isDark ? (byte)0xFF : (byte)0x20,
            isDark ? (byte)0xFF : (byte)0x28));
        _text.Foreground = new SolidColorBrush(isDark ? Colors.White : Color.FromRgb(0x1B, 0x1F, 0x26));
        _text.FontSize = _fontSize;
        _text.FontFamily = _fontFamily switch
        {
            "rounded" => new FontFamily("Segoe UI Variable, Microsoft YaHei UI"),
            "mono" => new FontFamily("Cascadia Mono, Consolas"),
            _ => new FontFamily("Segoe UI Variable, Microsoft YaHei UI, Segoe UI"),
        };
    }

    /// <summary>Windows 应用深浅色（注册表 AppsUseLightTheme；读不到按浅色）。</summary>
    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme", 1) is int v && v == 1;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>显示单行文本；文本变化时交叉淡入（对齐 renderLine）。</summary>
    public void RenderLine(string text)
    {
        if (_shownText == text && Visibility == Visibility.Visible)
        {
            return; // 重入 no-op（对齐 TS：同文本不重绘）
        }
        Visibility = Visibility.Visible;
        if (_shownText != text)
        {
            _text.Text = text;
            _shownText = text;
            CrossFade();
        }
    }

    public void Hide()
    {
        if (Visibility == Visibility.Collapsed) return;
        _shownText = null;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>气泡紧贴宠物头顶（headroom 间隙，对齐 snugBubble：整数 px 防抖动）。</summary>
    public void SnugToHeadroom(double headroomGapPx)
    {
        var gap = Math.Floor(Math.Max(0, headroomGapPx));
        var transform = (TranslateTransform)RenderTransform;
        if (Math.Abs(transform.Y - gap) > 0.01)
        {
            transform.Y = gap;
        }
    }

    private void CrossFade()
    {
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, animation);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopPet.App.Windows;

/// <summary>
/// 宠物气泡（对齐 bubble.ts 的 BubbleRenderer）：单行胶囊、文本变化交叉淡入
/// （150ms ease，Lumen 动效规范）、重复文本 no-op、headroom 定位在宠物头顶。
/// </summary>
public sealed class BubbleView : Border
{
    private readonly TextBlock _text = new()
    {
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x26)),
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 240,
    };

    private string? _shownText;

    public BubbleView()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF)); // glass
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0x1C, 0x20, 0x28));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(18);
        Padding = new Thickness(12, 7, 12, 7);
        Child = _text;
        Visibility = Visibility.Collapsed;
        RenderTransform = new TranslateTransform();
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

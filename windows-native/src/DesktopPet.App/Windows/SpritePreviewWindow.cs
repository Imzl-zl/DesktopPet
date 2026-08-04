using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopPet.Core.Rendering;

namespace DesktopPet.App.Windows;

/// <summary>
/// 导入切片预览（对齐 settings.html 的切片预览交互，最小版）：
/// 原图 + 切片网格线覆盖 + 帧条（每行首帧缩略图），确认后导入。
/// </summary>
public sealed class SpritePreviewWindow : Window
{
    private readonly SpriteSheet _sheet;
    private readonly byte[] _sourceBytes;
    private readonly string _suggestedName;

    public SpritePreviewWindow(SpriteSheet sheet, byte[] sourceBytes, string suggestedName)
    {
        _sheet = sheet;
        _sourceBytes = sourceBytes;
        _suggestedName = suggestedName;

        Title = "切片预览 — DesktopPet";
        Width = 620;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));

        var root = new StackPanel { Margin = new Thickness(16) };

        var header = new TextBlock
        {
            Text = $"{suggestedName} — 检测到 {sheet.Clips.Count} 行动画",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        };
        root.Children.Add(header);

        // 原图 + 网格线覆盖
        var imageArea = new Grid { Width = 384, Height = 320, HorizontalAlignment = HorizontalAlignment.Center };
        var source = BitmapSource.Create(
            sheet.SourceWidth, sheet.SourceHeight, 96, 96, PixelFormats.Bgra32, null,
            RgbaToBgra(_sourceBytes), sheet.SourceWidth * 4);
        var image = new Image { Source = source, Stretch = Stretch.Uniform, Width = 384, Height = 320 };
        imageArea.Children.Add(image);

        var overlay = new Canvas { Width = 384, Height = 320 };
        var scaleX = 384.0 / sheet.SourceWidth;
        var scaleY = 320.0 / sheet.SourceHeight;
        foreach (var clip in sheet.Clips)
        {
            foreach (var frame in clip)
            {
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = frame.Width * scaleX,
                    Height = frame.Height * scaleY,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65)),
                    StrokeThickness = 1,
                    StrokeDashArray = [3, 2],
                };
                Canvas.SetLeft(rect, frame.RectX * scaleX);
                Canvas.SetTop(rect, frame.RectY * scaleY);
                overlay.Children.Add(rect);
            }
        }
        imageArea.Children.Add(overlay);
        root.Children.Add(imageArea);

        // 帧条：每行首帧缩略图
        var frameStrip = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), MaxWidth = 600 };
        foreach (var clip in _sheet.Clips)
        {
            if (clip.Count == 0) continue;
            var frame = clip[0];
            var thumb = new Image
            {
                Source = FrameToBitmap(frame),
                Width = 56,
                Height = 56 * frame.Height / (double)frame.Width,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(2),
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.NearestNeighbor);
            frameStrip.Children.Add(thumb);
        }
        root.Children.Add(frameStrip);

        // 操作按钮
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = "取消", Width = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4) };
        cancel.Click += (_, _) => { DialogResult = false; };
        var confirm = new Button { Content = "导入此宠物", Width = 110, Padding = new Thickness(4), IsDefault = true };
        confirm.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);

        Content = root;
    }

    /// <summary>导入时由 PetWindow 读取：原始文件字节 + 建议名。</summary>
    public (byte[] Bytes, string Name) ImportPayload => (_sourceBytes, _suggestedName);

    private static byte[] RgbaToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        return bgra;
    }

    private static BitmapSource FrameToBitmap(SpriteFrame frame)
        => BitmapSource.Create(
            frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null,
            RgbaToBgra(frame.Rgba), frame.Width * 4);
}

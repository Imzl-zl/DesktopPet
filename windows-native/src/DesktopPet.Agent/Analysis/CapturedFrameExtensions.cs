using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using DesktopPet.Agent.Capture;

namespace DesktopPet.Agent.Analysis;

/// <summary>灰度帧 → data URL（视觉模型消息用）。</summary>
public static class CapturedFrameExtensions
{
    public static string ToDataUrl(this CapturedFrame frame)
    {
        using var image = Image.LoadPixelData<L8>(frame.Gray, frame.Width, frame.Height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }
}

using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet.App;

/// <summary>从嵌入资源 app.ico 加载托盘/窗口图标（与 exe 图标同源，避免代码绘制与资源双份维护）。</summary>
internal static class AppIcons
{
    private const string ResourceUri = "pack://application:,,,/Assets/app.ico";

    /// <summary>托盘图标（H.NotifyIcon 需要 System.Drawing.Icon）。</summary>
    public static Icon TrayIcon()
    {
        using var stream = OpenStream();
        return new Icon(stream);
    }

    /// <summary>WPF 窗口图标（取最大帧，避免 16px 首帧在任务栏/Alt-Tab 模糊）。</summary>
    public static ImageSource WindowIcon()
    {
        using var stream = OpenStream();
        var decoder = new IconBitmapDecoder(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    private static Stream OpenStream()
        => Application.GetResourceStream(new Uri(ResourceUri))?.Stream
           ?? throw new InvalidOperationException("app.ico 资源缺失（确认 csproj 含 <Resource Include=\"Assets\\app.ico\" />）");
}

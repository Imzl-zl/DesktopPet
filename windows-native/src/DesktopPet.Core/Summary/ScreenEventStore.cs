namespace DesktopPet.Core.Summary;

/// <summary>
/// 屏幕事件 journal 文件规则（jsonl，一天一个文件）：
/// {appDataDir}/diary/screen-YYYY-MM-DD.jsonl，一行一条屏幕事件。
/// 落盘/清理/读取的 IO 在应用层（Core 零 IO），此处只定义路径与文件名匹配规则。
/// </summary>
public static class ScreenEventStore
{
    public const string FilePrefix = "screen-";
    public const string FileExtension = ".jsonl";

    public static string Path(string appDataDir, DateOnly day)
        => System.IO.Path.Combine(appDataDir, "diary", $"{FilePrefix}{day:yyyy-MM-dd}{FileExtension}");

    /// <summary>文件名中的日期（用于清理/读取解析；非本目录文件返回 null）。</summary>
    public static DateOnly? ParseDateFromFileName(string fileName)
    {
        if (!fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            return null;
        var middle = fileName.Substring(FilePrefix.Length, fileName.Length - FilePrefix.Length - FileExtension.Length);
        return DateOnly.TryParseExact(middle, "yyyy-MM-dd", out var day) ? day : null;
    }
}

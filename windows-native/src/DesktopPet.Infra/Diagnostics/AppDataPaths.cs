namespace DesktopPet.Infra.Diagnostics;

public sealed record AppDataPaths(string Root)
{
    public string Logs => Path.Combine(Root, "logs");
    public string Diary => Path.Combine(Root, "diary");
    public string Sprites => Path.Combine(Root, "sprites");

    /// <summary>生图历史画廊（阶段 5：PNG 文件 + index.json）。</summary>
    public string Gallery => Path.Combine(Root, "gallery");

    public static AppDataPaths ForCurrentUser()
        => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPet"));
}

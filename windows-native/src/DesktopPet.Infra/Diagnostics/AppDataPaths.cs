namespace DesktopPet.Infra.Diagnostics;

public sealed record AppDataPaths(string Root)
{
    public string Logs => Path.Combine(Root, "logs");
    public string Diary => Path.Combine(Root, "diary");
    public string Sprites => Path.Combine(Root, "sprites");

    public static AppDataPaths ForCurrentUser()
        => new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPet"));
}

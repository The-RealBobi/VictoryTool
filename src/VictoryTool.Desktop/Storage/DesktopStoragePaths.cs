namespace VictoryTool.Desktop.Storage;

public static class DesktopStoragePaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VictoryTool");

    public static string Settings => Path.Combine(Root, "settings.json");

    public static string Recovery => Path.Combine(Root, "Recovery");
}

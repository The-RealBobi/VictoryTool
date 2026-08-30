namespace VictoryTool.Application;

public static class ApplicationVersion
{
    public static string Current =>
        typeof(ApplicationVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}

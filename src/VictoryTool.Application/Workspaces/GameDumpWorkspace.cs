namespace VictoryTool.Application.Workspaces;

public sealed class GameDumpWorkspace
{
    private GameDumpWorkspace(string rootPath, string gameDataPath)
    {
        RootPath = rootPath;
        GameDataPath = gameDataPath;
    }

    public string RootPath { get; }

    public string GameDataPath { get; }

    public IReadOnlyList<string> EnumerateCfgBinFiles()
    {
        return Directory
            .EnumerateFiles(GameDataPath, "*.cfg.bin", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(GameDataPath, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static GameDumpWorkspace Open(string rootPath)
    {
        var result = new GameDumpLocator().Locate(rootPath);
        if (!result.IsValid)
            throw new GameDumpValidationException(result);

        var selection = result.Selection!;
        return new GameDumpWorkspace(selection.RootPath, selection.GameDataPath);
    }
}

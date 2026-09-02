using VictoryTool.Application.Diagnostics;

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
        var files = Directory
            .EnumerateFiles(GameDataPath, "*.cfg.bin", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(GameDataPath, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        GlobalLog.Debug("dump_cfgbin_files_enumerated", new Dictionary<string, object?>
        {
            ["count"] = files.Length,
        });
        return files;
    }

    public static GameDumpWorkspace Open(string rootPath)
    {
        using var operation = GlobalLog.BeginOperation("dump_workspace_open");
        var result = new GameDumpLocator().Locate(rootPath);
        if (!result.IsValid)
        {
            GlobalLog.Warn("dump_workspace_rejected", new Dictionary<string, object?>
            {
                ["diagnosticCount"] = result.Diagnostics.Count,
            });
            throw new GameDumpValidationException(result);
        }

        var selection = result.Selection!;
        GlobalLog.Info("dump_workspace_opened", new Dictionary<string, object?>
        {
            ["hasPcResources"] = selection.HasPcResources,
            ["hasSwitchResources"] = selection.HasSwitchResources,
        });
        return new GameDumpWorkspace(selection.RootPath, selection.GameDataPath);
    }
}

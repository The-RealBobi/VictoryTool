using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Workspaces;

public interface IGameDumpLocator
{
    GameDumpValidationResult Locate(string selectedPath);
}

public sealed class GameDumpLocator : IGameDumpLocator
{
    public GameDumpValidationResult Locate(string selectedPath)
    {
        using var operation = GlobalLog.BeginOperation("dump_locate", new Dictionary<string, object?>
        {
            ["hasSelectedPath"] = !string.IsNullOrWhiteSpace(selectedPath),
        });
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            GlobalLog.Warn("dump_locate_path_missing");
            return GameDumpValidationResult.Invalid(new GameDumpValidationDiagnostic(
                "path",
                "dump.path_required",
                "Select a folder that contains the extracted game data."));
        }

        var fullPath = Path.GetFullPath(selectedPath);
        if (!Directory.Exists(fullPath))
        {
            GlobalLog.Warn("dump_locate_directory_missing");
            return GameDumpValidationResult.Invalid(new GameDumpValidationDiagnostic(
                "path",
                "dump.path_missing",
                "The selected folder does not exist or is unavailable."));
        }

        var roots = GetCandidateRoots(fullPath)
            .Where(root => Directory.Exists(Path.Combine(root, "common", "gamedata")))
            .Distinct(PathComparer)
            .ToArray();

        if (roots.Length == 0)
        {
            GlobalLog.Warn("dump_locate_root_missing");
            return GameDumpValidationResult.Invalid(new GameDumpValidationDiagnostic(
                "path",
                "dump.root_not_found",
                "No supported game-data root was found. Select raw/data, common, gamedata, raw, or the dump folder."));
        }

        if (roots.Length > 1)
        {
            GlobalLog.Warn("dump_locate_root_ambiguous", new Dictionary<string, object?>
            {
                ["candidateCount"] = roots.Length,
            });
            return GameDumpValidationResult.Invalid(new GameDumpValidationDiagnostic(
                "path",
                "dump.root_ambiguous",
                "More than one supported game-data root was found near the selected folder."));
        }

        var rootPath = roots[0];
        var gameDataPath = Path.Combine(rootPath, "common", "gamedata");
        var hasPcResources = Directory.Exists(Path.Combine(rootPath, "dx11", "menu"));
        var hasSwitchResources = Directory.Exists(Path.Combine(rootPath, "nx", "menu"));
        var compatibleInputCount = CountCompatibleInputs(gameDataPath);
        var platformEvidence = new List<string>();
        if (hasPcResources) platformEvidence.Add("PC/DX11 menu resources");
        if (hasSwitchResources) platformEvidence.Add("Switch/NX menu resources");
        if (platformEvidence.Count == 0) platformEvidence.Add("No supported platform menu resources detected");

        GlobalLog.Info("dump_locate_completed", new Dictionary<string, object?>
        {
            ["candidateCount"] = roots.Length,
            ["hasPcResources"] = hasPcResources,
            ["hasSwitchResources"] = hasSwitchResources,
            ["compatibleInputCount"] = compatibleInputCount,
        });
        return GameDumpValidationResult.Valid(new GameDumpSelection(
            fullPath,
            rootPath,
            gameDataPath,
            hasPcResources,
            hasSwitchResources,
            "Unknown",
            compatibleInputCount,
            platformEvidence));
    }

    private static IEnumerable<string> GetCandidateRoots(string selectedPath)
    {
        yield return selectedPath;

        var name = Path.GetFileName(selectedPath);
        if (string.Equals(name, "common", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(selectedPath);
            if (parent is not null) yield return parent.FullName;
        }
        else if (string.Equals(name, "gamedata", StringComparison.OrdinalIgnoreCase))
        {
            var common = Directory.GetParent(selectedPath);
            var parent = common is null ? null : Directory.GetParent(common.FullName);
            if (parent is not null) yield return parent.FullName;
        }

        yield return Path.Combine(selectedPath, "data");
        yield return Path.Combine(selectedPath, "raw", "data");
        yield return Path.Combine(selectedPath, "._work", "raw", "data");
    }

    private static int CountCompatibleInputs(string gameDataPath)
    {
        try
        {
            return Directory.EnumerateFiles(gameDataPath, "*.cfg.bin", SearchOption.AllDirectories).Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GlobalLog.Warn("dump_locate_input_count_failed", exception: exception);
            return 0;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed record GameDumpSelection(
    string SelectedPath,
    string RootPath,
    string GameDataPath,
    bool HasPcResources,
    bool HasSwitchResources,
    string VersionEvidence,
    int CompatibleInputCount,
    IReadOnlyList<string> PlatformEvidence);

public sealed record GameDumpValidationDiagnostic(string Field, string Code, string Message);

public sealed record GameDumpValidationResult(
    GameDumpSelection? Selection,
    IReadOnlyList<GameDumpValidationDiagnostic> Diagnostics)
{
    public bool IsValid => Selection is not null && Diagnostics.Count == 0;

    public static GameDumpValidationResult Valid(GameDumpSelection selection) => new(selection, []);

    public static GameDumpValidationResult Invalid(params GameDumpValidationDiagnostic[] diagnostics) => new(null, diagnostics);
}

public enum IndexStage
{
    Validation,
    CfgBinIndexing,
    Localization,
    Assets,
    Completed,
}

public sealed record IndexProgress(IndexStage Stage, int Completed, int Total, string Message);

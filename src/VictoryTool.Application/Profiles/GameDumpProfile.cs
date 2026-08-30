using VictoryTool.Application.Workspaces;

namespace VictoryTool.Application.Profiles;

public sealed record GameDumpProfile(
    string Id,
    string RootPath,
    string GameDataPath,
    string Version,
    bool HasPcResources,
    bool HasSwitchResources)
{
    public static GameDumpProfile Create(string rootPath)
    {
        var result = new GameDumpLocator().Locate(rootPath);
        if (!result.IsValid)
            throw new GameDumpValidationException(result);

        var selection = result.Selection!;
        var normalized = selection.RootPath;
        return new GameDumpProfile(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant(),
            normalized,
            selection.GameDataPath,
            selection.VersionEvidence,
            selection.HasPcResources,
            selection.HasSwitchResources);
    }
}

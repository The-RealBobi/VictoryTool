using VictoryTool.Application.Workspaces;
using VictoryTool.Application.Diagnostics;

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
        using var operation = GlobalLog.BeginOperation("dump_profile_create");
        var result = new GameDumpLocator().Locate(rootPath);
        if (!result.IsValid)
        {
            GlobalLog.Warn("dump_profile_create_rejected", new Dictionary<string, object?>
            {
                ["diagnosticCount"] = result.Diagnostics.Count,
            });
            throw new GameDumpValidationException(result);
        }

        var selection = result.Selection!;
        var normalized = selection.RootPath;
        var profile = new GameDumpProfile(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant(),
            normalized,
            selection.GameDataPath,
            selection.VersionEvidence,
            selection.HasPcResources,
            selection.HasSwitchResources);
        GlobalLog.Info("dump_profile_created", new Dictionary<string, object?>
        {
            ["profileId"] = profile.Id,
            ["hasPcResources"] = profile.HasPcResources,
            ["hasSwitchResources"] = profile.HasSwitchResources,
        });
        return profile;
    }
}

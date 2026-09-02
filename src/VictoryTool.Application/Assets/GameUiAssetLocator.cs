using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Profiles;

namespace VictoryTool.Application.Assets;

public enum GameUiAssetKey
{
    RarityBanner,
    CommonIcons,
    PositionLabels,
    PlayerCardFrames,
    PlayerCardRadar,
}

public static class GameUiAssetLocator
{
    public static string Resolve(GameDumpProfile profile, GameUiAssetKey key, string locale)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (locale.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("The asset locale must be a directory name.", nameof(locale));

        var platform = profile.HasPcResources
            ? "dx11"
            : profile.HasSwitchResources
                ? "nx"
                : throw new InvalidOperationException("The selected dump has no supported menu asset platform.");
        var relativePath = key switch
        {
            GameUiAssetKey.RarityBanner => Path.Combine(
                platform, "menu", "200_icon", "05_icon_rarity", locale, "icon_rarity.g4tx"),
            GameUiAssetKey.CommonIcons => Path.Combine(
                platform, "menu", "200_icon", "15_icon_common", "icon_common.g4tx"),
            GameUiAssetKey.PositionLabels => Path.Combine(
                platform, "menu", "200_icon", "15_icon_common2", locale, "icon_common2.g4tx"),
            GameUiAssetKey.PlayerCardFrames => Path.Combine(
                platform, "menu", "00_soccer", "soccer11", "soccer11_04", locale, "soccer11_04.g4tx"),
            GameUiAssetKey.PlayerCardRadar => Path.Combine(
                platform, "menu", "00_soccer", "soccer11", "soccer11_06", locale, "soccer11_06.g4tx"),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
        var resolvedPath = Path.GetFullPath(Path.Combine(profile.RootPath, relativePath));
        GlobalLog.Debug("ui_asset_resolved", new Dictionary<string, object?>
        {
            ["key"] = key,
            ["locale"] = locale,
            ["platform"] = platform,
        });
        return resolvedPath;
    }
}

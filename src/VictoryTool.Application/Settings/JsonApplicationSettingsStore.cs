using System.Text.Json;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Settings;

public sealed record ApplicationSettings(
    string? GameDumpRoot,
    string LanguageCode,
    double? RosterPaneWidth = null,
    double? PreviewPaneWidth = null)
{
    public static ApplicationSettings Default { get; } = new(null, "en");
}

public interface IApplicationSettingsStore
{
    Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken);
}

public sealed class JsonApplicationSettingsStore(string path) : IApplicationSettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken)
    {
        using var operation = GlobalLog.BeginOperation("settings_load");
        if (!File.Exists(path))
        {
            GlobalLog.Debug("settings_file_missing");
            return ApplicationSettings.Default;
        }
        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, Options, cancellationToken)
            ?? ApplicationSettings.Default;
        GlobalLog.Debug("settings_loaded", new Dictionary<string, object?>
        {
            ["hasDumpRoot"] = !string.IsNullOrWhiteSpace(settings.GameDumpRoot),
            ["language"] = settings.LanguageCode,
        });
        return settings;
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var operation = GlobalLog.BeginOperation("settings_save", new Dictionary<string, object?>
        {
            ["hasDumpRoot"] = !string.IsNullOrWhiteSpace(settings.GameDumpRoot),
            ["language"] = settings.LanguageCode,
        });
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

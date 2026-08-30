using System.Text.Json;

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
        if (!File.Exists(path)) return ApplicationSettings.Default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, Options, cancellationToken)
            ?? ApplicationSettings.Default;
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
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

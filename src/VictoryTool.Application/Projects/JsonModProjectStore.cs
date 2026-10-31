using System.Text.Json;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Application.Projects;

public interface IModProjectStore
{
    Task<ModProjectDocument> LoadAsync(string path, CancellationToken cancellationToken);
    Task SaveAsync(string path, ModProjectDocument project, CancellationToken cancellationToken);
    Task SaveRecoveryAsync(string path, ModProjectDocument project, CancellationToken cancellationToken);
}

public sealed class JsonModProjectStore : IModProjectStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<ModProjectDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        using var operation = GlobalLog.BeginOperation("project_load");
        var fullPath = Path.GetFullPath(path);
        await using var stream = File.OpenRead(fullPath);
        var data = await JsonSerializer.DeserializeAsync<ProjectData>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("The project file is empty.");
        if (data.SchemaVersion != ModProjectDocument.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported project schema version.");
        if (data.Id == Guid.Empty || string.IsNullOrWhiteSpace(data.Name) || data.Batch is null
            || data.Batch.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.PackagePath))
            || data.Drafts?.Any(entry => entry?.Draft?.Fields is null) == true)
            throw new InvalidDataException("The project is missing required identity, batch or draft data.");
        var projectDirectory = Path.GetDirectoryName(fullPath)!;
        var batch = data.Batch.Select(entry => entry with
        {
            PackagePath = Path.IsPathRooted(entry.PackagePath)
                ? Path.GetFullPath(entry.PackagePath)
                : Path.GetFullPath(Path.Combine(projectDirectory, entry.PackagePath)),
        });
        var drafts = (data.Drafts ?? []).Select(entry => entry with
        {
            Draft = entry.Draft with
            {
                Skills = entry.Draft.Skills
                    ?? CharacterDraftSkills.FromLegacyFields(entry.Draft.Fields),
            },
            SourcePackagePath = entry.SourcePackagePath is null
                ? null
                : ResolveProjectPath(projectDirectory, entry.SourcePackagePath),
        });
        var project = new ModProjectDocument(
            data.Id, data.Name, data.SchemaVersion, batch, drafts,
            data.FunctionalBankReferenceDataRoot is null
                ? null
                : ResolveProjectPath(projectDirectory, data.FunctionalBankReferenceDataRoot),
            MapEditorPaths(data.EditorState, value => ResolveProjectPath(projectDirectory, value)));
        GlobalLog.Info("project_loaded", new Dictionary<string, object?>
        {
            ["schemaVersion"] = project.SchemaVersion,
            ["batchCount"] = project.Batch.Count,
            ["draftCount"] = project.Drafts.Count,
        });
        return project;
    }

    public Task SaveAsync(string path, ModProjectDocument project, CancellationToken cancellationToken) =>
        SaveAtomicAsync(path, project, cancellationToken);

    public Task SaveRecoveryAsync(string path, ModProjectDocument project, CancellationToken cancellationToken) =>
        SaveAtomicAsync(path + ".recovery", project, cancellationToken);

    private static async Task SaveAtomicAsync(
        string path,
        ModProjectDocument project,
        CancellationToken cancellationToken)
    {
        using var operation = GlobalLog.BeginOperation("project_save", new Dictionary<string, object?>
        {
            ["batchCount"] = project.Batch.Count,
            ["draftCount"] = project.Drafts.Count,
            ["isRecovery"] = path.EndsWith(".recovery", StringComparison.OrdinalIgnoreCase),
        });
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        var projectDirectory = Path.GetDirectoryName(fullPath)!;
        var portableBatch = project.Batch.Select(entry => entry with
        {
            PackagePath = MakePortablePath(projectDirectory, entry.PackagePath),
        }).ToArray();
        var portableDrafts = project.Drafts.Select(entry => entry with
        {
            SourcePackagePath = entry.SourcePackagePath is null
                ? null
                : MakePortablePath(projectDirectory, entry.SourcePackagePath),
        }).ToArray();
        var data = new ProjectData(
            project.Id, project.Name, project.SchemaVersion, portableBatch, portableDrafts,
            project.FunctionalBankReferenceDataRoot is null
                ? null
                : MakePortablePath(projectDirectory, project.FunctionalBankReferenceDataRoot),
            MapEditorPaths(project.EditorState, value => MakePortablePath(projectDirectory, value)));

        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, data, Options, cancellationToken);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string MakePortablePath(string projectDirectory, string packagePath)
    {
        var relativePath = Path.GetRelativePath(projectDirectory, Path.GetFullPath(packagePath));
        return Path.IsPathRooted(relativePath) ? Path.GetFullPath(packagePath) : relativePath;
    }

    private static string ResolveProjectPath(string projectDirectory, string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectDirectory, path));

    private static ProjectEditorState? MapEditorPaths(ProjectEditorState? editor, Func<string, string> map)
    {
        if (editor is null) return null;
        if (editor.Version != 1) throw new InvalidDataException("Unsupported editor recovery version.");
        if (editor.SavedDrafts is null || editor.SavedDrafts.Any(item => item.Value?.Fields is null || item.Key != item.Value.SymbolicId)
            || editor.ActiveDraft is { Fields: null })
            throw new InvalidDataException("Editor recovery contains an invalid draft.");
        if (editor.SavedOrigins?.Any(pair => pair.Value is null || string.IsNullOrWhiteSpace(pair.Value.PackagePath)
                || pair.Value.Length < 0 || pair.Value.WriteTimeUtcTicks < 0
                || pair.Value.Sha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) == true)
            throw new InvalidDataException("Editor recovery contains an invalid saved origin.");
        return editor with
        {
            SourcePackagePath = string.IsNullOrWhiteSpace(editor.SourcePackagePath) ? null : map(editor.SourcePackagePath),
            LastSavedPackagePath = string.IsNullOrWhiteSpace(editor.LastSavedPackagePath) ? null : map(editor.LastSavedPackagePath),
            GameDumpRoot = string.IsNullOrWhiteSpace(editor.GameDumpRoot) ? null : map(editor.GameDumpRoot),
            SavedOrigins = (editor.SavedOrigins ?? new Dictionary<string, SavedDraftOrigin>())
                .ToDictionary(pair => pair.Key, pair => pair.Value with { PackagePath = map(pair.Value.PackagePath) }),
        };
    }

    private sealed record ProjectData(
        Guid Id,
        string Name,
        int SchemaVersion,
        BatchEntry[] Batch,
        ProjectDraftEntry[]? Drafts = null,
        string? FunctionalBankReferenceDataRoot = null,
        ProjectEditorState? EditorState = null);
}

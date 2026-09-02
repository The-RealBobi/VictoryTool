using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Profiles;

namespace VictoryTool.Application.Exporting;

public enum ModelDependencyKind
{
    Model,
    Mesh,
    ObjectMetadata,
    PackagePart,
    Texture,
}

public sealed record ModelDependency(ModelDependencyKind Kind, string VirtualPath, string SourcePath);

public sealed record ModelDependencyResult(
    IReadOnlyList<ModelDependency> Dependencies,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsComplete => Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

public interface IModelDependencyResolver
{
    ModelDependencyResult Resolve(
        GameDumpProfile profile,
        ExportPlatform platform,
        string modelPath);
}

public sealed class ModelDependencyResolver : IModelDependencyResolver
{
    public ModelDependencyResult Resolve(
        GameDumpProfile profile,
        ExportPlatform platform,
        string modelPath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        using var operation = GlobalLog.BeginOperation("model_dependencies_resolve", new Dictionary<string, object?>
        {
            ["platform"] = platform,
        });

        var diagnostics = new List<Diagnostic>();
        if (modelPath.Contains("..", StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                "export.model_path_not_portable",
                $"Model path '{modelPath}' is not a portable dump-relative reference.",
                "Embed the selected model and its companions in the .vrchara package."));
            GlobalLog.Warn("model_dependency_path_rejected", new Dictionary<string, object?>
            {
                ["reason"] = "parent_segment",
            });
            return new ModelDependencyResult([], diagnostics);
        }

        if (Path.IsPathRooted(modelPath))
        {
            var normalizedAbsolutePath = modelPath.Replace('\\', '/');
            const string commonCharacterMarker = "/common/chr/";
            var marker = normalizedAbsolutePath.IndexOf(commonCharacterMarker, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                diagnostics.Add(Error(
                    "export.model_path_not_portable",
                    $"Model path '{modelPath}' is not a portable dump-relative reference.",
                    "Embed the selected model and its companions in the .vrchara package."));
                GlobalLog.Warn("model_dependency_path_rejected", new Dictionary<string, object?>
                {
                    ["reason"] = "absolute_path_outside_common_chr",
                });
                return new ModelDependencyResult([], diagnostics);
            }

            modelPath = normalizedAbsolutePath[(marker + "/common/".Length)..];
        }

        var commonModelPath = NormalizeCommonModelPath(modelPath);
        var extension = Path.GetExtension(commonModelPath);
        if (!extension.Equals(".g4md", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".g4pkm", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "export.model_format_unsupported",
                $"Model dependency '{modelPath}' is not G4MD or G4PKM.",
                "Select a supported character model."));
            GlobalLog.Warn("model_dependency_format_unsupported");
            return new ModelDependencyResult([], diagnostics);
        }

        var dependencies = new List<ModelDependency>();
        AddRequired(profile, commonModelPath, ModelDependencyKind.Model, "export.model_file_missing", dependencies, diagnostics);

        var commonStem = commonModelPath[..^extension.Length];
        AddRequired(profile, $"{commonStem}.g4mg", ModelDependencyKind.Mesh, "export.model_mesh_missing", dependencies, diagnostics);

        if (extension.Equals(".g4pkm", StringComparison.OrdinalIgnoreCase))
            AddObservedPackageParts(profile, commonStem, dependencies);

        var platformRoot = platform switch
        {
            ExportPlatform.Pc => "dx11",
            ExportPlatform.Switch => "nx",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };
        var characterRelative = commonStem.StartsWith("common/chr/", StringComparison.OrdinalIgnoreCase)
            ? commonStem["common/".Length..]
            : commonStem;
        AddRequired(
            profile,
            $"{platformRoot}/{characterRelative}.g4tx",
            ModelDependencyKind.Texture,
            "export.model_texture_missing",
            dependencies,
            diagnostics);

        GlobalLog.Debug("model_dependencies_resolved", new Dictionary<string, object?>
        {
            ["dependencyCount"] = dependencies.Count,
            ["diagnosticCount"] = diagnostics.Count,
        });
        return new ModelDependencyResult(dependencies, diagnostics);
    }

    private static string NormalizeCommonModelPath(string modelPath)
    {
        var normalized = modelPath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("common/chr/", StringComparison.OrdinalIgnoreCase)) return normalized;
        if (normalized.StartsWith("chr/", StringComparison.OrdinalIgnoreCase)) return $"common/{normalized}";
        return $"common/chr/{normalized}";
    }

    private static void AddRequired(
        GameDumpProfile profile,
        string virtualPath,
        ModelDependencyKind kind,
        string diagnosticCode,
        ICollection<ModelDependency> dependencies,
        ICollection<Diagnostic> diagnostics)
    {
        var sourcePath = ResolveSourcePath(profile, virtualPath);
        if (File.Exists(sourcePath))
        {
            dependencies.Add(new ModelDependency(kind, virtualPath, sourcePath));
            return;
        }

        diagnostics.Add(Error(
            diagnosticCode,
            $"The model dependency '{virtualPath}' is missing from the selected dump.",
            "Select a compatible dump or embed a complete authored model family."));
    }

    private static void AddObservedPackageParts(
        GameDumpProfile profile,
        string commonStem,
        ICollection<ModelDependency> dependencies)
    {
        var virtualDirectory = Path.GetDirectoryName(commonStem.Replace('/', Path.DirectorySeparatorChar));
        var stemName = Path.GetFileName(commonStem);
        if (virtualDirectory is null) return;
        var sourceDirectory = Path.Combine(profile.RootPath, virtualDirectory);
        if (!Directory.Exists(sourceDirectory)) return;

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, $"{stemName}*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(sourcePath);
            var kind = extension.ToLowerInvariant() switch
            {
                ".objbin" => ModelDependencyKind.ObjectMetadata,
                ".g4pk" => ModelDependencyKind.PackagePart,
                _ => (ModelDependencyKind?)null,
            };
            if (kind is null) continue;
            var virtualPath = Path.GetRelativePath(profile.RootPath, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            dependencies.Add(new ModelDependency(kind.Value, virtualPath, sourcePath));
        }
    }

    private static string ResolveSourcePath(GameDumpProfile profile, string virtualPath) =>
        Path.Combine(profile.RootPath, virtualPath.Replace('/', Path.DirectorySeparatorChar));

    private static Diagnostic Error(string code, string message, string recoveryAction) =>
        new(code, DiagnosticSeverity.Error, message, recoveryAction);
}

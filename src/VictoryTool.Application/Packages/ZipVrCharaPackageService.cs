using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Profiles;
using VictoryTool.Application.Assets;
using VictoryTool.Application.Diagnostics;
using VictoryTool.G4.Textures;
using VictoryTool.G4.Animations;

namespace VictoryTool.Application.Packages;

public sealed record VrCharaManifest(
    int FormatVersion,
    CharacterDraft Character,
    IReadOnlyList<CharacterResource> Resources,
    IReadOnlyList<GameResourceReference> GameResources,
    IReadOnlyList<CfgBinPatchSet> Patches,
    IReadOnlyDictionary<string, CharacterPackageLocalization>? Localizations = null,
    string LocalesFile = "locales.json",
    string? ToolVersion = null);

public sealed record CharacterPackageLocalization(
    string LocalizedName,
    string? Description,
    string? RomanizedName,
    string? JapaneseName,
    string? ShortName = null,
    string? UpperName = null);

public sealed record CharacterResource(string VirtualPath, string Checksum, long ByteLength);

public sealed record AuthoredResource(string VirtualPath, ReadOnlyMemory<byte> Content);

public sealed record VrCharaPackage(VrCharaManifest Manifest, IReadOnlyList<AuthoredResource> Resources);

public sealed record GameResourceReference(string VirtualPath);

public sealed record CfgBinPatchSet(string TablePath, IReadOnlyList<CharacterPatchRecord> Records);

public sealed record CharacterPatchRecord(string SymbolicKey, IReadOnlyDictionary<string, string?> Values);

public sealed record PackageValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public interface IVrCharaPackageService
{
    Task<CharacterDraft> LoadAsync(string path, CancellationToken cancellationToken);
    Task SaveAsync(string path, CharacterDraft draft, CancellationToken cancellationToken);
    Task SaveAsync(string path, VrCharaPackage package, CancellationToken cancellationToken);
    Task<VrCharaManifest> LoadManifestAsync(string path, CancellationToken cancellationToken);
    Task<VrCharaPackage> LoadPackageAsync(string path, CancellationToken cancellationToken);
    PackageValidationResult Validate(VrCharaManifest manifest);
    PackageValidationResult ValidateAgainstDump(VrCharaManifest manifest, GameDumpProfile profile);
}

public sealed class ZipVrCharaPackageService : IVrCharaPackageService
{
    private const long MaximumMetadataEntryBytes = 16 * 1024 * 1024;
    private const long MaximumAuthoredResourceBytes = 256 * 1024 * 1024;
    private const long MaximumAuthoredPackageBytes = 768 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<CharacterDraft> LoadAsync(string path, CancellationToken cancellationToken)
    {
        using var operation = GlobalLog.BeginOperation("vrchara_load");
        var package = await LoadPackageAsync(path, cancellationToken);
        var draft = await HydrateAuthoredResourcesAsync(package, cancellationToken);
        GlobalLog.Info("vrchara_loaded", new Dictionary<string, object?>
        {
            ["resourceCount"] = package.Resources.Count,
            ["hasStandardPortrait"] = draft.Assets?.StandardPortraitPath is not null,
            ["hasUniformPortrait"] = draft.Assets?.UniformPortraitPath is not null,
            ["hasHeadModel"] = draft.Models?.HeadModelPath is not null,
        });
        return draft;
    }

    public async Task<VrCharaManifest> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        using var operation = GlobalLog.BeginOperation("vrchara_manifest_load");
        var package = await LoadPackageAsync(path, cancellationToken);
        return package.Manifest;
    }

    public async Task<VrCharaPackage> LoadPackageAsync(string path, CancellationToken cancellationToken)
    {
        using var operation = GlobalLog.BeginOperation("vrchara_package_load");
        using var archive = ZipFile.OpenRead(path);
        var manifests = archive.Entries.Where(entry => entry.FullName == "manifest.json").ToArray();
        if (manifests.Length != 1)
        {
            throw new InvalidDataException("A .vrchara package must contain exactly one manifest.json.");
        }

        var manifestContent = await ReadEntryContentAsync(manifests[0], MaximumMetadataEntryBytes, cancellationToken);
        using var document = ParsePackageJson(manifestContent, "manifest.json");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("manifest.json must contain a JSON object.");
        VrCharaManifest? manifest;
        try
        {
            manifest = document.RootElement.TryGetProperty("formatVersion", out _)
                ? document.RootElement.Deserialize<VrCharaManifest>(Options)
                : MigrateLegacyManifest(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The character manifest has invalid fields.", exception);
        }
        if (manifest is null) throw new InvalidDataException("The character manifest is empty.");
        if (manifest.Character is null || manifest.Character.Fields is null)
            throw new InvalidDataException("The character manifest must contain a character with a fields object.");
        if (manifest.Resources is null || manifest.GameResources is null || manifest.Patches is null)
            throw new InvalidDataException("The character manifest is missing a required resource or patch list.");
        manifest = manifest with { Character = NormalizeLegacyAppearance(manifest.Character, document.RootElement) };
        IReadOnlyDictionary<string, CharacterPackageLocalization>? locales = null;
        if (!string.IsNullOrWhiteSpace(manifest.LocalesFile))
        {
            var matchingLocalesEntries = archive.Entries
                .Where(entry => entry.FullName == manifest.LocalesFile)
                .Take(2)
                .ToArray();
            if (matchingLocalesEntries.Length > 1)
                throw new InvalidDataException($"The package contains duplicate locales entries: {manifest.LocalesFile}");
            var localesEntry = matchingLocalesEntries.SingleOrDefault();
            if (localesEntry is not null)
            {
                var localesContent = await ReadEntryContentAsync(localesEntry, MaximumMetadataEntryBytes, cancellationToken);
                try
                {
                    locales = JsonSerializer.Deserialize<IReadOnlyDictionary<string, CharacterPackageLocalization>>(
                        localesContent, Options);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("The package locales file has invalid JSON.", exception);
                }
            }
        }
        manifest = SynchronizeManifestLocalizations(manifest, locales);
        manifest = manifest with { Character = NormalizeDraft(manifest.Character) };

        var validation = Validate(manifest);
        if (!validation.IsValid) throw new InvalidDataException(string.Join(" ", validation.Errors));
        var requestedResourcePaths = manifest.Resources
            .Select(resource => $"resources/{resource.VirtualPath}")
            .ToHashSet(StringComparer.Ordinal);
        var resourceEntryByPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!requestedResourcePaths.Contains(entry.FullName)) continue;
            if (!resourceEntryByPath.TryAdd(entry.FullName, entry))
                throw new InvalidDataException($"Duplicate authored resource entry: {entry.FullName}");
        }

        var resourceEntries = new List<(CharacterResource Resource, ZipArchiveEntry Entry)>(manifest.Resources.Count);
        foreach (var resource in manifest.Resources)
        {
            var resourcePath = $"resources/{resource.VirtualPath}";
            var entry = resourceEntryByPath.GetValueOrDefault(resourcePath)
                ?? throw new InvalidDataException($"Missing authored resource: {resource.VirtualPath}");
            if (entry.Length != resource.ByteLength)
                throw new InvalidDataException($"Uncompressed length mismatch for authored resource: {resource.VirtualPath}");
            resourceEntries.Add((resource, entry));
        }

        var resources = new List<AuthoredResource>(manifest.Resources.Count);
        foreach (var (resource, entry) in resourceEntries)
        {
            var content = await ReadEntryContentAsync(entry, MaximumAuthoredResourceBytes, cancellationToken);
            if (content.LongLength != resource.ByteLength
                || !string.Equals(Hash(content), resource.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Checksum mismatch for authored resource: {resource.VirtualPath}");
            }
            resources.Add(new AuthoredResource(resource.VirtualPath, content));
        }
        var package = new VrCharaPackage(manifest, resources);
        GlobalLog.Info("vrchara_package_loaded", new Dictionary<string, object?>
        {
            ["formatVersion"] = manifest.FormatVersion,
            ["resourceCount"] = resources.Count,
            ["gameReferenceCount"] = manifest.GameResources.Count,
            ["patchSetCount"] = manifest.Patches.Count,
            ["localizationCount"] = manifest.Localizations?.Count ?? 0,
        });
        return package;
    }

    private static async Task<byte[]> ReadEntryContentAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes || entry.Length > int.MaxValue)
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' exceeds its {maximumBytes / (1024 * 1024)} MiB uncompressed size limit.");
        var content = new byte[(int)entry.Length];
        await using var stream = entry.Open();
        await stream.ReadExactlyAsync(content, cancellationToken);
        var probe = new byte[1];
        if (await stream.ReadAsync(probe, cancellationToken) != 0)
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' expands beyond its declared uncompressed length.");
        return content;
    }

    private static async Task<CharacterDraft> HydrateAuthoredResourcesAsync(
        VrCharaPackage package,
        CancellationToken cancellationToken)
    {
        using var operation = GlobalLog.BeginOperation("vrchara_resources_hydrate", new Dictionary<string, object?>
        {
            ["resourceCount"] = package.Resources.Count,
        });
        var draft = package.Manifest.Character;
        var resourceByPath = package.Resources.ToDictionary(
            resource => resource.VirtualPath,
            StringComparer.OrdinalIgnoreCase);
        var paths = new[]
        {
            draft.Assets?.StandardPortraitPath,
            draft.Assets?.UniformPortraitPath,
            draft.Fields.GetValueOrDefault("Assets.StandardPortraitSourcePath"),
            draft.Fields.GetValueOrDefault("Assets.UniformPortraitSourcePath"),
            draft.Models?.HeadModelPath,
            draft.Models?.BodyModelPath,
            draft.Models?.UniformModelPath,
            draft.Models?.ShoesModelPath,
        }
        .Concat(ExpandModelResourcePaths(draft.Models))
        .Select(path => path ?? string.Empty)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
        var modelDirectories = paths
            .Where(path => path.StartsWith("common/chr/", StringComparison.OrdinalIgnoreCase))
            .Select(GetVirtualDirectory)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        paths = paths
            .Concat(resourceByPath.Keys.Where(path =>
                modelDirectories.Contains(GetVirtualDirectory(path))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!paths.Any(path => !string.IsNullOrWhiteSpace(path) && resourceByPath.ContainsKey(path!)))
        {
            GlobalLog.Debug("vrchara_resources_hydrate_skipped");
            return draft;
        }

        var extractionRoot = Path.Combine(
            Path.GetTempPath(), "VictoryTool", "vrchara", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);
        var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resourceByPath.TryGetValue(path!, out var resource)) continue;
            // Keep the package-relative layout when materialising authored resources.
            // Apart from making diagnostics useful, this prevents two resources with
            // the same filename in different package folders from overwriting each other.
            var destination = Path.Combine(
                extractionRoot,
                resource.VirtualPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, resource.Content.ToArray(), cancellationToken);
            extracted[path!] = destination;
        }

        var assets = draft.Assets;
        var hydratedFields = draft.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (assets is not null)
        {
            var standard = ReplaceHydratedPath(
                draft.Fields.GetValueOrDefault("Assets.StandardPortraitSourcePath"), extracted)
                ?? ReplaceHydratedPath(assets.StandardPortraitPath, extracted);
            var uniform = ReplaceHydratedPath(
                draft.Fields.GetValueOrDefault("Assets.UniformPortraitSourcePath"), extracted)
                ?? ReplaceHydratedPath(assets.UniformPortraitPath, extracted);
            assets = assets with
            {
                StandardPortraitPath = standard,
                UniformPortraitPath = uniform,
            };
            if (standard is not null)
            {
                hydratedFields["Assets.StandardPortraitPath"] = standard;
                hydratedFields["Assets.StandardPortraitSourcePath"] = standard;
            }
            if (uniform is not null)
            {
                hydratedFields["Assets.UniformPortraitPath"] = uniform;
                hydratedFields["Assets.UniformPortraitSourcePath"] = uniform;
            }
        }
        var models = draft.Models;
        if (models is not null)
        {
            models = models with
            {
                HeadModelPath = ReplaceHydratedModelPath(models.HeadModelPath, extracted),
                BodyModelPath = ReplaceHydratedModelPath(models.BodyModelPath, extracted),
                UniformModelPath = ReplaceHydratedModelPath(models.UniformModelPath, extracted),
                ShoesModelPath = ReplaceHydratedModelPath(models.ShoesModelPath, extracted),
            };
        }
        GlobalLog.Info("vrchara_resources_hydrated", new Dictionary<string, object?>
        {
            ["extractedCount"] = extracted.Count,
            ["hasAssets"] = assets is not null,
            ["hasModels"] = models is not null,
        });
        return draft with
        {
            Assets = assets ?? new CharacterDraftAssets(null, null),
            Models = models ?? new CharacterDraftModels(null, null),
            Fields = hydratedFields,
        };
    }

    private static string? ReplaceHydratedPath(
        string? path,
        IReadOnlyDictionary<string, string> extracted) =>
        path is not null && extracted.TryGetValue(path, out var localPath) ? localPath : path;

    private static string GetVirtualDirectory(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static string? ReplaceHydratedModelPath(
        string? path,
        IReadOnlyDictionary<string, string> extracted)
    {
        if (path is null) return null;
        if (extracted.TryGetValue(path, out var localPath)) return localPath;
        var packagePath = ToModelPackagePath(path);
        return packagePath is not null && extracted.TryGetValue(packagePath, out localPath)
            ? localPath
            : path;
    }

    private static IReadOnlyList<string> ExpandModelResourcePaths(CharacterDraftModels? models)
    {
        var paths = new List<string>();
        foreach (var modelPath in new[] { models?.HeadModelPath, models?.BodyModelPath, models?.UniformModelPath, models?.ShoesModelPath })
        {
            var packagePath = ToModelPackagePath(modelPath);
            if (packagePath is null) continue;
            paths.Add(packagePath);
            paths.Add(Path.ChangeExtension(packagePath, ".g4mg"));
            var texturePath = packagePath.Replace("common/chr/", "dx11/chr/", StringComparison.OrdinalIgnoreCase);
            paths.Add(Path.ChangeExtension(texturePath, ".g4tx"));
        }
        return paths;
    }

    private static string? ToModelPackagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("common/chr/", StringComparison.OrdinalIgnoreCase)) return normalized;
        if (normalized.StartsWith("_face/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("_uniform/", StringComparison.OrdinalIgnoreCase))
            return $"common/chr/{normalized}";
        return null;
    }

    public async Task SaveAsync(string path, CharacterDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        using var operation = GlobalLog.BeginOperation("vrchara_draft_save", new Dictionary<string, object?>
        {
            ["isDirty"] = draft.IsDirty,
            ["hasCustomModel"] = draft.Models?.HeadModelPath?.Contains("99_CUSTOM", StringComparison.OrdinalIgnoreCase) == true,
        });
        var (normalizedDraft, authoredResources) = PrepareAuthoredResources(draft);
        var draftLocalization = normalizedDraft.Localization ?? new CharacterDraftLocalization(
            null, null, GameLocaleCatalog.CreateEmptyLocalizations());
        var localizations = draftLocalization.Locales.ToDictionary(
            pair => pair.Key,
            pair => new CharacterPackageLocalization(
                string.IsNullOrWhiteSpace(pair.Value.LocalizedName)
                    ? normalizedDraft.DisplayName
                    : pair.Value.LocalizedName,
                pair.Value.Description,
                pair.Value.RomanizedName,
                pair.Value.JapaneseName,
                pair.Value.ShortName,
                pair.Value.UpperName),
            StringComparer.OrdinalIgnoreCase);
        await SaveAsync(
            path,
            new VrCharaPackage(new VrCharaManifest(
                2,
                normalizedDraft,
                authoredResources.Select(resource => new CharacterResource(
                    resource.VirtualPath,
                    Hash(resource.Content.Span),
                    resource.Content.Length)).ToArray(),
                [],
                [],
                localizations,
                ToolVersion: ApplicationVersion.Current), authoredResources),
            cancellationToken);
        GlobalLog.Info("vrchara_draft_saved", new Dictionary<string, object?>
        {
            ["resourceCount"] = authoredResources.Count,
            ["localizationCount"] = localizations.Count,
        });
    }

    private static (CharacterDraft Draft, IReadOnlyList<AuthoredResource> Resources) PrepareAuthoredResources(CharacterDraft draft)
    {
        var resources = new List<AuthoredResource>();
        var normalized = EnsurePackageInternalName(NormalizeDraft(draft));
        var sourceStandardPortrait = normalized.Assets?.StandardPortraitPath;
        var sourceUniformPortrait = normalized.Assets?.UniformPortraitPath;
        var sourceFields = normalized.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        // A .vrchara owns authored PNGs only.  Never leave a dump G4TX container
        // reference behind: readers may prefer that stale field over the embedded
        // PNG and incorporation would then resolve the wrong portrait (or an
        // absolute path from the machine that created the package).
        foreach (var key in sourceFields.Keys
                     .Where(key => key.Contains("PortraitContainer", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            sourceFields.Remove(key);
        sourceFields.Remove("Assets.PortraitPayloadFormat");
        var hasStandardPng = IsPngPath(sourceStandardPortrait)
            && Path.IsPathRooted(sourceStandardPortrait!)
            && File.Exists(sourceStandardPortrait);
        var hasUniformPng = IsPngPath(sourceUniformPortrait)
            && Path.IsPathRooted(sourceUniformPortrait!)
            && File.Exists(sourceUniformPortrait);
        if (hasStandardPng)
        {
            AddSourcePortraitResource(resources, sourceStandardPortrait, "standard");
            sourceFields["Assets.StandardPortraitPath"] = "assets/portraits/standard.png";
            sourceFields["Assets.StandardPortraitSourcePath"] = "assets/portraits/standard.png";
        }
        else
        {
            sourceFields.Remove("Assets.StandardPortraitPath");
            sourceFields.Remove("Assets.StandardPortraitSourcePath");
        }
        if (hasUniformPng)
        {
            AddSourcePortraitResource(resources, sourceUniformPortrait, "uniform");
            sourceFields["Assets.UniformPortraitPath"] = "assets/portraits/uniform.png";
            sourceFields["Assets.UniformPortraitSourcePath"] = "assets/portraits/uniform.png";
        }
        else
        {
            sourceFields.Remove("Assets.UniformPortraitPath");
            sourceFields.Remove("Assets.UniformPortraitSourcePath");
        }
        normalized = normalized with
        {
            Assets = (normalized.Assets ?? new CharacterDraftAssets(null, null)) with
            {
                StandardPortraitPath = hasStandardPng ? "assets/portraits/standard.png" : null,
                UniformPortraitPath = hasUniformPng ? "assets/portraits/uniform.png" : null,
            },
            Fields = sourceFields,
        };

        normalized = PackCustomModelFamily(
            normalized,
            resources,
            normalized.Models?.HeadModelPath,
            "Models.HeadModelPath",
            "_face",
            "custom head model");
        normalized = PackCustomModelFamily(
            normalized,
            resources,
            normalized.Models?.UniformModelPath,
            "Models.UniformModelPath",
            "_uniform",
            "custom uniform model");
        normalized = PackCustomModelFamily(
            normalized,
            resources,
            normalized.Models?.BodyModelPath,
            "Models.BodyModelPath",
            "_uniform",
            "custom body model");
        normalized = PackCustomModelFamily(
            normalized,
            resources,
            normalized.Models?.ShoesModelPath,
            "Models.ShoesModelPath",
            "_uniform",
            "custom shoes model");
        return (normalized, resources);
    }

    private static CharacterDraft PackCustomModelFamily(
        CharacterDraft draft,
        ICollection<AuthoredResource> resources,
        string? sourcePath,
        string fieldName,
        string resourceCategory,
        string description)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathRooted(sourcePath)) return draft;
        if (!sourcePath.EndsWith(".g4md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"A {description} must be a G4MD file.");

        var fullModelPath = Path.GetFullPath(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(fullModelPath);
        if (string.IsNullOrWhiteSpace(stem) || stem.Contains('/') || stem.Contains('\\'))
            throw new InvalidDataException($"The {description} filename is invalid.");
        var marker = fullModelPath.Replace('\\', '/').IndexOf("/common/chr/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            throw new InvalidDataException($"The {description} must be inside a data/common/chr folder so its platform texture can be found.");

        var meshPath = Path.ChangeExtension(fullModelPath, ".g4mg");
        var texturePath = fullModelPath[..marker] + "/dx11/chr/" + fullModelPath[(marker + "/common/chr/".Length)..];
        texturePath = Path.ChangeExtension(texturePath, ".g4tx");
        var virtualStem = $"common/chr/{resourceCategory}/99_CUSTOM/{stem}/{stem}";
        var files = new[]
        {
            (fullModelPath, virtualStem + ".g4md"),
            (meshPath, virtualStem + ".g4mg"),
            (texturePath, $"dx11/chr/{resourceCategory}/99_CUSTOM/{stem}/{stem}.g4tx"),
        };
        foreach (var file in files)
        {
            if (!File.Exists(file.Item1))
                throw new FileNotFoundException($"The {description} family is missing: {file.Item1}");
            resources.Add(new AuthoredResource(file.Item2, File.ReadAllBytes(file.Item1)));
        }

        foreach (var extension in new[] { ".g4sk", ".objbin", ".clobin" })
        {
            var companionPath = Path.ChangeExtension(fullModelPath, extension);
            if (File.Exists(companionPath))
                resources.Add(new AuthoredResource(virtualStem + extension, File.ReadAllBytes(companionPath)));
        }

        var animationConfigPath = Path.ChangeExtension(fullModelPath, ".g4anim.json");
        var lightweightAnimations = false;
        if (File.Exists(animationConfigPath))
        {
            var profile = JsonSerializer.Deserialize<CharacterAnimationProfile>(
                File.ReadAllText(animationConfigPath), Options)
                ?? throw new InvalidDataException($"The animation config is empty: {animationConfigPath}");
            lightweightAnimations = profile.AvatarRecipe is not null;
            var animationResourceDirectory = virtualStem[..(virtualStem.LastIndexOf('/') + 1)];
            var generatedBanks = (lightweightAnimations ? Enumerable.Empty<string>() : Directory.EnumerateFiles(
                    Path.GetDirectoryName(fullModelPath)!,
                    $"{stem}_from_*_p*.g4mt",
                    SearchOption.TopDirectoryOnly))
                .Select(path =>
                {
                    var fileStem = Path.GetFileNameWithoutExtension(path);
                    var prefix = $"{stem}_from_";
                    var route = fileStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? fileStem[prefix.Length..]
                        : string.Empty;
                    var typeStart = route.LastIndexOf("_p", StringComparison.OrdinalIgnoreCase);
                    var sourceFamily = typeStart > 0 ? route[..typeStart] : string.Empty;
                    var bankType = typeStart >= 0 ? route[(typeStart + 2)..] : string.Empty;
                    if (bankType.Length != 3 || !bankType.All(char.IsAsciiDigit))
                        throw new InvalidDataException($"Generated G4MT bank has an invalid type suffix: {path}");
                    if (sourceFamily.Length == 0 || sourceFamily.Any(character =>
                            !char.IsAsciiLetterOrDigit(character) && character != '_'))
                        throw new InvalidDataException($"Generated G4MT bank has an invalid source-family name: {path}");
                    var sourceResourcePath = $"{sourceFamily}/{sourceFamily}_p{bankType}.g4pk";
                    return new CharacterAnimationBank(sourceResourcePath, bankType,
                        animationResourceDirectory + Path.GetFileName(path),
                        Hash(File.ReadAllBytes(path)));
                })
                .OrderBy(bank => bank.SourceResourcePath, StringComparer.Ordinal)
                .ToArray();
            var generatedPackages = new List<CharacterAnimationPackage>();
            if (!lightweightAnimations && fieldName.Equals("Models.UniformModelPath", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(profile.ModelStem)
                    || profile.ModelStem.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
                    throw new InvalidDataException("The animation config has an invalid model ID for resolving G4PK banks.");
                var dataRoot = fullModelPath[..marker];
                var packageDirectory = Path.Combine(dataRoot, "common", "chr", profile.ModelStem);
                if (Directory.Exists(packageDirectory))
                {
                    var packagePrefix = $"{profile.ModelStem}_p";
                    foreach (var packagePath in Directory.EnumerateFiles(packageDirectory,
                                 $"{profile.ModelStem}_p*.g4pk", SearchOption.TopDirectoryOnly))
                    {
                        var packageName = Path.GetFileNameWithoutExtension(packagePath);
                        var type = packageName.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase)
                            ? packageName[packagePrefix.Length..]
                            : string.Empty;
                        if (type.Length != 3 || !type.All(char.IsAsciiDigit))
                            throw new InvalidDataException($"Generated G4PK bank has an invalid type suffix: {packagePath}");
                        if (new FileInfo(packagePath).Length > MaximumAuthoredResourceBytes)
                            throw new InvalidDataException($"Generated G4PK bank exceeds the 256 MiB package resource limit: {packagePath}");

                        var resourcePath = $"common/chr/{profile.ModelStem}/{Path.GetFileName(packagePath)}";
                        var content = File.ReadAllBytes(packagePath);
                        if (G4pkAnimationPackage.Read(content).Resources.Count != 1)
                            throw new InvalidDataException($"Generated G4PK bank must contain exactly one G4MT resource: {packagePath}");
                        var existingResource = resources.FirstOrDefault(resource =>
                            resource.VirtualPath.Equals(resourcePath, StringComparison.OrdinalIgnoreCase));
                        if (existingResource is null)
                            resources.Add(new AuthoredResource(resourcePath, content));
                        else if (!existingResource.Content.Span.SequenceEqual(content))
                            throw new InvalidDataException($"Conflicting generated G4PK bank: {resourcePath}");
                        generatedPackages.Add(new CharacterAnimationPackage(type, resourcePath, Hash(content)));
                    }
                }
            }
            if (!lightweightAnimations && profile.GeneratedBanks is { Count: > 0 }
                && !profile.GeneratedBanks.OrderBy(bank => bank.SourceResourcePath, StringComparer.Ordinal)
                    .SequenceEqual(generatedBanks))
                throw new InvalidDataException($"The animation config bank manifest does not match the G4MT files beside {fullModelPath}.");
            profile = profile with
            {
                ModelPath = virtualStem + ".g4md",
                SchemaVersion = generatedBanks.Length > 0 || generatedPackages.Count > 0 ? 6 : profile.SchemaVersion,
                GeneratedBanks = generatedBanks,
                GeneratedPackages = generatedPackages,
            };
            ValidateAnimationProfile(profile, stem);
            var targetSkeletonPath = Path.ChangeExtension(fullModelPath, ".g4sk");
            if (File.Exists(targetSkeletonPath)
                && !Hash(File.ReadAllBytes(targetSkeletonPath)).Equals(profile.TargetRig.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The animation config does not match its model G4SK: {animationConfigPath}");
            var existing = draft.AnimationProfiles ?? [];
            var prior = existing.FirstOrDefault(item =>
                item.ModelPath.Equals(profile.ModelPath, StringComparison.OrdinalIgnoreCase));
            if (prior is not null && !prior.TargetRig.Sha256.Equals(profile.TargetRig.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Conflicting animation configs target the same model: {profile.ModelPath}");
            if (prior is null)
                draft = draft with { AnimationProfiles = existing.Append(profile).ToArray() };
        }

        foreach (var companionPath in Directory.EnumerateFiles(
                     Path.GetDirectoryName(fullModelPath)!,
                     $"{stem}_*",
                     SearchOption.TopDirectoryOnly)
                 .Where(path => IsModelCompanion(path)
                     && (!lightweightAnimations || Path.GetExtension(path).ToLowerInvariant() is not (".g4pk" or ".g4mt")))
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var companionName = Path.GetFileName(companionPath);
            resources.Add(new AuthoredResource(
                virtualStem[..(virtualStem.LastIndexOf('/') + 1)] + companionName,
                File.ReadAllBytes(companionPath)));
        }

        var relativeModelPath = $"{resourceCategory}/99_CUSTOM/{stem}/{stem}.g4md";
        var modelFields = draft.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        modelFields[fieldName] = relativeModelPath;
        return draft with
        {
            Models = (draft.Models ?? new CharacterDraftModels(null, null)) with
            {
                HeadModelPath = fieldName == "Models.HeadModelPath" ? relativeModelPath : draft.Models?.HeadModelPath,
                BodyModelPath = fieldName == "Models.BodyModelPath" ? relativeModelPath : draft.Models?.BodyModelPath,
                UniformModelPath = fieldName == "Models.UniformModelPath" ? relativeModelPath : draft.Models?.UniformModelPath,
                ShoesModelPath = fieldName == "Models.ShoesModelPath" ? relativeModelPath : draft.Models?.ShoesModelPath,
            },
            Fields = modelFields,
        };
    }

    private static bool IsModelCompanion(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".g4pk" or ".g4mt" or ".mevbin" or ".objbin";

    private static void AddSourcePortraitResource(
        ICollection<AuthoredResource> resources,
        string? sourcePath,
        string name)
    {
        if (!IsPngPath(sourcePath) || !Path.IsPathRooted(sourcePath) || !File.Exists(sourcePath)) return;
        resources.Add(new AuthoredResource($"assets/portraits/{name}.png", File.ReadAllBytes(sourcePath)));
    }

    private static CharacterDraft AddPngPortraitSources(
        CharacterDraft draft,
        ICollection<AuthoredResource> resources,
        string standardPath,
        string uniformPath)
    {
        AddSourcePortraitResource(resources, standardPath, "standard");
        AddSourcePortraitResource(resources, uniformPath, "uniform");
        var fields = draft.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        fields["Assets.StandardPortraitSourcePath"] = "assets/portraits/standard.png";
        fields["Assets.UniformPortraitSourcePath"] = "assets/portraits/uniform.png";
        fields["Assets.StandardPortraitPath"] = "assets/portraits/standard.png";
        fields["Assets.UniformPortraitPath"] = "assets/portraits/uniform.png";
        return draft with
        {
            Assets = (draft.Assets ?? new CharacterDraftAssets(null, null)) with
            {
                StandardPortraitPath = "assets/portraits/standard.png",
                UniformPortraitPath = "assets/portraits/uniform.png",
            },
            Fields = fields,
        };
    }

    private static CharacterDraft EnsurePackageInternalName(CharacterDraft draft)
    {
        var fields = draft.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (fields.TryGetValue("Advanced.InternalName", out var current)
            && IsCustomInternalName(current))
            return draft;

        if (fields.TryGetValue("Identity.InternalName", out var legacy)
            && IsCustomInternalName(legacy))
        {
            fields["Advanced.InternalName"] = legacy;
            return draft with { Fields = fields };
        }

        Span<byte> randomBytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(randomBytes);
        fields["Advanced.InternalName"] = $"c99{BitConverter.ToUInt32(randomBytes) % 1_000_000u:D6}";
        return draft with { Fields = fields };
    }

    private static bool IsCustomInternalName(string? value) =>
        value is { Length: 9 }
        && value.StartsWith("c99", StringComparison.Ordinal)
        && value.Skip(3).All(char.IsAsciiDigit);

    private static string GetPackagePortraitStem(CharacterDraft draft) =>
        draft.Fields.GetValueOrDefault("Advanced.InternalName")
        ?? throw new InvalidDataException("The package has no custom internal character name.");

    private static void AddPortraitResource(
        ref CharacterDraft draft,
        ICollection<AuthoredResource> resources,
        string? sourcePath,
        string variant)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathRooted(sourcePath)) return;
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException($"The selected portrait is missing: {fullPath}");
        if (!fullPath.EndsWith(".g4tx", StringComparison.OrdinalIgnoreCase)
            && !fullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        var packageVariant = "face";
        var destinationStem = GetPackagePortraitStem(draft);
        var virtualPath = $"dx11/menu/200_icon/10_icon_chr/{packageVariant}/{destinationStem}.g4tx";
        var content = extension == ".png"
            ? PngPortraitG4TxConverter.Convert(
                fullPath,
                FindPortraitTemplate(draft),
                FindPortraitTemplateStem(draft),
                destinationStem)
            : RenamePortraitIdentifier(File.ReadAllBytes(fullPath), destinationStem);
        var entryStem = destinationStem;
        resources.Add(new AuthoredResource(virtualPath, content));
        var assets = draft.Assets ?? new CharacterDraftAssets(null, null);
        var updatedAssets = variant == "face"
            ? assets with { StandardPortraitPath = virtualPath }
            : assets with { UniformPortraitPath = virtualPath };
        var fields = draft.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        fields[variant == "face" ? "Assets.StandardPortraitPath" : "Assets.UniformPortraitPath"] = virtualPath;
        SetPortraitEntryFields(fields, entryStem);
        draft = draft with
        {
            Assets = updatedAssets,
            Fields = fields,
        };
    }

    private static void AddCombinedPortraitResource(
        ref CharacterDraft draft,
        ICollection<AuthoredResource> resources,
        string layerOnePath,
        string layerTwoPath)
    {
        var first = Path.GetFullPath(layerOnePath);
        var second = Path.GetFullPath(layerTwoPath);
        if (!File.Exists(first)) throw new FileNotFoundException($"The selected portrait is missing: {first}");
        if (!File.Exists(second)) throw new FileNotFoundException($"The selected portrait is missing: {second}");
        var destinationStem = GetPackagePortraitStem(draft);
        var virtualPath = $"dx11/menu/200_icon/10_icon_chr/face/{destinationStem}_l.g4tx";
        var template = FindPortraitTemplate(draft);
        var content = PngPortraitG4TxConverter.Convert(
            first,
            second,
            template,
            FindPortraitTemplateStem(draft),
            destinationStem);
        resources.Add(new AuthoredResource(virtualPath, content));
        var assets = draft.Assets ?? new CharacterDraftAssets(null, null);
        var fields = draft.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        fields["Assets.StandardPortraitPath"] = virtualPath;
        fields["Assets.UniformPortraitPath"] = virtualPath;
        SetPortraitEntryFields(fields, destinationStem);
        draft = draft with
        {
            Assets = assets with { StandardPortraitPath = virtualPath, UniformPortraitPath = virtualPath },
            Fields = fields,
        };
    }

    private static bool IsPngPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static void SetPortraitEntryFields(IDictionary<string, string?> fields, string stem)
    {
        fields["Assets.StandardPortraitEntry"] = $"{stem}_1_l00";
        fields["Assets.UniformPortraitEntry"] = $"{stem}_2_l00";
    }

    private static string FindPortraitTemplateStemFromContent(ReadOnlyMemory<byte> content)
    {
        var texture = G4TxDocument.Read(content.Span).Textures.FirstOrDefault()
            ?? throw new InvalidDataException("The portrait G4TX has no textures.");
        var marker = texture.Name.IndexOf("_1_", StringComparison.Ordinal);
        return marker > 0 ? texture.Name[..marker] : texture.Name.Split('_')[0];
    }

    private static byte[] RenamePortraitIdentifier(byte[] content, string destinationStem)
    {
        var sourceStem = FindPortraitTemplateStemFromContent(content);
        return string.Equals(sourceStem, destinationStem, StringComparison.Ordinal)
            ? content
            : G4TxDocument.Read(content).RenameIdentifier(sourceStem, destinationStem);
    }

    private static string FindPortraitTemplate(CharacterDraft draft)
    {
        foreach (var value in draft.Fields.Values)
            if (!string.IsNullOrWhiteSpace(value) && value.EndsWith(".g4tx", StringComparison.OrdinalIgnoreCase) && File.Exists(value))
                return value;
        throw new FileNotFoundException("A two-layer G4TX portrait template is required to export PNG icons.");
    }

    private static string FindPortraitTemplateStem(CharacterDraft draft)
    {
        var template = FindPortraitTemplate(draft);
        var texture = G4TxDocument.Read(File.ReadAllBytes(template)).Textures.FirstOrDefault()
            ?? throw new InvalidDataException("The portrait template has no textures.");
        var marker = texture.Name.IndexOf("_1_", StringComparison.Ordinal);
        return marker > 0 ? texture.Name[..marker] : texture.Name.Split('_')[0];
    }

    public async Task SaveAsync(string path, VrCharaPackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        using var operation = GlobalLog.BeginOperation("vrchara_package_save", new Dictionary<string, object?>
        {
            ["resourceCount"] = package.Resources.Count,
            ["localizationCount"] = package.Manifest.Localizations?.Count ?? 0,
        });
        var manifest = CreateManifest(package);
        var validation = Validate(manifest);
        if (!validation.IsValid)
        {
            GlobalLog.Warn("vrchara_package_save_rejected", new Dictionary<string, object?>
            {
                ["errorCount"] = validation.Errors.Count,
            });
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        }
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var stream = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(stream, manifest, Options, cancellationToken);
                foreach (var resource in package.Resources)
                {
                    var resourceEntry = archive.CreateEntry($"resources/{resource.VirtualPath}", CompressionLevel.Optimal);
                    await using var stream = resourceEntry.Open();
                    await stream.WriteAsync(resource.Content, cancellationToken);
                }
                if (!string.IsNullOrWhiteSpace(manifest.LocalesFile) && manifest.Localizations is not null)
                {
                    var localesEntry = archive.CreateEntry(manifest.LocalesFile, CompressionLevel.Optimal);
                    await using var localesStream = localesEntry.Open();
                    await JsonSerializer.SerializeAsync(localesStream, manifest.Localizations, Options, cancellationToken);
                }
            }

            File.Move(temporaryPath, fullPath, true);
            GlobalLog.Info("vrchara_package_saved", new Dictionary<string, object?>
            {
                ["resourceCount"] = package.Resources.Count,
                ["hasLocalesFile"] = !string.IsNullOrWhiteSpace(manifest.LocalesFile)
                    && manifest.Localizations is not null,
            });
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public PackageValidationResult Validate(VrCharaManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();
        if (manifest.FormatVersion != 2) errors.Add("Unsupported .vrchara manifest version.");
        if (manifest.Character is null) errors.Add("The package must contain one character.");
        foreach (var profile in manifest.Character?.AnimationProfiles ?? [])
        {
            if (profile is null)
            {
                errors.Add("The package contains an empty animation config entry.");
                continue;
            }
            try
            {
                ValidateAnimationProfile(profile, profile.ModelStem);
            }
            catch (InvalidDataException exception)
            {
                errors.Add(exception.Message);
            }
        }
        if (!string.IsNullOrWhiteSpace(manifest.LocalesFile)
            && (!IsSafeVirtualPath(manifest.LocalesFile) || !manifest.LocalesFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            errors.Add($"Invalid locales file path: {manifest.LocalesFile}");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalResourceBytes = 0;
        foreach (var resource in manifest.Resources)
        {
            if (resource is null)
            {
                errors.Add("The package contains an empty authored resource entry.");
                continue;
            }
            if (resource.ByteLength < 0 || resource.ByteLength > MaximumAuthoredPackageBytes - totalResourceBytes)
                errors.Add($"The package's authored resources exceed the 768 MiB uncompressed size limit at '{resource.VirtualPath}'.");
            else
                totalResourceBytes += resource.ByteLength;
            if (!IsSafeVirtualPath(resource.VirtualPath))
                errors.Add($"Invalid authored resource path: {resource.VirtualPath}");
            else if (!paths.Add(resource.VirtualPath))
                errors.Add($"Duplicate authored resource path: {resource.VirtualPath}");
            if (resource.VirtualPath.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase))
                errors.Add("Complete CFGBIN files cannot be embedded in a .vrchara package.");
            if (resource.VirtualPath.EndsWith(".g4md", StringComparison.OrdinalIgnoreCase)
                && !resource.VirtualPath.StartsWith("common/chr/_face/99_CUSTOM/", StringComparison.Ordinal)
                && !resource.VirtualPath.StartsWith("common/chr/_uniform/99_CUSTOM/", StringComparison.Ordinal))
                errors.Add("Custom model resources must use a supported 99_CUSTOM face or uniform path.");
        }
        var resourceByPath = (manifest.Resources ?? [])
            .Where(resource => resource is not null && !string.IsNullOrWhiteSpace(resource.VirtualPath))
            .GroupBy(resource => resource.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var profile in manifest.Character?.AnimationProfiles ?? [])
        foreach (var bank in profile?.GeneratedBanks ?? [])
        {
            if (bank is null || !resourceByPath.TryGetValue(bank.ResourcePath, out var resource)
                || !resource.Checksum.Equals(bank.Sha256, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Generated G4MT bank '{bank?.ResourcePath}' is missing or its checksum differs from the animation config.");
        }
        foreach (var profile in manifest.Character?.AnimationProfiles ?? [])
        foreach (var package in profile?.GeneratedPackages ?? [])
        {
            if (package is null || !resourceByPath.TryGetValue(package.ResourcePath, out var resource)
                || !resource.Checksum.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Generated G4PK bank '{package?.ResourcePath}' is missing or its checksum differs from the animation config.");
        }
        foreach (var reference in manifest.GameResources)
        {
            if (reference is null)
            {
                errors.Add("The package contains an empty game resource reference.");
                continue;
            }
            if (!IsSafeVirtualPath(reference.VirtualPath))
                errors.Add($"Invalid game resource reference: {reference.VirtualPath}");
        }
        foreach (var patchSet in manifest.Patches)
        {
            if (patchSet is null)
            {
                errors.Add("The package contains an empty CFGBIN patch set.");
                continue;
            }
            if (!IsSafeVirtualPath(patchSet.TablePath)
                || !patchSet.TablePath.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase)
                || !(patchSet.TablePath.StartsWith("common/gamedata/", StringComparison.OrdinalIgnoreCase)
                     || patchSet.TablePath.StartsWith("common/text/", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Invalid CFGBIN patch table path: {patchSet.TablePath}");
            }
            if (patchSet.Records is null)
            {
                errors.Add($"CFGBIN patch set '{patchSet.TablePath}' has no record list.");
                continue;
            }
            foreach (var record in patchSet.Records)
            {
                if (record is null)
                {
                    errors.Add($"CFGBIN patch set '{patchSet.TablePath}' contains an empty record.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(record.SymbolicKey))
                    errors.Add($"CFGBIN patch set '{patchSet.TablePath}' contains a record without a symbolic key.");
                if (record.Values is null)
                    errors.Add($"CFGBIN patch record '{record.SymbolicKey}' has no values.");
            }
        }
        if (manifest.Localizations is not null)
        {
            foreach (var localization in manifest.Localizations)
            {
                if (localization.Value is null)
                {
                    errors.Add($"Localization '{localization.Key}' has no value.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(localization.Key)
                    || localization.Key.Contains('/')
                    || localization.Key.Contains('\\'))
                    errors.Add($"Invalid localization locale: {localization.Key}");
                if (string.IsNullOrWhiteSpace(localization.Value.LocalizedName))
                    errors.Add($"Localization '{localization.Key}' requires a localized name.");
            }
        }
        if (errors.Count > 0)
            GlobalLog.Debug("vrchara_validation_failed", new Dictionary<string, object?>
            {
                ["errorCount"] = errors.Count,
            });
        return new PackageValidationResult(errors);
    }

    private static void ValidateAnimationProfile(CharacterAnimationProfile profile, string expectedStem)
    {
        if (profile is null || profile.SourceRig is null || profile.TargetRig is null
            || profile.Joints is null || profile.GeneratedBanks is null || profile.GeneratedPackages is null
            || string.IsNullOrWhiteSpace(profile.ModelStem)
            || profile.SchemaVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7) || profile.Producer != "G4_Blender"
            || !profile.ModelStem.Equals(expectedStem, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported or mismatched animation config for model '{expectedStem}'.");
        if (!IsSha256(profile.SourceRig.Sha256) || !IsSha256(profile.TargetRig.Sha256))
            throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid rig hash.");
        if (!IsSafeVirtualPath(profile.ModelPath)
            || !profile.ModelPath.EndsWith(".g4md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid model path.");
        if (profile.SourceModelStem is not null
            && (profile.SourceModelStem.Length == 0
                || profile.SourceModelStem.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
            throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid source-model identity.");
        if (profile.AvatarRecipe is { } recipe)
        {
            if (profile.SchemaVersion != 7 || string.IsNullOrWhiteSpace(profile.SourceModelStem)
                || profile.GeneratedBanks.Count != 0 || profile.GeneratedPackages.Count != 0)
                throw new InvalidDataException("Lightweight animation recipes require schema 7 without generated banks.");
            recipe.Validate();
            if (!Hash(Convert.FromBase64String(recipe.SourceSkeletonBase64)).Equals(profile.SourceRig.Sha256, StringComparison.OrdinalIgnoreCase)
                || !Hash(Convert.FromBase64String(recipe.TargetSkeletonBase64)).Equals(profile.TargetRig.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The animation recipe skeletons do not match the profile rig hashes.");
        }
        else if (profile.SchemaVersion == 7)
            throw new InvalidDataException("Animation schema 7 requires an avatar recipe.");
        if (profile.Joints.Count == 0)
            throw new InvalidDataException($"Animation config for '{expectedStem}' has no joints.");
        if ((profile.GeneratedBanks.Count > 0 || profile.GeneratedPackages.Count > 0) && profile.SchemaVersion < 6)
            throw new InvalidDataException($"Animation config for '{expectedStem}' uses generated banks without schema version 6.");
        var sourceBankPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bankPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bank in profile.GeneratedBanks)
        {
            if (bank is null || string.IsNullOrWhiteSpace(bank.Type) || bank.Type.Length != 3
                || !bank.Type.All(char.IsAsciiDigit) || !IsSha256(bank.Sha256)
                || string.IsNullOrWhiteSpace(bank.SourceResourcePath) || !IsSafeVirtualPath(bank.SourceResourcePath)
                || !HasAnimationBankRoute(bank.SourceResourcePath, bank.Type)
                || !sourceBankPaths.Add(bank.SourceResourcePath)
                || string.IsNullOrWhiteSpace(bank.ResourcePath) || !IsSafeVirtualPath(bank.ResourcePath)
                || !bank.ResourcePath.EndsWith($"_from_{Path.GetFileName(Path.GetDirectoryName(bank.SourceResourcePath))}_p{bank.Type}.g4mt", StringComparison.OrdinalIgnoreCase)
                || !bankPaths.Add(bank.ResourcePath))
                throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid or duplicate generated G4MT bank.");
        }
        var packagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in profile.GeneratedPackages)
        {
            if (package is null || string.IsNullOrWhiteSpace(package.Type)
                || package.Type.Length != 3 || !package.Type.All(char.IsAsciiDigit)
                || !IsSha256(package.Sha256) || !IsSafeVirtualPath(package.ResourcePath)
                || !package.ResourcePath.EndsWith($"/{profile.ModelStem}_p{package.Type}.g4pk", StringComparison.OrdinalIgnoreCase)
                || !packagePaths.Add(package.ResourcePath) || !packageTypes.Add(package.Type))
                throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid or duplicate generated G4PK bank.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceNamesByHash = new Dictionary<uint, string>();
        foreach (var joint in profile.Joints)
        {
            if (joint is null || string.IsNullOrWhiteSpace(joint.Name) || !names.Add(joint.Name)
                || joint.SourceRest is null || joint.TargetRest is null)
                throw new InvalidDataException($"Animation config for '{expectedStem}' has an empty or duplicate joint name.");
            ValidateTransform(joint.SourceRest, expectedStem, joint.Name);
            ValidateTransform(joint.TargetRest, expectedStem, joint.Name);
            if (profile.SchemaVersion >= 5)
            {
                if ((joint.SourceName is null) != (joint.SourceNameCrc32 is null)
                    || (joint.SourceName is not null && string.IsNullOrWhiteSpace(joint.SourceName)))
                    throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid source-joint mapping for '{joint.Name}'.");
                if (joint.SourceName is { } sourceName)
                {
                    var sourceHash = joint.SourceNameCrc32!.Value;
                    if (sourceNamesByHash.TryGetValue(sourceHash, out var priorName)
                        && !priorName.Equals(sourceName, StringComparison.Ordinal))
                        throw new InvalidDataException($"Animation config for '{expectedStem}' has conflicting source names for joint hash {sourceHash}.");
                    sourceNamesByHash.TryAdd(sourceHash, sourceName);
                }
            }
            if (joint.MotionBasisRotation is not null)
            {
                if (profile.SchemaVersion < 3 || joint.MotionBasisRotation.Count != 4
                    || joint.MotionBasisRotation.Any(value => !float.IsFinite(value))
                    || joint.MotionBasisRotation.Sum(value => value * value) <= float.Epsilon)
                    throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid motion-basis rotation for joint '{joint.Name}'.");
            }
        }
        foreach (var joint in profile.Joints)
            if (joint.Parent is not null && (!names.Contains(joint.Parent) || joint.Parent.Equals(joint.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid parent for joint '{joint.Name}'.");
        var sourceNames = profile.Joints
            .Where(joint => joint.SourceName is not null)
            .Select(joint => joint.SourceName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ValidateAnimationHierarchy(profile.Joints, joint => joint.Parent, expectedStem, "target");
        if (profile.SchemaVersion < 4)
        {
            if (profile.Joints.Any(joint => joint.SourceParent is not null))
                throw new InvalidDataException($"Animation config for '{expectedStem}' uses source parents without schema version 4.");
            ValidateAnimationHierarchy(profile.Joints, joint => joint.Parent, expectedStem, "source");
        }
        else if (profile.AvatarRecipe is null)
        {
            var targetBasedParents = profile.Joints.All(joint => joint.SourceParent is null || names.Contains(joint.SourceParent));
            foreach (var joint in profile.Joints)
                if (joint.SourceParent is not null
                    && (!(targetBasedParents ? names : sourceNames).Contains(joint.SourceParent)
                        || joint.SourceParent.Equals(targetBasedParents ? joint.Name : joint.SourceName ?? joint.Name,
                            StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Animation config for '{expectedStem}' has an invalid source parent for joint '{joint.Name}'.");
            if (targetBasedParents)
                ValidateAnimationHierarchy(profile.Joints, joint => joint.SourceParent, expectedStem, "source");
            else
                ValidateSourceAnimationHierarchy(profile.Joints, expectedStem);
        }
    }

    private static bool HasAnimationBankRoute(string resourcePath, string type)
    {
        var parts = resourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && parts[0].Length > 0
            && parts[1].Equals($"{parts[0]}_p{type}.g4pk", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateAnimationHierarchy(IReadOnlyList<CharacterAnimationJoint> joints,
        Func<CharacterAnimationJoint, string?> parentOf, string stem, string label)
    {
        var byName = joints.ToDictionary(joint => joint.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var joint in joints)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = joint;
            while (true)
            {
                if (!visited.Add(current.Name))
                    throw new InvalidDataException($"Animation config for '{stem}' has a {label} hierarchy cycle at '{current.Name}'.");
                var parent = parentOf(current);
                if (parent is null) break;
                current = byName[parent];
            }
        }
    }

    private static void ValidateSourceAnimationHierarchy(
        IReadOnlyList<CharacterAnimationJoint> joints, string stem)
    {
        var parentsByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var joint in joints)
        {
            if (joint.SourceName is null)
                continue;
            if (parentsByName.TryGetValue(joint.SourceName, out var existingParent)
                && !string.Equals(existingParent, joint.SourceParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Animation config for '{stem}' has conflicting source parents for joint '{joint.SourceName}'.");
            parentsByName[joint.SourceName] = joint.SourceParent;
        }

        foreach (var name in parentsByName.Keys)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? current = name;
            while (current is not null && parentsByName.TryGetValue(current, out var parent))
            {
                if (!visited.Add(current))
                    throw new InvalidDataException($"Animation config for '{stem}' has a source hierarchy cycle at '{current}'.");
                current = parent;
            }
        }
    }

    private static void ValidateTransform(CharacterAnimationTransform transform, string stem, string joint)
    {
        if (transform.Translation is null || transform.Rotation is null || transform.Scale is null
            || transform.Translation.Count != 3 || transform.Rotation.Count != 4 || transform.Scale.Count != 3
            || transform.Translation.Concat(transform.Rotation).Concat(transform.Scale).Any(value => !float.IsFinite(value)))
            throw new InvalidDataException($"Animation config for '{stem}' has an invalid transform for joint '{joint}'.");
        var rotationLengthSquared = transform.Rotation.Sum(value => value * value);
        if (!float.IsFinite(rotationLengthSquared) || rotationLengthSquared <= float.Epsilon)
            throw new InvalidDataException($"Animation config for '{stem}' has an invalid rotation quaternion for joint '{joint}'.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public PackageValidationResult ValidateAgainstDump(VrCharaManifest manifest, GameDumpProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<string>(Validate(manifest).Errors);
        foreach (var reference in manifest.GameResources)
        {
            if (!IsSafeVirtualPath(reference.VirtualPath)) continue;
            var relativePath = reference.VirtualPath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(profile.RootPath, relativePath);
            if (!File.Exists(fullPath))
                errors.Add($"Missing referenced game resource: {reference.VirtualPath}");
        }
        if (errors.Count > 0)
            GlobalLog.Debug("vrchara_dump_validation_failed", new Dictionary<string, object?>
            {
                ["errorCount"] = errors.Count,
            });
        return new PackageValidationResult(errors);
    }

    private static VrCharaManifest? MigrateLegacyManifest(JsonElement root)
    {
        try
        {
            var draft = root.Deserialize<CharacterDraft>(Options);
            return draft is null ? null : new VrCharaManifest(2, draft, [], [], []);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The legacy character manifest has invalid fields.", exception);
        }
    }

    private static JsonDocument ParsePackageJson(byte[] content, string entryName)
    {
        try
        {
            return JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The package entry '{entryName}' contains invalid JSON.", exception);
        }
    }

    private static CharacterDraft NormalizeDraft(CharacterDraft draft)
    {
        var fields = new Dictionary<string, string?>(draft.Fields, StringComparer.Ordinal);
        var models = draft.Models ?? new CharacterDraftModels(null, null);
        models = SynchronizeModelFields(fields, models);
        var skills = draft.Skills ?? CharacterDraftSkills.FromLegacyFields(draft.Fields);
        var variants = CharacterRarityCatalog.EnsureDraftVariants(draft.Variants);
        if (variants is not { Count: > 0 })
            return draft with { Fields = fields, Models = models, Skills = skills, Variants = variants };

        // Older packages kept the edited primary gameplay row only in the
        // top-level mirror.  Treat that mirror as authoritative for the
        // delivered (rarity-0) row so re-opening and exporting such a package
        // cannot silently restore the old element or gameplay values.
        var primaryIndex = variants
            .Select((variant, index) => (variant, index))
            .FirstOrDefault(item => (item.variant.Gameplay.SpecialRarity ?? 0) == 0)
            .index;
        if ((uint)primaryIndex >= (uint)variants.Count)
            primaryIndex = 0;
        var primary = variants[primaryIndex];
        var topLevel = draft.Gameplay ?? primary.Gameplay;
        var hasEditedGameplayMirror = !string.IsNullOrWhiteSpace(topLevel.Affinity)
            && (!string.Equals(topLevel.Affinity, "Neutral", StringComparison.OrdinalIgnoreCase)
                || topLevel.MainPosition is not null
                || topLevel.SubPosition is not null
                || topLevel.PlayStyle is not null
                || topLevel.Growth is not null
                || topLevel.Rank is not null
                || topLevel.AbilityBoardId is not null);
        var primarySkills = draft.Skills is { Slots: var authoredSlots }
            && authoredSlots.Any(slot => slot.SkillId is not null || slot.UnlockLevel is not null)
            ? draft.Skills
            : primary.Skills;
        var mergedPrimary = primary with
        {
            Skills = primarySkills,
            Gameplay = hasEditedGameplayMirror
                ? primary.Gameplay with
                {
                    Affinity = topLevel.Affinity,
                    MainPosition = topLevel.MainPosition ?? primary.Gameplay.MainPosition,
                    SubPosition = topLevel.SubPosition ?? primary.Gameplay.SubPosition,
                    PlayStyle = topLevel.PlayStyle ?? primary.Gameplay.PlayStyle,
                    Growth = topLevel.Growth ?? primary.Gameplay.Growth,
                    Rank = topLevel.Rank ?? primary.Gameplay.Rank,
                    AbilityBoardId = topLevel.AbilityBoardId ?? primary.Gameplay.AbilityBoardId,
                    SpecialRarity = primary.Gameplay.SpecialRarity,
                    RegistrationProfile = topLevel.RegistrationProfile,
                }
                : primary.Gameplay,
        };
        var normalizedVariants = variants.ToArray();
        normalizedVariants[primaryIndex] = mergedPrimary;
        return draft with { Fields = fields, Models = models, Skills = skills, Variants = normalizedVariants };
    }

    private static CharacterDraftModels SynchronizeModelFields(
        IDictionary<string, string?> fields,
        CharacterDraftModels models)
    {
        models = models with
        {
            HeadModelPath = models.HeadModelPath ?? GetModelField(fields, "Models.HeadModelPath"),
            BodyModelPath = models.BodyModelPath ?? GetModelField(fields, "Models.BodyModelPath"),
            UniformModelPath = models.UniformModelPath ?? GetModelField(fields, "Models.UniformModelPath"),
            ShoesModelPath = models.ShoesModelPath ?? GetModelField(fields, "Models.ShoesModelPath"),
            SkinColorRgba = models.SkinColorRgba ?? GetModelField(fields, "Models.SkinColorRgba"),
            UniformModel = models.UniformModel
                ?? ParseNullableInt(GetModelField(fields, "Models.UniformModel")),
            ShoesModel = models.ShoesModel
                ?? ParseNullableInt(GetModelField(fields, "Models.ShoesModel")),
            GloveModel = models.GloveModel
                ?? ParseNullableInt(GetModelField(fields, "Models.GloveModel")),
            EquipmentColor = models.EquipmentColor
                ?? ParseNullableInt(GetModelField(fields, "Models.EquipmentColor")),
            UniformCollarOpen = models.UniformCollarOpen
                ?? ParseNullableInt(GetModelField(fields, "Models.UniformCollarOpen"))
                ?? ParseNullableInt(GetModelField(fields, "Models.EquipmentFlag1")),
            EquipmentFlag2 = models.EquipmentFlag2
                ?? ParseNullableInt(GetModelField(fields, "Models.EquipmentFlag2")),
            ChestSize = models.ChestSize
                ?? ParseNullableInt(GetModelField(fields, "Models.ChestSize"))
                ?? ParseNullableInt(GetModelField(fields, "Models.BoobSize")),
            ForceKit = models.ForceKit
                ?? ParseNullableInt(GetModelField(fields, "Models.ForceKit")),
        };

        SetModelField(fields, "Models.HeadModelPath", models.HeadModelPath);
        SetModelField(fields, "Models.BodyModelPath", models.BodyModelPath);
        SetModelField(fields, "Models.UniformModelPath", models.UniformModelPath);
        SetModelField(fields, "Models.ShoesModelPath", models.ShoesModelPath);
        SetModelField(fields, "Models.SkinColorRgba", models.SkinColorRgba);
        SetModelField(fields, "Models.UniformModel", models.UniformModel);
        SetModelField(fields, "Models.ShoesModel", models.ShoesModel);
        SetModelField(fields, "Models.GloveModel", models.GloveModel);
        SetModelField(fields, "Models.EquipmentColor", models.EquipmentColor);
        SetModelField(fields, "Models.UniformCollarOpen", models.UniformCollarOpen);
        SetModelField(fields, "Models.EquipmentFlag2", models.EquipmentFlag2);
        SetModelField(fields, "Models.ChestSize", models.ChestSize);
        SetModelField(fields, "Models.ForceKit", models.ForceKit);
        fields.Remove("Models.EquipmentFlag1");
        fields.Remove("Models.BoobSize");
        return models;
    }

    private static void SetModelField(
        IDictionary<string, string?> fields,
        string key,
        object? value)
    {
        if (value is null) return;
        fields[key] = value switch
        {
            int integer => integer.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static string? GetModelField(
        IDictionary<string, string?> fields,
        string key) => fields.TryGetValue(key, out var value) ? value : null;

    private static int? ParseNullableInt(string? value) =>
        TryParseInt(value, out var parsed) ? parsed : null;

    private static CharacterDraft NormalizeLegacyAppearance(CharacterDraft draft, JsonElement root)
    {
        var character = root.TryGetProperty("character", out var characterElement)
            ? characterElement
            : root;
        if (!character.TryGetProperty("models", out var modelsElement)) return draft;

        var models = draft.Models ?? new CharacterDraftModels(null, null);
        var fields = new Dictionary<string, string?>(draft.Fields, StringComparer.Ordinal);
        if (models.ChestSize is null
            && TryGetIntProperty(modelsElement, out var chestSize, "chestSize", "boobSize"))
        {
            models = models with { ChestSize = chestSize };
            fields["Models.ChestSize"] = chestSize.ToString(CultureInfo.InvariantCulture);
        }
        if (models.UniformCollarOpen is null
            && TryGetIntProperty(modelsElement, out var collarOpen, "uniformCollarOpen", "equipmentFlag1"))
        {
            models = models with { UniformCollarOpen = collarOpen };
            fields["Models.UniformCollarOpen"] = collarOpen.ToString(CultureInfo.InvariantCulture);
        }
        return draft with { Fields = fields, Models = models };
    }

    private static bool TryGetIntProperty(JsonElement element, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out value)) return true;
        }
        value = default;
        return false;
    }

    private static bool TryParseInt(string? value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static bool IsSafeVirtualPath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.Any(char.IsControl)
        && !Path.IsPathRooted(path)
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.Contains(':')
        && !path.Contains('\\');

    private static VrCharaManifest CreateManifest(VrCharaPackage package)
    {
        var resources = package.Resources.Select(resource => new CharacterResource(
            resource.VirtualPath,
            Hash(resource.Content.Span),
            resource.Content.Length)).ToArray();
        var manifest = package.Manifest with
        {
            FormatVersion = 2,
            Resources = resources,
            ToolVersion = ApplicationVersion.Current,
        };
        var localizations = manifest.Localizations is { Count: > 0 }
            ? manifest.Localizations
            : ToPackageLocalizations(manifest.Character.Localization, manifest.Character.DisplayName);
        return SynchronizeManifestLocalizations(manifest, localizations);
    }

    private static VrCharaManifest SynchronizeManifestLocalizations(
        VrCharaManifest manifest,
        IReadOnlyDictionary<string, CharacterPackageLocalization>? localizations)
    {
        var canonical = localizations is null
            ? new Dictionary<string, CharacterPackageLocalization>(StringComparer.OrdinalIgnoreCase)
            : localizations.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        if (canonical.Count == 0)
            return manifest;

        var current = manifest.Character.Localization ?? new CharacterDraftLocalization(
            null,
            null,
            GameLocaleCatalog.CreateEmptyLocalizations());
        var values = canonical.ToDictionary(
            pair => pair.Key,
            pair => new CharacterDraftLocalizedText(
                pair.Value.LocalizedName,
                pair.Value.Description,
                pair.Value.RomanizedName,
                pair.Value.JapaneseName,
                pair.Value.ShortName,
                pair.Value.UpperName),
            StringComparer.OrdinalIgnoreCase);

        return manifest with
        {
            Character = manifest.Character with
            {
                Localization = current with { LocaleValues = values },
            },
            Localizations = canonical,
        };
    }

    private static IReadOnlyDictionary<string, CharacterPackageLocalization> ToPackageLocalizations(
        CharacterDraftLocalization? localization,
        string fallbackDisplayName)
    {
        var values = localization?.Locales ?? GameLocaleCatalog.CreateEmptyLocalizations();
        return values.ToDictionary(
            pair => pair.Key,
            pair => new CharacterPackageLocalization(
                string.IsNullOrWhiteSpace(pair.Value.LocalizedName)
                    ? fallbackDisplayName
                    : pair.Value.LocalizedName,
                pair.Value.Description,
                pair.Value.RomanizedName,
                pair.Value.JapaneseName,
                pair.Value.ShortName,
                pair.Value.UpperName),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));
}

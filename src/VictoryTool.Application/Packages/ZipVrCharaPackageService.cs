using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Profiles;
using VictoryTool.Application.Assets;
using VictoryTool.G4.Textures;

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
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<CharacterDraft> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var package = await LoadPackageAsync(path, cancellationToken);
        return await HydrateAuthoredResourcesAsync(package, cancellationToken);
    }

    public async Task<VrCharaManifest> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        var package = await LoadPackageAsync(path, cancellationToken);
        return package.Manifest;
    }

    public async Task<VrCharaPackage> LoadPackageAsync(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifests = archive.Entries.Where(entry => entry.FullName == "manifest.json").ToArray();
        if (manifests.Length != 1)
        {
            throw new InvalidDataException("A .vrchara package must contain exactly one manifest.json.");
        }

        await using var stream = manifests[0].Open();
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var manifest = document.RootElement.TryGetProperty("formatVersion", out _)
            ? document.RootElement.Deserialize<VrCharaManifest>(Options)
            : MigrateLegacyManifest(document.RootElement);
        if (manifest is null) throw new InvalidDataException("The character manifest is empty.");
        manifest = manifest with { Character = NormalizeLegacyAppearance(manifest.Character, document.RootElement) };
        IReadOnlyDictionary<string, CharacterPackageLocalization>? locales = null;
        if (!string.IsNullOrWhiteSpace(manifest.LocalesFile))
        {
            var localesEntry = archive.GetEntry(manifest.LocalesFile);
            if (localesEntry is not null)
            {
                await using var localesStream = localesEntry.Open();
                locales = await JsonSerializer.DeserializeAsync<IReadOnlyDictionary<string, CharacterPackageLocalization>>(
                    localesStream, Options, cancellationToken);
            }
        }
        manifest = SynchronizeManifestLocalizations(manifest, locales);
        manifest = manifest with { Character = NormalizeDraft(manifest.Character) };

        var validation = Validate(manifest);
        if (!validation.IsValid) throw new InvalidDataException(string.Join(" ", validation.Errors));
        var resources = new List<AuthoredResource>(manifest.Resources.Count);
        foreach (var resource in manifest.Resources)
        {
            var entry = archive.GetEntry($"resources/{resource.VirtualPath}")
                ?? throw new InvalidDataException($"Missing authored resource: {resource.VirtualPath}");
            await using var resourceStream = entry.Open();
            await using var memory = new MemoryStream();
            await resourceStream.CopyToAsync(memory, cancellationToken);
            var content = memory.ToArray();
            if (content.LongLength != resource.ByteLength
                || !string.Equals(Hash(content), resource.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Checksum mismatch for authored resource: {resource.VirtualPath}");
            }
            resources.Add(new AuthoredResource(resource.VirtualPath, content));
        }
        return new VrCharaPackage(manifest, resources);
    }

    private static async Task<CharacterDraft> HydrateAuthoredResourcesAsync(
        VrCharaPackage package,
        CancellationToken cancellationToken)
    {
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
        }
        .Concat(ExpandModelResourcePaths(draft.Models))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
        if (!paths.Any(path => !string.IsNullOrWhiteSpace(path) && resourceByPath.ContainsKey(path!)))
            return draft;

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
            };
        }
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
        foreach (var modelPath in new[] { models?.HeadModelPath, models?.BodyModelPath })
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

        var sourcePath = normalized.Models?.HeadModelPath;
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathRooted(sourcePath))
            return (normalized, resources);
        if (!sourcePath.EndsWith(".g4md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A custom head model must be a G4MD file.");

        var fullModelPath = Path.GetFullPath(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(fullModelPath);
        if (string.IsNullOrWhiteSpace(stem) || stem.Contains('/') || stem.Contains('\\'))
            throw new InvalidDataException("The custom head model filename is invalid.");
        var meshPath = Path.ChangeExtension(fullModelPath, ".g4mg");
        var marker = fullModelPath.Replace('\\', '/').IndexOf("/common/chr/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            throw new InvalidDataException("The custom head model must be inside a data/common/chr folder so its platform texture can be found.");
        var texturePath = fullModelPath[..marker] + "/dx11/chr/" + fullModelPath[(marker + "/common/chr/".Length)..];
        texturePath = Path.ChangeExtension(texturePath, ".g4tx");
        var virtualStem = $"common/chr/_face/99_CUSTOM/{stem}/{stem}";
        var files = new[]
        {
            (fullModelPath, virtualStem + ".g4md"),
            (meshPath, virtualStem + ".g4mg"),
            (texturePath, $"dx11/chr/_face/99_CUSTOM/{stem}/{stem}.g4tx"),
        };
        resources.AddRange(files.Select(file =>
        {
            if (!File.Exists(file.Item1))
                throw new FileNotFoundException($"The custom model family is missing: {file.Item1}");
            return new AuthoredResource(file.Item2, File.ReadAllBytes(file.Item1));
        }));
        var relativeModelPath = $"_face/99_CUSTOM/{stem}/{stem}.g4md";
        var modelFields = normalized.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        modelFields["Models.HeadModelPath"] = relativeModelPath;
        normalized = normalized with
        {
            Models = (normalized.Models ?? new CharacterDraftModels(null, null)) with { HeadModelPath = relativeModelPath },
            Fields = modelFields,
        };
        return (normalized, resources);
    }

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
        var manifest = CreateManifest(package);
        var validation = Validate(manifest);
        if (!validation.IsValid) throw new InvalidDataException(string.Join(" ", validation.Errors));
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
        if (!string.IsNullOrWhiteSpace(manifest.LocalesFile)
            && (!IsSafeVirtualPath(manifest.LocalesFile) || !manifest.LocalesFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            errors.Add($"Invalid locales file path: {manifest.LocalesFile}");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in manifest.Resources)
        {
            if (!IsSafeVirtualPath(resource.VirtualPath))
                errors.Add($"Invalid authored resource path: {resource.VirtualPath}");
            else if (!paths.Add(resource.VirtualPath))
                errors.Add($"Duplicate authored resource path: {resource.VirtualPath}");
            if (resource.VirtualPath.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase))
                errors.Add("Complete CFGBIN files cannot be embedded in a .vrchara package.");
            if (resource.VirtualPath.EndsWith(".g4md", StringComparison.OrdinalIgnoreCase)
                && !resource.VirtualPath.StartsWith("common/chr/_face/99_CUSTOM/", StringComparison.Ordinal))
                errors.Add("Custom head models must use common/chr/_face/99_CUSTOM/.");
        }
        foreach (var reference in manifest.GameResources)
        {
            if (!IsSafeVirtualPath(reference.VirtualPath))
                errors.Add($"Invalid game resource reference: {reference.VirtualPath}");
        }
        foreach (var patchSet in manifest.Patches)
        {
            if (!IsSafeVirtualPath(patchSet.TablePath)
                || !patchSet.TablePath.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase)
                || !(patchSet.TablePath.StartsWith("common/gamedata/", StringComparison.OrdinalIgnoreCase)
                     || patchSet.TablePath.StartsWith("common/text/", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Invalid CFGBIN patch table path: {patchSet.TablePath}");
            }
        }
        if (manifest.Localizations is not null)
        {
            foreach (var localization in manifest.Localizations)
            {
                if (string.IsNullOrWhiteSpace(localization.Key)
                    || localization.Key.Contains('/')
                    || localization.Key.Contains('\\'))
                    errors.Add($"Invalid localization locale: {localization.Key}");
                if (string.IsNullOrWhiteSpace(localization.Value.LocalizedName))
                    errors.Add($"Localization '{localization.Key}' requires a localized name.");
            }
        }
        return new PackageValidationResult(errors);
    }

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
        return new PackageValidationResult(errors);
    }

    private static VrCharaManifest? MigrateLegacyManifest(JsonElement root)
    {
        var draft = root.Deserialize<CharacterDraft>(Options);
        return draft is null ? null : new VrCharaManifest(2, draft, [], [], []);
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
        && !Path.IsPathRooted(path)
        && !path.Contains("..", StringComparison.Ordinal)
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

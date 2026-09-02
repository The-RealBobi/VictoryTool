using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Application.Profiles;
using VictoryTool.G4.Textures;

namespace VictoryTool.Application.Assets;

public sealed record UniformAssetDescriptor(
    string RelativeContainerPath,
    string ShirtTextureEntryName,
    string SkinMaskTextureEntryName);

public sealed record UniformPreviewRequest(
    int Gender,
    int BodyType,
    uint SkinColorArgb,
    string Locale,
    UniformAssetDescriptor? ExactAsset = null,
    int? UniformVariant = null,
    int ChestSize = 0,
    int? UniformModel = null);

public enum UniformPreviewProvenance
{
    Exact,
    ModelMapped,
    BodyCompatibleDefault,
    Fallback,
}

public sealed record UniformPreviewResult(
    DecodedTexture? Texture,
    UniformPreviewProvenance Provenance,
    string? DiagnosticCode = null);

public interface IUniformPreviewResolver
{
    Task<UniformPreviewResult> ResolveAsync(
        GameDumpProfile profile,
        UniformPreviewRequest request,
        CancellationToken cancellationToken);
}

public sealed class UniformPreviewResolver : IUniformPreviewResolver
{
    private readonly IGameAssetPreviewService _previewService;
    private readonly DefaultUniformCatalog? _configuredDefaults;
    private readonly Dictionary<string, DefaultUniformCatalog> _dumpDefaults =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _catalogLock = new();

    public UniformPreviewResolver(
        IGameAssetPreviewService previewService,
        DefaultUniformCatalog? configuredDefaults = null)
    {
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _configuredDefaults = configuredDefaults;
    }

    public async Task<UniformPreviewResult> ResolveAsync(
        GameDumpProfile profile,
        UniformPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        using var operation = GlobalLog.BeginOperation("uniform_preview_resolve", new Dictionary<string, object?>
        {
            ["gender"] = request.Gender,
            ["bodyType"] = request.BodyType,
            ["uniformVariant"] = request.UniformVariant,
            ["uniformModel"] = request.UniformModel,
            ["hasExactAsset"] = request.ExactAsset is not null,
        });
        var provenance = UniformPreviewProvenance.Exact;
        var descriptor = request.ExactAsset;
        var uniformVariant = request.UniformVariant
            ?? CharacterUniformVariantCatalog.Resolve(request.Gender, request.BodyType, request.ChestSize);
        if (descriptor is null)
        {
            var catalog = GetDefaults(profile, cancellationToken);
            if (request.UniformModel is { } uniformModel
                && catalog.TryResolveModel(
                    uniformModel, request.Gender, uniformVariant, out descriptor))
                provenance = UniformPreviewProvenance.ModelMapped;
            else if (catalog.TryResolve(
                         request.Gender, uniformVariant, out descriptor))
                provenance = UniformPreviewProvenance.BodyCompatibleDefault;
            else
            {
                GlobalLog.Warn("uniform_preview_mapping_unverified");
                return new UniformPreviewResult(
                    null, UniformPreviewProvenance.Fallback, "uniform.body_mapping_unverified");
            }
        }
        if (descriptor is null)
        {
            GlobalLog.Warn("uniform_preview_mapping_unverified");
            return new UniformPreviewResult(
                null, UniformPreviewProvenance.Fallback, "uniform.body_mapping_unverified");
        }

        var root = Path.GetFullPath(profile.RootPath);
        var sourcePath = Path.GetFullPath(Path.Combine(root, descriptor.RelativeContainerPath));
        var relative = Path.GetRelativePath(root, sourcePath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            GlobalLog.Error("uniform_preview_path_outside_dump", data: new Dictionary<string, object?>
            {
                ["relativePath"] = relative,
            });
            return new UniformPreviewResult(null, UniformPreviewProvenance.Fallback, "uniform.path_outside_dump");
        }

        var shirt = await _previewService.LoadAsync(
            sourcePath,
            descriptor.ShirtTextureEntryName,
            null,
            cancellationToken);
        if (shirt.Texture is null)
        {
            GlobalLog.Warn("uniform_preview_shirt_missing", new Dictionary<string, object?>
            {
                ["diagnostic"] = shirt.DiagnosticCode,
            });
            return new UniformPreviewResult(null, UniformPreviewProvenance.Fallback, shirt.DiagnosticCode);
        }

        var mask = await _previewService.LoadAsync(
            sourcePath,
            descriptor.SkinMaskTextureEntryName,
            null,
            cancellationToken);
        if (mask.Texture is null)
        {
            GlobalLog.Warn("uniform_preview_skin_mask_missing");
            return new UniformPreviewResult(
                shirt.Texture,
                provenance,
                "uniform.skin_mask_missing");
        }

        GlobalLog.Debug("uniform_preview_resolved", new Dictionary<string, object?>
        {
            ["provenance"] = provenance,
        });
        return new UniformPreviewResult(
            TextureCompositor.ApplySkinMask(shirt.Texture, mask.Texture, request.SkinColorArgb),
            provenance);
    }

    private DefaultUniformCatalog GetDefaults(
        GameDumpProfile profile,
        CancellationToken cancellationToken)
    {
        if (_configuredDefaults is not null) return _configuredDefaults;
        lock (_catalogLock)
        {
            if (_dumpDefaults.TryGetValue(profile.RootPath, out var existing)) return existing;
            var loaded = DefaultUniformCatalog.Load(profile, cancellationToken);
            _dumpDefaults.Add(profile.RootPath, loaded);
            return loaded;
        }
    }
}

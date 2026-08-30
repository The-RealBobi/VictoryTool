using Avalonia.Media.Imaging;
using VictoryTool.Application.Assets;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Profiles;

namespace VictoryTool.Desktop.Assets;

public sealed class PlayerCardAssetSet : IDisposable
{
    public PlayerCardAssetSet(
        Bitmap? positionIcon,
        Bitmap? genderIcon,
        Bitmap? bodyTypeIcon,
        Bitmap? uniform,
        IReadOnlyList<string> diagnosticCodes)
    {
        PositionIcon = positionIcon;
        GenderIcon = genderIcon;
        BodyTypeIcon = bodyTypeIcon;
        Uniform = uniform;
        DiagnosticCodes = diagnosticCodes;
    }

    public Bitmap? PositionIcon { get; }
    public Bitmap? GenderIcon { get; }
    public Bitmap? BodyTypeIcon { get; }
    public Bitmap? Uniform { get; }
    public IReadOnlyList<string> DiagnosticCodes { get; }

    public void Dispose()
    {
        PositionIcon?.Dispose();
        GenderIcon?.Dispose();
        BodyTypeIcon?.Dispose();
        Uniform?.Dispose();
    }
}

public sealed record PlayerCardAssetRequest(
    CharacterCatalogItem Character,
    CharacterVariantSummary? Variant,
    string Locale,
    int? BodyTypeOverride = null,
    int? GenderOverride = null,
    int? UniformModelOverride = null,
    int? ChestSizeOverride = null);

public interface IPlayerCardAssetLoader
{
    Task<PlayerCardAssetSet> LoadAsync(
        GameDumpProfile profile,
        PlayerCardAssetRequest request,
        CancellationToken cancellationToken);
}

public sealed class PlayerCardAssetLoader : IPlayerCardAssetLoader
{
    private const uint DefaultSkinColorArgb = 0xFFD49A72;
    private readonly IGameAssetPreviewService _previewService;
    private readonly IUniformPreviewResolver _uniformResolver;

    public PlayerCardAssetLoader(
        IGameAssetPreviewService previewService,
        IUniformPreviewResolver uniformResolver)
    {
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
        _uniformResolver = uniformResolver ?? throw new ArgumentNullException(nameof(uniformResolver));
    }

    public async Task<PlayerCardAssetSet> LoadAsync(
        GameDumpProfile profile,
        PlayerCardAssetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Character);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Locale);

        var position = LoadSpriteAsync(
            profile,
            PlayerCardSpriteKind.Position,
            request.Variant is null ? null : CharacterPositionCatalog.ResolveSpriteValue(request.Variant.MainPosition),
            request.Locale,
            cancellationToken);
        var genderValue = request.GenderOverride ?? request.Character.BaseMetadata?.Gender;
        var gender = LoadSpriteAsync(
            profile, PlayerCardSpriteKind.Gender, genderValue, request.Locale, cancellationToken);
        var bodyType = request.BodyTypeOverride ?? request.Character.BaseMetadata?.BodyType;
        var body = LoadSpriteAsync(
            profile, PlayerCardSpriteKind.BodyType, bodyType, request.Locale, cancellationToken);
        var chestSize = request.ChestSizeOverride ?? request.Character.BaseMetadata?.ChestSize ?? 0;
        var uniform = LoadUniformAsync(
            profile,
            genderValue,
            bodyType,
            chestSize,
            request.Character.UniformPreviewAsset,
            request.UniformModelOverride ?? request.Character.BaseMetadata?.UniformModel,
            request.Locale,
            cancellationToken);

        try
        {
            await Task.WhenAll(position, gender, body, uniform);
            var results = new[] { position.Result, gender.Result, body.Result, uniform.Result };
            return new PlayerCardAssetSet(
                position.Result.Bitmap,
                gender.Result.Bitmap,
                body.Result.Bitmap,
                uniform.Result.Bitmap,
                results.Where(result => result.DiagnosticCode is not null)
                    .Select(result => result.DiagnosticCode!)
                    .ToArray());
        }
        catch
        {
            DisposeCompleted(position);
            DisposeCompleted(gender);
            DisposeCompleted(body);
            DisposeCompleted(uniform);
            throw;
        }
    }

    private async Task<LoadedBitmap> LoadSpriteAsync(
        GameDumpProfile profile,
        PlayerCardSpriteKind kind,
        int? value,
        string locale,
        CancellationToken cancellationToken)
    {
        if (value is null || !PlayerCardSpriteCatalog.TryResolve(kind, value.Value, locale, out var descriptor))
            return new LoadedBitmap(null, $"assets.{kind.ToString().ToLowerInvariant()}_unsupported");
        var result = await _previewService.LoadAsync(profile, descriptor.Request, cancellationToken);
        return result.Texture is null
            ? new LoadedBitmap(null, result.DiagnosticCode)
            : new LoadedBitmap(DecodedTextureBitmapFactory.Create(result.Texture), null);
    }

    private async Task<LoadedBitmap> LoadUniformAsync(
        GameDumpProfile profile,
        int? gender,
        int? bodyType,
        int chestSize,
        UniformAssetDescriptor? exactAsset,
        int? uniformModel,
        string locale,
        CancellationToken cancellationToken)
    {
        if (gender is null || bodyType is null)
            return new LoadedBitmap(null, "uniform.body_mapping_unverified");
        var result = await _uniformResolver.ResolveAsync(
            profile,
            new UniformPreviewRequest(
                gender.Value,
                bodyType.Value,
                DefaultSkinColorArgb,
                locale,
                exactAsset,
                ChestSize: chestSize,
                UniformModel: uniformModel),
            cancellationToken);
        return result.Texture is null
            ? new LoadedBitmap(null, result.DiagnosticCode)
            : new LoadedBitmap(DecodedTextureBitmapFactory.Create(result.Texture), result.DiagnosticCode);
    }

    private static void DisposeCompleted(Task<LoadedBitmap> task)
    {
        if (task.IsCompletedSuccessfully)
            task.Result.Bitmap?.Dispose();
    }

    private sealed record LoadedBitmap(Bitmap? Bitmap, string? DiagnosticCode);
}

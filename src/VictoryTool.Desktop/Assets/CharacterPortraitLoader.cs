using Avalonia;
using Avalonia.Media.Imaging;
using VictoryTool.Application.Assets;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Desktop.Assets;

public enum CharacterPortraitKind
{
    Standard,
    UniformCompatible,
    RosterThumbnail,
}

public sealed record CharacterPortraitRequest(
    CharacterCatalogItem Character,
    CharacterPortraitKind Kind);

public sealed class CharacterPortraitLoadResult : IDisposable
{
    public CharacterPortraitLoadResult(Bitmap? bitmap, string? diagnosticCode)
    {
        Bitmap = bitmap;
        DiagnosticCode = diagnosticCode;
    }

    public Bitmap? Bitmap { get; }
    public string? DiagnosticCode { get; }

    public void Dispose() => Bitmap?.Dispose();
}

public interface ICharacterPortraitLoader
{
    Task<CharacterPortraitLoadResult> LoadAsync(
        CharacterPortraitRequest request,
        CancellationToken cancellationToken);
}

public sealed class CharacterPortraitLoader : ICharacterPortraitLoader
{
    private readonly IGameAssetPreviewService _previewService;

    public CharacterPortraitLoader(IGameAssetPreviewService previewService)
    {
        _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
    }

    public async Task<CharacterPortraitLoadResult> LoadAsync(
        CharacterPortraitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var character = request.Character;
        ArgumentNullException.ThrowIfNull(character);
        using var operation = GlobalLog.BeginOperation("character_portrait_load", new Dictionary<string, object?>
        {
            ["kind"] = request.Kind,
            ["hasStandardPath"] = character.StandardPortraitResourcePath is not null,
            ["hasUniformPath"] = character.UniformPortraitResourcePath is not null,
        });
        var portraitPath = request.Kind == CharacterPortraitKind.UniformCompatible
            ? character.UniformPortraitResourcePath ?? character.PortraitResourcePath
            : character.StandardPortraitResourcePath ?? character.PortraitResourcePath;
        if (string.IsNullOrWhiteSpace(portraitPath))
        {
            GlobalLog.Warn("character_portrait_path_missing");
            return new CharacterPortraitLoadResult(null, "assets.source_missing");
        }

        if (portraitPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return LoadPng(portraitPath, request.Kind);

        var entryName = request.Kind == CharacterPortraitKind.UniformCompatible
            ? character.PortraitMetadata?.UniformPortraitEntryName
                ?? character.PortraitMetadata?.StandardPortraitEntryName
            : character.PortraitMetadata?.StandardPortraitEntryName
                ?? character.PortraitMetadata?.UniformPortraitEntryName;
        if (character.PortraitMetadata is not null && string.IsNullOrWhiteSpace(entryName))
        {
            GlobalLog.Warn("character_portrait_entry_missing");
            return new CharacterPortraitLoadResult(null, "assets.portrait_entry_missing");
        }

        var preview = await _previewService.LoadAsync(
            portraitPath,
            entryName,
            null,
            cancellationToken);
        if (preview.Texture is null)
        {
            GlobalLog.Warn("character_portrait_preview_failed", new Dictionary<string, object?>
            {
                ["diagnostic"] = preview.DiagnosticCode,
            });
            return new CharacterPortraitLoadResult(null, preview.DiagnosticCode);
        }
        GlobalLog.Debug("character_portrait_loaded", new Dictionary<string, object?>
        {
            ["width"] = preview.Texture.Width,
            ["height"] = preview.Texture.Height,
        });
        return
            new CharacterPortraitLoadResult(
                request.Kind == CharacterPortraitKind.RosterThumbnail
                    ? DecodedTextureBitmapFactory.CreateThumbnail(preview.Texture, 48)
                    : DecodedTextureBitmapFactory.Create(preview.Texture),
                null);
    }

    private static CharacterPortraitLoadResult LoadPng(string path, CharacterPortraitKind kind)
    {
        if (!File.Exists(path))
        {
            GlobalLog.Warn("character_portrait_png_missing");
            return new CharacterPortraitLoadResult(null, "assets.source_missing");
        }

        try
        {
            var source = new Bitmap(path);
            if (kind != CharacterPortraitKind.RosterThumbnail)
                return new CharacterPortraitLoadResult(source, null);

            var sourceSize = source.PixelSize;
            var scale = Math.Min(48d / sourceSize.Width, 48d / sourceSize.Height);
            var targetSize = new PixelSize(
                Math.Max(1, (int)Math.Round(sourceSize.Width * scale)),
                Math.Max(1, (int)Math.Round(sourceSize.Height * scale)));
            var thumbnail = source.CreateScaledBitmap(targetSize, BitmapInterpolationMode.HighQuality);
            source.Dispose();
            return new CharacterPortraitLoadResult(thumbnail, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            GlobalLog.Warn("character_portrait_png_invalid", exception: exception);
            return new CharacterPortraitLoadResult(null, "assets.source_invalid");
        }
    }
}

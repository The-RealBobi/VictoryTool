using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media.Imaging;
using VictoryTool.Application.Assets;
using VictoryTool.Application.Characters;
using VictoryTool.Application.Diagnostics;
using VictoryTool.G4.Textures;

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
        cancellationToken.ThrowIfCancellationRequested();
        var character = request.Character;
        ArgumentNullException.ThrowIfNull(character);
        using var operation = GlobalLog.BeginOperation("character_portrait_load", new Dictionary<string, object?>
        {
            ["kind"] = request.Kind,
            ["hasStandardPath"] = character.StandardPortraitResourcePath is not null,
            ["hasUniformPath"] = character.UniformPortraitResourcePath is not null,
        });
        if (request.Kind == CharacterPortraitKind.UniformCompatible
            && character.PortraitMetadata?.IncludesUniform == true)
            return new CharacterPortraitLoadResult(null, null);
        var portraitPath = request.Kind == CharacterPortraitKind.UniformCompatible
            ? character.UniformPortraitResourcePath ?? character.PortraitResourcePath
            : character.StandardPortraitResourcePath ?? character.PortraitResourcePath;
        if (string.IsNullOrWhiteSpace(portraitPath))
        {
            GlobalLog.Debug("character_portrait_path_missing");
            return new CharacterPortraitLoadResult(null, "assets.source_missing");
        }

        if (portraitPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            var result = await Task.Run(() => LoadPng(portraitPath, request.Kind, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                result.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return result;
        }

        var entryName = request.Kind == CharacterPortraitKind.UniformCompatible
            ? character.PortraitMetadata?.UniformPortraitEntryName
                ?? character.PortraitMetadata?.StandardPortraitEntryName
            : character.PortraitMetadata?.StandardPortraitEntryName
                ?? character.PortraitMetadata?.UniformPortraitEntryName;
        if (character.PortraitMetadata is null && File.Exists(portraitPath))
        {
            try
            {
                var container = G4TxDocument.Read(await File.ReadAllBytesAsync(portraitPath, cancellationToken));
                entryName = (request.Kind == CharacterPortraitKind.UniformCompatible
                    ? container.Textures.ElementAtOrDefault(1)
                    : container.Textures.FirstOrDefault())?.Name
                    ?? container.Textures.FirstOrDefault()?.Name;
                if (string.IsNullOrWhiteSpace(entryName))
                    return new CharacterPortraitLoadResult(null, "assets.portrait_entry_missing");
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                GlobalLog.Warn("character_portrait_container_invalid", exception: exception);
                return new CharacterPortraitLoadResult(null, "assets.source_invalid");
            }
        }
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

    private static CharacterPortraitLoadResult LoadPng(
        string path,
        CharacterPortraitKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            GlobalLog.Warn("character_portrait_png_missing");
            return new CharacterPortraitLoadResult(null, "assets.source_missing");
        }

        Bitmap? bitmap = null;
        try
        {
            if (kind == CharacterPortraitKind.RosterThumbnail)
            {
                using var stream = File.OpenRead(path);
                Span<byte> header = stackalloc byte[24];
                stream.ReadExactly(header);
                if (!header[..8].SequenceEqual(new byte[] {137, 80, 78, 71, 13, 10, 26, 10})
                    || !header[12..16].SequenceEqual("IHDR"u8))
                    throw new InvalidDataException("Invalid PNG header.");
                var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
                var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
                if (width <= 0 || height <= 0) throw new InvalidDataException("Invalid PNG dimensions.");
                stream.Position = 0;
                bitmap = width >= height
                    ? Bitmap.DecodeToWidth(stream, 48, BitmapInterpolationMode.HighQuality)
                    : Bitmap.DecodeToHeight(stream, 48, BitmapInterpolationMode.HighQuality);
            }
            else bitmap = new Bitmap(path);
            cancellationToken.ThrowIfCancellationRequested();

            var result = new CharacterPortraitLoadResult(bitmap, null);
            bitmap = null;
            return result;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            GlobalLog.Warn("character_portrait_png_invalid", exception: exception);
            return new CharacterPortraitLoadResult(null, "assets.source_invalid");
        }
        finally
        {
            bitmap?.Dispose();
        }
    }
}

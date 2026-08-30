using VictoryTool.Application.Profiles;
using VictoryTool.G4.Textures;

namespace VictoryTool.Application.Assets;

public sealed record GameUiAssetRequest(
    GameUiAssetKey Key,
    string Locale,
    string? TextureEntryName = null,
    string? SubTextureName = null);

public sealed record GameAssetPreviewResult(
    string SourcePath,
    DecodedTexture? Texture,
    string? DiagnosticCode = null)
{
    public bool IsAvailable => Texture is not null;
}

public interface IGameAssetPreviewService
{
    Task<GameAssetPreviewResult> LoadAsync(
        GameDumpProfile profile,
        GameUiAssetRequest request,
        CancellationToken cancellationToken);

    Task<GameAssetPreviewResult> LoadAsync(
        string sourcePath,
        string? textureEntryName,
        string? subTextureName,
        CancellationToken cancellationToken);
}

public sealed class GameAssetPreviewService : IGameAssetPreviewService
{
    private readonly IG4TextureDecoder _decoder;
    private readonly int _capacity;
    private readonly Dictionary<CacheKey, DecodedTexture> _cache = [];
    private readonly Queue<CacheKey> _cacheOrder = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GameAssetPreviewService(IG4TextureDecoder decoder, int capacity = 64)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _decoder = decoder;
        _capacity = capacity;
    }

    public async Task<GameAssetPreviewResult> LoadAsync(
        GameDumpProfile profile,
        GameUiAssetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = GameUiAssetLocator.Resolve(profile, request.Key, request.Locale);
        return await LoadAsync(
            path,
            request.TextureEntryName,
            request.SubTextureName,
            cancellationToken);
    }

    public async Task<GameAssetPreviewResult> LoadAsync(
        string sourcePath,
        string? textureEntryName,
        string? subTextureName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path))
            return new GameAssetPreviewResult(path, null, "assets.source_missing");

        var sourceInfo = new FileInfo(path);
        var key = new CacheKey(
            path,
            textureEntryName,
            subTextureName,
            sourceInfo.LastWriteTimeUtc.Ticks,
            sourceInfo.Length);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out var cached))
                return new GameAssetPreviewResult(path, cached);
        }
        finally
        {
            _gate.Release();
        }

        var source = await File.ReadAllBytesAsync(path, cancellationToken);
        G4TxDocument document;
        try
        {
            document = G4TxDocument.Read(source);
        }
        catch (InvalidDataException)
        {
            return new GameAssetPreviewResult(path, null, "assets.container_invalid");
        }
        G4SubTextureEntry? subTexture = null;
        G4TextureEntry? texture;
        if (subTextureName is not null)
        {
            subTexture = document.SubTextures.FirstOrDefault(entry =>
                string.Equals(entry.Name, subTextureName, StringComparison.Ordinal));
            if (subTexture is null)
                return new GameAssetPreviewResult(path, null, "assets.subtexture_missing");
            texture = document.Textures[subTexture.ParentTextureIndex];
            if (textureEntryName is not null
                && !string.Equals(texture.Name, textureEntryName, StringComparison.Ordinal))
                return new GameAssetPreviewResult(path, null, "assets.texture_entry_mismatch");
        }
        else
        {
            texture = textureEntryName is null
                ? document.Textures.FirstOrDefault()
                : document.Textures.FirstOrDefault(entry =>
                    string.Equals(entry.Name, textureEntryName, StringComparison.Ordinal));
        }
        if (texture is null)
            return new GameAssetPreviewResult(path, null, "assets.texture_entry_missing");
        DecodedTexture decoded;
        try
        {
            var decodedParent = await _decoder.DecodeAsync(texture, cancellationToken);
            decoded = subTexture is null ? decodedParent : Crop(decodedParent, subTexture);
        }
        catch (InvalidDataException)
        {
            return new GameAssetPreviewResult(path, null, "assets.container_invalid");
        }
        catch (NotSupportedException)
        {
            return new GameAssetPreviewResult(path, null, "assets.format_unsupported");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out var concurrentlyCached))
                return new GameAssetPreviewResult(path, concurrentlyCached);
            while (_cache.Count >= _capacity && _cacheOrder.TryDequeue(out var expired))
                _cache.Remove(expired);
            _cache.Add(key, decoded);
            _cacheOrder.Enqueue(key);
        }
        finally
        {
            _gate.Release();
        }
        return new GameAssetPreviewResult(path, decoded);
    }

    private static DecodedTexture Crop(DecodedTexture source, G4SubTextureEntry region)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || region.X > source.Width - region.Width
            || region.Y > source.Height - region.Height)
            throw new InvalidDataException("A G4TX subtexture lies outside its decoded parent texture.");

        var stride = checked(region.Width * 4);
        var pixels = new byte[checked(stride * region.Height)];
        for (var row = 0; row < region.Height; row++)
        {
            var sourceOffset = checked((region.Y + row) * source.Stride + region.X * 4);
            source.BgraPixels.AsSpan(sourceOffset, stride).CopyTo(pixels.AsSpan(row * stride, stride));
        }
        return new DecodedTexture(region.Width, region.Height, stride, pixels);
    }

    private sealed record CacheKey(
        string Path,
        string? TextureEntryName,
        string? SubTextureName,
        long LastWriteTimeUtcTicks,
        long Length);
}

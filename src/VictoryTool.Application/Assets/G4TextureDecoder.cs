using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using VictoryTool.G4.Textures;

namespace VictoryTool.Application.Assets;

public sealed record DecodedTexture(int Width, int Height, int Stride, byte[] BgraPixels);

public interface IG4TextureDecoder
{
    Task<DecodedTexture> DecodeAsync(G4TextureEntry texture, CancellationToken cancellationToken);
}

public sealed class BcnG4TextureDecoder : IG4TextureDecoder
{
    public async Task<DecodedTexture> DecodeAsync(
        G4TextureEntry texture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var decoder = new BcDecoder();
        Memory2D<ColorRgba32> rgba;
        if (texture.PayloadKind == G4TexturePayloadKind.Dds)
        {
            await using var stream = new MemoryStream(texture.Payload.ToArray(), writable: false);
            rgba = await decoder.Decode2DAsync(stream, cancellationToken);
        }
        else if (texture.PayloadKind == G4TexturePayloadKind.NxTexture)
        {
            var nx = NxTextureDocument.Read(texture.Payload.Span);
            if (nx.Format == NxTextureFormat.Rgba8888)
                return DecodeRawRgba(nx);
            var format = nx.Format switch
            {
                NxTextureFormat.Bc1 => CompressionFormat.Bc1,
                NxTextureFormat.Bc3 => CompressionFormat.Bc3,
                NxTextureFormat.Bc7 => CompressionFormat.Bc7,
                _ => throw new NotSupportedException($"Unsupported NXTCH format identifier 0x{nx.FormatIdentifier:X}.")
            };
            rgba = await decoder.DecodeRaw2DAsync(
                nx.GetLinearMipData(0),
                nx.Width,
                nx.Height,
                format,
                cancellationToken);
        }
        else
        {
            throw new NotSupportedException("The G4TX texture payload kind cannot be decoded.");
        }

        return ConvertToBgra(rgba);
    }

    private static DecodedTexture ConvertToBgra(Memory2D<ColorRgba32> rgba)
    {
        var width = rgba.Width;
        var height = rgba.Height;
        var stride = checked(width * 4);
        var bgra = new byte[checked(stride * height)];
        var pixels = rgba.Span;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = pixels[y, x];
                var offset = y * stride + x * 4;
                bgra[offset] = color.b;
                bgra[offset + 1] = color.g;
                bgra[offset + 2] = color.r;
                bgra[offset + 3] = color.a;
            }
        }

        return new DecodedTexture(width, height, stride, bgra);
    }

    private static DecodedTexture DecodeRawRgba(NxTextureDocument texture)
    {
        var rgba = texture.GetLinearMipData(0);
        var stride = checked(texture.Width * 4);
        var bgra = new byte[rgba.Length];
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            bgra[offset] = rgba[offset + 2];
            bgra[offset + 1] = rgba[offset + 1];
            bgra[offset + 2] = rgba[offset];
            bgra[offset + 3] = rgba[offset + 3];
        }
        return new DecodedTexture(texture.Width, texture.Height, stride, bgra);
    }
}

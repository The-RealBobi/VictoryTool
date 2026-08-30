using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
using VictoryTool.G4.Textures;

namespace VictoryTool.Application.Assets;

public sealed class PortraitG4TxBuilder
{
    public byte[] BuildPc(
        ReadOnlyMemory<byte> template,
        ReadOnlyMemory<byte> portrait1Bgra,
        ReadOnlyMemory<byte> portrait2Bgra,
        int width,
        int height,
        string sourceStem,
        string destinationStem)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var expectedBytes = checked(width * height * 4);
        if (portrait1Bgra.Length != expectedBytes || portrait2Bgra.Length != expectedBytes)
            throw new ArgumentException("Portrait BGRA buffers must match the requested dimensions.");
        var document = G4TxDocument.Read(template.Span);
        if (document.TextureCount != 2
            || document.Textures.Any(texture => texture.Width != width || texture.Height != height)
            || document.Textures.Any(texture => texture.PayloadKind != G4TexturePayloadKind.Dds))
            throw new InvalidDataException("The portrait template is not a compatible two-layer PC G4TX.");

        var encoder = new BcEncoder(CompressionFormat.Bc7);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Format = CompressionFormat.Bc7;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.DdsPreferDxt10Header = true;
        var first = Serialize(encoder.EncodeToDds(portrait1Bgra.Span, width, height, PixelFormat.Bgra32));
        var second = Serialize(encoder.EncodeToDds(portrait2Bgra.Span, width, height, PixelFormat.Bgra32));
        var replaced = document.ReplaceTextures(new Dictionary<string, G4TextureReplacement>
        {
            [document.Textures[0].Name] = new(first, width, height),
            [document.Textures[1].Name] = new(second, width, height),
        });
        var renamed = G4TxDocument.Read(replaced).RenameIdentifier(sourceStem, destinationStem);
        var restored = G4TxDocument.Read(renamed);
        if (restored.Textures.Any(texture => !texture.Name.Contains(destinationStem, StringComparison.Ordinal)))
            throw new InvalidDataException("The rebuilt portrait retained an unexpected texture identifier.");
        return renamed;
    }

    private static byte[] Serialize(DdsFile file)
    {
        using var stream = new MemoryStream();
        file.Write(stream);
        return stream.ToArray();
    }
}

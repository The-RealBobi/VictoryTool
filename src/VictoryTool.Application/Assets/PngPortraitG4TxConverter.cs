using SkiaSharp;
using VictoryTool.G4.Textures;

namespace VictoryTool.Application.Assets;

public static class PngPortraitG4TxConverter
{
    public static byte[] Convert(string pngPath, string templatePath, string sourceStem, string destinationStem)
    {
        var (layerOnePath, layerTwoPath) = ResolveLayers(pngPath);
        return ConvertLayers(layerOnePath, layerTwoPath, templatePath, sourceStem, destinationStem);
    }

    public static byte[] Convert(
        string layerOnePath,
        string layerTwoPath,
        string templatePath,
        string sourceStem,
        string destinationStem)
    {
        return ConvertLayers(layerOnePath, layerTwoPath, templatePath, sourceStem, destinationStem);
    }

    public static byte[] Convert(
        ReadOnlyMemory<byte> layerOnePng,
        ReadOnlyMemory<byte> layerTwoPng,
        string templatePath,
        string sourceStem,
        string destinationStem)
    {
        var template = File.ReadAllBytes(templatePath);
        var document = G4TxDocument.Read(template);
        var texture = document.Textures.FirstOrDefault()
            ?? throw new InvalidDataException("The portrait template has no textures.");
        using var source = SKBitmap.Decode(layerOnePng.ToArray())
            ?? throw new InvalidDataException("The standard portrait PNG could not be decoded.");
        using var secondSource = SKBitmap.Decode(layerTwoPng.ToArray())
            ?? throw new InvalidDataException("The rear-hair-free portrait PNG could not be decoded.");
        return ConvertBitmaps(source, secondSource, template, texture, sourceStem, destinationStem);
    }

    private static byte[] ConvertLayers(
        string layerOnePath,
        string? layerTwoPath,
        string templatePath,
        string sourceStem,
        string destinationStem)
    {
        var template = File.ReadAllBytes(templatePath);
        var document = G4TxDocument.Read(template);
        var texture = document.Textures.FirstOrDefault()
            ?? throw new InvalidDataException("The portrait template has no textures.");
        using var source = SKBitmap.Decode(layerOnePath)
            ?? throw new InvalidDataException($"The portrait PNG could not be decoded: {layerOnePath}");
        using var secondSource = layerTwoPath is null ? null : SKBitmap.Decode(layerTwoPath);
        using var resized = source.Resize(new SKImageInfo(texture.Width, texture.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidDataException("The portrait PNG could not be resized.");
        using var secondResized = (secondSource ?? source).Resize(new SKImageInfo(texture.Width, texture.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidDataException("The companion portrait PNG could not be resized.");
        return BuildPixels(resized, secondResized, template, texture, sourceStem, destinationStem);
    }

    private static byte[] ConvertBitmaps(
        SKBitmap source,
        SKBitmap secondSource,
        byte[] template,
        G4TextureEntry texture,
        string sourceStem,
        string destinationStem)
    {
        using var resized = source.Resize(new SKImageInfo(texture.Width, texture.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidDataException("The portrait PNG could not be resized.");
        using var secondResized = secondSource.Resize(new SKImageInfo(texture.Width, texture.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidDataException("The companion portrait PNG could not be resized.");
        return BuildPixels(resized, secondResized, template, texture, sourceStem, destinationStem);
    }

    private static byte[] BuildPixels(
        SKBitmap resized,
        SKBitmap secondResized,
        byte[] template,
        G4TextureEntry texture,
        string sourceStem,
        string destinationStem)
    {
        var pixels = new byte[texture.Width * texture.Height * 4];
        var colors = resized.Pixels;
        var secondColors = secondResized.Pixels;
        for (var i = 0; i < colors.Length; i++)
        {
            var color = colors[i];
            var offset = i * 4;
            pixels[offset] = color.Blue;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Red;
            pixels[offset + 3] = color.Alpha;
        }
        var secondPixels = new byte[pixels.Length];
        for (var i = 0; i < secondColors.Length; i++)
        {
            var color = secondColors[i];
            var offset = i * 4;
            secondPixels[offset] = color.Blue;
            secondPixels[offset + 1] = color.Green;
            secondPixels[offset + 2] = color.Red;
            secondPixels[offset + 3] = color.Alpha;
        }
        return new PortraitG4TxBuilder().BuildPc(template, pixels, secondPixels, texture.Width, texture.Height, sourceStem, destinationStem);
    }

    private static (string LayerOne, string? LayerTwo) ResolveLayers(string path)
    {
        var file = Path.GetFileName(path);
        var marker = file.IndexOf("_1_100", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            marker = file.IndexOf("_2_100", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return (path, null);
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var layerOneName = file[..marker] + "_1_100" + file[(marker + 6)..];
        var layerTwoName = file[..marker] + "_2_100" + file[(marker + 6)..];
        var layerOne = Path.Combine(directory, layerOneName);
        var layerTwo = Path.Combine(directory, layerTwoName);
        if (!File.Exists(layerOne) || !File.Exists(layerTwo)) return (path, null);
        return (layerOne, layerTwo);
    }
}

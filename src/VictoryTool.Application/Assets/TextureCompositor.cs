using VictoryTool.G4.Textures;

namespace VictoryTool.Application.Assets;

public static class TextureCompositor
{
    public static DecodedTexture ApplySkinMask(
        DecodedTexture shirt,
        DecodedTexture mask,
        uint skinColorArgb)
    {
        ArgumentNullException.ThrowIfNull(shirt);
        ArgumentNullException.ThrowIfNull(mask);
        if (shirt.Width != mask.Width || shirt.Height != mask.Height)
            throw new ArgumentException("The shirt and skin mask dimensions must match.", nameof(mask));

        var output = shirt.BgraPixels.ToArray();
        var skinAlpha = (byte)(skinColorArgb >> 24);
        var skinRed = (byte)(skinColorArgb >> 16);
        var skinGreen = (byte)(skinColorArgb >> 8);
        var skinBlue = (byte)skinColorArgb;

        for (var y = 0; y < shirt.Height; y++)
        {
            var shirtRow = y * shirt.Stride;
            var maskRow = y * mask.Stride;
            for (var x = 0; x < shirt.Width; x++)
            {
                var destination = shirtRow + x * 4;
                var maskOffset = maskRow + x * 4;
                var sourceAlpha = mask.BgraPixels[maskOffset + 3] * skinAlpha / 255;
                if (sourceAlpha == 0)
                    continue;

                var destinationAlpha = output[destination + 3];
                var inverseAlpha = 255 - sourceAlpha;
                var outputAlpha = sourceAlpha + destinationAlpha * inverseAlpha / 255;
                output[destination] = Blend(skinBlue, output[destination], sourceAlpha, destinationAlpha, outputAlpha);
                output[destination + 1] = Blend(skinGreen, output[destination + 1], sourceAlpha, destinationAlpha, outputAlpha);
                output[destination + 2] = Blend(skinRed, output[destination + 2], sourceAlpha, destinationAlpha, outputAlpha);
                output[destination + 3] = (byte)outputAlpha;
            }
        }

        return new DecodedTexture(shirt.Width, shirt.Height, shirt.Stride, output);
    }

    private static byte Blend(
        byte source,
        byte destination,
        int sourceAlpha,
        int destinationAlpha,
        int outputAlpha)
    {
        if (outputAlpha == 0)
            return 0;
        var destinationContribution = destination * destinationAlpha * (255 - sourceAlpha) / 255;
        return (byte)((source * sourceAlpha + destinationContribution) / outputAlpha);
    }
}

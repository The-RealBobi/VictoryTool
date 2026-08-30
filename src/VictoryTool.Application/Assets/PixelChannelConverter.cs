namespace VictoryTool.Application.Assets;

public static class PixelChannelConverter
{
    public static void RgbaToBgraInPlace(Span<byte> pixels)
    {
        if (pixels.Length % 4 != 0)
            throw new ArgumentException("The pixel buffer must contain complete RGBA pixels.", nameof(pixels));

        for (var index = 0; index < pixels.Length; index += 4)
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
    }
}

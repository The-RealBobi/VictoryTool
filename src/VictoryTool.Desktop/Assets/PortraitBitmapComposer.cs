using Avalonia;
using Avalonia.Media.Imaging;

namespace VictoryTool.Desktop.Assets;

public static class PortraitBitmapComposer
{
    public static RenderTargetBitmap Compose(Bitmap back, Bitmap uniform, Bitmap front, int maximumDimension)
    {
        var scale = maximumDimension / (double)Math.Max(back.PixelSize.Width, back.PixelSize.Height);
        var size = new PixelSize(Math.Max(1, (int)Math.Round(back.PixelSize.Width * scale)),
            Math.Max(1, (int)Math.Round(back.PixelSize.Height * scale)));
        var result = new RenderTargetBitmap(size, new Vector(96, 96));
        try
        {
            using var context = result.CreateDrawingContext();
            var bounds = new Rect(0, 0, size.Width, size.Height);
            context.DrawImage(back, bounds);
            context.DrawImage(uniform, bounds);
            context.DrawImage(front, bounds);
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}

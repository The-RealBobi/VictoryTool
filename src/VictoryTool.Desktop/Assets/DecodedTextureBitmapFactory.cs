using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VictoryTool.Application.Assets;

namespace VictoryTool.Desktop.Assets;

public static class DecodedTextureBitmapFactory
{
    public static WriteableBitmap Create(DecodedTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Width <= 0 || texture.Height <= 0 || texture.Stride < texture.Width * 4)
            throw new ArgumentException("The decoded texture dimensions or stride are invalid.", nameof(texture));
        if (texture.BgraPixels.Length < checked(texture.Stride * texture.Height))
            throw new ArgumentException("The decoded texture pixel buffer is incomplete.", nameof(texture));

        var bitmap = new WriteableBitmap(
            new PixelSize(texture.Width, texture.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        for (var row = 0; row < texture.Height; row++)
        {
            Marshal.Copy(
                texture.BgraPixels,
                row * texture.Stride,
                IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                texture.Width * 4);
        }
        return bitmap;
    }

    public static WriteableBitmap CreateThumbnail(DecodedTexture texture, int maximumDimension)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (maximumDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        if (texture.Width <= 0 || texture.Height <= 0 || texture.Stride < texture.Width * 4)
            throw new ArgumentException("The decoded texture dimensions or stride are invalid.", nameof(texture));
        if (texture.BgraPixels.Length < checked(texture.Stride * texture.Height))
            throw new ArgumentException("The decoded texture pixel buffer is incomplete.", nameof(texture));

        var scale = Math.Min(1d, maximumDimension / (double)Math.Max(texture.Width, texture.Height));
        var width = Math.Max(1, (int)Math.Round(texture.Width * scale));
        var height = Math.Max(1, (int)Math.Round(texture.Height * scale));
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(texture.Height - 1, (int)(y / scale));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(texture.Width - 1, (int)(x / scale));
                var sourceOffset = sourceY * texture.Stride + sourceX * 4;
                Marshal.Copy(
                    texture.BgraPixels,
                    sourceOffset,
                    IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes + x * 4),
                    4);
            }
        }
        return bitmap;
    }
}

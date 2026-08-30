using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Media;
using SkiaSharp;

namespace VictoryTool.Desktop.Views;

internal static class ScreenColorSampler
{
    private const int LeftMouseButton = 0x01;

    public static bool IsSupported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public static bool IsPrimaryButtonDown()
    {
        try
        {
            return OperatingSystem.IsMacOS()
                ? CGEventSourceButtonState(1, 0)
                : OperatingSystem.IsWindows() && (GetAsyncKeyState(LeftMouseButton) & 0x8000) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public static bool TrySampleCursor(out Color color, out string? error)
    {
        color = default;
        error = null;
        try
        {
            if (OperatingSystem.IsWindows()) return TrySampleWindowsCursor(out color, out error);
            if (OperatingSystem.IsMacOS()) return TrySampleMacCursor(out color, out error);
            error = "Screen colour sampling is not available on this platform.";
            return false;
        }
        catch (Exception exception) when (exception is ExternalException or IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TrySampleWindowsCursor(out Color color, out string? error)
    {
        color = default;
        error = null;
        if (!GetCursorPos(out var point))
        {
            error = "The cursor position could not be read.";
            return false;
        }
        var device = GetDC(IntPtr.Zero);
        if (device == IntPtr.Zero)
        {
            error = "The desktop could not be read.";
            return false;
        }
        try
        {
            var pixel = GetPixel(device, point.X, point.Y);
            if (pixel == uint.MaxValue)
            {
                error = "The selected screen pixel could not be read.";
                return false;
            }
            color = Color.FromRgb((byte)(pixel & 0xFF), (byte)((pixel >> 8) & 0xFF), (byte)((pixel >> 16) & 0xFF));
            return true;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, device);
        }
    }

    private static bool TrySampleMacCursor(out Color color, out string? error)
    {
        color = default;
        error = null;
        var location = GetMacCursorLocation();
        var path = Path.Combine(Path.GetTempPath(), $"victorytool-colour-{Guid.NewGuid():N}.png");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                ArgumentList = { "-x", "-R", $"{Math.Floor(location.X)},{Math.Floor(location.Y)},1,1", path },
            });
            if (process is null || !process.WaitForExit(2_000) || process.ExitCode != 0 || !File.Exists(path))
            {
                error = "macOS did not allow that screen pixel to be read. Enable Screen Recording for VictoryTool and try again.";
                return false;
            }
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap is null)
            {
                error = "The captured screen pixel could not be decoded.";
                return false;
            }
            var pixel = bitmap.GetPixel(0, 0);
            color = Color.FromRgb(pixel.Red, pixel.Green, pixel.Blue);
            return true;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static NativePoint GetMacCursorLocation()
    {
        var currentEvent = CGEventCreate(IntPtr.Zero);
        if (currentEvent == IntPtr.Zero) throw new ExternalException("The cursor position could not be read.");
        try { return CGEventGetLocation(currentEvent); }
        finally { CFRelease(currentEvent); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(double x, double y)
    {
        public double X { get; } = x;
        public double Y { get; } = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WindowsPoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr device);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr device, int x, int y);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern bool CGEventSourceButtonState(int stateId, int button);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern NativePoint CGEventGetLocation(IntPtr currentEvent);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}

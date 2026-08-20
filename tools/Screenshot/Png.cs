using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PgNimbus.Screenshot;

/// <summary>A decoded image as raw Bgra8888 pixels.</summary>
internal readonly record struct Pixels(int Width, int Height, byte[] Data)
{
    public int Stride => Width * 4;

    public int Offset(int x, int y) => (y * Stride) + (x * 4);
}

/// <summary>
/// Reading and writing PNGs as flat pixel buffers, through Avalonia's own
/// codec — the harness already depends on it, so this adds no package.
///
/// Everything goes through here rather than comparing an in-memory frame
/// against a decoded file: one decode path means pixel format, stride and alpha
/// handling cannot differ between the two sides of a comparison.
/// </summary>
internal static class Png
{
    public static Pixels Read(string path)
    {
        using var bitmap = new Bitmap(path);
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var data = new byte[stride * height];

        // CopyPixels wants unmanaged memory; a temporary block keeps this file
        // free of `unsafe`, which would otherwise have to be switched on
        // project-wide for one call.
        var buffer = Marshal.AllocHGlobal(data.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, data.Length, stride);
            Marshal.Copy(buffer, data, 0, data.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new Pixels(width, height, data);
    }

    public static void Write(string path, Pixels pixels)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var size = new PixelSize(pixels.Width, pixels.Height);
        using var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = bitmap.Lock())
        {
            Marshal.Copy(pixels.Data, 0, locked.Address, pixels.Data.Length);
        }

        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}

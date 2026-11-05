using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// The game's splash, as something Avalonia can draw.
///
/// The decode is Core's (<see cref="SplashBmp"/>) and shared with the ImGui Studio; all this does
/// is hand the same pixels to a <see cref="WriteableBitmap"/> instead of to OpenGL.
/// </summary>
internal static class SplashImage
{
    /// <summary>The picture and where its status band starts, or null when there is none to show.</summary>
    public static (WriteableBitmap Bitmap, int BarTop, int TextRight)? Load(string installFolder)
    {
        var decoded = SplashBmp.Decode(SplashBmp.PathIn(installFolder));
        if (decoded is null) return null;

        try
        {
            var bmp = new WriteableBitmap(
                new PixelSize(decoded.Width, decoded.Height), new Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Unpremul);

            using (var buffer = bmp.Lock())
            {
                // ROW BY ROW, AGAINST THE BUFFER'S OWN STRIDE. A locked framebuffer is free to pad
                // its rows, and a single block copy that assumes width*4 renders a picture sheared
                // diagonally on any machine where it does not. RowBytes is the only width that is
                // true of the destination.
                var srcStride = decoded.Width * 4;
                for (var y = 0; y < decoded.Height; y++)
                    Marshal.Copy(decoded.Rgba, y * srcStride,
                                 buffer.Address + y * buffer.RowBytes, srcStride);
            }
            return (bmp, decoded.BarTop, decoded.TextRight);
        }
        catch (Exception) { return null; }
    }
}

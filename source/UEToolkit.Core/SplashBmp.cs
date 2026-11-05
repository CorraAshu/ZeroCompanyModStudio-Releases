namespace UEToolkit.Core;

/// <summary>
/// The game's own splash image, decoded to raw pixels and nothing else.
///
/// The game ships it at `Content/Splash/Splash.bmp`, so a startup screen can show the thing a
/// person already associates with the game rather than something invented for a tool. Read from
/// THEIR install rather than bundled: shipping a copy would be redistributing somebody else's
/// artwork, and the file is right there.
///
/// DECODE ONLY, WITH NO GRAPHICS API IN IT, because two applications now want the same picture
/// and they draw it in different ways -- the ImGui Studio uploads a GL texture, the Avalonia one
/// makes a WriteableBitmap. A reader that returned either would force the other to keep a second
/// copy of this parsing, and a duplicated decoder is this repository's favourite bug: two code
/// paths disagreeing about one asset.
///
/// A BMP reader rather than an image library, because this is the only image these applications
/// decode and a dependency for one file is a poor trade. The format is narrow and checked: the
/// shipped file is 720x370, 32 bits per pixel, BI_BITFIELDS, which is the case handled. Anything
/// else returns null and the caller draws without a picture -- a missing splash is a cosmetic
/// loss and must never be a reason the tool will not start.
/// </summary>
public static class SplashBmp
{
    /// <param name="Rgba">Top-down, 8 bits per channel, four bytes per pixel.</param>
    /// <param name="BarTop">
    /// The first row of the black band the artwork ends with -- the strip the game's own splash
    /// keeps for status text, and where these applications write theirs. Equal to
    /// <paramref name="Height"/> when the image has no such band, which is the signal to write the
    /// text below the picture instead.
    /// </param>
    /// <param name="TextRight">
    /// The first column of the band that is not empty, i.e. where the engine and publisher logos
    /// begin. Text has to stop before it.
    /// </param>
    public sealed record Decoded(byte[] Rgba, int Width, int Height, int BarTop, int TextRight);

    private const int FileHeaderBytes = 14;

    /// <summary>Where the game keeps it, relative to the install folder.</summary>
    public static string PathIn(string installFolder) =>
        Path.Combine(installFolder, "SWZeroCompany", "Content", "Splash", "Splash.bmp");

    /// <summary>
    /// Decode, or null. Never throws: this runs on the way to the first frame, and there is
    /// nothing here worth failing a launch over.
    /// </summary>
    public static Decoded? Decode(string bmpPath)
    {
        try
        {
            if (!File.Exists(bmpPath)) return null;
            var raw = File.ReadAllBytes(bmpPath);
            if (raw.Length < FileHeaderBytes + 40 || raw[0] != 'B' || raw[1] != 'M') return null;

            var pixelOffset = BitConverter.ToInt32(raw, 10);
            var headerSize = BitConverter.ToInt32(raw, FileHeaderBytes);
            var width = BitConverter.ToInt32(raw, FileHeaderBytes + 4);
            var height = BitConverter.ToInt32(raw, FileHeaderBytes + 8);
            var bpp = BitConverter.ToInt16(raw, FileHeaderBytes + 14);
            var compression = BitConverter.ToInt32(raw, FileHeaderBytes + 16);

            // 32-bit only, uncompressed or BI_BITFIELDS(3). Paletted and RLE BMPs exist and this
            // does not read them; the game's file is neither.
            if (bpp != 32 || compression is not (0 or 3)) return null;
            if (width <= 0 || width > 8192 || height == 0 || Math.Abs(height) > 8192) return null;

            // A negative height means the rows are stored top-down. Both orders are legal and a
            // reader that assumes one renders the image upside down on the other.
            var topDown = height < 0;
            height = Math.Abs(height);

            var need = (long) pixelOffset + (long) width * height * 4;
            if (need > raw.Length) return null;

            // The channel order is DECLARED, in the masks, and only guessed at by readers that
            // then get it wrong on somebody's file. BI_BITFIELDS puts them after the 40-byte core
            // header; a V4/V5 header carries them at the same place.
            uint rMask = 0x00FF0000, gMask = 0x0000FF00, bMask = 0x000000FF, aMask = 0xFF000000;
            if (compression == 3 && headerSize >= 52 && raw.Length >= FileHeaderBytes + 52)
            {
                rMask = BitConverter.ToUInt32(raw, FileHeaderBytes + 40);
                gMask = BitConverter.ToUInt32(raw, FileHeaderBytes + 44);
                bMask = BitConverter.ToUInt32(raw, FileHeaderBytes + 48);
                aMask = headerSize >= 56 && raw.Length >= FileHeaderBytes + 56
                    ? BitConverter.ToUInt32(raw, FileHeaderBytes + 52)
                    : 0u;
            }

            var rShift = Shift(rMask); var gShift = Shift(gMask);
            var bShift = Shift(bMask); var aShift = Shift(aMask);

            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var sourceRow = topDown ? y : height - 1 - y;
                var src = pixelOffset + sourceRow * width * 4;
                var dst = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var v = BitConverter.ToUInt32(raw, src + x * 4);
                    pixels[dst + x * 4 + 0] = (byte) ((v & rMask) >> rShift);
                    pixels[dst + x * 4 + 1] = (byte) ((v & gMask) >> gShift);
                    pixels[dst + x * 4 + 2] = (byte) ((v & bMask) >> bShift);
                    // An alpha mask of zero means the file does not carry alpha. Reading the top
                    // byte anyway is what makes a splash render fully transparent for the people
                    // whose copy has zeroes there.
                    pixels[dst + x * 4 + 3] = aMask == 0 ? (byte) 255 : (byte) ((v & aMask) >> aShift);
                }
            }

            var (barTop, textRight) = FindBar(pixels, width, height);
            return new Decoded(pixels, width, height, barTop, textRight);
        }
        catch (Exception) { return null; }
    }

    /// <summary>How far right a mask's lowest set bit sits, so a channel can be shifted down.</summary>
    private static int Shift(uint mask)
    {
        if (mask == 0) return 0;
        var n = 0;
        while ((mask & 1) == 0) { mask >>= 1; n++; }
        return n;
    }

    /// <summary>
    /// The image's size, without decoding it.
    ///
    /// Wanted before the window exists: the window is MADE the size of the splash, so asking the
    /// decoded image how big it is would be a frame too late and the first thing anyone saw would
    /// be a window resizing itself.
    /// </summary>
    public static (int Width, int Height)? SizeOf(string bmpPath)
    {
        try
        {
            if (!File.Exists(bmpPath)) return null;
            using var f = File.OpenRead(bmpPath);
            Span<byte> head = stackalloc byte[FileHeaderBytes + 12];
            if (f.Read(head) < head.Length || head[0] != 'B' || head[1] != 'M') return null;
            var w = BitConverter.ToInt32(head[(FileHeaderBytes + 4)..]);
            var h = Math.Abs(BitConverter.ToInt32(head[(FileHeaderBytes + 8)..]));
            return w is > 0 and <= 8192 && h is > 0 and <= 8192 ? (w, h) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Find the black band along the bottom, and how far along it the logos start.
    ///
    /// Measured rather than written down. 301 and 623 are the answers for the file shipping today,
    /// and a hardcoded pair would keep being used after the game shipped a different picture --
    /// putting the status text across the artwork, or behind the Unreal logo, with nothing to say
    /// it had happened. The measurement costs one pass over a small image, once.
    ///
    /// The LEFT of each row decides where the band starts, because the logos sit on the right and
    /// would otherwise read as artwork. That is the same region the text goes in, so this is one
    /// measurement answering one question.
    /// </summary>
    private static (int Top, int Right) FindBar(byte[] px, int w, int h)
    {
        const int Dark = 40;
        var probe = Math.Max(1, (int) (w * 0.45f));

        var top = h;
        for (var y = h - 1; y >= 0; y--)
        {
            if (AnyLit(px, w, y, 0, probe, Dark)) break;
            top = y;
        }

        // Too thin to write in is the same as not having one. A couple of dark rows at the foot of
        // a picture is not a status bar, and text placed in one would be clipped.
        if (h - top < 28) return (h, w);

        var right = w;
        for (var x = 0; x < w; x++)
        {
            var lit = false;
            for (var y = top; y < h && !lit; y++)
            {
                var i = (y * w + x) * 4;
                lit = (px[i] + px[i + 1] + px[i + 2]) / 3 > Dark;
            }
            if (lit) { right = x; break; }
        }
        return (top, right);
    }

    private static bool AnyLit(byte[] px, int w, int y, int x0, int x1, int threshold)
    {
        for (var x = x0; x < x1; x++)
        {
            var i = (y * w + x) * 4;
            if ((px[i] + px[i + 1] + px[i + 2]) / 3 > threshold) return true;
        }
        return false;
    }
}

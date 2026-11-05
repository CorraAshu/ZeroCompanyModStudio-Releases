namespace UEToolkit.Core;

/// <summary>
/// A BC1 (DXT1) encoder, for authoring a texture the game will accept.
///
/// The game's UI thumbnails are PF_DXT1, 256x256, one mip, payload inline in the package -- so an
/// authored icon is a same-length substitution into a template and nothing in the package moves.
/// That is the whole reason a mod can ship an icon without the cooker.
///
/// CUE4Parse brings decoders only; there was nothing here to encode with. This is a bounding-box
/// encoder: for a 4x4 block it takes the extremes of the colours present as the two endpoints and
/// picks the nearest of the four palette entries per pixel. That is not the best BC1 an encoder
/// can produce -- a proper one fits a principal axis -- but a 256x256 icon at 4 bits per pixel is
/// not where quality is won or lost, and this has no failure mode on flat or near-flat blocks,
/// which is most of a UI thumbnail.
/// </summary>
public static class Bc1Encoder
{
    /// <summary>Bytes one BC1 block occupies: two endpoints and sixteen two-bit indices.</summary>
    public const int BlockBytes = 8;

    /// <summary>Below this, a pixel is treated as fully transparent and forces punch-through.</summary>
    private const int AlphaCutoff = 128;

    /// <summary>
    /// Encode RGBA8 pixels. <paramref name="width"/> and <paramref name="height"/> must be
    /// multiples of four, which every mip of a power-of-two texture is.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width % 4 != 0 || height % 4 != 0)
            throw new ArgumentException($"BC1 works in 4x4 blocks; {width}x{height} does not divide");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("not enough pixel data for the stated size");

        var output = new byte[width / 4 * (height / 4) * BlockBytes];
        var at = 0;
        Span<byte> block = stackalloc byte[16 * 4];

        for (var by = 0; by < height; by += 4)
        for (var bx = 0; bx < width; bx += 4)
        {
            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var src = ((by + y) * width + bx + x) * 4;
                var dst = (y * 4 + x) * 4;
                block[dst] = rgba[src];
                block[dst + 1] = rgba[src + 1];
                block[dst + 2] = rgba[src + 2];
                block[dst + 3] = rgba[src + 3];
            }

            EncodeBlock(block, output.AsSpan(at, BlockBytes));
            at += BlockBytes;
        }

        return output;
    }

    /// <summary>
    /// One 4x4 colour block, as sixteen RGBA pixels.
    ///
    /// Visible to <see cref="Bc3Encoder"/>, which is BC1 colour with the alpha carried separately
    /// in a BC4 block. It hands over pixels whose alpha is already 255, so the punch-through
    /// branch below resolves to false on its own -- BC3 needs no second copy of this and no flag
    /// to turn it off.
    /// </summary>
    internal static void EncodeBlock(ReadOnlySpan<byte> block, Span<byte> output)
    {
        // Any pixel below the cutoff forces the three-colour mode, where index 3 is transparent
        // black and the palette loses an interpolated entry. DXT1 has no other way to carry alpha.
        var punchThrough = false;
        for (var i = 0; i < 16; i++) if (block[i * 4 + 3] < AlphaCutoff) { punchThrough = true; break; }

        int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0, opaque = 0;
        for (var i = 0; i < 16; i++)
        {
            if (block[i * 4 + 3] < AlphaCutoff) continue;   // a transparent pixel has no colour to fit
            opaque++;
            int r = block[i * 4], g = block[i * 4 + 1], b = block[i * 4 + 2];
            if (r < minR) minR = r; if (r > maxR) maxR = r;
            if (g < minG) minG = g; if (g > maxG) maxG = g;
            if (b < minB) minB = b; if (b > maxB) maxB = b;
        }

        if (opaque == 0)
        {
            // Every pixel is transparent. Three-colour mode with both endpoints black and every
            // index pointing at the transparent entry.
            output[0] = 0; output[1] = 0; output[2] = 0; output[3] = 0;
            output[4] = 0xFF; output[5] = 0xFF; output[6] = 0xFF; output[7] = 0xFF;
            return;
        }

        var c0 = To565(maxR, maxG, maxB);
        var c1 = To565(minR, minG, minB);

        // The MODE is chosen by the ordering of the two endpoints, not by a flag, so the ordering
        // has to be forced -- and when both endpoints quantise to the same value the required
        // ordering may be unreachable, which is why the equal case is handled rather than assumed.
        if (punchThrough)
        {
            if (c0 > c1) (c0, c1) = (c1, c0);
            if (c0 == c1 && c0 > 0) c0--;              // c0 < c1 keeps three-colour mode
        }
        else
        {
            if (c0 < c1) (c0, c1) = (c1, c0);
            if (c0 == c1)
            {
                // A flat block. Four-colour mode needs c0 > c1; nudging c1 down keeps the palette
                // at the block's colour and avoids falling into the transparent mode by accident.
                if (c1 > 0) c1--; else c0++;
            }
        }

        Span<int> paletteR = stackalloc int[4];
        Span<int> paletteG = stackalloc int[4];
        Span<int> paletteB = stackalloc int[4];
        BuildPalette(c0, c1, punchThrough, paletteR, paletteG, paletteB);

        uint indices = 0;
        for (var i = 0; i < 16; i++)
        {
            uint pick;
            if (punchThrough && block[i * 4 + 3] < AlphaCutoff)
            {
                pick = 3;                                // the transparent entry
            }
            else
            {
                int r = block[i * 4], g = block[i * 4 + 1], b = block[i * 4 + 2];
                var best = int.MaxValue;
                pick = 0;
                var entries = punchThrough ? 3 : 4;      // entry 3 is transparent in that mode
                for (var p = 0; p < entries; p++)
                {
                    var dr = r - paletteR[p];
                    var dg = g - paletteG[p];
                    var db = b - paletteB[p];
                    var d = dr * dr + dg * dg + db * db;
                    if (d >= best) continue;
                    best = d;
                    pick = (uint) p;
                }
            }
            indices |= pick << (i * 2);
        }

        output[0] = (byte) c0; output[1] = (byte) (c0 >> 8);
        output[2] = (byte) c1; output[3] = (byte) (c1 >> 8);
        output[4] = (byte) indices; output[5] = (byte) (indices >> 8);
        output[6] = (byte) (indices >> 16); output[7] = (byte) (indices >> 24);
    }

    private static void BuildPalette(ushort c0, ushort c1, bool punchThrough,
                                     Span<int> r, Span<int> g, Span<int> b)
    {
        From565(c0, out r[0], out g[0], out b[0]);
        From565(c1, out r[1], out g[1], out b[1]);

        if (punchThrough)
        {
            // Three colours: the midpoint, then transparent.
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2;
            r[3] = g[3] = b[3] = 0;
        }
        else
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3;
        }
    }

    private static ushort To565(int r, int g, int b) =>
        (ushort) (((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    /// <summary>
    /// Expand 565 back the way hardware does -- replicating the high bits into the low ones, not
    /// shifting and leaving zeroes. Getting this wrong biases every colour dark and the error only
    /// shows as a picture that is slightly too dim, which is easy to blame on the source image.
    /// </summary>
    private static void From565(ushort c, out int r, out int g, out int b)
    {
        var r5 = (c >> 11) & 0x1F;
        var g6 = (c >> 5) & 0x3F;
        var b5 = c & 0x1F;
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }
}

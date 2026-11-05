namespace UEToolkit.Core;

/// <summary>
/// A BC4 block: ONE channel, sixteen pixels, eight bytes.
///
/// Not used on its own by anything here, and that is the point -- it is the half that
/// <see cref="Bc3Encoder"/> uses for alpha and that <see cref="Bc5Encoder"/> uses twice for a
/// normal map. Writing it once is what keeps those two from drifting apart.
///
/// The format stores two endpoints and sixteen THREE-bit indices, and like BC1 it picks its mode
/// from the ORDER of the endpoints rather than a flag: `r0 > r1` gives eight interpolated values,
/// and `r0 &lt;= r1` gives six plus hard 0 and 255. Eight is always the better fit for image data, so
/// the maximum goes first -- and the degenerate case where they are equal lands in the six-value
/// mode and still encodes a flat block exactly, because both endpoints hold the same value.
/// </summary>
public static class Bc4Encoder
{
    public const int BlockBytes = 8;

    /// <summary>
    /// Encode one 4x4 block taken from <paramref name="source"/> at a stride, reading one byte per
    /// pixel starting at <paramref name="channel"/>.
    ///
    /// Reading through a stride rather than taking a packed block is what lets BC5 pull R and G
    /// out of the same RGBA buffer without copying it twice.
    /// </summary>
    public static void EncodeBlock(ReadOnlySpan<byte> source, int width, int blockX, int blockY,
                                   int pixelStride, int channel, Span<byte> output)
    {
        Span<byte> values = stackalloc byte[16];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
            values[y * 4 + x] = source[((blockY + y) * width + blockX + x) * pixelStride + channel];

        int min = 255, max = 0;
        for (var i = 0; i < 16; i++)
        {
            if (values[i] < min) min = values[i];
            if (values[i] > max) max = values[i];
        }

        // Maximum first, so `r0 > r1` selects the eight-value mode wherever the block is not flat.
        int r0 = max, r1 = min;

        Span<int> palette = stackalloc int[8];
        BuildPalette(r0, r1, palette);

        ulong indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = int.MaxValue;
            var pick = 0;
            for (var p = 0; p < 8; p++)
            {
                var d = values[i] - palette[p];
                d *= d;
                if (d >= best) continue;
                best = d;
                pick = p;
            }
            indices |= (ulong) pick << (i * 3);
        }

        output[0] = (byte) r0;
        output[1] = (byte) r1;
        for (var i = 0; i < 6; i++) output[2 + i] = (byte) (indices >> (i * 8));
    }

    private static void BuildPalette(int r0, int r1, Span<int> p)
    {
        p[0] = r0;
        p[1] = r1;
        if (r0 > r1)
        {
            // Eight interpolated values, evenly spaced between the endpoints.
            for (var i = 1; i <= 6; i++) p[i + 1] = ((7 - i) * r0 + i * r1) / 7;
        }
        else
        {
            // Six interpolated, then the two hard values the mode adds. Reached only when the
            // block is flat, where every entry below is the same number anyway.
            for (var i = 1; i <= 4; i++) p[i + 1] = ((5 - i) * r0 + i * r1) / 5;
            p[6] = 0;
            p[7] = 255;
        }
    }
}

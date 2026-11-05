namespace UEToolkit.Core;

/// <summary>
/// BC3 (DXT5): a BC4 alpha block followed by a BC1 colour block, sixteen bytes per 4x4.
///
/// This is what the game's character colour maps are -- `T_Dozen_Head_D` is PF_DXT5 at 4096x4096 --
/// so it is the format a repainted skin has to come back as. BC1 could not carry those: it has one
/// bit of alpha and DXT5 has eight, which matters for anything with a mask in the alpha channel.
///
/// Neither half is written here. The alpha is <see cref="Bc4Encoder"/> and the colour is
/// <see cref="Bc1Encoder"/>'s block with alpha handed over as opaque, so BC1's punch-through
/// branch resolves to false by itself. Two encoders, three formats, one copy of each algorithm.
/// </summary>
public static class Bc3Encoder
{
    public const int BlockBytes = 16;

    /// <summary>Encode RGBA8 pixels. Both dimensions must divide by four.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width % 4 != 0 || height % 4 != 0)
            throw new ArgumentException($"BC3 works in 4x4 blocks; {width}x{height} does not divide");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("not enough pixel data for the stated size");

        var output = new byte[width / 4 * (height / 4) * BlockBytes];
        var at = 0;

        // The colour half is fed opaque pixels: alpha is already carried by the BC4 block, and
        // leaving the real alpha in would let a transparent pixel drag BC1 into punch-through
        // mode -- losing a palette entry to an alpha this format does not store there.
        Span<byte> opaque = stackalloc byte[16 * 4];

        for (var by = 0; by < height; by += 4)
        for (var bx = 0; bx < width; bx += 4)
        {
            Bc4Encoder.EncodeBlock(rgba, width, bx, by, 4, 3, output.AsSpan(at, Bc4Encoder.BlockBytes));

            for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
            {
                var src = ((by + y) * width + bx + x) * 4;
                var dst = (y * 4 + x) * 4;
                opaque[dst] = rgba[src];
                opaque[dst + 1] = rgba[src + 1];
                opaque[dst + 2] = rgba[src + 2];
                opaque[dst + 3] = 255;
            }

            Bc1Encoder.EncodeBlock(opaque, output.AsSpan(at + Bc4Encoder.BlockBytes, Bc1Encoder.BlockBytes));
            at += BlockBytes;
        }

        return output;
    }
}

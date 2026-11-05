namespace UEToolkit.Core;

/// <summary>
/// BC5: two BC4 blocks, sixteen bytes per 4x4, carrying two channels at eight bits each.
///
/// This is what the game's normal maps are -- `T_Dozen_Head_N` is PF_BC5 at 4096x4096 -- and the
/// reason the format exists is that a normal map does not need three channels. X and Y are stored
/// and Z is rebuilt in the shader as `sqrt(1 - x^2 - y^2)`, which is exact for a unit vector and
/// spends the saved channel on precision instead.
///
/// So a repainted normal map's BLUE CHANNEL IS DISCARDED HERE, and that is the format doing what
/// it is for rather than a shortcut: whatever blue an editor wrote is recomputed at render time.
/// Worth knowing before wondering why an exported and re-imported normal map is not byte-identical.
/// </summary>
public static class Bc5Encoder
{
    public const int BlockBytes = 16;

    /// <summary>Encode RGBA8 pixels, keeping red and green. Both dimensions must divide by four.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width % 4 != 0 || height % 4 != 0)
            throw new ArgumentException($"BC5 works in 4x4 blocks; {width}x{height} does not divide");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("not enough pixel data for the stated size");

        var output = new byte[width / 4 * (height / 4) * BlockBytes];
        var at = 0;

        for (var by = 0; by < height; by += 4)
        for (var bx = 0; bx < width; bx += 4)
        {
            Bc4Encoder.EncodeBlock(rgba, width, bx, by, 4, 0, output.AsSpan(at, Bc4Encoder.BlockBytes));
            Bc4Encoder.EncodeBlock(rgba, width, bx, by, 4, 1,
                                   output.AsSpan(at + Bc4Encoder.BlockBytes, Bc4Encoder.BlockBytes));
            at += BlockBytes;
        }

        return output;
    }
}

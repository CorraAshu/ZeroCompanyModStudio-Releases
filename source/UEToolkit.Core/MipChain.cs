namespace UEToolkit.Core;

/// <summary>
/// Build a texture's smaller levels from its full-size pixels.
///
/// A cooked texture is not one image, it is a chain: 4096, 2048, 1024, and so on down. Replacing
/// only the top level leaves every smaller one showing the ORIGINAL artwork, so a repainted skin
/// would look right up close and revert to the game's as the camera pulled back -- which reads as
/// a rendering bug rather than a missing step.
///
/// How the halving is done depends on what the numbers MEAN, which is why this takes a
/// <see cref="Kind"/> rather than just averaging:
///
///   Srgb     colour is stored gamma-encoded, so averaging the stored bytes is arithmetic on the
///            wrong quantity and comes out too dark. Converted to linear, averaged, converted
///            back. The error is small per level and compounds down the chain.
///
///   Linear   masks, roughness, metalness: the byte IS the value. Averaged directly, and running
///            those through the sRGB path would be the same mistake in the other direction.
///
///   Normal   a normal map holds unit VECTORS. Averaging two of them gives something shorter than
///            unit, which flattens the lighting a little at every level; so Z is reconstructed,
///            the vectors are averaged and renormalised, and X and Y are written back.
/// </summary>
public static class MipChain
{
    public enum Kind { Srgb, Linear, Normal }

    public sealed record Level(int Width, int Height, byte[] Rgba);

    /// <summary>sRGB byte to linear, once, because the alternative is a pow() per channel per pixel.</summary>
    private static readonly float[] ToLinear = BuildToLinear();

    private static float[] BuildToLinear()
    {
        var t = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255f;
            t[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return t;
    }

    private static byte ToSrgb(float linear)
    {
        var c = linear <= 0.0031308f ? linear * 12.92f
                                     : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte) Math.Clamp((int) MathF.Round(c * 255f), 0, 255);
    }

    /// <summary>
    /// Every level from the full size down to 1x1, the first being the input itself.
    ///
    /// The whole chain, not a truncated one: the caller replacing a cooked texture has to produce
    /// exactly as many levels as the original declares, and it is easier to drop the tail than to
    /// discover halfway through that it is missing.
    /// </summary>
    public static List<Level> Build(byte[] rgba, int width, int height, Kind kind)
    {
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("not enough pixel data for the stated size");

        var levels = new List<Level> { new(width, height, rgba) };
        while (width > 1 || height > 1)
        {
            var nw = Math.Max(1, width / 2);
            var nh = Math.Max(1, height / 2);
            levels.Add(new Level(nw, nh, Halve(levels[^1].Rgba, width, height, nw, nh, kind)));
            width = nw;
            height = nh;
        }
        return levels;
    }

    private static byte[] Halve(byte[] src, int sw, int sh, int dw, int dh, Kind kind)
    {
        var dst = new byte[dw * dh * 4];

        for (var y = 0; y < dh; y++)
        for (var x = 0; x < dw; x++)
        {
            // Clamped rather than wrapped, so a 1-pixel-wide level averages the column with
            // itself instead of reading the opposite edge.
            var x0 = Math.Min(x * 2, sw - 1);
            var x1 = Math.Min(x * 2 + 1, sw - 1);
            var y0 = Math.Min(y * 2, sh - 1);
            var y1 = Math.Min(y * 2 + 1, sh - 1);

            // Four plain locals, NOT a stackalloc.
            //
            // A `stackalloc` here was a stack overflow waiting for a big enough texture: stack
            // memory taken inside a loop is not released until the METHOD returns, so this held
            // sixteen bytes per pixel for the whole pass. At 256x256 that is a megabyte and it
            // survived; at 4096x4096 it is 268 MB and the process died. The synthetic test never
            // reached it -- only a real 4K texture did.
            var a0 = (y0 * sw + x0) * 4;
            var a1 = (y0 * sw + x1) * 4;
            var a2 = (y1 * sw + x0) * 4;
            var a3 = (y1 * sw + x1) * 4;

            var o = (y * dw + x) * 4;

            if (kind == Kind.Normal)
            {
                float nx = 0, ny = 0, nz = 0;

                // Unrolled through a local function rather than iterated over a collection, for
                // the same reason as above: anything allocated per pixel in this loop is held
                // until the whole pass finishes. The first fix for the overflow put a `stackalloc`
                // back in to iterate the four offsets, which was the identical bug in a new shape.
                void Accumulate(int i)
                {
                    // Byte 0..255 back to the -1..1 the channel encodes.
                    var vx = src[i] / 127.5f - 1f;
                    var vy = src[i + 1] / 127.5f - 1f;
                    // Z from the other two, which is what the shader does with a BC5 map.
                    nx += vx;
                    ny += vy;
                    nz += MathF.Sqrt(Math.Max(0f, 1f - vx * vx - vy * vy));
                }

                Accumulate(a0); Accumulate(a1); Accumulate(a2); Accumulate(a3);

                var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-6f) { nx /= len; ny /= len; }
                else { nx = 0; ny = 0; }

                dst[o] = (byte) Math.Clamp((int) MathF.Round((nx + 1f) * 127.5f), 0, 255);
                dst[o + 1] = (byte) Math.Clamp((int) MathF.Round((ny + 1f) * 127.5f), 0, 255);
                dst[o + 2] = 255;   // rebuilt in the shader; BC5 would discard it anyway
                dst[o + 3] = 255;
                continue;
            }

            for (var c = 0; c < 4; c++)
            {
                // Alpha is never gamma-encoded, whatever the colour channels are.
                var gamma = kind == Kind.Srgb && c < 3;
                if (gamma)
                {
                    var sum = ToLinear[src[a0 + c]] + ToLinear[src[a1 + c]] +
                              ToLinear[src[a2 + c]] + ToLinear[src[a3 + c]];
                    dst[o + c] = ToSrgb(sum / 4f);
                }
                else
                {
                    var sum = src[a0 + c] + src[a1 + c] + src[a2 + c] + src[a3 + c];
                    dst[o + c] = (byte) (sum / 4);
                }
            }
        }

        return dst;
    }

    /// <summary>
    /// A level grown to a whole 4x4 block, for the tail of the chain.
    ///
    /// Block formats have no size below 4x4: a 2x2 mip still occupies one full block, and the
    /// encoders here refuse anything that does not divide by four rather than quietly reading past
    /// the end of a buffer. Edge pixels are repeated, which is what the sampler sees anyway.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height) PadToBlock(byte[] rgba, int width, int height)
    {
        if (width >= 4 && height >= 4) return (rgba, width, height);

        var pw = Math.Max(4, width);
        var ph = Math.Max(4, height);
        var padded = new byte[pw * ph * 4];

        for (var y = 0; y < ph; y++)
        for (var x = 0; x < pw; x++)
        {
            var sx = Math.Min(x, width - 1);
            var sy = Math.Min(y, height - 1);
            Array.Copy(rgba, (sy * width + sx) * 4, padded, (y * pw + x) * 4, 4);
        }
        return (padded, pw, ph);
    }
}

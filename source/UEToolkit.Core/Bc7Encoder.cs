namespace UEToolkit.Core;

/// <summary>
/// BC7, sixteen bytes per 4x4, written in TWO of its eight modes and the better of the two kept per block:
///
///   MODE 6 -- one RGBA line: endpoints of 7 bits a channel plus a shared low bit each, sixteen steps along it. Colour
///   and alpha at 8 effective bits, the smooth-gradient mode quick encoders lean on.
///   MODE 5 -- colour and alpha on SEPARATE lines: 7-bit colour endpoints, 8-bit alpha endpoints, four steps each,
///   chosen independently. A block whose alpha does not follow its colour -- an opacity map's edge across a colour
///   ramp -- has no single line through it; mode 6 alone left errors over 100 on the skin-marking maps.
///
/// WHY IT EXISTS: an Import over a BC7 texture said "no encoder for PF_BC7" and wrote nothing. The game stores 1,423
/// textures that way (portraits, skin markings, many weapon and UI maps -- counted by the private --texscan).
///
/// The partitioned modes (0-3, 7) are not written: they buy quality where three or more unrelated colours meet in one
/// block, and cost a search over 64 partition shapes. The decoder does not care which mode wrote a block. Endpoints
/// come from each line's principal axis (power iteration on the covariance), are refined by least squares against the
/// steps chosen, and the block with the least squared error is written. Checked by --bctest against CUE4Parse's own
/// BC7 decoder, and by --vtauthor round trips of the game's own BC7 pages.
/// </summary>
public static class Bc7Encoder
{
    public const int BlockBytes = 16;

    /// <summary>The specification's interpolation weights, out of 64: sixteen steps (mode 6) and four (mode 5).</summary>
    private static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
    private static readonly int[] Weights2 = [0, 21, 43, 64];

    /// <summary>Encode RGBA8 pixels. Both dimensions must divide by four.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width % 4 != 0 || height % 4 != 0)
            throw new ArgumentException($"BC7 works in 4x4 blocks; {width}x{height} does not divide");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("not enough pixel data for the stated size");

        var source = rgba.ToArray();
        var blocksX = width / 4;
        var output = new byte[blocksX * (height / 4) * BlockBytes];

        // A ROW OF BLOCKS PER TASK: a 4K map is a million blocks and each is independent.
        Parallel.For(0, height / 4, by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (var bx = 0; bx < blocksX; bx++)
            {
                for (var y = 0; y < 4; y++)
                    source.AsSpan(((by * 4 + y) * width + bx * 4) * 4, 16).CopyTo(block.Slice(y * 16, 16));
                EncodeBlock(block, output.AsSpan((by * blocksX + bx) * BlockBytes, BlockBytes));
            }
        });

        return output;
    }

    /// <summary>One 4x4 block: sixty-four bytes of RGBA in, sixteen bytes of BC7 out -- whichever mode fits it better.</summary>
    public static void EncodeBlock(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        var six = Mode6(px, dst);
        if (six == 0) return;

        Span<byte> five = stackalloc byte[BlockBytes];
        if (Mode5(px, five) < six) five.CopyTo(dst);
    }

    // ------------------------------------------------------------------ mode 6

    private static long Mode6(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        Span<float> lo = stackalloc float[4];
        Span<float> hi = stackalloc float[4];
        Axis(px, 0, 4, lo, hi);

        Span<int> e0 = stackalloc int[4];
        Span<int> e1 = stackalloc int[4];
        Span<int> best0 = stackalloc int[4];
        Span<int> best1 = stackalloc int[4];
        Span<byte> idx = stackalloc byte[16];
        Span<byte> bestIdx = stackalloc byte[16];
        var bestErr = long.MaxValue;
        int bestP0 = 0, bestP1 = 0;

        // THREE ROUNDS: the axis guess, then the least-squares endpoints for the steps the best so far chose.
        for (var round = 0; round < 3; round++)
        {
            for (var p0 = 0; p0 < 2; p0++)
            for (var p1 = 0; p1 < 2; p1++)
            {
                for (var c = 0; c < 4; c++) { e0[c] = QuantiseP(lo[c], p0); e1[c] = QuantiseP(hi[c], p1); }
                var err = Assign(px, 0, 4, e0, e1, Weights4, idx);
                if (err < bestErr)
                {
                    bestErr = err; bestP0 = p0; bestP1 = p1;
                    e0.CopyTo(best0); e1.CopyTo(best1); idx.CopyTo(bestIdx);
                }
            }

            if (bestErr == 0 || !Refit(px, 0, 4, bestIdx, Weights4, lo, hi)) break;
        }

        // THE FIRST PIXEL'S STEP HAS ITS TOP BIT FIXED AT ZERO: a block that wants 8 or more is written the other way round.
        if (bestIdx[0] >= 8)
        {
            for (var c = 0; c < 4; c++) (best0[c], best1[c]) = (best1[c], best0[c]);
            (bestP0, bestP1) = (bestP1, bestP0);
            for (var i = 0; i < 16; i++) bestIdx[i] = (byte) (15 - bestIdx[i]);
        }

        // 128 bits, least significant first: the mode (six zeros and a one), R0 R1 G0 G1 B0 B1 A0 A1 at seven bits, the
        // two low bits, then sixteen 4-bit steps of which the first has three.
        dst.Clear();
        var pos = 0;
        Put(dst, ref pos, 1 << 6, 7);
        for (var c = 0; c < 4; c++) { Put(dst, ref pos, best0[c] >> 1, 7); Put(dst, ref pos, best1[c] >> 1, 7); }
        Put(dst, ref pos, bestP0, 1);
        Put(dst, ref pos, bestP1, 1);
        Put(dst, ref pos, bestIdx[0], 3);
        for (var i = 1; i < 16; i++) Put(dst, ref pos, bestIdx[i], 4);
        return bestErr;
    }

    /// <summary>An endpoint channel to the nearest value mode 6 can hold with this low bit: 2 * (0..127) + p.</summary>
    private static int QuantiseP(float value, int p) =>
        Math.Clamp((int) MathF.Round((value - p) / 2f), 0, 127) * 2 + p;

    // ------------------------------------------------------------------ mode 5

    private static long Mode5(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        // COLOUR: a line through RGB, 7-bit endpoints (expanded by repeating the top bit), four steps.
        Span<float> lo = stackalloc float[4];
        Span<float> hi = stackalloc float[4];
        Axis(px, 0, 3, lo, hi);

        Span<int> e0 = stackalloc int[4];
        Span<int> e1 = stackalloc int[4];
        Span<int> best0 = stackalloc int[4];
        Span<int> best1 = stackalloc int[4];
        Span<byte> idx = stackalloc byte[16];
        Span<byte> ci = stackalloc byte[16];
        var colourErr = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            for (var c = 0; c < 3; c++) { e0[c] = Expand7(Quantise7(lo[c])); e1[c] = Expand7(Quantise7(hi[c])); }
            var err = Assign(px, 0, 3, e0, e1, Weights2, idx);
            if (err < colourErr) { colourErr = err; e0.CopyTo(best0); e1.CopyTo(best1); idx.CopyTo(ci); }
            if (colourErr == 0 || !Refit(px, 0, 3, ci, Weights2, lo, hi)) break;
        }

        // ALPHA: its own line, exact 8-bit endpoints, four steps.
        Span<float> alo = stackalloc float[4];
        Span<float> ahi = stackalloc float[4];
        Axis(px, 3, 1, alo, ahi);
        Span<int> a0 = stackalloc int[4];
        Span<int> a1 = stackalloc int[4];
        Span<int> bestA0 = stackalloc int[4];
        Span<int> bestA1 = stackalloc int[4];
        Span<byte> ai = stackalloc byte[16];
        var alphaErr = long.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            a0[3] = Math.Clamp((int) MathF.Round(alo[3]), 0, 255);
            a1[3] = Math.Clamp((int) MathF.Round(ahi[3]), 0, 255);
            var err = Assign(px, 3, 1, a0, a1, Weights2, idx);
            if (err < alphaErr) { alphaErr = err; a0.CopyTo(bestA0); a1.CopyTo(bestA1); idx.CopyTo(ai); }
            if (alphaErr == 0 || !Refit(px, 3, 1, ai, Weights2, alo, ahi)) break;
        }

        // EACH SET'S FIRST STEP has its top bit fixed at zero, and each set is turned round on its own.
        if (ci[0] >= 2)
        {
            for (var c = 0; c < 3; c++) (best0[c], best1[c]) = (best1[c], best0[c]);
            for (var i = 0; i < 16; i++) ci[i] = (byte) (3 - ci[i]);
        }
        if (ai[0] >= 2)
        {
            (bestA0[3], bestA1[3]) = (bestA1[3], bestA0[3]);
            for (var i = 0; i < 16; i++) ai[i] = (byte) (3 - ai[i]);
        }

        // 128 bits: the mode (five zeros and a one), a rotation of 0 (alpha is alpha), R0 R1 G0 G1 B0 B1 at seven bits,
        // A0 A1 at eight, sixteen 2-bit colour steps and sixteen 2-bit alpha steps, the first of each with one bit.
        dst.Clear();
        var pos = 0;
        Put(dst, ref pos, 1 << 5, 6);
        Put(dst, ref pos, 0, 2);
        for (var c = 0; c < 3; c++) { Put(dst, ref pos, best0[c] >> 1, 7); Put(dst, ref pos, best1[c] >> 1, 7); }
        Put(dst, ref pos, bestA0[3], 8);
        Put(dst, ref pos, bestA1[3], 8);
        Put(dst, ref pos, ci[0], 1);
        for (var i = 1; i < 16; i++) Put(dst, ref pos, ci[i], 2);
        Put(dst, ref pos, ai[0], 1);
        for (var i = 1; i < 16; i++) Put(dst, ref pos, ai[i], 2);
        return colourErr + alphaErr;
    }

    /// <summary>The 7-bit value whose expansion lands nearest an 8-bit endpoint.</summary>
    private static int Quantise7(float value)
    {
        var q = Math.Clamp((int) MathF.Round(value * 127f / 255f), 0, 127);
        var best = q;
        for (var t = Math.Max(0, q - 1); t <= Math.Min(127, q + 1); t++)
            if (Math.Abs(Expand7(t) - value) < Math.Abs(Expand7(best) - value)) best = t;
        return best;
    }

    /// <summary>A 7-bit endpoint as the decoder widens it: the top bit repeated into the bottom.</summary>
    private static int Expand7(int q) => (q << 1) | (q >> 6);

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// The line through channels [first, first + count) of the block, as the two ends of its pixels' projection onto
    /// the principal axis. Written into the same channel positions of <paramref name="lo"/> and <paramref name="hi"/>.
    /// </summary>
    private static void Axis(ReadOnlySpan<byte> px, int first, int count, Span<float> lo, Span<float> hi)
    {
        Span<float> mean = stackalloc float[4];
        for (var i = 0; i < 16; i++)
            for (var c = 0; c < count; c++) mean[c] += px[i * 4 + first + c];
        for (var c = 0; c < count; c++) mean[c] /= 16f;

        Span<float> cov = stackalloc float[16];
        for (var i = 0; i < 16; i++)
            for (var a = 0; a < count; a++)
            {
                var da = px[i * 4 + first + a] - mean[a];
                for (var b = 0; b < count; b++) cov[a * 4 + b] += da * (px[i * 4 + first + b] - mean[b]);
            }

        // POWER ITERATION from the channel with the largest spread: eight steps settle a small symmetric matrix. NOT
        // from all-ones -- a block whose red rises as its green falls has an axis at right angles to that, the first
        // step comes out zero, and the block would be written flat.
        var widest = 0;
        for (var c = 1; c < count; c++) if (cov[c * 4 + c] > cov[widest * 4 + widest]) widest = c;
        Span<float> v = stackalloc float[4];
        Span<float> next = stackalloc float[4];
        v[widest] = 1;
        for (var it = 0; it < 8; it++)
        {
            var norm = 0f;
            for (var a = 0; a < count; a++)
            {
                var s = 0f;
                for (var b = 0; b < count; b++) s += cov[a * 4 + b] * v[b];
                next[a] = s;
                norm = Math.Max(norm, Math.Abs(s));
            }
            if (norm < 1e-6f) break;
            for (var a = 0; a < count; a++) v[a] = next[a] / norm;
        }

        float tMin = float.MaxValue, tMax = float.MinValue;
        for (var i = 0; i < 16; i++)
        {
            var t = 0f;
            for (var c = 0; c < count; c++) t += (px[i * 4 + first + c] - mean[c]) * v[c];
            tMin = Math.Min(tMin, t);
            tMax = Math.Max(tMax, t);
        }

        var len2 = 0f;
        for (var c = 0; c < count; c++) len2 += v[c] * v[c];
        if (len2 < 1e-12f) len2 = 1;
        for (var c = 0; c < count; c++)
        {
            lo[first + c] = Math.Clamp(mean[c] + v[c] * tMin / len2, 0, 255);
            hi[first + c] = Math.Clamp(mean[c] + v[c] * tMax / len2, 0, 255);
        }
    }

    /// <summary>
    /// Each pixel's nearest step along a line through channels [first, first + count), given the endpoints as the
    /// decoder will see them; the squared error over those channels.
    /// </summary>
    private static long Assign(ReadOnlySpan<byte> px, int first, int count, ReadOnlySpan<int> e0, ReadOnlySpan<int> e1,
                               int[] weights, Span<byte> idx)
    {
        var steps = weights.Length;
        Span<int> palette = stackalloc int[64];
        for (var s = 0; s < steps; s++)
            for (var c = first; c < first + count; c++)
                palette[s * 4 + c] = ((64 - weights[s]) * e0[c] + weights[s] * e1[c] + 32) >> 6;

        long total = 0;
        for (var i = 0; i < 16; i++)
        {
            int best = int.MaxValue, at = 0;
            for (var s = 0; s < steps; s++)
            {
                var d = 0;
                for (var c = first; c < first + count; c++)
                {
                    var diff = px[i * 4 + c] - palette[s * 4 + c];
                    d += diff * diff;
                }
                if (d < best) { best = d; at = s; }
            }
            idx[i] = (byte) at;
            total += best;
        }
        return total;
    }

    /// <summary>
    /// The endpoints that best fit the chosen steps, by least squares per channel. False when every pixel chose one
    /// step, which leaves the fit undetermined (and the line is already as good as one colour gets).
    /// </summary>
    private static bool Refit(ReadOnlySpan<byte> px, int first, int count, ReadOnlySpan<byte> idx, int[] weights,
                              Span<float> lo, Span<float> hi)
    {
        double aa = 0, ab = 0, bb = 0;
        Span<double> xa = stackalloc double[4];
        Span<double> xb = stackalloc double[4];
        for (var i = 0; i < 16; i++)
        {
            var w = weights[idx[i]] / 64.0;
            var u = 1 - w;
            aa += u * u; ab += u * w; bb += w * w;
            for (var c = first; c < first + count; c++) { xa[c] += u * px[i * 4 + c]; xb[c] += w * px[i * 4 + c]; }
        }

        var det = aa * bb - ab * ab;
        if (Math.Abs(det) < 1e-9) return false;
        for (var c = first; c < first + count; c++)
        {
            lo[c] = (float) Math.Clamp((bb * xa[c] - ab * xb[c]) / det, 0, 255);
            hi[c] = (float) Math.Clamp((aa * xb[c] - ab * xa[c]) / det, 0, 255);
        }
        return true;
    }

    /// <summary>The low <paramref name="bits"/> of a value into the block at a bit position, least significant first.</summary>
    private static void Put(Span<byte> dst, ref int pos, int value, int bits)
    {
        for (var i = 0; i < bits; i++, pos++)
            if (((value >> i) & 1) != 0) dst[pos >> 3] |= (byte) (1 << (pos & 7));
    }
}

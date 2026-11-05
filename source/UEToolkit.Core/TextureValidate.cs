using System.Globalization;

namespace UEToolkit.Core;

/// <summary>
/// Checks the textures an export actually wrote.
///
/// A file on disk with a plausible size is not evidence that anything decoded — the mesh
/// equivalent of "the code ran, therefore it worked", which cost this project four extractions.
/// A texture pass has already once reported success while writing nothing.
///
/// Two checks earn their place:
///
/// **Dimensions against what the material declared.** `atlas-slots.tsv` records the cooked
/// `PlatformData` size of every atlas texture, so the written image can be compared against an
/// independent statement of what it should be. A mismatch means the decode produced a different
/// mip, or a different texture entirely — and since the atlas grid is derived from those same
/// dimensions, a mismatch also means the UVs baked against it are wrong.
///
/// **Flat images.** A failed decode frequently yields a uniform fill rather than an error. That
/// is indistinguishable from a legitimate flat texture by size alone, and there are plenty of
/// those (an engine's default black texture, a flat white one), so it is reported rather than failed.
/// </summary>
public static class TextureValidate
{
    public sealed record TexReport(string File, int Width, int Height, bool Flat,
                                   int ExpectedW, int ExpectedH, byte[]? FlatColour = null)
    {
        public bool SizeKnown => ExpectedW > 0 && ExpectedH > 0;
        public bool SizeMatches => !SizeKnown || (Width == ExpectedW && Height == ExpectedH);

        /// <summary>
        /// Uniform (0,0,0,0) — transparent black. This is not a texture anyone authors; it is
        /// what an empty buffer looks like. A legitimately flat map is opaque: a black emissive
        /// is (0,0,0,255) and an unperturbed normal is roughly (128,128,255,255). One character's
        /// normal and ORME maps came out this way, 6144x4096 of nothing, while its diffuse
        /// beside them decoded fine.
        /// </summary>
        public bool Empty => Flat && FlatColour is { Length: >= 4 } c &&
                             c[0] == 0 && c[1] == 0 && c[2] == 0 && c[3] == 0;

        public string Colour => FlatColour is null ? "" :
            "(" + string.Join(",", FlatColour) + ")";
    }

    /// <summary>TGA header: 18 bytes, little-endian width/height at 12 and 14.</summary>
    private static (int W, int H, int Bpp, int DataOffset)? TgaHeader(byte[] d)
    {
        if (d.Length < 18) return null;
        int idLen = d[0], colourMapType = d[1];
        var w = BitConverter.ToUInt16(d, 12);
        var h = BitConverter.ToUInt16(d, 14);
        int bpp = d[16];
        if (w == 0 || h == 0 || (bpp != 8 && bpp != 16 && bpp != 24 && bpp != 32)) return null;
        var offset = 18 + idLen + (colourMapType == 1 ? BitConverter.ToUInt16(d, 5) * (d[7] / 8) : 0);
        return (w, h, bpp, offset);
    }

    /// <summary>
    /// Is every sampled pixel identical? Sampled rather than exhaustive: an 8K atlas is 100 MB
    /// of pixels and the question only needs an answer, not a census.
    /// </summary>
    /// <summary>
    /// Walks the image with a stride COPRIME to its width, so the samples cross columns.
    ///
    /// Stepping by pixels/N aliases catastrophically: on a 6144-wide image that step is exactly
    /// one row, so every sample lands in the same column. A transparent left edge then makes the
    /// whole texture read as uniform — which is precisely what happened, reporting
    /// a character's normal map as an empty buffer when its channels in fact span 0..255. A check
    /// that samples a single column is not sampling the image.
    /// </summary>
    private const int Walk = 7919;

    private static byte[]? FlatColour(byte[] d, int offset, int bpp, int pixels)
    {
        var stride = bpp / 8;
        if (offset + stride > d.Length) return null;
        var first = d.AsSpan(offset, stride).ToArray();
        var samples = Math.Min(pixels, 8192);
        for (var i = 1; i < samples; i++)
        {
            var p = offset + (int) ((long) i * Walk % pixels) * stride;
            if (p + stride > d.Length) continue;
            for (var b = 0; b < stride; b++)
                if (d[p + b] != first[b]) return null;
        }
        // TGA stores BGRA; report RGBA so the numbers read the way anyone expects.
        return stride >= 4 ? [first[2], first[1], first[0], first[3]]
             : stride == 3 ? [first[2], first[1], first[0], (byte) 255]
             : first;
    }

    public sealed record ChannelStats(int Width, int Height,
        (byte Min, byte Max, double Mean) R, (byte Min, byte Max, double Mean) G,
        (byte Min, byte Max, double Mean) B, (byte Min, byte Max, double Mean) A);

    /// <summary>
    /// Per-channel range and mean of a written TGA.
    ///
    /// A packed map's filename claims a layout — ORME says occlusion/roughness/metal/emissive —
    /// and the only way to know whether that is true of THIS file is to look at the channels
    /// apart. It also settles "empty or merely plain", which file size cannot: an all-zero alpha
    /// beside varying colour is a mask that failed to write, while a flat 255 alpha is normal.
    /// </summary>
    public static ChannelStats? Channels(string tgaPath, int sampleTarget = 200_000)
    {
        byte[] d;
        try { d = File.ReadAllBytes(tgaPath); } catch { return null; }
        var hdr = TgaHeader(d);
        if (hdr is not { } h) return null;
        var stride = h.Bpp / 8;
        if (stride < 3) return null;

        long pixels = (long) h.W * h.H;
        Span<byte> min = stackalloc byte[4] { 255, 255, 255, 255 };
        Span<byte> max = stackalloc byte[4];
        Span<double> sum = stackalloc double[4];
        var n = 0;
        var count = (int) Math.Min(pixels, sampleTarget);
        for (var i = 0; i < count; i++)
        {
            // Same coprime walk as FlatColour, and for the same reason.
            var p = h.DataOffset + (int) ((long) i * Walk % pixels) * stride;
            if (p + stride > d.Length) continue;
            // TGA is BGRA on disk; index back to RGBA so the report reads as expected.
            for (var c = 0; c < 4; c++)
            {
                var v = c switch { 0 => d[p + 2], 1 => d[p + 1], 2 => d[p], _ => stride >= 4 ? d[p + 3] : (byte) 255 };
                if (v < min[c]) min[c] = v;
                if (v > max[c]) max[c] = v;
                sum[c] += v;
            }
            n++;
        }
        if (n == 0) return null;
        return new ChannelStats(h.W, h.H,
            (min[0], max[0], sum[0] / n), (min[1], max[1], sum[1] / n),
            (min[2], max[2], sum[2] / n), (min[3], max[3], sum[3] / n));
    }

    /// <summary>Expected cooked dimensions per exported texture file, from the atlas record.</summary>
    public static Dictionary<string, (int W, int H)> ExpectedSizes(string exportDir)
    {
        var map = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var slots = Path.Combine(exportDir, "atlas-slots.tsv");
        if (!File.Exists(slots)) return map;
        foreach (var line in File.ReadLines(slots).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 5 || f[2].Length == 0) continue;
            if (!int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
                !int.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) continue;
            // "/Game/X/Y.Y" is the same asset the index calls "<Root>/Content/X/Y". Key on the
            // part after /Game/, before the object name, which is what the exporter mirrors.
            var p = f[2];
            var cut = p.LastIndexOf('.');
            if (cut > 0) p = p[..cut];
            if (p.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) p = p[6..];
            if (w > 0 && h > 0) map[p.Replace('\\', '/')] = (w, h);
        }
        return map;
    }

    public static (int Checked, List<TexReport> Bad, List<TexReport> FlatOnes) CheckTree(
        string exportDir, int jobs = 1)
    {
        var expected = ExpectedSizes(exportDir);
        var files = Directory.EnumerateFiles(exportDir, "*.tga", SearchOption.AllDirectories).ToList();
        var bad = new List<TexReport>();
        var flat = new List<TexReport>();
        var n = 0;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, f =>
        {
            byte[] d;
            try { d = File.ReadAllBytes(f); } catch { return; }
            Interlocked.Increment(ref n);

            var hdr = TgaHeader(d);
            if (hdr is not { } h)
            {
                lock (bad) bad.Add(new TexReport(f, 0, 0, false, 0, 0));
                return;
            }

            // Match the file back to the record by its path under Content/.
            var key = f.Replace('\\', '/');
            var idx = key.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            key = idx >= 0 ? key[(idx + 9)..] : Path.GetFileNameWithoutExtension(f);
            if (key.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) key = key[..^4];
            expected.TryGetValue(key, out var want);

            var colour = FlatColour(d, h.DataOffset, h.Bpp, h.W * h.H);
            var rep = new TexReport(f, h.W, h.H, colour is not null,
                want.Item1, want.Item2, colour);
            if (!rep.SizeMatches) lock (bad) bad.Add(rep);
            if (rep.Flat) lock (flat) flat.Add(rep);
        });

        return (n, bad, flat);
    }
}

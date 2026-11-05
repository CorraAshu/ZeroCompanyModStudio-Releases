namespace UEToolkit.Core;

/// <summary>
/// Compare two renders of what should be the same mesh.
///
/// Used by `--pskcompare` to check an exported `.psk` against the game's own mesh. The two are
/// rendered by different code paths on purpose: the game mesh has its atlas transform applied in
/// the VERTEX SHADER, the exported file has it baked into the stored UVs. Agreement is evidence the
/// derivation is right; disagreement localises where it is wrong.
/// </summary>
public static class PixelDiff
{
    public sealed record Result(
        int CoveredA, int CoveredB, double SilhouetteMismatch,
        double MeanDelta, int MaxDelta, string Verdict);

    // The viewport's clear colour. Pixels matching it are background, not geometry.
    private const byte ClearR = 33, ClearG = 34, ClearB = 39;

    private static bool IsBackground(byte[] p, int i) =>
        Math.Abs(p[i] - ClearR) <= 2 && Math.Abs(p[i + 1] - ClearG) <= 2 && Math.Abs(p[i + 2] - ClearB) <= 2;

    /// <summary>
    /// Two numbers, because they fail differently and conflating them hides the useful one.
    ///
    /// **Silhouette** is whether the same pixels are covered at all — that moves when the GEOMETRY
    /// or the camera differs, and if it is high the colour figure is meaningless because it is
    /// comparing a model against the background.
    ///
    /// **Colour** is the mean channel difference over pixels BOTH cover, which is the UV question:
    /// same shape, different texels means the atlas transform disagrees. A wrong grid does not move
    /// a single vertex, so silhouette stays at zero and only this moves — which is exactly the class
    /// of bug that passed every existing check.
    /// </summary>
    public static Result Compare(byte[] a, byte[] b, int width, int height)
    {
        if (a.Length != b.Length) return new Result(0, 0, 1, 255, 255, "SIZE MISMATCH - cannot compare");

        int coveredA = 0, coveredB = 0, both = 0, onlyOne = 0, maxDelta = 0;
        long sum = 0;

        for (var i = 0; i < a.Length; i += 4)
        {
            var bgA = IsBackground(a, i);
            var bgB = IsBackground(b, i);
            if (!bgA) coveredA++;
            if (!bgB) coveredB++;
            if (bgA != bgB) { onlyOne++; continue; }
            if (bgA) continue;

            both++;
            var d = Math.Max(Math.Abs(a[i] - b[i]),
                    Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            sum += d;
            if (d > maxDelta) maxDelta = d;
        }

        var covered = Math.Max(1, coveredA);
        var silhouette = (double) onlyOne / covered;
        var mean = both == 0 ? 255 : (double) sum / both;

        // Thresholds are deliberately loose. The two paths are not bit-identical by construction:
        // the shader remaps per fragment while the file stores remapped UVs per vertex, so
        // interpolation and texture filtering differ slightly at edges. What matters is whether the
        // SAME TEXELS are being sampled, and a wrong grid is not a subtle difference -- reading a
        // 6x1 atlas as 12x2 puts entirely different artwork on the model.
        // Wording matters here, and my first version got it wrong. It said a disagreement meant
        // "the export and the shader are not sampling the same texels", which reads as the EXPORT
        // being at fault. It is not a reference comparison: the export knows things the shader
        // does not -- pooled UV extents across every mesh sharing a texture, and a refusal to grid
        // a tiling texture at all. Swept over 700 character meshes of one game, both disagreements that
        // were run down (a part's pooled 8x2, a `/Shaders/Tiling/` worm pattern) had
        // the EXPORT right and the viewport under-informed. A disagreement is a lead, not a verdict.
        var verdict = silhouette > 0.02
            ? "GEOMETRY DIFFERS - shapes do not line up, colour figure is not meaningful"
            : mean <= 6 ? "MATCH - both paths sample the same texels"
            : mean <= 20 ? "CLOSE - small differences, worth an eye"
            : "DIFFER - the two UV derivations disagree; check WHICH is right, the export often has more information";

        return new Result(coveredA, coveredB, silhouette, mean, maxDelta, verdict);
    }

    public static void Write(byte[] rgbaBottomUp, int width, int height, string path)
    {
        using var bmp = ToBitmap(rgbaBottomUp, width, height);
        Save(bmp, path);
    }

    /// <summary>
    /// Where they differ, and by how much — red channel scaled so a small difference is visible.
    /// A picture localises the disagreement; a mean over the whole frame cannot say "the shoulder".
    /// </summary>
    public static void WriteDiff(byte[] a, byte[] b, int width, int height, string path)
    {
        var outp = new byte[a.Length];
        for (var i = 0; i < a.Length; i += 4)
        {
            var d = Math.Max(Math.Abs(a[i] - b[i]),
                    Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            var v = (byte) Math.Min(255, d * 4);
            outp[i] = v; outp[i + 1] = (byte) (v / 3); outp[i + 2] = (byte) (v / 3); outp[i + 3] = 255;
        }
        using var bmp = ToBitmap(outp, width, height);
        Save(bmp, path);
    }

    private static SkiaSharp.SKBitmap ToBitmap(byte[] rgbaBottomUp, int width, int height)
    {
        var bmp = new SkiaSharp.SKBitmap(width, height, SkiaSharp.SKColorType.Rgba8888,
            SkiaSharp.SKAlphaType.Premul);
        for (var y = 0; y < height; y++)
            System.Runtime.InteropServices.Marshal.Copy(
                rgbaBottomUp, (height - 1 - y) * width * 4, bmp.GetPixels() + y * width * 4, width * 4);
        return bmp;
    }

    private static void Save(SkiaSharp.SKBitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        using var fs = File.OpenWrite(path);
        data.SaveTo(fs);
    }
}

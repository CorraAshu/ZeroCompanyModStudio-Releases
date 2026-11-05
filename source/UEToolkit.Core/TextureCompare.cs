namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// Say when an authored MASK is a very different thing from the one it replaces.
///
/// WHY THIS EXISTS. A Para helmet shipped glossy for a whole release, and its author assumed their
/// own art was at fault, then rebuilt twice on my say-so while I looked in the wrong place. The
/// mask was measurable the entire time:
///
///     the game's       green mean 115, std 55,  1% pure black
///     the author's     green mean  29, std 48, 39% pure black
///
/// CONFIRMED IN GAME by the author, 2026-09-10: replacing that mask with a white one removed the
/// gloss. So GREEN IS ROUGHNESS in this material family -- brighter is rougher is less shiny --
/// and 39% of their mask sitting at pure black was 39% of the helmet at roughness zero, which is a
/// mirror. That is the finding; everything below is only how to notice it earlier.
///
/// A mask encodes how a surface answers light. Unlike a diffuse -- where being completely
/// different from the template is the whole point of authoring one -- a mask that reads four times
/// darker than the one it stands in for is nearly always a mistake, and it is invisible until the
/// part is in game under a light.
///
/// So this is deliberately NOT run on colour. <see cref="TextureImport.KindFor"/> already sorts a
/// file by its name the way this game names its own (_D, _MSK, _N), and only the mask kind is
/// checked. Warning about a diffuse that differs from the template would fire on every mod ever
/// built and teach people to stop reading these.
///
/// LIKE FOR LIKE, which is the only fiddly part. The template is a 4096 virtual texture and the
/// author's file is whatever they painted; comparing a full-resolution image against a decoded mip
/// would compare two different things, and a "pure black" count especially -- box filtering a mip
/// blurs isolated black away, so the same image measures blacker at full size than small. Both
/// sides are therefore brought to ONE small size: a mip of the template near 256 across, and the
/// author's image scaled to exactly that. The absolute numbers then differ from what a full-size
/// measurement would say, and the COMPARISON is honest, which is what the note is for.
/// </summary>
public static class TextureCompare
{
    /// <summary>Longest side to compare at. Small enough to be free, big enough to mean something.</summary>
    private const int CompareAt = 256;

    /// <summary>A channel is only worth comparing when the template actually uses it.</summary>
    private const double Used = 8.0;

    /// <summary>How far apart two means have to be before it is worth saying, in 0-255 levels.</summary>
    private const double MeanGap = 32.0;

    /// <summary>...and by what factor, so a bright pair needs more than the same absolute gap.</summary>
    private const double MeanRatio = 1.6;

    /// <summary>How much more of the author's image may be pure black before it is worth saying.</summary>
    private const double BlackGap = 0.20;

    /// <summary>
    /// One line about how <paramref name="imagePath"/> compares with the texture it replaces, or
    /// null when there is nothing worth saying -- which is the normal case.
    ///
    /// Never throws and never blocks a build: this is a remark about art, and a mod whose mask is
    /// deliberately unlike the game's is a legitimate thing to make.
    /// </summary>
    public static string? Note(IFileProvider provider, string templatePackage, string imagePath)
    {
        try
        {
            if (TextureImport.KindFor(imagePath) != MipChain.Kind.Linear) return null;

            if (AssetLoader.Read(() => provider.LoadPackageObject<UTexture2D>(templatePackage))
                is not { } tex) return null;

            var (was, w, h) = Decode(tex);
            if (was is null || w <= 0 || h <= 0) return null;

            using var image = Image.Load<Rgba32>(imagePath);
            TextureImport.ScaleTo(image, w, h);
            var now = new byte[w * h * 4];
            image.CopyPixelDataTo(now);

            var bits = new List<string>();
            var greenMoved = false;
            for (var c = 0; c < 4; c++)
            {
                var t = Mean(was, c);
                var a = Mean(now, c);
                if (t <= Used) continue;                       // the template does not use it
                if (Math.Abs(t - a) < MeanGap) continue;
                if (Math.Max(t, a) / Math.Max(1.0, Math.Min(t, a)) < MeanRatio) continue;
                if (c == 1) greenMoved = true;
                bits.Add($"{"red green blue alpha".Split(' ')[c]} averages {a:F0} " +
                         $"where the one it replaces averages {t:F0}");
            }

            var blackWas = Black(was);
            var blackNow = Black(now);
            if (blackNow - blackWas >= BlackGap)
                bits.Add($"{blackNow:P0} of it is pure black against {blackWas:P0}");

            if (bits.Count == 0) return null;

            // The roughness sentence ONLY where green is what moved. Said on a tint mask -- which
            // is also a "mask" by name and legitimately differs from the game's per part -- it
            // would be a confident explanation of the wrong thing.
            return $"{Path.GetFileName(imagePath)} is a MASK and reads very differently from " +
                   $"{Leaf(templatePackage)}: {string.Join("; ", bits)}." +
                   (greenMoved
                       ? " GREEN IS ROUGHNESS on these materials -- darker is shinier and pure " +
                         "black is a mirror -- so this is what a part that comes out unexpectedly " +
                         "glossy looks like, before it is in game."
                       : "") +
                   " Deliberate if you meant it.";
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The template at a small size, as RGBA.
    ///
    /// A virtual texture has no mip chain to index -- its levels ARE its mips for this purpose, and
    /// DecodeMip takes the level. An ordinary texture indexes its own. Either way the level chosen
    /// is the first one at or under <see cref="CompareAt"/> across, falling back to the smallest.
    /// </summary>
    private static (byte[]? Pixels, int Width, int Height) Decode(UTexture2D tex)
    {
        var pd = tex.PlatformData;
        var levels = pd.VTData is { } vt ? (int) vt.NumMips : pd.Mips.Length;
        if (levels <= 0) return (null, 0, 0);

        var width = pd.VTData is { } v ? (int) v.Width : pd.SizeX;
        var pick = 0;
        while (pick < levels - 1 && (width >> pick) > CompareAt) pick++;

        var img = CUE4Parse_Conversion.Textures.TextureDecoder.DecodeMip(
            tex, pick, ETexturePlatform.DesktopMobile);
        if (img is null) return (null, 0, 0);

        var px = (byte[]) img.Data.Clone();
        // The decoder hands back BGRA for some formats and RGBA for others. Normalising here is
        // what stopped an earlier encoder test reporting a catastrophic error that was one
        // swapped pair -- see the same note in --vtauthor.
        if (img.PixelFormat == EPixelFormat.PF_B8G8R8A8)
            for (var i = 0; i < img.Width * img.Height; i++)
                (px[i * 4], px[i * 4 + 2]) = (px[i * 4 + 2], px[i * 4]);

        return (px, img.Width, img.Height);
    }

    private static double Mean(byte[] px, int channel)
    {
        double sum = 0;
        for (var i = channel; i < px.Length; i += 4) sum += px[i];
        return px.Length == 0 ? 0 : sum / (px.Length / 4);
    }

    /// <summary>Fraction of pixels with nothing in red, green or blue. Alpha is not coverage here.</summary>
    private static double Black(byte[] px)
    {
        var n = px.Length / 4;
        if (n == 0) return 0;
        var black = 0;
        for (var i = 0; i < n; i++)
            if (px[i * 4] <= 2 && px[i * 4 + 1] <= 2 && px[i * 4 + 2] <= 2) black++;
        return (double) black / n;
    }

    private static string Leaf(string path) =>
        path.Length == 0 ? path : path[(path.LastIndexOf('/') + 1)..];
}

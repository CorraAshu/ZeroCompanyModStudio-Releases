using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace UEToolkit.Core;

/// <summary>
/// Turn a painted image into a texture the game loads, by writing it into a template of the same
/// shape.
///
/// The character maps this replaces are VIRTUAL textures -- tiled pages with borders and a page
/// table, not a mip chain -- and authoring one of those is a different and much larger job. A mod
/// does not have to use one, though, and the game ships ordinary 2048x2048 textures in exactly the
/// formats a skin needs:
///
///     colour   PF_DXT5   T_Default_BaseColor
///     mask     PF_DXT1   T_Sky_Blue
///     normal   PF_BC5    T_WaterNormal01_N
///
/// Picking a template whose size AND format already match is what makes this a payload swap rather
/// than a serialiser: nothing in the package's structure has to change, because every byte count
/// stays the same.
///
/// A big texture SPLITS its mips, which is the one thing here that is not obvious:
///
///     2048..128   streamed, in a separate BulkData chunk    5,586,944 bytes for DXT5
///     64..1       inline, inside the package itself         5,488 bytes
///
/// So there are two payloads to replace, in two different chunks, and a swap that finds only the
/// inline one produces a texture that is correct when the camera is close and reverts to the
/// template's artwork as it pulls back.
/// </summary>
public static class TextureImport
{
    /// <summary>What a template is, measured rather than assumed.</summary>
    public sealed record Template(string PackagePath, EPixelFormat Format, int Width, int Height,
                                  int MipCount, int FirstInlineMip,
                                  List<byte[]> InlineMips, int StreamedBytes)
    {
        public int InlineBytes => InlineMips.Sum(m => m.Length);

        public string Describe =>
            $"{Path.GetFileName(PackagePath)} {Width}x{Height} {Format}, {MipCount} mips " +
            $"({StreamedBytes:N0} streamed + {InlineBytes:N0} inline across {InlineMips.Count})";
    }

    /// <summary>The two payloads, ready to go into the two chunks they belong in.</summary>
    /// <summary>
    /// The encoded mips, split the way the template splits them.
    ///
    /// The inline ones are kept PER MIP rather than concatenated, because they are not contiguous
    /// in the package -- each is followed by its own SizeX/SizeY/SizeZ before the next begins, so
    /// a search for the joined run finds nothing at all.
    /// </summary>
    public sealed record Authored(List<byte[]> InlineMips, byte[] Streamed, List<string> Notes)
    {
        public int InlineBytes => InlineMips.Sum(m => m.Length);
    }

    // ------------------------------------------------------------------ reading a template

    /// <summary>
    /// Measure a template: its format, size, and exactly which mips are inline against streamed.
    ///
    /// Read from the asset rather than worked out from the dimensions, because "which mips are
    /// inline" is a cooker decision and not a rule -- a 2048 texture here keeps seven inline and a
    /// 256 icon keeps its only one.
    /// </summary>
    public static Template? Describe(IFileProvider provider, string templatePath, out string? why)
    {
        why = null;
        try
        {
            var tex = provider.LoadPackageObject<UTexture2D>(templatePath);
            var pd = tex.PlatformData;

            if (pd.VTData is not null)
            { why = $"{templatePath} is a VIRTUAL texture, which cannot be authored this way"; return null; }
            if (pd.Mips.Length == 0)
            { why = $"{templatePath} has no mips"; return null; }

            var inline = new List<byte[]>();
            var streamed = 0;
            var firstInline = -1;

            for (var i = 0; i < pd.Mips.Length; i++)
            {
                var data = pd.Mips[i].BulkData?.Data;
                if (data is null || data.Length == 0)
                { why = $"{templatePath} mip {i} has no data to measure"; return null; }

                // The flags say where it lives, and the flags are what the loader obeys.
                var flags = pd.Mips[i].BulkData!.Header.BulkDataFlags;
                var isInline = flags.HasFlag(CUE4Parse.UE4.Assets.Objects.EBulkDataFlags.BULKDATA_ForceInlinePayload);

                if (isInline)
                {
                    if (firstInline < 0) firstInline = i;
                    inline.Add(data);
                }
                else
                {
                    if (firstInline >= 0)
                    { why = $"{templatePath} mixes streamed mips after inline ones"; return null; }
                    streamed += data.Length;
                }
            }

            if (firstInline < 0) { why = $"{templatePath} has no inline mips"; return null; }

            // The package's OWN name, not the path that was typed. A file can be found by its
            // mounted path (Engine/Plugins/.../T_WaterNormal01_N) while the container files it
            // under a package name (/DatasmithContent/.../T_WaterNormal01_N), and AssetCopy
            // matches on the id derived from the second. Asking the loaded package removes a
            // whole class of "found it, then could not find it".
            var resolved = tex.Owner?.Name ?? templatePath;

            return new Template(resolved, tex.Format, pd.SizeX, pd.SizeY,
                                pd.Mips.Length, firstInline, inline, streamed);
        }
        catch (Exception e) { why = $"{templatePath}: {e.Message}"; return null; }
    }

    // ------------------------------------------------------------------ encoding an image

    /// <summary>
    /// Scale an image to an exact size WITHOUT premultiplying alpha, which is the only reason this
    /// exists instead of a bare <c>Resize(w, h)</c>.
    ///
    /// THE BUG. ImageSharp's resize premultiplies alpha by default and un-premultiplies after --
    /// correct when alpha means COVERAGE, because it stops transparent black bleeding into the
    /// edges of a cutout. It is catastrophic when alpha means DATA, and in a game texture every
    /// channel is data: **this game's masks carry their content in GREEN with alpha flat 0**, and
    /// multiplying by an alpha of zero erases the whole image. Un-premultiplying cannot bring it
    /// back, because dividing by zero is guarded to leave zero.
    ///
    /// So an authored mask that had to be resized came out FLAT, the roughness it feeds came out
    /// uniform, and the part rendered glossy in game. Reported twice independently -- once on a
    /// Clone Commando torso, once on a Para helmet whose author assumed their own art was at
    /// fault. Both were 2048 files in a 4096 slot.
    ///
    /// MEASURED, three variants of one mask through the real build, as the level-0 detail of the
    /// virtual texture that came out:
    ///
    ///     2048, alpha 0,   resized       0.07     <- annihilated
    ///     2048, alpha 255, resized       0.66
    ///     4096, alpha 0,   not resized   0.66
    ///
    /// The two that survive agree to two decimal places, which is what says the resize is the
    /// whole difference rather than one of the two.
    ///
    /// `ResizeMode.Stretch` because that is what `Resize(w, h)` does -- the default is `Max`,
    /// which preserves aspect ratio and would silently letterbox a non-square source into a size
    /// the template does not expect.
    /// </summary>
    public static void ScaleTo(Image<Rgba32> image, int width, int height) =>
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            PremultiplyAlpha = false,
        }));

    /// <summary>
    /// How a channel's numbers should be filtered when halved. Chosen from the name because that
    /// is the only place the information exists -- a texture does not record whether it is colour
    /// or a mask, and the game's own naming (_D, _MSK, _N) says so consistently.
    /// </summary>
    public static MipChain.Kind KindFor(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        if (n.EndsWith("_N") || n.Contains("_NORMAL")) return MipChain.Kind.Normal;
        if (n.EndsWith("_D") || n.EndsWith("_BC") || n.Contains("_BASECOLOR") ||
            n.Contains("_ALBEDO") || n.Contains("_DIFFUSE")) return MipChain.Kind.Srgb;
        // Masks, roughness, metalness and anything unrecognised: the byte IS the value, and
        // running data through the sRGB curve is the more damaging of the two mistakes.
        return MipChain.Kind.Linear;
    }

    /// <summary>
    /// Shared with <see cref="VirtualTexture"/>: an ordinary mip and a virtual page are the same
    /// block-compression problem at different sizes, and having one of them pick a different
    /// encoder from the other is a difference nothing would ever report.
    /// </summary>
    public static byte[] Encode(EPixelFormat format, byte[] rgba, int w, int h) => format switch
    {
        // DXT1 gets OPAQUE pixels. Its single alpha bit means any pixel under the cutoff turns
        // transparent black and takes its block's colour with it -- so a map whose alpha channel
        // is unused and left at ZERO encodes to a completely blank texture. That is not
        // hypothetical: this game's masks carry data only in green with alpha flat 0, and the
        // first measurement of one came back at mean error 26 against a source it had erased.
        //
        // A texture that needs cut-out alpha needs DXT5, which is the same decision the cooker
        // makes. Nothing here has a use for one bit of it.
        EPixelFormat.PF_DXT1 => Bc1Encoder.Encode(Opaque(rgba), w, h),
        EPixelFormat.PF_DXT5 => Bc3Encoder.Encode(rgba, w, h),
        EPixelFormat.PF_BC5 => Bc5Encoder.Encode(rgba, w, h),
        _ => throw new NotSupportedException($"no encoder for {format}"),
    };

    private static byte[] Opaque(byte[] rgba)
    {
        var copy = (byte[]) rgba.Clone();
        for (var i = 3; i < copy.Length; i += 4) copy[i] = 255;
        return copy;
    }

    /// <summary>
    /// Encode an image into the exact byte layout the template uses.
    ///
    /// Every length is checked against the template rather than trusted, because the whole reason
    /// this works without touching the package's structure is that the counts match -- and a
    /// mismatch found here is a clear message, while the same mismatch found later is a texture
    /// that decodes as noise.
    /// </summary>
    public static Authored? Author(Template t, string imagePath, MipChain.Kind? kind,
                                   out string? why, Action<string>? log = null)
    {
        why = null;
        var notes = new List<string>();

        try
        {
            using var image = Image.Load<Rgba32>(imagePath);
            if (image.Width != t.Width || image.Height != t.Height)
            {
                notes.Add($"resized {image.Width}x{image.Height} to {t.Width}x{t.Height} " +
                          "to match the template");
                ScaleTo(image, t.Width, t.Height);
            }

            var rgba = new byte[t.Width * t.Height * 4];
            image.CopyPixelDataTo(rgba);

            var use = kind ?? KindFor(imagePath);
            log?.Invoke($"      {Path.GetFileName(imagePath)}: {t.Width}x{t.Height} -> " +
                        $"{t.Format}, {use} filtering");

            var levels = MipChain.Build(rgba, t.Width, t.Height, use);
            if (levels.Count < t.MipCount)
            { why = $"the image makes {levels.Count} mips and the template wants {t.MipCount}"; return null; }

            var streamed = new List<byte>();
            var inline = new List<byte[]>();

            for (var i = 0; i < t.MipCount; i++)
            {
                var lvl = levels[i];
                var (px, pw, ph) = MipChain.PadToBlock(lvl.Rgba, lvl.Width, lvl.Height);
                var block = Encode(t.Format, px, pw, ph);

                if (i >= t.FirstInlineMip)
                {
                    // Each inline mip must match its counterpart EXACTLY, since each is written
                    // back over the template's own bytes in place.
                    var want = t.InlineMips[i - t.FirstInlineMip].Length;
                    if (block.Length != want)
                    { why = $"mip {i} encoded to {block.Length} bytes, template has {want}"; return null; }
                    inline.Add(block);
                }
                else streamed.AddRange(block);
            }

            if (streamed.Count != t.StreamedBytes)
            { why = $"streamed mips came to {streamed.Count:N0} bytes, template has {t.StreamedBytes:N0}"; return null; }

            return new Authored(inline, streamed.ToArray(), notes);
        }
        catch (Exception e) { why = $"{Path.GetFileName(imagePath)}: {e.Message}"; return null; }
    }

    // ------------------------------------------------------------------ putting it in the copy

    /// <summary>
    /// Replace both payloads in a copied template, and rename it into the mod.
    ///
    /// The streamed chunk is replaced WHOLESALE -- it holds the big mips and nothing else, which
    /// was measured rather than assumed: for the DXT5 template the chunk is 5,586,944 bytes and the
    /// mips it should hold come to 5,586,944. The inline run is found by matching the template's
    /// own bytes, the same way an authored icon is, and requiring exactly one hit is what keeps it
    /// from writing into something that merely looks similar.
    /// </summary>
    public static List<IoStoreWriter.Chunk>? Swap(AssetCopy.Source source, Template t,
                                                  Authored made, string newPackageName,
                                                  string containerPath, out string? why,
                                                  Action<string>? log = null)
    {
        why = null;

        // SWAPPED FIRST, RENAMED SECOND, and the order is not a preference.
        //
        // Renaming rewrites the package through ZenPackage, and the pixels are searched for by
        // matching the template's own bytes. Doing it the other way round found the inline run
        // ZERO times -- the same ordering constraint the icon author already documented, arrived
        // at again the hard way.
        var pieces = new List<AssetCopy.Piece>();
        var swappedInline = false;
        var swappedStreamed = false;

        foreach (var piece in source.Pieces)
        {
            if (piece.IsPackage)
            {
                var data = (byte[]) piece.Data.Clone();

                // One mip at a time, each searched for FORWARD OF THE LAST. Searching the whole
                // package for every mip independently would be ambiguous at the small end -- the
                // 4x4, 2x2 and 1x1 levels are all one block and are often identical bytes -- but
                // they are stored in order, so the previous match bounds the next.
                var from = 0;
                for (var m = 0; m < t.InlineMips.Count; m++)
                {
                    var at = IndexOf(data, t.InlineMips[m], from);
                    if (at < 0)
                    {
                        why = $"mip {t.FirstInlineMip + m} ({t.InlineMips[m].Length} bytes) is not " +
                              $"in the package after offset {from}";
                        return null;
                    }
                    made.InlineMips[m].CopyTo(data, at);
                    from = at + t.InlineMips[m].Length;
                }

                pieces.Add(piece with { Data = data });
                swappedInline = true;
            }
            else if (piece.Data.Length == t.StreamedBytes)
            {
                pieces.Add(piece with { Data = made.Streamed });
                swappedStreamed = true;
            }
            else pieces.Add(piece);
        }

        if (!swappedInline) { why = "no package chunk to put the inline mips in"; return null; }
        if (!swappedStreamed && t.StreamedBytes > 0)
        { why = $"no payload chunk of {t.StreamedBytes:N0} bytes to replace"; return null; }

        var chunks = AssetCopy.Rename(source with { Pieces = pieces }, newPackageName,
                                      containerPath, log);

        log?.Invoke($"      pixels replaced: {made.Streamed.Length:N0} streamed + " +
                    $"{made.InlineBytes:N0} inline across {made.InlineMips.Count} mip(s)");
        return chunks;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        var span = haystack.AsSpan();
        for (var i = from; i + needle.Length <= haystack.Length; i++)
            if (span[i] == needle[0] && span.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}

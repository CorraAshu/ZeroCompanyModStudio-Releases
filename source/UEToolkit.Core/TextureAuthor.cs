using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace UEToolkit.Core;

/// <summary>
/// Author a cooked texture from a PNG or TGA, with no cooker.
///
/// This is the last thing that needed Unreal. It works because of what the game's UI thumbnails
/// actually are: PF_DXT1, 256x256, ONE mip, and the payload stored INLINE in the package rather
/// than in a separate bulk chunk. So authoring one is a same-length substitution into a template
/// -- the compressed pixels are swapped for ours and not one offset in the package moves.
///
/// That also sets the limits honestly. The template decides the format and the dimensions; the
/// image is resized to fit. A texture that needed a different format, more mips, or an out-of-line
/// payload would need real cooking, and is refused rather than half-produced.
/// </summary>
public static class TextureAuthor
{
    /// <summary>What a template must be for its pixels to be replaceable.</summary>
    public sealed record Shape(int Width, int Height, int PayloadBytes, string Format);

    /// <summary>Images that can be read. ImageSharp decodes both; nothing else is claimed.</summary>
    public static bool IsSupportedImage(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an image would be DISTORTED by the fit, and by how much.
    ///
    /// The image is stretched to the template's dimensions, which is the identity for the square
    /// icons this is for and a visible squash for anything else. Silently squashing someone's
    /// artwork and letting them find out in the menu is the failure worth avoiding; letterboxing
    /// instead would introduce transparent margins that a DXT1 icon renders as black, which is
    /// not obviously better, so the behaviour stays and gets reported.
    /// </summary>
    public static string? AspectNote(string imagePath, Shape shape)
    {
        try
        {
            var info = Image.Identify(imagePath);
            if (info is null) return null;
            var from = info.Width / (double) info.Height;
            var to = shape.Width / (double) shape.Height;
            if (Math.Abs(from - to) / to < 0.01) return null;
            return $"{info.Width}x{info.Height} is stretched to {shape.Width}x{shape.Height}";
        }
        catch { return null; }
    }

    /// <summary>
    /// Read a template's shape, or say why it cannot be used. Checked before anything is written,
    /// because "the icon silently stayed the template's" is exactly the failure to avoid.
    /// </summary>
    public static Shape? ShapeOf(IFileProvider provider, string templatePackagePath, out string? why)
    {
        why = null;
        var obj = AssetLoader.LoadPrimary(provider, templatePackagePath);
        if (obj is not UTexture2D tex) { why = $"{templatePackagePath} is not a texture"; return null; }

        var pd = tex.PlatformData;
        // Three block formats, which is what this game's textures actually use: colour maps and
        // masks are DXT5, normals are BC5. Refusing everything but DXT1 meant the only textures
        // that could be replaced were icons.
        if (BytesPerPixel(pd.PixelFormat) is not { } ovBpp)
        { why = $"template is {pd.PixelFormat}; this can encode PF_DXT1, PF_DXT5 and PF_BC5"; return null; }
        if (pd.Mips.Length != 1)
        { why = $"template has {pd.Mips.Length} mips; only a single-mip texture is a plain swap"; return null; }

        var mip = pd.Mips[0];
        var data = mip.BulkData?.Data;
        if (data is null || data.Length == 0)
        { why = "template's mip has no inline payload -- its pixels live in a bulk chunk"; return null; }

        // DXT1 is half a byte per pixel; DXT5 and BC5 are one, because they carry a second block
        // of the same size for alpha or the second channel.
        var expected = (int) (mip.SizeX * mip.SizeY * ovBpp);
        if (data.Length != expected)
        { why = $"template mip is {data.Length:N0} bytes, not the {expected:N0} {pd.PixelFormat} needs"; return null; }

        return new Shape(mip.SizeX, mip.SizeY, data.Length, pd.PixelFormat);
    }

    /// <summary>
    /// Just the pixel dimensions of a texture, whatever its format.
    ///
    /// Separate from <see cref="ShapeOf"/> because that answers "can I write my pixels over this",
    /// and refuses BC7 and multi-mip textures for good reasons that have nothing to do with
    /// measuring one. Pointing a brush at a texture the GAME already ships needs the size and
    /// nothing else -- and the one weapon part in the game that draws a texture draws a BC7 one,
    /// so asking the authoring question there answers no to a question nobody asked.
    /// </summary>
    public static (int Width, int Height)? SizeOf(IFileProvider provider, string packagePath)
    {
        if (AssetLoader.LoadPrimary(provider, packagePath) is not UTexture2D tex) return null;
        var mip = tex.PlatformData?.Mips?.FirstOrDefault();
        if (mip is null || mip.SizeX <= 0 || mip.SizeY <= 0) return null;
        return (mip.SizeX, mip.SizeY);
    }

    /// <summary>Bytes per pixel for the block formats this can write, or null for one it cannot.</summary>
    private static float? BytesPerPixel(string format) => format switch
    {
        "PF_DXT1" => 0.5f,
        "PF_DXT5" => 1f,
        "PF_BC5" => 1f,
        _ => null,
    };

    /// <summary>
    /// Encode an image to the template's exact payload. Separate from the substitution so the
    /// encoder can be tested against a shape without a package in hand.
    /// </summary>
    public static byte[] EncodeFor(string imagePath, Shape shape)
    {
        using var image = Image.Load<Rgba32>(imagePath);
        if (image.Width != shape.Width || image.Height != shape.Height)
            TextureImport.ScaleTo(image, shape.Width, shape.Height);

        var rgba = new byte[shape.Width * shape.Height * 4];
        image.CopyPixelDataTo(rgba);

        var encoded = shape.Format switch
        {
            "PF_DXT5" => Bc3Encoder.Encode(rgba, shape.Width, shape.Height),
            "PF_BC5" => Bc5Encoder.Encode(rgba, shape.Width, shape.Height),
            _ => Bc1Encoder.Encode(rgba, shape.Width, shape.Height),
        };
        if (encoded.Length != shape.PayloadBytes)
            throw new InvalidOperationException(
                $"encoded {encoded.Length:N0} bytes for a {shape.PayloadBytes:N0} byte payload");
        return encoded;
    }

    /// <summary>
    /// Produce a new texture package: the template's bytes, with its pixels replaced by the
    /// image's and its names moved to <paramref name="newPackageName"/>.
    /// </summary>
    public static byte[]? Author(IFileProvider provider, string templatePackagePath, string imagePath,
                                 string newPackageName, out string? why)
    {
        var shape = ShapeOf(provider, templatePackagePath, out why);
        if (shape is null) return null;

        var containerPath = ModBuild.ContainerPathFor(templatePackagePath);
        if (!provider.TrySaveAsset(containerPath, out var raw))
        { why = $"could not read {containerPath}"; return null; }

        var tex = (UTexture2D) AssetLoader.LoadPrimary(provider, templatePackagePath)!;
        var original = tex.PlatformData.Mips[0].BulkData!.Data!;

        // Find the payload by its exact bytes rather than by a computed offset. 32 KB of DXT1 is
        // not going to occur twice by accident, and requiring exactly one match makes a wrong
        // guess an error here instead of a corrupt texture later.
        var at = IndexOf(raw, original);
        if (at < 0) { why = "the template's pixels are not present in its own package bytes"; return null; }
        if (IndexOf(raw, original, at + 1) >= 0)
        { why = "the template's pixels appear more than once; refusing to guess which is the mip"; return null; }

        byte[] encoded;
        try { encoded = EncodeFor(imagePath, shape); }
        catch (Exception e) { why = $"could not encode {Path.GetFileName(imagePath)}: {e.Message}"; return null; }

        var output = (byte[]) raw.Clone();
        encoded.CopyTo(output, at);

        // Renaming happens AFTER the swap: the payload is found in the template's own bytes, and
        // renaming first would move everything after the name batch.
        var pkg = ZenPackage.Read(output);
        var subs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [templatePackagePath] = newPackageName,
            [Path.GetFileNameWithoutExtension(templatePackagePath)] =
                newPackageName[(newPackageName.LastIndexOf('/') + 1)..],
        };
        pkg.RenameNames(subs);

        why = null;
        return pkg.Write();
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        var span = haystack.AsSpan();
        for (var i = from; i + needle.Length <= haystack.Length; i++)
            if (span[i] == needle[0] && span.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}

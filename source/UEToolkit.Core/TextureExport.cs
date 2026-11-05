using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

namespace UEToolkit.Core;

/// <summary>
/// Writes a texture on its own, which CUE4Parse's generic Exporter cannot do — it handles
/// meshes and animations and refuses a UTexture2D outright.
///
/// That matters because textures are otherwise written only as a side effect of exporting a
/// mesh's materials, once per referencing mesh. A shared atlas then lands dozens of times:
/// 250 meshes produced 7.2 GB, almost all of it the same maps repeated. Exporting meshes
/// without materials and every texture exactly once is ~24x smaller, but only works if there
/// is a standalone path — this one.
/// </summary>
public static class TextureExport
{
    /// <summary>
    /// Decode and write one texture, mirroring the game's folder structure under
    /// <paramref name="outputDir"/>. Returns the file written, or null with a reason.
    /// </summary>
    /// <param name="dropAlpha">
    /// Write a PNG with NO alpha channel. A DCC that finds an alpha channel in a base-colour image treats it
    /// as the material's opacity (Blender's importers both do), so a base colour whose alpha holds something
    /// else -- or nothing -- turns see-through there. Only the PNG route honours it; HDR and the other formats
    /// are written as they were.
    /// </param>
    public static (string? Path, string? Error) Write(
        UTexture texture, string outputDir, string assetPath, ETextureFormat format, bool dropAlpha = false)
    {
        var r = WriteMeasured(texture, outputDir, assetPath, format, dropAlpha);
        return (r.Path, r.Error);
    }

    /// <summary>
    /// <see cref="Write"/> that also says whether the decoded image has any texel whose alpha is not fully
    /// opaque (always false for a texture the PNG route does not write, such as an HDR one, and when the
    /// image is written without alpha). The check converts the decoded image once more to read its texels.
    /// </summary>
    public static (string? Path, string? Error, bool AlphaNotOpaque) WriteMeasured(
        UTexture texture, string outputDir, string assetPath, ETextureFormat format, bool dropAlpha = false)
    {
        var alphaNotOpaque = false;
        string? partial = null;
        try
        {
            // The PAYLOAD fetch under the gate; the decode outside it.
            //
            // `Decode()` was recorded as safe to run parallel because "it works on an already-
            // loaded object". The object is loaded; its pixels are not. `Decode()` calls
            // `GetFirstMipIndex()`, which calls `EnsureValidBulkData`, which touches
            //
            //   * `MipDataProvider` — a `TryLoad<UTextureAllMipDataProviderFactory>` through the
            //     asset's user data, i.e. a package load through the import table, caching into an
            //     unsynchronised field; and
            //   * `BulkData.Data` — a `Lazy` that reads the `.ubulk` straight out of the archive.
            //
            // Both are provider reads, and this method is the whole texture pass of `--exportall`
            // running on `--jobs` threads with no lock at all. It is the third leg of the export
            // non-determinism, and the evidence was already in this file: `Diagnose` was written
            // because "a texture failed on one run and succeeded on the next".
            //
            // Only the fetch is serialised. The BC7/ASTC decode of an 8K atlas is pure CPU on
            // bytes already in hand, and that is where the entire cost of a texture pass lives —
            // so this keeps the parallelism that matters and gives up the part that was never
            // safe. Once the `Lazy` has been forced here, `DecodeMip` re-checks it, finds it
            // present and never reaches the archive again.
            int mipIndex;
            bool virtualTexture;
            lock (AssetLoader.Gate)
            {
                // A virtual texture decodes from chunk payloads this warm-up does not cover, so it
                // stays inside the gate whole rather than being half-protected. There are a
                // handful per game; the mip path is the one that has to stay parallel.
                virtualTexture = texture.PlatformData?.VTData is not null;
                mipIndex = virtualTexture ? 0 : texture.GetFirstMipIndex();
            }

            var decoded = virtualTexture
                ? AssetLoader.Read(() => texture.Decode(ETexturePlatform.DesktopMobile))
                : texture.DecodeMip(mipIndex, ETexturePlatform.DesktopMobile);
            if (decoded is null) return (null, $"decoded to nothing - {Diagnose(texture)}", false);

            string ext;
            byte[] bytes;
            if (dropAlpha && format == ETextureFormat.Png && !PixelFormatUtils.IsHDR(decoded.PixelFormat))
                bytes = EncodeOpaquePng(decoded, out ext);
            else
            {
                if (format == ETextureFormat.Png && !PixelFormatUtils.IsHDR(decoded.PixelFormat))
                    alphaNotOpaque = HasNonOpaqueAlpha(decoded);
                bytes = decoded.Encode(format, saveHdrAsHdr: true, out ext);
            }
            if (bytes.Length == 0) return (null, $"encoded to 0 bytes as {ext}", false);

            // Keep the game's tree: <out>/<Project>/Content/.../T_Name.tga
            //
            // The leading separator is trimmed because a PACKAGE name is rooted
            // ("/Game/Game/..."), and Path.Combine throws away the output directory when its
            // second argument is rooted -- which quietly writes to the root of the current drive.
            // Callers that pass an index path are unaffected; this only ever removes a slash
            // that would have been a bug.
            var relative = Path.ChangeExtension(assetPath, null)
                .TrimStart('/', '\\');
            var full = Path.Combine(outputDir, relative + "." + ext.TrimStart('.'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            partial = full;
            File.WriteAllBytes(full, bytes);
            partial = null;
            return (full, null, alphaNotOpaque);
        }
        catch (Exception e)
        {
            // A write that failed part way (a full disk) must not leave a truncated image behind.
            if (partial is not null) { try { File.Delete(partial); } catch { /* the Log reports what remains */ } }
            // A handful of BC7 control maps can refuse to decode; one bad texture should
            // not take down a pass covering 5,218 of them.
            return (null, $"{e.Message} - {Diagnose(texture)}", false);
        }
    }

    /// <summary>True when any texel of the decoded image has an alpha below 255.</summary>
    private static bool HasNonOpaqueAlpha(CTexture decoded)
    {
        using var bitmap = decoded.ToSkBitmap();
        if (bitmap.BytesPerPixel != 4 || bitmap.AlphaType == SKAlphaType.Opaque) return false;
        var span = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = span.Slice(y * stride, bitmap.Width * 4);
            for (var i = 3; i < row.Length; i += 4)
                if (row[i] != 255) return true;
        }
        return false;
    }

    /// <summary>
    /// The same pixels as <c>Encode(Png)</c> with the alpha channel left out: the bitmap is re-described as
    /// opaque, which makes the PNG encoder write colour type RGB (or grey) and nothing else. The colour
    /// channels are not touched, so this is not a premultiply.
    /// </summary>
    private static byte[] EncodeOpaquePng(CTexture decoded, out string ext)
    {
        ext = "png";
        using var bitmap = decoded.ToSkBitmap();
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, bitmap.ColorType, SKAlphaType.Opaque);
        using var pixmap = new SKPixmap(info, bitmap.GetPixels(), bitmap.RowBytes);
        using var data = pixmap.Encode(SKPngEncoderOptions.Default);
        return data?.ToArray() ?? [];
    }

    /// <summary>
    /// Why a texture produced no image — separating **the payload never arrived** from
    /// **the decoder has no path for this format**.
    ///
    /// "decoded to nothing" was the entire report, which is unactionable and, worse, hides the
    /// distinction that matters. A mip's `BulkData.Data` is a `Lazy` read straight out of the
    /// archive, so an empty payload where the header says bytes exist is a LOAD failure — the
    /// same class of fault as the export race — while a present payload with no image out is a
    /// decoder gap in CUE4Parse. Those need opposite responses and looked identical.
    ///
    /// Written because a texture failed on one run and succeeded on the next, and
    /// nothing in the log could say which kind of failure it had been.
    /// </summary>
    public static string Diagnose(UTexture texture)
    {
        // Under the gate, because diagnosing a failure must not cause one. This reads
        // `mip.BulkData.Data` and calls `GetFirstMip()`, which are the same lazy archive reads
        // `Write` now serialises — and it runs on the export's worker threads, on the one path
        // where something has already gone wrong.
        lock (AssetLoader.Gate)
        {
        try
        {
            var mips = texture.PlatformData?.Mips;
            var sb = new System.Text.StringBuilder();
            sb.Append(texture.GetType().Name).Append(' ').Append(texture.Format);
            if (texture is UTexture2D t2) sb.Append($" imported {t2.ImportedSize.X}x{t2.ImportedSize.Y}");
            sb.Append($", {mips?.Length ?? 0} mip(s)");

            if (mips is not { Length: > 0 }) return sb.Append("; NO MIPS AT ALL").ToString();

            var mip = mips[0];
            sb.Append($"; mip0 {mip.SizeX}x{mip.SizeY}");
            if (mip.SizeZ > 1) sb.Append($"x{mip.SizeZ}");

            var onDisk = mip.BulkData?.Header.SizeOnDisk ?? 0;
            int got;
            try { got = mip.BulkData?.Data?.Length ?? 0; }
            catch (Exception e) { return sb.Append($"; mip0 payload read THREW {e.GetType().Name}: {e.Message}").ToString(); }

            sb.Append($", payload {got:N0} of {onDisk:N0} byte(s) on disk");
            // GetFirstMip walks the mips for one whose bulk data is actually usable; null means
            // none of them are, whatever the headers claim.
            if (texture.GetFirstMip() is null) sb.Append("; NO MIP HAS USABLE BULK DATA");

            sb.Append(onDisk > 0 && got == 0
                ? " -> PAYLOAD DID NOT LOAD (a read failure, not a format the decoder lacks)"
                : " -> payload present, so this is a decoder gap for that format/type");
            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"could not be inspected: {e.GetType().Name}: {e.Message}";
        }
        }
    }
}

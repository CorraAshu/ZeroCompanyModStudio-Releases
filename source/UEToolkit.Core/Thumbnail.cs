using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;

namespace UEToolkit.Core;

/// <summary>
/// ONE ASSET'S PICTURE, small, as PNG bytes.
///
/// The Studio's clothing list wants a thumbnail per row and there are 1,178 wearable parts, so
/// this exists to be called ONE ROW AT A TIME as rows come on screen — never over the catalogue.
/// Decoding 1,178 textures to show twenty is the shape of mistake that makes a tool feel broken
/// while it is working perfectly.
///
/// The gating is <see cref="TextureExport"/>'s and the reasoning is written out there at length:
/// `Decode()` looks like it works on an already-loaded object, and it does not — the object is
/// loaded, its PIXELS are not, and fetching them reads the archive and caches into unsynchronised
/// fields. So the fetch is inside <see cref="AssetLoader.Gate"/> and the decode is outside it.
/// </summary>
public static class Thumbnail
{
    /// <summary>
    /// The picture for an icon path, as PNG, or null with a reason.
    ///
    /// <paramref name="objectPath"/> is what a part's IconPath holds — an OBJECT path such as
    /// "/Game/.../T_Thumb_X.T_Thumb_X". The package is everything before the last dot, which is
    /// exactly what <see cref="AssetLoader.PackagePath"/> already computes.
    /// </summary>
    public static byte[]? Png(IFileProvider provider, string objectPath, out string? why)
    {
        why = null;
        if (objectPath.Length == 0) { why = "no icon path"; return null; }

        try
        {
            var texture = AssetLoader.Load<UTexture2D>(provider, objectPath);
            if (texture is null) { why = "the icon is not a texture, or is not there"; return null; }

            int mipIndex;
            bool virtualTexture;
            lock (AssetLoader.Gate)
            {
                // A virtual texture decodes from chunk payloads the mip path does not cover, so it
                // stays inside the gate whole rather than being half-protected.
                virtualTexture = texture.PlatformData?.VTData is not null;
                mipIndex = virtualTexture ? 0 : texture.GetFirstMipIndex();
            }

            var decoded = virtualTexture
                ? AssetLoader.Read(() => texture.Decode(ETexturePlatform.DesktopMobile))
                : texture.DecodeMip(mipIndex, ETexturePlatform.DesktopMobile);

            if (decoded is null) { why = TextureExport.Diagnose(texture); return null; }

            var bytes = decoded.Encode(ETextureFormat.Png, saveHdrAsHdr: false, out _);
            if (bytes.Length == 0) { why = "encoded to nothing"; return null; }
            return bytes;
        }
        catch (Exception e)
        {
            // NAMED, not swallowed. A row with no picture and a row whose picture failed look
            // identical, and only one of them is worth chasing.
            why = e.Message;
            return null;
        }
    }
}

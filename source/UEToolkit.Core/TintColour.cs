namespace UEToolkit.Core;

using CUE4Parse.FileProvider;

/// <summary>
/// The colour a customization part paints through its tint mask.
///
/// This is the other half of how the game makes eleven helmets out of one. A colour variant is an
/// ordinary part carrying two fragments -- `CustomizationFragmentMaterialTexture`, which overrides
/// the material's `Tint Mask VT` with that variant's mask, and
/// `CustomizationFragmentMaterialColor`, which holds the colour and the material parameter to feed
/// it to. One says WHERE, the other says WHAT. Nothing links a variant to a base: they are
/// siblings sharing a mesh.
///
/// So authoring a variant is: duplicate the part, point the mask at your own, and change this one
/// property. The first two already work; this is the third.
///
/// THE VALUE IS AN FLinearColor AND THE MENU SHOWS sRGB. Measured on
/// CPD_H_Outfit_Clo000_HELM_TintA, whose colour reads `4B4B4B` in every tool that displays it:
///
///     Color  at byte 3487, 16 bytes:  29 5C 8F 3D  29 5C 8F 3D  29 5C 8F 3D  00 00 00 00
///
/// 0x3D8F5C29 is 0.07 as a float, and 0x4B is 75, and 75/255 = 0.294 -- which is 0.07 only after
/// the sRGB curve. Writing the sRGB fraction straight in would produce a part whose colour is
/// visibly too light and whose hex still reads back correctly in a byte dump, which is the kind of
/// wrong that survives a review.
///
/// The fourth float is alpha and the game ships it ZERO, so it is left exactly as found rather
/// than "corrected" to one.
/// </summary>
public static class TintColour
{
    /// <summary>The fragment class that holds the colour. Located by class, not by export name.</summary>
    public const string Fragment = "CustomizationFragmentMaterialColor";

    /// <summary>How many bytes an FLinearColor occupies: four floats.</summary>
    private const int Bytes = 16;

    /// <summary>
    /// One sRGB channel as the linear float the engine stores.
    ///
    /// The standard sRGB transfer function, not the 2.2 approximation -- UE uses this one, and the
    /// two differ most in the dark end where most of these armour colours live.
    /// </summary>
    public static float Linear(byte srgb)
    {
        var v = srgb / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>The inverse, for showing a stored colour back to the author.</summary>
    public static byte Srgb(float linear)
    {
        var v = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte) Math.Clamp((int) MathF.Round(v * 255f), 0, 255);
    }

    /// <summary>Six hex digits to three bytes, or null when it is not six hex digits.</summary>
    public static (byte R, byte G, byte B)? Parse(string? hex)
    {
        var clean = new string((hex ?? "").Where(Uri.IsHexDigit).ToArray());
        if (clean.Length != 6) return null;
        return (Convert.ToByte(clean[..2], 16),
                Convert.ToByte(clean[2..4], 16),
                Convert.ToByte(clean[4..6], 16));
    }

    /// <summary>
    /// Set the part's tint colour, given as sRGB hex.
    ///
    /// Read back through the parser afterwards for the same reason every other edit here is: an
    /// offset that was written is not proof that the offset was the property. See
    /// <see cref="ScalarEdit"/>, which exists because "wrote 20 at byte 1319" was printed,
    /// believed, and false.
    /// </summary>
    /// <param name="packageName">
    /// THE TEMPLATE'S package name, not the one being built.
    ///
    /// Locating a property needs the container header for the schema, and the package this edit
    /// is applied to has never been in a container -- the build is what makes it. The template IS
    /// mounted, and the authored package is a renamed copy of it, so its structure is the same
    /// one. Passing the new name gets "no mounted container holds ...", which is true and useless.
    /// </param>
    public static byte[] Set(IFileProvider provider, byte[] raw, string packageName, string hex,
                             out string? why, Action<string>? log = null)
    {
        why = null;

        if (Parse(hex) is not { } want)
        { why = $"'{hex}' is not six hex digits"; return raw; }

        if (!PropertyAdd.Locate(provider, raw, packageName, Fragment, "Color",
                                out var start, out var length, out why))
        {
            // Not a fault on its own: most parts carry no colour fragment, and a part that carries
            // none simply has no colour to set. The caller decides whether that matters.
            why = $"this part has no {Fragment} to set a colour on ({why})";
            return raw;
        }

        if (length != Bytes || start < 0 || start + length > raw.Length)
        { why = $"the colour is {length} byte(s), not the {Bytes} an FLinearColor takes"; return raw; }

        var was = $"{Srgb(BitConverter.ToSingle(raw, start)):X2}" +
                  $"{Srgb(BitConverter.ToSingle(raw, start + 4)):X2}" +
                  $"{Srgb(BitConverter.ToSingle(raw, start + 8)):X2}";

        var written = (byte[]) raw.Clone();
        BitConverter.GetBytes(Linear(want.R)).CopyTo(written, start);
        BitConverter.GetBytes(Linear(want.G)).CopyTo(written, start + 4);
        BitConverter.GetBytes(Linear(want.B)).CopyTo(written, start + 8);
        // Alpha untouched -- see the class note.

        if (Read(provider, written, packageName) is not { } back)
        { why = "written, but the colour could not be read back"; return raw; }

        var wanted = $"{want.R:X2}{want.G:X2}{want.B:X2}";
        if (!string.Equals(back, wanted, StringComparison.OrdinalIgnoreCase))
        { why = $"the colour was set to {wanted} and reads back {back}"; return raw; }

        log?.Invoke($"      tint colour: {was} -> {back}");
        why = null;
        return written;
    }

    /// <summary>The colour this package's fragment carries, as sRGB hex, or null when it has none.</summary>
    public static string? Read(IFileProvider provider, byte[] raw, string packageName)
    {
        if (!PropertyAdd.Locate(provider, raw, packageName, Fragment, "Color",
                                out var start, out var length, out _) ||
            length != Bytes || start < 0 || start + length > raw.Length)
            return null;

        return $"{Srgb(BitConverter.ToSingle(raw, start)):X2}" +
               $"{Srgb(BitConverter.ToSingle(raw, start + 4)):X2}" +
               $"{Srgb(BitConverter.ToSingle(raw, start + 8)):X2}";
    }
}

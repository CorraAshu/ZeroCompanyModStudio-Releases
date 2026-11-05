using System.Globalization;
using System.Numerics;
using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// THE GAME'S OWN COLOURS, as the player is offered them.
///
/// They are not a palette asset and there is no table of them. Each colour is a CUSTOMIZATION
/// PART of its own, filed under `/Outfit/Color/`, carrying a DisplayName and a MaterialColor
/// fragment holding one hex value — 456 of them. So the list is READ from the game rather than
/// written down anywhere, which is also why searching the index for "Palette" or "Swatch" finds
/// nothing: the thing is not called that.
///
/// IN CORE BECAUSE TWO APPLICATIONS WANT IT. The ImGui Studio has built this list for a while
/// inside its Artwork panel; the Avalonia viewport's tint picker needs the same 456 colours, and
/// a second copy of a reader is a second thing to be wrong about which folder holds them.
///
/// Which swatch drives which material parameter is settled separately and by measurement — see
/// <see cref="TintSwatches"/>: Primary is Color 01, Secondary Color 02, Tertiary Color 03 and
/// Visor Color 04.
/// </summary>
public static class TintPalette
{
    /// <param name="Hex">Six hex digits, the form <see cref="TintColour"/> parses and writes.</param>
    /// <param name="Rgb">The same value 0..1, for a shader uniform.</param>
    public sealed record Swatch(string Name, string Hex, Vector3 Rgb)
    {
        /// <summary>
        /// WHAT A PLAIN ComboBox DRAWS, which is where these are chosen from.
        ///
        /// THE HEX IS PART OF IT because the NAME IS NOT UNIQUE. The game ships several colour
        /// parts called Amber and several called Blue, so a list of names alone is a run of
        /// identical rows: the author, on the viewport tint picker, "seeing amber and blue
        /// repeated over and over without you knowing what their real variant colors are".
        ///
        /// Name is left alone rather than disambiguated, because it is what NameOfColour and the
        /// tint driver flag match on, and because it is the game's own name for the part. This
        /// adds the one thing that tells two of them apart and happens also to be the answer to
        /// the question being asked: the colour itself.
        /// </summary>
        public override string ToString() => $"{Name}   #{Hex}";
    }

    /// <summary>
    /// Every colour part in the game, by name.
    ///
    /// A PACKAGE LOAD PER COLOUR, so it is worth doing once and keeping. 456 loads is seconds,
    /// not minutes, but it is not something to do while somebody is dragging a picker open.
    /// </summary>
    public static List<Swatch> Read(IFileProvider provider, AssetIndex index)
    {
        var found = new List<Swatch>();

        foreach (var f in index.Root.DescendantFiles()
                     .Where(f => f.FullPath.Contains("/Outfit/Color/", StringComparison.OrdinalIgnoreCase) &&
                                 f.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
        {
            var (name, hex) = PartDetails.ColourPart(provider, f.FullPath);
            if (name is not { Length: > 0 } || hex is not { Length: 6 }) continue;
            if (!int.TryParse(hex, NumberStyles.HexNumber, null, out var packed)) continue;

            found.Add(new Swatch(name, hex, new Vector3(
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                (packed & 0xFF) / 255f)));
        }

        // BY NAME, because the order the index walks them in is the order of the container and
        // means nothing to anybody choosing a colour.
        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }
}

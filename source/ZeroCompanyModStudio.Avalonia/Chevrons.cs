using Avalonia.Media;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE FOLD ARROW, DRAWN RATHER THAN TYPED.
///
/// Every chevron in this application used to be a text glyph, and the author's report was that
/// they are "way to small to see". Measuring why turned up three separate faults, none of which a
/// bigger font size alone would have fixed:
///
///   THE CODEPOINTS ARE NOT IN THE UI FACE. U+25BE, U+25B8, U+2304 and U+203A are none of them in
///   Segoe UI, so every one of them was drawn by font FALLBACK into whatever Windows chose. The
///   size of that glyph's ink is then not a thing this application controls at all: the small
///   triangles carry about a third of an em, which at the FontSize actually set came out around
///   3.4 by 3.7 device pixels.
///
///   THE SIZES DISAGREED. Four values across the fifty-odd sites, 9, 10, 10.5 and 11.5, because
///   each site carried its own.
///
///   AND SO DID THE SHAPES. Two incompatible pairs, solid triangles in the sheet and thin
///   arrowheads everywhere else, so "the arrow" was never one thing the eye could learn.
///
/// A drawn path answers all three at once and cannot regress into any of them: no font can fail
/// to supply it, it is one shape, and its weight and colour come from one style rather than from
/// fifty attributes. It is also resolution independent, which a 3px glyph very much is not.
///
/// TWELVE BY TWELVE, stroked rather than filled. A chevron is a line with a corner in it; filling
/// one means authoring a closed outline and choosing a thickness by editing coordinates, where a
/// stroke is one number in the style.
/// </summary>
public static class Chevrons
{
    /// <summary>Open: pointing down, at what is beneath it.</summary>
    public static readonly Geometry Down = StreamGeometry.Parse("M 2.5,4.5 L 6,8 L 9.5,4.5");

    /// <summary>Closed: pointing along, at what is folded away.</summary>
    public static readonly Geometry Right = StreamGeometry.Parse("M 4.5,2.5 L 8,6 L 4.5,9.5");

    /// <summary>The one either way, so a caller reads as the question it is asking.</summary>
    public static Geometry For(bool open) => open ? Down : Right;
}

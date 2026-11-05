namespace UEToolkit.Core;

/// <summary>
/// What an authored part INHERITED from its template, and what happened to each piece.
///
/// Every fault this builder has shipped had one shape: a part is authored by copying one the game
/// already has, and anything the builder did not think to change came along with it. Seven
/// separate bugs, one cause --
///
///     the template's unlock gate      the part never appeared
///     the template's PrimaryAssetName the part collided with the template and lost
///     an Accepts.Outfit tag           the part was offered only beside one outfit set
///     the template's imports, dropped the loader crashed resolving a dependency
///     only the FIRST mesh option      the character wore the template's armour
///     a material colour fragment      the mesh's own colours were painted over
///     a string-table display name     the part wore the template's name
///
/// Each was fixed by noticing it. That is the part that does not scale, because the next template
/// carries something else. So the build now ENUMERATES what it inherited and classifies every
/// piece: replaced, kept on purpose, or NOT ACCOUNTED FOR. The last category is the whole point --
/// it is the only way a fragment nobody has thought about announces itself before shipping.
/// </summary>
public static class PartAudit
{
    /// <summary>What the builder does with a fragment it recognises.</summary>
    public enum Disposition
    {
        /// <summary>Its contents are substituted for the mod's own.</summary>
        Replaced,

        /// <summary>Carried through deliberately -- the part would be wrong without it.</summary>
        Kept,

        /// <summary>Carried, but it changes how the part looks or behaves. Reported.</summary>
        Suspect,

        /// <summary>Nobody has decided. The build says so rather than shipping it silently.</summary>
        Unaccounted,
    }

    public sealed record Item(string Fragment, Disposition What, string Why);

    /// <summary>
    /// Fragments this builder has actually reasoned about. Anything absent from here is reported,
    /// which is deliberate: the failure mode being designed against is a fragment that nobody
    /// considered riding along unnoticed, so an unknown name must be loud rather than ignored.
    /// </summary>
    private static readonly (string Prefix, Disposition What, string Why)[] Known =
    {
        ("BitReactorUIDataFragment", Disposition.Replaced,
            "display name and icon are substituted"),
        ("CustomizationFragmentMesh", Disposition.Replaced,
            "every mesh option is pointed at the mod's mesh"),
        ("CustomizationFragmentInstanceMesh", Disposition.Replaced,
            "every mesh option is pointed at the mod's mesh"),
        ("CustomizationFragmentOverrideSlots", Disposition.Kept,
            "this is what lets a helmet hide hair; conflicts with other outfit slots are scored"),
        ("CustomizationFragmentInstanceOverrideSlots", Disposition.Kept,
            "as above"),
        ("CustomizationFragmentFoley", Disposition.Kept,
            "footstep and movement sounds; harmless on a different mesh"),
        ("CustomizationFragmentHelmetVO", Disposition.Kept,
            "filters the wearer's voice as if through a helmet, which is what a helmet should do"),
        ("CustomizationFragmentMaterialColor", Disposition.Suspect,
            "RECOLOURS the mesh -- the template's palette will override the mod's own artwork"),
        // Reasoned about as of the slot-naming work: `tintMask` redirects the texture it paints
        // through, and the slots it names decide WHICH sections it reaches -- which is why the
        // build gives every grown slot a name of its own. It was reported as unaccounted-for
        // long after that stopped being true.
        ("CustomizationFragmentMaterialTexture", Disposition.Suspect,
            "paints a tint mask over the material slots it names -- `tintMask` redirects which " +
            "texture, and a slot it does not name keeps its own"),
        ("CustomizationFragmentAttributeSets", Disposition.Suspect,
            "carries weapon stat tables, which a wearable part should not have"),
        ("CustomizationFragmentAbilities", Disposition.Suspect,
            "grants abilities, which a wearable part should not have"),
    };

    /// <summary>Classify everything a template contributed.</summary>
    public static List<Item> Of(PartDetails.Detail template)
    {
        var items = new List<Item>();
        foreach (var fragment in (template.Fragments ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal)
                                                                             .OrderBy(x => x, StringComparer.Ordinal))
        {
            var match = Known.FirstOrDefault(k => fragment.StartsWith(k.Prefix, StringComparison.Ordinal));
            items.Add(match.Prefix is null
                ? new Item(fragment, Disposition.Unaccounted,
                           "this builder has never reasoned about this fragment; it is being " +
                           "carried through unchanged and may do anything")
                : new Item(fragment, match.What, match.Why));
        }
        return items;
    }

    /// <summary>The lines a build should print, or an empty list when nothing needs saying.</summary>
    public static List<string> Concerns(string partId, IEnumerable<Item> items) =>
        items.Where(i => i.What is Disposition.Suspect or Disposition.Unaccounted)
             .Select(i => $"{partId}: {i.Fragment} -- {i.Why}")
             .ToList();
}

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;

namespace UEToolkit.Core;

/// <summary>
/// HOW MANY COLOURS A PART LETS THE PLAYER CHOOSE, and which of them the part has taken away.
///
/// Reported by Nada: "I've got this tint file on the commando helmet I've duped but I'm not able
/// to access more than the 2 tints it had as default." He had authored a tint mask with three
/// channels painted and could reach two of them, which reads exactly like a limit in the tool.
///
/// It is not. Every outfit region has FOUR colour swatches and a part decides how many of them
/// the menu offers, by putting the rest in two containers on its OverrideSlots fragment. The
/// Clone Commando helmet, read out of the game:
///
///     CPD_H_Outfit_Clo010_HELM
///       LockedSlots   ...Outfit.Helmet.Color.Secondary   ...Outfit.Helmet.Color.Visor
///       HiddenSlots   ...Outfit.Helmet.Color.Secondary   ...Outfit.Helmet.Color.Visor
///
/// Two swatches taken away leaves two, which is the whole of the report.
///
/// **THE GAME DOES THIS TO ITSELF, which is the proof it is the mechanism and not a coincidence.**
/// <c>CPD_H_Outfit_Clo000_HELM</c> hides Secondary and Visor; <c>CPD_H_Outfit_Clo000_HELM_TintA</c>
/// through <c>_TintK</c> are the SAME helmet and the same mesh, hide only Visor, and each ships a
/// "Color 02" default. Those eleven variants exist to hand the second swatch back.
///
/// **WHICH SWATCH DRIVES WHICH MATERIAL PARAMETER** is not written down in any asset -- the link
/// is made by the game as it dresses a character -- so it is DERIVED, from every clone-outfit part
/// in the game cross-tabulated by what each sets against what each hides:
///
///     sets Color 01,02,03,04   hides nothing              3 parts
///     sets Color 01,02         hides Tertiary, Visor      8 parts
///     sets Color 01,02,03      hides Visor                7 parts
///     sets Color 02            hides Visor               72 parts
///
/// A part sets defaults for the swatches it OFFERS and hides the rest from the end, every time.
/// So Primary is Color 01, Secondary is Color 02, Tertiary is Color 03 and Visor is Color 04.
///
/// **AND WHETHER PUTTING ONE BACK WILL DO ANYTHING** is a fact rather than a guess, because the
/// material says so. <c>MI_Clo010_HELM</c> carries "Color 02 Intensity" 1 and "Color 04 Intensity"
/// 0 -- so the commando helmet's second swatch is live and merely unreachable, while its fourth is
/// switched off in the material and would come back as a picker that does nothing. That
/// distinction is the difference between a fix and a wasted evening, and the old wording in the
/// panel ("a swatch you put back may well change nothing on the model") hedged both ways because
/// nothing had gone and looked.
/// </summary>
public static class TintSwatches
{
    /// <summary>
    /// One colour swatch of one outfit region.
    ///
    /// <paramref name="Intensity"/> is the material's "Color NN Intensity", which is what settles
    /// whether the swatch reaches the model. Negative means no material said -- unknown, not zero.
    /// </summary>
    public sealed record Swatch(string Tag, string Name, string Parameter, int Index,
                                double Intensity, bool Hidden, bool Locked)
    {
        /// <summary>Whether the material multiplies this colour in at all.</summary>
        public bool Live => Intensity > 0.0001;

        /// <summary>Whether the material was read. False means "no answer", not "no".</summary>
        public bool Known => Intensity >= 0;

        /// <summary>Whether the menu offers it as things stand.</summary>
        public bool Offered => !Hidden && !Locked;

        /// <summary>Which channel of the tint mask paints it. Visor has none -- see the class note.</summary>
        public string Channel => Index switch
        {
            1 => "the RED channel of the tint mask",
            2 => "the GREEN channel of the tint mask",
            3 => "the BLUE channel of the tint mask",
            _ => "a separate mask, not one of the tint mask's three channels",
        };
    }

    /// <summary>What one part offers, and what it has taken away.</summary>
    public sealed record Reading(string Region, List<Swatch> Swatches, string? Why)
    {
        public IEnumerable<Swatch> Taken => Swatches.Where(s => !s.Offered);
        public int Offered => Swatches.Count(s => s.Offered);
    }

    /// <summary>The four swatch names, in the order the game's own parts hide them from.</summary>
    public static readonly string[] Names = ["Primary", "Secondary", "Tertiary", "Visor"];

    private const string SlotPrefix = "br.Customization.Slot.Character.Outfit.";

    /// <summary>
    /// The colour swatches of the part at <paramref name="package"/>.
    ///
    /// PRISTINE, like every other authoring read: with the author's own mods mounted, a plain
    /// lookup finds their copy of a part they have already changed.
    /// </summary>
    public static Reading Read(IFileProvider provider, string package)
    {
        PartDetails.Detail detail;
        try
        {
            var raw = AssetCopy.PristineRead(provider, package, out var why)?.Pieces
                               .FirstOrDefault(x => x.IsPackage)?.Data;
            detail = raw is null
                ? PartDetails.Read(provider, package)
                : PartDetails.ReadExports(
                      AssetLoader.Read(() => PropertyAdd.PristineExports(provider, raw, package)),
                      package);
            _ = why;
        }
        catch (Exception e) { return new Reading("", [], e.Message); }

        if (detail.Error is { Length: > 0 } bad) return new Reading("", [], bad);

        // WHICH REGION'S SWATCHES THESE ARE. A helmet's four are not a torso's four, and the tag
        // carries the region in the middle -- taken from what the part already claims rather than
        // from the slot the author picked in the editor, because a template is the authority on
        // what it is and the editor's slot is only what the author called it.
        var region = RegionOf(detail.HiddenSlots) ?? RegionOf(detail.LockedSlots ?? []) ??
                     RegionOf(detail.OverrideSlots) ?? "";
        if (region.Length == 0) return new Reading("", [], null);

        var intensity = Intensities(provider, detail.MeshPath);

        var swatches = new List<Swatch>();
        for (var i = 0; i < Names.Length; i++)
        {
            var tag = $"{SlotPrefix}{region}.Color.{Names[i]}";
            var parameter = $"Color {i + 1:00}";
            swatches.Add(new Swatch(
                tag, Names[i], parameter, i + 1,
                intensity.TryGetValue(parameter + " Intensity", out var v) ? v : -1,
                detail.HiddenSlots.Contains(tag, StringComparer.OrdinalIgnoreCase),
                (detail.LockedSlots ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }
        return new Reading(region, swatches, null);
    }

    /// <summary>The outfit region named in the middle of any slot tag, or null.</summary>
    private static string? RegionOf(IReadOnlyList<string> tags)
    {
        foreach (var tag in tags)
        {
            if (!tag.StartsWith(SlotPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = tag[SlotPrefix.Length..];
            var dot = rest.IndexOf('.');
            if (dot > 0) return rest[..dot];
        }
        return null;
    }

    /// <summary>
    /// The "Color NN Intensity" scalars of the materials on a part's mesh.
    ///
    /// EVERY material, not the first: a helmet is one slot and a torso is several, and a swatch is
    /// live if anything the part wears responds to it. Taking the maximum rather than the first
    /// keeps the answer "does this reach the model anywhere", which is the question being asked.
    /// </summary>
    private static Dictionary<string, double> Intensities(IFileProvider provider, string? meshPath)
    {
        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(meshPath)) return found;

        try
        {
            var materials = AssetLoader.Read(() =>
            {
                var mesh = provider.LoadPackage(AssetLoader.PackagePath(meshPath!))
                                   .GetExports().FirstOrDefault(
                                       e => e is CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh
                                              or CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh);
                return AssetLoader.MaterialsOf(mesh).Where(m => m is not null).ToList();
            });

            foreach (var material in materials)
            {
                if (material is null) continue;
                if (PartDetails.Unwrap(PartDetails.Value(material.Properties, "ScalarParameterValues"))
                    is not UScriptArray scalars) continue;

                foreach (var entry in scalars.Properties)
                {
                    if (PartDetails.Unwrap(entry.GenericValue) is not FStructFallback one) continue;
                    if (PartDetails.Unwrap(PartDetails.Value(one.Properties, "ParameterInfo"))
                        is not FStructFallback info) continue;
                    if (PartDetails.Value(info.Properties, "Name")?.ToString() is not { Length: > 0 } name)
                        continue;
                    if (PartDetails.Value(one.Properties, "ParameterValue") is not { } raw) continue;

                    var value = raw switch
                    {
                        float f => (double) f, double d => d, int i => i, _ => double.NaN,
                    };
                    if (double.IsNaN(value)) continue;
                    found[name] = found.TryGetValue(name, out var had) ? Math.Max(had, value) : value;
                }
            }
        }
        catch (Exception) { /* no material read is "unknown", which Swatch.Known reports */ }

        return found;
    }
}

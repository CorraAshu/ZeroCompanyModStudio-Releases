using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Objects;

namespace UEToolkit.Core;

/// <summary>
/// Everything a specialization is, read out of the part that defines it.
///
/// A specialization is a customization part like a helmet is, which is why the browser can already
/// list one -- and it is nothing like a helmet in what it CONTAINS, which is why the browser could
/// never do anything with one. It has no mesh. What it has is a name, an icon, one identity tag,
/// a handful of always-on abilities, a focus tree, and a gear kit:
///
///     BitReactorUIDataFragment                 name, short and long description, icon tag
///     CustomizationFragmentGameplayTags        the tag that IS this specialization
///     CustomizationFragmentAbilities           abilities granted outright
///     BrunoCustomizationFragmentLeveledAbilities   the focus tree
///     CustomizationFragmentGearKits            starting equipment
///     CustomizationFragmentAttributeSets       stat bonuses, as a table
///
/// The tree is three lines of six, six and five on every one of the game's eight, and each slot
/// carries what it unlocks and what it costs. That shape is worth stating because it is what an
/// editor has to lay out, and it is not obvious from the class names.
///
/// Read from the PART rather than from the registry: the registry knows a specialization's tags
/// and nothing else about it -- see PartsIndex -- so everything a person would actually want to
/// change lives here.
/// </summary>
public static class SpecDetails
{
    /// <summary>One rung of one focus-tree line: what it grants, and what it takes to get there.</summary>
    public sealed record Rung(string Ability, int MinLevel, int FocusLevel, int FocusCost)
    {
        public string Leaf
        {
            get
            {
                var cut = Ability.LastIndexOf('/');
                var leaf = cut < 0 ? Ability : Ability[(cut + 1)..];
                var dot = leaf.IndexOf('.');
                return dot < 0 ? leaf : leaf[..dot];
            }
        }
    }

    public sealed record Spec(
        string Path,
        string Name,
        string? DisplayName,
        string? ShortDescription,
        string? LongDescription,
        string? IconTag,
        string? IconPath,
        IReadOnlyList<string> AllowedSlots,
        IReadOnlyList<string> DisallowedSlots,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> Abilities,
        IReadOnlyList<IReadOnlyList<Rung>> Tree,
        IReadOnlyList<string> GearKits,
        IReadOnlyList<string> AttributeTables,
        string? Error)
    {
        /// <summary>Primary specializations are the ones the picker offers; secondaries are their partners.</summary>
        public bool IsPrimary =>
            AllowedSlots.Any(s => s.EndsWith(".Primary", StringComparison.OrdinalIgnoreCase));

        public bool IsSecondary =>
            AllowedSlots.Any(s => s.EndsWith(".Secondary", StringComparison.OrdinalIgnoreCase));

        /// <summary>How many rungs across the whole tree, which is 17 on everything the game ships.</summary>
        public int RungCount => Tree.Sum(line => line.Count);

        /// <summary>
        /// The tag that identifies THIS specialization, as opposed to the ones describing where it
        /// may go. A copied specialization keeps its template's, and two parts claiming to be the
        /// Assault is not a thing the game is expecting.
        /// </summary>
        public string? Identity => Tags.FirstOrDefault(
            t => t.Contains(".Specializations.", StringComparison.OrdinalIgnoreCase));
    }

    public static Spec Read(IFileProvider provider, string packagePath)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(packagePath)).GetExports().ToArray());

            string? display = null, shortDesc = null, longDesc = null, iconTag = null, iconPath = null;
            var abilities = new List<string>();
            var gearKits = new List<string>();
            var tables = new List<string>();
            var tags = new List<string>();
            var tree = new List<IReadOnlyList<Rung>>();

            foreach (var e in exports)
            {
                switch (e.ExportType)
                {
                    case "BitReactorUIDataFragment":
                        display ??= Text(e.Properties, "DisplayName");
                        shortDesc ??= Text(e.Properties, "ShortDescription");
                        longDesc ??= Text(e.Properties, "LongDescription");
                        // Through the browser's own reader rather than a second one: it already
                        // knows a brush can name its image by tag or by path, and falls back from
                        // the small image to the large. A specialization uses the tag form, which
                        // is why it shows no thumbnail in the parts list.
                        var art = PartDetails.ImageOf(e.Properties, "SmallImage");
                        if (art.Path is null && art.BankTag is null)
                            art = PartDetails.ImageOf(e.Properties, "LargeImage");
                        iconTag ??= art.BankTag;
                        iconPath ??= art.Path;
                        break;

                    case "CustomizationFragmentGameplayTags":
                    case "CustomizationFragmentInstanceGameplayTags":
                        tags.AddRange(PartDetails.TagsIn(new[] { e }, "GameplayTags"));
                        break;

                    case "CustomizationFragmentAbilities":
                    case "CustomizationFragmentInstanceAbilities":
                        abilities.AddRange(PartDetails.SoftPathArray(e.Properties, "Abilities"));
                        break;

                    case "CustomizationFragmentGearKits":
                    case "CustomizationFragmentInstanceGearKits":
                        gearKits.AddRange(PartDetails.SoftPathArray(e.Properties, "GearKits"));
                        break;

                    case "BrunoCustomizationFragmentLeveledAbilities":
                        foreach (var line in PartDetails.StructArray(e.Properties, "LeveledAbilities"))
                        {
                            var rungs = new List<Rung>();
                            foreach (var rung in PartDetails.StructArray(line.Properties, "LeveledAbilityList_Struct"))
                                rungs.Add(new Rung(
                                    PartDetails.SoftPathOf(rung.Properties, "LeveledAbility") ?? "",
                                    Int(rung.Properties, "MinLevel"),
                                    Int(rung.Properties, "FocusLevel"),
                                    Int(rung.Properties, "FocusCost")));
                            tree.Add(rungs);
                        }
                        break;
                }
            }

            // The slots live on the part itself, not on a fragment: the main export is whichever
            // one carries AllowedSlots, and its class is a Blueprint whose name varies by kind.
            var allowed = PartDetails.TagsIn(exports, "AllowedSlots");
            var disallowed = PartDetails.TagsIn(exports, "DisallowedSlots");

            // The stat table, which is the same fragment the weapon browser reads.
            foreach (var e in exports)
                if (e.ExportType is "CustomizationFragmentAttributeSets"
                                 or "CustomizationFragmentInstanceAttributeSets")
                    foreach (var v in AttributeTablesIn(e.Properties))
                        tables.Add(v);

            var name = packagePath[(packagePath.LastIndexOf('/') + 1)..];
            if (name.IndexOf('.') is var d and > 0) name = name[..d];

            return new Spec(packagePath, name, display, shortDesc, longDesc, iconTag, iconPath,
                            allowed, disallowed, tags, abilities, tree, gearKits, tables, null);
        }
        catch (Exception e)
        {
            return new Spec(packagePath, packagePath, null, null, null, null, null,
                            [], [], [], [], [], [], [], e.Message);
        }
    }

    /// <summary>Is this part a specialization at all? Decided by its slot, not by its name.</summary>
    public static bool LooksLikeSpec(string slotTag) =>
        slotTag.Contains(".Specializations.", StringComparison.OrdinalIgnoreCase);

    private static string? Text(List<FPropertyTag> props, string name) =>
        PartDetails.Value(props, name)?.ToString() is { Length: > 0 } s ? s : null;

    private static int Int(List<FPropertyTag> props, string name) =>
        PartDetails.Value(props, name) switch
        {
            int i => i,
            byte b => b,
            short sh => sh,
            long l => (int) l,
            float f => (int) f,
            double dd => (int) dd,
            _ => 0,
        };

    /// <summary>The attribute tables a spec points at, through the same map a weapon uses.</summary>
    private static List<string> AttributeTablesIn(List<FPropertyTag> props)
    {
        var found = new List<string>();
        if (PartDetails.Value(props, "AttributeSetsNew") is not UScriptMap map) return found;
        foreach (var (_, v) in map.Properties)
            if (v?.GenericValue is FStructFallback entry &&
                PartDetails.SoftPathOf(entry.Properties, "DefaultAttributes") is { Length: > 0 } t)
                found.Add(t);
        return found;
    }
}

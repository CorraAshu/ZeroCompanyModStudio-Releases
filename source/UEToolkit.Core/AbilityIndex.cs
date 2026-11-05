using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// Every ability and gear kit in the game, by name, so a specialization can be edited by PICKING
/// rather than by typing an asset path.
///
/// The paths are the reason this exists. A rung of a focus tree holds
/// <c>/Game/Game/GameData/Abilities/Gunslinger/GA_Firestorm_T1.GA_Firestorm_T1_C</c>, and three
/// separate things in that string have to be right: the folder, which is NOT the folder anybody
/// would guess (GA_Firestorm lives under Gunslinger and GA_BullRush under Soldier, while the
/// Assault specialization grants both), the object name after the dot, and the <c>_C</c> that marks
/// a Blueprint's generated class. Asking a person to type it is asking them to get all three right
/// from memory.
///
/// Read from the FILE INDEX rather than the asset registry: an ability is not a customization part,
/// so the registry's part list does not contain one, and the index is the complete answer in a
/// single pass.
/// </summary>
public static class AbilityIndex
{
    /// <summary>
    /// One pickable asset. <paramref name="Folder"/> is worth carrying separately because it is
    /// what tells two similar names apart -- the folder is the ability's class family, and it is
    /// the only thing distinguishing GA_Overwatch_Pistol from GA_Overwatch_Pistol_Rex at a glance.
    /// </summary>
    public sealed record Entry(string Name, string Folder, string Package);

    /// <summary>Abilities: the GA_ assets, which is what a focus tree and an always-on list hold.</summary>
    public const string AbilityPrefix = "GA_";

    /// <summary>Gear kits: the GK_ assets, a specialization's starting equipment.</summary>
    public const string GearKitPrefix = "GK_";

    /// <summary>
    /// Everything whose asset name starts with <paramref name="prefix"/>, sorted by name.
    ///
    /// Duplicated leaf names are kept, not collapsed. Two assets sharing a name is exactly the case
    /// where a silent pick puts the wrong power into somebody's specialization -- so both are
    /// listed, with their folders, and the person choosing decides.
    /// </summary>
    public static List<Entry> Of(IFileProvider provider, string prefix)
    {
        var found = new List<Entry>();
        foreach (var key in provider.Files.Keys)
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = key.LastIndexOf('/');
            if (slash < 0) continue;
            var name = key[(slash + 1)..^".uasset".Length];
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var pkg = PackageId.PackageNameFor(key, ModBuild.ProjectName);
            if (pkg is null) continue;

            // The last folder, which is the family: Abilities/Gunslinger -> "Gunslinger".
            var dir = pkg[..pkg.LastIndexOf('/')];
            found.Add(new Entry(name, dir[(dir.LastIndexOf('/') + 1)..], pkg));
        }

        return found.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>
    /// The object path to write for this entry, in the same SHAPE as the value it replaces.
    ///
    /// A Blueprint asset generates a class named with a <c>_C</c> suffix and the ability lists
    /// reference the CLASS; a gear kit is a plain data asset and references the object. Rather than
    /// guess which kind an asset is -- which would need the package opened -- the shape is copied
    /// from whatever is being replaced, because the game already wrote that one correctly.
    /// </summary>
    public static string PathLike(Entry entry, string replacing)
    {
        var wantsClass = replacing.EndsWith("_C", StringComparison.Ordinal);
        return $"{entry.Package}.{entry.Name}{(wantsClass ? "_C" : "")}";
    }

    /// <summary>The readable end of an object path: the asset name, without the _C.</summary>
    public static string Leaf(string objectPath)
    {
        if (objectPath.Length == 0) return "";
        var dot = objectPath.LastIndexOf('.');
        var leaf = dot >= 0 && dot < objectPath.Length - 1 ? objectPath[(dot + 1)..] : objectPath;
        return leaf.EndsWith("_C", StringComparison.Ordinal) ? leaf[..^2] : leaf;
    }
}

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;

namespace UEToolkit.Core;

/// <summary>
/// A weapon's numbers, read from the DataTable its part points at.
///
/// Weapons in this game are customization parts like any other -- same class, same fragments --
/// and their stats hang off a <c>CustomizationFragmentAttributeSets</c> fragment as a plain UE
/// DataTable of <c>AttributeMetaData</c> rows. So "show me the stats" and later "let me change
/// them" need no new system: the table is ordinary cooked data.
///
/// Row names are qualified attribute paths, e.g. <c>BitReactorRangedWeaponSet.Damage</c>, which
/// is why the set name is split out -- a part can carry more than one set.
///
/// It reads the PRISTINE game rather than the mount. With a mod installed, the plain provider
/// finds that mod's copy of a table it has already changed, so the "the game ships" column beside
/// the author's own value would show the author's own value -- and every edit would be recorded
/// against the wrong starting number.
/// </summary>
public static class WeaponStats
{
    public sealed record Attribute(string Set, string Name, float BaseValue, float Min, float Max)
    {
        /// <summary>
        /// Does the shipped base value sit outside the range the table declares?
        ///
        /// Worth surfacing rather than clamping. The pistol ships CriticalHitDamage at 4.0 with a
        /// declared MaxValue of 2.0, so these bounds are editor annotation rather than a runtime
        /// clamp -- and a stat editor that silently enforced them would refuse to reproduce values
        /// the game itself uses.
        /// </summary>
        public bool OutOfDeclaredRange => BaseValue < Min || BaseValue > Max;
    }

    /// <summary>The row struct this understands. Anything else is a different kind of table.</summary>
    public const string RowStruct = "AttributeMetaData";

    public static List<Attribute> Read(IFileProvider provider, string tablePath)
    {
        var result = new List<Attribute>();
        try
        {
            var table = AssetLoader.Read(() =>
                AssetCopy.PristineExports(provider, AssetLoader.PackagePath(tablePath))
                         .OfType<UDataTable>().FirstOrDefault());
            if (table?.RowMap is null) return result;

            // ONLY AN ATTRIBUTE TABLE. This read whatever DataTable it was handed and invented
            // BaseValue, MinValue and MaxValue as zero for every row that had none -- so pointing
            // an override at the ACTION POINT COST table produced a stat editor listing its
            // thirty-nine rows as attributes, every one reading 0 and marked "unset". Plausible,
            // completely wrong, and it is what the generic table editor was mistaken for.
            if (!string.Equals(table.RowStructName, RowStruct, StringComparison.OrdinalIgnoreCase))
                return result;

            foreach (var (key, row) in table.RowMap)
            {
                var full = key.Text;
                var dot = full.LastIndexOf('.');
                var set = dot > 0 ? full[..dot] : "";
                var name = dot > 0 ? full[(dot + 1)..] : full;
                result.Add(new Attribute(set, name,
                    Float(row, "BaseValue"), Float(row, "MinValue"), Float(row, "MaxValue")));
            }
            // Table order is the authoring order, which is meaningful -- Damage and Range first,
            // the falloff and overwatch detail after -- so it is kept rather than sorted.
        }
        catch { /* a part that names a table we cannot read reports no stats, not a crash */ }
        return result;
    }

    private static float Float(FStructFallback row, string name)
    {
        var v = row.Properties.FirstOrDefault(p => p.Name.Text == name)?.Tag?.GenericValue;
        return v switch
        {
            float f => f,
            double d => (float) d,
            int i => i,
            _ => 0f,
        };
    }
}

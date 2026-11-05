namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;

/// <summary>
/// WHAT THE ARMOURY ACTUALLY SELLS -- the shop inventory tables, and every slot in them.
///
/// This answers a question two features have now been blocked on, and the answer had been read
/// backwards for two days. Nothing appeared to enumerate a utility item or a weapon modification:
/// <c>--refs UtilityItem_Medkit_T1</c> scans 110,146 packages and finds ONE, the item itself. That
/// was written down as "discovery is an AssetManager scan", and a mod's own item was assumed to be
/// found the same way the game's are.
///
/// IT IS NOT. Three <c>BrunoShopInventoryTable</c> assets name the stock outright:
///
///     ShopTable_MS32_Utility_Tier1/2/3    51 slots, one UtilityItem_ each
///     ShopTable_MS32_Mods_Tier1/2/3       48 slots, one Modification_ each
///     ShopTable_AllUtility / _AllMods     the roots, which weight the three tiers by campaign arc
///
/// `--refs` missed them because the reference is a HARD IMPORT -- a package id in the container
/// plus a stored export hash -- and a scan for a NAME cannot see one. The same distinction that
/// made a weapon's firing sound un-renameable; see <see cref="CopyContents.Reference.Hard"/>.
///
/// TWO CONSEQUENCES, and both are good news:
///
/// - A mod's own utility item or modification is NOT found by any scan. It has to be put in a
///   table, or it will never be offered for sale however correct it is. This is why the
///   ModProbeCustomizations "PROBE BETA COIL" never turned up in the Black Market: nothing was
///   ever going to point at it, and waiting for a lucky restock could not have worked.
/// - Taking over a slot is an ordinary re-point of a hard import, which this tool already does and
///   which is confirmed in game. So a mod CAN put its own item on sale -- deterministically, at a
///   known slot, with no dice roll to wait on.
/// </summary>
public static class ShopTables
{
    /// <summary>The class every shop table is an instance of.</summary>
    public const string TableClass = "BrunoShopInventoryTable";

    /// <summary>
    /// One purchasable slot: which table, which entry in it, and what is in that entry.
    ///
    /// <paramref name="Index"/> is the entry's position in the table's <c>WeightedItems</c> array
    /// and is how a slot is addressed for a takeover -- the array cannot GROW in a cooked package,
    /// so authoring a new item for sale means naming which existing entry it stands in.
    ///
    /// <paramref name="Weight"/> is the roll weight and <paramref name="Stock"/> how many the shop
    /// carries. A slot with weight 0 in a tier table is not drawn.
    /// </summary>
    public sealed record Slot(string Table, string TableLeaf, int Index,
                              string Item, string ItemLeaf, double Weight, int Stock)
    {
        /// <summary>An entry that points at a sub-table rather than an item -- a tier root.</summary>
        public bool IsGroup => Item.Length == 0;
    }

    /// <summary>
    /// Every slot in every shop table in the mounted game, in table then entry order.
    ///
    /// PRISTINE ONLY, for the reason <see cref="WeaponIndex.Of"/> gives: this is the list of the
    /// GAME's slots, which is what an author picks one out of. A mod's own copy of a table is not
    /// one of them.
    /// </summary>
    public static List<Slot> Of(IFileProvider provider)
    {
        var found = new List<Slot>();
        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = key.LastIndexOf('/');
            if (slash < 0) continue;
            var leaf = key[(slash + 1)..^".uasset".Length];
            // By NAME first, because opening 110,146 packages to ask each one its class is not a
            // thing to do while drawing a list. Every table the game ships is ShopTable_*, and the
            // class is checked below on the few that match, so a rename in a patch costs the row
            // rather than producing a wrong one.
            if (!leaf.StartsWith("ShopTable", StringComparison.Ordinal)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pkg) continue;
            found.AddRange(Read(provider, pkg, leaf));
        }
        return found;
    }

    /// <summary>The slots of one table, or nothing when it will not open or is not a shop table.</summary>
    private static List<Slot> Read(IFileProvider provider, string package, string leaf)
    {
        var found = new List<Slot>();
        try
        {
            var table = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(package)).GetExports()
                        .FirstOrDefault(e => e.ExportType == TableClass));
            if (table is null) return found;
            if (PartDetails.Value(table.Properties, "WeightedItems") is not UScriptArray entries)
                return found;

            for (var i = 0; i < entries.Properties.Count; i++)
            {
                if (PartDetails.Unwrap(entries.Properties[i]?.GenericValue)
                    is not FStructFallback entry) continue;

                var weight = Number(entry.Properties, "Weight");
                var item = "";
                var stock = 0;
                if (PartDetails.Unwrap(PartDetails.Value(entry.Properties, "ShopItem"))
                    is FStructFallback shopItem)
                {
                    item = Reference(PartDetails.Value(shopItem.Properties, "Item"));
                    stock = (int) Number(shopItem.Properties, "Stock");
                }

                found.Add(new Slot(package, leaf, i, item,
                                   item.Length == 0 ? "" : item[(item.LastIndexOf('/') + 1)..],
                                   weight, stock));
            }
        }
        catch { }
        return found;
    }

    /// <summary>
    /// The package a slot's item reference names.
    ///
    /// HARD, which is the whole point of this file -- the value is an FPackageIndex, so resolving
    /// it is what turns a slot into a name a person can read, and re-pointing it is what puts
    /// something else on sale.
    /// </summary>
    private static string Reference(object? value)
    {
        if (PartDetails.Unwrap(value) is not FPackageIndex pi || pi.IsNull) return "";
        try
        {
            var path = pi.ResolvedObject?.GetPathName() ?? "";
            var dot = path.LastIndexOf('.');
            var slash = path.LastIndexOf('/');
            return dot > slash ? path[..dot] : path;
        }
        catch { return ""; }
    }

    private static double Number(List<FPropertyTag> props, string name) =>
        PartDetails.Value(props, name) switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0,
        };
}

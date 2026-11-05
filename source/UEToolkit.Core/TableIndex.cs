using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// Every data table the game ships, listed from the ASSET REGISTRY rather than by opening packages.
///
/// A user of the released tool: "You working on a way to edit the economy files such as DT_Costs
/// and those assets? They are unclickable/undraggable for me." They were unclickable because no
/// browser listed them -- the tool had a browser per KIND OF THING (parts, weapons, specs) and a
/// data table is not one of those, it is the shape a hundred different things are stored in.
///
/// **WHY THE REGISTRY.** Finding tables by opening packages means opening 180,000 of them; finding
/// them by name means trusting a convention, and the convention is wrong -- <c>DT_Costs</c> and
/// <c>DT_CostsTable_v4</c> are tables, and so are the weapon attribute tables, which are called
/// <c>Attributes_RangedWeap_...</c> and carry no prefix at all. The registry already records every
/// asset's class, so the answer is one file read and a walk. That is also why this is the game's
/// OWN registry (<see cref="ModBuild.PristineRegistry"/>) rather than whatever is mounted.
///
/// A `CompositeDataTable` is listed beside a plain one because it is editable in the same way and
/// people will look for it by name -- but it carries a trap that the plain kind does not, and
/// <see cref="TableRows"/> is where that is explained and reported.
/// </summary>
public static class TableIndex
{
    /// <summary>
    /// One table. <paramref name="Folder"/> is the last path segment, which is what tells two
    /// tables with similar names apart -- five of them live in <c>DynamicGACosts</c> alone.
    /// </summary>
    public sealed record Entry(string Name, string Folder, string Package, string Class)
    {
        /// <summary>Whether its rows are declared to come from other tables. See <see cref="TableRows"/>.</summary>
        public bool IsComposite =>
            Class.Contains("Composite", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The registry class names that mean "this holds rows THIS CAN READ".
    ///
    /// NOT CurveTable, and that is a decision rather than an oversight. The game ships eight of
    /// them and they are a different shape entirely -- keys and interpolation, not rows and
    /// fields -- so <see cref="TableRows"/> reports "holds no data table" for every one. Listing
    /// eight rows that always fail is worse than not listing them: it reads as a broken tool
    /// rather than an unsupported kind. They are also what backs the "a curve table decides this"
    /// warning on an ability's numbers, so they are worth doing properly one day.
    /// </summary>
    private static readonly string[] Classes = ["DataTable", "CompositeDataTable"];

    /// <summary>
    /// Every table in the game, sorted by folder then name.
    ///
    /// Returns an empty list rather than throwing: a caller drawing a browser wants "none found"
    /// as something it can say on screen, and a game with no registry at all is a game this whole
    /// panel does not apply to.
    /// </summary>
    public static List<Entry> Of(IFileProvider provider)
    {
        var found = new List<Entry>();
        try
        {
            if (ModBuild.PristineRegistry(provider, out _) is not { } raw) return found;
            var doc = RegistryDocument.Read(raw);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var record in RegistryAssets.Walk(doc))
            {
                var cls = Name(doc, record.ClassName);
                if (!Classes.Contains(cls, StringComparer.Ordinal)) continue;

                var package = Name(doc, record.PackageName);
                if (package.Length == 0 || !seen.Add(package)) continue;

                var leaf = package[(package.LastIndexOf('/') + 1)..];
                var dir = package[..package.LastIndexOf('/')];
                found.Add(new Entry(leaf, dir[(dir.LastIndexOf('/') + 1)..], package, cls));
            }
        }
        catch (Exception) { return found; }

        return found.OrderBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>A name index resolved through the document's own batch, or empty if out of range.</summary>
    private static string Name(RegistryDocument doc, uint index) =>
        index < doc.Names.Count ? doc.Names[(int) index] : "";
}

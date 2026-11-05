namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Objects;

/// <summary>
/// An enemy GROUP's health and combat numbers, and how to change one group without changing the
/// group that shares its tables.
///
/// WHY A SCREEN AND NOT THE TABLE GRID. Every table in this game has been editable since
/// `753a914`, these 286 included -- but editing one of them edits it for everyone who reads it,
/// and in this game that is rarely who you think. `CPD_TacticalSpec_Enemy_Generic_Marauder` is
/// used by 15 characters, 12 Outlaw and 3 Pyke, and the tables it points at are the SEPARATIST
/// BX Droid Commando's. Nerf the Outlaws in place and you nerf the droids. That is the whole
/// reason this exists.
///
/// THE CHAIN, traced out of the shipped game:
///
///     CD_Char_Enemy_OutlawA_Marauder_Hum_001        a character definition
///       -> CPD_TacticalSpec_Enemy_Generic_Marauder  the spec: THE STATS LIVE HERE
///            8 x CustomizationFragmentAttributeSets, one per tier x difficulty
///              each -> BitReactorHealthSet -> Attributes_..._Health_T1[_Hard]     2 rows
///                      BitReactorCombatSet -> Attributes_..._Combat_T1[_Hard]    12 rows
///
/// Sixteen tables per group. The axes are Faction / Role / Tier / Difficulty and nothing else --
/// there is no planet and no mission axis.
///
/// WHICH FRAGMENT IS WHICH TIER IS NOT READ, deliberately. The fragments carry no tier key this
/// reader has to decode; the tier and difficulty are in the TABLE'S NAME (`_T1`, `_T4_Hard`), and
/// taking them from there means the reader cannot be wrong about a keying it never looked at. Every
/// `DefaultAttributes` soft path under every attribute-set fragment is collected, and that set IS
/// the group.
///
/// MULTIPLIERS, NOT TYPED VALUES. A group is 16 tables x (2 + 12) rows = 224 numbers, which is not
/// a thing anyone fills in. A multiplier per STAT applies across the whole spread and keeps the
/// game's own curve -- a T4 Insane enemy stays harder than a T1 Easy one, which is what an author
/// means by "make Outlaws weaker" and is not what typing 224 numbers would produce.
/// </summary>
public static class EnemyStats
{
    /// <summary>Where the enemy tactical specs live. Their own folder, so this is not a guess.</summary>
    private const string SpecFolder = "Customizations/Characters/Common/Specialization/Tactical/Enemy";

    /// <summary>
    /// Where the enemy CHARACTER definitions live -- 277 packages, which is what makes the reverse
    /// lookup affordable. The same question asked against the whole game scans 110,160.
    /// </summary>
    private const string PawnFolder = "Core/Pawns/EnemyCharacters";

    /// <summary>One stat table this group uses, and what the name says about when it applies.</summary>
    /// <param name="Set">`BitReactorHealthSet` or `BitReactorCombatSet`, from the map key.</param>
    /// <param name="Tier">T1, T2, T4, TX -- as written in the asset name.</param>
    /// <param name="Difficulty">"" for the default, else Easy / Hard / Insane.</param>
    public sealed record Table(string Package, string Leaf, string Set, string Tier, string Difficulty)
    {
        public string When => Difficulty.Length == 0 ? Tier : $"{Tier} {Difficulty}";
    }

    /// <summary>One row of one table: what it is called and what the game ships it at.</summary>
    public sealed record Stat(string Row, double Base);

    /// <summary>
    /// A group: one tactical spec, the tables it points at, and who is affected by changing it.
    /// </summary>
    /// <param name="Users">
    /// The character definitions that name this spec. This is the BLAST RADIUS and the single most
    /// useful thing on the screen -- an author changing "Marauder (Generic)" is changing Outlaws
    /// AND Pykes, and nothing else in the game will tell them so.
    /// </param>
    public sealed record Group(string Package, string Leaf, string Display,
                               IReadOnlyList<Table> Tables, IReadOnlyList<string> Users)
    {
        /// <summary>The factions its users belong to, from the folder each definition sits in.</summary>
        public IReadOnlyList<string> Factions => Users
            .Select(FactionOf).Where(f => f.Length > 0)
            .Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

        public string Describe => Users.Count == 0
            ? $"{Display} -- no character uses it"
            : $"{Display} -- {Users.Count} character(s): {string.Join(", ", Factions)}";
    }

    /// <summary>
    /// Every enemy group in the game, with its tables. <paramref name="withUsers"/> costs one pass
    /// over the 277 enemy pawn packages and is what fills in <see cref="Group.Users"/>; without it
    /// the blast radius is empty and the caller must not claim one.
    /// </summary>
    public static List<Group> All(IFileProvider provider, bool withUsers = true)
    {
        var specs = provider.Files.Keys
            .Where(p => p.Contains(SpecFolder, StringComparison.OrdinalIgnoreCase) &&
                        p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var users = withUsers ? Users(provider) : new Dictionary<string, List<string>>();

        var groups = new List<Group>();
        foreach (var path in specs)
        {
            var leaf = Path.GetFileNameWithoutExtension(path);
            var tables = TablesOf(provider, path);
            if (tables.Count == 0) continue;          // a spec with no attribute sets is not a group

            // THE GAME PACKAGE NAME, not the mounted file path. An override targets `/Game/Game/...`
            // and a container path is `SWZeroCompany/Content/Game/...`; handing over the second
            // builds a mod whose every copy is correct and whose override is SKIPPED as "not in
            // any mounted container" -- which is what the first build of this did.
            var package = PackageId.PackageNameFor(path, ModBuild.ProjectName);
            if (package is null) continue;

            groups.Add(new Group(package, leaf, Pretty(leaf), tables,
                                 users.TryGetValue(leaf, out var u) ? u : []));
        }
        return groups;
    }

    /// <summary>
    /// Every table a spec points at, from every attribute-set fragment it carries.
    ///
    /// Read off the SOFT PATHS rather than by resolving them: the table is copied by package name
    /// and never loaded here, so making this depend on 16 more package loads per group would cost
    /// the whole screen its responsiveness and buy nothing.
    /// </summary>
    public static List<Table> TablesOf(IFileProvider provider, string specPath)
    {
        var found = new List<Table>();
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(specPath)).GetExports().ToList());

            foreach (var e in exports)
            {
                if (!e.ExportType.Contains("AttributeSets", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var p in e.Properties)
                {
                    if (p.Tag?.GenericValue is not UScriptMap map) continue;
                    foreach (var kv in map.Properties)
                    {
                        var set = Leaf(kv.Key?.GenericValue?.ToString() ?? "");
                        var table = SoftPathIn(kv.Value?.GenericValue);
                        if (table.Length == 0) continue;

                        var package = table.Contains('.') ? table[..table.IndexOf('.')] : table;
                        var leaf = Leaf(package);
                        if (found.Any(x => string.Equals(x.Package, package, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var (tier, difficulty) = TierOf(leaf);
                        found.Add(new Table(package, leaf, set, tier, difficulty));
                    }
                }
            }
        }
        catch (Exception) { /* a spec that will not parse is simply not a group */ }
        return found;
    }

    /// <summary>The rows of one stat table, with the value the game ships.</summary>
    public static List<Stat> StatsOf(IFileProvider provider, string tablePackage)
    {
        var stats = new List<Stat>();
        try
        {
            foreach (var row in TableRows.Read(provider, tablePackage).Rows)
                // BaseValue is the one the game starts from; Min and Max are editor annotation and
                // are NOT runtime clamps -- the DC-17 ships a CriticalHitDamage above its own
                // declared max. Scaling them would be scaling a label.
                if (row.Cells.FirstOrDefault(c =>
                        c.Field.Equals("BaseValue", StringComparison.OrdinalIgnoreCase)) is { Numeric: true } bv)
                    stats.Add(new Stat(row.Name, bv.Number));
        }
        catch (Exception) { }
        return stats;
    }

    /// <summary>
    /// Which character definitions name each spec, in one pass over the enemy pawn folder.
    ///
    /// By NAME TABLE rather than by resolving references: a package's own name table holds every
    /// name it mentions, which is how --refs answers this, and it means 277 header reads instead
    /// of 277 full loads. Measured at 1.2 seconds for the folder.
    /// </summary>
    private static Dictionary<string, List<string>> Users(IFileProvider provider)
    {
        var byspec = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pawns = provider.Files.Keys
            .Where(p => p.Contains(PawnFolder, StringComparison.OrdinalIgnoreCase) &&
                        p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));

        foreach (var pawn in pawns)
        {
            try
            {
                var names = AssetLoader.Read(() =>
                    ZenPackage.Read(provider.SaveAsset(pawn)).Names);
                foreach (var n in names)
                {
                    if (!n.StartsWith("CPD_TacticalSpec_", StringComparison.Ordinal)) continue;
                    if (!byspec.TryGetValue(n, out var list)) byspec[n] = list = new List<string>();
                    var leaf = Path.GetFileNameWithoutExtension(pawn);
                    if (!list.Contains(leaf)) list.Add(leaf);
                }
            }
            catch (Exception) { }
        }
        return byspec;
    }

    // ------------------------------------------------------------------ planning the edit

    /// <summary>What the author asked for: a multiplier per stat row, across the whole group.</summary>
    public sealed record Adjust(string Row, double Multiplier);

    /// <summary>One number the build will write, and where.</summary>
    public sealed record Change(string CopyId, string Row, double Was, double Now);

    /// <summary>
    /// What a group's adjustment becomes: one copy per table, the numbers inside them, and the
    /// rename that points the spec at the copies.
    ///
    /// NOTHING OF THE GAME'S IS TOUCHED. Each table is copied KEEPING ITS LEAF NAME -- a soft
    /// object path stores package and asset name separately, so an identical leaf means only the
    /// package path has to be re-pointed -- and the spec override rewrites those paths. The BX
    /// droids go on reading the originals at 28.
    /// </summary>
    public sealed record Plan(string SpecPackage, IReadOnlyList<(string Id, string Template)> Copies,
                              IReadOnlyList<Change> Changes,
                              IReadOnlyDictionary<string, string> Renames)
    {
        public string Describe =>
            $"{Copies.Count} table(s) copied, {Changes.Count} number(s) changed, " +
            $"{Renames.Count} path(s) re-pointed on {Leaf(SpecPackage)}";
    }

    /// <summary>
    /// Turn multipliers into the copies, numbers and renames a project needs.
    ///
    /// A multiplier of 1 changes nothing and is skipped -- so a group the author opened and left
    /// alone contributes no copies, and a mod does not grow by 16 tables for a look.
    ///
    /// ROUNDED, because these are hit points and action points. 28 x 0.5 is 14 and not 14.000001,
    /// and a table full of long decimals is a table nobody can read back. Anything that rounds to
    /// its own starting value is dropped for the same reason a multiplier of 1 is.
    /// </summary>
    /// <param name="packageNameOf">
    /// What a copy of this leaf will be CALLED once built. Passed in rather than assembled here,
    /// the same way MaterialSkin.Repaint takes it: the build already owns that arithmetic, and the
    /// first version of this reproduced it and lost a folder -- the copies landed at
    /// `/Game/Game/Customizations/...` while the rename pointed at `/Game/Customizations/...`, so
    /// every table was copied, edited and then pointed at from nowhere.
    /// </param>
    public static Plan Build(IFileProvider provider, Group group, IReadOnlyList<Adjust> adjusts,
                             Func<string, string> packageNameOf)
    {
        var copies = new List<(string Id, string Template)>();
        var changes = new List<Change>();
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);

        var wanted = adjusts.Where(a => Math.Abs(a.Multiplier - 1.0) > 0.0001)
                            .ToDictionary(a => a.Row, a => a.Multiplier, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return new Plan(group.Package, copies, changes, renames);

        foreach (var table in group.Tables)
        {
            var stats = StatsOf(provider, table.Package);
            var mine = stats.Where(s => wanted.ContainsKey(s.Row)).ToList();
            if (mine.Count == 0) continue;             // this table carries none of the adjusted rows

            var rows = new List<Change>();
            foreach (var s in mine)
            {
                var now = Math.Round(s.Base * wanted[s.Row]);
                if (Math.Abs(now - s.Base) < 0.0001) continue;
                rows.Add(new Change(table.Leaf, s.Row, s.Base, now));
            }
            if (rows.Count == 0) continue;

            copies.Add((table.Leaf, table.Package));
            changes.AddRange(rows);
            renames[table.Package] = packageNameOf(table.Leaf);
        }

        return new Plan(group.Package, copies, changes, renames);
    }

    // ------------------------------------------------------------------ names

    /// <summary>
    /// `Attributes_Enemy_Separatist_BXMarauder_Health_T4_Hard` -> ("T4", "Hard").
    ///
    /// From the name because that is where it is. The tiers this game ships are T1, T2, T4 and TX;
    /// an unrecognised one comes back as itself rather than being forced into the list, because a
    /// tier nobody has seen is worth showing rather than hiding.
    /// </summary>
    public static (string Tier, string Difficulty) TierOf(string leaf)
    {
        var bits = leaf.Split('_');
        for (var i = bits.Length - 1; i >= 0; i--)
        {
            var b = bits[i];
            if (b.Length < 2 || b[0] != 'T') continue;
            if (!(b[1] == 'X' || char.IsDigit(b[1]))) continue;
            var difficulty = i + 1 < bits.Length ? string.Join(" ", bits[(i + 1)..]) : "";
            return (b, difficulty);
        }
        return ("", "");
    }

    /// <summary>`CPD_TacticalSpec_Enemy_Generic_Marauder` -> `Enemy Generic Marauder`.</summary>
    public static string Pretty(string leaf)
    {
        var s = leaf;
        if (s.StartsWith("CPD_TacticalSpec_", StringComparison.Ordinal)) s = s["CPD_TacticalSpec_".Length..];
        return s.Replace('_', ' ');
    }

    private static string FactionOf(string pawnLeaf)
    {
        // CD_Char_Enemy_OutlawA_Marauder_Hum_001 -> Outlaw. The trailing letter is a variant
        // within the faction, not a faction of its own -- OutlawA and OutlawB are both Outlaws.
        var bits = pawnLeaf.Split('_');
        var at = Array.FindIndex(bits, b => b.Equals("Enemy", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= bits.Length) return "";
        var f = bits[at + 1];
        return f.Length > 1 && char.IsUpper(f[^1]) && char.IsLower(f[^2]) ? f[..^1] : f;
    }

    private static string SoftPathIn(object? value)
    {
        // The map's value is a struct holding DefaultAttributes, so the path is one level down.
        if (value is FScriptStruct fs) value = fs.StructType;
        if (value is not FStructFallback sf) return "";
        foreach (var p in sf.Properties)
            if (p.Name.Text.Equals("DefaultAttributes", StringComparison.OrdinalIgnoreCase))
                return p.Tag?.GenericValue?.ToString() ?? "";
        return "";
    }

    private static string Leaf(string path)
    {
        if (path.Length == 0) return path;
        var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('.'));
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }
}

namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;

/// <summary>
/// WHO A PLAYER CAN RECRUIT, and what each of them is made of.
///
/// The roster is three DataTables, not a scan: <c>DT_InitialCharacterPool</c> (43 rows),
/// <c>_Clones</c> (4) and <c>_Droids</c> (4). Nothing else decides who turns up in the databank,
/// and 794 <c>CD_Char*</c> assets in the game are NOT in any of them -- every enemy, every
/// campaign walk-on. That gap is the whole subject: the characters exist and the roster does not
/// name them.
///
/// A ROW IS TWO SOFT REFERENCES, which is the fact that makes this cheap:
///
///     PoolCharacterData
///       CustomizationDefinition  -> CD_CharDatabank05      the character
///       CharacterClass           -> Char_Hero_Humanoid_C   the pawn it spawns as
///       bIsActive, PoolCharacterID, CustomizationSlots, two timestamps
///
/// Both are SOFT paths inside the row struct, so re-pointing one is an ordinary
/// <c>renameNames</c> override -- built, proven, and confirmed to build clean against this very
/// table. That means "swap a recruit for one of the game's other 794 characters" needs no new
/// capability at all. What it does NOT give is a 44th recruit: a row cannot be added yet, so
/// taking one over costs whoever was in it.
///
/// `--copyrefs` on one of these tables finds only its RowStruct, and that is not evidence the rows
/// point at nothing -- a DataTable's rows are not export properties, so the reference walker never
/// sees them. Read here through CUE4Parse's own <see cref="UDataTable.RowMap"/>.
/// </summary>
public static class CharacterIndex
{
    /// <summary>The roster tables, by the name they all share.</summary>
    public const string PoolPrefix = "DT_InitialCharacterPool";

    /// <summary>
    /// WHAT THE GAME ITSELF COUNTS AS A ROSTER TABLE, which is not what this reader assumed.
    ///
    /// `DR_InitialCharacterPool`'s sources are META sources: they search
    /// `$AssetSearchRoots/Game/Customizations/` with IncludePatterns `*InitialCharacterPool`,
    /// `*InitialCharacterPool_Clones*` and `*InitialCharacterPool_Droids*`. So the rule is ENDS
    /// WITH (or contains, for the two suffixed pools) -- not STARTS WITH.
    ///
    /// This reader required `DT_InitialCharacterPool...`, so it could not see a table a MOD
    /// ships -- `DT_PoolProbeInitialCharacterPool` was read by the game and offered a recruit in
    /// game while this index reported nothing at all. Once the Studio began shipping such tables
    /// by default it could no longer see the recruits it had just made.
    ///
    /// Measured 2026-09-20 off the registry asset's own properties.
    /// </summary>
    public static bool IsPoolTable(string leaf) =>
        leaf.Contains("InitialCharacterPool", StringComparison.OrdinalIgnoreCase) &&
        leaf.StartsWith("DT_", StringComparison.OrdinalIgnoreCase);

    /// <summary>A character definition -- what a pool row points at.</summary>
    public const string CharacterPrefix = "CD_Char";

    /// <summary>
    /// One recruit the game offers.
    ///
    /// <paramref name="Name"/> and <paramref name="Story"/> come from the character's NAME PART,
    /// two levels down -- the row names a CD_, whose Info.Name slot names a
    /// <c>CPD_*_Name_*</c>, which carries a <c>CustomizationFragmentName</c> holding a first name,
    /// a last name and a biography. That part is what a mod would author to give a recruit an
    /// identity of its own.
    /// </summary>
    public sealed record Recruit(
        string Pool, string PoolLeaf, string Row,
        string Definition, string Pawn,
        string Name, string Story,
        string NamePart, string Species, string CharacterClass,
        bool Active,
        IReadOnlyList<string> Missing)
    {
        public bool Complete => Missing.Count == 0;

        /// <summary>"Astromech" / "Humanoid", from the pawn class the row spawns.</summary>
        public string Kind
        {
            get
            {
                var leaf = Leaf(Pawn);
                var at = leaf.LastIndexOf('_');
                return at > 0 ? leaf[(at + 1)..] : leaf;
            }
        }

        /// <summary>What the roster calls this slot, which is the row key.</summary>
        public string Describe => Name.Length > 0 ? $"{Name} ({Row})" : Row;

        public static string Leaf(string package) =>
            package.Length == 0 ? "" : package[(package.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// Every row of every roster table, in table then row order.
    ///
    /// PRISTINE ONLY, for the reason <see cref="WeaponIndex.Of"/> gives -- with mods mounted this
    /// would list somebody else's roster edits as the game's.
    /// </summary>
    public static List<Recruit> Of(IFileProvider provider)
    {
        var found = new List<Recruit>();

        // The name part's text is read once per PART rather than once per row: several rows can
        // share a character, and opening the same package fifty times to draw one list is the
        // shape of a tab that takes a second to appear.
        var named = new Dictionary<string, (string Name, string Story)>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var leaf = key[(key.LastIndexOf('/') + 1)..^".uasset".Length];
            if (!IsPoolTable(leaf)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pool) continue;

            UDataTable? table;
            try
            {
                table = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(pool)).GetExports()
                            .OfType<UDataTable>().FirstOrDefault());
            }
            catch { continue; }
            if (table is null) continue;

            foreach (var (row, value) in table.RowMap)
            {
                var missing = new List<string>();
                var definition = Package(PartDetails.SoftPathOf(
                    PartDetails.Value(value.Properties, "CustomizationDefinition")) ?? "");
                var pawn = Package(PartDetails.SoftPathOf(
                    PartDetails.Value(value.Properties, "CharacterClass")) ?? "");
                if (definition.Length == 0) missing.Add("CustomizationDefinition");
                if (pawn.Length == 0) missing.Add("CharacterClass");

                var (namePart, species, charClass) = SlotsOf(provider, definition);
                if (definition.Length > 0 && namePart.Length == 0) missing.Add("a name part");

                if (namePart.Length > 0 && !named.ContainsKey(namePart))
                    named[namePart] = TextOf(provider, namePart);
                var text = namePart.Length > 0 && named.TryGetValue(namePart, out var t)
                    ? t : ("", "");

                found.Add(new Recruit(
                    Pool: pool, PoolLeaf: leaf, Row: row.Text,
                    Definition: definition, Pawn: pawn,
                    Name: text.Item1, Story: text.Item2,
                    NamePart: namePart, Species: species, CharacterClass: charClass,
                    Active: PartDetails.Value(value.Properties, "bIsActive") is not bool b || b,
                    Missing: missing));
            }
        }

        return found.OrderBy(r => r.PoolLeaf, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Name.Length > 0 ? r.Name : r.Row, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>
    /// Every character definition in the game -- what a roster row could be pointed at.
    ///
    /// By NAME out of the file index rather than by opening 794 packages: this is a picker's list,
    /// and the folder each one sits in is what tells a Coil Striker from a campaign walk-on. The
    /// row's own re-point is checked when it is built, which is what makes a cheap list here safe.
    /// </summary>
    public sealed record Character(string Name, string Folder, string Package)
    {
        /// <summary>"PoolCharacters/Clone", which is how a person tells 794 apart.</summary>
        public string Where => Folder;
    }

    public static List<Character> Definitions(IFileProvider provider)
    {
        var found = new List<Character>();
        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var leaf = key[(key.LastIndexOf('/') + 1)..^".uasset".Length];
            if (!leaf.StartsWith(CharacterPrefix, StringComparison.Ordinal)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pkg) continue;

            // Two folders up, which is where the family lives: .../PoolCharacters/Clone/CD_Char_Clone1
            // says "Clone" on its own and "PoolCharacters/Clone" usefully.
            var dir = pkg[..pkg.LastIndexOf('/')];
            var up = dir.LastIndexOf('/');
            var parent = up > 0 ? dir[..up] : dir;
            var where = up > 0
                ? $"{parent[(parent.LastIndexOf('/') + 1)..]}/{dir[(up + 1)..]}"
                : dir[(dir.LastIndexOf('/') + 1)..];
            found.Add(new Character(leaf, where, pkg));
        }
        return found.OrderBy(c => c.Folder, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    // ------------------------------------------------------------------ reading one character

    /// <summary>
    /// The three slots of a character definition worth showing: its name part, its species and its
    /// class.
    ///
    /// A CD_ carries about eighty-six slot overrides and only these three say who somebody IS at a
    /// glance. Read from the SlotOverrides map by tag, because the map's ORDER is the cook's and
    /// indexing it by position is the sort of thing that works until a patch.
    /// </summary>
    public static (string NamePart, string Species, string Class) SlotsOf(
        IFileProvider provider, string definition)
    {
        if (definition.Length == 0) return ("", "", "");
        try
        {
            var export = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(definition)).GetExports()
                        .FirstOrDefault(e => e.ExportType == "CustomizationDefinition"));
            if (export is null) return ("", "", "");
            if (PartDetails.Value(export.Properties, "SlotOverrides") is not UScriptMap map)
                return ("", "", "");

            string name = "", species = "", cls = "";
            foreach (var kv in map.Properties)
            {
                var tag = PartDetails.TagOf(kv.Key?.GenericValue);
                if (tag is null) continue;
                var value = Package(PartDetails.SoftPathOf(kv.Value?.GenericValue) ?? "");
                if (value.Length == 0) continue;

                if (tag.EndsWith(".Character.Info.Name", StringComparison.Ordinal)) name = value;
                else if (tag.EndsWith(".Character.Species", StringComparison.Ordinal)) species = value;
                else if (tag.EndsWith(".Character.Class", StringComparison.Ordinal)) cls = value;
            }
            return (name, species, cls);
        }
        catch { return ("", "", ""); }
    }

    /// <summary>
    /// A recruit's name and biography, off the CustomizationFragmentName its name part carries.
    ///
    /// FirstName and LastName are separate fields and most pool characters have both; the droids
    /// have only a first name. Joined here rather than in the caller so a blank last name does not
    /// produce a trailing space in every list that shows one.
    /// </summary>
    private static (string Name, string Story) TextOf(IFileProvider provider, string namePart)
    {
        var (first, last, story) = TextsOf(provider, namePart);
        return ((first + " " + last).Trim(), story);
    }

    /// <summary>
    /// The three texts a name part carries, kept apart: first name, last name, biography. Blank
    /// when the part has no CustomizationFragmentName or cannot be read.
    ///
    /// Public because duplicating one of the game's characters keeps their name, and a name is
    /// two fields on the copy -- "BX Droid Commando" is a first name with no last name.
    /// </summary>
    public static (string First, string Last, string Story) TextsOf(IFileProvider provider, string namePart)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(namePart)).GetExports().ToList());
            var fragment = exports.FirstOrDefault(e => e.ExportType == "CustomizationFragmentName");
            if (fragment is null) return ("", "", "");
            return (Text(fragment.Properties, "FirstName"), Text(fragment.Properties, "LastName"),
                    Text(fragment.Properties, "BackgroundDescription"));
        }
        catch { return ("", "", ""); }
    }

    private static string Text(List<FPropertyTag> props, string name) =>
        PartDetails.Value(props, name) switch
        {
            CUE4Parse.UE4.Objects.Core.i18N.FText t => t.Text ?? "",
            string s => s,
            _ => "",
        };

    /// <summary>"/A/B/C.C_C" -> "/A/B/C". A package path is what a re-point and a link both want.</summary>
    private static string Package(string objectPath)
    {
        if (objectPath.Length == 0) return "";
        var dot = objectPath.LastIndexOf('.');
        var slash = objectPath.LastIndexOf('/');
        return dot > slash ? objectPath[..dot] : objectPath;
    }
}

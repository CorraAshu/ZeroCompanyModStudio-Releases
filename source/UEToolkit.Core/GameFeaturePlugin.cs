using System.Text;
using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// Deliver a mod the way this game's own mod runtime expects one: as a Game Feature plugin.
///
/// WHY THIS EXISTS. Until now a mod shipped the game's whole merged asset registry with its own
/// entry added, at a path every mod writes. Load order picked a winner and the loser's parts were
/// simply absent -- and no care by either author could prevent it, because two people publishing
/// mods have never met. That is not a bug in a mod, it is the wrong delivery format.
///
/// The game already has the right one. `UBitReactorModRuntimeSubsystem::Initialize` tail-jumps
/// into `RefreshAvailableMods`, which scans `FPaths::ProjectModsDir()` for `*.pak`, mounts each
/// one, reads the `AssetRegistry.bin` beside the `.uplugin` INSIDE it, and hands the plugin to
/// `UGameFeaturesSubsystem::LoadGameFeaturePlugin`. Each plugin's registry is APPENDED. Two mods
/// that each add a part never touch each other, and a conflict exists only when two mods really do
/// replace the same asset -- which is the only time one should.
///
/// WHAT A PLUGIN NEEDS, and where each piece comes from:
///
///   &lt;Name&gt;.uplugin        written here; the game's own ZCTestMod is the shape
///   AssetRegistry.bin     RegistryDocument, seeded EMPTY rather than from the game's 26 MB one
///   &lt;Name&gt; data asset     a GameFeatureData, authored below from a minimal template
///   content               the mod's packages, in an IoStore pair as always
/// </summary>
public static class GameFeaturePlugin
{
    /// <summary>
    /// The index a package uses to name a script class.
    ///
    /// An authored package refers to its class by a HASH of the class path rather than by the
    /// path, and the loader resolves that hash through the global script object map. Computed the
    /// way UE computes it: dots and colons become slashes, everything lowercases, CityHash64 over
    /// the UTF-16 bytes, and the top two bits are cleared to leave room for the type tag.
    ///
    /// Checked rather than trusted: computing these found the real entries in this build's map,
    /// which is the only evidence that the function is right.
    /// </summary>
    public static ulong ScriptImport(string classPath)
    {
        var flat = new string(classPath.Select(c => c is '.' or ':' ? '/' : char.ToLowerInvariant(c)).ToArray());
        var hash = CUE4Parse.Utils.CityHash.CityHash64(Encoding.Unicode.GetBytes(flat)) & ~(3UL << 62);
        return hash | (1UL << 62);   // EType.ScriptImport
    }

    public const string DataClass = "/Script/GameFeatures.GameFeatureData";
    public const string DataClassDefault = "/Script/GameFeatures.Default__GameFeatureData";

    /// <summary>
    /// THE GAME'S OWN DATATABLE OVERLAY ACTION, taken out of the script object map rather than
    /// computed from a path.
    ///
    /// A script import is a hash of the class's full path, and the module a game class lives in
    /// is not written down anywhere we can read. It does not have to be: the global script object
    /// map is a table of those hashes to leaf names, so looking the LEAF up gives the exact index
    /// the loader will resolve -- no module name required, and no chance of computing a hash for
    /// a path that is almost right. Read with `--scriptobj DataTableOverlay`:
    ///
    ///     0x631DD60A6A4614AD  BitReactorGameFeatureAction_DataTableOverlay
    ///     0x4FD0C3362A4AE3EC  Default__BitReactorGameFeatureAction_DataTableOverlay
    ///     0x4678555944AB9BCF  BitReactorDataTableOverlay            (the struct)
    ///
    /// Kept as constants WITH the lookup that produced them, so a game patch that moves the class
    /// is caught by re-running that verb rather than by a mod that silently does nothing.
    /// </summary>
    public const ulong OverlayActionClass = 0x631DD60A6A4614ADUL;
    public const ulong OverlayActionDefault = 0x4FD0C3362A4AE3ECUL;

    /// <summary>
    /// THE GAME'S OWN FEATURE-DATA CLASS FOR MODS, which is not the engine's.
    ///
    /// `UBitReactorModGameFeatureData` overrides exactly one thing -- `GetPrimaryAssetId` -- and
    /// that is the whole point of it: a mod's feature data gets a primary asset TYPE of its own,
    /// so the asset manager and the mod runtime can tell a mod's feature from the game's. Read
    /// out of the PDB: the class has no other virtual of its own.
    ///
    /// Every mod this tool has shipped authored the ENGINE's `GameFeatureData`, which mounts and
    /// registers content perfectly well -- that is the pak and the registry doing the work -- but
    /// gives the mod runtime a feature whose primary asset id is the wrong type. Nothing we had
    /// ever shipped depended on the feature ACTIVATING, so nothing said so.
    /// </summary>
    public const ulong ModDataClass = 0x4233B9767692729AUL;
    public const ulong ModDataClassDefault = 0x4E39964B0AFD8AC2UL;

    /// <summary>The leaf class name a mod's feature data export carries.</summary>
    public const string ModDataExportClass = "BitReactorModGameFeatureData";

    /// <summary>The leaf name the action's export carries, which is how PropertyAdd finds it.</summary>
    public const string OverlayActionExportClass = "BitReactorGameFeatureAction_DataTableOverlay";

    /// <summary>The array on the action holding target/overlay pairs.</summary>
    public const string OverlaysProperty = "Overlays";

    /// <summary>The array on a GameFeatureData holding its actions.</summary>
    public const string ActionsProperty = "Actions";

    // ------------------------------------------------------------------ the descriptor

    /// <summary>
    /// The plugin's own gameplay tag files.
    ///
    /// Unreal adds a Game Feature plugin's config directory to the tags manager's search path when
    /// the feature activates, so a mod can register tags the game has never heard of. **Confirmed
    /// in game**: a weapon carrying a tag that appears in no game asset showed up correctly.
    ///
    /// BOTH files are written because both mechanisms exist and it is NOT known which one this
    /// build honours -- the first working mod shipped both, and dropping one has never been tested.
    /// A kilobyte is not worth the experiment until something needs it.
    /// </summary>
    public static string TagList(IEnumerable<string> tags) =>
        "[/Script/GameplayTags.GameplayTagsList]\r\n" +
        string.Concat(tags.Select(t =>
            $"+GameplayTagList=(Tag=\"{t}\",DevComment=\"added by this mod\")\r\n"));

    public static string TagSettings(IEnumerable<string> tags) =>
        "[/Script/GameplayTags.GameplayTagsSettings]\r\n" +
        string.Concat(tags.Select(t =>
            $"+GameplayTagList=(Tag=\"{t}\",DevComment=\"added by this mod\")\r\n"));

    /// <summary>
    /// The `.uplugin`, in the shape this game accepts.
    ///
    /// `ExplicitlyLoaded` and `BuiltInInitialFeatureState` are the two that matter and neither is
    /// a default: without the first the plugin's content mounts with the game rather than under
    /// the subsystem's control, and without the second it loads but never activates, which looks
    /// exactly like a mod that did not install.
    /// </summary>
    public static string Uplugin(string name, string friendly, string description, string author)
    {
        static string Q(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                                      .Replace("\r", " ").Replace("\n", " ");
        return $$"""
        {
            "FileVersion": 3,
            "Version": 1,
            "VersionName": "1.0",
            "FriendlyName": "{{Q(friendly.Length > 0 ? friendly : name)}}",
            "Description": "{{Q(description)}}",
            "Category": "Game Features",
            "CreatedBy": "{{Q(author)}}",
            "CanContainContent": true,
            "IsBetaVersion": false,
            "IsExperimentalVersion": false,
            "Installed": false,
            "ExplicitlyLoaded": true,
            "BuiltInInitialFeatureState": "Active",
            "Plugins": [
                { "Name": "GameFeatures", "Enabled": true },
                { "Name": "ModularGameplay", "Enabled": true }
            ]
        }
        """;
    }

    // ------------------------------------------------------------------ the data asset

    /// <summary>
    /// A GameFeatureData package, authored by retyping a minimal one.
    ///
    /// The game ships no GameFeatureData to copy, so this takes the smallest package it can find
    /// -- a few hundred bytes, one export, an input action or a sound curve -- and changes the
    /// two things that decide what an export IS: the class it names and the template it was made
    /// from. Its properties are then replaced with an empty block, because the old ones describe
    /// the old class and unversioned properties are matched BY POSITION against the class's
    /// schema. Left in place they would be read as whatever the new class happens to declare in
    /// those slots.
    ///
    /// An empty unversioned property block is one fragment marked last with no values, and then
    /// the four-byte false that says the object carries no guid. Six bytes.
    /// </summary>
    public static byte[]? AuthorData(IFileProvider provider, string templatePackage,
                                     string newPackageName, out string? why, Action<string>? log = null)
        => AuthorAs(provider, templatePackage, newPackageName, ModDataClass, ModDataClassDefault,
                    ModDataExportClass, out why, log);

    /// <summary>
    /// The same retype, for ANY class named by a script import index.
    ///
    /// <see cref="AuthorData"/> was this with the GameFeatureData's two hashes written in. Pulling
    /// them out is what lets a mod author a SECOND kind of object -- a game feature ACTION -- by
    /// exactly the route that has been shipping data assets for weeks, rather than by new
    /// machinery whose first failure would be in game.
    ///
    /// The indices are passed rather than paths because a game class's module is not readable and
    /// does not need to be: the global script object map holds the hashes already. See
    /// <see cref="OverlayActionClass"/>.
    /// </summary>
    public static byte[]? AuthorAs(IFileProvider provider, string templatePackage,
                                   string newPackageName, ulong classIndex, ulong templateIndex,
                                   string what, out string? why, Action<string>? log = null)
    {
        why = null;

        var source = AssetCopy.Read(provider, templatePackage, out why);
        if (source is null) return null;
        if (source.Pieces.Any(p => !p.IsPackage))
        { why = $"{templatePackage} has payload chunks; pick a template with none"; return null; }

        var raw = source.Pieces.First(p => p.IsPackage).Data;

        var headerSize = (int) BitConverter.ToUInt32(raw, 4);
        var exportMapOffset = BitConverter.ToInt32(raw, ExportMapOffsetField);
        var bundleEntriesOffset = BitConverter.ToInt32(raw, ExportBundleEntriesOffsetField);
        var exportCount = (bundleEntriesOffset - exportMapOffset) / ExportMapEntryBytes;
        if (exportCount != 1)
        { why = $"{templatePackage} has {exportCount} exports; this needs exactly one"; return null; }

        var serialOffset = (long) BitConverter.ToUInt64(raw, exportMapOffset);
        var serialSize = (long) BitConverter.ToUInt64(raw, exportMapOffset + 8);

        // The empty export, before anything else: splicing corrects the export map's sizes, and
        // doing it after the class had been patched would work too but leaves the package in a
        // state where the two disagree if the splice throws.
        var empty = new byte[6];
        BitConverter.GetBytes((ushort) 0x0100).CopyTo(empty, 0);   // one fragment, last, no values
        var spliced = ZenPackage.SpliceExportData(raw, headerSize + (int) serialOffset,
                                                  (int) serialSize, empty);

        // Class and template. TemplateIndex points at the class default object, and an export
        // whose class moved but whose template did not is constructed from the wrong archetype.
        BitConverter.GetBytes(classIndex).CopyTo(spliced, exportMapOffset + ClassIndexAt);
        BitConverter.GetBytes(templateIndex).CopyTo(spliced, exportMapOffset + TemplateIndexAt);

        var pkg = ZenPackage.Read(spliced);
        pkg.RenameNames(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [source.PackageName] = newPackageName,
            [Leaf(source.PackageName)] = Leaf(newPackageName),
        });

        var output = pkg.Write();
        log?.Invoke($"      {templatePackage} ({raw.Length} bytes) retyped as {what} " +
                    $"-> {newPackageName} ({output.Length} bytes)");
        return output;
    }

    /// <summary>
    /// A package small enough and plain enough to retype into a GameFeatureData.
    ///
    /// Searched rather than named, because a hard-coded asset path is a dependency on one game's
    /// content that breaks silently the first time a patch moves it. The requirements are what
    /// <see cref="AuthorData"/> actually needs: one export, no payload chunks, and small enough
    /// that nothing of the original survives that matters. Candidates are tried in size order and
    /// the first that authors cleanly wins, so the test IS the selection.
    /// </summary>
    public static string? FindTemplate(IFileProvider provider, Action<string>? log = null)
    {
        var candidates = provider.Files
            .Where(kv => kv.Key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Where(kv => kv.Value.Size is > 0 and < 1024)
            .OrderBy(kv => kv.Value.Size)
            .Take(40)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var path in candidates)
        {
            try
            {
                if (AssetLoader.LoadPrimary(provider, path)?.Owner?.Name is not { } name) continue;
                if (AuthorData(provider, name, "/Probe/Probe", out _) is null) continue;
                log?.Invoke($"      GameFeatureData template: {name}");
                return name;
            }
            catch (Exception) { /* the next candidate, not a failed build */ }
        }
        return null;
    }

    // ------------------------------------------------------------------ a feature AND its action

    /// <summary>
    /// One package holding TWO objects: a feature data and, as its SUBOBJECT, an action.
    ///
    /// WHY THIS IS NOT THE SAME AS TWO PACKAGES. `GameFeatureData.Actions` is an INSTANCED array,
    /// and an instanced reference is meant to name a subobject of the owner. A mod that ships its
    /// action as an asset of its own writes a perfectly valid CROSS-PACKAGE reference, and
    /// whether the loader honours one for an instanced property is not something the bytes can
    /// say -- the first attempt did exactly that, read back clean, installed, and changed nothing
    /// in game.
    ///
    /// So the shape is copied from the game rather than invented. An InputAction's `Triggers` is
    /// `TArray&lt;TObjectPtr&lt;UInputTrigger&gt;&gt;` marked Instanced -- the same UPROPERTY shape as
    /// `Actions` -- and `IA_UI_MoveUp` is 480 bytes holding exactly that: a root object and one
    /// subobject outered to it. **Its own Triggers value, read out of the package, is
    /// `01 00 00 00 02 00 00 00`** -- a count of one and then FPackageIndex 2, the second export,
    /// one-based. That is the reference to write, and it needs no import at all.
    /// </summary>
    public static byte[]? AuthorPair(IFileProvider provider, string templatePackage,
                                     string newPackageName, string subobjectStem,
                                     ulong class0, ulong default0, ulong class1, ulong default1,
                                     out string? why, Action<string>? log = null)
    {
        why = null;

        var source = AssetCopy.Read(provider, templatePackage, out why);
        if (source is null) return null;
        if (source.Pieces.Any(p => !p.IsPackage))
        { why = $"{templatePackage} has payload chunks; pick a template with none"; return null; }

        var raw = source.Pieces.First(p => p.IsPackage).Data;
        var exportMapOffset = BitConverter.ToInt32(raw, ExportMapOffsetField);
        var bundleEntriesOffset = BitConverter.ToInt32(raw, ExportBundleEntriesOffsetField);
        if ((bundleEntriesOffset - exportMapOffset) / ExportMapEntryBytes != 2)
        { why = $"{templatePackage} does not have exactly two exports"; return null; }

        // The SECOND export must be a subobject of the first, or this is two root objects that
        // happen to share a package and the instancing this exists for does not apply.
        var outer = BitConverter.ToUInt64(raw, exportMapOffset + ExportMapEntryBytes + OuterIndexAt);
        if (ZenPackage.KindOf(outer) != ZenPackage.ImportKind.Export)
        { why = $"{templatePackage}'s second export is not a subobject of its first"; return null; }

        // EMPTY BOTH PROPERTY BLOCKS, LOWEST EXPORT FIRST. Unversioned properties are matched by
        // POSITION against the class's schema, so the template's own values would be read as
        // whatever the new class declares in those slots. Each splice corrects the export map, so
        // the offsets are re-read between them rather than carried.
        var empty = new byte[6];
        BitConverter.GetBytes((ushort) 0x0100).CopyTo(empty, 0);   // one fragment, last, no values

        for (var i = 0; i < 2; i++)
        {
            var headerSize = (int) BitConverter.ToUInt32(raw, 4);
            var at = BitConverter.ToInt32(raw, ExportMapOffsetField) + i * ExportMapEntryBytes;
            var offset = (long) BitConverter.ToUInt64(raw, at);
            var size = (long) BitConverter.ToUInt64(raw, at + 8);
            raw = ZenPackage.SpliceExportData(raw, headerSize + (int) offset, (int) size, empty);
        }

        var mapAt = BitConverter.ToInt32(raw, ExportMapOffsetField);
        BitConverter.GetBytes(class0).CopyTo(raw, mapAt + ClassIndexAt);
        BitConverter.GetBytes(default0).CopyTo(raw, mapAt + TemplateIndexAt);
        BitConverter.GetBytes(class1).CopyTo(raw, mapAt + ExportMapEntryBytes + ClassIndexAt);
        BitConverter.GetBytes(default1).CopyTo(raw, mapAt + ExportMapEntryBytes + TemplateIndexAt);

        var pkg = ZenPackage.Read(raw);
        var subLeaf = pkg.ExportNames.Length > 1 ? pkg.ExportNames[1] : "";
        var subStem = ZenPackage.ParseNumber(subLeaf).Stem ?? subLeaf;

        var subs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [source.PackageName] = newPackageName,
            [Leaf(source.PackageName)] = Leaf(newPackageName),
        };
        if (subStem.Length > 0 && !subs.ContainsKey(subStem)) subs[subStem] = subobjectStem;
        pkg.RenameNames(subs);

        var output = pkg.Write();
        log?.Invoke($"      {templatePackage} ({source.Bytes} bytes) retyped as a feature AND its " +
                    $"action -> {newPackageName} ({output.Length} bytes)");
        return output;
    }

    /// <summary>
    /// A small package holding a root object and ONE subobject of it.
    ///
    /// Searched rather than named, for the reason <see cref="FindTemplate"/> gives: a hard-coded
    /// asset path is a dependency on one game's content that breaks silently when a patch moves
    /// it. The test IS the selection -- a candidate that does not author cleanly is skipped.
    /// </summary>
    public static string? FindPairTemplate(IFileProvider provider, Action<string>? log = null)
    {
        // COUNT THE EXPORTS BEFORE LOADING ANYTHING. The first version sorted by size and took
        // the first 400, which never reached the two-export candidates at all -- this game has
        // hundreds of single-export packages smaller than the 480-byte InputActions. Reading the
        // export map out of the raw header is a few arithmetic operations, so the expensive
        // LoadPrimary only runs on packages that are already the right shape.
        foreach (var path in provider.Files
                     .Where(kv => kv.Key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                     .Where(kv => kv.Value.Size is > 0 and < 2048)
                     .OrderBy(kv => kv.Value.Size)
                     .Select(kv => kv.Key)
                     .Take(6000))
        {
            try
            {
                byte[]? probe = null;
                if (!AssetLoader.Read(() => provider.TrySaveAsset(path, out probe)) || probe is null)
                    continue;
                var mapAt = BitConverter.ToInt32(probe, ExportMapOffsetField);
                var endAt = BitConverter.ToInt32(probe, ExportBundleEntriesOffsetField);
                if ((endAt - mapAt) / ExportMapEntryBytes != 2) continue;
                if (ZenPackage.KindOf(BitConverter.ToUInt64(
                        probe, mapAt + ExportMapEntryBytes + OuterIndexAt)) != ZenPackage.ImportKind.Export)
                    continue;

                if (AssetLoader.LoadPrimary(provider, path)?.Owner?.Name is not { } name) continue;
                if (AuthorPair(provider, name, "/Probe/Probe", "Probe",
                               ModDataClass, ModDataClassDefault,
                               OverlayActionClass, OverlayActionDefault, out _) is null) continue;
                log?.Invoke($"      feature+action template: {name}");
                return name;
            }
            catch (Exception) { /* the next candidate, not a failed build */ }
        }
        return null;
    }

    /// <summary>The FPackageIndex naming the second export: one-based, as UE writes it.</summary>
    public const int SubobjectIndex = 2;

    // FExportMapEntry: CookedSerialOffset(8) CookedSerialSize(8) ObjectName(8) OuterIndex(8)
    //                  ClassIndex(8) SuperIndex(8) TemplateIndex(8) PublicExportHash(8)
    //                  ObjectFlags(4) FilterFlags(1) pad(3)   = 72
    private const int OuterIndexAt = 8 + 8 + 8;
    private const int ClassIndexAt = 8 + 8 + 8 + 8;
    private const int TemplateIndexAt = ClassIndexAt + 8 + 8;

    private const int ExportMapOffsetField = 32;
    private const int ExportBundleEntriesOffsetField = 36;
    private const int ExportMapEntryBytes = 72;

    private static string Leaf(string packageName) => packageName[(packageName.LastIndexOf('/') + 1)..];
}

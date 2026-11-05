using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UEToolkit.Core;

/// <summary>
/// WHAT THE BLENDER ADD-ON ASKS THE GAME, in one place: two command-line verbs, `--catalog` and
/// `--import1`, answered by whichever executable the add-on was pointed at.
///
/// The add-on reads nothing itself (see BlenderAddon/zerocompany_importer/backend.py). It starts
/// this program once to list every importable mesh and once per import, and it reads exactly two
/// things back: the catalogue file, and the LAST line starting `OK: ` on stdout. That contract is
/// the reason this class exists as one piece of code with two callers - the Mod Studio, whose exe
/// is what the add-on is normally pointed at, and the multi-game toolkit's own command line - so
/// the two can never answer differently, and an add-on installed from either one finds the same
/// catalogue and the same files.
///
/// WHAT DIFFERS BETWEEN THE CALLERS IS ONLY HOW THE GAME GETS MOUNTED, which is why
/// <see cref="RunAsync"/> takes a delegate for it. Everything printed from here on is identical.
/// </summary>
public static class AddonBackend
{
    public const string CatalogFlag = "--catalog";
    public const string ImportFlag = "--import1";

    /// <summary>True when the command line is one of the add-on's two verbs.</summary>
    public static bool Wants(string[] args) => args.Contains(CatalogFlag) || args.Contains(ImportFlag);

    /// <summary>Why a mount produced nothing, so each verb can word it as it always has.</summary>
    public enum Failure
    {
        None,
        /// <summary>No profile of that name (the line the verbs have always printed).</summary>
        ProfileNotFound,
        /// <summary>A profile was found and the mount did not produce a provider.</summary>
        MountFailed,
        /// <summary>Anything else, with its own sentence in <see cref="MountResult.Message"/>.</summary>
        Other,
    }

    /// <summary>What a caller's mount delegate hands back: the game, or the reason there is none.</summary>
    public sealed record MountResult(IFileProvider? Provider, AssetIndex? Index,
                                     Failure Failure = Failure.None, string? Message = null)
    {
        public static MountResult Ok(IFileProvider provider, AssetIndex index) => new(provider, index);
        public static MountResult NoProfile() => new(null, null, Failure.ProfileNotFound);
        public static MountResult Failed() => new(null, null, Failure.MountFailed);
        public static MountResult Cannot(string message) => new(null, null, Failure.Other, message);
    }

    /// <summary>
    /// Answer the verb on the command line, or return null when it is not one of ours so the
    /// caller can try its next. Mounting happens through <paramref name="mount"/> and only after
    /// the arguments have been judged, so a malformed call costs no mount.
    ///
    /// `--import1` is tried before `--catalog`, as the toolkit's own dispatcher always did.
    /// </summary>
    public static async Task<int?> RunAsync(string[] args, Func<Task<MountResult>> mount)
    {
        if (args.Contains(ImportFlag)) return await ImportAsync(args, mount);
        if (args.Contains(CatalogFlag)) return await CatalogAsync(args, mount);
        return null;
    }

    // ------------------------------------------------------------------ --catalog

    /// <summary>One importable mesh: where the game files it and what it is called.</summary>
    public readonly record struct Row(string Category, string Collection, string Name, string Path);

    // --catalog [out.json]
    //
    // THE ASSET LIST A DCC PLUGIN BROWSES. Every importable mesh in the game, grouped the way the
    // game itself groups them, so a Blender or Max plugin can show a category list without asking the
    // game anything. Written once and cached: mounting is 1.6s, and nobody should pay that to scroll.
    //
    // Categories are DERIVED from the game's own folder tree rather than invented, because a taxonomy
    // somebody made up is a taxonomy that stops matching the game at the next patch.
    public static async Task<int> CatalogAsync(string[] args, Func<Task<MountResult>> mount)
    {
        var outPath = args.SkipWhile(a => a != CatalogFlag).Skip(1).FirstOrDefault(a => !a.StartsWith("--"));

        var game = await mount();
        if (game.Index is null || game.Provider is null) return Refuse(game, "");

        var rows = Rows(game.Index);

        Console.WriteLine();
        Console.WriteLine($"  {rows.Count:N0} importable meshes");
        Console.WriteLine();
        Console.WriteLine($"  {"category",-24} {"meshes",8}   biggest collections");
        foreach (var g in rows.GroupBy(r => r.Category).OrderByDescending(g => g.Count()))
        {
            var top = g.GroupBy(r => r.Collection).OrderByDescending(x => x.Count()).Take(4)
                       .Select(x => $"{x.Key}({x.Count()})");
            Console.WriteLine($"  {g.Key,-24} {g.Count(),8:N0}   {string.Join(", ", top)}");
        }

        if (outPath is not null)
        {
            File.WriteAllText(outPath, ToJson(rows));
            Console.WriteLine();
            Console.WriteLine($"  written to {outPath}  ({new FileInfo(outPath).Length / 1024:N0} KB)");
        }
        return 0;
    }

    /// <summary>Every static or skeletal mesh in the index, in the game's own category terms.</summary>
    public static List<Row> Rows(AssetIndex index)
    {
        var rows = new List<Row>();
        foreach (var f in index.Root.DescendantFiles())
        {
            if (!f.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var leaf = Path.GetFileNameWithoutExtension(f.Name);
            var skeletal = leaf.StartsWith("SK_", StringComparison.OrdinalIgnoreCase);
            var stat = leaf.StartsWith("SM_", StringComparison.OrdinalIgnoreCase);
            if (!skeletal && !stat) continue;

            // ".../Content/Game/<category>/<collection>/..." -- the two segments after Game are what
            // the game itself uses to separate a Character from a Prop, and a Droid from a Humanoid.
            var parts = f.FullPath.Split('/');
            var at = Array.FindIndex(parts, x => string.Equals(x, "Game", StringComparison.OrdinalIgnoreCase));
            if (at < 0 || at + 2 >= parts.Length) continue;
            var category = parts[at + 1];
            var collection = at + 2 < parts.Length - 1 ? parts[at + 2] : "";
            rows.Add(new Row(category, collection, leaf, f.FullPath));
        }
        return rows;
    }

    /// <summary>The catalogue file the add-on reads: a compact array, sorted by name.</summary>
    public static string ToJson(IEnumerable<Row> rows)
    {
        var doc = new JsonArray();
        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            doc.Add(new JsonObject
            {
                ["category"] = r.Category, ["collection"] = r.Collection,
                ["name"] = r.Name, ["path"] = r.Path,
            });
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    // ------------------------------------------------------------------ --import1

    // --import1 <exactCookedPath> <outDir> [gltf|psk]
    //
    // EXPORT ONE ASSET, FOR A DCC PLUGIN. Exact path in, one machine-readable line out, so a Blender
    // addon can spawn this per import and know what to open without parsing prose.
    //
    // Deliberately NOT substring matching like --exportmesh. A plugin passes a path it got from
    // --catalog, and a substring that quietly reaches a neighbour would import a different character
    // than the one somebody clicked.
    public static async Task<int> ImportAsync(string[] args, Func<Task<MountResult>> mount)
    {
        var given = args.SkipWhile(a => a != ImportFlag).Skip(1)
                        .TakeWhile(a => !a.StartsWith("--")).ToArray();
        if (given.Length < 2) { Console.WriteLine("usage: --import1 <exactCookedPath> <outDir> [gltf|psk]"); return 1; }
        var cookedPath = given[0];
        var outDir = given[1];
        var target = given.Length > 2 && given[2].Equals("psk", StringComparison.OrdinalIgnoreCase)
            ? ExportTarget.ActorX : ExportTarget.Gltf;

        var game = await mount();
        if (game.Provider is null) return Refuse(game, "ERROR ");

        return ImportOne(game.Provider, cookedPath, outDir, target);
    }

    /// <summary>Export one asset by its exact path. Prints the `OK: &lt;file&gt;` line on success.</summary>
    public static int ImportOne(IFileProvider provider, string cookedPath, string outDir, ExportTarget target)
    {
        Directory.CreateDirectory(outDir);

        // A path that is not in the game used to escape as an unhandled exception - a stack trace on
        // stderr and nothing the add-on could show. It is one of the two ways this verb fails on a
        // wrong argument, so it is answered the way the other is.
        UObject? loaded;
        try { loaded = AssetLoader.LoadPrimary(provider, cookedPath); }
        catch (Exception e) { Console.WriteLine($"ERROR could not load {cookedPath}: {e.Message}"); return 1; }
        if (loaded is null) { Console.WriteLine($"ERROR could not load {cookedPath}"); return 1; }

        var res = AssetExporter.Export(loaded, new ExportRequest(target, outDir));
        if (!res.Success || res.SavedPath is null)
        { Console.WriteLine($"ERROR export failed: {res.Error}"); return 1; }

        // ONE line a script can read, after everything else it printed. The plugin reads the last
        // OK: line rather than scraping the log.
        Console.WriteLine($"OK: {res.SavedPath}");
        return 0;
    }

    /// <summary>Print why there is no game, in the verb's own words, and the exit code that goes with it.</summary>
    private static int Refuse(MountResult game, string prefix)
    {
        Console.WriteLine(game.Failure switch
        {
            Failure.ProfileNotFound => prefix + "profile not found",
            Failure.Other => "ERROR " + game.Message,
            _ => prefix + "mount failed",
        });
        return 1;
    }
}

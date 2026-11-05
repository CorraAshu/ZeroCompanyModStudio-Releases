namespace UEToolkit.Core;

using CUE4Parse.FileProvider;

/// <summary>
/// Moving a HARD IMPORT from one package to another, for a copy or for an override.
///
/// This was the copy loop's own code and is lifted out because an OVERRIDE needs it too and could
/// not have it. The two halves of a hard reference are the reason the job is not one line:
///
///     the store entry   ImportedPackages[n] -- WHICH package this asset loads
///     the package       ImportedPublicExportHashes[h] -- which EXPORT inside it
///
/// A rename touches neither. Moving only the package leaves the hash describing an export the new
/// package may not have, which resolves to nothing and looks exactly like a mod that never
/// installed -- see <see cref="CopyContents.Reference.Hard"/>, which exists because a weapon's
/// firing sound was re-pointed, reported as changed, built, installed, and still played the
/// original.
///
/// WHY AN OVERRIDE NEEDS THIS AT ALL. The Armoury's stock is six `BrunoShopInventoryTable` assets
/// naming 99 items, each by a hard import (see <see cref="ShopTables"/>). Putting a mod's own item
/// on sale means taking over one of those slots, and the table being edited is the GAME's -- an
/// override. Until this existed, `repointImports` was on <see cref="ModCopy"/> alone, so a mod
/// could point its own assets anywhere and could not change what the shop offers. Copying the
/// table does not avoid it either: the root table names the three tiers by hard import too.
///
/// The hashes are matched BY NAME, with the old package's leaf swapped for the new one's:
/// `GE_PlagueStack_C` becomes `GE_Bind_Target_C`, `Default__GE_PlagueStack_C` becomes
/// `Default__GE_Bind_Target_C`, and a subobject whose name carries no leaf keeps its name.
///
/// NOT BY POSITION, which is what this did until 2026-09-22 on the theory that two packages cooked
/// from the same Blueprint parent put the same role at the same index. Two gameplay effects do
/// not: Force Grip's re-point of `GE_PlagueStack` to `GE_Bind_Target` sent the import that named
/// the effect CLASS to the fourth export of the new package, which is
/// `Default__GE_Bind_Target_C:AdditionalEffectsGameplayEffectComponent_0`. The build succeeded,
/// the verifier passed (the hash was a real export of the right package), and a ClassProperty
/// holding a component nulls itself on load -- so the ability would have fired and applied
/// nothing. `--props` on the built copy showed it. A name with no unique counterpart is reported
/// rather than guessed at.
/// </summary>
public static class ImportRepoint
{
    /// <summary>One package's exports as a redirect needs them: name, owner and public hash, in export-map order.</summary>
    public sealed record ExportTable(string[] Names, int[] Outers, ulong[] Hashes)
    {
        public static ExportTable Of(IFileProvider provider, string packageName)
        {
            var src = AssetCopy.PristineRead(provider, packageName, out _);
            var raw = src?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
            if (raw is null) return new([], [], []);
            try
            {
                var z = ZenPackage.Read(raw);
                return new(z.ExportNames, z.ExportOuters, z.PublicExportHashes);
            }
            catch { return new([], [], []); }
        }
    }

    /// <summary>
    /// What an export of one package becomes in another: <see cref="Name"/> is the export it now
    /// names, and <see cref="StandsInFor"/> is set when that is not its own counterpart but its
    /// OWNER's -- because the new package has nothing of that name.
    /// </summary>
    public sealed record Counterpart(int Index, string Name, string? StandsInFor);

    /// <summary>
    /// The export of <paramref name="to"/> that plays the role export <paramref name="at"/> of
    /// <paramref name="from"/> played: the same name with the package leaf swapped, and when there
    /// is none, the counterpart of its nearest OWNER.
    ///
    /// THE OWNER, because of what these imports are for. A package that names a gameplay effect's
    /// class also imports that effect's default COMPONENTS -- not to read them, which its bytecode
    /// never does, but so the loader brings the effect's defaults in first. Re-pointed at a
    /// different effect, those components have no counterpart (`GEC_PlagueEvent_C_0` exists in
    /// GE_PlagueStack and nowhere in GE_Bind_Target), and the default object that owns them is
    /// the load-order dependency they stood for. Left unmatched, the import resolves to nothing.
    ///
    /// Null when nothing up the chain has a counterpart, or when a name matches more than one
    /// export -- said, not guessed.
    /// </summary>
    public static Counterpart? CounterpartOf(ExportTable from, ExportTable to, int at,
                                             string fromLeaf, string toLeaf)
    {
        var usable = Math.Min(to.Names.Length, to.Hashes.Length);
        for (int walk = at, steps = 0;
             walk >= 0 && walk < from.Names.Length && steps < 16;
             walk = walk < from.Outers.Length ? from.Outers[walk] : -1, steps++)
        {
            var wanted = from.Names[walk].Replace(fromLeaf, toLeaf, StringComparison.OrdinalIgnoreCase);
            var hits = Enumerable.Range(0, usable)
                .Where(k => string.Equals(to.Names[k], wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count > 1) return null;
            if (hits.Count == 1 && to.Hashes[hits[0]] != 0)
                return new Counterpart(hits[0], to.Names[hits[0]], walk == at ? null : from.Names[walk]);
        }
        return null;
    }

    /// <summary>
    /// Rewrite the export hashes so they describe the TARGET's exports rather than the original's.
    ///
    /// Returns the package bytes, edited where a redirect applied and unchanged where none did. A
    /// redirect naming a package this asset does not import is silently no-op here and reported by
    /// <see cref="MovePackages"/>, which owns that check -- saying it twice for one mistake reads
    /// as two mistakes.
    /// </summary>
    /// <param name="resolve">
    /// Turns a redirect's TARGET into a package name. Copies write a mod-relative id here, which
    /// the caller expands; an override names a game package outright. Passing the caller's own
    /// resolver keeps that difference where it belongs.
    /// </param>
    /// <param name="hashesFrom">
    /// WHICH PACKAGE'S EXPORT HASHES DESCRIBE THE TARGET, when the target is one this build is
    /// still making and so cannot be read out of the mounted game.
    ///
    /// A copy keeps its template's export hashes, so the answer for a mod's own asset is its
    /// TEMPLATE. Without this the lookup returned an empty array and the whole redirect was
    /// skipped -- silently, so an override pointed at a mod's own item swapped the store entry,
    /// looked entirely correct, and still resolved the ORIGINAL's export. Caught by the verifier
    /// rather than by reading this, which is the only reason it is not shipped.
    ///
    /// Null means "the target names itself", which is right whenever the target already exists.
    /// </param>
    public static byte[] MoveHashes(IFileProvider provider, byte[] bytes,
                                    ContainerHeader.StoreEntry store,
                                    IEnumerable<KeyValuePair<string, string>> repoints,
                                    Func<string, string> resolve, string who,
                                    List<string> warnings, Action<string>? log,
                                    Func<string, string>? hashesFrom = null)
    {
        foreach (var (from, to) in repoints)
        {
            var target = resolve(to);
            if (string.Equals(from, target, StringComparison.OrdinalIgnoreCase)) continue;

            var source = hashesFrom?.Invoke(target) ?? target;

            // The NAMES and OWNERS beside the hashes, so a hash can be turned into the role it
            // plays and that role found in the other package. The leaf swapped in is the
            // SOURCE's -- the package the hashes are read from -- because a mod's own copy keeps
            // its TEMPLATE's export hashes, so the template's names are the ones those belong to.
            var fromTable = ExportTable.Of(provider, from);
            var toTable = ExportTable.Of(provider, source);

            // SAID OUT LOUD. This used to `continue` in silence, which is how a redirect that
            // moved no hash at all reached a built container looking finished.
            if (fromTable.Hashes.Length == 0 || toTable.Hashes.Length == 0)
            {
                var missing = fromTable.Hashes.Length == 0 ? Leaf(from) : Leaf(source);
                warnings.Add($"{who}: {Leaf(from)} -> {Leaf(target)} could not move its export " +
                             $"hashes, because {missing} publishes none this build can read. The " +
                             "redirect will point at the right package and look up the wrong " +
                             "export, which resolves to nothing.");
                continue;
            }

            var slot = store.ImportedPackages.IndexOf(PackageId.Of(from));
            if (slot < 0) continue;

            var pkg = ZenPackage.Read(bytes);
            var map = pkg.ImportMap;
            var hashes = pkg.ImportedPublicExportHashes;
            var moved = new List<string>();
            var owned = new List<string>();
            var unmatched = new List<string>();

            for (var i = 0; i < map.Length; i++)
            {
                if (ZenPackage.KindOf(map[i]) != ZenPackage.ImportKind.PackageImport) continue;
                var (pkgAt, hashAt) = ZenPackage.PackageImportOf(map[i]);
                if (pkgAt != slot || hashAt < 0 || hashAt >= hashes.Length) continue;

                var export = Array.IndexOf(fromTable.Hashes, hashes[hashAt]);
                if (export < 0 || export >= fromTable.Names.Length)
                { unmatched.Add($"0x{hashes[hashAt]:X16}"); continue; }

                var role = fromTable.Names[export];
                if (CounterpartOf(fromTable, toTable, export, Leaf(from), Leaf(source)) is not { } c)
                {
                    unmatched.Add($"{role} (nothing in {Leaf(source)} plays its part, nor its owner's)");
                    continue;
                }

                pkg.SetImportedPublicExportHash(hashAt, toTable.Hashes[c.Index]);
                moved.Add($"{role} -> {c.Name}");
                if (c.StandsInFor is { } owner) owned.Add($"{role} (owned by {owner}) now names {c.Name}");
            }

            if (moved.Count > 0)
            {
                bytes = pkg.Write();
                log?.Invoke($"      import {Leaf(from)} -> {Leaf(target)}: " +
                            $"{moved.Count} export hash(es) moved with it ({string.Join(", ", moved)})");
            }
            if (owned.Count > 0)
                warnings.Add($"{who}: {Leaf(target)} has no counterpart for {owned.Count} " +
                             $"subobject(s) {Leaf(from)} imported, so each names its owner instead -- " +
                             $"{string.Join("; ", owned)}. An imported subobject is usually there to " +
                             "make its owner load first rather than to be read; if this asset does " +
                             "read one as a value, it now reads the owner.");
            if (unmatched.Count > 0)
                warnings.Add($"{who}: pointing at {Leaf(target)} left {unmatched.Count} import(s) " +
                             $"describing an export {Leaf(from)} has and it does not " +
                             $"({string.Join(", ", unmatched)}). Those resolve to nothing -- the two " +
                             "assets are not the same shape, so copy the one you want instead of " +
                             "pointing at it.");
        }
        return bytes;
    }

    /// <summary>
    /// The store entry's imported packages with the redirects applied.
    ///
    /// A copy of the list, never the original: the caller's <c>src.Store</c> may be the pristine
    /// entry that other assets in the same build also read, and editing it in place would move
    /// their imports too.
    ///
    /// A redirect that matches nothing is REPORTED, both in the log and as a warning. It leaves
    /// the asset loading what it always loaded, which is the entire failure this exists to
    /// prevent, and it is invisible from outside.
    /// </summary>
    public static List<ulong> MovePackages(ContainerHeader.StoreEntry store,
                                           IEnumerable<KeyValuePair<string, string>> repoints,
                                           Func<string, string> resolve, string who,
                                           List<string> warnings, Action<string>? log)
    {
        var imports = store.ImportedPackages.ToList();
        foreach (var (from, to) in repoints)
        {
            var target = resolve(to);
            var fromId = PackageId.Of(from);
            var toId = PackageId.Of(target);
            var hit = false;
            for (var i = 0; i < imports.Count; i++)
                if (imports[i] == fromId) { imports[i] = toId; hit = true; }

            log?.Invoke(hit
                ? $"      import {Leaf(from)} -> {Leaf(target)}"
                : $"      import {Leaf(from)} NOT FOUND among {imports.Count} import(s)");
            if (!hit)
                warnings.Add($"{who}: it does not import {Leaf(from)}, so that redirect " +
                             "changed nothing");
        }
        return imports;
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
}

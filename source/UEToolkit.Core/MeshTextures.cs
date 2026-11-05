using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;

namespace UEToolkit.Core;

/// <summary>
/// Every texture a set of meshes actually references, by package path.
///
/// Exists because a filtered export writes meshes whose textures were never queued. The full
/// `--exportall` run is built on a sound trade: meshes export with `ExportMaterials = false`,
/// because CUE4Parse re-decodes a shared atlas once per mesh that uses it — measured at 7.2 GB
/// for 250 meshes — and a separate pass covers every texture in the game exactly once instead.
///
/// `--filter` narrows BOTH queues by the same path substring, and that is where the trade breaks:
/// the assumption "the texture pass covers everything" stops being true, silently. One
/// game made it obvious — a hero's meshes sat under one character folder and his textures in a
/// sibling folder with a different name, so a filter on the mesh folder exported five meshes and
/// six textures, not one of which any of those meshes uses. The result imports as untextured grey and
/// nothing in the run says otherwise.
///
/// So: follow the materials rather than the path. A character's textures live wherever the artist
/// put them, which is not reliably beside the mesh in any game.
/// </summary>
public static class MeshTextures
{
    /// <summary>
    /// The index paths of every texture referenced by these meshes, restricted to what the
    /// provider actually has.
    ///
    /// **Call this before the parallel export starts, not inside it.** Resolving a material lazily
    /// loads its texture packages, and CUE4Parse's package cache is not safe to race on — doing it
    /// under the export's parallelism killed a 74-minute run with an `AccessViolationException`,
    /// which .NET Core does not permit catching. This runs single-threaded for that reason; see
    /// <see cref="PskUv"/>, which serialises the same walk behind a lock.
    /// </summary>
    public static List<string> Referenced(
        IFileProvider provider, IEnumerable<string> meshPaths, Action<string>? log = null)
    {
        // Case-insensitive, because a path resolved out of a material is spelled however the
        // material spells it and the provider keys are whatever the pak carries.
        var have = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Keyed by the part after "/Content/", for plugin mounts FixPath cannot resolve. Ambiguous
        // keys are dropped rather than guessed: a suffix that two packages share identifies
        // neither, and picking one would be a silent wrong texture, which is worse than a miss.
        var byContentRel = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in provider.Files.Keys)
        {
            have[k] = k;
            var i = k.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var rel = k[(i + 9)..];
            byContentRel[rel] = byContentRel.ContainsKey(rel) ? null : k;
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Distinct, like `found`. Counting every occurrence instead put "45 found, 958 missing"
        // in the log for 22 meshes, which reads as a catastrophic miss rate and is really a
        // handful of shared library textures referenced by every material in the character.
        var missed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int meshes = 0, noMaterials = 0, threw = 0;

        foreach (var meshPath in meshPaths)
        {
            // Under AssetLoader.Gate, and it was not until 2026-08-09.
            //
            // This walk is the same operation PskUv serialises with a comment saying the cache "is
            // not safe to race on", and it ran here with no lock — inside `--exportall`, which is
            // also what `--regress` shells out to for every game-check. `--exportall` did not set
            // SkipLibraries either, so a background character/animation library was loading
            // packages on another thread while this resolved materials. That is the export
            // non-determinism's hole: the bad resolve gets CACHED, so the later export loop reads
            // nothing for a material that plainly exists and writes the mesh with raw UVs.
            //
            // The scope spans the load AND the walk. Gating them separately would leave the same
            // race in a narrower window, which is exactly what two locks over one cache achieved.
            lock (AssetLoader.Gate)
            {
            UObject? obj;
            try { obj = AssetLoader.LoadPrimary(provider, meshPath); }
            catch { threw++; continue; }

            var mats = AssetLoader.MaterialsOf(obj).Where(m => m is not null).ToList();
            if (mats.Count == 0) { noMaterials++; continue; }
            meshes++;

            foreach (var mat in mats)
            {
                // AllLayers, not FirstLayer. The atlas walk in PskUv wants the one texture the
                // shader samples for colour; this wants everything the material binds — normal,
                // roughness, mask, the lot — because that is what a DCC needs to rebuild it.
                var p = new CMaterialParams2();
                try { mat!.GetParams(p, EMaterialFormat.AllLayers); }
                catch { threw++; continue; }

                foreach (var t in p.Textures.Values)
                {
                    if (t is not UObject u) continue;
                    if (PackagePath(u) is not { Length: > 0 } pkg) continue;
                    if (Lookup(provider, have, byContentRel, pkg) is { } hit) found.Add(hit);
                    else missed.Add(pkg);
                }
            }
            }
        }

        log?.Invoke($"materials: {meshes:N0} mesh(es) reference {found.Count:N0} texture(s)" +
                    (noMaterials > 0 ? $", {noMaterials:N0} with no materials" : "") +
                    (missed.Count > 0 ? $", {missed.Count:N0} not found in the game" : "") +
                    (threw > 0 ? $", {threw:N0} failed to resolve" : ""));
        // Name a few. A count alone cannot distinguish "engine defaults, as expected" from "the
        // path form is wrong for this game", and those need opposite responses -- the second is
        // what FixPath was added for, after it silently produced zero textures on one game.
        foreach (var m in missed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(3))
            log?.Invoke($"  not found: {m}");
        return found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A package path as a provider key, or null if the game does not have it.
    ///
    /// <c>FixPath</c> does the work, and it is not optional: a package resolved out of a material
    /// names itself in UE's virtual form (`/Game/Characters/...`) while the provider is keyed by
    /// the mounted form (`<Project>/Content/Characters/...`). Appending ".uasset" to the virtual
    /// path matches nothing. It went unnoticed for exactly as long as it was only tried on
    /// games whose packages happen to report the mounted form already — on another the same
    /// code found **0 textures and missed 1,535 references**, and the only reason that was visible
    /// is that the miss is counted and printed rather than skipped.
    ///
    /// The raw form is still tried, because FixPath rewrites nothing when the path is already a
    /// project path and there is no reason to depend on that staying true.
    /// </summary>
    private static string? Lookup(IFileProvider provider, Dictionary<string, string> have,
                                  Dictionary<string, string?> byContentRel, string pkg)
    {
        try
        {
            if (have.TryGetValue(provider.FixPath(pkg), out var fixedHit)) return fixedHit;
        }
        catch { /* FixPath indexes the first character; a degenerate path is just a miss */ }
        if (have.TryGetValue(pkg + ".uasset", out var rawHit)) return rawHit;

        // Plugin content roots. FixPath resolves /Game/ and /Engine/ and whatever mount points the
        // provider registered, and a shared material library can be none of those: its materials name
        // textures as `/SharedLibrary/CVW/Textures/...` while the files are mounted at
        // `Engine/Plugins/SharedLibrary/Content/CVW/Textures/...`. Dropping the leading root
        // leaves the content-relative path, which is the part both forms agree on -- 41 of one
        // character's 86 distinct texture references are in such a library, so this is not a long tail.
        var trimmed = pkg.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        if (slash <= 0) return null;
        return byContentRel.TryGetValue(trimmed[(slash + 1)..] + ".uasset", out var relHit)
            ? relHit   // null when the suffix was ambiguous, which is a deliberate miss
            : null;
    }

    /// <summary>
    /// The package a loaded object came from, without its extension.
    ///
    /// `GetPathName()` is `Package.ObjectName` — the object is named twice for a texture, once as
    /// the package and once as the export. Trimming at the LAST dot is what turns
    /// `.../Some_Texture.Some_Texture` back into a file we can look up. Prefer the owning
    /// package's own name where it is available, which needs no string surgery at all.
    /// </summary>
    private static string? PackagePath(UObject o)
    {
        if (o.Owner?.Name is { Length: > 0 } owner) return owner;
        var p = o.GetPathName();
        var dot = p.LastIndexOf('.');
        return dot > 0 ? p[..dot] : p;
    }

    // MaterialsOf was here, byte-identical to PskUv's copy and with no lock. See
    // AssetLoader.MaterialsOf for what that cost.
}

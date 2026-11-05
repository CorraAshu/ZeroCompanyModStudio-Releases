using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;

namespace UEToolkit.Core;

/// <summary>
/// Every atlas texture in the game and the tile grid it is laid out on, settled once.
///
/// **A grid cannot be derived from one mesh, and the viewport only ever has one.**
/// A body mesh that spans `U 1.005..1.946` is one tile wide. Every square-tile grid of a
/// 6144x2048 texture contains that, so the shader concludes 1x1, skips the remap and samples column
/// 1 of 6 instead of column 2. The exporter gets it right only because it pools across the 39 slots
/// sharing that atlas texture, where the extent reaches `U 3.995` and only 6x2 fits.
///
/// So the viewport was not missing logic, it was missing DATA. This builds that data once per game
/// and caches it, the way the character and animation libraries already do, and both the shader and
/// the exporter read the same answer. It also retires "the grid was decided per mesh" as a class of
/// bug rather than case by case — that one has been found and re-found three times.
///
/// Verified against the texture itself: one body texture is visibly six columns by two rows (four
/// grey armour columns, a red region spanning the last two), which is what the pooled derivation
/// says and what a single part cannot possibly reveal.
/// </summary>
public sealed class AtlasLibrary
{
    /// <summary>Texture object path to its grid.</summary>
    public Dictionary<string, GridEntry> Grids { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Textures the scan pooled and judged NOT to be atlases.
    ///
    /// This is not the same as being absent, and the difference is the whole reason the table is
    /// worth having. "Pooled and flat" is a conclusion drawn from every mesh in the game and it
    /// should override whatever one mesh implies — the case where one backpack
    /// on a square texture reads as a 6x6 atlas that does not exist. "Absent" means the scan never
    /// reached this texture, and there the per-mesh derivation is still the best answer available.
    /// </summary>
    public HashSet<string> Flat { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed record GridEntry(float Cols, float Rows, float VMinTile);

    public int MeshesScanned { get; set; }
    public int SlotsRecorded { get; set; }

    /// <summary>
    /// Where this table came from, because two tables with the same shape are not the same claim.
    ///
    /// A whole-game scan has seen every mesh, so "not an atlas" from it is a real conclusion. A
    /// table pooled from ONE export has only seen what that export contained, and a texture shared
    /// with a mesh outside it may have a wider extent than anything here shows. Both are useful and
    /// they are not interchangeable, so the file says which it is rather than leaving it to be
    /// inferred from a count.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// The table the viewport reads. Null until a game is loaded and a cache is found, and a null
    /// here MUST mean "derive the grid the old way" rather than "no atlas" — a game whose library
    /// has never been built would otherwise lose the atlas handling it has today.
    /// </summary>
    public static AtlasLibrary? Active { get; set; }

    public bool TryGet(string? texturePath, out GridEntry entry)
    {
        entry = null!;
        return texturePath is { Length: > 0 } && Grids.TryGetValue(texturePath, out entry!);
    }

    /// <summary>What this table has to say about a texture, as three distinct answers.</summary>
    public enum Verdict { Unknown, Atlas, Flat }

    public Verdict Lookup(string? texturePath, out GridEntry entry)
    {
        entry = null!;
        if (texturePath is not { Length: > 0 }) return Verdict.Unknown;
        if (Grids.TryGetValue(texturePath, out entry!)) return Verdict.Atlas;
        return Flat.Contains(texturePath) ? Verdict.Flat : Verdict.Unknown;
    }

    /// <summary>
    /// Scan meshes, pool their UV extents per texture, and settle a grid for each.
    ///
    /// Converting a mesh is the whole cost here, which is why the result is cached rather than
    /// rebuilt. It reuses <see cref="AtlasGrids"/> — the exporter's own pooling — instead of a
    /// second implementation, because two derivations of the same grid is exactly how the export
    /// and the viewport came to disagree in the first place.
    /// </summary>
    /// <param name="limit">
    /// Scan only the first N meshes. For MEASURING the scan, not for building a real table — a
    /// partial pool understates every extent, so a truncated table is not a smaller right answer,
    /// it is a wrong one. It is recorded in <see cref="Source"/> so it cannot be mistaken later.
    /// </param>
    /// <param name="skip">
    /// Start at mesh N. Also measurement only — paired with <paramref name="limit"/> it bisects a
    /// scan, so the expensive region can be examined without re-walking the cheap thousands in
    /// front of it every attempt.
    /// </param>
    public static AtlasLibrary Build(IFileProvider provider, AssetIndex index,
                                     Action<string>? log = null, Action<int, int>? progress = null,
                                     int limit = 0, int skip = 0, string? poolReport = null)
    {
        var lib = new AtlasLibrary();
        var grids = new AtlasGrids();

        var meshes = index.Root.DescendantFiles()
            .Where(f => f.Kind is AssetKind.SkeletalMesh or AssetKind.StaticMesh)
            .Select(f => f.FullPath)
            .ToList();
        var total = meshes.Count;
        if (skip > 0) meshes = meshes.Skip(skip).ToList();
        if (limit > 0 && limit < meshes.Count) meshes = meshes.Take(limit).ToList();

        log?.Invoke($"atlas library: scanning {meshes.Count:N0} mesh(es)" +
                    (meshes.Count < total
                        ? $" of {total:N0} (PARTIAL from {skip:N0} - measurement only)"
                        : ""));


        // Where the time actually goes, split by phase. This scan ran for over two hours with no
        // way to tell whether that was material resolution, Nanite decode or I/O -- and the last
        // time throughput was asserted here without measurement, the assertion was wrong. Cheap
        // enough to leave in: two stopwatches over an operation costing ~100 ms.
        var swLoad = new System.Diagnostics.Stopwatch();
        var swMaterials = new System.Diagnostics.Stopwatch();
        var swConvert = new System.Diagnostics.Stopwatch();
        var started = System.Diagnostics.Stopwatch.StartNew();

        // A watchdog, because every instrument here so far reported only on COMPLETION.
        //
        // Per-mesh timing logs after the mesh finishes, so a mesh that never finishes trips
        // nothing: the scan sat 47 minutes inside one call having emitted no progress line (the
        // loop had not advanced) and no SLOW line (the mesh had not returned). Pegged CPU, silent
        // log, and I read the silence as "no pathological mesh" — the second time in one night that
        // an instrument's blind spot got taken for evidence.
        //
        // This one reports the mesh currently in hand and how long it has been there, so a hang
        // names itself while it is still hanging.
        var current = "";
        var currentSince = started.Elapsed;
        using var watchdog = new Timer(_ =>
        {
            var held = started.Elapsed - currentSince;
            if (held.TotalSeconds >= 30 && current.Length > 0)
                log?.Invoke($"atlas library: STILL BUSY {held.TotalSeconds:0}s on {current}");
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        // CUE4Parse forces a full GC after every Nanite conversion, which is right for FModel and
        // ruinous here: the collection is per mesh and costs whatever the whole live heap costs, so
        // the scan degrades continuously the longer it runs. See
        // MeshConverter.AggressiveGarbageCollection for the measurements.
        var gcBefore = CUE4Parse_Conversion.Meshes.MeshConverter.AggressiveGarbageCollection;
        CUE4Parse_Conversion.Meshes.MeshConverter.AggressiveGarbageCollection = false;

        int unmatched = 0, skipped = 0;
        try
        {
        for (var i = 0; i < meshes.Count; i++)
        {
            current = meshes[i];
            currentSince = started.Elapsed;
            if ((i & 255) == 0) progress?.Invoke(i, meshes.Count);
            try
            {
                // Every phase timed per mesh, not just conversion.
                //
                // Timing only conversion is how a scan sat for 46 minutes past mesh 2,048 without
                // tripping a single "SLOW" line: the check could not see the phase that was
                // actually slow. An instrument that covers part of the work reports silence for
                // the rest, and silence reads as "nothing to see".
                var t0 = started.Elapsed;
                swLoad.Start();
                var obj = AssetLoader.LoadPrimary(provider, meshes[i]);
                swLoad.Stop();
                var tLoad = started.Elapsed - t0;
                if (obj is null) continue;

                // Materials FIRST, geometry only if they could matter. A mesh whose materials name
                // no texture cannot vote on any grid, so converting it is pure cost — and
                // conversion is essentially the entire cost of this scan, doubled on Nanite meshes
                // because the proxy has to be converted before it can be rejected. Skipping is
                // exactly equivalent, not an approximation: with no texture there is nothing for a
                // slot to pool onto.
                var t1 = started.Elapsed;
                swMaterials.Start();
                var mats = PskUv.AtlasTextures(obj);
                swMaterials.Stop();
                var tMat = started.Elapsed - t1;
                if (mats.Count == 0)
                {
                    skipped++;
                    Slow(log, meshes[i], tLoad, tMat, TimeSpan.Zero);
                    continue;
                }

                var t2 = started.Elapsed;
                swConvert.Start();
                var sections = SectionUvs(obj);
                swConvert.Stop();
                Slow(log, meshes[i], tLoad, tMat, started.Elapsed - t2);
                if (sections.Count == 0) continue;

                grids.Record(meshes[i], mats, sections);
                lib.MeshesScanned++;
                lib.SlotsRecorded += sections.Count;
                // A slot whose material name is not in the map contributes NOTHING to any grid,
                // and did so silently. That is how a character was lost: every one of its parts
                // converted to a proxy whose section carried the base material's name while the
                // mesh's material list carried its armour variant, so 39 slots voted on nothing and
                // the atlas they all share was concluded not to exist. The count is logged now
                // because a wrong table and a right one look identical from the outside.
                foreach (var s in sections)
                    if (s.Material.Length > 0 && !mats.ContainsKey(s.Material)) unmatched++;
            }
            catch { /* one unreadable mesh is not worth failing a game-wide scan over */ }
        }
        }
        finally { CUE4Parse_Conversion.Meshes.MeshConverter.AggressiveGarbageCollection = gcBefore; }

        var other = started.Elapsed - swLoad.Elapsed - swMaterials.Elapsed - swConvert.Elapsed;
        log?.Invoke($"atlas library: {started.Elapsed.TotalMinutes:0.#} min total - " +
                    $"load {swLoad.Elapsed.TotalMinutes:0.#}, " +
                    $"materials {swMaterials.Elapsed.TotalMinutes:0.#}, " +
                    $"convert {swConvert.Elapsed.TotalMinutes:0.#}, " +
                    $"other {other.TotalMinutes:0.#}");
        if (skipped > 0)
            log?.Invoke($"atlas library: {skipped:N0} mesh(es) name no texture at all - not converted");
        if (unmatched > 0)
            log?.Invoke($"atlas library: {unmatched:N0} slot(s) name a material the mesh does not " +
                        "list - those vote on no texture at all");

        grids.ResolveGrids(log);
        foreach (var (path, g) in grids.Grids)
            lib.Grids[path] = new GridEntry(g.Cols, g.Rows, g.VMinTile);
        foreach (var path in grids.Pooled)
            if (!lib.Grids.ContainsKey(path)) lib.Flat.Add(path);

        // Write the pooled EVIDENCE, not just the verdict.
        //
        // A grid is a conclusion drawn from every slot that shares a texture, and the table records
        // only the conclusion. When the whole-game scan and the export disagreed — 9x1 against 18x2
        // on one body texture, regressing 24 meshes — there was no way to ask WHICH slot
        // widened the pool, and a 58-minute rescan is too expensive to spend on a guess. One slot
        // reaching a hair past a tile edge halves the tile for every mesh sharing the texture, so
        // that question will be asked again.
        try
        {
            var evidence = poolReport ?? Path.Combine(Path.GetTempPath(), "atlas-pool.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            File.WriteAllLines(evidence, grids.Lines());
            log?.Invoke($"atlas library: pooled evidence written to {evidence}");
        }
        catch (Exception e) { log?.Invoke($"atlas library: could not write pool report - {e.Message}"); }

        lib.Source = $"scan:{lib.MeshesScanned} of {meshes.Count} mesh(es)";
        log?.Invoke($"atlas library: {lib.Grids.Count:N0} atlas texture(s) and {lib.Flat.Count:N0} " +
                    $"flat, from {lib.SlotsRecorded:N0} slot(s) across {lib.MeshesScanned:N0} mesh(es)");
        return lib;
    }

    /// <summary>
    /// Per-section UV extents straight off the converted mesh.
    ///
    /// The exporter reads these back out of the file it just wrote; here there is no file, so they
    /// come from the same converted LOD the viewport would render. Reading the FIRST usable LOD
    /// matters — a skipped LOD has no vertices and would contribute an empty extent that votes for
    /// a smaller grid than the truth.
    /// </summary>
    public static List<PskUv.SectionUv> SectionUvs(UObject obj)
    {
        // Under the gate for the same reason the exporter converts under it: this runs on a
        // background thread while the viewport may be converting a mesh of its own, and racing
        // CUE4Parse's caches fails as an uncatchable native AccessViolationException.
        lock (AssetLoader.Gate) return SectionUvsCore(obj);
    }

    private static List<PskUv.SectionUv> SectionUvsCore(UObject obj)
    {
        if (obj is USkeletalMesh sk && sk.TryConvert(out var cSkel) && cSkel.LODs.Count > 0)
        {
            var lod = cSkel.LODs.FirstOrDefault(l => !l.SkipLod) ?? cSkel.LODs[0];
            return lod.Verts is null || lod.Indices is null || lod.Sections is null
                ? []
                : Extents(lod.Verts, lod.Indices.Value, lod.Sections.Value);
        }
        // Convert the way the EXPORTER does, which is not the way the viewport does.
        //
        // This table exists so the shader and the written file agree, and the export records its
        // extents by reading back the PSK it wrote — a file produced with
        // `ENaniteMeshFormat.OnlyNaniteLOD`. `MeshConvert.Static` is the VIEWPORT's rule: prefer
        // Nanite only when it is more than twice the best normal LOD, a threshold that exists to
        // avoid paying for a conversion. Measuring through it was measuring different geometry.
        //
        // One helmet part is the case: Nanite 7,526 verts against a normal
        // LOD of 4,027 — 1.87x, just under the bar — so the scan took the normal LOD and read
        // `V -0.131..1.018` where the exported Nanite geometry reads `0.007..0.989`. One mesh
        // outside 0..1 forces a second row, `Fit` halves the tile, and the body texture
        // becomes 18x2 instead of 9x1, putting 24 parts at half scale.
        //
        // Back through MeshConvert, which now prefers Nanite whenever it is bigger rather than only
        // when it is twice as big. That was the difference this scan had to work around locally, and
        // a local workaround is a second rule waiting to drift — the whole reason the two disagreed
        // in the first place. One rule, three consumers: viewport, scan, export.
        if (obj is UStaticMesh sm)
        {
            var cStat = MeshConvert.Static(sm, out _);
            if (cStat is null || cStat.LODs.Count == 0) return [];

            var lod = cStat.LODs.FirstOrDefault(l => !l.SkipLod) ?? cStat.LODs[0];
            return lod.Verts is null || lod.Indices is null || lod.Sections is null
                ? []
                : Extents(lod.Verts, lod.Indices.Value, lod.Sections.Value);
        }
        return [];
    }

    /// <summary>
    /// The same table, pooled from an export's `atlas-slots.tsv` instead of by scanning the game.
    ///
    /// That file already IS the record: one row per material slot of every mesh the export wrote,
    /// with the texture it uses and that slot's UV extent as authored. Pooling it is the identical
    /// operation <see cref="Build"/> performs, on data that has already been paid for — seconds
    /// instead of hours, because the expensive part of a scan is converting geometry and the export
    /// converted it once already.
    ///
    /// **Narrower than a scan, and knowingly so.** It has only seen the meshes that were exported,
    /// so its "flat" verdicts are weaker: a texture shared with a mesh outside the export could have
    /// a wider extent than any row here shows. That is why <see cref="Source"/> is recorded — and
    /// why the viewport falls back to per-mesh derivation for a texture the table has never seen,
    /// rather than assuming absence means flatness.
    /// </summary>
    public static AtlasLibrary FromSlots(string slotsTsv, Action<string>? log = null)
    {
        var grids = new AtlasGrids();
        var rows = grids.Load(slotsTsv);
        if (rows == 0)
        {
            log?.Invoke($"atlas library: no usable rows in {slotsTsv}");
            return new AtlasLibrary { Source = $"slots:{slotsTsv} (empty)" };
        }

        grids.ResolveGrids(log);
        var lib = new AtlasLibrary
        {
            SlotsRecorded = rows,
            MeshesScanned = grids.Files,
            Source = $"slots:{slotsTsv}",
        };
        foreach (var (path, g) in grids.Grids) lib.Grids[path] = new GridEntry(g.Cols, g.Rows, g.VMinTile);
        foreach (var path in grids.Pooled) if (!lib.Grids.ContainsKey(path)) lib.Flat.Add(path);

        log?.Invoke($"atlas library: {lib.Grids.Count:N0} atlas texture(s) and {lib.Flat.Count:N0} flat, " +
                    $"pooled from {rows:N0} recorded slot(s) across {lib.MeshesScanned:N0} exported mesh(es)");
        return lib;
    }

    // ---- disk cache -------------------------------------------------------------------------
    //
    // Building this costs 35 minutes on a large game (12,408 meshes, converted). That single number drives
    // every decision below, and makes it a different kind of cache from LibraryCache.

    /// <summary>
    /// Bump when the DERIVATION changes — <see cref="UvAtlas.Grid"/>, <see cref="UvAtlas.Fit"/>,
    /// <see cref="AtlasGrids.ResolvePool"/> or the pooling key.
    ///
    /// Deliberately hand-maintained where <see cref="LibraryCache"/> uses the assembly's
    /// ModuleVersionId. That is right for a two-minute rebuild and wrong here: MVID changes on
    /// every compile, so a build id would throw away 35 minutes of work every time any file in
    /// the solution was edited, and nobody would keep the cache. The cost of getting this wrong is
    /// a stale table, so the rule is narrow and stated: touch the grid maths, bump this.
    /// </summary>
    private const int SchemaVersion = 1;

    private sealed record CachePayload(string Signature, int Version, int MeshesScanned,
                                       int SlotsRecorded, Dictionary<string, GridEntry> Grids,
                                       List<string> Flat, string Source);

    /// <summary>
    /// Identity of the answer, not just of the game. The atlas settings are in here because they
    /// are inputs to every grid — flipping <c>Enabled</c> or changing <c>VTop</c> in the profile
    /// changes what this table should say, and a cache keyed only on the game would keep serving
    /// grids derived under the old ones.
    /// </summary>
    public static string Signature(GameProfile profile, int fileCount)
    {
        var a = profile.Atlas;
        return $"{profile.Name}|{profile.PaksDirectory}|{profile.EngineVersion}|{fileCount}|" +
               $"{a.Enabled}|{a.TileSize}|{a.VTop}|{a.Slack}|v{SchemaVersion}";
    }

    /// <summary>
    /// Beside `profiles/`, NOT under the bin folder, for the same reason <see cref="AppSettings"/>
    /// is: <c>AppContext.BaseDirectory</c> would give Debug and Release separate caches, and here
    /// that means running one configuration costs 35 minutes to learn what the other already knows.
    /// </summary>
    /// <summary>
    /// WHERE A NEW TABLE IS WRITTEN: the OUTERMOST candidate, which is the shared one.
    ///
    /// A shipped build has exactly one, so this is the same answer. A source tree has two or
    /// more -- a project's own bin/ copy and the repository root -- and only the root is an
    /// ancestor of every OTHER binary here. Writing to the nearest put an 18-minute whole-game
    /// scan inside UEToolkit.App's output, where the Level Editor, the thing that needed it,
    /// could never look: its own bin/ has no `profiles` copy, so its walk goes to the root.
    /// </summary>
    public static string? CachePath(GameProfile profile, bool createDir = false) =>
        CachePaths(profile, createDir).LastOrDefault();

    /// <summary>
    /// EVERY PLACE THIS CACHE COULD LIVE, nearest first.
    ///
    /// It used to stop at the first ancestor holding a `profiles` folder, which is right for
    /// writing and WRONG for reading, because the toolkit's binaries do not agree on which
    /// ancestor that is: UEToolkit.App's build copies `profiles` into its own output, so it
    /// stops immediately; the Level Editor's does not, so it walks up to the repository root.
    ///
    /// The result was an 18-minute whole-game scan, built from the App, that the Level Editor
    /// could not see -- and the editor's only symptom would have been going on printing
    /// `atlas library: not built` after somebody had just built it. A silent miss whose only
    /// evidence is the absence of an improvement nobody promised.
    ///
    /// Writing still goes to the first. Reading tries them all.
    /// </summary>
    public static List<string> CachePaths(GameProfile profile, bool createDir = false)
    {
        var found = new List<string>();
        var safe = new string(profile.Name.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (!Directory.Exists(Path.Combine(dir.FullName, "profiles"))) continue;
            var cache = Path.Combine(dir.FullName, ".cache");
            found.Add(Path.Combine(cache, $"atlas-{safe}.json"));
        }
        // The one that will be WRITTEN, made last, because creating every candidate directory
        // would scatter empty .cache folders through the tree on a read.
        if (createDir && found.Count > 0)
            Directory.CreateDirectory(Path.GetDirectoryName(found[^1])!);
        return found;
    }

    /// <summary>
    /// Move any texture the profile DECLARES to be one image out of the grid table.
    ///
    /// Applied on load rather than folded into <see cref="Signature"/>, deliberately. This is a
    /// declaration, not a derivation: the table is still exactly what the scan concluded, and this
    /// overrides its verdict for textures where the evidence is a picture rather than a number. So
    /// editing the list takes effect on the next run instead of costing a **35-minute rebuild** —
    /// and a rebuild nobody wants to pay is a rebuild that gets skipped, leaving the real table
    /// stale, which is the failure this whole cache is written around.
    /// </summary>
    public void ApplyNotAnAtlas(Action<string>? log = null)
    {
        var forced = Grids.Keys.Where(UvAtlas.ForcedFlat).ToList();
        foreach (var path in forced) { Grids.Remove(path); Flat.Add(path); }
        if (forced.Count > 0)
            log?.Invoke($"atlas library: {forced.Count} texture(s) forced flat by the profile " +
                        $"({string.Join(", ", forced.Select(p => p.Split('/')[^1]))})");
    }

    public static AtlasLibrary? Load(GameProfile profile, int fileCount, Action<string>? log = null)
    {
        // NEAREST FIRST, then every other ancestor -- see CachePaths.
        var path = CachePaths(profile).FirstOrDefault(File.Exists);
        if (path is null) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<CachePayload>(File.ReadAllText(path));
            if (payload is null) return null;
            if (payload.Version != SchemaVersion || payload.Signature != Signature(profile, fileCount))
            {
                // Say WHICH, because "the viewport went back to guessing grids" is otherwise a
                // silent regression after a game patch, and 35 minutes is too long to spend
                // wondering whether it was meant to happen.
                log?.Invoke("atlas library: cached table is stale (game or grid rules changed) - rebuild it");
                return null;
            }
            var loaded = new AtlasLibrary
            {
                Grids = new Dictionary<string, GridEntry>(payload.Grids, StringComparer.OrdinalIgnoreCase),
                Flat = new HashSet<string>(payload.Flat ?? [], StringComparer.OrdinalIgnoreCase),
                MeshesScanned = payload.MeshesScanned,
                SlotsRecorded = payload.SlotsRecorded,
                Source = payload.Source ?? "",
            };
            loaded.ApplyNotAnAtlas(log);
            return loaded;
        }
        catch (Exception e)
        {
            log?.Invoke($"atlas library: cache unreadable ({e.Message}) - rebuild it");
            return null;
        }
    }

    public string? Save(GameProfile profile, int fileCount, Action<string>? log = null)
    {
        var path = CachePath(profile, createDir: true);
        if (path is null) { log?.Invoke("atlas library: no profiles folder found, nothing cached"); return null; }
        try
        {
            var payload = new CachePayload(Signature(profile, fileCount), SchemaVersion,
                MeshesScanned, SlotsRecorded, Grids,
                Flat.OrderBy(x => x, StringComparer.Ordinal).ToList(), Source);
            File.WriteAllText(path, JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception e)
        {
            log?.Invoke($"atlas library: could not cache - {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Name a mesh that cost real time, and say WHICH phase took it.
    ///
    /// "This mesh was slow" is not actionable; "this mesh spent 40 s resolving materials" is. The
    /// three phases fail for unrelated reasons — a huge Nanite payload, a layered material graph
    /// walked through `AllLayers`, or a cold read off a container — and they need different fixes.
    /// </summary>
    private static void Slow(Action<string>? log, string path,
                             TimeSpan load, TimeSpan materials, TimeSpan convert)
    {
        if (log is null) return;
        var total = load + materials + convert;
        if (total.TotalSeconds < 5) return;
        log($"atlas library: SLOW {total.TotalSeconds,6:0.#}s  " +
            $"load {load.TotalSeconds:0.#} materials {materials.TotalSeconds:0.#} " +
            $"convert {convert.TotalSeconds:0.#}  {path}");
    }

    private static List<PskUv.SectionUv> Extents(
        CUE4Parse_Conversion.Meshes.PSK.CMeshVertex[] verts,
        uint[] indices,
        CUE4Parse_Conversion.Meshes.PSK.CMeshSection[] sections)
    {
        var result = new List<PskUv.SectionUv>();
        for (var si = 0; si < sections.Length; si++)
        {
            var s = sections[si];
            if (s.NumFaces <= 0) continue;
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            var count = 0;
            var end = s.FirstIndex + s.NumFaces * 3;
            for (var i = s.FirstIndex; i < end && i < indices.Length; i++)
            {
                var vi = (int) indices[i];
                if (vi < 0 || vi >= verts.Length) continue;
                var uv = verts[vi].UV;
                if (!float.IsFinite(uv.U) || !float.IsFinite(uv.V)) continue;
                uMin = MathF.Min(uMin, uv.U); uMax = MathF.Max(uMax, uv.U);
                vMin = MathF.Min(vMin, uv.V); vMax = MathF.Max(vMax, uv.V);
                count++;
            }
            if (count == 0) continue;
            // The material ASSET name, falling back to the slot label.
            //
            // `CMeshSection.MaterialName` is `MaterialSlotName` — what the artist called the slot,
            // not what is plugged into it. On the body mesh behind that 39-slot atlas the slot is
            // named for the base material while the material plugged into it is the armour variant,
            // and `PskUv.AtlasTextures` keys on the material. Joining slot names against material names
            // fails with no error and no missing row: the slot is recorded against no texture, so
            // it votes on nothing, and an atlas shared by 39 parts is concluded not to exist.
            //
            // The exporter never hit this because it reads its sections back out of the written
            // PSK, and the PSK writer stores the material name. Reading from the converted mesh
            // instead is what exposed the difference.
            var name = s.Material?.Name.Text ?? s.MaterialName ?? "";
            result.Add(new PskUv.SectionUv(si, name, uMin, uMax, vMin, vMax));
        }
        return result;
    }
}

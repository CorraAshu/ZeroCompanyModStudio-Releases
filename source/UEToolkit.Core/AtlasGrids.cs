using System.Collections.Concurrent;
using System.Globalization;

namespace UEToolkit.Core;

/// <summary>
/// Decides each atlas's tile grid once, from every mesh that uses it, and then bakes it in.
///
/// The grid is a property of the TEXTURE. That was already written down, but the code still
/// decided it per mesh, from the one mesh in hand -- and a single mesh does not contain enough
/// evidence to settle it. One part's UVs spanned only U 0..1: that fits a 4x1 grid and it fits
/// the 8x2 grid the rest of the character uses, and nothing about that part can distinguish them.
/// It got 4x1 while its two sibling parts got 8x2, so one part of one character exported
/// at double scale in the wrong row. Pooled over all three, U reaches 8.0 and 8x2 is forced.
///
/// So export runs in two phases: write every mesh first, then -- once the whole population has
/// been seen -- settle the grids and rewrite the UVs.
///
/// The pooling key is the texture's PATH, never a material name. Material names collide across
/// unrelated assets (six different "lambert1"s ship in this game), and pooling by name would
/// merge atlases that share nothing but a Maya default.
///
/// **Source UV bounds are recorded at export time and pooled from that record, never re-read
/// from the files.** A full extraction is resumable and gets interrupted in practice, so the
/// files on disk may be a mix of baked and unbaked; measuring them would fold already-remapped
/// 0..1 extents into the pool and shrink every grid. The record is of the mesh as authored, so
/// it stays true whatever state the files are in.
/// </summary>
public sealed class AtlasGrids
{
    /// <summary>One material slot of one written file: its atlas, and its UVs as authored.</summary>
    private sealed record Slot(string File, string Material, string TexPath, int W, int H,
                               float UMin, float UMax, float VMin, float VMax);

    private readonly ConcurrentBag<Slot> _slots = [];

    /// <summary>
    /// Files written by THIS run. They must be baked even if uv-baked.log already names them:
    /// a freshly exported file is unremapped by definition, so trusting the log would leave it
    /// that way forever. This is the failure that would follow from deleting done.log without
    /// also deleting uv-baked.log -- every mesh re-exports and the bake skips every one.
    /// </summary>
    private readonly HashSet<string> _fresh = new(StringComparer.OrdinalIgnoreCase);

    // MaxUSpan/MaxVSpan are the widest a SINGLE slot covers, kept alongside the union because the
    // two answer different questions: the union says which tiles are addressed at all, the widest
    // slot says whether any one mesh spans more than one tile. See UvAtlas.Grid.
    private readonly Dictionary<string, (int W, int H, float UMin, float UMax, float VMin, float VMax,
        int Slots, float MaxUSpan, float MaxVSpan)> _pool
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (float Cols, float Rows, float VMinTile)> _grid
        = new(StringComparer.OrdinalIgnoreCase);

    public int Files => _slots.Select(s => s.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int Atlases => _grid.Count;

    /// <summary>
    /// Record a written mesh: which atlas each material slot uses, and that slot's UV extent as
    /// authored. Reading the extent here, immediately after writing, is what makes the pool
    /// independent of whether the file is later baked.
    /// </summary>
    public void Record(string meshFile, Dictionary<string, (string Path, int W, int H)> materials,
                       IEnumerable<PskUv.SectionUv> sections)
    {
        lock (_fresh) _fresh.Add(meshFile);
        foreach (var s in sections)
        {
            if (s.Material.Length == 0) continue;
            // A slot whose material resolves to no texture is still recorded, with an empty
            // path. It votes on nothing and gets no grid, but it belongs in the manifest: most
            // of this game's environment art uses layered materials with no plain diffuse, and
            // "this slot exists and has no texture we can name" is the useful answer, not a
            // missing row. It is also the whole reason the manifest no longer needs a second
            // pass over every asset -- the pass that crashed the run.
            var tex = materials.TryGetValue(s.Material, out var t) ? t : ("", 0, 0);
            _slots.Add(new Slot(meshFile, s.Material, tex.Item1, tex.Item2, tex.Item3,
                s.UMin, s.UMax, s.VMin, s.VMax));
        }
    }

    private const string Header = "file\tmaterial\ttexture\tw\th\tuMin\tuMax\tvMin\tvMax";

    /// <summary>Persist the record so a resumed run pools over everything ever exported.</summary>
    public void Save(string path)
    {
        var inv = CultureInfo.InvariantCulture;
        File.WriteAllLines(path, new[] { Header }.Concat(_slots
            .OrderBy(s => s.File, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Material)
            .Select(s => string.Join('\t', s.File, s.Material, s.TexPath, s.W, s.H,
                s.UMin.ToString("R", inv), s.UMax.ToString("R", inv),
                s.VMin.ToString("R", inv), s.VMax.ToString("R", inv)))));
    }

    /// <summary>Reload a previous run's record. Files that no longer exist are dropped.</summary>
    public int Load(string path)
    {
        if (!File.Exists(path)) return 0;
        var inv = CultureInfo.InvariantCulture;
        var n = 0;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 9) continue;
            if (!int.TryParse(f[3], out var w) || !int.TryParse(f[4], out var h)) continue;
            if (!float.TryParse(f[5], NumberStyles.Float, inv, out var uMin) ||
                !float.TryParse(f[6], NumberStyles.Float, inv, out var uMax) ||
                !float.TryParse(f[7], NumberStyles.Float, inv, out var vMin) ||
                !float.TryParse(f[8], NumberStyles.Float, inv, out var vMax)) continue;
            if (!File.Exists(f[0])) continue;
            _slots.Add(new Slot(f[0], f[1], f[2], w, h, uMin, uMax, vMin, vMax));
            n++;
        }
        return n;
    }

    /// <summary>
    /// Pool every recorded extent onto its texture, settle a grid per texture, then bake.
    ///
    /// <paramref name="bakedLog"/> names files whose UVs have already been rewritten; they are
    /// skipped and the log is appended to as more are done. Baking twice would divide the UVs
    /// twice, and an interrupted bake is not a hypothetical on a run this long.
    /// Returns how many files were rewritten.
    /// </summary>
    /// <param name="root">
    /// Output directory. Only meshes inside it are baked — see the note where it is applied.
    /// </param>
    /// <summary>Texture path to its pooled grid. Empty until <see cref="ResolveGrids"/> runs.</summary>
    public IReadOnlyDictionary<string, (float Cols, float Rows, float VMinTile)> Grids => _grid;

    /// <summary>
    /// Every texture the pool actually saw. A texture in here but NOT in <see cref="Grids"/> has
    /// been judged against the whole population and is not an atlas — which is a real answer, and
    /// a different one from "never looked at it". The viewport needs to tell those apart: the
    /// first should override its per-mesh guess, the second must leave it alone.
    /// </summary>
    public IEnumerable<string> Pooled => _pool.Keys;

    /// <summary>
    /// Pool every recorded slot per TEXTURE and settle one grid for each — the half of the export
    /// that is a fact about the game rather than about the files being written.
    ///
    /// Split out so the viewport can have the same answer. A grid cannot be derived from one mesh:
    /// A body mesh that spans `U 1.005..1.946` is one tile wide, and every square-tile grid
    /// of a 6144x2048 texture contains that — so the shader concludes 1x1, skips the remap, and
    /// samples the wrong column. Pooled across the 39 slots sharing that atlas texture the extent
    /// reaches `U 3.995` and only 6x2 fits. The evidence is in the population, never in the part.
    /// </summary>
    public int ResolveGrids(Action<string>? log = null)
    {
        if (_slots.IsEmpty) return 0;
        ResolvePool(log);
        return _grid.Count;
    }

    public int ResolveAndApply(int jobs, string? bakedLog = null, Action<string>? log = null,
                               string? root = null)
    {
        if (_slots.IsEmpty) return 0;

        ResolvePool(log);
        return ApplyToFiles(jobs, bakedLog, log, root);
    }

    private void ResolvePool(Action<string>? log)
    {
        if (_grid.Count > 0) return;   // already settled
        var outliers = 0;
        foreach (var s in _slots)
        {
            if (s.TexPath.Length == 0) continue;
            // Pooling takes the widest extent, so one mesh that TILES a texture others atlas
            // would drag the pool past any grid that fits and switch the whole atlas off -- the
            // fix quietly undoing itself across the game. A slot that cannot belong to any
            // square-tile grid of this texture (the finest being 64px) is not evidence about
            // its layout, so it does not vote.
            if (!Plausible(s)) { outliers++; continue; }
            var su = s.UMax - s.UMin;
            var sv = s.VMax - s.VMin;
            if (!_pool.TryGetValue(s.TexPath, out var p))
            {
                _pool[s.TexPath] = (s.W, s.H, s.UMin, s.UMax, s.VMin, s.VMax, 1, su, sv);
                continue;
            }
            _pool[s.TexPath] = (p.W, p.H,
                MathF.Min(p.UMin, s.UMin), MathF.Max(p.UMax, s.UMax),
                MathF.Min(p.VMin, s.VMin), MathF.Max(p.VMax, s.VMax), p.Slots + 1,
                MathF.Max(p.MaxUSpan, su), MathF.Max(p.MaxVSpan, sv));
        }

        if (outliers > 0) log?.Invoke($"atlases: {outliers:N0} tiling slot(s) excluded from the vote");

        // Now the extents span the whole atlas, so Fit sees evidence no single part could supply.
        var notAddressed = 0;
        foreach (var (path, p) in _pool)
        {
            // A texture NOBODY addresses beyond 0..1 is not being used as an atlas, whatever its
            // shape. The grid comes from the texture's dimensions, so a plain non-square texture
            // -- 2048x1024 -- yields a 2x1 grid and gets its UVs halved into the left tile even
            // though they already covered the whole image.
            //
            // That is not hypothetical: enabling the atlas for one game to fix a creature's
            // genuine 2x1 atlas immediately broke a flesh material whose UVs were
            // a correct 0.001..0.998 on a 2048x1024 map and came out stopping at half the image.
            //
            // The pooling is what makes this safe for a real atlas: a part using only tile 0
            // spans 0..1 on its own, but pooled with its siblings the texture reaches U 8.0 and
            // still gets its grid. The test is the POOL, never a single slot.
            var slack = UvAtlas.Settings.Slack;
            if (p.UMax <= 1f + slack && p.VMax <= 1f + slack &&
                p.UMin >= -slack && p.VMin >= -slack)
            { notAddressed++; continue; }

            // Declared a single image by the profile, because no rule derived from UVs can tell.
            // See AtlasSettings.NotAnAtlas: one outlier slot defeats the test above, and both
            // candidate replacements were measured against the population and refuted.
            if (UvAtlas.ForcedFlat(path)) { notAddressed++; continue; }

            var (cols, rows, vMinTile, enabled) = UvAtlas.Grid(
                p.W, p.H, p.UMin, p.UMax, p.VMin, p.VMax, p.MaxUSpan, p.MaxVSpan);
            if (enabled) _grid[path] = (cols, rows, vMinTile);
        }
        if (notAddressed > 0)
            log?.Invoke($"atlases: {notAddressed:N0} texture(s) never addressed outside 0..1 - left alone");
        log?.Invoke($"atlases: {_grid.Count:N0} of {_pool.Count:N0} texture(s) resolved to a grid");
    }

    private int ApplyToFiles(int jobs, string? bakedLog, Action<string>? log, string? root)
    {
        var baked = bakedLog is not null && File.Exists(bakedLog)
            ? new HashSet<string>(File.ReadAllLines(bakedLog), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Each file gets its own material-name lookup, so colliding names stay separated -- a
        // name is only ever resolved against the materials of the file it came from.
        var byFile = _slots.GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .Where(g => !baked.Contains(g.Key) || _fresh.Contains(g.Key)).ToList();

        // ...and ONLY files inside the directory being written.
        //
        // atlas-slots.tsv stores ABSOLUTE paths while the double-bake guard (uv-baked.log) is
        // per-directory, so a record carried into another output folder -- copied deliberately to
        // reuse a whole-game atlas vote, which is a reasonable thing to want -- reaches back and
        // bakes the original export a SECOND time. Confirmed by doing it: a 3,088-mesh
        // export went from 481 ok / 296 suspect as written to 329 / 448 on disk,
        // because 2,236 of its meshes had their UVs divided twice by a run that contained three.
        // Halving UVs that are already correct is silent -- they stay inside 0..1.
        if (root is { Length: > 0 })
        {
            var full = Path.GetFullPath(root);
            var before = byFile.Count;
            byFile = byFile.Where(g => Path.GetFullPath(g.Key)
                .StartsWith(full, StringComparison.OrdinalIgnoreCase)).ToList();
            if (before != byFile.Count)
                log?.Invoke($"uv: {before - byFile.Count:N0} recorded mesh(es) live outside this " +
                            "export and were NOT re-baked");
        }

        if (byFile.Count == 0) { log?.Invoke("uv: nothing left to bake"); return 0; }

        var writer = bakedLog is not null ? new StreamWriter(bakedLog, append: true) { AutoFlush = true } : null;
        var changed = 0;
        try
        {
            Parallel.ForEach(byFile, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, g =>
            {
                var mats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in g) mats[s.Material] = s.TexPath;
                try
                {
                    var r = PskUv.Apply(g.Key, name =>
                        mats.TryGetValue(name, out var tex) && _grid.TryGetValue(tex, out var grid)
                            ? grid : null);
                    if (r.Remapped > 0) Interlocked.Increment(ref changed);
                    if (writer is not null) lock (writer) writer.WriteLine(g.Key);
                }
                catch (Exception e) { log?.Invoke($"  remap failed {Path.GetFileName(g.Key)}: {e.Message}"); }
            });
        }
        finally { writer?.Dispose(); }
        return changed;
    }

    /// <summary>
    /// Could this slot's UVs belong to some square-tile grid of its texture at all? The finest
    /// tile worth considering is 64px, which bounds the columns at w/64 and the rows at h/64.
    /// Anything past that is a tiling shader, and tiling says nothing about an atlas layout.
    /// </summary>
    private static bool Plausible(Slot s)
    {
        if (s.W <= 0 || s.H <= 0) return false;
        float maxCols = Math.Max(1, s.W / 64), maxRows = Math.Max(1, s.H / 64);
        return s.UMin >= -UvAtlas.Slack && s.UMax <= maxCols + UvAtlas.Slack &&
               s.VMax <= UvAtlas.VTop + UvAtlas.Slack && s.VMin >= UvAtlas.VTop - maxRows - UvAtlas.Slack;
    }

    public IEnumerable<string> WrittenFiles =>
        _slots.Select(s => s.File).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every atlas and the evidence behind its grid, for a report beside the export.</summary>
    public IEnumerable<string> Lines() =>
        _pool.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k =>
        {
            var p = k.Value;
            var g = _grid.TryGetValue(k.Key, out var gg) ? $"{gg.Cols:0}x{gg.Rows:0}" : "direct";
            return $"{g,-8} {p.W,6}x{p.H,-6} {p.Slots,5} slot  " +
                   $"U {p.UMin,8:0.000}..{p.UMax,-8:0.000} V {p.VMin,8:0.000}..{p.VMax,-8:0.000}  {k.Key}";
        });
}

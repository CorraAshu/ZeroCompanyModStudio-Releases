using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Re-reads a written .psk/.pskx and checks it is actually usable.
///
/// This exists because three separate UV bugs shipped this project a full extraction each, and
/// every one of them reported success while writing wrong files:
///
///   1. --exportall ran its own loop and never called the remap at all.
///   2. The remap was called but keyed off ExportResult.SavedPath, which is null when materials
///      are off, so it skipped every mesh silently.
///   3. The remap ran with a 12x2 grid on a 6x1 atlas, putting every UV at half scale.
///
/// The common thread is that nothing ever looked at the RESULT. Success was inferred from the
/// code having run, which is exactly the thing in question. So this reads the bytes back off
/// disk -- not the buffer we meant to write -- and asserts what has to be true of them.
///
/// (1) and (2) show up as UVs still spanning 0..6. (3) does not: half scale is still inside
/// 0..1, and no single mesh can tell, because a part legitimately covers only a slice of the
/// atlas. It takes the UNION across every mesh sharing a material -- see <see cref="Atlas"/> --
/// which is why the sections are reported rather than just a pass/fail.
/// </summary>
public static class PskValidate
{
    /// <summary>A UV may sit this far outside 0..1 before it counts as outside; islands bleed.</summary>
    private const float Edge = 0.02f;

    public sealed record Issue(string Kind, string Detail)
    {
        public override string ToString() => $"{Kind}: {Detail}";
    }

    /// <summary>One material slot's UV extent as written.</summary>
    public sealed record Section(string Material, float UMin, float UMax, float VMin, float VMax, int Wedges);

    public sealed record Report(
        string File, int Points, int Wedges, int Bones, int Influences,
        IReadOnlyList<Section> Sections, IReadOnlyList<Issue> Issues)
    {
        /// <summary>Channels that ship on every mesh and had never been inspected.</summary>
        public int Normals { get; init; }
        public int Colors { get; init; }
        public IReadOnlyList<(string Name, int Count, float UMin, float UMax, float VMin, float VMax)>
            ExtraUv { get; init; } = [];

        public bool Ok => Issues.Count == 0;
        public override string ToString() =>
            Ok ? "ok" : string.Join("; ", Issues.Select(i => i.ToString()));
    }

    public static Report Check(string path)
    {
        var issues = new List<Issue>();
        var sections = new List<Section>();
        byte[] d;
        try { d = File.ReadAllBytes(path); }
        catch (Exception e) { return new Report(path, 0, 0, 0, 0, sections, [new Issue("unreadable", e.Message)]); }

        var chunks = PskUv.ReadChunks(d);
        var points = chunks.GetValueOrDefault("PNTS0000");
        var wedges = chunks.GetValueOrDefault("VTXW0000");
        var bones = chunks.GetValueOrDefault("REFSKELT");
        var infl = chunks.GetValueOrDefault("RAWWEIGHTS");
        var names = PskUv.MaterialNames(d, chunks);

        // A file with bones and no geometry is a skeleton export, not a broken mesh: SK_*_Skeleton
        // assets carry a hierarchy and nothing else, and that is what they are for.
        var skeletonOnly = bones.Count > 0 && points.Count == 0 && wedges.Count == 0;
        if (!skeletonOnly)
        {
            if (points.Count == 0) issues.Add(new Issue("empty", "no points"));
            if (wedges.Count == 0) issues.Add(new Issue("empty", "no wedges"));
        }

        // Positions. A NaN here is not cosmetic: it propagates through every transform a DCC
        // applies and takes the bounding box with it, so the mesh imports as an invisible speck.
        var badPos = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var o = points.Offset + i * points.Size;
            if (!Finite(d, o) || !Finite(d, o + 4) || !Finite(d, o + 8)) badPos++;
        }
        if (badPos > 0) issues.Add(new Issue("nan", $"{badPos:N0} non-finite position(s)"));

        // Wedges: UV sanity, and that the indices point at things that exist.
        var lo = new Dictionary<int, (float U, float V)>();
        var hi = new Dictionary<int, (float U, float V)>();
        var perMat = new Dictionary<int, int>();
        int badUv = 0, badPoint = 0, badMat = 0;
        for (var i = 0; i < wedges.Count; i++)
        {
            var o = wedges.Offset + i * wedges.Size;
            var pt = BitConverter.ToInt32(d, o);
            var mat = d[o + 12];
            if (pt < 0 || pt >= points.Count) badPoint++;
            if (names.Count > 0 && mat >= names.Count) { badMat++; continue; }

            var u = BitConverter.ToSingle(d, o + 4);
            var v = BitConverter.ToSingle(d, o + 8);
            if (!float.IsFinite(u) || !float.IsFinite(v)) { badUv++; continue; }

            perMat[mat] = perMat.GetValueOrDefault(mat) + 1;
            if (!lo.TryGetValue(mat, out var l)) { lo[mat] = (u, v); hi[mat] = (u, v); continue; }
            lo[mat] = (MathF.Min(l.U, u), MathF.Min(l.V, v));
            var h = hi[mat];
            hi[mat] = (MathF.Max(h.U, u), MathF.Max(h.V, v));
        }
        if (badUv > 0) issues.Add(new Issue("nan", $"{badUv:N0} non-finite UV(s)"));
        if (badPoint > 0) issues.Add(new Issue("index", $"{badPoint:N0} wedge(s) reference a missing point"));
        if (badMat > 0) issues.Add(new Issue("index", $"{badMat:N0} wedge(s) reference a missing material"));

        foreach (var (mat, min) in lo.OrderBy(k => k.Key))
        {
            var max = hi[mat];
            sections.Add(new Section(mat < names.Count ? names[mat] : $"material_{mat}",
                min.U, max.U, min.V, max.V, perMat[mat]));
        }

        // Faces have to address wedges that exist, whichever index width was written.
        var faces = chunks.TryGetValue("FACE3200", out var f32) ? f32 : chunks.GetValueOrDefault("FACE0000");
        var wide = faces.Size >= 18;
        var badFace = 0;
        for (var i = 0; i < faces.Count; i++)
        {
            var o = faces.Offset + i * faces.Size;
            for (var k = 0; k < 3; k++)
            {
                var w = wide ? BitConverter.ToInt32(d, o + k * 4) : BitConverter.ToUInt16(d, o + k * 2);
                if (w < 0 || w >= wedges.Count) badFace++;
            }
        }
        if (badFace > 0) issues.Add(new Issue("index", $"{badFace:N0} face corner(s) reference a missing wedge"));

        // Bones. A parent must come EARLIER in the array: importers walk the list once and build
        // the hierarchy as they go, so a forward reference silently reparents to nothing.
        //
        // -1 is a root and is fine at any index, not just index 0, so a bone at index 7 with no
        // parent is not an ordering error and the check below does not call it one.
        //
        // **But MORE THAN ONE root is a defect, and this file used to say it was normal.** The
        // note here read "several roots in one file is normal" because treating index 0 as the
        // only legal root had accused 41 sockets on a crate. That reasoning was right about the
        // FORMAT and wrong about every importer that reads it: a PSK bone list has to be a TREE,
        // and ActorX resolves a parent as `bones[parent + 1]` over a 1-based array, so the second
        // root asks for `bones[0]` and the import dies with
        //     -- Runtime error: array index must be positive number, got: 0
        //
        // Found by an import failing in 3ds Max, not here -- this validator passed the file. A
        // check that approves output the only target application refuses is measuring the wrong
        // thing. 20 of 12,390 exported meshes; `--pskfixroots` repairs them.
        int badParent = 0, badQuat = 0, roots = 0;
        for (var i = 0; i < bones.Count; i++)
        {
            var o = bones.Offset + i * bones.Size;
            var parent = BitConverter.ToInt32(d, o + 72);
            // Bone 0 is the root: ActorX writes its parent as itself, so 0 is legal there and
            // only there. Everywhere else the parent has to be strictly earlier.
            if (i == 0 ? parent > 0 : parent >= i || parent < -1) badParent++;
            if (parent < 0 && i > 0) roots++;
            var q = 0f;
            for (var k = 0; k < 4; k++)
            {
                var c = BitConverter.ToSingle(d, o + 76 + k * 4);
                if (!float.IsFinite(c)) { q = float.NaN; break; }
                q += c * c;
            }
            if (!float.IsFinite(q) || MathF.Abs(MathF.Sqrt(q) - 1f) > 0.01f) badQuat++;
        }
        if (badParent > 0) issues.Add(new Issue("bones", $"{badParent} bone(s) with an out-of-order parent"));
        if (roots > 0)
            issues.Add(new Issue("bones",
                $"{roots + 1} root bone(s) - a PSK skeleton must be ONE tree, and ActorX fails " +
                $"with 'array index must be positive number, got: 0'. Run --pskfixroots"));
        if (badQuat > 0) issues.Add(new Issue("bones", $"{badQuat} bone(s) with a non-unit orientation"));

        // Skin weights. A point whose weights do not sum to 1 shrinks toward the origin when the
        // rig moves -- proportionally to how far short it falls -- which reads as a torn mesh
        // rather than as a weighting error, so it is worth asserting rather than eyeballing.
        if (infl.Count > 0)
        {
            var sum = new float[points.Count];
            var seen = new bool[points.Count];
            int badBone = 0, badWeight = 0;
            for (var i = 0; i < infl.Count; i++)
            {
                var o = infl.Offset + i * infl.Size;
                var w = BitConverter.ToSingle(d, o);
                var pt = BitConverter.ToInt32(d, o + 4);
                var bone = BitConverter.ToInt32(d, o + 8);
                if (bone < 0 || bone >= bones.Count) badBone++;
                if (!float.IsFinite(w) || w < 0f) { badWeight++; continue; }
                if (pt < 0 || pt >= sum.Length) continue;
                sum[pt] += w;
                seen[pt] = true;
            }
            if (badBone > 0) issues.Add(new Issue("weights", $"{badBone:N0} influence(s) on a missing bone"));
            if (badWeight > 0) issues.Add(new Issue("weights", $"{badWeight:N0} negative or non-finite weight(s)"));

            int unweighted = 0, offSum = 0;
            var worst = 1f;
            for (var i = 0; i < sum.Length; i++)
            {
                if (!seen[i]) { unweighted++; continue; }
                if (MathF.Abs(sum[i] - 1f) <= 1e-3f) continue;
                offSum++;
                if (MathF.Abs(sum[i] - 1f) > MathF.Abs(worst - 1f)) worst = sum[i];
            }
            if (unweighted > 0) issues.Add(new Issue("weights", $"{unweighted:N0} point(s) with no influence"));
            if (offSum > 0) issues.Add(new Issue("weights", $"{offSum:N0} point(s) whose weights miss 1.0 (worst {worst:0.####})"));
        }

        // Normals, vertex colours and the second UV channel. These have been written on every
        // mesh since the beginning and nothing had ever looked at them -- the same shape as the
        // texture pass that reported success while writing nothing. Checked here rather than by
        // re-exporting, because the answer is already in the file.
        var normals = chunks.GetValueOrDefault("VTXNORMS");
        int badNormal = 0, shortNormal = 0;
        for (var i = 0; i < normals.Count; i++)
        {
            var o = normals.Offset + i * normals.Size;
            float x = BitConverter.ToSingle(d, o), y = BitConverter.ToSingle(d, o + 4),
                  z = BitConverter.ToSingle(d, o + 8);
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) { badNormal++; continue; }
            var len = MathF.Sqrt(x * x + y * y + z * z);
            if (MathF.Abs(len - 1f) > 0.02f) shortNormal++;
        }
        if (badNormal > 0) issues.Add(new Issue("normals", $"{badNormal:N0} non-finite normal(s)"));
        // A normal that is not unit length shades wrong rather than failing, so it would never
        // announce itself -- exactly why it is worth asserting instead of assuming.
        if (shortNormal > 0) issues.Add(new Issue("normals", $"{shortNormal:N0} non-unit normal(s)"));
        if (points.Count > 0 && normals.Count == 0 && !skeletonOnly)
            issues.Add(new Issue("normals", "no normal chunk"));

        var colors = chunks.GetValueOrDefault("VERTEXCOLOR");
        if (colors.Count > 0 && colors.Count != wedges.Count)
            issues.Add(new Issue("colors", $"{colors.Count:N0} colour(s) for {wedges.Count:N0} wedge(s)"));

        // UV1 onward. UV0 can be a UDIM atlas; the extra channels are a separate question
        // and their range is reported rather than judged, since a lightmap UV in 0..1 and an
        // atlas UV outside it are both legitimate depending on what the channel is for.
        var extraUv = new List<(string Name, int Count, float UMin, float UMax, float VMin, float VMax)>();
        for (var ch = 0; ch < 8; ch++)
        {
            if (!chunks.TryGetValue($"EXTRAUVS{ch}", out var e) || e.Count == 0) break;
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            var bad = 0;
            for (var i = 0; i < e.Count; i++)
            {
                var o = e.Offset + i * e.Size;
                var u = BitConverter.ToSingle(d, o);
                var v = BitConverter.ToSingle(d, o + 4);
                if (!float.IsFinite(u) || !float.IsFinite(v)) { bad++; continue; }
                uMin = MathF.Min(uMin, u); uMax = MathF.Max(uMax, u);
                vMin = MathF.Min(vMin, v); vMax = MathF.Max(vMax, v);
            }
            if (bad > 0) issues.Add(new Issue("uv1", $"EXTRAUVS{ch}: {bad:N0} non-finite"));
            if (e.Count != wedges.Count)
                issues.Add(new Issue("uv1", $"EXTRAUVS{ch}: {e.Count:N0} for {wedges.Count:N0} wedge(s)"));
            if (uMin <= uMax)
                extraUv.Add(($"EXTRAUVS{ch}", e.Count, uMin, uMax, vMin, vMax));
        }

        return new Report(path, points.Count, wedges.Count, bones.Count, infl.Count, sections, issues)
        {
            Normals = normals.Count,
            Colors = colors.Count,
            ExtraUv = extraUv,
        };
    }

    /// <summary>
    /// What every mesh sharing one material says about that material's atlas, pooled.
    ///
    /// No single mesh can tell a right grid from a grid that is twice too wide: a part covers a
    /// slice of the atlas either way, and both slices sit inside 0..1. What gives it away is that
    /// EVERY part sharing the atlas stops at the same fraction. If the union of all of them tops
    /// out near 0.5, half the texture is addressed by nothing -- nobody authors an atlas that
    /// way, so the grid was doubled. That is bug (3) exactly, and it is the reading the user got
    /// by hand in Max by overlaying the shells on the image.
    /// </summary>
    public sealed class Atlas
    {
        private readonly Dictionary<string, (float UMin, float UMax, float VMin, float VMax, int Meshes, long Wedges)> _m
            = new(StringComparer.OrdinalIgnoreCase);

        public void Add(Report r)
        {
            lock (_m)
                foreach (var s in r.Sections)
                {
                    if (!_m.TryGetValue(s.Material, out var a))
                    {
                        _m[s.Material] = (s.UMin, s.UMax, s.VMin, s.VMax, 1, s.Wedges);
                        continue;
                    }
                    _m[s.Material] = (MathF.Min(a.UMin, s.UMin), MathF.Max(a.UMax, s.UMax),
                                      MathF.Min(a.VMin, s.VMin), MathF.Max(a.VMax, s.VMax),
                                      a.Meshes + 1, a.Wedges + s.Wedges);
                }
        }

        /// <summary>Lines for a report file: every material, worst first, with a verdict.</summary>
        public IEnumerable<string> Lines()
        {
            lock (_m)
                return _m.OrderBy(k => Rank(k.Value.UMin, k.Value.UMax, k.Value.VMin, k.Value.VMax))
                    .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(k =>
                    {
                        var (uMin, uMax, vMin, vMax, meshes, wedges) = k.Value;
                        return $"{Verdict(uMin, uMax, vMin, vMax),-12} {k.Key,-48} " +
                               $"U {uMin,7:0.000}..{uMax,-7:0.000} V {vMin,7:0.000}..{vMax,-7:0.000} " +
                               $"{meshes,4} mesh {wedges,9:N0} wedge";
                    }).ToList();
        }

        public (int Ok, int Outside, int Suspect) Counts()
        {
            lock (_m)
            {
                int ok = 0, outside = 0, suspect = 0;
                foreach (var v in _m.Values)
                    switch (Verdict(v.UMin, v.UMax, v.VMin, v.VMax))
                    {
                        case "OUTSIDE": outside++; break;
                        case "suspect": suspect++; break;
                        default: ok++; break;   // "ok" and "no-uvs" both need no action
                    }
                return (ok, outside, suspect);
            }
        }

        private static int Rank(float uMin, float uMax, float vMin, float vMax) =>
            Verdict(uMin, uMax, vMin, vMax) switch { "OUTSIDE" => 0, "suspect" => 1, "ok" => 2, _ => 3 };

        // "Half the atlas is unused" is the signature of a doubled grid. The bar is 0.55 rather
        // than 0.5 so a shell that just crosses the midpoint is not accused, and 0.26 catches a
        // grid that came out four times too wide.
        private static string Verdict(float uMin, float uMax, float vMin, float vMax)
        {
            // All-zero UVs are not a scale problem to be diagnosed -- shadow and collision meshes
            // ship with no UVs at all, which is fine, and calling that "suspect" buries the real
            // ones. It is still worth naming: a zero-area shell is also how a remap that has gone
            // wrong presents.
            if (uMin == 0f && uMax == 0f && vMin == 0f && vMax == 0f) return "no-uvs";
            if (uMax > 1f + Edge || vMax > 1f + Edge || uMin < -Edge || vMin < -Edge) return "OUTSIDE";
            if (uMax <= 0.55f || vMax <= 0.55f) return "suspect";
            return "ok";
        }
    }

    private static bool Finite(byte[] d, int o) => float.IsFinite(BitConverter.ToSingle(d, o));

    /// <summary>Walks an export tree and checks every mesh in it. Returns the per-file failures.</summary>
    /// <summary>
    /// <paramref name="each"/> sees every report, not just the failures. Added so a caller that
    /// needs per-file detail does not walk 12,390 meshes a second time — the baseline did, and
    /// paid 150 s for what takes 75.
    /// </summary>
    public static (int Checked, List<Report> Bad, Atlas Atlas, Channels Channels) CheckTree(
        string root, int jobs = 1, Action<int>? progress = null, Action<Report>? each = null)
    {
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".psk", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".pskx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var atlas = new Atlas();
        var channels = new Channels();
        var bad = new List<Report>();
        var n = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, f =>
        {
            var r = Check(f);
            atlas.Add(r);
            channels.Add(r);
            if (each is not null) lock (each) each(r);
            if (!r.Ok) lock (bad) bad.Add(r);
            var i = Interlocked.Increment(ref n);
            if (i % 1000 == 0) progress?.Invoke(i);
        });
        return (files.Count, bad, atlas, channels);
    }

    /// <summary>
    /// How many meshes carry each optional channel, and what range the extra UVs occupy.
    /// Presence is the first question -- a channel written for nothing at all looks identical
    /// to one written correctly until someone counts.
    /// </summary>
    public sealed class Channels
    {
        private int _files, _normals, _colors, _uv1;
        private float _uMin = float.MaxValue, _uMax = float.MinValue,
                      _vMin = float.MaxValue, _vMax = float.MinValue;

        public void Add(Report r)
        {
            lock (this)
            {
                _files++;
                if (r.Normals > 0) _normals++;
                if (r.Colors > 0) _colors++;
                if (r.ExtraUv.Count == 0) return;
                _uv1++;
                foreach (var e in r.ExtraUv)
                {
                    _uMin = MathF.Min(_uMin, e.UMin); _uMax = MathF.Max(_uMax, e.UMax);
                    _vMin = MathF.Min(_vMin, e.VMin); _vMax = MathF.Max(_vMax, e.VMax);
                }
            }
        }

        public IEnumerable<string> Lines()
        {
            lock (this)
            {
                yield return $"# normals      : {_normals,7:N0} of {_files:N0} mesh(es)";
                yield return $"# vertex colour: {_colors,7:N0} of {_files:N0} mesh(es)";
                yield return _uv1 == 0
                    ? $"# extra UV     : {0,7:N0} of {_files:N0} mesh(es)  -- NO mesh carries a second UV channel"
                    : $"# extra UV     : {_uv1,7:N0} of {_files:N0} mesh(es)  " +
                      $"U {_uMin:0.000}..{_uMax:0.000} V {_vMin:0.000}..{_vMax:0.000}";
            }
        }
    }

    /// <summary>The whole report as text, ready to write beside the export.</summary>
    public static IEnumerable<string> Format(int checkedCount, List<Report> bad, Atlas atlas,
                                             Channels? channels = null)
    {
        var (ok, outside, suspect) = atlas.Counts();
        yield return $"# {checkedCount:N0} mesh file(s) checked, {bad.Count:N0} with issues";
        if (channels is not null)
            foreach (var l in channels.Lines()) yield return l;
        yield return $"# materials: {ok:N0} ok, {outside:N0} OUTSIDE 0..1, {suspect:N0} suspect";
        yield return "#";
        yield return "# OUTSIDE = UVs left the 0..1 box. Either the atlas remap did not run, or the";
        yield return "#           material genuinely tiles (shield and shadow shaders do -- those are";
        yield return "#           correct and must not be 'fixed').";
        yield return "# suspect = every mesh sharing this material stops around half the image, so the";
        yield return "#           atlas grid is probably twice too wide or twice too tall.";
        yield return "";
        foreach (var l in atlas.Lines()) yield return l;
        if (bad.Count == 0) yield break;
        yield return "";
        yield return "# per-file issues";
        foreach (var r in bad.OrderBy(r => r.File, StringComparer.OrdinalIgnoreCase))
            yield return $"{Path.GetFileName(r.File)}\t{r}";
    }
}

using System.Globalization;
using System.Numerics;
using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Emits a MaxScript that rebuilds a level from its placements: one instance per placement, on
/// meshes the user has already imported.
///
/// A TSV of 10,620 rows is a fact about the level and not a scene. Nobody places ten thousand
/// objects by hand, so until this existed the level work stopped one step short of being usable —
/// which is the same gap the character `PlacementScript` closed, and it is wanted for the same
/// stated reason: *"the max script is nice for times i don't want to assemble stuff myself"*.
///
/// **Transforms are emitted as `matrix3` rows, never as quaternions.** Max's quaternion convention
/// is the opposite of the obvious one and assigning `.rotation` silently applies the conjugate,
/// which would put ten thousand objects at mirrored orientations that each look plausible. A
/// matrix3 row IS the image of a basis vector, so there is nothing left to get backwards — the
/// same reasoning that settled the level composition cross-check, where matrices decided a
/// question two quaternion paths could not.
///
/// **The mirror is the exporter's, not a new one.** A written PSK stores positions with Y negated
/// and rotations mirrored about the XZ plane, so a placement has to land in that same space or the
/// geometry and its position disagree. Under the reflection M = diag(1,−1,1) a rotation R becomes
/// M·R·M, and because M fixes X and Z and flips Y, the three basis images come out as
///
///     rowX =  M·(R·x̂)      rowY = −M·(R·ŷ)      rowZ =  M·(R·ẑ)
///
/// which is self-checking: put R = identity and it returns the identity, as an unrotated object
/// must. Put R = 90° about Z and it returns −90° about Z, which is what mirroring Y does to the
/// sense of a rotation about the axis it leaves alone.
/// </summary>
public static class LevelScript
{
    /// <summary>Objects the script will place, and meshes it could not name.</summary>
    public sealed record Stats(int Meshes, int Instances, int Skipped);

    /// <summary>
    /// The node name a placement wants, which is the OBJECT name and not the package stem.
    ///
    /// `/Game/Levels/.../A15.StaticMesh_5` and `A15.StaticMesh_9` are two different meshes stored
    /// in one package; taking the stem would ask Max for a node called "A15" eighteen times and
    /// stack eighteen unrelated meshes on one another.
    /// </summary>
    public static string NodeName(string meshPath)
    {
        var last = meshPath.Replace('\\', '/').Split('/')[^1];
        var dot = last.LastIndexOf('.');
        return dot > 0 ? last[(dot + 1)..] : last;
    }

    /// <summary>Bounding-box size of a written PSK, in the units the file holds.</summary>
    public static Vector3 MeshSize(string pskPath)
    {
        var d = File.ReadAllBytes(pskPath);
        var chunks = PskUv.ReadChunks(d);
        if (!chunks.TryGetValue("PNTS0000", out var c) || c.Count == 0) return Vector3.Zero;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (var i = 0; i < c.Count; i++)
        {
            var o = c.Offset + i * c.Size;
            var v = new Vector3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4),
                                BitConverter.ToSingle(d, o + 8));
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) continue;
            lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v);
        }
        return hi - lo;
    }

    public static string Emit(IEnumerable<LevelExport.Placement> placements, string levelName,
        out Stats stats, IReadOnlyDictionary<string, Vector3>? meshSizes = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        // Grouped by mesh so the source node is resolved once rather than ten thousand times, and
        // so a missing mesh is reported once with its whole placement count rather than as ten
        // thousand separate failures.
        var groups = placements
            .GroupBy(p => NodeName(p.MeshPath), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        sb.AppendLine($"-- {Branding.Product} level placement for {levelName}");
        sb.AppendLine($"-- {groups.Sum(g => g.Count()):N0} placement(s) of {groups.Count:N0} mesh(es)");
        sb.AppendLine("--");
        sb.AppendLine("-- Import the meshes first (ActorX .psk/.pskx), then run this. Every placement");
        sb.AppendLine("-- becomes an INSTANCE of the imported mesh, so ten thousand objects share one");
        sb.AppendLine("-- set of geometry; a level would not fit in memory otherwise.");
        sb.AppendLine("--");
        sb.AppendLine("-- Meshes you have not imported are reported at the end and cost nothing else,");
        sb.AppendLine("-- so this is usable against a partial export.");
        sb.AppendLine();
        sb.AppendLine("(");
        sb.AppendLine("    -- Hide the imported originals once their instances exist. They are left in");
        sb.AppendLine("    -- the scene rather than deleted so the script can be run twice.");
        sb.AppendLine("    local hideSources = true");
        sb.AppendLine();
        sb.AppendLine("    local src = undefined");
        sb.AppendLine("    local sources = #()");
        sb.AppendLine("    local missing = #()");
        sb.AppendLine("    local placed = #()");
        sb.AppendLine("    local made = 0");
        sb.AppendLine();
        sb.AppendLine("    -- Exact name first, then case-insensitively, then as a prefix: ActorX");
        sb.AppendLine("    -- decorates the name it gives an imported mesh.");
        sb.AppendLine("    fn findNode nm = (");
        sb.AppendLine("        local n = getNodeByName nm exact:true");
        sb.AppendLine("        if n != undefined do return n");
        sb.AppendLine("        for o in objects where (stricmp o.name nm) == 0 do return o");
        sb.AppendLine("        for o in objects where (matchPattern o.name pattern:(nm + \"*\")) do return o");
        sb.AppendLine("        undefined");
        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    -- Select the mesh the following placements instance. n is how many are");
        sb.AppendLine("    -- coming, so a missing mesh reports what it cost.");
        sb.AppendLine("    fn useMesh nm n = (");
        sb.AppendLine("        src = findNode nm");
        sb.AppendLine("        if src == undefined then (");
        sb.AppendLine("            append missing (nm + \" (\" + (n as string) + \" placement(s))\")");
        sb.AppendLine("            false");
        sb.AppendLine("        ) else ( appendIfUnique sources src; true )");
        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    -- Is the mesh the size we think it is?");
        sb.AppendLine("    --");
        sb.AppendLine("    -- Node positions cannot answer this. If the importer brings geometry in at a");
        sb.AppendLine("    -- different scale from the placements, every object lands exactly where it");
        sb.AppendLine("    -- should and the scene still looks blown apart, because the parts are the");
        sb.AppendLine("    -- wrong size for the gaps between them. Comparing MAX's idea of a mesh's");
        sb.AppendLine("    -- bounding box against the .psk we wrote is what separates the two.");
        sb.AppendLine("    local sizeBad = 0");
        sb.AppendLine("    local sizeSeen = 0");
        sb.AppendLine("    local sizeWorst = 0.0");
        sb.AppendLine("    fn checkSize nm wantX wantY wantZ = (");
        sb.AppendLine("        local o = findNode nm");
        sb.AppendLine("        if o == undefined do return false");
        sb.AppendLine("        local got = o.max - o.min");
        sb.AppendLine("        local want = [wantX, wantY, wantZ]");
        sb.AppendLine("        local big = amax want.x want.y want.z");
        sb.AppendLine("        if big < 0.001 do return false");
        sb.AppendLine("        local gotBig = amax got.x got.y got.z");
        sb.AppendLine("        local ratio = gotBig / big");
        sb.AppendLine("        sizeSeen += 1");
        sb.AppendLine("        if (abs (ratio - 1.0)) > 0.02 do (");
        sb.AppendLine("            sizeBad += 1");
        sb.AppendLine("            if (abs (ratio - 1.0)) > (abs (sizeWorst - 1.0)) do sizeWorst = ratio");
        sb.AppendLine("            format \"  SIZE % : Max says % , the .psk says % -- ratio %\\n\" nm gotBig big ratio");
        sb.AppendLine("        )");
        sb.AppendLine("        true");
        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    -- One placement. The nine numbers are the rotated, scaled basis vectors and");
        sb.AppendLine("    -- the last three are the position -- exactly what matrix3 wants, with no");
        sb.AppendLine("    -- rotation convention left to get backwards.");
        // Parameters are m11..m33/tx..tz and NOT ax/ay/az, bx/by/bz: **`by` is a MAXScript
        // reserved word** -- the loop step in `for i = 1 to 10 by 2` -- so `fn p ... bx by bz ...`
        // is a syntax error at the function declaration, and the whole file fails to parse. It
        // cost the user a four-million-poly import to find. `off` is reserved too (the on/off
        // boolean), which is why the check below measures `delta`.
        sb.AppendLine("    fn p m11 m12 m13 m21 m22 m23 m31 m32 m33 tx ty tz = (");
        sb.AppendLine("        if src == undefined do return false");
        sb.AppendLine("        local n = instance src");
        sb.AppendLine("        n.transform = matrix3 [m11, m12, m13] [m21, m22, m23] [m31, m32, m33] [tx, ty, tz]");
        sb.AppendLine("        append placed n");
        sb.AppendLine("        made += 1");
        sb.AppendLine("        true");
        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    disableSceneRedraw()");
        sb.AppendLine("    try (");

        var instances = 0;
        var skipped = 0;
        foreach (var g in groups)
        {
            sb.AppendLine();
            sb.AppendLine($"    useMesh \"{Escape(g.Key)}\" {g.Count()}");
            foreach (var pl in g)
            {
                var q = ToQuaternion(pl.Rotation);
                var rx = Mirror(Vector3.Transform(Vector3.UnitX, q)) * pl.Scale.X;
                // ŷ picks up the reflection's sign as well as the mirror; see the class note.
                var ry = -Mirror(Vector3.Transform(Vector3.UnitY, q)) * pl.Scale.Y;
                var rz = Mirror(Vector3.Transform(Vector3.UnitZ, q)) * pl.Scale.Z;
                var pos = Mirror(new Vector3((float) pl.Location.X, (float) pl.Location.Y,
                    (float) pl.Location.Z));

                sb.AppendLine($"    p {N(rx.X)} {N(rx.Y)} {N(rx.Z)}  {N(ry.X)} {N(ry.Y)} {N(ry.Z)}  " +
                              $"{N(rz.X)} {N(rz.Y)} {N(rz.Z)}  {N(pos.X)} {N(pos.Y)} {N(pos.Z)}");
                instances++;
            }
        }

        sb.AppendLine();
        sb.AppendLine("    ) catch ( enableSceneRedraw(); throw() )");
        sb.AppendLine("    enableSceneRedraw()");
        sb.AppendLine("    redrawViews()");
        sb.AppendLine();
        // Size checks run against the SOURCE nodes by name, so they come BEFORE those nodes are
        // hidden — `findNode` walks `objects`, and relying on a hidden node still turning up there
        // is the kind of assumption that works until it does not.
        if (meshSizes is { Count: > 0 })
        {
            sb.AppendLine();
            foreach (var g in groups)
            {
                if (!meshSizes.TryGetValue(g.Key, out var s) || s == Vector3.Zero) continue;
                sb.AppendLine($"    checkSize \"{Escape(g.Key)}\" {N(s.X)} {N(s.Y)} {N(s.Z)}");
            }
            sb.AppendLine("    if sizeSeen == 0 then format \"(no mesh sizes could be checked)\\n\"");
            sb.AppendLine("    else if sizeBad == 0 then format \"mesh sizes: all % agree with the .psk files\\n\" sizeSeen");
            sb.AppendLine("    else (");
            sb.AppendLine("        format \"\\n!! % of % mesh(es) are the WRONG SIZE in Max, worst ratio %\\n\" sizeBad sizeSeen sizeWorst");
            sb.AppendLine("        format \"!! The placements are then right and the scene still looks blown apart,\\n\"");
            sb.AppendLine("        format \"!! because the parts are the wrong size for the gaps between them.\\n\"");
            sb.AppendLine("        format \"!! Check the ActorX importer's scale setting, then send this ratio back.\\n\\n\"");
            sb.AppendLine("    )");
        }

        sb.AppendLine("    if hideSources do for o in sources where isValidNode o do hide o");
        sb.AppendLine();
        sb.AppendLine("    format \"placed % instance(s)\\n\" made");
        sb.AppendLine("    if missing.count > 0 do (");
        sb.AppendLine("        format \"% mesh(es) not in the scene -- import them and run again:\\n\" missing.count");
        sb.AppendLine("        for m in missing do format \"    %\\n\" m");
        sb.AppendLine("    )");

        // Measure the scene MAX built and compare it to what we predicted, in Max, in one paste.
        //
        // Verify the artefact, not the formula -- the same reason the animation exporter re-reads every .psa
        // it writes. Everything checked before this point was checked against our own arithmetic;
        // this is the first number that comes back from the importer, and the importer is the one
        // participant that has never been wrong here. If the numbers agree the convention is
        // settled, and if they do not they say by how much and in which direction, which is worth
        // far more than "it looks off".
        // `ceiling` excludes `light`, because a hanging ceiling lamp's mesh name contains both
        // words -- without it the ceiling bucket reads "18 objects" for a corridor with two
        // ceiling panels, and a check whose label misdescribes what it counted is a check nobody
        // can act on.
        // A registered game module adds rows for pieces its own levels are built from.
        var checks = new List<(string Label, string Key, string Not)>
        {
            ("floor", "floor", ""), ("ceiling", "ceiling", "light"), ("wall", "wall", ""),
            ("crate", "crate", ""),
        };
        checks.AddRange(GameModules.All.SelectMany(m => m.LevelChecks));
        var rows = new List<(string Label, string Pattern, string Not, float Lo, float Hi, int N)>();
        foreach (var (label, key, not) in checks)
        {
            var z = placements
                .Where(p => NodeName(p.MeshPath).Contains(key, StringComparison.OrdinalIgnoreCase) &&
                            (not.Length == 0 ||
                             !NodeName(p.MeshPath).Contains(not, StringComparison.OrdinalIgnoreCase)))
                .Select(p => (float) p.Location.Z).ToList();
            if (z.Count > 0)
                rows.Add((label, "*" + key + "*", not.Length == 0 ? "" : "*" + not + "*",
                          z.Min(), z.Max(), z.Count));
        }

        if (rows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    -- What MAX ended up with, against what we predicted before it ran.");
            sb.AppendLine("    -- Paste this output back; it settles the convention with numbers.");
            sb.AppendLine("    format \"\\n-- measured in Max, expected from the level data --\\n\"");
            sb.AppendLine("    local bad = 0");
            sb.AppendLine("    -- NB MAXScript's format takes a BARE % only. No %s, no widths, no");
            sb.AppendLine("    -- precision -- '%-14s' prints literally and eats the argument.");
            sb.AppendLine("    fn zcheck label pat notpat wantLo wantHi wantN = (");
            // No `continue` -- MAXScript does not have one, and a script that dies here would
            // waste the whole import it was written to check. Nest the test instead.
            sb.AppendLine("        local lo = 1000000000.0");
            sb.AppendLine("        local hi = -1000000000.0");
            sb.AppendLine("        local n = 0");
            sb.AppendLine("        for o in placed do (");
            sb.AppendLine("            local hit = matchPattern o.name pattern:pat");
            sb.AppendLine("            if hit and notpat != \"\" do hit = not (matchPattern o.name pattern:notpat)");
            sb.AppendLine("            if hit do (");
            sb.AppendLine("                if o.pos.z < lo do lo = o.pos.z");
            sb.AppendLine("                if o.pos.z > hi do hi = o.pos.z");
            sb.AppendLine("                n += 1");
            sb.AppendLine("            )");
            sb.AppendLine("        )");
            sb.AppendLine("        if n == 0 then (");
            sb.AppendLine("            format \"  MISSING % : none placed, expected %\\n\" label wantN");
            sb.AppendLine("            1");
            sb.AppendLine("        ) else (");
            // A centimetre of tolerance: the TSV rounds to three decimals and Max stores floats,
            // so demanding equality would fail on arithmetic rather than on placement.
            sb.AppendLine("            local delta = amax (abs (lo - wantLo)) (abs (hi - wantHi))");
            sb.AppendLine("            local ok = (delta <= 1.0) and (n == wantN)");
            sb.AppendLine("            local tag = if ok then \"ok      \" else \"MISMATCH\"");
            sb.AppendLine("            format \"  % % : % of % objects, Z % .. % , expected % .. % , off by %\\n\" tag label n wantN lo hi wantLo wantHi delta");
            sb.AppendLine("            if ok then 0 else 1");
            sb.AppendLine("        )");
            sb.AppendLine("    )");
            foreach (var (label, pattern, not, lo, hi, n) in rows)
                sb.AppendLine($"    bad += zcheck \"{Escape(label)}\" \"{Escape(pattern)}\" " +
                              $"\"{Escape(not)}\" {N(lo)} {N(hi)} {n}");
            sb.AppendLine("    if bad == 0 then format \"ALL CHECKS PASS -- the transform is right\\n\"");
            sb.AppendLine("    else format \"% CHECK(S) FAILED -- send this output back\\n\" bad");
        }

        sb.AppendLine(")");

        stats = new Stats(groups.Count, instances, skipped);
        // Refuse to hand back a script 3ds Max cannot parse. There is no compiler on this side,
        // and the last one that got out cost the user a four-million-polygon import before failing
        // on its own function declaration.
        var script = sb.ToString();
        MaxScriptCheck.Assert(script, $"the level script for {levelName}");
        return script;

        string N(float v) => v.ToString("0.####", inv);
    }

    /// <summary>
    /// Worst disagreement, in degrees, between <see cref="ToQuaternion"/> and CUE4Parse's own
    /// <c>FRotator.Quaternion()</c> over these placements.
    ///
    /// Two implementations agreeing is not proof either matches UE — but this one decides the
    /// orientation of every object in the scene, and a wrong Euler order produces rotations that
    /// are individually plausible and collectively nonsense. Agreement with the library that reads
    /// the package is the strongest check available without a DCC, and it costs nothing.
    /// </summary>
    public static double VerifyRotation(IEnumerable<LevelExport.Placement> placements)
        => VerifyRotation(placements, out _);

    public static double VerifyRotation(IEnumerable<LevelExport.Placement> placements,
        out string worstWhere)
    {
        var worst = 0.0;
        worstWhere = "";
        foreach (var pl in placements)
        {
            var mine = Quaternion.Normalize(ToQuaternion(pl.Rotation));
            var theirs = pl.Rotation.Quaternion();
            var lib = Quaternion.Normalize(new Quaternion(theirs.X, theirs.Y, theirs.Z, theirs.W));
            // q and -q are the same rotation, so take whichever pairing is nearer.
            //
            // NOT `2*acos(dot)`, which is the obvious form and is unusable here: acos is
            // ill-conditioned as its argument approaches 1, so a rounding of 1e-7 in the dot
            // product comes out as ~0.03 degrees of "disagreement". This check reported 0.056 deg
            // worst across A15 while the two quaternions were BIT-IDENTICAL when printed --
            // it was measuring its own arithmetic. Chasing that number would have meant
            // "fixing" a conversion that already matched UE's own source line for line.
            //
            // The chord form is well conditioned exactly where the answer matters: for a small
            // angle the distance between two unit quaternions IS the half-angle, to first order.
            var d = Math.Min((mine - lib).Length(), (mine + lib).Length());
            var deg = 2 * Math.Asin(Math.Clamp(d / 2, 0, 1)) * 180 / Math.PI;
            if (deg <= worst) continue;
            worst = deg;
            worstWhere = $"{pl.Actor}/{pl.Component} " +
                         $"pitch {pl.Rotation.Pitch:R} yaw {pl.Rotation.Yaw:R} " +
                         $"roll {pl.Rotation.Roll:R}\n" +
                         $"        ours {mine.X:R},{mine.Y:R},{mine.Z:R},{mine.W:R}\n" +
                         $"        lib  {lib.X:R},{lib.Y:R},{lib.Z:R},{lib.W:R}";
        }
        return worst;
    }

    /// <summary>UE object space to the exporter's: negate Y, which is what a written PSK holds.</summary>
    private static Vector3 Mirror(Vector3 v) => new(v.X, -v.Y, v.Z);

    /// <summary>
    /// An <c>FRotator</c> as a quaternion, without depending on CUE4Parse's operator.
    ///
    /// UE's rotator is degrees of pitch (Y), yaw (Z) and roll (X), composed yaw · pitch · roll.
    /// Written out here because this is the one place a level's ten thousand orientations are
    /// decided, and CUE4Parse's own quaternion multiply carries the comment *"both yield different
    /// results idk why"* across its two code paths.
    /// </summary>
    private static Quaternion ToQuaternion(CUE4Parse.UE4.Objects.Core.Math.FRotator r)
    {
        const float toRad = MathF.PI / 180f;
        var (sp, cp) = MathF.SinCos((float) r.Pitch * toRad * 0.5f);
        var (sy, cy) = MathF.SinCos((float) r.Yaw * toRad * 0.5f);
        var (sr, cr) = MathF.SinCos((float) r.Roll * toRad * 0.5f);
        return new Quaternion(
            cr * sp * sy - sr * cp * cy,
            -cr * sp * cy - sr * cp * sy,
            cr * cp * sy - sr * sp * cy,
            cr * cp * cy + sr * sp * sy);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

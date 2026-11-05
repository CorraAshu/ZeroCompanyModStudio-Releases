using System.Globalization;
using System.Numerics;
using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Emits a MaxScript that assembles an exported character: link each part to its bone, and move
/// the ones that need moving.
///
/// Parts export at their own origin, so a character is five or more pieces that all land on top
/// of each other. The game already knows where each belongs — its region table names the bone — so
/// this is a lookup, not a puzzle.
///
/// **Everything is measured from the EXPORTED files, not from the UE assets.** The bone position
/// comes out of the written PSK's REFSKELT, which is already in the exporter's coordinate system
/// (Y negated, centimetres), so the numbers in the script are the numbers Max will see. Deriving
/// them from UE data instead would mean a second conversion path that could disagree with the one
/// that produced the geometry — and that class of disagreement is exactly what cost four
/// extractions.
///
/// The two authoring conventions are the whole problem, and some games mix them:
///   * **Origin-authored** — helmets, hats, most static attachments. Modelled around the origin,
///     so they must be carried to the bone.
///   * **In-place** — modelled where they belong already. Moving these applies the position a
///     second time and throws them across the room. All 121 parts of one vehicle are like this.
/// The test is which is nearer: the part's own centre to the origin, or to the bone. That is the
/// same test <c>Viewport.AttachTransform</c> uses, deliberately — two implementations of this
/// would drift, and the viewport is the reference the user checks against.
/// </summary>
public static class PlacementScript
{
    public sealed record Placement(
        string MeshName, string BoneName, bool OriginAuthored, Vector3 BonePos, Vector3 PartCentre,
        // Is the offset a DELTA to add, or the absolute position to set?
        //
        // An origin-authored part sits at (0,0,0), so carrying one to its bone is the same
        // operation either way and the script sets the position. A separately authored face rig
        // is not at the origin -- its root bone is at its own rest position, just the wrong one --
        // so setting the position would discard where the part already is and land it somewhere
        // new. The distinction is invisible for origin-authored parts, which is exactly why it
        // needs to be explicit rather than implied by OriginAuthored.
        bool Relative = false)
    {
        /// <summary>Where the part has to move. Zero for in-place parts, which must not move.</summary>
        public Vector3 Offset => OriginAuthored ? BonePos : Vector3.Zero;
    }

    /// <summary>Bone world-space rest positions of a written PSK, by name.</summary>
    public static Dictionary<string, Vector3> BonePositions(string pskPath)
        => PskSkeleton.Positions(pskPath);

    /// <summary>Centre of a written mesh's points, in the same space as the bones.</summary>
    public static Vector3 Centre(string pskPath)
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
        // Bounds centre, not the mean of the points: a dense cluster of vertices in one corner
        // drags the mean and would mislabel where the part is authored.
        return lo.X > hi.X ? Vector3.Zero : (lo + hi) * 0.5f;
    }

    /// <summary>
    /// Decide, for one exported part, whether it needs carrying to its bone.
    /// <paramref name="hostBones"/> comes from whichever exported part carries the full rig.
    /// </summary>
    public static Placement? For(string partPsk, string? boneName,
                                 IReadOnlyDictionary<string, Vector3> hostBones)
    {
        if (boneName is null || !hostBones.TryGetValue(boneName, out var bonePos)) return null;
        var centre = Centre(partPsk);
        var originAuthored = centre.Length() < Vector3.Distance(centre, bonePos);
        return new Placement(Path.GetFileNameWithoutExtension(partPsk), boneName,
            originAuthored, bonePos, centre);
    }

    /// <summary>
    /// Place parts by aligning each one's ROOT bone onto the same bone in the host rig, for games
    /// that ship no assembly data.
    ///
    /// Where the game's region table names the attach bone, <see cref="For"/> is a lookup. Some
    /// games name nothing — a character is simply several meshes in a folder — and they need it
    /// more, not less: **a face rig can be authored around its own root**, so a
    /// body and its face imported together put the head at the floor. One face disagrees with
    /// its body about `neckA` by 172.4 cm and another about `spineC` by 167.45 cm, in both cases
    /// with zero rotation, which is the signature of a part authored at its own origin.
    ///
    /// The rule is the part's own skeleton: a face rig's root bone IS `spineC`, a bone the
    /// body rig also has. Aligning them is arithmetic — `hostPos - partPos` — not the
    /// nearer-to-origin-or-bone heuristic <see cref="For"/> has to use when the only thing known
    /// is a bone name. Where the roots do not correspond, this returns nothing rather than
    /// guessing; a part moved to the wrong bone is harder to notice than one that did not move.
    /// </summary>
    public static List<Placement> ByRootBone(IReadOnlyList<string> partPsks, out string? hostPsk)
    {
        hostPsk = null;
        var places = new List<Placement>();
        if (partPsks.Count < 2) return places;

        var rigs = new List<(string Path, List<PskSkeleton.Bone> Bones, Dictionary<string, Vector3> World)>();
        foreach (var p in partPsks)
        {
            try
            {
                var bones = PskSkeleton.Read(p);
                if (bones.Count > 0) rigs.Add((p, bones, PskSkeleton.Positions(p)));
            }
            catch { /* an unreadable part simply is not placed */ }
        }
        if (rigs.Count < 2) return places;

        // The host is the rig that can actually RECEIVE the others: the one containing the most of
        // the other parts' root bones. Bone count is only the tie-break.
        //
        // "Most bones" alone is wrong here, and confidently so. A facial rig is hundreds of bones
        // of face against a stub of body -- one face is 455 bones to its body's 188 -- so it
        // wins on count and then has nowhere to attach anything, because it has no `origin` bone
        // for the body to align to. The first version of this reported "parts already share a rig
        // origin" for a character whose head was 167 cm out of place.
        var roots = rigs.Select(r => r.Bones[0].Name).ToList();
        var host = rigs
            .OrderByDescending(r => roots.Count(n =>
                !n.Equals(r.Bones[0].Name, StringComparison.OrdinalIgnoreCase) && r.World.ContainsKey(n)))
            // Then bones, then vertices. Several of a character's parts are equally capable of
            // hosting -- a body, cape, cloth and tassel can all carry the same 193-bone rig and
            // agree on every shared bone, so the offset is right whichever is chosen -- but a run
            // that announces a cape's rig as the host invites doubt about a correct answer.
            //
            // Vertices cannot lead: a hair part can be 21,102 verts of hair cards on 31 bones,
            // more geometry than the body's 20,561 and nothing like a body rig.
            .ThenByDescending(r => r.Bones.Count)
            .ThenByDescending(r => VertexCount(r.Path))
            .First();
        hostPsk = host.Path;

        foreach (var part in rigs)
        {
            if (ReferenceEquals(part.Path, host.Path)) continue;
            var root = part.Bones[0].Name;
            // A part whose root is the host's root is already in the host's space. Nothing to do,
            // and saying "offset 0" would still be a line telling someone to move it.
            if (root.Equals(host.Bones[0].Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!host.World.TryGetValue(root, out var hostPos)) continue;
            if (!part.World.TryGetValue(root, out var partPos)) continue;

            var delta = hostPos - partPos;
            if (delta.Length() < 0.01f) continue;   // already aligned
            places.Add(new Placement(Path.GetFileNameWithoutExtension(part.Path), root,
                OriginAuthored: true, BonePos: delta, PartCentre: partPos, Relative: true));
        }
        return places;
    }

    /// <summary>Points in a written mesh, for picking the biggest part. 0 if unreadable.</summary>
    private static int VertexCount(string pskPath)
    {
        try
        {
            var chunks = PskUv.ReadChunks(File.ReadAllBytes(pskPath));
            return chunks.TryGetValue("PNTS0000", out var c) ? c.Count : 0;
        }
        catch { return 0; }
    }

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>
    /// The MaxScript itself. It reports what it could not find rather than failing quietly —
    /// object names depend on how ActorX named them on import, which this cannot know for
    /// certain, so the script has to be honest about misses.
    /// </summary>
    /// <param name="notes">
    /// Extra header lines. <see cref="VehicleScript"/> uses these to state which hull bone each
    /// turret was mounted on and whether that pairing was inferred — facts the reader needs and
    /// that a flat list of `place` calls cannot carry. It is a parameter rather than a second
    /// emitter because a copy of this function is exactly the failure this file keeps hitting.
    /// </param>
    public static string Emit(string characterName, IReadOnlyList<Placement> places,
                              IReadOnlyList<string>? notes = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- {Branding.Product} placement for {characterName}");
        sb.AppendLine("--");
        sb.AppendLine("-- Import every part first (ActorX .psk), then run this.");
        sb.AppendLine("-- It links each part to its bone and moves the ones authored at the origin.");
        sb.AppendLine("-- Parts authored in place are linked but NOT moved -- moving those applies");
        sb.AppendLine("-- their position twice.");
        if (notes is { Count: > 0 })
        {
            sb.AppendLine("--");
            foreach (var n in notes) sb.AppendLine($"-- {n}");
        }
        sb.AppendLine();
        sb.AppendLine("(");
        sb.AppendLine("    -- Match each part's orientation to its bone as well as its position.");
        sb.AppendLine("    -- MEASURED in Max: hand-aligning a part with the Align dialog produces");
        sb.AppendLine("    -- exactly the bone's rotation (0.014 degrees apart), so this reproduces");
        sb.AppendLine("    -- what the dialog does. Set false for position only.");
        sb.AppendLine("    local applyRotation = true");
        sb.AppendLine();
        sb.AppendLine("    local missing = #()");
        sb.AppendLine();
        sb.AppendLine("    -- Match on exact name first, then case-insensitively, then as a prefix:");
        sb.AppendLine("    -- ActorX may decorate the name it gives an imported mesh.");
        sb.AppendLine("    -- A prefix match must break on a SEPARATOR, not mid-word.");
        sb.AppendLine("    --");
        sb.AppendLine("    -- `matchPattern` is case-insensitive, so looking up the bone `turret` matched");
        sb.AppendLine("    -- `TurretBack_M` -- a different bone, and a DESCENDANT of the node being");
        sb.AppendLine("    -- placed, so Max refused with `Illegal parent`. That error was the lucky");
        sb.AppendLine("    -- outcome: had the wrong bone not been an ancestor, the turret would have been");
        sb.AppendLine("    -- parented to the wrong place and reported as placed.");
        sb.AppendLine("    --");
        sb.AppendLine("    -- The fallback exists because --split appends `_<material>` and ActorX");
        sb.AppendLine("    -- decorates names, and both add a SEPARATOR first. Requiring one keeps");
        sb.AppendLine("    -- SM_Foo -> SM_Foo_MI_Metal while rejecting turret -> TurretBack_M.");
        sb.AppendLine("    fn nameMatches candidate nm = (");
        sb.AppendLine("        if (stricmp candidate nm) == 0 then true");
        sb.AppendLine("        else if candidate.count > nm.count and \\");
        sb.AppendLine("                (stricmp (substring candidate 1 nm.count) nm) == 0 then \\");
        sb.AppendLine("            (candidate[nm.count + 1] == \"_\")");
        sb.AppendLine("        else false");
        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    fn findNode nm = (");
        sb.AppendLine("        local n = getNodeByName nm exact:true");
        sb.AppendLine("        if n != undefined do return n");
        sb.AppendLine("        for o in objects where (stricmp o.name nm) == 0 do return o");
        sb.AppendLine("        for o in objects where (nameMatches o.name nm) do return o");
        sb.AppendLine("        undefined");
        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    -- EVERY node matching the name, not the first one.");
        sb.AppendLine("    --");
        sb.AppendLine("    -- `--split` writes one object per material id, so a mesh exported as");
        sb.AppendLine("    -- SM_Foo arrives as SM_Foo_MI_Metal, SM_Foo_MI_Paint, SM_Foo_MI_Plastic.");
        sb.AppendLine("    -- The prefix fallback above matches all three and returns one, which would");
        sb.AppendLine("    -- place a third of the model and leave the rest at the origin -- reported as");
        sb.AppendLine("    -- success, because the node WAS found. Splitting and placement are separate");
        sb.AppendLine("    -- features that would have quietly disagreed the first time both were used.");
        sb.AppendLine("    fn findNodes nm = (");
        sb.AppendLine("        local hits = #()");
        sb.AppendLine("        local n = getNodeByName nm exact:true");
        sb.AppendLine("        if n != undefined do append hits n");
        sb.AppendLine("        for o in objects where (stricmp o.name nm) == 0 and (findItem hits o) == 0 do append hits o");
        sb.AppendLine("        if hits.count == 0 do (");
        sb.AppendLine("            for o in objects where (nameMatches o.name nm) do append hits o");
        sb.AppendLine("        )");
        sb.AppendLine("        hits");
        sb.AppendLine("    )");
        sb.AppendLine();
        sb.AppendLine("    fn place meshName boneName dx dy dz doMove relative = (");
        sb.AppendLine("        local hits = findNodes meshName");
        sb.AppendLine("        local b = findNode boneName");
        sb.AppendLine("        if hits.count == 0 then ( append missing (meshName + \" (mesh)\"); return false )");
        sb.AppendLine("        if b == undefined then ( append missing (meshName + \" -> \" + boneName + \" (bone)\"); return false )");
        sb.AppendLine("        -- Never parent a node to itself or to something hanging off it.");
        sb.AppendLine("        --");
        sb.AppendLine("        -- Max raises `Illegal parent, possibly same node or ancestor node` and the");
        sb.AppendLine("        -- whole fileIn aborts, so every placement after the bad one is lost. Catching");
        sb.AppendLine("        -- it here costs one walk up the chain, keeps the remaining parts placed, and");
        sb.AppendLine("        -- names the pair -- which is the information needed to fix it.");
        sb.AppendLine("        for m in hits do (");
        sb.AppendLine("            local anc = b");
        sb.AppendLine("            while anc != undefined do (");
        sb.AppendLine("                if anc == m do (");
        sb.AppendLine("                    append missing (meshName + \" -> \" + boneName + \" (would be its own descendant)\")");
        sb.AppendLine("                    format \"    SKIPPED % -> % : % hangs off %, so parenting would be circular\n\" \\");
        sb.AppendLine("                        meshName boneName boneName meshName");
        sb.AppendLine("                    return false");
        sb.AppendLine("                )");
        sb.AppendLine("                anc = anc.parent");
        sb.AppendLine("            )");
        sb.AppendLine("        )");
        sb.AppendLine("        for m in hits do (");
        sb.AppendLine("        local p0 = m.pos");
        sb.AppendLine("        -- Ask MAX where the bone is; do not tell it.");
        sb.AppendLine("        --");
        sb.AppendLine("        -- The commented numbers are ours, FK'd from the PSK's REFSKELT. They are a");
        sb.AppendLine("        -- SECOND derivation of something the importer has already worked out, and the");
        sb.AppendLine("        -- two can disagree -- over a scale, an axis order, or an FK composition that");
        sb.AppendLine("        -- does not match ActorX's. Reading b.transform.pos removes that whole class");
        sb.AppendLine("        -- of divergence: whatever the importer built, the part goes there.");
        sb.AppendLine("        if doMove do (");
        sb.AppendLine("            -- relative: the part is already somewhere and only needs shifting onto");
        sb.AppendLine("            -- its bone. Setting the position would discard where it is. This one");
        sb.AppendLine("            -- stays on our numbers on purpose -- it is a DELTA between two values");
        sb.AppendLine("            -- from the same PSK space, so a scale or axis difference cancels.");
        sb.AppendLine("            if relative then m.pos += [dx, dy, dz]");
        sb.AppendLine("            else (");
        sb.AppendLine("                -- This is exactly Max's own Align, which is how the user does it");
        sb.AppendLine("                -- by hand:");
        sb.AppendLine("                --     Align Position (World)  : X/Y/Z, Pivot Point -> Pivot Point");
        sb.AppendLine("                --     Align Orientation (Local): X/Y/Z");
        sb.AppendLine("                --     Match Scale              : OFF");
        sb.AppendLine("                --");
        sb.AppendLine("                -- Note what that rules out. `m.transform = b.transform` is the");
        sb.AppendLine("                -- tempting one-liner and it would copy the bone's SCALE as well --");
        sb.AppendLine("                -- silently wrong, and hard to see on a part that is roughly the");
        sb.AppendLine("                -- right size. Match Scale is unchecked for a reason.");
        sb.AppendLine("                --");
        sb.AppendLine("                -- PIVOT POINT, both sides. In MAXScript `node.pos` IS the pivot,");
        sb.AppendLine("                -- not the bounding-box centre, so this matches the dialog. The");
        sb.AppendLine("                -- difference is not cosmetic: a head part can have its centre");
        sb.AppendLine("                -- ~9 units off its own origin, so a centre-based align would sit");
        sb.AppendLine("                -- quietly wrong by that much.");
        sb.AppendLine("                --");
        sb.AppendLine("                -- Rotation first, then position: setting .rotation spins about the");
        sb.AppendLine("                -- pivot and would otherwise carry an already-set position with it.");
        sb.AppendLine("                --");
        sb.AppendLine("                -- MEASURED, not assumed. Hand-aligning a shoulder part");
        sb.AppendLine("                -- with the dialog gave a rotation 0.014 degrees from the bone's,");
        sb.AppendLine("                -- i.e. the same rotation. Assigning it here is that operation.");
        sb.AppendLine("                --");
        sb.AppendLine("                -- DO NOT compare euler triples to check this. The dialog result");
        sb.AppendLine("                -- reads (40.99, 59.11, -173.98) against the bone's (56.45, 45.48,");
        sb.AppendLine("                -- -136.73) and looks plainly different -- they are the same");
        sb.AppendLine("                -- rotation in ZYX. Comparing eulers is how this was called broken.");
        sb.AppendLine("                if applyRotation do m.rotation = b.transform.rotation");
        sb.AppendLine("                m.pos = b.transform.pos");
        sb.AppendLine("            )");
        sb.AppendLine("        )");
        sb.AppendLine("        m.parent = b");
        sb.AppendLine("        -- A part told to move that did NOT move is the silent failure.");
        sb.AppendLine("        --");
        sb.AppendLine("        -- A turret anchor reported as `placed` while sitting at the origin looks");
        sb.AppendLine("        -- identical in the log to one that worked, and that is exactly how the");
        sb.AppendLine("        -- turret cannon came out wrong: the node was found, so nothing complained.");
        sb.AppendLine("        -- Distance moved costs one line and turns `it did not seem to move` into a");
        sb.AppendLine("        -- number.");
        sb.AppendLine("        if doMove and (distance p0 m.pos) < 0.001 do");
        sb.AppendLine("            format \"    DID NOT MOVE % (already at % - is this the node you meant?)\n\" m.name p0");
        sb.AppendLine("        )");
        sb.AppendLine("        -- Say how far our computed bone position was from the one Max built. It");
        sb.AppendLine("        -- no longer changes the result, but a large number here means the exported");
        sb.AppendLine("        -- rig and the importer disagree, which is worth knowing rather than hiding.");
        sb.AppendLine("        if doMove and not relative do (");
        sb.AppendLine("            local drift = distance b.transform.pos [dx, dy, dz]");
        sb.AppendLine("            if drift > 0.5 do format \"    NOTE % : our numbers were % off Max's bone\\n\" meshName drift");
        sb.AppendLine("        )");
        sb.AppendLine("        -- The count matters once --split is in play: one node and six should");
        sb.AppendLine("        -- not report identically.");
        sb.AppendLine("        format \"placed % (% node(s)) -> %\\n\" meshName hits.count boneName");
        sb.AppendLine("        true");
        sb.AppendLine("    )");
        sb.AppendLine();
        foreach (var p in places)
        {
            var o = p.Offset;
            sb.AppendLine($"    place \"{p.MeshName}\" \"{p.BoneName}\" " +
                          $"{F(o.X)} {F(o.Y)} {F(o.Z)} {(p.OriginAuthored ? "true" : "false")} " +
                          $"{(p.Relative ? "true" : "false")}" +
                          $"   -- {(!p.OriginAuthored ? "authored in place, not moved"
                                   : p.Relative ? $"shifted onto {p.BoneName} ({o.Length():0.#} cm)"
                                                : "authored at origin, carried to the bone")}");
        }
        sb.AppendLine();
        sb.AppendLine("    if missing.count > 0 then (");
        // MAXScript's format placeholder is `%`, not `%s` -- printf habits print a stray letter.
        sb.AppendLine("        format \"\\n% NOT placed -- import them, or check the object name:\\n\" missing.count");
        sb.AppendLine("        for m in missing do format \"    %\\n\" m");
        sb.AppendLine("    ) else format \"\\nall parts placed\\n\"");
        sb.AppendLine(")");
        // Same gate as the level script. This one has never shipped a parse error, and that is
        // luck rather than care -- its parameter names simply happened to miss the keyword list.
        var script = sb.ToString();
        MaxScriptCheck.Assert(script, $"the placement script for {characterName}");
        return script;
    }
}

using System.Globalization;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace UEToolkit.Core;

/// <summary>
/// Where each mesh sits in a level.
///
/// A `.umap` is a package like any other; its exports are the actors and components of the scene,
/// and a `StaticMeshComponent` carries both the mesh it draws and the transform it draws it at.
/// That pair is the whole of a placement, and it is what turns "here are 12,000 meshes" into
/// "here is a level".
///
/// **Transforms are read from the component, not the actor.** An actor's root component holds the
/// placement while the actor itself often holds nothing, so reading the actor gives identity
/// transforms that look like a scene collapsed at the origin — plausible, and wrong.
///
/// **A component's transform is relative to what it is attached to**, so the chain has to be
/// composed. How much that matters varies enormously and cannot be guessed: 6 of 536 components
/// in one streaming cell are attached, against 55 of 72 in another level. Writing the relative
/// value as though it were the world one puts a quarter of the first and three quarters of the
/// second in the wrong place, and every one of them lands somewhere plausible.
/// </summary>
public static class LevelExport
{
    public sealed record Placement(
        string Actor, string Component, string MeshPath,
        FVector Location, FRotator Rotation, FVector Scale,
        // How many attachments were composed to get here, and to what. Depth 0 is a component
        // whose transform was already the world one.
        int Depth = 0, string? AttachedTo = null, string? Socket = null, string? Cell = null,
        // The component class. An InstancedStaticMeshComponent draws its mesh MANY times from
        // per-instance transforms and its own transform is frequently the identity, so counting it
        // as one placement at the origin is both an undercount and a wrong position.
        string Type = "", int Instances = 1, int Instance = -1);

    /// <summary>Counts worth reporting, because "it worked" is not a number.</summary>
    public sealed record ReadStats(
        int Components, int Attached, int Composed, int NoMesh,
        int ParentOutsidePackage, int Cycles, int Sockets, int AbsoluteChannels,
        int HlodSkipped = 0, int InstancedComponents = 0, int InstanceRows = 0,
        // Placements we actually WRITE whose attach chain passes through a socket. Separate from
        // `Sockets`, which counts components naming one anywhere in the export table -- including
        // components that draw nothing and so cost the output nothing.
        int SocketPlacements = 0,
        // Placements brought in from LEVEL INSTANCES: whole buildings authored as their own .umap
        // and dropped in as one actor.
        int FromLevelInstances = 0,
        // Placements a prefab's own LevelInstancePivotOffset moved. Separate from
        // FromLevelInstances because most level instances have no pivot: reporting them as one
        // number would hide the fix going quiet.
        int Pivoted = 0,
        // Socket attachments whose offset we DID resolve and compose. The counterpart to
        // SocketPlacements, and the only number that says the socket work is doing anything: a
        // fix that silently stops warning looks exactly like a fix that silently stopped running.
        int SocketsResolved = 0)
    {
        public IEnumerable<string> ToLines()
        {
            yield return $"  {Components:N0} component(s) draw a mesh; " +
                         $"{NoMesh:N0} other component(s) draw nothing";
            if (InstancedComponents > 0)
                yield return $"  {InstancedComponents:N0} instanced component(s) expanded to " +
                             $"{InstanceRows:N0} instance(s)";
            if (FromLevelInstances > 0)
                yield return $"  {FromLevelInstances:N0} placement(s) came from level instance(s) - " +
                             "whole levels dropped in as one actor";
            if (Pivoted > 0)
                yield return $"  {Pivoted:N0} of those were moved by the prefab's own " +
                             "LevelInstancePivotOffset";
            if (HlodSkipped > 0)
                yield return $"  {HlodSkipped:N0} HLOD proxy component(s) skipped (--hlod to keep)";
            if (Attached > 0)
                yield return $"  {Attached:N0} attached to a parent; {Composed:N0} chain(s) composed";
            // Everything below is a placement this cannot place correctly. Silence here would be
            // the failure mode -- a scene that is 99% right looks entirely right.
            if (ParentOutsidePackage > 0)
                yield return $"  !! {ParentOutsidePackage:N0} attach to a component in ANOTHER package - left relative";
            if (Cycles > 0)
                yield return $"  !! {Cycles:N0} attachment cycle(s) - treated as roots";
            // Only the second number is a defect in the output. The first is context for it: a
            // socket on a component that draws nothing costs the export nothing, and reporting the
            // two as one number made A15 look like it had two misplaced meshes when it has none.
            if (SocketsResolved > 0)
                yield return $"  {SocketsResolved:N0} socket attachment(s) resolved to a bone or " +
                             "socket transform and composed";
            if (SocketPlacements > 0)
                yield return $"  !! {SocketPlacements:N0} written placement(s) hang off a socket we " +
                             "could NOT resolve, so they are in the wrong place";
            else if (Sockets > SocketsResolved)
                yield return $"  {Sockets - SocketsResolved:N0} component(s) name an attach SOCKET " +
                             "that neither resolved nor reaches a written placement";
            if (AbsoluteChannels > 0)
                yield return $"  {AbsoluteChannels:N0} component(s) set bAbsolute* - that channel ignores the parent";
        }
    }

    public static List<Placement> Read(IFileProvider provider, string umapPath,
                                       Action<string>? log = null)
        => Read(provider, umapPath, out _, log);

    /// <summary>
    /// A whole World Partition level: the persistent map plus every streaming cell under it.
    ///
    /// **A UE5 level is not one `.umap`.** World Partition cooks the actors into generated
    /// streaming cells beside the map, and the map itself keeps almost nothing — one map is a
    /// 2.6 MB package holding **28 placements**, while one of its 554 cells holds 536. Reading the
    /// map alone and reporting placements looks like it worked and gives an empty level.
    ///
    /// Cells live at `<dir>/<LevelName>/_Generated_/*.umap`, which is a naming convention rather
    /// than something the package declares, so this reports how many it found: nought cells for a
    /// level that plainly has content is the tell that the convention differs in some game.
    /// </summary>
    public static List<string> FindCells(IFileProvider provider, string umapPath)
    {
        var stem = umapPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
            ? umapPath[..^5] : umapPath;
        var prefix = stem + "/_Generated_/";
        return provider.Files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Read the persistent map and all of its streaming cells as one placement set.
    ///
    /// A cell that fails to open is counted and skipped rather than aborting: 554 packages is
    /// enough that one bad package should not cost the other 553, and a silent partial result is
    /// exactly the shape of failure this whole file is written against.
    /// </summary>
    public static List<Placement> ReadWorld(IFileProvider provider, string umapPath,
                                            out ReadStats total, Action<string>? log = null,
                                            bool verify = false, bool includeHlod = false)
    {
        var all = new List<Placement>();
        int comp = 0, att = 0, cmpd = 0, noMesh = 0, outside = 0, cyc = 0, sock = 0, abs = 0;
        int hlod = 0, ismc = 0, isnt = 0, sockPlace = 0, fromLi = 0, sockFixed = 0, pivoted = 0;
        var failed = 0;
        double worstCm = 0, worstDeg = 0;

        void Take(string path, string? cell)
        {
            try
            {
                all.AddRange(Read(provider, path, out var s, null, cell, includeHlod));
                comp += s.Components; att += s.Attached; cmpd += s.Composed; noMesh += s.NoMesh;
                outside += s.ParentOutsidePackage; cyc += s.Cycles;
                sock += s.Sockets; abs += s.AbsoluteChannels;
                hlod += s.HlodSkipped; ismc += s.InstancedComponents; isnt += s.InstanceRows;
                sockPlace += s.SocketPlacements; fromLi += s.FromLevelInstances;
                sockFixed += s.SocketsResolved; pivoted += s.Pivoted;

                // Only where composition actually ran. Cross-checking a package whose components
                // are all roots proves nothing except that copying a value works.
                if (verify && s.Composed > 0)
                {
                    var (cm, deg) = VerifyComposition(provider, path);
                    worstCm = Math.Max(worstCm, cm);
                    worstDeg = Math.Max(worstDeg, deg);
                }
            }
            catch { failed++; }
        }

        Take(umapPath, null);
        var mapOnly = all.Count;

        var cells = FindCells(provider, umapPath);
        log?.Invoke($"  {cells.Count:N0} streaming cell(s) beside the map");
        foreach (var c in cells) Take(c, Path.GetFileNameWithoutExtension(c));

        total = new ReadStats(comp, att, cmpd, noMesh, outside, cyc, sock, abs,
            HlodSkipped: hlod, InstancedComponents: ismc, InstanceRows: isnt,
            SocketPlacements: sockPlace, FromLevelInstances: fromLi, Pivoted: pivoted,
            SocketsResolved: sockFixed);
        if (log is not null)
        {
            log($"  persistent map: {mapOnly:N0} placement(s); cells: {all.Count - mapOnly:N0}");
            foreach (var l in total.ToLines()) log(l);
            if (failed > 0) log($"  !! {failed:N0} cell(s) would not open");
            if (verify)
                // WHAT THE THRESHOLD IS MEASURING. Both sides are float32, and a ring-shaped level puts
                // components at coordinates over 100,000 cm, where float32 can only represent
                // steps of about 0.01 cm -- so a limit of 0.01 measures the NUMBER FORMAT rather
                // than the composition, and the check duly reported a 0.016 cm "disagreement" on a
                // scene both sides agree about. Half a millimetre is still far tighter than any
                // real composition fault: the ones this has actually caught were a 180-degree
                // rotation and a whole-chain offset, not a fifth of a millimetre.
                log(worstCm < CrossCheckCm && worstDeg < 0.01
                    ? $"  composition cross-check: every composed package agrees " +
                      $"({worstCm:0.####} cm / {worstDeg:0.####} deg worst)"
                    : $"  !! composition cross-check DISAGREES: {worstCm:0.###} cm / {worstDeg:0.###} deg worst");
        }
        return all;
    }

    /// <summary>Composed world transforms, parallel to the export list.</summary>
    public sealed record Composed(
        FQuat[] Rot, FVector[] Loc, FVector[] Scale, int[] Depth, int[] Parent,
        string?[] Socket, int ParentOutside, int Cycles, int Sockets, int AbsoluteChannels,
        int SocketsResolved = 0);

    /// <summary>
    /// ONE LINK IN THE CHAIN THAT PUT A THING WHERE IT IS.
    ///
    /// A placement is never one number. It is a component's own transform, composed through
    /// however many parents it hangs off, each of those possibly read from an ARCHETYPE in
    /// another package, possibly hung on a SOCKET, possibly ignoring its parent on one channel.
    /// Five mechanisms, and when a prop lands in the wrong place any one of them can be the
    /// reason.
    ///
    /// Telling them apart by experiment took an hour a time -- hide a thing, rebuild, look, ask
    /// the author whether it moved. This is that hour as a click.
    /// </summary>
    public sealed record Step(
        int Index, string Name, string Type, int Depth,
        // WHICH OBJECT ACTUALLY WROTE EACH CHANNEL. "own" is the export in the level; anything
        // else names the archetype it was inherited from, which is the single most common reason
        // a transform is not where you went looking for it.
        string LocFrom, string RotFrom, string ScaleFrom,
        FVector Loc, FRotator Rot, FVector Scale,
        // The socket this link hangs off its parent, and whether we found it. An UNRESOLVED
        // socket is real damage -- it means the offset silently became zero.
        string? Socket, bool SocketFound, FVector SocketOffset,
        bool AbsLoc, bool AbsRot, bool AbsScale,
        FVector World, FVector WorldScale);

    /// <summary>
    /// WHAT COMPOSITION DID, recorded as it happens.
    ///
    /// Filled by the same pass that produces the picture, never by a second implementation.
    /// A readout computed independently can disagree with what is drawn, and then it is worse
    /// than nothing: it sends you looking for a bug in the half that is right.
    /// </summary>
    public sealed class Trace
    {
        internal FVector[] Loc = [];
        internal FRotator[] Rot = [];
        internal FVector[] Scale = [];
        internal string[] LocFrom = [], RotFrom = [], ScaleFrom = [];
        internal string?[] Socket = [];
        internal bool[] SocketFound = [];
        internal FVector[] SocketOffset = [];
        internal bool[] AbsLoc = [], AbsRot = [], AbsScale = [];

        internal void Size(int n)
        {
            Loc = new FVector[n]; Rot = new FRotator[n]; Scale = new FVector[n];
            LocFrom = new string[n]; RotFrom = new string[n]; ScaleFrom = new string[n];
            Socket = new string?[n]; SocketFound = new bool[n]; SocketOffset = new FVector[n];
            AbsLoc = new bool[n]; AbsRot = new bool[n]; AbsScale = new bool[n];
        }
    }

    /// <summary>
    /// WHERE EVERY PART OF THIS THING'S POSITION CAME FROM, root first.
    ///
    /// Re-composes the package with recording on and walks back up the attach chain from the
    /// component asked about. Re-composing rather than caching a trace for every export is
    /// deliberate: a package of 3,920 exports composes in single-digit milliseconds, and this
    /// runs on a click.
    /// </summary>
    public static List<Step> Provenance(IReadOnlyList<UObject> exports, UObject component)
    {
        var at = -1;
        for (var i = 0; i < exports.Count; i++)
            if (ReferenceEquals(exports[i], component)) { at = i; break; }
        if (at < 0) return [];

        var trace = new Trace();
        var w = ComposeAll(exports, inherit: true, trace);

        // Up to the root, then reversed -- a chain reads outside-in, the way the level is built.
        var chain = new List<int>();
        for (int i = at, guard = 0; i >= 0 && guard < 64; i = w.Parent[i], guard++) chain.Add(i);
        chain.Reverse();

        var steps = new List<Step>(chain.Count);
        foreach (var i in chain)
            steps.Add(new Step(
                i, exports[i].Name ?? "?", exports[i].ExportType ?? "?", w.Depth[i],
                trace.LocFrom[i], trace.RotFrom[i], trace.ScaleFrom[i],
                trace.Loc[i], trace.Rot[i], trace.Scale[i],
                trace.Socket[i], trace.SocketFound[i], trace.SocketOffset[i],
                trace.AbsLoc[i], trace.AbsRot[i], trace.AbsScale[i],
                w.Loc[i], w.Scale[i]));
        return steps;
    }

    /// <summary>
    /// THE FIRST OBJECT IN THE ARCHETYPE CHAIN THAT ACTUALLY SERIALISES THIS PROPERTY.
    ///
    /// A component placed by a Blueprint writes down only what it OVERRIDES -- often just
    /// `AttachParent` and `CreationMethod`. Its mesh and its transform sit on the template it was
    /// made from, one package away. UE calls that the archetype; CUE4Parse hands it over as
    /// `Template`. Reading the export alone made 761 Blueprint actors of SK_Anaxes_020 invisible
    /// in the level editor, and leaves the same hole in anything else that asks an export
    /// directly.
    /// </summary>
    public static UObject? Holder(UObject? e, string name, int depth = 8)
    {
        for (var o = e; o is not null && depth-- > 0; o = Archetype(o))
            if (o.Properties.Any(p => p.Name.Text == name)) return o;
        return null;
    }

    /// <summary>
    /// THE PER-INSTANCE TRANSFORMS, WHICH MAY LIVE ON THE ARCHETYPE.
    ///
    /// `bInheritPerInstanceData` on a cooked ISM means the placed component INHERITS its
    /// instances from the Blueprint's component template and serialises none of its own, so the
    /// level package holds a component with zero instances and UE rebuilds it on spawn. Asking
    /// the export alone reports nothing for a floor made of hundreds of tiles.
    ///
    /// Zero Company's hangar is built this way: 51 `BPP_LI_Hub_Hangar_FloorGroup_A_C` actors,
    /// two ISM components each drawing `SM_Hub_DeckFloor_A` and `_B`, every one of them reading
    /// as a single placement at the actor's origin. That is the misaligned deck floor and the
    /// missing catwalk.
    ///
    /// The same shape as <see cref="Holder"/>, and it needs its own walk because
    /// `PerInstanceSMData` is a TYPED FIELD CUE4Parse deserialises, not a tagged property, so
    /// Holder cannot see it.
    /// </summary>
    public static FInstancedStaticMeshInstanceData[]? InstancesOf(UObject? e, int depth = 8)
    {
        for (var o = e; o is not null && depth-- > 0; o = Archetype(o))
            if (o is UInstancedStaticMeshComponent { PerInstanceSMData: { Length: > 0 } d }) return d;
        return null;
    }

    /// <summary>
    /// THE MATERIALS THIS PLACEMENT USES INSTEAD OF THE MESH'S OWN, by slot index.
    ///
    /// `OverrideMaterials` on a MeshComponent replaces the mesh's material at that slot for this
    /// placement only, and the hub writes it 2,724 times across 82 of its 118 levels. Reading
    /// the mesh alone paints every one of them with its default material, which is the author's
    /// "weird textures being applied to crates and tons of stuff that doesn't get textures" --
    /// a report I dismissed as a base-colour-only viewport before --levelaudit counted it.
    ///
    /// A null entry in the array is UE for "use the mesh's own for this slot", and is kept as
    /// null rather than dropped, because the array is indexed BY SLOT and closing the gaps would
    /// shift every material after it onto the wrong part of the model.
    ///
    /// Through the archetype, like the mesh and the transform: a cooked Blueprint component
    /// writes down only what it overrides.
    /// </summary>
    public static string?[]? OverrideMaterialsOf(UObject? e)
    {
        if (Holder(e, "OverrideMaterials") is not { } holder) return null;
        if (holder.GetOrDefault<FPackageIndex?[]?>("OverrideMaterials", null) is not { Length: > 0 } arr)
            return null;

        var any = false;
        var paths = new string?[arr.Length];
        for (var i = 0; i < arr.Length; i++)
        {
            var path = arr[i] is { IsNull: false } idx ? idx.ResolvedObject?.GetPathName() : null;
            paths[i] = path;
            if (!string.IsNullOrEmpty(path)) any = true;
        }
        return any ? paths : null;
    }

    /// <summary>
    /// WHICH OBJECT A VALUE CAME OFF, in the words the readout uses.
    ///
    /// "own" means the level package states it. Anything else means the level does NOT, and the
    /// value arrived from a template in another package -- which is why editing the level's own
    /// export would not move the thing.
    /// </summary>
    private static string Whose(UObject self, UObject? holder) =>
        holder is null ? "default" : ReferenceEquals(holder, self) ? "own" : holder.Name ?? "archetype";

    private static UObject? Archetype(UObject o)
    {
        try { return o.Template?.Load(); }
        catch { return null; }          // a template in a package we cannot read is not a fault
    }

    /// <summary>
    /// WHERE A SOCKET ACTUALLY IS, in the space of the mesh that owns it.
    ///
    /// A rigged prop is a skeletal mesh plus a hull of StaticMeshComponents, each attached to a
    /// BONE by name. The component carries no offset of its own, because the bone IS the offset:
    /// Zero Company's `cockpit_chair_l` is thirteen metres down a dropship. Composing the
    /// attachment without it stacks 169 different meshes on one point, which is what the author
    /// was looking at when he said two big meshes appeared and he could no longer walk the map.
    /// The LAAT doors are the same thing: sockets literally named `door_back_03_r`.
    ///
    /// Two shapes answer to one `AttachSocketName`, and UE resolves them in this order: a real
    /// SOCKET, which is a bone plus a relative offset and so composes twice; failing that, a
    /// BONE, whose reference pose composed up the bone chain IS the transform. Zero Company's
    /// vehicles are the second kind.
    ///
    /// Only the TABLE is kept, never the mesh. A USkeletalMesh holds its LOD geometry, and
    /// keeping one alive to remember 99 names is the 20 GB the level reader once paid to
    /// remember a material. One book per compose call, so there is no lifetime question and no
    /// cache to invalidate when a different game is mounted.
    /// </summary>
    private sealed class SocketBook
    {
        private readonly Dictionary<string, Dictionary<string, (FQuat R, FVector T, FVector S)>?> _byMesh
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The socket's transform in the parent component's own space, or null.</summary>
        public (FQuat R, FVector T, FVector S)? Find(UObject parent, string socket)
        {
            UObject? mesh;
            try { mesh = MeshOf(parent); }
            catch { return null; }
            if (mesh is null) return null;

            var key = mesh.GetPathName();
            if (!_byMesh.TryGetValue(key, out var table))
            {
                try { table = Build(mesh); }
                catch { table = null; }   // an unreadable rig is a placement we cannot fix, not a crash
                _byMesh[key] = table;
            }
            return table is not null && table.TryGetValue(socket, out var t) ? t : null;
        }

        private static UObject? MeshOf(UObject c)
        {
            // GetSkeletalMesh walks the archetype chain itself, and covers both the pre-5.1
            // `SkeletalMesh` property and `SkinnedAsset`.
            if (c is USkinnedMeshComponent sk && sk.GetSkeletalMesh() is { IsNull: false } si)
                return si.Load();

            // The static mesh may be on the ARCHETYPE, exactly as it is for a placement: a cooked
            // component writes down only what it overrides. Asking the export alone is what hid
            // every Blueprint prop in the game the first time round.
            if (c is UStaticMeshComponent st)
                return (st.GetStaticMesh() is { IsNull: false } sti ? sti : null)?.Load()
                       ?? Holder(c, "StaticMesh")?
                          .GetOrDefault<FPackageIndex?>("StaticMesh", null)?.Load();

            return Holder(c, "SkeletalMesh")?
                   .GetOrDefault<FPackageIndex?>("SkeletalMesh", null)?.Load();
        }

        private static Dictionary<string, (FQuat R, FVector T, FVector S)>? Build(UObject mesh)
        {
            var table = new Dictionary<string, (FQuat R, FVector T, FVector S)>(
                StringComparer.OrdinalIgnoreCase);

            if (mesh is USkeletalMesh skm && skm.ReferenceSkeleton is { } rs &&
                rs.FinalRefBoneInfo.Length > 0 &&
                rs.FinalRefBoneInfo.Length == rs.FinalRefBonePose.Length)
            {
                // BONES FIRST. The reference pose is per-bone and relative to its parent, so it
                // composes up the chain exactly the way an attachment does. UE's bone array is
                // PARENT BEFORE CHILD by contract, so one forward pass does the whole skeleton;
                // a parent index pointing forward is broken data and is treated as a root rather
                // than read out of an array slot that has not been filled in yet.
                //
                // THE TRANSLATION IS NOT MULTIPLIED BY THE PARENT'S SCALE, and that is not an
                // omission. CUE4Parse's FReferenceSkeleton constructor runs AdjustBoneScales,
                // which has already folded the accumulated ancestor scale into every
                // translation, so scaling it again here would apply it twice. Our own bind-pose
                // composition -- FbxMeshExport.ModelSpaceOf, the one --pskcompare checks against
                // a render of the game -- composes rotation and translation only for exactly
                // this reason, and this has to agree with it or a rig places one way in an FBX
                // and another way in a level.
                //
                // Invisible on every rig in Zero Company, because their bone scales are all 1 and
                // the two formulas are then identical. That is what makes it worth writing down:
                // it would have sat here until the first scaled rig, and then looked like a bug
                // in the socket work rather than in the bone maths.
                var n = rs.FinalRefBoneInfo.Length;
                var br = new FQuat[n];
                var bt = new FVector[n];
                var bs = new FVector[n];
                for (var i = 0; i < n; i++)
                {
                    var lp = rs.FinalRefBonePose[i];
                    var p = rs.FinalRefBoneInfo[i].ParentIndex;
                    if (p >= 0 && p < i)
                    {
                        bt[i] = br[p].RotateVector(lp.Translation) + bt[p];
                        br[i] = br[p] * lp.Rotation;
                        bs[i] = bs[p] * lp.Scale3D;
                    }
                    else
                    {
                        br[i] = lp.Rotation;
                        bt[i] = lp.Translation;
                        bs[i] = lp.Scale3D;
                    }
                    table[rs.FinalRefBoneInfo[i].Name.Text] = (br[i], bt[i], bs[i]);
                }

                // A REAL SOCKET WINS OVER A BONE OF THE SAME NAME, which is UE's own order, so
                // the sockets have to be written after the bones rather than before them.
                foreach (var sref in skm.Sockets)
                {
                    if (sref is null || sref.IsNull) continue;
                    if (sref.Load<USkeletalMeshSocket>() is not { } s) continue;
                    var r = FQuat.Identity;
                    var t = FVector.ZeroVector;
                    var k = FVector.OneVector;
                    if (table.TryGetValue(s.BoneName.Text, out var b)) { r = b.R; t = b.T; k = b.S; }
                    table[s.SocketName.Text] = (r * s.RelativeRotation.Quaternion(),
                                                r.RotateVector(k * s.RelativeLocation) + t,
                                                k * s.RelativeScale);
                }
                return table;
            }

            // A STATIC MESH HAS SOCKETS AND NO BONES, so a socket's own offset is the whole
            // transform. Null rather than an empty table for a mesh that declares none, so a
            // caller can tell "no sockets here" from "this name is not one of them".
            if (mesh is UStaticMesh stm)
            {
                foreach (var sref in stm.Sockets)
                {
                    if (sref is null || sref.IsNull) continue;
                    if (sref.Load<UStaticMeshSocket>() is not { } s) continue;
                    table[s.SocketName.Text] = (s.RelativeRotation.Quaternion(),
                                                s.RelativeLocation, s.RelativeScale);
                }
                return table.Count > 0 ? table : null;
            }

            return null;
        }
    }

    /// <summary>
    /// Compose every export's world transform from its attach chain.
    ///
    /// <paramref name="inherit"/> reads each local transform through the ARCHETYPE chain rather
    /// than off the export. Off by default so the exporter keeps the behaviour it was verified
    /// with; the level editor's viewport asks for it, because half the props in a cooked map
    /// carry no transform of their own at all.
    /// </summary>
    public static Composed Compose(IReadOnlyList<UObject> exports, bool inherit = false) =>
        ComposeAll(exports, inherit);

    /// <summary>As above, recording WHERE each channel was read from. See <see cref="Trace"/>.</summary>
    public static Composed Compose(IReadOnlyList<UObject> exports, bool inherit, Trace trace) =>
        ComposeAll(exports, inherit, trace);

    public static List<Placement> Read(IFileProvider provider, string umapPath,
                                       out ReadStats stats, Action<string>? log = null,
                                       string? cell = null, bool includeHlod = false,
                                       int depth = 0,
                                       Dictionary<string, List<Placement>>? instances = null)
    {
        var found = new List<Placement>();
        // GetExports() is lazy, so the enumeration is what touches the cache, not LoadPackage --
        // materialise inside the gate. A umap read is 554 packages on a World Partition level and
        // ran ungated beside anything else holding the provider.
        var exports = AssetLoader.Read(() =>
            provider.LoadPackage(AssetLoader.PackagePath(umapPath)).GetExports().ToList());
        // INHERIT. Half the components in a cooked map carry no transform of their own; the
        // level editor has read them this way since f426d3e and the exporter had the same hole.
        var w = ComposeAll(exports, inherit: true);

        int noMesh = 0, attached = 0, composedCount = 0, components = 0;
        int hlod = 0, instanced = 0, instanceRows = 0, socketPlacements = 0;
        for (var i = 0; i < exports.Count; i++)
        {
            var e = exports[i];
            // Every component that draws a mesh, whatever actor owns it. Matching on the TYPE
            // rather than a name list catches InstancedStaticMeshComponent and the hierarchical
            // variant, which is most of the scenery in a UE5 level.
            //
            // AND SKINNED COMPONENTS, which this excluded outright -- so `MeshOf` learning to
            // resolve a SkeletalMesh changed nothing at all until the gate above it opened. 268
            // SkeletalMeshComponents across 28 hub levels never reached it: the crew stations'
            // weapons, and the LAAT and dropship bodies whose static hull parts we place
            // correctly on their bones. A prop whose parts are right and whose body is missing
            // is what "LAATs floating in the air with their doors flipping around" looks like
            // once the socket offsets are fixed.
            //
            // A level carries no animation state, so the BIND POSE is what gets drawn, and that
            // is where the game puts the prop before anything animates it.
            // ANY COMPONENT, and let `MeshOf` decide whether it draws. Naming the classes was
            // the fault, not the list being short: `LandscapeNaniteComponent` is UE 5's Nanite
            // representation of a whole TERRAIN and resolves an ordinary static mesh, and the
            // string "StaticMeshComponent" is not inside its name -- so the ground of every
            // skirmish map was excluded by a test that never mentioned landscapes. The next
            // component class UE invents would have gone the same way.
            //
            // COMPONENTS ONLY, though, and that part is deliberate. For a cooked Blueprint prop
            // TWO exports answer the same question -- the component carries the mesh and the
            // ACTOR resolves the same mesh through its archetype -- which drew 369 of the hub's
            // props twice at one transform, read as meshes blinking in and out. The editor
            // dedupes by (owner, mesh, position) afterwards; this exporter does not, so the gate
            // is what keeps actors out.
            if (!e.ExportType.Contains("Component", StringComparison.OrdinalIgnoreCase))
                continue;

            // HLOD proxies are not level geometry. UE generates them to draw distant scenery
            // cheaply, so every one is a merged, reduced stand-in for meshes that are ALSO in the
            // level at full detail. Including them double-counts the scene and, because their own
            // transform is often the identity, dumps the copies in a heap at the origin: 2,567
            // of A15's 2,665 origin-clustered placements were these.
            //
            // **Ask the ACTOR, not just the component class.** Testing `ExportType` alone caught
            // 2,794 of A15's proxies and missed 234 more, because UE also emits them as an
            // ORDINARY `StaticMeshComponent` owned by a generated `HLOD_A_<hex>` actor — zero of
            // those 234 match on the class. They are the harder half to notice, too: only 80 sit
            // at the origin, so the other 154 are merged low-detail geometry scattered through the
            // scene on top of the real meshes, which reads as art rather than as duplication.
            //
            // The 13 remaining origin-clustered placements are genuine — the POA bridge exterior
            // and the escape pods really are authored at (0,0,0) — so "at the origin" is not the
            // test and never was.
            var owner = e.Outer?.Name.Text ?? "";
            if (!includeHlod &&
                (e.ExportType.Contains("HLOD", StringComparison.OrdinalIgnoreCase) ||
                 owner.Contains("HLOD", StringComparison.OrdinalIgnoreCase)))
            { hlod++; continue; }

            var meshPath = MeshOf(e);
            if (meshPath is null) { noMesh++; continue; }
            components++;
            if (w.Parent[i] >= 0) attached++;
            if (w.Depth[i] > 0) composedCount++;
            if (w.Socket[i] is not null) socketPlacements++;

            var actor = owner;
            var parentName = w.Parent[i] >= 0 ? exports[w.Parent[i]].Name : null;

            // An instanced component draws its mesh many times from per-instance transforms, and
            // its own transform is frequently the identity. Reading it as ONE placement is both a
            // massive undercount and a wrong position.
            //
            // PerInstanceSMData is a TYPED FIELD on UInstancedStaticMeshComponent, deserialised by
            // CUE4Parse, not a UE property -- `GetOrDefault<...>("PerInstanceSMData")` finds
            // nothing and reports zero instances for a component with thousands.
            // THROUGH THE ARCHETYPE, because a cooked ISM that sets bInheritPerInstanceData
            // carries none of its own -- see InstancesOf.
            var data = InstancesOf(e);
            if (data is { Length: > 0 })
            {
                instanced++;
                instanceRows += data.Length;
                for (var k = 0; k < data.Length; k++)
                {
                    var t = data[k].TransformData;
                    // Instance transforms are relative to the component, so they compose exactly
                    // like an attachment does.
                    var rot = w.Rot[i] * t.Rotation;
                    var scale = w.Scale[i] * t.Scale3D;
                    var loc = w.Rot[i].RotateVector(w.Scale[i] * t.Translation) + w.Loc[i];
                    found.Add(new Placement(actor, e.Name, meshPath, loc, rot.Rotator(), scale,
                        w.Depth[i], parentName, w.Socket[i], cell, e.ExportType, data.Length, k));
                }
                continue;
            }

            found.Add(new Placement(actor, e.Name, meshPath,
                w.Loc[i], w.Rot[i].Rotator(), w.Scale[i],
                w.Depth[i], parentName, w.Socket[i], cell,
                e.ExportType, 1));
        }

        // A LEVEL INSTANCE IS A WHOLE BUILDING IN ANOTHER .UMAP. UE5 lets a structure be authored
        // once as its own level and dropped in as a single actor carrying a `CookedWorldAsset`
        // path; none of its geometry lives in the package that placed it. An export that misses
        // them reports every count correctly and is missing entire buildings -- SK_Anaxes_020
        // drops nineteen.
        var expanded = 0;
        var pivoted = 0;
        if (depth < MaxLevelInstanceDepth)
        {
            instances ??= new Dictionary<string, List<Placement>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < exports.Count; i++)
            {
                if (Holder(exports[i], "CookedWorldAsset") is not { } holder) continue;
                if (WorldAssetPath(holder) is not { } inner) continue;

                // The instance's own placement: its root component's composed transform.
                var root = exports[i].GetOrDefault<FPackageIndex?>("RootComponent", null);
                var at = root is { IsExport: true } && root.Index - 1 < exports.Count ? root.Index - 1 : i;

                if (!instances.TryGetValue(inner, out var inside))
                {
                    try { inside = Read(provider, inner, out _, null, cell, includeHlod, depth + 1, instances); }
                    catch { inside = new List<Placement>(); }
                    instances[inner] = inside;
                }

                // THE PREFAB'S PIVOT, added in ITS space -- before the instance's rotation and
                // scale, never after. A prefab dropped in rotated would otherwise be offset along
                // the world axes instead of its own.
                var pivot = LevelInstancePivot(provider, inner);
                var pivotIsReal = pivot.X != 0 || pivot.Y != 0 || pivot.Z != 0;

                foreach (var p in inside)
                {
                    // Same composition an attachment gets, because that is what this is.
                    var rot = w.Rot[at] * p.Rotation.Quaternion();
                    var scale = w.Scale[at] * p.Scale;
                    var loc = w.Rot[at].RotateVector(w.Scale[at] * (p.Location + pivot)) + w.Loc[at];
                    found.Add(p with { Location = loc, Rotation = rot.Rotator(), Scale = scale });
                    expanded++;
                    if (pivotIsReal) pivoted++;
                }
            }
        }

        stats = new ReadStats(components, attached, composedCount, noMesh,
            w.ParentOutside, w.Cycles, w.Sockets, w.AbsoluteChannels,
            hlod, instanced, instanceRows, socketPlacements, expanded, pivoted, w.SocketsResolved);
        if (log is not null)
        {
            log($"  {exports.Count:N0} export(s) in the package");
            foreach (var l in stats.ToLines()) log(l);
        }
        return found;
    }

    /// <summary>
    /// Compose every export's world transform from its attach chain.
    ///
    /// Factored out because the cross-check needs the SAME composition indexed the same way. The
    /// first version of that check matched placements back to exports by component NAME and
    /// reported a 180-degree error on a scene that was correct — UE reuses component names across
    /// actors freely, so the lookup compared one actor's transform against another's. Identical
    /// mistake to pooling atlas grids by material name.
    /// </summary>
    private const int MaxLevelInstanceDepth = 4;

    /// <summary>
    /// A LEVEL INSTANCE'S CONTENTS ARE OFFSET BY THE PREFAB'S OWN PIVOT, and for months they
    /// were not.
    ///
    /// When a designer makes a Level Instance, the pivot they choose is not where the actors
    /// inside it sit -- the actors stay where they were authored and the difference is written
    /// into the prefab's own world settings as `LevelInstancePivotOffset`. UE applies it on load,
    /// `FTransform(PivotOffset) * LevelTransform`, so a prop at local `p` lands at
    /// `instance.Transform(p + pivot)`. Ignore it and every prop inside that prefab is displaced
    /// by the offset, which in this game runs to FIFTY METRES.
    ///
    /// **Measured, not assumed, because applying an offset the cook had already baked in would
    /// be worse than ignoring it.** `--pivotcheck` settles both questions from the data:
    ///
    ///   * IS IT LIVE? The hub ships each hangar lift as six packages -- one per story state --
    ///     and all six are placed at the IDENTICAL instance transform. `Lift08_A` and `Lift08_F`
    ///     hold the same 79 placements spanning the same local Z 178.0..179.0, and differ only in
    ///     pivot: -175 against -1330. The contents do not carry the height, so the pivot is the
    ///     entire eleven and a half metres of lift travel. Without it we drew all six at one
    ///     height, which is a lift that never moves.
    ///   * WHICH WAY ROUND? Two DIFFERENT lifts agree to 0.0 cm under `local + pivot` and under
    ///     nothing else. Lift08_A: -703.0 + 178.0 + (-175.0) = -700.0. Lift09_A:
    ///     -1228.0 + 178.0 + 350.0 = -700.0. Two decks at one height in one hangar. The other
    ///     sign puts them at -350 and -1400, which agrees with nothing.
    ///
    /// Cached by package path, and the cache holds VALUES -- never a UObject, which is how one
    /// bound section once rooted 20 GB of packages.
    /// </summary>
    public static FVector LevelInstancePivot(IFileProvider provider, string packagePath)
    {
        lock (PivotCache)
            if (PivotCache.TryGetValue(packagePath, out var got)) return got;

        var v = new FVector(0, 0, 0);
        try
        {
            var ex = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(packagePath)).GetExports().ToList());
            foreach (var e in ex)
                if (e.GetOrDefault<FVector?>("LevelInstancePivotOffset", null) is { } found) { v = found; break; }
        }
        catch { /* a prefab we cannot read offsets by nothing, which is what it did before */ }

        lock (PivotCache) PivotCache[packagePath] = v;
        return v;
    }

    private static readonly Dictionary<string, FVector> PivotCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forget the pivots, so a remount does not answer from the previous mount.</summary>
    public static void ForgetPivots() { lock (PivotCache) PivotCache.Clear(); }



    /// <summary>How far two float32 compositions may differ before it means anything. See the note at the report.</summary>
    public const double CrossCheckCm = 0.05;

    /// <summary>
    /// The package a LevelInstance points at. Cooked builds write it as a soft object path, so it
    /// arrives as text rather than as an import to resolve.
    /// </summary>
    private static string? WorldAssetPath(UObject holder)
    {
        var text = holder.GetOrDefault<FSoftObjectPath?>("CookedWorldAsset", null)?.AssetPathName.Text;
        if (string.IsNullOrEmpty(text))
            text = holder.GetOrDefault<FPackageIndex?>("CookedWorldAsset", null)?.ResolvedObject?.GetPathName();
        if (string.IsNullOrEmpty(text)) return null;

        // "/Game/.../Thing.Thing" names the object inside the package; the package is what is in
        // front of the dot.
        var dot = text.LastIndexOf('.');
        return dot > 0 ? text[..dot] : text;
    }

    private static Composed ComposeAll(IReadOnlyList<UObject> exports, bool inherit = false,
                                       Trace? trace = null)
    {
        // Every export, not just the mesh-bearing ones. A StaticMeshComponent frequently hangs off
        // a plain SceneComponent that carries the actual offset, so a table of only mesh
        // components cannot resolve its own parents.
        var n = exports.Count;
        var localRot = new FQuat[n];
        var localLoc = new FVector[n];
        var localScale = new FVector[n];
        var parent = new int[n];
        var absLoc = new bool[n];
        var absRot = new bool[n];
        var absScale = new bool[n];
        // TWO ARRAYS, BECAUSE THEY ANSWER DIFFERENT QUESTIONS. `ownSocket` is the socket this
        // component itself names, and is what gets APPLIED; `socketOf` is the warning, and now
        // carries only the ones we could not resolve. Sharing one array would either apply a
        // parent's socket a second time to every child below it, or go on reporting a placement
        // as misplaced after it had been put right.
        var ownSocket = new string?[n];
        var socketOf = new string?[n];
        var book = new SocketBook();
        int parentOutside = 0, sockets = 0, absAny = 0, socketsResolved = 0;
        trace?.Size(n);

        for (var i = 0; i < n; i++)
        {
            var e = exports[i];

            // `From` is the export itself unless the caller asked to inherit, in which case it is
            // whichever object up the archetype chain actually writes the property down.
            UObject? From(string p) => inherit ? Holder(e, p) : e;

            // Held rather than re-fetched, because the trace has to name the object the value
            // was ACTUALLY read from, and calling From twice would ask the archetype chain twice
            // for the same answer on a path that runs per export.
            var locHolder = From("RelativeLocation");
            var rotHolder = From("RelativeRotation");
            var sclHolder = From("RelativeScale3D");

            localLoc[i] = locHolder?.GetOrDefault("RelativeLocation", FVector.ZeroVector)
                          ?? FVector.ZeroVector;
            var rotator = rotHolder?.GetOrDefault("RelativeRotation", FRotator.ZeroRotator)
                          ?? FRotator.ZeroRotator;
            localRot[i] = rotator.Quaternion();
            localScale[i] = sclHolder?.GetOrDefault("RelativeScale3D", FVector.OneVector)
                            ?? FVector.OneVector;

            if (trace is not null)
            {
                trace.Loc[i] = localLoc[i];
                trace.Rot[i] = rotator;
                trace.Scale[i] = localScale[i];
                trace.LocFrom[i] = Whose(e, locHolder);
                trace.RotFrom[i] = Whose(e, rotHolder);
                trace.ScaleFrom[i] = Whose(e, sclHolder);
            }

            parent[i] = -1;
            if ((From("AttachParent") ?? e).GetOrDefault<FPackageIndex?>("AttachParent", null)
                is { IsNull: false } ap)
            {
                // Index > 0 is the export map, and the array index is Index - 1. A NEGATIVE index
                // is an import — a component in a different package — which this cannot resolve
                // and must not silently treat as a root.
                if (ap.IsExport && ap.Index - 1 < n) parent[i] = ap.Index - 1;
                else parentOutside++;
            }

            // Per-channel, and per-channel is not pedantry: a component can inherit its parent's
            // position while keeping world rotation, which is how billboards and some lights are
            // built. Composing all three would rotate them with the parent.
            absLoc[i] = From("bAbsoluteLocation")?.GetOrDefault("bAbsoluteLocation", false) ?? false;
            absRot[i] = From("bAbsoluteRotation")?.GetOrDefault("bAbsoluteRotation", false) ?? false;
            absScale[i] = From("bAbsoluteScale")?.GetOrDefault("bAbsoluteScale", false) ?? false;
            if (absLoc[i] || absRot[i] || absScale[i]) absAny++;

            var sock = (From("AttachSocketName") ?? e).GetOrDefault("AttachSocketName", new FName()).Text;
            if (!string.IsNullOrEmpty(sock) && sock != "None") { ownSocket[i] = sock; sockets++; }

            if (trace is not null)
            {
                trace.AbsLoc[i] = absLoc[i];
                trace.AbsRot[i] = absRot[i];
                trace.AbsScale[i] = absScale[i];
                trace.Socket[i] = ownSocket[i];
            }
        }

        // Compose, memoised, with a cycle guard. Depth is carried out because it is the cheapest
        // way to see at a glance whether composition did anything at all.
        var worldRot = new FQuat[n];
        var worldLoc = new FVector[n];
        var worldScale = new FVector[n];
        var depth = new int[n];
        var done = new bool[n];
        var onStack = new bool[n];
        var cycles = 0;

        void Compose(int i)
        {
            if (done[i]) return;
            var p = parent[i];
            if (p < 0 || onStack[p] || p == i)
            {
                // A cycle cannot happen in valid data and does happen in cooked data. Treat the
                // component as a root rather than recursing forever, and SAY so.
                if (p >= 0 && (onStack[p] || p == i)) cycles++;
                worldRot[i] = localRot[i];
                worldLoc[i] = localLoc[i];
                worldScale[i] = localScale[i];
                depth[i] = 0;
                // There is no parent to hang a socket off, so a socket named here is damage we
                // cannot repair and has to keep being reported.
                socketOf[i] = ownSocket[i];
                done[i] = true;
                return;
            }

            onStack[i] = true;
            Compose(p);
            onStack[i] = false;

            // THE PARENT'S TRANSFORM, PLUS THE SOCKET THE CHILD HANGS OFF IT.
            //
            // A socket is just another link in the chain, so this is the same three lines run
            // twice: once to carry the socket out into world space, once to put the child on it.
            // Order matters inside the first pass, because the translation has to be built from
            // the parent's own rotation and scale BEFORE either is folded into the socket's.
            var pr = worldRot[p];
            var pt = worldLoc[p];
            var ps = worldScale[p];
            var onSocket = false;
            if (ownSocket[i] is { } sname && book.Find(exports[p], sname) is { } so)
            {
                pt = pr.RotateVector(ps * so.T) + pt;
                pr *= so.R;
                ps *= so.S;
                onSocket = true;
                socketsResolved++;
                if (trace is not null) { trace.SocketFound[i] = true; trace.SocketOffset[i] = so.T; }
            }

            // UE's FTransform composition, split per channel so bAbsolute* can be honoured:
            //   Q(world) = Q(parent) * Q(local)
            //   S(world) = S(parent) * S(local)
            //   T(world) = Q(parent) . (S(parent) * T(local)) + T(parent)
            worldRot[i] = absRot[i] ? localRot[i] : pr * localRot[i];
            worldScale[i] = absScale[i] ? localScale[i] : ps * localScale[i];
            worldLoc[i] = absLoc[i]
                ? localLoc[i]
                : pr.RotateVector(ps * localLoc[i]) + pt;
            depth[i] = depth[p] + 1;

            // An UNRESOLVED socket ANYWHERE up the chain displaces everything below it, not just
            // the component that names one. Carrying the flag down is what makes the warning mean
            // something: A15 reports two socket attachments and **neither reaches a placement we
            // write**, so the count was measuring socket usage in the export table rather than
            // damage to the output. Without this there is no way to tell those two cases apart,
            // and they call for completely different work.
            //
            // A socket we DID resolve is not damage, and must not be carried down: leaving it in
            // is how a fixed placement goes on reporting itself broken, which is the shape of
            // every check that cannot fail.
            if (!onSocket && ownSocket[i] is not null) socketOf[i] = ownSocket[i];
            if (socketOf[i] is null && !absLoc[i]) socketOf[i] = socketOf[p];
            done[i] = true;
        }

        for (var i = 0; i < n; i++) Compose(i);

        return new Composed(worldRot, worldLoc, worldScale, depth, parent, socketOf,
            parentOutside, cycles, sockets, absAny, socketsResolved);
    }

    /// <summary>
    /// Recompute every composition with an independent implementation and report the worst
    /// disagreement, in centimetres and degrees.
    ///
    /// Worth the duplication for one specific reason: CUE4Parse's own `FQuat` multiply carries the
    /// comment *"both yield different results idk why"* over its SSE and scalar paths, and the
    /// whole scene hangs off that operator. A composition that is subtly wrong puts every child a
    /// small distance from where it belongs, which looks like art rather than like a bug.
    ///
    /// <see cref="System.Numerics.Quaternion"/> has no such ambiguity, so agreement between the
    /// two is evidence; agreement of the code with itself would not be.
    /// </summary>
    public static (double WorstCm, double WorstDeg) VerifyComposition(
        IFileProvider provider, string umapPath)
    {
        var exports = AssetLoader.Read(() =>
            provider.LoadPackage(AssetLoader.PackagePath(umapPath)).GetExports().ToList());
        var n = exports.Count;

        var locQ = new System.Numerics.Quaternion[n];
        var locT = new System.Numerics.Vector3[n];
        var locS = new System.Numerics.Vector3[n];
        var parent = new int[n];
        var abs = new (bool L, bool R, bool S)[n];

        // THE TABLE IS SHARED, THE ARITHMETIC IS NOT, and that split is the whole point of this
        // function. What it exists to cross-check is the COMPOSITION, because CUE4Parse's FQuat
        // multiply has two paths that disagree; where a bone SITS is data, not arithmetic, and
        // reading it twice would only check that a dictionary lookup is deterministic. Compose it
        // here in System.Numerics, off the same book.
        //
        // Leaving sockets out of this side altogether was the live trap: ComposeAll would place a
        // dropship correctly, this would place it in a heap, and the check would cry wolf at
        // thirteen metres on a scene that had just been put right. Exactly what happened the last
        // time the two sides were given different instructions.
        var ownSock = new string?[n];
        var book = new SocketBook();

        for (var i = 0; i < n; i++)
        {
            var e = exports[i];

            // SAME INPUTS, TWO ARITHMETICS. This side reads through the archetype chain as well,
            // because what it exists to cross-check is the COMPOSITION -- CUE4Parse's own FQuat
            // multiply carries the comment "both yield different results idk why" over its SSE
            // and scalar paths, and the whole scene hangs off that operator. Giving the two sides
            // different property lookups would make it report a disagreement that is only the two
            // having been asked different questions: it did, the moment ComposeAll learned to
            // inherit, at 1,557 cm and 145 degrees on a scene that was fine.
            var l = Holder(e, "RelativeLocation")?.GetOrDefault("RelativeLocation", FVector.ZeroVector)
                    ?? FVector.ZeroVector;
            var r = Holder(e, "RelativeRotation")?.GetOrDefault("RelativeRotation", FRotator.ZeroRotator)
                    ?? FRotator.ZeroRotator;
            var s = Holder(e, "RelativeScale3D")?.GetOrDefault("RelativeScale3D", FVector.OneVector)
                    ?? FVector.OneVector;
            locT[i] = new((float) l.X, (float) l.Y, (float) l.Z);
            locS[i] = new((float) s.X, (float) s.Y, (float) s.Z);
            locQ[i] = FromRotator(r.Pitch, r.Yaw, r.Roll);
            parent[i] = -1;
            if ((Holder(e, "AttachParent") ?? e).GetOrDefault<FPackageIndex?>("AttachParent", null)
                is { IsNull: false } ap && ap.IsExport && ap.Index - 1 < n) parent[i] = ap.Index - 1;
            abs[i] = (Holder(e, "bAbsoluteLocation")?.GetOrDefault("bAbsoluteLocation", false) ?? false,
                      Holder(e, "bAbsoluteRotation")?.GetOrDefault("bAbsoluteRotation", false) ?? false,
                      Holder(e, "bAbsoluteScale")?.GetOrDefault("bAbsoluteScale", false) ?? false);
            var sk = (Holder(e, "AttachSocketName") ?? e)
                     .GetOrDefault("AttachSocketName", new FName()).Text;
            if (!string.IsNullOrEmpty(sk) && sk != "None") ownSock[i] = sk;
        }

        var wq = new System.Numerics.Quaternion[n];
        var wt = new System.Numerics.Vector3[n];
        var ws = new System.Numerics.Vector3[n];
        var done = new bool[n];
        var stack = new bool[n];

        void Go(int i)
        {
            if (done[i]) return;
            var p = parent[i];
            if (p < 0 || stack[p] || p == i)
            { wq[i] = locQ[i]; wt[i] = locT[i]; ws[i] = locS[i]; done[i] = true; return; }
            stack[i] = true; Go(p); stack[i] = false;

            var pq = wq[p];
            var pt = wt[p];
            var pscale = ws[p];
            if (ownSock[i] is { } sname && book.Find(exports[p], sname) is { } so)
            {
                var st = new System.Numerics.Vector3((float) so.T.X, (float) so.T.Y, (float) so.T.Z);
                var ss = new System.Numerics.Vector3((float) so.S.X, (float) so.S.Y, (float) so.S.Z);
                var sq = new System.Numerics.Quaternion(so.R.X, so.R.Y, so.R.Z, so.R.W);
                pt = System.Numerics.Vector3.Transform(pscale * st, pq) + pt;
                pq = System.Numerics.Quaternion.Multiply(pq, sq);
                pscale *= ss;
            }

            wq[i] = abs[i].R ? locQ[i] : System.Numerics.Quaternion.Multiply(pq, locQ[i]);
            ws[i] = abs[i].S ? locS[i] : pscale * locS[i];
            wt[i] = abs[i].L ? locT[i]
                  : System.Numerics.Vector3.Transform(pscale * locT[i], pq) + pt;
            done[i] = true;
        }
        for (var i = 0; i < n; i++) Go(i);

        // BY INDEX, against the same composition Read uses. Never by name.
        // The cross-check must ask the SAME question, or it reports a disagreement that is
        // only the two sides having been given different instructions.
        var theirs = ComposeAll(exports, inherit: true);
        double worstCm = 0, worstDeg = 0;
        for (var i = 0; i < n; i++)
        {
            var a = new System.Numerics.Vector3(
                (float) theirs.Loc[i].X, (float) theirs.Loc[i].Y, (float) theirs.Loc[i].Z);
            worstCm = Math.Max(worstCm, System.Numerics.Vector3.Distance(a, wt[i]));

            var b = new System.Numerics.Quaternion(
                theirs.Rot[i].X, theirs.Rot[i].Y, theirs.Rot[i].Z, theirs.Rot[i].W);
            var q = wq[i];
            if (System.Numerics.Quaternion.Dot(b, q) < 0) q = -q;   // double cover
            var rel = System.Numerics.Quaternion.Conjugate(b) * q;
            var deg = float.RadiansToDegrees(2f * MathF.Atan2(
                new System.Numerics.Vector3(rel.X, rel.Y, rel.Z).Length(), MathF.Abs(rel.W)));
            if (float.IsFinite(deg)) worstDeg = Math.Max(worstDeg, deg);
        }
        return (worstCm, worstDeg);
    }

    /// <summary>UE's rotator-to-quaternion, in System.Numerics. Public because the level editor's
    /// live preview needs the SAME one -- a second copy is how two halves of a rotation drift.</summary>
    public static System.Numerics.Quaternion FromRotator(float pitch, float yaw, float roll)
    {
        // UE applies yaw about Z, pitch about Y, roll about X, and its quaternion for a rotator is
        // built with these exact half-angle products.
        const float d2r = MathF.PI / 180f;
        var (sp, cp) = MathF.SinCos(pitch * d2r * 0.5f);
        var (sy, cy) = MathF.SinCos(yaw * d2r * 0.5f);
        var (sr, cr) = MathF.SinCos(roll * d2r * 0.5f);
        return new System.Numerics.Quaternion(
            cr * sp * sy - sr * cp * cy,
            -cr * sp * cy - sr * cp * sy,
            cr * cp * sy - sr * sp * cy,
            cr * cp * cy + sr * sp * sy);
    }

    /// <summary>The asset a component draws, as a /Game path.</summary>
    /// <summary>
    /// A MATERIAL NAMED BY OverrideMaterials, WHICH MAY NOT BE AN ASSET AT ALL.
    ///
    /// 228 of the hub's 559 override materials are SUBOBJECTS inside a level package rather than
    /// standalone assets:
    ///
    ///   /Game/.../LI_Station_Cly_ON.LI_Station_Cly_ON:PersistentLevel
    ///       .BP_LGT_Glow_C_0.LightMesh.LightMeshMaterial
    ///
    /// `LoadPackageObject` splits at the LAST dot, so it reads everything up to `.LightMesh` as a
    /// package path and throws KeyNotFoundException on a file that does not exist. The package is
    /// the part before the FIRST dot after the last slash; the rest names an object inside it.
    ///
    /// That was 229 of the viewport's 304 binds failing, every one of them silently falling back
    /// to the mesh's own material - which is why the crew stations' glow lamps drew lit when the
    /// game paints them `MI_Emissive_Off_4500_Lumen_Off`.
    ///
    /// Direct load FIRST, because 330 of them are ordinary assets and the fallback costs a
    /// package read. Measured across the hub: 558 of 559 resolve, and the one that does not is
    /// `/BitReactorCore/Materials/M_Green`, which is simply not in the shipped paks.
    /// </summary>
    public static UMaterialInterface? LoadMaterial(IFileProvider provider, string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            if (provider.LoadPackageObject(path) is UMaterialInterface direct) return direct;
        }
        catch { /* a subobject path throws here; that is what the fallback is for */ }

        try
        {
            var slash = path.LastIndexOf('/');
            var dot = path.IndexOf('.', slash + 1);
            if (dot <= 0) return null;

            var leaf = path[(path.LastIndexOf('.') + 1)..];
            return provider.LoadPackage(path[..dot]).GetExports()
                           .FirstOrDefault(x => string.Equals(x.Name, leaf, StringComparison.Ordinal))
                   as UMaterialInterface;
        }
        catch { return null; }
    }

    /// <summary>The mesh an export draws, for callers outside this file.</summary>
    public static string? MeshPathOf(UObject export) => MeshOf(export);

    private static string? MeshOf(UObject export)
    {
        if (export is UStaticMeshComponent smc && smc.GetStaticMesh() is { } lazy &&
            lazy.ResolvedObject is { } resolved)
            return resolved.GetPathName();

        // THROUGH THE ARCHETYPE. A component placed by a Blueprint serialises only what it
        // overrides, so the mesh is usually one package away on the template -- asking the export
        // alone silently drops every Blueprint-placed prop in the level. Falling back to the raw
        // property as well, because not every component type resolves through the typed accessor.
        if (Holder(export, "StaticMesh")?
            .GetOrDefault<FPackageIndex?>("StaticMesh", null)?.ResolvedObject?.GetPathName()
            is { Length: > 0 } sm) return sm;

        // A SKINNED PROP IS STILL A PROP, and this asked for `StaticMesh` and nothing else -- so
        // 268 SkeletalMeshComponents across 28 hub levels were counted as "no mesh" and dropped.
        //
        // That is not an edge case here: a rigged prop in this game is a skeletal mesh PLUS a
        // hull of StaticMeshComponents attached to its bones, so the socket fix put the LAAT's
        // doors in the right place around a LAAT that was never drawn at all. His report of
        // "LAATs floating in the air with their doors flipping around" was two faults, and this
        // is the second.
        //
        // `SkinnedAsset` is the UE 5.1-and-later name and `SkeletalMesh` the older one; cooked
        // content in this game writes both, so both are asked for. The bind pose is what gets
        // drawn -- a level carries no animation state, and a prop at its bind pose is right
        // where the game puts it before anything animates it.
        // THROUGH CUE4Parse'S OWN ACCESSOR, not a hand-rolled property read. `USkinnedMeshComponent
        // .GetSkeletalMesh()` already tries `SkeletalMesh`, falls back to the 5.1 name
        // `SkinnedAsset`, and walks the Template chain -- the same three things the static path
        // needs. A copy of that walk written here read the property and came back empty, and a
        // second implementation of a lookup is how this file's oldest bugs happened.
        if (export is USkinnedMeshComponent skinned &&
            skinned.GetSkeletalMesh() is { IsNull: false } skRef &&
            skRef.ResolvedObject?.GetPathName() is { Length: > 0 } sk) return sk;

        // And the raw property for anything that is not typed as a skinned component -- a
        // Blueprint-generated class arrives as a bare UObject.
        foreach (var name in new[] { "SkeletalMesh", "SkinnedAsset" })
            if (Holder(export, name) is { } h &&
                h.GetOrDefault<FPackageIndex?>(name, null)?.ResolvedObject?.GetPathName()
                    is { Length: > 0 } sk2) return sk2;

        return null;
    }

    /// <summary>
    /// TSV, because the consumer is a DCC script rather than this tool. Location and scale in the
    /// game's own units and axes — no conversion here, since the meshes exported alongside are in
    /// the same space and converting one but not the other is how a scene ends up mirrored.
    /// </summary>
    public static IEnumerable<string> ToTsv(IEnumerable<Placement> placements)
    {
        var inv = CultureInfo.InvariantCulture;
        yield return string.Join('\t', "actor", "component", "mesh",
            "x", "y", "z", "pitch", "yaw", "roll", "sx", "sy", "sz",
            "depth", "attachedTo", "socket", "cell", "type", "instances", "instance");
        foreach (var p in placements)
            yield return string.Join('\t', p.Actor, p.Component, p.MeshPath,
                p.Location.X.ToString("0.###", inv), p.Location.Y.ToString("0.###", inv),
                p.Location.Z.ToString("0.###", inv),
                p.Rotation.Pitch.ToString("0.###", inv), p.Rotation.Yaw.ToString("0.###", inv),
                p.Rotation.Roll.ToString("0.###", inv),
                p.Scale.X.ToString("0.####", inv), p.Scale.Y.ToString("0.####", inv),
                p.Scale.Z.ToString("0.####", inv),
                p.Depth.ToString(inv), p.AttachedTo ?? "", p.Socket ?? "", p.Cell ?? "",
                p.Type, p.Instances.ToString(inv), p.Instance.ToString(inv));
    }
}

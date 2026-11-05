namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.UObject;

/// <summary>
/// WHICH BONES A MESH IS SIMULATED ON, and whether the file replacing it still moves them.
///
/// THE REPORT THIS EXISTS FOR. A mod author imported their own lekku, and said it "imported just
/// fine and was okay ingame but didn't have the physics like the ones the game itself ships".
/// Nothing had failed: the mesh copied correctly, the part kept its anim class, the bone map came
/// out identical. Their lekku simply hung there.
///
/// HOW THIS GAME SWINGS SOMETHING. Not cloth -- a bone chain:
///
///     SK_HAAM_Twi_T01     PhysicsAsset             -> PHYS_Twi_T01
///                         PostProcessAnimBlueprint -> ABP_Twi_T01_PostProcess_C
///
/// `PHYS_Twi_T01` holds 17 bodies. `spine_04`, `neck_01`, `head` and both `upperarm`s are
/// Kinematic -- they follow the animation and are what the rest hangs from -- and `Lekku_02_l`
/// through `Lekku_07_r` are Simulated. The post-process anim blueprint is one AnimNode_RigidBody,
/// and it runs on every mesh that names it, whatever the mesh's own skeleton is called: a Twi'lek
/// HELMET (SKEL_Twi_T01_Helm) points at the head's pair and works, so the bodies are matched BY
/// BONE NAME and nothing else. 113 physics assets in this game work this way -- lekku, montrals,
/// Weequay braids, 14 hairstyles, hero capes, Moira's bag and keys.
///
/// SO THE FAILURE IS SILENT AND HAS ONE CAUSE. Sculpt lekku, weight them to `head` because that
/// is the bone under them, and the simulation still runs perfectly -- on bones no vertex follows.
/// Every check the build already had looks the other way round: <see cref="MeshImport"/> warns
/// when the FILE names a bone the TEMPLATE lacks, and says nothing when the template has physics
/// bones the file ignores. That is exactly this case, and it is why nobody was told.
///
/// This is a note, never a refusal. A mesh that deliberately drops a chain -- a shorter lekku, a
/// bald head where a ponytail was -- is a legitimate thing to build, and the author is the one who
/// knows which they meant.
/// </summary>
public static class PhysicsBones
{
    /// <summary>One body in a physics asset: the bone it is tied to, and whether it simulates.</summary>
    public sealed record Body(string Bone, bool Simulated);

    /// <summary>
    /// The physics asset a skeletal mesh names, as a package path, or "".
    ///
    /// Soft-typed on purpose. Everything here has to survive a mesh with no physics at all, which
    /// is the overwhelming majority -- 113 physics assets against thousands of meshes.
    /// </summary>
    public static string AssetOf(UObject? mesh)
    {
        if (mesh is null) return "";
        try
        {
            var path = mesh.GetOrDefault<FPackageIndex?>("PhysicsAsset", null)
                          ?.ResolvedObject?.GetPathName();
            return string.IsNullOrEmpty(path) ? "" : Package(path);
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Whether a mesh names a post-process anim blueprint -- what RUNS the simulation.</summary>
    public static string AnimBlueprintOf(UObject? mesh)
    {
        if (mesh is null) return "";
        try
        {
            var path = mesh.GetOrDefault<FPackageIndex?>("PostProcessAnimBlueprint", null)
                          ?.ResolvedObject?.GetPathName();
            return path ?? "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// Every body in a physics asset, read by walking the package's EXPORTS rather than by
    /// resolving its SkeletalBodySetups array.
    ///
    /// The bodies are top-level exports of the physics asset's own package -- `PHYS_Twi_T01`
    /// carries 17 of them beside the UPhysicsAsset itself -- so anything holding both a `BoneName`
    /// and a `PhysicsType` is one. Walking exports costs the same read and does not depend on the
    /// array property being resolvable, which is the half most likely to change between engine
    /// versions.
    /// </summary>
    public static IReadOnlyList<Body> Bodies(IFileProvider provider, string physicsAssetPackage)
    {
        if (string.IsNullOrEmpty(physicsAssetPackage)) return [];
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(physicsAssetPackage)).GetExports().ToList());

            var bodies = new List<Body>();
            foreach (var e in exports)
            {
                if (Text(e, "BoneName") is not { Length: > 0 } bone) continue;
                // ABSENT IS NOT SIMULATED. UE's default for this property is PhysType_Default,
                // which means "ask the owner" -- so a body that does not say is not evidence that
                // anything swings, and claiming it would produce a warning nobody can act on.
                var type = Text(e, "PhysicsType") ?? "";
                bodies.Add(new Body(bone, type.Contains("Simulated", StringComparison.OrdinalIgnoreCase)));
            }
            return bodies;
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// What to say about an import that has just replaced <paramref name="mesh"/>, or nothing.
    ///
    /// TWO MEASURES, and getting either wrong makes this noise rather than help.
    ///
    /// The reference set is not the physics asset's whole body list -- it is the bones the mesh
    /// BEING REPLACED moves, which is <paramref name="templateBones"/>, the union of its own
    /// section bone maps. PHYS_Twi_T01 simulates twelve lekku bones; SK_HAAF_Twi_T01_HelmBase
    /// covers the top of the lekku and reaches five. Measured against twelve, a round trip of the
    /// template's own geometry is told it lost seven bones that were never its to lose -- which is
    /// a warning on a file that is perfect, and the fastest way to teach someone to ignore these.
    ///
    /// The author's side is measured by WEIGHT and not by cluster: an FBX can carry a cluster for
    /// a bone and put zero on every vertex of it, which moves exactly as little as having none.
    /// </summary>
    public static IEnumerable<string> Notes(IFileProvider provider, UObject? mesh,
                                            IReadOnlyCollection<string> templateBones,
                                            IEnumerable<FbxReader.Mesh> fbx)
    {
        var assetPackage = AssetOf(mesh);
        if (assetPackage.Length == 0) yield break;

        var simulated = Bodies(provider, assetPackage).Where(b => b.Simulated)
                                                      .Select(b => b.Bone).ToList();
        if (simulated.Count == 0) yield break;

        // What the template itself moves. A physics asset can name bodies for bones this
        // particular mesh has no geometry on -- a shared one covers several meshes -- and those
        // are not the author's business.
        var declared = new HashSet<string>(templateBones, StringComparer.OrdinalIgnoreCase);
        var driven = simulated.Where(declared.Contains).ToList();
        if (driven.Count == 0) yield break;

        var asset = Leaf(assetPackage);

        var weighted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in fbx)
            foreach (var cluster in m.Influences)
                if (cluster.Weights.Any(w => w > 0.0001))
                    weighted.Add(cluster.Bone);

        var idle = driven.Where(b => !weighted.Contains(b)).ToList();

        // WEIGHTED ON ALL OF THEM, which used to be the silent case and is half of the trouble.
        //
        // This reader was written for an author who WANTED the swing and did not get it, so it
        // only ever spoke when weight was MISSING. The other direction costs just as much an
        // evening: an author building something rigid on a base mesh that happens to carry a
        // chain gets a piece that simulates, and every check passes. Reported 2026-09-11 --
        // "most of it came from using an object that had built in physics bones within the
        // skeleton, once I swapped over to another base mesh everything started working
        // perfectly" -- diagnosed by the author himself, after the evening.
        //
        // A note and not a warning: for a lekku or a cape this is the intended outcome, and the
        // sentence says so before it says the other thing.
        if (idle.Count == 0)
        {
            yield return
                $"{asset} swings the mesh you replaced on {driven.Count} bone(s) ({Some(driven)}) " +
                "and your file puts weight on all of them, so this piece SIMULATES rather than " +
                "following the animation -- right for hair, a lekku or a cape. If it is meant to " +
                "be rigid, build it on a mesh with no physics asset: weight on these bones is " +
                "what makes it swing, however the shape is modelled.";
            yield break;
        }

        if (idle.Count == driven.Count)
        {
            yield return
                $"the mesh you replaced is PHYSICS-DRIVEN -- {asset} swings it on {driven.Count} " +
                $"bone(s) ({Some(driven)}) -- and your file puts weight on none of them, so " +
                "nothing will move. Skin the part that should swing to those bones, in your own " +
                "tool, and it will behave the way the game's does.";
            yield break;
        }

        yield return
            $"{asset} swings the mesh you replaced on {driven.Count} bone(s), and your file " +
            $"weights {driven.Count - idle.Count} of them: {Some(idle)} " +
            $"carr{(idle.Count == 1 ? "ies" : "y")} no weight and will not move. Deliberate if " +
            "your shape is shorter than the one it replaces.";
    }

    /// <summary>
    /// The bones a cooked LOD puts REAL WEIGHT on, which is not the same as the bones its sections
    /// declare.
    ///
    /// This has to be measured the same way on both sides or the comparison is not one. Using the
    /// section bone map as the template's answer and real weights as the author's produced a
    /// warning on a round trip of the game's own geometry: SK_HAAF_Twi_T01_HelmBase's bone map
    /// lists seven simulated lekku bones and only five of them carry weight, so a perfect copy was
    /// told it had lost two. Both sides count weight now.
    ///
    /// A vertex's BoneIndex is an index into ITS SECTION's bone map, not into the skeleton, so the
    /// vertices have to be walked per section -- reading them against section zero's map would
    /// name the wrong bones on every mesh with more than one section.
    /// </summary>
    public static IReadOnlyCollection<string> Weighted(FStaticLODModel? lod, FMeshBoneInfo[] skeleton)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lod?.Sections is not { Length: > 0 } sections) return names;
        if (Vertices(lod) is not { Length: > 0 } verts) return names;

        foreach (var sec in sections)
        {
            if (sec.BoneMap is not { Length: > 0 } map) continue;
            var from = (int) sec.BaseVertexIndex;
            var to = Math.Min(from + sec.NumVertices, verts.Length);
            for (var v = Math.Max(0, from); v < to; v++)
            {
                if (verts[v]?.Infs is not { } inf) continue;
                var n = Math.Min(inf.BoneIndex.Length, inf.BoneWeight.Length);
                for (var k = 0; k < n; k++)
                {
                    if (inf.BoneWeight[k] == 0) continue;
                    var slot = inf.BoneIndex[k];
                    if (slot >= map.Length) continue;
                    var bone = map[slot];
                    if (bone < skeleton.Length) names.Add(skeleton[bone].Name.Text);
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Whichever of the four vertex forms this LOD was cooked in. CUE4Parse copies the separate
    /// skin weight buffer onto the vertices it filled, and fills exactly one of these.
    /// </summary>
    private static FSkelMeshVertexBase[]? Vertices(FStaticLODModel lod)
    {
        var b = lod.VertexBufferGPUSkin;
        if (b is null) return null;
        if (b.VertsFloat is { Length: > 0 }) return b.VertsFloat;
        if (b.VertsHalf is { Length: > 0 }) return b.VertsHalf;
        if (b.VertsFloatPacked is { Length: > 0 }) return b.VertsFloatPacked;
        if (b.VertsHalfPacked is { Length: > 0 }) return b.VertsHalfPacked;
        return null;
    }

    /// <summary>A few names and a count, because a chain of sixteen is unreadable in full.</summary>
    private static string Some(IReadOnlyList<string> names) =>
        names.Count <= 4
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

    private static string? Text(UObject export, string property)
    {
        foreach (var p in export.Properties)
            if (p.Name.Text.Equals(property, StringComparison.OrdinalIgnoreCase))
                return p.Tag?.GenericValue?.ToString();
        return null;
    }

    /// <summary>`/Game/A/B.B` -> `/Game/A/B`, leaving a path that is already one alone.</summary>
    private static string Package(string objectPath)
    {
        var dot = objectPath.LastIndexOf('.');
        var slash = objectPath.LastIndexOf('/');
        return dot > slash ? objectPath[..dot] : objectPath;
    }

    private static string Leaf(string path) =>
        path.Length == 0 ? path : path[(path.LastIndexOf('/') + 1)..];
}

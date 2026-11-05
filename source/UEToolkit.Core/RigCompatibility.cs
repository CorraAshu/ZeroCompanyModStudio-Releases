using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Animation;

namespace UEToolkit.Core;

/// <summary>
/// WHICH SKELETON'S ANIMATIONS WILL PLAY ON THIS MESH.
///
/// A mesh names one USkeleton asset, and the obvious thing to do is offer the animations that
/// target it. For this game that offers nothing at all: every armour kit ships its OWN skeleton
/// -- SK_HAAF_Clo001_TORS points at SKEL_HAA_Clo001 -- and not one animation targets any of them.
/// All 1,734 humanoid animations target SKEL_Humanoid_01, an asset no wearable part references.
/// Measured, not assumed: 4,556 sequences across 64 skeletons, and the armour kits hold 5 between
/// them.
///
/// So the question is not which asset an animation names. It is whether its BONES line up, which
/// is the only thing playback actually depends on: <see cref="UEToolkit.App"/>'s track map is
/// built by bone NAME, so a sequence authored on one rig drives an identically-named rig exactly
/// as it drives its own. Kit skeletons are copies of the humanoid rig, so they do.
///
/// The comparison is one-directional on purpose. What matters is how much of THIS rig the
/// candidate can drive; a candidate with extra bones this mesh does not have loses nothing,
/// because a track with no bone to write to is simply not read.
/// </summary>
public static class RigCompatibility
{
    /// <param name="Shared">Bones of the mesh's rig that the candidate skeleton also has.</param>
    /// <param name="Total">Bones the mesh's rig has.</param>
    public sealed record Match(string SkeletonPath, int Shared, int Total)
    {
        public float Fraction => Total > 0 ? (float) Shared / Total : 0f;
        public string Leaf
        {
            get
            {
                var dot = SkeletonPath.LastIndexOf('.');
                return dot >= 0 ? SkeletonPath[(dot + 1)..] : SkeletonPath;
            }
        }
    }

    private static readonly Dictionary<string, string[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>
    /// The bone names of a USkeleton asset, cached.
    ///
    /// There are only a few dozen skeletons in a game against thousands of sequences, which is
    /// what makes this affordable: the alternative -- asking each animation whether it fits -- is
    /// the same question multiplied by four and a half thousand package loads.
    /// </summary>
    public static IReadOnlyList<string> BonesOf(IFileProvider provider, string skeletonPath)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(skeletonPath, out var got)) return got;
            Cache[skeletonPath] = [];   // a failure is remembered too, or it is retried every frame
        }

        string[] names = [];
        try
        {
            if (AssetLoader.LoadPrimary(provider, skeletonPath) is USkeleton skel &&
                skel.ReferenceSkeleton?.FinalRefBoneInfo is { Length: > 0 } info)
                names = info.Select(b => b.Name.Text).ToArray();
        }
        catch { /* a skeleton that will not load is simply not a candidate */ }

        lock (Gate) Cache[skeletonPath] = names;
        return names;
    }

    /// <summary>
    /// The candidates that can drive <paramref name="rigBones"/>, best first.
    /// </summary>
    /// <param name="least">
    /// How much of the rig a candidate must cover to be offered. Well below 1 on purpose: a kit
    /// skeleton carries cloth and armour bones the base humanoid rig has never heard of, and an
    /// animation that drives the body while leaving a shoulder pad at its bind pose is still the
    /// animation the author asked to see.
    /// </param>
    public static List<Match> For(IFileProvider provider, IEnumerable<string> candidates,
                                  IReadOnlyCollection<string> rigBones, float least = 0.5f)
    {
        var want = new HashSet<string>(rigBones, StringComparer.OrdinalIgnoreCase);
        var found = new List<Match>();

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (path.Length == 0) continue;
            var bones = BonesOf(provider, path);
            if (bones.Count == 0) continue;

            var shared = bones.Count(b => want.Contains(b));
            var match = new Match(path, shared, want.Count);
            if (match.Fraction >= least) found.Add(match);
        }

        // Deterministic: ties are the normal case -- nine armour kits share one rig exactly --
        // and an arbitrary order among them makes the list reshuffle between runs.
        found.Sort((a, b) => b.Shared != a.Shared
            ? b.Shared.CompareTo(a.Shared)
            : string.Compare(a.Leaf, b.Leaf, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    /// <summary>Forget every cached skeleton — for a game being unmounted or remounted.</summary>
    public static void Forget()
    {
        lock (Gate) Cache.Clear();
    }
}

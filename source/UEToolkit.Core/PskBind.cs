using System.Numerics;
using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Compares the bind pose of two written .psk files, bone by bone.
///
/// Two parts of one character must agree on the rest pose of every bone they share, or importing
/// both into a DCC unifies the skeletons, that bone takes one part's value, and the other part
/// deforms by exactly the difference. It is invisible at rest — each mesh can still sit at its
/// own bind — and only appears once something animates, which makes it look like an animation
/// bug and sends you looking in the wrong place.
///
/// One character can ship two rig generations: Common/Skeleton/SK_Body.psk is 89 bones, everything
/// in Default/Mesh/ is 119. Of the 77 names they share, exactly two differ — an attach bone
/// by 3.8 cm — and pairing a helmet from one with a body from the other stretched the neck by
/// that amount. Sweeping one part against all 49 meshes of the character made the outlier obvious: 48 read
/// "0 of 119 differing" and the odd one read "2 of 77".
///
/// Reads the WRITTEN file rather than the source asset, so it describes what a DCC will actually
/// import.
/// </summary>
public static class PskBind
{
    public sealed record BoneDiff(string Name, float Cm, float Deg);

    public sealed record Result(
        string A, string B, int BonesA, int BonesB, int Shared, int OnlyA, int OnlyB,
        IReadOnlyList<BoneDiff> Differing)
    {
        /// <summary>Bind poses agree where the rigs overlap — safe to import together.</summary>
        public bool Agree => Differing.Count == 0;

        public override string ToString() =>
            Shared == 0
                ? $"no bones in common ({BonesA} vs {BonesB})"
                : Agree
                    ? $"agree on all {Shared} shared bone(s)"
                    : $"{Differing.Count} of {Shared} shared bone(s) differ, worst " +
                      $"{Differing[0].Name} {Differing[0].Cm:0.##} cm / {Differing[0].Deg:0.#} deg";
    }

    // Reading and FK live in PskSkeleton: the PSK stores rotations mirrored, and two copies of
    // that convention would drift. A comparison survives getting it wrong -- a systematic error
    // cancels between two files -- which is precisely why it went unnoticed here until placement,
    // which needs absolute positions, exposed it.

    /// <summary>Positions within this many cm, and rotations within this many degrees, agree.</summary>
    public const float TolCm = 0.01f;
    public const float TolDeg = 0.1f;

    public static Result Compare(string aPath, string bPath)
    {
        var a = PskSkeleton.Read(aPath);
        var b = PskSkeleton.Read(bPath);
        var byNameB = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < b.Count; i++) byNameB[b[i].Name] = i;

        var (ra, pa) = PskSkeleton.World(a);
        var (rb, pb) = PskSkeleton.World(b);

        var diffs = new List<BoneDiff>();
        var shared = 0;
        for (var i = 0; i < a.Count; i++)
        {
            if (!byNameB.TryGetValue(a[i].Name, out var j)) continue;
            shared++;
            var cm = Vector3.Distance(pa[i], pb[j]);
            // Angle between two orientations, via the RELATIVE rotation rather than acos(dot).
            // acos loses precision exactly where it matters here -- near dot = 1, a rounding
            // error of 1e-7 becomes a tenth of a degree, and it reported 20 meshes as
            // differing by exactly the 0.1 deg tolerance on one thumb bone. atan2 of the vector
            // part against the scalar part stays accurate at small angles.
            // Quaternion double cover: q and -q are the same orientation, so align signs first.
            var qb = Quaternion.Dot(ra[i], rb[j]) < 0f ? -rb[j] : rb[j];
            var rel = Quaternion.Conjugate(ra[i]) * qb;
            var deg = float.RadiansToDegrees(
                2f * MathF.Atan2(new Vector3(rel.X, rel.Y, rel.Z).Length(), MathF.Abs(rel.W)));
            if (cm > TolCm || deg > TolDeg) diffs.Add(new BoneDiff(a[i].Name, cm, deg));
        }
        diffs.Sort((x, y) => y.Cm.CompareTo(x.Cm));
        return new Result(aPath, bPath, a.Count, b.Count, shared,
            a.Count - shared, b.Count - shared, diffs);
    }

    /// <summary>
    /// Compare one part against every mesh in a directory tree, worst first. Sweeping is what
    /// makes an odd rig obvious: a single pairwise result says two files disagree, but a sweep
    /// says which ONE of fifty is the outlier.
    /// </summary>
    public static IEnumerable<Result> Sweep(string pskPath, string root)
    {
        var files = Directory.EnumerateFiles(root, "*.psk", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.pskx", SearchOption.AllDirectories))
            .Where(f => !string.Equals(f, pskPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            Result r;
            try { r = Compare(pskPath, f); } catch { continue; }
            if (r.Shared > 0) yield return r;
        }
    }
}

using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;

namespace UEToolkit.Core;

/// <summary>
/// A bind pose can carry a SCALE, and a PSK has nowhere to put one.
///
/// THE FAULT THIS FIXES, measured 2026-09-11 on a community mod of 66 helmets: 36 of them came out
/// of a dump a hundred times too big while being correct in game. The export path was not at
/// fault -- the same command on a game mesh reproduces an older dump byte for byte -- the meshes
/// simply carry a bone scaled by 100:
///
///     AIRBORN   bind scale NOT 1: SK_HAAF_Clo001_HELM(100,100,100)   head Z 16051.18   bounds 2950.8
///     ARCNEW    every bone is 1                                      head Z   160.51   bounds   28.7
///
/// 16051.18 / 100 is 160.51 and 2950.8 / 100 is 29.5 -- the scaled mesh divided by its own bone
/// scale lands exactly on its unscaled sibling from the same mod. That equality is why this is a
/// division and not a guess: the right answer was already sitting in the next folder.
///
/// The engine honours the scale, so the mod is right in game. Everything here ignored it -- and
/// not only the writers: `Scale3D` occurred in LevelExport and RegionIndex and in NO mesh or
/// skeleton export at all, so `--meshinfo` was reporting a head bone at Z=16051 as though that
/// were the truth.
///
/// WHY DIVIDE THE FILE RATHER THAN TEACH PSK ABOUT SCALE: the format has no field for it. ActorX's
/// bone record is a quaternion, a translation and three size floats nothing reads. There is no
/// representation to write, so the only faithful thing is to hand the DCC the geometry at the size
/// the engine renders, bound to a skeleton at that same size.
/// </summary>
public static class PskScale
{
    /// <summary>
    /// The single uniform scale a rig applies to everything, or 1.
    ///
    /// DELIBERATELY NARROW. It answers only for the shape actually seen in the wild: one ROOT bone
    /// carrying one uniform scale, which every other bone inherits. Anything else -- a scale
    /// halfway down a chain, two different scales, a non-uniform one -- is left alone and said out
    /// loud, because dividing a partially scaled rig by one number would move the bones that were
    /// already right. A rig like that needs the scale composed per bone and the vertices
    /// transformed by their own weights, which is a different and much larger change than this.
    /// </summary>
    public static float RootScaleOf(USkeletalMesh mesh, out string? note)
    {
        note = null;
        var info = mesh.ReferenceSkeleton?.FinalRefBoneInfo;
        var pose = mesh.ReferenceSkeleton?.FinalRefBonePose;
        if (info is null || pose is null) return 1f;

        var scaledRoot = 0f;
        var offenders = new List<string>();
        for (var i = 0; i < info.Length && i < pose.Length; i++)
        {
            var s = pose[i].Scale3D;
            float x = (float) s.X, y = (float) s.Y, z = (float) s.Z;
            if (Near(x, 1) && Near(y, 1) && Near(z, 1)) continue;

            var name = info[i].Name.Text;
            if (info[i].ParentIndex >= 0) { offenders.Add($"{name} is not a root"); continue; }
            if (!Near(x, y) || !Near(y, z)) { offenders.Add($"{name} is not uniform ({x},{y},{z})"); continue; }
            if (scaledRoot != 0f && !Near(scaledRoot, x))
            { offenders.Add($"{name} disagrees with {scaledRoot}"); continue; }
            scaledRoot = x;
        }

        if (offenders.Count > 0)
        {
            note = "its bind pose carries a scale this cannot normalise -- " +
                   string.Join("; ", offenders.Take(3)) +
                   ". The mesh is exported as it is stored, which may be the wrong size in a DCC.";
            return 1f;
        }
        return scaledRoot == 0f || Near(scaledRoot, 1) ? 1f : scaledRoot;
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.0001f;

    /// <summary>
    /// Divide a written .psk's points and bone translations by <paramref name="by"/>, in place.
    ///
    /// Chunk-wise and byte-preserving: every chunk header, every count and everything this does not
    /// name is copied through untouched, so a mesh whose scale is 1 is not written at all (the
    /// caller skips it) and one that is scaled differs ONLY in the floats that had to move.
    /// </summary>
    public static bool Normalise(string pskPath, float by, out string? why, out int points, out int bones)
    {
        why = null; points = 0; bones = 0;
        if (Math.Abs(by - 1f) < 0.0001f) return true;
        if (by == 0f || float.IsNaN(by) || float.IsInfinity(by))
        { why = $"a scale of {by} is not something to divide by"; return false; }

        byte[] d;
        try { d = File.ReadAllBytes(pskPath); }
        catch (Exception e) { why = e.Message; return false; }

        // ACTRHEAD, then a run of <name[20], flags, dataSize, dataCount, payload> chunks. Walked
        // rather than searched: a float pattern occurs by accident inside vertex data, and the only
        // safe way to find a chunk is to count past the ones before it.
        var at = 0;
        while (at + 32 <= d.Length)
        {
            var name = System.Text.Encoding.ASCII.GetString(d, at, 20).TrimEnd('\0', ' ');
            var size = BitConverter.ToInt32(d, at + 24);
            var count = BitConverter.ToInt32(d, at + 28);
            var body = at + 32;
            if (size < 0 || count < 0 || (long) body + (long) size * count > d.Length)
            { why = $"chunk '{name}' runs past the end of the file"; return false; }

            switch (name)
            {
                // A point is three floats and nothing else.
                case "PNTS0000" when size == 12:
                    for (var i = 0; i < count; i++)
                        for (var c = 0; c < 3; c++)
                        {
                            var o = body + i * 12 + c * 4;
                            BitConverter.TryWriteBytes(d.AsSpan(o, 4), BitConverter.ToSingle(d, o) / by);
                        }
                    points = count;
                    break;

                // VBone: name[64], flags(4), children(4), parent(4), quat(16), TRANSLATION(12),
                // length(4), xyz sizes(12). The rotation is scale-free and stays exactly as it is;
                // only the translation carries length, so only the translation is divided.
                case "REFSKELT" when size >= 104:
                    for (var i = 0; i < count; i++)
                        for (var c = 0; c < 3; c++)
                        {
                            var o = body + i * size + 92 + c * 4;
                            BitConverter.TryWriteBytes(d.AsSpan(o, 4), BitConverter.ToSingle(d, o) / by);
                        }
                    bones = count;
                    break;
            }

            at = body + size * count;
        }

        if (points == 0) { why = "no PNTS0000 chunk, so this is not a .psk this can rescale"; return false; }

        try { File.WriteAllBytes(pskPath, d); }
        catch (Exception e) { why = e.Message; return false; }
        return true;
    }
}

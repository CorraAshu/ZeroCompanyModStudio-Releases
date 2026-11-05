using System.Text.Json;

namespace UEToolkit.Core;

/// <summary>
/// Where an exported glTF actually puts its bones, read out of the file itself.
///
/// The check behind <c>--rigcheck</c>, and it deliberately opens the .glb by hand rather than
/// asking an importer. The bug it exists to catch was in the EXPORTER, so a reader that shared
/// any of the exporter's assumptions could agree with it and be wrong in the same direction;
/// walking the node hierarchy and multiplying the transforms out is the one reading that cannot.
///
/// **What went wrong, and why nothing noticed for a release and a half.** CUE4Parse's glTF writer
/// swaps Y and Z to get from Unreal's space to glTF's. For a POSITION that is a plain permutation
/// and correct. For a ROTATION it is a mirror -- determinant -1 -- so the quaternion's vector part
/// has to be permuted AND NEGATED, or every bone turns the wrong way about the swapped axis. It
/// was only permuted. The mesh was unaffected, because vertices go through the position overload,
/// so the model imported perfectly and only the armature was wrong: the spine ran downward out of
/// the hips and the feet finished above the shoulders.
///
/// It is checked as WORLD heights because that is the only place the fault is visible. Every local
/// transform in the broken file looks entirely reasonable on its own.
/// </summary>
public static class RigCheck
{
    /// <summary>
    /// Every bone's height above the floor -- glTF's +Y -- keyed by bone name.
    ///
    /// Rotation and translation only. Skeletons do not carry scale here, and a check that quietly
    /// handled a case it had never seen would be a check nobody could trust.
    /// </summary>
    public static Dictionary<string, float> BoneHeights(string glbPath, out string? why)
    {
        var found = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        why = null;
        try
        {
            var raw = File.ReadAllBytes(glbPath);
            if (raw.Length < 20 || BitConverter.ToUInt32(raw, 0) != 0x46546C67u)
            { why = "not a .glb"; return found; }

            // A glb is a header then chunks; the first chunk is the JSON.
            var at = 12;
            var length = BitConverter.ToInt32(raw, 8);
            string? json = null;
            while (at + 8 <= length && at + 8 <= raw.Length)
            {
                var chunkLength = BitConverter.ToInt32(raw, at);
                var chunkType = BitConverter.ToUInt32(raw, at + 4);
                at += 8;
                if (chunkType == 0x4E4F534Au) { json = System.Text.Encoding.UTF8.GetString(raw, at, chunkLength); break; }
                at += chunkLength;
            }
            if (json is null) { why = "the glb carries no JSON chunk"; return found; }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodes))
            { why = "the glb has no nodes"; return found; }

            var all = nodes.EnumerateArray().ToArray();
            var roots = doc.RootElement.TryGetProperty("scenes", out var scenes) &&
                        scenes.GetArrayLength() > 0 &&
                        scenes[0].TryGetProperty("nodes", out var sceneNodes)
                ? sceneNodes.EnumerateArray().Select(x => x.GetInt32()).ToArray()
                : [0];

            foreach (var root in roots) Walk(all, root, 0f, 0f, 0f, Identity, found);
        }
        catch (Exception e) { why = e.Message; }
        return found;
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>
    /// Down one branch, carrying the parent's world position and rotation as a 3x3.
    ///
    /// Matrices rather than quaternion algebra on purpose: the whole fault being guarded against is
    /// a quaternion convention, and a checker written in the same vocabulary as the bug is a
    /// checker that can repeat it.
    /// </summary>
    private static void Walk(JsonElement[] nodes, int index, float x, float y, float z,
                             float[] basis, Dictionary<string, float> found)
    {
        if (index < 0 || index >= nodes.Length) return;
        var node = nodes[index];

        float lx = 0, ly = 0, lz = 0;
        if (node.TryGetProperty("translation", out var t) && t.GetArrayLength() == 3)
        { lx = t[0].GetSingle(); ly = t[1].GetSingle(); lz = t[2].GetSingle(); }

        // The parent's rotation applied to this node's local offset.
        var wx = x + basis[0] * lx + basis[1] * ly + basis[2] * lz;
        var wy = y + basis[3] * lx + basis[4] * ly + basis[5] * lz;
        var wz = z + basis[6] * lx + basis[7] * ly + basis[8] * lz;

        var mine = basis;
        if (node.TryGetProperty("rotation", out var r) && r.GetArrayLength() == 4)
            mine = Multiply(basis, Matrix(r[0].GetSingle(), r[1].GetSingle(),
                                          r[2].GetSingle(), r[3].GetSingle()));

        if (node.TryGetProperty("name", out var name) && name.GetString() is { } text)
            found[text] = wy;

        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                Walk(nodes, child.GetInt32(), wx, wy, wz, mine, found);
    }

    /// <summary>A unit quaternion as a 3x3 rotation, row-major.</summary>
    private static float[] Matrix(float x, float y, float z, float w) =>
    [
        1 - 2 * (y * y + z * z), 2 * (x * y - z * w),     2 * (x * z + y * w),
        2 * (x * y + z * w),     1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
        2 * (x * z - y * w),     2 * (y * z + x * w),     1 - 2 * (x * x + y * y),
    ];

    private static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[9];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }
}

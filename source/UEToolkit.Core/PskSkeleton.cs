using System.Numerics;
using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Reads the rest skeleton out of a written .psk and forward-kinematics it to world space.
///
/// One implementation, because getting it wrong is quiet. The PSK stores bone rotations MIRRORED,
/// not as UE holds them: the writer negates Y on every bone's quaternion and additionally negates
/// W on bone 0.
///
/// Mirroring a rotation about the XZ plane maps q = (x, y, z, w) to (x, -y, z, -w). What the file
/// holds is (x, -y, z, w), and (x, -y, z, -w) = -conj(x, -y, z, w) — the same rotation, since q
/// and -q are equal as rotations. So **every bone except the root is stored conjugated**, and the
/// root, whose W was negated as well, is already correct.
///
/// Composing the stored values raw looks plausible and is not: on one character it put every bone at
/// an identical Z of 42.01 with the chain running along X, while the body mesh it skins plainly
/// runs along Z from 0 to 107.6. A rig cannot disagree with its own geometry, which is the check
/// that caught it — bone positions mean nothing until compared against the mesh they move.
///
/// A comparison between two files survives the wrong convention, because a systematic error
/// cancels; ABSOLUTE positions do not. Anything that places geometry needs this.
/// </summary>
public static class PskSkeleton
{
    public readonly record struct Bone(string Name, int Parent, Quaternion Rot, Vector3 Pos);

    /// <summary>Parent-relative rest transforms, with the mirror convention undone.</summary>
    public static List<Bone> Read(string pskPath)
    {
        var d = File.ReadAllBytes(pskPath);
        var chunks = PskUv.ReadChunks(d);
        var list = new List<Bone>();
        if (!chunks.TryGetValue("REFSKELT", out var c)) return list;
        for (var i = 0; i < c.Count; i++)
        {
            var o = c.Offset + i * c.Size;
            var end = Array.IndexOf(d, (byte) 0, o, Math.Min(64, d.Length - o));
            var name = Encoding.ASCII.GetString(d, o, (end < 0 ? o + 64 : end) - o);
            var parent = BitConverter.ToInt32(d, o + 72);
            var q = new Quaternion(BitConverter.ToSingle(d, o + 76), BitConverter.ToSingle(d, o + 80),
                                   BitConverter.ToSingle(d, o + 84), BitConverter.ToSingle(d, o + 88));
            var p = new Vector3(BitConverter.ToSingle(d, o + 92), BitConverter.ToSingle(d, o + 96),
                                BitConverter.ToSingle(d, o + 100));
            // Bone 0 had its W negated too, which already makes it the mirrored rotation.
            list.Add(new Bone(name, parent, i == 0 ? q : Quaternion.Conjugate(q), p));
        }
        return list;
    }

    /// <summary>World-space rest transforms, in the file's own coordinate system.</summary>
    public static (Quaternion[] Rot, Vector3[] Pos) World(List<Bone> bones)
    {
        var rot = new Quaternion[bones.Count];
        var pos = new Vector3[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            if (b.Parent < 0 || b.Parent >= i) { rot[i] = b.Rot; pos[i] = b.Pos; continue; }
            rot[i] = rot[b.Parent] * b.Rot;
            pos[i] = pos[b.Parent] + Vector3.Transform(b.Pos, rot[b.Parent]);
        }
        return (rot, pos);
    }

    /// <summary>Bone name to world rest position — what a placement needs.</summary>
    public static Dictionary<string, Vector3> Positions(string pskPath)
    {
        var bones = Read(pskPath);
        var (_, pos) = World(bones);
        var map = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < bones.Count; i++) map[bones[i].Name] = pos[i];
        return map;
    }
}

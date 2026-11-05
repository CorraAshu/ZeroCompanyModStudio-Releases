using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Re-reads written .psa files and checks the KEYS, not just the structure.
///
/// This exists because "the export succeeded" is a much weaker claim than it sounds. A
/// decompressor that returns nothing still writes a perfectly well-formed PSA: right chunk
/// headers, right bone count, right frame count, every key an identity transform. Bone-name
/// checks (<see cref="PsaBones"/>) pass on it too, because the names come from the skeleton
/// rather than the decoder. The first thing that would notice is a person importing it into Max
/// and seeing a character stand still, which is far too late and very hard to attribute.
///
/// So the questions here are the ones a wrong decoder fails: are the quaternions unit-length,
/// do the keys actually change over time, and is anything NaN.
/// </summary>
public static class PsaValidate
{
    public sealed record Result(
        string File, int Bones, int Anims, int Frames, int Keys,
        int NonUnitQuats, int NaNs, int MovingBones, double MaxTranslation, string Verdict);

    /// <summary>
    /// ActorX chunk header: 20-byte id, int flags, int datasize, int datacount.
    /// </summary>
    private static IEnumerable<(string Id, int Size, int Count, int Offset)> Chunks(byte[] d)
    {
        for (var off = 0; off + 32 <= d.Length;)
        {
            var id = Encoding.ASCII.GetString(d, off, 20).Split('\0')[0];
            var size = BitConverter.ToInt32(d, off + 24);
            var count = BitConverter.ToInt32(d, off + 28);
            if (size < 0 || count < 0 || off + 32L + (long) size * count > d.Length) yield break;
            yield return (id, size, count, off + 32);
            off += 32 + size * count;
        }
    }

    public static Result Check(string path)
    {
        byte[] d;
        try { d = File.ReadAllBytes(path); }
        catch (Exception e) { return new Result(path, 0, 0, 0, 0, 0, 0, 0, 0, "unreadable: " + e.Message); }

        int bones = 0, anims = 0, frames = 0, keys = 0;
        var keysOffset = -1;
        var keyStride = 0;

        foreach (var (id, size, count, off) in Chunks(d))
        {
            switch (id)
            {
                case "BONENAMES": bones = count; break;
                case "ANIMINFO":
                    anims = count;
                    // AnimInfoBinary is 168 bytes; NumRawFrames sits at +164.
                    if (count > 0 && size >= 168) frames = BitConverter.ToInt32(d, off + 164);
                    break;
                case "ANIMKEYS": keys = count; keysOffset = off; keyStride = size; break;
            }
        }

        if (keysOffset < 0 || keyStride < 32 || bones == 0)
            return new Result(path, bones, anims, frames, keys, 0, 0, 0, 0, "no keys");

        // VQuatAnimKey: FVector position (12), FQuat orientation (16), float time (4).
        int nonUnit = 0, nans = 0;
        double maxTrans = 0;
        var moved = new bool[bones];
        var first = new (float X, float Y, float Z, float W, float PX, float PY, float PZ)[bones];
        var haveFirst = new bool[bones];

        // Keys are frame-major, bone-minor: the whole skeleton for frame 0, then frame 1...
        for (var k = 0; k < keys; k++)
        {
            var o = keysOffset + k * keyStride;
            var px = BitConverter.ToSingle(d, o);
            var py = BitConverter.ToSingle(d, o + 4);
            var pz = BitConverter.ToSingle(d, o + 8);
            var qx = BitConverter.ToSingle(d, o + 12);
            var qy = BitConverter.ToSingle(d, o + 16);
            var qz = BitConverter.ToSingle(d, o + 20);
            var qw = BitConverter.ToSingle(d, o + 24);

            if (float.IsNaN(px) || float.IsNaN(py) || float.IsNaN(pz) ||
                float.IsNaN(qx) || float.IsNaN(qy) || float.IsNaN(qz) || float.IsNaN(qw) ||
                float.IsInfinity(px) || float.IsInfinity(py) || float.IsInfinity(pz)) { nans++; continue; }

            var len = Math.Sqrt(qx * (double) qx + qy * (double) qy + qz * (double) qz + qw * (double) qw);
            if (Math.Abs(len - 1.0) > 0.01) nonUnit++;

            var mag = Math.Sqrt(px * (double) px + py * (double) py + pz * (double) pz);
            if (mag > maxTrans) maxTrans = mag;

            var bone = k % bones;
            if (!haveFirst[bone]) { first[bone] = (qx, qy, qz, qw, px, py, pz); haveFirst[bone] = true; continue; }
            var f = first[bone];
            if (Math.Abs(qx - f.X) > 1e-5f || Math.Abs(qy - f.Y) > 1e-5f ||
                Math.Abs(qz - f.Z) > 1e-5f || Math.Abs(qw - f.W) > 1e-5f ||
                Math.Abs(px - f.PX) > 1e-4f || Math.Abs(py - f.PY) > 1e-4f ||
                Math.Abs(pz - f.PZ) > 1e-4f) moved[bone] = true;
        }

        var movingBones = moved.Count(m => m);

        // Above this, a clip in which nothing whatsoever moves stops being explicable as a pose.
        // One second at 30 fps. Chosen after 6-frame lean additives proved a threshold of 2 far
        // too eager: it called 40 of a 55-clip sample broken, and every one of them was fine.
        const int StaticFrameLimit = 30;

        // Stillness is weak evidence and it took a false alarm to calibrate it. A great many
        // shipped clips are POSES that hold: holster poses, additive bases, and the directional
        // lean/aim additives a blendspace samples by weight rather than by time. One game's
        // ADD_NAV_D/F/L/R clips are six frames of one pose each, and the four are different from
        // each other -- so the additive delta was being applied correctly and "every bone
        // static" was still true of all of them.
        //
        // What is NOT ambiguous: NaNs and non-unit quaternions. Those are always defects. A long
        // clip that never moves is merely suspicious, and is reported as a note below that.
        var verdict =
            nans > 0 ? "NaN KEYS"
            : nonUnit > keys / 100 ? "NON-UNIT QUATS"
            : movingBones > 0 ? "ok"
            : frames > StaticFrameLimit ? $"EVERY BONE STATIC across {frames} frames - worth a look"
            : "static (a held pose looks like this)";


        return new Result(path, bones, anims, frames, keys, nonUnit, nans, movingBones, maxTrans, verdict);
    }

    /// <summary>Check every .psa under a directory. Returns the results worst-first.</summary>
    public static List<Result> CheckDirectory(string dir) =>
        Directory.EnumerateFiles(dir, "*.psa", SearchOption.AllDirectories)
            .Select(Check)
            .OrderBy(r => r.Verdict == "ok")
            .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// The bone names a written .psa drives.
///
/// ActorX matches a PSA to a rig by NAME, so this list is exactly what determines whether an
/// animation does anything at all when imported. A PSA whose names appear in no rig imports
/// silently and moves nothing.
///
/// **Compare case-insensitively.** A PSA written from some engines' data carries lowercase names
/// (`spine_01`, `head`) and the PSK carries UE's CamelCase (`Spine_01`, `Head`) — the two halves of
/// this toolchain disagree on spelling for every bone in the game. A case-sensitive comparison
/// reports 100% of bones missing, which is how "the cloth bones are absent from the PSK
/// rig, so that animation drives nothing" became a recorded known issue. It was never true.
/// </summary>
public static class PsaBones
{
    public static List<string> Read(string psaPath)
    {
        var list = new List<string>();
        byte[] d;
        try { d = File.ReadAllBytes(psaPath); } catch { return list; }

        // Same 32-byte chunk header as the PSK; BONENAMES holds one 120-byte VBone each.
        for (var off = 0; off + 32 <= d.Length;)
        {
            var id = Encoding.ASCII.GetString(d, off, 20).Split('\0')[0];
            var size = BitConverter.ToInt32(d, off + 24);
            var count = BitConverter.ToInt32(d, off + 28);
            if (size < 0 || count < 0 || off + 32L + (long) size * count > d.Length) break;
            if (id == "BONENAMES")
            {
                for (var i = 0; i < count; i++)
                {
                    var o = off + 32 + i * size;
                    var end = Array.IndexOf(d, (byte) 0, o, Math.Min(64, d.Length - o));
                    list.Add(Encoding.ASCII.GetString(d, o, (end < 0 ? o + 64 : end) - o));
                }
                return list;
            }
            off += 32 + size * count;
        }
        return list;
    }
}

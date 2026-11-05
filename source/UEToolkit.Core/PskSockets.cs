using System.Buffers.Binary;
using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Gives a PSK's socket bones a single root, so ActorX can import it.
///
/// **A PSK bone list has to be a TREE, and CUE4Parse writes static-mesh sockets as bones with
/// `ParentIndex = -1`.** A prop with 42 sockets therefore ships 42 roots, and an importer
/// resolving a parent as `bones[parent + 1]` — MAXScript arrays being 1-based — asks for
/// `bones[0]` on every one after the first. That is exactly the error ActorX raises:
///
///     -- Runtime error: array index must be positive number, got: 0
///
/// It cost a whole 23-mesh import to find, and it had been latent since the first extraction:
/// `PskValidate` was taught that **"−1 is a legitimate parent at any bone index"**, which is true
/// of the FORMAT and false of every importer that reads it. A file our validator accepts and the
/// only target application rejects is not a valid file — the validator was measuring the wrong
/// thing, and now says so.
///
/// Measured across the full export: **20 of 12,390 meshes**, all of them props you meet — oxygen
/// tanks, crates, medical beds, barriers, grenades, a portable shield, a turret damage mesh.
/// Rare enough to have gone unnoticed while only characters were being imported, common enough to
/// hit the moment anyone imports environment art.
///
/// **The fix adds an identity root rather than reparenting, deliberately.** PSK bone positions are
/// PARENT-RELATIVE, so making socket 1 a child of socket 0 would silently move it by socket 0's
/// transform unless the arithmetic were undone — and doing that arithmetic ourselves is the
/// mistake this project has made repeatedly. Against an identity root every stored position keeps
/// its meaning untouched and no number changes.
/// </summary>
public static class PskSockets
{
    private const int BoneSize = 120;
    private const int NameSize = 64;
    private const int ParentOffset = 72;      // name[64] + flags(4) + numChildren(4)
    private const int ChildrenOffset = 68;
    private const int OrientationOffset = 76; // parent(4) then FQuat x,y,z,w

    public sealed record Result(bool Changed, int RootsBefore, int Bones, string? Skipped);

    /// <summary>
    /// Does this file need fixing, decided from the chunk headers alone.
    ///
    /// Streams a few dozen bytes per chunk instead of reading the whole mesh. That matters because
    /// this runs on EVERY exported mesh inside the parallel export loop, while only 20 of 12,390
    /// need anything done — and a full <c>ReadAllBytes</c> per mesh is real I/O and GC pressure
    /// added to a loop with a known timing-sensitive race in it. Cheap enough to be invisible is
    /// the only acceptable cost for a check that almost always says no.
    /// </summary>
    public static bool NeedsRoot(string path)
    {
        using var fs = FreshFile.OpenRead(path);
        var hdr = new byte[32];
        long off = 0;
        while (true)
        {
            fs.Position = off;
            if (fs.Read(hdr, 0, 32) < 32) return false;
            var name = Encoding.ASCII.GetString(hdr, 0, 20).Split('\0')[0];
            var size = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(24));
            var count = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(28));
            if (size < 0 || count < 0 || off + 32L + (long) size * count > fs.Length) return false;

            if (name == "REFSKELT" && count > 1)
            {
                var bones = new byte[(long) size * count];
                fs.Position = off + 32;
                if (fs.Read(bones, 0, bones.Length) < bones.Length) return false;
                var roots = 0;
                for (var i = 0; i < count; i++)
                    if (BinaryPrimitives.ReadInt32LittleEndian(bones.AsSpan(i * size + ParentOffset)) < 0
                        && ++roots > 1) return true;
                return false;
            }
            off += 32 + (long) size * count;
        }
    }

    /// <summary>How many bones have no parent. More than one is a forest, not a tree.</summary>
    public static int CountRoots(byte[] data)
    {
        var (off, _, count) = Find(data, "REFSKELT");
        if (off < 0) return 0;
        var roots = 0;
        for (var i = 0; i < count; i++)
            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + i * BoneSize + ParentOffset)) < 0)
                roots++;
        return roots;
    }

    /// <summary>
    /// Rewrite <paramref name="path"/> with a single root if it has several, in place.
    ///
    /// Refuses when the mesh has skin weights: those index bones by number, so inserting a bone
    /// would shift every weight onto the wrong one. Every affected mesh measured so far has zero
    /// weights — they are static props — but refusing is the difference between a fix that is safe
    /// because it was checked and one that is safe because nothing has broken yet.
    /// </summary>
    public static Result AddRoot(string path, string rootName = "ROOT")
    {
        var data = FreshFile.ReadAllBytes(path);
        var (boneOff, boneHdr, boneCount) = Find(data, "REFSKELT");
        if (boneOff < 0 || boneCount == 0) return new Result(false, 0, 0, null);

        var roots = CountRoots(data);
        if (roots <= 1) return new Result(false, roots, boneCount, null);

        var (_, _, weights) = Find(data, "RAWWEIGHTS");
        if (weights > 0)
            return new Result(false, roots, boneCount,
                $"{weights} skin weight(s) index bones by number");

        var newCount = boneCount + 1;
        var outBytes = new byte[data.Length + BoneSize];

        // Everything up to the bone chunk's data, then the header with one more bone.
        Array.Copy(data, 0, outBytes, 0, boneOff);
        BinaryPrimitives.WriteInt32LittleEndian(outBytes.AsSpan(boneHdr + 28), newCount);

        // The new root: identity, at the origin, parented to nothing, owning every socket.
        var root = outBytes.AsSpan(boneOff, BoneSize);
        root.Clear();
        Encoding.ASCII.GetBytes(rootName).CopyTo(root[..NameSize]);
        BinaryPrimitives.WriteInt32LittleEndian(root[ChildrenOffset..], boneCount);
        BinaryPrimitives.WriteInt32LittleEndian(root[ParentOffset..], -1);
        // W = -1, not +1. CUE4Parse negates W on bone 0 "because the importer has invert enabled
        // by default", so an identity root has to be written the way bone 0 always is.
        WriteFloat(root[(OrientationOffset + 12)..], -1f);
        WriteFloat(root[(OrientationOffset + 28)..], 1f);   // Length
        WriteFloat(root[(OrientationOffset + 32)..], 1f);   // Size X
        WriteFloat(root[(OrientationOffset + 36)..], 1f);   // Size Y
        WriteFloat(root[(OrientationOffset + 40)..], 1f);   // Size Z

        // The original bones, shifted up one and re-parented to the root.
        Array.Copy(data, boneOff, outBytes, boneOff + BoneSize, data.Length - boneOff);
        for (var i = 0; i < boneCount; i++)
        {
            var b = outBytes.AsSpan(boneOff + BoneSize + i * BoneSize, BoneSize);
            var parent = BinaryPrimitives.ReadInt32LittleEndian(b[ParentOffset..]);
            // A real parent keeps its relationship and only moves up by one; a root becomes a
            // child of the new one.
            BinaryPrimitives.WriteInt32LittleEndian(b[ParentOffset..], parent < 0 ? 0 : parent + 1);

            // The old bone 0 had its W negated by the writer's `i == 0` rule. It is no longer
            // bone 0, so that negation has to come back off or its rotation is the conjugate.
            // Nothing in the file records that this was done, which is why it is done HERE, at
            // the one place that moves a bone out of index 0.
            if (i == 0)
                WriteFloat(b[(OrientationOffset + 12)..],
                    -ReadFloat(b[(OrientationOffset + 12)..]));
        }

        FreshFile.WriteAllBytes(path, outBytes);
        return new Result(true, roots, newCount, null);
    }

    /// <summary>Offset of a chunk's DATA, its HEADER, and its element count. −1 when absent.</summary>
    private static (int Data, int Header, int Count) Find(byte[] data, string id)
    {
        for (var off = 0; off + 32 <= data.Length;)
        {
            var name = Encoding.ASCII.GetString(data, off, 20).Split('\0')[0];
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 24));
            var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(off + 28));
            if (size < 0 || count < 0 || off + 32L + (long) size * count > data.Length) break;
            if (name == id) return (off + 32, off, count);
            off += 32 + size * count;
        }
        return (-1, -1, 0);
    }

    private static float ReadFloat(Span<byte> s) => BitConverter.ToSingle(s);

    private static void WriteFloat(Span<byte> s, float v) =>
        BitConverter.TryWriteBytes(s, v);
}

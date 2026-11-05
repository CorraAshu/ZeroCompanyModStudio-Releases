using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Writes an IoStore container -- the .utoc/.ucas pair a UE5 game actually loads packages from.
///
/// This is the last thing a mod tool needs that Unreal was doing for it. The game refuses cooked
/// packages from a legacy .pak (tested 2026-08-29), so a container is not optional.
///
/// Everything is written UNCOMPRESSED. Compression is per block and the format carries a method
/// index per entry, so method 0 is legitimate -- the game's own mod container already has such a
/// block, where Oodle would have made the data bigger. A mod pak is a few hundred kilobytes and
/// nothing about it needs an Oodle encoder.
/// </summary>
public static class IoStoreWriter
{
    /// <summary>Compression block size. Chunks start on a block boundary in the virtual space.</summary>
    public const uint BlockSize = 64 * 1024;

    private const int HeaderSize = 144;
    private const int MethodNameLength = 32;
    private const uint None = uint.MaxValue;

    private static readonly byte[] Magic =
        { 0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D,
          0x2D, 0x3D, 0x3D, 0x2D, 0x2D, 0x3D, 0x3D, 0x2D };

    /// <summary>
    /// One addressable chunk. <paramref name="Path"/> is where it appears in the directory index;
    /// null keeps it out of the index, which is what the container header chunk wants -- it is
    /// found by id, not by name.
    /// </summary>
    public sealed record Chunk(ulong Id, ushort Index, byte Type, byte[] Data, string? Path);

    /// <summary>
    /// The mount point every container written here uses.
    ///
    /// A reader REQUIRES a mount point beginning "../../.." -- anything else is rejected as
    /// strange and the container is silently mounted at the root with its declared prefix thrown
    /// away. Since <see cref="Chunk"/> carries each file's full path from the container root
    /// anyway, mounting at the root is both correct and the only value that cannot be got wrong.
    /// </summary>
    public const string MountPoint = "../../../";

    public static void Write(string utocPath, string ucasPath,
                             ulong containerId, IReadOnlyList<Chunk> chunks)
    {
        var blocks = new List<(ulong UcasOffset, uint Size)>();
        var offsets = new List<(ulong Virtual, ulong Length)>();

        using (var cas = new FileStream(ucasPath, FileMode.Create, FileAccess.Write))
        {
            ulong virtualCursor = 0;
            foreach (var chunk in chunks)
            {
                // Each chunk begins on a compression-block boundary in the VIRTUAL space, because
                // the reader finds its first block by dividing the chunk offset by the block size.
                // Start one mid-block and it reads from the wrong place.
                offsets.Add((virtualCursor, (ulong) chunk.Data.LongLength));

                var written = 0;
                while (written < chunk.Data.Length)
                {
                    var size = Math.Min((int) BlockSize, chunk.Data.Length - written);
                    // 16-byte alignment: the reader takes each block's position from its own entry,
                    // so this is not required for correctness, but AES works in 16-byte units and
                    // an unaligned layout would have to be rebuilt to ever encrypt one.
                    Align(cas, 16);
                    blocks.Add(((ulong) cas.Position, (uint) size));
                    cas.Write(chunk.Data, written, size);
                    written += size;
                }

                var blockCount = (chunk.Data.Length + (int) BlockSize - 1) / (int) BlockSize;
                virtualCursor += (ulong) Math.Max(1, blockCount) * BlockSize;
            }

            // Pad the tail. A reader takes each block's size ALIGNED UP TO 16 -- unconditionally,
            // not only for encrypted containers -- so the last block needs those extra bytes to
            // exist. Without this the final block of the last chunk overruns the end of the file,
            // which shows up as one unreadable file at the end of an otherwise perfect container.
            Align(cas, 16);
        }

        var directoryIndex = BuildDirectoryIndex(MountPoint, chunks);

        using var toc = new FileStream(utocPath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(toc, Encoding.UTF8, leaveOpen: true);

        w.Write(Magic);
        w.Write((byte) 8);                    // EIoStoreTocVersion.ReplaceIoChunkHashWithIoHash
        w.Write((byte) 0);                    // reserved
        w.Write((ushort) 0);                  // reserved
        w.Write((uint) HeaderSize);
        w.Write((uint) chunks.Count);
        w.Write((uint) blocks.Count);
        w.Write((uint) 12);                   // TocCompressedBlockEntrySize
        w.Write((uint) 0);                    // CompressionMethodNameCount -- nothing but None
        w.Write((uint) MethodNameLength);
        w.Write(BlockSize);
        w.Write((uint) directoryIndex.Length);
        w.Write((uint) 1);                    // PartitionCount
        w.Write(containerId);
        w.Write(new byte[16]);                // EncryptionKeyGuid: none
        w.Write((uint) 0x8);                  // ContainerFlags: Indexed. NOT Compressed.
        // No perfect hash. The format allows its absence and the reader then finds a chunk by
        // searching the id array; writing one would mean deriving UE's seed search for no gain on
        // a container this size.
        w.Write((uint) 0);                    // TocChunkPerfectHashSeedsCount
        w.Write(ulong.MaxValue);              // PartitionSize
        w.Write((uint) 0);                    // TocChunksWithoutPerfectHashCount
        w.Write((uint) 0);                    // reserved
        for (var i = 0; i < 5; i++) w.Write((ulong) 0);

        foreach (var c in chunks)
        {
            w.Write(c.Id);
            // The chunk index is stored BYTE-SWAPPED -- UE writes it in network order inside an
            // otherwise little-endian struct.
            w.Write((ushort) (((c.Index & 0xFF) << 8) | ((c.Index & 0xFF00) >> 8)));
            w.Write((byte) 0);
            w.Write(c.Type);
        }

        foreach (var (virt, length) in offsets)
        {
            // Five bytes each, BIG-ENDIAN, packed with no padding.
            WriteUInt40BigEndian(w, virt);
            WriteUInt40BigEndian(w, length);
        }

        foreach (var (ucasOffset, size) in blocks)
        {
            // offset:40 | compressedSize:24, then uncompressedSize:24 | methodIndex:8.
            // Compressed and uncompressed sizes are equal and the method is 0.
            w.Write(ucasOffset | ((ulong) size << 40));
            w.Write(size & 0xFFFFFF);
        }

        // No compression method names, because the count is zero: index 0 always means None.

        w.Write(directoryIndex);

        foreach (var c in chunks)
        {
            // BLAKE3-160 of the UNCOMPRESSED chunk, then flags. Flags stay 0: not compressed, not
            // memory mapped.
            w.Write(Blake3.Hash160(c.Data));
            w.Write((byte) 0);
            w.Write(new byte[3]);             // pad to 24
        }
    }

    private static void Align(Stream s, int to)
    {
        var pad = (int) ((s.Position + to - 1) / to * to - s.Position);
        if (pad > 0) s.Write(new byte[pad]);
    }

    private static void WriteUInt40BigEndian(BinaryWriter w, ulong value)
    {
        w.Write((byte) (value >> 32));
        w.Write((byte) (value >> 24));
        w.Write((byte) (value >> 16));
        w.Write((byte) (value >> 8));
        w.Write((byte) value);
    }

    /// <summary>
    /// The directory index: a mount point, a tree of directories, a list of files, and one shared
    /// string table. A file entry's UserData is the index of its chunk in the toc, which is how a
    /// path resolves to data.
    /// </summary>
    private static byte[] BuildDirectoryIndex(string mountPoint, IReadOnlyList<Chunk> chunks)
    {
        var strings = new List<string>();
        var stringIndex = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint Intern(string s)
        {
            if (stringIndex.TryGetValue(s, out var at)) return at;
            stringIndex[s] = (uint) strings.Count;
            strings.Add(s);
            return (uint) (strings.Count - 1);
        }

        // Directory 0 is the root and has no name.
        var dirs = new List<(uint Name, uint FirstChild, uint NextSibling, uint FirstFile)>
            { (None, None, None, None) };
        var files = new List<(uint Name, uint NextFile, uint UserData)>();
        var childLookup = new Dictionary<(int Parent, string Name), int>();

        int Directory(int parent, string name)
        {
            if (childLookup.TryGetValue((parent, name), out var found)) return found;

            var index = dirs.Count;
            dirs.Add((Intern(name), None, None, None));
            childLookup[(parent, name)] = index;

            // Prepended, not appended: the reader walks FirstChild then NextSibling, and either
            // order produces the same set of paths. Prepending keeps this O(1) per entry.
            var p = dirs[parent];
            dirs[index] = dirs[index] with { NextSibling = p.FirstChild };
            dirs[parent] = p with { FirstChild = (uint) index };
            return index;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Path is not { } path) continue;

            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var dir = 0;
            for (var p = 0; p < parts.Length - 1; p++) dir = Directory(dir, parts[p]);

            var fileIndex = files.Count;
            var d = dirs[dir];
            files.Add((Intern(parts[^1]), d.FirstFile, (uint) i));
            dirs[dir] = d with { FirstFile = (uint) fileIndex };
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        WriteFString(w, mountPoint);

        w.Write(dirs.Count);
        foreach (var d in dirs)
        {
            w.Write(d.Name); w.Write(d.FirstChild); w.Write(d.NextSibling); w.Write(d.FirstFile);
        }

        w.Write(files.Count);
        foreach (var f in files) { w.Write(f.Name); w.Write(f.NextFile); w.Write(f.UserData); }

        w.Write(strings.Count);
        foreach (var s in strings) WriteFString(w, s);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>An FString: a length that INCLUDES the terminator, then the bytes.</summary>
    private static void WriteFString(BinaryWriter w, string s)
    {
        if (s.Length == 0) { w.Write(0); return; }
        var bytes = Encoding.ASCII.GetBytes(s);
        w.Write(bytes.Length + 1);
        w.Write(bytes);
        w.Write((byte) 0);
    }
}

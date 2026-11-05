using System.Security.Cryptography;
using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Writes a legacy .pak -- the last thing UnrealPak.exe was still doing for us.
///
/// A mod's cooked packages cannot live here: the game refuses to load them from a .pak and wants
/// a .utoc/.ucas pair (tested 2026-08-29, see <see cref="IoStoreWriter"/>). But the merged
/// AssetRegistry.bin still ships in a .pak, so a mod needs one, and needing one meant needing a
/// 100 GB engine install.
///
/// Everything is written UNCOMPRESSED and unencrypted. A registry is one file; nothing about it
/// needs an Oodle encoder.
/// </summary>
public static class PakWriter
{
    /// <summary>PakFile_Version_Fnv64BugFix -- the version whose path hash is the FIXED FNV.</summary>
    public const int Version = 11;

    public const uint Magic = 0x5A6F12E1;

    private const int MethodNameLength = 32;
    private const int MaxMethods = 5;

    /// <summary>Footer: guid, encrypted flag, magic, version, index offset/size, SHA1, methods.</summary>
    private const int FooterSize = 16 + 1 + 4 + 4 + 8 + 8 + 20 + MethodNameLength * MaxMethods;

    /// <summary>An uncompressed, unencrypted entry serializes to exactly this many bytes.</summary>
    private const int EntryHeaderSize = 8 + 8 + 8 + 4 + 20 + 1 + 4;

    /// <summary>One file. <paramref name="Path"/> is relative to the mount point, with '/'.</summary>
    public sealed record Entry(string Path, byte[] Data);

    /// <summary>What a pak says about itself before any file is read.</summary>
    public sealed record Scalars(int Version, string MountPoint, ulong PathHashSeed, int NumEntries);

    /// <summary>
    /// FPakFile::HashPath, for pak version >= 11.
    ///
    /// FNV-1a over the LOWERCASED path as UTF-16, seeded by ADDING the seed to the offset basis.
    /// That addition is the whole trick, and UE's own comment beside it says "this is not strictly
    /// correct as the offset should be prime and InOffset could be arbitrary". Every attempt that
    /// treats the seed as the basis, or xors it in, produces a hash the game will never look up.
    /// </summary>
    public static ulong HashPath(string relativePathFromMount, ulong seed)
    {
        const ulong offsetBasis = 0xCBF29CE484222325;
        const ulong prime = 0x00000100000001B3;

        var fnv = offsetBasis + seed;
        foreach (var b in Encoding.Unicode.GetBytes(relativePathFromMount.ToLowerInvariant()))
            fnv = (fnv ^ b) * prime;
        return fnv;
    }

    /// <summary>
    /// FCrc::StrCrc32 -- a standard CRC-32, but over each character widened to FOUR bytes. UE walks
    /// a TCHAR as (c, c>>8, c>>16, c>>24) and the top two are always zero for a 16-bit char, so the
    /// input is effectively UTF-32.
    /// </summary>
    public static uint StrCrc32(string s)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var c in s)
        {
            uint ch = c;
            for (var i = 0; i < 4; i++)
            {
                crc = (crc >> 8) ^ Crc32Table[(crc ^ ch) & 0xFF];
                ch >>= 8;
            }
        }
        return ~crc;
    }

    /// <summary>
    /// The seed UnrealPak would have chosen: the CRC of the lowercased path it wrote the pak to.
    ///
    /// This depends on the build machine's directory layout, which is the clearest possible proof
    /// that the value is arbitrary -- it is stored in the index and the runtime reads it back, so
    /// any seed loads. Reproducing UnrealPak's choice only buys byte-identical output.
    /// </summary>
    public static ulong SeedForPath(string pakPath) =>
        StrCrc32(Path.GetFullPath(pakPath).ToLowerInvariant());

    public static void Write(string pakPath, string mountPoint, IReadOnlyList<Entry> entries,
                             ulong? seed = null)
    {
        if (entries.Count == 0) throw new ArgumentException("a pak with no files proves nothing", nameof(entries));

        var pathHashSeed = seed ?? SeedForPath(pakPath);
        if (!mountPoint.EndsWith('/')) mountPoint += "/";   // MakeDirectoryFromPath

        using var file = new FileStream(pakPath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);

        // --- payload: an entry header immediately before each file's bytes -------------------
        var offsets = new long[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var data = entries[i].Data;
            offsets[i] = file.Position;
            // The in-place header carries Offset 0. UE compares it against the index entry with
            // IndexDataEquals, which deliberately skips Offset ("only in the Index") and Hash
            // ("only in the payload"), so writing the real offset here would be wrong, not merely
            // redundant.
            WriteEntry(w, offset: 0, data.LongLength, SHA1.HashData(data));
            w.Write(data);
        }

        // --- the two secondary indexes ------------------------------------------------------
        var encoded = new MemoryStream();
        var pathHashes = new List<(ulong Hash, int Location)>();
        // Directory -> file -> location. A directory key is relative to the mount, keeps its
        // trailing '/', and carries NO leading '/' -- except the root, which is exactly "/".
        var directories = new Dictionary<string, List<(string Name, int Location)>>(StringComparer.Ordinal);

        using (var ew = new BinaryWriter(encoded, Encoding.UTF8, leaveOpen: true))
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var relative = entries[i].Path.Replace('\\', '/').TrimStart('/');
                var location = (int) encoded.Position;   // an offset into the encoded array
                EncodeEntry(ew, offsets[i], entries[i].Data.LongLength);

                pathHashes.Add((HashPath(relative, pathHashSeed), location));

                var (directory, name) = SplitPath(relative);
                foreach (var parent in ParentsOf(directory))
                    if (!directories.ContainsKey(parent)) directories[parent] = new();
                if (!directories.TryGetValue(directory, out var contents))
                    directories[directory] = contents = new();
                contents.Add((name, location));
            }
        }

        var encodedBytes = encoded.ToArray();
        var pathHashIndex = BuildPathHashIndex(pathHashes);
        var directoryIndex = BuildDirectoryIndex(directories);

        // --- primary index ------------------------------------------------------------------
        // It names the offsets of the two indexes that follow it, so its own length has to be
        // known first. The length does not depend on those offsets, so building it once with
        // zeroes gives the exact size.
        var indexOffset = file.Position;
        var probe = BuildPrimaryIndex(mountPoint, entries.Count, pathHashSeed, 0, 0, encodedBytes,
                                      pathHashIndex, directoryIndex);
        var pathHashOffset = indexOffset + probe.Length;
        var directoryOffset = pathHashOffset + pathHashIndex.Length;
        var primary = BuildPrimaryIndex(mountPoint, entries.Count, pathHashSeed,
                                        pathHashOffset, directoryOffset, encodedBytes,
                                        pathHashIndex, directoryIndex);
        if (primary.Length != probe.Length)
            throw new InvalidOperationException("primary index changed size between passes");

        w.Write(primary);
        w.Write(pathHashIndex);
        w.Write(directoryIndex);

        // --- footer -------------------------------------------------------------------------
        w.Write(new byte[16]);                       // EncryptionKeyGuid: none
        w.Write((byte) 0);                           // bEncryptedIndex
        w.Write(Magic);
        w.Write(Version);
        w.Write(indexOffset);
        w.Write((long) primary.Length);
        w.Write(SHA1.HashData(primary));
        w.Write(new byte[MethodNameLength * MaxMethods]);   // no compression methods at all
    }

    /// <summary>Read what a pak says about itself: version, mount point and path hash seed.</summary>
    public static Scalars ReadScalars(string pakPath)
    {
        using var file = new FileStream(pakPath, FileMode.Open, FileAccess.Read);
        using var r = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);

        file.Position = file.Length - FooterSize + 16 + 1;
        var magic = r.ReadUInt32();
        if (magic != Magic) throw new InvalidDataException($"not a pak: magic 0x{magic:X8}");
        var version = r.ReadInt32();
        var indexOffset = r.ReadInt64();
        var indexSize = r.ReadInt64();

        file.Position = indexOffset;
        var index = new BinaryReader(new MemoryStream(r.ReadBytes((int) indexSize)));
        var mountPoint = ReadFString(index);
        var numEntries = index.ReadInt32();
        var seed = index.ReadUInt64();
        return new Scalars(version, mountPoint, seed, numEntries);
    }

    private static void WriteEntry(BinaryWriter w, long offset, long size, byte[] sha1)
    {
        w.Write(offset);
        w.Write(size);
        w.Write(size);              // UncompressedSize -- equal, nothing is compressed
        w.Write((uint) 0);          // CompressionMethodIndex: None
        w.Write(sha1);
        // CompressionBlocks are serialized ONLY when the method index is non-zero.
        w.Write((byte) 0);          // Flags
        w.Write((uint) 0);          // CompressionBlockSize
    }

    /// <summary>
    /// EncodePakEntry's bitfield. For an uncompressed entry inside a pak below 4 GB this is
    /// exactly twelve bytes: three "fits in 32 bits" flags, the offset, and the size.
    /// </summary>
    private static void EncodeEntry(BinaryWriter w, long offset, long size)
    {
        if (offset > uint.MaxValue || size > uint.MaxValue)
            throw new NotSupportedException("a pak over 4 GB needs the 64-bit encoded form");

        const uint offsetIs32Bit = 1u << 31;
        const uint uncompressedSizeIs32Bit = 1u << 30;
        const uint sizeIs32Bit = 1u << 29;
        // Method index 0, not encrypted, zero compression blocks, packed block size 0.
        w.Write(offsetIs32Bit | uncompressedSizeIs32Bit | sizeIs32Bit);
        w.Write((uint) offset);
        w.Write((uint) size);
        // The compressed-size field follows only when the method index is non-zero.
    }

    private static byte[] BuildPathHashIndex(List<(ulong Hash, int Location)> pathHashes)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(pathHashes.Count);
        foreach (var (hash, location) in pathHashes) { w.Write(hash); w.Write(location); }

        // The block does not end with the map. UnrealPak writes a PRUNED directory index straight
        // after it, and the runtime reads one straight after it, so leaving it out shifts nothing
        // -- the block is simply four bytes short and the reader runs off the end of it. Pruning
        // is a shipping-game memory optimisation driven by DirectoryIndexKeepFiles, and with no
        // such config the pruned index is empty: one count of zero.
        w.Write(0);

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildDirectoryIndex(
        Dictionary<string, List<(string Name, int Location)>> directories)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(directories.Count);
        foreach (var (directory, files) in directories)
        {
            WriteFString(w, directory);
            w.Write(files.Count);
            foreach (var (name, location) in files) { WriteFString(w, name); w.Write(location); }
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildPrimaryIndex(string mountPoint, int numEntries, ulong seed,
                                            long pathHashOffset, long directoryOffset,
                                            byte[] encoded, byte[] pathHashIndex, byte[] directoryIndex)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        WriteFString(w, mountPoint);
        w.Write(numEntries);
        w.Write(seed);

        // A bool goes to a binary archive as a UINT32, not a byte.
        w.Write((uint) 1);                              // bHasPathHashIndex
        w.Write(pathHashOffset);
        w.Write((long) pathHashIndex.Length);
        w.Write(SHA1.HashData(pathHashIndex));

        w.Write((uint) 1);                              // bHasFullDirectoryIndex
        w.Write(directoryOffset);
        w.Write((long) directoryIndex.Length);
        w.Write(SHA1.HashData(directoryIndex));

        w.Write(encoded.Length);
        w.Write(encoded);
        w.Write(0);                                     // no non-encoded entries

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// FPakFile::SplitPathInline. A file with no directory lands in the root, whose key is the
    /// single character "/"; anything deeper keeps the trailing slash and drops the leading one.
    /// </summary>
    private static (string Directory, string Name) SplitPath(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? ("/", relative) : (relative[..(slash + 1)], relative[(slash + 1)..]);
    }

    /// <summary>Every ancestor of a directory, because UE adds them as empty entries.</summary>
    private static IEnumerable<string> ParentsOf(string directory)
    {
        while (directory != "/")
        {
            var trimmed = directory.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            directory = slash < 0 ? "/" : trimmed[..(slash + 1)];
            yield return directory;
        }
    }

    /// <summary>An FString: a length INCLUDING the terminator, then the bytes.</summary>
    private static void WriteFString(BinaryWriter w, string s)
    {
        if (s.Length == 0) { w.Write(0); return; }
        var bytes = Encoding.ASCII.GetBytes(s);
        w.Write(bytes.Length + 1);
        w.Write(bytes);
        w.Write((byte) 0);
    }

    private static string ReadFString(BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n == 0) return "";
        return n > 0
            ? Encoding.ASCII.GetString(r.ReadBytes(n)[..^1])
            : Encoding.Unicode.GetString(r.ReadBytes(-n * 2)[..^2]);
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}

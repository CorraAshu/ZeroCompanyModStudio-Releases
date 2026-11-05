using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// The container header chunk -- the package store that tells the runtime which packages a
/// container holds and what each one imports.
///
/// <see cref="IoStoreWriter"/> can write a container, but only by carrying this chunk through
/// verbatim, which limits it to repackaging what a cook already produced. Authoring a NEW package
/// means adding its id and its store entry here; a package chunk whose id is not in this list is
/// invisible, and the loader never asks for it.
///
/// Reading it, writing it, and comparing the two is the whole gate. See <c>--iohdr</c>.
/// </summary>
public sealed class ContainerHeader
{
    /// <summary>'nCoI' -- the signature every container header starts with.</summary>
    public const uint Signature = 0x496F436E;

    /// <summary>
    /// EIoContainerHeaderVersion. 5 = SoftPackageReferencesOffset, the latest in UE 5.6.
    /// A 5.6 runtime REFUSES anything below 3 outright (it sets the archive error and stops).
    /// </summary>
    public const uint VersionNoExportInfo = 3;
    public const uint VersionSoftPackageReferences = 4;
    public const uint VersionSoftPackageReferencesOffset = 5;

    /// <summary>EIoChunkType::ContainerHeader.</summary>
    public const byte ChunkType = 6;

    /// <summary>The size of one store entry's fixed part: two 8-byte array views.</summary>
    private const int StoreEntrySize = 16;

    /// <summary>
    /// One package's store entry. In UE 5.6 this is exactly sixteen bytes on disk -- two array
    /// views -- because export info was dropped at header version 3.
    /// </summary>
    public sealed record StoreEntry(List<ulong> ImportedPackages, List<byte[]> ShaderMapHashes);

    public uint Version = VersionSoftPackageReferencesOffset;
    public ulong ContainerId;

    /// <summary>Parallel to <see cref="Store"/>, index for index.</summary>
    public List<ulong> PackageIds = new();
    public List<StoreEntry> Store = new();

    public List<ulong> OptionalSegmentPackageIds = new();
    public List<StoreEntry> OptionalSegmentStore = new();

    /// <summary>Names referenced ONLY by the redirect and localisation tables below.</summary>
    public List<string> RedirectsNames = new();
    public List<(ulong SourcePackageId, uint NameIndex, uint NameNumber)> LocalizedPackages = new();
    public List<(ulong Source, ulong Target, uint NameIndex, uint NameNumber)> PackageRedirects = new();

    /// <summary>
    /// Kept as raw bytes. It is a nested table with the same relative-offset encoding as the store
    /// entries, and nothing here needs to change it -- a hand-written container declares no soft
    /// references at all, which is a single 32-bit false.
    /// </summary>
    public byte[] SoftPackageReferences = Array.Empty<byte>();

    /// <summary>
    /// The chunk id this header lives under: the container id, then a zero chunk index in network
    /// order, a zero group, and the type. Derived the same way on the writing and mounting side,
    /// so it follows from the container id and nothing else.
    /// </summary>
    public static byte[] ChunkIdFor(ulong containerId)
    {
        var id = new byte[12];
        BitConverter.TryWriteBytes(id.AsSpan(0, 8), containerId);
        id[8] = 0; id[9] = 0;      // chunk index 0, big-endian -- zero reads the same either way
        id[10] = 0;                // chunk group, bulk data only
        id[11] = ChunkType;
        return id;
    }

    public static ContainerHeader Read(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var h = new ContainerHeader();

        var signature = r.ReadUInt32();
        if (signature != Signature)
            throw new InvalidDataException($"not a container header: signature 0x{signature:X8}");
        h.Version = r.ReadUInt32();
        if (h.Version < VersionNoExportInfo)
            throw new NotSupportedException($"container header version {h.Version} predates UE 5.6");
        h.ContainerId = r.ReadUInt64();

        h.PackageIds = ReadPackageIds(r);
        h.Store = ReadStoreEntries(r.ReadBytes(r.ReadInt32()), h.PackageIds.Count);

        h.OptionalSegmentPackageIds = ReadPackageIds(r);
        h.OptionalSegmentStore = ReadStoreEntries(r.ReadBytes(r.ReadInt32()),
                                                  h.OptionalSegmentPackageIds.Count);

        h.RedirectsNames = ReadNameBatch(r);

        var localized = r.ReadInt32();
        for (var i = 0; i < localized; i++)
            h.LocalizedPackages.Add((r.ReadUInt64(), r.ReadUInt32(), r.ReadUInt32()));

        var redirects = r.ReadInt32();
        for (var i = 0; i < redirects; i++)
            h.PackageRedirects.Add((r.ReadUInt64(), r.ReadUInt64(), r.ReadUInt32(), r.ReadUInt32()));

        if (h.Version >= VersionSoftPackageReferencesOffset)
        {
            // An offset ABSOLUTE from the start of this chunk, then the blob it points at. The
            // writer emits the pair, then the blob, then seeks back and patches the pair.
            var offset = r.ReadInt64();
            var size = r.ReadInt64();
            if (size > 0 && offset >= 0 && offset + size <= data.LongLength)
            {
                r.BaseStream.Position = offset;
                h.SoftPackageReferences = r.ReadBytes((int) size);
            }
        }
        else if (h.Version >= VersionSoftPackageReferences)
        {
            h.SoftPackageReferences = r.ReadBytes((int) (data.Length - r.BaseStream.Position));
        }

        return h;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write(Signature);
        w.Write(Version);
        w.Write(ContainerId);

        WritePackageIds(w, PackageIds);
        var store = BuildStoreEntries(Store);
        w.Write(store.Length);
        w.Write(store);

        WritePackageIds(w, OptionalSegmentPackageIds);
        var optional = BuildStoreEntries(OptionalSegmentStore);
        w.Write(optional.Length);
        w.Write(optional);

        WriteNameBatch(w, RedirectsNames);

        w.Write(LocalizedPackages.Count);
        foreach (var (id, index, number) in LocalizedPackages)
        { w.Write(id); w.Write(index); w.Write(number); }

        w.Write(PackageRedirects.Count);
        foreach (var (source, target, index, number) in PackageRedirects)
        { w.Write(source); w.Write(target); w.Write(index); w.Write(number); }

        if (Version >= VersionSoftPackageReferencesOffset)
        {
            var patchAt = ms.Position;
            w.Write((long) 0);
            w.Write((long) 0);
            var blobAt = ms.Position;
            w.Write(SoftPackageReferences);
            var end = ms.Position;

            ms.Position = patchAt;
            w.Write(SoftPackageReferences.Length > 0 ? blobAt : -1L);
            w.Write((long) SoftPackageReferences.Length);
            ms.Position = end;
        }
        else if (Version >= VersionSoftPackageReferences)
        {
            w.Write(SoftPackageReferences);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static List<ulong> ReadPackageIds(BinaryReader r)
    {
        var n = r.ReadInt32();
        var ids = new List<ulong>(n);
        for (var i = 0; i < n; i++) ids.Add(r.ReadUInt64());
        return ids;
    }

    private static void WritePackageIds(BinaryWriter w, List<ulong> ids)
    {
        w.Write(ids.Count);
        foreach (var id in ids) w.Write(id);
    }

    /// <summary>
    /// The store entries blob: a table of fixed-size entries, then a data region they point into.
    ///
    /// An array view is a count and an offset, and the offset is relative to THE VIEW'S OWN
    /// ADDRESS -- not the entry's, and not the blob's. Getting that base wrong by the eight bytes
    /// between the two views is the classic way to read a package's shader hashes as its imports.
    /// </summary>
    private static List<StoreEntry> ReadStoreEntries(byte[] blob, int count)
    {
        var entries = new List<StoreEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var at = i * StoreEntrySize;
            entries.Add(new StoreEntry(
                ReadView(blob, at, 8, span => BitConverter.ToUInt64(span)),
                ReadView(blob, at + 8, 20, span => span.ToArray())));
        }
        return entries;
    }

    private static List<T> ReadView<T>(byte[] blob, int fieldPos, int stride,
                                       Func<ReadOnlySpan<byte>, T> read)
    {
        var num = BitConverter.ToUInt32(blob, fieldPos);
        var offset = BitConverter.ToUInt32(blob, fieldPos + 4);
        var items = new List<T>((int) num);
        if (num == 0) return items;                       // offset is written as 0, not a real one
        var at = fieldPos + (int) offset;
        for (var i = 0; i < num; i++)
            items.Add(read(blob.AsSpan(at + i * stride, stride)));
        return items;
    }

    private static byte[] BuildStoreEntries(List<StoreEntry> entries)
    {
        if (entries.Count == 0) return Array.Empty<byte>();

        var tocSize = entries.Count * StoreEntrySize;
        using var toc = new MemoryStream();
        using var payload = new MemoryStream();
        using var tw = new BinaryWriter(toc, Encoding.UTF8, leaveOpen: true);
        using var pw = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);

        void View(int count, Action write)
        {
            // The payload's position measured from the view field that points at it. The view is
            // about to occupy the eight bytes at toc.Position, so that is the base.
            var fieldPos = (int) toc.Position;
            var dataPos = tocSize + (int) payload.Position;
            tw.Write((uint) count);
            // A zero-length array MUST record offset zero rather than where its data would have
            // gone -- a C++ consumer calls Data() unconditionally and would follow a live pointer
            // into another package's payload.
            tw.Write(count > 0 ? (uint) (dataPos - fieldPos) : 0u);
            if (count > 0) write();
        }

        foreach (var e in entries)
        {
            // Imports and shader hashes interleave PER PACKAGE -- not all imports first.
            View(e.ImportedPackages.Count, () => { foreach (var id in e.ImportedPackages) pw.Write(id); });
            View(e.ShaderMapHashes.Count, () => { foreach (var h in e.ShaderMapHashes) pw.Write(h); });
        }

        tw.Flush(); pw.Flush();
        if (toc.Length != tocSize) throw new InvalidOperationException("store entry table is the wrong size");

        var result = new byte[tocSize + payload.Length];
        toc.ToArray().CopyTo(result, 0);
        payload.ToArray().CopyTo(result, tocSize);
        return result;
    }

    /// <summary>
    /// A name batch in its ARCHIVE form: counts, hashes, two-byte headers, then the characters
    /// packed end to end. Unlike the standalone blob form there is no padding between the regions.
    /// An empty batch is four bytes -- a zero count and nothing else, not even the string size.
    /// </summary>
    private static List<string> ReadNameBatch(BinaryReader r)
    {
        var names = new List<string>();
        var count = r.ReadUInt32();
        if (count == 0) return names;

        var stringBytes = r.ReadUInt32();
        r.ReadUInt64();                                   // hash algorithm id
        for (var i = 0u; i < count; i++) r.ReadUInt64();  // per-name hashes

        var headers = new (bool Utf16, int Length)[count];
        for (var i = 0u; i < count; i++)
        {
            var a = r.ReadByte();
            var b = r.ReadByte();
            headers[i] = ((a & 0x80) != 0, ((a & 0x7F) << 8) | b);
        }

        var strings = r.ReadBytes((int) stringBytes);
        var at = 0;
        foreach (var (utf16, length) in headers)
        {
            var size = utf16 ? length * 2 : length;
            names.Add(utf16
                ? Encoding.Unicode.GetString(strings, at, size)
                : Encoding.ASCII.GetString(strings, at, size));
            at += size;
        }
        return names;
    }

    private static void WriteNameBatch(BinaryWriter w, List<string> names)
    {
        w.Write((uint) names.Count);
        if (names.Count == 0) return;                     // and NOTHING else follows

        var encoded = names.Select(n => n.All(c => c < 128)
            ? (Utf16: false, Bytes: Encoding.ASCII.GetBytes(n), Length: n.Length)
            : (Utf16: true, Bytes: Encoding.Unicode.GetBytes(n), Length: n.Length)).ToList();

        w.Write((uint) encoded.Sum(e => e.Bytes.Length));
        w.Write(NameHash.AlgorithmId);
        foreach (var n in names) w.Write(NameHash.Of(n));
        foreach (var e in encoded)
        {
            w.Write((byte) ((e.Utf16 ? 0x80 : 0) | ((e.Length >> 8) & 0x7F)));
            w.Write((byte) (e.Length & 0xFF));
        }
        foreach (var e in encoded) w.Write(e.Bytes);
    }
}

using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// A cooked package in Zen format -- the shape UE packages take inside a .ucas, as opposed to the
/// legacy .uasset/.uexp pair.
///
/// This matters because the game will not load a legacy package: tested on 2026-08-29 with a valid
/// legacy package at the correct path inside a mounted .pak, and it never appeared. So a mod tool
/// that does not need Unreal has to write THIS.
///
/// Layout, confirmed against a shipped package:
///     FZenPackageSummary        52 bytes on UE 5.3+
///     FZenPackageVersioningInfo only when the summary says so. Cooked packages say no.
///     FZenPackageCellOffsets    8 bytes, from the Verse-cells engine version onward
///     name batch                THE SAME FORMAT THE ASSET REGISTRY USES, hashes and all
///     everything else           import map, export map, bundles, then the export data
///
/// Only the name batch is decoded. Everything before it is fixed-size and copied; everything after
/// is addressed by byte offsets held in the summary, so as long as the name batch keeps its size
/// those offsets stay correct and the bytes can be carried verbatim. Same reasoning as
/// <see cref="RegistryDocument"/>: what is never interpreted cannot be misinterpreted.
/// </summary>
public sealed class ZenPackage
{
    /// <summary>Bytes before the name batch: the summary, and the cell offsets if present.</summary>
    public byte[] Head = [];

    public ulong HashVersion;
    public List<string> Names = new();
    public List<bool> NameIsUtf16 = new();

    /// <summary>Everything from the end of the name batch to the end of the file.</summary>
    public byte[] Tail = [];

    /// <summary>Where the name batch was found. Recorded because it is DERIVED, not assumed.</summary>
    public int NameBatchOffset;

    /// <summary>
    /// The file offset <see cref="Tail"/> begins at, as the package was READ.
    ///
    /// Everything the summary points at past the name batch -- the import map, the export map, the
    /// bundles -- is addressed absolutely, so turning one of those offsets into an index into Tail
    /// needs the number the name batch actually ended at. Derived on read rather than recomputed,
    /// because recomputing it is the step that would silently drift from the bytes in hand.
    /// </summary>
    public int TailStart;

    /// <summary>The summary offset of the imported public export hashes: one ulong per import.</summary>
    private const int ImportedPublicExportHashesOffsetField = 24;

    /// <summary>The summary offset of the import map: one FPackageObjectIndex per import.</summary>
    private const int ImportMapOffsetField = 28;


    /// <summary>
    /// The import map, as the raw FPackageObjectIndex values.
    ///
    /// A hard reference in export data is an FPackageIndex: negative means "import", and
    /// -(n + 1) is entry n here. That is the four bytes the Armoury reads a weapon's picture
    /// through, and the four bytes a copy of a part that has no texture import cannot write.
    /// </summary>
    public ulong[] ImportMap => Section(ImportMapOffsetField, ExportMapOffsetField);

    /// <summary>
    /// One export hash per import, in the same order: what the importing package expects to find
    /// in the package it is importing from.
    ///
    /// A cloned package keeps its TEMPLATE's stored hash -- nothing here recomputes one -- which is
    /// why redirecting an import at a clone resolves at all.
    /// </summary>
    public ulong[] ImportedPublicExportHashes => Section(ImportedPublicExportHashesOffsetField, ImportMapOffsetField);

    /// <summary>Where PublicExportHash sits inside an export map entry.</summary>
    private const int PublicExportHashAt = 56;

    /// <summary>The export map's size in bytes -- for checking the stride above against a package.</summary>
    public int ExportMapBytes
    {
        get
        {
            if (Head.Length < ExportBundleEntriesOffsetField + 4) return 0;
            var from = (int) BitConverter.ToUInt32(Head, ExportMapOffsetField) - TailStart;
            var to = (int) BitConverter.ToUInt32(Head, ExportBundleEntriesOffsetField) - TailStart;
            return from < 0 || to < from || to > Tail.Length ? 0 : to - from;
        }
    }

    /// <summary>
    /// The PUBLIC EXPORT HASH of each export: the number another package's import matches on.
    ///
    /// Nothing here computes one. A clone keeps the hash its template stored, which is precisely
    /// why an import redirected at a clone still resolves -- and is what lets a hard reference be
    /// pointed at a texture this tool authored.
    /// </summary>
    public ulong[] PublicExportHashes
    {
        get
        {
            var bytes = ExportMapBytes;
            if (bytes <= 0 || bytes % ExportMapEntryBytes != 0) return [];
            var from = (int) BitConverter.ToUInt32(Head, ExportMapOffsetField) - TailStart;
            var hashes = new ulong[bytes / ExportMapEntryBytes];
            for (var i = 0; i < hashes.Length; i++)
                hashes[i] = BitConverter.ToUInt64(Tail, from + i * ExportMapEntryBytes + PublicExportHashAt);
            return hashes;
        }
    }

    /// <summary>
    /// The object name of each export, in export-map order -- the order <see cref="PublicExportHashes"/>
    /// lists their hashes in, so an export's hash can be found by its name. A numbered name comes
    /// back joined, "Foo_2", the way the game spells it.
    ///
    /// Exists because an import names ONE export of a package, and a recipe package's class export
    /// is not its first: the one that recruits a character is `Recipe_X_C`, third of seven.
    /// </summary>
    public string[] ExportNames
    {
        get
        {
            var bytes = ExportMapBytes;
            if (bytes <= 0 || bytes % ExportMapEntryBytes != 0) return [];
            var from = (int) BitConverter.ToUInt32(Head, ExportMapOffsetField) - TailStart;
            var names = new string[bytes / ExportMapEntryBytes];
            for (var i = 0; i < names.Length; i++)
            {
                var at = from + i * ExportMapEntryBytes + ObjectNameAt;
                if (at + 8 > Tail.Length) { names[i] = ""; continue; }
                var packed = BitConverter.ToUInt32(Tail, at);
                var number = BitConverter.ToUInt32(Tail, at + 4);
                var index = (int) (packed & 0x3FFFFFFFu);
                var name = (packed & 0xC0000000u) == 0 && index >= 0 && index < Names.Count ? Names[index] : "";
                names[i] = number == 0 ? name : $"{name}_{number - 1}";
            }
            return names;
        }
    }

    /// <summary>
    /// The OUTER of each export, as the index of another export in this package -- or -1 for a
    /// top-level export (and for an outer that is not an export, which a cooked package does not
    /// write). Same order as <see cref="ExportNames"/>.
    ///
    /// Exists so an export with no counterpart in another package can be traced to the one that
    /// OWNS it: a gameplay effect's components hang off its default object, and when two effects
    /// share no components, the default object is what both still have.
    /// </summary>
    public int[] ExportOuters
    {
        get
        {
            var bytes = ExportMapBytes;
            if (bytes <= 0 || bytes % ExportMapEntryBytes != 0) return [];
            var from = (int) BitConverter.ToUInt32(Head, ExportMapOffsetField) - TailStart;
            var outers = new int[bytes / ExportMapEntryBytes];
            for (var i = 0; i < outers.Length; i++)
            {
                var at = from + i * ExportMapEntryBytes + OuterIndexAt;
                var value = at + 8 <= Tail.Length ? BitConverter.ToUInt64(Tail, at) : ulong.MaxValue;
                outers[i] = KindOf(value) == ImportKind.Export && (long) (value & 0x3FFFFFFFFFFFFFFFul) < outers.Length
                    ? (int) (value & 0x3FFFFFFFFFFFFFFFul) : -1;
            }
            return outers;
        }
    }

    /// <summary>A run of ulongs between two summary offsets, as an array. Empty when malformed.</summary>
    private ulong[] Section(int fromOffsetAt, int toOffsetAt)
    {
        if (Head.Length < toOffsetAt + 4) return [];
        var from = (int) BitConverter.ToUInt32(Head, fromOffsetAt) - TailStart;
        var to = (int) BitConverter.ToUInt32(Head, toOffsetAt) - TailStart;
        if (from < 0 || to < from || to > Tail.Length || (to - from) % 8 != 0) return [];
        var values = new ulong[(to - from) / 8];
        for (var i = 0; i < values.Length; i++) values[i] = BitConverter.ToUInt64(Tail, from + i * 8);
        return values;
    }

    /// <summary>The summary offset of the imported package NAMES: a name batch of package paths.</summary>
    private const int ImportedPackageNamesOffsetField = 48;

    /// <summary>
    /// Give this package a HARD reference to one export of another package, and return the
    /// FPackageIndex that names it -- the four bytes an ObjectProperty holds.
    ///
    /// THIS IS WHAT THE ARMOURY DRAWS A WEAPON'S PICTURE THROUGH. Measured in game 2026-09-02: a
    /// brush carrying only the soft path draws the game's fallback even when that path points at a
    /// texture the game itself ships, and the one game part that draws a texture carries the same
    /// brush plus this. A copy of a part with no texture import had no way to say it.
    ///
    /// Three runs grow together, because an import is not one number:
    ///   ImportedPublicExportHashes  what to look for in the other package
    ///   ImportMap                   (which imported package, which of those hashes)
    ///   ImportedPackageNames        the path, as a name batch of its own
    /// and a fourth lives outside the package entirely -- the container store entry's
    /// ImportedPackages, which is what <paramref name="importedPackageIndex"/> indexes. The caller
    /// owns that one; passing an index the store entry does not have gives an import that resolves
    /// to nothing, which is exactly the failure this is fixing.
    ///
    /// Appending rather than inserting is deliberate: every existing FPackageIndex keeps its
    /// meaning, so nothing already in the export data has to be rewritten.
    /// </summary>
    public int AddPackageImport(int importedPackageIndex, ulong publicExportHash,
                                string? importedPackageName)
    {
        var hashIndex = ImportedPublicExportHashes.Length;

        // AN EMPTY HASH SECTION STARTS WHERE IT STARTED, and the generic seam rule cannot know
        // that. A package with no imported public export hashes writes
        // ImportedPublicExportHashesOffset and ImportMapOffset as the SAME number, so inserting
        // the first hash at that seam moves BOTH -- the section stays zero-length and the eight
        // bytes land in front of it. The import map entry then asks for hash 0 of 0, which is
        // exactly what the build's own read-back reported on the first mod to import anything
        // into an authored package: "the import map entry was written and its hash was not".
        //
        // Only the import map moves. Measured on /AudioModulation/Pan retyped as a
        // GameFeatureData: 3 imports, 0 hashes, the two offsets equal.
        var hashesAt = BitConverter.ToUInt32(Head, ImportedPublicExportHashesOffsetField);
        var importsAt = BitConverter.ToUInt32(Head, ImportMapOffsetField);

        InsertIntoTail((int) importsAt - TailStart, BitConverter.GetBytes(publicExportHash));

        if (hashesAt == importsAt)
            BitConverter.GetBytes(hashesAt).CopyTo(Head, ImportedPublicExportHashesOffsetField);

        var importIndex = ImportMap.Length;
        var entry = ((ulong) ImportKind.PackageImport << 62)
                    | ((ulong) (uint) importedPackageIndex << 32)
                    | (uint) hashIndex;
        InsertIntoTail((int) BitConverter.ToUInt32(Head, ExportMapOffsetField) - TailStart,
                       BitConverter.GetBytes(entry));

        if (!string.IsNullOrEmpty(importedPackageName)) AppendImportedPackageName(importedPackageName);

        return -(importIndex + 1);
    }

    /// <summary>
    /// Give this package a SCRIPT import -- a native class or struct, named by the hash the global
    /// script object map resolves (SlotAdd.UIDataStruct and friends) -- and return the FPackageIndex
    /// naming it. Reused when already there. A script import is the one kind of import that is only
    /// its eight bytes: no public-export hash, no imported package name.
    /// </summary>
    public int AddScriptImport(ulong scriptObjectIndex)
    {
        if (KindOf(scriptObjectIndex) != ImportKind.ScriptImport)
            throw new ArgumentException($"0x{scriptObjectIndex:X16} is not a script import");
        var imports = ImportMap;
        var existing = Array.IndexOf(imports, scriptObjectIndex);
        if (existing >= 0) return -(existing + 1);

        // AN EMPTY IMPORT MAP STARTS WHERE IT STARTED -- the same seam problem AddPackageImport
        // works around for an empty hash section, but here TWO sections collapse onto the seam at
        // once, not one. ImportedPublicExportHashes holds one hash per PACKAGE import only
        // (AddPackageImport grows both together), so an import map with nothing in it -- neither
        // kind -- has zero package imports too, and its hash section is empty in step: both
        // ImportedPublicExportHashesOffset and ImportMapOffset then sit at the very same seam as
        // ExportMapOffset. Inserting the first entry there moves every offset "at or past" it under
        // InsertIntoTail's rule -- all three, not just the import map's own start -- and restoring
        // only ImportMapOffset would leave ImportedPublicExportHashesOffset sitting PAST it: an
        // inverted section. Both sections precede the export map and neither may move; only
        // ExportMapOffset (and what follows it) is genuinely growing away from them.
        var hashesAt = BitConverter.ToUInt32(Head, ImportedPublicExportHashesOffsetField);
        var importsAt = BitConverter.ToUInt32(Head, ImportMapOffsetField);
        var exportsAt = BitConverter.ToUInt32(Head, ExportMapOffsetField);

        InsertIntoTail((int) exportsAt - TailStart, BitConverter.GetBytes(scriptObjectIndex));

        if (importsAt == exportsAt)
            BitConverter.GetBytes(importsAt).CopyTo(Head, ImportMapOffsetField);
        if (hashesAt == exportsAt)
            BitConverter.GetBytes(hashesAt).CopyTo(Head, ImportedPublicExportHashesOffsetField);

        return -(imports.Length + 1);
    }

    /// <summary>
    /// Add one path to the imported-package-names batch, which is the same shape as the main one.
    ///
    /// Rewritten whole rather than spliced in three places: a name batch keeps its hashes, its
    /// length headers and its strings in three separate runs, so "append a name" is an insertion
    /// into each of them and re-emitting is both shorter and harder to get subtly wrong.
    /// </summary>
    private void AppendImportedPackageName(string name)
    {
        if (Head.Length < ImportedPackageNamesOffsetField + 4) return;
        var at = (int) BitConverter.ToUInt32(Head, ImportedPackageNamesOffsetField) - TailStart;
        var end = (int) BitConverter.ToUInt32(Head, 4) - TailStart;   // HeaderSize: the export data
        if (at < 0 || end <= at || end > Tail.Length) return;

        if (at + 4 > Tail.Length) return;
        var count = BitConverter.ToInt32(Tail, at);
        if (count < 0 || count > 100_000) return;

        // AN EMPTY BATCH IS FOUR BYTES, NOT SIXTEEN, and until a mod authored a package from a
        // template that imports nothing this had never been seen. A batch with no names writes
        // the count and STOPS -- no string-bytes field, no hash version, no runs and no trailer --
        // so reading a header out of it walks off the end of the section and throws where the
        // caller can only report "not long enough to read a value".
        //
        // MEASURED on a feature authored from /AudioModulation/Pan, which imports three SCRIPT
        // objects and no packages at all: offset 149, end 153, `00 00 00 00`. The first import a
        // mod adds to such a package therefore has to BUILD the batch rather than grow one, and
        // the version is the package's own name-batch hash version because both batches are
        // hashed the same way.
        int stringBytes, hashesAt, headersAt, stringsAt, batchEnd;
        ulong version;
        if (count == 0)
        {
            stringBytes = 0;
            version = HashVersion;
            hashesAt = headersAt = stringsAt = batchEnd = at + 4;
        }
        else
        {
            if (at + 16 > Tail.Length) return;
            stringBytes = BitConverter.ToInt32(Tail, at + 4);
            version = BitConverter.ToUInt64(Tail, at + 8);
            hashesAt = at + 16;
            headersAt = hashesAt + count * 8;
            stringsAt = headersAt + count * 2;
            if (stringsAt + stringBytes > Tail.Length) return;
            batchEnd = stringsAt + stringBytes;
        }
        if (batchEnd > end) return;

        // THE BATCH IS NOT THE LAST THING IN THE HEADER. Measured on this game's packages: between
        // the end of the names batch and HeaderSize there are exactly 4 bytes PER IMPORTED PACKAGE
        // (77 names -> 308 bytes, 12 -> 48, 1 -> 4), an int32 each, nearly all zero. Until
        // 2026-09-07 this rewrote everything up to HeaderSize as the batch alone, which THREW THAT
        // TABLE AWAY -- and because the seam rule below then moved the section's own offset by the
        // shrink, ImportedPackageNamesOffset pointed 231 bytes BEFORE its data. CUE4Parse never
        // reads the names, so the read-back stayed clean; the game's loader crashed at the main
        // menu (FAsyncPackage2::ProcessExportDependencies). The table is carried verbatim and
        // grown by one zero entry, the value 76 of the 77 measured entries hold.
        var trailer = Tail.AsSpan(batchEnd, end - batchEnd).ToArray();
        var perPackage = trailer.Length == count * 4;

        var added = Encoding.UTF8.GetBytes(name);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(count + 1);
        w.Write(stringBytes + added.Length);
        w.Write(version);
        w.Write(Tail, hashesAt, count * 8);
        w.Write(NameHash.Of(name));
        w.Write(Tail, headersAt, count * 2);
        w.Write((byte) (added.Length >> 8));
        w.Write((byte) (added.Length & 0xFF));
        w.Write(Tail, stringsAt, stringBytes);
        w.Write(added);
        w.Write(trailer);
        if (perPackage) w.Write(0);
        w.Flush();

        ReplaceInTail(at, end - at, ms.ToArray());
    }

    /// <summary>
    /// Insert bytes into <see cref="Tail"/> and move every summary offset that pointed at or past
    /// the seam.
    ///
    /// "At or past", not "past": a section whose offset EQUALS the insertion point is the one being
    /// grown from behind, and it now begins later. Getting that boundary wrong shifts a whole run
    /// by eight bytes and the package still parses, which is the worst way for it to be wrong.
    /// </summary>
    private void InsertIntoTail(int index, byte[] bytes) => ReplaceInTail(index, 0, bytes);

    private void ReplaceInTail(int index, int oldLength, byte[] bytes)
    {
        if (index < 0 || oldLength < 0 || index + oldLength > Tail.Length)
            throw new InvalidDataException($"edit at {index}+{oldLength} is outside the {Tail.Length}-byte tail");

        var grown = new byte[Tail.Length - oldLength + bytes.Length];
        Tail.AsSpan(0, index).CopyTo(grown);
        bytes.CopyTo(grown, index);
        Tail.AsSpan(index + oldLength).CopyTo(grown.AsSpan(index + bytes.Length));
        Tail = grown;

        var delta = bytes.Length - oldLength;
        if (delta == 0) return;
        var seam = (uint) (TailStart + index);
        // An INSERTION at a seam grows the section that ends there, so an offset AT the seam is
        // the next section's start and moves. A REPLACEMENT begins a section: the offset at the
        // seam is that section's own start and stays put; what moves is everything at or past the
        // replaced run's end. Treating both alike is what sent ImportedPackageNamesOffset
        // backwards by the bytes a rewrite had dropped.
        var moveFrom = oldLength == 0 ? seam : seam + (uint) oldLength;
        foreach (var field in MovableOffsets)
        {
            if (field + 4 > Head.Length) continue;
            var value = BitConverter.ToUInt32(Head, field);
            if (value >= moveFrom) BitConverter.GetBytes((uint) (value + delta)).CopyTo(Head, field);
        }
    }

    /// <summary>What an FPackageObjectIndex refers to. The top two bits say which.</summary>
    public enum ImportKind { Export = 0, ScriptImport = 1, PackageImport = 2, Null = 3 }

    /// <summary>The kind of one import map entry.</summary>
    public static ImportKind KindOf(ulong packageObjectIndex) =>
        packageObjectIndex == ulong.MaxValue ? ImportKind.Null
                                             : (ImportKind) (packageObjectIndex >> 62);

    /// <summary>
    /// The two numbers a PackageImport entry actually carries: WHICH imported package, and WHICH
    /// of this package's stored hashes to look for inside it.
    ///
    /// Both are needed together and neither is the entry's own index in the import map, which is
    /// the mistake that makes an import map look like it lines up with the hash run when it does
    /// not. A weapon's gear kit imports six exports out of one package: six import entries, six
    /// different hash indices, one package index shared between them.
    /// </summary>
    public static (int Package, int Hash) PackageImportOf(ulong packageObjectIndex) =>
        ((int) ((packageObjectIndex >> 32) & 0x3FFFFFFF), (int) (uint) packageObjectIndex);

    /// <summary>
    /// Overwrite one entry of <see cref="ImportedPublicExportHashes"/>.
    ///
    /// EIGHT BYTES FOR EIGHT BYTES, so nothing moves: no offset in the summary changes, no export
    /// shifts, and the package needs no other repair. That is the whole reason re-pointing a hard
    /// import at an unrelated asset is a safe edit rather than a rebuild -- see
    /// <see cref="ModBuild"/>'s repointImports, which pairs it with the store entry's package list.
    /// </summary>
    public void SetImportedPublicExportHash(int index, ulong hash)
    {
        if (Head.Length < ImportMapOffsetField + 4) return;
        var from = (int) BitConverter.ToUInt32(Head, ImportedPublicExportHashesOffsetField) - TailStart;
        var to = (int) BitConverter.ToUInt32(Head, ImportMapOffsetField) - TailStart;
        var at = from + index * 8;
        if (from < 0 || at < from || at + 8 > to || at + 8 > Tail.Length) return;
        BitConverter.GetBytes(hash).CopyTo(Tail, at);
    }

    public uint HeaderSize => BitConverter.ToUInt32(Head, 4);
    public bool HasVersioningInfo => BitConverter.ToUInt32(Head, 0) != 0;

    /// <summary>The summary itself, before any optional block.</summary>
    private const int SummaryBytes = 52;

    /// <summary>
    /// Replace a run of bytes inside the EXPORT DATA and fix the export map to match.
    ///
    /// The name batch can be resized because everything after it is addressed by offsets in the
    /// summary (see <see cref="RenameNames"/>). Export data cannot: each export records its own
    /// offset and size, so changing one export's length moves every export after it and silently
    /// corrupts them unless the map is corrected too.
    ///
    /// This exists for one job -- giving a part its own display name. That name is an FText, and
    /// the game's parts store it as a STRING TABLE reference whose text lives in a separate asset,
    /// so there is no name to rename and no string to patch in place. The only way to say
    /// "Anakin Sleeves (Mod)" is to write a different FText, which is a different length.
    ///
    /// Offsets are absolute in the file. Export offsets are relative to HeaderSize, which is where
    /// the export data begins (IoPackage calls that allExportDataOffset).
    /// </summary>
    public static byte[] SpliceExportData(byte[] raw, int start, int oldLength, byte[] replacement)
    {
        var headerSize = (int) BitConverter.ToUInt32(raw, 4);
        var exportMapOffset = BitConverter.ToInt32(raw, ExportMapOffsetField);
        var exportBundleEntriesOffset = BitConverter.ToInt32(raw, ExportBundleEntriesOffsetField);

        if (start < headerSize || start + oldLength > raw.Length)
            throw new ArgumentOutOfRangeException(nameof(start), "the edit is not inside the export data");
        var count = (exportBundleEntriesOffset - exportMapOffset) / ExportMapEntryBytes;
        if (count <= 0) throw new InvalidDataException("no export map to correct");

        var delta = replacement.Length - oldLength;
        var relative = (ulong) (start - headerSize);

        var output = new byte[raw.Length + delta];
        Array.Copy(raw, 0, output, 0, start);
        replacement.CopyTo(output, start);
        Array.Copy(raw, start + oldLength, output, start + replacement.Length,
                   raw.Length - start - oldLength);

        var touched = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = exportMapOffset + i * ExportMapEntryBytes;
            var offset = BitConverter.ToUInt64(raw, entry);
            var size = BitConverter.ToUInt64(raw, entry + 8);

            // The one export the edit lands in grows or shrinks; every export that STARTS after it
            // moves. Compared rather than assumed sorted, because nothing promises the map is in
            // offset order.
            if (relative >= offset && relative < offset + size)
            {
                if (relative + (ulong) oldLength > offset + size)
                    throw new InvalidDataException("the edit straddles two exports");
                BitConverter.GetBytes((ulong) ((long) size + delta)).CopyTo(output, entry + 8);
                touched++;
            }
            else if (offset > relative)
            {
                BitConverter.GetBytes((ulong) ((long) offset + delta)).CopyTo(output, entry);
            }
        }

        if (touched != 1)
            throw new InvalidDataException($"the edit belongs to {touched} exports, not one");
        return output;
    }

    /// <summary>
    /// Where one export's serialised data sits in the file: its absolute start and its length.
    ///
    /// Export offsets are relative to HeaderSize (see <see cref="SpliceExportData"/>), so this is
    /// the one place the two are added, rather than every caller doing its own sum.
    /// </summary>
    public static (int Start, int Length) ExportDataRange(byte[] raw, int exportIndex)
    {
        var exportMapOffset = BitConverter.ToInt32(raw, ExportMapOffsetField);
        var count = (BitConverter.ToInt32(raw, ExportBundleEntriesOffsetField) - exportMapOffset) / ExportMapEntryBytes;
        if (exportIndex < 0 || exportIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(exportIndex), $"the package has {count} exports");

        var entry = exportMapOffset + exportIndex * ExportMapEntryBytes;
        var headerSize = (long) BitConverter.ToUInt32(raw, 4);
        return ((int) (headerSize + (long) BitConverter.ToUInt64(raw, entry)),
                (int) BitConverter.ToUInt64(raw, entry + 8));
    }

    /// <summary>Summary fields this needs by name rather than by position in a magic list.</summary>
    private const int ExportMapOffsetField = 32;
    private const int ExportBundleEntriesOffsetField = 36;
    public const int ExportMapEntryBytes = 72;

    public static ZenPackage Read(byte[] raw)
    {
        if (raw.Length < SummaryBytes + 16)
            throw new InvalidDataException("too short to be a Zen package");
        if (BitConverter.ToUInt32(raw, 0) != 0)
            throw new NotSupportedException(
                "this package carries versioning info; only cooked, unversioned packages are handled");

        // Whether the 8-byte Verse cell offsets are present depends on the engine version, and
        // guessing wrong shifts everything. Rather than carry a version table, TRY BOTH and let
        // the file decide: a name batch declares how many string bytes it holds, so only the
        // correct offset reconciles. A wrong one lands on arbitrary numbers and does not.
        foreach (var offset in new[] { SummaryBytes + 8, SummaryBytes })
        {
            if (TryReadNameBatch(raw, offset, out var pkg)) return pkg!;
        }

        throw new InvalidDataException(
            "could not locate the name batch at either candidate offset - the summary layout " +
            "does not match this engine version");
    }

    private static bool TryReadNameBatch(byte[] raw, int offset, out ZenPackage? result)
    {
        result = null;
        try
        {
            var p = offset;
            var num = BitConverter.ToInt32(raw, p); p += 4;
            var declaredStringBytes = BitConverter.ToInt32(raw, p); p += 4;
            var hashVersion = BitConverter.ToUInt64(raw, p); p += 8;

            if (num < 0 || num > 1_000_000) return false;
            if (hashVersion != NameHash.AlgorithmId) return false;
            if (p + num * 10 > raw.Length) return false;

            // Hashes are skipped and RECOMPUTED on write, so a round-trip proves NameHash here
            // as well -- exactly as it does for the asset registry.
            p += num * 8;
            var headers = p;
            p += num * 2;
            var stringStart = p;

            var names = new List<string>(num);
            var utf16 = new List<bool>(num);
            for (var i = 0; i < num; i++)
            {
                var b0 = raw[headers + i * 2];
                var b1 = raw[headers + i * 2 + 1];
                var length = ((b0 & 0x7F) << 8) | b1;
                var wide = (b0 & 0x80) != 0;
                if (p + (wide ? length * 2 : length) > raw.Length) return false;
                utf16.Add(wide);
                if (wide)
                {
                    names.Add(Encoding.Unicode.GetString(raw, p, length * 2));
                    p += length * 2;
                }
                else
                {
                    names.Add(Encoding.UTF8.GetString(raw, p, length));
                    p += length;
                }
            }

            if (p - stringStart != declaredStringBytes) return false;

            result = new ZenPackage
            {
                Head = raw[..offset],
                HashVersion = hashVersion,
                Names = names,
                NameIsUtf16 = utf16,
                Tail = raw[p..],
                NameBatchOffset = offset,
                TailStart = p,
            };
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Every byte offset in the summary that points PAST the name batch, and so moves when the
    /// name batch changes size. Positions within <see cref="Head"/>.
    ///
    /// CookedHeaderSize (byte 20) is deliberately not here. It records what the LEGACY header size
    /// would have been, not a position in this file, and shifting it would be wrong.
    /// </summary>
    private static readonly int[] MovableOffsets =
    {
        4,   // HeaderSize -- also where the export data starts
        24,  // ImportedPublicExportHashesOffset
        28,  // ImportMapOffset
        32,  // ExportMapOffset
        36,  // ExportBundleEntriesOffset
        40,  // DependencyBundleHeadersOffset
        44,  // DependencyBundleEntriesOffset
        48,  // ImportedPackageNamesOffset
        52,  // CellImportMapOffset   \ present only when Head is 60 bytes
        56,  // CellExportMapOffset   /
    };

    /// <summary>
    /// Replace names, and move the offsets that a change in the name batch's size displaces.
    ///
    /// This is what makes authoring a part tractable without a general property serialiser.
    /// Everything that distinguishes one customization part from another is a NAME: the mesh path,
    /// the icon path, every slot tag, and the part's own name and package path. A part's whole name
    /// map is sixteen entries. Only the display text lives in property bytes.
    ///
    /// So a new part can be a known-good cooked one with its names swapped -- which inherits its
    /// entire structure, and therefore cannot get the structure wrong.
    ///
    /// The export map needs no adjustment: its CookedSerialOffset values are relative to the start
    /// of the export data, not to the file, so they move with it.
    /// </summary>
    /// <summary>
    /// Add a name to the map and return its index, moving everything after the batch to suit.
    ///
    /// Needed because a reference can only be redirected to a name the package ALREADY holds, and
    /// the one thing a mod most wants to add -- the tag that hides a character's face under a
    /// helmet -- is a name no outfit template carries. Existing indices are untouched, because the
    /// name is appended rather than inserted.
    /// </summary>
    public int AppendName(string name)
    {
        var existing = Names.IndexOf(name);
        if (existing >= 0) return existing;
        if (name.Any(c => c > 0x7F))
            throw new NotSupportedException($"non-ASCII name: {name}");

        var before = NameBatchBytes();
        Names.Add(name);
        NameIsUtf16.Add(false);
        Shift(NameBatchBytes() - before);
        return Names.Count - 1;
    }

    /// <summary>Move every offset that points past the name batch.</summary>
    private void Shift(int delta)
    {
        if (delta == 0) return;
        foreach (var at in MovableOffsets)
        {
            if (at + 4 > Head.Length) continue;
            var value = BitConverter.ToInt32(Head, at);
            BitConverter.GetBytes(value + delta).CopyTo(Head, at);
        }
    }

    public void RenameNames(IReadOnlyDictionary<string, string> substitutions)
    {
        var before = NameBatchBytes();

        for (var i = 0; i < Names.Count; i++)
            if (substitutions.TryGetValue(Names[i], out var replacement))
            {
                Names[i] = replacement;
                // A replacement that needs UTF-16 would change the encoding as well as the length.
                // Refused rather than silently mis-encoded; no path or tag in this game needs it.
                if (replacement.Any(c => c > 0x7F))
                    throw new NotSupportedException($"non-ASCII replacement name: {replacement}");
                NameIsUtf16[i] = false;
            }

        SplitNumberedNames();

        var delta = NameBatchBytes() - before;
        if (delta == 0) return;

        foreach (var at in MovableOffsets)
        {
            if (at + 4 > Head.Length) continue;
            var value = BitConverter.ToInt32(Head, at);
            BitConverter.GetBytes(value + delta).CopyTo(Head, at);
        }
    }

    /// <summary>Bytes into an FExportMapEntry where its ObjectName FMappedName begins.</summary>
    private const int ObjectNameAt = 16;

    /// <summary>Bytes into an FExportMapEntry where its OuterIndex FPackageObjectIndex begins.</summary>
    private const int OuterIndexAt = 24;

    /// <summary>Bytes into the summary where the package's own name FMappedName begins.</summary>
    private const int SummaryNameAt = 8;

    /// <summary>
    /// An FName's number lives in the FMappedName, not in the string -- so a name ending in
    /// `_<digits>` has to be stored SPLIT.
    ///
    /// UE's FName is (entry, Number), and a trailing _N is taken off into the number whenever a
    /// name is built from a string: `Foo_2` interns as ("Foo", 3). The cooker stores it that way
    /// too. The game's own Male_Neutral_TG_Agree_2.uasset holds "...Male_Neutral_TG_Agree" in its
    /// name map with Name.Number = 3, and its export is ("Male_Neutral_TG_Agree", 3).
    ///
    /// This writer had no Number concept and wrote the literal with Number 0. FPackageId is a hash
    /// of the STRING, so such a package still mounts and every structural check passes -- and
    /// nothing that resolves the object BY NAME can ever match it. Confirmed in game: a part whose
    /// id ended in _2 drew a menu tile with no name and equipped nothing, and renaming it to end
    /// in a letter fixed it outright.
    ///
    /// ONLY THE TWO FMappedNames WHOSE MEANING IS KNOWN ARE TOUCHED -- the summary's package name
    /// and each export's object name. Export DATA also holds FMappedNames (a soft object path is
    /// two of them), and re-pointing those would need every one of them found; missing one would
    /// leave a reference to an entry that no longer says what it used to. Everything else keeps
    /// pointing at the literal entry, which is exactly what it did before this existed.
    /// </summary>
    private void SplitNumberedNames()
    {
        if (Head.Length >= SummaryNameAt + 8) Split(Head, SummaryNameAt);

        // The export map, addressed the same way ExportMapBytes addresses it -- through the CURRENT
        // summary offsets, which are still pre-delta here, as Tail's own indices are.
        var bytes = ExportMapBytes;
        if (bytes <= 0 || bytes % ExportMapEntryBytes != 0) return;
        var from = (int) BitConverter.ToUInt32(Head, ExportMapOffsetField) - TailStart;
        if (from < 0 || from + bytes > Tail.Length) return;

        for (var i = 0; i < bytes / ExportMapEntryBytes; i++)
            Split(Tail, from + i * ExportMapEntryBytes + ObjectNameAt);
    }

    /// <summary>
    /// Re-point one FMappedName at the STEM of its name and give it the number, when its name ends
    /// in a splittable `_<digits>` and it does not already carry one.
    /// </summary>
    private void Split(byte[] buffer, int at)
    {
        if (at + 8 > buffer.Length) return;

        var packed = BitConverter.ToUInt32(buffer, at);
        var number = BitConverter.ToUInt32(buffer, at + 4);
        // Already numbered -- the cooker's own split, carried through untouched.
        if (number != 0) return;

        // The top two bits say which name map this index belongs to; only a package-local name is
        // ours to re-point.
        if ((packed & 0xC0000000u) != 0) return;

        var index = (int) (packed & 0x3FFFFFFFu);
        if (index < 0 || index >= Names.Count) return;
        var (stem, value) = ParseNumber(Names[index]);
        if (stem is null) return;

        var stemIndex = Names.FindIndex(n => string.Equals(n, stem, StringComparison.Ordinal));
        if (stemIndex < 0)
        {
            Names.Add(stem);
            NameIsUtf16.Add(false);
            stemIndex = Names.Count - 1;
        }

        BitConverter.GetBytes((uint) stemIndex).CopyTo(buffer, at);
        BitConverter.GetBytes(value).CopyTo(buffer, at + 4);
    }

    /// <summary>
    /// A name's stem and the FName number it implies, or (null, 0) when it does not split.
    ///
    /// UE's own rules, not an approximation of them -- see FNameHelper::ParseNumber. The number
    /// stored is ONE MORE than the digits, because 0 means "no number at all". A leading zero
    /// refuses to split, which is why "Rocket_04" stays literal and "Rocket_4" does not; getting
    /// that backwards would corrupt every name the cooker deliberately left whole.
    /// </summary>
    public static (string? Stem, uint Number) ParseNumber(string name)
    {
        var at = name.LastIndexOf('_');
        if (at <= 0 || at == name.Length - 1) return (null, 0);

        var digits = name[(at + 1)..];
        if (digits.Length > 10 || !digits.All(char.IsAsciiDigit)) return (null, 0);
        if (digits.Length > 1 && digits[0] == '0') return (null, 0);
        if (!uint.TryParse(digits, out var value) || value == uint.MaxValue) return (null, 0);

        return (name[..at], value + 1);
    }

    /// <summary>Size the name batch serialises to, for working out how far everything after moves.</summary>
    private int NameBatchBytes()
    {
        var total = 16 + Names.Count * 8 + Names.Count * 2;
        for (var i = 0; i < Names.Count; i++)
            total += NameIsUtf16[i]
                ? Encoding.Unicode.GetByteCount(Names[i])
                : Encoding.UTF8.GetByteCount(Names[i]);
        return total;
    }

    public byte[] Write()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write(Head);

        w.Write(Names.Count);
        var stringBytes = 0;
        for (var i = 0; i < Names.Count; i++)
            stringBytes += NameIsUtf16[i]
                ? Encoding.Unicode.GetByteCount(Names[i])
                : Encoding.UTF8.GetByteCount(Names[i]);
        w.Write(stringBytes);
        w.Write(HashVersion);

        foreach (var n in Names) w.Write(NameHash.Of(n));

        for (var i = 0; i < Names.Count; i++)
        {
            var len = NameIsUtf16[i]
                ? Encoding.Unicode.GetByteCount(Names[i]) / 2
                : Encoding.UTF8.GetByteCount(Names[i]);
            w.Write((byte) ((NameIsUtf16[i] ? 0x80 : 0) | (len >> 8)));
            w.Write((byte) (len & 0xFF));
        }

        for (var i = 0; i < Names.Count; i++)
            w.Write(NameIsUtf16[i]
                ? Encoding.Unicode.GetBytes(Names[i])
                : Encoding.UTF8.GetBytes(Names[i]));

        w.Write(Tail);
        w.Flush();
        return ms.ToArray();
    }
}

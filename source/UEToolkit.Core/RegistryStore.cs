using CUE4Parse.UE4.Versions;

namespace UEToolkit.Core;

/// <summary>
/// The tag value store inside an AssetRegistry.bin, as byte ranges we can rebuild.
///
/// Every tag value in the registry -- 44,664 assets' worth -- lives in one shared store of eleven
/// parallel arrays, and each asset points into it. That sharing is why the whole file is 26 MB
/// rather than hundreds.
///
/// This deliberately does NOT decode most of it. A merge only ever APPENDS: new names go on the
/// end of the name batch, new values on the end of the string table, new pairs on the end of the
/// pair array. Appending leaves every existing index valid, which means the existing bytes can be
/// copied verbatim -- and bytes copied verbatim cannot be corrupted by a misunderstanding of a
/// field this code never has to interpret.
///
/// So the sections that only grow at the end are kept as raw spans, and only the three things a
/// merge actually touches are decoded: the counts, the ANSI string table, and the pair arrays.
/// </summary>
public sealed class RegistryStore
{
    public const uint BeginMagicOld = 0x12345678u;
    public const uint BeginMagic = 0x12345679u;
    public const uint EndMagic = 0x87654321u;

    /// <summary>Where the store began in the source file, and where it ended.</summary>
    public int Start, End;

    public uint Magic;
    /// <summary>The eleven section counts, in the order the format writes them.</summary>
    public int[] Nums = new int[11];
    /// <summary>The four bytes after the counts. Copied rather than interpreted.</summary>
    public uint Pad;

    // Raw spans, in write order. Each is the section's bytes with its leading alignment removed;
    // alignment is recomputed on write, which reproduces it exactly as long as sizes are unchanged.
    public byte[] Texts = [];
    public byte[] NumberlessNames = [];
    public byte[] Names = [];
    public byte[] NumberlessExportPaths = [];
    public byte[] ExportPaths = [];
    public byte[] AnsiStringOffsets = [];
    public byte[] WideStringOffsets = [];
    public byte[] AnsiStrings = [];
    public byte[] WideStrings = [];
    public byte[] NumberlessPairs = [];
    public byte[] Pairs = [];

    /// <summary>
    /// Whether this registry version pads its store sections to 16 bytes.
    ///
    /// Version-gated, and the gate is easy to miss because CUE4Parse hides it inside
    /// AlignPosInArchive, which quietly does nothing below MemoryMappedTagDataStore. This game
    /// ships version 21 and does NOT align -- its store magic sits at byte 16,608,665, one byte
    /// past the name batch rather than at the next boundary. Assuming alignment put the reader
    /// seven bytes late and it read a length as a magic number.
    /// </summary>
    public bool Aligned;

    private int Align(int p) => Aligned ? (p + 15) & ~15 : p;

    /// <summary>
    /// Read the store that begins at or after <paramref name="from"/>.
    ///
    /// Sizes for the variable-length sections are found by WALKING them rather than computed,
    /// because a registry FName is one uint32 or two depending on a flag bit in the first.
    /// </summary>
    public static RegistryStore Read(byte[] raw, int from, FAssetRegistryVersionType version)
    {
        var s = new RegistryStore
        {
            Aligned = version >= FAssetRegistryVersionType.MemoryMappedTagDataStore,
        };
        var p = s.Align(from);
        s.Start = p;

        s.Magic = BitConverter.ToUInt32(raw, p); p += 4;
        if (s.Magic != BeginMagic && s.Magic != BeginMagicOld)
            throw new InvalidDataException(
                $"asset registry store has bad magic 0x{s.Magic:X8} at {s.Start}");
        // Only the newer, text-first layout is handled. The older one interleaves the text array
        // differently and no game here ships it; failing loudly beats writing a file that is
        // subtly in the wrong order.
        if (s.Magic != BeginMagic)
            throw new NotSupportedException("this registry uses the pre-text-first store layout");

        for (var i = 0; i < 11; i++) { s.Nums[i] = BitConverter.ToInt32(raw, p); p += 4; }
        s.Pad = BitConverter.ToUInt32(raw, p); p += 4;

        s.Texts = s.Section(raw, ref p, () => SkipStrings(raw, ref p, s.Nums[4]));
        s.NumberlessNames = s.Section(raw, ref p, () => p += s.Nums[0] * 4);
        s.Names = s.Section(raw, ref p, () => SkipNames(raw, ref p, s.Nums[1]));
        s.NumberlessExportPaths = s.Section(raw, ref p, () => p += s.Nums[2] * 16);
        s.ExportPaths = s.Section(raw, ref p, () => SkipExportPaths(raw, ref p, s.Nums[3]));
        s.AnsiStringOffsets = s.Section(raw, ref p, () => p += s.Nums[5] * 4);
        s.WideStringOffsets = s.Section(raw, ref p, () => p += s.Nums[6] * 4);
        s.AnsiStrings = s.Section(raw, ref p, () => p += s.Nums[7]);
        s.WideStrings = s.Section(raw, ref p, () => p += s.Nums[8] * 2);
        s.NumberlessPairs = s.Section(raw, ref p, () => p += s.Nums[9] * 8);
        s.Pairs = s.Section(raw, ref p, () => SkipPairs(raw, ref p, s.Nums[10]));

        var end = BitConverter.ToUInt32(raw, p); p += 4;
        if (end != EndMagic)
            throw new InvalidDataException($"asset registry store has bad end magic 0x{end:X8}");
        s.End = p;
        return s;
    }

    /// <summary>Align, run the walker, and hand back the bytes it walked over.</summary>
    private byte[] Section(byte[] raw, ref int p, Action walk)
    {
        p = Align(p);
        var start = p;
        walk();
        return raw[start..p];
    }

    /// <summary>A registry FName: a uint32 index, plus a uint32 number when the top bit is set.</summary>
    private static void SkipNames(byte[] raw, ref int p, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var index = BitConverter.ToUInt32(raw, p); p += 4;
            if ((index & 0x80000000u) != 0) p += 4;
        }
    }

    private static void SkipExportPaths(byte[] raw, ref int p, int count)
    {
        // Class (an FTopLevelAssetPath: two FNames), Object, Package.
        for (var i = 0; i < count; i++) SkipNames(raw, ref p, 4);
    }

    private static void SkipPairs(byte[] raw, ref int p, int count)
    {
        for (var i = 0; i < count; i++)
        {
            SkipNames(raw, ref p, 1);
            p += 4; // FValueId
        }
    }

    private static void SkipStrings(byte[] raw, ref int p, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var len = BitConverter.ToInt32(raw, p); p += 4;
            // Negative means UTF-16 and the magnitude is the character count.
            p += len >= 0 ? len : -len * 2;
        }
    }

    /// <summary>Write the store back, reproducing the alignment padding from the section sizes.</summary>
    public void Write(BinaryWriter w, long baseOffset)
    {
        AlignTo(w, baseOffset);
        w.Write(Magic);
        foreach (var n in Nums) w.Write(n);
        w.Write(Pad);

        foreach (var section in new[]
                 {
                     Texts, NumberlessNames, Names, NumberlessExportPaths, ExportPaths,
                     AnsiStringOffsets, WideStringOffsets, AnsiStrings, WideStrings,
                     NumberlessPairs, Pairs,
                 })
        {
            AlignTo(w, baseOffset);
            w.Write(section);
        }

        w.Write(EndMagic);
    }

    /// <summary>
    /// Pad to the next 16-byte boundary.
    ///
    /// Measured from <paramref name="baseOffset"/> -- the start of the file -- not from the start
    /// of the store, because that is what the reader aligns against. Aligning relative to the
    /// store would agree only when the store happens to begin on a boundary.
    /// </summary>
    private void AlignTo(BinaryWriter w, long baseOffset)
    {
        if (!Aligned) return;
        var pos = w.BaseStream.Position - baseOffset;
        var pad = (int) (((pos + 15) & ~15L) - pos);
        for (var i = 0; i < pad; i++) w.Write((byte) 0);
    }
}

using CUE4Parse.UE4.Versions;

namespace UEToolkit.Core;

/// <summary>
/// The FAssetData records in a registry: how to walk them, and how to write a new one.
///
/// A merge never rewrites the existing records -- see <see cref="RegistryDocument"/> for why --
/// so the walk exists purely to CHECK this code's understanding of the format before it writes
/// one. Over the shipped registry it must consume exactly the bytes between the store and the
/// tail: 5,422,444 of them across 120,757 records. A format that is wrong by one field drifts and
/// the total will not land, which is a far better test than eyeballing a single record.
/// </summary>
public static class RegistryAssets
{
    /// <summary>
    /// A record, decoded to the name INDICES it holds. Indices rather than strings because that
    /// is what the bytes contain; a caller resolves them through the document's own name batch.
    ///
    /// The class PACKAGE is here because CUE4Parse drops it -- it keeps only the class asset name
    /// -- and writing a record needs both halves.
    /// </summary>
    public readonly record struct Record(
        int Start, int Length,
        uint PackagePath, uint ClassPackage, uint ClassName, uint PackageName, uint AssetName,
        ulong TagHandle);

    /// <summary>
    /// Walk every record. Throws if the walk does not land exactly on the end of the section,
    /// which is what makes this a test rather than a formality.
    /// </summary>
    public static List<Record> Walk(RegistryDocument doc)
    {
        var raw = doc.AssetBytes;
        var spans = new List<Record>(doc.AssetCount);
        var p = 0;
        var filterEditorOnly = BitConverter.ToInt32(doc.Header, 20) != 0;

        for (var i = 0; i < doc.AssetCount; i++)
        {
            var start = p;
            var packagePath = ReadName(raw, ref p);
            var classPackage = doc.Version >= FAssetRegistryVersionType.ClassPaths
                ? ReadName(raw, ref p)
                : 0u;
            var className = ReadName(raw, ref p);
            var packageName = ReadName(raw, ref p);
            var assetName = ReadName(raw, ref p);
            // Written only when editor-only data is KEPT. This game filters it, so the field is
            // absent -- and reading it anyway shifts every record after the first.
            if (doc.Version >= FAssetRegistryVersionType.RemoveAssetPathFNames && !filterEditorOnly)
                SkipName(raw, ref p);                                // OptionalOuterPath

            var handle = BitConverter.ToUInt64(raw, p); p += 8;
            SkipBundles(raw, ref p, doc.Version);

            var chunks = BitConverter.ToInt32(raw, p); p += 4;
            p += chunks * 4;
            p += 4;                                                  // PackageFlags

            spans.Add(new Record(start, p - start, packagePath, classPackage, className,
                packageName, assetName, handle));
        }

        if (p != raw.Length)
            throw new InvalidDataException(
                $"asset record walk ended at {p:N0} of {raw.Length:N0} bytes after " +
                $"{doc.AssetCount:N0} records - the record format is wrong");

        return spans;
    }

    /// <summary>
    /// Take assets OUT of a registry, by asset name, and say which went.
    ///
    /// An unlock mod is nothing but registry records: it re-registers a gated asset with the gate
    /// tag missing, and the menus -- which filter on the registry, not on the asset -- start
    /// offering it. That is how a mod unlocks a class. It is also how a mod unlocks something the
    /// author did not intend to, and until now there was no way to take one back without the
    /// project the mod was built from, which for an old mod may not exist.
    ///
    /// The real case: a mod that unlocked Captain Rex's pistols also unlocked
    /// CPD_WeaponSpec_Blaster_Pistol_Rex -- a weapon CLASS carrying the same display name as the
    /// ordinary pistol class, with nothing in the game keyed to it. Two identical "BLASTER PISTOL"
    /// rows in the Armoury, one of them empty.
    ///
    /// **The records are contiguous byte spans and nothing points INTO them**, so removing one is
    /// a splice plus a count. Names and tag maps in the store are shared and left alone: an
    /// orphaned name costs a few bytes and is what a merge would leave anyway. <see cref="Walk"/>
    /// runs again over the result, and it throws unless the walk lands exactly on the end -- so a
    /// bad splice is refused rather than shipped.
    /// </summary>
    public static List<string> Drop(RegistryDocument doc, Func<string, bool> unwanted)
    {
        var records = Walk(doc);
        var going = records.Where(r => unwanted(doc.Names[(int) r.AssetName])).ToList();
        if (going.Count == 0) return [];

        var kept = new MemoryStream();
        foreach (var r in records)
            if (!going.Contains(r)) kept.Write(doc.AssetBytes, r.Start, r.Length);

        doc.AssetBytes = kept.ToArray();
        doc.AssetCount -= going.Count;

        // Re-walked, because everything above this line is arithmetic on offsets.
        Walk(doc);
        return going.Select(r => doc.Names[(int) r.AssetName]).ToList();
    }

    private static void SkipName(byte[] raw, ref int p) => ReadName(raw, ref p);

    /// <summary>
    /// A registry FName. The top bit of the first word means a number follows; the returned index
    /// has it cleared, because that is what indexes the name batch.
    /// </summary>
    private static uint ReadName(byte[] raw, ref int p)
    {
        var index = BitConverter.ToUInt32(raw, p); p += 4;
        if ((index & 0x80000000u) == 0) return index;
        p += 4;
        return index & 0x7FFFFFFFu;
    }

    private static void SkipBundles(byte[] raw, ref int p, FAssetRegistryVersionType version)
    {
        var count = BitConverter.ToInt32(raw, p); p += 4;
        for (var i = 0; i < count; i++)
        {
            SkipName(raw, ref p);                                    // BundleName
            var assets = BitConverter.ToInt32(raw, p); p += 4;
            for (var j = 0; j < assets; j++)
            {
                SkipName(raw, ref p);
                if (version >= FAssetRegistryVersionType.ClassPaths) SkipName(raw, ref p);
                // SubPathString: a length, then that many bytes, or -length UTF-16 characters.
                var len = BitConverter.ToInt32(raw, p); p += 4;
                p += len >= 0 ? len : -len * 2;
            }
        }
    }

    /// <summary>One asset to add, in the vocabulary of the registry rather than the mod.</summary>
    public sealed record NewAsset(
        string PackageName,
        string AssetName,
        string PackagePath,
        string ClassPackage,
        string ClassName,
        IReadOnlyList<(string Key, string Value)> Tags);

    /// <summary>
    /// Append one asset: intern its names, put its tag values in the string table, its key/value
    /// pairs on the end of the pair array, and its record on the end of the asset section.
    ///
    /// Everything is appended, never inserted, so every index the existing 120,757 records hold
    /// stays valid and their bytes stay untouched.
    /// </summary>
    public static void Append(RegistryDocument doc, NewAsset asset)
    {
        var store = doc.Store;

        // --- values into the ANSI string table.
        //
        // "Ansi" is the format's name for it, not a constraint: the reader decodes it as UTF-8,
        // so a non-ASCII value is fine here and does not need the wide table.
        var offsets = new List<uint>(ReadUInts(store.AnsiStringOffsets));
        var strings = new List<byte>(store.AnsiStrings);

        var valueIds = new List<uint>(asset.Tags.Count);
        foreach (var (_, value) in asset.Tags)
        {
            offsets.Add((uint) strings.Count);
            strings.AddRange(System.Text.Encoding.UTF8.GetBytes(value));
            strings.Add(0);
            // FValueId packs the type into the low three bits. AnsiString is type 0.
            valueIds.Add((uint) ((offsets.Count - 1) << 3));
        }

        // --- key/value pairs, as NUMBERED pairs so the keys are ordinary names.
        //
        // The numberless variant would need an entry in a second name table as well; the numbered
        // one costs four bytes more per pair and keeps this to one place.
        var pairs = new List<byte>(store.Pairs);
        var pairBegin = store.Nums[10];
        for (var i = 0; i < asset.Tags.Count; i++)
        {
            pairs.AddRange(BitConverter.GetBytes(doc.InternName(asset.Tags[i].Key)));
            pairs.AddRange(BitConverter.GetBytes(valueIds[i]));
        }

        store.AnsiStringOffsets = ToBytes(offsets);
        store.AnsiStrings = strings.ToArray();
        store.Pairs = pairs.ToArray();
        store.Nums[5] = offsets.Count;
        store.Nums[7] = strings.Count;
        store.Nums[10] = pairBegin + asset.Tags.Count;

        // --- the record itself.
        var rec = new List<byte>(64);

        // A NAME ENDING IN _<digits> IS WRITTEN SPLIT, exactly as the package is.
        //
        // A registry FName is an index, and the top bit says a number word follows -- which is
        // what ReadName above has always understood on the way in and what this never produced on
        // the way out. UE takes a trailing _N off a name into that number, so "Foo_2" is
        // ("Foo", 3), and the cooker splits in the registry too: the game's own AssetRegistry.bin
        // holds "Male_Neutral_TG_Agree" and does not contain the literal "Male_Neutral_TG_Agree_2"
        // anywhere.
        //
        // THE TWO HALVES HAVE TO AGREE. Splitting only the package (which ZenPackage now does)
        // and leaving the registry literal is WORSE than leaving both alone: with both literal the
        // part at least drew a nameless tile, and with them disagreeing it vanishes from the menu
        // entirely. Confirmed in game, in that order, tonight.
        void Name(string n)
        {
            var (stem, number) = ZenPackage.ParseNumber(n);
            if (stem is null) { rec.AddRange(BitConverter.GetBytes(doc.InternName(n))); return; }

            rec.AddRange(BitConverter.GetBytes(doc.InternName(stem) | 0x80000000u));
            rec.AddRange(BitConverter.GetBytes(number));
        }

        Name(asset.PackagePath);
        if (doc.Version >= FAssetRegistryVersionType.ClassPaths) Name(asset.ClassPackage);
        Name(asset.ClassName);
        Name(asset.PackageName);
        Name(asset.AssetName);

        var filterEditorOnly = BitConverter.ToInt32(doc.Header, 20) != 0;
        if (doc.Version >= FAssetRegistryVersionType.RemoveAssetPathFNames && !filterEditorOnly)
            Name("None");

        // The handle: bit 63 says the keys are numberless (they are not), bits 32-47 the count,
        // bits 0-31 the index of the first pair.
        var handle = ((ulong) (ushort) asset.Tags.Count << 32) | (uint) pairBegin;
        rec.AddRange(BitConverter.GetBytes(handle));

        rec.AddRange(BitConverter.GetBytes(0));   // no asset bundles
        rec.AddRange(BitConverter.GetBytes(0));   // no chunk ids
        rec.AddRange(BitConverter.GetBytes(0));   // package flags

        var merged = new byte[doc.AssetBytes.Length + rec.Count];
        doc.AssetBytes.CopyTo(merged, 0);
        rec.CopyTo(merged, doc.AssetBytes.Length);
        doc.AssetBytes = merged;
        doc.AssetCount++;
    }

    private static IEnumerable<uint> ReadUInts(byte[] b)
    {
        for (var i = 0; i + 4 <= b.Length; i += 4) yield return BitConverter.ToUInt32(b, i);
    }

    private static byte[] ToBytes(List<uint> values)
    {
        var b = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++) BitConverter.GetBytes(values[i]).CopyTo(b, i * 4);
        return b;
    }
}

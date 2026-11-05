namespace UEToolkit.Core;

/// <summary>
/// ONE NEW EXPORT IN A COOKED ZEN PACKAGE: its export-map row, its place in the load order, its
/// dependencies, its public hash, and its data at the end of the file.
///
/// Generalised from <see cref="LevelAdd.AddActor"/>, which proved the approach in game, with two
/// things it did not do: the new export gets a public hash of its OWN (LevelAdd copies its donor's,
/// giving two exports one identity), and the export that lists it -- a part and its Fragments --
/// is made to wait for it, as every fragment the game ships is (measured on CPD_H_Outfit_Clo002_HELM:
/// the part's "serialize after create" group names all seven of its fragments).
///
/// The bundle entries and the dependency section are DECODED WHOLE, changed, and written back whole.
/// Unchanged exports re-encode to exactly the bytes they had; only the new row, its two bundle
/// entries, its dependency header and entries, and the lister's one extra entry differ.
/// </summary>
public static class ExportAdd
{
    // FZenPackageSummary fields (UE 5.6) -- the same offsets LevelAdd and ZenCheck measure.
    private const int HeaderSizeAt = 4;
    private const int ExportMapAt = 32;
    private const int ExportBundleEntriesAt = 36;
    private const int DependencyBundleHeadersAt = 40;
    private const int DependencyBundleEntriesAt = 44;
    private const int ImportedPackageNamesAt = 48;
    private const int CellImportMapAt = 52;
    private const int CellExportMapAt = 56;

    private const int EntryBytes = 72;            // FExportMapEntry
    private const int BundleEntryBytes = 8;       // FExportBundleEntry: LocalExportIndex, CommandType
    private const int DependencyHeaderBytes = 20; // FDependencyBundleHeader: FirstEntryIndex + EntryCount[2][2]

    // Inside an FExportMapEntry.
    private const int SerialOffsetAt = 0, SerialSizeAt = 8, NameIndexAt = 16, NameNumberAt = 20,
                      OuterIndexAt = 24, ClassIndexAt = 32, TemplateIndexAt = 48, PublicExportHashAt = 56;

    /// <summary>
    /// What to add. <paramref name="Donor"/> is an export of the same package whose row supplies
    /// the flags and filter flags; <paramref name="ClassIndex"/> and <paramref name="TemplateIndex"/>
    /// are FPackageObjectIndex values as the export map stores them (a script import is its hash).
    /// The four dependency arrays are FPackageIndex values in FDependencyBundleHeader's order:
    /// what the new export's CREATE waits on (created / serialised), then what its SERIALIZE waits on.
    /// </summary>
    public sealed record Spec(int Donor, int Outer, ulong ClassIndex, ulong TemplateIndex, string Name, byte[] Data,
                              int[] CreateAfterCreate, int[] CreateAfterSerialize, int[] SerializeAfterCreate,
                              int[] SerializeAfterSerialize, int ListedBy = -1);

    public static byte[]? Append(byte[] raw, Spec spec, out int newIndex, out string? why)
    {
        newIndex = -1;
        why = null;
        try
        {
            var headerSize = (int) BitConverter.ToUInt32(raw, HeaderSizeAt);
            var expMap = BitConverter.ToInt32(raw, ExportMapAt);
            var bundleAt = BitConverter.ToInt32(raw, ExportBundleEntriesAt);
            var depHeadAt = BitConverter.ToInt32(raw, DependencyBundleHeadersAt);
            var depEntryAt = BitConverter.ToInt32(raw, DependencyBundleEntriesAt);
            var impNamesAt = BitConverter.ToInt32(raw, ImportedPackageNamesAt);

            var exportCount = (bundleAt - expMap) / EntryBytes;
            bool In(int i) => i >= 0 && i < exportCount;
            if (!In(spec.Donor) || !In(spec.Outer) || (spec.ListedBy >= 0 && !In(spec.ListedBy)))
            { why = "the donor, outer or lister is outside the export map"; return null; }
            if ((depEntryAt - depHeadAt) / DependencyHeaderBytes != exportCount)
            { why = "the dependency headers do not match the export count"; return null; }

            var pkg = ZenPackage.Read(raw);

            // THE NEW ROW GOES AT bundleAt, which is only "the end of the export map" when the two
            // Verse cell maps between it and the export map are empty (see LevelAdd's note on
            // CellImportMapOffset/CellExportMapOffset). A non-empty cell map would put the row after
            // it while exportCount above still counted as if the whole span were 72-byte rows --
            // wrong silently, because ZenCheck has no rule for these two fields either.
            if (pkg.NameBatchOffset >= CellExportMapAt + 4)
            {
                var cellImportAt = BitConverter.ToInt32(raw, CellImportMapAt);
                var cellExportAt = BitConverter.ToInt32(raw, CellExportMapAt);
                if (cellImportAt != bundleAt || cellExportAt != bundleAt)
                { why = "the package's Verse cell maps are not empty -- not handled"; return null; }
            }

            if (pkg.ExportNames.Contains(spec.Name, StringComparer.OrdinalIgnoreCase))
            { why = $"an export called {spec.Name} already exists"; return null; }
            var (stem, number) = ZenPackage.ParseNumber(spec.Name);
            var nameIndex = pkg.Names.IndexOf(stem ?? spec.Name);
            if (nameIndex < 0) { why = $"'{stem ?? spec.Name}' is not in the name map -- append it first"; return null; }

            newIndex = exportCount;

            // 1. THE ROW: the donor's, with everything that must differ replaced.
            var entry = raw[(expMap + spec.Donor * EntryBytes)..(expMap + (spec.Donor + 1) * EntryBytes)];
            BitConverter.GetBytes((ulong) (raw.Length - headerSize)).CopyTo(entry, SerialOffsetAt);
            BitConverter.GetBytes((ulong) spec.Data.Length).CopyTo(entry, SerialSizeAt);
            BitConverter.GetBytes((uint) nameIndex).CopyTo(entry, NameIndexAt);
            BitConverter.GetBytes(stem is null ? 0u : number).CopyTo(entry, NameNumberAt);
            BitConverter.GetBytes((ulong) spec.Outer).CopyTo(entry, OuterIndexAt);
            BitConverter.GetBytes(spec.ClassIndex).CopyTo(entry, ClassIndexAt);
            BitConverter.GetBytes(spec.TemplateIndex).CopyTo(entry, TemplateIndexAt);
            var path = ExportHash.PathOf(pkg, spec.Outer) + "/" + spec.Name;
            BitConverter.GetBytes(ExportHash.Public(path)).CopyTo(entry, PublicExportHashAt);

            // 2. THE LOAD ORDER: Create at the end of the last Create run, Serialize at the very end.
            var bundles = new List<(int Export, uint Command)>();
            for (var at = bundleAt; at < depHeadAt; at += BundleEntryBytes)
                bundles.Add((BitConverter.ToInt32(raw, at), BitConverter.ToUInt32(raw, at + 4)));
            if (bundles.Count != exportCount * 2)
            { why = $"{bundles.Count} bundle entries for {exportCount} exports, expected two each"; newIndex = -1; return null; }
            var lastSerialRun = bundles.Count;
            while (lastSerialRun > 0 && bundles[lastSerialRun - 1].Command == 1) lastSerialRun--;
            if (lastSerialRun == 0 || lastSerialRun == bundles.Count)
            { why = "the bundle entries do not end in a Serialize run"; newIndex = -1; return null; }

            // M-6: an INTERLEAVED bundle (Create, Serialize, Create, Serialize, ...) could carry the
            // lister's own Serialize entry somewhere BEFORE lastSerialRun -- outside the trailing
            // run this walk found, but still ahead of where the new Create is about to land. The
            // dependency added below (step 3) tells the loader the lister's Serialize must wait for
            // this Create, but nothing makes the PHYSICAL ORDER agree with that promise, and neither
            // CUE4Parse nor ZenCheck reads bundle order against the dependency table to catch it.
            // MEASURED: every part looked at so far is all-Creates-then-all-Serializes, so this
            // refuses nothing today -- it is here for the shape that has not been seen yet.
            if (spec.ListedBy >= 0)
            {
                var listerSerialAt = bundles.FindIndex(b => b.Export == spec.ListedBy && b.Command == 1);
                if (listerSerialAt >= 0 && listerSerialAt < lastSerialRun)
                {
                    why = "the lister's Serialize entry sits before where the new export's Create " +
                          "would be inserted -- an interleaved bundle, not handled";
                    newIndex = -1;
                    return null;
                }
            }

            bundles.Insert(lastSerialRun, (newIndex, 0u));
            bundles.Add((newIndex, 1u));

            // 3. THE DEPENDENCIES, per export in four groups.
            var deps = new List<int[][]>();
            for (var i = 0; i < exportCount; i++)
            {
                var h = depHeadAt + i * DependencyHeaderBytes;
                var cursor = BitConverter.ToInt32(raw, h);
                var groups = new int[4][];
                for (var g = 0; g < 4; g++)
                {
                    var n = (int) BitConverter.ToUInt32(raw, h + 4 + g * 4);
                    groups[g] = new int[n];
                    for (var k = 0; k < n; k++) groups[g][k] = BitConverter.ToInt32(raw, depEntryAt + (cursor + k) * 4);
                    cursor += n;
                }
                deps.Add(groups);
            }
            if (spec.ListedBy >= 0) deps[spec.ListedBy][2] = [.. deps[spec.ListedBy][2], newIndex + 1];
            deps.Add([spec.CreateAfterCreate, spec.CreateAfterSerialize, spec.SerializeAfterCreate, spec.SerializeAfterSerialize]);

            // 4. THE HEADER, re-emitted from the export map on; everything before it is unchanged.
            using var ms = new MemoryStream();
            ms.Write(raw, 0, bundleAt);                                      // summary, names, imports, old rows
            ms.Write(entry);                                                 // the new row
            var newBundleAt = (int) ms.Length;
            foreach (var (e, c) in bundles) { ms.Write(BitConverter.GetBytes(e)); ms.Write(BitConverter.GetBytes(c)); }
            var newDepHeadAt = (int) ms.Length;
            var total = 0;
            foreach (var groups in deps)
            {
                ms.Write(BitConverter.GetBytes(total));
                foreach (var g in groups) { ms.Write(BitConverter.GetBytes((uint) g.Length)); total += g.Length; }
            }
            var newDepEntryAt = (int) ms.Length;
            foreach (var groups in deps) foreach (var g in groups) foreach (var v in g) ms.Write(BitConverter.GetBytes(v));
            var newImpNamesAt = (int) ms.Length;
            ms.Write(raw, impNamesAt, headerSize - impNamesAt);              // imported package names + their trailer
            var newHeaderSize = (int) ms.Length;
            ms.Write(raw, headerSize, raw.Length - headerSize);              // every export's data, unmoved relative to HeaderSize
            ms.Write(spec.Data);                                             // the new export's data
            var output = ms.ToArray();

            // 5. THE SUMMARY.
            BitConverter.GetBytes((uint) newHeaderSize).CopyTo(output, HeaderSizeAt);
            BitConverter.GetBytes(newBundleAt).CopyTo(output, ExportBundleEntriesAt);
            BitConverter.GetBytes(newDepHeadAt).CopyTo(output, DependencyBundleHeadersAt);
            BitConverter.GetBytes(newDepEntryAt).CopyTo(output, DependencyBundleEntriesAt);
            BitConverter.GetBytes(newImpNamesAt).CopyTo(output, ImportedPackageNamesAt);
            // The Verse cell offsets sit beside the bundle entries and move with them (see LevelAdd);
            // present only when the name batch starts past them.
            if (pkg.NameBatchOffset >= CellExportMapAt + 4)
            {
                Bump(output, CellImportMapAt, EntryBytes);
                Bump(output, CellExportMapAt, EntryBytes);
            }

            if (!ZenCheck.Ok(output, out var zw)) { why = "the grown package is malformed: " + zw; newIndex = -1; return null; }
            return output;
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            newIndex = -1;
            return null;
        }
    }

    private static void Bump(byte[] b, int at, int by) =>
        BitConverter.GetBytes(BitConverter.ToInt32(b, at) + by).CopyTo(b, at);
}

using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// Is this package's header self-consistent -- measured off the BYTES, through no parser.
///
/// WHY THIS EXISTS, and why it does not go through <see cref="ZenPackage"/> or CUE4Parse.
///
/// On 2026-09-07 a mod shipped that crashed the game at the main menu, inside
/// FAsyncPackage2::ProcessExportDependencies. The cause was <see cref="ZenPackage"/>'s
/// AppendImportedPackageName throwing away the int32-per-imported-package table that follows the
/// imported-package-names batch, which then dragged the section's own summary offset 231 bytes
/// backwards. The build read the result back and pronounced it clean, and so did every diagnostic
/// aimed at it, because NEITHER READER READS THAT SECTION. CUE4Parse does not; nor did we.
///
/// So the build's central promise -- "it reads the files back and checks them before anything
/// reaches the game" -- was false for a day, and it was false in the one way a read-back can never
/// catch: a reader cannot notice a section it has no model for. The arc packages came out SMALLER
/// after gaining a recipe and nothing said a word.
///
/// The fix for that class of fault is a check that never decodes anything. Every rule here is
/// arithmetic on the summary's own numbers -- offsets against each other, section sizes against
/// the counts they declare, the header against the file. A section this tool has never understood
/// is still MEASURED, because its size is stated by the two offsets either side of it. That is the
/// property a decoder cannot have and this can.
///
/// Every rule below was run over the pristine game and holds for every package it ships; the
/// sweep is `--zencheck`, kept so the rules can be re-proven after a game update rather than
/// believed. A rule that fires on the game's own files is a wrong rule, not a discovery.
/// </summary>
public static class ZenCheck
{
    // The summary's fields, by byte position. Named rather than indexed so a rule reads as the
    // question it is asking.
    private const int HeaderSizeAt = 4;
    private const int ImportedPublicExportHashesAt = 24;
    private const int ImportMapAt = 28;
    private const int ExportMapAt = 32;
    private const int ExportBundleEntriesAt = 36;
    private const int DependencyBundleHeadersAt = 40;
    private const int DependencyBundleEntriesAt = 44;
    private const int ImportedPackageNamesAt = 48;

    /// <summary>Bytes per export map entry. The same stride <see cref="ZenPackage"/> uses.</summary>
    private const int ExportMapEntryBytes = ZenPackage.ExportMapEntryBytes;

    /// <summary>
    /// One measurable run of the header: how many things it holds, and how many bytes it occupies.
    ///
    /// The pair is the point. A count on its own cannot catch a section that lost bytes, and a
    /// size on its own cannot catch a section that lost entries; the fault that shipped moved them
    /// in opposite directions -- one more name, 231 fewer bytes -- and only the pair says so.
    /// </summary>
    public sealed record Run(string Name, int Count, int Bytes);

    /// <summary>What a package's header says about itself.</summary>
    public sealed record Shape(int HeaderSize, int FileBytes, long ExportDataBytes, Run[] Runs)
    {
        public Run? this[string name] =>
            Array.Find(Runs, r => r.Name.Equals(name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every way <paramref name="raw"/> disagrees with itself. Empty means the header is sound.
    ///
    /// Never throws. A package too damaged to measure is a fault, not an exception -- the caller
    /// is a build reporting on several files and wants the rest of them checked.
    /// </summary>
    public static List<string> Faults(byte[] raw)
    {
        var faults = new List<string>();
        Measure(raw, faults);
        return faults;
    }

    /// <summary>The one-line form, for a caller that only needs a verdict and a reason.</summary>
    public static bool Ok(byte[] raw, out string? why)
    {
        var faults = Faults(raw);
        why = faults.Count == 0 ? null : string.Join("; ", faults);
        return faults.Count == 0;
    }

    /// <summary>
    /// Measure the header, adding a line to <paramref name="faults"/> for each disagreement.
    ///
    /// Returns what it managed to measure even when it found faults, because the growth comparison
    /// wants the counts of a package that is already known to be wrong -- saying WHICH run lost
    /// bytes is most of the diagnosis.
    /// </summary>
    public static Shape? Measure(byte[] raw, List<string> faults)
    {
        if (raw.Length < 60)
        { faults.Add($"only {raw.Length} bytes: too short to be a Zen package header"); return null; }

        // Versioned packages carry an extra block between the summary and the name batch, and
        // nothing this tool writes is versioned. Say so and stop rather than measure the wrong
        // bytes -- a rule applied at the wrong offset invents faults, which is worse than none.
        if (BitConverter.ToUInt32(raw, 0) != 0)
        { faults.Add("carries versioning info, so its header is not the cooked layout"); return null; }

        var headerSize = (int) BitConverter.ToUInt32(raw, HeaderSizeAt);
        if (headerSize <= 0 || headerSize > raw.Length)
        {
            faults.Add($"HeaderSize {headerSize} is outside the {raw.Length:N0}-byte file");
            return null;
        }

        // WHERE THE MAIN NAME BATCH STARTS is decided by the file, not by a version table: the
        // 8-byte Verse cell offsets are present on some engine versions and absent on others, and
        // a batch declares how many string bytes it holds, so only the correct offset reconciles.
        // Same reasoning as ZenPackage.Read, and deliberately re-derived here rather than borrowed
        // -- a check that inherits the reader's assumption cannot catch the reader being wrong.
        var nameBatchAt = -1;
        var nameEnd = -1;
        var nameCount = 0;
        foreach (var candidate in new[] { 60, 52 })
        {
            // allowEmpty is false HERE on purpose. An empty batch is four zero bytes, which any
            // offset can produce by accident, and accepting one would let the locator settle on
            // the wrong candidate and measure every later rule against shifted bytes. A package
            // always names itself, so its own batch is never empty and nothing is lost.
            if (!Batch(raw, candidate, allowEmpty: false, out var c, out _, out var e)) continue;
            nameBatchAt = candidate; nameEnd = e; nameCount = c;
            break;
        }
        if (nameBatchAt < 0)
        { faults.Add("no name batch reconciles at either 52 or 60: the summary layout is not this engine's"); return null; }

        // ------------------------------------------------------------------ the offsets in order
        //
        // The summary's seven section offsets describe consecutive runs, so each is the end of the
        // one before it. Non-decreasing is therefore the whole rule, and it is the rule the
        // shipped fault broke: ImportedPackageNamesOffset went BACKWARDS past the section before
        // it. Nothing else in the tool was in a position to notice.
        var fields = new (string Name, int At)[]
        {
            ("ImportedPublicExportHashesOffset", ImportedPublicExportHashesAt),
            ("ImportMapOffset",                  ImportMapAt),
            ("ExportMapOffset",                  ExportMapAt),
            ("ExportBundleEntriesOffset",        ExportBundleEntriesAt),
            ("DependencyBundleHeadersOffset",    DependencyBundleHeadersAt),
            ("DependencyBundleEntriesOffset",    DependencyBundleEntriesAt),
            ("ImportedPackageNamesOffset",       ImportedPackageNamesAt),
        };
        var offsets = new int[fields.Length];
        for (var i = 0; i < fields.Length; i++) offsets[i] = (int) BitConverter.ToUInt32(raw, fields[i].At);

        var ordered = true;
        if (offsets[0] < nameEnd)
        {
            faults.Add($"{fields[0].Name} {offsets[0]} is before the name batch ends at {nameEnd}");
            ordered = false;
        }
        for (var i = 1; i < offsets.Length; i++)
        {
            if (offsets[i] >= offsets[i - 1]) continue;
            faults.Add($"{fields[i].Name} {offsets[i]} is before {fields[i - 1].Name} {offsets[i - 1]}");
            ordered = false;
        }
        if (offsets[^1] > headerSize)
        {
            faults.Add($"{fields[^1].Name} {offsets[^1]} is past HeaderSize {headerSize}");
            ordered = false;
        }

        // Sizes derived from offsets that are out of order are meaningless, and reporting a
        // negative-length section as a second fault would bury the one that matters.
        if (!ordered) return null;

        var runs = new List<Run> { new("names", nameCount, nameEnd - nameBatchAt) };

        // ------------------------------------------------------------------ the import runs
        //
        // An import is not one number: a public export hash says what to look for, and an import
        // map entry says where. They are written in step, one 8-byte entry each, so the two runs
        // must be the same size -- which makes this the check for an AddPackageImport that grew
        // one of them and not the other.
        var hashBytes = offsets[1] - offsets[0];
        var importBytes = offsets[2] - offsets[1];
        if (hashBytes % 8 != 0) faults.Add($"the imported public export hashes are {hashBytes} bytes, not a multiple of 8");
        if (importBytes % 8 != 0) faults.Add($"the import map is {importBytes} bytes, not a multiple of 8");
        var hashCount = hashBytes / 8;
        var importCount = importBytes / 8;
        var packageImports = 0;
        for (var i = 0; i < importCount; i++)
        {
            var at = offsets[1] + i * 8;
            if (at + 8 > raw.Length) break;
            var entry = BitConverter.ToUInt64(raw, at);
            if (ZenPackage.KindOf(entry) != ZenPackage.ImportKind.PackageImport) continue;
            packageImports++;
            var hashIndex = (int) (uint) entry;
            if (hashIndex < hashCount) continue;
            faults.Add($"import {i} asks for public export hash {hashIndex} of {hashCount}: " +
                       "the import map entry was written and its hash was not");
            break;
        }
        runs.Add(new Run("imports", importCount, importBytes));
        runs.Add(new Run("imported hashes", hashCount, hashBytes));

        // ------------------------------------------------------------------ the export map
        var exportMapBytes = offsets[3] - offsets[2];
        if (exportMapBytes % ExportMapEntryBytes != 0)
            faults.Add($"the export map is {exportMapBytes} bytes, which is not a whole number of " +
                       $"{ExportMapEntryBytes}-byte entries");
        var exportCount = exportMapBytes / ExportMapEntryBytes;
        runs.Add(new Run("exports", exportCount, exportMapBytes));

        // ------------------------------------------------------------------ imported package names
        //
        // THE SECTION THE CRASH WAS IN. Its batch is followed by exactly four bytes per imported
        // package -- the FName numbers of those paths, nearly all zero. Measured across the whole
        // pristine game: the gap between the batch's end and HeaderSize is count*4 in every
        // package that ships. A tool that rewrites the batch and stops at HeaderSize eats that
        // table, and this is the arithmetic that says so.
        var namesBytes = headerSize - offsets[6];
        var importedNames = 0;
        if (!Batch(raw, offsets[6], allowEmpty: true, out importedNames, out _, out var importedEnd))
            faults.Add($"the imported package names at {offsets[6]} do not reconcile as a name batch " +
                       $"({namesBytes} byte section)");
        else
        {
            var trailer = headerSize - importedEnd;
            if (trailer != importedNames * 4)
                faults.Add($"{importedNames} imported package name(s) but {trailer} byte(s) between the " +
                           $"batch and HeaderSize, not {importedNames * 4}: the per-package number table " +
                           "is the wrong size");
        }
        runs.Add(new Run("imported package names", importedNames, namesBytes));

        // ------------------------------------------------------------------ the export data
        //
        // Each export states its own offset (from HeaderSize) and size, so the file's length is
        // not an independent fact -- it is the furthest an export reaches. A splice that moved
        // the data and not the map, or the map and not the data, shows up here and nowhere else.
        long furthest = 0;
        var mapAt = offsets[2];
        for (var i = 0; i < exportCount; i++)
        {
            var entry = mapAt + i * ExportMapEntryBytes;
            if (entry + 16 > raw.Length) break;
            var off = (long) BitConverter.ToUInt64(raw, entry);
            var size = (long) BitConverter.ToUInt64(raw, entry + 8);
            if (off < 0 || size < 0 || headerSize + off + size > raw.Length)
            {
                faults.Add($"export {i} spans {headerSize + off}..{headerSize + off + size} of a " +
                           $"{raw.Length:N0}-byte file");
                break;
            }
            if (off + size > furthest) furthest = off + size;
        }
        if (exportCount > 0 && headerSize + furthest != raw.Length)
            faults.Add($"the export data ends at {headerSize + furthest:N0} but the file is " +
                       $"{raw.Length:N0} bytes ({raw.Length - headerSize - furthest:+#;-#;0})");

        return new Shape(headerSize, raw.Length, furthest, runs.ToArray());
    }

    /// <summary>
    /// Compare a package against the one it was made from: a run that GAINED entries must not have
    /// LOST bytes.
    ///
    /// This is the second half of the same lesson. The header of the corrupt arc package was
    /// internally consistent in every way a single file can be checked -- it had simply been
    /// rebuilt smaller, with one more name in it. Only the pristine package beside it says that is
    /// impossible.
    ///
    /// Deliberately one-directional: a run may lose entries and lose bytes (a rename can shorten
    /// the name batch), and this says nothing about that. It fires only on the combination that
    /// cannot happen.
    /// </summary>
    public static List<string> Growth(byte[] before, byte[] after)
    {
        var faults = new List<string>();
        var a = Measure(before, new List<string>());
        var b = Measure(after, faults);
        if (a is null || b is null) return faults;

        // Runs whose shrink is fully accounted for by entries that were swapped for shorter ones.
        var explained = new HashSet<string>(StringComparer.Ordinal);

        foreach (var run in b.Runs)
        {
            if (a[run.Name] is not { } was) continue;
            if (!(run.Count > was.Count && run.Bytes < was.Bytes)) continue;

            // A NAME BATCH IS ACCOUNTED FOR BY ITS NAMES, and "gained an entry, lost bytes" is not
            // impossible there -- it is what a COPY does. Force Grip appended one name and renamed
            // four long ones shorter (GA_Resonate_Enemy_Coil_Captain_C -> ZC_ForceGrip_C and three
            // like it), so its batch rightly grew by one entry and shrank by fourteen bytes, and
            // this refused it.
            //
            // Not by loosening the rule. A name costs a fixed eight-byte hash and two-byte header
            // plus its characters, so what the batch SHOULD weigh follows exactly from which names
            // appeared and which went. If it weighs that, every byte is explained and nothing was
            // dropped; if it weighs less, content really did go missing -- the arc package's
            // fault, which still fails.
            var gap = "";
            if (run.Name == "names" && NamesAccountFor(before, after, was.Bytes, run.Bytes, out gap))
            { explained.Add(run.Name); continue; }

            faults.Add($"'{run.Name}' went from {was.Count} entr(ies) in {was.Bytes:N0} bytes to " +
                       $"{run.Count} in {run.Bytes:N0}: it gained content and lost bytes" +
                       (run.Name == "names" ? $" ({gap})" : ""));
        }

        if (b.HeaderSize < a.HeaderSize &&
            b.Runs.Any(r => a[r.Name] is { } w && r.Count > w.Count && !explained.Contains(r.Name)))
            faults.Add($"the header shrank from {a.HeaderSize:N0} to {b.HeaderSize:N0} bytes while gaining entries");

        return faults;
    }

    /// <summary>
    /// Whether a name batch weighs exactly what its template's did, plus the names that appeared
    /// and minus the names that went.
    ///
    /// A Zen name batch stores every name as an 8-byte hash, a 2-byte header and its characters
    /// (one byte each, or two when any is outside ASCII), behind a fixed header that does not
    /// change. So the difference between two batches is entirely a matter of which names differ,
    /// and a batch that comes up SHORT of that sum has lost something no rename explains.
    /// </summary>
    private static bool NamesAccountFor(byte[] before, byte[] after, long wasBytes, long nowBytes,
                                        out string detail)
    {
        try
        {
            var was = ZenPackage.Read(before).Names;
            var now = ZenPackage.Read(after).Names;
            var gone = was.Except(now, StringComparer.Ordinal).ToList();
            var came = now.Except(was, StringComparer.Ordinal).ToList();

            static long Cost(string s) => 8 + 2 + (s.All(c => c < 128) ? s.Length : s.Length * 2L);
            var expected = wasBytes + came.Sum(Cost) - gone.Sum(Cost);

            detail = $"{came.Count} name(s) in, {gone.Count} out: expected {expected:N0} bytes, " +
                     $"found {nowBytes:N0}";
            return nowBytes == expected;
        }
        catch (Exception e) { detail = $"the names could not be compared -- {e.Message}"; return false; }
    }

    /// <summary>
    /// Parse a name batch at <paramref name="at"/>: the shape the asset registry uses, and the
    /// shape BOTH batches in a Zen package use.
    ///
    /// Reconciliation is the whole value. Summing the per-name length headers and comparing the
    /// total against the declared string byte count is what tells a real batch from arbitrary
    /// bytes -- which is how the main batch's offset is found, and how a rewritten
    /// imported-package-names batch is caught claiming a size it does not have.
    ///
    /// AN EMPTY BATCH IS FOUR BYTES, NOT SIXTEEN. UE's SaveNameBatch writes the count and returns
    /// immediately when it is zero, so there is no string-byte count and no hash version to check
    /// against. Measured: 200 of the first 500 pristine packages have exactly that -- a four-byte
    /// imported-package-names section holding a zero. The first version of this check called all
    /// two hundred of them corrupt, which is the sweep earning its place: every one of those rules
    /// was about to be enforced against people's builds.
    /// </summary>
    private static bool Batch(byte[] raw, int at, bool allowEmpty,
                              out int count, out int stringBytes, out int end)
    {
        count = 0; stringBytes = 0; end = 0;
        if (at < 0 || at + 4 > raw.Length) return false;
        if (allowEmpty && BitConverter.ToInt32(raw, at) == 0) { end = at + 4; return true; }
        if (at + 16 > raw.Length) return false;

        count = BitConverter.ToInt32(raw, at);
        stringBytes = BitConverter.ToInt32(raw, at + 4);
        var version = BitConverter.ToUInt64(raw, at + 8);
        if (count < 0 || count > 1_000_000 || stringBytes < 0) return false;
        if (version != NameHash.AlgorithmId) return false;

        var headers = at + 16 + count * 8;
        var strings = headers + count * 2;
        if (strings < 0 || strings + stringBytes > raw.Length) return false;

        var total = 0;
        for (var i = 0; i < count; i++)
        {
            var b0 = raw[headers + i * 2];
            var length = ((b0 & 0x7F) << 8) | raw[headers + i * 2 + 1];
            total += (b0 & 0x80) != 0 ? length * 2 : length;
        }
        if (total != stringBytes) return false;

        end = strings + stringBytes;
        return true;
    }

    /// <summary>
    /// The header's runs as one line, for a log that wants to say what it measured rather than
    /// only that it found nothing wrong. A check whose passing output is silence is
    /// indistinguishable from a check that did not run.
    /// </summary>
    public static string Describe(Shape shape)
    {
        var sb = new StringBuilder();
        sb.Append($"header {shape.HeaderSize:N0} of {shape.FileBytes:N0} bytes");
        foreach (var run in shape.Runs) sb.Append($", {run.Count} {run.Name}");
        return sb.ToString();
    }
}

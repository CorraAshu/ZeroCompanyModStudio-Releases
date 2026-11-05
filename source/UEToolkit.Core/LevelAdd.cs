using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// ADD AN ACTOR TO A COOKED .umap.
///
/// This is the gate the level editor has never got through. Moving an actor and repointing its
/// mesh are both confirmed in game; adding one has never been tried, and the asset list the author
/// asked for -- pick a mesh, put it in the scene -- stands entirely on it.
///
/// THE APPROACH IS DUPLICATION, NOT AUTHORING. A new actor is a byte-for-byte copy of one the
/// level already has, with three numbers changed. Nothing here writes an FProperty, invents a
/// class hash or composes a transform: the donor's own bytes are known to load, and the further
/// this stays from re-implementing UE's serialiser the fewer ways it has to be subtly wrong.
///
/// TWO THINGS FALL OUT OF THE MEASUREMENTS AND MAKE THIS FAR SMALLER THAN IT LOOKS:
///
///   * The name map never changes. An export's name is an FMappedName -- an index into the name
///     map AND a number -- and UE renders (entry, N) as "entry_N-1". The donor actor is
///     (BitReactorStaticMeshActor, 2) = BitReactorStaticMeshActor_1, so a new one is the SAME
///     index with a higher number. No new string, no name batch resize, and none of the offset
///     repair that would need. See ZenPackage.SplitNumberedNames for why the number matters: a
///     package that wrote the suffix into the string mounted, passed every check and could not be
///     found by name in game.
///
///   * Export offsets are relative to HeaderSize. Growing the header moves the whole export data
///     block, and every existing offset stays correct because they all move with it.
///
/// WHAT THIS DOES NOT DO, said plainly: the new export's dependency bundle declares NO
/// dependencies. Its component is created before it in the Create order, and the mesh it points at
/// is already imported by the donor, so nothing it needs should be missing -- but "should" is the
/// word, and if the game refuses the level this is the first thing to suspect.
/// </summary>
public static class LevelAdd
{
    // Summary field positions. The same ones ZenCheck measures against each other.
    private const int HeaderSizeAt = 4;
    private const int ImportMapAt = 28;
    private const int ExportMapAt = 32;
    private const int ExportBundleEntriesAt = 36;
    private const int DependencyBundleHeadersAt = 40;
    private const int DependencyBundleEntriesAt = 44;
    private const int ImportedPackageNamesAt = 48;

    /// <summary>
    /// The VERSE CELL offsets, present only when the summary is 60 bytes rather than 52.
    ///
    /// These are why the first grown package could not be opened. CUE4Parse positions the export
    /// bundle entries from CellImportMapOffset, not from ExportBundleEntriesOffset -- so leaving
    /// them behind pointed the reader 144 bytes short, it read 4,522 entries out of the end of the
    /// export map, and the first LocalExportIndex it decoded indexed past the export array.
    ///
    /// Every arithmetic check passed: ZenCheck does not know these fields exist, and the two it
    /// does know about were correct. ZenPackage has known about them all along -- they are in its
    /// MovableOffsets list, because a name batch that changes size moves them too.
    /// </summary>
    private const int CellImportMapAt = 52;
    private const int CellExportMapAt = 56;

    private const int ExportEntryBytes = 72;
    private const int BundleEntryBytes = 8;
    private const int DependencyHeaderBytes = 20;

    // Offsets inside an FExportMapEntry, confirmed against the shipped dropship level.
    private const int SerialOffsetAt = 0;
    private const int SerialSizeAt = 8;
    private const int NameIndexAt = 16;
    private const int NameNumberAt = 20;
    private const int OuterIndexAt = 24;

    /// <summary>What was added, for a caller that wants to say so rather than guess.</summary>
    public sealed record Added(int ActorExport, int ComponentExport, int ActorNumber,
                               int HeaderGrewBy, int FileGrewBy);

    /// <summary>
    /// Copy <paramref name="donorActor"/> and its component into a new actor at the end of the
    /// package, and list it in the level's Actors array.
    ///
    /// The donor indices come from the caller because working out which export is an actor needs
    /// class names, and the caller already has the package open through CUE4Parse. Keeping this
    /// side purely arithmetic is what makes it checkable.
    /// </summary>
    /// <summary>
    /// TAKE THE DONOR'S SLOT instead of appending one.
    ///
    /// A DISCRIMINATING EXPERIMENT, not a feature. The package is correct by every reader we have
    /// -- 2,261 exports, 880 actors, the editor composes the copy at exactly the right place -- and
    /// the game shows nothing. That leaves two possibilities and no way to tell them apart from
    /// outside: either the new EXPORT is never instantiated, or the grown ACTORS ARRAY is not what
    /// the runtime reads.
    ///
    /// Pointing the donor's existing slot at the copy separates them, because the array does not
    /// change length at all:
    ///   original gone, copy visible  -> the export loads; appending to Actors[] is what fails
    ///   original gone, nothing there -> the export is never instantiated
    ///   nothing changes              -> Actors[] is not what drives this
    /// </summary>
    public static bool TakeDonorSlot;

    public static byte[]? AddActor(byte[] raw, int donorActor, int donorComponent, int levelExport,
                                   int expectedActors,
                                   out string? why, out Added? added, Action<string>? log = null)
    {
        why = null;
        added = null;
        try
        {
            var headerSize = (int) BitConverter.ToUInt32(raw, HeaderSizeAt);
            var expMap = BitConverter.ToInt32(raw, ExportMapAt);
            var bundleAt = BitConverter.ToInt32(raw, ExportBundleEntriesAt);
            var depHeadAt = BitConverter.ToInt32(raw, DependencyBundleHeadersAt);
            var depEntryAt = BitConverter.ToInt32(raw, DependencyBundleEntriesAt);
            var impNamesAt = BitConverter.ToInt32(raw, ImportedPackageNamesAt);
            var importBytes = expMap - BitConverter.ToInt32(raw, ImportMapAt);

            var exportCount = (bundleAt - expMap) / ExportEntryBytes;
            if (exportCount <= 0) { why = "no export map"; return null; }
            if (donorActor < 0 || donorActor >= exportCount ||
                donorComponent < 0 || donorComponent >= exportCount ||
                levelExport < 0 || levelExport >= exportCount)
            { why = "a donor index is outside the export map"; return null; }

            // THE ENTRIES ARE GROUPED INTO BUNDLES, and it is not one block of Creates followed by
            // one of Serializes. LGT_Dropship_ROOT runs Create x1, Serialize x1, Create x915,
            // Serialize x915 -- export 0 is a bundle on its own and the other 915 are a second.
            // Blank has the same four-run shape at its own size.
            //
            // So a new export joins the LAST bundle: its Create goes at the end of the final Create
            // run and its Serialize at the very end. Assuming a single block put the Create 587
            // entries early on this level, inside another bundle, which is a load order the game
            // would have had every right to refuse.
            var bundleCount = (depHeadAt - bundleAt) / BundleEntryBytes;
            if (bundleCount != exportCount * 2)
            { why = $"{bundleCount} bundle entries for {exportCount} exports, expected two each"; return null; }

            var lastSerialRun = bundleCount;
            while (lastSerialRun > 0 &&
                   BitConverter.ToUInt32(raw, bundleAt + (lastSerialRun - 1) * BundleEntryBytes + 4) == 1)
                lastSerialRun--;
            if (lastSerialRun == 0 || lastSerialRun == bundleCount)
            { why = "the bundle entries do not end in a Serialize run"; return null; }

            var newActor = exportCount;
            var newComp = exportCount + 1;

            // ---------------------------------------------------------------- 1. the Actors array
            //
            // Done FIRST, on the untouched package, because SpliceExportData corrects every export
            // offset for the four bytes it inserts -- and it can only do that while the header it
            // is reading still matches the file.
            var levelOffset = (long) BitConverter.ToUInt64(raw, expMap + levelExport * ExportEntryBytes + SerialOffsetAt);
            var levelSize = (long) BitConverter.ToUInt64(raw, expMap + levelExport * ExportEntryBytes + SerialSizeAt);
            var levelStart = headerSize + (int) levelOffset;
            var levelData = raw[levelStart..(levelStart + (int) levelSize)];

            if (FindActorsArray(levelData, exportCount, importBytes / 8, expectedActors) is not { } actorsAt)
            {
                why = $"could not find an Actors array of {expectedActors} in the ULevel export " +
                      $"({levelData.Length} bytes, needs {4 + expectedActors * 4})";
                return null;
            }

            var count = BitConverter.ToInt32(levelData, actorsAt);

            byte[] grown;
            if (TakeDonorSlot)
            {
                // THE DONOR'S OWN SLOT, re-pointed at the copy. The array does not change length,
                // so this separates "the export is never instantiated" from "a longer Actors array
                // is not read" -- see TakeDonorSlot for what each outcome means.
                var slot = -1;
                for (var i = 0; i < count; i++)
                    if (BitConverter.ToInt32(levelData, actorsAt + 4 + i * 4) == donorActor + 1)
                    { slot = i; break; }
                if (slot < 0) { why = "the donor is not listed in the Actors array"; return null; }

                grown = (byte[]) levelData.Clone();
                BitConverter.GetBytes(newActor + 1).CopyTo(grown, actorsAt + 4 + slot * 4);
                log?.Invoke($"      Actors[{slot}] re-pointed from the donor to the copy, " +
                            $"array stays {count} long");
            }
            else
            {
                grown = new byte[levelData.Length + 4];
                Array.Copy(levelData, 0, grown, 0, actorsAt);
                BitConverter.GetBytes(count + 1).CopyTo(grown, actorsAt);
                // APPENDED, not inserted at the front. The array's order is the level's own and
                // the game walks it; putting a new actor last is the only position that cannot
                // displace something that already works.
                var listEnd = actorsAt + 4 + count * 4;
                Array.Copy(levelData, actorsAt + 4, grown, actorsAt + 4, count * 4);
                BitConverter.GetBytes(newActor + 1).CopyTo(grown, listEnd);
                Array.Copy(levelData, listEnd, grown, listEnd + 4, levelData.Length - listEnd);
            }

            var work = ZenPackage.SpliceExportData(raw, levelStart, levelData.Length, grown);
            if (!TakeDonorSlot) log?.Invoke($"      Actors[] {count} -> {count + 1}, ULevel export grew 4 bytes");

            // Re-read: the splice moved export data and the summary offsets are unchanged, but the
            // file is a different length and the level export's own size has changed.
            headerSize = (int) BitConverter.ToUInt32(work, HeaderSizeAt);

            // ---------------------------------------------------------------- 2. the new exports
            var donorActorEntry = expMap + donorActor * ExportEntryBytes;
            var donorCompEntry = expMap + donorComponent * ExportEntryBytes;

            var actorData = ExportData(work, headerSize, donorActorEntry);
            var compData = ExportData(work, headerSize, donorCompEntry);

            // The actor names its component twice -- StaticMeshComponent and RootComponent -- as an
            // FPackageIndex, which for an export is index + 1. Both must be re-pointed at the new
            // component, and finding exactly two is the check that this is the pair it claims to
            // be rather than some other actor that happens to be 72 bytes.
            var swapped = Repoint(actorData, donorComponent + 1, newComp + 1);
            if (swapped != 2)
            { why = $"the donor actor references its component {swapped} times, expected 2"; return null; }

            var dataStart = work.Length;
            var newData = new byte[actorData.Length + compData.Length];
            actorData.CopyTo(newData, 0);
            compData.CopyTo(newData, actorData.Length);

            // ---------------------------------------------------------------- 3. the header rows
            //
            // Inserted from the HIGHEST offset down, so each insertion point is still valid when it
            // is used. Going the other way would need every later offset recomputed after every
            // step, which is exactly the bookkeeping that goes wrong quietly.
            var actorEntry = NewEntry(work, donorActorEntry, dataStart - headerSize + 0,
                                      actorData.Length, NextNumber(work, expMap, exportCount, donorActorEntry),
                                      levelExport);
            var compEntry = NewEntry(work, donorCompEntry, dataStart - headerSize + actorData.Length,
                                     compData.Length, BitConverter.ToUInt32(work, donorCompEntry + NameNumberAt),
                                     newActor);

            // THE DEPENDENCY BUNDLE, COPIED FROM THE DONOR RATHER THAN LEFT EMPTY.
            //
            // The first attempt declared none, and the game proved that is not good enough: with
            // the donor's Actors[] slot re-pointed at the copy, the original VANISHED -- so the
            // runtime read the array and followed the index -- and nothing appeared in its place.
            // The array is honoured; the export is never instantiated. The one thing the writer
            // had guessed at was exactly this.
            //
            // A copy of an actor needs a copy of its dependencies: the same things must exist
            // before it, because it is the same object. Self-references are re-pointed, so the
            // copy waits for ITS component rather than for the donor's.
            var totalDepEntries = (impNamesAt - depEntryAt) / 4;
            var (actorDeps, actorCounts) = Dependencies(raw, depHeadAt, depEntryAt, donorActor);
            var (compDeps, compCounts) = Dependencies(raw, depHeadAt, depEntryAt, donorComponent);

            for (var i = 0; i + 4 <= actorDeps.Length; i += 4)
            {
                if (BitConverter.ToInt32(actorDeps, i) == donorComponent + 1)
                    BitConverter.GetBytes(newComp + 1).CopyTo(actorDeps, i);
                else if (BitConverter.ToInt32(actorDeps, i) == donorActor + 1)
                    BitConverter.GetBytes(newActor + 1).CopyTo(actorDeps, i);
            }
            for (var i = 0; i + 4 <= compDeps.Length; i += 4)
            {
                if (BitConverter.ToInt32(compDeps, i) == donorActor + 1)
                    BitConverter.GetBytes(newActor + 1).CopyTo(compDeps, i);
                else if (BitConverter.ToInt32(compDeps, i) == donorComponent + 1)
                    BitConverter.GetBytes(newComp + 1).CopyTo(compDeps, i);
            }

            var depHeader = new byte[DependencyHeaderBytes * 2];
            BitConverter.GetBytes(totalDepEntries).CopyTo(depHeader, 0);
            actorCounts.CopyTo(depHeader, 4);
            BitConverter.GetBytes(totalDepEntries + actorDeps.Length / 4).CopyTo(depHeader, DependencyHeaderBytes);
            compCounts.CopyTo(depHeader, DependencyHeaderBytes + 4);

            var newDepEntries = new byte[actorDeps.Length + compDeps.Length];
            actorDeps.CopyTo(newDepEntries, 0);
            compDeps.CopyTo(newDepEntries, actorDeps.Length);
            log?.Invoke($"      dependencies copied: actor {actorDeps.Length / 4}, " +
                        $"component {compDeps.Length / 4} entr(ies)");

            // Create entries go at the END of the Create block, Serialize at the end of the file's
            // Serialize block. The component is created before the actor that points at it.
            var creates = Bundle((newComp, 0u), (newActor, 0u));
            var serialises = Bundle((newActor, 1u), (newComp, 1u));

            // THE DEPENDENCY HEADERS GO IN FIRST, because they sit at the highest offset of the
            // three sections being grown and inserting from the top down is what keeps every
            // later insertion point valid. They are one per export in INDEX order, so the two new
            // ones belong at the END of that section -- putting them at its start would silently
            // renumber every export's dependencies by two.
            // The new dependency ENTRIES go at the end of their own section, which is the highest
            // offset of the four being grown -- so it is inserted first, same rule as the rest.
            var output = work;
            output = InsertAt(output, impNamesAt, newDepEntries);
            output = InsertAt(output, depHeadAt + exportCount * DependencyHeaderBytes, depHeader);
            output = InsertAt(output, depHeadAt, serialises);
            output = InsertAt(output, bundleAt + lastSerialRun * BundleEntryBytes, creates);
            output = InsertAt(output, bundleAt, [.. actorEntry, .. compEntry]);

            // ---------------------------------------------------------------- 4. the summary
            var headerGrew = actorEntry.Length + compEntry.Length + creates.Length +
                             serialises.Length + depHeader.Length + newDepEntries.Length;
            BitConverter.GetBytes((uint) (headerSize + headerGrew)).CopyTo(output, HeaderSizeAt);

            var mapGrew = actorEntry.Length + compEntry.Length;
            var bundleGrew = creates.Length + serialises.Length;
            Bump(output, ExportBundleEntriesAt, mapGrew);
            Bump(output, DependencyBundleHeadersAt, mapGrew + bundleGrew);
            Bump(output, DependencyBundleEntriesAt, mapGrew + bundleGrew + depHeader.Length);
            Bump(output, ImportedPackageNamesAt, mapGrew + bundleGrew + depHeader.Length + newDepEntries.Length);

            // The cell offsets sit beside the bundle entries and move with them. Whether they are
            // present at all is DERIVED -- ZenPackage reports where the name batch begins, and a
            // batch at 60 means the two extra fields are there -- rather than assumed from the
            // engine version, which is the kind of assumption that is right until a game patch.
            if (ZenPackage.Read(raw).NameBatchOffset >= CellExportMapAt + 4)
            {
                Bump(output, CellImportMapAt, mapGrew);
                Bump(output, CellExportMapAt, mapGrew);
                log?.Invoke("      cell offsets moved with the bundle entries");
            }

            // The new export data has to follow the header rows, not the position it was measured
            // at before they were inserted.
            var final = new byte[output.Length + newData.Length];
            output.CopyTo(final, 0);
            newData.CopyTo(final, output.Length);

            added = new Added(newActor, newComp,
                              (int) BitConverter.ToUInt32(actorEntry, NameNumberAt),
                              headerGrew, final.Length - raw.Length);
            log?.Invoke($"      export {newActor} (actor) and {newComp} (component) appended, " +
                        $"header +{headerGrew} bytes, file +{final.Length - raw.Length}");
            return final;
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// Add a copy of a NAMED actor, resolving the donor and the level out of the package itself.
    ///
    /// The byte-level AddActor takes indices because that keeps it arithmetic and checkable; a
    /// caller holding a mod project has a NAME. This is the bridge, and it is where the package is
    /// opened through CUE4Parse -- the one reader that knows which export is a level and which is
    /// an actor.
    /// </summary>
    /// <param name="alreadyAdded">
    /// HOW MANY ACTORS A PREVIOUS CALL HAS ALREADY APPENDED to these bytes.
    ///
    /// The actor count comes from the PROVIDER, which reads what the game shipped -- so on a
    /// second spawn into one package it still says 350 while `raw` now holds 351, and the search
    /// for the Actors array refuses because it cannot find an array of 350 in a level that has
    /// 351. Correctly refuses: a loose search once grew an array of ONE in a 350-actor level and
    /// every downstream check passed, which is why it takes the real count from its caller and
    /// will not guess.
    ///
    /// So the caller, which is the only thing that knows how many it has put in already, says.
    /// This was unreachable while one spawn per package was the only thing anyone had built; the
    /// asset list makes several the ordinary case.
    /// </param>
    public static byte[]? AddCopy(IFileProvider provider, byte[] raw, string packageName,
                                  string donorActor, out int newActorIndex,
                                  out string? why, Action<string>? log = null,
                                  int alreadyAdded = 0)
    {
        newActorIndex = -1;
        why = null;
        try
        {
            var exports = AssetLoader.Read(() => provider.LoadPackage(packageName)?.GetExports().ToArray())
                          ?? [];
            if (exports.Length == 0) { why = $"{packageName} has no exports"; return null; }

            // "#427" ADDRESSES AN EXPORT DIRECTLY, the same form LevelEdit takes, and for the same
            // reason: this hub level has THREE exports called BitReactorStaticMeshActor_5, so a
            // name simply cannot reach a large share of its actors.
            int donor;
            if (donorActor.StartsWith('#') && int.TryParse(donorActor[1..], out var direct))
            {
                if (direct < 0 || direct >= exports.Length)
                { why = $"export #{direct} is past the end of {packageName} ({exports.Length})"; return null; }
                donor = direct;
            }
            else
            {
                var hits = exports.Select((e, i) => (e, i))
                                  .Where(x => string.Equals(x.e?.Name, donorActor, StringComparison.OrdinalIgnoreCase))
                                  .Select(x => x.i).ToList();
                if (hits.Count == 0) { why = $"no actor called '{donorActor}' in {packageName}"; return null; }
                if (hits.Count > 1)
                { why = $"'{donorActor}' names {hits.Count} exports here -- address it as #N"; return null; }
                donor = hits[0];
            }

            // BY THE EXPORT MAP'S OWN OuterIndex, not by the reader's Outer object and not by name.
            //
            // Names repeat in this level -- three exports called BitReactorStaticMeshActor_5 --
            // so a string match can pick a component belonging to a different actor, and the copy
            // would draw someone else's mesh. Reference identity looked like the rigorous answer
            // and is not: CUE4Parse resolves Outer to its own instance, so comparing it against
            // the export array found nothing at all and #427 was reported as having no component.
            //
            // The bytes are unambiguous. An export's OuterIndex is its outer's export index, which
            // is exactly the fact the anatomy dump confirmed against the dropship.
            var expMapAt = BitConverter.ToInt32(raw, ExportMapAt);
            var comp = -1;
            for (var i = 0; i < exports.Length; i++)
            {
                if (exports[i]?.ExportType?.EndsWith("Component", StringComparison.Ordinal) != true) continue;
                var outer = BitConverter.ToUInt64(raw, expMapAt + i * ExportEntryBytes + OuterIndexAt);
                if (outer != (ulong) donor) continue;
                comp = i;
                break;
            }
            if (comp < 0)
            { why = $"'{donorActor}' (#{donor}) has no component whose outer is that export"; return null; }

            var levelAt = Array.FindIndex(exports, e => e is CUE4Parse.UE4.Objects.Engine.ULevel);
            if (levelAt < 0) { why = $"{packageName} has no ULevel export"; return null; }
            var actors = ((exports[levelAt] as CUE4Parse.UE4.Objects.Engine.ULevel)?.Actors?.Length ?? 0)
                       + alreadyAdded;
            if (actors == 0) { why = "the level lists no actors to append to"; return null; }

            log?.Invoke($"      copying '{donorActor}' (#{donor}) and its '{exports[comp]!.Name}' (#{comp}), " +
                        $"{exports.Length} exports, {actors} actors");

            var grown = AddActor(raw, donor, comp, levelAt, actors, out why, out var added, log);
            if (grown is null) return null;
            newActorIndex = added!.ActorExport;
            return grown;
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// One export's dependency entries and its 2x2 count matrix, copied out.
    ///
    /// The header is FirstEntryIndex followed by four counts; the entries are FPackageIndex, four
    /// bytes each, starting at that index into the dependency entries section. Summing the four
    /// counts gives how many belong to this export.
    /// </summary>
    private static (byte[] Entries, byte[] Counts) Dependencies(byte[] raw, int depHeadAt,
                                                                int depEntryAt, int export)
    {
        var at = depHeadAt + export * DependencyHeaderBytes;
        var first = BitConverter.ToInt32(raw, at);
        var counts = raw[(at + 4)..(at + DependencyHeaderBytes)];

        var total = 0;
        for (var i = 0; i < 4; i++) total += (int) BitConverter.ToUInt32(counts, i * 4);

        var from = depEntryAt + first * 4;
        if (total <= 0 || from < depEntryAt || from + total * 4 > raw.Length)
            return ([], counts);
        return (raw[from..(from + total * 4)], counts);
    }

    /// <summary>One export's serialised bytes, copied out.</summary>
    private static byte[] ExportData(byte[] raw, int headerSize, int entry)
    {
        var at = headerSize + (int) (long) BitConverter.ToUInt64(raw, entry + SerialOffsetAt);
        var size = (int) (long) BitConverter.ToUInt64(raw, entry + SerialSizeAt);
        return raw[at..(at + size)];
    }

    /// <summary>A new export map entry: the donor's, with the four fields that must differ.</summary>
    private static byte[] NewEntry(byte[] raw, int donorEntry, long serialOffset, int serialSize,
                                   uint nameNumber, int outerExport)
    {
        var entry = raw[donorEntry..(donorEntry + ExportEntryBytes)];
        BitConverter.GetBytes((ulong) serialOffset).CopyTo(entry, SerialOffsetAt);
        BitConverter.GetBytes((ulong) serialSize).CopyTo(entry, SerialSizeAt);
        BitConverter.GetBytes(nameNumber).CopyTo(entry, NameNumberAt);
        // An export's outer is its plain export index with the kind bits clear -- confirmed by the
        // dropship's component #434 carrying 0x...07 for its actor at export 7.
        BitConverter.GetBytes((ulong) outerExport).CopyTo(entry, OuterIndexAt);
        return entry;
    }

    /// <summary>
    /// A free FName number for the donor's name entry.
    ///
    /// UE renders (entry, N) as "entry_N-1", so every export sharing this name entry has already
    /// claimed a number and the new one must not collide: two exports with the same name under the
    /// same outer is a level with two actors the game cannot tell apart.
    /// </summary>
    private static uint NextNumber(byte[] raw, int expMap, int exportCount, int donorEntry)
    {
        var nameIndex = BitConverter.ToUInt32(raw, donorEntry + NameIndexAt);
        var highest = 0u;
        for (var i = 0; i < exportCount; i++)
        {
            var at = expMap + i * ExportEntryBytes;
            if (BitConverter.ToUInt32(raw, at + NameIndexAt) != nameIndex) continue;
            highest = Math.Max(highest, BitConverter.ToUInt32(raw, at + NameNumberAt));
        }
        return highest + 1;
    }

    /// <summary>Re-point every FPackageIndex equal to <paramref name="from"/>. Returns how many.</summary>
    private static int Repoint(byte[] data, int from, int to)
    {
        var n = 0;
        for (var i = 0; i + 4 <= data.Length; i++)
            if (BitConverter.ToInt32(data, i) == from)
            {
                BitConverter.GetBytes(to).CopyTo(data, i);
                n++;
                i += 3;
            }
        return n;
    }

    private static byte[] Bundle(params (int Export, uint Command)[] entries)
    {
        var bytes = new byte[entries.Length * BundleEntryBytes];
        for (var i = 0; i < entries.Length; i++)
        {
            BitConverter.GetBytes((uint) entries[i].Export).CopyTo(bytes, i * BundleEntryBytes);
            BitConverter.GetBytes(entries[i].Command).CopyTo(bytes, i * BundleEntryBytes + 4);
        }
        return bytes;
    }

    private static byte[] InsertAt(byte[] raw, int at, byte[] what)
    {
        if (what.Length == 0) return raw;
        var output = new byte[raw.Length + what.Length];
        Array.Copy(raw, 0, output, 0, at);
        what.CopyTo(output, at);
        Array.Copy(raw, at, output, at + what.Length, raw.Length - at);
        return output;
    }

    private static void Bump(byte[] raw, int field, int by) =>
        BitConverter.GetBytes((uint) (BitConverter.ToUInt32(raw, field) + by)).CopyTo(raw, field);

    /// <summary>
    /// Where the Actors array starts inside the ULevel export's data.
    ///
    /// FOUND, NOT PARSED. The array is written straight after the tagged property block by
    /// ULevel::Serialize, and walking those properties to find where they end means implementing
    /// enough of UE's property serialiser to be wrong in a new way.
    ///
    /// THE COUNT IS PASSED IN, and that is not a convenience. The first version of this looked for
    /// "an int32 N followed by N plausible indices" and on LGT_Dropship_ROOT it matched a stray 1
    /// in the property block: it grew an array of ONE, reported "Actors[] 1 -> 2" for a level with
    /// THREE HUNDRED AND FIFTY actors, and every check downstream passed -- the header agreed with
    /// itself, every export had exactly one Create and one Serialize, nothing overran the file.
    /// A search that can match almost anywhere finds almost anything. The caller has already read
    /// the real count through CUE4Parse, so the search is for THAT number and the match must be
    /// unique.
    /// </summary>
    private static int? FindActorsArray(byte[] data, int exportCount, int importCount, int expected)
    {
        // BYTE BY BYTE, not in four-byte steps. A tagged property block ends wherever it ends --
        // names, sizes and values are not padded to four -- so the array that follows it has no
        // reason to be aligned. Stepping by four searched a quarter of the positions and found
        // nothing in a level that plainly has 350 actors.
        int? found = null;
        for (var at = 0; at + 4 <= data.Length; at++)
        {
            var n = BitConverter.ToInt32(data, at);
            if (n != expected || n <= 0) continue;
            if (at + 4 + n * 4 > data.Length) continue;

            var plausible = true;
            for (var i = 0; i < n && plausible; i++)
            {
                var idx = BitConverter.ToInt32(data, at + 4 + i * 4);
                // FPackageIndex: positive is export+1, negative is -(import+1), zero is null.
                plausible = idx <= exportCount && idx >= -importCount;
            }
            // AMBIGUITY IS A REFUSAL, not a coin toss. Two candidates means the search cannot tell
            // which one the level uses, and picking either would corrupt a package that then
            // passes every structural check.
            if (!plausible) continue;
            if (found is not null) return null;
            found = at;
        }
        return found;
    }
}

using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Objects.Unversioned;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;

namespace UEToolkit.Core;

/// <summary>
/// Find a named field of a named row inside a cooked DataTable, by walking the table the way the
/// engine reads it.
///
/// This is what <see cref="NumberEdit"/> is not. That one finds a number by searching the whole
/// asset for its current value and refuses when the value is not unique, which is honest and
/// covers about half the numbers in a weapon table: Damage 17 is unique, ReactionDamage 1 sits
/// beside AssistDamage 1, ReactionChanceToHit 1 and every other 1.0 in the file. A balance editor
/// that can change half the numbers is not a balance editor.
///
/// So this walks instead of searching. The layout, taken from UDataTable.Deserialize and
/// UObject.Deserialize rather than guessed at:
///
///     the table's own unversioned property block
///     a bool, and 16 bytes of guid if it is set        (skipped for a class default object)
///     int32 numRows
///     per row:  FName rowName, then that row's own unversioned property block
///
/// A row's block is read exactly like an export's -- FStructFallback delegates to the same reader
/// -- which is why the inner loop here is the one <see cref="PropertyAdd.Locate"/> already uses:
/// an FUnversionedHeader, an iterator over the present properties, and the schema to say what each
/// one is. Position before the read is the field's offset; position after gives its length.
///
/// It reports rather than assumes. Every failure names what it was looking for and what it found,
/// because the alternative -- a silent wrong offset -- writes four bytes into the middle of some
/// other value and produces an asset that still parses.
/// </summary>
public static class DataTableEdit
{
    /// <summary>
    /// One field of one row, where its bytes are, and WHAT IT IS.
    ///
    /// The type is here because width cannot say. A whole-game sweep over 860 tables found two
    /// ways this went wrong with a float writer and no type:
    ///
    ///   * a DoubleProperty is eight bytes, so the write was refused -- honest, but it meant a
    ///     panel offering an editable box for a number nothing could change;
    ///   * an IntProperty is four bytes, so the write was ACCEPTED and put float 2.0f over int 2,
    ///     which reads back as 3E-45. That is the same trap as writing a float into the top half
    ///     of a double (see ScalarEdit), in a place nobody had looked.
    ///
    /// <paramref name="Type"/> is the mappings' own name for it: FloatProperty, DoubleProperty,
    /// IntProperty, BoolProperty, StructProperty, and so on.
    /// </summary>
    public sealed record Field(string Row, string Name, int Start, int Length, string Type)
    {
        /// <summary>Whether <see cref="SetNumber"/> can write this one.</summary>
        public bool Writable => Kind(Type) != Number.None;
    }

    /// <summary>How a table field is stored. Not a width: four bytes is a float OR an int.</summary>
    public enum Number { None, Float, Double, Int }

    /// <summary>What the mappings' type name means for writing.</summary>
    public static Number Kind(string type) => type switch
    {
        "FloatProperty" => Number.Float,
        "DoubleProperty" => Number.Double,
        "IntProperty" => Number.Int,
        _ => Number.None,
    };

    /// <summary>
    /// Every field of every row, in serialised order, with byte offsets.
    ///
    /// The whole walk exposed rather than only the lookup, because it is how the lookup is
    /// CHECKED: an offset table that agrees with what CUE4Parse read out of the same asset is an
    /// offset table that is walking the file correctly, and one number in isolation cannot say
    /// that about itself.
    /// </summary>
    public static List<Field> Walk(IFileProvider provider, byte[] raw, string packageName,
                                   out string? why) =>
        Walk(provider, raw, packageName, out why, out _);

    /// <summary>
    /// The same walk, and it also says whether the package IS A DATATABLE.
    ///
    /// <paramref name="isTable"/> is true from the moment the package's DataTable export is
    /// found, whatever happens after: a refusal "not in this table" and a refusal "no schema for
    /// the row struct" are both about a table. It stays false for the two answers that say the
    /// target is something else -- no mounted container holds it, or it holds no DataTable export
    /// -- and for a package that would not even open, where nothing is known.
    ///
    /// A CALLER NEEDS THIS because a table and a not-table want opposite things of a refusal. A
    /// number aimed at something that is not a table may be found some other way (by the export's
    /// name, by its value). A number aimed at a table row that the walk cannot place must NOT be:
    /// a search by value finds whatever cell happens to hold the same four bytes, and writes there.
    /// </summary>
    public static List<Field> Walk(IFileProvider provider, byte[] raw, string packageName,
                                   out string? why, out bool isTable)
    {
        why = null;
        isTable = false;
        var found = new List<Field>();
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null)
            { why = $"no mounted container holds {packageName}"; return found; }

            var pkg = PropertyAdd.Open(provider, raw, packageName, container);
            var exports = pkg.ExportsLazy.Select(x => x.Value).ToArray();

            var index = Array.FindIndex(exports, e => e is UDataTable);
            if (index < 0) { why = $"{packageName} holds no DataTable export"; return found; }
            isTable = true;
            if (exports[index] is not UDataTable table)
            { why = "the DataTable export did not deserialise"; return found; }

            if (exports[index].Class?.Object?.Value is not UStruct tableClass)
            { why = "the DataTable export's class is not a loaded struct"; return found; }
            var tableSchema = PropertyAdd.SchemaFor(pkg, tableClass);
            if (tableSchema is null) { why = "no property schema for DataTable"; return found; }

            var rowSchema = RowSchema(pkg, table);
            if (rowSchema is null)
            { why = $"no property schema for row struct '{table.RowStructName ?? "?"}'"; return found; }

            var headerSize = (int) BitConverter.ToUInt32(raw, 4);
            var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg)
            {
                Position = headerSize + (int) pkg.ExportMap[index].CookedSerialOffset,
            };

            // The table's own properties, read only to get past them.
            if (!SkipBlock(ar, tableSchema, out why)) return found;

            // UObject.Deserialize writes a bool after the properties, and a guid when it is set.
            // Not for a class default object -- and a DataTable is not one, but the check is what
            // the engine does, so it is what this does.
            if (!exports[index].Flags.HasFlag(EObjectFlags.RF_ClassDefaultObject))
                if (ar.ReadBoolean()) ar.Position += 16;

            var numRows = ar.Read<int>();
            // A sanity bound before trusting a length read out of a byte array: a wrong position
            // here reads a wrong count and then loops on garbage until something throws far away
            // from the actual mistake.
            if (numRows < 0 || numRows > 100000)
            { why = $"read {numRows:N0} rows, which cannot be right -- the walk is off"; return found; }

            for (var r = 0; r < numRows; r++)
            {
                var rowName = ar.ReadFName().Text;
                var header = new FUnversionedHeader(ar);
                if (!header.HasValues) continue;

                using var it = new FIterator(header);
                do
                {
                    var (val, isNonZero) = it.Current;
                    // A zero-valued property occupies no bytes: it can be named but not written
                    // over, so it is recorded with a zero length rather than skipped silently.
                    if (!rowSchema.TryGetValue(val, out var info))
                    { why = $"row '{rowName}': property {val} is not in the mappings"; return found; }

                    if (!isNonZero)
                    { found.Add(new Field(rowName, info.Name, -1, 0, info.MappingType.Type)); continue; }

                    var at = (int) ar.Position;
                    _ = new FPropertyTag(ar, info, ReadType.NORMAL);
                    found.Add(new Field(rowName, info.Name, at, (int) ar.Position - at,
                                        info.MappingType.Type));
                } while (it.MoveNext());
            }

            return found;
        }
        catch (Exception e) { why = e.Message; return found; }
    }

    /// <summary>
    /// WHERE THE ROWS ARE, as byte offsets into the package.
    ///
    /// Everything <see cref="AddRow"/> needs, and the one fact nothing else here has ever asked
    /// for: whether anything FOLLOWS the last row inside the export. <see cref="Walk"/> reads
    /// numRows rows and stops, so a trailer would have gone unnoticed -- and appending a row at
    /// the end of the row block would then land on top of it.
    /// </summary>
    /// <param name="CountAt">Absolute offset of the int32 row count.</param>
    /// <param name="RowsEnd">Absolute offset just past the last row.</param>
    /// <param name="ExportEnd">Absolute offset just past the export's serialised data.</param>
    public sealed record Shape(int Index, int CountAt, int RowsEnd, int ExportEnd, int NumRows,
                               IReadOnlyList<Row> Rows)
    {
        /// <summary>Bytes between the last row and the end of the export. Zero is the happy case.</summary>
        public int Trailing => ExportEnd - RowsEnd;

        public Row? Find(string name) =>
            Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One row and the bytes it occupies.
    ///
    /// <paramref name="Start"/> is at its FNAME, not at its property header, because that is what a
    /// duplicate needs: everything from the name onwards is the row, and only the first eight
    /// bytes of it change.
    /// </summary>
    public sealed record Row(string Name, int Start, int End);

    /// <summary>
    /// Measure a cooked DataTable: its row count, its row names, and where the row block ends.
    ///
    /// The same walk <see cref="Walk"/> makes, kept separate rather than folded into it because
    /// they answer different questions -- that one is a map of the FIELDS for editing a value,
    /// this one is the shape of the CONTAINER for adding a row.
    /// </summary>
    public static Shape? Measure(IFileProvider provider, byte[] raw, string packageName,
                                 out string? why)
    {
        why = null;
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null) { why = $"no mounted container holds {packageName}"; return null; }

            var pkg = PropertyAdd.Open(provider, raw, packageName, container);
            var exports = pkg.ExportsLazy.Select(x => x.Value).ToArray();
            var index = Array.FindIndex(exports, e => e is UDataTable);
            if (index < 0) { why = $"{packageName} holds no DataTable export"; return null; }
            if (exports[index] is not UDataTable table)
            { why = "the DataTable did not deserialise"; return null; }
            if (exports[index].Class?.Object?.Value is not UStruct tableClass)
            { why = "the DataTable export's class is not a loaded struct"; return null; }

            var tableSchema = PropertyAdd.SchemaFor(pkg, tableClass);
            var rowSchema = RowSchema(pkg, table);
            if (tableSchema is null || rowSchema is null)
            { why = "no property schema for the table or its rows"; return null; }

            var headerSize = (int) BitConverter.ToUInt32(raw, 4);
            var exportStart = headerSize + (int) pkg.ExportMap[index].CookedSerialOffset;
            var exportEnd = exportStart + (int) pkg.ExportMap[index].CookedSerialSize;

            var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg)
            { Position = exportStart };
            if (!SkipBlock(ar, tableSchema, out why)) return null;
            if (!exports[index].Flags.HasFlag(EObjectFlags.RF_ClassDefaultObject))
                if (ar.ReadBoolean()) ar.Position += 16;

            var countAt = (int) ar.Position;
            var numRows = ar.Read<int>();
            if (numRows < 0 || numRows > 100000)
            { why = $"read {numRows:N0} rows, which cannot be right -- the walk is off"; return null; }

            var names = new List<Row>(numRows);
            for (var r = 0; r < numRows; r++)
            {
                var rowAt = (int) ar.Position;
                var rowName = ar.ReadFName().Text;
                var header = new FUnversionedHeader(ar);
                if (header.HasValues)
                {
                    using var it = new FIterator(header);
                    do
                    {
                        var (val, isNonZero) = it.Current;
                        if (!rowSchema.TryGetValue(val, out var info))
                        { why = $"row '{rowName}': property {val} is not in the mappings"; return null; }
                        if (isNonZero) _ = new FPropertyTag(ar, info, ReadType.NORMAL);
                    } while (it.MoveNext());
                }
                names.Add(new Row(rowName, rowAt, (int) ar.Position));
            }

            return new Shape(index, countAt, (int) ar.Position, exportEnd, numRows, names);
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    /// <summary>
    /// ADD A ROW to a cooked DataTable, by duplicating one that is already there.
    ///
    /// This is the wall three separate features have taken the long way around -- a shop slot, a
    /// mesh palette entry, a roster row -- each of them TAKING an existing entry because a new one
    /// could not be made. A released mod grew this game's recruitment table from 43 rows to 247,
    /// so it was never impossible; it was merely not built.
    ///
    /// DUPLICATED RATHER THAN CONSTRUCTED, deliberately. Building a row from nothing means
    /// emitting an unversioned header and values for an arbitrary struct, and getting the schema
    /// order wrong there produces a row that parses and means something else. Copying a row the
    /// game itself wrote starts from bytes that are known good, and the only edits are the ones
    /// asked for.
    ///
    /// THE INSERT POINT IS ONE BYTE BACK, and that is not a fiddle. `SpliceExportData` decides
    /// which export grew by asking which one CONTAINS the edit -- `relative >= offset &amp;&amp;
    /// relative &lt; offset + size` -- and the end of the row block is the end of the export, so an
    /// insert exactly there is contained by nothing and the export's size never grows. Rewriting
    /// the final byte as itself-plus-the-new-row puts the edit inside the export, where the fixup
    /// can see it.
    ///
    /// Measured before this was written: all three of this game's pool tables have ZERO bytes
    /// between the last row and the end of the export (`--dtprobe`). A table that does carry a
    /// trailer is refused rather than guessed at.
    /// </summary>
    /// <param name="softRefs">
    /// Fields of the new row to re-point, as field name -> full object path. A soft object path is
    /// an FName pair -- the package and the asset -- followed by a subpath string, so this
    /// overwrites sixteen bytes and leaves the subpath as the template wrote it.
    /// </param>
    public static byte[] AddRow(IFileProvider provider, byte[] raw, string packageName,
                                string newRow, string templateRow,
                                IReadOnlyDictionary<string, string> softRefs,
                                out string? why, Action<string>? log = null)
    {
        why = null;
        try
        {
            var shape = Measure(provider, raw, packageName, out why);
            if (shape is null) return raw;
            if (shape.Trailing != 0)
            {
                why = $"{shape.Trailing} byte(s) follow the last row, so a new one cannot go on " +
                      "the end without landing on them";
                return raw;
            }
            if (shape.Find(newRow) is not null)
            { why = $"'{newRow}' is already a row of this table"; return raw; }
            if (shape.Find(templateRow) is not { } template)
            { why = $"'{templateRow}' is not a row of this table, so there is nothing to copy"; return raw; }

            // Every name the new row needs, appended first. Appending shifts every offset after
            // the name batch, so the walk has to be redone afterwards -- which is what the
            // recursion does, exactly as PropertyAdd.AddMeshOption does it.
            var pkg = ZenPackage.Read(raw);
            var needed = new List<string> { newRow };
            foreach (var path in softRefs.Values)
            {
                var (package, asset) = SplitPath(path);
                needed.Add(package);
                needed.Add(asset);
            }
            var missing = needed.Where(n => n.Length > 0 && pkg.Names.IndexOf(n) < 0)
                                .Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                foreach (var n in missing) pkg.AppendName(n);
                return AddRow(provider, pkg.Write(), packageName, newRow, templateRow, softRefs,
                              out why, log);
            }

            // The count, bumped in place -- same width, and it sits before the rows so writing it
            // now cannot disturb the offsets the splice below is about to use.
            var bumped = (byte[]) raw.Clone();
            BitConverter.GetBytes(shape.NumRows + 1).CopyTo(bumped, shape.CountAt);

            // The new row: our name, then the template's header and values byte for byte.
            var body = raw.AsSpan(template.Start + 8, template.End - template.Start - 8).ToArray();
            var row = new byte[8 + body.Length];
            BitConverter.GetBytes((uint) pkg.Names.IndexOf(newRow)).CopyTo(row, 0);
            body.CopyTo(row, 8);

            var lastByte = bumped[shape.RowsEnd - 1];
            var replacement = new byte[1 + row.Length];
            replacement[0] = lastByte;
            row.CopyTo(replacement, 1);
            var written = ZenPackage.SpliceExportData(bumped, shape.RowsEnd - 1, 1, replacement);
            log?.Invoke($"row '{newRow}' copied from '{templateRow}': {row.Length} byte(s), " +
                        $"{shape.NumRows} -> {shape.NumRows + 1} rows");

            // The re-points, found by walking the WRITTEN package: the new row's field offsets do
            // not exist until it does, and computing them from the template's would assume the
            // splice landed exactly where it was asked to.
            if (softRefs.Count > 0)
            {
                var fields = Walk(provider, written, packageName, out var walkWhy);
                if (walkWhy is not null)
                { why = $"the table no longer walks after the new row -- {walkWhy}"; return raw; }

                foreach (var (field, path) in softRefs)
                {
                    var found = fields.FirstOrDefault(
                        f => string.Equals(f.Row, newRow, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(f.Name, field, StringComparison.Ordinal));
                    if (found is null)
                    { why = $"the new row has no '{field}' to re-point"; return raw; }
                    if (found.Length < 16)
                    {
                        why = $"'{field}' is {found.Length} bytes, too short to be a soft object " +
                              "path (a name pair is sixteen)";
                        return raw;
                    }

                    var (package, asset) = SplitPath(path);
                    var pi = pkg.Names.IndexOf(package);
                    var ai = pkg.Names.IndexOf(asset);
                    if (pi < 0 || ai < 0)
                    { why = $"'{path}' is not in the name map after appending it"; return raw; }

                    BitConverter.GetBytes((uint) pi).CopyTo(written, found.Start);
                    BitConverter.GetBytes(0u).CopyTo(written, found.Start + 4);
                    BitConverter.GetBytes((uint) ai).CopyTo(written, found.Start + 8);
                    BitConverter.GetBytes(0u).CopyTo(written, found.Start + 12);
                    log?.Invoke($"    {field} -> {path}");
                }
            }

            // READ BACK, OR REFUSE. The row has to come out of CUE4Parse's own reader with the
            // name asked for and every re-point in place -- the same standard AddMeshOption holds
            // itself to, and the only thing that separates "the bytes were written" from "the
            // table gained a row".
            var check = PristineTable(provider, written, packageName);
            if (check is null) { why = "the edited table no longer reads as a DataTable"; return raw; }
            var got = check.RowMap.FirstOrDefault(
                kv => string.Equals(kv.Key.Text, newRow, StringComparison.OrdinalIgnoreCase)).Value;
            if (got is null)
            { why = $"'{newRow}' does not read back -- the table still has {check.RowMap.Count} row(s)"; return raw; }

            foreach (var (field, path) in softRefs)
            {
                var reads = PartDetails.SoftPathOf(PartDetails.Value(got.Properties, field)) ?? "";
                if (!string.Equals(reads, path, StringComparison.OrdinalIgnoreCase))
                {
                    why = $"'{newRow}.{field}' reads back as '{reads}', not '{path}'";
                    return raw;
                }
            }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>"/A/B/C.C" -> ("/A/B/C", "C"). A soft object path is stored as those two names.</summary>
    private static (string Package, string Asset) SplitPath(string path)
    {
        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('/');
        return dot > slash
            ? (path[..dot], path[(dot + 1)..])
            : (path, slash >= 0 ? path[(slash + 1)..] : path);
    }

    /// <summary>The DataTable export of an edited package, read through the pristine container.</summary>
    private static UDataTable? PristineTable(IFileProvider provider, byte[] raw, string packageName)
    {
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null) return null;
            return PropertyAdd.Open(provider, raw, packageName, container)
                              .ExportsLazy.Select(x => x.Value).OfType<UDataTable>().FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Rewrite one float field of one row. The shape every existing caller uses; the work is
    /// <see cref="SetNumber"/>, which knows about the other two widths as well.
    /// </summary>
    public static byte[] SetFloat(IFileProvider provider, byte[] raw, string packageName,
                                  string row, string field, float was, float now,
                                  out int at, out string? why) =>
        SetNumber(provider, raw, packageName, row, field, was, now, out at, out why);

    /// <summary>
    /// Rewrite one number of one row, AS THE TYPE THE MAPPINGS SAY IT IS.
    ///
    /// This used to be float-only, and a sweep over all 860 of the game's tables found both ways
    /// that was wrong. A DoubleProperty was refused for being eight bytes -- honest, but it meant
    /// no editor could ever offer it. An IntProperty was ACCEPTED, because an int is four bytes
    /// too, and writing float 5.0f over int 5 leaves a value that reads back as 7E-45. Nothing
    /// complained: the width matched, the write landed, and the number was destroyed. It is the
    /// same trap ScalarEdit was written for, in the one editor that had not met it yet.
    ///
    /// Checked before and after, under the type: the bytes must currently read
    /// <paramref name="was"/>, and after writing they must read <paramref name="now"/>. The first
    /// check is what turns a walk that has silently drifted into a refusal rather than a corrupted
    /// asset -- an offset landing in the middle of some other value almost never happens to hold
    /// the value expected there.
    /// </summary>
    public static byte[] SetNumber(IFileProvider provider, byte[] raw, string packageName,
                                   string row, string field, double was, double now,
                                   out int at, out string? why) =>
        SetNumber(provider, raw, packageName, row, field, was, now, out at, out why, out _);

    /// <summary>
    /// The same write, which also says whether the target is a DataTable (the walk's
    /// <c>isTable</c>).
    ///
    /// WHEN <paramref name="isTable"/> IS TRUE AND <paramref name="why"/> IS SET, THE CELL IS NOT
    /// THERE TO BE FOUND and the right answer is to refuse. The caller that goes on to search for
    /// the old value by its bytes is the caller that writes into a different row after a game
    /// patch has renamed the one that was asked for.
    /// </summary>
    public static byte[] SetNumber(IFileProvider provider, byte[] raw, string packageName,
                                   string row, string field, double was, double now,
                                   out int at, out string? why, out bool isTable)
    {
        at = -1;
        var fields = Walk(provider, raw, packageName, out why, out isTable);
        if (why is not null) return raw;

        var hit = fields.FirstOrDefault(f =>
            string.Equals(f.Row, row, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.Name, field, StringComparison.OrdinalIgnoreCase));

        // No bytes, or no mention at all. Both mean the value is currently zero -- unversioned
        // serialisation stores neither -- so this is a raise rather than a write, and RaiseZero
        // reports precisely which of the two it found.
        if (hit is null || hit.Length == 0)
        {
            if (Math.Abs(was) > 0.0001)
            {
                var rows = fields.Select(f => f.Row).Distinct().Count();
                why = hit is null
                    ? $"'{row}.{field}' is not in this table ({rows} row(s) walked)"
                    : $"'{row}.{field}' is zero, not the {was} this edit expects";
                return raw;
            }
            return RaiseZero(provider, raw, packageName, row, field, now, out at, out why);
        }

        var kind = Kind(hit.Type);
        if (kind == Number.None)
        {
            why = $"'{row}.{field}' is a {hit.Type}, which is not a number this can write";
            return raw;
        }

        var width = kind == Number.Double ? 8 : 4;
        if (hit.Length != width)
        {
            why = $"'{row}.{field}' is a {hit.Type} and occupies {hit.Length} bytes, not {width}";
            return raw;
        }

        var reads = ReadAs(raw, hit.Start, kind);
        if (!Agrees(reads, was))
        {
            why = $"'{row}.{field}' reads {reads}, not the {was} this edit expects";
            return raw;
        }

        var copy = (byte[]) raw.Clone();
        Bytes(now, kind).CopyTo(copy, hit.Start);

        var back = ReadAs(copy, hit.Start, kind);
        if (!Agrees(back, now))
        { why = $"'{row}.{field}' still reads {back} after writing {now}"; return raw; }

        at = hit.Start;
        return copy;
    }

    /// <summary>One number read under the type the mappings gave it.</summary>
    private static double ReadAs(byte[] raw, int at, Number kind) => kind switch
    {
        Number.Double => BitConverter.ToDouble(raw, at),
        Number.Int => BitConverter.ToInt32(raw, at),
        _ => BitConverter.ToSingle(raw, at),
    };

    /// <summary>One number written as the type the mappings gave it.</summary>
    private static byte[] Bytes(double value, Number kind) => kind switch
    {
        Number.Double => BitConverter.GetBytes(value),
        Number.Int => BitConverter.GetBytes((int) Math.Round(value)),
        _ => BitConverter.GetBytes((float) value),
    };

    /// <summary>
    /// A tolerance, not equality, and for the reason ScalarEdit has one: a value written into a
    /// FLOAT comes back as the nearest float, so 0.06 reads 0.05999999865889549.
    /// </summary>
    private static bool Agrees(double a, double b) =>
        Math.Abs(a - b) <= Math.Max(1e-4, Math.Abs(b) * 1e-6);

    /// <summary>
    /// Give a zero-valued field an actual value.
    ///
    /// A zero is not stored. Unversioned serialisation marks it in the header's zero mask and
    /// writes no bytes for it, so seven of the pistol's twenty rows cannot be edited by writing
    /// over anything -- there is nothing there. Raising one means rebuilding the row's header with
    /// that property no longer marked zero, and splicing four bytes in where schema order puts them.
    ///
    /// The header rebuild is <see cref="PropertyAdd.BuildHeader"/>, unchanged: fragments, runs and
    /// a zero mask sized the way the reader counts it. The splice is ZenPackage.SpliceExportData,
    /// which grows the export the edit lands in and moves every export after it -- the row is
    /// INSIDE an export rather than being one, and that turns out not to matter, because the fixup
    /// finds the export by offset rather than being told which.
    ///
    /// Handles a property absent from the header entirely as well as one merely marked zero. Both
    /// come out of the schema the same way, and a version that only handled the case seen in one
    /// game's tables is a version that breaks quietly on the next.
    /// </summary>
    public static byte[] RaiseZero(IFileProvider provider, byte[] raw, string packageName,
                                   string row, string field, double now,
                                   out int at, out string? why, Action<string>? log = null)
    {
        at = -1;
        why = null;
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null) { why = $"no mounted container holds {packageName}"; return raw; }

            var pkg = PropertyAdd.Open(provider, raw, packageName, container);
            var exports = pkg.ExportsLazy.Select(x => x.Value).ToArray();
            var index = Array.FindIndex(exports, e => e is UDataTable);
            if (index < 0) { why = $"{packageName} holds no DataTable export"; return raw; }
            if (exports[index] is not UDataTable table)
            { why = "the DataTable did not deserialise"; return raw; }
            if (exports[index].Class?.Object?.Value is not UStruct tableClass)
            { why = "the DataTable export's class is not a loaded struct"; return raw; }

            var tableSchema = PropertyAdd.SchemaFor(pkg, tableClass);
            var rowSchema = RowSchema(pkg, table);
            if (tableSchema is null || rowSchema is null)
            { why = "no property schema for the table or its rows"; return raw; }

            // Which schema slot the field is, resolved BEFORE the walk. The row may not mention it
            // at all, and a slot number discovered by seeing the property cannot be found for a
            // property that is not there.
            var targetIdx = -1;
            var targetType = "";
            var total = rowSchema.CountProperties(true);
            for (var i = 0; i < total; i++)
                if (rowSchema.TryGetValue(i, out var pi) &&
                    string.Equals(pi.Name, field, StringComparison.Ordinal))
                { targetIdx = i; targetType = pi.MappingType.Type; break; }
            if (targetIdx < 0) { why = $"'{field}' is not a property of the row struct"; return raw; }

            // THE WIDTH OF THE HOLE IS THE TYPE'S, not four. A raise SPLICES bytes in where schema
            // order puts them, so an int raised as a float is not merely a wrong value -- every
            // byte after it in the row is off by however much the widths differ.
            var targetKind = Kind(targetType);
            if (targetKind == Number.None)
            {
                why = $"'{row}.{field}' is a {targetType}, which is not a number this can write";
                return raw;
            }

            var headerSize = (int) BitConverter.ToUInt32(raw, 4);
            var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg)
            {
                Position = headerSize + (int) pkg.ExportMap[index].CookedSerialOffset,
            };
            if (!SkipBlock(ar, tableSchema, out why)) return raw;
            if (!exports[index].Flags.HasFlag(EObjectFlags.RF_ClassDefaultObject))
                if (ar.ReadBoolean()) ar.Position += 16;

            var numRows = ar.Read<int>();
            if (numRows < 0 || numRows > 100000)
            { why = $"read {numRows:N0} rows, which cannot be right"; return raw; }

            for (var r = 0; r < numRows; r++)
            {
                var name = ar.ReadFName().Text;
                var rowStart = (int) ar.Position;
                var header = new FUnversionedHeader(ar);
                var headerEnd = (int) ar.Position;

                var mine = string.Equals(name, row, StringComparison.OrdinalIgnoreCase);
                var present = new List<(int Index, bool Zero)>();
                var insertAt = -1;

                if (header.HasValues)
                {
                    using var it = new FIterator(header);
                    do
                    {
                        var (val, isNonZero) = it.Current;
                        present.Add((val, !isNonZero));
                        if (!rowSchema.TryGetValue(val, out var info))
                        { why = $"row '{name}': property {val} is not in the mappings"; return raw; }
                        if (!isNonZero) continue;

                        var here = (int) ar.Position;
                        _ = new FPropertyTag(ar, info, ReadType.NORMAL);
                        // The first value whose slot sits AFTER the target is where the target's
                        // own bytes belong: values are written in schema order, so this is the gap.
                        if (mine && insertAt < 0 && val > targetIdx) insertAt = here;
                    } while (it.MoveNext());
                }

                var rowEnd = (int) ar.Position;
                if (!mine) continue;

                if (present.Any(x => x.Index == targetIdx && !x.Zero))
                { why = $"'{row}.{field}' already has a value -- write over it instead"; return raw; }

                // Past every value, so the target's bytes go on the end of the row.
                if (insertAt < 0) insertAt = rowEnd;

                var oldHeader = new byte[headerEnd - rowStart];
                Array.Copy(raw, rowStart, oldHeader, 0, oldHeader.Length);

                present.RemoveAll(x => x.Index == targetIdx);
                present.Add((targetIdx, false));
                var newHeader = PropertyAdd.BuildHeader(present);

                log?.Invoke($"row {row}: slots " +
                            string.Join(" ", present.OrderBy(x => x.Index)
                                                    .Select(x => $"{x.Index}{(x.Zero ? "z" : "v")}")) +
                            $" | header {Convert.ToHexString(oldHeader)} -> {Convert.ToHexString(newHeader)}" +
                            $" | rowStart {rowStart} headerEnd {headerEnd} insertAt {insertAt} rowEnd {rowEnd}");

                var value = Bytes(now, targetKind);
                var replacement = new byte[newHeader.Length + (insertAt - headerEnd) +
                                           value.Length + (rowEnd - insertAt)];
                var w = 0;
                newHeader.CopyTo(replacement, w); w += newHeader.Length;
                Array.Copy(raw, headerEnd, replacement, w, insertAt - headerEnd);
                w += insertAt - headerEnd;
                value.CopyTo(replacement, w); w += value.Length;
                Array.Copy(raw, insertAt, replacement, w, rowEnd - insertAt);

                var written = ZenPackage.SpliceExportData(raw, rowStart, rowEnd - rowStart, replacement);

                // The WHOLE table read back, not just the field. A rebuilt header that
                // desynchronises the reader does not fail loudly -- it produces different numbers
                // in other rows, which is exactly what a balance mod would ship without noticing.
                var after = Walk(provider, written, packageName, out var backWhy);
                if (backWhy is not null)
                { why = $"the raised row would not read back: {backWhy}"; return raw; }

                var got = after.FirstOrDefault(f =>
                    string.Equals(f.Row, row, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Name, field, StringComparison.Ordinal));
                // CHECKED UNDER THE TYPE, not as a float. This check was float-only and it is
                // where the whole-game sweep's last twelve refusals came from: a raised
                // IntProperty holds int 3, which read as a float is 4E-45, so a correct write was
                // reported as a failure. A DoubleProperty is eight bytes and failed the width test
                // for the same reason. Both were the checker being wrong, not the write.
                var wantWidth = targetKind == Number.Double ? 8 : 4;
                if (got is null || got.Length != wantWidth)
                {
                    why = $"'{row}.{field}' is a {targetType} and is " +
                          $"{(got is null ? "not present" : got.Length + " byte(s)")} after the " +
                          $"edit, not {wantWidth}";
                    return raw;
                }

                var reads = ReadAs(written, got.Start, targetKind);
                if (!Agrees(reads, now))
                { why = $"'{row}.{field}' reads {reads} after writing {now}"; return raw; }

                at = got.Start;
                return written;
            }

            why = $"no row named '{row}' in this table";
            return raw;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// The schema for the table's ROW struct.
    ///
    /// A native struct carries no property list of its own, exactly as a native class does not, so
    /// it comes from the mappings by name -- FAttributeMetaData is one, and every weapon table in
    /// this game uses it. A Blueprint struct is described by wrapping its own list.
    /// </summary>
    private static Struct? RowSchema(IoPackage pkg, UDataTable table)
    {
        if (table.RowStructName is { Length: > 0 } named && pkg.Mappings is { } m &&
            m.Types.TryGetValue(named, out var native))
            return native;

        var ptr = table.GetOrDefault<FPackageIndex?>("RowStruct");
        if (ptr is not null && ptr.TryLoad<UStruct>(out var struc) && struc is not null)
            return PropertyAdd.SchemaFor(pkg, struc);

        return null;
    }

    /// <summary>Read an unversioned property block only to get past it.</summary>
    private static bool SkipBlock(FAssetArchive ar, Struct schema, out string? why)
    {
        why = null;
        var header = new FUnversionedHeader(ar);
        if (!header.HasValues) return true;

        using var it = new FIterator(header);
        do
        {
            var (val, isNonZero) = it.Current;
            if (!isNonZero) continue;
            if (!schema.TryGetValue(val, out var info))
            { why = $"property {val} is not in the mappings"; return false; }
            _ = new FPropertyTag(ar, info, ReadType.NORMAL);
        } while (it.MoveNext());

        return true;
    }
}

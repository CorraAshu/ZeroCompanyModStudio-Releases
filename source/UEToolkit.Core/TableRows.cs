using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;

namespace UEToolkit.Core;

/// <summary>
/// One data table read as a GRID: its rows, its fields, and which of them a mod can change.
///
/// The reading half of the answer to "the economy files are unclickable" -- <see cref="TableIndex"/>
/// finds the tables, this says what is in one, and the override editor writes through
/// <see cref="DataTableEdit"/>, which has been able to do it since weapon stats were built. Nothing
/// new writes anything: the whole gap was that no interface could show a table that was not a
/// weapon's attribute table.
///
/// **NUMERIC IS NOT THE SAME AS EDITABLE**, and a sweep over all 860 of the game's tables is what
/// established that rather than a guess. A DoubleProperty reads as a number and is eight bytes; an
/// IntProperty reads as a number and is four, exactly like a float, so a float writer accepts it
/// and destroys it. So the mappings' TYPE decides which cells get a box -- see
/// <see cref="DataTableEdit.Field"/>, which now carries it for the same reason.
///
/// **A ZERO IS STILL EDITABLE HERE, unlike a property on an export.** Unversioned serialisation
/// stores no zeros, so a zero field has no bytes -- but <see cref="DataTableEdit.SetNumber"/> falls
/// through to <see cref="DataTableEdit.RaiseZero"/>, which grows the row and puts the property
/// back. It is a larger edit and it is reported as one, and that is the only difference the author
/// sees.
///
/// **THE COMPOSITE TRAP, which is why this reads ParentTables.** <c>DT_Costs</c> is a
/// <c>CompositeDataTable</c>: it declares that its rows come from <c>DT_CostsTable_v4</c>, and
/// cooking has baked a copy of those rows into it as well. So there are five tables in
/// <c>DynamicGACosts</c> that all look editable, all accept an edit, and all read back clean --
/// and which one the running game takes its numbers from is not something the files can settle.
/// Somebody who picks the wrong one of five gets a mod that builds, installs, verifies and does
/// nothing, and concludes the tool is broken. So a composite says what it is made of, and the
/// panel offers the parent.
/// </summary>
public static class TableRows
{
    /// <summary>
    /// One field of one row. <paramref name="Number"/> is meaningful only when
    /// <paramref name="Numeric"/>; everything else is shown as <paramref name="Text"/>.
    /// </summary>
    public sealed record Cell(string Field, double Number, string Text, bool Numeric, bool Stored,
                              string Type)
    {
        /// <summary>
        /// Whether a mod can actually change it -- decided by the mappings' type, not by whether
        /// the value came back looking like a number. A bool is a number and one byte; a double is
        /// a number and eight. Offering a box for either offers a box the build refuses.
        /// </summary>
        public bool Editable => DataTableEdit.Kind(Type) != DataTableEdit.Number.None;
    }

    /// <summary>One row, by the name the table keys it on.</summary>
    public sealed record Row(string Name, List<Cell> Cells);

    /// <summary>
    /// A whole table. <paramref name="Fields"/> is every field in serialised order and
    /// <paramref name="Numbers"/> the subset a mod can WRITE -- kept apart so a panel can put the
    /// editable ones in columns and the rest in a tooltip, rather than making the grid twice as
    /// wide as the window for values nothing can change.
    /// </summary>
    public sealed record Reading(string Package, string Name, string RowStruct, string Class,
                                 List<string> Parents, List<string> Fields, List<string> Numbers,
                                 List<Row> Rows, int TotalRows, string? Why);

    /// <summary>
    /// How many rows are read before this stops. A grid is a thing a person scans, and a table
    /// with thousands of rows is one nobody is going to scan -- but silently showing the first few
    /// hundred as though they were all of them is the failure worth avoiding, so the count that
    /// EXISTS is reported separately from the count that was read.
    /// </summary>
    public const int MaxRows = 500;

    /// <summary>
    /// Read one table.
    ///
    /// PRISTINE, like every other authoring read: with a mod installed, the plain provider finds
    /// the mod's copy of a table it has already overridden, and a second edit would then be built
    /// on top of the first.
    /// </summary>
    public static Reading Read(IFileProvider provider, string package)
    {
        var name = package.Length == 0 ? "" : package[(package.LastIndexOf('/') + 1)..];
        var empty = new Reading(package, name, "", "", [], [], [], [], 0, null);

        var raw = AssetCopy.PristineRead(provider, package, out var readWhy)?.Pieces
                           .FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null)
            return empty with { Why = readWhy ?? $"{name} could not be read out of the game" };

        UDataTable? table;
        UObject? export;
        try
        {
            var exports = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, raw, package));
            table = exports.OfType<UDataTable>().FirstOrDefault();
            export = table;
        }
        catch (Exception e) { return empty with { Why = e.Message }; }

        if (table is null || export is null)
            return empty with { Why = $"{name} holds no data table" };

        // WHERE THE BYTES ARE AND WHAT THEY ARE. The walk is the same one the writer uses, so
        // "stored" means exactly "SetNumber will overwrite rather than grow", and the type is the
        // mappings' own. Keyed by a TUPLE rather than a joined string: a separator inside a key is
        // a bug waiting for a row name that contains it, and one of those keys quietly acquired a
        // NUL byte while this file was being written.
        var walked = new Dictionary<(string Row, string Field), DataTableEdit.Field>();
        foreach (var f in DataTableEdit.Walk(provider, raw, package, out _))
            walked[(f.Row, f.Name)] = f;

        var parents = new List<string>();
        if (PartDetails.Unwrap(PartDetails.Value(export.Properties, "ParentTables"))
            is UScriptArray parentArray)
            foreach (var one in parentArray.Properties)
                if (PartDetails.SoftPathOf(one.GenericValue) is { Length: > 0 } path)
                    parents.Add(AssetLoader.PackagePath(path));

        var fields = new List<string>();
        var numbers = new List<string>();
        var rows = new List<Row>();

        foreach (var (rowName, row) in table.RowMap)
        {
            if (rows.Count >= MaxRows) break;
            var cells = new List<Cell>();
            foreach (var tag in row.Properties)
            {
                var field = tag.Name.Text;
                if (!fields.Contains(field, StringComparer.Ordinal)) fields.Add(field);

                var value = tag.Tag?.GenericValue;
                var number = Numeric(value);
                var found = walked.GetValueOrDefault((rowName.Text, field));

                var cell = new Cell(field, number ?? 0, Readable(value), number is not null,
                                    found is { Length: > 0 }, found?.Type ?? "");
                if (cell.Editable && !numbers.Contains(field, StringComparer.Ordinal))
                    numbers.Add(field);
                cells.Add(cell);
            }
            rows.Add(new Row(rowName.Text, cells));
        }

        return new Reading(package, name, table.RowStructName ?? "",
                           export.ExportType ?? "DataTable",
                           parents, fields, numbers, rows, table.RowMap.Count, null);
    }

    /// <summary>Any numeric property as a double, whatever width it was stored at.</summary>
    private static double? Numeric(object? value) => value switch
    {
        float f => f,
        double d => d,
        int i => i,
        short s => s,
        long l => l,
        uint u => u,
        _ => null,
    };

    /// <summary>Anything not a number, said the way the row dump says it.</summary>
    private static string Readable(object? value)
    {
        var unwrapped = PartDetails.Unwrap(value);
        var text = unwrapped switch
        {
            null => "",
            FStructFallback fb when PartDetails.Value(fb.Properties, "TagName") is { } tag =>
                tag.ToString() ?? "",
            _ => unwrapped.ToString() ?? "",
        };
        return text.Replace("\r", " ").Replace("\n", " ");
    }
}

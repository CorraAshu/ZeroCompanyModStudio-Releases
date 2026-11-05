using CUE4Parse.FileProvider;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE DATA TABLE, open as a tab.
///
/// The metadata is free — the index already has the name, the folder and the class. The ROWS are
/// a package read, so they arrive after the tab does, and the sheet says it is still reading
/// rather than showing an empty table that looks like an answer.
///
/// TWO COUNTS, NOT ONE. TableRows stops at 500 and reports what it READ separately from what
/// EXISTS, for the reason it writes down: "silently showing the first few hundred as though they
/// were all of them is the failure worth avoiding". The sheet repeats both.
/// </summary>
public sealed class TableTab : SheetTab
{
    private readonly TableIndex.Entry _entry;
    private TableRows.Reading? _read;

    public TableTab(TableIndex.Entry entry)
    {
        _entry = entry;
        Title = entry.Name;
        Loading = true;
    }

    public override string Title { get; }
    public override string Tooltip => _entry.Package;
    public override string Subtitle => _entry.Package;
    public TableIndex.Entry Entry => _entry;

    /// <summary>The rows as read, once they have arrived.</summary>
    public TableRows.Reading? Reading => _read;

    /// <summary>
    /// A table is edited through its NUMBERS: one box per number a mod can write, the way an
    /// ability's effects are. There is no name or mesh to change on a table.
    /// </summary>
    public override bool CanEdit => true;

    public override string EditBadge => "EDITING THE GAME'S OWN TABLE";

    public override string EditWarning =>
        "This mod ships a changed copy of the game's table. A row is read by everything that names it, " +
        "so a number changed here changes the game everywhere that row is used, and another mod " +
        "changing the same table will conflict with this one.";

    public override string Headline
    {
        get
        {
            var bits = new List<string>();
            if (_entry.IsComposite) bits.Add("composite — its rows come from other tables");
            if (_read is { TotalRows: > 0 } r)
                bits.Add(r.TotalRows == r.Rows.Count
                    ? $"{r.TotalRows:N0} row(s)"
                    : $"{r.Rows.Count:N0} of {r.TotalRows:N0} rows read");
            return string.Join("   ·   ", bits);
        }
    }

    public override string Problem => _read?.Why ?? "";

    /// <summary>Read the rows off the UI thread. A table is a package load like any other.</summary>
    public async Task LoadAsync(IFileProvider provider)
    {
        var package = _entry.Package;
        _read = await Task.Run(() => TableRows.Read(provider, package));
        Loading = false;
        Raise(nameof(Headline));
        Raise(nameof(HasHeadline));
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
        Raise(nameof(Sheet));
    }

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Folder", _entry.Folder);
            Fact("Class", _entry.Class);
            if (_read is { RowStruct.Length: > 0 }) Fact("Row type", Leaf(_read.RowStruct));
            blocks.Add(new SheetFact { Label = "Package", Value = _entry.Package, Wide = true });

            if (_read is not { } r) return blocks;

            if (r.Parents.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Built from ({r.Parents.Count})" });
                foreach (var p in r.Parents) blocks.Add(new SheetBullet { Text = Leaf(p) });
            }

            if (r.Fields.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Fields ({r.Fields.Count})" });
                var fband = false;
                foreach (var f in r.Fields)
                {
                    blocks.Add(new SheetPair
                    {
                        Left = f,
                        // WHICH FIELDS ARE NUMBERS, because those are the ones that can be edited
                        // today. Saying so here is the difference between a list of names and a
                        // list of what a mod can reach.
                        Right = r.Numbers.Contains(f) ? "number" : "",
                        Band = fband,
                    });
                    fband = !fband;
                }
            }

            if (r.Rows.Count > 0)
            {
                blocks.Add(new SheetBand
                {
                    Text = r.TotalRows == r.Rows.Count
                        ? $"Rows ({r.TotalRows:N0})"
                        : $"Rows ({r.Rows.Count:N0} of {r.TotalRows:N0})",
                });
                foreach (var row in r.Rows) blocks.Add(new SheetBullet { Text = RowName(row) });
            }

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }
        }
    }

    /// <summary>
    /// A row's name, however TableRows.Row spells it.
    ///
    /// Read through reflection rather than assumed, because printing the record's own ToString is
    /// exactly the fault he called out twice — once on this section's list and once on the part
    /// sheet's meshes. If there is no Name to find, the type name is at least honest about it.
    /// </summary>
    private static string RowName(object row)
    {
        var t = row.GetType();
        foreach (var name in new[] { "Name", "Key", "Row", "Id" })
            if (t.GetProperty(name)?.GetValue(row) is string s && s.Length > 0)
                return s;
        return t.Name;
    }
}

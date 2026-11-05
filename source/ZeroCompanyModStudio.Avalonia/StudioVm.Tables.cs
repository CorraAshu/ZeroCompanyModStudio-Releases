using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// EDITING A DATA TABLE, in the Tables section.
///
/// THE FORMAT IS THE 1.6.1 ONE, and it is not new. The old panel's "Change these numbers" put an
/// override on the TABLE'S OWN PACKAGE (ModOverride.Target = the package) and the grid under
/// "Its numbers" recorded each typed cell as
/// <c>ov.Numbers.Add(new NumberChange(row.Name, field, cell.Number, value))</c> (Overrides.cs,
/// DrawTableNumbers / SetNumberAt). Row is the table's row name, Field is the struct field, Was is
/// what the game ships, Now is what was typed; project JSON is overrides[].numbers[] with
/// row/field/was/now. This writes exactly that, so a project that already carries table edits
/// opens with the same values and builds unchanged. ModBuild applies them with
/// DataTableEdit.SetNumber (the row walk) and reads them back.
///
/// THE CELLS ARE A LIST, not a grid, by the author's decision: "Row  ·  Field" with a box each, in
/// the NUMBERS card the ability sheet already has (EffectRow: Export = row name, Path = field), so
/// a changed cell is green, resets to the game's own, and counts on the card the same way every
/// other number does. A table can hold thousands of them, so the card has a find box and shows the
/// first screenful of what matches rather than the lot.
///
/// EDIT IS A CHOICE, as it is for an ability: opening a table reads it, Edit (the section list's
/// right-click) opens the editor, and nothing is written into the project until a number is typed.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// Enough cells to read, few enough that the card stays responsive: the list is not
    /// virtualised, so every cell is a live row of controls. The find box reaches the rest.
    /// </summary>
    private const int TableCellsShown = 300;

    /// <summary>
    /// Six decimals, not three. A table full of 0.0625 and 1.3333 shows a rounded "game" value
    /// at three, and a box that reads back its own rounded text on leaving would then record an
    /// edit nobody made. (The same reason an unchanged box is left alone below.)
    ///
    /// AND INVARIANT, like everything typed into or printed in a number box here (NumberText):
    /// the machine's regional format made "0.5" mean five on a German PC.
    /// </summary>
    private static string Shown(double value) => NumberText.Show(value);

    private static TableRows.Cell? CellOf(TableRows.Reading r, string row, string field)
        => r.Rows.FirstOrDefault(x => Same(x.Name, row))?.Cells.FirstOrDefault(c => c.Field == field);

    /// <summary>
    /// A table tab, set up for the card: it has a find box, its help line says what a TABLE'S
    /// cells mean, and typing in the box narrows the list.
    /// </summary>
    private TableTab NewTableTab(TableIndex.Entry entry)
    {
        var tab = new TableTab(entry);
        tab.EffectsFilterable = true;
        tab.EffectsHelp =
            "'Now' is what the game ships. Anything you leave alone is not shipped. A cell marked " +
            "'zero, not stored' is a zero the game keeps no bytes for: giving it a value rebuilds " +
            "that row to hold one, which is a larger edit than the rest.";
        tab.EffectsFilterChanged = () => RefillTable(tab);
        return tab;
    }

    /// <summary>Read the rows, then fill the editable cells off them and the mod as it stands.</summary>
    private async Task LoadTableAsync(TableTab tab, CUE4Parse.FileProvider.IFileProvider provider)
    {
        await tab.LoadAsync(provider);
        RefillTable(tab);
    }

    /// <summary>
    /// Open the editor on a table: the NUMBERS card, in edit mode.
    ///
    /// ONLY THAT CARD. A table has no name, no mesh, no picture; every other card is absent
    /// (ShowOnly), and the card itself turns on in RefillTable once the rows have been read, so a
    /// table still being read does not draw an empty one.
    /// </summary>
    public void OpenTableEditor(TableTab tab)
    {
        tab.ShowOnly();
        tab.SecEffects.Open = true;
        tab.BeginEdit(tab.Title, "", "", "");
        tab.FoldSheetOnce();
        RefillTable(tab);
        Say($"editing {tab.Title}: type a new number over any cell; nothing is written until you do");
    }

    /// <summary>Rebuild the table's cells off the reading, the find box and the override as it stands.</summary>
    public void RefillTable(TableTab tab)
    {
        if (tab.Reading is not { } r || r.Why is not null) return;

        var ov = FindOverride(r.Package);
        var find = tab.EffectsFilter.Trim();
        var cells = new List<SheetTab.EffectRow>();
        var total = 0;      // editable cells in the whole table
        var matching = 0;   // ... of which match the find box

        foreach (var row in r.Rows)
        {
            foreach (var field in r.Numbers)
            {
                var cell = row.Cells.FirstOrDefault(c => c.Field == field);
                if (cell is null || !cell.Editable) continue;
                total++;

                if (find.Length > 0 &&
                    row.Name.Contains(find, StringComparison.OrdinalIgnoreCase) is false &&
                    field.Contains(find, StringComparison.OrdinalIgnoreCase) is false)
                    continue;

                matching++;
                if (cells.Count >= TableCellsShown) continue;

                var edit = ov?.Numbers.FirstOrDefault(n => Same(n.Row, row.Name) && Same(n.Field, field));
                cells.Add(new SheetTab.EffectRow
                {
                    Export = row.Name,
                    Path = field,
                    // THE WHOLE ROW NAME. Shortened to its last segment, two rows that differ only
                    // in their prefix would read as the same row twice.
                    Label = $"{row.Name}  ·  {field}",
                    Detail = cell.Type,
                    Where = $"{row.Name}.{field}",
                    Now = Shown(cell.Number),
                    // A table row can be GROWN, so a zero that is not stored is still editable.
                    Stored = true,
                    Value = Shown(edit?.Now ?? cell.Number),
                    Mine = edit is not null,
                    State = edit is not null ? "" : StateOf(cell),
                    Note = cell.Stored ? "" : ZeroNote,
                });
            }
        }

        tab.EffectsSay(cells, [], []);
        tab.SecEffects.Present = true;
        CountTable(tab);

        tab.EffectsNote =
            (total == 0 ? "This table has no numbers a mod can change. " : "") +
            (find.Length > 0 && matching == 0 ? $"Nothing matches '{find}'. " : "") +
            (matching > cells.Count
                ? $"Showing {cells.Count:N0} of {matching:N0} " +
                  (find.Length > 0 ? "matching " : "editable ") +
                  "numbers: type a row or a field name in the box to reach the rest. "
                : "") +
            "A row is shared by everything that names it: changing one changes the game " +
            "everywhere it is used.";

        // THE COMPOSITE WARNING IS A WARNING, so it takes the card's amber line -- the slot an
        // ability uses to say where the rest of its numbers live -- and not the faint grey note
        // that is the quietest text on the card. It is the one sentence here a person must not
        // skim past: the edit builds and reads back clean, and the game may still not use it.
        tab.EffectsElsewhere = r.Parents.Count > 0
            ? "THIS IS A COMPOSITE TABLE. Its rows come from " +
              string.Join(", ", r.Parents.Select(p => p[(p.LastIndexOf('/') + 1)..])) +
              ", and which of the two the running game takes its numbers from is not something " +
              "the files settle: change the row there as well, or instead."
            : "";
    }

    private const string ZeroNote =
        "The game stores no bytes for a zero. Giving it a value rebuilds the row to hold one.";

    private static string StateOf(TableRows.Cell cell) => cell.Stored ? "" : "zero, not stored";

    /// <summary>"3 changed" on the card, off the override as it stands.</summary>
    private void CountTable(TableTab tab)
    {
        var changed = tab.Reading is { } r ? FindOverride(r.Package)?.Numbers.Count ?? 0 : 0;
        tab.SecEffects.Count = changed == 0 ? "" : $"{changed} changed";
    }

    /// <summary>
    /// Record, replace or drop one cell. Setting it back to the game's own removes the entry
    /// rather than storing "17 becomes 17": a no-op edit would still be shipped, still be checked
    /// against the asset, and still be one more thing that can fail a build for no gain.
    ///
    /// THE ROW IS UPDATED IN PLACE, not rebuilt. A rebuild replaces every box on the card, and a
    /// box replaced while the next one is taking focus is a Tab key that lands nowhere: typing
    /// down a column of numbers is the whole use of this.
    /// </summary>
    public void SetTableNumber(TableTab tab, SheetTab.EffectRow row)
    {
        if (_mod is null || tab.Reading is not { } r) return;
        if (CellOf(r, row.Export, row.Path) is not { } cell) return;

        var existing = FindOverride(r.Package)?.Numbers.FirstOrDefault(
            n => Same(n.Row, row.Export) && Same(n.Field, row.Path));

        // LEAVING A BOX THAT WAS NOT CHANGED changes nothing. Without this, tabbing through a
        // column re-records every box it passed, and a game value that does not survive being
        // printed (a float is not a decimal) would be saved as an edit of its own.
        var typed = (row.Value ?? "").Trim();
        var shown = existing is null ? row.Now : Shown(existing.Now);
        if (typed == shown) { row.Value = shown; return; }

        // A NEW NUMBER, so whatever was said about the last one no longer applies.
        tab.Feedback("");

        // A REFUSAL IS SAID WHERE THE PERSON IS LOOKING, not only in the log: red in the log, on
        // the card above the list, and in a word beside the box. The box goes back to what it
        // held, so without the reason it reads as a box that ignored what was typed.
        void Refuse(string why, string word)
        {
            var line = $"{row.Where}: {why} -- the box is back to {shown}";
            SayBad($"{tab.Title}: {line}");
            tab.Feedback(line, bad: true);
            row.Value = shown;
            row.State = word;
        }

        // INVARIANT, whatever the PC's regional format is: "0.5" and "2,5" mean a half and two
        // and a half on every machine (NumberText says why, and what is refused as ambiguous).
        if (!NumberText.TryParse(typed, out var now, out var parseWhy))
        { Refuse(parseWhy, "not accepted"); return; }

        // THE FIELD'S OWN WIDTH decides what can be stored: an int cannot hold 2.5 (the build
        // would write 2 and then refuse, because it reads back as something else), and a float
        // cannot hold what overflows one. A number that is changed to fit is SAID, in words that
        // name both numbers: the box shows the whole number afterwards, and "I typed 2.5 and it
        // says 3" with no reason is the same silence as a refusal.
        var kind = DataTableEdit.Kind(cell.Type);
        var rounded = "";
        if (kind == DataTableEdit.Number.Int)
        {
            var whole = Math.Round(now, MidpointRounding.AwayFromZero);
            if (Math.Abs(whole) > int.MaxValue)
            { Refuse($"'{typed}' is more than a whole number field can hold", "too large"); return; }
            if (whole != now) rounded = $"{Shown(now)} rounded to {Shown(whole)}";
            now = whole;
        }
        else if (kind == DataTableEdit.Number.Float && Math.Abs(now) > float.MaxValue)
        { Refuse($"'{typed}' is more than this field can hold", "too large"); return; }

        var was = cell.Number;
        var changed = Math.Abs(now - was) > 0.0001;

        // AN OVERRIDE IS MADE ONLY WHEN THERE IS A CHANGE TO PUT IN IT: typing the game's own
        // number into a table the mod has never touched must not ship a rewritten package.
        var ov = changed ? MakeOverride(r.Package) : FindOverride(r.Package);
        if (ov is null)
        {
            row.Value = row.Now;
            if (rounded.Length > 0)
            {
                // 2.4 into a whole-number field is the game's own 2 again, and that is worth
                // saying: the box has gone back and nothing was recorded.
                var line = $"{row.Where}: {rounded}, the game's own number, so nothing changed " +
                           "(the field holds whole numbers)";
                Say($"{tab.Title}: {line}");
                tab.Feedback(line);
            }
            return;
        }

        ov.Numbers.RemoveAll(x => Same(x.Row, row.Export) && Same(x.Field, row.Path));
        if (changed) ov.Numbers.Add(new ModOverride.NumberChange(row.Export, row.Path, was, now));

        _mod.Save();

        row.Value = Shown(changed ? now : was);
        row.Mine = changed;
        row.State = changed ? (rounded.Length > 0 ? $"rounded to {Shown(now)}" : "") : StateOf(cell);
        CountTable(tab);

        var said = changed
            ? $"{row.Where} = {Shown(now)}, was {Shown(was)}"
            : $"{row.Where} is the game's own again";
        if (rounded.Length > 0)
        {
            // "2.5 rounded to 3, the field holds whole numbers"; or, when the rounding lands on
            // the game's own number and so takes the change out, says that instead.
            var why = changed ? $"{rounded}, the field holds whole numbers"
                              : $"{rounded}, the game's own number, so the change is taken out " +
                                "(the field holds whole numbers)";
            said += $" ({why})";
            tab.Feedback($"{row.Where}: {why}");
        }
        Say($"{tab.Title}: {said}");
        AfterEditsChanged();
    }

    /// <summary>Put the game's own number back, and drop the entry.</summary>
    public void ResetTableNumber(TableTab tab, SheetTab.EffectRow row)
    {
        if (_mod is null || tab.Reading is not { } r) return;
        if (FindOverride(r.Package) is not { } ov) return;
        if (CellOf(r, row.Export, row.Path) is not { } cell) return;

        tab.Feedback("");
        ov.Numbers.RemoveAll(x => Same(x.Row, row.Export) && Same(x.Field, row.Path));
        _mod.Save();

        row.Value = row.Now;
        row.Mine = false;
        row.State = StateOf(cell);
        CountTable(tab);

        Say($"{tab.Title}: {row.Where} is the game's own again");
        AfterEditsChanged();
    }

#if DEV
    /// <summary>
    /// DEV: `--tableedit "&lt;table name&gt;|&lt;row name&gt;|&lt;field&gt;|&lt;value&gt;"` does what a person does: picks
    /// the table in the Tables list, chooses Edit, waits for its rows, finds the cell (typing the
    /// row's name in the find box if the first screenful does not hold it) and types the value
    /// into the box, through the handler the box uses. An empty value types nothing and reports the cell;
    /// an optional fifth part is then typed into the find box.
    /// </summary>
    public async Task DevTableEditAsync(string spec,
                                        Func<SheetTab.EffectRow, string, Task<bool>>? typeIntoBox = null)
    {
        var parts = spec.Split('|');
        for (var wait = 0; wait < 600 && _provider is null; wait++) await Task.Delay(200);
        if (parts.Length is not (4 or 5) || _provider is null)
        { Say($"--tableedit: need table|row|field|value[|find], and the game open ({spec})"); return; }
        if (_mod is null) { Say("--tableedit: open a mod first (--openmod <json>)"); return; }

        var entry = TableIndex.Of(_provider).FirstOrDefault(
            t => t.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (entry is null) { Say($"--tableedit: no table named {parts[0]}"); return; }

        var item = new SectionItem { Name = entry.Name, Payload = entry };
        EditPart(item);
        if (Tab is not TableTab tab || tab.Entry.Package != entry.Package)
        { Say("--tableedit: the table did not open"); return; }

        for (var wait = 0; wait < 600 && tab.Loading; wait++) await Task.Delay(200);
        RefillTable(tab);

        SheetTab.EffectRow? Find() => tab.EffectRows.FirstOrDefault(
            r => r.Export.Equals(parts[1], StringComparison.OrdinalIgnoreCase) &&
                 r.Path.Equals(parts[2], StringComparison.OrdinalIgnoreCase));

        var cell = Find();
        if (cell is null) { tab.EffectsFilter = parts[1]; cell = Find(); }
        if (cell is null)
        {
            Say($"--tableedit: no editable cell {parts[1]}.{parts[2]} in {tab.Title} " +
                $"(reading why={tab.Reading?.Why}, {tab.Reading?.Rows.Count} rows); first cells: " +
                string.Join(" | ", tab.EffectRows.Take(6).Select(r => $"{r.Export}.{r.Path}={r.Now}")));
            return;
        }

        // AN EMPTY VALUE TYPES NOTHING: open the editor on a table and report what the cell shows,
        // which is how a project that already carries an edit is looked at.
        if (parts[3].Length == 0)
        {
            Say($"[tableedit] {cell.Export}.{cell.Path}: game {cell.Now}, box shows {cell.Value}, mine={cell.Mine}, " +
                $"state '{cell.State}', card count '{tab.SecEffects.Count}', {tab.EffectRows.Count} cell(s) listed");
            if (parts.Length == 5) tab.EffectsFilter = parts[4];
            return;
        }

        Say($"[tableedit] {cell.Export}.{cell.Path}: game {cell.Now}, typing {parts[3]}");

        // INTO THE BOX ON SCREEN when the view can find it, and the box is then LEFT, so the
        // handler a person's own typing reaches is what runs. Without a box (nothing drawn yet)
        // the view model's method is called directly, and the line below says which happened.
        // The value "reset" presses the row's Reset button instead of typing.
        var press = parts[3].Equals("reset", StringComparison.OrdinalIgnoreCase);
        var viaBox = typeIntoBox is not null && await typeIntoBox(cell, press ? "<reset>" : parts[3]);
        if (!viaBox)
        {
            if (press) ResetTableNumber(tab, cell);
            else
            {
                cell.Value = parts[3];
                SetTableNumber(tab, cell);
            }
        }
        var storedNow = FindOverride(tab.Entry.Package)?.Numbers
            .FirstOrDefault(n => Same(n.Row, cell.Export) && Same(n.Field, cell.Path));
        Say($"[tableedit] {(viaBox ? "typed into the box and left it" : "no box was on screen; called the handler's own method")}: " +
            $"box now shows {cell.Value}, mine={cell.Mine}, row says '{cell.State}', card count '{tab.SecEffects.Count}', " +
            $"override numbers={FindOverride(tab.Entry.Package)?.Numbers.Count ?? 0}, " +
            $"recorded now={(storedNow is null ? "(none)" : NumberText.Show(storedNow.Now))}, " +
            $"the card says {(tab.EffectsFeedback.Length == 0 ? "(nothing)" : $"{(tab.EffectsFeedbackBad ? "RED" : "AMBER")} '{tab.EffectsFeedback}'")}");

        // The optional fifth part types into the find box afterwards, so a photograph can show
        // the cell that was changed rather than the first screenful of a long list.
        if (parts.Length == 5) tab.EffectsFilter = parts[4];
    }

    /// <summary>
    /// DEV: `--switchmod &lt;project.json&gt;`, after `--tableedit`, opens ANOTHER mod while the table
    /// card is still up, says what the card shows, then leaves every box on it (what a click
    /// elsewhere does) and says what the second mod now holds. The card must show the second
    /// mod's numbers (none), and leaving a box must record nothing into it.
    /// </summary>
    public async Task DevSwitchModAsync(string path)
    {
        if (Tab is ModificationTab attachment) { await DevSwitchModAttachAsync(attachment, path); return; }
        if (Tab is not TableTab tab || tab.Reading is not { } r)
        { Say("[switchmod] no table card is open"); return; }

        string Card() => $"card count '{tab.SecEffects.Count}', {tab.EffectRows.Count(x => x.Mine)} green row(s), " +
                         "boxes: " + string.Join(" ", tab.EffectRows.Take(4).Select(x => $"{x.Path}={x.Value}(game {x.Now})"));

        Say($"[switchmod] before: {Card()}; mod is {Path.GetFileName(_modPath)}");
        OpenMod(path);
        await Task.Delay(500);
        Say($"[switchmod] after opening {Path.GetFileName(path)}: {Card()}");

        // LEAVE EVERY BOX, through the handler a lost focus reaches.
        foreach (var row in tab.EffectRows.ToList()) SetTableNumber(tab, row);

        var held = FindOverride(r.Package)?.Numbers.Count ?? 0;
        var onDisk = _mod is null ? "(no mod)" : $"{_mod.Overrides.Count} override(s) in the project, {_mod.Overrides.Sum(o => o.Numbers.Count)} number(s)";
        Say($"[switchmod] after leaving every box: this mod holds {held} number(s) on {tab.Title}; {onDisk}; {Card()}");
    }
#endif
}

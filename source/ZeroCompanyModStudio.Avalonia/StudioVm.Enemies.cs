using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// REBALANCING A GROUP OF ENEMIES.
///
/// The catalogue is a SECTION, as every other list of the game's own things is — pick a group,
/// it opens as a tab, and the tab is where the numbers are. The old studio put the catalogue
/// behind "Add to this mod" because its left column was the mod's contents; here the left column
/// is already a list of the game and the section strip is where lists of the game live.
///
/// THE BLAST RADIUS IS THE POINT. Several groups share their stat tables with a DIFFERENT
/// faction — Outlaw and Pyke Marauders read the Separatist BX Droid Commando's — so "used by"
/// is on the row, in the sheet, and in the warning line, rather than behind a hover.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// Every group in the game, read ONCE and kept.
    ///
    /// EnemyStats.All opens every enemy spec and walks the 277 pawn definitions. That is fine
    /// once; the old studio's own note records what it cost when something asked for it every
    /// frame. Nothing here asks until the Enemies section is opened.
    /// </summary>
    private List<EnemyStats.Group>? _enemyGroups;

    public IReadOnlyList<EnemyStats.Group> EnemyGroups => _enemyGroups ?? [];

    /// <summary>Read the catalogue if it has not been read. Off the UI thread; call it awaited.</summary>
    public async Task<List<EnemyStats.Group>> EnemyGroupsAsync()
    {
        if (_enemyGroups is not null) return _enemyGroups;
        if (_provider is null) return [];

        var provider = _provider;
        _enemyGroups = await Task.Run(() =>
        {
            try { return EnemyStats.All(provider); }
            catch (Exception) { return new List<EnemyStats.Group>(); }
        });

        Say($"enemy groups: {_enemyGroups.Count} tactical specialization(s)");
        return _enemyGroups;
    }

    /// <summary>
    /// Open one group's numbers.
    ///
    /// ONE TAB PER GROUP, because two panels on one group would be two places an edit could
    /// disagree — and the multipliers are written straight into the project, so the second one
    /// would be showing a state it read before the first wrote.
    /// </summary>
    public async Task OpenEnemyGroup(EnemyStats.Group group)
    {
        if (_provider is null) { Say("the game is not mounted yet"); return; }

        if (Tabs.OfType<EnemyTab>().FirstOrDefault(
                t => t.Group.Leaf.Equals(group.Leaf, StringComparison.OrdinalIgnoreCase)) is { } open)
        { Tab = open; return; }

        var tab = new EnemyTab(group);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        tab.ShowOnly(tab.SecEnemy);
        tab.SecEnemy.Present = true;
        tab.EnemyNote = "reading what the game ships them at…";
        tab.BeginEdit("", "", "", "");

        await FillEnemyRowsAsync(tab);
    }

    /// <summary>
    /// WHAT THE GAME SHIPS THEM AT, across every table in the group.
    ///
    /// OFF THE UI THREAD: one package load per table, and a group has up to 32. The spread —
    /// lowest to highest — is what a single number cannot say: a stat that reads "40 .. 260" is
    /// one the game already scales by tier and difficulty, and that is exactly why the edit is a
    /// multiplier rather than a value.
    /// </summary>
    private async Task FillEnemyRowsAsync(EnemyTab tab)
    {
        if (_provider is null) return;

        var provider = _provider;
        var group = tab.Group;

        var found = await Task.Run(() =>
        {
            var rows = new List<(string Row, double Low, double High)>();
            try
            {
                foreach (var set in group.Tables.GroupBy(x => x.Set))
                {
                    var perRow = new Dictionary<string, List<double>>(StringComparer.Ordinal);
                    foreach (var table in set)
                        foreach (var stat in EnemyStats.StatsOf(provider, table.Package))
                        {
                            if (!perRow.TryGetValue(stat.Row, out var vals))
                                perRow[stat.Row] = vals = new List<double>();
                            vals.Add(stat.Base);
                        }
                    foreach (var (row, vals) in perRow) rows.Add((row, vals.Min(), vals.Max()));
                }
            }
            catch (Exception) { /* a group that will not read is a group with no rows */ }
            return rows;
        });

        // READ BACK OUT OF THE PROJECT, not remembered from the last edit. An entry made in a
        // previous session is the ordinary case here — this is the panel a person comes back to.
        var entry = _mod?.EnemyStats.FirstOrDefault(
            e => e.Group.Equals(group.Leaf, StringComparison.OrdinalIgnoreCase));

        tab.SetEnemyRows(found.Select(f => new SheetTab.EnemyRow(
            f.Row, f.Low, f.High,
            entry is not null && entry.Multipliers.TryGetValue(f.Row, out var m) ? m : 1.0)));

        tab.EnemyNote = found.Count == 0
            ? "none of this group's tables would read, so there is nothing to change"
            : group.Users.Count == 0
                ? $"{found.Count} stat(s). Nothing in the game uses this group."
                : $"{found.Count} stat(s) across {group.Tables.Count} table(s). " +
                  $"Changing them changes {group.Users.Count} character(s): " +
                  string.Join(", ", group.Factions) + ".";

        Say($"{group.Display}: {found.Count} stat(s) across {group.Tables.Count} table(s)");
    }

    /// <summary>
    /// Put a multiplier on one stat.
    ///
    /// ×1 IS NOT A CHANGE, it is the absence of one, so it removes the entry rather than storing
    /// it — and an entry left holding nothing is removed too. Without that, a mod carries every
    /// group somebody opened, nudged and put back, and the build ships a copy of their tables.
    /// </summary>
    public void SetEnemyMultiplier(EnemyTab tab, SheetTab.EnemyRow row, string text)
    {
        if (_mod is null) { Say("open a mod first — a change has to be recorded somewhere"); return; }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var m))
        { Say($"{row.Shown}: '{text}' is not a number"); row.Took(row.Multiplier); return; }

        // THE SAME RANGE THE OLD STUDIO'S SLIDER HAD. Outside it the number is almost always a
        // typo — a stat at ×100 is not a rebalance, and a stat at 0 cannot be undone by the
        // author afterwards because the build has nothing to scale back up.
        if (m < 0.1 || m > 10)
        { Say($"{row.Shown}: ×{m:0.##} is outside 0.1 to 10"); row.Took(row.Multiplier); return; }

        m = Math.Round(m, 2);

        var entry = _mod.EnemyStats.FirstOrDefault(
            e => e.Group.Equals(tab.Group.Leaf, StringComparison.OrdinalIgnoreCase));

        if (Math.Abs(m - 1.0) < 0.0001)
        {
            entry?.Multipliers.Remove(row.Row);
            Say($"{tab.Group.Display}: {row.Shown} is the game's again");
        }
        else
        {
            if (entry is null)
            {
                entry = new ModEnemyStats { Group = tab.Group.Leaf };
                _mod.EnemyStats.Add(entry);
            }
            entry.Multipliers[row.Row] = m;
            Say($"{tab.Group.Display}: {row.Shown} ×{m:0.##}  →  {row.Ships} becomes " +
                $"{Math.Round(row.Low * m):0.##}" +
                (Math.Abs(row.Low - row.High) < 0.0001 ? "" : $" .. {Math.Round(row.High * m):0.##}"));
        }

        // AN ENTRY THAT ASKS FOR NOTHING MUST NOT SHIP. The build copies a group's tables the
        // moment the project names the group, so an empty entry is up to 32 duplicated assets
        // that change nothing and collide with anybody else touching them.
        if (entry is { Multipliers.Count: 0 }) _mod.EnemyStats.Remove(entry);

        _mod.Save();
        row.Took(m);
        AfterEditsChanged();
    }

    /// <summary>Put every stat in this group back to the game's.</summary>
    public void ClearEnemyGroup(EnemyTab tab)
    {
        if (_mod is null) return;

        var entry = _mod.EnemyStats.FirstOrDefault(
            e => e.Group.Equals(tab.Group.Leaf, StringComparison.OrdinalIgnoreCase));
        if (entry is null) { Say($"{tab.Group.Display}: this mod does not change it"); return; }

        var n = entry.Multipliers.Count;
        _mod.EnemyStats.Remove(entry);
        _mod.Save();

        foreach (var row in tab.EnemyRows) row.Took(1.0);
        Say($"{tab.Group.Display}: all {n} change(s) removed — they are the game's again");
        AfterEditsChanged();
    }
}

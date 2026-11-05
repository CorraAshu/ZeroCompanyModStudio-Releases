using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// AN ABILITY'S PROGRESSION: every tier, and every number behind it.
///
/// A GA_ ability wraps a GE_ effect and the numbers live in the effect, on its cooldown, or on the
/// ability itself — so a tier's damage and its cost are not in the same asset, and an author
/// wanting "tier four costs less" has three places to look and no way to know which.
/// <see cref="AbilityProgression"/> reads the lot as one table and writes each edit wherever that
/// number actually lives.
///
/// THE SHARED CELL IS THE WHOLE REASON THIS IS NOT A GRID OF TEXT BOXES. Several tiers routinely
/// name ONE number, so typing into it moves all of them. That is legitimate and it is not what
/// somebody editing "tier four" usually means, so the two are different acts and the panel asks
/// which rather than picking one.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// Read an ability's tiers and put them on the panel.
    ///
    /// OFF THE UI THREAD: it opens the ability, its effects and its cooldowns — one package load
    /// per cell in the worst case.
    /// </summary>
    public async Task OpenAbilityProgression(AbilityTab tab)
    {
        if (_provider is null) { Say("the game is not mounted yet"); return; }

        var provider = _provider;
        var package = tab.Ability.Package;

        // THE PANEL HAS TO BE IN EDIT MODE or none of this is on screen: the whole edit column
        // is gated on Editing, and filling a card behind it is filling something nobody sees.
        //
        // An ability has no name, no mesh, no picture and no slot, so every card but this one is
        // absent — the rule the part and weapon editors both follow.
        // ONLY PROGRESSION HERE. The other three turn themselves on in RefillEffects, once
        // the reading says the ability HAS numbers, references or containers — listing them in
        // ShowOnly made all three present before anything had been read, so an ability with no
        // containers still drew an empty TAG CONTAINERS card.
        tab.ShowOnly(tab.SecProg);
        tab.SecProg.Open = true;

        tab.BeginEdit(tab.Title, "", "", "");

        // THE READ SHEET FOLDS AWAY, as it does for a table and a modification: beside the editor it is a 330-pixel
        // column, and at about 1500 px it drew over the cards' headers (final review, triage 1). The header button
        // brings it back.
        tab.FoldSheetOnce();

        tab.ProgressionNote = "reading its tiers…";
        var family = await Task.Run(() => AbilityProgression.Read(provider, package));
        tab.Family = family;

        if (family.Why is { Length: > 0 } why || family.Tiers.Count == 0)
        {
            tab.SetProgression([]);
            tab.ProgressionNote = family.Why ?? "this ability has no tiers to show";
            Say($"{tab.Title}: {tab.ProgressionNote}");

            // AN ABILITY WITH NO TIERS STILL HAS NUMBERS. Returning here left the other three
            // cards empty on every effect that is not part of a progression family, which is
            // most of them.
            await FillEffectsAsync(tab);
            return;
        }

        FillProgression(tab, family);
        tab.ProgressionNote =
            $"{family.Tiers.Count} tier(s), {family.Columns.Count} value(s) each. A number shared " +
            "by several tiers can be moved everywhere at once, or this tier can be given its own " +
            "copy of the asset that holds it.";
        Say($"{tab.Title}: {family.Tiers.Count} tier(s), {family.Columns.Count} value(s) each");

        await FillEffectsAsync(tab);
    }

    private void FillProgression(AbilityTab tab, AbilityProgression.Family family)
    {
        if (_mod is null) { tab.SetProgression([]); return; }

        var rows = new List<SheetTab.ProgRow>();
        foreach (var tier in family.Tiers)
            foreach (var column in family.Columns)
            {
                if (!tier.Cells.TryGetValue(column.Key, out var cell)) continue;
                var showing = AbilityProgression.Current(_mod, tier, column);

                // ONCE THIS TIER HAS ITS OWN COPY IT IS NOT SHARING ANY MORE, and the family's
                // count cannot know that — it was read off the GAME before the copy existed. A
                // row that still says "shared with 5 others" beside a number only this tier has
                // is the panel describing the state before the edit it just made.
                var shared = showing.OwnCopy.Length > 0 ? 0 : cell.SharedWith;

                rows.Add(new SheetTab.ProgRow(
                    tier.Index, column.Key, column.Label,
                    AbilityProgression.RoleName(column.Role),
                    showing.Value, showing.Edited, shared));
            }

        tab.SetProgression(rows);
    }

    /// <summary>
    /// Write one cell.
    ///
    /// <paramref name="ownCopy"/> is the fork the shared cell forces: false moves the number
    /// everywhere that names it, true copies the asset into the mod and points THIS tier at the
    /// copy. Apply refuses the second where it cannot be done — a result effect is one entry in
    /// an array, and moving one tier onto its own copy of that means editing the array — and it
    /// says so rather than doing half of it.
    /// </summary>
    public void SetProgression(AbilityTab tab, SheetTab.ProgRow row, string text, bool ownCopy)
    {
        if (_mod is null) { Say("open a mod first — a change has to be recorded somewhere"); return; }
        if (tab.Family is not { } family) { Say("this ability's tiers have not been read"); return; }

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var v))
        { Say($"{row.Label}: '{text}' is not a number"); return; }

        var tier = family.Tiers.FirstOrDefault(t => t.Index == row.Tier);
        var column = family.Columns.FirstOrDefault(c => c.Key == row.Key);
        if (tier is null || column is null)
        { Say($"{row.Where}: that cell is not in this family any more"); return; }

        var done = AbilityProgression.Apply(_mod, tier, column, v, ownCopy, out var why);
        if (done is null) { Say($"{row.Where} {row.Label}: {why ?? "could not be changed"}"); return; }

        _mod.Save();
        Say($"{tab.Title}: {done.Summary}");
        FillProgression(tab, family);
        AfterEditsChanged();
    }

    /// <summary>Put one cell back to what the game says.</summary>
    public void ClearProgression(AbilityTab tab, SheetTab.ProgRow row)
    {
        if (_mod is null || tab.Family is not { } family) return;

        var tier = family.Tiers.FirstOrDefault(t => t.Index == row.Tier);
        var column = family.Columns.FirstOrDefault(c => c.Key == row.Key);
        if (tier is null || column is null) return;

        AbilityProgression.Clear(_mod, tier, column);
        _mod.Save();
        Say($"{tab.Title}: {row.Where} {row.Label} is the game's again");
        FillProgression(tab, family);
        AfterEditsChanged();
    }
}

using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// UTILITY ITEMS: duplicating one, and getting it into the Armoury.
///
/// THE ARMOURY DOES NOT SCAN FOR WHAT EXISTS. It sells from six tables naming 99 items by hard
/// import, so a duplicate that is perfect in every other way is offered to nobody and nothing
/// anywhere says why. That is exactly how the PROBE BETA COIL modification spent two days waiting
/// for a shop restock that could not have helped it.
///
/// So the copy editor carries a card that says, in as many words, that this item is on sale
/// nowhere — and the control that fixes it, with the item being displaced named before anything
/// happens.
///
/// A COOKED ARRAY CANNOT GROW, so taking a slot REPLACES what it sold rather than adding a row.
/// Core's own note explains why row growth does not help here even though it exists: a roster row
/// names its character by soft path and can be copied and re-pointed, while a shop row names its
/// item by HARD IMPORT and a copied row shares its template's import-map entry — so adding one
/// sells a second copy of whatever the template sold, and re-pointing it moves both.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// Duplicate a utility item into this mod: its shop entry, its gear kit and its actor.
    ///
    /// THREE COPIES, NOT FIVE, and that is Core's decision rather than this method's: the ability
    /// set and the ability stay pointing at the game's, because a re-skinned thermal detonator
    /// that does what a thermal detonator does is what somebody duplicating one usually wants —
    /// and copying the ability would silently detach it from any balance change the game makes.
    ///
    /// Until now this fell through to <c>ModPart.FromGamePart</c>, which refuses anything that is
    /// not a wearable outfit slot — so a utility item could not be duplicated at all, and the
    /// shop card below had nothing to be about.
    /// </summary>
    public bool DuplicateUtility(UtilityIndex.Utility utility)
    {
        if (_mod is null) { Say("open a mod first — a duplicate has to go somewhere"); return false; }
        if (_provider is null) { Say("the game is not mounted yet"); return false; }

        var id = UtilityAuthor.IdFor(_mod, utility);
        var json = UtilityAuthor.Duplicate(_provider, utility, _mod, id, out var why);
        if (json is null)
        { Say($"{utility.Name}: {why ?? "could not duplicate it"}"); return false; }

        // THE SAME SHAPE WeaponAuthor.MakePlayable RETURNS — a `copies` array — so it is added
        // the same way, through ModCopy.From rather than by building the objects here.
        var made = new List<ModCopy>();
        foreach (var c in json["copies"]?.AsArray() ?? [])
            if (c is System.Text.Json.Nodes.JsonObject o)
            {
                var copy = ModCopy.From(o);
                _mod.Copies.Add(copy);
                made.Add(copy);
            }

        if (made.Count == 0) { Say($"{utility.Name}: the duplicate produced nothing"); return false; }

        _mod.Save();
        AfterEditsChanged();

        Say($"duplicated {utility.Name} as {id} — its own shop item, gear kit and actor. " +
            "It is NOT on sale yet: the Armoury sells from a fixed list.");

        // OPENED ON THE SHOP ITEM, not on whichever copy happened to be last. The item is the one
        // the author has anything to do with — the kit and the actor are plumbing — and it is the
        // one the "sell it" card hangs off.
        var item = made.FirstOrDefault(
                       c => c.Id.StartsWith(UtilityIndex.ItemPrefix, StringComparison.Ordinal))
                   ?? made[0];
        _ = EditGenericCopy(item);
        return true;
    }

    /// <summary>
    /// EVERY UTILITY SLOT IN THE GAME'S SHOP TABLES, read once and kept.
    ///
    /// Filtered to the UTILITY ones by what is IN each slot rather than by the table's name,
    /// because the name is a convention and the contents are the fact — the same six tables also
    /// stock weapon modifications, and a grenade in a modification slot turns up in the Black
    /// Market's mod list.
    /// </summary>
    public async Task<List<UtilityAuthor.Vacancy>> ShopSlotsAsync()
    {
        if (_slots is not null) return _slots;
        if (_provider is null) return [];

        var provider = _provider;
        _slots = await Task.Run(() =>
        {
            try { return UtilityAuthor.Vacancies(provider); }
            catch (Exception) { return new List<UtilityAuthor.Vacancy>(); }
        });

        Say($"the Armoury's utility shelves: {_slots.Count} slot(s)");
        return _slots;
    }

    private List<UtilityAuthor.Vacancy>? _slots;

    /// <summary>
    /// Fill the shop card for one utility copy.
    ///
    /// WHERE IT SELLS IS READ BACK OUT OF THE PROJECT, by looking for a re-point whose TARGET is
    /// this item's package — not remembered from the press that put it there. A card that
    /// remembered would be right until the project was reopened.
    /// </summary>
    private async Task FillSellAsync(CopyTab tab, ModCopy copy)
    {
        if (_mod is null) return;

        tab.SecSell.Present = true;

        var package = UtilityAuthor.FolderFor(_mod) + copy.Id;

        var on = _mod.Overrides
            .SelectMany(o => o.RepointImports
                .Where(kv => string.Equals(kv.Value, package, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (Table: o.Target, Was: kv.Key)))
            .ToList();

        if (on.Count > 0)
        {
            tab.SetOnSale(
                string.Join("   ·   ", on.Select(x => $"in {Leaf(x.Table)}, where {Leaf(x.Was)} sold")),
                "The item it replaced is not offered while this mod is installed. Taking it off " +
                "sale gives that slot back.");
            return;
        }

        tab.SetOnSale("", "");
        tab.SellNote = "reading the Armoury's shelves…";

        var slots = await ShopSlotsAsync();
        tab.SetShopSlots(slots.Select(v => new SheetTab.ShopSlot(v.Describe, v.ItemLeaf, v.Tier)));

        tab.SellNote = slots.Count == 0
            ? "No utility shop slots were found in this game, so there is nowhere to put it."
            : "The Armoury sells from a fixed list, so nothing offers this yet. Whatever the slot " +
              "you choose sold is not offered while your mod is installed.";
    }

    /// <summary>
    /// Take a slot.
    ///
    /// THE ITEM BEING DISPLACED IS NAMED BEFORE AND AFTER — the row says it, and the log says it
    /// again with the tier, because this is the one edit here that takes something out of
    /// somebody's game rather than adding to it.
    /// </summary>
    public async Task PutOnSale(CopyTab tab)
    {
        if (_mod is null) { Say("open a mod first"); return; }
        if (tab.SellSlot is not { } chosen) { Say("choose a slot first"); return; }

        var slots = await ShopSlotsAsync();
        var slot = slots.FirstOrDefault(v => v.Describe == chosen.Describe);
        if (slot is null) { Say($"{chosen.Describe}: that slot is not in the game any more"); return; }

        var copy = tab.Copy;
        var package = UtilityAuthor.FolderFor(_mod) + copy.Id;

        var ov = UtilityAuthor.PutOnSale(_mod, slot, package, out var displaced, out var why);
        if (ov is null) { Say($"{copy.Id}: {why ?? "could not take that slot"}"); return; }

        _mod.Save();
        Say($"{(copy.Display.Length > 0 ? copy.Display : copy.Id)} now sells in {slot.Tier}, " +
            $"in place of {displaced}.");
        await FillSellAsync(tab, copy);
        AfterEditsChanged();
    }

    /// <summary>
    /// Give the slot back.
    ///
    /// THE RE-POINT GOES, AND SO DOES THE OVERRIDE IF IT HELD NOTHING ELSE. An override left
    /// saying nothing must not ship: it is a byte-for-byte rewrite of a game package that changes
    /// nothing and collides with every other mod touching that table. The same rule the part
    /// editor already follows.
    /// </summary>
    public async Task TakeOffSale(CopyTab tab)
    {
        if (_mod is null) return;

        var copy = tab.Copy;
        var package = UtilityAuthor.FolderFor(_mod) + copy.Id;
        var gone = new List<string>();

        foreach (var ov in _mod.Overrides.ToList())
        {
            foreach (var kv in ov.RepointImports
                         .Where(kv => string.Equals(kv.Value, package, StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                ov.RepointImports.Remove(kv.Key);
                gone.Add($"{Leaf(kv.Key)}'s slot in {Leaf(ov.Target)}");
            }

            if (!ov.HasEdit && ov.Icon.Length == 0) _mod.Overrides.Remove(ov);
        }

        if (gone.Count == 0) { Say($"{copy.Id}: it is not on sale anywhere"); return; }

        _mod.Save();
        Say($"{copy.Id}: off sale — gave back {string.Join(" and ", gone)}");
        await FillSellAsync(tab, copy);
        AfterEditsChanged();
    }
}

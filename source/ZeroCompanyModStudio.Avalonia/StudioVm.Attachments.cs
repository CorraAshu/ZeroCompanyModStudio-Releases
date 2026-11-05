using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// WHAT ATTACHMENTS DO: the 64 weapon modifications (barrels, cells, scopes, stabilizers), their
/// numbers, their names, their rarity and their prices.
///
/// THE FORMAT IS THE 1.6.1 ONE, and it is not new. The public ImGui Studio's screen of this name
/// (ModAttachments.cs) put every change in an ordinary <see cref="ModOverride"/>, and this writes
/// exactly the same records so a project that already carries them opens with the same values and
/// builds unchanged:
///
///   the ABILITY (GA_Modification_...) holds what it does, on an override of its own package:
///     a number it carries     Numbers   NumberChange(export, "GameplayEffectModifer", was, now)
///     ... and its display copy Numbers  NumberChange(export, "Effect UI Data.EffectValue", copy, now)
///                                       (only where the copy agrees with the stat)
///     a number it inherits    AddScalars ScalarAdd(export, "GameplayEffectModifer", now)
///     a second number         Numbers   NumberChange(export, its path, was, now)
///
///   the SHOP ITEM (Modification_...) holds name, text, rarity and price, on a second override:
///     DisplayName, Description
///     rarity                  RemoveTags/AddTags, TagsProperty = "ItemTags"
///     a price                 Numbers   NumberChange(shopExport, "BuyCost" | "SellValue", was, now)
///
/// A percentage is stored as the fraction the game stores (0.06), and TYPED as a percentage (6),
/// because a box that showed 0.06 beside a description saying "6% chance" invites 15 meaning
/// fifteen percent and a shipped 1500% one.
///
/// Nothing here uses a modification up. This edits the 64 in place; whether a mod can ADD a
/// sixty-fifth is a separate, open question.
/// </summary>
public sealed partial class StudioVm
{
    private IReadOnlyList<Modifications.Entry> ModificationList()
        => _provider is null ? Array.Empty<Modifications.Entry>() : Sections.ModificationsOf(_provider);

    private ModOverride? ShopOverride(Modifications.Entry e)
        => e.Shop is null ? null : FindOverride(e.Shop.Package);

    /// <summary>A number as the BOX shows it: a percentage is typed as a percentage.</summary>
    private static string BoxText(double value, bool percent)
        => NumberText.Show(percent ? value * 100 : value);

    /// <summary>
    /// A price in credits, printed the way the box that takes it reads: digits and nothing else.
    ///
    /// NOT <c>:N0</c>, which groups by the PC's own convention (2,000 here, 2.000 on a German
    /// machine), and NOT the invariant "N0" either: that prints 2,000 on every PC, and the price
    /// box refuses 2,000 because a comma followed by three digits could be a decimal point. What
    /// is printed is what the box accepts.
    /// </summary>
    public static string Credits(double value) => NumberText.Show(value, "0");

    /// <summary>
    /// What a tier that carries no number of its own is actually using: the nearest lower tier of
    /// the same style that does. Derived rather than looked up, because the inheritance is a
    /// Blueprint parent chain and nothing in the cooked data spells it out.
    /// </summary>
    private double Inherits(Modifications.Entry e)
        => ModificationList()
            .Where(x => x.Slot == e.Slot && x.Style == e.Style && x.Tier < e.Tier && x.Stat is not null)
            .OrderByDescending(x => x.Tier)
            .Select(x => x.Stat!.Value)
            .FirstOrDefault();

    /// <summary>
    /// The number the mod asks for at one property of the ABILITY, or null for nothing.
    /// Two shapes, because two edits: a property the asset carries is a NUMBER change and one it
    /// inherits is a property ADD. The rows only want the value on screen.
    /// </summary>
    private static double? AskedFor(ModOverride? ov, Modifications.Entry e, string path)
    {
        if (ov is null) return null;
        foreach (var n in ov.Numbers)
            if (n.Row == e.Export && n.Field == path) return n.Now;
        foreach (var s in ov.AddScalars)
            if (s.Export == e.Export && s.Name == path) return s.Value;
        return null;
    }

    /// <summary>The four rarities the game ships, in the order it uses them -- one per tier.</summary>
    private IEnumerable<string> RaritiesOf()
        => ModificationList().Where(x => x.Shop is { Rarity.Length: > 0 })
                             .GroupBy(x => x.Shop!.Rarity, StringComparer.Ordinal)
                             // BY THE TIER THEY SIT ON, not alphabetically: the ladder is Common,
                             // Uncommon, Rare, Unique, and by name it reads Common, Rare, Uncommon.
                             .OrderBy(g => g.Min(x => x.Tier))
                             .Select(g => g.Key);

    // ------------------------------------------------------------------ opening it

    /// <summary>
    /// Open the editor on a modification: the NUMBERS card and the Black Market card, in edit mode.
    /// Nothing is written into the project until a box is left with a different value in it.
    /// </summary>
    public void OpenModificationEditor(ModificationTab tab)
    {
        tab.ShowOnly(tab.SecEffects, tab.SecShop);
        tab.BeginEdit("", "", "", "");
        tab.FoldSheetOnce();
        FillModification(tab);
        Say($"editing {tab.Title}: type over a number, or its name, price or rarity; nothing is written until you do");
    }

    /// <summary>Rebuild both cards off the game's numbers and the mod as it stands.</summary>
    public void FillModification(ModificationTab tab)
    {
        var e = tab.Entry;
        var ability = FindOverride(e.Package);
        var shopOv = ShopOverride(e);

        // WHILE THIS IS FILLED a control's own change events mean nothing: a ComboBox raises
        // SelectionChanged when its items are replaced, and that is not a person choosing one.
        tab.BeginFill();
        try
        {
            tab.Feedback("");
            tab.ShopSay("");

            // ---------------------------------------------------------------- the numbers
            var rows = new List<SheetTab.EffectRow> { StatRowFor(e, ability) };
            rows.AddRange(e.Others.Select(o => OtherRowFor(e, o, ability)));
            tab.EffectsSay(rows, [], []);
            tab.SecEffects.Present = true;

            var tiers = ModificationList().Where(x => x.Slot == e.Slot && x.Style == e.Style)
                                          .OrderBy(x => x.Tier)
                                          .Select(x => $"T{x.Tier} " + (x.Stat is null
                                              ? "inherited" : Sections.StatText(x.Stat.Value, x.Percent)));
            tab.EffectsNote =
                $"The tiers of {e.Readable}: {string.Join("  ·  ", tiers)}." +
                (e.Shown is not null
                    ? " Its second copy (the number its menus keep) moves with it."
                    : "");
            tab.EffectsHelp =
                "'Now' is what the game ships. Anything you leave alone is not shipped. Type a number " +
                "over a box and leave it to record it" +
                (e.Percent ? "; this one is a percentage, so 6 means 6%." : ".");
            tab.EffectsElsewhere = e.CopyDisagrees && e.Copy is not null && e.Stat is not null
                ? $"The game already shows {Sections.StatText(e.Copy.Value, e.Percent)} in the copy its menus keep, " +
                  $"not {Sections.StatText(e.Stat.Value, e.Percent)}. That is in the game's own files, not " +
                  "something your mod did: your change moves the number that DOES something, and this " +
                  "copy is left as the game shipped it."
                : "";

            // ---------------------------------------------------------------- the shop
            if (e.Shop is not { } shop)
            {
                tab.ShopProblem = "No shop item is paired with this one, so it cannot be renamed, repriced or re-rated here.";
                tab.SecShop.Present = false;
            }
            else
            {
                tab.ShopProblem = "";
                tab.SecShop.Present = true;
                tab.ShopNameGame = shop.Name;
                tab.ShopTextGame = Modifications.Plain(shop.Description);
                tab.ShopName = shopOv?.DisplayName ?? "";
                tab.ShopText = shopOv?.Description ?? "";

                tab.ShopRarities.Clear();
                if (shop.Rarity.Length > 0)
                    foreach (var t in RaritiesOf()) tab.ShopRarities.Add(ModificationTab.Rarity(t));
                var rareNow = shopOv?.AddTags.FirstOrDefault(
                                  t => t.Contains(Modifications.RarityTag, StringComparison.Ordinal))
                              ?? shop.Rarity;
                tab.ShopRarity = shop.Rarity.Length == 0 ? null : ModificationTab.Rarity(rareNow);
                tab.ShopRarityGame = shop.Rarity.Length == 0 ? "no rarity"
                                   : "the game's own: " + ModificationTab.Rarity(shop.Rarity);
                tab.ShopRarityMine = shop.Rarity.Length > 0 && rareNow != shop.Rarity;

                tab.ShopRows.Clear();
                tab.ShopRows.Add(PriceRowFor(e, "BuyCost", shop.Buy, shopOv));
                tab.ShopRows.Add(PriceRowFor(e, "SellValue", shop.Sell, shopOv));
                tab.ShopBeyond = HeldElsewhere(shopOv, e, shop: true) is { Count: > 0 } extra
                    ? "This override also carries " + Plainly(extra) +
                      ", which this card does not show. It is kept."
                    : "";
            }
        }
        finally { tab.EndFill(); }

        MarkModification(tab);
    }

    /// <summary>
    /// The counts on the two cards, the "put it back" button and the line under the title, off the
    /// project as it stands. Called after every write, because each of those is a function of it.
    /// </summary>
    private void MarkModification(ModificationTab tab)
    {
        var e = tab.Entry;
        var shopOv = ShopOverride(e);

        var numbers = tab.EffectRows.Count(r => r.Mine);
        tab.SecEffects.Count = numbers == 0 ? "" : $"{numbers} changed";

        var shop = (shopOv?.DisplayName.Length > 0 ? 1 : 0) + (shopOv?.Description.Length > 0 ? 1 : 0) +
                   (tab.ShopRarityMine ? 1 : 0) + tab.ShopRows.Count(r => r.Mine);
        tab.SecShop.Count = shop == 0 ? "" : $"{shop} changed";

        // THE "PUT EVERYTHING BACK" BUTTON IS FOR WHAT THIS SCREEN RECORDED. An override that holds
        // only somebody else's edit (a class pointer from the Abilities section) is the mod
        // touching this modification, and the note beside the title says so, but there is nothing
        // here for the button to take off.
        tab.Altered = ScreenRecords(e) > 0;
        NoteModification(tab);
    }

    /// <summary>What the mod does to this modification, stated beside it.</summary>
    private void NoteModification(ModificationTab tab)
    {
        var e = tab.Entry;
        var ability = FindOverride(e.Package);
        var shopOv = ShopOverride(e);
        if (ability is null && shopOv is null) { tab.ModNote = ""; return; }

        var bits = new List<string>();
        if (AskedFor(ability, e, Modifications.StatProperty) is { } stat)
            bits.Add(e.Stat is null
                ? $"gives it a number of its own, {Sections.StatText(stat, e.Percent)}"
                : $"changes its number from {Sections.StatText(e.Stat.Value, e.Percent)} to {Sections.StatText(stat, e.Percent)}");
        foreach (var o in e.Others)
            if (AskedFor(ability, e, o.Path) is { } v)
                bits.Add($"changes {o.Label} from {NumberText.Show(o.Value)} to {NumberText.Show(v)}");
        if (shopOv?.DisplayName.Length > 0) bits.Add($"renames it to “{shopOv.DisplayName}”");
        if (shopOv?.Description.Length > 0) bits.Add("changes what it says");
        if (shopOv?.AddTags.Any(t => t.Contains(Modifications.RarityTag, StringComparison.Ordinal)) == true)
            bits.Add("changes how rare it is");
        if (shopOv is not null && e.Shop is { } shop)
        {
            if (shopOv.Numbers.FirstOrDefault(n => n.Row == shop.Export && n.Field == "BuyCost") is { } buy)
                bits.Add($"prices it at {Credits(buy.Now)} credits (the game's {Credits(shop.Buy)})");
            if (shopOv.Numbers.FirstOrDefault(n => n.Row == shop.Export && n.Field == "SellValue") is { } sell)
                bits.Add($"buys it back at {Credits(sell.Now)} credits (the game's {Credits(shop.Sell)})");
        }

        tab.ModNote = bits.Count == 0
            ? $"{ModTitle} overrides this modification"
            : $"{ModTitle} " + string.Join(", ", bits);
    }

    // ------------------------------------------------------------------ the rows

    private SheetTab.EffectRow StatRowFor(Modifications.Entry e, ModOverride? ability)
    {
        var asked = AskedFor(ability, e, Modifications.StatProperty);
        var where = $"{e.Export}.{Modifications.StatProperty}";

        // A TIER THAT CARRIES NO NUMBER is not given a box. Four of the sixty-four take it from the
        // tier below and raise only their cap, and a box pre-filled with a value that is not in the
        // asset invites an edit whose "was" is a guess. Giving it one ADDS the property rather than
        // writing over it, which is a larger edit than the rest, so it is a button that says so.
        if (e.Stat is null && asked is null)
            return new SheetTab.EffectRow
            {
                Export = e.Export,
                Path = Modifications.StatProperty,
                Label = "Its number",
                Detail = "this tier carries no number of its own: it uses the tier below's, " +
                         Sections.StatText(Inherits(e), e.Percent) + ", and raises only its cap",
                Where = where,
                Now = "inherited",
                Stored = false,
                Value = "",
                ActionLabel = "Give it one",
                Note = "Nothing is wrong with it. Giving it one ADDS the property rather than writing over " +
                       "it, which is a larger edit than the others. It starts at the value it is using now.",
            };

        var value = asked ?? e.Stat!.Value;
        return new SheetTab.EffectRow
        {
            Export = e.Export,
            Path = Modifications.StatProperty,
            Label = "Its number",
            Detail = e.Percent ? "a percentage: type 6 for 6%, not 0.06" : "a plain number",
            Where = where,
            Now = e.Stat is null ? "inherited" : Sections.StatText(e.Stat.Value, e.Percent),
            Stored = true,
            Adds = e.Stat is null,
            Value = BoxText(value, e.Percent),
            Mine = asked is not null,
            State = e.Stat is null ? "adds the property" : "",
        };
    }

    private static SheetTab.EffectRow OtherRowFor(Modifications.Entry e, Modifications.Number o, ModOverride? ability)
    {
        var asked = AskedFor(ability, e, o.Path);
        return new SheetTab.EffectRow
        {
            Export = e.Export,
            Path = o.Path,
            // ITS PROPERTY NAME IS THE LABEL: the game gives these no display name, and inventing
            // one would be this tool putting words in the asset's mouth.
            Label = o.Label,
            Detail = "a second number it carries; the description's {effectDescription_2}",
            Where = $"{e.Export}.{o.Path}",
            Now = NumberText.Show(o.Value),
            Stored = true,
            Value = NumberText.Show(asked ?? o.Value),
            Mine = asked is not null,
        };
    }

    private static SheetTab.EffectRow PriceRowFor(Modifications.Entry e, string property, int theirs, ModOverride? shopOv)
    {
        var asked = shopOv?.Numbers.FirstOrDefault(n => n.Row == e.Shop!.Export && n.Field == property)?.Now;
        return new SheetTab.EffectRow
        {
            Export = e.Shop!.Export,
            Path = property,
            Label = property == "BuyCost" ? "Costs" : "Sells for",
            Detail = property == "BuyCost" ? "credits, to buy it" : "credits, when it is sold back",
            Where = $"{e.Shop.Export}.{property}",
            Now = theirs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Stored = true,
            Value = NumberText.Show(asked ?? theirs),
            Mine = asked is not null,
        };
    }

    /// <summary>
    /// What an override holds that THIS SCREEN did not put there, in plain words.
    ///
    /// The screen's own records are the ones it can name and take off again: on the ability the
    /// stat, its copy, a second number and a "Give it one"; on the shop item the name, the
    /// sentence, the rarity tags and the two prices. Anything else is somebody else's -- the
    /// Abilities section's class pointers and tag gates, a Data Explorer property, a re-point --
    /// and is kept when this screen's are put back.
    /// </summary>
    private static List<string> HeldElsewhere(ModOverride? ov, Modifications.Entry e, bool shop)
    {
        var held = new List<string>();
        if (ov is null) return held;

        static string Many(int n, string one, string many) => n == 1 ? one : $"{n} {many}";

        var numbers = ov.Numbers.Count(n => !IsMyAbilityNumber(e, n) && !IsMyPrice(e, n));
        if (numbers > 0) held.Add(Many(numbers, "a number change", "number changes"));
        var added = ov.AddScalars.Count(s => !IsMyAbilityScalar(e, s));
        if (added > 0) held.Add(Many(added, "an added number", "added numbers"));
        if (ov.AddClasses.Count > 0) held.Add(Many(ov.AddClasses.Count, "a class pointer", "class pointers"));
        var gates = ov.AddTagContainers.Count;
        if (gates > 0) held.Add(Many(gates, "a tag gate", "tag gates"));
        var tags = ov.AddTags.Count(t => !IsRarityTag(t)) + ov.RemoveTags.Count(t => !IsRarityTag(t));
        if (tags > 0) held.Add(Many(tags, "a tag edit", "tag edits"));
        // A name and a sentence are this screen's on the shop item and somebody else's anywhere else.
        if (!shop && ov.DisplayName.Length > 0) held.Add("a name");
        if (!shop && ov.Description.Length > 0) held.Add("a sentence");
        if (ov.Repoints.Count > 0) held.Add($"{ov.Repoints.Count} re-point(s)");
        if (ov.RenameNames.Count > 0) held.Add($"{ov.RenameNames.Count} renamed name(s)");
        if (ov.AddOptions.Count > 0) held.Add($"{ov.AddOptions.Count} added palette entr(ies)");
        if (ov.HasFlag) held.Add("a cleared flag");
        if (ov.HasAddProperty) held.Add("an added property");
        if (!string.IsNullOrWhiteSpace(ov.MeshFile)) held.Add("an imported mesh");
        if (!string.IsNullOrWhiteSpace(ov.Image)) held.Add("an image");
        if (ov.Icon.Length > 0) held.Add("an icon");
        return held;
    }

    /// <summary>"a, b and c".</summary>
    private static string Plainly(IReadOnlyList<string> items)
        => items.Count <= 1 ? string.Join("", items)
                            : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static bool IsRarityTag(string tag)
        => tag.Contains(Modifications.RarityTag, StringComparison.Ordinal);

    /// <summary>One of the numbers THIS SCREEN writes on the ability: the stat, its second copy, or a second number.</summary>
    private static bool IsMyAbilityNumber(Modifications.Entry e, ModOverride.NumberChange n)
        => n.Row == e.Export &&
           (n.Field == Modifications.StatProperty || n.Field == Modifications.ShownProperty ||
            e.Others.Any(o => o.Path == n.Field));

    /// <summary>The property "Give it one" adds to a tier that inherits its number.</summary>
    private static bool IsMyAbilityScalar(Modifications.Entry e, ModOverride.ScalarAdd s)
        => s.Export == e.Export && s.Name == Modifications.StatProperty;

    /// <summary>One of the two prices THIS SCREEN writes on the shop item.</summary>
    private static bool IsMyPrice(Modifications.Entry e, ModOverride.NumberChange n)
        => e.Shop is { } shop && n.Row == shop.Export && n.Field is "BuyCost" or "SellValue";

    /// <summary>How many records this screen has for one modification, across its two overrides.</summary>
    private int ScreenRecords(Modifications.Entry e)
    {
        var count = 0;
        if (FindOverride(e.Package) is { } a)
            count += a.Numbers.Count(n => IsMyAbilityNumber(e, n)) +
                     a.AddScalars.Count(s => IsMyAbilityScalar(e, s));
        if (ShopOverride(e) is { } s)
            count += (s.DisplayName.Length > 0 ? 1 : 0) + (s.Description.Length > 0 ? 1 : 0) +
                     (s.AddTags.Concat(s.RemoveTags).Any(IsRarityTag) ? 1 : 0) +
                     s.Numbers.Count(n => IsMyPrice(e, n));
        return count;
    }

    // ------------------------------------------------------------------ writing a number

    /// <summary>
    /// A number, ON LEAVING THE BOX: the ability's own, a second one it carries, or a price.
    ///
    /// THE ROW IS UPDATED IN PLACE, not rebuilt: a rebuild replaces every box on the card, and a
    /// box replaced while the next one is taking focus is a Tab key that lands nowhere.
    /// AND AN UNCHANGED BOX RECORDS NOTHING: a percentage shown as 6 is the game's 0.06, and one
    /// that did not survive being printed would otherwise be saved as an edit nobody made.
    /// </summary>
    public void SetModificationNumber(ModificationTab tab, SheetTab.EffectRow row)
    {
        if (_mod is null) return;
        if (tab.ShopRows.Contains(row)) { SetModificationPrice(tab, row); return; }

        var e = tab.Entry;
        var isStat = row.Path == Modifications.StatProperty;
        var other = isStat ? null : e.Others.FirstOrDefault(o => o.Path == row.Path);
        if (!isStat && other is null) return;
        var percent = isStat && e.Percent;

        var existing = AskedFor(FindOverride(e.Package), e, row.Path);
        var game = isStat ? e.Stat?.Value : other!.Value;
        var baseline = existing ?? game;
        // No number to compare against: a tier with no number of its own, and not given one. It has
        // no box to type in, so there is nothing to record.
        if (baseline is null) { row.Value = ""; return; }

        var shown = BoxText(baseline.Value, percent);
        var typed = (row.Value ?? "").Trim();
        if (typed == shown) { row.Value = shown; return; }

        tab.Feedback("");
        if (!NumberText.TryParse(typed, out var parsed, out var why))
        {
            var line = $"{row.Label}: {why} -- the box is back to {shown}";
            SayBad($"{tab.Title}: {line}");
            tab.Feedback(line, bad: true);
            row.Value = shown;
            return;
        }

        // STORED AS THE FRACTION THE GAME STORES. A percentage typed to six places is a fraction of
        // eight, so it is rounded there to take the division's noise off (0.07300000000000001).
        var now = percent ? Math.Round(parsed / 100.0, 10) : parsed;
        var was = game;

        if (isStat) SetStat(e, now); else SetOther(e, other!, now);

        var after = AskedFor(FindOverride(e.Package), e, row.Path);
        row.Mine = after is not null;
        row.Value = BoxText(after ?? game ?? now, percent);

        var label = isStat ? "its number" : row.Label;
        Say(after is null
            ? $"{tab.Title}: {label} is the game's own again"
            : $"{tab.Title}: {label} = {Sections.StatText(now, percent)}" +
              (was is null ? " (the property is added)" : $", was {Sections.StatText(was.Value, percent)}"));
        MarkModification(tab);
        AfterEditsChanged();
    }

    /// <summary>Put the game's own number back, and drop the entry.</summary>
    public void ResetModificationNumber(ModificationTab tab, SheetTab.EffectRow row)
    {
        if (_mod is null) return;
        var e = tab.Entry;
        tab.Feedback("");
        tab.ShopSay("");

        if (tab.ShopRows.Contains(row))
        {
            if (e.Shop is not { } shop) return;
            SetShop(e, ov => ov.Numbers.RemoveAll(n => n.Row == shop.Export && n.Field == row.Path));
            row.Mine = false;
            row.Value = row.Now;
            Say($"{tab.Title}: {row.Label} is the game's own again");
        }
        else if (row.Path == Modifications.StatProperty)
        {
            SetStat(e, double.NaN);
            if (e.Stat is null)
            {
                // A TIER WITH NO NUMBER OF ITS OWN goes back to having no box at all.
                var at = tab.EffectRows.IndexOf(row);
                if (at >= 0) tab.EffectRows[at] = StatRowFor(e, FindOverride(e.Package));
            }
            else
            {
                row.Mine = false;
                row.Value = BoxText(e.Stat.Value, e.Percent);
            }
            Say($"{tab.Title}: its number is the game's own again");
        }
        else if (e.Others.FirstOrDefault(o => o.Path == row.Path) is { } other)
        {
            SetOther(e, other, other.Value);
            row.Mine = false;
            row.Value = NumberText.Show(other.Value);
            Say($"{tab.Title}: {row.Label} is the game's own again");
        }

        MarkModification(tab);
        AfterEditsChanged();
    }

    /// <summary>"Give it one": a tier that inherits its number gets one of its own, starting at the value in use.</summary>
    public void GiveModificationNumber(ModificationTab tab, SheetTab.EffectRow row)
    {
        if (_mod is null) { Say("open a mod first — an edit has to be recorded somewhere"); return; }
        var e = tab.Entry;
        if (row.Path != Modifications.StatProperty || e.Stat is not null) return;

        var start = Inherits(e);
        SetStat(e, start);
        var at = tab.EffectRows.IndexOf(row);
        if (at >= 0) tab.EffectRows[at] = StatRowFor(e, FindOverride(e.Package));

        Say($"{tab.Title}: gave it a number of its own, {Sections.StatText(start, e.Percent)} " +
            "(the property is added, starting at the value it was using)");
        MarkModification(tab);
        AfterEditsChanged();
    }

    /// <summary>
    /// Record, replace or drop the change to one modification's stat.
    ///
    /// <paramref name="now"/> equal to the game's own value REMOVES the request rather than storing
    /// "2 becomes 2": a no-op is still shipped, still checked against the asset, and one more thing
    /// that can fail a build for nothing. NaN means the same, and is what reset passes for a
    /// modification that has no value of its own to go back to.
    ///
    /// THE SECOND COPY MOVES WITH IT. Most tiers keep the same number at
    /// <c>Effect UI Data.EffectValue</c>; where the two agree in the shipped asset they are
    /// written together, so a modification cannot end up saying one number and doing another.
    /// </summary>
    private void SetStat(Modifications.Entry e, double now)
    {
        var back = double.IsNaN(now) || (e.Stat is not null && Modifications.Same(now, e.Stat.Value));
        var ov = EditedAbility(e, back);
        if (ov is null) return;

        ov.Numbers.RemoveAll(n => n.Row == e.Export &&
                                  (n.Field == Modifications.StatProperty ||
                                   n.Field == Modifications.ShownProperty));
        ov.AddScalars.RemoveAll(s => s.Export == e.Export && s.Name == Modifications.StatProperty);

        if (!back)
        {
            if (e.Stat is null)
                ov.AddScalars.Add(new ModOverride.ScalarAdd(e.Export, Modifications.StatProperty, now));
            else
            {
                ov.Numbers.Add(new ModOverride.NumberChange(
                    e.Export, Modifications.StatProperty, e.Stat.Value, now));
                if (e.Shown is not null)
                    ov.Numbers.Add(new ModOverride.NumberChange(
                        e.Export, Modifications.ShownProperty, e.Shown.Value, now));
            }
        }

        Done(ov);
    }

    /// <summary>The same, for a second number -- which three of the sixteen styles carry.</summary>
    private void SetOther(Modifications.Entry e, Modifications.Number other, double now)
    {
        var back = Modifications.Same(now, other.Value);
        var ov = EditedAbility(e, back);
        if (ov is null) return;

        ov.Numbers.RemoveAll(n => n.Row == e.Export && n.Field == other.Path);
        if (!back)
            ov.Numbers.Add(new ModOverride.NumberChange(e.Export, other.Path, other.Value, now));

        Done(ov);
    }

    /// <summary>The override this modification's ability changes go in: found, made when one is needed, or not made at all for "put it back".</summary>
    private ModOverride? EditedAbility(Modifications.Entry e, bool back)
    {
        if (FindOverride(e.Package) is { } found) return found;
        return back ? null : MakeOverride(e.Package);
    }

    /// <summary>
    /// After a change: an override left holding nothing is dropped, and the project is saved.
    ///
    /// Not tidiness. The builder refuses an override that asks for no change, so one left behind by
    /// a reset would fail the next build with a complaint about an asset the author had just
    /// finished putting back.
    /// </summary>
    private void Done(ModOverride ov)
    {
        if (!ov.HasEdit && string.IsNullOrWhiteSpace(ov.Image) &&
            string.IsNullOrWhiteSpace(ov.MeshFile) && ov.Icon.Length == 0)
            _mod!.Overrides.Remove(ov);
        _mod!.Save();
    }

    // ------------------------------------------------------------------ the shop item

    /// <summary>Apply a change to the shop override, making one if needed and dropping it once it asks for nothing.</summary>
    private void SetShop(Modifications.Entry e, Action<ModOverride> change)
    {
        if (e.Shop is null || _mod is null) return;
        var ov = MakeOverride(e.Shop.Package);
        change(ov);
        Done(ov);
    }

    /// <summary>
    /// What to store for a text field: nothing, when it is empty or is what the game already says.
    /// Typing the game's own name back in is how somebody undoes a rename, and storing it would ship
    /// an override that renames a thing to what it is already called.
    /// </summary>
    private static string Wanted(string typed, string? theirs)
        => typed.Trim().Length == 0 || typed.Trim() == (theirs ?? "").Trim() ? "" : typed.Trim();

    /// <summary>The name or the sentence, ON LEAVING THE BOX.</summary>
    public void SetModificationText(ModificationTab tab, bool name)
    {
        if (_mod is null || tab.Entry.Shop is not { } shop) return;
        var e = tab.Entry;
        var ov = ShopOverride(e);

        var stored = name
            ? Wanted(tab.ShopName, shop.Name)
            : Wanted(tab.ShopText, Modifications.Plain(shop.Description));

        // NOTHING CHANGED means no override is made and the project is not written. COMPARED
        // TRIMMED TO TRIMMED: a project from elsewhere can hold a name with a space on its end, the
        // box shows it as stored, and leaving the box would otherwise rewrite it trimmed -- the
        // project file changing though nothing was typed.
        if (stored == ((name ? ov?.DisplayName : ov?.Description) ?? "").Trim()) { SetBox(); return; }

        SetShop(e, o => { if (name) o.DisplayName = stored; else o.Description = stored; });
        SetBox();

        var what = name ? "its name" : "what it says";
        Say(stored.Length == 0
            ? $"{tab.Title}: {what} is the game's own again"
            : $"{tab.Title}: {what} = “{stored}”");
        MarkModification(tab);
        AfterEditsChanged();

        void SetBox() { if (name) tab.ShopName = stored; else tab.ShopText = stored; }
    }

    /// <summary>One of the two prices, in credits, ON LEAVING THE BOX.</summary>
    private void SetModificationPrice(ModificationTab tab, SheetTab.EffectRow row)
    {
        var e = tab.Entry;
        if (e.Shop is not { } shop) return;
        var property = row.Path;
        var theirs = property == "BuyCost" ? shop.Buy : shop.Sell;
        var existing = ShopOverride(e)?.Numbers.FirstOrDefault(n => n.Row == shop.Export && n.Field == property)?.Now;

        var shown = NumberText.Show(existing ?? theirs);
        var typed = (row.Value ?? "").Trim();
        if (typed == shown) { row.Value = shown; return; }

        tab.ShopSay("");
        void Refuse(string why)
        {
            var line = $"{row.Label}: {why} -- the box is back to {shown}";
            SayBad($"{tab.Title}: {line}");
            tab.ShopSay(line, bad: true);
            row.Value = shown;
        }

        if (!NumberText.TryParse(typed, out var parsed, out var parseWhy)) { Refuse(parseWhy); return; }

        // A PRICE IS A WHOLE NUMBER OF CREDITS, and is not negative. A fraction is rounded and a
        // negative is taken to nothing, and either is SAID: the box shows the whole number afterwards,
        // and "I typed 2.5 and it says 3" with no reason is the same silence as a refusal.
        var whole = Math.Round(parsed, MidpointRounding.AwayFromZero);
        if (Math.Abs(whole) > int.MaxValue) { Refuse($"'{typed}' is more than a price can hold"); return; }
        var note = "";
        if (whole < 0) { note = $"{NumberText.Show(parsed)} is below zero, and a price cannot be negative, so it is 0"; whole = 0; }
        else if (whole != parsed) note = $"{NumberText.Show(parsed)} rounded to {NumberText.Show(whole)}, a price is whole credits";

        var want = (int) whole;
        SetShop(e, ov =>
        {
            ov.Numbers.RemoveAll(n => n.Row == shop.Export && n.Field == property);
            if (want != theirs)
                ov.Numbers.Add(new ModOverride.NumberChange(shop.Export, property, theirs, want));
        });

        var mine = want != theirs;
        row.Mine = mine;
        row.Value = NumberText.Show(want);
        if (note.Length > 0) tab.ShopSay($"{row.Label}: {note}");
        Say($"{tab.Title}: {row.Label} " + (mine ? $"= {Credits(want)} credits, was {Credits(theirs)}" : "is the game's own again") +
            (note.Length > 0 ? $" ({note})" : ""));
        MarkModification(tab);
        AfterEditsChanged();
    }

    /// <summary>
    /// How rare the Black Market thinks it is, as one tag swapped for another inside ItemTags.
    /// A SWAP, never an addition: two rarity tags on one item is not a thing the game has an answer
    /// for, so the one that is there comes out in the same rewrite that puts the new one in.
    /// </summary>
    public void SetModificationRarity(ModificationTab tab, string? word)
    {
        if (tab.Filling || _mod is null || word is null || tab.Entry.Shop is not { } shop) return;
        var e = tab.Entry;
        var tag = RaritiesOf().FirstOrDefault(t => ModificationTab.Rarity(t) == word);
        if (tag is null) return;

        var ov = ShopOverride(e);
        var now = ov?.AddTags.FirstOrDefault(t => t.Contains(Modifications.RarityTag, StringComparison.Ordinal))
                  ?? shop.Rarity;
        if (tag == now) return;

        SetShop(e, o =>
        {
            o.RemoveTags.RemoveAll(t => t.Contains(Modifications.RarityTag, StringComparison.Ordinal));
            o.AddTags.RemoveAll(t => t.Contains(Modifications.RarityTag, StringComparison.Ordinal));

            if (tag == shop.Rarity)
            {
                // Back to the game's own asks for nothing -- and the container name goes with it, so
                // an override left holding only a rename does not also carry a line about a tag list
                // it no longer touches. Unless another tag edit is still there: it was written for
                // that container, and pointing it at AllowedSlots would send it to the wrong one.
                if (o.AddTags.Count == 0 && o.RemoveTags.Count == 0)
                    o.TagsProperty = ModOverride.DefaultTagsProperty;
                return;
            }

            // The container the tags live in, said out loud on the override. Without it the build
            // looks for AllowedSlots, which a shop item does not have.
            o.TagsProperty = "ItemTags";
            o.RemoveTags.Add(shop.Rarity);
            o.AddTags.Add(tag);
        });

        tab.ShopRarityMine = tag != shop.Rarity;
        Say($"{tab.Title}: how rare = {word}" + (tag == shop.Rarity ? " (the game's own)" : $", was {ModificationTab.Rarity(shop.Rarity)}"));
        MarkModification(tab);
        AfterEditsChanged();
    }

    public void ResetModificationRarity(ModificationTab tab)
    {
        if (tab.Entry.Shop is not { } shop || shop.Rarity.Length == 0) return;
        var word = ModificationTab.Rarity(shop.Rarity);
        tab.ShopRarity = word;                  // the drop-down follows, and raises the change a choice does
        SetModificationRarity(tab, word);       // and if it was not on screen to raise it, this does
    }

    // ------------------------------------------------------------------ putting it back

    /// <summary>
    /// Everything THIS SCREEN recorded for one modification, off: its number (and the copy that
    /// moved with it, a second number, a number it was given) on the ability; its name, sentence,
    /// rarity and two prices on the shop item. "Revert" in the section list, and the button on the
    /// card.
    ///
    /// NOT THE TWO OVERRIDES. Each is a package the mod changes, and other sections write to the
    /// same packages: the Abilities section puts a class pointer or a tag gate on the ability, the
    /// Data Explorer grid puts any property on the shop item. Taking the override whole (as the
    /// 1.6.1 screen did, which had no such section to collide with) deleted that work without a
    /// word, one line after the card had said "It is kept." So this takes off the same records the
    /// per-row Reset takes off, and then lets <see cref="Done"/> decide: an override left holding
    /// nothing goes, one still holding something stays -- and the status line says what stayed.
    /// </summary>
    public bool RevertModification(Modifications.Entry e)
    {
        if (_mod is null) return false;

        var gone = new List<string>();
        var keptA = new List<string>();
        var keptS = new List<string>();

        if (FindOverride(e.Package) is { } a)
        {
            var taken = a.Numbers.RemoveAll(n => IsMyAbilityNumber(e, n)) +
                        a.AddScalars.RemoveAll(s => IsMyAbilityScalar(e, s));
            if (taken > 0) gone.Add("its number");
            keptA = HeldElsewhere(a, e, shop: false);
            Done(a);
        }

        if (ShopOverride(e) is { } s)
        {
            if (s.DisplayName.Length > 0) { s.DisplayName = ""; gone.Add("its name"); }
            if (s.Description.Length > 0) { s.Description = ""; gone.Add("what it says"); }
            if (ClearRarity(s)) gone.Add("how rare it is");
            if (s.Numbers.RemoveAll(n => IsMyPrice(e, n)) > 0) gone.Add("its prices");
            keptS = HeldElsewhere(s, e, shop: true);
            Done(s);
        }

        // WHAT STAYED, and on which of the two assets, because "kept a number change" twice reads
        // as a mistake and the author needs to know where to look for it.
        var keptNote = "";
        if (keptA.Count + keptS.Count > 0)
        {
            var where = new List<string>();
            if (keptA.Count > 0) where.Add($"{Plainly(keptA)} on its ability");
            if (keptS.Count > 0) where.Add($"{Plainly(keptS)} on its shop item");
            keptNote = $"; kept {string.Join(", and ", where)} -- none of it put there by this screen";
        }

        if (gone.Count == 0)
        {
            Say(keptNote.Length == 0
                ? $"{e.Title}: this mod does nothing to it"
                : $"{e.Title}: this screen has nothing of this mod's to put back{keptNote}");
            return false;
        }

        Say($"reverted {e.Title} — removed {Plainly(gone)}{keptNote}");
        foreach (var open in Tabs.OfType<ModificationTab>().Where(t => t.Entry.Asset == e.Asset))
        {
            if (open.Editing) FillModification(open); else NoteModification(open);
        }
        AfterEditsChanged();
        return true;
    }

    /// <summary>Take the rarity swap off an override. True when there was one.</summary>
    private static bool ClearRarity(ModOverride o)
    {
        var taken = o.RemoveTags.RemoveAll(IsRarityTag) + o.AddTags.RemoveAll(IsRarityTag);
        if (o.AddTags.Count == 0 && o.RemoveTags.Count == 0)
            o.TagsProperty = ModOverride.DefaultTagsProperty;
        return taken > 0;
    }

    /// <summary>Open tabs follow the project when another mod is opened or this one is replaced.</summary>
    private void RefillModificationTabs()
    {
        foreach (var open in Tabs.OfType<ModificationTab>().ToList())
        {
            if (open.Editing) FillModification(open); else NoteModification(open);
        }
    }

#if DEV
    /// <summary>
    /// DEV: `--switchmod &lt;project.json&gt;` with a modification open (see StudioVm.Tables): open ANOTHER
    /// mod while its cards are up, say what they show, then leave every box (what a click elsewhere
    /// does) and say what the second mod now holds. The cards must show the second mod's numbers,
    /// and leaving a box must record nothing into it.
    /// </summary>
    public async Task DevSwitchModAttachAsync(ModificationTab tab, string path)
    {
        string Card() => $"numbers '{tab.SecEffects.Count}' [" +
                         string.Join(" ", tab.EffectRows.Select(x => $"{x.Label}={x.Value}(game {x.Now},mine={x.Mine})")) +
                         $"], shop '{tab.SecShop.Count}' [name '{tab.ShopName}', text '{tab.ShopText}', rarity '{tab.ShopRarity}'" +
                         $", " + string.Join(" ", tab.ShopRows.Select(x => $"{x.Label}={x.Value}(game {x.Now},mine={x.Mine})")) + "]";

        Say($"[switchmod] before: {Card()}; mod is {Path.GetFileName(_modPath)}");
        OpenMod(path);
        await Task.Delay(700);
        Say($"[switchmod] after opening {Path.GetFileName(path)}: {Card()}");

        // LEAVE EVERY BOX, through the handlers a lost focus reaches.
        foreach (var row in tab.EffectRows.ToList()) SetModificationNumber(tab, row);
        foreach (var row in tab.ShopRows.ToList()) SetModificationNumber(tab, row);
        SetModificationText(tab, true);
        SetModificationText(tab, false);

        var onDisk = _mod is null ? "(no mod)" : $"{_mod.Overrides.Count} override(s) in the project, {_mod.Overrides.Sum(o => o.Numbers.Count + o.AddScalars.Count)} number(s)";
        Say($"[switchmod] after leaving every box: {onDisk}; {Card()}");
    }

    /// <summary>
    /// DEV: `--attachedit "&lt;asset fragment&gt;|op|op..."` does what a person does: finds the modification
    /// in the Attachments list, chooses Edit, and then for each op types into the REAL control on
    /// screen and leaves it, so the XAML's own handlers are what is witnessed.
    ///
    ///   stat=&lt;text&gt;            the main number            other=&lt;label&gt;=&lt;text&gt;   a second number
    ///   name=&lt;text&gt;            the shop name              text=&lt;text&gt;            what it says
    ///   rarity=&lt;word&gt;          the rarity drop-down       buy=&lt;n&gt;  sell=&lt;n&gt;       the prices
    ///   give                   the "Give it one" button   reset=&lt;stat|label|buy|sell|rarity&gt;
    ///   putback                the "Put this one back" button
    ///   report                 say what the project holds for it
    ///   (a leading ~ on the asset fragment skips the controls and calls the handlers directly)
    /// </summary>
    public async Task DevAttachEditAsync(string spec, Func<object, string, Task<bool>>? typeInto = null)
    {
        var parts = spec.Split('|');
        // A LEADING "~" calls the view model's own methods instead of typing into the controls: the
        // same handlers a box's LostFocus reaches, minus the second and a half a real box costs, so
        // all 64 modifications can be walked in one run.
        if (parts[0].StartsWith('~')) { parts[0] = parts[0][1..]; typeInto = null; }
        for (var wait = 0; wait < 600 && _provider is null; wait++) await Task.Delay(200);
        if (_provider is null) { Say("--attachedit: the game is not open"); return; }
        if (_mod is null) { Say("--attachedit: open a mod first (--openmod <json>)"); return; }

        var all = ModificationList();
        var hits = all.Where(x => x.Asset.Contains(parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count != 1)
        {
            Say($"--attachedit: '{parts[0]}' matches {hits.Count} modification(s)" +
                (hits.Count > 1 ? ": " + string.Join(", ", hits.Take(6).Select(h => h.Asset)) : ""));
            return;
        }
        var entry = hits[0];

        var item = new SectionItem { Name = entry.Title, Payload = entry };
        EditPart(item);
        if (Tab is not ModificationTab tab || tab.Entry.Asset != entry.Asset)
        { Say("--attachedit: the modification did not open"); return; }
        await Task.Delay(800);   // a layout pass, so the boxes exist

        string Held()
        {
            var a = FindOverride(entry.Package);
            var s = ShopOverride(entry);
            string N(ModOverride? o) => o is null ? "(none)" :
                "numbers[" + string.Join("; ", o.Numbers.Select(n => $"{n.Row}.{n.Field} {NumberText.Show(n.Was, "0.########")}->{NumberText.Show(n.Now, "0.########")}")) + "]" +
                " scalars[" + string.Join("; ", o.AddScalars.Select(x => $"{x.Export}.{x.Name}={NumberText.Show(x.Value, "0.########")}")) + "]" +
                (o.DisplayName.Length > 0 ? $" name='{o.DisplayName}'" : "") +
                (o.Description.Length > 0 ? $" text='{o.Description}'" : "") +
                (o.AddTags.Count + o.RemoveTags.Count > 0 ? $" tags({o.TagsProperty}) +[{string.Join(",", o.AddTags)}] -[{string.Join(",", o.RemoveTags)}]" : "");
            return $"ability override: {N(a)}  |  shop override: {N(s)}";
        }

        SheetTab.EffectRow? RowNamed(string what)
        {
            var w = what.ToLowerInvariant();
            return tab.EffectRows.FirstOrDefault(r => w == "stat" ? r.Path == Modifications.StatProperty
                                                                  : r.Label.Contains(what, StringComparison.OrdinalIgnoreCase))
                   ?? tab.ShopRows.FirstOrDefault(r => (w == "buy" && r.Path == "BuyCost") || (w == "sell" && r.Path == "SellValue"));
        }

        foreach (var op in parts.Skip(1))
        {
            var eq = op.IndexOf('=');
            var key = (eq < 0 ? op : op[..eq]).ToLowerInvariant();
            var val = eq < 0 ? "" : op[(eq + 1)..];
            var typed = false;
            string said;

            switch (key)
            {
                case "stat" or "buy" or "sell":
                {
                    var row = RowNamed(key);
                    if (row is null) { Say($"[attachedit] {op}: no such row"); continue; }
                    typed = typeInto is not null && await typeInto(row, val);
                    if (!typed) { row.Value = val; SetModificationNumber(tab, row); }
                    said = $"box now '{row.Value}', mine={row.Mine}, card '{tab.SecEffects.Count}' / shop '{tab.SecShop.Count}'";
                    break;
                }
                case "other":
                {
                    var second = val.IndexOf('=');
                    if (second < 0) { Say($"[attachedit] {op}: other=<label>=<text>"); continue; }
                    var row = RowNamed(val[..second]);
                    if (row is null) { Say($"[attachedit] {op}: no row labelled '{val[..second]}'"); continue; }
                    var text = val[(second + 1)..];
                    typed = typeInto is not null && await typeInto(row, text);
                    if (!typed) { row.Value = text; SetModificationNumber(tab, row); }
                    said = $"box now '{row.Value}', mine={row.Mine}";
                    break;
                }
                case "name" or "text" or "rarity":
                    typed = typeInto is not null && await typeInto(key, val);
                    if (!typed)
                    {
                        if (key == "name") { tab.ShopName = val; SetModificationText(tab, true); }
                        else if (key == "text") { tab.ShopText = val; SetModificationText(tab, false); }
                        else SetModificationRarity(tab, val);
                    }
                    said = $"name '{tab.ShopName}', text '{tab.ShopText}', rarity '{tab.ShopRarity}' (mine={tab.ShopRarityMine})";
                    break;
                case "give":
                {
                    var row = tab.EffectRows.FirstOrDefault(r => r.HasAction);
                    if (row is null) { Say("[attachedit] give: this one carries its own number, there is no button"); continue; }
                    typed = typeInto is not null && await typeInto(row, "<action>");
                    if (!typed) GiveModificationNumber(tab, row);
                    said = $"stat row now has a box: {tab.EffectRows.FirstOrDefault()?.CanType}, value '{tab.EffectRows.FirstOrDefault()?.Value}'";
                    break;
                }
                case "reset":
                {
                    if (val == "rarity")
                    {
                        typed = typeInto is not null && await typeInto("<resetrarity>", "");
                        if (!typed) ResetModificationRarity(tab);
                        said = $"rarity '{tab.ShopRarity}' (mine={tab.ShopRarityMine})";
                        break;
                    }
                    var row = RowNamed(val);
                    if (row is null) { Say($"[attachedit] {op}: no such row"); continue; }
                    typed = typeInto is not null && await typeInto(row, "<reset>");
                    if (!typed) ResetModificationNumber(tab, row);
                    // THE ROW ON SCREEN NOW, not the one that was pressed: Reset on a tier that was
                    // given a number replaces its row with the no-box one.
                    var cur = RowNamed(val);
                    said = $"the row now: {(cur is null ? "(none)" : $"box {(cur.CanType ? "offered" : "NOT offered")}, shows '{cur.Value}', game '{cur.Now}', mine={cur.Mine}, action '{cur.ActionLabel}'")}";
                    break;
                }
                case "putback":
                    typed = typeInto is not null && await typeInto("<putback>", "");
                    if (!typed) RevertModification(entry);
                    said = "";
                    break;
                case "report":
                    typed = true;      // nothing is typed; it only says what the project holds
                    said = "";
                    break;
                default:
                    Say($"[attachedit] unknown op '{op}'");
                    continue;
            }

            Say(key == "report"
                ? "[attachedit] report:"
                : $"[attachedit] {op} ({(typed ? "through the real control" : "no control on screen; called the handler's own method")}): {said}");
            Say($"[attachedit]    feedback: card {(tab.EffectsFeedback.Length == 0 ? "(nothing)" : $"{(tab.EffectsFeedbackBad ? "RED" : "AMBER")} '{tab.EffectsFeedback}'")}; " +
                $"shop {(tab.ShopFeedback.Length == 0 ? "(nothing)" : $"{(tab.ShopFeedbackBad ? "RED" : "AMBER")} '{tab.ShopFeedback}'")}");
            Say($"[attachedit]    held: {Held()}");
        }

        Say($"[attachedit] done. {Held()}");
    }

    /// <summary>
    /// DEV, for --tabswitch: open a modification, an ability or a table in its EDITOR and hand
    /// back the tab. Waits for what the card reads, so its boxes exist.
    /// </summary>
    public async Task<SheetTab?> DevOpenInEditorAsync(string kind, string fragment)
    {
        for (var wait = 0; wait < 600 && _provider is null; wait++) await Task.Delay(200);
        if (_provider is null || _mod is null) { Say("[tabswitch] open a mod and the game first"); return null; }

        switch (kind)
        {
            case "mod":
            {
                var hit = ModificationList().Where(x => x.Asset.Contains(fragment, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count != 1) { Say($"[tabswitch] '{fragment}' matches {hit.Count} modification(s)"); return null; }
                EditPart(new SectionItem { Name = hit[0].Title, Payload = hit[0] });
                return Tab as ModificationTab;
            }
            case "ability":
            {
                var hit = AbilityIndex.Of(_provider, "GA_").Concat(AbilityIndex.Of(_provider, "GE_"))
                                      .Where(x => x.Package.EndsWith("/" + fragment, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hit.Count != 1) { Say($"[tabswitch] '{fragment}' matches {hit.Count} abilit(ies)"); return null; }
                EditPart(new SectionItem { Name = hit[0].Name, Payload = hit[0] });
                if (Tab is not AbilityTab ab) return null;
                for (var wait = 0; wait < 240 && !ab.EffectsRead; wait++) await Task.Delay(250);
                return ab;
            }
            case "spec":
            {
                // A specialization copy this project holds, with its RUNGS card up (level, focus and cost boxes).
                var hit = SpecsInMod.FirstOrDefault(s => s.Id.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (hit is null)
                { Say($"[tabswitch] no specialization copy matching '{fragment}' among {string.Join(", ", SpecsInMod.Select(s => s.Id))}"); return null; }
                await EditSpec(hit);
                if (Tab is not SpecTab st || !st.Spec.Id.Equals(hit.Id, StringComparison.OrdinalIgnoreCase)) return null;
                for (var wait = 0; wait < 120 && st.Rungs.Count == 0; wait++) await Task.Delay(250);
                st.SecRungs.Open = true;
                return st;
            }
            case "enemy":
            {
                // An enemy GROUP's tab with its NUMBERS card up: one multiplier box per stat row.
                var groups = await EnemyGroupsAsync();
                var hit = groups.FirstOrDefault(g => g.Display.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                          ?? groups.FirstOrDefault(g => g.Leaf.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (hit is null) { Say($"[tabswitch] no enemy group matching '{fragment}' among {groups.Count}: {string.Join(" | ", groups.Take(40).Select(g => g.Display))}"); return null; }
                await OpenEnemyGroup(hit);
                if (Tab is not EnemyTab et) return null;
                for (var wait = 0; wait < 160 && et.EnemyRows.Count == 0; wait++) await Task.Delay(250);
                et.SecEnemy.Open = true;
                return et;
            }
            case "weapon":
            {
                // A weapon COPY this project holds, opened in its editor with its NUMBERS card up. The
                // copy needs numbers of its own (--ownstats) or the card has no boxes to type into.
                var hit = WeaponsInMod.FirstOrDefault(c => c.Id.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                if (hit is null)
                { Say($"[tabswitch] no weapon copy matching '{fragment}' among {string.Join(", ", WeaponsInMod.Select(c => c.Id))}"); return null; }
                await EditWeaponCopy(hit);
                if (Tab is not WeaponTab wt || wt.Copy?.Id != hit.Id) return null;
                for (var wait = 0; wait < 120 && wt.WeaponStats.Count == 0; wait++) await Task.Delay(250);
                wt.SecStats.Open = true;
                return wt;
            }
            case "table":
            {
                var hit = TableIndex.Of(_provider).FirstOrDefault(t => t.Name.Equals(fragment, StringComparison.OrdinalIgnoreCase));
                if (hit is null) { Say($"[tabswitch] no table named {fragment}"); return null; }
                EditPart(new SectionItem { Name = hit.Name, Payload = hit });
                if (Tab is not TableTab tt) return null;
                for (var wait = 0; wait < 600 && tt.Loading; wait++) await Task.Delay(200);
                RefillTable(tt);
                return tt;
            }
        }
        return null;
    }

    /// <summary>
    /// DEV: `--abmodswitch "&lt;GA_ asset&gt;|&lt;typed number&gt;|&lt;project.json&gt;"`. Opens the ability in its editor,
    /// types a number into its first box and leaves it, then opens ANOTHER mod while the card is up
    /// and says what the card shows and what leaving every box records into the second mod.
    /// </summary>
    public async Task DevAbilitySwitchModAsync(string spec)
    {
        var p = spec.Split('|');
        if (p.Length != 3) { Say("--abmodswitch: GA_ asset|typed number|project.json"); return; }
        if (await DevOpenInEditorAsync("ability", p[0]) is not AbilityTab tab) return;

        string Card() => $"card '{tab.SecEffects.Count}' [" + string.Join(" ", tab.EffectRows.Where(r => r.CanType).Take(3).Select(x => $"{x.Label}={x.Value}(game {x.Now},mine={x.Mine})")) +
                         $"], gates '{tab.SecGates.Count}', points '{tab.SecPoints.Count}', tier rows {tab.Progression.Count(r => r.Edited)} edited";

        var row = tab.EffectRows.First(r => r.CanType && !r.HasAction);
        row.Value = p[1];
        SetEffectNumber(tab, row);
        Say($"[abmodswitch] mod A after typing {p[1]} into {row.Label}: {Card()}; mod A holds: {DevHeld()}");

        OpenMod(p[2]);
        await Task.Delay(800);
        Say($"[abmodswitch] after opening {Path.GetFileName(p[2])}: {Card()}");

        foreach (var r in tab.EffectRows.ToList()) SetEffectNumber(tab, r);
        Say($"[abmodswitch] after leaving every box: {Card()}; mod B holds: {DevHeld()}");
    }

    /// <summary>
    /// DEV: `--abbox "&lt;GA_/GE_ asset&gt;|&lt;path or label fragment&gt;|&lt;text, or ~ to leave the box as it is&gt;"`.
    /// Opens the ability in its editor, puts the text in the matching number box and leaves it (the
    /// handler a lost focus reaches), and says what the card and the project hold. `~` is a person
    /// tabbing through a box without typing.
    /// </summary>
    public async Task DevAbilityBoxAsync(string spec)
    {
        var p = spec.Split('|');
        if (p.Length is not (3 or 4)) { Say("--abbox: asset|row fragment|text[|card key to leave open]"); return; }
        if (await DevOpenInEditorAsync("ability", p[0]) is not AbilityTab tab) return;

        // An optional fourth part folds every card but one, so a photograph can show it (the
        // progression table is fourteen tiers tall and pushes the numbers off the bottom).
        if (p.Length == 4 && tab.EditSections.FirstOrDefault(s => s.Key.Equals(p[3], StringComparison.OrdinalIgnoreCase)) is { } only)
        {
            foreach (var s in tab.EditSections) s.Open = ReferenceEquals(s, only);
            tab.SelectSection(only);
        }

        var rows = tab.EffectRows.Where(r => r.Path.Contains(p[1], StringComparison.OrdinalIgnoreCase) ||
                                             r.Label.Contains(p[1], StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0) { Say($"[abbox] no row matches '{p[1]}' among {tab.EffectRows.Count}"); return; }
        var row = rows[0];

        Say($"[abbox] {tab.Title} / {row.Path}: game {row.Now}, box shows '{row.Value}', mine={row.Mine}; project {DevHeld()}");
        if (p[2] != "~") row.Value = p[2];
        SetEffectNumber(tab, row);
        var cur = tab.EffectRows.FirstOrDefault(r => r.Path == row.Path && r.Export == row.Export) ?? row;
        Say($"[abbox] after leaving the box (typed {(p[2] == "~" ? "nothing" : "'" + p[2] + "'")}): box shows '{cur.Value}', mine={cur.Mine}; " +
            $"card says {(tab.EffectsFeedback.Length == 0 ? "(nothing)" : $"{(tab.EffectsFeedbackBad ? "RED" : "AMBER")} '{tab.EffectsFeedback}'")}; project {DevHeld()}");
    }

    /// <summary>DEV, for --tabswitch: what the project holds, one line per override, so "where did it land" is read off the project.</summary>
    public string DevHeld()
    {
        if (_mod is null) return "(no mod)";
        if (_mod.Overrides.Count == 0) return "(no overrides)";
        return string.Join(" || ", _mod.Overrides.Select(o =>
            Path.GetFileName(o.Target) + ": " +
            string.Join("; ", o.Numbers.Select(n => $"{n.Row}.{n.Field} {NumberText.Show(n.Was, "0.########")}->{NumberText.Show(n.Now, "0.########")}")) +
            string.Join("; ", o.AddScalars.Select(s => $"+{s.Export}.{s.Name}={NumberText.Show(s.Value, "0.########")}"))));
    }

    /// <summary>DEV, for --tabswitch: the numbers a tab's boxes show right now.</summary>
    public static string DevBoxes(SheetTab tab)
        => tab is SpecTab { Rungs.Count: > 0 } sp
            ? string.Join(" ", sp.Rungs.Take(1).Select(r => $"rung0 level={r.Level} focus={r.Focus} cost={r.Cost}"))
        : tab is EnemyTab { EnemyRows.Count: > 0 } en
            ? string.Join(" ", en.EnemyRows.Where(r => r.CanScale).Take(3).Select(r => $"{r.Shown}=x{r.Text}{(r.Changed ? "*" : "")}"))
        : tab is WeaponTab { WeaponStats.Count: > 0 } w
            ? string.Join(" ", w.WeaponStats.Take(4).Select(r => $"{r.Label}={r.Text}{(r.Changed ? "*" : "")}"))
            : string.Join(" ", tab.EffectRows.Where(r => r.CanType).Take(4).Select(r => $"{r.Label}={r.Value}{(r.Mine ? "*" : "")}"));

    /// <summary>DEV, for --tabswitch on a specialization: the first rung of every spec copy in the project, as the project holds it.</summary>
    public string DevSpecHeld()
        => _mod is null ? "(no mod)"
         : string.Join(" || ", _mod.Specs.Select(s => s.Id + ": " + (s.Tree.Count > 0 && s.Tree[0].Count > 0
               ? $"rung 0 level {s.Tree[0][0].MinLevel}, focus {s.Tree[0][0].FocusLevel}, cost {s.Tree[0][0].FocusCost}" : "(no rungs)")));

    /// <summary>DEV, for --tabswitch on an enemy group: the multipliers the project holds, per group.</summary>
    public string DevEnemyHeld()
        => _mod is null ? "(no mod)"
         : _mod.EnemyStats.Count == 0 ? "(no enemy multipliers)"
         : string.Join(" || ", _mod.EnemyStats.Select(e => e.Group + ": " + string.Join(", ", e.Multipliers.Select(kv => $"{kv.Key}=x{kv.Value}"))));

    /// <summary>
    /// DEV, for --tabswitch on a weapon: what each weapon copy in the project says its numbers are, listing only
    /// the ones the project moved off the game's table. Read from the project through the build's own reader,
    /// so "where did it land" is the project's answer and not the box's.
    /// </summary>
    public string DevWeaponHeld()
    {
        if (_mod is null || _provider is null) return "(no mod)";
        var parts = new List<string>();
        foreach (var c in WeaponsInMod)
        {
            var stats = WeaponAuthor.StatsOf(_provider, _mod, c.Id, out _);
            var moved = stats?.Where(s => s.Changed).Select(s => $"{s.Label}={NumberText.Show(s.Value, "0.########")}").ToList();
            parts.Add(c.Id + ": " + (moved is { Count: > 0 } ? string.Join(", ", moved) : "(nothing moved)"));
        }
        return string.Join(" || ", parts);
    }
#endif
}

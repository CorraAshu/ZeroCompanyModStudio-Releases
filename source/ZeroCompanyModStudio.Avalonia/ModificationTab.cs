using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE WEAPON MODIFICATION, open as a tab: a barrel, a cell, a scope or a stabilizer.
///
/// The screen the public 1.6.1 Studio called "What attachments do", cut to one modification a
/// tab, which is how every other section of this Studio opens its things. What 1.6.1 showed as a
/// table of sixty-four rows is the section's list here (the number each carries is on its row),
/// and the four tiers of a style, which the old page drew side by side, are the last block of
/// this sheet so that a number can still be judged against its neighbours.
///
/// A MODIFICATION IS TWO ASSETS. The ability holds what it DOES, a single number; the shop item
/// holds what it is CALLED, how rare it is and what it costs. They are joined by the item's own
/// reference to the ability, so editing one modification completely is an override on each. The
/// sheet says which is which, because a player thinks of them as one thing and the build does not.
///
/// Read from <see cref="Modifications"/>, which the ImGui Studio also read, so the two cannot
/// disagree about a modification.
/// </summary>
public sealed class ModificationTab : SheetTab
{
    private readonly Modifications.Entry _e;
    private readonly IReadOnlyList<Modifications.Entry> _family;

    public ModificationTab(Modifications.Entry entry, IReadOnlyList<Modifications.Entry> family)
    {
        _e = entry;
        _family = family;
        Title = entry.Title;
    }

    public Modifications.Entry Entry => _e;

    public override string Title { get; }

    public override string Tooltip => _e.Package;

    /// <summary>The asset name under the title: what the build and the project call it.</summary>
    public override string Subtitle => _e.Asset;

    public override string Description => Modifications.Plain(_e.Does);

    public override string Headline =>
        $"{Sections.SlotTitle(_e.Slot).TrimEnd('s')}  ·  {_e.Readable}  ·  tier {_e.Tier}" +
        (_e.Shop is { Rarity.Length: > 0 } s ? $"  ·  {Rarity(s.Rarity)}" : "");

    public override string Problem =>
        _e.Shop is null
            ? "No shop item is paired with this one, so its name, price and rarity cannot be changed here."
            : "";

    /// <summary>The numbers and the shop half: the two cards this tab edits.</summary>
    public override bool CanEdit => true;

    public override string EditBadge => "EDITING THE GAME'S OWN MODIFICATION";

    public override string EditWarning =>
        "A modification is one asset that every gun reads. Change it and it changes for your weapons " +
        "and for the game's alike, on every save. Two mods that change the same modification collide, " +
        "and the build's clash check says so. Nothing here uses a modification up.";

    /// <summary>`BitReactor.ItemType.Rarity.Uncommon` as the one word a person would say.</summary>
    public static string Rarity(string tag) =>
        tag.Length == 0 ? "no rarity" : tag[(tag.LastIndexOf('.') + 1)..];

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Slot", Sections.SlotTitle(_e.Slot).TrimEnd('s'));
            Fact("Style", _e.Readable);
            Fact("Tier", _e.Tier.ToString());

            if (_e.Stat is { } stat)
                Fact("Its number", Sections.StatText(stat.Value, _e.Percent) +
                                   (_e.Percent ? "   (the game stores it as a fraction)" : ""));
            else
                Fact("Its number",
                     "none of its own: it uses the tier below's, " +
                     (InheritedFrom() is { } below ? Sections.StatText(below, _e.Percent) : "—") +
                     ", and raises only its cap");

            foreach (var other in _e.Others)
                Fact(other.Label, Sections.StatText(other.Value, false));

            if (_e.CopyDisagrees && _e.Copy is not null && _e.Stat is not null)
                Fact("The game shows", $"{Sections.StatText(_e.Copy.Value, _e.Percent)} in the copy " +
                                       "its menus keep, not " +
                                       $"{Sections.StatText(_e.Stat.Value, _e.Percent)} — the game's own files");

            if (_e.Shop is { } shop)
            {
                // DIGITS ONLY, not :N0: that groups by the PC's convention (2,000 or 2.000), and the
                // price box refuses a comma followed by three digits. See StudioVm.Credits.
                Fact("Costs", $"{StudioVm.Credits(shop.Buy)} credits");
                Fact("Sells for", $"{StudioVm.Credits(shop.Sell)} credits");
            }

            blocks.Add(new SheetFact { Label = "Ability", Value = _e.Package, Wide = true });
            if (_e.Shop is { } item)
                blocks.Add(new SheetFact { Label = "Shop item", Value = item.Package, Wide = true });

            // THE FOUR TIERS, because a number is judged against its neighbours. The old page drew
            // them as four rows in one table; a tab per modification would otherwise lose that.
            var tiers = _family.Where(x => x.Slot == _e.Slot && x.Style == _e.Style)
                               .OrderBy(x => x.Tier).ToList();
            if (tiers.Count > 1)
            {
                blocks.Add(new SheetBand { Text = $"The {tiers.Count} tiers of {_e.Readable}" });
                var tband = false;
                foreach (var t in tiers)
                {
                    blocks.Add(new SheetPair
                    {
                        Left = $"T{t.Tier}  {t.Title}",
                        Right = t.Stat is null
                            ? "uses the tier below"
                            : Sections.StatText(t.Stat.Value, t.Percent),
                        Band = tband,
                    });
                    tband = !tband;
                }
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

    /// <summary>The nearest lower tier of the same style that carries a number: what an inheriting tier uses.</summary>
    private double? InheritedFrom() =>
        _family.Where(x => x.Slot == _e.Slot && x.Style == _e.Style && x.Tier < _e.Tier && x.Stat is not null)
               .OrderByDescending(x => x.Tier)
               .Select(x => (double?) x.Stat!.Value)
               .FirstOrDefault();
}

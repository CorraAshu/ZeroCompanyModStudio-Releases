using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A DUPLICATED ASSET THIS MOD SHIPS, open as a tab — the generic one.
///
/// A <see cref="ModCopy"/> is the most general thing a project can hold: the mod ships its own
/// copy of one of the game's registered assets, at a name of its own, and everything it points at
/// still points where the original did. Three kinds of copy have editors that know what they are
/// — a weapon chain, a character definition, a specialization — and this is what the rest get,
/// which is everything from a utility item to the four plumbing links of a weapon.
///
/// THE WHOLE POINT IS "WHAT IS INSIDE IT". A copy's other fields are a name and a description;
/// what makes a copy worth making is that its references can be moved, and until this tab existed
/// the new Studio offered no way to see that an asset HAD any. The old studio's own words for the
/// state before it grew this list: a weapon's Blueprint "named its firing sound, its projectile,
/// its muzzle flash, its volley profile and its animation set — and offered a blank box and no
/// way to learn any of those existed".
///
/// The edit surface is <see cref="SheetTab"/>'s, as it is for everything else here.
/// </summary>
public sealed class CopyTab : SheetTab
{
    public CopyTab(ModCopy copy)
    {
        Copy = copy;
    }

    /// <summary>The copy this tab edits. A copy tab never opens on anything else.</summary>
    public ModCopy Copy { get; }

    public override string Title => AbilityText.Shown(Copy);

    public override string Subtitle => Copy.Id;

    public override string Tooltip => $"{Copy.Id} — your own copy of {Copy.Template}";

    public override string Aside => $"your own copy of {Leaf(Copy.Template)}";

    public override bool IsCopy => true;
    public override bool CanEdit => true;

    /// <summary>
    /// WHAT THIS COPY IS A LINK OF, where it is one.
    ///
    /// A weapon is five copies, and four of them are plumbing that reaches this editor rather
    /// than the weapon one. Removing or renaming a link on its own leaves the rest of that weapon
    /// pointing at an asset the mod no longer ships — which builds, installs, and is a weapon
    /// with a hole in it. Said at the top, where the same warning sits for everything else that
    /// looks usable and is not.
    /// </summary>
    public string PartOf { get; set; } = "";

    public override string Problem => PartOf.Length > 0
        ? $"This is one link of {PartOf}. Its name and what it points at are only correct as a " +
          "SET — change one here and the rest of that weapon still names the old one. The weapon's " +
          "own editor moves them together."
        : "";

    /// <summary>Title and subtitle both follow the fields, so a save re-reads them.</summary>
    public void Renamed()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(Tooltip));
    }

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Copied from", Copy.Template);
            Fact("Ships as", Copy.Id);
            if (Copy.MadeBy.Length > 0) Fact("Made by", Copy.MadeBy);

            // BOTH MAPS, NAMED APART. Which of them holds a reference is not cosmetic: a soft
            // path moves by renaming and a hard import does not, and writing the wrong one is an
            // edit that reports success and changes nothing in the game.
            if (Copy.RenameNames.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Renamed names ({Copy.RenameNames.Count})" });
                foreach (var kv in Copy.RenameNames.Take(12))
                    blocks.Add(new SheetBullet { Text = $"{Leaf(kv.Key)}  →  {Leaf(kv.Value)}" });
                if (Copy.RenameNames.Count > 12)
                    blocks.Add(new SheetBullet { Text = $"and {Copy.RenameNames.Count - 12} more" });
            }

            if (Copy.RepointImports.Count > 0)
            {
                blocks.Add(new SheetBand
                { Text = $"Re-pointed imports ({Copy.RepointImports.Count})" });
                foreach (var kv in Copy.RepointImports.Take(12))
                    blocks.Add(new SheetBullet { Text = $"{Leaf(kv.Key)}  →  {Leaf(kv.Value)}" });
            }

            if (Copy.Numbers.Count > 0)
                blocks.Add(new SheetBand { Text = $"Numbers changed ({Copy.Numbers.Count})" });
            if (Copy.TagEdits.Count > 0)
                blocks.Add(new SheetBand { Text = $"Tag edits ({Copy.TagEdits.Count})" });
            if (Copy.Slots.Count > 0)
                blocks.Add(new SheetBand { Text = $"Slots set ({Copy.Slots.Count})" });

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }
        }
    }
}

using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A CHARACTER OF THIS MOD'S OWN, open as a tab.
///
/// A character is a <see cref="ModCopy"/> like any other — the mod ships its own copy of one of
/// the game's 794 customization definitions — and what makes it a character rather than a
/// duplicated asset is a single fact the old Studio also routes on: the id begins `CD_Char_`.
/// Everything else about it is spread across the project rather than held in one object:
///
///   the definition    this copy. Its `Slots` map is what the author changes on the look card.
///   the name          a SECOND copy, one over, carrying FirstName / LastName /
///                     BackgroundDescription in its `Text` map. Edited here, because a person
///                     looking for a character's name looks at the character.
///   the offer         a row added to one of the three hiring pools, found by what it points at.
///
/// STARTING FROM ONE OF THE GAME'S OWN is what makes this an evening's feature rather than a
/// month's: species, rig, face and outfit all arrive with the template, and every one of them can
/// be changed afterwards, one slot at a time.
/// </summary>
public sealed class CharacterTab : SheetTab
{
    public CharacterTab(ModCopy copy)
    {
        Copy = copy;
    }

    /// <summary>The definition this tab edits. A character tab never opens on anything else.</summary>
    public ModCopy Copy { get; }

    /// <summary>
    /// The name part beside it, or null when the mod has not got one.
    ///
    /// Set by the panel rather than looked up here: finding it needs the whole project, and a tab
    /// that reaches back into the mod to answer a property is how two views of one thing start
    /// disagreeing about it.
    /// </summary>
    public ModCopy? NamePart { get; set; }

    public override string Title
    {
        get
        {
            var name = $"{CharFirst} {CharLast}".Trim();
            return name.Length > 0 ? name : Copy.Display.Length > 0 ? Copy.Display : Copy.Id;
        }
    }

    public override string Subtitle => Copy.Id;

    public override string Tooltip => $"{Copy.Id} — started from {Copy.Template}";

    public override string Aside => $"a character of this mod's own, from {Leaf(Copy.Template)}";

    public override bool IsCopy => true;
    public override bool CanEdit => true;

    public override string EditBadge => "A CHARACTER OF THIS MOD'S OWN";

    public override string EditWarning =>
        "Their name and story are written on a name part one copy over, and their look is the " +
        "template's until a slot here says otherwise. Nothing of the game's is written over.";

    /// <summary>
    /// A character nobody is offered is a character nobody meets.
    ///
    /// Said at the top rather than only on the card, because it is the one fault that leaves
    /// everything else looking finished: the definition builds, the name part builds, the mod
    /// installs, and they are simply never in the hiring pool.
    /// </summary>
    public override string Problem => CharOfferedWarn
        ? "No hiring-pool row offers this character yet, so nothing in the game will ever put " +
          "them in front of you. Make them again, or add the row by hand."
        : "";

    /// <summary>Title follows the name fields, so a save re-reads it.</summary>
    public void Renamed()
    {
        Raise(nameof(Title));
        Raise(nameof(Tooltip));
    }

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Started from", Copy.Template);
            Fact("Ships as", Copy.Id);
            if (NamePart is not null) Fact("Name part", NamePart.Id);
            if (Copy.MadeBy.Length > 0) Fact("Made by", Copy.MadeBy);

            if (CharFrom.Length > 0)
                blocks.Add(new SheetFact { Label = "Rig", Value = CharFrom, Wide = true });

            // THE SLOTS THE AUTHOR HAS CHANGED, not all of them. The full list is the look card;
            // what belongs on a read sheet is the answer to "what did I do to this one".
            if (Copy.Slots.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Slots you changed ({Copy.Slots.Count})" });
                foreach (var kv in Copy.Slots.Take(14))
                    blocks.Add(new SheetBullet
                    {
                        Text = $"{Short(kv.Key)}  →  " +
                               (kv.Value.Length == 0 ? "(empty)" : SlotEdit.LeafOf(kv.Value)),
                    });
                if (Copy.Slots.Count > 14)
                    blocks.Add(new SheetBullet { Text = $"and {Copy.Slots.Count - 14} more" });
            }

            if (CharOffered.Length > 0)
            {
                blocks.Add(new SheetBand { Text = "In the hiring pool", Accent = CharOfferedWarn });
                blocks.Add(new SheetBullet { Text = CharOffered });
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

    /// <summary>"br.Customization.Slot.Character.Outfit.Torso.Mesh" -> "Outfit / Torso / Mesh".</summary>
    private static string Short(string tag)
    {
        var t = tag.StartsWith(SlotEdit.TagPrefix, StringComparison.Ordinal)
            ? tag[SlotEdit.TagPrefix.Length..] : tag;
        if (t.StartsWith("Character.", StringComparison.Ordinal)) t = t["Character.".Length..];
        return string.Join(" / ", t.Replace("Appearance.Humanoid.", "").Split('.'));
    }
}

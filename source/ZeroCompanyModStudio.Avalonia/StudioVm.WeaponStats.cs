using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE REST OF A WEAPON: what colour it fires, which Armoury shelf it lands on, and its numbers.
///
/// All three are Core calls with a question in front of them. None of them is a field on the
/// copy: the bolt colour is a property on this weapon's own ACTOR, the shelf is a rename inside
/// the armoury part, and the numbers are an attribute table the class shares until the weapon is
/// given one of its own.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// WHAT COLOUR IT FIRES, on this mod's own copy of the weapon's actor.
    ///
    /// The weapon it was copied from keeps whatever colour it had, and so does every other weapon
    /// of its kind. A weapon that does not copy its own actor has nothing to colour — said
    /// rather than offered as a control that writes nowhere.
    /// </summary>
    private void FillWeaponBolts(WeaponTab tab, ModCopy copy)
    {
        if (_mod is null || _provider is null) return;
        if (WeaponAuthor.IdentityOf(copy.Id) is not { } who) return;

        var actor = _mod.Copies.FirstOrDefault(
            x => x.Id.StartsWith("BP_", StringComparison.Ordinal) &&
                 WeaponAuthor.NamesTheWeapon(x.Id, who.Id));
        if (actor is null) { tab.SetBolts([], -1, ""); return; }

        var choices = BoltColour.Choices(_provider);
        if (choices.Count == 0)
        {
            tab.SetBolts([], -1, "The game's colour list would not read, so this cannot be offered.");
            return;
        }

        // The export is the TEMPLATE's default object: a copy keeps the names its template had,
        // so a copy of BP_Rifle_DC-15A still answers to Default__BP_Rifle_DC-15A_C.
        var export = "Default__" + Leaf(actor.Template) + "_C";
        var present = BoltColour.Current(_provider, actor.Template, export);

        // WHAT THE PROJECT ALREADY SAYS, so the control shows the mod rather than the game.
        var chosen =
            actor.Numbers.FirstOrDefault(n => n.Row == export && n.Field == BoltColour.Property)
                is { } num ? (int) num.Now
            : actor.AddScalars.FirstOrDefault(a => a.Export == export && a.Name == BoltColour.Property)
                is { } add ? (int) add.Value
            : present ?? 0;

        tab.SetBolts(choices.Select(x => new SheetTab.BoltChoice(x.Index, x.Label)), chosen,
                     present is null
                         ? $"Its donor states no colour, so yours would be given one it did not " +
                           $"have — the game's default is {choices[0].Label}."
                         : $"Its donor fires " +
                           (choices.FirstOrDefault(x => x.Index == present)?.Label ?? $"{present}") + ".");
    }

    /// <summary>Write the chosen bolt colour onto the weapon's own actor.</summary>
    public void SetWeaponBolt(WeaponTab tab, int index)
    {
        if (_mod is null || _provider is null || tab.Copy is not { } copy) return;
        if (WeaponAuthor.IdentityOf(copy.Id) is not { } who) return;

        var actor = _mod.Copies.FirstOrDefault(
            x => x.Id.StartsWith("BP_", StringComparison.Ordinal) &&
                 WeaponAuthor.NamesTheWeapon(x.Id, who.Id));
        if (actor is null) { Say($"{copy.Id}: this weapon copies no actor, so it has no bolts"); return; }

        var export = "Default__" + Leaf(actor.Template) + "_C";
        var present = BoltColour.Current(_provider, actor.Template, export);

        BoltColour.Setting(actor, export, present, index);
        _mod.Save();

        var label = BoltColour.Choices(_provider).FirstOrDefault(x => x.Index == index)?.Label ?? $"{index}";
        Say($"{copy.Id}: fires {label.ToLowerInvariant()}");
        FillWeaponBolts(tab, copy);
        AfterEditsChanged();
    }

    /// <summary>
    /// WHERE IT APPEARS IN THE ARMOURY.
    ///
    /// Three answers, and the order matters: a shelf THIS MOD defines is checked first, because
    /// InCustomClass asks only about the four shared shelves — so a weapon on its own shelf
    /// answered "no" and the panel said "on the game's LONGARM shelf" directly above a line
    /// naming the shelf it was really on. Two true sentences contradicting each other is worse
    /// than either alone.
    /// </summary>
    private void FillWeaponShelf(WeaponTab tab, ModCopy copy)
    {
        if (_mod is null) return;
        if (WeaponAuthor.IdentityOf(copy.Id) is not { } who) { tab.SetShelf("", "", false); return; }

        if (WeaponAuthor.OwnClassOf(_mod, copy.Id) is { } own)
        {
            var shelf = own[(own.LastIndexOf('.') + 1)..].ToUpperInvariant();
            tab.SetShelf($"On {shelf}, a shelf this mod defines",
                         "Nobody else's weapons are on it, and it needs no other mod.",
                         onOwn: true);
            return;
        }

        var custom = WeaponAuthor.InCustomClass(_mod, copy.Id);
        if (custom is null) { tab.SetShelf("", "", false); return; }

        tab.SetShelf(
            custom == true
                ? $"On the CUSTOM {who.Class.ToUpperInvariant()} shelf"
                : $"On the game's {who.Class.ToUpperInvariant()} shelf",
            custom == true
                ? "Beside other people's mod weapons. It needs the “Custom Weapon Classes” " +
                  "mod installed, which this tool ships and installs with it."
                : "Beside the game's own. That shelf was measured losing its FIRST row — one of " +
                  "the game's weapons — once it holds more than seven, counting every mod " +
                  "already installed.",
            onOwn: false);
    }

    /// <summary>Move it between the game's shelf and the custom one.</summary>
    public void SetWeaponShelf(WeaponTab tab, bool custom)
    {
        if (_mod is null || tab.Copy is not { } copy) return;

        var done = WeaponAuthor.SetCustomClass(_mod, copy.Id, custom, apply: true, out var why);
        if (done is null) { Say($"{copy.Id}: {why ?? "could not move it"}"); return; }

        _mod.Save();
        Say($"{copy.Id}: moved — {done.Count} change(s)");
        FillWeaponShelf(tab, copy);
        AfterEditsChanged();
    }

    /// <summary>
    /// A SHELF OF THIS MOD'S OWN, with a name and numbers nobody else's weapons share.
    ///
    /// Offered BELOW the two shared shelves rather than instead of them: the shipped ones need no
    /// setup and no dependency of the author's, so they are the right default. This is for
    /// somebody who wants their own name on the Armoury's list.
    ///
    /// WHAT IT BEHAVES LIKE IS NOT WHAT IT IS CALLED. The animations, the abilities and the
    /// numbers the weapon starts from all come from the class it is modelled on — a shelf that
    /// behaves like a Repeater holds repeaters, whatever the name on it says. Core keeps the list
    /// of what can be mirrored and refuses anything outside it, so the panel offers Core's list
    /// rather than one written here.
    /// </summary>
    public void MakeOwnShelf(WeaponTab tab)
    {
        if (_mod is null || _provider is null) { Say("the game is not mounted yet"); return; }
        if (tab.Copy is not { } copy) return;

        var name = tab.EditShelfName.Trim();
        if (name.Length == 0) { Say("give the shelf a name first"); return; }

        var like = tab.EditShelfLike;

        var done = WeaponAuthor.MakeOwnClass(_provider, _mod, copy.Id, name,
                                             name.ToUpperInvariant(), like,
                                             apply: true, out var why);
        if (done is null) { Say($"{copy.Id}: {why ?? "could not make the shelf"}"); return; }

        _mod.Save();
        tab.EditShelfName = "";
        Say($"{copy.Id}: on a shelf of its own called {name.ToUpperInvariant()}, behaving like a " +
            $"{like} — {done.Count} change(s)");
        FillWeaponShelf(tab, copy);
        AfterEditsChanged();
    }

    /// <summary>
    /// ITS NUMBERS — damage, rate, range and the rest.
    ///
    /// A weapon shares its class's attribute table until it is given one of its OWN. Editing the
    /// shared one would change every weapon of that kind, the game's included, so the panel says
    /// that and offers the split rather than letting somebody do it by accident.
    /// </summary>
    private void FillWeaponStats(WeaponTab tab, ModCopy copy)
    {
        if (_mod is null || _provider is null) return;

        var stats = WeaponAuthor.StatsOf(_provider, _mod, copy.Id, out var why);
        if (stats is null)
        {
            tab.SetWeaponStats([], why ?? "");
            return;
        }

        tab.SetWeaponStats(
            stats.Where(s => s.Editable)
                 .Select(s => new SheetTab.StatRow(s.Row, s.Label, s.Value, s.Changed)),
            "");
    }

    /// <summary>Give it numbers of its own, so changing them changes nothing else.</summary>
    public void GiveWeaponOwnStats(WeaponTab tab)
    {
        if (_mod is null || _provider is null || tab.Copy is not { } copy) return;

        var done = WeaponAuthor.OwnStats(_provider, _mod, copy.Id, apply: true, out var why);
        if (done is null) { Say($"{copy.Id}: {why ?? "could not give it its own numbers"}"); return; }

        _mod.Save();
        Say($"{copy.Id}: has numbers of its own — {done.Count} change(s)");
        FillWeaponStats(tab, copy);
        AfterEditsChanged();
    }

    /// <summary>
    /// WHAT FITS IT — whether this weapon keeps its donor's type tag or mints one of its own.
    ///
    /// The tag is what the game's palettes are keyed by, so keeping the donor's means every
    /// attachment the donor takes fits this one too. Minting its own means NOTHING of the game's
    /// fits it, and every piece has to come from this mod — which is what somebody wants who has
    /// modelled a whole gun and does not want the game's scopes appearing on it.
    /// </summary>
    private void FillWeaponFits(WeaponTab tab, ModCopy copy)
    {
        if (_mod is null) return;

        if (WeaponAuthor.KeepsDonorTag(_mod, copy.Id) is not { } keeps)
        { tab.SetFits("", "", false); return; }

        tab.SetFits(
            keeps ? "It answers to its donor's type" : "It has a type of its own",
            keeps
                ? "So every attachment the donor takes fits this one, and its pieces come out of "
                + "the game's own palettes."
                : "So nothing of the game's fits it — every piece has to come from this mod. "
                + "That is what a whole gun of your own wants.",
            keeps);
    }

    /// <summary>Move it between the donor's type and one of its own.</summary>
    public void SetWeaponFits(WeaponTab tab, bool ownType)
    {
        if (_mod is null || _provider is null || tab.Copy is not { } copy) return;

        var done = WeaponAuthor.SetDonorTag(_provider, _mod, copy.Id, !ownType,
                                            apply: true, out var why);
        if (done is null) { Say($"{copy.Id}: {why ?? "could not change its type"}"); return; }

        foreach (var c in done) Say("   " + c.What);
        _mod.Save();
        Say($"{copy.Id}: {(ownType ? "has a type of its own now" : "answers to its donor's type again")}" +
            $" — {done.Count} change(s)");

        FillWeaponFits(tab, copy);
        FillWeaponAttachments(tab, copy);
        AfterEditsChanged();
    }

    /// <summary>
    /// ITS ATTACHMENTS — which barrels, scopes, cells and stocks this weapon can wear, and which
    /// of them this mod owns a copy of.
    ///
    /// A palette this mod copies is one the author can change; one it does not is the game's, and
    /// changing that would change it for every weapon that shares it.
    /// </summary>
    private void FillWeaponAttachments(WeaponTab tab, ModCopy copy)
    {
        if (_mod is null || _provider is null) return;

        var found = WeaponAuthor.AttachmentsOf(_provider, _mod, copy.Id, out _);

        // TWO OF THESE STATES ARE WARNINGS, and both look like success in the project.
        //
        // SHADOWED is the one that was reported: the mod names a mesh for a palette that already
        // answers to this weapon's tag with the DONOR's mesh, so the added entry sits behind it
        // and the game never reaches it. The build refuses the write; a panel reading the project
        // alone showed "your mesh" and an author shipped twenty rows of it.
        //
        // NOTHING is its mirror: the weapon minted a type tag of its own, so a palette keyed to
        // the donor's answers nothing at all and the slot renders empty.
        tab.SetAttachments(found.Select(a => new SheetTab.AttachRow(
            a.Palette, a.PaletteLeaf,
            a.Slot.Length > 0 ? a.Slot : a.PaletteLeaf,
            a.Style,
            a.State switch
            {
                WeaponAuthor.Wearing.Donor => "the donor's",
                WeaponAuthor.Wearing.Nothing => "nothing renders here — it answers to the donor's type",
                WeaponAuthor.Wearing.Hidden => "hidden by this mod",
                WeaponAuthor.Wearing.Mesh => "your own mesh",
                WeaponAuthor.Wearing.MeshAndSkin => "your own mesh and paint",
                WeaponAuthor.Wearing.Shadowed =>
                    "wired, but the donor's shows instead — give it a type of its own",
                _ => a.State.ToString(),
            },
            Mine: a.State is WeaponAuthor.Wearing.Mesh or WeaponAuthor.Wearing.MeshAndSkin
                           or WeaponAuthor.Wearing.Hidden,
            Warn: a.State is WeaponAuthor.Wearing.Shadowed or WeaponAuthor.Wearing.Nothing)));
    }

    /// <summary>Take a copy of one attachment palette, or give it back to the game's.</summary>
    public void SetWeaponAttachment(WeaponTab tab, SheetTab.AttachRow row, bool own)
    {
        if (_mod is null || _provider is null || tab.Copy is not { } copy) return;

        if (!WeaponAuthor.SetAttachment(_provider, _mod, copy.Id, row.Palette, own, out var why))
        { Say($"{row.Slot}: {why ?? "could not be changed"}"); return; }

        _mod.Save();
        Say($"{copy.Id}: {row.Slot} is {(own ? "this mod's own copy now" : "the game's again")}");
        FillWeaponAttachments(tab, copy);
        AfterEditsChanged();
    }

    /// <summary>Write one number, through the build's own setter.</summary>
    public void SetWeaponStat(WeaponTab tab, SheetTab.StatRow row, string text)
    {
        if (_mod is null || _provider is null || tab.Copy is not { } copy) return;

        if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
        { Say($"{row.Label}: '{text}' is not a number"); return; }

        if (!WeaponAuthor.SetStat(_provider, _mod, copy.Id, row.Row, v, out var why))
        { Say($"{row.Label}: {why ?? "could not be set"}"); return; }

        _mod.Save();
        Say($"{copy.Id}: {row.Label} is {v}");
        FillWeaponStats(tab, copy);
        AfterEditsChanged();
    }
}

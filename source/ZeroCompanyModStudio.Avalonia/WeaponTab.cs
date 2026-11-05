using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE WEAPON, open as a tab.
///
/// THE SHEET IS THE CHAIN. A weapon is not one asset: the Armoury enumerates a gear-kit PART,
/// which names a gear kit, which names a weapon actor, which names a loadout, which names the
/// mesh palettes. Whether a mod can start from one is entirely a question of whether that chain
/// is complete, so the chain IS the sheet rather than a detail buried under it.
///
/// Read from <see cref="WeaponIndex"/>, which the ImGui browser also reads, so the two cannot
/// disagree about a weapon.
/// </summary>
public sealed class WeaponTab : SheetTab
{
    private readonly WeaponIndex.Weapon _w;

    public WeaponTab(WeaponIndex.Weapon weapon)
    {
        _w = weapon;
        _title = weapon.Name.Length > 0 ? weapon.Name : weapon.Key;
    }

    /// <summary>What the copy calls itself, or the game weapon's name when this is not one.</summary>
    public override string Title => Copy is { Display.Length: > 0 } c ? c.Display : _title;

    private readonly string _title;

    public override string Tooltip =>
        Copy is { } copy ? $"{copy.Id} — your own copy of {_w.Key}" : _w.Key;

    /// <summary>The asset name under the title: a copy cooks to its OWN, not the donor's.</summary>
    public override string Subtitle => Copy is { } c ? c.Id : _w.Key;

    public override string Aside => Copy is not null ? $"your own copy of {_w.Name}" : "";

    public WeaponIndex.Weapon Weapon => _w;

    /// <summary>
    /// THE WEAPON COPY THIS TAB IS EDITING, when it is editing one.
    ///
    /// The same seam PartTab.Copy is, and for the same reason: the panel is the same panel, and
    /// what changes is WHERE a change is written. A weapon copy is a ModCopy — one of several,
    /// because a weapon is a chain — and the gear-kit part is the one that stands for the whole.
    /// </summary>
    public ModCopy? Copy { get; private set; }

    public override bool IsCopy => Copy is not null;

    /// <summary>
    /// Only a DUPLICATE is editable. An override of a weapon is a different act with a different
    /// cost and this panel does not write one yet, so offering Edit on the game's own weapon
    /// would be a button that opens a panel whose Save has nowhere to go.
    /// </summary>
    public override bool CanEdit => Copy is not null;

    /// <summary>Its paint is read out of the game after the panel opens.</summary>
    public override bool ReadsTextures => true;

    public void EditingCopy(ModCopy copy)
    {
        Copy = copy;
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(Aside));
        Raise(nameof(HasAside));
        Raise(nameof(Tooltip));
        Raise(nameof(IsCopy));
        Raise(nameof(EditBadge));
        Raise(nameof(CanEdit));
        Raise(nameof(PictureBlurb));
    }

    /// <summary>The copy's own name and id, raised after a save that moved either.</summary>
    public void CopyRenamed()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
    }

    public override string Headline => _w.Class.Length > 0 ? _w.Class : "";

    /// <summary>A weapon's rows are its pieces — base, barrel, stock, scope, cell.</summary>
    public override string MeshesAre => "one per piece it is assembled from";

    /// <summary>
    /// The two states that are not faults of ours, said in the game's own terms.
    ///
    /// NeedsGearKit is a finished weapon the game cannot equip — eight ship that way, and what
    /// stands between one and a player is two small assets. An incomplete chain is usually an
    /// _Enemy variant sharing the player weapon's actor and loadout deliberately, or a lightsaber,
    /// which is not assembled from palettes at all. Neither is broken; both refuse duplication,
    /// and the difference matters to whoever is deciding what to mod.
    /// </summary>
    public override string Problem =>
        _w.NeedsGearKit
            ? "The game has this weapon and cannot equip it: an actor, a loadout and a type part, "
              + "and no gear kit. Two small assets stand between it and a player."
        : !_w.Complete
            ? "Part of this weapon's chain belongs to another weapon, so there is nothing of its "
              + "own to duplicate. Enemy variants and lightsabers are like this by design."
            : "";

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Class", _w.Class);
            Fact("Key", _w.Key);

            blocks.Add(new SheetBand { Text = "The chain" });
            Link("Gear kit part", _w.GearKitPart);
            Link("Gear kit", _w.GearKit);
            Link("Actor", _w.Actor);
            Link("Loadout", _w.Loadout);
            Link("Type part", _w.TypePart);

            if (_w.Missing.Count > 0)
            {
                blocks.Add(new SheetBand
                { Text = $"Missing from the chain ({_w.Missing.Count})", Accent = true });
                foreach (var m in _w.Missing) blocks.Add(new SheetBullet { Text = m });
            }

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }

            // A LINK IS NAMED, OR SAID TO BE ABSENT. An empty row would read as a field nobody
            // filled in; "not in this game" is the answer the chain check actually produced.
            void Link(string label, string package)
            {
                blocks.Add(new SheetPair
                {
                    Left = label,
                    Right = package.Length > 0 ? Leaf(package) : "— none",
                    Band = band,
                });
                band = !band;
            }
        }
    }
}

/// <summary>
/// ONE UTILITY ITEM, open as a tab.
///
/// Grenades and the rest. The chain is shorter than a weapon's but has the same shape and the
/// same failure: a gear kit with an actor, a model and an ability, and no shop ITEM to reach it
/// by, is an item the game has and cannot sell.
/// </summary>
public sealed class UtilityTab : SheetTab
{
    private readonly UtilityIndex.Utility _u;

    public UtilityTab(UtilityIndex.Utility utility)
    {
        _u = utility;
        Title = utility.Name.Length > 0 ? utility.Name : utility.Key;
    }

    public override string Title { get; }
    public override string Tooltip => _u.Item.Length > 0 ? _u.Item : _u.Key;
    public override string Subtitle => _u.Key;
    public override string Description => Strip(_u.Does);
    public UtilityIndex.Utility Utility => _u;

    public override string Headline
    {
        get
        {
            var bits = new List<string>();
            if (_u.Slot.Length > 0) bits.Add(_u.Slot);
            if (_u.Rarity.Length > 0) bits.Add(_u.Rarity);
            if (_u.Buyable) bits.Add("sold in the Armoury");
            return string.Join("   ·   ", bits);
        }
    }

    public override string Problem =>
        _u.NeedsItem
            ? "The game has this one and cannot equip it: a gear kit with an actor, a model and "
              + "an ability, and no shop item to reach it by. One small data asset stands between "
              + "it and a player."
        : !_u.Complete
            ? $"Part of this item's chain is missing ({_u.Missing.Count}), so there is nothing "
              + "complete to duplicate."
            : "";

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Slot", _u.Slot);
            Fact("Rarity", _u.Rarity);
            if (_u.Buy > 0 || _u.Sell > 0) Fact("Price", $"buy {_u.Buy:N0}   ·   sell {_u.Sell:N0}");
            Fact("Type tag", ShortTag(_u.TypeTag));

            blocks.Add(new SheetBand { Text = "The chain" });
            Link("Shop item", _u.Item);
            Link("Gear kit", _u.GearKit);
            Link("Actor", _u.Actor);
            Link("Model", _u.Mesh);
            Link("Ability set", _u.AbilitySet);
            Link("Ability", _u.Ability);
            Link("Attributes", _u.Attributes);

            if (_u.Requires.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Requires ({_u.Requires.Count})" });
                foreach (var t in _u.Requires) blocks.Add(new SheetBullet { Text = ShortTag(t) });
            }

            if (_u.Ignores.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Ignores ({_u.Ignores.Count})" });
                foreach (var t in _u.Ignores) blocks.Add(new SheetBullet { Text = ShortTag(t) });
            }

            if (_u.Missing.Count > 0)
            {
                blocks.Add(new SheetBand
                { Text = $"Missing from the chain ({_u.Missing.Count})", Accent = true });
                foreach (var m in _u.Missing) blocks.Add(new SheetBullet { Text = m });
            }

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }

            void Link(string label, string package)
            {
                blocks.Add(new SheetPair
                {
                    Left = label,
                    Right = package.Length > 0 ? Leaf(package) : "— none",
                    Band = band,
                });
                band = !band;
            }
        }
    }
}

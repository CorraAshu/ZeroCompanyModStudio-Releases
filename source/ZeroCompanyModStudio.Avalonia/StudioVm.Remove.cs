using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// TAKE SOMETHING BACK OUT OF THE MOD.
///
/// His ask, in his words: "So if i want to say delete a part or something i made i can?" — and
/// until now he could not. Everything this Studio does adds to a project or changes what is in
/// one; the only way to take a part out was to close the Studio and edit the JSON, which is the
/// same answer the mod's own details had before they got a panel.
///
/// IT ASKS FIRST, AND IT ASKS BY NAME. There is no undo here — Edit ▸ Undo and Redo are drawn
/// and disabled, because undo would have to be snapshots of the project text and that is not
/// built — so the one act that destroys work says what it is about to destroy, what else that
/// breaks, and waits.
///
/// NOTHING OF THE GAME'S IS TOUCHED. A part, a copy, a mesh copy and a specialization are things
/// the mod ADDS, so removing one leaves nothing behind. An override is the opposite — it is a
/// change to one of the game's own assets, and removing it is what puts the game's own back.
/// The dialog says which of those two it is doing, because they are opposite acts on one menu.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// What removing this row would do, or null when the row is not one of the mod's own.
    ///
    /// SEPARATE FROM DOING IT so the dialog and the act cannot disagree about what is being
    /// removed. The panel has drawn a state nobody read back seven times in this project; a
    /// confirmation built from a second reading of the same row is the eighth waiting to happen.
    /// </summary>
    public sealed record Removal(object Item, string Heading, string Detail, string Warn,
                                 string Aside, string Button, string Said);

    public Removal? WhatRemoving(object? payload)
    {
        if (_mod is null || payload is null) return null;

        const string Cooked =
            "Its cooked file stays in the project folder until the next build, which is harmless " +
            "— nothing reads it.";

        switch (payload)
        {
            case ModPart p:
            {
                var name = p.Display.Length > 0 ? p.Display : p.Id;
                return new Removal(p,
                    $"Remove “{name}” from this mod?",
                    "It is a clothing part your mod adds. The game's own part it was copied from " +
                    "is untouched, and nothing already in the game changes.",
                    Warn: "",
                    Aside: Cooked,
                    Button: "Remove the part",
                    Said: $"removed the part {p.Id}");
            }

            case ModCopy c:
            {
                var name = c.Display.Length > 0 ? c.Display : c.Id;

                // A WEAPON IS FIVE COPIES AND A CHAIN. Removing one link leaves the rest naming
                // an asset the mod no longer ships, which builds, installs, and is a weapon with
                // a hole in it. Named, and counted, rather than discovered from the build.
                var chain = _mod.Copies
                    .Where(x => x.Id.StartsWith(WeaponIndex.GearKitPartPrefix, StringComparison.Ordinal))
                    .Select(x => WeaponAuthor.IdentityOf(x.Id))
                    .FirstOrDefault(who => who is not null &&
                                           WeaponAuthor.NamesTheWeapon(c.Id, who.Value.Id))?.Id;

                var siblings = chain is null ? 0
                    : _mod.Copies.Count(x => !ReferenceEquals(x, c) &&
                                             WeaponAuthor.NamesTheWeapon(x.Id, chain));

                return new Removal(c,
                    $"Remove “{name}” from this mod?",
                    $"It is your own copy of {Leaf(c.Template)}. The game's own asset is " +
                    "untouched.",
                    // TRUE, AND NO MORE THAN TRUE. "the others still name THIS one" would be a
                    // claim about which links reference which, and this does not read that —
                    // what it knows is how many pieces the chain has and that taking one out
                    // leaves a hole. Saying the stronger thing would be the panel describing a
                    // state nobody read back, which is the fault this project keeps hitting.
                    Warn: chain is null ? "" :
                        $"This is one piece of the weapon {chain}, which this mod ships as " +
                        $"{siblings + 1} pieces. Taking one out leaves the rest pointing at an " +
                        "asset the mod no longer ships, so that weapon will build and install " +
                        "with a piece missing.",
                    Aside: Cooked,
                    Button: "Remove the copy",
                    Said: $"removed the copy {c.Id}");
            }

            case ModMesh m:
            {
                var wears = WearersOf(m);
                return new Removal(m,
                    $"Remove the mesh copy of {Leaf(m.Source)}?",
                    "The mod keeps its own copy of this mesh. Removing it takes the copy, your " +
                    "geometry and its paint out of the project — the game's own mesh has never " +
                    "been touched.",
                    Warn: wears.Length > 0
                        ? $"{wears} still points at it, and will fall back to the game's own mesh."
                        : "",
                    Aside: m.MeshFile.Length > 0
                        ? $"{m.MeshFile} stays in ModSource — this removes the entry that points " +
                          "at it, not your file."
                        : Cooked,
                    Button: "Remove the mesh copy",
                    Said: $"removed the mesh copy of {Leaf(m.Source)}");
            }

            case ModSpec s:
            {
                var name = s.Display.Length > 0 ? s.Display : s.Id;
                return new Removal(s,
                    $"Remove the specialization “{name}”?",
                    $"It is your own copy of {Leaf(s.Template)}, with its crest, what it grants " +
                    $"and all {s.RungCount} of its rungs. The game's own is untouched.",
                    Warn: "",
                    Aside: Cooked,
                    Button: "Remove the specialization",
                    Said: $"removed the specialization {s.Id}");
            }

            case ModOverride o:
            {
                // THE OPPOSITE ACT, on the same menu. Everything else here takes something the
                // mod ADDS out of it; this puts one of the GAME's own assets back the way it
                // was. Saying "remove" about both without saying which is which is how somebody
                // deletes their work believing they are undoing a change.
                return new Removal(o,
                    $"Put the game's own {Leaf(o.Target)} back?",
                    "Your mod changes this asset of the game's. Removing the change is not " +
                    "deleting anything you made somewhere else — it puts this one asset back to " +
                    "what the game ships, and your mod stops touching it.",
                    Warn: "",
                    Aside: "",
                    Button: "Put the game's own back",
                    Said: $"stopped changing {Leaf(o.Target)}");
            }

            case ModMission mi:
                return new Removal(mi,
                    $"Remove the mission “{(mi.Id.Length > 0 ? mi.Id : "(unnamed)")}”?",
                    $"It is your own copy of {Leaf(mi.From)}.",
                    Warn: "",
                    Aside: Cooked,
                    Button: "Remove the mission",
                    Said: $"removed the mission {mi.Id}");

            case ModIcon ic:
                return new Removal(ic,
                    $"Remove the icon “{ic.Tag}”?",
                    "It is an image-bank entry your mod defines.",
                    Warn: "",
                    Aside: ic.Image.Length > 0
                        ? $"{ic.Image} stays in ModSource — this removes the entry, not your file."
                        : "",
                    Button: "Remove the icon",
                    Said: $"removed the icon {ic.Tag}");

            case ModEnemyStats es:
                return new Removal(es,
                    $"Remove the enemy stat changes for {Leaf(es.Group)}?",
                    $"All {es.Multipliers.Count} of them. That group of enemies goes back to the " +
                    "numbers the game ships.",
                    Warn: "",
                    Aside: "",
                    Button: "Remove the stat changes",
                    Said: $"removed the enemy stats for {Leaf(es.Group)}");

            default:
                return null;
        }
    }

    /// <summary>
    /// Do it. Returns false and says why when the project no longer holds the thing.
    ///
    /// THE TAB GOES WITH IT. A panel left open on something the project no longer holds is a
    /// panel editing an object nothing will ever save — and the three editors that guard against
    /// it do so by refusing the save, which reads as a broken button rather than as a thing that
    /// is gone.
    /// </summary>
    public bool RemoveFromMod(Removal r)
    {
        if (_mod is null) { Say("open a mod first"); return false; }

        var went = r.Item switch
        {
            ModPart p => _mod.Parts.Remove(p),
            ModCopy c => _mod.Copies.Remove(c),
            ModMesh m => _mod.Meshes.Remove(m),
            ModSpec s => _mod.Specs.Remove(s),
            ModOverride o => _mod.Overrides.Remove(o),
            ModMission mi => _mod.Missions.Remove(mi),
            ModIcon ic => _mod.Icons.Remove(ic),
            ModEnemyStats es => _mod.EnemyStats.Remove(es),
            _ => false,
        };

        if (!went) { Say("this mod no longer holds that — nothing was removed"); return false; }

        foreach (var tab in Tabs.OfType<SheetTab>().Where(t => Edits(t, r.Item)).ToList())
            CloseTab(tab);

        _mod.Save();
        Say(r.Said + " — the game's own is untouched");
        AfterModChanged();
        AfterEditsChanged();
        return true;
    }

    /// <summary>
    /// One of the mod's own things, by any fragment of what the list calls it.
    ///
    /// For the flags that drive this without a mouse. It searches the SAME lists the Data
    /// Explorer draws from under "show only modified", so a check names a row the way the
    /// person clicking it would.
    /// </summary>
    public object? FindMine(string fragment)
    {
        if (_mod is null || fragment.Length == 0) return null;

        bool Hit(params string?[] names) =>
            names.Any(n => n is { Length: > 0 } &&
                           n.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        // IN THE ORDER THE LIST DRAWS THEM, and an override last: its Target is a full game
        // path, so a fragment like "Clo001" matches the override of a part AND the copy of it,
        // and the copy is the thing somebody naming it means.
        return _mod.Parts.FirstOrDefault(p => Hit(p.Id, p.Display)) as object
            ?? _mod.Copies.FirstOrDefault(c => Hit(c.Id, c.Display)) as object
            ?? _mod.Meshes.FirstOrDefault(m => Hit(m.Source, m.MeshFile)) as object
            ?? _mod.Specs.FirstOrDefault(s => Hit(s.Id, s.Display)) as object
            ?? _mod.Missions.FirstOrDefault(mi => Hit(mi.Id, mi.From)) as object
            ?? _mod.Icons.FirstOrDefault(ic => Hit(ic.Tag, ic.Image)) as object
            ?? _mod.EnemyStats.FirstOrDefault(es => Hit(es.Group)) as object
            ?? _mod.Overrides.FirstOrDefault(o => Hit(o.Target));
    }

    /// <summary>Whether this tab is open on that exact object.</summary>
    private static bool Edits(SheetTab tab, object item) => tab switch
    {
        PartTab p => ReferenceEquals(p.Copy, item),
        WeaponTab w => ReferenceEquals(w.Copy, item),
        CopyTab c => ReferenceEquals(c.Copy, item),
        ReplacementTab m => ReferenceEquals(m.Mesh, item),
        OverrideTab o => item is ModOverride ov && o.Target.Equals(ov.Target, StringComparison.OrdinalIgnoreCase),
        SpecTab s => ReferenceEquals(s.Spec, item),
        _ => false,
    };
}

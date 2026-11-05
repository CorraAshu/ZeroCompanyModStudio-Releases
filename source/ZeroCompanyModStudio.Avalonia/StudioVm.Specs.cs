using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// DUPLICATING A SPECIALIZATION, AND EDITING THE COPY.
///
/// A specialization carries no geometry — asking it for a mesh is what labelled all 151 of them
/// broken once. What it has is a name, a crest drawn from an image BANK by tag, who may take it,
/// what it grants outright, and a focus TREE of three lines the recruit climbs.
///
/// <see cref="ModSpec.CopyOf"/> is the whole of the duplicate: everything is carried across,
/// because a copy that dropped the tree would be a copy of the name alone, and the identity tag is
/// KEPT deliberately — an authored spec carrying its template's identity was measured reaching
/// both the picker and the game's own recruit generator.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>The specialisations this project ships.</summary>
    public IReadOnlyList<ModSpec> SpecsInMod => _mod?.Specs ?? [];

    /// <summary>
    /// EVERY CREST THE GAME HAS, as tags with the picture each one draws.
    ///
    /// Read once and kept: it is a walk of the image banks, and the picker can be opened again
    /// and again while somebody decides.
    /// </summary>
    public async Task<List<PickAssetWindow.Candidate>> CrestsAsync()
        => await BankIconsAsync(ImageBanks.ClassFamily);

    /// <summary>
    /// The game's bank icons in one family, as picker candidates — crests for a specialization,
    /// armoury pictures for a weapon.
    ///
    /// ONE READER FOR BOTH. The crest picker was this method with the family written into it, and
    /// a second copy for weapons would be two places for "which banks does the game have" to
    /// drift. Cached per family: reading them opens every bank the game ships.
    /// </summary>
    public async Task<List<PickAssetWindow.Candidate>> BankIconsAsync(string family)
    {
        if (_bankIcons.TryGetValue(family, out var have)) return have;
        if (_provider is null) return [];

        var provider = _provider;
        var found = await Task.Run(() =>
        {
            try
            {
                var files = ImageBanks.Candidates(provider);
                return ImageBanks.Read(provider, files)
                    .Where(x => x.Tag.StartsWith(family, StringComparison.Ordinal))
                    .OrderBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new PickAssetWindow.Candidate(
                        ImageBanks.FamilyLeaf(x.Tag), x.Tag, x.Tag, x.TexturePath))
                    .ToList();
            }
            catch (Exception) { return []; }
        });

        _bankIcons[family] = found;
        return found;
    }

    private readonly Dictionary<string, List<PickAssetWindow.Candidate>> _bankIcons =
        new(StringComparer.Ordinal);

    /// <summary>Every ability, or every gear kit, for the grant pickers.</summary>
    public List<PickAssetWindow.Candidate> Granted(bool gear)
    {
        if (_provider is null) return [];

        try
        {
            return AbilityIndex.Of(_provider,
                        gear ? AbilityIndex.GearKitPrefix : AbilityIndex.AbilityPrefix)
                   .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                   .Select(a => new PickAssetWindow.Candidate(a.Name, a.Folder, a.Package))
                   .ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// Duplicate a specialization into this mod.
    ///
    /// ASKED OF THE ASSET, not of the section: a spec reached from the Data Explorer duplicates
    /// the same way as one reached from the Specs list, and the thing that decides is whether the
    /// package reads as a specialization at all.
    /// </summary>
    public bool DuplicateSpec(PartsCatalog.Entry e)
    {
        if (_mod is null) { Say("open a mod first — a duplicate has to go somewhere"); return false; }
        if (_provider is null) { Say("the game is not mounted yet"); return false; }

        var src = SpecDetails.Read(_provider, e.Part.Path);
        if (src.Error is { Length: > 0 } err)
        { Say($"{e.Label}: could not be read as a specialization — {err}"); return false; }

        // UNIQUE AMONG WHAT THE MOD HOLDS, as a weapon's is: a second copy is a second
        // specialization, not one overwriting the other.
        var id = $"CPD_Spec_{e.Part.Name}";
        var n = 2;
        while (_mod.Specs.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            id = $"CPD_Spec_{e.Part.Name}_{n++}";

        var made = ModSpec.CopyOf(src, id);
        _mod.Specs.Add(made);
        _mod.Save();
        AfterEditsChanged();

        Say($"duplicated {e.Label} as {id} — {made.Tree.Count} line(s), {made.RungCount} rung(s)");
        OpenSpecEditor(src, made);
        return true;
    }

    /// <summary>Open the editor on a specialization this project already holds.</summary>
    public async Task EditSpec(ModSpec spec)
    {
        if (_mod is null) { Say("open a mod first"); return; }
        if (_provider is null) { Say("the game is not mounted yet"); return; }

        if (Tabs.OfType<SpecTab>().FirstOrDefault(
                t => t.Spec.Id.Equals(spec.Id, StringComparison.OrdinalIgnoreCase)) is { } open)
        { Tab = open; return; }

        var provider = _provider;
        var src = await Task.Run(() => SpecDetails.Read(provider, spec.Template));
        if (src.Error is { Length: > 0 } err)
        {
            Say($"{spec.Id}: the specialization this was copied from could not be read — {err}");
            return;
        }

        OpenSpecEditor(src, spec);
    }

    /// <summary>The panel, on a specialization. One route, shared by Duplicate and by opening one.</summary>
    private void OpenSpecEditor(SpecDetails.Spec src, ModSpec spec)
    {
        var tab = new SpecTab(src, spec);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        // WHAT A SPECIALIZATION HAS, and nothing else. It wears no mesh, takes no tint, has no
        // slot and fires no bolts — every one of those cards is absent rather than empty.
        // AND NOTHING ELSE. Until this was declared rather than assumed, a specialization drew
        // Meshes, Icon, Tints, Tags and Advanced — five cards over an asset that carries no
        // geometry at all, which is the very thing this class's own comment warns about.
        tab.ShowOnly(tab.SecIdentity, tab.SecCrest, tab.SecWho, tab.SecGrants, tab.SecFiles);

        tab.EditId = spec.Id;
        tab.EditForWho = spec.ForCharacter;
        tab.EditIdentity = spec.Identity.Length > 0 ? spec.Identity : src.Identity ?? "";
        tab.EditCrest = spec.IconTag;

        tab.BeginEdit(spec.Display.Length > 0 ? spec.Display : src.DisplayName ?? src.Name,
                      spec.Description, "", spec.Icon);

        FillSpecGrants(tab, spec);
        FillSpecRungs(tab, spec);

        NoteMod(tab, null);
        Say($"editing your copy — the game's own {src.Name} is untouched");
    }

    /// <summary>What it hands out from the start, without spending focus.</summary>
    private static void FillSpecGrants(SpecTab tab, ModSpec spec)
        => tab.SetGrants(spec.Abilities.Select(a => new SheetTab.GrantRow(a, isGear: false))
                             .Concat(spec.GearKits.Select(g => new SheetTab.GrantRow(g, isGear: true))));

    /// <summary>The focus tree, flattened to rows that know which line they belong to.</summary>
    private static void FillSpecRungs(SpecTab tab, ModSpec spec)
    {
        var rows = new List<SheetTab.RungRow>();
        for (var li = 0; li < spec.Tree.Count; li++)
            for (var ri = 0; ri < spec.Tree[li].Count; ri++)
                rows.Add(new SheetTab.RungRow(li, ri, spec.Tree[li][ri]));
        tab.SetRungs(rows);
    }

    /// <summary>
    /// Write what the panel holds into the specialization.
    ///
    /// THE ID IS A PLAIN FIELD HERE, unlike a weapon's: a specialization is ONE asset, not a
    /// chain, so nothing else in the project names it and there is nothing to move with it.
    /// </summary>
    public bool SaveSpecEdit(SpecTab tab)
    {
        if (_mod is null) return false;
        var spec = tab.Spec;

        var wrote = new List<string>();

        var name = tab.EditName.Trim();
        if (name.Length > 0 && name != spec.Display) { spec.Display = name; wrote.Add($"called “{name}”"); }

        var said = tab.EditDescription.Trim();
        if (said != spec.Description)
        { spec.Description = said; wrote.Add(said.Length > 0 ? "its own description" : "the template's words back"); }

        var who = tab.EditForWho.Trim();
        if (who != spec.ForCharacter)
        { spec.ForCharacter = who; wrote.Add(who.Length > 0 ? $"only for {who}" : "offered to anyone"); }

        var identity = tab.EditIdentity.Trim();
        if (identity != spec.Identity) { spec.Identity = identity; wrote.Add("its identity tag"); }

        var crest = tab.EditCrest.Trim();
        if (crest != spec.IconTag) { spec.IconTag = crest; wrote.Add("its crest"); }

        var want = tab.EditId.Trim();
        if (want.Length > 0 && !want.Equals(spec.Id, StringComparison.Ordinal))
        {
            if (_mod.Specs.Any(s => !ReferenceEquals(s, spec) &&
                                    s.Id.Equals(want, StringComparison.OrdinalIgnoreCase)))
                Say($"{spec.Id}: this mod already has a specialization called {want}");
            else { spec.Id = want; wrote.Add($"named {want}"); }
            tab.EditId = spec.Id;
        }

        _mod.Save();
        tab.MarkSaved();
        tab.SpecRenamed();
        Say($"{spec.Id}: your copy saved — " +
            (wrote.Count > 0 ? string.Join(", ", wrote) : "as it was"));
        AfterEditsChanged();
        return true;
    }

    /// <summary>
    /// One rung's numbers, written through the row's own entry in the project.
    ///
    /// A number the game ships as ZERO cannot be raised — it takes no space in the file, so there
    /// is nothing to write over. The build says so; this does not pretend otherwise by refusing
    /// the typing, because what an author wants to see is their number and the build's answer.
    /// </summary>
    public void SetRung(SpecTab tab, SheetTab.RungRow row, string which, string text)
    {
        if (_mod is null) return;

        if (!int.TryParse(text, out var v) || v < 0)
        { Say($"{row.Where}: '{text}' is not a whole number"); return; }

        switch (which)
        {
            case "level": row.Rung.MinLevel = v; row.Level = v.ToString(); break;
            case "focus": row.Rung.FocusLevel = v; row.Focus = v.ToString(); break;
            case "cost": row.Rung.FocusCost = v; row.Cost = v.ToString(); break;
            default: return;
        }

        _mod.Save();
        Say($"{tab.Spec.Id}: {row.Where} — {which} is {v}");
        AfterEditsChanged();
    }

    /// <summary>Point one rung at a different ability.</summary>
    public void SetRungAbility(SpecTab tab, SheetTab.RungRow row, string path)
    {
        if (_mod is null) return;

        row.Rung.Ability = path;
        row.Ability = path;
        _mod.Save();
        Say($"{tab.Spec.Id}: {row.Where} grants {row.AbilityLeaf}");
        AfterEditsChanged();
    }

    /// <summary>Change, add or drop one of the things it grants outright.</summary>
    public void SetGrant(SpecTab tab, SheetTab.GrantRow? row, string path, bool gear)
    {
        if (_mod is null) return;
        var spec = tab.Spec;

        var into = gear ? spec.GearKits : spec.Abilities;

        if (row is null)
        {
            if (path.Length == 0) return;
            into.Add(path);
            Say($"{spec.Id}: grants {Leaf(path)}");
        }
        else
        {
            var list = row.IsGear ? spec.GearKits : spec.Abilities;
            var at = list.IndexOf(row.Path);
            if (at < 0) { Say($"{spec.Id}: that entry is not in the project any more"); return; }

            if (path.Length == 0) { list.RemoveAt(at); Say($"{spec.Id}: no longer grants {row.Leafed}"); }
            else { list[at] = path; Say($"{spec.Id}: grants {Leaf(path)} instead of {row.Leafed}"); }
        }

        _mod.Save();
        FillSpecGrants(tab, spec);
        AfterEditsChanged();
    }
}

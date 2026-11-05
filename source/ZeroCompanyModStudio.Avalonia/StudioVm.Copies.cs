using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE TWO GENERIC EDITORS: a duplicated asset, and a mesh this mod keeps its own copy of.
///
/// These are the shapes a project holds that no specialised editor claims. A weapon is a chain of
/// <see cref="ModCopy"/> and has its own panel; a specialization is a <see cref="ModSpec"/> and
/// has its own; a clothing part is a <see cref="ModPart"/> and has had one the longest. What was
/// left over is everything else — and "everything else" is a lot of what his projects actually
/// hold, because a utility item, a weapon's four plumbing links, and every standalone mesh
/// replacement the old studio ever made are all one of these two.
///
/// WHAT MAKES THE COPY EDITOR WORTH HAVING is "what is inside it": every reference the copied
/// asset holds, read out of the asset, each one pointable somewhere else. Everything before this
/// was a from/to box that works perfectly once you already know the exact string and is useless
/// otherwise.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>Every duplicated asset this project ships, weapons and characters included.</summary>
    public IReadOnlyList<ModCopy> AllCopiesInMod => _mod?.Copies ?? [];

    /// <summary>The mesh copies this project ships.</summary>
    public IReadOnlyList<ModMesh> MeshesInMod => _mod?.Meshes ?? [];

    // ------------------------------------------------------------------ a duplicated asset

    /// <summary>
    /// Open the generic editor on a copy.
    ///
    /// A WEAPON GOES TO THE WEAPON EDITOR, because a weapon is five copies that are only correct
    /// as a set and the generic panel would happily rename one of them. The four links underneath
    /// still reach this editor — that is what it is for — and the panel says which weapon they
    /// belong to rather than letting somebody find out from the build.
    /// </summary>
    public async Task EditGenericCopy(ModCopy copy)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        if (WeaponAuthor.IdentityOf(copy.Id) is not null &&
            copy.Id.StartsWith(WeaponIndex.GearKitPartPrefix, StringComparison.Ordinal))
        { await EditWeaponCopy(copy); return; }

        // A CHARACTER IS A COPY, and routed on the one fact that tells it apart: the id. The old
        // Studio forks on exactly the same line. Everything a character needs — a name that lives
        // on a second copy, a hiring-pool row, and a slot for every part they wear — is invisible
        // to the generic editor, which would show them as a duplicated asset with a Display box.
        if (IsCharacter(copy)) { await EditCharacter(copy); return; }

        if (Tabs.OfType<CopyTab>().FirstOrDefault(t => ReferenceEquals(t.Copy, copy)) is { } open)
        { Tab = open; return; }

        var tab = new CopyTab(copy);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        // WHICH WEAPON THIS IS A LINK OF, if any. Asked of WeaponAuthor rather than of the id's
        // spelling: it is the thing that knows how a chain is named, and a second opinion here
        // is how the panel and the build come to disagree about what a name means.
        tab.PartOf = _mod.Copies
            .Where(x => x.Id.StartsWith(WeaponIndex.GearKitPartPrefix, StringComparison.Ordinal))
            .Select(x => WeaponAuthor.IdentityOf(x.Id))
            .FirstOrDefault(who => who is not null &&
                                   WeaponAuthor.NamesTheWeapon(copy.Id, who.Value.Id))
            ?.Id ?? "";

        // WHAT A DUPLICATED ASSET HAS, and nothing else. It wears no mesh of its own, takes no
        // tint and carries no gate tags — those belong to the PART that names it.
        tab.ShowOnly(tab.SecIdentity, tab.SecInside, tab.SecFiles, tab.SecDeeper);

        tab.EditId = copy.Id;
        FillCopyDeepRows(tab, copy);

        // AN ABILITY'S WORDS LIVE IN UIData, not on a UI fragment, so its boxes read and write the
        // copy's own text entries -- see AbilityText, including why the part-shaped fields must
        // stay empty for one.
        if (AbilityText.IsAbility(copy))
        {
            tab.ShowsLongDescription = true;
            tab.EditLongDescription = AbilityText.Get(copy, AbilityText.LongDescription);
            tab.BeginEdit(AbilityText.Get(copy, AbilityText.Name),
                          AbilityText.Get(copy, AbilityText.Description), "", "");
        }
        else tab.BeginEdit(copy.Display, copy.Description, "", "");

        NoteMod(tab, null);
        Say($"editing your copy of {Leaf(copy.Template)} — the game's own is untouched");

        // A UTILITY ITEM GETS THE ONE CARD NOTHING ELSE HERE NEEDS. Everything else about it —
        // its name, its description, what it points at — the plain editor already does; what the
        // plain editor cannot say is that the copy is INVISIBLE until a shop table names it,
        // which is not true of anything else a mod can make.
        if (copy.Id.StartsWith(UtilityIndex.ItemPrefix, StringComparison.Ordinal))
            await FillSellAsync(tab, copy);

        await FillInsideAsync(tab, copy);
    }

    /// <summary>
    /// EVERY REFERENCE THE COPIED ASSET HOLDS, read off the asset itself.
    ///
    /// OFF THE UI THREAD: it opens the template package and walks every export. And through the
    /// plain provider deliberately — <see cref="CopyContents.Read"/> answers "what does this
    /// asset point at", which is a question about the asset as the game has it, not about what a
    /// pristine read would say.
    /// </summary>
    private async Task FillInsideAsync(CopyTab tab, ModCopy copy)
    {
        if (_provider is null)
        { tab.SetInside([]); tab.InsideNote = "the game is not mounted yet"; return; }

        if (copy.Template.Length == 0)
        { tab.SetInside([]); tab.InsideNote = "this copy names nothing to have been copied from"; return; }

        var provider = _provider;
        var template = copy.Template;

        tab.InsideNote = "reading what it points at…";
        var (refs, why) = await Task.Run(() =>
        {
            var found = CopyContents.Read(provider, template, out var w);
            return (found, w);
        });

        if (why is { Length: > 0 })
        { tab.SetInside([]); tab.InsideNote = why; Say($"{copy.Id}: {why}"); return; }

        tab.SetInside(refs.Select(r => new SheetTab.InsideRow(
            r.Property, r.Export, r.Path, r.Package, r.Kind, r.Hard, MineFor(copy, r))));

        tab.InsideNote = refs.Count == 0
            ? "this asset points at nothing — there is nothing here to move"
            : "";

        Say($"{copy.Id}: {refs.Count} reference(s) inside {Leaf(template)}");
    }

    /// <summary>
    /// What this mod already points one reference at, or empty.
    ///
    /// BOTH MAPS. A hard import is re-pointed through RepointImports and a soft path through
    /// RenameNames — a row that looked only at the renames showed a hard import as untouched
    /// however many times it had been set.
    /// </summary>
    private static string MineFor(ModCopy copy, CopyContents.Reference r)
        => copy.RenameNames.TryGetValue(r.Package, out var to) ? to
         : copy.RepointImports.TryGetValue(r.Package, out var into) ? into
         : "";

    /// <summary>
    /// Point one reference at something else.
    ///
    /// WHICH MAP IS THE ROW'S TO SAY, not this method's to guess — see
    /// <see cref="SheetTab.InsideRow.Hard"/>. And BOTH HALVES of a soft path: the package and
    /// the asset name are separate FNames, and rewriting only the first leaves a reference that
    /// resolves to nothing while reading as an edit that took.
    /// </summary>
    public void PointInside(CopyTab tab, SheetTab.InsideRow row, string want)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        want = want.Trim();
        if (want.Length == 0)
        { Say($"{row.Property}: paste a path into the box first, or press Choose"); return; }

        var copy = tab.Copy;

        // "/A/B/C.C" and "/A/B/C" both arrive here — a path copied out of the game reader carries
        // the object half and a path out of the picker does not.
        var package = want.Contains('.') ? want[..want.LastIndexOf('.')] : want;
        var leaf = package[(package.LastIndexOf('/') + 1)..];

        if (package.Equals(row.Package, StringComparison.OrdinalIgnoreCase))
        { RevertInside(tab, row); return; }

        if (row.Hard) copy.RepointImports[row.Package] = package;
        else
        {
            copy.RenameNames[row.Package] = package;
            if (!string.Equals(row.GameLeaf, leaf, StringComparison.Ordinal))
                copy.RenameNames[row.GameLeaf] = leaf;
        }

        _mod.Save();

        row.Mine = package;
        row.Typed = "";
        FillCopyDeepRows(tab, copy);
        Say($"{copy.Id}: {row.Property} now points at {leaf}" +
            (row.Hard ? "   (a hard import — re-pointed, not renamed)" : ""));
        AfterEditsChanged();
    }

    /// <summary>Put one reference back to what the copied asset named.</summary>
    public void RevertInside(CopyTab tab, SheetTab.InsideRow row)
    {
        if (_mod is null) return;
        var copy = tab.Copy;

        // EVERY KEY THAT COULD CARRY IT. A soft path was written as two entries and a hard import
        // as one, and which was used is the row's business — but a row whose Hard flag has since
        // been read differently would otherwise leave half the edit in the project, where it
        // builds and the panel says it does not exist.
        var went = copy.RenameNames.Remove(row.Package);
        went |= copy.RenameNames.Remove(row.GameLeaf);
        went |= copy.RepointImports.Remove(row.Package);

        if (!went) { Say($"{row.Property}: your copy already points at {row.GameLeaf}"); return; }

        _mod.Save();
        row.Mine = "";
        row.Typed = "";
        FillCopyDeepRows(tab, copy);
        Say($"{copy.Id}: {row.Property} is back to {row.GameLeaf}");
        AfterEditsChanged();
    }

    /// <summary>
    /// The two raw maps, in the Advanced card.
    ///
    /// KEPT ALONGSIDE the reference list rather than replaced by it. The list can only offer what
    /// the template actually points AT; a name that appears somewhere the reader does not reach
    /// has nowhere else to be typed, and the build reports a rename that matched nothing, so a
    /// wrong one there is not silent.
    /// </summary>
    private static void FillCopyDeepRows(SheetTab tab, ModCopy copy)
        => tab.SetDeepRows(
            copy.RenameNames.Select(kv => new SheetTab.DeepRow(
                "renames", kv.Key, kv.Value, "everywhere in this copy"))
            .Concat(copy.RepointImports.Select(kv => new SheetTab.DeepRow(
                "re-points", kv.Key, kv.Value, "a hard import"))));

    /// <summary>
    /// The game's own assets that classify as the same kind of thing as this row.
    ///
    /// THE SAME RULE THAT LABELLED THE ROW — <see cref="CopyContents.KindOf"/> — rather than the
    /// index's own AssetKind. The two do not agree: the index knows a package is a DataAsset and
    /// this knows it is an Ability, and offering "every DataAsset" where the row says Ability is
    /// a list of nine thousand things with the fifty useful ones in it.
    /// </summary>
    public List<PickAssetWindow.Candidate> InsideCandidates(string kind)
    {
        if (_index is null) return [];

        return _index.Root.DescendantFiles()
                     .Where(f => f.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                     .Select(f => PropertyEdit.PackageForFile(f.FullPath))
                     .OfType<string>()
                     .Where(pkg => CopyContents.KindOf(pkg).Equals(kind, StringComparison.Ordinal))
                     .Select(pkg => new PickAssetWindow.Candidate(
                         pkg[(pkg.LastIndexOf('/') + 1)..], pkg, pkg))
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    /// <summary>Write the panel's own fields into the copy.</summary>
    public bool SaveCopyEdit(CopyTab tab)
    {
        if (_mod is null) return false;
        var copy = tab.Copy;

        var wrote = new List<string>();

        if (AbilityText.IsAbility(copy))
        {
            AbilityText.Set(copy, AbilityText.Name, tab.EditName, "its name", wrote);
            AbilityText.Set(copy, AbilityText.Description, tab.EditDescription, "its description", wrote);
            AbilityText.Set(copy, AbilityText.LongDescription, tab.EditLongDescription,
                            "its long description", wrote);
        }
        else
        {
            var name = tab.EditName.Trim();
            if (name != copy.Display)
            { copy.Display = name; wrote.Add(name.Length > 0 ? $"called “{name}”" : "no name of its own"); }

            var said = tab.EditDescription.Trim();
            if (said != copy.Description)
            { copy.Description = said; wrote.Add(said.Length > 0 ? "its own description" : "the template's words back"); }
        }

        var want = tab.EditId.Trim();
        if (want.Length > 0 && !want.Equals(copy.Id, StringComparison.Ordinal))
        {
            if (_mod.Copies.Any(c => !ReferenceEquals(c, copy) &&
                                     c.Id.Equals(want, StringComparison.OrdinalIgnoreCase)))
                Say($"{copy.Id}: this mod already has a copy called {want}");
            else if (tab.PartOf.Length > 0)
                // A LINK'S NAME IS PART OF A SET. The weapon's other four copies name this one;
                // renaming it here leaves them pointing at an asset the mod no longer ships,
                // which builds and installs and is a weapon with a hole in it.
                Say($"{copy.Id}: this is one link of {tab.PartOf} and the rest of that weapon " +
                    "names it. Rename it from the weapon's own editor, which moves them together.");
            else { copy.Id = want; wrote.Add($"named {want}"); }
            tab.EditId = copy.Id;
        }

        _mod.Save();
        tab.MarkSaved();
        tab.Renamed();
        Say($"{copy.Id}: your copy saved — " +
            (wrote.Count > 0 ? string.Join(", ", wrote) : "as it was"));
        AfterEditsChanged();
        return true;
    }

    /// <summary>
    /// Take a copy out of this mod.
    ///
    /// THE GAME'S ASSET WAS NEVER TOUCHED, so there is nothing to put back — but a link of a
    /// weapon chain is a different matter, and removing one on its own leaves the rest pointing
    /// at something the mod no longer ships. Said, not refused: an author dismantling a weapon
    /// by hand is doing something legitimate, and the panel's job is to make sure they know.
    /// </summary>
    public void RemoveCopy(CopyTab tab)
    {
        if (_mod is null) return;
        var copy = tab.Copy;

        if (!_mod.Copies.Remove(copy))
        { Say($"{copy.Id}: this mod no longer holds it"); return; }

        _mod.Save();
        CloseTab(tab);

        Say($"removed {copy.Id} from this mod — the game's {Leaf(copy.Template)} was never touched" +
            (tab.PartOf.Length > 0
                ? $". It was one link of {tab.PartOf}, and the rest of that weapon now names an " +
                  "asset this mod does not ship."
                : ""));
        AfterEditsChanged();
    }

    // ------------------------------------------------------------------ a mesh copy

    /// <summary>
    /// Open the editor on a mesh this mod keeps its own copy of.
    ///
    /// Two cards and no others: a mesh carries geometry and paint, and a name, a slot and an icon
    /// belong to the PART that wears it.
    /// </summary>
    public async Task EditReplacement(ModMesh mesh)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        if (Tabs.OfType<ReplacementTab>()
                .FirstOrDefault(t => ReferenceEquals(t.Mesh, mesh)) is { } open)
        { Tab = open; return; }

        var tab = new ReplacementTab(mesh);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        // GEOMETRY AND PAINT, and nothing else. The textures card turns itself on once the
        // materials have been read.
        tab.ShowOnly(tab.SecMeshes);

        // ONE ROW, because a mesh copy is one mesh. The row's "game path" is the asset it copies,
        // which is what an import is measured against — the same meaning it carries on a part.
        tab.SetMeshRows([new SheetTab.MeshRow("this mesh", mesh.Source, mesh.MeshFile, "")]);

        tab.WornBy = WearersOf(mesh);
        tab.BeginEdit("", "", mesh.MeshFile, "");
        tab.Reread();

        // AND HOW IT FITS. A standalone mesh copy is the case the fit card was written for: a
        // weapon piece replaced by an FBX with nothing else on the panel to say whether it is
        // the right size.
        FillFit(tab);

        NoteMod(tab, null);
        Say($"editing your copy of {Leaf(mesh.Source)} — the game's own mesh is untouched");

        await FillReplacementPaintAsync(tab, mesh);
    }

    /// <summary>
    /// WHAT IN THIS MOD POINTS AT THIS MESH COPY, named.
    ///
    /// A copy nothing names builds, installs, and is never loaded — which from the outside looks
    /// exactly like an import that failed. Parts are asked directly; a weapon is asked through
    /// its own mesh entries rather than by opening its loadout, because this runs on the UI
    /// thread and the weapon's answer costs a package read per piece.
    /// </summary>
    private string WearersOf(ModMesh mesh)
    {
        if (_mod is null) return "";

        var who = new List<string>();

        foreach (var p in _mod.Parts)
        {
            if (PackageOf(p.Mesh).Equals(PackageOf(mesh.Source), StringComparison.OrdinalIgnoreCase))
                who.Add(p.Display.Length > 0 ? p.Display : p.Id);
            else if (p.Regions.Any(r => PackageOf(r.Mesh)
                         .Equals(PackageOf(mesh.Source), StringComparison.OrdinalIgnoreCase)))
                who.Add((p.Display.Length > 0 ? p.Display : p.Id) + " (a piece of it)");
        }

        // A WEAPON'S PIECES NAME THEIR MESHES THROUGH THE MOD'S OWN ENTRIES, which is to say
        // through this very list — so a mesh entry that exists at all was made for a weapon
        // piece by WeaponAuthor, and the weapon it belongs to is the one whose tag the copies
        // carry. Cheap, and it does not open a package.
        if (who.Count == 0)
            foreach (var c in _mod.Copies)
            {
                if (WeaponAuthor.IdentityOf(c.Id) is not { } id) continue;
                if (!who.Contains(id.Id)) who.Add(id.Id);
            }

        return string.Join(", ", who.Take(4)) + (who.Count > 4 ? $" and {who.Count - 4} more" : "");
    }

    /// <summary>
    /// The paint surface, on the one mesh this copy carries.
    ///
    /// <see cref="FillPaintAsync"/> unchanged: it takes the mesh package, the thing that holds
    /// the paint, and the piece name — and a mesh copy IS the thing that holds the paint, so it
    /// hands itself in where a part hands in its region and a weapon hands in its ModMesh.
    /// </summary>
    private async Task FillReplacementPaintAsync(ReplacementTab tab, ModMesh mesh)
    {
        if (_provider is null)
        {
            tab.SetTextureRows([]);
            tab.SetMaterialGroups([], canAdd: false);
            return;
        }

        var pkg = PackageOf(mesh.Source);
        if (pkg.Length == 0)
        {
            tab.SetTextureRows([]);
            tab.SetMaterialGroups([], canAdd: false);
            Say("this entry names no mesh to read materials from");
            return;
        }

        await FillPaintAsync(tab, [(pkg, mesh, "")], copyMode: true);
    }

    /// <summary>Read the mesh row and the sheet back after the geometry changed.</summary>
    public void RefillReplacement(ReplacementTab tab)
    {
        tab.SetMeshRows([new SheetTab.MeshRow("this mesh", tab.Mesh.Source, tab.Mesh.MeshFile, "")]);
        tab.Reread();
        FillFit(tab);
    }
}

using System.Text.Json.Nodes;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// DUPLICATING A WEAPON, AND EDITING THE COPY.
///
/// A weapon is not a part and is not duplicated like one. The Armoury enumerates a gear-kit PART,
/// which names a gear kit, which names a weapon actor, which names a loadout, which names the mesh
/// PALETTES a weapon is assembled from — and a copy needs one of each, pointed at the others
/// instead of at the game's. <see cref="WeaponAuthor"/> is what knows that chain; this file asks
/// it and puts the answer on screen.
///
/// THE SAME PANEL. The edit surface lives on <see cref="SheetTab"/> and its markup is bound to
/// SheetTab's own collections, so a weapon tab draws the cards a part tab draws as soon as it
/// fills them. What differs is where a change LANDS: a part copy is a ModPart, a weapon copy is a
/// ModCopy with a ModMesh per piece.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// DUPLICATE A WEAPON INTO THIS MOD — the whole chain, through WeaponAuthor's own expansion.
    ///
    /// Refused where the conversion refuses, in its words: a weapon whose chain is not its own
    /// has nothing to copy, which is every enemy variant and every lightsaber. That is not a
    /// fault in the weapon and the sheet already says so.
    /// </summary>
    public bool DuplicateWeapon(WeaponIndex.Weapon weapon)
    {
        if (_mod is null) { Say("open a mod first — a duplicate has to go somewhere"); return false; }
        if (_provider is null) { Say("the game is not mounted yet"); return false; }

        // UNIQUE AMONG WHAT THE MOD ALREADY HOLDS, so duplicating twice does not collide — the
        // ImGui studio's own rule, and the reason a second copy is a second weapon rather than
        // one silently overwriting the other.
        var id = weapon.Name.Replace("-", "").Replace(" ", "") + "Copy";
        var n = 2;
        while (_mod.Copies.Any(c => c.Id == $"CPD_GK_{weapon.Class}_{id}")) id = $"{id}{n++}";

        // INTO THIS MOD, so every re-point names the folder the build will really use. A spec
        // without it re-points at a folder that does not exist and the chain resolves to nothing.
        var spec = new WeaponAuthor.Spec(weapon, id, _mod, Display: $"{weapon.Name} Copy");
        var json = WeaponAuthor.Expand(_provider, spec, out var why);
        if (json is null) { Say($"{weapon.Describe}: {why ?? "could not duplicate it"}"); return false; }

        foreach (var t in json["tags"]?.AsArray() ?? [])
            if (t?.GetValue<string>() is { Length: > 0 } tag) _mod.Tags.Add(tag);
        foreach (var m in json["meshes"]?.AsArray() ?? [])
            if (m is JsonObject o) _mod.Meshes.Add(ModMesh.From(o));
        foreach (var p in json["parts"]?.AsArray() ?? [])
            if (p is JsonObject o) _mod.Parts.Add(ModPart.From(o));
        foreach (var c in json["copies"]?.AsArray() ?? [])
            if (c is JsonObject o) _mod.Copies.Add(ModCopy.From(o));

        _mod.Save();
        AfterEditsChanged();

        var made = _mod.Copies.FirstOrDefault(x => x.Id == $"CPD_GK_{weapon.Class}_{id}");
        if (made is null)
        {
            // The expansion succeeded and the gear-kit part is not among what it produced. Said
            // rather than swallowed: it would leave a mod holding a chain nothing can edit.
            Say($"duplicated {weapon.Describe}, but its gear-kit part is not in the result — " +
                "nothing to open the editor on");
            return false;
        }

        Say($"duplicated {weapon.Describe} as {id} — give it your own model and name");
        OpenWeaponEditor(weapon, made);
        return true;
    }

    /// <summary>The weapon copies this project ships, for anything that reaches one by name.</summary>
    public IReadOnlyList<ModCopy> WeaponsInMod =>
        _mod is null ? [] : _mod.Copies.Where(c => WeaponAuthor.IdentityOf(c.Id) is not null).ToList();

    /// <summary>
    /// Open the editor on a weapon copy this project already holds.
    ///
    /// THE DONOR IS FOUND BY THE TEMPLATE, as a part copy's is: the copy records the gear kit it
    /// was made from, and the weapon that owns that gear kit is the one whose chain the panel
    /// reads. A copy whose donor the game no longer has is SAID rather than opened half empty.
    /// </summary>
    public async Task EditWeaponCopy(ModCopy copy)
    {
        if (_mod is null) { Say("open a mod first"); return; }
        if (_provider is null) { Say("the game is not mounted yet"); return; }

        if (Tabs.OfType<WeaponTab>().FirstOrDefault(
                t => t.Copy is { } c && c.Id.Equals(copy.Id, StringComparison.OrdinalIgnoreCase))
            is { } open)
        { Tab = open; return; }

        var provider = _provider;
        var all = await Task.Run(() => WeaponIndex.Of(provider));

        var donor = all.FirstOrDefault(
            w => string.Equals(w.GearKitPart, copy.Template, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(w.GearKit, copy.Template, StringComparison.OrdinalIgnoreCase));

        if (donor is null)
        {
            Say($"{copy.Id}: the game no longer has the weapon this was copied from " +
                $"({Leaf(copy.Template)}), so its chain cannot be read");
            return;
        }

        OpenWeaponEditor(donor, copy);
    }

    /// <summary>
    /// The edit panel, on a weapon copy. One route, used by Duplicate and by opening an existing
    /// copy — the same arrangement the part editor settled on.
    /// </summary>
    private void OpenWeaponEditor(WeaponIndex.Weapon donor, ModCopy copy)
    {
        var tab = new WeaponTab(donor);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        tab.EditingCopy(copy);

        // WHAT A WEAPON COPY CAN SAY ABOUT ITSELF. The sections a weapon has NOTHING for are
        // absent rather than empty — the rule every card here follows — so a weapon shows no
        // colours, no gate tags, no slot and no takeovers, because it has none of them.
        // WHAT A WEAPON HAS, declared rather than subtracted. The list this replaces was seven
        // lines turning cards OFF, which is a list that has to grow every time a card is added
        // anywhere — and the one it forgets is a card nobody reports, because a card that should
        // not be there still looks like a feature.
        //
        // Its own four — bolts, shelf, numbers, what fits it — and its icon and textures turn
        // themselves on once the weapon's actor and materials have been read.
        tab.ShowOnly(tab.SecIdentity, tab.SecMeshes, tab.SecIcon, tab.SecFiles);

        tab.EditId = copy.Id;
        tab.BeginEdit(copy.Display.Length > 0 ? copy.Display : donor.Name,
                      copy.Description,
                      "", copy.Icon);

        FillWeaponPieces(tab, copy);
        _ = FillBankIconAsync(tab, copy);

        NoteMod(tab, null);
        Say($"editing your copy — the game's own {donor.Name} is untouched");
    }

    /// <summary>
    /// THE ARMOURY PICTURE, which is a TAG rather than an asset path.
    ///
    /// The gear-kit copy names its icon through a shared image bank, and changing it is a
    /// RE-POINT of the donor's tag -- `RenameNames[donorTag] = chosen` -- which the build already
    /// understands and which is proved in game. So this row writes one of those; it is not a new
    /// mechanism, it is a way of writing the one that exists without knowing it is there.
    ///
    /// Absent when the template names no icon by tag, which is most things. A row that cannot do
    /// anything still reads as a feature.
    /// </summary>
    private async Task FillBankIconAsync(SheetTab tab, ModCopy copy)
    {
        // THE CATALOGUE DECIDES THIS AND IT IS STILL BEING BUILT WHEN THE PANEL OPENS. Asking
        // early answers "this template names no icon by tag" about one that does, and the row
        // is simply absent — so the wait is here rather than a cached "no". The first version
        // cached exactly that and the row never appeared at all.
        if (_catalog.Building)
        {
            var catalog = _catalog;
            await Task.Run(() => { while (catalog.Building) Thread.Sleep(50); });
        }
        FillBankIcon(tab, copy);
    }

    private void FillBankIcon(SheetTab tab, ModCopy copy)
    {
        var donor = DonorIconTag(copy.Template);
        tab.BankIcon = donor ?? "";
        if (donor is null) { tab.BankIconSays = ""; return; }

        var now = copy.RenameNames.TryGetValue(donor, out var to) && to.Length > 0 ? to : donor;
        tab.BankIconSays = now.Equals(donor, StringComparison.OrdinalIgnoreCase)
            ? $"{ImageBanks.FamilyLeaf(now)} — its template's own"
            : $"{ImageBanks.FamilyLeaf(now)} — yours, instead of {ImageBanks.FamilyLeaf(donor)}";
    }

    /// <summary>The tag a template names its icon by, from the catalogue. Null when it names none.</summary>
    private string? DonorIconTag(string template)
    {
        // NOT CACHED UNTIL THE CATALOGUE IS READY. `_catalog` is never null — it is a field
        // initialised at construction — so the old guard could not fire, and an answer taken
        // while it was still building was remembered for the rest of the session.
        if (template.Length == 0 || !_catalog.Ready) return null;
        if (_donorIconTag.TryGetValue(template, out var got)) return got;

        static string PackageOf(string path)
        {
            var slash = path.LastIndexOf('/');
            var dot = path.IndexOf('.', slash + 1);
            return dot > 0 ? path[..dot] : path;
        }

        var want = PackageOf(template);
        var tag = _catalog.Entries
            .FirstOrDefault(e => string.Equals(PackageOf(e.Part.Path), want,
                                               StringComparison.OrdinalIgnoreCase))
            ?.Detail.IconBankTag;
        _donorIconTag[template] = tag;
        return tag;
    }
    private readonly Dictionary<string, string?> _donorIconTag = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Give this copy one of the GAME's bank icons, written as the re-point the build reads.
    ///
    /// Choosing the donor's own tag REMOVES the re-point rather than storing "X becomes X": a
    /// no-op entry would still ship, still be checked, and still be one more thing that can fail
    /// a build for no gain.
    /// </summary>
    public void SetBankIcon(SheetTab tab, ModCopy copy, string tag)
    {
        if (DonorIconTag(copy.Template) is not { } donor) return;

        if (tag.Equals(donor, StringComparison.OrdinalIgnoreCase)) copy.RenameNames.Remove(donor);
        else copy.RenameNames[donor] = tag;

        _mod?.Save();
        FillBankIcon(tab, copy);
        Say($"{copy.Id}: its armoury picture is now {ImageBanks.FamilyLeaf(tag)}");
        AfterEditsChanged();
    }

    /// <summary>
    /// EVERY PIECE THE WEAPON IS ASSEMBLED FROM, as mesh rows.
    ///
    /// A weapon has no mesh of its own: the pieces come from PALETTES keyed by a tag, one per
    /// slot — base, barrel, stock, scope, cell — and each is a copy this mod owns. So the rows
    /// are the slots, and what a row shows is the mesh that slot gives this weapon today.
    ///
    /// The mod's own geometry for a slot is a <see cref="ModMesh"/> matched to the piece by
    /// SOURCE, which is the pairing the ImGui studio makes for the same reason: a weapon part
    /// names no mesh, so the mesh entry is what makes the mod's copy exist.
    /// </summary>
    private void FillWeaponPieces(WeaponTab tab, ModCopy copy)
    {
        if (_mod is null || _provider is null) return;

        var rows = new List<SheetTab.MeshRow>();
        foreach (var piece in WeaponAuthor.PiecesOf(_provider, _mod, copy.Id))
        {
            var mine = _mod.Meshes.FirstOrDefault(
                m => string.Equals(m.Source, piece.Mesh, StringComparison.OrdinalIgnoreCase));

            // A HIDDEN PIECE IS NOT AN EMPTY ONE. Hiding renames the mesh to a name no asset
            // has, inside the mod's own copy of the palette, so the donor keeps its parts — the
            // arrangement somebody wants who modelled their own gun and still wants the game's
            // scopes to fit.
            rows.Add(new SheetTab.MeshRow(
                piece.Slot,
                piece.Mesh,
                mine?.MeshFile ?? "",
                piece.Hidden ? WeaponAuthor.HiddenSuffix : ""));
        }

        tab.SetMeshRows(rows);
        tab.SecMeshes.Present = rows.Count > 0;

        if (rows.Count == 0)
            Say($"{copy.Id}: this copy has no palette of its own for any slot, so there is " +
                "nothing here to change — its model is still the donor's");

        CheckWeaponModel(tab, copy);

        // Underneath the open editor, because it is package loads — one material chain per piece.
        _ = FillWeaponPaintAsync(tab, copy);

        // A WEAPON'S OWN THREE. Each is absent when this weapon has nothing of that kind: no
        // actor copied means no bolts, and a weapon on nobody's shelf means no shelf card.
        FillWeaponBolts(tab, copy);
        FillWeaponShelf(tab, copy);
        FillWeaponStats(tab, copy);
        FillWeaponFits(tab, copy);
        FillWeaponAttachments(tab, copy);
    }

    /// <summary>
    /// EVERY TEXTURE THE WEAPON'S PIECES WEAR, as the same card a part's textures use.
    ///
    /// Painting a piece paints the mod's OWN copy of that mesh, so the weapon this was copied
    /// from keeps its paint and so does every other weapon in the game — including where an
    /// attachment points a slot at the game's palette rather than at this one.
    ///
    /// ONLY PIECES THE MOD OWNS A MESH FOR. Artwork on a piece the weapon still shares with its
    /// donor would be artwork with nowhere to go: the paint goes into the mod's copy, and
    /// without a copy there is nothing to put it in. That is the same reason an import is
    /// refused there.
    /// </summary>
    private async Task FillWeaponPaintAsync(WeaponTab tab, ModCopy copy)
    {
        if (_mod is null || _provider is null) return;

        var meshes = new List<(string Pkg, IMeshAuthoring? Paint, string Piece)>();
        foreach (var piece in WeaponAuthor.PiecesOf(_provider, _mod, copy.Id))
        {
            if (piece.Mesh.Length == 0) continue;
            var pkg = PackageOf(piece.Mesh);
            if (pkg.Length == 0 ||
                meshes.Any(m => m.Pkg.Equals(pkg, StringComparison.OrdinalIgnoreCase))) continue;

            var mine = _mod.Meshes.FirstOrDefault(
                m => string.Equals(m.Source, piece.Mesh, StringComparison.OrdinalIgnoreCase));
            if (mine is null) continue;

            meshes.Add((pkg, mine, piece.Slot));
        }

        if (meshes.Count == 0)
        {
            tab.SetTextureRows([]);
            tab.SetMaterialGroups([], canAdd: false);
            Say($"{copy.Id}: this weapon owns no mesh copy yet, so there is nothing of its own " +
                "to paint — its pieces still wear the donor's artwork");
            return;
        }

        await FillPaintAsync(tab, meshes, copyMode: true);
    }

    /// <summary>
    /// WHETHER THIS WEAPON COPIES EVERY PART ITS MODEL IS MADE OF.
    ///
    /// A weapon is assembled from one part per slot — base, barrel, stock, scope, cell — and each
    /// has to be the mod's OWN copy, keyed to this weapon's tag. A copy missing one shows nothing
    /// in that slot: a receiver floating with no barrel. Weapons duplicated before the tool read
    /// the loadout's slot map came out that way, and re-duplicating one throws away the name, the
    /// picture and the model put in since — so it is repaired in place instead.
    ///
    /// Asked once per fill, not per frame: it reads the donor's loadout and every part it names
    /// out of the containers.
    /// </summary>
    private void CheckWeaponModel(WeaponTab tab, ModCopy copy)
    {
        if (_mod is null || _provider is null) return;

        var plan = WeaponAuthor.CompleteModel(_provider, _mod, copy.Id, apply: false, out var why);
        tab.SetModelGap(plan?.Count ?? 0,
                        plan is null ? why ?? "" : string.Join("   ·   ", plan.Take(4).Select(c => c.What)));

        if (plan is { Count: > 0 })
            Say($"{copy.Id}: {plan.Count} piece(s) of its model are missing — in the game this " +
                "weapon is incomplete. “Complete the model” adds them.");
    }

    /// <summary>
    /// Add the pieces this weapon's model is missing, through WeaponAuthor's own repair.
    ///
    /// It makes the palette COPY and the mesh entry together and keys them to this weapon's tag,
    /// which is why an import refuses to invent an entry on its own.
    /// </summary>
    public void CompleteWeaponModel(WeaponTab tab)
    {
        if (_mod is null || _provider is null || tab.Copy is not { } copy) return;

        var done = WeaponAuthor.CompleteModel(_provider, _mod, copy.Id, apply: true, out var why);
        if (done is null) { Say($"{copy.Id}: {why ?? "could not complete the model"}"); return; }

        foreach (var c in done) Say("   " + c.What);
        _mod.Save();
        Say($"{copy.Id}: added {done.Count} missing piece(s) to the model");

        // Read the pieces again over the project as it now is, rather than patching the rows —
        // the repair adds palette copies AND mesh entries, and both change what a row shows.
        FillWeaponPieces(tab, copy);
        AfterEditsChanged();
    }

    /// <summary>
    /// Write what the panel holds into the weapon copy.
    ///
    /// RENAMING IS NOT A FIELD ASSIGNMENT. A weapon's id is written into every copy of its
    /// chain — the gear kit, the type part, each palette, and the renames inside them — so
    /// changing it goes through <see cref="WeaponAuthor.Rename"/>, which moves all of them or
    /// refuses and says why. Assigning `copy.Id` here would leave a chain naming a weapon that
    /// no longer exists, which builds and installs and shows nothing.
    /// </summary>
    public bool SaveWeaponEdit(WeaponTab tab)
    {
        if (_mod is null || tab.Copy is not { } copy) return false;

        var wrote = new List<string>();

        var name = tab.EditName.Trim();
        if (name.Length > 0 && name != copy.Display)
        {
            copy.Display = name;
            wrote.Add($"called “{name}”");
        }

        var said = tab.EditDescription.Trim();
        if (said != copy.Description)
        {
            copy.Description = said;
            wrote.Add(said.Length > 0 ? "its own description" : "the donor's description back");
        }

        var want = tab.EditId.Trim();
        if (want.Length > 0 && !want.Equals(copy.Id, StringComparison.Ordinal))
        {
            var changes = WeaponAuthor.Rename(_mod, copy.Id, want, apply: true, out var why);
            if (changes is null) Say($"{copy.Id}: not renamed — {why}");
            else
            {
                foreach (var c in changes) Say("   " + c.What);
                wrote.Add($"renamed across {changes.Count} entr(ies)");
            }
            // Whatever the outcome, the box shows what the project now holds rather than what
            // was typed at it — a rename that was refused must not look as though it took.
            tab.EditId = copy.Id;
        }

        _mod.Save();
        tab.MarkSaved();
        tab.CopyRenamed();
        Say($"{copy.Id}: your copy saved — " +
            (wrote.Count > 0 ? string.Join(", ", wrote) : "as it was"));
        AfterEditsChanged();
        return true;
    }

    /// <summary>
    /// PUT AN FBX OVER ONE PIECE of a weapon.
    ///
    /// The mod's own geometry for a slot is a <see cref="ModMesh"/>, not a field on the copy: a
    /// weapon part names no mesh at all — its pieces come from palettes keyed by a tag — so the
    /// mesh ENTRY is what makes the mod's copy of that asset exist, and the palette copy beside
    /// it is what points at it. Matched to the piece by SOURCE, which is the pairing the build
    /// makes too.
    /// </summary>
    public void ImportWeaponPiece(WeaponTab tab, SheetTab.MeshRow row, string file)
    {
        if (_mod is null) { Say("open a mod first — an import has to belong to one"); return; }
        if (tab.Copy is null) { Say("only a copy can wear your own geometry"); return; }

        if (row.GamePath.Length == 0)
        {
            Say($"{row.Variant}: this slot's palette gives the weapon no mesh, so there is " +
                "nothing for a file to stand in for");
            return;
        }

        // THE MESH ENTRY HAS TO EXIST ALREADY, and this must not invent one.
        //
        // An entry is only half of what a piece needs: the entry makes the mod own a copy of
        // that asset, and the PALETTE copy beside it is what points the weapon at the copy.
        // WeaponAuthor.CompleteModel makes both, wired to each other. An entry conjured here
        // would ship a mesh nothing names — the import would look applied in this panel and the
        // weapon would wear the donor's geometry in the game. The ImGui studio refuses the same
        // import in the same words for the same reason.
        var mine = _mod.Meshes.FirstOrDefault(
            m => string.Equals(m.Source, row.GamePath, StringComparison.OrdinalIgnoreCase));
        if (mine is null)
        {
            // SAID PRECISELY, because the reason is not obvious and the usual advice is wrong
            // here. A duplicate makes ONE mesh copy, for the piece named by the convention
            // `SK_<Class>_<Name>_Base_01`, and points that weapon's base palette at it. Every
            // other piece still wears the donor's geometry, and so does the base on a weapon
            // whose palette does not follow the convention — the DC-15A's base palette gives a
            // Foregrip, so none of its five pieces pairs. The ImGui studio has the same gap.
            Say($"{row.Variant}: this mod owns no copy of {Leaf(row.GamePath)}, so an import " +
                "here would ship geometry nothing points at. Duplicating a weapon makes one " +
                "mesh copy — its base — and the other pieces still wear the donor's.");
            return;
        }

        var leaf = ImportArt(file, "Meshes", out var why);
        if (leaf is null) { Say($"{row.Variant}: {why}"); return; }

        mine.MeshFile = leaf;
        TakeDeclaredBasis(mine, leaf);
        _mod.Save();

        row.Replacement = leaf;
        row.Mark();
        _artVersion++;
        Say($"{tab.Copy.Id}: the {row.Variant} now wears {leaf}");
        AfterEditsChanged();
    }

    /// <summary>
    /// Put one piece back to the geometry the donor's palette gives it.
    ///
    /// The imported file stays in ModSource — another piece may point at it, and deleting a
    /// shared file to tidy up after one of them breaks the others silently. The same rule the
    /// part editor's mesh revert follows.
    /// </summary>
    public void RevertWeaponPiece(WeaponTab tab, SheetTab.MeshRow row)
    {
        if (_mod is null || tab.Copy is null) return;

        var mine = _mod.Meshes.FirstOrDefault(
            m => string.Equals(m.Source, row.GamePath, StringComparison.OrdinalIgnoreCase));

        if (mine is null || mine.MeshFile.Length == 0)
        { Say($"{row.Variant}: it already wears the donor's own geometry"); return; }

        mine.MeshFile = "";

        // THE ENTRY STAYS. For a weapon it is not a note about an import — it is what makes the
        // mod's own copy of that mesh EXIST, and the palette copy beside it points at that copy.
        // Removing it because the import went would leave the palette naming an asset the build
        // no longer makes, which is a weapon with a hole in it.
        _mod.Save();

        row.Replacement = "";
        row.Mark();
        _artVersion++;
        Say($"{tab.Copy.Id}: the {row.Variant} is the donor's again");
        AfterEditsChanged();
    }
}

using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A CHARACTER OF THE AUTHOR'S OWN.
///
/// Started from one of the game's 794 customization definitions, which is what makes this an
/// evening's feature rather than a month's: species, rig, face and outfit all arrive with the
/// template, and every one of them can be changed afterwards, one slot at a time, here.
///
/// THREE THINGS ARE WRITTEN, and only the first is obvious:
///   the definition   a ModCopy of the template. Its `Slots` map is the whole of the look card.
///   the name part    a SECOND copy, carrying FirstName / LastName / BackgroundDescription.
///   the offer        a row added to one of the three hiring pools, pointing at the definition.
/// A character missing the third builds, installs, and is never met by anybody — so the panel
/// says so at the top rather than leaving it to be discovered in game.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>A copy is a character when its id says so — the same fact the old Studio routes on.</summary>
    public static bool IsCharacter(ModCopy copy)
        => copy.Id.StartsWith(CharacterPrefix, StringComparison.Ordinal);

    private const string CharacterPrefix = "CD_Char_";

    // ------------------------------------------------------------------ opening one

    /// <summary>Open a character of this mod's own, with both of its cards filled.</summary>
    public async Task EditCharacter(ModCopy copy)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        if (Tabs.OfType<CharacterTab>().FirstOrDefault(t => ReferenceEquals(t.Copy, copy))
            is { } open)
        { Tab = open; return; }

        var tab = new CharacterTab(copy);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        // WHAT A CHARACTER HAS. No mesh, no tint, no gate tags — those belong to the PARTS their
        // slots point at, each of which is one of the game's own and edited on its own page.
        tab.ShowOnly(tab.SecPerson, tab.SecLook, tab.SecFiles, tab.SecDeeper);

        tab.EditId = copy.Id;
        FillCopyDeepRows(tab, copy);
        NoteMod(tab, null);

        // AND INTO EDIT MODE, which is what puts the cards on screen at all: SheetColumn is 4
        // while editing and 2 otherwise, so a tab that never calls this draws its read sheet and
        // nothing else. The first drive of this panel looked entirely correct and had no cards.
        tab.BeginEdit(copy.Display, copy.Description, "", "");

        await FillCharacterAsync(tab);
        await FillLookAsync(tab);

        // AFTER THE SLOTS, both of them: FillPlays reads _charSlots for the enemy kit and the
        // spare slot, and FillRemake reads what the play boxes have turned on.
        await FillPlaysAsync(tab);
        FillRemake(tab);

        Say($"editing {tab.Title} — a character of this mod's own");
    }

    /// <summary>
    /// Who they are: the name and story off the name part, and the row that offers them.
    /// </summary>
    private async Task FillCharacterAsync(CharacterTab tab)
    {
        if (_mod is null) return;
        var copy = tab.Copy;

        tab.SecPerson.Present = true;

        // THE NAME LIVES ONE COPY OVER. Without it their name is the template's, which is a
        // character called whatever the enemy they were started from is called.
        var np = CharacterAuthor.NamePartOf(_mod, copy);
        tab.NamePart = np;

        if (np is null)
        {
            tab.CharNameWarn =
                "No name part of their own is in this mod, so in game they carry the template's " +
                "name. Make them again to get one.";
            tab.CharSay("", "", "");
        }
        else
        {
            tab.CharNameWarn = "";
            tab.CharSay(np.Text.TryGetValue("FirstName", out var f) ? f : "",
                        np.Text.TryGetValue("LastName", out var l) ? l : "",
                        np.Text.TryGetValue("BackgroundDescription", out var s) ? s : "");
        }

        // WHICH ROW OFFERS THEM, asked of CharacterAuthor rather than re-derived here. It is the
        // thing that knows how a pool row names a definition, and a second opinion in the panel
        // is how a screen and a build come to disagree about what is in the mod.
        var row = CharacterAuthor.RowOf(_mod, copy);
        if (row is null)
        {
            tab.CharOffered = "No hiring-pool row offers this character yet.";
            tab.CharOfferedWarn = true;
        }
        else
        {
            // WHICH SHAPE THE ROW IS IN, because it decides whether this mod can live beside
            // another recruit mod. A table of the mod's own is registered ALONGSIDE the game's by
            // the pool registry's search rule; an override REPLACES the game's table, and Unreal
            // mounts one copy of a package, so two such mods erase each other's recruits.
            var held = CharacterAuthor.PoolRows(_mod)
                                      .FirstOrDefault(r => ReferenceEquals(r.Row, row));

            tab.CharOffered = held is { ShippedAs.Length: > 0 }
                ? $"Offered in the {Leaf(held.Pool)} pool as row '{row.Row}', copied from " +
                  $"'{row.From}' for its pawn — through {held.ShippedAs}, a table this mod ships, " +
                  "so it does not fight other recruit mods."
                : $"Offered in {Leaf(held?.Pool ?? "")} as row '{row.Row}', copied from " +
                  $"'{row.From}' for its pawn. That is the GAME's own table, which only one mod " +
                  "can own — make them again to move the row onto a table of this mod's own.";
            tab.CharOfferedWarn = false;
        }

        // THE RIG, because it decides which pawn animates them and is the single most useful
        // fact about a character that is not a name. Off the UI thread: it opens a package.
        if (_provider is { } provider && copy.Template.Length > 0)
        {
            var template = copy.Template;
            var rig = await Task.Run(() =>
            {
                try { return CharacterAuthor.RigOf(provider, template); }
                catch (Exception) { return ""; }
            });
            tab.CharFrom = rig.Length > 0
                ? $"{rig}, which lives on the {CharacterAuthor.PawnFor(rig).ToLowerInvariant()} pawn"
                : "";
        }

        tab.Renamed();
        tab.Raise2(nameof(SheetTab.Sheet));
        tab.Raise2(nameof(SheetTab.Problem));
        tab.Raise2(nameof(SheetTab.HasProblem));
    }

    /// <summary>
    /// Write the name and story back onto the name part.
    ///
    /// ONE SAVE FOR THE THREE OF THEM, because they are one thought. The old studio wrote each
    /// keystroke straight into the copy, which works and gives a person no moment at which they
    /// have decided anything.
    /// </summary>
    public void SaveCharacterName(CharacterTab tab)
    {
        if (_mod is null) return;

        if (tab.NamePart is not { } np)
        { Say($"{tab.Copy.Id}: no name part of their own to write a name onto"); return; }

        np.Text["FirstName"] = tab.CharFirst.Trim();
        np.Text["LastName"] = tab.CharLast.Trim();
        np.Text["BackgroundDescription"] = tab.CharStory;
        _mod.Save();

        tab.CharKept();
        tab.Renamed();
        tab.Raise2(nameof(SheetTab.Sheet));

        var who = $"{tab.CharFirst} {tab.CharLast}".Trim();
        Say(who.Length > 0
                ? $"{tab.Copy.Id}: they are {who} now"
                : $"{tab.Copy.Id}: their name is cleared, so the template's shows in game");
        AfterEditsChanged();
    }

    // ------------------------------------------------------------------ what they look like

    /// <summary>The template's slots, read once per template rather than once per keystroke.</summary>
    private List<SlotEdit.Slot> _charSlots = new();
    private string _charSlotsFor = "";
    private string? _charSlotsWhy;

    /// <summary>
    /// Every slot of the template, and what sits in each one now.
    ///
    /// The template's list is what there is to fill; the author's own choices are an overlay on
    /// top of it, held in the copy's `Slots` map and written into the definition at build time —
    /// not as a rename, which cannot reach a numbered colour.
    /// </summary>
    private async Task FillLookAsync(CharacterTab tab)
    {
        if (_mod is null) return;
        var copy = tab.Copy;

        tab.SecLook.Present = true;

        if (_provider is not { } provider)
        { tab.LookProblem = "the game is not mounted yet"; tab.LookSays([]); return; }

        if (!string.Equals(_charSlotsFor, copy.Template, StringComparison.Ordinal))
        {
            var template = copy.Template;
            var read = await Task.Run(() =>
            {
                var found = SlotEdit.ReadFrom(provider, template, out var why);
                return (found, why);
            });
            _charSlots = read.found;
            _charSlotsWhy = read.why;
            _charSlotsFor = copy.Template;
        }

        if (_charSlotsWhy is not null)
        {
            tab.LookProblem = "The template's slots could not be read: " + _charSlotsWhy;
            tab.LookSays([]);
            return;
        }

        tab.LookProblem = "";
        tab.Bespoke = CharacterAuthor.IsBespoke(_charSlots);
        RefillLook(tab);

        // AND THEN WAIT FOR THE GAME'S PARTS, if they are not there yet. The catalogue starts at
        // mount and takes the better part of a minute; a character opened inside that window
        // would otherwise get a Choose button over an empty list. The card fills itself in when
        // they arrive rather than making anybody press anything again.
        if (_catalog.Ready) return;
        for (var spin = 0; spin < 240 && !_catalog.Ready; spin++) await Task.Delay(250);
        RefillLook(tab);
    }

    /// <summary>
    /// Rebuild the table off slots already read. The "show every slot" box calls this, and so
    /// does every write, because a slot's row has three states and recomputing one by hand is
    /// how a Reset button comes to sit beside a value it no longer describes.
    /// </summary>
    public void RefillLook(CharacterTab tab)
    {
        var copy = tab.Copy;
        var rows = new List<SheetTab.LookRow>();
        var group = "";
        var ready = _catalog.Ready;

        foreach (var s in _charSlots)
        {
            var tail = ShortSlot(s.Tail);
            var plumbing = PlumbingSlots.Contains(tail);
            if (plumbing && !tab.ShowEverySlot) continue;

            var here = tail.Split('.')[0];
            if (!string.Equals(here, group, StringComparison.Ordinal))
            {
                group = here;
                rows.Add(new SheetTab.LookRow { IsHeader = true, Shown = here });
            }

            var mine = copy.Slots.TryGetValue(s.Tag, out var own);
            var shown = mine ? own! : s.Path;

            rows.Add(new SheetTab.LookRow
            {
                Tag = s.Tag,
                Shown = Humanise(tail),
                Tip = TagTooltip(s.Tag),
                Plumbing = plumbing,
                Value = shown.Length > 0 ? SlotEdit.LeafOf(shown) : "",
                Path = shown + (mine
                    ? "   (yours; the template had " +
                      (s.Path.Length > 0 ? SlotEdit.LeafOf(s.Path) : "nothing") + ")"
                    : ""),
                Mine = mine,
                // ONLY A MESH OR A PATTERN MAY BE LEFT BARE. An empty Species or an empty Class
                // is not a character with less on it, it is a character that does not load.
                CanEmpty = !mine && shown.Length > 0 &&
                           (tail.EndsWith(".Mesh", StringComparison.Ordinal) ||
                            tail.EndsWith(".Pattern", StringComparison.Ordinal)),
                Ready = ready,
            });
        }

        tab.LookSays(rows);
        tab.PartsWaiting = !ready;
        tab.SecLook.Count = copy.Slots.Count > 0 ? $"{copy.Slots.Count} changed" : "";
    }

    /// <summary>
    /// What may go in a slot: the parts that SAY they fit it, by tag.
    ///
    /// AND WHEN NONE DO, the parts named like the one in it now, widening the family until there
    /// are enough to choose from — an outfit colour and an eye colour carry no slot tag at all,
    /// so a strict reading offers an empty list for the two slots people most want to change.
    ///
    /// The template's own species is a SCOPE rather than a section break: a face from another
    /// species may not fit the body, and the picker can hold that as a box you untick.
    /// </summary>
    public (List<PickAssetWindow.Candidate> All, HashSet<string> SameSpecies) SlotCandidates(
        SheetTab.LookRow row)
    {
        // NOT A THIN ANSWER. With the catalogue still building this returned the one part already
        // in the slot, which looks exactly like a slot nothing in the game fits.
        if (!_catalog.Ready)
        {
            Say("the game's parts are still being read, so there is nothing to choose from yet");
            return ([], []);
        }

        var parts = _catalog.Entries.Select(e => e.Part).ToList();
        var current = row.Value.Length > 0 ? row.Path.Split("   (")[0] : "";
        var currentPkg = SlotEdit.PackageOf(current);
        var family = SpeciesFolder(currentPkg);

        var tail = row.Tag.StartsWith(SlotEdit.TagPrefix, StringComparison.Ordinal)
            ? row.Tag[SlotEdit.TagPrefix.Length..] : row.Tag;

        var list = parts.Where(p => p.Slots.Contains(tail)).ToList();
        if (list.Count == 0 && current.Length > 0)
        {
            var leaf = SlotEdit.LeafOf(current);
            for (var cut = leaf.LastIndexOf('_'); cut > 0 && list.Count < 8;
                 cut = leaf.LastIndexOf('_', cut - 1))
            {
                var prefix = leaf[..(cut + 1)];
                list = parts.Where(
                    p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        var found = list
            .Select(p => (Leaf: p.Name, Package: AsPackage(p.Path),
                          Same: family.Length == 0 ||
                                p.Path.Contains($"/{family}/", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(x => x.Package, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(x => x.Same)
            .ThenBy(x => x.Leaf, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // WHAT IS IN THE SLOT NOW, even when nothing claims to fit it. A picker that cannot show
        // you the thing you are replacing is a picker you cannot check your answer against.
        if (currentPkg.Length > 0 &&
            !found.Any(x => string.Equals(x.Package, currentPkg, StringComparison.OrdinalIgnoreCase)))
            found.Insert(0, (SlotEdit.LeafOf(current), currentPkg, true));

        var same = new HashSet<string>(found.Where(x => x.Same).Select(x => x.Package),
                                       StringComparer.OrdinalIgnoreCase);

        var all = found.Select(x => new PickAssetWindow.Candidate(
            x.Leaf,
            string.Equals(x.Package, currentPkg, StringComparison.OrdinalIgnoreCase)
                ? "in the slot now"
                : x.Same ? SpeciesFolder(x.Package) is { Length: > 0 } sf ? sf : "fits this slot"
                         : "other species — may not fit this body",
            x.Package)).ToList();

        return (all, same);
    }

    /// <summary>Put a part of the game's into one of their slots.</summary>
    public void SetSlot(CharacterTab tab, SheetTab.LookRow row, string package)
    {
        if (_mod is null) return;

        tab.Copy.Slots[row.Tag] = CharacterAuthor.ObjectPath(package);
        _mod.Save();

        RefillLook(tab);
        tab.SlotNote = $"{row.Shown} is now {SlotEdit.LeafOf(package)}.";
        tab.Raise2(nameof(SheetTab.Sheet));
        Say($"{tab.Copy.Id}: {row.Shown} -> {SlotEdit.LeafOf(package)}");
        AfterEditsChanged();
    }

    /// <summary>Put the template's own back.</summary>
    public void ResetSlot(CharacterTab tab, SheetTab.LookRow row)
    {
        if (_mod is null) return;

        tab.Copy.Slots.Remove(row.Tag);
        _mod.Save();

        RefillLook(tab);
        tab.SlotNote = $"{row.Shown} is the template's again.";
        tab.Raise2(nameof(SheetTab.Sheet));
        Say($"{tab.Copy.Id}: {row.Shown} is the template's again");
        AfterEditsChanged();
    }

    /// <summary>Leave the slot bare. Meshes and patterns only — see RefillLook.</summary>
    public void EmptySlot(CharacterTab tab, SheetTab.LookRow row)
    {
        if (_mod is null) return;

        tab.Copy.Slots[row.Tag] = "";
        _mod.Save();

        RefillLook(tab);
        tab.SlotNote = $"{row.Shown} is empty.";
        tab.Raise2(nameof(SheetTab.Sheet));
        Say($"{tab.Copy.Id}: {row.Shown} emptied");
        AfterEditsChanged();
    }


    // ------------------------------------------------------------------ making one

    /// <summary>Every character the game ships, read once and kept. The picker's whole list.</summary>
    public async Task<List<CharacterIndex.Character>> CharacterTemplatesAsync()
    {
        if (_gameCharacters is not null) return _gameCharacters;
        if (_provider is not { } provider) return [];

        _gameCharacters = await Task.Run(() =>
        {
            try { return CharacterIndex.Definitions(provider); }
            catch (Exception) { return new List<CharacterIndex.Character>(); }
        });
        return _gameCharacters;
    }
    private List<CharacterIndex.Character>? _gameCharacters;

    /// <summary>Every hiring-pool row the game ships. The pools are grouped out of these.</summary>
    public async Task<List<CharacterIndex.Recruit>> GameRecruitsAsync()
    {
        if (_gameRecruits is not null) return _gameRecruits;
        if (_provider is not { } provider) return [];

        _gameRecruits = await Task.Run(() =>
        {
            try { return CharacterIndex.Of(provider); }
            catch (Exception) { return new List<CharacterIndex.Recruit>(); }
        });
        return _gameRecruits;
    }
    private List<CharacterIndex.Recruit>? _gameRecruits;

    /// <summary>
    /// The three pools as choices: a label a person reads, and the row a new one is copied from.
    ///
    /// ONE ROW PER POOL, not one per recruit. What the copy takes from it is the pool it lands in
    /// and the pawn it spawns, and every complete row in a pool agrees about both.
    /// </summary>
    public async Task<List<(string Label, CharacterIndex.Recruit Template)>> PoolChoicesAsync()
    {
        var all = await GameRecruitsAsync();
        var found = new List<(string Label, CharacterIndex.Recruit Template)>();

        foreach (var group in all.Where(r => r.Complete).GroupBy(r => r.PoolLeaf))
        {
            var first = group.First();
            var label = first.PoolLeaf.EndsWith("_Clones", StringComparison.Ordinal) ? "Clones"
                      : first.PoolLeaf.EndsWith("_Droids", StringComparison.Ordinal) ? "Droids"
                      : "The databank";
            found.Add((label, first));
        }

        return found.OrderBy(p => p.Label == "The databank" ? 0 : 1)
                    .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// TAKE ONE OF THE GAME'S 51 RECRUITS INTO THIS MOD, keeping the name the game gives them.
    ///
    /// The row already names both halves of the decision -- the character, and the pool with the
    /// pawn that comes with it -- so this needs no picker at all. It ADDS a row rather than
    /// taking theirs: nobody is displaced, and the original stays in the pool beside the copy.
    ///
    /// Through <see cref="MakeCharacter"/> like everything else. The old Studio's own note for
    /// this path is the reason a duplicate is made at all rather than a row pointed at the game's
    /// definition: a row aimed at the game's own asset gave the author nothing to edit -- "it
    /// never shows in duplicated assets to even let me edit anything" -- so an add makes the same
    /// definition and name part that making a character does.
    /// </summary>
    public async Task DuplicateRecruit(CharacterIndex.Recruit row)
    {
        if (_mod is null) { Say("open a mod first — a duplicate has to go somewhere"); return; }
        if (_provider is not { } provider)
        { Say("the game is not mounted yet, so there is nothing to copy"); return; }
        if (row.Definition.Length == 0)
        { Say($"{row.Describe} has no character to copy"); return; }
        if (!row.Complete)
        { Say($"{row.Describe} points at {row.Missing.Count} thing(s) that are not there, so " +
              "there is nothing complete to copy"); return; }

        // THEIR OWN NAME, which is the whole difference between this and making a character.
        // FallbackFirstName covers a definition whose name part cannot be read -- a nameless
        // recruit would otherwise arrive in the pool as a blank card.
        var (first, last, story) = await Task.Run(() =>
        {
            try { return CharacterAuthor.NameOf(provider, row.Definition); }
            catch (Exception) { return ("", "", ""); }
        });
        if (first.Length == 0 && last.Length == 0)
            first = CharacterAuthor.FallbackFirstName(row.Definition);

        await MakeCharacter(row.Definition, first, last, story, row);
    }

    /// <summary>
    /// ADD SOMEBODY WHO IS NOT IN A POOL AT ALL -- a B2, a Magnaguard, any of the game's 663.
    ///
    /// The same call, with the character chosen rather than taken from a row. The pool row is
    /// what decides the table and the pawn, so it is still the donor even though the character
    /// comes from somewhere else -- and that pairing is exactly what <see cref="Warn"/> checks.
    /// </summary>
    public async Task AddToPool(string definition, CharacterIndex.Recruit pool)
    {
        if (_mod is null) { Say("open a mod first — they have to go somewhere"); return; }
        if (_provider is not { } provider)
        { Say("the game is not mounted yet, so there is nothing to start them from"); return; }

        var (first, last, story) = await Task.Run(() =>
        {
            try { return CharacterAuthor.NameOf(provider, definition); }
            catch (Exception) { return ("", "", ""); }
        });
        if (first.Length == 0 && last.Length == 0)
            first = CharacterAuthor.FallbackFirstName(definition);

        // SAID, NEVER REFUSED. An astromech definition on a humanoid pawn is a droid wearing a
        // person's skeleton, and the author is the one who can decide that is what they meant.
        if (CharacterAuthor.LooksWrong(pool, definition) is { } warn) Say($"careful: {warn}");

        await MakeCharacter(definition, first, last, story, pool);
    }

    /// <summary>
    /// TAKE SOMEBODY'S PLACE IN THE ROSTER -- the row keeps its name and names YOUR character.
    ///
    /// CONFIRMED IN GAME 2026-09-21, and it is not what it used to be. The mod's own pool table is
    /// a copy of the game's, so it already carries the game's rows; re-pointing one THERE takes the
    /// row over and the shared DT_InitialCharacterPool is never touched. Two recruit mods can do
    /// this at once. The old shape overrode the game's table, and only one mod could own that.
    ///
    /// THE DIFFERENCE FROM "add somebody" IS ONE ROW, NOT ONE CHARACTER. Both make the same three
    /// assets -- a definition, a name part, and a page to edit them on. Add keeps the new row and
    /// nobody is displaced; this drops it and re-points an existing one, so the roster is the same
    /// length and the person who was in that slot is no longer offered.
    ///
    /// SAID, NOT REFUSED, twice over: the pawn/character mismatch as everywhere else, and the fact
    /// that this DISPLACES somebody -- which is the part an author will want to undo, so the log
    /// names who it was.
    /// </summary>
    public async Task TakeTheirPlace(string definition, CharacterIndex.Recruit row)
    {
        if (_mod is null) { Say("open a mod first — they have to go somewhere"); return; }
        if (_provider is not { } provider)
        { Say("the game is not mounted yet, so there is nothing to start them from"); return; }

        // BOTH SHAPES ASKED, so a project that took this row over before 2026-09-21 is not given a
        // second re-point the first one silently wins over.
        if (CharacterAuthor.PoolSwaps(_mod).Any(
                s => string.Equals(s.From, row.Definition, StringComparison.OrdinalIgnoreCase)))
        { SayBad($"this mod already takes {row.Describe}'s place"); return; }

        var (first, last, story) = await Task.Run(() =>
        {
            try { return CharacterAuthor.NameOf(provider, definition); }
            catch (Exception) { return ("", "", ""); }
        });
        if (first.Length == 0 && last.Length == 0)
            first = CharacterAuthor.FallbackFirstName(definition);

        if (CharacterAuthor.LooksWrong(row, definition) is { } warn) Say($"careful: {warn}");

        var made = await MakeCharacter(definition, first, last, story, row);
        if (made is null) return;                       // MakeCharacter has already said why

        if (!CharacterAuthor.TakeOver(_mod, made, row, out var why))
        { SayBad(why ?? $"could not put them in {row.Describe}'s place"); return; }

        _mod.Save();
        AfterModChanged();
        AfterEditsChanged();

        var who = $"{first} {last}".Trim();
        Say($"{who} now takes {row.Describe}'s place in {Leaf(row.Pool)} — the row keeps its name " +
            $"and names {made.Id} instead. {(row.Name.Length > 0 ? row.Name : row.Row)} is no " +
            "longer offered while this mod is installed. Through a table this mod ships, so it " +
            "does not fight other recruit mods.");
    }

    /// <summary>
    /// PUT A TAKEN-OVER ROW BACK -- the row names the game's character again.
    ///
    /// The undo half of <see cref="TakeTheirPlace"/>, and the reason it exists rather than being
    /// left to "remove from this mod": the rename lives on the mod's POOL TABLE, and removing that
    /// would take every added row with it. This removes one re-point and nothing else.
    ///
    /// THE CHARACTER STAYS. Whoever was put in that place is still a copy in this mod, now with
    /// nothing pointing at it -- which the build already reports, and which is the right default,
    /// because the author may be about to put them somewhere else. The log says so rather than
    /// leaving them to wonder where their character went.
    /// </summary>
    public void PutRowBack(CharacterIndex.Recruit row)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        if (CharacterAuthor.PoolSwaps(_mod).FirstOrDefault(
                s => string.Equals(s.From, row.Definition, StringComparison.OrdinalIgnoreCase))
            is not { } swap)
        { Say($"this mod does not take {row.Describe}'s place, so there is nothing to put back"); return; }

        if (!CharacterAuthor.Unswap(_mod, row.Definition))
        { SayBad($"could not put {row.Describe} back"); return; }

        _mod.Save();
        AfterModChanged();
        AfterEditsChanged();

        Say($"{row.Describe} is offered again. {Leaf(swap.To)} is still in this mod, with nothing " +
            "pointing at it — put them in somebody else's place, or remove them on the My mods page.");
    }

    /// <summary>The rig a template rides, and whether its body is one piece. Both open packages.</summary>
    public async Task<(string Rig, bool Bespoke)> RigAndBespokeAsync(string template)
    {
        if (_provider is not { } provider) return ("", false);

        return await Task.Run<(string, bool)>(() =>
        {
            try
            {
                var rig = CharacterAuthor.RigOf(provider, template);
                var bespoke = CharacterAuthor.IsBespoke(
                    SlotEdit.ReadFrom(provider, template, out _));
                return (rig, bespoke);
            }
            catch (Exception) { return ("", false); }
        });
    }

    /// <summary>
    /// Make them: the definition, the name part, and the row that offers them.
    ///
    /// ONE PATH INTO THE PROJECT. The dialog calls this and so does the driver flag, because a
    /// test that writes through a second route proves the second route.
    /// </summary>
    /// <returns>
    /// The definition copy it made, so a caller can do something further with it -- which
    /// "take their place" must, because its row surgery happens after Create has minted one.
    /// </returns>
    public async Task<ModCopy?> MakeCharacter(string template, string first, string last,
                                              string story, CharacterIndex.Recruit rowTemplate)
    {
        if (_mod is null) { Say("open a mod first"); return null; }

        // THE PROVIDER IS NEEDED FROM THE FIRST LINE NOW: Create asks it which faction is the
        // player's, so a character made from an enemy template does not stay an enemy.
        if (_provider is not { } provider)
        { Say("the game is not mounted yet, so there is nothing to start them from"); return null; }

        var all = await GameRecruitsAsync();
        if (all.Count == 0) { Say("the hiring pool could not be read"); return null; }

        var nameTemplate = CharacterAuthor.NamePartTemplate(all);
        var made = CharacterAuthor.Create(provider, _mod, all, template, nameTemplate,
                                          rowTemplate, first, last, story, out var why);
        if (made is null) { Say(why ?? "could not make them"); return null; }

        var copy = _mod.Copies.FirstOrDefault(c => c.Id == made.DefinitionId);

        // A DROID-RIGGED ENEMY GOES ON ITS OWN PAWN FROM THE START — the measured recipe. On a
        // hero pawn it stands and moves like a person, because the game picks hero animations by
        // weapon class and has none for this rig. Undone with the box on their own page.
        var ownPawn = false;
        if (copy is not null)
        {
            var mod = _mod;
            var rig = await Task.Run(() =>
            {
                try { return CharacterAuthor.RigOf(provider, template); }
                catch (Exception) { return ""; }
            });

            if (rig.Length > 0 &&
                !rig.Equals("Humanoid", StringComparison.OrdinalIgnoreCase) &&
                !rig.Equals("Astromech", StringComparison.OrdinalIgnoreCase) &&
                CharacterAuthor.EnemyPawnFor(provider, template).Length > 0)
                ownPawn = CharacterAuthor.UseEnemyPawn(provider, mod, copy, true, out _);
        }

        _mod.Save();
        AfterModChanged();
        AfterEditsChanged();

        var who = $"{first} {last}".Trim();
        Say($"{who} is {made.DefinitionId}, with {made.NamePartId} carrying the name and a row " +
            $"'{made.Row}' in {Leaf(made.Pool)}. Their face, outfit and colours are on their page." +
            (ownPawn ? " They spawn on their own enemy pawn, and their Faction is Zero Company's."
                     : ""));

        if (copy is not null) await EditCharacter(copy);
        return copy;
    }

#if DEV
    /// <summary>
    /// --newchar "&lt;template&gt;|&lt;first&gt;|&lt;last&gt;[|&lt;pool&gt;]", the form filled in by a flag.
    ///
    /// PIPES RATHER THAN SEPARATE FLAGS because a name has a space in it and PowerShell splits an
    /// argument list on spaces — the same shape the old Studio's --modnewchar uses.
    /// </summary>
    public async Task MakeCharacterFromKey(string key)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        var bits = key.Split('|');
        if (bits.Length < 2)
        { Say("--newchar wants <template>|<first>|<last>[|<pool>]"); return; }

        var characters = await CharacterTemplatesAsync();
        var template = characters.FirstOrDefault(
            c => c.Name.Contains(bits[0], StringComparison.OrdinalIgnoreCase));
        if (template is null) { Say($"--newchar: no character matches '{bits[0]}'"); return; }

        var pools = await PoolChoicesAsync();
        if (pools.Count == 0) { Say("--newchar: the hiring pool could not be read"); return; }

        // NAMED, OR LET THE RIG DECIDE — exactly as the dialog does, so the flag exercises the
        // choice a person would get rather than a simpler one written for the test.
        var pool = bits.Length > 3
            ? pools.FirstOrDefault(
                p => p.Label.Contains(bits[3], StringComparison.OrdinalIgnoreCase))
            : default;

        if (pool.Template is null)
        {
            var (rig, _) = await RigAndBespokeAsync(template.Package);
            var wants = CharacterAuthor.PawnFor(rig);
            pool = pools.FirstOrDefault(
                p => p.Template.Kind.Contains(wants, StringComparison.OrdinalIgnoreCase));
        }
        if (pool.Template is null) pool = pools[0];

        await MakeCharacter(template.Package, bits[1], bits.Length > 2 ? bits[2] : "", "",
                            pool.Template);
    }
#endif

    // ------------------------------------------------------------------ the slot vocabulary

    /// <summary>
    /// Slots that are not choices: the umbrellas that decide the species and the rig, and the
    /// ones the game fills in at recruitment.
    ///
    /// SHOWN ONLY ON REQUEST, and that is not tidiness. Changing Species without changing Head,
    /// Face, FacialDetails and BodyArt to match is a character that does not load, and filling a
    /// Tactical slot is what broke somebody else's mod.
    /// </summary>
    private static readonly HashSet<string> PlumbingSlots = new(StringComparer.Ordinal)
    {
        "Info", "Info.Name", "Info.DilemmaArchetype", "Class", "Classification", "Appearance",
        "Species", "Appearance.Humanoid.Body", "Appearance.Humanoid.Head",
        "Appearance.Humanoid.Head.Face", "Appearance.Humanoid.FacialDetails",
        "Appearance.Humanoid.BodyArt", "Appearance.Humanoid.BodyArt.Tattoo",
        "Appearance.Humanoid.BodyArt.EyeShadow", "Appearance.Humanoid.BodyArt.EyeLiner",
        "Appearance.Humanoid.BodyArt.Lipstick",
        "Outfit", "Outfit.Helmet", "Outfit.Torso", "Outfit.Arms", "Outfit.Legs", "Outfit.Boots",
        "Specializations", "Specializations.Talent", "Specializations.Weapon",
        "Specializations.Tactical.Primary", "Specializations.Tactical.Secondary",
        "Rig", "Infected",
    };

    /// <summary>"Character.Outfit.Torso.Mesh" -> "Outfit.Torso.Mesh".</summary>
    private static string ShortSlot(string tail) =>
        tail.StartsWith("Character.", StringComparison.Ordinal) ? tail["Character.".Length..] : tail;

    /// <summary>"Appearance.Humanoid.Head.Face.Mesh" -> "Head / Face / Mesh".</summary>
    private static string Humanise(string shortTail) =>
        string.Join(" / ", shortTail.Replace("Appearance.Humanoid.", "").Split('.'));

    /// <summary>
    /// A tag, and what the GAME'S OWN DEVELOPERS wrote about it.
    ///
    /// Only 354 of 7,435 tags carry a DevComment, but the ones that do are the answers to
    /// questions this tool otherwise leaves the author to guess at — including a flat
    /// "DEPRECATED." on twenty-one facts that look entirely live from the outside.
    /// </summary>
    private string TagTooltip(string tag) =>
        _provider is { } p && GameplayTagIndex.Comment(p, tag) is { } note
            ? $"{tag}\n\n{note}"
            : tag;

    /// <summary>".../Characters/Niemoidian/..." -> "Niemoidian"; "" for the shared folders.</summary>
    private static string SpeciesFolder(string package)
    {
        const string marker = "/Characters/";
        var i = package.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var rest = package[(i + marker.Length)..];
        var j = rest.IndexOf('/');
        var folder = j < 0 ? rest : rest[..j];
        return folder.Equals("Humanoid", StringComparison.OrdinalIgnoreCase) ||
               folder.Equals("Common", StringComparison.OrdinalIgnoreCase) ||
               folder.StartsWith("_", StringComparison.Ordinal) ? "" : folder;
    }

    /// <summary>A part's path as a PACKAGE path, whichever form the registry handed out.</summary>
    private static string AsPackage(string path)
        => path.StartsWith("/", StringComparison.Ordinal)
            ? SlotEdit.PackageOf(path)
            : PackageId.PackageNameFor(path, ModBuild.ProjectName) ?? SlotEdit.PackageOf(path);
}

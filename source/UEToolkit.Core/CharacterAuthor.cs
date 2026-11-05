namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Exports;

/// <summary>
/// Changing who a player can recruit.
///
/// ONE OPERATION SO FAR, and it is deliberately the one that needs no new capability: point a
/// roster row at a different character. A pool row names its character and its pawn by SOFT path
/// (see <see cref="CharacterIndex"/>), so the whole edit is a <c>renameNames</c> override on the
/// roster table -- machinery that is built, proven, and was checked against this exact table
/// before this file existed.
///
/// TWO OPERATIONS NOW. <see cref="Swap"/> points an existing row at somebody else, which costs the
/// recruit who was in it; <see cref="Add"/> makes a NEW row, which costs nothing. Add is the one to
/// reach for, and Swap is kept because it is the smaller edit and because a mod that only ever
/// wants a different starting roster has no reason to lengthen it.
///
/// Adding works here and not in the shop tables, and the reason is worth knowing: a roster row
/// names its character by SOFT path, so a copied row can be re-pointed by rewriting two names. A
/// shop row names its item by HARD import, and a copied row shares its template's import entry --
/// so growing that table would need the IMPORT MAP grown too, which is not built. See
/// <see cref="UtilityAuthor.PutOnSale"/>, which is still a takeover for exactly that reason.
///
/// THE PAWN IS LEFT ALONE. A row names both a character and a pawn class, and the two have to
/// agree: an astromech definition in a row that spawns Char_Hero_Humanoid_C is a droid wearing a
/// person's skeleton. Swapping within a kind needs no pawn change. Swapping across one is
/// CAUTIONED, not refused -- see <see cref="LooksWrong"/>, which explains why anything confident
/// enough to refuse would be wrong more often than right.
/// </summary>
public static class CharacterAuthor
{
    /// <summary>One thing a proposed swap would do, for a panel to show before it happens.</summary>
    public sealed record Change(string What, string From, string To);

    /// <summary>
    /// What a swap would do, without doing it.
    ///
    /// Two renames, because a soft object path is TWO names -- the package path and the asset name
    /// -- and moving one of them leaves a reference that resolves to nothing while reading as
    /// edited. The same pair every soft re-point in this codebase writes.
    /// </summary>
    public static List<Change> Plan(CharacterIndex.Recruit row, string target) =>
    [
        new("the roster row points at", CharacterIndex.Recruit.Leaf(row.Definition),
            CharacterIndex.Recruit.Leaf(target)),
        new("its package path", row.Definition, target),
    ];

    /// <summary>
    /// Point a roster row at a different character definition -- THROUGH A TABLE THIS MOD SHIPS.
    ///
    /// CONFIRMED IN GAME 2026-09-21. The mod's own pool table is a COPY of the game's, so it
    /// already carries the game's rows; re-pointing one of them there takes that row over without
    /// the shared <c>DT_InitialCharacterPool</c> being touched at all. The registry resolves a row
    /// by NAME across its sources and the mod's answer is the one the game draws.
    ///
    /// That is why this no longer writes an override. An override REPLACES the game's table, and
    /// Unreal mounts one copy of a package, so two mods doing it erase each other's recruits --
    /// the scene-wide "only one recruitment mod can be active". A copy composes.
    /// See [[zero-company-pool-dataregistry]].
    ///
    /// The row's pawn class is left as it is, so the caller is responsible for choosing a
    /// character of the same kind; <see cref="LooksWrong"/> says when the paths plainly disagree.
    ///
    /// A rename moves a name wherever it appears in that table, so swapping a character that two
    /// rows share would move both. <see cref="SharedBy"/> is how a caller finds that out; it is
    /// reported rather than refused, because the caller may well mean it.
    /// </summary>
    public static ModCopy? Swap(ModProject mod, CharacterIndex.Recruit row, string target,
                                out string? why)
    {
        why = null;
        if (row.Pool.Length == 0) { why = "that recruit is not in a roster table"; return null; }
        if (row.Definition.Length == 0)
        { why = $"{row.Describe} names no character, so there is nothing to re-point"; return null; }
        if (target.Length == 0) { why = "no character was chosen"; return null; }
        if (string.Equals(target, row.Definition, StringComparison.OrdinalIgnoreCase))
        { why = $"{row.Describe} already is that character"; return null; }

        // ALREADY RE-POINTED, IN EITHER SHAPE. A project written before 2026-09-21 put the rename
        // on an override and still builds, so asking only the copy would let a second swap be
        // recorded that the first one silently wins over.
        if (PoolSwaps(mod).Any(s => string.Equals(s.From, row.Definition,
                                                  StringComparison.OrdinalIgnoreCase)))
        { why = $"this mod already re-points {row.Describe}"; return null; }

        // ONE TABLE COPY PER POOL, shared with Add -- so a mod that both adds a recruit and takes
        // somebody's place ships one table, not two claiming the same registry name.
        var id = PoolTableIdFor(mod, row.Pool);
        var table = mod.Copies.FirstOrDefault(
            c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (table is null)
        {
            table = new ModCopy { Id = id, Template = row.Pool };
            mod.Copies.Add(table);
        }

        // A soft object path is TWO names -- the package path and the asset name -- and moving one
        // of them leaves a reference that resolves to nothing while reading as edited.
        table.RenameNames[row.Definition] = target;
        table.RenameNames[CharacterIndex.Recruit.Leaf(row.Definition)] =
            CharacterIndex.Recruit.Leaf(target);
        return table;
    }

    /// <summary>One roster row this mod takes over, and where the edit lives.</summary>
    /// <param name="Pool">The game pool holding the row.</param>
    /// <param name="From">The character the row named before.</param>
    /// <param name="To">The character it names now.</param>
    /// <param name="In">The name map holding it, so a caller can take it back out.</param>
    /// <param name="ShippedAs">The mod's own table it rides in, or "" when it overrides the game's.</param>
    public sealed record PoolSwap(string Pool, string From, string To,
                                  Dictionary<string, string> In, string ShippedAs);

    /// <summary>
    /// Every roster row this mod takes over, IN BOTH SHAPES.
    ///
    /// The same dual reader as <see cref="PoolRows"/> and for the same reason: swaps written
    /// before 2026-09-21 sit on an override of the game's table, those projects still build, and a
    /// reader that knew only the new shape would show such a mod as changing nothing.
    ///
    /// A pool table's name map holds NOTHING BUT SWAPS, which is what makes this readable at all
    /// -- so the full-path key is the swap and its leaf twin is skipped rather than counted again.
    /// </summary>
    public static IEnumerable<PoolSwap> PoolSwaps(ModProject mod)
    {
        foreach (var copy in mod.Copies)
        {
            if (!IsPoolTable(copy.Template)) continue;
            foreach (var (from, to) in copy.RenameNames)
                if (from.StartsWith('/'))
                    yield return new PoolSwap(copy.Template, from, to, copy.RenameNames, copy.Id);
        }

        foreach (var ov in mod.Overrides)
        {
            if (!IsPoolTable(ov.Target)) continue;
            foreach (var (from, to) in ov.RenameNames)
                if (from.StartsWith('/'))
                    yield return new PoolSwap(ov.Target, from, to, ov.RenameNames, "");
        }
    }

    /// <summary>
    /// Put a taken-over row back, in whichever shape it was written.
    ///
    /// Both names go, because half a re-point is a reference that resolves to nothing. An override
    /// left with nothing to do is NOT removed here -- it may carry added rows too, and deciding it
    /// is empty means guessing at every field somebody else's card may have written.
    /// </summary>
    public static bool Unswap(ModProject mod, string definition)
    {
        if (PoolSwaps(mod).FirstOrDefault(
                s => string.Equals(s.From, definition, StringComparison.OrdinalIgnoreCase))
            is not { } swap) return false;

        swap.In.Remove(swap.From);
        // ONLY IF IT STILL NAMES THIS SWAP'S TARGET. Two rows re-pointed at one character share
        // the leaf entry, and taking it out from under the other one breaks a swap nobody touched.
        var leaf = CharacterIndex.Recruit.Leaf(swap.From);
        if (!PoolSwaps(mod).Any(s => string.Equals(CharacterIndex.Recruit.Leaf(s.From), leaf,
                                                   StringComparison.OrdinalIgnoreCase)))
            swap.In.Remove(leaf);
        return true;
    }

    /// <summary>
    /// TAKE A ROSTER ROW OVER WITH A CHARACTER THIS MOD HAS JUST MADE.
    ///
    /// The pair that makes "take their place" mean what it says. <see cref="Create"/> mints the
    /// character AND a row of its own; this takes that row back out and re-points the chosen one
    /// instead, so the character is offered ONCE, in somebody else's place, rather than twice.
    ///
    /// IT POINTS AT A COPY, NOT AT THE GAME'S OWN ASSET, and that is the whole reason Create runs
    /// first. A row aimed straight at a game definition gave the author nothing to work with --
    /// "it never shows in duplicated assets to even let me edit anything" (2026-09-07) -- so the
    /// same three assets an added recruit gets are made here too, and only the row differs.
    ///
    /// The target is a PACKAGE path. A rename maps name to name, and the name table holds the
    /// package and the asset name separately -- an object path would match neither.
    /// </summary>
    public static bool TakeOver(ModProject mod, ModCopy definition, CharacterIndex.Recruit row,
                                out string? why)
    {
        why = null;

        // FOUND BEFORE ANYTHING MOVES, because it is matched on the row's value and the swap is
        // about to rewrite names around it.
        if (RowOf(mod, definition) is { } theirOwn &&
            PoolRows(mod).FirstOrDefault(r => ReferenceEquals(r.Row, theirOwn)) is { } held)
            held.In.Remove(theirOwn);

        if (Swap(mod, row, UtilityAuthor.FolderFor(mod) + definition.Id, out why) is null)
            return false;
        return true;
    }

    /// <summary>
    /// Whether a package -- or a bare copy id -- is one of the hiring-pool tables.
    ///
    /// The GAME's own naming rule, not a guess: the registry registers any DataTable under the
    /// customizations tree whose name ends in <c>InitialCharacterPool</c>, with or without a
    /// <c>_Clones</c> / <c>_Droids</c> suffix. <see cref="PoolTableIdFor"/> is what writes one.
    ///
    /// Public because <see cref="ModBuild"/> needs it too: a pool table is pointed at by NOTHING
    /// in the project on purpose -- the registry finds it by scanning, exactly as it finds a gear
    /// kit or a weapon spec -- so the loose-copy warning has to know to leave it alone.
    ///
    /// IT ASKS THE LEAF, NOT THE PATH, AND THAT IS NOT TIDINESS. Every recruit's name part is
    /// copied from `.../Info/Name/InitialCharacterPool/CPD_H_Name_BaileyBela` -- the FOLDER is
    /// called InitialCharacterPool. Matching the whole path would call every name part a pool
    /// table, and <see cref="PoolSwaps"/> would then read its renames as roster swaps and offer
    /// to undo edits that have nothing to do with the roster.
    /// </summary>
    public static bool IsPoolTable(string package) =>
        SlotEdit.LeafOf(package).Contains("InitialCharacterPool", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ADD a recruit: a new roster row, pointed at a character of your choosing.
    ///
    /// Nobody is displaced. The row is copied from one the game wrote -- which is what carries the
    /// pawn class, the active flag and the timestamps -- and its character reference is rewritten.
    /// See <see cref="DataTableEdit.AddRow"/>, which reads the result back and refuses bytes that
    /// do not come out as the row asked for.
    ///
    /// THE TEMPLATE DECIDES THE PAWN, and that is why it is a parameter rather than "the first row
    /// in the table": a copy of a droid row spawns Char_Hero_Astromech_C whatever character it
    /// names, so a humanoid recruit has to be copied from a humanoid row. The caller picks it.
    /// </summary>
    /// <summary>
    /// The pool table THIS MOD SHIPS AS ITS OWN, for one of the game's pools.
    ///
    /// THE NAME IS THE ENTIRE MECHANISM. `DR_InitialCharacterPool`'s three sources are META
    /// sources: they SEARCH `$AssetSearchRoots/Game/Customizations/` and register every DataTable
    /// matching `*InitialCharacterPool`, `*InitialCharacterPool_Clones*` or
    /// `*InitialCharacterPool_Droids*`. A mod's folder is already inside that tree, so a table
    /// named to match is registered ALONGSIDE the game's rather than replacing it -- which is why
    /// two recruit mods can now be installed together.
    ///
    /// Get the suffix wrong and the table ships, mounts, and is never read by anything.
    /// Confirmed in game 2026-09-20. See [[zero-company-pool-dataregistry]].
    /// </summary>
    public static string PoolTableIdFor(ModProject mod, string poolPackage)
    {
        const string core = "InitialCharacterPool";
        var stem = new string((mod.Name ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (stem.Length == 0) stem = "Mod";

        var leaf = SlotEdit.LeafOf(poolPackage);
        var at = leaf.IndexOf(core, StringComparison.OrdinalIgnoreCase);
        var suffix = at < 0 ? "" : leaf[(at + core.Length)..];
        return $"DT_{stem}{core}{suffix}";
    }

    /// <summary>One pool row this mod adds, and enough to change or remove it.</summary>
    /// <param name="Pool">The game pool it joins.</param>
    /// <param name="Row">The row itself.</param>
    /// <param name="In">The list holding it, so a caller can take it back out.</param>
    /// <param name="ShippedAs">The mod's own table it rides in, or "" when it overrides the game's.</param>
    public sealed record PoolRow(string Pool, ModOverride.RowAdd Row,
                                 List<ModOverride.RowAdd> In, string ShippedAs);

    /// <summary>
    /// Every hiring-pool row this mod adds, IN BOTH SHAPES.
    ///
    /// New ones ride a table of the mod's own; projects written before 2026-09-20 put them on an
    /// override of the game's table, and those still build and still have to be readable. A reader
    /// that knew only one shape would show an older project as having no recruits at all.
    /// </summary>
    public static IEnumerable<PoolRow> PoolRows(ModProject mod)
    {
        foreach (var copy in mod.Copies)
            foreach (var row in copy.AddRows)
                yield return new PoolRow(copy.Template, row, copy.AddRows, copy.Id);

        foreach (var ov in mod.Overrides)
            foreach (var row in ov.AddRows)
                yield return new PoolRow(ov.Target, row, ov.AddRows, "");
    }

    /// <summary>
    /// Offer a character in a hiring pool, through a table THIS MOD SHIPS.
    ///
    /// NOT an override of the game's table any more. That was the only way until the registry's
    /// search rule was measured, and it is why recruit mods were mutually exclusive: Unreal mounts
    /// one copy of a package, so the loser's rows vanished entirely.
    /// </summary>
    public static ModCopy? Add(ModProject mod, CharacterIndex.Recruit template, string target,
                               string rowName, out string? why)
    {
        why = null;
        if (template.Pool.Length == 0) { why = "that recruit is not in a roster table"; return null; }
        if (target.Length == 0) { why = "no character was chosen"; return null; }
        if (rowName.Length == 0) { why = "the new recruit needs a row name"; return null; }

        if (PoolRows(mod).Any(r => string.Equals(r.Row.Row, rowName, StringComparison.OrdinalIgnoreCase)))
        { why = $"this mod already adds a row called '{rowName}'"; return null; }

        var id = PoolTableIdFor(mod, template.Pool);
        var table = mod.Copies.FirstOrDefault(
            c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (table is null)
        {
            table = new ModCopy { Id = id, Template = template.Pool };
            mod.Copies.Add(table);
        }

        var add = new ModOverride.RowAdd { Row = rowName, From = template.Row };
        // The full OBJECT path, which is what a soft reference holds -- "/A/B/C.C", not "/A/B/C".
        add.Set["CustomizationDefinition"] = ObjectPath(target);
        table.AddRows.Add(add);
        return table;
    }

    // ------------------------------------------------------------------ a character of the author's own

    /// <summary>What <see cref="Create"/> made: the ids and paths a panel needs to find it again.</summary>
    public sealed record NewCharacter(string Stem, string DefinitionId, string Definition,
                                      string NamePartId, string NamePart, string Row, string Pool);

    /// <summary>The asset-name stem a name makes: letters and digits only, "Kal Vestra" -> "KalVestra".</summary>
    public static string StemFor(string first, string last) =>
        new string((first + last).Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// The name part to copy for a new character: one of the hiring pool's own.
    ///
    /// A pool character's name part carries exactly two fragments -- the UI data and the three
    /// texts -- and nothing that ties it to a person. A story character's carries a per-character
    /// gameplay tag and an animation proxy table as well, and a copy of that would inherit both.
    /// The pool's shape is the one the game itself uses for anybody it generates.
    /// </summary>
    public static string NamePartTemplate(IReadOnlyList<CharacterIndex.Recruit> all) =>
        all.Select(r => r.NamePart)
           .FirstOrDefault(p => p.Contains("/InitialCharacterPool/", StringComparison.OrdinalIgnoreCase))
        ?? all.Select(r => r.NamePart).FirstOrDefault(p => p.Length > 0) ?? "";

    /// <summary>
    /// MAKE A CHARACTER a mod can ship: a copy of a character definition with its own name part
    /// and a hiring-pool row that offers them.
    ///
    /// Three things, and the reason it is three: the definition is what a row names and what the
    /// game dresses; the name part is where a first name, last name and biography actually live
    /// (the definition only points at it); and the row is what puts them in front of the player.
    /// The in-game databank makes the same three, in a save file nobody can share.
    ///
    /// The definition starts as a copy of <paramref name="templateDefinition"/> -- any of the
    /// game's 663 -- with only Info.Name changed, to the new name part. Everything else about
    /// them is then a slot edit on the copy, which is a panel's job. The row is copied from
    /// <paramref name="rowTemplate"/>, which decides the pool and the pawn.
    /// </summary>
    public static NewCharacter? Create(IFileProvider provider, ModProject mod,
                                       IReadOnlyList<CharacterIndex.Recruit> all,
                                       string templateDefinition, string namePartTemplate,
                                       CharacterIndex.Recruit rowTemplate,
                                       string first, string last, string story, out string? why)
    {
        why = null;
        first = first.Trim(); last = last.Trim(); story = story.Trim();
        if (templateDefinition.Length == 0) { why = "no character was chosen to start from"; return null; }
        if (namePartTemplate.Length == 0) { why = "no name part to copy -- is the hiring pool readable?"; return null; }
        if (rowTemplate.Pool.Length == 0) { why = "that recruit is not in a roster table"; return null; }
        if (first.Length == 0) { why = "a character needs at least a first name"; return null; }

        var stem = StemFor(first, last);
        if (stem.Length == 0) { why = "the name has no letters or digits to make an asset name from"; return null; }

        // Ids nothing in the mod uses AND NOTHING THE GAME ALREADY SHIPS. A number is only ever
        // appended to the STEM, never after an underscore: "CD_Char_Kal2" is a plain name,
        // "CD_Char_Kal_2" is an FName with a number.
        //
        // THE GAME'S NAMES MATTER HERE BECAUSE OF ONE ACTION: duplicating a recruit keeps the name
        // the game gives them, so the stem IS the game's -- "BaileyBela" -- and the name part
        // would come out as `CPD_H_Name_BaileyBela`, which the game already ships. A
        // PrimaryAssetId is (Type, Name) with no path in it, so that is one id claimed twice and
        // the asset manager keeps one; the copy installs and verifies and is never found. The
        // build warns about this, and a default that always trips its own warning is the wrong
        // default. Both ids move together so the pair stays consistent.
        var gameNames = AssetCopy.GameAssetNames(provider);
        var suffix = "";
        for (var n = 2; mod.Copies.Any(c => c.Id.Equals("CD_Char_" + stem + suffix, StringComparison.OrdinalIgnoreCase) ||
                                           c.Id.Equals("CPD_H_Name_" + stem + suffix, StringComparison.OrdinalIgnoreCase)) ||
                        gameNames.Contains("CD_Char_" + stem + suffix) ||
                        gameNames.Contains("CPD_H_Name_" + stem + suffix); n++)
            suffix = n.ToString();
        var definitionId = "CD_Char_" + stem + suffix;
        var namePartId = "CPD_H_Name_" + stem + suffix;
        var folder = UtilityAuthor.FolderFor(mod);
        var definition = folder + definitionId;
        var namePart = folder + namePartId;

        // The row name, unique in its pool and among what this mod already adds.
        var taken = all.Where(r => string.Equals(r.Pool, rowTemplate.Pool, StringComparison.OrdinalIgnoreCase))
                       .Select(r => r.Row)
                       .Concat(PoolRows(mod).Select(r => r.Row.Row))
                       .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var row = stem;
        for (var n = 2; taken.Contains(row); n++) row = stem + n;

        var cd = new ModCopy { Id = definitionId, Template = templateDefinition };
        cd.Slots[SlotEdit.TagPrefix + "Character.Info.Name"] = ObjectPath(namePart);

        // AND THEIR FACTION IS YOURS, for every character, not just the droids.
        //
        // An ENEMY template carries the enemy's faction -- CD_Char_Enemy_Pyke_Enforcer holds
        // CPD_Faction_Pyke -- and a recruit wearing it is a Pyke Syndicate operative sitting in
        // the player's hiring pool. The game does not offer them, and nothing anywhere says why:
        // the row is present, active, and points at a definition that loads.
        //
        // THIS WAS ALWAYS A BUG AND IT TOOK A HUMANOID TO FIND IT. The faction was being fixed
        // only inside UseEnemyPawn, which runs for non-humanoid, non-astromech rigs -- so every
        // character anyone had ever made (all droids) went through it and appeared, and the
        // first humanoid made from an enemy template did not. Measured 2026-09-20: Kal Vestra,
        // a Pyke, absent from the recruitment screen across 25+ refreshes.
        //
        // A databank template already holds the player's faction, so writing it is a no-op there.
        var mine = PlayerFaction(provider);
        if (mine.Length > 0)
            cd.Slots[SlotEdit.TagPrefix + "Character.Info.Faction"] = mine;
        var np = new ModCopy { Id = namePartId, Template = namePartTemplate };
        np.Text["FirstName"] = first;
        np.Text["LastName"] = last;
        np.Text["BackgroundDescription"] = story;

        mod.Copies.Add(cd);
        mod.Copies.Add(np);
        if (Add(mod, rowTemplate, definition, row, out why) is null)
        {
            mod.Copies.Remove(cd);
            mod.Copies.Remove(np);
            return null;
        }
        return new NewCharacter(stem + suffix, definitionId, definition, namePartId, namePart, row, rowTemplate.Pool);
    }

    /// <summary>
    /// The name a game character already has -- first name, last name, story -- read off the name
    /// part its Info.Name slot points at. Blank when there is none or it cannot be read.
    ///
    /// Exists so that duplicating one of the game's characters keeps their name: a BX Commando
    /// arrives as "BX Droid Commando", the way the game says it, instead of the form asking for one.
    /// </summary>
    public static (string First, string Last, string Story) NameOf(IFileProvider provider, string definition)
    {
        try
        {
            var namePart = SlotEdit.ReadFrom(provider, definition, out _)
                .FirstOrDefault(s => s.Tail.Equals("Character.Info.Name", StringComparison.OrdinalIgnoreCase))
                ?.Path ?? "";
            if (namePart.Length == 0 || namePart.Equals("None", StringComparison.OrdinalIgnoreCase))
                return ("", "", "");
            return CharacterIndex.TextsOf(provider, SlotEdit.PackageOf(namePart));
        }
        catch { return ("", "", ""); }
    }

    /// <summary>
    /// A first name for a definition that carries none: the last piece of its asset name, so
    /// CD_Char_Enemy_Separatist_BXCommando gives "BXCommando". Never blank for a non-blank path.
    /// </summary>
    public static string FallbackFirstName(string definition)
    {
        var leaf = CharacterIndex.Recruit.Leaf(definition);
        const string prefix = "CD_Char_";
        var stem = leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? leaf[prefix.Length..] : leaf;
        var bits = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return bits.Length > 0 ? bits[^1] : stem;
    }

    /// <summary>The name-part copy that belongs to a definition copy, by the id rule <see cref="Create"/> uses.</summary>
    public static ModCopy? NamePartOf(ModProject mod, ModCopy definition)
    {
        const string prefix = "CD_Char_";
        if (!definition.Id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var stem = definition.Id[prefix.Length..];
        return mod.Copies.FirstOrDefault(c => c.Id.Equals("CPD_H_Name_" + stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A package path as the OBJECT path a soft reference stores: "/A/B/C" -> "/A/B/C.C".
    ///
    /// Left alone when it already carries one. A row whose reference is written without the asset
    /// half resolves to nothing, and it is the sort of thing that reads correctly in a diff.
    /// </summary>
    public static string ObjectPath(string package)
    {
        if (package.Length == 0) return "";
        var slash = package.LastIndexOf('/');
        return package.IndexOf('.', slash + 1) > 0 ? package : $"{package}.{package[(slash + 1)..]}";
    }

    /// <summary>
    /// A row name for a new recruit that nothing in the table already uses.
    ///
    /// From the character's own asset name, because that is what somebody will look for when they
    /// wonder which row is theirs. Not ending in an underscore and a number -- UE splits a
    /// trailing _N off an FName as a number.
    /// </summary>
    public static string RowNameFor(IReadOnlyList<CharacterIndex.Recruit> all, string pool,
                                    string target)
    {
        var leaf = CharacterIndex.Recruit.Leaf(target);
        var stem = new string(leaf.Where(char.IsLetterOrDigit).ToArray());
        if (stem.Length == 0) stem = "Recruit";
        var taken = all.Where(r => string.Equals(r.Pool, pool, StringComparison.OrdinalIgnoreCase))
                       .Select(r => r.Row).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem)) return stem;
        for (var n = 2; n < 100; n++)
            if (!taken.Contains(stem + n)) return stem + n;
        return stem + "X";
    }

    /// <summary>
    /// How many OTHER roster rows name the same character.
    ///
    /// Measured rather than assumed: the game gives every row its own CD_CharDatabank, so the
    /// answer is zero for everything it ships -- but a rename moves a name wherever it occurs, and
    /// a caller that has not been told the count cannot know whether it is about to move one row
    /// or five.
    /// </summary>
    public static int SharedBy(IReadOnlyList<CharacterIndex.Recruit> all, CharacterIndex.Recruit row) =>
        all.Count(r => r != row &&
                       string.Equals(r.Pool, row.Pool, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(r.Definition, row.Definition, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A sentence about a swap that looks wrong, or null.
    ///
    /// NOT A CLASSIFIER, and it deliberately does not try to be one. A row names a pawn class as
    /// well as a character, and the two have to agree -- an astromech definition on
    /// Char_Hero_Humanoid_C is a droid wearing a person's skeleton. But 663 characters live in
    /// many folders and most of those folders say nothing about a kind, so anything confident
    /// enough to REFUSE would be wrong more often than it was right.
    ///
    /// So this catches the one case the paths do settle -- a droid row taking something from
    /// nowhere near the droid folders -- and says it as a caution the person can overrule. The
    /// row's pawn and the candidate's folder are both shown beside it, which is what actually
    /// lets them decide.
    /// </summary>
    public static string? LooksWrong(CharacterIndex.Recruit row, string target)
    {
        // THE PATH GUESS -- kept only as the fallback for a definition that cannot be read. It
        // waved a B1 through because its folder is "SeparatistDroids", and the B1 was invisible.
        if (!row.Kind.Contains("Astromech", StringComparison.OrdinalIgnoreCase)) return null;
        if (target.Contains("Astromech", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("Droid", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("_CharacterPool/", StringComparison.Ordinal)) return null;
        return "This row spawns an astromech pawn, and that character does not look like a droid. " +
               "It would wear the wrong skeleton.";
    }

    /// <summary>
    /// Whether a definition is a BESPOKE one-piece body -- an enemy's, usually: Appearance is
    /// CPD_Appearance_Bespoke and the outfit a single full-body mesh. CONFIRMED IN GAME 2026-09-06:
    /// the databank shows such a recruit as "NOT EDITABLE -- this operator's profile and
    /// appearance cannot be customized", with empty Headwear and Tops. Not a fault; there is no
    /// face, skin or outfit slot for the in-game editor to work on. The Studio's slot page is
    /// where that character's look changes.
    /// </summary>
    public static bool IsBespoke(IReadOnlyList<SlotEdit.Slot> slots) =>
        slots.Any(s => s.Tail == "Character.Appearance" &&
                       s.Leaf.Contains("Bespoke", StringComparison.OrdinalIgnoreCase)) ||
        slots.Any(s => s.Tail == "Character.Outfit" &&
                       s.Leaf.Contains("Bespoke", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ a droid's own animations, EXPERIMENTAL

    /// <summary>The fragment class that hands a character an animation proxy table.</summary>
    public const string ProxyTableFragment = "BP_CustomizationAnimProxyTableFragment_C";

    /// <summary>
    /// WHY THIS EXISTS. A B1 on the hero pawn stands and moves like a human (in game, 2026-09-06).
    /// Hero animations come from the WEAPON SPEC: the hero Blaster Rifle spec carries a variant
    /// selector that picks an animation set and proxy table by the character's tags -- Kabb's own,
    /// a jetpack variant, the human default -- and has no droid variant. The B1's rifle table
    /// (`PxTable_B1_2HRifle`, on the ENEMY rifle spec) maps the same 41 proxy keys the human
    /// rifle table maps, so the clips exist and speak the hero framework's language; nothing
    /// hands them to a hero. The one per-character animation channel the game shows is a name
    /// part carrying a proxy-table fragment (Bennic's, for his unarmed clips). This copies such a
    /// name part and re-points its table at the rig's rifle table. UNTESTED in game at the time of
    /// writing; rifles only, because that is the only table a droid rig has.
    /// </summary>
    public static string RifleProxyTableFor(IFileProvider provider, string rig)
    {
        if (rig.Length == 0) return "";
        var marker = $"/Anim/Proxy/EnemySpecializations/Droid_{rig}/";
        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!key[(key.LastIndexOf('/') + 1)..].StartsWith("PxTable_", StringComparison.Ordinal)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is { } pkg) return pkg;
        }
        return "";
    }

    /// <summary>The proxy table a part's fragment names, as a package path, or "" when it carries none.</summary>
    public static string ProxyTableOf(IFileProvider provider, string part)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(part)).GetExports().ToList());
            var fragment = exports.FirstOrDefault(e => e.ExportType == ProxyTableFragment);
            if (fragment is null) return "";
            return SlotEdit.PackageOf(PartDetails.SoftPathOf(fragment.Properties, "ProxyTableSoft") ?? "");
        }
        catch { return ""; }
    }

    /// <summary>
    /// A name part that carries a proxy-table fragment, with the table it names. Bennic's is the
    /// one measured; it is tried first, and the rest of the name parts are scanned if it is gone.
    /// </summary>
    public static (string NamePart, string Table) NamePartWithProxyTable(IFileProvider provider)
    {
        var candidates = AssetCopy.PristineFiles(provider)
            .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        k.Contains("/Info/Name/", StringComparison.OrdinalIgnoreCase) &&
                        k[(k.LastIndexOf('/') + 1)..].StartsWith("CPD_H_Name_", StringComparison.Ordinal))
            .OrderBy(k => k.Contains("CPD_H_Name_Bennic.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        foreach (var key in candidates)
        {
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pkg) continue;
            var table = ProxyTableOf(provider, pkg);
            if (table.Length > 0) return (pkg, table);
        }
        return ("", "");
    }

    /// <summary>The per-character name tag a part carries, "" when none: "br.Customization.Part.Character.Info.Name.Bennic".</summary>
    public static string NameTagOf(IFileProvider provider, string part)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(part)).GetExports().ToList());
            foreach (var e in exports.Where(e => e.ExportType == "CustomizationFragmentGameplayTags"))
                foreach (var t in PartDetails.TagsOf(e.Properties, "GameplayTags"))
                    if (t.StartsWith(PartsIndex.NamePrefix, StringComparison.Ordinal)) return t;
        }
        catch { }
        return "";
    }

    /// <summary>Whether a name-part copy has been given a rig's table this way.</summary>
    public static bool UsesRigAnimations(ModCopy namePart) =>
        namePart.RenameNames.Keys.Any(k => k.Contains("PxTable_", StringComparison.Ordinal));

    /// <summary>
    /// Turn the experiment on or off for a character. ON: the name-part copy takes the
    /// proxy-carrying template, its table is re-pointed at the rig's rifle table, and the
    /// template's own name tag becomes one of the mod's, declared. OFF: back to the pool's plain
    /// template with nothing renamed. The texts (name, story) apply to either template.
    /// </summary>
    public static bool UseRigAnimations(IFileProvider provider, ModProject mod,
                                        IReadOnlyList<CharacterIndex.Recruit> all, ModCopy definition,
                                        bool on, out string? why)
    {
        why = null;
        var np = NamePartOf(mod, definition);
        if (np is null) { why = "this character has no name part of its own in the mod"; return false; }

        if (!on)
        {
            np.Template = NamePartTemplate(all);
            np.RenameNames.Clear();
            return true;
        }

        var rig = RigOf(provider, definition.Template);
        var table = RifleProxyTableFor(provider, rig);
        if (table.Length == 0) { why = $"the game ships no rifle animation table for a {rig} rig"; return false; }
        var (carrier, carried) = NamePartWithProxyTable(provider);
        if (carrier.Length == 0) { why = "no name part in the game carries an animation table to copy"; return false; }

        np.Template = carrier;
        np.RenameNames.Clear();
        np.RenameNames[carried] = table;
        np.RenameNames[SlotEdit.LeafOf(carried)] = SlotEdit.LeafOf(table);

        // The carrier's own name tag would make this character Bennic for every tag-driven
        // choice. It becomes a tag of the mod's own, declared, so it exists on other machines.
        var was = NameTagOf(provider, carrier);
        if (was.Length > 0)
        {
            var stem = definition.Id.StartsWith("CD_Char_", StringComparison.Ordinal) ? definition.Id["CD_Char_".Length..] : definition.Id;
            var mine = PartsIndex.NamePrefix + stem;
            np.RenameNames[was] = mine;
            if (!mod.Tags.Contains(mine)) mod.Tags.Add(mine);
        }
        return true;
    }

    /// <summary>
    /// SECOND EXPERIMENT. In game (2026-09-07 ~01:00) the name-part table half-worked: the B1
    /// flickers into his own clips and back, because the animation SET still comes from the hero
    /// weapon spec the roll gave him. The only parts carrying a B1 animation set are the B1's own
    /// enemy weapon spec, which the roll throws out of Specializations.Weapon. So: keep that spec
    /// in a SPARE slot the roll never touches, and let its fragments apply from there. Untested:
    /// whether the game applies a part's fragments from a slot it was not meant for; and the
    /// spec's other fragments come too -- enemy abilities, attributes, and a DefaultPart gear kit
    /// (the E-5), which may clash with the rolled weapon or may be exactly what was wanted.
    /// </summary>
    public static readonly string[] SpareSlots =
    {
        TagPrefixOf("Character.Appearance.Humanoid.Body.Height"),
        TagPrefixOf("Character.Info.DilemmaArchetype"),
        TagPrefixOf("Character.Specializations.Weapon.GearKit"),
    };

    private static string TagPrefixOf(string tail) => SlotEdit.TagPrefix + tail;

    /// <summary>The spare slot this definition's copy would use: the first of <see cref="SpareSlots"/> the template has EMPTY, or "".</summary>
    public static string SpareSlotFor(IReadOnlyList<SlotEdit.Slot> templateSlots) =>
        SpareSlots.FirstOrDefault(tag => templateSlots.Any(s => s.Tag == tag && s.Path.Length == 0)) ?? "";

    /// <summary>The weapon spec the template names, "" when none.</summary>
    public static string WeaponSpecOf(IReadOnlyList<SlotEdit.Slot> templateSlots) =>
        templateSlots.FirstOrDefault(s => s.Tail == "Character.Specializations.Weapon")?.Path ?? "";

    /// <summary>Whether a definition copy carries a weapon spec in one of the spare slots.</summary>
    public static bool UsesRigWeaponSpec(ModCopy definition) =>
        SpareSlots.Any(tag => definition.Slots.TryGetValue(tag, out var v) &&
                              v.Contains("WeaponSpec", StringComparison.OrdinalIgnoreCase));

    public static bool UseRigWeaponSpec(IReadOnlyList<SlotEdit.Slot> templateSlots, ModCopy definition,
                                        bool on, out string? why)
    {
        why = null;
        if (!on)
        {
            foreach (var tag in SpareSlots)
                if (definition.Slots.TryGetValue(tag, out var v) &&
                    v.Contains("WeaponSpec", StringComparison.OrdinalIgnoreCase))
                    definition.Slots.Remove(tag);
            return true;
        }
        var spec = WeaponSpecOf(templateSlots);
        if (spec.Length == 0) { why = "the template names no weapon spec of its own to carry"; return false; }
        var spare = SpareSlotFor(templateSlots);
        if (spare.Length == 0) { why = "the template has no empty spare slot to carry it in"; return false; }
        definition.Slots[spare] = spec;   // replaces a tactical spec riding there: one spare slot
        return true;
    }

    /// <summary>The tactical spec the template names, "" when none.</summary>
    public static string TacticalSpecOf(IReadOnlyList<SlotEdit.Slot> templateSlots) =>
        templateSlots.FirstOrDefault(s => s.Tail == TacticalSlot)?.Path ?? "";

    /// <summary>Whether a definition copy carries a tactical spec in one of the spare slots.</summary>
    public static bool UsesRigTacticalSpec(ModCopy definition) =>
        SpareSlots.Any(tag => definition.Slots.TryGetValue(tag, out var v) &&
                              v.Contains("TacticalSpec", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Carry the rig's own ENEMY tactical spec in a spare slot, for a pool recruit's action points.
    ///
    /// MEASURED 2026-09-07 15:00 from the encounter autosave of a fresh pool B1 with no action
    /// points: his Class slot held the game's enemy class part (no ability set on it), his
    /// Tactical.Primary slot held the ROLLED hero spec CPD_TacticalSpec_Scoundrel (attribute sets
    /// only, no ability set either), and the enemy weapon spec in the spare slot carries an EMPTY
    /// ability-set fragment. Nothing on him granted AbilitySet_BasicCharacterDefaults, which is
    /// where move, turn and action points come from; the only parts that carry it are an enemy's
    /// tactical spec and the hero class part, and the hiring roll replaces both. The one fresh BX
    /// who did have action points (14:00) had our legal tactical copy in the slot -- the roll can
    /// pick it, since it is legal, and that day it did. Luck, not a route.
    ///
    /// The slots the roll left alone in that save: Name, Faction, the spare Body.Height slot, the
    /// outfit, and the weapon the player equipped. So the tactical spec goes into the spare slot,
    /// where its ability-set fragment (BasicCharacterDefaults + the rig's class set) rides along
    /// untouched. Untested whether the game applies a part's fragments from a slot it was not
    /// meant for -- the same question the weapon-spec experiment asked and never answered, because
    /// that spec had nothing to apply. Mutually exclusive with the weapon spec: one spare slot.
    /// </summary>
    public static bool UseRigTacticalSpec(IReadOnlyList<SlotEdit.Slot> templateSlots, ModCopy definition,
                                          bool on, out string? why)
    {
        why = null;
        foreach (var tag in SpareSlots)
            if (definition.Slots.TryGetValue(tag, out var v) &&
                (v.Contains("TacticalSpec", StringComparison.OrdinalIgnoreCase) ||
                 (on && v.Contains("WeaponSpec", StringComparison.OrdinalIgnoreCase))))
                definition.Slots.Remove(tag);
        if (!on) return true;
        var spec = TacticalSpecOf(templateSlots);
        if (spec.Length == 0) { why = "the template names no tactical spec of its own to carry"; return false; }
        var spare = SpareSlotFor(templateSlots);
        if (spare.Length == 0) { why = "the template has no empty spare slot to carry it in"; return false; }
        definition.Slots[spare] = spec;
        return true;
    }

    // ------------------------------------------------------------------ the enemy's own pawn

    /// <summary>
    /// THE DROIDEKA MOD'S RECIPE, measured 2026-09-07 (PlayableDroideka V1.0, mounted beside the
    /// game): its pool row names the ENEMY pawn class -- Char_Enemy_Separatist_Droideka_C, not a
    /// hero pawn -- and its definition is the enemy's with exactly one slot changed, Faction to
    /// Zero Company. The enemy pawn brings its own animation blueprint, so the droid animates;
    /// the enemy class, weapon spec, gear kit and tactical spec stay its own. The B1 has such a
    /// pawn too (Char_Enemy_Separatist_B1), as do B2, BX, Magnaguard.
    ///
    /// The enemy pawn for an enemy template, as the object path a row's CharacterClass holds:
    /// ".../Char_Enemy_X.Char_Enemy_X_C" for a template ".../CD_Char_Enemy_X", found in the same
    /// folder first and then anywhere under Core/Pawns. "" when the template is not an enemy's or
    /// no pawn is named like it.
    /// </summary>
    public static string EnemyPawnFor(IFileProvider provider, string templateDefinition)
    {
        const string prefix = "CD_Char_Enemy_";
        var leaf = CharacterIndex.Recruit.Leaf(templateDefinition);
        if (!leaf.StartsWith(prefix, StringComparison.Ordinal)) return "";
        // CACHED, because the character page asks every frame and the answer now costs a read of
        // every pawn package in the definition's folder. The game does not change while we run.
        if (_enemyPawns.TryGetValue(templateDefinition, out var known)) return known;

        var pawnLeaf = "Char_Enemy_" + leaf[prefix.Length..];
        var folder = templateDefinition[..templateDefinition.LastIndexOf('/')];

        // Every enemy pawn under Core/Pawns: the definition's own folder first, then the one the
        // name rule points at, then the rest by name.
        var pawns = new List<string>();
        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            var name = key[(key.LastIndexOf('/') + 1)..];
            if (!name.StartsWith("Char_Enemy_", StringComparison.Ordinal) ||
                !name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pkg) continue;
            if (!pkg.Contains("/Core/Pawns/", StringComparison.OrdinalIgnoreCase)) continue;
            pawns.Add(pkg);
        }
        pawns = pawns
            .OrderBy(p => p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => CharacterIndex.Recruit.Leaf(p).Equals(pawnLeaf, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // THE PAWN THAT SAYS SO. A pawn class declares the definition it wears --
        // DefaultCustomizationDefinition on its default object -- and a name is only a guess at
        // that declaration: Char_Enemy_Separatist_BXMarauder wears CD_Char_Enemy_Separatist_BXCommando
        // and no Char_Enemy_Separatist_BXCommando exists, so the BX Commando the author added
        // came out on no pawn at all (2026-09-07). Own folder only, unless nothing there says so.
        var wanted = ObjectPath(templateDefinition);
        string? found = null;
        foreach (var scope in new[] { true, false })
        {
            foreach (var pkg in pawns)
            {
                var own = pkg.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
                if (scope != own) continue;
                if (DefaultDefinitionOf(provider, pkg).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                { found = pkg; break; }
            }
            if (found is not null) break;
        }
        // Nothing declares it: the name rule alone, which is what this did before.
        found ??= pawns.FirstOrDefault(
            p => CharacterIndex.Recruit.Leaf(p).Equals(pawnLeaf, StringComparison.OrdinalIgnoreCase));

        var pawn = found is null ? "" : $"{found}.{CharacterIndex.Recruit.Leaf(found)}_C";
        _enemyPawns[templateDefinition] = pawn;
        return pawn;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _enemyPawns =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The definition a pawn class wears by default -- DefaultCustomizationDefinition, which sits
    /// on the pawn's CustomizationInstance COMPONENT export (measured on the BX Marauder), not on
    /// its default object -- as an object path, or "" when it has none or cannot be read.
    /// </summary>
    public static string DefaultDefinitionOf(IFileProvider provider, string pawnPackage)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(pawnPackage)).GetExports().ToList());
            foreach (var e in exports.OrderBy(e => e.ExportType == "CustomizationInstance" ? 0 : 1))
            {
                var path = PartDetails.SoftPathOf(
                    PartDetails.Value(e.Properties, "DefaultCustomizationDefinition"));
                if (path is not null) return path;
            }
            return "";
        }
        catch { return ""; }
    }

    /// <summary>The player's faction part, as the object path a Faction slot holds; "" if not found.</summary>
    public static string PlayerFaction(IFileProvider provider)
    {
        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith("/CPD_Faction_ZeroCompany.uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is { } pkg) return ObjectPath(pkg);
        }
        return "";
    }

    /// <summary>The row this mod adds for a definition copy, or null.</summary>
    public static ModOverride.RowAdd? RowOf(ModProject mod, ModCopy definition)
    {
        var mine = ObjectPath(UtilityAuthor.FolderFor(mod) + definition.Id);
        return PoolRows(mod).Select(r => r.Row)
            .FirstOrDefault(a => a.Set.TryGetValue("CustomizationDefinition", out var d) &&
                                 string.Equals(d, mine, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One thing offering a recruit again would change, said before it happens.</summary>
    public sealed record Offer(string What, string From, string To);

    /// <summary>
    /// The next identity for a recruit being offered again: the stem with "Again" on it, bumped
    /// until nothing in the mod uses it.
    ///
    /// The suffix is BARE. UE splits a trailing <c>_N</c> off an FName as a number, so an asset
    /// called <c>CD_Char_X_2</c> mounts and nothing resolves it -- see
    /// [[zero-company-fname-number-suffix]]. An existing Again/AgainN is REPLACED rather than
    /// appended to, so offering the same recruit five times gives AgainN, not AgainAgainAgain.
    /// </summary>
    private static string NextIdentity(string id, Func<string, bool> taken)
    {
        var stem = System.Text.RegularExpressions.Regex.Replace(
            id, "Again[0-9]*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (stem.Length == 0) stem = id;
        if (!taken(stem + "Again")) return stem + "Again";
        for (var n = 2; n < 1000; n++)
            if (!taken(stem + "Again" + n)) return stem + "Again" + n;
        return stem + "AgainX";
    }

    /// <summary>
    /// OFFER THIS RECRUIT AGAIN, by giving them a new identity and changing nothing else.
    ///
    /// MEASURED IN GAME 2026-09-20, and it is the only known cure. A pool row can stop being
    /// offered while everything about it is still correct: the row is present, `bIsActive` is
    /// true, its definition loads, every asset it names exists -- and the game simply never draws
    /// it. **Editing the data does not help.** A BX was made slot-identical to a working droid,
    /// verified in the installed container, and was still never offered across many refreshes; the
    /// same character under a new copy id and a new row name appeared immediately, carrying all of
    /// its own original data.
    ///
    /// THE MECHANISM IS NOT KNOWN and this must not pretend otherwise. It is not the databank
    /// saves, not SharedGameSettings, not the campaign save (which holds no reference to the mod at
    /// all), not an asset-registry cache and not a name collision -- all were searched. This is a
    /// remedy, not an explanation. See [[zero-company-pool-dataregistry]].
    ///
    /// ONLY the definition and its row move. The name part, weapon spec, gear kit, animation sets
    /// and the pawn copy keep their ids, because that is exactly what was proven and every extra
    /// rename is another chain to get half-right. The pawn's re-point is carried across because it
    /// names the definition by copy id, and a half-renamed pair builds, installs, and quietly
    /// wears the enemy's own definition instead of this one.
    ///
    /// With <paramref name="apply"/> false nothing is touched and the plan is the answer, so a
    /// panel can show what the button will do before anybody presses it.
    /// </summary>
    public static List<Offer>? OfferAgain(ModProject mod, ModCopy definition, bool apply, out string? why)
    {
        why = null;
        if (definition is null) { why = "no character is open"; return null; }

        var row = RowOf(mod, definition);
        if (row is null)
        { why = "this character is not offered in a hiring pool, so there is nothing to offer again"; return null; }

        var takenIds = mod.Copies.Select(c => c.Id).Concat(mod.Parts.Select(p => p.Id))
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenRows = PoolRows(mod).Select(r => r.Row.Row).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newId = NextIdentity(definition.Id, takenIds.Contains);
        var newRow = NextIdentity(row.Row, takenRows.Contains);
        var oldPath = ObjectPath(UtilityAuthor.FolderFor(mod) + definition.Id);
        var newPath = ObjectPath(UtilityAuthor.FolderFor(mod) + newId);

        var plan = new List<Offer>
        {
            new("the character", definition.Id, newId),
            new("its pool row", row.Row, newRow),
            new("the row points at", oldPath, newPath),
        };

        // A VALUE, NEVER A KEY. A re-point's key is the donor's package and its value is the copy
        // id being pointed at, so this moves the pawn's reference to this definition and leaves
        // the game package it came from alone.
        foreach (var c in mod.Copies)
            foreach (var (k, v) in c.RepointImports)
                if (v.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
                    plan.Add(new Offer($"{c.Id} points at", k, newId));

        if (!apply) return plan;

        foreach (var c in mod.Copies)
            foreach (var k in c.RepointImports
                               .Where(kv => kv.Value.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
                               .Select(kv => kv.Key).ToList())
                c.RepointImports[k] = newId;

        definition.Id = newId;
        row.Row = newRow;
        row.Set["CustomizationDefinition"] = newPath;
        return plan;
    }

    /// <summary>Whether the definition's row names an enemy pawn.</summary>
    public static bool UsesEnemyPawn(ModProject mod, ModCopy definition) =>
        RowOf(mod, definition) is { } row && row.Set.TryGetValue("CharacterClass", out var c) &&
        (c.Contains("Char_Enemy_", StringComparison.OrdinalIgnoreCase) ||
         c.StartsWith(UtilityAuthor.FolderFor(mod), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Put the character on the enemy's own pawn (or back on the pool's): the row's CharacterClass
    /// is rewritten, and when going on, the Faction slot becomes the player's -- the one change
    /// the Droideka mod made to the enemy's definition.
    /// </summary>
    public static bool UseEnemyPawn(IFileProvider provider, ModProject mod, ModCopy definition,
                                    bool on, out string? why)
    {
        why = null;
        var row = RowOf(mod, definition);
        if (row is null) { why = "no hiring-pool row of this mod offers this character"; return false; }
        var pawnId = PawnIdFor(definition);
        if (!on)
        {
            row.Set.Remove("CharacterClass");
            // The pawn copy goes with this box unless the story route still needs it.
            if (!UsesStoryRecruit(mod, definition))
                mod.Copies.RemoveAll(c => c.Id.Equals(pawnId, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        // A PAWN OF THE MOD'S OWN, wearing this definition by default, and the row aimed at it.
        // Measured in game 2026-09-07: the game picks a character's pawn by which pawn declares his
        // definition as its default, and a pawn whose default is the ENEMY's definition applies
        // the enemy's parts underneath the character's -- a fresh BX on the game's BX pawn had no
        // action points, the same BX on the copy did. The copy used to come with the story box,
        // so turning that off silently took the pawn away; it belongs here.
        if (!EnsurePawnCopy(provider, mod, definition, out why)) return false;
        row.Set["CharacterClass"] = $"{UtilityAuthor.FolderFor(mod)}{pawnId}.{pawnId}_C";

        var faction = PlayerFaction(provider);
        var factionSlot = SlotEdit.TagPrefix + "Character.Info.Faction";
        if (faction.Length > 0 && !definition.Slots.ContainsKey(factionSlot))
            definition.Slots[factionSlot] = faction;
        Stamp(mod, definition, "pawn");
        return true;
    }

    /// <summary>
    /// The pawn class copy for a definition, made if it is not there: the enemy pawn that declares
    /// the template as its default, duplicated, its class and default object renamed, and its
    /// DefaultCustomizationDefinition import re-pointed at this definition.
    /// </summary>
    public static bool EnsurePawnCopy(IFileProvider provider, ModProject mod, ModCopy definition, out string? why)
    {
        why = null;
        var pawnId = PawnIdFor(definition);
        if (mod.Copies.Any(c => c.Id.Equals(pawnId, StringComparison.OrdinalIgnoreCase))) return true;
        var pawnPath = EnemyPawnFor(provider, definition.Template);
        if (pawnPath.Length == 0) { why = "the template is not an enemy's, so no pawn class declares it"; return false; }
        var pawnPackage = SlotEdit.PackageOf(pawnPath);
        var pawnLeaf = CharacterIndex.Recruit.Leaf(pawnPackage);
        var wears = DefaultDefinitionOf(provider, pawnPackage);
        if (wears.Length == 0) { why = $"{pawnLeaf} declares no default definition to re-point"; return false; }
        var pawn = new ModCopy { Id = pawnId, Template = pawnPackage };
        pawn.RenameNames[pawnLeaf + "_C"] = pawnId + "_C";
        pawn.RenameNames["Default__" + pawnLeaf + "_C"] = "Default__" + pawnId + "_C";
        pawn.RepointImports[SlotEdit.PackageOf(wears)] = definition.Id;
        mod.Copies.Add(pawn);
        // A blaster shipped before the pawn asked for the stance set and had no pawn to put it on.
        if (UsesRigRifle(mod, definition)) UseStanceSet(provider, mod, definition, true, out _);
        return true;
    }

    // ------------------------------------------------------------------ the enemy's own kit, made legal for a recruit

    /// <summary>The tag that marks a part as only ever placed by hand -- an authored character's.</summary>
    public const string AuthoredOnlyTag = "br.Customization.Accepts.AuthoredOnly";
    private const string AcceptsCharacterPrefix = "br.Customization.Accepts.Character.";
    private const string ClassTagPrefix = "br.Customization.Part.Character.Class.";

    /// <summary>The four kit slots a recruit has to keep, and the id prefix a copy of each takes.</summary>
    public static readonly (string Slot, string IdPrefix)[] KitSlots =
    {
        ("Character.Class", "CPD_Char_Class_"),
        ("Character.Specializations.Tactical.Primary", "CPD_TacticalSpec_"),
        ("Character.Specializations.Weapon", "CPD_WeaponSpec_"),
        ("Character.Specializations.Weapon.GearKit", "CPD_GearKit_"),
    };

    /// <summary>Whether a template wears an enemy's class part -- the kit below is then in play.</summary>
    public static bool HasEnemyKit(IReadOnlyList<SlotEdit.Slot> slots) =>
        slots.Any(s => s.Tail.Equals("Character.Class", StringComparison.OrdinalIgnoreCase) &&
                       s.Leaf.StartsWith("CPD_Char_Class_Enemy", StringComparison.OrdinalIgnoreCase));

    /// <summary>A part's AllowedSlots tags; empty when it carries none or cannot be read.</summary>
    public static List<string> AllowedSlotsOf(IFileProvider provider, string part)
    {
        if (part.Length == 0) return new List<string>();
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(part)).GetExports().ToList());
            return PartDetails.TagsIn(exports, "AllowedSlots");
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// The part a spec fills its sub-slot with by default -- a weapon spec's gear kit, off its
    /// CustomizationFragmentSlot -- as a package path, "" when it names none.
    /// </summary>
    public static string DefaultPartOf(IFileProvider provider, string part)
    {
        if (part.Length == 0) return "";
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(part)).GetExports().ToList());
            foreach (var e in exports.Where(e => e.ExportType == "CustomizationFragmentSlot"))
            {
                var path = PartDetails.SoftPathOf(PartDetails.Value(e.Properties, "DefaultPart"));
                if (path is not null) return SlotEdit.PackageOf(path);
            }
        }
        catch { }
        return "";
    }

    /// <summary>Whether a definition copy's Class slot already names a part of this mod's own.</summary>
    public static bool UsesEnemyKit(ModProject mod, ModCopy definition) =>
        definition.Slots.TryGetValue(SlotEdit.TagPrefix + "Character.Class", out var c) &&
        c.StartsWith(UtilityAuthor.FolderFor(mod), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// KEEP THE ENEMY'S OWN KIT -- the Playable Droideka mod's data changes, on copies.
    ///
    /// Measured 2026-09-07 (the mod's pak diffed against the game): an enemy's class part, gear
    /// kit, tactical spec and weapon spec all carry `Accepts.AuthoredOnly`, and the tactical spec
    /// sits in a pseudo-slot (`Accepts.Character.Specializations.Tactical`) rather than the real
    /// one. A recruit is not authored, so the game throws those parts out and rolls hero ones --
    /// which is the whole of what the author saw: hero animations on a B1 (they ride on the
    /// weapon spec), and a unit with no action points (for an enemy, the move and turn basics --
    /// AbilitySet_BasicCharacterDefaults -- ride on the TACTICAL spec, and the enemy class part
    /// carries none; a hero's ride on its class part). The Droideka mod's author removed
    /// AuthoredOnly from the class part and gear kit, and rewrote the tactical spec's tags to
    /// [the real Primary slot, the droid's class tag]. That is done here, on COPIES, with the one
    /// tool a copy has -- a name rename -- so a removed tag becomes a duplicate of the slot tag
    /// beside it, which the container tolerates. The weapon spec is treated the same way, and
    /// its default gear kit is copied with it and the spec re-pointed at the copy, so the two
    /// stay together and no shared enemy part is touched.
    ///
    /// UNTESTED IN GAME at the time of writing: whether the recruit roll leaves a legal, non-
    /// authored weapon spec where it finds it. The Droideka's own weapon spec was left authored
    /// by its author and its rig ignores proxy tables, so it proves nothing about that.
    /// </summary>
    /// <summary>The one slot of the kit that carries an enemy's basics: its tactical spec.</summary>
    public const string TacticalSlot = "Character.Specializations.Tactical.Primary";

    /// <summary>Whether the definition's tactical slot names a copy of this mod's own, with the class slot left the template's.</summary>
    public static bool UsesTacticalSpec(ModProject mod, ModCopy definition) =>
        definition.Slots.TryGetValue(SlotEdit.TagPrefix + TacticalSlot, out var t) &&
        t.StartsWith(UtilityAuthor.FolderFor(mod), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ONLY THE TACTICAL SPEC, made legal -- the part of the kit that matters, measured in game
    /// 2026-09-07 on fresh recruits: with the four kit copies a droid on the mod's pawn had action
    /// points; with the hero class part instead, and no kit, none. An enemy's move and turn
    /// basics ride on its tactical spec, and the roll leaves a legal one in place. The weapon and
    /// gear-kit copies are what turned up in everyone's recruitment offers, so they stay out.
    /// </summary>
    public static bool UseTacticalSpec(IFileProvider provider, ModProject mod, ModCopy definition,
                                       bool on, out string? why) =>
        UseEnemyKit(provider, mod, definition, on, out why, new[] { TacticalSlot });

    public static bool UseEnemyKit(IFileProvider provider, ModProject mod, ModCopy definition,
                                   bool on, out string? why, IReadOnlyCollection<string>? only = null)
    {
        why = null;
        var folder = UtilityAuthor.FolderFor(mod);
        const string prefix = "CD_Char_";
        var stem = definition.Id.StartsWith(prefix, StringComparison.Ordinal) ? definition.Id[prefix.Length..] : definition.Id;
        var slots = KitSlots.Where(k => only is null || only.Contains(k.Slot)).ToArray();

        if (!on)
        {
            foreach (var (slot, _) in slots)
            {
                var tag = SlotEdit.TagPrefix + slot;
                if (!definition.Slots.TryGetValue(tag, out var v) ||
                    !v.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                var id = SlotEdit.LeafOf(v);
                definition.Slots.Remove(tag);
                // The copy goes unless another definition of this mod still names it.
                var stillUsed = mod.Copies.Any(d => d != definition && d.Slots.Values.Any(
                    x => SlotEdit.LeafOf(x).Equals(id, StringComparison.OrdinalIgnoreCase)));
                if (!stillUsed)
                    mod.Copies.RemoveAll(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            }
            return true;
        }
        if (only is null ? UsesEnemyKit(mod, definition) : UsesTacticalSpec(mod, definition)) return true;

        var read = SlotEdit.ReadFrom(provider, definition.Template, out var readWhy);
        if (read.Count == 0) { why = readWhy ?? "the template's slots could not be read"; return false; }
        string PartIn(string tail) =>
            read.FirstOrDefault(s => s.Tail.Equals(tail, StringComparison.OrdinalIgnoreCase))?.Path is { Length: > 0 } p
                ? SlotEdit.PackageOf(p) : "";

        var weaponTemplate = PartIn("Character.Specializations.Weapon");
        // The class gate the enemy's own weapon spec demands -- "...Class.Battledroid.B1" -- is what
        // the tactical spec's AuthoredOnly becomes, as the Droideka mod did.
        var gate = AllowedSlotsOf(provider, weaponTemplate)
            .FirstOrDefault(t => t.StartsWith(ClassTagPrefix, StringComparison.Ordinal)) ?? "";

        var made = new List<(string Slot, ModCopy Copy, string Template)>();
        foreach (var (slot, idPrefix) in slots)
        {
            var template = PartIn(slot);
            // An empty gear-kit slot is filled at runtime from the weapon spec's default part;
            // that part is the one to copy, or the kit stays an authored part the game drops.
            if (template.Length == 0 && slot.EndsWith(".GearKit", StringComparison.Ordinal) && weaponTemplate.Length > 0)
                template = DefaultPartOf(provider, weaponTemplate);
            if (template.Length == 0) continue;

            var allowed = AllowedSlotsOf(provider, template);
            var slotTag = SlotEdit.TagPrefix + slot;
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in allowed)
            {
                if (t == AuthoredOnlyTag)
                    renames[t] = slot.EndsWith("Tactical.Primary", StringComparison.Ordinal) && gate.Length > 0 ? gate : slotTag;
                else if (t.StartsWith(AcceptsCharacterPrefix, StringComparison.Ordinal))
                    renames[t] = SlotEdit.TagPrefix + "Character." + t[AcceptsCharacterPrefix.Length..] +
                                 (slot.EndsWith(".Primary", StringComparison.Ordinal) ? ".Primary" : "");
            }
            if (renames.Count == 0) continue;   // already legal for a recruit; the game's part serves

            var id = idPrefix + stem;
            for (var n = 2; mod.Copies.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); n++)
                id = idPrefix + stem + n;
            var copy = new ModCopy { Id = id, Template = template };
            foreach (var (from, to) in renames) copy.RenameNames[from] = to;
            made.Add((slot, copy, template));
        }
        if (made.Count == 0) { why = "every part of the template's kit is already legal for a recruit"; return false; }

        // The weapon spec's default gear kit becomes the copy of it: both halves of the soft path.
        var gear = made.FirstOrDefault(m => m.Slot.EndsWith(".GearKit", StringComparison.Ordinal));
        var weapon = made.FirstOrDefault(m => m.Slot.Equals("Character.Specializations.Weapon", StringComparison.Ordinal));
        if (gear.Copy is not null && weapon.Copy is not null)
        {
            weapon.Copy.RenameNames[gear.Template] = folder + gear.Copy.Id;
            weapon.Copy.RenameNames[SlotEdit.LeafOf(gear.Template)] = gear.Copy.Id;
        }

        foreach (var (slot, copy, _) in made)
        {
            mod.Copies.Add(copy);
            definition.Slots[SlotEdit.TagPrefix + slot] = ObjectPath(folder + copy.Id);
        }
        Stamp(mod, definition, "kit");
        return true;
    }

    // ------------------------------------------------------------------ what built a copy, and when

    /// <summary>
    /// One thing the character page builds, and the version of the recipe that builds it.
    ///
    /// WHY THERE IS A VERSION AT ALL. Four times over two days the author was told to untick a
    /// box and tick it again, because a fix had changed what the box builds and the copy in his
    /// project was still the old one. He had no way to know that: a copy looks the same whichever
    /// build made it, so the instruction could only ever come from me -- and once it silently
    /// threw away a choice he had made, because the own-gun copies belong to the weapon that owns
    /// them and went with it.
    ///
    /// So the copy records what built it, and the tool can say the thing I was saying.
    ///
    /// BUMP THE NUMBER when the recipe changes what it BUILDS -- different renames, a different
    /// template, another copy shipped alongside. NOT for a fix somewhere else, and not for a
    /// changed label or tooltip. The number's only job is to mean "a copy the old recipe made is
    /// not what this box makes now", and a version that cries wolf teaches people to press the
    /// button without reading it.
    /// </summary>
    public sealed record Feature(string Key, string Label, int Recipe);

    /// <summary>
    /// The features, IN DEPENDENCY ORDER: each one may rely on the ones before it, and nothing
    /// relies on the ones after. <see cref="Remake"/> turns them off backwards and on forwards,
    /// which is the whole reason the order is a property of this list rather than of the code
    /// that happens to walk it.
    ///
    /// Every recipe starts at 1 rather than at the number of times it has really changed. That
    /// count is not recoverable -- these existed for two days before anything recorded them -- and
    /// inventing one would be a number that looks measured and is not. What matters is that a copy
    /// made before today carries 0 and is therefore behind, which is true.
    /// </summary>
    public static readonly Feature[] Features =
    [
        new("pawn",    "the pawn they spawn on",              1),
        new("kit",     "their own class and specs",           1),
        new("rifle",   "the weapon for the Armoury",          1),
        new("own-gun", "only their own gun in that weapon",   1),
        new("story",   "joining by story recipe",             1),
    ];

    public static Feature? FeatureOf(string key) => Array.Find(Features, f => f.Key == key);

    /// <summary>Whether a feature's box is on for this character.</summary>
    public static bool IsOn(ModProject mod, ModCopy definition, string key) => key switch
    {
        "pawn"    => UsesEnemyPawn(mod, definition),
        "kit"     => UsesEnemyKit(mod, definition) || UsesTacticalSpec(mod, definition),
        "rifle"   => UsesRigRifle(mod, definition),
        "own-gun" => UsesOwnGun(mod, definition),
        "story"   => UsesStoryRecruit(mod, definition),
        _         => false,
    };

    /// <summary>
    /// The copies a feature OWNS -- the ones its box creates and its box takes away again.
    ///
    /// ONE list, used to stamp them, to judge them stale and to say how many there are. The
    /// alternative is a second list of ids written beside the first, and the failure when the two
    /// drift is that a copy is silently never checked -- which is the shape of half the faults in
    /// this file. Ids that are not in the mod are dropped, so this answers about what is there.
    /// </summary>
    public static List<string> IdsOf(ModProject mod, ModCopy definition, string key)
    {
        var ids = new List<string>();
        switch (key)
        {
            case "pawn":
                ids.Add(PawnIdFor(definition));
                break;
            case "kit":
                // Found the way the off path finds them: through the definition's own slots. A
                // kit copy is numbered when its name is taken, so its id cannot be derived.
                foreach (var (slot, _) in KitSlots)
                    if (definition.Slots.TryGetValue(SlotEdit.TagPrefix + slot, out var v) &&
                        v.StartsWith(UtilityAuthor.FolderFor(mod), StringComparison.OrdinalIgnoreCase))
                        ids.Add(SlotEdit.LeafOf(v));
                break;
            case "rifle":
                // The stance set belongs to the weapon: UseRigRifle makes it and UseRigRifle takes
                // it away, so it is the weapon's box that remakes it.
                ids.Add(RifleIdFor(definition));
                ids.Add(StanceSetIdFor(definition));
                break;
            case "own-gun":
                ids.Add(OwnGunPartId(definition));
                ids.Add(OwnGunKitId(definition));
                break;
            case "story":
                for (var n = 1; n <= 64; n++)
                {
                    var id = RecipeIdFor(definition, n);
                    if (!mod.Copies.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) break;
                    ids.Add(id);
                }
                break;
        }
        return ids.Where(id => mod.Copies.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                  .ToList();
    }

    /// <summary>Record which recipe built a feature's copies. Called after its box is turned on.</summary>
    public static void Stamp(ModProject mod, ModCopy definition, string key)
    {
        if (FeatureOf(key) is not { } feature) return;
        foreach (var id in IdsOf(mod, definition, key))
            if (mod.Copies.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } copy)
            { copy.MadeBy = key; copy.Recipe = feature.Recipe; }
    }

    /// <summary>
    /// The features that are ON and whose copies an older recipe built.
    ///
    /// A copy with no stamp counts as behind, and that is deliberate rather than pessimistic: it
    /// is not that such a copy is known to be wrong, it is that nothing about it can be shown to
    /// match what the box builds now. Saying so once, with a button, is better than the tool
    /// quietly assuming and being wrong on the one project where it mattered.
    /// </summary>
    public static List<Feature> Stale(ModProject mod, ModCopy definition)
    {
        var stale = new List<Feature>();
        foreach (var feature in Features)
        {
            if (!IsOn(mod, definition, feature.Key)) continue;
            foreach (var id in IdsOf(mod, definition, feature.Key))
            {
                if (mod.Copies.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    is not { } copy || copy.Recipe >= feature.Recipe) continue;
                stale.Add(feature);
                break;
            }
        }
        return stale;
    }

    /// <summary>
    /// Build every feature this character has again, with the recipes the running tool has.
    ///
    /// This is the untick-and-re-tick dance, done completely and in the right order, which is
    /// exactly what doing it by hand could not promise. What is ON is read FIRST and turned back
    /// on from that record, so a feature whose copies belong to another feature cannot be lost to
    /// the order they are removed in -- unticking the weapon took the own-gun choice with it, and
    /// the only record that the choice had ever been made was the copy that had just been deleted.
    ///
    /// Off backwards, on forwards, through <see cref="Features"/>. Refuses rather than half-does:
    /// a character left with some features remade and some removed is worse than one untouched.
    /// </summary>
    public static bool Remake(IFileProvider provider, ModProject mod, ModCopy definition,
                              out string? why, out List<string> what, out List<string> notes)
    {
        why = null;
        what = new List<string>();

        // WHAT WENT PART-WAY. Several of these return true having set `why` -- UseRigRifle does it
        // when the stance set could not be made, because a blaster without one is still worth
        // having and the pawn box may supply it later. That is a reasonable thing for it to do and
        // an unreasonable thing to throw away: the first version of this reported a clean remake
        // while the stance set had silently not been built, which is the same class of fault the
        // button exists to fix, committed by the button.
        notes = new List<string>();

        // READ THE WHOLE STATE BEFORE TOUCHING ANY OF IT. Everything below is decided from these
        // three values; nothing is asked of the project again while it is half taken apart.
        var on = Features.Where(f => IsOn(mod, definition, f.Key)).ToList();
        var fullKit = UsesEnemyKit(mod, definition);
        var storyCount = Math.Max(1, IdsOf(mod, definition, "story").Count);
        if (on.Count == 0) { why = "nothing on this character was built by one of these boxes"; return false; }

        foreach (var feature in Enumerable.Reverse(on))
            if (!Toggle(provider, mod, definition, feature.Key, false, fullKit, storyCount, out why))
            { why = $"{feature.Label}: {why ?? "could not be taken away"}"; return false; }

        foreach (var feature in on)
        {
            if (!Toggle(provider, mod, definition, feature.Key, true, fullKit, storyCount, out var partly))
            { why = $"{feature.Label}: {partly ?? "could not be built again"}"; return false; }
            if (partly is not null) notes.Add($"{feature.Label}: {partly}");
            Stamp(mod, definition, feature.Key);
            what.Add(feature.Label);
        }
        return true;
    }

    private static bool Toggle(IFileProvider provider, ModProject mod, ModCopy definition,
                               string key, bool on, bool fullKit, int storyCount, out string? why)
    {
        why = null;
        switch (key)
        {
            case "pawn":    return UseEnemyPawn(provider, mod, definition, on, out why);
            // Which of the two the author chose is not recoverable once the slots are cleared,
            // which is why it is read before anything is removed and passed in here.
            case "kit":     return fullKit ? UseEnemyKit(provider, mod, definition, on, out why)
                                           : UseTacticalSpec(provider, mod, definition, on, out why);
            case "rifle":   return UseRigRifle(provider, mod, definition, on, out why);
            case "own-gun": return UseOwnGun(provider, mod, definition, on, out why);
            case "story":   return UseStoryRecruit(provider, mod, definition, on, out why, storyCount);
            default:        why = $"there is no feature called '{key}'"; return false;
        }
    }

    // ------------------------------------------------------------------ joining by story recipe

    /// <summary>The game's own recruit recipe, copied for a character of the mod's: Jae's.</summary>
    public const string RecipeTemplate = "/Game/Game/GameData/Recipes/Recipe_JaeAddedToRoster";

    /// <summary>
    /// The weighted tables the campaign arcs draw recipes from, every cycle from chapter two to
    /// six (measured on Arc_MainStory_A2Start..A6, 2026-09-07). The tutorial arc and the epilogue
    /// draw from none. An entry in all eight is drawn within a cycle or two wherever a campaign is.
    /// </summary>
    public static readonly string[] StoryRecipeTables =
    {
        "/Game/Game/GameData/Recipes/FieldTasks_Optional/RecipeTables/RecipeTable_Arc2_Bucket1",
        "/Game/Game/GameData/Recipes/FieldTasks_Optional/RecipeTables/RecipeTable_Arc2_Bucket2",
        "/Game/Game/GameData/Recipes/FieldTasks_Optional/RecipeTables/RecipeTable_Arc3_Bucket1",
        "/Game/Game/GameData/Recipes/FieldTasks_Optional/RecipeTables/RecipeTable_Arc4_Bucket1",
        "/Game/Game/GameData/Recipes/FieldTasks_Optional/RecipeTables/RecipeTable_Arc4_Bucket2",
        "/Game/Game/GameData/Recipes/FieldTasks_Optional/RecipeTables/RecipeTable_Arc5_Bucket1",
        "/Game/Game/GameData/Recipes/FieldTasks_Optional/RecipeTables/RecipeTable_Arc5_Bucket2",
        "/Game/Game/GameData/Recipes/FieldTasks_Optional/RecipeTables/RecipeTable_Arc6_Bucket1",
    };

    /// <summary>
    /// The campaign arcs whose scripted turns the recipe is ADDED to outright, which is not a
    /// draw: the arc applies its turn entry's Recipes[] at that turn. Chapter one is the tutorial
    /// and is left alone; everything from the first hub cycle to the epilogue is here. The
    /// author's first cycle with table entries alone drew one of the game's own instead.
    /// </summary>
    public static readonly string[] StoryArcs =
    {
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A2Start",
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A2",
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A3",
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A4Start",
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A4",
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A5Start",
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A5",
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A6",
        "/Game/Game/GameData/CampaignData/MainStory/Arc_MainStory_A7",
    };

    /// <summary>Against the game's entries at weight 1 each: drawn almost every time.</summary>
    public const float StoryRecipeWeight = 50f;

    /// <summary>
    /// The recipe copy's id for a definition copy -- the n-th of them, because a recipe runs once
    /// and a mod may want three B1s. The number sits INSIDE the stem, never after an underscore
    /// at the end, which an FName would take as its number (see ZenPackage.SplitNumberedNames).
    /// </summary>
    public static string RecipeIdFor(ModCopy definition, int n = 1) =>
        "Recipe_" + StemOf(definition) + (n > 1 ? n.ToString() : "") + "AddedToRoster";
    public static string PawnIdFor(ModCopy definition) => "Char_" + StemOf(definition);

    /// <summary>How many recipe copies the mod carries for this definition: how many can join.</summary>
    public static int StoryRecruitCount(ModProject mod, ModCopy definition)
    {
        var n = 0;
        while (mod.Copies.Any(c => c.Id.Equals(RecipeIdFor(definition, n + 1), StringComparison.OrdinalIgnoreCase))) n++;
        return n;
    }

    private static string StemOf(ModCopy definition)
    {
        const string prefix = "CD_Char_";
        return definition.Id.StartsWith(prefix, StringComparison.Ordinal) ? definition.Id[prefix.Length..] : definition.Id;
    }

    /// <summary>Whether the mod carries a recipe copy for this definition.</summary>
    public static bool UsesStoryRecruit(ModProject mod, ModCopy definition) =>
        mod.Copies.Any(c => c.Id.Equals(RecipeIdFor(definition), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// JOIN BY STORY RECIPE -- the game's own way of adding a character to the roster, as data.
    ///
    /// Measured 2026-09-07. The hiring pool ROLLS a recruit: it throws out the definition's class,
    /// tactical spec and weapon spec and fills the slots with hero parts, and it did so even when
    /// copies of those parts were legal in the package and the registry both (two in-game tests).
    /// A story character never goes through that: `Recipe_JaeAddedToRoster` holds a
    /// CustomReward_RecruitCharacter whose Character is a PAWN CLASS, and the campaign arc grants
    /// recipes from its turn entries and from weighted tables it draws every cycle. The Aftermath
    /// mod recruits Bennic with a copy of exactly that recipe, and the Playable Droideka mod's
    /// script calls the same function by hand.
    ///
    /// Three things, all copies and one append, nothing of the game's written over except the
    /// draw tables:
    ///   - a PAWN CLASS of the mod's own: the enemy pawn duplicated, its DefaultCustomizationDefinition
    ///     import re-pointed at this definition, so the recruit is made wearing it -- name,
    ///     faction and kit, unrolled;
    ///   - a RECIPE: Jae's, with Character re-pointed at that pawn class;
    ///   - an entry in each chapter's draw table, at a weight that all but guarantees the draw,
    ///     auto-started, so the character joins on the next cycle or the one after.
    /// The hiring-pool row, if there is one, is left alone; a rolled twin can still turn up in
    /// recruitment, and the character page says so.
    ///
    /// NOT TESTED IN GAME at the time of writing; every piece read back out of a built container.
    /// </summary>
    public static bool UseStoryRecruit(IFileProvider provider, ModProject mod, ModCopy definition,
                                       bool on, out string? why, int count = 1)
    {
        why = null;
        var folder = UtilityAuthor.FolderFor(mod);
        var pawnId = PawnIdFor(definition);
        count = Math.Clamp(count, 1, 20);

        // OFF, or a different count: everything of the route goes, and ON rebuilds it. A recipe
        // runs once, so "how many can join" is how many recipe copies there are.
        var had = StoryRecruitCount(mod, definition);
        if (!on || had != count)
        {
            var mine = Enumerable.Range(1, Math.Max(had, count) + 1).Select(n => RecipeIdFor(definition, n))
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // The pawn copy stays when the pawn box owns it: a fresh BX without it spawned on the
            // game's pawn and lost his action points (2026-09-07).
            mod.Copies.RemoveAll(c => mine.Contains(c.Id) ||
                                      (c.Id.Equals(pawnId, StringComparison.OrdinalIgnoreCase) && !UsesEnemyPawn(mod, definition)));
            foreach (var ov in mod.Overrides) ov.AddRecipes.RemoveAll(r => mine.Contains(r.Recipe));
            mod.Overrides.RemoveAll(ov => (StoryRecipeTables.Contains(ov.Target, StringComparer.OrdinalIgnoreCase) ||
                                           StoryArcs.Contains(ov.Target, StringComparer.OrdinalIgnoreCase)) && !ov.HasEdit);
            if (!on) return true;
        }
        else return true;

        // The pawn: the mod's own copy of the enemy's, wearing this definition by default -- made
        // here if the pawn box has not made it already.
        if (!EnsurePawnCopy(provider, mod, definition, out why)) return false;

        // The recipe: Jae's, which names its pawn class by a soft class path -- two names.
        string jaePawnPackage = "", jaePawnClass = "";
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(RecipeTemplate)).GetExports().ToList());
            foreach (var e in exports.Where(e => e.ExportType == "CustomReward_RecruitCharacter_C"))
            {
                var path = PartDetails.SoftPathOf(PartDetails.Value(e.Properties, "Character")) ?? "";
                var dot = path.LastIndexOf('.');
                if (dot > 0) { jaePawnPackage = path[..dot]; jaePawnClass = path[(dot + 1)..]; }
            }
        }
        catch (Exception e) { why = $"the recipe template could not be read: {e.Message}"; return false; }
        if (jaePawnPackage.Length == 0) { why = "the recipe template names no character to re-point"; return false; }

        var recipeLeaf = CharacterIndex.Recruit.Leaf(RecipeTemplate);
        for (var n = 1; n <= count; n++)
        {
            var recipeId = RecipeIdFor(definition, n);
            var recipe = new ModCopy { Id = recipeId, Template = RecipeTemplate };
            recipe.RenameNames[recipeLeaf + "_C"] = recipeId + "_C";
            recipe.RenameNames["Default__" + recipeLeaf + "_C"] = "Default__" + recipeId + "_C";
            recipe.RenameNames[jaePawnPackage] = folder + pawnId;
            recipe.RenameNames[jaePawnClass] = pawnId + "_C";
            // ONCE. The recipe sits in every scripted turn of every arc and in every draw table,
            // so without these it would be added again at every turn the campaign reaches. Jae's
            // recipe leaves them at their default (false); the game's own repeat-proof tasks set
            // them, so the flags are known to be honoured.
            // On the DEFAULT OBJECT, which is where a Blueprint keeps its values; the export named
            // like the class is the class itself, and has no such property.
            recipe.AddScalars.Add(new ModOverride.ScalarAdd("Default__" + recipeLeaf + "_C", "bBlockActiveDuplicates", 1));
            recipe.AddScalars.Add(new ModOverride.ScalarAdd("Default__" + recipeLeaf + "_C", "bBlockCompletedDuplicates", 1));
            mod.Copies.Add(recipe);

            foreach (var target in StoryArcs.Concat(StoryRecipeTables))
            {
                var ov = mod.Overrides.FirstOrDefault(x => x.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (ov is null) { ov = new ModOverride { Target = target }; mod.Overrides.Add(ov); }
                if (!ov.AddRecipes.Any(r => r.Recipe.Equals(recipeId, StringComparison.OrdinalIgnoreCase)))
                    ov.AddRecipes.Add(new ModOverride.RecipeAdd { Recipe = recipeId, Weight = StoryRecipeWeight, AutoStart = true });
            }
        }
        Stamp(mod, definition, "story");
        Stamp(mod, definition, "pawn");
        return true;
    }

    // ------------------------------------------------------------------ a rifle that carries their animations

    /// <summary>The hero rifle specialization, whose Kabb variant is the slot a droid variant takes.</summary>
    public const string HeroRifleSpec = "/Game/Game/Customizations/Characters/Common/Specialization/Weapon/CPD_WeaponSpec_Blaster_Rifle";

    public static string RifleIdFor(ModCopy definition) => "CPD_WeaponSpec_Rifle_" + StemOf(definition);

    public static bool UsesRigRifle(ModProject mod, ModCopy definition) =>
        mod.Copies.Any(c => c.Id.Equals(RifleIdFor(definition), StringComparison.OrdinalIgnoreCase));

    /// <summary>The tag a definition's rig part carries -- "br.Customization.Part.Character.Rig.B1" -- or "".</summary>
    public static string RigTagOf(IFileProvider provider, string definition)
    {
        try
        {
            var rigPart = SlotEdit.ReadFrom(provider, definition, out _)
                .FirstOrDefault(s => s.Tail == "Character.Rig")?.Path ?? "";
            if (rigPart.Length == 0) return "";
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(SlotEdit.PackageOf(rigPart))).GetExports().ToList());
            foreach (var e in exports.Where(e => e.ExportType == "CustomizationFragmentGameplayTags"))
                foreach (var t in PartDetails.TagsOf(e.Properties, "GameplayTags"))
                    if (t.StartsWith("br.Customization.Part.Character.Rig.", StringComparison.Ordinal)) return t;
        }
        catch { }
        return "";
    }

    /// <summary>The animation SET a weapon spec carries (its first AnimSet fragment), as a package path, or "".</summary>
    private static string AnimSetOf(IFileProvider provider, string spec, Func<string, bool>? pick = null)
    {
        var (_, sets) = AnimPairsOf(provider, spec);
        return sets.FirstOrDefault(s => pick is null || pick(s)) ?? "";
    }

    /// <summary>Every non-empty animation table and animation set a weapon spec names, as package paths, in export order.</summary>
    private static (List<string> Tables, List<string> Sets) AnimPairsOf(IFileProvider provider, string spec)
    {
        var tables = new List<string>();
        var sets = new List<string>();
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(spec)).GetExports().ToList());
            foreach (var e in exports)
            {
                if (e.ExportType == ProxyTableFragment &&
                    PartDetails.SoftPathOf(PartDetails.Value(e.Properties, "ProxyTableSoft")) is { Length: > 0 } t)
                    tables.Add(SlotEdit.PackageOf(t));
                if (e.ExportType == "BP_CustomizationAnimSetFragment_C" &&
                    PartDetails.SoftPathOf(PartDetails.Value(e.Properties, "AnimSetSoft")) is { Length: > 0 } s)
                    sets.Add(SlotEdit.PackageOf(s));
            }
        }
        catch { }
        return (tables, sets);
    }

    /// <summary>
    /// A RIFLE FOR THE ARMOURY THAT MOVES LIKE THEM -- the author's idea (2026-09-07): "make the
    /// weapons the droids use into Armoury weapons and give them to them when recruited".
    ///
    /// It works because the animation table and set ride on the WEAPON SPEC, and the hero rifle
    /// spec already switches them by character tag -- Kabb has his own pair. A copy whose Kabb
    /// variant is keyed to the RIG tag instead, and points at the rig's rifle table and set, gives
    /// a droid its own animations the moment the rifle is equipped, and a human the normal ones.
    /// Four renames, no new exports. Whatever the hiring pool rolled first stops mattering for
    /// the basic moves; hero ability animations still play on the droid skeleton as best they can.
    /// </summary>
    public static bool UseRigRifle(IFileProvider provider, ModProject mod, ModCopy definition,
                                   bool on, out string? why)
    {
        why = null;
        var id = RifleIdFor(definition);
        if (!on)
        {
            // The gun kit and the stance set exist for this copy; they go with it.
            UseOwnGun(provider, mod, definition, false, out _);
            mod.Copies.RemoveAll(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            UseStanceSet(provider, mod, definition, false, out _);
            return true;
        }
        if (UsesRigRifle(mod, definition)) return true;

        var rig = RigOf(provider, definition.Template);
        var rigTag = RigTagOf(provider, definition.Template);
        if (rig.Length == 0 || rigTag.Length == 0) { why = "the template's rig could not be read"; return false; }
        // THE DROID'S OWN PAIR, whatever weapon family it is -- the BX's one-handed rifle, the
        // B2's wrist blasters -- read off the template's own weapon spec. The carrier is the hero
        // rifle either way: the pawn's animation blueprint asks for its own keys, and what the
        // equipped spec has to hand it is this table and this set, not a hero family's.
        var ownSpec = SlotEdit.ReadFrom(provider, definition.Template, out _)
            .FirstOrDefault(s => s.Tail == "Character.Specializations.Weapon")?.Path ?? "";
        if (ownSpec.Length == 0) { why = "the template names no weapon spec of its own"; return false; }
        // THE RIG'S OWN, not the first: the BX spec carries three table fragments -- an empty
        // one, the human rifle's, and Droid_BX's -- and the first is the empty one.
        var (tables, sets) = AnimPairsOf(provider, SlotEdit.PackageOf(ownSpec));
        var table = tables.FirstOrDefault(t => t.Contains(rig, StringComparison.OrdinalIgnoreCase))
                    ?? tables.FirstOrDefault(t => t.Contains("EnemySpecializations", StringComparison.OrdinalIgnoreCase))
                    ?? "";
        if (table.Length == 0) { why = "the template's own weapon spec names no animation table of its rig's to carry"; return false; }
        var set = sets.FirstOrDefault(s => s.Contains(rig, StringComparison.OrdinalIgnoreCase))
                  ?? sets.FirstOrDefault() ?? "";
        if (set.Length == 0) { why = "the template's own weapon spec names no animation set to carry"; return false; }

        // Kabb's pair on the hero rifle, read rather than assumed.
        var kabbSet = AnimSetOf(provider, HeroRifleSpec, p => CharacterIndex.Recruit.Leaf(p).Contains("Kabb", StringComparison.OrdinalIgnoreCase));
        var kabbTable = "";
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(HeroRifleSpec)).GetExports().ToList());
            foreach (var e in exports.Where(e => e.ExportType == ProxyTableFragment))
            {
                var path = PartDetails.SoftPathOf(PartDetails.Value(e.Properties, "ProxyTableSoft")) ?? "";
                if (path.Contains("Kabb", StringComparison.OrdinalIgnoreCase)) kabbTable = SlotEdit.PackageOf(path);
            }
        }
        catch { }
        if (kabbSet.Length == 0 || kabbTable.Length == 0) { why = "the hero rifle spec's Kabb variant could not be read"; return false; }

        var copy = new ModCopy { Id = id, Template = HeroRifleSpec, Display = $"{rig} blaster" };
        // ONLY THIS RIG MAY WEAR IT. Every animation branch in the copy is the droid's, so a human
        // who equipped it would play droid clips on a human skeleton -- and the Armoury offers a
        // weapon class to anyone. MEASURED: the game gates a BROWSABLE part to a rig by putting
        // the rig tag in AllowedSlots (CPD_H_Outfit_B1_Battledroid_TORS_MSA_TALB is
        // [Outfit.Torso.Mesh, Rig.B1] and nothing else); a CLASS gate plus AuthoredOnly is what
        // it uses to hide a part from browsing instead, which is not wanted here. A tag edit
        // rather than a rename, because the registry copy of AllowedSlots is what the Armoury
        // filters on and the build mirrors this edit into it.
        // The hero spec has NO AllowedSlots, so this creates one -- and it must name the slot as
        // well as the rig, or the part stops being offered for the weapon slot at all.
        copy.TagEdits.Add(new ModCopy.TagEdit("AllowedSlots",
            new List<string> { SlotEdit.TagPrefix + "Character.Specializations.Weapon", rigTag },
            new List<string>()));
        copy.RenameNames[PartsIndex.NamePrefix + "KabbUppercut"] = rigTag;
        copy.RenameNames[kabbTable] = table;
        copy.RenameNames[CharacterIndex.Recruit.Leaf(kabbTable)] = CharacterIndex.Recruit.Leaf(table);
        copy.RenameNames[kabbSet] = set;
        copy.RenameNames[CharacterIndex.Recruit.Leaf(kabbSet)] = CharacterIndex.Recruit.Leaf(set);
        // AND EVERY OTHER TABLE AND SET THE HERO RIFLE CARRIES -- its default branch (the plain
        // human 2HRifle pair) and the jetpack set. In game (2026-09-07, a fresh BX with the rifle
        // equipped): aiming right, and at the shot his arms detached and his head twisted -- a
        // HUMAN montage on the droid skeleton. Measured: the BX set has 111 keys, the human rifle
        // set 142, and the missing ones are the player-side shot keys (every Aim.Enter_*, Aim.Exit_*,
        // Confirm.Standing/Crouching, Aim.Turn_*) an AI never plays. A key the rig's set lacks was
        // resolved from the copy's remaining human set. With nothing human left in the copy, a
        // missing key has no clip at all, which is what the rig's own enemy spec gives the AI.
        // Several imports now name the same package; the store lists it once per import, which
        // the loader treats as the same dependency.
        var (heroTables, heroSets) = AnimPairsOf(provider, HeroRifleSpec);
        foreach (var t in heroTables.Where(t => t != kabbTable && t != table))
        {
            copy.RenameNames[t] = table;
            copy.RenameNames[CharacterIndex.Recruit.Leaf(t)] = CharacterIndex.Recruit.Leaf(table);
        }
        foreach (var sSet in heroSets.Where(x => x != kabbSet && x != set))
        {
            copy.RenameNames[sSet] = set;
            copy.RenameNames[CharacterIndex.Recruit.Leaf(sSet)] = CharacterIndex.Recruit.Leaf(set);
        }
        mod.Copies.Add(copy);
        // And the stance the rifle's abilities declare, on the pawn -- see UseStanceSet. Left as
        // a note rather than a refusal when there is no pawn copy yet: the pawn box applies it.
        if (!UseStanceSet(provider, mod, definition, true, out var stanceWhy))
            why = stanceWhy;
        // A gun kit made for an EARLIER copy of this blaster: put its renames back on the new one.
        // Measured 2026-09-07 in the author's project: own-gun box ticked, then the blaster
        // unticked and re-ticked -- the kit kept the minted class tag and the fresh blaster had
        // neither the tag nor the default part, so the class listed no kit and the Armoury fell
        // back to the Relby. The order the boxes are clicked in must not matter.
        if (UsesOwnGun(mod, definition) && !UseOwnGun(provider, mod, definition, true, out var gunWhy))
            why = gunWhy;
        Stamp(mod, definition, "rifle");
        Stamp(mod, definition, "own-gun");
        return true;
    }

    // ------------------------------------------------------------------ the stance the rifle abilities ask for

    public const string RifleStanceTag = "BitReactor.Animation.WeaponStance.2HRifle";
    /// <summary>The archetype every rifle actor names (BP_Rifle_E-5 among them): stance set BY RIG, Human as the default.</summary>
    public const string RifleArchetype = "/Game/Game/GameData/AnimationSets/SASArch_2HRifle";
    /// <summary>The stance set of a PLAYABLE rig on a rifle -- the key list a player's shot needs.</summary>
    public const string PlayableStanceSet = "/Game/Game/GameData/AnimationSets/SAS_Kabb_2HRifle";
    /// <summary>
    /// A name of OUR OWN, not the game's with a capital letter moved. "SAS_" + the stem gave
    /// `SAS_B1BattleDroid_2HRifle` against the game's `SAS_B1Battledroid_2HRifle`, and a
    /// PrimaryAssetId is the NAME rather than the path -- two assets, one id, and the one the
    /// game keeps may not be ours ([[zero-company-weapon-model-swap]]). The build's own collision
    /// note caught it.
    /// </summary>
    public static string StanceSetIdFor(ModCopy definition) => "SAS_Recruit_" + StemOf(definition);
    public static bool UsesStanceSet(ModProject mod, ModCopy definition) =>
        mod.Copies.Any(c => c.Id.Equals(StanceSetIdFor(definition), StringComparison.OrdinalIgnoreCase));

    /// <summary>Drop this character's stance copy, including one an older build named differently.</summary>
    private static void RemoveStanceCopies(ModProject mod, ModCopy definition, string id) =>
        mod.Copies.RemoveAll(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                                  (c.Id.StartsWith("SAS_", StringComparison.Ordinal) &&
                                   c.Id.Contains(StemOf(definition), StringComparison.OrdinalIgnoreCase)));

    /// <summary>The pawn's own stance set (StanceAnimationSets[0]) as a package path, and its stance tag.</summary>
    public static (string Set, string Stance) StanceSetOf(IFileProvider provider, string pawnPackage)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(pawnPackage)).GetExports().ToList());
            string set = "";
            foreach (var e in exports)
            {
                if (PartDetails.Unwrap(PartDetails.Value(e.Properties, "StanceAnimationSets")) is not UScriptArray arr) continue;
                foreach (var item in arr.Properties)
                {
                    var v = PartDetails.Unwrap(item.GenericValue);
                    string path = "";
                    try { path = v is FPackageIndex pi ? pi.ResolvedObject?.GetPathName() ?? "" : ""; } catch { }
                    if (path.Length > 0) { set = SlotEdit.PackageOf(path); break; }
                }
                if (set.Length > 0) break;
            }
            return set.Length == 0 ? ("", "") : (set, StanceTagOf(provider, set));
        }
        catch { return ("", ""); }
    }

    /// <summary>A stance set's WeaponStanceTag, "" when unreadable.</summary>
    public static string StanceTagOf(IFileProvider provider, string set)
    {
        try
        {
            // Pristine for the same reason the archetype is: a stance set this tool has already
            // shipped a copy of must not be read back as the thing to copy.
            var exports = AssetCopy.PristineExports(provider, set);
            foreach (var e in exports.Where(e => e.ExportType == "StanceAnimationSet"))
                if (PartDetails.TagOf(PartDetails.Value(e.Properties, "WeaponStanceTag")) is { Length: > 0 } tag) return tag;
        }
        catch { }
        return "";
    }

    /// <summary>Every entry tag a stance set carries, in order.</summary>
    public static List<string> StanceKeysOf(IFileProvider provider, string set)
    {
        var keys = new List<string>();
        try
        {
            // Pristine for the same reason the archetype is: a stance set this tool has already
            // shipped a copy of must not be read back as the thing to copy.
            var exports = AssetCopy.PristineExports(provider, set);
            foreach (var e in exports.Where(e => e.ExportType == "StanceAnimationSet"))
                if (PartDetails.Value(e.Properties, "Animations") is UScriptArray arr)
                    foreach (var item in arr.Properties)
                        if (PartDetails.Unwrap(item.GenericValue) is FStructFallback st &&
                            PartDetails.TagOf(PartDetails.Value(st.Properties, "EntryTag")) is { Length: > 0 } t)
                            keys.Add(t);
        }
        catch { }
        return keys;
    }

    /// <summary>The stance set the rifle archetype names for a rig tag, "" when the rig has no version.</summary>
    public static string ArchetypeVersionFor(IFileProvider provider, string rigTag)
    {
        try
        {
            // PRISTINE. This asset is one the mod OVERRIDES, so reading it through the plain
            // provider on a machine with the mod installed answers with the mod's own renames --
            // and the recipe is then told to start from a copy of itself. See PristineExports.
            var exports = AssetCopy.PristineExports(provider, RifleArchetype);
            foreach (var e in exports.Where(e => e.ExportType == "StanceAnimationSetArchetype"))
                if (PartDetails.Value(e.Properties, "Versions") is UScriptArray arr)
                    foreach (var item in arr.Properties)
                        if (PartDetails.Unwrap(item.GenericValue) is FStructFallback st &&
                            PartDetails.TagOf(PartDetails.Value(st.Properties, "MatchingAnimRigTag")) == rigTag &&
                            PartDetails.SoftPathOf(PartDetails.Value(st.Properties, "StanceAnimationSet")) is { Length: > 0 } path)
                            return SlotEdit.PackageOf(path);
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Which entry a rig's set already has that stands in for one it lacks. The rig owns every
    /// clip named here; nothing human is ever chosen. Null when nothing fits.
    /// </summary>
    private static string? StandInFor(string key, IReadOnlyCollection<string> have)
    {
        const string P = "BitReactor.Animation.";
        string? Pick(params string[] candidates) => candidates.Select(c => P + c).FirstOrDefault(have.Contains);
        var k = key.StartsWith(P, StringComparison.Ordinal) ? key[P.Length..] : key;
        // Aim.Turn_90L -> the idle turn of the same angle; steps and strafes -> the aim step
        // enters and exits; crouched variants -> their standing ones; stance-suffixed -> plain.
        if (k.StartsWith("Aim.Turn_", StringComparison.Ordinal))
            return Pick("Idle.Turn_" + k["Aim.Turn_".Length..], "Aim.Turn_ExactFacing", "Aim.Enter_0");
        if (k.StartsWith("Aim.StepFwd_", StringComparison.Ordinal) || k.StartsWith("Aim.StepBack_", StringComparison.Ordinal))
            return Pick("Aim.Enter_" + k["Aim.".Length..], "Aim.Turn_ExactFacing", "Aim.Enter_0");
        if (k.StartsWith("Aim.StepLeft_", StringComparison.Ordinal) || k.StartsWith("Aim.StepRight_", StringComparison.Ordinal))
            return Pick("Aim.Exit_" + k["Aim.".Length..], "Aim.Turn_ExactFacing", "Aim.Enter_0");
        if (k.StartsWith("Aim.Strafe", StringComparison.Ordinal))
            return Pick("Aim.Turn_ExactFacing", "Aim.Enter_0");
        if (k.Contains("_Crouch_", StringComparison.Ordinal))
            return Pick(k.Replace("_Crouch_", "_"), "Aim.Enter_0");
        if (k.EndsWith(".Crouched", StringComparison.Ordinal))
            return Pick(k[..^".Crouched".Length], "Idle.Turn_ExactFacing");
        if (k.EndsWith(".2HRifle", StringComparison.Ordinal) || k.EndsWith(".1HPistol", StringComparison.Ordinal))
            return Pick(k[..k.LastIndexOf('.')], "Aim.Enter_0");
        return Pick("Aim.Enter_0");
    }

    /// <summary>
    /// THE STANCE, measured 2026-09-07 in three steps. (1) With every human table and set out of
    /// the blaster copy a fresh BX still broke at the shot. (2) The player-side shot clips are
    /// not in the weapon spec's animation set: the rifle ACTOR (BP_Rifle_E-5) names a stance
    /// archetype, SASArch_2HRifle, which lists a stance set PER RIG -- Human as the default, then
    /// Kabb, B1, BX -- and a key the rig's set lacks falls to the Human one. (3) Kabb, a playable
    /// hero on his own rig, carries 47 keys; the BX's enemy set 33, and the 14 missing are the aim
    /// turns, steps and strafes a player's shot plays. The B2 has no version at all, so he is
    /// Human throughout. (The pawn's own StanceAnimationSets never came into it.)
    ///
    /// So: a copy of the rig's stance set with every key the playable set has and it lacks, each
    /// a clone of one of the rig's own entries (see StandInFor), its stance tag 2HRifle; and the
    /// archetype overridden to name it -- by rename where the rig has a version, by an added
    /// version where it has none. Nothing human is left for the shot to fall back on.
    /// </summary>
    public static bool UseStanceSet(IFileProvider provider, ModProject mod, ModCopy definition,
                                    bool on, out string? why)
    {
        why = null;
        var id = StanceSetIdFor(definition);
        var rigTag = RigTagOf(provider, definition.Template);
        if (rigTag.Length == 0) { why = "the template's rig tag could not be read"; return false; }
        var gameSet = ArchetypeVersionFor(provider, rigTag);
        var folder = WeaponAuthor.FolderFor(mod);
        var arch = mod.Overrides.FirstOrDefault(o => o.Target == RifleArchetype);

        if (!on)
        {
            RemoveStanceCopies(mod, definition, id);
            if (arch is not null)
            {
                if (gameSet.Length > 0)
                {
                    arch.RenameNames.Remove(gameSet);
                    arch.RenameNames.Remove(CharacterIndex.Recruit.Leaf(gameSet));
                }
                arch.AddVersions.RemoveAll(v => v.Rig == rigTag);
                if (!arch.HasEdit) mod.Overrides.Remove(arch);
            }
            // A pawn re-point from the earlier reading of this problem, if one is still there.
            var pawnOld = mod.Copies.FirstOrDefault(c => c.Id.Equals(PawnIdFor(definition), StringComparison.OrdinalIgnoreCase));
            if (pawnOld is not null)
                foreach (var k in pawnOld.RepointImports.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList())
                    pawnOld.RepointImports.Remove(k);
            return true;
        }

        // The rig's own set: the archetype's version for it, else the pawn's own.
        var source = gameSet;
        if (source.Length == 0)
        {
            var pawnPackage = SlotEdit.PackageOf(EnemyPawnFor(provider, definition.Template));
            if (pawnPackage.Length > 0) source = StanceSetOf(provider, pawnPackage).Set;
        }
        if (source.Length == 0) { why = "no stance set of this rig's could be found, in the rifle archetype or on the pawn"; return false; }
        var stance = StanceTagOf(provider, source);
        var have = StanceKeysOf(provider, source);
        var need = StanceKeysOf(provider, PlayableStanceSet);
        if (have.Count == 0 || need.Count == 0)
        {
            why = have.Count == 0
                ? $"no animation keys could be read from {CharacterIndex.Recruit.Leaf(source)}, this rig's stance set"
                : $"no animation keys could be read from {CharacterIndex.Recruit.Leaf(PlayableStanceSet)}, " +
                  "the playable set the missing keys are taken from";
            return false;
        }

        var copy = new ModCopy { Id = id, Template = source };
        if (stance.Length > 0 && stance != RifleStanceTag) copy.RenameNames[stance] = RifleStanceTag;
        var haveSet = new HashSet<string>(have, StringComparer.Ordinal);
        foreach (var key in need.Where(k => !haveSet.Contains(k)))
            if (StandInFor(key, haveSet) is { } like) copy.AddAnimations.Add((key, like));
        RemoveStanceCopies(mod, definition, id);
        mod.Copies.Add(copy);

        if (arch is null) { arch = new ModOverride { Target = RifleArchetype }; mod.Overrides.Add(arch); }
        if (gameSet.Length > 0)
        {
            arch.RenameNames[gameSet] = folder + id;
            arch.RenameNames[CharacterIndex.Recruit.Leaf(gameSet)] = id;
        }
        else
        {
            arch.AddVersions.RemoveAll(v => v.Rig == rigTag);
            arch.AddVersions.Add((folder + id, rigTag));
        }
        return true;
    }

    /// <summary>
    /// Every id a character's own features MINT for him -- the blaster, its gun kit, the stance
    /// set, his pawn, his recipes.
    ///
    /// Needed because "nothing points at it" is how a leftover is spotted, and several of these
    /// are pointed at by NOTHING on purpose: the Armoury finds a weapon class by scanning the
    /// registry for the slot tag, so a blaster copy is named by no other asset and is not loose.
    /// </summary>
    public static IEnumerable<string> FeatureIds(ModCopy definition)
    {
        yield return RifleIdFor(definition);
        yield return OwnGunPartId(definition);
        yield return OwnGunKitId(definition);
        yield return StanceSetIdFor(definition);
        yield return PawnIdFor(definition);
        for (var n = 1; n <= 12; n++) yield return RecipeIdFor(definition, n);
    }

    // ------------------------------------------------------------------ only their own gun

    /// <summary>The table every "typical shot" cinematic is chosen from, by tags on the character.</summary>
    public const string ShotCinematicTable = "/Game/Game/Abilities/Common/Typical/Fire/DT_AG_TypicalShot_MASTER";

    /// <summary>The class tag a definition's class part carries -- "...Class.Battledroid.BX". "" when none.</summary>
    public static string ClassTagOf(IFileProvider provider, ModCopy definition)
    {
        var classPart = SlotEdit.ReadFrom(provider, definition.Template, out _)
            .FirstOrDefault(s => s.Tail == "Character.Class")?.Path ?? "";
        if (classPart.Length == 0) return "";
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(SlotEdit.PackageOf(classPart))).GetExports().ToList());
            foreach (var e in exports.Where(x => x.ExportType == "CustomizationFragmentGameplayTags"))
                foreach (var t in PartDetails.TagsOf(e.Properties, "GameplayTags"))
                    if (t.StartsWith(ClassTagPrefix, StringComparison.Ordinal)) return t;
        }
        catch { }
        return "";
    }

    /// <summary>
    /// THE TAGS A DROID'S OWN SHOT CINEMATIC DEMANDS OF HIM, read from the table that chooses it.
    ///
    /// MEASURED 2026-09-07, after the per-rig fire sequence was fixed and the author reported the
    /// BX right on one shot and broken on the next: there is a SECOND animation, the cinematic
    /// "typical shot", and it is chosen from DT_AG_TypicalShot_MASTER -- 82 weighted rows, each
    /// with RequiredTags/BlockTags matched against the tags the CHARACTER carries. The BX's three
    /// rows require Weapon.Enemy.BX, Class.Battledroid.BX, Weapon.Blaster.Rifle and
    /// Weapon.Type.2HRifle.Rifle; the B1's and B2's require their Weapon.Enemy.X and
    /// Class.Battledroid.X. A recruit carries the class tag (his class part is the enemy's) and
    /// NOT Weapon.Enemy.X, which lives only on the enemy WEAPON SPEC he no longer wears -- and
    /// once "only their own gun" mints a class tag he loses Weapon.Blaster.Rifle as well. So no
    /// droid row can match, the pick falls to a human row, and a human cinematic drives a droid
    /// skeleton. Weighted rows are why it looked intermittent.
    ///
    /// Returns the union of the RequiredTags of every row that names this droid's class tag.
    /// </summary>
    public static List<string> ShotCinematicTagsFor(IFileProvider provider, ModCopy definition, string gunTypeTag)
    {
        var wanted = new List<string>();
        var blocked = new List<string>();
        var classTag = ClassTagOf(provider, definition);
        if (classTag.Length == 0) return wanted;
        const string TypePrefix = "br.Customization.Part.Weapon.Type.";
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(ShotCinematicTable)).GetExports().ToList());
            foreach (var table in exports.OfType<CUE4Parse.UE4.Assets.Exports.Engine.UDataTable>())
                foreach (var (_, row) in table.RowMap)
                {
                    var required = SelectionTagsIn(row, "RequiredTags");
                    if (!required.Contains(classTag, StringComparer.Ordinal)) continue;

                    // ONLY THE ROWS FOR THE GUN WE SHIP. The BX has rifle rows and a longarm row,
                    // and each blocks the other's weapon-type tags -- so taking the union of both
                    // hands him a set that blocks every row he could have matched. A row whose
                    // required weapon type is not ours, or an ancestor of ours, is not ours.
                    var mismatched = required.Any(t => t.StartsWith(TypePrefix, StringComparison.Ordinal) &&
                                                       !string.Equals(t, gunTypeTag, StringComparison.Ordinal) &&
                                                       !gunTypeTag.StartsWith(t + ".", StringComparison.Ordinal));
                    if (mismatched) continue;

                    foreach (var t in required)
                        if (!wanted.Contains(t, StringComparer.Ordinal)) wanted.Add(t);
                    foreach (var t in SelectionTagsIn(row, "BlockTags"))
                        if (!blocked.Contains(t, StringComparer.Ordinal)) blocked.Add(t);
                }
        }
        catch { }
        // Never hand him a tag one of his own rows blocks.
        wanted.RemoveAll(t => blocked.Contains(t, StringComparer.Ordinal));
        return wanted;
    }

    /// <summary>Every entry of one selection list on a cinematic row, global and per-category alike.</summary>
    private static List<string> SelectionTagsIn(FStructFallback row, string which)
    {
        var tags = new List<string>();
        void FromSelection(object? value)
        {
            if (PartDetails.Unwrap(value) is not FStructFallback sel) return;
            foreach (var t in PartDetails.TagsOf(sel.Properties, which))
                if (!tags.Contains(t, StringComparer.Ordinal)) tags.Add(t);
        }
        FromSelection(PartDetails.Value(row.Properties, "GlobalSelectionTags"));
        if (PartDetails.Unwrap(PartDetails.Value(row.Properties, "CategorizedSelectionTags")) is UScriptMap map)
            foreach (var kv in map.Properties) FromSelection(kv.Value?.GenericValue);
        return tags;
    }

    /// <summary>
    /// Whether a gear kit says where its weapon sits WHILE HELD -- a HeldWeaponSocket naming a
    /// custom socket, with an offset.
    ///
    /// MEASURED 2026-09-08, after the two socket NAMES were carried onto a copied human kit and
    /// the B2's bolts still came from his root: his own kit also carries
    /// HeldWeaponSocket = CustomSocket "Muzzle" with a 2cm offset, and the human rifle kit has no
    /// such property at all. The names alone were not enough; the held state is what decides where
    /// the weapon goes while he is carrying it. A kit that says this is the one to COPY, rather
    /// than to imitate two fields at a time -- the human kit's AssociatedAbilitySet, the one thing
    /// it has that his does not, grants nothing (AbilitySet_Gear_RifleStandard is empty).
    /// </summary>
    public static bool HasHeldSocket(IFileProvider provider, string gearKitPackage)
    {
        if (gearKitPackage.Length == 0) return false;
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(gearKitPackage)).GetExports().ToList());
            foreach (var e in exports.Where(x => (x.ExportType ?? "").Contains("GearItem", StringComparison.Ordinal)))
                if (PartDetails.Unwrap(PartDetails.Value(e.Properties, "HeldWeaponSocket")) is FStructFallback held &&
                    (PartDetails.Value(held.Properties, "CustomSocketName")?.ToString() ?? "").Length > 0)
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>The gear kit ASSET the droid's own kit part spawns his gun from, "" when unreadable.</summary>
    public static string OwnGearKitOf(IFileProvider provider, ModCopy definition)
    {
        var kitPart = OwnKitPartOf(provider, definition);
        if (kitPart.Length == 0) return "";
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(SlotEdit.PackageOf(kitPart))).GetExports().ToList());
            var kits = new List<string>();
            foreach (var e in exports.Where(x => x.ExportType == "CustomizationFragmentGearKits"))
            {
                if (PartDetails.Unwrap(PartDetails.Value(e.Properties, "GearKits")) is not UScriptArray arr) continue;
                foreach (var item in arr.Properties)
                    if (PartDetails.Unwrap(item.GenericValue) is FSoftObjectPath sop && (sop.AssetPathName.Text ?? "").Length > 0)
                    {
                        var one = SlotEdit.PackageOf(sop.AssetPathName.Text);
                        if (!kits.Contains(one, StringComparer.OrdinalIgnoreCase)) kits.Add(one);
                    }
            }
            return PreferredGearKit(kits);
        }
        catch { return ""; }
    }

    /// <summary>
    /// Which of a kit part's gear kits is the droid's GUN. See the comment in OwnGunOf: the B2's
    /// part lists his rocket ABILITY first and his blaster second.
    /// </summary>
    public static string PreferredGearKit(IReadOnlyList<string> kits) =>
        kits.FirstOrDefault(k => CharacterIndex.Recruit.Leaf(k).Contains("Range", StringComparison.OrdinalIgnoreCase))
        ?? kits.FirstOrDefault(k => !CharacterIndex.Recruit.Leaf(k).Contains("AreaWeap", StringComparison.OrdinalIgnoreCase))
        ?? kits.FirstOrDefault() ?? "";

    /// <summary>
    /// The two sockets a gear kit attaches its actor by: the socket on the CHARACTER and the
    /// socket on the WEAPON. ("", "") when the kit names none.
    ///
    /// MEASURED 2026-09-07 after the author's "the B2 lasers are coming from below him, probably
    /// at his root bone": the human rifle kit we copy attaches at CharacterSocket weaponSocket_0
    /// by GearSocket Attach; the B2's own kit uses CharacterSocket Muzzle by GearSocket Grip. A
    /// socket the skeleton does not have attaches the actor at the actor's origin, which on a
    /// character is the root -- so the blaster, and every bolt it fired, sat at his feet.
    ///
    /// Only the two names are taken. His kit also carries a HeldWeaponSocket and a 2cm
    /// SocketOffset that the human kit has no slot for at all, and adding an absent struct is a
    /// different and much larger job than renaming a name that is already there.
    /// </summary>
    public static (string Character, string Gear) GearSocketsOf(IFileProvider provider, string gearKitPackage)
    {
        if (gearKitPackage.Length == 0) return ("", "");
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(gearKitPackage)).GetExports().ToList());
            foreach (var e in exports.Where(x => (x.ExportType ?? "").Contains("GearItem", StringComparison.Ordinal)))
            {
                var ch = PartDetails.Value(e.Properties, "CharacterSocket")?.ToString() ?? "";
                var ge = PartDetails.Value(e.Properties, "GearSocket")?.ToString() ?? "";
                if (ch.Length > 0 || ge.Length > 0) return (ch, ge);
            }
        }
        catch { }
        return ("", "");
    }

    /// <summary>The rig tag a human wears, and the option a copied human kit therefore carries.</summary>
    public const string HumanoidRigTag = "br.Customization.Part.Character.Rig.Humanoid";

    /// <summary>
    /// The gear kit part a definition brings of its own: the GearKit slot when it names one, and
    /// otherwise the DEFAULT PART of its weapon spec, which is where an enemy's sits (the BX's
    /// CPD_WeaponSpec_Enemy_BXMarauder names CPD_GearKit_Rifle_E5_Enemy that way).
    /// </summary>
    public static string OwnKitPartOf(IFileProvider provider, ModCopy definition)
    {
        var slots = SlotEdit.ReadFrom(provider, definition.Template, out _);
        var kitPart = slots.FirstOrDefault(s => s.Tail == "Character.Specializations.Weapon.GearKit")?.Path ?? "";
        if (kitPart.Length > 0) return kitPart;
        var spec = slots.FirstOrDefault(s => s.Tail == "Character.Specializations.Weapon")?.Path ?? "";
        return spec.Length > 0 ? DefaultPartOf(provider, SlotEdit.PackageOf(spec)) : "";
    }

    /// <summary>
    /// THE SHOT ANIMATION, and it is not an animation set at all. MEASURED 2026-09-07 after three
    /// fixes aimed at animation sets all failed in game: a gear kit grants a "PFA shot" ability
    /// per RIG, through a variant selector keyed on the rig tag, and that ability holds four LEVEL
    /// SEQUENCES -- LS_NoCover, LS_LowCover, LS_HighCoverLeft/Right -- which ARE the fire
    /// animation. GA_PFAShot_Human_2HRifle_VT51 plays LS_AG_2HRifle_PFA_VT51_01; the BX's
    /// GA_PFAShot_BX_E5 plays LS_AG_BX_Fire_E5_PFA_01. A human sequence bound to a droid mesh is
    /// exactly the detached arms and twisted head the author saw.
    ///
    /// Returns the ability this kit grants for <paramref name="rigTag"/>, or -- when the kit has
    /// no variant selector at all, which is how the B2's kit is built -- the one ability it
    /// grants outright. "" when nothing matches.
    /// </summary>
    public static string PfaShotOf(IFileProvider provider, string gearKitPart, string rigTag)
    {
        if (gearKitPart.Length == 0) return "";
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(SlotEdit.PackageOf(gearKitPart))).GetExports().ToList());

            static string AbilityIn(UObject e)
            {
                if (PartDetails.Unwrap(PartDetails.Value(e.Properties, "Abilities")) is not UScriptArray arr) return "";
                foreach (var item in arr.Properties)
                    if (PartDetails.Unwrap(item.GenericValue) is FSoftObjectPath sop &&
                        (sop.AssetPathName.Text ?? "").Length > 0)
                        return sop.AssetPathName.Text;
                return "";
            }

            var selectors = exports.Where(x => x.ExportType == "CustomizationFragmentVariantSelector").ToList();
            foreach (var e in selectors)
                foreach (var opt in PartDetails.StructArray(e.Properties, "OPTIONS"))
                {
                    if (!PartDetails.TagsOf(opt.Properties, "RequiredTags").Contains(rigTag, StringComparer.Ordinal)) continue;
                    if (PartDetails.Unwrap(PartDetails.Value(opt.Properties, "Fragments")) is not UScriptArray frags) continue;
                    foreach (var f in frags.Properties)
                    {
                        var name = "";
                        try { name = PartDetails.Unwrap(f.GenericValue) is FPackageIndex pi ? pi.ResolvedObject?.Name.Text ?? "" : ""; }
                        catch { }
                        var target = exports.FirstOrDefault(x => x.Name == name);
                        if (target is not null && AbilityIn(target) is { Length: > 0 } got) return got;
                    }
                }

            // No rig options at all: the kit grants one shot outright, as the B2's does.
            if (selectors.Count == 0)
                foreach (var e in exports.Where(x => x.ExportType == "CustomizationFragmentAbilities"))
                    if (AbilityIn(e) is { Length: > 0 } only) return only;
        }
        catch { }
        return "";
    }

    /// <summary>The Armoury part for the droid's own gun, routed into the blaster's own class.</summary>
    public static string OwnGunPartId(ModCopy definition) => WeaponIndex.GearKitPartPrefix + "Rifle_" + StemOf(definition);
    public static string OwnGunKitId(ModCopy definition) => "GK_Rifle_" + StemOf(definition);
    /// <summary>The class tag minted for the "&lt;rig&gt; blaster", so only kits carrying it are listed in it.</summary>
    public static string OwnClassTag(ModCopy definition) => WeaponAuthor.ClassTag(StemOf(definition));

    public static bool UsesOwnGun(ModProject mod, ModCopy definition) =>
        mod.Copies.Any(c => c.Id.Equals(OwnGunPartId(definition), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The Armoury weapon that IS the droid's own gun, read rather than named: the template's gear
    /// kit part -> the gear kit it lists -> the actor that kit spawns -> the weapon index entry
    /// with that actor. The BX and the B1 both spawn BP_Rifle_E-5, so both come out as the E-5;
    /// the B2's kit spawns wrist blasters no Armoury weapon has, so it comes out null.
    /// </summary>
    public static WeaponIndex.Weapon? OwnGunOf(IFileProvider provider, ModCopy definition, out string why)
    {
        why = "";
        var kitPart = OwnKitPartOf(provider, definition);
        if (kitPart.Length == 0) { why = "the template names no gear kit of its own, and its weapon spec has no default part"; return null; }

        string gearKit = "", actor = "";
        try
        {
            var kitExports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(SlotEdit.PackageOf(kitPart))).GetExports().ToList());
            // EVERY kit it lists, then the one that is his GUN. A kit can list several: the B2's
            // holds GK_AreaWeap_WristRocket (his rocket ABILITY, which the author heard exploding
            // in the distance every time he fired) and GK_RangedWeap_WristBlaster (his gun). The
            // ranged one is the shot -- and it is the weapon his own cinematic row names,
            // LS_AG_B2_StandardShot_WristBlaster_Base. "Range" rather than "Ranged": the droids'
            // rifle kit is spelled GK_RangeWeap_Rifle_E5_Enemy.
            var kits = new List<string>();
            foreach (var e in kitExports.Where(e => e.ExportType == "CustomizationFragmentGearKits"))
            {
                if (PartDetails.Unwrap(PartDetails.Value(e.Properties, "GearKits")) is not UScriptArray arr) continue;
                foreach (var item in arr.Properties)
                    if (PartDetails.Unwrap(item.GenericValue) is FSoftObjectPath sop && (sop.AssetPathName.Text ?? "").Length > 0)
                    {
                        var one = SlotEdit.PackageOf(sop.AssetPathName.Text);
                        if (!kits.Contains(one, StringComparer.OrdinalIgnoreCase)) kits.Add(one);
                    }
            }
            gearKit = PreferredGearKit(kits);
            if (gearKit.Length == 0) { why = $"{SlotEdit.LeafOf(kitPart)} lists no gear kit"; return null; }

            var gkExports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(gearKit)).GetExports().ToList());
            foreach (var e in gkExports)
            {
                var v = PartDetails.Unwrap(PartDetails.Value(e.Properties, "ActorClass"));
                var path = "";
                try
                {
                    path = v is FPackageIndex pi ? pi.ResolvedObject?.GetPathName() ?? ""
                         : v is FSoftObjectPath sp ? sp.AssetPathName.Text ?? "" : "";
                }
                catch { }
                if (path.Length > 0) { actor = SlotEdit.PackageOf(path); break; }
            }
        }
        catch (Exception e) { why = e.Message; return null; }
        if (actor.Length == 0) { why = $"{CharacterIndex.Recruit.Leaf(gearKit)} spawns no actor this can read"; return null; }

        var weapon = WeaponIndex.Of(provider)
            .FirstOrDefault(w => w.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase));
        // A real Armoury weapon has a type part. The index also lists every actor in the gear
        // folder split at its first underscore, so BP_B2_WristRocket comes back as class "B2",
        // name "WristRocket" -- an entry with no type part, handled below like a miss.
        if (weapon is not null && weapon.TypePart.Length > 0) return weapon;

        // NOT AN ARMOURY WEAPON, AND STILL A GUN. The B2's kit spawns BP_B2_WristRocket: a plain
        // mesh actor with no loadout, no type part and no customization definition behind it, so
        // the index -- which is keyed on those -- never lists it. A gear kit does not need them:
        // it spawns an actor, and the enemy B2 fights with exactly this one equipped. So the actor
        // stands in as the weapon, named after the rig; with no type part there is no type tag to
        // rename, and no palette serves it meshes -- it draws its own.
        var rig = RigOf(provider, definition.Template);
        var leaf = CharacterIndex.Recruit.Leaf(actor);
        var name = leaf.StartsWith("BP_", StringComparison.Ordinal) ? leaf[3..] : leaf;
        if (rig.Length > 0 && name.StartsWith(rig + "_", StringComparison.OrdinalIgnoreCase)) name = name[(rig.Length + 1)..];
        name = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").Replace('_', ' ');
        return new WeaponIndex.Weapon(Key: "Rifle_" + name.Replace(" ", ""), Class: "Rifle",
                                      Name: rig.Length > 0 ? $"{rig} {name.ToLowerInvariant()}" : name,
                                      GearKitPart: "", GearKit: "", Actor: actor, Loadout: "", TypePart: "",
                                      Missing: new[] { "CD_", "CPD_WP_Type_" });
    }

    /// <summary>
    /// ONLY THEIR OWN GUN -- the author's ask (2026-09-07): "add the BX real weapon into that BX
    /// Blaster list and remove all the other weapons in it".
    ///
    /// The "&lt;rig&gt; blaster" is a copy of the hero rifle CLASS, and the Armoury lists in a
    /// class every gear kit whose AllowedSlots carry the class's identity tag -- which is why the
    /// copy showed every rifle. So: (1) the copy's identity tag becomes one minted for it
    /// (`...Weapon.Blaster.&lt;Stem&gt;`, shipped in tags[]), so no game kit carries it; (2) the
    /// droid's own gun gets a gear kit the way "make it playable" gives one -- a same-class donor's
    /// kit with its actor re-pointed at BP_Rifle_E-5, and the donor's Armoury part with its class
    /// tag renamed to the minted one and its type tag to the E-5's, so the palettes serve E-5
    /// meshes; (3) the copy's DefaultPart names that kit, so the class's default weapon is in the
    /// class. Same recipe as the custom weapon class, proven in game 2026-09-03. Ids carry the
    /// stem, so two mods (and the author's own E5_Blaster) never claim one id.
    /// </summary>
    public static bool UseOwnGun(IFileProvider provider, ModProject mod, ModCopy definition,
                                 bool on, out string? why)
    {
        why = null;
        var rifle = mod.Copies.FirstOrDefault(c => c.Id.Equals(RifleIdFor(definition), StringComparison.OrdinalIgnoreCase));
        var partId = OwnGunPartId(definition);
        var kitId = OwnGunKitId(definition);
        var minted = OwnClassTag(definition);
        var rifleTag = WeaponAuthor.ClassTag("Rifle");
        var heroDefault = DefaultPartOf(provider, HeroRifleSpec);   // CPD_GK_Rifle_Relby-v10

        if (!on)
        {
            mod.Copies.RemoveAll(c => c.Id.Equals(partId, StringComparison.OrdinalIgnoreCase) ||
                                      c.Id.Equals(kitId, StringComparison.OrdinalIgnoreCase));
            mod.Tags.RemoveAll(t => t == minted);
            if (rifle is not null)
            {
                rifle.RenameNames.Remove(rifleTag);
                if (heroDefault.Length > 0)
                {
                    rifle.RenameNames.Remove(heroDefault);
                    rifle.RenameNames.Remove(CharacterIndex.Recruit.Leaf(heroDefault));
                }
            }
            return true;
        }

        if (rifle is null) { why = "ship the blaster first -- this narrows what it lists"; return false; }
        if (heroDefault.Length == 0) { why = "the hero rifle spec's default weapon could not be read"; return false; }
        var weapon = OwnGunOf(provider, definition, out var gunWhy);
        if (weapon is null) { why = gunWhy; return false; }
        // The donor is always a RIFLE kit: the blaster is a copy of the rifle class, and the kit
        // carries the ability set and item tags that say what kind of thing this is.
        var all = WeaponIndex.Of(provider);
        var donor = all.Where(w => w.Complete && w.Class.Equals("Rifle", StringComparison.OrdinalIgnoreCase) &&
                                   !w.Key.EndsWith("_Enemy", StringComparison.OrdinalIgnoreCase))
                       .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (donor is null) { why = "no rifle has a gear kit to copy one from"; return false; }

        var folder = WeaponAuthor.FolderFor(mod);
        mod.Copies.RemoveAll(c => c.Id.Equals(partId, StringComparison.OrdinalIgnoreCase) ||
                                  c.Id.Equals(kitId, StringComparison.OrdinalIgnoreCase));

        // The gear kit: the donor's, its actor re-pointed at the gun's. A HARD import, so it
        // moves through repointImports and takes the actor's stored export hashes with it.
        // HIS OWN KIT WHEN IT SAYS WHERE THE GUN IS HELD -- see HasHeldSocket. Copying it brings
        // the actor, both socket names, the held socket and its offset together, which is what the
        // B2's wrist blaster needs and what carrying two names onto a human kit could not give
        // him. A kit that does not say (the droids' E-5) keeps the human donor, whose blaster is
        // held the ordinary way and is confirmed right in game.
        var ownKit = OwnGearKitOf(provider, definition);
        ModCopy kit;
        if (HasHeldSocket(provider, ownKit))
        {
            kit = new ModCopy { Id = kitId, Template = ownKit, Display = weapon.Name };
        }
        else
        {
            kit = new ModCopy { Id = kitId, Template = donor.GearKit, Display = weapon.Name };
            kit.RepointImports[donor.Actor] = weapon.Actor;
            var mineSockets = GearSocketsOf(provider, ownKit);
            var theirSockets = GearSocketsOf(provider, donor.GearKit);
            if (mineSockets.Character.Length > 0 &&
                !string.Equals(mineSockets.Character, theirSockets.Character, StringComparison.Ordinal))
                kit.SetNames.Add(new ModCopy.NameSet("ActorGearItem", "CharacterSocket", mineSockets.Character));
            if (mineSockets.Gear.Length > 0 &&
                !string.Equals(mineSockets.Gear, theirSockets.Gear, StringComparison.Ordinal))
                kit.SetNames.Add(new ModCopy.NameSet("ActorGearItem", "GearSocket", mineSockets.Gear));
        }
        mod.Copies.Add(kit);

        // The Armoury part: names the kit, carries the minted class tag, and the gun's type tag
        // so the shared palettes serve its meshes.
        var part = new ModCopy { Id = partId, Template = donor.GearKitPart, Display = weapon.Name };
        part.RenameNames[donor.GearKit] = folder + kitId;
        part.RenameNames["GK_" + donor.Key] = kitId;
        // The class it is LISTED under moves by a container edit, not a rename: the same FName
        // sits in GameplayTags, where the shot cinematic wants it left alone. See ModCopy.TagEdit.
        part.TagEdits.Add(new ModCopy.TagEdit("AllowedSlots", new List<string> { minted },
                                                  new List<string> { rifleTag }));
        if (weapon.TypePart.Length > 0 &&
            WeaponAuthor.TypeTagOf(provider, donor.TypePart) is { } fromTag &&
            WeaponAuthor.TypeTagOf(provider, weapon.TypePart) is { } toTag &&
            !string.Equals(fromTag, toTag, StringComparison.Ordinal))
            part.RenameNames[fromTag] = toTag;

        // AND THE SHOT ITSELF -- see PfaShotOf. The donor is a human kit, so its rig option is
        // Humanoid and the sequences it plays are a human's. The option becomes this rig's and
        // points at the shot the droid's OWN kit grants him, which is the one the enemy version
        // of him fires with. Both halves of the soft path: the package and the class name.
        var rigTag2 = RigTagOf(provider, definition.Template);
        var mine = PfaShotOf(provider, OwnKitPartOf(provider, definition), rigTag2);
        var theirs = PfaShotOf(provider, donor.GearKitPart, HumanoidRigTag);
        if (rigTag2.Length > 0 && mine.Length > 0 && theirs.Length > 0 &&
            !string.Equals(mine, theirs, StringComparison.Ordinal))
        {
            part.RenameNames[HumanoidRigTag] = rigTag2;
            part.RenameNames[SlotEdit.PackageOf(theirs)] = SlotEdit.PackageOf(mine);
            // The package path and the CLASS name are the two FNames a soft path is made of; the
            // bare leaf is not in the map, and asking for it only prints a line that trains the
            // eye to ignore "not in the name map".
            part.RenameNames[theirs[(theirs.LastIndexOf('.') + 1)..]] = mine[(mine.LastIndexOf('.') + 1)..];
        }
        // AND THE TAGS HIS OWN SHOT CINEMATIC DEMANDS -- see ShotCinematicTagsFor. They go on the
        // KIT rather than on the blaster: the Armoury lists a class by what a kit's AllowedSlots
        // say, so what the kit TELLS THE CHARACTER cannot widen the list, and the tags apply
        // exactly while he is holding this gun.
        var gunType = weapon.TypePart.Length > 0 ? WeaponAuthor.TypeTagOf(provider, weapon.TypePart) ?? "" : "";
        var cine = ShotCinematicTagsFor(provider, definition, gunType);
        if (cine.Count > 0) part.TagEdits.Add(new ModCopy.TagEdit("GameplayTags", cine, new List<string>()));
        mod.Copies.Add(part);

        // The blaster: its own identity, and its own default weapon -- both halves of the soft path.
        rifle.RenameNames[rifleTag] = minted;
        rifle.RenameNames[heroDefault] = folder + partId;
        rifle.RenameNames[CharacterIndex.Recruit.Leaf(heroDefault)] = partId;
        if (!mod.Tags.Contains(minted)) mod.Tags.Add(minted);
        Stamp(mod, definition, "own-gun");
        return true;
    }

    // ------------------------------------------------------------------ the pawn, decided by the rig

    /// <summary>
    /// The rig a definition names in its Character.Rig slot -- "Astromech", "B1", "Humanoid",
    /// "Droideka" -- or "" when it cannot be read.
    /// </summary>
    public static string RigOf(IFileProvider provider, string definition)
    {
        if (definition.Length == 0) return "";
        var rig = SlotEdit.ReadFrom(provider, definition, out _)
                          .FirstOrDefault(s => s.Tail == "Character.Rig")?.Leaf ?? "";
        const string prefix = "CPD_Character_Rig_";
        return rig.StartsWith(prefix, StringComparison.Ordinal) ? rig[prefix.Length..] : rig;
    }

    /// <summary>
    /// Which of the game's two hero pawns a rig can live on. MEASURED, 2026-09-06: the game has
    /// exactly Char_Hero_Humanoid and Char_Hero_Astromech; of the Recruitable mod's 502 rows, 498
    /// -- every B1, B2, BX, Droideka and Magnaguard -- ride the humanoid pawn, and only the four
    /// R-units ride the astromech. The author's own B1 on the astromech pawn was INVISIBLE on the
    /// squad screen: a floating rifle and no body.
    /// </summary>
    public static string PawnFor(string rig) =>
        rig.Contains("Astromech", StringComparison.OrdinalIgnoreCase) ? "Astromech" : "Humanoid";

    /// <summary>
    /// <see cref="LooksWrong(CharacterIndex.Recruit, string)"/> with the definition READ rather
    /// than its path guessed at: the rig it names against the pawn the row spawns.
    /// </summary>
    public static string? LooksWrong(IFileProvider provider, CharacterIndex.Recruit row, string target)
    {
        var rig = RigOf(provider, target);
        if (rig.Length == 0) return LooksWrong(row, target);
        var wants = PawnFor(rig);
        if (row.Kind.Contains(wants, StringComparison.OrdinalIgnoreCase)) return null;
        return $"That character's rig is {rig}, which lives on the {wants.ToLowerInvariant()} pawn; " +
               $"this row spawns {row.Kind}. A B1 on the astromech pawn was INVISIBLE on the squad " +
               "screen -- a floating weapon and no body. Droids is for R-units; everyone else, " +
               "droid or not, belongs in the databank or the clones.";
    }
}

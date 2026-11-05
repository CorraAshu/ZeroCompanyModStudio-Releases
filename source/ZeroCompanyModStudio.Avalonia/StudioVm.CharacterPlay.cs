using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// HOW A CHARACTER PLAYS, and making them again.
///
/// Seven boxes, each one a MEASURED RECIPE and several still untested in game. They exist because
/// the hiring pool ROLLS a recruit's class, tactical spec and weapon spec away whatever the data
/// says — seen in game twice — so a droid recruited through the pool arrives with hero animations
/// and no action points. Each box is one attempt at getting some of that back, and the blurb under
/// each says what was actually observed rather than what was hoped.
///
/// NOTHING HERE IS ON BY DEFAULT except the pawn, which a droid-rigged enemy gets at creation.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// Whose "only their own gun" was on when their weapon was last taken away.
    ///
    /// Unticking the weapon removes the gun kit WITH it — the kit belongs to the weapon — and
    /// re-ticking then made a blaster listing every rifle again, because the only record that the
    /// narrow list had ever been wanted was the copy that had just been deleted. The author was
    /// told to untick and re-tick that box to pick up a fix, and it silently undid a choice he
    /// had made (2026-09-08).
    /// </summary>
    private readonly HashSet<string> _hadOwnGun = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ what the card offers

    /// <summary>
    /// Which boxes this character gets, and which of them are on.
    ///
    /// EVERY ONE IS CONDITIONAL ON THE GAME DATA. A hero-rigged recruit needs none of this and is
    /// shown none of it; a template with no enemy kit cannot keep one. A card of toggles that do
    /// nothing for this character is worse than no card.
    /// </summary>
    private async Task FillPlaysAsync(CharacterTab tab)
    {
        if (_mod is null || _provider is not { } provider) return;

        var mod = _mod;
        var copy = tab.Copy;
        var template = copy.Template;

        // BOTH PACKAGE READS IN ONE HOP off the UI thread. Asked separately they were two
        // round trips on every tick of every box.
        var read = await Task.Run(() =>
        {
            var rig = "";
            var pawn = "";
            try { rig = CharacterAuthor.RigOf(provider, template); } catch (Exception) { }
            try { pawn = CharacterAuthor.EnemyPawnFor(provider, template); } catch (Exception) { }
            return (Rig: rig, Pawn: pawn);
        });

        tab.RigName = read.Rig;

        // A HERO RIG NEEDS NONE OF THIS. Humanoid and Astromech are the two the hero pawns
        // actually animate; everything else moves like a person unless something here is done.
        var heroRig = read.Rig.Length == 0 ||
                      read.Rig.Equals("Humanoid", StringComparison.OrdinalIgnoreCase) ||
                      read.Rig.Equals("Astromech", StringComparison.OrdinalIgnoreCase);

        var np = CharacterAuthor.NamePartOf(mod, copy);

        tab.RigWarn = heroRig
            ? ""
            : $"A {read.Rig} rig on a hero pawn stands and moves like a human: the game picks hero " +
              "animations by weapon class and has none for this rig. Seen in game, 2026-09-06.";

        // THE KIT, for an enemy template: the four parts a recruit would otherwise lose.
        tab.ShowEnemyKit = _charSlotsWhy is null && CharacterAuthor.HasEnemyKit(_charSlots);
        tab.PlayEnemyKit = CharacterAuthor.UsesEnemyKit(mod, copy);

        // THE RIG BLOCK. The name part is part of the condition because two of these write to it.
        var rigBlock = !heroRig && np is not null;
        var hasPawn = rigBlock && read.Pawn.Length > 0;

        tab.ShowEnemyPawn = hasPawn;
        tab.PawnFrom = read.Pawn.Length > 0 ? Leaf(SlotEdit.PackageOf(read.Pawn)) : "";
        tab.PlayEnemyPawn = CharacterAuthor.UsesEnemyPawn(mod, copy);

        tab.ShowStory = hasPawn;
        tab.PlayStory = CharacterAuthor.UsesStoryRecruit(mod, copy);
        tab.StoryCount = CharacterAuthor.StoryRecruitCount(mod, copy).ToString();

        tab.ShowRifle = hasPawn;
        tab.PlayRifle = CharacterAuthor.UsesRigRifle(mod, copy);
        tab.PlayOwnGun = CharacterAuthor.UsesOwnGun(mod, copy);

        // THE KIT IMPLIES THE TACTICAL SPEC, because the kit ships one. Shown as on rather than
        // as a box that is off beside four copies that exist.
        tab.ShowTactical = hasPawn;
        tab.PlayTactical = CharacterAuthor.UsesTacticalSpec(mod, copy) ||
                           CharacterAuthor.UsesEnemyKit(mod, copy);

        tab.ShowRigAnim = rigBlock;
        tab.PlayRigAnim = np is not null && CharacterAuthor.UsesRigAnimations(np);

        // THE SPARE SLOT needs both a spec worth carrying and an empty slot to carry it in.
        var tspec = _charSlotsWhy is null ? CharacterAuthor.TacticalSpecOf(_charSlots) : "";
        var spare = _charSlotsWhy is null ? CharacterAuthor.SpareSlotFor(_charSlots) : "";
        tab.ShowSpareSpec = rigBlock && tspec.Length > 0 && spare.Length > 0;
        tab.PlaySpareSpec = CharacterAuthor.UsesRigTacticalSpec(copy);
        tab.SpareSays = tab.ShowSpareSpec
            ? $"{SlotEdit.LeafOf(tspec)} carries the move, turn and action point basics, and it " +
              $"would ride in {spare.Replace(SlotEdit.TagPrefix, "")}, which the roll never touches."
            : "";

        tab.SecPlays.Present = true;
        tab.PlaysSettled();

        FillOffer(tab);

        var on = new[] { tab.PlayEnemyKit, tab.PlayEnemyPawn, tab.PlayStory, tab.PlayRifle,
                         tab.PlayTactical, tab.PlayRigAnim, tab.PlaySpareSpec }.Count(x => x);
        tab.SecPlays.Count = on == 0 ? "" : $"{on} on";
    }

    // ------------------------------------------------------------------ ticking one

    /// <summary>
    /// One box, applied.
    ///
    /// THE BOX HAS ALREADY MOVED by the time this runs — its binding wrote the new value — so the
    /// wanted state is read off the tab rather than passed in. Whatever happens, the card is
    /// filled again from the project afterwards, which is what puts a refused box back where it
    /// belongs instead of leaving it showing a change that did not happen.
    /// </summary>
    public async Task SetPlay(CharacterTab tab, string which)
    {
        if (_mod is null || _provider is not { } provider) return;

        var mod = _mod;
        var copy = tab.Copy;
        string? why = null;
        var ok = true;
        var note = "";

        switch (which)
        {
            case "kit":
            {
                var on = tab.PlayEnemyKit;
                if (on)
                {
                    // THE TWO EXPERIMENTS WERE GUESSES AT THIS SAME PROBLEM. Left on beside the
                    // kept kit, one doubles the weapon spec and the other fights the kept table.
                    // Turned off quietly rather than refused, because the kit supersedes both.
                    var recruits = await GameRecruitsAsync();
                    if (CharacterAuthor.NamePartOf(mod, copy) is { } np &&
                        CharacterAuthor.UsesRigAnimations(np))
                        CharacterAuthor.UseRigAnimations(provider, mod, recruits, copy, false, out _);
                    if (CharacterAuthor.UsesRigWeaponSpec(copy))
                        CharacterAuthor.UseRigWeaponSpec(_charSlots, copy, false, out _);
                }

                ok = CharacterAuthor.UseEnemyKit(provider, mod, copy, on, out why);
                note = on
                    ? "Their class, tactical spec, weapon spec and gear kit are now copies of the " +
                      "enemy's own, made legal for a recruit. The two experiments are off. Build and see."
                    : "Their kit slots are the template's again, and the copies are gone.";
                break;
            }

            case "pawn":
            {
                var on = tab.PlayEnemyPawn;
                ok = CharacterAuthor.UseEnemyPawn(provider, mod, copy, on, out why);
                note = on
                    ? "Their row now spawns a pawn of the mod's own that wears this definition, " +
                      "and their Faction slot is Zero Company's. Build and see."
                    : "Their row spawns the pool's pawn again, and the pawn copy is gone unless " +
                      "the story recipe still uses it.";
                break;
            }

            case "story":
            {
                var on = tab.PlayStory;
                var count = Count(tab.StoryCount);
                ok = CharacterAuthor.UseStoryRecruit(provider, mod, copy, on, out why, count);
                note = on
                    ? $"A pawn class, {count} recruit recipe(s) and their entries in " +
                      $"{CharacterAuthor.StoryRecipeTables.Length} draw tables are in the mod. " +
                      "Build and install; they join within a cycle or two of play each."
                    : "The pawn class, the recipes and the draw-table entries are gone.";
                break;
            }

            case "rifle":
            {
                var on = tab.PlayRifle;

                // REMEMBERED BEFORE IT IS DESTROYED. See _hadOwnGun.
                if (!on && CharacterAuthor.UsesOwnGun(mod, copy)) _hadOwnGun.Add(copy.Id);

                ok = CharacterAuthor.UseRigRifle(provider, mod, copy, on, out why);

                if (ok && on && _hadOwnGun.Remove(copy.Id) &&
                    !CharacterAuthor.UsesOwnGun(mod, copy))
                    CharacterAuthor.UseOwnGun(provider, mod, copy, true, out _);

                note = on
                    ? $"{CharacterAuthor.RifleIdFor(copy)} is in the mod: a Blaster Rifle whose " +
                      "animations are this rig's own. Build and install, then equip it on them in " +
                      "the Armoury."
                    : "The weapon is gone.";
                break;
            }

            case "owngun":
            {
                var on = tab.PlayOwnGun;
                ok = CharacterAuthor.UseOwnGun(provider, mod, copy, on, out why);
                var gun = mod.Copies
                             .FirstOrDefault(x => x.Id == CharacterAuthor.OwnGunPartId(copy))?.Display
                          ?? "their gun";
                note = on
                    ? $"The blaster now has a class tag of its own and lists one gear kit: the " +
                      $"{gun}. In the Armoury the blaster's list is that gun alone."
                    : "The blaster lists every rifle again, and the gear kit is gone.";
                break;
            }

            case "tactical":
            {
                var on = tab.PlayTactical;
                ok = CharacterAuthor.UseTacticalSpec(provider, mod, copy, on, out why);
                note = on
                    ? "Their tactical spec is a copy of the enemy's own, made legal for a recruit: " +
                      "the move and turn basics come with it. A STORY recruit keeps it; the hiring " +
                      "roll replaces it on a pool recruit — see the spare slot box below."
                    : "Their tactical spec is the template's again, and the copy is gone.";
                break;
            }

            case "riganim":
            {
                var on = tab.PlayRigAnim;
                var recruits = await GameRecruitsAsync();
                ok = CharacterAuthor.UseRigAnimations(provider, mod, recruits, copy, on, out why);
                note = on
                    ? "Their name part now carries the rig's rifle animation table. Build, give " +
                      "them a rifle, and see."
                    : "Their name part is the pool's plain one again.";
                break;
            }

            case "sparespec":
            {
                var on = tab.PlaySpareSpec;
                ok = CharacterAuthor.UseRigTacticalSpec(_charSlots, copy, on, out why);
                note = on
                    ? "The rig's own tactical spec rides in a spare slot the roll never touches. " +
                      "Build and install; a FRESH recruit tells whether the basics apply from there."
                    : "The spare slot is empty again.";
                break;
            }

            default:
                return;
        }

        if (!ok)
        {
            Say($"{copy.Id}: {why ?? "could not change that"}");
            await FillPlaysAsync(tab);
            return;
        }

        mod.Save();
        tab.SlotNote = note;
        await FillPlaysAsync(tab);
        FillRemake(tab);
        tab.Raise2(nameof(SheetTab.Sheet));
        Say($"{copy.Id}: {note}");
        AfterEditsChanged();
    }

    /// <summary>How many story recipes to ship. A recipe runs once, so each joiner needs one.</summary>
    public async Task SetStoryCount(CharacterTab tab)
    {
        if (!tab.PlayStory) return;
        await SetPlay(tab, "story");
    }

    /// <summary>1 to 20, and anything else is 1. Twenty recipes is already an odd thing to want.</summary>
    private static int Count(string text)
        => int.TryParse(text, out var n) && n is >= 1 and <= 20 ? n : 1;

    // ------------------------------------------------------------------ making them again

    /// <summary>
    /// Which of this character's features were built by an older recipe.
    ///
    /// A COPY WITH NO VERSION RECORDED COUNTS AS OLDER: it was made before the tool kept track, so
    /// there is no way to show it matches what these boxes build now.
    /// </summary>
    private void FillRemake(CharacterTab tab)
    {
        if (_mod is null) { tab.SecRemake.Present = false; return; }

        var stale = CharacterAuthor.Stale(_mod, tab.Copy);
        tab.RemakeSays = stale.Count == 0
            ? ""
            : "Built by an older version of this tool: " +
              Listed(stale.Select(f => f.Label).ToList()) + ".";

        tab.SecRemake.Present = stale.Count > 0;
        tab.RemakeReady = _provider is not null;
    }

    /// <summary>
    /// The card is there for anybody this mod OFFERS, and it shows the plan before the press.
    ///
    /// NOT CONDITIONAL ON ANYTHING MEASURABLE, deliberately. There is no way from here to tell a
    /// recruit the game has stopped drawing from one it simply has not drawn yet -- the author is
    /// the one who can see they never come up. Hiding the card until some detectable condition is
    /// met would hide it exactly when it is needed.
    /// </summary>
    private void FillOffer(CharacterTab tab)
    {
        if (_mod is null) { tab.SecOffer.Present = false; return; }

        var plan = CharacterAuthor.OfferAgain(_mod, tab.Copy, apply: false, out _);
        tab.SecOffer.Present = plan is not null;
        tab.OfferSays = plan is null ? "" : "Would " + Renames(plan) + ".";
    }

    /// <summary>
    /// The plan as a SENTENCE, not a list of object paths.
    ///
    /// Two of the four entries are the full package path of the definition before and after, which
    /// is the same two names again with a folder on the front -- shown in full they wrapped to
    /// four lines of near-identical text and buried the two names that matter. The whole plan
    /// still goes to the log, where the detail belongs. [[check-the-ui-fits-before-shipping]]
    /// </summary>
    private static string Renames(IReadOnlyList<CharacterAuthor.Offer> plan)
    {
        var character = plan.FirstOrDefault(c => c.What == "the character");
        var row = plan.FirstOrDefault(c => c.What == "its pool row");
        var rest = plan.Count - (character is null ? 0 : 1) - (row is null ? 0 : 1);
        var said = "rename " + (character is null ? "" : $"{character.From} to {character.To}") +
                   (character is not null && row is not null ? ", and " : "") +
                   (row is null ? "" : $"their pool row {row.From} to {row.To}");
        return rest <= 0 ? said
                         : said + $", moving the {rest} reference{(rest == 1 ? "" : "s")} that " +
                           "point at them";
    }

    /// <summary>
    /// Give a recruit a new identity so the game offers them again.
    ///
    /// The PLAN IS SHOWN AGAIN AFTERWARDS rather than a "done": this renames assets, and an
    /// author who cannot read which names moved cannot tell a rename that worked from one that
    /// half-worked -- the fault that ships a character quietly wearing the enemy's own definition.
    /// </summary>
    public async Task OfferAgain(CharacterTab tab)
    {
        if (_mod is null) return;

        var was = tab.Copy.Id;
        var plan = CharacterAuthor.OfferAgain(_mod, tab.Copy, apply: true, out var why);
        if (plan is null)
        { Say($"{was}: {why ?? "could not offer them again"}"); return; }

        _mod.Save();

        tab.EditId = tab.Copy.Id;
        tab.OfferSays = "Renamed — " + Renames(plan).Replace("rename ", "") +
                        ". Build and install, and they are offered again.";
        foreach (var c in plan) Say($"[offer] {c.What}: {c.From} -> {c.To}");
        Say($"{was}: offered again as {tab.Copy.Id}");

        await FillCharacterAsync(tab);
        await FillPlaysAsync(tab);
        tab.Raise2(nameof(SheetTab.Sheet));
        AfterEditsChanged();
    }

    /// <summary>
    /// Take every feature away and build it again with the recipes this version ships.
    ///
    /// NOTHING ELSE IS TOUCHED — not their name, their outfit, the slots set by hand, or any
    /// other character — and what is ticked stays ticked. The part way results are SAID rather
    /// than swallowed: a remake that reports success while one of its pieces quietly did not get
    /// built is the exact fault this button exists to end.
    /// </summary>
    public async Task RemakeCharacter(CharacterTab tab)
    {
        if (_mod is null || _provider is not { } provider) return;

        if (!tab.RemakeReady)
        { Say("still reading the game — the remake reads it once for every recipe"); return; }

        var mod = _mod;
        var copy = tab.Copy;

        var done = await Task.Run(() =>
        {
            var ok = CharacterAuthor.Remake(provider, mod, copy, out var why, out var what,
                                            out var notes);
            return (Ok: ok, Why: why, What: what, Notes: notes);
        });

        if (!done.Ok)
        { Say($"{copy.Id}: {done.Why ?? "could not build them again"}"); return; }

        mod.Save();

        tab.SlotNote = $"Made again: {Listed(done.What)}. Build and install to put it in the game." +
                       (done.Notes.Count == 0
                           ? ""
                           : "  Not everything landed: " + string.Join("; ", done.Notes));

        foreach (var n in done.Notes) Say($"{copy.Id}: {n}");
        Say($"{copy.Id}: made again — {Listed(done.What)}");

        await FillCharacterAsync(tab);
        await FillPlaysAsync(tab);
        FillRemake(tab);
        tab.Raise2(nameof(SheetTab.Sheet));
        AfterEditsChanged();
    }

    /// <summary>"a, b and c" — for a sentence rather than a list.</summary>
    private static string Listed(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}

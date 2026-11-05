namespace UEToolkit.Core;

using CUE4Parse.FileProvider;

/// <summary>
/// A MISSION, TURNED INTO WORK THE BUILDER ALREADY KNOWS HOW TO DO.
///
/// Nothing here writes a package. A mission is a duplicated game asset with some properties
/// changed, which is exactly what a <see cref="ModCopy"/> is, and the copy path in
/// <see cref="ModBuild"/> is 500 lines of hard-won handling -- pristine reads so an installed mod
/// is not its own source, GUID swaps, name-map renames, import re-pointing, store entries,
/// registry entries and chunk emission. Writing a second path beside it would mean getting all of
/// that right again for no gain, and getting it subtly wrong is what actually happens.
///
/// So this TRANSLATES. Each mission becomes one ModCopy; each table it sits in becomes another.
/// The pieces map cleanly because a cooked package only has a few kinds of thing in it:
///
///     a TAG or a ROW NAME   -> RenameNames    an entry in the name map, substituted
///     an ASSET REFERENCE    -> RepointImports an import, which a rename cannot reach
///     an FText              -> Text           found by property name, spliced
///     a NUMBER or a FLAG    -> Numbers        overwritten in place, see PropertyAdd.SetInt
///
/// WHY A TAG IS A RENAME AND NOT A TAG EDIT. `EnemyFactionTag` is a single FGameplayTag, not a
/// container, so there is no list to add to or remove from -- the tag IS a name in the map, and
/// substituting that name is both the smallest change and the one least able to go wrong. It is
/// safe here because a mission names each of these tags exactly once; it would not be safe on a
/// package that used the same tag for two purposes.
/// </summary>
public static class MissionBuild
{
    /// <summary>The export class a mission's own properties live on.</summary>
    public const string MissionClass = "BrunoMissionData";

    /// <summary>The export class the UI half lives on -- name, descriptions, card picture.</summary>
    public const string FragmentClass = "BitReactorUIDataFragment";

    /// <summary>The weighted table's export class, and the array it keeps its rows in.</summary>
    public const string TableClass = "MissionWeightedTable";
    public const string RowsProperty = "WeightedMissions";

    /// <summary>
    /// What a mission asks the builder to do, and what could not be asked for.
    /// </summary>
    /// <param name="Copies">One per asset: the mission, then a copy of each table it sits in.</param>
    /// <param name="Notes">Things the author should know -- never silent.</param>
    /// <param name="Overrides">
    /// THE TABLES, which must NOT be copies. A ModCopy ships into the mod's own folder, so a copy
    /// of `SK_Table_Imperial` lands at `/Game/Game/Customizations/Mods/&lt;mod&gt;/SK_Table_Imperial`
    /// -- a brand new asset that nothing in the game references. The campaign arc names the table
    /// by its own path, so the edit has to arrive AT THAT PATH, which is what an override is.
    ///
    /// This was wrong for two builds. Both of them shipped a table nobody reads, and the second
    /// one looked like a success: the log said "takes row 0 of SK_Table_Imperial", the verifier
    /// passed 13 checks, and the mission could never have appeared.
    /// </param>
    public sealed record Plan(List<ModCopy> Copies, List<ModOverride> Overrides, List<string> Notes);

    /// <summary>
    /// Translate every mission in a project.
    ///
    /// <paramref name="donors"/> is what the mission was copied FROM, read from the game -- needed
    /// because a substitution needs both halves, and the OLD value only exists in the donor. A
    /// mission whose donor cannot be read is reported and skipped rather than built wrong: writing
    /// `br.Faction.Separatist` over nothing produces a mission that spawns nobody and looks fine.
    /// </summary>
    public static Plan Translate(ModProject mod, IFileProvider provider)
    {
        var copies = new List<ModCopy>();
        var overrides = new List<ModOverride>();
        var notes = new List<string>();
        if (mod.Missions.Count == 0) return new Plan(copies, overrides, notes);

        var byPackage = MissionIndex.All(provider)
            .ToDictionary(m => m.Package, StringComparer.OrdinalIgnoreCase);
        var tables = MissionIndex.Tables(provider);

        // READ ONCE, AND ONLY IF ASKED FOR. Walking every campaign arc costs a package load each,
        // and a project that places nothing directly should not pay for it.
        var placements = mod.Missions.Any(x => x.Placements.Count > 0)
            ? MissionIndex.Placements(provider)
            : [];

        foreach (var m in mod.Missions)
        {
            if (string.IsNullOrWhiteSpace(m.Id)) { notes.Add("a mission with no name was skipped"); continue; }
            if (string.IsNullOrWhiteSpace(m.From))
            { notes.Add($"{m.Id}: no mission to copy from, so it was skipped"); continue; }
            if (!byPackage.TryGetValue(m.From, out var donor))
            { notes.Add($"{m.Id}: could not read {Leaf(m.From)}, so it was skipped"); continue; }

            var copy = new ModCopy { Id = m.Id, Template = m.From, NewGuid = true };

            // ---- the words. Both descriptions separately: ModCopy.Description writes the same
            // text into both, which is right for a weapon and wrong for a mission that has a
            // one-line blurb and a full briefing.
            if (m.Display.Length > 0 && m.Display != donor.Display) copy.Display = m.Display;
            if (m.ShortDescription != donor.ShortDescription)
                copy.Text["ShortDescription"] = m.ShortDescription;
            if (m.LongDescription != donor.LongDescription)
                copy.Text["LongDescription"] = m.LongDescription;

            // ---- tags and row names: name-map substitutions, old -> new.
            Rename(copy, donor.Faction, m.Faction, notes, m.Id, "faction");
            Rename(copy, donor.Type, m.Type, notes, m.Id, "objective");
            Rename(copy, donor.RegionTag, m.RegionTag, notes, m.Id, "region tag");
            Rename(copy, donor.Planet, m.Planet, notes, m.Id, "planet");
            Rename(copy, donor.RegionAssetName, m.RegionAsset, notes, m.Id, "region asset");
            Rename(copy, donor.FactTag, m.FactTag, notes, m.Id, "fact");

            // The difficulty tag, and any other mission tag, the same way -- matched by POSITION in
            // the donor's list, because the tags are distinct names and a set has no other key.
            for (var i = 0; i < m.MissionTags.Count && i < donor.MissionTags.Count; i++)
                Rename(copy, donor.MissionTags[i], m.MissionTags[i], notes, m.Id, "mission tag");
            if (m.MissionTags.Count > donor.MissionTags.Count)
                notes.Add($"{m.Id}: it asks for {m.MissionTags.Count} mission tags and the mission " +
                          $"it copies has {donor.MissionTags.Count} -- the extra ones need a property " +
                          "to be grown and were not written");

            // ---- asset references: imports, which a rename cannot move.
            Repoint(copy, donor.Level, m.Level);
            Repoint(copy, donor.MapConfiguration, m.MapConfiguration);
            Repoint(copy, donor.RewardTable, m.RewardTable);
            Repoint(copy, donor.LocationTable, m.LocationTable);
            for (var i = 0; i < m.Rewards.Count && i < donor.Rewards.Count; i++)
                Repoint(copy, donor.Rewards[i], m.Rewards[i]);
            // AN EXTRA REWARD WAS BEING DROPPED IN SILENCE. Rewards are re-pointed one for one, so
            // a list longer than the donor's has nothing to point at -- and unlike the briefing
            // lines and the mission tags, this case had no note. An author adding a fifth reward
            // to a mission whose donor has four would have seen a clean build and four rewards.
            if (m.Rewards.Count > donor.Rewards.Count)
                notes.Add($"{m.Id}: it lists {m.Rewards.Count} rewards and the mission it copies " +
                          $"has {donor.Rewards.Count} -- the extra ones were not written. Copy a " +
                          "mission with more rewards to get more slots.");

            // THE MAP IS THE ONE THAT MATTERS, so it is said out loud rather than left in a diff.
            //
            // A BLANK IS NOT A CHANGE. Every other field treats an empty value as "leave the
            // donor's alone" -- Repoint returns early on one -- and this note did not, so a
            // mission whose level was simply not filled in reported "fought on  instead of
            // SK_Anaxes_045_Root", naming a map it was still using. Caught by building a
            // hand-written project rather than one the panel seeded.
            if (m.Level is { Length: > 0 } && !Same(donor.Level, m.Level))
                notes.Add($"{m.Id}: fought on {Leaf(m.Level)} instead of {Leaf(donor.Level)} -- " +
                          "the map's own objective actors and spawn points are what it will use");

            // ---- numbers and flags.
            //
            // ADDRESSED BY THE DONOR'S EXPORT NAME, not by its class. The builder's second-chance
            // writer walks exports by NAME -- a copy keeps the names its template had -- so a
            // mission copied from SK_Brentaal_030 still answers to `SK_Brentaal_030` however the
            // copy itself is called. Handing it the class name finds nothing and the build stops
            // with "no such property", which is a confusing way to be told the address was wrong.
            var on = donor.Leaf;
            Number(copy, on, "MinSquadSize", donor.MinSquad, m.MinSquad);
            Number(copy, on, "MaxSquadSize", donor.MaxSquad, m.MaxSquad);
            Number(copy, on, "LifeSpan", donor.LifeSpan, m.LifeSpan);
            Flag(copy, on, "bBlockActiveDuplicates", donor.BlockActiveDuplicates, m.BlockActiveDuplicates);
            Flag(copy, on, "bBlockCompletedDuplicates", donor.BlockCompletedDuplicates, m.BlockCompletedDuplicates);

            // THE TWO THAT LIVE ON SUB-OBJECTS, and are therefore not reachable this way yet. A
            // mission's reward sub-objects are named `CustomReward_IncrementFactValue_C_0` and
            // `CustomReward_RewardTable_C_1` -- generated names with a trailing index this reader
            // does not collect, so there is no address to hand over. Said rather than dropped: an
            // author who changes the increment and sees the donor's number in game would have no
            // way to know why.
            if (m.FactIncrement != donor.FactIncrement)
                notes.Add($"{m.Id}: the fact increment stayed at {donor.FactIncrement} -- it lives " +
                          "on a sub-object whose generated name is not read yet");
            if (m.RewardPreRoll != donor.RewardPreRoll)
                notes.Add($"{m.Id}: the reward preroll flag stayed as the donor's, for the same reason");

            // ---- THE BRIEFING LINES ARE NOT WRITTEN, and this is where that is admitted.
            //
            // They were, via `copy.Text["ObjectivesText[0]"]`, which does not work and was not
            // checked: `copy.Text` is keyed by PROPERTY NAME and looked up against the export's
            // schema, so an indexed key finds nothing. Worse, a text write that finds nothing
            // SKIPPED THE WHOLE ASSET -- the author's first real mission build ended with
            // "SKIPPED SK_Kaller_025_Mod: ObjectivesText[0]: no ObjectivesText[0] on any export"
            // and no mission at all. An invented key dressed up as a property.
            //
            // `ObjectivesText` is an ARRAY of FText. Replacing one element means splicing inside
            // the array and fixing the element's own length prefix, which none of the text editors
            // here can do -- they take a property's byte range, and this needs an element's. So
            // the lines stay the donor's and the author is told, rather than the mission being
            // thrown away over its subtitle.
            var wanted = m.Objectives.Where((t, i) => i >= donor.Objectives.Count ||
                                                      t != donor.Objectives[i]).ToList();
            if (wanted.Count > 0)
                notes.Add($"{m.Id}: the briefing line(s) stay as {Leaf(m.From)}'s " +
                          $"(\"{string.Join("\", \"", donor.Objectives)}\") -- writing one means " +
                          "splicing inside an FText array, which is not built yet. Everything else " +
                          "about the mission is written normally.");

            // ---- EDITING THE GAME'S OWN MISSION, rather than shipping one beside it.
            //
            // Everything above collected the author's changes into a ModCopy, and a ModCopy and a
            // ModOverride carry the same four kinds of edit -- a display name, name-map renames,
            // import re-points and numbers. So the collection is shared and only the DESTINATION
            // differs: a copy lands at a new id in the mod's folder, an override lands on the
            // game's own package. Nothing else about the translation changes.
            //
            // NO TABLE WORK AT ALL. The mission the author edited already sits in whatever rows
            // point at it, so there is nothing to draw and nothing to take over -- which is the
            // whole reason this mode is the better test.
            if (m.InPlace)
            {
                var edit = new ModOverride { Target = m.From };
                if (copy.Display is { Length: > 0 }) edit.DisplayName = copy.Display;

                // ONE DESCRIPTION, NOT TWO. ModOverride carries a single Description and writes it
                // into both ShortDescription and LongDescription; ModCopy.Text can address them
                // separately. The long one is preferred because it is the one a mission page
                // shows, and the author is told rather than left to notice in game.
                var longer = copy.Text.TryGetValue("LongDescription", out var lg) ? lg : null;
                var shorter = copy.Text.TryGetValue("ShortDescription", out var sh) ? sh : null;
                if (longer is { Length: > 0 } || shorter is { Length: > 0 })
                {
                    edit.Description = longer is { Length: > 0 } ? longer : shorter!;
                    if (longer is { Length: > 0 } && shorter is { Length: > 0 } && longer != shorter)
                        notes.Add($"{m.Id}: editing in place writes ONE description into both the " +
                                  "blurb and the briefing -- they will read the same. Duplicating " +
                                  "the mission instead lets them differ.");
                }

                foreach (var (from, to) in copy.RenameNames) edit.RenameNames[from] = to;
                foreach (var (from, to) in copy.RepointImports) edit.RepointImports[from] = to;
                edit.Numbers.AddRange(copy.Numbers);

                overrides.Add(edit);
                notes.Add($"{m.Id}: edits the GAME's {Leaf(m.From)} in place -- while this mod is " +
                          "installed, that mission is yours rather than the one the game ships. " +
                          "It keeps every table row that already points at it, so nothing needs " +
                          "drawing.");

                if (m.Tables.Count > 0)
                    notes.Add($"{m.Id}: its {m.Tables.Count} table placement(s) were ignored -- a " +
                              "mission edited in place is already in the game's tables.");
                if (m.Placements.Count > 0)
                    notes.Add($"{m.Id}: its {m.Placements.Count} campaign-turn placement(s) were " +
                              "ignored -- a mission edited in place already sits wherever the game " +
                              "put it, so there is no slot to take over.");
                continue;
            }

            copies.Add(copy);

            // ---- WHERE IT SHOWS UP.
            //
            // "IN NO TABLE" IS ONLY WORTH SAYING IF NOTHING ELSE DELIVERS IT. A mission that takes
            // a campaign turn slot needs no table at all -- that is the whole point of the route --
            // and a note reading "nothing will ever offer it" beside one that says it arrives on a
            // named turn is the kind of contradiction an author reasonably reads as a failure.
            if (m.Tables.Count == 0 && m.Placements.Count == 0 && m.ArcAdds.Count == 0)
                notes.Add($"{m.Id}: it is in no table and takes no campaign slot, so nothing " +
                          "will ever offer it");

            // COUNTED, AND CHECKED AFTERWARDS. Every failure of this feature so far has been the
            // same one -- the mission shipped and no table pointed at it -- and every time the
            // build looked successful, because the missing half was a note among six. The count
            // below is compared against what actually got written, and a mission that ends with
            // nothing pointing at it says so in the FIRST line of its report.
            var pointedAt = 0;

            foreach (var row in m.Tables)
            {
                if (string.IsNullOrWhiteSpace(row.Table))
                { notes.Add($"{m.Id}: a placement with no table was skipped"); continue; }
                var table = tables.FirstOrDefault(t =>
                    string.Equals(t.Package, row.Table, StringComparison.OrdinalIgnoreCase));
                if (table is null)
                { notes.Add($"{m.Id}: could not read {Leaf(row.Table)}"); continue; }

                // ---- ADDING A ROW, which displaces nothing. The author's own words for why this
                // is the one that was wanted: "i was hoping it'd just appear without making any
                // existing missions disappear and appear long side it".
                //
                // This branch used to write NOTHING while reporting that adding was "untested",
                // which cost three build-install-play round trips -- the sentence described a
                // gamble where there was an omission. It is now the DEFAULT, because taking over a
                // row costs the player a shipped mission and no table has a spare row to give:
                // bEmptyOption is false on all 65 rows across the five tables.
                if (row.ReplaceRow < 0)
                {
                    var grow = overrides.FirstOrDefault(o =>
                        string.Equals(o.Target, table.Package, StringComparison.OrdinalIgnoreCase));
                    if (grow is null)
                    {
                        grow = new ModOverride { Target = table.Package };
                        overrides.Add(grow);
                    }
                    grow.AddMissions.Add(new ModOverride.MissionAdd
                    {
                        Mission = m.Id,
                        Weight = (float) row.Weight,
                    });
                    pointedAt++;

                    // THE ODDS, said in the same terms the takeover branch uses. A weight of 1
                    // against a table's existing 1s is one candidate among count+1; the author
                    // cannot work that out from a number in a box.
                    var per = row.Weight / (table.Rows.Sum(r => r.Weight) + row.Weight);
                    notes.Add($"{m.Id}: added as a NEW row of {table.Leaf} at weight " +
                              $"{row.Weight:0.##} -- nothing was displaced, every mission the table " +
                              $"already had is still in it, and this one is about {per:P0} of each " +
                              "pull.");

                    if (row.RequireTags.Count > 0 || row.IgnoreTags.Count > 0 || row.FactValues.Count > 0)
                        notes.Add($"{m.Id}: the fact gates on its new row in {table.Leaf} were not " +
                                  "written -- the row is added ungated, so it can be drawn as soon " +
                                  "as the table is pulled.");
                    continue;
                }

                if (row.ReplaceRow >= table.Rows.Count)
                {
                    notes.Add($"{m.Id}: {table.Leaf} has {table.Rows.Count} rows and row " +
                              $"{row.ReplaceRow} was asked for");
                    continue;
                }

                var taken = table.Rows[row.ReplaceRow];
                var mine = PackageFor(m.Id, mod);

                // ONE COPY OF THE TABLE PER MISSION IS WRONG when two missions take rows in the
                // same table -- the second copy would be made from the game's again and lose the
                // first. Folded instead: the copy is reused and a second re-point added to it.
                // ONE OVERRIDE PER TABLE, shared when two missions take rows in the same one --
                // a second override on the same target would be made from the game's asset again
                // and lose the first one's re-point.
                var edit = overrides.FirstOrDefault(o =>
                    string.Equals(o.Target, table.Package, StringComparison.OrdinalIgnoreCase));
                if (edit is null)
                {
                    edit = new ModOverride { Target = table.Package };
                    overrides.Add(edit);
                }

                if (Same(taken.MissionPackage, mine))
                    notes.Add($"{m.Id}: row {row.ReplaceRow} of {table.Leaf} already points at it");
                else
                {
                    // BOTH SIDES AS PACKAGE NAMES, for the reason on Repoint above. The row's
                    // Mission reads back as an object path and `mine` was built as one too, so
                    // this entry matched nothing and the whole override was dropped as unchanged.
                    if (PackageOnly(taken.MissionPackage) is { } fromPkg &&
                        PackageOnly(mine) is { } toPkg)
                        edit.RepointImports[fromPkg] = toPkg;
                    pointedAt++;
                    notes.Add($"{m.Id}: takes row {row.ReplaceRow} of {table.Leaf} from " +
                              $"{Leaf(taken.MissionPackage)} -- that mission will no longer be offered");
                }

                // ---- THE WEIGHT IS NOT WRITTEN, and this is the second time I invented a key.
                //
                // It was `Numbers.Add(new NumberChange("WeightedMissions[0]", "Weight", ...))`,
                // and `Row` there is an EXPORT NAME or a DataTable row name -- never an array
                // index. It failed exactly the way `ObjectivesText[0]` failed an hour earlier,
                // with the same shape of message, and because a failed number SKIPS the asset it
                // took the whole table copy with it: "skipped: SK_Table_Imperial:
                // WeightedMissions[0].Weight: no export matching 'WeightedMissions[0]'". The
                // mission shipped and nothing pointed at it, for the second build running.
                //
                // A row's Weight is a float inside a struct inside an array. Reaching it means
                // walking the array to the element and reading its unversioned property header --
                // the same machinery an added row would need. Until that exists the row keeps the
                // weight it had, and the author is told rather than the table being thrown away.
                if (Math.Abs(row.Weight - taken.Weight) > 0.0001)
                    notes.Add($"{m.Id}: the weight stays at {taken.Weight:0.##} -- writing one " +
                              "means reaching inside a struct in a cooked array, which is not " +
                              $"built yet. To make it near-certain, take over SEVERAL of " +
                              $"{table.Leaf}'s {table.Rows.Count} rows: every row you take points " +
                              "at your mission, so whichever the game draws is yours.");

                if (row.RequireTags.Count > 0 || row.IgnoreTags.Count > 0 || row.FactValues.Count > 0)
                    notes.Add($"{m.Id}: the fact gates on its row in {table.Leaf} need the row's " +
                              "requirement structs to be grown, which is not built yet -- the row " +
                              "is taken over without them");
            }

            // ---- PLACED DIRECTLY ON A CAMPAIGN TURN, which is the route with no lottery in it.
            //
            // Structurally identical to taking over a table row, and deliberately so: find or
            // reuse one override per target, add a re-point, count it. The only difference is the
            // target -- a campaign arc rather than a weighted table -- and that ONE choice can
            // produce TWO overrides, because a "Start" arc is a prefix of the plain arc of the
            // same number and both declare the same turn. Taking A4Start's t3 without taking A4's
            // would leave the displaced mission still arriving from the other arc.
            foreach (var takeOver in m.Placements)
            {
                if (string.IsNullOrWhiteSpace(takeOver)) continue;

                var slots = placements.Where(p =>
                    Same(p.MissionPackage, takeOver)).ToList();
                if (slots.Count == 0)
                {
                    notes.Add($"{m.Id}: no campaign turn places {Leaf(takeOver)}, so that slot " +
                              "could not be taken over. Pick one the arcs actually place.");
                    continue;
                }

                var mine = PackageFor(m.Id, mod);
                if (Same(takeOver, mine))
                { notes.Add($"{m.Id}: it already holds that slot"); continue; }

                foreach (var slot in slots)
                {
                    var edit = overrides.FirstOrDefault(o =>
                        string.Equals(o.Target, slot.ArcPackage, StringComparison.OrdinalIgnoreCase));
                    if (edit is null)
                    {
                        edit = new ModOverride { Target = slot.ArcPackage };
                        overrides.Add(edit);
                    }
                    // BOTH SIDES AS PACKAGE NAMES. MissionIndex.Placements already strips the
                    // `.Asset` half off what it reads; `mine` is built as an object path, so it
                    // still has to be reduced or the entry matches nothing and the whole override
                    // is dropped as unchanged -- the exact bug the table branch above carries a
                    // note about.
                    if (PackageOnly(takeOver) is { } fromPkg && PackageOnly(mine) is { } toPkg)
                        edit.RepointImports[fromPkg] = toPkg;
                }

                pointedAt++;
                var where = string.Join(", ", slots.Select(s => $"{s.Arc} turn {s.Turn}"));
                notes.Add($"{m.Id}: takes the campaign slot of {Leaf(takeOver)} ({where}) -- it " +
                          "arrives on that turn with no table, no weight and no roll, and that " +
                          "mission is no longer offered while this mod is installed." +
                          (slots.Count > 1
                              ? $" Both arcs were re-pointed; a 'Start' arc is a prefix of the " +
                                "plain one, so taking only one would have left the other delivering it."
                              : ""));
            }

            // ---- ADDED TO A CAMPAIGN TURN, which is guaranteed AND displaces nothing.
            //
            // The only route with neither a roll nor a cost. The turn keeps every mission it
            // already placed and gains this one, so it arrives on that turn full stop -- where a
            // table row is a weighted candidate and taking a slot re-points a shipped mission away.
            foreach (var entry in m.ArcAdds)
            {
                if (ModMission.SplitArcAdd(entry) is not { } spot)
                { notes.Add($"{m.Id}: \"{entry}\" is not an arc and turn, so it was skipped"); continue; }

                var arcEdit = overrides.FirstOrDefault(o =>
                    string.Equals(o.Target, spot.Arc, StringComparison.OrdinalIgnoreCase));
                if (arcEdit is null)
                {
                    arcEdit = new ModOverride { Target = spot.Arc };
                    overrides.Add(arcEdit);
                }
                arcEdit.AddArcMissions.Add(new ModOverride.ArcMissionAdd
                {
                    Mission = m.Id,
                    Turn = spot.Turn,
                });
                pointedAt++;
                notes.Add($"{m.Id}: added to {Leaf(spot.Arc)} turn {spot.Turn} -- it arrives on " +
                          "that turn with no table, no weight and no roll, and nothing the turn " +
                          "already placed was displaced.");
            }

            // THE ONE THING WORTH REFUSING OVER, said first and said plainly.
            if (pointedAt == 0)
                notes.Insert(0, $"{m.Id} WILL NEVER APPEAR: nothing points at it. The mission is " +
                                "built and shipped, but nothing was changed to deliver it. Open " +
                                "the mission's \"When it appears\" tab and do ONE of: take over a " +
                                "campaign turn slot, which delivers it on that turn with no roll; " +
                                "or pick a table, choose \"Take over a row\" and press \"take " +
                                "every row\", which enters it in a lottery.");
        }

        return new Plan(copies, overrides, notes);
    }

    // ------------------------------------------------------------------------------ plumbing

    private static void Rename(ModCopy copy, string? was, string now,
                               List<string> notes, string id, string what)
    {
        if (string.IsNullOrWhiteSpace(now) || Same(was, now)) return;
        if (string.IsNullOrWhiteSpace(was))
        {
            // NOTHING TO SUBSTITUTE. A name map holds what the package uses; a mission whose donor
            // names no fact tag has no entry to rewrite, and adding one is a different operation.
            notes.Add($"{id}: its {what} could not be set to {now} -- the mission it copies has " +
                      "none, and there is no name to substitute");
            return;
        }
        copy.RenameNames[was!] = now;
    }

    /// <summary>
    /// A reference re-pointed, BY PACKAGE NAME on both sides.
    ///
    /// This is the bug that cost four build-install-play round trips. A property reads back as
    /// `BrunoMissionData'/Game/.../SK_Brentaal_030.SK_Brentaal_030'` -- an OBJECT path, package
    /// and asset -- and `RepointImports` is keyed by PACKAGE NAME. Handing it the object path
    /// looks up a package called "SK_Brentaal_030.SK_Brentaal_030", finds nothing, and reports
    /// "it does not import X, so that redirect changed nothing". The override then contains no
    /// change at all, so the build DROPS it as "a package replaced by a copy of itself" -- and
    /// the mission ships with nothing in the game pointing at it.
    ///
    /// Every symptom said so and I read none of them: the doubled asset name in the message
    /// ("Rewards_Missions_Shared.Rewards_Missions_Shared"), the "changed nothing" line, and the
    /// absent table in `--modfiles`. It is the third time today an object path has been used
    /// where a package name belongs -- see MissionIndex.PackageOf, written for the same fault in
    /// the schedule reader an hour earlier.
    /// </summary>
    private static void Repoint(ModCopy copy, string? was, string now)
    {
        var from = PackageOnly(was);
        var to = PackageOnly(now);
        if (from is null || to is null || Same(from, to)) return;
        copy.RepointImports[from] = to;
    }

    /// <summary>`/Game/X/A.A` -> `/Game/X/A`. See <see cref="Repoint"/> for why this matters.</summary>
    private static string? PackageOnly(string? objectPath)
    {
        if (objectPath is not { Length: > 0 } || !objectPath.Contains('/')) return null;
        var cut = objectPath.LastIndexOf('.');
        var slash = objectPath.LastIndexOf('/');
        return cut > slash ? objectPath[..cut] : objectPath;
    }

    private static void Number(ModCopy copy, string export, string property, double was, double now)
    {
        if (Math.Abs(was - now) < 0.0001) return;
        copy.Numbers.Add(new ModOverride.NumberChange(export, property, was, now));
    }

    private static void Flag(ModCopy copy, string export, string property, bool was, bool now)
    {
        if (was == now) return;
        // A FLAG IS A NUMBER HERE, 0 or 1, so it travels the same list. PropertyAdd.SetBool is what
        // reads it back out at the other end, and it refuses a flag the donor never wrote -- which
        // is the case this cannot serve and says so rather than appearing to work.
        copy.Numbers.Add(new ModOverride.NumberChange(export, property, was ? 1 : 0, now ? 1 : 0));
    }

    /// <summary>Where this mod's own copy of an asset stands, so a table can point at it.</summary>
    private static string PackageFor(string id, ModProject mod)
    {
        var folder = mod.Folder is { Length: > 0 } f ? f.Trim('/') : "Customizations/Mods";
        // PACKAGE NAME, not `pkg.asset`. Repoint normalises anyway, but building it wrong here is
        // how the wrong shape spread in the first place.
        return $"/Game/Game/{folder}/{id}";
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Leaf(string? p) =>
        p is null ? "-" : p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p;
}

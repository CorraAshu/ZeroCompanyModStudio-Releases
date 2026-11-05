using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// AUTHORING A MISSION.
///
/// A mission is DATA — where it is fought, who you fight, what you do, how big the squad is, what
/// it pays and what it remembers. The one part that is NOT data is the MAP: objective actors,
/// spawner placements and reinforcement pins live in the level's own umap, and writing one is
/// untested. But POINTING a mission at a shipped level is ordinary — the game runs 125 missions
/// on 74 maps, and SK_Kaller_030_Root alone carries a Separatist Capture, an Imperial Rescue and
/// an Imperial Sabotage. So the map is a thing you choose, not a thing you make.
///
/// BUILT BY DUPLICATING A SHIPPED ONE, never synthesised. A BrunoMissionData is a cooked asset
/// with an import table, three sub-objects and a map-configuration Blueprint; writing one from
/// nothing means getting all of that right for no gain when the game ships 125 of them.
///
/// AND EVERY TAG, ROW NAME AND ASSET PATH IS A CHOOSER over what the game actually has. The old
/// studio shipped seven of these as empty text boxes; the author opened it and said he did not
/// know what was supposed to go in them, which is the only possible response to a blank box
/// labelled "Image bank tag".
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>The missions this project ships.</summary>
    public IReadOnlyList<ModMission> MissionsInMod => _mod?.Missions ?? [];

    /// <summary>
    /// Every mission the game ships, read once and kept.
    ///
    /// The whole editor's choosers are built out of these — the planets, the maps, the reward
    /// tables, the facts. Reading them twice would be reading 125 packages twice.
    /// </summary>
    public async Task<List<MissionIndex.Mission>> GameMissionsAsync()
    {
        if (_gameMissions is not null) return _gameMissions;
        if (_provider is null) return [];

        var provider = _provider;
        _gameMissions = await Task.Run(() =>
        {
            try { return MissionIndex.All(provider); }
            catch (Exception) { return new List<MissionIndex.Mission>(); }
        });

        Say($"the game's missions: {_gameMissions.Count}");
        return _gameMissions;
    }

    private List<MissionIndex.Mission>? _gameMissions;
    private List<MissionIndex.Faction>? _gameFactions;

    private async Task<List<MissionIndex.Faction>> FactionsAsync()
    {
        if (_gameFactions is not null) return _gameFactions;
        if (_provider is null) return [];

        var provider = _provider;
        _gameFactions = await Task.Run(() =>
        {
            try { return MissionIndex.Factions(provider); }
            catch (Exception) { return new List<MissionIndex.Faction>(); }
        });
        return _gameFactions;
    }

    /// <summary>
    /// The objective kinds. LIKELY BUT NOT PROVEN that this drives the mission rather than merely
    /// labelling it: Kaller 030, 040 and 045 all run on ONE map and are a Capture, a Rescue and a
    /// Sabotage, which is hard to explain if the level decides. Said as "likely" in the panel.
    /// </summary>
    private static readonly string[] ObjectiveKinds =
    [
        "br.Mission.Skirmish.Battle", "br.Mission.Skirmish.Capture", "br.Mission.Skirmish.Eliminate",
        "br.Mission.Skirmish.Escape", "br.Mission.Skirmish.Rescue", "br.Mission.Skirmish.Retrieve",
        "br.Mission.Skirmish.Sabotage", "br.Mission.Skirmish.Unknown",
    ];

    private static readonly string[] DifficultyTags =
    [
        "BitReactor.Design.MissionDifficulty.Easy",
        "BitReactor.Design.MissionDifficulty.Medium",
        "BitReactor.Design.MissionDifficulty.Hard",
    ];

    // ------------------------------------------------------------------ starting one

    /// <summary>
    /// Duplicate a shipped mission into this project.
    ///
    /// EVERY FIELD IS SEEDED FROM THE MISSION IT WAS COPIED FROM, so a build straight after this
    /// produces the donor's mission under a new name — which is the thing worth having first,
    /// because it separates "can a mod ship a mission at all" from "did I fill it in right".
    /// </summary>
    public async Task<bool> DuplicateMission(MissionIndex.Mission src, bool inPlace)
    {
        if (_mod is null) { Say("open a mod first — a mission has to go somewhere"); return false; }

        // AN IN-PLACE EDIT KEEPS THE GAME'S OWN NAME, because that is the asset it targets. A
        // "_Mod" suffix would name a package nothing points at.
        var id = src.Leaf;
        if (!inPlace)
        {
            // NOT ENDING IN _digits. UE splits a trailing _N off an FName into a NUMBER and our
            // writer does not, so `SK_Mod_Lothal_2` resolves to nothing at all — an afternoon,
            // once. Roman numerals for the same reason.
            id = $"{src.Leaf}_Mod";
            var n = 2;
            while (_mod.Missions.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                id = $"{src.Leaf}_Mod{Roman(n++)}";
        }
        else if (_mod.Missions.Any(x => x.InPlace &&
                     x.From.Equals(src.Package, StringComparison.OrdinalIgnoreCase)))
        { Say($"{src.Leaf}: this mod already edits it in place"); return false; }

        var made = new ModMission
        {
            Id = id,
            From = src.Package,
            InPlace = inPlace,
            // AN IN-PLACE EDIT STARTS AS WHAT THE GAME SHIPS, with no "(Mod)" on it: the point is
            // to change one thing deliberately and see it, and a name that differs by default
            // makes the first build's result ambiguous — did the edit land, or is that the suffix?
            Display = inPlace ? src.Display
                    : src.Display.Length > 0 ? src.Display + " (Mod)" : id,
            ShortDescription = src.ShortDescription,
            LongDescription = src.LongDescription,
            Thumbnail = src.Thumbnail ?? "",
            ImageBankTag = src.ImageBankTag ?? "",
            Planet = src.Planet ?? "",
            LocationTable = src.LocationTable ?? "",
            RegionTag = src.RegionTag ?? "",
            RegionAsset = src.RegionAssetName ?? "",
            Level = src.Level,
            Faction = src.Faction,
            Type = src.Type,
            MinSquad = src.MinSquad,
            MaxSquad = src.MaxSquad,
            LifeSpan = src.LifeSpan,
            BlockActiveDuplicates = src.BlockActiveDuplicates,
            BlockCompletedDuplicates = src.BlockCompletedDuplicates,
            MapConfiguration = src.MapConfiguration ?? "",
            RewardTable = src.RewardTable ?? "",
            RewardPreRoll = src.RewardPreRoll,
            FactTag = src.FactTag ?? "",
            FactIncrement = src.FactIncrement,
        };
        made.Objectives.AddRange(src.Objectives);
        made.MissionTags.AddRange(src.MissionTags);
        made.Rewards.AddRange(src.Rewards);

        _mod.Missions.Add(made);
        _mod.Save();
        AfterEditsChanged();

        Say(inPlace
            ? $"{src.Leaf}: this mod now edits the game's own copy in place — it is already on " +
              "the galaxy map, so a change here needs no roll to be seen"
            : $"duplicated {src.Leaf} as {id} — a mission of your own. Nothing offers it yet.");

        await OpenMissionEditor(src, made);
        return true;

        static string Roman(int i) => i switch
        {
            2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
            9 => "IX", 10 => "X", _ => new string('I', Math.Max(1, i)),
        };
    }

    /// <summary>Open the editor on a mission this project already holds.</summary>
    public async Task EditMission(ModMission mine)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        if (Tabs.OfType<MissionTab>().FirstOrDefault(
                t => ReferenceEquals(t.Mine, mine)) is { } open)
        { Tab = open; return; }

        var all = await GameMissionsAsync();
        var src = all.FirstOrDefault(
            m => m.Package.Equals(mine.From, StringComparison.OrdinalIgnoreCase));
        if (src is null)
        {
            Say($"{mine.Id}: the mission this was copied from ({Leaf(mine.From)}) is not in the " +
                "game any more");
            return;
        }

        await OpenMissionEditor(src, mine);
    }

    /// <summary>The panel, on a mission. One route, shared by Duplicate and by opening one.</summary>
    private async Task OpenMissionEditor(MissionIndex.Mission src, ModMission mine)
    {
        var tab = new MissionTab(src, mine);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        // WHAT A MISSION HAS. No mesh, no tint, no slot, no icon of the kind a part wears — its
        // picture is a texture named by path, which lives on the briefing card with the words.
        tab.ShowOnly(tab.SecIdentity, tab.SecBriefing, tab.SecPlace, tab.SecFight, tab.SecPays,
                     tab.SecWhen, tab.SecFiles);

        tab.EditId = mine.Id;
        tab.EditsInPlace = mine.InPlace;
        tab.InPlaceNote = mine.InPlace
            ? $"This mod ships {Leaf(mine.From)} at the game's own path, so the galaxy map draws " +
              "YOUR version wherever it would have drawn theirs. Nothing new is added and no " +
              "table is touched."
            : $"A mission of your own at {mine.Id}. The galaxy map draws from weighted tables and " +
              "turn slots, so until something names it, nothing will offer it.";

        await FillMissionAsync(tab, mine);
        await FillWhenAsync(tab, mine);

        tab.BeginEdit(mine.Display, mine.ShortDescription, "", "");
        NoteMod(tab, null);
    }

    /// <summary>
    /// Put the mission on the panel, and build every chooser out of what the game has.
    ///
    /// EVERY VALUE IS READ OFF THE PROJECT, and every LIST off the game. The recurring fault in
    /// this editor has been a panel showing a state nobody read back; the cheapest guard is that
    /// there is no other source for anything drawn here.
    /// </summary>
    private async Task FillMissionAsync(MissionTab tab, ModMission mine)
    {
        tab.BeginFill();
        try { await FillMissionCore(tab, mine); }
        finally { tab.EndFill(); }
    }

    private async Task FillMissionCore(MissionTab tab, ModMission mine)
    {
        tab.SecBriefing.Present = true;
        tab.SecPlace.Present = true;
        tab.SecFight.Present = true;
        tab.SecPays.Present = true;
        tab.SecFiles.Present = true;

        tab.EditBriefing = mine.LongDescription;
        tab.EditThumbnail = mine.Thumbnail;
        tab.EditBankTag = mine.ImageBankTag;
        tab.EditRegionTag = mine.RegionTag;
        tab.EditRegionAsset = mine.RegionAsset;
        tab.EditMapConfig = mine.MapConfiguration;
        tab.LocationTableIs = mine.LocationTable;
        tab.EditMinSquad = mine.MinSquad.ToString();
        tab.EditMaxSquad = mine.MaxSquad.ToString();
        tab.EditLifeSpan = mine.LifeSpan.ToString();
        tab.EditBlockActive = mine.BlockActiveDuplicates;
        tab.EditBlockDone = mine.BlockCompletedDuplicates;
        tab.EditRewardTable = mine.RewardTable;
        tab.EditPreRoll = mine.RewardPreRoll;
        tab.EditFactTag = mine.FactTag;
        tab.EditFactIncrement = mine.FactIncrement.ToString();

        tab.SetMissionLists(mine.Objectives, mine.Rewards,
                            mine.MissionTags.Where(t => !t.Contains(".MissionDifficulty.")));

        var all = await GameMissionsAsync();
        var factions = await FactionsAsync();

        // THE PLANETS THE GAME'S OWN MISSIONS USE. A row name that is not in a location table is
        // a mission on no planet, so the list is what the tables actually hold.
        Fill(tab.PlanetChoices, all
            .Where(m => m.Planet is { Length: > 0 })
            .DistinctBy(m => m.Planet!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m.Planet, StringComparer.OrdinalIgnoreCase)
            .Select(m => new SheetTab.Choice(m.Planet!, m.Planet!, Leaf(m.LocationTable ?? ""))));

        // THE MAPS, each named with what already runs on it — because picking Brentaal's map
        // means inheriting Brentaal's objective actors, and that is the single most surprising
        // thing about this editor.
        Fill(tab.LevelChoices, all
            .Where(m => m.Level.Length > 0)
            .DistinctBy(m => m.Level, StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m.Where, StringComparer.OrdinalIgnoreCase)
            .Select(m => new SheetTab.Choice(m.Level, Leaf(m.Level),
                                             $"{m.Where}, {m.Objective}")));

        // ONLY THE FACTIONS THAT CAN FIGHT. Five of the twelve tags have a FactionData asset and
        // that asset owns the spawn sets a level draws from; a mission naming one of the other
        // seven spawns nobody, silently, with a clean build.
        Fill(tab.FactionChoices, factions
            .Where(f => f.CanFight)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new SheetTab.Choice(f.Tag, f.Name,
                $"{f.SpawnSets} roster{(f.SpawnSets == 1 ? "" : "s")}")));

        Fill(tab.TypeChoices, ObjectiveKinds.Select(t => new SheetTab.Choice(t, Tail(t))));
        Fill(tab.DifficultyChoices, DifficultyTags.Select(t => new SheetTab.Choice(t, Tail(t))));

        tab.PickedPlanet = tab.PlanetChoices.FirstOrDefault(
            c => c.Value.Equals(mine.Planet, StringComparison.OrdinalIgnoreCase));
        tab.PickedLevel = tab.LevelChoices.FirstOrDefault(
            c => c.Value.Equals(mine.Level, StringComparison.OrdinalIgnoreCase));
        tab.PickedFaction = tab.FactionChoices.FirstOrDefault(
            c => c.Value.Equals(mine.Faction, StringComparison.OrdinalIgnoreCase));
        tab.PickedType = tab.TypeChoices.FirstOrDefault(
            c => c.Value.Equals(mine.Type, StringComparison.OrdinalIgnoreCase));
        tab.PickedDifficulty = tab.DifficultyChoices.FirstOrDefault(
            c => mine.MissionTags.Any(t => t.Equals(c.Value, StringComparison.OrdinalIgnoreCase)));

        RecheckMission(tab, mine, all, factions);

        static void Fill(System.Collections.ObjectModel.ObservableCollection<SheetTab.Choice> into,
                         IEnumerable<SheetTab.Choice> from)
        {
            into.Clear();
            foreach (var c in from) into.Add(c);
        }

        static string Tail(string tag) =>
            tag.Contains('.') ? tag[(tag.LastIndexOf('.') + 1)..] : tag;
    }

    /// <summary>
    /// THE THREE THINGS THAT ARE TRUE AND SURPRISING, recomputed after every change.
    ///
    /// None of them is an error and none is refused — each is a fact about the game that an
    /// author cannot see from the fields alone, and every one of them costs an evening to
    /// discover from a build.
    /// </summary>
    private static void RecheckMission(MissionTab tab, ModMission mine,
                                       List<MissionIndex.Mission> all,
                                       List<MissionIndex.Faction> factions)
    {
        // WHERE THE GAME ITSELF FILES THIS PLANET. Both region fields follow the planet, and one
        // that does not match puts the card in the wrong corner of the galaxy map.
        tab.PlanetNote = "";
        if (mine.Planet.Length > 0 &&
            all.FirstOrDefault(m => string.Equals(m.Planet, mine.Planet,
                                                  StringComparison.OrdinalIgnoreCase)) is { } onPlanet &&
            (!string.Equals(onPlanet.RegionTag ?? "", mine.RegionTag, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(onPlanet.RegionAssetName ?? "", mine.RegionAsset, StringComparison.OrdinalIgnoreCase)))
            tab.PlanetNote =
                $"the game files {mine.Planet} under {onPlanet.RegionTag} / {onPlanet.RegionAssetName}";

        // WHOSE MAP IT IS. Normal rather than risky — the game puts several missions on one map
        // routinely — but it is the thing nobody expects.
        tab.LevelNote = "";
        if (mine.Level.Length > 0 &&
            all.FirstOrDefault(m => string.Equals(m.Level, mine.Level,
                                                  StringComparison.OrdinalIgnoreCase)) is { } owner &&
            !string.Equals(owner.Package, mine.From, StringComparison.OrdinalIgnoreCase))
            tab.LevelNote =
                $"this map already runs “{owner.Display}” — the game puts several missions on one " +
                "map routinely, so this is the normal case rather than a risk";

        // A FACTION WITH NO ROSTER PUTS NOBODY ON THE MAP.
        tab.FactionWarn = "";
        if (mine.Faction.Length > 0 &&
            factions.FirstOrDefault(f => string.Equals(f.Tag, mine.Faction,
                                                       StringComparison.OrdinalIgnoreCase))
                is null or { CanFight: false })
            tab.FactionWarn =
                $"{mine.Faction} has no spawn sets — the mission would put no enemies on the map";
    }
}

public sealed partial class StudioVm
{
    // ------------------------------------------------------------------ writing a mission

    /// <summary>
    /// Write the typed fields into the mission.
    ///
    /// THE CHOOSERS ARE NOT HERE. A planet, a map, a faction, an objective and a difficulty are
    /// written the moment they are chosen — the choice IS the edit and there is nothing a Save
    /// could add to it. What Save takes is the text and the numbers, which somebody is in the
    /// middle of typing until they are not.
    /// </summary>
    public bool SaveMissionEdit(MissionTab tab)
    {
        if (_mod is null || tab.Mine is not { } mine) return false;

        var wrote = new List<string>();

        var name = tab.EditName.Trim();
        if (name != mine.Display) { mine.Display = name; wrote.Add($"called \u201c{name}\u201d"); }

        var said = tab.EditDescription.Trim();
        if (said != mine.ShortDescription) { mine.ShortDescription = said; wrote.Add("its summary"); }

        var brief = tab.EditBriefing.Trim();
        if (brief != mine.LongDescription) { mine.LongDescription = brief; wrote.Add("its briefing"); }

        Put(tab.EditThumbnail, mine.Thumbnail, v => mine.Thumbnail = v, "its picture");
        Put(tab.EditBankTag, mine.ImageBankTag, v => mine.ImageBankTag = v, "its bank tag");
        Put(tab.EditRegionTag, mine.RegionTag, v => mine.RegionTag = v, "its region tag");
        Put(tab.EditRegionAsset, mine.RegionAsset, v => mine.RegionAsset = v, "its region");
        Put(tab.EditMapConfig, mine.MapConfiguration, v => mine.MapConfiguration = v,
            "its map configuration");
        Put(tab.EditRewardTable, mine.RewardTable, v => mine.RewardTable = v, "its reward table");
        Put(tab.EditFactTag, mine.FactTag, v => mine.FactTag = v, "the fact it raises");

        // CLAMPED THE WAY THE GAME'S OWN VALUES ARE. A squad of twelve is not a squad the game
        // can seat, and a lifespan of zero is a mission that expires before it is drawn.
        Num(tab.EditMinSquad, mine.MinSquad, 0, 8, v => mine.MinSquad = v, "the smallest squad");
        Num(tab.EditMaxSquad, mine.MaxSquad, 0, 8, v => mine.MaxSquad = v, "the biggest squad");
        Num(tab.EditLifeSpan, mine.LifeSpan, 1, 99, v => mine.LifeSpan = v, "how long it stays");
        Num(tab.EditFactIncrement, mine.FactIncrement, -99, 99, v => mine.FactIncrement = v,
            "what it adds to that fact");

        if (tab.EditBlockActive != mine.BlockActiveDuplicates)
        { mine.BlockActiveDuplicates = tab.EditBlockActive; wrote.Add("whether it can run twice at once"); }
        if (tab.EditBlockDone != mine.BlockCompletedDuplicates)
        { mine.BlockCompletedDuplicates = tab.EditBlockDone; wrote.Add("whether it can run again"); }
        if (tab.EditPreRoll != mine.RewardPreRoll)
        { mine.RewardPreRoll = tab.EditPreRoll; wrote.Add("whether the reward is rolled up front"); }

        // THE ID IS NOT EDITABLE ON AN IN-PLACE EDIT, because it IS the game's asset name — that
        // is the whole mechanism. Changing it would name a package nothing points at.
        var want = tab.EditId.Trim();
        if (want.Length > 0 && !want.Equals(mine.Id, StringComparison.Ordinal))
        {
            if (mine.InPlace)
                Say($"{mine.Id}: this edits the game's own mission in place, so its name IS the " +
                    "game's asset name. Renaming it would point at nothing.");
            else if (_mod.Missions.Any(x => !ReferenceEquals(x, mine) &&
                                            x.Id.Equals(want, StringComparison.OrdinalIgnoreCase)))
                Say($"{mine.Id}: this mod already has a mission called {want}");
            else { mine.Id = want; wrote.Add($"named {want}"); }
            tab.EditId = mine.Id;
        }

        _mod.Save();
        tab.MarkSaved();
        tab.Renamed();
        Say($"{mine.Id}: saved — " + (wrote.Count > 0 ? string.Join(", ", wrote) : "as it was"));
        AfterEditsChanged();
        return true;

        void Put(string typed, string was, Action<string> onto, string what)
        {
            var v = typed.Trim();
            if (v == was) return;
            onto(v);
            wrote.Add(what);
        }

        void Num(string typed, int was, int low, int high, Action<int> onto, string what)
        {
            if (!int.TryParse(typed.Trim(), out var v))
            { Say($"{what}: '{typed}' is not a whole number"); return; }
            v = Math.Clamp(v, low, high);
            if (v == was) return;
            onto(v);
            wrote.Add($"{what} is {v}");
        }
    }

    /// <summary>
    /// One of the five choosers, written the moment it is chosen.
    ///
    /// THE LOCATION TABLE MOVES WITH THE PLANET. A planet is a ROW of a particular region
    /// location table and there are several; keeping the donor's table while changing the row
    /// names a row that table does not have, and the mission lands on no planet at all.
    /// </summary>
    public async Task PickMission(MissionTab tab, string which)
    {
        if (_mod is null || tab.Mine is not { } mine) return;

        var all = await GameMissionsAsync();

        switch (which)
        {
            case "planet" when tab.PickedPlanet is { } p:
                mine.Planet = p.Value;
                if (all.FirstOrDefault(m => string.Equals(m.Planet, p.Value,
                        StringComparison.OrdinalIgnoreCase))?.LocationTable is { Length: > 0 } table)
                {
                    mine.LocationTable = table;
                    tab.LocationTableIs = table;
                }
                Say($"{mine.Id}: fought on {p.Value}");
                break;

            case "level" when tab.PickedLevel is { } l:
                mine.Level = l.Value;
                Say($"{mine.Id}: on the map {Leaf(l.Value)}");
                break;

            case "faction" when tab.PickedFaction is { } f:
                mine.Faction = f.Value;
                Say($"{mine.Id}: you fight {f.Shown}");
                break;

            case "type" when tab.PickedType is { } ty:
                mine.Type = ty.Value;
                Say($"{mine.Id}: the objective is {ty.Shown}");
                break;

            // ONE DIFFICULTY TAG, NOT TWO. The tag list can hold several and only one of them
            // means a difficulty, so the old one comes out before the new one goes in.
            case "difficulty" when tab.PickedDifficulty is { } d:
                mine.MissionTags.RemoveAll(t => t.Contains(".MissionDifficulty."));
                mine.MissionTags.Add(d.Value);
                Say($"{mine.Id}: {d.Shown}");
                break;

            default: return;
        }

        _mod.Save();
        RecheckMission(tab, mine, all, await FactionsAsync());
        AfterEditsChanged();
    }

    /// <summary>Put the region fields back to what the game files this planet under.</summary>
    public async Task MatchThePlanet(MissionTab tab)
    {
        if (_mod is null || tab.Mine is not { } mine) return;

        var all = await GameMissionsAsync();
        if (all.FirstOrDefault(m => string.Equals(m.Planet, mine.Planet,
                StringComparison.OrdinalIgnoreCase)) is not { } onPlanet) return;

        mine.RegionTag = onPlanet.RegionTag ?? "";
        mine.RegionAsset = onPlanet.RegionAssetName ?? "";
        tab.EditRegionTag = mine.RegionTag;
        tab.EditRegionAsset = mine.RegionAsset;

        _mod.Save();
        Say($"{mine.Id}: filed where the game files {mine.Planet}");
        RecheckMission(tab, mine, all, await FactionsAsync());
        AfterEditsChanged();
    }

    /// <summary>
    /// WHAT THE GAME'S OWN MISSIONS USE for one of the free-text fields.
    ///
    /// Every one of these values already exists on the 125 missions the reader opened, so the
    /// honest control is a list of them rather than a box.
    /// </summary>
    public async Task<List<PickAssetWindow.Candidate>> MissionChoicesAsync(string field)
    {
        var all = await GameMissionsAsync();

        IEnumerable<string?> from = field switch
        {
            "thumbnail" => all.Select(m => m.Thumbnail),
            "bank" => all.Select(m => m.ImageBankTag),
            "regiontag" => all.Select(m => m.RegionTag),
            "regionasset" => all.Select(m => m.RegionAssetName),
            "mapconfig" => all.Select(m => m.MapConfiguration),
            "reward" => all.SelectMany(m => m.Rewards),
            "rewardtable" => all.Select(m => m.RewardTable),
            "objective" => all.SelectMany(m => m.Objectives),
            "missiontag" => all.SelectMany(m => m.MissionTags),
            "fact" => all.Select(m => m.FactTag),
            _ => [],
        };

        return from.Where(x => x is { Length: > 0 })
                   .Select(x => x!)
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                   .Select(x => new PickAssetWindow.Candidate(
                       x.Contains('.') ? x[(x.LastIndexOf('.') + 1)..]
                       : x.Contains('/') ? x[(x.LastIndexOf('/') + 1)..] : x,
                       x, x))
                   .ToList();
    }

    /// <summary>Add one line to a mission's objectives, rewards or extra tags.</summary>
    public void AddMissionLine(MissionTab tab, string kind, string value)
    {
        if (_mod is null || tab.Mine is not { } mine || value.Length == 0) return;

        var into = kind switch
        {
            "objective" => mine.Objectives,
            "reward" => mine.Rewards,
            "tag" => mine.MissionTags,
            _ => null,
        };
        if (into is null) return;

        if (into.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)))
        { Say($"{mine.Id}: it already has {Leaf(value)}"); return; }

        into.Add(value);
        _mod.Save();
        Refill(tab, mine);
        Say($"{mine.Id}: + {Leaf(value)}");
        AfterEditsChanged();
    }

    /// <summary>Take one line back out.</summary>
    public void DropMissionLine(MissionTab tab, SheetTab.LineRow row)
    {
        if (_mod is null || tab.Mine is not { } mine) return;

        var from = row.Kind switch
        {
            "objective" => mine.Objectives,
            "reward" => mine.Rewards,
            "tag" => mine.MissionTags,
            _ => null,
        };
        if (from is null || !from.Remove(row.Value)) return;

        _mod.Save();
        Refill(tab, mine);
        Say($"{mine.Id}: \u2212 {row.Shown}");
        AfterEditsChanged();
    }

    /// <summary>
    /// THE DIFFICULTY TAG IS NOT IN THE LIST, because the chooser above owns it — the list would
    /// otherwise show it twice and let it be removed from under the control that sets it.
    /// </summary>
    private static void Refill(MissionTab tab, ModMission mine)
        => tab.SetMissionLists(mine.Objectives, mine.Rewards,
                               mine.MissionTags.Where(t => !t.Contains(".MissionDifficulty.")));
}

public sealed partial class StudioVm
{
    // ------------------------------------------------------------------ when a mission turns up

    // A MISSION THAT EXISTS IS OFFERED TO NOBODY. The galaxy map places some missions directly
    // and draws the rest from weighted tables, and neither consults what a mod happens to ship.
    // Three routes in, and they cost different things — see SheetTab's own note.

    private List<MissionIndex.Turn>? _gameTurns;
    private List<MissionIndex.Placement>? _gamePlacements;
    private List<MissionIndex.Table>? _gameTables;
    private List<MissionIndex.Draw>? _gameSchedule;
    private Dictionary<string, int>? _arcOrder;

    /// <summary>
    /// The campaign's own shape, read once: its turns, what it places, and its tables.
    ///
    /// ONLY ARCS THE CAMPAIGN ACTUALLY RUNS. `Arc_TestingStory01` and `Arc_CinematicTesting_A1`
    /// sit in the same folder and place missions of their own, and a slot taken in one of them is
    /// never reached by any playthrough. Worse, the short name collides — both `Arc_MainStory_A1`
    /// and `Arc_CinematicTesting_A1` shorten to "A1" — so a testing placement read as a
    /// first-chapter one.
    /// </summary>
    private async Task ReadCampaignAsync()
    {
        if (_gameTurns is not null || _provider is null) return;

        var provider = _provider;
        var got = await Task.Run(() =>
        {
            try
            {
                return (Turns: MissionIndex.Turns(provider),
                        Places: MissionIndex.Placements(provider),
                        Tables: MissionIndex.Tables(provider),
                        Schedule: MissionIndex.Schedule(provider),
                        Order: MissionIndex.ArcOrder(provider));
            }
            catch (Exception)
            {
                // NAMED HERE TOO. An unnamed tuple on one branch strips the names from the
                // whole expression, and the caller then cannot say which list is which.
                return (Turns: new List<MissionIndex.Turn>(),
                        Places: new List<MissionIndex.Placement>(),
                        Tables: new List<MissionIndex.Table>(),
                        Schedule: new List<MissionIndex.Draw>(),
                        Order: new Dictionary<string, int>());
            }
        });

        _gameTurns = got.Turns;
        _gamePlacements = got.Places;
        _gameTables = got.Tables;
        _gameSchedule = got.Schedule;
        _arcOrder = got.Order;

        Say($"the campaign: {_gameTurns.Count} turn(s), {_gamePlacements.Count} placed mission(s), " +
            $"{_gameTables.Count} weighted table(s)");
    }

    /// <summary>`Arc_MainStory_A4Start` -> `A4Start`, which is what the campaign order calls it.</summary>
    private static string ArcShort(string arc)
    {
        var cut = arc.LastIndexOf('_');
        return cut >= 0 && cut < arc.Length - 1 ? arc[(cut + 1)..] : arc;
    }

    /// <summary>How many times the campaign pulls from one table. Invisible otherwise, and it is
    /// the difference between seeing your mission and not: SK_Table_Imperial is drawn three times
    /// in a whole campaign and SK_Table_Coil eight.</summary>
    private int PullsOf(string tablePackage) => (_gameSchedule ?? [])
        .Count(d => string.Equals(d.TablePackage, tablePackage, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fill the three routes, and say whether any of them will offer this mission.</summary>
    private async Task FillWhenAsync(MissionTab tab, ModMission mine)
    {
        tab.BeginFill();
        try { await FillWhenCore(tab, mine); }
        finally { tab.EndFill(); }
    }

    private async Task FillWhenCore(MissionTab tab, ModMission mine)
    {
        await ReadCampaignAsync();
        tab.SecWhen.Present = true;

        var order = _arcOrder;

        var turns = (_gameTurns ?? [])
            .Where(t => order is null || order.ContainsKey(t.Arc))
            .Select(t =>
            {
                var entry = ModMission.ArcAddEntry(t.ArcPackage, t.Number);
                var bits = new List<string>();
                if (t.Missions > 0) bits.Add($"{t.Missions} mission(s) placed");
                if (t.Tables > 0) bits.Add($"{t.Tables} table pull(s)");
                if (t.Recipes > 0) bits.Add($"{t.Recipes} recipe(s)");
                var alone = t.Missions == 0 && t.Tables == 0;
                return new SheetTab.TurnRow(
                    entry, ArcShort(t.Arc), t.Number,
                    alone ? "nothing — yours would arrive alone" : string.Join(", ", bits),
                    alone,
                    mine.ArcAdds.Any(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase)));
            })
            .ToList();

        // GROUPED BY THE MISSION, NOT BY ARC AND TURN. No shipped mission is placed by two arcs,
        // but one that were would have to have BOTH re-pointed or it still arrives from the other.
        var slots = (_gamePlacements ?? [])
            .Where(p => order is null || order.ContainsKey(p.Arc))
            .GroupBy(p => p.MissionPackage, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var leaf = Leaf(g.Key);
                var finale = leaf.Contains("Finale", StringComparison.OrdinalIgnoreCase);
                var (costs, kind) =
                    finale ? ("the campaign's finale — not offered", "finale")
                    : leaf.StartsWith("MM_", StringComparison.OrdinalIgnoreCase) ? ("main story", "story")
                    : leaf.StartsWith("HM_", StringComparison.OrdinalIgnoreCase) ? ("hero mission", "hero")
                    : ("optional skirmish — cheapest to take", "cheap");

                return new SheetTab.SlotRow(
                    g.Key, leaf,
                    string.Join(", ", g.Select(p => $"{ArcShort(p.Arc)} turn {p.Turn}")),
                    costs, kind, finale,
                    mine.Placements.Any(x => x.Equals(g.Key, StringComparison.OrdinalIgnoreCase)));
            })
            .OrderBy(s => s.Kind switch { "cheap" => 0, "hero" => 1, "story" => 2, _ => 3 })
            .ThenBy(s => s.Leaf, StringComparer.OrdinalIgnoreCase)
            .ToList();

        tab.TableChoices.Clear();
        foreach (var x in (_gameTables ?? []).OrderBy(x => x.Leaf, StringComparer.OrdinalIgnoreCase))
            tab.TableChoices.Add(new SheetTab.Choice(
                x.Package, x.Leaf, $"{x.Rows.Count} rows, drawn {PullsOf(x.Package)}x"));

        var rows = mine.Tables.Select(r => MakeLottery(tab, r)).ToList();

        tab.SetWhen(turns, slots, rows);
        RemarkWhen(tab, mine);
    }

    /// <summary>One lottery row, with the table's own rows behind its take-over chooser.</summary>
    private SheetTab.LotteryRow MakeLottery(MissionTab tab, ModMissionRow r)
    {
        var made = new SheetTab.LotteryRow(
            r, r.Table,
            r.Weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            r.ReplaceRow < 0, Math.Max(0, r.ReplaceRow));

        FillLottery(tab, made, r);
        return made;
    }

    /// <summary>
    /// What the chosen table holds, and what this row would do to it.
    ///
    /// THE ARITHMETIC IS THE POINT of showing it at all: "one more row among six" and "about 14%
    /// of each pull" are the same fact, and only the second answers "will I ever see it".
    /// </summary>
    private void FillLottery(MissionTab tab, SheetTab.LotteryRow row, ModMissionRow held)
    {
        tab.BeginFill();
        try { FillLotteryCore(tab, row, held); }
        finally { tab.EndFill(); }
    }

    private void FillLotteryCore(MissionTab tab, SheetTab.LotteryRow row, ModMissionRow held)
    {
        var table = (_gameTables ?? []).FirstOrDefault(
            x => string.Equals(x.Package, held.Table, StringComparison.OrdinalIgnoreCase));

        row.Rows.Clear();
        if (table is not null)
            for (var i = 0; i < table.Rows.Count; i++)
                row.Rows.Add(new SheetTab.Choice(
                    i.ToString(), $"row {i}", Leaf(table.Rows[i].MissionPackage)));

        row.PickedRow = row.Rows.FirstOrDefault(c => c.Value == row.WhichRow.ToString())
                        ?? row.Rows.FirstOrDefault();

        row.Effect = table is null ? "pick a table"
            : held.ReplaceRow < 0
                ? $"{table.Leaf} would go from {table.Rows.Count} rows to {table.Rows.Count + 1}. " +
                  (table.Rows.Sum(x => x.Weight) + held.Weight is var total && total > 0
                      ? $"At weight {held.Weight:0.##} yours is about {held.Weight / total:P0} of each pull."
                      : "")
            : $"{table.Leaf} keeps its {table.Rows.Count} rows; one of them points at your mission " +
              "instead of the one it held.";

        row.Requires.Clear();
        foreach (var x in held.RequireTags) row.Requires.Add(new SheetTab.LineRow("require", x));
        row.Ignores.Clear();
        foreach (var x in held.IgnoreTags) row.Ignores.Add(new SheetTab.LineRow("ignore", x));
        row.Gates.Clear();
        foreach (var f in held.FactValues)
            row.Gates.Add(new SheetTab.GateRow(f, f.Tag, f.Operation,
                f.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                f.MissingIsZero));
        row.Remark();
    }

    /// <summary>
    /// WHETHER ANYTHING WILL EVER OFFER IT, in one sentence.
    ///
    /// A mission in no table, on no turn and in no slot builds, installs, reads back clean and is
    /// never drawn — which from the outside is a mod that did not work. An IN-PLACE edit needs
    /// none of this, because it replaces a mission the campaign already offers.
    /// </summary>
    private static void RemarkWhen(MissionTab tab, ModMission mine)
    {
        if (mine.InPlace)
        {
            tab.NeverOffered = false;
            tab.WhenSays =
                "This edits the game's own mission in place, so it arrives exactly where and when " +
                "that one already does. Nothing here is needed.";
            tab.SecWhen.Count = "in place";
            return;
        }

        var bits = new List<string>();
        if (mine.ArcAdds.Count > 0) bits.Add($"added to {mine.ArcAdds.Count} campaign turn(s)");
        if (mine.Placements.Count > 0) bits.Add($"takes {mine.Placements.Count} campaign slot(s)");
        if (mine.Tables.Count > 0) bits.Add($"in {mine.Tables.Count} weighted table row(s)");

        tab.NeverOffered = bits.Count == 0;
        tab.WhenSays = bits.Count == 0
            ? "Nothing offers this mission. It is in no table, on no turn and in no slot, so it " +
              "will build, install, read back clean and never be drawn."
            : char.ToUpperInvariant(string.Join(", ", bits)[0]) + string.Join(", ", bits)[1..] + ".";
        tab.SecWhen.Count = bits.Count == 0 ? "nothing offers it" : "";
        tab.SecWhen.Changed = bits.Count > 0;
    }

    /// <summary>Add this mission to a campaign turn, or take it off one. Nothing is displaced.</summary>
    public void SetArcAdd(MissionTab tab, SheetTab.TurnRow row)
    {
        if (_mod is null || tab.Mine is not { } mine) return;

        if (row.Taken)
        {
            if (!mine.ArcAdds.Any(x => x.Equals(row.Entry, StringComparison.OrdinalIgnoreCase)))
                mine.ArcAdds.Add(row.Entry);
            Say($"{mine.Id}: arrives on {row.Arc} {row.Turn}, beside whatever that turn already places");
        }
        else
        {
            mine.ArcAdds.RemoveAll(x => x.Equals(row.Entry, StringComparison.OrdinalIgnoreCase));
            Say($"{mine.Id}: no longer added to {row.Arc} {row.Turn}");
        }

        _mod.Save();
        RemarkWhen(tab, mine);
        AfterEditsChanged();
    }

    /// <summary>Take a campaign slot, or give it back. This one DOES displace a shipped mission.</summary>
    public void SetPlacement(MissionTab tab, SheetTab.SlotRow row)
    {
        if (_mod is null || tab.Mine is not { } mine) return;

        // THE FINALE IS NEVER TAKEN. The control is disabled, and this refuses as well: a guard
        // that lives only in the view is one a flag or a future button walks straight past.
        if (row.Finale)
        { row.Taken = false; Say("the campaign's finale cannot be displaced"); return; }

        if (row.Taken)
        {
            if (!mine.Placements.Any(x => x.Equals(row.Package, StringComparison.OrdinalIgnoreCase)))
                mine.Placements.Add(row.Package);
            Say($"{mine.Id}: takes {row.Leaf}'s slot ({row.PlacedBy}) — {row.Leaf} is no longer " +
                "offered while this mod is installed");
        }
        else
        {
            mine.Placements.RemoveAll(x => x.Equals(row.Package, StringComparison.OrdinalIgnoreCase));
            Say($"{mine.Id}: gave {row.Leaf}'s slot back");
        }

        _mod.Save();
        RemarkWhen(tab, mine);
        AfterEditsChanged();
    }

    /// <summary>Put this mission into a weighted table.</summary>
    public async Task AddLottery(MissionTab tab)
    {
        if (_mod is null || tab.Mine is not { } mine) return;
        await ReadCampaignAsync();

        // ADD A ROW BY DEFAULT, because taking one over costs the game the mission that sat
        // there and no table has a spare: bEmptyOption is false on all 65 of them.
        var made = new ModMissionRow
        {
            Table = (_gameTables ?? []).FirstOrDefault()?.Package ?? "",
            Weight = 1,
            ReplaceRow = -1,
        };
        mine.Tables.Add(made);
        _mod.Save();

        // REBUILT IN ONE GO rather than appended. A row pushed into a live bound collection is
        // realised later, and its controls then raise their own change events against a card
        // that is no longer being filled — see SetLottery's "whichrow".
        await FillWhenAsync(tab, mine);
        Say($"{mine.Id}: a row in {Leaf(made.Table)} — one candidate among that table's others");
        AfterEditsChanged();
    }

    /// <summary>Take one lottery row back out.</summary>
    public async Task DropLottery(MissionTab tab, SheetTab.LotteryRow row)
    {
        if (_mod is null || tab.Mine is not { } mine) return;
        if (row.Held is not ModMissionRow held || !mine.Tables.Remove(held)) return;

        _mod.Save();
        await FillWhenAsync(tab, mine);
        Say($"{mine.Id}: out of {row.TableLeaf}");
        AfterEditsChanged();
    }

    /// <summary>One field of one lottery row.</summary>
    public void SetLottery(MissionTab tab, SheetTab.LotteryRow row, string which)
    {
        if (_mod is null || tab.Mine is not { } mine) return;
        if (row.Held is not ModMissionRow held) return;

        switch (which)
        {
            case "table":
                held.Table = row.Table;
                // THE ROW INDEX IS THE OLD TABLE'S. Keeping it would name a row the new table may
                // not have, and the take-over would land on whatever happened to be at that index.
                held.ReplaceRow = row.AddsRow ? -1 : 0;
                Say($"{mine.Id}: in {row.TableLeaf}, drawn {PullsOf(held.Table)}x a campaign");
                break;

            case "weight":
                if (!double.TryParse(row.Weight, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var w) ||
                    w < 0)
                { Say($"weight: '{row.Weight}' is not a number"); return; }
                held.Weight = w;
                break;

            case "how":
                held.ReplaceRow = row.AddsRow ? -1
                    : int.TryParse(row.PickedRow?.Value, out var at) ? at : 0;
                Say(row.AddsRow
                    ? $"{mine.Id}: ADDS a row to {row.TableLeaf} — nothing is displaced"
                    : $"{mine.Id}: takes over one of {row.TableLeaf}'s rows");
                break;

            // A WRITE THAT CHANGES NOTHING IS NOT A WRITE, and here that is not tidiness.
            //
            // A ComboBox raises SelectionChanged when its container is REALISED, which happens
            // after the fill that set it has returned — so the latch is already released and the
            // handler runs with whatever the freshly built control selected. Eleven rows added
            // in one press all came back as row 0 that way. Comparing first means a realisation
            // that re-selects what is already there writes nothing at all.
            case "whichrow":
                if (row.AddsRow) return;
                if (!int.TryParse(row.PickedRow?.Value, out var pick) ||
                    pick == held.ReplaceRow) return;
                held.ReplaceRow = pick;
                break;

            default: return;
        }

        _mod.Save();
        FillLottery(tab, row, held);
        RemarkWhen(tab, mine);
        AfterEditsChanged();
    }

    /// <summary>
    /// TAKE EVERY ROW of the chosen table, because the weight cannot be written yet.
    ///
    /// One row of six is about a one-in-six pull; every row pointed at your mission is a
    /// certainty, and it needs no machinery that does not already work. Good for proving a
    /// mission works — and it costs the game every mission those rows held, so the message says
    /// so and the rows are as easy to take back out as they were to add.
    /// </summary>
    public async Task TakeEveryRow(MissionTab tab, SheetTab.LotteryRow row)
    {
        if (_mod is null || tab.Mine is not { } mine) return;
        if (row.Held is not ModMissionRow held) return;

        var table = (_gameTables ?? []).FirstOrDefault(
            x => string.Equals(x.Package, held.Table, StringComparison.OrdinalIgnoreCase));
        if (table is null) { Say("pick a table first"); return; }

        // THE MODELS FIRST, ALL OF THEM, and the card rebuilt once at the end. Appending
        // eleven rows to a live bound collection realised eleven ComboBoxes whose own
        // SelectionChanged then wrote row 0 over every index this loop had just set.
        held.ReplaceRow = Math.Max(0, held.ReplaceRow);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var at = i;
            if (mine.Tables.Any(x => x.ReplaceRow == at &&
                                     string.Equals(x.Table, held.Table,
                                                   StringComparison.OrdinalIgnoreCase))) continue;
            mine.Tables.Add(new ModMissionRow
            { Table = held.Table, Weight = held.Weight, ReplaceRow = at });
        }

        _mod.Save();
        await FillWhenAsync(tab, mine);
        Say($"{mine.Id}: takes all {table.Rows.Count} of {table.Leaf}'s rows — whichever the game " +
            $"draws, it draws yours. It costs the game every mission those rows held.");
        AfterEditsChanged();
    }

    /// <summary>A fact the row requires, or one that blocks it.</summary>
    public void AddGateTag(MissionTab tab, SheetTab.LotteryRow row, string kind, string tag)
    {
        if (_mod is null || tab.Mine is not { } mine) return;
        if (row.Held is not ModMissionRow held || tag.Length == 0) return;

        var into = kind == "require" ? held.RequireTags : held.IgnoreTags;
        if (into.Any(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase))) return;
        into.Add(tag);

        _mod.Save();
        FillLottery(tab, row, held);
        Say(kind == "require"
            ? $"{mine.Id}: only drawn once {Leaf(tag)} is set"
            : $"{mine.Id}: never drawn while {Leaf(tag)} is set");
        AfterEditsChanged();
    }

    public void DropGateTag(MissionTab tab, SheetTab.LotteryRow row, SheetTab.LineRow line)
    {
        if (_mod is null || row.Held is not ModMissionRow held) return;

        var from = line.Kind == "require" ? held.RequireTags : held.IgnoreTags;
        if (!from.Remove(line.Value)) return;

        _mod.Save();
        FillLottery(tab, row, held);
        AfterEditsChanged();
    }

    /// <summary>A numeric gate — the game's own crisis table gates every row on one.</summary>
    public void AddGate(MissionTab tab, SheetTab.LotteryRow row)
    {
        if (_mod is null || row.Held is not ModMissionRow held) return;

        held.FactValues.Add(new ModFactCondition { Operation = "LessThan" });
        _mod.Save();
        FillLottery(tab, row, held);
        AfterEditsChanged();
    }

    public void SetGate(MissionTab tab, SheetTab.LotteryRow row, SheetTab.GateRow gate, string which)
    {
        if (_mod is null || gate.Held is not ModFactCondition cond) return;

        switch (which)
        {
            case "tag": cond.Tag = gate.Tag; break;
            case "op": cond.Operation = gate.Operation; break;
            case "value":
                if (!double.TryParse(gate.Value, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var v))
                { Say($"the number: '{gate.Value}' is not one"); return; }
                cond.Value = v;
                break;
            case "zero": cond.MissingIsZero = gate.MissingIsZero; break;
            default: return;
        }

        _mod.Save();
        AfterEditsChanged();
    }

    public void DropGate(MissionTab tab, SheetTab.LotteryRow row, SheetTab.GateRow gate)
    {
        if (_mod is null || row.Held is not ModMissionRow held) return;
        if (gate.Held is not ModFactCondition cond || !held.FactValues.Remove(cond)) return;

        _mod.Save();
        FillLottery(tab, row, held);
        AfterEditsChanged();
    }
}

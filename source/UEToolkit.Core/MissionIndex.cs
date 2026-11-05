namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.GameplayTags;

/// <summary>
/// THE GAME'S OWN MISSIONS, AND THE TABLES THAT DECIDE WHEN THEY APPEAR.
///
/// Read rather than inferred, because the shape of a mission is the whole design of the editor
/// above it. A `BrunoMissionData` is seventeen properties and three sub-objects, and every one of
/// them is data -- planet, map, enemy faction, objective kind, squad size, rewards, and the fact
/// it increments on completion. The ONLY part of a mission that is not data is the map.
///
/// AND THE GAME REUSES MAPS, which I had written down as the opposite. 125 missions run on 74
/// maps; 36 maps carry two or more. `SK_Kaller_030_Root` carries THREE -- Kaller 030 (Separatist,
/// Capture), 040 (Imperial, Rescue) and 045 (Imperial, Sabotage). Different factions and different
/// objectives, on one level, shipped. So a new mission on an existing map is not a novel thing
/// being attempted; it is what the game already does, and it is strong evidence that the faction
/// tag really does choose the roster and the type tag really does drive the objective.
///
/// WHAT THIS IS FOR. The author's goal, in his words: "take existing levels and make new missions
/// on them, set post-game", with Separatist holdouts fighting in the Empire era. Nothing ties a
/// table to a faction -- the shipped game aligns them by NAMING CONVENTION alone -- so a
/// Separatist-faction mission whose ROW sits in `SK_Table_Imperial`, fact-gated to post-campaign,
/// is exactly what he described, using two axes the game has always had and never crossed.
///
/// WHAT IS STILL UNKNOWN. The shared-map evidence above makes 1 and 2 likely but does not settle
/// them -- a level could hold every objective set and switch on something neither tag names:
///   1. does `EnemyFactionTag` SELECT the roster, or only label it?
///   2. does `MissionTypeTag` DRIVE the objective, or do the level's own
///      `BP_TacticalObjective_*` actors decide and the tag only feed the UI?
///   3. does a row a MOD adds to a shipped `MissionWeightedTable` get drawn?
/// Do not write any of them up as established. A launch settles all three.
/// See [[zero-company-missions-are-data]].
/// </summary>
public static class MissionIndex
{
    /// <summary>Where the shipped missions live. Their own folder, so this is not a guess.</summary>
    private const string MissionFolder = "GameData/MissionData";

    /// <summary>The five weighted tables, one per faction plus the crisis one.</summary>
    private const string TableFolder = "GameData/MissionData/MissionTables";

    /// <summary>Where spawn groups live -- the rosters a level's spawners point at.</summary>
    private const string SpawnFolder = "GameData/SpawnGroups";

    // ------------------------------------------------------------------------------ what a mission is

    /// <summary>
    /// One shipped mission, with every property an author can change.
    ///
    /// NOT a summary. The editor writes each of these back, so a field missing here is a field an
    /// author cannot reach -- which is the whole of what was asked for.
    /// </summary>
    public sealed record Mission(
        string Package,
        string Leaf,
        // --- the UI fragment
        string Display,
        string ShortDescription,
        string LongDescription,
        string? Thumbnail,
        string? ImageBankTag,
        // --- where
        string? LocationTable,
        string? Planet,
        string? RegionTag,
        string? RegionAssetType,
        string? RegionAssetName,
        string Level,
        // --- what
        string Faction,
        string Type,
        IReadOnlyList<string> MissionTags,
        IReadOnlyList<string> Objectives,
        // --- how much
        int MinSquad,
        int MaxSquad,
        int LifeSpan,
        bool BlockActiveDuplicates,
        bool BlockCompletedDuplicates,
        string? MapConfiguration,
        // --- what you get, and what it remembers
        IReadOnlyList<string> Rewards,
        string? RewardTable,
        bool RewardPreRoll,
        string? FactTag,
        int FactIncrement)
    {
        /// <summary>The planet if the row is readable, else the asset's own name. Never blank.</summary>
        public string Where => Planet is { Length: > 0 } p ? p : Leaf;

        /// <summary>`br.Mission.Skirmish.Rescue` -> `Rescue`. The tag's own last word.</summary>
        public string Objective => Short(Type);

        /// <summary>`br.Faction.Imperial` -> `Imperial`.</summary>
        public string Who => Short(Faction);

        private static string Short(string tag) =>
            tag.Length == 0 ? "" : tag.Contains('.') ? tag[(tag.LastIndexOf('.') + 1)..] : tag;
    }

    /// <summary>
    /// One row of a weighted table: a mission, how likely it is, and what must already be true.
    /// </summary>
    public sealed record Row(
        double Weight,
        bool EmptyOption,
        string MissionPackage,
        IReadOnlyList<string> RequireTags,
        IReadOnlyList<string> IgnoreTags,
        IReadOnlyList<FactCondition> FactValues);

    /// <summary>
    /// A numeric condition on a fact -- the mechanism behind "appears only after that one".
    ///
    /// A mission's rewards can carry `CustomReward_IncrementFactValue`, so mission A raises a fact
    /// and mission B's ROW requires it. `Facts.Values.Strategy.Skirmish.TableImperial &lt; 2` is how
    /// the shipped crisis table paces itself, and it is the same lever an era gate would use.
    /// </summary>
    public sealed record FactCondition(
        string Tag,
        string Operation,
        double Value,
        bool MissingIsZero,
        string? OtherTag,
        double Min,
        bool InclusiveMin,
        double Max,
        bool InclusiveMax);

    public sealed record Table(string Package, string Leaf, IReadOnlyList<Row> Rows);

    /// <summary>
    /// A FACTION THAT CAN ACTUALLY FIGHT YOU -- and the chain that makes `EnemyFactionTag` mean
    /// something.
    ///
    /// The faction tag list holds twelve tags, and picking one for a mission looked like a free
    /// choice between twelve. It is not. A mission's `EnemyFactionTag` is matched against a
    /// `FactionData` asset's `IdentifyingFactionTag`, and THAT asset owns the `SpawnSets` the
    /// level's spawners draw from:
    ///
    ///     mission.EnemyFactionTag  ->  FactionData.IdentifyingFactionTag
    ///                                    SpawnSets[]      -> SS_&lt;Faction&gt;_Threat_NN
    ///                                    CrisisEliteSpawnSets[]
    ///
    /// **Only five FactionData assets ship**: Coil (10 sets), Separatist (10), Outlaw (5),
    /// Imperial (2), Slugrat (1). Those are exactly the five factions the 125 shipped missions
    /// use, which is not a coincidence -- the other seven tags (BC, Mandalorian, Miner, Noble,
    /// Pyke, SecondSister, Traitor) have no FactionData, so a mission naming one has no roster to
    /// draw from and would spawn nobody. Nothing anywhere would say so.
    ///
    /// This also answers, from the data rather than from a guess, the first of the open questions
    /// above: `EnemyFactionTag` really does SELECT the roster. It is still not proven that a MOD's
    /// tag reaches the same chain, which a launch settles.
    /// </summary>
    public sealed record Faction(string Tag, string Leaf, int SpawnSets, int CrisisSets)
    {
        /// <summary>`br.Faction.Separatist` -> `Separatist`.</summary>
        public string Name => Tag.Contains('.') ? Tag[(Tag.LastIndexOf('.') + 1)..] : Tag;

        /// <summary>A faction with no spawn set cannot put anyone on a map.</summary>
        public bool CanFight => SpawnSets > 0;
    }

    /// <summary>One roster: a spawn group and the characters it puts on the map.</summary>
    public sealed record SpawnGroup(string Package, string Leaf, IReadOnlyList<SpawnEntry> Entries);

    public sealed record SpawnEntry(
        string CharacterClass,
        int MinCount,
        int MaxCount,
        bool PullFromPool,
        IReadOnlyList<string> TagsWhenSpawned)
    {
        /// <summary>`Char_Enemy_Imperial_TKTrooper_C` -> `TKTrooper`, best effort.</summary>
        public string Display
        {
            get
            {
                var leaf = CharacterClass;
                var cut = leaf.LastIndexOf('.');
                if (cut >= 0) leaf = leaf[(cut + 1)..];
                if (leaf.EndsWith("_C", StringComparison.Ordinal)) leaf = leaf[..^2];
                return leaf.StartsWith("Char_Enemy_", StringComparison.Ordinal) ? leaf[11..] : leaf;
            }
        }

        public string Count => MinCount == MaxCount ? $"{MinCount}" : $"{MinCount}-{MaxCount}";
    }

    // ------------------------------------------------------------------------------ reading

    /// <summary>
    /// Every shipped mission. 31 in `Optional` plus 11 `AuthoredHero` and the crisis ones, so this
    /// opens about fifty packages -- affordable on a click, not every frame.
    /// </summary>
    public static List<Mission> All(IFileProvider provider)
    {
        var found = new List<Mission>();
        foreach (var path in PackagesUnder(provider, MissionFolder))
        {
            // The tables live under MissionData too and are not missions.
            if (path.Contains(TableFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (Read(provider, path) is { } m) found.Add(m);
        }
        return found.OrderBy(m => m.Where, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.Leaf, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One mission, by its container path. Null when the package holds no mission.</summary>
    public static Mission? Read(IFileProvider provider, string containerPath)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(containerPath)).GetExports().ToList());

            var data = exports.FirstOrDefault(e =>
                string.Equals(e.ExportType, "BrunoMissionData", StringComparison.Ordinal));
            if (data is null) return null;

            var package = PackageId.PackageNameFor(containerPath, ModBuild.ProjectName);
            if (package is null) return null;

            // THE UI HALF IS A SEPARATE EXPORT, named from `Fragments`. Reading the mission export
            // alone gives a mission with no name -- the display name, both descriptions and the
            // thumbnail all live on a `BitReactorUIDataFragment` beside it.
            var ui = exports.FirstOrDefault(e =>
                string.Equals(e.ExportType, "BitReactorUIDataFragment", StringComparison.Ordinal));

            // So do the rewards. `CustomReward_IncrementFactValue_C` is the fact this mission
            // raises on completion, which is the entire sequencing mechanism.
            var fact = exports.FirstOrDefault(e =>
                e.ExportType?.StartsWith("CustomReward_IncrementFactValue", StringComparison.Ordinal) == true);
            var rewardTable = exports.FirstOrDefault(e =>
                e.ExportType?.StartsWith("CustomReward_RewardTable", StringComparison.Ordinal) == true);

            var location = Struct(data, "Location");
            var region = Struct(data, "RegionAssetId");
            var image = ui is null ? null : Struct(ui, "SmallImage");

            return new Mission(
                Package: package,
                Leaf: Path.GetFileNameWithoutExtension(containerPath),
                Display: Text(ui, "DisplayName") ?? "",
                ShortDescription: Text(ui, "ShortDescription") ?? "",
                LongDescription: Text(ui, "LongDescription") ?? "",
                Thumbnail: image is null ? null : BrushTexture(image),
                ImageBankTag: image is null ? null : PartDetails.TagOf(image.Properties, "ImageBankTag"),
                LocationTable: location is null ? null : ObjectIn(Value(location.Properties, "DataTable")),
                Planet: location is null ? null : Str(Value(location.Properties, "RowName")),
                RegionTag: PartDetails.TagOf(data.Properties, "RegionTag"),
                RegionAssetType: region is null ? null
                    : Str(Value(Struct(region, "PrimaryAssetType")?.Properties, "Name")),
                RegionAssetName: region is null ? null : Str(Value(region.Properties, "PrimaryAssetName")),
                Level: ObjectIn(Value(data.Properties, "Level")) ?? "",
                Faction: PartDetails.TagOf(data.Properties, "EnemyFactionTag") ?? "",
                Type: PartDetails.TagOf(data.Properties, "MissionTypeTag") ?? "",
                MissionTags: Tags(data, "MissionTags"),
                Objectives: Texts(data, "ObjectivesText"),
                MinSquad: Int(data, "MinSquadSize"),
                MaxSquad: Int(data, "MaxSquadSize"),
                LifeSpan: Int(data, "LifeSpan"),
                BlockActiveDuplicates: Bool(data, "bBlockActiveDuplicates"),
                BlockCompletedDuplicates: Bool(data, "bBlockCompletedDuplicates"),
                MapConfiguration: ObjectIn(Value(data.Properties, "MapConfiguration")),
                Rewards: Objects(data, "Rewards"),
                RewardTable: rewardTable is null ? null
                    : ObjectIn(Value(rewardTable.Properties, "RewardTable")),
                RewardPreRoll: rewardTable is not null && Bool(rewardTable, "bPreRoll"),
                FactTag: fact is null ? null : PartDetails.TagOf(fact.Properties, "FactValueTag"),
                FactIncrement: fact is null ? 0 : Int(fact, "IncrementAmount"));
        }
        catch (Exception) { return null; }
    }

    /// <summary>The five weighted tables, with their rows.</summary>
    public static List<Table> Tables(IFileProvider provider)
    {
        var found = new List<Table>();
        foreach (var path in PackagesUnder(provider, TableFolder))
        {
            try
            {
                var exports = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(path)).GetExports().ToList());
                var t = exports.FirstOrDefault(e =>
                    string.Equals(e.ExportType, "MissionWeightedTable", StringComparison.Ordinal));
                if (t is null) continue;

                var package = PackageId.PackageNameFor(path, ModBuild.ProjectName);
                if (package is null) continue;

                var rows = new List<Row>();
                foreach (var entry in StructsIn(Value(t.Properties, "WeightedMissions")))
                {
                    var reqs = Struct(entry, "FactTagRequirements");
                    rows.Add(new Row(
                        Weight: Num(Value(entry.Properties, "Weight")),
                        EmptyOption: Value(entry.Properties, "bEmptyOption") is true,
                        MissionPackage: ObjectIn(Value(entry.Properties, "Mission")) ?? "",
                        RequireTags: reqs is null ? [] : TagsIn(Value(reqs.Properties, "RequireTags")),
                        IgnoreTags: reqs is null ? [] : TagsIn(Value(reqs.Properties, "IgnoreTags")),
                        FactValues: Conditions(Value(entry.Properties, "FactValueRequirements"))));
                }
                found.Add(new Table(package, Path.GetFileNameWithoutExtension(path), rows));
            }
            catch (Exception) { /* a table that will not parse is simply not offered */ }
        }
        return found.OrderBy(t => t.Leaf, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// WHEN A TABLE IS ACTUALLY DRAWN FROM -- one arc, one turn, one pull.
    ///
    /// The thing that makes "era gating" mean something, and it is not what the name suggests. A
    /// table is not ACTIVE during an era. A campaign arc's `TurnsMap` says, for one turn, "pull
    /// `NumberToSelect` missions out of this table" -- and that is the only moment the table is
    /// consulted. The author asked whether a mission in `SK_Table_Imperial` would appear "since
    /// I'm already in that era", and the honest answer needed this measured:
    ///
    ///     SK_Table_Imperial   Arc_MainStory_A5      turn 0
    ///                         Arc_MainStory_A5Start turn 1
    ///                         Arc_MainStory_A6      turn 0
    ///
    /// Three draws in the whole campaign, one mission each. So a row added after those turns have
    /// passed can never be drawn, and a row added before them is one weighted candidate out of
    /// six. That is a completely different thing from "it shows up in the Empire era", and an
    /// author cannot find it out from anywhere else.
    ///
    /// ARCS ARE NOT THE ONLY POSSIBLE CONSUMER. This reads the shipped campaign arcs, which is
    /// where every draw found so far lives; something else could pull a table without going
    /// through one. Presented as "these are the draws we can see", not as a complete list.
    /// </summary>
    /// <param name="TablePackage">
    /// PACKAGE ONLY, with the `.Asset` half stripped. An object reference reads back as
    /// `/Game/.../SK_Table_Imperial.SK_Table_Imperial` and a package name is
    /// `/Game/.../SK_Table_Imperial`, so comparing the two raw forms never matches -- which made
    /// the panel say every table was drawn 0 times and print "no campaign arc pulls from this
    /// table" in red under all five. Caught only by printing the schedule beside a count.
    /// </param>
    /// <param name="Crisis">
    /// True when the draw comes from `CrisisMissionData.MissionTable` rather than the turn's
    /// `MissionTables` array. A different field and a different mechanism -- it is how the crisis
    /// table is reached, which is why that table looked unused when only the array was read.
    /// </param>
    public sealed record Draw(string Arc, int Turn, string TablePackage,
                              int NumberToSelect, bool AllowDuplicates, bool Crisis = false);

    /// <summary>Every table draw the shipped campaign arcs schedule. See <see cref="Draw"/>.</summary>
    public static List<Draw> Schedule(IFileProvider provider)
    {
        var found = new List<Draw>();
        foreach (var path in PackagesUnder(provider, "GameData/CampaignData"))
        {
            try
            {
                var exports = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(path)).GetExports().ToList());
                var arc = exports.FirstOrDefault(e =>
                    string.Equals(e.ExportType, "BrunoCampaignArc", StringComparison.Ordinal));
                if (arc is null) continue;

                var name = Path.GetFileNameWithoutExtension(path);
                if (PartDetails.Unwrap(Value(arc, "TurnsMap")) is not UScriptMap turns) continue;

                foreach (var kv in turns.Properties)
                {
                    if (!int.TryParse(Str(kv.Key?.GenericValue) ?? "", out var turn)) continue;
                    if (Struct(kv.Value?.GenericValue) is not { } perTurn) continue;

                    foreach (var entry in StructsIn(Value(perTurn.Properties, "MissionTables")))
                    {
                        var table = PackageOf(ObjectIn(Value(entry.Properties, "MissionTable")));
                        if (table is null) continue;
                        found.Add(new Draw(
                            name, turn, table,
                            (int) Num(Value(entry.Properties, "NumberToSelect")),
                            Value(entry.Properties, "bAllowDuplicates") is true));
                    }

                    // THE CRISIS TABLE IS REACHED ANOTHER WAY. It is not in the MissionTables
                    // array at all -- it hangs off CrisisMissionData beside it, which is why
                    // reading only the array reported SK_Table_CrisisTable as never used while
                    // four arcs plainly name it.
                    if (Value(perTurn.Properties, "bHasCrisisMissionData") is true &&
                        Struct(Value(perTurn.Properties, "CrisisMissionData")) is { } crisis &&
                        PackageOf(ObjectIn(Value(crisis.Properties, "MissionTable"))) is { } ct)
                        found.Add(new Draw(name, turn, ct,
                                           (int) Num(Value(crisis.Properties, "NumEffectsToPick")),
                                           false, Crisis: true));
                }
            }
            catch (Exception) { /* an arc that will not parse contributes no draws */ }
        }
        // IN CAMPAIGN ORDER, which is not alphabetical and not guessable.
        //
        // `Campaign_MainStory.Arcs` runs A1, A2Start, A2, A3, A4Start, A4, A5Start, A5, A6, A7 --
        // so a "Start" arc comes BEFORE the plain one of the same number, not after it and not as
        // some chapter-select variant. Sorting these by name puts A5 ahead of A5Start and reverses
        // two of the five pairs, which turns "what is still ahead of me" into a wrong answer.
        var order = ArcOrder(provider);
        return found.OrderBy(d => order.TryGetValue(d.Arc, out var at) ? at : int.MaxValue)
                    .ThenBy(d => d.Turn).ToList();
    }

    /// <summary>
    /// A MISSION A CAMPAIGN TURN PLACES DIRECTLY -- no table, no weight, no roll.
    ///
    /// The turn struct carries `Missions[]` beside `MissionTables[]`, and where a table entry says
    /// "pull one of these at random" this says "this one, on this turn". The shipped campaign uses
    /// it 31 times across 57 turns, so it is not an exotic corner -- it is how nearly every story
    /// and hero mission reaches the player, and it is the only route with no lottery in it.
    ///
    /// Which makes it the deterministic way to put an author's mission in front of the player: an
    /// arc is an ordinary cooked package (`Arc_MainStory_A2Start` has 267 imports over 77 imported
    /// packages) and the missions it places are among those imports, so re-pointing one swaps what
    /// that turn places. Exactly the machinery a table row takeover already uses.
    /// </summary>
    /// <param name="ArcPackage">
    /// The override target. PACKAGE ONLY, for the reason on <see cref="Draw.TablePackage"/>.
    /// </param>
    /// <param name="MissionPackage">
    /// The mission this turn currently places -- what an author taking the slot would displace.
    /// Also package-only, because it is compared against a mission's own package name.
    /// </param>
    public sealed record Placement(string Arc, string ArcPackage, int Turn, string MissionPackage);

    /// <summary>
    /// Every mission the shipped arcs place directly. See <see cref="Placement"/>.
    ///
    /// GROUP BY MISSION, NOT BY ARC AND TURN. Nothing in the shipped campaign places one mission
    /// from two arcs -- all 20 placements are distinct -- but a mission that WERE placed twice has
    /// to have both arcs re-pointed or it still arrives from the other one, and grouping costs
    /// nothing while assuming uniqueness would fail silently.
    ///
    /// A "START" ARC IS NOT A PREFIX OF THE PLAIN ONE, though it was written up that way for an
    /// afternoon. `--props "Arc_MainStory_A2"` matches `Arc_MainStory_A2Start` as well -- the name
    /// is a prefix of the name -- so a dump of "A2" was two assets concatenated and A2 appeared to
    /// re-declare every turn A2Start did. A2 places ONE mission. Read a single package before
    /// claiming anything about how two of them relate.
    /// </summary>
    public static List<Placement> Placements(IFileProvider provider)
    {
        var found = new List<Placement>();
        foreach (var path in PackagesUnder(provider, "GameData/CampaignData"))
        {
            try
            {
                var exports = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(path)).GetExports().ToList());
                var arc = exports.FirstOrDefault(e =>
                    string.Equals(e.ExportType, "BrunoCampaignArc", StringComparison.Ordinal));
                if (arc is null) continue;

                var package = PackageId.PackageNameFor(path, ModBuild.ProjectName);
                if (package is null) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (PartDetails.Unwrap(Value(arc, "TurnsMap")) is not UScriptMap turns) continue;

                foreach (var kv in turns.Properties)
                {
                    if (!int.TryParse(Str(kv.Key?.GenericValue) ?? "", out var turn)) continue;
                    if (Struct(kv.Value?.GenericValue) is not { } perTurn) continue;
                    if (PartDetails.Unwrap(Value(perTurn.Properties, "Missions")) is not UScriptArray arr)
                        continue;

                    foreach (var item in arr.Properties)
                        if (PackageOf(ObjectIn(item?.GenericValue)) is { } mission)
                            found.Add(new Placement(name, package, turn, mission));
                }
            }
            catch (Exception) { /* an arc that will not parse places nothing */ }
        }
        // Campaign order, not alphabetical -- see the note on Schedule.
        var order = ArcOrder(provider);
        return found.OrderBy(p => order.TryGetValue(p.Arc, out var at) ? at : int.MaxValue)
                    .ThenBy(p => p.Turn).ToList();
    }

    /// <summary>
    /// One campaign turn, and what it already does.
    ///
    /// EVERY turn, including the empty ones -- which is the difference from <see cref="Placements"/>
    /// and the whole reason this exists. A turn that places nothing and pulls nothing is still a
    /// perfectly good place to ADD a mission, and it is the CHEAPEST one: nothing else happens on
    /// it, so the author's mission arrives alone. A picker built from Placements could never offer
    /// those, and they are the ones worth offering first.
    /// </summary>
    public sealed record Turn(string Arc, string ArcPackage, int Number,
                              int Missions, int Tables, int Recipes);

    /// <summary>Every turn of every campaign arc, in campaign order. See <see cref="Turn"/>.</summary>
    public static List<Turn> Turns(IFileProvider provider)
    {
        var found = new List<Turn>();
        foreach (var path in PackagesUnder(provider, "GameData/CampaignData"))
        {
            try
            {
                var exports = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(path)).GetExports().ToList());
                var arc = exports.FirstOrDefault(e =>
                    string.Equals(e.ExportType, "BrunoCampaignArc", StringComparison.Ordinal));
                if (arc is null) continue;

                var package = PackageId.PackageNameFor(path, ModBuild.ProjectName);
                if (package is null) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (PartDetails.Unwrap(Value(arc, "TurnsMap")) is not UScriptMap turns) continue;

                foreach (var kv in turns.Properties)
                {
                    if (!int.TryParse(Str(kv.Key?.GenericValue) ?? "", out var turn)) continue;
                    if (Struct(kv.Value?.GenericValue) is not { } perTurn) continue;
                    found.Add(new Turn(name, package, turn,
                                       Count(perTurn, "Missions"),
                                       Count(perTurn, "MissionTables"),
                                       Count(perTurn, "Recipes")));
                }
            }
            catch (Exception) { /* an arc that will not parse contributes no turns */ }
        }
        var order = ArcOrder(provider);
        return found.OrderBy(t => order.TryGetValue(t.Arc, out var at) ? at : int.MaxValue)
                    .ThenBy(t => t.Number).ToList();

        static int Count(FStructFallback s, string property) =>
            PartDetails.Unwrap(Value(s.Properties, property)) is UScriptArray a ? a.Properties.Count : 0;
    }

    /// <summary>Arc name -> its position in the campaign, from the campaign asset itself.</summary>
    public static Dictionary<string, int> ArcOrder(IFileProvider provider)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in PackagesUnder(provider, "GameData/CampaignData"))
        {
            if (!Path.GetFileNameWithoutExtension(path)
                     .StartsWith("Campaign_MainStory", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var exports = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(path)).GetExports().ToList());
                var campaign = exports.FirstOrDefault(e =>
                    string.Equals(e.ExportType, "BrunoCampaign", StringComparison.Ordinal));
                if (campaign is null) continue;

                var at = 0;
                foreach (var arc in Objects(campaign, "Arcs"))
                {
                    var leaf = arc.Contains('/') ? arc[(arc.LastIndexOf('/') + 1)..] : arc;
                    if (leaf.Contains('.')) leaf = leaf[..leaf.IndexOf('.')];
                    order[leaf] = at++;
                }
            }
            catch (Exception) { }
        }
        return order;
    }

    /// <summary>
    /// The factions that can actually be fought, with how many rosters each has.
    /// See <see cref="Faction"/> for why this is not the same as the tag list.
    /// </summary>
    public static List<Faction> Factions(IFileProvider provider)
    {
        var found = new List<Faction>();
        foreach (var path in PackagesUnder(provider, "GameData/CampaignData/Factions"))
        {
            try
            {
                var exports = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(path)).GetExports().ToList());
                var f = exports.FirstOrDefault(e =>
                    string.Equals(e.ExportType, "FactionData", StringComparison.Ordinal));
                if (f is null) continue;

                var tag = PartDetails.TagOf(f.Properties, "IdentifyingFactionTag");
                if (tag is not { Length: > 0 }) continue;

                found.Add(new Faction(tag, Path.GetFileNameWithoutExtension(path),
                                      Count(Value(f, "SpawnSets")),
                                      Count(Value(f, "CrisisEliteSpawnSets"))));
            }
            catch (Exception) { }
        }
        return found.OrderByDescending(f => f.SpawnSets)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

        static int Count(object? value) =>
            PartDetails.Unwrap(value) is UScriptArray arr ? arr.Properties.Count : 0;
    }

    /// <summary>Every spawn group, filed by faction folder. 365 ship.</summary>
    public static List<SpawnGroup> SpawnGroups(IFileProvider provider)
    {
        var found = new List<SpawnGroup>();
        foreach (var path in PackagesUnder(provider, SpawnFolder))
        {
            try
            {
                var exports = AssetLoader.Read(() =>
                    provider.LoadPackage(AssetLoader.PackagePath(path)).GetExports().ToList());
                var g = exports.FirstOrDefault(e =>
                    string.Equals(e.ExportType, "SpawnGroup", StringComparison.Ordinal));
                if (g is null) continue;

                var package = PackageId.PackageNameFor(path, ModBuild.ProjectName);
                if (package is null) continue;

                var entries = new List<SpawnEntry>();
                foreach (var e in StructsIn(Value(g.Properties, "SpawnCharacters")))
                    entries.Add(new SpawnEntry(
                        CharacterClass: ObjectIn(Value(e.Properties, "CharacterClass")) ?? "",
                        MinCount: (int) Num(Value(e.Properties, "MinCount")),
                        MaxCount: (int) Num(Value(e.Properties, "MaxCount")),
                        PullFromPool: Value(e.Properties, "bPullFromCharacterPool") is true,
                        TagsWhenSpawned: TagsIn(Value(e.Properties, "TagsToAddWhenSpawned"))));

                found.Add(new SpawnGroup(package, Path.GetFileNameWithoutExtension(path), entries));
            }
            catch (Exception) { }
        }
        return found.OrderBy(g => g.Leaf, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------------------ plumbing

    private static IEnumerable<string> PackagesUnder(IFileProvider provider, string folder) =>
        provider.Files.Keys
            .Where(p => p.Contains(folder, StringComparison.OrdinalIgnoreCase) &&
                        p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    private static object? Value(List<FPropertyTag>? props, string name) =>
        props?.FirstOrDefault(p => p.Name.Text == name)?.Tag?.GenericValue;

    private static object? Value(UObject o, string name) => Value(o.Properties, name);

    /// <summary>
    /// A struct property, both wrappers off.
    ///
    /// A StructProperty arrives as an FScriptStruct WRAPPING an FStructFallback, and testing only
    /// the outer one matches nothing while printing the struct's type name -- which reads exactly
    /// like an empty property. That cost a whole probe run on `--levelactors`.
    /// </summary>
    private static FStructFallback? Struct(UObject o, string name) => Struct(Value(o, name));

    private static FStructFallback? Struct(FStructFallback s, string name) =>
        Struct(Value(s.Properties, name));

    private static FStructFallback? Struct(object? value) => PartDetails.Unwrap(value) switch
    {
        FStructFallback f => f,
        _ => null,
    };

    private static IEnumerable<FStructFallback> StructsIn(object? value)
    {
        if (PartDetails.Unwrap(value) is not UScriptArray arr) yield break;
        foreach (var item in arr.Properties)
            if (Struct(item?.GenericValue) is { } s) yield return s;
    }

    private static double Num(object? v) => v switch
    {
        int i => i, float f => f, double d => d, long l => l, byte b => b, short sh => sh,
        _ => double.TryParse(v?.ToString(), out var n) ? n : 0,
    };

    private static int Int(UObject o, string name) => (int) Num(Value(o, name));
    private static bool Bool(UObject o, string name) => Value(o, name) is true;

    private static string? Str(object? v)
    {
        var s = PartDetails.Unwrap(v)?.ToString();
        return string.IsNullOrWhiteSpace(s) || s == "None" ? null : s;
    }

    /// <summary>
    /// The package an object reference points at, without the class prefix or the sub-object.
    ///
    /// `BrunoMissionData'/Game/.../SK_Brentaal_030.SK_Brentaal_030'` has to come back as the path
    /// alone, because that is what a redirect and a copy are keyed on.
    /// </summary>
    private static string? ObjectIn(object? value)
    {
        var s = PartDetails.Unwrap(value)?.ToString();
        if (string.IsNullOrWhiteSpace(s) || s == "None") return null;

        // Strip `Class'...'` if present.
        var quote = s.IndexOf('\'');
        if (quote >= 0)
        {
            var end = s.LastIndexOf('\'');
            if (end > quote) s = s[(quote + 1)..end];
        }
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// The texture a card draws, two structs down.
    ///
    /// `SmallImage` is a struct holding a `SoftBrush` struct, and the texture is a property INSIDE
    /// that -- `ResourceObject`, with `WeakResourceObject` beside it. Reading `SoftBrush` itself as
    /// an object reference returns the words "CUE4Parse.UE4.Assets.Objects.FStructFallback", which
    /// is not null, so every thumbnail read back as a successfully-read path that is nonsense.
    /// The same fault this file's own Struct() comment warns about, made one level further in.
    /// </summary>
    private static string? BrushTexture(FStructFallback image)
    {
        var brush = Struct(Value(image.Properties, "SoftBrush"));
        if (brush is null) return null;
        return ObjectIn(Value(brush.Properties, "ResourceObject"))
            ?? ObjectIn(Value(brush.Properties, "WeakResourceObject"));
    }

    /// <summary>
    /// `/Game/Path/Asset.Asset` -> `/Game/Path/Asset`, which is the form a package name takes.
    ///
    /// Null for anything that is not a real reference -- an empty FPackageIndex reads back as the
    /// string "0", which is not null and would otherwise enter the schedule as a table called 0.
    /// </summary>
    private static string? PackageOf(string? objectPath)
    {
        if (objectPath is not { Length: > 0 } || !objectPath.Contains('/')) return null;
        var cut = objectPath.LastIndexOf('.');
        var slash = objectPath.LastIndexOf('/');
        return cut > slash ? objectPath[..cut] : objectPath;
    }

    private static string? Text(UObject? o, string name) =>
        o is null ? null : Str(Value(o, name));

    private static List<string> Texts(UObject o, string name)
    {
        var found = new List<string>();
        if (PartDetails.Unwrap(Value(o, name)) is UScriptArray arr)
            foreach (var item in arr.Properties)
                if (Str(item?.GenericValue) is { } s) found.Add(s);
        return found;
    }

    private static List<string> Objects(UObject o, string name)
    {
        var found = new List<string>();
        if (PartDetails.Unwrap(Value(o, name)) is UScriptArray arr)
            foreach (var item in arr.Properties)
                if (ObjectIn(item?.GenericValue) is { } s) found.Add(s);
        return found;
    }

    private static List<string> Tags(UObject o, string name) => TagsIn(Value(o, name));

    /// <summary>
    /// The tags in a container, which is a struct holding an array -- not an array.
    /// </summary>
    private static List<string> TagsIn(object? value)
    {
        var found = new List<string>();
        var unwrapped = PartDetails.Unwrap(value);

        if (unwrapped is FGameplayTagContainer c)
        {
            foreach (var t in c.GameplayTags) found.Add(t.TagName.Text);
            return found;
        }
        if (unwrapped is UScriptArray arr)
        {
            foreach (var item in arr.Properties)
                if (PartDetails.TagOf(item?.GenericValue) is { } t) found.Add(t);
            return found;
        }
        if (unwrapped is FStructFallback s &&
            PartDetails.Unwrap(Value(s.Properties, "GameplayTags")) is UScriptArray inner)
            foreach (var item in inner.Properties)
                if (PartDetails.TagOf(item?.GenericValue) is { } t) found.Add(t);
        return found;
    }

    private static List<FactCondition> Conditions(object? value)
    {
        var found = new List<FactCondition>();
        foreach (var s in StructsIn(value))
            found.Add(new FactCondition(
                Tag: PartDetails.TagOf(s.Properties, "FactValueTag") ?? "",
                Operation: Str(Value(s.Properties, "ComparisonOperation")) ?? "",
                Value: Num(Value(s.Properties, "Value")),
                MissingIsZero: Value(s.Properties, "bTreatMissingFactAsZero") is true,
                OtherTag: PartDetails.TagOf(s.Properties, "OtherFactValueTag"),
                Min: Num(Value(s.Properties, "Min")),
                InclusiveMin: Value(s.Properties, "bInclusiveMin") is true,
                Max: Num(Value(s.Properties, "Max")),
                InclusiveMax: Value(s.Properties, "bInclusiveMax") is true));
        return found;
    }
}

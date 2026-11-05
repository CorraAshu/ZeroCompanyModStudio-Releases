namespace UEToolkit.Core;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// A MISSION THIS MOD ADDS: where it is fought, who you fight, what you do, and when it turns up.
///
/// BUILT BY DUPLICATING A SHIPPED ONE, never synthesised. A `BrunoMissionData` is a cooked asset
/// with an import table, three sub-objects and a map configuration Blueprint; writing one from
/// nothing means getting all of that right for no gain, when the game ships 125 of them and the
/// Studio has duplicated game assets since the first part. <see cref="From"/> is the one it starts
/// as, and everything below is what gets patched into the copy.
///
/// THE FIVE AXES, all data, all here:
///   1. WHERE   -- <see cref="Planet"/> (a row of a region location table) and <see cref="Level"/>
///   2. WHO     -- <see cref="Faction"/>, one of the game's twelve faction tags
///   3. WHAT    -- <see cref="Type"/>, the objective kind
///   4. HOW BIG -- squad sizes, lifespan
///   5. WHEN    -- <see cref="Tables"/>, a weighted row with fact gates
///
/// AXES 2 AND 5 ARE INDEPENDENT, and the shipped game never crosses them -- it aligns table with
/// faction by NAMING CONVENTION only. So "Separatist holdouts in the Empire era" is a
/// Separatist-faction mission whose row sits in `SK_Table_Imperial`, gated on a fact that only the
/// late campaign has raised. That is the author's stated goal and it needs no new machinery.
///
/// WHAT IS NOT HERE: the map. Objective actors, spawner placements and reinforcement pins live in
/// the level's `_Gameplay.umap`, and ADDING or MOVING one needs a cooked `.umap` written, which is
/// untested. But POINTING a mission at a shipped level is ordinary -- the game runs 125 missions on
/// 74 maps, and `SK_Kaller_030_Root` alone carries a Separatist Capture, an Imperial Rescue and an
/// Imperial Sabotage. So the map is a thing you choose, not a thing you have to make.
/// </summary>
public sealed class ModMission
{
    /// <summary>The asset name the mission ships as. Must not end in _digits -- see PackageId.</summary>
    public string Id = "";

    /// <summary>The shipped mission it is duplicated from, by full package name.</summary>
    public string From = "";

    /// <summary>
    /// CHANGE THE GAME'S OWN MISSION instead of shipping a new one beside it.
    ///
    /// The author's idea, and a better test than the one it replaces: "instead of trying to see if
    /// a new mission works, let's edit one i know i can see". A new mission has to be drawn from a
    /// weighted table before anyone can look at it, so a failure could be the mission, the table,
    /// the pull schedule or the save's position -- four suspects and a campaign turn per attempt.
    /// Editing a mission ALREADY ON THE MAP has one suspect and needs no turn at all.
    ///
    /// The build emits a <see cref="ModOverride"/> on <see cref="From"/> rather than a copy at a
    /// new id, so nothing new is added and no table is touched -- the mission keeps every row that
    /// already points at it. Proven by probe before this field existed: an override on
    /// `SK_Anaxes_045` carrying only a display name builds, verifies "read back clean" and ships
    /// `SK_Anaxes_045.uasset` at the game's own path.
    ///
    /// THE COST, worth knowing before choosing it: this replaces a mission the game ships. While
    /// the mod is installed that mission is yours, not theirs.
    /// </summary>
    public bool InPlace;

    // ----------------------------------------------------------------- what the player is told

    public string Display = "";
    public string ShortDescription = "";
    public string LongDescription = "";

    /// <summary>The card picture, a texture's object path. Blank keeps the donor's.</summary>
    public string Thumbnail = "";

    /// <summary>The image-bank tag the card falls back to when no texture is named.</summary>
    public string ImageBankTag = "";

    /// <summary>One line per objective, as the briefing lists them.</summary>
    public List<string> Objectives { get; } = new();

    // ----------------------------------------------------------------- where

    /// <summary>The planet, as a ROW NAME of <see cref="LocationTable"/>. Not free text.</summary>
    public string Planet = "";

    public string LocationTable = "";
    public string RegionTag = "";
    public string RegionAsset = "";

    /// <summary>The map, by object path. The one thing here that is not editable data.</summary>
    public string Level = "";

    // ----------------------------------------------------------------- who and what

    public string Faction = "";
    public string Type = "";

    /// <summary>`BitReactor.Design.MissionDifficulty.Medium` and anything beside it.</summary>
    public List<string> MissionTags { get; } = new();

    // ----------------------------------------------------------------- how much

    public int MinSquad = 4;
    public int MaxSquad = 4;

    /// <summary>Turns the mission stays on the galaxy map before it expires.</summary>
    public int LifeSpan = 4;

    public bool BlockActiveDuplicates = true;
    public bool BlockCompletedDuplicates = true;
    public string MapConfiguration = "";

    // ----------------------------------------------------------------- what you get

    public List<string> Rewards { get; } = new();
    public string RewardTable = "";
    public bool RewardPreRoll = true;

    /// <summary>
    /// The fact this mission raises on completion -- the whole sequencing mechanism.
    ///
    /// Mission A increments a fact; mission B's ROW requires it. That is how "appears after you
    /// finish that one" is expressed, and it is the same lever an era gate uses.
    /// </summary>
    public string FactTag = "";

    public int FactIncrement = 1;

    // ----------------------------------------------------------------- when it appears

    public List<ModMissionRow> Tables { get; } = new();

    /// <summary>
    /// CAMPAIGN TURN SLOTS THIS MISSION TAKES OVER -- the route with no lottery in it.
    ///
    /// Each entry is a SHIPPED mission's package name, one the campaign places directly via
    /// `TurnsMap[turn].Missions[]`. Taking the slot re-points the arc at this mission instead, so
    /// it arrives on that turn with no weight, no roll and no fact gate -- where a table row is
    /// one weighted candidate that may never be drawn at all.
    ///
    /// STORED AS THE MISSION, NOT THE ARC AND TURN, deliberately: the build looks up every arc that
    /// places the named mission and re-points all of them, so a mission placed twice cannot be
    /// half-taken. No shipped mission is placed twice, but assuming so would fail silently.
    ///
    /// THE COST, and it is real: the mission whose slot this takes is no longer offered while the
    /// mod is installed. Only two of the shipped 20 are ordinary optional skirmishes
    /// (`SK_Kaller_020`, `SK_Lothal_030`, both in A2Start); the rest are story or hero missions.
    /// </summary>
    public List<string> Placements { get; } = new();

    /// <summary>
    /// CAMPAIGN TURNS THIS MISSION IS ADDED TO -- guaranteed, and displacing nothing.
    ///
    /// The route the author asked for after being offered three that each cost something: "Wish
    /// there was a way to just make it appear without doing something like that". A weighted row
    /// is a roll and can lose; taking a turn slot re-points a shipped mission away; editing in
    /// place replaces an offer. This appends to the turn's own `Missions` array, so the turn
    /// places the game's missions AND this one, and it arrives when that turn runs.
    ///
    /// Each entry is `Arc|Turn` -- the arc's full package name, a pipe, and the arc's own turn key.
    /// A pipe because a package name holds slashes and dots and a turn key is an integer, so the
    /// two cannot be told apart by any separator that appears in either.
    /// </summary>
    public List<string> ArcAdds { get; } = new();

    /// <summary>Split an <see cref="ArcAdds"/> entry, or null when it is malformed.</summary>
    public static (string Arc, int Turn)? SplitArcAdd(string entry)
    {
        var bar = entry.LastIndexOf('|');
        if (bar <= 0 || !int.TryParse(entry[(bar + 1)..], out var turn)) return null;
        return (entry[..bar], turn);
    }

    /// <summary>Build an <see cref="ArcAdds"/> entry.</summary>
    public static string ArcAddEntry(string arcPackage, int turn) => $"{arcPackage}|{turn}";

    /// <summary>What the list shows: the name if it has one, else the id.</summary>
    public string Label => Display.Length > 0 ? Display : Id.Length > 0 ? Id : "(unnamed mission)";

    // ----------------------------------------------------------------- json

    private static string? Get(JsonObject o, string key) =>
        o[key]?.GetValueKind() == JsonValueKind.String ? o[key]!.GetValue<string>() : null;

    private static int GetInt(JsonObject o, string key, int fallback) =>
        o[key]?.GetValueKind() == JsonValueKind.Number ? o[key]!.GetValue<int>() : fallback;

    private static bool GetBool(JsonObject o, string key, bool fallback) => o[key]?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => fallback,
    };

    private static void ReadInto(JsonObject o, string key, List<string> into)
    {
        if (o[key] is not JsonArray arr) return;
        foreach (var node in arr)
            if (node?.GetValueKind() == JsonValueKind.String) into.Add(node.GetValue<string>());
    }

    public static ModMission From_(JsonObject o)
    {
        var m = new ModMission
        {
            Id = Get(o, "id") ?? "",
            From = Get(o, "from") ?? "",
            InPlace = GetBool(o, "editsInPlace", false),
            Display = Get(o, "display") ?? "",
            ShortDescription = Get(o, "short") ?? "",
            LongDescription = Get(o, "long") ?? "",
            Thumbnail = Get(o, "thumbnail") ?? "",
            ImageBankTag = Get(o, "imageBankTag") ?? "",
            Planet = Get(o, "planet") ?? "",
            LocationTable = Get(o, "locationTable") ?? "",
            RegionTag = Get(o, "regionTag") ?? "",
            RegionAsset = Get(o, "regionAsset") ?? "",
            Level = Get(o, "level") ?? "",
            Faction = Get(o, "faction") ?? "",
            Type = Get(o, "type") ?? "",
            MinSquad = GetInt(o, "minSquad", 4),
            MaxSquad = GetInt(o, "maxSquad", 4),
            LifeSpan = GetInt(o, "lifeSpan", 4),
            BlockActiveDuplicates = GetBool(o, "blockActiveDuplicates", true),
            BlockCompletedDuplicates = GetBool(o, "blockCompletedDuplicates", true),
            MapConfiguration = Get(o, "mapConfiguration") ?? "",
            RewardTable = Get(o, "rewardTable") ?? "",
            RewardPreRoll = GetBool(o, "rewardPreRoll", true),
            FactTag = Get(o, "factTag") ?? "",
            FactIncrement = GetInt(o, "factIncrement", 1),
        };
        ReadInto(o, "objectives", m.Objectives);
        ReadInto(o, "missionTags", m.MissionTags);
        ReadInto(o, "rewards", m.Rewards);
        ReadInto(o, "placements", m.Placements);
        ReadInto(o, "arcAdds", m.ArcAdds);

        if (o["tables"] is JsonArray tabs)
            foreach (var node in tabs)
                if (node is JsonObject t) m.Tables.Add(ModMissionRow.From_(t));
        return m;
    }

    public JsonObject To()
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["from"] = From,
            ["display"] = Display,
        };
        if (InPlace) o["editsInPlace"] = true;
        if (ShortDescription.Length > 0) o["short"] = ShortDescription;
        if (LongDescription.Length > 0) o["long"] = LongDescription;
        if (Thumbnail.Length > 0) o["thumbnail"] = Thumbnail;
        if (ImageBankTag.Length > 0) o["imageBankTag"] = ImageBankTag;

        o["planet"] = Planet;
        if (LocationTable.Length > 0) o["locationTable"] = LocationTable;
        if (RegionTag.Length > 0) o["regionTag"] = RegionTag;
        if (RegionAsset.Length > 0) o["regionAsset"] = RegionAsset;
        o["level"] = Level;
        o["faction"] = Faction;
        o["type"] = Type;

        o["minSquad"] = MinSquad;
        o["maxSquad"] = MaxSquad;
        o["lifeSpan"] = LifeSpan;
        o["blockActiveDuplicates"] = BlockActiveDuplicates;
        o["blockCompletedDuplicates"] = BlockCompletedDuplicates;
        if (MapConfiguration.Length > 0) o["mapConfiguration"] = MapConfiguration;

        if (RewardTable.Length > 0) o["rewardTable"] = RewardTable;
        o["rewardPreRoll"] = RewardPreRoll;
        if (FactTag.Length > 0) { o["factTag"] = FactTag; o["factIncrement"] = FactIncrement; }

        Put(o, "objectives", Objectives);
        Put(o, "missionTags", MissionTags);
        Put(o, "rewards", Rewards);
        Put(o, "placements", Placements);
        Put(o, "arcAdds", ArcAdds);

        if (Tables.Count > 0)
        {
            var tabs = new JsonArray();
            foreach (var t in Tables) tabs.Add(t.To());
            o["tables"] = tabs;
        }
        return o;

        static void Put(JsonObject into, string key, List<string> values)
        {
            if (values.Count == 0) return;
            var arr = new JsonArray();
            foreach (var v in values) arr.Add(v);
            into[key] = arr;
        }
    }
}

/// <summary>
/// One placement: which weighted table draws this mission, how likely it is, and what must
/// already be true for it to be offered.
///
/// <see cref="ReplaceRow"/> is the difference between the two routes and the reason it exists.
/// TAKING OVER A ROW is pure substitution -- the table keeps its row count and one row's mission
/// pointer is redirected, which is machinery this project has used since the first repaint. ADDING
/// a row grows an array inside a cooked asset, which is the thing nobody has proven yet. Both are
/// offered, taking over is the default, and a mission proved one way tells us whether the other is
/// even needed.
/// </summary>
public sealed class ModMissionRow
{
    /// <summary>The `MissionWeightedTable`, by full package name.</summary>
    public string Table = "";

    public double Weight = 1;

    /// <summary>
    /// Which existing row to take over, or -1 to ADD one.
    ///
    /// Taking over costs the game the mission that sat there, which is said plainly in the panel:
    /// this is how you put a mission in front of the player without writing to a cooked array.
    /// </summary>
    public int ReplaceRow = -1;

    /// <summary>Facts that must be present / absent for the row to be drawn.</summary>
    public List<string> RequireTags { get; } = new();
    public List<string> IgnoreTags { get; } = new();

    /// <summary>Numeric conditions -- "this fact is under 2", which is how the game paces itself.</summary>
    public List<ModFactCondition> FactValues { get; } = new();

    public static ModMissionRow From_(JsonObject o)
    {
        var r = new ModMissionRow
        {
            Table = o["table"]?.GetValueKind() == JsonValueKind.String ? o["table"]!.GetValue<string>() : "",
            Weight = o["weight"]?.GetValueKind() == JsonValueKind.Number ? o["weight"]!.GetValue<double>() : 1,
            ReplaceRow = o["replaceRow"]?.GetValueKind() == JsonValueKind.Number
                ? o["replaceRow"]!.GetValue<int>() : -1,
        };
        Fill(o, "requireTags", r.RequireTags);
        Fill(o, "ignoreTags", r.IgnoreTags);
        if (o["factValues"] is JsonArray fv)
            foreach (var node in fv)
                if (node is JsonObject f) r.FactValues.Add(ModFactCondition.From_(f));
        return r;

        static void Fill(JsonObject from, string key, List<string> into)
        {
            if (from[key] is not JsonArray arr) return;
            foreach (var node in arr)
                if (node?.GetValueKind() == JsonValueKind.String) into.Add(node.GetValue<string>());
        }
    }

    public JsonObject To()
    {
        var o = new JsonObject { ["table"] = Table, ["weight"] = Weight, ["replaceRow"] = ReplaceRow };
        if (RequireTags.Count > 0) { var a = new JsonArray(); foreach (var t in RequireTags) a.Add(t); o["requireTags"] = a; }
        if (IgnoreTags.Count > 0) { var a = new JsonArray(); foreach (var t in IgnoreTags) a.Add(t); o["ignoreTags"] = a; }
        if (FactValues.Count > 0) { var a = new JsonArray(); foreach (var f in FactValues) a.Add(f.To()); o["factValues"] = a; }
        return o;
    }
}

/// <summary>
/// "This fact is less than two." The shipped crisis table gates every one of its 26 rows this way,
/// and it is the mechanism behind both era gating and "after you finish that one".
/// </summary>
public sealed class ModFactCondition
{
    public string Tag = "";

    /// <summary>`LessThan`, `GreaterThanOrEqual`, ... -- the enum's own name, without its prefix.</summary>
    public string Operation = "LessThan";

    public double Value;

    /// <summary>A fact nobody has raised reads as zero rather than failing the test.</summary>
    public bool MissingIsZero;

    public static ModFactCondition From_(JsonObject o) => new()
    {
        Tag = o["tag"]?.GetValueKind() == JsonValueKind.String ? o["tag"]!.GetValue<string>() : "",
        Operation = o["op"]?.GetValueKind() == JsonValueKind.String ? o["op"]!.GetValue<string>() : "LessThan",
        Value = o["value"]?.GetValueKind() == JsonValueKind.Number ? o["value"]!.GetValue<double>() : 0,
        MissingIsZero = o["missingIsZero"]?.GetValueKind() == JsonValueKind.True,
    };

    public JsonObject To() => new()
    {
        ["tag"] = Tag,
        ["op"] = Operation,
        ["value"] = Value,
        ["missingIsZero"] = MissingIsZero,
    };
}

using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;

namespace UEToolkit.Core;

/// <summary>
/// The tile-atlas numbers, which are one studio's authoring facts rather than universal truths.
/// `TileSize` is already known not to be universal even within one game — one body atlas is
/// 512 — so they belong in the profile where a game can state its own or leave them alone.
/// </summary>
public sealed class AtlasSettings
{
    /// <summary>Off by default. See <see cref="GameProfile.Atlas"/> for why that is deliberate.</summary>
    public bool Enabled { get; set; }

    /// <summary>The usual authoring tile. A starting point, not a floor.</summary>
    public int TileSize { get; set; } = 1024;

    /// <summary>These atlases end at this V; the origin follows from the row count.</summary>
    public float VTop { get; set; } = 1f;

    /// <summary>Islands bleed a fraction past a tile edge; without this V=1.008 buys a row.</summary>
    public float Slack { get; set; } = 0.05f;

    /// <summary>Material parameter suffixes that name a packed mask rather than a colour map.</summary>
    public List<string> PackedMapSuffixes { get; set; } =
        new() { "CO", "COH", "ORM", "ORME", "ORH", "ORHG" };

    /// <summary>
    /// Texture path substrings that are ONE image, whatever their UVs imply. Forced flat.
    ///
    /// This is a stated fact rather than a derived one, and it has to be: **the grid is recorded
    /// nowhere in the game, from either direction** — a material dump finds no field naming one
    /// and the shipped definitions have no atlas data at all. Everything else here is
    /// inferred from UV extents, and for one texture that inference has no safe answer.
    ///
    /// The case is a 4096x2048 single continuous artwork — a command-post display, with its
    /// title text spanning the middle and corner brackets framing the whole
    /// image. It is graded **4x2** because ONE slot of 44 (U 0.993..1.748,
    /// V −0.478) reaches outside the 0..1 box and so defeats the "nobody addresses beyond 0..1"
    /// test that correctly flattens every comparable texture. The other 43 then sample an eighth
    /// of the picture.
    ///
    /// **Two derivable rules were tried against the population and both fail.** Dropping the
    /// single most extreme slot changes exactly one texture in 133, so it buys nothing elsewhere
    /// and risks the function that has already shipped three UV bugs. Excluding slots that
    /// STRADDLE a tile boundary — which that slot does — would exclude **833 of 1,824 slots**,
    /// among them two atlases confirmed correct in 3ds Max by the
    /// user. Straddling is normal in a real atlas; a part's island spans tiles.
    ///
    /// So the only evidence that separates this case is the PICTURE, and the code cannot see it.
    /// A one-line human observation is the honest mechanism, and it says so out loud rather than
    /// dressing itself up as a heuristic.
    /// </summary>
    public List<string> NotAnAtlas { get; set; } = new();
}

/// <summary>
/// Game-specific readers and conventions. **Every switch defaults OFF**, so a title gets nothing
/// game-shaped unless its profile asks. The probes that trigger these are name-based, and a
/// probe that fires on the wrong game produces a wrong RESULT, not an error.
///
/// What can live here: every constant, vocabulary list, name map and probe string. What cannot:
/// the format readers themselves. A profile can say "use that reader with these parameters"; it
/// cannot contain one.
///
/// **KEYS THIS CLASS HAS NO PROPERTY FOR ARE KEPT, NOT DROPPED.** The settings that belong to a
/// reader Core does not carry live in whatever registered module owns that reader (see
/// <see cref="GameModules"/>). A profile written for an earlier build, or for the multi-game
/// toolkit, still names them: they load into <see cref="Extra"/>, a module reads them with
/// <see cref="ExtraAs{T}"/>, and <see cref="GameProfile.Save"/> writes them back untouched.
/// </summary>
public sealed class ReaderSettings
{
    /// <summary>
    /// The game builds its character customization menu from parts listed in the asset registry
    /// (BitReactor's system: Zero Company). Turns on the Parts tab.
    ///
    /// Off by default like everything here, and for the usual reason: with it on, a game that
    /// has no such registry simply shows an empty tab, which reads as a broken feature rather
    /// than an inapplicable one.
    /// </summary>
    public bool CustomizationParts { get; set; }

    /// <summary>
    /// The Asset Manager primary type the parts are registered under. Matched instead of the
    /// class name because most parts are Blueprint subclasses, and a class-name match misses
    /// the majority of them. See <see cref="PartsIndex"/>.
    /// </summary>
    public string CustomizationPartType { get; set; } = "CustomizationPartDefinition";

    /// <summary>
    /// Every key of the "Readers" block this class has no property for, exactly as the file had it.
    /// Without it loading would DROP them and the next save would erase them; with it a profile
    /// round-trips whole, whichever application wrote it.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();

    /// <summary>
    /// The kept keys read as a settings object of the caller's own: only the properties of
    /// <typeparamref name="T"/> are taken, any it does not name are ignored, and a key the file
    /// lacks leaves the property at its default. The profile's OWN serializer options, not a copy of
    /// them (<see cref="GameProfile.JsonOpts"/>): same case-sensitivity, same number parsing, and the
    /// same enum-by-name reading, and a change to the profile's rules cannot leave this behind.
    /// </summary>
    public T ExtraAs<T>() where T : new()
    {
        if (Extra.Count == 0) return new T();
        return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(Extra, GameProfile.JsonOpts), GameProfile.JsonOpts) ?? new T();
    }

    /// <summary>Socket/bone name prefixes that are rigging furniture, not attachment points.</summary>
    public List<string> SkipSocketPrefixes { get; set; } = new() { "fx_", "garbage" };

    /// <summary>
    /// DCC default material names, which are empty authoring slots rather than real materials.
    /// Binding their fallback textures is worse than binding nothing — a vehicle's belt settled
    /// that — and they collide across unrelated assets, so nothing may be pooled by these names.
    /// </summary>
    public List<string> DccDefaultMaterials { get; set; } =
        new() { "lambert1", "standardSurface1", "standardSurface2", "Rubber___Soft1" };
}

/// <summary>
/// How to guess an asset's kind from its path. These are NAMING CONVENTIONS, not facts about
/// the engine. The defaults (`SK_`, `SM_`, `T_`, `MI`, `A_`, `BP_` ...) are the common Unreal
/// prefixes, which many projects follow and none is obliged to. Some games keep textures in `TXT/`
/// folders with `_C`/`_N`/`_RMA` suffixes and no prefix at all, so a `T_` rule finds nothing there
/// and the whole tree reads as "Data" — confidently and uselessly.
///
/// The index cannot afford the honest answer (opening 860,000 packages to read each export type),
/// so it guesses. A guess that is wrong should at least be correctable per game.
/// </summary>
public sealed class ClassifyRules
{
    public List<string> SkeletalMeshPrefixes { get; set; } = new() { "SK_" };
    public List<string> StaticMeshPrefixes { get; set; } = new() { "SM_" };
    public List<string> TexturePrefixes { get; set; } = new() { "T_" };
    public List<string> MaterialPrefixes { get; set; } = new() { "MI", "M_", "MM_" };
    public List<string> AnimationPrefixes { get; set; } = new() { "A_", "AS_" };
    public List<string> BlueprintPrefixes { get; set; } = new() { "BP_", "ABP_" };

    /// <summary>Path fragments, checked after prefixes and lower-cased.</summary>
    public List<string> TextureFolders { get; set; } = new() { "/textures/" };
    public List<string> MaterialFolders { get; set; } = new() { "/materials/" };
    public List<string> AnimationFolders { get; set; } = new() { "/animations/" };

    /// <summary>
    /// Folders whose contents are character rig assets. Some games' skeletal meshes carry no
    /// prefix at all — the hero is a bare file inside a `Rig/` folder — so without this there is
    /// nothing in the name to key on and every character mesh in the game reads as "Data".
    /// </summary>
    public List<string> SkeletalMeshFolders { get; set; } = new();

    /// <summary>
    /// Suffixes for the support assets that share a rig folder with the mesh — the Skeleton, the
    /// PhysicsAsset, the cloth solver. Checked BEFORE <see cref="SkeletalMeshFolders"/>, because
    /// the folder rule would otherwise claim all of them.
    /// </summary>
    public List<string> DataSuffixes { get; set; } = new();

    /// <summary>
    /// Prefixes for support assets that are not meshes, checked before
    /// <see cref="SkeletalMeshFolders"/> for the same reason as <see cref="DataSuffixes"/>.
    ///
    /// A folder rule is a blunt instrument: it claims everything a rig folder contains, and a rig
    /// folder contains far more than rigs. In one game's hero rig folder, 11 of the 16
    /// assets it labelled SkeletalMesh are data assets, bone masks, blend definitions and a curve.
    /// They cost an export nothing — they just fail — but in the UI they are the assets that look
    /// like meshes and do nothing when clicked, which reads as a broken viewport.
    /// </summary>
    public List<string> DataPrefixes { get; set; } = new();

    /// <summary>
    /// Fragments anywhere in the filename that mark a support asset. The bluntest of the three,
    /// so it needs evidence rather than a hunch before anything goes in it.
    ///
    /// It earns its place on one game's physics assets, which are named four ways — a
    /// `_PhysicsAsset` suffix, a bare `_Physics`, and two longer variants that end in a descriptive
    /// word — and a suffix rule catches 66 of 84. Checked against every package in the game's rig
    /// folders,
    /// "_Physics" anywhere in the name matches 288 assets and **all 288 are PhysicsAssets**, with
    /// no real mesh caught. Verify the same way before adding to this list: `--classes` prints the
    /// class each package declares, which is the answer rather than another guess.
    /// </summary>
    public List<string> DataContains { get; set; } = new();

    /// <summary>Filename suffixes that name a texture, for games that use no prefix.</summary>
    public List<string> TextureSuffixes { get; set; } = new();
}

/// <summary>
/// Everything needed to open one UE game. Serialisable so a game only has to be
/// configured once, and so support for a new title is a JSON file rather than code.
/// </summary>
/// <summary>
/// A small, representative export this game is checked against, so a change to SHARED code is
/// caught on every game rather than on whichever one was being worked on.
///
/// The profile system already isolates a game's DATA — naming rules, engine version, atlas
/// settings, paths. Nothing in one profile can reach another game's run. What is shared is the
/// CODE: one exporter, one UV atlas resolver, one material resolver, one PSK writer. Splitting
/// those per game would mean three toolkits and every bug fixed three times, so instead the
/// regression is made cheap to detect: pick a handful of assets per game, record what the export
/// says about them, and diff.
///
/// Keep <see cref="Filter"/> small and INTERESTING rather than broad. One character can cover a
/// UDIM atlas, variant materials and both mesh types; another a multi-part character whose
/// textures live outside its own folder. A filter that exports a thousand meshes is a guard
/// nobody runs.
/// </summary>
public sealed class RegressionCheck
{
    /// <summary>Path substring, exactly as `--exportall --filter` takes it.</summary>
    public string Filter { get; set; } = "";

    /// <summary>Include the texture pass. Off keeps a run fast; on covers texture decode too.</summary>
    public bool Textures { get; set; }

    /// <summary>
    /// A level to read as well, exactly as `--level` takes it, or null to skip.
    ///
    /// `--exportall` never touches <c>LevelExport</c>, so until this existed a level regression
    /// went entirely uncaught — and levels are the one output with four independent ways of
    /// coming out silently wrong (World Partition cells, HLOD proxies, instanced components,
    /// attach chains), each of which produces a scene that looks like art rather than a bug.
    ///
    /// Pick a SMALL level for the same reason <see cref="Filter"/> stays small. A15's cells hold
    /// 10,854 placements; a guard that takes ten minutes is a guard nobody runs.
    /// </summary>
    public string? Level { get; set; }

    /// <summary>
    /// More levels to read, for when ONE cannot cover the mechanisms that matter.
    ///
    /// **A guard that cannot produce a dirty run is not a guard.** The socket fix went in against
    /// two level baselines that between them held ZERO socket attachments, so `--regress` reported
    /// "7 games unchanged" about a change it could not have detected; the level named above was
    /// added for exactly that reason. Then the LevelInstancePivotOffset fix moved 4,003 placements
    /// in one sublevel and all three level baselines stayed silent again, because not one of them
    /// places a prefab carrying a pivot.
    ///
    /// So this list holds one level per MECHANISM rather than one per game. Keep each small, for
    /// the same reason <see cref="Filter"/> stays small.
    /// </summary>
    public List<string>? Levels { get; set; }

    /// <summary>Every level to read: <see cref="Level"/> first, then <see cref="Levels"/>.</summary>
    public IEnumerable<string> AllLevels()
    {
        if (Level is { Length: > 0 }) yield return Level;
        foreach (var l in Levels ?? []) if (l is { Length: > 0 }) yield return l;
    }

    /// <summary>
    /// "all" to export every LOD, as `--lods` takes it; null for the default first-LOD-only run.
    ///
    /// Worth setting on at least one game. The multi-LOD path has its own failure modes that a
    /// single-LOD export cannot reach -- sockets appended once per LOD until the last file held
    /// four copies and ten duplicate bone names, and a destination name rebuilt from the package
    /// path so the SMALLEST LOD was written over the base name. Both shipped in a 199 GB export
    /// while every baseline stayed green, because no baseline ever asked for more than one LOD.
    /// </summary>
    public string? Lods { get; set; }
}

public sealed class GameProfile
{
    public string Name { get; set; } = "Unnamed";

    /// <summary>What `--regress` exports for this game, or null to skip it.</summary>
    public RegressionCheck? Regression { get; set; }

    /// <summary>Directory containing the .pak/.utoc/.ucas files.</summary>
    public string PaksDirectory { get; set; } = "";


    /// <summary>Engine version, e.g. "GAME_UE5_5". Parsed into <see cref="EGame"/>.</summary>
    public string EngineVersion { get; set; } = nameof(EGame.GAME_UE5_5);

    /// <summary>Absolute path to a .usmap. Required for UE5 (unversioned properties).</summary>
    public string? MappingsFile { get; set; }

    /// <summary>Main AES key as 0x-prefixed hex, or null/empty when the game is unencrypted.</summary>
    public string? AesKey { get; set; }

    /// <summary>Extra AES keys by pak GUID, for games with per-chunk encryption.</summary>
    public Dictionary<string, string> ExtraAesKeys { get; set; } = new();

    public ETexturePlatform TexturePlatform { get; set; } = ETexturePlatform.DesktopMobile;

    /// <summary>Sub-paths to treat as "characters" when batch extracting.</summary>
    public List<string> CharacterPaths { get; set; } = new();

    /// <summary>Paths to skip entirely (mod paks, lipsync, localisation...).</summary>
    public List<string> ExcludePaths { get; set; } = new();

    /// <summary>
    /// The UDIM tile-atlas remap. **Off unless a profile turns it on**, deliberately.
    ///
    /// The tile atlas is one studio's own scheme for avoiding ten textures per model, not a UE5
    /// convention, and the remap is not merely inert elsewhere — it can fire on legitimately
    /// tiling UVs and silently squash them. A trim sheet spanning U 0..4 on a SQUARE texture
    /// yields cols = rows = 4, passes every guard, and gets its U divided by four: one tile of a
    /// sheet meant to repeat four times. The scheme escapes that only because its atlases keep V within
    /// about -1..1, so vertically-tiling maps fail the V check — its convention protecting us,
    /// not the algorithm being careful. UE4 trim sheets are exactly the case that breaks it.
    ///
    /// So the default is OFF and a game opts in. An omitted field then means "feature not enabled",
    /// which is visible, rather than "silently behaves like the first game", which is a
    /// plausible-looking wrong answer on someone else's game.
    /// </summary>
    public AtlasSettings Atlas { get; set; } = new();

    /// <summary>
    /// Which game-specific readers to run, and the names they probe for. All off by default —
    /// see <see cref="ReaderSettings"/>. Start a new game from a NEUTRAL profile, never from
    /// another game's: deriving from one inherits its behaviour silently.
    /// </summary>
    public ReaderSettings Readers { get; set; } = new();

    /// <summary>Naming conventions for guessing asset kinds. See <see cref="ClassifyRules"/>.</summary>
    public ClassifyRules Classify { get; set; } = new();

    [JsonIgnore]
    public EGame Game => Enum.TryParse<EGame>(EngineVersion, true, out var g) ? g : EGame.GAME_UE5_5;

    /// <summary>
    /// Whether this engine version cannot be read without a .usmap: UE5 defaults to unversioned
    /// properties, where a field has no name in the file and only the map can supply it. UE4 titles
    /// generally ship versioned ones and parse without. The SAME rule the mount report states
    /// ("*** NONE - assets will parse as garbage ***"), so the two cannot disagree about which
    /// profiles are in trouble.
    /// </summary>
    [JsonIgnore]
    public bool MappingsRequired => Game >= EGame.GAME_UE5_0;

    /// <summary>
    /// THE ONE QUESTION "CAN THIS PROFILE BE MOUNTED AND READ CORRECTLY?", asked by everything that
    /// has to choose between profiles: the Studio's first run (<see cref="GameSetup"/>) and the
    /// toolkit's profile lookup (AppState.FindProfile) both call this and nothing else.
    ///
    /// It holds when the game's folder is really there, and the mappings this profile NEEDS are:
    /// a usmap it names must exist, and one it does not name is only acceptable for an engine
    /// version that reads without one (<see cref="MappingsRequired"/>). Looser than that and a
    /// hand-edited profile with a path and no usmap mounts, reads everything, and is wrong
    /// everywhere with no error; stricter and a UE4 game with no usmap is turned away.
    ///
    /// This only JUDGES. <see cref="Validate"/> still throws where it always did, and a profile that
    /// fails this is not changed or rejected on that account -- a caller that has no better profile
    /// to offer carries on with it and lets the mount say what is wrong.
    /// </summary>
    public bool IsUsable(out string why)
    {
        if (string.IsNullOrWhiteSpace(PaksDirectory))
        { why = "this profile does not say where the game is"; return false; }
        if (!Directory.Exists(PaksDirectory))
        { why = $"there is no folder at {PaksDirectory}"; return false; }

        if (!string.IsNullOrWhiteSpace(MappingsFile))
        {
            if (!File.Exists(MappingsFile))
            { why = $"the mappings file {MappingsFile} is not there"; return false; }
        }
        else if (MappingsRequired)
        {
            why = $"it names no mappings file, and {EngineVersion} cannot be read without one";
            return false;
        }

        why = "";
        return true;
    }

    /// <summary>The same JSON <see cref="Save"/> writes, without writing it - so a caller can find
    /// out whether a file would change before touching the disk.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>
    /// Everything in the file this class has no property for -- kept, and written back out.
    ///
    /// Without this, Save() is a LOSSY rewrite: it serialises the known properties and silently
    /// discards the rest. Every profile here carries a hand-written `_Notes` block recording what
    /// was measured about that game and what will go wrong if a setting is off, and the first
    /// mount after saving became automatic deleted sixty lines of it. Nothing warned, because from
    /// the serialiser's point of view nothing was wrong.
    ///
    /// The general rule this is an instance of: code that reads a file, keeps part of it, and
    /// writes the whole thing back will throw away whatever it was not told to care about.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // Paths and usmap names are full of `+` and other characters the default encoder escapes
        // into +. Valid JSON, unreadable to a person editing the file by hand -- and these
        // files are edited by hand.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static GameProfile Load(string path) =>
        JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidDataException($"Could not parse profile: {path}");

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Fail early with a clear message rather than deep inside CUE4Parse.</summary>
    public void Validate()
    {
        if (!Directory.Exists(PaksDirectory))
            throw new DirectoryNotFoundException(string.IsNullOrWhiteSpace(PaksDirectory)
                // EMPTY IS A DIFFERENT FAULT FROM WRONG, and it printed the same: "Paks directory
                // not found:" with nothing after the colon, which tells somebody nothing at all.
                // An empty one means this profile was never filled in -- the Studio does that on
                // its first run -- so the thing to say is how to fill it in.
                ? "This copy has not been told where the game is. Open Zero Company Mod Studio " +
                  "once and let it find your game; it saves what it finds, and the Blender add-on " +
                  "and the command line then use the same answer."
                : $"Paks directory not found: {PaksDirectory}");
        if (!Enum.TryParse<EGame>(EngineVersion, true, out _))
            throw new InvalidDataException($"Unknown EngineVersion '{EngineVersion}'. Use a CUE4Parse EGame name, e.g. GAME_UE5_5.");
        if (!string.IsNullOrEmpty(MappingsFile) && !File.Exists(MappingsFile))
            throw new FileNotFoundException(
                $"Mappings file not found: {MappingsFile}\n" +
                "Without mappings a UE5 game parses as garbage BUT DOES NOT ERROR - refusing to continue.");
    }
}

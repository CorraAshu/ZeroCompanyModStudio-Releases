using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;

namespace UEToolkit.Core;

/// <summary>
/// Settings that belong to the USER, not to a game.
///
/// A <see cref="GameProfile"/> answers "where is this game and how is it encoded" — paks, engine
/// version, mappings, keys. None of that transfers. How you want assets WRITTEN does: a mesh
/// format, a texture format, an output folder and an LOD choice are the same whether the mesh came
/// from one game or another, and re-picking them per game is how two exports end up in different
/// formats without anyone deciding that.
///
/// So this is stored once, beside `profiles/` rather than inside one, and every game reads it.
/// It is deliberately NOT committed: it holds absolute paths that are true only on this machine.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Where exports go. Shared by the Export panel, the texture Export button and the CLI.</summary>
    public string OutputDirectory { get; set; } = "";

    public ExportTarget MeshFormat { get; set; } = ExportTarget.ActorX;

    /// <summary>
    /// `FirstLod` is the full-detail mesh; `AllLods` writes the game's reduced ones too. Not a
    /// quality dial in the usual direction — see <see cref="NaniteFormat"/>.
    /// </summary>
    public ELodFormat LodFormat { get; set; } = ELodFormat.FirstLod;

    /// <summary>
    /// `OnlyNaniteLOD` is source-density geometry. `OnlyNormalLODs` is the game's non-Nanite
    /// FALLBACK, which is a proxy shell rather than a reduced mesh — one character's head drops from
    /// 26,206 points to 124 — so it is a deliberate choice, not a smaller-is-better setting.
    /// </summary>
    public ENaniteMeshFormat NaniteFormat { get; set; } = ENaniteMeshFormat.OnlyNaniteLOD;

    /// <summary>
    /// TGA by default. CUE4Parse's PNG writer composites alpha into RGB, so a packed mask opens
    /// looking like the flattened result with the real channel data gone. PNG is far smaller and
    /// fine for plain colour maps; the trade is stated rather than hidden.
    /// </summary>
    public ETextureFormat TextureFormat { get; set; } = ETextureFormat.Tga;

    public bool ExportMaterials { get; set; } = true;
    public bool ExportMorphTargets { get; set; } = true;

    /// <summary>
    /// Ask the release page, once per launch, whether there is a newer build.
    ///
    /// ON by default and switchable, which is the honest arrangement for a request the user did
    /// not make. What it sends is nothing -- a GET to a public API that answers the same for
    /// everybody -- and what it does with the answer is put a line in the menu bar. It is here
    /// rather than hard-wired because "this tool talks to the internet" is a thing some people
    /// want to be able to say no to without having to trust that it does not matter.
    ///
    /// See <see cref="UpdateCheck"/>. A build whose Home is not a GitHub repository ignores this
    /// entirely; there is nothing to ask.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Serialise mesh conversion against every other provider read. Default ON: measured at no
    /// throughput cost (warm-run difference under 1.5% either way across two workloads) and it
    /// closes the export non-determinism. See <see cref="AssetExporter.SerializeConversion"/>.
    /// </summary>
    public bool SerializeConversion { get; set; } = true;

    /// <summary>
    /// Mods this installation made -- by container name, e.g. "ArfHelmet_P".
    ///
    /// It lives here, in the USER's settings, rather than in the mod, and that is the whole point.
    /// Anything written into a mod folder travels with a copy of that folder, so a marker there
    /// would say "mine" on everybody's machine. A record kept on the side says only what THIS
    /// installation actually did.
    ///
    /// It is not a security boundary and is not meant to be one -- see <see cref="ModOwnership"/>.
    /// Someone determined enough to hand-edit this file was always going to use FModel instead.
    /// </summary>
    public List<string> MyMods { get; set; } = new();

    /// <summary>
    /// Mods whose author has already been told what shipping one does and does not protect.
    ///
    /// Kept so the note appears once per mod rather than on every build. It is a note and not a
    /// setting: there is nothing to turn on, and offering a switch would be offering a promise
    /// this cannot keep.
    /// </summary>
    public List<string> NotedMods { get; set; } = new();

    [JsonIgnore] public string? LoadedFrom { get; private set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Beside `profiles/`, so the app and every headless command agree. NOT under
    /// `AppContext.BaseDirectory`: that is the bin folder, which would give Debug and Release
    /// separate settings and make "I changed it and the export ignored me" a real bug report.
    ///
    /// <paramref name="createDirIfMissing"/> is for the WRITER. A program with no `profiles`
    /// folder at or above it (an unzipped download whose folder was tidied, a copy of the program
    /// on its own) used to answer null here and so could never save a setting at all -- the
    /// parameter existed and was ignored. With it, the folder is made beside the program, which is
    /// where the next <see cref="Load"/> looks first. A reader never creates anything.
    /// </summary>
    public static string? FindPath(bool createDirIfMissing = false)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var profiles = Path.Combine(dir.FullName, "profiles");
            if (Directory.Exists(profiles)) return Path.Combine(profiles, "settings.json");
        }
        if (!createDirIfMissing) return null;
        try
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "profiles");
            Directory.CreateDirectory(beside);
            return Path.Combine(beside, "settings.json");
        }
        catch (Exception) { return null; }   // read-only location: the caller reports that nothing was saved
    }

    public static AppSettings Load(Action<string>? log = null)
    {
        var path = FindPath();
        if (path is null || !File.Exists(path)) return new AppSettings();
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json)
                         ?? new AppSettings();
            loaded.LoadedFrom = path;
            return loaded;
        }
        catch (Exception e)
        {
            // Never let a malformed settings file stop the app starting; say so and use defaults.
            log?.Invoke($"settings: {Path.GetFileName(path)} unreadable ({e.Message}) - using defaults");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Why the last <see cref="Save"/> wrote nothing, in words a person can read; null when it
    /// worked (or none has run). Save returns null for two different things -- no place to keep the
    /// file, and a write that failed (a read-only or protected folder) -- and a window that told
    /// somebody "the folder was not found" about the second was telling them something false.
    /// </summary>
    [JsonIgnore] public string? SaveFailure { get; private set; }

    public string? Save(Action<string>? log = null)
    {
        SaveFailure = null;
        var path = FindPath(true);
        if (path is null)
        {
            SaveFailure = "the settings folder could not be found or created";
            log?.Invoke("settings: no profiles folder found or made, nothing saved");
            return null;
        }
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            LoadedFrom = path;
            return path;
        }
        catch (Exception e)
        {
            SaveFailure = e.Message;
            log?.Invoke($"settings: could not save - {e.Message}");
            return null;
        }
    }

    /// <summary>Build an export request from these settings, for one output directory.</summary>
    public ExportRequest ToRequest(string? outputDirectory = null) => new(
        MeshFormat,
        outputDirectory ?? OutputDirectory,
        TextureFormat: TextureFormat,
        ExportMaterials: ExportMaterials,
        ExportMorphTargets: ExportMorphTargets,
        LodFormat: LodFormat,
        NaniteFormat: NaniteFormat);
}

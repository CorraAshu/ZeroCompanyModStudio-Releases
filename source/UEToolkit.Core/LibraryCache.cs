using System.Text.Json;
using System.Text.Json.Serialization;

namespace UEToolkit.Core;

/// <summary>
/// Disk cache for the animation and character libraries. Building them means loading
/// hundreds of packages (~2 minutes on a large game), which is far too slow to repeat on every
/// launch.
/// </summary>
public static class LibraryCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record CachePayload(
        string Signature,
        int Version,
        List<AnimEntry> Animations,
        List<CharacterModel> Characters);

    /// <summary>Bump when the cached record shapes change.</summary>
    private const int SchemaVersion = 2;

    /// <summary>
    /// Identifies the game build AND the code that produced the cache. File count plus
    /// profile identity notices a patch or a different game without hashing 50 GB; the build
    /// identity notices that the resolution heuristics themselves changed, which the file
    /// count cannot. Without it a rebuild silently keeps serving results from the old rules.
    ///
    /// The lists are not all produced by Core: a registered <see cref="IGameModule"/> reads
    /// characters with code of its own, and the animation list the toolkit caches comes out of that
    /// same assembly. The modules' assemblies are folded in too, or a change there would leave the
    /// old results in force. Read at call time, not once, because modules are registered after this
    /// class could first be touched; with none registered (the Studio, the Level Editor) nothing is
    /// added and the string is Core's alone.
    /// </summary>
    public static string Signature(GameProfile profile, int fileCount) =>
        $"{profile.Name}|{profile.PaksDirectory}|{profile.EngineVersion}|{fileCount}|{BuildId}{ModulesBuildId()}";

    private static readonly string BuildId =
        typeof(CharacterLibrary).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    private static string ModulesBuildId() =>
        string.Join("", GameModules.All.Select(m => m.GetType().Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8]));

    private static string PathFor(string dataDir) => Path.Combine(dataDir, "libraries.json");

    public static bool TryLoad(string dataDir, string signature,
        out List<AnimEntry> anims, out List<CharacterModel> characters)
    {
        anims = [];
        characters = [];
        try
        {
            var path = PathFor(dataDir);
            if (!File.Exists(path)) return false;

            var payload = JsonSerializer.Deserialize<CachePayload>(File.ReadAllText(path), Options);
            if (payload is null || payload.Version != SchemaVersion || payload.Signature != signature)
                return false;

            anims = payload.Animations;
            characters = payload.Characters;
            return anims.Count > 0 || characters.Count > 0;
        }
        catch { return false; }   // a corrupt cache should just rebuild, never throw
    }

    public static void Save(string dataDir, string signature,
        IReadOnlyList<AnimEntry> anims, IReadOnlyList<CharacterModel> characters)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var payload = new CachePayload(signature, SchemaVersion,
                anims.ToList(), characters.ToList());
            File.WriteAllText(PathFor(dataDir), JsonSerializer.Serialize(payload, Options));
        }
        catch { /* caching is an optimisation; failing to write it is not an error */ }
    }

    public static void Clear(string dataDir)
    {
        try { File.Delete(PathFor(dataDir)); } catch { }
    }
}

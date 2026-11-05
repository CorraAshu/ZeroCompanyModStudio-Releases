using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UEToolkit.Core;

/// <summary>
/// WHAT THE INSTALLED COPY OF A MOD WAS BUILT FROM, so "you have edited since you built" can be
/// answered by CONTENT rather than by a file's clock.
///
/// Reported by a user of the released tool: "my mods are perpetually telling me this though. The
/// installed version is the only version I've built" -- with the mod working correctly in game and
/// his changes visible. He was right on every count.
///
/// The check compared the project file's LAST WRITE TIME against the newest container in the
/// installed folder, and a timestamp is a poor stand-in for the question. Two things move it
/// without the mod's content changing at all:
///
///   * <c>SaveMod</c> writes unconditionally and is called from about a dozen UI actions, so
///     working in the panel rewrites the file whether or not anything is different.
///   * Some projects are rewritten merely by being opened, because their text differs from what
///     today's serialiser emits. MEASURED on the author's own `CloneHead.json`: the file holds
///     `game's` where the serialiser writes `game\\u0027s`, and has no `parts` key where the
///     serialiser writes `"parts": []`. Anything written by an older version can do this.
///
/// Either way the file's clock jumps past the install and the warning never clears again -- which
/// is exactly "some of my mods but not others", because it depends on what is inside each project
/// and which ones have been opened.
///
/// So the install records a HASH of the project's canonical text, and the check compares that. A
/// cosmetic rewrite hashes the same and says nothing; a real edit hashes differently and says so.
///
/// **IN THE STUDIO'S OWN SETTINGS FOLDER**, keyed by project path, on the author's instruction.
/// The two alternatives were worse in specific ways: beside the project puts a file in the folder
/// somebody keeps their work in, and inside the installed mod would travel into the zip that
/// "Zip it for sharing" hands to strangers.
///
/// Every failure is silence, and the caller reads that as "cannot say" and falls back to the old
/// timestamp rule. A staleness notice that cannot be computed is not worth an error.
/// </summary>
public static class BuiltFrom
{
    private const string FileName = "built-from.json";

    /// <summary>The canonical text's hash, short enough to read in the file and long enough to trust.</summary>
    public static string HashOf(string projectText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectText)))[..16];

    /// <summary>
    /// Remember what was just installed.
    ///
    /// <paramref name="projectText"/> is the project SERIALISED, not the bytes on disk -- the whole
    /// point is to be blind to how the file happens to be written.
    /// </summary>
    public static void Record(string dataDirectory, string projectPath, string projectText)
    {
        try
        {
            var all = Read(dataDirectory);
            all[Key(projectPath)] = HashOf(projectText);

            Directory.CreateDirectory(dataDirectory);
            var o = new JsonObject();
            foreach (var (k, v) in all) o[k] = v;
            File.WriteAllText(Path.Combine(dataDirectory, FileName),
                              o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* a note about staleness is not worth failing an install over */ }
    }

    /// <summary>What the installed copy of this project was built from, or null if nothing was recorded.</summary>
    public static string? Recorded(string dataDirectory, string projectPath)
    {
        try { return Read(dataDirectory).GetValueOrDefault(Key(projectPath)); }
        catch (Exception) { return null; }
    }

    /// <summary>Forget one, so removing a mod does not leave an answer about a mod that is gone.</summary>
    public static void Forget(string dataDirectory, string projectPath)
    {
        try
        {
            var all = Read(dataDirectory);
            if (!all.Remove(Key(projectPath))) return;

            var o = new JsonObject();
            foreach (var (k, v) in all) o[k] = v;
            File.WriteAllText(Path.Combine(dataDirectory, FileName),
                              o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }

    private static Dictionary<string, string> Read(string dataDirectory)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return found;

        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return found;
        foreach (var (k, v) in o)
            if (v?.GetValue<string>() is { Length: > 0 } hash) found[k] = hash;
        return found;
    }

    /// <summary>
    /// The full path, normalised so the same project is the same key however it was reached.
    ///
    /// Case-insensitive and separator-agnostic because Windows is, and a project opened once
    /// through a mapped drive and once through its real path is one project.
    /// </summary>
    private static string Key(string projectPath)
    {
        try { return Path.GetFullPath(projectPath).Replace('/', '\\').ToLowerInvariant(); }
        catch (Exception) { return projectPath.Replace('/', '\\').ToLowerInvariant(); }
    }
}

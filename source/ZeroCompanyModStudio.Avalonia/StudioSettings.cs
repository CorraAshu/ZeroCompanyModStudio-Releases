namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// The few things this application has to remember between runs.
///
/// IN THE USER'S APPLICATION DATA, NOT BESIDE THE EXECUTABLE. The ImGui Studio keeps its
/// `modfolder.txt` under its own `bin/.../settings/`, which has two consequences worth avoiding
/// here: a rebuild into a different configuration cannot see it, and `dotnet clean` -- which
/// `publish.ps1` runs on every publish -- deletes it outright. A person who has told the tool
/// where their mods live should not have to tell it again because somebody cut a release.
///
/// Per user rather than per machine, because the answer is "where do I keep MY mods".
/// </summary>
internal static class StudioSettings
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zero Company Mod Studio");

    private static string ModFolderFile => Path.Combine(Dir, "modfolder.txt");

    /// <summary>
    /// WHERE 1.6.1 KEPT THE SAME ANSWER: `settings\modfolder.txt` beside the program (its DataDir). Somebody
    /// who unzips 2.0 over a 1.6.1 folder has told the tool where their mods live already; asking the per-user
    /// file alone sent them to an empty Documents folder with "0 mod projects" (final review I-2).
    /// </summary>
    private static string LegacyModFolderFile => Path.Combine(AppContext.BaseDirectory, "settings", "modfolder.txt");

    /// <summary>
    /// Where this application keeps what it remembers between runs.
    ///
    /// The same folder the settings live in, because that is what it IS — the ImGui studio calls
    /// its equivalent DataDir and BuiltFrom takes one, so a record written by either is read by
    /// either rather than each keeping its own idea of what was last built.
    /// </summary>
    public static string DataDir
    {
        get
        {
            try { Directory.CreateDirectory(Dir); } catch (Exception) { /* use it anyway */ }
            return Dir;
        }
    }

    /// <summary>
    /// Where this person keeps their mods, or null when they have not said.
    ///
    /// Null rather than the default, so a caller can tell "never asked" from "asked, and it is
    /// the default" -- the first is worth prompting about and the second is not.
    /// </summary>
    public static string? ModFolder() => ModFolder(ModFolderFile, LegacyModFolderFile);

    /// <summary>
    /// The same question with both places given, so a test can ask it without touching a real setting.
    ///
    /// The per-user file, when it exists, is the answer (it is what Change... writes, and it is newer than
    /// anything 1.6.1 left behind). When it does not, the 1.6.1 file beside the program is read, and what it
    /// says is CARRIED INTO the per-user file, so from then on there is one place and Change... works as it
    /// always has. A folder named there that has since gone is returned like any other: the caller creates
    /// or lists it exactly as it does for a per-user answer that has gone.
    /// </summary>
    internal static string? ModFolder(string perUserFile, string legacyFile)
    {
        try
        {
            if (File.Exists(perUserFile)) return Read(perUserFile);

            var old = Read(legacyFile);
            if (old is null) return null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(perUserFile)!);
                File.WriteAllText(perUserFile, old);
            }
            catch (Exception) { /* the answer still stands; it is read from there again next time */ }
            return old;
        }
        catch (Exception) { return null; }

        static string? Read(string file)
        {
            if (!File.Exists(file)) return null;
            var saved = File.ReadAllText(file).Trim();
            return saved.Length > 0 ? saved : null;
        }
    }

    public static void SetModFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ModFolderFile, folder);
        }
        catch (Exception) { /* not worth interrupting anyone over; it will ask again next time */ }
    }
}

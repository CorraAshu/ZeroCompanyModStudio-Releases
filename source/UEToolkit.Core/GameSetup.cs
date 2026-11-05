using System.Text.Json;

namespace UEToolkit.Core;

/// <summary>
/// FIRST-RUN GAME DISCOVERY: from "a program that was just unzipped" to "a profile that names the
/// game", with no window anywhere in it.
///
/// WHY THIS EXISTS. `profiles/zero-company.json` ships with an empty PaksDirectory on purpose, so
/// publishing cannot leak the machine that built it. The filled-in copy is made on the user's
/// machine, at first run, and written to `settings/active-profile.json` beside the program. The
/// ImGui Studio did that in `Startup.Mount`; the Avalonia Studio shipped in 2.0.0's first artifact
/// without it, and on every machine but the author's it opened with "This copy has not been told
/// where the game is". Everything that reads the answer afterwards -- the Studio, the Blender
/// add-on through `ZeroCompanyModStudio.exe --profile zero-company --catalog`, the command line -- reads the files
/// this writes, so their NAMES, PLACES and SHAPES are the old Studio's, not a new design:
///
///   settings/install.json          {"Install":"...\\Star Wars Zero Company"}
///   settings/active-profile.json   profile-template.json with PaksDirectory + MappingsFile filled in
///
/// "FIND IT" IS SEPARATE FROM "ASK THE USER". <see cref="Resolve"/> finds, remembers and builds
/// without ever showing anything, and says when it cannot (<see cref="Way.NeedsAnswer"/>); the
/// window that asks lives in the application and ends by calling <see cref="Use"/>. That split is
/// what lets a headless entry point -- the add-on's `--catalog` / `--import1` against the Studio's
/// own exe -- ask the same question without building a window.
///
/// THE ORDER IT TRIES THINGS IN, and each step is the answer only if it is really usable (see
/// <see cref="GameProfile.IsUsable"/>: the folder exists AND the usmap file exists):
///   1. a named profile in `profiles/` that names a game      (a source checkout; nothing is written)
///   2. `settings/install.json`                               (the second run, and every UPGRADER:
///                                                              1.6.x wrote it; the profile is rebuilt
///                                                              from it so the newest bundled usmap wins)
///   3. a `settings/active-profile.json` on its own           (an upgrader whose install note is gone)
///   4. otherwise somebody has to say, and <see cref="Way.NeedsAnswer"/> comes back with what Steam's
///      own library list found (<see cref="GameFinder"/>) to offer as the answer: one, several or none.
///      One install found is OFFERED, never used behind anyone's back - the author's decision, and
///      what the ImGui Studio's `Startup.Begin` always did. Steps 1-3 stay silent, because an
///      answer that still works needs no question.
///
/// AN ANSWER THAT NO LONGER WORKS IS ASKED ABOUT, NOT REPLACED. If `settings/install.json` names a
/// game that is gone (an unplugged drive, a moved folder), the answer is step 4 with
/// <see cref="Outcome.Gone"/> set, and `install.json` stays exactly as it was until a person says
/// otherwise. Steam finding a different install is not permission to overwrite what they chose.
/// </summary>
public static class GameSetup
{
    public const string NamedProfile = "zero-company.json";
    public const string TemplateFile = "profile-template.json";
    public const string ActiveFile = "active-profile.json";
    public const string InstallFile = "install.json";

    /// <summary>Beside the program, not in AppData: someone who wants to start over deletes a
    /// folder they can see, and moving the tool takes its settings with it. The old Studio's rule.</summary>
    public static string SettingsDir(string? appDir = null)
        => Path.Combine(appDir ?? AppContext.BaseDirectory, "settings");

    public enum Way
    {
        /// <summary>`profiles/zero-company.json` names a game (a source checkout). Nothing was written.</summary>
        NamedProfile,
        /// <summary>`settings/install.json` was still true; the profile was rebuilt from it.</summary>
        Remembered,
        /// <summary>No install note, but `settings/active-profile.json` names a game that is there.</summary>
        SavedProfile,
        /// <summary>The user answered (<see cref="Use"/>).</summary>
        Chosen,
        /// <summary>Nothing usable is remembered: somebody has to say. Candidates holds what was
        /// found to offer (one, several or none), and Gone the remembered install that went missing.</summary>
        NeedsAnswer,
        /// <summary>An install is known but the profile cannot be made (no template, no usmap, ...).</summary>
        Failed,
    }

    /// <param name="ProfilePath">The profile to mount, or null when there is none (see Way).</param>
    /// <param name="Install">The install folder in use, when one is known; used for the startup picture.</param>
    /// <param name="Say">One line for the log: what was decided and why, or what is wrong.</param>
    /// <param name="Candidates">For <see cref="Way.NeedsAnswer"/>: every install found, possibly none,
    /// best first - the first is what the question offers as its answer.</param>
    /// <param name="Gone">For <see cref="Way.NeedsAnswer"/>: where the game WAS, when something
    /// remembered it and it is no longer there; else null (a first run).</param>
    /// <param name="Problem">For <see cref="Way.NeedsAnswer"/>: what went wrong while looking (the
    /// finder threw), to be shown beside the question; else null.</param>
    public sealed record Outcome(Way Way, string? ProfilePath, string? Install, string Say,
                                 IReadOnlyList<string> Candidates, string? Gone = null,
                                 string? Problem = null)
    {
        public bool Ready => ProfilePath is not null;
        public bool NeedsAnswer => Way == Way.NeedsAnswer;

        /// <summary>The folder to take the game's splash from: the one in use, else the best guess.</summary>
        public string? PictureFrom => Install ?? Candidates.FirstOrDefault();
    }

    private static readonly IReadOnlyList<string> None = Array.Empty<string>();

    private sealed record Remembered(string Install);

    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true };

    // ------------------------------------------------------------------ finding

    /// <summary>
    /// Everything short of asking. Safe to call as often as you like: when nothing has changed it
    /// writes nothing (not even a new timestamp), so a second start is the same file as the first.
    /// </summary>
    /// <param name="appDir">The folder the program runs from; defaults to the real one.</param>
    /// <param name="find">Where installs come from. Tests and the dialog's photographs pass their own.</param>
    public static Outcome Resolve(string? appDir = null, Func<List<string>>? find = null, string program = StudioName)
    {
        appDir ??= AppContext.BaseDirectory;
        var settings = SettingsDir(appDir);

        // 1. A profile in profiles/ that really names a game. In a source checkout that is the
        // author's own; in a download it is the SHIPPED one, which is blank and so is skipped.
        if (FindNamed(appDir) is { } named && TryLoad(named) is { } np && np.IsUsable(out _))
            return new(Way.NamedProfile, named, GameFinder.InstallOf(np.PaksDirectory),
                       $"game: using profile {named}", None);

        // 2. What an earlier run (this one's, or the ImGui Studio's) remembered.
        var note = ReadNote(Path.Combine(settings, InstallFile));
        if (note is not null && Alive(note) is { } known)
        {
            var profile = Build(appDir, known, program, out var why);
            if (profile is not null)
                return new(Way.Remembered, profile, known,
                           $"game: {known} (remembered in settings\\{InstallFile})", None);

            // The install is there but the profile could not be rebuilt (a template or usmap
            // went missing). A profile that was already saved and still works is better than none.
            if (SavedProfile(appDir, settings) is { } keep)
                return keep with { Say = $"game: using the saved profile ({why})" };
            return new(Way.Failed, null, known, why!, None);
        }

        // 3. A saved profile with no install note beside it (or one whose note has gone stale).
        if (SavedProfile(appDir, settings) is { } saved) return saved;

        // 4. Nothing remembered works, so somebody has to say - and what is offered is whatever
        // Steam's list holds, one or several or nothing. Never used silently: even exactly one is
        // a question with its answer filled in, the way the ImGui Studio always asked it.
        // Judged by the same strict test Use() applies, so a candidate that Use would refuse is
        // not offered in the first place.
        var found = Installs(find, out var trouble);

        // WHERE THE GAME WAS, if something remembered it. Not replaced here: the note stays as it
        // is until Use() writes a new one, so a drive that was only unplugged is found again as soon
        // as it is plugged in, and nothing is lost by having looked.
        var gone = note ?? GoneFromSavedProfile(settings);

        string say;
        if (gone is not null)
            say = $"game: the install remembered before ({gone}) is no longer there" +
                  (found.Count == 0 ? " and no other was found"
                   : found.Count == 1 ? $" - found {found[0]}, to be confirmed"
                   : $" - {found.Count} installs found, which one is played?");
        else
            say = found.Count == 0 ? "game: no Zero Company install found on this machine"
                : found.Count == 1 ? $"game: found {found[0]}, to be confirmed"
                : $"game: {found.Count} installs found - which one is played?";
        if (trouble is not null) say += $" (looking for it failed: {trouble})";

        return new(Way.NeedsAnswer, null, null, say, found, gone, trouble);
    }

    /// <summary>
    /// Every Zero Company install the finder knows of, best first, each judged by the strict
    /// <see cref="GameFinder.IsInstall"/>. A finder that throws is an empty list plus the reason in
    /// <paramref name="problem"/> - the question is still asked, with the reason beside it, rather
    /// than the whole start failing with no window.
    /// </summary>
    public static List<string> Installs(Func<List<string>>? find, out string? problem)
    {
        problem = null;
        try { return (find ?? GameFinder.Find)().Where(GameFinder.IsInstall).ToList(); }
        catch (Exception e) { problem = e.Message; return new List<string>(); }
    }

    /// <summary>
    /// The user said where the game is. Checks it, builds the profile, remembers the install. The
    /// answer is judged on what has to be THERE rather than on the folder's name, and a path inside
    /// the install (the project, Paks) is accepted and widened to the install so mod folders
    /// beside Content stay visible. Nothing is written unless all of it can be.
    /// </summary>
    public static Outcome Use(string? appDir, string install, string program = StudioName)
    {
        appDir ??= AppContext.BaseDirectory;
        var typed = (install ?? "").Trim().Trim('"');
        if (typed.Length == 0 || !Directory.Exists(typed))
            return new(Way.Failed, null, null, $"There is no folder at {typed}.", None);

        var root = GameFinder.InstallOf(typed) ?? Path.TrimEndingDirectorySeparator(Path.GetFullPath(typed));
        if (!GameFinder.IsInstall(root))
            return new(Way.Failed, null, root, $"No game data under {typed}.", None);

        // BEFORE building anything: a folder the Studio cannot write to (Program Files) would fail
        // at the first file, and "Access to the path ... is denied" tells nobody what to do.
        if (WriteProblem(appDir, program) is { } cannotWrite)
            return new(Way.Failed, null, root, cannotWrite, None);

        // THE NOTE FIRST. install.json is what the next start believes, and it outranks the profile
        // (the profile is rebuilt from it), so a new profile written beside an old note is silently
        // undone on the next start while this window said "saved". If the profile then cannot be
        // built, the note is put back as it was, so a failed answer changes nothing.
        var settings = SettingsDir(appDir);
        var noteFile = Path.Combine(settings, InstallFile);
        var before = File.Exists(noteFile) ? File.ReadAllText(noteFile) : null;
        if (Remember(settings, root) is { } noNote)
            return new(Way.Failed, null, root, $"Could not remember the game folder: {noNote}", None);

        var profile = Build(appDir, root, program, out var why);
        if (profile is null)
        {
            try { if (before is null) File.Delete(noteFile); else File.WriteAllText(noteFile, before); }
            catch (Exception) { /* the note names a real install; the next start asks or rebuilds */ }
            return new(Way.Failed, null, root, why!, None);
        }

        return new(Way.Chosen, profile, root,
                   $"game: {root} (remembered in settings\\{InstallFile})", None);
    }

    /// <summary>
    /// Why this program cannot keep its settings beside itself, or null when it can. The Studio's
    /// settings live in the folder it runs from (so that deleting or moving the folder is the whole
    /// of uninstalling or moving it), and a folder under Program Files is not one an ordinary
    /// account can write to. Asked by the question window as it opens, so the answer is on screen
    /// before anybody presses anything, and again by <see cref="Use"/>.
    ///
    /// A probe file is made and removed; the settings folder itself is removed again if this made it,
    /// so looking leaves nothing behind.
    /// </summary>
    public static string? WriteProblem(string? appDir = null, string program = StudioName)
    {
        var settings = SettingsDir(appDir);
        var made = !Directory.Exists(settings);
        try
        {
            Directory.CreateDirectory(settings);
            var probe = Path.Combine(settings, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception e)
        {
            return CannotSave(e, program);
        }
        finally
        {
            if (made)
                try { Directory.Delete(settings); } catch (Exception) { /* not empty or not ours: leave it */ }
        }
    }

    /// <summary>The Mod Studio's own name for itself, which is what every sentence here said before a second program
    /// shared this code; the default of every <c>program</c> parameter, so the Studio's text is unchanged.</summary>
    public const string StudioName = "the Studio";

    /// <summary>What to DO about a folder that cannot be written to, not just that it cannot. <paramref name="program"/>
    /// names the program that is asking (the Level Editor passes its own name); the Studio's wording is the default,
    /// character for character.</summary>
    private static string CannotSave(Exception e, string program = StudioName)
        => program == StudioName
            ? $"The Studio cannot save its settings here ({e.Message}) It keeps them in the folder " +
              "it runs from, so move the whole Studio folder somewhere you can write to - Documents, " +
              "for example - and run it from there."
            : $"{char.ToUpperInvariant(program[0])}{program[1..]} cannot save its settings here ({e.Message}) It keeps them in " +
              "the folder it runs from, so move that whole folder somewhere you can write to - Documents, " +
              "for example - and run it from there.";

    // ------------------------------------------------------------------ is the map for this build?

    /// <summary>
    /// Whether the profile's usmap suits the build of the game that is installed - the old Studio's
    /// startup did this after every mount and put up a screen. Done HERE on the profile, before any
    /// mount, because it needs only the game's executable and the map's file name, and so the
    /// question can be put to somebody before the Studio is on screen rather than over it.
    ///
    /// A usmap describes one build's property layout; reading a newer build through it does not
    /// fail, it returns WRONG VALUES, and a mod built on them installs cleanly and misbehaves in
    /// game. Null when the profile cannot be read - a build we cannot identify is not worth
    /// stopping for. Remember the answer with <see cref="RememberBuild"/> once it has been SHOWN, or
    /// "did the game change since last time" compares this run against itself and never fires.
    /// </summary>
    public static GameInstall.Verdict? CheckBuild(string profilePath, string? appDir = null)
    {
        try
        {
            var profile = GameProfile.Load(profilePath);
            return GameInstall.Check(profile, GameInstall.Remembered(SettingsDir(appDir), profile.Name));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Write down the build that was just looked at (settings/gamebuilds.json, the old
    /// Studio's file and shape), so the next start can tell whether the game moved since.</summary>
    public static void RememberBuild(string profilePath, GameInstall.Verdict verdict, string? appDir = null)
    {
        try
        {
            var profile = GameProfile.Load(profilePath);
            GameInstall.Remember(SettingsDir(appDir), profile.Name, verdict.Identity ?? "");
        }
        catch (Exception) { /* forgetting is a worse next run, not a failed one */ }
    }

    // ------------------------------------------------------------------ the profile

    /// <summary>
    /// The filled-in profile for an install, saved as `settings/active-profile.json`; null with the
    /// reason in <paramref name="problem"/> when it cannot be made.
    ///
    /// The template is never written to. The filled-in copy lives in the settings folder, so a
    /// setup that went wrong is fixed by deleting one file rather than by repairing an installed
    /// one. PaksDirectory is the PROJECT folder, not Content/Paks: a mod installs to
    /// Mods/&lt;name&gt;/Content/Paks, a sibling of the game's own Paks, and mounting one level up
    /// shows the game and everything installed. MappingsFile is the newest usmap in `mappings/`,
    /// chosen afresh each time so an upgrade that leaves the old map behind still reads the new one.
    /// </summary>
    private static string? Build(string appDir, string install, string program, out string? problem)
    {
        problem = null;
        var paks = GameFinder.ProjectIn(install) ?? GameFinder.PaksIn(install);
        if (paks is null) { problem = $"No game data under {install}."; return null; }

        // Without a usmap the game still mounts and every property reads as garbage, silently --
        // so there is no version of this that carries on without saying anything.
        var usmap = GameInstall.BundledMappings(appDir, paks);
        if (usmap is null)
        {
            problem = "No .usmap in the 'mappings' folder beside this program. Without it the " +
                      "game's data cannot be read correctly, so it will not be read at all.";
            return null;
        }

        GameProfile profile;
        try
        {
            profile = GameProfile.Load(Path.Combine(appDir, TemplateFile));
            profile.PaksDirectory = paks;
            profile.MappingsFile = usmap;
        }
        catch (Exception e)
        {
            problem = "Could not prepare the game profile: " + e.Message;
            return null;
        }

        var path = Path.Combine(SettingsDir(appDir), ActiveFile);
        try
        {
            SaveIfChanged(profile, path);
            return path;
        }
        catch (Exception e)
        {
            // The write is the part that fails in a folder the account cannot write to; say so
            // in words that name the fix.
            problem = CannotSave(e, program);
            return null;
        }
    }

    /// <summary>A `settings/active-profile.json` that already works, with its usmap brought up to date.</summary>
    private static Outcome? SavedProfile(string appDir, string settings)
    {
        var path = Path.Combine(settings, ActiveFile);
        if (TryLoad(path) is not { } profile || !Directory.Exists(profile.PaksDirectory)) return null;

        try
        {
            if (GameInstall.BundledMappings(appDir, profile.PaksDirectory) is { } newest &&
                !string.Equals(newest, profile.MappingsFile, StringComparison.OrdinalIgnoreCase))
            {
                profile.MappingsFile = newest;
                SaveIfChanged(profile, path);
            }
        }
        catch (Exception) { /* keep the profile as it was; IsUsable decides whether it still works */ }

        if (!profile.IsUsable(out _)) return null;
        return new(Way.SavedProfile, path, GameFinder.InstallOf(profile.PaksDirectory),
                   $"game: using the saved profile ({profile.PaksDirectory})", None);
    }

    /// <summary>Where a saved profile says the game is, when that folder is no longer there - so a
    /// start with no install note but a profile left by an older run can still say what went missing.</summary>
    private static string? GoneFromSavedProfile(string settings)
    {
        if (TryLoad(Path.Combine(settings, ActiveFile)) is not { } profile) return null;
        if (string.IsNullOrWhiteSpace(profile.PaksDirectory) || Directory.Exists(profile.PaksDirectory)) return null;
        return profile.PaksDirectory;
    }

    /// <summary>Written only when the content differs, so a second start leaves the file alone
    /// (its timestamp included; with nothing to write nothing is touched at all, so a read-only
    /// folder is fine for a start that changes nothing) and a half-written file is never what
    /// another process reads.</summary>
    private static void SaveIfChanged(GameProfile profile, string path)
    {
        var text = profile.ToJson();
        if (File.Exists(path) && File.ReadAllText(path) == text) return;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".new";
        File.WriteAllText(temp, text);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            try { File.Delete(temp); } catch (Exception) { /* nothing more to do about a stray temp */ }
            throw;
        }
    }

    // ------------------------------------------------------------------ what is remembered

    /// <summary>
    /// What `settings/install.json` says, exactly as written: the path, or null when there is no
    /// note, it cannot be read, or it holds nothing. Whether that path is still true is
    /// <see cref="Alive"/>'s question - kept apart so a note that has gone stale can be told from
    /// no note at all, which is the difference between "ask, and say what went missing" and "ask".
    /// </summary>
    private static string? ReadNote(string installFile)
    {
        try
        {
            if (!File.Exists(installFile)) return null;
            var saved = JsonSerializer.Deserialize<Remembered>(File.ReadAllText(installFile), Lenient);
            return saved is null || string.IsNullOrWhiteSpace(saved.Install) ? null : saved.Install;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The install a remembered path names, if that is still true. Re-checked rather than trusted:
    /// an install can be moved or removed between runs, and a remembered path that no longer holds
    /// a game must send somebody back to being asked rather than into a mount that fails. Widened
    /// if it names the project or Paks folder, and judged strictly: an older note that points above
    /// the install (the lenient test once let that through) is simply not an answer, and the saved
    /// profile beside it still is.
    /// </summary>
    private static string? Alive(string note)
    {
        var root = GameFinder.InstallOf(note) ?? note;
        return GameFinder.IsInstall(root) ? root : null;
    }

    /// <summary>Writes the install note; the reason it could not, or null.</summary>
    private static string? Remember(string settings, string install)
    {
        try
        {
            Directory.CreateDirectory(settings);
            var file = Path.Combine(settings, InstallFile);
            var text = JsonSerializer.Serialize(new Remembered(install));
            if (!File.Exists(file) || File.ReadAllText(file) != text) File.WriteAllText(file, text);
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    // ------------------------------------------------------------------ small helpers

    /// <summary>profiles/&lt;zero-company.json&gt; at the program's folder or up to seven above it.</summary>
    private static string? FindNamed(string appDir)
    {
        var dir = new DirectoryInfo(appDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "profiles", NamedProfile);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static GameProfile? TryLoad(string path)
    {
        try { return File.Exists(path) ? GameProfile.Load(path) : null; }
        catch (Exception) { return null; }
    }
}

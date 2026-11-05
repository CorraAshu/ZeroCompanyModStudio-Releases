using Microsoft.Win32;

namespace UEToolkit.Core;

/// <summary>
/// Where Zero Company is installed on this machine.
///
/// Asked once and remembered, because the answer is a path most people would have to go hunting
/// for and none of them should have to. Steam knows it already: the client records its library
/// folders in a file beside itself, and a game is a named folder under one of them. Reading that
/// is a lookup rather than a search of the disk, which is the difference between a first run that
/// takes no time and one that scans every drive.
///
/// Everything here fails soft and returns nothing. A missing registry key, a Steam install that
/// moved, a library on a drive that is not plugged in - none of those are errors worth stopping
/// for, because the fallback is to ask the person, and asking is always available.
/// </summary>
public static class GameFinder
{
    // MOVED INTO CORE for the Avalonia Studio, which needs the same answer and cannot reference
    // the ImGui application to get it. Nothing about it was ever UI: a registry read and a walk
    // of Steam's own library file, with no graphics API anywhere in it.

    /// <summary>The folder Steam gives this game, under any library's `steamapps/common`.</summary>
    private const string FolderName = "Star Wars Zero Company";

    /// <summary>The project folder inside it - what UE calls the project directory.</summary>
    private const string ProjectFolder = "SWZeroCompany";

    /// <summary>
    /// Candidate installs, best first, with no duplicates.
    ///
    /// A list rather than one answer: someone can genuinely have two (a second library, a
    /// leftover copy), and picking silently between them is how a tool ends up editing the
    /// install its user is not playing.
    /// </summary>
    public static List<string> Find()
    {
        var found = new List<string>();

        foreach (var library in SteamLibraries())
        {
            var candidate = Path.Combine(library, "steamapps", "common", FolderName);
            if (LooksRight(candidate)) Add(found, candidate);
        }

        // Steam is where it will be for nearly everyone, but a manual copy is still a copy. The
        // fixed drives cost one directory probe each, not a walk.
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            foreach (var guess in new[]
                     {
                         Path.Combine(drive.Name, "SteamLibrary", "steamapps", "common", FolderName),
                         Path.Combine(drive.Name, "Program Files (x86)", "Steam", "steamapps",
                                      "common", FolderName),
                         Path.Combine(drive.Name, "Games", FolderName),
                         Path.Combine(drive.Name, FolderName),
                     })
                if (LooksRight(guess)) Add(found, guess);
        }

        return found;
    }

    private static void Add(List<string> into, string path)
    {
        var full = Path.GetFullPath(path);
        if (!into.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
            into.Add(full);
    }

    /// <summary>
    /// Whether a folder is really this game, judged by what has to be there for the tool to work
    /// rather than by its name. A folder called "Star Wars Zero Company" holding nothing is not an
    /// install, and accepting it means failing later with something less clear.
    /// </summary>
    public static bool LooksRight(string installFolder)
    {
        try
        {
            var paks = PaksIn(installFolder);
            return paks is not null && FolderPicker.HasPaks(paks);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Whether a folder IS a Zero Company install: `SWZeroCompany/Content/Paks` is directly under it
    /// and holds containers. STRICT AND CHEAP, on purpose, and the opposite of
    /// <see cref="LooksRight"/>, which goes looking for any `Paks` folder below the one it is given.
    ///
    /// That leniency is right for a tool that serves any game and wrong for one that serves this
    /// one: pointed at `steamapps/common` or a drive root it walks everything beneath, takes the
    /// shallowest Paks folder it meets - another game's - and calls it good, and a profile built
    /// on that mounts the wrong game and reads garbage with no error. It also made the first-run
    /// box scan the disk on every keystroke. Exact paths cost one directory probe.
    /// </summary>
    public static bool IsInstall(string? installFolder)
    {
        if (string.IsNullOrWhiteSpace(installFolder)) return false;
        try
        {
            var paks = Path.Combine(installFolder, ProjectFolder, "Content", "Paks");
            return Directory.Exists(paks) && FolderPicker.HasPaks(paks);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The PROJECT folder inside an install -- the one holding Content, Binaries and Mods.
    ///
    /// This is what gets mounted, rather than Content/Paks, and the difference is the whole reason
    /// installed mods are visible in the asset browser at all: a mod deploys to
    /// SWZeroCompany\Mods\&lt;name&gt;\Content\Paks, which is NOT under the game's own Paks folder
    /// and so was never mounted. Mounting a level higher takes in both.
    ///
    /// Everything that reads back from this still works, which was checked rather than hoped:
    /// ResolvePaks picks the SHALLOWEST Paks directory below it, so Install still writes to the
    /// game's own and not into some mod's; and FindExecutable walks UP looking for Binaries, which
    /// the project folder holds directly.
    /// </summary>
    public static string? ProjectIn(string installFolder)
    {
        if (string.IsNullOrWhiteSpace(installFolder)) return null;
        var project = Path.Combine(installFolder, ProjectFolder);
        return Directory.Exists(Path.Combine(project, "Content")) ? project : null;
    }

    /// <summary>
    /// The install folder a path inside the game belongs to - the inverse of <see cref="ProjectIn"/>.
    ///
    /// A saved profile names the PROJECT or its Paks folder, never the install, and the one thing
    /// that wants the install is the startup picture (the game keeps its splash beside the project).
    /// Walking up to the folder that holds `SWZeroCompany` answers it without asking Steam again,
    /// and returns null for a path that is not under a Zero Company install at all.
    /// </summary>
    public static string? InstallOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ProjectFolder, "Content")))
                    // Without the trailing separator a pasted "...\Zero Company\" would be saved
                    // as one, and never compare equal to the same folder found by Steam's list.
                    return Path.TrimEndingDirectorySeparator(dir.FullName);
        }
        catch (Exception) { /* a path that cannot be walked has no install to name */ }
        return null;
    }

    /// <summary>The Paks folder inside an install, or null.</summary>
    public static string? PaksIn(string installFolder)
    {
        if (string.IsNullOrWhiteSpace(installFolder)) return null;

        // The known layout first. Falling straight to the search would also accept an install
        // whose Paks folder belongs to some other project sharing the drive.
        var direct = Path.Combine(installFolder, ProjectFolder, "Content", "Paks");
        if (Directory.Exists(direct)) return direct;

        // Someone may point at the project folder itself, or at Paks. Both are reasonable things
        // to hand a tool asking "where is the game", so both work.
        var resolved = FolderPicker.ResolvePaks(installFolder);
        return FolderPicker.HasPaks(resolved) ? resolved : null;
    }

    /// <summary>
    /// Every Steam library folder, from Steam's own record of them.
    ///
    /// `libraryfolders.vdf` is Valve's key/value text. It is parsed here for one key rather than
    /// properly, because one key is what is wanted and a VDF parser is a dependency and a
    /// maintenance burden for a file whose shape has been the same for years.
    /// </summary>
    private static List<string> SteamLibraries()
    {
        var libraries = new List<string>();
        var steam = SteamPath();
        if (steam is null) return libraries;

        libraries.Add(steam);

        try
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return libraries;

            foreach (var line in File.ReadAllLines(vdf))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;

                // "path"		"D:\\SteamLibrary"
                var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var path = parts[^1].Replace("\\\\", "\\").Trim();
                if (Directory.Exists(path)) libraries.Add(path);
            }
        }
        catch (Exception) { /* an unreadable libraryfolders just means fewer candidates */ }

        return libraries;
    }

    private static string? SteamPath()
    {
        // Per-user first: it is where the running client's own path lives, and a machine can have
        // a 32-bit registry entry left over from an install that is long gone.
        foreach (var (hive, key, value) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            try
            {
                if (hive.OpenSubKey(key)?.GetValue(value) is string path &&
                    Directory.Exists(path)) return path;
            }
            catch (Exception) { /* try the next one */ }
        }
        return null;
    }
}

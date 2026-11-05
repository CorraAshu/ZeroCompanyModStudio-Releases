using System.IO.Compression;

namespace UEToolkit.Core;

/// <summary>
/// Package an installed mod as a zip somebody else can use.
///
/// The people this tool is for upload to Nexus, and until now the last step was theirs: find the
/// folder the mod was installed into, work out that the ZIP has to contain the folder rather than
/// its contents, and write the instructions themselves. Every one of those is easy to get subtly
/// wrong, and each way of getting it wrong produces a download that does nothing when extracted --
/// which the uploader cannot see, because on their machine it is already installed.
///
/// THE STRUCTURE IS THE WHOLE POINT. The zip's root holds the mod's FOLDER, so extracting it into
/// SWZeroCompany\Mods gives Mods\&lt;Name&gt;\ and not Mods\Content\. A zip made from inside the
/// folder looks identical in a file browser and installs to nothing.
///
/// What goes in is what <see cref="ModInstall"/> put there and nothing else: the descriptor, the
/// registry, the three containers, and whatever config the plugin carries. Read off the installed
/// folder rather than rebuilt from the project, so the thing shared is the thing tested.
///
/// A MOD WITH AN EARLY HALF LIVES IN TWO PLACES, so the zip stops being the folder and becomes the
/// game folder: <c>Mods/&lt;Plugin&gt;/...</c> plus <c>Content/Paks/~mods/&lt;Plugin&gt;_Early_P.*</c>,
/// extracted into SWZeroCompany. One extraction, both destinations, nothing for the person to
/// sort. A mod with no early half is zipped exactly as before.
/// </summary>
public static class ModShare
{
    /// <summary>The instructions that travel with it. Written into the zip, not left to the page.</summary>
    public const string ReadmeName = "HOW TO INSTALL.txt";

    /// <summary>
    /// Zip <paramref name="installedFolder"/> into <paramref name="zipPath"/>, folder and all.
    ///
    /// Returns false with a reason rather than throwing: this is a button, and a person who
    /// pressed it wants a sentence.
    ///
    /// <paramref name="earlyFiles"/> is the installed early trio (<see cref="ModInstall.EarlyFiles"/>),
    /// or null. Entries that are not on disk are ignored, so a stale list cannot put a missing file
    /// in the zip. What is left decides the layout: none = the plain zip; the whole trio = the
    /// two-place zip, see the class remarks; anything between is refused, naming what is missing,
    /// because a zip with part of the early half installs a mod whose early changes never show and
    /// says nothing about it.
    /// </summary>
    public static bool Zip(string installedFolder, string zipPath, string modName,
                           out string? why, Action<string>? log = null,
                           IReadOnlyList<string>? earlyFiles = null)
    {
        why = null;
        try
        {
            if (!Directory.Exists(installedFolder))
            { why = $"{installedFolder} is not there -- install the mod first, and share what was installed"; return false; }

            var files = Directory.EnumerateFiles(installedFolder, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0)
            { why = $"{installedFolder} is empty"; return false; }

            // A mod folder with no containers in it is a folder the plugin manager skips without a
            // word, and zipping one produces a download that installs and does nothing. Checked
            // here rather than trusted, because this is the last point at which anybody looks.
            if (!files.Any(f => f.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase)) ||
                !files.Any(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)) ||
                !files.Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
            { why = "that folder has no .pak, .utoc and .ucas trio in it, so it would install a mod that does nothing"; return false; }

            var folder = Path.GetFileName(installedFolder.TrimEnd(Path.DirectorySeparatorChar,
                                                                  Path.AltDirectorySeparatorChar));
            if (folder.Length == 0) { why = "could not read the mod folder's name"; return false; }

            // Only what is really there. With none of it the zip is exactly what it always was.
            var early = earlyFiles?.Where(File.Exists).ToList() ?? new List<string>();
            if (EarlyProblem(early) is { } earlyWhy) { why = earlyWhy; return false; }
            var twoPlaces = early.Count > 0;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // The folder in front of every entry, and under Mods when the zip is the game folder.
                var prefix = twoPlaces ? $"Mods/{folder}" : folder;
                foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    // Forward slashes, and the folder in front of every entry. Windows writes
                    // backslashes here quite happily and several extractors then treat the whole
                    // path as one filename.
                    var relative = Path.GetRelativePath(installedFolder, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, $"{prefix}/{relative}", CompressionLevel.Optimal);
                    log?.Invoke($"   {prefix}/{relative}");
                }

                // The early half goes FLAT into Content/Paks/~mods, which the game reads at startup:
                // where ModInstall puts it, and where it looks for it again.
                foreach (var file in early)
                {
                    var entry = $"Content/Paks/~mods/{Path.GetFileName(file)}";
                    zip.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
                    log?.Invoke($"   {entry}");
                }

                // Beside the Mods folder it describes when the zip is the game folder; inside the mod's
                // folder, as it always was, when the zip IS the folder.
                var readme = zip.CreateEntry(twoPlaces ? ReadmeName : $"{folder}/{ReadmeName}",
                                             CompressionLevel.Optimal);
                using var writer = new StreamWriter(readme.Open());
                writer.Write(twoPlaces
                    ? ReadmeEarly(modName, folder, early.Select(Path.GetFileName).ToList()!)
                    : Readme(modName, folder));
            }

            log?.Invoke($"   {files.Count + early.Count + 1} file(s) -> {Path.GetFileName(zipPath)} " +
                        $"({new FileInfo(zipPath).Length:N0} bytes)");
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    /// <summary>
    /// What the person who downloads it needs to know, and nothing else.
    ///
    /// The uninstall warning is here rather than only in the tool because it is the one thing that
    /// breaks somebody's SAVE: the save holds modded gear by full package path, so removing a mod
    /// whose gear is equipped leaves the Armoury unable to resolve it. Whoever downloads this will
    /// remove it one day, and they will not have read anything by then.
    ///
    /// AND THE ~mods WARNING, for the same reason: it is aimed at the person who will be TOLD to
    /// put it there. The author, 2026-09-09: "I don't believe people are understanding that it
    /// can't go in those folders and needs those other 2 files and just releasing the paks."
    ///
    /// That instruction is right for nearly every other Unreal game, so it is what people reach
    /// for, and it fails in the worst possible way here: the containers still mount from
    /// Content\Paks\~mods -- the engine's own pak scan finds them and `~` sorts last -- so a
    /// replacement can still take effect, while the game's mod loader never sees the folder and
    /// never reads the registry, so anything the mod ADDS is simply absent. Half a mod, no error,
    /// and the reasonable conclusion is that the mod is broken.
    ///
    /// The tool cannot reach the person doing it. This file can: it is the one piece of text that
    /// travels with the download and is open in Notepad at the moment the decision is made.
    /// </summary>
    public static string Readme(string modName, string folder) =>
        string.Join(Environment.NewLine,
            modName,
            new string('=', Math.Max(modName.Length, 3)),
            "",
            "A mod for Star Wars: Zero Company. No mod loader, no script extender.",
            "",
            "TO INSTALL",
            "",
            $"  Put the '{folder}' folder into your game's Mods folder, so that you end up with:",
            "",
            $"      ...\\Star Wars Zero Company\\SWZeroCompany\\Mods\\{folder}\\{folder}.uplugin",
            "",
            "  Make one if there is no Mods folder there yet. Close the game first.",
            "",
            "NOT IN ~mods, AND NOT THE .pak ON ITS OWN",
            "",
            "  Most Unreal games take a .pak dropped into Content\\Paks\\~mods. This one does not,",
            "  and getting it wrong is hard to spot: the files will still mount from there, so",
            "  part of the mod can appear to work -- while the game never reads the mod's",
            "  registry, so anything the mod ADDS never shows up at all. It looks installed and",
            "  it looks like the mod is broken.",
            "",
            "  Keep the folder together, and put the FOLDER in Mods. The .uplugin and",
            "  AssetRegistry.bin next to this file are how the game finds the mod in the first",
            "  place; the three files under Content\\Paks are the mod itself. All of them travel",
            "  together.",
            "",
            "TO REMOVE",
            "",
            "  IN THE GAME FIRST: unequip anything from this mod and save.",
            "",
            "  Your save records modded gear by where its files live, so deleting the folder while",
            "  a character is still wearing or carrying something from it leaves the game unable to",
            "  find that item. Unequip, save, quit, and then delete the folder.",
            "",
            "  Nothing belonging to the game is changed by installing this, so removing the folder",
            "  removes the mod completely.",
            "",
            "Made with Zero Company Mod Studio.");

    /// <summary>
    /// The instructions for a mod with an early half: two destinations, one extraction.
    ///
    /// The removal part carries the same safety as <see cref="Readme"/>'s -- unequip and save in the
    /// game BEFORE deleting, because the save holds modded gear by path -- adapted to two places to
    /// delete. That is the one instruction here whose omission breaks somebody's save.
    /// </summary>
    public static string ReadmeEarly(string modName, string folder, IReadOnlyList<string> earlyNames) =>
        $"{modName}\r\n\r\n" +
        "HOW TO INSTALL\r\n" +
        "Extract this zip into your game's SWZeroCompany folder -- the one that holds Mods and Content --\r\n" +
        "so that you end up with BOTH of these:\r\n" +
        $"    ...\\SWZeroCompany\\Mods\\{folder}\\{folder}.uplugin\r\n" +
        string.Concat(earlyNames.Select(n => $"    ...\\SWZeroCompany\\Content\\Paks\\~mods\\{n}\r\n")) +
        "\r\nWHY TWO PLACES\r\n" +
        "This mod changes game files that the game loads before it reads the Mods folder. Those changes\r\n" +
        "have to sit in Content\\Paks\\~mods, which the game reads at startup; the rest of the mod works\r\n" +
        "from Mods like any other. Without the ~mods files the mod loads but those changes never show.\r\n" +
        "\r\nTO UNINSTALL\r\n" +
        "IN THE GAME FIRST: unequip anything from this mod and save.\r\n" +
        "Your save records modded gear by where its files live, so deleting them while a character is still\r\n" +
        "wearing or carrying something from this mod leaves the game unable to find that item.\r\n" +
        "Back up your saves first. Then unequip, save, and quit.\r\n" +
        $"Close the game before you delete anything: delete the Mods\\{folder} folder AND the ~mods files\r\n" +
        "listed above. Nothing belonging to the game is changed by installing this, so removing both\r\n" +
        "removes the mod completely.\r\n" +
        "\r\nYou can delete this HOW TO INSTALL.txt as well: the zip put it in your SWZeroCompany folder.\r\n";

    private static readonly string[] EarlyTrio = ["pak", "utoc", "ucas"];

    /// <summary>
    /// Why these (existing) early files cannot be shared, or null. None is fine -- that is a mod with no
    /// early half. Otherwise it has to be exactly one .pak, .utoc and .ucas of one name: any less is a
    /// mod whose early changes silently never show.
    /// </summary>
    private static string? EarlyProblem(List<string> early)
    {
        if (early.Count == 0) return null;
        var stems = early.Select(f => Path.GetFileNameWithoutExtension(f))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (stems.Count == 1)
        {
            var have = early.Select(f => Path.GetExtension(f).TrimStart('.')).ToList();
            var missing = EarlyTrio.Where(e => !have.Contains(e, StringComparer.OrdinalIgnoreCase))
                                   .Select(e => $"{stems[0]}.{e}").ToList();
            if (missing.Count > 0)
                return $"the mod's early half is incomplete -- {string.Join(" and ", missing)} " +
                       $"{(missing.Count == 1 ? "is" : "are")} missing from Content\\Paks\\~mods, so a zip of it would " +
                       "install a mod whose early changes never show. Install the mod again, then share it";
            if (early.Count == EarlyTrio.Length) return null;
        }
        return "the early files are not one .pak, .utoc and .ucas set: " +
               string.Join(", ", early.Select(Path.GetFileName));
    }
}

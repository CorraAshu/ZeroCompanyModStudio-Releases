using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Versions;

namespace UEToolkit.Core;

/// <summary>
/// Put a built mod into the game, and take it back out.
///
/// Until this existed the build wrote three files and told a person to copy them somewhere by
/// hand, which is the one step in the whole pipeline that needed a shell and a paragraph of
/// instructions -- and the paragraph was WRONG for a while after the install location changed,
/// with nothing to catch that because prose does not run.
///
/// THERE ARE TWO LAYOUTS, and which one a mod needs is decided by the build, not here.
///
///   plugin   A FOLDER in the project's Mods directory holding a .uplugin, the mod's OWN small
///            asset registry, and the containers under Content/Paks. The GAME's own mod runtime
///            finds it -- not Unreal's plugin manager; see PluginsFolderName. Nothing is shared
///            between two mods, so two mods cannot collide however many are installed and whoever
///            wrote them. A `_P` container here also WINS against the game's base containers, so
///            this layout serves overrides as well as new content. This is what a released mod
///            ships as.
///
///   legacy   The trio dropped loose in Content/Paks/~mods, where the pak carries a COPY OF THE
///            WHOLE GAME REGISTRY with the mod's entries added, at a path every mod writes. Two of
///            those clobber each other and the loser is simply absent from the game while looking
///            perfectly installed. Kept because builds in that shape exist on disk and have to be
///            listed and removable, not because it is a reasonable way to ship.
///
/// Three things are load-bearing in both, and each is a fault we already paid for:
///
///   nothing overwritten  Neither layout writes over a game file, so there is no backup to keep
///                        and no restore to get right. An earlier note claimed ~mods does not work
///                        in this game; every candidate in that experiment was a LONE PAK, which
///                        is a fact about siblings and was written up as a fact about the folder.
///
///   all three            A pak with no .utoc sibling never mounts, anywhere. That IS what the
///                        ~mods experiment proved, so a half-installed mod is refused rather than
///                        copied, and an existing half-install is reported as broken rather than
///                        listed.
///
///   locked files         The game maps these containers while it runs. Writing over one produces
///                        a file that is neither the old mod nor the new one, and the failure
///                        surfaces later as a crash the person will blame on the mod.
///
/// A plugin mod that changes a file the game loads before it mounts Mods/ (<see cref="EarlyLoad"/>) has a
/// SECOND HALF: <c>&lt;Plugin&gt;_Early_P.{pak,utoc,ucas}</c>, flat in Content/Paks/~mods. It is the same mod --
/// installed, removed, lock-checked and owned with the plugin folder, and never listed as a mod of its own.
///
/// <see cref="ConflictsWith"/> is the part that is not just a file copy. Two mods that define the
/// same package cannot both be installed: one silently wins, and which one is not worth guessing
/// at. The game shipped no way to notice that -- our own dev setup papered over it by moving a
/// container aside -- so the containers already installed are read and their package ids compared
/// against the ones about to be written. That check spans BOTH layouts, because a plugin mod and
/// a ~mods mod defining one package collide exactly as two of either would.
/// </summary>
public static class ModInstall
{
    /// <summary>How a mod sits in the game folder. See the class comment for what each costs.</summary>
    public enum Layout { Plugin, Legacy }

    /// <summary>
    /// The community convention, and the reason it works: `~` sorts last, so these containers
    /// mount after the game's own and win any lookup they share.
    /// </summary>
    public const string ModsFolderName = "~mods";

    /// <summary>
    /// FPaths::ProjectModsDir(), which is the project directory plus this.
    ///
    /// It is NOT Unreal's plugin manager that finds us. That searches
    /// `&lt;Plugin&gt;/Content/Paks/&lt;Platform&gt;/` and passes mount order 0, and never sees this layout at
    /// all. What mounts a mod here is the GAME's own
    /// `UBitReactorModRuntimeSubsystem::RefreshAvailableMods`, which scans ProjectModsDir for `*.pak`,
    /// calls `TryMountMod`, and then loads `&lt;ModDir&gt;/AssetRegistry.bin`. The mod runtime is not
    /// dormant -- it is our loader.
    /// </summary>
    public const string PluginsFolderName = "Mods";

    private static readonly string[] Trio = { "pak", "utoc", "ucas" };

    /// <summary>One mod that is installed. <c>Missing</c> empty means it can load.</summary>
    public sealed record Present(string Name, long Bytes, IReadOnlyList<string> Missing, Layout How,
                                 string Root)
    {
        public bool Complete => Missing.Count == 0;

        /// <summary>
        /// When this copy was built, from the newest CONTAINER file under it.
        ///
        /// The container rather than the folder: a folder's timestamp moves for reasons that have
        /// nothing to do with a build -- something written beside it, a file removed, a backup
        /// tool touching it -- and a false "you have edited since you built" is worse than none,
        /// because the first thing it teaches is to ignore it.
        ///
        /// Null when nothing datable is there, which the caller reads as "cannot say" rather than
        /// as "old".
        /// </summary>
        public DateTime? BuiltAt
        {
            get
            {
                try
                {
                    if (Root.Length == 0 || !Directory.Exists(Root)) return null;
                    DateTime? newest = null;
                    foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        if (!file.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) &&
                            !file.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase) &&
                            !file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) continue;
                        var at = File.GetLastWriteTimeUtc(file);
                        if (newest is null || at > newest) newest = at;
                    }
                    return newest;
                }
                catch (Exception) { return null; }
            }
        }
    }

    /// <summary>
    /// A package this mod defines that something already installed also defines. Both cannot
    /// apply; the file named here is the one to remove, or to rename so it loses the sort.
    /// </summary>
    public sealed record Clash(string Container, string PackageName, ulong PackageId,
                               ClashKind Kind = ClashKind.OtherMod);

    /// <summary>
    /// What kind of collision this is, because they are not all faults.
    ///
    /// Overriding a game asset is a package id collision BY DESIGN -- it is the whole mechanism,
    /// and treating it as a clash refused every override before it could be tested. What must
    /// stay a hard failure is the collision nobody asked for: a package landing on a game id
    /// without an <c>overrides</c> entry saying so is a mod clobbering a real asset by accident,
    /// which is the most damaging thing this tool can do and the least visible.
    /// </summary>
    public enum ClashKind
    {
        /// <summary>Another mod defines it too. Neither is more entitled; a person must choose.</summary>
        OtherMod,

        /// <summary>A declared override of a game package. Intended, and reported, not refused.</summary>
        Replaces,

        /// <summary>A game package this mod lands on WITHOUT declaring it. Always a fault.</summary>
        Undeclared,
    }

    // ------------------------------------------------------------------ where things go

    /// <summary>
    /// The real Paks folder for a profile whose PaksDirectory may point at either the Paks folder
    /// itself or the game root -- both are valid there, because the provider searches downward and
    /// does not care. Installing does care, so the ambiguity is resolved here rather than left to
    /// whoever wrote the profile.
    /// </summary>
    public static string? ResolvePaks(string paksDirectory)
    {
        if (string.IsNullOrWhiteSpace(paksDirectory) || !Directory.Exists(paksDirectory)) return null;

        // The picker's search, not a second one. Both answer "where are this game's containers",
        // and the first version of this asked a DIFFERENT question -- a folder literally named
        // Paks under one named Content -- which is a naming convention rather than evidence.
        // Two implementations of one question drift, and this one decides where files get
        // written.
        var found = FolderPicker.ResolvePaks(paksDirectory);

        // It hands back what it was given when it finds nothing, so the test is whether there are
        // actually containers there -- never whether a path came back.
        return FolderPicker.HasPaks(found) ? Path.GetFullPath(found) : null;
    }

    public static string ModsDirectory(string paks) => Path.Combine(paks, ModsFolderName);

    /// <summary>
    /// The project directory -- what FPaths::ProjectDir() returns -- found by walking up from the
    /// Paks folder for the directory that HAS a Content subfolder. That relationship is the
    /// definition (ProjectContentDir() is ProjectDir() + "Content/"), which is why it is tested
    /// rather than assumed.
    ///
    /// Two levels up from Paks would be right for every normal install and silently wrong for the
    /// one case ResolvePaks explicitly allows: a profile pointing at a folder that merely happens
    /// to hold containers. Writing a mod folder two levels above that is writing it somewhere
    /// nothing will ever look.
    /// </summary>
    public static string? ProjectDirectory(string paks)
    {
        try
        {
            for (var d = new DirectoryInfo(paks); d is not null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "Content"))) return d.FullName;
        }
        catch { /* an unreadable parent is the same answer as no parent */ }
        return null;
    }

    /// <summary>Where plugin mods go. Null when the Paks folder is not inside a project.</summary>
    public static string? PluginsDirectory(string paks) =>
        ProjectDirectory(paks) is { } project ? Path.Combine(project, PluginsFolderName) : null;

    /// <summary>
    /// This mod's own folder inside the Mods directory. Null when there is no Mods directory, and
    /// null when the name does not survive being turned into one.
    ///
    /// THE CHECK IS ON THE RESOLVED PATH, not on the name. Uninstalling DELETES this folder and
    /// everything under it, so what matters is where the path actually lands -- and on Windows
    /// that is not the string that was combined. Win32 strips trailing dots and spaces from each
    /// component AFTER the combine, and `Path.GetInvalidFileNameChars()` contains neither, so a
    /// name that passes every check on the way in can still alias somewhere else on the way out:
    ///
    ///     Mods + "..."        ->  Mods\           the Mods folder ITSELF. Uninstalling a mod by
    ///                             that name enumerated and deleted every mod installed.
    ///     Mods + "ArfHelmet." ->  Mods\ArfHelmet  somebody else's mod. Installing under that name
    ///                             swept that mod's containers as "stale" and deleted them, while
    ///                             the panel still said "not installed".
    ///
    /// Both were real, and both were found by testing the guard rather than reading it. So the
    /// question asked here is the one that decides the outcome: does this path resolve to a direct
    /// child of the Mods folder that is still CALLED what we asked for. A separator, a `..`, an
    /// empty name and a name of spaces all fail it for the same reason, without needing to be
    /// enumerated.
    /// </summary>
    public static string? PluginDirectory(string paks, string modName)
    {
        var name = PluginName(modName);
        if (name.Length == 0) return null;
        if (PluginsDirectory(paks) is not { } mods) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(mods, name));

            // Still called what we asked for. This is what rejects a trailing dot or space: the
            // name survives the combine and is gone by the time the path is resolved.
            if (!Path.GetFileName(full).Equals(name, StringComparison.OrdinalIgnoreCase)) return null;

            // And directly inside the Mods folder -- never beside it, never deeper.
            var parent = Path.GetDirectoryName(full);
            return parent is not null &&
                   parent.Equals(Path.GetFullPath(mods).TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase)
                ? full : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The plugin's name: the container name without the _P the patch suffix adds. The folder, the
    /// .uplugin inside it and the mount point all have to agree on this -- the plugin manager
    /// reads the descriptor's name off the folder -- while the containers keep the suffix, because
    /// that is what makes them win a lookup against the game's own.
    /// </summary>
    public static string PluginName(string containerName) =>
        containerName.EndsWith("_P", StringComparison.Ordinal) ? containerName[..^2] : containerName;

    // ------------------------------------------------------------------ what is installed

    /// <summary>
    /// Every mod installed, in either layout.
    ///
    /// Incomplete ones are listed WITH what they are missing rather than hidden, because a mod
    /// that is present and not loading is exactly the situation a person needs help seeing -- it
    /// looks installed from the file manager.
    ///
    /// Both layouts are walked even though only one is written any more. A build made before the
    /// plugin layout existed is still sitting in ~mods on the machine that made it, and a lister
    /// that cannot see it is a lister that cannot help remove it.
    /// </summary>
    public static List<Present> Installed(string paks)
    {
        var found = new List<Present>();
        found.AddRange(InstalledPlugins(paks));
        found.AddRange(InstalledLegacy(paks));
        return found;
    }

    public static List<Present> InstalledPlugins(string paks)
    {
        var found = new List<Present>();
        var root = PluginsDirectory(paks);
        if (root is null || !Directory.Exists(root)) return found;

        foreach (var dir in Directory.EnumerateDirectories(root)
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var missing = new List<string>();
            long bytes = 0;

            var uplugin = Path.Combine(dir, name + ".uplugin");
            if (File.Exists(uplugin)) bytes += new FileInfo(uplugin).Length;
            else missing.Add(name + ".uplugin");

            var registry = Path.Combine(dir, "AssetRegistry.bin");
            if (File.Exists(registry)) bytes += new FileInfo(registry).Length;
            else missing.Add("AssetRegistry.bin");

            // The containers are named after the CONTAINER, which keeps the _P -- so the folder is
            // not enough to name them, and they are found rather than constructed. A mod built
            // under a different name and copied in by hand still reads correctly this way.
            var paksIn = Path.Combine(dir, "Content", "Paks");
            var stems = Directory.Exists(paksIn)
                ? Directory.EnumerateFiles(paksIn)
                      .Where(f => Trio.Contains(Path.GetExtension(f).TrimStart('.'),
                                                StringComparer.OrdinalIgnoreCase))
                      .Select(f => Path.GetFileNameWithoutExtension(f))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            if (stems.Count == 0) missing.Add("Content/Paks containers");
            foreach (var stem in stems)
                foreach (var ext in Trio)
                {
                    var f = Path.Combine(paksIn, $"{stem}.{ext}");
                    if (File.Exists(f)) bytes += new FileInfo(f).Length;
                    else missing.Add($"{stem}.{ext}");
                }

            found.Add(new Present(name, bytes, missing, Layout.Plugin, dir));
        }
        return found;
    }

    public static List<Present> InstalledLegacy(string paks)
    {
        var mods = ModsDirectory(paks);
        var found = new List<Present>();
        if (!Directory.Exists(mods)) return found;
        var plugins = PluginsDirectory(paks);

        foreach (var name in Directory.EnumerateFiles(mods, "*.*")
                     .Where(f => Trio.Contains(Path.GetExtension(f).TrimStart('.'),
                                               StringComparer.OrdinalIgnoreCase))
                     .Select(f => Path.GetFileNameWithoutExtension(f))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     // The early half of an INSTALLED plugin is not a mod of its own: it is listed under the
                     // plugin. The same rule Owner uses -- the plugin folder has to exist. An early trio whose
                     // folder is gone (deleted by hand, or an uninstall that stopped partway) still overrides
                     // the game's file from ~mods, so it is listed, as the Legacy entry it now is, for a
                     // person to see and remove; and so is a third party's loose mod that merely ends this way.
                     .Where(n => !(EarlyLoad.IsEarlyContainer(n) && plugins is not null &&
                                   Directory.Exists(Path.Combine(plugins, EarlyLoad.PluginOf(n)))))
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            long bytes = 0;
            var missing = new List<string>();
            foreach (var ext in Trio)
            {
                var f = Path.Combine(mods, $"{name}.{ext}");
                if (File.Exists(f)) bytes += new FileInfo(f).Length;
                else missing.Add("." + ext);
            }
            found.Add(new Present(name, bytes, missing, Layout.Legacy, mods));
        }
        return found;
    }

    /// <summary>The installed early trio of this mod (existing files only), for sharing and listing.</summary>
    public static List<string> EarlyFiles(string paks, string modName)
    {
        var mods = ModsDirectory(paks);
        var name = EarlyLoad.ContainerName(modName);
        return Trio.Select(ext => Path.Combine(mods, $"{name}.{ext}")).Where(File.Exists).ToList();
    }

    // ------------------------------------------------------------------ can we write

    /// <summary>
    /// Whether the files this install would touch can be written right now.
    ///
    /// The test is the thing actually cared about -- can this file be opened for writing -- rather
    /// than the name of a process, which differs per game and per launcher and would have to be
    /// configured to be wrong. A running game that has never seen this mod locks nothing, and
    /// adding a file it will not read until it restarts harms nothing, so that case is correctly
    /// allowed through.
    ///
    /// BOTH layouts are tested regardless of which one is being written, because installing
    /// removes the other one's copy of this same mod -- see <see cref="Install"/> -- and a delete
    /// blocked by a lock fails just as hard as a write.
    /// </summary>
    public static bool CanWrite(string paks, string modName, out string? blockedBy)
    {
        blockedBy = null;
        foreach (var f in Touched(paks, modName))
        {
            if (!File.Exists(f)) continue;
            // WRITE ACCESS, SHARED -- exactly what the copy that follows needs, and no more.
            // This asked for EXCLUSIVE access (FileShare.None), which fails whenever anything at
            // all holds the file, including a reader that would not obstruct a copy: this app's
            // own view of the game mounts the Mods folder, so the tool could refuse to install
            // over a file it was merely reading. The report then blamed the game, which was not
            // running. A locked file still fails here, because the game opens its containers in a
            // way that denies writers.
            try { using var s = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { blockedBy = f; return false; }
            catch (UnauthorizedAccessException) { blockedBy = f; return false; }
        }
        return true;
    }

    /// <summary>
    /// Can every file this mod owns actually be DELETED?
    ///
    /// FileShare.Delete is the whole point: it asks the question Windows will ask, which is
    /// whether every existing handle was opened in a way that tolerates the file going away.
    /// A reader that did not say so blocks a delete no matter what rights the user has -- and
    /// Explorer reports that as "you need permission", which sends people looking at ACLs that
    /// were never the problem.
    /// </summary>
    public static bool CanDelete(string paks, string modName, out string? blockedBy)
    {
        blockedBy = null;
        foreach (var f in Touched(paks, modName))
        {
            if (!File.Exists(f)) continue;
            // EXCLUSIVE, unlike CanWrite -- and the difference is the whole point.
            //
            // Installing can tolerate a reader: the copy succeeds and the reader sees stale bytes
            // until it reopens. Uninstalling cannot. CUE4Parse opens containers with share-delete,
            // so the files DO delete -- and then sit DELETE-PENDING until the last handle closes,
            // invisible in a listing while still occupying their directory entry. The folder then
            // refuses to go, looking empty, and Explorer reports that as needing permission. So the
            // question here is not "may I delete this file" but "is anyone holding it at all".
            try { using var s = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { blockedBy = f; return false; }
            catch (UnauthorizedAccessException) { blockedBy = f; return false; }
        }
        return true;
    }

    /// <summary>Every file either layout of this mod owns. Missing ones included; callers skip them.</summary>
    private static IEnumerable<string> Touched(string paks, string modName)
    {
        var mods = ModsDirectory(paks);
        foreach (var ext in Trio) yield return Path.Combine(mods, $"{modName}.{ext}");

        // The early half (EarlyLoad) sits in the same folder under its own name. Before the plugin
        // folder is looked for, because it is written and removed whether or not one is found.
        foreach (var ext in Trio) yield return Path.Combine(mods, $"{EarlyLoad.ContainerName(modName)}.{ext}");

        if (PluginDirectory(paks, modName) is not { } dir) yield break;
        yield return Path.Combine(dir, PluginName(modName) + ".uplugin");
        yield return Path.Combine(dir, "AssetRegistry.bin");

        // Every container in there, not just the ones this build would write. Renaming a mod and
        // reinstalling leaves the old pair beside the new one, and both mount -- so the install
        // deletes them, which means the install has to be able to.
        var paksIn = Path.Combine(dir, "Content", "Paks");
        if (!Directory.Exists(paksIn)) yield break;
        foreach (var f in Directory.EnumerateFiles(paksIn))
            if (Trio.Contains(Path.GetExtension(f).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                yield return f;
    }

    // ------------------------------------------------------------------ conflicts

    /// <summary>
    /// Packages this mod defines that a container already installed also defines.
    ///
    /// Every container's package store is read -- the same header <c>--iohdr</c> prints -- and the
    /// ids compared. Ids, not names: the id is what the loader files a package under, so two
    /// packages colliding on it collide in the game whatever they are called.
    ///
    /// The mod's own previously installed copy is skipped, since replacing yourself is what
    /// installing means.
    /// </summary>
    public static List<Clash> ConflictsWith(ModBuild.Result built, string paks, EGame game,
                                            string modName, Action<string>? log = null)
    {
        // Indexed rather than ToDictionary: two parts sharing an id is a fault the validator
        // catches long before here, and it should not come back as a raw duplicate-key throw from
        // the install step.
        var mine = new Dictionary<ulong, string>();
        var overrides = new HashSet<ulong>();
        foreach (var p in built.Parts)
        {
            var id = PackageId.Of(p.PackageName);
            mine[id] = p.PackageName;
            if (p.IsOverride) overrides.Add(id);
        }
        var clashes = new List<Clash>();

        var containers = new List<string>();
        if (Directory.Exists(paks))
            containers.AddRange(Directory.EnumerateFiles(paks, "*.utoc", SearchOption.AllDirectories));

        // The Mods folder is a SIBLING of Content, not under Paks, so the sweep above cannot see a
        // single plugin mod. Leaving it out would have made this check answer "no conflicts" for
        // the only layout anyone ships in -- confidently, and about nothing.
        if (PluginsDirectory(paks) is { } pluginRoot && Directory.Exists(pluginRoot))
            containers.AddRange(Directory.EnumerateFiles(pluginRoot, "*.utoc", SearchOption.AllDirectories));

        var ownFolder = PluginName(modName);
        foreach (var utoc in containers)
        {
            if (Path.GetFileNameWithoutExtension(utoc).Equals(modName, StringComparison.OrdinalIgnoreCase))
                continue;
            // Our own early half. Owner() names it only while the plugin folder exists, and an early
            // trio outliving a hand-deleted folder is still ours -- not a rival to refuse over.
            if (EarlyLoad.IsEarlyContainer(utoc) &&
                EarlyLoad.PluginOf(utoc).Equals(ownFolder, StringComparison.OrdinalIgnoreCase)) continue;
            // And anything in our own folder, whatever the container inside is called.
            if (Owner(utoc, paks) is { } owner &&
                owner.Equals(ownFolder, StringComparison.OrdinalIgnoreCase)) continue;

            List<ulong> ids;
            try { ids = PackageIdsIn(utoc, game); }
            catch (Exception e)
            {
                // A container we cannot read is not evidence of a conflict, and refusing to
                // install over one unreadable file would block on the game's own oddities.
                log?.Invoke($"   could not read {Path.GetFileName(utoc)}: {e.Message}");
                continue;
            }

            var isGame = IsGameContainer(utoc, paks);
            foreach (var id in ids)
                if (mine.TryGetValue(id, out var name))
                    clashes.Add(new Clash(utoc, name, id,
                        !isGame ? ClashKind.OtherMod
                        : overrides.Contains(id) ? ClashKind.Replaces
                        : ClashKind.Undeclared));
        }
        return clashes;
    }

    /// <summary>
    /// Whether a container is the game's own, as opposed to another mod's.
    ///
    /// Decided by position rather than by name: the shipped containers sit DIRECTLY in
    /// Content/Paks, while every route a mod arrives by is a subfolder of it (<c>~mods</c>,
    /// <c>LogicMods</c>) or outside it entirely (the Mods plugin folder). Naming them instead
    /// would mean keeping a list of what the game happens to ship, which goes stale on a patch.
    /// </summary>
    private static bool IsGameContainer(string container, string paks)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(container));
            return dir is not null && dir.TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(paks).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        // A path that will not parse is treated as NOT the game's, which routes it to the
        // strictest outcome rather than the most permissive one.
        catch { return false; }
    }

    /// <summary>
    /// The plugin folder a container belongs to, or null if it is not inside one. Lets a clash be
    /// named after the MOD rather than after a container three folders down that a person would
    /// then have to go looking for.
    /// </summary>
    public static string? Owner(string container, string paks)
    {
        if (PluginsDirectory(paks) is not { } root) return null;

        // An early trio in ~mods belongs to the plugin it was built with (EarlyLoad).
        if (EarlyLoad.IsEarlyContainer(container) &&
            Directory.Exists(Path.Combine(root, EarlyLoad.PluginOf(container))))
            return EarlyLoad.PluginOf(container);

        try
        {
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            for (var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(container)) ?? ".");
                 d?.Parent is not null; d = d.Parent)
                if (d.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar)
                        .Equals(full, StringComparison.OrdinalIgnoreCase)) return d.Name;
        }
        // Only ever used to put a nicer name on a message. A path this cannot parse is not a
        // reason to take down the install that was reporting the clash.
        catch { /* fall through to "not in a mod folder" */ }
        return null;
    }

    /// <summary>
    /// Another installed mod that also ships a merged asset registry AT THE GAME'S OWN PATH.
    ///
    /// This is a worse conflict than two mods defining the same package, and it is invisible to
    /// the check above, which compares package ids inside .utoc containers. The registry does not
    /// live there -- a LEGACY mod puts it in the legacy .pak at SWZeroCompany/AssetRegistry.bin, a
    /// path every legacy mod writes. Load order picks one and the loser's copy is simply absent.
    ///
    /// What that costs is total. A legacy mod's registry is the game's entries plus its own, built
    /// from the PRISTINE registry so that rebuilding does not compound -- so the winner's copy has
    /// no record of the loser's parts at all, and a part the registry does not list does not
    /// exist. The loser installs cleanly, overwrites nothing, verifies green, and is simply not in
    /// the game. That happened: two mods, one visible, and the install said "nothing in the game
    /// folder was overwritten".
    ///
    /// A PLUGIN mod is not a participant. Its registry holds its own entries only and sits inside
    /// its own folder, which no other mod writes -- so it neither loses this fight nor starts one,
    /// and the caller skips this check when installing one. That is the property the layout exists
    /// for: two mods by two authors who have never heard of each other, installed together, both
    /// working.
    /// </summary>
    public sealed record RegistryClash(string Container, string Path, long Bytes);

    public static List<RegistryClash> RegistryConflicts(string paks, EGame game, string modName,
                                                        Action<string>? log = null)
    {
        var found = new List<RegistryClash>();
        var mods = System.IO.Path.Combine(paks, ModsFolderName);
        if (!Directory.Exists(mods)) return found;

        foreach (var pak in Directory.EnumerateFiles(mods, "*.pak", SearchOption.AllDirectories))
        {
            // Ours is allowed to ship one; that is the whole point of it.
            if (System.IO.Path.GetFileNameWithoutExtension(pak)
                    .Equals(modName, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                // Disposed before returning. A reader left open holds the file, and holding a file
                // in the folder we are about to write to is the exact fault that made installing
                // fail with "file in use" once already.
                using var reader = new PakFileReader(pak, new VersionContainer(game));
                reader.Mount(StringComparer.OrdinalIgnoreCase);
                foreach (var (path, file) in reader.Files)
                    if (path.EndsWith("AssetRegistry.bin", StringComparison.OrdinalIgnoreCase))
                        found.Add(new RegistryClash(pak, path, file.Size));
            }
            catch (Exception e)
            { log?.Invoke($"   could not read {System.IO.Path.GetFileName(pak)}: {e.Message}"); }
        }
        return found;
    }

    /// <summary>Every package id a container's store lists. Empty when it carries no store.</summary>
    public static List<ulong> PackageIdsIn(string utoc, EGame game)
    {
        // Neither the directory index nor a Mount: the package store is in the TOC, and asking for
        // the index rules out exactly the container that most needs checking -- global.utoc has
        // none, so requiring it turned the engine's own container into an unreadable one.
        //
        // DISPOSED, because the reader holds the container's .ucas open and a held file cannot be
        // deleted: it was left to the GC, so reading another mod's container to compare ids left that
        // mod un-removable (CanDelete asks for exclusive access) until a collection happened to run. The
        // early containers made it visible -- the next thing done to one is often removing it.
        using var reader = new IoStoreReader(utoc, EIoStoreTocReadOptions.Default, new VersionContainer(game));

        var want = ContainerHeader.ChunkIdFor(reader.TocResource.Header.ContainerId.Id);
        var chunk = reader.TocResource.ChunkIds.FirstOrDefault(
            c => ContainerHeader.ChunkIdFor(c.ChunkId).AsSpan().SequenceEqual(want) &&
                 c.ChunkType == ContainerHeader.ChunkType);
        if (chunk.ChunkId == 0 && chunk.ChunkType == 0) return new List<ulong>();

        return ContainerHeader.Read(reader.Read(chunk)).PackageIds.ToList();
    }

    // ------------------------------------------------------------------ install / remove

    /// <summary>
    /// Put the build in the game, in the layout the BUILD produced.
    ///
    /// Which layout is not an install-time choice and is deliberately not offered as one. The two
    /// differ in what is inside the .pak: a plugin build's holds a descriptor and its own small
    /// registry, a legacy build's holds a copy of the game's whole registry at the game's own
    /// path. Dropping a plugin build in ~mods installs a pak with no registry the game will read;
    /// putting a legacy build in a Mods folder installs one with no descriptor to find it. Both
    /// mount and do nothing, which is the failure this codebase has spent the most hours on. So
    /// the build says, by handing back the loose files it wrote, and the install obeys.
    ///
    /// Throws with a sentence a person can act on, rather than returning a flag, because every
    /// reason to stop here is a reason the caller cannot fix.
    /// </summary>
    public static string Install(ModBuild.Result built, string paks, string modName,
                                 Action<string>? log = null)
    {
        var sources = new[] { built.Pak, built.Utoc, built.Ucas };
        // The early half is checked with the rest, not later: a mod half-installed because its second
        // container was missing is a mod whose override is silently ignored.
        var absent = sources.Concat(built.Early is { } early ? new[] { early.Pak, early.Utoc, early.Ucas } : [])
                            .Where(f => !File.Exists(f)).ToList();
        if (absent.Count > 0)
            throw new FileNotFoundException(
                "the build did not produce " + string.Join(", ", absent.Select(Path.GetFileName)) +
                " -- a pak with no .utoc and .ucas beside it never mounts, so this would install " +
                "a mod that silently does nothing.");

        if (!CanWrite(paks, modName, out var blocked))
            throw new IOException(
                $"{Path.GetFileName(blocked)} is in use -- close the game first. Writing over a " +
                "container it has open leaves a file that is neither version.");

        var into = built.Plugin is { } plugin
            ? InstallPlugin(built, plugin, paks, modName, log)
            : InstallLegacy(sources, paks, modName, log);
        if (built.Plugin is not null) InstallEarly(built, paks, modName, log);

        // Recorded HERE, at the moment this machine puts the mod where the browser will find it.
        // That record is what keeps your own work browsable once the browser starts treating other
        // people's mods as read-only -- see ModOwnership. It goes in the user's settings, never
        // into the mod, because anything inside a mod folder travels with a copy of it.
        try { ModOwnership.Claim(modName); }
        catch (Exception e) { log?.Invoke($"      could not record this as yours: {e.Message}"); }

        return into;
    }

    /// <summary>
    /// A plugin folder in the project's Mods directory. Confirmed in game -- and confirmed for
    /// OVERRIDES too (2026-08-30): a `_P` container here wins a lookup against the game's own base
    /// containers, so a mod can replace a shipped asset from its own folder. It does not need
    /// `~mods`, `Content/Paks`, or UE4SS.
    ///
    /// This IS the mod runtime's own folder -- see PluginsFolderName. An earlier comment here said
    /// the runtime is never called, which was wrong.
    /// </summary>
    private static string InstallPlugin(ModBuild.Result built, ModBuild.PluginFiles plugin,
                                        string paks, string modName, Action<string>? log)
    {
        var missing = new[] { plugin.Uplugin, plugin.Registry }.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0)
            throw new FileNotFoundException(
                "the build did not produce " + string.Join(", ", missing.Select(Path.GetFileName)) +
                " -- a mod folder without both is not a plugin, and the plugin manager skips it " +
                "without a word.");

        if (PluginDirectory(paks, modName) is not { } dir)
            throw new DirectoryNotFoundException(PluginsDirectory(paks) is null
                ? $"{paks} is not inside a project -- nothing above it holds a Content folder, so " +
                  "there is nowhere a Mods folder would be read from."
                : $"'{modName}' cannot name a folder, and a mod is installed as a folder named " +
                  "after it.");

        var name = PluginName(modName);

        // The build's own idea of its name has to agree with the one being installed under.
        //
        // They can disagree: the panel keeps the last build result when a different mod is picked
        // from the list, so a result for mod A can reach an install for mod B. The descriptor
        // inside `plugin.Uplugin` names A and its pak mounts at A's path -- writing it into B's
        // folder produces a plugin that is neither, and the stale sweep below would first delete
        // B's real containers to make room for it. Cheap to check, and the only place both names
        // are in scope.
        if (!plugin.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"this build is for '{plugin.Name}' and would be installed as '{name}' -- build " +
                $"'{name}' first. Installing it would write {plugin.Name}'s descriptor into " +
                $"{name}'s folder, over whatever is already there.");

        var contentPaks = Path.Combine(dir, "Content", "Paks");
        Directory.CreateDirectory(contentPaks);

        // Containers from an earlier build under a different name. They mount too, so leaving one
        // behind installs two mods and calls it one.
        foreach (var stale in Directory.EnumerateFiles(contentPaks)
                     .Where(f => Trio.Contains(Path.GetExtension(f).TrimStart('.'),
                                               StringComparer.OrdinalIgnoreCase))
                     .Where(f => !Path.GetFileNameWithoutExtension(f)
                                      .Equals(modName, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            File.Delete(stale);
            log?.Invoke($"   removed stale {Path.GetFileName(stale)}");
        }

        foreach (var (src, dst) in new[]
                 {
                     (plugin.Uplugin, Path.Combine(dir, name + ".uplugin")),
                     (plugin.Registry, Path.Combine(dir, "AssetRegistry.bin")),
                     (built.Pak,  Path.Combine(contentPaks, modName + ".pak")),
                     (built.Utoc, Path.Combine(contentPaks, modName + ".utoc")),
                     (built.Ucas, Path.Combine(contentPaks, modName + ".ucas")),
                 })
            Copy(src, dst, log);

        // Anything else the plugin carries -- today its gameplay tag files, which live in
        // subfolders and so need those made first. A mod whose tags do not travel with it works on
        // the machine that built it and nowhere else, which is the worst way for this to fail.
        foreach (var (relative, absolute) in plugin.Extra ?? [])
        {
            var target = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Copy(absolute, target, log);
        }

        // The same mod also sitting in ~mods from before this layout existed. Both would load, and
        // the legacy one carries a whole copy of the game's registry -- so the older, worse copy of
        // this very mod would go on clobbering every other mod's registry on its behalf.
        foreach (var ext in Trio)
        {
            var old = Path.Combine(ModsDirectory(paks), $"{modName}.{ext}");
            if (!File.Exists(old)) continue;
            File.Delete(old);
            log?.Invoke($"   removed the older ~mods copy {Path.GetFileName(old)}");
        }

        return dir;
    }

    /// <summary>
    /// The early half (EarlyLoad): copied flat into ~mods, which the engine mounts before anything
    /// loads. A build with no early content removes a stale early trio of this mod, or the old copy of
    /// the game file it once changed keeps winning.
    /// </summary>
    private static void InstallEarly(ModBuild.Result built, string paks, string modName, Action<string>? log)
    {
        var mods = ModsDirectory(paks);
        var name = EarlyLoad.ContainerName(modName);
        if (built.Early is { } early)
        {
            Directory.CreateDirectory(mods);
            // The .utoc is what makes a container exist, and this is the engine's own scan folder, which
            // it reads at every launch. So it is the LAST file to arrive and, on a reinstall, the FIRST to
            // go: the old one would otherwise sit over a .ucas that is being overwritten, and a copy that
            // fails partway (disk full, antivirus) leaves that old index over new or half-written data.
            // With it removed up front, a failure at any point leaves a pak and maybe a .ucas, which mount
            // nothing, and the game uses its own copy of the file. A failed delete changes nothing at all.
            var utoc = Path.Combine(mods, name + ".utoc");
            if (File.Exists(utoc)) File.Delete(utoc);
            Copy(early.Pak, Path.Combine(mods, name + ".pak"), log);
            Copy(early.Ucas, Path.Combine(mods, name + ".ucas"), log);
            Copy(early.Utoc, utoc, log);
            log?.Invoke($"   {early.Packages.Count} game file(s) load before mods are mounted: installed into {mods}");
            return;
        }
        foreach (var ext in Trio)
        {
            var stale = Path.Combine(mods, $"{name}.{ext}");
            if (!File.Exists(stale)) continue;
            File.Delete(stale);
            log?.Invoke($"   removed the stale early file {Path.GetFileName(stale)} -- this build has none");
        }
    }

    private static string InstallLegacy(string[] sources, string paks, string modName,
                                        Action<string>? log)
    {
        var mods = ModsDirectory(paks);
        Directory.CreateDirectory(mods);
        foreach (var src in sources)
            Copy(src, Path.Combine(mods, modName + Path.GetExtension(src)), log);
        return mods;
    }

    private static void Copy(string src, string dst, Action<string>? log)
    {
        File.Copy(src, dst, overwrite: true);

        // A truncated copy mounts and then fails somewhere far from here.
        var wrote = new FileInfo(dst).Length;
        var expected = new FileInfo(src).Length;
        if (wrote != expected)
            throw new IOException($"{Path.GetFileName(dst)} copied {wrote:N0} of {expected:N0} bytes");
        log?.Invoke($"   {Path.GetFileName(dst),-32} {wrote,14:N0}");
    }

    /// <summary>
    /// Take a mod back out, in whichever layout it is in -- both, if it is somehow in both.
    /// Returns what was removed.
    ///
    /// The plugin folder goes with it. An empty folder holding a stale descriptor is a plugin the
    /// manager still finds and still fails to load, which is a worse state than uninstalled and
    /// looks like a successful uninstall from the file manager.
    /// </summary>
    public static List<string> Uninstall(string paks, string modName, Action<string>? log = null)
    {
        // DELETING IS NOT WRITING, and this used to ask the wrong question.
        //
        // CanWrite opens with FileShare.ReadWrite on purpose, so that THIS application merely
        // reading a mod does not stop it installing over it. That is right for a copy and wrong
        // here: a file somebody holds open cannot be deleted at all, so the guard passed and
        // Directory.Delete then threw -- and the only message anyone saw blamed the game, which
        // is usually not even running. The Studio mounts the Mods folder to show installed mods
        // in the browser, so the likeliest thing holding a mod open is the Studio itself.
        if (!CanDelete(paks, modName, out var blocked))
            throw new IOException(
                $"{Path.GetFileName(blocked)} is open, so it cannot be removed. " +
                "Close the game if it is running. If it is not, this application is holding it: " +
                "it reads installed mods to show them in the browser. Closing this application and " +
                "opening it again releases them, and then removing the mod will work.");

        var removed = new List<string>();

        if (PluginDirectory(paks, modName) is { } dir && Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                removed.Add(Path.GetRelativePath(dir, f));
            Directory.Delete(dir, recursive: true);
            log?.Invoke($"   removed {dir} and the {removed.Count} file(s) in it");
        }

        var mods = ModsDirectory(paks);
        foreach (var ext in Trio)
        {
            var f = Path.Combine(mods, $"{modName}.{ext}");
            if (!File.Exists(f)) continue;
            File.Delete(f);
            removed.Add(Path.GetFileName(f));
            log?.Invoke($"   removed {Path.GetFileName(f)}");
        }

        // The early half of a plugin mod, if it has one.
        foreach (var ext in Trio)
        {
            var f = Path.Combine(mods, $"{EarlyLoad.ContainerName(modName)}.{ext}");
            if (!File.Exists(f)) continue;
            File.Delete(f);
            removed.Add(Path.GetFileName(f));
            log?.Invoke($"   removed {Path.GetFileName(f)}");
        }
        return removed;
    }
}

using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;

namespace UEToolkit.Core;

/// <summary>
/// Which container an asset came from, and whether the person at the keyboard owns it.
///
/// THE PROBLEM THIS SOLVES IS NOT SECURITY, AND SAYING SO IS PART OF THE DESIGN.
///
/// A mod's contents cannot be protected. The game must read them, the game's key manager holds no
/// key for this title, and a mod cannot register one without running code in the game process --
/// which is the thing this whole toolchain exists to avoid. Even with a key it would have to ship
/// beside the data, because the person it would be protecting against is the person running the
/// game. FModel opens these containers today and will keep doing so.
///
/// What IS in our gift is not being the easy path. This studio mounts the project folder rather
/// than Content/Paks -- deliberately, so you can look at what you built without launching the game
/// -- and the effect is that every mod installed beside the game appears in the asset tree with an
/// Export tab next to it. "I opened it in the official mod tool and pressed export" is a far
/// shorter road than "I learned FModel and found a usmap", and closing that road is worth doing
/// even though the long road stays open.
///
/// So: a mod you built stays fully browsable, and a mod somebody else built is listed, named,
/// credited, and read-only.
/// </summary>
public static class ModOwnership
{
    public enum Kind
    {
        /// <summary>The game's own container.</summary>
        Game,

        /// <summary>A mod this machine has a project file for -- yours, and fully open.</summary>
        Mine,

        /// <summary>A mod installed here that this machine did not build.</summary>
        Other,
    }

    /// <summary>
    /// One mounted container. <paramref name="ModName"/> and <paramref name="Author"/> are read
    /// from the plugin descriptor beside it, so they are whatever that mod's author put there --
    /// treat them as a credit, never as proof of anything.
    /// </summary>
    public sealed record Container(string Name, string Path, int FileCount, Kind Kind,
                                   string? ModName, string? Author);

    public sealed class Map
    {
        public required List<Container> Containers { get; init; }
        public required Dictionary<string, Container> ByFile { get; init; }

        public int FileCount => ByFile.Count;

        /// <summary>Where a file came from, or null when nothing claimed it.</summary>
        public Container? ContainerOf(string filePath) =>
            ByFile.TryGetValue(filePath, out var c) ? c : null;

        /// <summary>
        /// Whether this file may be read out of the tool -- exported, used as a template, or
        /// copied into a mod.
        ///
        /// An UNATTRIBUTED file is readable. That is deliberate: the map is built from the
        /// containers' own directory indexes, and a file that fell out of it is a gap in our
        /// bookkeeping, not evidence of anything. Refusing on a gap would break the game's own
        /// assets the first time a container indexed something unusually.
        /// </summary>
        public bool IsReadable(string filePath) => ContainerOf(filePath)?.Kind != Kind.Other;

        /// <summary>Why a file cannot be used, in words meant for the person who clicked.</summary>
        public string? WhyLocked(string filePath)
        {
            var c = ContainerOf(filePath);
            if (c is null || c.Kind != Kind.Other) return null;
            var who = string.IsNullOrWhiteSpace(c.Author) ? "another modder" : c.Author;
            return $"This is part of {c.ModName ?? c.Name}, by {who}. Someone else's mod is " +
                   "listed here so you can see what is installed, not so it can be taken apart.";
        }
    }

    /// <summary>
    /// Build the map. One pass over every mounted container's file list, which is the same shape
    /// of walk ModBuild.StoreEntries already makes.
    /// </summary>
    /// <param name="paksDirectory">
    /// The folder the provider was pointed at. Needed because a mounted container knows only its
    /// own FILE NAME -- "HeroSpecs_P.utoc", with no directory -- so which mod it belongs to cannot
    /// be read off the reader and has to be found on disk.
    /// </param>
    /// <param name="modProjectFolder">
    /// Where this machine's mod projects live. A container is YOURS when a project in here builds
    /// to its name -- see <see cref="Mine"/> for why that is the test rather than a name pattern.
    /// </param>
    public static Map Of(IFileProvider provider, string? paksDirectory,
                         string? modProjectFolder = null)
    {
        var containers = new List<Container>();
        var byFile = new Dictionary<string, Container>(StringComparer.OrdinalIgnoreCase);
        if (provider is not IVfsFileProvider vfs)
            return new Map { Containers = containers, ByFile = byFile };

        var mine = Mine(modProjectFolder);
        var installed = Installed(paksDirectory);

        foreach (var reader in vfs.MountedVfs)
        {
            var file = reader.Name ?? "";
            var leaf = System.IO.Path.GetFileNameWithoutExtension(file);

            installed.TryGetValue(file, out var from);
            var kind = from is null ? Kind.Game
                     : mine.Contains(Normalise(leaf)) || mine.Contains(Normalise(from.Folder))
                       ? Kind.Mine
                     : Kind.Other;

            var container = new Container(leaf.Length > 0 ? leaf : file, from?.Path ?? file,
                                          reader.Files.Count, kind,
                                          from?.Name ?? from?.Folder, from?.Author);
            containers.Add(container);

            foreach (var path in reader.Files.Keys)
            {
                // A file offered by two containers is one patching the other, and the STRICTER
                // claim wins rather than the first. Someone else's mod overriding a game asset
                // still contains their work at that path, and "the game had it first" would hand
                // it straight back out.
                if (byFile.TryGetValue(path, out var held) && held.Kind >= container.Kind) continue;
                byFile[path] = container;
            }
        }

        // LOOSE FILES, which no container claims and the walk above therefore misses entirely.
        //
        // The provider indexes the game directory as well as its containers, so a mod's .uplugin
        // and its AssetRegistry.bin arrive as ordinary files with no reader behind them. Found by
        // MOUNT PATH instead: anything under a folder named after a mod belongs to that mod.
        //
        // The assets themselves were never at risk -- meshes and textures live inside the
        // containers -- but a gap that leaves some of a mod's files open is a gap somebody will
        // eventually put content in.
        var byFolder = containers
            .Where(c => c.Kind != Kind.Game)
            .GroupBy(c => c.ModName ?? c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (byFolder.Count > 0)
        {
            foreach (var path in provider.Files.Keys)
            {
                if (byFile.ContainsKey(path)) continue;
                if (FolderIn(path, installed) is not { } folder) continue;
                var owner = byFolder.Values.FirstOrDefault(
                    c => string.Equals(FolderOf(c), folder, StringComparison.OrdinalIgnoreCase));
                if (owner is not null) byFile[path] = owner;
            }
        }

        return new Map { Containers = containers, ByFile = byFile };
    }

    /// <summary>The mod folder this container came out of, for matching loose files to it.</summary>
    private static string FolderOf(Container c) =>
        System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(c.Path)) ?? "") ?? "");

    /// <summary>
    /// The mod folder a mounted path sits under, matched against the folders actually installed.
    ///
    /// Checked against the real list rather than by pattern: a game asset whose path happens to
    /// contain a folder called "Mods" -- and this game HAS one, /Game/Game/Customizations/Mods --
    /// would otherwise be attributed to a mod that does not exist and locked for no reason.
    /// </summary>
    private static string? FolderIn(string mountPath, Dictionary<string, OnDisk> installed)
    {
        var folders = installed.Values.Select(v => v.Folder)
                               .Distinct(StringComparer.OrdinalIgnoreCase);
        var parts = mountPath.Replace('\\', '/').Split('/');
        foreach (var folder in folders)
            for (var i = 0; i < parts.Length - 1; i++)
                if (parts[i].Equals("Mods", StringComparison.OrdinalIgnoreCase) &&
                    parts[i + 1].Equals(folder, StringComparison.OrdinalIgnoreCase))
                    return folder;
        return null;
    }

    private sealed record OnDisk(string Folder, string Path, string? Name, string? Author);

    /// <summary>
    /// Every container installed as a mod, keyed by the file name a mounted reader reports.
    ///
    /// Found by walking the Mods folder rather than by reading the containers, because the reader
    /// does not carry a path -- and matching on the name alone is sound here for the same reason
    /// two mods cannot share a container name in the first place: they would collide on disk.
    /// </summary>
    private static Dictionary<string, OnDisk> Installed(string? paksDirectory)
    {
        var found = new Dictionary<string, OnDisk>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(paksDirectory) || !Directory.Exists(paksDirectory))
            return found;

        // Both layouts this game accepts: the plugin folder the installer writes, and the legacy
        // ~mods drop the engine also scans. A mod in either is somebody's work.
        //
        // THE MODS FOLDER IS FOUND BY WALKING UP, NOT BY COMBINING ONTO THE PAKS PATH. It is a
        // SIBLING of Content, and a profile may point at the project OR at <project>/Content/Paks
        // -- the shipping one points at the latter. Combining "Mods" onto that yields
        // <project>/Content/Paks/Mods, which never exists, so `Installed` came back EMPTY, every
        // mounted container fell to `from is null -> Kind.Game`, and ModConflicts skipped it as
        // the game's own.
        //
        // MEASURED 2026-09-20 and it was silent: a container that demonstrably HELD the package
        // (holds=True) was reported as no conflict at all, so "another mod already changes this"
        // could never fire and the build's "sharing nothing" was asserted by a check that had
        // nothing to look at. The same shape as a reader that cannot see a mod's own table --
        // see [[a-counter-of-intent-cannot-witness-work]].
        var roots = new List<string>();
        if (ModInstall.PluginsDirectory(paksDirectory) is { } pluginsRoot) roots.Add(pluginsRoot);
        roots.Add(System.IO.Path.Combine(paksDirectory, ModInstall.PluginsFolderName));
        if (ModInstall.ResolvePaks(paksDirectory) is { } resolvedPaks)
            roots.Add(System.IO.Path.Combine(resolvedPaks, ModInstall.ModsFolderName));
        roots.Add(System.IO.Path.Combine(paksDirectory, "Content", "Paks", ModInstall.ModsFolderName));

        foreach (var modsRoot in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(modsRoot)) continue;

            foreach (var dir in Directory.EnumerateDirectories(modsRoot))
            {
                var folder = System.IO.Path.GetFileName(dir);
                var (name, author) = Descriptor(dir, folder);

                foreach (var container in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    var ext = System.IO.Path.GetExtension(container);
                    if (!ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".ucas", StringComparison.OrdinalIgnoreCase)) continue;
                    found[System.IO.Path.GetFileName(container)] =
                        new OnDisk(folder, container, name, author);
                }
            }

            // A loose pak dropped straight into ~mods, with no folder of its own.
            foreach (var container in Directory.EnumerateFiles(modsRoot))
            {
                var ext = System.IO.Path.GetExtension(container);
                if (!ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".ucas", StringComparison.OrdinalIgnoreCase)) continue;
                var leaf = System.IO.Path.GetFileNameWithoutExtension(container);
                found[System.IO.Path.GetFileName(container)] =
                    new OnDisk(leaf, container, null, null);
            }
        }
        return found;
    }

    /// <summary>
    /// The mod's own name and author, from the .uplugin the build ships beside its containers.
    ///
    /// Read off disk rather than out of the mount, because the descriptor is a LOOSE file in the
    /// plugin folder -- it is not inside any container and the provider has never seen it.
    /// </summary>
    private static (string? Name, string? Author) Descriptor(string dir, string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.uplugin"))
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
                return (Text(json, "FriendlyName") ?? folder, Text(json, "CreatedBy"));
            }
        }
        catch { /* a descriptor we cannot read simply contributes no credit */ }
        return (null, null);
    }

    /// <summary>
    /// Record that this installation made a mod, so its assets stay open in the browser.
    ///
    /// Called after a successful install rather than after a build: building writes files to a
    /// folder of your choosing and proves nothing about what is in the game, while installing is
    /// the moment this machine puts that mod where the browser will later find it.
    /// </summary>
    public static void Claim(string containerName)
    {
        var name = Normalise(containerName);
        if (name.Length == 0) return;
        var settings = AppSettings.Load();
        if (settings.MyMods.Any(m => Normalise(m) == name)) return;
        settings.MyMods.Add(containerName);
        settings.Save();
    }

    /// <summary>Forget a claim. The inverse of <see cref="Claim"/>, for a mod you removed.</summary>
    public static bool Unclaim(string containerName)
    {
        var name = Normalise(containerName);
        var settings = AppSettings.Load();
        var gone = settings.MyMods.RemoveAll(m => Normalise(m) == name);
        if (gone > 0) settings.Save();
        return gone > 0;
    }

    /// <summary>Everything this installation has claimed.</summary>
    public static IReadOnlyList<string> Claimed() => AppSettings.Load().MyMods;

    /// <summary>
    /// Names compare without their `_P` suffix or extension, because one mod is referred to three
    /// ways -- the plugin folder `HeroSpecs`, the container `HeroSpecs_P`, the file
    /// `HeroSpecs_P.utoc` -- and a claim recorded as one spelling has to match the other two.
    /// </summary>
    private static string Normalise(string name)
    {
        var raw = name.Trim();

        // THE RAW NAME, tested before any extension is taken off. An early container ("X_Early_P", or
        // a path to one of its files) belongs to the mod it was built with and claims as that mod, so
        // its key must be the plugin's key however it is spelled. Path.GetFileNameWithoutExtension
        // reads the last dot of a BARE name as an extension, so tested after it, "Sith_Assassin_v1.2_Early_P"
        // arrives as "Sith_Assassin_v1" and "Sith_Assassin_v1.2_Early_P.utoc" as "Sith_Assassin_v1.2":
        // two keys for one container. EarlyLoad strips only a container extension. The plugin's name
        // then goes down the SAME path a plugin's own name does, so the two always agree.
        if (EarlyLoad.IsEarlyContainer(raw)) raw = EarlyLoad.PluginOf(raw);

        var v = Path.GetFileNameWithoutExtension(raw);
        if (v.EndsWith("_P", StringComparison.OrdinalIgnoreCase)) v = v[..^2];
        return v.ToLowerInvariant();
    }

    /// <summary>
    /// The container names this machine can build, from the mod projects on disk.
    ///
    /// The test is "is there a project here that builds to this container", not "does the name look
    /// like mine" and not a marker written into the mod. Both of those are claims a copied mod would
    /// carry too. A project file is the one thing a person who merely installed a mod does not
    /// have -- and if they do have the source, they have the source, so treating it as theirs is
    /// correct rather than a hole.
    /// </summary>
    private static HashSet<string> Mine(string? modProjectFolder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Two independent signals, and either is enough. The install record covers a mod you built
        // here and no longer have open; the project files cover one you have the source for but
        // installed from another machine. Requiring both would lock people out of their own work,
        // which is a far worse failure than letting one through.
        foreach (var claimed in Claimed()) names.Add(Normalise(claimed));

        var folder = modProjectFolder ?? ModLibrary.DefaultFolder;
        if (!Directory.Exists(folder)) return names;

        foreach (var entry in ModLibrary.List(folder))
        {
            if (entry.Unreadable is not null) continue;
            try
            {
                var mod = ModProject.Load(entry.Path);
                var container = mod.ContainerName;
                names.Add(Normalise(container));
                names.Add(container);
                // The plugin FOLDER drops the _P the container carries, and it is what the installed
                // mod is filed under -- so both spellings have to be recognised, or a mod you built
                // reads as somebody else's.
                names.Add(container.EndsWith("_P", StringComparison.Ordinal)
                          ? container[..^2] : container);
                if (!string.IsNullOrWhiteSpace(mod.Name)) names.Add(mod.Name);
            }
            catch { /* a project that will not load cannot claim a container */ }
        }
        return names;
    }

    private static string? Text(System.Text.Json.Nodes.JsonNode? node, string key) =>
        node?[key]?.GetValue<string>() is { Length: > 0 } s ? s : null;
}

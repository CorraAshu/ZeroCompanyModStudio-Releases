using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;

namespace UEToolkit.Core;

/// <summary>
/// Which OTHER installed mods already change an asset this mod is about to change.
///
/// Nothing warned about this, and it mattered very little until it suddenly mattered a lot. For
/// most of this tool's life a mod ADDED things -- a duplicated helmet, a new weapon, a
/// specialisation of its own -- and two mods adding different things cannot collide. Then
/// replacements shipped, and then table editing, and both of those change the GAME'S OWN asset in
/// place. Two mods that replace the same helmet, or edit the same cost table, now ship the same
/// package id, and the container mounted last simply wins.
///
/// **The failure has no symptom.** Both mods install, both verify, both read back clean, and one
/// of them silently does nothing. The person who notices is the player, who reports it to whichever
/// author they happen to know -- and neither author can see the problem from inside their own
/// project, because each one's mod works perfectly on its own.
///
/// Found by CHUNK ID rather than by file path, because that is what the game matches on and what
/// every other lookup here uses: an override ships the game's own package id deliberately, which is
/// the whole mechanism, and it is also exactly what makes two of them indistinguishable to the
/// loader. Two mods can have entirely different folder names, different file paths and different
/// display names, and still be the same id.
/// </summary>
public static class ModConflicts
{
    /// <summary>
    /// One installed mod that also holds this package. <paramref name="Mine"/> is true when this
    /// machine has a project for it -- which usually means it is the very mod being edited, and
    /// the caller drops it.
    /// </summary>
    public sealed record Holder(string Container, string ModName, string? Author, bool Mine);

    /// <summary>
    /// Every installed mod holding <paramref name="packageName"/>, the game itself excluded.
    ///
    /// <paramref name="ownership"/> is the map the application already builds once at mount, so
    /// this costs one pass over the mounted containers' chunk tables and nothing else. Returns an
    /// empty list rather than throwing: a caller drawing a warning wants "nobody else has it" as
    /// an answer it can act on.
    /// </summary>
    public static List<Holder> Of(IFileProvider provider, string packageName,
                                  ModOwnership.Map? ownership)
    {
        var found = new List<Holder>();
        if (provider is not IVfsFileProvider vfs || packageName.Length == 0) return found;

        try
        {
            var wanted = PackageId.Of(packageName);
            foreach (var reader in vfs.MountedVfs.OfType<IoStoreReader>())
            {
                if (!reader.TocResource.ChunkIds.Any(c => c.ChunkId == wanted)) continue;

                var leaf = Path.GetFileNameWithoutExtension(reader.Name ?? "");
                var container = ownership?.Containers.FirstOrDefault(
                    c => string.Equals(c.Name, leaf, StringComparison.OrdinalIgnoreCase));

                // THE GAME HOLDING IT IS THE NORMAL CASE, not a conflict -- an override exists to
                // change the game's own asset, so the game's container is always one of the
                // holders, and reporting it would make the warning meaningless.
                //
                // WITHOUT AN OWNERSHIP MAP the `_P` suffix decides, and that is not a guess: a
                // patch container is how every mod ships and it is the same rule PristineRead
                // already follows to avoid reading a mod's copy as the game's. The map is only
                // ever an improvement on the NAME -- it knows what the plugin descriptor called
                // itself -- so the build can ask this question without being handed one.
                var isMod = container is null
                    ? leaf.EndsWith("_P", StringComparison.OrdinalIgnoreCase)
                    : container.Kind != ModOwnership.Kind.Game;
                if (!isMod) continue;

                found.Add(new Holder(container?.Name ?? leaf,
                                     container?.ModName ?? Trim(leaf),
                                     container?.Author,
                                     container?.Kind == ModOwnership.Kind.Mine));
            }
        }
        catch (Exception) { return found; }

        return found;
    }

    /// <summary>
    /// Every INSTALLED mod holding this package, read from the containers ON DISK.
    ///
    /// THE MOUNTED PROVIDER CANNOT ANSWER THIS IN THE STUDIO, which is why this exists beside
    /// <see cref="Of"/>. The Studio mounts the game WITHOUT installed mods on purpose -- reading
    /// its own output back as the game is the own-mod trap that `PristineFiles` exists for -- so
    /// `MountedVfs` holds six game containers and no mod, and a scan of it can only ever say
    /// "nobody else has this". The question is about what is INSTALLED, not what is mounted, so
    /// it is asked of the folder.
    ///
    /// Cached against a cheap signature of the mods tree -- how many containers and the newest
    /// write time -- so installing a mod mid-session invalidates it without anybody remembering
    /// to say so, and the common case costs one directory walk.
    /// </summary>
    public static List<Holder> OnDisk(string? paksDirectory, CUE4Parse.UE4.Versions.EGame game,
                                      string packageName)
    {
        var found = new List<Holder>();
        if (string.IsNullOrWhiteSpace(paksDirectory) || packageName.Length == 0) return found;

        var index = IndexFor(paksDirectory, game);
        if (index is null) return found;
        return index.TryGetValue(PackageId.Of(packageName), out var holders)
            ? new List<Holder>(holders)
            : found;
    }

    private static readonly object _indexLock = new();
    private static string _indexKey = "";
    private static Dictionary<ulong, List<Holder>>? _index;

    /// <summary>The package ids every installed container holds, by mod. Null when there is no Mods folder.</summary>
    private static Dictionary<ulong, List<Holder>>? IndexFor(string paksDirectory,
                                                             CUE4Parse.UE4.Versions.EGame game)
    {
        var roots = new List<string>();
        if (ModInstall.PluginsDirectory(paksDirectory) is { } plugins) roots.Add(plugins);
        roots.Add(Path.Combine(paksDirectory, ModInstall.PluginsFolderName));
        if (ModInstall.ResolvePaks(paksDirectory) is { } paks)
            roots.Add(Path.Combine(paks, ModInstall.ModsFolderName));
        roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        if (roots.Count == 0) return null;

        List<string> tocs;
        try
        {
            tocs = roots.SelectMany(r => Directory.EnumerateFiles(r, "*.utoc", SearchOption.AllDirectories))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { return null; }

        string key;
        try
        {
            var newest = tocs.Count == 0 ? 0L
                       : tocs.Max(f => File.GetLastWriteTimeUtc(f).Ticks);
            key = $"{string.Join("|", roots)}#{tocs.Count}#{newest}#{game}";
        }
        catch (Exception) { key = string.Join("|", roots) + "#?"; }

        lock (_indexLock)
        {
            if (_index is not null && key == _indexKey) return _index;

            var built = new Dictionary<ulong, List<Holder>>();
            foreach (var toc in tocs)
            {
                var leaf = Path.GetFileNameWithoutExtension(toc);
                // <Mods>/<Name>/Content/Paks/<Name>_P.utoc -- the MOD is the folder, because a
                // container three levels down is not a name anybody recognises.
                var paksDir = Path.GetDirectoryName(toc);
                // An early container is the second half of a plugin mod, and named after it.
                var modName = EarlyLoad.IsEarlyContainer(leaf) ? EarlyLoad.PluginOf(leaf) : leaf;
                if (paksDir is not null &&
                    Path.GetFileName(paksDir).Equals("Paks", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetDirectoryName(Path.GetDirectoryName(paksDir)) is { } folder)
                    modName = Path.GetFileName(folder);

                try
                {
                    // Disposed: the reader holds the .ucas open, and a held file cannot be removed. It is
                    // only read for its chunk ids.
                    using var reader = new IoStoreReader(
                        toc, CUE4Parse.UE4.IO.Objects.EIoStoreTocReadOptions.ReadDirectoryIndex,
                        new CUE4Parse.UE4.Versions.VersionContainer(game));
                    var holder = new Holder(leaf, modName, null, false);
                    foreach (var c in reader.TocResource.ChunkIds ?? [])
                    {
                        if (!built.TryGetValue(c.ChunkId, out var list))
                            built[c.ChunkId] = list = new List<Holder>();
                        if (!list.Any(h => h.Container.Equals(leaf, StringComparison.OrdinalIgnoreCase)))
                            list.Add(holder);
                    }
                }
                catch (Exception) { /* a container this cannot open is one it cannot speak for */ }
            }

            _index = built;
            _indexKey = key;
            return built;
        }
    }

    /// <summary>
    /// The same list with the mod BEING EDITED taken out of it.
    ///
    /// A mod that has been built and installed once holds every package it overrides, so without
    /// this every override in an installed project reports itself as a conflict with itself --
    /// the same shape of mistake as authoring reading its own installed output as the game.
    ///
    /// Matched on the container and the mod name because the two are not always the same string:
    /// a mod ships as <c>&lt;Name&gt;_P.utoc</c> inside a folder named for the mod, and which of
    /// those the map recorded depends on whether a plugin descriptor was found beside it.
    /// </summary>
    public static List<Holder> Besides(IFileProvider provider, string packageName,
                                       ModOwnership.Map? ownership, string thisMod,
                                       string? paksDirectory = null)
    {
        // BOTH SOURCES, because neither is enough on its own. A caller that mounted the mods
        // (--withmods, the ref profiles) gets them from the provider; the Studio never mounts
        // them and gets them from the folder. Merged on the container name so a caller with both
        // does not report one mod twice.
        var found = Of(provider, packageName, ownership);
        if (paksDirectory is not null)
            foreach (var h in OnDisk(paksDirectory, provider.Versions.Game, packageName))
                if (!found.Any(x => Same(x.Container, h.Container))) found.Add(h);

        return found.Where(h => !Same(h.Container, thisMod) && !Same(h.ModName, thisMod)).ToList();
    }

    /// <summary>
    /// What to tell somebody, in one sentence they can act on.
    ///
    /// A WARNING AND NOT A REFUSAL, on the author's instruction: "the warning should still let
    /// them go through with it, just in case they are trying to make 2 different mods or
    /// something". Two mods that change the same asset is a perfectly reasonable thing to be
    /// making -- what is not reasonable is finding out during a play session that one of them
    /// appears to do nothing.
    /// </summary>
    public static string Sentence(List<Holder> holders)
    {
        if (holders.Count == 0) return "";
        var who = string.Join(", ", holders.Select(h => h.ModName));
        return $"{who} already change(s) this asset. " +
               "Both will build and both will install, but the game only reads one of them -- " +
               "whichever it mounts last. Take the other mod out of your Mods folder before you " +
               "test this one in game, or you will not be able to tell which one you are seeing.";
    }

    /// <summary>
    /// Whether two names mean the same mod, allowing for the <c>_P</c> a patch container carries
    /// and for the spaces and punctuation a display name has and a file name does not.
    /// </summary>
    private static bool Same(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || b.Length == 0) return false;
        return string.Equals(Plain(a), Plain(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string name) =>
        EarlyLoad.IsEarlyContainer(name) ? EarlyLoad.PluginOf(name)
        : name.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ? name[..^2] : name;

    /// <summary>The comparable form of a name: no _P, no spaces, no punctuation.</summary>
    private static string Plain(string name) =>
        new(Trim(name).Where(char.IsLetterOrDigit).ToArray());
}

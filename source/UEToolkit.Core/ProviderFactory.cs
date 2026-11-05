using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Compression;
using CUE4Parse_Conversion.Textures.BC;

namespace UEToolkit.Core;

/// <summary>
/// Opens a game from a <see cref="GameProfile"/>. Centralises the native-library setup that
/// is easy to forget and fails in confusing ways when missed.
/// </summary>
public static class ProviderFactory
{
    private static bool _nativesReady;

    /// <summary>
    /// Detex decodes BC6H/BC7. Without it, texture decode throws
    /// "Detex decompression failed: not initialized" from deep inside the exporter.
    /// Oodle is required for most UE5 IoStore payloads.
    /// </summary>
    public static async Task InitNativesAsync(string dataDir)
    {
        if (_nativesReady) return;
        Directory.CreateDirectory(dataDir);

        var detex = Path.Combine(dataDir, DetexHelper.DLL_NAME);
        if (!File.Exists(detex)) await DetexHelper.LoadDllAsync(detex);
        DetexHelper.Initialize(detex);

        string? oodle = Path.Combine(dataDir, OodleHelper.OodleFileName);
        if (!File.Exists(oodle)) await OodleHelper.DownloadOodleDllAsync(ref oodle);
        await OodleHelper.InitializeAsync(oodle);

        _nativesReady = true;
    }

    /// <summary>
    /// Every directory to mount, root first: the Paks folder and each folder beneath it except
    /// the one mods are installed into. Each is then searched TopDirectoryOnly, which together
    /// covers the same files a recursive search would minus the excluded subtree.
    ///
    /// When there is no mods folder -- every game but a modded one -- this returns just the root
    /// and the caller falls back to a plain recursive search, so the ordinary path is exactly what
    /// it always was.
    /// </summary>
    /// <summary>
    /// Mount installed mods as well as the game.
    ///
    /// OFF by default, and the default is the important half: the editor writes into the folder a
    /// mod is installed to, and a provider holding those files open made installing fail with
    /// "file in use" -- a fault that looked like a permissions problem and was the tool competing
    /// with itself.
    ///
    /// ON is for looking at what was shipped. Rendering an installed mod in this tool's own
    /// viewport turns a test cycle from "ask the user to launch the game and describe what they
    /// see" into a screenshot, which is the difference between debugging a mesh and guessing at
    /// it. Never set while a build or install can run.
    /// </summary>
    public static bool IncludeMods;

    private static (DirectoryInfo Root, DirectoryInfo[] Extra, SearchOption How) MountPlan(
        string paksDirectory)
    {
        var root = new DirectoryInfo(paksDirectory);
        if (!root.Exists) return (root, [], SearchOption.AllDirectories);
        if (IncludeMods)
        {
            // The project's Mods folder is named EXPLICITLY, because it is a sibling of Content
            // and not under Paks at all -- a recursive search from here cannot reach it. Left out,
            // --withmods would mount the game plus whatever is still in ~mods and quietly report
            // that a plugin mod's assets are absent, which is the same answer it gives for a mod
            // that failed to build.
            //
            // Resolved first, because a profile is allowed to point at the game root -- the
            // provider searches downward and does not care -- and the project is found by walking
            // UP from the Paks folder. Starting that walk at the game root climbs out of the game
            // entirely, into Steam's own folders.
            var mods = ModInstall.PluginsDirectory(FolderPicker.ResolvePaks(root.FullName));
            if (mods is not null && Directory.Exists(mods))
                return (root, [new DirectoryInfo(mods)], SearchOption.AllDirectories);
            return (root, [], SearchOption.AllDirectories);
        }

        List<DirectoryInfo> all;
        try { all = root.EnumerateDirectories("*", SearchOption.AllDirectories).ToList(); }
        catch (Exception) { return (root, [], SearchOption.AllDirectories); }

        var kept = all.Where(d => !IsUnderModsFolder(d, root)).ToArray();

        // Nothing to exclude -- every game but a modded one. One recursive search, exactly as
        // before, so the ordinary path is untouched.
        if (kept.Length == all.Count) return (root, [], SearchOption.AllDirectories);

        // Something WAS excluded, so each directory is named and searched shallowly. This branch
        // must also cover the case where the mods folder is the ONLY subdirectory: an empty
        // `kept` still has to mean "the root, alone", never "nothing to exclude" -- which is what
        // a plain count of the list would have said, quietly mounting the mods back in.
        return (root, kept, SearchOption.TopDirectoryOnly);
    }

    /// <summary>
    /// Is this directory inside a folder mods are installed into?
    ///
    /// BOTH NAMES, and for four months it only checked one. `ModsFolderName` is `~mods`, where a
    /// loose pak goes; `PluginsFolderName` is `Mods`, which is where THIS TOOL INSTALLS -- and the
    /// comment on CreateAsync has always claimed that folder is excluded. It was not. The exclusion
    /// appeared to work only because a profile pointing at `Content\Paks` cannot see `Mods` anyway:
    /// it is a sibling of `Content`, not a child of `Paks`.
    ///
    /// The Studio writes `settings/active-profile.json` with whatever folder the author picked on
    /// first run, and picking the GAME FOLDER is the obvious thing to do -- the author's says
    /// `...\SWZeroCompany`. From that root `Mods\<mod>\Content\Paks` IS reachable, so the Studio
    /// mounted every mod the author had installed and read it as the game. Measured 2026-09-13:
    /// `CPD_TacticalSpec_Anakin` came back `from HeroSpecs_P.utoc` wearing the author's own edit.
    ///
    /// WHAT THAT COSTS, beyond a browser that lies about what the game ships: MissionBuild computes
    /// every edit as `donor.Old -> your.New` and takes the donor from this provider, while the
    /// bytes come from a pristine read. Rebuild a mission mod over its own installed copy and the
    /// donor ALREADY holds the new value, the comparison finds no change, and the build ships an
    /// override that edits nothing -- silently, with a clean verifier.
    ///
    /// <see cref="IncludeMods"/> is the way to mount them ON PURPOSE, and it names the plugins
    /// folder explicitly rather than relying on a recursive search reaching it.
    /// </summary>
    private static bool IsUnderModsFolder(DirectoryInfo dir, DirectoryInfo root)
    {
        for (var d = dir; d is not null && !d.FullName.Equals(root.FullName, StringComparison.OrdinalIgnoreCase);
             d = d.Parent)
            if (d.Name.Equals(ModInstall.ModsFolderName, StringComparison.OrdinalIgnoreCase) ||
                d.Name.Equals(ModInstall.PluginsFolderName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static async Task<DefaultFileProvider> CreateAsync(GameProfile profile, string dataDir)
    {
        profile.Validate();
        await InitNativesAsync(dataDir);

        // The folder this tool INSTALLS INTO is not mounted, and that is a fix rather than an
        // omission. Two separate faults came from mounting it, each worked around on its own
        // before the shared cause was named:
        //
        //   a locked file     the app holds every container it mounts. Installing a second build
        //                     of a mod meant overwriting a file the toolkit itself had open, and
        //                     the error blamed the game, which was not even running.
        //
        //   a stacked registry a mod pak carries an AssetRegistry.bin at the SAME path as the
        //                     game's, and being a _P patch it wins. The next build read its own
        //                     output back in and appended to it -- which is what
        //                     ModBuild.PristineRegistry undoes, and why ModBuild also has to
        //                     exclude already-deployed parts from template selection.
        //
        // Reading the BASE GAME is what a mod tool wants regardless: a template is something the
        // game ships, not something a person happens to have installed. Both workarounds stay --
        // a mod dropped straight into Paks rather than ~mods is still possible, and is still
        // theirs to catch.
        var (mountRoot, mountExtra, mountHow) = MountPlan(profile.PaksDirectory);
        var provider = new DefaultFileProvider(
            mountRoot, mountExtra, mountHow,
            new VersionContainer(profile.Game),
            StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(profile.MappingsFile))
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(profile.MappingsFile);

        // Without this, FNaniteResources.LoadAllPages is a silent no-op: Nanite meshes come
        // through as their cooked non-Nanite proxy, which for these assets is a stub. A
        // helmet reads as 106 verts and a hat as a flat 20-vertex plane while
        // the real geometry sits unread in the streaming pages.
        provider.ReadNaniteData = true;

        provider.Initialize();

        if (!string.IsNullOrWhiteSpace(profile.AesKey))
            await provider.SubmitKeyAsync(new FGuid(), new FAesKey(profile.AesKey));
        foreach (var (guid, key) in profile.ExtraAesKeys)
            await provider.SubmitKeyAsync(new FGuid(guid), new FAesKey(key));

        provider.Mount();
        provider.PostMount();
        return provider;
    }

}

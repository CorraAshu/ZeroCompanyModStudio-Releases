using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;

namespace UEToolkit.Core;

/// <summary>
/// MOUNTING THE GAME, USING NOTHING BUT UEToolkit.Core.
///
/// The toolkit's own `AppState.LoadGameAsync` does this and more, but it lives in UEToolkit.App
/// beside the ImGui interface -- referencing that project to get at it would drag the whole old
/// renderer in. The sequence itself is short and is the part that matters; everything expensive
/// (the provider, the natives, the index) is Core and is shared.
/// </summary>
public static class GameMount
{
    // IN CORE because THREE applications now want it: the Level Editor, the Avalonia Mod Studio,
    // and anything else that needs the game open without dragging UEToolkit.App's ImGui interface
    // along. Nothing in here was ever UI.

    public static string DataDir => Path.Combine(AppContext.BaseDirectory, ".data");

    /// <summary>
    /// Walk up from the executable looking for profiles/&lt;name&gt;, so this works from
    /// bin/Release/net10.0/win-x64 as well as from a published layout.
    /// </summary>
    public static string? FindProfile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "profiles", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public sealed record Mounted(IFileProvider Provider, AssetIndex Index, GameProfile Profile, string Summary);

    public static async Task<Mounted> OpenAsync(string profilePath, Action<string> say)
    {
        say($"reading {Path.GetFileName(profilePath)}");
        var profile = GameProfile.Load(profilePath);

        // Game-specific behaviour comes from the profile, exactly as it does in the toolkit.
        // Validate() inside CreateAsync refuses a missing .usmap, which matters more here than
        // almost anywhere: on this game a wrong mappings path does not error, it silently yields
        // garbage properties, and every actor would read as empty.
        UvAtlas.Settings = profile.Atlas;
        AssetIndex.Rules = profile.Classify;
        GameModules.ProfileLoaded(profile);

        say($"mounting {profile.PaksDirectory}");
        var provider = await ProviderFactory.CreateAsync(profile, DataDir);

        say("indexing");
        var index = AssetIndex.Build(provider, profile.ExcludePaths);

        // THE ATLAS LIBRARY, WHICH THIS APP NEVER LOADED.
        //
        // `TextureCache` asks `AtlasLibrary.Active` how a texture is laid out and falls back to
        // DERIVING the grid from whichever mesh it happens to be looking at -- and one mesh
        // cannot answer that question: a part covering a single tile fits every square grid of
        // the texture, so the guess comes out finer than the truth and the shader samples the
        // wrong column. That is a UV bug with no error message.
        //
        // UEToolkit.App loads it on mount and this app, written later, did not -- so the table
        // could be built and the editor would go on guessing, saying nothing. Building it was
        // necessary and, on its own, changed nothing at all.
        //
        // Measured on Zero Company, 18,997 meshes: ZERO atlas textures and 1,057 flat. So for
        // this game the table's whole content is "stop guessing, they are all flat", which is
        // exactly the case a per-mesh derivation gets wrong in the expensive direction.
        AtlasLibrary.Active = AtlasLibrary.Load(profile, index.FileCount, say);
        // A PROFILE WITH NO TILE ATLASES CANNOT BE MISLED BY A MISSING TABLE. UvAtlas.Grid returns a
        // flat 1x1 before it derives anything while Atlas.Enabled is false, so there is nothing
        // for the table to change -- and the public download never ships one, so the old warning
        // printed on every mount of it and said something was wrong when nothing was.
        var atlasSaid = AtlasLibrary.Active is { } al
            ? $"atlas library: {al.Grids.Count:N0} atlas texture(s), {al.Flat.Count:N0} flat, " +
              $"source [{al.Source}]"
            : !profile.Atlas.Enabled
                ? "atlas library: not needed (this game's profile has no tile atlases)"
                : "atlas library: NOT BUILT - the viewport derives each grid from one mesh, which is " +
                  "wrong for a part that covers a single tile (the table comes from a whole-game scan and none ships with this program)";
        say(atlasSaid);
        // AND TO STDOUT, because `say` reaches the in-app Log only. This is a silent-failure
        // line by nature -- a missing table changes no count and throws nothing, it just makes
        // some UVs quietly wrong -- so the one place it must not be invisible is the headless
        // run that exists to check it.
        Console.WriteLine(atlasSaid);

        var containers = provider is IVfsFileProvider vfs ? vfs.MountedVfs.Count : 0;
        var summary =
            $"{Path.GetFileName(profilePath)}   ·   {containers} containers   ·   " +
            $"{index.FileCount:N0} files   ·   mappings loaded   ·   mounted read-only";
        say($"indexed {index.FileCount:N0} files");
        return new Mounted(provider, index, profile, summary);
    }
}

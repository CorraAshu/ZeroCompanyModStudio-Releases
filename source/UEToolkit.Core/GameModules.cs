using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// Game-specific behaviour an APPLICATION brings with it, which Core does not own.
///
/// Core mounts whatever game it is given and reads it through the formats every Unreal game
/// shares. A title's OWN formats - a tag container with its own animation data, a character
/// assembled from a table nobody else has - are not Core's business, and the Mod Studio and the
/// Level Editor serve one game and need none of them. An application that does need one (the
/// multi-game toolkit) registers a module at startup, and the few places in Core that have a
/// gap of that shape ask the registered modules. With nothing registered every question gets
/// the neutral answer, so a Core without modules is exactly the Core a one-game application runs.
/// </summary>
public interface IGameModule
{
    /// <summary>A short name for logs.</summary>
    string Name { get; }

    /// <summary>
    /// A profile has been loaded and is about to be mounted. Read whatever the module keeps in
    /// it - the profile's reader settings hold keys Core has no property for, see
    /// <see cref="ReaderSettings.ExtraAs{T}"/>.
    /// </summary>
    void ProfileLoaded(GameProfile profile) { }

    /// <summary>
    /// Characters this module can read straight from the game's own data. Where it returns
    /// any, they win outright and <see cref="CharacterLibrary"/> never falls back to guessing
    /// from folders. Return nothing when the mounted game has no such data.
    /// </summary>
    IEnumerable<CharacterModel> ReadCharacterModels(IFileProvider provider, AssetIndex index,
                                                    Action<string>? log) => [];

    /// <summary>
    /// Extra rows for the height check a level script ends with (<see cref="LevelScript.Emit"/>):
    /// a label, a fragment the mesh name must contain, and one it must not ("" for none). Core's
    /// rows are the generic pieces every level has; a game whose levels are built from pieces
    /// recognisable by name adds its own here.
    /// </summary>
    IEnumerable<(string Label, string Key, string Not)> LevelChecks => [];
}

/// <summary>The modules an application registered. Empty unless the application adds some.</summary>
public static class GameModules
{
    private static readonly List<IGameModule> Registered = new();
    private static readonly object Gate = new();

    public static IReadOnlyList<IGameModule> All
    {
        get { lock (Gate) return Registered.ToArray(); }
    }

    /// <summary>Add a module; registering the same instance twice is harmless.</summary>
    public static void Register(IGameModule module)
    {
        lock (Gate)
            if (!Registered.Contains(module)) Registered.Add(module);
    }

    /// <summary>The registered module of a type, or null.</summary>
    public static T? Find<T>() where T : class, IGameModule => All.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Whatever loads a profile calls this once the profile is read and before anything
    /// consults it, so every module sees the settings of the game that is about to mount.
    /// </summary>
    public static void ProfileLoaded(GameProfile profile)
    {
        foreach (var module in All) module.ProfileLoaded(profile);
    }
}

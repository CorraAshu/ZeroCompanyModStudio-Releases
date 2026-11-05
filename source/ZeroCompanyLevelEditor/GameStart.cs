using UEToolkit.Core;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// WHERE THE GAME IS, decided the Mod Studio's way and by the Mod Studio's code - with no window in it.
///
/// The two programs ship in ONE folder and mean the same thing by "the game": what is remembered in
/// <c>settings\install.json</c> and <c>settings\active-profile.json</c> beside them. Pick the game once,
/// in either, and both know it; this class is the Level Editor's half of that, and it is
/// <see cref="GameSetup.Resolve"/> and nothing else (the Level Editor used to look for the named
/// <c>profiles\zero-company.json</c> alone, which in a download is the SHIPPED profile - blank on
/// purpose - so a downloader's first start failed with "profile not found").
///
/// "FIND IT" IS SEPARATE FROM "ASK THE USER", as in Core: this finds and remembers and never shows
/// anything; the window that asks (the Studio's own <c>GameFolderWindow</c>, linked) is shown by App
/// when this says it needs an answer. That split is also what lets the headless <c>--check</c> verb
/// (<see cref="LevelEditorVerbs"/>) run exactly this code with no window at all.
/// </summary>
internal static class GameStart
{
    /// <summary>
    /// Where installs come from. Null is the real thing (Steam's library list). The developer flag
    /// <c>--nofind</c> swaps in an empty list so the "where is the game" window can be walked through for
    /// real on a machine that has the game.
    /// </summary>
    public static Func<List<string>>? Finder = null;

    /// <summary>
    /// Everything short of asking. Never throws: a search that fails becomes the question, with the
    /// reason beside it, because looking for the game must not be what stops the editor opening.
    /// </summary>
    public static GameSetup.Outcome Resolve()
    {
#if DEV
        // --profile <name>: mount THAT named profile the old way, for the developer who points the
        // editor at a profile of his own. Without it a developer run goes through Resolve, exactly
        // as a download does - a profile beside the repository (a source checkout) is its first step.
        var args = Environment.GetCommandLineArgs();
        var asked = Array.IndexOf(args, "--profile");
        if (asked >= 0 && asked + 1 < args.Length && !args[asked + 1].StartsWith("--"))
        {
            var file = args[asked + 1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? args[asked + 1] : args[asked + 1] + ".json";
            var named = GameMount.FindProfile(file);
            return named is null
                ? new GameSetup.Outcome(GameSetup.Way.Failed, null, null,
                                        $"game: --profile {file} was not found beside the program or above it",
                                        Array.Empty<string>())
                : new GameSetup.Outcome(GameSetup.Way.NamedProfile, named, null,
                                        $"game: using profile {named}", Array.Empty<string>());
        }
        if (args.Contains("--nofind")) Finder = () => new List<string>();
#endif
        try { return GameSetup.Resolve(find: Finder, program: UEToolkit.Core.Branding.Product); }
        catch (Exception e)
        {
            return new GameSetup.Outcome(GameSetup.Way.NeedsAnswer, null, null,
                                         $"game: looking for it failed - {e.Message}",
                                         Array.Empty<string>(), null, e.Message);
        }
    }
}

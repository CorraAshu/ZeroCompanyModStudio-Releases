using CUE4Parse.FileProvider.Vfs;
using UEToolkit.Core;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// THE LEVEL EDITOR'S TWO HEADLESS QUESTIONS, ANSWERED BY ITS OWN EXECUTABLE WITH NO WINDOW.
///
///   ZeroCompanyLevelEditor.exe --version     the program and its version, then exit 0
///   ZeroCompanyLevelEditor.exe --check       do what the window does at start - find the game the way the Mod
///                                            Studio does, check the mappings against the installed build,
///                                            mount it read-only and list the levels - then say what happened
///
/// <c>--check</c> exits 0 when the editor would open on the game, 3 when it would have to ASK where the game
/// is (it does not ask: it never shows a window), 1 when it cannot be set up or the mount failed.
///
/// WHY THESE EXIST, and why they are not developer flags. The release has no background mode (that is a
/// developer feature, compiled out), so a published copy of this program can only be started two ways: with a
/// window, which belongs to whoever is at the keyboard, or as one of these. They are what a script, a support
/// request or the release's own unzip check can run from the downloaded folder and trust to open nothing -
/// the same reason the Mod Studio answers the Blender add-on's <c>--catalog</c> and <c>--import1</c> itself.
/// publish.ps1's stage check (tools/stagecheck.ps1, check Addon) looks for both in the published dll.
///
/// NO AVALONIA APP. <see cref="Program.Main"/> asks <see cref="Wants"/> first, runs this and exits before an
/// <c>AppBuilder</c> exists; nothing in here may build a window. This is a Windows-subsystem program (no console
/// of its own), so <see cref="HeadlessConsole.Attach"/> makes Console reach whoever started it - the same plumbing the
/// Studio's AddonVerbs uses, for the same reason.
/// </summary>
internal static class LevelEditorVerbs
{
    public static bool Wants(string[] args) => args.Contains("--version") || args.Contains("--check");

    /// <summary>Answer the verb and return the exit code. Never shows a window.</summary>
    public static int Run(string[] args)
    {
        var tidy = HeadlessConsole.Attach();
        try
        {
            Console.WriteLine($"{Branding.Product} {Branding.Version}");
            return args.Contains("--version") ? 0 : CheckAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Console.WriteLine($"ERROR {e.Message}");
            return 1;
        }
        finally
        {
            Console.Out.Flush();
            tidy?.Invoke();
        }
    }

    /// <summary>
    /// A release run with ZCMS_BACKGROUND=1 that is NOT one of the verbs above: say why nothing opened. That
    /// variable means "nobody may be interrupted" to every program in this repository, and this build has no
    /// background mode to honour it with, so it opens no window at all (exit code 2).
    /// </summary>
    public static int RefuseWindow(string[] args)
    {
        var tidy = HeadlessConsole.Attach();
        try
        {
            Console.WriteLine($"refused: ZCMS_BACKGROUND=1 forbids a window and this build has no background mode " +
                              $"(it is a developer feature), so {Branding.Product} opened nothing. " +
                              "--version and --check open no window and still run.");
            return 2;
        }
        finally
        {
            Console.Out.Flush();
            tidy?.Invoke();
        }
    }

    // ------------------------------------------------------------------ --check

    private static async Task<int> CheckAsync()
    {
        // 1. WHERE THE GAME IS: Core's Resolve, through the same GameStart the window uses.
        var setup = GameStart.Resolve();
        Console.WriteLine(setup.Say);

        if (setup.NeedsAnswer)
        {
            // The window would ask here. This does not: no window, ever.
            if (setup.Gone is not null) Console.WriteLine($"gone: the game is no longer at {setup.Gone}");
            if (setup.Problem is not null) Console.WriteLine($"problem: {setup.Problem}");
            foreach (var found in setup.Candidates) Console.WriteLine($"found: {found}");
            Console.WriteLine("NEEDS AN ANSWER: start the Level Editor (or the Mod Studio) once - it asks where the " +
                              "game is, one click - and then run this again.");
            return 3;
        }
        if (setup.ProfilePath is not { } profilePath)
        {
            Console.WriteLine("FAILED: the game cannot be set up (see the line above).");
            return 1;
        }
        Console.WriteLine($"profile: {profilePath}");

        // 2. IS THE USMAP FOR THIS BUILD? SAID and not remembered: remembering is what lets the window show
        // its "the game has changed" screen once, and a headless call that used it up would take that screen
        // away from the person it is for.
        if (GameSetup.CheckBuild(profilePath) is { } verdict)
            Console.WriteLine($"game build: {verdict.Say}{(verdict.WorthSaying ? "  [the window would show its warning]" : "")}");
        else
            Console.WriteLine("game build: could not be identified");

        // 3. MOUNT IT, read-only, the way the window does, and list the levels it offers.
        try
        {
            var mounted = await GameMount.OpenAsync(profilePath, line =>
            {
                if (!line.StartsWith("atlas library:", StringComparison.Ordinal)) Console.WriteLine(line);
            });
            if (mounted.Provider is IVfsFileProvider vfs)
                Console.WriteLine($"containers: {vfs.MountedVfs.Count}");
            Console.WriteLine($"files: {mounted.Index.FileCount:N0}");

            var maps = LevelReader.Maps(mounted.Index);
            Console.WriteLine($"levels: {maps.Count} map(s), {maps.Sum(m => m.Sublevels.Count):N0} sublevel(s) listed");
            Console.WriteLine("OK: the Level Editor would open on this game.");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAILED: could not open the game: {e.Message}");
            return 1;
        }
    }
}

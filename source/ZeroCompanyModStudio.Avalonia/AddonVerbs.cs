using CUE4Parse.FileProvider.Vfs;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE BLENDER ADD-ON'S TWO QUESTIONS, ANSWERED BY THE STUDIO'S OWN EXECUTABLE, WITH NO WINDOW.
///
///   ZeroCompanyModStudio.exe --profile zero-company --catalog &lt;out.json&gt;
///   ZeroCompanyModStudio.exe --profile zero-company --import1 &lt;cooked path&gt; &lt;outDir&gt; [gltf|psk]
///
/// Until 2.0.0 the add-on ran `UEToolkit.App.exe`, the multi-game toolkit's command line, which
/// shipped beside the Studio for this one purpose. A release that is only the Mod Studio should
/// not carry a second program, so the Studio answers these itself. The logic is
/// <see cref="AddonBackend"/> in UEToolkit.Core - the very code the toolkit's verbs call - so the
/// two can never answer differently; this class is only "find the game the way the Studio finds
/// it, then ask Core".
///
/// THESE ARE USER-FACING FLAGS, not developer ones: they are NOT inside "#if DEV", and the
/// published build answers them (publish.ps1's stage check looks for exactly that).
///
/// NO WINDOW AND NO AVALONIA APP. <see cref="Program.Main"/> asks <see cref="Wants"/> first, runs
/// this and exits before an <c>AppBuilder</c> exists. This exe is a Windows-subsystem program (it
/// has no console of its own), and a process like that still writes to a pipe its parent gave it -
/// which is how Python's <c>capture_output=True</c> reads it. When nobody gave it one but a person
/// typed the command at a console, the output is attached to THAT console (never a new one).
///
/// WHERE THE GAME IS comes from <see cref="GameSetup.Resolve"/> and nothing else, never from
/// `settings/active-profile.json` read raw. After an upgrade from 1.6.x the saved profile still
/// names the OLD usmap, which is still on disk (unzipping over a folder deletes nothing); Resolve
/// brings it up to date, and reading the file directly would mount the game with the wrong map and
/// read every property wrongly without an error. If Resolve cannot decide without asking a person,
/// this does NOT ask (no window, ever): it prints one line saying to start the Studio once, and
/// exits 1.
/// </summary>
internal static class AddonVerbs
{
    public static bool Wants(string[] args) => AddonBackend.Wants(args);

    /// <summary>Answer the verb and return the exit code. Never shows a window.</summary>
    public static int Run(string[] args)
    {
        var tidy = HeadlessConsole.Attach();
        try
        {
            var code = AddonBackend.RunAsync(args, () => MountAsync(args)).GetAwaiter().GetResult();
            return code ?? 1;
        }
        catch (Exception e)
        {
            // A verb that threw is a failure the add-on can show: its last stdout lines are what it
            // prints, and a stack trace on stderr is not.
            Console.WriteLine($"ERROR {e.Message}");
            return 1;
        }
        finally
        {
            Console.Out.Flush();
            tidy?.Invoke();
        }
    }

    // ------------------------------------------------------------------ the mount

    private static async Task<AddonBackend.MountResult> MountAsync(string[] args)
    {
        // `--profile <name>`: accepted because the add-on has always passed it, and checked because
        // this program serves one game. A name it does not know is the toolkit's own answer.
        var asked = args.SkipWhile(a => a != "--profile").Skip(1).FirstOrDefault();
        if (asked is not null && !asked.StartsWith("--"))
        {
            var bare = asked.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? asked[..^5] : asked;
            if (!bare.Equals(Path.GetFileNameWithoutExtension(GameSetup.NamedProfile), StringComparison.OrdinalIgnoreCase))
                return AddonBackend.MountResult.NoProfile();
            Console.WriteLine($"profile: {asked}");
        }

        GameSetup.Outcome setup;
        try { setup = GameSetup.Resolve(); }
        catch (Exception e) { return AddonBackend.MountResult.Cannot($"looking for the game failed: {e.Message}"); }

        if (setup.ProfilePath is not { } profilePath)
            return AddonBackend.MountResult.Cannot(NotReady(setup));

        Console.WriteLine(setup.Say);

        try
        {
            // The build check is SAID and not remembered: remembering is what lets the Studio show
            // its "the game has changed" screen once, and a headless call that used it up would take
            // that screen away from the person it is for.
            if (GameSetup.CheckBuild(profilePath) is { } verdict)
                Console.WriteLine($"game build: {verdict.Say}");

            var mounted = await GameMount.OpenAsync(profilePath, line =>
            {
                // GameMount writes the atlas line to stdout itself.
                if (!line.StartsWith("atlas library:", StringComparison.Ordinal)) Console.WriteLine(line);
            });
            if (mounted.Provider is IVfsFileProvider vfs)
                foreach (var line in MountReporting.Report(vfs, mounted.Profile.Game).ToLines())
                    Console.WriteLine(line);
            return AddonBackend.MountResult.Ok(mounted.Provider, mounted.Index);
        }
        catch (Exception e)
        {
            return AddonBackend.MountResult.Cannot($"could not open the game: {e.Message}");
        }
    }

    /// <summary>
    /// ONE line, for a game that cannot be mounted without a person: the add-on shows the last
    /// lines this prints, so this is what somebody reads in Blender. It names the fix, which is
    /// always the same - start the Studio once, where the question is asked and answered.
    /// </summary>
    private static string NotReady(GameSetup.Outcome setup)
    {
        const string fix = "Start ZeroCompanyModStudio.exe once - it asks where the game is, one click - and then try again.";
        if (setup.NeedsAnswer)
            return setup.Gone is not null
                ? $"The game is no longer where the Studio last found it ({setup.Gone}). {fix}"
                : $"The Zero Company Mod Studio has not been told where the game is. {fix}";
        return $"The Studio could not set the game up: {setup.Say} {fix}";
    }

    /// <summary>
    /// A release run with ZCMS_BACKGROUND=1 that is NOT one of the add-on's verbs: say why nothing opened. That variable
    /// means "nobody may be interrupted" to every program in this repository, and a release has no background mode to
    /// honour it with, so it opens no window at all (exit code 2). Called from Program.Main under "#if !DEV".
    /// </summary>
    public static int RefuseWindow()
    {
        var tidy = HeadlessConsole.Attach();
        try
        {
            Console.WriteLine($"refused: ZCMS_BACKGROUND=1 forbids a window and this build cannot keep one out of sight " +
                              $"(that is a developer feature), so {Branding.Product} opened nothing. " +
                              "The Blender add-on's --catalog and --import1 open no window and still run.");
            return 2;
        }
        finally
        {
            Console.Out.Flush();
            tidy?.Invoke();
        }
    }
}

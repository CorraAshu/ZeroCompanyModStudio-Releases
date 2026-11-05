using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UEToolkit.Core;
using ZeroCompanyModStudioAvalonia;

namespace ZeroCompanyLevelEditor;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var theme = "Dark";
#if DEV
        // --theme <Dark|Light|Holotable>: open in that theme, for a photograph of each. (View > Theme does
        // the same from the menu; the flag is a driver for a run with no mouse.)
        var args = Environment.GetCommandLineArgs();
        var i = Array.FindIndex(args, a => a == "--theme");
        if (i >= 0 && i + 1 < args.Length) theme = args[i + 1];
#endif
        AppTheme.Apply(theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // EXPLICIT SHUTDOWN, because this application has no single main window for the first part of
            // its start: the "where is the game" window (when it has to ask) closes before the editor
            // opens, and under the default rule that would end the process between the two.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
#if DEV
            // ZCMS_BACKGROUND=1: no window of this process may take focus or show on a monitor. Must be in
            // place before the first window exists.
            if (BackgroundMode.On) BackgroundMode.Install(this);
#endif
            _ = StartSafely(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// <see cref="Start"/>, with a net under it. It runs fire-and-forget, so an exception nobody catches is
    /// not a crash dialog, it is a process with no window that never exits - the worst way for a first run
    /// to fail. Whatever goes wrong, a person gets the editor (open without the game, and it says why), and
    /// failing even that the process ends rather than idling unseen.
    /// </summary>
    private static async Task StartSafely(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try { await Start(desktop); }
        catch (Exception e)
        {
            Console.WriteLine($"startup: {e}");
            try
            {
                if (desktop.Windows.Count == 0) OpenEditor(desktop, null);
            }
            catch (Exception again)
            {
                Console.WriteLine($"startup (second try): {again}");
                desktop.Shutdown();
            }
        }
    }

    /// <summary>
    /// WHERE THE GAME IS, decided here and not at the mount, because the one thing that may need a person is
    /// this, and it should come BEFORE the editor rather than over it - the Mod Studio's order, by its code.
    /// Resolve finds without ever showing anything (the remembered install, a profile left by the Studio or
    /// by an older run); it hands back "needs an answer" when nothing remembered still works, with whatever
    /// Steam's library list found to offer - one, several or none - and the window that asks follows.
    /// </summary>
    private static async Task Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var setup = GameStart.Resolve();
        var profilePath = setup.ProfilePath;

        if (setup.NeedsAnswer)
        {
            // Closing it unanswered is "open without the game", and the editor says so where the game
            // would have been. Use() has already written the answer to disk, which is where the mount reads
            // it from; the result is only read here to know which profile to check.
            var ask = new GameFolderWindow(new GameAsk(setup.Candidates)
            {
                Gone = setup.Gone,
                Problem = setup.Problem,
                Program = "the Level Editor",
            });
            var asked = new TaskCompletionSource();
            ask.Closed += (_, _) => asked.TrySetResult();
            ask.Show();
            await asked.Task;
            profilePath = ask.Result?.ProfilePath;
        }

        // IS THE USMAP FOR THIS BUILD? A level read through the wrong mappings does not fail, it draws
        // plausible garbage - so the Studio's one-button warning comes first, before the editor, and
        // nothing at all when the map matches.
        if (profilePath is not null)
        {
            try { await GameBuildWindow.RunAsync(profilePath, program: "the Level Editor"); }
            catch (Exception e) { Console.WriteLine($"build check: {e.Message}"); }
        }

        OpenEditor(desktop, profilePath);
    }

    private static void OpenEditor(IClassicDesktopStyleApplicationLifetime desktop, string? profilePath)
    {
        var main = new MainWindow(profilePath);
        desktop.MainWindow = main;
        main.Closed += (_, _) => desktop.Shutdown();
        main.Show();
    }
}

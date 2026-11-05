using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // EXPLICIT SHUTDOWN, because this application has no single main window for most of
            // its startup. The splash closes, the projects window closes, and under the default
            // rule either of those would end the process before the next one appeared.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
#if DEV
            // ZCMS_BACKGROUND=1: no window of this process may take focus or show on a monitor.
            if (BackgroundMode.On) BackgroundMode.Install(this);
#endif
            _ = StartSafely(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// <see cref="Start"/>, with a net under it. It runs fire-and-forget, so an exception nobody
    /// catches is not a crash dialog, it is a process with no window that never exits - the worst
    /// way for a first run to fail. Whatever goes wrong, a person gets a window: the projects
    /// window if nothing is open yet (the game is simply not mounted, and the Studio says why),
    /// and failing even that, the process ends rather than idling unseen.
    /// </summary>
    private static async Task StartSafely(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try { await Start(desktop); }
        catch (Exception e)
        {
            Console.WriteLine($"startup: {e}");
            try
            {
                if (desktop.Windows.Count == 0) await ShowProjects(desktop, null);
            }
            catch (Exception again)
            {
                Console.WriteLine($"startup (second try): {again}");
                desktop.Shutdown();
            }
        }
    }

    /// <summary>
    /// Splash, then the projects window, then the Studio.
    ///
    /// The author's shape, in his words: the startup image, then "a brand new window to appear
    /// that will show you Create a Mod or your mods you already have being worked on", and only
    /// once something is chosen does "the real program start".
    /// </summary>
    private static async Task Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Where installs come from. Null is the real thing (Steam's library list); the developer
        // flag below swaps in an empty one so the "where is the game" screens can be walked
        // through for real on a machine that has the game.
        Func<List<string>>? finder = null;

#if DEV
        // --nofind: pretend Steam's list is empty, so first run asks. Everything after that is the
        // real path - the splash, the question, what the answer writes, the Studio's mount.
        if (Environment.GetCommandLineArgs().Contains("--nofind")) finder = () => new List<string>();

        // --openmod <path>: straight into the Studio on a project, no splash and no picker.
        // The counterpart of --newmod, and for the same reason: the main window is otherwise only
        // reachable through two windows and a click.
        var all = Environment.GetCommandLineArgs();

        // --concept: the edit panel drawn as a PROPOSAL, with nothing behind it. It opens on its
        // own, is the whole of the application while it is up, and saves nothing — so the shape
        // can be judged, and photographed, before any of it is wired. `--concept b` opens on the
        // second of the two shapes.
        if (all.Contains("--concept"))
        {
            var which = Array.IndexOf(all, "--concept");
            var second = which + 1 < all.Length && all[which + 1] == "b";
            var concept = new ConceptWindow(second);
            desktop.MainWindow = concept;
            concept.Closed += (_, _) => desktop.Shutdown();
            concept.Show();
            return;
        }

        // --setupdialog [none|many|one|gone|again|againnone|restart]: the "where is the game" window
        // on its own, with candidates made up for the photograph (none = nothing found, many = two,
        // one = one, gone = one found and the remembered install missing, again = opened from the
        // menu on an install, againnone = from the menu with nothing mounted and nothing found,
        // restart = from the menu with the game mounted, so saving ends in "restart the Studio").
        // Nothing is searched for and nothing is mounted. --setuptype <path> types into its box and
        // --setuppress presses "Use this install", so the part AFTER the press can be checked
        // without a mouse - the same reasoning as --newmod ... --create.
        var sd = Array.IndexOf(all, "--setupdialog");
        if (sd >= 0)
        {
            var kind = sd + 1 < all.Length && !all[sd + 1].StartsWith("--") ? all[sd + 1] : "many";
            var found = GameFinder.Find();
            var shown = kind switch
            {
                "none" or "againnone" => new List<string>(),
                "one" or "gone" => found.Take(1).ToList(),
                _ => found.Take(1).Concat(new[] { @"D:\SteamLibrary\steamapps\common\Star Wars Zero Company" }).ToList(),
            };
            var ask = new GameFolderWindow(new GameAsk(shown)
            {
                Gone = kind is "gone" ? @"D:\Games\Star Wars Zero Company" : null,
                Again = kind is "again" or "againnone" or "restart",
                Selected = kind is "again" or "restart" ? found.FirstOrDefault() : null,
                RestartAfter = kind is "restart",
                Problem = all.Contains("--setupproblem") ? "the Steam library list could not be read" : null,
            });
            var tt = Array.IndexOf(all, "--setuptype");
            if (tt >= 0 && tt + 1 < all.Length) ask.TypePath(all[tt + 1]);
            var askClosed = new TaskCompletionSource();
            ask.Closed += (_, _) => askClosed.TrySetResult();
            ask.Show();
            if (all.Contains("--setuppress"))
            {
                ask.Press();
                if (ask.Result is { } r) Console.WriteLine($"setup: {r.Way} -> {r.ProfilePath}");
                else Console.WriteLine($"setup: not accepted - {ask.VerdictText}");
            }
            await askClosed.Task;
            desktop.Shutdown();
            return;
        }

        // --builddialog [text]: the "game has changed - Carry on anyway" screen on its own, with the
        // wording the real check would produce (or the text given), for the photograph.
        var bd = Array.IndexOf(all, "--builddialog");
        if (bd >= 0)
        {
            var say = bd + 1 < all.Length && !all[bd + 1].StartsWith("--")
                ? all[bd + 1]
                : "the game is build 199049 but the mappings are for 999999. Textures and properties " +
                  "may read wrongly. " + UEToolkit.Core.Branding.StaleMappingsAdvice;
            var build = new GameBuildWindow(say);
            var buildClosed = new TaskCompletionSource();
            build.Closed += (_, _) => buildClosed.TrySetResult();
            build.Show();
            await buildClosed.Task;
            desktop.Shutdown();
            return;
        }

        var om = Array.IndexOf(all, "--openmod");
        if (om >= 0 && om + 1 < all.Length)
        {
            var straight = new MainWindow(all[om + 1]);
            desktop.MainWindow = straight;
            straight.Closed += (_, _) => desktop.Shutdown();
            straight.Show();
            return;
        }

        // --newmod: open the name box on its own, with no splash and no projects window.
        //
        // The ImGui Studio carries `--modnameit` for the same reason and says why: it is the path
        // that renamed a second weapon's assets over the first's, and READING it had not shown
        // that. A dialog that only a mouse can reach is a dialog no check ever exercises.
        if (Environment.GetCommandLineArgs().Contains("--newmod"))
        {
            var only = new NewModWindow();
            // --newmod <name> types it in, so the VERDICT can be checked and not just the layout.
            var args = Environment.GetCommandLineArgs();
            var at = Array.IndexOf(args, "--newmod");
            if (at >= 0 && at + 1 < args.Length && !args[at + 1].StartsWith("--"))
                only.ModName = args[at + 1];
            // THE REAL PATH, not a shortcut around it. A flag that opens the dialog and then
            // exits tests the dialog; it does not test what happens after Create, which is the
            // part that matters and the part nothing else can reach without a mouse.
            var onlyClosed = new TaskCompletionSource();
            only.Closed += (_, _) => onlyClosed.TrySetResult();
            only.Show();

            // AFTER Show and after the handler is attached. Pressing first closed a window nobody
            // was listening to yet, so the wait below never finished and the Studio never opened —
            // while the project file had been written perfectly well. Exactly the shape of failure
            // this flag exists to catch, found by the flag itself.
            if (args.Contains("--create")) only.Press();

            await onlyClosed.Task;

            if (only.CreatedPath is not { } fresh) { desktop.Shutdown(); return; }
            var made = new MainWindow(fresh);
            desktop.MainWindow = made;
            made.Closed += (_, _) => desktop.Shutdown();
            made.Show();
            return;
        }
#endif

        // WHERE THE GAME IS, decided here and not at the first mount, because the one thing that
        // may need a person is this, and it should come BEFORE the Studio rather than over it.
        // Resolve finds without ever showing anything (the remembered install, an upgrader's saved
        // profile); it hands back "needs an answer" when nothing remembered still works, with
        // whatever Steam's library list found to offer - one, several or none - and the window that
        // asks follows the splash. The picture is the game's own, so it comes from whichever
        // install is in use, else the best candidate.
        StudioVm.Finder = finder;
        GameSetup.Outcome setup;
        try { setup = GameSetup.Resolve(find: finder); }
        catch (Exception e)
        {
            // Looking for the game must never be what stops the Studio from opening: say so, and
            // ask - the box takes a typed path whatever went wrong with the search.
            setup = new GameSetup.Outcome(GameSetup.Way.NeedsAnswer, null, null,
                                          $"game: looking for it failed - {e.Message}",
                                          Array.Empty<string>(), null, e.Message);
        }
        var install = setup.PictureFrom;

        var splash = new SplashWindow(install)
        {
            Status = setup.Gone is not null ? "The game is not where it was. You are asked where it is."
                   : setup.NeedsAnswer ? "Setting up. You are asked this once."
                   : install is null ? "Zero Company not found — the Studio still opens"
                   : "Zero Company Mod Studio",
        };
        splash.Show();

        // Long enough to read, short enough not to be in the way. There is no loading behind it
        // yet; when there is, this becomes "until that finishes" rather than a wait.
        await Task.Delay(1400);
        splash.Close();

        // The profile that will be mounted, if there is one: Resolve's, or the one the answer made.
        var profilePath = setup.ProfilePath;
        if (setup.NeedsAnswer)
        {
            // Closing it unanswered is "open without the game", and the Studio says so where the
            // game would have been. Use() has already written the answer to disk, which is where
            // the mount reads it from; the result is only read here to know which profile to check.
            var ask = new GameFolderWindow(new GameAsk(setup.Candidates)
            {
                Gone = setup.Gone,
                Problem = setup.Problem,
            });
            var asked = new TaskCompletionSource();
            ask.Closed += (_, _) => asked.TrySetResult();
            ask.Show();
            await asked.Task;
            profilePath = ask.Result?.ProfilePath;
        }

        // IS THE USMAP FOR THIS BUILD? The old Studio put up a screen when it was not, with one
        // button, and so does this one - before the Studio, not over it. Nothing when it matches.
        if (profilePath is not null)
        {
            try { await GameBuildWindow.RunAsync(profilePath); }
            catch (Exception e) { Console.WriteLine($"build check: {e.Message}"); }
        }

        await ShowProjects(desktop, null);
    }

    /// <summary>
    /// The projects window, and whatever the person chose out of it.
    ///
    /// Recursive on "create": the create flow is not built, so it comes straight back here rather
    /// than leaving somebody in a Studio with nothing open and no way back to this window except
    /// the menu they have not been shown yet.
    /// </summary>
    private static async Task ShowProjects(IClassicDesktopStyleApplicationLifetime desktop,
                                           Window? owner)
    {
        var picker = new ProjectsWindow();   // opens on the saved mods folder

        var closed = new TaskCompletionSource();
        picker.Closed += (_, _) => closed.TrySetResult();
        if (owner is null) picker.Show(); else await picker.ShowDialog(owner);
        await closed.Task;

        if (picker.Chosen is { } path)
        {
            var main = new MainWindow(path);
            desktop.MainWindow = main;
            main.Closed += (_, _) => desktop.Shutdown();
            main.Show();
            return;
        }

        if (picker.WantsNew)
        {
            // Named before the Studio opens, so it never appears with nothing in it. Cancelling
            // the name box goes back to the projects window rather than dumping somebody into an
            // empty Studio they did not ask for.
            var naming = new NewModWindow();
            var namingClosed = new TaskCompletionSource();
            naming.Closed += (_, _) => namingClosed.TrySetResult();
            naming.Show();
            await namingClosed.Task;

            if (naming.CreatedPath is not { } made) { await ShowProjects(desktop, null); return; }

            var main = new MainWindow(made);
            desktop.MainWindow = main;
            main.Closed += (_, _) => desktop.Shutdown();
            main.Show();
            return;
        }

        // Closed without choosing, and with no Studio behind it, means they are done.
        if (desktop.MainWindow is null) desktop.Shutdown();
    }
}

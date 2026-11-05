using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// What the question window is told: what was found, what went missing, and which of the two
/// ways it was opened.
/// </summary>
/// <param name="Candidates">Every install found, best first. May be empty.</param>
public sealed record GameAsk(IReadOnlyList<string> Candidates)
{
    /// <summary>The install to start on. Default: the first candidate - the old Studio's behaviour,
    /// and the reason a single find needs only one click.</summary>
    public string? Selected { get; init; }

    /// <summary>Where the game WAS, when it was remembered and is no longer there.</summary>
    public string? Gone { get; init; }

    /// <summary>What went wrong while looking, to be shown beside the question.</summary>
    public string? Problem { get; init; }

    /// <summary>Opened from Project > Find the game again, rather than at the first run.</summary>
    public bool Again { get; init; }

    /// <summary>The game is mounted right now, so a new answer cannot take effect until the Studio is
    /// started again (see MainWindow.OnFindGame for why). The window then says so after saving it.</summary>
    public bool RestartAfter { get; init; }

    /// <summary>The folder the settings go in; null is the real one beside the program.</summary>
    public string? AppDir { get; init; }

    /// <summary>
    /// What this window calls the program that opened it, where it names it in a sentence ("The Studio is
    /// using ...", "Close the Studio and open it again"). The Mod Studio's default is "the Studio"; the
    /// Level Editor, which links this same file, says "the Level Editor". The words are nothing else the
    /// two programs do differently: the answer, its files and its checks are Core's, shared.
    /// </summary>
    public string Program { get; init; } = "the Studio";
}

/// <summary>
/// The question Zero Company Mod Studio asks when it cannot answer it by itself: where is the game?
///
/// FINDING IS NOT HERE. <see cref="GameSetup.Resolve"/> looks (the remembered install, an
/// upgrader's saved profile, Steam's library list) and hands back "needs an answer" with what it
/// found; this window is the asking half, and what it does on "Use this install" is
/// <see cref="GameSetup.Use"/>, which checks the folder, builds the profile and remembers the
/// install. A window that built the profile itself would be a second opinion about what a profile
/// is, and the Blender add-on and the command line read what Core writes.
/// </summary>
public sealed class GameFolderVm : Obs
{
    private readonly string? _appDir;
    private readonly bool _again;
    private readonly bool _restartAfter;
    private readonly string _program;
    private string _typed = "";
    private string? _selected;
    private string _verdict = "";
    private IBrush _verdictBrush = Brushes.Gray;
    private bool _canUse;
    private bool _done;
    private readonly string? _inUse;

    public GameFolderVm(IReadOnlyList<string> candidates, string? appDir = null)
        : this(new GameAsk(candidates) { AppDir = appDir }) { }

    public GameFolderVm(GameAsk ask)
    {
        _appDir = ask.AppDir;
        _again = ask.Again;
        _restartAfter = ask.RestartAfter;
        _program = ask.Program;
        // Who else reads the same answer, said in the footnote: the Studio's own wording as it was, and
        // for another program that shares the settings, the Studio named first.
        var sharedWith = ask.Program == "the Studio"
            ? "the Blender add-on and the command line"
            : "the Mod Studio, the Blender add-on and the command line";
        var theProgram = char.ToUpperInvariant(_program[0]) + _program[1..];   // "The Studio" at the start of a sentence
        _inUse = ask.Again ? ask.Selected : null;

        foreach (var c in ask.Candidates) Candidates.Add(c);

        // The install in use may not be one Steam's list knows (a manual copy): it is still the
        // first thing to offer, so it is added to the list rather than left only in the box.
        if (ask.Selected is { Length: > 0 } now &&
            !Candidates.Any(c => string.Equals(c, now, StringComparison.OrdinalIgnoreCase)))
            Candidates.Insert(0, now);

        // The FIRST candidate is chosen for them, as the old Studio did: one install found means one
        // click, several means the likeliest is already picked and the others are one click away.
        // (This used to leave several unpicked, which read as "none found" with a list under it.)
        _selected = ask.Selected is { Length: > 0 } sel
            ? Candidates.FirstOrDefault(c => string.Equals(c, sel, StringComparison.OrdinalIgnoreCase))
            : Candidates.FirstOrDefault();
        _typed = _selected ?? "";

        var whatWasFound = Candidates.Count switch
        {
            0 => "Could not find Star Wars Zero Company automatically. Point this at the folder " +
                 "the game is installed in - the one holding SWZeroCompany.",
            1 => "Found your install.",
            _ => "Found more than one install. Which one do you play?",
        };

        if (_again)
            Intro = ask.Selected is { Length: > 0 } current
                ? $"{theProgram} is using {current}. Choose another install, or point this at the " +
                  "game's folder if it has moved."
                : (ask.Gone is not null ? $"The game is no longer where it was: {ask.Gone}\n" : "") +
                  (Candidates.Count == 0
                      ? $"{theProgram} has no game open. Point this at the folder the game is " +
                        "installed in - the one holding SWZeroCompany."
                      : $"{theProgram} has no game open. " + whatWasFound);
        else
            Intro = (ask.Gone is not null ? $"The game is no longer where it was: {ask.Gone}\n" : "") +
                    whatWasFound;

        // Two things that stop an answer being kept, said up front instead of after a press:
        // the finder failing, and a folder this program cannot write its settings to.
        var problems = new List<string>();
        if (ask.Problem is { Length: > 0 }) problems.Add("Looking for the game failed: " + ask.Problem);
        if (GameSetup.WriteProblem(_appDir, _program) is { } cannotWrite) problems.Add(cannotWrite);
        Problem = string.Join("\n", problems);

        Footnote = _again
            ? $"The answer is saved in the settings folder beside this program, and {sharedWith} " +
              "use the same one." +
              (_restartAfter ? $" {theProgram} reads the game when it starts, so a new answer is used " +
                               "the next time it is opened." : "")
            : (ask.Gone is null ? "You are asked this once. " : "") +
              $"The answer is saved in the settings folder beside this " +
              $"program, and {sharedWith} use the same one. To change it " +
              $"later, use Project > Find the game again… in {_program}.";

        Recheck();
    }

    public ObservableCollection<string> Candidates { get; } = new();
    public bool HasCandidates => Candidates.Count > 0;
    public string Intro { get; }
    public string Footnote { get; }

    /// <summary>A line in red above the box when something already stands in the way; empty if not.</summary>
    public string Problem { get; }
    public bool HasProblem => Problem.Length > 0;

    /// <summary>The left button: "Open without the game" at the first run, "Cancel" from the menu,
    /// and "OK" once an answer has been saved and only a restart is left to do.</summary>
    public string SkipText => _done ? "OK" : _again ? "Cancel" : "Open without the game";
    public bool NotDone => !_done;

    /// <summary>What <see cref="GameSetup.Use"/> made of it, once it has worked; else null.</summary>
    public GameSetup.Outcome? Result { get; private set; }

    public string? SelectedCandidate
    {
        get => _selected;
        set { if (Set(ref _selected, value) && value is not null) Typed = value; }
    }

    public string Typed
    {
        get => _typed;
        set { if (Set(ref _typed, value)) Recheck(); }
    }

    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public IBrush VerdictBrush { get => _verdictBrush; private set => Set(ref _verdictBrush, value); }
    public bool CanUse { get => _canUse; private set => Set(ref _canUse, value); }

    /// <summary>Judged on the path in the box, every keystroke, by the test the profile will be
    /// built under: what has to be there, not what the folder is called. One exact directory probe,
    /// never a search below the folder - typing a drive letter must not scan the drive.</summary>
    private void Recheck()
    {
        if (_done) return;
        var path = _typed.Trim().Trim('"');
        if (path.Length == 0)
        {
            CanUse = false;
            Verdict = "choose the folder the game is installed in";
            VerdictBrush = Res("Faint", Brushes.Gray);
        }
        else if (GameFinder.IsInstall(GameFinder.InstallOf(path) ?? path))
        {
            CanUse = true;
            Verdict = "game data found";
            VerdictBrush = Res("Edited", Brushes.MediumSeaGreen);
        }
        else
        {
            CanUse = false;
            Verdict = "no game data under that folder";
            VerdictBrush = Res("Danger", Brushes.IndianRed);
        }
    }

    /// <summary>
    /// Builds the profile and remembers the install. True when the window should close now; false
    /// when it should stay (it failed, and says why in place, or it worked and a restart is left).
    ///
    /// A FAILURE LEAVES THE BUTTON ALONE. It used to switch itself off until the text was edited,
    /// which turned "no usmap beside the program" into a dead end: fix the folder, come back, press
    /// - and nothing, with no hint that retyping a letter was the way to wake it.
    /// </summary>
    public bool Use()
    {
        var made = GameSetup.Use(_appDir, _typed, _program);
        if (!made.Ready)
        {
            Verdict = made.Say;
            VerdictBrush = Res("Danger", Brushes.IndianRed);
            return false;
        }

        Result = made;
        if (!_restartAfter) return true;

        // The game is mounted, and the answer is saved: say plainly that the next start uses it -
        // unless it is the very install already in use, in which case nothing needs doing.
        _done = true;
        CanUse = false;
        Verdict = string.Equals(made.Install, _inUse, StringComparison.OrdinalIgnoreCase)
            ? $"That is the install {_program} is already using ({made.Install}). Nothing changed."
            : $"Saved: {made.Install}. Close {_program} and open it again to use this install.";
        VerdictBrush = Res("Edited", Brushes.MediumSeaGreen);
        Raise(nameof(SkipText));
        Raise(nameof(NotDone));
        return false;
    }

    private static IBrush Res(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out var found) == true && found is IBrush b
            ? b : fallback;
}

public partial class GameFolderWindow : Window
{
    private readonly GameFolderVm _vm;

    /// <summary>The outcome of "Use this install", or null when they chose to open without the game.</summary>
    public GameSetup.Outcome? Result => _vm.Result;

    public GameFolderWindow() : this(Array.Empty<string>()) { }

    public GameFolderWindow(IReadOnlyList<string> candidates, string? appDir = null)
        : this(new GameAsk(candidates) { AppDir = appDir }) { }

    public GameFolderWindow(GameAsk ask)
    {
        _vm = new GameFolderVm(ask);
        AvaloniaXamlLoader.Load(this);
        DataContext = _vm;
        // The program's own name in the title bar: "Zero Company Mod Studio — Find the game" in the Studio,
        // "Zero Company Level Editor — Find the game" in the Level Editor, which links this file.
        Title = $"{Branding.Product} — Find the game{(ask.Again ? " again" : "")}";
        Opened += (_, _) => this.FindControl<TextBox>("PathBox")?.Focus();
    }

    private void OnPathKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where is Star Wars Zero Company installed?",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { Length: > 0 } local)
            _vm.Typed = local;
    }

    private void OnSkip(object? sender, RoutedEventArgs e) => Close();

    private void OnUse(object? sender, RoutedEventArgs e) => Press();

    /// <summary>What the button does, callable without one - for a driver flag, so the part AFTER
    /// the press (the profile written, the install remembered) can be checked without a mouse.</summary>
    public void Press() { if (_vm.Use()) Close(); }

    /// <summary>Type into the box from outside, for a driver flag.</summary>
    public void TypePath(string path) => _vm.Typed = path;

    /// <summary>The line under the box, for a driver flag to read back.</summary>
    public string VerdictText => _vm.Verdict;
}

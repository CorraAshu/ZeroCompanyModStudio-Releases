using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// The screen the ImGui Studio put up when the game's build is not the one the shipped usmap was
/// made for: what changed, what that costs, and one button, "Carry on anyway".
///
/// WHEN IT APPEARS is the old Studio's rule, kept: <see cref="GameInstall.Verdict.WorthSaying"/>. The
/// map and the game's executable name the same engine changelist, so a map for another build is
/// "stale" - demonstrable, and shown EVERY start until the map is replaced. When the map names no
/// changelist, the fallback is "did the game's build change since the last start": that is shown
/// ONCE, because the build is written down (<see cref="GameSetup.RememberBuild"/>) after it has
/// been shown and the next start compares equal.
/// </summary>
public partial class GameBuildWindow : Window
{
    /// <summary>What the Studio calls itself in the two sentences below that name it.</summary>
    public const string StudioName = "this Studio";

    public GameBuildWindow() : this("") { }

    /// <param name="say">The verdict's sentence.</param>
    /// <param name="program">
    /// What the two sentences that name the program call it. "this Studio" is the Mod Studio's own wording and
    /// leaves the window exactly as it was; the Level Editor, which links this same file, passes "the Level
    /// Editor". The title bar carries the program's own name either way.
    /// </param>
    public GameBuildWindow(string say, string program = StudioName)
    {
        AvaloniaXamlLoader.Load(this);
        // The title bar names the program: "Zero Company Mod Studio — The game has changed" in the Studio,
        // "Zero Company Level Editor — ..." in the Level Editor.
        Title = $"{Branding.Product} — The game has changed";
        if (program != StudioName)
        {
            var heading = this.FindControl<TextBlock>("Heading")!;
            var means = this.FindControl<TextBlock>("Means")!;
            heading.Text = heading.Text!.Replace(StudioName, program);
            means.Text = means.Text!.Replace(StudioName, program);
        }
        // The verdict's sentence is a log line and starts lower-case ("the game is build ..."); on
        // a screen of its own it is the first line a person reads, so it gets its capital.
        this.FindControl<TextBlock>("Say")!.Text =
            say.Length > 0 ? char.ToUpperInvariant(say[0]) + say[1..] : say;
    }

    private void OnCarryOn(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Ask the question about this profile and, if it is worth putting to anyone, put it. Returns
    /// when the person has answered. Closing the window is "carry on", the same as the button: there
    /// is nothing to refuse, only something to have been told.
    /// </summary>
    public static async Task RunAsync(string profilePath, Window? owner = null, string program = StudioName)
    {
        // Off the UI thread: it looks for the executable under Binaries.
        var verdict = await Task.Run(() => GameSetup.CheckBuild(profilePath));
        if (verdict is null) return;

        if (verdict.WorthSaying)
        {
            var window = new GameBuildWindow(verdict.Say, program);
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            if (owner is null) window.Show(); else window.Show(owner);
            await closed.Task;
        }

        // AFTER it has been shown, or "changed since last time" would compare this run to itself.
        GameSetup.RememberBuild(profilePath, verdict);
    }
}

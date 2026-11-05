using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// About: what this is, who made it, what may be done with it, where the next version is, and the
/// one switch for the one thing this program does on the internet unasked.
///
/// The ImGui Studio's window (UserInterface.DrawAbout) in this Studio's look. Every sentence that
/// carries a promise - the licence, what the update check sends - is the old one word for word.
/// </summary>
public partial class AboutWindow : Window
{
    private readonly AppSettings? _settings;
    private readonly Action<string>? _say;

    /// <summary>
    /// The sentence saying the last choice could not be saved, kept until a save works. It lives
    /// in a field because the update check can finish while this window is open and redraw the
    /// sentence under the switch; that redraw used to erase the warning.
    /// </summary>
    private string? _saveNote;

    public AboutWindow() : this(null, null) { }

    /// <param name="settings">
    /// The user's settings (profiles\settings.json), the file the ImGui Studio kept the same
    /// switch in. Null hides the switch rather than showing one that saves nowhere.
    /// </param>
    /// <param name="say">Where to put a line in the Studio's log.</param>
    public AboutWindow(AppSettings? settings, Action<string>? say)
    {
        AvaloniaXamlLoader.Load(this);
        _settings = settings;
        _say = say;

        Title = $"About {Branding.Product}";
        this.FindControl<TextBlock>("ProductText")!.Text = Branding.Product;

        var version = UpdateNotice.Running;
        var versionText = this.FindControl<TextBlock>("VersionText")!;
        versionText.Text = $"version {version}";
        versionText.IsVisible = version.Length > 0;

        this.FindControl<TextBlock>("AuthorText")!.Text = Branding.Author;

        // Nothing to point at, nothing drawn: a blank Home hides the buttons, the address and the
        // update switch together, because without a release page there is nothing for them to say.
        var home = Branding.Home.Length > 0;
        this.FindControl<StackPanel>("HomeBlock")!.IsVisible = home;
        this.FindControl<Button>("GuidesButton")!.IsVisible = Branding.Landing.Length > 0;
        this.FindControl<TextBlock>("HomeText")!.Text = Branding.Home;

        // The switch is offered only where there is a check to switch: Home is a GitHub address
        // (UpdateCheck derives its repository from it) and there are settings to keep the answer.
        var check = home && settings is not null && UpdateCheck.RepoOf(Branding.Home) is not null;
        this.FindControl<StackPanel>("CheckBlock")!.IsVisible = check;
        if (check) this.FindControl<CheckBox>("CheckBox")!.IsChecked = settings!.CheckForUpdates;
        ShowCheckText();

        // The check can finish while this is open (it runs off the UI thread); say so then.
        UpdateNotice.Changed += OnUpdateChanged;
        Closed += (_, _) => UpdateNotice.Changed -= OnUpdateChanged;
    }

    private void OnUpdateChanged() => Dispatcher.UIThread.Post(ShowCheckText);

    /// <summary>
    /// The sentence under the switch: the newer version when there is one, else what the check
    /// does and does not do. The save note (<see cref="_saveNote"/>) replaces both while the choice
    /// is not written down, because a switch that looks set and is not is the worst thing it can say.
    /// </summary>
    private void ShowCheckText()
    {
        var text = this.FindControl<TextBlock>("CheckText")!;
        text.Text = _saveNote
            ?? (UpdateNotice.Found is { } newer
                ? $"Version {newer.Version} is available." +
                  (newer.Published is null ? "" : $" Published {newer.Published}.") +
                  " Nothing is downloaded or installed for you."
                : "It asks the release page above and nothing else. Nothing about you or " +
                  "your game is sent, and nothing is ever downloaded or installed for you.");
    }

    private void OnDownloads(object? sender, RoutedEventArgs e)
    {
        if (Branding.OpenLink(Branding.Home) is { } why)
            _say?.Invoke($"could not open {Branding.Home}: {why}");
    }

    /// <summary>
    /// What "Guides on the web" says it opens. The XAML's own tooltip names the Studio's Handbook menu, which a host
    /// without one (the Level Editor) does not have, so such a host says it in its own words here. The Studio leaves
    /// this alone and keeps the XAML's.
    /// </summary>
    public string? GuidesTooltip
    {
        set => ToolTip.SetTip(this.FindControl<Button>("GuidesButton")!, value);
    }

    private void OnGuides(object? sender, RoutedEventArgs e)
    {
        if (Branding.OpenLink(Branding.Landing) is { } why)
            _say?.Invoke($"could not open {Branding.Landing}: {why}");
    }

    /// <summary>
    /// Saved on the spot. Click rather than IsCheckedChanged, so setting the box from the file
    /// when the window opens is not mistaken for somebody choosing.
    /// </summary>
    private void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var on = this.FindControl<CheckBox>("CheckBox")!.IsChecked == true;
        _settings.CheckForUpdates = on;
        var saved = _settings.Save(_say);
        if (saved is null)
        {
            // WHICH failure, not a guess: no folder to keep the file in, or a folder that would not
            // take the write. The log already carries the same words.
            _saveNote = $"This choice could not be saved ({(_settings.SaveFailure ?? "the settings file could not be written").TrimEnd('.')}), " +
                        "so it holds until the Studio is closed and no longer.";
            ShowCheckText();
        }
        else
        {
            _saveNote = null;
            ShowCheckText();
            _say?.Invoke(on ? "update check: on - the Studio looks for a newer version when it starts"
                            : "update check: off - the Studio will not look for a newer version");
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

#if DEV
    /// <summary>
    /// Developer flag --aboutcheck: press the switch the way a click does - set it, then run the
    /// handler a click runs - for a run with no mouse. What matters is what that WRITES.
    /// </summary>
    public void PressCheck(bool on)
    {
        var box = this.FindControl<CheckBox>("CheckBox")!;
        if (!box.IsVisible && !this.FindControl<StackPanel>("CheckBlock")!.IsVisible)
            _say?.Invoke("--aboutcheck: the switch is not offered in this build");
        box.IsChecked = on;
        OnCheckForUpdates(null, new RoutedEventArgs());
    }
#endif
}

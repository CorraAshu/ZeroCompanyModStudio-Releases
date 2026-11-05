using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// The release notes for a newer version, opened from the menu bar's notice.
///
/// The ImGui Studio's "What's new" window (UserInterface.DrawWhatsNew), which opens what changed
/// rather than the download page: the order matters, because a person is asked to decide whether
/// to update and should be told what updating would get them first. The download is one button
/// further on.
/// </summary>
public partial class WhatsNewWindow : Window
{
    private readonly UpdateCheck.Release _release;
    private readonly Action<string>? _say;

    public WhatsNewWindow() : this(new UpdateCheck.Release("", "", null), null) { }

    public WhatsNewWindow(UpdateCheck.Release release, Action<string>? say)
    {
        AvaloniaXamlLoader.Load(this);
        _release = release;
        _say = say;

        Title = $"What's new in {Branding.Product} {release.Version}";

        var running = UpdateNotice.Running;
        this.FindControl<TextBlock>("HeadText")!.Text =
            $"You are running {running}." +
            (release.Published is null ? "" : $"  {release.Version} was published {release.Published}.");

        var notes = UpdateCheck.Readable(release.Notes);
        this.FindControl<SelectableTextBlock>("NotesText")!.Text = notes.Length == 0
            ? "This release came with no notes. The download page may say more."
            : notes;
    }

    private void OnOpenPage(object? sender, RoutedEventArgs e)
    {
        // The page's address came out of a response from the internet; UpdateNotice.OpenWebLink
        // opens web addresses and nothing else. A release with no usable address falls back to
        // the releases page we already know.
        var why = UpdateNotice.OpenWebLink(_release.Url);
        if (why is not null && Branding.Home.Length > 0) why = UpdateNotice.OpenWebLink(Branding.Home);
        if (why is not null) _say?.Invoke($"could not open the download page: {why}");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

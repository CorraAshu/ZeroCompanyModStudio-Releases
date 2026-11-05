using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE UPDATE NOTICE, as the menu bar binds it. The answer itself lives in
/// <see cref="UpdateNotice"/>; this is only its text and its visibility, so the markup has
/// something to bind to and the window that opens from it reads the same release.
///
/// Every property is empty or false until a newer release has been found, which is what keeps
/// the notice from existing at all in the ordinary case: an item that is not visible takes no
/// room in the menu bar, and a notice that shows "up to date" is a notice nobody asked for.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// WHO MADE IT, as the menu bar prints it: "Zero Company Mod Studio by Clonetrooper163".
    /// Blank when Branding.Author is - a build with no author has no credit to draw.
    /// </summary>
    public string CreditText => Branding.Author.Length == 0 ? "" : $"{Branding.Product} by {Branding.Author}";

    /// <summary>Whether there is a credit to draw.</summary>
    public bool HasCredit => Branding.Author.Length > 0;

    /// <summary>Whether a release newer than this one has been found.</summary>
    public bool HasUpdate => UpdateNotice.Found is not null;

    /// <summary>The menu-bar notice itself, worded as the ImGui Studio worded it.</summary>
    public string UpdateText => UpdateNotice.Found is { } r ? $"version {r.Version} is available" : "";

    /// <summary>
    /// What hovering the notice says: what is running, when the other one came out, and the
    /// promise that clicking it only reads.
    /// </summary>
    public string UpdateTip => UpdateNotice.Found is not { } r ? "" :
        $"You are running {UpdateNotice.Running}." +
        (r.Published is null ? "" : $" {r.Version} was published {r.Published}.") +
        "\nClick to see what changed. Nothing is installed for you.";

    /// <summary>The About menu's second entry for the same release, where somebody who ignored the bar looks.</summary>
    public string UpdateGetText => UpdateNotice.Found is { } r ? $"Get version {r.Version}" : "";

    /// <summary>
    /// The check finished (or was faked): tell the bindings. Called on the UI thread; the event
    /// that triggers it comes from the thread the request ran on.
    /// </summary>
    public void UpdateChanged()
    {
        Raise(nameof(HasUpdate));
        Raise(nameof(UpdateText));
        Raise(nameof(UpdateTip));
        Raise(nameof(UpdateGetText));
    }
}

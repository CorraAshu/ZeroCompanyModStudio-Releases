using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// WHETHER THERE IS A NEWER RELEASE, for the whole application: one answer, asked once.
///
/// The ImGui Studio held this in its interface class and asked from the first frame of its menu
/// bar (UserInterface.StartUpdateCheck). Here it is a static, because three places want the same
/// answer and none of them owns it: the menu bar's notice, the release-notes window, and About
/// (which says the same thing again beside its switch). Asked ONCE PER LAUNCH, not once per
/// window - the Studio can open more than one main window in a run, and a second request for
/// the same answer is a request GitHub rate-limits at sixty an hour.
///
/// WHAT IT DOES NOT DO is as deliberate as what it does, and is all in <see cref="UpdateCheck"/>:
/// nothing is downloaded, nothing is installed, nothing about the person or their game is sent,
/// and every failure is silence. A machine with no internet never finds out it asked.
/// </summary>
internal static class UpdateNotice
{
    private static int _asked;

    /// <summary>
    /// A release newer than this one, once the check has found one. Null until then, and null
    /// for good when there is none - "up to date" and "could not ask" look the same on purpose.
    /// </summary>
    public static UpdateCheck.Release? Found { get; private set; }

    /// <summary>
    /// Raised when <see cref="Found"/> changes. FROM A BACKGROUND THREAD: the request runs off
    /// the UI thread, and whoever listens marshals its own redraw.
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// The version this build is: <see cref="Branding.Version"/>, else the assembly's own.
    /// Blank only when neither can be read, which is a build with no version stamp at all.
    /// </summary>
    public static string Running
    {
        get
        {
#if DEV
            if (_runningAs is { Length: > 0 }) return _runningAs;
#endif
            return Branding.Version.Length > 0 ? Branding.Version : AssemblyVersion();
        }
    }

    /// <summary>The assembly's own version, for a build that set no Branding.Version.</summary>
    private static string AssemblyVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            var info = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (info is { Length: > 0 } &&
                info[0] is System.Reflection.AssemblyInformationalVersionAttribute a &&
                a.InformationalVersion is { Length: > 0 } v)
                return v.Split('+')[0];
            return asm?.GetName().Version?.ToString(3) ?? "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// Ask whether there is a newer release, in the background, at most once per launch.
    ///
    /// Fire and forget on purpose. The answer is worth a line in the menu bar and nothing at all
    /// if getting it delays the first frame, so nothing waits on this and nothing fails because
    /// of it.
    ///
    /// GATED BY THE USER'S SWITCH, <see cref="AppSettings.CheckForUpdates"/> - the same field of
    /// the same profiles\settings.json the ImGui Studio read and wrote, so an upgrader who turned
    /// the check off has it still off. The switch is read HERE, at the one place that would
    /// otherwise make the request, so there is no path to the network that skips it.
    /// </summary>
    public static void StartOnce(AppSettings settings, Action<string>? say = null)
    {
        if (Interlocked.Exchange(ref _asked, 1) != 0) return;
#if DEV
        // A faked notice stands in for the real one; asking GitHub as well would only spend a
        // request of the sixty an hour and could overwrite the picture being taken.
        if (_pretended) { say?.Invoke("update check: skipped - a notice is being faked"); return; }
        // A background run is a test run, and tests launch this Studio dozens of times an hour:
        // each would spend one of GitHub's sixty unauthenticated requests. Only a run that asked to
        // pretend to be another version (--runningas) is there to see the real answer.
        if (Environment.GetEnvironmentVariable("ZCMS_BACKGROUND") == "1" && _runningAs is null)
        { say?.Invoke("update check: skipped - background test run (pass --runningas to ask)"); return; }
#endif
        if (!settings.CheckForUpdates)
        {
#if DEV
            say?.Invoke("update check: off (Look for a newer version is unticked) - nothing asked");
#endif
            return;
        }

        var home = Branding.Home;
        var running = Running;
        if (home.Length == 0 || running.Length == 0) return;

#if DEV
        say?.Invoke($"update check: asking {UpdateCheck.RepoOf(home)?.Owner}/{UpdateCheck.RepoOf(home)?.Repo} " +
                    $"as version {running}");
#endif
        _ = Task.Run(async () =>
        {
            var found = await UpdateCheck.NewerThanAsync(home, running).ConfigureAwait(false);
#if DEV
            say?.Invoke(found is null
                ? $"update check: no newer release than {running} (or none could be asked)"
                : $"update check: version {found.Version} is available (published {found.Published})");
#endif
            if (found is null) return;
            Found = found;
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Only http(s) links, and only to open in a browser. The address of a release comes out of
    /// a response from the internet, and the shell will start whatever it is handed - a path to a
    /// program included - so an address that is not a web address is not opened.
    /// </summary>
    public static string? OpenWebLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "that is not a web address";
        return Branding.OpenLink(uri.AbsoluteUri);
    }

#if DEV
    private static bool _pretended;
    private static string? _runningAs;

    /// <summary>
    /// <c>--fakeupdate &lt;version&gt;</c>: put an update notice on screen without one existing.
    ///
    /// The notice draws only when the release page has something newer, which for the current
    /// build it never does, so the one state nobody could look at was the state it exists for.
    /// The menu bar clips in silence, and only a picture says whether two right-aligned items
    /// still fit beside each other.
    /// </summary>
    public static void Pretend(string version, string url)
    {
        _pretended = true;
        Found = new UpdateCheck.Release(version, url, "2099-01-01")
        {
            // REAL NOTES, in the shape the real ones arrive in - headings, bold, a markdown link
            // and a rule - because the window's whole job is turning that into something
            // readable, and a stub of plain prose would photograph a reducer that had done
            // nothing.
            Notes = string.Join("\n", [
                "### Fixed: the imported skeleton was upside down",
                "",
                "Every import before this one came in with its **armature inverted**. The model was",
                "always right, which is why it was easy to miss.",
                "",
                "---",
                "",
                "### New: every other number in the game",
                "",
                "[Browse the game](https://example.invalid) then Tables. All 852 of them.",
            ]),
        };
        Changed?.Invoke();
    }

    /// <summary>
    /// <c>--runningas &lt;version&gt;</c>: ask the real release page AS IF this were another
    /// version, so the live check can be shown to find something. Without it "no notice" is what
    /// a working check and a silently failing one both print.
    /// </summary>
    public static void RunAs(string version) => _runningAs = version;
#endif
}

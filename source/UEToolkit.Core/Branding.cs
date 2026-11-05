namespace UEToolkit.Core;

/// <summary>
/// What this build calls itself, wherever a person can see it.
///
/// Two applications share this code: the toolkit, and Zero Company Mod Studio - which is the one
/// that gets handed to people who play the game and must not look like a piece of somebody's
/// internal tooling. Every name a user reads comes from here rather than from a literal, because
/// the alternative is finding them one at a time after someone has already seen the wrong one:
/// the default mods folder, the line written into the top of every saved mod, the export folder.
///
/// Deliberately mutable statics set once at startup, not constants. A constant cannot differ
/// between two applications built from the same file, which is the entire requirement.
/// </summary>
public static class Branding
{
    /// <summary>
    /// Open a link in whatever the person browses with.
    ///
    /// `UseShellExecute` is the whole trick and it is not the default: without it .NET tries to
    /// EXECUTE the string as a program, which fails on every URL ever passed to it. Returns the
    /// reason rather than throwing, because a dead link is worth a line in the log and is not
    /// worth taking the application down for.
    /// </summary>
    public static string? OpenLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "there is no link to open";
        try
        {
            using var opened = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>The product name, as it appears in text a user reads.</summary>
    public static string Product = "UEToolkit";

    /// <summary>
    /// What a person is told to DO when the game is a different build from the one the shipped
    /// mappings were made for (<see cref="GameInstall.Check"/>).
    ///
    /// The toolkit's reader can dump a fresh usmap, so its default says so. Nobody who downloaded
    /// the Studio can, and for them that sentence is an instruction they cannot carry out: the
    /// Studio sets this to "look for a newer version", which is the one thing they can do.
    /// </summary>
    public static string StaleMappingsAdvice = "Dump a new usmap for this build.";

    /// <summary>
    /// Who made this build, shown in the menu bar and in About.
    ///
    /// A field rather than a literal buried in the interface, because it is read in four places
    /// and a credit that disagrees with itself is worse than none. Set beside Product.
    /// </summary>
    public static string Author = "";

    /// <summary>The version shown beside the name. Read from the assembly when this is blank.</summary>
    public static string Version = "";

    /// <summary>Where the tool comes from, for the About window. Blank hides the line.</summary>
    public static string Home = "";

    /// <summary>
    /// The folder under Documents where mod projects live by default.
    ///
    /// Its own setting rather than <see cref="Product"/> with a word after it, because the two
    /// answers that reads well are not the same string: "Zero Company Mod Studio Mods" is what
    /// deriving it gives, and "Zero Company Mods" is what anybody would actually name a folder.
    /// </summary>
    public static string ModsFolder = "UEToolkit Mods";

    /// <summary>The folder under Documents that exports are written to by default.</summary>
    public static string ExportsFolder = "UEToolkit Exports";

    /// <summary>
    /// Whether this build arranges itself for somebody MAKING something rather than somebody
    /// reading a cooked game.
    ///
    /// The two applications share every panel, and the difference between them is not which
    /// panels exist -- it is which ones are the point. The toolkit is an asset browser whose nine
    /// tabs are nine equals; the Studio is a mod tool that inherited the same strip, so the tab
    /// an author wants sat seventh of nine beside five they will never open.
    ///
    /// It is a FLAG rather than a second layout file for the reason recorded above the picker
    /// skip: two builds drift, and they drift exactly where it hurts.
    /// </summary>
    public static bool AuthorFirst;

    /// <summary>
    /// The friendly page for someone who wants to READ about this rather than download it.
    ///
    /// Separate from <see cref="Home"/> on purpose, and the reason is not presentation. Home is
    /// where the releases are, and the update check DERIVES the repository from it -- it accepts
    /// a github.com address and nothing else, so pointing Home at a project page would turn the
    /// update check off without a word. Two links, each doing its own job.
    ///
    /// Empty means the button is not drawn.
    /// </summary>
    public static string Landing = "";

    /// <summary>
    /// The guides this build points people at, in menu order. Empty means the Handbook menu is not
    /// drawn at all -- the toolkit has no guides, and an empty menu is worse than no menu.
    ///
    /// Kept here rather than in the menu code for the reason everything else in this file is: two
    /// applications share that code and only one of them has documentation. It also means the URLs
    /// are in ONE place when they move, instead of being found one at a time by whoever notices a
    /// dead link.
    /// </summary>
    public static Guide[] Guides = [];

    /// <summary>
    /// One guide, reachable two ways. <paramref name="File"/> is a copy shipped beside the
    /// executable and is what the in-application reader shows; <paramref name="Url"/> is the
    /// published page, for someone who would rather read it in their own browser.
    ///
    /// Both, because they fail differently: a link is dead without internet and rots when the page
    /// moves, and a local copy is only ever as current as the build it shipped with.
    /// </summary>
    public sealed record Guide(string Label, string File, string Url);

    /// <summary>
    /// How to show a guide inside the application, when the application can.
    ///
    /// A delegate rather than a call, because the menu that offers it is shared source compiled
    /// into two executables and the reader exists in only one of them -- it is a WinForms window
    /// hosting a browser control, which the toolkit neither has nor wants. Null here means "no
    /// reader", and the menu falls back to the published link without any of it being a failure.
    /// </summary>
    public static Func<string, string, string?>? GuideReader;

    /// <summary>
    /// This build serves ONE game and must not be able to be pointed at another.
    ///
    /// The distinction the mod studio exists for. It is not about hiding features -- it ships the
    /// whole workbench, browser and viewport and exporters -- it is about the handful of settings
    /// that answer "which game": the Paks directory, the engine version, the mappings file and the
    /// AES key. Those are what turn this into a general extraction tool for anything on disk, and
    /// they are the reason it is a separate executable rather than a flag.
    /// </summary>
    public static bool SingleGame;
}

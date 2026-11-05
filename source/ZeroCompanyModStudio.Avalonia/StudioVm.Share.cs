using System.Diagnostics;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// "ZIP IT FOR SHARING": the installed mod, packed into a zip somebody else can use.
///
/// Ported from the ImGui Studio's install row (<c>UEToolkit.App\ModPanel\Install.cs</c>); the work
/// is Core's <see cref="ModShare.Zip"/> and <see cref="ModInstall.EarlyFiles"/>, called exactly
/// as the old button called them, so a mod zipped from here is the same zip.
///
/// MADE FROM WHAT WAS INSTALLED, NOT FROM THE PROJECT, so the thing shared is the thing tested.
/// That is also why the button is only live once the mod is installed: the people this tool is for
/// upload to Nexus, and a zip made the wrong way round (the folder's contents, not the folder)
/// looks right in a file browser and installs to nothing - which the uploader cannot see, because
/// on their machine the mod is already there.
///
/// A mod with an EARLY half (files the game loads before it reads Mods, delivered through
/// Content\Paks\~mods) lives in two places, and its zip is the GAME folder rather than the mod's;
/// the tooltip and the success line both say which one the person is about to get, because the
/// two are extracted into different folders.
/// </summary>
public sealed partial class StudioVm
{
#if DEV
    /// <summary>
    /// DEVELOPER-ONLY (<c>--installto &lt;Paks folder&gt;</c>): install, un-install and look for the
    /// mod in THIS folder instead of the mounted game's. The build still reads the game to build
    /// from it, but every WRITE goes to the scratch folder, so the install, share and uninstall
    /// paths can be driven without the author's real game being touched. Fails CLOSED: a folder
    /// that does not resolve means "nowhere to install", never a fall-back to the real game.
    /// </summary>
    public string? PaksOverride { get; set; }
#endif

    /// <summary>
    /// The Paks folder the mod is installed into (and looked for in): the mounted game's, unless a
    /// developer run named another. The ONE place the three install paths ask.
    /// </summary>
    private string? InstallPaks()
    {
#if DEV
        if (!string.IsNullOrEmpty(PaksOverride)) return ModInstall.ResolvePaks(PaksOverride);
#endif
        return ModInstall.ResolvePaks(_profile?.PaksDirectory ?? "");
    }

    private bool _zipping;
    private bool _shareReady;
    private string _shareTip = "Open a mod first.";

    /// <summary>The zip the last press made, or null.</summary>
    public string? ShareZip { get; private set; }

    /// <summary>Whether the button does anything: the mod is installed and nothing else is busy.</summary>
    public bool CanShare => _shareReady && !_building && !_zipping;

    public string ShareLabel => _zipping ? "Packing…" : "Zip it for sharing";

    /// <summary>
    /// What the button says it will do - or, while it is not live, WHY not. A greyed button with
    /// no explanation is the shape of thing that makes somebody believe the tool is broken.
    /// </summary>
    public string ShareTip => _shareTip;

    /// <summary>
    /// Ask the game folder whether this mod is installed, and re-word the button to match. Called
    /// when the mod changes, after every install and uninstall, and when the window comes back to
    /// the front - the folder can change from outside the Studio (the old panel re-listed it every
    /// second for the same reason). Cheap: one folder listing, never a scan of the game.
    /// </summary>
    public void RefreshShare()
    {
        bool ready;
        string tip;
        try { (ready, tip) = ShareState(); }
        catch (Exception e) { ready = false; tip = "Could not look at the game folder: " + e.Message; }

        var changed = ready != _shareReady || tip != _shareTip;
        _shareReady = ready;
        _shareTip = tip;
        if (!changed) return;
        Raise(nameof(CanShare));
        Raise(nameof(ShareTip));
    }

    private (bool Ready, string Tip) ShareState()
    {
        if (_mod is null) return (false, "Open a mod first.");

        var paks = InstallPaks();
        if (paks is null)
            return (false, "Nowhere to look for it: this profile's game folder does not resolve to a Paks folder.");

        var name = _mod.ContainerName;
        const string Why = "It packs what is INSTALLED, so what you share is what you tested.";

        // Matched PER LAYOUT rather than against either name, for the reason the old panel gives:
        // "either" also matches things that are not this mod (a legacy trio named without the
        // suffix), and the button would then claim an install that is not there.
        var already = ModInstall.Installed(paks).FirstOrDefault(i => i.Name.Equals(
            i.How == ModInstall.Layout.Plugin ? ModInstall.PluginName(name) : name,
            StringComparison.OrdinalIgnoreCase));
        if (already is null)
            return (false, string.Join(Environment.NewLine,
                "This mod is not installed, and the zip is made from what is installed.",
                "Press Build and install first, then come back.",
                "",
                Why));

        // Installed, but with no folder of its own: there is nothing for the zip to wrap.
        if (already.How == ModInstall.Layout.Legacy)
            return (false, string.Join(Environment.NewLine,
                "This mod is installed the old way - three loose files in " + ModInstall.ModsFolderName + ",",
                "with no folder of its own - and there is no folder to zip.",
                "Press Build and install again to get one.",
                "",
                Why));

        // Worked out here, in the refresh, and not every time the tooltip opens.
        //
        // PARAGRAPHS, never hard-wrapped lines: the tip is a wrapping text block, and a line
        // broken for a different width (the old panel's) reads ragged in this one.
        var early = ModInstall.EarlyFiles(paks, name);
        return (true, early.Count > 0
            ? string.Join(Environment.NewLine,
                "Packs the installed mod into a zip anyone can use, beside your project file.",
                "",
                "This mod changes game files that load before the game reads its Mods folder, so it has two halves and the zip mirrors the GAME folder, not just the mod's. " +
                $"Whoever downloads it extracts into SWZeroCompany -- the folder that holds {ModInstall.PluginsFolderName} and Content -- NOT into Mods, and both halves land where they belong:",
                $@"    SWZeroCompany\{ModInstall.PluginsFolderName}\{ModInstall.PluginName(name)}",
                $@"    SWZeroCompany\Content\Paks\{ModInstall.ModsFolderName}\{EarlyLoad.ContainerName(name)}.*",
                "",
                "Instructions for installing and removing it go in with it.",
                "",
                Why)
            : string.Join(Environment.NewLine,
                "Packs the installed mod into a zip anyone can use, beside your project file.",
                "",
                "The zip holds the mod's FOLDER, so whoever downloads it extracts into " +
                $@"SWZeroCompany\{ModInstall.PluginsFolderName} and is done. Instructions for installing and removing it go in with it.",
                "",
                Why));
    }

    /// <summary>
    /// Zip the installed mod beside its project file, say how big it is and where it extracts to,
    /// and open Explorer with the zip selected. The sentence is what gets copied into the Nexus
    /// page, so it says when the mod needs the SWZeroCompany folder rather than Mods.
    /// </summary>
    public async Task ShareAsync()
    {
        if (_mod is null) { Say("open a mod first"); return; }
        if (_zipping || _building) return;

        var mod = _mod;
        var paks = InstallPaks();
        if (paks is null)
        { ShareFailed("this profile's game folder does not resolve to a Paks folder"); return; }

        var name = mod.ContainerName;
        var plugin = ModInstall.PluginDirectory(paks, name);

        // A mod the old way (three files beside the game's, no folder of its own) has nothing for
        // ModShare to wrap, and the generic "is not there" would blame the wrong thing.
        if (plugin is null || !Directory.Exists(plugin))
        {
            ShareFailed(ModInstall.Installed(paks).Any(i => i.How == ModInstall.Layout.Legacy &&
                            i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ? "this mod is installed the old way (three loose files in ~mods), which has no folder to zip - Build and install it again to get one"
                : $"{(plugin ?? name)} is not there -- install the mod first, and share what was installed");
            return;
        }

        var zip = Path.Combine(Path.GetDirectoryName(mod.FilePath) ?? ".", ModInstall.PluginName(name) + ".zip");
        var shown = mod.DisplayName.Length > 0 ? mod.DisplayName : mod.Name;
        var early = ModInstall.EarlyFiles(paks, name);

        _zipping = true;
        BuildFailed = false;
        BuildStatus = "";
        RaiseShare();
        Say($"packing {ModInstall.PluginName(name)} for sharing...");

        // The packing lines are collected on the worker and said HERE, from the UI thread: said
        // from the worker they are posted, and the line after the await can overtake them, so the
        // "N file(s) ->" tally arrived after the verdict it belongs before.
        var packed = new List<string>();
        string? why = null;
        bool ok;
        try { ok = await Task.Run(() => ModShare.Zip(plugin, zip, shown, out why, packed.Add, early)); }
        catch (Exception e) { ok = false; why = e.Message; }

        _zipping = false;
        RaiseShare();
        foreach (var line in packed) Say(line);

        if (!ok) { ShareFailed(why ?? "could not pack it"); return; }

        ShareZip = zip;
        var said = $"{Path.GetFileName(zip)} is ready to upload -- {new FileInfo(zip).Length / 1024:N0} KB" +
                   (early.Count > 0
                       ? $@" -- it carries the Content\Paks\{ModInstall.ModsFolderName} half too, so it is extracted into SWZeroCompany, not {ModInstall.PluginsFolderName}"
                       : "");
        BuildFailed = false;
        BuildStatus = said;
        Say(said);
        Say($"   {zip}");

        // Straight to it in Explorer: a path in a status line is a path somebody has to retype,
        // and this one is several folders deep.
        RevealInExplorer(zip);
    }

    private void ShareFailed(string why)
    {
        BuildFailed = true;
        BuildStatus = why;
        SayBad("share: " + why);
    }

    private void RaiseShare()
    {
        Raise(nameof(CanShare));
        Raise(nameof(ShareLabel));
        Raise(nameof(CanBuild));
    }

    private void RevealInExplorer(string zip)
    {
#if DEV
        // A background run must not put a window on the author's desktop. What would have opened
        // is logged, so the test still witnesses that the step was reached with the right path.
        if (BackgroundMode.On)
        {
            Say($"   [dev, background] would have opened: explorer.exe /select,\"{zip}\"");
            return;
        }
#endif
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"")
                { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) { Say($"   could not open the folder: {e.Message}"); }
    }
}

using Avalonia.Threading;
using UEToolkit.Core;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// BUILD AND INSTALL, FROM THE EDITOR THAT AUTHORED THE EDIT.
///
/// His ask: "we should really put a build and install into the level editor so you don't need to
/// go through another program to build and install your map edit". It is the same complaint as
/// studio-ui-is-a-browser-not-an-authoring-tool, one app along -- the level editor is where a map
/// edit is MADE, and making it a read loop that hands off to a second program for the write half
/// is exactly the split he objected to before. Every probe this week cost a round trip: edit here,
/// switch to the Studio, build, install, launch.
///
/// IT DOES NOT BREACH "build mods through the STUDIO, never the CLI". That rule exists because a
/// CLI shortcut tests a route no user takes. A button in the level editor IS a route a user takes
/// -- a second real app, not a bypass -- and it runs the same three Core calls in the same order
/// the Studio does, so the two cannot drift into testing different things. The Studio's path stays
/// exactly as it was; two apps that both build is fine, one app that builds and another that
/// silently cannot is the trap.
/// </summary>
public sealed partial class Editor
{
    private bool _building;
    public bool Building
    {
        get => _building;
        set { if (Set(ref _building, value)) Raise(nameof(CanBuild)); }
    }

    public bool CanBuild => HasProject && _provider is not null && !_building;

    /// <summary>The read-back's own words, or why the build never got that far.</summary>
    private string _buildVerdict = "";
    public string BuildVerdict { get => _buildVerdict; private set => Set(ref _buildVerdict, value); }

    /// <summary>Good / bad / nothing yet, so the panel can colour the verdict without parsing it.</summary>
    private bool _buildFailed;
    public bool BuildFailed { get => _buildFailed; private set => Set(ref _buildFailed, value); }

    private bool _buildDone;
    public bool BuildDone { get => _buildDone; private set => Set(ref _buildDone, value); }

    /// <summary>Where the game's profile says its Paks live. Kept from the mount for the install.</summary>
    private GameProfile? _profile;

    /// <summary>
    /// SAVE, BUILD, READ BACK, INSTALL -- in that order, and stopping at the first thing that
    /// refuses.
    ///
    /// Saving first because the thing he is looking at is the thing he means: a build that
    /// silently used the last saved copy of the project would install a map edit missing whatever
    /// he did in the last minute, and it would look like the edit had not worked.
    /// </summary>
    public async Task BuildAndInstallAsync()
    {
        if (_project is null) { BuildVerdict = "no mod project is open"; BuildFailed = true; BuildDone = true; return; }
        if (_provider is null) { BuildVerdict = "no game is mounted"; BuildFailed = true; BuildDone = true; return; }
        if (_building) return;

        Building = true;
        BuildDone = false;
        BuildFailed = false;
        BuildVerdict = "building…";
        Status = "building " + _project.Name + "…";

        if (HasChanges)
        {
            var saved = SaveMoves();
            Log.Add("build: " + saved);
        }

        var project = _project;
        var provider = _provider;
        var paksDir = _profile?.PaksDirectory ?? "";

        var lines = new List<string>();
        var verdict = "";
        var failed = true;

        await Task.Run(() =>
        {
            void Say(string line) => lines.Add(line.TrimEnd());

            try
            {
                // THE GAME'S OWN REGISTRY, read past any deployed mod. Without this a rebuild
                // stacks the mod on a copy of itself, which is the Studio's first check too.
                var registry = ModBuild.PristineRegistry(provider, out var from);
                if (registry is null)
                {
                    verdict = "every AssetRegistry.bin in the game is a _P patch, so a build would " +
                              "stack this mod on a copy of itself. Take the installed mod out first.";
                    return;
                }
                Say($"registry: {from} ({registry.Length:N0} bytes)");

                var temp = Path.Combine(Path.GetTempPath(), "zc-level-editor-registry.bin");
                File.WriteAllBytes(temp, registry);

                // SOMEWHERE NOBODY LOOKS, for the reason the Studio gives: the build's output is a
                // flat scratch layout, and Install is what arranges it into the folder the game
                // reads. A second copy sitting next to the project gets mistaken for the real one.
                var outDir = Path.Combine(Path.GetTempPath(), "ZeroCompanyLevelEditor", "build",
                                          project.Name);

                var built = ModBuild.Build(provider, project, temp, outDir, project.ContainerName, Say);
                foreach (var s in built.Skipped) Say("   skipped: " + s);
                foreach (var w in built.Warnings) Say("   note: " + w);
                foreach (var f in new[] { built.Pak, built.Ucas, built.Utoc })
                {
                    try { Say($"   wrote {Path.GetFileName(f),-34} {new FileInfo(f).Length,14:N0}"); }
                    catch { /* the file is right there; a stat failure is not worth a message */ }
                }

                // THE GATE. Every fault this builder has ever had was caught by reading the output
                // back and comparing it against something known good; not one was caught by the
                // build agreeing with itself. A map edit's read-back now includes the new actors --
                // see ModVerify's spawn check, which did not exist until this button did.
                Say("reading it back…");
                var verify = ModVerify.Run(provider, project, built);
                foreach (var c in verify.All)
                    Say($"   {(c.Verdict == ModVerify.Verdict.Good ? " " : "!")} {c.What}: {c.Detail}");
                Say(verify.Summary);

                if (!verify.Clean)
                {
                    // NOT INSTALLED, and said plainly. The Studio's Install button has never
                    // consulted a verdict -- which is how a map edit with a failed check got into
                    // the game -- and repeating that here would make the check decoration.
                    verdict = verify.Summary + " — not installed. The log says which.";
                    return;
                }

                var paks = ModInstall.ResolvePaks(paksDir);
                if (paks is null)
                {
                    verdict = "built and read back clean, but this profile's game folder does not " +
                              "resolve to a Paks folder, so there is nowhere to install it.";
                    return;
                }
                if (built.Plugin is not null && ModInstall.PluginsDirectory(paks) is null)
                {
                    verdict = $"built and read back clean, but {paks} is not inside a game project, " +
                              "so there is no Mods folder to put it in.";
                    return;
                }
                if (!ModInstall.CanWrite(paks, project.ContainerName, out var blockedBy))
                {
                    // Says what was observed, without naming a culprit it cannot see. A running
                    // game is the usual holder; it is not the only one.
                    verdict = "built and read back clean, but something has " +
                              Path.GetFileName(blockedBy ?? "its old copy") +
                              " open. If the game is running, close it and press it again.";
                    return;
                }

                var where = ModInstall.Install(built, paks, project.ContainerName, Say);
                verdict = $"{verify.Summary} and installed to {where}";
                failed = false;
            }
            catch (Exception e)
            {
                Say($"FAILED: {e.GetType().Name}: {e.Message}");
                verdict = e.Message;
            }
        });

        foreach (var l in lines) Log.Add(l);
        BuildVerdict = verdict;
        BuildFailed = failed;
        BuildDone = true;
        Status = (failed ? "build: " : "") + verdict;
        Building = false;
    }
}

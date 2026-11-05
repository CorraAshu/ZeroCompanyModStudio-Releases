using Avalonia.Threading;
using CUE4Parse.FileProvider;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// BUILDING THE MOD AND PUTTING IT IN THE GAME.
///
/// Its own file for the reason the ImGui studio splits ModPanel/Build.cs off: this is the only
/// part of the Studio that writes outside the project file, and everything it does is either a
/// call into Core or a sentence explaining why it refused.
///
/// ONE PRESS, THREE ACTS, and the read-back is the GATE between the second and the third. Every
/// fault this builder has ever had was caught by reading the output back and comparing it against
/// something known good; not one was caught by the build agreeing with itself. So a mod that does
/// not come back clean is NOT installed, and the bar says so.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// WHAT WOULD STOP THIS MOD BUILDING, asked of the project's own validator.
    ///
    /// <see cref="ModProject.Validate"/> is what the ImGui studio's build bar counts, and it is
    /// the thing that knows the rules — two parts sharing an id, a slot that needs a mesh and has
    /// none, an id ending in _digits that UE will not load. A second opinion written here is how
    /// two tools come to disagree about whether the same project can be built.
    /// </summary>
    private void RecheckProblems()
    {
        var was = _problems;
        _problems = _mod is null ? [] : ModProject.Validate([_mod]);

        // SAID IN THE LOG, IN RED, and only when the list CHANGES. This runs after every edit,
        // so saying them every time would bury the log under the same two sentences — and saying
        // them nowhere is what put them in the build bar, where three share one trimmed line and
        // the rest are a tooltip. The author: "it should be inside the log in red".
        if (!_problems.SequenceEqual(was, StringComparer.Ordinal))
        {
            foreach (var p in _problems.Except(was, StringComparer.Ordinal)) SayBad(p);

            // AND WHEN THE LAST ONE GOES, because a red line with nothing after it reads as a
            // mod that is still broken.
            if (_problems.Count == 0 && was.Count > 0)
                Say($"nothing is in the way now — {was.Count} thing(s) fixed");
        }

        Raise(nameof(Problems));
        Raise(nameof(HasProblems));
        Raise(nameof(ProblemsSay));
        Raise(nameof(BuildSays));
        Raise(nameof(BuildReady));
        Raise(nameof(BuildBlocked));
        Raise(nameof(CanBuild));
        Raise(nameof(BuildWhere));
        Raise(nameof(BuildWhereTip));
    }

    private List<string> _problems = [];

    /// <summary>Everything the validator has against this project, in its own words.</summary>
    public IReadOnlyList<string> Problems => _problems;
    public bool HasProblems => _problems.Count > 0;

    /// <summary>
    /// The problems as one line, because a count with no way to reach what it counts is a dead
    /// end — the old studio made the count a BUTTON for the same reason.
    /// </summary>
    public string ProblemsSay => string.Join("   ·   ", _problems.Take(3)) +
                                 (_problems.Count > 3 ? $"   ·   and {_problems.Count - 3} more" : "");

    private bool _building;

    public bool Building
    {
        get => _building;
        private set
        {
            if (!Set(ref _building, value)) return;
            Raise(nameof(CanBuild));
            Raise(nameof(BuildLabel));
            Raise(nameof(BuildSays));
            Raise(nameof(BuildReady));
            Raise(nameof(BuildBlocked));
            Raise(nameof(BuildBusy));
            Raise(nameof(CanShare));
        }
    }

    /// <summary>
    /// Whether the button does anything.
    ///
    /// Every reason it might not is SAID beside it rather than left to a greyed control: a
    /// disabled button with no explanation is the shape of thing that makes somebody believe the
    /// tool is broken.
    /// </summary>
    public bool CanBuild => !_building && !_zipping && _provider is not null &&
                            _mod is { HasContent: true } && _problems.Count == 0;

    public bool BuildBusy => _building;
    public bool BuildReady => CanBuild;
    public bool BuildBlocked => !_building && _problems.Count > 0;

    public string BuildLabel => _building ? "Building…" : "Build and install";

    public string BuildSays =>
        _building ? "Building…"
        : _mod is null ? "no mod open"
        : _problems.Count > 0
            ? _problems.Count == 1 ? "1 thing needs fixing" : $"{_problems.Count} things need fixing"
        : !_mod.HasContent ? "nothing in it yet"
        : _provider is null ? "the game is not mounted"
        : "Ready";

    /// <summary>
    /// Where it goes, said before it goes there rather than after. A mod that changes one of the
    /// game files the game reads BEFORE it mounts its Mods folder (<see cref="EarlyLoad"/>) goes in
    /// two places: its folder, and a second container in ~mods. The bar used to name only the first,
    /// so it described an install that was half of the real one.
    /// </summary>
    public string BuildWhere => _mod is null ? "" :
        $"→   SWZeroCompany\\Mods\\{_mod.Name}" +
        (ExpectsEarlyHalf(_mod)
            ? $"   +   Paks\\{ModInstall.ModsFolderName}\\{EarlyLoad.ContainerName(_mod.ContainerName)}"
            : "");

    /// <summary>The bar's address in full, for a mod with two halves; null (no tooltip at all) otherwise.</summary>
    public string? BuildWhereTip => _mod is null || !ExpectsEarlyHalf(_mod) ? null :
        "This mod changes game files that load before the game reads its Mods folder, so it installs in " +
        "two places: its own folder in SWZeroCompany\\Mods, and a second container, " +
        $"{EarlyLoad.ContainerName(_mod.ContainerName)}, in SWZeroCompany\\Content\\Paks\\{ModInstall.ModsFolderName}, " +
        "where the game's own scan finds it.";

    /// <summary>
    /// Whether a build of this project will have the early half. The build decides it from the
    /// mod's overrides and from the game files its slot sets are stamped onto: those on
    /// <see cref="EarlyLoad"/>'s measured list go into the second container. Worked out here from
    /// the same two lists, so the bar can say it before anything is built.
    /// </summary>
    private static bool ExpectsEarlyHalf(ModProject mod) =>
        mod.Overrides.Any(o => EarlyLoad.IsEarly(o.Target)) ||
        mod.SlotSets.Any(set => set.Targets.Any(t => t.StartsWith('/') && EarlyLoad.IsEarly(t)));

    private string _buildStatus = "";

    /// <summary>What the last build or install actually did, in one sentence.</summary>
    public string BuildStatus
    {
        get => _buildStatus;
        private set { if (Set(ref _buildStatus, value)) Raise(nameof(HasBuildStatus)); }
    }

    public bool HasBuildStatus => _buildStatus.Length > 0;

    private bool _buildFailed;

    public bool BuildFailed
    {
        get => _buildFailed;
        private set { if (Set(ref _buildFailed, value)) Raise(nameof(BuildWorked)); }
    }

    public bool BuildWorked => !_buildFailed;

    /// <summary>
    /// Build it, read it back, and if it is clean put it in the game.
    ///
    /// All of it runs off the UI thread and reports through <see cref="Say"/>, so the log carries
    /// the build lines, the read-back's findings and the install in the order they happened —
    /// one account of what was done, rather than a panel and a log that can disagree about it.
    /// </summary>
    public async Task BuildAndInstallAsync()
    {
        if (_mod is null) { Say("open a mod first — there is nothing to build"); return; }
        if (_provider is null) { Say("the game is not mounted yet, and a build reads it"); return; }
        if (!_mod.HasContent) { Say($"{_mod.Name}: there is nothing in this mod yet"); return; }

        RecheckProblems();
        if (_problems.Count > 0)
        {
            // NAMED, not counted. The bar has the number; what somebody needs in order to act is
            // which thing, and the log is where the rest of the build's account goes anyway.
            Say($"{_mod.Name}: {_problems.Count} thing(s) need fixing first");
            foreach (var p in _problems) Say("   " + p);

            // NOT into BuildStatus as well. The bar already carries the problems on their own
            // line, and putting the first one in the status too made it say the same sentence
            // twice, one above the other, in a bar that is meant to be one line.
            BuildFailed = true;
            return;
        }

        var mod = _mod;
        var provider = _provider;
        var profile = _profile;

        Building = true;
        BuildFailed = false;
        BuildStatus = "";

        // SOMEWHERE NOBODY LOOKS. The build's output is a flat scratch layout — registry,
        // descriptor and the containers side by side — and Install is what arranges it into the
        // mod folder the game reads. Written beside the projects it gets found, compared with the
        // installed copy, and taken for a different, wrong build.
        var into = Path.Combine(Path.GetTempPath(), "ZeroCompanyModStudio", "build", mod.Name);

        // THE OUTCOME COMES BACK, rather than being posted at the UI thread from inside.
        //
        // It was assigned through OnUi, which POSTS — so anything reading BuildStatus right after
        // the await read the value from before the build. The flag that drives this without a
        // mouse printed "says=Building…, status=" about a build that had finished and installed,
        // which is a report that would have made a working build look broken.
        var outcome = await Task.Run<(bool Failed, string Status)>(() =>
        {
            try
            {
                // A _P PATCH IS THIS MOD'S OWN INSTALLED COPY. Building on one stacks the mod on
                // a copy of itself and the registry comes out holding two of everything.
                var registry = ModBuild.PristineRegistry(provider, out var from);
                if (registry is null)
                    throw new InvalidOperationException(
                        "every AssetRegistry.bin in the game is a _P patch, so a build would " +
                        "stack this mod on a copy of itself. Remove the installed mod first.");

                Say($"registry: {from} ({registry.Length:N0} bytes)");
                var temp = Path.Combine(Path.GetTempPath(), "zcms-avalonia-registry.bin");
                File.WriteAllBytes(temp, registry);

                // THE PAKS FOLDER, so the build's "another mod already changes this" check has
                // the installed mods to look at. Without it the check scans a mount that holds
                // the game and nothing else, and reassures every time.
                var result = ModBuild.Build(provider, mod, temp, into, mod.ContainerName, Say,
                                            profile?.PaksDirectory);

                // THE BUILD'S OWN FINDINGS. They are collected on the result rather than passed
                // to the log callback, so without these three loops they are readable nowhere.
                foreach (var s in result.Skipped) Say("   skipped: " + s);
                foreach (var w in result.Warnings) Say("   note: " + w);
                foreach (var f in new[] { result.Pak, result.Ucas, result.Utoc })
                {
                    try { Say($"   wrote {Path.GetFileName(f),-34}{new FileInfo(f).Length,14:N0}"); }
                    catch (Exception) { /* the file is right there; a stat failure is not a message */ }
                }

                Say("reading it back…");
                var verify = ModVerify.Run(provider, mod, result);

                // EVERYTHING IT FOUND, passes included, in the order it found them. A check whose
                // result nobody can see is indistinguishable from one that never ran.
                foreach (var c in verify.All.Where(c => c.Verdict != ModVerify.Verdict.Good))
                    Say($"   {c.What}: {c.Detail}");
                foreach (var c in verify.Overall.Where(c => c.Verdict == ModVerify.Verdict.Good))
                    Say($"   {c.What}: {c.Detail}");
                foreach (var p in verify.Parts)
                {
                    Say($"   {p.Id}");
                    foreach (var c in p.Checks.Where(c => c.Verdict == ModVerify.Verdict.Good))
                        Say($"      {c.What}: {c.Detail}");
                }
                Say(verify.Summary);

                if (!verify.Clean)
                {
                    var bad = verify.All.Count(c => c.Verdict == ModVerify.Verdict.Bad);
                    return Stop($"Built, but reading it back found {bad} fault(s), so it was NOT " +
                                "installed. They are in the log.");
                }

                return InstallBuilt(result, mod, profile, provider);
            }
            catch (Exception e) { return Stop(e.Message); }
        });

        BuildFailed = outcome.Failed;
        BuildStatus = outcome.Status;
        Building = false;
        RefreshShare();   // installed, or taken out: the share button follows

        (bool, string) Stop(string why)
        {
            Say("build: " + why);
            return (true, why);
        }
    }

    /// <summary>
    /// The build's last step: put a clean result in the game, or say what stopped it.
    ///
    /// Every refusal here names what was OBSERVED rather than a culprit it cannot see. A running
    /// game is the usual thing holding the old copy open; it is not the only one.
    /// </summary>
    private (bool Failed, string Status) InstallBuilt(
        ModBuild.Result r, ModProject mod, GameProfile? profile, IFileProvider provider)
    {
        (bool, string) Stop(string why)
        {
            Say("install: " + why);
            return (true, why);
        }

        var paks = InstallPaks();
        if (paks is null)
            return Stop("Built and checked, but this profile's game folder does not resolve to a " +
                        "Paks folder, so there is nowhere to install it.");

        if (r.Plugin is not null && ModInstall.PluginsDirectory(paks) is null)
            return Stop($"Built and checked, but {paks} is not inside a game project, so there " +
                        "is no Mods folder to put it in.");

        // THE CONTAINER NAME, NOT THE MOD'S NAME, at every one of these.
        //
        // ModInstall's `modName` parameter means the CONTAINER name -- the one carrying `_P` --
        // and it derives the plugin folder from that with PluginName(). Handing it the already
        // stripped name looked right, because stripping a stripped name is a no-op and the folder
        // came out correct, so the mod installed and the log said so. What was wrong was the only
        // part nobody reads: the three container files were written as `Name.pak/.ucas/.utoc`
        // instead of `Name_P....`, and `_P` is what mounts a container at PATCH priority. Without
        // it a mod cannot win a lookup against the game's own copy -- so a new part still appears
        // and every OVERRIDE silently loses, which is most of what this Studio writes.
        //
        // Measured 2026-09-20: all fourteen mods built by the ImGui Studio and confirmed in game
        // carry `_P`; the first one this Studio installed was the only one that did not.
        if (!ModInstall.CanWrite(paks, mod.ContainerName, out var blockedBy))
            return Stop("Built and checked, but not installed: something has " +
                        Path.GetFileName(blockedBy ?? "its old copy") + " open. If the game is " +
                        "running, close it and build again.");

        // THE BASE MOD THIS ONE DEPENDS ON. A weapon routed onto a custom Armoury shelf is
        // invisible without the mod that DEFINES that shelf — it builds, installs, reads back
        // clean and never appears, which from the outside is a mod that failed to install. So it
        // goes in first, and the person is TOLD: a tool that quietly installs a second mod into
        // somebody's game is worse than one that says too much.
        string? alsoInstalled = null;
        if (!BaseMod.IsBaseMod(mod.Name) && BaseMod.NeedsWeaponClasses(mod))
        {
            var outcome = BaseMod.Ensure(provider, paks, out var baseWhy, Say);
            if (baseWhy is not null) Say("   " + baseWhy);
            if (outcome is BaseMod.Outcome.Failed or BaseMod.Outcome.NotBundled)
                return Stop("Built and checked, but this mod's weapons appear on a custom " +
                            "Armoury shelf and " + baseWhy);
            if (outcome == BaseMod.Outcome.Installed) alsoInstalled = BaseMod.WeaponClasses;
        }

        // ASKED BEFORE COPYING. Two mods defining the same package cannot both apply, and which
        // of them wins is not worth guessing at — so the answer belongs in front of the person
        // while they can still do something about it, not in a bug report later.
        var clashes = new List<ModInstall.Clash>();
        var registryClashes = new List<ModInstall.RegistryClash>();
        if (profile is not null)
        {
            clashes = ModInstall.ConflictsWith(r, paks, profile.Game, mod.ContainerName, Say);
            // Only a classic build is in the registry fight: a plugin mod's registry holds its own
            // entries in its own folder, so asking would list every legacy mod on the machine as
            // clashing with something that cannot clash with it.
            registryClashes = r.Plugin is not null
                ? []
                : ModInstall.RegistryConflicts(paks, profile.Game, mod.ContainerName, Say);
        }

        try
        {
            var where = ModInstall.Install(r, paks, mod.ContainerName, Say);

            // WHAT THIS COPY WAS BUILT FROM, so "older than your edits" can be answered by
            // content rather than by the project file's clock.
            try { BuiltFrom.Record(StudioSettings.DataDir, mod.FilePath, mod.Serialize()); }
            catch (Exception) { /* a staleness note is not worth failing an install over */ }

            var bad = clashes.Count(c => c.Kind != ModInstall.ClashKind.Replaces);
            var said =
                registryClashes.Count > 0
                    ? $"Installed to {where}, but {registryClashes.Count} other mod(s) ship an " +
                      "asset registry too. Only one can win, and the winner has no record of the " +
                      "other's parts — so one of them will be missing from the game entirely."
                : bad > 0
                    ? $"Installed to {where}, but {bad} package(s) clash with another mod — only " +
                      "one of them will apply. They are named in the log."
                : r.Plugin is not null && r.Early is { } early
                    ? $"Installed to {where}. Most of it is in its own folder, which can sit beside " +
                      "anyone else's mod without either of them knowing. But " +
                      $"{early.Packages.Count} game file(s) are loaded before the game reads that folder, so " +
                      $"{early.Name} went into {ModInstall.ModsFolderName}, where the game's own scan finds it " +
                      "-- and that half replaces the game's copy of those files, so it can clash with another " +
                      "mod that changes the same ones."
                : r.Plugin is not null
                    ? $"Installed to {where}. Its own folder, sharing nothing — so it can sit " +
                      "beside anyone else's mod without either of them knowing."
                    : $"Installed to {where}. Nothing in the game folder was overwritten.";

            if (alsoInstalled is not null)
                said += $"  Also installed \"{alsoInstalled}\", which defines the custom Armoury " +
                        "shelves this mod's weapons appear on — share it alongside this mod.";

            Say(said);
            foreach (var line in WhereToLook(mod)) Say("   " + line);
            return (false, said);
        }
        catch (Exception e) { return Stop(e.Message); }
    }

    /// <summary>
    /// WHERE IN THE GAME TO GO AND LOOK for what this mod adds.
    ///
    /// The build has known this all along and never said it: it holds the parts, their slots and
    /// the shelf a weapon was routed onto. Somebody who has just built their first mod has
    /// exactly one question, and "Built 4 part(s)" does not answer it.
    /// </summary>
    private static List<string> WhereToLook(ModProject mod)
    {
        var lines = new List<string>();

        foreach (var group in mod.Parts.GroupBy(p => p.Slot))
        {
            var named = group.Select(p => p.Display.Length > 0 ? p.Display : p.Id).Take(3);
            var more = group.Count() > 3 ? $" and {group.Count() - 3} more" : "";
            lines.Add($"Customise a character  →  {group.Key}  →  {string.Join(", ", named)}{more}");
        }

        foreach (var c in mod.Copies)
        {
            if (WeaponAuthor.IdentityOf(c.Id) is null) continue;
            var shelf = WeaponAuthor.OwnClassOf(mod, c.Id) is { } own
                ? own[(own.LastIndexOf('.') + 1)..].ToUpperInvariant()
                : WeaponAuthor.InCustomClass(mod, c.Id) == true ? "the CUSTOM shelf"
                : "its own class";
            lines.Add($"Armoury  →  {shelf}  →  {(c.Display.Length > 0 ? c.Display : c.Id)}");
        }

        if (mod.Specs.Count > 0) lines.Add("The SPECIALIZATION tab, when you make a recruit");

        if (mod.Overrides.Count > 0)
            lines.Add(mod.Overrides.Count == 1
                ? "1 of the game's own things is changed — look where it already was"
                : $"{mod.Overrides.Count} of the game's own things are changed — look where they " +
                  "already were");

        if (lines.Count == 0) lines.Add("nothing that shows up on its own yet");
        return lines;
    }

    /// <summary>
    /// Take it back out of the game.
    ///
    /// The record of what it was built from goes with it: nothing is installed to be stale
    /// against, and a record left behind would answer about a mod that is no longer there.
    /// </summary>
    public async Task UninstallAsync()
    {
        if (_mod is null) { Say("open a mod first"); return; }

        var mod = _mod;
        var paks = InstallPaks();
        if (paks is null)
        { Say("this profile's game folder does not resolve to a Paks folder"); return; }

        Building = true;
        var outcome = await Task.Run<(bool Failed, string Status)>(() =>
        {
            try
            {
                var removed = ModInstall.Uninstall(paks, mod.ContainerName, Say);
                BuiltFrom.Forget(StudioSettings.DataDir, mod.FilePath);
                if (RecruitScript.Uninstall(paks, mod.Name, Say))
                    removed.Add("the recruit script");

                var said = removed.Count == 0
                    ? "Nothing of this mod was installed."
                    : $"Removed {removed.Count} file(s) from the game.";
                Say(said);
                return (false, said);
            }
            catch (Exception e)
            {
                Say("uninstall: " + e.Message);
                return (true, e.Message);
            }
        });

        BuildFailed = outcome.Failed;
        BuildStatus = outcome.Status;
        Building = false;
        RefreshShare();   // installed, or taken out: the share button follows
    }
}

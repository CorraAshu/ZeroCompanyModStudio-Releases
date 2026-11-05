using System.Diagnostics;

namespace UEToolkit.Core;

/// <summary>
/// Which BUILD of a game is installed, and whether the mappings still match it.
///
/// A usmap describes the property layout of one build. When the game updates, the layout can move
/// and the old map goes stale -- and the failure is silent, which is the whole problem: an
/// unversioned cook read with the wrong mappings does not error, it produces plausible garbage.
/// That has already cost this project real time on two games, so it is worth detecting rather
/// than diagnosing.
///
/// The check is EXACT here rather than a heuristic, which was a pleasant surprise:
///
///     the exe    SWZeroCompany.exe  FileVersion 196320
///     the usmap  SWZeroCompany-5.6.1-196320+++ProjectBruno+Stable-a1e7f571.usmap
///
/// Both carry the same engine changelist, so "is this map for this build" is a comparison of two
/// integers taken from metadata that already exists -- no hashing a 230 MB executable, and no
/// remembering anything. When a usmap does not carry a changelist the answer falls back to
/// remembering the build we last ran against, which works for any game but only notices a change
/// AFTER it happens.
/// </summary>
public static class GameInstall
{
    /// <summary>Executables that ship beside a UE game and are never the game.</summary>
    private static readonly string[] NotTheGame =
    {
        "CrashReportClient", "EpicWebHelper", "UnrealCEFSubProcess", "EasyAntiCheat",
        "UnrealEditor", "BattlEye", "Launcher",
    };

    /// <summary>The installed build: its version string, and the size as a fallback identity.</summary>
    public sealed record Build(string Executable, string? Version, long Bytes)
    {
        /// <summary>
        /// What to compare between runs: BOTH the version and the size, always.
        ///
        /// Preferring the version alone looked reasonable and was wrong, which four games showed
        /// immediately. One game's executable reports `UE5-CL-0` -- a placeholder the studio
        /// never fills in -- so a version-only identity is constant across every update the game
        /// will ever ship, and the one game whose update silently broke extraction is exactly the
        /// one it would never have noticed. Others carry no version resource at
        /// all. The size moves whenever the build does, in all four cases.
        /// </summary>
        public string Identity => $"{(string.IsNullOrWhiteSpace(Version) ? "?" : Version)}/{Bytes}";

        public string Describe => string.IsNullOrWhiteSpace(Version)
            ? $"{Path.GetFileName(Executable)} ({Bytes:N0} bytes)"
            : $"{Path.GetFileName(Executable)} {Version}";
    }

    // ------------------------------------------------------------------ finding the game

    /// <summary>
    /// The game's executable, from its Paks folder. Walks up to the install root and looks in
    /// Binaries, which is where every UE game puts it.
    /// </summary>
    public static string? FindExecutable(string paksDirectory)
    {
        try
        {
            for (var dir = new DirectoryInfo(paksDirectory); dir is not null; dir = dir.Parent)
            {
                var binaries = Path.Combine(dir.FullName, "Binaries");
                if (!Directory.Exists(binaries)) continue;

                var exes = Directory.EnumerateFiles(binaries, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !NotTheGame.Any(n => Path.GetFileNameWithoutExtension(f)
                        .Contains(n, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (exes.Count == 0) continue;

                // The largest. A shipping build dwarfs every helper beside it, and this needs no
                // list of the naming conventions studios use for the real one.
                return exes.OrderByDescending(f => new FileInfo(f).Length).First();
            }
        }
        catch (Exception) { /* an unreadable install is simply one we cannot identify */ }
        return null;
    }

    /// <summary>What is installed right now, or null when the executable cannot be found or read.</summary>
    public static Build? Read(string paksDirectory)
    {
        var exe = FindExecutable(paksDirectory);
        if (exe is null) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            var version = info.FileVersion?.Trim();
            return new Build(exe, string.IsNullOrWhiteSpace(version) ? null : version,
                             new FileInfo(exe).Length);
        }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ mappings

    /// <summary>
    /// The engine changelist a usmap filename carries, or null when it does not carry one.
    ///
    /// Dumped maps are named `&lt;Game&gt;-&lt;engine&gt;-&lt;changelist&gt;+++&lt;branch&gt;-&lt;hash&gt;.usmap`. The
    /// changelist is the only all-digit, several-digit field, which is specific enough to find
    /// without depending on position -- and a renamed file simply answers null rather than
    /// answering wrongly.
    /// </summary>
    public static string? MappingChangelist(string usmapPath)
    {
        if (string.IsNullOrWhiteSpace(usmapPath)) return null;

        var name = Path.GetFileNameWithoutExtension(usmapPath);
        return name.Split('-', '+', '_')
                   .FirstOrDefault(part => part.Length >= 4 && part.All(char.IsAsciiDigit));
    }

    /// <summary>
    /// A usmap shipped beside the tool for this game, or null.
    ///
    /// Bundling one is what makes the first run work without asking a person to find a file they
    /// have no way of producing. Matching is on the game's own name, so several can ship together
    /// and the right one is chosen without configuration.
    ///
    /// **AND THE NEWEST OF THOSE, which is not what this did and cost a release.** The tie-break
    /// was filename LENGTH, on the reasoning that a longer name is a more specific game match. Two
    /// maps for the SAME game differ only in the changelist -- `...-196320+++...` against
    /// `...-196985+++...` -- so their names are exactly the same length, the sort is arbitrary, and
    /// directory order hands back the alphabetically first, which is the OLDEST.
    ///
    /// That is not a corner case, it is the upgrade path: the release notes say to unzip over the
    /// top, which does not delete the previous map, so everybody who updated in place ended up
    /// with two and the tool went on reading the stale one. Reported within the hour of 1.4.2, by
    /// somebody who had done exactly as they were told.
    ///
    /// So: the name match still decides WHICH GAME (longest wins, `SWZeroCompany` over a shorter
    /// name that is a substring of it), and the changelist then decides WHICH BUILD. A map whose
    /// name carries no changelist sorts below any that does, rather than winning by accident.
    /// </summary>
    public static string? BundledMappings(string appDirectory, string paksDirectory)
    {
        try
        {
            var dir = Path.Combine(appDirectory, "mappings");
            if (!Directory.Exists(dir)) return null;

            var maps = Directory.GetFiles(dir, "*.usmap");
            if (maps.Length == 0) return null;
            if (maps.Length == 1) return maps[0];

            var path = paksDirectory.Replace('\\', '/');
            return maps.Where(m => path.Contains(Path.GetFileNameWithoutExtension(m).Split('-')[0],
                                                 StringComparison.OrdinalIgnoreCase))
                       // Which GAME first -- a longer name beats one that is merely a substring.
                       .OrderByDescending(m => Path.GetFileNameWithoutExtension(m).Split('-')[0].Length)
                       // Then which BUILD: the highest changelist the filename states. Parsed by
                       // the same reader the startup check uses, so "which map" and "is it stale"
                       // can never disagree about what a filename says.
                       .ThenByDescending(m => long.TryParse(MappingChangelist(m), out var cl) ? cl : -1)
                       .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The engine version a usmap filename states, as an <see cref="CUE4Parse.UE4.Versions.EGame"/>.
    ///
    /// Better evidence than anything else available at setup time. Guessing the version from the
    /// INSTALL PATH is inference about a folder someone chose; guessing it from the pak layout
    /// only separates UE4 from UE5. The usmap was dumped from the running game and its name says
    /// `-5.6.1-`, which is the engine that produced the cook the map describes.
    ///
    /// It does NOT beat a confident title match. A game with its own EGame entry (a title-specific
    /// engine fork in CUE4Parse's list) encodes fork behaviour that a generic GAME_UE4_27 does not,
    /// and reading meshes wrong is what that costs. This only fills in for a guess.
    /// </summary>
    public static CUE4Parse.UE4.Versions.EGame? EngineFromMappings(string? usmapPath)
    {
        if (string.IsNullOrWhiteSpace(usmapPath)) return null;

        foreach (var seg in Path.GetFileNameWithoutExtension(usmapPath).Split('-'))
        {
            var bits = seg.Split('.');
            if (bits.Length < 2) continue;
            if (!int.TryParse(bits[0], out var major) || major is < 4 or > 5) continue;
            if (!int.TryParse(bits[1], out var minor)) continue;

            return Enum.TryParse<CUE4Parse.UE4.Versions.EGame>(
                $"GAME_UE{major}_{minor}", out var g) ? g : null;
        }
        return null;
    }

    // ------------------------------------------------------------------ remembering a build

    /// <summary>
    /// The build a game was last mounted at, kept BESIDE the profile rather than in it.
    ///
    /// This lived in the profile for about ten minutes and was wrong there. Profiles are curated,
    /// hand-edited, version-controlled configuration -- every one of ours carries a long `_Notes`
    /// block of what was measured about that game -- and "the build we saw last time" is runtime
    /// state. Writing it back on every mount rewrote those files automatically: the first attempt
    /// silently deleted sixty lines of notes, and even once that was fixed it expanded every
    /// defaulted field and dirtied the working tree on every launch.
    ///
    /// Config is what a person writes; state is what the program observes. Keeping the program
    /// out of the first one is why this is a separate file.
    /// </summary>
    private static string BuildsFile(string dataDir) => Path.Combine(dataDir, "gamebuilds.json");

    public static string? Remembered(string dataDir, string profileName)
    {
        try
        {
            var f = BuildsFile(dataDir);
            if (!File.Exists(f)) return null;
            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(f));
            return all is not null && all.TryGetValue(profileName, out var v) ? v : null;
        }
        catch (Exception) { return null; }
    }

    public static void Remember(string dataDir, string profileName, string identity)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var f = BuildsFile(dataDir);
            var all = File.Exists(f)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                      File.ReadAllText(f)) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
            // Nothing to say when it is already written down: a start that learns nothing new leaves
            // the file (and its timestamp, and a folder we cannot write to) alone.
            if (all.TryGetValue(profileName, out var already) && already == identity) return;
            all[profileName] = identity;
            File.WriteAllText(f, System.Text.Json.JsonSerializer.Serialize(
                all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* forgetting is a worse next run, not a failed one */ }
    }

    // ------------------------------------------------------------------ the verdict

    public enum Freshness
    {
        /// <summary>Nothing to compare. Not a problem, and not a reassurance either.</summary>
        Unknown,
        /// <summary>The mappings are for this build.</summary>
        Matches,
        /// <summary>The game changed since we last ran. The mappings MAY be stale.</summary>
        GameChanged,
        /// <summary>The mappings are demonstrably for a different build.</summary>
        Stale,
    }

    public sealed record Verdict(Freshness What, string Say, string? Identity)
    {
        public bool WorthSaying => What is Freshness.GameChanged or Freshness.Stale;
    }

    /// <summary>
    /// Whether the profile's mappings suit the installed build.
    ///
    /// Deliberately a WARNING and never a refusal. A stale usmap does not always break anything --
    /// the layout may not have moved for the things a person is looking at -- and being locked out
    /// of a tool by a check that is only probably right is worse than being told. A wrong ENGINE
    /// VERSION is the one that costs everything, and that is a different setting.
    /// </summary>
    public static Verdict Check(GameProfile profile, string? remembered)
    {
        var build = Read(profile.PaksDirectory);
        if (build is null)
            return new Verdict(Freshness.Unknown, "no executable found to check against", null);

        // The precise test: both sides name the same engine changelist.
        var mapCl = MappingChangelist(profile.MappingsFile ?? "");
        if (mapCl is not null && !string.IsNullOrWhiteSpace(build.Version))
        {
            if (build.Version!.Contains(mapCl, StringComparison.Ordinal))
                return new Verdict(Freshness.Matches,
                                   $"mappings match the installed build ({mapCl})", build.Identity);

            return new Verdict(Freshness.Stale,
                $"the game is build {build.Version} but the mappings are for {mapCl}. " +
                "Textures and properties may read wrongly. " + Branding.StaleMappingsAdvice,
                build.Identity);
        }

        // The fallback: did it change since we last ran?
        if (string.IsNullOrWhiteSpace(remembered))
            return new Verdict(Freshness.Unknown, $"first run against {build.Describe}", build.Identity);

        if (!string.Equals(remembered, build.Identity, StringComparison.Ordinal))
            return new Verdict(Freshness.GameChanged,
                $"the game was updated since {Branding.Product} last ran it " +
                $"({remembered} -> {build.Identity}). The mappings may be stale.",
                build.Identity);

        return new Verdict(Freshness.Matches, $"unchanged since last run ({build.Identity})",
                           build.Identity);
    }
}

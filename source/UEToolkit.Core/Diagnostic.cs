namespace UEToolkit.Core;

/// <summary>
/// Everything somebody would otherwise be asked for, as one block of text they can paste.
///
/// EVERY BUG FIXED IN THIS TOOL'S FIRST WEEK CAME FROM A USER REPORT, and every one of them
/// arrived as a screenshot. A scrolling log, photographed; an error string read back out of a
/// JPEG; a round trip spent asking "can you also send a photo of the log". Two of those reports
/// turned out to hinge on a fact nobody had thought to include -- which version they were running.
/// One was on 1.0 hand-editing a settings file around something fixed in 1.1.
///
/// So the report says what it is BEFORE it says what happened: the version, the build, the
/// profile, what is mounted, what is installed. None of that is interesting until it is the
/// answer, and it is the answer often enough to be worth six lines.
///
/// It is assembled here rather than in the panel because it belongs to the tool rather than to the
/// window: the same text is worth having from a command line, and a report that differs depending
/// on where it was taken from is a report you have to ask questions about.
/// </summary>
public static class Diagnostic
{
    /// <summary>
    /// The header: what this copy is and what it is looking at.
    ///
    /// Deliberately free of anything identifying: it names the game's build and the mods by their
    /// own display names, never a path. Somebody pasting this into a public thread should not be
    /// pasting their username with it, and a full path is the usual way that happens.
    ///
    /// THE LOG UNDERNEATH IS A DIFFERENT MATTER and the interface says so rather than pretending
    /// otherwise: a log's job is to say where things are, so it carries the folders it printed.
    /// Sanitising it would be worse than warning about it -- the paths are frequently the answer.
    /// </summary>
    public sealed record About(string Product, string Version, string Profile, string GameBuild,
                               int Containers, int Files, List<string> Mods, string OpenMod);

    /// <summary>One block of text: the header, then the log.</summary>
    public static string Report(About about, IEnumerable<string> log)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{about.Product} {about.Version}");
        text.AppendLine($"windows {Environment.OSVersion.Version}  .net {Environment.Version}");
        text.AppendLine($"profile {(about.Profile.Length > 0 ? about.Profile : "(none)")}");
        text.AppendLine($"game    {(about.GameBuild.Length > 0 ? about.GameBuild : "(not read)")}");
        text.AppendLine($"mounted {about.Containers:N0} container(s), {about.Files:N0} file(s)");
        text.AppendLine(about.Mods.Count == 0
            ? "mods    none installed"
            : $"mods    {about.Mods.Count}: {string.Join(", ", about.Mods)}");
        if (about.OpenMod.Length > 0) text.AppendLine($"open    {about.OpenMod}");
        text.AppendLine();
        text.AppendLine("--- log ---");
        foreach (var line in log) text.AppendLine(line);
        return text.ToString();
    }

    /// <summary>
    /// Just the last build, for a report about a build.
    ///
    /// A session's log runs to hundreds of lines and most of it is the mount. What somebody
    /// actually needs to see is the build that failed, which begins at the last line that opens
    /// one -- so this finds that line and keeps everything after it. Falls back to the whole log
    /// rather than to nothing: a report with too much in it can still be read.
    /// </summary>
    public static List<string> LastBuild(IEnumerable<string> log)
    {
        var all = log.ToList();
        var from = all.FindLastIndex(
            l => l.Contains("build: folder:", StringComparison.OrdinalIgnoreCase) ||
                 l.Contains("build: registry:", StringComparison.OrdinalIgnoreCase));
        return from < 0 ? all : all[from..];
    }
}

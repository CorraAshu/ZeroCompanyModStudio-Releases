using System.Text.Json;

namespace UEToolkit.Core;

/// <summary>
/// Is there a newer release than the one running?
///
/// Written the day after the first public release, for a reason that is about support rather than
/// features: a tool people download once and keep is a tool whose bug reports arrive about builds
/// that were fixed weeks ago. The author cannot answer those without first working out which build
/// the person is on, and the person has no way of knowing there is another one -- the download
/// page is somewhere they visited once.
///
/// What it does NOT do is as deliberate as what it does. It does not download anything, it does
/// not install anything, and it sends nothing about the person or their game: one GET to a public
/// API that answers the same for everybody. Finding the update is the tool's job; deciding to take
/// it is not.
///
/// Every failure is silence. No network, a rate limit, GitHub down, a repository that has no
/// releases yet -- all of them mean "nothing to say", because a tool that opens with a complaint
/// about its own update check has made the user's problem out of its own.
/// </summary>
public static class UpdateCheck
{
    /// <summary>A release newer than the one running.</summary>
    /// <summary>
    /// A release, and WHAT CHANGED IN IT.
    ///
    /// The notes are the whole reason anybody acts on this. "A newer version is available" is a
    /// fact about a number; four releases went out in one day and a user was still on 1.0,
    /// hand-editing a settings file to work around something fixed in 1.1 -- because nothing on
    /// screen said that was what he would get. The API returns the notes in the same response the
    /// version came from, so this costs nothing.
    /// </summary>
    public sealed record Release(string Version, string Url, string? Published)
    {
        /// <summary>The release notes as written, in GitHub's markdown. Empty when there are none.</summary>
        public string Notes { get; init; } = "";
    }

    /// <summary>
    /// The owner and repository of a GitHub URL, or null.
    ///
    /// DERIVED from <see cref="Branding.Home"/> rather than configured beside it. The alternative
    /// is two settings that mean the same thing and can disagree, and the failure when they do is
    /// silent -- the tool cheerfully checks a repository nobody is releasing to. A Home that is
    /// not a GitHub repository simply has no update check, which is the correct behaviour for the
    /// toolkit, whose Home is blank.
    /// </summary>
    public static (string Owner, string Repo)? RepoOf(string home)
    {
        if (string.IsNullOrWhiteSpace(home)) return null;
        if (!Uri.TryCreate(home, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Ask GitHub for the latest release of the repository <paramref name="home"/> names, and
    /// return it only when it is NEWER than <paramref name="running"/>.
    ///
    /// Returns null for every other outcome, including "up to date" -- the caller has nothing to
    /// draw in that case, and giving it a result to inspect is how a notice ends up on screen
    /// saying the user is running the version they are running.
    /// </summary>
    public static async Task<Release?> NewerThanAsync(string home, string running,
                                                      CancellationToken cancel = default)
    {
        if (RepoOf(home) is not { } repo) return null;

        // A running version this cannot parse is one it cannot reason about, so there is no
        // comparison to make and nothing honest to put on screen.
        if (!IsVersion(running)) return null;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            // GitHub refuses an API request with no User-Agent, with a 403 that reads like a
            // permission problem. Naming the tool also means the author can see in the API logs
            // that it is this and not a scraper.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"{Branding.Product.Replace(" ", "")}/{(running.Length > 0 ? running : "0")}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var url = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/releases/latest";
            using var response = await http.GetAsync(url, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // A draft is not released and a prerelease is not what someone downloading the tool
            // should be sent to. Both fields are absent on some responses, so absence reads as
            // "no", not as a reason to give up.
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

            if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } tagName) return null;
            var latest = tagName.TrimStart('v', 'V');
            if (Compare(latest, running) <= 0) return null;

            var page = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
            var when = root.TryGetProperty("published_at", out var pub) ? pub.GetString() : null;
            var notes = root.TryGetProperty("body", out var text) ? text.GetString() ?? "" : "";
            return new Release(latest, page ?? home,
                               when is { Length: >= 10 } ? when[..10] : null) { Notes = notes };
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Release notes as something to read in a window that cannot render markdown.
    ///
    /// Not a markdown parser and not trying to be. It takes the four things GitHub's own notes
    /// actually use -- headings, bold, links, rules -- and leaves everything else exactly as
    /// written, because notes are prose and prose survives being shown as prose. A link becomes
    /// its TEXT and loses its address, which is right here: nothing in this window is clickable,
    /// and a bare URL in the middle of a sentence is worse than no URL.
    /// </summary>
    public static string Readable(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = new List<string>();

        // WHAT THE LINE BEFORE WAS, so a paragraph the author hard-wrapped at 80 columns reads as one
        // paragraph. GitHub (and every markdown renderer) joins consecutive lines of prose into one;
        // this window draws each line break it is given, so a wrapped paragraph showed as a stack of
        // short ragged lines with an orphan word on the end of each. A blank line, a heading, a table
        // row, a code fence or a list item starts something new; a line that is none of those continues
        // the line above it, a list item included (that is how a wrapped bullet is written).
        var prev = LineKind.Blank;
        var prevHardBreak = false;
        var fenced = false;

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            // Two trailing spaces are markdown's "break here"; honour it.
            var hardBreak = raw.Length - line.Length >= 2;
            var lead = line.TrimStart();

            // A code fence: its own lines are verbatim (never joined), and the marker line reads as a
            // blank, which is what stripping the backticks from it has always done.
            if (lead.StartsWith("```", StringComparison.Ordinal) || lead.StartsWith("~~~", StringComparison.Ordinal))
            { fenced = !fenced; lines.Add(""); prev = LineKind.Blank; prevHardBreak = false; continue; }

            // A horizontal rule is a separator that reads as a row of dashes; a blank line says
            // the same thing in a window this size.
            if (!fenced && line.StartsWith("---", StringComparison.Ordinal) && line.Trim('-').Length == 0)
            { lines.Add(""); prev = LineKind.Blank; prevHardBreak = false; continue; }

            // A TABLE'S RULE ROW is |---|---|---| and means nothing without the table drawn. The
            // data rows read acceptably as pipe-separated text and are kept; this drops only the
            // row that exists to tell a renderer where the header ends.
            if (!fenced && line.StartsWith("|", StringComparison.Ordinal) &&
                line.All(c => c is '|' or '-' or ':' or ' ')) continue;

            var kind = fenced ? LineKind.Verbatim
                     : lead.Length == 0 ? LineKind.Blank
                     : lead.StartsWith("#", StringComparison.Ordinal) ? LineKind.Heading
                     : lead.StartsWith("|", StringComparison.Ordinal) ? LineKind.Table
                     // A block quote's lines keep their own lines: joined, a three-line callout read as
                     // one run-on line with stray '>' marks in it (measured on the 1.5 notes).
                     : lead.StartsWith(">", StringComparison.Ordinal) ? LineKind.Quote
                     : IsListItem(lead) ? LineKind.Item
                     : LineKind.Prose;

            line = line.TrimStart('#').TrimStart();
            line = Links(line);
            line = line.Replace("**", "").Replace("`", "");
            line = Emphasis(line);
            line = Ascii(line);

            if (kind == LineKind.Prose && (prev is LineKind.Prose or LineKind.Item) && !prevHardBreak &&
                lines.Count > 0 && lines[^1].Length > 0)
            {
                // Emphasis again over the joined line: a *phrase* wrapped across two lines has one
                // star on each, which neither line alone could pair.
                lines[^1] = Emphasis(lines[^1] + " " + line.TrimStart());
                // Still the same item or paragraph, so the NEXT continuation joins it too.
                prevHardBreak = hardBreak;
                continue;
            }

            lines.Add(line);
            prev = kind;
            prevHardBreak = hardBreak;
        }

        // Never more than one blank line in a row: stripping rules and headings leaves gaps that
        // would otherwise push the actual words off the bottom of the window.
        var text = new System.Text.StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0) { if (!blank) text.Append('\n'); blank = true; continue; }
            blank = false;
            text.Append(line).Append('\n');
        }
        return text.ToString().Trim();
    }

    private enum LineKind { Blank, Heading, Table, Item, Quote, Prose, Verbatim }

    /// <summary>A bullet ("- ", "* ", "+ ") or a numbered item ("1. ", "2) ").</summary>
    private static bool IsListItem(string trimmed)
    {
        if (trimmed.Length < 2) return false;
        if (trimmed[0] is '-' or '*' or '+') return trimmed[1] is ' ' or '\t';
        var i = 0;
        while (i < trimmed.Length && char.IsAsciiDigit(trimmed[i])) i++;
        return i > 0 && i + 1 < trimmed.Length && (trimmed[i] is '.' or ')') && (trimmed[i + 1] is ' ' or '\t');
    }

    /// <summary>
    /// A single asterisk around a word is italics, and with no renderer it is just an asterisk.
    ///
    /// Bold was already stripped and this was not, so the 1.4 notes came through the window
    /// reading "what a class *declares*" -- which looks like a typo in the release notes rather
    /// than emphasis, exactly the failure the arrow glyph caused. Found by printing the REAL
    /// notes through this rather than a sample, which is the only way either of them showed up.
    ///
    /// PAIRS ONLY. A lone asterisk in prose is a footnote mark or a wildcard and means something
    /// where it stands; removing one would change a sentence rather than tidy it. So the openers
    /// and closers are counted first and nothing happens unless there is an even number of them.
    /// </summary>
    private static string Emphasis(string line)
    {
        var marks = line.Count(c => c == '*');
        return marks >= 2 && marks % 2 == 0 ? line.Replace("*", "") : line;
    }

    /// <summary>
    /// Typographic punctuation reduced to ASCII, because the window cannot draw the rest.
    ///
    /// ImGui's default font carries no glyph for an arrow, and a missing glyph is not an error --
    /// it is a gap, so "Handbook -> Your First Mod" renders as "Handbook  Your First Mod" and
    /// reads as a typo in somebody else's release notes. These are the characters release notes
    /// actually contain; anything else is left alone rather than guessed at.
    /// </summary>
    private static string Ascii(string line)
    {
        var made = new System.Text.StringBuilder(line.Length);
        var skipSpace = false;
        foreach (var c in line)
            switch (c)
            {
                case '\u2192': made.Append("->"); break;
                case '\u2014': case '\u2013': made.Append("--"); break;
                case '\u2212': made.Append('-'); break;    // a typographic MINUS, not a hyphen
                case '\u2018': case '\u2019': made.Append('\''); break;
                case '\u201c': case '\u201d': made.Append('"'); break;
                case '\u2026': made.Append("..."); break;
                case '\u2022': made.Append('-'); break;
                case '\u00a0': made.Append(' '); break;

                // ANYTHING THE FONT HAS NO GLYPH FOR, dropped rather than shown.
                //
                // The default atlas is Latin-1 and nothing wider, and a character outside it does
                // not fail -- it draws as a question mark or as nothing at all. A warning sign
                // emoji at the head of a paragraph came through the 1.4 notes as "??", which
                // reads as mojibake in somebody else's writing. The named cases above are the
                // ones worth translating; the rest of the range is dropped with the space beside
                // it, so a line loses a decoration rather than gaining a fault.
                default:
                    if (c < ' ' && c != '\t') break;
                    if (c > '\u00ff') { skipSpace = true; break; }
                    if (skipSpace && c == ' ') { skipSpace = false; break; }
                    skipSpace = false;
                    made.Append(c);
                    break;
            }
        return made.ToString();
    }

    /// <summary>A markdown link reduced to the words it shows.</summary>
    private static string Links(string line)
    {
        var made = new System.Text.StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '[') { made.Append(line[i]); continue; }
            var close = line.IndexOf(']', i);
            if (close < 0 || close + 1 >= line.Length || line[close + 1] != '(')
            { made.Append(line[i]); continue; }
            var end = line.IndexOf(')', close);
            if (end < 0) { made.Append(line[i]); continue; }
            made.Append(line, i + 1, close - i - 1);
            i = end;
        }
        return made.ToString();
    }

    /// <summary>
    /// Compare two dotted version strings numerically: negative when <paramref name="a"/> is older.
    ///
    /// Numerically rather than as text, because "1.10" is newer than "1.9" and string comparison
    /// says the opposite -- a bug that would only appear on the tenth release, months after
    /// anybody could connect it to this. A missing component is zero, so "1.0" and "1.0.0" match.
    ///
    /// Anything that is not a run of dotted numbers compares as 1, which the caller reads as "do
    /// not report an update": a version this cannot parse is one it cannot reason about, and
    /// guessing about it on screen is worse than saying nothing.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var left = Parts(a);
        var right = Parts(b);
        if (left is null || right is null) return 1;

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : 0;
            var y = i < right.Length ? right[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }

    /// <summary>Whether <see cref="Compare"/> can order this string at all.</summary>
    public static bool IsVersion(string version) => Parts(version) is not null;

    private static int[]? Parts(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var trimmed = version.Trim().TrimStart('v', 'V');

        // A build suffix -- "1.2.0+abc123", "1.2.0-rc1" -- is not part of the ordering here.
        var cut = trimmed.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) trimmed = trimmed[..cut];

        var bits = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length == 0) return null;

        var numbers = new int[bits.Length];
        for (var i = 0; i < bits.Length; i++)
            if (!int.TryParse(bits[i], out numbers[i])) return null;
        return numbers;
    }
}

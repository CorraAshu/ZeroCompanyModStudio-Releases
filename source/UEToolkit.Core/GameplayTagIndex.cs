namespace UEToolkit.Core;

using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;

/// <summary>
/// Every gameplay tag the GAME itself declares, what its own developers wrote about them, and the
/// ones it has since RENAMED.
///
/// The build warns when a mod names a tag nothing declares, because a tag that exists on no
/// machine but the author's is the failure nobody can debug from the outside -- see
/// <see cref="ModBuild"/>'s "put it in \"tags\"" warning. That check only ever knew the tags the
/// MOD declares, so it fired for every `br.*` in the game as well, including tags a mod is
/// perfectly right to reference: the E-5's own weapon type, the DC-15A's, every slot and species.
///
/// A warning that fires on correct work is worse than no warning. It cost an hour on the E-5,
/// where "'E-5' looks like a gameplay tag and this mod does not declare it" sat in the build log
/// beside the real fault and was read as noise -- correctly, because it WAS noise.
///
/// The tags live in ini files beside the game, not in an asset: `Config/DefaultGameplayTags.ini`
/// and 23 files under `Config/Tags/`. Read once per mount; the parse is a regex over about 350 KB.
/// </summary>
public static class GameplayTagIndex
{
    // `+GameplayTagList=(Tag="br.Customization.Slot.Weapon.Type",DevComment="")`. Matched on the
    // Tag= field rather than the whole line, because the same files also carry DevComment= and a
    // line-shaped pattern would take that too. `OldTagName=`/`NewTagName=` do NOT match here:
    // after their "Tag" comes "Name=", not "=".
    private static readonly Regex Declaration =
        new("Tag\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    // The same entry with the note beside it. Quoted with BACKSLASH ESCAPES -- eight of the game's
    // comments contain \" and one contains \\\" -- so the value is matched as "an escape, or a
    // character that is neither quote nor backslash", rather than as "not a quote", which would
    // end the comment at the first inner quote and drop the rest of the sentence.
    private static readonly Regex Described =
        new("Tag\\s*=\\s*\"([^\"]+)\"\\s*,\\s*DevComment\\s*=\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Compiled);

    // `GameplayTagRedirects=(OldTagName="...",NewTagName="...")` -- a tag the game has RENAMED and
    // still answers to. See the note beside where these are folded in.
    private static readonly Regex Redirect =
        new("OldTagName\\s*=\\s*\"([^\"]+)\"\\s*,\\s*NewTagName\\s*=\\s*\"([^\"]+)\"",
            RegexOptions.Compiled);

    /// <summary>
    /// What the mounted game's ini files say about tags.
    ///
    /// <paramref name="Names"/> is the question the build actually asks -- "will this tag resolve
    /// on somebody else's machine" -- so it holds more than the declarations: every PARENT of a
    /// declared tag, and every OLD NAME the game redirects.
    /// </summary>
    /// <param name="OnlyByRedirect">
    /// The old names that are in <paramref name="Names"/> for no other reason -- nothing declares
    /// them and they are nobody's parent. Exactly the set the build used to warn about wrongly,
    /// which is worth being able to count rather than infer.
    /// </param>
    public sealed record Catalogue(
        HashSet<string> Names,
        IReadOnlyDictionary<string, string> Comments,
        IReadOnlyDictionary<string, string> Renamed,
        IReadOnlySet<string> OnlyByRedirect);

    // ONE PARSE PER MOUNT. Declared() is called once per build and could afford to re-read; the
    // tooltip that shows a slot's dev comment is drawn every frame and cannot. Keyed on the
    // provider by reference, because a different provider is a different game or a re-mount, and
    // either way the answer has to be read again.
    private static readonly object Gate = new();
    private static IFileProvider? _forProvider;
    private static Catalogue? _cached;

    /// <summary>
    /// Every tag declared by the mounted game, or an empty set when the files cannot be read.
    ///
    /// Empty rather than throwing, and the caller must treat empty as "cannot say" rather than as
    /// "nothing is declared" -- warning on every tag because a file would not open is the same
    /// false alarm in a different costume.
    /// </summary>
    public static HashSet<string> Declared(IFileProvider provider) => Of(provider).Names;

    /// <summary>Everything the ini files say, parsed once per mounted provider.</summary>
    public static Catalogue Of(IFileProvider provider)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_forProvider, provider) && _cached is { } had) return had;
            var made = Read(provider);
            _forProvider = provider;
            _cached = made;
            return made;
        }
    }

    /// <summary>
    /// The note the game's own developers left on a tag, or null.
    ///
    /// Only about 350 of 7,400 tags carry one, and the ones that do are worth showing: "Slots that
    /// have this tag will be able to accept parts that have this tag", "Species that have this tag
    /// use the human head slots and heads", and "DEPRECATED." on twenty-one roster facts that
    /// otherwise look perfectly live.
    /// </summary>
    public static string? Comment(IFileProvider? provider, string tag) =>
        provider is not null && Of(provider).Comments.TryGetValue(tag, out var note) ? note : null;

    private static Catalogue Read(IFileProvider provider)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (path, file) in provider.Files)
            {
                if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
                // The two places UE keeps them: the project's own list, and the per-feature files
                // a plugin contributes. Matched by path so a game that adds another is covered.
                if (!path.Contains("/Config/Tags/", StringComparison.OrdinalIgnoreCase) &&
                    !path.EndsWith("GameplayTags.ini", StringComparison.OrdinalIgnoreCase)) continue;

                var text = System.Text.Encoding.UTF8.GetString(file.Read());
                foreach (Match m in Declaration.Matches(text))
                    found.Add(m.Groups[1].Value);

                foreach (Match m in Described.Matches(text))
                    if (Unescape(m.Groups[2].Value) is { Length: > 0 } note)
                        comments[m.Groups[1].Value] = note;

                foreach (Match m in Redirect.Matches(text))
                    renamed[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }
        catch { /* an unreadable ini leaves the set short, which the caller reads as "cannot say" */ }

        // A RENAMED TAG STILL RESOLVES, and this did not know that.
        //
        // The same ini files carry GameplayTagRedirects, which is how the game keeps old data
        // working after it renames a tag. There are 68 of them and 67 of the old names appear
        // nowhere else, so a mod naming one built a warning saying the game does not declare it.
        // The game answers to it perfectly well -- that is the entire purpose of a redirect -- and
        // the E-5 lesson is that a warning which fires on correct work gets the real ones read as
        // noise alongside it.
        //
        // EVERY PARENT EXISTS TOO. Unreal registers "a.b" and "a" the moment "a.b.c" is declared,
        // and the game's own parts lean on that: the weapon slot,
        // br.Customization.Slot.Character.Specializations.Weapon, is declared nowhere by itself and
        // sits in the AllowedSlots of every weapon spec. Without this, the build called a rename
        // to that very tag "a tag neither this mod nor the game declares" (2026-09-07).
        foreach (var tag in found.ToList())
        {
            var at = tag.LastIndexOf('.');
            while (at > 0)
            {
                found.Add(tag[..at]);
                at = tag.LastIndexOf('.', at - 1);
            }
        }

        // A RENAMED TAG STILL RESOLVES, and this did not know that.
        //
        // The same ini files carry GameplayTagRedirects, which is how the game keeps old data
        // working after it renames a tag. There are 267 of them and most of the old names appear
        // nowhere else, so a mod naming one built a warning saying the game does not declare it.
        // The game answers to it perfectly well -- that is the entire purpose of a redirect -- and
        // the E-5 lesson is that a warning which fires on correct work gets the real ones read as
        // noise alongside it.
        //
        // AFTER the parent pass, and given none of its own. A redirect covers the exact name it
        // names and nothing beneath it: the game accepts `a.b.c` because a redirect says so, and
        // has no opinion whatever about `a.b`. Expanding these would put tags in the set that
        // resolve on nobody's machine, which is the failure this whole class of check exists to
        // catch.
        //
        // MEASURED against the shipped tag files, 2026-09-09.
        var onlyByRedirect = new HashSet<string>(StringComparer.Ordinal);
        foreach (var old in renamed.Keys)
            if (found.Add(old)) onlyByRedirect.Add(old);

        return new Catalogue(found, comments, renamed, onlyByRedirect);
    }

    /// <summary>
    /// An ini string value as it reads. `\"` and `\'` are the two the game's comments use, and
    /// `\\` has to come out as one backslash or "For \\\"hold that thought\\\" lines" keeps a
    /// stray slash in the tooltip.
    /// </summary>
    private static string Unescape(string raw)
    {
        if (!raw.Contains('\\')) return raw.Trim();
        var made = new System.Text.StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length) i++;
            made.Append(raw[i]);
        }
        return made.ToString().Trim();
    }
}

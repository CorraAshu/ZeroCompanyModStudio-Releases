using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// The words an ABILITY copy shows a player, and where the project keeps them.
///
/// A part names itself in a top-level DisplayName and describes itself in ShortDescription; an
/// ability does neither. It keeps a Name, a Description and a LongDescription one struct down, in
/// UIData, and the copy's own `text` map is where the project records them -- the same three keys
/// Force Grip's were written under by hand before these boxes existed. The build's text step
/// finds each one, and ADDS it where the template's UIData never carried it (Resonate has no
/// Description at all).
///
/// The part-shaped fields are left alone for an ability, and that is not only tidiness: the build
/// writes Display to a DisplayName, which an ability does not have, and skips the whole copy when
/// it cannot -- so a name typed into the old box would have dropped Force Grip from the mod.
///
/// One place, because the editor, the tab title and the mod list all have to agree on what an
/// ability copy is and what it is called.
/// </summary>
internal static class AbilityText
{
    public const string Name = "Name";
    public const string Description = "Description";
    public const string LongDescription = "LongDescription";

    /// <summary>A copy of a gameplay ABILITY -- `GA_`, the prefix the Studio already sorts by.</summary>
    public static bool IsAbility(ModCopy copy)
        => copy.Template[(copy.Template.LastIndexOf('/') + 1)..].StartsWith("GA_", StringComparison.Ordinal);

    /// <summary>What the project holds for one of the three, or "" when it holds nothing.</summary>
    public static string Get(ModCopy copy, string field)
        => copy.Text.TryGetValue(field, out var value) ? value : "";

    /// <summary>
    /// Record what the box says. An EMPTY box gives the text back to the game: the entry is
    /// REMOVED rather than written blank, because the build writes a blank as an empty text --
    /// which would wipe the template's words, and the author asked for the opposite.
    /// </summary>
    public static void Set(ModCopy copy, string field, string typed, string label, List<string> wrote)
    {
        var want = typed.Trim();
        var had = copy.Text.TryGetValue(field, out var was) ? was : null;
        if (want.Length == 0)
        {
            if (had is null) return;
            copy.Text.Remove(field);
            wrote.Add($"{label} back to the game's");
            return;
        }
        if (want == had) return;
        copy.Text[field] = want;
        wrote.Add(label);
    }

    /// <summary>The name to show for a copy in a list or on a tab: its own, when it has one.</summary>
    public static string Shown(ModCopy copy)
        => copy.Display.Length > 0 ? copy.Display
         : IsAbility(copy) && Get(copy, Name) is { Length: > 0 } named ? named
         : copy.Id;
}

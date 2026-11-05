using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE TINT A PART PAINTS: which mask, and which colour through it.
///
/// This is what makes one part a COLOURWAY of another, and neither field had an editor here —
/// <c>ModPart.TintColor</c> had no reference anywhere in the Avalonia project at all.
///
/// THE GAME'S OWN SETS ARE BUILT EXACTLY THIS WAY. The eleven Clo000 helmets are eleven PARTS
/// sharing one mesh, each carrying its own mask and its own colour, and nothing links them to a
/// base. So a mod makes a set the same way: duplicate the part and change these two. "Duplicate
/// as another colour" is that, in one press.
///
/// A COPY'S ONLY. Both fields live on <see cref="ModPart"/>; an override rewrites the game's own
/// part in place and has nowhere to put a tint of the author's.
///
/// WHICH CHANNEL FEEDS WHICH COLOUR IS NOT KNOWABLE FROM HERE. The game keeps three regions in
/// one mask — R, G and B — and the wiring lives in the material graph, which a cooked instance
/// does not expose. The viewport paints the channels and labels them as channels for that
/// reason; this panel names the mask and the colour, which are the two things the part actually
/// carries.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// Put the part's tint on the panel.
    ///
    /// THE TEMPLATE'S MASK IS READ TOO, so the row can NAME what it falls back to. "the
    /// template's" tells somebody nothing; the name is what they need, because a mask is drawn
    /// for one kit's UVs and a part built from another kit's template paints the wrong panels —
    /// which is what happened to the commando torso and is invisible until the game is launched.
    /// </summary>
    private void FillTint(PartTab tab, ModPart copy, PartDetails.Detail detail)
    {
        tab.SecTint.Present = true;

        tab.TemplateTintMask = detail.TintMasks?.FirstOrDefault() ?? "";
        tab.TakesTintColour = detail.HasMaterialColor;

        tab.EditTintMask = copy.TintMask;
        tab.EditTintColour = copy.TintColor;

        var slots = detail.TintSlots?.Count ?? 0;
        tab.TintNote =
            tab.TemplateTintMask.Length == 0 && copy.TintMask.Length == 0
                ? "This part's template paints no tint, so a colour here has nothing to paint " +
                  "through. Naming a mask gives it one."
            : detail.HasMaterialColor
                ? $"The mask decides WHERE the colour lands; the colour is what lands there." +
                  (slots > 0 ? $" Its template aims at {slots} tint slot(s)." : "")
            : "Its template carries no colour parameter, so there is nothing here for a colour " +
              "to be written into — the mask is still yours to change.";
    }

    /// <summary>
    /// Name the mask this part paints through.
    ///
    /// WRITTEN AS IT HAPPENS, like an import: the picker's answer is the edit, and there is
    /// nothing a Save could add to it. Empty puts the template's back.
    /// </summary>
    public void SetTintMask(PartTab tab, string package)
    {
        if (_mod is null || tab.Copy is not { } copy) return;

        package = package.Trim();
        if (package.Equals(copy.TintMask, StringComparison.Ordinal)) return;

        copy.TintMask = package;
        _mod.Save();
        tab.EditTintMask = package;
        _artVersion++;

        Say(package.Length > 0
            ? $"{copy.Id}: paints its tint through {Leaf(package)}"
            : $"{copy.Id}: back to its template's mask" +
              (tab.TemplateTintMask.Length > 0 ? $" ({Leaf(tab.TemplateTintMask)})" : ""));
        AfterEditsChanged();
    }

    /// <summary>
    /// The colour this part paints.
    ///
    /// SIX HEX DIGITS, and anything else is filtered out rather than refused — somebody pasting
    /// "#8A4B2C" means the six digits in it, and a box that rejects the paste is a box that
    /// makes them retype what they already had.
    /// </summary>
    public void SetTintColour(PartTab tab, string hex)
    {
        if (_mod is null || tab.Copy is not { } copy) return;

        var clean = new string((hex ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (clean.Length > 6) clean = clean[..6];

        if (clean.Length is > 0 and < 6)
        { Say($"{copy.Id}: a colour is six hex digits — '{clean}' is {clean.Length}"); return; }

        if (clean.Equals(copy.TintColor, StringComparison.Ordinal)) return;

        copy.TintColor = clean;
        _mod.Save();
        tab.EditTintColour = clean;
        _artVersion++;

        Say(clean.Length > 0
            ? $"{copy.Id}: paints #{clean}" +
              (NameOfColour(clean) is { } named ? $" ({named})" : "")
            : $"{copy.Id}: back to its template's colour");
        AfterEditsChanged();
    }

    /// <summary>The game's own name for a colour, where it has one. For the log, not the field.</summary>
    private string? NameOfColour(string hex) => Swatches
        .FirstOrDefault(s => s.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>
    /// A SECOND PART, same mesh and same artwork, for another colour.
    ///
    /// ROUND-TRIPPED THROUGH THE PROJECT'S OWN JSON rather than copied field by field. A
    /// hand-written copy silently drops whatever field is added next, and a part carries meshes,
    /// materials, textures, tags, takeovers and regions — the old studio learnt this and said so.
    ///
    /// A LETTER, NEVER A NUMBER. Unreal splits a trailing _N off a name into an FName number and
    /// the part does not load; the project has a memory of that costing a day.
    /// </summary>
    public void DuplicateAsColour(PartTab tab)
    {
        if (_mod is null) { Say("open a mod first"); return; }
        if (tab.Copy is not { } copy)
        { Say("only a copy can be duplicated as another colour"); return; }

        var made = ModPart.From(copy.ToJson());

        var n = 1;
        string Suffix(int i) => "_" + (char)('a' + (i % 26)) + (i >= 26 ? (i / 26).ToString() : "");
        while (_mod.Parts.Any(x => x.Id.Equals(copy.Id + Suffix(n), StringComparison.OrdinalIgnoreCase)))
            n++;

        made.Id = copy.Id + Suffix(n);
        made.Display = $"{(copy.Display.Length > 0 ? copy.Display : copy.Id)} {(char)('A' + (n % 26))}";

        _mod.Parts.Add(made);
        _mod.Save();

        Say($"{made.Id} added — same mesh, same artwork. Give it a colour of its own.");
        AfterEditsChanged();

        _ = EditCopy(made);
    }
}

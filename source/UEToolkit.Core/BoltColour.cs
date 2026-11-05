namespace UEToolkit.Core;

using CUE4Parse.FileProvider;

/// <summary>
/// What colour a weapon fires, which is ONE BYTE on its actor.
///
/// `Blaster VFX Color` on the weapon actor's class default object, of `ENUM_Blaster_Color`.
/// CONFIRMED IN GAME 2026-09-06: the same weapon, with the same meshes, the same `Projectile`
/// class and the same `MuzzleFlash_VFX`, fired blue through an actor that sets 1 and red through
/// one that sets nothing -- so this property is what decides it, and nothing else on that object.
///
/// TWO MECHANISMS, AND THE DIFFERENCE IS NOT THE AUTHOR'S PROBLEM. A cooked package stores only
/// what was set, so a clone weapon's actor CARRIES the property (Blue is a deliberate 1) and a
/// Separatist one does not (Red is the class default, serialised as nothing at all). Changing the
/// first is `numbers`; the second has to be ADDED, which is a different edit with a different
/// failure mode. <see cref="Setting"/> decides which, so the control above it is one dropdown.
/// </summary>
public static class BoltColour
{
    /// <summary>The enum that names the colours. Read, never assumed -- see <see cref="Choices"/>.</summary>
    public const string EnumPackage = "/Game/Game/FX/Data/ENUM_Blaster_Color";

    /// <summary>The property, and the export it sits on. The export is the actor's CDO.</summary>
    public const string Property = "Blaster VFX Color";

    /// <param name="Index">The byte actually stored.</param>
    /// <param name="Label">What the game's own designers call it -- "Red", not "NewEnumerator0".</param>
    public sealed record Choice(int Index, string Label);

    /// <summary>
    /// The colours this game defines, in order, with the names its designers gave them.
    ///
    /// READ FROM THE ENUM, not written down here. A UserDefinedEnum keeps its display names in a
    /// `DisplayNameMap` beside the enumerators -- "Blaster Color Red" against `NewEnumerator0` --
    /// and those are the only names that mean anything to a person. Hard-coding four would also
    /// mean a fifth added by a patch is quietly unavailable rather than simply appearing, which is
    /// the rule DrawRarity already follows for weapon rarities.
    ///
    /// `*_MAX` is dropped: UnrealHeaderTool appends it to every enum and it is not a colour.
    /// </summary>
    public static List<Choice> Choices(IFileProvider provider)
    {
        var found = new List<Choice>();
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(ModBuild.ContainerPathFor(EnumPackage)).GetExports().ToList());

            foreach (var e in exports)
            {
                if (PartDetails.Value(e.Properties, "DisplayNameMap") is not
                    CUE4Parse.UE4.Assets.Objects.UScriptMap map) continue;

                var at = 0;
                foreach (var kv in map.Properties)
                {
                    var key = kv.Key?.GenericValue?.ToString() ?? "";
                    var label = kv.Value?.GenericValue?.ToString() ?? key;
                    at++;
                    if (key.EndsWith("_MAX", StringComparison.Ordinal)) continue;
                    // "Blaster Color Red" -> "Red". The prefix is the enum saying which enum it is,
                    // which the label beside a control does not need to repeat.
                    var word = label.StartsWith("Blaster Color ", StringComparison.OrdinalIgnoreCase)
                        ? label["Blaster Color ".Length..] : label;
                    found.Add(new Choice(at - 1, word));
                }
                if (found.Count > 0) break;
            }
        }
        catch { /* an enum that will not read leaves the control saying so, not throwing */ }
        return found;
    }

    /// <summary>
    /// Whether <paramref name="actorPackage"/> already serialises the property, and what it reads.
    ///
    /// Null means the property is absent -- the weapon takes the class default, which is enumerator
    /// zero. That is not a failure and is the commoner case: every Separatist blaster in the game
    /// is red by saying nothing.
    /// </summary>
    public static int? Current(IFileProvider provider, string actorPackage, string export)
    {
        try
        {
            var src = AssetCopy.PristineRead(provider, actorPackage, out _);
            var raw = src?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
            if (raw is null) return null;
            if (!PropertyAdd.Locate(provider, raw, actorPackage, export, Property,
                                    out var at, out var length, out _)) return null;
            return length == 1 && at >= 0 && at < raw.Length ? raw[at] : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Record the wanted colour on <paramref name="copy"/>, through whichever edit can reach it.
    ///
    /// A property that is THERE is changed by value and needs the old one, because ScalarEdit
    /// refuses to write over a byte that is not what the author was looking at. A property that is
    /// ABSENT is added. Both are cleared first so switching colour twice leaves one instruction
    /// rather than a pile, and so choosing the default again leaves the copy as it was.
    /// </summary>
    public static void Setting(ModCopy copy, string export, int? present, int want)
    {
        copy.Numbers.RemoveAll(n => n.Row == export && n.Field == Property);
        copy.AddScalars.RemoveAll(a => a.Export == export && a.Name == Property);

        if (present is { } was)
        {
            // Already what it says: nothing to record, and an edit that changes nothing is an
            // instruction that can only fail later.
            if (was == want) return;
            copy.Numbers.Add(new ModOverride.NumberChange(export, Property, was, want));
            return;
        }

        // Absent AND the default wanted: the absence already says it. Adding a zero would be an
        // edit whose whole content is "leave it alone".
        if (want == 0) return;
        copy.AddScalars.Add(new ModOverride.ScalarAdd(export, Property, want));
    }
}

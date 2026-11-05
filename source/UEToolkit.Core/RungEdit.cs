using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// The levels and focus costs on a specialization's focus tree -- the last part of a spec the
/// editor could show and not change.
///
/// WHY NOT A SCHEMA WALK. <see cref="DataTableEdit"/> finds a number by walking the asset the way
/// the engine reads it, and that is the right tool for a DataTable, whose rows are a flat list.
/// A focus tree is three levels down: an array of lines, each line a struct holding an array of
/// rungs, each rung a struct holding three ints. Walking that means resolving a nested struct's
/// schema from its parent's property info at every level, and being wrong anywhere in it puts four
/// bytes into the middle of something else.
///
/// So this locates by CONTENT instead, the way <see cref="FlagEdit"/> does. Every rung names an
/// ability, that name is an FName reference in the export data, and the rung's numbers follow it
/// in schema order. The anchor is the reference; the PROOF is that the bytes after it read the
/// three values the reader already knows this rung has. An offset that has drifted lands on
/// numbers that do not match and is refused, rather than written.
///
/// WHAT IT CANNOT DO, and refuses clearly: give a value to a number the game ships as ZERO. An
/// unversioned property whose value is zero occupies no bytes at all -- it is named in the header
/// and absent from the data -- so there is nothing to overwrite, and adding it means rebuilding
/// that rung's header. In practice that is the first rung's focus cost, which every line ships at
/// 0. Changing a number to zero is fine; it is only going the other way that has nowhere to write.
/// </summary>
public static class RungEdit
{
    /// <summary>The three numbers on a rung, in the order they serialise.</summary>
    public static readonly string[] Fields = ["MinLevel", "FocusLevel", "FocusCost"];

    /// <summary>Where one rung's numbers live, and what they currently read.</summary>
    /// <param name="At">Byte offset of each PRESENT number, in <see cref="Fields"/> order; -1 for
    /// one the file omits because it is zero.</param>
    public sealed record Found(int Line, int Rung, int[] At, int[] Was);

    /// <summary>
    /// Locate a rung's numbers.
    ///
    /// <paramref name="ability"/> is the rung's ability object path, which is what makes this rung
    /// findable at all; the three values are what make the find checkable.
    /// </summary>
    public static Found? Locate(byte[] raw, string ability, int line, int rung,
                                int minLevel, int focusLevel, int focusCost, out string? why)
    {
        why = null;
        var want = new[] { minLevel, focusLevel, focusCost };

        // The asset name, which is the more distinctive half of a soft object path -- the package
        // path is shared by every tier of one ability, the `_C` class name is not.
        var pkgPath = ModBuild.PackageOf(ability) ?? ability;
        var leaf = pkgPath[(pkgPath.LastIndexOf('/') + 1)..];
        if (leaf.Length == 0) { why = $"'{ability}' has no asset name to anchor on"; return null; }

        var pkg = ZenPackage.Read(raw);
        var nameIndex = pkg.Names.IndexOf(leaf + "_C");
        if (nameIndex < 0) nameIndex = pkg.Names.IndexOf(leaf);
        if (nameIndex < 0)
        { why = $"'{leaf}' is not in this package's name map"; return null; }

        // An FName in export data: the mapped index, then the number, which is zero for anything
        // that is not "Foo_3".
        var anchor = new byte[8];
        BitConverter.GetBytes((uint) nameIndex).CopyTo(anchor, 0);
        BitConverter.GetBytes(0u).CopyTo(anchor, 4);

        var headerSize = (int) BitConverter.ToUInt32(raw, 4);
        var anchors = new List<int>();
        for (var at = headerSize; at + anchor.Length < raw.Length; at++)
            if (raw.AsSpan(at, anchor.Length).SequenceEqual(anchor)) anchors.Add(at);

        // THE EXPECTED PLACE FIRST, and it is expected rather than searched for a reason. A soft
        // object path serialises as two FNames and a sub-path string: anchoring on the second name
        // puts the numbers at eight bytes for the rest of that name plus four for an empty string.
        //
        // Trying that one offset before searching is what makes a sparse rung addressable at all.
        // A rung of 0/1/0 contributes ONE int to the file, and "an int reading 1 somewhere in the
        // next thirty-two bytes" matches more than one place -- so the scan refused every rung
        // whose levels start at zero, which is the first rung of every line in the game.
        var hits = new List<Found>();
        foreach (var at in anchors)
            if (Reads(raw, at + anchor.Length + ExactGap, want, out var exact))
                hits.Add(new Found(line, rung, exact, want));

        // Only when the layout was not what it should be. Kept because the fallback costs nothing
        // and the alternative to a wider search is refusing outright on a package shaped in a way
        // nobody here has seen yet.
        if (hits.Count == 0)
            foreach (var at in anchors)
                for (var gap = 0; gap <= MaxGap; gap++)
                    if (Reads(raw, at + anchor.Length + gap, want, out var offsets))
                        hits.Add(new Found(line, rung, offsets, want));

        if (hits.Count == 0)
        {
            why = $"found '{leaf}' but no {want.Count(v => v != 0)} number(s) reading " +
                  $"{minLevel}/{focusLevel}/{focusCost} after it";
            return null;
        }
        if (hits.Count > 1)
        {
            // Two places that both look right is not a place to guess. It happens when one line
            // repeats an ability with identical numbers, which is a real thing an author can make.
            why = $"'{leaf}' with {minLevel}/{focusLevel}/{focusCost} matches {hits.Count} places " +
                  "in the file; refusing to pick one";
            return null;
        }
        return hits[0];
    }

    /// <summary>
    /// How far past the ability reference the numbers may start.
    ///
    /// Two FNames and an empty sub-path string is twenty bytes, and the bound is generous rather
    /// than exact because the point of the search is not to predict the layout -- the value check
    /// is what decides a match.
    /// </summary>
    private const int MaxGap = 32;

    /// <summary>
    /// Where the numbers sit when the layout is the usual one: past the rest of the asset FName,
    /// then past an empty sub-path string.
    /// </summary>
    private const int ExactGap = 4;

    /// <summary>
    /// Do the int32s at <paramref name="start"/> read the non-zero wanted values, in order?
    ///
    /// The zero ones are expected to be ABSENT, which is what makes this a real test rather than a
    /// loose one: a rung of 0/1/0 contributes a single int, and finding a single int reading 1 in
    /// the right place is a much narrower claim than finding three ints somewhere.
    /// </summary>
    private static bool Reads(byte[] raw, int start, int[] want, out int[] offsets)
    {
        offsets = [-1, -1, -1];
        var at = start;
        for (var i = 0; i < want.Length; i++)
        {
            if (want[i] == 0) continue;            // omitted from the file entirely
            if (at + 4 > raw.Length) return false;
            if (BitConverter.ToInt32(raw, at) != want[i]) return false;
            offsets[i] = at;
            at += 4;
        }
        // Every wanted value being zero leaves nothing to have matched, and "it matched nothing"
        // is not a location.
        return offsets.Any(o => o >= 0);
    }

    /// <summary>
    /// Write one rung's numbers. Four bytes for four bytes, so nothing moves and no offset found
    /// before this call goes stale.
    /// </summary>
    public static byte[] Set(byte[] raw, Found found, int[] now, out List<string> notes)
    {
        notes = [];
        var edited = (byte[]) raw.Clone();

        for (var i = 0; i < Fields.Length; i++)
        {
            if (now[i] == found.Was[i]) continue;

            if (found.At[i] < 0)
            {
                notes.Add($"{Fields[i]} stays {found.Was[i]}: the game ships it as zero, which " +
                          "takes no space in the file, so there is nothing to write over");
                continue;
            }
            BitConverter.GetBytes(now[i]).CopyTo(edited, found.At[i]);
            notes.Add($"{Fields[i]} {found.Was[i]} -> {now[i]}");
        }
        return edited;
    }
}

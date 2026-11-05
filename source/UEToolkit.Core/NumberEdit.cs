namespace UEToolkit.Core;

/// <summary>
/// Change a number inside a cooked asset, in place.
///
/// The first thing a balance mod needs, and the smallest edit this build performs: four bytes,
/// same length in as out, so nothing downstream has to be told the asset changed size.
///
/// **Located by its CURRENT VALUE, and only when that value is unique in the asset.** A weapon's
/// stats are DataTable rows, and walking to a named row's named field means re-implementing the
/// row-by-row unversioned walk that <see cref="PropertyAdd"/> does for an export's own properties.
/// That walk is the right answer and it is not this one. What this does instead is refuse: a float
/// that occurs once can be rewritten with certainty, and one that occurs eleven times cannot be
/// told from the other ten, so it is not attempted.
///
/// That refusal is not a formality. Damage 17 occurs once in the DC-17's table; ReactionDamage 1
/// occurs alongside AssistDamage 1, ReactionChanceToHit 1 and every other 1.0 in the file. So this
/// can change the interesting numbers and says plainly when it cannot -- which is the honest half
/// of a first cut, and the reason the caller must state what it expects to find rather than only
/// what it wants to write.
/// </summary>
public static class NumberEdit
{
    /// <summary>
    /// Rewrite the single float reading <paramref name="was"/> as <paramref name="now"/>.
    /// Returns the bytes unchanged, with a reason, when that float is absent or not unique.
    /// </summary>
    public static byte[] SetFloat(byte[] raw, float was, float now, out int at, out string? why)
    {
        at = -1;
        why = null;

        var pattern = BitConverter.GetBytes(was);
        var hits = new List<int>();
        for (var i = 0; i + 4 <= raw.Length; i++)
            if (raw[i] == pattern[0] && raw[i + 1] == pattern[1] &&
                raw[i + 2] == pattern[2] && raw[i + 3] == pattern[3])
                hits.Add(i);

        if (hits.Count == 0)
        {
            // Almost always means the value moved: the asset says something else now, either
            // because the game updated or because this edit already ran.
            why = $"no float reading {was} anywhere in this asset";
            return raw;
        }

        if (hits.Count > 1)
        {
            why = $"{hits.Count} floats read {was}, and nothing here can tell which one is meant";
            return raw;
        }

        // THE UPPER HALF OF A DOUBLE LOOKS EXACTLY LIKE THE FLOAT, and this search cannot tell
        // them apart on its own:
        //
        //     double 2.0  = 40 00 00 00 00 00 00 00
        //     float  2.0f =             40 00 00 00
        //
        // An attachment's stat is a double, so this matched four bytes INTO it, wrote 20.0f, and
        // made 134,217,728.0 -- reporting "2 -> 20" the whole way. Every round number in this
        // game's data has that shape, so the guard is not an edge case.
        //
        // Four zero bytes in front of a match is what that looks like from here. Refused rather
        // than guessed at, and it names the tool that CAN answer: ScalarEdit locates by name and
        // knows the width.
        if (hits[0] >= 4 && raw[hits[0] - 4] == 0 && raw[hits[0] - 3] == 0 &&
            raw[hits[0] - 2] == 0 && raw[hits[0] - 1] == 0)
        {
            why = $"the only float reading {was} sits four zero bytes in, so it is probably the " +
                  "upper half of a double and writing it would corrupt the value -- name the " +
                  "export and property instead, which locates it by name and knows the width";
            return raw;
        }

        var copy = (byte[]) raw.Clone();
        BitConverter.GetBytes(now).CopyTo(copy, hits[0]);
        at = hits[0];
        return copy;
    }
}

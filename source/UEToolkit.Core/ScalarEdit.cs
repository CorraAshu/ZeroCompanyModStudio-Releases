namespace UEToolkit.Core;

using CUE4Parse.FileProvider;

/// <summary>
/// Change a number that is already in an asset, found BY NAME rather than by its value.
///
/// This exists because searching for a value cannot know what it hit. `NumberEdit.SetFloat` scans
/// for the four bytes of a float and overwrites the first unique match, and that went wrong in the
/// worst available way: an attachment's stat, `GameplayEffectModifer`, is a DOUBLE, and
///
///     double 2.0   = 40 00 00 00 00 00 00 00
///     float  2.0f  =             40 00 00 00
///
/// -- the float's bytes ARE the double's upper half. So the search matched halfway into the
/// double, wrote float 20.0f over it, and produced 134,217,728.0, while the build reported
/// "2 -> 20" and the read-back said clean. Every round number in this game's data is exposed to
/// that, because every one of them is exactly representable both ways.
///
/// <see cref="PropertyAdd.Locate"/> walks the export's properties by name and returns the range
/// the value occupies, so the width is known rather than assumed.
///
/// THE EDIT IS SELF-CHECKING AT BOTH ENDS.
///
/// Before: the caller passes the value it believes is there, and the located bytes are read back
/// under each encoding the width allows; nothing is written unless one of them agrees. A property
/// that has moved, or that is not the number the author was looking at, is refused rather than
/// overwritten -- the failure the value search could not detect.
///
/// After: the written package is WALKED AGAIN and the property read through the parser. Checking
/// the bytes at the offset just written would only confirm that a copy landed where it was told
/// to; walking again asks whether that offset is the property that was named. "Wrote 20 at byte
/// 1319" was printed once, believed, and false. "It now reads 20" cannot be.
/// </summary>
public static class ScalarEdit
{
    /// <summary>How a located number is stored, decided by width and confirmed against `was`.</summary>
    public enum Kind
    {
        None,
        Float,
        Double,
        Int,

        /// <summary>
        /// ONE BYTE: a byte-backed enum, holding the enumerator's index.
        ///
        /// A weapon's bolt colour is `Blaster VFX Color` on the actor's class default object, of
        /// `ENUM_Blaster_Color` -- 0 Red, 1 Blue, 2 Green, 3 Yellow. CONFIRMED IN GAME
        /// 2026-09-06: the same weapon, same meshes, same projectile and same muzzle flash, fired
        /// blue through an actor that sets 1 and red through one that sets nothing.
        ///
        /// It is the narrowest width here and has to be tested FIRST, before the four- and
        /// eight-byte arms -- not because they would match a single byte, but because
        /// SingleEntryMap reads ahead of `start` and a one-byte property has nothing behind it.
        /// </summary>
        Byte,

        /// <summary>
        /// An int that is the VALUE of a single-entry TMap, not the property itself.
        ///
        /// A modification's price is a map from a resource asset to an amount, because the game
        /// can in principle charge in something other than credits. Everything it ships has one
        /// entry, so the property that Locate hands back is the whole map and the number sits
        /// inside it:
        ///
        ///     00 00 00 00   entries to remove, always zero here
        ///     01 00 00 00   how many entries
        ///     FB FF FF FF   the key -- an import index, four bytes
        ///     D0 07 00 00   the value: 2000 credits
        ///
        /// Read from a real asset rather than assumed, and every one of those four fields is
        /// CHECKED before anything is written: two entries, or a non-zero removal count, or a
        /// value that is not what the caller expected, and the edit is refused. The key is never
        /// touched, so the map still prices the same resource.
        /// </summary>
        IntInMap,
    }

    /// <summary>Bytes before the first entry of a serialised TMap: the removal count, then the count.</summary>
    private const int MapHeaderBytes = 8;

    /// <summary>
    /// Set <paramref name="property"/> on <paramref name="export"/> to <paramref name="now"/>.
    ///
    /// Same width in, same width out, so no offset moves and nothing after it has to be corrected.
    /// </summary>
    public static byte[] SetByName(IFileProvider provider, byte[] raw, string packageName,
                                   string export, string property, double was, double now,
                                   out Kind kind, out int at, out string? why)
    {
        kind = Kind.None;
        at = -1;

        if (!PropertyAdd.Locate(provider, raw, packageName, export, property,
                                out var start, out var length, out why))
            return raw;

        kind = Encoding(raw, start, length, was);
        if (kind == Kind.None)
        {
            // Says what IS there, because "it does not match" without the actual value sends
            // somebody looking for a mistake in their own number.
            why = $"{export}.{property} is {length} byte(s) at {start} and does not read {was}" +
                  (length == 8 ? $" -- it reads {BitConverter.ToDouble(raw, start)}"
                   : length == 4 ? $" -- it reads {BitConverter.ToSingle(raw, start)} as a float, " +
                                   $"{BitConverter.ToInt32(raw, start)} as an int"
                   // A one-byte miss used to print no actual value at all, which is exactly the
                   // silence the rest of this message exists to prevent: without it, somebody
                   // goes looking for a mistake in their own number.
                   : length == 1 ? $" -- it reads {raw[start]}"
                   : "");
            return raw;
        }

        var written = (byte[]) raw.Clone();
        switch (kind)
        {
            // One byte for one byte, so no offset moves and the export needs no repair.
            case Kind.Byte: written[start] = (byte) Math.Round(now); break;
            case Kind.Double: BitConverter.GetBytes(now).CopyTo(written, start); break;
            case Kind.Float: BitConverter.GetBytes((float) now).CopyTo(written, start); break;
            case Kind.Int: BitConverter.GetBytes((int) Math.Round(now)).CopyTo(written, start); break;
            case Kind.IntInMap:
                BitConverter.GetBytes((int) Math.Round(now)).CopyTo(written, MapValueAt(start, length));
                break;
        }

        // READ BACK THROUGH THE WALK, not out of the bytes just written.
        //
        // Checking the bytes at `start` would only confirm that the copy landed where the copy was
        // told to land, which was never in doubt. What was in doubt -- and what actually went
        // wrong once -- is whether that offset is the property the caller named. Walking the
        // written package again asks the parser, and the parser is what the game agrees with.
        //
        // This is the difference between "wrote 20 at byte 1319" and "it now reads 20", and the
        // first of those was printed, believed, and false: the value search had written into the
        // middle of a double and the asset read 134,217,728.
        if (!Read(provider, written, packageName, export, property, kind,
                  out var back, out var backWhy))
        { why = $"written, but could not be read back: {backWhy}"; return raw; }

        if (!Agrees(back, now))
        {
            why = $"{export}.{property} was set to {now} and reads back {back}";
            return raw;
        }

        at = start;
        why = null;
        return written;
    }

    /// <summary>
    /// Did <see cref="SetByName"/> fail because it could not REACH THE EXPORT AT ALL?
    ///
    /// TRUE for exactly four reasons, each a statement about the address and not about the
    /// property: no mounted container holds the package, no export in it matches the name, the
    /// export's class is not a loaded struct, no property schema exists for that class. In each of
    /// those nothing was looked at, so a search of the package for the old value was never
    /// contradicted by anything, and it is the only way left to find a number in a target the
    /// name cannot open (a hand-written project whose "row" is not an export's name).
    ///
    /// FALSE for everything else, and that direction is the point. Every other reason means the
    /// export WAS reached and the property was looked at: it is not a property of it, it is not
    /// carried, it is serialised as zero, an array is shorter than the path says, a map has no
    /// such key, it reads another number than the edit expects, it did not read back as written.
    /// Also false for an exception's message and for any reason somebody adds later without
    /// thinking about this method. After an answer like those, a search by value can only find a
    /// DIFFERENT four bytes that happen to read the old number, write there, and be read back
    /// "clean" by a verifier that can only read a property that exists.
    ///
    /// SO THIS IS AN ALLOW-LIST OF THE ONE CASE THE SEARCH WAS KEPT FOR, and a reason nobody has
    /// met yet is refused. A refused build is loud and the author fixes the project; a wrong
    /// write is silent and ships. Do not turn this back into a list of reasons to refuse: the
    /// first version did that (an allow-list of "answers"), missed `holds 1 item(s), so [7] is
    /// not there` and `has no key '...'`, and wrote Modifiers[7]'s edit onto Modifiers[0].
    ///
    /// Matched at the START of the reason, because these four are the whole text of a reason that
    /// <see cref="PropertyAdd.Locate"/> builds itself; a longer message that merely mentions one
    /// of them is somebody else's reason.
    /// </summary>
    public static bool Unaddressable(string? why)
        => why is { Length: > 0 } &&
           (why.StartsWith("no mounted container holds ", StringComparison.Ordinal) ||
            why.StartsWith("no export matching '", StringComparison.Ordinal) ||
            why.StartsWith("no property schema for ", StringComparison.Ordinal) ||
            (why.StartsWith("the '", StringComparison.Ordinal) &&
             why.EndsWith("' export's class is not a loaded struct", StringComparison.Ordinal)));

    /// <summary>
    /// What <paramref name="property"/> reads in these bytes, found the same way it is written and
    /// read under the encoding the caller already established.
    ///
    /// Public because a read-back is worth having on its own: it is how anything checks an asset
    /// it has just changed without carrying an offset around, and it is the only honest answer to
    /// "did that edit take".
    /// </summary>
    public static bool Read(IFileProvider provider, byte[] raw, string packageName,
                            string export, string property, Kind kind,
                            out double value, out string? why)
    {
        value = 0;
        if (!PropertyAdd.Locate(provider, raw, packageName, export, property,
                                out var start, out var length, out why))
            return false;

        if (start < 0 || start + length > raw.Length)
        { why = $"{export}.{property} is outside the package"; return false; }

        // The kind, not the width, because four bytes is a float OR an int and reading the wrong
        // one of those turns 20 into 1.1e-44 without anything looking amiss.
        switch (kind)
        {
            case Kind.Byte when length == 1: value = raw[start]; return true;
            case Kind.Double when length == 8: value = BitConverter.ToDouble(raw, start); return true;
            case Kind.Float when length == 4: value = BitConverter.ToSingle(raw, start); return true;
            case Kind.Int when length == 4: value = BitConverter.ToInt32(raw, start); return true;
            case Kind.IntInMap when SingleEntryMap(raw, start, length):
                value = BitConverter.ToInt32(raw, MapValueAt(start, length)); return true;
            default:
                why = $"{export}.{property} is {length} byte(s), which is not a {kind}";
                return false;
        }
    }

    /// <summary>
    /// Where the value of a one-entry map sits: past the two counts and past the key, whose size
    /// is whatever is left. Derived rather than assumed to be four bytes, so a map keyed by
    /// something wider is read correctly or not at all.
    /// </summary>
    private static int MapValueAt(int start, int length) => start + length - 4;

    /// <summary>
    /// Do these bytes look like a map with exactly one entry and nothing removed?
    ///
    /// Every one of these is a refusal, not a formality. Two entries and the value being written
    /// belongs to whichever came first, which is a price set on the wrong resource; a non-zero
    /// removal count means a shape this has never seen; and under sixteen bytes there is no room
    /// for two counts, a key and a value.
    /// </summary>
    private static bool SingleEntryMap(byte[] raw, int start, int length) =>
        length >= MapHeaderBytes + 8 &&
        start + length <= raw.Length &&
        BitConverter.ToInt32(raw, start) == 0 &&
        BitConverter.ToInt32(raw, start + 4) == 1;

    /// <summary>What <see cref="Reads"/> found. Three answers, because they mean three things.</summary>
    public enum Reading
    {
        /// <summary>There is no such property on that export here -- so nothing was checked.</summary>
        NotFound,
        /// <summary>It is there and reads something else. This is the one that matters.</summary>
        Wrong,
        /// <summary>It is there and reads what was asked for.</summary>
        Right,
    }

    /// <summary>
    /// Does <paramref name="property"/> in these bytes read <paramref name="expected"/>?
    ///
    /// For checking an asset somebody else has written -- the built package pulled back out of a
    /// container, rather than the buffer an edit just returned. The encoding is decided the same
    /// way <see cref="SetByName"/> decides it, by reading the located bytes under each form the
    /// width allows, so a caller needs to know only the number it is looking for.
    ///
    /// NOT FOUND is separated from WRONG on purpose. A number aimed at a DataTable row is not a
    /// property on an export and will never be found this way; that is a check that does not
    /// apply, and reporting it as a failure would fail every weapon-stat mod ever built.
    /// </summary>
    public static Reading Reads(IFileProvider provider, byte[] raw, string packageName,
                                string export, string property, double expected, out string? why)
    {
        if (!PropertyAdd.Locate(provider, raw, packageName, export, property,
                                out var start, out var length, out why))
            return Reading.NotFound;

        if (Encoding(raw, start, length, expected) != Kind.None) { why = null; return Reading.Right; }

        why = $"{export}.{property} should read {expected} and reads " +
              (length == 8 ? BitConverter.ToDouble(raw, start).ToString()
               : length == 4 ? $"{BitConverter.ToSingle(raw, start)} as a float, " +
                               $"{BitConverter.ToInt32(raw, start)} as an int"
               : $"{length} bytes this cannot read");
        return Reading.Wrong;
    }

    /// <summary>
    /// Whether a number read back is the number that was asked for.
    ///
    /// A tolerance, not equality, and for a reason the double is not: a value written into a FLOAT
    /// comes back as the nearest float, so 0.06 reads 0.05999999865889549 and an exact comparison
    /// would fail every percentage in the game.
    /// </summary>
    private static bool Agrees(double a, double b) =>
        Math.Abs(a - b) <= Math.Max(1e-6, Math.Abs(b) * 1e-6);

    /// <summary>
    /// Which encoding the located bytes are in, by reading them back and comparing to what the
    /// caller expected. A width alone does not say: four bytes is a float OR an int, and the two
    /// are not remotely interchangeable.
    /// </summary>
    private static Kind Encoding(byte[] raw, int start, int length, double was)
    {
        if (start < 0 || start + length > raw.Length) return Kind.None;

        // A tolerance, not equality: the caller's `was` came out of a float in a UI or a JSON
        // file, and 0.1 does not survive that round trip exactly.
        static bool Same(double a, double b) =>
            Math.Abs(a - b) <= Math.Max(1e-6, Math.Abs(b) * 1e-6);

        // FIRST, and narrowest. SingleEntryMap reads several fields forward from `start`, which a
        // one-byte property does not have, so asking it about a byte is asking it to read past the
        // property into whatever follows.
        if (length == 1)
            return Same(raw[start], was) ? Kind.Byte : Kind.None;

        if (length == 8)
            return Same(BitConverter.ToDouble(raw, start), was) ? Kind.Double : Kind.None;

        // A map's value, when the property Locate handed back is the whole map. It cannot collide
        // with either plain width: a scalar is four or eight bytes and a map with room for two
        // counts, a key and a value is at least sixteen.
        if (SingleEntryMap(raw, start, length) &&
            Same(BitConverter.ToInt32(raw, MapValueAt(start, length)), was))
            return Kind.IntInMap;

        if (length == 4)
        {
            if (Same(BitConverter.ToSingle(raw, start), was)) return Kind.Float;
            if (Same(BitConverter.ToInt32(raw, start), was)) return Kind.Int;
        }

        return Kind.None;
    }
}

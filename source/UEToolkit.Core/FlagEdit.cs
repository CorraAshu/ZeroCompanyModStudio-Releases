namespace UEToolkit.Core;

/// <summary>
/// Clear a boolean that sits immediately before a known reference in an export.
///
/// The narrow tool for a narrow job, and deliberately not a general property editor. Cooked
/// packages are unversioned -- a property has no name or size beside its value, and finding one by
/// name needs the header schema this project has not built. But a bool does not need finding when
/// something next to it can be found: an FName reference is eight bytes with a distinctive index,
/// and the flags of a struct are serialised immediately before its first reference.
///
/// Measured across three weapon specs, which is what makes the offset a fact rather than a guess:
///
///     Melee_2H_TelRea   06 02 03 03 | 01    | FName pair     one bool
///     Melee_1H_Anakin   05 04 03 03 | 01 01 | FName pair     two bools
///     Blaster_Rifle     0a 03       |       | FName pair     none
///
/// The header length changes with the property set and the bools follow it, so counting forward
/// from the start of an export is wrong. Counting BACK from the reference is right, and
/// `bSlotIsLocked` is the last flag before `DefaultPart` in both specs that carry it.
///
/// Three things must all hold or nothing is written. A byte flipped in the wrong place inside a
/// package that REPLACES a real game asset is the most damaging edit this tool can make, and it
/// would not announce itself.
/// </summary>
public static class FlagEdit
{
    /// <summary>An FName in export data: the mapped index, then the number.</summary>
    private const int MappedNameBytes = 8;

    /// <summary>
    /// Set the byte <paramref name="back"/> bytes before the sole reference to
    /// <paramref name="anchor"/> to zero, having checked it currently reads <paramref name="was"/>.
    /// Returns the package unchanged, with a reason, if any guard fails.
    /// </summary>
    public static byte[] Clear(byte[] raw, string anchor, int back, byte was, out string? why)
    {
        why = null;
        if (back <= 0) { why = "the offset must be at least one byte before the anchor"; return raw; }

        var pkg = ZenPackage.Read(raw);
        var at = pkg.Names.IndexOf(anchor);
        if (at < 0) { why = $"'{anchor}' is not in the package's name map"; return raw; }

        // Only the export data. A name is referenced by the import and export maps too, and a
        // flag is not stored next to either of those.
        var start = (int) pkg.HeaderSize;
        if (start < 0 || start >= raw.Length) { why = "the export data is empty"; return raw; }

        var needle = new byte[MappedNameBytes];
        BitConverter.GetBytes((uint) at).CopyTo(needle, 0);
        BitConverter.GetBytes(0u).CopyTo(needle, 4);

        var hits = new List<int>();
        var span = raw.AsSpan();
        for (var i = start; i + MappedNameBytes <= raw.Length; i++)
            if (span[i] == needle[0] && span.Slice(i, MappedNameBytes).SequenceEqual(needle))
                hits.Add(i);

        // The same house rule the other editors follow: a pattern that matches a surprising number
        // of times has not been located, it has been guessed at.
        if (hits.Count != 1)
        { why = $"'{anchor}' is referenced {hits.Count} time(s) in the export data, not once"; return raw; }

        var target = hits[0] - back;
        if (target < start)
        { why = "the byte is before the export data begins"; return raw; }

        if (raw[target] != was)
        {
            why = $"expected 0x{was:X2} at {back} byte(s) before '{anchor}' but found " +
                  $"0x{raw[target]:X2} -- refusing to write into a byte that is not what was described";
            return raw;
        }

        var output = (byte[]) raw.Clone();
        output[target] = 0;
        return output;
    }

    /// <summary>
    /// Clear a boolean the SCHEMA can find, rather than one an anchor sits beside.
    ///
    /// <see cref="Clear"/> exists because this project once had no header schema; it does now, and
    /// where the schema reaches a property this is the better tool -- it names what it is writing
    /// instead of counting backwards from a neighbour, so a template whose property set differs
    /// gets a refusal rather than a byte in the wrong place.
    ///
    /// Refuses on anything it did not expect. A bool in an unversioned export is ONE byte, and a
    /// located span of any other length means the schema walk landed somewhere else -- so the
    /// length is checked, and so is the value, because clearing a flag that is already clear is a
    /// caller's mistake worth reporting rather than a no-op worth hiding.
    ///
    /// Verified by RE-READING. The write is confirmed by locating the property again in the
    /// written bytes, which walks the whole export header afresh: a package the edit had corrupted
    /// stops parsing there rather than reaching a mod folder.
    /// </summary>
    public static byte[] ClearByName(CUE4Parse.FileProvider.IFileProvider provider, byte[] raw,
                                     string packageName, string exportClass, string property,
                                     out string? why)
    {
        if (!PropertyAdd.Locate(provider, raw, packageName, exportClass, property,
                                out var at, out var length, out why))
        {
            why ??= $"could not find {exportClass}.{property}";
            return raw;
        }

        if (length != 1)
        { why = $"{property} occupies {length} byte(s), not the one a bool occupies"; return raw; }
        if (at < 0 || at >= raw.Length)
        { why = $"{property} is at {at}, outside the package"; return raw; }
        if (raw[at] == 0)
        { why = $"{property} is already false"; return raw; }
        if (raw[at] != 1)
        { why = $"{property} reads 0x{raw[at]:X2}, which is neither true nor false"; return raw; }

        var output = (byte[]) raw.Clone();
        output[at] = 0;

        if (!PropertyAdd.Locate(provider, output, packageName, exportClass, property,
                                out var again, out _, out var readWhy))
        { why = $"the package did not read back after clearing {property}: {readWhy}"; return raw; }
        if (again != at || output[again] != 0)
        { why = $"{property} did not read back as false"; return raw; }

        why = null;
        return output;
    }
}

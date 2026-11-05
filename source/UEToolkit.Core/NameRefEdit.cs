namespace UEToolkit.Core;

/// <summary>
/// Re-point every reference to one name at a different name, without changing any length.
///
/// This is the cheapest edit a cooked package admits, and it turns out to be enough for a whole
/// class of mod. A gameplay tag QUERY stores the tags it asks about in a dictionary of FNames, and
/// an FName in export data is just an index into the package's name map. So "ask about a tag
/// nothing carries" is an eight-byte write: swap the index, and every gate built on that query
/// opens. Nothing moves, no offset needs correcting, and the property serialiser this project does
/// not yet have is not needed.
///
/// It is deliberately NOT <see cref="ZenPackage.RenameNames"/>, which would achieve the same thing
/// by editing the map entry itself. Renaming re-points every use at once, including uses nobody
/// enumerated -- fine when the name is a path used once, wrong when it is a tag prefix that other
/// properties legitimately share. This counts the references it is about to change and refuses
/// unless the count is the one the caller predicted, which is the same house rule
/// <see cref="GameplayTagEdit"/> and <see cref="DisplayNameEdit"/> already follow: a pattern that
/// matches a surprising number of times has not been located, it has been guessed at.
/// </summary>
public static class NameRefEdit
{
    /// <summary>An FName in export data: the mapped index, then the number.</summary>
    private const int MappedNameBytes = 8;

    /// <summary>
    /// Re-point <paramref name="expected"/> references to <paramref name="fromName"/> at
    /// <paramref name="toName"/>. Returns the package unchanged, with a reason, when the count
    /// does not match -- editing the wrong eight bytes corrupts a property silently, and not
    /// editing is always the safer failure.
    /// </summary>
    public static byte[] Repoint(byte[] raw, string fromName, string toName, int expected, out string? why)
    {
        why = null;
        if (string.Equals(fromName, toName, StringComparison.Ordinal))
        { why = "the two names are the same"; return raw; }

        var pkg = ZenPackage.Read(raw);

        var from = pkg.Names.IndexOf(fromName);
        if (from < 0) { why = $"'{fromName}' is not in the package's name map"; return raw; }

        // A name already present keeps its index; only a new one is appended, and then its index
        // is one past the end because AppendName never inserts.
        var to = pkg.Names.IndexOf(toName);
        var appending = to < 0;
        if (appending) to = pkg.Names.Count;

        var hits = Find(raw, pkg, from);
        if (hits.Count != expected)
        {
            why = $"'{fromName}' is referenced {hits.Count} time(s) in the export data, not {expected}";
            return raw;
        }

        var output = (byte[]) raw.Clone();
        var replacement = MappedName(to);
        foreach (var at in hits) replacement.CopyTo(output, at);

        if (!appending) return output;

        // Order is load-bearing and has precedent in GameplayTagEdit: the bytes are edited FIRST,
        // and the name appended afterwards on a package re-read from the edited bytes. Appending
        // moves HeaderSize, so every offset found before it goes stale.
        var grown = ZenPackage.Read(output);
        grown.AppendName(toName);
        return grown.Write();
    }

    /// <summary>
    /// How many times a name is referenced from the export data. Exposed so a caller can find out
    /// what to predict instead of discovering it as a refusal.
    /// </summary>
    public static int Count(byte[] raw, string name)
    {
        var pkg = ZenPackage.Read(raw);
        var at = pkg.Names.IndexOf(name);
        return at < 0 ? 0 : Find(raw, pkg, at).Count;
    }

    /// <summary>
    /// Every eight-byte `(index, 0)` at or after the start of the export data.
    ///
    /// The search starts at HeaderSize on purpose. The import and export maps reference names too,
    /// and an object's own name is not a property value -- re-pointing one there would rename the
    /// export rather than change what a property says.
    ///
    /// Byte-by-byte rather than on a four-byte stride: UE does not pad its serialised properties,
    /// so an FName's file offset carries no alignment guarantee. Coincidental matches are possible
    /// and are exactly what the count guard is for.
    /// </summary>
    private static List<int> Find(byte[] raw, ZenPackage pkg, int nameIndex)
    {
        var needle = MappedName(nameIndex);
        var hits = new List<int>();
        var span = raw.AsSpan();
        var start = (int) pkg.HeaderSize;
        if (start < 0 || start >= raw.Length) return hits;

        for (var i = start; i + MappedNameBytes <= raw.Length; i++)
            if (span[i] == needle[0] && span.Slice(i, MappedNameBytes).SequenceEqual(needle))
                hits.Add(i);
        return hits;
    }

    /// <summary>An FName as export data spells it: the mapped index, then a zero number.</summary>
    private static byte[] MappedName(int index)
    {
        var bytes = new byte[MappedNameBytes];
        BitConverter.GetBytes((uint) index).CopyTo(bytes, 0);
        // The name NUMBER. A numbered FName is "Foo_3", which a gameplay tag never is.
        BitConverter.GetBytes(0u).CopyTo(bytes, 4);
        return bytes;
    }
}

namespace UEToolkit.Core;

/// <summary>
/// Add a gameplay tag to a container already inside a cooked package.
///
/// This exists so `hidesHead: true` can MEAN something again. In the Unreal pipeline it drove the
/// editor script, which wrote the tags directly; in a builder that authors by copying an existing
/// part it degenerated into "pick a template that happens to hide something", and nothing in the
/// game hides a character's FACE -- exactly one part does, and it is the mod the editor produced.
/// So the tag has to be written rather than shopped for.
///
/// The container is found by the tags it ALREADY holds. A cooked FGameplayTagContainer is a count
/// followed by that many FNames, and an FName in export data is an index into the package's name
/// map plus a number -- so the existing tags give a byte pattern specific enough to locate, and
/// requiring exactly one match is what stops it being a guess.
/// </summary>
public static class GameplayTagEdit
{
    /// <summary>An FName in export data: the mapped index, then the number.</summary>
    private const int MappedNameBytes = 8;

    /// <summary>
    /// Append <paramref name="tag"/> to the container whose current contents are
    /// <paramref name="existing"/>. Returns the package unchanged, with a reason, when the
    /// container cannot be located unambiguously -- writing into the wrong place would corrupt the
    /// package, and not editing is always the safer failure.
    /// </summary>
    public static byte[] Add(byte[] raw, IReadOnlyList<string> existing, string tag, out string? why)
    {
        why = null;
        if (existing.Contains(tag, StringComparer.Ordinal)) return raw;

        var pkg = ZenPackage.Read(raw);

        // Every existing tag has to resolve to a name index, or the pattern cannot be built.
        var indices = new List<int>();
        foreach (var t in existing)
        {
            var at = pkg.Names.IndexOf(t);
            if (at < 0) { why = $"'{t}' is not in the package's name map"; return raw; }
            indices.Add(at);
        }

        // An EMPTY container serialises to a bare count of zero, which occurs all over a package
        // and cannot be told apart from any other zero. Refused rather than guessed at.
        if (indices.Count == 0)
        { why = "the container is empty, so there is no pattern to find it by"; return raw; }

        var pattern = Serialize(indices);
        var found = IndexOfAll(raw, pattern);
        if (found.Count != 1)
        { why = $"the tag list appears {found.Count} times; refusing to guess which is the right one"; return raw; }

        // The new name is appended, so its index is one past the end -- and appending happens after
        // the splice, on a package read back from the spliced bytes, because the splice must not
        // see a name batch that has already moved.
        var newIndex = pkg.Names.IndexOf(tag);
        var appending = newIndex < 0;
        if (appending) newIndex = pkg.Names.Count;

        var replacement = Serialize(indices.Append(newIndex).ToList());
        var spliced = ZenPackage.SpliceExportData(raw, found[0], pattern.Length, replacement);

        if (!appending) return spliced;

        var grown = ZenPackage.Read(spliced);
        grown.AppendName(tag);
        return grown.Write();
    }

    /// <summary>
    /// Rewrite a tag container to exactly <paramref name="wanted"/>.
    ///
    /// This exists because re-pointing a NAME is the wrong tool for removing a gate. A name
    /// reference is eight bytes, and a small name index makes a byte pattern common enough to
    /// occur by accident: dropping Captain Rex's name tag from his gear kit found TWO matches in a
    /// package that mentions him once, because his index is 4 and `04 00 00 00 00 00 00 00` is
    /// unremarkable. The count guard refused, correctly, and there was nothing to re-point safely.
    ///
    /// Locating the whole CONTAINER by its full contents is specific in a way one name never is --
    /// the same argument <see cref="Add"/> already makes -- and it can shrink as well as swap,
    /// which is what removing a gate actually means.
    /// </summary>
    public static byte[] Set(byte[] raw, IReadOnlyList<string> existing, IReadOnlyList<string> wanted,
                             out string? why)
    {
        why = null;
        if (existing.SequenceEqual(wanted, StringComparer.Ordinal)) return raw;
        if (existing.Count == 0)
        { why = "the container is empty, so there is no pattern to find it by"; return raw; }

        var pkg = ZenPackage.Read(raw);

        var current = new List<int>();
        foreach (var t in existing)
        {
            var at = pkg.Names.IndexOf(t);
            if (at < 0) { why = $"'{t}' is not in the package's name map"; return raw; }
            current.Add(at);
        }

        var pattern = Serialize(current);
        var found = IndexOfAll(raw, pattern);
        if (found.Count != 1)
        { why = $"the tag list appears {found.Count} times; refusing to guess which is the right one"; return raw; }

        // Names that are not in the map yet are appended AFTER the splice, for the reason Add
        // documents: appending moves the name batch, so an offset found beforehand goes stale.
        var appended = new List<string>();
        var next = new List<int>();
        foreach (var t in wanted)
        {
            var at = pkg.Names.IndexOf(t);
            if (at < 0)
            {
                var already = appended.IndexOf(t);
                at = already >= 0 ? pkg.Names.Count + already : pkg.Names.Count + appended.Count;
                if (already < 0) appended.Add(t);
            }
            next.Add(at);
        }

        var spliced = ZenPackage.SpliceExportData(raw, found[0], pattern.Length, Serialize(next));
        if (appended.Count == 0) return spliced;

        var grown = ZenPackage.Read(spliced);
        foreach (var t in appended) grown.AppendName(t);
        return grown.Write();
    }

    /// <summary>
    /// The same edit applied to EVERY container that currently reads <paramref name="existing"/>,
    /// when the caller knows how many there are and wants all of them changed the same way.
    ///
    /// <see cref="Set"/> refuses on more than one match, and that refusal is right: two containers
    /// holding the same tags are two different statements, and picking one is guessing. But there
    /// is a case where it is not a guess, and it is the case colour swatches land in.
    ///
    ///     CPD_H_Outfit_Clo010_HELM
    ///       LockedSlots   ...Helmet.Color.Secondary   ...Helmet.Color.Visor
    ///       HiddenSlots   ...Helmet.Color.Secondary   ...Helmet.Color.Visor
    ///
    /// Identical contents, so one byte pattern and two occurrences -- and handing a swatch back
    /// means taking it out of BOTH. Out of HiddenSlots alone it comes back greyed out forever; out
    /// of LockedSlots alone it stays invisible. So "change every one of them" is exactly what is
    /// meant, and saying how many there should be keeps it a statement rather than a sweep: an
    /// asset with three matches when two were expected is not the asset this was reasoned about.
    ///
    /// REMOVAL ONLY. Every wanted tag must already be in the name map, which it is when the edit
    /// takes tags away. Appending a name moves the name batch and would invalidate the offsets of
    /// the matches not yet spliced -- solvable, but it is machinery for a case nothing has.
    ///
    /// LAST MATCH FIRST, so an earlier offset is still where it was: a splice rewrites sizes in
    /// the export map, which is fixed-width and sits before the data, so nothing ahead of the cut
    /// moves.
    /// </summary>
    public static byte[] SetEvery(byte[] raw, IReadOnlyList<string> existing,
                                  IReadOnlyList<string> wanted, int expected, out string? why)
    {
        why = null;
        if (existing.SequenceEqual(wanted, StringComparer.Ordinal)) return raw;
        if (existing.Count == 0)
        { why = "the container is empty, so there is no pattern to find it by"; return raw; }
        if (expected < 1) { why = $"{expected} container(s) is not a number to edit"; return raw; }

        var pkg = ZenPackage.Read(raw);

        var current = new List<int>();
        foreach (var t in existing)
        {
            var at = pkg.Names.IndexOf(t);
            if (at < 0) { why = $"'{t}' is not in the package's name map"; return raw; }
            current.Add(at);
        }

        var next = new List<int>();
        foreach (var t in wanted)
        {
            var at = pkg.Names.IndexOf(t);
            if (at < 0)
            { why = $"'{t}' is not in the package's name map, and this only takes tags away"; return raw; }
            next.Add(at);
        }

        var pattern = Serialize(current);
        var replacement = Serialize(next);
        var found = IndexOfAll(raw, pattern);
        if (found.Count != expected)
        {
            why = $"the tag list appears {found.Count} time(s) and {expected} were expected";
            return raw;
        }

        var written = raw;
        for (var i = found.Count - 1; i >= 0; i--)
            written = ZenPackage.SpliceExportData(written, found[i], pattern.Length, replacement);
        return written;
    }

    /// <summary>
    /// Does a container holding exactly <paramref name="wanted"/>, in that order, appear exactly
    /// once in these bytes?
    ///
    /// The read-back for <see cref="Set"/>, and it is deliberately the same standard as the write:
    /// the list is turned into the bytes it must serialise to and those bytes are looked for.
    ///
    /// IT EXISTS BECAUSE THE OBVIOUS READ-BACK CANNOT WORK HERE. Re-parsing the built package and
    /// asking it for its tags reads NOTHING on an asset whose class is a Blueprint generated in
    /// another package -- the class does not resolve, so the export comes back as a bare UObject
    /// with no properties -- and a check that reads nothing reports everything missing. Measured:
    /// a correct rarity swap was reported as "does not carry Rarity.Unique. It reads []".
    ///
    /// This needs no schema and no class. A name map and a byte pattern are enough, and both come
    /// straight out of the package.
    /// </summary>
    public static bool Reads(byte[] raw, IReadOnlyList<string> wanted, out string? why)
    {
        why = null;
        if (wanted.Count == 0)
        { why = "an empty container has no pattern to look for"; return false; }

        var pkg = ZenPackage.Read(raw);
        var indices = new List<int>();
        foreach (var t in wanted)
        {
            var at = pkg.Names.IndexOf(t);
            if (at < 0) { why = $"'{t}' is not in the package's name map"; return false; }
            indices.Add(at);
        }

        var found = IndexOfAll(raw, Serialize(indices));
        if (found.Count == 1) return true;

        why = found.Count == 0
            ? "no container in the package holds exactly these tags, in this order"
            : $"{found.Count} containers hold exactly these tags, so this says nothing about which";
        return false;
    }

    private static byte[] Serialize(List<int> indices)
    {
        var bytes = new byte[4 + indices.Count * MappedNameBytes];
        BitConverter.GetBytes(indices.Count).CopyTo(bytes, 0);
        for (var i = 0; i < indices.Count; i++)
        {
            BitConverter.GetBytes((uint) indices[i]).CopyTo(bytes, 4 + i * MappedNameBytes);
            // The name NUMBER, zero for every tag: a numbered FName is "Foo_3", which a gameplay
            // tag never is.
            BitConverter.GetBytes(0u).CopyTo(bytes, 8 + i * MappedNameBytes);
        }
        return bytes;
    }

    private static List<int> IndexOfAll(byte[] haystack, byte[] needle)
    {
        var hits = new List<int>();
        var span = haystack.AsSpan();
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (span[i] == needle[0] && span.Slice(i, needle.Length).SequenceEqual(needle))
                hits.Add(i);
        return hits;
    }
}

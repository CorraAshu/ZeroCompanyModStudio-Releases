using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.i18N;

namespace UEToolkit.Core;

/// <summary>
/// Give an authored part its own name in the menu.
///
/// The name is an FText on the part's UI fragment, and the game's parts store it as a STRING TABLE
/// reference -- a table asset plus a key, with the text itself in a separate asset. So there is no
/// name in the name map to rename and no string in the package to overwrite: the only way to say
/// "Anakin Sleeves (Mod)" is to replace the FText with a different KIND of FText, the
/// culture-invariant form the cook produces when someone types a name into the editor.
///
/// That form is shorter or longer than the reference it replaces, which is why
/// <see cref="ZenPackage.SpliceExportData"/> exists.
/// </summary>
public static class DisplayNameEdit
{
    /// <summary>ETextHistoryType.None, written as a signed byte.</summary>
    private const byte HistoryNone = 0xFF;

    /// <summary>
    /// Replace an FText that occupies a known byte range, whatever it currently holds.
    ///
    /// The escape hatch for the case <see cref="SetText"/> cannot serve: it anchors on the string
    /// the FText already carries, and an EMPTY one carries none. The game leaves DisplayName empty
    /// on roughly a third of its parts -- including every piece of Captain Rex's armour -- because
    /// they were never meant to appear in a menu. Those are exactly the parts somebody duplicates
    /// and then wants to name, and until this existed the build refused the rename and, since the
    /// check is what gates Install, refused the mod.
    ///
    /// The range comes from <see cref="PropertyAdd.Locate"/>, which walks the export's property
    /// list rather than searching the bytes, so it is exact whether the value is empty or not.
    /// </summary>
    public static byte[] SetTextAt(byte[] raw, int start, int length, string name, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(name)) { why = "no text given"; return raw; }
        if (name.Any(c => c > 0x7F)) { why = $"'{name}' is not ASCII"; return raw; }
        if (start < 0 || length < 0 || start + length > raw.Length)
        { why = "the range is not inside the package"; return raw; }

        // The existing flags are kept: they are the first four bytes of any FText and say things
        // about the text that have nothing to do with what it says.
        var flags = length >= 4 ? BitConverter.ToUInt32(raw, start) : 0u;

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(flags);
            w.Write(HistoryNone);
            w.Write(1u);                       // bHasCultureInvariantString -- a bool is 32 bits
            var bytes = Encoding.ASCII.GetBytes(name);
            w.Write(bytes.Length + 1);         // an FString length INCLUDES the terminator
            w.Write(bytes);
            w.Write((byte) 0);
        }

        return ZenPackage.SpliceExportData(raw, start, length, ms.ToArray());
    }

    /// <summary>
    /// Make an FText EMPTY: its flags kept, history None, and no culture-invariant string -- the
    /// shape a cooked empty text has. For the last name a droid does not have: a blank left to
    /// "keep the template's" put the pool template's "Bela" after "B1 Battle Droid" (2026-09-07).
    /// </summary>
    public static byte[] ClearTextAt(byte[] raw, int start, int length, out string? why)
    {
        why = null;
        if (start < 0 || length < 0 || start + length > raw.Length)
        { why = "the range is not inside the package"; return raw; }
        var flags = length >= 4 ? BitConverter.ToUInt32(raw, start) : 0u;

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(flags);
            w.Write(HistoryNone);
            w.Write(0u);                       // bHasCultureInvariantString: false, so no string follows
        }
        return ZenPackage.SpliceExportData(raw, start, length, ms.ToArray());
    }

    /// <summary>
    /// Rewrite the DisplayName of the first UI fragment that has one. Returns the package
    /// unchanged, with a reason, when the name cannot be located -- a wrong guess here corrupts
    /// the package, so not editing is always the safer answer.
    /// </summary>
    public static byte[] SetDisplayName(byte[] raw, IReadOnlyList<UObject> exports, string name,
                                        out string? why)
        => SetText(raw, exports, "DisplayName", name, out why);

    /// <summary>
    /// Rewrite any FText on the UI fragment, by property name.
    ///
    /// The same machinery as the display name, which was only ever hardcoded to `DisplayName`
    /// because that was the first text anyone wanted to change. An authored part inherits its
    /// template's ShortDescription and LongDescription too, and a specialisation someone invented
    /// that still describes itself as Assault is wearing the template's identity in the one place
    /// the player actually reads.
    /// </summary>
    public static byte[] SetText(byte[] raw, IReadOnlyList<UObject> exports, string property,
                                 string name, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(name)) { why = "no text given"; return raw; }
        if (name.Any(c => c > 0x7F)) { why = $"'{name}' is not ASCII"; return raw; }

        var text = exports.Select(e => Property(e, property)).FirstOrDefault(t => t is not null);
        if (text is null) { why = $"no {property} on any export"; return raw; }

        // The anchor is whatever STRING the existing FText carries, because that is the only part
        // of it that can be found in the bytes. Its position fixes the FText's start, since every
        // field before it is a known width.
        var (anchor, before) = text.TextHistory switch
        {
            FTextHistory.StringTableEntry s => (s.Key, 4 + 1 + 8),   // flags, type, FName table id
            FTextHistory.None n when n.CultureInvariantString is { } c => (c, 4 + 1 + 4),
            FTextHistory.Base b => (b.SourceString, -1),             // namespace and key come first
            _ => (null, -1),
        };
        if (anchor is null || before < 0)
        { why = $"DisplayName is a {text.HistoryType} FText, which this does not rewrite"; return raw; }

        var found = FindFString(raw, anchor);
        if (found.Count != 1)
        { why = $"the existing name appears {found.Count} times; refusing to guess"; return raw; }

        var lengthAt = found[0];
        var start = lengthAt - before;
        var end = lengthAt + 4 + Encoding.ASCII.GetByteCount(anchor) + 1;
        if (start < 0) { why = "the FText starts before the file does"; return raw; }

        var flags = BitConverter.ToUInt32(raw, start);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(flags);
            w.Write(HistoryNone);
            w.Write(1u);                       // bHasCultureInvariantString -- a bool is 32 bits
            var bytes = Encoding.ASCII.GetBytes(name);
            w.Write(bytes.Length + 1);         // an FString length INCLUDES the terminator
            w.Write(bytes);
            w.Write((byte) 0);
        }

        return ZenPackage.SpliceExportData(raw, start, end - start, ms.ToArray());
    }

    /// <summary>
    /// Write an FText WHEREVER this asset keeps one of that name -- including inside a struct.
    ///
    /// <see cref="SetText"/> knows one place: a top-level property on a UI fragment. That covers
    /// every part and every specialisation, and it covers none of the GEAR KIT, whose name sits in
    /// <c>GearKitInfo.DisplayName</c> one struct down. The gear kit is what the Armoury's MODEL
    /// header draws, so until this existed a duplicated weapon carried its own name on five assets
    /// and the donor's on the sixth -- and the sixth was the one at the top of the screen.
    ///
    /// The known place is tried FIRST and unchanged. Only when it finds nothing does this look for
    /// others, and then it refuses an ambiguous answer rather than picking one: two properties of
    /// this name in one export are two different things.
    ///
    /// **The result is read back before it is returned.** A nested locate is a claim about byte
    /// offsets in a struct nothing proved has a header -- see <see cref="PropertyAdd.Locate"/> --
    /// so bytes that do not parse back to the text asked for are handed back as an error, not as a
    /// package. <paramref name="where"/> says which place was written, for the log.
    /// </summary>
    public static byte[] SetTextAnywhere(IFileProvider provider, byte[] raw, string packageName,
                                         IReadOnlyList<UObject> exports, string property,
                                         string value, out string? why, out string? where)
    {
        where = null;
        var known = SetText(raw, exports, property, value, out why);
        if (why is null) { where = "its UI fragment"; return known; }

        var candidates = exports
            .Where(e => e is not null)
            .SelectMany(e => PropertyAdd.PathsTo(e, property).Select(path => (Export: e, Path: path)))
            .Where(c => c.Path.Contains('.'))          // the flat case is SetText's, and it failed
            .ToList();

        // NOT CARRIED ANYWHERE -- but perhaps DECLARED. A cooked package writes only what differs
        // from the default, so a copy of Resonate has a UIData with a Name and no Description at
        // all, and asking for a description used to end here with "stayed as the template's". If
        // exactly one struct's TYPE has the member, it is added there; several is refused rather
        // than guessed, the same rule as the carried case below.
        if (candidates.Count == 0)
        {
            var declared = exports
                .Where(e => e is not null)
                .SelectMany(e => PropertyAdd.StructsDeclaring(provider, e, property)
                                            .Select(path => (Export: e, Path: path)))
                .ToList();
            if (declared.Count == 0) return raw;       // keep SetText's reason: it is the true one
            if (declared.Count > 1)
            {
                why = $"{why}; '{property}' could be added to {string.Join(", ", declared.Select(d => d.Path))} " +
                      "-- refusing to guess which";
                return raw;
            }

            var (owner, into) = declared[0];
            var added = PropertyAdd.AddText(provider, raw, packageName, owner.Name, $"{into}.{property}",
                                            value, out var addWhy);
            if (addWhy is not null) { why = $"{why}; and adding it to {into}: {addWhy}"; return raw; }

            why = null;
            where = $"{into}, added";
            return added;
        }
        if (candidates.Count > 1)
        {
            why = $"'{property}' appears at {string.Join(", ", candidates.Select(c => c.Path))} " +
                  "-- refusing to guess which one is the name";
            return raw;
        }

        var (export, at) = candidates[0];
        if (!PropertyAdd.Locate(provider, raw, packageName, export.Name, at,
                                out var start, out var length, out var locateWhy))
        { why = $"{at}: {locateWhy}"; return raw; }

        var written = SetTextAt(raw, start, length, value, out why);
        if (why is not null) return raw;

        // READ IT BACK. Everything above this line is arithmetic on offsets.
        var after = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, written, packageName));
        var reread = after.FirstOrDefault(e => e is not null &&
                          string.Equals(e.Name, export.Name, StringComparison.Ordinal));
        if (reread is null || TextAt(reread, at) is not { } got)
        { why = $"after writing it, {at} could not be read back"; return raw; }
        if (!string.Equals(got, value, StringComparison.Ordinal))
        { why = $"after writing it, {at} reads \"{got}\" and not \"{value}\""; return raw; }

        where = at;
        return written;
    }

    /// <summary>The text at a dotted path, or null when the path leads nowhere.</summary>
    private static string? TextAt(UObject export, string path)
    {
        IEnumerable<CUE4Parse.UE4.Assets.Objects.FPropertyTag> here = export.Properties;
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var tag = here.FirstOrDefault(x => x.Name.Text == parts[i]);
            if (tag is null) return null;
            if (i == parts.Length - 1) return (tag.Tag?.GenericValue as FText)?.Text;
            if (tag.Tag?.GenericValue is not CUE4Parse.UE4.Assets.Objects.FScriptStruct
                { StructType: CUE4Parse.UE4.Assets.Objects.FStructFallback inner }) return null;
            here = inner.Properties;
        }
        return null;
    }

    /// <summary>
    /// The DisplayName FText, from the UI fragment that carries it. Matched the same way
    /// PartDetails matches it -- a prefix test, because BitReactorUIDataFragment is subclassed by
    /// BitReactorUIDataFragmentWithPlatformOverrides and an equality test misses those.
    /// </summary>
    private static FText? Property(UObject export, string name)
    {
        if (!export.ExportType.StartsWith("BitReactorUIDataFragment", StringComparison.Ordinal))
            return null;
        return export.Properties.FirstOrDefault(p => p.Name.Text == name)?.Tag?.GenericValue as FText;
    }

    /// <summary>
    /// Every place an ASCII FString with this exact content appears: the four-byte length that
    /// counts the terminator, then the characters. Matching the LENGTH as well as the text is what
    /// stops a substring of some other string being taken for the whole of this one.
    /// </summary>
    private static List<int> FindFString(byte[] raw, string value)
    {
        var body = Encoding.ASCII.GetBytes(value);
        var prefix = BitConverter.GetBytes(body.Length + 1);
        var hits = new List<int>();

        for (var i = 0; i + 4 + body.Length + 1 <= raw.Length; i++)
        {
            if (raw[i] != prefix[0] || raw[i + 1] != prefix[1] ||
                raw[i + 2] != prefix[2] || raw[i + 3] != prefix[3]) continue;
            if (raw[i + 4 + body.Length] != 0) continue;
            if (!raw.AsSpan(i + 4, body.Length).SequenceEqual(body)) continue;
            hits.Add(i);
        }
        return hits;
    }
}

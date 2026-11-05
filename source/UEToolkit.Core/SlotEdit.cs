namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Objects;

/// <summary>
/// The SLOTS of a character definition, read and rewritten in place.
///
/// A <c>CustomizationDefinition</c> is one map: slot tag -> the part in that slot, seventy-odd
/// entries for a humanoid, every value a SOFT object path and most of what makes somebody who they
/// are. Info.Name, Species, Head.Face.Mesh, five outfit meshes and their colours, the voice, the
/// portrait pose. Change the values and you have made a different person.
///
/// WHY THIS IS NOT A RENAME. A soft path is two FNames, and the copy machinery already renames
/// FNames -- so the first design was "rename the name-map entries". It cannot work, and the reason
/// is in <see cref="ZenPackage.ParseNumber"/>: a name ending in a splittable <c>_N</c> is stored as
/// its STEM in the name map with the number on each USE. Every outfit colour is one --
/// <c>CPD_H_Outfit_Color_Amber_30</c> is ("CPD_H_Outfit_Color_Amber", 31) -- and the stem is
/// SHARED: Amber_30 on the torso and Amber_32 on the legs are one entry with two numbers. Renaming
/// the entry moves both, and asking to rename "Amber_30" finds nothing at all, in silence. So the
/// value is written where the cooker wrote it: the two FMappedNames of that one map entry, index
/// and number, exactly as <see cref="DataTableEdit.AddRow"/> writes a row's soft reference.
///
/// The map is located through <see cref="PropertyAdd.Locate"/>, which reads the TEMPLATE's schema,
/// so this runs before the copy's renames, in the same position the copy loop gives every other
/// property edit. Every name a value needs is appended to the name map FIRST and the package
/// rewritten, because appending moves every offset after the name batch.
///
/// The result is read back through CUE4Parse before it is returned, or refused. A map entry that
/// was written to the wrong bytes reads back as somebody else's face, and nothing short of loading
/// it would ever say so.
/// </summary>
public static class SlotEdit
{
    public const string ExportClass = "CustomizationDefinition";
    public const string Property = "SlotOverrides";
    public const string TagPrefix = "br.Customization.Slot.";

    /// <summary>One slot as it stands: its full tag and the object path in it, "" when empty.</summary>
    public sealed record Slot(string Tag, string Path)
    {
        /// <summary>"Character.Outfit.Torso.Mesh" -- the tag without the prefix every slot shares.</summary>
        public string Tail => Tag.StartsWith(TagPrefix, StringComparison.Ordinal) ? Tag[TagPrefix.Length..] : Tag;

        /// <summary>The asset name in the slot, or "" for an empty one.</summary>
        public string Leaf => LeafOf(Path);
    }

    /// <summary>The asset name of an object path: "/A/B/C.C" -> "C". "" stays "".</summary>
    public static string LeafOf(string path)
    {
        if (path.Length == 0) return "";
        var dot = path.LastIndexOf('.');
        var slash = path.LastIndexOf('/');
        return dot > slash ? path[(dot + 1)..] : path[(slash + 1)..];
    }

    /// <summary>The package half of an object path: "/A/B/C.C" -> "/A/B/C".</summary>
    public static string PackageOf(string path)
    {
        if (path.Length == 0) return "";
        var slash = path.LastIndexOf('/');
        var dot = path.IndexOf('.', slash + 1);
        return dot > 0 ? path[..dot] : path;
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Every slot of a definition the game (or a mount) holds, in the map's own order.</summary>
    public static List<Slot> ReadFrom(IFileProvider provider, string definitionPackage, out string? why)
    {
        why = null;
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(definitionPackage)).GetExports().ToList());
            return FromExports(exports, out why);
        }
        catch (Exception e) { why = e.Message; return new List<Slot>(); }
    }

    /// <summary>Every slot of a definition held as bytes -- a copy under construction.</summary>
    public static List<Slot> ReadBytes(IFileProvider provider, byte[] raw, string templatePackage, out string? why)
    {
        why = null;
        try
        {
            var exports = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, raw, templatePackage));
            return FromExports(exports, out why);
        }
        catch (Exception e) { why = e.Message; return new List<Slot>(); }
    }

    private static List<Slot> FromExports(IEnumerable<CUE4Parse.UE4.Assets.Exports.UObject?> exports, out string? why)
    {
        why = null;
        var export = exports.FirstOrDefault(e => e is not null && e.ExportType == ExportClass);
        if (export is null) { why = $"no {ExportClass} export"; return new List<Slot>(); }
        if (PartDetails.Value(export.Properties, Property) is not UScriptMap map)
        { why = $"no {Property} map on it"; return new List<Slot>(); }

        var found = new List<Slot>();
        foreach (var kv in map.Properties)
        {
            var tag = PartDetails.TagOf(kv.Key?.GenericValue);
            if (tag is null) continue;
            var path = PartDetails.SoftPathOf(kv.Value?.GenericValue) ?? "";
            if (string.Equals(path, "None", StringComparison.OrdinalIgnoreCase)) path = "";
            found.Add(new Slot(tag, path));
        }
        return found;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Write new values into a definition's slots.
    ///
    /// <paramref name="values"/> is slot tag (full, or the tail after the shared prefix) -> the
    /// full OBJECT path to put there ("/A/B/C.C"), or "" to empty the slot. A tag the map does not
    /// hold is refused: a slot cannot be added here, only filled, and a misspelt one is more likely
    /// than a missing one.
    /// </summary>
    public static byte[] Set(IFileProvider provider, byte[] raw, string templatePackage,
                             IReadOnlyDictionary<string, string> values, out string? why,
                             Action<string>? log = null)
    {
        why = null;
        if (values.Count == 0) return raw;

        // 1. Every name the new values need, in the map first. A numbered name goes in as its
        //    STEM, which is how the cooker stores it; the number is written on the use below.
        var pkg = ZenPackage.Read(raw);
        var needed = new List<string> { "None" };
        foreach (var path in values.Values)
        {
            if (path.Length == 0) continue;
            needed.Add(StemOf(PackageOf(path)));
            needed.Add(StemOf(LeafOf(path)));
        }
        var missing = needed.Where(n => n.Length > 0 && pkg.Names.IndexOf(n) < 0)
                            .Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            foreach (var n in missing) pkg.AppendName(n);
            raw = pkg.Write();
            pkg = ZenPackage.Read(raw);
            log?.Invoke($"      {missing.Count} name(s) appended for the slots");
        }

        // 2. The map's bytes, located against the template's schema on the package AS IT NOW IS.
        if (!PropertyAdd.Locate(provider, raw, templatePackage, ExportClass, Property,
                                out var start, out var length, out var locateWhy))
        { why = $"could not locate {Property}: {locateWhy}"; return raw; }

        var entries = Parse(pkg, raw, start, length, out var parseWhy);
        if (entries is null) { why = $"{Property} does not parse as a map of tag -> soft path: {parseWhy}"; return raw; }

        // 3. Overwrite the two FMappedNames of each requested entry, in place -- same width.
        var written = (byte[]) raw.Clone();
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tag, path) in values) wanted[Full(tag)] = path;

        var landed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!wanted.TryGetValue(e.Tag, out var path)) continue;
            landed.Add(e.Tag);
            WriteName(written, e.ValueAt, pkg, path.Length == 0 ? "None" : PackageOf(path));
            WriteName(written, e.ValueAt + 8, pkg, path.Length == 0 ? "None" : LeafOf(path));
            log?.Invoke($"      {e.Tag[(e.Tag.StartsWith(TagPrefix, StringComparison.Ordinal) ? TagPrefix.Length : 0)..]}" +
                        $": {(e.Path.Length == 0 ? "(empty)" : LeafOf(e.Path))} -> {(path.Length == 0 ? "(empty)" : LeafOf(path))}");
        }
        var unknown = wanted.Keys.Where(t => !landed.Contains(t)).ToList();
        if (unknown.Count > 0)
        {
            why = $"the definition has no slot called {string.Join(", ", unknown.Select(t => t[(t.StartsWith(TagPrefix, StringComparison.Ordinal) ? TagPrefix.Length : 0)..]))}";
            return raw;
        }

        // 4. READ BACK, OR REFUSE.
        var check = ReadBytes(provider, written, templatePackage, out var backWhy);
        if (backWhy is not null) { why = $"the edited definition no longer reads: {backWhy}"; return raw; }
        foreach (var (tag, path) in wanted)
        {
            var got = check.FirstOrDefault(s => string.Equals(s.Tag, tag, StringComparison.Ordinal));
            if (got is null) { why = $"{tag} did not read back at all"; return raw; }
            if (!string.Equals(got.Path, path, StringComparison.OrdinalIgnoreCase))
            { why = $"{got.Tail} reads back as '{got.Path}', not '{path}'"; return raw; }
        }
        return written;
    }

    /// <summary>A tag as the map holds it: the shared prefix put back when the caller left it off.</summary>
    private static string Full(string tag) =>
        tag.StartsWith(TagPrefix, StringComparison.Ordinal) ? tag : TagPrefix + tag;

    /// <summary>"CPD_H_Outfit_Color_Amber_30" -> "CPD_H_Outfit_Color_Amber"; an unnumbered name stays.</summary>
    private static string StemOf(string name) => ZenPackage.ParseNumber(name).Stem ?? name;

    /// <summary>One FMappedName: the stem's index in the package's own name map, and the number.</summary>
    private static void WriteName(byte[] buffer, int at, ZenPackage pkg, string name)
    {
        var (stem, number) = ZenPackage.ParseNumber(name);
        var index = pkg.Names.IndexOf(stem ?? name);
        if (index < 0) throw new InvalidOperationException($"'{name}' is not in the name map after appending it");
        BitConverter.GetBytes((uint) index).CopyTo(buffer, at);
        BitConverter.GetBytes(stem is null ? 0u : number).CopyTo(buffer, at + 4);
    }

    // ------------------------------------------------------------------ the map's bytes

    /// <summary>One entry as the bytes hold it: the tag, the path it reads as, and where the value's two names sit.</summary>
    private sealed record Entry(string Tag, string Path, int ValueAt);

    /// <summary>
    /// A cooked MapProperty of GameplayTag -> SoftObjectPath, measured on CD_CharDatabank26
    /// (2,288 bytes, 76 entries of 30):
    ///
    ///     int32 keys-to-remove (0)      int32 count
    ///     per entry:
    ///       uint16 0x0300               the KEY is a struct written with unversioned property
    ///                                   serialisation: one fragment saying "one property, last"
    ///       key FName (8)               the tag's name
    ///       package FName (8)           the VALUE is written natively: FTopLevelAssetPath ...
    ///       asset FName (8)
    ///       FString subpath (4, empty)  ... and its sub-path string
    ///
    /// An empty slot is the name "None" in both halves. The first version of this assumed the key
    /// was a bare name, was off by two bytes from the first entry, and refused -- which is what
    /// the every-byte check below is for: a wrong layout fails here rather than writing a face
    /// over a voice.
    /// </summary>
    private static List<Entry>? Parse(ZenPackage pkg, byte[] raw, int start, int length, out string? why)
    {
        why = null;
        var end = start + length;
        if (length < 8 || end > raw.Length) { why = "too short"; return null; }
        var removed = BitConverter.ToInt32(raw, start);
        var count = BitConverter.ToInt32(raw, start + 4);
        if (removed != 0) { why = $"{removed} key(s) to remove -- not a cooked map"; return null; }
        if (count < 0 || count > 4096) { why = $"{count} entries"; return null; }

        var at = start + 8;
        var found = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            if (at + 2 + 8 + 16 + 4 > end) { why = $"entry {i} runs past the property"; return null; }
            var header = BitConverter.ToUInt16(raw, at);
            if (header != 0x0300)
            {
                why = $"entry {i} at +{at - start}: key header 0x{header:X4}, expected 0x0300 " +
                      "(one unversioned property, last)";
                return null;
            }
            var tag = NameAt(pkg, raw, at + 2) ?? "";
            var valueAt = at + 10;
            var package = NameAt(pkg, raw, valueAt) ?? "";
            var asset = NameAt(pkg, raw, valueAt + 8) ?? "";
            var strLen = BitConverter.ToInt32(raw, valueAt + 16);
            var strBytes = strLen >= 0 ? strLen : -strLen * 2;
            if (strBytes < 0 || strBytes > 4096)
            {
                // Said with the bytes, because a layout guess that is wrong here is wrong in a
                // way only the bytes can explain.
                var from = Math.Max(start, at - 40);
                var hex = BitConverter.ToString(raw, from, Math.Min(96, end - from)).Replace("-", " ");
                why = $"entry {i} ({tag}) at +{at - start}: subpath length {strLen}; bytes from +{from - start}: {hex}";
                return null;
            }
            at = valueAt + 20 + strBytes;
            var path = package.Length == 0 || package == "None" ? "" : $"{package}.{asset}";
            found.Add(new Entry(tag, path, valueAt));
        }
        if (at != end) { why = $"{count} entries end at byte {at - start} of {length}"; return null; }
        return found;
    }

    /// <summary>An FMappedName read back as text, or null when it is not a package-local name.</summary>
    private static string? NameAt(ZenPackage pkg, byte[] raw, int at)
    {
        var packed = BitConverter.ToUInt32(raw, at);
        var number = BitConverter.ToUInt32(raw, at + 4);
        if ((packed & 0xC0000000u) != 0) return null;
        var index = (int) (packed & 0x3FFFFFFFu);
        if (index < 0 || index >= pkg.Names.Count) return null;
        return number == 0 ? pkg.Names[index] : $"{pkg.Names[index]}_{number - 1}";
    }
}

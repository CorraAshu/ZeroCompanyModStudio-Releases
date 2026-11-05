using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Objects.Unversioned;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;

namespace UEToolkit.Core;

/// <summary>
/// Give an export a property it does not currently carry.
///
/// Every other editor in this project changes a value that is already there: <see cref="FlagEdit"/>
/// clears a bool it can find, <see cref="GameplayTagEdit"/> rewrites a container it locates BY ITS
/// CONTENTS, <see cref="NameRefEdit"/> re-points a name that appears. All of them fail on the same
/// wall - a property that was never serialised has no bytes to find, and an empty container has no
/// pattern. That wall is why a hero specialisation with no `AllowedSlots` could not be given one.
///
/// A cooked package serialises properties UNVERSIONED: no names, no sizes, just a header saying
/// which of the class's properties are present and then their values in schema order. So adding one
/// means three things at once, and getting any of them wrong corrupts a package that REPLACES a
/// game asset:
///
///   1. the schema index of the new property, from the usmap
///   2. the byte offset where its value belongs, which is after every present property with a
///      lower index -- and finding that means actually deserialising them, since none of them
///      records its own length
///   3. a rebuilt header marking the new index present, with the zero-mask bits of every existing
///      property preserved
///
/// The whole edit goes in as ONE splice from the start of the export to the insertion point, so
/// <see cref="ZenPackage.SpliceExportData"/> corrects the export map exactly once.
///
/// **It verifies itself and refuses rather than returning a maybe.** The result is re-read through
/// CUE4Parse before it is handed back: the new property must be there with the value asked for, and
/// every property that was there before must still read with the value it had. A package that
/// parses differently than intended is returned as an error, not as bytes.
/// </summary>
public static class PropertyAdd
{
    private const int SkipMax = 127;
    private const int ValueMax = 127;

    /// <summary>
    /// Add a gameplay tag container property holding <paramref name="tags"/>.
    ///
    /// The tag container first because it is the shape that matters: nearly every gate in this game
    /// is one, and it is the shape the rest of the builder already understands.
    /// </summary>
    public static byte[] AddTagContainer(IFileProvider provider, byte[] raw, string packageName,
                                         string exportClass, string property,
                                         IReadOnlyList<string> tags, out string? why)
    {
        why = null;
        if (tags.Count == 0) { why = "no tags to add"; return raw; }

        try
        {
            var plan = Plan(provider, raw, packageName, exportClass, property, out why);
            if (plan is null) return raw;

            // The name map first, so the indices the value refers to exist before it does. Any
            // tag already in the map keeps its index; the rest are appended, which moves the name
            // batch and every offset after it -- so this happens BEFORE the offsets are used.
            var pkg = ZenPackage.Read(raw);
            var missing = tags.Where(t => pkg.Names.IndexOf(t) < 0)
                              .Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                foreach (var t in missing) pkg.AppendName(t);
                var grown = pkg.Write();
                // Re-planned against the grown package rather than adjusted: the header moved, and
                // an offset carried across that move is the bug this project has already paid for
                // twice. See GameplayTagEdit.
                return AddTagContainer(provider, grown, packageName, exportClass, property, tags,
                                       out why);
            }

            var indices = tags.Select(t => pkg.Names.IndexOf(t)).ToList();
            var value = SerializeTagContainer(indices);

            var replacement = new byte[plan.NewHeader.Length +
                                       (plan.InsertAt - plan.HeaderEnd) + value.Length];
            plan.NewHeader.CopyTo(replacement, 0);
            Array.Copy(raw, plan.HeaderEnd, replacement, plan.NewHeader.Length,
                       plan.InsertAt - plan.HeaderEnd);
            value.CopyTo(replacement, plan.NewHeader.Length + (plan.InsertAt - plan.HeaderEnd));

            var written = ZenPackage.SpliceExportData(raw, plan.ExportStart,
                                                      plan.InsertAt - plan.ExportStart, replacement);

            var complaint = Verify(provider, written, packageName, exportClass, property, tags,
                                   plan.Before);
            if (complaint is not null)
            { why = "the edit did not read back correctly: " + complaint; return raw; }

            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Add a single SCALAR property -- a float, an int, a bool or an enum -- that the export does
    /// not currently serialise, OR raise one it serialises as a zero.
    ///
    /// The header work is <see cref="AddTagContainer"/>'s and is not repeated: planning an
    /// insertion is generic, because an unversioned header describes WHICH schema slots are
    /// present and says nothing about what any of them contains. Only the value bytes differ, and
    /// for these four types the value is between one and four bytes with no name map involved --
    /// which is why this is short and why it was worth doing rather than hand-patching one widget.
    ///
    /// **A byte-backed enum is ONE BYTE, the enumerator's index.** Writing an FName there is the
    /// mistake that already cost this project an evening (see the weapon icon), so an enum value
    /// is given as a NUMBER here and the caller is the one that has to know which.
    ///
    /// A bool serialises as one byte, and only when it is TRUE: unversioned serialisation records
    /// a false as "present and zero" in the header's zero mask with no bytes at all, which
    /// <see cref="Plan"/> cannot splice into. So false is refused rather than written wrong.
    /// </summary>
    public static byte[] AddScalar(IFileProvider provider, byte[] raw, string packageName,
                                   string exportClass, string property, double value,
                                   out string? why)
    {
        why = null;
        try
        {
            // A DOTTED PATH IS A MEMBER OF A STRUCT -- UIData.SortingPriority, which decides where
            // the ability wheel puts an ability and which no enemy ability sets. Plan knows only
            // the export's own properties, so it goes to the struct writer instead.
            if (property.Contains('.'))
                return AddStructScalar(provider, raw, packageName, exportClass, property, value, out why);

            var plan = Plan(provider, raw, packageName, exportClass, property, out why);
            if (plan is null) return raw;

            if (ScalarBytes(plan.Type, value, property, out why) is not { } bytes) return raw;

            var replacement = new byte[plan.NewHeader.Length +
                                       (plan.InsertAt - plan.HeaderEnd) + bytes.Length];
            plan.NewHeader.CopyTo(replacement, 0);
            Array.Copy(raw, plan.HeaderEnd, replacement, plan.NewHeader.Length,
                       plan.InsertAt - plan.HeaderEnd);
            bytes.CopyTo(replacement, plan.NewHeader.Length + (plan.InsertAt - plan.HeaderEnd));

            var written = ZenPackage.SpliceExportData(raw, plan.ExportStart,
                                                      plan.InsertAt - plan.ExportStart, replacement);

            var complaint = VerifyScalar(provider, written, packageName, exportClass, property,
                                         value, plan.Before);
            if (complaint is not null)
            { why = "the edit did not read back correctly: " + complaint; return raw; }

            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// One number as a cooked property of this type writes it, or null with the reason.
    ///
    /// A bool serialises as one byte, and only when it is TRUE: unversioned serialisation records
    /// a false as "present and zero" in the header's zero mask with no bytes at all, so false is
    /// refused rather than written wrong.
    /// </summary>
    private static byte[]? ScalarBytes(string type, double value, string property, out string? why)
    {
        why = null;
        switch (type)
        {
            case "FloatProperty":  return BitConverter.GetBytes((float) value);
            case "DoubleProperty": return BitConverter.GetBytes(value);
            case "IntProperty":    return BitConverter.GetBytes((int) value);
            case "BoolProperty":
                if (value != 0) return new byte[] { 1 };
                why = $"'{property}' false is serialised as no bytes at all, so it cannot be added";
                return null;
            case "ByteProperty":
            case "EnumProperty":
                if (value is >= 0 and <= 255) return new byte[] { (byte) value };
                why = $"'{property}' is a byte-backed enum and {value} is not a byte";
                return null;
            default:
                why = $"'{property}' is a {type}, which this writes no value for";
                return null;
        }
    }

    /// <summary>
    /// A number added as a member of a STRUCT: the member's type from the struct's own schema,
    /// its bytes from <see cref="ScalarBytes"/>, and the write and the read-back from
    /// <see cref="AddStructMember"/>, which refuses unless the member holds exactly those bytes and
    /// everything else in the export is byte-identical.
    /// </summary>
    private static byte[] AddStructScalar(IFileProvider provider, byte[] raw, string packageName,
                                          string exportClass, string path, double value, out string? why)
    {
        why = null;
        var parts = SplitPath(path);
        if (parts.Length < 2)
        { why = $"'{path}' names no struct to add it to -- give the path to it, e.g. UIData.SortingPriority"; return raw; }
        var structPath = string.Join('.', parts[..^1]);
        var member = parts[^1];

        var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
        if (container is null) { why = $"no mounted container holds {packageName}"; return raw; }
        var pkg = Open(provider, raw, packageName, container);
        if (FindExport(pkg.ExportsLazy.Select(x => x.Value).ToArray(), exportClass) is not { } export)
        { why = $"no export matching '{exportClass}'"; return raw; }
        if (export.Class?.Object?.Value is not UStruct struc || SchemaFor(pkg, struc) is not { } schema)
        { why = $"the '{exportClass}' export has no property schema"; return raw; }
        if (SchemaAt(pkg, schema, structPath, out why) is not { } structSchema) return raw;

        string? type = null;
        var total = structSchema.CountProperties(true);
        for (var i = 0; i < total && type is null; i++)
            if (structSchema.TryGetValue(i, out var info) && string.Equals(info.Name, member, StringComparison.Ordinal))
                type = info.MappingType.Type;
        if (type is null) { why = $"'{member}' is not a member of {structSchema.Name}"; return raw; }

        if (ScalarBytes(type, value, path, out why) is not { } bytes) return raw;
        var written = AddStructMember(provider, raw, packageName, exportClass, structPath, member, bytes, out why);
        return why is null ? written : raw;
    }

    /// <summary>
    /// Read the added scalar back and check it, along with every property that was already there.
    ///
    /// The second half is the one that matters. A wrong header does not usually lose the property
    /// that was added -- it shifts the ones AFTER it onto neighbouring schema slots, so the new
    /// value reads perfectly while a sibling quietly becomes something else.
    /// </summary>
    /// <summary>
    /// The NUMBER behind an enum property's name, or null when it cannot be established.
    ///
    /// CUE4Parse turns the stored byte into a name on the way in -- `IndexToEnum` looks the index
    /// up in the enum's own (name, value) table -- so checking an added enum means going back the
    /// other way. Through that same table when it is serialised with the property, which is the
    /// exact inverse and survives an enum whose values are not 0,1,2,3.
    ///
    /// The `Name::Index` form is the SECOND test, not the first, because it is what IndexToEnum
    /// falls back to when the enum is NOT serialised -- so matching it is reading CUE4Parse's own
    /// fallback rather than guessing at a naming convention. Null rather than a guess when neither
    /// applies: VerifyScalar's job is to refuse what it cannot confirm.
    /// </summary>
    private static long? EnumIndexOf(IFileProvider provider, FPropertyTag prop, string text)
    {
        // THE TABLE IS THE ANSWER WHEN THERE IS ONE, and a miss against it is a REFUSAL rather
        // than a reason to try harder. IndexToEnum returns "Name::7" for an index the enum does
        // not define, so falling through to parse that tail would confirm any byte at all: a
        // measured 99 was written into a four-value enum and read back "correct", because 99 came
        // out the other side. Confirming the byte survived is not confirming it means anything.
        if (prop.TagData?.Enum is { Names: { Length: > 0 } names })
        {
            foreach (var (name, value) in names)
                if (string.Equals(name.Text, text, StringComparison.Ordinal)) return value;
            return null;
        }

        // A NATIVE ENUM HAS ITS TABLE IN THE MAPPINGS. Only a Blueprint enum travels with the
        // property; an engine one -- EGameplayEffectTriggerType, which ends Force Grip's hold at
        // the gripper's next turn -- reads back as "EGameplayEffectTriggerType::SourceTeamTurnStart",
        // has no digits to parse, and so refused its own correct write of 3. The same rule as the
        // table above: a name the mappings do not list is a refusal.
        if (prop.TagData?.EnumName is { Length: > 0 } enumName &&
            provider.MappingsContainer?.MappingsForGame?.Enums is { } enums &&
            enums.TryGetValue(enumName, out var listed))
        {
            var entry = text.Contains("::", StringComparison.Ordinal) ? text[(text.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : text;
            foreach (var (value, name) in listed)
                if (string.Equals(name, entry, StringComparison.Ordinal) ||
                    string.Equals(name, text, StringComparison.Ordinal)) return value;
            return null;
        }

        // No table serialised with the property, so "Name::N" is all CUE4Parse itself could
        // produce and reading it back is the most that can be checked.
        var cut = text.LastIndexOf("::", StringComparison.Ordinal);
        var tail = cut >= 0 ? text[(cut + 2)..] : text;
        return long.TryParse(tail, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// The export a caller named, BY ITS OWN NAME FIRST and then by its class.
    ///
    /// The same two steps <see cref="ClassSchema"/> takes to CHOOSE the export, and that is the
    /// whole point of it being one function. A verifier that resolves the name differently from
    /// the planner is not checking the write -- it is checking a different export, and when no
    /// export of that class exists it reports the package as corrupt instead.
    ///
    /// That is what `AddTagContainer` did on `CPD_WeaponSpec_Blaster_Rifle` for two days: planned
    /// by export name, verified by class, and refused its own correct write as "the export is
    /// gone". The write had been right the whole time. Two later verifiers had each copied the
    /// two-step version by hand, which is why only the oldest was wrong -- so it lives here now
    /// rather than in three places that can drift apart again.
    /// </summary>
    private static UObject? FindExport(UObject[] exports, string exportClass) =>
        exports.FirstOrDefault(e =>
                   e is not null &&
                   string.Equals(e.Name, exportClass, StringComparison.OrdinalIgnoreCase))
     ?? exports.FirstOrDefault(e =>
                   e is not null &&
                   (string.Equals(e.Class?.Name.Text, exportClass, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.ExportType, exportClass, StringComparison.OrdinalIgnoreCase)));

    private static string? VerifyScalar(IFileProvider provider, byte[] written, string packageName,
                                        string exportClass, string property, double value,
                                        Dictionary<string, string> before)
    {
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            var pkg = Open(provider, written, packageName, container);
            var export = FindExport(pkg.ExportsLazy.Select(x => x.Value).ToArray(), exportClass);
            if (export is null) return $"the '{exportClass}' export is gone";

            var prop = export.Properties.FirstOrDefault(x => x.Name.Text == property);
            if (prop is null) return $"'{property}' is still not there";

            var read = prop.Tag?.GenericValue;
            var ok = read switch
            {
                float f  => Math.Abs(f - value) < 0.01,
                double d => Math.Abs(d - value) < 0.01,
                int i    => i == (int) value,
                bool b   => b == (value != 0),
                byte y   => y == (byte) value,
                // AN ENUM COMES BACK AS ITS NAME, NOT ITS NUMBER, and this is what made AddScalar
                // refuse its own correct write. EnumProperty is an FPropertyTagType<FName>: the
                // byte is read and immediately turned into "ENUM_Blaster_Color::NewEnumerator2"
                // through the enum's own table, so the fallback below compared that string against
                // "2" and got false. The value written was right and the package was thrown away.
                FName name => EnumIndexOf(provider, prop, name.Text) is { } index && index == (long) value,
                _        => read?.ToString() is { } t &&
                            (t == value.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                             t == ((int) value).ToString()),
            };
            // AN ENUM SAYS WHICH VALUES EXIST, because the author picked a number and the only
            // useful answer is which numbers there were. "reads back as ENUM_Blaster_Color::99,
            // not 99" is true and reads like a contradiction.
            if (!ok && read is FName && prop.TagData?.Enum is { Names: { Length: > 0 } choices })
                return $"'{property}' has no enumerator {value}. {prop.TagData.EnumName} is: " +
                       string.Join(", ", choices.Select(c => $"{c.Item2} {c.Item1.Text.Split("::")[^1]}"));
            if (!ok && read is FName && prop.TagData?.EnumName is { Length: > 0 } native &&
                provider.MappingsContainer?.MappingsForGame?.Enums is { } enums &&
                enums.TryGetValue(native, out var listed))
                return $"'{property}' has no enumerator {value}. {native} is: " +
                       string.Join(", ", listed.Select(c => $"{c.Key} {c.Value}"));

            if (!ok) return $"'{property}' reads back as {read ?? "(null)"}, not {value}";

            foreach (var (name, was) in before)
            {
                var kept = export.Properties.FirstOrDefault(x => x.Name.Text == name);
                if (kept is null) return $"'{name}' was lost";
                var now = kept.Tag?.GenericValue?.ToString() ?? "(null)";
                if (now != was) return $"'{name}' changed from {was} to {now}";
            }
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>
    /// Give an export a CLASS-VALUED property it does not have, pointing at another asset.
    ///
    /// The gap Daelen hit, reported 2026-09-09: "'Add a property it does not have' can
    /// successfully add inherited GameplayAbility effects like CooldownGameplayEffectClass to
    /// GA_RocketStrike_T#, but there is no way to assign a BlueprintGC value to it." Correct on
    /// both counts. <see cref="AddScalar"/> writes numbers, and a ClassProperty's value is not a
    /// number -- it is an FPackageIndex, four bytes naming a row of the import map.
    ///
    /// **THE IMPORT MUST EXIST FIRST, and this does not add it.** An FPackageIndex into a row that
    /// is not there resolves to nothing, which is the same silence as no property at all. The
    /// caller adds the import -- <see cref="AddImport"/>, whose return value is exactly the
    /// <paramref name="fpackageIndex"/> wanted here -- AND appends the package's id to the
    /// container store entry's ImportedPackages, because the runtime resolves through that list
    /// rather than through the package alone. ModBuild's recipe path is the worked example.
    ///
    /// Ordering matters twice over: AddImport rewrites the import map and the imported-package
    /// names trailer, moving everything after them, so this must be PLANNED against the bytes
    /// AddImport returned and never against the ones handed to it.
    ///
    /// Read back before it is returned, and the read-back is the point rather than a formality: an
    /// FPackageIndex that lands on the wrong import row reads as a perfectly valid reference to
    /// the wrong asset, and nothing downstream would ever say so.
    /// </summary>
    public static byte[] AddClass(IFileProvider provider, byte[] raw, string packageName,
                                  string exportClass, string property, int fpackageIndex,
                                  string expectedLeaf, out string? why)
    {
        why = null;
        try
        {
            if (fpackageIndex == 0)
            { why = $"'{property}' was given a null reference to write"; return raw; }

            var plan = Plan(provider, raw, packageName, exportClass, property, out why);
            if (plan is null) return raw;

            // ObjectProperty and ClassProperty serialise identically -- an FPackageIndex. A
            // SoftObjectProperty does NOT, and neither does an FInstancedStruct, so they are
            // refused by name rather than written wrongly and discovered in game.
            if (plan.Type is not ("ClassProperty" or "ObjectProperty"))
            { why = $"'{property}' is a {plan.Type}, not a class or object reference"; return raw; }

            var bytes = BitConverter.GetBytes(fpackageIndex);
            var replacement = new byte[plan.NewHeader.Length +
                                       (plan.InsertAt - plan.HeaderEnd) + bytes.Length];
            plan.NewHeader.CopyTo(replacement, 0);
            Array.Copy(raw, plan.HeaderEnd, replacement, plan.NewHeader.Length,
                       plan.InsertAt - plan.HeaderEnd);
            bytes.CopyTo(replacement, plan.NewHeader.Length + (plan.InsertAt - plan.HeaderEnd));

            var written = ZenPackage.SpliceExportData(raw, plan.ExportStart,
                                                      plan.InsertAt - plan.ExportStart, replacement);

            var complaint = VerifyClass(provider, written, packageName, exportClass, property,
                                        expectedLeaf, fpackageIndex, plan.Before);
            if (complaint is not null)
            { why = "the edit did not read back correctly: " + complaint; return raw; }

            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Read the added reference back and check it landed on the intended import, along with every
    /// property that was already there.
    ///
    /// **BY INDEX FIRST, and by name only when the name is available.** A brand-new import cannot
    /// be RESOLVED here: the reference is a row in the package's import map, and resolving it
    /// needs the container store entry's ImportedPackages -- which the build writes at pak time,
    /// long after this runs. So the read comes back as the bare number, and an early version of
    /// this refused its own correct write with "reads back as -24, which does not name
    /// GE_Cooldown_Sabotage_T2_C". Measured on GA_RocketStrike_T3, 2026-09-09.
    ///
    /// The index is still worth checking and is what AddWeightedRecipe checks for the same reason:
    /// it proves the four bytes went in at the right offset, which is the failure that matters. A
    /// wrong offset shifts every property after it, and that is caught by the `before` sweep
    /// below rather than by the value itself.
    ///
    /// When the reference DOES resolve -- an import the package already had -- the name is checked
    /// too, and matched on the leaf: the caller holds a package name and what comes back is the
    /// class inside it, `GE_Cooldown_Sabotage_T2_C` for `/Game/.../GE_Cooldown_Sabotage_T2`.
    /// </summary>
    private static string? VerifyClass(IFileProvider provider, byte[] written, string packageName,
                                       string exportClass, string property, string expectedLeaf,
                                       int fpackageIndex, Dictionary<string, string> before)
    {
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            var pkg = Open(provider, written, packageName, container);
            var export = FindExport(pkg.ExportsLazy.Select(x => x.Value).ToArray(), exportClass);
            if (export is null) return $"the '{exportClass}' export is gone";

            var prop = export.Properties.FirstOrDefault(x => x.Name.Text == property);
            if (prop is null) return $"'{property}' is still not there";

            var read = prop.Tag?.GenericValue?.ToString() ?? "";
            if (read.Length == 0) return $"'{property}' reads back as nothing";

            // The resolved name when there is one, the raw index when there is not. Either is a
            // pass; anything else is the four bytes having gone somewhere unintended.
            var byName = expectedLeaf.Length > 0 &&
                         read.Contains(expectedLeaf, StringComparison.OrdinalIgnoreCase);
            var byIndex = read.Trim() == fpackageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!byName && !byIndex)
                return $"'{property}' reads back as {read}, which is neither {expectedLeaf} nor " +
                       $"the import index {fpackageIndex} it was written with";

            foreach (var (name, was) in before)
            {
                var kept = export.Properties.FirstOrDefault(x => x.Name.Text == name);
                if (kept is null) return $"'{name}' was lost";
                var now = kept.Tag?.GenericValue?.ToString() ?? "(null)";
                if (now != was) return $"'{name}' changed from {was} to {now}";
            }
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>
    /// Point a UI fragment's image at a texture, the way the DC-17m's armoury part is built.
    ///
    /// An FBitReactorImageReference has a SELECTOR, ImageReferenceType, and two halves: a bank tag
    /// and a soft brush. The weapons the game ships use the tag; the DC-17m uses the brush, with
    /// the selector set to SoftBrush, and the armoury draws its texture directly. A mod's own bank
    /// under a minted tag was measured NOT to be read (the game drew its fallback picture for
    /// every such tag), so a copy that wants a picture of its own gets the DC-17m's shape: the
    /// whole property value is rewritten as {selector = SoftBrush, brush = {soft path, Texture2D,
    /// FullColor, size}}. No hard ResourceObject: that is an import, and the brush's soft path is
    /// what "Weak" is for.
    ///
    /// Written with the same care as <see cref="AddTagContainer"/>: names appended first, the
    /// value spliced whole, and the result re-read -- the reference must come back naming exactly
    /// the texture asked for, or the package is handed back untouched with the reason.
    /// </summary>
    public static byte[] SetImageToTexture(IFileProvider provider, byte[] raw, string packageName,
                                           string exportClass, string property,
                                           string texturePackage, string textureAsset,
                                           float width, float height, out string? why,
                                           int? hardIndex = null)
    {
        why = null;
        try
        {
            var needed = new[] { texturePackage, textureAsset };
            var pkg = ZenPackage.Read(raw);
            var missing = needed.Where(n => pkg.Names.IndexOf(n) < 0).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                // Appended, then started over against the grown package: the name batch moved
                // and every offset after it with it. Same rule as AddTagContainer.
                foreach (var n in missing) pkg.AppendName(n);
                return SetImageToTexture(provider, pkg.Write(), packageName, exportClass, property,
                                         texturePackage, textureAsset, width, height, out why, hardIndex);
            }

            if (!Locate(provider, raw, packageName, exportClass, property, out var start, out var length, out why))
                return raw;

            // Schema order is unversioned order, from the mappings (--schema prints it):
            //   BitReactorImageReference  0 ImageReferenceType  1 ImageBankTag  2 SoftBrush
            //   BitReactorSoftBrush       0 WeakResourceObject  1 BrushType  6 ImageType  7 ImageSize
            // Byte for byte the DC-17m's own SmallImage (--imagetest prints it): an enum backed by
            // a byte is ONE BYTE, its enumerator's index -- SoftBrush = 1, Texture2D = 1,
            // FullColor = 1 -- not an FName; a soft path is two FNames and an empty sub-path
            // string; the slate size is two floats. The first version wrote the enums as names,
            // which is what a versioned package does, and the reader stopped dead at the third
            // byte.
            //
            // SLOT 9 IS THE ONE THAT DRAWS. Measured in game 2026-09-02 with a two-row probe: a
            // brush carrying slots 0/1/6/7 alone shows the Armoury's fallback picture even when
            // its soft path names a texture the GAME ships, and the one game part that draws a
            // texture carries the same four slots plus ResourceObject -- an FPackageIndex, four
            // bytes, negative for an import. The soft path is not read at all.
            var slots = new List<(int, bool)> { (0, false), (1, false), (6, false), (7, false) };
            if (hardIndex is not null) slots.Add((9, false));

            var brush = new List<byte>();
            brush.AddRange(BuildHeader(slots));
            brush.AddRange(NameBytes(pkg.Names.IndexOf(texturePackage)));
            brush.AddRange(NameBytes(pkg.Names.IndexOf(textureAsset)));
            brush.AddRange(BitConverter.GetBytes(0));
            brush.Add(1);   // EBitReactorSoftBrushType::Texture2D
            brush.Add(1);   // ESlateBrushImageType::FullColor
            brush.AddRange(BitConverter.GetBytes(width));
            brush.AddRange(BitConverter.GetBytes(height));
            if (hardIndex is not null) brush.AddRange(BitConverter.GetBytes(hardIndex.Value));

            var value = new List<byte>();
            value.AddRange(BuildHeader(new List<(int, bool)> { (0, false), (2, false) }));
            value.Add(1);   // EBitReactorImageReferenceType::SoftBrush
            value.AddRange(brush);

            var written = ZenPackage.SpliceExportData(raw, start, length, value.ToArray());
            LastAttempt = written;

            // Read back, or refuse.
            var fragment = PristineExports(provider, written, packageName).FirstOrDefault(e =>
                (e.ExportType ?? "").StartsWith(exportClass, StringComparison.OrdinalIgnoreCase));
            var got = fragment is null ? (Path: (string?) null, BankTag: (string?) null)
                                       : PartDetails.ImageOf(fragment.Properties, property);
            var want = texturePackage + "." + textureAsset;
            if (!string.Equals(got.Path, want, StringComparison.Ordinal))
            {
                why = $"{property} reads back as '{got.Path ?? (got.BankTag is null ? "(nothing)" : "bank tag " + got.BankTag)}', not {want}";
                return raw;
            }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Give a package a hard import of one export in another, and say which FPackageIndex names it.
    ///
    /// The package half of what a brush's ResourceObject needs; the store entry's ImportedPackages
    /// is the other half and belongs to the caller, because it lives in the CONTAINER rather than
    /// in the package -- see <see cref="ZenPackage.AddPackageImport"/>.
    ///
    /// The hash is the TEMPLATE texture's, not one computed here: a package this tool authors is a
    /// clone with its names swapped, and nothing recomputes the stored PublicExportHash. That is
    /// why an import redirected at a clone resolves, and it is why an import ADDED for one does.
    /// </summary>
    public static byte[] AddImport(byte[] raw, int importedPackageIndex, ulong publicExportHash,
                                   string importedPackageName, out int fpackageIndex, out string? why)
    {
        why = null;
        fpackageIndex = 0;
        try
        {
            var pkg = ZenPackage.Read(raw);
            fpackageIndex = pkg.AddPackageImport(importedPackageIndex, publicExportHash, importedPackageName);
            return pkg.Write();
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>The public export hash a package stores for its first export, or 0.</summary>
    public static ulong PublicExportHashOf(IFileProvider provider, string packageName)
    {
        var src = AssetCopy.PristineRead(provider, packageName, out _);
        var raw = src?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null) return 0;
        try { return ZenPackage.Read(raw).PublicExportHashes.FirstOrDefault(); }
        catch { return 0; }
    }

    /// <summary>
    /// EVERY export's public export hash, in export order, or empty when the package will not read.
    ///
    /// The whole run rather than the first, because an importing package names SEVERAL exports of
    /// one package -- a gear kit reaches six of a weapon actor's nine -- and moving that import to
    /// a different asset means matching each one. Position is what two cooked siblings share: both
    /// weapon actors are the same Blueprint parent, so export N is the same role in each.
    /// </summary>
    public static ulong[] PublicExportHashesOf(IFileProvider provider, string packageName)
    {
        var src = AssetCopy.PristineRead(provider, packageName, out _);
        var raw = src?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null) return [];
        try { return ZenPackage.Read(raw).PublicExportHashes; }
        catch { return []; }
    }

    /// <summary>The object name of each export of a package, in the order <see cref="PublicExportHashesOf"/> lists their hashes.</summary>
    public static string[] ExportNamesOf(IFileProvider provider, string packageName)
    {
        var src = AssetCopy.PristineRead(provider, packageName, out _);
        var raw = src?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null) return [];
        try { return ZenPackage.Read(raw).ExportNames; }
        catch { return []; }
    }

    /// <summary>The map a campaign arc keeps its scripted turns in, and the array of recipes each turn adds.</summary>
    public const string TurnsMapProperty = "TurnsMap";
    public const string TurnRecipesProperty = "Recipes";

    /// <summary>The recipe entry both the table and the arc use: Status, RecipeClass and bAutoStart, present; all else default.</summary>
    private static byte[] RecipePayloadBytes(int recipePackageIndex, byte status, bool autoStart)
    {
        // A FALSE bool is header-only -- a zero slot, no bytes -- but a TRUE one is written as one
        // byte, the way FBoolProperty::SerializeItem writes it. Measured the hard way: with no
        // byte the entry read back with bAutoStart False, off the byte that followed.
        var present = new List<(int, bool)> { (1, status == 0), (3, false) };
        if (autoStart) present.Add((7, false));
        var bytes = new List<byte>();
        bytes.AddRange(BuildHeader(present));
        if (status != 0) bytes.Add(status);
        bytes.AddRange(BitConverter.GetBytes(recipePackageIndex));
        if (autoStart) bytes.Add(1);
        return bytes.ToArray();
    }

    /// <summary>
    /// Add a recipe to EVERY scripted turn of a campaign arc, so it is granted at the next turn
    /// the arc processes wherever a campaign stands.
    ///
    /// Why: a draw from a weighted table is a draw -- one cycle of the author's campaign drew one
    /// of the game's own entries past three of ours -- and the arc's turn entries are not drawn,
    /// they are applied: the game's own silent recipes (a crisis initialiser with bAutoStart) ride
    /// there. An arc is a data asset: TurnsMap, turn number -> a struct holding Objectives,
    /// Missions, RecipeTables and Recipes[]. The map has no property path a Locate can descend,
    /// so this walks it the way <see cref="Plan"/> walks an export: key, then the value's
    /// unversioned header and its present values in schema order, noting where each turn's
    /// Recipes array lies. The splices go in from the LAST turn back, so each earlier offset is
    /// still true when it is used. A turn whose Recipes array is absent is left alone and counted.
    /// </summary>
    public static byte[] AddArcRecipe(IFileProvider provider, byte[] raw, string packageName,
                                      int recipePackageIndex, byte status, bool autoStart,
                                      out int turnsTouched, out string? why)
    {
        turnsTouched = 0;
        why = null;
        try
        {
            if (recipePackageIndex >= 0) { why = "the recipe must be an import -- a negative FPackageIndex"; return raw; }
            if (!Locate(provider, raw, packageName, "BrunoCampaignArc", TurnsMapProperty,
                        out var start, out var length, out why))
                return raw;

            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null) { why = "no mounted container holds the arc, so its schema cannot be resolved"; return raw; }
            var pkg = Open(provider, raw, packageName, container);
            if (pkg.Mappings is not { } mappings || !mappings.Types.TryGetValue("BrunoCampaignArc", out var arcSchema) || arcSchema is null)
            { why = "BrunoCampaignArc is not in the mappings"; return raw; }
            string? valueStruct = null;
            for (var i = 0; i < arcSchema.CountProperties(true); i++)
                if (arcSchema.TryGetValue(i, out var p) && p.Name == TurnsMapProperty)
                    valueStruct = p.MappingType.ValueType?.StructType;
            if (valueStruct is null || !mappings.Types.TryGetValue(valueStruct, out var turnSchema) || turnSchema is null)
            { why = $"the turn struct ({valueStruct ?? "unnamed"}) is not in the mappings"; return raw; }
            var recipesSlot = -1;
            for (var i = 0; i < turnSchema.CountProperties(true); i++)
                if (turnSchema.TryGetValue(i, out var p) && p.Name == TurnRecipesProperty) recipesSlot = i;
            if (recipesSlot < 0) { why = $"{valueStruct} has no {TurnRecipesProperty}"; return raw; }

            var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg) { Position = start };
            var removed = ar.Read<int>();
            if (removed != 0) { why = $"{TurnsMapProperty} removes {removed} key(s), a shape this does not walk"; return raw; }
            var count = ar.Read<int>();
            if (count < 0 || count > 10_000) { why = $"{TurnsMapProperty} declares {count} entries"; return raw; }

            var arrays = new List<(int Key, int Start, int Length)>();
            for (var e = 0; e < count; e++)
            {
                var key = ar.Read<int>();
                var header = new FUnversionedHeader(ar);
                if (!header.HasValues) continue;
                using var it = new FIterator(header);
                do
                {
                    var (val, isNonZero) = it.Current;
                    if (!isNonZero) continue;
                    if (!turnSchema.TryGetValue(val, out var info))
                    { why = $"{valueStruct}: property {val} is not in the mappings, so its size is unknown"; return raw; }
                    var at = (int) ar.Position;
                    _ = new FPropertyTag(ar, info, ReadType.NORMAL);
                    if (val == recipesSlot) arrays.Add((key, at, (int) ar.Position - at));
                } while (it.MoveNext());
            }
            if ((int) ar.Position != start + length)
            { why = $"the walk ended {start + length - ar.Position} byte(s) short of the map's end"; return raw; }
            if (arrays.Count == 0) { why = "no turn carries a Recipes array to grow"; return raw; }

            var element = RecipePayloadBytes(recipePackageIndex, status, autoStart);
            var written = raw;
            foreach (var (_, at, len) in arrays.OrderByDescending(a => a.Start))
            {
                var n = BitConverter.ToInt32(written, at);
                var value = new List<byte>();
                value.AddRange(BitConverter.GetBytes(n + 1));
                value.AddRange(written.AsSpan(at + 4, len - 4).ToArray());
                value.AddRange(element);
                written = ZenPackage.SpliceExportData(written, at, len, value.ToArray());
            }

            // Read back, or refuse: every touched turn's last recipe must be the one asked for.
            var exports = PristineExports(provider, written, packageName);
            var arc = exports.FirstOrDefault(e => e is not null && e.ExportType == "BrunoCampaignArc");
            if (arc is null) { why = "no BrunoCampaignArc export reads back"; return raw; }
            if (PartDetails.Value(arc.Properties, TurnsMapProperty) is not UScriptMap map)
            { why = $"{TurnsMapProperty} does not read back as a map"; return raw; }
            var confirmed = 0;
            foreach (var kv in map.Properties)
            {
                if (PartDetails.Unwrap(kv.Value?.GenericValue) is not FStructFallback turn) continue;
                if (PartDetails.Value(turn.Properties, TurnRecipesProperty) is not UScriptArray arr || arr.Properties.Count == 0) continue;
                if (PartDetails.Unwrap(arr.Properties[^1].GenericValue) is not FStructFallback last) continue;
                var got = PartDetails.Unwrap(PartDetails.Value(last.Properties, "RecipeClass")) is FPackageIndex pi ? pi.Index : 0;
                var auto = PartDetails.Value(last.Properties, "bAutoStart") is bool b && b;
                if (got == recipePackageIndex && auto == autoStart) confirmed++;
            }
            if (confirmed != arrays.Count)
            { why = $"{confirmed} of {arrays.Count} turn(s) read back with the recipe last"; return raw; }
            turnsTouched = confirmed;
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>The array a campaign turn places missions in, with no table and no roll.</summary>
    public const string TurnMissionsProperty = "Missions";

    /// <summary>
    /// APPEND a mission to ONE campaign turn -- guaranteed placement, displacing nothing.
    ///
    /// The last gap in the mission feature, and the author's words for why it is the one that was
    /// wanted: "Wish there was a way to just make it appear without doing something like that".
    /// Every other route trades something. A weighted row is a ROLL and may lose. Taking a turn
    /// slot re-points a shipped mission and costs the player that mission. Editing in place is
    /// instant but replaces an offer. This one waits for a turn the author CHOOSES and costs
    /// nothing at all: the turn places the game's missions and the author's, side by side.
    ///
    /// ONE TURN, not every turn, which is the difference from <see cref="AddArcRecipe"/>. A recipe
    /// on every scripted turn is how a recruit gets granted wherever a campaign stands; a mission
    /// on every turn would put the same contract on the map over and over.
    ///
    /// The element is the simplest one in this file: `BrunoTurnSettings.Missions` is
    /// `ArrayProperty inner=ObjectProperty`, so an entry is a bare four-byte FPackageIndex with no
    /// unversioned header of its own -- where a WeightedMission needs a header and a weight, and a
    /// RecipePayload needs three fields.
    ///
    /// The walk is <see cref="AddArcRecipe"/>'s, and has to be: a TMap has no property path a
    /// <see cref="Locate"/> can descend, so the map is walked key by key, reading each turn's
    /// unversioned header and its present values in schema order until the wanted array's byte
    /// range is known.
    /// </summary>
    public static byte[] AddArcMission(IFileProvider provider, byte[] raw, string packageName,
                                       int missionPackageIndex, int turnKey, out string? why)
    {
        why = null;
        try
        {
            if (missionPackageIndex >= 0)
            { why = "the mission must be an import -- a negative FPackageIndex"; return raw; }
            if (!Locate(provider, raw, packageName, "BrunoCampaignArc", TurnsMapProperty,
                        out var start, out var length, out why))
                return raw;

            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null)
            { why = "no mounted container holds the arc, so its schema cannot be resolved"; return raw; }
            var pkg = Open(provider, raw, packageName, container);
            if (pkg.Mappings is not { } mappings ||
                !mappings.Types.TryGetValue("BrunoCampaignArc", out var arcSchema) || arcSchema is null)
            { why = "BrunoCampaignArc is not in the mappings"; return raw; }

            string? valueStruct = null;
            for (var i = 0; i < arcSchema.CountProperties(true); i++)
                if (arcSchema.TryGetValue(i, out var p) && p.Name == TurnsMapProperty)
                    valueStruct = p.MappingType.ValueType?.StructType;
            if (valueStruct is null || !mappings.Types.TryGetValue(valueStruct, out var turnSchema) || turnSchema is null)
            { why = $"the turn struct ({valueStruct ?? "unnamed"}) is not in the mappings"; return raw; }

            var missionsSlot = -1;
            for (var i = 0; i < turnSchema.CountProperties(true); i++)
                if (turnSchema.TryGetValue(i, out var p) && p.Name == TurnMissionsProperty) missionsSlot = i;
            if (missionsSlot < 0) { why = $"{valueStruct} has no {TurnMissionsProperty}"; return raw; }

            var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg) { Position = start };
            var removed = ar.Read<int>();
            if (removed != 0)
            { why = $"{TurnsMapProperty} removes {removed} key(s), a shape this does not walk"; return raw; }
            var count = ar.Read<int>();
            if (count < 0 || count > 10_000) { why = $"{TurnsMapProperty} declares {count} entries"; return raw; }

            var at = -1;
            var len = 0;
            var turnsSeen = new List<int>();
            for (var e = 0; e < count; e++)
            {
                var key = ar.Read<int>();
                turnsSeen.Add(key);
                var header = new FUnversionedHeader(ar);
                if (!header.HasValues) continue;
                using var it = new FIterator(header);
                do
                {
                    var (val, isNonZero) = it.Current;
                    if (!isNonZero) continue;
                    if (!turnSchema.TryGetValue(val, out var info))
                    { why = $"{valueStruct}: property {val} is not in the mappings, so its size is unknown"; return raw; }
                    var atHere = (int) ar.Position;
                    _ = new FPropertyTag(ar, info, ReadType.NORMAL);
                    if (val == missionsSlot && key == turnKey)
                    { at = atHere; len = (int) ar.Position - atHere; }
                } while (it.MoveNext());
            }
            if ((int) ar.Position != start + length)
            { why = $"the walk ended {start + length - ar.Position} byte(s) short of the map's end"; return raw; }

            if (at < 0)
            {
                // ABSENT IS NOT EMPTY. A turn that declares no Missions array at all has no bytes
                // to grow, and saying "turn 4" when the arc has turns 0..3 is the likelier mistake
                // -- so both are named rather than reported as one failure.
                why = turnsSeen.Contains(turnKey)
                    ? $"turn {turnKey} carries no {TurnMissionsProperty} array to grow"
                    : $"this arc has no turn {turnKey} -- it has {string.Join(", ", turnsSeen.OrderBy(x => x))}";
                return raw;
            }
            if (len < 4) { why = $"{TurnMissionsProperty} on turn {turnKey} is {len} bytes, too short to be an array"; return raw; }

            var n = BitConverter.ToInt32(raw, at);
            var value = new List<byte>();
            value.AddRange(BitConverter.GetBytes(n + 1));
            value.AddRange(raw.AsSpan(at + 4, len - 4).ToArray());
            value.AddRange(BitConverter.GetBytes(missionPackageIndex));
            var written = ZenPackage.SpliceExportData(raw, at, len, value.ToArray());

            // READ BACK, OR REFUSE. A turn that grew an entry pointing at nothing is worse than one
            // that never grew: the build looks clean and the mission never arrives.
            var exports = PristineExports(provider, written, packageName);
            var arc = exports.FirstOrDefault(e => e is not null && e.ExportType == "BrunoCampaignArc");
            if (arc is null) { why = "no BrunoCampaignArc export reads back"; return raw; }
            if (PartDetails.Value(arc.Properties, TurnsMapProperty) is not UScriptMap map)
            { why = $"{TurnsMapProperty} does not read back as a map"; return raw; }
            foreach (var kv in map.Properties)
            {
                if (!int.TryParse(PartDetails.Unwrap(kv.Key?.GenericValue)?.ToString(), out var k) || k != turnKey) continue;
                if (PartDetails.Unwrap(kv.Value?.GenericValue) is not FStructFallback turn)
                { why = $"turn {turnKey} does not read back as a struct"; return raw; }
                if (PartDetails.Value(turn.Properties, TurnMissionsProperty) is not UScriptArray arr ||
                    arr.Properties.Count != n + 1)
                { why = $"turn {turnKey} does not read back with {n + 1} mission(s)"; return raw; }
                var got = PartDetails.Unwrap(arr.Properties[^1].GenericValue) is FPackageIndex pi ? pi.Index : 0;
                if (got != missionPackageIndex)
                { why = $"turn {turnKey}'s new entry reads back as index {got}, not {missionPackageIndex}"; return raw; }
                return written;
            }
            why = $"turn {turnKey} is not in the map after the write";
            return raw;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Set a NameProperty to <paramref name="value"/>, in place.
    ///
    /// A rename cannot do this when the value carries an FName NUMBER. Measured 2026-09-07: the
    /// human rifle kit attaches its weapon at CharacterSocket "weaponSocket_0", and the name map
    /// holds "weaponSocket" with the number 1 beside it -- so a rename of "weaponSocket_0" found
    /// nothing and said so, and the B2's blaster stayed on a socket his skeleton does not have,
    /// which put it and every bolt it fired on his root bone. Writing the property is also
    /// narrower than a rename: a rename moves every use of that name in the package, and "Attach"
    /// is a word a package may use for more than one thing.
    /// </summary>
    public static byte[] SetName(IFileProvider provider, byte[] raw, string packageName,
                                 string exportClass, string property, string value, out string? why)
    {
        why = null;
        try
        {
            var (stem, number) = ZenPackage.ParseNumber(value);
            var name = stem ?? value;

            // The name first: appending moves the batch and every offset after it, so the range
            // this writes into has to be located against the GROWN package, never the old one.
            var pkg = ZenPackage.Read(raw);
            if (pkg.Names.IndexOf(name) < 0)
            {
                pkg.AppendName(name);
                return SetName(provider, pkg.Write(), packageName, exportClass, property, value, out why);
            }
            var index = pkg.Names.IndexOf(name);

            if (!Locate(provider, raw, packageName, exportClass, property, out var start, out var length, out why))
                return raw;
            if (length != 8) { why = $"{property} is {length} bytes, not an FName"; return raw; }

            var written = (byte[]) raw.Clone();
            BitConverter.GetBytes((uint) index).CopyTo(written, start);
            BitConverter.GetBytes(number).CopyTo(written, start + 4);

            // Read back, or refuse -- THROUGH THE SAME FINDER THAT LOCATED IT.
            //
            // This used to match on `ExportType.StartsWith(exportClass)`, which disagrees with
            // Locate: that accepts an export's NAME as well as its class, and every other method
            // here is called with a name. So naming `Default__BP_WeaponGearActor_C` located the
            // property, wrote the eight bytes correctly, and then failed its own read-back with
            // "reads back as '(nothing)'" -- because the verifier looked for an export whose
            // CLASS began with that name and there is none. A checker that disagrees with the
            // writer refuses correct work, which is worse than not checking.
            //
            // AND AT ANY DEPTH. The parsed-value check that replaced that one looked up a TOP-LEVEL
            // property by name, so `ApplyEffectClasses[1].TargetTag.TagName` -- an FName inside a
            // struct inside an array element -- was located, written correctly, and then refused
            // as reading '(nothing)'. SetText had exactly this fault and was fixed the same way;
            // this is the third writer in this file to refuse its own correct work by asking a
            // narrower question than it answered.
            //
            // So the property is located AGAIN in the written bytes, by the same walk, and the
            // eight bytes there must be the index and number just written. That also proves the
            // property is still findable after the write, which a parsed comparison would not.
            if (!Locate(provider, written, packageName, exportClass, property,
                        out var backAt, out var backLen, out var backWhy))
            { why = $"{property} cannot be found after the write: {backWhy}"; return raw; }
            if (backLen != 8) { why = $"{property} reads back as {backLen} bytes, not an FName"; return raw; }

            var gotIndex = BitConverter.ToUInt32(written, backAt);
            var gotNumber = BitConverter.ToInt32(written, backAt + 4);
            if (gotIndex != (uint) index || gotNumber != number)
            {
                var names = ZenPackage.Read(written).Names;
                var gotName = gotIndex < names.Count ? names[(int) gotIndex] : $"#{gotIndex}";
                why = $"{property} reads back as '{gotName}' (number {gotNumber}), not {value}";
                return raw;
            }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Write an FString property, which is the FIRST edit here whose LENGTH CHANGES.
    ///
    /// Every other write in this file is a swap: a number over a number, a name-table index over a
    /// name-table index, eight bytes for eight. An FString is a count and then its characters, so
    /// a longer word needs more room and every offset after it moves -- which is exactly what
    /// SpliceExportData exists for, and why this can be written at all without a new kind of
    /// repair.
    ///
    /// THE ENCODING IS UE's OWN RULE, not a preference: a string whose characters are all below
    /// 128 is written as one byte each with a POSITIVE count, and anything else as UTF-16 with a
    /// NEGATIVE one. Both counts INCLUDE the terminator, and an empty string is a count of zero
    /// with no characters at all -- not a count of one and a null.
    /// </summary>
    public static byte[] SetString(IFileProvider provider, byte[] raw, string packageName,
                                   string exportClass, string property, string value, out string? why)
    {
        why = null;
        try
        {
            if (!Locate(provider, raw, packageName, exportClass, property,
                        out var start, out var length, out why))
                return raw;

            var written = ZenPackage.SpliceExportData(raw, start, length, FStringBytes(value));

            // Read back, or refuse -- through the same finder that located it. See SetName: a
            // verifier that disagrees with its own writer refuses correct work.
            var export = FindExport(PristineExports(provider, written, packageName), exportClass);
            var got = export is null ? null : PartDetails.Value(export.Properties, property)?.ToString();
            if (!string.Equals(got, value, StringComparison.Ordinal))
            { why = $"{property} reads back as '{got ?? "(nothing)"}', not '{value}'"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    // ------------------------------------------------------------------ struct arrays

    /// <summary>
    /// Duplicate or remove one element of an array of STRUCTS.
    ///
    /// THE SHAPE SetSoftRefEntry REFUSED, and why it can be done now. A soft reference is always
    /// twenty bytes, so adding one is arithmetic. A struct element is written exactly as an
    /// export is -- an unversioned header naming which of its properties are present, then those
    /// values -- so nothing records its length and two elements of the same struct can differ in
    /// size. Arithmetic does not reach them.
    ///
    /// But <see cref="Locate"/> already walks that header: it takes an indexed path, and to
    /// resolve `[3]` it must read elements 0, 1 and 2 to find where each ends. The boundaries
    /// existed; they had never been asked for on their own.
    ///
    /// DUPLICATE RATHER THAN CREATE, and that is a deliberate limit rather than a shortcut. An
    /// element built from nothing needs an unversioned header written from nothing -- a fragment
    /// stream, a zero mask, and a decision about which properties to declare present. Copying an
    /// element that the game itself wrote gives a header that is known good, and the copy's values
    /// can then be edited one at a time through the same indexed path, which is a thing that
    /// already works. So "add a tier" is duplicate-then-edit, and every byte of it was written by
    /// the cooker.
    ///
    /// THE READ-BACK IS THE INVARIANT, not a count. After the splice, every element is located
    /// again and their lengths must add up to the array's length exactly. A count can be right
    /// while the bytes are cut through the middle of a header; lengths that fill the array
    /// cannot.
    /// </summary>
    public static byte[] SetStructEntry(IFileProvider provider, byte[] raw, string packageName,
                                        string exportClass, string property, int index,
                                        bool duplicate, out string? why)
    {
        why = null;
        try
        {
            if (!Locate(provider, raw, packageName, exportClass, property,
                        out var arrayAt, out var arrayLen, out why))
                return raw;

            if (arrayLen < 4)
            { why = $"{property} is {arrayLen} byte(s), too short to be an array"; return raw; }

            var count = BitConverter.ToInt32(raw, arrayAt);
            if (count <= 0) { why = $"{property} holds nothing to copy or remove"; return raw; }
            if (index < 0 || index >= count)
            { why = $"{property} holds {count} item(s), so [{index}] is not there"; return raw; }
            if (!duplicate && count == 1)
            {
                // AN EMPTY ARRAY IS NOT THE SAME AS ONE WITH NOTHING IN IT. Unversioned
                // serialisation stores no bytes for a property at its default, and an empty array
                // IS the default -- so a count of zero written where the cooker would have
                // written nothing at all is a shape the game never produces. Refusing is honest;
                // guessing is a corrupted export.
                why = $"{property} holds one item, and emptying an array is not something this " +
                      "writes -- the cooker stores an empty array as no bytes at all";
                return raw;
            }

            if (!Locate(provider, raw, packageName, exportClass, $"{property}[{index}]",
                        out var elemAt, out var elemLen, out why))
                return raw;

            if (elemAt < arrayAt + 4 || elemAt + elemLen > arrayAt + arrayLen)
            { why = $"[{index}] lies outside {property}'s own bytes"; return raw; }

            // THE COUNT FIRST, because it is the same four bytes either way and so moves nothing.
            var staged = (byte[]) raw.Clone();
            BitConverter.GetBytes(count + (duplicate ? 1 : -1)).CopyTo(staged, arrayAt);

            var element = raw.AsSpan(elemAt, elemLen).ToArray();
            var replacement = duplicate ? element.Concat(element).ToArray() : [];
            var written = ZenPackage.SpliceExportData(staged, elemAt, elemLen, replacement);

            // Read back, or refuse. Every element located again, and they must fill the array.
            if (!Locate(provider, written, packageName, exportClass, property,
                        out var backAt, out var backLen, out var backWhy))
            { why = $"{property} cannot be found after the write: {backWhy}"; return raw; }

            var backCount = BitConverter.ToInt32(written, backAt);
            if (backCount != count + (duplicate ? 1 : -1))
            { why = $"{property} reads back with {backCount} item(s), not {count + (duplicate ? 1 : -1)}"; return raw; }

            var filled = 4;
            for (var i = 0; i < backCount; i++)
            {
                if (!Locate(provider, written, packageName, exportClass, $"{property}[{i}]",
                            out _, out var len, out var eWhy))
                { why = $"{property}[{i}] cannot be found after the write: {eWhy}"; return raw; }
                filled += len;
            }
            if (filled != backLen)
            {
                why = $"{property}'s {backCount} element(s) account for {filled} of {backLen} " +
                      "bytes — the splice cut through something";
                return raw;
            }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    // ------------------------------------------------------------------ FText

    /// <summary>
    /// Set an FText to a LITERAL string.
    ///
    /// AN FText IS NOT A STRING, IT IS A LOOKUP, and that is the whole difficulty. Measured on
    /// this game's own bytes, a display name looks like this:
    ///
    ///     00 00 00 00  0B  14 00 00 00 00 00 00 00  13 00 00 00 "Class_Assault_Name "
    ///     flags        |   tableId FName             key FString
    ///                  historyType 11 = StringTableEntry
    ///
    /// There is NO source string in those bytes. The words on screen come from a string table
    /// asset, found by that key. So "edit the display name" cannot mean "overwrite the text" --
    /// there is no text there to overwrite.
    ///
    /// WHAT THIS WRITES INSTEAD is the shape the game ships for text that IS its own, which the
    /// census found seven of and which reads like this:
    ///
    ///     00 00 00 00  00  01 00 00 00 " "  21 00 00 00 "7CBB...BC40 "  12 00 00 00 "Coil Crisis Elite "
    ///     flags        |   namespace         key                            source
    ///                  historyType 0 = Base
    ///
    /// An empty namespace, a GUID key, and the string itself. Nothing is invented: this is
    /// byte-for-byte the shape of CPD_H_Name_Enemy_Coil_Enforcer_Crisis_VIP's LastName.
    ///
    /// THE KEY IS REPLACED, AND ON PURPOSE. A Base history is still looked up first, and keeping
    /// a key that resolves in the English locres would show the LOCALISED string and ignore the
    /// source we just wrote -- a change that lands perfectly in the file and does nothing on
    /// screen, which is the hardest kind to diagnose. A key derived from the text is in no
    /// localisation table, so the fallback fires and the source is what shows. Derived rather
    /// than random so that building the same mod twice produces the same bytes.
    /// </summary>
    public static byte[] SetText(IFileProvider provider, byte[] raw, string packageName,
                                 string exportClass, string property, string text, out string? why)
    {
        why = null;
        try
        {
            if (!LocateSized(provider, raw, packageName, exportClass, property,
                             out var start, out var length, out var sizes, out why))
                return raw;

            // IT HAS TO BE AN FText ALREADY. Locate finds a property's bytes by walking the
            // schema, and a schema says what a property IS -- but the bytes are what gets
            // overwritten, so the shape is checked against them before anything is written.
            if (length < 5)
            { why = $"{property} is {length} byte(s), too short to be an FText"; return raw; }
            var was = (sbyte) raw[start + 4];
            if (was is not (-1 or 0 or 11))
            {
                why = $"{property} has history type {was}, which is not one this writes over " +
                      "(None, Base or StringTableEntry)";
                return raw;
            }

            var bytes = TextBytes(packageName, exportClass, property, text);
            var written = ZenPackage.SpliceExportData(raw, start, length, bytes);

            // EVERY INSTANCED STRUCT AROUND IT GREW OR SHRANK BY THE SAME AMOUNT, and says how big
            // it is. Each size sits BEFORE the text, so the splice left its offset where it was.
            foreach (var at in sizes)
                BitConverter.GetBytes(BitConverter.ToInt32(written, at) + bytes.Length - length).CopyTo(written, at);

            // READ BACK THROUGH THE SAME WALK THAT LOCATED IT.
            //
            // The first version of this looked for a top-level property named `property` and
            // compared its FText. That works for `DisplayName` and cannot work for
            // `UIData.Name`, because no property is CALLED "UIData.Name" -- so the writer wrote
            // the right bytes and then refused them, reporting "does not read back as an FText"
            // about an FText it had just written. A checker that disagrees with its own writer
            // refuses correct work; that is the second time in this file, after SetName.
            //
            // Locating again and parsing the bytes asks the same question the walk answers, at
            // any depth, and it also proves the property is still FINDABLE after the splice --
            // which a comparison against a parsed value would not.
            if (!Locate(provider, written, packageName, exportClass, property,
                        out var backAt, out var backLen, out var backWhy))
            { why = $"{property} cannot be found after the write: {backWhy}"; return raw; }

            if (ReadsAsText(written, backAt, backLen) is not { } got)
            { why = $"{property} does not read back as an FText"; return raw; }
            if (!string.Equals(got, text, StringComparison.Ordinal))
            { why = $"{property} reads back as '{got}', not '{text}'"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// A literal FText as <see cref="SetText"/> writes it -- flags, a Base history, an empty
    /// namespace, a key derived from the text, and the text. One builder, so a text ADDED where
    /// there was none (<see cref="AddText"/>) is byte-for-byte the shape of one overwritten, which
    /// is the shape the game already showed on screen for Assault-MODDED and Devour-MODDED.
    /// </summary>
    internal static byte[] TextBytes(string packageName, string exportClass, string property, string text)
    {
        var value = new List<byte>();
        value.AddRange(BitConverter.GetBytes(0));      // flags: none
        value.Add(0);                                  // historyType: Base
        value.AddRange(Terminated(""));                // namespace: empty, as the game writes it
        value.AddRange(Terminated(KeyFor(packageName, exportClass, property, text)));
        value.AddRange(Terminated(text));
        return value.ToArray();
    }

    /// <summary>
    /// Give a struct an FText it does not carry: <paramref name="path"/> names the struct and the
    /// member, "UIData.Description".
    ///
    /// Force Grip is why. It is a copy of the Coil Captain's Resonate, and Resonate's UIData has a
    /// Name and no Description at all -- so <see cref="SetText"/> had nothing to overwrite and the
    /// tooltip stayed blank. The member is added with <see cref="AddStructMember"/> and then read
    /// back through the same walk that finds any other text.
    /// </summary>
    public static byte[] AddText(IFileProvider provider, byte[] raw, string packageName,
                                 string exportClass, string path, string text, out string? why)
    {
        why = null;
        var parts = SplitPath(path);
        if (parts.Length < 2)
        { why = $"'{path}' names no struct to add it to -- give the path to it, e.g. UIData.Description"; return raw; }

        var written = AddStructMember(provider, raw, packageName, exportClass, string.Join('.', parts[..^1]), parts[^1],
                                      TextBytes(packageName, exportClass, path, text), out why);
        if (why is not null) return raw;

        if (!Locate(provider, written, packageName, exportClass, path, out var at, out var len, out var backWhy))
        { why = $"{path} cannot be found after it was added: {backWhy}"; return raw; }
        if (ReadsAsText(written, at, len) is not { } got || !string.Equals(got, text, StringComparison.Ordinal))
        { why = $"{path} does not read back as '{text}'"; return raw; }
        return written;
    }

    /// <summary>
    /// Set a text, or ADD it when its struct declares it and does not carry it -- the one call a
    /// caller wants when it does not know which of the two a template needs.
    ///
    /// Tried in that order and not decided up front, because the two refuse for exactly the
    /// opposite reasons: SetText refuses a member that is absent and AddText one that is present.
    /// Whichever succeeds was the right one; when both refuse, both reasons are said.
    /// </summary>
    public static byte[] WriteText(IFileProvider provider, byte[] raw, string packageName,
                                   string exportClass, string path, string text,
                                   out string? why, out bool added)
    {
        added = false;
        var set = SetText(provider, raw, packageName, exportClass, path, text, out var setWhy);
        if (setWhy is null) { why = null; return set; }

        var add = AddText(provider, raw, packageName, exportClass, path, text, out var addWhy);
        if (addWhy is null) { why = null; added = true; return add; }

        why = $"{setWhy}; and adding it instead: {addWhy}";
        return raw;
    }

    /// <summary>
    /// The source string of a Base-history FText at these bytes, or null if that is not what is
    /// there.
    ///
    /// Only the shape <see cref="SetText"/> writes is parsed, and deliberately: this exists to
    /// check that writer's own output, so understanding more shapes than it produces would only
    /// let a wrong one pass.
    /// </summary>
    public static string? ReadsAsText(byte[] raw, int start, int length)
    {
        if (length < 5 || start + length > raw.Length) return null;
        if ((sbyte) raw[start + 4] != 0) return null;   // not a Base history

        var at = start + 5;
        for (var field = 0; field < 3; field++)
        {
            if (at + 4 > start + length) return null;
            var count = BitConverter.ToInt32(raw, at);
            at += 4;

            var wide = count < 0;
            var chars = Math.Abs(count);
            var bytes = wide ? chars * 2 : chars;
            if (bytes < 0 || at + bytes > start + length) return null;

            // The third field is the source string, which is the one being checked.
            if (field == 2)
                return bytes == 0 ? "" :
                    wide ? System.Text.Encoding.Unicode.GetString(raw, at, bytes - 2)
                         : System.Text.Encoding.ASCII.GetString(raw, at, bytes - 1);
            at += bytes;
        }
        return null;
    }

    /// <summary>
    /// An FString written the way the game writes the ones inside an FText: a count that ALWAYS
    /// includes a terminator, even when the string is empty.
    ///
    /// <see cref="FStringBytes"/> writes an empty string as a count of zero and no characters,
    /// which is what a standalone FString property holds. Inside an FText the game writes the
    /// empty namespace as a count of ONE and a single null -- measured, both of them. Either
    /// reads back empty, so this is not a correctness fix; it is so that what a mod writes is
    /// indistinguishable from what the editor wrote beside it.
    /// </summary>
    private static byte[] Terminated(string value)
    {
        if (value.All(c => c < 128))
        {
            var ascii = new byte[4 + value.Length + 1];
            BitConverter.GetBytes(value.Length + 1).CopyTo(ascii, 0);
            System.Text.Encoding.ASCII.GetBytes(value).CopyTo(ascii, 4);
            return ascii;   // the terminator is the zero already there
        }

        var wide = new byte[4 + (value.Length + 1) * 2];
        BitConverter.GetBytes(-(value.Length + 1)).CopyTo(wide, 0);
        System.Text.Encoding.Unicode.GetBytes(value).CopyTo(wide, 4);
        return wide;
    }

    /// <summary>
    /// A localisation key for text this mod owns: 32 hex characters, as the editor mints them,
    /// but derived from what is being written rather than drawn at random.
    ///
    /// Derived so a rebuild of the same project produces the same bytes. Unique enough that it
    /// will not collide with a key in the game's own localisation data, which is the only thing
    /// that would matter if it did.
    /// </summary>
    private static string KeyFor(string package, string export, string property, string text)
    {
        var seed = System.Text.Encoding.UTF8.GetBytes($"{package}|{export}|{property}|{text}");
        return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(seed));
    }

    /// <summary>An FString as UE serialises one: a signed count, then the characters.</summary>
    internal static byte[] FStringBytes(string value)
    {
        if (value.Length == 0) return BitConverter.GetBytes(0);

        if (value.All(c => c < 128))
        {
            var ascii = new byte[4 + value.Length + 1];
            BitConverter.GetBytes(value.Length + 1).CopyTo(ascii, 0);
            System.Text.Encoding.ASCII.GetBytes(value).CopyTo(ascii, 4);
            return ascii;   // the terminator is the zero already there
        }

        var wide = new byte[4 + (value.Length + 1) * 2];
        BitConverter.GetBytes(-(value.Length + 1)).CopyTo(wide, 0);
        System.Text.Encoding.Unicode.GetBytes(value).CopyTo(wide, 4);
        return wide;
    }

    // ------------------------------------------------------------------ soft reference arrays

    /// <summary>Bytes one entry of a TArray&lt;TSoftObjectPtr&gt; occupies: two FNames and an empty sub-path.</summary>
    private const int SoftRefBytes = 8 + 8 + 4;

    /// <summary>
    /// Add or remove one entry of an array of SOFT references.
    ///
    /// WHY THIS ARRAY AND NOT ARRAYS IN GENERAL. An entry's size is what decides whether a generic
    /// writer is possible, and a struct entry's size depends on which of its properties happen to
    /// be stored -- which is why every struct array here has a bespoke writer that walks the
    /// schema. A soft reference is always the same twenty bytes: a package FName, an asset FName,
    /// and an empty sub-path string. So the count and the entries are all there is, and adding or
    /// removing one is arithmetic.
    ///
    /// These are the arrays worth reaching first: Abilities, AbilitySets, GameplayEffects. They
    /// hold what a part DOES, they are soft so they need no import map entry, and giving a part an
    /// extra ability was not expressible at all before.
    ///
    /// THE SHAPE IS CHECKED, NOT ASSUMED. If the located bytes are not a count followed by that
    /// many twenty-byte entries then this is not the array it was thought to be, and writing into
    /// it would corrupt the export -- so it refuses and says what it measured.
    /// </summary>
    public static byte[] SetSoftRefEntry(IFileProvider provider, byte[] raw, string packageName,
                                         string exportClass, string property,
                                         string package, string asset, bool add, out string? why)
    {
        why = null;
        try
        {
            if (add)
            {
                // NAMES FIRST, then start again: appending moves the name batch and every offset
                // after it, so a location taken before the append points into the wrong bytes.
                var pkg = ZenPackage.Read(raw);
                var missing = new[] { package, asset }
                    .Where(n => pkg.Names.IndexOf(n) < 0).Distinct(StringComparer.Ordinal).ToList();
                if (missing.Count > 0)
                {
                    foreach (var n in missing) pkg.AppendName(n);
                    return SetSoftRefEntry(provider, pkg.Write(), packageName, exportClass,
                                           property, package, asset, add, out why);
                }
            }

            if (!Locate(provider, raw, packageName, exportClass, property,
                        out var start, out var length, out why))
                return raw;

            if (length < 4) { why = $"{property} is {length} byte(s), too short to be an array"; return raw; }
            var count = BitConverter.ToInt32(raw, start);
            if (count < 0 || length != 4 + count * SoftRefBytes)
            {
                why = $"{property} declares {count} entr(ies) in {length} bytes, which is not " +
                      $"{4 + count * SoftRefBytes} — it is not an array of soft references";
                return raw;
            }

            var names = ZenPackage.Read(raw);
            var wantPackage = names.Names.IndexOf(package);
            var wantAsset = names.Names.IndexOf(asset);

            var kept = new List<byte[]>();
            var removed = false;
            for (var i = 0; i < count; i++)
            {
                var at = start + 4 + i * SoftRefBytes;
                var entry = raw.AsSpan(at, SoftRefBytes).ToArray();
                if (!add && !removed &&
                    BitConverter.ToInt32(entry, 0) == wantPackage &&
                    BitConverter.ToInt32(entry, 8) == wantAsset)
                { removed = true; continue; }
                kept.Add(entry);
            }

            if (add)
            {
                var entry = new byte[SoftRefBytes];
                BitConverter.GetBytes((uint) wantPackage).CopyTo(entry, 0);
                BitConverter.GetBytes((uint) wantAsset).CopyTo(entry, 8);
                kept.Add(entry);
            }
            else if (!removed)
            { why = $"{property} has no entry naming {package}.{asset}"; return raw; }

            var value = new List<byte>(BitConverter.GetBytes(kept.Count));
            foreach (var e in kept) value.AddRange(e);

            var written = ZenPackage.SpliceExportData(raw, start, length, value.ToArray());

            // Read back, or refuse -- through the same finder that located it.
            var export = FindExport(PristineExports(provider, written, packageName), exportClass);
            var back = export?.Properties.FirstOrDefault(x => x.Name.Text == property);
            if (back?.Tag?.GenericValue is not UScriptArray got)
            { why = $"{property} does not read back as an array"; return raw; }
            if (got.Properties.Count != kept.Count)
            { why = $"{property} reads back with {got.Properties.Count} entr(ies), not {kept.Count}"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    // ------------------------------------------------------------------ stance animation sets

    /// <summary>
    /// The byte range of every entry of a struct array, walked the way <see cref="AddArcRecipe"/>
    /// walks a turn: the array's count, then each entry's unversioned header and its present
    /// values in schema order. Refuses when the walk does not land on the array's end.
    /// </summary>
    private static bool WalkStructArray(IFileProvider provider, byte[] raw, string packageName,
                                        string exportClass, string property, string structName,
                                        out int start, out int length, out List<(int At, int Len)> entries,
                                        out string? why)
    {
        entries = new List<(int, int)>();
        if (!Locate(provider, raw, packageName, exportClass, property, out start, out length, out why))
            return false;
        var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
        if (container is null) { why = "no mounted container holds the package, so its schema cannot be resolved"; return false; }
        var pkg = Open(provider, raw, packageName, container);
        if (pkg.Mappings is not { } mappings || !mappings.Types.TryGetValue(structName, out var schema) || schema is null)
        { why = $"{structName} is not in the mappings"; return false; }

        var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg) { Position = start };
        var count = ar.Read<int>();
        if (count < 0 || count > 100_000) { why = $"{property} declares {count} entries"; return false; }
        for (var e = 0; e < count; e++)
        {
            var at = (int) ar.Position;
            var header = new FUnversionedHeader(ar);
            if (header.HasValues)
            {
                using var it = new FIterator(header);
                do
                {
                    var (val, isNonZero) = it.Current;
                    if (!isNonZero) continue;
                    if (!schema.TryGetValue(val, out var info))
                    { why = $"{structName}: property {val} is not in the mappings, so its size is unknown"; return false; }
                    _ = new FPropertyTag(ar, info, ReadType.NORMAL);
                } while (it.MoveNext());
            }
            entries.Add((at, (int) ar.Position - at));
        }
        if ((int) ar.Position != start + length)
        { why = $"the walk ended {start + length - ar.Position} byte(s) short of {property}'s end"; return false; }
        return true;
    }

    /// <summary>
    /// Give a stance animation set entries it lacks, each a CLONE of an entry it has with only the
    /// tag changed -- so the clip is one the rig already owns and the bytes are the game's own.
    ///
    /// Why: the weapon actor's stance archetype picks a stance set by RIG and falls back to the
    /// human set key by key. A playable rig (Kabb) carries 47 keys; the BX's enemy set carries
    /// 33, and the 14 missing are the aim turns, steps and strafes a player's shot plays -- so
    /// those came out as human clips on the droid skeleton. Nothing new is imported: the tag is a
    /// name appended to the package, the montage index is the like entry's.
    /// </summary>
    public static byte[] AddAnimationEntries(IFileProvider provider, byte[] raw, string packageName,
                                             IReadOnlyList<(string Tag, string Like)> adds,
                                             out int added, out string? why)
    {
        added = 0; why = null;
        try
        {
            if (adds.Count == 0) return raw;
            const string ExportClass = "StanceAnimationSet", Property = "Animations", Struct = "AnimationSetEntry";

            // The existing tags, in entry order, off a read-back of the untouched bytes.
            var before = PristineExports(provider, raw, packageName).FirstOrDefault(e => e is not null && e.ExportType == ExportClass);
            if (before is null) { why = "no StanceAnimationSet export reads back"; return raw; }
            if (PartDetails.Value(before.Properties, Property) is not UScriptArray have)
            { why = $"{Property} does not read back as an array"; return raw; }
            var tags = have.Properties.Select(x => PartDetails.Unwrap(x.GenericValue) is FStructFallback st
                                                   ? PartDetails.TagOf(PartDetails.Value(st.Properties, "EntryTag")) ?? "" : "").ToList();

            var wanted = adds.Where(a => !tags.Contains(a.Tag)).ToList();
            if (wanted.Count == 0) return raw;
            foreach (var (tag, like) in wanted)
                if (!tags.Contains(like)) { why = $"{tag}: the set has no {like} to clone"; return raw; }

            // Names first: appending shifts everything after the name batch.
            var zen = ZenPackage.Read(raw);
            var nameIndex = wanted.ToDictionary(a => a.Tag, a => zen.AppendName(a.Tag));
            var named = zen.Write();

            if (!WalkStructArray(provider, named, packageName, ExportClass, Property, Struct,
                                 out var start, out var length, out var entries, out why))
                return raw;
            if (entries.Count != tags.Count) { why = $"walked {entries.Count} entries, read back {tags.Count}"; return raw; }

            var value = new List<byte>();
            value.AddRange(BitConverter.GetBytes(tags.Count + wanted.Count));
            value.AddRange(named.AsSpan(start + 4, length - 4).ToArray());
            foreach (var (tag, like) in wanted)
            {
                var (at, len) = entries[tags.IndexOf(like)];
                var header = len - 12;   // FName (8) + ObjectProperty (4) after the unversioned header
                if (header < 1 || header > 8) { why = $"an entry is {len} bytes, not a header plus a name and an object"; return raw; }
                var clone = named.AsSpan(at, len).ToArray();
                BitConverter.GetBytes(nameIndex[tag]).CopyTo(clone, header);
                BitConverter.GetBytes(0).CopyTo(clone, header + 4);
                value.AddRange(clone);
            }
            var written = ZenPackage.SpliceExportData(named, start, length, value.ToArray());

            // Read back, or refuse.
            var after = PristineExports(provider, written, packageName).FirstOrDefault(e => e is not null && e.ExportType == ExportClass);
            if (after is null || PartDetails.Value(after.Properties, Property) is not UScriptArray got ||
                got.Properties.Count != tags.Count + wanted.Count)
            { why = $"{Property} does not read back with {tags.Count + wanted.Count} entries"; return raw; }
            for (var i = 0; i < wanted.Count; i++)
            {
                var entry = PartDetails.Unwrap(got.Properties[tags.Count + i].GenericValue) as FStructFallback;
                var gotTag = entry is null ? null : PartDetails.TagOf(PartDetails.Value(entry.Properties, "EntryTag"));
                if (gotTag != wanted[i].Tag) { why = $"entry {tags.Count + i} reads back as {gotTag ?? "(nothing)"}, not {wanted[i].Tag}"; return raw; }
            }
            added = wanted.Count;
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Give a stance archetype a version for a rig it has none for: a clone of its last
    /// rig-matched version with the set's two path names and the rig tag changed.
    ///
    /// Why: SASArch_2HRifle lists Human, Kabb, B1 and BX; a B2 holding a rifle matches none and
    /// is Human throughout. The set named here is a mod copy, so its names are appended to the
    /// archetype's name batch; the rig tag likewise.
    /// </summary>
    public static byte[] AddArchetypeVersion(IFileProvider provider, byte[] raw, string packageName,
                                             string setPackage, string rigTag, out string? why)
    {
        why = null;
        try
        {
            const string ExportClass = "StanceAnimationSetArchetype", Property = "Versions", Struct = "StanceAnimationSetTagMatch";
            var setLeaf = setPackage[(setPackage.LastIndexOf('/') + 1)..];

            var zen = ZenPackage.Read(raw);
            var pkgName = zen.AppendName(setPackage);
            var assetName = zen.AppendName(setLeaf);
            var rigName = zen.AppendName(rigTag);
            var named = zen.Write();

            if (!WalkStructArray(provider, named, packageName, ExportClass, Property, Struct,
                                 out var start, out var length, out var entries, out why))
                return raw;
            if (entries.Count == 0) { why = $"{Property} is empty, so there is no version to clone"; return raw; }

            // The last version: rig-matched, with an empty specialization container -- the shape
            // wanted. Its bytes are cloned and the three names in them replaced BY VALUE: a name is
            // an (index, number) pair, so the old set's package name, its asset name and the old
            // rig tag are found as their indices and swapped for the appended ones. No layout is
            // assumed -- the entry measured 36 bytes where a soft path, a tag and a header add up
            // to 32, and whatever the other four are, they are carried as they were.
            var oldVersion = PristineExports(provider, raw, packageName).FirstOrDefault(e => e is not null && e.ExportType == ExportClass);
            if (oldVersion is null || PartDetails.Value(oldVersion.Properties, Property) is not UScriptArray olds ||
                olds.Properties.Count != entries.Count || PartDetails.Unwrap(olds.Properties[^1].GenericValue) is not FStructFallback oldLast)
            { why = $"{Property} does not read back with {entries.Count} versions to clone from"; return raw; }
            var oldSet = PartDetails.SoftPathOf(PartDetails.Value(oldLast.Properties, "StanceAnimationSet")) ?? "";
            var oldRig = PartDetails.TagOf(PartDetails.Value(oldLast.Properties, "MatchingAnimRigTag")) ?? "";
            var oldPkg = oldSet.Contains('.') ? oldSet[..oldSet.LastIndexOf('.')] : oldSet;
            var oldLeaf = oldPkg[(oldPkg.LastIndexOf('/') + 1)..];
            if (oldPkg.Length == 0 || oldRig.Length == 0) { why = "the last version names no set or no rig to clone from"; return raw; }
            var oldPkgName = zen.Names.IndexOf(oldPkg);
            var oldLeafName = zen.Names.IndexOf(oldLeaf);
            var oldRigName = zen.Names.IndexOf(oldRig);
            if (oldPkgName < 0 || oldLeafName < 0 || oldRigName < 0)
            { why = $"the last version's names are not all in the name map ({oldPkg}, {oldLeaf}, {oldRig})"; return raw; }

            var (at, len) = entries[^1];
            var clone = named.AsSpan(at, len).ToArray();
            var swapped = 0;
            for (var i = 0; i + 8 <= clone.Length; i += 1)
            {
                var idx = BitConverter.ToInt32(clone, i);
                var num = BitConverter.ToInt32(clone, i + 4);
                if (num != 0) continue;
                var to = idx == oldPkgName ? pkgName : idx == oldLeafName ? assetName : idx == oldRigName ? rigName : -1;
                if (to < 0) continue;
                BitConverter.GetBytes(to).CopyTo(clone, i);
                swapped++;
                i += 7;
            }
            if (swapped != 3) { why = $"{swapped} of the 3 names were found in the cloned version ({len} bytes: {BitConverter.ToString(clone)})"; return raw; }

            var value = new List<byte>();
            value.AddRange(BitConverter.GetBytes(entries.Count + 1));
            value.AddRange(named.AsSpan(start + 4, length - 4).ToArray());
            value.AddRange(clone);
            var written = ZenPackage.SpliceExportData(named, start, length, value.ToArray());

            var after = PristineExports(provider, written, packageName).FirstOrDefault(e => e is not null && e.ExportType == ExportClass);
            if (after is null || PartDetails.Value(after.Properties, Property) is not UScriptArray got ||
                got.Properties.Count != entries.Count + 1)
            { why = $"{Property} does not read back with {entries.Count + 1} versions"; return raw; }
            if (PartDetails.Unwrap(got.Properties[^1].GenericValue) is not FStructFallback last)
            { why = "the new version does not read back as a struct"; return raw; }
            var gotSet = PartDetails.SoftPathOf(PartDetails.Value(last.Properties, "StanceAnimationSet")) ?? "";
            var gotRig = PartDetails.TagOf(PartDetails.Value(last.Properties, "MatchingAnimRigTag")) ?? "";
            if (!gotSet.StartsWith(setPackage, StringComparison.Ordinal) || gotRig != rigTag)
            { why = $"the new version reads back as {gotSet} for {gotRig}, not {setPackage} for {rigTag}"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>The array a recipe table keeps its weighted entries in.</summary>
    public const string WeightedRecipesProperty = "WeightedRecipes";

    /// <summary>The array a MISSION table keeps its weighted entries in.</summary>
    public const string WeightedMissionsProperty = "WeightedMissions";

    /// <summary>
    /// ADD A MISSION TO A WEIGHTED TABLE, rather than taking a row off something else.
    ///
    /// THE WHOLE POINT IS THAT NOTHING IS DISPLACED. Every route the mission editor had until now
    /// re-points an existing row or an existing campaign slot, which puts the author's mission in
    /// front of the player by taking a shipped one away -- and no table has a spare row to spend:
    /// `bEmptyOption` is false on all 65 rows across the five tables. The author's words for what
    /// he actually wanted: "i was hoping it'd just appear without making any existing missions
    /// disappear and appear long side it". That is this, and only this.
    ///
    /// SIMPLER THAN <see cref="AddWeightedRecipe"/>, which is the same edit one asset over.
    /// `WeightedMission` and `WeightedRecipe` share slots 0..3 exactly, and where a recipe's slot 4
    /// is a RecipePayload struct a mission's is a bare ObjectProperty:
    ///     WeightedMission   0 Weight (float)   1 bEmptyOption   2 FactTagRequirements
    ///                       3 FactValueRequirements             4 Mission (an FPackageIndex)
    /// Only 0 and 4 are written; every other slot takes its default, which is what an untouched
    /// fact gate and an un-flagged empty option are. A FALSE bool has no bytes at all in
    /// unversioned data -- it is a zero slot in the header -- so `bEmptyOption` costs nothing.
    ///
    /// The mission must already be an IMPORT of this table. <see cref="AddImport"/> puts it there
    /// and hands back the negative FPackageIndex this takes; the caller owns the container store
    /// entry's ImportedPackages that the import's index refers to.
    /// </summary>
    public static byte[] AddWeightedMission(IFileProvider provider, byte[] raw, string packageName,
                                            int missionPackageIndex, float weight, out string? why)
    {
        why = null;
        try
        {
            if (missionPackageIndex >= 0)
            { why = "the mission must be an import -- a negative FPackageIndex"; return raw; }
            if (!Locate(provider, raw, packageName, "MissionWeightedTable", WeightedMissionsProperty,
                        out var start, out var length, out why))
                return raw;
            if (length < 4)
            { why = $"{WeightedMissionsProperty} is {length} bytes, too short to be an array"; return raw; }
            var count = BitConverter.ToInt32(raw, start);
            if (count < 0 || count > 10_000)
            { why = $"{WeightedMissionsProperty} declares {count} entries"; return raw; }

            var element = new List<byte>();
            element.AddRange(BuildHeader(new List<(int, bool)> { (0, weight == 0f), (4, false) }));
            if (weight != 0f) element.AddRange(BitConverter.GetBytes(weight));
            element.AddRange(BitConverter.GetBytes(missionPackageIndex));

            var value = new List<byte>();
            value.AddRange(BitConverter.GetBytes(count + 1));
            value.AddRange(raw.AsSpan(start + 4, length - 4).ToArray());
            value.AddRange(element);

            var written = ZenPackage.SpliceExportData(raw, start, length, value.ToArray());

            // READ BACK, OR REFUSE. A table that grew a row pointing at nothing is worse than one
            // that never grew: the author sees a clean build and the mission is one more candidate
            // that can never be drawn.
            var exports = PristineExports(provider, written, packageName);
            var table = exports.FirstOrDefault(e => e is not null && e.ExportType == "MissionWeightedTable");
            if (table is null) { why = "no MissionWeightedTable export reads back"; return raw; }
            if (PartDetails.Value(table.Properties, WeightedMissionsProperty) is not UScriptArray arr ||
                arr.Properties.Count != count + 1)
            { why = $"{WeightedMissionsProperty} does not read back with {count + 1} entries"; return raw; }
            if (PartDetails.Unwrap(arr.Properties[^1].GenericValue) is not FStructFallback entry)
            { why = "the new entry does not read back as a struct"; return raw; }
            var gotWeight = PartDetails.Value(entry.Properties, "Weight") is float w ? w : 0f;
            if (Math.Abs(gotWeight - weight) > 0.001f)
            { why = $"the new entry reads back at weight {gotWeight}, not {weight}"; return raw; }
            var gotMission = PartDetails.Unwrap(PartDetails.Value(entry.Properties, "Mission")) is FPackageIndex pi
                ? pi.Index : 0;
            if (gotMission != missionPackageIndex)
            { why = $"the new entry's Mission reads back as index {gotMission}, not {missionPackageIndex}"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Add one entry to a RecipeWeightedTable: a recipe class, at a weight, started on its own the
    /// moment it is drawn.
    ///
    /// The second array this tool can grow, on the plan <see cref="AddMeshOption"/> proved: bump the
    /// count, keep the bytes, append an element. The element is BUILT from the mappings' two
    /// schemas rather than cloned -- WeightedRecipe (8 slots) and its RecipePayload (24 slots) --
    /// with only the properties that matter marked present, so every other one takes its default:
    ///     WeightedRecipe   0 Weight (float)          4 RecipePayload (struct)
    ///     RecipePayload    1 Status (enum byte)      3 RecipeClass (object: an FPackageIndex)
    ///                      7 bAutoStart (bool)
    /// A FALSE bool in unversioned data has no bytes -- it is a zero slot in the header -- and a
    /// TRUE one is a present slot followed by a single 1 byte. Status is written because every
    /// entry the game ships writes it, which says Available is not the struct's default.
    ///
    /// Why this exists (2026-09-07): a campaign arc draws recipes from these tables every cycle,
    /// and a recipe is how the game itself puts a story character on the roster -- kit intact, past
    /// the hiring pool's roll. An entry here is therefore the data-only trigger for a recruit.
    /// </summary>
    public static byte[] AddWeightedRecipe(IFileProvider provider, byte[] raw, string packageName,
                                           int recipePackageIndex, float weight, bool autoStart,
                                           byte status, out string? why)
    {
        why = null;
        try
        {
            if (recipePackageIndex >= 0)
            { why = "the recipe must be an import -- a negative FPackageIndex"; return raw; }
            if (!Locate(provider, raw, packageName, "RecipeWeightedTable", WeightedRecipesProperty,
                        out var start, out var length, out why))
                return raw;
            if (length < 4) { why = $"{WeightedRecipesProperty} is {length} bytes, too short to be an array"; return raw; }
            var count = BitConverter.ToInt32(raw, start);
            if (count < 0 || count > 10_000) { why = $"{WeightedRecipesProperty} declares {count} entries"; return raw; }

            // A FALSE bool is header-only -- a zero slot, no bytes -- but a TRUE one is written as
            // one byte, the way FBoolProperty::SerializeItem writes it. Measured the hard way: with
            // no byte the entry read back with bAutoStart False, off the byte that followed.
            var payload = RecipePayloadBytes(recipePackageIndex, status, autoStart);

            var element = new List<byte>();
            element.AddRange(BuildHeader(new List<(int, bool)> { (0, weight == 0f), (4, false) }));
            if (weight != 0f) element.AddRange(BitConverter.GetBytes(weight));
            element.AddRange(payload);

            var value = new List<byte>();
            value.AddRange(BitConverter.GetBytes(count + 1));
            value.AddRange(raw.AsSpan(start + 4, length - 4).ToArray());
            value.AddRange(element);

            var written = ZenPackage.SpliceExportData(raw, start, length, value.ToArray());

            // Read back, or refuse: the new entry must come back as the recipe, the weight and the
            // start asked for, or the table goes back untouched with the reason.
            var exports = PristineExports(provider, written, packageName);
            var table = exports.FirstOrDefault(e => e is not null && e.ExportType == "RecipeWeightedTable");
            if (table is null) { why = "no RecipeWeightedTable export reads back"; return raw; }
            if (PartDetails.Value(table.Properties, WeightedRecipesProperty) is not UScriptArray arr ||
                arr.Properties.Count != count + 1)
            { why = $"{WeightedRecipesProperty} does not read back with {count + 1} entries"; return raw; }
            if (PartDetails.Unwrap(arr.Properties[^1].GenericValue) is not FStructFallback entry)
            { why = "the new entry does not read back as a struct"; return raw; }
            var gotWeight = PartDetails.Value(entry.Properties, "Weight") is float w ? w : 0f;
            if (Math.Abs(gotWeight - weight) > 0.001f)
            { why = $"the new entry reads back at weight {gotWeight}, not {weight}"; return raw; }
            if (PartDetails.Unwrap(PartDetails.Value(entry.Properties, "RecipePayload")) is not FStructFallback pay)
            { why = "the new entry's RecipePayload does not read back"; return raw; }
            var gotClass = PartDetails.Unwrap(PartDetails.Value(pay.Properties, "RecipeClass")) is FPackageIndex pi ? pi.Index : 0;
            if (gotClass != recipePackageIndex)
            { why = $"the new entry's RecipeClass reads back as index {gotClass}, not {recipePackageIndex}"; return raw; }
            var gotStart = PartDetails.Value(pay.Properties, "bAutoStart") is bool b && b;
            if (gotStart != autoStart) { why = $"the new entry's bAutoStart reads back {gotStart}"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Add one entry to a mesh palette: a tag, and the mesh that tag should wear.
    ///
    /// THIS IS THE WALL EVERYTHING ELSE HAS BEEN STOPPED BY. Every edit this tool makes to a cooked
    /// package so far REPLACES something -- a name, a value, a whole property. Adding an ARRAY
    /// ENTRY has been written down as impossible more than once, and never actually tried.
    ///
    /// It turns out not to need element parsing at all. An array's value is a count followed by its
    /// elements back to back, and <see cref="Locate"/> already gives the whole run's byte range,
    /// because it measures a property by letting CUE4Parse read it. So the edit is: bump the count,
    /// keep the existing bytes, and append one more element on the end.
    ///
    /// The element is the smallest struct in the game -- two slots, both shapes already written
    /// elsewhere here:
    ///     CustomizationSkeletalMeshPaletteOption   0 RequiredTags (GameplayTagContainer)
    ///                                             1 SkeletalMesh (SoftObjectProperty)
    /// A soft path is two FNames and an empty sub-path string, the same as a brush's.
    ///
    /// Names are appended first and the whole thing re-planned against the grown package, for the
    /// reason <see cref="AddTagContainer"/> gives: the name batch moves and every offset after it
    /// moves with it. Read back or refused, the same as every other write here.
    /// </summary>
    public static byte[] AddMeshOption(IFileProvider provider, byte[] raw, string packageName,
                                       string exportClass, string tag,
                                       string meshPackage, string meshAsset, out string? why)
    {
        why = null;
        try
        {
            // ALREADY THERE? Then appending is pointless and the reason is worth saying properly.
            //
            // A palette is keyed by weapon TYPE TAG, and a duplicated weapon keeps the donor's tag
            // by default -- so its tag is the one the donor's entry already answers to. The game
            // reads that entry; a second one appended after it is never reached. This used to be
            // caught only by the read-back below, which found the DONOR's entry when it looked the
            // tag up and reported "reads back wearing <the game's mesh>, not <yours>" -- true, and
            // no help at all to an author whose twenty rows of "your mesh" then shipped as none.
            // Reported 2026-09-11: the base weapon right, every fitted modification the game's.
            if (PartDetails.ReadExports(PristineExports(provider, raw, packageName), packageName)
                    .Meshes.FirstOrDefault(m => m.RequiredTags.Contains(tag, StringComparer.Ordinal))
                is { } already)
            {
                why = $"this palette already has an entry for {tag}, wearing " +
                      $"{already.MeshPath?[(already.MeshPath.LastIndexOf('/') + 1)..] ?? "nothing"}. " +
                      "The game reads that one, so an entry added after it is never reached -- " +
                      "this weapon shares the type tag it was copied from. Give it a tag of its " +
                      "own and every modification will find this mesh instead.";
                return raw;
            }

            var needed = new[] { tag, meshPackage, meshAsset };
            var pkg = ZenPackage.Read(raw);
            var missing = needed.Where(n => pkg.Names.IndexOf(n) < 0).Distinct(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                foreach (var n in missing) pkg.AppendName(n);
                return AddMeshOption(provider, pkg.Write(), packageName, exportClass, tag,
                                     meshPackage, meshAsset, out why);
            }

            if (!Locate(provider, raw, packageName, exportClass, MeshOptionsProperty,
                        out var start, out var length, out why))
                return raw;
            if (length < 4) { why = $"{MeshOptionsProperty} is {length} bytes, too short to be an array"; return raw; }

            var count = BitConverter.ToInt32(raw, start);
            if (count < 0 || count > 10_000) { why = $"{MeshOptionsProperty} declares {count} entries"; return raw; }

            var element = new List<byte>();
            element.AddRange(BuildHeader(new List<(int, bool)> { (0, false), (1, false) }));
            element.AddRange(SerializeTagContainer(new List<int> { pkg.Names.IndexOf(tag) }));
            element.AddRange(NameBytes(pkg.Names.IndexOf(meshPackage)));
            element.AddRange(NameBytes(pkg.Names.IndexOf(meshAsset)));
            element.AddRange(BitConverter.GetBytes(0));

            var value = new List<byte>();
            value.AddRange(BitConverter.GetBytes(count + 1));
            value.AddRange(raw.AsSpan(start + 4, length - 4).ToArray());
            value.AddRange(element);

            var written = ZenPackage.SpliceExportData(raw, start, length, value.ToArray());
            LastAttempt = written;

            // Read back, or refuse. The new entry must come back with BOTH halves -- a tag with no
            // mesh is an entry the game finds and draws nothing for, which is the failure this is
            // trying to make impossible rather than merely unlikely.
            var exports = PristineExports(provider, written, packageName);
            var got = PartDetails.ReadExports(exports, packageName).Meshes
                .FirstOrDefault(m => m.RequiredTags.Contains(tag, StringComparer.Ordinal));
            if (got is null)
            { why = $"the new entry does not read back -- nothing in {MeshOptionsProperty} requires {tag}"; return raw; }
            var want = meshPackage + "." + meshAsset;
            if (!string.Equals(got.MeshPath, want, StringComparison.Ordinal))
            { why = $"the new entry reads back wearing '{got.MeshPath}', not {want}"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>The array a mesh palette keeps its tag-to-mesh entries in.</summary>
    public const string MeshOptionsProperty = "SkeletalMeshOptions";

    // ------------------------------------------------------------------ the game's own overlay action

    /// <summary>
    /// Fill a DataTable overlay action's list: each entry names a table of the GAME's and a table
    /// of the MOD's to lay over it.
    ///
    /// WHY THIS IS WORTH HAVING. Every table edit this tool has ever made rewrites the game's own
    /// asset, which means two mods that both touch one table cannot coexist and neither author can
    /// do anything about it. The game ships a first-class answer to that --
    /// `BitReactorGameFeatureAction_DataTableOverlay`, whose runtime halves `AppliedTargetTables`
    /// and `AppliedOverlayTables` exist so it can be taken back off on deactivate -- and nothing
    /// we build has ever used it.
    ///
    /// FBitReactorDataTableOverlay is two soft references and nothing else, TargetTable then
    /// OverlayTable, so an entry is the same shape <see cref="AddMeshOption"/> already writes for
    /// a mesh: a top-level asset path is TWO FNames, the package and the asset, followed by an
    /// empty sub-path string.
    /// </summary>
    public static byte[] AddTableOverlays(
        IFileProvider provider, byte[] raw, string packageName, string exportClass,
        IReadOnlyList<(string TargetPackage, string TargetAsset,
                       string OverlayPackage, string OverlayAsset)> pairs,
        out string? why, string? headerFrom = null)
    {
        why = null;
        try
        {
            if (pairs.Count == 0) { why = "no overlay pairs to write"; return raw; }

            // NAMES FIRST, and then start again. Appending one moves the name batch and every
            // offset after it, so a plan made before the append points into the wrong bytes --
            // AddTagContainer's rule, and the reason this recurses rather than continuing.
            var needed = pairs.SelectMany(p => new[] { p.TargetPackage, p.TargetAsset,
                                                       p.OverlayPackage, p.OverlayAsset })
                              .Distinct(StringComparer.Ordinal).ToList();
            var pkg = ZenPackage.Read(raw);
            var missing = needed.Where(n => pkg.Names.IndexOf(n) < 0).ToList();
            if (missing.Count > 0)
            {
                foreach (var n in missing) pkg.AppendName(n);
                return AddTableOverlays(provider, pkg.Write(), packageName, exportClass, pairs,
                                        out why, headerFrom);
            }

            var plan = Plan(provider, raw, packageName, exportClass,
                            GameFeaturePlugin.OverlaysProperty, out why, headerFrom);
            if (plan is null) return raw;
            if (plan.Type != "ArrayProperty")
            { why = $"'{GameFeaturePlugin.OverlaysProperty}' is a {plan.Type}, not an array"; return raw; }

            var value = new List<byte>();
            value.AddRange(BitConverter.GetBytes(pairs.Count));
            foreach (var p in pairs)
            {
                value.AddRange(BuildHeader(new List<(int, bool)> { (0, false), (1, false) }));
                value.AddRange(SoftPath(pkg, p.TargetPackage, p.TargetAsset));
                value.AddRange(SoftPath(pkg, p.OverlayPackage, p.OverlayAsset));
            }

            var written = Splice(raw, plan, value.ToArray());
            LastAttempt = written;

            var complaint = VerifyOverlays(provider, written, packageName, exportClass, pairs, headerFrom);
            if (complaint is not null)
            { why = "the overlay list did not read back correctly: " + complaint; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Give an object array one entry, by the FPackageIndex that names it.
    ///
    /// `GameFeatureData.Actions` is the only reason this exists and it is the last link in the
    /// chain: an action nothing points at is an action the subsystem never runs. An object array
    /// is a count and then one FPackageIndex per entry -- the same four bytes
    /// <see cref="AddClass"/> writes for a single reference, which is why that method's warning
    /// applies here verbatim: <see cref="AddImport"/> must have put the import there FIRST, and
    /// the plan must be made against the bytes it returned.
    /// </summary>
    public static byte[] AddObjectArray(IFileProvider provider, byte[] raw, string packageName,
                                        string exportClass, string property,
                                        IReadOnlyList<int> fpackageIndices, out string? why,
                                        string? headerFrom = null)
    {
        why = null;
        try
        {
            if (fpackageIndices.Count == 0) { why = $"'{property}' was given nothing to point at"; return raw; }
            if (fpackageIndices.Any(i => i == 0))
            { why = $"'{property}' was given a null reference to write"; return raw; }

            var plan = Plan(provider, raw, packageName, exportClass, property, out why, headerFrom);
            if (plan is null) return raw;
            if (plan.Type != "ArrayProperty")
            { why = $"'{property}' is a {plan.Type}, not an array"; return raw; }

            var value = new List<byte>();
            value.AddRange(BitConverter.GetBytes(fpackageIndices.Count));
            foreach (var i in fpackageIndices) value.AddRange(BitConverter.GetBytes(i));

            var written = Splice(raw, plan, value.ToArray());
            LastAttempt = written;

            // READ BACK BY COUNT, not by name. A brand-new import cannot be resolved here at all
            // -- the reference is a row in the package's import map and resolving it needs the
            // container store entry's ImportedPackages, which the build writes at pak time. That
            // is the same reason VerifyClass checks the bare index, and an early version of this
            // refused its own correct write over it.
            var exports = PristineExports(provider, written, packageName, headerFrom);
            var export = FindExport(exports, exportClass);
            if (export is null) { why = $"the '{exportClass}' export is gone"; return raw; }
            var back = export.Properties.FirstOrDefault(x => x.Name.Text == property);
            if (back is null) { why = $"'{property}' is still not there"; return raw; }
            if (back.Tag?.GenericValue is UScriptArray arr && arr.Properties.Count != fpackageIndices.Count)
            { why = $"'{property}' reads back with {arr.Properties.Count} entr(ies), not {fpackageIndices.Count}"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>A top-level asset path: the package FName, the asset FName, an empty sub-path.</summary>
    private static byte[] SoftPath(ZenPackage pkg, string package, string asset)
    {
        var b = new List<byte>();
        b.AddRange(NameBytes(pkg.Names.IndexOf(package)));
        b.AddRange(NameBytes(pkg.Names.IndexOf(asset)));
        b.AddRange(BitConverter.GetBytes(0));
        return b.ToArray();
    }

    /// <summary>
    /// The splice AddClass makes, said once.
    ///
    /// A plan is the new unversioned header, the values that were already there up to the
    /// insertion point, and then the new value -- replacing everything from the export's start to
    /// that point.
    /// </summary>
    private static byte[] Splice(byte[] raw, Insertion plan, byte[] value)
    {
        var replacement = new byte[plan.NewHeader.Length + (plan.InsertAt - plan.HeaderEnd) + value.Length];
        plan.NewHeader.CopyTo(replacement, 0);
        Array.Copy(raw, plan.HeaderEnd, replacement, plan.NewHeader.Length, plan.InsertAt - plan.HeaderEnd);
        value.CopyTo(replacement, plan.NewHeader.Length + (plan.InsertAt - plan.HeaderEnd));
        return ZenPackage.SpliceExportData(raw, plan.ExportStart, plan.InsertAt - plan.ExportStart,
                                           replacement);
    }

    /// <summary>
    /// Every pair must come back naming BOTH tables.
    ///
    /// An overlay whose target read back and whose overlay did not is an action the runtime finds,
    /// applies to the game's table, and lays nothing over -- which is worse than no action, and is
    /// exactly the class of half-written entry AddMeshOption's read-back exists to catch.
    /// </summary>
    private static string? VerifyOverlays(
        IFileProvider provider, byte[] written, string packageName, string exportClass,
        IReadOnlyList<(string TargetPackage, string TargetAsset,
                       string OverlayPackage, string OverlayAsset)> pairs, string? headerFrom)
    {
        try
        {
            var export = FindExport(PristineExports(provider, written, packageName, headerFrom),
                                    exportClass);
            if (export is null) return $"the '{exportClass}' export is gone";

            var prop = export.Properties.FirstOrDefault(
                x => x.Name.Text == GameFeaturePlugin.OverlaysProperty);
            if (prop is null) return $"'{GameFeaturePlugin.OverlaysProperty}' is still not there";
            if (prop.Tag?.GenericValue is not UScriptArray arr)
                return $"'{GameFeaturePlugin.OverlaysProperty}' reads back as " +
                       $"{prop.Tag?.GenericValue?.GetType().Name ?? "nothing"}, not an array";
            if (arr.Properties.Count != pairs.Count)
                return $"reads back with {arr.Properties.Count} entr(ies), not {pairs.Count}";

            for (var i = 0; i < pairs.Count; i++)
            {
                var flat = arr.Properties[i]?.GenericValue?.ToString() ?? "";
                if (arr.Properties[i]?.GenericValue is FScriptStruct { StructType: FStructFallback sf })
                    flat = string.Join(" ", sf.Properties.Select(
                        q => q.Name.Text + "=" + (q.Tag?.GenericValue?.ToString() ?? "")));

                foreach (var want in new[] { pairs[i].TargetAsset, pairs[i].OverlayAsset })
                    if (!flat.Contains(want, StringComparison.OrdinalIgnoreCase))
                        return $"entry {i} reads back as '{flat}', which does not name {want}";
            }
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>The last package SetImageToTexture wrote, kept even when it refused it -- for --imagetest.</summary>
    public static byte[]? LastAttempt;

    /// <summary>An FName as a cooked export writes it: the name index, and a number that is never set.</summary>
    private static byte[] NameBytes(int index)
    {
        var b = new byte[8];
        BitConverter.GetBytes((uint) index).CopyTo(b, 0);
        return b;
    }

    /// <summary>
    /// The byte range a property occupies inside its export, or false with a reason.
    ///
    /// The same walk <see cref="Plan"/> makes, stopped at a property that IS there rather than at
    /// the gap where a missing one belongs. It exists because a value cannot always be found by
    /// its content: an FText that is EMPTY has no string to search for, and the game leaves the
    /// display name empty on around a third of its parts -- every one of which is a part somebody
    /// might copy and want to name.
    ///
    /// <paramref name="property"/> may be a DOTTED PATH into nested structs -- "GearKitInfo.
    /// DisplayName" -- because a value a person reads is not always a top-level property. A gear
    /// kit's name, which is what the Armoury's MODEL header draws, is one level down, and until
    /// this descended every duplicated weapon in the game called itself IQA-11.
    ///
    /// A segment may also INDEX AN ARRAY -- "Modifiers[0].ModifierMagnitude.ScalableFloatMagnitude
    /// .Value". Names alone could not reach an ability's numbers at all: a gameplay effect keeps
    /// what it does to you in `Modifiers[]`, one struct per attribute it changes, so every value
    /// somebody wants to alter sits behind an array element. That was the whole reason abilities
    /// could not be edited, and it is not specific to them -- an array of structs is how this game
    /// stores most lists of anything.
    ///
    /// Descending needs no new machinery, only the fact that a struct without a native serialiser
    /// is written EXACTLY as an export is: an unversioned header, then its present values in
    /// schema order. So the walk one level down is the same walk, against the struct's schema from
    /// the mappings, begun where the struct's own bytes begin.
    ///
    /// A struct that DOES serialise itself natively -- a tag container, a soft path -- has no such
    /// header, and descending into one reads its contents as a header and returns a range that
    /// means nothing. Nothing here can tell the two apart, so a nested locate is a CLAIM to be
    /// checked by reading the written result back, not a fact. Callers that write through one do.
    /// </summary>
    /// <param name="headerFrom">A real, mounted fallback identity, the way
    /// <see cref="PristineExports"/> takes one -- for a package that is not itself mounted (a
    /// mod's own new part or copy): finds a container at all, and (see <see cref="At"/>) resolves
    /// any export's class, which raw's own invented package name never can.</param>
    public static bool Locate(IFileProvider provider, byte[] raw, string packageName,
                              string exportClass, string property,
                              out int start, out int length, out string? why,
                              string? headerFrom = null)
        => LocateSized(provider, raw, packageName, exportClass, property, out start, out length, out _, out why, headerFrom);

    /// <summary>
    /// <see cref="Locate"/>, and the SIZE FIELDS the walk passed through on its way: the byte
    /// offsets of every instanced struct's size that encloses the range (see <see cref="Inside"/>).
    /// A writer that changes the range's length must add the difference to each of them, or the
    /// value after it is read from the wrong place -- by the game, by CUE4Parse, and by this walk,
    /// which refuses a size that does not match. A writer that keeps the length can ignore them.
    /// </summary>
    public static bool LocateSized(IFileProvider provider, byte[] raw, string packageName,
                                   string exportClass, string property,
                                   out int start, out int length, out List<int> sizes, out string? why,
                                   string? headerFrom = null)
    {
        start = 0;
        length = 0;
        sizes = new List<int>();
        why = null;
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, headerFrom ?? packageName, pristine: true);
            if (container is null)
            { why = $"no mounted container holds {headerFrom ?? packageName}"; return false; }

            var pkg = Open(provider, raw, packageName, container);
            var exports = pkg.ExportsLazy.Select(x => x.Value).ToArray();

            // By the export's own NAME first, then by its class -- the same order and the same
            // reason as Plan: a package can hold several exports of one class, and taking whichever
            // comes first is a silent wrong-target. A caller naming a class still matches on the
            // second test, because no export is NAMED after its class.
            var index = Array.FindIndex(exports, e =>
                e is not null && string.Equals(e.Name, exportClass, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                index = Array.FindIndex(exports, e =>
                    e is not null &&
                    (string.Equals(e.Class?.Name.Text, exportClass, StringComparison.OrdinalIgnoreCase) ||
                     (e.ExportType ?? "").StartsWith(exportClass, StringComparison.OrdinalIgnoreCase)));
            if (index < 0) { why = $"no export matching '{exportClass}'"; return false; }

            return At(provider, raw, packageName, pkg, exports, index, exportClass,
                      property, sizes, out start, out length, out why, headerFrom);
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    /// <summary>
    /// The same walk, against an export named by INDEX rather than by name.
    ///
    /// A LEVEL is why this exists. In an asset a name is a good enough address, but a sublevel
    /// holds fifty components called `StaticMeshComponent0` and four called `Root`, so
    /// <see cref="Locate"/>'s first-match would silently write to somebody else's prop. The
    /// actors are uniquely named and hold a `RootComponent` reference, so the way to the right
    /// bytes is to follow that reference -- which lands on an index, not a name.
    /// </summary>
    public static bool LocateAt(IFileProvider provider, byte[] raw, string packageName, int index,
                                string property, out int start, out int length, out string? why)
    {
        start = 0;
        length = 0;
        why = null;
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null)
            { why = $"no mounted container holds {packageName}"; return false; }

            var pkg = Open(provider, raw, packageName, container);
            var exports = pkg.ExportsLazy.Select(x => x.Value).ToArray();
            if (index < 0 || index >= exports.Length)
            { why = $"export #{index} is outside 0..{exports.Length - 1}"; return false; }

            return At(provider, raw, packageName, pkg, exports, index, $"#{index}",
                      property, new List<int>(), out start, out length, out why);
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    /// <summary>
    /// The tail both addressing modes share, so there is one walk rather than two.
    ///
    /// <paramref name="headerFrom"/>: a real, mounted fallback for the CLASS only, when
    /// <paramref name="raw"/>'s own package cannot resolve it -- a mod's own new part or copy.
    /// IoPackage reads its OWN name from the package's own embedded summary
    /// (CreateFNameFromMappedName(summary.Name), CUE4Parse's IoPackage constructor -- never from
    /// anything <see cref="Open"/> is handed) and resolves an imported class by looking THAT name
    /// up in the container's own store entries; Author's rename has already overwritten it with
    /// the new package's own invented path by the time slots run, which is never a key in any
    /// real container, so no choice of container fixes this -- the lookup key itself is wrong.
    /// MEASURED, not guessed: with headerFrom threaded into CueHeaderFor and even into Open's own
    /// archive name, the class still would not resolve.
    ///
    /// headerFrom's package is unrenamed and real, so a normal load of it resolves its class
    /// correctly, and the SAME export index names the SAME class there -- Author only renames
    /// names, and a slot's own ExportAdd.Append only ever adds a NEW export after it. Only the
    /// SCHEMA is borrowed this way; every OFFSET below still comes from raw/pkg exactly as
    /// before, so a byte range this returns is always a range into the CALLER's own raw, never
    /// into the donor's (which the caller never sees and this never writes back).
    /// </summary>
    private static bool At(IFileProvider provider, byte[] raw, string packageName, IoPackage pkg,
                           UObject[] exports, int index, string label, string property, List<int> sizes,
                           out int start, out int length, out string? why, string? headerFrom = null)
    {
        start = 0;
        length = 0;
        why = null;

        var struc = exports[index].Class?.Object?.Value as UStruct;
        if (struc is null && headerFrom is not null)
        {
            var donor = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(headerFrom)).GetExports().ToArray());
            struc = index < donor.Length ? donor[index].Class?.Object?.Value as UStruct : null;
        }
        if (struc is null)
        { why = $"the '{label}' export's class is not a loaded struct"; return false; }

        var schema = SchemaFor(pkg, struc);
        if (schema is null)
        { why = $"no property schema for {struc.Name}"; return false; }

        var headerSize = (int) BitConverter.ToUInt32(raw, 4);
        var exportStart = headerSize + (int) pkg.ExportMap[index].CookedSerialOffset;
        var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg)
        {
            Position = exportStart,
        };

        return Descend(ar, pkg, schema, SplitPath(property), 0, sizes, out start, out length, out why);
    }

    /// <summary>
    /// A property path cut into its segments at the dots that are NOT inside brackets:
    /// "Fail Text Map[Ability.ActivateFail.TagsMissing]" is one segment, not three.
    ///
    /// A map keyed by gameplay tag is why. The key is the tag's own name, and a tag's name is
    /// dotted -- so splitting on every dot cut the key in half and asked the widget for a property
    /// called "Fail Text Map[Ability", which exists nowhere.
    /// </summary>
    public static string[] SplitPath(string path)
    {
        var parts = new List<string>();
        var depth = 0;
        var from = 0;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '[') depth++;
            else if (path[i] == ']' && depth > 0) depth--;
            else if (path[i] == '.' && depth == 0)
            {
                parts.Add(path[from..i]);
                from = i + 1;
            }
        }
        parts.Add(path[from..]);
        return parts.ToArray();
    }

    /// <summary>
    /// Where an export keeps a property of this name -- as dotted paths <see cref="Locate"/> can
    /// take, one per place it was found.
    ///
    /// Searches the PARSED values rather than the schema, and that is the point rather than a
    /// convenience. A struct is only descendable when it serialises the way an export does, and
    /// nothing in the mappings says which structs those are -- but CUE4Parse has already made the
    /// distinction by the time this runs: a struct it read as tagged properties comes back as an
    /// FStructFallback holding them, and one with a serialiser of its own comes back as its own
    /// type with no property list. So descending exactly where a fallback appears is descending
    /// exactly where the bytes have a header to walk.
    ///
    /// It reports every hit rather than the first. A caller that writes through one of these
    /// should refuse an ambiguous answer: two properties of the same name in one export are two
    /// different things, and picking whichever came first is how the wrong one gets written.
    /// </summary>
    public static List<string> PathsTo(UObject export, string property)
    {
        var found = new List<string>();
        Collect(export.Properties, "");
        return found;

        void Collect(IEnumerable<FPropertyTag> properties, string prefix)
        {
            foreach (var tag in properties)
            {
                var name = tag.Name.Text;
                if (string.Equals(name, property, StringComparison.Ordinal))
                    found.Add(prefix + name);
                if (tag.Tag?.GenericValue is FScriptStruct { StructType: FStructFallback inner })
                    Collect(inner.Properties, prefix + name + ".");
            }
        }
    }

    /// <summary>
    /// The structs in an export whose TYPE declares <paramref name="member"/> and whose cooked
    /// bytes left it out -- as dotted paths <see cref="AddStructMember"/> can take.
    ///
    /// The other half of <see cref="PathsTo"/>. That one finds where a property IS; a cooked
    /// package writes only what differs from the default, so a member an asset never set is in no
    /// parsed value at all -- Resonate's UIData has a Name and simply no Description -- and the
    /// only place it can be found is the struct's schema. Same walk, same rule about descending
    /// only where CUE4Parse read a fallback, and every hit reported for the caller to refuse an
    /// ambiguous answer.
    /// </summary>
    public static List<string> StructsDeclaring(IFileProvider provider, UObject export, string member)
    {
        var found = new List<string>();
        if (provider.MappingsContainer?.MappingsForGame?.Types is not { } types) return found;
        Collect(export.Properties, "");
        return found;

        void Collect(IEnumerable<FPropertyTag> properties, string prefix)
        {
            foreach (var tag in properties)
            {
                if (tag.Tag?.GenericValue is not FScriptStruct { StructType: FStructFallback inner }) continue;
                var path = prefix + tag.Name.Text;

                if (tag.TagData?.StructType is { } type && types.TryGetValue(type, out var schema) &&
                    schema is not null && Declares(schema, member) &&
                    !inner.Properties.Any(p => string.Equals(p.Name.Text, member, StringComparison.Ordinal)))
                    found.Add(path);

                Collect(inner.Properties, path + ".");
            }
        }

        static bool Declares(Struct schema, string name)
        {
            var total = schema.CountProperties(true);
            for (var i = 0; i < total; i++)
                if (schema.TryGetValue(i, out var info) && string.Equals(info.Name, name, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// One level of <see cref="Locate"/>'s walk: find <c>path[depth]</c> in this schema, then
    /// either hand back its byte range or step INSIDE it and look for the next segment.
    ///
    /// The archive must be positioned at the first byte of the unversioned header this schema
    /// describes -- the export's first byte at the top level, the struct property's first byte
    /// below that.
    /// </summary>
    private static bool Descend(FAssetArchive ar, IoPackage pkg, Struct schema, string[] path,
                                int depth, List<int> sizes, out int start, out int length, out string? why)
    {
        start = 0;
        length = 0;
        why = null;

        var (want, key) = Bracketed(path[depth]);

        // A NAME WITH A BRACKET IN IT is still a name when the schema has one spelled that way --
        // tried first, so an entry is only ever looked for in a property that exists.
        if (key is not null && SlotOf(schema, path[depth]) >= 0) (want, key) = (path[depth], null);

        var wanted = SlotOf(schema, want);
        if (wanted < 0) { why = $"'{want}' is not a property of {schema.Name}"; return false; }

        var header = new FUnversionedHeader(ar);
        if (!header.HasValues) { why = $"{schema.Name} serialises no properties"; return false; }

        using var it = new FIterator(header);
        do
        {
            var (val, isNonZero) = it.Current;
            if (!isNonZero)
            {
                // A zero-valued property occupies no bytes at all, so it can be pointed at
                // but not spliced over.
                if (val == wanted)
                { why = $"'{want}' is serialised as zero and has no bytes"; return false; }
                continue;
            }
            if (!schema.TryGetValue(val, out var info))
            { why = $"property {val} is not in the mappings"; return false; }

            var at = (int) ar.Position;
            _ = new FPropertyTag(ar, info, ReadType.NORMAL);
            if (val != wanted) continue;

            // ONE ENTRY OF A MAP, by its key. Reading the property left the archive at the map's
            // end, which is what the entry-by-entry walk has to arrive at to be believed.
            if (key is not null &&
                string.Equals(info.MappingType.Type, "MapProperty", StringComparison.Ordinal))
                return Entry(ar, pkg, info, at, (int) ar.Position, want, key, path, depth, sizes,
                             out start, out length, out why);

            // ONE ITEM OF A LIST. Reading the property left the archive past the whole array, so
            // this goes back to where its bytes began and counts elements forward.
            if (key is not null)
            {
                if (!int.TryParse(key, System.Globalization.NumberStyles.None,
                                  System.Globalization.CultureInfo.InvariantCulture, out var index))
                {
                    why = $"'{want}' is a {info.MappingType.Type}: only a map is looked up by a key, " +
                          $"and a list is numbered, so '[{key}]' names nothing in it";
                    return false;
                }
                return Element(ar, pkg, info, at, want, index, path, depth, sizes,
                               out start, out length, out why);
            }

            if (depth == path.Length - 1)
            {
                start = at;
                length = (int) ar.Position - at;
                return true;
            }

            // INSIDE it. Reading the property moved the archive past the whole struct, so the
            // descent rewinds to where its bytes began -- which is where its own header sits.
            if (info.MappingType.StructType is not { } inner)
            {
                why = $"'{want}' is a {info.MappingType.Type} and not a struct, so " +
                      $"'{Segment(path[depth + 1]).Name}' cannot be inside it";
                return false;
            }
            if (Inside(ar, pkg, inner, at, (int) ar.Position, sizes, out var innerSchema, out why) is not { } innerAt)
                return false;

            ar.Position = innerAt;
            return Descend(ar, pkg, innerSchema!, path, depth + 1, sizes, out start, out length, out why);
        } while (it.MoveNext());

        why = $"{schema.Name} does not currently carry '{want}'";
        return false;
    }

    /// <summary>
    /// One path segment split into a name and, if it has one, the array index it asks for:
    /// "Modifiers[0]" -> ("Modifiers", 0), "DisplayName" -> ("DisplayName", -1). A bracket that
    /// holds no whole number is a map KEY, so its name is still the part before it, with no index.
    /// </summary>
    private static (string Name, int Index) Segment(string raw)
    {
        var (name, key) = Bracketed(raw);
        return int.TryParse(key, out var n) && n >= 0 ? (name, n) : (name, -1);
    }

    /// <summary>
    /// One path segment split into a name and whatever its brackets hold:
    /// "Fail Text Map[Ability.ActivateFail.TagsMissing]" -> ("Fail Text Map", "Ability.ActivateFail.TagsMissing"),
    /// "Modifiers[0]" -> ("Modifiers", "0"), "DisplayName" -> ("DisplayName", null).
    ///
    /// What the brackets MEAN is the property's to decide, not the text's: a list is numbered and
    /// a map is looked up by key, and a map keyed by number (a campaign arc's turns) makes "[3]"
    /// either. So this only cuts; <see cref="Descend"/> reads the property's type and chooses.
    /// </summary>
    private static (string Name, string? Key) Bracketed(string raw)
    {
        var open = raw.IndexOf('[');
        if (open <= 0 || !raw.EndsWith("]", StringComparison.Ordinal)) return (raw, null);
        return (raw[..open], raw[(open + 1)..^1]);
    }

    /// <summary>The schema slot of the property called <paramref name="name"/>, or -1.</summary>
    private static int SlotOf(Struct schema, string name)
    {
        var total = schema.CountProperties(true);
        for (var i = 0; i < total; i++)
            if (schema.TryGetValue(i, out var slot) && string.Equals(slot.Name, name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>
    /// The byte range of one VALUE of a map, found by its key -- or the walk continued inside it.
    ///
    /// The radial ability menu is why. Its `Fail Text Map` says, per failure tag, what the wheel
    /// prints above an ability it will not let you use, and the entry for a missing tag points at
    /// a string-table key the game never wrote -- which is the red "MISSING STRING TABLE ENTRY"
    /// over Force Drain. That entry is the only thing to change, and nothing could address it.
    ///
    /// A cooked map is a count of keys REMOVED from the parent's map (always none in what this was
    /// written for, and refused otherwise, as the arc walk refuses it), a count of entries, then
    /// each key and its value back to back. Nothing records where an entry ends, so every entry up
    /// to the wanted one is READ, with the call UScriptMap itself makes -- a property tag built from
    /// the key or value type in the mappings, read as <see cref="ReadType.MAP"/>.
    ///
    /// THE WALK HAS TO END WHERE THE MAP DOES. <paramref name="end"/> is where reading the whole
    /// property stopped; a walk that reads one entry at the wrong size arrives somewhere else, and
    /// is refused rather than trusted -- the range it found would be the right size in the wrong
    /// place, and the writer's read-back walks the same way and cannot see that.
    ///
    /// A key is matched by its WORDS (<see cref="KeyText"/>): a gameplay tag by its name, a number
    /// by its digits. Byte keys and values are refused for the reason arrays refuse them.
    /// </summary>
    private static bool Entry(FAssetArchive ar, IoPackage pkg, PropertyInfo info, int at, int end,
                              string want, string key, string[] path, int depth, List<int> sizes,
                              out int start, out int length, out string? why)
    {
        start = 0;
        length = 0;

        if (MapSlots(ar, info, at, end, want, out why) is not { } slots) return false;

        var hits = slots.Where(s => string.Equals(s.Key, key, StringComparison.Ordinal)).ToList();
        if (hits.Count == 0)
        {
            why = $"'{want}' has no key '{key}' -- its keys are " +
                  string.Join(", ", slots.Take(20).Select(s => s.Key)) +
                  (slots.Count > 20 ? $", and {slots.Count - 20} more" : "");
            return false;
        }
        if (hits.Count > 1)
        { why = $"'{want}' has {hits.Count} keys that read as '{key}', so it names no one entry"; return false; }
        var hit = hits[0];

        if (depth == path.Length - 1)
        {
            start = hit.ValueStart;
            length = hit.ValueEnd - hit.ValueStart;
            return true;
        }

        var valueType = info.MappingType.ValueType!;
        if (valueType.StructType is not { } innerStruct)
        {
            why = $"'{want}[{key}]' is a {valueType.Type} and not a struct, so " +
                  $"'{Segment(path[depth + 1]).Name}' cannot be inside it";
            return false;
        }
        if (Inside(ar, pkg, innerStruct, hit.ValueStart, hit.ValueEnd, sizes, out var innerSchema, out why)
            is not { } innerAt)
            return false;

        ar.Position = innerAt;
        return Descend(ar, pkg, innerSchema!, path, depth + 1, sizes, out start, out length, out why);
    }

    /// <summary>
    /// Where a struct value's own bytes begin, and the schema they follow -- the step every
    /// descent makes, from a property, a list item or a map value.
    ///
    /// A plain struct IS its bytes: an unversioned header, then its values. An INSTANCED struct
    /// (FInstancedStruct: a slot that can hold a struct of any type, which is what UITagData's bank
    /// holds per tag) has a serialiser of its own: a reference to the struct type it holds, that
    /// struct's SIZE in bytes, then the struct. So its schema is the one the reference names, its
    /// bytes start eight in -- and the size is a promise about them. Readers jump by it (CUE4Parse
    /// sets its position to start + size whatever it read), so a write that changes the length
    /// inside must change the size too: its offset is added to <paramref name="sizes"/> for the
    /// writer. A size that does not reach exactly <paramref name="end"/> is refused here, which is
    /// also how a stale one is caught when a write is read back.
    /// </summary>
    private static int? Inside(FAssetArchive ar, IoPackage pkg, string structType, int at, int end,
                               List<int> sizes, out Struct? schema, out string? why)
    {
        schema = null;
        why = null;
        if (pkg.Mappings is not { } m) { why = "the package has no mappings"; return null; }

        if (!string.Equals(structType, "InstancedStruct", StringComparison.Ordinal))
        {
            if (!m.Types.TryGetValue(structType, out schema) || schema is null)
            { why = $"the mappings do not describe '{structType}'"; return null; }
            return at;
        }

        ar.Position = at;
        var held = new FPackageIndex(ar);
        var size = ar.Read<int>();
        if (held.IsNull || held.ResolvedObject?.Name.Text is not { } name)
        { why = "an instanced struct here holds nothing, or a type that cannot be resolved"; return null; }
        if (at + 8 + size != end)
        {
            why = $"an instanced {name} here says it is {size} byte(s), but its value runs {end - at - 8} -- " +
                  "a size nothing corrected after a write";
            return null;
        }
        if (!m.Types.TryGetValue(name, out schema) || schema is null)
        { why = $"the mappings do not describe '{name}', the struct an instanced struct here holds"; return null; }

        sizes.Add(at + 4);
        return at + 8;
    }

    /// <summary>One entry of a cooked map: what its key says, and where its key and value lie.</summary>
    private sealed record MapSlot(string Key, object? KeyValue, int KeyStart, int ValueStart, int ValueEnd);

    /// <summary>
    /// Every entry of a map, read in place -- the walk <see cref="Entry"/> and
    /// <see cref="AddMapEntry"/> both make, so the two cannot disagree about where an entry is.
    /// <paramref name="at"/> is the map property's first byte and <paramref name="end"/> the byte
    /// after its last; null, with the reason, when the walk does not arrive exactly there.
    /// </summary>
    private static List<MapSlot>? MapSlots(FAssetArchive ar, PropertyInfo info, int at, int end,
                                           string want, out string? why)
    {
        why = null;

        if (info.MappingType.InnerType is not { } keyType || info.MappingType.ValueType is not { } valueType)
        { why = $"the mappings do not say what '{want}' maps from and to"; return null; }
        if (string.Equals(keyType.Type, "ByteProperty", StringComparison.Ordinal) ||
            string.Equals(valueType.Type, "ByteProperty", StringComparison.Ordinal))
        { why = $"'{want}' holds bytes, which a map reads a different way -- not addressable here"; return null; }

        ar.Position = at;
        var removed = ar.Read<int>();
        if (removed != 0)
        { why = $"'{want}' removes {removed} key(s) from its parent's, a shape this does not walk"; return null; }
        var count = ar.Read<int>();
        if (count < 0 || count > end - ar.Position)
        { why = $"'{want}' says it holds {count} entries, which the package has no room for"; return null; }

        var keyInfo = new PropertyInfo(0, want, keyType);
        var valueInfo = new PropertyInfo(0, want, valueType);
        var slots = new List<MapSlot>(count);
        for (var i = 0; i < count; i++)
        {
            var keyStart = (int) ar.Position;
            var read = new FPropertyTag(ar, keyInfo, ReadType.MAP);
            var valueStart = (int) ar.Position;
            _ = new FPropertyTag(ar, valueInfo, ReadType.MAP);
            slots.Add(new MapSlot(KeyText(read.Tag?.GenericValue), read.Tag?.GenericValue,
                                  keyStart, valueStart, (int) ar.Position));
        }

        if ((int) ar.Position != end)
        {
            why = $"reading '{want}' entry by entry ended {end - (int) ar.Position} byte(s) away from " +
                  "where the map does, so an entry was read at the wrong size";
            return null;
        }
        return slots;
    }

    /// <summary>
    /// A map key in the words an author would write it in. A struct of ONE member is that member
    /// -- a gameplay tag is a struct holding only its TagName, and nobody writes a tag as a struct
    /// -- a name is its text, and a number is its invariant digits.
    /// </summary>
    internal static string KeyText(object? value) => value switch
    {
        FScriptStruct { StructType: FStructFallback one } when one.Properties.Count == 1
            => KeyText(one.Properties[0].Tag?.GenericValue),
        FName name => name.Text,
        IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "",
    };

    /// <summary>
    /// The name a map key IS underneath -- an FName, or a struct of one member holding one (a
    /// gameplay tag) -- or null when the key is something else.
    /// </summary>
    private static FName? NameIn(object? value) => value switch
    {
        FScriptStruct { StructType: FStructFallback one } when one.Properties.Count == 1
            => NameIn(one.Properties[0].Tag?.GenericValue),
        FName name => name,
        _ => null,
    };

    /// <summary>
    /// The mappings' description of the property a path names: its type, and for a map what it
    /// maps from and to. The last segment names the property itself, so it carries no bracket.
    /// </summary>
    private static PropertyInfo? InfoAt(IoPackage pkg, Struct schema, string path, out string? why)
    {
        why = null;
        var parts = SplitPath(path);
        var owner = parts.Length == 1 ? schema : SchemaAt(pkg, schema, string.Join('.', parts[..^1]), out why);
        if (owner is null) return null;
        var slot = SlotOf(owner, parts[^1]);
        if (slot < 0 || !owner.TryGetValue(slot, out var info))
        { why = $"'{parts[^1]}' is not a property of {owner.Name}"; return null; }
        return info;
    }

    /// <summary>
    /// Give a map one entry MORE: a copy of the entry under <paramref name="fromKey"/>, under
    /// <paramref name="toKey"/>, after the last.
    ///
    /// Force Grip's status is why. What the game prints for a status -- "Drained", its description,
    /// its icon -- is not in the effect at all: the effect names a TAG, and the words are that tag's
    /// entry in a UI table (UITagData_Effects' TagDataBank, keyed by tag). Force Grip applied the
    /// Coil Seer's own effect and so wore the Seer's words. Its own copy of the effect names a tag
    /// of the mod's, and that tag needs a line in the table: copying the Seer's line gives it the
    /// same icon and shape, and the words are then set like any other text.
    ///
    /// ONLY THE KEY IS NEW. The one to copy must be there and the new one must NOT be -- two
    /// entries under one key is not a map. The key has to be a NAME underneath (a gameplay tag, or
    /// an FName): the copied key's bytes hold that name as an index into the package's name table,
    /// and the new key is those bytes with the index changed. The new name is appended to the
    /// table first when the package lacks it, which moves every offset after the table -- so, as
    /// in <see cref="SetName"/>, everything is located against the GROWN package.
    ///
    /// Read back through the same walk, or refuse: the new entry is found by its key and holds the
    /// copied value byte for byte, and the copied entry is still there, unchanged.
    /// </summary>
    public static byte[] AddMapEntry(IFileProvider provider, byte[] raw, string packageName, string exportClass,
                                     string mapPath, string fromKey, string toKey, out string? why)
    {
        why = null;
        try
        {
            if (string.Equals(fromKey, toKey, StringComparison.Ordinal))
            { why = $"the new key is the one being copied, '{toKey}'"; return raw; }

            var (toStem, toNumber) = ZenPackage.ParseNumber(toKey);
            var toName = toStem ?? toKey;
            var names = ZenPackage.Read(raw);
            if (names.Names.IndexOf(toName) < 0)
            {
                names.AppendName(toName);
                return AddMapEntry(provider, names.Write(), packageName, exportClass, mapPath, fromKey, toKey, out why);
            }
            var toIndex = names.Names.IndexOf(toName);
            var (fromStem, fromNumber) = ZenPackage.ParseNumber(fromKey);
            var fromIndex = names.Names.IndexOf(fromStem ?? fromKey);
            if (fromIndex < 0)
            { why = $"'{fromKey}' is not a name this package holds, so no key of it can be copied"; return raw; }

            // THE MAP, and what the mappings say it maps from and to.
            if (!Locate(provider, raw, packageName, exportClass, mapPath, out var mapAt, out var mapLength, out why))
                return raw;
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null) { why = $"no mounted container holds {packageName}"; return raw; }
            var pkg = Open(provider, raw, packageName, container);
            if (FindExport(pkg.ExportsLazy.Select(x => x.Value).ToArray(), exportClass) is not { } export)
            { why = $"no export matching '{exportClass}'"; return raw; }
            if (export.Class?.Object?.Value is not UStruct struc || SchemaFor(pkg, struc) is not { } schema)
            { why = $"the '{exportClass}' export has no property schema"; return raw; }
            if (InfoAt(pkg, schema, mapPath, out why) is not { } info) return raw;
            if (!string.Equals(info.MappingType.Type, "MapProperty", StringComparison.Ordinal))
            { why = $"'{mapPath}' is a {info.MappingType.Type}, not a map"; return raw; }

            var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg);
            if (MapSlots(ar, info, mapAt, mapAt + mapLength, mapPath, out why) is not { } slots) return raw;
            if (slots.Any(s => string.Equals(s.Key, toKey, StringComparison.Ordinal)))
            { why = $"'{mapPath}' already has a key '{toKey}'"; return raw; }
            var sources = slots.Where(s => string.Equals(s.Key, fromKey, StringComparison.Ordinal)).ToList();
            if (sources.Count != 1)
            {
                why = sources.Count == 0
                    ? $"'{mapPath}' has no key '{fromKey}' to copy"
                    : $"'{mapPath}' has {sources.Count} keys that read as '{fromKey}', so it names no one entry";
                return raw;
            }
            var source = sources[0];

            // THE KEY IS A NAME UNDERNEATH, or nothing here can say where in its bytes the name sits.
            if (NameIn(source.KeyValue) is null)
            {
                why = $"'{mapPath}' is keyed by {info.MappingType.InnerType?.StructType ?? info.MappingType.InnerType?.Type}; " +
                      "only a key that is a name (a gameplay tag, an FName) can be given a new one here";
                return raw;
            }

            var key = raw.AsSpan(source.KeyStart, source.ValueStart - source.KeyStart).ToArray();
            var was = new byte[8];
            BitConverter.GetBytes((uint) fromIndex).CopyTo(was, 0);
            BitConverter.GetBytes(fromNumber).CopyTo(was, 4);
            var at = new List<int>();
            for (var i = 0; i + 8 <= key.Length; i++)
                if (key.AsSpan(i, 8).SequenceEqual(was)) at.Add(i);
            if (at.Count != 1)
            {
                why = $"the name '{fromKey}' is in its key's {key.Length} bytes {at.Count} times, " +
                      "so which one makes it the key cannot be told";
                return raw;
            }
            BitConverter.GetBytes((uint) toIndex).CopyTo(key, at[0]);
            BitConverter.GetBytes(toNumber).CopyTo(key, at[0] + 4);

            // The map again: its removed-count, one entry more, every old entry as it was, the new
            // one last.
            var value = raw.AsSpan(source.ValueStart, source.ValueEnd - source.ValueStart).ToArray();
            var grown = new byte[mapLength + key.Length + value.Length];
            raw.AsSpan(mapAt, mapLength).CopyTo(grown);
            BitConverter.GetBytes(slots.Count + 1).CopyTo(grown, 4);
            key.CopyTo(grown, mapLength);
            value.CopyTo(grown, mapLength + key.Length);
            var written = ZenPackage.SpliceExportData(raw, mapAt, mapLength, grown);

            if (!Locate(provider, written, packageName, exportClass, $"{mapPath}[{toKey}]",
                        out var newAt, out var newLength, out var newWhy))
            { why = $"[{toKey}] cannot be found after the write: {newWhy}"; return raw; }
            if (!written.AsSpan(newAt, newLength).SequenceEqual(value))
            { why = $"[{toKey}] reads back as {newLength} byte(s) that are not the {value.Length} copied"; return raw; }
            if (!Locate(provider, written, packageName, exportClass, $"{mapPath}[{fromKey}]",
                        out var oldAt, out var oldLength, out var oldWhy))
            { why = $"[{fromKey}] was lost: {oldWhy}"; return raw; }
            if (!written.AsSpan(oldAt, oldLength).SequenceEqual(value))
            { why = $"[{fromKey}] changed"; return raw; }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// The byte range of one element of an array, or the walk continued INSIDE that element.
    ///
    /// <paramref name="at"/> is the first byte of the array property itself, which is a count
    /// followed by the elements back to back. NOTHING RECORDS AN ELEMENT'S LENGTH -- a struct
    /// element is written exactly as an export is, header then values -- so the only way to the
    /// element that is wanted is to read the ones before it and see where each ends.
    ///
    /// The reading is done with the same call UScriptArray itself makes: a
    /// <see cref="FPropertyTag"/> built from the INNER type in the mappings, read as
    /// <see cref="ReadType.ARRAY"/>. That matters -- an element is not serialised the way a
    /// property of that type would be at the top level, and reading it as one walks off into the
    /// middle of the next element without complaining.
    ///
    /// A BYTE array is refused rather than indexed. UScriptArray decides between a byte and an
    /// enum for those by sniffing the bytes ahead (TestReadFName), which this does not do, so the
    /// two readings would diverge here and nowhere else. Nothing anybody wants to edit is in one,
    /// and a refusal that names the reason is worth more than a wrong offset.
    /// </summary>
    private static bool Element(FAssetArchive ar, IoPackage pkg, PropertyInfo info, int at,
                                string want, int index, string[] path, int depth, List<int> sizes,
                                out int start, out int length, out string? why)
    {
        start = 0;
        length = 0;
        why = null;

        if (!string.Equals(info.MappingType.Type, "ArrayProperty", StringComparison.Ordinal))
        { why = $"'{want}' is a {info.MappingType.Type}, and only an array can be indexed"; return false; }

        if (info.MappingType.InnerType is not { } inner)
        { why = $"the mappings do not say what '{want}' holds"; return false; }

        if (string.Equals(inner.Type, "ByteProperty", StringComparison.Ordinal))
        { why = $"'{want}' holds bytes, which an array reads a different way -- not indexable here"; return false; }

        ar.Position = at;
        var count = ar.Read<int>();
        if (count < 0 || count > ar.Length - ar.Position)
        { why = $"'{want}' says it holds {count} item(s), which the package has no room for"; return false; }
        if (index >= count)
        { why = $"'{want}' holds {count} item(s), so [{index}] is not there"; return false; }

        var elemInfo = new PropertyInfo(0, want, inner);
        var elemStart = (int) ar.Position;
        for (var i = 0; i <= index; i++)
        {
            elemStart = (int) ar.Position;
            _ = new FPropertyTag(ar, elemInfo, ReadType.ARRAY);
        }
        var elemEnd = (int) ar.Position;

        if (depth == path.Length - 1)
        {
            start = elemStart;
            length = elemEnd - elemStart;
            return true;
        }

        if (inner.StructType is not { } innerStruct)
        {
            why = $"'{want}[{index}]' is a {inner.Type} and not a struct, so " +
                  $"'{Segment(path[depth + 1]).Name}' cannot be inside it";
            return false;
        }
        if (Inside(ar, pkg, innerStruct, elemStart, elemEnd, sizes, out var innerSchema, out why) is not { } innerAt)
            return false;

        ar.Position = innerAt;
        return Descend(ar, pkg, innerSchema!, path, depth + 1, sizes, out start, out length, out why);
    }

    // ------------------------------------------------------------------ planning

    private sealed record Insertion(int ExportStart, int HeaderEnd, int InsertAt, byte[] NewHeader,
                                    Dictionary<string, string> Before, string Type);

    /// <summary>
    /// EVERY PROPERTY AN EXPORT'S CLASS DECLARES, whether or not the cooked file carries it.
    ///
    /// This is the difference between what an asset SAYS and what it CAN say, and the gap is
    /// where a whole feature was hiding. A cooked package serialises only the properties whose
    /// values differ from the class default, so reading one tells you nothing about the rest --
    /// <c>GE_Cooldown_RocketStrike</c> reads as a tag and a -1 duration and looks like an asset
    /// with nothing in it to change, while its class declares <c>UsesPerCooldown</c> as its very
    /// first property. Eighteen of the game's own cooldown effects set that one to 2, 3 or 4; the
    /// once-per-encounter ones simply leave it at the default, so it is absent rather than absent-
    /// minded, and <see cref="AddScalar"/> has been able to put a property back for a year.
    ///
    /// So the list is the answer to "what else could this asset carry", and the reason a request
    /// to "make cooldown effects editable" turned out to be answerable rather than a refusal.
    ///
    /// The order is the schema's own, which is worth keeping: a Blueprint class's own properties
    /// come first and the engine's inherited ones after, so the interesting few are at the top.
    /// </summary>
    public static (string Name, string Type)[] Declared(IFileProvider provider, byte[] raw,
                                                        string packageName, string exportName,
                                                        out string? why, string? headerFrom = null)
    {
        why = null;
        try
        {
            var schema = ClassSchema(provider, raw, packageName, exportName, out _, out _, out why,
                                     headerFrom);
            if (schema is null) return [];

            var total = schema.CountProperties(true);
            var found = new List<(string, string)>();
            for (var i = 0; i < total; i++)
                if (schema.TryGetValue(i, out var info) && info.Name is { Length: > 0 })
                    found.Add((info.Name, info.MappingType.Type ?? ""));
            return found.ToArray();
        }
        catch (Exception e) { why = e.Message; return []; }
    }

    /// <summary>
    /// The TAG CONTAINERS an export's class declares, whether or not the cooked file carries them.
    ///
    /// <see cref="Declared"/> answers with a type NAME, and for a container that name is
    /// "StructProperty" -- which says nothing about which struct. The mappings do carry it, in
    /// `MappingType.StructType`, so asking that question needs its own pass rather than a wider
    /// return from Declared that every existing caller would have to start ignoring.
    ///
    /// The point of it is the same as Declared's: a cooked package omits every property still at
    /// its class default, so an ability that declares six containers and sets none reads as
    /// having no containers at all. <see cref="AddTagContainer"/> can put any of them back.
    /// </summary>
    public static string[] DeclaredContainers(IFileProvider provider, byte[] raw,
                                              string packageName, string exportName,
                                              out string? why)
    {
        why = null;
        try
        {
            var schema = ClassSchema(provider, raw, packageName, exportName, out _, out _, out why);
            if (schema is null) return [];

            var total = schema.CountProperties(true);
            var found = new List<string>();
            for (var i = 0; i < total; i++)
                if (schema.TryGetValue(i, out var info) && info.Name is { Length: > 0 } &&
                    info.MappingType.Type == "StructProperty" &&
                    string.Equals(info.MappingType.StructType, "GameplayTagContainer",
                                  StringComparison.Ordinal))
                    found.Add(info.Name);
            return found.ToArray();
        }
        catch (Exception e) { why = e.Message; return []; }
    }

    /// <summary>
    /// The property schema of one export, and the package and index it was found through.
    ///
    /// Lifted out of <see cref="Plan"/> unchanged when <see cref="Declared"/> needed the same
    /// three-step resolution -- find the export by name then by class, take its LOADED class, and
    /// fall back to the mappings for a native one. Two copies of that would be two places for the
    /// null-guard on a widget tree's unconstructable exports to be forgotten.
    /// </summary>
    private static Struct? ClassSchema(IFileProvider provider, byte[] raw, string packageName,
                                       string exportClass, out IoPackage? pkg, out int index,
                                       out string? why, string? headerFrom = null)
    {
        why = null;
        pkg = null;
        index = -1;

        // THE HEADER NEED NOT BE THIS PACKAGE'S, and until a mod authored a package of its own
        // that was never in question: every caller here edits a GAME asset, which is mounted.
        //
        // A container header carries the container's context, not the asset's meaning, so a
        // BRAND NEW package -- one this tool invented, which no container holds -- can be opened
        // against the header of the template it was retyped from. Without that the schema lookup
        // fails on a package that is perfectly well formed, with "no mounted container holds it",
        // and the edit is refused for a reason that is true and beside the point.
        var container = AssetCopy.CueHeaderFor(provider, headerFrom ?? packageName, pristine: true);
        if (container is null)
        { why = $"no mounted container holds {headerFrom ?? packageName}, so its schema cannot be resolved"; return null; }

        pkg = Open(provider, raw, packageName, container);
        var exports = pkg.ExportsLazy.Select(x => x.Value).ToArray();

        // By the export's own NAME first, then by its class. A widget package holds half a dozen
        // SizeBoxes and only one of them wraps the list being fixed; matching on class alone would
        // take whichever happened to come first and report success, which is exactly the kind of
        // silent wrong-target this file exists to refuse.
        // Null-guarded because a big package -- a widget tree above all -- has exports this
        // reader cannot construct, and they arrive as nulls in the middle of the list.
        index = Array.FindIndex(exports, e =>
            e is not null && string.Equals(e.Name, exportClass, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = Array.FindIndex(exports, e =>
                e is not null &&
                (string.Equals(e.Class?.Name.Text, exportClass, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e.ExportType, exportClass, StringComparison.OrdinalIgnoreCase)));
        if (index < 0)
        { why = $"no export named or classed '{exportClass}' in {packageName}"; return null; }

        // The LOADED class, not the reference to it: the schema comes from the struct's own
        // property list, which is the same thing UObject.Deserialize insists on before it will
        // read an unversioned export at all.
        if (exports[index].Class?.Object?.Value is not UStruct struc)
        { why = $"the '{exportClass}' export's class is not a loaded struct, so it has no schema"; return null; }

        // A BLUEPRINT class carries its own property list, read out of the package. A NATIVE one --
        // SizeBox, OverlaySlot, anything under /Script/ -- arrives as a UScriptClass stub whose
        // ChildProperties is NULL, and building a SerializedStruct from it dereferences that null
        // before the constructor's first line finishes. For those the schema is the mappings' own
        // entry, which is the same thing `--schema` prints.
        if (struc is UScriptClass || struc.ChildProperties is null)
        {
            if (provider.MappingsContainer?.MappingsForGame is not { } native ||
                !native.Types.TryGetValue(struc.Name, out var fromMappings) || fromMappings is null)
            { why = $"'{struc.Name}' is a native class and is not in the mappings, so it has no schema"; return null; }
            return fromMappings;
        }
        return new SerializedStruct(pkg.Mappings, struc);
    }

    private static Insertion? Plan(IFileProvider provider, byte[] raw, string packageName,
                                   string exportClass, string property, out string? why,
                                   string? headerFrom = null)
    {
        var schema = ClassSchema(provider, raw, packageName, exportClass, out var pkg, out var index,
                                 out why, headerFrom);
        if (schema is null || pkg is null) return null;

        var total = schema.CountProperties(true);
        var wanted = -1;
        for (var i = 0; i < total; i++)
            if (schema.TryGetValue(i, out var info) &&
                string.Equals(info.Name, property, StringComparison.Ordinal)) { wanted = i; break; }
        if (wanted < 0)
        {
            // The near misses, because "not a property" is unhelpful when the real answer is
            // almost always a spelling or a property that lives on a different export.
            var offered = new List<string>();
            for (var i = 0; i < total && offered.Count < 40; i++)
                if (schema.TryGetValue(i, out var info)) offered.Add($"{i}:{info.Name}");
            why = $"'{property}' is not a property of {schema.Name}. It has: {string.Join(", ", offered)}";
            return null;
        }

        var headerSize = (int) BitConverter.ToUInt32(raw, 4);
        var exportStart = headerSize + (int) pkg.ExportMap[index].CookedSerialOffset;

        // Read the header and then walk the values, which is the only way to know where one ends.
        var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg)
        {
            Position = exportStart,
        };
        var header = new FUnversionedHeader(ar);
        var headerEnd = (int) ar.Position;

        var present = new List<(int Index, bool Zero)>();
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        var insertAt = -1;
        var raising = false;

        if (header.HasValues)
        {
            using var it = new FIterator(header);
            do
            {
                var (val, isNonZero) = it.Current;
                if (val == wanted && isNonZero)
                { why = $"{schema.Name} already carries '{property}' - this adds one that is absent"; return null; }

                // A PROPERTY THAT IS PRESENT AND ZERO, which is a third state this refused for a
                // year and should not have. Unversioned serialisation has three of them, not two:
                // absent from the header entirely, present with bytes, and present with the zero
                // bit set and NO bytes. The last one read as "'X' is serialised as zero and has no
                // bytes" from Locate and "already carries it" from here, so an author was told
                // both that it could not be written over and that it could not be added -- for
                // BlastBaseDamage on a rocket ability, among a great many others.
                //
                // Raising one is the same edit as adding one with the zero bit cleared: the header
                // already distinguishes the two states (see BuildHeader's zero mask) and the value
                // goes exactly where the walk has reached, because a zero property occupies no
                // bytes and the values after it are the ones already at this position.
                if (val == wanted)
                {
                    raising = true;
                    insertAt = (int) ar.Position;
                    present.Add((val, false));
                    continue;
                }

                // The FIRST property whose index is above the new one is where the new value goes:
                // values are written in schema order and nothing separates them.
                if (val > wanted && insertAt < 0) insertAt = (int) ar.Position;

                present.Add((val, !isNonZero));
                if (isNonZero && schema.TryGetValue(val, out var info))
                {
                    var tag = new FPropertyTag(ar, info, ReadType.NORMAL);
                    before[info.Name] = tag.Tag?.GenericValue?.ToString() ?? "(null)";
                }
                else if (isNonZero)
                { why = $"{schema.Name}: property {val} is not in the mappings, so its size is unknown"; return null; }
            } while (it.MoveNext());
        }

        // Nothing above it: the value goes after every existing one, which is the end of the
        // export MINUS whatever the export writes after its properties. Taken from the export map
        // rather than from where the walk stopped, because appending inside that trailer would be
        // silent -- and for a year this refused every export that had one, which was 1,062 of the
        // game's 7,279 ability numbers.
        if (insertAt < 0)
        {
            var end = exportStart + (int) pkg.ExportMap[index].CookedSerialSize;
            var after = end - (int) ar.Position;
            var trailer = TailSize(pkg, index, raw, (int) ar.Position, after);
            if (trailer < 0)
            { why = $"{schema.Name} has {after} byte(s) after its properties that this " +
                    "does not understand, so where the value belongs is a guess"; return null; }
            insertAt = end - trailer;
        }

        if (!raising) present.Add((wanted, false));
        schema.TryGetValue(wanted, out var wantedInfo);
        return new Insertion(exportStart, headerEnd, insertAt, BuildHeader(present), before,
                             wantedInfo?.MappingType.Type ?? "");
    }

    /// <summary>
    /// How many bytes an export writes AFTER its properties, or -1 when that is not understood.
    ///
    /// UObject.Serialize does not stop at the property block, and this is where a whole class of
    /// refusals came from: a plan that could not say where the properties ended could not say
    /// where a new value belonged, so it gave up. 1,062 of the game's ability numbers -- every
    /// GA_BrutalStrike tier, every GA_FleetOfFoot tier, GE_DestructibleHealthInitialize -- were
    /// refused for four bytes.
    ///
    /// The four bytes are not a mystery. UObject.Deserialize does exactly two things after the
    /// properties, and which one depends on whether the export is a class default object:
    ///
    ///  * NOT a CDO: an int32 "has a guid", and sixteen more bytes when it says yes.
    ///  * A CDO whose class is a BlueprintGeneratedClass: an FPackageIndex naming the sparse class
    ///    data struct -- four bytes, and null in everything here.
    ///
    /// It is checked against the size actually left over rather than assumed, so an export that
    /// carries something else still refuses. That is the point: the refusal was right, it was
    /// only too broad.
    /// </summary>
    private static int TailSize(IoPackage pkg, int index, byte[] raw, int at, int after)
    {
        if (after == 0) return 0;
        if (after < 0 || at + 4 > raw.Length) return -1;

        var export = pkg.ExportsLazy[index].Value;
        if (export is null) return -1;

        if (!export.Flags.HasFlag(EObjectFlags.RF_ClassDefaultObject))
        {
            // The guid flag, then the guid. Read rather than assumed: an export that says it has
            // one and is four bytes long is an export this has misunderstood.
            var hasGuid = BitConverter.ToInt32(raw, at);
            if (hasGuid == 0 && after == 4) return 4;
            if (hasGuid == 1 && after == 20) return 20;
            return -1;
        }

        // A CDO writes the sparse class data index only when its class is a Blueprint one, which
        // is the same condition UObject.Deserialize tests -- so a native CDO with four spare bytes
        // is something else and is refused.
        var ofClass = export.Class?.Object?.Value.ExportType ?? "";
        return after == 4 && ofClass.EndsWith("BlueprintGeneratedClass", StringComparison.Ordinal)
               ? 4 : -1;
    }

    // ------------------------------------------------------------------ a member INSIDE a struct

    /// <summary>
    /// Give a STRUCT value a member it does not carry -- `UIData.Description` on an ability whose
    /// UIData never had one.
    ///
    /// <see cref="Plan"/> adds to an EXPORT and knows nothing below it. But a struct without a
    /// native serialiser is written exactly as an export is -- its own unversioned header, then its
    /// present values in schema order (see <see cref="Locate"/>) -- so adding inside one is the
    /// same edit one level down: find the struct, read ITS header against ITS schema, rebuild that
    /// header with the member present, and splice the value in at its schema position. Nothing
    /// outside the struct records its length, so the export map is the only other thing that
    /// moves, and <see cref="ZenPackage.SpliceExportData"/> owns that.
    ///
    /// The same gap blocked TargetingTypeText -- the red header on a granted class ability -- and
    /// SortingPriority, which decides where the wheel puts one. This is the writer for all three.
    ///
    /// Read back before it is returned, byte for byte: the new member must be findable at its path
    /// holding exactly the bytes written, and every member the struct already carried, and every
    /// other property of the export, must hold exactly the bytes it held. A header rebuilt with one
    /// bit in the wrong place still parses; it just hands each later member its neighbour's value.
    /// </summary>
    public static byte[] AddStructMember(IFileProvider provider, byte[] raw, string packageName,
                                         string exportClass, string structPath, string member,
                                         byte[] value, out string? why)
    {
        why = null;
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null) { why = $"no mounted container holds {packageName}"; return raw; }

            var pkg = Open(provider, raw, packageName, container);
            var exports = pkg.ExportsLazy.Select(x => x.Value).ToArray();
            if (FindExport(exports, exportClass) is not { } export)
            { why = $"no export matching '{exportClass}'"; return raw; }
            if (export.Class?.Object?.Value is not UStruct struc || SchemaFor(pkg, struc) is not { } schema)
            { why = $"the '{exportClass}' export has no property schema"; return raw; }

            var structSchema = SchemaAt(pkg, schema, structPath, out why);
            if (structSchema is null) return raw;

            var wanted = -1;
            var total = structSchema.CountProperties(true);
            var members = new List<string>();
            for (var i = 0; i < total; i++)
            {
                if (!structSchema.TryGetValue(i, out var slot)) continue;
                members.Add(slot.Name);
                if (string.Equals(slot.Name, member, StringComparison.Ordinal)) wanted = i;
            }
            if (wanted < 0)
            { why = $"'{member}' is not a member of {structSchema.Name}. It has: {string.Join(", ", members)}"; return raw; }

            if (!Locate(provider, raw, packageName, exportClass, structPath,
                        out var structStart, out var structLength, out why))
                return raw;

            // THE STRUCT'S OWN HEADER AND VALUES, walked the way Plan walks an export's.
            var ar = new FAssetArchive(new FByteArchive(packageName, raw, provider.Versions), pkg)
            {
                Position = structStart,
            };
            var header = new FUnversionedHeader(ar);
            var headerEnd = (int) ar.Position;

            var present = new List<(int Index, bool Zero)>();
            var carried = new List<string>();
            var insertAt = -1;
            var raising = false;

            if (header.HasValues)
            {
                using var it = new FIterator(header);
                do
                {
                    var (val, isNonZero) = it.Current;
                    if (val == wanted && isNonZero)
                    { why = $"{structSchema.Name} already carries '{member}' - this adds one that is absent"; return raw; }

                    // Present and ZERO occupies no bytes, so raising it is adding it where the walk
                    // has reached -- Plan's third state, one level down.
                    if (val == wanted)
                    {
                        raising = true;
                        insertAt = (int) ar.Position;
                        present.Add((val, false));
                        continue;
                    }

                    if (val > wanted && insertAt < 0) insertAt = (int) ar.Position;
                    present.Add((val, !isNonZero));

                    if (!isNonZero) continue;
                    if (!structSchema.TryGetValue(val, out var info))
                    { why = $"{structSchema.Name}: member {val} is not in the mappings, so its size is unknown"; return raw; }
                    _ = new FPropertyTag(ar, info, ReadType.NORMAL);
                    carried.Add(info.Name);
                } while (it.MoveNext());
            }

            // THE TWO WALKS MUST AGREE ON WHERE THE STRUCT ENDS. Locate measured it from the parent
            // by reading the struct whole; this measured it member by member. If they differ, the
            // struct is not serialised the way its schema says -- a native serialiser, most likely --
            // and a header written into it would be written into something that is not a header.
            var structEnd = (int) ar.Position;
            if (structEnd != structStart + structLength)
            {
                why = $"{structPath} is {structLength} byte(s) read whole and {structEnd - structStart} read " +
                      "member by member, so it is not laid out the way its schema says";
                return raw;
            }
            if (insertAt < 0) insertAt = structEnd;
            if (!raising) present.Add((wanted, false));

            var newHeader = BuildHeader(present);
            var replacement = new byte[newHeader.Length + (insertAt - headerEnd) + value.Length];
            newHeader.CopyTo(replacement, 0);
            Array.Copy(raw, headerEnd, replacement, newHeader.Length, insertAt - headerEnd);
            value.CopyTo(replacement, newHeader.Length + (insertAt - headerEnd));

            var written = ZenPackage.SpliceExportData(raw, structStart, insertAt - structStart, replacement);

            // READ BACK, BY BYTES. The new member first...
            if (!Locate(provider, written, packageName, exportClass, $"{structPath}.{member}",
                        out var gotAt, out var gotLen, out var gotWhy))
            { why = $"{structPath}.{member} cannot be found after the write: {gotWhy}"; return raw; }
            if (!written.AsSpan(gotAt, gotLen).SequenceEqual(value))
            { why = $"{structPath}.{member} reads back as {gotLen} byte(s) that are not the {value.Length} written"; return raw; }

            // ...then every member that was already there, and every other property of the export.
            // The property that CONTAINS the struct is the one that grew, so it is the one left out
            // -- by its bare name, because a path that starts "Modifiers[0]" lives in "Modifiers".
            var root = Segment(SplitPath(structPath)[0]).Name;
            var unchanged = carried.Select(m => $"{structPath}.{m}")
                .Concat(export.Properties.Select(p => p.Name.Text)
                    .Where(n => !string.Equals(n, root, StringComparison.Ordinal)));
            foreach (var path in unchanged)
            {
                if (!Locate(provider, raw, packageName, exportClass, path, out var wasAt, out var wasLen, out _))
                    continue;   // not addressable by name (an element of an array) -- nothing to compare
                if (!Locate(provider, written, packageName, exportClass, path, out var nowAt, out var nowLen, out var lostWhy))
                { why = $"{path} was lost: {lostWhy}"; return raw; }
                if (!raw.AsSpan(wasAt, wasLen).SequenceEqual(written.AsSpan(nowAt, nowLen)))
                { why = $"{path} changed: {wasLen} byte(s) before, {nowLen} after"; return raw; }
            }
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// The schema of the struct a dotted path names, found BY NAME through the mappings -- the same
    /// resolution <see cref="Descend"/> makes on its way down, without reading any bytes. An
    /// indexed segment, "Modifiers[0]", takes the array's element type.
    /// </summary>
    private static Struct? SchemaAt(IoPackage pkg, Struct schema, string path, out string? why)
    {
        why = null;
        var at = schema;
        foreach (var part in SplitPath(path))
        {
            var (name, index) = Segment(part);
            PropertyInfo? found = null;
            var total = at.CountProperties(true);
            for (var i = 0; i < total && found is null; i++)
                if (at.TryGetValue(i, out var info) && string.Equals(info.Name, name, StringComparison.Ordinal))
                    found = info;
            if (found is null) { why = $"'{name}' is not a property of {at.Name}"; return null; }

            var type = index >= 0 ? found.MappingType.InnerType : found.MappingType;
            if (type?.StructType is not { } inner)
            { why = $"'{part}' is a {type?.Type ?? "property"} and not a struct, so nothing can be added inside it"; return null; }
            if (pkg.Mappings is not { } m || !m.Types.TryGetValue(inner, out var next) || next is null)
            { why = $"the mappings do not describe '{inner}'"; return null; }
            at = next;
        }
        return at;
    }

    // ------------------------------------------------------------------ the header

    /// <summary>
    /// Fragments for a set of present property indices.
    ///
    /// A fragment is one ushort: how many indices to SKIP, whether the run it introduces has any
    /// zero-valued members, whether it is the last, and how many values follow. Skips and runs are
    /// both seven bits, so anything longer is split across fragments rather than truncated - a
    /// silently truncated skip would shift every property after it onto the wrong schema slot.
    ///
    /// AN OBJECT WITH NOTHING STORED is one fragment that SKIPS EVERY SLOT, the way UE's
    /// FUnversionedHeaderBuilder leaves it: measured `0b 01` on the game's empty
    /// CustomizationFragmentSlot (11 slots). Pass the schema's count as <paramref name="total"/>;
    /// callers that never write an empty header may leave it 0.
    /// </summary>
    internal static byte[] BuildHeader(List<(int Index, bool Zero)> present, int total = 0)
    {
        // Sorted HERE rather than by the caller. Fragments are runs of consecutive slots separated
        // by skips, so the whole encoding assumes ascending order -- and a caller that appends one
        // slot to an already-ordered list gets a header that decodes into nonsense with no
        // complaint from anything. That is precisely what happened the first time a second caller
        // used this: an added slot 0 on the end of 1..4 produced a two-fragment header claiming a
        // skip of 123.
        present.Sort((a, b) => a.Index.CompareTo(b.Index));

        var packed = new List<ushort>();
        var zeroBits = new List<bool>();
        var cursor = 0;
        var i = 0;

        while (i < present.Count)
        {
            var skip = present[i].Index - cursor;
            while (skip > SkipMax)
            { packed.Add(Pack(SkipMax, false, 0)); skip -= SkipMax; }

            var run = 1;
            while (i + run < present.Count && present[i + run].Index == present[i].Index + run) run++;
            run = Math.Min(run, ValueMax);

            var anyZero = false;
            for (var k = i; k < i + run; k++) anyZero |= present[k].Zero;

            packed.Add(Pack(skip, anyZero, run));
            // ALL of a fragment's values contribute a bit when any of them is zero - that is how
            // the reader counts the mask, so writing only the zero ones puts every later bit on
            // the wrong property.
            if (anyZero) for (var k = i; k < i + run; k++) zeroBits.Add(present[k].Zero);

            cursor = present[i].Index + run;
            i += run;
        }

        if (packed.Count == 0) packed.Add(Pack(Math.Min(total, SkipMax), false, 0));
        packed[^1] = (ushort) (packed[^1] | 0x0100u);   // IsLast

        var bytes = new List<byte>();
        foreach (var p in packed) bytes.AddRange(BitConverter.GetBytes(p));

        if (zeroBits.Count > 0)
        {
            // The reader sizes the mask by bit count: one byte to 8, two to 16, then whole 32-bit
            // words. Writing a byte where it expects a word desynchronises everything after it.
            var maskBytes = zeroBits.Count <= 8 ? 1 : zeroBits.Count <= 16 ? 2
                          : (zeroBits.Count + 31) / 32 * 4;
            var mask = new byte[maskBytes];
            for (var b = 0; b < zeroBits.Count; b++)
                if (zeroBits[b]) mask[b / 8] |= (byte) (1 << (b % 8));
            bytes.AddRange(mask);
        }

        return bytes.ToArray();
    }

    private static ushort Pack(int skip, bool hasZeroes, int valueNum) =>
        (ushort) ((skip & 0x7F) | (hasZeroes ? 0x80 : 0) | ((valueNum & 0x7F) << 9));

    // ------------------------------------------------------------------ the value

    /// <summary>
    /// A cooked FGameplayTagContainer: a count, then that many FNames. The same layout
    /// <see cref="GameplayTagEdit"/> writes, and the reason that class can find one by its
    /// contents at all.
    /// </summary>
    private static byte[] SerializeTagContainer(List<int> nameIndices)
    {
        var bytes = new byte[4 + nameIndices.Count * 8];
        BitConverter.GetBytes(nameIndices.Count).CopyTo(bytes, 0);
        for (var i = 0; i < nameIndices.Count; i++)
        {
            BitConverter.GetBytes((uint) nameIndices[i]).CopyTo(bytes, 4 + i * 8);
            BitConverter.GetBytes(0u).CopyTo(bytes, 8 + i * 8);   // the name NUMBER, never set
        }
        return bytes;
    }

    // ------------------------------------------------------------------ reading it back

    /// <summary>
    /// Re-read the written package and say what is wrong with it, or null.
    ///
    /// Checking the properties that were ALREADY there matters as much as checking the new one. A
    /// header rebuilt with one bit in the wrong place still parses; it just hands every later
    /// property the value of its neighbour, which is exactly the kind of damage that reaches a
    /// game looking like a gameplay bug.
    /// </summary>
    private static string? Verify(IFileProvider provider, byte[] written, string packageName,
                                  string exportClass, string property, IReadOnlyList<string> tags,
                                  Dictionary<string, string> before)
    {
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            var pkg = Open(provider, written, packageName, container);
            var export = FindExport(pkg.ExportsLazy.Select(x => x.Value).ToArray(), exportClass);
            if (export is null) return $"the '{exportClass}' export is gone";

            var now = PartDetails.TagsIn(new[] { export! }, property);
            if (now.Count == 0) return $"'{property}' is still not there";
            if (!now.SequenceEqual(tags, StringComparer.Ordinal))
                return $"'{property}' reads back as [{string.Join(", ", now)}]";

            foreach (var (name, was) in before)
            {
                var prop = export.Properties.FirstOrDefault(p => p.Name.Text == name);
                if (prop is null) return $"'{name}' was lost";
                var value = prop.Tag?.GenericValue?.ToString() ?? "(null)";
                if (value != was) return $"'{name}' changed from {was} to {value}";
            }
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>
    /// The property schema for an export's class.
    ///
    /// TWO CASES, and taking only one of them throws. A Blueprint class carries its own property
    /// list and is described by wrapping it; a NATIVE class carries none -- `ChildProperties` is
    /// null on it -- and has to be looked up in the mappings by name. UObject.Deserialize splits
    /// exactly here, and the fragment exports in this game are native, so a version that only
    /// handled Blueprint classes worked on the part itself and threw on everything inside it.
    /// </summary>
    internal static Struct? SchemaFor(IoPackage pkg, UStruct struc)
    {
        if (struc is UScriptClass)
            return pkg.Mappings is { } m && m.Types.TryGetValue(struc.Name, out var native)
                ? native : null;
        return new SerializedStruct(pkg.Mappings, struc);
    }

    /// <summary>
    /// The exports of the PRISTINE package, parsed from bytes rather than fetched from the mount.
    ///
    /// provider.LoadPackage returns whatever currently wins the lookup, and once a mod is
    /// installed that is the MOD -- so a second build of the same mod read its own previous output
    /// and looked for text that the first build had already replaced. It failed with "the existing
    /// name appears 0 times", which is a true statement about the wrong file, and it only happens
    /// on the second build, which is the one nobody tests.
    /// </summary>
    public static UObject[] PristineExports(IFileProvider provider, byte[] raw, string packageName,
                                            string? headerFrom = null)
    {
        var container = AssetCopy.CueHeaderFor(provider, headerFrom ?? packageName, pristine: true);
        if (container is null) return [];

        var pkg = Open(provider, raw, packageName, container);
        var exports = pkg.ExportsLazy.Select(x => x.Value).ToArray();

        // TRIED EXACTLY AS BEFORE, first. Every caller here that already worked -- the Game
        // Feature plugin path (AddTableOverlays/AddObjectArray, ModBuild.cs's DTO_Action/
        // GameFeatureData authoring, and --dtoprobe) also passes a headerFrom that differs from
        // packageName, and must see this unchanged: confirmed by the R21 finding, and reproduced
        // below before this branch existed.
        if (headerFrom is null || headerFrom == packageName || !AnyUnresolvedPackageImportClass(pkg, exports))
            return exports;

        // ONLY ON THE FAILURE MEASURED FOR A RENAMED PART OR COPY: an export that DOES declare a
        // package-import class (ExportMap[i].ClassIndex.IsPackageImport -- read straight from
        // raw's own bytes, so this is never a guess about what the export "should" have) whose
        // Class did not resolve. IoPackage resolves an imported class by looking ITS OWN name up
        // inside the container (CUE4Parse's IoPackage.GetStoreEntryAndImportedPackageIds, keyed
        // FPackageId.FromName(Name), where Name comes from the package's own embedded summary --
        // never from packageName here). A mod's own new part or copy has already been renamed to
        // its own invented path by the time slots run, which is never a key in any real
        // container, so it resolves no imported package and every such export's Class -- so its
        // Properties too -- comes back null/empty, silently, not a thrown error. MEASURED
        // (--modbuild SlotPartTest.json): a mod part's own top-level export (a package-import
        // class) read back with 0 properties and a null Class; the DTO_Action/GameFeatureData
        // path's exports are retyped to SCRIPT-import classes (GameFeaturePlugin.ModDataClass/
        // OverlayActionClass, both resolved globally, not through this package's own container
        // linkage at all) and so never trip this check, which is why their read-back already
        // worked without this branch.
        //
        // The package's OWN self-name entry is renamed back to headerFrom, on a throwaway COPY
        // (ZenPackage.Read never aliases raw -- Head/Tail are fresh slices). Undone here, never
        // written back: the real, built bytes never carry the template's own name. Only the FULL
        // PATH half of a rename moves -- the bare leaf (an export's own name) is a SEPARATE
        // name-map entry (Author's own rename note: "the full path, and the bare asset name...
        // both have to move" -- this is the inverse of one half of that alone), so ExportNames
        // and every property VALUE still read exactly what raw's own bytes hold.
        var zen = ZenPackage.Read(raw);
        if (!zen.Names.Contains(packageName, StringComparer.Ordinal)) return exports;
        zen.RenameNames(new Dictionary<string, string> { [packageName] = headerFrom });
        return Open(provider, zen.Write(), packageName, container).ExportsLazy.Select(x => x.Value).ToArray();
    }

    /// <summary>
    /// Whether ANY export declares its class by PACKAGE IMPORT (never a script import, never
    /// null) but that class failed to resolve -- IoPackage's own signature for "I could not find
    /// my own store entry" (see <see cref="PristineExports"/>'s own note). A genuinely classless
    /// export (ClassIndex.IsNull) is not this; neither is a script-import class that resolved
    /// through the global object store regardless of this package's own identity.
    /// </summary>
    private static bool AnyUnresolvedPackageImportClass(IoPackage pkg, UObject[] exports)
    {
        for (var i = 0; i < exports.Length && i < pkg.ExportMap.Length; i++)
            if (pkg.ExportMap[i].ClassIndex.IsPackageImport && exports[i]?.Class is null)
                return true;
        return false;
    }

    internal static IoPackage Open(IFileProvider provider, byte[] raw, string name,
                                  CUE4Parse.UE4.IO.Objects.FIoContainerHeader? container) =>
        new(new FByteArchive(name, raw, provider.Versions), container,
            (FArchive?) null, null, provider as IVfsFileProvider);

    /// <summary>
    /// Overwrite a number that is ALREADY THERE, in place.
    ///
    /// The cheapest of the three ways this file changes a package, and the only one that needs no
    /// splice: an int32 property that the export already carries occupies exactly four bytes, so
    /// the value is written over them and every offset after it is untouched. Nothing is added,
    /// nothing moves, and the read-back is a formality rather than a guard against drift.
    ///
    /// THE PROPERTY MUST BE PRESENT. Unversioned property data has no bytes at all for a property
    /// that is taking its default -- there is nothing to overwrite and no room to put anything --
    /// so a mission whose donor ships MinSquadSize 4 can be set to any other number and a donor
    /// that omits the property cannot. Callers are told which happened rather than left to guess:
    /// `why` says "not present", which is the signal to leave the donor's value alone.
    /// </summary>
    public static byte[] SetInt(IFileProvider provider, byte[] raw, string packageName,
                                string exportClass, string property, int value, out string? why)
    {
        why = null;
        try
        {
            if (!Locate(provider, raw, packageName, exportClass, property, out var at, out var len, out why))
                return raw;
            // FOUR BYTES EXACTLY. A longer range means the locate descended into something that is
            // not an int -- a struct that serialises itself natively reads back as a range that
            // means nothing -- and writing four bytes into the front of it would corrupt whatever
            // it really is. See the note on Locate.
            if (len != 4) { why = $"{property} is {len} bytes, not an int32"; return raw; }

            var written = (byte[]) raw.Clone();
            BitConverter.GetBytes(value).CopyTo(written, at);
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }

    /// <summary>
    /// Overwrite a bool that is already written as a byte.
    ///
    /// ONLY WHERE ONE IS WRITTEN. FBoolProperty in unversioned data is header-only when FALSE -- a
    /// zero slot and no bytes -- and one byte when TRUE, which is the asymmetry AddWeightedRecipe
    /// had to be taught the hard way. So flipping TRUE to FALSE and back is a byte write, and
    /// turning on a flag the donor leaves at its default is a splice this does not attempt. The
    /// caller is told, and a mission keeps the donor's flag rather than silently claiming to have
    /// changed it.
    /// </summary>
    public static byte[] SetBool(IFileProvider provider, byte[] raw, string packageName,
                                 string exportClass, string property, bool value, out string? why)
    {
        why = null;
        try
        {
            if (!Locate(provider, raw, packageName, exportClass, property, out var at, out var len, out why))
                return raw;
            if (len != 1) { why = $"{property} is {len} bytes, not a written bool"; return raw; }

            var written = (byte[]) raw.Clone();
            written[at] = (byte) (value ? 1 : 0);
            return written;
        }
        catch (Exception e) { why = e.Message; return raw; }
    }
}

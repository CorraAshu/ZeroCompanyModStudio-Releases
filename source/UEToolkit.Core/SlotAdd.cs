using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;

namespace UEToolkit.Core;

/// <summary>
/// ADD A SLOT TO A PART, and CLEAR A PART'S OWN SLOTS out of it: two writers over the same
/// CustomizationFragmentSlot class and the same Fragments list -- the thing the customization UI
/// builds its choices from.
///
/// Add builds a slot from nothing, not a transplant (spec, approach A): the object's data is an
/// unversioned block built from the class schema (<see cref="UnversionedBlock"/>), appended with
/// its own row, hash and dependencies (<see cref="ExportAdd"/>), then listed by the part. Its
/// template is the class's own default object, so every field not written takes the native
/// default -- which is why a false bSlotCanBeEmpty is written (the default is true) and
/// bSlotVisibleInUI is not.
///
/// ClearSlots removes every CustomizationFragmentSlot entry a part's Fragments already lists --
/// what a container copied from one of the game's own (the ATTACHMENTS category starts from the
/// Tops container) needs before this mod's own slots go in; see that method's own comment.
///
/// READ BACK, OR REFUSE: the grown or shrunk package is decoded and must read exactly as asked.
/// </summary>
public static class SlotAdd
{
    /// <summary>What a colour slot paints: the slot it tints, that slot's parts' material slots, and the channel.</summary>
    public sealed record Paint(string Slot, IReadOnlyList<string> MaterialSlots, string Channel, bool Small);

    public sealed record Slot(string Id, string Tag, string Name, string? Icon, IReadOnlyList<string> Fits,
                              string? Default, bool CanBeEmpty, string? Camera, Paint? Paints);

    public sealed record Added(string Id, string Export, int Index);

    // The loader's own hashes, read with --scriptobj on 2026-09-23 (build 196985); all four
    // re-read unchanged on build 199049 (2026-10-31). Re-run
    // `--scriptobj CustomizationFragmentSlot` after a game patch.
    public const ulong SlotClass = 0x60C1D61F3BEB644CUL;
    public const ulong SlotDefault = 0x7B44DC2B378E6309UL;
    public const ulong MaterialTargetStruct = 0x7E1E8C745F4AD7E1UL;
    public const ulong UIDataStruct = 0x57F51226B0F3A13FUL;
    public const string SlotClassName = "CustomizationFragmentSlot";

    public static byte[] Add(IFileProvider provider, byte[] raw, string packageName, string partExport,
                             IReadOnlyList<Slot> slots, out List<Added> added, out string? why,
                             string? headerFrom = null, Action<string>? log = null)
    {
        added = [];
        why = null;
        try
        {
            if (provider.MappingsContainer?.MappingsForGame?.Types is not { } types ||
                !types.TryGetValue(SlotClassName, out var schema))
            { why = $"the mappings do not describe {SlotClassName}"; return raw; }

            // REFUSALS, before anything is written.
            var start = PropertyAdd.PristineExports(provider, raw, packageName, headerFrom);
            // PristineExports returns [] both when the package genuinely has no such export AND
            // when it could not find a container to read it from at all (AssetCopy.CueHeaderFor
            // returned null) -- the second is a different failure and used to be reported as the
            // first, which sent someone looking at the wrong export name for nothing.
            if (start.Length == 0) { why = $"no container was found for {headerFrom ?? packageName}"; return raw; }
            if (start.All(e => e?.Name != partExport)) { why = $"the package has no export called {partExport}"; return raw; }
            var existing = start.Where(e => e?.ExportType == SlotClassName && e.Outer?.Name == partExport)
                                .Select(e => TagOf(Value(e, "SlotNameTag"))).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var asked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in slots)
            {
                if (string.IsNullOrWhiteSpace(s.Tag)) { why = $"slot {s.Id} has no tag"; return raw; }
                if (existing.Contains(s.Tag)) { why = $"{partExport} already has a slot {s.Tag}"; return raw; }
                if (!asked.Add(s.Tag)) { why = $"{s.Tag} is asked for twice"; return raw; }
            }
            // A colour slot's paint target must be a real slot -- either already on the part, or
            // one of the slots this same call is adding (a colour slot and the slot it tints can
            // arrive together, as the attachment/attachment-colour pair does).
            var paintable = existing.Concat(slots.Select(s => s.Tag)).ToHashSet(StringComparer.Ordinal);
            foreach (var s in slots)
                if (s.Paints is { } p && !paintable.Contains(p.Slot))
                { why = $"slot {s.Id} paints {p.Slot}, which is no slot on {partExport}"; return raw; }

            var work = raw;
            // Held in a LOCAL list, not `added` itself, until every slot has succeeded: a refusal
            // partway through a multi-slot call must hand back an EMPTY `added`, not one naming
            // the slot(s) that got written before the one that refused (ModBuild/
            // ModVerify read it). `added` starts `[]` above and every early return leaves it that
            // way untouched; only the one successful `return work;` below assigns it.
            var built = new List<Added>();
            foreach (var slot in slots)
            {
                work = EnsureNames(work, NamesFor(slot));

                // A colour slot's traits reference the game's own trait structs by SCRIPT import --
                // added here, before pkg is (re-)read, so the header the rest of this iteration
                // works from already has them.
                int materialTarget = 0, uiData = 0;
                if (slot.Paints is { } paints)
                {
                    var withImports = ZenPackage.Read(work);
                    materialTarget = withImports.AddScriptImport(MaterialTargetStruct);
                    if (paints.Small) uiData = withImports.AddScriptImport(UIDataStruct);
                    work = withImports.Write();
                }

                // Re-read AFTER the imports are added: inserting import rows can shift the export
                // map and name map, so pkg's indices must come from the FINAL header, not a stale one.
                var pkg = ZenPackage.Read(work);
                var part = Array.IndexOf(pkg.ExportNames, partExport);
                var donor = DonorFor(work, pkg, part);
                if (donor < 0) { why = $"{partExport} owns no fragment to model the new slot's row on"; return raw; }
                var exportName = NextName(pkg.ExportNames);

                var values = new List<UnversionedBlock.Value>
                {
                    new("SlotNameTag", ValueBytes.Tag(pkg, slot.Tag)),
                    new("SlotDisplayName", ValueBytes.Text(packageName, exportName, "SlotDisplayName", slot.Name)),
                };
                if (!string.IsNullOrEmpty(slot.Icon)) values.Add(new("SlotIcon", ValueBytes.SoftObject(pkg, slot.Icon)));
                if (!slot.CanBeEmpty) values.Add(new("bSlotCanBeEmpty", null));
                if (!string.IsNullOrEmpty(slot.Camera)) values.Add(new("DesiredCameraTypeTag", ValueBytes.Tag(pkg, slot.Camera)));
                if (slot.Fits.Count > 0) values.Add(new("AllowedTags", ValueBytes.TagContainer(pkg, slot.Fits)));
                if (slot.Paints is { } paint)
                {
                    if (!types.TryGetValue("CustomizationTraitMaterialTarget", out var mtSchema) ||
                        !types.TryGetValue("CustomizationTraitUIData", out var uiSchema))
                    { why = "the mappings do not describe the trait structs"; return raw; }
                    var items = new List<(int, byte[])>
                    {
                        (materialTarget, UnversionedBlock.Build(mtSchema, [
                            new("SlotNameTagsToApply", ValueBytes.TagContainer(pkg, [paint.Slot])),
                            new("MaterialSlotNames", ValueBytes.StrArray(paint.MaterialSlots)),
                            new("MaterialParameterName", ValueBytes.Str(paint.Channel)),
                        ], out why)),
                    };
                    if (why is not null) return raw;
                    if (paint.Small)
                    {
                        if (ValueBytes.EnumByte(provider, "ECustomizationUIDisplayOverride", "SmallDefault") is not { } small)
                        { why = "ECustomizationUIDisplayOverride has no SmallDefault in the mappings"; return raw; }
                        items.Add((uiData, UnversionedBlock.Build(uiSchema, [new("UIDisplayOverride", [small])], out why)));
                        if (why is not null) return raw;
                    }
                    values.Add(new("Traits", ValueBytes.InstancedStructs(items)));
                }
                if (!string.IsNullOrEmpty(slot.Default)) values.Add(new("DefaultPart", ValueBytes.SoftObject(pkg, slot.Default)));
                var data = UnversionedBlock.Object(schema, values, out why);
                if (why is not null) return raw;

                var grown = ExportAdd.Append(work, new ExportAdd.Spec(donor, part, SlotClass, SlotDefault, exportName, data,
                    CreateAfterCreate: [part + 1], CreateAfterSerialize: [], SerializeAfterCreate: [], SerializeAfterSerialize: [],
                    ListedBy: part), out var index, out why);
                if (grown is null) return raw;

                var listed = AppendToObjectArray(provider, grown, packageName, partExport, "Fragments", index + 1,
                                                 out why, headerFrom);
                if (why is not null) { why = "Fragments: " + why; return raw; }
                work = listed;
                built.Add(new Added(slot.Id, exportName, index));
                log?.Invoke($"      slot {slot.Id}: {exportName} (export {index}) {slot.Tag}");
            }

            var final = PropertyAdd.PristineExports(provider, work, packageName, headerFrom);
            var complaints = Check(final, partExport, slots);
            if (complaints.Count > 0) { why = "the slots do not read back: " + string.Join("; ", complaints); return raw; }

            // I-4 (spec 2.6): every export that existed BEFORE this call must still decode exactly
            // as it did -- the one exception is the part's own Fragments, which is asked to grow.
            // Check above only asks whether the NEW slots read back; nothing until now asked whether
            // the Fragments splice or a header insert left a SIBLING export damaged, and a package
            // that still parses says nothing about that. Same JSON-serialisation comparison the
            // --addslot probe uses (Addslot.cs), kept independent of it on purpose -- see the class
            // summary's READ BACK, OR REFUSE. Shared with ClearSlots via SiblingsDecodeAsBefore (the
            // WRITERS' own check, not the probes' -- see that method's own comment).
            if (SiblingsDecodeAsBefore(start, final, partExport) is { } damaged)
            { why = $"{start[damaged].Name} decodes differently after adding slots"; return raw; }

            if (!ZenCheck.Ok(work, out var zw)) { why = "the package is malformed after adding slots: " + zw; return raw; }
            added = built;
            return work;
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            return raw;
        }
    }

    /// <summary>
    /// CLEAR A PART'S SLOTS: remove every entry of its Fragments list that names a
    /// CustomizationFragmentSlot and keep every other fragment, in order. The slot objects stay in
    /// the package, unreferenced -- the part is built from its Fragments list (the attachments
    /// test confirms the game ignores them) -- so nothing else in the file moves.
    ///
    /// For a CONTAINER copied from one of the game's (the ATTACHMENTS category starts from the Tops
    /// container): its own slots go, its UI data stays, and the mod's slots are added after.
    /// READ BACK, OR REFUSE.
    /// </summary>
    public static byte[] ClearSlots(IFileProvider provider, byte[] raw, string packageName, string partExport,
                                    out int removed, out string? why, string? headerFrom = null)
    {
        removed = 0;
        why = null;
        try
        {
            var before = PropertyAdd.PristineExports(provider, raw, packageName, headerFrom);
            // Add's own two-part check (above): [] means EITHER, and reporting the wrong one sends
            // someone looking at the export name for nothing when the real problem is that no
            // container was found to read from at all (AssetCopy.CueHeaderFor returned null).
            if (before.Length == 0) { why = $"no container was found for {headerFrom ?? packageName}"; return raw; }
            if (before.All(e => e?.Name != partExport)) { why = $"the package has no export called {partExport}"; return raw; }
            if (!PropertyAdd.Locate(provider, raw, packageName, partExport, "Fragments", out var at, out var len, out why, headerFrom))
            { why = $"'Fragments' could not be located: {why}"; return raw; }
            if (len < 4 || (len - 4) % 4 != 0) { why = $"'Fragments' is {len} byte(s), not an array of object references"; return raw; }
            var count = BitConverter.ToInt32(raw, at);
            if (count < 0 || len != 4 + count * 4) { why = $"'Fragments' declares {count} entr(ies) in {len} bytes"; return raw; }

            var kept = new List<int>();
            var cleared = 0;
            for (var i = 0; i < count; i++)
            {
                var index = BitConverter.ToInt32(raw, at + 4 + i * 4);
                var isSlot = index > 0 && index - 1 < before.Length &&
                             string.Equals(before[index - 1]?.ExportType, SlotClassName, StringComparison.Ordinal);
                if (isSlot) cleared++; else kept.Add(index);
            }
            if (cleared == 0) { why = $"{partExport} lists no {SlotClassName} to clear"; return raw; }
            // An empty array is written as a ZERO in the header, not as a count of 0; this writer
            // does not rewrite headers, and a part with no fragments at all is not a container.
            if (kept.Count == 0) { why = $"clearing would leave {partExport} with no fragments at all"; return raw; }

            var bytes = new List<byte>(BitConverter.GetBytes(kept.Count));
            foreach (var k in kept) bytes.AddRange(BitConverter.GetBytes(k));
            var written = ZenPackage.SpliceExportData(raw, at, len, bytes.ToArray());

            var after = PropertyAdd.PristineExports(provider, written, packageName, headerFrom);
            var part = after.FirstOrDefault(e => e?.Name == partExport);
            if (part?.Properties.FirstOrDefault(p => p.Name.Text == "Fragments")?.Tag?.GenericValue is not UScriptArray got ||
                got.Properties.Count != kept.Count)
            { why = "'Fragments' does not read back with the kept entries"; return raw; }
            if (SiblingsDecodeAsBefore(before, after, partExport) is { } damaged)
            { why = $"export {damaged} ({before[damaged].Name}) no longer decodes as it did"; return raw; }
            if (!ZenCheck.Ok(written, out var zw)) { why = "the package is malformed after clearing: " + zw; return raw; }
            removed = cleared;
            return written;
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            removed = 0;
            return raw;
        }
    }

    /// <summary>
    /// Every asked-for slot, read out of the decoded exports: a CustomizationFragmentSlot owned by the
    /// part, listed in its Fragments, carrying the tag, name, fits, default, icon, camera and
    /// can-be-empty asked for. Empty when all of them read as asked.
    /// </summary>
    public static List<string> Check(UObject[] exports, string partExport, IReadOnlyList<Slot> slots)
    {
        var complaints = new List<string>();
        var part = exports.FirstOrDefault(e => e?.Name == partExport);
        if (part is null) return [$"no export {partExport}"];
        var listed = (Value(part, "Fragments") as UScriptArray)?.Properties
                     .Select(p => (p.GenericValue as FPackageIndex)?.Name).OfType<string>().ToHashSet() ?? [];

        foreach (var slot in slots)
        {
            var e = exports.FirstOrDefault(x => x?.ExportType == SlotClassName && x.Outer?.Name == partExport &&
                                                TagOf(Value(x, "SlotNameTag")) == slot.Tag);
            if (e is null) { complaints.Add($"no slot carries {slot.Tag}"); continue; }
            if (!listed.Contains(e.Name)) complaints.Add($"{e.Name} is not in {partExport}.Fragments");
            if ((Value(e, "SlotDisplayName") as FText)?.Text != slot.Name) complaints.Add($"{e.Name} is not named “{slot.Name}”");
            var fits = (Unwrap(Value(e, "AllowedTags")) as FGameplayTagContainer?)?.GameplayTags.Select(t => t.TagName.Text).ToList() ?? [];
            if (!fits.SequenceEqual(slot.Fits, StringComparer.Ordinal)) complaints.Add($"{e.Name} fits [{string.Join(", ", fits)}]");
            if ((Value(e, "bSlotCanBeEmpty") as bool? ?? true) != slot.CanBeEmpty) complaints.Add($"{e.Name} can-be-empty is wrong");
            if ((TagOf(Value(e, "DesiredCameraTypeTag")) ?? "") != (slot.Camera ?? "")) complaints.Add($"{e.Name} camera is wrong");
            if (!SamePath(Value(e, "SlotIcon"), slot.Icon)) complaints.Add($"{e.Name} icon is wrong");
            if (!SamePath(Value(e, "DefaultPart"), slot.Default)) complaints.Add($"{e.Name} default part is wrong");
            if (slot.Paints is { } paint)
            {
                // Unwrapped independently of the probe's own copy (Addslot.cs) -- the checks share
                // nothing with each other, same as the rest of this class shares nothing with the
                // probe. MEASURED: the game's Traits elements come back wrapped as
                // FScriptStruct { StructType: FInstancedStruct }, not a bare FInstancedStruct.
                var traits = (Value(e, "Traits") as UScriptArray)?.Properties
                    .Select(p => p.GenericValue switch
                    {
                        FInstancedStruct { NonConstStruct: { } b } => b,
                        FScriptStruct { StructType: FInstancedStruct { NonConstStruct: { } b } } => b,
                        _ => (FStructFallback?) null,
                    }).ToList() ?? [];
                var mt = traits.FirstOrDefault(t => t?.Properties.Any(p => p.Name.Text == "MaterialParameterName") == true);
                if (mt is null) complaints.Add($"{e.Name} has no material-target trait");
                else
                {
                    var applyTo = (Unwrap(mt.Properties.First(p => p.Name.Text == "SlotNameTagsToApply").Tag?.GenericValue) as FGameplayTagContainer?)
                                  ?.GameplayTags.Select(t => t.TagName.Text).ToList() ?? [];
                    var mats = (mt.Properties.First(p => p.Name.Text == "MaterialSlotNames").Tag?.GenericValue as UScriptArray)
                               ?.Properties.Select(p => (string) p.GenericValue!).ToList() ?? [];
                    var channel = mt.Properties.First(p => p.Name.Text == "MaterialParameterName").Tag?.GenericValue as string;
                    if (!applyTo.SequenceEqual([paint.Slot]) || !mats.SequenceEqual(paint.MaterialSlots) || channel != paint.Channel)
                        complaints.Add($"{e.Name} paints [{string.Join(", ", applyTo)}] {string.Join("/", mats)} {channel}");
                }
                var small = traits.Any(t => t?.Properties.Any(p => p.Name.Text == "UIDisplayOverride" &&
                                                                   (p.Tag?.GenericValue as FName?)?.Text.EndsWith("SmallDefault") == true) == true);
                if (small != paint.Small) complaints.Add($"{e.Name} small-tile reads {small}");
            }
        }
        return complaints;
    }

    /// <summary>Put every name the slot's values refer to into the package's name map, so the
    /// encoders can index them. Names with a "_N" suffix are stored as their stem.</summary>
    public static byte[] EnsureNames(byte[] raw, IEnumerable<string> names)
    {
        var pkg = ZenPackage.Read(raw);
        var before = pkg.Names.Count;
        foreach (var n in names.Where(n => !string.IsNullOrEmpty(n)))
        {
            var (stem, _) = ZenPackage.ParseNumber(n);
            pkg.AppendName(stem ?? n);
        }
        return pkg.Names.Count == before ? raw : pkg.Write();
    }

    private static IEnumerable<string> NamesFor(Slot slot)
    {
        yield return SlotClassName;
        yield return slot.Tag;
        if (slot.Camera is { Length: > 0 }) yield return slot.Camera;
        foreach (var f in slot.Fits) yield return f;
        foreach (var path in new[] { slot.Icon, slot.Default })
        {
            if (string.IsNullOrEmpty(path)) continue;
            var (package, asset) = PropertyEdit.SplitRef(path);
            if (package is not null) yield return package;
            if (asset is not null) yield return asset;
        }
        if (slot.Paints is { } p) yield return p.Slot;
    }

    /// <summary>
    /// Give an object array ONE MORE entry, by the FPackageIndex that names it -- the append
    /// <see cref="PropertyAdd.AddObjectArray"/> does not do.
    ///
    /// AddObjectArray's own Plan() refuses a property that already reads back non-zero, which is
    /// right for a scalar it must not clobber by accident and wrong for Fragments: every real part
    /// already lists the fragments before this one (measured: CPD_H_Outfit_Clo002_HELM already
    /// carries 7, byte for byte per --blockcheck). So the existing array is LOCATED, the way
    /// <see cref="PropertyAdd.SetSoftRefEntry"/> locates a soft reference array, rather than
    /// planned -- its header entry does not change, only its byte content grows -- and the new
    /// count and entry are spliced in where the old ones were.
    /// </summary>
    private static byte[] AppendToObjectArray(IFileProvider provider, byte[] raw, string packageName,
                                              string exportClass, string property, int fpackageIndex,
                                              out string? why, string? headerFrom = null)
    {
        why = null;
        if (fpackageIndex == 0) { why = $"'{property}' was given a null reference to write"; return raw; }
        if (!PropertyAdd.Locate(provider, raw, packageName, exportClass, property, out var at, out var len, out why, headerFrom))
        { why = $"'{property}' could not be located to append to: {why}"; return raw; }
        if (len < 4 || (len - 4) % 4 != 0)
        { why = $"'{property}' is {len} byte(s), not an array of object references"; return raw; }
        var count = BitConverter.ToInt32(raw, at);
        if (count < 0 || len != 4 + count * 4)
        { why = $"'{property}' declares {count} entr(ies) in {len} bytes, which is not {4 + count * 4}"; return raw; }

        var grown = new List<byte>(BitConverter.GetBytes(count + 1));
        grown.AddRange(raw.AsSpan(at + 4, len - 4).ToArray());
        grown.AddRange(BitConverter.GetBytes(fpackageIndex));

        var written = ZenPackage.SpliceExportData(raw, at, len, grown.ToArray());

        // Read back by count, the same guard AddObjectArray uses -- a brand-new import cannot be
        // resolved any other way here (see AddObjectArray's own note on ImportedPackages).
        var exports = PropertyAdd.PristineExports(provider, written, packageName, headerFrom);
        var export = exports.FirstOrDefault(e => e?.Name == exportClass);
        if (export is null) { why = $"the '{exportClass}' export is gone"; return raw; }
        var back = export.Properties.FirstOrDefault(x => x.Name.Text == property);
        if (back?.Tag?.GenericValue is not UScriptArray got)
        { why = $"'{property}' does not read back as an array"; return raw; }
        if (got.Properties.Count != count + 1)
        { why = $"'{property}' reads back with {got.Properties.Count} entr(ies), not {count + 1}"; return raw; }
        return written;
    }

    /// <summary>A fragment of the part whose template is a native default object (as HelmetVO's is),
    /// else any fragment of the part: its row supplies the flags a fragment carries.</summary>
    private static int DonorFor(byte[] raw, ZenPackage pkg, int part)
    {
        var outers = pkg.ExportOuters;
        var map = BitConverter.ToInt32(raw, 32);
        var owned = Enumerable.Range(0, outers.Length).Where(i => outers[i] == part).ToList();
        foreach (var i in owned)
            if (ZenPackage.KindOf(BitConverter.ToUInt64(raw, map + i * 72 + 48)) == ZenPackage.ImportKind.ScriptImport) return i;
        return owned.Count > 0 ? owned[0] : -1;
    }

    /// <summary>CustomizationFragmentSlot_N with N one past the highest the package already uses.</summary>
    private static string NextName(string[] exportNames)
    {
        var highest = -1;
        foreach (var n in exportNames)
        {
            if (n == SlotClassName) highest = Math.Max(highest, 0);
            var (stem, number) = ZenPackage.ParseNumber(n);
            if (stem == SlotClassName) highest = Math.Max(highest, (int) number - 1);
        }
        return $"{SlotClassName}_{highest + 1}";
    }

    /// <summary>
    /// The first export common to BEFORE and AFTER (by index) whose properties no longer decode the
    /// same way, ignoring only PARTEXPORT's own Fragments -- the one property either caller is asked
    /// to change. Null when every export the two arrays share still decodes exactly as it did.
    ///
    /// SHARED BY Add AND ClearSlots ONLY. The --addslot, --addexport and --clearslots PROBES each
    /// keep their OWN separate copy of this same comparison, on purpose: a probe calling into this
    /// method would be checking these writers with the writers' own logic, so a bug here would hide
    /// from every probe instead of being caught by one that re-derives the check independently. Do
    /// not point a probe at this method.
    /// </summary>
    private static int? SiblingsDecodeAsBefore(UObject[] before, UObject[] after, string partExport)
    {
        for (var i = 0; i < before.Length; i++)
        {
            // before[i] null: nothing existed at this index to have kept decoding -- skip, as ever.
            // after[i] null OR MISSING while before[i] EXISTED, though, is not the same thing: an
            // export that decoded before and does not after is damaged, not absent, and used to be
            // SKIPPED here (the same guard covered both), which can never report it. Unlikely in
            // practice -- PristineExports reads every index of ExportsLazy.Value eagerly, so a
            // missing entry would mean the header itself grew or shrank the export map -- but the
            // final review asked for the distinction anyway, in case it ever stops being unlikely.
            if (before[i] is null) continue;
            if (after.Length <= i || after[i] is null) return i;
            var b = before[i].Properties.Where(p => before[i].Name != partExport || p.Name.Text != "Fragments");
            var a = after[i].Properties.Where(p => after[i].Name != partExport || p.Name.Text != "Fragments");
            if (Newtonsoft.Json.JsonConvert.SerializeObject(b) != Newtonsoft.Json.JsonConvert.SerializeObject(a))
                return i;
        }
        return null;
    }

    internal static object? Value(UObject e, string name) =>
        e.Properties.FirstOrDefault(p => p.Name.Text == name)?.Tag?.GenericValue;

    internal static object? Unwrap(object? v) => v is FScriptStruct s ? s.StructType : v;

    internal static string? TagOf(object? v) => Unwrap(v) switch
    {
        FStructFallback f => (f.Properties.FirstOrDefault(p => p.Name.Text == "TagName")?.Tag?.GenericValue as FName?)?.Text,
        FGameplayTag t => t.TagName.Text,
        _ => null,
    };

    internal static bool SamePath(object? v, string? want)
    {
        if (string.IsNullOrEmpty(want)) return v is null;
        if (v is not FSoftObjectPath s) return false;
        var (wp, wa) = PropertyEdit.SplitRef(want);
        var (gp, ga) = PropertyEdit.SplitRef(s.AssetPathName.Text);
        return string.Equals(wp, gp, StringComparison.OrdinalIgnoreCase) && string.Equals(wa, ga, StringComparison.OrdinalIgnoreCase);
    }
}

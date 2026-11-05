using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;

namespace UEToolkit.Core;

/// <summary>
/// WHAT UE5 WOULD SHOW FOR THIS ASSET, BESIDE WHAT WE SHOW.
///
/// The author's question, 2026-09-21: "how hard is it to replicate all the data assets exactly
/// how they would be seen in UE5" — meaning the Details panel a person opening that asset in the
/// editor would get, not merely the class name.
///
/// So this prints the two lists side by side and does not summarise them. The left list is the
/// properties the FILE carries, which is all the property view has ever shown. The right list is
/// the properties the CLASS DECLARES, walked up the super chain out of the mappings, which is
/// what the editor's Details panel enumerates. They are not the same length and the difference
/// is the answer: a cooked asset serialises only what differs from its class default, so every
/// property left at its default is absent from the file and therefore invisible to us — while
/// being right there, editable, in the editor.
///
/// It also counts the TYPES, because that decides whether a typed editor is even possible: a
/// checkbox needs to know a property is a bool, a dropdown needs the enum's value table. Both
/// are present in what we already load and both are discarded before the UI sees them.
///
/// AND IT LOOKS FOR COOKED METADATA, which is the only route by which UE5's own Category,
/// DisplayName and ToolTip can survive cooking. A usmap carries none of those — the format is
/// names, types and super links and nothing else, in every version up to Latest — but UE5 has a
/// separate opt-in that writes a UClassCookedMetaData export beside the class holding the whole
/// per-property metadata map. Whether this game enabled it is a fact about the game, so it is
/// measured here rather than assumed either way.
/// </summary>
public static class UeFidelity
{
    /// <summary>One property as the class declares it, and whatever the file has for it.</summary>
    public sealed record Field(
        int Index,
        string Name,
        string Type,
        string Detail,
        string DeclaredBy,
        bool Inherited,
        bool InFile,
        string Value);

    public sealed record ExportReport(
        string Name,
        string ClassName,
        IReadOnlyList<string> Chain,
        IReadOnlyList<Field> Fields,
        int InFileCount,
        IReadOnlyList<string> FileOnly,
        string? SchemaWhy);

    /// <summary>
    /// The report for one package.
    ///
    /// Everything here is read, never inferred: the class chain comes from the mappings' own
    /// SuperType links, the declared property list from each schema's own dictionary, and the
    /// "in file" column from the export's property tags.
    /// </summary>
    public static List<string> Report(IFileProvider provider, string packagePath)
    {
        var lines = new List<string>();
        var mappings = provider.MappingsContainer?.MappingsForGame;

        lines.Add($"package    : {packagePath}");
        lines.Add($"container  : {AssetProps.WhichContainer(provider, packagePath) ?? "?"}");
        lines.Add($"mappings   : {(mappings is null ? "NOT LOADED" : $"{mappings.Types.Count:N0} types, {mappings.Enums.Count:N0} enums")}");
        lines.Add("");

        var pkg = provider.LoadPackage(packagePath);
        var exports = pkg.GetExports().ToArray();

        lines.Add($"exports    : {exports.Length:N0} — " +
                  string.Join(", ", exports.Take(12).Select(x => $"{x.Name}:{x.ExportType}")));
        lines.Add("");

        // THE METADATA QUESTION, ASKED OF THE FILE. Printed whichever way it comes out, because
        // "no categories" and "categories we never read" look identical on screen.
        lines.AddRange(CookedMetaData(exports));
        lines.Add("");

        var n = 0;
        foreach (var e in exports)
        {
            n++;
            if (e is UClassCookedMetaData or UStructCookedMetaData or UEnumCookedMetaData) continue;

            var rep = ForExport(e, mappings);
            lines.Add($"export {n}/{exports.Length}  {rep.Name}");
            lines.Add($"  class    : {rep.ClassName}");
            lines.Add($"  chain    : {(rep.Chain.Count == 0 ? "(not resolved)" : string.Join(" -> ", rep.Chain))}");

            if (rep.SchemaWhy is { } why)
            {
                lines.Add($"  SCHEMA   : {why}");
                lines.Add($"  in file  : {rep.InFileCount:N0} properties");
                lines.Add("");
                foreach (var f in rep.Fields)
                    lines.Add($"    {f.Name,-42} {f.Type,-22} {Trim(f.Value, 40)}");
                lines.Add("");
                continue;
            }

            var own = rep.Fields.Count(f => !f.Inherited);
            lines.Add($"  in file  : {rep.InFileCount:N0} properties");
            lines.Add($"  declared : {rep.Fields.Count:N0} properties ({own:N0} own, {rep.Fields.Count - own:N0} inherited)");
            lines.Add("");
            lines.Add("  #    property                                   type                   declared by                    in file  value");
            foreach (var f in rep.Fields)
                lines.Add($"  {f.Index,-4} {f.Name,-42} " +
                          $"{Trim(f.Type + (f.Detail.Length > 0 ? $"<{f.Detail}>" : ""), 22),-22} " +
                          $"{Trim(f.DeclaredBy, 30),-30} {(f.InFile ? "yes" : "-"),-8} {Trim(f.Value, 40)}");

            lines.Add("");
            var missing = rep.Fields.Count - rep.Fields.Count(f => f.InFile);
            lines.Add($"  TALLY    : {rep.Fields.Count:N0} declared, {rep.Fields.Count(f => f.InFile):N0} in the file, " +
                      $"{missing:N0} left at the class default and INVISIBLE to the property view");

            if (rep.FileOnly.Count > 0)
                lines.Add($"  FILE ONLY: {rep.FileOnly.Count:N0} property tags the schema does not declare — " +
                          string.Join(", ", rep.FileOnly.Take(8)));

            lines.Add("  WIDGETS  : " + Widgets(rep.Fields, mappings));
            lines.Add("");
        }

        return lines;
    }

    /// <summary>
    /// Whether this package carries UE5's own editor metadata, and what it says.
    ///
    /// The store is a map of property name to a map of metadata key to value — Category,
    /// DisplayName, ToolTip, ClampMin, EditCondition, the lot. If it is here, UE5's Details panel
    /// can be reproduced label for label. If it is not, categories and tooltips cannot be
    /// recovered from the shipped game at all and a grouping of our own is the only honest option.
    /// </summary>
    public static List<string> CookedMetaData(IReadOnlyList<UObject> exports)
    {
        var lines = new List<string>();
        var found = 0;

        foreach (var e in exports)
        {
            switch (e)
            {
                case UClassCookedMetaData c:
                {
                    found++;
                    lines.Add($"COOKED METADATA  {e.Name} ({e.ExportType})");
                    lines.AddRange(Store(c.ClassMetaData, "  "));
                    if (c.FunctionsMetaData.Count > 0)
                        lines.Add($"  functions with metadata: {c.FunctionsMetaData.Count:N0}");
                    break;
                }

                // The struct and enum stores are plain UObjects in CUE4Parse, so read the
                // property straight off the export rather than through a typed field.
                case UStructCookedMetaData or UEnumCookedMetaData:
                {
                    found++;
                    lines.Add($"COOKED METADATA  {e.Name} ({e.ExportType})");
                    if (e.TryGetValue(out FStructCookedMetaDataStore s, "MetaData", "StructMetaData", "ClassMetaData"))
                        lines.AddRange(Store(s, "  "));
                    else
                        lines.Add("  (present, but no store property on it under a name we tried)");
                    break;
                }
            }
        }

        if (found == 0)
            lines.Add("COOKED METADATA  none in this package — no Category, DisplayName or ToolTip " +
                      "survived cooking here");
        return lines;
    }

    private static List<string> Store(FStructCookedMetaDataStore s, string pad)
    {
        var lines = new List<string>();
        var obj = s.ObjectMetaData.ObjectMetaData;
        if (obj is { Count: > 0 })
            lines.Add($"{pad}class-level: " + string.Join(", ", obj.Select(kv => $"{kv.Key}={Trim(kv.Value ?? "", 40)}")));

        if (s.PropertiesMetaData is not { Count: > 0 })
        {
            lines.Add($"{pad}per-property: none");
            return lines;
        }

        lines.Add($"{pad}per-property: {s.PropertiesMetaData.Count:N0} properties carry metadata");
        foreach (var kv in s.PropertiesMetaData)
        {
            var keys = kv.Value?.FieldMetaData;
            if (keys is not { Count: > 0 }) continue;
            lines.Add($"{pad}  {kv.Key,-38} " +
                      string.Join("  ", keys.Select(m => $"{m.Key}={Trim(m.Value ?? "", 34)}")));
        }
        return lines;
    }

    /// <summary>
    /// The declared list for one export, and the file's own values against it.
    ///
    /// The schema comes from the mappings for a NATIVE class and from the package for a
    /// Blueprint-generated one, which is the same split PropertyAdd.ClassSchema already makes:
    /// a native class arrives as a UScriptClass stub whose ChildProperties is null.
    /// </summary>
    public static ExportReport ForExport(UObject e, TypeMappings? mappings)
    {
        var cls = e.ExportType;
        var inFile = new Dictionary<string, FPropertyTag>(StringComparer.Ordinal);
        foreach (var p in e.Properties)
            inFile.TryAdd(p.Name.Text, p);

        var schema = AssetProps.SchemaFor(e, mappings, out var why);
        if (schema is null)
            return new ExportReport(e.Name, cls, Chain(cls, mappings),
                e.Properties.Select((p, i) => new Field(
                    i, p.Name.Text, p.PropertyType.Text, Detail(p), "?", false, true,
                    Flat(p.Tag?.GenericValue))).ToList(),
                e.Properties.Count, Array.Empty<string>(), why);

        var fields = new List<Field>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var at = 0;
        var walk = schema;
        var first = true;

        // OWN FIRST, THEN INHERITED, which is the mappings' own index space: TryGetValue looks in
        // this class's dictionary for [0, PropertyCount) and hands anything above it to the super
        // after subtracting that count. So this order is the order the serialiser uses, and it is
        // also the only grouping we can honestly offer when no cooked metadata carries UE5's
        // categories.
        while (walk is not null)
        {
            var props = walk.Properties;
            if (props is not null)
                // BY THE DICTIONARY KEY, not PropertyInfo.Index -- see AssetProps.Walk. That field
                // is 0 for every property of ArrayDim 1, so grouping by it reported one property
                // per class and made this probe's own tally an UNDER-count.
                foreach (var kv in props.OrderBy(kv => kv.Key))
                {
                    var pi = kv.Value;
                    if (!seen.Add(pi.Name)) continue;   // a child shadowing a parent, or a static array's extra slots
                    var t = pi.MappingType;
                    var has = inFile.TryGetValue(pi.Name, out var tag);
                    fields.Add(new Field(
                        at++, pi.Name, t.Type, Detail(t), walk.Name, !first, has,
                        has ? Flat(tag!.Tag?.GenericValue) : ""));
                }

            first = false;
            walk = walk.Super?.Value;
        }

        var fileOnly = inFile.Keys.Where(k => !seen.Contains(k)).ToList();
        return new ExportReport(e.Name, cls, Chain(cls, mappings), fields,
                                inFile.Count, fileOnly, null);
    }

    private static Struct? SchemaFor(UObject e, TypeMappings? mappings, out string? why)
    {
        why = null;
        if (mappings is null) { why = "no mappings loaded"; return null; }

        // The LOADED class when the package carries it — a Blueprint-generated class holds its
        // own property list. A native class is a stub, and its property list is the mappings'.
        if (e.Class?.Object?.Value is UStruct struc && struc is not UScriptClass &&
            struc.ChildProperties is not null)
            return new SerializedStruct(mappings, struc);

        if (mappings.Types.TryGetValue(e.ExportType, out var native) && native is not null)
            return native;

        why = $"'{e.ExportType}' is neither a loaded struct in this package nor a type in the mappings";
        return null;
    }

    /// <summary>The super chain by NAME, out of the mappings' own SuperType links.</summary>
    private static List<string> Chain(string cls, TypeMappings? mappings)
    {
        var chain = new List<string> { cls };
        if (mappings is null) return chain;

        var at = cls;
        for (var guard = 0; guard < 64; guard++)
        {
            if (!mappings.Types.TryGetValue(at, out var s) || s?.SuperType is not { Length: > 0 } up)
                break;
            chain.Add(up);
            at = up;
        }
        return chain;
    }

    private static string Detail(PropertyType t)
        => t.StructType ?? t.EnumName ?? (t.InnerType is { } i
            ? i.Type + (i.StructType is { } si ? ":" + si : i.EnumName is { } ei ? ":" + ei : "")
            : "");

    private static string Detail(FPropertyTag p)
        => p.TagData?.StructType ?? p.TagData?.EnumName ?? p.TagData?.InnerType ?? "";

    /// <summary>
    /// WHICH TYPED EDITORS THIS ASSET WOULD NEED, and whether the data for them is in hand.
    ///
    /// An enum counts as ready only when its VALUE TABLE is in the mappings, because a dropdown
    /// with no options is not a dropdown.
    /// </summary>
    private static string Widgets(IReadOnlyList<Field> fields, TypeMappings? mappings)
    {
        var bucket = new Dictionary<string, int>(StringComparer.Ordinal);
        var enumsReady = 0;
        var enumsBlind = 0;

        foreach (var f in fields)
        {
            var kind = f.Type switch
            {
                "BoolProperty" => "checkbox",
                "EnumProperty" or "ByteProperty" => "dropdown",
                "ObjectProperty" or "SoftObjectProperty" or "ClassProperty" or "AssetObjectProperty"
                    or "SoftClassProperty" or "LazyObjectProperty" => "asset picker",
                "StructProperty" => "struct",
                "ArrayProperty" => "array",
                "MapProperty" or "SetProperty" => "map/set",
                "StrProperty" or "TextProperty" or "NameProperty" or "Utf8StrProperty"
                    or "AnsiStrProperty" => "text box",
                "FloatProperty" or "DoubleProperty" or "IntProperty" or "Int8Property"
                    or "Int16Property" or "Int64Property" or "UInt16Property" or "UInt32Property"
                    or "UInt64Property" => "number box",
                _ => "other:" + f.Type,
            };
            bucket[kind] = bucket.GetValueOrDefault(kind) + 1;

            if (kind != "dropdown") continue;
            if (f.Detail.Length > 0 && mappings?.Enums.ContainsKey(f.Detail) == true) enumsReady++;
            else enumsBlind++;
        }

        var parts = bucket.OrderByDescending(kv => kv.Value)
                          .Select(kv => $"{kv.Key} {kv.Value}").ToList();
        if (enumsReady + enumsBlind > 0)
            parts.Add($"[enum tables: {enumsReady} present, {enumsBlind} missing]");
        return string.Join(", ", parts);
    }

    private static string Flat(object? v)
        => v?.ToString()?.Replace('\n', ' ').Replace('\r', ' ') ?? "";

    private static string Trim(string s, int n)
        => s.Length <= n ? s : s[..(n - 1)] + "~";
}

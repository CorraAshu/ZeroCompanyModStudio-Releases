using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.VirtualFileSystem;

namespace UEToolkit.Core;

/// <summary>One row of an asset's property tree: a name, what it holds, and what is under it.</summary>
public sealed class PropNode
{
    public required string Name { get; init; }

    /// <summary>The value, where a row has one. A row with children usually does not.</summary>
    public string Value { get; init; } = "";

    /// <summary>
    /// What it is, when there is no single value to show — "12 entries", "6 x ObjectProperty",
    /// "empty". WITHOUT brackets: the view puts those on, so one place decides how it reads.
    /// </summary>
    public string Note { get; init; } = "";

    /// <summary>
    /// The export this property sits directly on, when it sits directly on one.
    ///
    /// EMPTY FOR ANYTHING NESTED. ScalarEdit.SetByName addresses a property as export.property,
    /// so a member of a struct three levels down has no address it can use — and offering to edit
    /// one would be offering something the build cannot do.
    /// </summary>
    public string Export { get; init; } = "";

    /// <summary>The value as a number, when it is one. This is what an edit's `was` must match.</summary>
    public double? Number { get; init; }

    /// <summary>
    /// Worth offering a box for: a number or a bool sitting directly on an export.
    ///
    /// It is a CANDIDATE, not a promise. Whether the build can really write it is answered by
    /// PropertyEdit.Try rehearsing the edit on the game's own bytes, because a cooked package
    /// stores only what was set and no amount of reading the value can tell you whether there are
    /// bytes behind it.
    /// </summary>
    /// <summary>
    /// Worth offering a box for.
    ///
    /// A NAME COUNTS, and it is the first thing here that is not a number. An FName property is
    /// eight bytes -- a name-table index and a number -- so setting one is still a write in
    /// place, and PropertyAdd.SetName appends to the batch when the value is new. It is a
    /// CANDIDATE, not a promise: the rehearsal decides, as it always has.
    /// </summary>
    public bool Editable => Export.Length > 0 &&
                            (Number is not null || Type is "NameProperty" or "StrProperty");

    /// <summary>
    /// The SERIALISER'S name for this property's type -- BoolProperty, StructProperty,
    /// ArrayProperty -- and the narrowing beside it: the struct, the enum, or an array's inner
    /// type.
    ///
    /// THIS WAS THROWN AWAY, and it is the one thing a typed editor cannot do without. A checkbox
    /// has to know a property is a bool, a dropdown has to know which enum, an asset picker has to
    /// know it is an object reference -- and all of it was parsed, sat on the FPropertyTag, and
    /// was dropped one line before the UI. Measured 2026-09-22 across four assets: every row had a
    /// real type and none of it reached the screen.
    /// </summary>
    public string Type { get; init; } = "";

    public string Detail { get; init; } = "";

    /// <summary>
    /// Whether the FILE carries this property, or only the class DECLARES it.
    ///
    /// A cooked asset serialises only what differs from its class default, so a property left at
    /// its default has no bytes at all -- and an absent property and a property that does not
    /// exist looked exactly the same here, which is to say invisible. Measured 2026-09-22 across
    /// four assets: 43 properties declared, 18 in the files, **25 invisible**. The hidden ones
    /// were the empty fragment payloads -- SkeletalMeshOptions, GameplayTags, SlotNameTag,
    /// AttributeSets -- which is to say exactly the ones somebody would want to fill.
    /// </summary>
    public bool Stored { get; init; } = true;

    /// <summary>The class that declares this property, walked up the super chain.</summary>
    public string DeclaredBy { get; init; } = "";

    /// <summary>
    /// The name as UE5's Details panel would print it: `bSlotCanBeEmpty` -> "Slot Can Be Empty".
    ///
    /// EMPTY MEANS "the same as Name", and that is not laziness -- it is how a SYNTHETIC name is
    /// kept out of the algorithm. `[0]`, `key[3]` and `val[3]` are labels this reader invents for
    /// array and map entries, and running them through a word-splitter written for identifiers
    /// turns `[0]` into `[ 0`. Only a name that came off a property tag or a schema gets one.
    /// </summary>
    public string Display { get; init; } = "";

    /// <summary>What to put on screen: the display name when there is one, the raw name when not.</summary>
    public string Label => Display.Length > 0 ? Display : Name;

    /// <summary>
    /// An enum's own values, in the order it declares them.
    ///
    /// The mappings carry 2,706 of these tables beside the 16,140 types, and none of them had
    /// ever been read: a dropdown with no options is not a dropdown, and without the list the
    /// only thing a reader could do with an enum was print the name it happens to hold.
    ///
    /// Empty for everything that is not an enum, which is what <see cref="PropNode.Type"/> being
    /// ByteProperty or EnumProperty decides.
    /// </summary>
    public IReadOnlyList<(long Value, string Name)> Choices { get; init; } = [];

    /// <summary>
    /// The type AS UE WRITES IT, which is what somebody who had opened this asset in the editor
    /// would recognise: `bool`, `float`, `FGameplayTag`, `TArray&lt;TSoftObjectPtr&gt;`.
    ///
    /// `BoolProperty` is the name of the thing that SERIALISES a bool, not the name of the type,
    /// and showing it is a reader telling you about its own plumbing. The author's question was
    /// what somebody creating this in UE5 would see, so the answer is spelled their way.
    /// </summary>
    public string UeType => AssetProps.UeSpelling(Type, Detail);

    public List<PropNode> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;
}

/// <summary>
/// Everything one asset's package has to say, as a tree rather than as printed lines.
/// </summary>
public sealed class AssetRead
{
    public required string FilePath { get; init; }
    public required string PackagePath { get; init; }

    /// <summary>
    /// The container the provider actually SERVED this from.
    ///
    /// With a mod installed there are two packages at one id, and which one you are reading is
    /// not a detail: it decided, for a whole evening once, whether a property "existed". A panel
    /// that shows properties without saying where they came from invites exactly that hour back.
    /// </summary>
    public string? Container { get; init; }

    /// <summary>Why it could not be read, or null. NAMED rather than shown as an empty asset.</summary>
    public string? Error { get; init; }

    /// <summary>One node per export, its children being that export's properties.</summary>
    public List<PropNode> Exports { get; } = new();

    /// <summary>
    /// Rows under the exports — and it is a count of ROWS, which is what it has always been:
    /// nested struct fields and array elements have always been in it.
    /// </summary>
    public int PropertyCount => Exports.Sum(Count);

    /// <summary>
    /// How many of those the FILE does not carry.
    ///
    /// SAID SEPARATELY, because the two are not the same fact and a single total quietly merges
    /// them. Before declared-but-absent rows existed, "12 properties" meant twelve in the file;
    /// afterwards it would have meant twelve rows of which some number is the class talking. That
    /// is a counter changing its meaning without saying so, which this project has been bitten by
    /// before.
    /// </summary>
    public int DefaultCount => Exports.Sum(AtDefault);

    private static int Count(PropNode n) => n.Children.Count + n.Children.Sum(Count);

    private static int AtDefault(PropNode n)
        => n.Children.Count(c => !c.Stored) + n.Children.Sum(AtDefault);
}

/// <summary>
/// READ ANY ASSET'S PROPERTIES, for the Studio's property panel and for --props.
///
/// The walk itself is lifted out of <c>UEToolkit.App/Verbs/Props.cs</c>, which had it twice over
/// (<c>Props.cs</c> and <c>Props2.cs</c>) printing straight to the console. Two copies of a
/// reader CAN disagree about an asset, which is the reason `SplashBmp` moved into Core, so this
/// is one copy building a tree and the callers decide what to do with it.
/// </summary>
public static class AssetProps
{
    /// <summary>
    /// How deep the walk goes before it stops describing and starts printing type names.
    ///
    /// These nest through material overrides and would otherwise be a page per part. The old
    /// console dump used an indent of 16 with a step of 2, which is this.
    /// </summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// Read one asset.
    ///
    /// EVERYTHING IS INSIDE <see cref="AssetLoader.Read"/>. CUE4Parse's package cache is not safe
    /// to race on and the failure is an AccessViolationException from native code, which .NET
    /// will not let anyone catch — it has taken this project down twice, once with no crash log
    /// at all. `GetExports()` is lazy, so the ENUMERATION has to be inside the lock too and not
    /// merely the `LoadPackage` call, or the race is moved rather than closed.
    /// </summary>
    public static AssetRead Read(IFileProvider provider, string filePath)
    {
        var package = AssetLoader.PackagePath(filePath);
        var container = WhichContainer(provider, package);

        try
        {
            return AssetLoader.Read(() =>
            {
                var pkg = provider.LoadPackage(package);
                var read = new AssetRead
                {
                    FilePath = filePath, PackagePath = package, Container = container,
                };

                var mappings = provider.MappingsContainer?.MappingsForGame;

                foreach (var e in pkg.GetExports().ToArray())
                {
                    var node = new PropNode { Name = e.Name, Note = e.ExportType };

                    // WHERE EACH PROPERTY COMES FROM, before anything is built. The schema walk
                    // gives a place in the class chain for every name the class declares, and it
                    // is the same walk that finds the absent ones -- doing it once and reading
                    // both answers out of it is what lets the two lists be merged rather than
                    // stacked.
                    var from = Declarations(e, mappings);

                    var rows = new List<(int Depth, int Index, PropNode Node)>();
                    var present = new HashSet<string>(StringComparer.Ordinal);
                    var fileOrder = 0;

                    foreach (var p in e.Properties)
                    {
                        present.Add(p.Name.Text);
                        var where = from.GetValueOrDefault(p.Name.Text);
                        rows.Add((where.Depth, where.Index, Node(
                            p.Name.Text, p.Tag?.GenericValue, 0, e.Name,
                            p.PropertyType.Text, DetailOf(p),
                            // BLANK WHEN IT IS THE EXPORT'S OWN. Writing the class on every row
                            // says nothing on the rows where it is obvious and buries the rows
                            // where it is not.
                            where.Class == e.ExportType ? "" : where.Class,
                            real: true, maps: mappings)));

                        // A PROPERTY THE SCHEMA DOES NOT DECLARE keeps its place in FILE order at
                        // the very end, rather than colliding at index 0 with the class's first.
                        if (where.Class.Length == 0)
                            rows[^1] = (int.MaxValue, fileOrder++, rows[^1].Node);
                    }

                    foreach (var a in Absent(e, mappings, present))
                        rows.Add((a.Depth, a.Index, new PropNode
                        {
                            Name = a.Name, Type = a.Type, Detail = a.Detail,
                            Display = DisplayName(a.Name, a.Type == "BoolProperty"),
                            // AN ABSENT ENUM STILL GETS ITS LIST, for the reason an absent bool
                            // still gets a box: the property exists, and the rehearsal is what
                            // decides whether this particular one can be written.
                            Choices = ChoicesFor(a.Type, a.Detail, mappings),
                            Stored = false, Export = e.Name,
                            DeclaredBy = a.DeclaredBy == e.ExportType ? "" : a.DeclaredBy,
                        }));

                    // OWN FIRST, THEN UP THE CHAIN, and in the schema's own order within each
                    // class. UE5 groups a Details panel by CATEGORY, and no category survived
                    // cooking -- measured 2026-09-22, there is no UClassCookedMetaData in any
                    // package this game ships and a usmap carries no metadata in any version. So
                    // the declaring class is the honest substitute: a real fact about the
                    // property rather than a grouping invented here.
                    foreach (var r in rows.OrderBy(r => r.Depth).ThenBy(r => r.Index))
                        node.Children.Add(r.Node);

                    read.Exports.Add(node);
                }
                return read;
            });
        }
        catch (Exception ex)
        {
            // A NAMED FAILURE, never an empty tree. An asset that would not open and an asset with
            // no properties look identical on screen, and only one of them is a bug.
            return new AssetRead
            {
                FilePath = filePath, PackagePath = package, Container = container,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// One property, by its CONTENTS rather than by its type name.
    ///
    /// A row reading "AlternativeRootMeshSlotName   FStructFallback" answers nothing, and that is
    /// the property deciding what a helmet is parented to. The wrappers are the reason: a
    /// property's GenericValue is never the struct itself — it is an FScriptStruct around it, or
    /// an FInstancedStruct around that — so stopping at any one of them shows a type where a
    /// value was wanted.
    /// </summary>
    private static PropNode Node(string name, object? value, int depth, string export = "",
                                 string type = "", string detail = "", string declaredBy = "",
                                 bool real = false, TypeMappings? maps = null)
    {
        // A DISPLAY NAME ONLY FOR A NAME SOMEBODY DECLARED. See PropNode.Display: `[0]` and
        // `key[3]` are this reader's own labels and an identifier splitter mangles them.
        var display = real ? DisplayName(name, type == "BoolProperty") : "";
        // EVERY BRANCH BELOW BUILDS ITS OWN PropNode, and each one has to carry the export.
        //
        // Only the LEAF return at the bottom set it, so a property whose value is a container --
        // an array, a map, a struct -- came out with no export on it and was therefore
        // unaddressable: not editable, and not findable by <Export>.<Property> from outside. It
        // did not matter while only numbers could be edited, because a container is not a number.
        // It matters the moment an ARRAY is a thing somebody can add an entry to.
        //
        // Third time today that constructing an object field by field has silently dropped one.
        if (depth < MaxDepth)
            switch (value)
            {
                case FStructFallback sf:
                {
                    // NO LABEL on a struct. The reference leaves its value cell empty and lets the
                    // chevron say there is something inside, which is right: "struct" is
                    // a restatement of the arrow beside it.
                    var n = new PropNode
                    {
                        Name = name, Type = type, Detail = detail, DeclaredBy = declaredBy,
                        Display = display, Export = depth == 0 ? export : "",
                    };
                    foreach (var q in sf.Properties)
                        n.Children.Add(Node(q.Name.Text, q.Tag?.GenericValue, depth + 1,
                                            type: q.PropertyType.Text, detail: DetailOf(q),
                                            real: true));
                    return n;
                }

                case FScriptStruct fss:
                    return Node(name, fss.StructType, depth, export, type, detail, declaredBy, real);

                case FInstancedStruct fis:
                    return Node(name, fis.ScriptStruct?.StructType, depth, export, type, detail,
                                declaredBy, real);

                // Maps, whose KEYS are the interesting half: a Blueprint's default map is how the
                // game says "these specialisations exist", and a type name says nothing about
                // which ones.
                case UScriptMap map:
                {
                    var n = new PropNode
                    {
                        Name = name, Type = type, Detail = detail, DeclaredBy = declaredBy,
                        Display = display, Export = depth == 0 ? export : "",
                        Note = map.Properties.Count == 1 ? "1 entry" : $"{map.Properties.Count:N0} entries",
                    };
                    var i = 0;
                    foreach (var kv in map.Properties)
                    {
                        n.Children.Add(Node($"key[{i}]", kv.Key?.GenericValue, depth + 1));
                        n.Children.Add(Node($"val[{i}]", kv.Value?.GenericValue, depth + 1));
                        i++;
                    }
                    return n;
                }

                case UScriptArray arr:
                {
                    var n = new PropNode
                    {
                        Name = name, Type = type, DeclaredBy = declaredBy, Display = display,
                        Export = depth == 0 ? export : "",
                        // AN ARRAY'S DETAIL IS ITS INNER TYPE, and the array itself knows it even
                        // when the tag that declared it did not -- an inner read off the data is
                        // better than an inner remembered from the schema.
                        Detail = arr.InnerType.Length > 0 ? arr.InnerType : detail,
                        // THE COUNT ONLY. It used to read "3 x ObjectProperty", and the inner
                        // type now has a column of its own -- saying it twice in two spellings is
                        // worse than saying it once in UE's.
                        Note = arr.Properties.Count == 1 ? "1 item" : $"{arr.Properties.Count:N0} items",
                    };
                    for (var k = 0; k < arr.Properties.Count; k++)
                        n.Children.Add(Node($"[{k}]", arr.Properties[k]?.GenericValue, depth + 1,
                                            type: arr.InnerType));
                    return n;
                }

                case System.Collections.IEnumerable seq and not string:
                {
                    var n = new PropNode
                    {
                        Name = name, Type = type, Detail = detail, DeclaredBy = declaredBy,
                        Display = display, Export = depth == 0 ? export : "",
                    };
                    var i = 0;
                    foreach (var item in seq) n.Children.Add(Node($"[{i++}]", item, depth + 1));
                    if (i == 0) return new PropNode { Name = name, Note = "empty" };
                    n = Retitle(n, i == 1 ? "1 item" : $"{i:N0} items");
                    return n;
                }
            }

        // THE ADDRESS ONLY AT DEPTH ZERO, which is a property sitting directly on the export.
        // Everything deeper is inside a struct, a map or an array, and has no export.property
        // name for the build to find it by.
        // AN ENUM'S NUMBER, RESOLVED HERE, and that is what makes a dropdown need no new kind of
        // change at all. CUE4Parse hands an enum back as its NAME, so AsNumber returns nothing and
        // the property reads as uneditable -- but the byte on disk is a byte like any other, and
        // ScalarEdit's Kind.Byte has been writing those for a year. Looking the name up in the
        // enum's own table turns "which member" back into a number, and every existing step from
        // Editable through the rehearsal to ModOverride.NumberChange then works unchanged.
        var flat = Flat(value);
        var choices = depth == 0 ? ChoicesFor(type, detail, maps) : [];
        var number = depth == 0 ? AsNumber(value) : null;
        if (number is null && choices.Count > 0)
            number = choices.FirstOrDefault(c => string.Equals(
                LeafOfEnum(c.Name), LeafOfEnum(flat), StringComparison.OrdinalIgnoreCase)) is
                { Name.Length: > 0 } hit ? hit.Value : null;

        return new PropNode
        {
            Name = name,
            Value = flat,
            Type = type,
            Detail = detail,
            DeclaredBy = declaredBy,
            Display = display,
            Choices = choices,
            Export = depth == 0 ? export : "",
            Number = number,
        };
    }

    /// <summary>
    /// The value as a double, when it is a number or a bool, and null otherwise.
    ///
    /// A bool counts because that is how the write side sees one: a true bool is a single byte,
    /// which ScalarEdit reads and writes as a byte. A FALSE one has no bytes at all and cannot be
    /// located — which is why this only decides whether to OFFER a box, and the rehearsal decides
    /// whether the edit is possible.
    /// </summary>
    private static double? AsNumber(object? v) => v switch
    {
        float f => f,
        double d => d,
        int i => i,
        uint i => i,
        short i => i,
        ushort i => i,
        long i => i,
        ulong i => i,
        sbyte i => i,
        byte i => i,
        bool b => b ? 1 : 0,
        _ => null,
    };

    /// <summary>A copy carrying a note, since the children are already attached to the original.</summary>
    private static PropNode Retitle(PropNode from, string note)
    {
        // EVERY FIELD, not the ones that existed when this was written. Retitle rebuilds the
        // node, so a field added to PropNode and not added here is silently lost on exactly the
        // rows that go through it -- which is how AllowedSlots, a GameplayTagContainer and so an
        // IEnumerable, came out with no declaring class while its neighbours had theirs.
        var n = new PropNode
        {
            Name = from.Name, Value = from.Value, Note = note,
            Type = from.Type, Detail = from.Detail, Display = from.Display,
            Stored = from.Stored, DeclaredBy = from.DeclaredBy,
            Export = from.Export, Number = from.Number,
        };
        n.Children.AddRange(from.Children);
        return n;
    }

    /// <summary>
    /// Every property the class declares that the file does not carry, own ones first.
    ///
    /// The order is the mappings' own index space, which is the order the SERIALISER uses: a
    /// class's own properties occupy [0, PropertyCount) and anything above that belongs to its
    /// super, after subtracting that count. So "own first, then inherited" is not a presentation
    /// choice -- it is how the schema is laid out.
    /// </summary>
    private static IEnumerable<(string Name, string Type, string Detail, string DeclaredBy,
                                int Depth, int Index)> Absent(
        UObject e, TypeMappings? mappings, HashSet<string> present)
    {
        var seen = new HashSet<string>(present, StringComparer.Ordinal);
        foreach (var d in Walk(e, mappings))
        {
            if (!seen.Add(d.Name)) continue;   // present, or a child shadowing a parent
            yield return (d.Name, d.Type, d.Detail, d.Class, d.Depth, d.Index);
        }
    }

    /// <summary>
    /// Where every declared property SITS in the class chain: which class declares it, how far up,
    /// and its index within that class.
    /// </summary>
    private static Dictionary<string, (string Class, int Depth, int Index)> Declarations(
        UObject e, TypeMappings? mappings)
    {
        var at = new Dictionary<string, (string, int, int)>(StringComparer.Ordinal);
        foreach (var d in Walk(e, mappings))
            at.TryAdd(d.Name, (d.Class, d.Depth, d.Index));
        return at;
    }

    /// <summary>
    /// The class chain's properties, own class first and then upwards.
    ///
    /// The order within a class is the mappings' own index space, which is the order the
    /// SERIALISER uses: a class's own properties occupy [0, PropertyCount) and anything above
    /// belongs to its super after subtracting that count. So "own first, then inherited" is not a
    /// presentation choice -- it is how the schema is laid out.
    /// </summary>
    private static IEnumerable<(string Name, string Type, string Detail, string Class,
                                int Depth, int Index)> Walk(UObject e, TypeMappings? mappings)
    {
        var schema = SchemaFor(e, mappings, out _);
        var walk = schema;

        for (var depth = 0; walk is not null && depth < 64; depth++)
        {
            var props = walk.Properties;
            if (props is not null)
            {
                // THE DICTIONARY KEY IS THE SCHEMA INDEX, AND PropertyInfo.Index IS NOT.
                //
                // The usmap parser stores each property at `properties[realIndex + j]` while
                // setting the clone's own `Index = j` -- so every property of ArrayDim 1 carries
                // Index 0, and a SerializedStruct does the same with `Math.Min(i, ArrayDim - 1)`.
                // Grouping by that field collapsed an entire class to ONE property: a slot
                // fragment declaring eleven showed one, and the under-count looked like a missing
                // schema rather than a wrong key.
                //
                // A STATIC ARRAY is the reason a dedupe is needed at all -- its N slots hold N
                // clones of one property -- so the dedupe is by NAME, which is what actually
                // repeats.
                var emitted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in props.OrderBy(kv => kv.Key))
                {
                    var pi = kv.Value;
                    if (!emitted.Add(pi.Name)) continue;
                    var t = pi.MappingType;
                    yield return (pi.Name, t.Type, DetailOf(t), walk.Name, depth, kv.Key);
                }
            }
            walk = walk.Super?.Value;
        }
    }

    /// <summary>
    /// The schema for one export's class: the PACKAGE's copy for a Blueprint-generated class, the
    /// MAPPINGS' for a native one.
    ///
    /// A native class arrives as a UScriptClass stub whose ChildProperties is null, and building
    /// a SerializedStruct from it dereferences that null before the constructor's first line
    /// finishes -- which is why the two cases cannot share a branch.
    /// </summary>
    public static Struct? SchemaFor(UObject e, TypeMappings? mappings, out string? why)
    {
        why = null;
        if (mappings is null) { why = "no mappings loaded"; return null; }

        if (e.Class?.Object?.Value is UStruct struc && struc is not UScriptClass &&
            struc.ChildProperties is not null)
            return new SerializedStruct(mappings, struc);

        if (mappings.Types.TryGetValue(e.ExportType, out var native) && native is not null)
            return native;

        why = $"'{e.ExportType}' is neither a loaded struct in this package nor a type in the mappings";
        return null;
    }

    /// <summary>What narrows a property's type, from the schema's side.</summary>
    private static string DetailOf(PropertyType t)
        => t.StructType ?? t.EnumName ?? t.InnerType?.Type ?? "";

    /// <summary>
    /// The value table for an enum-shaped property, or nothing.
    ///
    /// ORDERED BY VALUE, because that is the order the enum declares them in and the order
    /// somebody reading a dropdown expects. The _MAX member every UENUM carries is dropped: it is
    /// a count, not a choice, and offering it would let somebody pick a value the game has no
    /// member for.
    /// </summary>
    private static IReadOnlyList<(long Value, string Name)> ChoicesFor(
        string type, string detail, TypeMappings? maps)
    {
        if (type is not ("ByteProperty" or "EnumProperty")) return [];
        if (detail.Length == 0 || maps is null) return [];
        if (!maps.Enums.TryGetValue(detail, out var table) || table is null) return [];

        return table.Where(kv => !kv.Value.EndsWith("_MAX", StringComparison.Ordinal))
                    .OrderBy(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
    }

    /// <summary>
    /// The member name out of whichever form it arrives in.
    ///
    /// A value reads back as `EBitReactorThing::Member` from one path and as the bare `Member`
    /// from another, and matching those two as strings finds nothing.
    /// </summary>
    public static string LeafOfEnum(string name)
    {
        var at = name.LastIndexOf("::", StringComparison.Ordinal);
        return at < 0 ? name : name[(at + 2)..];
    }

    /// <summary>What narrows a property's type: the struct, the enum, or an array's inner.</summary>
    private static string DetailOf(FPropertyTag p)
        => p.TagData?.StructType ?? p.TagData?.EnumName ?? p.TagData?.InnerType ?? "";

    /// <summary>
    /// The PACKAGE a reference names, out of the way CUE4Parse prints one.
    ///
    /// An object reference reads back as `Class'/Game/A/B.B_C'` and a soft one as `/Game/A/B.B`,
    /// so the package is the path with the class, the quotes and the OBJECT NAME after the final
    /// dot taken off. Two slashes are demanded before it is believed to be a path at all --
    /// `ObjectProperty[3]`, which is how an array prints, has none, and a "follow" that opened
    /// nothing would be worse than no arrow.
    /// </summary>
    public static string? PackagePathIn(string value)
    {
        var at = value.IndexOf('/');
        if (at < 0) return null;

        var rest = value[at..];
        var end = rest.IndexOfAny(['\'', ' ', ',', ')']);
        if (end >= 0) rest = rest[..end];

        var dot = rest.LastIndexOf('.');
        if (dot > 0) rest = rest[..dot];

        return rest.Count(c => c == '/') >= 2 ? rest : null;
    }

    /// <summary>
    /// UE's own `FName::NameToDisplayString`, followed rather than approximated.
    ///
    /// This is the one piece of UE5's Details panel that can be reproduced EXACTLY from a shipped
    /// game, because it is an algorithm rather than a lookup: categories and tooltips are metadata
    /// and metadata did not survive cooking, but a display name is computed from the identifier
    /// every time the editor draws it.
    ///
    /// The subtle part is the RUN. A stretch of capitals and digits is kept together, so
    /// `DrawScale3D` comes out "Draw Scale 3D" rather than "Draw Scale 3 D", and a lower-case
    /// letter is what breaks the run. `bIsBool` drops a leading `b` only when a capital follows
    /// it, so `bVisible` becomes "Visible" while `bone` is left alone.
    /// </summary>
    public static string DisplayName(string name, bool isBool = false)
    {
        if (name.Length == 0) return name;

        var outp = new System.Text.StringBuilder(name.Length + 8);
        var inARun = false;
        var wasSpace = false;
        var wasOpenParen = false;
        var wasNumber = false;
        var wasMinus = false;

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            var lower = char.IsLower(ch);
            var upper = char.IsUpper(ch);
            var digit = char.IsDigit(ch);

            if (i == 0 && isBool && ch == 'b' && name.Length > 1 && char.IsUpper(name[1]))
                continue;

            if ((upper || (digit && !wasMinus)) && !inARun && !wasOpenParen && !wasNumber)
            {
                if (!wasSpace && outp.Length > 0) { outp.Append(' '); wasSpace = true; }
                inARun = true;
            }

            if (lower) inARun = false;
            if (ch == '_') { ch = ' '; inARun = true; }

            if (outp.Length == 0 || wasSpace || wasOpenParen) ch = char.ToUpperInvariant(ch);

            wasSpace = ch == ' ';
            wasOpenParen = ch == '(';
            wasNumber = digit || (wasNumber && (ch == '.' || ch == '-'));
            wasMinus = ch == '-';

            outp.Append(ch);
        }

        return outp.ToString();
    }

    /// <summary>
    /// A property type as UNREAL writes it, not as the serialiser names it.
    ///
    /// `BoolProperty` is the name of the thing that serialises a bool; `bool` is the type. Showing
    /// the first is a reader talking about its own plumbing, and the question this answers is what
    /// somebody who had made this asset in the editor would recognise.
    ///
    /// The detail is the struct, the enum or an array's inner type, and it is used where UE would
    /// use it -- `FGameplayTag` rather than a bare struct, `TArray&lt;TSoftObjectPtr&gt;` rather than a
    /// bare array. An unknown type is returned UNCHANGED rather than guessed at: a wrong spelling
    /// is worse than an honest one nobody recognises.
    /// </summary>
    public static string UeSpelling(string type, string detail = "")
    {
        static string Struct(string d) => d.Length == 0 ? "struct"
            : d.StartsWith('F') && d.Length > 1 && char.IsUpper(d[1]) ? d : "F" + d;

        // A CONTAINER'S INNER, WITHOUT ITS OWN EMPTY BRACKETS. Spelling the inner on its own gives
        // `TSoftObjectPtr<>`, and nesting that produced `TArray<TSoftObjectPtr<>>` -- two pairs of
        // brackets where UE writes one. The empty pair says "this reader does not know what is
        // inside", which is true at the top level and noise one level down.
        static string Inner(string d)
        {
            if (d.Length == 0) return "";
            var s = UeSpelling(d);
            return s.EndsWith("<>", StringComparison.Ordinal) ? s[..^2] : s;
        }

        return type switch
        {
            "" => "",
            "BoolProperty" => "bool",
            "FloatProperty" => "float",
            "DoubleProperty" => "double",
            "IntProperty" => "int32",
            "Int8Property" => "int8",
            "Int16Property" => "int16",
            "Int64Property" => "int64",
            "UInt16Property" => "uint16",
            "UInt32Property" => "uint32",
            "UInt64Property" => "uint64",
            "ByteProperty" => detail.Length > 0 ? detail : "uint8",
            "EnumProperty" => detail.Length > 0 ? detail : "enum",
            "StrProperty" or "Utf8StrProperty" or "AnsiStrProperty" => "FString",
            "NameProperty" => "FName",
            "TextProperty" => "FText",
            "StructProperty" => Struct(detail),
            "ObjectProperty" => "UObject*",
            "ClassProperty" => "TSubclassOf<>",
            "SoftObjectProperty" => "TSoftObjectPtr<>",
            "SoftClassProperty" => "TSoftClassPtr<>",
            "LazyObjectProperty" => "TLazyObjectPtr<>",
            "WeakObjectProperty" => "TWeakObjectPtr<>",
            "AssetObjectProperty" => "FSoftObjectPath",
            "InterfaceProperty" => "TScriptInterface<>",
            "DelegateProperty" or "MulticastDelegateProperty" => "delegate",
            "FieldPathProperty" => "TFieldPath<>",
            "ArrayProperty" => $"TArray<{Inner(detail)}>",
            "SetProperty" => $"TSet<{Inner(detail)}>",
            "MapProperty" => "TMap<,>",
            "OptionalProperty" => "TOptional<>",
            _ => type,
        };
    }

    /// <summary>
    /// One line, always. A newline inside a value would break a row into two and leave the second
    /// one nameless.
    /// </summary>
    private static string Flat(object? value)
        => value?.ToString()?.Replace('\n', ' ').Replace('\r', ' ') ?? "";

    /// <summary>
    /// Which container served this package.
    ///
    /// ASK THE ENTRY THE PROVIDER ACTUALLY SERVES, not the first mounted container holding the
    /// path. Those differed on 2026-09-07: a community mod's override mounted beside the game was
    /// the copy every property came from, while a scan of the mount list still named
    /// pakchunk1-Windows.utoc, because that list is not in the order the file table was filled.
    /// A "which copy" line naming the wrong copy is worse than no line.
    /// </summary>
    public static string? WhichContainer(IFileProvider? provider, string package)
    {
        try
        {
            if (provider is not null && provider.Files.TryGetValue(package, out var served)
                && served is VfsEntry entry)
                return Path.GetFileName(entry.Vfs.Name);

            if (provider is not IVfsFileProvider vfs) return null;
            foreach (var reader in vfs.MountedVfs)
                if (reader.Files.ContainsKey(package))
                    return Path.GetFileName(reader.Name);
        }
        catch (Exception) { /* a name is a convenience; never take the read down for one */ }
        return null;
    }
}

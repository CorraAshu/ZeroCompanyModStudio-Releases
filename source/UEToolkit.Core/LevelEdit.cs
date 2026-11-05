using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;

namespace UEToolkit.Core;

/// <summary>
/// MOVING SOMETHING IN A COOKED LEVEL.
///
/// Two things make a level different from every asset edited here so far, and both are addressing
/// problems rather than writing problems:
///
///   THE NAMES REPEAT.  A sublevel holds fifty `StaticMeshComponent0` and four `Root`.
///                      `PropertyAdd.Locate` takes the first match, which in a level is a silent
///                      wrong-target. The ACTORS are uniquely named, so the address starts there.
///
///   THE ACTOR HAS NO TRANSFORM.  Measured on the shipped game: a `TacticalPoint` serialises
///                      `OwningTeam` and `RootComponent` and nothing else, and a
///                      `BitReactorStaticMeshActor` is the same. The position lives on the
///                      component the actor points at, so the walk has to FOLLOW that reference.
///
/// Hence a path like `RootComponent.RelativeLocation.Z`: hop along object references, then into
/// the struct, then to one component of it.
///
/// The struct itself is NOT descendable by the ordinary walk. `FVector` and `FRotator` have native
/// serialisers in UE5 -- three doubles, no property header -- so there is nothing for
/// `PropertyAdd.Descend` to read. Their layout is fixed and known, so this addresses them by
/// offset instead, and <see cref="Read"/> exists so that arithmetic can be CHECKED against what
/// the parser says the struct holds before anything is written.
/// </summary>
public static class LevelEdit
{
    /// <summary>Bytes of an FVector / FRotator in UE5: three doubles, large-world-coordinates.</summary>
    public const int VectorBytes = 24;

    /// <summary>
    /// The final segment of a path, as an offset into the struct.
    ///
    /// FVector is X, Y, Z. FRotator is Pitch, Yaw, Roll -- declaration order, which is also
    /// serialisation order, and NOT the order the editor's own fields are drawn in. Both spellings
    /// are accepted because `P/Y/R` is what a dump prints and `Pitch/Yaw/Roll` is what the
    /// property is called.
    /// </summary>
    public static int? ComponentOffset(string component) => component.ToUpperInvariant() switch
    {
        "X" or "PITCH" or "P" => 0,
        "Y" or "YAW" => 8,
        "Z" or "ROLL" or "R" => 16,
        _ => null,
    };

    public sealed record Found(int ExportIndex, string ExportName, string ExportClass,
                               int StructAt, int At, double Value,
                               double C0, double C1, double C2);

    /// <summary>
    /// Open the package, find the uniquely-named export, then hop along object references.
    ///
    /// Shared by the vector and the object-reference locators, because a second copy of "walk to
    /// the thing the path names" is a second place for the one-based FPackageIndex to be got
    /// wrong.
    /// </summary>
    private static bool OpenAndFind(IFileProvider provider, byte[] raw, string packageName,
                                    string actor, string[] hops,
                                    out UObject[] exports, out int index, out string? why)
    {
        why = null; index = -1; exports = Array.Empty<UObject>();
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null) { why = $"no mounted container holds {packageName}"; return false; }
            exports = PropertyAdd.Open(provider, raw, packageName, container)
                                 .ExportsLazy.Select(x => x.Value).ToArray();
        }
        // THE TYPE AND THE FRAME, not just the message. "Index was outside the bounds of the
        // array." names neither what threw nor where, and that is the entire diagnostic a build
        // gets when a rewritten package will not open -- which is exactly when it matters most.
        catch (Exception e)
        {
            var frame = (e.StackTrace ?? "")
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? "";
            why = $"{e.GetType().Name}: {e.Message}{(frame.Length > 0 ? $"  [{frame}]" : "")}";
            return false;
        }

        // AN EXPORT INDEX, WRITTEN "#427", BECAUSE A NAME CANNOT REACH EVERY ACTOR.
        //
        // UE reuses export names freely -- a cooked hub level has THREE exports called
        // BitReactorStaticMeshActor_5 -- so the refusal below is not a rare edge case, it is the
        // only answer available for a large share of a level's actors. Without this there is
        // simply no way to address them, and "start from something unique" becomes "give up".
        //
        // An index is a POSITION, so it is only meaningful against the exact bytes it was read
        // from. That is safe here for the same reason a move is: every edit is addressed against
        // the PRISTINE package, and the build refuses to write unless what is already there
        // matches the `was` it was given. An index that drifts hits the wrong mesh name and stops.
        if (actor.StartsWith('#') && int.TryParse(actor[1..], out var direct))
        {
            if (direct < 0 || direct >= exports.Length)
            { why = $"export #{direct} is past the end of the map ({exports.Length} exports)"; return false; }
            index = direct;
        }
        else
        {

        var matches = new List<int>();
        for (var i = 0; i < exports.Length; i++)
            if (string.Equals(exports[i]?.Name, actor, StringComparison.OrdinalIgnoreCase)) matches.Add(i);

        if (matches.Count == 0) { why = $"no export named '{actor}'"; return false; }
        if (matches.Count > 1)
        {
            // NAME THE COST. "ambiguous" on its own sends somebody looking for a typo, when what
            // they have to do is pick a different, uniquely-named actor to start from.
            why = $"'{actor}' names {matches.Count} exports (#{string.Join(", #", matches)}) -- " +
                  "start from a unique name, or name one of those exports directly as '#427'";
            return false;
        }

        index = matches[0];
        }

        foreach (var hop in hops)
        {
            if (!PropertyAdd.LocateAt(provider, raw, packageName, index, hop,
                                      out var hopAt, out var hopLen, out var hopWhy))
            { why = $"{hop}: {hopWhy}"; return false; }

            // An object reference inside a package is an FPackageIndex: four bytes, and positive
            // when it points at an export -- one-based, so the export it names is value - 1.
            if (hopLen != 4)
            { why = $"'{hop}' is {hopLen} bytes, which is not an object reference"; return false; }

            var pointer = BitConverter.ToInt32(raw, hopAt);
            if (pointer <= 0)
            { why = $"'{hop}' points at {(pointer == 0 ? "nothing" : "an import")}, not an export in this package"; return false; }

            index = pointer - 1;
            if (index >= exports.Length)
            { why = $"'{hop}' points at export #{index}, past the end of the map"; return false; }
        }
        return true;
    }

    /// <summary>Where an object reference sits, what it points at now, and what it would say.</summary>
    public sealed record Pointer(int ExportIndex, string ExportName, string ExportClass,
                                 int At, int Value, string Points);

    /// <summary>
    /// SWAPPING WHAT A COMPONENT DRAWS, WITHOUT GROWING THE PACKAGE.
    ///
    /// "Delete this chair and put a crate there" sounds like it needs the thing this toolkit has
    /// never managed -- adding an export to a cooked .umap, which means rewriting the export map,
    /// the name map, the imports and every offset behind them. It does not. A component's mesh is
    /// an `FPackageIndex`: FOUR BYTES naming an import slot. Point it at a slot the package
    /// already has and the chair becomes a crate; point it at zero and the chair draws nothing.
    ///
    /// So the edit is the same shape as a move -- overwrite a value that is already there -- and
    /// it inherits everything a move already has: the build applies it, the verifier reads it
    /// back, and it cannot corrupt a package because nothing changes size.
    ///
    /// THE LIMIT, SAID PLAINLY: the mesh has to be one the package ALREADY references. A level
    /// that draws no crate anywhere cannot be given one this way, and this refuses rather than
    /// writing an index that resolves to nothing. Adding a genuinely new asset is still the gate.
    ///
    /// The new value is COPIED FROM A DONOR export in the same package rather than assembled from
    /// the import map, because a donor proves the index resolves -- the game is already drawing
    /// it. An index worked out from the map and wrong by one is a mesh that silently does not
    /// appear, which is the failure this whole tool keeps being built to avoid.
    /// </summary>
    public static Pointer? LocatePointer(IFileProvider provider, byte[] raw, string packageName,
                                         string actor, string path, out string? why)
    {
        why = null;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 1)
        { why = $"'{path}' needs at least a property, e.g. RootComponent.StaticMesh"; return null; }

        if (!OpenAndFind(provider, raw, packageName, actor, segments[..^1], out var exports,
                         out var index, out why))
            return null;

        if (!PropertyAdd.LocateAt(provider, raw, packageName, index, segments[^1],
                                  out var at, out var length, out var propWhy))
        { why = $"{segments[^1]}: {propWhy}"; return null; }

        if (length != 4)
        { why = $"'{segments[^1]}' is {length} bytes, which is not an object reference"; return null; }

        var value = BitConverter.ToInt32(raw, at);
        return new Pointer(index, exports[index]?.Name ?? "?", exports[index]?.ExportType ?? "?",
                           at, value, Describe(exports, index, segments[^1]));
    }

    private static string Describe(UObject[] exports, int index, string property)
    {
        try
        {
            var o = exports[index]?.GetOrDefault<FPackageIndex?>(property, null);
            return o?.ResolvedObject?.GetPathName() ?? "(nothing)";
        }
        catch { return "(unreadable)"; }
    }

    /// <summary>
    /// Point <paramref name="path"/> at whatever draws <paramref name="mesh"/> in this same
    /// package, or at NOTHING when <paramref name="mesh"/> is empty. Returns the edited bytes.
    /// </summary>
    /// <summary>
    /// Point a component at an FPackageIndex the caller ALREADY HAS.
    ///
    /// <see cref="SetPointer"/> takes a mesh by NAME and finds a donor export that already draws
    /// it, because for a swap that is the honest way to get an index that is known to resolve. For
    /// a freshly ADDED import there is no donor by definition -- nothing in the package draws it
    /// yet -- so that search scans every export, finds nothing, and refuses. On the hub's Pit that
    /// took FIFTY-FIVE SECONDS before saying no, which is what a build looks like when it appears
    /// to hang.
    ///
    /// AddImport already returns the index. There is nothing to search for.
    /// </summary>
    public static byte[]? SetPointerTo(IFileProvider provider, byte[] raw, string packageName,
                                       string actor, string path, int packageIndex,
                                       out int at, out string? why)
    {
        at = 0;
        var found = LocatePointer(provider, raw, packageName, actor, path, out why);
        if (found is null) return null;

        var written = (byte[]) raw.Clone();
        BitConverter.GetBytes(packageIndex).CopyTo(written, found.At);

        // READ BACK THROUGH THE WALK, the same rule a move follows: checking the bytes at an
        // offset only confirms the copy landed where it was told to.
        var back = LocatePointer(provider, written, packageName, actor, path, out var backWhy);
        if (back is null) { why = $"written, but could not be read back: {backWhy}"; return null; }
        if (back.Value != packageIndex)
        { why = $"{actor}.{path} was set to {packageIndex} and reads back {back.Value}"; return null; }

        at = found.At;
        why = null;
        return written;
    }

    public static byte[]? SetPointer(IFileProvider provider, byte[] raw, string packageName,
                                     string actor, string path, string mesh,
                                     out int at, out int wrote, out string? why)
    {
        at = 0; wrote = 0;
        var found = LocatePointer(provider, raw, packageName, actor, path, out why);
        if (found is null) return null;
        at = found.At;

        if (mesh.Length == 0)
        {
            wrote = 0;                       // FPackageIndex 0 is the null object
        }
        else if (DonorValue(provider, raw, packageName, path, mesh, out var donor, out var donorWhy) is { } v)
        {
            wrote = v;
            _ = donor;
        }
        else { why = donorWhy; return null; }

        var edited = (byte[]) raw.Clone();
        BitConverter.GetBytes(wrote).CopyTo(edited, at);
        return edited;
    }

    /// <summary>
    /// The FPackageIndex some OTHER export in this package uses to draw <paramref name="mesh"/>.
    ///
    /// Matched on the asset's leaf name, because that is what somebody reading the level browser
    /// has in front of them. Ambiguity is refused rather than guessed: two different meshes whose
    /// names both end the same way is a mod that draws the wrong one and looks like art.
    /// </summary>
    private static int? DonorValue(IFileProvider provider, byte[] raw, string packageName,
                                   string path, string mesh, out string donor, out string? why)
    {
        donor = ""; why = null;
        var property = path[(path.LastIndexOf('.') + 1)..];

        var map = DonorMap(provider, raw, packageName, property, out why);
        if (map is null) return null;

        if (!map.TryGetValue(mesh, out var hits))
        {
            why = $"nothing in {packageName} draws '{mesh}' -- this can only point at a mesh the " +
                  "package already references, so pick one the level already uses";
            return null;
        }
        if (hits.Count > 1)
        {
            why = $"'{mesh}' matches {hits.Count} different references in {packageName} " +
                  $"({string.Join(", ", hits.Values)}) -- name it more precisely";
            return null;
        }

        donor = hits.Values.First();
        return hits.Keys.First();
    }

    /// <summary>
    /// EVERY MESH THIS PACKAGE CAN BE POINTED AT, by leaf name, with the index each one would be
    /// written as.
    ///
    /// Built by the SAME scan <see cref="DonorValue"/> uses, deliberately: a picker built from the
    /// level browser instead would offer meshes the writer then refuses, because an actor that
    /// inherits its mesh from a Blueprint archetype SHOWS a mesh while carrying no four bytes of
    /// its own to copy. Offering a choice that cannot be honoured is worse than a short list.
    /// </summary>
    private static Dictionary<string, Dictionary<int, string>>? DonorMap(
        IFileProvider provider, byte[] raw, string packageName, string property, out string? why)
    {
        why = null;
        UObject[] exports;
        try
        {
            var container = AssetCopy.CueHeaderFor(provider, packageName, pristine: true);
            if (container is null) { why = $"no mounted container holds {packageName}"; return null; }
            exports = PropertyAdd.Open(provider, raw, packageName, container)
                                 .ExportsLazy.Select(x => x.Value).ToArray();
        }
        catch (Exception e) { why = e.Message; return null; }

        var map = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < exports.Length; i++)
        {
            string? full;
            try { full = exports[i]?.GetOrDefault<FPackageIndex?>(property, null)?.ResolvedObject?.GetPathName(); }
            catch { continue; }
            if (full is null) continue;

            // THE PROPERTY HAS TO BE IN THIS EXPORT'S OWN BYTES. Resolving is not enough -- a
            // cooked component serialises only what it OVERRIDES, so one that takes its mesh from
            // its archetype reads back a mesh and has nothing here to copy an index out of.
            if (!PropertyAdd.LocateAt(provider, raw, packageName, i, property, out var a, out var len, out _)) continue;
            if (len != 4) continue;

            var leaf = full[(full.LastIndexOf('/') + 1)..];
            var bare = leaf[(leaf.LastIndexOf('.') + 1)..];
            if (!map.TryGetValue(bare, out var hits)) map[bare] = hits = new Dictionary<int, string>();
            hits[BitConverter.ToInt32(raw, a)] = exports[i]?.Name ?? "?";
        }
        return map;
    }

    /// <summary>
    /// The mesh names a component in this package can be pointed at, sorted, and only the ones
    /// that are UNAMBIGUOUS -- a name resolving to two different assets is refused by the writer,
    /// so it has no business being offered as a choice.
    /// </summary>
    public static List<string> Donors(IFileProvider provider, byte[] raw, string packageName,
                                      string property, out string? why)
    {
        var map = DonorMap(provider, raw, packageName, property, out why);
        if (map is null) return new List<string>();
        return map.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key)
                  .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }


    /// <summary>
    /// Walk <paramref name="path"/> from the uniquely-named export <paramref name="actor"/> and
    /// report where it lands, WITHOUT writing anything.
    ///
    /// Refuses an ambiguous starting name rather than taking the first, which is the whole reason
    /// this is not just a call to <see cref="PropertyAdd.Locate"/>.
    /// </summary>
    public static Found? Locate(IFileProvider provider, byte[] raw, string packageName,
                                string actor, string path, out string? why)
    {
        why = null;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        { why = $"'{path}' needs at least a struct and a component, e.g. RelativeLocation.Z"; return null; }

        var offset = ComponentOffset(segments[^1]);
        if (offset is null)
        { why = $"'{segments[^1]}' is not X/Y/Z or Pitch/Yaw/Roll"; return null; }

        var structName = segments[^2];
        var hops = segments[..^2];

        if (!OpenAndFind(provider, raw, packageName, actor, hops, out var exports, out var index, out why))
            return null;

        if (!PropertyAdd.LocateAt(provider, raw, packageName, index, structName,
                                  out var at, out var length, out var structWhy))
        { why = $"{structName}: {structWhy}"; return null; }

        if (length != VectorBytes)
        { why = $"'{structName}' is {length} bytes, not the {VectorBytes} of a vector"; return null; }

        return new Found(index, exports[index]?.Name ?? "?", exports[index]?.ExportType ?? "?",
                         at, at + offset.Value, BitConverter.ToDouble(raw, at + offset.Value),
                         BitConverter.ToDouble(raw, at),
                         BitConverter.ToDouble(raw, at + 8),
                         BitConverter.ToDouble(raw, at + 16));
    }

    /// <summary>
    /// Write one component. Eight bytes for eight bytes, so no offset moves and no header needs
    /// repairing -- the same shape of edit as every number this toolkit already ships.
    ///
    /// <paramref name="was"/> is CHECKED, not decorative: it is the only thing standing between a
    /// path that resolved to the wrong export and a mod that quietly moves a different prop.
    /// </summary>
    public static byte[] Set(IFileProvider provider, byte[] raw, string packageName,
                             string actor, string path, double was, double now,
                             out int at, out string? why)
    {
        at = -1;
        var found = Locate(provider, raw, packageName, actor, path, out why);
        if (found is null) return raw;

        // NaN MEANS "DO NOT CHECK", and it is spelled out rather than left to arithmetic: a
        // comparison against NaN is false, so the guard would have been skipped by accident and
        // nobody reading it would know that was intended. A spawned actor is the case -- the copy
        // holds its DONOR's coordinates, and the caller is saying where the new one goes, not what
        // it used to be.
        if (!double.IsNaN(was) && Math.Abs(found.Value - was) > 0.001)
        {
            why = $"{actor}.{path} reads {found.Value}, not the {was} this edit expects";
            return raw;
        }

        var written = (byte[]) raw.Clone();
        BitConverter.GetBytes(now).CopyTo(written, found.At);

        // READ BACK THROUGH THE WALK, not out of the bytes just written: checking the bytes at an
        // offset only confirms the copy landed where it was told to, which was never in doubt.
        var back = Locate(provider, written, packageName, actor, path, out var backWhy);
        if (back is null) { why = $"written, but could not be read back: {backWhy}"; return raw; }
        if (Math.Abs(back.Value - now) > 0.001)
        { why = $"{actor}.{path} was set to {now} and reads back {back.Value}"; return raw; }

        at = found.At;
        why = null;
        return written;
    }
}

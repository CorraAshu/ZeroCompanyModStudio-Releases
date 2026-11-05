using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// PROVE A PROPERTY EDIT BEFORE RECORDING IT.
///
/// The Studio's property panel offers a value to type into. The question that matters is not
/// whether the panel accepted the number, it is whether the BUILD will be able to write it — and
/// those are different questions, because a cooked package stores only what was set. A property
/// left at its class default has no bytes to change and no offset to find, a false bool is
/// recorded in the header's zero mask with no bytes at all, and a property nested inside a struct
/// is not addressable by name from its export.
///
/// So this does not predict. It performs the real edit against the game's own bytes, through the
/// same <see cref="ScalarEdit.SetByName"/> the build uses, and throws the result away. What comes
/// back is the build's own answer, given before anything is written into the project rather than
/// hours later when a mod installs and does nothing.
///
/// That is the difference this repository keeps paying for: a counter of INTENT cannot witness
/// the work. "The panel accepted it" is intent. "It read back as 20 when written" is the work.
/// </summary>
public static class PropertyEdit
{
    /// <summary>
    /// Rehearse setting <paramref name="property"/> on <paramref name="export"/>.
    ///
    /// Returns true when the build will be able to make this change, false with a reason in
    /// <paramref name="why"/> when it will not.
    /// </summary>
    /// <summary>
    /// The same rehearsal for a change that is not a number.
    ///
    /// The BUILD'S OWN WRITE, run for real on the game's bytes and thrown away, which is the rule
    /// <see cref="Try"/> was written to: a check of our own devising drifts from the writer, and
    /// the only check that cannot is the writer.
    ///
    /// An unknown kind is REFUSED BY NAME rather than ignored. A project carrying a kind this
    /// build does not implement is a project written by a newer Studio, and saying so is the
    /// difference between "that will not work" and a mod that installs and does nothing.
    /// </summary>
    public static bool TryValue(IFileProvider provider, string packageName, string export,
                                string property, string kind, string now, out string? why)
        => TryValue(provider, packageName, export, property, kind, "", now, out why);

    /// <summary>The same, for a kind whose subject is what was THERE rather than what replaces it.</summary>
    public static bool TryValue(IFileProvider provider, string packageName, string export,
                                string property, string kind, string was, string now, out string? why)
    {
        why = null;

        byte[]? raw = null;
        try
        {
            var got = AssetLoader.Read(
                () => provider.TrySaveAsset(ModBuild.ContainerPathFor(packageName), out raw));
            if (!got || raw is null) { why = "the game's copy of that package could not be read"; return false; }
        }
        catch (Exception e) { why = e.Message; return false; }

        switch (kind)
        {
            case "name":
                PropertyAdd.SetName(provider, raw, packageName, export, property, now, out why);
                return why is null;

            case "string":
                PropertyAdd.SetString(provider, raw, packageName, export, property, now, out why);
                return why is null;

            // An FText is the only kind whose SHAPE changes: a lookup becomes a literal, so the
            // bytes that come out share nothing with the bytes that went in. See SetText.
            case "text":
                PropertyAdd.SetText(provider, raw, packageName, export, property, now, out why);
                return why is null;

            // A constant inside a Blueprint function's code: `export` is the function, `property`
            // the constant's type, and it must appear exactly once.
            case "scriptconst":
                ScriptPatch.SetConstant(provider, raw, packageName, export, property, was, now, out why);
                return why is null;

            // A map entry copied under a new key: `was` is the entry, `now` the key it gains.
            case "mapadd":
                PropertyAdd.AddMapEntry(provider, raw, packageName, export, property, was, now, out why);
                return why is null;

            // An element of a STRUCT array, named by index. `now` carries the index because that
            // is the whole of what the change says: which element, and whether it is being
            // copied or taken out.
            case "structdup" or "structdrop":
            {
                if (!int.TryParse(kind == "structdup" ? now : was, out var at))
                { why = $"'{(kind == "structdup" ? now : was)}' is not an element index"; return false; }
                PropertyAdd.SetStructEntry(provider, raw, packageName, export, property, at,
                                           kind == "structdup", out why);
                return why is null;
            }

            // The VALUE is the asset, as `/Game/A/B.B`. An add carries it in `now` and a removal
            // in `was`, which is the same record either way round.
            case "softadd" or "softremove":
            {
                var want = kind == "softadd" ? now : was;
                var (refPackage, refAsset) = SplitRef(want);
                if (refPackage is null)
                { why = $"'{want}' is not an asset path like /Game/Folder/Thing.Thing"; return false; }
                PropertyAdd.SetSoftRefEntry(provider, raw, packageName, export, property,
                                            refPackage, refAsset!, kind == "softadd", out why);
                return why is null;
            }

            default:
                why = $"this build does not know how to write a '{kind}' value";
                return false;
        }
    }

    /// <summary>
    /// An asset path split into the two FNames a soft reference holds.
    ///
    /// `/Game/A/B.B` is the package and then the object, and BOTH are kept. The object name is not
    /// the package's leaf and must not be re-derived from it: a soft CLASS reference is written
    /// `/Game/A/GA_Thing.GA_Thing_C`, and deriving the leaf turns that into `GA_Thing`, which names
    /// the Blueprint ASSET rather than its generated class. The game asks for the class, so the
    /// entry resolves to nothing and the ability silently does not appear.
    ///
    /// THAT IS EXACTLY WHAT THIS DID until it was measured: every shipped entry in a spec's
    /// Abilities array ends `_C`, and every entry this wrote did not. Read correctly -- the dot
    /// position was found -- and then dropped a line later.
    ///
    /// A path with no object name is taken as the package and its own leaf, which is right for the
    /// top-level asset case that has no dot to read.
    /// </summary>
    public static (string? Package, string? Asset) SplitRef(string path)
    {
        var clean = path.Trim();
        if (clean.Length == 0 || clean.Count(c => c == '/') < 2) return (null, null);

        var slash = clean.LastIndexOf('/');
        var dot = clean.LastIndexOf('.');

        // A dot AFTER the last slash separates the object; one before it is part of a folder name.
        if (dot > slash) return (clean[..dot], clean[(dot + 1)..]);
        return (clean, clean[(slash + 1)..]);
    }

    public static bool Try(IFileProvider provider, string packageName, string export,
                           string property, double was, double now, out string? why)
    {
        why = null;

        byte[]? raw = null;
        try
        {
            // UNDER THE GATE. TrySaveAsset decompresses out of the container, which is the same
            // reader state a package load walks — and the export race has taken this exact shape
            // every time it has been found: not a missing lock, a SECOND path to the same unsafe
            // thing written without one. See AssetLoader.Gate.
            var got = AssetLoader.Read(
                () => provider.TrySaveAsset(ModBuild.ContainerPathFor(packageName), out raw));
            if (!got || raw is null) { why = "the game's copy of that package could not be read"; return false; }
        }
        catch (Exception e) { why = e.Message; return false; }

        // The real edit, on the real bytes, verified by ScalarEdit's own read-back walk. The
        // written array is discarded: the BUILD writes it for keeps, from the project, so that
        // one path stays the only one that produces an installed asset.
        ScalarEdit.SetByName(provider, raw, packageName, export, property, was, now,
                             out _, out _, out why);
        return why is null;
    }

    /// <summary>
    /// The package a file in the index belongs to, as an override names it.
    ///
    /// The inverse of <see cref="ModBuild.ContainerPathFor"/>, and written beside nothing else so
    /// the pair can be read together. SWZeroCompany/Content/Game/X.uasset becomes /Game/Game/X —
    /// which is the form <see cref="ModOverride.Target"/> takes, and the form every property edit
    /// has to be addressed in.
    /// </summary>
    public static string? PackageForFile(string filePath)
    {
        var p = filePath.Replace('\\', '/').TrimStart('/');

        var marker = $"{ModBuild.ProjectName}/Content/";
        if (!p.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = p[marker.Length..];
        var dot = rest.LastIndexOf('.');
        if (dot > rest.LastIndexOf('/')) rest = rest[..dot];

        return "/Game/" + rest;
    }
}

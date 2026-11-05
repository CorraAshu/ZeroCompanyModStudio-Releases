namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;

/// <summary>
/// Everything one copied asset POINTS AT, so the editor can show it instead of asking somebody to
/// type a path they have no way of knowing.
///
/// A copy's only control used to be a blank from/to box. That is the right escape hatch and the
/// wrong front door: it works perfectly once you already know the exact string, and it is useless
/// otherwise -- which for a weapon's BP_ asset means the sound, the animations and the effects
/// were all reachable and none of them were visible. The author's words: "you should really open
/// that BP up and let us see everything that's in it that can be edited".
///
/// So this reads the asset and returns every reference in it. The values are already there; the
/// only thing that was missing was showing them.
///
/// A REFERENCE IS TWO NAMES, and that is why <see cref="Reference.Leaf"/> exists. A soft object
/// path is stored as the package path AND the asset name, separately, so re-pointing one means
/// writing both -- and a rename that changes only the path leaves a reference that resolves to
/// nothing while looking edited.
/// </summary>
public static class CopyContents
{
    /// <param name="Export">Which export inside the package holds it.</param>
    /// <param name="Property">The property's name, as the game calls it.</param>
    /// <param name="Path">The full object path it points at.</param>
    /// <param name="Kind">What sort of thing that is, for grouping. See <see cref="KindOf"/>.</param>
    /// <param name="Hard">
    /// Whether this is a HARD import rather than a soft object path -- which decides WHICH MAP
    /// re-points it, and is the difference between an edit that works and one that silently does
    /// nothing.
    ///
    /// A soft path is stored as two FNames, so renaming names moves it. A hard import is a package
    /// id in the container plus a stored export hash, and no rename can reach either -- it needs
    /// repointImports. Until this flag existed the editor wrote a rename for both, so a weapon's
    /// firing sound could be re-pointed in the panel, reported as changed, built, installed, and
    /// still play the original. The build said "'WPN_DC15_Shot_CloseRange' is not in the name map,
    /// so it is left alone" and nobody was reading the build log.
    /// </param>
    public sealed record Reference(string Export, string Property, string Path, string Kind,
                                   bool Hard = false)
    {
        /// <summary>The asset name half: "/A/B/C.C" -> "C". The other half a rename has to cover.</summary>
        public string Leaf
        {
            get
            {
                var tail = Path[(Path.LastIndexOf('/') + 1)..];
                var dot = tail.IndexOf('.');
                return dot >= 0 ? tail[(dot + 1)..] : tail;
            }
        }

        /// <summary>The package half: "/A/B/C.C" -> "/A/B/C".</summary>
        public string Package
        {
            get
            {
                var dot = Path.LastIndexOf('.');
                var slash = Path.LastIndexOf('/');
                return dot > slash ? Path[..dot] : Path;
            }
        }
    }

    /// <summary>
    /// What a reference IS, from its path. Grouping only -- nothing keys off it.
    ///
    /// Read from the path rather than the property name because the property names are the game's
    /// and are not consistent: a weapon's firing sound is "AkEvent", its ambient is
    /// "AkEventAmbientCombat", and a foley switch is "AkWeaponTypeSwitch". The folder they all
    /// live in is the same.
    /// </summary>
    public static string KindOf(string path)
    {
        if (path.Contains("/WwiseAudio/", StringComparison.OrdinalIgnoreCase)) return "Sound";
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        if (leaf.StartsWith("SK_", StringComparison.Ordinal) ||
            leaf.StartsWith("SM_", StringComparison.Ordinal)) return "Mesh";
        if (leaf.StartsWith("T_", StringComparison.Ordinal)) return "Texture";
        if (leaf.StartsWith("MI_", StringComparison.Ordinal) ||
            leaf.StartsWith("M_", StringComparison.Ordinal)) return "Material";
        if (leaf.StartsWith("GA_", StringComparison.Ordinal) ||
            leaf.StartsWith("GE_", StringComparison.Ordinal)) return "Ability";
        if (leaf.StartsWith("ABP_", StringComparison.Ordinal) ||
            leaf.StartsWith("AS_", StringComparison.Ordinal) ||
            path.Contains("/Animation", StringComparison.OrdinalIgnoreCase)) return "Animation";
        if (leaf.StartsWith("CPD_", StringComparison.Ordinal) ||
            leaf.StartsWith("CD_", StringComparison.Ordinal) ||
            leaf.StartsWith("BP_", StringComparison.Ordinal) ||
            leaf.StartsWith("GK_", StringComparison.Ordinal)) return "Part";
        return "Other";
    }

    /// <summary>
    /// Every reference in a package, in the order they are found, without duplicates.
    ///
    /// Returns an empty list rather than throwing: this feeds a panel that draws every frame, and
    /// an asset that will not read is a thing to say quietly, not to take the window down for.
    /// </summary>
    public static List<Reference> Read(IFileProvider provider, string packagePath, out string? why)
    {
        why = null;
        var found = new List<Reference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<UObject> exports;
        try
        {
            exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(packagePath)).GetExports().ToList());
        }
        catch (Exception e) { why = e.Message; return found; }

        foreach (var export in exports)
        {
            var name = export.Name;
            foreach (var prop in export.Properties)
                Walk(name, prop.Name.Text, prop.Tag?.GenericValue, 0);
        }
        return found;

        void Add(string export, string property, string? path, bool hard = false)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path[0] != '/') return;
            // An asset can name the same thing from two properties; the editor wants one row.
            if (!seen.Add(export + "|" + path)) return;
            found.Add(new Reference(export, property, path, KindOf(path), hard));
        }

        // The same shapes --props walks. A property's value is never the struct itself: it is an
        // FScriptStruct around it, or an FInstancedStruct around that, or a script array whose
        // own Properties are the elements. Stopping at any one of them finds nothing.
        void Walk(string export, string property, object? value, int depth)
        {
            if (depth > 12 || value is null) return;
            switch (value)
            {
                case FStructFallback sf:
                    foreach (var q in sf.Properties) Walk(export, q.Name.Text, q.Tag?.GenericValue, depth + 1);
                    return;
                case FScriptStruct fss: Walk(export, property, fss.StructType, depth + 1); return;
                case FInstancedStruct fis: Walk(export, property, fis.ScriptStruct?.StructType, depth + 1); return;
                case UScriptArray arr:
                    foreach (var item in arr.Properties) Walk(export, property, item?.GenericValue, depth + 1);
                    return;
                case UScriptMap map:
                    foreach (var kv in map.Properties)
                    {
                        Walk(export, property, kv.Key?.GenericValue, depth + 1);
                        Walk(export, property, kv.Value?.GenericValue, depth + 1);
                    }
                    return;
                case FSoftObjectPath sop: Add(export, property, sop.AssetPathName.Text); return;
                case FPackageIndex pi:
                    // A hard import. ResolvedObject gives the path an import actually names, which
                    // is what a re-point has to match -- and it is flagged HARD, because renaming
                    // names cannot move it.
                    try { Add(export, property, pi.ResolvedObject?.GetPathName(), hard: true); } catch { }
                    return;
                case string s when s.StartsWith('/'): Add(export, property, s); return;
            }
        }
    }
}

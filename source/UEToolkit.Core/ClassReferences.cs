namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Assets.Exports;

/// <summary>
/// WHICH ASSETS A CLASS-VALUED PROPERTY MAY BE POINTED AT, and what it points at today.
///
/// <see cref="PropertyAdd.AddClass"/> can give an ability a `CooldownGameplayEffectClass` naming
/// any package at all, and most of them would be wrong in a way nothing reports: a ClassProperty
/// holding the wrong kind of class is a reference the game resolves and then does nothing with.
/// The author's request was for "an asset picker", and a picker that offers 35,000 assets is a
/// text box with extra steps.
///
/// **THE MAPPINGS CANNOT ANSWER THIS.** A usmap records the serialisation shape and not the C++
/// type parameter, so `CooldownGameplayEffectClass` reads as `ObjectProperty` and nothing more --
/// checked against the shipped 196985 usmap before going looking elsewhere.
///
/// The ASSET REGISTRY can, and it is already mounted. Every Blueprint carries `NativeParentClass`
/// and `ParentClass` tags, so the question "what kind of thing is this" has a recorded answer:
///
///     GE_Cooldown_RocketStrike   NativeParentClass  BitReactorCooldownGameplayEffect
///                                ParentClass        GE_Cooldown_Base_C
///     GA_RocketStrike_T3         NativeParentClass  BRPresentableGameplayAbility
///                                ParentClass        GA_RocketStrike_T1_C
///
/// So the candidate list is derived from the GAME'S OWN ANSWER rather than from a rule written
/// here. Given an asset and a property:
///
///   1. the registry says which class the asset's Blueprint DERIVES FROM,
///   2. that parent is read, and the value it holds for the property is what this asset inherits,
///   3. the registry says what KIND that value is,
///   4. and every asset of that kind is a candidate.
///
/// Step 2 is worth having on its own. `GA_RocketStrike_T3` serialises no cooldown reference at
/// all -- all six tiers inherit one from `GA_RocketStrike_T1` -- so "what does this currently
/// use?" had no answer anywhere in the tool, and it is the first thing an author needs to know
/// before changing it.
/// </summary>
public static class ClassReferences
{
    /// <summary>What a class-valued property holds now, and what may go in it.</summary>
    /// <param name="Inherited">
    /// The package the property resolves to today, or "" when nothing in the chain sets it. A
    /// value INHERITED from a parent Blueprint counts: it is what the game uses.
    /// </param>
    /// <param name="InheritedFrom">The asset that actually carries it, when it is not this one.</param>
    /// <param name="Kind">
    /// The `NativeParentClass` every candidate must share, derived from what is inherited. Empty
    /// when nothing is inherited, and an empty Kind means the picker cannot narrow -- the caller
    /// offers a typed path and says why rather than offering everything.
    /// </param>
    public sealed record Slot(string Property, string Inherited, string InheritedFrom, string Kind);

    /// <summary>One asset a slot may be pointed at.</summary>
    public sealed record Candidate(string Name, string Package);

    /// <summary>
    /// The registry, read once per provider.
    ///
    /// Every lookup here is by package name, so it is turned into a dictionary once rather than
    /// walked per question -- the picker asks about one property and then about several thousand
    /// candidates, and a linear scan for each is the difference between instant and not.
    /// </summary>
    private sealed record Catalogue(
        Dictionary<string, (string Native, string Parent, string Asset)> ByPackage,
        List<(string Asset, string Package, string Native)> All);

    private static readonly object Gate = new();
    private static IFileProvider? _for;
    private static Catalogue? _read;

    private static Catalogue Read(IFileProvider provider)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_for, provider) && _read is { } had) return had;

            var byPackage = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
            var all = new List<(string, string, string)>();
            try
            {
                var path = PartsIndex.FindRegistryPath(provider);
                if (path is not null && provider.TrySaveAsset(path, out var bytes))
                {
                    var state = new FAssetRegistryState(new FByteArchive(path, bytes));
                    foreach (var d in state.PreallocatedAssetDataBuffers)
                    {
                        if (d.TagsAndValues is null) continue;
                        var native = Tag(d.TagsAndValues, "NativeParentClass");
                        var parent = Tag(d.TagsAndValues, "ParentClass");
                        if (native.Length == 0 && parent.Length == 0) continue;

                        var package = d.PackageName.Text;
                        var asset = d.AssetName.Text;
                        // The `_C` row and the asset row describe one thing. The asset row is the
                        // one worth keeping: it is what a package path resolves to, and the two
                        // carry the same parent tags anyway.
                        if (asset.EndsWith("_C", StringComparison.Ordinal)) continue;

                        byPackage[package] = (native, parent, asset);
                        all.Add((asset, package, native));
                    }
                }
            }
            catch (Exception) { /* no registry is "cannot narrow", which the caller handles */ }

            var made = new Catalogue(byPackage, all);
            _for = provider;
            _read = made;
            return made;
        }
    }

    /// <summary>
    /// A registry tag's value with UE's decoration taken off.
    ///
    /// They are stored as `/Script/CoreUObject.Class'/Script/BitReactorGame.BitReactorCooldownGameplayEffect'`
    /// and as `BlueprintGeneratedClass'/Game/.../GE_Cooldown_Base.GE_Cooldown_Base_C'`. What is
    /// wanted is the part inside the quotes; comparing the whole strings would work too and would
    /// break the first time one of them is written a different way.
    /// </summary>
    private static string Tag(IDictionary<CUE4Parse.UE4.Objects.UObject.FName, string> tags,
                              string name)
    {
        foreach (var (k, v) in tags)
        {
            if (!string.Equals(k.Text, name, StringComparison.Ordinal)) continue;
            if (v is not { Length: > 0 }) return "";
            var open = v.IndexOf('\'');
            var close = v.LastIndexOf('\'');
            return open >= 0 && close > open ? v[(open + 1)..close] : v;
        }
        return "";
    }

    /// <summary>An object path's package half: "/A/B/C.C_C" -> "/A/B/C".</summary>
    private static string PackageOf(string objectPath)
    {
        if (objectPath.Length == 0) return "";
        var dot = objectPath.IndexOf('.');
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    /// <summary>
    /// What <paramref name="property"/> on <paramref name="package"/> holds today and may hold.
    ///
    /// Walks UP the Blueprint chain rather than assuming one level: the tiers happen to inherit
    /// from T1 directly, and nothing says the next family will. Bounded at eight, which is deeper
    /// than any chain in this game and stops a cycle in the registry from hanging the editor.
    /// </summary>
    public static Slot Describe(IFileProvider provider, string package, string exportName,
                                string property)
    {
        var reg = Read(provider);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var at = package;

        for (var up = 0; up < 8 && at.Length > 0 && seen.Add(at); up++)
        {
            var value = ValueIn(provider, at, up == 0 ? exportName : "", property);
            if (value.Length > 0)
            {
                var target = PackageOf(value);
                var kind = reg.ByPackage.TryGetValue(target, out var row) ? row.Native : "";
                return new Slot(property, target, up == 0 ? "" : at, kind);
            }
            at = reg.ByPackage.TryGetValue(at, out var here) ? PackageOf(here.Parent) : "";
        }
        return new Slot(property, "", "", "");
    }

    /// <summary>
    /// The value one package's CDO serialises for a property, or "".
    ///
    /// Read through the provider rather than through a plan: this is a question about what the
    /// GAME ships, so the mounted asset is the answer and there is nothing to splice.
    /// </summary>
    private static string ValueIn(IFileProvider provider, string package, string exportName,
                                  string property)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(package)).GetExports().ToArray());
            foreach (var e in exports)
            {
                if (e is null) continue;
                if (exportName.Length > 0 &&
                    !string.Equals(e.Name, exportName, StringComparison.OrdinalIgnoreCase)) continue;
                // A CDO is where a class default lives, and it is the only export worth reading
                // when the caller named none.
                if (exportName.Length == 0 &&
                    !e.Name.StartsWith("Default__", StringComparison.Ordinal)) continue;

                var prop = e.Properties.FirstOrDefault(x => x.Name.Text == property);
                if (prop?.Tag?.GenericValue?.ToString() is { Length: > 0 } text)
                {
                    var open = text.IndexOf('\'');
                    var close = text.LastIndexOf('\'');
                    return open >= 0 && close > open ? text[(open + 1)..close] : "";
                }
            }
        }
        catch (Exception) { /* an unreadable parent is "cannot say", not an error */ }
        return "";
    }

    /// <summary>What a tag container holds now, and which asset in the chain actually carries it.</summary>
    /// <param name="Tags">
    /// The tags the property resolves to today, or empty when nothing in the chain sets it.
    /// A container INHERITED from a parent Blueprint counts: it is what the game reads.
    /// </param>
    /// <param name="InheritedFrom">The asset that actually carries it, when it is not this one.</param>
    public sealed record TagSlot(string Property, List<string> Tags, string InheritedFrom);

    /// <summary>
    /// What <paramref name="property"/> on <paramref name="package"/> holds today, for a CONTAINER.
    ///
    /// <see cref="Describe"/>'s sibling, and separate for the reason the two differ: a class-valued
    /// slot is narrowed by the registry's NativeParentClass and a tag container is not narrowed by
    /// anything -- a tag is a tag. What is worth knowing is only the first half, "what does this
    /// use today", and for an inherited container that is written nowhere in the asset using it.
    ///
    /// Same walk UP the Blueprint chain, same bound of eight. An author about to add
    /// `WeaponTypeTags` to a tier needs to see the three the base tier already has, or the add
    /// silently replaces them for that tier alone.
    /// </summary>
    /// <remarks>
    /// ALL the properties in one walk, not one walk each. An ability declares half a dozen
    /// containers and the chain is up to eight packages deep, so a per-property version loads the
    /// same parent forty-odd times -- and the whole-game abilities sweep reads 1,779 assets. The
    /// answer for every property is in the same export, so it is read once and asked six
    /// questions.
    /// </remarks>
    public static List<TagSlot> DescribeTags(IFileProvider provider, string package,
                                             string exportName, IReadOnlyList<string> properties)
    {
        var found = new Dictionary<string, TagSlot>(StringComparer.Ordinal);
        if (properties.Count == 0) return [];

        var reg = Read(provider);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var at = package;

        for (var up = 0; up < 8 && at.Length > 0 && seen.Add(at) && found.Count < properties.Count; up++)
        {
            var exports = ExportsFor(provider, at, up == 0 ? exportName : "");
            if (exports.Length > 0)
                foreach (var property in properties)
                {
                    if (found.ContainsKey(property)) continue;
                    var tags = PartDetails.TagsIn(exports, property);
                    if (tags.Count > 0) found[property] = new TagSlot(property, tags, up == 0 ? "" : at);
                }
            at = reg.ByPackage.TryGetValue(at, out var here) ? PackageOf(here.Parent) : "";
        }

        // Every property asked about comes back, set or not: "nothing in the chain carries this"
        // is an answer an author needs, and it is the one that means an add cannot replace
        // anything by surprise.
        return properties.Select(x => found.TryGetValue(x, out var had) ? had
                                                                       : new TagSlot(x, [], ""))
                         .ToList();
    }

    /// <summary>The exports a chain step's answer can come from: the named one, else the CDO.</summary>
    private static UObject[] ExportsFor(IFileProvider provider, string package, string exportName)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(package)).GetExports().ToArray());
            return exports.Where(e =>
                e is not null &&
                (exportName.Length > 0
                    ? string.Equals(e.Name, exportName, StringComparison.OrdinalIgnoreCase)
                    : e.Name.StartsWith("Default__", StringComparison.Ordinal))).ToArray();
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// Every asset of one kind, for a picker. Sorted by name, because a picker is read.
    ///
    /// Empty <paramref name="kind"/> returns nothing rather than everything: "we could not work
    /// out what belongs here" and "anything belongs here" are different answers, and offering
    /// thirty-five thousand assets is how a picker becomes a way to break a mod.
    /// </summary>
    public static List<Candidate> OfKind(IFileProvider provider, string kind)
    {
        if (kind.Length == 0) return [];
        return Read(provider).All
            .Where(x => string.Equals(x.Native, kind, StringComparison.OrdinalIgnoreCase))
            .Select(x => new Candidate(x.Asset, x.Package))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The short name of a native class path, for showing: "...BitReactorCooldownGameplayEffect".</summary>
    public static string KindName(string kind) =>
        kind.Length == 0 ? "" : kind[(kind.LastIndexOf('.') + 1)..];
}

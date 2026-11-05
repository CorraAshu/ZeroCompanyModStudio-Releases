using CUE4Parse.FileProvider;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Readers;

namespace UEToolkit.Core;

/// <summary>
/// Every customization part the game ships, read from its ASSET REGISTRY rather than by opening
/// packages.
///
/// This is the same source the game itself filters on: each part carries a registry tag named
/// `CustomizationPartTags` holding its slot, its rig, the character it is tied to and whatever
/// gates it, so the menu can be built without loading a single asset. Reading it costs a couple
/// of seconds for ~3,500 parts; opening those packages one at a time costs twenty minutes and
/// answers the same question.
///
/// What it CANNOT tell you: mesh paths and display names are not registry tags. For those, open
/// the individual part.
/// </summary>
public static class PartsIndex
{
    public sealed record Part(
        string Path,
        string Name,
        string Class,
        IReadOnlyList<string> Slots,
        IReadOnlyList<string> Rigs,
        IReadOnlyList<string> Gates,
        IReadOnlyList<string> Characters,
        IReadOnlyList<string> Tags,
        /// <summary>
        /// Species this part is restricted to -- Clone, Droid and so on. Empty for nearly
        /// everything. A part with one is offered ONLY to characters of that species, and until
        /// this existed nothing in the tool knew such a restriction was there. Optional so every
        /// existing caller keeps compiling.
        /// </summary>
        IReadOnlyList<string>? Species = null)
    {
        /// <summary>The species lock as a sentence fragment, or "" when there is none.</summary>
        public string SpeciesLock => Species is { Count: > 0 } ? string.Join(", ", Species) : "";

        public string Slot => Slots.Count > 0 ? string.Join(",", Slots) : "(none)";
        public string Character => string.Join(",", Characters);

        /// <summary>
        /// Why you cannot wear it, in one word. Order matters: a piece carrying both an edition
        /// unlock and AuthoredOnly is reported by the stronger gate, because that is the one that
        /// decides whether it can appear at all.
        /// </summary>
        public string Gate
        {
            get
            {
                if (Gates.Any(g => g.StartsWith("Unlocks.Deluxe", StringComparison.Ordinal) ||
                                   g.StartsWith("Unlocks.Preorder", StringComparison.Ordinal)))
                    return "edition";
                if (Gates.Any(g => g.StartsWith("Unlocks.", StringComparison.Ordinal)))
                    return "story";
                if (Gates.Contains("AuthoredOnly")) return "dev";
                return "open";
            }
        }

        /// <summary>Outfit family from the path, e.g. CaptainRex, Clo, Coi. "-" when it has none.</summary>
        public string Family
        {
            get
            {
                const string marker = "/Outfit/";
                var i = Path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "-";
                var rest = Path[(i + marker.Length)..];
                var j = rest.IndexOf('/');
                return j < 0 ? rest : rest[..j];
            }
        }
    }

    private const string SlotPrefix = "br.Customization.Slot.";
    private const string RigPrefix  = "br.Customization.Part.Character.Rig.";
    /// <summary>
    /// The tag that ties a part to one character, as a prefix. Public because the BUILDER writes
    /// these as well as reading them -- restricting a part to a character is adding one of these,
    /// and freeing a copied one is taking it away.
    /// </summary>
    public const string NamePrefix = "br.Customization.Part.Character.Info.Name.";

    /// <summary>
    /// A SPECIES LOCK, which is a restriction and was being read as nothing at all.
    ///
    /// Describe sorted tags into slots, rigs, gates and characters, and a
    /// `Part.Character.Species.*` tag matched none of them -- so a part restricted to clones
    /// reported an open gate and no character restriction, built clean, installed, and never
    /// appeared for anybody else, with nothing anywhere saying why. Found when the Clone Commando
    /// armour did not show in the menu: it carries Species.Clone and the per-region Clo010 parts,
    /// which work, carry no species tag at all.
    /// </summary>
    public const string SpeciesPrefix = "br.Customization.Part.Character.Species.";
    private const string AccPrefix  = "br.Customization.Accepts.";

    /// <summary>The registry lives at the project root of the mounted game, e.g.
    /// SWZeroCompany/AssetRegistry.bin. Found rather than hard-coded so this works per game.</summary>
    public static string? FindRegistryPath(IFileProvider provider) =>
        provider.Files.Keys
            .Where(k => k.EndsWith("/AssetRegistry.bin", StringComparison.OrdinalIgnoreCase))
            // Shallowest wins: DLC and plugin folders ship their own, and the project root one
            // is the index the game actually loads at startup.
            .OrderBy(k => k.Count(c => c == '/'))
            .FirstOrDefault();

    /// <summary>
    /// Read every part. <paramref name="primaryAssetType"/> is what the Asset Manager rule in the
    /// game's DefaultGame.ini registers them under; matching on it rather than on the class name
    /// is deliberate, because most outfits are Blueprint subclasses (BP_CPD_H_Outfit_Slg_Base_C
    /// and friends) and a class-name match would silently miss them.
    /// </summary>
    public static List<Part> Read(IFileProvider provider,
                                  string primaryAssetType = "CustomizationPartDefinition")
    {
        var path = FindRegistryPath(provider);
        if (path is null) return new List<Part>();
        if (!provider.TrySaveAsset(path, out var bytes)) return new List<Part>();

        var state = new FAssetRegistryState(new FByteArchive(path, bytes));
        var parts = new List<Part>();

        foreach (var data in state.PreallocatedAssetDataBuffers)
        {
            var tagsAndValues = data.TagsAndValues;
            if (tagsAndValues is null) continue;
            if (!TryGet(tagsAndValues, "PrimaryAssetType", out var type) || type != primaryAssetType)
                continue;

            TryGet(tagsAndValues, "CustomizationPartTags", out var exported);
            TryGet(tagsAndValues, "NativeClass", out var cls);
            parts.Add(Describe(data.PackageName.Text, data.AssetName.Text, cls, exported));
        }

        parts.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return parts;
    }

    /// <summary>
    /// One part, from the two registry tags that describe it.
    ///
    /// Split out of <see cref="Read"/> so a part this tool has just WRITTEN can be described by
    /// the same code that describes the game's. That matters more than the deduplication: a mod
    /// whose parts are gated is invisible in exactly the way a mod with no parts is, and the only
    /// way to tell before shipping it is to run the built entry through this.
    /// </summary>
    public static Part Describe(string path, string name, string? nativeClass, string? exportedTags)
    {
        var tags = ParseTagContainer(exportedTags);

        var slots = new List<string>();
        var rigs = new List<string>();
        var gates = new List<string>();
        var chars = new List<string>();
        var species = new List<string>();
        foreach (var t in tags)
        {
            if (t.StartsWith(SlotPrefix, StringComparison.Ordinal)) slots.Add(t[SlotPrefix.Length..]);
            else if (t.StartsWith(RigPrefix, StringComparison.Ordinal)) rigs.Add(t[RigPrefix.Length..]);
            else if (t.StartsWith(NamePrefix, StringComparison.Ordinal)) chars.Add(t[NamePrefix.Length..]);
            // BEFORE the Accepts test is irrelevant -- these are different prefixes -- but AFTER
            // Name matters not at all either. What matters is that it is tested: it was not.
            else if (t.StartsWith(SpeciesPrefix, StringComparison.Ordinal)) species.Add(t[SpeciesPrefix.Length..]);
            else if (t.StartsWith(AccPrefix, StringComparison.Ordinal)) gates.Add(t[AccPrefix.Length..]);
        }

        return new Part(path, name, CleanClass(nativeClass), slots, rigs, gates, chars, tags, species);
    }

    private static bool TryGet(IDictionary<CUE4Parse.UE4.Objects.UObject.FName, string> map,
                               string key, out string value)
    {
        foreach (var kv in map)
        {
            if (string.Equals(kv.Key.Text, key, StringComparison.Ordinal))
            {
                value = kv.Value ?? "";
                return true;
            }
        }
        value = "";
        return false;
    }

    /// <summary>
    /// Pull tag names out of an exported FGameplayTagContainer:
    ///   (GameplayTags=((TagName="br.A.B"),(TagName="br.C.D")))
    /// The registry stores the EXPORT STRING, not a struct, so this parses text on purpose.
    /// </summary>
    private static List<string> ParseTagContainer(string? exported)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(exported)) return result;
        const string needle = "TagName=\"";
        var at = 0;
        while (true)
        {
            var start = exported.IndexOf(needle, at, StringComparison.Ordinal);
            if (start < 0) break;
            var valueStart = start + needle.Length;
            var end = exported.IndexOf('"', valueStart);
            if (end < 0) break;
            result.Add(exported[valueStart..end]);
            at = end + 1;
        }
        return result;
    }

    /// <summary>NativeClass arrives as Class'/Script/X.Y' or BlueprintGeneratedClass'/Game/....C'.</summary>
    private static string CleanClass(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = raw;
        var tick = s.LastIndexOf('\'');
        if (tick > 0) s = s[..tick];
        var dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[(dot + 1)..];
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        return s;
    }
}

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;

namespace UEToolkit.Core;

/// <summary>
/// The half of a customization part that the asset registry does NOT carry: its display name,
/// its menu icon, the meshes it puts on the character, and -- for weapons -- its stats.
///
/// <see cref="PartsIndex"/> answers "what parts exist and what gates them" for the whole game in
/// a couple of seconds, because the registry holds the tags. It cannot answer "what is this
/// called" or "what does it look like" -- those live in the part's own package, behind its
/// FRAGMENT list, and reading them means opening the package.
///
/// I expected that to cost minutes for ~3,500 packages and designed a lazy per-row reader around
/// the assumption. Measured, it is 0.20 ms each and 0.7s for all of them, so callers read the lot
/// up front and everything is searchable immediately. `--partdetail` prints the measurement.
///
/// The fragment layout, from the game's own usmap:
///   BitReactorAssetDefinitionBase  ->  Fragments : Array&lt;Object&gt;
///     BitReactorUIDataFragment     ->  DisplayName, ShortDescription, SmallImage, LargeImage
///     CustomizationFragmentMesh    ->  SkeletalMeshOptions[], bReplacesBaseMesh, OptionalAnimClass
///     CustomizationFragmentOverrideSlots -> HiddenSlots, LockedSlots, OverrideSlots
///     CustomizationFragmentAttributeSets -> AttributeSetsNew, the weapon stat tables
///     CustomizationFragmentAbilities     -> Abilities
/// </summary>
public static class PartDetails
{
    /// <summary>
    /// One <c>CustomizationFragmentMaterialSwap</c>: at run time the game replaces the material on
    /// the slots named in <see cref="Slots"/> with <see cref="Replacement"/>.
    ///
    /// The game's own HM00 head carries six of them, one per skin tone, each aimed at the slot
    /// <c>MI_Head</c>. They sit behind a variant selector keyed on the wearer's race tag, so a
    /// character that has a race gets its head's skin material replaced whatever the mesh imports.
    /// Read so a build can say so when an author has put a material of their own on that slot.
    /// </summary>
    public sealed record MaterialSwap(string Replacement, IReadOnlyList<string> Slots);

    /// <summary>
    /// One entry of a mesh fragment's palette: the body types it is for, and the mesh.
    ///
    /// <paramref name="Fragment"/> is WHICH mesh fragment of the part it came from, counted in
    /// export order. It used to be thrown away, and that is what made a garment dressing the
    /// whole character unreadable: the Clone Commando torso carries FIVE mesh fragments -- boots,
    /// torso, pack, legs, arms -- and flattening them gave one list of nine options with nothing
    /// saying that they were five different garments and not nine body-type variants of one.
    /// </summary>
    public sealed record MeshOption(string MeshPath, IReadOnlyList<string> RequiredTags,
                                    int Fragment = 0)
    {
        /// <summary>Body/height/weight variant in the short form the browser shows.</summary>
        public string Variant
        {
            get
            {
                var bits = new List<string>();
                foreach (var t in RequiredTags)
                {
                    var leaf = t[(t.LastIndexOf('.') + 1)..];
                    if (!bits.Contains(leaf)) bits.Add(leaf);
                }
                return bits.Count == 0 ? "-" : string.Join("/", bits);
            }
        }

        /// <summary>
        /// The body REGION this mesh dresses, read from the asset name.
        ///
        /// THE DATA DOES NOT SAY. A mesh fragment carries meshes and an anim class and nothing
        /// else -- no region field, no slot tag -- so the five fragments of a whole-body garment
        /// are told apart only by what their meshes are CALLED: SK_HAAM_Clo010_BOOT, _TORS,
        /// _LEGS, _ARMS, _PACK. Measured across the catalogue, that token is present and
        /// consistent, and it is not always last (SK_HAAF_FemHawksA_TORS_B), so it is looked for
        /// anywhere in the name rather than taken off the end.
        ///
        /// Empty when the name carries no token we know -- a LABEL problem, not a build one:
        /// <see cref="Fragment"/> is what anything structural keys off.
        /// </summary>
        public string Region => RegionTokenOf(MeshPath);
    }

    /// <summary>
    /// The region token an asset NAME carries, read the way <see cref="MeshOption.Region"/> reads a
    /// mesh's: the first underscore-separated word that is a known token, anywhere in the leaf.
    /// "" when there is none.
    ///
    /// Not only meshes carry one. The game names a tint mask <c>T_Clo001_BOOT_a_Tint</c> and the
    /// material slot it paints <c>MI_BOOT</c> (or <c>MI_BOOT_ADD</c>), so the same reading says which
    /// piece of a garment a tint fragment is for -- see <see cref="TintPieceOf"/>. One tokeniser, so
    /// a mesh, its mask and its slot cannot disagree about what piece they are.
    /// </summary>
    public static string RegionTokenOf(string pathOrName)
    {
        if (string.IsNullOrEmpty(pathOrName)) return "";
        var leaf = pathOrName[(pathOrName.LastIndexOf('/') + 1)..];
        var dot = leaf.IndexOf('.');
        if (dot >= 0) leaf = leaf[..dot];
        foreach (var token in leaf.Split('_'))
            foreach (var known in Regions)
                if (string.Equals(token, known, StringComparison.OrdinalIgnoreCase))
                    return known;
        return "";
    }

    /// <summary>
    /// One <c>CustomizationFragmentMaterialTexture</c>: the tint mask it paints through and the
    /// material slots it aims at. A garment that dresses several pieces carries one per piece --
    /// the Clone legs and boots have <c>T_Clo001_BOOT_a_Tint</c> on <c>MI_BOOT</c> and
    /// <c>T_Clo001_LEGS_a_Tint</c> on <c>MI_LEGS</c> -- and <see cref="Detail.TintMasks"/> keeps only
    /// the masks, which is the half of this that cannot say whose they are.
    /// </summary>
    public sealed record TintFragment(string Mask, IReadOnlyList<string> Slots);

    /// <summary>
    /// WHICH PIECE OF THE GARMENT A TEMPLATE'S TINT MASK IS FOR, as a canonical region token
    /// ("BOOT", "LEGS", ...), or "" when it cannot be told.
    ///
    /// The slot a fragment aims at is the better witness: that is the material the game really
    /// paints, and the mesh wearing it is the piece. The mask's own name is the fallback, for a slot
    /// that carries no token ("MI_Cloth"). A mask the template paints through on slots of SEVERAL
    /// pieces cannot be given to one of them, and answers "" rather than guessing -- a mask handed to
    /// the wrong piece is the fault this exists to stop.
    /// </summary>
    public static string TintPieceOf(Detail d, string mask)
    {
        var fragments = (d.TintFragments ?? [])
            .Where(f => string.Equals(f.Mask, mask, StringComparison.Ordinal)).ToList();

        var fromSlots = fragments.SelectMany(f => f.Slots)
            .Select(s => Canon(RegionTokenOf(s))).Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        if (fromSlots.Count == 1) return fromSlots[0];
        if (fromSlots.Count > 1) return "";

        return Canon(RegionTokenOf(mask));
    }

    /// <summary>
    /// The region tokens an asset name can carry. Longest-lived spellings first is not needed --
    /// the match is exact per token, so BOOT and BOOTS are separate entries rather than a prefix.
    /// </summary>
    private static readonly string[] Regions =
        ["BOOT", "BOOTS", "TORS", "TORSO", "LEGS", "ARMS", "PACK", "HELM", "HAIR", "HEAD", "FACE"];

    /// <summary>The region tokens a mod file may name, for the editor and the handbook.</summary>
    public static IReadOnlyList<string> RegionTokens => Regions;

    /// <summary>
    /// Fold the spellings that mean the same piece, so two templates spelling it differently key
    /// the same. Returns "" for anything not a region token.
    /// </summary>
    public static string Canon(string token) => token.ToUpperInvariant() switch
    {
        "BOOT" or "BOOTS" => "BOOT",
        "TORS" or "TORSO" => "TORS",
        "LEGS" => "LEGS",
        "ARMS" => "ARMS",
        "PACK" => "PACK",
        "HELM" => "HELM",
        "HAIR" => "HAIR",
        "HEAD" => "HEAD",
        "FACE" => "FACE",
        _ => "",
    };

    /// <summary>
    /// THE STABLE KEY PER MESH FRAGMENT -- the one thing a mod file may store, and the one thing
    /// the builder matches on.
    ///
    /// The canonical region token when any option in the group carries one; otherwise the
    /// ordinal-first option's asset leaf, which is at least a name and not a position. Never
    /// <see cref="RegionOf"/>'s "part N" fallback: that string IS the export index wearing a
    /// label's clothes, and export order is not comparable between templates -- Clo010's fragment
    /// suffixes run _0 _1 _14 _2 _3 and Hawks' run _1 _2 _14 _0.
    ///
    /// When a canonical token would be produced by more than one fragment of the same part, EVERY
    /// fragment carrying it drops to its leaf key instead. So the key set is collision-free without
    /// any tie-break that could depend on order.
    /// </summary>
    public static IReadOnlyList<(int Fragment, string Key)> RegionKeys(Detail d)
    {
        var groups = d.MeshFragments.ToList();
        var tokens = FragmentTokens(groups);
        var contested = ContestedOf(tokens);

        var keys = new List<(int, string)>();
        for (var i = 0; i < groups.Count; i++)
        {
            var token = tokens[i];
            var key = token.Length > 0 && !contested.Contains(token) ? token : Leaf(groups[i]);
            keys.Add((groups[i].Key, key));
        }
        return keys;

        static string Leaf(IGrouping<int, MeshOption> g)
        {
            var first = g.FirstOrDefault()?.MeshPath ?? "";
            if (first.Length == 0) return "";
            var leaf = first[(first.LastIndexOf('/') + 1)..];
            var dot = leaf.IndexOf('.');
            return dot >= 0 ? leaf[..dot] : leaf;
        }
    }

    /// <summary>The canonical region token of each mesh fragment, "" for one that carries none.</summary>
    private static List<string> FragmentTokens(List<IGrouping<int, MeshOption>> groups)
    {
        var tokens = new List<string>();
        foreach (var g in groups)
        {
            var token = "";
            foreach (var m in g)
                if (Canon(m.Region) is { Length: > 0 } c) { token = c; break; }
            tokens.Add(token);
        }
        return tokens;
    }

    private static HashSet<string> ContestedOf(List<string> tokens) =>
        tokens.Where(t => t.Length > 0)
              .GroupBy(t => t, StringComparer.Ordinal)
              .Where(x => x.Count() > 1)
              .Select(x => x.Key)
              .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The region tokens more than one mesh fragment of this part carries -- the ones
    /// <see cref="RegionKeys"/> gives up on and keys by mesh name instead. A piece named by such a
    /// token cannot be told from its twin, so anything matching by token (a tint mask to the piece
    /// it is for) must not assume the token is a key.
    /// </summary>
    public static IReadOnlySet<string> ContestedRegionTokens(Detail d) =>
        ContestedOf(FragmentTokens(d.MeshFragments.ToList()));

    /// <summary>
    /// Which fragment a stored key names, or null.
    ///
    /// Tolerant in one direction only -- exact key, then any option's asset leaf in the group, then
    /// the canonical token -- and it REFUSES an ambiguous key rather than resolving it by taking
    /// the first. A piece silently applied to a fragment it did not name is the failure this whole
    /// design exists to avoid.
    /// </summary>
    public static int? FragmentFor(Detail d, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var exact = RegionKeys(d).Where(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase))
                                 .Select(k => (int?) k.Fragment).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;

        var byLeaf = new List<int>();
        foreach (var g in d.MeshFragments)
            foreach (var m in g)
            {
                var leaf = m.MeshPath[(m.MeshPath.LastIndexOf('/') + 1)..];
                var dot = leaf.IndexOf('.');
                if (dot >= 0) leaf = leaf[..dot];
                if (string.Equals(leaf, key, StringComparison.OrdinalIgnoreCase)) { byLeaf.Add(g.Key); break; }
            }
        if (byLeaf.Count == 1) return byLeaf[0];
        if (byLeaf.Count > 1) return null;

        var canon = Canon(key);
        if (canon.Length == 0) return null;
        var byToken = d.MeshFragments
            .Where(g => g.Any(m => string.Equals(Canon(m.Region), canon, StringComparison.Ordinal)))
            .Select(g => g.Key).ToList();
        return byToken.Count == 1 ? byToken[0] : null;
    }

    /// <summary>
    /// One (piece, mesh) pair per fragment: what a part built from this template wears, before the
    /// author changes anything.
    ///
    /// <paramref name="preferBody"/> is a mesh path or body token whose HAAM/HAAF flavour picks
    /// between a fragment's body-type options; the ordinal-first option answers when nothing
    /// matches. Shared so that the staging button, <c>--modstage</c> and the probes cannot produce
    /// different pieces for the same part.
    /// </summary>
    public static IReadOnlyList<(string Region, string Mesh)> RegionMeshes(Detail d, string? preferBody)
    {
        var body = BodyToken(preferBody);
        var keys = RegionKeys(d);
        var made = new List<(string, string)>();
        foreach (var g in d.MeshFragments)
        {
            var key = keys.FirstOrDefault(k => k.Fragment == g.Key).Key ?? "";
            var pick = g.FirstOrDefault(m => BodyToken(m.MeshPath) is { Length: > 0 } t &&
                                             string.Equals(t, body, StringComparison.OrdinalIgnoreCase))
                       ?? g.FirstOrDefault();
            if (pick is not null && key.Length > 0) made.Add((key, pick.MeshPath));
        }
        return made;
    }

    /// <summary>
    /// Whether a fragment's mesh options are keyed by HEAD rather than by body type.
    ///
    /// Eyebrows and facial hair carry one option per head -- 29 and 25 of them on the two parts
    /// measured -- each cut to that head and skinned to its facial rig, and each is gated by a
    /// part tag that NAMES the head: `...Humanoid.Human.Head.Face.HF03`, `...Devaronian.Head.Face.D02`.
    /// A helmet or a torso is gated by `...Body.Height.Average` and `...Body.Type.Masculine`
    /// instead, and none of those mention a face.
    ///
    /// Read from the tags and not from the slot name on purpose: the tags are what the GAME keys the
    /// option by, so a slot the list does not know about yet, or a facial-hair part that is one piece
    /// of two, answers correctly without anyone remembering to add it here. EVERY option has to name
    /// a head -- one that does not is a body-type table that happens to mention a face.
    /// </summary>
    public static bool KeyedPerHead(IEnumerable<MeshOption> options)
    {
        var any = false;
        foreach (var o in options)
        {
            if (!o.RequiredTags.Any(NamesAHead)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// A tag like `...Human.Head.Face.HF03` -- a face tag whose last word is a HEAD, not a build.
    ///
    /// Two helmets (CPD_H_Outfit_Wor001A_HELM and CPD_Twi_Outfit_HELM_Base) key their two options
    /// `...Human.Head.Face.Feminine` and `...Head.Face.Masculine`. That is the body-type split
    /// spelled with a face tag, it is two options for two builds and not one per head, and treating
    /// it as per-head would leave a helmet's options alone. Measured across all 3,519 parts
    /// (`--modstage`): with these two words excluded no garment slot reads as keyed per head.
    /// </summary>
    private static bool NamesAHead(string tag)
    {
        const string face = ".Head.Face.";
        var at = tag.IndexOf(face, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        var leaf = tag[(at + face.Length)..];
        return !leaf.Equals("Feminine", StringComparison.OrdinalIgnoreCase) &&
               !leaf.Equals("Masculine", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The body-type token of an asset name -- HAAM, HAAF, HTAM and the rest -- or "".
    ///
    /// The kit prefix on every character mesh: SK_<b>HAAM</b>_Clo010_TORS. Read as a token rather
    /// than a prefix, because the same name also carries the kit and the region.
    /// </summary>
    public static string BodyToken(string? meshPathOrToken)
    {
        if (string.IsNullOrWhiteSpace(meshPathOrToken)) return "";
        var leaf = meshPathOrToken[(meshPathOrToken.LastIndexOf('/') + 1)..];
        var dot = leaf.IndexOf('.');
        if (dot >= 0) leaf = leaf[..dot];
        foreach (var token in leaf.Split('_'))
            if (token.Length == 4 &&
                (token.StartsWith("HA", StringComparison.OrdinalIgnoreCase) ||
                 token.StartsWith("HT", StringComparison.OrdinalIgnoreCase)))
                return token.ToUpperInvariant();
        return "";
    }

    /// <summary>
    /// A name for one fragment's group, for a person reading a table: its region when the asset
    /// names carry one, and its position when they do not.
    /// </summary>
    public static string RegionOf(IGrouping<int, MeshOption> fragment)
    {
        foreach (var m in fragment)
            if (m.Region.Length > 0) return m.Region;
        return $"part {fragment.Key + 1}";
    }

    public sealed record Detail(
        string Path,
        string? DisplayName,
        string? ShortDescription,
        string? IconPath,
        string? IconBankTag,
        IReadOnlyList<MeshOption> Meshes,
        string? AnimClassPath,
        IReadOnlyList<string> HiddenSlots,
        IReadOnlyList<string> OverrideSlots,
        bool ReplacesBaseMesh,
        IReadOnlyList<string> AttributeTables,
        IReadOnlyList<string> Abilities,
        string? Error,
        /// <summary>
        /// Whether the part carries a MATERIAL COLOUR fragment. It recolours whatever mesh the
        /// part points at, so a mod that swaps the mesh and keeps the fragment gets the template's
        /// palette painted over its own artwork -- Captain Rex's blue markings came out grey.
        /// </summary>
        /// <summary>
        /// The tint mask texture(s) the colour fragments paint through. A part that wants a
        /// different one has to name what it is redirecting FROM, and this is it.
        /// </summary>
        IReadOnlyList<string>? TintMasks = null,
        bool HasMaterialColor = false,
        /// <summary>
        /// Every fragment the part carries, by export type. A part is the sum of its fragments,
        /// and one authored by copying another inherits ALL of them -- so a builder that handles
        /// the fragments it happens to know about will keep shipping whatever it does not.
        /// </summary>
        IReadOnlyList<string>? Fragments = null,
        /// <summary>
        /// The MATERIAL SLOTS the tint fragments aim at, by name. A fragment does not paint a
        /// mesh, it paints the slots it names -- so a mesh whose other slots carry their own
        /// masks keeps them, which is how the game leaves a bodysuit untinted under a torso that
        /// takes the player's colour. Empty means the fragments name no slot, which the game
        /// reads as all of them.
        /// </summary>
        IReadOnlyList<string>? TintSlots = null,
        /// <summary>
        /// The paragraph a menu shows under the name. Carried because the BUILDER rewrites it and
        /// the DIFF has to know what it was -- otherwise the change the author asked for reads as
        /// one nobody asked for. Often begins with the whole of ShortDescription, which is why
        /// substitutions over these two have to be applied longest-first.
        /// </summary>
        string? LongDescription = null,
        /// <summary>
        /// Slots the part LOCKS in the menu, which is a different job from taking one over. The
        /// clone legs both take over `Boots.Mesh` and lock `Boots`: the first swaps the mesh, the
        /// second stops the player choosing another. A mod that undoes the takeover has to undo
        /// the lock in the same edit, or the boots slot is simply dead.
        /// </summary>
        IReadOnlyList<string>? LockedSlots = null,
        /// <summary>
        /// The part locks the slot IT IS IN, so once it is chosen the player cannot choose
        /// anything else there.
        /// </summary>
        /// <remarks>
        /// The OverrideSlots fragment has FIVE fields, not the three containers this read for a
        /// year: <c>bLockOwningSlot</c> and <c>bHideOwningSlot</c> sit after them and say the same
        /// two things about the part's OWN slot that LockedSlots and HiddenSlots say about other
        /// people's. Confirmed in the shipped usmap, in that order (2026-09-09).
        /// </remarks>
        bool LocksOwnSlot = false,
        /// <summary>The part hides the slot it is in, so the row disappears from the menu entirely.</summary>
        bool HidesOwnSlot = false,
        /// <summary>
        /// The material swaps the part carries, one per MaterialSwap fragment. Empty or null for
        /// the great majority of parts; the HM00 head has six. See <see cref="MaterialSwap"/>.
        /// </summary>
        IReadOnlyList<MaterialSwap>? MaterialSwaps = null,
        /// <summary>
        /// The tint fragments one by one -- mask AND the slots it paints -- in export order, where
        /// <see cref="TintMasks"/> is the distinct masks alone. A fragment that carries no mask is
        /// not here. See <see cref="TintPieceOf(Detail, string)"/>.
        /// </summary>
        IReadOnlyList<TintFragment>? TintFragments = null)
    {
        /// <summary>
        /// What to show as this part's name. Falls back to the asset name, because a great many
        /// parts have an EMPTY DisplayName -- every dev-only piece does, Captain Rex's armour
        /// included -- and a blank row is worse than a filename.
        /// </summary>
        public string Label(string assetName) =>
            string.IsNullOrWhiteSpace(DisplayName) ? assetName : DisplayName!;

        public bool HasName => !string.IsNullOrWhiteSpace(DisplayName);
        public string? MeshPath => Meshes.Count > 0 ? Meshes[0].MeshPath : null;

        /// <summary>
        /// The mesh options grouped the way the part carries them: one group per fragment, in
        /// export order. Each group is ONE GARMENT and its body-type variants.
        /// </summary>
        public IEnumerable<IGrouping<int, MeshOption>> MeshFragments =>
            Meshes.GroupBy(m => m.Fragment).OrderBy(g => g.Key);

        /// <summary>
        /// How many separate garments this part is.
        ///
        /// More than one means it dresses more than its own slot -- measured over the catalogue,
        /// 69 of 285 torsos and 66 of 157 legs do, against 3 of 165 helmets. A mod part carries
        /// ONE mesh, so a part shaped like this cannot be reproduced by duplicating it: whichever
        /// mesh is named is written into every region and the wearer gets five copies of it.
        /// </summary>
        public int MeshFragmentCount => Meshes.Select(m => m.Fragment).Distinct().Count();

        /// <summary>
        /// A part that grants abilities and carries stat tables is a weapon specialisation, not a
        /// piece of clothing. There is no flag saying so -- the class and the slot are the same as
        /// armour's -- so this is what it looks like from the data.
        /// </summary>
        public bool IsWeapon => AttributeTables.Count > 0 || Abilities.Count > 0;

        /// <summary>
        /// Does this part take over the head and hair, so the wearer's face is not drawn?
        ///
        /// Read from OverrideSlots, NOT HiddenSlots. They are different fields doing different
        /// jobs and the names invite exactly the wrong guess: HiddenSlots on an ordinary outfit
        /// piece lists the COLOUR slots it removes from the menu -- most torsos and legs have
        /// some -- while claiming the head is an OVERRIDE. Inferring from HiddenSlots reported
        /// 114 of 158 leg parts as head-hiding, which is how this was found.
        /// </summary>
        public bool HidesHead => OverrideSlots.Any(s =>
            s.Contains(".Hair.", StringComparison.Ordinal) ||
            s.Contains(".Head.", StringComparison.Ordinal));

        /// <summary>
        /// Does it hide the FACE -- which is the half of <see cref="HidesHead"/> that a helmet
        /// actually needs.
        ///
        /// HidesHead is true for hair OR head, and those are not the same promise. Measured on
        /// `CPD_H_Outfit_Clo001_HELM_212th`, a stock clone helmet:
        ///
        ///     overrides br.Customization.Slot.Character.Hair.Hair.Mesh
        ///     overrides br.Customization.Slot.Character.Hair.Facial.Mesh
        ///
        /// -- hair and stubble, and nothing else. HidesHead says true, so the editor's checkbox
        /// reads "hides the head", the template gate passes it, and the verifier reports Good,
        /// while in game the wearer's face renders straight through the visor. One conflation and
        /// four places believing it.
        ///
        /// So anything asking "will the face show through?" asks THIS, and HidesHead keeps its
        /// job of matching a part against a template of the same kind.
        /// </summary>
        public bool HidesFace => OverrideSlots.Any(s =>
            s.Contains(".Head.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Read one part. Never throws: a part that fails to parse comes back with
    /// <see cref="Detail.Error"/> set, because the browser draws thousands of rows and one bad
    /// package must not take the panel down with it.
    /// </summary>
    public static Detail Read(IFileProvider provider, string packagePath)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(packagePath)).GetExports().ToArray());
            return ReadExports(exports, packagePath);
        }
        catch (Exception ex)
        {
            return new Detail(packagePath, null, null, null, null,
                Array.Empty<MeshOption>(), null, Array.Empty<string>(), Array.Empty<string>(),
                false, Array.Empty<string>(), Array.Empty<string>(), ex.Message);
        }
    }

    /// <summary>
    /// The same reading, over exports a caller already has.
    ///
    /// Split out so a package that is not in the mounted game can be inspected -- one this tool
    /// has just authored, for instance. Reading it through the same code the browser uses is the
    /// point: it means "the new part is valid" is measured by the reader that reads real ones.
    /// </summary>
    public static Detail ReadExports(IReadOnlyList<UObject> exports, string packagePath)
    {
        try
        {

            string? name = null, shortDesc = null, longDesc = null, icon = null, bankTag = null,
                    animClass = null;
            var meshes = new List<MeshOption>();
            var hidden = new List<string>();
            var overrides = new List<string>();
            var locked = new List<string>();
            var locksOwn = false;
            var hidesOwn = false;
            var tables = new List<string>();
            var abilities = new List<string>();
            var replaces = false;
            var materialColor = false;
            var tintMasks = new List<string>();
            var tintSlots = new List<string>();
            var tintFragments = new List<TintFragment>();
            var fragments = new List<string>();
            var swaps = new List<MaterialSwap>();
            // Counted here rather than inside the reader, because it numbers the FRAGMENTS and
            // the reader is called once per fragment. Export order, which is stable per cook and
            // is not the order the Fragments array lists them in -- Clo010 has _0 _1 _14 _2 _3
            // and Hawks has _1 _2 _14 _0, so neither the array nor the export suffix is a number
            // anything should rely on.
            var meshFragment = 0;

            foreach (var e in exports)
            {
                // Recorded for EVERY export, before any switch decides whether this code knows
                // what it is. The unrecognised ones are the point.
                if (e.ExportType.Contains("Fragment", StringComparison.Ordinal))
                    fragments.Add(e.ExportType);

                switch (e.ExportType)
                {
                    // Subclassed by BitReactorUIDataFragmentWithPlatformOverrides, so this is a
                    // prefix test rather than an equality one.
                    case var t when t.StartsWith("BitReactorUIDataFragment", StringComparison.Ordinal):
                        name ??= TextOf(e.Properties, "DisplayName");
                        shortDesc ??= TextOf(e.Properties, "ShortDescription");
                        longDesc ??= TextOf(e.Properties, "LongDescription");
                        // Small first: it is the grid thumbnail, and the one that is set when
                        // only one of the two is.
                        var small = ImageOf(e.Properties, "SmallImage");
                        if (small.Path is null && small.BankTag is null)
                            small = ImageOf(e.Properties, "LargeImage");
                        icon ??= small.Path;
                        bankTag ??= small.BankTag;
                        break;

                    case var t2 when t2.Contains("MaterialColor", StringComparison.Ordinal):
                        materialColor = true;
                        break;

                    // THE TINT MASK LIVES HERE, not on the colour fragment. A part carries both:
                    // MaterialColor holds the colour and the parameter it feeds ("Color 02"), and
                    // MaterialTexture holds the mask and its parameter ("Tint Mask VT"). Reading
                    // the mask off the colour fragment finds nothing and reads as "this template
                    // paints no colours" on a template that plainly does.
                    case var t3 when t3.Contains("MaterialTexture", StringComparison.Ordinal):
                        var fragmentMask = TintMaskOf(e.Properties);
                        if (fragmentMask is { Length: > 0 } mask &&
                            !tintMasks.Contains(mask)) tintMasks.Add(mask);
                        var fragmentSlots = TargetOf(e.Properties).Slots;
                        foreach (var slot in fragmentSlots)
                            if (!tintSlots.Contains(slot, StringComparer.OrdinalIgnoreCase))
                                tintSlots.Add(slot);
                        if (fragmentMask is { Length: > 0 })
                            tintFragments.Add(new TintFragment(fragmentMask, fragmentSlots));
                        break;

                    // A RUN-TIME REPLACEMENT OF A SLOT'S MATERIAL. Read for its target slots and
                    // what it swaps in, which is all a build needs to know to say that a material
                    // an author put on one of those slots will be replaced in game.
                    case var t4 when t4.Contains("MaterialSwap", StringComparison.Ordinal):
                        swaps.Add(new MaterialSwap(
                            SoftPathOf(e.Properties, "ReplacementMaterial") ?? "",
                            TargetOf(e.Properties).Slots));
                        break;

                    case "CustomizationFragmentMesh":
                    case "CustomizationFragmentInstanceMesh":
                        ReadMeshFragment(e, meshes, ref animClass, ref replaces, meshFragment++);
                        break;

                    case "CustomizationFragmentOverrideSlots":
                    case "CustomizationFragmentInstanceOverrideSlots":
                        hidden.AddRange(TagsOf(e.Properties, "HiddenSlots"));
                        overrides.AddRange(TagsOf(e.Properties, "OverrideSlots"));
                        // THE THIRD CONTAINER, which was being read past. Clone legs take the
                        // boots' MESH through OverrideSlots and lock the boots SLOT through this
                        // one, and dropping only the first leaves the wearer unable to choose
                        // boots that are no longer being replaced.
                        locked.AddRange(TagsOf(e.Properties, "LockedSlots"));
                        // AND THE TWO FLAGS AFTER THEM, which do to the part's OWN slot what the
                        // containers do to other slots. A part that sets either is one the player
                        // cannot back out of once it is on, which is worth SAYING before somebody
                        // copies it and inherits the trap.
                        if (BoolOf(e.Properties, "bLockOwningSlot")) locksOwn = true;
                        if (BoolOf(e.Properties, "bHideOwningSlot")) hidesOwn = true;
                        break;

                    // Weapons. Same class, same cook, same delivery as armour -- the difference
                    // is entirely which fragments the part carries.
                    case "CustomizationFragmentAttributeSets":
                    case "CustomizationFragmentInstanceAttributeSets":
                        // AttributeSetsNew is the shipped one; AttributeSets is the older shape
                        // and is read too rather than assumed dead.
                        tables.AddRange(MapValuePaths(e.Properties, "AttributeSetsNew",
                                                      "DefaultAttributes"));
                        tables.AddRange(MapValuePaths(e.Properties, "AttributeSets", null));
                        break;

                    case "CustomizationFragmentAbilities":
                    case "CustomizationFragmentInstanceAbilities":
                        abilities.AddRange(SoftPathArray(e.Properties, "Abilities"));
                        break;
                }
            }

            return new Detail(packagePath, name, shortDesc, icon, bankTag,
                meshes, animClass, hidden, overrides, replaces, tables, abilities, null, tintMasks, materialColor,
                fragments, tintSlots, longDesc, locked, locksOwn, hidesOwn, swaps, tintFragments);
        }
        catch (Exception ex)
        {
            return new Detail(packagePath, null, null, null, null,
                Array.Empty<MeshOption>(), null, Array.Empty<string>(), Array.Empty<string>(),
                false, Array.Empty<string>(), Array.Empty<string>(), ex.Message);
        }
    }

    private static void ReadMeshFragment(UObject e, List<MeshOption> meshes,
                                         ref string? animClass, ref bool replaces, int fragment)
    {
        foreach (var opt in StructArray(e.Properties, "SkeletalMeshOptions"))
        {
            var mesh = SoftPathOf(opt.Properties, "SkeletalMesh");
            if (mesh is null) continue;
            meshes.Add(new MeshOption(mesh, TagsOf(opt.Properties, "RequiredTags"), fragment));
        }
        animClass ??= SoftPathOf(e.Properties, "OptionalAnimClass");
        if (BoolOf(e.Properties, "bReplacesBaseMesh")) replaces = true;
    }

    /// <summary>
    /// Resolve a texture for a part icon, given the path <see cref="Detail.IconPath"/> holds.
    /// Separate from <see cref="Read"/> because decoding is far more expensive than reading the
    /// reference, and the browser wants the reference for every visible row but the pixels for
    /// only the ones it can fit.
    /// </summary>
    public static UTexture? LoadIcon(IFileProvider provider, string iconPath)
    {
        try
        {
            return AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(iconPath))
                        .GetExports().OfType<UTexture>().FirstOrDefault());
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ property helpers
    //
    // Deliberately the same shapes RegionIndex uses. CUE4Parse hands back a GenericValue whose
    // runtime type depends on how the property was serialised, so every read is a type switch
    // rather than a cast.

    private static string? TextOf(List<FPropertyTag> props, string name)
    {
        var v = Value(props, name);
        var text = v switch
        {
            FText t => t.Text,
            string s => s,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// An FBitReactorImageReference is either a soft brush pointing at a texture, or a TAG
    /// naming an entry in a shared BitReactorImageBankSet. Both are returned: a bank tag is not
    /// a texture, but it is the difference between "this part has no icon" and "its icon is
    /// somewhere this does not follow yet", and those must not look the same in the UI.
    /// </summary>
    internal static (string? Path, string? BankTag) ImageOf(List<FPropertyTag> props, string name)
    {
        if (Struct(props, name) is not { } img) return (null, null);

        var tag = TagOf(img.Properties, "ImageBankTag");
        if (Struct(img.Properties, "SoftBrush") is not { } brush) return (null, tag);

        // WeakResourceObject is the cooked soft reference; ResourceObject is the hard one an
        // uncooked brush carries. Which is present depends on the brush, so try both.
        var path = SoftPathOf(brush.Properties, "WeakResourceObject")
                   ?? SoftPathOf(brush.Properties, "ResourceObject");
        return (path, tag);
    }

    internal static string? TagOf(List<FPropertyTag> props, string name) =>
        TagOf(Value(props, name));

    /// <summary>
    /// The same reading of a value a caller already has -- a TMap's KEY, which has no property
    /// name to look it up by.
    /// </summary>
    internal static string? TagOf(object? value)
    {
        var s = Unwrap(value) switch
        {
            FGameplayTag t => t.TagName.Text,
            FName fn => fn.Text,
            // A tag is not always serialised AS a tag. An image bank reference is a struct with a
            // TagName inside it, and falling through to ToString() on that returns the words
            // "CUE4Parse.UE4.Assets.Objects.FStructFallback" -- which is not null, so every caller
            // treated it as a tag it had successfully read.
            FStructFallback st => TagOf(st.Properties, "TagName"),
            { } v => v.ToString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) || s == "None" ? null : s;
    }

    /// <summary>
    /// The tags in a named container, from whichever export carries one.
    ///
    /// Public because an OVERRIDE needs to read a container it is about to rewrite, and the two
    /// ways a container arrives -- a typed FGameplayTagContainer behind an FScriptStruct, or a
    /// plain struct fallback -- are both handled here already. Re-deriving that elsewhere is how
    /// the silent-empty-list failure documented on Unwrap gets reintroduced.
    /// </summary>
    public static List<string> TagsIn(IEnumerable<UObject> exports, string property)
    {
        foreach (var e in exports)
        {
            var tags = TagsOf(e.Properties, property);
            if (tags.Count > 0) return tags;
        }
        return new List<string>();
    }

    internal static List<string> TagsOf(List<FPropertyTag> props, string name)
    {
        var result = new List<string>();
        if (Unwrap(Value(props, name)) is FGameplayTagContainer c)
        {
            foreach (var t in c.GameplayTags) result.Add(t.TagName.Text);
            return result;
        }
        // A container that came through as a plain struct rather than a typed one.
        if (Struct(props, name) is { } fb && Value(fb.Properties, "GameplayTags") is UScriptArray arr)
            foreach (var item in arr.Properties)
                if (item.GenericValue?.ToString() is { Length: > 0 } s)
                    result.Add(s);
        return result;
    }

    /// <summary>
    /// Take the struct out of its property wrapper.
    ///
    /// A StructProperty's GenericValue is an <c>FScriptStruct</c>, never the struct itself, so
    /// <c>is FGameplayTagContainer</c> against the raw value is always false. It fails SILENTLY:
    /// every part reported an empty HiddenSlots and RequiredTags, which reads exactly like a game
    /// that does not use those fields -- and this one does, on every helmet it ships. Text, soft
    /// object and bool properties are not wrapped, which is why those worked and made the zeros
    /// look believable.
    /// </summary>
    /// <summary>
    /// A property's value with the wrappers taken off.
    ///
    /// There are TWO, not one. A struct property's GenericValue is an FScriptStruct around the
    /// struct -- and sometimes an FInstancedStruct around THAT. Handling only the first reads
    /// `MaterialTargetOverride` as nothing, which is how a tint fragment appeared to name no
    /// material slot and no parameter on a part that plainly targets "MI_TORS" / "Tint Mask VT".
    /// The props printer had known about both wrappers for months.
    /// </summary>
    internal static object? Unwrap(object? value) => value switch
    {
        FScriptStruct s => Unwrap(s.StructType),
        FInstancedStruct i => Unwrap(i.ScriptStruct?.StructType),
        _ => value,
    };

    /// <summary>
    /// A soft object reference, as the full OBJECT path -- "/Game/X/SK_Y.SK_Y", suffix included.
    ///
    /// Deliberately not stripped to the package path. Everything that LOADS one of these passes it
    /// through <see cref="AssetLoader.PackagePath"/>, which drops the suffix anyway, so keeping it
    /// costs nothing; and everything that AUTHORS one needs it, because a mod definition written
    /// without the suffix imports as an empty soft pointer and the part silently equips nothing.
    /// The suffix is also not reconstructable in general -- an anim class is "Name.Name_C", not
    /// "Name.Name" -- so guessing it back later is not an option.
    /// </summary>
    internal static string? SoftPathOf(List<FPropertyTag> props, string name) =>
        SoftPathOf(Value(props, name));

    /// <summary>
    /// The same reading of a value a caller already has -- a TMap's, which has no property name to
    /// look up. Kept beside the named form so both shapes a reference arrives in stay in one place.
    /// </summary>
    internal static string? SoftPathOf(object? value)
    {
        var path = Unwrap(value) switch
        {
            FSoftObjectPath sop => sop.AssetPathName.Text,
            FPackageIndex pi => pi.ResolvedObject?.GetPathName(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(path) || path == "None" ? null : path;
    }

    /// <summary>Soft object paths out of an array property, as package paths.</summary>
    internal static List<string> SoftPathArray(List<FPropertyTag> props, string name)
    {
        var result = new List<string>();
        if (Value(props, name) is not UScriptArray arr) return result;
        foreach (var item in arr.Properties)
            if (Path(item.GenericValue) is { } p)
                result.Add(p);
        return result;
    }

    /// <summary>
    /// Package paths out of a TMap's VALUES. <paramref name="inner"/> names a field to read from
    /// a struct value; pass null when the value is itself the reference.
    /// </summary>
    private static List<string> MapValuePaths(List<FPropertyTag> props, string name, string? inner)
    {
        var result = new List<string>();
        if (Value(props, name) is not UScriptMap map) return result;
        foreach (var (_, v) in map.Properties)
        {
            var raw = Unwrap(v?.GenericValue);
            if (inner is not null && raw is FStructFallback fb)
            {
                if (SoftPathOf(fb.Properties, inner) is { } p) result.Add(p);
            }
            else if (Path(raw) is { } q) result.Add(q);
        }
        return result;
    }

    /// <summary>A reference of any shape as a full object path, or null if it is not one.</summary>
    private static string? Path(object? v)
    {
        var path = v switch
        {
            FSoftObjectPath sop => sop.AssetPathName.Text,
            FPackageIndex pi => pi.ResolvedObject?.GetPathName(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(path) || path == "None" ? null : path;
    }

    private static bool BoolOf(List<FPropertyTag> props, string name) =>
        Value(props, name) is bool b && b;

    internal static object? Value(List<FPropertyTag> props, string name) =>
        props.FirstOrDefault(p => p.Name.Text == name)?.Tag?.GenericValue;

    /// <summary>
    /// The tint mask a colour fragment paints through.
    ///
    /// The reference is not a plain one: the fragment wraps it in a STRUCT that also carries the
    /// material parameter to feed it to ("Tint Mask VT"), so reading the property as an object
    /// path returns nothing at all -- which read as "this template paints no colours" on a
    /// template that plainly does. Tried both ways round, because a bare reference is the shape
    /// another fragment could reasonably use.
    /// </summary>
    /// <summary>
    /// WHICH MATERIAL SLOT A TINT FRAGMENT TARGETS, and which parameter of it.
    ///
    /// Both halves matter and only one of them was ever read. A fragment says
    /// `MaterialSlotNames = ["MI_TORS"]` and `MaterialParameterName = "Tint Mask VT"` -- so a mod
    /// whose mesh names its slots differently is targeted at a slot that is not there, and the
    /// tint lands nowhere or on the wrong material. Redirecting the TEXTURE alone does not fix
    /// that, which is worth knowing before believing a one-line change.
    /// </summary>
    public static (List<string> Slots, string? Parameter) TargetOf(List<FPropertyTag> props)
    {
        var slots = new List<string>();
        string? parameter = null;

        if (Struct(props, "MaterialTargetOverride") is { } target)
        {
            parameter = TextOf(target.Properties, "MaterialParameterName");
            if (Value(target.Properties, "MaterialSlotNames") is UScriptArray names)
                foreach (var n in names.Properties)
                    if (n.GenericValue?.ToString() is { Length: > 0 } text) slots.Add(text);
        }
        parameter ??= TextOf(props, "MaterialParameterName");
        return (slots, parameter);
    }

    /// <summary>
    /// A colour fragment's value as "RRGGBB", or null.
    ///
    /// The palette is 474 parts under Customizations/.../Outfit/Color/, each one a DisplayName
    /// and a single colour, and this is the whole of what they carry.
    /// </summary>
    public static string? ColourOf(List<FPropertyTag> props)
    {
        var value = Unwrap(Value(props, "Color"));
        if (value is null) return null;

        var text = value.ToString();
        if (text is null) return null;

        // FColor prints as hex already; anything else is taken at its word and trimmed to six.
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        return hex.Length >= 6 ? hex[..6] : null;
    }

    /// <summary>Name and colour of one palette part, for the swatch list.</summary>
    public static (string? Name, string? Colour) ColourPart(IFileProvider provider, string packagePath)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(packagePath)).GetExports().ToArray());
            string? name = null, colour = null;
            foreach (var e in exports)
            {
                if (e.ExportType.StartsWith("BitReactorUIDataFragment", StringComparison.Ordinal))
                    name ??= TextOf(e.Properties, "DisplayName");
                if (e.ExportType.Contains("MaterialColor", StringComparison.Ordinal))
                    colour ??= ColourOf(e.Properties);
            }
            return (name, colour);
        }
        catch { return (null, null); }
    }

    public static string? TintMaskPublic(List<FPropertyTag> props) => TintMaskOf(props);

    internal static string? TintMaskOf(List<FPropertyTag> props)
    {
        if (SoftPathOf(props, "Texture") is { Length: > 0 } direct) return direct;
        if (Struct(props, "Texture") is not { } wrapper) return null;

        foreach (var inner in wrapper.Properties)
            if (SoftPathOf(inner.Tag) is { Length: > 0 } found) return found;
        return null;
    }

    private static FStructFallback? Struct(List<FPropertyTag> props, string name) =>
        Unwrap(Value(props, name)) as FStructFallback;

    internal static List<FStructFallback> StructArray(List<FPropertyTag> props, string name)
    {
        var result = new List<FStructFallback>();
        if (Value(props, name) is not UScriptArray arr) return result;
        foreach (var item in arr.Properties)
            if (item.GenericValue is FScriptStruct { StructType: FStructFallback fb })
                result.Add(fb);
        return result;
    }
}

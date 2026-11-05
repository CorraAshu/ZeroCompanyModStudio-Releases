namespace UEToolkit.Core;

using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;

/// <summary>
/// Every UTILITY ITEM in the game as a whole chain -- the grenades, medpacs, the flamethrower, the
/// scattergun, and the Jedi passives -- so a Utilities tab can offer "duplicate this one" without
/// a person having to know that a grenade is four assets.
///
/// A GRENADE IS NOT A WEAPON, and the difference is the whole reason this file is not
/// <see cref="WeaponIndex"/> with different prefixes. A weapon's model comes out of shared mesh
/// PALETTES keyed by a type tag, which is why duplicating one means five copies and a re-keyed tag
/// in each. A utility item's model is set directly on its actor's component template:
///
///     UtilityItem_ThermalDetonator_T1   BP_BrunoUtilityInventoryItem_C  -- name, icon, price
///       AssociatedGearKit    -> GK_ThermalDetonator_T1                  -- the gear kit
///         ActorClass           -> BP_Weapon_ImpactGrenade_C             -- the thrown actor
///           Weapon_SM            -> SM_Grenade_Impact_01                -- THE MODEL, one property
///         AssociatedAbilitySet -> AbilitySet_ThermalDetonator_T1
///           Ability              -> GA_ExplosiveGrenade_T1              -- damage, range, radius
///           DefaultAttributes    -> Attributes_ThermalDetonator_T1      -- an AttributeMetaData table
///
/// No palettes, no type parts, no minted gameplay tag. Four assets and a mesh reference.
///
/// THE CHAIN IS READ, NOT DERIVED FROM NAMES -- and this is the one place where copying
/// <see cref="WeaponIndex"/> would have been wrong. The game names weapons consistently enough
/// that five prefixes plus one key find all five assets. Utility items are not named that way:
///
///     UtilityItem_Medkit_T1   -> GK_MedKit_T1          (different case)
///     UtilityItem_RSKF44_T1   -> GK_Utility_RSKF-44_T1 (different name entirely)
///
/// A name-derived chain drops those silently, which reads as "the game does not have a medpac". So
/// the gear kit is read off the item's own <c>AssociatedGearKit</c>, the actor off the kit's
/// <c>ActorClass</c>, and the mesh off the actor. One package read per link, and every link is
/// what the game itself says rather than what a pattern predicts.
/// </summary>
public static class UtilityIndex
{
    /// <summary>The shop item -- what the Armoury sells and equips.</summary>
    public const string ItemPrefix = "UtilityItem_";

    /// <summary>The gear kit an item names. Shared with weapons, which is why the folder matters.</summary>
    public const string GearKitPrefix = "GK_";

    /// <summary>Where the Armoury's own event tag says an item goes: Throwable, Health, Ability...</summary>
    private const string SlotTag = "UI.Event.Armory.Utilities.Equip.";

    /// <summary>What a rarity tag looks like inside ItemTags. Same shape as a modification's.</summary>
    private const string RarityTag = "ItemType.Rarity.";

    /// <summary>
    /// One utility item, with every asset needed to duplicate it and every value worth editing.
    ///
    /// <paramref name="Missing"/> empty means the chain is complete. Anything in it names a link
    /// that was expected and not found -- listed with the reason rather than dropped, for the
    /// reason <see cref="WeaponIndex.Weapon"/> gives: an item that silently fails to appear is
    /// indistinguishable from one the game does not have.
    /// </summary>
    public sealed record Utility(
        string Key,
        string Item, string ItemExport, string UiExport,
        string Name, string Does, string Icon,
        string TypeTag, string Slot, string Rarity,
        int Buy, int Sell,
        string GearKit, string Actor, string Mesh,
        string AbilitySet, string Ability, string Attributes,
        IReadOnlyList<string> Requires, IReadOnlyList<string> Ignores,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> Missing)
    {
        public bool Complete => Missing.Count == 0;

        /// <summary>"Impact Bomb (Throwable)", which is how a person recognises it in a list.</summary>
        public string Describe =>
            Name.Length > 0 ? (Slot.Length > 0 ? $"{Name} ({Slot})" : Name) : Key;

        /// <summary>
        /// The game HAS this one and cannot equip it: a gear kit with an actor, a model and an
        /// ability, and NO shop item to reach it by.
        ///
        /// The same shape as the weapons that ship without a gear kit. What stands between one of
        /// these and a player is a single small data asset.
        /// </summary>
        public bool NeedsItem => Item.Length == 0 && GearKit.Length > 0 && Actor.Length > 0;

        /// <summary>Buyable in the Armoury, rather than granted by a class or a hero.</summary>
        public bool Buyable => Tags.Any(t => t.EndsWith("ItemType.Buyable", StringComparison.Ordinal));

        /// <summary>The asset name half of a package path, which is what a list shows.</summary>
        public static string Leaf(string package) =>
            package.Length == 0 ? "" : package[(package.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// Every utility item, plus every gear kit that has no item, sorted by slot then name.
    ///
    /// THE PRISTINE GAME ONLY, for the reason <see cref="WeaponIndex.Of"/> gives: the Studio
    /// mounts the game's whole folder, so every installed mod is mounted too, and reading them
    /// here would offer a mod's own grenade as one of the game's to duplicate.
    /// </summary>
    public static List<Utility> Of(IFileProvider provider)
    {
        var items = new List<(string Package, string Asset)>();
        var kits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = key.LastIndexOf('/');
            if (slash < 0) continue;

            var asset = key[(slash + 1)..^".uasset".Length];
            var isItem = asset.StartsWith(ItemPrefix, StringComparison.Ordinal);
            var isKit = asset.StartsWith(GearKitPrefix, StringComparison.Ordinal);
            if (!isItem && !isKit) continue;

            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pkg) continue;
            if (isItem) items.Add((pkg, asset));
            else kits[pkg] = asset;
        }

        var found = new List<Utility>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pkg, asset) in items)
            if (Read(provider, pkg, asset) is { } entry)
            {
                found.Add(entry);
                if (entry.GearKit.Length > 0) claimed.Add(entry.GearKit);
            }

        // A SECOND PASS, FROM THE GEAR KIT -- because the first pass cannot see the ones worth
        // finding. The Armoury reaches a utility through its shop item, so keying this index on
        // the item lists one only when it already has the very thing an author would want to give
        // it.
        //
        // The folder comes from the kits the items already named rather than being written down,
        // so this cannot drift from where the game keeps them, and does nothing if nothing
        // matched. Weapon gear kits live in that same folder and are excluded by their own tags --
        // see <see cref="IsUtility"/>, which is a measurement rather than the guess this comment
        // used to carry ("a rifle's kit falls out on its own", which was simply false: the pass
        // swept in a hundred rifles, lightsabers and enemy kits and buried the three rows worth
        // finding).
        //
        // THE COMMONEST FOLDER, NOT THE FIRST ONE. Taking the first kit any item named looked
        // equivalent and is not: the cut Vibrosword sits in /Game/__PendingDelete/, and when its
        // item came first the whole second pass searched that folder and found nothing at all --
        // silently, because an empty second pass looks exactly like a game with no orphans in it.
        if (Commonest(found.Where(u => u.GearKit.Length > 0).Select(u => Folder(u.GearKit)))
            is { Length: > 0 } folder)
            foreach (var (pkg, asset) in kits)
            {
                if (!Folder(pkg).Equals(folder, StringComparison.OrdinalIgnoreCase)) continue;
                if (claimed.Contains(pkg)) continue;
                if (Orphan(provider, pkg, asset) is { } entry) found.Add(entry);
            }

        // Slot NAME inside the order bucket, or the slots the order does not name -- Tech, Armor --
        // interleave into one another and the list reads as though it is not grouped at all.
        return found.OrderBy(u => SlotOrder(u))
                    .ThenBy(u => u.Slot, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(u => u.Name.Length > 0 ? u.Name : u.Key, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(u => u.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>The folder a package sits in, with its trailing slash.</summary>
    private static string Folder(string package)
    {
        var at = package.LastIndexOf('/');
        return at > 0 ? package[..(at + 1)] : "";
    }

    /// <summary>
    /// The value that appears most often, or "" for an empty sequence.
    ///
    /// Used where a majority is the honest answer and any single sample is not -- see the second
    /// pass, where one cut asset in an unusual folder was enough to send the whole search there.
    /// </summary>
    private static string Commonest(IEnumerable<string> values) =>
        values.Where(v => v.Length > 0)
              .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
              .OrderByDescending(g => g.Count())
              .FirstOrDefault()?.Key ?? "";

    /// <summary>
    /// One utility item and everything down its chain, or null when its package will not open.
    ///
    /// A package that cannot be read drops out rather than throwing: this runs for the whole game
    /// every time the tab opens, and one unreadable asset must not take the other seventy-nine.
    /// </summary>
    private static Utility? Read(IFileProvider provider, string package, string asset)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(package)).GetExports().ToList());

            // The item's own export is the one named after the asset; the TEXT is not on it. It
            // lives on a BitReactorUIDataFragment the item lists as a fragment, which is its own
            // export in the same package -- so an edit addressed at the item finds no DisplayName
            // at all. The same split a modification has; see Modifications.Shop.
            var item = exports.FirstOrDefault(e => e.Name == asset)
                       ?? exports.FirstOrDefault(
                           e => e.ExportType.EndsWith("InventoryItem_C", StringComparison.Ordinal));
            if (item is null) return null;
            var ui = exports.FirstOrDefault(
                e => e.ExportType.StartsWith("BitReactorUIDataFragment", StringComparison.Ordinal));

            var tags = PartDetails.TagsOf(item.Properties, "ItemTags");
            var missing = new List<string>();

            var gearKit = Package(PathOf(PartDetails.Value(item.Properties, "AssociatedGearKit")));
            if (gearKit.Length == 0) missing.Add("AssociatedGearKit");

            var (actor, mesh, abilitySet, ability, attributes) = Chain(provider, gearKit, missing);

            return new Utility(
                Key: asset[ItemPrefix.Length..],
                Item: package, ItemExport: item.Name, UiExport: ui?.Name ?? "",
                Name: ui is null ? "" : Text(ui.Properties, "DisplayName"),
                Does: ui is null ? "" : Modifications.Plain(Text(ui.Properties, "ShortDescription")),
                Icon: ui is null ? "" : Brush(ui.Properties, "SmallImage", "LargeImage"),
                TypeTag: PartDetails.TagOf(item.Properties, "UtilityItemTypeTag") ?? "",
                Slot: Suffix(tags, SlotTag), Rarity: Suffix(tags, RarityTag),
                Buy: Credits(item.Properties, "BuyCost"),
                Sell: Credits(item.Properties, "SellValue"),
                GearKit: gearKit, Actor: actor, Mesh: mesh,
                AbilitySet: abilitySet, Ability: ability, Attributes: attributes,
                Requires: ClassTags(item.Properties, "RequireTags"),
                Ignores: ClassTags(item.Properties, "IgnoreTags"),
                Tags: tags, Missing: missing);
        }
        catch { return null; }
    }

    /// <summary>
    /// A gear kit with no shop item, read as far as it goes.
    ///
    /// Its NAME comes from the kit's own GearKitInfo, which is where a shop item would have taken
    /// it from anyway -- so an orphan reads as "Timed Mine" rather than as a filename. A kit whose
    /// actor cannot be read is not a utility at all and returns null rather than appearing as a
    /// broken row.
    /// </summary>
    private static Utility? Orphan(IFileProvider provider, string package, string asset)
    {
        var name = "";
        List<string> tags;
        try
        {
            var kit = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(package)).GetExports()
                        .FirstOrDefault(e => e.Name == asset));
            if (kit is null) return null;
            tags = PartDetails.TagsOf(kit.Properties, "GameplayTags");
            if (!IsUtility(tags)) return null;
            if (PartDetails.Unwrap(PartDetails.Value(kit.Properties, "GearKitInfo"))
                is FStructFallback info) name = Text(info.Properties, "DisplayName");
        }
        catch { return null; }

        var missing = new List<string> { ItemPrefix + asset[GearKitPrefix.Length..] };
        var (actor, mesh, abilitySet, ability, attributes) = Chain(provider, package, missing);
        if (actor.Length == 0) return null;

        return new Utility(
            Key: asset[GearKitPrefix.Length..],
            Item: "", ItemExport: "", UiExport: "",
            Name: name, Does: "", Icon: "",
            TypeTag: "", Slot: "", Rarity: "", Buy: 0, Sell: 0,
            GearKit: package, Actor: actor, Mesh: mesh,
            AbilitySet: abilitySet, Ability: ability, Attributes: attributes,
            Requires: [], Ignores: [], Tags: tags, Missing: missing);
    }

    /// <summary>
    /// WHAT A UTILITY DOES: every number on its ability's class default object.
    ///
    /// The same shape as a modification's stat and edited the same way -- a top-level scalar on a
    /// Blueprint CDO, located by <see cref="PropertyAdd"/> and written at its own width -- which is
    /// why it returns <see cref="Modifications.Number"/> rather than a record of its own. What
    /// consumes it is the same too: <c>ModOverride.NumberChange</c> takes exactly a dotted path
    /// and a value.
    ///
    /// Measured across the eight ability families, 2026-09-06. They are plain and few:
    ///
    ///     grenades      GrenadeBaseDamage, MaxThrowRange, GrenadeBlastRadius
    ///                   + per type: Displacement Distance, Daze Chance v Organic
    ///     cone weapons  Scattergun Damage / Flamethrower Damage, Cone Width, Cone Length
    ///     medpacs       Heal Percentage
    ///
    /// TOP LEVEL ONLY, and BOOLS AND STRUCTS ARE LEFT OUT -- for the reasons Modifications gives:
    /// a checkbox drawn as a number box accepts 0.5, and the numbers nested in a UI struct are
    /// display copies that would let somebody change the label without changing the thing.
    ///
    /// **43 OF THE 84 COME BACK EMPTY, and that is not a fault. The caller must NOT draw a blank
    /// as a zero.** Two different reasons, and they need different edits:
    ///
    /// - INHERITED. `GA_Medkit_T2`'s ParentClass is `GA_Medkit_T1_C`, and a cooked package does
    ///   not write a property identical to the class it derives from -- so the tier that reads
    ///   nothing is the tier that agrees with its parent. Changing one means ADDING the property
    ///   rather than writing over it: `addProperties`, not `numbers`. The same split the
    ///   stabilizers have in <see cref="Modifications.Entry.Inherited"/> and the same one bolt
    ///   colour has in <see cref="BoltColour"/>.
    /// - EFFECT-DRIVEN. `GA_Serenity_T1` is one GameplayEffect and no scalar at all. Its number is
    ///   in `GE_Serenity_T1`, which this does not follow.
    ///
    /// Which of the two a given blank is has NOT been established per item -- only for the medkit
    /// and Serenity, which are one example of each.
    /// </summary>
    public static List<Modifications.Number> Numbers(IFileProvider provider, Utility utility)
    {
        var found = new List<Modifications.Number>();
        if (utility.Ability.Length == 0) return found;
        try
        {
            var cdo = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(utility.Ability)).GetExports()
                        .FirstOrDefault(e => e.Name.StartsWith("Default__", StringComparison.Ordinal)));
            if (cdo is null) return found;

            foreach (var tag in cdo.Properties)
                if (tag.Tag?.GenericValue is double or float or int &&
                    Convert.ToDouble(tag.Tag.GenericValue) is var v)
                    found.Add(new Modifications.Number(tag.Name.Text, tag.Name.Text, v));
        }
        catch { }
        return found;
    }

    /// <summary>
    /// Whether a gear kit is a UTILITY's rather than a weapon's, by the tags the kit itself
    /// carries. Measured across ten kits of every kind, 2026-09-06:
    ///
    ///     GK_ThermalDetonator_T1        ItemType.Utility, Item.Utility.Grenade
    ///     GK_TimedMine_T1               ItemType.Utility, Item.Utility.Grenade
    ///     GK_MedKit_T2_FirstResponder   ItemType.Utility, Item.Utility.MedKit
    ///     GK_Serenity_T1                ItemType.Utility, Item.Utility.Serenity
    ///     GK_QuickdrawPistol_T1                           Item.Utility.HoldoutPistol
    ///     GK_Rifle_DC-15A               ItemType.Weapon.Primary
    ///     GK_Repeater_T-21              ItemType.Weapon.Primary
    ///     GK_MeleeWeap_Enemy_Coil_Striker  ItemType.Weapon.Primary
    ///
    /// TWO KITS SAY NEITHER, and they are excluded on purpose rather than by accident.
    /// <c>GK_CombatStim_T1</c> is tagged only <c>Weapon.CombatStim</c> and its GearKitInfo reads
    /// "Bacta Stim" -- a superseded copy of the four <c>GK_BactaStim_T*</c> that ship with items.
    /// <c>GK_Utility_RPS-6</c> is tagged <c>Weapon.RocketLauncher</c>, and the RPS-6 is already
    /// listed by <see cref="WeaponIndex"/> as a weapon needing a gear kit. Neither is lost; both
    /// would be noise here.
    /// </summary>
    private static bool IsUtility(IReadOnlyList<string> tags) =>
        tags.Any(t => t.EndsWith(".ItemType.Utility", StringComparison.Ordinal) ||
                      t.Contains(".Item.Utility.", StringComparison.Ordinal));

    /// <summary>
    /// Everything below the gear kit: the actor, its model, the ability set, the ability the set
    /// grants and the attribute table it seeds.
    ///
    /// Each link is read off the previous one. A link that is absent is recorded in
    /// <paramref name="missing"/> rather than guessed at -- half a chain reported as a whole one
    /// is what makes a duplicate quietly share the original's model.
    /// </summary>
    private static (string Actor, string Mesh, string AbilitySet, string Ability, string Attributes)
        Chain(IFileProvider provider, string gearKit, List<string> missing)
    {
        if (gearKit.Length == 0) return ("", "", "", "", "");

        var actor = "";
        var abilitySet = "";
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(gearKit)).GetExports().ToList());

            // The actor is on the gear ITEM export and the ability set on the kit export, so both
            // are looked for across every export rather than on one guessed to hold them.
            foreach (var e in exports)
            {
                if (actor.Length == 0)
                    actor = Package(PathOf(PartDetails.Value(e.Properties, "ActorClass")));
                if (abilitySet.Length == 0)
                    abilitySet = Package(PathOf(PartDetails.Value(e.Properties, "AssociatedAbilitySet")));
            }
            if (actor.Length == 0) missing.Add(Utility.Leaf(gearKit) + " -> ActorClass");
        }
        catch { missing.Add(gearKit); return ("", "", "", "", ""); }

        var mesh = actor.Length > 0 ? MeshOf(provider, actor) : "";
        var (ability, attributes) = abilitySet.Length > 0 ? Granted(provider, abilitySet) : ("", "");
        return (actor, mesh, abilitySet, ability, attributes);
    }

    /// <summary>
    /// The model a utility actor shows, off its Weapon_SM component template.
    ///
    /// THIS IS THE ONE PROPERTY A MODEL SWAP CHANGES, and it is why a grenade is so much cheaper
    /// to re-model than a rifle. A weapon actor's own template carries NO mesh -- a rifle's comes
    /// from the palettes -- so the same read against BP_Longarm_IQA-11 finds nothing, correctly.
    ///
    /// Static first, then skeletal: a thrown prop is a StaticMesh, but the smoke grenade and the
    /// thermal detonator each ship an SK_ beside their SM_, so neither may be assumed.
    /// </summary>
    private static string MeshOf(IFileProvider provider, string actor)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(actor)).GetExports().ToList());
            foreach (var name in new[] { "StaticMesh", "SkeletalMesh", "SkinnedAsset" })
                foreach (var e in exports)
                    if (e.GetOrDefault<FPackageIndex?>(name, null) is { IsNull: false } m &&
                        Safe(m) is { Length: > 0 } path)
                        return Package(path);
        }
        catch { }
        return "";
    }

    /// <summary>
    /// The ability an ability set grants and the attribute table it seeds.
    ///
    /// The set is the join between the gear kit and the two things that decide what a grenade
    /// DOES: the ability carries damage, throw range and blast radius as plain scalars on its
    /// class default object, and the table carries the attributes granted while it is held.
    ///
    /// Read through CopyContents rather than by walking the properties, because a granted ability
    /// is an element of a struct inside an array and the table is a soft path inside another --
    /// two nestings this would otherwise have to reproduce, in a file that already reads three
    /// packages per row.
    /// </summary>
    private static (string Ability, string Attributes) Granted(IFileProvider provider, string set)
    {
        try
        {
            var refs = CopyContents.Read(provider, set, out _);
            string First(string prefix) =>
                refs.FirstOrDefault(r => Utility.Leaf(r.Package)
                        .StartsWith(prefix, StringComparison.Ordinal))?.Package ?? "";
            return (First("GA_"), First("Attributes_"));
        }
        catch { return ("", ""); }
    }

    // ------------------------------------------------------------------ property helpers
    //
    // The same shapes Modifications reads, for the same reason: CUE4Parse hands back a
    // GenericValue whose runtime type depends on how the property was serialised.

    /// <summary>
    /// A reference's full object path, whether it was stored soft or hard.
    ///
    /// AssociatedGearKit is a soft path and ActorClass is a hard import, and a reader that handled
    /// only one of them would find half the chain. WHICH MAP re-points each is CopyContents'
    /// business; here they are both just a path.
    /// </summary>
    private static string PathOf(object? value) => PartDetails.Unwrap(value) switch
    {
        FSoftObjectPath sop => sop.AssetPathName.Text ?? "",
        FPackageIndex pi => Safe(pi),
        string s when s.StartsWith('/') => s,
        _ => "",
    };

    /// <summary>Resolving an import can throw on a reference the mount does not carry.</summary>
    private static string Safe(FPackageIndex pi)
    {
        try { return pi.ResolvedObject?.GetPathName() ?? ""; } catch { return ""; }
    }

    /// <summary>"/A/B/C.C_C" -> "/A/B/C". A package path is what everything downstream wants.</summary>
    private static string Package(string objectPath)
    {
        if (objectPath.Length == 0) return "";
        var dot = objectPath.LastIndexOf('.');
        var slash = objectPath.LastIndexOf('/');
        return dot > slash ? objectPath[..dot] : objectPath;
    }

    /// <summary>
    /// The tags inside a CharacterClassRequirements struct -- who may equip this at all.
    ///
    /// Every throwable in the game ignores <c>br.CharacterClass.Bespoke.Jedi</c>, and a hero's
    /// passives REQUIRE that hero's own class tag, so a duplicate that copied neither would be
    /// offered to the wrong people. Read from the struct rather than from the item, because that
    /// is where it lives.
    /// </summary>
    private static List<string> ClassTags(List<FPropertyTag> props, string name)
    {
        if (PartDetails.Unwrap(PartDetails.Value(props, "CharacterClassRequirements"))
            is not FStructFallback req) return [];
        return PartDetails.TagsOf(req.Properties, name);
    }

    /// <summary>The tail of the first tag carrying a prefix: ...Equip.Throwable -> "Throwable".</summary>
    private static string Suffix(IReadOnlyList<string> tags, string prefix)
    {
        foreach (var t in tags)
        {
            var at = t.IndexOf(prefix, StringComparison.Ordinal);
            if (at >= 0) return t[(at + prefix.Length)..];
        }
        return "";
    }

    /// <summary>What it costs, out of the credits entry of a resource -> amount map.</summary>
    private static int Credits(List<FPropertyTag> props, string name)
    {
        if (PartDetails.Value(props, name) is not UScriptMap map) return 0;
        foreach (var kv in map.Properties)
            if (kv.Value?.GenericValue is int i) return i;
        return 0;
    }

    private static string Text(List<FPropertyTag> props, string name) =>
        PartDetails.Value(props, name) switch
        {
            FText t => t.Text ?? "",
            string s => s,
            _ => "",
        };

    /// <summary>The texture an image reference points at, trying each named field in turn.</summary>
    private static string Brush(List<FPropertyTag> props, params string[] names)
    {
        foreach (var name in names)
        {
            if (PartDetails.Unwrap(PartDetails.Value(props, name)) is not FStructFallback image) continue;
            if (PartDetails.Unwrap(PartDetails.Value(image.Properties, "SoftBrush"))
                is not FStructFallback brush) continue;
            var path = PartDetails.SoftPathOf(PartDetails.Value(brush.Properties, "ResourceObject"))
                       ?? PartDetails.SoftPathOf(PartDetails.Value(brush.Properties, "WeakResourceObject"));
            if (path is { Length: > 0 }) return path;
        }
        return "";
    }

    /// <summary>
    /// Slot order as the Armoury groups them rather than alphabetically, with the two kinds of
    /// row that carry no slot tag kept apart and kept last.
    ///
    /// They are not the same thing and reading them as one is a lie about the game: an ORPHAN is a
    /// finished thing the game cannot equip, which is the row worth finding, while an item with no
    /// slot tag is a cut asset whose chain does not resolve. Both used to print under one heading
    /// reading "no shop item", which said the wrong thing about whichever of the two it was.
    /// </summary>
    private static int SlotOrder(Utility u) => u.Slot switch
    {
        "Throwable" => 0,
        "Health" => 1,
        "SecondaryWpn" => 2,
        "Ability" => 3,
        "" => u.NeedsItem ? 8 : 9,
        _ => 4,
    };

    /// <summary>The heading a row belongs under, which is its slot unless it has no slot.</summary>
    public static string Group(Utility u) =>
        u.Slot.Length > 0 ? u.Slot
        : u.NeedsItem ? "the game cannot equip these"
        : "no slot tag";
}

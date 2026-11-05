using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;

namespace UEToolkit.Core;

/// <summary>
/// The weapon modifications a player fits in the Armoury -- barrels, cells, scopes and
/// stabilizers -- and the numbers that decide what each one actually does.
///
/// A modification is TWO assets, and only one of them is interesting here. The inventory item
/// (<c>Modification_Barrel_Condenser_T1</c>) is what the Black Market sells: cost, rarity, the
/// slot tag, the mesh it fits. The ABILITY (<c>GA_Modification_Barrel_Condenser_T1</c>) is what it
/// does, and that is one number on the class default object:
///
///     GameplayEffectModifer   2      (the four tiers ship 2, 3, 4, 6)
///
/// The GameplayEffect the ability names holds no number of its own -- it is a SetByCaller with a
/// magnitude of zero -- so the ability's scalar is the whole stat. Editing it is an ordinary
/// override, and the value read back from a built mod confirms it.
///
/// WHY THE PROPERTY IS FOUND BY NAME AND ITS WIDTH CARRIED. `GameplayEffectModifer` is a DOUBLE,
/// and the four bytes of the equivalent float are the double's upper half for every round value
/// the game ships. A search for the float's bytes therefore matches halfway into the double and
/// writes a number nobody asked for while reporting success -- see <see cref="ScalarEdit"/>, which
/// exists because that happened. Everything here addresses a property by export and dotted path so
/// the width is known rather than assumed.
///
/// THE STAT HAS A SECOND COPY beside it. Most tiers carry <c>Effect UI Data.EffectValue</c> -- a
/// FLOAT, four bytes, immediately after the double -- reading the same number.
///
/// What reads it is NOT established, and the obvious guess is wrong: the descriptions interpolate
/// <c>{effectDescription_1}</c>, and the Tracking Scopes do not keep the copy in step with the
/// stat. The first (T1) leaves EffectValue unserialised, and the other three carry 4, 6 and 8
/// against stats of 2, 3 and 4 (measured on the game, 2026-11-02) -- so the sentence a player
/// reads is not simply that copy. It is a copy the menus keep for something, and the only safe
/// thing to say about it is that the two agree in MOST shipped data (see
/// <see cref="Entry.CopyDisagrees"/> for the four that do not).
///
/// So it is MOVED WITH THE STAT rather than left behind, and only when the two currently agree. A
/// modification whose copy already differs is saying something this does not understand, and
/// overwriting it would be a guess dressed up as a fix.
/// </summary>
public static class Modifications
{
    /// <summary>The asset-name prefix every modification ability shares.</summary>
    public const string Prefix = "GA_Modification_";

    /// <summary>The same, for the shop item -- the ability's name with the GA_ dropped.</summary>
    public const string ItemPrefix = "Modification_";

    /// <summary>What a rarity tag looks like inside the item's ItemTags.</summary>
    public const string RarityTag = "ItemType.Rarity.";

    /// <summary>The scalar that decides what the modification does.</summary>
    public const string StatProperty = "GameplayEffectModifer";

    /// <summary>Where the number shown to the player is cached, when it is cached at all.</summary>
    public const string ShownProperty = "Effect UI Data.EffectValue";

    /// <summary>
    /// The tag the game puts on a value that is a FRACTION rather than a count.
    ///
    /// Not inferred from the number being under one. The Rapid Cycler's 0.06 is six percent and
    /// the Balancing Stabilizer's 1 is one point of Defense, and the difference is stated in the
    /// data -- <c>br.UI.ValueType.Percentage</c> against <c>br.UI.ValueType.Scalar</c> -- so it is
    /// read rather than guessed. An editor that showed "0.06" with no hint invites somebody to
    /// type 15 meaning fifteen percent and ship a 1500% chance.
    /// </summary>
    private const string PercentTag = "Percentage";

    /// <summary>
    /// One editable number on a modification: where it lives, what it reads, and how wide it is.
    ///
    /// <paramref name="Path"/> is a dotted property path relative to the class default object,
    /// which is exactly what an override's number change takes as its <c>field</c>.
    /// </summary>
    public sealed record Number(string Path, string Label, double Value);

    /// <summary>
    /// The half of a modification the BLACK MARKET sells: what it is called, what it costs, and
    /// how rare it is.
    ///
    /// A separate asset from the ability, in a separate folder, and the two are joined by the
    /// item's <c>ModificationAbilityClass</c>. That split is why a modification takes two
    /// overrides to change completely -- the ability decides what it DOES, this decides everything
    /// a player sees before they buy it.
    ///
    /// <paramref name="UiExport"/> is named separately because the text does not live on the item.
    /// It lives on a <c>BitReactorUIDataFragment</c> the item lists as a fragment, which is its own
    /// export in the same package, and an edit addressed at the item finds no DisplayName at all.
    /// </summary>
    public sealed record Shop(
        string Package,
        string Export,
        string UiExport,
        string Name,
        string Description,
        string Icon,
        int Buy,
        int Sell,
        string Rarity,
        IReadOnlyList<string> Tags);

    /// <summary>One modification: what it is called, what it does, and the numbers behind that.</summary>
    public sealed record Entry(
        string Package,
        string Export,
        string Asset,
        string Slot,
        string Style,
        int Tier,
        string Name,
        string Does,
        string Icon,
        Shop? Shop,
        Number? Stat,
        Number? Copy,
        Number? Shown,
        bool Percent,
        IReadOnlyList<Number> Others)
    {
        /// <summary>
        /// This tier does not carry the stat at all -- it inherits one from the class it derives
        /// from, and four of the sixty-four do.
        ///
        /// The stabilizers are why: Balancing T2 and T3 raise only their CAP and leave the bonus
        /// per attack alone, so the property is identical to the parent's and a cooked package
        /// does not write a property that has not changed. Nothing is wrong with them, and an
        /// editor that showed a blank or a zero would be describing a modification that works.
        ///
        /// Changing one means ADDING the property rather than writing over it -- a larger edit,
        /// and one the author should be told about rather than have happen silently.
        /// </summary>
        public bool Inherited => Stat is null;

        /// <summary>
        /// The asset's second copy of the stat says something different from the stat.
        ///
        /// FOUR of the sixty-four do this (measured on the game, 2026-11-02), and it is the game's
        /// own data rather than a fault here: Advanced Tracking Scope T2 does 2 and carries 4,
        /// Hunter's Tracking Scope T3 does 3 and carries 6, Stalker Scope T4 does 4 and carries 8,
        /// and IQ Ergonomic T4 does 2 and carries 3. That is also the clearest evidence that the
        /// two are independent -- so the copy is left alone, and the author is told, because a
        /// stat change that visibly does not move the other number reads as the tool having
        /// failed.
        ///
        /// NOT COUNTED, because nothing here can see it: a tier that INHERITS its stat has none to
        /// compare with, so it is never flagged even where it carries a copy (Type-K Ergonomic T3
        /// carries 3 against the 1 it inherits through the tier chain this screen assumes). Whether
        /// that is a fifth depends on the real Blueprint parent chain, which is unmeasured.
        /// </summary>
        public bool CopyDisagrees => Stat is not null && Copy is not null && Shown is null;

        /// <summary>
        /// The style with its words separated -- "PhaseAdjuster" reads as "Phase Adjuster".
        ///
        /// Used only where the modification has no name of its own to show. Every one the game
        /// ships does, but a scan that falls back to the asset name is a scan that still lists a
        /// modification added by a future patch instead of dropping it.
        /// </summary>
        public string Readable => Regex.Replace(Style, "(?<=[a-z])(?=[A-Z])", " ");

        /// <summary>What the list shows: the game's own name, or the asset's if it has none.</summary>
        public string Title => Name.Length > 0 ? Name : $"{Readable} T{Tier}";
    }

    /// <summary>
    /// Every modification in the game, ordered the way the Armoury groups them -- by slot, then
    /// by style, then by tier.
    ///
    /// <c>GA_Modification_Base</c> is left out: it is the class the others derive from, carries no
    /// stat of its own and is not a thing a player can fit.
    /// </summary>
    public static List<Entry> All(IFileProvider provider)
    {
        // ONE PASS for both halves. The shop items live in a different folder under a different
        // naming convention, so they are collected by asset NAME here rather than reconstructed
        // from the ability's path -- a path built by string surgery breaks the day a patch moves a
        // folder, whereas a missing entry in this map simply reads as "no shop half".
        var abilities = new List<(string Package, string Asset)>();
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in provider.Files.Keys)
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = key.LastIndexOf('/');
            if (slash < 0) continue;

            var asset = key[(slash + 1)..^".uasset".Length];
            var isAbility = asset.StartsWith(Prefix, StringComparison.Ordinal);
            var isItem = !isAbility && asset.StartsWith(ItemPrefix, StringComparison.Ordinal);
            if (!isAbility && !isItem) continue;
            if (asset.Equals(Prefix + "Base", StringComparison.Ordinal)) continue;

            var pkg = PackageId.PackageNameFor(key, ModBuild.ProjectName);
            if (pkg is null) continue;

            if (isAbility) abilities.Add((pkg, asset));
            else items[asset] = pkg;
        }

        var found = new List<Entry>();
        foreach (var (pkg, asset) in abilities)
        {
            // The item is the ability's name with the GA_ dropped -- guessed, then CHECKED
            // against the item's own reference back. See ShopFor.
            var shop = items.TryGetValue(ItemPrefix + asset[Prefix.Length..], out var itemPkg)
                ? ShopFor(provider, itemPkg, pkg) : null;
            if (Read(provider, pkg, asset, shop) is { } entry) found.Add(entry);
        }

        var ordered = found
            .OrderBy(e => SlotOrder(e.Slot))
            .ThenBy(e => e.Style, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Tier)
            .ToList();

        // PERCENTAGE IS A PROPERTY OF THE FAMILY, not of the tier, and reading it per tier gets it
        // wrong on three quarters of them. Only the tier ones carry `EffectValueTag`: a cooked
        // package writes a property that differs from the class it derives from, and the higher
        // tiers derive from the lower, so they inherit the tag and never serialise it. Left as
        // read, the Rapid Cycler's 0.06 was marked a percentage and its 0.09, 0.12 and 0.15 were
        // not -- one family described four different ways.
        //
        // The four tiers of a style share one description, one effect key and one unit, so what
        // any of them says goes for all of them.
        var families = ordered.Where(e => e.Percent)
                              .Select(e => (e.Slot, e.Style))
                              .ToHashSet();
        for (var i = 0; i < ordered.Count; i++)
            if (!ordered[i].Percent && families.Contains((ordered[i].Slot, ordered[i].Style)))
                ordered[i] = ordered[i] with { Percent = true };

        return ordered;
    }

    /// <summary>
    /// One modification, or null when its package will not open.
    ///
    /// A package that cannot be read drops out of the list rather than throwing: this scan runs
    /// for the whole game every time the section is opened, and one unreadable asset must not be
    /// able to take the other sixty-three with it.
    /// </summary>
    private static Entry? Read(IFileProvider provider, string package, string asset, Shop? shop)
    {
        try
        {
            var cdo = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(package))
                        .GetExports()
                        .FirstOrDefault(e => e.Name.StartsWith("Default__", StringComparison.Ordinal)));
            if (cdo is null) return null;

            // The slot and style out of the asset name: GA_Modification_Barrel_Condenser_T1 is
            // slot "Barrel", style "Condenser", tier 1. Read from the NAME rather than from the
            // slot tag on the inventory item, because the item is a second asset and this list
            // exists to be cheap -- and the two have never disagreed on any of the sixty-four.
            var rest = asset[Prefix.Length..];
            var tier = 0;
            var under = rest.LastIndexOf('_');
            if (under > 0 && rest[(under + 1)..] is ['T', .. var digits] &&
                int.TryParse(digits, out var n))
            { tier = n; rest = rest[..under]; }

            var cut = rest.IndexOf('_');
            var slot = cut > 0 ? rest[..cut] : rest;
            var style = cut > 0 ? rest[(cut + 1)..] : "";

            var ui = PartDetails.Unwrap(PartDetails.Value(cdo.Properties, "UIData")) as FStructFallback;
            var name = ui is null ? "" : Text(ui.Properties, "Name");
            var does = ui is null ? "" : Plain(Text(ui.Properties, "Description"));
            var icon = ui is null ? "" : IconOf(ui);

            var stat = Scalar(cdo.Properties, StatProperty, "What it does");
            var effect = PartDetails.Unwrap(PartDetails.Value(cdo.Properties, "Effect UI Data"))
                         as FStructFallback;
            var copy = effect is null
                ? null
                : Scalar(effect.Properties, "EffectValue", "Its second copy") is { } c
                    ? c with { Path = ShownProperty } : null;
            var shown = copy is not null && stat is not null && Same(copy.Value, stat.Value)
                ? copy : null;
            var percent = effect is not null &&
                          (PartDetails.TagOf(PartDetails.Value(effect.Properties, "EffectValueTag"))
                           ?? "").Contains(PercentTag, StringComparison.OrdinalIgnoreCase);

            // Every OTHER top-level number. Some modifications carry a second one -- the
            // stabilizers cap their bonus with an "Effect Value Max Defense" -- and a stat editor
            // that showed one of the two would be editing half of what the player feels.
            //
            // Top level only. The numbers nested inside the UI data structs are display copies of
            // these, and listing them as separate editable values invites somebody to change the
            // label without changing the thing.
            var others = new List<Number>();
            foreach (var tag in cdo.Properties)
            {
                var field = tag.Name.Text;
                if (field == StatProperty) continue;
                if (Value(tag.Tag?.GenericValue) is not { } v) continue;
                others.Add(new Number(field, field, v));
            }

            // THE SHOP'S NAME WINS where there is one. It is what the Black Market and the
            // fitted-modification list show. The two are the same string in everything the game
            // ships, but they are two assets, and an author who renames one has renamed the one
            // a player reads.
            if (shop is not null && shop.Name.Length > 0) name = shop.Name;

            return new Entry(package, cdo.Name, asset, slot, style, tier, name, does, icon,
                             shop, stat, copy, shown, percent, others);
        }
        catch { return null; }
    }

    /// <summary>
    /// Whether two numbers are the same value, allowing for one of them having been through a
    /// float. The display copy is a four-byte float and the stat is an eight-byte double, so
    /// anything not exactly representable comes back very slightly apart.
    /// </summary>
    public static bool Same(double a, double b) =>
        Math.Abs(a - b) <= Math.Max(1e-6, Math.Abs(b) * 1e-6);

    /// <summary>
    /// The shop half, read from the item package -- or null when it will not open, or is not this
    /// ability's item.
    ///
    /// THE PAIRING IS CHECKED RATHER THAN ASSUMED. The item names its ability outright in
    /// <c>ModificationAbilityClass</c>, so a guess made from asset names can be confirmed against
    /// the data. A mismatch would hang one modification's price, rarity and name off another's
    /// stat -- wrong in a way that looks entirely right on screen, which is the kind this codebase
    /// keeps paying for.
    /// </summary>
    private static Shop? ShopFor(IFileProvider provider, string itemPackage, string abilityPackage)
    {
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(itemPackage)).GetExports().ToArray());
            if (exports.Length == 0) return null;

            var item = exports.FirstOrDefault(
                e => e.Properties.Any(x => x.Name.Text == "ModificationAbilityClass"));
            if (item is null) return null;

            var names = PartDetails.SoftPathOf(
                PartDetails.Value(item.Properties, "ModificationAbilityClass")) ?? "";
            if (!names.StartsWith(abilityPackage + ".", StringComparison.OrdinalIgnoreCase))
                return null;

            // THE TEXT IS ON A DIFFERENT EXPORT. The item lists a BitReactorUIDataFragment among
            // its Fragments, and that fragment -- its own export in the same package -- is what
            // carries DisplayName, ShortDescription and the two images. An edit addressed at the
            // item itself finds no name at all, which is why the export is carried separately.
            var ui = exports.FirstOrDefault(
                e => e.Properties.Any(x => x.Name.Text == "DisplayName"));

            var tags = PartDetails.TagsIn(exports, "ItemTags");

            return new Shop(
                itemPackage,
                item.Name,
                ui?.Name ?? "",
                ui is null ? "" : Text(ui.Properties, "DisplayName"),
                ui is null ? "" : Text(ui.Properties, "ShortDescription"),
                ui is null ? "" : Brush(ui.Properties, "SmallImage"),
                Credits(item.Properties, "BuyCost"),
                Credits(item.Properties, "SellValue"),
                tags.FirstOrDefault(t => t.Contains(RarityTag, StringComparison.OrdinalIgnoreCase)) ?? "",
                tags);
        }
        catch { return null; }
    }

    /// <summary>
    /// What one of the price maps says, in credits.
    ///
    /// BuyCost and SellValue are TMaps from a resource asset to an amount rather than plain
    /// numbers -- the game can in principle price a thing in something other than credits. Every
    /// modification it ships has exactly one entry and that entry is credits, so the first value
    /// IS the price; if that ever stops being true this returns the first of several rather than
    /// inventing a total.
    /// </summary>
    private static int Credits(List<FPropertyTag> props, string name)
    {
        if (PartDetails.Value(props, name) is not UScriptMap map) return 0;
        foreach (var kv in map.Properties)
            if (Value(kv.Value?.GenericValue) is { } v) return (int) v;
        return 0;
    }

    /// <summary>The texture an image reference points at, through its soft brush.</summary>
    private static string Brush(List<FPropertyTag> props, string name)
    {
        if (PartDetails.Unwrap(PartDetails.Value(props, name)) is not FStructFallback image)
            return "";
        if (PartDetails.Unwrap(PartDetails.Value(image.Properties, "SoftBrush"))
            is not FStructFallback brush) return "";
        return PartDetails.SoftPathOf(PartDetails.Value(brush.Properties, "ResourceObject"))
               ?? PartDetails.SoftPathOf(PartDetails.Value(brush.Properties, "WeakResourceObject"))
               ?? "";
    }

    private static Number? Scalar(List<FPropertyTag> props, string name, string label)
    {
        var tag = props.FirstOrDefault(p => p.Name.Text == name);
        if (tag is null) return null;
        return Value(tag.Tag?.GenericValue) is { } v ? new Number(name, label, v) : null;
    }

    /// <summary>
    /// A property's value as a number, or null when it is not one.
    ///
    /// Bools are excluded on purpose even though they would convert: a checkbox rendered as a
    /// number box is a control that accepts 0.5.
    /// </summary>
    private static double? Value(object? generic) => generic switch
    {
        double d => d,
        float f => f,
        int i => i,
        _ => null,
    };

    private static string Text(List<FPropertyTag> props, string name) =>
        PartDetails.Value(props, name) switch
        {
            FText t => t.Text ?? "",
            string s => s,
            _ => "",
        };

    private static string IconOf(FStructFallback ui)
    {
        if (PartDetails.Unwrap(PartDetails.Value(ui.Properties, "Icon")) is not FStructFallback brush)
            return "";
        return PartDetails.SoftPathOf(PartDetails.Value(brush.Properties, "ResourceObject"))
               ?? PartDetails.SoftPathOf(PartDetails.Value(brush.Properties, "WeakResourceObject"))
               ?? "";
    }

    /// <summary>
    /// A description with the game's rich-text markup taken off, so it can be shown as a sentence.
    ///
    /// The strings are written for the game's own text renderer:
    ///   Increases &lt;Keyword id="UI.Keyword.CriticalHit"&gt;Critical Hit Damage&lt;/&gt; by ...
    /// Left as-is it reads as markup; stripped naively to nothing it loses the words that say what
    /// the modification affects, which is the only part worth reading. So the tags go and the text
    /// between them stays.
    ///
    /// The <c>{effectDescription_1}</c> placeholders are left in place. They are the game filling
    /// in the number at runtime, and showing that is more truthful than substituting a value here
    /// and implying this tool knows how the sentence will read.
    /// </summary>
    public static string Plain(string markup)
    {
        if (markup.Length == 0) return "";
        var stripped = Regex.Replace(markup, "<[^>]*>", "");
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Slot order as the Armoury lists them, rather than alphabetically. A player looking for the
    /// scope reaches for the third group because that is where it is in the game.
    /// </summary>
    private static int SlotOrder(string slot) => slot switch
    {
        "Barrel" => 0,
        "Cells" => 1,
        "Scope" => 2,
        "Stabilizer" => 3,
        _ => 4,
    };
}

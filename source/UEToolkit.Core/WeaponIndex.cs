using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// Every weapon in the game as a WHOLE CHAIN, so a Weapons tab can offer "duplicate this one"
/// without a person having to know that a weapon is five assets.
///
/// A weapon is not one asset and there is no asset that lists them. What the armoury enumerates is
/// the gear-kit PART; that names a gear kit, which names a weapon actor, which names a loadout,
/// which names a type part, whose tag is what selects meshes out of the palettes. Duplicating a
/// weapon means duplicating all five and re-pointing each at the previous one -- miss a link and
/// the copy quietly shares the original's, which builds, installs, and is the same weapon under a
/// new name.
///
/// THE CHAIN IS DERIVED FROM NAMES, AND THEN CHECKED. The game names all five after the same
/// `&lt;Class&gt;_&lt;Name&gt;` key -- CPD_GK_Longarm_IQA-11, GK_Longarm_IQA-11, BP_Longarm_IQA-11,
/// CD_Longarm_IQA-11, CPD_WP_Type_Longarm_IQA-11 -- which makes the chain cheap to find and
/// unreliable to trust. So every link is looked up in the file index, and a weapon missing one is
/// LISTED WITH THE REASON rather than dropped: a weapon that silently fails to appear in the tab is
/// indistinguishable from one the game does not have, and the person looking for it cannot tell
/// which happened.
/// </summary>
public static class WeaponIndex
{
    /// <summary>
    /// One weapon, with every asset needed to duplicate it.
    ///
    /// <paramref name="Missing"/> empty means the chain is complete and the weapon can be
    /// duplicated. Anything in it names a link that was expected and not found.
    ///
    /// A weapon with links missing is NOT broken. An _Enemy variant shares the player weapon's
    /// actor and loadout deliberately, and a lightsaber is not assembled from mesh palettes at
    /// all -- they simply have no chain of their own to copy. Measured: 39 weapons, 21 with a
    /// complete chain.
    /// </summary>
    public sealed record Weapon(string Key, string Class, string Name,
                                string GearKitPart, string GearKit, string Actor,
                                string Loadout, string TypePart,
                                IReadOnlyList<string> Missing)
    {
        public bool Complete => Missing.Count == 0;

        /// <summary>"IQA-11 (Longarm)", which is how a person recognises it in a list.</summary>
        public string Describe => $"{Name} ({Class})";

        /// <summary>
        /// The game HAS this weapon and cannot equip it: an actor, a loadout and a type part, and
        /// no gear kit.
        ///
        /// Eight of them ship that way -- the E-5, the RPS-6, the DC-17M in all three of its
        /// modes. They are finished weapons with their own meshes, sounds and volley profiles, and
        /// the only thing standing between them and a player is two small assets. Giving them one
        /// is <see cref="WeaponAuthor.MakePlayable"/>.
        /// </summary>
        public bool NeedsGearKit =>
            Actor.Length > 0 && Loadout.Length > 0 && TypePart.Length > 0 &&
            (GearKitPart.Length == 0 || GearKit.Length == 0);
    }

    /// <summary>The part the armoury actually enumerates. Everything else hangs off its name.</summary>
    public const string GearKitPartPrefix = "CPD_GK_";

    /// <summary>
    /// Every weapon, sorted by class then name.
    ///
    /// Read from the file index rather than the asset registry for the reason
    /// <see cref="AbilityIndex"/> is: it is the complete answer in one pass, and the links being
    /// checked are packages rather than registered parts.
    /// </summary>
    public static List<Weapon> Of(IFileProvider provider)
    {
        // Leaf name -> package name, for every asset. The chain check is five lookups per weapon,
        // so it wants a dictionary rather than a scan each time.
        //
        // THE PRISTINE GAME ONLY. The Studio mounts the game's whole folder, so every installed
        // mod is mounted too -- this tool's own output included. Reading them here would list a
        // mod's weapon as one to duplicate, and worse, would resolve a shared leaf name like
        // SK_Longarm_IQA-11_Base_01 to a mod's copy of it.
        var byLeaf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = key.LastIndexOf('/');
            if (slash < 0) continue;
            var leaf = key[(slash + 1)..^".uasset".Length];
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is { } pkg)
                byLeaf.TryAdd(leaf, pkg);
        }

        var found = new List<Weapon>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Weapon Build(string key)
        {
            // "Longarm_IQA-11" -> class Longarm, name IQA-11. A weapon whose name itself contains
            // an underscore keeps it; only the FIRST separates the class.
            var cut = key.IndexOf('_');
            var cls = cut > 0 ? key[..cut] : "";
            var name = cut > 0 ? key[(cut + 1)..] : key;

            var missing = new List<string>();
            string Link(string prefix)
            {
                if (byLeaf.TryGetValue(prefix + key, out var pkg)) return pkg;
                missing.Add(prefix + key);
                return "";
            }

            var gearKitPart = Link(GearKitPartPrefix);
            var gearKit = Link("GK_");
            var actor = Link("BP_");
            var loadout = Link("CD_");
            var typePart = Link("CPD_WP_Type_");

            return new Weapon(key, cls, name, gearKitPart, gearKit, actor, loadout, typePart, missing);
        }

        foreach (var leaf in byLeaf.Keys)
            if (leaf.StartsWith(GearKitPartPrefix, StringComparison.Ordinal) &&
                seen.Add(leaf[GearKitPartPrefix.Length..]))
                found.Add(Build(leaf[GearKitPartPrefix.Length..]));

        // A SECOND PASS, FROM THE WEAPON ACTOR -- because the first pass cannot see the weapons
        // worth finding.
        //
        // The armoury enumerates the gear-kit PART, so keying this index on it lists a weapon only
        // when it already has the very thing an author would want to give it. Eight of the game's
        // weapons ship with an actor, a loadout and a type part and NO gear kit -- finished, with
        // their own meshes and sounds, and unequippable -- and every one of them was invisible
        // here. Nothing said so: they were not filtered out with a reason, they were never added.
        //
        // The folder is taken from a weapon the first pass already found rather than written down,
        // so this cannot drift from where the game actually keeps its weapon actors, and does
        // nothing at all if the first pass found none.
        if (found.FirstOrDefault(w => w.Actor.Length > 0)?.Actor is { } anyActor &&
            anyActor.LastIndexOf('/') is var at && at > 0)
        {
            var folder = anyActor[..(at + 1)];
            foreach (var (leaf, package) in byLeaf)
            {
                if (!leaf.StartsWith("BP_", StringComparison.Ordinal)) continue;
                if (!package.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                var key = leaf["BP_".Length..];
                if (!seen.Add(key)) continue;
                found.Add(Build(key));
            }
        }

        return found.OrderBy(w => w.Class, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>
    /// One palette a weapon appears in, and the mesh it contributes there.
    ///
    /// <paramref name="Palette"/> is a CPD_WP_&lt;Slot&gt;_&lt;NN&gt;_&lt;Style&gt; part -- the
    /// slot AND the attachment style together, because the game keeps one palette per style and a
    /// player switching attachments is switching which palette is consulted.
    /// </summary>
    public sealed record Piece(string Palette, string PaletteLeaf, string Mesh, string MeshLeaf);

    /// <summary>
    /// Every palette that carries this weapon, and the mesh it puts there.
    ///
    /// THIS IS WHAT DECIDES WHETHER ATTACHMENTS SHOW. A weapon's meshes do not live on the weapon;
    /// they are entries in shared palettes keyed by its type tag, and there is one palette per
    /// attachment STYLE. So a rifle that appears in six palettes has six meshes, and a mod weapon
    /// that fills only the Base one renders nothing when the player fits a different barrel -- the
    /// attachment still gives its stats, because those live on the modification's ability and never
    /// on the mesh, but the gun does not change shape.
    ///
    /// Found by name map: a palette holding the weapon's tag also holds the path of the mesh it
    /// contributes, and that mesh is named after the weapon. Reading the option array properly
    /// would pair them exactly; the naming does it here without opening the fragments, and the
    /// build's rename check is what catches a pairing that is wrong.
    /// </summary>
    public static List<Piece> PiecesOf(IFileProvider provider, Weapon weapon, string typeTag)
    {
        var found = new List<Piece>();
        var needle = $"SK_{weapon.Class}_{weapon.Name}_";

        // Pristine, for the reason given in Of: a mod's own palette copy is not one of the game's.
        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = key.LastIndexOf('/');
            if (slash < 0) continue;
            var leaf = key[(slash + 1)..^".uasset".Length];
            if (!leaf.StartsWith("CPD_WP_", StringComparison.Ordinal)) continue;
            if (leaf.StartsWith("CPD_WP_Type_", StringComparison.Ordinal)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pkg) continue;

            var copied = AssetCopy.PristineRead(provider, pkg, out _);
            var raw = copied?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
            if (raw is null) continue;

            HashSet<string> names;
            try { names = new HashSet<string>(ZenPackage.Read(raw).Names, StringComparer.Ordinal); }
            catch { continue; }
            if (!names.Contains(typeTag)) continue;

            // The mesh this palette contributes for this weapon, by the game's own naming.
            var mesh = names.FirstOrDefault(
                n => n.Contains(needle, StringComparison.Ordinal) && n.StartsWith("/Game/", StringComparison.Ordinal));
            var meshLeaf = mesh is null ? "" : mesh[(mesh.LastIndexOf('/') + 1)..];
            found.Add(new Piece(pkg, leaf, mesh ?? "", meshLeaf));
        }

        return found.OrderBy(x => x.PaletteLeaf, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// How many weapons the Armoury will show in one class before it starts losing one.
    ///
    /// MEASURED IN GAME 2026-09-02, both directions. The Longarm list holds seven; at eight the
    /// FIRST row is damaged -- with two plain duplicates it kept its name and lost its icon, and
    /// with two heavier parts the whole row went blank. Its assets were perfect throughout, so
    /// this is something fixed-size losing its first entry rather than anything wrong with data.
    ///
    /// Seven is the bracket that was measured ON LONGARM, not a number read out of the game, and
    /// the severity at eight varied with what the extra parts contained -- so treat it as "this is
    /// where it started going wrong once", not as a promise that seven is always safe.
    ///
    /// AND THE LIST SHOWS WHAT A PLAYER HAS UNLOCKED, not what exists. The count here is of
    /// weapons that EXIST in the class, which is an upper bound on the rows.
    ///
    /// **THERE IS NO CAP. THIS NUMBER IS NOT A LIMIT AND THE WARNING NO LONGER CLAIMS ONE.**
    /// It is kept only as the count above which the Armoury's own display fault becomes likely
    /// enough to be worth mentioning.
    ///
    /// What was really happening, measured in game 2026-09-03: a row goes blank while remaining
    /// SELECTABLE, and WHICH row is blank changes when you scroll and changes again when you
    /// leave the screen and come back. That is not a cap and not lost data -- it is the Armoury's
    /// list recycling its row widgets. `WBP_WeaponTypeButton` has a `BuildOn` animation driving
    /// **RenderOpacity**, so a row reused without that animation replaying sits at zero opacity:
    /// present, hittable, invisible.
    ///
    /// The old reading -- "seven fine, eight damaged the first row" -- was two single looks at a
    /// non-deterministic fault, and the "first row" was a coincidence of an unscrolled list. Both
    /// of those were written down as measurements. See [[measurement-is-not-conclusion]].
    /// </summary>
    public const int ClassHolds = 7;

    /// <summary>
    /// The weapons in one class that the Armoury actually lists, ACROSS EVERY MOUNTED CONTAINER.
    ///
    /// Deliberately not pristine -- unlike <see cref="Of"/>, the whole point here is to count what
    /// a player will really see, which is the game's weapons plus every one already installed by
    /// any mod, including other people's.
    ///
    /// `_Enemy` variants are excluded because the Armoury does not show them: Longarm has six
    /// `CPD_GK_` parts and four rows.
    /// </summary>
    public static List<string> ListedIn(IFileProvider provider, string weaponClass)
    {
        // BY THE TAG, NOT BY THE NAME. An armoury part's class is the tag in its AllowedSlots, and
        // the two stopped agreeing the moment a duplicate could be ROUTED: a weapon still called
        // CPD_GK_Longarm_X but carrying ...Blaster.CustomLongarm is a Custom Longarm, and counting
        // it by its name would warn about a shelf it left and stay silent about the one it joined.
        //
        // Reading names by name is exactly the fault this file already carries a withdrawn claim
        // about -- counting the files that EXIST and calling it rows. It costs one package read per
        // gear-kit part, about forty, which is what PiecesOf already spends per palette.
        var want = "br.Customization.Part.Character.Specializations.Weapon.Blaster." + weaponClass;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in provider.Files.Keys)
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = key.LastIndexOf('/');
            if (slash < 0) continue;
            var leaf = key[(slash + 1)..^".uasset".Length];
            if (!leaf.StartsWith(GearKitPartPrefix, StringComparison.Ordinal)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pkg) continue;

            var raw = AssetCopy.Read(provider, pkg, out _)?.Pieces
                               .FirstOrDefault(x => x.IsPackage)?.Data;
            if (raw is null) continue;
            HashSet<string> names;
            try { names = new HashSet<string>(ZenPackage.Read(raw).Names, StringComparer.Ordinal); }
            catch { continue; }

            if (!names.Contains(want)) continue;
            // The real reason the Armoury does not list an _Enemy variant, rather than its name:
            // AuthoredOnly is the gate, and it is what hides Rex's pistols and the lightsabers too.
            if (names.Contains("br.Customization.Accepts.AuthoredOnly")) continue;
            found.Add(leaf);
        }
        return found.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The type TAG a weapon's parts are keyed by, as the game writes it.
    ///
    /// Derived rather than read, because reading it means opening the type part and this is wanted
    /// while drawing a list. It is checked against the real thing when a weapon is actually
    /// duplicated -- the rename would fail loudly if it were wrong, which is the property that
    /// makes deriving it safe here.
    /// </summary>
    public static string TypeTagFor(Weapon weapon, string family = "2HRifle") =>
        $"br.Customization.Part.Weapon.Type.{family}.{weapon.Class}.{weapon.Name}";
}

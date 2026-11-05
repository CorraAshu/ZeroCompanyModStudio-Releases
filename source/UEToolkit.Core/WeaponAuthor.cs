using System.Text.Json.Nodes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;

namespace UEToolkit.Core;

/// <summary>
/// "Duplicate this weapon" turned into a mod project.
///
/// A weapon is five assets plus a mesh, a mesh palette and a gameplay tag, and getting one wrong
/// gives a copy that quietly shares the original's parts. Doing it by hand took an afternoon and
/// three wrong turns; nobody should have to repeat that to rename a rifle.
///
/// THIS EMITS THE ORDINARY PRIMITIVES -- `tags`, `meshes`, `parts`, `copies` -- rather than adding
/// a build path of its own. So the generated project is one a person can open, read, and edit by
/// hand afterwards; the build treats it as any other mod; and none of this can regress a helmet.
/// The DLT-20A, which is in the game and correct, is the reference: what this writes has to build
/// to the same thing.
///
/// The cost of that choice: the project stores the EXPANDED form, so the high-level intent
/// ("this is a copy of the IQA-11") survives only in the template fields it leaves behind. Worth it
/// for now -- a new build path is a new way for a working mod to break.
/// </summary>
public static class WeaponAuthor
{
    /// <summary>What a person actually chooses. Everything else is derived or defaulted.</summary>
    public sealed record Spec(
        WeaponIndex.Weapon Template,
        string Id,
        ModProject? Into = null,
        string Display = "",
        string MeshFile = "",
        float MeshScale = 0,
        string MeshBasis = "",
        (float X, float Y, float Z) MeshOffset = default,
        IReadOnlyDictionary<string, string>? Textures = null,
        IReadOnlyList<string>? HideMeshes = null,
        string Tag = "",
        bool CustomClass = true);

    // ------------------------------------------------------------- which Armoury class it lands in

    /// <summary>
    /// The gameplay tag that puts a weapon in a class, as the game writes it on an armoury part's
    /// AllowedSlots.
    ///
    /// This is the whole routing mechanism and it is one string. Proved in game twice: IconProbe's
    /// Probe B moved a DC-17M into the Longarm list by renaming nothing else, and the custom
    /// classes were reached the same way.
    /// </summary>
    public static string ClassTag(string weaponClass) =>
        "br.Customization.Part.Character.Specializations.Weapon.Blaster." + weaponClass;

    /// <summary>
    /// The four game classes the base mod mirrors, by the name that appears in an asset.
    ///
    /// Only these four. The others -- DC-17M, the lightsabers, Rex's pistols, Pacifist -- are
    /// gated (AuthoredOnly, a character name, or an unlock) and a mirror of one would be a class
    /// nobody can pick. A weapon duplicated from one of those keeps the donor's class, which is
    /// the honest fallback: it competes for the game's shelf, and the build says so.
    /// </summary>
    public static readonly string[] MirroredClasses = { "Longarm", "Rifle", "Pistol", "Repeater" };

    /// <summary>
    /// Whether a duplicate of this weapon can be routed into a class of its own.
    ///
    /// Deliberately NOT "is the tag well formed" -- a weapon whose class has no mirror is a real
    /// and common case, and offering the switch for it would produce a weapon in a class that does
    /// not exist, which is indistinguishable from a mod that failed to install.
    /// </summary>
    public static bool HasCustomClass(string weaponClass) =>
        MirroredClasses.Contains(weaponClass, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The custom class a duplicate of this one belongs in, or null when there is no mirror.
    ///
    /// The name has to match what CustomWeaponClasses ships -- CPD_WeaponSpec_Custom_&lt;Class&gt;
    /// carrying ...Blaster.Custom&lt;Class&gt; -- because a tag naming a class nothing defines is
    /// silently invisible rather than an error. See [[zero-company-custom-weapon-class]].
    /// </summary>
    public static string? CustomClassTag(string weaponClass) =>
        HasCustomClass(weaponClass) ? ClassTag("Custom" + weaponClass) : null;

    /// <summary>The base mod that defines the custom classes. A weapon routed into one needs it.</summary>
    public const string BaseModName = "CustomWeaponClasses";

    /// <summary>The property a loadout keeps its slot-to-part map under.</summary>
    public const string SlotMapProperty = "SlotOverrides";

    /// <summary>Where a mod's own assets live when the project does not say.</summary>
    public const string ModFolder = "/Game/Game/Customizations/Mods/";

    /// <summary>
    /// The package folder a mod's own assets are built into, as a prefix ending in "/".
    ///
    /// EVERY re-point this class writes has to agree with where the build actually puts the
    /// assets, and the build puts them under the project's own folder -- which the Studio gives a
    /// mod of its own, "Customizations/Mods/&lt;Name&gt;". This was the constant above, so a weapon
    /// duplicated into any such mod had a loadout naming assets one directory up from the ones
    /// beside it: the type part, the palette and the mesh were all built, all correct, and all
    /// unreachable. In the game that is a weapon with no model at all.
    /// </summary>
    public static string FolderFor(ModProject? mod) =>
        mod is null ? ModFolder
                    : "/Game/Game/" + mod.Folder.Replace('\\', '/').Trim('/') + "/";

    // ------------------------------------------------------------------ the model, which is five parts

    /// <summary>
    /// One mesh palette a weapon's loadout fills a slot from.
    ///
    /// <paramref name="Names"/> is the palette's whole name map, kept because every decision about
    /// a copy of it -- which mesh to re-point, which piece to hide -- is "is this name in there",
    /// and reading the package again for each question is the expensive way to ask.
    /// </summary>
    public sealed record SlotPalette(string Slot, string Package, string Leaf,
                                     IReadOnlySet<string> Names);

    /// <summary>
    /// Every mesh palette a weapon is actually assembled from, read from its LOADOUT.
    ///
    /// A WEAPON IS NOT ONE PALETTE. Its loadout names one part per slot -- Base, Barrel, Stock,
    /// Scope, Cell -- and each of those holds this weapon's mesh for that slot, keyed by the type
    /// tag. Copying only the Base one gave a duplicate that is a floating receiver with no barrel,
    /// stock, scope or cell: those four palettes have no entry under the NEW tag, so the game finds
    /// nothing to put there. It builds, it installs, and half the gun is missing.
    ///
    /// WHICH SLOTS CARRY A MESH IS NOT A LIST IN THIS FILE. A slot counts when the part it names
    /// actually carries the donor's type tag -- the same question the game asks. So the paint slots
    /// drop out on their own, and so does a slot this particular weapon has no entry in, without
    /// anything here having to know their names or how many there are.
    ///
    /// Null with a reason only when the loadout cannot be read at all. An empty list is a real
    /// answer and the caller decides what it means.
    /// </summary>
    public static List<SlotPalette>? PalettesOf(IFileProvider provider, string loadout,
                                                string typeTag, string? exceptPackage,
                                                out string? why)
    {
        why = null;
        UObject[] exports;
        try
        {
            exports = AssetLoader.Read(() =>
                provider.LoadPackage(AssetLoader.PackagePath(loadout)).GetExports().ToArray());
        }
        catch (Exception ex)
        {
            why = $"cannot read {Leaf(loadout)}, so its parts cannot be found -- {ex.Message}";
            return null;
        }

        var map = exports.Select(e => PartDetails.Value(e.Properties, SlotMapProperty))
                         .OfType<UScriptMap>().FirstOrDefault();
        if (map is null)
        {
            why = $"{Leaf(loadout)} has no {SlotMapProperty} map, so which parts make up this " +
                  "weapon cannot be read";
            return null;
        }

        var found = new List<SlotPalette>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in map.Properties)
        {
            if (PartDetails.SoftPathOf(value?.GenericValue) is not { } objectPath) continue;
            var package = AssetLoader.PackagePath(objectPath);
            if (exceptPackage is not null &&
                package.Equals(exceptPackage, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(package)) continue;

            // The palette is what it CONTAINS, not what it is called. A paint slot's part does not
            // carry the weapon's type tag and neither does a slot this weapon has no piece in.
            var names = NamesOf(provider, package);
            if (names is null || !names.Contains(typeTag)) continue;

            found.Add(new SlotPalette(PartDetails.TagOf(key?.GenericValue) ?? "", package, Leaf(package), names));
        }
        return found;
    }

    /// <summary>
    /// What a copy of one palette is called: <c>CPD_WP_Base_01_Default</c> copied for "Blaster"
    /// becomes <c>CPD_WP_Base_Blaster</c>.
    ///
    /// Ending in the weapon's id is not cosmetic -- <see cref="Rename"/> moves a duplicate's whole
    /// chain by that suffix, so a palette copy named any other way would be left behind by a
    /// rename, pointing at assets that no longer exist under those names.
    /// </summary>
    public static string PaletteCopyId(string paletteLeaf, string id)
    {
        const string prefix = "CPD_WP_";
        var kind = paletteLeaf.StartsWith(prefix, StringComparison.Ordinal)
            ? paletteLeaf[prefix.Length..] : paletteLeaf;
        if (kind.IndexOf('_') is var cut && cut > 0) kind = kind[..cut];
        kind = AssetNameOf(kind);
        return kind.Length == 0 ? $"{prefix}{id}" : $"{prefix}{kind}_{id}";
    }

    /// <summary>
    /// The project JSON for a duplicated weapon, or null with a reason.
    ///
    /// Nothing here is written hopefully. Every name it emits is one the build will check against a
    /// real name map, and a rename that does not land is reported by the build rather than
    /// producing a copy that silently points at the original -- which is why deriving names by
    /// convention is safe at this layer and would not be at the layer that writes bytes.
    /// </summary>
    public static JsonObject? Expand(IFileProvider provider, Spec spec, out string? why)
    {
        why = null;
        // Where the build will actually put this weapon's assets. Everything below re-points at
        // these names, so a folder that disagrees with the build is a chain that resolves to
        // nothing -- see FolderFor.
        var modFolder = FolderFor(spec.Into);

        if (string.IsNullOrWhiteSpace(spec.Id))
        { why = "the new weapon needs a name"; return null; }
        if (spec.Id.Any(c => c is '/' or '\\' or '.' or ' '))
        { why = $"'{spec.Id}' cannot name an asset -- no dots, slashes or spaces"; return null; }
        if (!spec.Template.Complete)
        { why = $"{spec.Template.Describe} has no chain of its own to copy -- it is missing " +
                $"{string.Join(", ", spec.Template.Missing)}. Pick a weapon the armoury lists.";
          return null; }

        var t = spec.Template;
        var id = spec.Id;
        var display = string.IsNullOrWhiteSpace(spec.Display) ? id : spec.Display;

        // ITS OWN TAG, not one borrowed. Borrowing a live weapon's drags that weapon's whole parts
        // list onto the model -- which is what a stray ACP barrel turned out to be -- and borrowing
        // a cut one runs out after about eight. See zero-company-invent-a-gameplay-tag.
        // FOUND IN THE TYPE PART, not guessed at. The family in the middle of the tag
        // ("2HRifle") does not follow from the class name, and a tag guessed wrong produces a
        // rename that matches nothing -- which the build reports, but only after a person has
        // waited for it. Read the part's own names and take the one that is actually there.
        var typeNames = NamesOf(provider, t.TypePart);
        if (typeNames is null)
        { why = $"cannot read {Leaf(t.TypePart)}, so its type tag cannot be found"; return null; }

        var oldTag = typeNames.FirstOrDefault(
            n => n.StartsWith(TypeTagPrefix, StringComparison.Ordinal) &&
                 n.EndsWith("." + t.Name, StringComparison.Ordinal));
        if (oldTag is null)
        {
            var seen = typeNames
                .Where(n => n.StartsWith(TypeTagPrefix, StringComparison.Ordinal))
                .Take(4).ToList();
            why = $"{Leaf(t.TypePart)} carries no type tag ending in '{t.Name}'" +
                  (seen.Count > 0 ? $" -- it has {string.Join(", ", seen)}" : " at all");
            return null;
        }

        // A DUPLICATE KEEPS THE DONOR'S TYPE TAG BY DEFAULT, and that default is the whole
        // difference between a weapon the game's attachments fit and one they do not.
        //
        // The tag is the key a weapon's meshes are stored under in shared palettes, and fitting one
        // of the game's modifications points that slot at the GAME's palette -- so a copy with a
        // tag of its own is in none of them and the piece VANISHES the moment a modification is
        // fitted. Confirmed in game both ways on 2026-09-02: minted, a fitted barrel, stock or cell
        // disappeared; keeping the donor's, a fitted scope shows the modification's scope.
        //
        // It used to mint one, which is right for a weapon whose model is one imported piece --
        // then nothing of the game's should answer for it -- and wrong for everything else.
        // SetDonorTag switches either way afterwards, so this is a default and not a decision.
        var newTag = string.IsNullOrWhiteSpace(spec.Tag) ? oldTag : spec.Tag;
        var ownTag = !newTag.Equals(oldTag, StringComparison.Ordinal);

        // The mesh this weapon's tag selects out of the shared palette. Its LEAF follows the
        // game's naming, but its folder does not follow from the class -- a Rifle lives under
        // Blaster_Rifle and a Pistol under one of two pistol folders -- so the leaf is looked up
        // rather than a path constructed around a guess.
        var meshLeaf = $"SK_{t.Class}_{t.Name}_Base_01";
        var sourceMesh = PackageOfLeaf(provider, meshLeaf);
        // ALWAYS emit the mesh entry when the source can be found, even with nothing to put in it
        // yet. The editor attaches an FBX and textures TO an entry, so a duplicate made from the
        // tab with no entry has nowhere to put them -- which is what happened: the Weapons tab
        // duplicated a rifle and the Mod tab showed no mesh section at all. An empty entry is a
        // copy of the donor's mesh, which the list marks in red as "still the game's model".
        var wantsMesh = sourceMesh is not null;
        if (sourceMesh is null &&
            (!string.IsNullOrWhiteSpace(spec.MeshFile) || spec.Textures is { Count: > 0 }))
        { why = $"cannot find {meshLeaf}, so there is nowhere to put your model for {t.Describe}"; return null; }

        // EVERY palette this weapon is assembled from, read from its loadout rather than assumed.
        // See PalettesOf: a weapon is five parts and copying one of them is a floating receiver.
        var palettes = PalettesOf(provider, t.Loadout, oldTag, t.TypePart, out var palettesWhy);
        if (palettes is null) { why = palettesWhy; return null; }
        if (palettes.Count == 0)
        {
            why = $"{Leaf(t.Loadout)} names no part carrying {oldTag}, so {t.Describe} has no mesh " +
                  "palette to copy -- a duplicate of it would have no model at all";
            return null;
        }

        var root = new JsonObject
        {
            ["name"] = id,
            ["displayName"] = display,
            ["author"] = "",
            ["version"] = "1.0",
            ["description"] = $"{display}, duplicated from the {t.Describe}. Its mesh, its textures " +
                              "and its mesh palettes are the mod's own copies; nothing of the game's " +
                              "is written over." +
                              (ownTag ? "" : " It answers to the same weapon type, so the game's " +
                                             "barrel, scope, stock and cell modifications fit it."),
        };
        // Only a tag this mod INVENTS is declared. Declaring one of the game's would put a tag the
        // game already defines into this mod's Config/Tags, which is a conflict, not a definition.
        if (ownTag) root["tags"] = new JsonArray(newTag);

        // 1. The mesh: the game's, copied into the mod, with the author's geometry inside the copy.
        if (wantsMesh)
        {
            var mesh = new JsonObject { ["source"] = sourceMesh! };
            if (!string.IsNullOrWhiteSpace(spec.MeshFile)) mesh["import"] = spec.MeshFile;
            if (spec.MeshScale > 0) mesh["meshScale"] = spec.MeshScale;
            if (!string.IsNullOrWhiteSpace(spec.MeshBasis)) mesh["meshBasis"] = spec.MeshBasis;
            if (spec.MeshOffset != default)
                mesh["meshOffset"] = new JsonArray(spec.MeshOffset.X, spec.MeshOffset.Y, spec.MeshOffset.Z);
            if (spec.Textures is { Count: > 0 })
            {
                var tex = new JsonObject();
                foreach (var (slot, file) in spec.Textures) tex[slot] = file;
                mesh["textures"] = tex;
            }
            root["meshes"] = new JsonArray(mesh);
        }

        // 2. The TYPE PART, carrying the new tag. This is what the palette entry matches on.
        root["parts"] = new JsonArray(new JsonObject
        {
            ["id"] = $"CPD_WP_Type_{t.Class}_{id}",
            ["slot"] = "WeaponType",
            ["template"] = t.TypePart,
            ["display"] = display,
            ["renameNames"] = ownTag ? new JsonObject { [oldTag] = newTag } : new JsonObject(),
        });

        // 3. THE MESH PALETTES -- one per slot, not one. Each is copied so its entry for this
        //    weapon can be re-keyed to the new tag; the game's own palettes are left alone, so the
        //    donor weapon still works. Copying only the Base one gave a floating receiver.
        var loadoutRenames = new JsonObject
        {
            [t.TypePart] = $"{modFolder}CPD_WP_Type_{t.Class}_{id}",
            [$"CPD_WP_Type_{t.Key}"] = $"CPD_WP_Type_{t.Class}_{id}",
        };
        var copies = new JsonArray();
        foreach (var palette in palettes)
        {
            var copyId = PaletteCopyId(palette.Leaf, id);
            // Nothing to re-key when the copy answers to the donor's tag: the entry already there
            // IS its entry, and the palette copy exists so a piece's MESH can be replaced.
            var renames = new JsonObject();
            if (ownTag) renames[oldTag] = newTag;

            // The author's own geometry goes wherever the donor's mesh actually is, rather than
            // into whichever palette is called "Base" -- one fewer name this has to be right about.
            if (wantsMesh && palette.Names.Contains(sourceMesh!))
                renames[sourceMesh!] = modFolder + meshLeaf;

            // Pieces of the donor the new weapon does not want. Renamed to a dead name inside the
            // mod's OWN copy, so nothing of the game's changes and the donor keeps its parts.
            foreach (var hide in spec.HideMeshes ?? [])
            {
                if (palette.Names.Contains(hide)) renames[hide] = hide + "_HiddenByMod";
                if (PackageOfLeaf(provider, hide) is { } hidePath && palette.Names.Contains(hidePath))
                    renames[hidePath] = hidePath + "_HiddenByMod";
            }

            copies.Add(new JsonObject
            {
                ["id"] = copyId,
                ["template"] = palette.Package,
                ["newGuid"] = true,
                ["renameNames"] = renames,
            });

            // A soft object path is two names, so the loadout names the palette twice.
            loadoutRenames[palette.Package] = modFolder + copyId;
            loadoutRenames[palette.Leaf] = copyId;
        }

        // 4. The chain, each link re-pointed at the previous copy. A link left pointing at the
        //    original is a copy that IS the original, under a different name.
        copies.Add(new JsonObject
        {
            ["id"] = $"CD_{t.Class}_{id}",
            ["template"] = t.Loadout,
            ["newGuid"] = true,
            ["renameNames"] = loadoutRenames,
        });
        copies.Add(new JsonObject
        {
            ["id"] = $"BP_{t.Class}_{id}",
            ["template"] = t.Actor,
            ["newGuid"] = true,
            // The actor names its loadout as a HARD import, which no rename moves.
            ["renameNames"] = new JsonObject { [$"CD_{t.Key}"] = $"CD_{t.Class}_{id}" },
            ["repointImports"] = new JsonObject { [t.Loadout] = $"CD_{t.Class}_{id}" },
        });
        // THE SIXTH PLACE A WEAPON'S NAME LIVES, and the one at the top of the screen: the
        // Armoury's "MODEL:" header draws the GEAR KIT's name, not the armoury part's. It sits in
        // GearKitInfo.DisplayName, one struct down, which is why every duplicate read "IQA-11"
        // while its five other assets read the author's name.
        copies.Add(new JsonObject
        {
            ["id"] = $"GK_{t.Class}_{id}",
            ["template"] = t.GearKit,
            ["newGuid"] = true,
            ["display"] = display,
            ["renameNames"] = new JsonObject { [$"BP_{t.Key}"] = $"BP_{t.Class}_{id}" },
            ["repointImports"] = new JsonObject { [t.Actor] = $"BP_{t.Class}_{id}" },
        });
        // WHICH CLASS IT LANDS IN. The armoury part's AllowedSlots carries the class tag, and
        // renaming that one string moves the weapon between classes -- so a duplicate goes into
        // the mirrored CUSTOM class rather than competing for the game's own per-class shelf,
        // where an eighth weapon was measured damaging the first row.
        //
        // It is a rename on the copy like any other, which means the registry's copy of the tags
        // moves with it: ModBuild applies a copy's renames to both halves, and a part whose
        // package and registry entry disagree about its class is in one class to the menu and
        // another to the game.
        var gearKitRenames = new JsonObject
        {
            [t.GearKit] = $"{modFolder}GK_{t.Class}_{id}",
            [$"GK_{t.Key}"] = $"GK_{t.Class}_{id}",
        };
        if (spec.CustomClass && CustomClassTag(t.Class) is { } custom)
            gearKitRenames[ClassTag(t.Class)] = custom;

        copies.Add(new JsonObject
        {
            ["id"] = $"CPD_GK_{t.Class}_{id}",
            ["template"] = t.GearKitPart,
            ["newGuid"] = true,
            ["display"] = display,
            ["renameNames"] = gearKitRenames,
        });
        root["copies"] = copies;
        return root;
    }

    /// <summary>
    /// Put the armoury name on the GEAR KIT copy too, where a project does not have it yet.
    ///
    /// Every weapon duplicated before the gear kit was known to hold a name of its own has five
    /// assets saying what the author called it and a sixth saying "IQA-11" -- and the sixth is the
    /// one the Armoury prints across the top of the screen. Nothing about those projects is wrong
    /// enough to rebuild; they are one field short.
    ///
    /// Only fills a BLANK. An author who deliberately gave the gear kit a different name keeps it,
    /// and running this twice does nothing the second time.
    /// </summary>
    /// <summary>
    /// The most specific weapon-type tag a type part carries, or null.
    ///
    /// A type part holds two: the family it belongs to ("...2HRifle.Rifle") and the weapon itself
    /// ("...2HRifle.Rifle.E-5"). The longer one is the weapon's, and it is the tag every mesh
    /// palette in the game is keyed on.
    /// </summary>
    public static string? TypeTagOf(IFileProvider provider, string typePart)
    {
        if (string.IsNullOrWhiteSpace(typePart)) return null;
        try
        {
            var exports = AssetLoader.Read(() =>
                provider.LoadPackage(ModBuild.ContainerPathFor(typePart)).GetExports().ToList());
            return PartDetails.TagsIn(exports, "GameplayTags")
                .Where(t => t.StartsWith("br.Customization.Part.Weapon.Type.", StringComparison.Ordinal))
                .OrderByDescending(t => t.Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// GIVE A WEAPON THE GAME SHIPS THE GEAR KIT IT NEVER HAD, which is the whole of what stands
    /// between four finished weapons and a player.
    ///
    /// The E-5, the RSKF-44, the Scattergun and the Snubble-Special each have an actor, a loadout
    /// and a type part -- their own meshes, their own firing sounds, their own volley profiles --
    /// and no gear kit, so nothing can equip them. Measured: three references to BP_Rifle_E-5 in
    /// the whole game, two of them cinematics.
    ///
    /// Two assets, both copied from a weapon of the SAME CLASS that has them: the gear kit, whose
    /// hard ActorClass import is re-pointed at this weapon's actor, and the armoury part that
    /// names it. Nothing else is copied and nothing of the game's is written over -- the meshes,
    /// the loadout and the type part are already there and are simply reached.
    ///
    /// Same class, and refused otherwise. A gear kit carries an ability set and item tags that say
    /// what KIND of thing this is; a utility item handed a rifle's gear kit would equip and behave
    /// like a rifle. Only four classes have a donor at all, and they are exactly the four with a
    /// custom class shelf -- so a weapon this cannot serve is one the Armoury has nowhere to put.
    /// </summary>
    public static JsonObject? MakePlayable(IFileProvider provider, WeaponIndex.Weapon weapon,
                                           WeaponIndex.Weapon donor, ModProject? mod,
                                           bool customClass, out string? why)
    {
        why = null;
        if (!weapon.NeedsGearKit)
        { why = $"{weapon.Describe} already has a gear kit, or is missing more than one"; return null; }
        if (!donor.Complete)
        { why = $"{donor.Describe} has no complete chain to copy a gear kit from"; return null; }
        if (!string.Equals(donor.Class, weapon.Class, StringComparison.OrdinalIgnoreCase))
        { why = $"{donor.Describe} is a {donor.Class} and this is a {weapon.Class}"; return null; }

        var modFolder = FolderFor(mod);
        var gearKitId = $"GK_{weapon.Key}";
        var partId = $"{WeaponIndex.GearKitPartPrefix}{weapon.Key}";

        // The gear kit, pointed at THIS weapon's actor. A hard import, so it moves through
        // repointImports and takes the actor's stored export hashes with it -- a rename would
        // leave it loading the donor's weapon while looking edited.
        var copies = new JsonArray
        {
            new JsonObject
            {
                ["id"] = gearKitId,
                ["template"] = donor.GearKit,
                ["newGuid"] = true,
                ["display"] = weapon.Name,
                ["repointImports"] = new JsonObject { [donor.Actor] = weapon.Actor },
            },
        };

        var renames = new JsonObject
        {
            [donor.GearKit] = $"{modFolder}{gearKitId}",
            [$"GK_{donor.Key}"] = gearKitId,
        };
        // WHICH SHELF IT LANDS ON. Into the mirrored custom class rather than the game's own, for
        // the reason a duplicate goes there: an eighth weapon on a game shelf was measured
        // damaging the first row.
        if (customClass && CustomClassTag(weapon.Class) is { } custom)
            renames[ClassTag(weapon.Class)] = custom;
        // The armoury part names a weapon TYPE as well, and the donor's says the donor. Left alone
        // when either tag cannot be read, because a half-applied rename is worse than none.
        if (TypeTagOf(provider, donor.TypePart) is { } fromTag &&
            TypeTagOf(provider, weapon.TypePart) is { } toTag &&
            !string.Equals(fromTag, toTag, StringComparison.Ordinal))
            renames[fromTag] = toTag;

        copies.Add(new JsonObject
        {
            ["id"] = partId,
            ["template"] = donor.GearKitPart,
            ["newGuid"] = true,
            ["display"] = weapon.Name,
            ["renameNames"] = renames,
        });

        return new JsonObject { ["copies"] = copies };
    }

    /// <summary>
    /// The weapon whose gear kit is the right one to copy for <paramref name="weapon"/>: one of
    /// the same class with a complete chain. Null when its class has none.
    /// </summary>
    public static WeaponIndex.Weapon? DonorFor(IReadOnlyList<WeaponIndex.Weapon> all,
                                               WeaponIndex.Weapon weapon) =>
        all.Where(w => w.Complete &&
                       string.Equals(w.Class, weapon.Class, StringComparison.OrdinalIgnoreCase) &&
                       !w.Key.EndsWith("_Enemy", StringComparison.OrdinalIgnoreCase))
           .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
           .FirstOrDefault();

    public static int CarryNameToGearKit(ModProject mod)
    {
        var done = 0;
        foreach (var armoury in mod.Copies)
        {
            if (string.IsNullOrWhiteSpace(armoury.Display)) continue;
            if (IdentityOf(armoury.Id) is not { } who) continue;

            var kit = mod.Copies.FirstOrDefault(
                c => c.Id.Equals($"GK_{who.Class}_{who.Id}", StringComparison.Ordinal));
            if (kit is null || !string.IsNullOrWhiteSpace(kit.Display)) continue;

            kit.Display = armoury.Display;
            done++;
        }
        return done;
    }

    // ------------------------------------------------------------------ renaming a duplicate

    /// <summary>
    /// The identity a duplicated weapon's names share: for <c>CPD_GK_&lt;Class&gt;_&lt;Id&gt;</c>,
    /// the class and the id. Null for a copy that is not an armoury part of that shape.
    /// </summary>
    public static (string Class, string Id)? IdentityOf(string gearKitPartId)
    {
        if (!gearKitPartId.StartsWith(WeaponIndex.GearKitPartPrefix, StringComparison.Ordinal)) return null;
        var rest = gearKitPartId[WeaponIndex.GearKitPartPrefix.Length..];
        var us = rest.IndexOf('_');
        if (us <= 0 || us == rest.Length - 1) return null;
        return (rest[..us], rest[(us + 1)..]);
    }

    /// <summary>
    /// What an asset may be called, from what a person typed. Letters, digits, hyphens and
    /// underscores survive; everything else goes, so "Icon Test" names files IconTest.
    /// </summary>
    public static string AssetNameOf(string typed) =>
        new(typed.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    /// <summary>One change a rename makes, said before it is made.</summary>
    public sealed record Change(string What, string From, string To);

    /// <summary>
    /// Whether a name is one of THIS weapon's own -- an asset it is made of, its tag, or its icon
    /// tag -- rather than a name of the game's it merely points at.
    ///
    /// The rule for both the things that need it: <see cref="Rename"/> moves exactly these, and
    /// the editor locks exactly these, because a chain is only correct whole. Editing one by hand
    /// is how a weapon ended up with a renamed type part and five copies still pointing at the old
    /// name -- which builds, installs, and is indistinguishable from the rename doing nothing.
    ///
    /// An icon tag spells the id without punctuation and straight after the class, so it is
    /// matched by its alphanumeric form and without a boundary; everything else at one.
    /// </summary>
    public static bool NamesTheWeapon(string s, string id)
    {
        if (s.StartsWith("ImageBank.", StringComparison.OrdinalIgnoreCase))
        {
            var alnum = new string(id.Where(char.IsLetterOrDigit).ToArray());
            return alnum.Length > 0 && s.EndsWith(alnum, StringComparison.Ordinal);
        }
        if (id.Length == 0 || !s.EndsWith(id, StringComparison.Ordinal)) return false;
        var at = s.Length - id.Length;
        return at == 0 || s[at - 1] is '_' or '.' or '/';
    }

    /// <summary>
    /// Rename a duplicated weapon's assets as a set, or say why not.
    ///
    /// Everything a duplicate is made of ENDS in its id -- the six asset names, the tag, the icon
    /// tag, and every re-point between them, because <see cref="Expand"/> derived them all from
    /// one name. So a rename is "a name ending in the old id gets the new one", applied to every
    /// id, tag and re-point VALUE in the project (never a key: keys are the donor's names). The
    /// plan is built first and applied only whole, because a chain half-renamed is a weapon that
    /// quietly points at the one it came from -- the failure everything here exists to prevent.
    ///
    /// With <paramref name="apply"/> false nothing is touched and the plan is the answer; the
    /// editor shows it beside the button before anyone presses it.
    /// </summary>
    public static List<Change>? Rename(ModProject mod, string oldId, string newId, bool apply, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(oldId)) { why = "no weapon to rename"; return null; }
        if (string.IsNullOrWhiteSpace(newId)) { why = "the weapon needs a name"; return null; }
        // TAKEN AS TYPED AND MADE INTO A NAME, rather than refused. A person renaming a weapon
        // types the name they want players to read -- "Icon Test" -- and an asset name cannot
        // hold a space, a dot or a slash. Refusing that left the button dead with the reason in
        // small text beside it, which reads exactly like a rename that does not work.
        newId = AssetNameOf(newId);
        if (newId.Length == 0) { why = "that leaves nothing an asset can be called"; return null; }
        if (newId.Equals(oldId, StringComparison.Ordinal)) { why = "that is its name already"; return null; }

        // The two forms the icon tag needs; which names belong to this weapon at all is
        // NamesTheWeapon, so the editor and the rename cannot disagree about it.
        var oldAlnum = new string(oldId.Where(char.IsLetterOrDigit).ToArray());
        var newAlnum = new string(newId.Where(char.IsLetterOrDigit).ToArray());

        string? Swap(string s)
        {
            if (!NamesTheWeapon(s, oldId)) return null;
            return s.StartsWith("ImageBank.", StringComparison.OrdinalIgnoreCase)
                ? s[..^oldAlnum.Length] + newAlnum
                : s[..(s.Length - oldId.Length)] + newId;
        }

        var changes = new List<Change>();
        void Consider(string what, string from)
        {
            if (Swap(from) is { } to) changes.Add(new Change(what, from, to));
        }

        foreach (var c in mod.Copies)
        {
            Consider("copy", c.Id);
            foreach (var v in c.RenameNames.Values) Consider($"{c.Id} re-points to", v);
            foreach (var v in c.RepointImports.Values) Consider($"{c.Id} imports", v);
        }
        foreach (var p in mod.Parts)
        {
            Consider("part", p.Id);
            foreach (var v in p.RenameNames.Values) Consider($"{p.Id} re-points to", v);
        }
        foreach (var t in mod.Tags) Consider("tag", t);
        foreach (var i in mod.Icons) Consider("icon", i.Tag);

        if (changes.Count == 0) { why = $"nothing in this mod is named after '{oldId}'"; return null; }

        // A new asset name already in use is two packages at one id, which is the fault that
        // looks like a mod that never installed.
        var newAssetIds = changes.Where(ch => ch.What is "copy" or "part").Select(ch => ch.To)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clash = mod.Copies.Select(c => c.Id).Concat(mod.Parts.Select(p => p.Id))
                       .FirstOrDefault(id => newAssetIds.Contains(id));
        if (clash is not null) { why = $"this mod already has something called {clash}"; return null; }

        if (!apply) return changes;

        foreach (var c in mod.Copies)
        {
            if (Swap(c.Id) is { } id) c.Id = id;
            foreach (var k in c.RenameNames.Keys.ToList())
                if (Swap(c.RenameNames[k]) is { } v) c.RenameNames[k] = v;
            foreach (var k in c.RepointImports.Keys.ToList())
                if (Swap(c.RepointImports[k]) is { } v) c.RepointImports[k] = v;
        }
        foreach (var p in mod.Parts)
        {
            if (Swap(p.Id) is { } id) p.Id = id;
            foreach (var k in p.RenameNames.Keys.ToList())
                if (Swap(p.RenameNames[k]) is { } v) p.RenameNames[k] = v;
        }
        for (var i = 0; i < mod.Tags.Count; i++)
            if (Swap(mod.Tags[i]) is { } t) mod.Tags[i] = t;
        foreach (var i in mod.Icons)
            if (Swap(i.Tag) is { } t) i.Tag = t;
        return changes;
    }

    // ------------------------------------------------------------------ which pieces it shows

    /// <summary>The suffix a hidden mesh gets. Not a real asset, which is the point.</summary>
    public const string HiddenSuffix = "_HiddenByMod";

    /// <summary>One slot of a duplicated weapon, and what it puts there.</summary>
    /// <param name="Mesh">The mesh the donor contributes here, or "" when it contributes none.</param>
    public sealed record Piece(string Slot, string CopyId, string Mesh, bool Hidden, bool OwnMesh);

    /// <summary>
    /// What each slot of a duplicated weapon currently shows.
    ///
    /// Only slots the mod has a palette COPY for can be answered, because a slot pointing at the
    /// game's palette is not this mod's to change -- and only a copy can hide a piece without
    /// touching an asset every other weapon in the game shares.
    /// </summary>
    public static List<Piece> PiecesOf(IFileProvider provider, ModProject mod, string gearKitPartId)
    {
        var found = new List<Piece>();
        if (IdentityOf(gearKitPartId) is not { } who) return found;
        var typePart = mod.Parts.FirstOrDefault(
            x => x.Id.Equals($"CPD_WP_Type_{who.Class}_{who.Id}", StringComparison.OrdinalIgnoreCase));
        if (typePart is null) return found;
        if (DonorTagOf(provider, TemplatePath(provider, typePart.Template)) is not { } donorTag) return found;

        foreach (var copy in mod.Copies)
        {
            if (!copy.Id.StartsWith("CPD_WP_", StringComparison.Ordinal)) continue;
            if (copy.Id.StartsWith("CPD_WP_Type_", StringComparison.Ordinal)) continue;
            if (!NamesTheWeapon(copy.Id, who.Id)) continue;

            // The mesh the DONOR's palette gives this tag -- read from the template, because the
            // copy's own renames are exactly what this is deciding.
            var mesh = PartDetails.Read(provider, TemplatePath(provider, copy.Template)).Meshes
                .FirstOrDefault(m => m.RequiredTags.Contains(donorTag, StringComparer.Ordinal))?.MeshPath ?? "";
            var package = mesh.Length > 0 ? AssetLoader.PackagePath(mesh) : "";
            var hidden = package.Length > 0 && copy.RenameNames.TryGetValue(package, out var to) &&
                         to.EndsWith(HiddenSuffix, StringComparison.Ordinal);
            var own = package.Length > 0 && copy.RenameNames.TryGetValue(package, out var own2) &&
                      !own2.EndsWith(HiddenSuffix, StringComparison.Ordinal);
            var slot = copy.Id["CPD_WP_".Length..];
            if (slot.IndexOf('_') is var cut && cut > 0) slot = slot[..cut];
            found.Add(new Piece(slot, copy.Id, package, hidden, own));
        }
        return found.OrderBy(x => x.Slot, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Show or hide one slot's piece, inside the mod's OWN copy of that palette.
    ///
    /// This is what a weapon whose model is one imported piece needs. Keeping the donor's type tag
    /// makes every slot resolve -- which is how the game's attachments come to fit -- and a gun
    /// that already has its own barrel and stock modelled in then wears the donor's as well.
    ///
    /// Hiding renames the mesh to a name no asset has, in the mod's copy only, so the donor keeps
    /// its parts and no shared asset is touched. A FITTED MODIFICATION still shows, because that
    /// points the slot at the game's palette rather than at this copy -- which is exactly the
    /// arrangement someone wants who modelled their own gun and still wants the game's scopes.
    /// </summary>
    public static bool SetPieceShown(IFileProvider provider, ModProject mod, string gearKitPartId,
                                     string slot, bool show, out string? why)
    {
        why = null;
        var piece = PiecesOf(provider, mod, gearKitPartId)
            .FirstOrDefault(x => x.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (piece is null) { why = $"this weapon has no {slot} palette of its own"; return false; }
        if (piece.OwnMesh)
        { why = $"the {slot} already shows a mesh of the mod's own -- change that instead"; return false; }
        if (piece.Mesh.Length == 0)
        { why = $"the {slot} palette gives this weapon no mesh, so there is nothing to hide"; return false; }
        if (piece.Hidden == !show) { why = show ? "it already shows" : "it is already hidden"; return false; }

        var copy = mod.Copies.First(c => c.Id.Equals(piece.CopyId, StringComparison.OrdinalIgnoreCase));
        if (show)
        {
            copy.RenameNames.Remove(piece.Mesh);
            copy.RenameNames.Remove(Leaf(piece.Mesh));
        }
        else
        {
            // BOTH names. A soft object path is the package path and the asset name, and moving one
            // without the other leaves a reference that still resolves.
            copy.RenameNames[piece.Mesh] = piece.Mesh + HiddenSuffix;
            copy.RenameNames[Leaf(piece.Mesh)] = Leaf(piece.Mesh) + HiddenSuffix;
        }
        return true;
    }

    // ------------------------------------------------------------------ whose weapon type is it

    /// <summary>
    /// Whether this weapon still answers to the type tag it was copied FROM.
    ///
    /// Null when it is not a duplicated weapon at all. The donor's tag is the KEY of the type
    /// part's tag rename; a weapon that keeps it has no such rename to read, so the answer comes
    /// from whether one is there.
    /// </summary>
    // -------------------------------------------------- a shelf of the author's own

    /// <summary>Where the game keeps a weapon class's attribute table.</summary>
    public const string AttributeFolder =
        "/Game/Game/GameData/WeaponInfo/WeaponAttributes/Heroes/";

    /// <summary>The attribute table a class of this kind uses, by the game's own naming.</summary>
    public static string AttributeTableFor(string weaponClass) =>
        $"{AttributeFolder}Attributes_RangedWeap_Blaster{weaponClass}_Default";

    /// <summary>The class part id a mod's own class is built as.</summary>
    public static string OwnClassId(string classId) => $"CPD_WeaponSpec_Own_{classId}";

    /// <summary>
    /// The attribute table id a weapon's own numbers live in.
    ///
    /// It ENDS in the weapon's id, with no "_Default" after it, and that is not cosmetic:
    /// <see cref="NamesTheWeapon"/> is the whole rename rule and it requires a name to end in the
    /// id at a boundary. Named Attributes_RangedWeap_&lt;Id&gt;_Default, the table and the ability
    /// set's reference to it would be the two names a rename silently left behind.
    /// </summary>
    public static string OwnTableId(string classId) => $"Attributes_RangedWeap_{classId}";

    /// <summary>The gameplay tag a mod's own class is keyed by. Namespaced by the MOD.</summary>
    public static string OwnClassTag(string modName, string classId) =>
        ClassTag($"Own.{Sanitise(modName)}.{Sanitise(classId)}");

    /// <summary>A name reduced to what a gameplay tag segment and an asset name both allow.</summary>
    public static string Sanitise(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } clean ? clean : "Mod";

    // ------------------------------------------------------- numbers of the weapon's own

    /// <summary>
    /// Which package under <paramref name="folder"/> this one IMPORTS, or null.
    ///
    /// **An import is a package ID, not a name.** A hard object reference does not put the target's
    /// path in the importing package's name table at all -- which is why the first attempt at this,
    /// searching the name table, found nothing and reported the reference as absent. The ID is a
    /// hash of the package name, so the lookup is inverted instead: hash the handful of candidates
    /// that live in the right folder and see which ID the package actually imports. A dozen hashes,
    /// and it cannot be fooled by a naming convention that does not hold.
    /// </summary>
    private static string? ImportUnder(IFileProvider provider, string package, string folder)
    {
        var src = AssetCopy.PristineRead(provider, package, out _);
        if (src?.Store is null) return null;
        var ids = new HashSet<ulong>(src.Store.ImportedPackages);

        foreach (var key in AssetCopy.PristineFiles(provider))
        {
            if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            if (!key.Contains(folder, StringComparison.OrdinalIgnoreCase)) continue;
            if (PackageId.PackageNameFor(key, ModBuild.ProjectName) is not { } pkg) continue;
            if (ids.Contains(PackageId.Of(pkg))) return pkg;
        }
        return null;
    }

    /// <summary>The ability set a gear kit grants, which is what actually gives a weapon its stats.</summary>
    public static string? AbilitySetOf(IFileProvider provider, string gearKitPackage) =>
        ImportUnder(provider, gearKitPackage, "/GearAbilitySets/");

    /// <summary>The RANGED attribute table an ability set grants. It also grants a melee one.</summary>
    public static string? RangedTableOf(IFileProvider provider, string abilitySetPackage) =>
        ImportUnder(provider, abilitySetPackage, "/WeaponAttributes/Heroes/Attributes_RangedWeap_");

    /// <summary>
    /// Give a duplicated weapon NUMBERS OF ITS OWN.
    ///
    /// **MEASURED IN GAME 2026-09-03, after two wrong theories.** A weapon's stats do NOT come from
    /// its class. The chain is:
    ///
    ///     CPD_GK_&lt;Class&gt;_&lt;Weapon&gt;  ->  GK_&lt;Class&gt;_&lt;Weapon&gt;
    ///     GK_...AssociatedAbilitySet    ->  AbilitySet_Gear_&lt;Kind&gt;Standard
    ///     ...GrantedAttributes[0]       ->  Attributes_RangedWeap_&lt;Kind&gt;_Default
    ///
    /// and that grant carries **bOverrideExistingAttributeValues = True**, so it is applied AFTER
    /// the class part's own AttributeSets fragment and stamps over it. A class whose
    /// DefaultAttributes named a different table showed the original numbers on screen -- twice,
    /// with two different tables -- which is what sent this looking in the right place.
    ///
    /// So a weapon's numbers are the weapon's, not the class's, and that is the better answer:
    /// ANY duplicate can be rebalanced, with or without a shelf of its own.
    ///
    /// TWO copies and FOUR re-points, because both references are HARD:
    ///     AbilitySet_Gear_&lt;Id&gt;          the grant, pointed at the table below
    ///     Attributes_RangedWeap_&lt;Id&gt;_Default   the numbers, which ModCopy.Numbers edits
    /// Each needs its name renamed AND its import redirected -- a hard reference is a package id
    /// that no rename touches, and moving only one half looks correct everywhere a person checks.
    /// </summary>
    public static List<Change>? OwnStats(IFileProvider provider, ModProject mod,
                                         string gearKitPartId, bool apply, out string? why)
    {
        why = null;
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"'{gearKitPartId}' is not a duplicated weapon's armoury part"; return null; }

        var gkId = $"GK_{who.Class}_{who.Id}";
        var gk = mod.Copies.FirstOrDefault(x => x.Id.Equals(gkId, StringComparison.OrdinalIgnoreCase));
        if (gk is null)
        { why = $"{mod.Name} has no gear kit copy called {gkId}, so there is nothing to give stats to"; return null; }

        if (AbilitySetOf(provider, gk.Template) is not { } set)
        { why = $"{Leaf(gk.Template)} names no gear ability set, so where its stats come from is unknown"; return null; }
        if (RangedTableOf(provider, set) is not { } table)
        { why = $"{Leaf(set)} grants no ranged attribute table"; return null; }

        var setId = $"AbilitySet_Gear_{who.Id}";
        var tableId = OwnTableId(who.Id);
        if (mod.Copies.Any(x => x.Id.Equals(tableId, StringComparison.OrdinalIgnoreCase)))
        { why = $"{who.Id} already has numbers of its own"; return null; }

        var folder = FolderFor(mod);
        var changes = new List<Change>
        {
            new($"new ability set {setId}", set, folder + setId),
            new($"new stats table {tableId}", table, folder + tableId),
            new($"{gkId} grants", Leaf(set), setId),
            new($"{setId} numbers", Leaf(table), tableId),
        };
        if (!apply) return changes;

        var setCopy = new ModCopy { Id = setId, Template = set, NewGuid = true };
        setCopy.RenameNames[table] = folder + tableId;
        setCopy.RenameNames[Leaf(table)] = tableId;
        setCopy.RepointImports[table] = tableId;
        mod.Copies.Add(setCopy);

        mod.Copies.Add(new ModCopy { Id = tableId, Template = table, NewGuid = true });

        gk.RenameNames[set] = folder + setId;
        gk.RenameNames[Leaf(set)] = setId;
        gk.RepointImports[set] = setId;
        return changes;

        static string Leaf(string p) => p[(p.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// One number a weapon's own stats table holds, ready to draw in a list.
    ///
    /// <paramref name="Editable"/> is false for a value serialised as ZERO: unversioned
    /// serialisation omits it entirely, so there are no bytes to write over. Shown anyway, because
    /// "this stat exists and is 0" is what somebody needs to know before asking why setting it did
    /// nothing.
    /// </summary>
    public sealed record Stat(string Row, string Field, string Label, float Value, bool Editable,
                              bool Changed);

    /// <summary>The attribute set rows are keyed by; stripped from the label nobody needs it in.</summary>
    public const string StatPrefix = "BitReactorRangedWeaponSet.";

    /// <summary>
    /// A weapon's numbers, as the build will write them -- the table's values with the project's
    /// pending changes applied on top.
    ///
    /// Only BaseValue. Min and Max are EDITOR ANNOTATION rather than runtime clamps (the DC-17
    /// ships CriticalHitDamage 8 against a declared max of 2), so showing them invites someone to
    /// "fix" a number that is doing nothing.
    /// </summary>
    public static List<Stat>? StatsOf(IFileProvider provider, ModProject mod, string gearKitPartId,
                                      out string? why)
    {
        why = null;
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"'{gearKitPartId}' is not a duplicated weapon's armoury part"; return null; }

        var table = mod.Copies.FirstOrDefault(
            x => x.Id.Equals(OwnTableId(who.Id), StringComparison.OrdinalIgnoreCase));
        if (table is null) { why = "this weapon uses its kind's numbers, shared with the game's"; return null; }

        var copied = AssetCopy.PristineRead(provider, table.Template, out var readWhy);
        var raw = copied?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null) { why = readWhy ?? "its table could not be read"; return null; }

        var fields = DataTableEdit.Walk(provider, raw, table.Template, out var walkWhy);
        if (walkWhy is not null) { why = walkWhy; return null; }

        var stats = new List<Stat>();
        foreach (var f in fields.Where(x => x.Name == "BaseValue")
                                .OrderBy(x => x.Row, StringComparer.OrdinalIgnoreCase))
        {
            var editable = f.Length >= 4 && f.Start >= 0 && f.Start + 4 <= raw.Length;
            var value = editable ? BitConverter.ToSingle(raw, f.Start) : 0f;
            var asked = table.Numbers.FirstOrDefault(
                n => n.Row.Equals(f.Row, StringComparison.OrdinalIgnoreCase) && n.Field == f.Name);
            var label = f.Row.StartsWith(StatPrefix, StringComparison.Ordinal)
                            ? f.Row[StatPrefix.Length..] : f.Row;
            stats.Add(new Stat(f.Row, f.Name, label, (float) (asked?.Now ?? value), editable, asked is not null));
        }
        return stats;
    }

    /// <summary>
    /// Ask for one of a weapon's numbers to be different.
    ///
    /// The "was" recorded is the TEMPLATE's value, never the last thing typed: every build starts
    /// from a fresh copy of the game's table, so a change whose "was" is a previous edit would fail
    /// to locate its row the second time the mod is built. Setting a stat back to its original
    /// value REMOVES the request rather than recording a no-op.
    /// </summary>
    public static bool SetStat(IFileProvider provider, ModProject mod, string gearKitPartId,
                               string row, float now, out string? why)
    {
        why = null;
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"'{gearKitPartId}' is not a duplicated weapon's armoury part"; return false; }

        var table = mod.Copies.FirstOrDefault(
            x => x.Id.Equals(OwnTableId(who.Id), StringComparison.OrdinalIgnoreCase));
        if (table is null) { why = "this weapon has no numbers of its own yet"; return false; }

        var copied = AssetCopy.PristineRead(provider, table.Template, out _);
        var raw = copied?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null) { why = "its table could not be read"; return false; }
        var fields = DataTableEdit.Walk(provider, raw, table.Template, out var walkWhy);
        if (walkWhy is not null) { why = walkWhy; return false; }

        var field = fields.FirstOrDefault(
            x => x.Row.Equals(row, StringComparison.OrdinalIgnoreCase) && x.Name == "BaseValue");
        if (field is null) { why = $"'{row}' is not a row of its table"; return false; }
        if (field.Length < 4 || field.Start < 0 || field.Start + 4 > raw.Length)
        { why = $"'{row}' is serialised as zero and has no bytes to write over"; return false; }

        var was = BitConverter.ToSingle(raw, field.Start);
        table.Numbers.RemoveAll(n => n.Row.Equals(row, StringComparison.OrdinalIgnoreCase) &&
                                     n.Field == "BaseValue");
        if (Math.Abs(was - now) > 0.0001f)
            table.Numbers.Add(new ModOverride.NumberChange(field.Row, "BaseValue", was, now));
        return true;
    }

    /// <summary>
    /// Give a duplicated weapon a shelf of its OWN -- a weapon class this mod defines, with its
    /// own name, its own tag, and its own copy of the attribute table so its stats can be changed
    /// without touching the game's or anybody else's.
    ///
    /// THREE COPIES, and each one is a shape already proved in game:
    ///   the CLASS part      a copy of a game class, its identity tag renamed and re-pointed at
    ///                       the table copy -- exactly what CustomWeaponClasses ships
    ///   the ATTRIBUTE table a copy of that class's AttributeMetaData DataTable, which is what
    ///                       makes the stats the author's rather than the kind's
    ///   the weapon          its armoury part's class tag renamed to the new class
    ///
    /// <paramref name="behavesLike"/> is the game class copied from, and it is the most
    /// consequential choice here: the class part carries the ANIMATION SETS, the abilities, the
    /// gameplay effects and the attribute table, so a class copied from Repeater is a repeater in
    /// the hand whatever it is called. It defaults to the weapon's own class, which is the answer
    /// that surprises nobody.
    ///
    /// The tag is namespaced by the MOD, because a class a mod defines is not canonical the way
    /// the shipped four are -- two authors who both picked "Heavy" would collide, and a collision
    /// between two mods' classes is a shelf whose contents depend on mount order.
    /// </summary>
    public static List<Change>? MakeOwnClass(IFileProvider provider, ModProject mod,
                                             string gearKitPartId, string classId, string display,
                                             string? behavesLike, bool apply, out string? why)
    {
        why = null;
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"'{gearKitPartId}' is not a duplicated weapon's armoury part"; return null; }

        var id = Sanitise(classId);
        if (id.Length == 0) { why = "the class needs a name"; return null; }

        var from = string.IsNullOrWhiteSpace(behavesLike) ? who.Class : behavesLike!;
        if (!HasCustomClass(from))
        { why = $"a class can only be modelled on one of {string.Join(", ", MirroredClasses)} -- " +
                $"'{from}' is gated or is not assembled from mesh palettes"; return null; }

        var part = mod.Copies.FirstOrDefault(
            x => x.Id.Equals(gearKitPartId, StringComparison.OrdinalIgnoreCase));
        if (part is null) { why = $"{mod.Name} has no copy called {gearKitPartId}"; return null; }

        var classPartId = OwnClassId(id);
        if (mod.Copies.Any(x => x.Id.Equals(classPartId, StringComparison.OrdinalIgnoreCase)))
        { why = $"this mod already has a class called {id}"; return null; }

        var tag = OwnClassTag(mod.Name, id);
        var folder = FolderFor(mod);
        var template = $"/Game/Game/Customizations/Characters/Common/Specialization/Weapon/CPD_WeaponSpec_Blaster_{from}";

        var changes = new List<Change>
        {
            new($"new class part {classPartId}", template, folder + classPartId),
            new($"{classPartId} identity", ClassTag(from), tag),
            new($"{gearKitPartId} AllowedSlots", ClassTag(who.Class), tag),
            new("declared tag", "(none)", tag),
        };
        if (!apply) return changes;

        // The class part. Its identity tag becomes the new one, and BOTH halves of the attribute
        // table's soft path are re-pointed -- a soft path is two FNames, and moving one leaves a
        // reference that resolves to nothing at all.
        var classCopy = new ModCopy { Id = classPartId, Template = template, NewGuid = true, Display = display };
        classCopy.RenameNames[ClassTag(from)] = tag;

        // THE CLASS'S OWN AttributeSets FRAGMENT IS NOT WHERE STATS COME FROM, so it is left
        // pointing at the game's table and nothing is copied for it. MEASURED IN GAME: a class
        // whose DefaultAttributes named a different table showed the ORIGINAL numbers, because
        // the weapon's GEAR KIT grants the same attribute set afterwards with
        // bOverrideExistingAttributeValues = True and stamps over it. See OwnStats, which is
        // where a weapon's numbers actually live.

        // AND ITS DEFAULT WEAPON. The template's CustomizationFragmentSlot.DefaultPart names the
        // GAME's weapon for the class it was copied from -- the IQA-11 for a Longarm -- which is a
        // weapon on a different shelf entirely. Left alone, picking this class hands the soldier a
        // gun that is not in it, and the numbers on screen are that gun's class rather than this
        // one's, so the author's own stats appear to do nothing at all.
        if (DefaultPartOf(provider, template) is { } wasDefault)
        {
            classCopy.RenameNames[wasDefault] = folder + gearKitPartId;
            classCopy.RenameNames[Leaf(wasDefault)] = gearKitPartId;
        }
        mod.Copies.Add(classCopy);

        // The weapon moves onto it -- replacing any mirrored-class routing, since a weapon is on
        // exactly one shelf.
        part.RenameNames.Remove(ClassTag(who.Class));
        part.RenameNames[ClassTag(who.Class)] = tag;

        if (!mod.Tags.Contains(tag, StringComparer.Ordinal)) mod.Tags.Add(tag);
        return changes;

        static string Leaf(string p) => p[(p.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// The weapon a class hands out by default, as its package name, or null when it has none.
    ///
    /// READ rather than derived. The naming is regular for the four ordinary classes, and the
    /// point of reading it is that a class copied from anywhere else still gets the right answer.
    /// </summary>
    public static string? DefaultPartOf(IFileProvider provider, string classPackage)
    {
        var copied = AssetCopy.PristineRead(provider, classPackage, out _);
        var raw = copied?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null) return null;
        try
        {
            return ZenPackage.Read(raw).Names.FirstOrDefault(
                n => n.StartsWith("/Game/", StringComparison.Ordinal) &&
                     n.Contains("/" + WeaponIndex.GearKitPartPrefix, StringComparison.Ordinal));
        }
        catch { return null; }
    }

    /// <summary>
    /// The class of its OWN this weapon is on, or null when it is on a game or mirrored shelf.
    /// </summary>
    public static string? OwnClassOf(ModProject mod, string gearKitPartId)
    {
        if (IdentityOf(gearKitPartId) is not { } who) return null;
        var part = mod.Copies.FirstOrDefault(
            x => x.Id.Equals(gearKitPartId, StringComparison.OrdinalIgnoreCase));
        if (part is null) return null;
        if (!part.RenameNames.TryGetValue(ClassTag(who.Class), out var to)) return null;
        var own = ClassTag("Own.");
        return to.StartsWith(own, StringComparison.Ordinal) ? to : null;
    }

    /// <summary>
    /// Whether this duplicate is routed into the mirrored CUSTOM class, read from the project.
    ///
    /// Null when the weapon is not one this project holds, or when its class has no mirror -- the
    /// two cases a caller must not draw a switch for, and they are different: the first is an
    /// error and the second is simply how the game is.
    /// </summary>
    public static bool? InCustomClass(ModProject mod, string gearKitPartId)
    {
        if (IdentityOf(gearKitPartId) is not { } who) return null;
        if (!HasCustomClass(who.Class)) return null;
        var part = mod.Copies.FirstOrDefault(
            x => x.Id.Equals(gearKitPartId, StringComparison.OrdinalIgnoreCase));
        if (part is null) return null;
        return part.RenameNames.TryGetValue(ClassTag(who.Class), out var to) &&
               to.Equals(CustomClassTag(who.Class), StringComparison.Ordinal);
    }

    /// <summary>
    /// Put this duplicate in the custom class, or back in the game's own.
    ///
    /// One rename added or removed on the armoury part -- everything else about the weapon is
    /// unaffected, which is the point: its meshes, its tag, its attachments and its icon do not
    /// know or care which shelf it sits on.
    ///
    /// Returns the changes, or null with a reason. Nothing is written unless
    /// <paramref name="apply"/>, so a caller can show what would happen first.
    /// </summary>
    public static List<Change>? SetCustomClass(ModProject mod, string gearKitPartId, bool custom,
                                               bool apply, out string? why)
    {
        why = null;
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"'{gearKitPartId}' is not a duplicated weapon's armoury part"; return null; }
        if (CustomClassTag(who.Class) is not { } customTag)
        { why = $"the game has no custom class mirroring {who.Class}, so this weapon stays in {who.Class}"; return null; }

        var part = mod.Copies.FirstOrDefault(
            x => x.Id.Equals(gearKitPartId, StringComparison.OrdinalIgnoreCase));
        if (part is null) { why = $"{mod.Name} has no copy called {gearKitPartId}"; return null; }

        var gameTag = ClassTag(who.Class);

        // ROUTED ANYWHERE, not just to the custom shelf.
        //
        // This used to ask only whether the rename pointed at the CUSTOM tag, so a weapon on a
        // shelf of the mod's OWN read as "not routed" -- and asking to put it back on the game's
        // shelf was refused with "it is already in the game's Longarm", about a weapon plainly
        // listed somewhere else. Both studios draw that button and neither one worked.
        //
        // The rename IS the routing, whatever it points at, so removing it is the move back
        // wherever the weapon came from. The shelf itself stays in the mod; only this weapon
        // moves off it.
        var routed = part.RenameNames.TryGetValue(gameTag, out var to);
        var inCustom = routed && to!.Equals(customTag, StringComparison.Ordinal);

        if (custom && inCustom)
        { why = $"{gearKitPartId} is already in Custom {who.Class}"; return null; }
        if (!custom && !routed)
        { why = $"{gearKitPartId} is already in the game's {who.Class}"; return null; }

        var changes = new List<Change>
        {
            new($"{gearKitPartId} AllowedSlots",
                custom ? (routed ? to! : gameTag) : to!, custom ? customTag : gameTag),
        };
        if (!apply) return changes;

        if (custom) part.RenameNames[gameTag] = customTag;
        else part.RenameNames.Remove(gameTag);
        return changes;
    }

    public static bool? KeepsDonorTag(ModProject mod, string gearKitPartId)
    {
        if (IdentityOf(gearKitPartId) is not { } who) return null;
        var typePart = mod.Parts.FirstOrDefault(
            x => x.Id.Equals($"CPD_WP_Type_{who.Class}_{who.Id}", StringComparison.OrdinalIgnoreCase));
        if (typePart is null) return null;
        return !typePart.RenameNames.Keys.Any(k => k.StartsWith(TypeTagPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Switch a duplicated weapon between having a type tag of its own and keeping the donor's.
    ///
    /// THE TAG IS WHAT DECIDES WHETHER THE GAME'S ATTACHMENTS FIT. A weapon's meshes are entries in
    /// shared palettes keyed by that tag, and a fitted modification points the slot at the GAME's
    /// palette -- `Modification_Barrel_Condenser_T1` names `CPD_WP_Barrel_02_Condenser` outright.
    /// So a copy with a tag of its own has no entry in any palette the game reaches for, and the
    /// piece vanishes the moment an attachment is fitted; a copy that keeps the donor's is found in
    /// all twenty-one of them.
    ///
    /// The cost of keeping it is that this weapon IS that weapon as far as the palettes are
    /// concerned, so every slot it does not copy shows the donor's piece. That is what a person
    /// duplicating a rifle wants and exactly what a person importing a whole gun of their own does
    /// not -- hence a choice rather than a default.
    ///
    /// Two parts carrying one type tag is not a new situation: the armoury part of EVERY duplicate
    /// this tool has ever made already advertises the donor's tag, the DLT-20A included, and that
    /// weapon has been in the game and correct for two days.
    ///
    /// Same contract as <see cref="Rename"/>: with <paramref name="apply"/> false the plan is the
    /// answer and nothing is touched.
    /// </summary>
    public static List<Change>? SetDonorTag(IFileProvider provider, ModProject mod,
                                            string gearKitPartId, bool keepDonors,
                                            bool apply, out string? why)
    {
        why = null;
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"{gearKitPartId} is not a weapon's armoury part"; return null; }
        var typePart = mod.Parts.FirstOrDefault(
            x => x.Id.Equals($"CPD_WP_Type_{who.Class}_{who.Id}", StringComparison.OrdinalIgnoreCase));
        if (typePart is null || KeepsDonorTag(mod, gearKitPartId) is not { } keepsNow)
        { why = $"this mod has no CPD_WP_Type_{who.Class}_{who.Id}, so its type tag cannot be read"; return null; }
        if (keepsNow == keepDonors)
        { why = keepDonors ? "it already keeps the weapon's own type"
                           : "it already has a type of its own"; return null; }

        // THE DONOR'S TAG IS READ FROM THE DONOR, not from the project. Keeping the donor's tag
        // means deleting the renames that mention it, so a project in that state no longer records
        // what it was copied from -- and reading it back out of the renames worked in one direction
        // only, which is a switch that cannot be undone.
        var donorTag = DonorTagOf(provider, TemplatePath(provider, typePart.Template));
        if (donorTag is null)
        { why = $"cannot read a weapon type tag out of {Leaf(typePart.Template)}"; return null; }
        var ownTag = donorTag[..(donorTag.LastIndexOf('.') + 1)] + who.Id;
        if (donorTag.Equals(ownTag, StringComparison.Ordinal))
        { why = $"a tag of its own would be {ownTag}, which is the donor's"; return null; }

        var changes = new List<Change>();
        var from = keepDonors ? ownTag : donorTag;
        var to = keepDonors ? donorTag : ownTag;

        foreach (var part in mod.Parts)
        {
            if (!NamesTheWeapon(part.Id, who.Id)) continue;
            if (keepDonors && part.RenameNames.ContainsKey(donorTag))
                changes.Add(new Change($"{part.Id} stops re-keying the tag", donorTag, ownTag));
            else if (!keepDonors && !part.RenameNames.ContainsKey(donorTag) &&
                     part.Id.StartsWith("CPD_WP_Type_", StringComparison.Ordinal))
                changes.Add(new Change($"{part.Id} re-keys the tag", donorTag, ownTag));
        }
        foreach (var copy in mod.Copies)
        {
            if (!NamesTheWeapon(copy.Id, who.Id)) continue;
            if (keepDonors && copy.RenameNames.ContainsKey(donorTag))
                changes.Add(new Change($"{copy.Id} stops re-keying the tag", donorTag, ownTag));
            else if (!keepDonors && !copy.RenameNames.ContainsKey(donorTag) &&
                     copy.Id.StartsWith("CPD_WP_", StringComparison.Ordinal) &&
                     !copy.Id.StartsWith("CPD_WP_Type_", StringComparison.Ordinal))
                changes.Add(new Change($"{copy.Id} re-keys the tag", donorTag, ownTag));
        }
        foreach (var ov in mod.Overrides.Where(o => o.AddOptions.ContainsKey(from)))
            changes.Add(new Change($"{Leaf(ov.Target)} files its added mesh under the new tag", from, to));
        changes.Add(new Change(keepDonors ? "the mod stops declaring a tag" : "the mod declares its own tag",
                               from, to));

        if (!apply) return changes;

        foreach (var part in mod.Parts.Where(x => NamesTheWeapon(x.Id, who.Id)))
        {
            if (keepDonors) part.RenameNames.Remove(donorTag);
            else if (part.Id.StartsWith("CPD_WP_Type_", StringComparison.Ordinal))
                part.RenameNames[donorTag] = ownTag;
        }
        foreach (var copy in mod.Copies.Where(x => NamesTheWeapon(x.Id, who.Id)))
        {
            if (keepDonors) copy.RenameNames.Remove(donorTag);
            else if (copy.Id.StartsWith("CPD_WP_", StringComparison.Ordinal) &&
                     !copy.Id.StartsWith("CPD_WP_Type_", StringComparison.Ordinal))
                copy.RenameNames[donorTag] = ownTag;
        }

        // THE PALETTE ENTRIES MOVE WITH THE TAG. They are keyed by it, and this switch used to
        // leave them behind: minting a tag on a weapon whose modifications were already wired left
        // every added entry under the OLD key, so the weapon answered to one tag and its meshes
        // were filed under another. The visible result is identical to having done nothing, which
        // is the worst way for a switch to fail.
        foreach (var ov in mod.Overrides)
        {
            if (!ov.AddOptions.TryGetValue(from, out var moves)) continue;
            ov.AddOptions.Remove(from);
            ov.AddOptions[to] = moves;
        }

        // The mod declares a tag it INVENTS and never one of the game's -- declaring the game's
        // would put a tag the game already defines into this mod's Config/Tags, which is a
        // conflict rather than a definition.
        mod.Tags.RemoveAll(t => t.Equals(ownTag, StringComparison.Ordinal));
        if (!keepDonors) mod.Tags.Add(ownTag);
        return changes;
    }

    /// <summary>
    /// The weapon type tag a donor type part carries, read from the part itself.
    ///
    /// A type part carries exactly one; where a package mentions several, the one ending in the
    /// part's own name is the one that IS it, which is the same test <see cref="Expand"/> makes.
    /// </summary>
    private static string? DonorTagOf(IFileProvider provider, string typePartPackage)
    {
        if (NamesOf(provider, typePartPackage) is not { } names) return null;
        var tags = names.Where(n => n.StartsWith(TypeTagPrefix, StringComparison.Ordinal)).ToList();
        if (tags.Count == 0) return null;
        var leaf = Leaf(typePartPackage);
        const string prefix = "CPD_WP_Type_";
        if (leaf.StartsWith(prefix, StringComparison.Ordinal) &&
            leaf[prefix.Length..].IndexOf('_') is var cut && cut > 0)
        {
            var name = leaf[(prefix.Length + cut + 1)..];
            if (tags.FirstOrDefault(t => t.EndsWith("." + name, StringComparison.Ordinal)) is { } exact)
                return exact;
        }
        return tags.Count == 1 ? tags[0] : null;
    }

    // ------------------------------------------------------------------ every attachment it wears

    /// <summary>Where the weapon parts palettes live. All twenty-one of them, in one folder.</summary>
    public const string PaletteFolder = "/Customizations/Weapons/Parts/";

    /// <summary>The folder the game keeps the attachment art every weapon shares in.</summary>
    public const string SharedFolder = "/Weapons/Mods_SharedElements/";

    /// <summary>What the mod is currently doing with one attachment.</summary>
    public enum Wearing
    {
        /// <summary>Untouched -- the weapon wears the donor's mesh and the donor's skin.</summary>
        Donor,
        /// <summary>
        /// Untouched AND nothing will render: the weapon minted a type tag of its own, so the
        /// game's palette holds no entry for it.
        ///
        /// A separate state from Donor because they look identical in the project and are opposite
        /// in the game. Calling both "the game's" told an author with a minted tag that fitting
        /// this modification would show the donor's piece, when in fact the slot goes EMPTY --
        /// measured in game on 2026-09-02, both ways.
        /// </summary>
        Nothing,
        /// <summary>Emptied inside the mod's own copy, so this slot shows nothing.</summary>
        Hidden,
        /// <summary>The mod supplies the mesh and leaves the skin alone.</summary>
        Mesh,
        /// <summary>The mod supplies the mesh and paints it too.</summary>
        MeshAndSkin,
        /// <summary>
        /// WIRED BUT UNREACHABLE: the mod names a mesh for this palette, and the palette already
        /// answers to this weapon's tag with the donor's mesh.
        ///
        /// A palette is keyed by TYPE TAG and a duplicate keeps the donor's by default, so an
        /// added entry sits behind the donor's and the game never reaches it. The build refuses
        /// the write and says so, but the panel read the PROJECT -- "your mesh" the moment an
        /// option existed -- so an author saw twenty rows of green and shipped none of them.
        /// Reported 2026-09-11 as "the default base game items show up despite everything being
        /// pointed to a new mesh".
        ///
        /// Separate from Mesh because the project cannot tell them apart and the game cannot
        /// confuse them, which is the same reason Nothing is separate from Donor.
        /// </summary>
        Shadowed,
    }

    /// <summary>
    /// One attachment a weapon can appear as: which palette, which mesh, whose paint.
    /// </summary>
    /// <param name="Default">
    /// True for the `_01` palette, which the LOADOUT names -- the piece the weapon shows with
    /// nothing fitted. The other four are reached only by fitting a modification.
    /// </param>
    /// <param name="Shared">
    /// True when the material and textures live in Mods_SharedElements, i.e. every weapon in the
    /// game wears this same paint on its own mesh. The `_01`s are the weapon's own; `_02`-`_05`
    /// are shared. Only the MESH is per-weapon there, and it is what carries the position.
    /// </param>
    public sealed record Attachment(string Slot, string Style, string Palette, string PaletteLeaf,
                                    string Mesh, bool Default, bool Shared,
                                    string Material, IReadOnlyList<string> Textures,
                                    Wearing State, string? CopyId, string? Repaintable);

    /// <summary>
    /// Every attachment the donor weapon appears in, and what this mod does with each.
    ///
    /// Twenty-one palettes: one Base, and five each of Barrel, Cell, Scope and Stock. A palette
    /// holds one entry per WEAPON, keyed by the weapon's type tag, so "which attachments does this
    /// weapon have" is answered by asking each palette whether it names the donor's tag -- not by
    /// a list in the code, which would go stale the moment the game added a sixth style.
    ///
    /// The `_01`s are the weapon's own art. The rest are shared: one material and one texture set
    /// in Mods_SharedElements for every gun in the game, with only the MESH per-weapon, because
    /// the mesh is what knows where the piece sits on that particular gun.
    ///
    /// Reading the SKIN of each is what lets the editor be honest about the difference. It costs
    /// twenty-one package reads and it is the whole reason a person can be told "this one is
    /// shared" before they paint it rather than after.
    /// </summary>
    public static List<Attachment> AttachmentsOf(IFileProvider provider, ModProject mod,
                                                 string gearKitPartId, out string? why)
    {
        why = null;
        var found = new List<Attachment>();
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"'{gearKitPartId}' is not a duplicated weapon's armoury part"; return found; }

        var typePart = mod.Parts.FirstOrDefault(
            x => x.Id.Equals($"CPD_WP_Type_{who.Class}_{who.Id}", StringComparison.OrdinalIgnoreCase));
        if (typePart is null)
        { why = $"{mod.Name} has no type part for {who.Id}"; return found; }

        var donorTag = DonorTagOf(provider, TemplatePath(provider, typePart.Template));
        if (donorTag is null)
        { why = $"cannot read the weapon type tag off {typePart.Template}"; return found; }

        // The tag the mod's own copies answer to -- the donor's unless the author minted one.
        var ownTag = typePart.RenameNames.TryGetValue(donorTag, out var minted) ? minted : donorTag;

        // PRISTINE, because the Studio mounts the whole game folder and every installed mod with
        // it -- including this mod's own last build, whose palettes would answer for themselves.
        foreach (var file in AssetCopy.PristineFiles(provider)
                     .Where(f => f.Contains(PaletteFolder, StringComparison.OrdinalIgnoreCase) &&
                                 f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var palette = PackageId.PackageNameFor(file, ModBuild.ProjectName);
            var leaf = Leaf(palette);
            if (!leaf.StartsWith("CPD_WP_", StringComparison.Ordinal)) continue;
            if (leaf.StartsWith("CPD_WP_Type_", StringComparison.Ordinal)) continue;

            // CPD_WP_<Slot>_<Style>. Split on the two underscores after the prefix rather than
            // matching a list of slot names, so a slot the game adds later still reads.
            var rest = leaf["CPD_WP_".Length..];
            var cut = rest.IndexOf('_');
            if (cut <= 0) continue;
            var slot = rest[..cut];
            var style = rest[(cut + 1)..];

            var details = PartDetails.Read(provider, palette);
            var entry = details.Meshes.FirstOrDefault(
                m => m.RequiredTags.Contains(donorTag, StringComparer.Ordinal));
            if (entry is null) continue;                   // this weapon has no piece in this style

            var mesh = AssetLoader.PackagePath(entry.MeshPath ?? "");
            var isDefault = style.StartsWith("01_", StringComparison.Ordinal);

            // WHOSE PAINT. Read rather than inferred from the "_01" in the name: the naming and
            // the ownership agree today and the question being asked is about the art, so ask the
            // art. A mesh that cannot be read leaves this blank rather than guessing "own".
            var chain = mesh.Length > 0 ? MaterialSkin.Read(provider, mesh, out _) : null;
            var material = chain?.MaterialPackage ?? "";
            var textures = chain?.Skins.Select(s => s.TexturePackage).ToList() ?? [];
            var shared = material.Contains(SharedFolder, StringComparison.OrdinalIgnoreCase);

            // WHETHER IT CAN BE PAINTED AT ALL, asked of the first texture. Sparse page tables --
            // every non-square texture in this game -- used to be refused here, and a dropdown
            // that offers a repaint which quietly does nothing is worse than one that says why.
            string? repaintable = null;
            if (textures.Count > 0 && chain!.Skins[0].IsVirtual &&
                VirtualTexture.Describe(provider, textures[0], out var vtWhy) is null)
                repaintable = vtWhy;

            // WHAT THE MOD DOES HERE. Two mechanisms, because the game has two: the default
            // palette is COPIED and re-keyed, the other four are the game's and get an added
            // entry. Which one is in play follows from the palette, not from a setting.
            var state = Wearing.Donor;
            string? copyId = null;
            if (isDefault)
            {
                var copy = mod.Copies.FirstOrDefault(
                    c => c.Template.Equals(palette, StringComparison.OrdinalIgnoreCase) &&
                         NamesTheWeapon(c.Id, who.Id));
                copyId = copy?.Id;
                if (copy is not null && mesh.Length > 0 &&
                    copy.RenameNames.TryGetValue(mesh, out var to))
                    state = to.EndsWith(HiddenSuffix, StringComparison.Ordinal)
                        ? Wearing.Hidden : Wearing.Mesh;
            }
            else
            {
                var ov = mod.Overrides.FirstOrDefault(
                    o => o.Target.Equals(palette, StringComparison.OrdinalIgnoreCase));
                copyId = ov?.Target;
                // WIRED, and reachable only if this weapon's tag is its own. Every palette listed
                // here was found BY the donor's tag, so with that tag kept the donor's entry is
                // already in it and an added one sits behind it for ever.
                if (ov is not null && ov.AddOptions.ContainsKey(ownTag))
                    state = ownTag.Equals(donorTag, StringComparison.Ordinal)
                        ? Wearing.Shadowed : Wearing.Mesh;

                // A minted tag is in none of the game's palettes, so an untouched modification
                // slot renders NOTHING rather than the donor's piece. With the donor's tag kept,
                // all twenty-one of the game's entries answer and Donor is the truth.
                else if (!ownTag.Equals(donorTag, StringComparison.Ordinal)) state = Wearing.Nothing;
            }

            // PAINTED means the mod carries a mesh entry for this piece WITH textures on it.
            if (state == Wearing.Mesh &&
                mod.Meshes.Any(m => m.Source.Equals(mesh, StringComparison.OrdinalIgnoreCase) &&
                                    m.Textures.Count > 0))
                state = Wearing.MeshAndSkin;

            found.Add(new Attachment(slot, style, palette, leaf, mesh, isDefault, shared,
                                     material, textures, state, copyId, repaintable));
        }

        if (found.Count == 0)
            why = $"no weapon parts palette names {donorTag}, so this weapon has no attachments " +
                  "to list -- which would mean its type tag is wrong";
        return found;
    }

    /// <summary>
    /// Make one attachment the mod's own, or hand it back to the donor.
    ///
    /// TWO MECHANISMS, because the game has two and the palette decides which. The `_01` default
    /// is a palette the mod already COPIES, so its own copy simply re-points the mesh. The other
    /// four are the game's palettes, reached only when a player fits a modification, and a copy of
    /// one is never consulted -- so those get an OVERRIDE that ADDS an entry for this weapon's tag
    /// and leaves every other weapon's alone. Neither writes over anything of the game's art.
    ///
    /// It creates the mesh ENTRY and stops. What goes in it -- the FBX, the textures -- is the
    /// mesh editor's job, and duplicating that here would be a second place for the same fields to
    /// drift out of step with the first.
    ///
    /// Handing one back removes the wiring. It removes the mesh entry too, but ONLY when there is
    /// nothing of the author's in it: an entry holding an FBX or textures is work, and a button
    /// that silently discards work is not one anybody should press twice.
    /// </summary>
    public static bool SetAttachment(IFileProvider provider, ModProject mod, string gearKitPartId,
                                     string palette, bool own, out string? why)
    {
        why = null;
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"'{gearKitPartId}' is not a duplicated weapon's armoury part"; return false; }

        var all = AttachmentsOf(provider, mod, gearKitPartId, out var listWhy);
        if (listWhy is not null && all.Count == 0) { why = listWhy; return false; }

        var it = all.FirstOrDefault(a => a.Palette.Equals(palette, StringComparison.OrdinalIgnoreCase));
        if (it is null) { why = $"this weapon has no piece in {Leaf(palette)}"; return false; }
        if (it.Mesh.Length == 0)
        { why = $"{it.PaletteLeaf} gives this weapon no mesh, so there is nothing to replace"; return false; }

        var modFolder = FolderFor(mod);
        var leaf = Leaf(it.Mesh);
        var mine = modFolder + leaf;

        var typePart = mod.Parts.First(
            x => x.Id.Equals($"CPD_WP_Type_{who.Class}_{who.Id}", StringComparison.OrdinalIgnoreCase));
        var donorTag = DonorTagOf(provider, TemplatePath(provider, typePart.Template))!;
        var ownTag = typePart.RenameNames.TryGetValue(donorTag, out var minted) ? minted : donorTag;

        if (own)
        {
            if (!mod.Meshes.Any(m => m.Source.Equals(it.Mesh, StringComparison.OrdinalIgnoreCase)))
                mod.Meshes.Add(new ModMesh { Source = it.Mesh });

            if (it.Default)
            {
                var copy = mod.Copies.FirstOrDefault(
                    c => c.Template.Equals(palette, StringComparison.OrdinalIgnoreCase) &&
                         NamesTheWeapon(c.Id, who.Id));
                if (copy is null)
                { why = $"this mod has no copy of {it.PaletteLeaf} -- press Complete the model first"; return false; }

                // A slot that was EMPTIED is renamed to a dead name; both halves of that go, or
                // the mesh is pointed at the mod's copy and hidden at the same time.
                copy.RenameNames.Remove(leaf);
                copy.RenameNames[it.Mesh] = mine;
            }
            else
            {
                var ov = mod.Overrides.FirstOrDefault(
                    o => o.Target.Equals(palette, StringComparison.OrdinalIgnoreCase));
                if (ov is null) { ov = new ModOverride { Target = palette }; mod.Overrides.Add(ov); }
                ov.AddOptions[ownTag] = mine;

                // The wiring is made either way -- it is what the weapon needs the moment it has a
                // tag of its own -- but it does NOTHING until then, and saying so here is the only
                // place an author finds out before a build. Every palette in this list was found by
                // the donor's tag, so with that tag kept the donor's entry answers first.
                if (ownTag.Equals(donorTag, StringComparison.Ordinal))
                    why = $"{it.Slot} {it.Style} is wired, but it cannot show until this weapon " +
                          "has a type tag of its own: the palette already answers to the tag it " +
                          "was copied with, and the game reads that entry. Press \"Give it its " +
                          "own tag\" to make every wired modification reachable.";
            }
            return true;
        }

        // ------------------------------------------------------------------ back to the donor
        if (it.Default)
        {
            var copy = mod.Copies.FirstOrDefault(
                c => c.Template.Equals(palette, StringComparison.OrdinalIgnoreCase) &&
                     NamesTheWeapon(c.Id, who.Id));
            copy?.RenameNames.Remove(it.Mesh);
        }
        else
        {
            var ov = mod.Overrides.FirstOrDefault(
                o => o.Target.Equals(palette, StringComparison.OrdinalIgnoreCase));
            if (ov is not null)
            {
                ov.AddOptions.Remove(ownTag);

                // Dropped only when NOTHING is left in it. HasEdit does not count an image or an
                // imported mesh, so an override carrying one of those and one added option would
                // be thrown away whole by the shorter test.
                if (!ov.HasEdit && string.IsNullOrWhiteSpace(ov.Image) &&
                    string.IsNullOrWhiteSpace(ov.MeshFile)) mod.Overrides.Remove(ov);
            }
        }

        var entry = mod.Meshes.FirstOrDefault(
            m => m.Source.Equals(it.Mesh, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return true;
        if (entry.MeshFile.Length > 0 || entry.Textures.Count > 0)
        {
            why = $"{it.Slot} {it.Style} shows the game's piece again, but your model and textures " +
                  "are still in the mod -- delete the mesh entry if you meant to lose them";
            return true;
        }
        mod.Meshes.Remove(entry);
        return true;
    }

    // ------------------------------------------------------------------ completing an older copy

    /// <summary>
    /// Give a weapon duplicated by an EARLIER version of this tool the rest of its model.
    ///
    /// A duplicate made before <see cref="PalettesOf"/> existed copied the Base palette and no
    /// other, so its loadout still points the Barrel, Stock, Scope and Cell slots at the GAME's
    /// palettes -- which hold no entry under the copy's new tag. In the game that is a receiver
    /// floating with no barrel. The project on disk is the only record of that weapon, so the fix
    /// has to reach an existing project rather than only new ones: re-duplicating loses the name,
    /// the icon and the model the author has already put in.
    ///
    /// This adds only what is missing and touches nothing that is there, so running it on a
    /// complete weapon reports nothing to do. Same contract as <see cref="Rename"/>: with
    /// <paramref name="apply"/> false the plan is the answer and the project is untouched.
    ///
    /// The mesh repair below covers EVERY mesh in the project, not only this weapon's: a source
    /// naming the mod's own folder is broken whatever it belongs to, and the plan names the file
    /// so nothing is repaired silently.
    /// </summary>
    public static List<Change>? CompleteModel(IFileProvider provider, ModProject mod,
                                              string gearKitPartId, bool apply, out string? why)
    {
        why = null;
        if (IdentityOf(gearKitPartId) is not { } who)
        { why = $"{gearKitPartId} is not a weapon's armoury part"; return null; }

        var loadout = mod.Copies.FirstOrDefault(
            c => c.Id.Equals($"CD_{who.Class}_{who.Id}", StringComparison.OrdinalIgnoreCase));
        if (loadout is null)
        { why = $"this mod has no CD_{who.Class}_{who.Id}, so there is no loadout to complete"; return null; }
        if (string.IsNullOrWhiteSpace(loadout.Template))
        { why = $"{loadout.Id} does not say which loadout it was copied from"; return null; }

        // TWO tags, and they are the same tag for a weapon that keeps its donor's -- see
        // SetDonorTag. The DONOR's is what the game's palettes are keyed by, so it is what a
        // palette is searched for; the weapon's OWN is whatever the type part renames it to, and
        // when there is no such rename the weapon simply answers to the donor's.
        //
        // Read from the donor rather than from the project: a weapon that keeps the donor's tag
        // has no rename mentioning it, and looking there reported a weapon with nothing wrong
        // with it as one whose tag could not be found.
        var typePart = mod.Parts.FirstOrDefault(
            p => p.Id.Equals($"CPD_WP_Type_{who.Class}_{who.Id}", StringComparison.OrdinalIgnoreCase));
        if (typePart is null)
        { why = $"this mod has no CPD_WP_Type_{who.Class}_{who.Id}"; return null; }
        if (DonorTagOf(provider, TemplatePath(provider, typePart.Template)) is not { } donorTag)
        { why = $"cannot read a weapon type tag out of {Leaf(typePart.Template)}"; return null; }
        var ownTag = typePart.RenameNames.TryGetValue(donorTag, out var mapped) ? mapped : donorTag;

        var palettes = PalettesOf(provider, TemplatePath(provider, loadout.Template), donorTag,
                                  TemplatePath(provider, typePart.Template), out why);
        if (palettes is null) return null;

        var modFolder = FolderFor(mod);
        var have = mod.Copies.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<Change>();
        var toAdd = new List<(SlotPalette Palette, string CopyId)>();
        foreach (var palette in palettes)
        {
            var copyId = PaletteCopyId(palette.Leaf, who.Id);
            if (have.Contains(copyId)) continue;
            toAdd.Add((palette, copyId));
            // "Barrel", from br.Customization.Slot.Weapon.Barrel -- the name of the piece that is
            // missing, which is the only part of this a person needs to read.
            var slot = palette.Slot.Length > 0
                ? palette.Slot[(palette.Slot.LastIndexOf('.') + 1)..] : palette.Leaf;
            changes.Add(new Change($"{slot} needs its own palette", palette.Leaf, copyId));
        }

        // A MESH ENTRY NAMING THE MOD'S OWN LAST BUILD. `meshes[].source` is the GAME asset to
        // copy, and a duplicate made while the mod was installed got the mod's own copy instead --
        // the Studio mounts the game's whole folder, mods and all. The build then says "not in any
        // mounted container", skips it, and the weapon ships with no model of its own. Generation
        // no longer does this (see PackageOfLeaf); projects written before it still carry it.
        var meshFixes = new List<(ModMesh Mesh, string To)>();
        foreach (var mesh in mod.Meshes)
        {
            if (string.IsNullOrWhiteSpace(mesh.Source)) continue;
            var leaf = Leaf(mesh.Source);
            var pristine = PackageOfLeaf(provider, leaf);
            if (pristine is null ||
                pristine.Equals(mesh.Source, StringComparison.OrdinalIgnoreCase)) continue;
            meshFixes.Add((mesh, pristine));
            changes.Add(new Change($"{leaf} is copied from the mod itself", mesh.Source, pristine));
        }

        if (changes.Count == 0)
        { why = "this weapon already copies every palette its loadout names"; return null; }
        if (!apply) return changes;

        foreach (var (mesh, to) in meshFixes)
        {
            // The palette's re-point keyed off the old source too, so moving one without the other
            // leaves the copy pointing at the game's mesh instead of the mod's.
            foreach (var copy in mod.Copies)
                if (copy.RenameNames.TryGetValue(mesh.Source, out var pointsAt))
                {
                    copy.RenameNames.Remove(mesh.Source);
                    copy.RenameNames[to] = pointsAt;
                }
            mesh.Source = to;
        }

        foreach (var (palette, copyId) in toAdd)
        {
            var copy = new ModCopy { Id = copyId, Template = palette.Package, NewGuid = true };
            // Nothing to re-key when the weapon answers to the donor's tag: the entry already
            // there IS its entry.
            if (!ownTag.Equals(donorTag, StringComparison.Ordinal)) copy.RenameNames[donorTag] = ownTag;
            mod.Copies.Add(copy);
            // A soft object path is two names, so the loadout names the palette twice.
            loadout.RenameNames[palette.Package] = modFolder + copyId;
            loadout.RenameNames[palette.Leaf] = copyId;
        }
        return changes;
    }

    /// <summary>Where every weapon type tag starts, and how one is told from any other name.</summary>
    public const string TypeTagPrefix = "br.Customization.Part.Weapon.Type.";

    /// <summary>Every name in a package, or null if it cannot be read.</summary>
    private static HashSet<string>? NamesOf(IFileProvider provider, string package)
    {
        var copied = AssetCopy.PristineRead(provider, package, out _);
        var raw = copied?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null) return null;
        try { return new HashSet<string>(ZenPackage.Read(raw).Names, StringComparer.Ordinal); }
        catch { return null; }
    }

    /// <summary>
    /// The package whose asset name is exactly <paramref name="leaf"/>, IN THE PRISTINE GAME.
    ///
    /// A weapon's folder does not follow from its class -- Rifle lives under Blaster_Rifle, and
    /// pistols are split across a light and a heavy folder -- so the path is looked up rather than
    /// assembled out of a guess that would be right most of the time.
    ///
    /// Pristine because the answer is written into a project as the asset to COPY. Searching every
    /// mounted container instead found a mod's own copy of the mesh -- the Studio mounts the game's
    /// whole folder, mods and all -- and wrote a project that names its own last build as its
    /// template. See <see cref="AssetCopy.PristineFiles"/>.
    /// </summary>
    private static string? PackageOfLeaf(IFileProvider provider, string leaf) =>
        AssetCopy.PristinePackageOfLeaf(provider, leaf, ModBuild.ProjectName);

    /// <summary>
    /// A template named either way, as a package path.
    ///
    /// A project written BY HAND names its templates by bare asset name -- "CPD_WP_Type_Longarm_
    /// IQA-11" -- because that is what a person types and what <see cref="ModBuild.TemplateFor"/>
    /// resolves at build time. A project the Studio generates writes the full "/Game/..." path.
    /// Both are valid and the DLT-20A, which is in the game and correct, is the first kind.
    ///
    /// Anything here that READS a template has to accept both, and the first version of the type
    /// switch did not: it refused the one hand-written weapon in the game with "cannot read a
    /// weapon type tag", which is true of the string it was given and says nothing about the
    /// weapon. See [[audit-the-neighbours]] -- generated and hand-written differ, and the code that
    /// only ever saw one of them is where that difference bites.
    /// </summary>
    public static string TemplatePath(IFileProvider provider, string template) =>
        template.StartsWith('/') ? template : PackageOfLeaf(provider, template) ?? template;

    private static string Leaf(string package) => package[(package.LastIndexOf('/') + 1)..];
}

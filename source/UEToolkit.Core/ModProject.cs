using System.Text.Json;
using System.Text.Json.Nodes;

namespace UEToolkit.Core;

/// <summary>
/// A mod, as a file a person can read and a dropdown can write.
///
/// This is the seam between browsing and authoring. Everything here is in the USER's vocabulary,
/// not the engine's -- "Helmet" rather than br.Customization.Slot.Character.Outfit.Helmet.Mesh,
/// `hidesHead: true` rather than three override tags plus an alternative root plus a visor lock.
/// The builder translates. Keeping the tag names out of this file is what lets a UI offer a
/// dropdown of five slots instead of a text box someone has to get exactly right.
///
/// The file is edited in place and NOT round-tripped through a model: mod authors write comments
/// into these (the shipped one carries a `_comment` block explaining every field) and a save that
/// silently dropped them would be a data-loss bug wearing a serialiser's clothes. So the parsed
/// JsonObject is kept and only the fields that changed are rewritten.
/// </summary>
public sealed class ModProject
{
    /// <summary>
    /// The slots a part can occupy, as the UI should offer them. One entry per menu slot -- the
    /// builder also accepts the game's own labels ("Tops" for Torso, "Lower body" for Legs), but
    /// a dropdown should not offer two spellings of one thing.
    /// </summary>
    public static readonly string[] Slots =
    {
        // HEAD FIRST, because it is the person rather than what they are wearing -- and because
        // it is the newest thing here and belongs where an author will find it. A head is an
        // ordinary customization part in an ordinary slot; the only reason it was not on this list
        // is that the mesh importer could not read one until the compressed morph block could be
        // parsed. See CookedMesh.MorphFor.
        "Head",
        // THE PARTS THAT HANG OFF A HEAD, and the reason this list grew.
        //
        // 173 mesh-bearing parts sat in these three slots and NONE of them could be authored:
        // FromSlotTag did not know the tags, so every one was refused as "not a wearable outfit
        // slot" -- 100 hairstyles (92 of them open), 55 beards (all open), 18 lekku styles, which
        // cover Togruta as well as Twi'lek.
        //
        // Found while tracing a report that imported lekku come out with no physics. They were
        // reachable only sideways, through the Helmet slot, because a Twi'lek's lekku base is
        // ALSO published as a helmet part -- which is presumably what the author did.
        //
        // Their shape is the ordinary one: BitReactorUIDataFragment plus CustomizationFragmentMesh,
        // a single mesh fragment, and a species lock inherited from the template. Facial hair keys
        // its mesh options by HEAD (HF03, D01, D03...) rather than by body type, which the builder
        // handles in two ways: a part that KEEPS the game's mesh leaves every option as the
        // template has it, so each head keeps its own cut, and a part that REPLACES the mesh, chose
        // a material or gave itself textures puts that one mesh into every option, whatever the
        // variant is called. See KeepsHeadCuts and SlotLimit.
        "Hair", "FacialHair", "Lekku",
        // The rest of the same family, added once the first three were proved: horns are
        // Devaronian, antennae are Rodian, and all of them are BitReactorUIDataFragment plus one
        // CustomizationFragmentMesh with a species lock -- the ordinary shape.
        "Horns", "Antennae",
        // Eyebrows are the same family and were simply never looked at: the game ships 9 styles
        // in `...Head.Eyebrow.Mesh`, each BitReactorUIDataFragment plus ONE mesh fragment, and a
        // modder who wanted a custom brow had to borrow another slot and force the game's brow in
        // as a named template. The fragment keys its mesh options PER HEAD (29 of them): kept
        // as the game has them, each head keeps its own cut; a replaced mesh, a chosen material
        // or textures of the part's own make the builder fill every option with the one mesh --
        // see SlotLimit.
        "Eyebrows",
        "Helmet", "Torso", "Arms", "Legs", "Boots",
        // A backpack is worn like the rest and is ALSO one of the multi-region tokens, so a part
        // that dresses the whole character can carry one as a piece. See RegionForSlot.
        "Pack",
        // A DROID, not a person. Kept together and at the end because these three are the only
        // slots here that belong to a different body entirely -- an astromech has no head, torso
        // or boots, and offering them beside a helmet would read as though it did.
        "AstromechHead", "AstromechBody", "AstromechLegs",
        "Specialization", "Talent", "WeaponSpec",
        // Weapon PARTS -- the pieces a weapon is assembled from, as opposed to WeaponSpec, which is
        // the loadout entry a character chooses. A weapon's Type part carries no mesh: it is
        // metadata holding a gameplay tag, and the generic Base/Barrel/Stock/Scope/Cell parts hold
        // mesh PALETTES keyed by that tag.
        "WeaponType", "WeaponBase", "WeaponBarrel", "WeaponStock", "WeaponScope", "WeaponCell",
    };

    /// <summary>
    /// Slots whose parts carry no geometry.
    ///
    /// A specialisation is a part in exactly the sense the customization system means -- it sits in
    /// a slot, it is chosen from a menu, it is a CustomizationPartDefinition -- and it has no mesh
    /// at all. Everything mesh-shaped in the builder has to be optional for these, and asking
    /// "which slot is this" is more honest than testing whether a mesh field happens to be blank.
    /// </summary>
    /// <summary>
    /// The mesh-fragment region token a slot's own piece carries, or "".
    ///
    /// Slots are the menu's vocabulary and regions are the asset names'; they agree in meaning and
    /// not in spelling, and this is the only place that knows both. Used to pick a multi-piece
    /// part's ANCHOR -- the piece that sits on the slot the part itself occupies.
    /// </summary>
    public static string RegionForSlot(string slot) => slot.Trim().ToUpperInvariant() switch
    {
        "HELMET" => "HELM",
        "TORSO" => "TORS",
        "ARMS" => "ARMS",
        "LEGS" => "LEGS",
        "BOOTS" => "BOOT",
        "HEAD" => "HEAD",
        // A BACKPACK IS A REAL REGION. `CPD_H_Outfit_Cly_PACK` names its fragment `PACK`, and PACK
        // is one of the tokens a multi-piece outfit uses alongside BOOT/TORS/LEGS/ARMS -- so a
        // Pack part must be able to find its own piece the way a Torso part does.
        "PACK" => "PACK",
        // Hair, FacialHair, Lekku, Horns, Antennae, Eyebrows and the three astromech slots are
        // absent on purpose, not by oversight. This exists to pick the ANCHOR piece of a part
        // that carries SEVERAL mesh fragments, and every part in those slots carries exactly one
        // -- measured with --partdetail, which prints one "mesh piece" line each. Both callers
        // are already guarded by a fragment count above 1, so "" is right and is never reached
        // for them.
        _ => "",
    };

    public static bool SlotHasNoMesh(string slot) =>
        slot is "Specialization" or "Talent" or "WeaponSpec" or "WeaponType";

    /// <summary>
    /// Is "Hides the head" something a part in this slot can MEAN?
    ///
    /// The tick makes the build add an override of `...Humanoid.Head.Face`, so that the wearer's
    /// face is not drawn through the part. That is a thing an OUTFIT does -- a helmet's visor, or
    /// a suit that dresses the whole character -- and it is not a thing anything else can do:
    ///
    ///   - A HEAD is the face. Overriding the face slot from a part that sits IN the face slot is
    ///     a head hiding itself. The Devaronian heads reached the box ticked anyway, because
    ///     <see cref="PartDetails.Detail.HidesHead"/> is true for a part that overrides the HAIR
    ///     (their horns replace it), and the build then added an override the original never had.
    ///   - Hair, facial hair, eyebrows, horns, antennae and lekku are drawn ON the head. They
    ///     sit beside the face; none of them replaces it.
    ///   - The astromech slots belong to a droid, whose face is not this tag; the specialisation
    ///     and weapon slots are not worn at all.
    ///
    /// The six that are left are exactly the game's `Character.Outfit.*` slots (see
    /// <see cref="FromSlotTag"/>), which is why the line is drawn here rather than at "Helmet":
    /// a whole-character garment can sit in any of them, and a project that already ticks the box
    /// on one keeps building as it did. Of the 1,237 game parts that can be duplicated, 121 of
    /// 164 helmets take over the hair or head by tag, against 2 of 267 torsos and none of the
    /// arms, legs, boots or packs -- which is why only a helmet is SEEDED ticked, see
    /// <see cref="ModPart.FromGamePart"/>, while the box stays available across the family.
    ///
    /// ONE answer, asked by the build, the verifier and the editor, because three copies of "does
    /// this slot count?" is how the box came to mean different things in each.
    /// </summary>
    public static bool SlotCanHideHead(string slot) =>
        slot is "Helmet" or "Torso" or "Arms" or "Legs" or "Boots" or "Pack";

    /// <summary>
    /// A limit of a slot that an author should know BEFORE building, as one plain sentence, or ""
    /// when the slot has none.
    ///
    /// Eyebrows and facial hair are the two so far, and they are the same case. Their mesh options
    /// are keyed per HEAD (29 in CPD_H_Eyebrow_Thick_01, 25 in the human short beard), each cut to
    /// one head and skinned to its facial rig -- see <see cref="PartDetails.KeyedPerHead"/>, which
    /// finds them by their tags, so this list and that rule agree on exactly these two slots.
    ///
    /// A part that keeps the game's own mesh untouched keeps every head's cut (the build leaves
    /// the options alone). A part that REPLACES the mesh, or chooses a material, or gives itself
    /// textures needs a mesh of its own, and that one mesh goes into every option. Both halves are
    /// in the sentence because a modder who only renamed a brow should not be told it will look
    /// wrong, and one who replaced it should not be told it fits each head. The build says it
    /// again, as a serious note, for a choice or textures the author did not ask a mesh for.
    /// </summary>
    public static string SlotLimit(string slot) => slot switch
    {
        "Eyebrows" => PerHead("eyebrow"),
        "FacialHair" => PerHead("piece of facial hair"),
        _ => "",
    };

    private static string PerHead(string what) =>
        $"The game cuts each {what} to fit every head shape. Keep the game's mesh untouched and " +
        "every head keeps its own cut; replace the mesh, choose a material, or give it its own " +
        "textures, and one mesh is used on every head.";

    /// <summary>
    /// The friendly slot name for one of the game's slot tags, or null when the tag is not a
    /// wearable outfit slot a mod can target.
    ///
    /// Null is a real answer and must not be papered over with a default. Most of the game's
    /// 3,524 parts sit in slots this cannot author -- colours, names, presets, weapon
    /// specialisations, astromech bodies -- and quietly filing one of those under "Helmet" would
    /// produce a mod that builds, cooks, ships, and does nothing.
    /// </summary>
    public static string? FromSlotTag(string slotTag) => slotTag switch
    {
        // A head is not under Outfit -- it is the character's own appearance, and the tag says
        // so. Measured with `--partdetail CPD_H_Head`: all twelve heads report exactly this.
        "Character.Appearance.Humanoid.Head.Face.Mesh" => "Head",
        // Measured with `--parts`, which writes every part's slot tag: Hair.Hair.Mesh 100 parts,
        // Hair.Facial.Mesh 55, Lekku.Mesh 18. The doubled word in the hair tag is the game's, not
        // a typo here. Lekku.Style is the two BASE parts other lekku derive from -- it carries a
        // mesh like the rest, so it is authorable too.
        "Character.Hair.Hair.Mesh" => "Hair",
        "Character.Hair.Facial.Mesh" => "FacialHair",
        "Character.Lekku.Mesh" => "Lekku",
        "Character.Lekku.Style" => "Lekku",
        "Character.Horns.Mesh" => "Horns",
        "Character.Antennae.Mesh" => "Antennae",
        // Measured: `--props Human/Eyebrow/BP_CPD_H_Eyebrow.uasset` shows this as AllowedSlots[0],
        // and `--parts` lists 11 parts under it. The seven species containers sit one level up,
        // at `...Head.Eyebrow` with no `.Mesh`, and are deliberately not this slot.
        "Character.Appearance.Humanoid.Head.Eyebrow.Mesh" => "Eyebrows",
        "Character.Outfit.Pack.Mesh" => "Pack",
        "Character.Appearance.Astromech.Head.Mesh" => "AstromechHead",
        "Character.Appearance.Astromech.Body.Mesh" => "AstromechBody",
        "Character.Appearance.Astromech.Legs.Mesh" => "AstromechLegs",
        "Character.Outfit.Helmet.Mesh" => "Helmet",
        "Character.Outfit.Torso.Mesh" => "Torso",
        "Character.Outfit.Arms.Mesh" => "Arms",
        "Character.Outfit.Legs.Mesh" => "Legs",
        "Character.Outfit.Boots.Mesh" => "Boots",
        // The specialisation family. These are the parts behind the SPECIALIZATION, TALENT and
        // WEAPON CLASS tabs -- no geometry, but parts like any other.
        "Character.Specializations.Tactical.Primary" => "Specialization",
        "Character.Specializations.Talent" => "Talent",
        "Character.Specializations.Weapon" => "WeaponSpec",
        "Weapon.Type" => "WeaponType",
        "Weapon.Base" => "WeaponBase",
        "Weapon.Barrel" => "WeaponBarrel",
        "Weapon.Stock" => "WeaponStock",
        "Weapon.Scope" => "WeaponScope",
        "Weapon.Cell" => "WeaponCell",
        _ => null,
    };

    /// <summary>
    /// The inverse: the game's slot tag for a friendly name. Used when going the other way --
    /// finding a game part to author FROM, which has to be in the same slot or its override slots
    /// and tags will be wrong.
    /// </summary>
    public static string? ToSlotTag(string slot) => slot switch
    {
        "Head" => "Character.Appearance.Humanoid.Head.Face.Mesh",
        // Lekku maps back to Lekku.Mesh, the 18-part slot, and not to the 2-part Lekku.Style the
        // bases sit in -- this direction is used to FIND a part to author from, and the styles are
        // what an author means.
        "Hair" => "Character.Hair.Hair.Mesh",
        "FacialHair" => "Character.Hair.Facial.Mesh",
        "Lekku" => "Character.Lekku.Mesh",
        "Horns" => "Character.Horns.Mesh",
        "Antennae" => "Character.Antennae.Mesh",
        "Eyebrows" => "Character.Appearance.Humanoid.Head.Eyebrow.Mesh",
        "Pack" => "Character.Outfit.Pack.Mesh",
        "AstromechHead" => "Character.Appearance.Astromech.Head.Mesh",
        "AstromechBody" => "Character.Appearance.Astromech.Body.Mesh",
        "AstromechLegs" => "Character.Appearance.Astromech.Legs.Mesh",
        "Helmet" => "Character.Outfit.Helmet.Mesh",
        "Torso" => "Character.Outfit.Torso.Mesh",
        "Arms" => "Character.Outfit.Arms.Mesh",
        "Legs" => "Character.Outfit.Legs.Mesh",
        "Boots" => "Character.Outfit.Boots.Mesh",
        "Specialization" => "Character.Specializations.Tactical.Primary",
        "Talent" => "Character.Specializations.Talent",
        "WeaponSpec" => "Character.Specializations.Weapon",
        "WeaponType" => "Weapon.Type",
        "WeaponBase" => "Weapon.Base",
        "WeaponBarrel" => "Weapon.Barrel",
        "WeaponStock" => "Weapon.Stock",
        "WeaponScope" => "Weapon.Scope",
        "WeaponCell" => "Weapon.Cell",
        _ => null,
    };

    public string FilePath { get; private set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// A picture for this mod's card, as a file in ModSource/Icons.
    ///
    /// The card picks one up on its own -- the mod's own artwork if a part has some, else the
    /// icon of something it was copied from, marked as borrowed. That is a good default and it is
    /// not a choice, and the author went looking for the choice: "you'll have to explain to me how
    /// someone adds a photo to their mod in the mods panel, i can't figure it out." There was
    /// nothing to find.
    ///
    /// It belongs to the PROJECT, not to the built mod: nothing here is shipped, and the game
    /// never sees it. It is what the shelf looks like.
    /// </summary>
    public string Image { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public string Description { get; set; } = "";

    /// <summary>
    /// Ship a UE4SS script that recruits this mod's characters straight from their pawn class --
    /// see <see cref="RecruitScript"/> for why the hiring pool cannot do it. The two keys are
    /// the script's: one recruits the next character, the other re-applies definitions and team.
    /// </summary>
    public bool RecruitByScript { get; set; }
    public string RecruitKey { get; set; } = RecruitScript.DefaultRecruitKey;
    public string RepairKey { get; set; } = RecruitScript.DefaultRepairKey;

    /// <summary>
    /// The base name of the three files this mod builds into -- its own name, made safe for a
    /// filename, with `_P` on the end.
    ///
    /// That suffix is not decoration and neither is deriving this from the mod's name:
    ///
    ///   _P            Unreal mounts a `_P` container at patch priority, which is how a mod wins a
    ///                 lookup against the game's own copy of a package. It is also what
    ///                 <see cref="ModBuild.PristineRegistry"/> looks for when it skips past
    ///                 deployed mods to find the game's real registry -- so a mod WITHOUT it gets
    ///                 read back in on the next build and stacks a second copy of every part.
    ///
    ///   the mod name  The container id is derived from this string, so a fixed name would give
    ///                 every mod this tool builds the same id, and two of them could never be
    ///                 installed together. Until now the app passed one hardcoded string -- and it
    ///                 was the COOKED reference mod's name, so building from the app quietly
    ///                 produced files that would overwrite it.
    /// </summary>
    public string ContainerName
    {
        get
        {
            var safe = new string((string.IsNullOrWhiteSpace(Name) ? "Mod" : Name)
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c)
                .ToArray()).Trim('_');
            if (safe.Length == 0) safe = "Mod";
            return safe.EndsWith("_P", StringComparison.Ordinal) ? safe : safe + "_P";
        }
    }

    /// <summary>
    /// Where this mod's packages live, relative to /Game/Game/. Optional; the default keeps every
    /// mod in one folder, which is fine alone and a collision risk once two mods are installed.
    ///
    /// It is NOT free-form. The game's DefaultGame.ini registers CustomizationPartDefinition to be
    /// scanned under /Game/Game/Customizations (recursively), so a part outside that tree is never
    /// looked at -- it ships, mounts, and simply never appears, with no error anywhere.
    /// </summary>
    public string Folder { get; set; } = DefaultFolder;

    /// <summary>The default mod folder, and the one the cooked mod used.</summary>
    public const string DefaultFolder = "Customizations/Mods";

    /// <summary>
    /// The roots the game scans for parts, from its own DefaultGame.ini. Anything not under one of
    /// these is invisible however correctly it is built.
    /// </summary>
    public static readonly string[] ScannedRoots =
        { "Customizations", "__PendingDelete/Game/Customizations" };

    /// <summary>
    /// What Unreal refuses in a long package name (its INVALID_LONGPACKAGE_CHARACTERS): the space
    /// and ,'&amp;!~@#:*?"&lt;&gt;| plus the newline, carriage return and tab -- the dot is
    /// checked beside them, for the reason in <see cref="FolderProblem"/>.
    /// </summary>
    private const string InvalidPackageCharacters = " ,'&!~@#:*?\"<>|\n\r\t";

    private static string NameOfFolderCharacter(char c) => c switch
    {
        ' ' => "a space",
        '.' => "a dot (.)",
        ',' => "a comma (,)",
        '\'' => "an apostrophe (')",
        '\n' => "a newline",
        '\r' => "a carriage return",
        '\t' => "a tab",
        _ => $"the character '{c}'",
    };

    /// <summary>The folder as an author can read it in a message: a tab or a line break shown as its escape.</summary>
    private static string Readable(string folder) =>
        folder.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>Why a folder cannot be used, or null when it can.</summary>
    public static string? FolderProblem(string folder)
    {
        var clean = folder.Replace('\\', '/').Trim('/');
        if (clean.Length == 0) return "the folder is empty";
        if (clean.Contains("..")) return "the folder must not climb out with ..";

        // A CHARACTER UNREAL DOES NOT ALLOW IN A PACKAGE PATH, refused by name before the build.
        //
        // The folder becomes part of every package name the mod writes (/Game/Game/<folder>/...).
        // Unreal's own long-package-name rules reject a space, and ,'&!~@#:*?"<>| (its
        // INVALID_LONGPACKAGE_CHARACTERS); a '.' is no better, because an object path is
        // <package>.<object> and a package name that holds a dot is cut at it -- by this build's own
        // PackageOf as much as by the engine -- so a folder like MyMod_v1.2 points the mod's
        // repaints at a truncated package. Checked here, as a validator, because renaming the
        // folder is the author's cheap fix and quietly changing how packages are named would move
        // the id of every mod already built.
        foreach (var c in clean)
            if (c == '.' || InvalidPackageCharacters.Contains(c))
                return $"the folder '{Readable(clean)}' contains {NameOfFolderCharacter(c)}, which cannot be used in a " +
                       "package path" +
                       (c == '.' ? " (a dot ends the package name there, so the mod's assets would be cut short)" : "") +
                       " -- rename the folder using letters, digits, _ and -";
        foreach (var root in ScannedRoots)
            if (clean.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return null;
        return $"'{clean}' is outside the folders the game scans for parts " +
               $"({string.Join(", ", ScannedRoots.Select(r => "/Game/Game/" + r))}); " +
               "a part there would install and never appear";
    }

    public List<ModPart> Parts { get; } = new();

    /// <summary>
    /// Assets of the game's own that this mod ships a changed copy of, at their original paths.
    ///
    /// Separate from <see cref="Parts"/> because the two are opposites. A part is new: it gets a
    /// package name of the mod's own, and the worst it can do is not appear. An override replaces
    /// something that already works, so its failure mode is a game asset made corrupt -- which is
    /// why it is declared explicitly rather than inferred, and why the installer treats an
    /// UNdeclared collision with a game container as an error rather than as this.
    /// </summary>
    /// <summary>
    /// Whether this project would build anything at all.
    ///
    /// A part or an override -- either alone is a whole mod. Asking only about parts is what left
    /// the panel unable to build a hero unlock.
    /// </summary>
    /// <remarks>
    /// EnemyStats counts even though it is empty of copies and overrides in the FILE: those are
    /// what it expands into at build time, so a mod that only rebalances enemies has content by
    /// every measure except the one this list was originally written against.
    ///
    /// MISSIONS ARE THE SAME, AND WERE FORGOTTEN ANYWAY -- one feature after the remark above was
    /// written about exactly this. A mission-only mod read as "nothing in it yet", which greys out
    /// Build and install with no explanation; the author's report was "the button just don't
    /// work". **EVERY NEW LIST ON THIS CLASS HAS TO BE ADDED HERE AND TO
    /// <see cref="ShipsIntoItsOwnFolder"/>**, because nothing fails until someone builds a mod
    /// that holds only the new thing -- and the person who adds the list is never that someone.
    /// </remarks>
    public bool HasContent =>
        Parts.Count > 0 || Overrides.Count > 0 || Specs.Count > 0 || Copies.Count > 0 ||
        Meshes.Count > 0 || Icons.Count > 0 || EnemyStats.Count > 0 || Missions.Count > 0 ||
        TableOverlays.Count > 0 || SlotSets.Count > 0 ||
        // I-1: a slotSets Load could not read is still something in the project -- if it did not
        // count here, a mod with only a malformed slotSets would read as "nothing in it yet" and
        // never reach the build step that refuses it by name.
        SlotSetsUnreadable;

    /// <summary>
    /// Whether this mod puts any package of its own under <see cref="Folder"/>.
    ///
    /// <see cref="HasContent"/> without the overrides, and the difference is the whole point: an
    /// OVERRIDE ships at the GAME's package path, not in the mod's folder, so the folder cannot
    /// make it invisible. An override-only mod was refused outright by
    /// <see cref="FolderProblem"/> -- "a part there would install and never appear" -- about a part
    /// it does not have, in a folder it never writes to. That refusal is the reason a shop-slot
    /// takeover could not be built even after the machinery for it existed.
    /// </summary>
    public bool ShipsIntoItsOwnFolder =>
        Parts.Count > 0 || Specs.Count > 0 || Copies.Count > 0 ||
        Meshes.Count > 0 || Icons.Count > 0 || Missions.Count > 0 ||
        TableOverlays.Count > 0;

    /// <summary>
    /// Specializations this mod authors. See <see cref="ModSpec"/> for why they are not parts.
    /// </summary>
    public List<ModSpec> Specs { get; } = new();

    /// <summary>
    /// EVERY FILE ON DISK THIS PROJECT NAMES, in one place, because two walks of this graph will
    /// drift and the drift is invisible.
    ///
    /// `--modimages` grew its own copy of this walk and already carries the warning in its own
    /// comment -- "the first version walked Parts alone... A check that silently covers a subset
    /// is worse than no check." It was still missing three the BUILD resolves: `ModCopy.Icon`,
    /// `ModSpec.Icon`, and the materials on nested `AddArcMissions`. So the check that exists to
    /// prove every reference resolves could not see three kinds of reference at all.
    ///
    /// THE GROUND TRUTH IS <see cref="ModBuild.FindImage"/>'s CALL SITES. That one function
    /// resolves icons, textures and meshes alike, so whatever the build passes it is exactly what
    /// a project uses and exactly what a migration has to copy. This walk is written from that
    /// list rather than from the field names, which is how `TintMask` stayed out: the build
    /// resolves it with `PackageOf`, not `FindImage` -- it names a game asset, not a file.
    ///
    /// FILES ONLY. An icon may be a game OBJECT PATH ("T_Thumb_X.T_Thumb_X") with nothing on disk
    /// behind it; that is not a miss and must not be reported as one.
    /// </summary>
    public SortedSet<string> ArtReferences()
        => new(ArtReferencesByKind().Select(r => r.Reference), StringComparer.OrdinalIgnoreCase);

    /// <summary>Which of `ModSource/<project>/` subfolders a reference belongs in.</summary>
    public enum ArtKind { Icons, Meshes, Textures }

    /// <summary>
    /// The same walk, keeping WHICH FIELD each reference came from.
    ///
    /// The flat name set is enough to ask "does this resolve"; it is not enough to MOVE anything,
    /// because `ModSource/<project>/` is split into Icons, Meshes and Textures and the file name
    /// alone cannot say which. A `.tga` is a texture when a material names it and an icon when a
    /// part does, and the same file can be both.
    /// </summary>
    public List<(string Reference, ArtKind Kind)> ArtReferencesByKind()
    {
        var found = new List<(string, ArtKind)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Want(string? v, ArtKind kind)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var ext = System.IO.Path.GetExtension(v);
            if (!ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".fbx", StringComparison.OrdinalIgnoreCase)) return;
            if (seen.Add(v)) found.Add((v, kind));
        }
        void Paint(IEnumerable<ModMaterial> ms)
        {
            foreach (var m in ms)
                foreach (var t in m.Textures.Values) Want(t, ArtKind.Textures);
        }

        foreach (var part in Parts)
        {
            Want(part.Icon, ArtKind.Icons);
            Want(part.MeshFile, ArtKind.Meshes);
            Paint(part.Materials);
            foreach (var r in part.Regions) { Want(r.MeshFile, ArtKind.Meshes); Paint(r.Materials); }
        }
        foreach (var m in Meshes) { Want(m.MeshFile, ArtKind.Meshes); Paint(m.Materials); }
        foreach (var i in Icons) Want(i.Image, ArtKind.Icons);
        foreach (var c in Copies) Want(c.Icon, ArtKind.Icons);      // ModBuild.cs:4120 resolves this
        foreach (var sp in Specs) Want(sp.Icon, ArtKind.Icons);     // ModBuild.cs:4630 resolves this
        foreach (var o in Overrides)
        {
            Want(o.Image, ArtKind.Icons);
            Want(o.MeshFile, ArtKind.Meshes);
            // AND AN OVERRIDE'S OWN PAINT. `ModOverride.Materials` exists because "doing
            // replacement you are forced to only the material slots that the game model had" --
            // an override can carry textures exactly as a part can, and they are files on disk
            // like any other. I first wrote this as a recursion over nested arc missions, which
            // was a type that does not carry materials at all; the compiler caught it, which a
            // migration reading its own hand-written field list would not have.
            Paint(o.Materials);
        }
        return found;
    }

    /// <summary>Assets this mod duplicates. See <see cref="ModCopy"/>.</summary>
    public List<ModCopy> Copies { get; } = new();

    /// <summary>Meshes this mod makes its own copy of. See <see cref="ModMesh"/>.</summary>
    public List<ModMesh> Meshes { get; } = new();

    /// <summary>Icons of the mod's own: a tag it defines and the picture it draws. See <see cref="ModIcon"/>.</summary>
    public List<ModIcon> Icons { get; } = new();

    /// <summary>
    /// Gameplay tags this mod INVENTS, shipped in the plugin's own config so the game registers
    /// them.
    ///
    /// A mod that borrows a live weapon's type tag drags that weapon's whole parts list onto its
    /// model; a mod that borrows a CUT weapon's runs out when the cut weapons do. Minting one is
    /// the way out, and it works -- but only if the tag travels WITH the mod. The first one was
    /// typed into the installed folder by hand, which meant the same mod on anyone else's machine
    /// would have carried a tag that did not exist.
    /// </summary>
    public List<string> Tags { get; } = new();

    public List<ModOverride> Overrides { get; } = new();

    /// <summary>Enemy groups this mod rebalances. See <see cref="ModEnemyStats"/>.</summary>
    public List<ModEnemyStats> EnemyStats { get; } = new();

    /// <summary>Missions this mod adds to the galaxy map. See <see cref="ModMission"/>.</summary>
    public List<ModMission> Missions { get; } = new();

    /// <summary>
    /// Game tables this mod LAYS OVER rather than rewrites. See <see cref="ModTableOverlay"/>.
    ///
    /// Additive on purpose: a project saved before this existed reads back with an empty list and
    /// nothing else about it changes, which is the whole reason it is a list of its own rather
    /// than a new shape of <see cref="ModOverride"/>.
    /// </summary>
    public List<ModTableOverlay> TableOverlays { get; } = new();

    /// <summary>
    /// Slots this mod adds to parts -- see <see cref="ModSlotSet"/> and the add-a-slot spec.
    /// In <see cref="HasContent"/> and deliberately NOT in <see cref="ShipsIntoItsOwnFolder"/>: a set
    /// lands in packages its targets already ship -- an override at the game's path, or a part or
    /// copy counted there already -- so it never puts a package of its own under the folder.
    /// </summary>
    public List<ModSlotSet> SlotSets { get; } = new();

    /// <summary>
    /// I-1: true when the top-level `slotSets` key is PRESENT but not shaped as an array of objects
    /// -- an object instead of an array (a natural slip), or an array holding something that is not
    /// an object. Load leaves <see cref="SlotSets"/> empty rather than guess at a partial reading:
    /// this flag is what tells Save to keep the raw value untouched instead of deleting it as "no
    /// slot sets", <see cref="HasContent"/> to still count the project as having something in it,
    /// and <see cref="SlotSets.Resolve"/> to refuse the build by name.
    /// </summary>
    public bool SlotSetsUnreadable { get; private set; }

    /// <summary>The file exactly as it was read, so anything not modelled here survives a save.</summary>
    private JsonObject _root = new();

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    public static ModProject Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>
    /// The same read, from TEXT rather than from a file.
    ///
    /// UNDO is what this is for. The editor can restore a project by re-reading a string it kept
    /// before an edit, which needs no per-action inverse and no knowledge of what changed --
    /// every field comes back because every field went out. `--modroundtrip` is the standing
    /// proof that Load -> Serialize -> Load loses nothing, so a snapshot is worth exactly what
    /// the file on disk is worth.
    ///
    /// <paramref name="path"/> is where the project BELONGS, not where the text came from: a
    /// restored project has to keep saving over the same file.
    /// </summary>
    public static ModProject Parse(string text, string path)
    {
        var root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a JSON object");

        var mod = new ModProject
        {
            FilePath = path,
            _root = root,
            Name = Str(root, "name") ?? Path.GetFileNameWithoutExtension(path),
            DisplayName = Str(root, "displayName") ?? "",
            Image = Str(root, "image") ?? "",
            Author = Str(root, "author") ?? "",
            Version = Str(root, "version") ?? "1.0",
            Description = Str(root, "description") ?? "",
            RecruitByScript = root["recruitByScript"] is JsonValue rbs && rbs.TryGetValue<bool>(out var rbsOn) && rbsOn,
            RecruitKey = Str(root, "recruitKey") ?? RecruitScript.DefaultRecruitKey,
            RepairKey = Str(root, "repairKey") ?? RecruitScript.DefaultRepairKey,
            Folder = Str(root, "folder") is { Length: > 0 } f ? f.Replace('\\', '/').Trim('/') : DefaultFolder,
        };

        if (root["parts"] is JsonArray arr)
            foreach (var node in arr)
                if (node is JsonObject o)
                    mod.Parts.Add(ModPart.From(o));

        if (root["tags"] is JsonArray tgs)
            foreach (var node in tgs)
                if (node?.GetValue<string>() is { Length: > 0 } tag) mod.Tags.Add(tag);

        if (root["meshes"] is JsonArray mss)
            foreach (var node in mss)
                if (node is JsonObject o)
                    mod.Meshes.Add(ModMesh.From(o));

        if (root["icons"] is JsonArray ics)
            foreach (var node in ics)
                if (node is JsonObject o)
                    mod.Icons.Add(ModIcon.From(o));

        if (root["copies"] is JsonArray cps)
            foreach (var node in cps)
                if (node is JsonObject o)
                    mod.Copies.Add(ModCopy.From(o));

        if (root["specs"] is JsonArray sps)
            foreach (var node in sps)
                if (node is JsonObject o)
                    mod.Specs.Add(ModSpec.From(o));

        if (root["overrides"] is JsonArray ovr)
            foreach (var node in ovr)
                if (node is JsonObject o)
                    mod.Overrides.Add(ModOverride.From(o));

        if (root["enemyStats"] is JsonArray ens)
            foreach (var node in ens)
                if (node is JsonObject o)
                    mod.EnemyStats.Add(ModEnemyStats.From(o));

        if (root["missions"] is JsonArray msnRead)
            foreach (var node in msnRead)
                if (node is JsonObject o)
                    mod.Missions.Add(ModMission.From_(o));

        if (root["tableOverlays"] is JsonArray tovRead)
            foreach (var node in tovRead)
                if (node is JsonObject o)
                    mod.TableOverlays.Add(new ModTableOverlay
                    {
                        Target = (string?) o["target"] ?? "",
                        Row = (string?) o["row"] ?? "",
                        Field = (string?) o["field"] ?? "",
                        Was = (double?) o["was"] ?? 0,
                        Now = (double?) o["now"] ?? 0,
                    });

        // I-1: "slotSets" is unreadable as a WHOLE, not element by element, exactly like a set's
        // own "slots" key (ModSlotSet.From_) -- an object instead of an array, or an array holding
        // anything that is not an object, is a shape this Studio cannot half-trust. The old code
        // read a JsonArray and silently skipped a non-object element, or silently did nothing at
        // all for a non-array value -- either way SlotSets stayed empty with no record that
        // anything was even there, and the next Save() erased it (see SlotSetsUnreadable).
        if (root["slotSets"] is { } ssNode)
        {
            if (ssNode is JsonArray ssRead && ssRead.All(x => x is JsonObject))
                foreach (var node in ssRead)
                    mod.SlotSets.Add(ModSlotSet.From_((JsonObject) node!));
            else
                mod.SlotSetsUnreadable = true;
        }

        return mod;
    }

    /// <summary>
    /// Point this project at a different file, after its old one has been renamed.
    ///
    /// FilePath is otherwise write-once, because everything that saves goes through it and a
    /// project whose path can drift writes somewhere nobody is looking. See ModLibrary.Rename,
    /// which is the only thing that should call this.
    /// </summary>
    public void MoveTo(string path) => FilePath = path;

    public static ModProject Create(string path, string name)
    {
        var mod = new ModProject { FilePath = path, Name = name, DisplayName = name, Version = "1.0" };

        // A NEW mod gets a folder of its own, rather than the shared default.
        //
        // Two mods that both take the default put their parts at the same package paths, and a
        // part id used by both then collides in the game -- one wins, and which one is not worth
        // guessing at. Giving each new mod its own subfolder makes that impossible by default
        // instead of leaving it to a field nobody has a reason to touch.
        //
        // Set on CREATE and not as the default, deliberately: changing the default would move the
        // packages of every existing mod that never wrote the field, and a package that moves gets
        // a new id -- so mods that already work would quietly become different mods.
        var leaf = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (leaf.Length > 0) mod.Folder = DefaultFolder + "/" + leaf;
        // A new file gets the same explanatory block the hand-written one carries, because the
        // next person to open it in a text editor should find the vocabulary documented there
        // rather than only in a tool they may not be running.
        mod._root = new JsonObject
        {
            ["_comment"] = new JsonArray(
                $"Written by {Branding.Product}. Safe to edit by hand -- comments and unknown fields are kept.",
                "",
                "folder        optional, relative to /Game/Game/. Must sit under Customizations/",
                "              -- that is the only tree the game scans for parts.",
                "slot          Helmet | Torso | Arms | Legs | Boots",
                "mesh          full object path of a skeletal mesh, e.g. /Game/X/SK_Y.SK_Y",
                "icon          a PNG or TGA in ModSource/Icons, or a game texture's object path",
                "forCharacter  null = offered to everyone. A name = that character only.",
                "hidesHead     helmets need this or the face renders through the visor",
                "animClass     optional post-process anim blueprint",
                "template      the game part to build this one from, by name. Blank = choose one.",
                "              Name it when the automatic choice inherits the wrong behaviour --",
                "              a clone helmet needs a CLONE template or it loses the voice filter.",
                "ownMesh       true = carry a copy of the mesh, to edit rather than to point at",
                "import        an FBX to put in place of the template's geometry. Implies ownMesh.",
                "meshBasis     how the FBX's axes map onto Unreal's, e.g. \"XZY\" or \"-X-ZY\".",
                "meshScale     centimetres per FBX unit. Blank uses what was measured for this game.",
                "textures      your own artwork: { \"D\": \"MyHelmet_D.tga\", \"N\": \"MyHelmet_N.tga\" }",
                "takesOver     slots the part this was built from CLAIMS from the rest of the",
                "              outfit, and whether yours still does: { \"br...Boots.Mesh\": false }.",
                "              Clone legs replace the wearer's BOOTS -- false leaves them alone.",
                "              Only false is meaningful; a missing entry is \"as the template\".",
                "hidesFromMenu same shape, for the colour swatches the template takes away.",
                "              false gives one back. Its material may do nothing with it.",
                "slotName      inside \"materials\": what the mesh calls that slot, e.g. \"MI_PACK\".",
                "              A tint is aimed at a slot BY NAME, so slots sharing one are painted",
                "              together. Blank derives it from the material, which is what the",
                "              game's own meshes do -- set it only when that comes out wrong.",
                "",
                "overrides     a LIST, beside \"parts\". Each entry ships a changed copy of one of",
                "              the GAME's own assets, at that asset's own path:",
                "                { \"target\": \"/Game/...\", \"repointTag\": {",
                "                    \"from\": \"a.tag.the.asset.names\", \"to\": \"a.tag.to.name.instead\",",
                "                    \"expect\": 2 } }",
                "              expect is how many references you mean to change. A different count",
                "              means the edit found something else, and the build refuses.",
                "              The key names which of the part's textures it replaces -- the",
                "              suffix (D, MSK, N), the texture's asset name, or the material",
                "              parameter. Implies ownMesh."),
        };
        return mod;
    }

    /// <summary>
    /// Exactly what <see cref="Save"/> would write, without writing it.
    ///
    /// Comparing this against what was last written is how the editor knows there is unsaved work.
    /// The alternative -- a dirty flag set by every setter -- is a list of places to remember, and
    /// the one that gets forgotten is the one that loses somebody's afternoon.
    /// </summary>
    public string Serialize()
    {
        _root["name"] = Name;
        _root["displayName"] = DisplayName;
        if (Image.Length > 0) _root["image"] = Image; else _root.Remove("image");
        _root["author"] = Author;
        _root["version"] = Version;
        _root["description"] = Description;
        // The recruit script, only when asked for: an ordinary mod file says nothing about it.
        if (RecruitByScript)
        {
            _root["recruitByScript"] = true;
            _root["recruitKey"] = RecruitKey;
            _root["repairKey"] = RepairKey;
        }
        else { _root.Remove("recruitByScript"); _root.Remove("recruitKey"); _root.Remove("repairKey"); }
        // Only written when it is not the default, so an ordinary mod file stays as short as the
        // person who wrote it left it.
        if (!Folder.Equals(DefaultFolder, StringComparison.OrdinalIgnoreCase)) _root["folder"] = Folder;
        else _root.Remove("folder");

        var arr = new JsonArray();
        foreach (var p in Parts) arr.Add(p.ToJson());
        _root["parts"] = arr;

        if (Tags.Count > 0)
        {
            var tgs = new JsonArray();
            foreach (var t in Tags) tgs.Add(t);
            _root["tags"] = tgs;
        }
        else _root.Remove("tags");

        if (Icons.Count > 0)
        {
            var ics = new JsonArray();
            foreach (var i in Icons) ics.Add(i.ToJson());
            _root["icons"] = ics;
        }
        else _root.Remove("icons");

        if (Meshes.Count > 0)
        {
            var mss = new JsonArray();
            foreach (var m in Meshes) mss.Add(m.ToJson());
            _root["meshes"] = mss;
        }
        else _root.Remove("meshes");

        if (Copies.Count > 0)
        {
            var cps = new JsonArray();
            foreach (var c in Copies) cps.Add(c.ToJson());
            _root["copies"] = cps;
        }
        else _root.Remove("copies");

        // Empty is ABSENT, the same as every other list here -- a mod that opened the enemy screen
        // and changed nothing must not grow a key that says it did.
        if (EnemyStats.Count > 0)
        {
            var ens = new JsonArray();
            foreach (var e in EnemyStats) ens.Add(e.To());
            _root["enemyStats"] = ens;
        }
        else _root.Remove("enemyStats");

        // Same rule as every list here: absent when empty, so a mod that opened the missions
        // screen and added nothing does not grow a key that says it did.
        if (Missions.Count > 0)
        {
            var mss = new JsonArray();
            foreach (var m in Missions) mss.Add(m.To());
            _root["missions"] = mss;
        }
        else _root.Remove("missions");

        if (TableOverlays.Count > 0)
        {
            var tov = new JsonArray();
            foreach (var o in TableOverlays)
                tov.Add(new JsonObject
                {
                    ["target"] = o.Target,
                    ["row"] = o.Row,
                    ["field"] = o.Field,
                    ["was"] = o.Was,
                    ["now"] = o.Now,
                });
            _root["tableOverlays"] = tov;
        }
        else _root.Remove("tableOverlays");

        if (SlotSetsUnreadable)
        {
            // I-1: KEEP THE RAW VALUE UNTOUCHED. Load could not read it as an array of slot-set
            // objects, so nothing here was modelled -- rebuilding the key from an empty SlotSets
            // list (the branch below) would delete whatever the author, or a newer Studio, actually
            // wrote. _root already carries it verbatim from the read; simply never touch the key.
        }
        else if (SlotSets.Count > 0)
        {
            var ss = new JsonArray();
            foreach (var s in SlotSets) ss.Add(s.To());
            _root["slotSets"] = ss;
        }
        else _root.Remove("slotSets");

        if (Specs.Count > 0)
        {
            var sps = new JsonArray();
            foreach (var sp in Specs) sps.Add(sp.ToJson());
            _root["specs"] = sps;
        }
        else _root.Remove("specs");

        // Only written when there are any, for the same reason `folder` is: a mod that overrides
        // nothing should not carry an empty array explaining that it could have.
        if (Overrides.Count > 0)
        {
            var ovr = new JsonArray();
            foreach (var o in Overrides) ovr.Add(o.ToJson());
            _root["overrides"] = ovr;
        }
        else _root.Remove("overrides");

        return _root.ToJsonString(Write);
    }

    /// <summary>
    /// Write the project out, atomically, keeping the previous version.
    ///
    /// This used to be a bare WriteAllText, which TRUNCATES THE FILE FIRST and then writes. The
    /// editor autosaves about a second after every change, so the window in which the file on disk
    /// is empty or half-written is entered dozens of times an hour -- and anything that interrupts
    /// it there (a crash, a power cut, the disk saying no) leaves the author's mod as a truncated
    /// file rather than as what it was a moment ago.
    ///
    /// Temp-then-rename means the real file is only ever replaced by a COMPLETE one: File.Move
    /// over an existing file is atomic on NTFS. The .bak is the second half of the same idea --
    /// the previous version survives even a save that succeeded and should not have.
    ///
    /// The same pattern is already used for the sound library; this is that, applied to the one
    /// file an author cannot rebuild by pressing a button.
    /// </summary>
    public void Save()
    {
        var json = Serialize();
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(FilePath))
        {
            // Best effort, and deliberately not fatal: a backup that cannot be written is a
            // reason to carry on saving, not a reason to refuse.
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { }
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>
    /// Everything wrong with a set of mods, in the order it should be fixed.
    ///
    /// Duplicate ids are checked ACROSS mods, not only within one. Two mods that both define
    /// CPD_Mod_RexHelmet cook to the same package path and the second silently wins -- which
    /// looks, from the menu, like the first mod simply not working.
    /// </summary>
    public static List<string> Validate(IReadOnlyList<ModProject> mods)
    {
        var problems = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            // Checked once per mod, before the parts: a folder outside the scanned tree makes
            // every part in it invisible, so reporting it per part would bury the one cause under
            // five copies of its symptom. Skipped entirely for a mod that ships nothing there --
            // see ShipsIntoItsOwnFolder.
            if (mod.ShipsIntoItsOwnFolder && FolderProblem(mod.Folder) is { } folderWhy)
                problems.Add($"{mod.Name}: {folderWhy}");

            // Reserved: a build of mod X writes its early files as "X_Early_P", so a mod whose own
            // container is called that would be mistaken for somebody's early half.
            if (EarlyLoad.IsEarlyContainer(mod.ContainerName))
                problems.Add($"{mod.Name}: a name ending in \"_Early\" is reserved for the files a mod " +
                             "installs into ~mods -- rename it");

            // An icon of the mod's own is a tag and a picture, and both are needed: without the
            // tag nothing can name it, without the picture the bank entry draws nothing.
            var iconTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ic in mod.Icons)
            {
                if (string.IsNullOrWhiteSpace(ic.Tag))
                    problems.Add($"{mod.Name}: an icon has no tag");
                else if (!ic.Tag.StartsWith("ImageBank.", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{mod.Name}: icon tag '{ic.Tag}' is not an ImageBank.* tag, " +
                                 "so no menu will look it up");
                else if (!iconTags.Add(ic.Tag))
                    problems.Add($"{mod.Name}: icon {ic.Tag} is defined twice");
                if (string.IsNullOrWhiteSpace(ic.Image))
                    problems.Add($"{mod.Name}: icon {ic.Tag} names no image");
            }
        }

        foreach (var mod in mods)
        foreach (var p in mod.Parts)
        {
            // KEYED ON THE PACKAGE, WHICH IS FOLDER PLUS ID -- not on the id alone.
            //
            // Two mods sharing an id only collide when they also share a folder, because the
            // folder is what the built package path is made of. Keying on the id alone called a
            // collision on every project that had already taken the one lever offered against
            // one, and since a reported problem greys out Build for the WHOLE folder, keeping an
            // old version of a mod beside its rewrite disabled the button for every unrelated mod
            // in there too. The user hit exactly that: two DLT-20A projects, different folders,
            // nothing colliding, and no mod in the folder could be built.
            var where = $"{mod.Folder}/{p.Id}";
            if (string.IsNullOrWhiteSpace(p.Id))
                problems.Add($"{mod.Name}: a part has no id");
            else if (seen.TryGetValue(where, out var other) && other != mod.Name)
                problems.Add($"{p.Id} is defined by both {other} and {mod.Name} in " +
                             $"{mod.Folder} - they cook to the same package and one will " +
                             "silently win. Give one of them a folder of its own.");
            else if (seen.ContainsKey(where) && other == mod.Name)
                problems.Add($"{mod.Name}: {p.Id} is defined twice");
            else if (!string.IsNullOrWhiteSpace(p.Id)) seen[where] = mod.Name;

            if (!Slots.Contains(p.Slot, StringComparer.OrdinalIgnoreCase))
                problems.Add($"{mod.Name}/{p.Id}: '{p.Slot}' is not a slot " +
                             $"({string.Join(", ", Slots)})");
            // Only for a slot that HAS geometry. A weapon's Type part is metadata holding a
            // gameplay tag and a specialization is abilities and text; demanding a mesh of either
            // reports a fault on a part that is correct, and the editor showed it in red on a
            // freshly duplicated weapon that built and installed perfectly well.
            if (string.IsNullOrWhiteSpace(p.Mesh))
            {
                // Missing is only a fault where a mesh belongs. Guarding the FIRST rule alone left
                // the second one firing on the same empty string, so a correct weapon part went
                // from "no mesh" to "mesh '' has no object suffix" -- a fix that moved the
                // complaint rather than removing it.
                // ...and not where the geometry is carried by the PIECES instead. A whole-body
                // garment whose torso, legs and arms each name their own mesh has no single one,
                // and reporting that greys out Build for the entire folder.
                if (!SlotHasNoMesh(p.Slot) &&
                    !p.Regions.Any(r => !string.IsNullOrWhiteSpace(r.Mesh)))
                    problems.Add($"{mod.Name}/{p.Id}: no mesh");
            }
            // A mesh reference the game resolves is "/Game/Path/Name.Name". Written without the
            // object suffix it imports as an empty soft pointer and the part equips nothing --
            // which is invisible until someone wears it.
            else if (!p.Mesh.Contains('.'))
                problems.Add($"{mod.Name}/{p.Id}: mesh '{p.Mesh}' has no object suffix - " +
                             "it should end in .AssetName");

            // THE PIECES, under the same rules. Nothing here can fire on a part with no `regions`
            // key, which is every part that existed before this. Validate holds no IFileProvider,
            // so it cannot ask whether a piece name matches the template -- that is a build
            // warning and an editor colour, not a problem reportable from the file alone.
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in p.Regions)
            {
                if (string.IsNullOrWhiteSpace(r.Region))
                    problems.Add($"{mod.Name}/{p.Id}: a piece with no name");
                else if (!named.Add(r.Region))
                    problems.Add($"{mod.Name}/{p.Id}: the piece '{r.Region}' is named twice - " +
                                 "the second one would silently win");

                if (!string.IsNullOrWhiteSpace(r.Mesh) && !r.Mesh.Contains('.'))
                    problems.Add($"{mod.Name}/{p.Id}: piece '{r.Region}' mesh '{r.Mesh}' has no " +
                                 "object suffix - it should end in .AssetName");
            }
        }
        return problems;
    }

    private static string? Str(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) ? v?.GetValue<string>() : null;
}

/// <summary>One wearable piece. See <see cref="ModProject"/> for why the vocabulary is friendly.</summary>
public sealed class ModPart : IMeshAuthoring
{
    /// <summary>
    /// Turn a part the game already ships into the starting point for a mod entry, or null when
    /// it cannot be one.
    ///
    /// Null is the important half. Only about a third of the catalogue is a wearable outfit slot
    /// with geometry; the rest are colours, names, presets, weapon specialisations and Blueprint
    /// bases. Producing a plausible-looking entry for one of those gives a mod that builds, cooks,
    /// installs and does nothing, with no error anywhere to say why -- so the two reasons it can
    /// refuse are returned rather than defaulted around.
    /// </summary>
    public static ModPart? FromGamePart(string assetName, string label, string slotTag,
                                        string? meshPath, string? iconPath, string? animClass,
                                        bool hidesSomething, out string? why,
                                        IEnumerable<(string Region, string Mesh)>? regions = null)
    {
        var slot = ModProject.FromSlotTag(slotTag);
        if (slot is null)
        {
            why = "not a wearable outfit slot";
            return null;
        }
        // ONLY WHERE A MESH BELONGS. A weapon specialisation, a class, a talent and a weapon
        // TYPE part carry no geometry by design -- they are abilities, stat tables, text and a
        // gameplay tag -- and demanding one refused every single one of them with "no mesh of its
        // own", which reads as a fault in the part rather than as this asking the wrong question.
        //
        // The validator has known this since the beginning (see Validate, which guards the same
        // test with the same helper). Only this path did not.
        if (!ModProject.SlotHasNoMesh(slot) && string.IsNullOrWhiteSpace(meshPath))
        {
            why = "no mesh, and this slot needs one";
            return null;
        }

        why = null;
        var made = new ModPart
        {
            // This becomes a package name, and it has to be unique against every other mod
            // installed at the same time -- two mods sharing an id cook to the same path and one
            // silently wins. Naming it after the source part makes a collision obvious.
            Id = "CPD_Mod_" + assetName,
            Display = label + " (Mod)",
            Slot = slot,
            Mesh = meshPath ?? "",
            Icon = iconPath ?? "",
            AnimClass = animClass ?? "",
            // Carried across from the source part rather than inferred from the slot. The game's
            // own helmets state it explicitly, and a helmet that does not hide the head renders
            // the character's face through the visor.
            //
            // BUT ONLY FOR A HELMET. "Hides something" is Detail.HidesHead, which is true for any
            // part overriding the hair OR the head, and the box it seeds means something narrower:
            // "add a Head.Face override", which the build does for a part that does not already
            // carry one. A Devaronian head overrides only the hair (its horns replace it), so its
            // duplicate arrived ticked and the build gave it a face override the original never
            // had -- a duplicate that is the original plus a change nobody asked for.
            //
            // A helmet is the one part that SHOULD get it (stock clone helmets override hair
            // only, and the face rendering through their visor was measured in game), so a
            // helmet is seeded exactly as before. The two torsos that override hair are suits,
            // not helmets, and a faithful copy of them carries nothing more. An author who wants
            // the box on any other outfit part ticks it.
            HidesHead = hidesSomething && slot == "Helmet",

            // AND THE PART ITSELF, as the template. Picking a part in the browser means "duplicate
            // this one" -- that is the whole reason someone went looking through the catalogue.
            //
            // Without this the button took only the mesh, the icon and the name, and the builder
            // then RANKED the whole catalogue for something to build on. Clicking the clone helmet
            // could therefore hand back a part built on a Mandalorian one, wearing a clone mesh:
            // right geometry, wrong base class, wrong fragments -- and no clone voice filter,
            // because that lives in a fragment the Mandalorian part does not carry. Hours went
            // into finding that, and the cause was this line not existing.
            Template = assetName,
        };

        // EVERY PIECE, when the part dresses more than its own slot.
        //
        // A whole-body garment carries one mesh fragment per body region, and a part naming one
        // mesh puts that mesh in all of them -- so duplicating the Clone Commando gave a character
        // wearing five copies of one piece. Each piece is brought across pointing at the game's own
        // mesh, which reproduces the garment exactly; the author replaces the ones they want.
        //
        // Filled HERE rather than at the button, so the staging button and `--modstage` cannot
        // produce different parts for the same catalogue entry.
        if (regions is not null)
        {
            foreach (var (region, mesh) in regions)
                if (region.Length > 0) made.RegionFor(region).Mesh = mesh;

            // THE ANCHOR IS THE PIECE ON THE PART'S OWN SLOT, not Meshes[0].
            //
            // Detail.MeshPath is the first mesh fragment, which on the Clone Commando is the
            // BOOTS -- so a Torso part would have been anchored to a boot. The anchor is what
            // picks the body type for pieces the author leaves alone, and which piece's tint mask
            // the part's colour fragment is redirected to, so it has to be the garment's own.
            if (ModProject.RegionForSlot(slot) is { Length: > 0 } own &&
                made.RegionNamed(own) is { Mesh.Length: > 0 } anchor)
                made.Mesh = anchor.Mesh;
        }

        return made;
    }

    public string Id = "";
    public string Display = "";
    public string Slot = "Helmet";
    public string Mesh = "";
    public string Icon = "";
    public string ForCharacter = "";
    public string AnimClass = "";
    public bool HidesHead;

    /// <summary>
    /// <see cref="HidesHead"/> as the BUILD and the VERIFIER read it: the author's tick, but only
    /// in a slot where it can mean something (<see cref="ModProject.SlotCanHideHead"/>).
    ///
    /// A head cannot hide itself, so a Head part saved with the box ticked -- every Devaronian
    /// duplicate made before this existed -- builds as an unticked one and no longer gains a face
    /// override its original lacks. Nothing is rewritten in the file: the stale tick is simply not
    /// believed, and the build says so.
    /// </summary>
    public bool EffectiveHidesHead => HidesHead && ModProject.SlotCanHideHead(Slot);

    public int SortPriority;

    /// <summary>
    /// The game part to build this one from, naming it outright instead of letting the ranking
    /// choose. Empty means choose, which is what nearly every part should do.
    ///
    /// A template is not a detail: it decides the base class, the override slots, the gameplay
    /// tags AND every fragment nobody has reasoned about, all of which the mod inherits wholesale.
    /// The ranking in <see cref="ModBuild.TemplateFor"/> orders those by what has actually been
    /// seen to go wrong, and it is right often enough to be the default -- but it can only rank on
    /// what it knows to look at, and a fragment nobody has named yet is invisible to it.
    ///
    /// That is exactly how the helmet lost its voice filter. Every clone helmet carries a
    /// `CustomizationFragmentHelmetVO`; the Mandalorian part the ranking picked does not, and no
    /// score counted it. Teaching the ranking about that one fragment would fix that one case and
    /// leave the next unnamed fragment to be discovered the same way -- so the escape hatch is a
    /// name, which needs no new knowledge to be right.
    ///
    /// Matched leniently: an asset name, a suffix, or a full package path. A name that matches
    /// nothing is an ERROR rather than a silent fall back to the ranking, because a template that
    /// was asked for and quietly not used is a mod that builds, installs, and is subtly wrong.
    /// </summary>
    public string Template = "";

    /// <summary>
    /// Carry the mod's OWN copy of the mesh, instead of pointing at the game's.
    ///
    /// Everything authored until now was a rearrangement of assets the game ships: a part is a
    /// renamed copy of another part, and its mesh still belongs to the game. That is enough to
    /// combine what exists and no use at all to someone who wants to change the geometry, because
    /// there is nothing of theirs to change.
    ///
    /// With this set the mesh package is copied into the mod, renamed into its folder, and the
    /// part wears the copy -- which is also what an imported mesh will replace later. Costs the
    /// mesh's size in the mod: a helmet here is 630 KB.
    /// </summary>
    public bool OwnMesh;

    /// <summary>
    /// One entry per material slot, in the order the imported file's objects sit.
    ///
    /// Slot 0 is what <see cref="Textures"/> has always meant, so nothing that only knows about one
    /// material has to change. A second entry appears when the author paints a second object.
    /// </summary>
    public List<ModMaterial> Materials { get; } = new();

    /// <summary>The slot at <paramref name="index"/>, growing the list to reach it.</summary>
    public ModMaterial Material(int index) => ModMaterial.At(Materials, index);

    /// <summary>Whether ANY slot carries artwork of the author's.</summary>
    public bool HasPaint => Materials.Any(m => m.Textures.Count > 0);

    /// <summary>
    /// Whether ANY slot names a game material and paints nothing on it. Such a part needs a copy
    /// of the game's mesh to carry the choice, for the same reason a painted one does: the mesh is
    /// what names the material.
    /// </summary>
    public bool HasNamedMaterial => Materials.Any(m => m.IsNamedOnly);

    /// <summary>
    /// The PIECES of a garment that dresses more than its own slot. See <see cref="ModRegion"/>.
    ///
    /// Empty for every part that is one garment, which is every part authored before this existed
    /// and most parts after it -- measured over the catalogue, only Torso and Legs templates carry
    /// several mesh fragments (69 of 285 and 66 of 157; 3 of 165 helmets). Empty means
    /// <see cref="Mesh"/> answers for every piece, exactly as it always has.
    /// </summary>
    public List<ModRegion> Regions { get; } = new();

    /// <summary>The piece called <paramref name="key"/>, or null. Case-insensitive.</summary>
    public ModRegion? RegionNamed(string key) =>
        Regions.FirstOrDefault(r => string.Equals(r.Region, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The piece called <paramref name="key"/>, appending one when there is none.
    ///
    /// The <see cref="ModMaterial.At"/> analogue, except keyed by LABEL rather than by index --
    /// a piece's position in the template's export table is not stable between templates and must
    /// never be a key.
    /// </summary>
    public ModRegion RegionFor(string key)
    {
        var found = RegionNamed(key);
        if (found is not null) return found;
        var made = new ModRegion { Region = key };
        Regions.Add(made);
        return made;
    }

    /// <summary>
    /// Whether this part authors its pieces separately. The single switch every downstream change
    /// hangs off, so a part that does not is on the same code path it was on before pieces existed.
    /// </summary>
    public bool HasRegions => Regions.Any(r => r.Region.Length > 0 && !r.IsEmpty);

    /// <summary>Whether the part OR any of its pieces carries artwork of the author's.</summary>
    public bool HasAnyPaint => HasPaint || Regions.Any(r => r.HasPaint);

    /// <summary>
    /// Slot zero's artwork, keyed by which of the part's textures it replaces.
    ///
    /// Three ways to name the target and any of them will do: the material parameter
    /// ("Base BSC VT"), the game texture's asset name, or -- the one an author will reach for --
    /// the SUFFIX the game already sorts them by. `{"D": "MyHelmet_D.tga", "N": "MyHelmet_N.tga"}`
    /// needs nothing looked up.
    ///
    /// Setting this implies <see cref="OwnMesh"/>: the repaint copies the material and points the
    /// MESH at the copy, and a part still wearing the game's mesh is still wearing the game's
    /// material. Rather than fail with a reference that resolves to the original, the builder
    /// turns the copy on and says so.
    ///
    /// A mesh with more than one material slot paints the others through <see cref="Materials"/>;
    /// this is slot zero, which is what a one-material part has always had.
    /// </summary>
    public Dictionary<string, string> Textures => Material(0).Textures;

    /// <summary>
    /// An FBX to put in place of the template's geometry.
    ///
    /// Like <see cref="Textures"/> this implies <see cref="OwnMesh"/> -- the import goes into the
    /// mod's own copy, and the game's mesh is never touched. The template supplies everything the
    /// FBX does not: the skeleton, the required bones, the section's flags and its bone map, so an
    /// imported mesh is skinned to bones the section can actually address.
    /// </summary>
    public string MeshFile { get; set; } = "";

    /// <summary>
    /// THE TINT MASK THIS PART'S COLOURS ARE PAINTED THROUGH, by package name.
    ///
    /// The game tints a part by sampling a mask texture and using its channels to decide which
    /// panels take which of the player's chosen colours. The mask comes from the TEMPLATE, and a
    /// template drawn for one armour kit masks the wrong panels on another: a Clo010 commando
    /// torso built from a Clo001 template gets Clo001's mask sampled with Clo010's UVs, and the
    /// tint lands in the wrong places.
    ///
    /// Naming one here redirects the template's reference. Both are the game's own textures, so
    /// nothing is copied -- it is a name substitution in the part, the same mechanism that points
    /// a part at a different mesh.
    ///
    /// It also makes a piece tintable that the game does not tint: the Clo010 torso HAS a mask
    /// (T_Clo010_SUIT_Tint) and simply no part that uses it.
    /// </summary>
    public string TintMask { get; set; } = "";

    /// <summary>
    /// The colour this part paints through its tint mask, as sRGB hex ("4B4B4B"). Blank keeps the
    /// template's.
    ///
    /// This is what makes a COLOUR VARIANT: the game's eleven Clo000 helmets are eleven parts
    /// sharing one mesh, each with its own mask and its own value here. Nothing links them to a
    /// base -- so a mod does the same thing by duplicating one part and changing this.
    /// </summary>
    public string TintColor { get; set; } = "";

    /// <summary>
    /// How the FBX's axes map onto Unreal's, written as "XZY" or "-X-ZY", and how big a file unit
    /// is. Left blank the builder uses what was measured for this game's own exports; both are
    /// here because every FBX exporter has a different idea of "Y-up" and the answer is not
    /// derivable from the file, only checkable against a render.
    /// </summary>
    public string MeshBasis { get; set; } = "";
    public float MeshScale { get; set; }

    /// <summary>
    /// Move the imported mesh, in centimetres, after it is scaled.
    ///
    /// Separate from the scale because the two cannot do each other's job: scaling pivots about
    /// the origin, so resizing a helmet that sits a metre and a half up also slides it, and there
    /// is no single scale that gets both the size and the height right. Unreal's axes -- X
    /// forward, Y right, Z up.
    /// </summary>
    public float MeshOffsetX { get; set; }
    public float MeshOffsetY { get; set; }
    public float MeshOffsetZ { get; set; }

    /// <summary>The game mesh this part wears, which an import is measured against.</summary>
    public string MeshSource => Mesh;

    /// <summary>
    /// Abilities to swap, as the template's ability -> the one to use instead.
    ///
    /// This is what makes an authored SPECIALISATION more than a renamed copy. A specialisation's
    /// ability list is a set of soft object references, so swapping one is the same name
    /// substitution that already re-points a mesh or an icon -- and because the description, icon
    /// and tuning all live on the ability asset, swapping the reference brings the whole
    /// presentation with it.
    ///
    /// Written as bare asset names (GA_FleetOfFoot_T1 -> GA_ForcePush_T1); the builder resolves
    /// each to its package. A full object path is accepted too, for the rare name that is not
    /// unique.
    /// </summary>
    public Dictionary<string, string> Abilities { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Text to replace on the part's UI fragment, as property -> new value.
    ///
    /// `display` already covers DisplayName; this is for everything else a part says about itself,
    /// principally ShortDescription and LongDescription. An authored part that still carries its
    /// template's description is telling the player it is the template.
    /// </summary>
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Tags to drop from the copy's AllowedSlots.
    ///
    /// What makes a hero part usable as a template. A hero's specialisation carries its owner's
    /// name tag, and a copy inherits it -- so duplicating Tel-Rea's Padawan spec produces a part
    /// offered to Tel-Rea and nobody else, which is the opposite of why anyone duplicates it.
    ///
    /// Applied to the package AND to the registry entry, because those are two different copies of
    /// the tags and the menu filters on the registry one.
    /// </summary>
    public List<string> RemoveTags { get; } = new();

    /// <summary>
    /// Tags to ADD to the part's AllowedSlots (package) and its registry entry.
    ///
    /// The menu filters on the REGISTRY copy of these tags, not the package's (measured in game
    /// 2026-09-24), and <see cref="ModBuild.PartTagsFor"/> strips every `br.Customization.Accepts.*`
    /// tag from that copy to drop the template's own unlock gates. So putting a part in a slot its
    /// template never offered needs more than a removal -- the slot's own tag has to be ADDED, and
    /// it has to survive that strip, which is why this is applied after it rather than folded into
    /// <see cref="RemoveTags"/>.
    /// </summary>
    public List<string> AddTags { get; } = new();

    /// <summary>
    /// SLOTS THE TEMPLATE TAKES OVER, and whether this part still does. Full slot tag -> keep it.
    ///
    /// The clone legs replace the wearer's BOOTS: `OverrideSlots` claims `...Boots.Mesh` and
    /// `LockedSlots` claims `...Boots`, so the boots the player picked are swapped for the legs'
    /// own and the slot is greyed out. A duplicate inherits that, and an author who wanted the
    /// legs and not the takeover had no way to say so.
    ///
    /// Only entries set to FALSE mean anything: the rows come from the template, so `true` is
    /// simply what it already does and is dropped on save. Undoing a takeover edits BOTH
    /// containers -- see <see cref="PartDetails.Detail.LockedSlots"/>.
    /// </summary>
    public Dictionary<string, bool> TakesOver { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SLOTS THE TEMPLATE HIDES FROM THE MENU, and whether this part still does.
    ///
    /// The clone legs remove three colour swatches -- Visor, Tertiary, Secondary -- so a player
    /// wearing them cannot recolour those regions. Same shape as <see cref="TakesOver"/> and the
    /// same rule: only FALSE means anything.
    ///
    /// The lock is a DIFFERENT tag from the takeover case. A hidden slot is locked under its own
    /// name (`...Legs.Color.Visor` in both containers); a taken-over slot is locked under the tag
    /// with `.Mesh` removed. Getting that backwards edits the wrong container and the menu does
    /// not change.
    /// </summary>
    public Dictionary<string, bool> HidesFromMenu { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WHETHER THIS PART STILL LOCKS THE SLOT IT SITS IN, as its template does. True is what the
    /// template already does, so only FALSE is written -- the same rule as the two maps above.
    ///
    /// A different switch from either of them, and one nothing here knew about for a year: the
    /// OverrideSlots fragment ends in `bLockOwningSlot` and `bHideOwningSlot`, which say about the
    /// part's OWN slot what LockedSlots and HiddenSlots say about other people's. 49 of the game's
    /// 3,518 parts set the lock -- 44 named voiceovers, both bespoke appearances (which is why a
    /// clone's face cannot be changed), two torsos and Hawks' name -- so a mod built on any of
    /// those inherits a slot the player can never change again, with nothing saying so.
    ///
    /// There is no field for the hide. Not one part in the game sets `bHideOwningSlot`, so on
    /// every template it is absent from the header rather than present and false: turning it on is
    /// the grow-an-export job rather than a byte, with no shipped example to check the result
    /// against. Read and reported, deliberately not offered.
    /// </summary>
    public bool KeepsOwnSlotLock { get; set; } = true;

    /// <summary>
    /// Names to rewrite in the copied template, as from -> to.
    ///
    /// The escape hatch, for a reference the builder has no dedicated field for. The immediate use
    /// is the ICON: a specialisation names its icon with an `ImageBank.Icon.Class.*` gameplay tag
    /// rather than a texture path, so the icon field -- which cooks an image or re-points an asset
    /// path -- cannot reach it, while renaming one FName can.
    /// </summary>
    public Dictionary<string, string> RenameNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Fields this build does not know about, kept so a save cannot drop them.</summary>
    private JsonObject? _extra;

    public static ModPart From(JsonObject o)
    {
        var p = new ModPart
        {
            Id = Get(o, "id") ?? "",
            Display = Get(o, "display") ?? "",
            Slot = Get(o, "slot") ?? "Helmet",
            Mesh = Get(o, "mesh") ?? "",
            Icon = Get(o, "icon") ?? "",
            ForCharacter = Get(o, "forCharacter") ?? "",
            AnimClass = Get(o, "animClass") ?? "",
            Template = Get(o, "template") ?? "",
            HidesHead = o["hidesHead"]?.GetValue<bool>() ?? false,
            OwnMesh = o["ownMesh"]?.GetValue<bool>() ?? false,
            SortPriority = o["sortPriority"]?.GetValue<int>() ?? 0,
            _extra = o,
        };

        p.MeshFile = Get(o, "import") ?? "";
        p.MeshBasis = Get(o, "meshBasis") ?? "";
        p.MeshScale = o["meshScale"]?.GetValue<float>() ?? 0f;
        if (o["meshOffset"] is JsonArray off && off.Count == 3)
        {
            p.MeshOffsetX = off[0]?.GetValue<float>() ?? 0f;
            p.MeshOffsetY = off[1]?.GetValue<float>() ?? 0f;
            p.MeshOffsetZ = off[2]?.GetValue<float>() ?? 0f;
        }

        if (o["removeTags"] is JsonArray prem)
            foreach (var t in prem)
                if (t?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    p.RemoveTags.Add(t.GetValue<string>());

        if (o["addTags"] is JsonArray padd)
            foreach (var t in padd)
                if (t?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    p.AddTags.Add(t.GetValue<string>());

        if (o["takesOver"] is JsonObject pover)
            foreach (var (k, v) in pover)
                if (v?.GetValueKind() is System.Text.Json.JsonValueKind.True
                                      or System.Text.Json.JsonValueKind.False)
                    p.TakesOver[k] = v.GetValue<bool>();

        if (o["hidesFromMenu"] is JsonObject phide)
            foreach (var (k, v) in phide)
                if (v?.GetValueKind() is System.Text.Json.JsonValueKind.True
                                      or System.Text.Json.JsonValueKind.False)
                    p.HidesFromMenu[k] = v.GetValue<bool>();

        if (o["keepsOwnSlotLock"]?.GetValueKind() is System.Text.Json.JsonValueKind.True
                                                  or System.Text.Json.JsonValueKind.False)
            p.KeepsOwnSlotLock = o["keepsOwnSlotLock"]!.GetValue<bool>();

        if (o["text"] is JsonObject txt)
            foreach (var (k, v) in txt)
                if (v?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    p.Text[k] = v.GetValue<string>();

        if (o["renameNames"] is JsonObject ren)
            foreach (var (k, v) in ren)
                if (v?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    p.RenameNames[k] = v.GetValue<string>();

        if (o["abilities"] is JsonObject abil)
            foreach (var (k, v) in abil)
                if (v?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    p.Abilities[k] = v.GetValue<string>();

        ModMaterial.ReadInto(o, p.Materials);
        p.TintMask = Get(o, "tintMask") ?? "";
        p.TintColor = Get(o, "tintColor") ?? "";

        if (o["regions"] is JsonArray regs)
            foreach (var r in regs)
                if (r is JsonObject ro) p.Regions.Add(ModRegion.From(ro));

        return p;
    }

    public JsonObject ToJson()
    {
        // Start from what was read so unrecognised keys survive, then overwrite the known ones.
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        o["id"] = Id;
        o["display"] = Display;
        o["slot"] = Slot;
        o["mesh"] = Mesh;
        // Empty means "not set", and the builder tests these for truthiness -- so they are written
        // as JSON null rather than "", which would read as a character actually named "".
        o["icon"] = string.IsNullOrWhiteSpace(Icon) ? null : Icon;
        o["forCharacter"] = string.IsNullOrWhiteSpace(ForCharacter) ? null : ForCharacter;
        o["hidesHead"] = HidesHead;
        // Only written when asked for, so an ordinary part file stays short.
        if (!string.IsNullOrWhiteSpace(Template)) o["template"] = Template; else o.Remove("template");
        if (OwnMesh) o["ownMesh"] = true; else o.Remove("ownMesh");
        if (RemoveTags.Count > 0)
        {
            var prem = new JsonArray();
            foreach (var t in RemoveTags) prem.Add(t);
            o["removeTags"] = prem;
        }
        else o.Remove("removeTags");

        if (AddTags.Count > 0)
        {
            var padd = new JsonArray();
            foreach (var t in AddTags) padd.Add(t);
            o["addTags"] = padd;
        }
        else o.Remove("addTags");

        // Only the ones turned OFF. A `true` says the part does what its template does, which is
        // what a missing entry already says -- writing them would grow the file every time
        // somebody opened the step and changed nothing.
        foreach (var (key, map) in new[] { ("takesOver", TakesOver), ("hidesFromMenu", HidesFromMenu) })
        {
            var kept = map.Where(kv => !kv.Value).ToList();
            if (kept.Count == 0) { o.Remove(key); continue; }
            var written = new JsonObject();
            foreach (var (k, v) in kept) written[k] = v;
            o[key] = written;
        }

        // Same rule again: true is what the template already does, so it is a missing key.
        if (KeepsOwnSlotLock) o.Remove("keepsOwnSlotLock");
        else o["keepsOwnSlotLock"] = false;

        foreach (var (key, map) in new[] { ("text", Text), ("renameNames", RenameNames) })
        {
            if (map.Count == 0) { o.Remove(key); continue; }
            var obj = new JsonObject();
            foreach (var (k, v) in map) obj[k] = v;
            o[key] = obj;
        }
        if (Abilities.Count > 0)
        {
            var abil = new JsonObject();
            foreach (var (k, v) in Abilities) abil[k] = v;
            o["abilities"] = abil;
        }
        else o.Remove("abilities");
        ModMaterial.WriteInto(o, Materials);

        // WRITTEN OR REMOVED, never left to the _extra clone. Reading a key into the model and not
        // rewriting it is this design's one real hazard: the stale original array would come back
        // out of the DeepClone, so a piece deleted in the editor would look gone, be saved as it
        // was, and reappear on the next load.
        var pieces = Regions.Where(r => r.Region.Length > 0 && !r.IsEmpty).ToList();
        if (pieces.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var r in pieces) arr.Add(r.ToJson());
            o["regions"] = arr;
        }
        else o.Remove("regions");

        if (!string.IsNullOrWhiteSpace(MeshFile)) o["import"] = MeshFile; else o.Remove("import");
        if (!string.IsNullOrWhiteSpace(TintMask)) o["tintMask"] = TintMask; else o.Remove("tintMask");
        if (!string.IsNullOrWhiteSpace(TintColor)) o["tintColor"] = TintColor; else o.Remove("tintColor");
        if (!string.IsNullOrWhiteSpace(MeshBasis)) o["meshBasis"] = MeshBasis; else o.Remove("meshBasis");
        if (MeshScale > 0) o["meshScale"] = MeshScale; else o.Remove("meshScale");
        if (MeshOffsetX != 0 || MeshOffsetY != 0 || MeshOffsetZ != 0)
            o["meshOffset"] = new JsonArray(MeshOffsetX, MeshOffsetY, MeshOffsetZ);
        else o.Remove("meshOffset");
        o["animClass"] = string.IsNullOrWhiteSpace(AnimClass) ? null : AnimClass;
        if (SortPriority != 0) o["sortPriority"] = SortPriority;
        return o;
    }

    /// <summary>A string field that may legitimately be JSON null.</summary>
    private static string? Get(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is not null && v.GetValueKind() ==
            System.Text.Json.JsonValueKind.String
            ? v.GetValue<string>()
            : null;
}

/// <summary>
/// One asset of the game's own, shipped changed at its original package path.
///
/// Deliberately narrow for now. The only edit modelled is re-pointing a name reference, because
/// that is the one edit that needs no property serialiser and no offset correction -- see
/// <see cref="NameRefEdit"/>. A general "set this property to that value" needs machinery that can
/// grow an export, and inventing the schema for it before the machinery exists would fix the shape
/// of a feature nobody has built yet.
/// </summary>
/// <summary>
/// A specialization this mod authors, copied from one the game ships.
///
/// NOT a <see cref="ModPart"/>, and the difference is the whole reason specializations were
/// unmakeable. A part is Slot, Mesh, AnimClass, HidesHead, MeshScale -- an outfit piece. A
/// specialization has no mesh at all. What it has is an identity tag, a list of abilities granted
/// outright, a focus tree of seventeen rungs across three lines, and a gear kit. Nothing about the
/// two shapes overlaps except the name and the description, so making one fit the other would mean
/// a part with five dead fields and a specialization with none of the four that matter.
///
/// See <see cref="SpecDetails"/> for what is being copied.
/// </summary>
public sealed class ModSpec
{
    /// <summary>One rung of one focus-tree line: what it grants and what reaching it takes.</summary>
    public sealed class Rung
    {
        public string Ability = "";
        public int MinLevel;
        public int FocusLevel;
        public int FocusCost;
    }

    /// <summary>The asset name this specialization is built as.</summary>
    public string Id = "";

    /// <summary>The game specialization it was copied from, by package path. Everything not
    /// changed below is whatever the template already said.</summary>
    public string Template = "";

    /// <summary>The name in the menu. Blank keeps the template's, which is rarely what anyone wants.</summary>
    public string Display = "";

    /// <summary>Replaces both descriptions. Blank keeps the template's.</summary>
    public string Description = "";

    /// <summary>
    /// The one-line summary, when it should differ from the paragraph.
    ///
    /// The game writes a specialization's text twice: ShortDescription is a sentence or two for the
    /// list, LongDescription repeats it and then adds "...using the following skills" with the
    /// abilities spelled out. <see cref="Description"/> alone goes into BOTH, which puts a whole
    /// paragraph where the menu wants a line -- fine for a first draft and wrong for anything
    /// shipped. Blank keeps that behaviour, so nothing existing changes.
    ///
    /// Found rebuilding NewSpec from the mod that was installed: everything else came back
    /// identical and this was the only field that could not (2026-09-09).
    /// </summary>
    public string ShortDescription = "";

    /// <summary>The image bank tag for its icon, e.g. ImageBank.Icon.Class.Assault.</summary>
    public string IconTag = "";

    /// <summary>
    /// A picture of the author's own instead of a tag: a PNG or TGA in ModSource/Icons by name, or
    /// a /Game/... texture the game already ships.
    ///
    /// Written into the specialization's own UI fragment as a soft brush WITH the hard reference
    /// the menus actually read -- the same shape a weapon's armoury part carries, and the same
    /// reason a tag of the mod's own never worked: a tag no bank of the GAME's defines draws the
    /// fallback picture, and a bank a mod ships is not read. Blank keeps the tag.
    /// </summary>
    public string Icon = "";

    /// <summary>
    /// The tag that IS this specialization.
    ///
    /// A copy carries its template's, and two parts both claiming to be the Assault is not
    /// something the game is expecting -- so a duplicate gets a new one derived from its id, and
    /// this is where that lives.
    /// </summary>
    public string Identity = "";

    /// <summary>
    /// Which character this specialization is for, if only one.
    ///
    /// Blank means everyone, and blank is what a duplicate wants: a hero specialization carries its
    /// owner's name tag, and a copy of one inherits it -- so duplicating the Padawan spec without
    /// this produces a new specialization that only Tel-Rea can pick. Named, it does the opposite
    /// and is how a mod hands a kit to one hero.
    /// </summary>
    public string ForCharacter = "";

    /// <summary>Granted outright, without spending focus.</summary>
    public List<string> Abilities { get; } = new();

    /// <summary>Starting equipment.</summary>
    public List<string> GearKits { get; } = new();

    /// <summary>The focus tree: lines of rungs, three of them on everything the game ships.</summary>
    public List<List<Rung>> Tree { get; } = new();

    /// <summary>
    /// Whether to add this to the picker's map so the game offers it as a ninth choice.
    ///
    /// The map pairs each primary specialization with its secondary and has exactly eight entries;
    /// a specialization not in it exists and is never offered. See ModBuild.
    /// </summary>
    public bool AddToPicker = true;

    /// <summary>Fields this build does not know about, kept so a save cannot drop them.</summary>
    private JsonObject? _extra;

    public int RungCount => Tree.Sum(line => line.Count);

    /// <summary>
    /// A mod specialization copied from one the game ships, ready to edit.
    ///
    /// Everything is carried across, because a copy that dropped the focus tree would be a copy of
    /// the name only -- and the tree is what makes a specialization SELECTABLE at all. One thing is
    /// deliberately changed: the display name gets " copy" appended, so the two are distinguishable
    /// in a menu that will now show both.
    ///
    /// THE IDENTITY TAG IS KEPT. That looks wrong and is measured: an authored specialization
    /// carrying its template's identity tag shipped and worked -- it reached the picker AND the
    /// game's own recruit generator. A fresh tag would be one the game's tag table does not define,
    /// which nothing here can register, so the default is the configuration that is known to work
    /// and inventing one is left to the author.
    /// </summary>
    public static ModSpec CopyOf(SpecDetails.Spec src, string id)
    {
        var made = new ModSpec
        {
            Id = id,
            Template = src.Path,
            Display = (src.DisplayName ?? src.Name) + " copy",
            Description = src.ShortDescription ?? "",
            IconTag = src.IconTag ?? "",
            Identity = src.Identity ?? "",
        };
        made.Abilities.AddRange(src.Abilities);
        made.GearKits.AddRange(src.GearKits);
        foreach (var line in src.Tree)
            made.Tree.Add(line.Select(r => new Rung
            {
                Ability = r.Ability, MinLevel = r.MinLevel,
                FocusLevel = r.FocusLevel, FocusCost = r.FocusCost,
            }).ToList());
        return made;
    }

    public static ModSpec From(JsonObject o)
    {
        var sp = new ModSpec
        {
            Id = Get(o, "id") ?? "",
            Template = Get(o, "template") ?? "",
            Display = Get(o, "display") ?? "",
            Description = Get(o, "description") ?? "",
            ShortDescription = Get(o, "shortDescription") ?? "",
            IconTag = Get(o, "iconTag") ?? "",
            Icon = Get(o, "icon") ?? "",
            Identity = Get(o, "identity") ?? "",
            ForCharacter = Get(o, "forCharacter") ?? "",
            AddToPicker = o["addToPicker"]?.GetValue<bool>() ?? true,
            _extra = o,
        };

        if (o["abilities"] is JsonArray ab)
            foreach (var t in ab)
                if (t?.GetValue<string>() is { Length: > 0 } v) sp.Abilities.Add(v);

        if (o["gearKits"] is JsonArray gk)
            foreach (var t in gk)
                if (t?.GetValue<string>() is { Length: > 0 } v) sp.GearKits.Add(v);

        if (o["tree"] is JsonArray tree)
            foreach (var lineNode in tree)
            {
                var line = new List<Rung>();
                if (lineNode is JsonArray rungs)
                    foreach (var r in rungs)
                        if (r is JsonObject ro)
                            line.Add(new Rung
                            {
                                Ability = Get(ro, "ability") ?? "",
                                MinLevel = ro["minLevel"]?.GetValue<int>() ?? 0,
                                FocusLevel = ro["focusLevel"]?.GetValue<int>() ?? 0,
                                FocusCost = ro["focusCost"]?.GetValue<int>() ?? 0,
                            });
                sp.Tree.Add(line);
            }

        return sp;
    }

    public JsonObject ToJson()
    {
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        o["id"] = Id;
        o["template"] = Template;
        o["display"] = Display;
        o["description"] = Description;
        if (ShortDescription.Length > 0) o["shortDescription"] = ShortDescription;
        else o.Remove("shortDescription");
        o["iconTag"] = IconTag;
        if (Icon.Length > 0) o["icon"] = Icon;
        o["identity"] = Identity;
        if (ForCharacter.Length > 0) o["forCharacter"] = ForCharacter; else o.Remove("forCharacter");
        o["addToPicker"] = AddToPicker;

        foreach (var (key, list) in new[] { ("abilities", Abilities), ("gearKits", GearKits) })
        {
            if (list.Count == 0) { o.Remove(key); continue; }
            var arr = new JsonArray();
            foreach (var v in list) arr.Add(v);
            o[key] = arr;
        }

        if (Tree.Count > 0)
        {
            var tree = new JsonArray();
            foreach (var line in Tree)
            {
                var rungs = new JsonArray();
                foreach (var r in line)
                    rungs.Add(new JsonObject
                    {
                        ["ability"] = r.Ability,
                        ["minLevel"] = r.MinLevel,
                        ["focusLevel"] = r.FocusLevel,
                        ["focusCost"] = r.FocusCost,
                    });
                tree.Add(rungs);
            }
            o["tree"] = tree;
        }
        else o.Remove("tree");

        return o;
    }

    private static string? Get(JsonObject o, string key) =>
        o[key]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? o[key]!.GetValue<string>() : null;
}

/// <summary>
/// A registered asset duplicated under a new name.
///
/// <see cref="ModPart"/> cannot do this. It goes through PartsIndex, which indexes
/// CustomizationPartDefinition and nothing else -- so a weapon LOADOUT, which is a
/// CustomizationDefinition, is invisible to it. Measured the hard way: a correctly built Type part,
/// registry-identical to a real weapon's, does not put a weapon in the armoury. The game enumerates
/// loadouts.
///
/// So this is the general form: take any asset the registry lists, copy it under a new name,
/// re-point what it names, and give it an identity of its own.
/// </summary>
/// <summary>
/// A game mesh copied into the mod, with the author's geometry and textures put into the copy.
///
/// <see cref="ModPart"/> could already do this, and for a helmet that is the whole answer: a part
/// NAMES its own mesh, so the parts loop had a package to copy and a reference to re-point. A
/// weapon names none -- its meshes come from a shared palette keyed by a type tag -- so the only
/// way to give a weapon new geometry was <see cref="ModOverride"/>, which writes over the game's
/// own asset.
///
/// That is a real cost and the user named it: they want to add meshes without replacing anything.
/// So the mesh is separated from the thing that references it. This makes the copy; a palette copy
/// points at it; the game's asset is never touched and could still be used by the game.
/// </summary>
/// <summary>
/// ONE MATERIAL SLOT'S ARTWORK.
///
/// A mesh wears one material per SECTION, and a section is one object in the imported file -- so a
/// clone torso with a pauldron bolted onto it is two, and the pauldron's paint had nowhere to live
/// while a mod carried one flat set of textures per mesh. This is that place. Slot 0 is the body,
/// slot 1 is what was bolted on, in the order the objects sit in the file.
///
/// A slot the GAME's mesh does not have is painted from slot zero's material -- a second copy of
/// the same material with the author's other textures in it. There is nothing else to base it on,
/// and the section wearing it is one the author's own file added.
///
/// Written as `materials` only when there is more than one. A helmet keeps the `textures` key it
/// has always had, so no existing mod's file changes shape for a feature it does not use.
/// </summary>
public sealed class ModMaterial
{
    /// <summary>
    /// The author's own artwork, keyed by which of the slot's textures it replaces.
    ///
    /// Three ways to name the target and any of them will do: the material parameter
    /// ("Base BSC VT"), the game texture's asset name, or -- the one an author will reach for --
    /// the SUFFIX the game already sorts them by. `{"D": "MyHelmet_D.tga"}` needs nothing looked up.
    /// </summary>
    public Dictionary<string, string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A GAME MATERIAL THIS SLOT WEARS, by package name -- instead of a repainted copy.
    ///
    /// The case this exists for: a piece taken from another kit and bolted onto this one, which
    /// should wear the material it came with. Clone legs with Clo003's holsters want
    /// `MI_Clo001_LEGS` on the holster section, and painting that by exporting four 4K virtual
    /// textures and importing them back would be several minutes of work, seventy megabytes of
    /// mod, and a worse result than the original.
    ///
    /// Naming it costs NOTHING: the mesh gains one package import and the slot points at it. No
    /// copy, no textures, no repaint. The game's asset is untouched, exactly as when a mesh
    /// imports its own material.
    ///
    /// Set alongside <see cref="Textures"/> the textures win, because a repaint has to own its
    /// material in order to redirect it.
    /// </summary>
    public string Material { get; set; } = "";

    /// <summary>
    /// WHAT THE MESH CALLS THIS SLOT, which is how the game aims a tint at one piece and not
    /// another.
    ///
    /// A cooked section names its material by index, so this was assumed to be decoration -- and
    /// it is not. A customization fragment targets `MaterialSlotNames`, so two slots sharing a
    /// name are two slots the game cannot tell apart: the torso's tint mask lands on the backpack
    /// as well, sampled with the backpack's UVs, and the pack comes out wrong.
    ///
    /// Left blank the build derives one from the material -- `MI_Clo003_PACK` becomes `MI_PACK`,
    /// which is what the game's own meshes call that slot. Set it when the derivation is wrong
    /// for a kit, or to aim a template's tint at a slot deliberately.
    /// </summary>
    public string SlotName { get; set; } = "";

    public bool IsEmpty => Textures.Count == 0 && Material.Length == 0 && SlotName.Length == 0;

    /// <summary>
    /// A game material NAMED and nothing painted on it -- the slot wears that material outright.
    /// This is the shape "Choose..." writes, and the one the build used to read only when the
    /// part also imported a model file: with the game's own mesh kept, nothing carried it through.
    /// </summary>
    public bool IsNamedOnly => Material.Length > 0 && Textures.Count == 0;

    /// <summary>The slot at <paramref name="index"/>, growing the list to reach it.</summary>
    public static ModMaterial At(List<ModMaterial> slots, int index)
    {
        while (slots.Count <= index) slots.Add(new ModMaterial());
        return slots[index];
    }

    /// <summary>
    /// Read both shapes into one list: the flat `textures` key first, then `materials` over it.
    ///
    /// Both, in that order, because a file written before this existed has only the first and a
    /// file written after it has only the second -- and one written by hand could have both, in
    /// which case the one that names slots explicitly is the one that meant to.
    /// </summary>
    public static void ReadInto(JsonObject o, List<ModMaterial> slots)
    {
        if (o["textures"] is JsonObject flat)
            foreach (var (key, value) in flat)
                if (value?.GetValueKind() == JsonValueKind.String)
                    At(slots, 0).Textures[key] = value.GetValue<string>();

        if (o["materials"] is not JsonArray list) return;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject entry) continue;
            var slot = At(slots, i);
            slot.Textures.Clear();
            slot.Material = entry["material"]?.GetValueKind() == JsonValueKind.String
                ? entry["material"]!.GetValue<string>() : "";
            slot.SlotName = entry["slotName"]?.GetValueKind() == JsonValueKind.String
                ? entry["slotName"]!.GetValue<string>() : "";
            if (entry["textures"] is not JsonObject tex) continue;
            foreach (var (key, value) in tex)
                if (value?.GetValueKind() == JsonValueKind.String)
                    slot.Textures[key] = value.GetValue<string>();
        }
    }

    /// <summary>The shorter of the two shapes that says the same thing.</summary>
    public static void WriteInto(JsonObject o, List<ModMaterial> slots)
    {
        // Trailing empty slots say nothing, and writing them would turn a one-material part into a
        // `materials` array the moment a UI opened a second slot and the author closed it again.
        var used = slots.Count;
        while (used > 1 && slots[used - 1].IsEmpty) used--;

        // THE FLAT `textures` SHAPE CAN ONLY CARRY TEXTURES.
        //
        // A single slot that names a game material and paints nothing -- the case this class's own
        // doc comment advertises, arms borrowed from another kit wearing the material they came
        // with -- fell through to the branch below, which writes Textures and nothing else. The
        // material was silently dropped on save. On a REGION that is worse than losing a field:
        // the entry then reads back IsEmpty, and ModPart.ToJson's `!r.IsEmpty` filter deletes the
        // whole piece on the following save.
        var richer = slots.Take(Math.Max(used, 1))
                          .Any(x => x.Material.Length > 0 || x.SlotName.Length > 0);

        if (used > 1 || richer)
        {
            var list = new JsonArray();
            for (var i = 0; i < Math.Max(used, 1) && i < slots.Count; i++)
            {
                var tex = new JsonObject();
                foreach (var (key, value) in slots[i].Textures) tex[key] = value;
                var entry = new JsonObject { ["textures"] = tex };
                if (slots[i].Material.Length > 0) entry["material"] = slots[i].Material;
                if (slots[i].SlotName.Length > 0) entry["slotName"] = slots[i].SlotName;
                list.Add(entry);
            }
            o["materials"] = list;
            o.Remove("textures");
            return;
        }

        o.Remove("materials");
        if (slots.Count > 0 && slots[0].Textures.Count > 0)
        {
            var tex = new JsonObject();
            foreach (var (key, value) in slots[0].Textures) tex[key] = value;
            o["textures"] = tex;
        }
        else o.Remove("textures");
    }
}

/// <summary>
/// The mesh-and-textures half of an authored asset, which a PART and a mod's own MESH both have.
///
/// They carry the same six things and the editor draws the same two panels for either. Without
/// this those panels would be duplicated, and a duplicated panel is one that gets fixed in one
/// place -- which is how a weapon would quietly end up with the older, worse version of the
/// importer's UI.
/// </summary>
public interface IMeshAuthoring
{
    /// <summary>The game mesh this is based on. The size check measures the import against it.</summary>
    string MeshSource { get; }

    string MeshFile { get; set; }
    float MeshScale { get; set; }
    string MeshBasis { get; set; }
    float MeshOffsetX { get; set; }
    float MeshOffsetY { get; set; }
    float MeshOffsetZ { get; set; }

    /// <summary>One entry per material slot, in the order the imported file's objects sit.</summary>
    List<ModMaterial> Materials { get; }

    /// <summary>The slot at <paramref name="index"/>, growing the list to reach it.</summary>
    ModMaterial Material(int index);

    /// <summary>Slot zero's artwork, which for a single-material mesh is all of it.</summary>
    Dictionary<string, string> Textures { get; }

    /// <summary>Whether ANY slot carries artwork of the author's.</summary>
    bool HasPaint { get; }
}

/// <summary>
/// An icon of the mod's own: a gameplay tag this mod defines, and the picture it draws.
///
/// The game resolves a menu icon by TAG through shared image banks, and a cooked bank cannot be
/// added to -- so a picture of the author's own ships in a BANK of the mod's own: a copy of the
/// game bank holding <see cref="Template"/>, that one entry re-keyed to <see cref="Tag"/> and
/// pointed at a texture carrying the author's pixels. Proved in game 2026-09-01 by hand; the build
/// does all of it from these three fields, and declares the tag.
/// </summary>
public sealed class ModIcon
{
    /// <summary>The tag this mod defines, e.g. ImageBank.Icon.Weapon.MyRifle.</summary>
    public string Tag = "";

    /// <summary>A PNG or TGA in ModSource/Icons, by name.</summary>
    public string Image = "";

    /// <summary>
    /// The game entry whose slot this takes in the copied bank, e.g. ImageBank.Icon.Weapon.DC15A.
    /// Its texture is what the pixels replace, so it also decides the size and shape the picture
    /// is drawn at. Blank: the first entry of the tag's family not taken by another icon here.
    /// </summary>
    public string Template = "";

    public static ModIcon From(JsonObject o) => new()
    {
        Tag = o["tag"]?.GetValue<string>() ?? "",
        Image = o["image"]?.GetValue<string>() ?? "",
        Template = o["template"]?.GetValue<string>() ?? "",
    };

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["tag"] = Tag, ["image"] = Image };
        if (Template.Length > 0) o["template"] = Template;
        return o;
    }
}

/// <summary>
/// A <see cref="ModOverride"/> WEARING THE MESH INTERFACE, so the fit and material editors written
/// for a part can be given one.
///
/// An override carries the same five fit fields and its own material slots; what it does not carry
/// is textures, because a replacement's paint is separate overrides that repaint the game's own.
/// The adapter says so rather than throwing: a caller asking "what paint does this have" wants
/// "none" as an answer it can draw.
///
/// IN CORE RATHER THAN IN AN APP because both Studios need it and two copies of an adapter are two
/// places for "what does an override look like to the mesh editor" to drift.
/// </summary>
public sealed class OverrideMesh(ModOverride ov) : IMeshAuthoring
{
    /// <summary>The asset being replaced IS the mesh the import is measured against.</summary>
    public string MeshSource => ov.Target;

    public string MeshFile { get => ov.MeshFile; set => ov.MeshFile = value; }
    public float MeshScale { get => ov.MeshScale; set => ov.MeshScale = value; }
    public string MeshBasis { get => ov.MeshBasis; set => ov.MeshBasis = value; }
    public float MeshOffsetX { get => ov.MeshOffsetX; set => ov.MeshOffsetX = value; }
    public float MeshOffsetY { get => ov.MeshOffsetY; set => ov.MeshOffsetY = value; }
    public float MeshOffsetZ { get => ov.MeshOffsetZ; set => ov.MeshOffsetZ = value; }

    /// <summary>THE OVERRIDE'S OWN SLOTS, which the build reads and the editor writes.</summary>
    public List<ModMaterial> Materials => ov.Materials;

    /// <summary>Slot <paramref name="index"/>, made if it is not there yet.</summary>
    public ModMaterial Material(int index)
    {
        while (ov.Materials.Count <= index) ov.Materials.Add(new ModMaterial());
        return ov.Materials[index];
    }

    /// <summary>Empty, and really empty: a replacement's textures are separate overrides.</summary>
    public Dictionary<string, string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False: paint means textures of this slot's own and a replacement has none.</summary>
    public bool HasPaint => false;
}

public sealed class ModMesh : IMeshAuthoring
{
    /// <summary>The game mesh to copy, by package name.</summary>
    public string Source = "";

    /// <summary>A file to import over the copy's geometry. Optional -- textures alone are a repaint.</summary>
    public string MeshFile { get; set; } = "";

    public float MeshScale { get; set; }
    public string MeshBasis { get; set; } = "";
    public float MeshOffsetX { get; set; }
    public float MeshOffsetY { get; set; }
    public float MeshOffsetZ { get; set; }

    /// <summary>The game mesh being copied, which is what an import is measured against.</summary>
    public string MeshSource => Source;

    /// <summary>
    /// One entry per material slot, in the order the imported file's objects sit.
    ///
    /// Slot 0 is what <see cref="Textures"/> has always meant, so nothing that only knows about one
    /// material has to change. A second entry appears when the author paints a second object.
    /// </summary>
    public List<ModMaterial> Materials { get; } = new();

    /// <summary>The slot at <paramref name="index"/>, growing the list to reach it.</summary>
    public ModMaterial Material(int index) => ModMaterial.At(Materials, index);

    /// <summary>Whether ANY slot carries artwork of the author's.</summary>
    public bool HasPaint => Materials.Any(m => m.Textures.Count > 0);

    /// <summary>Slot zero's textures, painted onto the copy the way a part's are.</summary>
    public Dictionary<string, string> Textures => Material(0).Textures;

    private JsonObject? _extra;

    public static ModMesh From(JsonObject o)
    {
        var m = new ModMesh
        {
            Source = Get(o, "source") ?? "",
            MeshFile = Get(o, "import") ?? "",
            MeshScale = ModRegion.Num(o, "meshScale"),
            MeshBasis = Get(o, "meshBasis") ?? "",
            _extra = o,
        };
        if (o["meshOffset"] is JsonArray off && off.Count >= 3)
        {
            m.MeshOffsetX = ModRegion.Num(off[0]);
            m.MeshOffsetY = ModRegion.Num(off[1]);
            m.MeshOffsetZ = ModRegion.Num(off[2]);
        }
        ModMaterial.ReadInto(o, m.Materials);
        return m;
    }

    public JsonObject ToJson()
    {
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        o["source"] = Source;
        if (MeshFile.Length > 0) o["import"] = MeshFile; else o.Remove("import");
        if (MeshScale > 0) o["meshScale"] = MeshScale; else o.Remove("meshScale");
        if (MeshBasis.Length > 0) o["meshBasis"] = MeshBasis; else o.Remove("meshBasis");
        if (MeshOffsetX != 0 || MeshOffsetY != 0 || MeshOffsetZ != 0)
            o["meshOffset"] = new JsonArray(MeshOffsetX, MeshOffsetY, MeshOffsetZ);
        else o.Remove("meshOffset");
        ModMaterial.WriteInto(o, Materials);
        return o;
    }

    private static string? Get(JsonObject o, string key) =>
        o[key]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? o[key]!.GetValue<string>() : null;
}

/// <summary>
/// ONE PIECE of a part that dresses more than its own slot.
///
/// A customization part is a list of fragments, and a garment covering the whole character carries
/// one <c>CustomizationFragmentMesh</c> per body REGION -- the Clone Commando torso is five of
/// them: boots, torso, pack, legs, arms. A <see cref="ModPart"/> carries ONE mesh, which the
/// builder writes into every option it finds, so duplicating such a garment used to give the same
/// piece five times.
///
/// A region entry says what ONE of those pieces wears, and carries the whole authoring bundle for
/// it: its own import file, axes, scale, offset and artwork. A piece the part does not name keeps
/// the template's own geometry, so replacing one piece of five is one entry and not five.
///
/// Keyed by <see cref="Region"/> -- a LABEL read off the template's own mesh names (BOOT, TORS,
/// PACK, LEGS, ARMS) -- and never by position. Fragment export order is not stable between
/// templates: Clo010's suffixes run _0 _1 _14 _2 _3 and Hawks' run _1 _2 _14 _0.
/// </summary>
public sealed class ModRegion : IMeshAuthoring
{
    /// <summary>
    /// Which piece of the template this stands in for: BOOT, TORS, PACK, LEGS, ARMS and the rest
    /// of <see cref="PartDetails.RegionTokens"/>. A template whose mesh names carry no such token
    /// is keyed by the mesh's asset name instead -- see <c>PartDetails.RegionKeys</c>.
    /// </summary>
    public string Region = "";

    /// <summary>The game mesh this piece wears, and what <see cref="MeshFile"/> replaces.</summary>
    public string Mesh = "";

    /// <summary>A file to import over this piece's geometry. Optional -- textures alone repaint.</summary>
    public string MeshFile { get; set; } = "";

    public float MeshScale { get; set; }
    public string MeshBasis { get; set; } = "";
    public float MeshOffsetX { get; set; }
    public float MeshOffsetY { get; set; }
    public float MeshOffsetZ { get; set; }

    /// <summary>The game mesh being replaced, which is what an import is measured against.</summary>
    public string MeshSource => Mesh;

    /// <summary>One entry per material slot of THIS piece, in the order its objects sit.</summary>
    public List<ModMaterial> Materials { get; } = new();

    /// <summary>The slot at <paramref name="index"/>, growing the list to reach it.</summary>
    public ModMaterial Material(int index) => ModMaterial.At(Materials, index);

    /// <summary>Whether ANY slot of this piece carries artwork of the author's.</summary>
    public bool HasPaint => Materials.Any(m => m.Textures.Count > 0);

    /// <summary>Whether ANY slot of this piece names a game material and paints nothing on it.</summary>
    public bool HasNamedMaterial => Materials.Any(m => m.IsNamedOnly);

    /// <summary>Slot zero's textures, painted onto this piece the way a part's are.</summary>
    public Dictionary<string, string> Textures => Material(0).Textures;

    /// <summary>
    /// Whether this entry says anything at all beyond naming a piece.
    ///
    /// <see cref="Region"/> is deliberately NOT part of it: an entry that names only a piece says
    /// exactly what a missing entry says, which is "leave it alone".
    /// </summary>
    public bool IsEmpty =>
        Mesh.Length == 0 && MeshFile.Length == 0 && MeshScale == 0 && MeshBasis.Length == 0 &&
        MeshOffsetX == 0 && MeshOffsetY == 0 && MeshOffsetZ == 0 && Materials.All(m => m.IsEmpty);

    /// <summary>Fields this build does not know about, kept so a save cannot drop them.</summary>
    private JsonObject? _extra;

    public static ModRegion From(JsonObject o)
    {
        var r = new ModRegion
        {
            // CANONICALISED ON READ, so the model spells a piece the one way RegionKeys produces.
            // The builder's matcher folds BOOTS onto BOOT; the editor's HashSet lookup did not, so
            // a file written with the plural was drawn red as "not a piece of this template" while
            // the build bound it correctly -- and an edit made in that row was appended as a
            // SECOND entry for the same fragment, which the build then ignored.
            Region = PartDetails.Canon(Get(o, "region") ?? "") is { Length: > 0 } canon
                     ? canon : Get(o, "region") ?? "",
            Mesh = Get(o, "mesh") ?? "",
            MeshFile = Get(o, "import") ?? "",
            MeshBasis = Get(o, "meshBasis") ?? "",
            MeshScale = ModRegion.Num(o, "meshScale"),
            _extra = o,
        };
        if (o["meshOffset"] is JsonArray off && off.Count >= 3)
        {
            r.MeshOffsetX = ModRegion.Num(off[0]);
            r.MeshOffsetY = ModRegion.Num(off[1]);
            r.MeshOffsetZ = ModRegion.Num(off[2]);
        }
        ModMaterial.ReadInto(o, r.Materials);
        return r;
    }

    public JsonObject ToJson()
    {
        // Starts from what was read, the way every other sub-object does, so a `_comment` an author
        // wrote INSIDE a piece survives a save. ModMaterial does not do this -- WriteInto builds a
        // fresh object per slot -- and a hand-written key inside a `materials[i]` entry is lost
        // today. Not inherited here.
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        o["region"] = Region;
        if (Mesh.Length > 0) o["mesh"] = Mesh; else o.Remove("mesh");
        if (MeshFile.Length > 0) o["import"] = MeshFile; else o.Remove("import");
        if (MeshScale > 0) o["meshScale"] = MeshScale; else o.Remove("meshScale");
        if (MeshBasis.Length > 0) o["meshBasis"] = MeshBasis; else o.Remove("meshBasis");
        if (MeshOffsetX != 0 || MeshOffsetY != 0 || MeshOffsetZ != 0)
            o["meshOffset"] = new JsonArray(MeshOffsetX, MeshOffsetY, MeshOffsetZ);
        else o.Remove("meshOffset");
        ModMaterial.WriteInto(o, Materials);
        return o;
    }

    private static string? Get(JsonObject o, string key) =>
        o[key]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? o[key]!.GetValue<string>() : null;

    /// <summary>
    /// A number, however it happens to be stored.
    ///
    /// A node PARSED FROM TEXT converts to anything numeric. A node built IN MEMORY carries a
    /// concrete CLR type and refuses every other one -- so `GetValue&lt;double&gt;()` on the float this
    /// class itself wrote throws InvalidOperationException. Both cases happen: a mod is loaded
    /// from a file, and a part is round-tripped through ToJson/From in memory when it is
    /// duplicated. Reading it the obvious way worked on the first and crashed the Studio on the
    /// second, in a frame with no try/catch anywhere above it.
    /// </summary>
    internal static float Num(JsonObject o, string key) => Num(o[key]);

    internal static float Num(JsonNode? n)
    {
        if (n is null) return 0f;
        var v = n.AsValue();
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<double>(out var d)) return (float) d;
        if (v.TryGetValue<decimal>(out var m)) return (float) m;
        if (v.TryGetValue<int>(out var i)) return i;
        return 0f;
    }
}

/// <summary>
/// An enemy GROUP's stats, scaled.
///
/// THE PROJECT STORES THE MULTIPLIERS, not the numbers they expand into. A group is up to 32 stat
/// tables, and one health change is 16 copies and 32 number edits -- writing those into the file
/// would mean reopening the mod shows the OUTPUT and none of the intent, and changing your mind
/// from x0.5 to x0.6 would mean finding and editing 32 numbers that no longer say where they came
/// from. The build expands this; see EnemyStats.Build.
/// </summary>
public sealed class ModEnemyStats
{
    /// <summary>The tactical spec, by its asset name -- `CPD_TacticalSpec_Enemy_Generic_Marauder`.</summary>
    public string Group = "";

    /// <summary>Row name to multiplier, e.g. `BitReactorHealthSet.MaxHealth` -> 0.5.</summary>
    public Dictionary<string, double> Multipliers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each of these classes keeps its own, because they are not one type's helpers.</summary>
    private static string? Get(JsonObject o, string key) =>
        o[key]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? o[key]!.GetValue<string>() : null;

    public static ModEnemyStats From(JsonObject o)
    {
        var e = new ModEnemyStats { Group = Get(o, "group") ?? "" };
        if (o["multipliers"] is JsonObject m)
            foreach (var (k, v) in m)
                if (v?.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                    e.Multipliers[k] = v.GetValue<double>();
        return e;
    }

    public JsonObject To()
    {
        var o = new JsonObject { ["group"] = Group };
        var m = new JsonObject();
        foreach (var (k, v) in Multipliers) m[k] = v;
        o["multipliers"] = m;
        return o;
    }
}

public sealed class ModCopy
{
    /// <summary>The asset name the copy is built as.</summary>
    public string Id = "";

    /// <summary>The registered asset it is copied from, by full package name.</summary>
    public string Template = "";

    /// <summary>
    /// Which feature of the tool built this copy, and which version of that feature's recipe.
    ///
    /// Blank and zero mean "made before the tool recorded this", which is not the same as "made
    /// by hand" and is treated the same way: it cannot be shown to match what the box builds now.
    /// See <see cref="CharacterAuthor.Features"/> for what the number is for.
    /// </summary>
    public string MadeBy = "";

    public int Recipe;

    /// <summary>
    /// Names to substitute, which is how a copy stops pointing at the original's parts. A weapon
    /// loadout names its Type part; a copy that kept that name would be the same weapon under
    /// another filename.
    /// </summary>
    public Dictionary<string, string> RenameNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Give it a new identity guid, derived from its package name. On by default because the
    /// assets worth copying are the ones that HAVE an identity, and two of anything claiming one
    /// id is the fault that looks like the copy was never installed. See <see cref="GuidEdit"/>.
    /// </summary>
    public bool NewGuid = true;

    /// <summary>
    /// The name a player reads, when the copy should not keep the original's.
    ///
    /// Blank keeps it, which is right for a link in a chain nobody sees -- a gear kit's data asset
    /// or a weapon actor. It is the PART at the top of that chain that appears in a menu, and a
    /// copy of one that kept its name gives you two rows reading the same thing.
    /// </summary>
    public string Display = "";

    /// <summary>
    /// What the menu says about it. Replaces BOTH descriptions on a copy that has them -- the
    /// armoury part's short and long -- because a copy that kept its template's is telling the
    /// player it is the weapon it was copied from. Blank keeps them. The game's markup works.
    /// </summary>
    public string Description = "";

    /// <summary>
    /// Numbers to change in the COPY, as row / field / from / to.
    ///
    /// The same shape an override uses and for the same reason -- see
    /// <see cref="ModOverride.NumberChange"/> -- but pointed at a copy, which is the difference
    /// that makes it safe. An override edits the GAME's table, so a rebalanced weapon rebalances
    /// every weapon of that kind including the game's own. A copy is the author's alone.
    ///
    /// This is what gives a custom weapon CLASS stats of its own: the class part names an
    /// AttributeMetaData DataTable, a copy of that table can be re-pointed at, and its rows are
    /// addressable by name through DataTableEdit.
    /// </summary>
    public List<ModOverride.NumberChange> Numbers { get; } = new();

    /// <summary>
    /// Properties to ADD to the copy that its template does not serialise at all.
    ///
    /// Distinct from <see cref="Numbers"/>, and the distinction is the whole of it: a number CHANGES
    /// a value that is present, and a cooked package stores only what was set -- so a property left
    /// at its class default has no bytes to change and no offset to find. The E-5's weapon actor
    /// sets no `Blaster VFX Color` at all, which is exactly why it fires red, and why its colour
    /// could not be edited the way the DC-15A's can.
    ///
    /// Same shape as <see cref="ModOverride.AddScalars"/>, which has had this since a hero
    /// specialisation needed a slot tag it was never given -- but on a COPY, so the mod's own
    /// weapon changes and the game's does not.
    /// </summary>
    public List<ModOverride.ScalarAdd> AddScalars { get; } = new();

    /// <summary>
    /// ROWS ADDED TO A DATATABLE THIS MOD SHIPS AS ITS OWN, rather than to the game's.
    ///
    /// The same edit an override carries, on the other side of the line, and the character pool
    /// is why it matters. `DR_InitialCharacterPool` is a DataRegistry whose sources SEARCH
    /// `$AssetSearchRoots/Game/Customizations/` for tables matching `*InitialCharacterPool`, so a
    /// mod shipping its OWN table under that name is registered ALONGSIDE the game's rather than
    /// replacing it. Two recruit mods installed together then BOTH work -- which shipping a
    /// changed copy of the shared table can never do, because Unreal mounts one copy of a package
    /// and the loser's rows vanish entirely.
    ///
    /// Measured 2026-09-20 off DR_InitialCharacterPool's own properties.
    /// </summary>
    public List<ModOverride.RowAdd> AddRows { get; } = new();

    /// <summary>Entries to give a stance animation set it lacks: a tag, cloned from the entry for another tag. See PropertyAdd.AddAnimationEntries.</summary>
    public List<(string Tag, string Like)> AddAnimations { get; } = new();

    /// <summary>
    /// Tags to ADD to the copy's AllowedSlots (package) and its registry entry, if it has one --
    /// the same field <see cref="ModPart.AddTags"/> is, on a copy instead of a part. ADDITIVE only:
    /// a tag asked for here joins whatever the template already carried, so it is right when a
    /// copy needs to be reachable through an EXTRA gate without giving up the one its template
    /// has. It is NOT how the ATTACHMENTS container gets its own category tag -- that container
    /// STOPS being a Tops container, so it renames its category tag instead (`renameNames`,
    /// `Outfit.Torso` -> `Outfit.Attachments`), which REPLACES the old tag; `addTags` would leave
    /// `Outfit.Torso` sitting beside the new one, which is not what a container changing category
    /// wants.
    ///
    /// Distinct from <see cref="TagEdits"/>, which can add to or remove from ANY named container:
    /// this is the common case -- AllowedSlots only, additions only -- kept as its own field so a
    /// copy's project JSON reads the same "addTags" key a part's does, rather than the more
    /// general "tagEdits" form for what is, ordinarily, the same edit.
    /// </summary>
    public List<string> AddTags { get; } = new();

    /// <summary>
    /// Clear the copy's own <c>CustomizationFragmentSlot</c> entries out of its Fragments before
    /// any slot set of the mod's own is stamped in -- what the ATTACHMENTS container needs from
    /// its template, CPD_H_Outfit_Torso: the five Tops slots have no place on a wheel that offers
    /// attachments instead. The writing and its own read-back are <see cref="SlotAdd.ClearSlots"/>;
    /// this just asks for it.
    /// </summary>
    public bool ClearSlots;

    /// <summary>
    /// Tags added to and removed from ONE of the copy's tag containers, named by property.
    ///
    /// Per container rather than one list, because the two containers on a customization part say
    /// different things and an edit meant for one is wrong on the other: AllowedSlots is where a
    /// part may be worn and what the Armoury filters on, GameplayTags is what the part TELLS THE
    /// CHARACTER he has. A rename cannot serve both -- they are the same FName, so moving a class
    /// tag in AllowedSlots moves it in GameplayTags too, which is precisely how a kit that had
    /// just been given the tag its shot cinematic requires lost it again.
    /// </summary>
    public sealed record TagEdit(string Property, List<string> Add, List<string> Remove);

    public List<TagEdit> TagEdits { get; } = new();

    /// <summary>A NameProperty set outright, which a rename cannot do when the value carries an FName number. See PropertyAdd.SetName.</summary>
    public sealed record NameSet(string Export, string Property, string Value);

    public List<NameSet> SetNames { get; } = new();

    /// <summary>
    /// A picture of the author's own for the menu, a PNG or TGA in ModSource/Icons by name.
    ///
    /// Written INTO the copy's UI fragment the way the DC-17m's armoury part carries its picture:
    /// the image reference switched to a soft brush naming a texture the build authors from the
    /// file. Not a bank and not a tag: a mod's own bank was measured never to be read.
    /// </summary>
    public string Icon = "";

    /// <summary>
    /// Entries to ADD to a mesh palette, as tag -> mesh. Added, not re-keyed.
    ///
    /// A palette maps a weapon's type tag to the mesh it wears in that slot. Until now a copy could
    /// only take over an entry already there, so a weapon of the mod's own could exist in the copy
    /// only by stealing another weapon's place. An array in a cooked package turns out to be
    /// growable -- see <see cref="PropertyAdd.AddMeshOption"/> -- so a mod can have an entry of its
    /// own and leave every other weapon's alone.
    /// </summary>
    public Dictionary<string, string> AddOptions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Package-to-package redirects for HARD imports: what this asset must load, rather than what
    /// it merely names.
    ///
    /// Separate from <see cref="RenameNames"/> because the two live in different places. A soft
    /// object path is a pair of strings in the name map and moves when those strings are renamed.
    /// An import is a package ID in the container's store entry, and no amount of renaming touches
    /// it -- a Blueprint pointing at a class, or a gear kit pointing at its weapon actor, is this
    /// kind. Trying to move one with a rename fails with "not in the name map", which is true and
    /// unhelpful.
    /// </summary>
    public Dictionary<string, string> RepointImports { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Text to write into the copy, as property -> value: a name part's FirstName, LastName and
    /// BackgroundDescription. The same shape a part's <see cref="ModPart.Text"/> has, found the
    /// way a copy's description is -- anywhere in the package, by property name.
    /// </summary>
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Slot values for a copied CHARACTER DEFINITION, as slot tag -> object path ("" empties the
    /// slot). Written into the SlotOverrides map in place, by <see cref="SlotEdit"/> -- not by
    /// renaming, which cannot reach a numbered name such as an outfit colour.
    /// </summary>
    public Dictionary<string, string> Slots { get; } = new(StringComparer.Ordinal);

    private JsonObject? _extra;

    public static ModCopy From(JsonObject o)
    {
        var c = new ModCopy
        {
            Id = Get(o, "id") ?? "",
            Template = Get(o, "template") ?? "",
            NewGuid = o["newGuid"]?.GetValue<bool>() ?? true,
            ClearSlots = o["clearSlots"]?.GetValue<bool>() ?? false,
            MadeBy = Get(o, "madeBy") ?? "",
            Recipe = o["recipe"]?.GetValue<int>() ?? 0,
            Display = Get(o, "display") ?? "",
            Description = Get(o, "description") ?? "",
            Icon = Get(o, "icon") ?? "",
            _extra = o,
        };
        if (o["text"] is JsonObject texts)
            foreach (var kv in texts)
                if (kv.Value?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    c.Text[kv.Key] = kv.Value.GetValue<string>();
        // An EMPTY value is meaningful here -- it empties the slot -- so it is kept, unlike a
        // blank rename, which is a rename to nothing.
        if (o["slots"] is JsonObject slots)
            foreach (var kv in slots)
                if (kv.Value?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    c.Slots[kv.Key] = kv.Value.GetValue<string>();
        if (o["renameNames"] is JsonObject names)
            foreach (var kv in names)
                if (kv.Value?.GetValue<string>() is { Length: > 0 } to) c.RenameNames[kv.Key] = to;
        if (o["repointImports"] is JsonObject imports)
            foreach (var kv in imports)
                if (kv.Value?.GetValue<string>() is { Length: > 0 } to) c.RepointImports[kv.Key] = to;
        if (o["addOptions"] is JsonObject options)
            foreach (var kv in options)
                if (kv.Value?.GetValue<string>() is { Length: > 0 } to) c.AddOptions[kv.Key] = to;
        if (o["addRows"] is JsonArray crows)
            foreach (var e in crows)
                if (e is JsonObject cro && ModOverride.RowAdd.Parse(cro) is { } cadd)
                    c.AddRows.Add(cadd);
        if (o["addProperties"] is JsonArray caps)
            foreach (var e in caps)
                if (e is JsonObject cap && Get(cap, "export") is { Length: > 0 } capx &&
                    Get(cap, "name") is { Length: > 0 } capn)
                    c.AddScalars.Add(new ModOverride.ScalarAdd(capx, capn,
                                                               cap["value"]?.GetValue<double>() ?? 0));
        if (o["addAnimations"] is JsonArray cans)
            foreach (var e in cans)
                if (e is JsonObject can && Get(can, "tag") is { Length: > 0 } ctag && Get(can, "like") is { Length: > 0 } clike)
                    c.AddAnimations.Add((ctag, clike));
        if (o["setNames"] is JsonArray csn)
            foreach (var e in csn)
                if (e is JsonObject sno && Get(sno, "export") is { Length: > 0 } snx &&
                    Get(sno, "property") is { Length: > 0 } snp && Get(sno, "value") is { Length: > 0 } snv)
                    c.SetNames.Add(new NameSet(snx, snp, snv));
        if (o["addTags"] is JsonArray ctags)
            foreach (var t in ctags)
                if (t?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    c.AddTags.Add(t.GetValue<string>());
        if (o["tagEdits"] is JsonArray cte)
            foreach (var e in cte)
                if (e is JsonObject teo && Get(teo, "property") is { Length: > 0 } tep)
                {
                    var add = new List<string>();
                    var rem = new List<string>();
                    if (teo["add"] is JsonArray ta)
                        foreach (var t in ta) if (t?.GetValue<string>() is { Length: > 0 } v) add.Add(v);
                    if (teo["remove"] is JsonArray tr)
                        foreach (var t in tr) if (t?.GetValue<string>() is { Length: > 0 } v) rem.Add(v);
                    if (add.Count > 0 || rem.Count > 0) c.TagEdits.Add(new TagEdit(tep, add, rem));
                }
        if (o["numbers"] is JsonArray nums)
            foreach (var n in nums)
                if (n is JsonObject no && Get(no, "row") is { Length: > 0 } nrow &&
                    Get(no, "field") is { Length: > 0 } nfield)
                    c.Numbers.Add(new ModOverride.NumberChange(
                        nrow, nfield, no["was"]?.GetValue<double>() ?? 0,
                        no["now"]?.GetValue<double>() ?? 0));
        return c;
    }

    public JsonObject ToJson()
    {
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        o["id"] = Id;
        o["template"] = Template;
        o["newGuid"] = NewGuid;
        if (MadeBy.Length > 0) { o["madeBy"] = MadeBy; o["recipe"] = Recipe; }
        else { o.Remove("madeBy"); o.Remove("recipe"); }
        if (Display.Length > 0) o["display"] = Display; else o.Remove("display");
        if (Description.Length > 0) o["description"] = Description; else o.Remove("description");
        if (Icon.Length > 0) o["icon"] = Icon; else o.Remove("icon");
        if (Text.Count == 0) o.Remove("text");
        else
        {
            var texts = new JsonObject();
            foreach (var (property, value) in Text) texts[property] = value;
            o["text"] = texts;
        }
        if (Slots.Count == 0) o.Remove("slots");
        else
        {
            var slots = new JsonObject();
            foreach (var (tag, path) in Slots) slots[tag] = path;
            o["slots"] = slots;
        }
        if (RenameNames.Count == 0) o.Remove("renameNames");
        else
        {
            var names = new JsonObject();
            foreach (var (from, to) in RenameNames) names[from] = to;
            o["renameNames"] = names;
        }
        if (RepointImports.Count == 0) o.Remove("repointImports");
        else
        {
            var imports = new JsonObject();
            foreach (var (from, to) in RepointImports) imports[from] = to;
            o["repointImports"] = imports;
        }
        if (AddOptions.Count == 0) o.Remove("addOptions");
        else
        {
            var options = new JsonObject();
            foreach (var (tag, mesh) in AddOptions) options[tag] = mesh;
            o["addOptions"] = options;
        }
        if (Numbers.Count == 0) o.Remove("numbers");
        else
        {
            var nums = new JsonArray();
            foreach (var n in Numbers)
                nums.Add(new JsonObject
                {
                    ["row"] = n.Row, ["field"] = n.Field, ["was"] = n.Was, ["now"] = n.Now,
                });
            o["numbers"] = nums;
        }
        if (SetNames.Count > 0)
        {
            var sets = new JsonArray();
            foreach (var n in SetNames)
                sets.Add(new JsonObject { ["export"] = n.Export, ["property"] = n.Property, ["value"] = n.Value });
            o["setNames"] = sets;
        }
        else o.Remove("setNames");
        if (AddTags.Count > 0)
        {
            var ctags = new JsonArray();
            foreach (var t in AddTags) ctags.Add(t);
            o["addTags"] = ctags;
        }
        else o.Remove("addTags");
        if (ClearSlots) o["clearSlots"] = true; else o.Remove("clearSlots");
        if (TagEdits.Count > 0)
        {
            var edits = new JsonArray();
            foreach (var e in TagEdits)
            {
                var eo = new JsonObject { ["property"] = e.Property };
                if (e.Add.Count > 0) { var a = new JsonArray(); foreach (var t in e.Add) a.Add(t); eo["add"] = a; }
                if (e.Remove.Count > 0) { var r = new JsonArray(); foreach (var t in e.Remove) r.Add(t); eo["remove"] = r; }
                edits.Add(eo);
            }
            o["tagEdits"] = edits;
        }
        else o.Remove("tagEdits");
        if (AddAnimations.Count > 0)
        {
            var anims = new JsonArray();
            foreach (var (tag, like) in AddAnimations) anims.Add(new JsonObject { ["tag"] = tag, ["like"] = like });
            o["addAnimations"] = anims;
        }
        else o.Remove("addAnimations");
        if (AddRows.Count > 0)
        {
            var crows = new JsonArray();
            foreach (var add in AddRows) crows.Add(add.ToJson());
            o["addRows"] = crows;
        }
        else o.Remove("addRows");

        if (AddScalars.Count == 0) o.Remove("addProperties");
        else
        {
            var adds = new JsonArray();
            foreach (var a in AddScalars)
                adds.Add(new JsonObject
                {
                    ["export"] = a.Export, ["name"] = a.Name, ["value"] = a.Value,
                });
            o["addProperties"] = adds;
        }
        return o;
    }

    private static string? Get(JsonObject o, string key) =>
        o[key]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? o[key]!.GetValue<string>() : null;
}

public sealed class ModOverride
{
    /// <summary>The game package this replaces, by its full package name.</summary>
    public string Target = "";

    /// <summary>One name re-point: what is referenced now, what to reference instead, how many.</summary>
    public sealed record Repoint(string From, string To, int Expect);

    /// <summary>
    /// Every re-point this override asks for, in order.
    ///
    /// A list rather than one, because a single logical change often is not one name. A soft
    /// object reference is serialised as TWO FNames -- the package path and the object name -- so
    /// pointing an asset at a different asset is two re-points that must both land or the
    /// reference is left half-rewritten and resolves to nothing.
    /// </summary>
    public List<Repoint> Repoints { get; } = new();

    /// <summary>
    /// A new menu name for the overridden asset. Optional.
    ///
    /// Its first use is as an INSTRUMENT rather than a feature: a display name is the one change
    /// that is unmistakable on screen and involves no gameplay logic at all, so it answers "did
    /// this override take effect" without depending on a model of how the game gates anything.
    /// </summary>
    public string DisplayName = "";

    /// <summary>
    /// New description text for the overridden asset, replacing BOTH the short and the long one.
    ///
    /// One field for two properties because the game ships them identical on every weapon
    /// specialisation, and a mod author who wanted them to differ would be the first. Both are
    /// rewritten, because leaving one behind means a weapon that introduces itself twice and
    /// disagrees with itself once.
    ///
    /// It is the same edit as the name and it matters for the same reason: a rebalanced weapon
    /// whose text still describes the original is wearing somebody else's identity in the one
    /// place a player actually reads.
    /// </summary>
    public string Description = "";

    /// <summary>
    /// An FBX whose geometry replaces the target's.
    ///
    /// A PART carries its own copy of a mesh and imports into that. An override cannot: a weapon's
    /// mesh is named by shared palettes in a dozen other packages, all keyed by the weapon's type
    /// tag, so a copy under a new name is a copy nothing points at. The geometry has to go into the
    /// game's own package, shipped at the game's own id -- which is exactly what an override is.
    ///
    /// Same importer and the same refusals: every LOD's vertex and index counts have to fit what
    /// the package already declares, and a weight on a bone the section cannot address is dropped
    /// with a note rather than landing on bone zero.
    /// </summary>
    public string MeshFile = "";

    /// <summary>
    /// The same placement controls a part has, and needed for the same reason: a modelling package
    /// records a resize as a NODE SCALE and leaves the vertices alone, so a file can be the wrong
    /// size with nothing in it looking wrong. Blank means the file's own node, which is right far
    /// more often than not -- and the build reports the size it produced against the mesh it
    /// replaced, so a near miss is visible rather than found in game.
    /// </summary>
    public float MeshScale;
    public string MeshBasis = "";
    public float MeshOffsetX, MeshOffsetY, MeshOffsetZ;

    /// <summary>
    /// Accept a replacement that is a very different size from the mesh it replaces.
    ///
    /// The importer refuses one more than five times the height of its target, because across
    /// three exports of one helmet the file's own node scale read right, wrong by a hundredfold
    /// and wrong by seven percent -- and every one of those cost a game launch to discover.
    ///
    /// That check assumes a replacement is the same THING as its target, which is true of a helmet
    /// and false of a weapon: a gun is five meshes, and putting a whole rifle where the receiver
    /// goes is a deliberate six-fold difference. So the check can be waived, per override, by an
    /// author who has looked at the reported size and meant it. It still prints.
    /// </summary>
    public bool MeshAnySize;

    /// <summary>
    /// A PNG or TGA whose pixels replace the target texture's.
    ///
    /// The counterpart of <see cref="MeshFile"/>, and it exists for the same reason: a weapon's
    /// textures are named by materials in packages this mod does not ship, so a copy under a new
    /// name is a copy nothing samples. The pixels have to go into the game's own texture at the
    /// game's own id.
    ///
    /// Authored from the target itself, which is what makes it safe -- the template supplies the
    /// format, the mip layout and the dimensions, and an image that does not fit them is refused
    /// rather than written into a texture the GPU will read as noise.
    /// </summary>
    public string Image = "";

    /// <summary>
    /// A picture for the overridden asset's ICON — kept apart from <see cref="Image"/>, which is
    /// the asset's own PIXELS.
    ///
    /// THEY WERE ONE FIELD AND IT WAS DESTRUCTIVE. ModBuild reads Image in exactly one place, the
    /// texture branch, and there is no icon handling for an override anywhere in that file — so
    /// an icon set on a PART override made the build ask whether the part was a texture, get no
    /// for both the virtual and the plain case, and `continue`, silently dropping the WHOLE
    /// override: the display name, the description and the mesh along with the icon.
    ///
    /// THE BUILD DOES NOT READ THIS, on purpose, and that is not an omission. ModBuild's own
    /// words about a replacement's artwork are that "a replacement's textures are already
    /// separate overrides that repaint the game's own texture in place, so a copy would be a
    /// second mechanism for a job that has one". A part's icon IS a texture the part points at,
    /// so changing it is an override of THAT texture — which is a first-class feature of its
    /// own. The Studio records both: this field, so the editor can show what was chosen and
    /// Revert can find it, and the texture override that does the work.
    /// </summary>
    public string Icon = "";



    /// <summary>
    /// Names to rewrite IN THE NAME MAP, as from -> to.
    ///
    /// The blunt instrument, and the right one when a reference cannot be found by scanning. A
    /// re-point searches the export data for an eight-byte pattern and refuses when the count
    /// surprises it -- which is correct, and useless when the name's index is low enough that the
    /// pattern occurs by accident. One package path matched 393 times in a 15 KB asset that
    /// mentions it once.
    ///
    /// Renaming the map ENTRY changes every genuine use at once with no scanning at all. That is
    /// exactly what makes it dangerous: uses nobody enumerated change too. Use it when the name is
    /// specific enough that every use SHOULD change -- an asset path naming one asset -- and check
    /// the diff afterwards, never for a name that might mean two things.
    /// </summary>
    public Dictionary<string, string> RenameNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Package-to-package redirects for HARD imports: what the overridden asset must LOAD.
    ///
    /// The same field <see cref="ModCopy.RepointImports"/> carries, and separate from
    /// <see cref="RenameNames"/> for the same reason -- a soft path is two strings in the name map
    /// and an import is a package id in the store entry, which no rename touches.
    ///
    /// WHY AN OVERRIDE NEEDS IT. A copy can point anywhere and could not change what the game's
    /// own assets point at, which turned out to be the thing standing between a mod and the shop:
    /// the Armoury's stock is six tables naming 99 items by hard import
    /// (see <see cref="ShopTables"/>), and taking over a slot means editing the game's table.
    /// Copying the table does not avoid it -- the root names the three tiers by hard import too.
    ///
    /// The TARGET is a package name, used as given. A copy's version accepts a mod-relative id
    /// because a copy usually points at its own siblings; an override is aimed at the game.
    /// </summary>
    public Dictionary<string, string> RepointImports { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// ROWS TO ADD to the overridden DataTable, each copied from one already in it.
    ///
    /// The wall three features took the long way around. A shop slot, a mesh palette entry and a
    /// roster row were all TAKEN OVER rather than added, because a cooked container could not grow
    /// -- so a mod's own grenade cost the game a Bacta Stim and a mod's own recruit cost a
    /// databank entry. <see cref="DataTableEdit.AddRow"/> is what removes that trade for tables.
    ///
    /// COPIED, not constructed: a new row starts as the bytes of one the game itself wrote, and
    /// only the fields named in <see cref="RowAdd.Set"/> are changed. Building a row from nothing
    /// means emitting an unversioned header in schema order for an arbitrary struct, which is a
    /// much larger thing to get right and fails by producing a row that parses and means
    /// something else.
    /// </summary>
    public List<RowAdd> AddRows { get; } = new();

    /// <summary>
    /// One row to add: what to call it, which row to copy, and which of its fields to re-point.
    ///
    /// <see cref="Set"/> holds SOFT OBJECT PATHS only -- full object paths, "/A/B/C.C". That is
    /// what a pool row's character and pawn are, and what the shop tables' items are not (theirs
    /// are hard imports, which move through repointImports instead). A field that is neither is
    /// refused by the build rather than written wrongly.
    /// </summary>
    public sealed class RowAdd
    {
        public string Row = "";
        public string From = "";
        public Dictionary<string, string> Set { get; } = new(StringComparer.Ordinal);

        public static RowAdd? Parse(JsonObject o)
        {
            var add = new RowAdd
            {
                Row = Get(o, "row") ?? "",
                From = Get(o, "from") ?? "",
            };
            if (o["set"] is JsonObject set)
                foreach (var (k, v) in set)
                    if (v?.GetValue<string>() is { Length: > 0 } to) add.Set[k] = to;
            return add.Row.Length > 0 && add.From.Length > 0 ? add : null;
        }

        public JsonObject ToJson()
        {
            var o = new JsonObject { ["row"] = Row, ["from"] = From };
            if (Set.Count > 0)
            {
                var set = new JsonObject();
                foreach (var (k, v) in Set) set[k] = v;
                o["set"] = set;
            }
            return o;
        }
    }

    /// <summary>
    /// A recipe to append to a RecipeWeightedTable -- the game's own way of putting a story task,
    /// or a story RECRUIT, in front of the player: the campaign arc draws from these tables every
    /// cycle. <c>Recipe</c> is a copy's id or a full package name. The weight is against the
    /// table's existing entries, which the game sets at 1 each; auto-start runs the recipe the
    /// moment it is drawn, the way a recruit recipe must.
    /// </summary>
    public sealed class RecipeAdd
    {
        public string Recipe = "";
        public float Weight = 50f;
        public bool AutoStart = true;

        public static RecipeAdd? Parse(JsonObject o)
        {
            var add = new RecipeAdd { Recipe = Get(o, "recipe") ?? "" };
            if (o["weight"] is JsonValue w && w.TryGetValue<float>(out var weight)) add.Weight = weight;
            if (o["autoStart"] is JsonValue a && a.TryGetValue<bool>(out var auto)) add.AutoStart = auto;
            return add.Recipe.Length > 0 ? add : null;
        }

        public JsonObject ToJson() =>
            new() { ["recipe"] = Recipe, ["weight"] = Weight, ["autoStart"] = AutoStart };
    }

    /// <summary>Recipes appended to this table. See <see cref="RecipeAdd"/>.</summary>
    public List<RecipeAdd> AddRecipes { get; } = new();

    /// <summary>
    /// A MISSION APPENDED to a weighted mission table, displacing nothing.
    ///
    /// The difference between this and a re-pointed row is the whole reason it exists: taking over
    /// a row puts the author's mission in front of the player by removing a shipped one, and no
    /// table has a spare row to give -- `bEmptyOption` is false on all 65 rows across the five
    /// tables. <c>Mission</c> is a copy's id or a full package name; the weight is against the
    /// table's existing entries, which the game sets at 1 each.
    /// </summary>
    public sealed class MissionAdd
    {
        public string Mission = "";
        public float Weight = 1f;

        public static MissionAdd? Parse(JsonObject o)
        {
            var add = new MissionAdd { Mission = Get(o, "mission") ?? "" };
            if (o["weight"] is JsonValue w && w.TryGetValue<float>(out var weight)) add.Weight = weight;
            return add.Mission.Length > 0 ? add : null;
        }

        public JsonObject ToJson() => new() { ["mission"] = Mission, ["weight"] = Weight };
    }

    /// <summary>Missions appended to this table. See <see cref="MissionAdd"/>.</summary>
    public List<MissionAdd> AddMissions { get; } = new();

    /// <summary>
    /// A mission APPENDED to one campaign turn, placed with no table and no roll.
    ///
    /// The only route that is both guaranteed and free: the turn places the game's missions and
    /// this one beside them, where taking a slot re-points a shipped mission away and a weighted
    /// row can lose the draw. <c>Mission</c> is a copy's id or a full package name; <c>Turn</c> is
    /// the arc's own turn key, which is what `TurnsMap` is indexed by.
    /// </summary>
    public sealed class ArcMissionAdd
    {
        public string Mission = "";
        public int Turn;

        public static ArcMissionAdd? Parse(JsonObject o)
        {
            var add = new ArcMissionAdd { Mission = Get(o, "mission") ?? "" };
            if (o["turn"] is JsonValue t && t.TryGetValue<int>(out var turn)) add.Turn = turn;
            return add.Mission.Length > 0 ? add : null;
        }

        public JsonObject ToJson() => new() { ["mission"] = Mission, ["turn"] = Turn };
    }

    /// <summary>Missions appended to this ARC's turns. See <see cref="ArcMissionAdd"/>.</summary>
    public List<ArcMissionAdd> AddArcMissions { get; } = new();

    /// <summary>
    /// Gate tags to drop from the part's AllowedSlots.
    ///
    /// This is the honest way to open a gate. Re-pointing the tag's NAME leaves the gate
    /// technically present and relies on the substitute being satisfied; removing it says what is
    /// meant, and it works where a re-point cannot -- a short name index makes an eight-byte
    /// pattern that can occur by accident, and the re-point then refuses rather than guess.
    /// </summary>
    public List<string> RemoveTags { get; } = new();

    /// <summary>
    /// Gate tags to ADD to the part's AllowedSlots.
    ///
    /// Needed because removing a gate is not always enough to make a part offerable. Padawan and
    /// Warrior declare the slot they occupy (`Slot.Character.Specializations.Tactical.Primary`) and
    /// both appear once their character gate is gone; Rex and Anakin declare only
    /// `Accepts.Character.Specializations.Tactical` and neither does. The slot tag looks like what
    /// puts a part in the picker, so it has to be addable, not merely removable.
    /// </summary>
    public List<string> AddTags { get; } = new();

    /// <summary>
    /// Which tag container <see cref="RemoveTags"/> and <see cref="AddTags"/> edit.
    ///
    /// `AllowedSlots` for a customization part, which is what every override did before this
    /// existed and is therefore the default. A modification's shop item keeps its rarity in
    /// `ItemTags` instead -- a different container on a different kind of asset, edited exactly the
    /// same way -- and hard-coding the name meant the whole mechanism was reachable for one asset
    /// type only.
    ///
    /// Named rather than searched for. Two containers on one asset are two different statements,
    /// and picking whichever was found first is how a rarity edit would land on a slot gate.
    /// </summary>
    public string TagsProperty = DefaultTagsProperty;

    public const string DefaultTagsProperty = "AllowedSlots";

    /// <summary>
    /// COLOUR SWATCHES THE TARGET TAKES OFF THE MENU, that this override puts back. Only the ones
    /// turned OFF are stored, exactly as <see cref="ModPart.HidesFromMenu"/> does it.
    ///
    /// The same edit a duplicated part has had since slot takeovers were built, and it belongs on
    /// both sides for the reason Nada's report gave: he was not duplicating the commando helmet,
    /// he was REPLACING it, and a replacement had no way to reach the two containers that decide
    /// how many colours the player is offered. The part editor could and the override editor could
    /// not, which is an odd place for a capability to stop.
    ///
    /// TWO CONTAINERS, and that is why this is a field rather than something an author does with
    /// RemoveTags: a swatch is named in HiddenSlots and again in LockedSlots, and taking it out of
    /// one leaves either a swatch that is greyed out for no reason or one that is invisible and
    /// live. RemoveTags edits a single named container, so it cannot express this at all.
    /// </summary>
    public Dictionary<string, bool> HidesFromMenu { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WHETHER THE TARGET STILL LOCKS THE SLOT IT SITS IN. True is what it already does, so only
    /// false is written -- see <see cref="ModPart.KeepsOwnSlotLock"/> for what the switch is.
    ///
    /// On both sides for the reason the swatch handback is on both sides: "we should do it for
    /// both replacement and duplicates". A replacement is the ONLY way to unlock one of these for
    /// a character who already has it, because a duplicate is a new part nobody is wearing.
    /// </summary>
    public bool KeepsOwnSlotLock { get; set; } = true;

    /// <summary>
    /// A boolean to clear, found by the reference that follows it.
    ///
    /// `anchor` is a name whose FName reference appears exactly once in the export data; `back` is
    /// how many bytes before it the flag sits; `was` is what that byte must currently read. All
    /// three are checked and nothing is written unless every one holds -- see FlagEdit for why a
    /// flag is located this way rather than by name.
    /// </summary>
    public string FlagAnchor = "";
    public int FlagBack = 1;
    public int FlagWas = 1;

    /// <summary>
    /// A tag-container property to ADD, which the target does not currently carry.
    ///
    /// Everything else here edits something already serialised. This creates it -- see
    /// <see cref="PropertyAdd"/> for why that is a different problem and a more dangerous one.
    /// The export is named by CLASS because a package holds several and only one has the schema
    /// the property belongs to.
    /// </summary>
    public string AddPropertyExport = "";
    public string AddPropertyName = "";
    public List<string> AddPropertyTags = new();

    /// <summary>
    /// Numbers to change in the overridden asset, as row / field / from / to.
    ///
    /// Row and field are not always how the number is FOUND -- the build tries the table walk,
    /// then the property path, then <see cref="NumberEdit"/>, which locates a number by its current
    /// value and refuses when that value is not unique. They are how the change is CHECKED: the
    /// built asset is read back and the named row's named field has to read the new value.
    /// Recording only "17 becomes 99" would patch whichever 17 it found and call it success, which
    /// is the failure this whole build keeps having to design around.
    ///
    /// DOUBLE, not float, and it costs nothing to say so. The numbers here reach an eight-byte
    /// property as often as a four-byte one -- a weapon modification's stat is a double -- and
    /// rounding 0.06 through a float on the way in writes 0.059999998658895493 into it. The JSON
    /// has always held these as doubles; only this record was narrowing them.
    /// </summary>
    public sealed record NumberChange(string Row, string Field, double Was, double Now);

    /// <summary>
    /// Mesh-palette entries to ADD to the overridden palette, as tag -> mesh package.
    ///
    /// The same shape a copy uses, pointed at a GAME palette. It is how a weapon with a type tag
    /// of its OWN gets attachment meshes: fitting a modification points the slot at the GAME's
    /// palette for that style, which holds no entry under a minted tag, so the piece vanishes.
    /// ADDING an entry there rather than re-keying one leaves every weapon already in that palette
    /// untouched -- which is the difference between this and the option the notes rejected as
    /// "it steals a donor weapon's attachment mesh".
    ///
    /// It is still an override of a SHARED asset, so two mods doing it to the same palette
    /// collide; the build's clash check is what says so.
    /// </summary>
    public Dictionary<string, string> AddOptions { get; } = new(StringComparer.Ordinal);

    public List<NumberChange> Numbers { get; } = new();

    /// <summary>
    /// A property change that is NOT a number.
    ///
    /// WHY A SECOND LIST RATHER THAN A WIDER NumberChange. Every change this project has ever
    /// recorded has been four or eight bytes written over four or eight bytes, which is why
    /// `was`/`now` could be doubles and why the build has one writer. A name, a string and an
    /// array entry are none of those, and widening the old record would have changed the meaning
    /// of every `numbers` entry in every project already on disk.
    ///
    /// ADDITIVE ON PURPOSE. A project saved before this existed reads back with an empty list and
    /// nothing else about it changes, and a project saved after it is still read by nothing that
    /// does not know the key -- `_extra` carries anything unrecognised through a round trip
    /// already.
    ///
    /// KIND is what the build dispatches on, and it is a string rather than an enum so an
    /// unknown one can be REPORTED rather than crash a build written before it existed. "name"
    /// is the only one implemented; "string", "text" and "tag" are the ones it was shaped for.
    ///
    /// WAS is carried for CHECKING, exactly as NumberChange carries it: a write that finds
    /// something other than what the author saw is a write aimed at the wrong bytes.
    /// </summary>
    public sealed record ValueChange(string Row, string Field, string Kind, string Was, string Now);

    public List<ValueChange> Values { get; } = new();

    /// <summary>
    /// One component of one actor's transform, in a cooked LEVEL.
    ///
    /// Deliberately NOT a <see cref="NumberChange"/>, even though it writes a number. The three
    /// ways a NumberChange is resolved -- a table row walk, a property by name, a search by value
    /// -- are all wrong in a sublevel: it has no rows, its component names repeat fifty times
    /// over, and a search by value in a map full of coordinates would find somebody else's prop
    /// and report success. A move is addressed from a uniquely-named ACTOR and follows its
    /// RootComponent, which is the only address a level actually offers. See
    /// <see cref="LevelEdit"/>.
    ///
    /// <paramref name="Was"/> is checked before the write, and the result is read back through the
    /// same walk afterwards.
    /// </summary>
    public sealed record Move(string Actor, string Path, double Was, double Now);

    public List<Move> Moves { get; } = new();

    /// <summary>
    /// WHAT A COMPONENT DRAWS -- swapped for something else in the same level, or for nothing.
    ///
    /// This is how a level gets an actor REMOVED and another one PUT IN ITS PLACE without the
    /// thing that has never worked: growing a cooked .umap. A component's mesh is an
    /// `FPackageIndex`, four bytes naming an import slot. Point it at a slot the package already
    /// has and the chair becomes a crate; point it at zero and it draws nothing at all.
    ///
    /// So it is the same shape of edit as a <see cref="Move"/> -- overwrite a value that is
    /// already there, in place, nothing grows -- and it inherits the same guarantees: the build
    /// checks what was there first, and the verifier reads it back out of the packed container.
    ///
    /// <paramref name="Mesh"/> empty means NOTHING. <paramref name="Was"/> is the mesh expected
    /// to be there now, by leaf name, and is checked before the write for the same reason a
    /// move's `was` is: an edit aimed at the wrong actor should refuse rather than land.
    ///
    /// THE LIMIT, SAID PLAINLY: the new mesh has to be one the package ALREADY references. A
    /// level that draws no crate anywhere cannot be given one this way.
    ///
    /// "Adding a genuinely new asset to a cooked level is still the gate" used to stand here.
    /// It no longer does -- see <see cref="Spawn"/>, confirmed in game 2026-09-16. What is still
    /// unproven is adding an IMPORT, which is what a mesh from another package would need.
    /// </summary>
    public sealed record Swap(string Actor, string Path, string Was, string Mesh);

    public List<Swap> Swaps { get; } = new();

    /// <summary>
    /// AN ACTOR THAT WAS NOT THERE BEFORE -- the thing a Swap's own note calls "still the gate".
    ///
    /// A copy of <paramref name="Donor"/>, appended to the package and listed in the level's
    /// Actors array, then moved to X/Y/Z. Copying rather than authoring is deliberate: the donor's
    /// bytes are known to load, and the new actor differs from it by its component pointers, its
    /// FName number and its position.
    ///
    /// CONFIRMED IN GAME 2026-09-16: a copy of the hub holotable's centre piece, appended to
    /// HUB_Environment_Pit and floating four metres above the original. The dependency bundle is
    /// what made it work -- an export that declares none is never instantiated, and nothing
    /// outside the game can see that.
    ///
    /// THE LIMIT, SAID PLAINLY AND IT IS THE SAME ONE A SWAP HAS: the copy draws whatever the
    /// donor draws, because the mesh is an import the package already holds. Putting a mesh from
    /// ANOTHER level in here needs an import added as well, which is the next unproven thing and
    /// is deliberately not stacked on top of this one.
    /// </summary>
    /// <summary>
    /// <paramref name="TakeDonorSlot"/> is a PROBE, not a feature: instead of lengthening the
    /// level's Actors array, the donor's own entry is re-pointed at the copy. It exists to tell
    /// "the new export is never instantiated" apart from "a longer Actors array is not read",
    /// which nothing outside the game can distinguish.
    /// </summary>
    /// <param name="Mesh">
    /// A mesh package from ANYWHERE in the game, imported into this level so the copy can draw it.
    /// Empty means the copy keeps whatever its donor draws, which is the limit a Swap also has.
    /// </param>
    /// <param name="Id">
    /// WHO WROTE THIS ONE, so a tool can rewrite its own spawns without touching anybody else's.
    ///
    /// A move is identified by the actor and the property it overwrites, so saving the same move
    /// twice replaces it. A spawn overwrites nothing -- two identical spawns are two actors, which
    /// is a legitimate thing to ask for -- so there is no natural key, and a level editor saving
    /// into a project twice would double every actor it had added. This is that key, minted by
    /// whatever created the spawn. Empty means nothing owns it and nothing will rewrite it.
    /// </param>
    public sealed record Spawn(string Donor, double X, double Y, double Z,
                               bool TakeDonorSlot = false, string Mesh = "", string Id = "");

    public List<Spawn> Spawns { get; } = new();

    /// <summary>
    /// Scalar properties to ADD -- a float, an int, a bool or a byte-backed enum the target does
    /// not currently serialise. One entry per property.
    ///
    /// A list rather than one because the useful edits come in pairs and are meaningless apart: a
    /// SizeBox's HeightOverride does nothing without its bOverride_HeightOverride, and shipping
    /// half of that is a mod that installs, reads back clean and changes nothing.
    ///
    /// <paramref name="Export"/> is matched against the export's own NAME first and its class
    /// second, because the packages worth editing hold several exports of one class.
    /// </summary>
    public sealed record ScalarAdd(string Export, string Name, double Value);

    /// <summary>
    /// CLASS-VALUED properties to add: a ClassProperty or ObjectProperty the target does not
    /// serialise, pointed at another asset.
    ///
    /// The edit Daelen asked for, 2026-09-09, having found that a property could be added and not
    /// given a value. Its use is per-tier ability progression: every Rocket Strike tier inherits
    /// one `CooldownGameplayEffectClass` from `GA_RocketStrike_T1`, so a change to that effect
    /// changes all six at once. Naming a DIFFERENT effect on each tier is what Sabotage does --
    /// GE_Cooldown_Sabotage_T1..4, one per ability -- and this is how a mod can do the same.
    ///
    /// <paramref name="Target"/> is a package, and the class inside it is what gets imported:
    /// `/Game/.../GE_Cooldown_Sabotage_T2` names the export `GE_Cooldown_Sabotage_T2_C`. That is
    /// the `_C` a Blueprint asset carries, and it is the value a ClassProperty holds.
    /// </summary>
    public sealed record ClassAdd(string Export, string Name, string Target);

    /// <summary>
    /// TAG CONTAINERS to add: an FGameplayTagContainer the target does not serialise, holding
    /// <paramref name="Tags"/>.
    ///
    /// The other half of what Daelen asked for on 2026-09-09 -- inherited CDO values set on a
    /// local override, "including FGameplayTagContainer, ClassProperty, ObjectProperty". The
    /// container edit has existed since 1.0 as `addProperty`, and could add exactly ONE, while
    /// `addProperties` and `addClasses` were lists from the start. An ability needing two -- an
    /// asset tag container and a grant container, say -- had no way to say so.
    ///
    /// `addProperty` still loads and still runs, first. This is the list beside it.
    /// </summary>
    public sealed record ContainerAdd(string Export, string Name, List<string> Tags);

    public List<ScalarAdd> AddScalars { get; } = new();

    /// <summary>Class-valued properties to add. See <see cref="ClassAdd"/>.</summary>
    public List<ClassAdd> AddClasses { get; } = new();

    /// <summary>Tag containers to add. See <see cref="ContainerAdd"/>.</summary>
    public List<ContainerAdd> AddTagContainers { get; } = new();

    /// <summary>Versions to give a stance archetype: a stance set (package path) for a rig tag. See PropertyAdd.AddArchetypeVersion.</summary>
    public List<(string Set, string Rig)> AddVersions { get; } = new();

    /// <summary>
    /// WHAT EACH MATERIAL SLOT WEARS, for a replacement whose imported model has more materials
    /// than the asset it replaces. Exactly the list a <see cref="ModPart"/> carries, and read and
    /// written by the same two helpers.
    ///
    /// It was missing, and the gap is subtler than it looks. Replacing a one-material helmet with
    /// a six-material model already GREW the array -- the build says "material slots 1 -> 6" and
    /// every section comes through wearing its own slot. What it could not do was say what those
    /// five new slots should wear, so all of them fell back to the template's first material and
    /// the result in game is a model painted entirely in the body's paint. Reported as "when
    /// doing replacement you are forced to only the material slots that the game model had",
    /// which is what that looks like from the outside.
    ///
    /// The build path for a part has handled this for a long time -- a slot either NAMES a game
    /// material outright, costing one package import, or carries textures of its own and gets a
    /// repainted copy. None of it was reachable from an override because there was nowhere on an
    /// override to put the answer.
    /// </summary>
    public List<ModMaterial> Materials { get; } = new();

    /// <summary>True when this override asks for a property that is not there yet.</summary>
    public bool HasAddProperty => AddPropertyExport.Length > 0 && AddPropertyName.Length > 0 &&
                                  AddPropertyTags.Count > 0;

    /// <summary>True when this override asks for a flag to be cleared.</summary>
    public bool HasFlag => !string.IsNullOrWhiteSpace(FlagAnchor);

    /// <summary>True when this override asks for a name re-point.</summary>
    public bool HasRepoint => Repoints.Count > 0;

    /// <summary>True when this override asks for anything at all.</summary>
    /// <summary>Whether any slot says anything -- a named material, or paint of its own.</summary>
    public bool HasMaterials => Materials.Any(m => !m.IsEmpty);

    /// <summary>
    /// Slots the build adds to this override's part, filled from the project's slot sets at build
    /// time (see <see cref="SlotSets.Resolve"/>). TRANSIENT: never saved -- the slot sets are the
    /// source of truth, and a copy here would be a second one to drift.
    /// </summary>
    public List<SlotAdd.Slot> AddSlots { get; set; } = new();

    public bool HasEdit => !string.IsNullOrWhiteSpace(MeshFile) || !string.IsNullOrWhiteSpace(Image) ||
                           HasMaterials ||
                           HasRepoint || RemoveTags.Count > 0 || AddTags.Count > 0 ||
                           HidesFromMenu.Any(kv => !kv.Value) || !KeepsOwnSlotLock ||
                           RenameNames.Count > 0 || RepointImports.Count > 0 ||
                           AddRows.Count > 0 || AddRecipes.Count > 0 || AddMissions.Count > 0 || AddArcMissions.Count > 0 ||
                           HasFlag || HasAddProperty ||
                           AddScalars.Count > 0 || AddClasses.Count > 0 ||
                           AddTagContainers.Any(x => x.Tags.Count > 0) ||
                           AddOptions.Count > 0 || Numbers.Count > 0 || Moves.Count > 0 ||
                           Swaps.Count > 0 ||
                           // A SPAWN IS AN EDIT, and the biggest one this builder makes -- it is
                           // the only kind that makes a package GROW. Leaving it out meant a
                           // spawn-only override counted as asking for nothing, so the read-back's
                           // "every override was built" check never ran for the one edit that most
                           // needed it.
                           Spawns.Count > 0 ||
                           AddVersions.Count > 0 ||
                           !string.IsNullOrWhiteSpace(Description) ||
                           !string.IsNullOrWhiteSpace(DisplayName) ||
                           AddSlots.Count > 0;

    /// <summary>Fields this build does not know about, kept so a save cannot drop them.</summary>
    private JsonObject? _extra;

    public static ModOverride From(JsonObject o)
    {
        var ov = new ModOverride
        {
            Target = Get(o, "target") ?? "",
            DisplayName = Get(o, "displayName") ?? "",
            Description = Get(o, "description") ?? "",
            MeshFile = Get(o, "import") ?? "",
            MeshScale = (float) (o["meshScale"]?.GetValue<double>() ?? 0),
            MeshBasis = Get(o, "meshBasis") ?? "",
            MeshOffsetX = (float) (o["meshOffsetX"]?.GetValue<double>() ?? 0),
            MeshOffsetY = (float) (o["meshOffsetY"]?.GetValue<double>() ?? 0),
            MeshOffsetZ = (float) (o["meshOffsetZ"]?.GetValue<double>() ?? 0),
            MeshAnySize = o["meshAnySize"]?.GetValue<bool>() ?? false,
            Image = Get(o, "image") ?? "",
            Icon = Get(o, "icon") ?? "",
            TagsProperty = Get(o, "tagsProperty") ?? DefaultTagsProperty,
            _extra = o,
        };
        // The same two shapes a part's materials use, through the same reader, so a slot written
        // by one editor reads back in the other.
        ModMaterial.ReadInto(o, ov.Materials);

        if (o["renameNames"] is JsonObject ren)
            foreach (var (k, v) in ren)
                if (v?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    ov.RenameNames[k] = v.GetValue<string>();

        if (o["repointImports"] is JsonObject ovImports)
            foreach (var (k, v) in ovImports)
                if (v?.GetValue<string>() is { Length: > 0 } to)
                    ov.RepointImports[k] = to;

        // Same key and same shape as a part's, so a project reads the same either side.
        if (o["hidesFromMenu"] is JsonObject ovHide)
            foreach (var (k, v) in ovHide)
                if (v?.GetValueKind() == System.Text.Json.JsonValueKind.False ||
                    v?.GetValueKind() == System.Text.Json.JsonValueKind.True)
                    ov.HidesFromMenu[k] = v.GetValue<bool>();

        if (o["keepsOwnSlotLock"]?.GetValueKind() is System.Text.Json.JsonValueKind.True
                                                  or System.Text.Json.JsonValueKind.False)
            ov.KeepsOwnSlotLock = o["keepsOwnSlotLock"]!.GetValue<bool>();

        if (o["addRows"] is JsonArray ovRows)
            foreach (var entry in ovRows)
                if (entry is JsonObject ro && RowAdd.Parse(ro) is { } add)
                    ov.AddRows.Add(add);

        if (o["addRecipes"] is JsonArray ovRecipes)
            foreach (var entry in ovRecipes)
                if (entry is JsonObject rc && RecipeAdd.Parse(rc) is { } recipe)
                    ov.AddRecipes.Add(recipe);

        if (o["addMissions"] is JsonArray ovMissions)
            foreach (var entry in ovMissions)
                if (entry is JsonObject mo && MissionAdd.Parse(mo) is { } mission)
                    ov.AddMissions.Add(mission);

        if (o["addArcMissions"] is JsonArray ovArcMsn)
            foreach (var entry in ovArcMsn)
                if (entry is JsonObject amo && ArcMissionAdd.Parse(amo) is { } arcMission)
                    ov.AddArcMissions.Add(arcMission);

        if (o["removeTags"] is JsonArray rem)
            foreach (var t in rem)
                if (t?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    ov.RemoveTags.Add(t.GetValue<string>());

        if (o["addTags"] is JsonArray addt)
            foreach (var t in addt)
                if (t?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    ov.AddTags.Add(t.GetValue<string>());

        if (o["addProperty"] is JsonObject ap)
        {
            ov.AddPropertyExport = Get(ap, "export") ?? "";
            ov.AddPropertyName = Get(ap, "name") ?? "";
            if (ap["tags"] is JsonArray apTags)
                foreach (var t in apTags)
                    if (t?.GetValue<string>() is { Length: > 0 } s) ov.AddPropertyTags.Add(s);
        }

        if (o["addOptions"] is JsonObject ovOptions)
            foreach (var kv in ovOptions)
                if (kv.Value?.GetValue<string>() is { Length: > 0 } to) ov.AddOptions[kv.Key] = to;

        if (o["addProperties"] is JsonArray aps)
            foreach (var e in aps)
                if (e is JsonObject ape && Get(ape, "export") is { Length: > 0 } apx &&
                    Get(ape, "name") is { Length: > 0 } apn)
                    ov.AddScalars.Add(new ScalarAdd(apx, apn, ape["value"]?.GetValue<double>() ?? 0));

        if (o["addClasses"] is JsonArray acs)
            foreach (var e in acs)
                if (e is JsonObject ace && Get(ace, "export") is { Length: > 0 } acx &&
                    Get(ace, "name") is { Length: > 0 } acn &&
                    Get(ace, "target") is { Length: > 0 } act)
                    ov.AddClasses.Add(new ClassAdd(acx, acn, act));

        if (o["addTagContainers"] is JsonArray atcs)
            foreach (var e in atcs)
                if (e is JsonObject atc && Get(atc, "export") is { Length: > 0 } atx &&
                    Get(atc, "name") is { Length: > 0 } atn && atc["tags"] is JsonArray atl)
                {
                    var atTags = new List<string>();
                    foreach (var t in atl)
                        if (t?.GetValue<string>() is { Length: > 0 } ats) atTags.Add(ats);
                    if (atTags.Count > 0) ov.AddTagContainers.Add(new ContainerAdd(atx, atn, atTags));
                }

        if (o["addVersions"] is JsonArray avs)
            foreach (var e in avs)
                if (e is JsonObject av && Get(av, "set") is { Length: > 0 } avs1 && Get(av, "rig") is { Length: > 0 } avr)
                    ov.AddVersions.Add((avs1, avr));

        if (o["clearFlag"] is JsonObject cf)
        {
            ov.FlagAnchor = Get(cf, "before") ?? "";
            ov.FlagBack = cf["back"]?.GetValue<int>() ?? 1;
            ov.FlagWas = cf["was"]?.GetValue<int>() ?? 1;
        }

        if (o["numbers"] is JsonArray nums)
            foreach (var n in nums)
                if (n is JsonObject one)
                {
                    var row = Get(one, "row") ?? "";
                    var field = Get(one, "field") ?? "BaseValue";
                    if (row.Length == 0) continue;
                    ov.Numbers.Add(new NumberChange(row, field,
                        one["was"]?.GetValue<double>() ?? 0d,
                        one["now"]?.GetValue<double>() ?? 0d));
                }

        if (o["values"] is JsonArray vals)
            foreach (var v in vals)
                if (v is JsonObject one &&
                    Get(one, "row") is { Length: > 0 } vrow &&
                    Get(one, "field") is { Length: > 0 } vfield)
                    ov.Values.Add(new ValueChange(vrow, vfield, Get(one, "kind") ?? "name",
                                                  Get(one, "was") ?? "", Get(one, "now") ?? ""));

        if (o["moves"] is JsonArray moves)
            foreach (var m in moves)
                if (m is JsonObject one)
                {
                    var actor = Get(one, "actor") ?? "";
                    var path = Get(one, "path") ?? "";
                    if (actor.Length == 0 || path.Length == 0) continue;
                    ov.Moves.Add(new Move(actor, path,
                        one["was"]?.GetValue<double>() ?? 0d,
                        one["now"]?.GetValue<double>() ?? 0d));
                }

        if (o["swaps"] is JsonArray swaps)
            foreach (var m in swaps)
                if (m is JsonObject one)
                {
                    var actor = Get(one, "actor") ?? "";
                    var path = Get(one, "path") ?? "";
                    if (actor.Length == 0 || path.Length == 0) continue;
                    ov.Swaps.Add(new Swap(actor, path, Get(one, "was") ?? "", Get(one, "mesh") ?? ""));
                }

        if (o["spawns"] is JsonArray spawns)
            foreach (var m in spawns)
                if (m is JsonObject one)
                {
                    var donor = Get(one, "donor") ?? "";
                    if (donor.Length == 0) continue;
                    ov.Spawns.Add(new Spawn(donor,
                        one["x"]?.GetValue<double>() ?? 0d,
                        one["y"]?.GetValue<double>() ?? 0d,
                        one["z"]?.GetValue<double>() ?? 0d,
                        one["takeDonorSlot"]?.GetValue<bool>() ?? false,
                        Get(one, "mesh") ?? "",
                        Get(one, "id") ?? ""));
                }

        // `repointTag` is sugar for a one-element `repointTags`. Both are read so a mod file
        // written before the list existed still loads.
        if (o["repointTag"] is JsonObject rp) AddRepoint(ov, rp);
        if (o["repointTags"] is JsonArray rps)
            foreach (var n in rps)
                if (n is JsonObject one) AddRepoint(ov, one);
        return ov;
    }

    private static void AddRepoint(ModOverride ov, JsonObject o)
    {
        var from = Get(o, "from") ?? "";
        var to = Get(o, "to") ?? "";
        if (from.Length == 0 || to.Length == 0) return;
        ov.Repoints.Add(new Repoint(from, to, o["expect"]?.GetValue<int>() ?? 1));
    }

    public JsonObject ToJson()
    {
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        o["target"] = Target;
        if (!string.IsNullOrWhiteSpace(DisplayName)) o["displayName"] = DisplayName;
        else o.Remove("displayName");
        if (!string.IsNullOrWhiteSpace(Description)) o["description"] = Description;
        else o.Remove("description");
        if (!string.IsNullOrWhiteSpace(MeshFile)) o["import"] = MeshFile;
        else o.Remove("import");
        if (MeshScale > 0) o["meshScale"] = MeshScale; else o.Remove("meshScale");
        if (!string.IsNullOrWhiteSpace(MeshBasis)) o["meshBasis"] = MeshBasis; else o.Remove("meshBasis");
        if (MeshOffsetX != 0) o["meshOffsetX"] = MeshOffsetX; else o.Remove("meshOffsetX");
        if (MeshOffsetY != 0) o["meshOffsetY"] = MeshOffsetY; else o.Remove("meshOffsetY");
        if (MeshOffsetZ != 0) o["meshOffsetZ"] = MeshOffsetZ; else o.Remove("meshOffsetZ");
        if (MeshAnySize) o["meshAnySize"] = true; else o.Remove("meshAnySize");
        if (Image.Length > 0) o["image"] = Image; else o.Remove("image");
        if (Icon.Length > 0) o["icon"] = Icon; else o.Remove("icon");
        ModMaterial.WriteInto(o, Materials);
        if (HasAddProperty)
        {
            var apTags = new JsonArray();
            foreach (var t in AddPropertyTags) apTags.Add(t);
            o["addProperty"] = new JsonObject
            {
                ["export"] = AddPropertyExport,
                ["name"] = AddPropertyName,
                ["tags"] = apTags,
            };
        }
        else o.Remove("addProperty");

        if (AddScalars.Count > 0)
        {
            var aps = new JsonArray();
            foreach (var a in AddScalars)
                aps.Add(new JsonObject { ["export"] = a.Export, ["name"] = a.Name, ["value"] = a.Value });
            o["addProperties"] = aps;
        }
        else o.Remove("addProperties");

        if (AddClasses.Count > 0)
        {
            var acs = new JsonArray();
            foreach (var a in AddClasses)
                acs.Add(new JsonObject { ["export"] = a.Export, ["name"] = a.Name, ["target"] = a.Target });
            o["addClasses"] = acs;
        }
        else o.Remove("addClasses");

        // An EMPTY container never reaches the file. The editor keeps one while an author is
        // retyping it, and AddTagContainer refuses one anyway -- so dropping it here is what
        // stops a half-finished edit becoming a build that skips for a reason nobody asked for.
        if (AddTagContainers.Any(x => x.Tags.Count > 0))
        {
            var atcs = new JsonArray();
            foreach (var a in AddTagContainers.Where(x => x.Tags.Count > 0))
            {
                var atl = new JsonArray();
                foreach (var t in a.Tags) atl.Add(t);
                atcs.Add(new JsonObject { ["export"] = a.Export, ["name"] = a.Name, ["tags"] = atl });
            }
            o["addTagContainers"] = atcs;
        }
        else o.Remove("addTagContainers");

        if (AddVersions.Count > 0)
        {
            var avs = new JsonArray();
            foreach (var (set, rig) in AddVersions) avs.Add(new JsonObject { ["set"] = set, ["rig"] = rig });
            o["addVersions"] = avs;
        }
        else o.Remove("addVersions");

        if (AddOptions.Count == 0) o.Remove("addOptions");
        else
        {
            var ovOpts = new JsonObject();
            foreach (var (tag, mesh) in AddOptions) ovOpts[tag] = mesh;
            o["addOptions"] = ovOpts;
        }

        if (HasFlag)
            o["clearFlag"] = new JsonObject
            {
                ["before"] = FlagAnchor,
                ["back"] = FlagBack,
                ["was"] = FlagWas,
            };
        else o.Remove("clearFlag");

        if (RenameNames.Count > 0)
        {
            var ren = new JsonObject();
            foreach (var (k, v) in RenameNames) ren[k] = v;
            o["renameNames"] = ren;
        }
        else o.Remove("renameNames");

        if (RepointImports.Count > 0)
        {
            var imports = new JsonObject();
            foreach (var (from, to) in RepointImports) imports[from] = to;
            o["repointImports"] = imports;
        }
        else o.Remove("repointImports");

        if (AddRows.Count > 0)
        {
            var rows = new JsonArray();
            foreach (var add in AddRows) rows.Add(add.ToJson());
            o["addRows"] = rows;
        }
        else o.Remove("addRows");

        if (AddRecipes.Count > 0)
        {
            var recipes = new JsonArray();
            foreach (var add in AddRecipes) recipes.Add(add.ToJson());
            o["addRecipes"] = recipes;
        }
        else o.Remove("addRecipes");

        if (AddMissions.Count > 0)
        {
            var missions = new JsonArray();
            foreach (var add in AddMissions) missions.Add(add.ToJson());
            o["addMissions"] = missions;
        }
        else o.Remove("addMissions");

        if (AddArcMissions.Count > 0)
        {
            var arcMissions = new JsonArray();
            foreach (var add in AddArcMissions) arcMissions.Add(add.ToJson());
            o["addArcMissions"] = arcMissions;
        }
        else o.Remove("addArcMissions");

        foreach (var (key, list) in new[] { ("removeTags", RemoveTags), ("addTags", AddTags) })
        {
            if (list.Count == 0) { o.Remove(key); continue; }
            var arr = new JsonArray();
            foreach (var t in list) arr.Add(t);
            o[key] = arr;
        }
        if (Numbers.Count > 0)
        {
            var nums = new JsonArray();
            foreach (var n in Numbers)
                nums.Add(new JsonObject
                {
                    ["row"] = n.Row, ["field"] = n.Field,
                    ["was"] = n.Was, ["now"] = n.Now,
                });
            o["numbers"] = nums;
        }
        else o.Remove("numbers");

        if (Values.Count > 0)
        {
            var vals = new JsonArray();
            foreach (var v in Values)
                vals.Add(new JsonObject
                {
                    ["row"] = v.Row, ["field"] = v.Field, ["kind"] = v.Kind,
                    ["was"] = v.Was, ["now"] = v.Now,
                });
            o["values"] = vals;
        }
        else o.Remove("values");

        if (Moves.Count > 0)
        {
            var mv = new JsonArray();
            foreach (var m in Moves)
                mv.Add(new JsonObject
                {
                    ["actor"] = m.Actor, ["path"] = m.Path,
                    ["was"] = m.Was, ["now"] = m.Now,
                });
            o["moves"] = mv;
        }
        else o.Remove("moves");

        if (Swaps.Count > 0)
        {
            var sw = new JsonArray();
            foreach (var m in Swaps)
                sw.Add(new JsonObject
                {
                    ["actor"] = m.Actor, ["path"] = m.Path,
                    ["was"] = m.Was, ["mesh"] = m.Mesh,
                });
            o["swaps"] = sw;
        }
        else o.Remove("swaps");

        if (Spawns.Count > 0)
        {
            var sp = new JsonArray();
            foreach (var m in Spawns)
                sp.Add(new JsonObject
                {
                    ["donor"] = m.Donor, ["x"] = m.X, ["y"] = m.Y, ["z"] = m.Z,
                    ["takeDonorSlot"] = m.TakeDonorSlot, ["mesh"] = m.Mesh,
                    ["id"] = m.Id,
                });
            o["spawns"] = sp;
        }
        else o.Remove("spawns");

        if (TagsProperty != DefaultTagsProperty) o["tagsProperty"] = TagsProperty;
        else o.Remove("tagsProperty");

        // Only the ones turned OFF -- a `true` says the target does what it already does, which is
        // what a missing entry says. Written every time would grow the file for nothing.
        var swatchesBack = HidesFromMenu.Where(kv => !kv.Value).ToList();
        if (swatchesBack.Count == 0) o.Remove("hidesFromMenu");
        else
        {
            var hid = new JsonObject();
            foreach (var (k, v) in swatchesBack) hid[k] = v;
            o["hidesFromMenu"] = hid;
        }

        // Same rule again: true is what the target already does, so it is a missing key.
        if (KeepsOwnSlotLock) o.Remove("keepsOwnSlotLock");
        else o["keepsOwnSlotLock"] = false;

        o.Remove("repointTag");
        if (Repoints.Count > 0)
        {
            var rps = new JsonArray();
            foreach (var r in Repoints)
                rps.Add(new JsonObject { ["from"] = r.From, ["to"] = r.To, ["expect"] = r.Expect });
            o["repointTags"] = rps;
        }
        else o.Remove("repointTags");
        return o;
    }

    private static string? Get(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is not null && v.GetValueKind() ==
            System.Text.Json.JsonValueKind.String
            ? v.GetValue<string>()
            : null;
}

/// <summary>
/// ONE OF THE GAME'S TABLES, LAID OVER RATHER THAN REWRITTEN.
///
/// Every table edit this tool has made until now rewrites the game's own asset, so two mods that
/// both touch one table cannot coexist and neither author can do anything about it. The game ships
/// a first-class answer -- `BitReactorGameFeatureAction_DataTableOverlay` -- and a mod using it
/// ships a table OF ITS OWN and names the one it covers.
///
/// The change is carried as row/field/was/now, the same four things
/// <see cref="ModOverride.NumberChange"/> carries and for the same reason: `was` is checked before
/// the write, so a walk that has drifted is a refusal rather than a corrupted asset.
/// </summary>
public sealed class ModTableOverlay
{
    /// <summary>The GAME table this covers, as a package path.</summary>
    public string Target { get; set; } = "";

    public string Row { get; set; } = "";
    public string Field { get; set; } = "";
    public double Was { get; set; }
    public double Now { get; set; }

    public override string ToString() => $"{Leafly(Target)} {Row}.{Field} {Was} -> {Now}";

    private static string Leafly(string path)
    {
        var at = path.LastIndexOf('/');
        return at < 0 ? path : path[(at + 1)..];
    }
}

/// <summary>
/// A set of slots stamped onto every part it targets at build time. A target is a game part's
/// package path (the build overrides it) or one of this mod's parts or copies by id. Unknown keys
/// are KEPT AND WRITTEN BACK, not dropped, so the build can refuse them by name -- a project from a
/// newer Studio must not build into a mod that quietly lacks what it asked for, and a save by THIS
/// Studio must not be the thing that erases what the newer one wrote. The same protection covers a
/// KNOWN key this Studio cannot READ (R18): a wrong JSON type -- `"targets": [123]`, say -- must not
/// crash the loader, which is the Studio's own open-project path too. It is treated exactly like an
/// unknown key: named in <see cref="UnknownKeys"/>, left at its model default, and its raw value
/// kept and written back unchanged.
/// </summary>
public sealed class ModSlotSet
{
    public string Id = "";
    public List<string> Targets { get; } = new();
    public List<ModSlot> Slots { get; } = new();
    public List<string> UnknownKeys { get; } = new();

    // The object exactly as read, so a key this Studio does not model -- or cannot read -- round
    // trips WITH ITS VALUE. See ModPart._extra, the same pattern already used for the top-level
    // part/spec/copy/override shapes. UnknownKeys stays a plain name list alongside it: the build
    // refuses by name, and widening that to carry values too would mean two representations
    // of the same fact drifting.
    private JsonObject? _extra;

    private static readonly HashSet<string> Known = ["id", "targets", "slots", "_why", "_comment"];

    public static ModSlotSet From_(JsonObject o)
    {
        var s = new ModSlotSet { _extra = o };
        foreach (var (k, _) in o) if (!Known.Contains(k)) s.UnknownKeys.Add(k);

        if (TryStr(o, "id", "", out var id)) s.Id = id; else s.UnknownKeys.Add("id");
        if (!TryStrList(o, "targets", s.Targets)) s.UnknownKeys.Add("targets");

        // "slots" is unreadable as a WHOLE, not element by element, when anything in it is not an
        // object -- a slot this Studio cannot even open as JSON is not one it can half-trust (R18).
        // ModSlot.From_ itself handles a bad FIELD inside an otherwise-good slot object.
        if (o["slots"] is { } slotsNode)
        {
            if (slotsNode is JsonArray sl && sl.All(x => x is JsonObject))
                foreach (var x in sl) s.Slots.Add(ModSlot.From_((JsonObject) x!));
            else
                s.UnknownKeys.Add("slots");
        }
        return s;
    }

    public JsonObject To()
    {
        // Start from what was read so unrecognised keys survive WITH THEIR VALUES, then overwrite
        // the known ones -- exactly ModPart.ToJson's rule, and for the same reason: reading a key
        // into the model and not rewriting it here is the one real hazard, so every field this
        // class understands is written or removed below, never left to the clone alone. A key this
        // Studio could not READ is the same hazard again: skip the write entirely so the raw,
        // unreadable value already sitting in the clone survives the save (R18).
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        if (!UnknownKeys.Contains("id")) o["id"] = Id;
        if (!UnknownKeys.Contains("targets")) o["targets"] = new JsonArray(Targets.Select(t => (JsonNode) t).ToArray());
        if (!UnknownKeys.Contains("slots")) o["slots"] = new JsonArray(Slots.Select(s => (JsonNode) s.To()).ToArray());
        return o;
    }

    // R18's three small readers, used by every From_ below: a field reads only when its JSON kind
    // actually matches, and reports `false` otherwise so the caller can flag the key as unreadable
    // rather than throw. Absent or JSON null is NOT a failure -- it is today's default -- so it
    // reports `true` with the default left in `value`/`into`. Duplicated per class (see ModPart.Get
    // and its six siblings elsewhere in this file for the same choice, made the same way).
    private static bool TryStr(JsonObject o, string key, string @default, out string value)
    {
        value = @default;
        var v = o[key];
        if (v is null) return true;
        if (v.GetValueKind() != System.Text.Json.JsonValueKind.String) return false;
        value = v.GetValue<string>();
        return true;
    }

    // EVERY element must itself be a JSON string, or the whole array is unreadable -- a
    // partially-good array is kept verbatim rather than half-read. A null element is skipped, same
    // as an absent field above: only a wrong-TYPED element condemns the array.
    private static bool TryStrList(JsonObject o, string key, List<string> into)
    {
        var v = o[key];
        if (v is null) return true;
        if (v is not JsonArray arr) return false;
        var read = new List<string>();
        foreach (var x in arr)
        {
            if (x is null) continue;
            if (x.GetValueKind() != System.Text.Json.JsonValueKind.String) return false;
            if (x.GetValue<string>() is { Length: > 0 } s) read.Add(s);
        }
        into.AddRange(read);
        return true;
    }
}

/// <summary>
/// One slot inside a <see cref="ModSlotSet"/> -- one CustomizationFragmentSlot's worth of JSON, in
/// the project's own vocabulary. Unknown keys, and any KNOWN key this Studio cannot READ (a wrong
/// JSON type -- R18), are KEPT AND WRITTEN BACK exactly as <see cref="ModSlotSet"/> keeps its own:
/// named in <see cref="UnknownKeys"/> so the build can refuse by name, left at their model
/// default, but never lost from the file itself.
/// </summary>
public sealed class ModSlot
{
    public string Id = "", Tag = "", Name = "", Icon = "", Default = "", Camera = "";
    public List<string> Fits { get; } = new();
    public bool CanBeEmpty = true;
    public ModSlotPaint? Paints;
    public List<string> UnknownKeys { get; } = new();

    private JsonObject? _extra;

    private static readonly HashSet<string> Known =
        ["id", "tag", "name", "icon", "fits", "default", "canBeEmpty", "camera", "paints", "_why", "_comment"];

    public static ModSlot From_(JsonObject o)
    {
        var s = new ModSlot { _extra = o };
        foreach (var (k, _) in o) if (!Known.Contains(k)) s.UnknownKeys.Add(k);

        if (TryStr(o, "id", "", out var id)) s.Id = id; else s.UnknownKeys.Add("id");
        if (TryStr(o, "tag", "", out var tag)) s.Tag = tag; else s.UnknownKeys.Add("tag");
        if (TryStr(o, "name", "", out var name)) s.Name = name; else s.UnknownKeys.Add("name");
        if (TryStr(o, "icon", "", out var icon)) s.Icon = icon; else s.UnknownKeys.Add("icon");
        if (TryStr(o, "default", "", out var def)) s.Default = def; else s.UnknownKeys.Add("default");
        if (TryStr(o, "camera", "", out var camera)) s.Camera = camera; else s.UnknownKeys.Add("camera");
        if (TryBool(o, "canBeEmpty", true, out var canBeEmpty)) s.CanBeEmpty = canBeEmpty; else s.UnknownKeys.Add("canBeEmpty");
        if (!TryStrList(o, "fits", s.Fits)) s.UnknownKeys.Add("fits");

        // A paints-level unknown is listed HERE, on the slot, as "paints.<key>" -- the build refuses by
        // walking each slot's own UnknownKeys, so a stray key (or a wrong-typed one, R18) nested
        // under paints must show up there too. Its VALUE is kept by ModSlotPaint itself (own
        // _extra), not here: "paints" is a known key on the SLOT, so this class's own clone already
        // carries whatever paints reads back as, once Paints.To() rebuilds it below. A "paints" that
        // is present but not an object at all is the SAME case as a wrong-typed field (R18): it is
        // unreadable in its own right, named unprefixed on this slot, and Paints stays unset.
        if (o["paints"] is { } paintsNode)
        {
            if (paintsNode is JsonObject p) s.Paints = ModSlotPaint.From_(p, s.UnknownKeys);
            else s.UnknownKeys.Add("paints");
        }
        return s;
    }

    public JsonObject To()
    {
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        if (!UnknownKeys.Contains("id")) o["id"] = Id;
        if (!UnknownKeys.Contains("tag")) o["tag"] = Tag;
        if (!UnknownKeys.Contains("name")) o["name"] = Name;
        if (!UnknownKeys.Contains("icon")) { if (Icon.Length > 0) o["icon"] = Icon; else o.Remove("icon"); }
        if (!UnknownKeys.Contains("fits")) { if (Fits.Count > 0) o["fits"] = new JsonArray(Fits.Select(x => (JsonNode) x).ToArray()); else o.Remove("fits"); }
        if (!UnknownKeys.Contains("default")) { if (Default.Length > 0) o["default"] = Default; else o.Remove("default"); }
        if (!UnknownKeys.Contains("canBeEmpty")) o["canBeEmpty"] = CanBeEmpty;
        if (!UnknownKeys.Contains("camera")) { if (Camera.Length > 0) o["camera"] = Camera; else o.Remove("camera"); }
        if (!UnknownKeys.Contains("paints")) { if (Paints is not null) o["paints"] = Paints.To(); else o.Remove("paints"); }
        return o;
    }

    public SlotAdd.Slot ToSlot() => new(Id, Tag, Name, Icon.Length > 0 ? Icon : null, Fits,
                                        Default.Length > 0 ? Default : null, CanBeEmpty,
                                        Camera.Length > 0 ? Camera : null, Paints?.ToPaint());

    private static bool TryStr(JsonObject o, string key, string @default, out string value)
    {
        value = @default;
        var v = o[key];
        if (v is null) return true;
        if (v.GetValueKind() != System.Text.Json.JsonValueKind.String) return false;
        value = v.GetValue<string>();
        return true;
    }

    private static bool TryBool(JsonObject o, string key, bool @default, out bool value)
    {
        value = @default;
        var v = o[key];
        if (v is null) return true;
        if (v.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) return false;
        value = v.GetValue<bool>();
        return true;
    }

    private static bool TryStrList(JsonObject o, string key, List<string> into)
    {
        var v = o[key];
        if (v is null) return true;
        if (v is not JsonArray arr) return false;
        var read = new List<string>();
        foreach (var x in arr)
        {
            if (x is null) continue;
            if (x.GetValueKind() != System.Text.Json.JsonValueKind.String) return false;
            if (x.GetValue<string>() is { Length: > 0 } s) read.Add(s);
        }
        into.AddRange(read);
        return true;
    }
}

/// <summary>
/// What a colour slot paints: <see cref="SlotAdd.Paint"/>'s own project-JSON shape, nested under a
/// <see cref="ModSlot"/>'s "paints" key. An unknown key here, or a known one this Studio cannot READ
/// (a wrong JSON type -- R18), is reported on the OWNING SLOT's <see cref="ModSlot.UnknownKeys"/> as
/// "paints.&lt;key&gt;" -- that is the list the build walks to refuse by name -- but the raw
/// VALUE is kept and written back HERE, in this object's own clone, because this is the class whose
/// <see cref="To"/> actually re-emits the "paints" JSON.
/// </summary>
public sealed class ModSlotPaint
{
    public string Slot = "", Channel = "Color 01";
    public List<string> MaterialSlots { get; } = new();
    public bool Small;

    // Its OWN _extra, kept separately from the owning ModSlot's: a key unknown (or unreadable)
    // inside "paints" is reported on the SLOT (as "paints.<key>", see ModSlot.From_) but its value
    // lives here, because this is the object whose To() actually writes the "paints" JSON back out.
    private JsonObject? _extra;

    // Which of THIS object's own fields could not be read, by their OWN (unprefixed) name -- used
    // only to guard To() below. The names the SLOT sees are prefixed ("paints.<key>"); these are
    // not, because they index this class's own JSON object, not the slot's.
    private readonly HashSet<string> _unreadable = new();

    private static readonly HashSet<string> Known = ["slot", "materialSlots", "channel", "small", "_why", "_comment"];

    public static ModSlotPaint From_(JsonObject o, List<string> unknown)
    {
        var p = new ModSlotPaint { _extra = o };
        foreach (var (k, _) in o) if (!Known.Contains(k)) unknown.Add("paints." + k);

        if (TryStr(o, "slot", "", out var slot)) p.Slot = slot;
        else { p._unreadable.Add("slot"); unknown.Add("paints.slot"); }

        if (TryStr(o, "channel", "Color 01", out var channel)) p.Channel = channel;
        else { p._unreadable.Add("channel"); unknown.Add("paints.channel"); }

        if (TryBool(o, "small", false, out var small)) p.Small = small;
        else { p._unreadable.Add("small"); unknown.Add("paints.small"); }

        if (!TryStrList(o, "materialSlots", p.MaterialSlots))
        { p._unreadable.Add("materialSlots"); unknown.Add("paints.materialSlots"); }

        return p;
    }

    public JsonObject To()
    {
        var o = _extra is null ? new JsonObject() : (JsonObject) _extra.DeepClone();
        if (!_unreadable.Contains("slot")) o["slot"] = Slot;
        if (!_unreadable.Contains("materialSlots")) o["materialSlots"] = new JsonArray(MaterialSlots.Select(x => (JsonNode) x).ToArray());
        if (!_unreadable.Contains("channel")) o["channel"] = Channel;
        if (!_unreadable.Contains("small")) o["small"] = Small;
        return o;
    }

    public SlotAdd.Paint ToPaint() => new(Slot, MaterialSlots, Channel, Small);

    private static bool TryStr(JsonObject o, string key, string @default, out string value)
    {
        value = @default;
        var v = o[key];
        if (v is null) return true;
        if (v.GetValueKind() != System.Text.Json.JsonValueKind.String) return false;
        value = v.GetValue<string>();
        return true;
    }

    private static bool TryBool(JsonObject o, string key, bool @default, out bool value)
    {
        value = @default;
        var v = o[key];
        if (v is null) return true;
        if (v.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) return false;
        value = v.GetValue<bool>();
        return true;
    }

    private static bool TryStrList(JsonObject o, string key, List<string> into)
    {
        var v = o[key];
        if (v is null) return true;
        if (v is not JsonArray arr) return false;
        var read = new List<string>();
        foreach (var x in arr)
        {
            if (x is null) continue;
            if (x.GetValueKind() != System.Text.Json.JsonValueKind.String) return false;
            if (x.GetValue<string>() is { Length: > 0 } s) read.Add(s);
        }
        into.AddRange(read);
        return true;
    }
}

using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// Build a complete, installable mod with no Unreal at all.
///
/// Every piece of this was proved separately first: <see cref="ZenPackage"/> authors the package,
/// <see cref="PackageId"/> gives it the id a container files it under, <see cref="ContainerHeader"/>
/// lists it in the package store, <see cref="IoStoreWriter"/> writes the container,
/// <see cref="RegistryAssets"/> puts it in the asset registry and <see cref="PakWriter"/> ships
/// that registry. This is the assembly, and it is the step that removes the cook.
///
/// A part is authored by RENAMING one the game already ships. That is not a shortcut around
/// understanding the format -- a part definition's value is its gameplay tags, its override slots
/// and its class, all of which have to be exactly what the game expects, and the surest way to get
/// them right is to start from a part that already is right and change only the three things a mod
/// actually varies: the name, the mesh, and the icon.
/// </summary>
public static class ModBuild
{
    /// <summary>
    /// Where a mod's packages live by default, matching the folder the cooked mod used. A mod can
    /// choose another with `folder` -- see <see cref="ModProject.Folder"/> for why it is not free.
    /// </summary>
    public const string ModFolder = "Content/Game/" + ModProject.DefaultFolder + "/";

    /// <summary>The container folder for a mod's chosen folder.</summary>
    public static string FolderOf(ModProject mod) =>
        "Content/Game/" + mod.Folder.Replace('\\', '/').Trim('/') + "/";

    /// <summary>The project whose content mounts as /Game/, and the pak's mount point.</summary>
    public const string ProjectName = "SWZeroCompany";
    public const string MountPoint = "../../../" + ProjectName + "/";

    /// <summary>
    /// The same folder as a PACKAGE name. Parts are indexed by package name, not by container
    /// path, so filtering them with <see cref="ModFolder"/> matches nothing at all -- which is how
    /// a mod came to be built by templating off its own previously deployed parts.
    /// </summary>
    public static readonly string ModPackageFolder = PackageNameOf("x")[..^1];

    /// <summary>The package name a mod part of this id gets, in a given folder.</summary>
    public static string PackageNameOf(string id, string folder = ModFolder) =>
        PackageId.PackageNameFor($"{ProjectName}/{folder}{id}.uasset", ProjectName)!;

    /// <summary>
    /// The container path for a package name -- the inverse of PackageId.PackageNameFor.
    ///
    /// PartsIndex reports a part by its PACKAGE NAME, because that is what the asset registry
    /// stores. Feeding one back into PackageNameFor returns null, since a package name has neither
    /// a /Content/ nor an extension to find -- and null coalesced to "" looked exactly like a
    /// template with no registry entry.
    /// </summary>
    public static string ContainerPathFor(string packageName)
    {
        var trimmed = packageName.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var root = slash < 0 ? trimmed : trimmed[..slash];
        var rest = slash < 0 ? "" : trimmed[(slash + 1)..];
        // /Game/ is the project's own content and is the one root not named after its folder.
        return $"{(root.Equals("Game", StringComparison.OrdinalIgnoreCase) ? ProjectName : root)}/Content/{rest}.uasset";
    }

    /// <summary>
    /// One package the build produced. <paramref name="IsTexture"/> separates an authored icon
    /// from a part: they go into the container the same way, but a texture is not a wearable thing
    /// and checking it as one reports a failure that is not there.
    /// </summary>
    /// <summary>
    /// <paramref name="Redirected"/> is how many references this asset was pointed AT THE MOD's own
    /// assets, and it exists so the verifier can check the redirect rather than trust it. A copy
    /// that keeps the originals' names is a copy that keeps using the originals -- which builds,
    /// installs, verifies on every other question, and shows the game's own artwork.
    /// </summary>
    public sealed record BuiltPart(string Id, string PackageName, ulong ChunkId,
                                   string ContainerPath, int Bytes, string TemplatePath,
                                   bool IsTexture = false, bool IsCopy = false,
                                   int Redirected = 0, bool IsOverride = false,
                                   string? IconPackage = null,
                                   // What the author asked for, when the author knows. A
                                   // specialization is written as a diff against its template, so
                                   // it can say exactly -- and --diff then checks the result
                                   // against the intent instead of against a reconstruction of it.
                                   IReadOnlyDictionary<string, string>? Substitutions = null,
                                   // An override of a file on EarlyLoad's list: it leaves the plugin
                                   // container for the second one, installed into ~mods.
                                   bool IsEarly = false,
                                   // What the build did to each of the template's tint-mask
                                   // fragments, in the template's own fragment order, when it
                                   // substituted them PIECE BY PIECE. The verifier reads the
                                   // finished part back against exactly this and re-derives
                                   // nothing, so it cannot disagree with the build about which
                                   // piece a fragment belongs to.
                                   IReadOnlyList<TintExpectation>? TintExpected = null);

    /// <summary>
    /// One tint-mask fragment of a part built piece by piece: the piece the build took it to be
    /// for ("" when it could not tell), the mask it should now name, and whether the build
    /// substituted it (false when it was kept as the template has it).
    /// </summary>
    public sealed record TintExpectation(string Piece, string Mask, bool Substituted);

    /// <summary>
    /// The game's OWN AssetRegistry.bin, read past any deployed mod.
    ///
    /// A mod pak ships an AssetRegistry.bin at the same path as the game's, and being a `_P` patch
    /// it wins the lookup. So asking the provider for "the" registry while a previous build is
    /// installed returns one that ALREADY contains this mod, and appending to it stacks a second
    /// copy of every entry -- a fault that first shows up as duplicated menu items.
    ///
    /// Containers are searched directly instead, skipping any whose name marks it as a patch.
    /// Returns null when only patched copies exist, because guessing here is how the stacking
    /// happens.
    /// </summary>
    public static byte[]? PristineRegistry(IFileProvider provider, out string? from)
    {
        from = null;
        if (provider is not CUE4Parse.FileProvider.Vfs.IVfsFileProvider vfs) return null;

        foreach (var reader in vfs.MountedVfs.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            // `_P` is UE's patch marker and is what gives a mod pak its priority. A container
            // carrying it is by definition not the pristine game.
            var name = Path.GetFileNameWithoutExtension(reader.Name);
            if (name.EndsWith("_P", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var (path, file) in reader.Files)
            {
                if (!path.EndsWith("/AssetRegistry.bin", StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals("AssetRegistry.bin", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var bytes = file.Read();
                    from = $"{reader.Name} :: {path}";
                    return bytes;
                }
                catch { /* try the next container rather than fail the build */ }
            }
        }
        return null;
    }

    /// <summary>
    /// Every package's store entry, from every mounted container: what each package IMPORTS.
    ///
    /// This is not optional detail. A package's import map refers to other packages POSITIONALLY,
    /// through this list. An authored package that declares no imports while its exports reference
    /// a class in another package makes the loader index into an empty array, and the result is a
    /// crash inside FAsyncPackage2::ProcessExportDependencies -- reading 0xffffffffffffffff, with
    /// no error message and nothing in a log.
    ///
    /// The cooked mod's own parts genuinely import nothing, which is what made "write an empty
    /// entry" look correct. Those parts are plain CustomizationPartDefinition assets. The game's
    /// outfits are BLUEPRINT subclasses, so their class is a real package import -- and a part
    /// authored from one inherits exactly that dependency.
    /// </summary>
    public static Dictionary<ulong, ContainerHeader.StoreEntry> StoreEntries(IFileProvider provider)
    {
        var all = new Dictionary<ulong, ContainerHeader.StoreEntry>();
        if (provider is not CUE4Parse.FileProvider.Vfs.IVfsFileProvider vfs) return all;

        foreach (var reader in vfs.MountedVfs)
        {
            if (reader is not CUE4Parse.UE4.IO.IoStoreReader io) continue;
            try
            {
                var raw = AssetLoader.Read(() => io.Read(new CUE4Parse.UE4.IO.Objects.FIoChunkId(
                    io.TocResource.Header.ContainerId.Id, 0, ContainerHeader.ChunkType)));
                var header = ContainerHeader.Read(raw);
                for (var i = 0; i < header.PackageIds.Count && i < header.Store.Count; i++)
                    all[header.PackageIds[i]] = header.Store[i];
            }
            catch { /* a container without a readable header simply contributes nothing */ }
        }
        return all;
    }

    /// <summary>
    /// The two loose files a plugin build writes beside its containers. Their presence on the
    /// result is what tells the installer WHICH LAYOUT this build is -- see ModInstall.Install for
    /// why that has to travel with the build rather than be chosen at install time.
    /// </summary>
    /// <summary><paramref name="Extra"/> is (path inside the mod folder, path on disk now).</summary>
    public sealed record PluginFiles(string Name, string Uplugin, string Registry,
                                     IReadOnlyList<(string Relative, string Absolute)>? Extra = null);

    /// <summary>
    /// The second container a build writes when the mod changes a file on <see cref="EarlyLoad"/>'s list:
    /// installed into Content/Paks/~mods, because the game reads those files before it mounts Mods/.
    /// </summary>
    public sealed record EarlyFiles(string Name, string Pak, string Ucas, string Utoc,
                                    IReadOnlyList<string> Packages);

    /// <param name="Clashes">
    /// NOTES THAT MEAN THE MOD IS NOT THE MOD THE AUTHOR DESIGNED, kept apart from the rest.
    ///
    /// Everything in Warnings is a note, and most notes are informational -- a texture was
    /// resized, a piece wears the body's paint. Two assets built to one package name is not that
    /// kind of note: something the author asked for is missing from the result, and the mod ships
    /// looking complete.
    ///
    /// It was in Warnings, and it fired, and a mod with two helmets in it shipped anyway with one
    /// mesh. The panel renders "6 note(s) -- in the log below" in grey, which is the right
    /// treatment for a resize and the wrong one for this. Separated so the panel can say the
    /// second out loud without shouting about the first.
    /// </param>
    public sealed record Result(string Pak, string Ucas, string Utoc,
                                List<BuiltPart> Parts, List<string> Skipped, List<string> Warnings,
                                PluginFiles? Plugin = null, List<string>? Clashes = null,
                                // The ids behind the loose-copies note, as a LIST rather than only
                                // inside the sentence. The note has always named them; a comma
                                // separated run inside a paragraph is something a person has to
                                // retype one at a time, and the author did, nine of them. Given
                                // separately the panel can offer to remove them.
                                List<string>? LooseCopies = null,
                                // OVERRIDES THIS BUILD SYNTHESISED, which are not in the project.
                                // A mission's table edit is one (see MissionBuild), and the
                                // verifier looks an override up in mod.Overrides to tell a
                                // REQUESTED redirect from an accident -- so without these it found
                                // nothing, took the "asks for no redirect" branch, and called a
                                // correct eleven-row repoint "corruption about to replace a real
                                // game asset". Fourth time that rule has outlived its premise.
                                List<ModOverride>? SynthesisedOverrides = null,
                                // AND THE COPIES, for the same reason one function along:
                                // ModVerify resolves a redirect's target through HashSource to
                                // learn that a copy's hashes are its TEMPLATE's. Without these it
                                // resolved a mission to itself, read an unmounted package, and
                                // reported eleven correct hashes as "something the target does
                                // not publish".
                                List<ModCopy>? SynthesisedCopies = null,
                                // WHERE EACH NEW ACTOR LANDED, because only the build knows.
                                //
                                // A spawn is addressed as "#N", and N is decided while the export
                                // map is being grown -- it is not in the project and nothing
                                // downstream can work it out. Without it the read-back had no way
                                // to look a spawned actor up, so the newest and least proven edit
                                // this builder makes was the one edit it never checked.
                                List<Spawned>? Spawned = null,
                                // WHERE EACH SLOT SET ACTUALLY LANDED (I-3), the same reasoning as
                                // Spawned one field up: only the build knows the export a slot was
                                // really stamped onto -- an override's target can differ in case
                                // from the project override it merged into, and a part/copy id can
                                // collide with a Game Feature data asset's Id or another BuiltPart's
                                // -- so ModVerify must read the name the build actually used rather
                                // than deciding it a second time from BuiltPart.Id (R7, extended).
                                List<SlotStamp>? SlotStamps = null,
                                // THE SECOND CONTAINER, when an override of a file the game loads
                                // before it mounts Mods/ was split out -- see EarlyLoad. Null for
                                // every build that has none, which is every build until now.
                                EarlyFiles? Early = null);

    /// <summary>One actor this build added, and where it should now be.</summary>
    public sealed record Spawned(string Target, string Address, double X, double Y, double Z,
                                 string Asset);

    /// <summary>
    /// Where this build actually stamped one slot set: <paramref name="Target"/> is the landing's
    /// own target string (a game path, or one of this mod's part/copy ids -- exactly what
    /// <see cref="SlotSets.Resolve"/> produced, so a "was this even attempted" check needs no
    /// re-derivation), <paramref name="PackageName"/> is the package the slots actually live in, and
    /// <paramref name="PartExport"/> is the export they were stamped onto AS FOUND when they were
    /// written. One stamp per landing that succeeded; a landing that refused leaves none, which is
    /// how ModVerify knows to fail it as "never built" rather than passing by omission.
    /// </summary>
    public sealed record SlotStamp(string Target, string PackageName, string PartExport,
                                   IReadOnlyList<SlotAdd.Slot> Slots);

    /// <summary>
    /// Pick the part to start from. The template decides the override slots, the gameplay tags
    /// and the class, so it has to be the same KIND of thing as the part being authored -- and it
    /// needs a mesh and an icon of its own, because a reference that is not there cannot be
    /// substituted.
    /// </summary>
    public static PartsIndex.Part? TemplateFor(IFileProvider provider, List<PartsIndex.Part> parts,
                                               ModPart part, out string? compromise,
                                               out string? conflictNote, Action<string>? log = null)
    {
        compromise = null;
        conflictNote = null;
        var slotTag = ModProject.ToSlotTag(part.Slot);

        // No filter on the CLASS NAME, and none on the GATE. PartsIndex already selects on
        // PrimaryAssetType, and the game's outfits are Blueprint subclasses so a class-name filter
        // discards every one of them. The gate is stripped by PartTagsFor, and requiring "open"
        // here excluded Captain Rex's own parts -- which are dev-gated precisely because they are
        // the thing a mod wants to unlock.
        var notMod = parts.Count(p => !p.Path.StartsWith(ModPackageFolder, StringComparison.OrdinalIgnoreCase));
        var candidates = parts
            .Where(p => !p.Path.StartsWith(ModPackageFolder, StringComparison.OrdinalIgnoreCase))
            .Where(p => slotTag is null || p.Slots.Any(s =>
                        s.Contains(slotTag, StringComparison.OrdinalIgnoreCase)))
            // A character restriction IS a tag on the part, not a gate, and would offer the mod
            // part to that character only.
            .Where(p => p.Characters.Count == 0)
            // GATES: "dev" is fine, an EDITION or STORY unlock is not.
            //
            // Stripping the registry's Accepts.* tags removes AuthoredOnly -- proved by Captain
            // Rex's own dev-gated parts appearing and working. It does NOT remove a deluxe or
            // preorder entitlement: that lives in the part's own data, and a helmet and a pair of
            // boots built from edition-gated templates both came back wearing a DLC flag.
            //
            // Dropping the gate check entirely was an over-generalisation from the dev case.
            .Where(p => p.Gate is "open" or "dev")
            .OrderBy(p => p.Path, StringComparer.Ordinal)
            .ToList();

        var wantIcon = part.Icon.Length > 0;
        var wantAnim = part.AnimClass.Length > 0;
        var wantedMesh = PackageOf(part.Mesh);

        // Read every candidate ONCE. The scoring below needs to know something no single candidate
        // can tell it -- which Blueprint base class the mod's own mesh natively belongs to -- so a
        // one-pass "first acceptable" search cannot work.
        var scanned = new List<(PartsIndex.Part Part, PartDetails.Detail Detail)>();
        int noRead = 0, noMesh = 0, headMismatch = 0;
        foreach (var c in candidates)
        {
            var d = PartDetails.Read(provider, c.Path);
            if (d.Error is not null) { noRead++; continue; }
            if (string.IsNullOrEmpty(d.MeshPath) && !ModProject.SlotHasNoMesh(part.Slot))
            { noMesh++; continue; }
            if (part.EffectiveHidesHead != d.HidesHead) { headMismatch++; continue; }
            scanned.Add((c, d));
        }

        // THE BLUEPRINT BASE CLASS IS WHAT CARRIES THE MATERIAL BEHAVIOUR.
        //
        // Captain Rex's mesh rendered in Rex's own part looks right; the same mesh dropped into a
        // template of a different base came out grey, while one that happened to share his base
        // kept the blue. All six of his parts are BP_CPD_H_Outfit_Slg_Base_C, and 80 torso
        // templates share it -- so matching the base is nearly free and is what lets the build
        // keep the colours AND still pick a template that has an icon and a name to redirect.
        //
        // The class is learned from whichever part NATIVELY uses the mod's mesh, rather than
        // configured, because that part is the game's own statement of how the mesh should behave.
        var nativeClass = wantedMesh is null ? null : scanned
            .Where(s => s.Detail.Meshes.Any(m => string.Equals(PackageOf(m.MeshPath), wantedMesh,
                                                               StringComparison.OrdinalIgnoreCase)))
            .Select(s => s.Part.Class)
            .FirstOrDefault();
        if (nativeClass is not null)
            log?.Invoke($"      mesh belongs to {nativeClass}");

        // NAMED OUTRIGHT: the ranking is replaced, the DIAGNOSIS is not.
        //
        // See ModPart.Template for why the escape hatch is a name. What matters here is that it
        // narrows the candidates to one rather than returning that one -- an earlier version
        // returned early, and every note below silently stopped being computed. A named template
        // is the case where those notes matter MOST: the person overrode the ranking, so nothing
        // else is left to notice that their choice hides the wrong thing or belongs to the wrong
        // base class.
        //
        // None of the filters that built `candidates` apply -- each encodes "this is probably not
        // what you meant", and the author has just said what they meant. `nativeClass` is read
        // from the full scan above, because it is a fact about the MESH and is what the base-class
        // note depends on.
        var named = false;
        if (!string.IsNullOrWhiteSpace(part.Template))
        {
            var want = part.Template.Trim();
            static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
            var matches = parts
                // The one exclusion kept: templating off a part this tool already deployed is how
                // a mod stacks a second copy of itself, whoever asked for it.
                .Where(p => !p.Path.StartsWith(ModPackageFolder, StringComparison.OrdinalIgnoreCase))
                .Where(p => p.Path.Equals(want, StringComparison.OrdinalIgnoreCase) ||
                            Leaf(p.Path).Equals(want, StringComparison.OrdinalIgnoreCase) ||
                            p.Path.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToList();

            // Never a silent fall back to the ranking. A template that was asked for and quietly
            // not used builds, installs, and is wrong with nothing anywhere reporting it.
            if (matches.Count == 0)
            {
                log?.Invoke($"      template '{want}' matches no part the game ships");
                return null;
            }
            var chosen = matches[0];
            var chosenDetail = PartDetails.Read(provider, chosen.Path);
            if (chosenDetail.Error is not null)
            { log?.Invoke($"      template '{want}': {chosenDetail.Error}"); return null; }
            // A mesh-less template is a fault for an outfit part and NORMAL for a specialisation:
            // the parts behind the SPECIALIZATION, TALENT and WEAPON CLASS tabs carry abilities and
            // tags, never geometry. Refusing them here is what made "author a new specialisation"
            // look impossible when the only obstacle was a check written for a different slot.
            if (string.IsNullOrEmpty(chosenDetail.MeshPath) && !ModProject.SlotHasNoMesh(part.Slot))
            { log?.Invoke($"      template '{want}' has no mesh to substitute"); return null; }

            if (matches.Count > 1)
                compromise = $"'{want}' names {matches.Count} parts; using {Leaf(chosen.Path)}";
            log?.Invoke($"      template named outright: {chosen.Path}");
            scanned = new List<(PartsIndex.Part Part, PartDetails.Detail Detail)>
                { (chosen, chosenDetail) };
            named = true;
        }

        PartsIndex.Part? best = null;
        var bestScore = (FaceShows: int.MaxValue, MixedMeshes: int.MaxValue, Class: int.MaxValue,
                         Conflicts: int.MaxValue, Icon: int.MaxValue, Name: int.MaxValue,
                         Unknowns: int.MaxValue, Anim: int.MaxValue, Coverage: int.MaxValue);
        var bestOverrides = 0;
        var bestTurnedOff = 0;

        foreach (var (c, d) in scanned)
        {
            var wrongClass = nativeClass is not null &&
                             !string.Equals(c.Class, nativeClass, StringComparison.Ordinal) ? 1 : 0;
            // A helmet has to hide the FACE, not merely the hair. Detail.HidesHead is true for
            // either, so a template that hides only hair passes it and then renders the character's
            // face through the visor.
            var faceShows = part.EffectiveHidesHead && !d.HidesFace ? 1 : 0;

            // Mesh options should be the same KIND of thing as each other. One template mixed a
            // _PACK mesh in among its _TORS options; substituting the mod's torso into all of them
            // produced a part that never appeared. Consistency is checked between the options
            // rather than against the slot, because the game itself is not consistent there -- the
            // arms slot is legitimately served by templates whose meshes are named _TORS.
            var suffixes = d.Meshes
                .Select(m => PackageOf(m.MeshPath) is { } q ? q[(q.LastIndexOf('_') + 1)..] : "")
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var mixedMeshes = suffixes > 1 ? 1 : 0;

            var conflicts = d.OverrideSlots.Count(IsOutfitMeshSlot);

            // Prefer a template this builder UNDERSTANDS. Every fault so far came from inheriting
            // something nobody had reasoned about, so the count of fragments the audit cannot
            // account for is itself a ranking signal -- not merely something to report after the
            // choice is made. This subsumes the material-colour check, which is one such fragment.
            var unknowns = PartAudit.Of(d).Count(
                i => i.What is PartAudit.Disposition.Unaccounted or PartAudit.Disposition.Suspect);
            var noIcon = wantIcon && string.IsNullOrEmpty(d.IconPath) ? 1 : 0;
            // A name, like every other reference, can only be REDIRECTED. A template with no
            // DisplayName yields a part that shows its asset id.
            var noName = string.IsNullOrEmpty(d.DisplayName) ? 1 : 0;
            var noAnim = wantAnim && string.IsNullOrEmpty(d.AnimClassPath) ? 1 : 0;
            var coverage = d.OverrideSlots.Count - conflicts;

            // Order matters, and it is ordered by what has actually been SEEN to go wrong, worst
            // first: a face rendering through a helmet, a part that never appears, wrong materials,
            // a piece that hides another piece, then the two things a player reads off the menu --
            // a thumbnail and a name -- and only then the count of fragments nobody has reasoned
            // about. Ranking unknowns above the icon and the name cost both of them on the helmet,
            // in exchange for avoiding a fragment whose effect under a matching base class has
            // never been observed.
            var score = (faceShows, mixedMeshes, wrongClass, conflicts, noIcon, noName, unknowns,
                         noAnim, part.EffectiveHidesHead ? -coverage : coverage);
            if (score.CompareTo(bestScore) >= 0) continue;
            best = c;
            bestScore = score;
            bestOverrides = d.OverrideSlots.Count;
            // Of those conflicts, how many the author has turned off. Kept here because `d` is
            // the winner's detail and re-reading it later would be a second package load for a
            // note.
            bestTurnedOff = d.OverrideSlots.Count(
                x => IsOutfitMeshSlot(x) && part.TakesOver.TryGetValue(x, out var keep) && !keep);
        }

        if (best is null)
        {
            log?.Invoke($"      no template: {parts.Count} parts, {notMod} not already a mod, " +
                        $"{candidates.Count} in slot {part.Slot}; rejected {noRead} unreadable, " +
                        $"{noMesh} no mesh, {headMismatch} hides-head mismatch");
            return null;
        }

        log?.Invoke($"      template overrides {bestOverrides} slot(s): " +
                    $"{bestScore.Conflicts} outfit conflict(s)" +
                    (bestScore.Class == 0 ? "; base class matches the mesh" : ""));

        var notes = new List<string>();
        // Worded for whichever way the template was chosen. "No template for this slot does X"
        // is a statement about the whole candidate set and is simply false when the set is one
        // part the author named -- and it reads as "nothing could have worked", which would send
        // someone looking in the wrong place.
        // States the fact and the repair, not a consequence that does not follow.
        //
        // It used to say the head would render through the helmet. The builder WRITES the face
        // slot itself when hidesHead is set -- see the splice in Author, whose own comment is that
        // exactly one part in the game carries that tag, so template selection can never deliver
        // it. A warning about damage the next step undoes is a warning that teaches people to
        // ignore warnings; it surfaced on every clone-helmet template, which are the right
        // templates for a clone helmet. If the splice does fail it says so on its own line.
        // SAYS WHICH OF THE TWO THINGS WILL HAPPEN, because the splice does NOT run for a named
        // template -- see its gate in Author. This note claimed "the build adds it" either way,
        // which is a promise it does not keep on the path an author is most likely to take.
        if (bestScore.FaceShows == 1)
            notes.Add(named
                ? "the template you named does not carry the face-hiding slot, and a NAMED " +
                  "template is duplicated faithfully -- so the build does NOT add it. If this " +
                  "part shows the character's face, that is why: clear the template box to let " +
                  "the builder splice the tag in, or name one that hides the face"
                : "no template for this slot carries the face-hiding slot, so the build adds it " +
                  "-- if the part still shows the character's face, that splice is where to look");
        if (bestScore.MixedMeshes == 1)
            notes.Add("its template mixes different kinds of mesh, which has produced a part that " +
                      "never appeared in the menu");
        if (bestScore.Class != 0 && nativeClass is not null)
            notes.Add(named
                ? $"the template you named is {best.Class}, not {nativeClass} which its mesh " +
                  "belongs to, so the part's materials may not render as intended"
                : $"no {part.Slot} template shares {nativeClass}, the base its mesh belongs " +
                  "to, so the part's materials may not render as intended");
        // Counted against what the part ACTUALLY ships, not against the template. Turning a
        // takeover off and still being told the part hides whatever is in that slot is a note
        // arguing with the edit made three hundred lines below it.
        var stillTakes = bestScore.Conflicts - bestTurnedOff;
        if (stillTakes > 0)
            notes.Add($"its template takes over {stillTakes} other outfit slot(s), so " +
                      "wearing it will hide whatever is in them");
        if (bestScore.Unknowns > 0)
            // NOT "cannot account for". The count is Unaccounted PLUS Suspect, and a suspect
            // fragment is one this builder understands perfectly well and reports because it
            // changes how the part behaves. Saying otherwise reads as "the tool is lost" about
            // fragments it deliberately carries.
            notes.Add($"its template carries {bestScore.Unknowns} fragment(s) worth knowing " +
                      "about -- listed below");
        // NO LONGER "an icon cannot be added". A template that names its picture by BANK TAG --
        // every weapon type part does -- gets one written in as a soft brush over whatever that
        // tag draws, the way the game's own DC-17m part carries its picture. The note stays,
        // because a part with no icon AND no image of the author's still has no thumbnail, and it
        // now says which case this is.
        if (bestScore.Icon == 1)
            notes.Add("its template names no icon of its own -- give this part a picture and one " +
                      "is written in; without one it has no thumbnail");
        // No longer a compromise: an empty DisplayName is located by walking the export rather
        // than by searching for text that is not there, and written into. Kept as a note because
        // it is still worth knowing which templates have no name of their own -- but it no longer
        // claims the part will be unnamed, because it will not be.
        if (bestScore.Name == 1)
            notes.Add("its template carries no display name of its own, so this part's name is " +
                      "written into an empty one");
        if (bestScore.Anim == 1)
            compromise = named
                ? $"the template you named carries no anim class, so the part is built without " +
                  $"{part.AnimClass}"
                : $"no {part.Slot} template carries an anim class, so the part is built " +
                  $"without {part.AnimClass}";

        conflictNote = notes.Count > 0 ? string.Join("; ", notes) : null;
        return best;
    }

    /// <summary>
    /// The registry tags for an authored part: the template's, with the two that must not be
    /// inherited put right.
    ///
    /// PRIMARY ASSET NAME. The game finds a part by FPrimaryAssetId, which is the type plus this
    /// tag -- not by package path. Copied unchanged it makes the mod claim to BE the template, two
    /// assets answer to one id, and one of them silently loses. Every part built before this was
    /// registered under its template's name and none of them appeared.
    ///
    /// ACCEPTS TAGS. `br.Customization.Accepts.*` restricts a piece to one outfit set, so a helmet
    /// templated off an Mdo outfit is offered only alongside Mdo clothing. The cooked mod that
    /// works carries exactly two tags -- the rig and the slot -- and nothing else.
    /// </summary>
    public static List<(string, string)> PartTagsFor(
        IDictionary<CUE4Parse.UE4.Objects.UObject.FName, string>? templateTags, string assetName,
        IReadOnlyCollection<string>? remove = null, string? onlyFor = null,
        IReadOnlyDictionary<string, string>? rename = null, IReadOnlyCollection<string>? add = null)
    {
        var tags = new List<(string, string)>();
        if (templateTags is null) return tags;

        foreach (var kv in templateTags)
        {
            var key = kv.Key.Text;
            var value = kv.Value ?? "";

            if (key == "PrimaryAssetName") value = assetName;
            else if (key == "CustomizationPartTags")
            {
                // The part's own name substitutions apply to the REGISTRY's copy of its tags as
                // well as to the package. The two are separate records of the same thing -- the
                // package says what a part IS and the registry is what the menu filters on -- so a
                // tag renamed in one and not the other leaves a part that looks like two different
                // things depending on who is asking.
                if (rename is not null)
                    foreach (var (from, to) in rename) value = value.Replace(from, to, StringComparison.Ordinal);

                var kept = PartsIndex.Describe("", assetName, null, value).Tags
                    .Where(x => !x.StartsWith("br.Customization.Accepts.", StringComparison.Ordinal))
                    // The part's own removals apply here too. The package says what a part IS and
                    // the REGISTRY is what the menu filters on -- dropping a character gate from
                    // one and not the other leaves the copy still restricted to its hero.
                    .Where(x => remove is null || !remove.Contains(x, StringComparer.Ordinal));

                // "Only for X" means exactly X: any name tag the template brought along goes,
                // then the wanted one is added. Keeping both would offer the part to two
                // characters, which is not what anybody means by choosing one.
                if (!string.IsNullOrWhiteSpace(onlyFor))
                    kept = kept.Where(x => !x.StartsWith(PartsIndex.NamePrefix, StringComparison.Ordinal))
                               .Append(PartsIndex.NamePrefix + onlyFor);

                // The author's ADDED tags go in after the strip. The strip exists to drop the
                // template's unlock gates; it must not drop what the author asked for -- the menu
                // filters on this registry value, so a tag missing here is a part no slot offers.
                //
                // SNAPSHOT FIRST. `kept.Concat(add.Where(t => !kept.Contains(t, ...)))` closes over
                // `kept` by name, and assigning that expression back to `kept` makes the lambda ask
                // the sequence it is still being built from whether it already has each tag -- a
                // self-reference that overflowed the stack the first time a part actually reached
                // this line, not the harmless inefficiency it reads like.
                if (add is not null)
                {
                    var beforeAdd = kept;
                    kept = beforeAdd.Concat(add.Where(t => !beforeAdd.Contains(t, StringComparer.Ordinal)));
                }

                value = TagContainer(kept);
            }

            tags.Add((key, value));
        }
        return tags;
    }

    /// <summary>
    /// The registry tags for an OVERRIDE: the game's own record, with the same name substitution
    /// the package got.
    ///
    /// This exists because the registry keeps its OWN copy of a part's tags, written by
    /// GetAssetRegistryTags when the game was cooked, and editing the package does not touch it.
    /// Measured, not assumed: the pristine registry lists CPD_TacticalSpec_Padawan with
    /// CustomizationPartTags containing Tel-ReaVokoss, and a package override that removed that tag
    /// changed the part's DISPLAY NAME in game while leaving its availability exactly as it was --
    /// so the menu reads the name from the loaded package and the tags from the registry.
    ///
    /// Unlike <see cref="PartTagsFor"/> this renames NOTHING and strips NOTHING. An override is
    /// the same asset under the same name; the only thing that may differ is the substitution the
    /// author asked for.
    /// </summary>
    public static List<(string, string)> OverrideTagsFor(
        IDictionary<CUE4Parse.UE4.Objects.UObject.FName, string>? templateTags,
        IReadOnlyList<ModOverride.Repoint> repoints, IReadOnlyCollection<string>? remove = null,
        IReadOnlyCollection<string>? add = null)
    {
        var tags = new List<(string, string)>();
        if (templateTags is null) return tags;

        foreach (var kv in templateTags)
        {
            var value = kv.Value ?? "";
            // A blanket replace over the value is safe because a gameplay tag name is distinctive
            // enough not to occur inside a class path or an asset name, and it keeps this faithful
            // to "whatever the game said, with one name swapped".
            if (repoints.Count > 0)
            {
                foreach (var r in repoints) value = value.Replace(r.From, r.To, StringComparison.Ordinal);
                // De-duplicated, because the game's own projection is. Re-pointing a tag onto one
                // the part already carries leaves the same tag twice, and while a container is
                // only a list, an entry shaped unlike every other entry in the registry is a
                // difference nobody chose. Rebuild it the way TagContainer writes a fresh one.
                if (value.StartsWith("(GameplayTags=", StringComparison.Ordinal))
                    value = TagContainer(TagNamesIn(value).Distinct(StringComparer.Ordinal));
            }
            // Removals apply to the registry's copy too, and this is the half that decides whether
            // a gate actually opens -- the package says what a part is, the registry is what the
            // menu filters on.
            //
            // **ADDITIONS GO ONLY INTO CustomizationPartTags.** A record has more than one tag
            // container and they mean opposite things: CustomizationPartTags is what the part
            // DECLARES, CustomizationDisallowedPartTags is what it REFUSES. Both values begin
            // "(GameplayTags=", so a test on the shape alone put every added tag into both -- the
            // part declaring and refusing the same thing, and the menu leaving it out.
            //
            // Found on the clone head: adding the tag that makes a head selectable also made it
            // disallow being selectable, so no amount of correct tagging could ever have worked.
            // Every addTags override the tool has built carried this.
            //
            // Removals still apply to every container. Taking a name out of a refusal list is at
            // worst a no-op, and hero unlocks are proven in game on that behaviour.
            var ovIsPartTags = kv.Key.Text.Equals("CustomizationPartTags", StringComparison.Ordinal);
            var ovAdd = ovIsPartTags ? add : null;
            if ((remove is { Count: > 0 } || ovAdd is { Count: > 0 }) &&
                value.StartsWith("(GameplayTags=", StringComparison.Ordinal))
            {
                var names = TagNamesIn(value)
                    .Where(t => remove is null || !remove.Contains(t, StringComparer.Ordinal))
                    .ToList();
                foreach (var t in ovAdd ?? Array.Empty<string>())
                    if (!names.Contains(t, StringComparer.Ordinal)) names.Add(t);
                value = TagContainer(names.Distinct(StringComparer.Ordinal));
            }
            tags.Add((kv.Key.Text, value));
        }
        return tags;
    }

    /// <summary>A value trimmed to fit one log line.</summary>
    private static string Short(string value) =>
        value.Length <= 48 ? value : value[..45] + "...";

    /// <summary>
    /// A slot tag in the words a person uses. `Short` would truncate
    /// `br.Customization.Slot.Character.Outfit.Boots.Mesh` mid-tag and lose the only part of it
    /// that says which slot it is; the prefix is the same on every one of them.
    /// </summary>
    private static string SlotLeaf(string tag) =>
        tag.StartsWith(SlotTagPrefix, StringComparison.Ordinal) ? tag[SlotTagPrefix.Length..] : tag;

    private const string SlotTagPrefix = "br.Customization.Slot.Character.";

    /// <summary>The asset name out of a package name, for a log line before the loop computes it.</summary>
    private static string ovLeafOf(string packageName) =>
        packageName[(packageName.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Whether a name is shaped like a gameplay tag: dotted, and neither a path nor a sentence.
    ///
    /// Deliberately a shape test and not a lookup. The whole point of a minted tag is that it is in
    /// no table anywhere yet, so there is nothing to check it against -- and a check that could
    /// only ever confirm known tags would stay silent on exactly the case worth warning about.
    /// </summary>
    private static bool LooksLikeTag(string name) =>
        name.Length > 3 && name.Contains('.') && !name.Contains('/') && !name.Contains(' ');

    /// <summary>The last dotted or slashed segment of a name, for a log line that has to fit.</summary>
    private static string Leaf(string name)
    {
        var cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf('/'));
        return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
    }

    /// <summary>
    /// What a repointImports TARGET names: one of this mod's own copies, or any package in the game.
    ///
    /// A bare id -- "BP_Rifle_E-5" -- is a copy this mod builds, and that is what every project
    /// written so far uses. A full package name is taken as itself, which is what lets a gear kit
    /// point at a weapon the game already ships. It used to take the last segment of ANY value and
    /// resolve it inside the mod folder, so a full path silently became a mod-local asset that did
    /// not exist.
    /// </summary>
    private static string CopyTarget(string to, string modFolder) =>
        to.StartsWith('/') ? to : PackageNameOf(to, modFolder);

    /// <summary>
    /// The same, for a caller that has the project rather than the folder -- the verifier, which
    /// has to know where a copy's redirect actually landed in order to look for it in the store.
    /// </summary>
    /// <summary>
    /// The script module the game's mod framework lives in.
    ///
    /// Read out of the shipped PDB rather than guessed: the overlay action's compiled-in
    /// registration names
    /// `Bruno_Plugins_BitReactorModFramework_Source_BitReactorModRuntime_Public_...__Script_BitReactorModRuntime`,
    /// so the plugin is BitReactorModFramework and the module is BitReactorModRuntime.
    /// </summary>
    public const string ActionModule = "/Script/BitReactorModRuntime";

    public static string CopyTargetOf(string to, ModProject mod) => CopyTarget(to, FolderOf(mod));

    /// <summary>
    /// Which package's export hashes describe <paramref name="package"/>.
    ///
    /// Itself, unless this build is still MAKING it -- in which case its hashes are its template's,
    /// because a copy keeps them. See <see cref="ImportRepoint.MoveHashes"/>: without this, a
    /// redirect aimed at one of the mod's own assets reads an empty hash table out of a package
    /// that is not mounted yet, moves nothing, and leaves the reference resolving to the
    /// ORIGINAL's export while every other sign says it worked.
    ///
    /// Matched on the LEAF, because the redirect's target is a full package name and a copy is
    /// identified by its id.
    /// </summary>
    /// <summary>
    /// The stored value of one entry of an enum, from the mappings -- null when the enum or the
    /// entry is not there. A cooked EnumProperty is one byte holding this value, and the mappings
    /// are the only place that says which byte "Available" is.
    /// </summary>
    private static byte? EnumValue(IFileProvider provider, string enumName, string entry)
    {
        if (provider.MappingsContainer?.MappingsForGame?.Enums is not { } enums ||
            !enums.TryGetValue(enumName, out var values)) return null;
        foreach (var (value, name) in values)
            if (name == entry || name.EndsWith("::" + entry, StringComparison.Ordinal))
                return (byte) value;
        return null;
    }

    /// <param name="extra">
    /// COPIES THIS BUILD SYNTHESISED, which are not in the project file. A mission becomes a
    /// ModCopy at build time (see MissionBuild) and lives only in a local list, so a lookup
    /// against mod.Copies alone could not tell that `SK_Anaxes_045_Mod` was copied from
    /// `SK_Anaxes_045` -- and therefore could not know it inherits that package's export hashes.
    ///
    /// The failure is quiet and total: the redirect moves the package correctly and keeps the
    /// OLD export hash, so a table row points at the right package and looks up an export that is
    /// not there. It resolves to nothing, the mission is never offered, and every line of the
    /// build reads like a success. "could not move its export hashes, because X publishes none
    /// this build can read" is the one sentence that said so.
    /// </param>
    public static string HashSource(ModProject mod, string package, IReadOnlyList<ModCopy>? extra = null)
    {
        var leaf = package[(package.LastIndexOf('/') + 1)..];
        var found = mod.Copies.FirstOrDefault(
                        c => c.Id.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                    ?? extra?.FirstOrDefault(
                        c => c.Id.Equals(leaf, StringComparison.OrdinalIgnoreCase));
        return found?.Template is { Length: > 0 } t ? t : package;
    }

    /// <summary>The tag names inside a registry tag container, in the order written.</summary>
    private static List<string> TagNamesIn(string container)
    {
        var names = new List<string>();
        const string marker = "TagName=\"";
        var at = 0;
        while ((at = container.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
        {
            at += marker.Length;
            var end = container.IndexOf('"', at);
            if (end < 0) break;
            names.Add(container[at..end]);
            at = end;
        }
        return names;
    }

    /// <summary>A gameplay tag container in the exported text form the registry stores.</summary>
    private static string TagContainer(IEnumerable<string> tags)
    {
        var list = tags.ToList();
        return list.Count == 0
            ? "(GameplayTags=)"
            : "(GameplayTags=(" + string.Join(",", list.Select(x => $"(TagName=\"{x}\")")) + "))";
    }

    /// <summary>
    /// The tag that hides a character's face beneath a helmet. It is the PARENT tag, not the
    /// leaf `...Head.Face.Mesh` -- the cooked mod uses the parent, and searching for the leaf found
    /// fifteen unrelated dev helmets and none of the thing being looked for.
    /// </summary>
    public const string FaceSlotTag = "br.Customization.Slot.Character.Appearance.Humanoid.Head.Face";

    /// <summary>The package half of an object path, or null when there is not one.</summary>
    public static string? PackageOf(string? objectPath)
    {
        if (string.IsNullOrEmpty(objectPath) || !objectPath.StartsWith('/')) return null;
        var dot = objectPath.IndexOf('.');
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    /// <summary>
    /// Whether a part that wears <paramref name="wearing"/> should leave this fragment's mesh
    /// options exactly as the template has them.
    ///
    /// True when the options are keyed per HEAD (<see cref="PartDetails.KeyedPerHead"/>) and what
    /// the part wears is the mesh the part was SEEDED with, <paramref name="seed"/> -- the option
    /// <c>ModPart.FromGamePart</c> copies out of the template. That is a brow or a beard the author
    /// only renamed. Writing that one cut into every option, which is what every other slot does,
    /// puts the first head's brow (a Devaronian's, on Thick 1) on every human head.
    ///
    /// A mesh the author REPLACED is not the seed, so it still goes into every option: they asked
    /// for one mesh and the game's per-head cuts cannot be made from it. So does the mod's own copy
    /// of the seed (ownMesh, a repaint, an import): it lives in the mod's folder, so it is a
    /// different package and never equal. Compared by package, because a project may spell the mesh
    /// with or without the object suffix.
    ///
    /// Body-type options (helmets, torsos, anything keyed by height, weight and build) never match
    /// <see cref="PartDetails.KeyedPerHead"/>, so nothing about them changes.
    /// </summary>
    public static bool KeepsHeadCuts(IEnumerable<PartDetails.MeshOption> options, string? wearing,
                                       string? seed) =>
        PartDetails.KeyedPerHead(options) &&
        PackageOf(wearing) is { } wearingPackage &&
        PackageOf(seed) is { } seedPackage &&
        string.Equals(wearingPackage, seedPackage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a per-head part pays for a mesh COPY it did not ask for by name, as a sentence, or null.
    ///
    /// An eyebrow or a beard has one cut per head (29 and 25). The build leaves them alone while the
    /// part keeps the game's mesh (<see cref="KeepsHeadCuts"/>), and the moment the part needs a copy
    /// -- because it chose a material, or gave itself textures -- that ONE copy goes into every
    /// option and every head wears the first head's cut. It builds, verifies and installs clean, so
    /// it is said here. A part that asked for its own mesh or imported a file already knows.
    /// </summary>
    private static string? PerHeadCost(string who, IEnumerable<PartDetails.MeshOption> options,
                                       string? wearing, string? seed, bool askedForOwnMesh,
                                       bool paint, bool named)
    {
        if (askedForOwnMesh || !(paint || named)) return null;
        if (!KeepsHeadCuts(options, wearing, seed)) return null;

        var n = options.Count();
        var why = paint && named ? "your textures and your chosen material need"
                : paint ? "your textures need" : "your chosen material needs";
        return $"{who}: the game cuts this part to fit each head, and {why} a copy of the " +
               $"mesh -- so {n} of {n} head options now share ONE mesh copy, and every head loses " +
               "its own cut. To keep each head's cut, leave the mesh alone: no chosen material " +
               "and no textures of your own.";
    }

    /// <summary>
    /// Whether an override names another OUTFIT MESH slot -- the kind of override that would make
    /// one piece of a mod hide another. Head, hair and facial-hair overrides are not conflicts;
    /// they are what a helmet is for.
    /// </summary>
    private static bool IsOutfitMeshSlot(string slot) =>
        slot.Contains(".Torso.", StringComparison.Ordinal) ||
        slot.Contains(".Arms.", StringComparison.Ordinal) ||
        slot.Contains(".Legs.", StringComparison.Ordinal) ||
        slot.Contains(".Boots.", StringComparison.Ordinal) ||
        slot.Contains(".Helmet.", StringComparison.Ordinal);

    /// <summary>
    /// Whether an icon can be used without a cook. A game texture is an object path and can simply
    /// be pointed at; a loose PNG has to be imported and compressed into a platform texture, which
    /// is the cooker's job and is not reproduced here. Saying so is better than shipping a part
    /// whose icon silently stays the template's.
    /// </summary>
    public static bool IconNeedsCooking(string icon) =>
        icon.Length > 0 && !icon.StartsWith('/');

    /// <summary>
    /// Find a loose icon image. The mod file names it bare ("T_Mod_RexHelmet_Icon.png") because
    /// that is what a person writing the file wants to type, so the places it could sit are
    /// searched rather than demanded.
    /// </summary>
    /// <summary>The gameplay tags a mod registers: those it declares, plus one per icon of its own,
    /// plus every tag a slot set uses -- see <see cref="SlotSets.Tags"/> -- plus every tag a part's
    /// or a copy's own `addTags` names. Without the last of those, a brand-new tag used only in an
    /// `addTags` list (the ATTACHMENTS slot tags a placeholder part carries, say) never reached the
    /// mod's gameplay-tag ini -- it was written into AllowedSlots and the registry just fine, but
    /// nothing DECLARED it, which is a separate thing the game also wants.</summary>
    public static List<string> ShipTags(ModProject mod) =>
        mod.Tags.Concat(mod.Icons.Select(i => i.Tag)).Concat(SlotSets.Tags(mod))
           .Concat(mod.Parts.SelectMany(p => p.AddTags))
           .Concat(mod.Copies.SelectMany(c => c.AddTags))
           .Where(t => !string.IsNullOrWhiteSpace(t))
           .Distinct(StringComparer.Ordinal)
           .ToList();

    public static string? FindImage(string icon, string modFilePath)
    {
        var modDir = Path.GetDirectoryName(Path.GetFullPath(modFilePath)) ?? ".";

        // THIS MOD'S OWN SOURCE FOLDER, ahead of the shared one.
        //
        // `ModSource/<kind>/` used to be a single pool beside the projects, so every mod's picker
        // listed every other mod's art -- an author working on a clone trooper was offered the
        // textures of a jump trooper from a different project. The author's requirement:
        // per mod, "never overlap with any other mod source unless i import them into that mod
        // myself".
        //
        // Keyed on the PROJECT FILE'S NAME rather than the `name` field, because the file name is
        // what is unique on disk -- two projects cannot share one -- and because it is what both
        // ends of this already have in hand.
        //
        // ADDITIVE, deliberately: the shared candidates below stay, so every project built before
        // this keeps resolving with no edit to it. A per-mod copy simply wins when there is one.
        var own = Path.GetFileNameWithoutExtension(modFilePath);
        foreach (var candidate in new[]
                 {
                     // The author's own path first: an absolute or explicitly relative reference is
                     // a statement, not a guess, and two mods here point at a Desktop folder.
                     icon,
                     Path.Combine(modDir, "ModSource", own, "Icons", icon),
                     Path.Combine(modDir, "ModSource", own, "Textures", icon),
                     Path.Combine(modDir, "ModSource", own, "Meshes", icon),
                     Path.Combine(modDir, "..", "ModSource", own, "Icons", icon),
                     Path.Combine(modDir, "..", "ModSource", own, "Textures", icon),
                     Path.Combine(modDir, "..", "ModSource", own, "Meshes", icon),
                     Path.Combine(modDir, icon),
                     Path.Combine(modDir, "Icons", icon),
                     Path.Combine(modDir, "..", "ModSource", "Icons", icon),
                     Path.Combine(modDir, "ModSource", "Icons", icon),
                     // Artwork, which lives beside the icons rather than among them. Both folders
                     // are searched for both kinds: an author who puts a diffuse in Icons has made
                     // a filing mistake, not an error worth a build failure.
                     Path.Combine(modDir, "Textures", icon),
                     Path.Combine(modDir, "..", "ModSource", "Textures", icon),
                     Path.Combine(modDir, "ModSource", "Textures", icon),
                     Path.Combine(modDir, "Meshes", icon),
                     Path.Combine(modDir, "..", "ModSource", "Meshes", icon),
                     Path.Combine(modDir, "ModSource", "Meshes", icon),
                 })
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>
    /// Put the part's FBX into its copied mesh package.
    ///
    /// Everything the writer needs about the TEMPLATE comes from the parsed mesh -- the LOD vertex
    /// and index counts that locate the geometry, the bone map that says which bones the section
    /// may address, whether it carries vertex colours. None of it is guessed, and a mismatch in
    /// any of it is refused rather than written past.
    /// </summary>
    /// <summary>
    /// A GAME MESH COPIED INTO THE MOD, with the author's geometry and textures put into the copy.
    ///
    /// This is the whole reason a helmet mod never replaces anything: the game's mesh is read,
    /// written out under the mod's own package name, and the part points at that. Nothing of the
    /// game's is touched, and two mods doing it to the same source do not collide.
    ///
    /// Lifted out of the parts loop because a WEAPON needs exactly this and could not reach it. A
    /// part names its own mesh, so the parts loop had a package to work from; a weapon's mesh comes
    /// from a shared palette keyed by a tag, so there was nothing to hang it off and the weapon
    /// took the blunt route of overwriting the game's asset instead. The user objected to that, and
    /// was right to.
    ///
    /// <paramref name="label"/> is what a warning calls the caller, because the caller is no longer
    /// always a part.
    /// </summary>
    /// <summary>
    /// The source assets that MORE THAN ONE part of this mod copies.
    ///
    /// Keyed on the source package, because that -- not the part -- is what decides the
    /// destination name. Two parts off different bases never collide however similar they look;
    /// two parts off one base always do.
    ///
    /// Computed from the whole project before anything is written, so the answer does not depend
    /// on the order parts are built in. A rule that disambiguated "every part after the first"
    /// would move a mod's assets when its parts were merely reordered.
    /// </summary>
    private static HashSet<string> SharedSources(ModProject mod)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Count(string? objectPath)
        {
            if (PackageOf(objectPath) is not { Length: > 0 } pkg) return;
            seen[pkg] = seen.TryGetValue(pkg, out var n) ? n + 1 : 1;
        }

        foreach (var part in mod.Parts)
            // Only parts that actually take a copy. A part pointing at the game's own mesh writes
            // nothing and cannot collide with anything. A part that CHOSE a material takes one
            // too -- the mesh is what names it -- so it counts, or two heads off one base, one
            // of them choosing a material, would write to the same package and leave one standing.
            if (part.OwnMesh || part.HasPaint || part.HasNamedMaterial ||
                !string.IsNullOrWhiteSpace(part.MeshFile))
                Count(part.Mesh);

        // PIECES ARE NOT COUNTED HERE, deliberately. This pre-pass holds no provider, so for a
        // piece naming an import and no mesh it would count "" while the build copies the
        // TEMPLATE's mesh -- an answer about the wrong package, which is worse than no answer.
        // Every piece copy goes to a folder of its own instead; see the region branch in Build.

        foreach (var mesh in mod.Meshes) Count(mesh.Source);

        return seen.Where(x => x.Value > 1).Select(x => x.Key)
                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What ONE piece of a multi-piece garment will wear, and whether the mod carries a copy of it.
    /// </summary>
    /// <param name="Key">The piece's stable name -- see <see cref="PartDetails.RegionKeys"/>.</param>
    /// <param name="Fragment">Which mesh fragment of the template it is.</param>
    /// <param name="Mesh">The mesh the build will point that fragment at.</param>
    /// <param name="Entry">The author's entry, or null when they did not name this piece.</param>
    public sealed record RegionPlan(string Key, int Fragment, string Mesh, ModRegion? Entry);

    /// <summary>
    /// Resolve every piece of a template against what the part asks for. THE ONE PLACE the fallback
    /// rule lives, so the builder, the verifier and the editor cannot drift apart on it.
    ///
    /// Three cases, in order:
    ///   the author named this piece and gave it a mesh   -> that mesh
    ///   the part authors pieces but not THIS one         -> the template's own mesh for it, which
    ///                                                       makes the substitution a no-op and
    ///                                                       leaves the piece as the game has it
    ///   the part authors no pieces at all                -> <c>part.Mesh</c>, which is today
    ///
    /// <paramref name="unmatched"/> receives the names of entries that matched no fragment. They
    /// are never applied to a fragment they did not name -- an entry aimed at nothing is reported,
    /// not guessed at.
    /// </summary>
    public static List<RegionPlan> PlanRegions(PartDetails.Detail template, ModPart part,
                                               out List<string> unmatched)
    {
        var keys = PartDetails.RegionKeys(template);
        var body = PartDetails.BodyToken(part.Mesh);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<RegionPlan>();

        foreach (var group in template.MeshFragments)
        {
            var key = keys.FirstOrDefault(k => k.Fragment == group.Key).Key ?? "";

            // Matched through the ONE tolerant matcher, so an author writing BOOTS for a BOOT
            // fragment is understood and an ambiguous name is refused rather than resolved.
            ModRegion? entry = null;
            foreach (var r in part.Regions)
            {
                if (r.Region.Length == 0) continue;
                if (PartDetails.FragmentFor(template, r.Region) != group.Key) continue;
                entry = r;
                matched.Add(r.Region);
                break;
            }

            string mesh;
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Mesh)) mesh = entry.Mesh;
            else if (part.HasRegions)
                mesh = (group.FirstOrDefault(m =>
                            string.Equals(PartDetails.BodyToken(m.MeshPath), body,
                                          StringComparison.OrdinalIgnoreCase))
                        ?? group.FirstOrDefault())?.MeshPath ?? "";
            else mesh = part.Mesh;

            plans.Add(new RegionPlan(key, group.Key, mesh, entry));
        }

        unmatched = part.Regions.Where(r => r.Region.Length > 0 && !matched.Contains(r.Region))
                                .Select(r => r.Region).ToList();
        return plans;
    }

    /// <summary>
    /// One piece dressed up as a part, because <see cref="OwnMesh"/> was written for parts and
    /// asking it for anything else would mean two copies of a delicate path instead of one.
    ///
    /// The third caller of this pattern. It copies every FIELD of every material slot, not only
    /// the textures: the standalone-mesh loop drops <c>Material</c> and <c>SlotName</c>, and
    /// SlotName is exactly what aims a tint at the pack rather than the torso -- so copying that
    /// loop verbatim would inherit a live bug.
    /// </summary>
    /// <summary>
    /// The part, but wearing the GEOMETRY of an override this mod applies to the same mesh.
    ///
    /// Only the mesh-placement fields come from the override. The MATERIAL slots stay the part's
    /// own, because the copy being built exists so that this part can have a material of its own
    /// and the slot names are read off the part's paint -- taking the override's would name the
    /// slots after a replacement that repaints the game's texture in place and owns none.
    /// </summary>
    private static ModPart WithGeometryOf(ModPart part, ModOverride replacing)
    {
        var made = new ModPart
        {
            Id = part.Id,
            Mesh = part.Mesh,
            MeshFile = replacing.MeshFile,
            MeshScale = replacing.MeshScale,
            MeshBasis = replacing.MeshBasis,
            MeshOffsetX = replacing.MeshOffsetX,
            MeshOffsetY = replacing.MeshOffsetY,
            MeshOffsetZ = replacing.MeshOffsetZ,
        };

        for (var i = 0; i < part.Materials.Count; i++)
        {
            var to = made.Material(i);
            foreach (var (name, file) in part.Materials[i].Textures) to.Textures[name] = file;
            to.Material = part.Materials[i].Material;
            to.SlotName = part.Materials[i].SlotName;
        }
        return made;
    }

    /// <summary>
    /// Turn each enemy-stat adjustment into the copies and the override that carry it out.
    ///
    /// NOTHING OF THE GAME'S IS EDITED IN PLACE, and that is the entire point of the feature.
    /// `CPD_TacticalSpec_Enemy_Generic_Marauder` is used by 15 characters -- 12 Outlaw, 3 Pyke --
    /// and the tables it points at belong to the SEPARATIST BX Droid Commando. Editing those in
    /// place would rebalance the droids as well, silently, and an author would have no way to
    /// know. So each table is COPIED, the copy is scaled, and the spec is overridden to point at
    /// the copies.
    ///
    /// An adjustment that changes nothing contributes nothing: a group whose multipliers are all 1,
    /// or whose stats are all zero and cannot be scaled, produces no copies and no override rather
    /// than an override that rewrites sixteen paths to identical tables.
    /// </summary>
    private static void ExpandEnemyStats(IFileProvider provider, ModProject mod, Action<string>? log)
    {
        if (mod.EnemyStats.Count == 0) return;

        var groups = EnemyStats.All(provider, withUsers: false);
        foreach (var wanted in mod.EnemyStats)
        {
            var group = groups.FirstOrDefault(g =>
                g.Leaf.Equals(wanted.Group, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            { log?.Invoke($"  enemy stats: no group called '{wanted.Group}' -- skipped"); continue; }

            var adjusts = wanted.Multipliers
                .Select(kv => new EnemyStats.Adjust(kv.Key, kv.Value)).ToList();
            var plan = EnemyStats.Build(provider, group, adjusts,
                                        leaf => PackageNameOf(leaf, FolderOf(mod)));

            if (plan.Copies.Count == 0)
            {
                log?.Invoke($"  enemy stats: {group.Display} -- nothing to change " +
                            "(every multiplier is 1, or the stats it names are zero and a " +
                            "multiplier cannot raise a zero)");
                continue;
            }

            foreach (var (id, template) in plan.Copies)
            {
                var copy = new ModCopy { Id = id, Template = template, MadeBy = "enemystats" };
                foreach (var c in plan.Changes.Where(c => c.CopyId == id))
                    copy.Numbers.Add(new ModOverride.NumberChange(c.Row, "BaseValue", c.Was, c.Now));
                mod.Copies.Add(copy);
            }

            // ONE override for the spec, carrying every re-point. A second override on the same
            // target would be two mods fighting over one asset inside one mod.
            var ov = mod.Overrides.FirstOrDefault(o =>
                string.Equals(o.Target, plan.SpecPackage, StringComparison.OrdinalIgnoreCase));
            if (ov is null) { ov = new ModOverride { Target = plan.SpecPackage }; mod.Overrides.Add(ov); }
            foreach (var (from, to) in plan.Renames) ov.RenameNames[from] = to;

            log?.Invoke($"  enemy stats: {group.Display} -- {plan.Describe}");
        }
    }

    private static ModPart SyntheticPartFor(ModPart part, RegionPlan plan)
    {
        var made = new ModPart
        {
            // The part's own id, so OwnFolderFor files a contested source under the same
            // Parts/<id>/ folder the part already uses.
            Id = part.Id,
            Mesh = plan.Mesh,
            MeshFile = plan.Entry?.MeshFile ?? "",
            MeshScale = plan.Entry?.MeshScale ?? 0f,
            MeshBasis = plan.Entry?.MeshBasis ?? "",
            MeshOffsetX = plan.Entry?.MeshOffsetX ?? 0f,
            MeshOffsetY = plan.Entry?.MeshOffsetY ?? 0f,
            MeshOffsetZ = plan.Entry?.MeshOffsetZ ?? 0f,
        };

        var slots = plan.Entry?.Materials;
        for (var i = 0; slots is not null && i < slots.Count; i++)
        {
            var to = made.Material(i);
            foreach (var (name, file) in slots[i].Textures) to.Textures[name] = file;
            to.Material = slots[i].Material;
            to.SlotName = slots[i].SlotName;
        }
        return made;
    }

    /// <summary>
    /// Where one part's copies go: its own folder when it shares a source with another part,
    /// the mod's folder when it does not.
    ///
    /// The part id, because it is already required to be unique within the mod and is what the
    /// author sees in the editor -- so a collision that does happen is legible in the container
    /// rather than being a number.
    /// </summary>
    private static string OwnFolderFor(string modFolder, string sourcePackage, string partId,
                                       IReadOnlySet<string> shared, Action<string>? log)
    {
        if (!shared.Contains(sourcePackage)) return modFolder;

        // UNDER "Parts/", not directly under the mod folder.
        //
        // A part package lives at <modFolder><id>, so filing its assets at <modFolder><id>/ makes
        // a DIRECTORY whose name is exactly a package name. The game's own cooked content does
        // that once in 110,146 packages, which is not a precedent worth leaning on -- and the
        // first mod built this way had its second part show in the menu with no name and equip
        // nothing, while the first part was perfect. Everything measurable about that build was
        // correct: package, registry, container entry and mesh all read back clean.
        //
        // Whether the clash is the cause is NOT established. Putting the assets one level down
        // costs nothing, removes the oddity, and leaves a much smaller space to search if the
        // fault survives it.
        log?.Invoke($"      another part copies {Leaf(sourcePackage)} too, so this one's copies " +
                    $"go in a folder of its own: Parts/{partId}/");
        return $"{modFolder}Parts/{partId}/";
    }

    /// <param name="ownFolder">
    /// WHERE THIS PART'S COPIES GO, which is not always the mod's own folder.
    ///
    /// Every asset copied here is named after the SOURCE asset -- the mesh keeps its leaf name,
    /// and so do the material and the textures the repaint rewrites. That is deliberate: a slot,
    /// a texture suffix and half the build log are read off those names. But it means two parts
    /// built from the SAME game asset compute the SAME destination, and the second one written
    /// silently replaces the first -- one mesh, one material, one set of textures, and two parts
    /// pointing at all of it.
    ///
    /// That shipped. Two helmets in one mod, both off Clo000, and in the game both wore the
    /// second one. Nothing caught it: the build log said "copied ... -> ..." twice with identical
    /// destinations, and ModVerify passed because it checks each part on its own and each part
    /// was, individually, correct.
    ///
    /// So <see cref="SharedSources"/> finds the source assets more than one part copies, and those
    /// parts get a folder each. A source only one part uses keeps the path it has always had, so
    /// no mod that already works moves a single byte.
    /// </param>
    /// <param name="paintedAlready">
    /// ONE COPY PER (MATERIAL, ARTWORK) FOR THE WHOLE BUILD, so a mod does not ship the same
    /// repaint twice.
    ///
    /// A multi-slot part files every piece under Parts/&lt;id&gt;/&lt;key&gt;/ -- deliberately, because two
    /// pieces repainting one shared material would otherwise compute the same destination and the
    /// second would overwrite the first. The cost of that isolation is a byte-identical copy per
    /// piece, and a bodysuit worn by the torso AND the legs is how these kits are built: reported
    /// 2026-09-12 on a mod whose container reached 1.34 GB, with `T_Clo010_SUIT_D/MSK/N/Tint`
    /// (~89 MB together) copied once per region per part.
    ///
    /// The KEY is the source material AND the author's images, which is what makes this safe: two
    /// pieces painting one material with the same artwork produce identical bytes and can share a
    /// copy; two pieces painting it with DIFFERENT artwork are exactly the overwrite the folders
    /// exist to prevent, and they still get a copy each. Null disables it.
    /// </param>
    /// <param name="serious">
    /// Where a chosen material that could not be applied is said, so it reaches the panel as a
    /// serious note and not as a grey one. Null falls back to <paramref name="warnings"/>, which is
    /// where a caller with no such list (a mod's own mesh entry) wants it.
    /// </param>
    /// <param name="templateSwaps">
    /// The material swaps the part's template carries, so a material chosen for a slot the game
    /// swaps at run time can be called out. See <see cref="NamedMaterial.SwapProblems"/>.
    /// </param>
    /// <param name="paintedTintAlready">
    /// The tint-mask copy each paint in <paramref name="paintedAlready"/> wears, by the same key, so
    /// a piece OR A PART that SHARES an earlier paint can report the mask that paint made.
    /// </param>
    private static string? OwnMesh(IFileProvider provider, string sourceMeshPackage, ModPart part,
                                   ModProject mod, string ownFolder, string label,
                                   List<IoStoreWriter.Chunk> chunks,
                                   Dictionary<ulong, ContainerHeader.StoreEntry> storeFor,
                                   List<BuiltPart> built, List<string> warnings,
                                   Action<string>? log, Action<string>? tintMaskMade = null,
                                   Dictionary<string, string>? paintedAlready = null,
                                   Dictionary<string, string>? textureAlready = null,
                                   List<string>? serious = null,
                                   IReadOnlyList<PartDetails.MaterialSwap>? templateSwaps = null,
                                   string templateLeaf = "",
                                   Dictionary<string, string>? paintedTintAlready = null)
    {
        string? ownTintMask = null;
        string? ownMeshPath = null;
        {
            var copied = AssetCopy.Read(provider, sourceMeshPackage, out var copyWhy);
            if (copied is null)
                warnings.Add($"{label}: cannot carry its own mesh -- {copyWhy}");
            else
            {
                var leaf = sourceMeshPackage[(sourceMeshPackage.LastIndexOf('/') + 1)..];
                var meshPackage = PackageNameOf(leaf, ownFolder);
                var meshContainerPath = $"{ProjectName}/{ownFolder}{leaf}.uasset";

                // THE MOD'S OWN REPLACEMENT FOR THIS MESH, when it has one.
                //
                // A part takes a copy of its mesh the moment it carries a texture, because a
                // repaint needs a material of its own and the MESH is what names the material.
                // That copy was read from the provider, which is the GAME's bytes -- so a mod that
                // replaces a mesh with an override AND duplicates a part off it got the
                // replacement everywhere except on the part that had been painted.
                //
                // Reported by Nada: "if you have replaced a mesh/outfit and then dupe that asset
                // it works fine but if you change a single texture it reverts to the original game
                // mesh not the replaced mesh". Exactly right, and measurable -- a probe replacing
                // the 5,415-vertex Clo000 helmet with a 2,767-vertex file built a textured part
                // whose own copy still had 5,415, while the untextured one, which points at the
                // game path the override rewrote, had 2,767.
                //
                // The part's OWN import still wins where it has one: that is the author saying
                // what this particular part should look like, which is more specific than what
                // they said about the asset in general.
                var geometry = part;
                if (string.IsNullOrWhiteSpace(part.MeshFile) &&
                    mod.Overrides.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.MeshFile) &&
                        string.Equals(PackageOf(o.Target) ?? o.Target, sourceMeshPackage,
                                      StringComparison.OrdinalIgnoreCase)) is { } replacing)
                {
                    geometry = WithGeometryOf(part, replacing);
                    log?.Invoke($"      this mod replaces {Leaf(sourceMeshPackage)}, so its own " +
                                $"copy is built from {Path.GetFileName(replacing.MeshFile)} too");
                }


                // The repaint runs BEFORE the mesh is renamed, because what it produces is the
                // redirect the mesh needs: material -> the mod's copy of the material. Renaming
                // first would give a mesh copy that still names the game's material, and the
                // authored textures would sit in the container unreferenced.
                var meshRedirects = new List<AssetCopy.Redirect>();

                // EVERY IMPORT A REDIRECT SENDS SOMEWHERE WHOSE EXPORT IS NOT THE ONE IT LEAVES.
                //
                // A redirect moves the PACKAGE of a hard import; the export HASH beside it still
                // names the export of the material the mesh had. For a repainted copy of the mesh's
                // OWN material that is the same export (a copy keeps its template's hash) and
                // nothing needs doing -- which is why every unnamed repaint has always worked. For a
                // slot that names a DIFFERENT material, painted or not, the new package has an
                // export of another name and another hash, and the mesh read back as "does not
                // load". Collected here and applied once, after the model import if there is one.
                var hashMoves = new List<NamedMaterial.HashMove>();
                void NoteHashMove(string from, string to, string hashesFrom)
                {
                    if (string.Equals(from, hashesFrom, StringComparison.OrdinalIgnoreCase)) return;
                    if (!hashMoves.Any(m => m.From == from)) hashMoves.Add(new NamedMaterial.HashMove(from, to, hashesFrom));
                }

                // MATERIALS THE MOD ADDS, as opposed to ones it redirects.
                //
                // A slot the game's mesh already has is repainted by pointing its EXISTING import
                // at the mod's copy, which is a redirect and costs the package nothing. A slot the
                // author's file added has no import to point anywhere -- so the mesh has to be
                // given one, and the slot patched to name it. That is the only new machinery here;
                // everything else is the one-material path run more than once.
                var addedMaterials = new List<(string Package, string HashFrom)>();

                // SLOTS THAT NAME A GAME MATERIAL OUTRIGHT. No copy and no repaint -- the mesh
                // gains one import and the slot points at it, which is what a mesh does for its
                // own materials anyway. This is how a piece borrowed from another kit keeps the
                // material it came with.
                var namedMaterials = new List<(int Slot, string Package)>();
                for (var slot = 0; slot < part.Materials.Count; slot++)
                {
                    var named = part.Materials[slot].Material;
                    if (named.Length == 0) continue;
                    // NAMED AND PAINTED is no longer a conflict: the paint loop reads its chain
                    // from the named material and produces a copy of THAT, so this slot wants no
                    // bare import -- the copy is what it will wear.
                    if (part.Materials[slot].Textures.Count > 0) continue;
                    var pkg = PackageOf(named) ?? named;
                    namedMaterials.Add((slot, pkg));
                }

                if (part.HasPaint)
                {
                    var first = MaterialSkin.Read(provider, sourceMeshPackage, out var chainWhy);
                    if (first is null)
                        warnings.Add($"{label}: cannot repaint -- {chainWhy}");
                    else
                    {
                        log?.Invoke($"      {first.MaterialName} on slot {first.SlotName}, " +
                                    $"{first.Skins.Count} texture(s) of its own" +
                                    (first.SlotCount > 1 ? $" ({first.SlotCount} slots)" : ""));

                        for (var slot = 0; slot < part.Materials.Count; slot++)
                        {
                            var wanted = part.Materials[slot].Textures;
                            if (wanted.Count == 0) continue;

                            // Past the end of what the game's mesh has, there is no material of
                            // its own to follow -- so this one is painted from the first slot's,
                            // and becomes a SECOND copy of it with the author's other textures in.
                            var onTop = slot >= first.SlotCount;

                            // A GAME MATERIAL NAMED *AND* PAINTED: "use that one, with my tint in
                            // it". These were mutually exclusive until 2026-09-11 -- naming got the
                            // game's material verbatim, painting got a copy of whatever the MESH
                            // wore -- so an author who wanted an armour material with their own
                            // artwork in it could ask for neither. It is also the way out of the
                            // cloth trap below, which until now pointed at an option that dropped
                            // the author's textures on the floor.
                            var namedFor = part.Materials[slot].Material;
                            MaterialSkin.Chain? chain;
                            string? redirectFrom = null;
                            if (namedFor.Length > 0)
                            {
                                var namedPkg = PackageOf(namedFor) ?? namedFor;
                                chain = MaterialSkin.ReadMaterial(provider, namedPkg, out var namedWhy);
                                if (chain is null)
                                {
                                    warnings.Add($"{label}: material {slot + 1} names " +
                                                 $"{Leaf(namedFor)}, which could not be read -- {namedWhy}");
                                    continue;
                                }
                                // The redirect still has to name the material the MESH imports on
                                // this slot; the named one is a source of paint, not an import.
                                redirectFrom = onTop ? null
                                    : slot == first.Slot ? first.MaterialPackage
                                    : MaterialSkin.Read(provider, sourceMeshPackage, out _, slot)
                                                  ?.MaterialPackage;
                                log?.Invoke($"      material {slot + 1} paints {Leaf(namedPkg)} " +
                                            "rather than the mesh's own material for that slot");
                            }
                            // Past the end of what the game's mesh has, there is no material of
                            // its own to follow -- so this one is painted from the first slot's,
                            // and becomes a SECOND copy of it with the author's other textures in.
                            else if (onTop || slot == first.Slot) chain = first;
                            else
                            {
                                chain = MaterialSkin.Read(provider, sourceMeshPackage,
                                                          out var slotWhy, slot);
                                if (chain is null)
                                {
                                    warnings.Add($"{label}: material {slot + 1} was not painted -- {slotWhy}");
                                    continue;
                                }
                            }

                            // PAINTING ARMOUR INTO A CLOTH SHADER, said before the build finishes
                            // rather than left to be seen in game and blamed on the mask.
                            //
                            // A kit mesh's third material IS the undersuit, and the suit's material
                            // instance turns the cloth path on -- fuzz tint and the edge terms. An
                            // author whose third object is a shoulder plate gets that plate wearing
                            // fabric shading, which reads MATTE whatever roughness they paint. The
                            // build was doing exactly the right thing (a slot that exists is
                            // repainted from its OWN material) and the result still looked broken.
                            if (chain.IsCloth)
                                warnings.Add($"{label}: material {slot + 1} copies " +
                                             $"{Leaf(chain.MaterialPackage)}, which is a CLOTH " +
                                             "material -- it shades like fabric, so anything hard " +
                                             "painted into it reads matte in game however the " +
                                             "roughness mask is painted. If this slot is armour, " +
                                             "name an armour material on it -- \"material\": " +
                                             "\"<an armour MI>\" -- which now KEEPS your textures " +
                                             "and paints them into that material instead.");

                            var images = wanted.ToDictionary(
                                kv => kv.Key, kv => FindImage(kv.Value, mod.FilePath) ?? kv.Value,
                                StringComparer.OrdinalIgnoreCase);

                            // ALREADY PAINTED, IDENTICALLY, EARLIER IN THIS BUILD? Then point at
                            // that copy instead of making a second one. The key carries the source
                            // material and every image by key and path, so anything that would
                            // come out different is a different key and still gets its own copy.
                            var paintKey = chain.MaterialPackage + "|" + string.Join("|",
                                images.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                      .Select(kv => kv.Key + "=" + kv.Value));
                            if (paintedAlready is not null &&
                                paintedAlready.TryGetValue(paintKey, out var shared))
                            {
                                log?.Invoke($"      material {slot + 1} shares {Leaf(shared)}, " +
                                            "already painted in this build from the same artwork");
                                if (onTop) addedMaterials.Add((shared, chain.MaterialPackage));
                                else if (redirectFrom is { Length: > 0 })
                                {
                                    meshRedirects.Add(new AssetCopy.Redirect(
                                        redirectFrom, shared,
                                        KeepLeaf: !string.Equals(redirectFrom, chain.MaterialPackage, StringComparison.OrdinalIgnoreCase)));
                                    NoteHashMove(redirectFrom, shared, chain.MaterialPackage);
                                }
                                else
                                    meshRedirects.Add(new AssetCopy.Redirect(chain.MaterialPackage, shared));

                                // THE SHARED PAINT WEARS THE FIRST ONE'S TINT MASK, so this piece
                                // (or part) has one too. Skipping this `continue` past the tint
                                // code below left it reporting none, and the build then kept the
                                // game's mask on its fragment -- beside its siblings' own, and
                                // over artwork the author had given it. For a part that is a Tint
                                // the author imported and the build dropped, with a note saying
                                // they had not imported one.
                                if (ownTintMask is null && paintedTintAlready is not null &&
                                    paintedTintAlready.TryGetValue(paintKey, out var sharedTint))
                                {
                                    ownTintMask = sharedTint;
                                    log?.Invoke($"      its tint mask is that paint's {Leaf(sharedTint)}");
                                }
                                continue;
                            }

                            // Slot zero keeps the names it has always had, so an existing mod's
                            // assets do not move; every other slot is named apart from it.
                            var suffix = slot == 0 ? "" : $"_Mat{slot + 1}";
                            var painted = MaterialSkin.Repaint(provider, chain, images, ownFolder,
                                                               ProjectName, n => PackageNameOf(n, ownFolder),
                                                               out var paintWhy, log, suffix, textureAlready);
                            if (painted is null) { warnings.Add($"{label}: material {slot + 1}: {paintWhy}"); continue; }

                            // THE MOD'S OWN COPY OF THE TINT MASK, so the part's colour fragment
                            // can be pointed at it. See ownTintMask.
                            //
                            // Identified by the PARAMETER the source texture is bound to rather
                            // than by its name: "Tint Mask VT" is what the fragment overrides, and
                            // a texture called _Tint that is bound to something else is not it.
                            //
                            // THE COPY THIS PAINT WEARS, WHETHER IT AUTHORED IT OR SHARED IT. A
                            // repaint that finds the same source texture and the same image already
                            // authored earlier in the build points at that copy and authors
                            // nothing, so its Assets hold no tint texture: reading only Assets
                            // reported "no mask" for exactly the piece that reuses one, and the
                            // piece then kept the GAME's mask. SharedTextures names those.
                            string? paintTint = null;
                            if (chain.Skins.FirstOrDefault(x => x.Parameters.Any(
                                    pp => pp.Contains("Tint Mask", StringComparison.OrdinalIgnoreCase)))
                                is { } tintSkin)
                            {
                                if (painted.Assets.FirstOrDefault(x => x.IsTexture && string.Equals(
                                        x.Template, tintSkin.TexturePackage, StringComparison.OrdinalIgnoreCase))
                                    is { } tintMade)
                                    paintTint = tintMade.Package;
                                else if (painted.SharedTextures is { } sharedTints)
                                    foreach (var (template, package) in sharedTints)
                                        if (string.Equals(template, tintSkin.TexturePackage,
                                                          StringComparison.OrdinalIgnoreCase))
                                        { paintTint = package; break; }
                            }
                            // Remembered beside the paint key, so a LATER piece that shares this
                            // whole paint (the cache hit above) can report the same mask.
                            if (paintTint is not null)
                            {
                                if (paintedTintAlready is not null) paintedTintAlready[paintKey] = paintTint;
                                ownTintMask ??= paintTint;
                            }

                            chunks.AddRange(painted.Chunks);
                            foreach (var (id, entry) in painted.Store) storeFor[id] = entry;
                            foreach (var a in painted.Assets)
                                built.Add(new BuiltPart(a.Leaf, a.Package, a.Id, a.ContainerPath,
                                                        a.Bytes, a.Template, IsTexture: a.IsTexture,
                                                        IsCopy: true, Redirected: a.Redirected));
                            foreach (var w in painted.Warnings) warnings.Add($"{label}: {w}");

                            // WHAT THE MESH IMPORTS IS WHAT GETS REDIRECTED, which is not always
                            // what was painted: a slot naming a game material repaints THAT and
                            // still has to redirect the material the mesh actually points at.
                            if (paintedAlready is not null)
                                paintedAlready[paintKey] = painted.MaterialPackage;

                            // AND WHEN NOTHING WAS NAMED, WHAT THE MESH IMPORTS IS THE CHAIN'S OWN
                            // MATERIAL -- which is the ordinary case and was falling through to a
                            // warning.
                            //
                            // `redirectFrom` exists for the slot that names a DIFFERENT material to
                            // paint from: then the copy is of the named one and the mesh still
                            // imports its own, so the two differ. A slot that names nothing paints
                            // from the material the mesh imports, so they are the same thing and
                            // `chain.MaterialPackage` is the reference to move. The shared-material
                            // path above has always done exactly this; only this one did not, which
                            // is what makes it an oversight rather than a decision.
                            //
                            // WHAT IT COST: every part repainted through the plain `textures` form
                            // shipped its material and textures correctly and then had NOTHING
                            // POINT AT THEM -- the copies sat orphaned in the container while the
                            // mesh went on importing the game's material, so the part wore the
                            // donor's artwork. Measured on a five-part mod: all five meshes
                            // imported `.../_Armor_Kits/Clo001/Materials/MI_Clo001_TORS` and its
                            // siblings, while the mod's own MI_ and T_ copies held the author's art
                            // and were referenced by nothing. Everything else read back clean,
                            // which is why it took a container dump to see.
                            if (onTop) addedMaterials.Add((painted.MaterialPackage, chain.MaterialPackage));
                            else if (redirectFrom is { Length: > 0 })
                            {
                                meshRedirects.Add(new AssetCopy.Redirect(
                                    redirectFrom, painted.MaterialPackage,
                                    KeepLeaf: !string.Equals(redirectFrom, chain.MaterialPackage, StringComparison.OrdinalIgnoreCase)));
                                NoteHashMove(redirectFrom, painted.MaterialPackage, chain.MaterialPackage);
                            }
                            else if (chain.MaterialPackage is { Length: > 0 })
                                meshRedirects.Add(new AssetCopy.Redirect(chain.MaterialPackage,
                                                                         painted.MaterialPackage));
                            else
                                warnings.Add($"{label}: material {slot + 1} was painted but the " +
                                             "mesh's own material for that slot could not be read, " +
                                             "so nothing points at the copy -- the slot will wear " +
                                             "what it wore before");
                        }
                    }
                }

                // A MATERIAL NAMED, NOT PAINTED, ON GEOMETRY THAT IS KEPT.
                //
                // `namedMaterials` is otherwise consumed by ImportMesh alone, so a part that kept the
                // game's mesh and chose a material for a slot built clean and wore the base
                // mesh's own -- nothing carried the choice and nothing said so. The paint path
                // already knows how to make a mesh wear a different material: redirect the
                // copy's import of the one it has to the one wanted. A named material is that,
                // with a game package for the target instead of a repainted copy of one.
                //
                // Planned apart from the loop above because a slot that names a material and
                // paints nothing never enters it. Consumed here, so the import loop below does
                // not ALSO declare the package as a brand-new import nothing would point at.
                var wornMaterials = new List<NamedMaterial.Worn>();

                // The game mesh's slots, read ONCE for the plan below and the swap check after it.
                string? gameSlotsWhy = null;
                var gameSlots = part.Materials.Any(m => m.Material.Length > 0)
                    ? NamedMaterial.SlotsOf(provider, sourceMeshPackage, out gameSlotsWhy) : null;

                if (string.IsNullOrWhiteSpace(geometry.MeshFile) && namedMaterials.Count > 0)
                {
                    var notApplied = new List<string>();
                    wornMaterials = NamedMaterial.Plan(provider, sourceMeshPackage, label,
                                                       gameSlots, gameSlotsWhy,
                                                       namedMaterials, meshRedirects, notApplied, log);
                    foreach (var w in wornMaterials)
                    {
                        if (!meshRedirects.Any(r => r.FromPackage == w.From))
                            meshRedirects.Add(new AssetCopy.Redirect(w.From, w.To, KeepLeaf: true));
                        NoteHashMove(w.From, w.To, w.To);
                    }
                    foreach (var line in notApplied) (serious ?? warnings).Add(line);
                    namedMaterials.Clear();
                }

                // A SLOT THE GAME RE-SKINS AT RUN TIME. The HM00 head swaps MI_Head for a skin
                // tone whatever the mesh imports, so a material chosen for it would be built,
                // verified and never seen. Asked of every named slot, painted or not, applied
                // through a redirect or an import, because the swap does not care which.
                if (templateSwaps is { Count: > 0 } && part.Materials.Any(m => m.Material.Length > 0))
                {
                    var haveSlots = gameSlots ?? [];
                    var rename = string.IsNullOrWhiteSpace(geometry.MeshFile)
                        ? new Dictionary<int, string>()
                        : PlanSlotNames(
                            [.. Enumerable.Range(0, part.Materials.Count).Select(s => part.Materials[s].SlotName.Trim())],
                            [.. Enumerable.Range(0, part.Materials.Count)
                                 .Select(s => part.Materials[s].Material is { Length: > 0 } m ? PackageOf(m) ?? m : "")],
                            [.. haveSlots.Select(s => s.Name)]);
                    var chosen = new List<(int Slot, string SlotName, string Material)>();
                    for (var s = 0; s < part.Materials.Count; s++)
                    {
                        if (part.Materials[s].Material.Length == 0) continue;
                        // A slot past the game mesh's end has no name of its own unless the import
                        // gives it one, so it cannot be one a swap names.
                        var slotName = rename.TryGetValue(s, out var renamed) ? renamed
                                     : s < haveSlots.Count ? haveSlots[s].Name : "";
                        chosen.Add((s, slotName, PackageOf(part.Materials[s].Material) ?? part.Materials[s].Material));
                    }
                    foreach (var line in NamedMaterial.SwapProblems(label, templateLeaf, templateSwaps, chosen))
                        (serious ?? warnings).Add(line);
                }

                // A material with no section to wear it is paint nobody sees, and the reason is
                // always the same one: an extra material needs an extra OBJECT in the imported
                // file, and a part that imports nothing has one section however many materials it
                // names. Said out loud rather than shipped as a package that resolves to nothing.
                if (addedMaterials.Count > 0 && string.IsNullOrWhiteSpace(geometry.MeshFile))
                {
                    warnings.Add($"{label}: {addedMaterials.Count} extra material(s) were painted " +
                                 "and nothing wears them -- an extra material needs an extra " +
                                 "object in an imported file, and this imports none");
                    addedMaterials.Clear();
                }

                // THE COPY'S DECLARED IMPORTS, built here rather than after the rename, because a
                // material the mesh did not previously reference has to be declared BEFORE it can
                // be imported: the number an import map entry holds is a position in this list.
                var meshStore = copied.Store is null ? null
                              : AssetCopy.Store(copied.Store, meshRedirects);
                var extraMaterials = new List<(string Package, string HashFrom, int Import)>();
                var slotMaterials = new List<(int Slot, string Package, int Import)>();
                if (meshStore is not null)
                {
                    foreach (var (pkg, hashFrom) in addedMaterials)
                    {
                        extraMaterials.Add((pkg, hashFrom, meshStore.ImportedPackages.Count));
                        meshStore.ImportedPackages.Add(PackageId.Of(pkg));
                    }

                    // A named material is imported the same way, and its hash is its OWN -- it is
                    // the game's asset, not a clone of one, so there is no template to take a
                    // stored hash from.
                    foreach (var (slot, pkg) in namedMaterials)
                    {
                        var already = meshStore.ImportedPackages.IndexOf(PackageId.Of(pkg));
                        if (already >= 0) { slotMaterials.Add((slot, pkg, already)); continue; }
                        slotMaterials.Add((slot, pkg, meshStore.ImportedPackages.Count));
                        meshStore.ImportedPackages.Add(PackageId.Of(pkg));
                    }
                }
                else if (addedMaterials.Count > 0)
                    warnings.Add($"{label}: its extra material(s) cannot be imported -- the copied " +
                                 "mesh has no store entry to declare them in");

                // The imported geometry goes in BEFORE the rename, for the same reason the
                // textures do: renaming rewrites the package through ZenPackage, and every
                // offset this splice was measured against describes the bytes as they are now.
                var replaced = copied;
                if (!string.IsNullOrWhiteSpace(geometry.MeshFile))
                {
                    var imported = ImportMesh(provider, sourceMeshPackage, copied, geometry,
                                              mod.FilePath, out var importWhy, log,
                                              extraMaterials: extraMaterials,
                                              slotMaterials: slotMaterials);
                    // A REFUSED IMPORT IS NOT A FOOTNOTE. The mod still builds, still installs,
                    // and still works -- wearing the TEMPLATE'S geometry, because the author's was
                    // thrown away. That reads in game as "my model did not come through" and it
                    // was being reported in the same grey as a note about texture sizes, several
                    // lines down, where it was read past. Said as a refusal, in the author's terms.
                    if (imported is null)
                        warnings.Add($"{label}: YOUR MODEL WAS NOT USED -- {importWhy} " +
                                     "The part is built from the template's own geometry instead.");
                    else
                    {
                        replaced = copied with
                        {
                            Pieces = copied.Pieces
                                .Select(p => p.IsPackage ? p with { Data = imported.Package } : p)
                                .ToList(),
                        };
                        foreach (var n in imported.Notes) warnings.Add($"{label}: {n}");

                        // A CHOSEN MATERIAL THE IMPORT COULD NOT APPLY. The mesh's material list
                        // could not be located (CookedMesh.FindMaterials refuses a mesh with under
                        // eight bones, a weapon piece, and a list it cannot find), so the slots keep
                        // what the game's mesh gave them and every material chosen for one was
                        // dropped. Said once per material, serious, naming it -- the build used to
                        // blame "extra pieces" in grey and read clean.
                        if (imported.MaterialListWhy is { } listWhy)
                            foreach (var (chosenSlot, chosenPackage, _) in slotMaterials)
                                (serious ?? warnings).Add(
                                    $"{label}: material {chosenSlot + 1} ({Leaf(chosenPackage)}) was NOT applied: " +
                                    $"this mesh's material list could not be located ({listWhy}) -- " +
                                    "that slot still wears the material the game's mesh has");
                        log?.Invoke($"      {Path.GetFileName(geometry.MeshFile)}: " +
                                    $"{imported.Vertices:N0} vertex(es), {imported.Triangles:N0} triangle(s)");
                    }
                }

                // THE REDIRECTS' SECOND HALF. The rename and the store entry moved the PACKAGE; the
                // export hash inside the mesh still names the old material's export, which the new
                // package does not have. See NamedMaterial and `hashMoves` above.
                //
                // Run on whatever the package is NOW -- the imported model's bytes when there was an
                // import -- because the original slots' imports keep their positions: an import only
                // ever appends. It used to run only for a plain named slot on a kept mesh, so a slot
                // that named a material AND was repainted, or any of it on the import route, kept a
                // stale hash and read back as "does not load".
                if (hashMoves.Count > 0)
                {
                    var hashProblems = new List<string>();
                    if (copied.Store is null)
                        hashProblems.Add($"{label}: the material(s) you chose cannot be applied -- the " +
                                         "copied mesh has no store entry to redirect");
                    else
                    {
                        var moved = NamedMaterial.MoveHashes(
                            provider, replaced.Pieces.First(p => p.IsPackage).Data, copied.Store,
                            hashMoves, label, hashProblems, log);
                        replaced = replaced with
                        {
                            Pieces = replaced.Pieces
                                .Select(p => p.IsPackage ? p with { Data = moved } : p)
                                .ToList(),
                        };
                    }
                    foreach (var line in hashProblems) (serious ?? warnings).Add(line);
                }

                chunks.AddRange(AssetCopy.Rename(replaced, meshPackage, meshContainerPath,
                                                 meshRedirects, log));
                // The material is a HARD import of the mesh, so redirecting it has to move in the
                // store entry too -- the same fix the icon needed, one level down. Any material
                // the mod ADDED was appended to this list above, which is what its import indexes.
                if (meshStore is not null) storeFor[PackageId.Of(meshPackage)] = meshStore;
                else warnings.Add($"{label}: the copied mesh has no store entry to carry");

                // A soft reference names the package AND the asset, and the asset keeps its
                // name through the copy -- only the folder moved.
                ownMeshPath = $"{meshPackage}.{leaf}";
                // The size AFTER the import, not before. Reporting the template's size for a
                // mesh that grew by 145 KB says the import did not happen when it did.
                built.Add(new BuiltPart(leaf, meshPackage, PackageId.Of(meshPackage),
                                        meshContainerPath, replaced.Bytes, sourceMeshPackage,
                                        // Redirects INTO THE MOD, which is what the verifier's "points
                                        // at the mod's own assets" counts. A material the game already
                                        // has is checked by name instead (ModVerify.WearsChosenMaterials).
                                        IsCopy: true,
                                        Redirected: meshRedirects.Count -
                                                    wornMaterials.Select(w => w.From).Distinct().Count()));
            }
        }
        if (ownTintMask is { Length: > 0 }) tintMaskMade?.Invoke(ownTintMask);
        return ownMeshPath;
    }

    /// <param name="extraMaterials">
    /// Materials the mod added, which the mesh does not import yet: the package, the package whose
    /// stored export hash names its export, and the slot it will take in the container store
    /// entry's ImportedPackages -- which the caller has already appended it at.
    /// </param>
    private static CookedMesh.Imported? ImportMesh(IFileProvider provider, string meshPackage,
                                                   AssetCopy.Source copied, ModPart part,
                                                   string modFilePath, out string? why,
                                                   Action<string>? log, bool anySize = false,
                                                   IReadOnlyList<(string Package, string HashFrom,
                                                                  int Import)>? extraMaterials = null,
                                                   IReadOnlyList<(int Slot, string Package,
                                                                  int Import)>? slotMaterials = null)
    {
        why = null;

        var file = FindImage(part.MeshFile, modFilePath) ?? part.MeshFile;
        if (!File.Exists(file)) { why = $"cannot find the mesh file '{part.MeshFile}'"; return null; }

        // THE GAME'S OWN COPY, not whatever wins the provider lookup.
        //
        // Everything below reads shape off `template` -- per-LOD vertex counts, index counts,
        // required bones, material slots, the reference skeleton -- and writes it into `package`,
        // which comes from AssetCopy.PristineRead a few lines down. Those were two DIFFERENT
        // copies of the mesh whenever a mod had already replaced it, and the import then refused
        // itself with numbers that made no sense against the asset in front of you:
        //
        //     SKIPPED .../SK_HAAM_Hum_Trick_Head: LOD 1 parsed 13,671 vertices, the mesh says 32,730
        //
        // 13,671 is the game's real LOD 1; 32,730 is LOD 0's count, which is what every LOD of the
        // MOD's copy declares, because the import writes the author's mesh into all four (there is
        // no reducer). Reported from the field by somebody building on a head another mod already
        // replaced, and the check was right -- it was being handed two assets and told they were
        // one.
        if (AssetLoader.LoadPristinePrimary(provider, meshPackage)
            is not CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh template ||
            template.LODModels is not { Length: > 0 } lods)
        { why = $"{meshPackage} is not a skeletal mesh with LODs"; return null; }

        var package = copied.Pieces.FirstOrDefault(p => p.IsPackage)?.Data;
        if (package is null) { why = "the copied mesh has no package chunk"; return null; }

        FbxReader reader;
        FbxReader.Mesh? fbx;
        List<FbxReader.Mesh> fbxAll;
        try
        {
            reader = FbxReader.Read(file);
            // EVERY object, not the first. A body with a pad bolted to it is routinely two
            // objects, and reading one of them drops the other without a word.
            fbxAll = reader.ReadMeshes(out why);
            fbx = fbxAll.FirstOrDefault();
        }
        catch (Exception e) { why = $"{Path.GetFileName(file)}: {e.Message}"; return null; }
        if (fbx is null) return null;
        if (fbxAll.Count > 1)
            log?.Invoke($"      {fbxAll.Count} objects in the file, each its own section");

        // BLENDSHAPES ARE NOT CARRIED, and the author is told rather than left to wonder. No mesh
        // in this game has a morph target, so no template has the buffers to put one into --
        // carrying them would be structural work with nothing at the end of it. Said out loud
        // because in a modelling package the shapes are right there in front of the author.
        if (reader.MorphTargets > 0)
            log?.Invoke($"      note: the file has {reader.MorphTargets} blendshape target(s) and " +
                        "they are NOT carried -- nothing in this game uses morph targets, so " +
                        "there is nowhere for them to go");

        // THE FILE'S OWN SCALE, unless the mod overrides it.
        //
        // Resizing an object in a modelling package does not move its vertices -- it sets the
        // node's scale, and everything downstream is expected to honour it. Reading only the
        // vertices therefore ignores the artist entirely: they resize, re-export, and the mesh
        // comes out exactly as big as before, with nothing in the file looking wrong. That
        // happened, and it cost an export-and-check round trip to notice the node had gone from
        // 102.186 to 95.7046 while the vertices had not moved at all.
        var scale = part.MeshScale > 0 ? part.MeshScale
                  : reader.MeshNodeScale() is { } node && node.X > 0 ? (float) node.X
                  : DefaultMeshScale;
        if (part.MeshScale <= 0)
            log?.Invoke($"      scale {scale:F4} from the file's own node");
        // THE WHOLE NODE TRANSFORM -- its rotation and its move as well as its scale -- with the
        // mod's own mapping over the top. All four used to be assembled here, and the move was
        // never one of them; see MeshImport.Basis.ForFile for what that cost and why a named
        // mapping no longer switches the rotation off.
        if (MeshImport.Basis.ForFile(reader, scale, part.MeshBasis, out why, log) is not { } basis)
            return null;

        // ONE BASIS PER OBJECT.
        //
        // A file states each object's placement in ITS OWN node, and this used to read the first
        // one and apply it to everything. On a two-object file that is a coin flip: the body was
        // right because the body IS object one, and the pauldron bolted to it inherited the body's
        // transform instead of its own. Its node carried a scale of 2.664, so it arrived at
        // 1/2.664 of its size and -- scaling pivots about the origin -- at 1/2.664 of its height,
        // which put a shoulder pad at the knees. Nothing in the build was wrong about object one,
        // which is exactly why nothing caught it.
        //
        // The FILE's unit is object one's scale and is passed to every other object, so what each
        // node contributes is its own PLACEMENT and not a competing idea of how big a unit is.
        var bases = new List<MeshImport.Basis> { basis };
        var unit = reader.MeshNodeScale() is { } fileUnit && fileUnit.X != 0 ? (float) fileUnit.X : scale;
        for (var o = 1; o < fbxAll.Count; o++)
        {
            // THE GEOMETRY'S OWN INDEX, not its position in the list. ReadMeshes skips what it
            // cannot read -- one Geometry node per blendshape target, among others -- so the two
            // part company the moment a file carries any, and every object after the first would
            // be placed by somebody else's node.
            var each = MeshImport.Basis.ForFile(reader, scale, part.MeshBasis, out _, null,
                                                fbxAll[o].Geometry, unit);
            if (each is null) { bases.Add(basis); continue; }

            // Said out loud only when it DIFFERS, so a file whose objects share a transform stays
            // quiet and a file where one piece is placed differently says so.
            if (basis.Node is { } a && each.Node is { } b &&
                !a.SequenceEqual(b))
                log?.Invoke($"      object {o + 1} carries a node transform of its own");
            bases.Add(each);
        }
        if (part.MeshOffsetX != 0 || part.MeshOffsetY != 0 || part.MeshOffsetZ != 0)
        {
            // Every object, not only the first: the mod's offset moves the whole import, and
            // moving half of it would pull the pieces apart from each other.
            for (var i = 0; i < bases.Count; i++)
                bases[i] = bases[i] with
                {
                    OffsetX = part.MeshOffsetX, OffsetY = part.MeshOffsetY, OffsetZ = part.MeshOffsetZ,
                };
            basis = bases[0];
            log?.Invoke($"      moved ({part.MeshOffsetX}, {part.MeshOffsetY}, {part.MeshOffsetZ}) cm");
        }

        // SIZE-CHECKED AGAINST THE MESH IT REPLACES, before anything is written.
        //
        // The node scale is the artist's resize and has to be honoured -- but it is not
        // trustworthy on its own. Across three exports of one helmet it read 1.0 (right), 1.0
        // (wrong by ~100x, which shipped a 37-metre helmet a hundred metres from the character),
        // and 95.7046 (wrong by 6.8%). Nothing in any of those files looked wrong, and every one
        // of them cost a person a game launch to discover.
        //
        // A mod mesh REPLACES a specific game mesh, so the game's own mesh is the reference that
        // was missing. Its size is not a rule about what the mod may be -- an ARF helmet really is
        // bigger than the clone helmet it replaces -- so a difference is reported, never enforced.
        // What IS refused is an import that cannot be a helmet at all.
        if (lods[0].VertexBufferGPUSkin?.VertsFloat is { Length: > 0 } refVerts)
        {
            var theirs = MeshFit.Box.Of(refVerts.Select(v => ((float) v.Pos.X, (float) v.Pos.Y, (float) v.Pos.Z)));

            // EVERY OBJECT, NOT THE FIRST. The import MERGES them into one mesh, so the thing
            // that ships is the union -- and the union is what a size check about "can this be a
            // helmet at all" has to measure.
            //
            // REPORTED FROM THE GAME, 2026-09-17. A Deathtrooper helmet exported as two objects,
            // a helmet and a flashlight, and the build REFUSED it: "the import is 0.2x the height
            // of the mesh it replaces (5.8 cm against 35.6)". 5.8 cm is the FLASHLIGHT. It sorted
            // first in the file, so it was the only object measured, and the whole model was
            // thrown away and the part built from the template's geometry -- which in game reads
            // as "my model did not come through". Removing the flashlight made the helmet appear,
            // and the same fault had already cost him the arms, which he fixed by merging the
            // forearms and hands into one object.
            //
            // The refusal was right to exist and wrong about what it measured, and the correct
            // number was ALREADY IN THE LOG one line above it: `object 2: 22.9 x 28.2 x 29.2 cm`.
            // The per-object loop below was added when a shoulder pad shipped fifty centimetres
            // low; it reports every object and the RATIO still read object zero alone. Half a fix
            // reads exactly like a whole one until a file arrives whose first object is small.
            var ours = new List<(float, float, float)>(fbxAll.Sum(f => f.VertexCount));
            for (var o = 0; o < fbxAll.Count; o++)
            {
                // Its OWN basis, for the same reason the per-object report below uses one.
                var uBasis = bases[Math.Min(o, bases.Count - 1)];
                for (var i = 0; i + 2 < fbxAll[o].Positions.Length; i += 3)
                    ours.Add(uBasis.Apply((float) fbxAll[o].Positions[i], (float) fbxAll[o].Positions[i + 1],
                                          (float) fbxAll[o].Positions[i + 2]));
            }
            var mine = MeshFit.Box.Of(ours);
            if (fbxAll.Count > 1)
                log?.Invoke($"      size measured across all {fbxAll.Count} objects, which is what " +
                            "the import merges and ships");

            // EVERY OTHER OBJECT, measured against the mesh they are joining.
            //
            // This check only ever looked at the FIRST object, and said nothing about the rest --
            // which is how a shoulder pad shipped fifty centimetres below the shoulder it belongs
            // on, through a build that reported every number as correct. The body matched, so the
            // build was happy; nothing had looked at the piece that was actually wrong.
            //
            // A piece is not required to sit inside the template's box -- an antenna sticks out on
            // purpose -- so this reports rather than refuses. What it will not do is stay quiet.
            for (var o = 1; o < fbxAll.Count; o++)
            {
                // ITS OWN basis. Measuring object two with object one's transform would report
                // the position this check exists to catch as if it were correct.
                var oBasis = bases[Math.Min(o, bases.Count - 1)];
                var each = new List<(float, float, float)>(fbxAll[o].VertexCount);
                for (var i = 0; i + 2 < fbxAll[o].Positions.Length; i += 3)
                    each.Add(oBasis.Apply((float) fbxAll[o].Positions[i], (float) fbxAll[o].Positions[i + 1],
                                          (float) fbxAll[o].Positions[i + 2]));
                if (each.Count == 0) continue;

                var box = MeshFit.Box.Of(each);
                log?.Invoke($"      object {o + 1}: {box.Describe} at " +
                            $"({box.Centre.X:F2}, {box.Centre.Y:F2}, {box.Centre.Z:F2})");

                // Wholly outside is the loud case: the piece cannot be attached to anything it
                // was modelled against, and in game it is either buried or floating.
                var outside = box.Centre.Z < theirs.Z0 || box.Centre.Z > theirs.Z1 ||
                              box.Centre.X < theirs.X0 || box.Centre.X > theirs.X1 ||
                              box.Centre.Y < theirs.Y0 || box.Centre.Y > theirs.Y1;
                if (outside)
                    log?.Invoke($"      note: object {o + 1} sits OUTSIDE the mesh it joins " +
                                $"(which spans Z {theirs.Z0:F1}..{theirs.Z1:F1}) -- in game it " +
                                "will be buried inside the character or floating away from them");
            }

            var ratio = theirs.Height > 0 ? mine.Height / theirs.Height : 1f;
            log?.Invoke($"      size {mine.Describe} against the game's {theirs.Describe}" +
                        (Math.Abs(ratio - 1f) > 0.02f ? $"  ({ratio:F3}x its height)" : ""));
            log?.Invoke($"      centre ({mine.Centre.X:F2}, {mine.Centre.Y:F2}, {mine.Centre.Z:F2})" +
                        $" against the game's ({theirs.Centre.X:F2}, {theirs.Centre.Y:F2}, " +
                        $"{theirs.Centre.Z:F2})");

            // Wildly out means the scale is wrong, not that the art is unusual. Refusing here
            // costs a rebuild; not refusing costs a launch, and the mod looks absent rather than
            // wrong because the mesh is somewhere off the map.
            if (ratio is > 5f or < 0.2f && !anySize)
            {
                why = $"the import is {ratio:F1}x the height of the mesh it replaces " +
                      $"({mine.Height:F1} cm against {theirs.Height:F1}) -- the scale is wrong. " +
                      $"The file's own node says {(reader.MeshNodeScale() is { } n ? n.X : 0):F4}; " +
                      $"this game measures {DefaultMeshScale:F3}. Set meshScale to fix it.";
                return null;
            }
            // A near miss is the dangerous one: it looks almost right in game and reads as a
            // modelling choice. Say what scale WOULD match, so the number is there to copy.
            if (part.MeshScale <= 0 && Math.Abs(ratio - 1f) > 0.03f)
                log?.Invoke($"      note: a scale of {scale / ratio:F3} would match its height exactly" +
                            " -- set meshScale if that is what you meant");
        }

        // Bone NAMES, not indices. A section's bone map is a list of skeleton indices, and an FBX
        // knows only names -- so the translation happens here, and a weight on a bone the section
        // cannot address is dropped with a note rather than silently landing on bone zero.
        // THE UNION OF EVERY SECTION'S BONE MAP, not section zero's.
        //
        // A section's bone map lists only the bones THAT section needs, and a template with
        // several sections spreads them out: the Clo010 legs keep the thigh and calf bones in
        // section 0 and `knee_pad_l`/`knee_pad_r` in section 1. Resolving the import against
        // section zero alone therefore could not see the knee pad bones at all, dropped every
        // weight on them, and pinned 624 vertices to bone zero -- which put the knee pads inside
        // the legs. The build SAID so in a note and the note was true; nothing was reading it.
        //
        // Section 0's order comes first so a single-section template is byte-for-byte unchanged,
        // and anything only later sections mention is appended.
        var boneOrder = new List<ushort>();
        foreach (var sec in lods[0].Sections)
            foreach (var b in sec.BoneMap)
                if (!boneOrder.Contains(b)) boneOrder.Add(b);

        // WHAT THE MESH BEING REPLACED ACTUALLY MOVES.
        //
        // PhysicsBones compares against this rather than against the physics asset's whole body
        // list, and the difference is the difference between a useful warning and a false one:
        // PHYS_Twi_T01 simulates twelve lekku bones, and SK_HAAF_Twi_T01_HelmBase -- a helmet BASE
        // that only covers the top of them -- puts weight on five. Measured against twelve, a
        // round trip of the template's OWN geometry was told it had lost seven it never had.
        var templateBones = PhysicsBones.Weighted(lods[0], template.ReferenceSkeleton.FinalRefBoneInfo);

        // AND WHATEVER THE FILE IS SKINNED TO, when the skeleton has it and this LOD evaluates it.
        //
        // The template's own sections are a floor, not a ceiling. A garment borrowed from another
        // kit brings the bones it was rigged to: the Clo003 holsters on Clo010 legs are weighted
        // to `spine_02`, which no legs section lists because no legs geometry needed it -- and
        // dropping those weights is the difference between a holster that hangs off the hip and
        // one that does not move with the body at all.
        //
        // Bounded by RequiredBones, which is the set the LOD actually evaluates. A bone outside it
        // is not animated at that LOD, so pointing a vertex at it would be worse than dropping the
        // weight, and it stays refused with the note it already had.
        var evaluated = new HashSet<int>((lods[0].RequiredBones ?? []).Select(b => (int) b));
        var skeleton = template.ReferenceSkeleton.FinalRefBoneInfo;
        foreach (var name in fbxAll.SelectMany(m => m.Influences).Select(i => i.Bone).Distinct())
        {
            // THROUGH THE ALIAS TABLE, because the file does not use this rig's names.
            //
            // REPORTED FROM THE GAME, 2026-09-17: a helmet from a differently rigged character
            // had its neck clip, because
            // its `Neck` and `Neck1` weights had been folded onto `head`. This widening already
            // existed -- it is what lets a borrowed holster keep `spine_02` -- and it compared
            // the FILE'S name against the SKELETON'S. "Neck" never equals "neck_01", so the
            // lookup found nothing, skipped, and the weights fell back to the nearest bone the
            // part carried.
            //
            // The infuriating part: the import log said the answer a screen later -- "Neck wants
            // neck_01, Neck1 wants neck_02" -- because MeshImport asks RigAliases and this did
            // not. One table knew, and the two halves never spoke.
            //
            // A HELPER IS SKIPPED ON PURPOSE. `isHelper` means a corrective or physics bone this
            // rig has no equivalent for, and folding its weight onto the joint it supports is the
            // deliberate behaviour -- giving it a slot of its own would animate a correction the
            // rig cannot drive. See RigAliases.For.
            var wanted = name;
            if (Array.FindIndex(skeleton,
                    b => b.Name.Text.Equals(wanted, StringComparison.OrdinalIgnoreCase)) < 0 &&
                RigAliases.For(name, out var isHelper) is { } aliased)
            {
                if (isHelper) continue;
                wanted = aliased;
            }

            // Membership tested on the RESOLVED name, or a bone the template already lists under
            // this rig's spelling would be added a second time.
            if (boneOrder.Any(b => b < skeleton.Length &&
                    skeleton[b].Name.Text.Equals(wanted, StringComparison.OrdinalIgnoreCase))) continue;

            var at = Array.FindIndex(skeleton,
                b => b.Name.Text.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (at < 0 || !evaluated.Contains(at)) continue;

            boneOrder.Add((ushort) at);
            log?.Invoke($"      bone map: added '{wanted}'" +
                        (wanted.Equals(name, StringComparison.OrdinalIgnoreCase) ? "" : $" for '{name}'") +
                        ", which the file needs and the template's sections did not list");
        }

        // A bone index is one byte in the weight buffer ONLY WHILE bUse16BitBoneIndex IS CLEAR,
        // and the writer sets it from the data (see CookedMesh.Geometry). So the ceiling is the
        // format's, which MeshImport already states: sixteen bits of bone index.
        //
        // THIS GATE SAID 255 AND WAS WRONG. It was right when it was written, was never revisited
        // when the writer learned the wide form, and then refused every head in the game from a
        // limit the code below it no longer had. Reported by a user trying to put a custom face
        // on SK_HAAM_Hum_Trick_Head: "the template's sections need 660 bones between them and a
        // section can address 255", followed by "Build failed: nothing could be built".
        //
        // The template he was aiming at has 639 bones in its FIRST section alone, so this was
        // never a case that per-section maps would have rescued either -- a facial rig simply
        // needs the wide index, and the writer has been able to produce one for some time.
        //
        // Two limits for one rule is what went wrong, so this one now defers to the other rather
        // than restating it.
        if (boneOrder.Count > MeshImport.MaxBoneMap)
        {
            why = $"the template's sections need {boneOrder.Count} bones between them and a " +
                  $"section can address {MeshImport.MaxBoneMap:N0}";
            return null;
        }

        var boneNames = boneOrder
            .Select(b => b < template.ReferenceSkeleton.FinalRefBoneInfo.Length
                         ? template.ReferenceSkeleton.FinalRefBoneInfo[b].Name.Text : "")
            .ToList();

        if (lods[0].Sections.Length > 1)
            log?.Invoke($"      bone map: {boneOrder.Count} bone(s) across " +
                        $"{lods[0].Sections.Length} template section(s)");

        // GIVE THE MESH A HARD IMPORT OF EACH ADDED MATERIAL, and keep the FPackageIndex each one
        // came back as -- that is what its new slot will name. Stopping at the first failure
        // rather than skipping it: the list is consumed by POSITION, so a gap in it would point
        // one slot at the material meant for another, which is worse than the fallback.
        var bytes = package;
        var extraImports = new List<int>();
        foreach (var extra in extraMaterials ?? [])
        {
            var hash = PropertyAdd.PublicExportHashOf(provider, extra.HashFrom);
            if (hash == 0)
            { log?.Invoke($"      {Leaf(extra.HashFrom)} declares no export hash, so the slot " +
                          "for it keeps the first material"); break; }

            bytes = PropertyAdd.AddImport(bytes, extra.Import, hash, extra.Package,
                                          out var fpi, out var impWhy);
            if (impWhy is not null)
            { log?.Invoke($"      could not import {Leaf(extra.Package)}: {impWhy}"); break; }

            extraImports.Add(fpi);
            log?.Invoke($"      imports {Leaf(extra.Package)} as {fpi} (store slot {extra.Import})");
        }

        // The slots that name a game material, resolved to the FPackageIndex each will hold.
        var namedSlots = new Dictionary<int, int>();
        foreach (var named in slotMaterials ?? [])
        {
            var hash = PropertyAdd.PublicExportHashOf(provider, named.Package);
            if (hash == 0)
            { log?.Invoke($"      {Leaf(named.Package)} declares no export hash, so slot " +
                          $"{named.Slot} keeps the material it had"); continue; }

            bytes = PropertyAdd.AddImport(bytes, named.Import, hash, named.Package,
                                          out var nfpi, out var nWhy);
            if (nWhy is not null)
            { log?.Invoke($"      could not import {Leaf(named.Package)}: {nWhy}"); continue; }

            namedSlots[named.Slot] = nfpi;
            log?.Invoke($"      slot {named.Slot} names {Leaf(named.Package)} " +
                        $"as {nfpi} (store slot {named.Import})");
        }

        // What growing the material array needs, all of it read off the parsed template. The
        // import decides for itself whether to grow, because it is the only thing that knows how
        // many materials the file used -- and it has to decide BEFORE it writes a section that
        // names one.
        var slotName = template.SkeletalMaterials is { Length: > 0 } sm
            ? ZenPackage.Read(bytes).Names.FindIndex(n => n == sm[0].MaterialSlotName.PlainText) : -1;
        var growth = new CookedMesh.Growth(template.SkeletalMaterials?.Length ?? 0,
                                           template.ReferenceSkeleton.FinalRefBoneInfo.Length,
                                           slotName, extraImports, namedSlots,
                                           SlotNames(part, template, log));

        // THE TEMPLATE'S OWN VERTICES AND COLOURS, so a mask a round trip flattened can be taken
        // back. Read from LOD 0, which is what the import replaces.
        var templateColours = new List<(float, float, float, byte, byte, byte, byte)>();
        if (lods[0].VertexBufferGPUSkin?.VertsFloat is { Length: > 0 } tv &&
            lods[0].ColorVertexBuffer?.Data is { Length: > 0 } tc)
            for (var i = 0; i < tv.Length && i < tc.Length; i++)
                templateColours.Add(((float) tv[i].Pos.X, (float) tv[i].Pos.Y, (float) tv[i].Pos.Z,
                                     tc[i].R, tc[i].G, tc[i].B, tc[i].A));

        log?.Invoke($"      template carries {templateColours.Count:N0} vertex colour(s)");

        var imported = CookedMesh.Import(bytes,
                                        lods.Select(l => l.NumVertices).ToList(),
                                        lods.Select(l => l.Indices?.Buffer?.Length ?? 0).ToList(),
                                        lods[0].RequiredBones?.Length ?? 0,
                                        template.bHasVertexColors, boneNames, fbxAll, bases, out why, log,
                                        growth, boneOrder.ToArray(), templateColours);

        // WHETHER THE THING THAT MOVED THIS MESH CAN STILL MOVE IT.
        //
        // Asked here and not in CookedMesh, which works on bytes and knows nothing about a
        // USkeletalMesh -- the physics asset is a property on the parsed template, and this is
        // where the template is in hand. See PhysicsBones for the report that asked for it: a
        // lekku import that came out correct in every way the build could measure, and hung there.
        if (imported is not null)
            foreach (var note in PhysicsBones.Notes(provider, template, templateBones, fbxAll))
                imported.Notes.Add(note);

        return imported;
    }

    /// <summary>
    /// WHAT TO CALL EACH MATERIAL SLOT, which decides which of them a tint lands on.
    ///
    /// A grown slot is a CLONE of the first one, name included, and that is how a commando torso
    /// ended up with three slots all called `MI_TORS`. A customization fragment targets a slot by
    /// name -- `MaterialSlotNames: ["MI_TORS"]` -- so one mask painted all three, and the backpack
    /// got the torso's mask sampled with the backpack's UVs.
    ///
    /// The game's own meshes name every slot apart, and this follows their convention:
    ///
    ///     SK_HAAM_Clo010_TORS   slot 0  MI_SUIT       MI_Clo010_SUIT
    ///                           slot 1  MI_TORS       MI_Clo010_TORS
    ///     SK_HAAM_Clo003_TORS   slot 0  MI_TORS_ADD   MI_Clo003_TORS
    ///                           slot 1  MI_TORS       MI_Clo001_TORS
    ///
    /// -- the kit token drops out of the material's name and what is left names the slot, with
    /// `_ADD` breaking a tie. Only slots that NAME A GAME MATERIAL are renamed: one that clones
    /// the first slot's paint should keep the first slot's name, because it wears its material.
    ///
    /// AN EXPLICIT `slotName` IS HONOURED VERBATIM -- see PlanSlotNames, which is where the
    /// deciding happens and where both this and the PREVIEW go for the answer.
    /// </summary>
    private static Dictionary<int, string>? SlotNames(
        ModPart part,
        CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh template,
        Action<string>? log)
    {
        var have = template.SkeletalMaterials ?? [];
        var want = PlanSlotNames(
            [.. Enumerable.Range(0, part.Materials.Count).Select(s => part.Materials[s].SlotName.Trim())],
            // FROM WHAT THE AUTHOR NAMED, not from the slots that ended up importing it. A slot
            // that names a material AND paints it wears a COPY of that material, so it takes its
            // name from it just the same -- and it is absent from slotMaterials precisely because
            // it imports the copy instead. Reading the import list cost that slot its name.
            [.. Enumerable.Range(0, part.Materials.Count)
                 .Select(s => part.Materials[s].Material is { Length: > 0 } m
                              ? PackageOf(m) ?? m : "")],
            [.. have.Select(m => m.MaterialSlotName.Text)],
            log);
        return want.Count == 0 ? null : want;
    }

    /// <summary>
    /// WHICH SLOTS GET RENAMED AND TO WHAT. The one place this is decided, called by the build and
    /// by the preview, because a preview that names slots differently paints a tint the game will
    /// not -- which is the entire failure this area exists to stop.
    ///
    /// A name the author WROTE is taken verbatim. A name DERIVED from a material is made unique.
    /// That asymmetry is the fix for a bug reported 2026-09-11: the tie-break used to run over
    /// both, so `slotName` -- documented as the way to aim a template's tint at a slot
    /// deliberately -- could never actually put two slots under one name. A fragment names its
    /// target slots in an array on a GAME Blueprint shared by every part of that region, so a mod
    /// cannot add a name to it; making a second slot ANSWER to the name already there is the only
    /// route a mod has, and it was the one route blocked.
    ///
    /// Two slots under one name means ONE mask painted over both, each sampled with its own UVs.
    /// That is right for a mesh split across materials on a shared layout and wrong for a piece
    /// bolted on with a layout of its own -- which is why a derived name still never does it by
    /// accident, and why an explicit one says so in the log.
    /// </summary>
    /// <param name="explicitNames">Per slot, what the author wrote in `slotName`, or "".</param>
    /// <param name="namedMaterials">Per slot, the GAME material it wears outright, or "".</param>
    /// <param name="templateNames">What the template's mesh calls each slot it has.</param>
    public static Dictionary<int, string> PlanSlotNames(
        IReadOnlyList<string> explicitNames,
        IReadOnlyList<string> namedMaterials,
        IReadOnlyList<string> templateNames,
        Action<string>? log = null)
    {
        var first = templateNames.Count > 0 ? templateNames[0] : null;
        var count = Math.Max(explicitNames.Count, namedMaterials.Count);

        // WHAT EACH SLOT WOULD LIKE TO BE CALLED, before anything is made unique -- because which
        // names are already spoken for depends on which slots this ends up renaming.
        var asked = new Dictionary<int, string>();
        var spelt = new HashSet<int>();
        for (var slot = 0; slot < count; slot++)
        {
            var chosen = slot < explicitNames.Count ? explicitNames[slot].Trim() : "";
            if (chosen.Length > 0) spelt.Add(slot);
            else
            {
                // Derived only from a NAMED material. A painted slot has no game asset to take a
                // name from, and it wears the first slot's material, so it keeps its name too.
                var named = slot < namedMaterials.Count ? namedMaterials[slot] : "";
                if (string.IsNullOrEmpty(named)) continue;
                chosen = SlotNameFor(named);
            }
            asked[slot] = chosen;
        }
        if (asked.Count == 0) return new Dictionary<int, string>();

        // The names the template already has on slots this is NOT touching. A derived name that
        // landed on one of those would only move the bug to a different pair of slots.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var slot = 0; slot < templateNames.Count; slot++)
            if (!asked.ContainsKey(slot)) taken.Add(templateNames[slot]);

        var want = new Dictionary<int, string>();

        foreach (var (slot, chosen) in asked.Where(x => !spelt.Contains(x.Key)).OrderBy(x => x.Key))
        {
            // `_ADD` is the game's own tie-break: SK_HAAM_Clo003_TORS calls its second torso
            // material MI_TORS_ADD rather than repeat MI_TORS.
            var unique = chosen;
            for (var n = 1; taken.Contains(unique); n++)
                unique = n == 1 ? $"{chosen}_ADD" : $"{chosen}_ADD{n}";
            if (!string.Equals(unique, chosen, StringComparison.Ordinal))
                log?.Invoke($"      slot {slot} would be called {chosen}, which is taken -- " +
                            $"named {unique}. Write \"slotName\": \"{chosen}\" on it to share " +
                            "the name on purpose, which shares the tint with it.");
            taken.Add(unique);
            want[slot] = unique;
        }

        // THE AUTHOR'S OWN NAMES LAST, laid over names nobody chose.
        //
        // The other order is the obvious one and it is wrong: spelling `MI_LEGS` on a second slot
        // would push the slot that ALREADY derived MI_LEGS to `MI_LEGS_ADD` -- taking the tint off
        // a piece that had it, to give it to the piece being added. This way every derived name is
        // exactly what it was before any of this existed, and an explicit one simply joins it.
        foreach (var slot in spelt.OrderBy(x => x))
        {
            var chosen = asked[slot];
            if (taken.Contains(chosen))
                log?.Invoke($"      slot {slot} is called {chosen} DELIBERATELY, which another " +
                            "slot already answers to: one mask paints both, each sampled with " +
                            "its own UVs. That is what sharing a name is for -- and it is wrong " +
                            "if these two pieces do not share a UV layout.");
            taken.Add(chosen);
            want[slot] = chosen;
        }

        // ONE SLOT MUST STILL ANSWER TO THE TEMPLATE'S FIRST NAME, or the template's own tint
        // fragment matches nothing and the part stops taking the player's colour altogether --
        // a silent loss, and a worse one than the mask it is here to fix. When renaming would
        // leave nothing behind, slot zero keeps what it had.
        //
        // An author who SPELT slot zero's name is not overruled: they said what they wanted, and
        // this guard exists for names nobody chose.
        if (first is { Length: > 0 } && !taken.Contains(first) &&
            want.ContainsKey(0) && !spelt.Contains(0))
        {
            log?.Invoke($"      slot 0 keeps the name {first}: nothing else would answer to it, " +
                        "and this template's tint targets it");
            want.Remove(0);
        }

        return want;
    }

    /// <summary>
    /// The name each SECTION will end up with, for the preview -- the planned rename where there
    /// is one, otherwise what the mesh already calls that slot.
    ///
    /// The preview used to derive its own names with <see cref="SlotNameFor"/> alone, which read
    /// no `slotName` and applied no tie-break: two slots deriving one name both took the tint on
    /// screen and one took it in game.
    /// </summary>
    public static List<string?> PreviewSlotNames(
        IReadOnlyList<string> explicitNames,
        IReadOnlyList<string> namedMaterials,
        IReadOnlyList<string> templateNames,
        IReadOnlyList<string?> sectionMaterials)
    {
        var plan = PlanSlotNames(explicitNames, namedMaterials, templateNames);
        var names = new List<string?>();
        for (var i = 0; i < sectionMaterials.Count; i++)
        {
            if (plan.TryGetValue(i, out var planned)) { names.Add(planned); continue; }
            // No opinion: the slot keeps the name the mesh has. Past the template's end there is
            // no name of its own -- a grown slot is a CLONE of the first one -- so that is the
            // name it answers to, and the reason an added slot takes slot zero's tint.
            if (i < templateNames.Count) { names.Add(templateNames[i]); continue; }
            if (templateNames.Count > 0) { names.Add(templateNames[0]); continue; }
            names.Add(sectionMaterials[i] is { Length: > 0 } m ? SlotNameFor(m) : null);
        }
        return names;
    }

    /// <summary>
    /// `MI_Clo003_PACK` -> `MI_PACK`: the kit token out of the middle, which is what the game
    /// calls the slot that wears it. Anything not shaped that way is used as it stands -- a name
    /// that is merely long is still unique, and inventing one would be worse.
    ///
    /// Public because the PREVIEW has to name its sections the same way the build names its slots.
    /// If they disagree, the viewport paints a tint the game will not, which is the failure this
    /// whole area exists to stop.
    /// </summary>
    public static string SlotNameFor(string material)
    {
        var leaf = Leaf(material);
        var parts = leaf.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && (parts[0].Equals("MI", StringComparison.OrdinalIgnoreCase) ||
                                     parts[0].Equals("M", StringComparison.OrdinalIgnoreCase))
            ? $"{parts[0].ToUpperInvariant()}_{parts[^1]}"
            : leaf;
    }

    /// <summary>
    /// Centimetres per FBX unit, when the mod does not say. Measured against this game's own
    /// meshes rather than taken from the file, whose GlobalSettings claim 2.54 -- see
    /// <see cref="MeshImport.Basis.YUp"/>.
    /// </summary>
    public const float DefaultMeshScale = 102.186f;

    /// <summary>
    /// Author one package: read the template, rename it into the mod's folder, and point its mesh
    /// and icon at the ones the mod asks for.
    /// </summary>
    public static byte[]? Author(IFileProvider provider, PartsIndex.Part template, ModPart part,
                                 string newPackageName, string iconObjectPath, out string? why,
                                 Action<string>? log = null, string? meshOverride = null,
                                 string? ownTintMask = null,
                                 IReadOnlyDictionary<string, string>? regionMeshes = null,
                                 List<string>? warnings = null,
                                 IReadOnlyDictionary<string, string>? regionTintMasks = null,
                                 List<TintExpectation>? tintExpected = null)
    {
        // Assigned once, here, so every one of this method's two `return null` paths already
        // satisfies C#'s definite-assignment rule without repeating itself at each one; the tag
        // block below is the only place that overwrites it with something more specific than the
        // caller's own generic message.
        why = null;

        // TrySaveAsset decompresses out of the container, which is the same reader state a
        // package load walks -- so it belongs under the same gate as the LoadPackage calls a few
        // lines down, and did not have it. This is the shape the export race has taken every time
        // it has been found: not a missing lock, a SECOND path to the same unsafe thing that was
        // written without one. See AssetLoader.Gate.
        byte[]? raw = null;
        var got = AssetLoader.Read(() => provider.TrySaveAsset(ContainerPathFor(template.Path), out raw));
        if (!got || raw is null) return null;

        // The display name BEFORE anything else. It is an FText in the export data and replacing
        // it changes that export's length, so it is spliced against the template's own bytes while
        // the offsets still describe them. Renaming afterwards only moves the name batch, which
        // ZenPackage already accounts for.
        var exports = AssetLoader.Read(() =>
            provider.LoadPackage(AssetLoader.PackagePath(template.Path)).GetExports().ToArray());
        var detailForTags = PartDetails.ReadExports(exports, template.Path);
        if (!string.IsNullOrWhiteSpace(part.Display))
        {
            var renamed = DisplayNameEdit.SetDisplayName(raw, exports, part.Display, out var nameWhy);
            if (nameWhy is null) { raw = renamed; log?.Invoke($"      display name: \"{part.Display}\""); }
            else
            {
                // The template's DisplayName is EMPTY, which is true of about a third of the
                // game's parts -- they were never meant to appear in a menu, which is also why
                // they are the ones worth copying. There is no string to anchor on, so the
                // property is located by walking the export instead.
                if (PropertyAdd.Locate(provider, raw, template.Path, "BitReactorUIDataFragment",
                                       "DisplayName", out var dnAt, out var dnLen, out var dnWhy))
                {
                    var placed = DisplayNameEdit.SetTextAt(raw, dnAt, dnLen, part.Display, out var placeWhy);
                    if (placeWhy is null)
                    { raw = placed; log?.Invoke($"      display name: \"{part.Display}\" (written into an empty one)"); }
                    else Unnamed($"{nameWhy}; and {placeWhy}");
                }
                else Unnamed($"{nameWhy}; {dnWhy}");

                // A NAME THAT DID NOT GET WRITTEN IS A WARNING, not a log line.
                //
                // It was a log line until a user reported the verifier failing with `name in the
                // menu shows "CPD_Mod_..."` on one part out of 265. The build had said so -- in
                // the log, among hundreds of lines, phrased as "display name kept", which reads
                // like a decision rather than a failure. The only loud signal was a verifier
                // check most people never open.
                //
                // Both routes have to fail to get here: the template's DisplayName is empty AND
                // it has no BitReactorUIDataFragment to write one into. That is a property of the
                // TEMPLATE, so it hits some parts and not others -- which is exactly why it
                // looked intermittent to the person reporting it.
                void Unnamed(string why)
                {
                    log?.Invoke($"      display name kept: {why}");
                    warnings?.Add(
                        $"{part.Id}: it will show as \"{part.Id}\" in the menu, not " +
                        $"\"{part.Display}\" -- the template {Short(template.Path)} has no name " +
                        "to replace and nowhere to put one. Pick a different template, or accept " +
                        $"that the menu will read \"{part.Id}\". ({why})");
                }
            }
        }

        // The rest of the part's own words, spliced the same way and against the SAME exports --
        // they still hold the template's original values, which is exactly what each splice
        // searches for, and each one re-finds its anchor in the current bytes.
        foreach (var (property, value) in part.Text)
        {
            var written = DisplayNameEdit.SetText(raw, exports, property, value, out var textWhy);
            // Reported, never silent: an authored part that kept its template's description is
            // telling the player it IS the template, and the build would otherwise look clean.
            if (textWhy is not null) log?.Invoke($"      {property} kept: {textWhy}");
            else log?.Invoke($"      {property}: \"{Short(value)}\"");
            raw = written;
        }

        // hidesHead: WRITE the tag rather than hope a template carries it.
        //
        // Exactly one part in the game hides a character's face -- the mod the editor produced --
        // so template selection can never deliver this. The cooked part's recipe is three override
        // tags and a visor lock; the two hair tags come with any helmet template, and this is the
        // third.
        // NOT when the template was NAMED. Naming one means "duplicate this part", and a
        // duplicate that carries a tag the original does not is not a duplicate -- it is the
        // original plus a change nobody asked for, which is the whole class of bug this splice
        // turned out to be in. --diff reports exactly that, and for a clone helmet this tag was
        // the ONLY difference left once the mesh and the name are accounted for.
        //
        // The ranking path keeps it. There it is doing real work: the template was chosen by us,
        // it is usually a part that does not hide the face, and the build known to work in game
        // relies on the splice. Changing that would be trading a fault we have for one we have
        // not measured.
        // A NAMED TEMPLATE USED TO SKIP THIS, and that is how a helmet shipped with the wearer's
        // face showing through it.
        //
        // The reasoning above is still right about what it was written for: a duplicate carrying a
        // tag the original does not is not a duplicate. But it was applied one step too widely. It
        // guards against a change NOBODY ASKED FOR, and "hides the head" is a checkbox -- when it
        // is ticked, the author asked. Measured: `CPD_H_Outfit_Clo001_HELM_212th` overrides
        // `Hair.Hair.Mesh` and `Hair.Facial.Mesh` and nothing else, so Detail.HidesHead was true
        // on hair alone, the checkbox looked satisfied, the verifier reported Good, and the face
        // rendered through the visor in game.
        //
        // So the gate is what the author asked for and whether the part already does it, and the
        // template box no longer silences it. Untick the checkbox to get the faithful copy.
        //
        // AND ONLY IN A SLOT THAT CAN HIDE IT (ModProject.SlotCanHideHead). A Head part is the
        // face: overriding the face slot from inside it is a head hiding itself, and the
        // Devaronian heads arrived here ticked because they override the HAIR. This is also the
        // half of the fix that covers a project already saved with the tick -- EffectiveHidesHead
        // is false for it, so it builds like an unticked copy and matches the game's own part.
        if (part.HidesHead && !part.EffectiveHidesHead)
            log?.Invoke($"      'Hides the head' ignored: it is for outfit slots " +
                        $"({string.Join(", ", ModProject.Slots.Where(ModProject.SlotCanHideHead))}), " +
                        $"and this part is in {part.Slot}, so no face override was added.");
        if (part.EffectiveHidesHead && !detailForTags.OverrideSlots.Any(
                s => s.EndsWith(FaceSlotTag, StringComparison.Ordinal)))
        {
            var named = !string.IsNullOrWhiteSpace(part.Template);
            var withFace = GameplayTagEdit.Add(raw, detailForTags.OverrideSlots, FaceSlotTag, out var tagWhy);
            if (tagWhy is not null) log?.Invoke($"      face not hidden: {tagWhy}");
            else if (named)
                log?.Invoke($"      hides the face: added {FaceSlotTag}, which {part.Template} " +
                            $"does not carry -- it hides only the hair, so the face would have " +
                            $"shown through. Untick 'Hides the head' for a faithful copy.");
            else log?.Invoke($"      hides the face: added {FaceSlotTag}");
            raw = withFace;
        }

        // SLOT CLAIMS THE AUTHOR TURNED OFF -- takeovers and menu hides, in one place.
        //
        // A part claims another slot through THREE containers, and the claims are not the same
        // shape. Clone legs, read from the game:
        //
        //     OverrideSlots   ...Outfit.Boots.Mesh          swaps the boots for its own
        //     HiddenSlots     ...Outfit.Legs.Color.Visor    takes a colour swatch off the menu
        //     LockedSlots     ...Outfit.Boots               greys the boots row out
        //                     ...Outfit.Legs.Color.Visor    and the swatch with it
        //
        // So the LOCK TAG DIFFERS BY CASE: a taken-over slot is locked under the override tag
        // with its last segment removed, a hidden one under its own name. Undoing either without
        // its lock leaves a row that is greyed out for no reason -- which is worse than the
        // claim it was meant to remove.
        //
        // LockedSlots is therefore edited ONCE, from both sources. Two Set calls would mean the
        // second hunting for a pattern the first had just changed, which is the same rule the
        // gate tags below are written under. All of it before the rename pass, because a
        // container is located by the bytes of its current contents.
        var dropOver = part.TakesOver.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
        var dropHide = part.HidesFromMenu.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
        if (dropOver.Count > 0 || dropHide.Count > 0)
        {
            var lockTags = new List<string>();

            // ONE PATTERN, TWO CONTAINERS. A part that hides colour swatches usually LOCKS exactly
            // the same ones -- the clone commando helmet names Helmet.Color.Secondary and
            // Helmet.Color.Visor in HiddenSlots and again in LockedSlots -- so the two serialise
            // to identical bytes, and GameplayTagEdit refuses to guess between them. Correctly:
            // two containers reading the same thing are two statements.
            //
            // It is not a guess when the edit is the same for both, which is what handing a swatch
            // back is: out of HiddenSlots alone it comes back greyed out forever, out of
            // LockedSlots alone it stays invisible. Only when nothing else is being dropped,
            // because a takeover releases its lock under a DIFFERENT tag and the two containers
            // would then want different contents.
            var lockedSame = detailForTags.LockedSlots is { } lk &&
                             lk.SequenceEqual(detailForTags.HiddenSlots, StringComparer.Ordinal);
            if (dropOver.Count == 0 && dropHide.Count > 0 && lockedSame &&
                detailForTags.HiddenSlots.Count > 0)
            {
                var haveBoth = detailForTags.HiddenSlots;
                var goneBoth = dropHide
                    .Where(t => haveBoth.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                var wantBoth = haveBoth
                    .Where(t => !goneBoth.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();

                if (goneBoth.Count > 0)
                {
                    var written = GameplayTagEdit.SetEvery(raw, haveBoth, wantBoth, 2, out var bothWhy);
                    if (bothWhy is not null)
                        log?.Invoke($"      the swatch stays hidden: {bothWhy}");
                    else
                    {
                        raw = written;
                        log?.Invoke($"      HiddenSlots and LockedSlots now " +
                                    $"[{string.Join(", ", wantBoth.Select(SlotLeaf))}]");
                        log?.Invoke($"      {goneBoth.Count} colour swatch(es) back on the menu: " +
                                    string.Join(", ", goneBoth.Select(SlotLeaf)));
                    }
                    goto claimsDone;
                }
            }

            // Each container in turn: what it has, what the author dropped, what is left.
            var claims = new[]
            {
                (Name: "takes over", Have: (IReadOnlyList<string>) detailForTags.OverrideSlots,
                 Drop: dropOver, Locks: (Func<string, string>) (t =>
                     t.Contains('.') ? t[..t.LastIndexOf('.')] : t)),
                (Name: "hides from the menu", Have: (IReadOnlyList<string>) detailForTags.HiddenSlots,
                 Drop: dropHide, Locks: (Func<string, string>) (t => t)),
            };

            foreach (var (name, have, drop, locks) in claims)
            {
                if (drop.Count == 0) continue;

                var unknown = drop.Where(t => !have.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (unknown.Count > 0)
                    log?.Invoke($"      still {name}, the template does not claim it: " +
                                string.Join(", ", unknown.Select(SlotLeaf)));

                var want = have.Where(t => !drop.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (want.SequenceEqual(have, StringComparer.Ordinal)) continue;

                var written = GameplayTagEdit.Set(raw, have, want, out var claimWhy);
                if (claimWhy is not null) { log?.Invoke($"      still {name}: {claimWhy}"); continue; }

                raw = written;
                var undone = drop.Except(unknown, StringComparer.OrdinalIgnoreCase).ToList();
                log?.Invoke($"      no longer {name}: " + string.Join(", ", undone.Select(SlotLeaf)));
                // Only the claims that ACTUALLY came off release their lock. A container that
                // refused to be edited must keep its lock, or the menu offers a row the part is
                // still overriding.
                lockTags.AddRange(undone.Select(locks));
            }

            var haveLock = detailForTags.LockedSlots ?? [];
            var wantLock = haveLock.Where(t => !lockTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            // (the identical-containers case above jumps past both passes)
            if (haveLock.Count > 0 && !wantLock.SequenceEqual(haveLock, StringComparer.Ordinal))
            {
                var written = GameplayTagEdit.Set(raw, haveLock, wantLock, out var lockWhy);
                if (lockWhy is not null)
                    log?.Invoke($"      the slot stays LOCKED in the menu: {lockWhy}");
                else
                {
                    raw = written;
                    log?.Invoke($"      LockedSlots now [{string.Join(", ", wantLock.Select(SlotLeaf))}]");
                }
            }

            claimsDone: ;
        }

        // AND THE LOCK ON THE PART'S OWN SLOT, which is a fourth switch and not one of the three
        // containers above.
        //
        // `bLockOwningSlot` sits at the end of the same fragment and means: once the player picks
        // this, they cannot pick anything else here. 49 of the game's parts set it -- 44 named
        // voiceovers, both bespoke appearances, two torsos, Hawks' name -- and a mod built on one
        // inherits a dead slot with nothing anywhere saying why.
        //
        // Cleared BY SCHEMA rather than by pattern. It is a single byte with no distinctive
        // neighbour, and the template's containers have just been rewritten, so anything counting
        // from a landmark would be counting from one that has moved.
        if (!part.KeepsOwnSlotLock)
        {
            if (!detailForTags.LocksOwnSlot)
                log?.Invoke("      the template does not lock its own slot, so there is nothing to unlock");
            else
            {
                var unlocked = FlagEdit.ClearByName(provider, raw, template.Path,
                                                    "CustomizationFragmentOverrideSlots",
                                                    "bLockOwningSlot", out var ownWhy);
                if (ownWhy is not null) log?.Invoke($"      its own slot stays LOCKED: {ownWhy}");
                else
                {
                    raw = unlocked;
                    log?.Invoke("      its own slot is no longer locked: the player can change " +
                                "this again after picking it");
                }
            }
        }

        // Gate tags dropped BEFORE the rename pass, while the exports still describe these bytes.
        // Splicing a container after the name batch has moved would be looking for a pattern at a
        // stale offset.
        // Removals and a character restriction are the SAME edit to the same container, so they
        // are worked out together and written once. Two Set calls would mean the second one
        // hunting for a pattern the first had just changed.
        if (part.RemoveTags.Count > 0 || part.AddTags.Count > 0 || !string.IsNullOrWhiteSpace(part.ForCharacter))
        {
            var have = PartDetails.TagsIn(exports, "AllowedSlots");
            var missing = part.RemoveTags.Where(x => !have.Contains(x, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0)
                log?.Invoke($"      tag NOT removed, the template does not carry it: {string.Join(", ", missing)}");

            var want = have.Where(x => !part.RemoveTags.Contains(x, StringComparer.Ordinal)).ToList();

            if (!string.IsNullOrWhiteSpace(part.ForCharacter))
            {
                want = want.Where(x => !x.StartsWith(PartsIndex.NamePrefix, StringComparison.Ordinal))
                           .ToList();
                want.Add(PartsIndex.NamePrefix + part.ForCharacter);
            }

            // Tags the author ADDED go on last: a slot offers only parts that carry its own tag
            // (measured in game 2026-09-24), and nothing derived from the template may drop them.
            foreach (var t in part.AddTags)
                if (!want.Contains(t, StringComparer.Ordinal)) want.Add(t);

            if (!want.SequenceEqual(have, StringComparer.Ordinal))
            {
                var written = GameplayTagEdit.Set(raw, have, want, out var setWhy);
                if (setWhy is not null)
                {
                    // A REMOVAL or a character restriction that could not be written degrades the
                    // way it always has -- the part still builds, wearing its template's original
                    // gate, and the log says why (fails OPEN: a story-unlock tag left on is a part
                    // offered to fewer people, not a build that has to brick over a cosmetic gate).
                    // `addTags` is different: a slot offers a part ONLY when the part carries the
                    // slot's own tag (measured in game 2026-09-24), so asking for one and silently
                    // not getting it is a part that LOOKS tagged and is not offered -- the failure
                    // copies (see the copies loop's own addTags block) and overrides already Skip
                    // by name rather than log, and a part missing the same reasoning is why one
                    // built and shipped with `AllowedSlots` still reading exactly its template's.
                    if (part.AddTags.Count > 0) { why = setWhy; return null; }
                    log?.Invoke($"      tags unchanged: {setWhy}");
                }
                else
                {
                    raw = written;
                    log?.Invoke($"      AllowedSlots now [{string.Join(", ", want)}]");
                }
            }
        }

        var pkg = ZenPackage.Read(raw);

        var oldLeaf = Path.GetFileNameWithoutExtension(template.Path);
        var subs = new Dictionary<string, string>(StringComparer.Ordinal);

        // The package's own name appears twice: the full path, and the bare asset name. Both have
        // to move, and the FULL PATH has to become the mod's folder rather than keeping the
        // template's -- renaming only the leaf leaves the new part sitting in the game's own
        // directory, where the container has no chunk for it.
        subs[template.Path] = newPackageName;
        subs[oldLeaf] = part.Id;

        var detail = PartDetails.Read(provider, template.Path);

        // EVERY mesh option, not just the first. A part carries one entry per body type -- the
        // Mandalorian template has SK_HAAF_ (feminine) and SK_HAAM_ (masculine) -- and
        // Detail.MeshPath is only Meshes[0]. Substituting that alone left the other option
        // pointing at the template's mesh, so the icon showed Captain Rex and the character wore
        // a Mandalorian helmet: the mod looked installed and was wearing someone else's armour.
        //
        // A mod names ONE mesh, so every body type gets it. That matches what the cooked mod did.
        // meshOverride is the mod's OWN copy when it carries one, and part.Mesh -- the game's
        // asset -- when it does not.
        //
        // ...UNLESS THE PART AUTHORS ITS PIECES SEPARATELY. Then each mesh fragment is redirected
        // to the mesh named for THAT piece, and a fragment the author did not name resolves to its
        // own template mesh, which Reference drops as a no-op. The flat loop below is on its own
        // branch, so a part with no pieces needs no argument about equivalence with the piece
        // loop: it is the same Reference call it always made, behind the one test below.
        //
        // ...AND EXCEPT WHERE THE OPTIONS ARE KEYED PER HEAD AND THE AUTHOR KEPT THE GAME'S MESH.
        // An eyebrow or a beard has one option per head (29 and 25), each cut to its head, and a
        // plain Duplicate seeds the part with the FIRST option's mesh. Writing that into every
        // option gave every human head the Devaronian cut, to a modder who had only renamed it.
        // Then the options are left as the template has them; a mesh the author replaced (or a
        // copy of it the mod carries) is not the seed and still goes into every option. See
        // KeepsHeadCuts. Body-type options never qualify, so helmets and torsos are untouched.
        if (regionMeshes is null)
        {
            var wearing = meshOverride ?? part.Mesh;
            if (KeepsHeadCuts(detail.Meshes, wearing, detail.MeshPath))
                log?.Invoke($"      mesh: every head keeps its own cut ({detail.Meshes.Count} option(s) left as " +
                            "the template has them)");
            else
                foreach (var option in detail.Meshes)
                    Reference(subs, option.MeshPath, wearing, $"mesh [{option.Variant}]", log);
        }
        else
        {
            var regionKeys = PartDetails.RegionKeys(detail);
            var seeds = PartDetails.RegionMeshes(detail, detail.MeshPath);
            foreach (var group in detail.MeshFragments)
            {
                var key = regionKeys.FirstOrDefault(k => k.Fragment == group.Key).Key ?? "";
                if (!regionMeshes.TryGetValue(key, out var to) || string.IsNullOrWhiteSpace(to)) continue;
                if (KeepsHeadCuts(group, to, seeds.FirstOrDefault(x => x.Region == key).Mesh))
                {
                    log?.Invoke($"      mesh {key}: every head keeps its own cut ({group.Count()} option(s) left as " +
                                "the template has them)");
                    continue;
                }
                foreach (var option in group)
                    Reference(subs, option.MeshPath, to, $"mesh {key} [{option.Variant}]", log);
            }
        }
        Reference(subs, detail.IconPath, iconObjectPath, "icon", log);
        Reference(subs, detail.AnimClassPath, part.AnimClass, "anim class", log);

        // Ability swaps. Each side is resolved from a bare asset name to its object path, because
        // asking an author to write out
        // /Game/Game/GameData/Abilities/Assault/GA_FleetOfFoot_T1.GA_FleetOfFoot_T1_C
        // is asking them to get three things right that the index already knows.
        foreach (var (from, to) in part.Abilities)
        {
            var fromPath = AbilityPath(provider, from, out var fromWhy);
            var toPath = AbilityPath(provider, to, out var toWhy);
            // Reported, never skipped silently: an ability swap that did not happen leaves a
            // specialisation that builds, installs, and quietly has the template's powers.
            if (fromPath is null) { log?.Invoke($"      ability '{from}' NOT SWAPPED: {fromWhy}"); continue; }
            if (toPath is null) { log?.Invoke($"      ability '{to}' NOT SWAPPED: {toWhy}"); continue; }
            Reference(subs, fromPath, toPath, "ability", log);
        }

        // THE TINT MASK, when the part names one of its own.
        //
        // The mask comes from the template's colour fragment, and a template drawn for one armour
        // kit masks the wrong panels on another -- a Clo010 commando torso built from a Clo001
        // template gets Clo001's mask sampled with Clo010's UVs, and the colours land in the wrong
        // places. Both are the game's own textures, so this is a name substitution and nothing is
        // copied.
        //
        // THE AUTHOR'S OWN MASK IS USED WITHOUT BEING ASKED FOR, when they imported one.
        //
        // A tint variant is an ordinary part carrying two fragments: a MaterialColor with a colour
        // and a parameter name, and a MaterialTexture that overrides "Tint Mask VT" with THAT
        // variant's mask. So a part built from a variant paints the template's colour through the
        // TEMPLATE's mask -- over the author's artwork -- and their own imported Tint texture,
        // which the repaint correctly wrote into the material, is never sampled.
        //
        // Redirecting it needs the path of a copy the BUILD generated, which is not something an
        // author could type. So when they imported a Tint and named no mask of their own, theirs
        // is the one that is meant.
        var tintWanted = !string.IsNullOrWhiteSpace(part.TintMask)
            ? PackageOf(part.TintMask) ?? part.TintMask
            : ownTintMask;

        if (!string.IsNullOrWhiteSpace(tintWanted))
        {
            var masks = detail.TintMasks ?? [];
            if (masks.Count == 0 && !string.IsNullOrWhiteSpace(part.TintMask))
                log?.Invoke("      tint mask NOT SET: this template paints no colours, so there " +
                            "is no mask reference to redirect");

            // EACH PIECE KEEPS ITS OWN MASK.
            //
            // A garment that dresses several pieces carries one tint-mask fragment PER PIECE --
            // measured on CPD_H_Outfit_Clo001_LEGS_TintA_MSA_LB: T_Clo001_BOOT_a_Tint on MI_BOOT
            // and T_Clo001_LEGS_a_Tint on MI_LEGS. Redirecting every one of them to a single mask
            // painted the boot through the LEGS' mask: the legs' UV layout sampled with the boot's
            // UVs, which is a boot tinted in the wrong places. Reported by a modder making legs and
            // boots as one asset ("tint of the 1st piece applied to the 2nd"), and the new
            // Studio's per-piece texture rows could already write a Tint image for each piece.
            //
            // So when the part authors its pieces and the template's masks are for MORE THAN ONE
            // piece, each template mask is redirected to the mask of the piece it is FOR (see
            // PartDetails.TintPieceOf: the slot the fragment paints, else the mask's own name).
            //   - a piece that made no mask keeps the template's own, which is the game's mask for
            //     that piece and fits the game's mesh;
            //   - a template mask that cannot be given to one piece is left alone, and that is a
            //     WARNING: it decides what an authored Tint does.
            //
            // Not when the part NAMES a `tintMask`: that is the author choosing one mask for the
            // lot, deliberately, and it is honoured exactly as before. Not for a template whose
            // masks are all for ONE piece -- a helmet with two masks for its two slots has nothing
            // to mix up -- nor when no mask of the template can be matched to a piece the author
            // made a mask for (a piece keyed by a mesh name, say): there the old single mask is
            // still the better guess than dropping the author's artwork. Every earlier build of
            // those comes out byte-for-byte as it did.
            var maskPieces = masks.ToDictionary(m => m, m => PartDetails.TintPieceOf(detail, m),
                                                StringComparer.Ordinal);
            var pieceTokens = maskPieces.Values.Where(v => v.Length > 0).Distinct().ToList();
            var candidate = regionTintMasks is { Count: > 0 } &&
                            string.IsNullOrWhiteSpace(part.TintMask) && pieceTokens.Count > 1;

            // A TOKEN THAT TWO MESH FRAGMENTS CARRY IS NOT A PIECE KEY. RegionKeys keys such a
            // fragment by its mesh name instead, so the author's piece for it is filed under a
            // name no mask resolves to, and matching masks to pieces by token would call a piece
            // that DID make a mask one that made none -- and leave its artwork unused. A template
            // like that cannot have its masks matched to pieces, so it keeps the one-mask path.
            var contested = PartDetails.ContestedRegionTokens(detail);
            var contestedTokens = pieceTokens.Where(contested.Contains).ToList();
            if (candidate && contestedTokens.Count > 0)
                log?.Invoke($"      tint masks: {Leaf(template.Path)} cuts its {string.Join(", ", contestedTokens)} " +
                            "piece from more than one mesh, so a mask cannot be matched to one piece; " +
                            "every mask goes to the part's one mask, as before");

            var perPiece = candidate && contestedTokens.Count == 0 &&
                           maskPieces.Values.Any(v => v.Length > 0 && regionTintMasks!.ContainsKey(v));
            if (perPiece)
            {
                var pieceKeys = PartDetails.RegionKeys(detail);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var leftAlone = new List<string>();
                var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var have in masks)
                {
                    var piece = maskPieces[have];
                    if (piece.Length == 0)
                    {
                        // A WARNING, not a log line: this decides what an authored Tint does. The
                        // mask keeps painting through the game's own texture, so the Tint the
                        // author imported may not land where they drew it, and the build is
                        // otherwise clean.
                        leftAlone.Add(have);
                        log?.Invoke($"      tint mask {Leaf(have)} LEFT ALONE: it cannot be told which piece it is for");
                        warnings?.Add($"{part.Id}: the tint mask {Leaf(have)} of {Leaf(template.Path)} was left " +
                                      "alone -- it cannot be told which piece it is for, and giving it to the " +
                                      "wrong one would tint that piece through another piece's mask. It still " +
                                      "paints through the game's own texture, not through an imported Tint.");
                        continue;
                    }
                    if (!regionTintMasks!.TryGetValue(piece, out var own) || string.IsNullOrWhiteSpace(own))
                    {
                        log?.Invoke(pieceKeys.Any(k => string.Equals(k.Key, piece, StringComparison.OrdinalIgnoreCase))
                            ? $"      tint mask {Leaf(have)} kept: the {piece} piece made no tint mask of its own"
                            : $"      tint mask {Leaf(have)} kept: this template has no {piece} piece to make one");
                        continue;
                    }
                    Reference(subs, have, own, $"tint mask ({piece} piece's own)", log);
                    used.Add(piece);
                    substituted[have] = own;
                }

                // WHAT WAS DONE TO EACH FRAGMENT, in the template's own order, for the verifier to
                // read the finished part back against.
                if (tintExpected is not null)
                    foreach (var fragment in detail.TintFragments ?? [])
                    {
                        var did = substituted.TryGetValue(fragment.Mask, out var gave);
                        tintExpected.Add(new TintExpectation(
                            maskPieces.TryGetValue(fragment.Mask, out var forPiece) ? forPiece : "",
                            did ? gave! : fragment.Mask, did));
                    }

                // A piece's artwork that no mask of the template is aimed at does nothing, and the
                // build otherwise looks clean.
                foreach (var (piece, own) in regionTintMasks!)
                {
                    if (piece.Length == 0 || used.Contains(piece)) continue;
                    warnings?.Add(leftAlone.Count == 0
                        ? $"{part.Id}: the {piece} piece imported a Tint, but no tint mask of " +
                          $"{Leaf(template.Path)} is for that piece, so {Leaf(own)} is not used"
                        : $"{part.Id}: the {piece} piece imported a Tint, but none of the tint masks of " +
                          $"{Leaf(template.Path)} could be matched to that piece ({leftAlone.Count} could " +
                          $"not be matched to any piece and were left alone), so {Leaf(own)} is not used");
                }
            }
            else
                foreach (var have in masks)
                    Reference(subs, have, tintWanted!,
                              string.IsNullOrWhiteSpace(part.TintMask)
                                  ? "tint mask (your own imported one)" : "tint mask", log);
        }

        // The author's own substitutions LAST, so a deliberate rename wins over anything derived.
        foreach (var (from, to) in part.RenameNames)
        {
            if (!pkg.Names.Contains(from, StringComparer.Ordinal))
            { log?.Invoke($"      rename '{from}' SKIPPED: not in the template's name map"); continue; }
            subs[from] = to;
            log?.Invoke($"      rename: {from} -> {to}");
        }

        pkg.RenameNames(subs);
        return pkg.Write();
    }

    /// <summary>
    /// A soft object reference is stored as TWO names -- the package path and the asset name --
    /// so redirecting one means substituting both halves. Substituting only the path leaves the
    /// old asset name behind and the reference resolves to nothing.
    /// </summary>
    /// <summary>
    /// The object path of an ability, from its bare asset name.
    ///
    /// Ambiguity is refused rather than resolved by picking the first: two abilities sharing a leaf
    /// name is exactly the case where a silent choice swaps the wrong power into somebody's
    /// specialisation, and the failure would show up as "this ability does the wrong thing" long
    /// after the build that caused it.
    /// </summary>
    public static string? AbilityPath(IFileProvider provider, string name, out string? why)
    {
        why = null;
        // Already a full object path -- taken as written, which is the escape hatch for a name
        // this cannot resolve unambiguously.
        if (name.Contains('/')) return name;

        var want = "/" + name + ".uasset";
        var hits = provider.Files.Keys
            .Where(k => k.EndsWith(want, StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (hits.Count == 0) { why = "no asset of that name in the game"; return null; }
        if (hits.Count > 1)
        {
            why = $"{hits.Count} assets share that name -- write the full object path instead " +
                  $"(e.g. {PackageId.PackageNameFor(hits[0], ProjectName)}.{name}_C)";
            return null;
        }

        var pkg = PackageId.PackageNameFor(hits[0], ProjectName);
        if (pkg is null) { why = $"could not derive a package name from {hits[0]}"; return null; }
        // A Blueprint asset generates a class named with a _C suffix, and the ability lists
        // reference the CLASS, not the asset.
        return $"{pkg}.{name}_C";
    }

    internal static void Reference(Dictionary<string, string> subs, string? from, string to,
                                  string what, Action<string>? log)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;

        static (string Package, string Leaf) Split(string objectPath)
        {
            var pkg = objectPath.Contains('.') ? objectPath[..objectPath.IndexOf('.')] : objectPath;
            return (pkg, pkg[(pkg.LastIndexOf('/') + 1)..]);
        }

        var (oldPkg, oldLeaf) = Split(from);
        var (newPkg, newLeaf) = Split(to);
        if (oldPkg == newPkg) return;

        subs[oldPkg] = newPkg;
        if (oldLeaf != newLeaf)
        {
            subs[oldLeaf] = newLeaf;
            // A Blueprint generated class is a THIRD name -- the asset is `Foo`, the class it
            // generates is `Foo_C` -- and it does not match the bare leaf, so an anim class
            // substituted without this keeps pointing at the template's blueprint.
            subs[oldLeaf + "_C"] = newLeaf + "_C";
        }
        log?.Invoke($"      {what}: {oldPkg} -> {newPkg}");
    }

    /// <summary>
    /// Ship as a Game Feature plugin instead of as a pak that replaces the game's asset registry.
    ///
    /// The two differ in exactly one thing that matters: what goes in the `.pak`. The classic form
    /// writes the game's whole registry with the mod's entries added, at a path every mod writes,
    /// so two mods clobber each other. The plugin form writes a `.uplugin` and a registry
    /// containing the mod's OWN entries and nothing else, which the mod runtime appends. Two mods
    /// that each add a part then never touch each other.
    ///
    /// The CONTENT is identical either way and stays at its proven package paths. That is
    /// deliberate: this changed one variable against a build known to work in game, so when the
    /// plugin did not appear the reason was the delivery route and not the assets.
    ///
    /// ON BY DEFAULT since the plugin folder was confirmed in game. The classic form is reachable
    /// with `--classic` and is there to reproduce an old build, not to ship one: two mods built
    /// that way cannot coexist, and no amount of care by either author fixes that, because the
    /// collision is between a mod and a mod it has never heard of.
    /// </summary>
    public static bool AsPlugin = true;

    /// <param name="paksDirectory">
    /// Where the game's containers live, so the clash check can read the INSTALLED mods off disk.
    /// Optional, and null is the old behaviour: the check then sees only what the caller mounted,
    /// which for the Studio is the game and nothing else -- so the warning below could never
    /// fire. Measured 2026-09-20.
    /// </param>
    public static Result Build(IFileProvider provider, ModProject mod, string gameRegistryPath,
                               string outDir, string containerName, Action<string>? log = null,
                               string? paksDirectory = null)
    {
        Directory.CreateDirectory(outDir);
        var parts = PartsIndex.Read(provider);

        // The registry is read ONCE, by both readers, and paired by index -- the same arrangement
        // --regmerge uses. Neither reader alone is enough: the record walk carries the class
        // PACKAGE, which CUE4Parse drops, and CUE4Parse carries the decoded tag values, which the
        // walk gives only as handles into the value store.
        var registryBytes = File.ReadAllBytes(gameRegistryPath);
        var registry = RegistryDocument.Read(registryBytes);
        var records = RegistryAssets.Walk(registry);
        var decoded = RegistryFile.Read(registryBytes, Path.GetFileName(gameRegistryPath));
        if (decoded.PreallocatedAssetDataBuffers.Length != records.Count)
            throw new InvalidOperationException("the two registry readers disagree about the asset count");

        // The mod's OWN entries, kept alongside the merged ones so either can be shipped. Built
        // even when the classic form is being written, because it costs a few hundred bytes and
        // having both means the choice is made at the end rather than threaded through the build.
        var additive = RegistryDocument.EmptyLike(registry);
        void Register(RegistryAssets.NewAsset a)
        {
            RegistryAssets.Append(registry, a);
            RegistryAssets.Append(additive, a);
        }

        var templateEntry = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < records.Count; i++)
            templateEntry[registry.Names[(int) records[i].PackageName]] = i;

        // ENEMY STAT ADJUSTMENTS, expanded into copies and an override BEFORE anything reads
        // either list.
        //
        // Expanded here rather than built into the file, because the file stores the author's
        // MULTIPLIERS -- see ModEnemyStats for why. Expanding into the existing lists rather than
        // writing a second build path is the whole trick: a scaled group is exactly a set of table
        // copies plus a spec override that re-points at them, which is a thing this builder has
        // done since the custom weapon classes, and every check below applies to it unchanged.
        //
        // In memory only. `mod` is not saved from here, so the project on disk keeps saying x0.5.
        ExpandEnemyStats(provider, mod, log);

        var store = StoreEntries(provider);
        log?.Invoke($"  store entries: {store.Count:N0} package(s) across the mounted containers");

        // ONLY WHEN THE MOD ACTUALLY WRITES THERE. An override ships at the GAME's package path,
        // so a mod made only of overrides never touches its own folder and the rule cannot apply
        // to it -- it was refused anyway, with a message about a part it does not have.
        var folderWhy = mod.ShipsIntoItsOwnFolder ? ModProject.FolderProblem(mod.Folder) : null;
        if (folderWhy is not null) throw new InvalidOperationException(folderWhy);
        var modFolder = FolderOf(mod);

        // Source assets that more than one part copies. Worked out before anything is written --
        // see SharedSources -- because a part cannot tell on its own that another part is about to
        // land on top of it.
        var sharedSources = SharedSources(mod);

        if (!modFolder.Equals(ModFolder, StringComparison.OrdinalIgnoreCase))
            log?.Invoke($"  folder: /Game/Game/{mod.Folder}");

        var built = new List<BuiltPart>();
        var storeFor = new Dictionary<ulong, ContainerHeader.StoreEntry>();
        var skipped = new List<string>();

        // OVERRIDES DROPPED BECAUSE NOTHING WAS ASKED OF THEM. Only read by the "empty mod"
        // refusal, so that it can say which -- see there.
        var notAsked = new List<string>();

        // Every actor this build adds, so the read-back can find them. See Result.Spawned.
        var spawned = new List<Spawned>();
        var warnings = new List<string>();

        // NOTES THAT MEAN THE MOD IS NOT WHAT THE AUTHOR DESIGNED, kept apart from the rest -- see
        // Result.Clashes. Everything here goes into `warnings` too, so nothing is lost to a caller
        // that only reads those; the separation is so the panel can say these out loud.
        var serious = new List<string>();

        // SLOT SETS: where each lands. Unknown keys and unknown targets REFUSE THE BUILD BY NAME --
        // through Skip, not `serious`. `serious` only ever reaches `warnings`/Result.Clashes, and
        // neither gates a clean build: ModVerify's own Bad check is "nothing was skipped"
        // (built.Skipped), and ModPanel/Install has its own `r.Skipped.Count == 0` gate beside it.
        // A note nobody enforces is not a refusal, whatever it says.
        var slotLandings = SlotSets.Resolve(mod, out var slotProblems);
        foreach (var problem in slotProblems) Skip(problem);

        // A slot's default may name one of THIS MOD's parts or copies BY ID, so the author never
        // has to know the object path the build writes them to -- see SlotSets.ResolveDefaults.
        // Built from mod.Parts and mod.Copies with the SAME PackageNameOf call their own loops use
        // below to name their packages, so this map can never disagree with where the rest of the
        // build actually lands them. Resolved before any of the three loops below stamps a single
        // slot, so a stamped Default is always the resolved path, never the id.
        var objectPathById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in mod.Parts.Where(p => !string.IsNullOrWhiteSpace(p.Id)))
            objectPathById[p.Id] = $"{PackageNameOf(p.Id, modFolder)}.{p.Id}";
        foreach (var c in mod.Copies.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
            objectPathById[c.Id] = $"{PackageNameOf(c.Id, modFolder)}.{c.Id}";
        var defaultProblems = new List<string>();
        SlotSets.ResolveDefaults(slotLandings, objectPathById, defaultProblems);
        foreach (var problem in defaultProblems) Skip(problem);

        // I-3: where each slot set ACTUALLY landed, one SlotStamp per successful SlotAdd.Add call in
        // the three loops below -- see Result.SlotStamps.
        var slotStamps = new List<SlotStamp>();
        var slotOverrides = new List<ModOverride>();
        foreach (var landing in slotLandings.Where(l => l.IsGamePart))
        {
            var own = mod.Overrides.FirstOrDefault(o => o.Target.Equals(landing.Target, StringComparison.OrdinalIgnoreCase));
            if (own is not null) own.AddSlots = landing.Slots;
            else slotOverrides.Add(new ModOverride { Target = landing.Target, AddSlots = landing.Slots });
        }
        foreach (var ov in mod.Overrides.Where(o => !slotLandings.Any(l => l.IsGamePart && l.Target.Equals(o.Target, StringComparison.OrdinalIgnoreCase))))
            ov.AddSlots = new();   // a previous build's slots must not linger on a reused project object

        // The copies the loose check found, kept for the caller. See Result.LooseCopies.
        var looseCopies = new List<string>();

        // A COPY THE GAME FINDS FOR ITSELF, which nothing in a project ever points at and which
        // the loose check therefore called unused.
        //
        // The principle was already written down for character features -- "pointed at by nothing
        // ON PURPOSE, the Armoury finds a weapon class by scanning the registry rather than by
        // being told" -- and then applied only to those. It is the same for everything the game
        // discovers on its own:
        //
        //   GK_ / CPD_GK_          a gear kit, reached from the Armoury's shop tables
        //   CPD_WeaponSpec_        a weapon spec, matched by the tag it carries
        //   Modification_          an attachment, named by a shop slot
        //
        // MEASURED on the author's own shelf, 2026-09-09: E5_Blaster's two gear kits, DLT20A2's
        // CPD_GK_Longarm_DLT20A, Enemy_Recruits' three droid weapon specs and
        // ModProbeCustomizations' Modification_Barrel_ProbeBeta were all reported unused, all
        // wrongly. E5_Blaster's two were deleted on that advice and had to be restored from an
        // older snapshot -- which is why this is a build-side fix and not a wording change: the
        // warning is what the button acts on.
        static bool FoundByTheGame(string id) =>
            id.StartsWith("GK_", StringComparison.Ordinal) ||
            id.StartsWith("CPD_GK_", StringComparison.Ordinal) ||
            id.StartsWith("CPD_WeaponSpec_", StringComparison.Ordinal) ||
            id.StartsWith("Modification_", StringComparison.Ordinal) ||
            // A HIRING POOL TABLE IS FOUND BY SCANNING, exactly like the three above. Nothing in
            // the project points at it and nothing ever will -- DR_InitialCharacterPool's sources
            // SEARCH the customizations tree by name pattern. Without this the warning fires on
            // every recruit mod the Studio has ever written, and a warning that always trips on a
            // correct default is one people learn to read past.
            CharacterAuthor.IsPoolTable(id);

        // TWO PARTS WEARING THE SAME IMAGE UNDER DIFFERENT NAMES.
        //
        // Almost always a mistake, and a silent one: an author exports a second helmet's normal
        // map, saves it over the wrong name or never saves it at all, and ships a part wearing
        // another part's artwork. It looks like the tool crossed the textures -- the build, the
        // container and the material all read correct, because they ARE correct. The two files
        // simply hold the same bytes.
        //
        // Cost an evening. The diffuse and the mask differed between two helmets and displayed
        // correctly; the normal and the tint were byte-identical and one helmet appeared to be
        // wearing the other's. Distinct NAMES are why nothing else noticed.
        {
            var byContent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            // EVERY material list a part owns: its own AND its pieces'. A piece's artwork was
            // exempt from the one check that catches two parts shipping byte-identical files under
            // different names -- and a five-piece garment is five times the chance of making that
            // mistake, not less.
            static IEnumerable<ModMaterial> AllPaint(ModPart part)
            {
                foreach (var m in part.Materials) yield return m;
                foreach (var r in part.Regions)
                    foreach (var m in r.Materials) yield return m;
            }

            foreach (var part in mod.Parts)
            foreach (var paintSlot in AllPaint(part))
            foreach (var (key, file) in paintSlot.Textures)
            {
                if (string.IsNullOrWhiteSpace(file) || file.Contains('/')) continue;
                var path = FindImage(file, mod.FilePath);
                if (path is null || !File.Exists(path)) continue;
                try
                {
                    using var stream = File.OpenRead(path);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    if (!byContent.TryGetValue(hash, out var users)) byContent[hash] = users = [];
                    users.Add($"{part.Id} [{key}] = {file}");
                }
                catch { /* an unreadable image is already reported by the repaint */ }
            }

            foreach (var shared in byContent.Values.Where(v => v.Count > 1))
            {
                var names = shared.Select(x => x[(x.LastIndexOf('=') + 2)..]).Distinct().ToList();
                if (names.Count < 2) continue;   // one file used twice on purpose is fine
                serious.Add("these images are BYTE FOR BYTE IDENTICAL although they are different " +
                            $"files -- {string.Join("; ", shared)}. One of them is almost certainly " +
                            "not the artwork you meant to export, and the part wearing it will show " +
                            "the other one's.");
            }
        }





        var chunks = new List<IoStoreWriter.Chunk>();

        // The tags this mod ships: the ones declared outright, and one per icon of its own --
        // an icon IS a tag the mod defines, and asking the author to list it twice is a way to
        // get a mod that works on the author's machine only.
        var shipTags = ShipTags(mod);

        // A TAG RENAMED TO SOMETHING THE MOD NEVER DECLARED.
        //
        // Renaming a tag is how a mod does two opposite things: HIDE a part, by renaming its tag to
        // something nothing matches, and MINT one, by renaming it to a tag of the mod's own. The
        // two are identical in the project file and differ only in whether `tags` declares the
        // result -- so an author who meant to mint one and forgot gets a mod that works on their
        // machine and on nobody else's, which is the worst way for this to fail because the person
        // who can see the fault is not the person who can fix it.
        {
            var declaredTags = new HashSet<string>(shipTags, StringComparer.Ordinal);

            // A tag ANOTHER INSTALLED MOD declares is declared. The custom weapon classes are the
            // case that matters: a weapon routed into one renames its class tag to a tag the BASE
            // mod owns, and asking this mod to declare it too would have two mods minting one tag
            // -- which is the collision the single canonical base mod exists to avoid. So a tag is
            // only "undeclared" when nothing mounted defines it either.
            foreach (var w in WeaponAuthor.MirroredClasses)
                if (WeaponAuthor.CustomClassTag(w) is { } ct &&
                    provider.Files.Keys.Any(k => k.EndsWith(
                        $"/CPD_WeaponSpec_Custom_{w}.uasset", StringComparison.OrdinalIgnoreCase)))
                    declaredTags.Add(ct);

            // AND A TAG THE GAME ITSELF DECLARES IS DECLARED, which this did not know.
            //
            // The check only ever held the tags the MOD ships, so it fired on every `br.*` in the
            // game as well -- the E-5's own weapon type, the DC-15A's, every slot and species. A
            // mod naming one of those is doing the ordinary correct thing. Measured on the E-5:
            // "'E-5' looks like a gameplay tag and this mod does not declare it" sat in the build
            // log beside the real fault and was read as noise, which it was.
            //
            // An EMPTY set means the ini files could not be read, not that the game declares
            // nothing -- warning on everything because a file would not open is the same false
            // alarm wearing a different hat, so the whole check stands down.
            var gameCatalogue = GameplayTagIndex.Of(provider);
            var gameTags = gameCatalogue.Names;

            var undeclared = new List<string>();
            var wasRenamed = new List<string>();
            if (gameTags.Count > 0)
                foreach (var map in mod.Parts.Select(p => p.RenameNames)
                             .Concat(mod.Copies.Select(c => c.RenameNames))
                             .Concat(mod.Overrides.Select(o => o.RenameNames)))
                    foreach (var to in map.Values)
                        // A SLOT is the game's vocabulary, declared in its code, not in an ini:
                        // br.Customization.Slot.Character.Specializations.Tactical.Primary sits in
                        // the AllowedSlots of every hero tactical spec and appears in no ini, so the
                        // index cannot know it (2026-09-07). A mod cannot mint a slot, so a rename
                        // to one is never the fault this warning exists for.
                        if (LooksLikeTag(to) && !declaredTags.Contains(to) &&
                            !gameTags.Contains(to) && !undeclared.Contains(to) &&
                            !to.StartsWith("br.Customization.Slot.", StringComparison.Ordinal))
                            undeclared.Add(to);
                        // AND THE ONES THE GAME HAS SINCE RENAMED. These resolve, because the game
                        // ships a redirect for each -- which is why they are not warnings -- but an
                        // author who reached one from an old mod or an old forum post is naming a
                        // tag on its way out, and nothing else would ever tell them so.
                        else if (gameCatalogue.Renamed.ContainsKey(to) && !wasRenamed.Contains(to))
                            wasRenamed.Add(to);

            foreach (var t in wasRenamed)
                log?.Invoke($"   note: '{Leaf(t)}' is a tag the game has RENAMED to " +
                            $"'{Leaf(gameCatalogue.Renamed[t])}'. It still resolves -- the game " +
                            "ships a redirect for it -- so nothing is broken, but the new name is " +
                            "the one to move to.");

            foreach (var t in undeclared)
                warnings.Add($"'{Leaf(t)}' looks like a gameplay tag, and neither this mod nor the " +
                             "game declares it. To HIDE something that is correct -- nothing " +
                             "matches it, which is the point. To give this mod a tag of its OWN, " +
                             "put it in \"tags\" or it will not exist on anyone else's machine.");
        }

        // A REFERENCE IS A PACKAGE NAME, NEVER A FILE PATH, and until this pass existed nothing
        // said so.
        //
        // An asset has two names for one thing. In the container it is
        // `SWZeroCompany/Content/Game/Weapons/.../SK_Rifle_E-5_Base_01`; inside a package it is
        // `/Game/Game/Weapons/.../SK_Rifle_E-5_Base_01`. The Choose button in the contents list
        // handed over the first for months, which every check here happily accepted: the name
        // being replaced was present, the replacement survived the rename, the mod built and
        // installed. The engine then resolved it to nothing, and a weapon whose base, barrel,
        // stock and cell had all been re-pointed this way rendered as a bare foregrip tube.
        //
        // Repaired rather than refused. The projects already carrying the broken form are the
        // author's own, and a build that stops with "your file is wrong" would leave every one of
        // them to be hand-edited. Loudly, though -- silence is how this got here.
        {
            var repaired = new List<string>();
            foreach (var map in mod.Parts.Select(p => p.RenameNames)
                         .Concat(mod.Copies.Select(c => c.RenameNames))
                         .Concat(mod.Overrides.Select(o => o.RenameNames)))
                foreach (var key in map.Keys.ToList())
                {
                    var to = map[key];
                    // Only a cooked file path: it names a content root and does not start at one.
                    if (to.StartsWith('/') ||
                        !to.Contains("/Content/", StringComparison.OrdinalIgnoreCase)) continue;

                    var asPackage = PackageId.PackageNameFor(to + ".uasset", ProjectName);
                    if (asPackage is null)
                    {
                        warnings.Add($"'{Leaf(key)}' is pointed at '{to}', which is neither a " +
                                     "package name nor a file under a content root. A reference " +
                                     "has to read /Game/Game/... -- the form shown beside every " +
                                     "row in Contents. This one will resolve to nothing in game.");
                        continue;
                    }
                    map[key] = asPackage;
                    repaired.Add($"{Leaf(key)} -> {asPackage}");
                }

            foreach (var r in repaired)
                warnings.Add($"{r} was written as a FILE path and has been read as the package it " +
                             "names. Older builds shipped it verbatim, and the engine resolved it " +
                             "to nothing -- so if this mod looked wrong in game before, this is why.");
        }

        // AN ASSET NAME THE GAME ALREADY USES IS AN ID CLAIMED TWICE.
        //
        // A PrimaryAssetId is (Type, Name) -- the package PATH is not part of it. So a mod part
        // called CD_Rifle_E-5 at /Game/Game/Customizations/Mods/Mine/ and the game's own
        // CD_Rifle_E-5 at /Game/Game/Customizations/Weapons/ are ONE id, claimed by two assets,
        // and the asset manager keeps one of them. The mod installs, mounts, registers and
        // verifies perfectly, and in game the weapon renders as the customization system's
        // no-match fallback -- for a Base slot that is the RPS-6 rocket shell, which is how this
        // was finally found.
        //
        // A warning rather than a refusal: the collision may be exactly what an author wants when
        // they are deliberately replacing something. But it must be SAID, because nothing else in
        // the build or the game says it.
        {
            // TWO SOURCES, TWO REMEDIES. The first version of this check read every mounted file
            // and called them all "the game", which is wrong the moment another mod is installed
            // -- and being wrong there is worse than being silent, because it sends an author
            // looking through the game for an asset the game does not have. Measured: a weapon
            // whose gear kit was correct fired the wrong sound because a stale mod shipped the
            // same two names, and the build said the game shipped them.
            // ONE IMPLEMENTATION OF ONE QUESTION. CharacterAuthor.Create asks it too, to avoid
            // minting a name that trips this warning, and two copies of "is this name the
            // game's?" drift.
            var gameNames = AssetCopy.GameAssetNames(provider);

            // This mod's OWN previously-installed container is not a clash with anybody: it is the
            // last build of the thing being built, and every rebuild would otherwise warn on every
            // asset it has.
            var mine = containerName.EndsWith("_P", StringComparison.OrdinalIgnoreCase)
                ? containerName[..^2] : containerName;
            var modNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (from, key) in AssetCopy.ModFiles(provider))
            {
                if (!key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(from, mine, StringComparison.OrdinalIgnoreCase)) continue;
                modNames.TryAdd(Path.GetFileNameWithoutExtension(key), from);
            }

            foreach (var id in mod.Parts.Select(p => p.Id)
                         .Concat(mod.Copies.Select(c => c.Id))
                         .Concat(mod.Specs.Select(s => s.Id))
                         .Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                if (gameNames.Contains(id))
                    warnings.Add($"'{id}' is the name of an asset the game already ships. A " +
                                 "PrimaryAssetId is the NAME, not the path, so both are one id " +
                                 "and the game keeps only one of them -- yours may simply never " +
                                 "be found. Give it a name of its own unless you mean to replace " +
                                 "the game's.");
                else if (modNames.TryGetValue(id, out var other))
                    warnings.Add($"'{id}' is also shipped by the installed mod '{other}'. A " +
                                 "PrimaryAssetId is the NAME, not the path, so the two are one id " +
                                 "and only one of them is used -- which is why a weapon can look " +
                                 $"right and behave like the other mod's. Uninstall '{other}', or " +
                                 "give this one a name of its own.");
            }
        }

        // Every refusal is reported where it happens. A part that quietly falls out of the
        // loop leaves a mod that builds and is missing a piece, with nothing saying which.
        void Skip(string why) { skipped.Add(why); log?.Invoke("      SKIPPED " + why); }

        List<ImageBanks.Entry>? iconBanks = null;

        // ONE REPAINT PER (MATERIAL, ARTWORK) FOR THE WHOLE BUILD. Declared out here on purpose:
        // the duplication is both WITHIN a part -- two regions painting one shared material -- and
        // ACROSS parts, which is the larger half. A squad of four commandos each painting the
        // bodysuit shipped four byte-identical copies of four 25 MB virtual textures.
        var paintedAlready = new Dictionary<string, string>(StringComparer.Ordinal);

        // AND ONE PER TEXTURE, which catches what the material key cannot: two slots with their
        // own diffuse and mask, sharing one normal map and one tint between them.
        var textureAlready = new Dictionary<string, string>(StringComparer.Ordinal);

        // THE TINT MASK EACH OF THOSE PAINTS WEARS, by the same key. A repaint that is skipped
        // because an identical one exists still has to hand its piece the mask that one made.
        var paintedTintAlready = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var part in mod.Parts)
        {
            log?.Invoke($"  {part.Id}");
            var template = TemplateFor(provider, parts, part, out var compromise, out var conflictNote, log);
            if (template is null) { Skip($"{part.Id}: no template for slot {part.Slot}"); continue; }
            log?.Invoke($"      template: {template.Path}");
            if (compromise is not null) warnings.Add($"{part.Id}: {compromise}");
            if (conflictNote is not null) warnings.Add($"{part.Id}: {conflictNote}");

            var containerPath = $"{ProjectName}/{modFolder}{part.Id}.uasset";
            var packageName = PackageNameOf(part.Id, modFolder);

            // The registry entry is settled BEFORE anything is written. A package that reaches the
            // container without one is in the mod and invisible -- the customization system
            // enumerates the registry, not the filesystem -- so a part is built whole or not at all.
            var templateName = template.Path;   // PartsIndex reports package names already
            if (!templateEntry.TryGetValue(templateName, out var at))
            { Skip($"{part.Id}: the template {templateName} has no registry entry to copy"); continue; }

            // Building against a registry that ALREADY lists this part means a previous build of
            // the mod is deployed and its merged registry is what got mounted. Appending again
            // stacks a second entry for the same package, which is exactly the kind of fault that
            // shows up as a duplicated menu item three steps later.
            if (templateEntry.ContainsKey(packageName))
                warnings.Add($"{part.Id}: the registry already lists it -- this looks like a " +
                             "previously deployed build, so pass the game's pristine AssetRegistry.bin");

            // A loose PNG or TGA becomes a texture package of its own, authored the same way the
            // part is: by taking the template's icon and replacing its pixels. The part then
            // points at that instead of at the template's icon.
            var iconObjectPath = part.Icon;
            (string Package, string Asset, float Width, float Height, string Template)? partIconBrush = null;
            // The picture's package, when this part gets a hard reference to one of its own.
            ulong? partExtraImport = null;
            var templateDetail = PartDetails.Read(provider, template.Path);
            ulong? oldIconId = PackageOf(templateDetail.IconPath) is { } oldIconPkg
                ? PackageId.Of(oldIconPkg) : null;
            ulong? newIconId = null;

            if (IconNeedsCooking(part.Icon))
            {
                var detail = templateDetail;
                var image = FindImage(part.Icon, mod.FilePath);
                if (image is null)
                    warnings.Add($"{part.Id}: cannot find the image '{part.Icon}' -- keeping the template's icon");
                else if (!TextureAuthor.IsSupportedImage(image))
                    warnings.Add($"{part.Id}: '{part.Icon}' is not a PNG or TGA -- keeping the template's icon");
                else if (string.IsNullOrEmpty(detail.IconPath))
                {
                    // NO TEXTURE TO RENAME, because the template names its picture by BANK TAG --
                    // which every weapon TYPE part does, and the type part is what the Armoury's
                    // model list draws (measured: a picture written into the gear-kit part is
                    // ignored there, and this part's tag is what the row followed). So the picture
                    // is authored over whatever that tag resolves to and written INTO the part as
                    // a soft brush, the shape the game's own DC-17m part carries.
                    iconBanks ??= ImageBanks.Read(provider, ImageBanks.Candidates(provider), log);
                    var partBankTex = PackageOf(iconBanks.FirstOrDefault(
                        b => b.Tag.Equals(detail.IconBankTag, StringComparison.OrdinalIgnoreCase))?.TexturePath);
                    if (detail.IconBankTag is null || partBankTex is null)
                        warnings.Add($"{part.Id}: the template has no icon to build one from -- no icon");
                    else
                    {
                        var partTexId = "T_Icon_" + new string(part.Id.Where(char.IsLetterOrDigit).ToArray());
                        var partTexPackage = PackageNameOf(partTexId, modFolder);
                        if (AuthorIconTexture(image, partBankTex, partTexId, partTexPackage, part.Id) is { } partShape)
                            partIconBrush = (partTexPackage, partTexId, partShape.Width, partShape.Height, partBankTex);
                    }
                }
                else
                {
                    var iconTemplate = detail.IconPath!;
                    if (iconTemplate.Contains('.')) iconTemplate = iconTemplate[..iconTemplate.IndexOf('.')];
                    // Named after the IMAGE, not the part: that is the name the mod author chose
                    // and the one they will look for in the game's menu.
                    var iconId = Path.GetFileNameWithoutExtension(image);
                    if (iconId.Length == 0) iconId = "T_Mod_" + part.Id;
                    var iconPackage = PackageNameOf(iconId, modFolder);

                    var iconShape = TextureAuthor.ShapeOf(provider, iconTemplate, out _);
                    if (iconShape is not null &&
                        TextureAuthor.AspectNote(image, iconShape) is { } note)
                        warnings.Add($"{part.Id}: {Path.GetFileName(image)} {note}");

                    var iconBytes = TextureAuthor.Author(provider, iconTemplate, image, iconPackage, out var iconWhy);
                    if (iconBytes is null)
                    {
                        warnings.Add($"{part.Id}: could not author the icon -- {iconWhy}");
                    }
                    else
                    {
                        var iconContainerPath = $"{ProjectName}/{modFolder}{iconId}.uasset";
                        var iconChunkId = PackageId.Of(iconPackage);
                        chunks.Add(new IoStoreWriter.Chunk(iconChunkId, 0, 1, iconBytes, iconContainerPath));
                        if (store.TryGetValue(PackageId.Of(iconTemplate), out var iconEntry))
                            storeFor[iconChunkId] = iconEntry;
                        built.Add(new BuiltPart(iconId, iconPackage, iconChunkId, iconContainerPath,
                                                iconBytes.Length, iconTemplate, IsTexture: true));
                        iconObjectPath = $"{iconPackage}.{iconId}";
                        newIconId = iconChunkId;
                        log?.Invoke($"      icon authored from {Path.GetFileName(image)} -> {iconPackage}");

                        // The texture needs a registry entry of its own, copied from the icon it
                        // was built from. Without one the part references a package the game has
                        // no record of, and the thumbnail is simply missing.
                        if (templateEntry.TryGetValue(iconTemplate, out var iconAt))
                        {
                            var iconRecord = records[iconAt];
                            var iconTags = PartTagsFor(
                                decoded.PreallocatedAssetDataBuffers[iconAt].TagsAndValues, iconId);

                            Register(new RegistryAssets.NewAsset(
                                PackageName: iconPackage,
                                AssetName: iconId,
                                PackagePath: iconPackage[..iconPackage.LastIndexOf('/')],
                                ClassPackage: registry.Names[(int) iconRecord.ClassPackage],
                                ClassName: registry.Names[(int) iconRecord.ClassName],
                                Tags: iconTags));
                        }
                        else warnings.Add($"{part.Id}: the icon template has no registry entry to copy");
                    }
                }
            }

            if (newIconId is null && PackageOf(iconObjectPath) is { } chosenIconPkg)
                newIconId = PackageId.Of(chosenIconPkg);

            // Everything the template contributes, classified. An unaccounted fragment is not a
            // guess about what will break -- it is a statement that nobody has looked, which is
            // the condition every fault so far was shipped under.
            foreach (var concern in PartAudit.Concerns(part.Id, PartAudit.Of(templateDetail)))
                warnings.Add(concern);

            // The mod's OWN copy of the mesh, when the part asks for one. Everything else here
            // rearranges assets the game ships; this is the first thing that gives a person
            // something of their own to edit, and it is what an imported mesh will later replace.
            //
            // A mesh is a SOFT reference rather than an import, so nothing in the store entry has
            // to change -- the part names it by path, and Author already substitutes that path.
            // A repaint needs the mesh copy, because the mesh is what names the material. Turned on
            // here rather than refused: a part that asks for its own textures and keeps the game's
            // mesh would build, install, and wear the game's material -- the exact failure that
            // reads as "the tool did nothing".
            //
            // And so does a chosen material, for the same reason: the mesh is what names it.
            //
            // Unless the game's mesh ALREADY wears every material that was chosen: a copy would
            // carry nothing, so the part keeps pointing at the game's own mesh.
            var choiceWorn = part.HasNamedMaterial && PackageOf(part.Mesh) is { } chosenOn &&
                             NamedMaterial.AllWorn(provider, chosenOn, part.Materials);
            var wantsOwnMesh = part.OwnMesh || part.HasPaint || (part.HasNamedMaterial && !choiceWorn) ||
                               !string.IsNullOrWhiteSpace(part.MeshFile);
            if (!part.OwnMesh && wantsOwnMesh)
                log?.Invoke("      carrying its own mesh, because " +
                            (!string.IsNullOrWhiteSpace(part.MeshFile)
                                ? "an imported mesh has to go somewhere of its own"
                                : part.HasPaint
                                    ? "its own textures need a material of its own"
                                    : "the material you chose is named by the mesh"));
            else if (choiceWorn && !part.OwnMesh && !wantsOwnMesh)
                log?.Invoke("      the game's mesh already wears the material you chose, so there is " +
                            "no copy of it to carry");

            // THE FILE SAYS ONE BODY AND THE MESH SAYS THE OTHER.
            //
            // The game's clothing meshes are named for the body they are authored for -- HAAM
            // masculine, HAAF feminine -- and nothing in the browser said so, so a masculine
            // helmet was imported over the feminine mesh of a different kit. It builds, and what
            // it produces is a part registered for the wrong body wearing geometry cut for the
            // other one.
            //
            // Both halves are names, so this is a smell and not a proof, which is why it is a
            // note. It costs nothing when the author meant it.
            if (!string.IsNullOrWhiteSpace(part.MeshFile) && !string.IsNullOrWhiteSpace(part.Mesh))
            {
                static string Body(string n) =>
                    n.Contains("_HAAM", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("_HTAM", StringComparison.OrdinalIgnoreCase) ? "masculine"
                    : n.Contains("_HAAF", StringComparison.OrdinalIgnoreCase) ||
                      n.Contains("_HTAF", StringComparison.OrdinalIgnoreCase) ? "feminine" : "";

                var fileBody = Body(part.MeshFile);
                var meshBody = Body(Leaf(PackageOf(part.Mesh) ?? part.Mesh));
                if (fileBody.Length > 0 && meshBody.Length > 0 && fileBody != meshBody)
                    serious.Add($"{part.Id}: the file you are importing is named for a {fileBody} " +
                                $"body ({part.MeshFile}) and the mesh it replaces is the {meshBody} " +
                                $"one ({Leaf(PackageOf(part.Mesh) ?? part.Mesh)}). The part will be " +
                                $"offered to {meshBody} characters wearing geometry cut for the " +
                                "other body. Point the part at the mesh that matches your file.");
            }

            string? ownMeshPath = null;
            string? ownTintMask = null;
            Dictionary<string, string>? regionMeshes = null;

            // THE TINT MASK EACH PIECE MADE, by piece key. `ownTintMask` is still the ONE the
            // anchor wins, which is what a part without pieces needs; a garment that dresses
            // several pieces needs every piece's own, because the template carries a mask fragment
            // per piece and a boot painted through the legs' mask is the legs' UVs on the boot.
            Dictionary<string, string>? regionTintMasks = null;
            // What Author did to each tint fragment, for the verifier to read the finished part back against.
            var tintExpected = new List<TintExpectation>();

            if (part.HasRegions)
            {
                var pieceTintMasks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                regionTintMasks = pieceTintMasks;
                var plans = PlanRegions(templateDetail, part, out var unmatched);
                var pieces = string.Join(", ", plans.Select(p => p.Key).Where(k => k.Length > 0));
                regionMeshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var untouched = plans.Where(x => x.Entry is null && x.Key.Length > 0)
                                     .Select(x => x.Key).ToList();
                log?.Invoke($"      {plans.Count} piece(s): {pieces}");
                if (untouched.Count > 0)
                    log?.Invoke($"      left as the game has them: {string.Join(", ", untouched)}");

                // AIMED AT NOTHING. The author's model is simply absent from the result and the
                // build otherwise looks clean, so this is serious rather than grey.
                foreach (var name in unmatched)
                    serious.Add($"{part.Id}: the piece '{name}' matches no piece of " +
                                $"{Leaf(template.Path)} (it has: {pieces}) -- nothing wears it");

                // TWO PIECES OFF ONE SOURCE. `subs` holds one replacement per name, so the second
                // Reference call silently overwrites the first and one of the two pieces quietly
                // keeps the game's geometry.
                foreach (var clash in plans.Where(p => p.Entry is not null)
                                           .GroupBy(p => PackageOf(p.Mesh) ?? p.Mesh,
                                                    StringComparer.OrdinalIgnoreCase)
                                           .Where(g => g.Count() > 1))
                    serious.Add($"{part.Id}: the pieces " +
                                string.Join(" and ", clash.Select(p => $"'{p.Key}'")) +
                                $" both start from {Leaf(clash.Key)}, and only one of them can be " +
                                "redirected -- give them different meshes");

                foreach (var plan in plans)
                {
                    if (plan.Key.Length == 0 || string.IsNullOrWhiteSpace(plan.Mesh)) continue;

                    // A PIECE THE AUTHOR DID NOT NAME IS NOT TOUCHED AT ALL.
                    //
                    // It used to be resolved to the template's own mesh for that piece, on the
                    // reasoning that Reference would then drop it as a no-op. That is true for ONE
                    // of the fragment's options and false for the rest: Author redirects every
                    // option in the group, so a TORS fragment carrying HAAF and HAAM had its
                    // FEMININE option redirected onto the masculine mesh the anchor picked. A
                    // feminine character then wore masculine geometry -- the exact failure the
                    // body-token warning exists to catch, introduced by the thing meant to leave
                    // the piece alone. Absent from the map means absent from the substitution.
                    if (plan.Entry is null) continue;

                    // PER PIECE, so one imported FBX does not drag four unwanted copies of the
                    // game's meshes into the mod. `ownMesh` on the part still turns them all on.
                    var pieceChoiceCopies = plan.Entry is { } pe && pe.HasNamedMaterial &&
                                            !(PackageOf(plan.Mesh) is { } pieceMesh &&
                                              NamedMaterial.AllWorn(provider, pieceMesh, pe.Materials));
                    var wantsOwn = plan.Entry is { } e &&
                                   (part.OwnMesh || e.HasPaint || pieceChoiceCopies ||
                                    !string.IsNullOrWhiteSpace(e.MeshFile));
                    var src = PackageOf(plan.Mesh);
                    if (!wantsOwn || src is null) { regionMeshes[plan.Key] = plan.Mesh; continue; }

                    // The ANCHOR's tint mask is the part's ONE mask (`ownTintMask`) when more than
                    // one piece makes one: the colour fragment paints the part, and the anchor is
                    // the piece on the part's own slot. That one mask is what a template with a
                    // single mask is redirected to; a template with a mask PER PIECE is redirected
                    // piece by piece from `pieceTintMasks`, recorded below -- see Author.
                    var anchor = string.Equals(src, PackageOf(part.Mesh), StringComparison.OrdinalIgnoreCase);

                    // A FOLDER PER PIECE, unconditionally -- not OwnFolderFor's shared-source test.
                    //
                    // Two reasons, both read out of the real code rather than from the names.
                    // (a) OwnMesh names every material and texture copy after the SOURCE
                    // material's own name, so two pieces repainting one shared material -- a
                    // bodysuit worn by the torso AND the legs is how these kits are built --
                    // compute the same destination, and the second copy silently overwrites the
                    // first, leaving both pieces wearing the second's textures.
                    // (b) SharedSources cannot see the source at all when a piece names an import
                    // and no mesh of its own: the source is then the template's, which that
                    // pre-pass never counted, so its answer would be about the wrong package.
                    // Isolating the piece settles both without needing either question answered.
                    var pieceFolder = $"{modFolder}Parts/{part.Id}/{plan.Key}/";
                    var made = OwnMesh(provider, src, SyntheticPartFor(part, plan), mod,
                                       pieceFolder,
                                       $"{part.Id} [{plan.Key}]", chunks, storeFor, built, warnings,
                                       log, mask =>
                                       {
                                           pieceTintMasks[plan.Key] = mask;
                                           if (anchor) ownTintMask = mask; else ownTintMask ??= mask;
                                       },
                                       paintedAlready, textureAlready, serious,
                                       templateDetail.MaterialSwaps, Leaf(template.Path),
                                       paintedTintAlready: paintedTintAlready);

                    if (made is null)
                        serious.Add($"{part.Id}: the '{plan.Key}' piece produced no mesh of its " +
                                    "own, so it is still the game's");
                    else if (templateDetail.MeshFragments.FirstOrDefault(g => g.Key == plan.Fragment) is { } pieceGroup &&
                             PerHeadCost($"{part.Id} [{plan.Key}]", pieceGroup, plan.Mesh,
                                         PartDetails.RegionMeshes(templateDetail, templateDetail.MeshPath)
                                                    .FirstOrDefault(x => x.Region == plan.Key).Mesh,
                                         part.OwnMesh || !string.IsNullOrWhiteSpace(plan.Entry?.MeshFile),
                                         plan.Entry?.HasPaint == true, pieceChoiceCopies)
                             is { } pieceCost)
                        serious.Add(pieceCost);
                    regionMeshes[plan.Key] = made ?? plan.Mesh;
                }
            }
            else if (wantsOwnMesh && PackageOf(part.Mesh) is { } sourceMeshPackage)
            {
                ownMeshPath = OwnMesh(provider, sourceMeshPackage, part, mod,
                                      OwnFolderFor(modFolder, sourceMeshPackage, part.Id,
                                                   sharedSources, log),
                                      part.Id, chunks, storeFor, built, warnings, log,
                                      made => ownTintMask = made, paintedAlready, textureAlready,
                                      serious, templateDetail.MaterialSwaps, Leaf(template.Path),
                                      paintedTintAlready: paintedTintAlready);

                // A COPY THAT WAS NOT ASKED FOR BY NAME COSTS A PER-HEAD PART ITS CUTS.
                if (ownMeshPath is not null &&
                    PerHeadCost(part.Id, templateDetail.Meshes, part.Mesh, templateDetail.MeshPath,
                                part.OwnMesh || !string.IsNullOrWhiteSpace(part.MeshFile),
                                part.HasPaint, part.HasNamedMaterial && !choiceWorn) is { } headCost)
                    serious.Add(headCost);
            }

            // A TEMPLATE THAT DRESSES SEVERAL PIECES AND A PART THAT NAMES ONE MESH.
            //
            // This is what the builder has always done -- the one mesh goes into every fragment, so
            // boots, torso, legs and arms all become the same garment -- and nothing anywhere has
            // ever said so. Preserved deliberately (it is what every existing mod was built on),
            // and now said out loud, because a person seeing five copies of their torso in game had
            // no way to find out why.
            //
            // NOT FOR A PART THAT KEEPS EVERY HEAD'S OWN CUT. A flat per-head part the author only
            // renamed (a two-piece beard, unedited) has its options left exactly as the template has
            // them -- see KeepsHeadCuts -- so no piece gets "that same mesh", and saying so is false.
            if (templateDetail.MeshFragmentCount > 1 && !part.HasRegions &&
                !ModProject.SlotHasNoMesh(part.Slot) &&
                !KeepsHeadCuts(templateDetail.Meshes, ownMeshPath ?? part.Mesh, templateDetail.MeshPath))
                serious.Add($"{part.Id}: its template {Leaf(template.Path)} dresses " +
                            $"{templateDetail.MeshFragmentCount} pieces and this part names one " +
                            "mesh, so every piece gets that same mesh. Give it a `regions` entry " +
                            "per piece.");

            // NO ANCHOR. Validate lets a part with pieces carry no `mesh` of its own -- it has to,
            // or a garment whose geometry lives entirely in its pieces would grey out Build for the
            // whole folder. But the anchor is what the template ranking learns the base class from,
            // and which piece's tint mask the colour fragment is redirected to, so a blank one
            // degrades both with nothing said. Staging always sets it; a hand-written file may not.
            if (part.HasRegions && string.IsNullOrWhiteSpace(part.Mesh))
                warnings.Add($"{part.Id}: it authors pieces but names no `mesh` of its own. That " +
                             "mesh is the anchor -- the piece on the part's own slot -- and the " +
                             "ranking uses it to find the right base class. Name the piece that " +
                             "sits on this part's slot.");

            // Nothing wears them. A part whose geometry moved into its pieces still carries the
            // part-level import and paint fields, and they are read by nothing.
            if (part.HasRegions &&
                (!string.IsNullOrWhiteSpace(part.MeshFile) || part.HasPaint || part.HasNamedMaterial))
                serious.Add($"{part.Id}: it authors its pieces, so the part's own " +
                            (part.HasNamedMaterial ? "import, textures and chosen materials"
                                                   : "import and textures") +
                            " are not used by anything -- move them onto the piece they " +
                            "belong to.");

            // A TEMPLATE THAT PAINTS ITS OWN TINT MASK OVER THE AUTHOR'S ARTWORK.
            //
            // `CustomizationFragmentMaterialTexture` is how the game does colour variants of one
            // helmet: the mesh and the material are shared, and each variant carries a fragment
            // overriding ONE named parameter -- "Tint Mask VT" -- with that variant's mask, which
            // a MaterialColor fragment then paints a colour through.
            //
            // So it does not replace the author's textures wholesale, and an earlier version of
            // this note said it did. What it replaces is the MASK, and the template's colour then
            // lands wherever the template's mask says rather than wherever theirs does.
            //
            // Only said when the mask could NOT be redirected. A part that imported its own Tint
            // has already had the fragment pointed at it, above, and there is nothing to warn
            // about -- warning anyway would be crying wolf on the case that now works.
            if (part.HasAnyPaint && string.IsNullOrWhiteSpace(ownTintMask) &&
                string.IsNullOrWhiteSpace(part.TintMask) &&
                PartDetails.Read(provider, template.Path).Fragments is { } frags &&
                frags.Any(f => f.Contains("MaterialTexture", StringComparison.OrdinalIgnoreCase)))
                serious.Add($"{part.Id}: its template {Leaf(template.Path)} paints its own TINT " +
                            "MASK over the material, and this part imported no Tint texture to " +
                            "put there instead -- so the template's colours land where ITS mask " +
                            "says, not where your artwork does. Import a Tint image, or name one " +
                            "with `tintMask`.");

            var bytes = Author(provider, template, part, packageName, iconObjectPath, out var authorWhy,
                               log, ownMeshPath, ownTintMask, regionMeshes, warnings,
                               regionTintMasks, tintExpected);
            // authorWhy is null for the pre-existing failure (the template package would not read)
            // and set for the one Author now refuses itself for -- an addTags write that did not
            // land (see Author's own note). Falling back to the old text keeps every other caller
            // of this line reading exactly as it did.
            if (bytes is null) { Skip($"{part.Id}: {authorWhy ?? "could not read the template package"}"); continue; }

            // HOW THE PACKAGE STORES ITS OWN NAME, said out loud for an id that needs splitting.
            //
            // A trailing _<digits> belongs in the FName NUMBER, not in the string. Writing the
            // literal produces a package that mounts and cannot be resolved by name -- confirmed
            // in game. ZenPackage.RenameNames now splits it; this is the line that shows it did.
            if (ZenPackage.ParseNumber(part.Id).Stem is { } idStem)
            {
                var zp = ZenPackage.Read(bytes);
                var mapped = BitConverter.ToUInt32(zp.Head, 8) & 0x3FFFFFFFu;
                var num = BitConverter.ToUInt32(zp.Head, 12);
                var held = mapped < zp.Names.Count ? zp.Names[(int) mapped] : "<out of range>";
                log?.Invoke($"      name split: entry ends \"{Leaf(held)}\", FName number {num}" +
                            (num == 0 ? "  -- NOT SPLIT, this part will not resolve" : ""));
                if (num == 0)
                    serious.Add($"{part.Id}: its package stores the trailing number as part of the " +
                                "name instead of as an FName number, so nothing will resolve it");
            }

            // THE COLOUR THIS VARIANT PAINTS. After Author, because it is a byte-level property
            // edit on the finished package and Author works in names -- and because locating a
            // property means parsing the package the build actually produced.
            if (!string.IsNullOrWhiteSpace(part.TintColor))
            {
                // The TEMPLATE's name, for the schema -- see TintColour.Set.
                var tinted = TintColour.Set(provider, bytes, template.Path, part.TintColor,
                                            out var tintWhy, log);
                if (tintWhy is not null) warnings.Add($"{part.Id}: tint colour: {tintWhy}");
                else bytes = tinted;
            }

            // The picture into the part, when its template named one by tag. After Author, whose
            // renames leave the export structure alone -- only names move -- so the walk that
            // finds the property still reads the same bytes. Reported, never fatal: a part that
            // kept its template's picture is still a part.
            if (partIconBrush is { } brush)
            {
                // A template that named its picture by BANK TAG imports no texture, so there is
                // nothing to redirect and this is where an import has to be ADDED.
                var partSlot = store.TryGetValue(PackageId.Of(template.Path), out var partStore)
                    ? partStore.ImportedPackages.Count : -1;
                bytes = SetPicture(bytes, template.Path, part.Id, brush.Package, brush.Asset,
                                   brush.Width, brush.Height, brush.Template, partSlot,
                                   out partExtraImport, out _);
            }

            // SLOTS LAST, on the finished bytes: every other edit above has already moved what it
            // moves. No "changed nothing" gate follows here (unlike the override loop) -- a part
            // always queues its chunk -- so, unlike there, placement is not load-bearing.
            //
            // partSlots.PartExport, not part.Id: SlotSets.Resolve is the ONE place that decides a
            // part/copy's export name (R7), and ModVerify reads a landing's PartExport back
            // through that same call -- so the two can never disagree, even though today they
            // agree with part.Id too. MEASURED, not assumed: ZenPackage.Read(bytes).ExportNames
            // on SPT_Helmet (--modbuild SlotPartTest.json) was "BitReactorUIDataFragment_0,
            // SPT_Helmet, CustomizationFragmentFoley_0, CustomizationFragmentHelmetVO_0,
            // CustomizationFragmentHelmetVO_1, CustomizationFragmentMaterialTexture_1,
            // CustomizationFragmentMesh_14, CustomizationFragmentOverrideSlots_0" -- Author's own
            // rename (subs[oldLeaf] = part.Id) already lands the part's own top-level export at
            // exactly the id, confirming SlotSets.Resolve's export = target for a non-game
            // landing needs no correction.
            if (slotLandings.FirstOrDefault(l => !l.IsGamePart && l.Target.Equals(part.Id, StringComparison.OrdinalIgnoreCase))
                is { } partSlots)
            {
                bytes = SlotAdd.Add(provider, bytes, packageName, partSlots.PartExport, partSlots.Slots,
                                    out var partSlotsAdded, out var partSlotWhy, headerFrom: template.Path, log: log);
                if (partSlotWhy is not null) { Skip($"{part.Id}: slots: {partSlotWhy}"); continue; }
                log?.Invoke($"      {partSlotsAdded.Count} slot(s) added: " + string.Join(", ", partSlotsAdded.Select(a => a.Id)));
                slotStamps.Add(new SlotStamp(partSlots.Target, packageName, partSlots.PartExport, partSlots.Slots));
            }

            var chunkId = PackageId.Of(packageName);
            chunks.Add(new IoStoreWriter.Chunk(chunkId, 0, 1 /* ExportBundleData */, bytes, containerPath));
            // WHAT WAS ACTUALLY SUBSTITUTED, recorded rather than reconstructed.
            //
            // --diff prefers this map and otherwise falls back to ModDiff.SubstitutionsFor, which
            // maps EVERY mesh option onto ONE mesh. Right for a one-mesh part, wrong for a part
            // with pieces: the fallback picks one of the region copies and then reports the other
            // four faithful redirects as changes nobody asked for.
            IReadOnlyDictionary<string, string>? partIntent = null;
            if (regionMeshes is { Count: > 0 })
            {
                var intent = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [template.Path] = packageName,
                    [Leaf(template.Path)] = part.Id,
                };
                var intentKeys = PartDetails.RegionKeys(templateDetail);
                var intentSeeds = PartDetails.RegionMeshes(templateDetail, templateDetail.MeshPath);
                foreach (var group in templateDetail.MeshFragments)
                {
                    var key = intentKeys.FirstOrDefault(k => k.Fragment == group.Key).Key ?? "";
                    if (!regionMeshes.TryGetValue(key, out var to) || string.IsNullOrWhiteSpace(to)) continue;
                    // A fragment whose per-head options were left alone intended no redirect.
                    if (KeepsHeadCuts(group, to, intentSeeds.FirstOrDefault(x => x.Region == key).Mesh)) continue;
                    foreach (var option in group)
                    {
                        intent[option.MeshPath] = to;
                        intent[Leaf(option.MeshPath)] = Leaf(to);
                    }
                }
                if (!string.IsNullOrWhiteSpace(part.Display) &&
                    !string.IsNullOrWhiteSpace(templateDetail.DisplayName))
                    intent[templateDetail.DisplayName!] = part.Display;
                partIntent = intent;
            }

            built.Add(new BuiltPart(part.Id, packageName, chunkId, containerPath, bytes.Length,
                                    template.Path, Substitutions: partIntent,
                                    TintExpected: tintExpected.Count > 0 ? tintExpected : null));

            // The authored package is a copy of the template's bytes, so it imports exactly what
            // the template imports -- the mesh and icon are SOFT references and are not imports at
            // all. Copying the template's entry is therefore not an approximation.
            if (store.TryGetValue(PackageId.Of(template.Path), out var templateEntry2))
            {
                // The ICON is a hard import; the MESH is not. Measured, not assumed: the template's
                // three imports are its icon texture, its string table and its Blueprint base, and
                // its mesh is absent because a mesh option is a SOFT reference.
                //
                // So redirecting the icon has to redirect the import too. Leaving the template's
                // icon id in the list means the package references a texture it never declared and
                // declares one it no longer uses -- and the declared list is what the loader walks.
                var imports = templateEntry2.ImportedPackages
                    .Select(id => oldIconId is not null && id == oldIconId && newIconId is not null
                        ? newIconId.Value : id)
                    .ToList();

                // Appended, never inserted: an FPackageObjectIndex names a package by INDEX.
                if (partExtraImport is { } partAdded)
                {
                    imports.Add(partAdded);
                    log?.Invoke($"      imports its own picture as package [{imports.Count - 1}]");
                }
                storeFor[chunkId] = new ContainerHeader.StoreEntry(imports, templateEntry2.ShaderMapHashes);
                if (imports.Count > 0)
                    log?.Invoke($"      imports {imports.Count} package(s), carried over" +
                                (oldIconId != newIconId ? " (icon redirected)" : ""));
            }
            else warnings.Add($"{part.Id}: the template has no store entry, so its imports are " +
                              "unknown -- the part may fail to load");

            var record = records[at];
            var buffers = decoded.PreallocatedAssetDataBuffers[at];
            var tags = PartTagsFor(buffers.TagsAndValues, part.Id, part.RemoveTags, part.ForCharacter,
                                   part.RenameNames, part.AddTags);

            Register(new RegistryAssets.NewAsset(
                PackageName: packageName,
                AssetName: part.Id,
                PackagePath: packageName[..packageName.LastIndexOf('/')],
                // Both halves of the class come from the template. Inventing either one is how a
                // part ends up in the registry under a class the customization system never asks
                // for, which looks exactly like a part that does not exist.
                ClassPackage: registry.Names[(int) record.ClassPackage],
                ClassName: registry.Names[(int) record.ClassName],
                Tags: tags));

            // Describe the entry that was just WRITTEN, with the same code that describes the
            // game's own parts, and check it is actually wearable.
            //
            // This is the check that was missing. Selecting an open template is the fix; this is
            // the proof, and it is the half that matters -- a mod whose parts are gated is
            // invisible in exactly the way a mod that failed to install is, so a build reporting
            // itself clean while shipping three locked parts is worse than one that fails.
            var written = PartsIndex.Describe(
                packageName, part.Id, registry.Names[(int) record.ClassName],
                tags.FirstOrDefault(x => x.Item1 == "CustomizationPartTags").Item2);

            if (written.Gate != "open")
                warnings.Add($"{part.Id}: built from {template.Path}, which is gated behind " +
                             $"'{written.Gate}' -- the part will not appear in the menu");
            if (written.Characters.Count > 0)
                warnings.Add($"{part.Id}: restricted to {string.Join(", ", written.Characters)} " +
                             "by the template it was built from");

            // A SPECIES LOCK, which nothing used to notice. It is not a gate and not a character
            // restriction, so every existing check reported this part as unrestricted -- and it
            // then built clean, installed, and never appeared in the menu for anybody of another
            // species. Serious, because there is no other symptom at all.
            if (written.SpeciesLock is { Length: > 0 } lockedTo)
                serious.Add($"{part.Id}: its template {Leaf(template.Path)} is restricted to the " +
                            $"{lockedTo} species, and that restriction is inherited -- the part " +
                            "will not appear for a character of any other species. Build it from " +
                            "a template without a species tag if you want everyone to wear it.");
            if (written.Slots.Count == 0)
                warnings.Add($"{part.Id}: carries no slot tag, so nothing will offer it");

            // The identity, not just the wearability. A part registered under another asset's
            // primary name collides with it and loses, which looks exactly like a part that was
            // never installed -- and did, for every part built before this was checked.
            var primaryName = tags.FirstOrDefault(x => x.Item1 == "PrimaryAssetName").Item2;
            if (primaryName != part.Id)
                warnings.Add($"{part.Id}: registered under the name '{primaryName}', which is not " +
                             "its own -- it will collide with whatever owns that name");
        }

        // MESHES: a game mesh copied into the mod, with the author's geometry in the copy.
        //
        // Runs before the copies because a palette copy will name what this produces, and a person
        // reading the log should see the mesh appear before the thing that points at it.
        foreach (var mesh in mod.Meshes)
        {
            var mshSource = PackageOf(mesh.Source) ?? mesh.Source;
            if (string.IsNullOrWhiteSpace(mshSource)) { Skip("a mesh entry with no source"); continue; }

            var mshLeaf = mshSource[(mshSource.LastIndexOf('/') + 1)..];
            log?.Invoke($"  mesh {mshLeaf}");

            // A synthetic part, because OwnMesh was written for parts and asking it for anything
            // else would mean two copies of a delicate path instead of one.
            var mshPart = new ModPart
            {
                Id = mshLeaf,
                Mesh = mesh.Source,
                MeshFile = mesh.MeshFile,
                MeshScale = mesh.MeshScale,
                MeshBasis = mesh.MeshBasis,
                MeshOffsetX = mesh.MeshOffsetX,
                MeshOffsetY = mesh.MeshOffsetY,
                MeshOffsetZ = mesh.MeshOffsetZ,
            };
            // EVERY slot, not just the first. A weapon part carrying a second material would
            // otherwise lose it here, between the mesh that declares it and the builder that
            // paints it, with nothing to show for it but the body's paint on both sections.
            for (var i = 0; i < mesh.Materials.Count; i++)
                foreach (var (slot, file) in mesh.Materials[i].Textures)
                    mshPart.Material(i).Textures[slot] = file;

            var mshPath = OwnMesh(provider, mshSource, mshPart, mod,
                                  OwnFolderFor(modFolder, mshSource, mshLeaf, sharedSources, log),
                                  mshLeaf, chunks, storeFor, built, warnings, log);

            // Never silent. A mesh that did not copy leaves whatever points at it aimed at the
            // game's own asset, which builds, installs, and shows the game's model -- the exact
            // failure that reads as "the tool did nothing".
            if (mshPath is null) Skip($"{mshLeaf}: it produced no mesh of its own");
            else log?.Invoke($"      the mod's own copy is at {mshPath}");
        }

        // COPIES: a registered asset duplicated under a new name.
        //
        // The general case the part loop is not. That one goes through PartsIndex, which knows only
        // CustomizationPartDefinition -- so a weapon LOADOUT is invisible to it, and a loadout is
        // what the armoury actually enumerates. Measured: a Type part built correctly and
        // registered identically to a real weapon's does not put a weapon in the game.
        //
        // No template ranking, no slot, no mesh. The author names the asset; this copies it,
        // re-points what it names, and gives it an identity of its own.
        // ------------------------------------------------------------------ the mod's own icons
        //
        // A menu icon is a TAG resolved through shared image banks, and a cooked bank cannot be
        // added to -- so a picture of the author's own ships in a BANK of the mod's own: a copy of
        // the game bank holding the template entry, with that entry re-keyed to the mod's tag and
        // pointed at a texture carrying the author's pixels. The game finds banks by scanning for
        // the class, so the copy is found (proved in game 2026-09-01, by hand). Every step is a
        // primitive that already exists -- TextureAuthor for the pixels, a ModCopy for the bank,
        // the tag list for the tag -- so this turns each icon into those and hands the bank copies
        // to the loop below. One bank copy per game bank touched, however many icons take slots
        // in it.
        // The mod's own icon texture: the template's package with the author's pixels, named as
        // asked, put in the container and the registry. One body for the two things that want
        // one -- an entry in a bank of the mod's own, and a copy carrying its picture outright --
        // so the texture a copy draws is made exactly the way the one a bank draws is.
        /// <summary>
        /// Point an asset's SmallImage and LargeImage at a texture -- BOTH HALVES of the brush.
        ///
        /// A brush names its texture twice: a soft path, and a hard `ResourceObject` that is an
        /// index into the package's import table. **The Armoury reads the hard one and never reads
        /// the soft one** -- measured in game 2026-09-02 with a probe whose soft path named a
        /// texture the GAME itself ships and which still drew the fallback picture. So a picture
        /// without the import is a picture nobody sees.
        ///
        /// One function because there are three callers -- a part, a copy and a specialization --
        /// and three copies of byte machinery that subtle is how the same fault ships three times.
        ///
        /// <paramref name="hashFrom"/> is the package whose stored PublicExportHash to import by:
        /// the TEMPLATE texture for one this tool authored (a clone keeps its template's hash and
        /// nothing recomputes it), or the texture itself when it is the game's own.
        /// <paramref name="importSlot"/> is the index the package will take in the container store
        /// entry's ImportedPackages, which the caller must then APPEND it at.
        /// </summary>
        byte[] SetPicture(byte[] bytes, string templatePackage, string owner,
                          string texPackage, string texId, float width, float height,
                          string hashFrom, int importSlot,
                          out ulong? extraImport, out int imagesSet)
        {
            extraImport = null;
            imagesSet = 0;

            int? hard = null;
            var hash = PropertyAdd.PublicExportHashOf(provider, hashFrom);
            if (hash == 0 || importSlot < 0)
                warnings.Add($"{owner}: its picture can only be named softly, which the menus do " +
                             "not read -- " + (importSlot < 0
                                 ? "the template has no store entry"
                                 : $"{Leaf(hashFrom)} declares no export hash"));
            else
            {
                bytes = PropertyAdd.AddImport(bytes, importSlot, hash, texPackage,
                                              out var fpi, out var impWhy);
                if (impWhy is not null)
                    warnings.Add($"{owner}: could not add the picture's import -- {impWhy}");
                else { hard = fpi; extraImport = PackageId.Of(texPackage); }
            }

            foreach (var which in new[] { "SmallImage", "LargeImage" })
            {
                bytes = PropertyAdd.SetImageToTexture(provider, bytes, templatePackage,
                    "BitReactorUIDataFragment", which, texPackage, texId, width, height,
                    out var imgWhy, hard);
                if (imgWhy is not null) warnings.Add($"{owner}: {which} stayed the template's -- {imgWhy}");
                else imagesSet++;
            }

            if (imagesSet > 0)
                log?.Invoke($"      picture: {imagesSet} image(s) now draw {texId}" +
                            (hard is null ? " by soft path only -- the menus will not read it"
                                          : $", hard reference {hard}"));
            return bytes;
        }

        TextureAuthor.Shape? AuthorIconTexture(string image, string texTemplate, string texId,
                                                string texPackage, string owner)
        {
            var texShape = TextureAuthor.ShapeOf(provider, texTemplate, out var shapeWhy);
            if (texShape is null) { warnings.Add($"{owner}: {shapeWhy}"); return null; }
            if (TextureAuthor.AspectNote(image, texShape) is { } aspect)
                warnings.Add($"{owner}: {Path.GetFileName(image)} {aspect}");
            var texBytes = TextureAuthor.Author(provider, texTemplate, image, texPackage, out var texWhy);
            if (texBytes is null) { warnings.Add($"{owner}: could not author the texture -- {texWhy}"); return null; }

            var texChunk = PackageId.Of(texPackage);
            var texPath = $"{ProjectName}/{modFolder}{texId}.uasset";
            chunks.Add(new IoStoreWriter.Chunk(texChunk, 0, 1, texBytes, texPath));
            if (store.TryGetValue(PackageId.Of(texTemplate), out var texEntry)) storeFor[texChunk] = texEntry;
            built.Add(new BuiltPart(texId, texPackage, texChunk, texPath, texBytes.Length, texTemplate,
                                    IsTexture: true));
            if (templateEntry.TryGetValue(texTemplate, out var texAt))
            {
                var texRecord = records[texAt];
                Register(new RegistryAssets.NewAsset(
                    PackageName: texPackage,
                    AssetName: texId,
                    PackagePath: texPackage[..texPackage.LastIndexOf('/')],
                    ClassPackage: registry.Names[(int) texRecord.ClassPackage],
                    ClassName: registry.Names[(int) texRecord.ClassName],
                    Tags: PartTagsFor(decoded.PreallocatedAssetDataBuffers[texAt].TagsAndValues, texId)));
            }
            else warnings.Add($"{owner}: the texture template has no registry entry to copy");
            log?.Invoke($"      texture authored from {Path.GetFileName(image)} -> {texPackage}");
            return texShape;
        }

        // A project from the day the picture went through a BANK -- an `icons` entry under a
        // minted tag and a copy renamed to point at it -- is carried over to the way that draws.
        // The bank was measured never to be read by the game, so leaving such a project as it is
        // would build a weapon wearing the game's stand-in picture again. The migration is done
        // here as well as in the Studio's editor, because a project can be built without its copy
        // ever being selected.
        foreach (var legacyCopy in mod.Copies)
        {
            if (legacyCopy.Icon.Length > 0) continue;
            var hit = legacyCopy.RenameNames
                .Select(kv => (kv.Key, Icon: mod.Icons.FirstOrDefault(i => i.Tag.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))))
                .FirstOrDefault(x => x.Icon is not null);
            if (hit.Icon is null) continue;
            legacyCopy.Icon = hit.Icon.Image;
            legacyCopy.RenameNames.Remove(hit.Key);
            mod.Icons.Remove(hit.Icon);
            log?.Invoke($"  {legacyCopy.Id}: its picture {hit.Icon.Image} now goes into the part itself, not a bank");
        }

        var bankCopies = new List<ModCopy>();
        if (mod.Icons.Count > 0)
        {
            iconBanks = ImageBanks.Read(provider, ImageBanks.Candidates(provider), log);
            var banks = iconBanks;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var icon in mod.Icons)
            {
                log?.Invoke($"  icon {icon.Tag}");
                if (string.IsNullOrWhiteSpace(icon.Tag)) { Skip("an icon with no tag"); continue; }
                if (string.IsNullOrWhiteSpace(icon.Image)) { Skip($"{icon.Tag}: names no image"); continue; }
                var image = FindImage(icon.Image, mod.FilePath);
                if (image is null) { Skip($"{icon.Tag}: cannot find the image '{icon.Image}'"); continue; }
                if (!TextureAuthor.IsSupportedImage(image))
                { Skip($"{icon.Tag}: '{icon.Image}' is not a PNG or TGA"); continue; }
                if (banks.Any(b => b.Tag.Equals(icon.Tag, StringComparison.OrdinalIgnoreCase)))
                {
                    Skip($"{icon.Tag}: the game already defines this tag. A picture of your own " +
                         "needs a tag of the mod's own, or the game's bank and yours disagree.");
                    continue;
                }

                // The entry whose slot this takes: the one the author named -- the tag the weapon
                // or class had before -- else the first of the same family nobody here has taken.
                var cut = icon.Tag.LastIndexOf('.');
                var family = cut < 0 ? "" : icon.Tag[..cut];

                // A TEXTURE NAMED OUTRIGHT, rather than a bank entry whose slot to take. Then
                // there is no bank to copy: the picture is authored onto that texture's package
                // and what points at the result is the author's business. A weapon's armoury part
                // needs exactly this -- the one such part that draws a texture draws one that is
                // in no bank at all, so there is no bank entry to build on.
                if (icon.Template.StartsWith('/'))
                {
                    var texDirect = PackageOf(icon.Template) ?? icon.Template;
                    var texIdDirect = "T_Icon_" + new string(icon.Tag[(cut + 1)..].Where(char.IsLetterOrDigit).ToArray());
                    var texPkgDirect = PackageNameOf(texIdDirect, modFolder);
                    if (AuthorIconTexture(image, texDirect, texIdDirect, texPkgDirect, icon.Tag) is null)
                        Skip($"{icon.Tag}: its texture could not be authored (see the note above)");
                    else
                        log?.Invoke($"      authored on {Leaf(texDirect)} -> {texIdDirect}; " +
                                    "nothing points at it until something is re-pointed at it");
                    continue;
                }
                var donor = icon.Template.Length > 0
                    ? banks.FirstOrDefault(b => b.Tag.Equals(icon.Template, StringComparison.OrdinalIgnoreCase))
                    : banks.FirstOrDefault(b => b.Family.Equals(family, StringComparison.OrdinalIgnoreCase) &&
                                                !taken.Contains(b.Tag));
                if (donor is null)
                {
                    Skip($"{icon.Tag}: no bank entry to build on -- " + (icon.Template.Length > 0
                        ? $"'{icon.Template}' is in none of the game's {banks.Count} bank entries"
                        : $"nothing in the game's banks is in the family {family}"));
                    continue;
                }
                if (!taken.Add(donor.Tag))
                { Skip($"{icon.Tag}: another icon in this mod already takes {donor.Tag}'s slot"); continue; }
                if (PackageOf(donor.TexturePath) is not { } texTemplate)
                { Skip($"{icon.Tag}: {donor.Tag} names no texture to build on"); continue; }

                // The texture: the donor's package with the author's pixels, named after the tag.
                var texId = "T_Icon_" + new string(icon.Tag[(cut + 1)..].Where(char.IsLetterOrDigit).ToArray());
                var texPackage = PackageNameOf(texId, modFolder);
                if (AuthorIconTexture(image, texTemplate, texId, texPackage, icon.Tag) is null)
                { Skip($"{icon.Tag}: its texture could not be authored (see the note above)"); continue; }

                // The bank: this mod's copy of the one the donor lives in, its entry re-keyed to
                // the mod's tag and pointed at the texture above. A soft path is two names, the
                // package and the leaf, and both move.
                var bank = bankCopies.FirstOrDefault(
                    c => c.Template.Equals(donor.BankPackage, StringComparison.OrdinalIgnoreCase));
                if (bank is null)
                {
                    var owner = new string(mod.Name.Where(char.IsLetterOrDigit).ToArray());
                    if (owner.Length == 0) owner = "Mod";
                    bank = new ModCopy
                    {
                        Id = $"IMB_{owner}_{(donor.Bank.StartsWith("IMB_", StringComparison.Ordinal) ? donor.Bank[4..] : donor.Bank)}",
                        Template = donor.BankPackage,
                        NewGuid = true,
                    };
                    bankCopies.Add(bank);
                }
                bank.RenameNames[donor.Tag] = icon.Tag;
                bank.RenameNames[texTemplate] = texPackage;
                bank.RenameNames[Leaf(texTemplate)] = texId;
                // BOTH halves of the brush. An FSlateBrush carries the texture twice: a soft
                // WeakResourceObject, which the rename above moves, and a hard ResourceObject
                // import, which no rename touches -- it is a package id in the store entry. The
                // hand-written test moved only the soft one and drew, but it pointed at a GAME
                // texture the hard import could not disagree with. Pointed at the mod's own, the
                // two must agree, so the import is redirected the way the weapon chain's are.
                bank.RepointImports[texTemplate] = texId;
                log?.Invoke($"      {donor.Tag}'s slot in {donor.Bank} becomes {icon.Tag}, drawing {texId}");
            }
        }

        // HOW FULL THE CLASS ALREADY IS. The Armoury's list for one weapon class holds seven;
        // the eighth damages the FIRST row -- measured in game, both directions, and the game's own
        // 773-Firepuncher is what lost its icon. Counted across EVERY mounted container, because
        // what matters to a player is the game's weapons plus every mod already installed, not just
        // this one. Said before the build rather than after, so nobody ships one and finds out.
        // The class a copy ACTUALLY lands in, which is not the one its name says when it has been
        // routed: the armoury part's class tag is a rename like any other, and a weapon called
        // CPD_GK_Longarm_X carrying ...Blaster.CustomLongarm is a Custom Longarm. Counting it
        // against Longarm would warn about a shelf it is not on and stay silent about the one it is.
        string EffectiveClass(ModCopy c, string named) =>
            c.RenameNames.TryGetValue(WeaponAuthor.ClassTag(named), out var to) &&
            to.Equals(WeaponAuthor.CustomClassTag(named), StringComparison.Ordinal)
                ? "Custom" + named : named;

        // A weapon routed into a custom class needs the mod that DEFINES that class. Without it the
        // weapon is in a class nothing declares, which does not error anywhere -- it simply never
        // appears, exactly like a mod that failed to install. Checked across every mounted
        // container, because the base mod is a mod like any other.
        var routed = mod.Copies
            .Select(c => WeaponAuthor.IdentityOf(c.Id) is { } who ? (c, who.Class) : (null, ""))
            .Where(x => x.Item1 is not null && EffectiveClass(x.Item1!, x.Item2) != x.Item2)
            .Select(x => "Custom" + x.Item2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var custom in routed)
        {
            var defines = provider.Files.Keys.Any(
                k => k.EndsWith($"/CPD_WeaponSpec_{custom.Insert(6, "_")}.uasset",
                                StringComparison.OrdinalIgnoreCase));
            if (defines)
                log?.Invoke($"  {custom}: defined by an installed mod, so this weapon has a shelf");
            else
                warnings.Add($"this weapon is routed into the {custom} class, and NOTHING INSTALLED " +
                             $"DEFINES IT -- install the '{WeaponAuthor.BaseModName}' mod, or the " +
                             "weapon ships correctly and never appears in the Armoury.");
        }

        foreach (var wClass in mod.Copies
                     .Select(c => WeaponAuthor.IdentityOf(c.Id) is { } who
                                      ? EffectiveClass(c, who.Class) : null)
                     .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var listed = WeaponIndex.ListedIn(provider, wClass);
            var mine = mod.Copies.Count(c => WeaponAuthor.IdentityOf(c.Id) is { } who &&
                                             EffectiveClass(c, who.Class)
                                                 .Equals(wClass, StringComparison.OrdinalIgnoreCase) &&
                                             !listed.Contains(c.Id, StringComparer.OrdinalIgnoreCase));
            var total = listed.Count + mine;
            // SAYS WHAT WAS SEEN, AND NOT THAT IT IS A LIMIT.
            //
            // This used to announce that the Armoury "was measured losing its FIRST row" past
            // seven, which read as a cap an author had to design around. It is not one. A long
            // list drops a row's NAME AND PICTURE while leaving it selectable, and which row goes
            // changes as you scroll and changes again on re-entering the screen -- the game's row
            // widget fades itself in with an animation on RenderOpacity, and a recycled row that
            // does not replay it stays invisible. Nothing is lost and nothing here can fix it.
            //
            // A warning that misdescribes a fault is worse than none: it sent an author looking
            // for a limit in their own mod, and it said "use a different class", which does not
            // help because custom shelves do it too.
            if (total > WeaponIndex.ClassHolds)
                warnings.Add($"{total} {wClass} weapons would exist once everything is unlocked. " +
                             "In a list this long the Armoury sometimes draws a row with no name " +
                             "and no picture -- it is still selectable, and which row it happens " +
                             "to be changes as you scroll. That is the game's own list widget " +
                             "reusing its rows, not anything wrong with your mod, and it happens " +
                             "on the game's own weapon classes too. Nothing needs fixing here.");
            else
                log?.Invoke($"  {wClass}: {total} weapon(s) would exist once everything is unlocked");
        }

        // COPIES NOTHING POINTS AT. They still build, still register, and a customization part
        // among them is still a candidate the game may offer to anyone -- which is exactly how
        // three droid weapons turned up in the recruitment offers of humans (2026-09-07). They
        // accumulate when a box is unticked and a differently-named replacement is made, and
        // nothing has ever said they were there.
        //
        // Reported, not removed: "unreferenced" is judged by whether the id appears anywhere else
        // in the project at all, which is deliberately crude. A copy reached only by a hand-written
        // path this does not parse would be pruned wrongly, and a mod that silently loses an asset
        // is a worse failure than one that carries a spare.
        {
            var text = mod.Serialize();
            // What a character's own features mint is pointed at by nothing ON PURPOSE -- the
            // Armoury finds a weapon class by scanning the registry, not by being told.
            var minted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in mod.Copies.Where(c => c.Id.StartsWith("CD_Char_", StringComparison.Ordinal)))
                foreach (var id in CharacterAuthor.FeatureIds(def)) minted.Add(id);
            var loose = new List<string>();
            foreach (var copy in mod.Copies)
            {
                if (string.IsNullOrWhiteSpace(copy.Id)) continue;
                if (FoundByTheGame(copy.Id)) continue;
                // Its own "id" line is the one mention every copy has.
                var mentions = 0;
                for (var at = text.IndexOf(copy.Id, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(copy.Id, at + 1, StringComparison.Ordinal)) mentions++;
                if (mentions <= 1 && !minted.Contains(copy.Id)) loose.Add(copy.Id);
            }
            looseCopies.AddRange(loose);
            if (loose.Count > 0)
                warnings.Add($"{loose.Count} cop(ies) nothing else in this mod points at, and they " +
                             $"still ship: {string.Join(", ", loose)}. A leftover customization part " +
                             "is still offered to characters, which is how droid weapons once turned " +
                             "up in everyone's recruitment. Delete them on the My mods page unless " +
                             "something outside this project names them.");
        }

        // MISSIONS, TRANSLATED INTO COPIES rather than built by a path of their own.
        //
        // A mission IS a duplicated game asset with properties changed, and everything below --
        // pristine reads, GUID swaps, name renames, import re-pointing, store entries, registry
        // entries, chunk emission -- is exactly what that needs. See MissionBuild, which does the
        // translating and nothing else. A second path beside this one would mean getting 500 lines
        // of hard-won handling right again for no gain.
        var missionCopies = new List<ModCopy>();
        var missionOverrides = new List<ModOverride>();
        if (mod.Missions.Count > 0)
        {
            var plan = MissionBuild.Translate(mod, provider);
            missionCopies = plan.Copies;
            missionOverrides = plan.Overrides;
            // EVERY NOTE IS A WARNING, not a log line. They are all of the form "you asked for X
            // and X did not happen" -- a fact that could not be substituted, a table row taken
            // from the mission that had it, a map whose objectives you have just inherited. A
            // mission that quietly builds without one of those is the failure mode this whole
            // feature has to avoid, because nothing in game says which mission you are looking at.
            foreach (var note in plan.Notes) warnings.Add(note);
            log?.Invoke($"  {mod.Missions.Count} mission(s) -> {missionCopies.Count} asset(s)");
        }

        foreach (var copy in mod.Copies.Concat(bankCopies).Concat(missionCopies))
        {
            log?.Invoke($"  copy {copy.Id}");

            if (string.IsNullOrWhiteSpace(copy.Id)) { Skip("a copy with no asset name"); continue; }
            if (string.IsNullOrWhiteSpace(copy.Template))
            { Skip($"{copy.Id}: no template to copy"); continue; }

            // A registry entry is OPTIONAL, because only PRIMARY assets have one. A weapon's
            // chain runs part -> gear kit -> actor, and only the part is enumerated; the other two
            // are reached by path and would be refused by a build that insisted on finding them in
            // the registry.
            var cpRegistered = templateEntry.TryGetValue(copy.Template, out var cpAt);
            if (!cpRegistered)
                log?.Invoke("      not a primary asset, so it ships without a registry entry");

            // PRISTINE, for the reason an override reads pristine: once this mod is installed its
            // own output is a candidate to be its own source.
            var cpSrc = AssetCopy.PristineRead(provider, copy.Template, out var cpWhy);
            if (cpSrc is null) { Skip($"{copy.Id}: {cpWhy}"); continue; }

            var cpBytes = cpSrc.Pieces[0].Data;
            var cpPackage = PackageNameOf(copy.Id, modFolder);
            var cpPath = $"{ProjectName}/{modFolder}{copy.Id}.uasset";
            var cpTags = cpRegistered ? decoded.PreallocatedAssetDataBuffers[cpAt].TagsAndValues : null;

            // ROWS ADDED TO A TABLE THIS MOD SHIPS AS ITS OWN.
            //
            // The same loop the override path runs, against the copy's bytes and the TEMPLATE's
            // package name -- the bytes are still the template's here; the rename happens below.
            //
            // This is what lets a recruit mod stop fighting. DR_InitialCharacterPool's sources
            // SEARCH $AssetSearchRoots/Game/Customizations/ for tables matching
            // *InitialCharacterPool, so a table of our own under that name is registered beside
            // the game's instead of replacing it, and two such mods can be installed together.
            var cpRowFailed = false;
            foreach (var add in copy.AddRows)
            {
                var cpRowBefore = cpBytes.Length;
                cpBytes = DataTableEdit.AddRow(provider, cpBytes, copy.Template, add.Row, add.From,
                                               add.Set, out var cpRowWhy, log);
                if (cpRowWhy is not null)
                {
                    Skip($"{copy.Id}: row '{add.Row}' was not added -- {cpRowWhy}");
                    cpRowFailed = true;
                    break;
                }
                log?.Invoke($"      + row {add.Row}, copied from {add.From} " +
                            $"({cpBytes.Length - cpRowBefore:+#;-#;0} bytes)");
            }
            if (cpRowFailed) continue;

            // THE NAME a player reads, before anything else. It is an FText in the export data,
            // so replacing it changes that export's length -- it has to be spliced while the
            // exports still describe the bytes being spliced, which is the same ordering rule the
            // part and override paths follow.
            if (!string.IsNullOrWhiteSpace(copy.Display) || !string.IsNullOrWhiteSpace(copy.Description))
            {
                var cpExports = AssetLoader.Read(() =>
                    PropertyAdd.PristineExports(provider, cpBytes, copy.Template));
                if (!string.IsNullOrWhiteSpace(copy.Display))
                {
                    // ANYWHERE it keeps one, not only on a UI fragment. The armoury part keeps its
                    // name where every part does; the GEAR KIT keeps it in GearKitInfo, one struct
                    // down, and that is the one the Armoury's MODEL header draws.
                    var cpNamed = DisplayNameEdit.SetTextAnywhere(
                        provider, cpBytes, copy.Template, cpExports, "DisplayName", copy.Display,
                        out var cpNameWhy, out var cpNameWhere);
                    if (cpNameWhy is not null) { Skip($"{copy.Id}: {cpNameWhy}"); continue; }
                    cpBytes = cpNamed;
                    log?.Invoke($"      display name: \"{copy.Display}\" on {cpNameWhere}");
                }

                // BOTH descriptions, the way a specialization gets them: walked to, because the
                // two share a string-table key and an anchor search cannot tell them apart; the
                // anchor is the fallback. Reported rather than refused -- a copy that kept its
                // template's description is telling the player it IS the template, which is worth
                // a warning and not a missing weapon.
                if (!string.IsNullOrWhiteSpace(copy.Description))
                    foreach (var which in new[] { "ShortDescription", "LongDescription" })
                    {
                        var owner = cpExports.FirstOrDefault(
                            e => e.Properties.Any(p => p.Name.Text == which));
                        string? descWhy;
                        if (owner is not null && PropertyAdd.Locate(provider, cpBytes, copy.Template,
                                owner.ExportType ?? "", which, out var dAt, out var dLen, out _))
                            cpBytes = DisplayNameEdit.SetTextAt(cpBytes, dAt, dLen, copy.Description, out descWhy);
                        else
                            cpBytes = DisplayNameEdit.SetText(cpBytes, cpExports, which, copy.Description, out descWhy);

                        if (descWhy is not null)
                            warnings.Add(descWhy.StartsWith("no ", StringComparison.Ordinal)
                                ? $"{copy.Id}: its template has no {which} to replace, so the description was not written"
                                : $"{copy.Id}: {which} stayed the template's -- {descWhy}");
                        else
                            log?.Invoke($"      {which}: \"{(copy.Description.Length > 48 ? copy.Description[..48] + "..." : copy.Description)}\"");
                    }
            }

            // THE COPY'S OWN WORDS, beyond its menu name: a name part's first name, last name and
            // biography. Found anywhere in the package by property name, the way the description
            // is, and the exports re-read before each one because every splice moves the next.
            var cpTextOk = true;
            foreach (var (property, value) in copy.Text)
            {
                // A BLANK IS WRITTEN AS EMPTY, not refused and not left as the template's. Until
                // 2026-09-07 a blank kept the template's text, and that made a B1 recruited off
                // the Recruits tab "B1 Battle Droid Bela" with a Neimoidian smuggler's biography:
                // the pool template's last name and story, on a droid that has neither. A blank
                // last name is meant (droids, mononyms) and a blank story shows nothing, which is
                // the honest thing until the author writes one; the character page says so.
                if (string.IsNullOrWhiteSpace(value))
                {
                    var clExports = AssetLoader.Read(() =>
                        PropertyAdd.PristineExports(provider, cpBytes, copy.Template));
                    var clOwner = clExports.FirstOrDefault(
                        e => e is not null && e.Properties.Any(p => p.Name.Text == property));
                    if (clOwner is not null && PropertyAdd.Locate(provider, cpBytes, copy.Template,
                            clOwner.ExportType ?? "", property, out var clAt, out var clLen, out _))
                    {
                        var cleared = DisplayNameEdit.ClearTextAt(cpBytes, clAt, clLen, out var clWhy);
                        if (clWhy is null)
                        {
                            cpBytes = cleared;
                            log?.Invoke($"      {property}: cleared");
                            continue;
                        }
                    }
                    warnings.Add($"{copy.Id}: {property} is blank and could not be emptied, so it " +
                                 $"keeps the template's ({Leaf(copy.Template)}'s)");
                    continue;
                }

                // WHICHEVER EXPORT OWNS IT. SetText knows the UI fragment and nothing else, and a
                // name part keeps its three texts on a CustomizationFragmentName -- so the owner is
                // found first and the property located against the template's schema, the way the
                // descriptions are; SetTextAnywhere is the fallback for one kept inside a struct.
                var cpTextExports = AssetLoader.Read(() =>
                    PropertyAdd.PristineExports(provider, cpBytes, copy.Template));
                var cpOwner = cpTextExports.FirstOrDefault(
                    e => e is not null && e.Properties.Any(p => p.Name.Text == property));
                string? cpTextWhy;
                string cpTextWhere;
                byte[] cpTexted;
                if (cpOwner is not null && PropertyAdd.Locate(provider, cpBytes, copy.Template,
                        cpOwner.ExportType ?? "", property, out var cpTextAt, out var cpTextLen, out _))
                {
                    cpTexted = DisplayNameEdit.SetTextAt(cpBytes, cpTextAt, cpTextLen, value, out cpTextWhy);
                    cpTextWhere = cpOwner.ExportType ?? "its export";
                }
                else
                    cpTexted = DisplayNameEdit.SetTextAnywhere(
                        provider, cpBytes, copy.Template, cpTextExports, property, value,
                        out cpTextWhy, out cpTextWhere!);
                if (cpTextWhy is not null)
                {
                    // A TEXT THAT WOULD NOT WRITE IS NOT WORTH THE WHOLE ASSET.
                    //
                    // This used to Skip, so one unwritable string threw away the mission, weapon
                    // or character it belonged to -- the author's first mission build produced
                    // nothing at all because of a briefing subtitle. The branch a few lines up,
                    // for a text that could not be BLANKED, has always warned and carried on;
                    // this one now matches it. A copy wearing its template's description is a
                    // reportable fault, not a reason to ship no copy.
                    warnings.Add($"{copy.Id}: {property} stayed as {Leaf(copy.Template)}'s -- " +
                                 cpTextWhy);
                    continue;
                }
                cpBytes = cpTexted;
                log?.Invoke($"      {property}: \"{(value.Length > 48 ? value[..48] + "..." : value)}\" on {cpTextWhere}");
            }
            if (!cpTextOk) continue;

            // THE PICTURE, written into the part the way the DC-17m's is. The texture is authored
            // from the author's file over whatever the template draws today -- the texture behind
            // its bank tag, or the one it names outright -- so the size and format are the game's
            // own; then both images on the UI fragment are switched to a soft brush naming it.
            // Reported, not refused: a copy that kept its template's picture is still a weapon.
            string? cpIconPackage = null;
            // The package this copy will import for its picture, and the slot that import takes in
            // the container's list. The slot is the CURRENT count because the store block below
            // only ever REPLACES entries before appending this one -- see there.
            ulong? cpExtraImport = null;
            var cpImportSlot = cpSrc.Store?.ImportedPackages.Count ?? 0;
            // A GAME texture, named by package path rather than a file to cook. `parts` has taken
            // one of these since the beginning (IconNeedsCooking); a copy could not, so "use that
            // weapon's picture" had no way to be said. It is also the only way to put a picture in
            // a copy's brush that the game already ships and can certainly load, which is what
            // separates "the brush is not read" from "our texture is not found".
            if (!string.IsNullOrWhiteSpace(copy.Icon) && !IconNeedsCooking(copy.Icon))
            {
                var cpGamePkg = PackageOf(copy.Icon) ?? copy.Icon;
                var cpGameId = Leaf(cpGamePkg);
                // SIZE, not shape: nothing is being written over this texture, so the format it
                // is in is none of this code's business -- and the one weapon part in the game
                // that draws a texture draws a BC7 one, which the authoring check refuses.
                var cpGameSize = TextureAuthor.SizeOf(provider, cpGamePkg);
                if (cpGameSize is null)
                    warnings.Add($"{copy.Id}: {copy.Icon} is not a texture this can read");
                else
                {
                    // Imported by its OWN hash: this is the game's texture, not a clone of one.
                    cpBytes = SetPicture(cpBytes, copy.Template, copy.Id, cpGamePkg, cpGameId,
                                         cpGameSize.Value.Width, cpGameSize.Value.Height,
                                         cpGamePkg, cpImportSlot, out cpExtraImport, out _);
                }
            }
            else if (!string.IsNullOrWhiteSpace(copy.Icon))
            {
                var cpImage = FindImage(copy.Icon, mod.FilePath);
                if (cpImage is null)
                    warnings.Add($"{copy.Id}: cannot find the image '{copy.Icon}' -- keeping the template's icon");
                else if (!TextureAuthor.IsSupportedImage(cpImage))
                    warnings.Add($"{copy.Id}: '{copy.Icon}' is not a PNG or TGA -- keeping the template's icon");
                else
                {
                    var cpDetail = PartDetails.Read(provider, copy.Template);
                    var cpTexTemplate = PackageOf(cpDetail.IconPath);
                    if (cpTexTemplate is null && cpDetail.IconBankTag is { } cpBankTag)
                    {
                        iconBanks ??= ImageBanks.Read(provider, ImageBanks.Candidates(provider), log);
                        cpTexTemplate = PackageOf(iconBanks.FirstOrDefault(
                            b => b.Tag.Equals(cpBankTag, StringComparison.OrdinalIgnoreCase))?.TexturePath);
                    }
                    if (cpTexTemplate is null)
                        warnings.Add($"{copy.Id}: its template draws no texture this can build on -- keeping its icon");
                    else
                    {
                        var cpTexId = "T_Icon_" + new string(copy.Id.Where(char.IsLetterOrDigit).ToArray());
                        var cpTexPackage = PackageNameOf(cpTexId, modFolder);
                        if (AuthorIconTexture(cpImage, cpTexTemplate, cpTexId, cpTexPackage, copy.Id) is { } cpShape)
                        {
                            cpBytes = SetPicture(cpBytes, copy.Template, copy.Id, cpTexPackage, cpTexId,
                                                 cpShape.Width, cpShape.Height, cpTexTemplate,
                                                 cpImportSlot, out cpExtraImport, out var cpImagesSet);
                            if (cpImagesSet > 0) cpIconPackage = cpTexPackage;
                        }
                    }
                }
            }

            // THE IDENTITY GUID, before the rename: the search is over the current bytes and a
            // rename moves the name batch. The guid to look for comes from the REGISTRY, which
            // prints it -- so this is a search for a known sixteen bytes rather than a guess at
            // where an FGuid lives.
            var cpNewGuid = "";
            if (copy.NewGuid)
            {
                var cpOldHex = cpTags?.FirstOrDefault(kv => kv.Key.Text == "Id").Value;
                if (!cpRegistered) cpOldHex = null;
                if (string.IsNullOrWhiteSpace(cpOldHex))
                    log?.Invoke("      no Id in the registry, so there is no identity to change");
                else if (GuidEdit.Bytes(cpOldHex) is not { } cpOld)
                    log?.Invoke($"      Id '{cpOldHex}' is not a guid this can read");
                else
                {
                    var cpGuid = GuidEdit.From(cpPackage);
                    var cpSwapped = GuidEdit.Set(cpBytes, cpOld, cpGuid, out var cpGuidAt, out var cpGuidWhy);
                    if (cpGuidWhy is not null) { Skip($"{copy.Id}: {cpGuidWhy}"); continue; }
                    cpBytes = cpSwapped;
                    cpNewGuid = GuidEdit.Hex(cpGuid);
                    log?.Invoke($"      id {cpOldHex} -> {cpNewGuid} at byte {cpGuidAt}");
                }
            }

            // The rename, and the author's own substitutions with it. Both halves of the package's
            // own name have to move -- the full path and the bare asset name -- for the reason
            // Author gives: renaming only the leaf leaves the copy in the game's own directory,
            // where the container has no chunk for it.
            // A NAMED PROPERTY IS EDITED BEFORE THE REWRITE, and a table row after it. They are
            // located by different machinery and they want opposite orders.
            //
            // PropertyAdd.Locate opens the bytes against the TEMPLATE's container header to read
            // its schema, so it works on the package as the game shipped it and stops working once
            // RenameNames and Write have moved things -- measured: the same export and property
            // that an override edits at byte 4523 comes back "is not a property of
            // BP_Rifle_DC-15A_C" when asked after the rewrite. The override loop has always run
            // its numbers first and its renames last, which is why this only ever bit copies.
            //
            // Safe in this order because a scalar edit is same-width: it moves no offset and
            // touches no name, so the rename pass that follows sees exactly what it would have.
            // The row walk stays where it was, after the rewrite, because IT locates by walking
            // the package as it then stands.
            // ADDING a property the template never serialised, before anything else touches the
            // bytes. Plan re-reads the export's schema and rebuilds its header, which moves every
            // offset after it -- so this has to happen before the named-value pass below locates
            // one, and long before the renames.
            //
            // This is the half a weapon like the E-5 needs: its actor sets no Blaster VFX Color at
            // all, so there is nothing to change and the colour can only be given.
            var cpAddsOk = true;
            foreach (var a in copy.AddScalars)
            {
                var cpAdded = PropertyAdd.AddScalar(provider, cpBytes, copy.Template,
                                                    a.Export, a.Name, a.Value, out var cpAddWhy);
                if (cpAddWhy is not null)
                {
                    Skip($"{copy.Id}: adding {a.Export}.{a.Name}: {cpAddWhy}");
                    cpAddsOk = false;
                    break;
                }
                cpBytes = cpAdded;
                log?.Invoke($"      + {a.Export}.{a.Name} = {a.Value}");
            }
            if (!cpAddsOk) continue;
            var cpNamesFailed = false;
            foreach (var n in copy.SetNames)
            {
                cpBytes = PropertyAdd.SetName(provider, cpBytes, copy.Template, n.Export, n.Property, n.Value, out var cpNameWhy);
                if (cpNameWhy is not null)
                { Skip($"{copy.Id}: {n.Export}.{n.Property} = {n.Value}: {cpNameWhy}"); cpNamesFailed = true; break; }
                log?.Invoke($"      {n.Export}.{n.Property} = {n.Value}");
            }
            if (cpNamesFailed) continue;

            // TAG CONTAINERS THE COPY EDITS, one at a time. Read from the copy's CURRENT bytes,
            // because GameplayTagEdit.Set finds a container by its full contents and a stale
            // reading of it would not be found. These are edits a RENAME cannot make: the two
            // containers hold the same FName, so renaming a class tag in one renames it in both.
            var cpTagsFailed = false;
            foreach (var edit in copy.TagEdits)
            {
                var cpTagExports = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, cpBytes, copy.Template));
                var cpHave = PartDetails.TagsIn(cpTagExports, edit.Property);

                // ABSENT, not empty: a part with no such container at all. The hero weapon specs
                // carry no AllowedSlots -- an ungated part simply has none -- so gating one means
                // ADDING the property, and the caller passes the whole contents it should hold
                // (its own slot tag included, or the part would no longer be offered for that
                // slot at all).
                if (cpHave.Count == 0)
                {
                    if (edit.Remove.Count > 0 || edit.Add.Count == 0)
                    { Skip($"{copy.Id}: no {edit.Property} to change tags in"); cpTagsFailed = true; break; }

                    // ABSENT, not empty -- an ungated part carries no AllowedSlots at all, so
                    // gating one means ADDING the property, and the caller passes the whole
                    // contents it should hold (its own slot tag included, or the part would no
                    // longer be offered for that slot at all).
                    //
                    // On the MAIN export: a part's containers live on the export named for the
                    // package, and PropertyAdd resolves an export by NAME before class, so the
                    // template's leaf is the right thing to hand it. A copy does not rename its
                    // exports, which is why the TEMPLATE's leaf and not the copy's id.
                    //
                    // This was registry-only for two days on the strength of a verifier that
                    // could not see the export it had just written -- AddTagContainer had refused
                    // its own correct writes on these specs with "the export is gone". Now that it
                    // does not, the PACKAGE carries the gate and the game enforces it, which is
                    // the half a registry entry never had.
                    var cpAddedTags = PropertyAdd.AddTagContainer(
                        provider, cpBytes, copy.Template, Leaf(copy.Template), edit.Property,
                        edit.Add, out var cpAddTagWhy);
                    if (cpAddTagWhy is null)
                    {
                        cpBytes = cpAddedTags;
                        log?.Invoke($"      + {edit.Property} = [{string.Join(", ", edit.Add)}] " +
                                    "- the package carries this gate now, so the game enforces it");
                        continue;
                    }

                    // Still the registry when the package will not take it, and that is the half
                    // that matters most anyway: the menus filter on the registry's
                    // CustomizationPartTags, which is how a weapon class is found at all. It fails
                    // OPEN -- the restriction is what a player is OFFERED rather than what the
                    // game will accept -- so it can never lock an owner out of his own part.
                    log?.Invoke($"      {edit.Property} is absent and the package will not take it " +
                                $"({cpAddTagWhy}), so the gate is the registry's alone: " +
                                $"[{string.Join(", ", edit.Add)}]");
                    continue;
                }
                var cpMissing = edit.Remove.Where(t => !cpHave.Contains(t, StringComparer.Ordinal)).ToList();
                if (cpMissing.Count > 0)
                { Skip($"{copy.Id}: {edit.Property} does not carry {string.Join(", ", cpMissing)}"); cpTagsFailed = true; break; }
                var cpWant = cpHave.Where(t => !edit.Remove.Contains(t, StringComparer.Ordinal)).ToList();
                foreach (var t in edit.Add)
                    if (!cpWant.Contains(t, StringComparer.Ordinal)) cpWant.Add(t);
                if (cpWant.SequenceEqual(cpHave, StringComparer.Ordinal)) continue;
                cpBytes = GameplayTagEdit.Set(cpBytes, cpHave, cpWant, out var cpTagWhy);
                if (cpTagWhy is not null) { Skip($"{copy.Id}: {edit.Property}: {cpTagWhy}"); cpTagsFailed = true; break; }
                log?.Invoke($"      {edit.Remove.Count} tag(s) out, {edit.Add.Count} in: " +
                            $"{edit.Property} now [{string.Join(", ", cpWant)}]");
            }
            if (cpTagsFailed) continue;
            if (copy.AddAnimations.Count > 0)
            {
                cpBytes = PropertyAdd.AddAnimationEntries(provider, cpBytes, copy.Template, copy.AddAnimations,
                                                          out var cpAnims, out var cpAnimWhy);
                if (cpAnimWhy is not null) { Skip($"{copy.Id}: adding animation entries: {cpAnimWhy}"); continue; }
                log?.Invoke($"      + {cpAnims} animation entr(ies), each a clone of one the set has");
            }

            // THE SLOTS of a copied character definition -- who they are. Written into the map
            // in place by SlotEdit, here with the other property edits: located against the
            // template's schema, so before the renames, and appending its own names, so before
            // anything that walks the package by offset. Refused rather than reported: a
            // character whose face stayed the template's is not the character that was asked for.
            if (copy.Slots.Count > 0)
            {
                var cpSlotted = SlotEdit.Set(provider, cpBytes, copy.Template, copy.Slots,
                                             out var cpSlotWhy, log);
                if (cpSlotWhy is not null) { Skip($"{copy.Id}: slots: {cpSlotWhy}"); continue; }
                cpBytes = cpSlotted;
                log?.Invoke($"      {copy.Slots.Count} slot(s) written and read back");
            }

            var cpNamedDone = new HashSet<int>();
            var cpAddWhys = new Dictionary<int, string>();
            for (var ni = 0; ni < copy.Numbers.Count; ni++)
            {
                var n = copy.Numbers[ni];
                var cpEarly = ScalarEdit.SetByName(provider, cpBytes, copy.Template, n.Row, n.Field,
                                                   n.Was, n.Now, out var cpEarlyKind,
                                                   out var cpEarlyAt, out var cpEarlyWhy);
                if (cpEarlyWhy is null)
                {
                    cpBytes = cpEarly;
                    cpNamedDone.Add(ni);
                    // "reads back", not "wrote": ScalarEdit walks the written package and refuses
                    // unless the property now reads the new value.
                    log?.Invoke($"      {n.Row}.{n.Field}: {n.Was} -> {n.Now}, reads back {n.Now} " +
                                $"as a {cpEarlyKind.ToString().ToLowerInvariant()} at byte {cpEarlyAt}");
                    continue;
                }

                // NOT THERE AT ALL, perhaps. A cooked package leaves out every property still at its
                // default, so "cannot find it" can mean "declared and absent" -- UIData.SortingPriority
                // on every enemy ability, which decides whether Force Grip keeps its place on a full
                // class row. AddScalar puts it back, inside its struct when the field is a path, and
                // reads back byte for byte; it refuses a number that IS present, so it cannot paper
                // over the refusal above. HERE, before the rewrite below renames the exports: the row
                // is addressed by the TEMPLATE's export name, which after it no longer exists. `was`
                // cannot be checked against bytes that do not exist, and the log says so.
                var cpAdded = PropertyAdd.AddScalar(provider, cpBytes, copy.Template, n.Row, n.Field,
                                                    n.Now, out var cpAddWhy);
                if (cpAddWhy is not null) { cpAddWhys[ni] = cpAddWhy; continue; }   // the row walk gets it
                cpBytes = cpAdded;
                cpNamedDone.Add(ni);
                log?.Invoke($"      {n.Row}.{n.Field}: added as {n.Now} -- it was absent, so at its default");
            }

            var cpPkg = ZenPackage.Read(cpBytes);
            var cpSubs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [copy.Template] = cpPackage,
                [copy.Template[(copy.Template.LastIndexOf('/') + 1)..]] = copy.Id,
            };
            // A soft object path is TWO names -- the package path and the asset name -- and an
            // author naming both is right even though a given package may hold only one of them.
            // So a missing name is reported and skipped, and the copy is refused only when NONE of
            // its re-points landed, which is the case that leaves it pointing at the original.
            var cpLanded = 0;
            foreach (var (from, to) in copy.RenameNames)
            {
                if (!cpPkg.Names.Contains(from, StringComparer.Ordinal))
                {
                    log?.Invoke($"      '{Leaf(from)}' is not in the name map, so it is left alone");
                    continue;
                }
                cpSubs[from] = to;
                cpLanded++;
                log?.Invoke($"      {Leaf(from)} -> {Leaf(to)}");
            }
            if (copy.RenameNames.Count > 0 && cpLanded == 0 && copy.RepointImports.Count == 0)
            {
                Skip($"{copy.Id}: none of its {copy.RenameNames.Count} re-point(s) are in " +
                     $"{Leaf(copy.Template)}'s name map -- the copy would still point at the " +
                     "original. If it names that asset as a hard IMPORT, use repointImports.");
                continue;
            }

            cpPkg.RenameNames(cpSubs);
            cpBytes = cpPkg.Write();

            // CHECKED HERE, because the verifier cannot check it. Its "points at the mod's own
            // assets" test counts hard IMPORTS, and a loadout names its parts with SOFT object
            // paths -- which never appear in an import map, so that test can only ever answer no.
            //
            // Read the names back out of the written bytes instead: the substitution either landed
            // in the map or it did not, and a copy still naming the original's parts is the same
            // asset under a different filename.
            // ENTRIES OF THE MOD'S OWN, added rather than taken over. After the renames, so the
            // names it looks up are the package as it now stands.
            foreach (var (optTag, optMesh) in copy.AddOptions)
            {
                var optPkg = PackageOf(optMesh) ?? optMesh;
                var optLeaf = Leaf(optPkg);
                cpBytes = PropertyAdd.AddMeshOption(provider, cpBytes, copy.Template,
                    "CustomizationFragmentMesh", optTag, optPkg, optLeaf, out var optWhy);
                if (optWhy is not null)
                    warnings.Add($"{copy.Id}: {Leaf(optTag)} was not added -- {optWhy}");
                else
                    log?.Invoke($"      + {Leaf(optTag)} wears {optLeaf}");
            }

            // NUMBERS, last of the edits that touch bytes: a float is four bytes for four bytes,
            // so it moves no offset -- but it is LOCATED by a row walk over the package as it now
            // stands, so it has to run after every rename and after cpPkg.Write().
            //
            // On a COPY rather than an override, which is the whole point: an override edits the
            // GAME's table and rebalances every weapon of that kind, the game's own included.
            var cpNumbersOk = true;
            for (var ni = 0; ni < copy.Numbers.Count; ni++)
            {
                // Already applied above, before the rewrite. See cpNamedDone.
                if (cpNamedDone.Contains(ni)) continue;
                var n = copy.Numbers[ni];
                // SetNumber, not SetFloat: a table field is a float, a double or an int, and a
                // float writer put float bytes over an int and destroyed it -- silently, because
                // both are four bytes wide. Found by sweeping all 852 of the game's tables.
                var cpByRow = DataTableEdit.SetNumber(provider, cpBytes, copy.Template, n.Row, n.Field,
                                                     n.Was, n.Now,
                                                     out var cpNumAt, out var cpRowWhy);
                if (cpRowWhy is null)
                {
                    cpBytes = cpByRow;
                    log?.Invoke($"      {n.Row}.{n.Field}: {n.Was} -> {n.Now} at byte {cpNumAt} (by row)");
                    continue;
                }

                // BY NAME, the same second chance the override loop has had all along -- and the
                // one that makes a property on a copied BLUEPRINT reachable at all. Only a
                // DataTable answers to a row walk; a weapon actor's `Blaster VFX Color` is a
                // property on an export, so a copy could carry the whole weapon and never change
                // the one byte that decides what colour it fires.
                //
                // Addressed by the TEMPLATE's export name: a copy keeps the names its template
                // had unless a rename moved them, so a copy of BP_Rifle_DC-15A still answers to
                // `Default__BP_Rifle_DC-15A_C` however the copy itself is called. `copy.Template`
                // is passed as the package for the same reason the override loop passes its
                // target -- Locate needs a mounted package to read the schema from.
                var cpByName = ScalarEdit.SetByName(provider, cpBytes, copy.Template, n.Row, n.Field,
                                                    n.Was, n.Now, out var cpKind, out cpNumAt,
                                                    out var cpNameWhy);
                if (cpNameWhy is null)
                {
                    cpBytes = cpByName;
                    // "reads back", not "wrote": ScalarEdit walks the written package and refuses
                    // unless the property now reads the new value, so this is a statement about
                    // the asset rather than about our intention.
                    log?.Invoke($"      {n.Row}.{n.Field}: {n.Was} -> {n.Now}, reads back " +
                                $"{n.Now} as a {cpKind.ToString().ToLowerInvariant()} " +
                                $"at byte {cpNumAt}");
                    continue;
                }

                // ALL THE reasons, because they are different questions. The row walk says "no such
                // row"; the name walk says what the property actually reads; the add, tried before
                // the rename, says why it could not be put back. One of them is the answer.
                Skip($"{copy.Id}: {n.Row}.{n.Field}: {cpNameWhy}   (as a table row: {cpRowWhy}" +
                     (cpAddWhys.TryGetValue(ni, out var cpAddWhy) ? $"; adding it: {cpAddWhy})" : ")"));
                cpNumbersOk = false;
                break;
            }
            if (!cpNumbersOk) continue;

            // A HARD IMPORT IS A PACKAGE AND A HASH, and moving only the package is why a copy
            // could never point at an asset this mod did not build itself. See
            // <see cref="ImportRepoint"/>, which owns both halves -- it was this code, lifted out
            // when an OVERRIDE turned out to need the same thing to take over a shop slot.
            if (cpSrc.Store is not null)
                cpBytes = ImportRepoint.MoveHashes(provider, cpBytes, cpSrc.Store,
                                                   copy.RepointImports,
                                                   to => CopyTarget(to, modFolder),
                                                   copy.Id, warnings, log,
                                                   package => HashSource(mod, package, missionCopies));

            var cpAfter = ZenPackage.Read(cpBytes);
            // Only the AUTHOR's re-points, not the automatic package rename. A package does not
            // necessarily carry its own name in its own map -- the name comes from the container
            // path -- so asserting that rename landed refused a copy that was entirely correct.
            var cpStale = copy.RenameNames
                .Where(kv => cpSubs.ContainsKey(kv.Key))
                .Select(kv => kv.Value)
                .Where(to => !cpAfter.Names.Contains(to, StringComparer.Ordinal)).ToList();
            if (cpStale.Count > 0)
            {
                Skip($"{copy.Id}: the re-point did not survive the rename -- " +
                     $"{string.Join(", ", cpStale.Select(Leaf))} is not in the copy's name map");
                continue;
            }
            if (cpLanded > 0)
                log?.Invoke($"      {cpLanded} re-point(s) present in the copy's name map");

            // TAGS ADDED TO AllowedSlots -- the simple, common-case sibling of the TagEdits loop
            // above, and the same edit ModPart.AddTags makes on a part (see there). ONE Set call,
            // the way the override loop's RemoveTags/AddTags pair does it (see there too):
            // GameplayTagEdit locates a container by its full contents, so a second call would be
            // hunting for a pattern the first had just changed.
            //
            // AFTER THE RENAME, not with TagEdits above: a tag the author renamed (a slot's own
            // name tag, the ATTACHMENTS container's whole reason to exist) has to read as its NEW
            // name in this log line, and GameplayTagEdit.Set -- like the row walk below it always
            // has -- reparses cpBytes fresh, so it sees the package as it now stands rather than a
            // stale reading from before the rename moved the name batch. Still before clearSlots
            // and "SLOTS LAST" below, both of which want the AllowedSlots edit already settled.
            if (copy.AddTags.Count > 0)
            {
                // cpPackage, headerFrom: copy.Template -- the same arguments ClearSlots and
                // SlotAdd.Add below pass. By now cpBytes carries the RENAMED package (cpPkg.
                // RenameNames(cpSubs) above), so packageName has to be the name it now answers to;
                // PristineExports's own headerFrom branch is what lets it still resolve the class
                // import against copy.Template's container. Without headerFrom this read the same
                // failure PristineExports's own doc describes: a Blueprint-classed copy (any
                // helmet, CPD_H_Outfit_Clo002_HELM's BP_CPD_H_Outfit_Mtc_Base_C among them) came
                // back with 0 properties and a null Class, so AllowedSlots read empty and every
                // such copy was refused -- only the native-classed Tops container was ever tested.
                var cpAddExports = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, cpBytes, cpPackage, copy.Template));
                var cpAddHave = PartDetails.TagsIn(cpAddExports, "AllowedSlots");
                if (cpAddHave.Count == 0)
                { Skip($"{copy.Id}: no AllowedSlots to add tags to"); continue; }

                var cpAddWant = cpAddHave.ToList();
                foreach (var t in copy.AddTags)
                    if (!cpAddWant.Contains(t, StringComparer.Ordinal)) cpAddWant.Add(t);

                if (!cpAddWant.SequenceEqual(cpAddHave, StringComparer.Ordinal))
                {
                    cpBytes = GameplayTagEdit.Set(cpBytes, cpAddHave, cpAddWant, out var cpAddTagWhy);
                    if (cpAddTagWhy is not null) { Skip($"{copy.Id}: {cpAddTagWhy}"); continue; }
                    log?.Invoke($"      AllowedSlots now [{string.Join(", ", cpAddWant)}]");
                }
            }

            // CLEAR SLOTS NEXT, still before "SLOTS LAST" below -- the ATTACHMENTS container
            // starts life as a copy of the game's Tops container, and its five Tops slots have
            // to go before the mod's own are added, or the wheel would offer both. The export
            // name is copy.Id, same as the slot-set lookup just below uses -- a copy's own
            // export is renamed to copy.Id by the cpSubs rename above, whether or not this copy
            // also gets a slot set of its own.
            if (copy.ClearSlots)
            {
                cpBytes = SlotAdd.ClearSlots(provider, cpBytes, cpPackage, copy.Id,
                                             out var cpCleared, out var cpClearWhy, headerFrom: copy.Template);
                if (cpClearWhy is not null) { Skip($"{copy.Id}: clearSlots: {cpClearWhy}"); continue; }
                log?.Invoke($"      {cpCleared} slot(s) cleared");
            }

            // SLOTS LAST, on the finished bytes -- same reasoning as the parts loop above. Matched
            // by copy.Id, not the template's leaf: a copy's own export is renamed to copy.Id by the
            // cpSubs rename just above (cpPkg.RenameNames(cpSubs)), same as a part's is to part.Id.
            if (slotLandings.FirstOrDefault(l => !l.IsGamePart && l.Target.Equals(copy.Id, StringComparison.OrdinalIgnoreCase))
                is { } cpSlotSet)
            {
                cpBytes = SlotAdd.Add(provider, cpBytes, cpPackage, cpSlotSet.PartExport, cpSlotSet.Slots,
                                      out var cpSlotSetAdded, out var cpSlotSetWhy, headerFrom: copy.Template, log: log);
                if (cpSlotSetWhy is not null) { Skip($"{copy.Id}: slots: {cpSlotSetWhy}"); continue; }
                log?.Invoke($"      {cpSlotSetAdded.Count} slot(s) added: " + string.Join(", ", cpSlotSetAdded.Select(a => a.Id)));
                slotStamps.Add(new SlotStamp(cpSlotSet.Target, cpPackage, cpSlotSet.PartExport, cpSlotSet.Slots));
            }

            var cpChunk = PackageId.Of(cpPackage);
            chunks.Add(new IoStoreWriter.Chunk(cpChunk, 0, 1 /* ExportBundleData */, cpBytes, cpPath));
            foreach (var piece in cpSrc.Pieces.Where(x => !x.IsPackage))
                chunks.Add(new IoStoreWriter.Chunk(cpChunk, piece.Index, piece.Type, piece.Data, null));

            // Redirected stays 0: it means HARD-import redirects to the verifier, and these are
            // soft. Claiming them here would fail a check that is asking a different question.
            built.Add(new BuiltPart(copy.Id, cpPackage, cpChunk, cpPath, cpBytes.Length,
                                    copy.Template, IsCopy: true, IconPackage: cpIconPackage));

            if (cpSrc.Store is not null)
            {
                // The imports, with the asked-for redirects applied. An import is a package ID in
                // the store entry, so this is where a hard reference actually moves -- the same
                // swap the part loop makes when an authored icon replaces the template's.
                var cpImports = ImportRepoint.MovePackages(cpSrc.Store, copy.RepointImports,
                                                           to => CopyTarget(to, modFolder),
                                                           copy.Id, warnings, log);
                // The picture's package, appended -- NOT inserted. Every FPackageObjectIndex in
                // the import map names a package by INDEX, so putting one in the middle would
                // silently repoint every import after it.
                if (cpExtraImport is { } cpAdded)
                {
                    cpImports.Add(cpAdded);
                    log?.Invoke($"      imports its own picture as package [{cpImports.Count - 1}]");
                }
                storeFor[cpChunk] = new ContainerHeader.StoreEntry(cpImports, cpSrc.Store.ShaderMapHashes);
            }
            else warnings.Add($"{copy.Id}: no store entry to carry, so its imports are unknown");

            // The registry entry, which is what the game SCANS. Its class comes from the template
            // -- inventing one files the copy under a type nothing asks for -- and two tags are
            // replaced: the primary name, and the identity guid if one was written.
            if (!cpRegistered) continue;

            var cpRecord = records[cpAt];
            var cpEntryTags = new List<(string, string)>();
            foreach (var kv in cpTags ?? new Dictionary<CUE4Parse.UE4.Objects.UObject.FName, string>())
            {
                var value = kv.Value ?? "";
                if (kv.Key.Text == "PrimaryAssetName") value = copy.Id;
                else if (kv.Key.Text == "Id" && cpNewGuid.Length > 0) value = cpNewGuid;
                else foreach (var (from, to) in copy.RenameNames)
                    value = value.Replace(from, to, StringComparison.Ordinal);

                // AND THE SAME EDIT THE PACKAGE'S AllowedSlots GOT. The registry's
                // CustomizationPartTags is a cook-time projection of AllowedSlots, and it is what
                // the menus filter on -- so a class tag moved in the package and not here leaves a
                // part that is in one class to the game and another to the Armoury. Two sources of
                // that edit now: the general tagEdits loop above, and the simpler addTags every
                // copy has (ModCopy.AddTags) -- both mean the same registry rewrite, so both are
                // folded into the one pass rather than parsing CustomizationPartTags twice.
                var cpSlotEdit = copy.TagEdits.FirstOrDefault(e => e.Property == "AllowedSlots");
                if (kv.Key.Text == "CustomizationPartTags" &&
                    (cpSlotEdit is not null || copy.AddTags.Count > 0))
                {
                    var cpKept = PartsIndex.Describe("", copy.Id, null, value).Tags
                        .Where(t => cpSlotEdit is null || !cpSlotEdit.Remove.Contains(t, StringComparer.Ordinal))
                        .ToList();
                    foreach (var t in cpSlotEdit?.Add ?? [])
                        if (!cpKept.Contains(t, StringComparer.Ordinal)) cpKept.Add(t);
                    // addTags, checked against the GROWING cpKept rather than a snapshot, so a tag
                    // listed twice within addTags lands once -- the dedupe ModPart.AddTags's own
                    // registry half (PartTagsFor's lazy Concat) is missing, ledgered there rather
                    // than fixed, because that code is outside this change.
                    foreach (var t in copy.AddTags)
                        if (!cpKept.Contains(t, StringComparer.Ordinal)) cpKept.Add(t);
                    value = TagContainer(cpKept);
                }
                cpEntryTags.Add((kv.Key.Text, value));
            }

            Register(new RegistryAssets.NewAsset(
                PackageName: cpPackage,
                AssetName: copy.Id,
                PackagePath: cpPackage[..cpPackage.LastIndexOf('/')],
                ClassPackage: registry.Names[(int) cpRecord.ClassPackage],
                ClassName: registry.Names[(int) cpRecord.ClassName],
                Tags: cpEntryTags));

            log?.Invoke($"      registered as {registry.Names[(int) cpRecord.ClassName]}");
        }

        // SPECIALIZATIONS: parts with no geometry, authored from one the game ships.
        //
        // Most of the part loop above is absent here and none of it is missed. There is no template
        // to RANK -- the author picked one in the Specs tab, which is the whole point of that tab --
        // no mesh to substitute, no icon to cook, and no body types to keep consistent. What is left
        // is the half that decides whether the menu ever offers the thing: the registry entry, which
        // is exactly the part loop's, built from exactly the same three pieces.
        //
        // The route itself is proved. A specialization authored this way ("Vanguard") appeared on
        // the SPECIALIZATION tab and the game's own recruit generator rolled it onto new recruits --
        // so a spec that carries the right slot tag in the registry is a first-class member of the
        // game's own pool, not something the menu merely tolerates.
        foreach (var sp in mod.Specs)
        {
            log?.Invoke($"  specialization {sp.Id}");

            if (string.IsNullOrWhiteSpace(sp.Id)) { Skip("a specialization with no asset name"); continue; }
            if (string.IsNullOrWhiteSpace(sp.Template))
            { Skip($"{sp.Id}: no template to copy -- start one from the Specs tab"); continue; }

            var specPackage = PackageNameOf(sp.Id, modFolder);
            var specPath = $"{ProjectName}/{modFolder}{sp.Id}.uasset";

            // The registry entry is settled BEFORE anything is written, for the reason the part
            // loop gives: a package that reaches the container without one is in the mod and
            // invisible, because the customization system enumerates the registry, not the files.
            if (!templateEntry.TryGetValue(sp.Template, out var specAt))
            { Skip($"{sp.Id}: the template {sp.Template} has no registry entry to copy"); continue; }

            if (templateEntry.ContainsKey(specPackage))
                warnings.Add($"{sp.Id}: the registry already lists it -- this looks like a " +
                             "previously deployed build, so pass the game's pristine AssetRegistry.bin");

            var authored = SpecAuthor.Author(provider, sp.Template, sp, specPackage, out var specWhy, log);
            if (authored is null) { Skip($"{sp.Id}: {specWhy}"); continue; }

            log?.Invoke($"      from {sp.Template}");
            foreach (var applied in authored.Applied) log?.Invoke($"      {applied}");
            foreach (var w in authored.Warnings) warnings.Add($"{sp.Id}: {w}");

            // The picker's own map is a separate asset and is NOT what puts a specialization on the
            // tab -- Vanguard reached both the tab and the recruit generator without it, and the
            // game's two hero specs are absent from the map and get rolled anyway. Said here rather
            // than left as an unticked box, because a checkbox that does nothing is worse than one
            // that is not offered.
            if (sp.AddToPicker)
                log?.Invoke("      the picker finds it through its registry slot tag, not through " +
                            "the picker's map -- nothing extra to build");

            var specBytes = authored.Bytes;
            ulong? specExtraImport = null;

            // A PICTURE OF ITS OWN, rather than a tag. A specialization names its icon by TAG, and
            // a tag no bank of the GAME's defines draws the fallback -- a bank the mod ships is not
            // read. So the same treatment a weapon's armoury part gets: a soft brush WITH the hard
            // reference the menus actually read, written over whatever the tag resolves to today so
            // the size and format stay the game's own.
            if (!string.IsNullOrWhiteSpace(sp.Icon))
            {
                var specDetail = PartDetails.Read(provider, sp.Template);
                var specTexTemplate = PackageOf(specDetail.IconPath);
                if (specTexTemplate is null && specDetail.IconBankTag is { } specBankTag)
                {
                    iconBanks ??= ImageBanks.Read(provider, ImageBanks.Candidates(provider), log);
                    specTexTemplate = PackageOf(iconBanks.FirstOrDefault(
                        b => b.Tag.Equals(specBankTag, StringComparison.OrdinalIgnoreCase))?.TexturePath);
                }
                var specSlot = store.TryGetValue(PackageId.Of(sp.Template), out var specStoreFirst)
                    ? specStoreFirst.ImportedPackages.Count : -1;

                if (!IconNeedsCooking(sp.Icon))
                {
                    // A texture the game already ships, named outright.
                    var specGamePkg = PackageOf(sp.Icon) ?? sp.Icon;
                    var specGameSize = TextureAuthor.SizeOf(provider, specGamePkg);
                    if (specGameSize is null)
                        warnings.Add($"{sp.Id}: {sp.Icon} is not a texture this can read -- keeping its icon");
                    else
                        specBytes = SetPicture(specBytes, sp.Template, sp.Id, specGamePkg,
                                               Leaf(specGamePkg), specGameSize.Value.Width,
                                               specGameSize.Value.Height, specGamePkg, specSlot,
                                               out specExtraImport, out _);
                }
                else if (FindImage(sp.Icon, mod.FilePath) is not { } specImage)
                    warnings.Add($"{sp.Id}: cannot find the image '{sp.Icon}' -- keeping its icon");
                else if (!TextureAuthor.IsSupportedImage(specImage))
                    warnings.Add($"{sp.Id}: '{sp.Icon}' is not a PNG or TGA -- keeping its icon");
                else if (specTexTemplate is null)
                    warnings.Add($"{sp.Id}: its template draws no texture this can build on -- keeping its icon");
                else
                {
                    var specTexId = "T_Icon_" + new string(sp.Id.Where(char.IsLetterOrDigit).ToArray());
                    var specTexPackage = PackageNameOf(specTexId, modFolder);
                    if (AuthorIconTexture(specImage, specTexTemplate, specTexId, specTexPackage, sp.Id) is { } specShape)
                        specBytes = SetPicture(specBytes, sp.Template, sp.Id, specTexPackage, specTexId,
                                               specShape.Width, specShape.Height, specTexTemplate,
                                               specSlot, out specExtraImport, out _);
                }
            }

            var specChunk = PackageId.Of(specPackage);
            chunks.Add(new IoStoreWriter.Chunk(specChunk, 0, 1 /* ExportBundleData */,
                                               specBytes, specPath));
            // WHAT THE DIFF IS TOLD WAS ASKED FOR: the name substitutions the author made, plus
            // the TEXT it rewrote. A display name and a description are FText values, not names,
            // so they never appear in a rename map -- and the diff reported the three things the
            // author was explicitly asked to change as three things nobody asked for.
            var specIntent = new Dictionary<string, string>(authored.Substitutions, StringComparer.Ordinal);
            var specWas = PartDetails.Read(provider, sp.Template);
            if (!string.IsNullOrWhiteSpace(sp.Display) && specWas.DisplayName is { Length: > 0 } dWas)
                specIntent[dWas] = sp.Display;
            // Both descriptions become the one the author gave, and only the SHORT one is read
            // back here -- so the long one is matched by prefix below rather than left to read as
            // a change nobody asked for.
            if (!string.IsNullOrWhiteSpace(sp.Description))
            {
                if (specWas.ShortDescription is { Length: > 0 } sWas) specIntent[sWas] = sp.Description;
                // KNOWN GAP: this reads null on the specs tried so far even though the property is
                // plainly there, so the long description the author asked to change still shows in
                // --diff as a change nobody asked for. It is visible as exactly that in the output
                // -- was the template's paragraph, now the author's -- so it misleads nobody; it is
                // noise in the instrument, not a fault in the builder. Not chased further.
                if (specWas.LongDescription is { Length: > 0 } lWas) specIntent[lWas] = sp.Description;
            }

            built.Add(new BuiltPart(sp.Id, specPackage, specChunk, specPath,
                                    specBytes.Length, sp.Template,
                                    Substitutions: specIntent));

            // The template's imports, carried over. A spec's abilities and gear kits are SOFT
            // references and are not imports at all, so re-pointing them moves nothing here -- what
            // the entry actually declares is the Blueprint base class and the string table, which
            // the copy still uses. An empty entry would crash the loader with no message; see
            // StoreEntries.
            if (store.TryGetValue(PackageId.Of(sp.Template), out var specStore))
            {
                var specImports = specStore.ImportedPackages.ToList();
                // Appended, never inserted: an FPackageObjectIndex names its package by INDEX.
                if (specExtraImport is { } specAdded)
                {
                    specImports.Add(specAdded);
                    log?.Invoke($"      imports its own picture as package [{specImports.Count - 1}]");
                }
                storeFor[specChunk] = new ContainerHeader.StoreEntry(specImports, specStore.ShaderMapHashes);
                log?.Invoke($"      imports {specImports.Count} package(s), carried over");
            }
            else warnings.Add($"{sp.Id}: the template has no store entry, so its imports are " +
                              "unknown -- the specialization may fail to load");

            var specRecord = records[specAt];
            // Renamed and de-gated the same way a part's entry is: PrimaryAssetName becomes this
            // asset's own -- two assets answering to one id is how every early build vanished --
            // and the Accepts.* tags come off, which is what opens a developer gate.
            var specTags = PartTagsFor(decoded.PreallocatedAssetDataBuffers[specAt].TagsAndValues,
                                       sp.Id, onlyFor: string.IsNullOrWhiteSpace(sp.ForCharacter)
                                                       ? null : sp.ForCharacter.Trim());

            // The character gate, off the REGISTRY copy as well as the package. The menu filters on
            // the registry, so removing a name tag from one and not the other leaves the copy still
            // restricted to the hero it came from -- measured, not assumed.
            if (string.IsNullOrWhiteSpace(sp.ForCharacter))
            {
                for (var i = 0; i < specTags.Count; i++)
                {
                    if (specTags[i].Item1 != "CustomizationPartTags") continue;
                    var kept = PartsIndex.Describe("", sp.Id, null, specTags[i].Item2).Tags
                        .Where(t => !t.StartsWith(PartsIndex.NamePrefix, StringComparison.Ordinal));
                    specTags[i] = ("CustomizationPartTags", TagContainer(kept));
                }
            }

            Register(new RegistryAssets.NewAsset(
                PackageName: specPackage,
                AssetName: sp.Id,
                PackagePath: specPackage[..specPackage.LastIndexOf('/')],
                ClassPackage: registry.Names[(int) specRecord.ClassPackage],
                ClassName: registry.Names[(int) specRecord.ClassName],
                Tags: specTags));

            // Shown, not merely written. This is the half that decides who the game offers it to
            // -- the package says what a specialization IS and the registry is what the menu
            // filters on -- and it is the half nobody can inspect after the fact without a
            // separate tool.
            log?.Invoke("      registry: " +
                        specTags.FirstOrDefault(x => x.Item1 == "CustomizationPartTags").Item2);

            // Describe the entry that was just WRITTEN, with the code that describes the game's own
            // parts. A specialization that is gated or restricted is invisible in exactly the way
            // one that failed to install is, so a build reporting itself clean while shipping a
            // locked spec is worse than one that fails.
            var specWritten = PartsIndex.Describe(
                specPackage, sp.Id, registry.Names[(int) specRecord.ClassName],
                specTags.FirstOrDefault(x => x.Item1 == "CustomizationPartTags").Item2);

            if (specWritten.Gate != "open")
                warnings.Add($"{sp.Id}: built from {sp.Template}, which is gated behind " +
                             $"'{specWritten.Gate}' -- it will not appear in the menu");
            if (specWritten.Characters.Count > 0 && string.IsNullOrWhiteSpace(sp.ForCharacter))
                warnings.Add($"{sp.Id}: still restricted to {string.Join(", ", specWritten.Characters)} " +
                             "by the template it was copied from -- nobody else will be offered it");
            if (specWritten.Slots.Count == 0)
                warnings.Add($"{sp.Id}: carries no slot tag, so nothing will offer it");

            var specPrimary = specTags.FirstOrDefault(x => x.Item1 == "PrimaryAssetName").Item2;
            if (specPrimary != sp.Id)
                warnings.Add($"{sp.Id}: registered under the name '{specPrimary}', which is not its " +
                             "own -- it will collide with whatever owns that name");

            // A focus tree is what makes a specialization SELECTABLE. Anakin's and Rex's kits have
            // no leveled ability lines at all and the picker will not offer them however open their
            // tags are -- three separate attempts went into gates before that was measured. So a
            // copy of one is worth saying something about rather than shipping quietly.
            if (sp.RungCount == 0)
                warnings.Add($"{sp.Id}: it has no focus tree. The picker draws a three-line tree " +
                             "and a specialization that cannot fill one is never offered -- the " +
                             "hero kits (Rex, Anakin) are like this, and copying one inherits it");
        }

        // OVERRIDES: the game's own packages, shipped changed at their own ids.
        //
        // Almost nothing above applies. There is no template to choose (the target IS the
        // template), no registry entry to append (the game already lists this package, and a
        // second entry for one package id is the duplicate-PrimaryAssetId fault), no icon, no
        // mesh, and no rename -- an override keeps its own name, which is the entire point.
        //
        // What it gains instead is a failure mode a new part does not have. A part that goes wrong
        // does not appear; an override that goes wrong replaces a working game asset with a broken
        // one. So every step here refuses rather than warns.
        // THE MISSION TABLES RIDE WITH THE AUTHOR'S OWN OVERRIDES, because that is what they are:
        // an edit shipped at the GAME's package path. See MissionBuild.Plan.Overrides.
        foreach (var ov in mod.Overrides.Concat(missionOverrides).Concat(slotOverrides))
        {
            var label = string.IsNullOrWhiteSpace(ov.Target) ? "(no target)" : ov.Target;
            log?.Invoke($"  override {label}");

            if (string.IsNullOrWhiteSpace(ov.Target)) { Skip("an override with no target"); continue; }

            // ANOTHER INSTALLED MOD ALREADY CHANGES THIS. A WARNING, NEVER A REFUSAL.
            //
            // Two mods changing one asset is a reasonable thing to be making -- two variants of
            // the same idea, or a spare copy kept installed. What is not reasonable is finding out
            // during a play session that one of them appears to do nothing, which is exactly what
            // happens: both install, both verify, both read back clean, and the game reads
            // whichever container it mounted last.
            //
            // It could not happen while a mod only ADDED things. Replacements and table editing
            // both change the game's own asset in place, so they ship the game's own package id --
            // which is the mechanism, and also what makes two of them indistinguishable.
            if (ModConflicts.Besides(provider, ov.Target, null, mod.Name, paksDirectory)
                    is { Count: > 0 } clash)
            {
                warnings.Add($"{ov.Target}: {ModConflicts.Sentence(clash)}");
                log?.Invoke($"      ALSO CHANGED BY {string.Join(", ", clash.Select(c => c.ModName))}");
            }

            // PRISTINE, not the first container that answers. An override ships the game's own
            // package id, so once it is installed it is a candidate to be its own source, and
            // every rebuild would stack another edit on the last one.
            var src = AssetCopy.PristineRead(provider, ov.Target, out var srcWhy);
            if (src is null) { Skip($"{ov.Target}: {srcWhy}"); continue; }

            // The id needs no cross-check and it would be dishonest to write one: AssetCopy FINDS
            // the package by PackageId.Of(name), so comparing that hash against what it returned
            // can never fail. The protection against a mistyped target is the lookup itself --
            // a name that hashes to an id no container holds comes back null above, with a reason.
            var ovId = src.PackageId;

            // What DOES need checking, because nothing else establishes it: the path. The id is
            // what the loader resolves, but the container's directory index is keyed by path, and
            // ContainerPathFor derives ours by string surgery on the package name. If it derives
            // a path the game does not use, the override ships the right bytes under the right id
            // at the wrong name -- and every tool that looks it up by path, this build's own
            // read-back included, would be answering about a different file.
            var ovPath = ContainerPathFor(ov.Target);
            if (!provider.Files.ContainsKey(ovPath))
            {
                // A LEVEL IS FILED AS .umap. ContainerPathFor appends .uasset because every asset
                // edited here until now was one, and a sublevel is the first target that is not.
                // Both are tried against the game's own directory index rather than guessed from
                // the path, so the answer is the game's and not a rule about names.
                var ovAsMap = ovPath[..^".uasset".Length] + ".umap";
                if (provider.Files.ContainsKey(ovAsMap)) ovPath = ovAsMap;
                else
                {
                    Skip($"{ov.Target}: the game has no file at '{ovPath}' or '{ovAsMap}', which " +
                         "is where this override would be filed -- the derived container path is wrong");
                    continue;
                }
            }
            log?.Invoke($"      from {src.Container}, id 0x{ovId:X16}, {src.Pieces.Count} chunk(s)");

            var ovRaw = src.Pieces[0].Data;
            var ovBytes = ovRaw;

            // PIXELS, when the target is a texture.
            //
            // The same two branches MaterialSkin.Repaint uses, because this game's textures come
            // both ways and the difference is not cosmetic: a VIRTUAL texture's pages live in a
            // .ubulk beside the package and a plain one's mips are inline. Describe reads which,
            // Author encodes to that exact shape, and Swap writes the package AND its payloads.
            //
            // This produces CHUNKS rather than bytes, so it replaces the whole override for this
            // target -- there is nothing else to do to a texture.
            if (!string.IsNullOrWhiteSpace(ov.Image))
            {
                var ovImg = FindImage(ov.Image, mod.FilePath) ?? ov.Image;
                if (!File.Exists(ovImg))
                { Skip($"{ov.Target}: cannot find the image '{ov.Image}'"); continue; }

                List<IoStoreWriter.Chunk>? ovMade;
                string? ovImgWhy;
                if (VirtualTexture.Describe(provider, ov.Target, out _) is { } ovVt)
                {
                    var ovPages = VirtualTexture.Author(ovVt, ovImg, null, out ovImgWhy, log);
                    ovMade = ovPages is null ? null
                           : VirtualTexture.Swap(src, ovVt, ovPages, ov.Target, ovPath, out ovImgWhy, log);
                }
                else if (TextureImport.Describe(provider, ov.Target, out ovImgWhy) is { } ovPlain)
                {
                    var ovMip = TextureImport.Author(ovPlain, ovImg, null, out ovImgWhy, log);
                    ovMade = ovMip is null ? null
                           : TextureImport.Swap(src, ovPlain, ovMip, ov.Target, ovPath, out ovImgWhy, log);
                }
                else ovMade = null;

                if (ovMade is null) { Skip($"{ov.Target}: {ovImgWhy}"); continue; }

                chunks.AddRange(ovMade);
                if (src.Store is not null) storeFor[ovId] = src.Store;
                built.Add(new BuiltPart(ovLeafOf(ov.Target), ov.Target, ovId, ovPath,
                                        ovMade.Sum(c => c.Data.Length), ov.Target,
                                        IsTexture: true, IsCopy: true, IsOverride: true));
                log?.Invoke($"      pixels from {Path.GetFileName(ovImg)} " +
                            $"({ovMade.Count} chunk(s))");
                continue;
            }

            // Package ids this override's material slots add to its imports. Collected during
            // the mesh import and folded into the store entry at the end, where the entry is
            // written -- appended rather than replacing, so a re-point that MOVES existing entries
            // and a slot that ADDS one compose instead of one losing the other.
            var ovMaterialImports = new List<ulong>();

            // GEOMETRY FIRST, before any other edit.
            //
            // The import rewrites the export's vertex and index buffers, which moves everything
            // after them. Every other edit here locates its target by an offset or a byte pattern,
            // so all of them have to run against the RESULT of this rather than the other way
            // round -- the same ordering rule the part path follows, for the same reason.
            if (!string.IsNullOrWhiteSpace(ov.MeshFile))
            {
                var ovFile = FindImage(ov.MeshFile, mod.FilePath) ?? ov.MeshFile;
                if (!File.Exists(ovFile))
                { Skip($"{ov.Target}: cannot find the mesh file '{ov.MeshFile}'"); continue; }

                // WHAT EACH SLOT WEARS. The gap this closes is narrower than it was reported and
                // worth stating exactly: replacing a one-material helmet with a six-material model
                // ALREADY grew the array -- "material slots 1 -> 6", every section wearing its own
                // slot. What it could not do was say what the new slots wear, so all of them fell
                // back to the template's first material and the model arrived painted in the
                // body's paint. From outside that looks like being held to the game's slot count.
                //
                // A slot NAMES a game material and the mesh gains one package import, exactly as
                // the part path does it. Repainted copies are the part path's other half and are
                // deliberately not here: a replacement's textures are already separate overrides
                // that repaint the game's own texture in place, so a copy would be a second
                // mechanism for a job that has one.
                foreach (var slot in ov.Materials.Where(m => m.Textures.Count > 0))
                    warnings.Add($"{ov.Target}: a material slot names textures of its own, which a " +
                                 "replacement does not do -- replace the texture itself instead, " +
                                 "under Materials, and everything using it changes with it");

                var ovSlotMaterials = new List<(int Slot, string Package, int Import)>();
                if (src.Store is not null)
                {
                    var have = new List<ulong>(src.Store.ImportedPackages);
                    for (var slot = 0; slot < ov.Materials.Count; slot++)
                    {
                        var named = ov.Materials[slot].Material;
                        if (named.Length == 0) continue;
                        var pkg = PackageOf(named) ?? named;

                        // Already imported is the common case -- the game's own material, which
                        // the mesh it is being put on may well reference already -- and importing
                        // it twice would leave the second entry unreferenced.
                        var already = have.IndexOf(PackageId.Of(pkg));
                        if (already >= 0) { ovSlotMaterials.Add((slot, pkg, already)); continue; }
                        ovSlotMaterials.Add((slot, pkg, have.Count));
                        have.Add(PackageId.Of(pkg));
                        ovMaterialImports.Add(PackageId.Of(pkg));
                    }
                    if (ovSlotMaterials.Count > 0)
                        log?.Invoke($"      {ovSlotMaterials.Count} slot(s) name a material: " +
                                    string.Join(", ", ovSlotMaterials.Select(
                                        x => $"#{x.Slot} {Leaf(x.Package)}")));
                }
                else if (ov.Materials.Any(m => m.Material.Length > 0))
                    warnings.Add($"{ov.Target}: its slots name materials and there is no store " +
                                 "entry to declare them in, so they are not applied");

                // The template for the import is the target ITSELF -- it is the mesh being
                // replaced, so its LOD counts, its bone map and its vertex-colour flag are exactly
                // the shape the new geometry has to fit.
                var ovImported = ImportMesh(provider, ov.Target,
                                            src with { Pieces = src.Pieces }, new ModPart
                                            {
                                                Id = ovLeafOf(ov.Target),
                                                MeshFile = ov.MeshFile,
                                                MeshScale = ov.MeshScale,
                                                MeshBasis = ov.MeshBasis,
                                                MeshOffsetX = ov.MeshOffsetX,
                                                MeshOffsetY = ov.MeshOffsetY,
                                                MeshOffsetZ = ov.MeshOffsetZ,
                                            },
                                            mod.FilePath, out var ovImportWhy, log, ov.MeshAnySize,
                                            slotMaterials: ovSlotMaterials);
                if (ovImported is null) { Skip($"{ov.Target}: {ovImportWhy}"); continue; }

                ovBytes = ovImported.Package;
                foreach (var n in ovImported.Notes) warnings.Add($"{ov.Target}: {n}");

                // A MATERIAL THE REPLACEMENT COULD NOT APPLY, said here as it is on the part route.
                // The mesh's material list could not be located (CookedMesh.FindMaterials refuses a
                // mesh with under eight bones -- a weapon piece -- and a list it cannot
                // find), so each slot keeps what the game's mesh gave it and every material named
                // for one was dropped. ImportMesh no longer prints the grey "array could not be
                // found" note when slots are named, so without this line the choice vanished with
                // nothing said anywhere. `serious` is in scope in Build and reaches the panel as
                // one; it is folded into `warnings` once, at the end.
                if (ovImported.MaterialListWhy is { } ovListWhy)
                    foreach (var (chosenSlot, chosenPackage, _) in ovSlotMaterials)
                        serious.Add(
                            $"{ov.Target}: material {chosenSlot + 1} ({Leaf(chosenPackage)}) was NOT applied: " +
                            $"this mesh's material list could not be located ({ovListWhy}) -- " +
                            "that slot still wears the material the game's mesh has");
                log?.Invoke($"      {Path.GetFileName(ovFile)}: {ovImported.Vertices:N0} vertex(es), " +
                            $"{ovImported.Triangles:N0} triangle(s)");
            }

            // The display name FIRST, and against the PRISTINE bytes, for the same reason Author
            // does it first: it is an FText in the export data, so replacing it changes that
            // export's length, and it has to be spliced while the exports still describe the bytes
            // being spliced. A re-point afterwards only moves the name batch, which ZenPackage
            // already accounts for.
            if (!string.IsNullOrWhiteSpace(ov.DisplayName))
            {
                // From the pristine bytes, never from the mount -- see PropertyAdd.PristineExports.
                var ovExports = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, ovRaw, ov.Target));
                var ovNamed = DisplayNameEdit.SetDisplayName(ovBytes, ovExports, ov.DisplayName,
                                                             out var ovNameWhy);

                // AN EMPTY NAME IS STILL A NAME TO REPLACE, and a PART could already do this while
                // an override could not. SetDisplayName anchors on the string the FText carries,
                // so a part the game ships with no name at all has nothing to find -- and the game
                // ships plenty: every clone head, most of Captain Rex's armour. Those are exactly
                // the parts somebody overrides and then wants to label, and until now the build
                // refused the whole override over it.
                //
                // Walked to instead of searched for, the same escape hatch parts have used since
                // the beginning: Locate gives the FText's byte range whether it holds a string or
                // not, and SetTextAt writes over that range.
                // TWO SHAPES, tried in turn. A part names itself in a top-level DisplayName;
                // an ABILITY names itself in UIData.Name, one struct down -- "Precision Shot II"
                // rather than the asset's own GA_PrecisionShot_T2. Same split as the descriptions
                // just below, and the same reason it was not found before.
                if (ovNameWhy is not null)
                {
                    foreach (var ovNamePath in new[] { "DisplayName", "UIData.Name" })
                    {
                        var ovNameRoot = ovNamePath.Split('.')[0];
                        var ovOwn = ovExports.FirstOrDefault(
                            e => e is not null && e.Properties.Any(x => x.Name.Text == ovNameRoot));
                        if (ovOwn is null) continue;
                        // The owner's NAME, not its class -- see the note on the descriptions below.
                        if (!PropertyAdd.Locate(provider, ovBytes, ov.Target, ovOwn.Name,
                                                ovNamePath, out var ovNameAt, out var ovNameLen, out _))
                            continue;
                        ovNamed = DisplayNameEdit.SetTextAt(ovBytes, ovNameAt, ovNameLen,
                                                            ov.DisplayName, out ovNameWhy);
                        if (ovNameWhy is null) break;
                    }
                }

                if (ovNameWhy is not null) { Skip($"{ov.Target}: {ovNameWhy}"); continue; }
                ovBytes = ovNamed;
                log?.Invoke($"      display name: \"{ov.DisplayName}\"");
            }

            // Both descriptions, and against the PRISTINE exports for the same reason the name is:
            // the anchor being searched for is the text the game shipped, not whatever the last
            // edit left behind. Each rewrite resizes the export, so they run one at a time and the
            // result of one is the input to the next.
            if (!string.IsNullOrWhiteSpace(ov.Description))
            {
                var ovDescExports = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, ovRaw, ov.Target));
                var ovDescOk = true;
                var ovDescWrote = new List<string>();
                var ovDescMissed = new List<string>();
                // AND THE ABILITY SHAPE, which is not the part shape. A part carries
                // ShortDescription and LongDescription at the TOP LEVEL; an ability carries a
                // single Description one struct down, inside UIData, with the player-facing name
                // beside it as UIData.Name.
                //
                // Daelen hit this on Precision Shot: five tiers reported as "carrying no
                // description to replace" that each carry a perfectly good one -- T2 reads
                // "Precision Shot II ... Critical Damage is increased by +10%". Nothing was
                // inherited and nothing was missing; the build was looking at the wrong depth
                // for the wrong two names.
                foreach (var which in new[] { "ShortDescription", "LongDescription", "UIData.Description" })
                {
                    // NOT EVERY ASSET HAS BOTH, and refusing over the missing one refused the
                    // whole override. A weapon's specialisation carries the pair; a modification's
                    // shop item carries only ShortDescription, and so do the armoury parts. What
                    // matters is that SOMETHING was rewritten -- checked after the loop.
                    //
                    // A dotted path is checked by its ROOT: the export carries `UIData`, and
                    // whether the struct holds a Description is Locate's question, not this one.
                    var ovDescRoot = which.Split('.')[0];
                    var ovDescNested = which.Length > ovDescRoot.Length;
                    if (!ovDescExports.Any(e => e is not null &&
                                                e.Properties.Any(pr => pr.Name.Text == ovDescRoot)))
                    { ovDescMissed.Add(which); continue; }

                    string? ovDescWhy;
                    // Located by WALKING to the property, not by searching for the text it holds.
                    // These two share a string-table key, so the key appears twice in the package
                    // and an anchor search cannot say which FText is which -- it refuses,
                    // correctly, and would leave both descriptions untouched for ever.
                    var ovOwner = ovDescExports.FirstOrDefault(
                        e => e is not null && e.Properties.Any(pr => pr.Name.Text == ovDescRoot));
                    // BY THE OWNER'S OWN NAME, never its class. Locate resolves an export by
                    // NAME first and class second, and a Blueprint package holds BOTH
                    // `GA_PrecisionShot_T3_C` (the BlueprintGeneratedClass) and
                    // `Default__GA_PrecisionShot_T3_C` (the CDO that actually carries UIData).
                    // Handing it the class name matched the FORMER by name and reported
                    // "'UIData' is not a property of BlueprintGeneratedClass" -- the third time
                    // name-versus-class resolution has cost this project a day. The owner is the
                    // export we already found; naming it is exact.
                    if (ovOwner is not null && PropertyAdd.Locate(
                            provider, ovBytes, ov.Target, ovOwner.Name, which,
                            out var ovDescAt, out var ovDescLen, out _))
                        ovBytes = DisplayNameEdit.SetTextAt(ovBytes, ovDescAt, ovDescLen,
                                                            ov.Description, out ovDescWhy);
                    // THE ANCHOR SEARCH CANNOT SEE A NESTED ONE -- it looks for a top-level
                    // property by name -- so a UIData path that will not locate is reported as
                    // absent rather than failing the whole override over it.
                    else if (ovDescNested) { ovDescMissed.Add(which); continue; }
                    else
                        ovBytes = DisplayNameEdit.SetText(ovBytes, ovDescExports, which,
                                                          ov.Description, out ovDescWhy);

                    if (ovDescWhy is not null)
                    { Skip($"{ov.Target}: {which}: {ovDescWhy}"); ovDescOk = false; break; }
                    ovDescWrote.Add(which);
                }
                if (!ovDescOk) continue;

                // Nothing written at all IS a failure, and a quiet one otherwise: the build would
                // report a new description and ship an asset that still reads the old one.
                if (ovDescWrote.Count == 0)
                {
                    Skip($"{ov.Target}: carries no description to replace " +
                         $"(looked for {string.Join(" and ", ovDescMissed)})");
                    continue;
                }

                log?.Invoke($"      description: \"{Short(ov.Description)}\" " +
                            $"-> {string.Join(", ", ovDescWrote)}" +
                            (ovDescMissed.Count > 0
                             ? $" (no {string.Join(" or ", ovDescMissed)} on this asset)" : ""));
            }

            // COLOUR SWATCHES THE TARGET TAKES OFF THE MENU, handed back.
            //
            // Reported by Nada, who had painted a three-channel tint mask for the clone commando
            // helmet and could reach two colours. Nothing about the mask: CPD_H_Outfit_Clo010_HELM
            // names Helmet.Color.Secondary and Helmet.Color.Visor in HiddenSlots AND in
            // LockedSlots, which leaves two of the four swatches a helmet has.
            //
            // The duplicate path has done this since slot takeovers were built and the override
            // path could not, which is an odd place for a capability to stop -- and he was
            // replacing the helmet, not duplicating it.
            //
            // TWO CONTAINERS IN TWO EDITS, hidden first. They are separate containers so neither
            // spoils the other's pattern, but a swatch taken out of only one is worse than the
            // hide it was meant to undo: out of HiddenSlots alone it appears greyed out forever,
            // out of LockedSlots alone it stays invisible. So the lock is released only for the
            // swatches whose hide actually came off.
            var swatchesBack = ov.HidesFromMenu.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
            if (swatchesBack.Count > 0)
            {
                var ovDetail = PartDetails.ReadExports(
                    AssetLoader.Read(() => PropertyAdd.PristineExports(provider, ovBytes, ov.Target)),
                    ov.Target);

                var haveHidden = ovDetail.HiddenSlots;
                var haveLocked = ovDetail.LockedSlots ?? [];
                var unknown = swatchesBack
                    .Where(t => !haveHidden.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (unknown.Count > 0)
                    warnings.Add($"{ov.Target}: does not hide " +
                                 string.Join(", ", unknown.Select(SlotLeaf)) +
                                 " -- that swatch is already on the menu");

                var freed = swatchesBack.Except(unknown, StringComparer.OrdinalIgnoreCase).ToList();
                if (freed.Count == 0) { log?.Invoke("      no swatch to hand back"); }
                else
                {
                    // ONE PATTERN, TWO CONTAINERS, when the two hold the same tags -- which is the
                    // ordinary case and was the first thing this hit. GameplayTagEdit locates a
                    // container by its full contents and refuses on more than one match, correctly:
                    // two containers reading the same thing are two statements and choosing one is
                    // guessing. It is not a guess here, because a swatch has to leave BOTH -- out of
                    // HiddenSlots alone it comes back greyed out, out of LockedSlots alone it stays
                    // invisible -- so both matches want the identical edit and SetEvery makes it,
                    // with the count it expects stated so a third match refuses.
                    var same = haveLocked.SequenceEqual(haveHidden, StringComparer.Ordinal);
                    var wantHidden = haveHidden
                        .Where(t => !freed.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();

                    if (same)
                    {
                        var written = GameplayTagEdit.SetEvery(ovBytes, haveHidden, wantHidden, 2,
                                                               out var bothWhy);
                        if (bothWhy is not null)
                        { Skip($"{ov.Target}: the swatch stays hidden: {bothWhy}"); continue; }
                        ovBytes = written;
                        log?.Invoke($"      HiddenSlots and LockedSlots now " +
                                    $"[{string.Join(", ", wantHidden.Select(SlotLeaf))}]");
                    }
                    else
                    {
                        if (!wantHidden.SequenceEqual(haveHidden, StringComparer.Ordinal))
                        {
                            var written = GameplayTagEdit.Set(ovBytes, haveHidden, wantHidden, out var hideWhy);
                            if (hideWhy is not null)
                            { Skip($"{ov.Target}: the swatch stays hidden: {hideWhy}"); continue; }
                            ovBytes = written;
                            log?.Invoke($"      HiddenSlots now [{string.Join(", ", wantHidden.Select(SlotLeaf))}]");
                        }

                        var wantLocked = haveLocked
                            .Where(t => !freed.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                        if (haveLocked.Count > 0 &&
                            !wantLocked.SequenceEqual(haveLocked, StringComparer.Ordinal))
                        {
                            var written = GameplayTagEdit.Set(ovBytes, haveLocked, wantLocked, out var lockWhy);
                            if (lockWhy is not null)
                            { Skip($"{ov.Target}: the swatch stays LOCKED: {lockWhy}"); continue; }
                            ovBytes = written;
                            log?.Invoke($"      LockedSlots now [{string.Join(", ", wantLocked.Select(SlotLeaf))}]");
                        }
                    }

                    log?.Invoke($"      {freed.Count} colour swatch(es) back on the menu: " +
                                string.Join(", ", freed.Select(SlotLeaf)));
                }
            }

            // AND THE LOCK ON THE TARGET'S OWN SLOT. See the duplicate path for what the switch is;
            // it is on this side because a REPLACEMENT is the only way to unlock one for a
            // character who is already wearing it -- a duplicate is a new part nobody has on.
            if (!ov.KeepsOwnSlotLock)
            {
                var unlocked = FlagEdit.ClearByName(provider, ovBytes, ov.Target,
                                                    "CustomizationFragmentOverrideSlots",
                                                    "bLockOwningSlot", out var ownWhy);
                if (ownWhy is not null)
                { Skip($"{ov.Target}: its own slot stays LOCKED: {ownWhy}"); continue; }
                ovBytes = unlocked;
                log?.Invoke("      its own slot is no longer locked: the player can change this " +
                            "again after picking it");
            }

            // Gate tags removed from AllowedSlots, in ONE container rewrite rather than one per
            // tag: the container is located by its full contents, so every removal after the first
            // would otherwise be hunting for a pattern the previous one had just changed.
            if (ov.RemoveTags.Count > 0 || ov.AddTags.Count > 0)
            {
                var ovExports2 = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, ovRaw, ov.Target));
                var have = PartDetails.TagsIn(ovExports2, ov.TagsProperty);
                if (have.Count == 0)
                { Skip($"{ov.Target}: no {ov.TagsProperty} to change tags in"); continue; }

                var missing = ov.RemoveTags.Where(t => !have.Contains(t, StringComparer.Ordinal)).ToList();
                if (missing.Count > 0)
                { Skip($"{ov.Target}: does not carry {string.Join(", ", missing)}"); continue; }

                // Removals and additions in ONE container rewrite. Set() locates the container by
                // its full contents, so a second call would be hunting for a pattern the first had
                // just changed.
                var want = have.Where(t => !ov.RemoveTags.Contains(t, StringComparer.Ordinal)).ToList();
                foreach (var t in ov.AddTags)
                    if (!want.Contains(t, StringComparer.Ordinal)) want.Add(t);
                ovBytes = GameplayTagEdit.Set(ovBytes, have, want, out var ovTagWhy);
                if (ovTagWhy is not null) { Skip($"{ov.Target}: {ovTagWhy}"); continue; }

                // NOT verified by counting the name afterwards. That was tried and is unsound:
                // an eight-byte name reference occurs by coincidence, so Captain Rex's tag still
                // "appears" in his gear kit after it has genuinely been removed from the container,
                // and the build refused a correct edit.
                //
                // What makes the removal sound is where it happened. GameplayTagEdit.Set locates
                // the container by its FULL contents and refuses unless that pattern occurs exactly
                // once, so a successful splice is a statement about a specific container rather
                // than about a name that might mean several things.
                log?.Invoke($"      {ov.RemoveTags.Count} tag(s) out, {ov.AddTags.Count} in: " +
                            $"{ov.TagsProperty} now [{string.Join(", ", want)}]");
            }

            // SPAWNS FIRST, because they are the only level edit that GROWS the package. A move
            // and a swap are both value-for-value in place, so they do not care what came before
            // them; a spawn appends two exports and shifts every offset after the export map, so
            // anything that recorded a byte position earlier would be pointing at the wrong thing.
            //
            // Each spawn is added and then MOVED with the existing machinery, addressed as #N --
            // the export-index form that exists because three of the hub's actors share a name.
            // Reusing the move rather than writing a transform into the copy is deliberate: moving
            // an actor is confirmed in game, and this way the only unproven step is the append.
            // What each spawn wants to DRAW, carried to where imports can be added. An import is
            // not addable this early -- the store entry's package list is not in scope yet -- and
            // adding one shifts the export data anyway, so it is done after the moves rather than
            // between them.
            var spawnDraws = new List<(string Address, string Mesh)>();

            if (ov.Spawns.Count > 0)
            {
                var spawnOk = true;

                // HOW MANY THIS LOOP HAS ALREADY PUT IN. AddCopy asks the provider how long the
                // Actors array is, and the provider reads the package the GAME shipped -- so from
                // the second spawn onward its answer is short by exactly this. See the parameter's
                // own note; the refusal it caused is correct behaviour, not a bug in the search.
                var addedHere = 0;
                foreach (var sp in ov.Spawns)
                {
                    LevelAdd.TakeDonorSlot = sp.TakeDonorSlot;
                    var grown = LevelAdd.AddCopy(provider, ovBytes, ov.Target, sp.Donor,
                                                 out var newIndex, out var spawnWhy, log, addedHere);
                    if (grown is null)
                    {
                        Skip($"{ov.Target}: spawn from '{sp.Donor}': {spawnWhy}");
                        spawnOk = false;
                        break;
                    }
                    ovBytes = grown;
                    addedHere++;

                    var address = $"#{newIndex}";
                    // THROUGH RootComponent. A location lives on the COMPONENT, not on the
                    // actor -- addressing the actor directly gets "RelativeLocation is not a
                    // property of BitReactorStaticMeshActor", which is the reader being right.
                    foreach (var (axis, want) in new[] { ("RootComponent.RelativeLocation.X", sp.X),
                                                        ("RootComponent.RelativeLocation.Y", sp.Y),
                                                        ("RootComponent.RelativeLocation.Z", sp.Z) })
                    {
                        // `was` is not checked for a spawn: the copy holds the DONOR's coordinates
                        // and the caller is saying where the new one goes, not what it used to be.
                        var moved = LevelEdit.Set(provider, ovBytes, ov.Target, address, axis,
                                                  double.NaN, want, out var at, out var moveWhy);
                        if (moveWhy is not null)
                        {
                            Skip($"{ov.Target}: placing {address}.{axis}: {moveWhy}");
                            spawnOk = false;
                            break;
                        }
                        ovBytes = moved;
                        log?.Invoke($"      {address}.{axis} = {want}, reads back at byte {at}");
                    }
                    if (!spawnOk) break;
                    if (sp.Mesh.Length > 0) spawnDraws.Add((address, sp.Mesh));

                    // FOR THE READ-BACK. Only this loop knows which export index the copy got.
                    spawned.Add(new Spawned(ov.Target, address, sp.X, sp.Y, sp.Z, sp.Mesh));
                    log?.Invoke($"      spawned {address} from '{sp.Donor}' at {sp.X},{sp.Y},{sp.Z}");
                }
                if (!spawnOk) continue;
            }

            // MOVES, before Numbers and for the same reason Numbers sit here: eight bytes for
            // eight bytes, so nothing after them shifts. LevelEdit does its own addressing --
            // see ModOverride.Move for why a level cannot use any of the three routes a
            // NumberChange takes.
            if (ov.Moves.Count > 0)
            {
                var ovMovesOk = true;
                foreach (var m in ov.Moves)
                {
                    var moved = LevelEdit.Set(provider, ovBytes, ov.Target, m.Actor, m.Path,
                                              m.Was, m.Now, out var movedAt, out var movedWhy);
                    if (movedWhy is not null)
                    {
                        Skip($"{ov.Target}: {m.Actor}.{m.Path}: {movedWhy}");
                        ovMovesOk = false;
                        break;
                    }
                    ovBytes = moved;
                    // "reads back", not "wrote": LevelEdit walks the written package again and
                    // refuses unless the component now reads the new value.
                    log?.Invoke($"      {m.Actor}.{m.Path}: {m.Was} -> {m.Now}, reads back " +
                                $"{m.Now} at byte {movedAt}");
                }
                if (!ovMovesOk) continue;
            }

            // SWAPS, beside the moves and for the same reason: four bytes for four bytes, so
            // nothing after them shifts. This is what removes an actor from a level and puts a
            // different one in its place -- see ModOverride.Swap for why that does not need the
            // package to grow.
            if (ov.Swaps.Count > 0)
            {
                var ovSwapsOk = true;
                foreach (var m in ov.Swaps)
                {
                    // WHAT IS THERE NOW, checked before the write, the same way a move checks its
                    // `was`. An edit aimed at the wrong actor should refuse rather than land.
                    var now = LevelEdit.LocatePointer(provider, ovBytes, ov.Target, m.Actor, m.Path,
                                                      out var lookWhy);
                    if (now is null)
                    { Skip($"{ov.Target}: {m.Actor}.{m.Path}: {lookWhy}"); ovSwapsOk = false; break; }

                    var leaf = now.Points[(now.Points.LastIndexOf('/') + 1)..];
                    var bare = leaf[(leaf.LastIndexOf('.') + 1)..];
                    if (m.Was.Length > 0 && !bare.Equals(m.Was, StringComparison.OrdinalIgnoreCase))
                    {
                        Skip($"{ov.Target}: {m.Actor}.{m.Path} draws '{bare}', not the '{m.Was}' " +
                             "this edit expected -- refusing rather than swapping the wrong thing");
                        ovSwapsOk = false;
                        break;
                    }

                    var swapped = LevelEdit.SetPointer(provider, ovBytes, ov.Target, m.Actor, m.Path,
                                                       m.Mesh, out var swapAt, out var wrote,
                                                       out var swapWhy);
                    if (swapped is null)
                    { Skip($"{ov.Target}: {m.Actor}.{m.Path}: {swapWhy}"); ovSwapsOk = false; break; }

                    ovBytes = swapped;
                    var reads = LevelEdit.LocatePointer(provider, ovBytes, ov.Target, m.Actor, m.Path, out _);
                    log?.Invoke($"      {m.Actor}.{m.Path}: {bare} -> " +
                                $"{(m.Mesh.Length == 0 ? "nothing" : m.Mesh)}, wrote {wrote} at byte " +
                                $"{swapAt}, reads back {reads?.Points ?? "(unreadable)"}");
                }
                if (!ovSwapsOk) continue;
            }

            // Numbers, after anything that can move bytes about and before nothing in particular:
            // this edit is four bytes for four bytes, so it neither moves an offset nor cares
            // where it sits. It is placed here rather than first only so its "was" is checked
            // against the same bytes every other edit has already seen.
            if (ov.Numbers.Count > 0)
            {
                var ovNumbersOk = true;
                foreach (var n in ov.Numbers)
                {
                    // The row walk FIRST, because it can address any field. NumberEdit finds a
                    // number by its current value and refuses when that value is not unique, which
                    // rules out every 0 and 1 in a weapon table -- most of the reaction, assist and
                    // chance rows. The search stays behind it for a target that is not a table.
                    // SetNumber, not SetFloat -- see the copy path above and DataTableEdit.
                    var ovByRow = DataTableEdit.SetNumber(provider, ovBytes, ov.Target, n.Row, n.Field,
                                                         n.Was, n.Now,
                                                         out var ovNumAt, out var ovRowWhy,
                                                         out var ovIsTable);
                    if (ovRowWhy is null)
                    {
                        ovBytes = ovByRow;
                        log?.Invoke($"      {n.Row}.{n.Field}: {n.Was} -> {n.Now} at byte {ovNumAt} (by row)");
                        continue;
                    }

                    // A DATATABLE THAT WILL NOT GIVE UP THE CELL IS AN ANSWER, NOT A PROMPT TO
                    // TRY HARDER. The two searches below exist for a target that is not a table
                    // (an ability's number, a blueprint's property, a mission's). Aimed at a
                    // table they do the wrong thing in the one case that matters: after a game
                    // patch has renamed or dropped the row this edit names, the by-value search
                    // finds some OTHER cell holding the same four bytes, writes there, and reads
                    // it back "clean" -- a mod that quietly changes the wrong number. Found by
                    // review on Attributes_AI_Grenade (a row that does not exist, a value that
                    // happens to occur once). So a table is refused here, naming the row and the
                    // field, and the override is not shipped.
                    if (ovIsTable)
                    {
                        // THE ROW AND FIELD NAMED, once: most of the walk's reasons already open with
                        // them in quotes, and a refusal that says them twice is one the status bar
                        // cuts off before it gets to the point.
                        var named = ovRowWhy.Contains(n.Field, StringComparison.Ordinal)
                            ? ovRowWhy : $"{n.Row}.{n.Field}: {ovRowWhy}";
                        Skip($"{ov.Target}: {named} -- a data table's number is placed by its row " +
                             "and field only, and is never searched for by its value, which could " +
                             "change a different cell");
                        ovNumbersOk = false;
                        break;
                    }

                    // BY NAME, between the row walk and the value search. `row` is read as the
                    // EXPORT and `field` as the property, which is what an author naming a
                    // non-table target already writes. This is the only one of the three that
                    // knows the width, and width is what the value search got wrong: an
                    // attachment's stat is a DOUBLE whose upper half reads as the float.
                    var ovByName = ScalarEdit.SetByName(provider, ovBytes, ov.Target, n.Row, n.Field,
                                                        n.Was, n.Now, out var ovKind, out ovNumAt,
                                                        out var ovNameWhy);
                    if (ovNameWhy is null)
                    {
                        ovBytes = ovByName;
                        // "reads back" rather than "wrote", and the words are the point. A
                        // build that reports what it wrote is reporting its own intention;
                        // ScalarEdit walks the written package and refuses unless the property
                        // now reads the new value, so this line is a statement about the asset.
                        log?.Invoke($"      {n.Row}.{n.Field}: {n.Was} -> {n.Now}, reads back " +
                                    $"{n.Now} as a {ovKind.ToString().ToLowerInvariant()} " +
                                    $"at byte {ovNumAt}");
                        continue;
                    }

                    // THE SEARCH BY VALUE IS FOR ONE CASE, AND EVERY OTHER REASON IS REFUSED.
                    //
                    // By name either could not reach the export at all (no container holds it, no
                    // export has that name, no schema for its class: `Unaddressable`), or it
                    // reached the export and looked at the property and said no -- the array is
                    // shorter than the path says, the map has no such key, the property is not
                    // carried, it reads another number than this edit expects (the game was
                    // patched under the project), it did not read back as written. Searching the
                    // package for the OLD value after a "no" finds some other four bytes that read
                    // the same, writes there, and the verifier -- which can only read a property
                    // that exists -- says clean. So the search runs ONLY when nothing was looked at.
                    //
                    // KEEP THAT DIRECTION. This used to list the reasons to refuse and let the rest
                    // search; it missed two, and a stale `Modifiers[7]` was written onto
                    // `Modifiers[0]` and read back clean. A reason nobody has met yet, or an
                    // exception's message, is now a refusal: a refused build is loud and gets
                    // fixed, a wrong write is silent and ships.
                    if (!ScalarEdit.Unaddressable(ovNameWhy))
                    {
                        // The property named ONCE: most reasons already open with it, and a line
                        // that says it twice is one the status bar cuts off before the point.
                        var namedBy = ovNameWhy.Contains(n.Field, StringComparison.Ordinal)
                            ? ovNameWhy : $"{n.Row}.{n.Field}: {ovNameWhy}";
                        Skip($"{ov.Target}: {namedBy} -- this property was found by its name, so the " +
                             "build will not look for its old number somewhere else: that could " +
                             "change a different property");
                        ovNumbersOk = false;
                        break;
                    }

                    ovBytes = NumberEdit.SetFloat(ovBytes, (float) n.Was, (float) n.Now,
                                                  out ovNumAt, out var ovNumWhy);
                    if (ovNumWhy is not null)
                    {
                        // BOTH reasons. "no float reading 17" on its own sends someone looking for
                        // a value that is present and addressable, when what actually failed was
                        // the walk -- and the other way round for a target with no rows at all.
                        // ALL THREE reasons. Each addresses the target a different way, and a
                        // caller shown only one goes looking in the wrong place -- which is how
                        // "no float reading 17" sent somebody after a value that was present and
                        // addressable, when the walk was what had failed.
                        Skip($"{ov.Target}: {n.Row}.{n.Field}: by row, {ovRowWhy}; " +
                             $"by name, {ovNameWhy}; by value, {ovNumWhy}");
                        ovNumbersOk = false;
                        break;
                    }
                    log?.Invoke($"      {n.Row}.{n.Field}: {n.Was} -> {n.Now} at byte {ovNumAt} (by value)");
                }
                if (!ovNumbersOk) continue;
            }

            // THE CHANGES THAT ARE NOT NUMBERS, dispatched on their own kind.
            //
            // AFTER the numbers and BEFORE the renames, for the reason the renames are where they
            // are: an FName write appends to the name batch when the value is new, and appending
            // moves every offset after it. A number edit is four bytes for four bytes and does
            // not care; a rename relocates by name and must see the final batch.
            if (ov.Values.Count > 0)
            {
                var ovValuesOk = true;
                foreach (var v in ov.Values)
                {
                    string? ovValWhy;
                    switch (v.Kind)
                    {
                        case "name":
                            ovBytes = PropertyAdd.SetName(provider, ovBytes, ov.Target, v.Row,
                                                          v.Field, v.Now, out ovValWhy);
                            break;

                        case "string":
                            ovBytes = PropertyAdd.SetString(provider, ovBytes, ov.Target, v.Row,
                                                            v.Field, v.Now, out ovValWhy);
                            break;

                        case "text":
                            ovBytes = PropertyAdd.SetText(provider, ovBytes, ov.Target, v.Row,
                                                          v.Field, v.Now, out ovValWhy);
                            break;

                        // ONE ENTRY MORE IN A MAP: a copy of the entry under `was`, keyed `now`.
                        // Listed BEFORE any value that addresses the new key, which it must be --
                        // the values are applied in order, and `Map[now]` names nothing until this.
                        case "mapadd":
                            ovBytes = PropertyAdd.AddMapEntry(provider, ovBytes, ov.Target, v.Row,
                                                              v.Field, v.Was, v.Now, out ovValWhy);
                            break;

                        // A CONSTANT IN A BLUEPRINT'S CODE: `row` is the function, `field` the
                        // constant's type. Same size in, same size out, so nothing after it moves.
                        case "scriptconst":
                            ovBytes = ScriptPatch.SetConstant(provider, ovBytes, ov.Target, v.Row,
                                                              v.Field, v.Was, v.Now, out ovValWhy);
                            break;

                        case "structdup" or "structdrop":
                        {
                            var ovWhich = v.Kind == "structdup" ? v.Now : v.Was;
                            if (!int.TryParse(ovWhich, out var ovElement))
                            {
                                ovValWhy = $"'{ovWhich}' is not an element index";
                                break;
                            }
                            ovBytes = PropertyAdd.SetStructEntry(provider, ovBytes, ov.Target,
                                                                 v.Row, v.Field, ovElement,
                                                                 v.Kind == "structdup", out ovValWhy);
                            break;
                        }

                        case "softadd" or "softremove":
                        {
                            var ovWant = v.Kind == "softadd" ? v.Now : v.Was;
                            var (ovRefPkg, ovRefAsset) = PropertyEdit.SplitRef(ovWant);
                            if (ovRefPkg is null)
                            {
                                ovValWhy = $"'{ovWant}' is not an asset path like /Game/Folder/Thing.Thing";
                                break;
                            }
                            ovBytes = PropertyAdd.SetSoftRefEntry(
                                provider, ovBytes, ov.Target, v.Row, v.Field,
                                ovRefPkg, ovRefAsset!, v.Kind == "softadd", out ovValWhy);
                            break;
                        }

                        default:
                            // A project written by a NEWER Studio. Saying which kind is the
                            // difference between "that will not work" and a mod that installs and
                            // quietly does not do the thing it says it does.
                            ovValWhy = $"this build does not know how to write a '{v.Kind}' value";
                            break;
                    }

                    if (ovValWhy is not null)
                    {
                        Skip($"{ov.Target}: {v.Row}.{v.Field} ({v.Kind}): {ovValWhy}");
                        ovValuesOk = false;
                        break;
                    }
                    log?.Invoke($"      {v.Row}.{v.Field}: '{v.Was}' -> '{v.Now}', reads back as {v.Kind}");
                }
                if (!ovValuesOk) continue;
            }

            // Renames BEFORE the scanning edits: renaming moves the name batch, and a re-point
            // that had already located an offset would be pointing at stale bytes.
            if (ov.RenameNames.Count > 0)
            {
                var ovPkg = ZenPackage.Read(ovBytes);
                var ovAbsent = ov.RenameNames.Keys
                    .Where(k => !ovPkg.Names.Contains(k, StringComparer.Ordinal)).ToList();
                if (ovAbsent.Count > 0)
                {
                    // Silence here would be the whole failure: RenameNames simply does not match,
                    // the package is written back unchanged, and the mod ships doing nothing.
                    Skip($"{ov.Target}: not in the name map: {string.Join(", ", ovAbsent)}");
                    continue;
                }
                ovPkg.RenameNames(ov.RenameNames);
                ovBytes = ovPkg.Write();
                foreach (var (k, v) in ov.RenameNames)
                    log?.Invoke($"      renamed '{Leaf(k)}' -> '{Leaf(v)}'");
            }

            // A NEW property before everything else. It rebuilds the export's unversioned header
            // and can append to the name map, which moves every offset after it -- so any edit
            // that locates something by pattern or by offset has to run against the RESULT of
            // this, never the other way round.
            if (ov.HasAddProperty)
            {
                ovBytes = PropertyAdd.AddTagContainer(
                    provider, ovBytes, ov.Target, ov.AddPropertyExport, ov.AddPropertyName,
                    ov.AddPropertyTags, out var ovAddWhy);
                if (ovAddWhy is not null) { Skip($"{ov.Target}: {ovAddWhy}"); continue; }
                log?.Invoke($"      added {ov.AddPropertyName} with {ov.AddPropertyTags.Count} tag(s)");
            }

            // The SAME edit as addProperty above, as a LIST. That one could add exactly one
            // container while addProperties and addClasses were lists from the start, so an
            // override needing two of them could not be written at all.
            //
            // In order, each planned against the RESULT of the last: every one rebuilds the
            // export's unversioned header and may append names, so a second planned from the same
            // bytes as the first would land on the wrong schema slot.
            var ovContainerFailed = false;
            foreach (var a in ov.AddTagContainers)
            {
                if (a.Tags.Count == 0) continue;   // being retyped in the editor; never in a file
                ovBytes = PropertyAdd.AddTagContainer(provider, ovBytes, ov.Target, a.Export, a.Name,
                                                      a.Tags, out var ovContainerWhy);
                if (ovContainerWhy is not null)
                { Skip($"{ov.Target}: {a.Export}.{a.Name}: {ovContainerWhy}"); ovContainerFailed = true; break; }
                log?.Invoke($"      added {a.Export}.{a.Name} = [{string.Join(", ", a.Tags)}]");
            }
            if (ovContainerFailed) continue;

            // Scalars, for the same reason and in the same place: each one rebuilds the export's
            // header, so they run before anything that locates bytes. They are applied IN ORDER
            // and each is planned against the RESULT of the last -- adding two properties to one
            // export from a single stale plan puts the second on the wrong schema slot.
            var ovScalarFailed = false;
            foreach (var a in ov.AddScalars)
            {
                ovBytes = PropertyAdd.AddScalar(provider, ovBytes, ov.Target, a.Export, a.Name,
                                                a.Value, out var ovScalarWhy);
                if (ovScalarWhy is not null)
                { Skip($"{ov.Target}: {a.Export}.{a.Name}: {ovScalarWhy}"); ovScalarFailed = true; break; }
                log?.Invoke($"      added {a.Export}.{a.Name} = {a.Value}");
            }
            if (ovScalarFailed) continue;
            var ovVersionFailed = false;
            foreach (var (set, rig) in ov.AddVersions)
            {
                ovBytes = PropertyAdd.AddArchetypeVersion(provider, ovBytes, ov.Target, set, rig, out var ovVerWhy);
                if (ovVerWhy is not null) { Skip($"{ov.Target}: version for {rig}: {ovVerWhy}"); ovVersionFailed = true; break; }
                log?.Invoke($"      + version {Leaf(set)} for {rig}");
            }
            if (ovVersionFailed) continue;

            // The flag BEFORE any re-point: it is located by an anchor name's byte offset, and a
            // re-point that appended a name would move the export data out from under it.
            if (ov.HasFlag)
            {
                ovBytes = FlagEdit.Clear(ovBytes, ov.FlagAnchor, ov.FlagBack,
                                         (byte) ov.FlagWas, out var ovFlagWhy);
                if (ovFlagWhy is not null) { Skip($"{ov.Target}: {ovFlagWhy}"); continue; }
                log?.Invoke($"      cleared the flag {ov.FlagBack} byte(s) before '{Leaf(ov.FlagAnchor)}'");
            }

            var ovFailed = false;
            foreach (var r in ov.Repoints)
            {
                ovBytes = NameRefEdit.Repoint(ovBytes, r.From, r.To, r.Expect, out var ovWhy);
                if (ovWhy is not null) { Skip($"{ov.Target}: {ovWhy}"); ovFailed = true; break; }

                // Count the references BACK OUT of the edited bytes. Repoint promised to move all
                // of them; this is the promise being checked rather than trusted, and it is the
                // only verification here that does not depend on the property renderer being able
                // to SEE the changed value. It could not see one of these -- a gameplay tag
                // container renders through a default ToString, so the semantic diff reported
                // "nothing changed" on an edit that had demonstrably rewritten a tag.
                var ovLeft = NameRefEdit.Count(ovBytes, r.From);
                if (ovLeft != 0)
                {
                    Skip($"{ov.Target}: {ovLeft} reference(s) to '{r.From}' survived the re-point");
                    ovFailed = true; break;
                }
                log?.Invoke($"      re-pointed {r.Expect} reference(s) to '{Leaf(r.From)}' -> '{Leaf(r.To)}', 0 left");
            }
            if (ovFailed) continue;

            // PALETTE ENTRIES, last: AddMeshOption appends names, so it must run against the
            // package as it now stands -- after every rename and every re-point. Added, never
            // re-keyed, so no weapon already in this palette loses the mesh it had.
            foreach (var (optTag, optMesh) in ov.AddOptions)
            {
                var optLeaf = optMesh[(optMesh.LastIndexOf('/') + 1)..];
                ovBytes = PropertyAdd.AddMeshOption(provider, ovBytes, ov.Target,
                    "CustomizationFragmentMesh", optTag, optMesh, optLeaf, out var ovOptWhy);
                if (ovOptWhy is not null)
                { Skip($"{ov.Target}: {Leaf(optTag)} was not added -- {ovOptWhy}"); ovFailed = true; break; }
                log?.Invoke($"      + {Leaf(optTag)} wears {optLeaf}");
            }
            if (ovFailed) continue;

            // ROWS ADDED TO A DATATABLE, before everything below.
            //
            // This is the capability three features went without: a shop slot, a mesh palette
            // entry and a roster row were each TAKEN OVER because a cooked container could not
            // grow. A row is copied from one the game itself wrote and only the named fields are
            // re-pointed -- see DataTableEdit.AddRow, which reads the result back through
            // CUE4Parse and refuses bytes that do not come out as the row asked for.
            //
            // Each one is applied in turn against the bytes the last one produced, so two rows in
            // one override do not both compute their offsets from the original.
            var ovRowsAdded = 0;
            foreach (var add in ov.AddRows)
            {
                var before = ovBytes.Length;
                ovBytes = DataTableEdit.AddRow(provider, ovBytes, ov.Target, add.Row, add.From,
                                               add.Set, out var rowWhy, log);
                if (rowWhy is not null)
                { Skip($"{ov.Target}: row '{add.Row}' was not added -- {rowWhy}"); ovFailed = true; break; }
                log?.Invoke($"      + row {add.Row}, copied from {add.From} " +
                            $"({ovBytes.Length - before:+#;-#;0} bytes)");
                ovRowsAdded++;
            }
            if (ovFailed) continue;
            _ = ovRowsAdded;

            // HARD IMPORTS, moved before the changes-nothing test below, because a redirect is a
            // change the package bytes may not show. See ImportRepoint: the two halves are the
            // export hashes, which ARE in the bytes, and the store entry's package ids, which are
            // not -- so testing only the bytes can call a real redirect "changes nothing".
            //
            // This is what lets a mod change what the GAME's assets load, which no copy can do --
            // and the shop is the case that needed it: the Armoury's stock is six tables naming 99
            // items by hard import, so putting a mod's own item on sale means taking over a slot
            // in the game's own table. ShopTables has the measurement.
            //
            // The target is used as given rather than expanded against the mod folder, unlike a
            // copy's: a copy points at its own siblings by id, an override is aimed at the game.
            // A mod's own asset is still reachable by writing its full package name.
            List<ulong>? ovImports = null;
            if (ov.RepointImports.Count > 0 && src.Store is not null)
            {
                ovBytes = ImportRepoint.MoveHashes(provider, ovBytes, src.Store, ov.RepointImports,
                                                   to => to, ov.Target, warnings, log,
                                                   package => HashSource(mod, package, missionCopies));
                ovImports = ImportRepoint.MovePackages(src.Store, ov.RepointImports, to => to,
                                                       ov.Target, warnings, log);
                if (ovImports.SequenceEqual(src.Store.ImportedPackages)) ovImports = null;
            }

            // RECIPES ADDED TO A WEIGHTED TABLE, after the redirects because they extend the same
            // import list. A table entry needs two things a plain edit does not: an import of the
            // recipe's CLASS export -- found by name, and its hash is the template's, because a
            // copy keeps its template's hashes -- and the recipe's package in the store entry,
            // which is the container's half of an import. PropertyAdd.AddWeightedRecipe has the
            // bytes; this has the plumbing.
            if (ov.AddRecipes.Count > 0)
            {
                if (src.Store is null)
                { Skip($"{ov.Target}: no store entry, so no import can be added to it"); continue; }
                var status = EnumValue(provider, "ERecipeStatus", "Available");
                if (status is null)
                { Skip($"{ov.Target}: ERecipeStatus::Available is not in the mappings"); continue; }

                var imports = ovImports ?? new List<ulong>(src.Store.ImportedPackages);
                // A TABLE is drawn from; an ARC's turn entries are applied. The target says which.
                var isArc = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, ovBytes, ov.Target))
                                       .Any(e => e is not null && e.ExportType == "BrunoCampaignArc");
                foreach (var add in ov.AddRecipes)
                {
                    var recipePackage = CopyTarget(add.Recipe, modFolder);
                    var hashSource = HashSource(mod, recipePackage);
                    var names = PropertyAdd.ExportNamesOf(provider, hashSource);
                    var hashes = PropertyAdd.PublicExportHashesOf(provider, hashSource);
                    var classAt = Array.IndexOf(names, Leaf(hashSource) + "_C");
                    if (classAt < 0 || classAt >= hashes.Length)
                    {
                        Skip($"{ov.Target}: {Leaf(hashSource)} has no class export {Leaf(hashSource)}_C to import " +
                             $"({names.Length} export(s) read)");
                        ovFailed = true; break;
                    }
                    var recipeId = PackageId.Of(recipePackage);
                    var slot = imports.IndexOf(recipeId);
                    if (slot < 0) { imports.Add(recipeId); slot = imports.Count - 1; }
                    ovBytes = PropertyAdd.AddImport(ovBytes, slot, hashes[classAt], recipePackage,
                                                    out var recipeIndex, out var impWhy);
                    if (impWhy is not null)
                    { Skip($"{ov.Target}: could not import {Leaf(recipePackage)} -- {impWhy}"); ovFailed = true; break; }
                    string? recWhy;
                    if (isArc)
                    {
                        ovBytes = PropertyAdd.AddArcRecipe(provider, ovBytes, ov.Target, recipeIndex,
                                                           status.Value, add.AutoStart, out var turns, out recWhy);
                        if (recWhy is null)
                            log?.Invoke($"      + recipe {Leaf(recipePackage)} on {turns} scripted turn(s)" +
                                        $"{(add.AutoStart ? ", auto-start" : "")}, imported as package [{slot}]");
                    }
                    else
                    {
                        ovBytes = PropertyAdd.AddWeightedRecipe(provider, ovBytes, ov.Target, recipeIndex,
                                                                add.Weight, add.AutoStart, status.Value, out recWhy);
                        if (recWhy is null)
                            log?.Invoke($"      + recipe {Leaf(recipePackage)} at weight {add.Weight}" +
                                        $"{(add.AutoStart ? ", auto-start" : "")}, imported as package [{slot}]");
                    }
                    if (recWhy is not null)
                    { Skip($"{ov.Target}: recipe {Leaf(recipePackage)} was not added -- {recWhy}"); ovFailed = true; break; }
                }
                if (ovFailed) continue;
                ovImports = imports;
            }

            // MISSIONS ADDED TO A WEIGHTED TABLE. Same plumbing as a recipe, one difference that
            // matters: a recipe is imported by its CLASS export (`X_C`) because a recipe IS a
            // Blueprint, while a mission is a data asset whose main export carries the package's
            // own leaf name. And a COPY KEEPS ITS TEMPLATE'S EXPORT NAMES -- a mission copied from
            // SK_Anaxes_045 still answers to `SK_Anaxes_045` however the copy is called -- so the
            // name to look for is the HASH SOURCE's leaf, not the copy's id. Using the copy's id
            // finds nothing and the row is skipped, which reads as "adding a row does not work".
            if (ov.AddMissions.Count > 0)
            {
                if (src.Store is null)
                { Skip($"{ov.Target}: no store entry, so no import can be added to it"); continue; }

                var imports = ovImports ?? new List<ulong>(src.Store.ImportedPackages);
                foreach (var add in ov.AddMissions)
                {
                    var missionPackage = CopyTarget(add.Mission, modFolder);
                    // SYNTHESISED COPIES TOO. A mission's ModCopy is built by MissionBuild and is
                    // not in mod.Copies, so HashSource without `missionCopies` finds no template,
                    // reads zero exports and refuses the row -- the same omission that once left
                    // hashes unmoved and pointed a table row at the wrong export.
                    var hashSource = HashSource(mod, missionPackage, missionCopies);
                    var names = PropertyAdd.ExportNamesOf(provider, hashSource);
                    var hashes = PropertyAdd.PublicExportHashesOf(provider, hashSource);
                    var mainAt = Array.IndexOf(names, Leaf(hashSource));
                    if (mainAt < 0 || mainAt >= hashes.Length)
                    {
                        Skip($"{ov.Target}: {Leaf(hashSource)} has no export named {Leaf(hashSource)} to " +
                             $"import ({names.Length} export(s) read)");
                        ovFailed = true; break;
                    }
                    var missionId = PackageId.Of(missionPackage);
                    var slot = imports.IndexOf(missionId);
                    if (slot < 0) { imports.Add(missionId); slot = imports.Count - 1; }
                    ovBytes = PropertyAdd.AddImport(ovBytes, slot, hashes[mainAt], missionPackage,
                                                    out var missionIndex, out var impWhy);
                    if (impWhy is not null)
                    { Skip($"{ov.Target}: could not import {Leaf(missionPackage)} -- {impWhy}"); ovFailed = true; break; }

                    ovBytes = PropertyAdd.AddWeightedMission(provider, ovBytes, ov.Target, missionIndex,
                                                             add.Weight, out var addWhy);
                    if (addWhy is not null)
                    { Skip($"{ov.Target}: mission {Leaf(missionPackage)} was not added -- {addWhy}"); ovFailed = true; break; }
                    log?.Invoke($"      + mission {Leaf(missionPackage)} at weight {add.Weight}, " +
                                $"imported as package [{slot}] -- nothing was displaced");
                }
                if (ovFailed) continue;
                ovImports = imports;
            }

            // MISSIONS APPENDED TO A CAMPAIGN TURN. Same import plumbing as a table row -- the
            // turn's `Missions` entry is an FPackageIndex like the row's `Mission` -- and the same
            // trap: a copy keeps its TEMPLATE's export names, so the export to import is the HASH
            // SOURCE's leaf, never the copy's id.
            if (ov.AddArcMissions.Count > 0)
            {
                if (src.Store is null)
                { Skip($"{ov.Target}: no store entry, so no import can be added to it"); continue; }

                var imports = ovImports ?? new List<ulong>(src.Store.ImportedPackages);

                // ONE IMPORT PER MISSION, however many turns it goes on. AddImport APPENDS
                // unconditionally, so calling it once per turn gave the same mission two identical
                // import entries -- 32 bytes of map where 16 was right. Harmless at runtime, both
                // resolving to the same export, and caught only because the verifier does the
                // arithmetic. The FPackageIndex stays valid as more imports are appended, which is
                // exactly what AddPackageImport's "appending rather than inserting" note promises.
                var imported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var add in ov.AddArcMissions)
                {
                    var missionPackage = CopyTarget(add.Mission, modFolder);
                    if (!imported.TryGetValue(missionPackage, out var missionIndex))
                    {
                        var hashSource = HashSource(mod, missionPackage, missionCopies);
                        var names = PropertyAdd.ExportNamesOf(provider, hashSource);
                        var hashes = PropertyAdd.PublicExportHashesOf(provider, hashSource);
                        var mainAt = Array.IndexOf(names, Leaf(hashSource));
                        if (mainAt < 0 || mainAt >= hashes.Length)
                        {
                            Skip($"{ov.Target}: {Leaf(hashSource)} has no export named {Leaf(hashSource)} to " +
                                 $"import ({names.Length} export(s) read)");
                            ovFailed = true; break;
                        }
                        var missionId = PackageId.Of(missionPackage);
                        var slot = imports.IndexOf(missionId);
                        if (slot < 0) { imports.Add(missionId); slot = imports.Count - 1; }
                        ovBytes = PropertyAdd.AddImport(ovBytes, slot, hashes[mainAt], missionPackage,
                                                        out missionIndex, out var impWhy);
                        if (impWhy is not null)
                        { Skip($"{ov.Target}: could not import {Leaf(missionPackage)} -- {impWhy}"); ovFailed = true; break; }
                        imported[missionPackage] = missionIndex;
                    }

                    ovBytes = PropertyAdd.AddArcMission(provider, ovBytes, ov.Target, missionIndex,
                                                        add.Turn, out var arcWhy);
                    if (arcWhy is not null)
                    { Skip($"{ov.Target}: mission {Leaf(missionPackage)} was not placed -- {arcWhy}"); ovFailed = true; break; }
                    log?.Invoke($"      + mission {Leaf(missionPackage)} placed on turn {add.Turn} " +
                                "-- no table, no roll, nothing displaced");
                }
                if (ovFailed) continue;
                ovImports = imports;
            }

            // CLASS-VALUED PROPERTIES, which need the same plumbing as a recipe and for the same
            // reason: the value is an FPackageIndex, so the asset it names has to be imported by
            // the package AND listed in the container store entry.
            //
            // Asked for by Daelen after hitting the wall this closes -- a property could be added
            // and not given a value. The use is per-tier ability progression: all six Rocket
            // Strike tiers inherit one CooldownGameplayEffectClass from GA_RocketStrike_T1, so
            // there is exactly one place to change and it changes every tier. Sabotage names a
            // different cooldown effect on each of its four, which is what makes its uses-per-
            // combat progress; this is how a mod does the same to anything else.
            if (ov.AddClasses.Count > 0)
            {
                if (src.Store is null)
                { Skip($"{ov.Target}: no store entry, so no import can be added to it"); continue; }

                var imports = ovImports ?? new List<ulong>(src.Store.ImportedPackages);
                var classFailed = false;
                foreach (var add in ov.AddClasses)
                {
                    // ONE OF THE MOD'S OWN COPIES OR ONE OF THE GAME'S, named the same way a
                    // recipe is: a leading `/` is a package path, anything else is a copy id in
                    // this mod's folder. Per-tier progression means pointing at the mod's copies,
                    // so this is the case that matters rather than the exotic one.
                    var target = CopyTarget(add.Target, modFolder);

                    // THE HASHES ARE THE TEMPLATE'S when the target is a copy this build has not
                    // written yet -- a copy keeps its template's export hashes, and reading the
                    // copy itself gets an empty table out of a package that is not mounted. Same
                    // reasoning as the recipe path; see HashSource.
                    var hashSource = HashSource(mod, target);
                    var wanted = Leaf(hashSource) + "_C";
                    var names = PropertyAdd.ExportNamesOf(provider, hashSource);
                    var hashes = PropertyAdd.PublicExportHashesOf(provider, hashSource);
                    var classAt = Array.IndexOf(names, wanted);
                    if (classAt < 0 || classAt >= hashes.Length)
                    {
                        Skip($"{ov.Target}: {Leaf(hashSource)} has no class export {wanted} to point " +
                             $"{add.Name} at ({names.Length} export(s) read)");
                        classFailed = true; break;
                    }

                    // WHAT IT WILL READ BACK AS: the target PACKAGE's leaf, not a class name.
                    //
                    // A copy does not necessarily rename the class export inside it -- measured on
                    // GE_Cooldown_RocketStrike copied three times, where each copy is a different
                    // package and every one still holds `GE_Cooldown_RocketStrike_C`. So the class
                    // half of the resolved path is the TEMPLATE's name and only the package half
                    // distinguishes them. Expecting `<copy>_C` would refuse a correct write.
                    var reads = Leaf(target);

                    var targetId = PackageId.Of(target);
                    var slot = imports.IndexOf(targetId);
                    if (slot < 0) { imports.Add(targetId); slot = imports.Count - 1; }

                    ovBytes = PropertyAdd.AddImport(ovBytes, slot, hashes[classAt], target,
                                                    out var fpi, out var impWhy);
                    if (impWhy is not null)
                    { Skip($"{ov.Target}: could not import {Leaf(target)} -- {impWhy}"); classFailed = true; break; }

                    // PLANNED AGAINST THE RESULT of the import, never against what went into it:
                    // adding an import moves the export data, and a plan made before that points
                    // at bytes which have shifted.
                    ovBytes = PropertyAdd.AddClass(provider, ovBytes, ov.Target, add.Export,
                                                   add.Name, fpi, reads, out var addWhy);
                    if (addWhy is not null)
                    { Skip($"{ov.Target}: {add.Export}.{add.Name}: {addWhy}"); classFailed = true; break; }

                    log?.Invoke($"      {add.Export}.{add.Name} -> {target}, " +
                                $"imported as package [{slot}]");
                }
                if (classFailed) continue;
                ovImports = imports;
            }

            // ---------------------------------------------------------------- what a spawn draws
            //
            // A MESH FROM ANOTHER PACKAGE, which is the second gate. Adding the actor is proven;
            // this gives it something to draw that the level did not already reference, which is
            // what "pick an asset and place it" actually needs.
            //
            // Nothing here is new machinery. PropertyAdd.AddImport already grows the three runs an
            // import spans -- the public export hashes, the import map and the imported package
            // names, with its four-byte trailer -- and the store entry's package list is the
            // fourth, owned by this loop. Pointing the component at it afterwards is the same
            // SetPointer a Swap uses, which is confirmed in game.
            if (spawnDraws.Count > 0)
            {
                var drawOk = true;
                foreach (var (address, meshPackage) in spawnDraws)
                {
                    var meshLeaf = Leaf(meshPackage);
                    var meshNames = PropertyAdd.ExportNamesOf(provider, meshPackage);
                    var meshHashes = PropertyAdd.PublicExportHashesOf(provider, meshPackage);
                    var meshAt = Array.IndexOf(meshNames, meshLeaf);
                    if (meshAt < 0 || meshAt >= meshHashes.Length)
                    {
                        Skip($"{ov.Target}: {meshLeaf} has no export of its own name to import " +
                             $"({meshNames.Length} export(s) read)");
                        drawOk = false; break;
                    }

                    var drawImports = ovImports ?? new List<ulong>(src.Store?.ImportedPackages ?? []);
                    var meshId = PackageId.Of(meshPackage);
                    var meshSlot = drawImports.IndexOf(meshId);
                    if (meshSlot < 0) { drawImports.Add(meshId); meshSlot = drawImports.Count - 1; }
                    ovImports = drawImports;

                    ovBytes = PropertyAdd.AddImport(ovBytes, meshSlot, meshHashes[meshAt],
                                                    meshPackage, out var meshFpi, out var drawWhy);
                    if (drawWhy is not null)
                    { Skip($"{ov.Target}: could not import {meshLeaf} -- {drawWhy}"); drawOk = false; break; }

                    // BY INDEX, not by name. SetPointer looks for an export that already draws the
                    // mesh, which cannot exist for an import added a line ago -- and finding that
                    // out costs a scan of every export in the level. Fifty-five seconds on the
                    // hub's Pit, and then a refusal.
                    var pointed = LevelEdit.SetPointerTo(provider, ovBytes, ov.Target, address,
                                                         "RootComponent.StaticMesh", meshFpi,
                                                         out _, out var pointWhy);
                    if (pointed is null || pointWhy is not null)
                    { Skip($"{ov.Target}: {address} cannot draw {meshLeaf}: {pointWhy}"); drawOk = false; break; }
                    ovBytes = pointed;

                    log?.Invoke($"      {address} draws {meshLeaf}, imported as package [{meshSlot}]");
                }
                if (!drawOk) continue;
            }

            // SLOTS LAST, on the finished bytes: every other edit has already moved what it moves.
            //
            // Placed BEFORE the "changed nothing" gate just below, on purpose and not where the
            // plan's own line numbers point (after it, by the chunk queue). A slot-only override
            // starts this loop with `ovBytes == ovRaw` -- nothing above touches it, since none of
            // its other fields are set -- so if slots were added after that gate ran, the gate
            // would see no difference yet, take the "asked for something, changed nothing" branch
            // (HasEdit is true once AddSlots.Count > 0) and `continue` before this code ever ran.
            // Measured: that is exactly what the brief's own line number did to SlotBuildTest.json.
            if (ov.AddSlots.Count > 0)
            {
                ovBytes = SlotAdd.Add(provider, ovBytes, ov.Target, ovLeafOf(ov.Target), ov.AddSlots,
                                      out var ovSlotsAdded, out var ovSlotWhy, log: log);
                if (ovSlotWhy is not null) { Skip($"{ov.Target}: slots: {ovSlotWhy}"); continue; }
                log?.Invoke($"      {ovSlotsAdded.Count} slot(s) added: " + string.Join(", ", ovSlotsAdded.Select(a => a.Id)));
            }

            // An override that changed nothing is a package replaced by a copy of itself. It
            // cannot break anything and cannot do anything either, which is the more confusing of
            // the two failures because everything about it looks installed.
            if (ovImports is null &&
                ovBytes.Length == ovRaw.Length && ovBytes.AsSpan().SequenceEqual(ovRaw))
            {
                // DROPPED WITH A NOTE, not refused.
                //
                // It was a Skip, and a Skip fails the whole build. That was right while an
                // override was something you made one of on purpose; it is wrong now that
                // replacing a part sets up the part, its mesh and its textures together -- an
                // author who only wants a new model would have every other one of those empty,
                // and the mod would refuse to build until they filled in things they did not
                // want to change.
                //
                // Nothing is lost by dropping it: a package replaced by a copy of itself cannot
                // do anything. The note is what stops that being silent, and notes are now read
                // where the build result is rather than in the log.
                //
                // TWO DIFFERENT THINGS, and only one of them is worth saying. An override with
                // NOTHING asked of it is a placeholder -- replacing a part creates one to hang its
                // model and textures off, and an author who only wanted a new texture never gives
                // the part itself anything. Warning about that would put a line reading "nothing
                // was changed" on the build of a mod that changed exactly what was asked, which is
                // the note that makes people think their replacement failed.
                //
                // An override that WAS asked for something and produced no change is the real one:
                // a rename that matched nothing, a number already at that value.
                if (!ov.HasEdit)
                {
                    log?.Invoke($"      nothing asked of {ovLeafOf(ov.Target)}, so it is not shipped");
                    notAsked.Add(ovLeafOf(ov.Target));
                    continue;
                }
                warnings.Add($"{ov.Target}: what was asked for changed nothing, so it is not " +
                             "shipped. The name, number or rename may already be what you set.");
                continue;
            }

            // The edited bytes have to still parse. This is the check that separates "a mod that
            // does not work" from "a game asset that no longer loads".
            try { ZenPackage.Read(ovBytes); }
            catch (Exception e)
            { Skip($"{ov.Target}: the edited package no longer reads -- {e.Message}"); continue; }

            // EARLY: the game loads this file before it mounts Mods/, so it ships in a second
            // container installed into ~mods (EarlyLoad). A hard import of one of this mod's own
            // packages would point at something not mounted yet at startup -- refuse it by name.
            // A soft reference (a slot's default part) is resolved later and is fine.
            var ovEarly = AsPlugin && EarlyLoad.IsEarly(ov.Target);
            if (ovEarly)
            {
                var willImport = new List<ulong>(ovImports ?? src.Store?.ImportedPackages ?? new List<ulong>());
                willImport.AddRange(ovMaterialImports);
                var ownPackages = built.Where(b => !b.IsOverride)
                                       .GroupBy(b => b.ChunkId)
                                       .ToDictionary(g => g.Key, g => g.First().PackageName);
                var inward = willImport.Where(ownPackages.ContainsKey)
                                       .Select(id => ownPackages[id]).Distinct().ToList();
                if (inward.Count > 0)
                {
                    Skip($"{ov.Target}: loads before mods are mounted, but imports " +
                         $"{string.Join(", ", inward)} from this mod -- that package is not mounted " +
                         "yet at startup");
                    continue;
                }
            }

            // P7: the stamp is recorded HERE, after the last gate that can still Skip this
            // override, not back where SlotAdd.Add succeeds. A Skip between there and here used to
            // leave a stamp for a landing that never shipped -- ModVerify would then go looking for
            // slots in a package the build never wrote.
            if (ov.AddSlots.Count > 0)
                slotStamps.Add(new SlotStamp(ov.Target, ov.Target, ovLeafOf(ov.Target), ov.AddSlots));

            var ovLeaf = ov.Target[(ov.Target.LastIndexOf('/') + 1)..];
            chunks.Add(new IoStoreWriter.Chunk(ovId, 0, 1 /* ExportBundleData */, ovBytes, ovPath));

            // Payloads carried verbatim under the same id. This target has none, but a texture
            // override would -- and a package shipped without its payloads is exactly the failure
            // AssetCopy was written to prevent.
            foreach (var piece in src.Pieces.Where(p => !p.IsPackage))
                chunks.Add(new IoStoreWriter.Chunk(ovId, piece.Index, piece.Type, piece.Data, null));

            built.Add(new BuiltPart(ovLeaf, ov.Target, ovId, ovPath, ovBytes.Length,
                                    // Its own template: an override is a copy of the thing it
                                    // replaces, which is what makes it structurally safe.
                                    ov.Target, IsCopy: true, IsOverride: true, IsEarly: ovEarly));

            // The game's own store entry -- carried unchanged unless the author asked for a
            // redirect. Unchanged is correct by construction here in a way it is not for a part:
            // the bytes ARE the game's, so they import exactly what the game imports, and no icon
            // redirect can have moved one.
            if (src.Store is not null)
            {
                if (ovImports is not null || ovMaterialImports.Count > 0)
                {
                    var ovFinal = new List<ulong>(ovImports ?? src.Store.ImportedPackages);
                    ovFinal.AddRange(ovMaterialImports);
                    storeFor[ovId] = new ContainerHeader.StoreEntry(ovFinal, src.Store.ShaderMapHashes);
                    if (ovMaterialImports.Count > 0)
                        log?.Invoke($"      imports {ovFinal.Count} package(s), " +
                                    $"{ovMaterialImports.Count} added for material slots");
                }
                else
                {
                    storeFor[ovId] = src.Store;
                    log?.Invoke($"      imports {src.Store.ImportedPackages.Count} package(s), carried unchanged");
                }
            }
            else warnings.Add($"{ov.Target}: no store entry to carry, so its imports are unknown " +
                              "-- the override may fail to load");

            log?.Invoke($"      {ovRaw.Length:N0} -> {ovBytes.Length:N0} bytes " +
                        $"({ovBytes.Length - ovRaw.Length:+#;-#;0})");

            // AND THE REGISTRY ENTRY, which is the half a package edit cannot reach.
            //
            // The registry's CustomizationPartTags is a cook-time projection of the part's tags,
            // and it is what the menu filters on -- so an override that changes a tag in the
            // package alone changes nothing about what is offered. Confirmed the hard way: the
            // Padawan specialisation kept its character restriction after a byte-perfect package
            // edit, while a display-name override on the same screen took effect immediately.
            //
            // Appended only when the game's registry actually lists the target. If it does not,
            // the override is of something the customization system never enumerates, and an
            // invented entry would be worse than none.
            var ovChangedTags = ov.HasRepoint || ov.RemoveTags.Count > 0 || ov.AddTags.Count > 0;
            if (ovChangedTags && templateEntry.TryGetValue(ov.Target, out var ovAt))
            {
                var ovRecord = records[ovAt];
                var ovTags = OverrideTagsFor(
                    decoded.PreallocatedAssetDataBuffers[ovAt].TagsAndValues,
                    ov.Repoints, ov.RemoveTags, ov.AddTags);

                Register(new RegistryAssets.NewAsset(
                    PackageName: ov.Target,
                    AssetName: ovLeaf,
                    PackagePath: ov.Target[..ov.Target.LastIndexOf('/')],
                    ClassPackage: registry.Names[(int) ovRecord.ClassPackage],
                    ClassName: registry.Names[(int) ovRecord.ClassName],
                    Tags: ovTags));

                var ovShown = ovTags.FirstOrDefault(x => x.Item1 == "CustomizationPartTags").Item2;
                log?.Invoke($"      registry entry rewritten: {ovShown}");
            }
            else if (ovChangedTags)
                warnings.Add($"{ov.Target}: the game's registry does not list it, so only the " +
                             "package was changed -- if this part is chosen from a menu, the menu " +
                             "will not have noticed");
        }

        // A TABLE OVERLAY IS CONTENT, even though nothing is built for it until the plugin step
        // below: the mod ships a table of its own and an action that lays it over the game's, and
        // refusing that as "empty" would make the one mod shape this build learned last
        // impossible to ship.
        if (built.Count == 0 && mod.TableOverlays.Count == 0)
        {
            // THE REASON, NOT ONLY THE VERDICT. The Studio puts this exception's message in the
            // bar at the foot of the window, and "refusing to write an empty mod" alone sends
            // somebody looking for a mod with nothing in it when the real answer is one line up
            // in the log: the only override was refused, and why.
            //
            // THE PACKAGE'S LEAF, not its whole path: "/Game/Game/GameData/.../Attributes_AI_Grenade:"
            // is most of a bar that cuts a long line off, and the log has the full path.
            var firstSkip = skipped.Count == 0 ? "" : skipped[0];
            if (firstSkip.StartsWith('/') && firstSkip.IndexOf(": ", StringComparison.Ordinal) is var skipColon and > 0)
                firstSkip = firstSkip[(firstSkip.LastIndexOf('/', skipColon) + 1)..];
            var because = skipped.Count > 0
                ? $" -- {firstSkip}" +
                  (skipped.Count > 1 ? $" (and {skipped.Count - 1} more refused; the log has them)" : "")
                : notAsked.Count > 0
                    ? $" -- nothing is asked of {string.Join(", ", notAsked)}, so it is not shipped"
                    : "";
            throw new InvalidOperationException(
                "nothing could be built -- refusing to write an empty mod" + because);
        }

        // The plugin's own data asset, BEFORE the container header is assembled.
        //
        // It has to be in `built` and not merely in `chunks`, because the header is built from
        // `built` -- a package whose id the store does not declare is a package the loader never
        // sees, whatever else is in the container. The game features subsystem refuses to load a
        // plugin without this asset ("unrecoverable error as GameFeatureData is missing"), so
        // shipping without it would mount and then stop, which looks exactly like not mounting.
        if (AsPlugin)
        {
            var pluginName = containerName.EndsWith("_P", StringComparison.Ordinal)
                ? containerName[..^2] : containerName;

            if (GameFeaturePlugin.FindTemplate(provider, log) is { } gfdTemplate)
            {
                // ---------------------------------------------------------- table overlays
                //
                // THE GAME'S OWN ANSWER TO TWO MODS TOUCHING ONE TABLE, and the order here is the
                // whole of it: the mod's copy of the table first, then an action naming the pair,
                // then the feature data importing that action. A feature that points at nothing
                // is a feature the subsystem runs and which does nothing.
                //
                // The edit is made BEFORE the rename, against the game's own package name. A walk
                // needs a mounted container to resolve the row struct, and the copy is in no
                // container at all -- editing after renaming refuses itself for a reason that is
                // true and beside the point.
                var overlayPairs = new List<(string TargetPackage, string TargetAsset,
                                             string OverlayPackage, string OverlayAsset)>();
                foreach (var ovl in mod.TableOverlays)
                {
                    var src = AssetCopy.Read(provider, ovl.Target, out var srcWhy);
                    if (src is null)
                    { warnings.Add($"overlay {Leaf(ovl.Target)}: {srcWhy}"); continue; }

                    var piece = src.Pieces.FirstOrDefault(p => p.IsPackage);
                    if (piece is null)
                    { warnings.Add($"overlay {Leaf(ovl.Target)}: it has no package chunk"); continue; }

                    var edited = piece.Data;
                    if (ovl.Row.Length > 0 && ovl.Field.Length > 0)
                    {
                        edited = DataTableEdit.SetNumber(provider, edited, ovl.Target, ovl.Row,
                                                         ovl.Field, ovl.Was, ovl.Now,
                                                         out _, out var setWhy);
                        if (setWhy is not null)
                        {
                            warnings.Add($"overlay {Leaf(ovl.Target)}: {ovl.Row}.{ovl.Field} " +
                                         $"could not be set -- {setWhy}");
                            continue;
                        }
                    }

                    var overlayName = $"/{pluginName}/DT_Overlay_{Leaf(ovl.Target)}";
                    var overlayPath = $"{pluginName}/Content/DT_Overlay_{Leaf(ovl.Target)}.uasset";
                    var pieces = src.Pieces
                        .Select(p => p.IsPackage ? p with { Data = edited } : p).ToList();
                    var renamed = AssetCopy.Rename(src with { Pieces = pieces }, overlayName,
                                                   overlayPath, log);
                    chunks.AddRange(renamed);

                    var overlayId = PackageId.Of(overlayName);
                    built.Add(new BuiltPart($"overlay {Leaf(ovl.Target)}", overlayName, overlayId,
                                            overlayPath, edited.Length, ovl.Target, IsCopy: true));

                    // IN THE PLUGIN'S OWN REGISTRY, because the mod runtime reads it.
                    // `UBitReactorModRuntimeSubsystem::TryMountMod` names the literal
                    // "AssetRegistry.bin", tests that the file is there and loads it, and RETURNS
                    // FALSE if either fails -- read out of the shipped PDB at 0x092B07F0. Every
                    // mod of his that works ships a populated one; the first overlay-only mod
                    // shipped 104 bytes with nothing in it, which is the only structural
                    // difference between it and the nineteen that load.
                    Register(new RegistryAssets.NewAsset(
                        PackageName: overlayName,
                        AssetName: Leaf(overlayName),
                        PackagePath: overlayName[..overlayName.LastIndexOf('/')],
                        ClassPackage: "/Script/Engine",
                        ClassName: "DataTable",
                        Tags: []));

                    // The copy keeps the ORIGINAL's imports, which it still needs: a row struct
                    // lives in another package and an overlay that cannot resolve it is a table
                    // with no rows.
                    if (src.Store is { } ovlStore) storeFor[overlayId] = ovlStore;

                    overlayPairs.Add((ovl.Target, Leaf(ovl.Target), overlayName, Leaf(overlayName)));
                    log?.Invoke($"      overlay {Leaf(overlayName)} covers {Leaf(ovl.Target)}" +
                                (ovl.Row.Length > 0 ? $", {ovl.Row}.{ovl.Field} {ovl.Was} -> {ovl.Now}" : ""));
                }

                // ---------------------------------------------------------- the feature AND its action
                //
                // ONE PACKAGE, TWO OBJECTS. `Actions` is an INSTANCED array and an instanced
                // reference is meant to name a SUBOBJECT of the owner. The first attempt shipped
                // the action as an asset of its own and pointed a cross-package import at it: the
                // bytes read back clean, the mod installed, and nothing happened in game.
                //
                // So the action is now a subobject in the feature's own package, which is the
                // shape the game's own assets use -- an InputAction's instanced Triggers array
                // holds `01 00 00 00 02 00 00 00`, a count and then the second export, one-based.
                var gfdName = $"/{pluginName}/{pluginName}";
                var gfdPath = $"{pluginName}/Content/{pluginName}.uasset";
                byte[]? gfdBytes = null;
                string? gfdWhy = null;
                var carriesAction = false;

                if (overlayPairs.Count > 0)
                {
                    if (GameFeaturePlugin.FindPairTemplate(provider, log) is not { } pairTemplate)
                        serious.Add("no package holding a root object and one subobject to author " +
                                    "the feature and its action from -- the overlays would ship " +
                                    "and do nothing");
                    else
                    {
                        gfdBytes = GameFeaturePlugin.AuthorPair(
                            provider, pairTemplate, gfdName, "DTO_Action",
                            GameFeaturePlugin.ModDataClass, GameFeaturePlugin.ModDataClassDefault,
                            GameFeaturePlugin.OverlayActionClass, GameFeaturePlugin.OverlayActionDefault,
                            out gfdWhy, log);
                        if (gfdBytes is null) warnings.Add($"no feature+action package -- {gfdWhy}");

                        if (gfdBytes is not null)
                        {
                            gfdBytes = PropertyAdd.AddTableOverlays(
                                provider, gfdBytes, gfdName,
                                GameFeaturePlugin.OverlayActionExportClass, overlayPairs,
                                out var ovWhy, pairTemplate);
                            if (ovWhy is not null)
                            { warnings.Add($"the overlay action is empty -- {ovWhy}"); gfdBytes = null; }
                        }

                        if (gfdBytes is not null)
                        {
                            gfdBytes = PropertyAdd.AddObjectArray(
                                provider, gfdBytes, gfdName, GameFeaturePlugin.ModDataExportClass,
                                GameFeaturePlugin.ActionsProperty,
                                [GameFeaturePlugin.SubobjectIndex], out var actWhy, pairTemplate);
                            if (actWhy is not null)
                            { warnings.Add($"the feature lists no action -- {actWhy}"); gfdBytes = null; }
                            else
                            {
                                carriesAction = true;
                                log?.Invoke($"      {pluginName} carries DTO_Action as its own " +
                                            $"subobject (index {GameFeaturePlugin.SubobjectIndex}), " +
                                            $"{overlayPairs.Count} overlay(s)");
                            }
                        }

                        if (gfdBytes is null)
                            serious.Add("this mod lays tables over, and the action that does it could " +
                                        "not be authored -- the overlays would ship and do nothing");
                    }
                }

                // A mod with no overlays -- every mod until now -- still gets a feature data, and
                // it is the one export kind that has always worked.
                gfdBytes ??= GameFeaturePlugin.AuthorData(provider, gfdTemplate, gfdName,
                                                          out gfdWhy, log);
                if (gfdBytes is null) warnings.Add($"no GameFeatureData -- {gfdWhy}");
                else
                {
                    var gfdId = PackageId.Of(gfdName);
                    chunks.Add(new IoStoreWriter.Chunk(gfdId, 0, 1 /* ExportBundleData */,
                                                       gfdBytes, gfdPath));
                    built.Add(new BuiltPart(pluginName, gfdName, gfdId, gfdPath,
                                            gfdBytes.Length, gfdTemplate, IsCopy: true));
                    Register(new RegistryAssets.NewAsset(
                        PackageName: gfdName,
                        AssetName: pluginName,
                        PackagePath: $"/{pluginName}",
                        ClassPackage: ActionModule,
                        ClassName: GameFeaturePlugin.ModDataExportClass,
                        Tags: []));

                    // The action is INSIDE that package now, so it is registered against the same
                    // package name rather than one of its own.
                    if (carriesAction)
                        Register(new RegistryAssets.NewAsset(
                            PackageName: gfdName,
                            AssetName: "DTO_Action",
                            PackagePath: $"/{pluginName}",
                            ClassPackage: ActionModule,
                            ClassName: GameFeaturePlugin.OverlayActionExportClass,
                            Tags: []));
                }
            }
            else warnings.Add("no package small enough to author a GameFeatureData from");
        }

        // The container id: any self-consistent value works, because the runtime derives the
        // TWO ASSETS AT ONE PACKAGE PATH -- said out loud, because the container cannot say it.
        //
        // Chunks are keyed by package id, so a second asset claiming a name the mod already used
        // does not fail: it REPLACES the first, and the mod ships looking complete. That is how
        // the DLT-20A lost its 4096 body texture to a 512 copy authored from the cell's template,
        // and nothing in the build, the read-back or the game said a word.
        //
        // Naming texture copies after the texture they replace makes the common case impossible
        // rather than unlikely, which is the real fix. This is the backstop for every other way
        // two assets can land on one name -- and it warns rather than refuses, because which of
        // the two the author wanted is not something a build can know.
        foreach (var clash in built.GroupBy(b => b.PackageName, StringComparer.OrdinalIgnoreCase)
                                   .Where(g => g.Count() > 1))
            serious.Add($"{clash.Key} is written {clash.Count()} times -- by " +
                        $"{string.Join(" and ", clash.Select(b => b.Id).Distinct())}. They share " +
                        "one package, so only the last one survives; give one of them a name of " +
                        "its own.");

        // Said once, here, so a serious note reaches every caller that reads only `warnings`.
        warnings.AddRange(serious);

        // header chunk's id from the one recorded in the toc. Naming it after the container keeps
        // it reproducible.
        var containerId = PackageId.Of(containerName);

        // EARLY DELIVERY: split by chunk id -- every chunk of one override, payloads included,
        // carries that override's id. Early packages leave the plugin container entirely.
        var earlyIds = built.Where(b => b.IsEarly).Select(b => b.ChunkId).ToHashSet();
        var earlyParts = built.Where(b => earlyIds.Contains(b.ChunkId)).ToList();
        var earlyChunks = chunks.Where(c => earlyIds.Contains(c.Id)).ToList();
        chunks.RemoveAll(c => earlyIds.Contains(c.Id));

        // SORTED BY PACKAGE ID, and this is not tidiness.
        //
        // The engine finds a package's store entry by looking its id up in this list and taking
        // the entry at the same index -- with a BINARY SEARCH. On an unsorted list that finds some
        // packages and misses others, and a package whose entry is missed is indistinguishable
        // from one that was never shipped: the menu draws a registry tile with no name that equips
        // nothing.
        //
        // The game's own containers are sorted without exception -- 33,459 packages in pakchunk0
        // and 79,109 in pakchunk1, measured with `--hdrsort`. Ours were in insertion order, which
        // is why a one-part mod never showed it and a mod whose ids happened to fall in order
        // worked anyway. A two-helmet mod had its first part perfect and its second inert, with
        // the package, the registry entry, the container entry and the mesh all correct.
        //
        // Deduplicated as well: two entries with one id make the search's result depend on which
        // the engine lands on. The clash check above has already said so in the author's terms.
        ContainerHeader HeaderFor(ulong id, IEnumerable<BuiltPart> parts)
        {
            var h = new ContainerHeader { ContainerId = id };
            var orderedParts = parts
                .GroupBy(b => b.ChunkId)
                .Select(g => (Id: g.Key,
                              // The template's entry, NOT an empty one. See StoreEntries for what an
                              // empty entry costs: a crash in the loader with no message.
                              Entry: storeFor.TryGetValue(g.Key, out var e)
                                  ? e
                                  : new ContainerHeader.StoreEntry(new List<ulong>(), new List<byte[]>())))
                .OrderBy(x => x.Id)
                .ToList();
            foreach (var (pid, entry) in orderedParts)
            {
                h.PackageIds.Add(pid);
                h.Store.Add(entry);
            }
            // A 32-bit false: this container declares no soft package references.
            h.SoftPackageReferences = new byte[4];
            return h;
        }

        var header = HeaderFor(containerId, built.Where(b => !earlyIds.Contains(b.ChunkId)));
        chunks.Add(new IoStoreWriter.Chunk(containerId, 0, ContainerHeader.ChunkType,
                                           header.Write(), null));

        var utoc = Path.Combine(outDir, containerName + ".utoc");
        var ucas = Path.Combine(outDir, containerName + ".ucas");
        var pak = Path.Combine(outDir, containerName + ".pak");

        IoStoreWriter.Write(utoc, ucas, containerId, chunks);

        EarlyFiles? early = null;
        if (earlyParts.Count > 0)
        {
            var earlyName = EarlyLoad.ContainerName(containerName);
            var earlyId = PackageId.Of(earlyName);
            earlyChunks.Add(new IoStoreWriter.Chunk(earlyId, 0, ContainerHeader.ChunkType,
                                                    HeaderFor(earlyId, earlyParts).Write(), null));
            var eUtoc = Path.Combine(outDir, earlyName + ".utoc");
            var eUcas = Path.Combine(outDir, earlyName + ".ucas");
            var ePak = Path.Combine(outDir, earlyName + ".pak");
            IoStoreWriter.Write(eUtoc, eUcas, earlyId, earlyChunks);

            // PakWriter refuses a pak with no files, and the engine needs the .pak to mount the trio:
            // one text file at a virtual path nothing reads.
            var earlyPlugin = ModInstall.PluginName(containerName);
            var earlyPackages = earlyParts.Select(p => p.PackageName)
                                          .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            PakWriter.Write(ePak, $"{MountPoint}EarlyLoad/{earlyPlugin}/",
                new[] { new PakWriter.Entry("README.txt",
                    System.Text.Encoding.UTF8.GetBytes(EarlyLoad.Readme(earlyPlugin, earlyPackages))) });

            early = new EarlyFiles(earlyName, ePak, eUcas, eUtoc, earlyPackages);
            foreach (var p in earlyPackages)
                log?.Invoke($"   {p}: loads before mods are mounted -- delivered early as {earlyName} " +
                            "(Content/Paks/~mods)");
        }

        if (AsPlugin)
        {
            // THE MOUNT POINT IS THE PLUGIN'S OWN FOLDER, and both of the runtime's two
            // requirements follow from it. Read out of the shipped executable rather than guessed:
            //
            //   UBitReactorModRuntimeSubsystem::RefreshAvailableMods  0x06A336C0
            //       scans FPaths::ProjectModsDir() for "*.pak"
            //
            //   UBitReactorModRuntimeSubsystem::TryMountMod           0x06A34C80
            //       mount point, then append "AssetRegistry.bin" (17 bytes, 0x0AAFA690),
            //       then BitReactorModRuntimeUtils::LoadAssetRegistry  0x06A35610
            //
            //   BitReactorModRuntimeUtils::GetUPluginFilePathFromPakFile  0x06A35440
            //       mount point, drop its trailing slash, FPathViews::GetPathLeaf 0x018178E0,
            //       append ".uplugin" (0x0A9AA868)
            //
            // So: the registry sits at the ROOT OF THE MOUNT, and the descriptor is named after
            // the mount's LAST FOLDER. Mounting at Plugins/GameFeatures/<Name>/ satisfies both at
            // once, and puts the descriptor where the engine will accept it as a game feature --
            // FGameFeaturePluginTemplateDescription validates that one lives under that folder.
            //
            // The first attempt mounted at the project folder and put the registry three
            // directories below it, because that is where a plugin lives ON DISK -- which is not
            // the same question as where it lives INSIDE THE PAK. The runtime looked exactly where
            // it says it looks, found nothing, and the mod did not appear. No error: this build
            // has no engine logging, so a wrong path and a wrong format are the same silence.
            var plugin = containerName.EndsWith("_P", StringComparison.Ordinal)
                ? containerName[..^2] : containerName;
            var pluginMount = $"{MountPoint}Plugins/GameFeatures/{plugin}/";

            var own = additive.Write();

            // THE MOD'S OWN GAMEPLAY TAGS, travelling with it.
            //
            // A tag the mod invents has to be registered or nothing matches it, and the only place
            // to register one is the plugin's own config -- the game ships no config on disk at
            // all. The first mod to do this had its tag files typed in by hand AFTER installing,
            // which works exactly once, on one machine.
            var tagEntries = new List<PakWriter.Entry>
            {
                new(plugin + ".uplugin",
                    System.Text.Encoding.UTF8.GetBytes(GameFeaturePlugin.Uplugin(
                        plugin, mod.DisplayName, mod.Description, mod.Author))),
                new("AssetRegistry.bin", own),
            };
            if (shipTags.Count > 0)
            {
                tagEntries.Add(new PakWriter.Entry($"Config/Tags/{plugin}.ini",
                    System.Text.Encoding.UTF8.GetBytes(GameFeaturePlugin.TagList(shipTags))));
                tagEntries.Add(new PakWriter.Entry("Config/DefaultGameplayTags.ini",
                    System.Text.Encoding.UTF8.GetBytes(GameFeaturePlugin.TagSettings(shipTags))));
            }
            PakWriter.Write(pak, pluginMount, tagEntries.ToArray());

            // ALSO LOOSE, beside the pak.
            //
            // There are two ways this game could reach a plugin, and they want the same two files
            // in different places. The mod runtime reads them from INSIDE a pak dropped in the
            // mods folder -- which is the route above, and which this build has switched off: the
            // shipped config carries no BitReactorModRuntimeSubsystemSettings section at all, so
            // the TrackedEntitlements list its gate consults is empty and TryLoadMod never runs.
            //
            // Unreal's own plugin manager reads them as LOOSE FILES in a plugin folder, and it
            // scans the mods directory for those by a path the packaged plugin manifest does not
            // short-circuit -- mods are not in the manifest, which is the point of the folder.
            // Writing both costs a kilobyte and lets one build be tried either way.
            var loosePlugin = Path.Combine(outDir, plugin + ".uplugin");
            var looseRegistry = Path.Combine(outDir, "AssetRegistry.bin");
            File.WriteAllText(loosePlugin,
                              GameFeaturePlugin.Uplugin(plugin, mod.DisplayName, mod.Description, mod.Author));
            File.WriteAllBytes(looseRegistry, own);

            // Loose as well, for the same reason the descriptor is: Unreal's plugin manager reads
            // a mod folder as files on disk, and that is the route this game actually takes.
            var extras = new List<(string Relative, string Absolute)>();
            if (shipTags.Count > 0)
            {
                var tagsDir = Path.Combine(outDir, "Config", "Tags");
                Directory.CreateDirectory(tagsDir);
                var listIni = Path.Combine(tagsDir, plugin + ".ini");
                var settingsIni = Path.Combine(outDir, "Config", "DefaultGameplayTags.ini");
                File.WriteAllText(listIni, GameFeaturePlugin.TagList(shipTags));
                File.WriteAllText(settingsIni, GameFeaturePlugin.TagSettings(shipTags));
                extras.Add((Path.Combine("Config", "Tags", plugin + ".ini"), listIni));
                extras.Add((Path.Combine("Config", "DefaultGameplayTags.ini"), settingsIni));

                foreach (var t in shipTags)
                    log?.Invoke($"      registers its own gameplay tag {t}");
            }

            log?.Invoke($"      shipped as a Game Feature plugin, mounted at {pluginMount}");
            log?.Invoke($"      {plugin}.uplugin and a registry of {additive.AssetCount} entr(ies) " +
                        $"in {own.Length:N0} bytes -- not a copy of the game's " +
                        $"{registry.AssetCount:N0}, so it collides with nobody");
            return new Result(pak, ucas, utoc, built, skipped, warnings,
                              new PluginFiles(plugin, loosePlugin, looseRegistry, extras), serious,
                              looseCopies, missionOverrides.Concat(slotOverrides).ToList(), missionCopies, spawned,
                              slotStamps, early);
        }

        var merged = registry.Write();
        PakWriter.Write(pak, MountPoint,
            new[] { new PakWriter.Entry("AssetRegistry.bin", merged) });

        return new Result(pak, ucas, utoc, built, skipped, warnings, null, serious, looseCopies,
                          missionOverrides.Concat(slotOverrides).ToList(), missionCopies, spawned,
                          slotStamps, early);
    }

}

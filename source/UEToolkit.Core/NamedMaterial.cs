using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;

namespace UEToolkit.Core;

/// <summary>
/// A GAME MATERIAL AN AUTHOR CHOSE FOR A SLOT, carried through to the mesh that wears it.
///
/// "Choose..." in the editor writes <see cref="ModMaterial.Material"/> and nothing more. The build
/// used to read it only on two paths -- a slot that also carried textures (then the repaint made a
/// copy of the chosen material), and a part that imported a model file (then the slot was given an
/// import of it). A part that KEPT the game's mesh and chose a material for it fell through both:
/// the choice was dropped without a word and the part wore the base mesh's own material. Reported
/// by an author who picked Anakin's head material for an HM00 head.
///
/// The mesh is what names its materials, so the part has to wear a copy of the mesh to wear a
/// different material. That copy exists already -- it is what a repaint makes -- and so does the
/// way to point it elsewhere: a REDIRECT, which moves the copy's import of one package to another.
/// This class decides the redirects for the slots that name a material and paint nothing, and moves
/// the second half of the import with them, because a redirect alone is not enough here:
///
///     the store entry   ImportedPackages[n]            WHICH package the slot loads
///     the package       ImportedPublicExportHashes[h]  which EXPORT inside it
///
/// A repainted material is a copy that keeps its template's export hash, so repainting never has to
/// move it. A DIFFERENT game material has an export of a different name and so a different hash,
/// and a redirect that moved only the package would point at an export the new package does not
/// have. <see cref="ImportRepoint.MoveHashes"/> is the existing half that moves it.
///
/// It also reads back: <see cref="SlotsOf(USkeletalMesh)"/> is the one answer to "which material
/// does this mesh wear on this slot", for the build deciding what to redirect and for the verifier
/// asking what the BUILT mesh really imports.
/// </summary>
public static class NamedMaterial
{
    /// <summary>One material slot of a mesh: its index, the name a tint fragment aims at, and the package it wears.</summary>
    public sealed record Slot(int Index, string Name, string Package);

    /// <summary>One slot to be moved onto the material its author chose.</summary>
    public sealed record Worn(int Slot, string SlotName, string From, string To);

    /// <summary>
    /// One import whose PACKAGE a redirect moves and whose export HASH therefore has to follow:
    /// <paramref name="HashesFrom"/> is the package whose export the new import names. For a game
    /// material it is the target itself; for a repainted copy it is the material the copy was made
    /// from, because a copy keeps its template's export hash.
    /// </summary>
    public sealed record HashMove(string From, string To, string HashesFrom);

    /// <summary>
    /// Every slot a mesh declares, with the package its material import resolves to -- empty where
    /// it does not resolve. The resolution is CUE4Parse's own, through the import map and the
    /// export hash, so an import whose hash is wrong reads back as empty rather than as the
    /// package it was meant to name.
    /// </summary>
    public static List<Slot> SlotsOf(USkeletalMesh mesh)
    {
        var slots = new List<Slot>();
        var declared = mesh.SkeletalMaterials ?? [];
        for (var i = 0; i < declared.Length; i++)
        {
            string package;
            try { package = AssetLoader.Read(() => declared[i].Material?.Load()?.Owner?.Name) ?? ""; }
            catch (Exception) { package = ""; }
            slots.Add(new Slot(i, declared[i].MaterialSlotName.Text, package));
        }
        return slots;
    }

    /// <summary>The slots of a mesh in the mounted game, or null with a reason.</summary>
    public static List<Slot>? SlotsOf(IFileProvider provider, string meshPackage, out string? why)
    {
        why = null;
        try
        {
            if (AssetLoader.LoadPrimary(provider, meshPackage) is not USkeletalMesh mesh)
            { why = $"{Leaf(meshPackage)} is not a skeletal mesh"; return null; }
            return SlotsOf(mesh);
        }
        catch (Exception e) { why = $"{Leaf(meshPackage)}: {e.Message}"; return null; }
    }

    /// <summary>
    /// Decide which slots of the game's mesh to move onto which materials.
    ///
    /// Everything that cannot be done is said in <paramref name="problems"/>, by slot and by name,
    /// and the slot is left wearing what it wore: a choice that quietly does nothing is the fault
    /// this exists to end.
    /// </summary>
    /// <param name="slots">
    /// The game mesh's slots as <see cref="SlotsOf(IFileProvider, string, out string?)"/> read them
    /// (null with <paramref name="why"/> when it could not be read). Passed in so the caller reads
    /// the mesh once for this plan and for the swap check beside it.
    /// </param>
    /// <param name="named">The slots that name a material and paint nothing, as (slot, package).</param>
    /// <param name="redirects">
    /// The redirects this copy already has -- the repainted slots. A redirect is per PACKAGE, so two
    /// slots wearing one material cannot be sent to two different places.
    /// </param>
    public static List<Worn> Plan(IFileProvider provider, string meshPackage, string label,
                                  IReadOnlyList<Slot>? slots, string? why,
                                  IReadOnlyList<(int Slot, string Package)> named,
                                  IReadOnlyCollection<AssetCopy.Redirect> redirects,
                                  List<string> problems, Action<string>? log)
    {
        var worn = new List<Worn>();

        // Where each material already goes, so a second slot cannot claim it for somewhere else.
        var going = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in redirects) going[r.FromPackage] = r.ToPackage;

        foreach (var (slot, target) in named.OrderBy(n => n.Slot))
        {
            var n = slot + 1;
            var leaf = Leaf(target);

            if (!IsMaterial(provider, target, out var targetWhy))
            {
                problems.Add($"{label}: material {n} names {leaf}, which is not a material the " +
                             $"game has ({targetWhy}) -- it was NOT applied, and the slot keeps " +
                             "the material the game's mesh gives it. Choose one from the list, or " +
                             "check the name.");
                continue;
            }
            if (slots is null)
            {
                problems.Add($"{label}: material {n} names {leaf} and was NOT applied -- {why}");
                continue;
            }
            if (slot >= slots.Count)
            {
                problems.Add($"{label}: material {n} names {leaf} and was NOT applied -- the " +
                             $"game's mesh {Leaf(meshPackage)} has only {slots.Count} material " +
                             $"slot(s), so there is no slot {n} to put it on. An extra material " +
                             "needs an extra object in a model file of your own.");
                continue;
            }

            var here = slots[slot];
            if (here.Package.Length == 0)
            {
                problems.Add($"{label}: material {n} names {leaf} and was NOT applied -- slot {n} " +
                             $"('{here.Name}') of {Leaf(meshPackage)} has no material that loads, " +
                             "so there is no import to point at it.");
                continue;
            }
            if (string.Equals(here.Package, target, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"      material {n} names {leaf}, which the game's mesh already " +
                            $"wears on '{here.Name}'");
                continue;
            }
            if (going.TryGetValue(here.Package, out var elsewhere) &&
                !string.Equals(elsewhere, target, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{label}: material {n} names {leaf} and was NOT applied -- slot {n} " +
                             $"('{here.Name}') wears {Leaf(here.Package)} in the game's mesh, and " +
                             "another slot sharing that material has already been sent somewhere " +
                             "else. One material cannot go to two places.");
                continue;
            }

            // THE OTHER SLOTS THAT WEAR THE SAME MATERIAL come along. A redirect moves the
            // import, and the import is shared; moving one slot alone would need a second import
            // and a new section binding, which is a different mechanism. Said rather than hidden.
            var alsoMoves = slots.Where(s => s.Index != slot &&
                string.Equals(s.Package, here.Package, StringComparison.OrdinalIgnoreCase) &&
                !named.Any(x => x.Slot == s.Index &&
                                string.Equals(x.Package, target, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (alsoMoves.Count > 0)
                problems.Add($"{label}: material {n} wears {leaf} -- and so does " +
                             string.Join(" and ", alsoMoves.Select(s => $"slot {s.Index + 1} ('{s.Name}')")) +
                             $", because the game's mesh uses one material ({Leaf(here.Package)}) for " +
                             "both and a mod can only move them together.");

            going[here.Package] = target;
            worn.Add(new Worn(slot, here.Name, here.Package, target));
            log?.Invoke($"      material {n} ('{here.Name}'): {Leaf(here.Package)} -> {leaf}");
        }
        return worn;
    }

    /// <summary>
    /// Move the export hashes with the redirects, in the mesh's own package bytes. See the class
    /// remarks for why the redirect alone leaves the import pointing at an export that is not there.
    ///
    /// <paramref name="store"/> is the mesh's ORIGINAL store entry: the position of a package in
    /// it is what the package's import map indexes, and adding imports only ever appends, so that
    /// position still holds for a package an import has been spliced into.
    /// </summary>
    public static byte[] MoveHashes(IFileProvider provider, byte[] package,
                                    ContainerHeader.StoreEntry store, IEnumerable<HashMove> moves,
                                    string label, List<string> problems, Action<string>? log)
    {
        var list = moves.GroupBy(m => m.From, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (list.Count == 0) return package;

        var hashesFrom = list.GroupBy(m => m.To, StringComparer.Ordinal)
                             .ToDictionary(g => g.Key, g => g.First().HashesFrom, StringComparer.Ordinal);
        return ImportRepoint.MoveHashes(
            provider, package, store,
            list.Select(m => new KeyValuePair<string, string>(m.From, m.To)).ToList(),
            s => s, label, problems, log, hashesFrom: t => hashesFrom.TryGetValue(t, out var h) ? h : t);
    }

    /// <summary>
    /// Whether every slot that names a material and paints nothing is ALREADY wearing it in the
    /// game's own mesh -- in which case there is nothing to carry, and the part keeps pointing at
    /// the game's mesh.
    /// </summary>
    public static bool AllWorn(IFileProvider provider, string meshPackage, IReadOnlyList<ModMaterial> materials)
    {
        var slots = SlotsOf(provider, meshPackage, out _);
        if (slots is null) return false;
        for (var i = 0; i < materials.Count; i++)
        {
            if (!materials[i].IsNamedOnly) continue;
            var want = ModBuild.PackageOf(materials[i].Material) ?? materials[i].Material;
            if (i >= slots.Count || !string.Equals(slots[i].Package, want, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The PACKAGE and EXPORT HASH a built mesh's slot imports, read off the bytes -- no resolution.
    ///
    /// <see cref="SlotsOf(USkeletalMesh)"/> resolves through CUE4Parse, which loads the imported
    /// package from the mounted game; a copy the MOD made is not mounted there, so a repainted
    /// slot reads back as empty even when it is right. This reads the import itself instead: the
    /// slot's FPackageIndex from the material array (<see cref="SlotReferences"/>), through the
    /// import map to a (package, hash) pair, with the package taken from the container's store
    /// entry. A caller then compares the pair with what the mod's own copy publishes.
    ///
    /// NULL MEANS "COULD NOT READ IT", never "it is wrong": the caller reports that as a note, and
    /// keeps Bad for an import that WAS read and names the wrong thing.
    /// </summary>
    public static (ulong Package, ulong Hash)? ImportOfSlot(byte[] package, ContainerHeader.StoreEntry store,
                                                           USkeletalMesh mesh, int slot, out string? why)
    {
        why = null;
        try
        {
            var declared = mesh.SkeletalMaterials ?? [];
            if (slot < 0 || slot >= declared.Length) { why = $"the mesh has {declared.Length} slot(s)"; return null; }

            var zen = ZenPackage.Read(package);
            var refs = SlotReferences(package, zen, declared, out why);
            if (refs is null) return null;

            var fpi = refs[slot];
            if (fpi >= 0) { why = "the slot's material reference is not an import"; return null; }

            var map = zen.ImportMap;
            if (-fpi - 1 >= map.Length || ZenPackage.KindOf(map[-fpi - 1]) != ZenPackage.ImportKind.PackageImport)
            { why = "the slot's material is not a package import"; return null; }

            var (pkgAt, hashAt) = ZenPackage.PackageImportOf(map[-fpi - 1]);
            var hashes = zen.ImportedPublicExportHashes;
            if (pkgAt >= store.ImportedPackages.Count || hashAt < 0 || hashAt >= hashes.Length)
            { why = "the import names a package or hash that is not declared"; return null; }

            return (store.ImportedPackages[pkgAt], hashes[hashAt]);
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    /// <summary>
    /// Every slot's material reference (the FPackageIndex that begins its FSkeletalMaterial), read
    /// from the mesh's bytes by what must be true of the array rather than by where it ends.
    ///
    /// <see cref="CookedMesh.FindMaterials"/> finds the array by its END: the bone count that
    /// follows it. That is sound for a head or a garment and refuses a weapon piece (1-2 bones,
    /// "a lone 1 turns up everywhere"), so a verifier built on it could not read the barrel of a
    /// gun. This finds it by its START and its STRIDE instead: the slot count, then each slot's NAME
    /// (an index into this package's name table, four bytes into its entry) at one constant spacing.
    /// A real array has every slot's name at the same stride; a coincidence of one number does not.
    /// A one-slot mesh has no stride to check and rests on the count and the name alone, so the
    /// search is confined to the mesh's export data (the header's tables hold small numbers that
    /// match by accident) and keeps only an array whose references are package imports. More than
    /// one DISTINCT answer is refused as ambiguous rather than guessed between.
    ///
    /// A numbered slot name (one ending in `_2`) is stored SPLIT -- stem in the name table, number beside
    /// it -- so the table is searched for the stem (<c>FName.PlainText</c>), which is what is stored.
    /// </summary>
    private static int[]? SlotReferences(byte[] data, ZenPackage zen,
                                         CUE4Parse.UE4.Assets.Exports.SkeletalMesh.FSkeletalMaterial[] slots,
                                         out string? why)
    {
        why = null;
        var count = slots.Length;
        if (count == 0) { why = "the mesh reports no material slots"; return null; }

        var name = new uint[count];
        for (var j = 0; j < count; j++)
        {
            var stem = slots[j].MaterialSlotName.PlainText;
            var at = zen.Names.FindIndex(n => string.Equals(n, stem, StringComparison.Ordinal));
            // The way the rest of this code stems a numbered name (ZenPackage.ParseNumber), as the
            // fallback: PlainText is exactly what the table holds for a name CUE4Parse has read.
            if (at < 0 && ZenPackage.ParseNumber(slots[j].MaterialSlotName.Text).Stem is { } parsed)
                at = zen.Names.FindIndex(n => string.Equals(n, parsed, StringComparison.Ordinal));
            if (at < 0) { why = $"slot {j + 1}'s name '{stem}' is not in the package name table"; return null; }
            name[j] = (uint) at;
        }

        const uint Index = 0x3FFFFFFF;
        var found = new List<int[]>();
        // The array is serialised in the mesh's EXPORT DATA, after the header; the header's name
        // table and maps hold small numbers that match by accident.
        var first = Math.Max(8, (int) BitConverter.ToUInt32(data, 4) + 8);
        var imports = zen.ImportMap;
        bool IsImport(int fpi) => fpi < 0 && -fpi - 1 < imports.Length &&
                                  ZenPackage.KindOf(imports[-fpi - 1]) == ZenPackage.ImportKind.PackageImport;
        for (var at = first; at + 4 <= data.Length; at++)
        {
            if ((BitConverter.ToUInt32(data, at) & Index) != name[0]) continue;
            if (BitConverter.ToInt32(data, at - 8) != count) continue;

            if (count == 1)
            {
                if (IsImport(BitConverter.ToInt32(data, at - 4))) found.Add([BitConverter.ToInt32(data, at - 4)]);
                continue;
            }

            // The entry is at least thirty bytes (a reference, a name, the UV channel info); the
            // stride is whatever spacing puts every slot's own name where it should be.
            for (var stride = 28; stride <= 512 && at + (count - 1) * stride + 4 <= data.Length; stride++)
            {
                var every = true;
                for (var j = 1; j < count && every; j++)
                    every = (BitConverter.ToUInt32(data, at + j * stride) & Index) == name[j];
                if (!every) continue;

                var refs = new int[count];
                for (var j = 0; j < count; j++) refs[j] = BitConverter.ToInt32(data, at - 4 + j * stride);
                if (refs.All(r => r == 0 || IsImport(r))) found.Add(refs);
                // NO `break` HERE: the first stride that fits is not necessarily the true one. A
                // short false stride at the same position used to END the search and hide the real
                // array behind it; now every stride is collected, and two that disagree are the
                // ambiguity the check below refuses -- a note, never a guess.
            }
        }

        if (found.Count == 0) { why = $"no material array of {count} slot(s) was found"; return null; }
        if (found.Any(f => !f.SequenceEqual(found[0]))) { why = "more than one place looks like the material array"; return null; }
        return found[0];
    }

    /// <summary>
    /// A material the template SWAPS at run time, named by the author for the same slot.
    ///
    /// The game's HM00 head carries a <c>CustomizationFragmentMaterialSwap</c> per skin tone, each
    /// aimed at the slot <c>MI_Head</c>. Whatever the mesh imports for that slot, the game replaces
    /// it when the wearer has a race -- so a material chosen for it would be built, verified, and
    /// never seen. Said at build time, because nothing else in the chain can see it.
    /// </summary>
    /// <param name="named">Every slot the author named a material for: (slot, the name it will have, the material).</param>
    public static List<string> SwapProblems(string label, string templateName,
                                            IReadOnlyList<PartDetails.MaterialSwap>? swaps,
                                            IEnumerable<(int Slot, string SlotName, string Material)> named)
    {
        var said = new List<string>();
        if (swaps is not { Count: > 0 }) return said;

        foreach (var (slot, slotName, material) in named)
        {
            if (slotName.Length == 0) continue;
            var hits = swaps.Where(s => s.Slots.Any(
                x => string.Equals(x, slotName, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 0) continue;

            var first = hits.Select(h => Leaf(h.Replacement)).FirstOrDefault(l => l.Length > 0) ?? "another material";
            said.Add($"{label}: material {slot + 1} wears {Leaf(material)}, but the game's own data " +
                     $"swaps the material on this part's '{slotName}' slot ({hits.Count} swap(s) in " +
                     $"{templateName}, such as {first} -- the skin-tone variants of a head), so your " +
                     "choice will very likely be REPLACED in game. That is read from the template's " +
                     "fragments, not seen in game. Start from a part whose template has no swaps on " +
                     "that slot; for a head, Anakin's is one.");
        }
        return said;
    }

    private static bool IsMaterial(IFileProvider provider, string package, out string why)
    {
        why = "";
        try
        {
            var asset = AssetLoader.LoadPrimary(provider, package);
            if (asset is UMaterialInterface) return true;
            why = asset is null ? "nothing by that name could be read" : $"it is a {asset.ExportType}, not a material";
            return false;
        }
        catch (Exception) { why = "no package by that name"; return false; }
    }

    /// <summary>The asset's own name: the part after the last slash, without an object suffix.</summary>
    private static string Leaf(string path)
    {
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var dot = leaf.IndexOf('.');
        return dot >= 0 ? leaf[..dot] : leaf;
    }
}

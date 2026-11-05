using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace UEToolkit.Core;

/// <summary>
/// Repaint a part: the chain from a mesh to the pixels on it, copied into a mod.
///
/// A character's appearance is four packages deep -- part names a mesh, mesh names a material
/// instance in one of its slots, the instance names textures, and the textures hold the pages. A
/// mod that copies only the mesh has changed nothing anyone can see, because the copy still names
/// the game's material, which still names the game's textures.
///
/// So every link gets copied and every reference gets pointed at the copy. There is nothing
/// material-specific in that operation -- <see cref="AssetCopy.Rename"/> does the whole thing --
/// and what lives here is the part that IS specific: working out which textures belong to this
/// part rather than to the engine, and which of the author's images replaces which.
///
/// NOTHING IS OVERWRITTEN. Every asset produced is a new package in the mod's own folder with its
/// own package id; the game's originals are untouched and every other part that shares them keeps
/// working. That is the same architecture the rest of the builder already uses, and it is what
/// makes a mod removable by deleting three files.
/// </summary>
public static class MaterialSkin
{
    /// <summary>One texture the part wears, and every parameter that binds it.</summary>
    public sealed record Skin(string TexturePackage, string TextureName, List<string> Parameters,
                              bool IsVirtual, int Width, int Height, EPixelFormat Format)
    {
        /// <summary>_D, _MSK, _N -- the game's own naming, and how an author names their files.</summary>
        public string Suffix =>
            TextureName.LastIndexOf('_') is var at && at >= 0 && at < TextureName.Length - 1
                ? TextureName[(at + 1)..] : "";

        public string Describe =>
            $"{TextureName} {Width}x{Height} {Format} {(IsVirtual ? "virtual" : "plain")}" +
            $" <- {string.Join(", ", Parameters)}";
    }

    /// <summary>
    /// What a mesh's appearance is made of.
    ///
    /// <see cref="Slot"/> and <see cref="SlotCount"/> are what tell a caller whether a slot it
    /// wants to paint EXISTS on the game's mesh. One that does is repainted from its own material;
    /// one past the end has none, and is painted from this one instead.
    /// </summary>
    public sealed record Chain(string MeshPackage, string SlotName, string MaterialPackage,
                               string MaterialName, List<Skin> Skins, List<string> Shared,
                              int Slot = 0, int SlotCount = 1, bool IsCloth = false);

    /// <summary>
    /// Whether this material is parameterised as CLOTH rather than as a hard surface.
    ///
    /// Every character material on this kit shares one parent, so the parent says nothing. What
    /// separates the undersuit from the armour plate beside it is which parameters the INSTANCE
    /// overrides: `MI_Clo010_SUIT` sets `Cloth Fuzz Tint`, `EdgeExp` and `EdgeBright` and turns
    /// the tint intensity off; `MI_Clo010_LEGS` sets none of them.
    ///
    /// It matters because a kit mesh's third material IS the undersuit, so an author whose third
    /// object is armour has their armour painted into a cloth shader -- and it reads matte in game
    /// however the roughness mask is painted. Reported by an author as "extra materials after 2 are
    /// going matte", which is exactly the slot where the suit sits.
    ///
    /// Read off the instance's OWN overrides rather than through GetParams, which walks the parent
    /// chain and would report the cloth parameter for every material in the game.
    /// </summary>
    private static bool ClothOf(UMaterialInterface mat) =>
        PartDetails.Unwrap(PartDetails.Value(mat.Properties, "VectorParameterValues"))
            is UScriptArray vectors &&
        vectors.Properties.Any(p =>
            PartDetails.Unwrap(p.GenericValue) is FStructFallback entry &&
            PartDetails.Unwrap(PartDetails.Value(entry.Properties, "ParameterInfo"))
                is FStructFallback info &&
            PartDetails.Value(info.Properties, "Name")?.ToString() is { } n &&
            n.Contains("Cloth", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ reading the chain

    /// <summary>
    /// Follow a mesh to its material and that material's own textures.
    ///
    /// "Its own" is the judgement here, and it is made by LOCATION rather than by a name list: a
    /// character material binds two dozen textures and all but a handful are engine defaults --
    /// blue noise, wound decals, the placeholder virtual colour -- shared by every character in
    /// the game. Anything under the material's own kit folder is the part's; anything else is
    /// reported as shared and left alone. A name list would need updating for every kit; a folder
    /// rule is the same rule the cooker used to lay them out.
    /// </summary>
    /// <param name="slot">
    /// Which material slot to follow. -1, the default, takes the first that loads -- which is what
    /// a one-material part wants and what every caller wanted before meshes had more than one.
    /// </param>
    public static Chain? Read(IFileProvider provider, string meshPackage, out string? why,
                              int slot = -1)
    {
        why = null;
        try
        {
            if (AssetLoader.LoadPrimary(provider, meshPackage) is not USkeletalMesh mesh)
            { why = $"{meshPackage} is not a skeletal mesh"; return null; }

            var slots = mesh.SkeletalMaterials ?? [];
            var mats = mesh.Materials ?? [];
            if (mats.Length == 0) { why = $"{meshPackage} has no material slots"; return null; }

            // The FIRST slot with a material that loads. A part in this game wears one, and a mesh
            // with several would need the author to say which -- so say what was chosen rather
            // than silently taking slot zero when slot zero is empty.
            if (slot >= mats.Length)
            { why = $"{meshPackage} has {mats.Length} material slot(s), so there is no slot " +
                    $"{slot + 1} to read"; return null; }

            for (var i = 0; i < mats.Length; i++)
            {
                if (slot >= 0 && i != slot) continue;
                if (mats[i]?.Load<UMaterialInterface>() is not { } mat) continue;

                var matPackage = mat.Owner?.Name;
                if (string.IsNullOrEmpty(matPackage)) continue;

                var chain = ChainOf(provider, mat, matPackage, meshPackage,
                                    i < slots.Length ? slots[i].MaterialSlotName.Text : $"[{i}]",
                                    i, mats.Length, out why);
                if (chain is not null) return chain;
                return null;
            }

            why = slot >= 0
                ? $"{meshPackage} slot {slot + 1} has no material that loads"
                : $"{meshPackage} has {mats.Length} material slot(s) and none of them loaded";
            return null;
        }
        catch (Exception e) { why = $"{meshPackage}: {e.Message}"; return null; }
    }

    /// <summary>
    /// A MATERIAL NAMED OUTRIGHT, with no mesh in the way.
    ///
    /// The case this exists for: "use that material, but with my own tint in it". Naming a game
    /// material and painting one of its textures were mutually exclusive until this -- naming got
    /// you the game's material verbatim, painting got you a copy of whatever the MESH happened to
    /// wear on that slot, and an author who wanted the first with one texture changed could ask
    /// for neither. Reported by an author (2026-09-11) in exactly those words: "specify a specific
    /// material from the game to use, THEN replace a texture within that material".
    ///
    /// The chain it returns carries no mesh and no slot count, because there is no mesh to ask:
    /// a caller that needs to know whether a slot EXISTS must still read the mesh for that.
    /// </summary>
    public static Chain? ReadMaterial(IFileProvider provider, string materialPackage, out string? why)
    {
        why = null;
        try
        {
            if (AssetLoader.LoadPrimary(provider, materialPackage) is not UMaterialInterface mat)
            { why = $"{materialPackage} is not a material"; return null; }
            var matPackage = mat.Owner?.Name;
            if (string.IsNullOrEmpty(matPackage))
            { why = $"{materialPackage} has no package of its own"; return null; }
            return ChainOf(provider, mat, matPackage, "", ModBuildSlotName(matPackage), 0, 1, out why);
        }
        catch (Exception e) { why = $"{materialPackage}: {e.Message}"; return null; }
    }

    /// <summary>
    /// `MI_Clo003_PACK` -> `MI_PACK`, duplicated from ModBuild.SlotNameFor rather than referenced
    /// because Core cannot see it from here -- it is only ever the reported name of a chain read
    /// from a material, never what gets written into a mesh, which stays ModBuild's decision.
    /// </summary>
    private static string ModBuildSlotName(string materialPackage)
    {
        var leaf = materialPackage[(materialPackage.LastIndexOf('/') + 1)..];
        var bits = leaf.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return bits.Length >= 3 && (bits[0].Equals("MI", StringComparison.OrdinalIgnoreCase) ||
                                    bits[0].Equals("M", StringComparison.OrdinalIgnoreCase))
            ? $"{bits[0].ToUpperInvariant()}_{bits[^1]}" : leaf;
    }

    /// <summary>
    /// One material's own textures, which is the half of <see cref="Read"/> that never needed a
    /// mesh. Split out so a material named by an author can be read the same way as one a mesh
    /// points at -- the two used to be one method, and only the mesh route existed.
    /// </summary>
    private static Chain? ChainOf(IFileProvider provider, UMaterialInterface mat, string matPackage,
                                  string meshPackage, string slotName, int slot, int slotCount,
                                  out string? why)
    {
        why = null;
        var kit = KitFolderOf(matPackage);
        var byPackage = new Dictionary<string, Skin>(StringComparer.OrdinalIgnoreCase);
        var shared = new List<string>();

        var mp = new CMaterialParams2();
        try { mat.GetParams(mp, EMaterialFormat.AllLayers); }
        catch (Exception e) { why = $"{matPackage}: {e.Message}"; return null; }

        foreach (var (parameter, value) in mp.Textures)
        {
            if (value is not UTexture2D tex) continue;
            var package = tex.Owner?.Name;
            if (string.IsNullOrEmpty(package)) continue;

            if (!package.StartsWith(kit, StringComparison.OrdinalIgnoreCase))
            { if (!shared.Contains(package)) shared.Add(package); continue; }

            // Keyed by PACKAGE, not by parameter. This game binds one texture under three
            // names -- "Base BSC VT", "PM_Diffuse" and its own asset name all point at the
            // diffuse -- and redirecting the package fixes every one of them at once.
            // Keyed by parameter, the same file would be copied three times and two of the
            // copies would go unreferenced.
            if (byPackage.TryGetValue(package, out var already))
            { already.Parameters.Add(parameter); continue; }

            var pd = tex.PlatformData;
            byPackage[package] = new Skin(
                package, tex.Name, new List<string> { parameter },
                pd?.VTData is not null,
                pd?.VTData is { } vt ? (int) vt.Width : pd?.SizeX ?? 0,
                pd?.VTData is { } vt2 ? (int) vt2.Height : pd?.SizeY ?? 0,
                pd?.VTData is not null ? EPixelFormat.PF_Unknown : tex.Format);
        }

        // The format for a virtual texture is the LAYER's, not the texture's -- a virtual
        // texture reports PF_Unknown at the top level and keeps the real one per layer.
        var skins = byPackage.Values
            .Select(s => s.IsVirtual ? s with { Format = LayerFormat(provider, s.TexturePackage) } : s)
            .OrderBy(s => s.TextureName, StringComparer.Ordinal).ToList();

        return new Chain(meshPackage, slotName, matPackage, mat.Name, skins, shared,
                         slot, slotCount, ClothOf(mat));
    }


    private static EPixelFormat LayerFormat(IFileProvider provider, string package)
    {
        try
        {
            return AssetLoader.LoadPrimary(provider, package) is UTexture2D t &&
                   t.PlatformData?.VTData is { LayerTypes.Length: > 0 } vt
                ? vt.LayerTypes[0] : EPixelFormat.PF_Unknown;
        }
        catch { return EPixelFormat.PF_Unknown; }
    }

    /// <summary>
    /// The kit a material belongs to: its folder's parent, so sibling folders come with it.
    ///
    /// `.../Clo000/Materials/MI_Clo000_HELM` gives `.../Clo000/`, which covers `.../Clo000/Textures/`
    /// and nothing outside the kit.
    /// </summary>
    private static string KitFolderOf(string packageName)
    {
        var dir = packageName[..(packageName.LastIndexOf('/') + 1)].TrimEnd('/');
        var up = dir.LastIndexOf('/');
        return up <= 0 ? dir + "/" : dir[..(up + 1)];
    }

    // ------------------------------------------------------------------ repainting it

    public sealed record Made(string Leaf, string Package, ulong Id, string ContainerPath,
                              int Bytes, string Template, bool IsTexture, int Redirected = 0);

    /// <param name="SharedTextures">
    /// The textures this repaint pointed at a copy an EARLIER repaint made (the build-wide
    /// <c>textureAlready</c> cache), as (the game texture it replaces, the copy it uses). They are
    /// not in <paramref name="Assets"/> -- nothing was authored -- so a caller asking "which copy
    /// of the tint mask does this part wear?" and reading only Assets gets no answer for exactly
    /// the repaint that shares one.
    /// </param>
    public sealed record Built(List<IoStoreWriter.Chunk> Chunks,
                               Dictionary<ulong, ContainerHeader.StoreEntry> Store,
                               List<Made> Assets, string MaterialPackage, List<string> Warnings,
                               List<(string Template, string Package)>? SharedTextures = null);

    /// <summary>
    /// Which of the author's images replaces which of the part's textures.
    ///
    /// Three ways to name a target, tried in order: the parameter ("Base BSC VT"), the texture's
    /// own asset name, or the SUFFIX the game already sorts them by (_D, _MSK, _N). The last is the
    /// one an author will use, because it is the convention their files already follow -- and
    /// matching on it means a folder of three TGAs needs no mapping written at all.
    /// </summary>
    public static Skin? Match(Chain chain, string key)
    {
        var hits = chain.Skins.Where(s =>
            s.Parameters.Any(p => p.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
            s.TextureName.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            s.Suffix.Equals(key.TrimStart('_'), StringComparison.OrdinalIgnoreCase)).ToList();

        // Exactly one, or none. Two matches means the key is ambiguous, and choosing one of them
        // paints the wrong channel -- a normal map written into the diffuse slot looks like a
        // lighting bug rather than like a mistyped key.
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>
    /// Copy the material and the textures the author replaced, all into the mod's folder.
    ///
    /// Returns the redirect the MESH copy still needs -- from the game's material to the mod's --
    /// as <see cref="Built.MaterialPackage"/>. The mesh is not touched here because the builder
    /// already copies it for its own reasons, and copying it twice would give the mod two meshes.
    /// </summary>
    /// <param name="leafSuffix">
    /// Appended to every asset name this makes, so two slots painted from the SAME game material
    /// do not fight over one package path.
    ///
    /// They would, and silently. A mesh's extra slots are painted from slot zero's material -- the
    /// game's mesh has only the one -- so both copies would be named after that material and after
    /// its textures, and the second would replace the first at the same package name with no error
    /// anywhere. That is the fault that cost the DLT-20A its body texture, arrived at from a
    /// second direction; the fix there was to name a copy after what it replaces, and what makes
    /// two copies of ONE original different is which slot wears them.
    ///
    /// Empty for slot zero, so a single-material mod's assets keep the names they have always had.
    /// </param>
    /// <param name="textureAlready">
    /// ONE COPY PER (SOURCE TEXTURE, AUTHOR IMAGE) FOR THE WHOLE BUILD, shared across materials.
    ///
    /// The material-level cache above this only shares when the WHOLE image set matches, which
    /// misses the commonest case: two slots with their own diffuse and mask that use one normal
    /// map and one tint between them -- and the normal and the tint are the 25 MB ones. Reported
    /// 2026-09-12 on a 1.34 GB mod built exactly that way.
    ///
    /// Safe for the same reason: the bytes this produces are the SOURCE texture with the AUTHOR's
    /// image spliced into it, so identical inputs give identical output and the only thing being
    /// shared is a name. Anything that would come out different is a different key.
    ///
    /// It also skips the `_Mat2` infix for the second user, which is correct rather than lucky:
    /// that suffix exists to stop two DIFFERENT copies landing on one package path, and here
    /// there is only one copy.
    /// </param>
    public static Built? Repaint(IFileProvider provider, Chain chain,
                                 IReadOnlyDictionary<string, string> images,
                                 string modFolder, string projectName,
                                 Func<string, string> packageNameOf,
                                 out string? why, Action<string>? log = null,
                                 string leafSuffix = "",
                                 IDictionary<string, string>? textureAlready = null)
    {
        why = null;
        var warnings = new List<string>();
        var chunks = new List<IoStoreWriter.Chunk>();
        var store = new Dictionary<ulong, ContainerHeader.StoreEntry>();
        var assets = new List<Made>();
        var redirects = new List<AssetCopy.Redirect>();
        var sharedTextures = new List<(string Template, string Package)>();

        foreach (var (key, imagePath) in images)
        {
            if (Match(chain, key) is not { } skin)
            {
                // AMBIGUOUS and ABSENT are different faults with opposite fixes -- one wants a
                // narrower key, the other a different one -- and Match collapses both to null.
                var hits = chain.Skins.Where(x =>
                    x.Parameters.Any(pp => pp.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
                    x.TextureName.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    x.Suffix.Equals(key.TrimStart('_'), StringComparison.OrdinalIgnoreCase)).ToList();
                warnings.Add(hits.Count > 1
                    ? $"'{key}' is ambiguous on {chain.MaterialName} -- it matches " +
                      $"{string.Join(" and ", hits.Select(x => x.TextureName))}; name one of its " +
                      $"parameters instead ({string.Join(", ", hits.SelectMany(x => x.Parameters))})"
                    : $"'{key}' matches no texture on {chain.MaterialName} " +
                      $"(it wears {string.Join(", ", chain.Skins.Select(x => x.TextureName))})");
                continue;
            }
            if (!File.Exists(imagePath))
            { warnings.Add($"{Path.GetFileName(imagePath)} for '{key}' is not on disk"); continue; }

            // AUTHORED ALREADY, FROM THE SAME SOURCE AND THE SAME IMAGE? Then point at that copy.
            // Checked before the compare note and before the encode: re-authoring a 4096 virtual
            // texture is the expensive half of a build as well as the large half of a container.
            var texKey = skin.TexturePackage + "|" + imagePath;
            if (textureAlready is not null && textureAlready.TryGetValue(texKey, out var sharedTex))
            {
                redirects.Add(new AssetCopy.Redirect(skin.TexturePackage, sharedTex));
                sharedTextures.Add((skin.TexturePackage, sharedTex));
                log?.Invoke($"      {skin.TextureName} shares " +
                            $"{sharedTex[(sharedTex.LastIndexOf('/') + 1)..]}, already authored " +
                            $"from {Path.GetFileName(imagePath)}");
                continue;
            }

            // HOW THIS MASK COMPARES WITH THE ONE IT REPLACES, before either is written.
            //
            // Only masks, and only when the two are far enough apart to be worth a sentence -- see
            // TextureCompare, which exists because a helmet shipped glossy for a release with the
            // whole answer sitting in a channel average nobody had measured.
            //
            // NOT the tint mask, which is a mask by name and a different kind of thing: it marks
            // WHERE the palette lands, so large black areas are what it is for and differing from
            // the game's is the entire point of authoring one. Left in, it fired on every tinted
            // part in the author's own mods and said nothing anyone could act on. Identified by
            // the PARAMETER, the same way the tint redirect below finds it, rather than by a name
            // ending in _Tint that something else might be bound to.
            if (!skin.Parameters.Any(pp => pp.Contains("Tint Mask", StringComparison.OrdinalIgnoreCase)) &&
                TextureCompare.Note(provider, skin.TexturePackage, imagePath) is { } maskNote)
                warnings.Add(maskNote);

            var source = AssetCopy.Read(provider, skin.TexturePackage, out var copyWhy);
            if (source is null) { warnings.Add($"{skin.TextureName}: {copyWhy}"); continue; }

            // NAMED AFTER THE TEXTURE IT REPLACES, not after the author's file.
            //
            // It used to take the author's file name, so a mod's textures would be recognisable in
            // a browser rather than being four more copies of the game's names. That reasoning is
            // fine and the consequence was not: a weapon is FIVE parts, an author who paints two
            // of them from one file gets two copies at one package path, and the second silently
            // replaces the first. It cost the DLT-20A its 4096 body texture, which was overwritten
            // by a 512 copy authored from the cell's template -- same file, same name, different
            // template, no error anywhere.
            //
            // The replaced texture's own name cannot collide, because it is what makes the two
            // copies different in the first place: one part's diffuse and another's are different
            // packages in the game. It also matches what the MATERIAL copy below has always done,
            // and these sit in the mod's own folder, so "the mod's copy of the IQA-11's scope
            // diffuse" is exactly what the name now says.
            var leaf = skin.TextureName;
            if (leaf.Length == 0) leaf = Path.GetFileNameWithoutExtension(imagePath);

            // BEFORE THE TYPE SUFFIX, not after it. `T_Clo001_TORS_D` names a DIFFUSE by ending in
            // `_D`, and everything that has to work out what a texture IS reads that ending --
            // this tool's own viewport binds by it, and so does every other tool an author will
            // open the mod in. Appending the slot after it produced `T_Clo001_TORS_D_Mat2`, which
            // ends in nothing recognisable: the pauldron rendered flat grey in the Studio with
            // "unmatched params" in the log, while the same texture bound perfectly in game
            // because the game reads the material's PARAMETER and never looks at the name.
            //
            // So it goes in the middle -- `T_Clo001_TORS_Mat2_D` -- which is unique per slot AND
            // still says what it is.
            leaf = Infix(leaf, leafSuffix, skin.Suffix);
            var package = packageNameOf(leaf);
            var containerPath = $"{projectName}/{modFolder}{leaf}.uasset";

            List<IoStoreWriter.Chunk>? made;
            if (skin.IsVirtual)
            {
                var template = VirtualTexture.Describe(provider, skin.TexturePackage, out var vtWhy);
                if (template is null) { warnings.Add($"{skin.TextureName}: {vtWhy}"); continue; }

                if (Note(imagePath, template.Width, template.Height) is { } note)
                    warnings.Add($"{Path.GetFileName(imagePath)} {note}");

                var pages = VirtualTexture.Author(template, imagePath, null, out vtWhy, log);
                if (pages is null) { warnings.Add($"{skin.TextureName}: {vtWhy}"); continue; }

                made = VirtualTexture.Swap(source, template, pages, package, containerPath, out vtWhy, log);
                if (made is null) { warnings.Add($"{skin.TextureName}: {vtWhy}"); continue; }
            }
            else
            {
                var template = TextureImport.Describe(provider, skin.TexturePackage, out var tWhy);
                if (template is null) { warnings.Add($"{skin.TextureName}: {tWhy}"); continue; }

                if (Note(imagePath, template.Width, template.Height) is { } note)
                    warnings.Add($"{Path.GetFileName(imagePath)} {note}");

                var authored = TextureImport.Author(template, imagePath, null, out tWhy, log);
                if (authored is null) { warnings.Add($"{skin.TextureName}: {tWhy}"); continue; }

                made = TextureImport.Swap(source, template, authored, package, containerPath, out tWhy, log);
                if (made is null) { warnings.Add($"{skin.TextureName}: {tWhy}"); continue; }
            }

            chunks.AddRange(made);
            var id = PackageId.Of(package);
            store[id] = AssetCopy.Store(source.Store, null);
            assets.Add(new Made(leaf, package, id, containerPath, source.Bytes,
                                skin.TexturePackage, IsTexture: true));
            redirects.Add(new AssetCopy.Redirect(skin.TexturePackage, package));
            if (textureAlready is not null) textureAlready[texKey] = package;

            log?.Invoke($"      {skin.TextureName} -> {leaf} " +
                        $"from {Path.GetFileName(imagePath)} " +
                        $"({string.Join(", ", skin.Parameters)})");
        }

        if (redirects.Count == 0)
        {
            // WITH THE REASONS. Every per-texture failure was collected into `warnings`, and
            // `warnings` is returned only on the SUCCESS path -- so the one case where a person
            // needs to know why each key failed was the one case that said nothing but "nothing to
            // repaint". A key matching two slots and a key matching none produce the same silence,
            // and they need opposite fixes.
            why = "no texture was replaced, so there is nothing to repaint" +
                  (warnings.Count > 0 ? " -- " + string.Join("; ", warnings) : "");
            return null;
        }

        // The material instance, pointed at the copies. Its store entry has to move with it: the
        // textures are HARD imports here, unlike the mesh, so the list the loader walks before
        // opening the material still names the game's originals until it is rewritten.
        var matSource = AssetCopy.Read(provider, chain.MaterialPackage, out var matWhy);
        if (matSource is null) { why = $"{chain.MaterialName}: {matWhy}"; return null; }

        var matLeaf = chain.MaterialName + leafSuffix;
        var matPackage = packageNameOf(matLeaf);
        var matContainerPath = $"{projectName}/{modFolder}{matLeaf}.uasset";

        chunks.AddRange(AssetCopy.Rename(matSource, matPackage, matContainerPath, redirects, log));
        var matId = PackageId.Of(matPackage);
        store[matId] = AssetCopy.Store(matSource.Store, redirects);
        assets.Add(new Made(matLeaf, matPackage, matId, matContainerPath, matSource.Bytes,
                            chain.MaterialPackage, IsTexture: false, Redirected: redirects.Count));

        log?.Invoke($"      {chain.MaterialName} -> {matLeaf} " +
                    $"({redirects.Count} texture(s) redirected, " +
                    $"{store[matId].ImportedPackages.Count} import(s))");

        return new Built(chunks, store, assets, matPackage, warnings, sharedTextures);
    }

    /// <summary>
    /// Put <paramref name="suffix"/> into <paramref name="name"/> ahead of its type suffix.
    ///
    /// `T_Clo001_TORS_D` + `_Mat2` + type `D` gives `T_Clo001_TORS_Mat2_D`. A name that does not
    /// end in its type after all just takes the suffix on the end, which is the old behaviour and
    /// is still unique -- being unique is the part that cannot be given up.
    /// </summary>
    private static string Infix(string name, string suffix, string type)
    {
        if (suffix.Length == 0) return name;
        var tail = "_" + type;
        return type.Length > 0 && name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
            ? name[..^tail.Length] + suffix + tail
            : name + suffix;
    }

    /// <summary>
    /// Say when an image will be stretched, and by how much, without refusing it.
    ///
    /// The template's resolution is fixed -- the page tables are the template's -- so an image of
    /// another size is resized to fit. That is usually fine and occasionally the whole problem, so
    /// it is said out loud rather than left for someone to notice in game.
    /// </summary>
    private static string? Note(string imagePath, int width, int height)
    {
        try
        {
            var info = SixLabors.ImageSharp.Image.Identify(imagePath);
            if (info.Width == width && info.Height == height) return null;
            var up = info.Width < width || info.Height < height;
            return $"is {info.Width}x{info.Height} and the slot is {width}x{height} -- " +
                   (up ? "it will be scaled UP, which cannot add detail" : "it will be scaled down");
        }
        catch { return null; }
    }
}

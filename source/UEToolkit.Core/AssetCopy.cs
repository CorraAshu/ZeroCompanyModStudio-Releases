using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;

namespace UEToolkit.Core;

/// <summary>
/// Everything the game files under one package, so a mod can carry its own copy.
///
/// Authoring so far has only ever REDIRECTED references: a part is a renamed copy of another
/// part, and its mesh and icon still point at assets the game ships. That is enough to combine
/// what already exists and no help at all to somebody who wants to repaint a texture or replace a
/// mesh, because there is nothing of theirs to edit.
///
/// Copying one is not the same as copying a file. A package is a set of CHUNKS sharing a package
/// id -- the `.uasset` data, and separately the bulk payloads that hold anything large enough to
/// stream. A character texture in this game is 4096x4096 and its `.uasset` is 1,328 bytes: the
/// pixels are entirely in other chunks, so copying the one that looks like the asset produces a
/// texture with no image in it, and nothing about that failure says so.
///
/// A chunk id is `packageId(8) | index(2, byte-swapped) | 0 | type(1)`, so "everything filed under
/// this package" is a prefix match, and re-filing it under a new id is the same match with the
/// first eight bytes replaced.
/// </summary>
public static class AssetCopy
{
    /// <summary>One chunk of a package: what it is, and its bytes.</summary>
    public sealed record Piece(ushort Index, byte Type, byte[] Data)
    {
        /// <summary>
        /// Type 1 -- ExportBundleData -- is the package itself. Everything else is a payload it
        /// points into, copied verbatim: those bytes mean nothing without the package, and the
        /// package addresses them by chunk rather than by name.
        ///
        /// Not type 0. In EIoChunkType5 zero is `Invalid`, so a probe written against it found
        /// every payload of a texture and declared the package missing. ModBuild has always
        /// written 1 for the packages it authors; this is the same number read back.
        /// </summary>
        public const byte ExportBundleData = 1;
        public bool IsPackage => Type == ExportBundleData;

        public string Describe => $"type {Type}, index {Index}, {Data.Length:N0} bytes";
    }

    public sealed record Source(string PackageName, ulong PackageId, string Container,
                                List<Piece> Pieces, ContainerHeader.StoreEntry? Store)
    {
        public int Bytes => Pieces.Sum(p => p.Data.Length);
        public bool HasPayload => Pieces.Any(p => !p.IsPackage);
    }

    /// <summary>
    /// CUE4Parse's own header for whichever container holds this package.
    ///
    /// Distinct from <see cref="HeaderOf"/>, which returns THIS project's parsed header for
    /// writing containers. This one is for handing back to CUE4Parse when a package is parsed out
    /// of loose bytes: without it <c>IoPackage</c> cannot look up its store entry, so it resolves
    /// no imported packages, so an export whose class lives in another package gets no class and
    /// therefore no properties AT ALL. It does not fail -- it returns a bare UObject with an empty
    /// property list, which reads downstream as "this asset has nothing in it".
    /// </summary>
    public static CUE4Parse.UE4.IO.Objects.FIoContainerHeader? CueHeaderFor(IFileProvider provider, string packageName,
                                                   bool pristine)
    {
        if (provider is not IVfsFileProvider vfs) return null;
        var wanted = PackageId.Of(packageName);

        foreach (var reader in vfs.MountedVfs.OfType<IoStoreReader>())
        {
            if (pristine &&
                Path.GetFileNameWithoutExtension(reader.Name)
                    .EndsWith("_P", StringComparison.OrdinalIgnoreCase)) continue;
            if (!reader.TocResource.ChunkIds.Any(c => c.ChunkId == wanted)) continue;
            // Under the gate: the header is read LAZILY, so touching this property decompresses
            // a chunk out of the same reader the viewport may be loading a package from.
            try { return AssetLoader.Read(() => reader.ContainerHeader); }
            catch (Exception) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Read every chunk of <paramref name="packageName"/> from whichever mounted container holds
    /// it. Returns null with a reason rather than a partial answer -- a package copied without its
    /// payloads is the failure this class exists to prevent.
    /// </summary>
    public static Source? Read(IFileProvider provider, string packageName, out string? why)
        => Read(provider, packageName, pristine: false, out why);

    /// <summary>
    /// The same read, but never from a patch container.
    ///
    /// <see cref="Read"/> takes the FIRST mounted container holding the id, which for a new part
    /// is latent -- nothing else claims that id -- and for an OVERRIDE compounds every time it
    /// runs: the override ships the game's own package id, so once installed it is a candidate to
    /// be its own template, and each rebuild would stack another edit on the last.
    ///
    /// Skipping `_P` is the same rule <see cref="ModBuild.PristineRegistry"/> follows, and this
    /// returns null for the same reason it does -- when only patched copies exist, guessing is how
    /// the stacking happens.
    /// </summary>
    public static Source? PristineRead(IFileProvider provider, string packageName, out string? why)
        => Read(provider, packageName, pristine: true, out why);

    /// <summary>
    /// Read a package's EXPORTS past any installed mod: the game's own copy, whatever is deployed.
    ///
    /// <c>provider.LoadPackage</c> is not that. The Studio mounts the game's whole folder, so it
    /// mounts every mod already installed -- this tool's own output included -- and a `_P` patch
    /// container wins the lookup. An authoring path that reads a game asset through the plain
    /// provider is therefore reading its own last build as if it were the game.
    ///
    /// That is not hypothetical and it is not only about paths. MEASURED 2026-09-08 on the
    /// author's own machine: the stance-set recipe asks the rifle archetype which set belongs to
    /// Rig.BX; his installed mod OVERRIDES that archetype and renames the answer to its own copy;
    /// so the recipe was told to start from `SAS_BXDroidCommando_2HRifle`, a package that exists
    /// only inside the mod under a different path, could not read it, and gave up. Rebuilding the
    /// weapon silently produced no stance set at all -- for the one person guaranteed to have the
    /// mod installed, which is the person testing it.
    ///
    /// Same rule and same reason as <see cref="PristineFiles"/> and <see cref="PristineRead"/>.
    /// Returns an empty list rather than throwing: a caller deciding what to copy wants "nothing
    /// found" as an answer it can report, not an exception through the middle of an edit.
    /// </summary>
    public static List<CUE4Parse.UE4.Assets.Exports.UObject> PristineExports(
        IFileProvider provider, string packageName)
    {
        try
        {
            if (PristineRead(provider, packageName, out _) is not { } src) return [];
            if (src.Pieces.FirstOrDefault(x => x.IsPackage)?.Data is not { } raw) return [];
            return AssetLoader.Read(() =>
            {
                var pkg = new CUE4Parse.UE4.Assets.IoPackage(
                    new CUE4Parse.UE4.Readers.FByteArchive(packageName, raw, provider.Versions),
                    null, (CUE4Parse.UE4.Readers.FArchive?) null, null, provider as IVfsFileProvider);
                return pkg.ExportsLazy.Select(x => x.Value).ToList();
            });
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// Every file the PRISTINE game ships -- the containers no mod has patched.
    ///
    /// <c>provider.Files</c> is not that list. The Studio mounts the game's whole folder, so it
    /// also mounts every mod already installed -- this tool's own output included. Anything that
    /// looks a name up there can find a mod's copy instead of the game's, and an authoring path
    /// that does is writing a project that refers to its own last build: the second build then
    /// cannot find the "template", and the asset is silently dropped.
    ///
    /// That is not hypothetical. A duplicated weapon's mesh entry came back naming
    /// <c>/Game/Game/Customizations/Mods/&lt;Mod&gt;/SK_...</c> as the mesh to COPY, because the
    /// mod was installed when the weapon was duplicated. It built, said "cannot carry its own
    /// mesh", and shipped a weapon with no model of its own.
    ///
    /// Same `_P` rule as <see cref="PristineRead"/> and <see cref="ModBuild.PristineRegistry"/>.
    /// </summary>
    public static IEnumerable<string> PristineFiles(IFileProvider provider)
    {
        if (provider is not IVfsFileProvider vfs) yield break;
        foreach (var reader in vfs.MountedVfs)
        {
            if (Path.GetFileNameWithoutExtension(reader.Name)
                    .EndsWith("_P", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var path in reader.Files.Keys) yield return path;
        }
    }

    /// <summary>
    /// Every asset NAME the pristine game ships -- leaf names, no path, no extension.
    ///
    /// THE ONE QUESTION A NEW ID HAS TO ANSWER: is this name already the game's? A
    /// `PrimaryAssetId` is (Type, Name) and the package PATH is not part of it, so a mod asset
    /// called `CPD_H_Name_BaileyBela` and the game's own are ONE id claimed twice, and the asset
    /// manager keeps one. The mod installs, mounts, registers and verifies perfectly, and in game
    /// the part is simply never found. Measured 2026-09-06; see
    /// [[zero-company-weapon-model-swap]].
    ///
    /// Cached per provider, because the answer costs a pass over 180,000 paths and the game does
    /// not change while we run. Weakly held, so a provider that goes away takes its set with it.
    /// </summary>
    public static HashSet<string> GameAssetNames(IFileProvider provider)
    {
        if (_gameNames.TryGetValue(provider, out var have)) return have;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in PristineFiles(provider))
            if (key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                names.Add(Path.GetFileNameWithoutExtension(key));

        _gameNames.AddOrUpdate(provider, names);
        return names;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IFileProvider, HashSet<string>>
        _gameNames = new();

    /// <summary>
    /// Every file that came from an INSTALLED MOD, with the mod it came from -- the inverse of
    /// <see cref="PristineFiles"/>, and told apart the same way: a mod ships a `_P` container.
    ///
    /// Needed because "does this name already exist" has two different answers with two different
    /// remedies. A name the GAME uses means rename yours; a name ANOTHER MOD uses means one of you
    /// will be ignored, and either of you can move. Reporting the second as the first sends an
    /// author looking through the game for an asset that is not there.
    /// </summary>
    public static IEnumerable<(string Mod, string Path)> ModFiles(IFileProvider provider)
    {
        if (provider is not IVfsFileProvider vfs) yield break;
        foreach (var reader in vfs.MountedVfs)
        {
            var name = Path.GetFileNameWithoutExtension(reader.Name);
            if (!name.EndsWith("_P", StringComparison.OrdinalIgnoreCase)) continue;
            var mod = name[..^2];
            foreach (var path in reader.Files.Keys) yield return (mod, path);
        }
    }

    /// <summary>
    /// The pristine game's package whose asset name is exactly <paramref name="leaf"/>, or null.
    ///
    /// Case-insensitive, and the first match wins -- two of one name in the shipped game would be
    /// a question about the game rather than about this.
    /// </summary>
    public static string? PristinePackageOfLeaf(IFileProvider provider, string leaf, string project)
    {
        foreach (var path in PristineFiles(provider))
        {
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = path.LastIndexOf('/');
            if (slash < 0) continue;
            if (!string.Equals(path[(slash + 1)..^".uasset".Length], leaf,
                               StringComparison.OrdinalIgnoreCase)) continue;
            if (PackageId.PackageNameFor(path, project) is { } package) return package;
        }
        return null;
    }

    private static Source? Read(IFileProvider provider, string packageName, bool pristine, out string? why)
    {
        why = null;
        if (provider is not IVfsFileProvider vfs)
        { why = "this provider does not expose its containers"; return null; }

        var wanted = PackageId.Of(packageName);
        var skipped = 0;

        foreach (var reader in vfs.MountedVfs.OfType<IoStoreReader>())
        {
            // `_P` is UE's patch marker and is what gives a mod container its priority, so a
            // container carrying it is by definition not the pristine game.
            if (pristine &&
                Path.GetFileNameWithoutExtension(reader.Name)
                    .EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TocResource.ChunkIds.Any(c => c.ChunkId == wanted)) skipped++;
                continue;
            }

            var pieces = new List<Piece>();
            foreach (var chunk in reader.TocResource.ChunkIds)
            {
                if (chunk.ChunkId != wanted) continue;
                try
                {
                    pieces.Add(new Piece(LogicalIndex(chunk), chunk.ChunkType,
                                         AssetLoader.Read(() => reader.Read(chunk))));
                }
                catch (Exception e)
                { why = $"{packageName}: chunk type {chunk.ChunkType} would not read - {e.Message}"; return null; }
            }
            if (pieces.Count == 0) continue;

            // The package chunk first, so a caller that renames "the package" does not have to
            // hunt for it among the payloads.
            pieces.Sort((a, b) => a.IsPackage == b.IsPackage
                ? a.Index.CompareTo(b.Index)
                : a.IsPackage ? -1 : 1);

            if (!pieces[0].IsPackage)
            { why = $"{packageName} has payload chunks but no package chunk"; return null; }

            ContainerHeader.StoreEntry? store = null;
            try
            {
                var hdr = HeaderOf(reader);
                var at = hdr?.PackageIds.IndexOf(wanted) ?? -1;
                if (at >= 0 && hdr is not null && at < hdr.Store.Count) store = hdr.Store[at];
            }
            catch (Exception) { /* an entry we cannot read is rebuilt by the caller */ }

            return new Source(packageName, wanted, reader.Name, pieces, store);
        }

        // Saying WHICH kind of nothing this is matters: "only a patch container has it" means the
        // mod is already installed and the pristine copy is not mounted, which is a different
        // problem from a name that does not exist and has a different fix.
        why = skipped > 0
            ? $"{packageName} is only in {skipped} patch container(s) - the pristine copy is not mounted"
            : $"{packageName} is not in any mounted container";
        return null;
    }

    /// <summary>
    /// A chunk's index as the WRITER wants it.
    ///
    /// UE stores this field byte-swapped -- network order inside an otherwise little-endian
    /// struct -- and CUE4Parse keeps the raw form in `_chunkIndex`. IoStoreWriter takes the
    /// logical value and swaps it back on the way out, so handing it the raw one would swap it
    /// twice and file every payload under an index nothing looks for.
    /// </summary>
    private static ushort LogicalIndex(CUE4Parse.UE4.IO.Objects.FIoChunkId chunk) =>
        (ushort) (((chunk._chunkIndex & 0xFF) << 8) | ((chunk._chunkIndex & 0xFF00) >> 8));

    /// <summary>The container's package store, or null when it carries none.</summary>
    private static ContainerHeader? HeaderOf(IoStoreReader reader)
    {
        var want = ContainerHeader.ChunkIdFor(reader.TocResource.Header.ContainerId.Id);
        var chunk = reader.TocResource.ChunkIds.FirstOrDefault(
            c => ContainerHeader.ChunkIdFor(c.ChunkId).AsSpan().SequenceEqual(want) &&
                 c.ChunkType == ContainerHeader.ChunkType);
        return chunk.ChunkId == 0 && chunk.ChunkType == 0
            ? null
            : ContainerHeader.Read(AssetLoader.Read(() => reader.Read(chunk)));
    }

    /// <summary>
    /// Re-file a package under a new name: the package chunk is renamed, every payload is carried
    /// over untouched, and all of them move to the new package id.
    ///
    /// The payloads are NOT rewritten, and that is correct rather than lazy -- they hold pixels
    /// and vertices, which say nothing about what the package is called. Only the package chunk
    /// names anything.
    /// </summary>
    public static List<IoStoreWriter.Chunk> Rename(Source source, string newPackageName,
                                                   string containerPath, Action<string>? log = null)
        => Rename(source, newPackageName, containerPath, null, log);

    /// <summary>
    /// One reference a copied package makes, pointed somewhere else.
    ///
    /// Both halves move. A package reference is stored as the path AND the bare asset name, and
    /// substituting only the path leaves the old name behind pointing at nothing -- the same trap
    /// the mesh and icon redirects already document, arrived at from a third direction.
    /// </summary>
    /// <param name="KeepLeaf">
    /// Move the package path only, and leave the bare asset NAME alone. For a mesh pointed at a
    /// DIFFERENT game material the leaf is not a reference to anything: the import is by id and by
    /// hash. It is, however, a name-map entry that a slot may share -- MI_Eyebrows is both the
    /// brow's slot name and its material's leaf -- and renaming the entry renames the slot, which a
    /// customization fragment aims a tint at by name.
    /// </param>
    public sealed record Redirect(string FromPackage, string ToPackage, bool KeepLeaf = false)
    {
        public ulong FromId => PackageId.Of(FromPackage);
        public ulong ToId => PackageId.Of(ToPackage);
    }

    /// <summary>
    /// The same copy, with the references it makes pointed at the mod's own assets.
    ///
    /// This is the whole chain a repainted character travels -- part to mesh, mesh to material,
    /// material to textures -- and every link is this one operation. Nothing here knows what a
    /// material is; it knows that a package names other packages and that a copy which keeps the
    /// originals' names is a copy that keeps using the originals' assets.
    /// </summary>
    public static List<IoStoreWriter.Chunk> Rename(Source source, string newPackageName,
                                                   string containerPath,
                                                   IReadOnlyList<Redirect>? redirects,
                                                   Action<string>? log = null)
    {
        var newId = PackageId.Of(newPackageName);
        var chunks = new List<IoStoreWriter.Chunk>();

        foreach (var piece in source.Pieces)
        {
            if (piece.IsPackage)
            {
                var subs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [source.PackageName] = newPackageName,
                    [Leaf(source.PackageName)] = Leaf(newPackageName),
                };

                foreach (var r in redirects ?? [])
                {
                    if (r.FromPackage == r.ToPackage) continue;
                    subs[r.FromPackage] = r.ToPackage;
                    // Only when the leaves actually differ. Mapping a name to itself is harmless
                    // but mapping a SHARED leaf twice is not: two redirects whose assets have the
                    // same short name would fight over one entry.
                    var from = Leaf(r.FromPackage);
                    var to = Leaf(r.ToPackage);
                    if (!r.KeepLeaf && from != to && !subs.ContainsKey(from)) subs[from] = to;
                }

                var pkg = ZenPackage.Read(piece.Data);
                pkg.RenameNames(subs);
                chunks.Add(new IoStoreWriter.Chunk(newId, piece.Index, piece.Type,
                                                   pkg.Write(), containerPath));
                continue;
            }

            // A PAYLOAD IS A FILE, and it needs its name.
            //
            // This used to pass null here, on the confident reasoning that a payload is addressed
            // by chunk id and that giving it a filename would add an entry for something that is
            // not a file. The game's own containers say otherwise: beside every texture's 1,231
            // byte `.uasset` sits a 6,436,620 byte `.ubulk`, listed in the directory index, and
            // that is how the loader finds it.
            //
            // Nothing caught it for as long as the only assets a mod carried were meshes, parts
            // and icons -- none of which HAS a payload. Virtual textures were the first, and they
            // arrived in the container with their pages present, their headers perfect, their
            // sizes right, and no way for anything to reach them. The texture fell back to its
            // fallback colour, whose alpha is zero, and the helmet rendered see-through.
            chunks.Add(new IoStoreWriter.Chunk(newId, piece.Index, piece.Type, piece.Data,
                                               PayloadPath(containerPath, piece.Type)));
        }

        var moved = (redirects ?? []).Count(r => r.FromPackage != r.ToPackage);
        log?.Invoke($"      copied {source.PackageName} -> {newPackageName} " +
                    $"({chunks.Count} chunk(s), {source.Bytes:N0} bytes" +
                    (moved > 0 ? $", {moved} reference(s) redirected)" : ")"));
        return chunks;
    }

    /// <summary>
    /// The copy's DECLARED imports, with the redirected ones swapped.
    ///
    /// A package names what it imports twice over: inside itself, and in the container's package
    /// store, which is the list the loader actually walks before opening anything. Redirect one
    /// and not the other and the copy declares a dependency it no longer uses while using one it
    /// never declared -- which loads, and then finds the reference unresolved.
    /// </summary>
    public static ContainerHeader.StoreEntry Store(ContainerHeader.StoreEntry? entry,
                                                   IReadOnlyList<Redirect>? redirects)
    {
        if (entry is null) return new ContainerHeader.StoreEntry(new List<ulong>(), new List<byte[]>());
        if (redirects is null || redirects.Count == 0) return entry;

        var map = new Dictionary<ulong, ulong>();
        foreach (var r in redirects) if (r.FromId != r.ToId) map[r.FromId] = r.ToId;

        return new ContainerHeader.StoreEntry(
            entry.ImportedPackages.Select(id => map.TryGetValue(id, out var to) ? to : id).ToList(),
            entry.ShaderMapHashes);
    }

    /// <summary>
    /// What a payload chunk is called beside its package.
    ///
    /// The extension is the chunk's TYPE, and the reader picks which one to look for from the
    /// bulk data's flags -- ordinary payloads in `.ubulk`, optional ones in `.uptnl`, memory
    /// mapped ones in `.m.ubulk`. A payload filed under the wrong extension is exactly as
    /// unreachable as one filed under no name at all, so an unrecognised type gets none rather
    /// than a guess.
    /// </summary>
    private static string? PayloadPath(string containerPath, byte type)
    {
        var stem = containerPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
            ? containerPath[..^".uasset".Length]
            : containerPath;

        return type switch
        {
            2 => stem + ".ubulk",       // BulkData
            3 => stem + ".uptnl",       // OptionalBulkData
            4 => stem + ".m.ubulk",     // MemoryMappedBulkData
            _ => null,
        };
    }

    private static string Leaf(string packageName) =>
        packageName[(packageName.LastIndexOf('/') + 1)..];
}

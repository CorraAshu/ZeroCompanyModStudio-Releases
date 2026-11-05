using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;

namespace UEToolkit.Core;

public sealed record ExportProgress(int Done, int Total, string Current);

public sealed record BatchResult(int Exported, int Failed, IReadOnlyList<string> Errors)
{
    public override string ToString() =>
        $"{Exported} exported" + (Failed > 0 ? $", {Failed} failed" : "");
}

/// <summary>
/// Batch export to disk. Keeps the game's folder structure so a character's parts,
/// textures and animations land together and stay traceable to their source asset.
/// </summary>
public static class BatchExporter
{
    /// <summary>Reports meshes whose UV grid could not be resolved, so it is not silent.</summary>
    public static Action<string>? UvSkipped;

    /// <summary>Every written mesh, re-read off disk. See <see cref="PskValidate"/> for why.</summary>
    public static Action<PskValidate.Report>? Validated;

    /// <summary>Pools each mesh's UV extents per material so a doubled atlas grid is visible.</summary>
    public static PskValidate.Atlas? Atlas;

    /// <summary>Every mesh of a character variant, plus its materials and textures.</summary>
    public static BatchResult ExportCharacter(
        IFileProvider provider, CharacterVariant variant, ExportRequest request,
        Action<ExportProgress>? progress = null)
    {
        // A region can bind several meshes, so flatten rather than taking one per part.
        var paths = variant.Parts.SelectMany(p => p.Meshes).Select(m => m.AssetPath).Distinct().ToList();

        // The variant's material overrides are part of what the variant IS, not decoration.
        // One body mesh can serve every rank with the rank as the override, so meshes
        // alone put a high-rank character into Max wearing a low rank's textures. The mesh files
        // reference their own materials by slot and cannot be made to point elsewhere from
        // here, so the overrides are exported alongside and a manifest says which slot each
        // belongs to -- enough to reassign them in Max, which is a manual step regardless.
        var swaps = variant.Parts
            .SelectMany(p => p.Meshes)
            .SelectMany(m => m.MaterialOverrides
                .Where(o => o.MaterialPath is { Length: > 0 })
                .Select(o => (Mesh: m.DisplayName, o.SlotName, Path: o.MaterialPath!)))
            .ToList();

        var all = paths.Concat(swaps.Select(s => s.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = ExportPaths(provider, all, request, progress);

        // Where each part belongs on the rig. Written after the export, because it measures the
        // files that were just written rather than the assets they came from.
        try { WritePlacement(variant, request.OutputDirectory); }
        catch (Exception e)
        {
            result = result with { Errors = result.Errors.Append($"placement script: {e.Message}").ToList() };
        }

        if (swaps.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(request.OutputDirectory);
                File.WriteAllLines(
                    Path.Combine(request.OutputDirectory, $"{variant.Name}-material-overrides.txt"),
                    new[]
                    {
                        $"# {variant.Name}: material slots this variant overrides.",
                        "# Without these the meshes carry their default materials, which is",
                        "# another variant's look -- ranks share geometry and differ by material.",
                        "",
                    }.Concat(swaps
                        .OrderBy(s => s.Mesh, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(s => s.SlotName, StringComparer.OrdinalIgnoreCase)
                        .Select(s => $"{s.Mesh}\tslot {s.SlotName}\t-> {s.Path}")));
            }
            catch (Exception e)
            {
                return result with { Errors = result.Errors.Append($"material manifest: {e.Message}").ToList() };
            }
        }
        return result;
    }

    /// <summary>
    /// Emit the MaxScript that assembles this variant's exported parts.
    ///
    /// The host rig is taken from the exported part with the MOST bones. A character's parts are
    /// exported separately and each carries its own copy of the skeleton, but a helmet's copy is
    /// as good as the body's only when they agree — and a character can ship two rig generations that
    /// do not. Picking the richest rig avoids resolving bone positions against a reduced one.
    /// </summary>
    public static string? WritePlacement(CharacterVariant variant, string outputDir)
    {
        var parts = variant.Parts
            .SelectMany(p => p.Meshes.Select(m => (Part: p, Mesh: m)))
            .Select(x => (x.Mesh, File: FindExported(outputDir, x.Mesh.AssetPath)))
            .Where(x => x.File is not null)
            .ToList();
        if (parts.Count == 0) return null;

        var host = parts
            .Select(x => (x.File, Bones: PlacementScript.BonePositions(x.File!)))
            .OrderByDescending(x => x.Bones.Count).First();
        if (host.Bones.Count == 0) return null;

        var places = parts
            .Select(x => PlacementScript.For(x.File!, x.Mesh.ParentBoneName, host.Bones))
            .OfType<PlacementScript.Placement>()
            .ToList();
        if (places.Count == 0) return null;

        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, $"{variant.Name}-place.ms");
        File.WriteAllText(path, PlacementScript.Emit(variant.Name, places));

        // The same decisions in machine-readable form, for the FBX merge. Written from the same
        // `places` list, so the MaxScript and the FBX cannot disagree about where a part goes --
        // and it removes any temptation to guess the attach bone by proximity, which does not
        // work: origin-authored parts all sit near (0,0,0), so nearest-bone put a
        // backpack on a knee helper bone and a head on the world root.
        File.WriteAllLines(Path.Combine(outputDir, $"{variant.Name}-attach.tsv"),
            new[] { "mesh\tbone\toriginAuthored" }.Concat(
                places.Select(p => $"{p.MeshName}\t{p.BoneName}\t{(p.OriginAuthored ? "1" : "0")}")));
        return path;
    }

    /// <summary>Where an asset landed, .psk or .pskx, mirroring the game tree.</summary>
    private static string? FindExported(string outputDir, string assetPath)
    {
        var stem = Path.Combine(outputDir, Path.ChangeExtension(assetPath, null) ?? assetPath);
        foreach (var ext in new[] { ".psk", ".pskx" })
            if (File.Exists(stem + ext)) return stem + ext;
        return null;
    }

    /// <summary>Every animation bound to a skeleton.</summary>
    public static BatchResult ExportAnimations(
        IFileProvider provider, IEnumerable<AnimEntry> anims, ExportRequest request,
        Action<ExportProgress>? progress = null)
        => ExportPaths(provider, anims.Select(a => a.Path).ToList(), request, progress);

    /// <param name="cancel">
    /// Stops the run between assets. A whole-game dump is tens of thousands of files and hours of
    /// work, and one that cannot be called off is one somebody has to kill the application to
    /// escape -- which loses the partial result as well as the run.
    /// </param>
    public static BatchResult ExportPaths(
        IFileProvider provider, IReadOnlyList<string> assetPaths, ExportRequest request,
        Action<ExportProgress>? progress = null, CancellationToken cancel = default)
    {
        var errors = new List<string>();
        int ok = 0, fail = 0;
        var grids = new AtlasGrids();

        for (var i = 0; i < assetPaths.Count; i++)
        {
            if (cancel.IsCancellationRequested) break;
            var path = assetPaths[i];
            var name = Path.GetFileNameWithoutExtension(path);
            progress?.Invoke(new ExportProgress(i, assetPaths.Count, name));

            try
            {
                var obj = AssetLoader.LoadPrimary(provider, path);

                if (!IsExportable(obj))
                {
                    fail++;
                    errors.Add($"{name}: not an exportable asset type");
                    continue;
                }

                var result = AssetExporter.Export(obj!, request);
                if (result.Success)
                {
                    ok++;
                    // A UDIM atlas's UVs land outside 0..1, which any DCC
                    // reads as tiling. Bake the atlas remap into the written file.
                    //
                    // The path is DERIVED, not taken from result.SavedPath: the exporter
                    // reports no saved path when ExportMaterials is false, so trusting it
                    // silently skipped the remap for every mesh in a full extraction while
                    // still reporting success. Only per-character exports, which keep
                    // materials on, were remapped -- which is exactly the sort of difference
                    // between "how I test" and "how it runs" that hides for a whole run.
                    if (AfterMeshExport(request, path, result.SavedPath, obj!, grids) is { } uvErr)
                        errors.Add(uvErr);
                }
                else { fail++; errors.Add($"{name}: {result.Error}"); }
            }
            catch (Exception e)
            {
                fail++;
                errors.Add($"{name}: {e.Message}");
            }
        }

        // Now that the whole batch has been written, the atlases have been seen by every mesh
        // that uses them and the grids can be settled. Both export paths end here, deliberately:
        // one of these bugs existed purely because --exportall and per-character export were two
        // separate loops and only one of them was ever exercised.
        FinishBatch(grids, 1, root: request.OutputDirectory);
        progress?.Invoke(new ExportProgress(assetPaths.Count, assetPaths.Count, "done"));
        return new BatchResult(ok, fail, errors);
    }

    /// <summary>Bake the pooled grids into the written files, then check what landed.</summary>
    public static void FinishBatch(AtlasGrids grids, int jobs,
                                   string? bakedLog = null, Action<string>? log = null,
                                   string? root = null)
    {
        if (grids.Files == 0) return;
        var changed = grids.ResolveAndApply(jobs, bakedLog, log, root);
        log?.Invoke($"uv: {changed:N0} of {grids.Files:N0} mesh file(s) remapped");
        ValidateWritten(grids.WrittenFiles, jobs);
        // Once, single-threaded, after every thread has finished writing. See PruneEmptyDirs.
        if (root is not null) PruneEmptyDirs(root);
    }

    /// <summary>
    /// The post-export step every mesh must go through, whichever loop exported it.
    ///
    /// This exists as one shared method because it previously did not: the remap lived inside
    /// ExportPaths, while --exportall ran its own parallel loop calling AssetExporter directly,
    /// so a full extraction of 12,408 meshes wrote every UV unremapped while per-character
    /// exports were correct. Two code paths, one of which was the only one ever tested.
    /// Returns an error string, or null.
    /// </summary>
    public static string? AfterMeshExport(ExportRequest request, string assetPath,
                                          string? savedPath, UObject obj, AtlasGrids grids)
    {
        var name = Path.GetFileNameWithoutExtension(assetPath);
        var file = MeshFileFor(request.OutputDirectory, assetPath, savedPath, request.Target);
        // Nothing to remap is itself worth saying. A missing file here is how bug (2) presented:
        // the export "succeeded", the remap found no path, and the run reported nothing at all.
        if (file is null)
            return obj is USkeletalMesh or UStaticMesh
                ? $"{name}: exported but no {string.Join("/", ExtensionsFor(request.Target))} found" : null;

        // EVERYTHING BELOW READS AND REWRITES A PSK, so a glTF or UEFormat export cannot go
        // through it -- PskUv.ReadSections and PskUv.Apply parse chunk headers, and a .glb is not
        // that. The old code reached this point, failed to find a .psk, and reported
        // "exported but no .psk/.pskx found" -- which reads as "your model did not export" and is
        // the opposite of the truth. It exported; it is simply in a format the remap cannot open.
        //
        // Reported 2026-09-10 by a user dumping 501st parts: the .glb was on disk the whole time,
        // 2.3 MB of it, while the log said the mesh was missing. Neither CLI export verb could
        // reach this -- both hardcoded ActorX -- so the only way in was the Studio's format
        // setting, which REMEMBERS its choice between launches.
        //
        // The UVs really are unremapped, and for an atlas mesh that matters: they stay at
        // U 1.006..1.992 instead of being brought into 0..1, which textures the model wrongly.
        // AtlasTextures reads the UObject rather than the file, so whether this mesh cares can be
        // answered without opening anything.
        if (request.Target != ExportTarget.ActorX)
        {
            try
            {
                var onAtlas = PskUv.AtlasTextures(obj, out _);
                return onAtlas.Count > 0
                    ? $"{name}: exported to {Path.GetExtension(file)}, but its UVs are NOT remapped -- " +
                      $"the atlas remap only reads .psk. This mesh uses {onAtlas.Count} atlas " +
                      "texture(s), so export it as ActorX/PSK if the textures have to line up."
                    : null;
            }
            catch (Exception e) { return $"{name}: atlas lookup {e.Message}"; }
        }

        // EVERY LOD, not just the one the exporter named. TryWriteToDir reports the LAST file it
        // wrote, and that single path was all this method ever relocated, root-fixed or recorded
        // -- so under --lods all, _LOD0 (the file anyone actually imports) was left at the package
        // path with nothing done to it, and only the smallest LOD was processed.
        var files = LodSiblings(file)
            .Select(f => MirrorGameTree(request.OutputDirectory, assetPath, f)).ToList();
        file = files[0];

        // One root, or ActorX cannot open the file at all.
        //
        // CUE4Parse writes a static mesh's SOCKETS as bones with no parent, so a prop with 42
        // sockets ships 42 roots and the import dies on "array index must be positive number,
        // got: 0". It affects 20 of 12,390 meshes here, which is exactly rare enough to have gone
        // unnoticed until someone imported environment art rather than a character -- and it is
        // fixed HERE rather than only in --pskfixroots so a fresh extraction never reproduces it.
        // `NeedsRoot` reads chunk headers only, so the 12,370 meshes that need nothing pay a few
        // small reads rather than a whole file. A full read per mesh here measurably perturbs a
        // loop that still has a timing-sensitive race in it.
        //
        // Nothing below returns early on failure. An earlier version did, and it would have
        // skipped the atlas record for any mesh it could not fix -- losing that mesh's material
        // slots, which is precisely the symptom the export non-determinism already produces and
        // would have been indistinguishable from it.
        string? socketError = null;
        foreach (var f in files)
        {
            try
            {
                if (!PskSockets.NeedsRoot(f)) continue;
                var rooted = PskSockets.AddRoot(f);
                if (rooted.Skipped is { } why) socketError = $"{name}: multi-root skeleton left alone ({why})";
                else if (rooted.Changed)
                    UvSkipped?.Invoke($"{Path.GetFileNameWithoutExtension(f)}: " +
                                      $"{rooted.RootsBefore} socket root(s) given one parent");
            }
            catch (Exception e) { socketError = $"{name}: socket root fix {e.Message}"; }
        }

        // A BIND POSE'S SCALE, divided out, because a PSK cannot carry one. See PskScale: a mesh
        // whose rig is scaled by 100 is correct in game and a hundred times too big in a DCC, and
        // nothing here read Scale3D at all.
        //
        // AFTER the root fix and BEFORE the atlas record, deliberately. AddRoot rewrites the bone
        // chunk, so rescaling first would have its work overwritten; the atlas record reads
        // SECTIONS and UVs, which no amount of scaling touches.
        //
        // A scale of 1 writes nothing -- Normalise returns early and the file is not even opened,
        // so every mesh that was already right stays byte-identical.
        if (obj is USkeletalMesh scaledMesh)
        {
            var by = PskScale.RootScaleOf(scaledMesh, out var scaleNote);
            if (scaleNote is not null) socketError ??= $"{name}: {scaleNote}";
            else if (Math.Abs(by - 1f) > 0.0001f)
                foreach (var f in files)
                {
                    try
                    {
                        if (PskScale.Normalise(f, by, out var scaleWhy, out var pts, out var bns))
                            UvSkipped?.Invoke($"{Path.GetFileNameWithoutExtension(f)}: bind pose scaled " +
                                              $"{by:0.###}x, so {pts:N0} point(s) and {bns:N0} bone(s) " +
                                              "were divided back to the size the game renders");
                        else socketError ??= $"{name}: bind scale {by:0.###}x not applied -- {scaleWhy}";
                    }
                    catch (Exception e) { socketError ??= $"{name}: bind scale {e.Message}"; }
                }
        }

        // Record which atlas each material slot resolved to; the UVs are rewritten later, once
        // every mesh sharing that atlas has been seen. Deciding here, from this one mesh, is
        // what gave two parts of the same character two different grids.
        try
        {
            var mats = PskUv.AtlasTextures(obj, out var bad);
            var secs = PskUv.ReadSections(file);
            foreach (var f in files) grids.Record(f, mats, PskUv.ReadSections(f));
            UvSkipped?.Invoke($"{name}: {secs.Count} section(s), {mats.Count} atlas texture(s)" +
                              (files.Count > 1 ? $", {files.Count} LOD file(s)" : ""));
            if (bad.Total > 0 || bad.Recovered > 0) Unresolvable?.Invoke(name, bad);
        }
        catch (Exception e) { return $"{name}: atlas lookup {e.Message}"; }
        return socketError;
    }

    /// <summary>Reports material slots whose atlas could not be resolved, per mesh.</summary>
    public static Action<string, PskUv.Unresolved>? Unresolvable;

    /// <summary>
    /// Read each written mesh back and check it. Validating the buffer we meant to write would
    /// only confirm our own intent -- what matters is what landed, which is exactly where every
    /// one of these bugs differed from what the code said it was doing. Call after the UVs have
    /// been baked, or it will report the unremapped state it was handed.
    /// </summary>
    public static void ValidateWritten(IEnumerable<string> files, int jobs)
        => Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, f =>
        {
            try
            {
                var report = PskValidate.Check(f);
                Atlas?.Add(report);
                Validated?.Invoke(report);
            }
            catch { /* an unreadable file is already a failed export */ }
        });

    /// <summary>
    /// Put a written mesh where the toolkit's own file tree says it lives.
    ///
    /// CUE4Parse names the output from the UE **package** path, while textures here are written
    /// from the **container file** path. Those are usually the same string, which is why this went
    /// unnoticed across several games: `/Game/Characters/Foo/...` maps to the file path
    /// `<Project>/Content/Characters/Foo/...` exactly.
    ///
    /// They diverge whenever a plugin's mount point is not its folder. A GameFeature plugin
    /// can mount at `/PluginName/`, discarding the on-disk `Plugins/Group/Name/` nesting, so the mesh
    /// landed three folders away from its own textures and read as a failed export -- the user hit
    /// exactly that and reported missing textures that were present all along.
    ///
    /// The file path wins because it is what the tree shows and what every texture already uses.
    /// A mesh whose two paths agree is not touched, so games where they coincide are unaffected.
    /// </summary>
    private static string MirrorGameTree(string outputDir, string assetPath, string written)
    {
        try
        {
            // The DIRECTORY comes from the file path; the FILE NAME comes from what the exporter
            // actually wrote. Rebuilding the name from the package path instead dropped the
            // `_LOD3` suffix, so with --lods all the LOWEST-detail LOD was moved on top of the
            // base name and the real _LOD3 vanished: SK_B2_BattleDroid.psk held 4,412 verts where
            // LOD0 has 48,358. Every mesh in the characters and weapons export shipped that way.
            var stemDir = Path.GetDirectoryName(Path.ChangeExtension(assetPath, null) ?? assetPath) ?? "";
            var want = Path.Combine(outputDir, stemDir, Path.GetFileName(written));
            if (Path.GetFullPath(want).Equals(Path.GetFullPath(written), StringComparison.OrdinalIgnoreCase))
                return written;

            Directory.CreateDirectory(Path.GetDirectoryName(want)!);

            // The transient lock this used to retry by hand -- see FreshFile, which is now the
            // one place that knows the rule, because the READS a few lines after this move were
            // losing the same race and were not retrying at all.
            FreshFile.Move(written, want, overwrite: true);
            return want;
        }
        catch (Exception e)
        {
            // A mesh in the wrong folder is a nuisance; losing it is not -- so keep what was
            // written. But SAY SO: the first version of this swallowed the reason, and 5 of 566
            // meshes silently stayed at their package path with nothing in the log to explain it.
            // That silence is what made a one-line diagnosis take a whole extra export run.
            Relocations?.Invoke($"{Path.GetFileName(written)}: could not move into the game tree - {e.Message}");
            return written;
        }
    }

    /// <summary>
    /// Every LOD file the exporter wrote for one mesh, most detailed first.
    ///
    /// CUE4Parse names them <c>Name_LOD0..Name_LODn</c> and reports only the last one back, so the
    /// caller has to find the rest itself. A path with no <c>_LOD</c> suffix is its own only
    /// sibling, which is the <c>--lods first</c> case and by far the common one.
    /// </summary>
    private static List<string> LodSiblings(string written)
    {
        var dir = Path.GetDirectoryName(written);
        var stem = Path.GetFileNameWithoutExtension(written);
        var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*)_LOD(\d+)$");
        if (dir is null || !m.Success || !Directory.Exists(dir)) return [written];

        var baseName = m.Groups[1].Value;
        var rx = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(baseName) + @"_LOD(\d+)$");
        var found = new List<(int Lod, string Path)>();
        foreach (var f in Directory.EnumerateFiles(dir, baseName + "_LOD*.ps*"))
        {
            var hit = rx.Match(Path.GetFileNameWithoutExtension(f));
            if (hit.Success && int.TryParse(hit.Groups[1].Value, out var n)) found.Add((n, f));
        }
        if (found.Count == 0) return [written];
        return found.OrderBy(x => x.Lod).Select(x => x.Path).ToList();
    }

    /// <summary>Reports meshes that could not be moved into the game tree.</summary>
    public static Action<string>? Relocations;

    /// <summary>
    /// Delete directories the exporter's own naming left behind, once the batch is finished.
    ///
    /// **Not during the export.** Pruning per mesh raced the other export threads: this walks
    /// upward deleting empty directories, and a thread that had just created its destination
    /// could have it removed underneath before its own `File.Move` ran. That is what left 5 of
    /// 566 meshes at their package path -- rare, timing-dependent, and invisible because
    /// the failure was caught and discarded.
    /// </summary>
    private static void PruneEmptyDirs(string outputDir)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(outputDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* one stubborn directory is not worth failing a run over */ }
            }
        }
        catch { /* enumeration failed; leaving empty folders is harmless */ }
    }

    /// <summary>
    /// Where a mesh export actually landed. Prefers what the exporter reported, then falls back
    /// to the path it would have written -- the game tree mirrored under the output directory,
    /// with .psk for skeletal and .pskx for static.
    /// </summary>
    /// <summary>What a mesh file is called, for the format actually being written.</summary>
    /// <remarks>
    /// This used to name only .psk and .pskx whatever the request asked for, so every glTF and
    /// UEFormat export "went missing" -- see the note in <see cref="AfterMeshExport"/>.
    /// </remarks>
    internal static string[] ExtensionsFor(ExportTarget target) => target switch
    {
        ExportTarget.Gltf => [".glb", ".gltf"],
        ExportTarget.UEFormat => [".uemodel"],
        _ => [".psk", ".pskx"],
    };

    private static string? MeshFileFor(string outputDir, string assetPath, string? reported,
                                       ExportTarget target)
    {
        var exts = ExtensionsFor(target);
        if (reported is { Length: > 0 } &&
            exts.Any(e => reported.EndsWith(e, StringComparison.OrdinalIgnoreCase)) &&
            File.Exists(reported)) return reported;

        var stem = Path.Combine(outputDir, Path.ChangeExtension(assetPath, null) ?? assetPath);
        foreach (var ext in exts)
            if (File.Exists(stem + ext)) return stem + ext;
        return null;
    }

    // Materials are exportable in their own right, and have to be: a variant's overrides are
    // separate assets from the meshes that use them, so exporting only meshes loses them.
    private static bool IsExportable(UObject? o) =>
        o is USkeletalMesh or UStaticMesh or UAnimSequence or UAnimMontage or UMaterialInterface
            or CUE4Parse.UE4.Assets.Exports.Texture.UTexture;
}

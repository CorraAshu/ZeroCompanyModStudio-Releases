using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;

namespace UEToolkit.Core;

public enum ExportTarget
{
    /// <summary>ActorX .psk/.pskx + .psa — the 3ds Max path via ActorX Importer.</summary>
    ActorX,
    /// <summary>glTF 2.0 binary — good for Blender and quick visual checks.</summary>
    Gltf,
    /// <summary>UEFormat .uemodel/.ueanim — Blender addon.</summary>
    UEFormat,
    /// <summary>
    /// Autodesk FBX. Not implemented yet; planned via the Python FBX SDK driven as a
    /// subprocess over an intermediate file, which avoids any C++/CLI interop.
    /// </summary>
    Fbx
}

public sealed record ExportRequest(
    ExportTarget Target,
    string OutputDirectory,
    // TGA, not PNG. CUE4Parse's PNG writer composites the alpha into the RGB channels, so a
    // mask or packed map opens looking like the flattened result and the real colour data is
    // gone -- recoverable only by round-tripping through a tool that reads it differently.
    // TGA keeps the channels separate.
    ETextureFormat TextureFormat = ETextureFormat.Tga,
    bool ExportMaterials = true,
    bool ExportMorphTargets = true,
    // Which geometry, and how much of it. Both were hardcoded, which decided silently that every
    // export is the full-detail Nanite mesh -- the reason a whole-game dump is ~70 GB, with
    // single environment meshes at 160 MB. A Nanite mesh is source-density geometry; the normal
    // LODs are what the game actually renders at distance and are far more tractable in a DCC.
    // Neither choice is wrong, but it should be a choice.
    ELodFormat LodFormat = ELodFormat.FirstLod,
    ENaniteMeshFormat NaniteFormat = ENaniteMeshFormat.OnlyNaniteLOD);

/// <summary>
/// <paramref name="Exception"/> is the original throw, kept because a flattened message loses
/// the call path — and on an unfamiliar game the call path IS the finding ("this failed while
/// loading the additive base pose", not "this failed").
/// </summary>
public sealed record ExportResult(bool Success, string? SavedPath, string? Label, string? Error,
    Exception? Exception = null);

/// <summary>
/// Thin seam over CUE4Parse-Conversion so the rest of the toolkit never talks to the
/// exporter directly. FBX slots in here later without touching callers.
/// </summary>
public static class AssetExporter
{
    /// <summary>
    /// Serialise conversion against every other provider read, on the one gate.
    ///
    /// Converting a mesh is not a pure transform of an already-loaded object: it resolves
    /// materials and pulls `.ubulk` payloads, so it reaches the same CUE4Parse cache that
    /// <see cref="AssetLoader"/> guards. Running it unguarded on `--jobs` threads is the last
    /// open hole behind the export non-determinism -- a material intermittently resolving to
    /// nothing (so its UVs ship un-remapped), and once a point count moving while the wedge count
    /// held, which is geometry rather than material binding.
    ///
    /// Default ON because a wrong export that reports success is worse than a slow one. Measure
    /// the cost with `--fastexport` before assuming it matters.
    /// </summary>
    public static bool SerializeConversion = true;

    public static ExportResult Export(UObject asset, ExportRequest request)
        => SerializeConversion
            ? ExportLocked(asset, request)
            : ExportCore(asset, request);

    private static ExportResult ExportLocked(UObject asset, ExportRequest request)
    {
        lock (AssetLoader.Gate) return ExportCore(asset, request);
    }

    private static ExportResult ExportCore(UObject asset, ExportRequest request)
    {
        // FBX, WRITTEN HERE, WITH NOTHING INSTALLED. See FbxWriter for why that matters: the
        // other route is a PSK plus the Autodesk SDK for Python, which is a licence-encumbered
        // install almost nobody who downloads a mod tool will have.
        // Meshes only. A texture or an animation asked for during an FBX dump still has to come
        // out, and it comes out its own way -- routing everything here would have turned a
        // whole-game dump with FBX selected into a dump with no textures in it.
        if (request.Target == ExportTarget.Fbx && asset is USkeletalMesh or UStaticMesh)
            return Fbx(asset, request);

        // A TEXTURE ASKED FOR BY NAME. CUE4Parse's generic exporter refuses a UTexture2D
        // outright -- "export of ... is not supported yet" -- so until now every texture in a
        // dump was counted as a failure, and the only ones that reached disk were the ones a
        // mesh happened to drag along through its materials. Tick "Textures" and filter to a
        // folder of them and you got nothing at all. TextureExport has done this correctly for
        // ages; it was simply never on this path.
        if (asset is UTexture texture)
        {
            var (file, why) = TextureExport.Write(texture, request.OutputDirectory,
                asset.Owner?.Name ?? asset.Name, request.TextureFormat);
            return file is null
                ? new ExportResult(false, null, null, why ?? "the texture wrote nothing")
                : new ExportResult(true, file, asset.Name, null);
        }

        try
        {
            var options = BuildOptions(request);
            var exporter = new Exporter(asset, options);
            var dir = new DirectoryInfo(request.OutputDirectory);
            dir.Create();

            return exporter.TryWriteToDir(dir, out var label, out var saved)
                ? new ExportResult(true, saved, label, null)
                : new ExportResult(false, null, null, "Exporter reported failure.");
        }
        catch (Exception e)
        {
            // One bad texture aborts a whole mesh export, so surface the real reason.
            return new ExportResult(false, null, null, Flatten(e), e);
        }
    }

    /// <summary>
    /// One mesh as an .fbx, through <see cref="FbxMeshExport"/>.
    ///
    /// The path mirrors the game's own tree the same way every other export here does, so a file
    /// lands where somebody who knows the browser would look for it.
    /// </summary>
    private static ExportResult Fbx(UObject asset, ExportRequest request)
    {
        try
        {
            var name = asset.Name;
            // THE LEADING SLASH HAS TO GO. A package name is "/Game/Game/Characters/...", which
            // is ROOTED -- and Path.Combine throws the first argument away when the second is
            // rooted, so an export politely wrote nine files to \Game\... on the drive root instead of to the
            // folder the author picked. It reported success, because it had succeeded.
            var folder = request.OutputDirectory;
            if (asset.Owner?.Name is { Length: > 0 } package)
            {
                var relative = Path.GetDirectoryName(
                    package.Replace('/', Path.DirectorySeparatorChar)) ?? "";
                relative = relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (relative.Length > 0) folder = Path.Combine(request.OutputDirectory, relative);
            }
            var path = Path.Combine(folder, name + ".fbx");

            // RIGHT-HANDED, because the file says it is. UE is left-handed and the FBX declares the
            // Max axis system (right-handed, Z up), so the game's own numbers arrive in Blender or
            // 3ds Max as a MIRROR IMAGE of the game, inside out -- and a model edited there and brought
            // back through the Studio's importer, which reads a right-handed file, comes in mirrored.
            // With this on the mesh, every bone, the skin and the bind pose are written in one basis
            // (see FbxMeshExport.Options.RightHanded). PSK, glTF and the other formats are untouched.
            var options = new FbxMeshExport.Options(RightHanded: true);

            bool ok;
            string? why;
            switch (asset)
            {
                case USkeletalMesh skeletal:
                    if (!skeletal.TryConvert(out var converted))
                        return new ExportResult(false, null, null, "the mesh does not convert");
                    ok = FbxMeshExport.WriteSkeletal(converted, name, path, options, out why);
                    break;

                case UStaticMesh stat:
                    var still = MeshConvert.Static(stat, out _);
                    if (still is null)
                        return new ExportResult(false, null, null, "the mesh does not convert");
                    ok = FbxMeshExport.WriteStatic(still, name, path, options, out why);
                    break;

                default:
                    return new ExportResult(false, null, null, "not a mesh");
            }

            return ok ? new ExportResult(true, path, name, null)
                      : new ExportResult(false, null, null, why ?? "the writer reported failure");
        }
        catch (Exception e) { return new ExportResult(false, null, null, Flatten(e), e); }
    }

    public static ExporterOptions BuildOptions(ExportRequest request)
    {
        var meshFormat = request.Target switch
        {
            ExportTarget.Gltf => EMeshFormat.Gltf2,
            ExportTarget.UEFormat => EMeshFormat.UEFormat,
            _ => EMeshFormat.ActorX
        };

        return new ExporterOptions
        {
            MeshFormat = meshFormat,
            // CUE4Parse derives anim format from mesh format; keep them consistent.
            AnimFormat = meshFormat == EMeshFormat.UEFormat ? EAnimFormat.UEFormat : EAnimFormat.ActorX,
            NaniteMeshFormat = request.NaniteFormat,
            MaterialFormat = EMaterialFormat.AllLayersNoRef,
            TextureFormat = request.TextureFormat,
            LodFormat = request.LodFormat,
            SocketFormat = ESocketFormat.Bone,
            ExportMaterials = request.ExportMaterials,
            ExportMorphTargets = request.ExportMorphTargets,
            ExportHdrTexturesAsHdr = true,
        };
    }

    private static string Flatten(Exception e) => e switch
    {
        AggregateException agg => string.Join(" | ", agg.Flatten().InnerExceptions.Select(Describe).Distinct()),
        _ => Describe(e)
    };

    /// <summary>
    /// Message plus where it was thrown. "Invalid tag" is a true statement about four different
    /// formats in this codebase, and without a frame there is no way to tell which one is
    /// complaining -- the message alone sent an ACL investigation after a Chaos parser.
    /// </summary>
    private static string Describe(Exception e)
    {
        var frame = e.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
        return frame is null ? e.Message : $"{e.Message}  @ {frame}";
    }
}

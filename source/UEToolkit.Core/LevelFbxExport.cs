using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.Textures;
using static UEToolkit.Core.FbxWriter;

namespace UEToolkit.Core;

/// <summary>
/// A LEVEL AS ONE FBX: the meshes a viewport is drawing, at the places it draws them, with their
/// materials, written to disk one node at a time.
///
/// WHAT COMES OUT of <see cref="Export"/>, in the folder it is given:
///
///     name.fbx             one file. Every mesh is stored ONCE and placed as many times as it is
///                          used (a Geometry that many Models share), so a map with tens of
///                          thousands of placements is a few hundred megabytes and not gigabytes.
///     name.materials.json  the sidecar: for every material, every texture parameter and the file
///                          it became, plus the scalar / vector / switch values the game sets.
///     Textures\*.png       every texture any material carries, flat, written once each, named
///                          after the texture ASSET, as the game ships it (alpha included). An OPAQUE
///                          material whose base colour alpha is not fully opaque is wired to a second,
///                          alpha-free copy, NAME__rgb.png (see CUT-OUT below).
///     name.3dsmax-cutouts.ms
///                          only when some material is a cut-out: a MaxScript the person runs once in
///                          3ds Max after importing the FBX, because Max's importer cannot bring a
///                          cut-out in by itself (see <see cref="LevelFbxMaxScript"/>).
///
/// THE SCENE: one empty Model per sublevel (named after it) that parents that sublevel's
/// placements, so a person can hide a sublevel in Blender or Max; under it one Model per placement,
/// named after its actor (a repeated name gets _2, _3, ...). Each placement's materials are
/// connected to ITS Model, because a Geometry that is shared cannot say which material a copy wears:
/// the same crate painted three ways is three Models connecting three sets of Materials to one
/// Geometry.
///
/// HANDEDNESS. UE is left-handed and the file declares a right-handed (Max) axis system, so a level
/// written in UE's own space arrives mirrored as a whole. The export turns on
/// <see cref="FbxMeshExport.Options.RightHanded"/> -- the vertices and normals negate Y (the winding
/// stays: see that option for why) -- and writes every placement as P * M * P, P = diag(1, -1, 1), in the
/// same row-vector convention as M, BEFORE <see cref="FbxPlacement.Lcl"/> (which keeps the
/// determinant, so a mirrored placement is still a mirror). Both halves or neither: a mesh on one
/// side of the basis and its placement on the other is a scene that is wrong in a way no position
/// readback can see.
///
/// CUT-OUT. A material the viewport draws with an alpha test -- BlendMode Masked or Translucent, or
/// the switch "Use BaseColor Texture Alpha", except the character / weapon / eye families whose alpha is not an
/// opacity (see <see cref="IsCutOut"/>) -- has its base colour texture's ALPHA wired as opacity:
/// the same Texture is also connected to the material's <see cref="OpacityProperty"/>, and the material
/// states an Opacity of 1 and a TransparencyFactor of 0 so that texture alone decides. That is
/// <see cref="IsCutOut"/>, the viewport's own test. Every other material is OPAQUE. Its base colour texture
/// is still written as the game ships it, alpha included, but Blender (both importers) wires any alpha
/// channel it finds in a base-colour image to the material's opacity, so an opaque material whose texture
/// keeps something else in its alpha would turn see-through. When that texture's alpha is not fully
/// opaque, the material is wired instead to an alpha-free copy (NAME__rgb.png, written once); a fully
/// opaque alpha cannot hide anything and needs no copy.
///
/// WHAT IT NEVER DOES SILENTLY. A mesh that will not load or convert, a placement whose matrix is
/// not finite, a material that will not load, a texture that will not decode: each is named in
/// <see cref="Result.Missing"/> and in the manifest, and everything else is still written. Every
/// node is BUILT COMPLETELY before the stream is handed it (a throw halfway through a node leaves
/// <see cref="FbxStream"/> unusable), so a failure is caught before a byte of it is written.
///
/// THE PACKAGE GATE. CUE4Parse's package cache is not safe to race (see <see cref="AssetLoader.Gate"/>).
/// Everything here that reads a package -- loading and converting a mesh, loading a material, the
/// <c>choose</c> callback, reading a texture's name -- runs under <see cref="AssetLoader.Read{T}"/>,
/// which is re-entrant, so a caller already holding the gate is not deadlocked. The slow, pure-CPU
/// parts (encoding the FBX, decoding and writing a PNG) run outside it.
/// </summary>
public static class LevelFbxExport
{
    /// <summary>One drawn placement. An instanced row is its own Item.</summary>
    /// <param name="MeshPath">The mesh asset, as the viewport names it (an object or package path).</param>
    /// <param name="WorldUe">The placement in UE's space, System.Numerics row-vector convention, as <c>Scale * Rotation * Translation</c>.</param>
    /// <param name="Actor">The actor's name; becomes the Model's name.</param>
    /// <param name="Sublevel">The sublevel it belongs to; becomes the parent group's name.</param>
    /// <param name="OverrideMaterials">The component's material overrides by SLOT index; a null entry keeps the mesh's own.</param>
    public sealed record Item(string MeshPath, Matrix4x4 WorldUe, string Actor, string Sublevel,
                              string?[]? OverrideMaterials);

    /// <summary>
    /// The textures a material's picture comes from, as the viewport chose them. <c>BaseColor</c> and
    /// <c>Normal</c> are wired into the slots Blender and Max read on import; every texture in
    /// <c>Params.Textures</c> is written to the Textures folder and listed in the manifest. Read-only.
    /// </summary>
    public sealed record Textures(UTexture? BaseColor, UTexture? Normal, CMaterialParams2? Params);

    /// <param name="Placements">Placements written to the file.</param>
    /// <param name="Meshes">Distinct meshes written (Geometry records).</param>
    /// <param name="Materials">Material records written.</param>
    /// <param name="TexturesWritten">Texture files written.</param>
    /// <param name="Missing">Everything the file does not contain that it was asked to, each named: "what: why".</param>
    /// <param name="PeakWorkingSetBytes">The PROCESS's peak working set when the export finished (it only ever rises, so it includes whatever ran before).</param>
    public sealed record Result(string FbxPath, string ManifestPath, int Placements, int Meshes, int Materials,
                                int TexturesWritten, IReadOnlyList<string> Missing, TimeSpan Took,
                                long PeakWorkingSetBytes)
    {
        /// <summary>Of <see cref="Meshes"/>, how many are skeletal meshes written in their reference pose as plain geometry.</summary>
        public int SkeletalMeshes { get; init; }

        /// <summary>Of <see cref="Placements"/>, how many place one of those.</summary>
        public int SkeletalPlacements { get; init; }

        /// <summary>Placements the file does not contain (their mesh failed, or the matrix was not finite). Also named in <see cref="Missing"/>.</summary>
        public int PlacementsSkipped { get; init; }

        /// <summary>Sublevel groups written.</summary>
        public int Sublevels { get; init; }

        /// <summary>The 3ds Max script written beside the FBX, or null when no material is a cut-out.</summary>
        public string? MaxScriptPath { get; init; }

        /// <summary>The cut-out materials (by FBX material name) the script lists: what the manifest marks <c>cutOut</c>.</summary>
        public int CutOutMaterials { get; init; }
    }

    /// <summary>The flat folder, beside the FBX, that the textures are written to.</summary>
    public const string TexturesFolder = "Textures";

    /// <summary>
    /// The FBX material property a cut-out material's opacity texture is connected to: FBX's own name
    /// for an opacity map. The base colour is connected to DiffuseColor as well, the one Texture twice.
    /// </summary>
    public const string OpacityProperty = "TransparentColor";

    /// <summary>
    /// Whether the material is drawn with its base colour texture's alpha as a cut-out: THE ONE RULE, used by the
    /// viewport's texture binder (its alpha test) and by this exporter (the opacity wiring, the Max script). The
    /// viewport additionally needs a base colour to cut with; that half is the caller's.
    ///
    ///  - the switch "Use BaseColor Texture Alpha" on: the material says outright that the alpha is its opacity. CUT.
    ///  - BlendMode Translucent: CUT (decals, glass, effects: the alpha is the opacity).
    ///  - BlendMode Masked: CUT, EXCEPT in <see cref="AlphaCarriesOpacity"/>'s excluded families.
    ///
    /// WHY AN EXCLUSION. This game cooks most of its master materials as Masked and the cooked material does not
    /// say what its opacity mask reads: the expression graph is stripped, and the cached data keeps only parameter
    /// names and a bitmask of connected inputs (OpacityMask is connected on every Masked master). On the character,
    /// weapon and eye masters the base colour's alpha is a packed paint / damage / subsurface channel, not an
    /// opacity: 46% of Rex's helmet texture is under the 0.333 clip, 99% of a head's, and the game draws neither
    /// with a hole. Cutting by that alpha made helmets pierced, heads vanish (all but the eyes and teeth) and
    /// sclerae disappear. Measured over all 6,617 materials the game ships, each family's base colour alpha sampled.
    /// </summary>
    public static bool IsCutOut(UMaterialInterface? material, CMaterialParams2? parameters)
    {
        if (parameters is null) return false;
        if (parameters.TryGetSwitch(out var useAlpha, "Use BaseColor Texture Alpha") && useAlpha) return true;
        if (parameters.BlendMode is EBlendMode.BLEND_Translucent) return true;
        return parameters.BlendMode is EBlendMode.BLEND_Masked && AlphaCarriesOpacity(FamilyOf(material));
    }

    /// <summary>
    /// False for the root material families whose Masked blend is only the master's setting and whose base colour
    /// alpha is NOT the opacity (see <see cref="IsCutOut"/>): the customisable character gear, the skins, the
    /// weapons and the eyes. Prefixes of the family's name, so a later variant is covered; every other family
    /// (foliage, hair cards, decals, tiled kit, rocks, effects) keeps the cut.
    /// </summary>
    public static bool AlphaCarriesOpacity(string family)
    {
        foreach (var prefix in AlphaIsNotOpacity)
            if (family.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return true;
    }

    private static readonly string[] AlphaIsNotOpacity =
        ["M_CH_Customizable", "M_CH_Skin", "MM_CH_Skin", "M_Weapon", "M_EyeRefractive"];

    /// <summary>
    /// The name of the root parent material, walked up the instance chain (chains run nine deep in this game, so a
    /// walk and not one hop). Empty for no material.
    /// </summary>
    public static string FamilyOf(UMaterialInterface? material)
    {
        var cur = material;
        for (var i = 0; i < 12 && cur is not null; i++)
        {
            if (cur is not UMaterialInstance inst) break;
            if (inst.Parent is not UMaterialInterface up) break;
            cur = up;
        }
        return cur?.Name ?? "";
    }

    /// <summary>
    /// Write <paramref name="items"/> as <c>folder\name.fbx</c> (+ manifest + textures).
    ///
    /// The FBX is written as <c>name.fbx.part</c> and renamed when everything else is done. On cancel
    /// (an <see cref="OperationCanceledException"/>) or any failure the .part, the manifest and every
    /// texture file THIS call created are removed and the exception propagates: a half export never
    /// looks like a finished one. Textures that already existed in the folder are overwritten, not
    /// removed.
    ///
    /// <paramref name="choose"/> is called once per distinct material, under the package gate, and
    /// must not keep what it is handed. <paramref name="progress"/> is told after each mesh and each
    /// material; its Total grows once, when the materials are discovered after the meshes.
    /// </summary>
    public static Result Export(IFileProvider provider, IReadOnlyList<Item> items, string folder, string name,
                                Func<UMaterialInterface, Textures?> choose,
                                IProgress<(int Done, int Total, string What)>? progress,
                                CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(choose);
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("no folder", nameof(folder));
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("the name must be a plain file name", nameof(name));
        return new Exporter(provider, items, folder, name, choose, progress, cancel).Run();
    }

    // ------------------------------------------------------------------ the basis

    /// <summary>P = diag(1, -1, 1): UE (left-handed) to the file's right-handed space. Its own inverse.</summary>
    private static readonly Matrix4x4 Basis = Matrix4x4.CreateScale(1f, -1f, 1f);

    // ------------------------------------------------------------------ the run

    private readonly record struct SectionSlot(int MaterialIndex, string? Name, string Material);

    private readonly record struct Link(long Child, long Parent, string? Property);

    private sealed class MeshInfo(string path, string leaf)
    {
        public readonly string Path = path;
        public readonly string Leaf = leaf;
        public long GeometryId;
        public bool Written;
        public bool Skeletal;
        public string Why = "";
        public SectionSlot[] Slots = [];
        public int Placements;
        public long[]? PlainMaterials;       // the material ids of a placement with no overrides, made once
    }

    private sealed class MaterialRec(string key, string path, string slotName, long id)
    {
        public readonly string Key = key;
        public readonly string Path = path;          // the game path; "" for a slot the mesh gives no material
        public readonly string SlotName = slotName;
        public readonly long Id = id;
    }

    /// <summary>What one material came to once its parameters were read and its textures written.</summary>
    private sealed class ResolvedMaterial
    {
        public string? Why;
        public readonly List<(string Param, string? File)> Textures = [];
        public string? DiffuseFile, NormalFile;
        public string BlendMode = "unknown";    // the game's blend mode, as the viewport read it
        public bool CutOut;                     // the base colour's alpha is wired as opacity
        public readonly List<(string Name, float Value)> Scalars = [];
        public readonly List<(string Name, float[] Value)> Vectors = [];
        public readonly List<(string Name, bool Value)> Switches = [];
    }

    private sealed class WrittenTexture(string gamePath, string? file, bool rgbCopy, bool alphaNotOpaque)
    {
        public readonly string GamePath = gamePath;
        public readonly string? File = file;         // relative to the export folder, forward slashes; null when it failed
        public readonly bool RgbCopy = rgbCopy;      // an alpha-free copy of another written file: the base colour of opaque materials only
        public readonly bool AlphaNotOpaque = alphaNotOpaque;   // the RGBA file has a texel that is not fully opaque
        public string? RgbFile;                      // the alpha-free copy of this file, once one was written
        public string? CopyOf;                       // for a copy: the RGBA file it was made from
        public bool RgbTried;                        // a copy was attempted (written or failed): never twice
    }

    private sealed class WiredTexture(string file, string gamePath, string name, long id, long videoId)
    {
        public readonly string File = file;
        public readonly string GamePath = gamePath;
        public readonly string Name = name;
        public readonly long Id = id;
        public readonly long VideoId = videoId;
    }

    private sealed class ManifestMaterial(MaterialRec rec, string fbxName, ResolvedMaterial resolved)
    {
        public readonly MaterialRec Rec = rec;
        public readonly string FbxName = fbxName;
        public readonly ResolvedMaterial Resolved = resolved;
    }

    private sealed class Exporter
    {
        private readonly IFileProvider _provider;
        private readonly IReadOnlyList<Item> _items;
        private readonly string _folder, _name;
        private readonly Func<UMaterialInterface, Textures?> _choose;
        private readonly IProgress<(int Done, int Total, string What)>? _progress;
        private readonly CancellationToken _cancel;

        private readonly FbxMeshExport.Options _options = new(RightHanded: true);
        private readonly FbxMeshExport.IdSource _ids = new();
        private readonly List<(string What, string Why)> _missing = [];
        private readonly List<Link> _links = [];

        // Names. Objects (Models) share one pool so a Blender scene never has to rename them.
        private readonly HashSet<string> _objectNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _serial = new(StringComparer.Ordinal);
        // Case-insensitive: the 3ds Max script finds a material by name without regard to case, so two names that differ
        // only in case would let it switch the wrong one.
        private readonly HashSet<string> _materialNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _textureNames = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, MeshInfo> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MeshInfo> _meshOrder = [];
        private readonly Dictionary<string, MaterialRec> _materials = new(StringComparer.Ordinal);
        private readonly List<MaterialRec> _materialOrder = [];
        private readonly Dictionary<string, ResolvedMaterial> _resolved = new(StringComparer.Ordinal);
        private readonly List<ManifestMaterial> _manifest = [];
        private readonly Dictionary<string, WrittenTexture> _written = new(StringComparer.Ordinal);
        private readonly List<WrittenTexture> _writtenOrder = [];
        private readonly Dictionary<string, WiredTexture> _wired = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<WiredTexture> _wiredOrder = [];
        private readonly List<string> _createdTextures = [];
        private readonly string _textureDir;
        private readonly bool _textureDirExisted;

        private int _done, _total;
        private int _notTextures;
        private int _notTextureMaterials;

        public Exporter(IFileProvider provider, IReadOnlyList<Item> items, string folder, string name,
                        Func<UMaterialInterface, Textures?> choose,
                        IProgress<(int Done, int Total, string What)>? progress, CancellationToken cancel)
        {
            _provider = provider; _items = items; _folder = folder; _name = name;
            _choose = choose; _progress = progress; _cancel = cancel;
            _textureDir = Path.Combine(folder, TexturesFolder);
            _textureDirExisted = Directory.Exists(_textureDir);
        }

        // ============================================================== the run

        public Result Run()
        {
            var clock = Stopwatch.StartNew();
            Directory.CreateDirectory(_folder);
            var fbx = Path.Combine(_folder, _name + ".fbx");
            var part = fbx + ".part";
            var manifest = Path.Combine(_folder, _name + ".materials.json");
            var manifestPart = manifest + ".part";
            var script = Path.Combine(_folder, _name + LevelFbxMaxScript.Suffix);
            var scriptPart = script + ".part";

            FbxStream? stream = null;
            var fbxMoved = false;
            var manifestMoved = false;
            try
            {
                _cancel.ThrowIfCancellationRequested();
                stream = FbxStream.Create(part);
                var counts = Build(stream);
                stream.Finish();
                stream.Dispose();
                stream = null;

                // THE 3DS MAX SCRIPT: one per export that has a cut-out material, listing exactly the materials the
                // manifest marks cutOut. Written beside the others as a .part and renamed last.
                var cutOuts = _manifest.Where(m => m.Resolved.CutOut).Select(m => m.FbxName).ToList();
                // An older script of this name would list materials this export does not have. It goes BEFORE the manifest is
                // written so that a script which cannot be deleted is named in the manifest's missing list too.
                if (cutOuts.Count == 0 && File.Exists(script) && !TryDelete(script))
                    _missing.Add((Path.GetFileName(script), "an older script of this name could not be deleted and lists materials this export does not have; delete it by hand"));
                WriteManifest(manifestPart, cutOuts.Count > 0 ? _name + LevelFbxMaxScript.Suffix : null);
                if (cutOuts.Count > 0)
                    File.WriteAllText(scriptPart, LevelFbxMaxScript.Build(_name, cutOuts), new UTF8Encoding(false));

                // EVERYTHING THE RESULT NEEDS IS COMPUTED BEFORE THE FIRST RENAME: a throw after a rename would leave a
                // script (or manifest) beside an export that is then taken back. The one thing that can still fail after
                // the first rename is the renames themselves, and those are undone below.
                long peak;
                using (var self = Process.GetCurrentProcess()) peak = self.PeakWorkingSet64;
                var missing = _missing.Select(m => $"{m.What}: {m.Why}").ToList();
                var written = _writtenOrder.Count(t => t.File is not null);
                var tookNow = clock.Elapsed;
                var result = new Result(fbx, manifest, counts.Placed, counts.Meshes, counts.Materials,
                        written, missing, tookNow, peak)
                {
                    SkeletalMeshes = counts.SkeletalMeshes,
                    SkeletalPlacements = counts.SkeletalPlacements,
                    PlacementsSkipped = counts.Skipped,
                    Sublevels = counts.Sublevels,
                    MaxScriptPath = cutOuts.Count > 0 ? script : null,
                    CutOutMaterials = cutOuts.Count,
                };

                File.Move(part, fbx, overwrite: true);
                fbxMoved = true;
                File.Move(manifestPart, manifest, overwrite: true);
                manifestMoved = true;
                if (cutOuts.Count > 0) File.Move(scriptPart, script, overwrite: true);
                return result;
            }
            catch
            {
                // NOTHING OF A HALF EXPORT STAYS. The stream is closed first or the .part cannot be deleted.
                try { stream?.Dispose(); } catch { /* the original failure is the one to report */ }
                TryDelete(part);
                TryDelete(manifestPart);
                TryDelete(scriptPart);
                if (manifestMoved) TryDelete(manifest);
                if (fbxMoved) TryDelete(fbx);
                foreach (var texture in _createdTextures) TryDelete(texture);
                if (!_textureDirExisted) TryRemoveEmptyDirectory(_textureDir);
                throw;
            }
        }

        private readonly record struct Counts(int Placed, int Meshes, int Materials, int SkeletalMeshes,
                                              int SkeletalPlacements, int Skipped, int Sublevels);

        private Counts Build(FbxStream stream)
        {
            // ---------------------------------------------------------- what is to be written
            var sublevels = new List<string>();
            var sublevelIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in _items)
            {
                var group = GroupName(item.Sublevel);
                if (sublevelIndex.TryAdd(group, sublevels.Count)) sublevels.Add(group);

                var key = item.MeshPath ?? "";
                if (!_meshes.TryGetValue(key, out var mesh))
                {
                    mesh = new MeshInfo(key, LeafOf(key));
                    _meshes.Add(key, mesh);
                    _meshOrder.Add(mesh);
                }
                mesh.Placements++;
            }
            _total = _meshOrder.Count;

            // ---------------------------------------------------------- the header and the counts
            foreach (var header in FbxMeshExport.HeaderNodes(_options)) stream.Write(header);

            var total = stream.ReserveInt();
            var attributes = stream.ReserveInt();
            var geometries = stream.ReserveInt();
            var models = stream.ReserveInt();
            var materialCount = stream.ReserveInt();
            var textureCount = stream.ReserveInt();
            var videoCount = stream.ReserveInt();
            var definitions = FbxMeshExport.DefinitionsNode(total,
            [
                ("NodeAttribute", attributes), ("Geometry", geometries), ("Model", models),
                ("Material", materialCount), ("Texture", textureCount), ("Video", videoCount),
            ]);
            // The group nodes are NULLs, not the skeletons the shared table assumes of every NodeAttribute.
            foreach (var type in definitions.Children.Where(c => c.Name == "ObjectType" && Equals(c.Properties[0], "NodeAttribute")))
                foreach (var template in type.Children.Where(c => c.Name == "PropertyTemplate"))
                    template.Properties[0] = "FbxNull";
            stream.Write(definitions);

            stream.Open("Objects");

            // ---------------------------------------------------------- the sublevel groups
            var groupIds = new long[sublevels.Count];
            for (var i = 0; i < sublevels.Count; i++)
            {
                var attributeId = _ids.Next();
                groupIds[i] = _ids.Next();
                var groupName = UniqueObjectName(Clean(sublevels[i]));
                stream.Child(NullAttribute(attributeId, groupName));
                stream.Child(NullModel(groupIds[i], groupName));
                _links.Add(new Link(attributeId, groupIds[i], null));
                _links.Add(new Link(groupIds[i], 0L, null));
            }

            // ---------------------------------------------------------- pass 1: the meshes
            var geometryCount = 0;
            var skeletalMeshes = 0;
            foreach (var mesh in _meshOrder)
            {
                _cancel.ThrowIfCancellationRequested();
                mesh.GeometryId = _ids.Next();

                Node? node = null;
                try
                {
                    var built = BuildGeometry(mesh);
                    node = built.Node;
                    mesh.Slots = built.Slots;
                    mesh.Skeletal = built.Skeletal;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { mesh.Why = e.Message; }

                if (node is not null)
                {
                    stream.Child(node);
                    mesh.Written = true;
                    geometryCount++;
                    if (mesh.Skeletal) skeletalMeshes++;
                }
                else
                {
                    _missing.Add((mesh.Path.Length > 0 ? mesh.Path : "(an item with no mesh path)",
                        $"{mesh.Why}; {mesh.Placements:N0} placement(s) left out"));
                }
                _progress?.Report((++_done, _total, mesh.Leaf));
            }

            // ---------------------------------------------------------- pass 2: the placements
            var placed = 0;
            var skipped = 0;
            var skeletalPlacements = 0;
            var refused = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                if ((i & 255) == 0) _cancel.ThrowIfCancellationRequested();
                var item = _items[i];
                var mesh = _meshes[item.MeshPath ?? ""];
                if (!mesh.Written) { skipped++; continue; }

                Node model;
                var modelId = _ids.Next();
                try
                {
                    // P * M * P, row-vector convention, BEFORE the decomposition: the mesh was written
                    // as v * P, so its placement is the same transform seen through the same basis.
                    var (t, r, s) = FbxPlacement.Lcl(Basis * item.WorldUe * Basis);
                    var actor = string.IsNullOrWhiteSpace(item.Actor) ? mesh.Leaf : item.Actor;
                    model = FbxMeshExport.ModelNode(modelId, UniqueObjectName(Clean(actor)), t, r, s);
                }
                catch (ArgumentException e)
                {
                    // A matrix with a NaN or infinite element cannot be put in a file. Named, capped.
                    skipped++;
                    if (++refused <= 50)
                        _missing.Add(($"{item.Actor} ({mesh.Leaf})", $"placement left out: {e.Message}"));
                    continue;
                }

                stream.Child(model);
                placed++;
                if (mesh.Skeletal) skeletalPlacements++;

                _links.Add(new Link(modelId, groupIds[sublevelIndex[GroupName(item.Sublevel)]], null));
                _links.Add(new Link(mesh.GeometryId, modelId, null));
                foreach (var material in MaterialsFor(mesh, item.OverrideMaterials))
                    _links.Add(new Link(material, modelId, null));
            }
            if (refused > 50)
                _missing.Add(("placements with a matrix that is not finite", $"{refused - 50:N0} more, not named individually"));

            // ---------------------------------------------------------- pass 3: the materials
            _total = _meshOrder.Count + _materialOrder.Count;
            foreach (var rec in _materialOrder)
            {
                _cancel.ThrowIfCancellationRequested();
                stream.Child(WriteMaterial(rec));
                _progress?.Report((++_done, _total, rec.Path.Length > 0 ? LeafOf(rec.Path) : rec.SlotName));
            }
            if (_notTextures > 0)
                _missing.Add(("texture parameters that are not textures",
                    $"{_notTextures:N0} parameter(s) across {_notTextureMaterials:N0} material(s) hold something other than a texture; listed in the manifest with no file"));

            // The wired textures last, so the Objects section groups its types the way the SDK's own files do.
            foreach (var texture in _wiredOrder) stream.Child(TextureNode(texture));
            foreach (var texture in _wiredOrder) stream.Child(VideoNode(texture));
            stream.Close();

            // ---------------------------------------------------------- the connections
            stream.Open("Connections");
            foreach (var link in _links)
                stream.Child(link.Property is null
                    ? new Node("C").P("OO", link.Child, link.Parent)
                    : new Node("C").P("OP", link.Child, link.Parent, link.Property));
            stream.Close();

            var takes = new Node("Takes");
            takes.Add("Current").P("");
            stream.Write(takes);

            // ---------------------------------------------------------- the counts, now known
            // One NodeAttribute per group; one Model per group and per placement; a Texture and a
            // Video per wired file.
            var groups = sublevels.Count;
            var modelTotal = groups + placed;
            var materialsWritten = _materialOrder.Count;
            stream.Patch(attributes, groups);
            stream.Patch(geometries, geometryCount);
            stream.Patch(models, modelTotal);
            stream.Patch(materialCount, materialsWritten);
            stream.Patch(textureCount, _wiredOrder.Count);
            stream.Patch(videoCount, _wiredOrder.Count);
            // GlobalSettings counts as one more than the objects (see FbxMeshExport.DefinitionsNode).
            stream.Patch(total, groups + geometryCount + modelTotal + materialsWritten + _wiredOrder.Count * 2 + 1);

            return new Counts(placed, geometryCount, materialsWritten, skeletalMeshes, skeletalPlacements,
                              skipped, groups);
        }

        // ============================================================== meshes

        private (Node Node, SectionSlot[] Slots, bool Skeletal) BuildGeometry(MeshInfo mesh)
        {
            if (mesh.Path.Length == 0) throw new InvalidOperationException("the item names no mesh");

            // One gated stretch: load, convert, read the sections, build the node. The node is a pure
            // function of the converted arrays, but the arrays are lazy, so it is built before the
            // gate is let go rather than after.
            return AssetLoader.Read(() =>
            {
                if (AssetLoader.Load<UStaticMesh>(_provider, mesh.Path) is { } asset)
                {
                    var converted = MeshConvert.Static(asset, out _)
                                    ?? throw new InvalidOperationException("the mesh did not convert");
                    var lod = FbxMeshExport.StaticLod(converted)
                              ?? throw new InvalidOperationException("the mesh has no usable LOD");
                    var node = FbxMeshExport.StaticGeometry(mesh.GeometryId, mesh.Leaf, converted, _options);
                    return (node, SlotsOf(lod), false);
                }

                if (AssetLoader.Load<USkeletalMesh>(_provider, mesh.Path) is { } skinned)
                {
                    if (!skinned.TryConvert(out var converted))
                        throw new InvalidOperationException("the skeletal mesh did not convert");
                    var lod = FbxMeshExport.SkeletalLod(converted)
                              ?? throw new InvalidOperationException("the mesh has no usable LOD");
                    var node = FbxMeshExport.SkeletalReferencePose(mesh.GeometryId, mesh.Leaf, converted, _options);
                    return (node, SlotsOf(lod), true);
                }

                throw new InvalidOperationException((_provider.TryGetGameFile(mesh.Path, out _) || _provider.TryGetGameFile(AssetLoader.PackagePath(mesh.Path) + ".uasset", out _))
                    ? "the asset is not a mesh the toolkit can read"
                    : "the asset does not exist in the game files");
            });
        }

        private static SectionSlot[] SlotsOf(CBaseMeshLod lod)
        {
            var sections = lod.Sections?.Value ?? [];
            // A mesh with no sections still has the one slot the Geometry's material layer indexes.
            if (sections.Length == 0) return [new SectionSlot(-1, null, "")];
            return sections.Select(s => new SectionSlot(s.MaterialIndex, s.MaterialName,
                s.Material?.GetPathName() ?? "")).ToArray();
        }

        // ============================================================== materials

        /// <summary>
        /// The material ids a placement connects, in the order of the Geometry's material slots (its
        /// polygons index them by section). A slot the placement overrides wears the override; the
        /// rest wear the mesh's own. A material that would appear twice for one placement is given an
        /// ALIAS -- a second FBX Material with the same look -- because the DCCs index the slots by
        /// connection order and do not agree about a duplicate connection.
        /// </summary>
        private long[] MaterialsFor(MeshInfo mesh, string?[]? overrides)
        {
            // An array of nothing but nulls overrides nothing (the editor keeps the nulls in place).
            if (overrides is not null && overrides.All(string.IsNullOrEmpty)) overrides = null;
            if (overrides is null && mesh.PlainMaterials is { } known) return known;

            var ids = new long[mesh.Slots.Length];
            var taken = new List<string>(ids.Length);
            for (var i = 0; i < ids.Length; i++)
            {
                var slot = mesh.Slots[i];
                var path = overrides is not null && slot.MaterialIndex >= 0 && slot.MaterialIndex < overrides.Length &&
                           !string.IsNullOrEmpty(overrides[slot.MaterialIndex])
                    ? overrides[slot.MaterialIndex]!
                    : slot.Material;

                var baseKey = path.Length > 0 ? path : $"(no material) {mesh.Path} #{i}";
                var key = baseKey;
                for (var n = 2; taken.Contains(key); n++) key = $"{baseKey} #{n}";
                taken.Add(key);

                if (!_materials.TryGetValue(key, out var rec))
                {
                    rec = new MaterialRec(key, path, slot.Name ?? $"material_{i}", _ids.Next());
                    _materials.Add(key, rec);
                    _materialOrder.Add(rec);
                }
                ids[i] = rec.Id;
            }
            if (overrides is null) mesh.PlainMaterials = ids;
            return ids;
        }

        /// <summary>Read a material's parameters, write its textures, connect the wired ones, return its node.</summary>
        private Node WriteMaterial(MaterialRec rec)
        {
            if (!_resolved.TryGetValue(rec.Path, out var resolved))
            {
                resolved = rec.Path.Length > 0 ? Resolve(rec.Path) : new ResolvedMaterial { Why = "the mesh gives this slot no material" };
                _resolved.Add(rec.Path, resolved);
            }

            var fbxName = UniqueMaterialName(Clean(rec.Path.Length > 0 ? LeafOf(rec.Path) : rec.SlotName));
            _manifest.Add(new ManifestMaterial(rec, fbxName, resolved));

            if (resolved.DiffuseFile is { } diffuse)
            {
                var wired = Wire(diffuse);
                _links.Add(new Link(wired.Id, rec.Id, "DiffuseColor"));
                // THE CUT-OUT: the same Texture, its alpha, as the opacity. Only for the materials the viewport cuts.
                if (resolved.CutOut) _links.Add(new Link(wired.Id, rec.Id, OpacityProperty));
            }
            if (resolved.NormalFile is { } normal) _links.Add(new Link(Wire(normal).Id, rec.Id, "NormalMap"));

            return MaterialNode(rec.Id, fbxName, resolved.DiffuseFile is not null, resolved.NormalFile is not null, resolved.CutOut);
        }

        private ResolvedMaterial Resolve(string path)
        {
            var resolved = new ResolvedMaterial();

            // The reading is gated; the writing of the textures is not.
            var found = new List<(string Param, UTexture? Texture, string? GamePath, bool IsTexture)>();
            UTexture? baseColor = null, normal = null;
            var cutOut = false;
            string? baseColorPath = null, normalPath = null;
            try
            {
                AssetLoader.Read(() =>
                {
                    var material = LevelExport.LoadMaterial(_provider, path);
                    if (material is null) { resolved.Why = "the material did not load"; return 0; }

                    var choice = _choose(material);
                    if (choice is null) { resolved.Why = "the material's parameters could not be read"; return 0; }

                    baseColor = choice.BaseColor; normal = choice.Normal;
                    baseColorPath = baseColor?.GetPathName();
                    normalPath = normal?.GetPathName();
                    // The viewport cuts with the base colour's alpha, so no base colour is nothing to cut with.
                    cutOut = baseColor is not null && IsCutOut(material, choice.Params);
                    if (choice.Params is { } blend) resolved.BlendMode = BlendName(blend.BlendMode);

                    if (choice.Params is { } p)
                    {
                        foreach (var (param, value) in p.Textures)
                            found.Add(value is UTexture texture
                                ? (param, texture, texture.GetPathName(), true)
                                : (param, null, null, false));
                        foreach (var (n, v) in p.Scalars) resolved.Scalars.Add((n, v));
                        foreach (var (n, c) in p.Colors) resolved.Vectors.Add((n, [c.R, c.G, c.B, c.A]));
                        foreach (var (n, b) in p.Switches) resolved.Switches.Add((n, b));
                    }
                    return 0;
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { resolved.Why = $"{e.GetType().Name}: {e.Message}"; }

            if (resolved.Why is not null)
                _missing.Add((path, $"{resolved.Why}; written as a plain material with no textures"));

            // EVERY TEXTURE IS WRITTEN AS THE GAME SHIPS IT (RGBA, alpha kept). The base colour of an OPAQUE material
            // whose alpha is not fully opaque is wired to a second, alpha-free copy (__rgb) instead: a DCC that finds
            // alpha in a base-colour image treats it as the material's opacity. A cut-out material wires the RGBA file.
            resolved.CutOut = cutOut;
            var anyNotTexture = false;
            foreach (var (param, texture, gamePath, isTexture) in found)
            {
                if (!isTexture) { resolved.Textures.Add((param, null)); _notTextures++; anyNotTexture = true; continue; }
                var isBase = baseColor is not null && gamePath == baseColorPath;
                var rgba = WriteTexture(texture!, gamePath!);
                resolved.Textures.Add((param, isBase && !cutOut ? WriteRgbCopy(texture!, rgba) : rgba.File));
            }
            if (anyNotTexture) _notTextureMaterials++;

            // The wired pair. Usually already written as a parameter above; not always (a chooser may
            // hand back a texture that is not among the parameters), so write it if it is not.
            if (baseColor is not null)
            {
                var rgba = WriteTexture(baseColor, baseColorPath ?? baseColor.Name);
                resolved.DiffuseFile = cutOut ? rgba.File : WriteRgbCopy(baseColor, rgba);
            }
            if (normal is not null) resolved.NormalFile = WriteTexture(normal, normalPath ?? normal.Name).File;
            return resolved;
        }

        // ============================================================== textures

        /// <summary>
        /// Write a texture ONCE, however many materials and parameters name it, and return its file
        /// (relative to the export folder) or null when it would not write -- in which case it is named
        /// in Missing, once. Named after the texture ASSET; a different texture with the same asset
        /// name gets __2, __3, ...
        /// </summary>
        private WrittenTexture WriteTexture(UTexture texture, string gamePath)
        {
            if (_written.TryGetValue(gamePath, out var known)) return known;

            var unique = UniqueTextureStem(FileSafe(texture.Name));
            Directory.CreateDirectory(_textureDir);
            var pre = Directory.GetFiles(_textureDir, unique + ".*").Length > 0;
            var (written, error, alphaNotOpaque) = TextureExport.WriteMeasured(texture, _textureDir, unique, ETextureFormat.Png);

            string? relative = null;
            if (written is null) _missing.Add((gamePath, $"the texture was not written: {error}"));
            else
            {
                relative = TexturesFolder + "/" + System.IO.Path.GetFileName(written);
                if (!pre) _createdTextures.Add(written);
            }
            var entry = new WrittenTexture(gamePath, relative, rgbCopy: false, alphaNotOpaque);
            _written.Add(gamePath, entry);
            _writtenOrder.Add(entry);
            return entry;
        }

        /// <summary>
        /// The file an OPAQUE material's base colour is wired to: the texture's RGBA file when its alpha is fully
        /// opaque (nothing to hide), else an alpha-free copy named <c>&lt;name&gt;__rgb</c>, written once however
        /// many materials use it.
        /// </summary>
        private string? WriteRgbCopy(UTexture texture, WrittenTexture rgba)
        {
            if (rgba.File is null || !rgba.AlphaNotOpaque) return rgba.File;
            if (rgba.RgbTried) return rgba.RgbFile ?? rgba.File;
            rgba.RgbTried = true;

            var stem = System.IO.Path.GetFileNameWithoutExtension(rgba.File) + "__rgb";
            var unique = UniqueTextureStem(stem);
            var pre = Directory.GetFiles(_textureDir, unique + ".*").Length > 0;
            var (written, error, _) = TextureExport.WriteMeasured(texture, _textureDir, unique, ETextureFormat.Png, dropAlpha: true);
            if (written is null)
            {
                _missing.Add((rgba.GamePath, $"the alpha-free copy was not written ({error}); the opaque materials keep the RGBA file and may look see-through in a DCC that reads its alpha"));
                return rgba.File;
            }
            var relative = TexturesFolder + "/" + System.IO.Path.GetFileName(written);
            if (!pre) _createdTextures.Add(written);
            var entry = new WrittenTexture(rgba.GamePath, relative, rgbCopy: true, alphaNotOpaque: false) { CopyOf = rgba.File };
            _writtenOrder.Add(entry);
            rgba.RgbFile = relative;
            return relative;
        }

        private string UniqueTextureStem(string stem)
        {
            var unique = stem;
            for (var n = 2; !_textureNames.Add(unique); n++) unique = $"{stem}__{n}";
            return unique;
        }

        private WiredTexture Wire(string file)
        {
            if (_wired.TryGetValue(file, out var known)) return known;
            var gamePath = _writtenOrder.FirstOrDefault(t => t.File == file)?.GamePath ?? "";
            var wired = new WiredTexture(file, gamePath, System.IO.Path.GetFileNameWithoutExtension(file),
                                         _ids.Next(), _ids.Next());
            _wired.Add(file, wired);
            _wiredOrder.Add(wired);
            return wired;
        }

        // ============================================================== node builders

        /// <summary>A group: a Model that is nothing but a place in the outliner.</summary>
        private static Node NullModel(long id, string name)
        {
            var node = new Node("Model").P(id, Named(name, "Model"), "Null");
            node.Add("Version").P(232);
            var p = node.Add("Properties70");
            FbxMeshExport.Prop(p, "DefaultAttributeIndex", "int", "Integer", 0);
            FbxMeshExport.Prop(p, "Lcl Translation", "Lcl Translation", "", 0.0, 0.0, 0.0);
            FbxMeshExport.Prop(p, "Lcl Rotation", "Lcl Rotation", "", 0.0, 0.0, 0.0);
            FbxMeshExport.Prop(p, "Lcl Scaling", "Lcl Scaling", "", 1.0, 1.0, 1.0);
            node.Add("Shading").P(true);
            node.Add("Culling").P("CullingOff");
            return node;
        }

        private static Node NullAttribute(long id, string name)
        {
            var node = new Node("NodeAttribute").P(id, Named(name, "NodeAttribute"), "Null");
            node.Add("TypeFlags").P("Null");
            return node;
        }

        private static Node MaterialNode(long id, string name, bool hasBaseColor, bool hasNormal, bool cutOut)
        {
            var node = new Node("Material").P(id, Named(name, "Material"), "");
            node.Add("Version").P(102);
            node.Add("ShadingModel").P("phong");
            node.Add("MultiLayer").P(0);
            var p = node.Add("Properties70");
            // A textured material is white under its texture, so a viewer that multiplies the two
            // (several do) shows the texture and not the texture darkened to a grey.
            var grey = hasBaseColor ? 1.0 : 0.8;
            FbxMeshExport.Prop(p, "DiffuseColor", "Color", "", grey, grey, grey);
            if (hasNormal) FbxMeshExport.Prop(p, "NormalMap", "Vector3D", "Vector", 0.0, 0.0, 0.0);
            if (cutOut)
            {
                // The material itself is fully opaque (an Opacity of 1, a TransparencyFactor of 0): the
                // texture connected to OpacityProperty is the whole of the cut. TransparentColor is NOT
                // given a value -- its default, black, is "no transparent colour", and a white one reads
                // as fully transparent in importers that take the material's own number.
                FbxMeshExport.Prop(p, "Opacity", "double", "Number", 1.0);
                FbxMeshExport.Prop(p, "TransparencyFactor", "Number", "", 0.0);
            }
            return node;
        }

        // A TEXTURE IS NAMED RELATIVE TO THE FBX, never by its absolute path. The absolute path is the folder the
        // person exported into, usually under their own account's folder, and a level they share would carry it.
        // Blender and 3ds Max both look for a relative name beside the FBX.
        private Node TextureNode(WiredTexture texture)
        {
            var relative = texture.File.Replace('/', '\\');

            var node = new Node("Texture").P(texture.Id, Named(texture.Name, "Texture"), "");
            node.Add("Type").P("TextureVideoClip");
            node.Add("Version").P(202);
            node.Add("TextureName").P(Named(texture.Name, "Texture"));
            var p = node.Add("Properties70");
            FbxMeshExport.Prop(p, "UseMaterial", "bool", "", 1);
            FbxMeshExport.Prop(p, "UseMipMap", "bool", "", 0);
            node.Add("Media").P(Named(texture.Name, "Video"));
            node.Add("FileName").P(relative);
            node.Add("RelativeFilename").P(relative);
            node.Add("ModelUVTranslation").P(0.0, 0.0);
            node.Add("ModelUVScaling").P(1.0, 1.0);
            node.Add("Texture_Alpha_Source").P("None");
            node.Add("Cropping").P(0, 0, 0, 0);

            _links.Add(new Link(texture.VideoId, texture.Id, null));
            return node;
        }

        private Node VideoNode(WiredTexture texture)
        {
            var relative = texture.File.Replace('/', '\\');

            var node = new Node("Video").P(texture.VideoId, Named(texture.Name, "Video"), "Clip");
            node.Add("Type").P("Clip");
            var p = node.Add("Properties70");
            FbxMeshExport.Prop(p, "Path", "KString", "XRefUrl", relative);
            node.Add("UseMipMap").P(0);
            node.Add("Filename").P(relative);
            node.Add("RelativeFilename").P(relative);
            return node;
        }

        // ============================================================== the manifest

        private void WriteManifest(string path, string? maxScript)
        {
            using var file = File.Create(path);
            using var json = new Utf8JsonWriter(file, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            // A float is written AS a float: widened to double, 0.1f prints as 0.10000000149011612. A value that is not a
            // number (NaN, infinity) cannot be written as JSON: it is null, and the material says which ones in "nonFinite".
            var nonFinite = new List<string>();
            void Number(float v, string what)
            {
                if (float.IsFinite(v)) json.WriteNumberValue(v); else { json.WriteNullValue(); nonFinite.Add(what); }
            }
            void Text(string name, string? value)
            {
                if (value is null) json.WriteNull(name); else json.WriteString(name, value);
            }

            json.WriteStartObject();
            json.WriteNumber("format", 4);   // 4: per texture file rgbCopy / copyOf, normalMaps. 3: maxScript. 2: per material blendMode / cutOut / opacity
            json.WriteString("fbx", _name + ".fbx");
            Text("maxScript", maxScript);    // the 3ds Max script beside the FBX that switches the cut-outs on; null when there is none
            json.WriteString("normalMaps", "DirectX-style (green channel down), as the game ships them");
            json.WriteString("units", "centimetres");
            json.WriteString("space", "right-handed, Z up: the game's Y is negated");

            json.WriteStartArray("materials");
            foreach (var m in _manifest)
            {
                json.WriteStartObject();
                json.WriteString("path", m.Rec.Path);
                json.WriteString("fbxName", m.FbxName);
                Text("note", m.Resolved.Why);
                json.WriteString("blendMode", m.Resolved.BlendMode);

                // THE OPACITY CHOICE. A cut-out material names the file whose alpha channel is its opacity and
                // the FBX property it is connected to; an opaque one says so, and its base colour is the RGBA file or, where that has alpha, its alpha-free __rgb copy.
                json.WriteBoolean("cutOut", m.Resolved.CutOut);
                if (m.Resolved.CutOut)
                {
                    json.WriteStartObject("opacity");
                    json.WriteString("source", "base colour texture alpha");
                    Text("file", m.Resolved.DiffuseFile);
                    json.WriteString("property", OpacityProperty);
                    json.WriteEndObject();
                }
                else json.WriteNull("opacity");

                json.WriteStartObject("textures");
                foreach (var (param, file2) in m.Resolved.Textures) Text(param, file2);
                json.WriteEndObject();

                json.WriteStartObject("wired");
                Text("DiffuseColor", m.Resolved.DiffuseFile);
                Text("NormalMap", m.Resolved.NormalFile);
                json.WriteEndObject();

                json.WriteStartObject("scalars");
                nonFinite.Clear();
                foreach (var (n, v) in m.Resolved.Scalars) { json.WritePropertyName(n); Number(v, n); }
                json.WriteEndObject();

                json.WriteStartObject("vectors");
                foreach (var (n, v) in m.Resolved.Vectors)
                {
                    json.WriteStartArray(n);
                    for (var k = 0; k < v.Length; k++) Number(v[k], $"{n}[{k}]");
                    json.WriteEndArray();
                }
                json.WriteEndObject();

                if (nonFinite.Count > 0)
                {
                    json.WriteString("nonFiniteNote", "these values were not finite numbers and are written as null");
                    json.WriteStartArray("nonFinite");
                    foreach (var what in nonFinite) json.WriteStringValue(what);
                    json.WriteEndArray();
                }

                json.WriteStartObject("switches");
                foreach (var (n, v) in m.Resolved.Switches) json.WriteBoolean(n, v);
                json.WriteEndObject();

                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("textureFiles");
            foreach (var t in _writtenOrder)
            {
                json.WriteStartObject();
                Text("file", t.File);
                json.WriteString("gamePath", t.GamePath);
                json.WriteBoolean("rgbCopy", t.RgbCopy);   // true: the alpha-free copy of copyOf, wired to opaque materials' base colour
                Text("copyOf", t.CopyOf);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("missing");
            foreach (var (what, why) in _missing)
            {
                json.WriteStartObject();
                json.WriteString("what", what);
                json.WriteString("why", why);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
            json.Flush();
        }

        // ============================================================== names

        private static string BlendName(EBlendMode mode) => mode.ToString().Replace("BLEND_", "", StringComparison.Ordinal);

        private static string GroupName(string? sublevel) => string.IsNullOrWhiteSpace(sublevel) ? "Level" : sublevel;

        /// <summary>The asset's own name: after the last dot (an object path), else after the last slash.</summary>
        private static string LeafOf(string path)
        {
            var dot = path.LastIndexOf('.');
            var slash = path.LastIndexOf('/');
            var leaf = dot > slash ? path[(dot + 1)..] : path[(slash + 1)..];
            return leaf.Length > 0 ? leaf : "unnamed";
        }

        /// <summary>Control characters have no place in a node name (a NUL would end it); everything else stays.</summary>
        private static string Clean(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(c < ' ' ? '_' : c);
            return sb.Length > 0 ? sb.ToString() : "unnamed";
        }

        private static string FileSafe(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "texture";
        }

        /// <summary>A name no other object in the file has: the second Wall is Wall_2, the third Wall_3.</summary>
        private string UniqueObjectName(string wanted)
        {
            if (_objectNames.Add(wanted)) return wanted;
            var n = _serial.GetValueOrDefault(wanted, 1);
            string candidate;
            do { n++; candidate = $"{wanted}_{n}"; } while (!_objectNames.Add(candidate));
            _serial[wanted] = n;
            return candidate;
        }

        private string UniqueMaterialName(string wanted)
        {
            if (_materialNames.Add(wanted)) return wanted;
            string candidate;
            for (var n = 2; !_materialNames.Add(candidate = $"{wanted}_{n}"); n++) { }
            return candidate;
        }

        // ============================================================== cleanup

        /// <summary>Deletes the file if it is there; false when it is there and could not be deleted.</summary>
        private static bool TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void TryRemoveEmptyDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

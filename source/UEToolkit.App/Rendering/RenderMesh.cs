using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using Silk.NET.OpenGL;
using UEToolkit.Core;

namespace UEToolkit.App.Rendering;

public sealed class RenderSection
{
    public int FirstIndex;
    public int IndexCount;
    public string MaterialName = "";

    /// <summary>Slot this section's material was swapped out of, when a variant overrode it.</summary>
    public string? OverrodeMaterial;

    /// <summary>
    /// The material's own TwoSided flag, which decides whether back faces are drawn.
    ///
    /// Characters are often built from hollow single-sided shells — a collar is a tube, a torso is
    /// an open shell — and the ones you can see inside of are marked two-sided. Culling those
    /// anyway shows the BACKGROUND through the model: a neck read as a hole with a
    /// third of its pixels exactly the clear colour, and drawing back faces filled it to zero.
    /// </summary>
    public bool TwoSided;

    public UMaterialInterface? Material;

    /// <summary>
    /// Where <see cref="Material"/> came from, so it can be LET GO of and fetched again.
    ///
    /// A loaded UE material is a few hundred bytes and roots several megabytes: its texture
    /// parameters point into the package that declared them, and an IoPackage holds a
    /// `Lazy&lt;IoPackage[]&gt;` of its imports whose factory closure captures the FAssetArchive --
    /// which owns the whole package file as a byte[]. Measured on a map with 324 meshes: 436 byte
    /// arrays of 4-10 MB each, 4.26 GB, every one of them rooted through a RenderSection.
    ///
    /// The path is the part worth keeping. Whoever dresses the section can release the object and
    /// load it again from here if the shading mode changes.
    /// </summary>
    public string MaterialPath = "";

    /// <summary>
    /// The root parent material -- the FAMILY this section's shading belongs to.
    ///
    /// Recorded because ranking shader work by how many material INSTANCES a family has is the
    /// wrong metric, and it cost a whole feature to learn: MM_Rocks is third by instance count
    /// with 396, and not one map in the game places a CaveSpire, so its projection branch draws
    /// nothing at all. Instance count is not screen presence. What matters is how many SECTIONS
    /// DRAWN IN A LEVEL wear a family, which only the viewport can say.
    /// </summary>
    public string Family = "";

    /// <summary>
    /// WHICH MATERIAL SLOT OF THE MESH THIS SECTION USES, which is NOT its position in the list.
    ///
    /// `OverrideMaterials[i]` on a component replaces slot `i`, so a per-placement override has
    /// to be looked up by this and not by the loop counter. Two sections can share a slot and a
    /// mesh can leave a slot unused, so the two orders are only accidentally the same -- and
    /// getting it wrong would paint the right material onto the wrong part of the model, which is
    /// precisely the bug the override support exists to fix.
    /// </summary>
    public int MaterialIndex = -1;

    public uint Diffuse, NormalMap, Orme;   // 0 = not bound

    /// <summary>
    /// The detail normal and its two scalars, from `MM_Tiled_RGB`'s `Use Detail Normal?` branch.
    /// Tile 1 and intensity 1 are the parent's own defaults, so an unset section is a no-op.
    /// </summary>
    public uint DetailNormal;
    public float DetailTile = 1f, DetailIntensity = 1f;

    /// <summary>
    /// The three UV-quadrant tints and their strengths. White at strength 0 is a no-op, which is
    /// what an unset section must be.
    /// </summary>
    public Vector3 UvTintPN = Vector3.One, UvTintNP = Vector3.One, UvTintNN = Vector3.One;
    public Vector3 UvTintStrength = Vector3.Zero;
    public bool UvTint;

    /// <summary>
    /// MM_Rocks' world-axis projection: the colour laid on top, its normal, and the terms that
    /// decide where and how hard. `ProjAxis` is stored in UE space and converted at draw time.
    /// </summary>
    public uint ProjColor, ProjNrm;
    public Vector3 ProjAxis = Vector3.UnitZ, ProjTint = Vector3.One;
    public float ProjSize = 512f, ProjIntensity = 1f, ProjNrmIntensity = 1f;
    public float ProjSlopeMin = 0.2f, ProjSlopeMax = 0.4f;
    public Vector2 ProjRough = new(0f, 1f);

    /// <summary>The break-up mask, its own projection size, and its contrast. 0 contrast is identity.</summary>
    public uint ProjAlpha;
    public float ProjAlphaSize = 700f, ProjAlphaContrast;

    /// <summary>
    /// Discard fragments whose diffuse alpha falls below the clip value. True for a Masked or
    /// Translucent material, or one whose `Use BaseColor Texture Alpha` says the alpha is the
    /// shape -- which is what every decal in this game is.
    /// </summary>
    public bool AlphaCut;

    /// <summary>
    /// Does the bound mask carry OCCLUSION in its red channel?
    ///
    /// True for a real ORME map, which is what the name means -- Occlusion, Roughness, Metallic,
    /// Emissive -- and so the default, because that is what every game bound here before this
    /// existed. FALSE for a mask that packs only roughness and metallic, where red is unused.
    ///
    /// IT MATTERS BECAUSE ZERO IS A VALID OCCLUSION VALUE. Zero Company's `Base Mask 1 VT`
    /// measures R=0 across the whole texture (mean 0, max 0, on a 4096 torso mask) while green
    /// carries every panel and wear detail and blue a sparse metallic. Read as an ORME, its red
    /// says "fully occluded" and the ambient term `0.16 * ao` goes to nothing -- so binding the
    /// mask would have DARKENED every model rather than improving it.
    ///
    /// ao = 1 IS THE SAFE FAILURE. If this flag is wrong about a material, the cost is a missing
    /// subtle occlusion; the other way round the cost is a black model. So it is set false on
    /// the evidence of the name that matched, and the log says which name that was.
    /// </summary>
    public bool MaskHasAo = true;

    /// <summary>
    /// A decal's shape, packed into one channel of a shared mask atlas. `DecalChannel` is the
    /// selector -- dot it with the sampled texel and the right channel falls out.
    /// </summary>
    public uint DecalMask;
    public Vector4 DecalChannel = Vector4.Zero;
    public float DecalMaskPower = 1f, DecalMaskStrength = 1f, DecalAlpha = 1f;
    public Vector3 DecalTint = Vector3.One;
    public float DecalTintStrength;

    /// <summary>Fuzz, and the one parameter the material exposes for it.</summary>
    public bool Fuzz;
    public Vector3 FuzzColor = Vector3.Zero;

    /// <summary>The emissive sheet, its mask, and the colour and strength that scale them.</summary>
    public uint EmSheet, EmMask;
    public Vector3 EmSheetColor = Vector3.Zero;
    public float EmSheetTile = 1f, EmSheetStrength = 1f;

    /// <summary>
    /// The material's own light, and the colour it is multiplied by. 0 = this section does not glow.
    ///
    /// COUNTED BEFORE IT WAS EVER BOUND. Umbara: 3,046 sections, 1,059 carrying a real emissive
    /// texture and 988 resolving only a `Default*` PLACEHOLDER -- so a binder that trusted whatever
    /// resolved would have put a stand-in texture on a third of the level and lit it uniformly.
    /// That is why <see cref="TextureCache.IsPlaceholder"/> gates this.
    /// </summary>
    public uint Emissive;
    public Vector3 EmissiveTint = Vector3.One;

    /// <summary>
    /// This instance's own `Emissive Strength`, relative to the value most of its family chose,
    /// so the author's global slider keeps meaning what it meant. 0 where the material asks for
    /// no glow at all.
    /// </summary>
    public float EmissiveScale = 1f;

    /// <summary>
    /// The glow is a flat authored colour with no map behind it -- most of MM_Emissive. Replaces
    /// the surface term rather than adding to it, because the family carries no base-colour
    /// artwork for that term to be shading.
    /// </summary>
    public bool EmissiveFlat;
    public bool Visible = true;

    /// <summary>
    /// The "Tint Mask VT" this section's own material carries, by texture name. Recorded, and
    /// deliberately NOT rendered through.
    ///
    /// Every material in this game has one, and a customization fragment only overrides it for the
    /// SLOTS IT NAMES -- which is why a commando's bodysuit stays uncoloured under a torso the
    /// player has painted, and why painting one mask over every section was wrong. What the game
    /// does with a material's OWN mask cannot be reproduced here: the wiring from mask channel to
    /// "Color NN" lives in the material graph, which a cooked instance does not expose, and
    /// T_Clo010_SUIT_Tint is 100% in all three channels against colours that are all white. Run
    /// through this shader's doubling that is a pure white bodysuit -- confidently wrong, where
    /// leaving the albedo alone is merely incomplete. So a section no fragment names renders as
    /// itself.
    /// </summary>
    public string TintMaskName = "";

    /// <summary>
    /// The uploaded tint mask, or 0. R, G and B select Colour 01, 02 and 03.
    ///
    /// THE CHANNEL WIRING IS NOT A GUESS. The game ships `T_DefaultTintMSK` with the words
    /// "COLOR 1", "COLOR 2" and "COLOR 3" painted into its red, green and blue channels — the
    /// mask documenting its own meaning — and the fragment shader has always read it that way.
    /// </summary>
    public uint TintMask;

    /// <summary>
    /// WHETHER THIS MATERIAL TINTS ITSELF, from its own `Use Tint` switch.
    ///
    /// THIS IS THE PHASE 2 CLONE VISOR. The author: "you have the visor completely white when it
    /// should be black by default not reading the textures properly". It is not the textures.
    /// MI_Clo001_HELM carries its own colours and one of them is the visor:
    ///
    ///     Use Tint   True          Tint Mask VT  T_Clo001_HELM_Tint
    ///     Color 01   BCB8B3        the shell
    ///     Color 02   BCB8B3
    ///     Color 03   000000        THE VISOR
    ///
    /// The mask's blue channel marks the visor and Color 03 paints it black. The viewport applied
    /// a tint only when somebody CHOSE one in the picker, so with nothing chosen the visor kept
    /// the albedo's pale grey. "By default" was the whole of the report.
    /// </summary>
    public bool TintOwn;

    /// <summary>
    /// The material's own Color 01, 02 and 03, for the mask's R, G and B.
    ///
    /// READ RAW, not gamma encoded. These are stored as the numbers the artist typed (the props
    /// dump prints Color 01 as BCB8B3 with no conversion applied), which is the same space the
    /// swatch palette feeds this uniform from. Converting would brighten the shell against a
    /// picked colour that means the same thing. The VISOR is 000000 in either space, so the
    /// reported fault is fixed either way and only the shell's shade rides on this reading.
    ///
    /// `Color 0N Intensity` is deliberately NOT applied. All three read 1 here so it would change
    /// nothing, and the obvious reading -- multiply -- turns an intensity of 0 into a colour of
    /// BLACK rather than "do not tint this channel", which is a regression that would look like
    /// detail. Incomplete beats confidently wrong.
    /// </summary>
    public Vector3 TintC1 = Vector3.One, TintC2 = Vector3.One, TintC3 = Vector3.One;


    /// <summary>
    /// THE RGB TRIM SHEET, and the four colours the material lays through it.
    ///
    /// A DIFFERENT MECHANISM from <see cref="TintMaskName"/>, which is recorded and deliberately
    /// not rendered. That one cannot be reproduced because the mask-channel-to-"Color NN" wiring
    /// lives in the material graph; THIS one names its colours outright -- `R Color`, `G Color`,
    /// `B Color`, `A Color` -- so the parameter name IS the channel and nothing is inferred.
    ///
    /// The hub's hull wears it, and `Use tint only? (No DIF)` says the diffuse is not the surface
    /// colour at all. We were binding UNI_HulkMetalPaintedA_DIF and drawing it: loaded correctly,
    /// and not what the game shows.
    /// </summary>
    public uint TrimMask;
    public Vector3 TrimBase = Vector3.One, TrimR = Vector3.One, TrimG = Vector3.One,
                   TrimB = Vector3.One, TrimA = Vector3.One;
    public bool TrimUseA, TrimUv1;

    /// <summary>
    /// WHETHER THE TRIM COLOURS REPLACE THE DIFFUSE. Forced FALSE; see TextureCache.BindTrim.
    ///
    /// `Use tint only? (No DIF)` is true on every instance measured, and every one of those also
    /// ships `Tint Color` = FEFEFE with `Tint Brightness` 1.25. Read literally that paints a
    /// pure white ground under the mask, which put a shipping container's trim sections
    /// white-on-white against painted panels that are obviously not white. A white colour scaled
    /// by a brightness above one is a MULTIPLIER; what the switch means inside the graph is not
    /// something a cooked instance says, so the conservative reading wins: tint what the diffuse
    /// gave us, never replace it. `TrimNoDifSaid` keeps what the material actually claimed.
    /// </summary>
    public bool TrimNoDif;
    public bool TrimNoDifSaid;

    /// <summary>`R/G/B/A Channel Color Strength`, `RGB Tile` and `Tint Brightness`.</summary>
    public Vector4 TrimStrength = Vector4.One;
    public float TrimTile = 1f, TrimBright = 1f;

    /// <summary>
    /// `R/G/B/A Channel Roughness` and their strengths, plus `Roughness Min/Max`.
    ///
    /// Where the panel seams are. R's colour is within a few per cent of the grey it sits on and
    /// its roughness is 0.806 at strength 2.0 -- a gloss break rather than a tint, which no
    /// amount of getting the colour blend right would ever have shown.
    /// </summary>
    public Vector4 TrimRough = new(0.6f, 0.6f, 0.6f, 0.6f), TrimRoughStrength = Vector4.One;
    public Vector2 TrimRoughRemap = new(0f, 1f);

    /// <summary>
    /// `R/G/B/A Channel Metallic`, `RGB Metallic Strength` and `Metallic Min/Max`.
    ///
    /// 0/1/0/0 on this family: the RUST is the metal. That is the right way round -- oxidised
    /// steel showing through paint, and paint does not conduct -- and it is what makes a
    /// highlight keep the surface's colour instead of going grey.
    /// </summary>
    public Vector4 TrimMetal;
    public float TrimMetalStrength = 1f;
    public Vector2 TrimMetalRemap = new(0f, 1f);

    /// <summary>`Metallic Strength`, read for the record and not applied -- see the shader.</summary>
    public float TrimMetalBaseSaid = 1f;

    /// <summary>(cols, rows, vMinTile, enabled) — UDIM addressing, resolved per section.</summary>
    public Vector4 Udim = new(1, 1, 0, 0);
    public string UvMode = "direct";

    /// <summary>
    /// UV bounds of THIS section's own vertices. Mesh-wide bounds cannot describe a
    /// multi-section mesh, and a character part is itself only a slice of the atlas —
    /// deciding the tile grid from either one is what produced stretched textures.
    /// </summary>
    public float UMin, UMax, VMin, VMax;

    /// <summary>Pixel size of the bound diffuse, which is what the tile grid is derived from.</summary>
    public int TexW, TexH;

    /// <summary>
    /// Object path of the bound diffuse. The key into <see cref="UEToolkit.Core.AtlasLibrary"/>,
    /// and the reason it is a PATH and not the material or texture NAME: six different "lambert1"
    /// materials ship in this game, and pooling by name merges atlases that share nothing.
    /// </summary>
    public string DiffusePath = "";

    /// <summary>
    /// EVERY GL TEXTURE THIS SECTION POINTS AT, found by what the fields ARE: each public <c>uint</c> field of this
    /// class is a texture handle (0 = none) and nothing else here is a uint. Read from the class rather than listed,
    /// so a texture slot added tomorrow is counted as live by the release in <see cref="TextureCache.ReleaseUnused"/>
    /// without anybody remembering to add it to a list -- a forgotten slot there would delete a texture that is
    /// still being drawn.
    /// </summary>
    public void AddTextureHandles(HashSet<uint> into)
    {
        foreach (var field in HandleFields)
            if ((uint) field.GetValue(this)! is var handle && handle != 0) into.Add(handle);
    }

    private static readonly System.Reflection.FieldInfo[] HandleFields = typeof(RenderSection)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(f => f.FieldType == typeof(uint)).ToArray();
}

/// <summary>
/// A CUE4Parse mesh uploaded to the GPU. Positions come through unscaled — UE units are
/// centimetres, and the camera frames from the bounds, so no conversion is needed.
/// </summary>
public sealed class RenderMesh : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ebo;

    public readonly List<RenderSection> Sections = new();
    public Vector3 BoundsMin, BoundsMax;

    /// <summary>Which LOD this was built from.</summary>
    public int LodIndex { get; init; }

    /// <summary>
    /// The LOD indices a picker may offer — those that ship geometry and are not skipped.
    ///
    /// The LIST, not a count, because the indices are not always 0..n-1: a skipped level leaves a
    /// hole, and a picker built from a count would offer a level that falls straight back to the
    /// default while appearing to have been applied.
    /// </summary>
    public IReadOnlyList<int> LodsOffered { get; init; } = [];

    /// <summary>Matches Viewport.CullBackFaces; see the note there for why it defaults off.</summary>
    public static bool CullBackFaces;

    /// <summary>
    /// Whether the NORMAL and ORM maps are used, as opposed to base colour alone.
    ///
    /// Blender's "material preview" against its "rendered": the same textures, but the cheap one
    /// shows what something is and the full one shows what it looks like. A flag rather than a
    /// second shader because the maps are already bound -- what changes is whether the fragment
    /// shader is told about them.
    /// </summary>
    public static bool SurfaceMaps = true;

    /// <summary>
    /// Normal mapping, on by default -- but switchable, because it has never been on before and
    /// a renderer change that cannot be turned off is a renderer change that cannot be compared.
    /// `--nonormals` in either app.
    /// </summary>
    public static bool NormalMapping = true;

    /// <summary>How hard the tangent-space slopes push. 1 is the map as authored.</summary>
    public static float NormalStrength = 1f;

    /// <summary>Asset name, for diagnostics — the renderer itself never needs it.</summary>
    public string Label = "";

    /// <summary>
    /// Centre of the mesh in UE space. Attachments come in two flavours and only this tells
    /// them apart: helmets and hats authored around the ORIGIN need the attach bone's full
    /// model transform, while heads authored in CHARACTER space are already at head height
    /// and need only the bone's movement away from its bind pose.
    /// </summary>
    public Vector3 CentreUe;
    public int VertexCount { get; }
    public int TriangleCount { get; }
    public int BoneCount { get; init; }
    public Skeleton? Skeleton { get; init; }
    public bool HasSkin { get; private set; }

    /// <summary>Sockets exposed by this mesh: name -> (bone, relative transform).</summary>
    public readonly Dictionary<string, (string Bone, Matrix4x4 Local)> Sockets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set on static attachments: which bone to follow, and the offset from it.</summary>
    public int AttachBone = -1;
    /// <summary>
    /// Which rig the attach bone belongs to. Most helmets ride the body rig, but accessories
    /// authored against a separate FACE rig cannot all resolve against the body.
    /// </summary>
    public Skeleton? AttachHost;

    /// <summary>
    /// True when AttachLocal came from a real socket. A socket already states the exact offset
    /// and rotation the game uses, so the part rides the bone's full frame; only the guessed
    /// bare-bone fallback needs to reason about where the mesh was authored.
    /// </summary>
    public bool AttachedViaSocket;
    public Matrix4x4 AttachLocal = Matrix4x4.Identity;

    /// <summary>pos3 + normal3 + uv2 + boneIndex4 + boneWeight4</summary>
    /// <summary>
    /// Floats per vertex: pos3 normal3 uv2 boneIdx4 weight4 uv1-2.
    ///
    /// UV1 IS HERE BECAUSE A MATERIAL CAN PUT ITS MASK ON THE SECOND SET. The hub's hull wears
    /// the `*_RGBTrim*` family, whose `RGBMaskUV1` switch is true and whose meshes really do ship
    /// two UV sets -- measured on SM_Hub_Hull_Roof_00. `UvChannel`/`ExtraUv` below can only
    /// SUBSTITUTE one channel for the other, which cannot serve a shader that needs the albedo's
    /// UV and the mask's UV in the same fragment.
    ///
    /// EVERY PACKING SITE AND THE ATTRIBUTE POINTERS MOVE TOGETHER. The stride is derived from
    /// this constant, so a site that forgets to fill its two new slots does not misalign anything
    /// -- it writes UV1 (0,0) and the mask samples one corner. Silent, and grey rather than
    /// wrong-looking, which is why each site sets it explicitly even when it has nothing better.
    /// </summary>
    private const int Floats = 18;

    /// <summary>
    /// UE (Z-up, LEFT-handed) to GL (Y-up, RIGHT-handed): (x,y,z) -> (x,z,y).
    ///
    /// The determinant MUST be negative. This previously mapped to (x,z,-y), which is a pure
    /// rotation (det +1) and therefore preserves handedness -- left-handed geometry drawn by a
    /// right-handed renderer comes out MIRRORED, which is subtle enough to survive a long time
    /// on a roughly symmetric character. Swapping two axes instead of negating one gives
    /// det -1 and the correct chirality.
    ///
    /// Because this now genuinely flips handedness, it also reverses triangle winding, so the
    /// index buffer is left alone; it used to be swapped to compensate for a flip that was
    /// not actually happening.
    ///
    /// Row-vector convention to match System.Numerics; applied as the model matrix after
    /// skinning.
    /// </summary>
    public static readonly Matrix4x4 UeToGl = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    // ---- WHAT THE GPU HOLDS FOR MESHES, counted at the only two places it changes. Process-wide
    // statics, because the Level Editor's memory report asks "how many buffers are alive and how big" and
    // the answer must not depend on which viewport made them. Interlocked: a mesh can be made on a worker.
    private static long _liveMeshes, _liveVertexBytes, _liveIndexBytes, _madeMeshes, _disposedMeshes;
    private readonly long _vertexBytes, _indexBytes;
    private bool _disposed;

    /// <summary>Vertex buffers and index buffers alive right now: how many, and how many bytes were handed to the driver.</summary>
    public static (long Meshes, long VertexBytes, long IndexBytes, long Made, long Disposed) LiveBuffers =>
        (Interlocked.Read(ref _liveMeshes), Interlocked.Read(ref _liveVertexBytes), Interlocked.Read(ref _liveIndexBytes),
         Interlocked.Read(ref _madeMeshes), Interlocked.Read(ref _disposedMeshes));

    private unsafe RenderMesh(GL gl, float[] vertices, uint[] indices, int vertexCount)
    {
        _gl = gl;
        VertexCount = vertexCount;
        TriangleCount = indices.Length / 3;
        _vertexBytes = (long) vertices.Length * sizeof(float);
        _indexBytes = (long) indices.Length * sizeof(uint);

        // A throw part way must not leave the names it already made alive, uncounted: they are deleted here, since a
        // constructor that throws never becomes an object that can be disposed.
        try
        {
            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);

            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* v = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint) (vertices.Length * sizeof(float)),
                    v, BufferUsageARB.StaticDraw);

            _ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            fixed (uint* i = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint) (indices.Length * sizeof(uint)),
                    i, BufferUsageARB.StaticDraw);

            const uint stride = Floats * sizeof(float);   // pos3 normal3 uv2 boneIdx4 weight4 uv1-2
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*) 0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*) (3 * sizeof(float)));
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*) (6 * sizeof(float)));
            _gl.EnableVertexAttribArray(3);
            _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*) (8 * sizeof(float)));
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*) (12 * sizeof(float)));
            _gl.EnableVertexAttribArray(5);
            _gl.VertexAttribPointer(5, 2, VertexAttribPointerType.Float, false, stride, (void*) (16 * sizeof(float)));

            _gl.BindVertexArray(0);
        }
        catch
        {
            try { _gl.BindVertexArray(0); } catch { /* the original failure is the one to report */ }
            try
            {
                if (_ebo != 0) _gl.DeleteBuffer(_ebo);
                if (_vbo != 0) _gl.DeleteBuffer(_vbo);
                if (_vao != 0) _gl.DeleteVertexArray(_vao);
            }
            catch { /* no context to delete in: nothing more can be done */ }
            throw;
        }

        // COUNTED LAST: a constructor that throws above never becomes an object that can be disposed, so counting
        // before the GL calls left "alive" one too high for good.
        Interlocked.Increment(ref _liveMeshes);
        Interlocked.Increment(ref _madeMeshes);
        Interlocked.Add(ref _liveVertexBytes, _vertexBytes);
        Interlocked.Add(ref _liveIndexBytes, _indexBytes);
    }

    /// <summary>Which UV channel to use. Some materials key off UV1, not UV0.</summary>
    /// <summary>
    /// WHICH UV SET THE TRIM MASK IS SAMPLED ON, forced, for the experiment that tells two
    /// hypotheses apart. -1 honours the material's `RGBMaskUV1`; 0 or 1 overrides it.
    ///
    /// The first run honoured the material and produced per-triangle speckle across the hangar
    /// floor -- the signature of sampling a shared 4,096px sheet through a LIGHTMAP atlas, which
    /// is what UE usually keeps in UV1. The competing reading is that the game counts UV sets
    /// from one, so its "UV1" is our UV0. A switch settles it in one run each; reasoning about
    /// which convention a cooked material means does not.
    /// </summary>
    public static int TrimUvForce = -1;

    /// <summary>
    /// How many meshes arrived WITH a usable second UV set, and how many without.
    ///
    /// The number that turns "UV0 and UV1 render identically" from a puzzle into a fact: UV1
    /// falls back to UV0 per vertex, so a null ExtraUV makes the two bit-equal and every
    /// experiment on which set to sample comes back unchanged. --meshinfo says these meshes have
    /// two sets; this says whether the CONVERTER handed us the second one.
    /// </summary>
    public static int MeshesWithUv1, MeshesWithoutUv1;

    /// <summary>
    /// OFF BY DEFAULT, and staying off until the compositing is right. `--trim` turns it on.
    ///
    /// The plumbing is sound and measured: 455 of HUB_Hangar's 642 sections name an RGB trim
    /// sheet, the sheets decode fully at 4096x4096, and UV1 reaches the shader for 302 of 437
    /// meshes. What is NOT settled is how the four colours composite, and switched on with the
    /// obvious reading it makes the picture worse, not better -- it lays `B Color` (252527, near
    /// black) wherever the blue channel is non-zero and speckles the hangar floor.
    ///
    /// A REGRESSION THAT LOOKS LIKE DETAIL IS THE WORST KIND. Black flecks on painted metal read
    /// as wear, so this would have been believed rather than reported -- the same reason
    /// RenderSection.TintMask stays unwired. Incomplete beats confidently wrong.
    /// </summary>
    public static bool TrimEnabled;

    /// <summary>1 draws the sampled mask's RGB, 2 its alpha. See the shader.</summary>
    public static int TrimDebug;

    /// <summary>Channel layering order for the RGB trim sheet: 0 = R,G,B (ours); 1 = B,G,R.</summary>
    public static int TrimOrder;

    /// <summary>
    /// WHAT THE TRIM COLOURS ARE LAID OVER: -1 honours `Use tint only? (No DIF)`, 0 forces the
    /// DIFFUSE, 1 forces the material's `Tint Color`.
    ///
    /// Open question, and a switch rather than a guess. Honouring the material puts `Tint Color`
    /// underneath, and on these instances that reads FEFEFE -- white -- so wherever the sheet is
    /// black the surface came out flat white. But the sheets ARE mostly black in the sampled
    /// region (measured: 77.5% of the frame on RGB, 60.5% on alpha) against a mask that decodes
    /// perfectly at 4096x4096. A sheet that sparse marks WHERE trim goes and leaves the rest to
    /// something else, and the only candidate for that something is the diffuse we already draw.
    /// </summary>
    public static int TrimBaseForce = -1;

    public static int UvChannel = 0;
    public static int LastNumTexCoords { get; private set; }
    public static string LastUvReport { get; private set; } = "";

    /// <summary>
    /// Some games ship UDIM-style atlases: UV0 spans several tiles (U 0..8, V -1..1) rather than
    /// 0..1, and the cooked texture is those tiles flattened into one image. Normalise into
    /// the flattened image's 0..1 space.
    /// </summary>
    public static bool FlipV = true;
    public static bool UdimEnabled = true;

    // Derived per mesh from the actual UV bounds, so this generalises instead of assuming
    // one fixed 8x2 layout.
    private static float _uOrigin, _vOrigin, _uSpan = 1f, _vSpan = 1f;

    /// <summary>
    /// Walk every vertex twice more to measure UVs. True for the single-mesh inspector, which is
    /// what those numbers are for; false for anything loading a level's worth of geometry.
    /// </summary>
    public static bool MeasureUvs = true;

    private static void DetectUdim(CMeshVertex[] verts, FMeshUVFloat[]? over)
    {
        float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
        for (var i = 0; i < verts.Length; i++)
        {
            var uv = over is not null && i < over.Length ? over[i] : verts[i].UV;
            if (float.IsNaN(uv.U) || float.IsNaN(uv.V)) continue;
            uMin = MathF.Min(uMin, uv.U); uMax = MathF.Max(uMax, uv.U);
            vMin = MathF.Min(vMin, uv.V); vMax = MathF.Max(vMax, uv.V);
        }

        _uOrigin = MathF.Floor(uMin);
        _vOrigin = MathF.Floor(vMin);
        // Round up to whole tiles, with tolerance: islands bleed a fraction past the tile
        // edge (V reaches 1.008 here), which would otherwise round up to an extra row.
        const float slack = 0.05f;
        _uSpan = MathF.Max(1f, MathF.Ceiling(uMax - _uOrigin - slack));
        _vSpan = MathF.Max(1f, MathF.Ceiling(vMax - _vOrigin - slack));
        UMin = uMin; UMax = uMax; VMin = vMin; VMax = vMax;
        LastUdim = $"{_uSpan:0}x{_vSpan:0}";
    }

    /// <summary>
    /// How the last FBX preview's bones landed on the game's rig, in the author's words.
    ///
    /// This is the one number that says whether the mod will animate: a file whose bones are
    /// named for a different rig imports, renders, and then does not move -- and nothing else on
    /// screen distinguishes that from an animation that is simply still.
    /// </summary>
    public static string LastFbxSkinReport { get; private set; } = "";

    /// <summary>
    /// Bones the last FBX preview used that the rig does not have.
    ///
    /// Beside the sentence rather than read out of it: a caller deciding whether to colour that
    /// line as a warning should not be matching on its wording, which is the sort of coupling
    /// that survives every refactor except the one that breaks it.
    /// </summary>
    public static int LastFbxSkinMissing { get; private set; }

    public static string LastUdim { get; private set; } = "";
    public static bool LastWasNanite { get; private set; }
    public static string LastConvertReport { get; private set; } = "";
    public static float UMin { get; private set; }
    public static float UMax { get; private set; }
    public static float VMin { get; private set; }
    public static float VMax { get; private set; }
    /// <summary>Raw (untransformed) UVs and indices, for the UV-overlay debug dump.</summary>
    public static float[] LastRawUvs { get; private set; } = [];
    public static uint[] LastIndices { get; private set; } = [];

    public static float UOrigin => _uOrigin;
    public static float VOrigin => _vOrigin;
    public static float USpan => _uSpan;
    public static float VSpan => _vSpan;

    /// <summary>
    /// WHICH LOD TO BUILD, and which ones a picker may offer.
    ///
    /// <paramref name="want"/> of -1 means "the one this renderer has always chosen" — the first
    /// that is not skipped — so every existing caller keeps its behaviour to the letter. A real
    /// index picks that LOD, and an index that is missing or skipped falls back to the default
    /// rather than drawing nothing, because a picker offering a level the asset does not ship
    /// should show the model it does have.
    ///
    /// A LOD with no Verts is not offerable. Nanite meshes are the case that matters: they ship
    /// no LOD chain at all, so the list is one entry and the picker says so honestly instead of
    /// implying a choice.
    /// </summary>
    private static (T Lod, int Index, List<int> Offerable)? PickLod<T>(
        IReadOnlyList<T> lods, int want, Func<T, bool> skipped, Func<T, bool> hasVerts)
    {
        if (lods.Count == 0) return null;

        var offerable = new List<int>();
        for (var i = 0; i < lods.Count; i++)
            if (hasVerts(lods[i]) && !skipped(lods[i])) offerable.Add(i);

        var index = want >= 0 && offerable.Contains(want)
            ? want
            : offerable.Count > 0 ? offerable[0] : 0;

        return (lods[index], index, offerable);
    }

    public static RenderMesh? FromSkeletal(GL gl, USkeletalMesh mesh, int lodIndex = -1)
    {
        if (!mesh.TryConvert(out var converted) || converted.LODs.Count == 0) return null;
        if (PickLod(converted.LODs, lodIndex, l => l.SkipLod, l => l.Verts is not null)
            is not { } chosen) return null;
        var lod = chosen.Lod;
        if (lod.Verts is null || lod.Indices is null) return null;

        LastNumTexCoords = lod.NumTexCoords;
        var built = Build(gl, lod.Verts, lod.Indices.Value, lod.Sections?.Value, ExtraUv(lod), MaskUv(lod));
        if (built is null) return null;
        var rm = new RenderMesh(gl, built.Value.V, built.Value.I, lod.Verts.Length)
        {
            BoneCount = converted.RefSkeleton.Count,
            Skeleton = converted.RefSkeleton.Count > 0 ? new Skeleton(converted.RefSkeleton) : null,
            LodIndex = chosen.Index,
            LodsOffered = chosen.Offerable,
        }.WithSections(built.Value.S, built.Value.Min, built.Value.Max, built.Value.Skin);

        // Sockets can live on the mesh OR on the shared USkeleton, and some games put them on the
        // skeleton -- reading only the mesh's list found none at all, so helmets fell back to
        // a bare bone and lost the socket's relative rotation. Skeleton first so a mesh-level
        // socket of the same name still wins.
        var socketRefs = new List<CUE4Parse.UE4.Objects.UObject.FPackageIndex>();
        if (mesh.Skeleton?.Load<CUE4Parse.UE4.Assets.Exports.Animation.USkeleton>() is { } skelAsset)
            socketRefs.AddRange(skelAsset.Sockets);
        socketRefs.AddRange(mesh.Sockets);

        foreach (var idx in socketRefs)
        {
            if (idx?.Load<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMeshSocket>() is not { } sock) continue;
            var r = sock.RelativeRotation;
            var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(
                r.Yaw * MathF.PI / 180f, r.Pitch * MathF.PI / 180f, r.Roll * MathF.PI / 180f);
            var local = Matrix4x4.CreateScale(sock.RelativeScale.X, sock.RelativeScale.Y, sock.RelativeScale.Z)
                        * Matrix4x4.CreateFromQuaternion(q)
                        * Matrix4x4.CreateTranslation(sock.RelativeLocation.X, sock.RelativeLocation.Y, sock.RelativeLocation.Z);
            rm.Sockets[sock.SocketName.Text] = (sock.BoneName.Text, local);
        }
        return rm;
    }

    public static RenderMesh? FromStatic(GL gl, UStaticMesh mesh, int lodIndex = -1)
    {
        // Nanite meshes often ship a degenerate proxy as their normal LOD -- a helmet comes
        // through as a flat plane. The choice lives in UEToolkit.Core.MeshConvert because the
        // viewport is not the only thing that needs it: the atlas library measured proxy UVs and
        // proxy MATERIAL NAMES for a whole character, and drew a confident wrong conclusion from
        // them. One rule, three callers.
        var converted = UEToolkit.Core.MeshConvert.Static(mesh, out var usedNanite);
        LastWasNanite = usedNanite;

        LastConvertReport = $"lods={converted?.LODs.Count ?? 0} " +
                            $"verts={string.Join("/", converted?.LODs.Select(l => l.Verts?.Length ?? 0) ?? [])} " +
                            $"nanite={LastWasNanite}";

        if (converted is null || converted.LODs.Count == 0) return null;
        if (PickLod(converted.LODs, lodIndex, l => l.SkipLod, l => l.Verts is not null)
            is not { } chosen) return null;
        var lod = chosen.Lod;
        if (lod.Verts is null || lod.Indices is null) return null;

        LastNumTexCoords = lod.NumTexCoords;
        var built = Build(gl, lod.Verts, lod.Indices.Value, lod.Sections?.Value, ExtraUv(lod), MaskUv(lod));
        return built is null ? null : new RenderMesh(gl, built.Value.V, built.Value.I, lod.Verts.Length)
            {
                LodIndex = chosen.Index,
                LodsOffered = chosen.Offerable,
            }
            .WithSections(built.Value.S, built.Value.Min, built.Value.Max, built.Value.Skin);
    }

    /// <summary>
    /// THE SECOND UV SET, for a material that masks on UV1 -- or null when the mesh has one set.
    ///
    /// Distinct from <see cref="ExtraUv"/>, which SUBSTITUTES a channel for UV0 on the author's
    /// instruction. This is additive: the albedo keeps UV0 and the mask gets this, because a
    /// fragment needs both at once. `ExtraUV` is indexed from the SECOND set, so element 0 is UV1.
    /// </summary>
    private static FMeshUVFloat[]? MaskUv(CBaseMeshLod lod) =>
        lod.ExtraUV?.Value is { Length: > 0 } extra ? extra[0] : null;

    /// <summary>UV set for the requested channel, or null to fall back to vertex UV0.</summary>
    private static FMeshUVFloat[]? ExtraUv(CBaseMeshLod lod)
    {
        if (UvChannel <= 0 || lod.ExtraUV is null) return null;
        var extra = lod.ExtraUV.Value;
        var i = UvChannel - 1;
        return i < extra.Length ? extra[i] : null;
    }

    /// <summary>
    /// Render an EXPORTED `.psk` back, so the written file can be compared against the game's mesh.
    ///
    /// The one thing that must not happen here is the shader's UDIM remap: the export already baked
    /// the atlas transform into these UVs, so applying it again would divide them twice. Every
    /// section is left at `Udim = (1,1,0,0)`, whose `w` the shader tests before remapping — the same
    /// "direct" mode a mesh with no atlas uses.
    ///
    /// That is the whole point of the comparison. The viewport remaps in its VERTEX SHADER from the
    /// same `UvAtlas` code the export bakes into the file, so if the two paths agree the renders
    /// match, and if they disagree the pixels say so. It is the check that would have caught the
    /// grid being read per-mesh, and the 12288x2048 atlas read as 12x2 tiles of 1024.
    /// </summary>
    public static RenderMesh? FromPsk(GL gl, string path, out string? error)
    {
        var g = PskGeometry.Read(path);
        error = g.Error;
        if (g.Error is not null || g.Indices.Length == 0) return null;

        var data = new float[g.Positions.Length * Floats];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var i = 0; i < g.Positions.Length; i++)
        {
            var p = g.Positions[i];
            var n = g.Normals[i];
            // Same convention as Build: positions stay in UE space and uModel does UE->GL, while
            // bounds are converted now because they are only used for camera framing.
            var gl3 = new Vector3(p.X, p.Z, -p.Y);
            min = Vector3.Min(min, gl3);
            max = Vector3.Max(max, gl3);

            var o = i * Floats;
            data[o + 0] = p.X; data[o + 1] = p.Y; data[o + 2] = p.Z;
            data[o + 3] = n.X; data[o + 4] = n.Y; data[o + 5] = n.Z;
            data[o + 6] = g.Uvs[i].X; data[o + 7] = g.Uvs[i].Y;
            // A .psk carries one UV set, so UV1 mirrors UV0 rather than being left at the origin:
            // this path exists to compare the WRITTEN file against the game, and a mask sampled
            // at (0,0) would differ from the game for a reason that is not the export's fault.
            data[o + 16] = g.Uvs[i].X; data[o + 17] = g.Uvs[i].Y;
            // No skinning: this is the written bind-pose mesh, and the comparison renders both
            // meshes at rest. Bone 0 at full weight makes the palette a no-op.
            data[o + 8] = 0; data[o + 12] = 1f;
        }

        var sections = g.Sections
            .Select(s => new RenderSection
            {
                FirstIndex = s.FirstIndex,
                IndexCount = s.IndexCount,
                MaterialName = s.Material,
                Udim = new Vector4(1, 1, 0, 0),   // already baked -- do NOT remap again
                UvMode = "baked",
            })
            .ToList();

        return new RenderMesh(gl, data, g.Indices, g.Positions.Length)
            .WithSections(sections, min, max, skin: false);
    }

    /// <summary>
    /// THE AUTHOR'S OWN FBX, rendered as it will be imported.
    ///
    /// The model step used to preview `p.Mesh` -- the GAME mesh the part is built FROM -- and
    /// never the file being imported over it. So an author who put their own mesh in saw the
    /// template, unchanged, and had no way to tell a successful import from one that had done
    /// nothing at all. They reported it as a stale viewport; it had simply never been pointed at
    /// their file.
    ///
    /// Built through the SAME importer the build uses, so what is on screen is what the mod will
    /// contain: the same basis, the same welding, the same material groups. A preview that agreed
    /// with the file but not with the importer would be worse than none, because it would be
    /// believed.
    /// </summary>
    /// <param name="rig">
    /// THE TEMPLATE'S SKELETON, so the file's own weights have something to index.
    ///
    /// An FBX names its bones; a render vertex needs their positions in the palette. Only the
    /// mesh being replaced can supply that mapping, and it is the same mapping the build makes --
    /// a bone the game's rig does not have is dropped in both places, so a part that animates
    /// here animates in game. Null, or a file with no skin, renders at rest as before.
    /// </param>
    public static RenderMesh? FromFbx(GL gl, string path, string? mapping, float scale,
                                      Skeleton? rig, out string? error)
    {
        error = null;
        try
        {
            var reader = UEToolkit.Core.FbxReader.Read(path);
            var objects = reader.ReadMeshes(out error);
            if (objects.Count == 0) { error ??= "the file holds no geometry"; return null; }

            var unit = scale > 0 ? scale
                     : reader.MeshNodeScale() is { } node && node.X > 0 ? (float) node.X : 1f;

            var bases = new List<UEToolkit.Core.MeshImport.Basis>();
            foreach (var m in objects)
            {
                var b = UEToolkit.Core.MeshImport.Basis.ForFile(reader, unit, mapping, out var bWhy,
                                                                null, m.Geometry, unit);
                if (b is null) { error = bWhy; return null; }
                bases.Add(b);
            }

            var built = UEToolkit.Core.MeshImport.BuildAll(objects, bases, null, out error);
            if (built is null) return null;

            var data = new float[built.Vertices.Count * Floats];
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            // THE FILE'S BONE NAMES, RESOLVED AGAINST THE GAME'S RIG. Exact names only: a loose
            // match here would put weights on a bone that merely reads similarly, which animates
            // convincingly and is wrong -- and wrong in the viewport is worse than absent,
            // because it would be believed.
            var slot = new int[built.BoneMap.Count];
            var missing = new List<string>();
            for (var b = 0; b < built.BoneMap.Count; b++)
            {
                slot[b] = rig?.IndexOf(built.BoneMap[b]) ?? -1;
                if (slot[b] < 0) missing.Add(built.BoneMap[b]);
                else if (slot[b] >= Skeleton.MaxBones) slot[b] = -1;
            }
            var canSkin = rig is { Count: > 0 } && slot.Any(x => x >= 0);
            var unweighted = 0;

            for (var i = 0; i < built.Vertices.Count; i++)
            {
                var v = built.Vertices[i];
                // Positions stay in UE space and uModel does UE->GL, the same as every other
                // loader here; bounds are converted now because they only frame the camera.
                min = Vector3.Min(min, new Vector3(v.X, v.Z, -v.Y));
                max = Vector3.Max(max, new Vector3(v.X, v.Z, -v.Y));

                var o = i * Floats;
                data[o + 0] = v.X;  data[o + 1] = v.Y;  data[o + 2] = v.Z;
                data[o + 3] = v.NX; data[o + 4] = v.NY; data[o + 5] = v.NZ;
                data[o + 6] = v.U;  data[o + 7] = v.V;
                // The author's OWN imported mesh. MeshImport carries one UV set, so UV1 mirrors
                // UV0 -- set explicitly rather than left zero, because a mask sampled at one
                // corner paints a flat colour and reads as the import having lost its UVs.
                data[o + 16] = v.U; data[o + 17] = v.V;

                // Bone 0 at full weight is the no-skin fallback: with the palette bound it is
                // the root, which is where an unweighted vertex belongs anyway.
                data[o + 8] = 0; data[o + 12] = 1f;
                if (!canSkin) continue;

                // The importer keeps up to twelve influences; the renderer has four. Taking the
                // four HEAVIEST and renormalising is the same reduction the game's own fixed
                // layout makes, so the preview understates nothing that matters.
                var take = new List<(int Slot, int Weight)>(v.Bones.Length);
                for (var k = 0; k < v.Bones.Length && k < v.Weights.Length; k++)
                {
                    if (v.Weights[k] == 0) continue;
                    var at = v.Bones[k] < slot.Length ? slot[v.Bones[k]] : -1;
                    if (at >= 0) take.Add((at, v.Weights[k]));
                }
                if (take.Count == 0) { unweighted++; continue; }

                take.Sort((a, b) => b.Weight.CompareTo(a.Weight));
                if (take.Count > 4) take.RemoveRange(4, take.Count - 4);
                var total = (float) take.Sum(x => x.Weight);

                for (var k = 0; k < 4; k++)
                {
                    data[o + 8 + k] = k < take.Count ? take[k].Slot : 0;
                    data[o + 12 + k] = k < take.Count ? take[k].Weight / total : 0f;
                }
            }

            LastFbxSkinMissing = missing.Count;
            LastFbxSkinReport = rig is not { Count: > 0 }
                ? "no rig to skin against"
                : built.BoneMap.Count == 0
                    ? "the file carries no skinning"
                    : $"{built.BoneMap.Count - missing.Count}/{built.BoneMap.Count} bone(s) found on the rig" +
                      (missing.Count > 0
                          ? $", not on it: {string.Join(", ", missing.Take(6))}" +
                            (missing.Count > 6 ? $" and {missing.Count - 6} more" : "")
                          : "") +
                      (unweighted > 0 ? $"; {unweighted:N0} vertex(es) left on the root" : "");

            var sections = new List<RenderSection>();
            foreach (var g in built.Groups.OrderBy(x => x.FirstIndex))
                sections.Add(new RenderSection
                {
                    FirstIndex = g.FirstIndex,
                    IndexCount = g.Triangles * 3,
                    MaterialName = $"material {g.Material + 1}",
                    MaterialIndex = g.Material,
                    Udim = new Vector4(1, 1, 0, 0),
                    UvMode = "baked",
                });
            if (sections.Count == 0)
                sections.Add(new RenderSection
                {
                    FirstIndex = 0, IndexCount = built.Indices.Count,
                    MaterialName = "(all)", Udim = new Vector4(1, 1, 0, 0), UvMode = "baked",
                });

            var indices = built.Indices.Select(x => (uint) x).ToArray();
            return new RenderMesh(gl, data, indices, built.Vertices.Count)
                {
                    BoneCount = rig?.Count ?? 0,
                    Skeleton = canSkin ? rig : null,
                }
                .WithSections(sections, min, max, skin: canSkin);
        }
        catch (Exception e) { error = e.Message; return null; }
    }

    private RenderMesh WithSections(List<RenderSection> sections, Vector3 min, Vector3 max, bool skin)
    {
        HasSkin = skin;
        Sections.Clear();
        Sections.AddRange(sections);
        BoundsMin = min;
        BoundsMax = max;
        // Bounds are kept in GL space for camera framing; undo that to compare against bone
        // transforms, which stay in UE space.
        var c = (min + max) * 0.5f;
        CentreUe = new Vector3(c.X, -c.Z, c.Y);
        return this;
    }

    private static (float[] V, uint[] I, List<RenderSection> S, Vector3 Min, Vector3 Max, bool Skin)?
        Build(GL gl, CMeshVertex[] verts, uint[] indices, CMeshSection[]? sections,
              FMeshUVFloat[]? uvOverride, FMeshUVFloat[]? maskUv = null)
    {
        if (verts.Length == 0 || indices.Length == 0) return null;

        // THREE PASSES OVER THE VERTICES, TWO OF THEM DIAGNOSTIC.
        //
        // DetectUdim walks every vertex to find the UV bounds, and the report below walks them
        // again to print those same bounds. Both exist for the toolkit's single-mesh inspector,
        // where one mesh is opened and looked at. A LEVEL is 1,968 meshes and the hub's war table
        // alone is 1.4 MILLION vertices with no LOD chain -- it is Nanite, so there is no cheaper
        // level to load. Measured: 331ms a mesh, which is eleven minutes for the hub, and the
        // whole time the view keeps rearranging as things arrive.
        //
        // So the level editor turns them off. UDIM is off for this game anyway (the profile says
        // so), and the origin and span are reset rather than left holding the last mesh's answer.
        if (MeasureUvs) DetectUdim(verts, uvOverride);
        else { _uOrigin = 0; _vOrigin = 0; _uSpan = 1; _vSpan = 1; }

        var hasSkin = false;

        var data = new float[verts.Length * Floats];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var i = 0; i < verts.Length; i++)
        {
            var v = verts[i];
            // Positions stay in UE space so skinning happens in the same space as the bone
            // matrices; the UE->GL basis change is applied by uModel in the shader.
            var p = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
            var n = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z);

            // Bounds are for camera framing, so convert those to GL space now.
            var g = new Vector3(p.X, p.Z, -p.Y);
            min = Vector3.Min(min, g);
            max = Vector3.Max(max, g);

            var o = i * Floats;
            data[o + 0] = p.X; data[o + 1] = p.Y; data[o + 2] = p.Z;
            data[o + 3] = n.X; data[o + 4] = n.Y; data[o + 5] = n.Z;
            var uv = uvOverride is not null && i < uvOverride.Length ? uvOverride[i] : v.UV;
            data[o + 6] = uv.U; data[o + 7] = uv.V;

            // UV1, for a material whose mask lives on the second set. FALLS BACK TO UV0 rather
            // than to the origin: a mesh with one UV set is the common case, and a material that
            // asks for UV1 on such a mesh means the only set there is -- while (0,0) would sample
            // one corner of the mask for the whole surface and paint it a single flat colour.
            var m = maskUv is not null && i < maskUv.Length ? maskUv[i] : uv;
            if (i == 0) { if (maskUv is null) MeshesWithoutUv1++; else MeshesWithUv1++; }
            data[o + 16] = m.U; data[o + 17] = m.V;

            // Top four influences, weight-normalised. Static meshes and unweighted
            // vertices fall back to bone 0 at full weight so skinning is a no-op.
            data[o + 8] = 0; data[o + 12] = 1f;
            if (v is CSkelMeshVertex sv && sv.Influences.Count > 0)
            {
                var take = sv.Influences.OrderByDescending(x => x.Weight).Take(4).ToArray();
                var total = take.Sum(x => x.Weight);
                if (total <= 0f) total = 1f;
                for (var k = 0; k < 4; k++)
                {
                    var bi = k < take.Length ? Math.Min((int) take[k].Bone, Skeleton.MaxBones - 1) : 0;
                    var w = k < take.Length ? take[k].Weight / total : 0f;
                    data[o + 8 + k] = bi;
                    data[o + 12 + k] = w;
                }
                hasSkin = true;
            }
        }

        // Diagnostic: are the UVs actually per-vertex and in range?
        if (MeasureUvs)
        {
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            for (var i = 0; i < verts.Length; i++)
            {
                var uv = uvOverride is not null && i < uvOverride.Length ? uvOverride[i] : verts[i].UV;
                uMin = MathF.Min(uMin, uv.U); uMax = MathF.Max(uMax, uv.U);
                vMin = MathF.Min(vMin, uv.V); vMax = MathF.Max(vMax, uv.V);
            }
            var s0 = verts[0].UV; var s1 = verts[verts.Length / 2].UV; var s2 = verts[^1].UV;
            LastUvReport = $"U[{uMin:F3}..{uMax:F3}] V[{vMin:F3}..{vMax:F3}] " +
                           $"samples ({s0.U:F3},{s0.V:F3}) ({s1.U:F3},{s1.V:F3}) ({s2.U:F3},{s2.V:F3})";
        }

        // UeToGl genuinely flips handedness now, which reverses winding on its own -- so the
        // indices are taken as authored. Swapping them here as well would put every face
        // back the wrong way round.
        var idx = (uint[]) indices.Clone();

        var list = new List<RenderSection>();
        if (sections is { Length: > 0 })
        {
            foreach (var s in sections)
                list.Add(new RenderSection
                {
                    FirstIndex = s.FirstIndex,
                    IndexCount = s.NumFaces * 3,
                    MaterialName = s.MaterialName ?? "(none)",
                    MaterialIndex = s.MaterialIndex,
                    Material = s.Material?.Load<UMaterialInterface>(),
                    MaterialPath = s.Material?.GetPathName() ?? "",
                });
        }
        else
        {
            list.Add(new RenderSection { FirstIndex = 0, IndexCount = idx.Length, MaterialName = "(all)" });
        }

        LastRawUvs = new float[verts.Length * 2];
        for (var i = 0; i < verts.Length; i++)
        {
            var uv = uvOverride is not null && i < uvOverride.Length ? uvOverride[i] : verts[i].UV;
            LastRawUvs[i * 2] = uv.U;
            LastRawUvs[i * 2 + 1] = uv.V;
        }
        LastIndices = idx;

        // Per-section UV bounds, walked through the index buffer so a section only sees the
        // vertices it actually draws.
        foreach (var s in list)
        {
            float su = float.MaxValue, sU = float.MinValue, sv = float.MaxValue, sV = float.MinValue;
            var end = Math.Min(s.FirstIndex + s.IndexCount, idx.Length);
            for (var i = s.FirstIndex; i < end; i++)
            {
                var vi = (int) idx[i];
                if (vi < 0 || vi >= verts.Length) continue;
                float u = LastRawUvs[vi * 2], v = LastRawUvs[vi * 2 + 1];
                if (float.IsNaN(u) || float.IsNaN(v)) continue;
                su = MathF.Min(su, u); sU = MathF.Max(sU, u);
                sv = MathF.Min(sv, v); sV = MathF.Max(sV, v);
            }
            if (su > sU) { su = sU = sv = sV = 0f; }   // empty section
            s.UMin = su; s.UMax = sU; s.VMin = sv; s.VMax = sV;
        }

        return (data, idx, list, min, max, hasSkin);
    }

    /// <summary>Geometry only, no material state — used by the picking pass.</summary>
    public unsafe void DrawGeometry(Shader shader)
    {
        _gl.BindVertexArray(_vao);
        foreach (var s in Sections)
        {
            if (!s.Visible || s.IndexCount <= 0) continue;
            shader.Set("uUdim", s.Udim);
            _gl.DrawElements(PrimitiveType.Triangles, (uint) s.IndexCount,
                DrawElementsType.UnsignedInt, (void*) (s.FirstIndex * sizeof(uint)));
        }
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// What a customization fragment does to the tint, for the slots it names.
    ///
    /// <paramref name="Slots"/> are MATERIAL SLOT NAMES -- `MI_TORS`, not a material asset -- which
    /// is what the game matches on. A section whose slot is not in the list keeps its material's
    /// own mask and colours, which is the whole point: the fragment paints the torso and leaves
    /// the bodysuit alone. An empty list with a mask set means "every section", which is what a
    /// fragment naming no slot does.
    /// </summary>
    public readonly record struct Tint(bool On, uint Mask, Vector3[]? Colours,
                                       IReadOnlyList<string>? Slots)
    {
        public bool Targets(string slot) =>
            Mask != 0 && (Slots is null || Slots.Count == 0 ||
                          Slots.Contains(slot, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ONE PLACEMENT'S MATERIAL FOR ONE SLOT, already bound.
    ///
    /// A material override belongs to the PLACEMENT, not to the mesh: the hub writes
    /// `OverrideMaterials` 3,204 times, and the same crate mesh is painted several different ways
    /// in one level. Keying the mesh cache by (mesh + overrides) would work and was measured at
    /// 1.32x -- but it duplicates the VERTEX data to vary a texture, and geometry is the
    /// expensive half. The draw loop is already one call per placement, so passing the skin in
    /// costs nothing and shares one geometry.
    /// </summary>
    public readonly struct SectionSkin
    {
        public uint Diffuse { get; init; }
        public uint NormalMap { get; init; }
        public uint Orme { get; init; }
        public uint Emissive { get; init; }
        public Vector3 EmissiveTint { get; init; }
        public Vector4 Udim { get; init; }

        /// <summary>The GL textures this skin points at (0 = none is skipped). One place for the list, so the release
        /// and the DEV report cannot disagree about what a skin holds.</summary>
        public void AddTextureHandles(HashSet<uint> into)
        {
            foreach (var h in new[] { Diffuse, NormalMap, Orme, Emissive }) if (h != 0) into.Add(h);
        }
    }

    /// <summary>
    /// WHAT THE LAST TINTED DRAW BOUND, per section, for a driver flag to read back.
    ///
    /// "Is it following the tint texture" is not a question a screenshot answers: a mask that
    /// covers the whole garment and a mask belonging to a different section both come out as a
    /// broadly coloured model. The texture id each section BOUND is the difference, and it is
    /// invisible in every other way. Three identical ids here was the bug.
    /// </summary>
    public static string LastTintDraw = "";

    /// <summary>
    /// OFF unless a driver flag asks. Recording it concatenates a string per section per frame,
    /// and a diagnostic that costs allocations in the render loop for every user who never reads
    /// it is a diagnostic that has been paid for by the wrong person.
    /// </summary>
    public static bool RecordTintDraw;

    /// <summary>
    /// Draw the tint mask as the shader SAMPLES it, instead of shading. `--tintdebug`.
    ///
    /// A readback of the uploaded texture says what is in memory; this says what reaches a pixel.
    /// They are different claims and the second is the one that decides the picture.
    /// </summary>
    public static bool TintDebug;

    /// <summary>
    /// A MATERIAL PAINTS ITS OWN TINT, which is what the game draws and now what this draws.
    ///
    /// The author: "you have the visor completely white when it should be black by default".
    /// MI_Clo001_HELM carries `Color 03 = 000000` and its mask marks the visor in blue, so that
    /// visor is black by default and the viewport simply never applied a material's own colours.
    ///
    /// THIS SHIPPED OFF FOR ONE COMMIT, and the reason is worth keeping. Switched on, the WHOLE
    /// helmet went dark, and three separate readings of the mask failed to explain it: a channel
    /// count of an exported TGA, the same count of the UPLOADED texture, and the channel means.
    /// All three agreed the mask was 84% red, 2.2% blue and disjoint, which is exactly right.
    ///
    /// The fault was not in the mask. `uTintMask` was never told which texture unit to read, so
    /// it read unit 0 -- the DIFFUSE. A mask that is really an albedo is bright in all three
    /// channels, so every colour mixes at nearly full weight and the last one wins everywhere.
    /// That is also the author's older complaint, "it just colors the whole model instead of
    /// following the tint texture", in its original form.
    ///
    /// `--tintdebug` draws the mask the shader samples rather than shading with it, and is what
    /// finally showed it: it drew the helmet's scratches. See the sampler declarations in Draw.
    /// </summary>
    public static bool DefaultTintEnabled = true;

    public unsafe void Draw(Shader shader, Tint tint = default, SectionSkin?[]? skins = null)
    {
        // WHICH SECTIONS THE FRAGMENT REACHES, decided once for the mesh.
        //
        // When no section's slot matches, the slot names have told us nothing -- an imported mesh
        // whose materials the author has not chosen yet has sections called "material 1" -- and
        // the honest fallback is the old behaviour of painting all of them. Deciding this per
        // section instead would silently stop tinting every single-slot mod in the tool.
        // A MASK OF ZERO NOW MEANS "EACH SECTION'S OWN", not "no tint".
        //
        // It used to mean no tint, which forced every caller that wanted a mesh's own artwork to
        // pick ONE mask and hand it in for all of them -- and the viewport picked the first
        // section that had one. On a torso that is three sections each carrying its own mask,
        // the strap kit and the helmet were then painted through the TORSO's mask sampled with
        // their own UVs. Measured: "3 of 3 section(s) own a tint mask: MI_TORS, MI_Strap_Kit,
        // MI_HELM", all three painted through the first. That is the author's "it just colors
        // the whole model instead of following the tint texture", and the comment below this one
        // already claimed the lookup was per section.
        // A CHOSEN TINT, which overrides what the material says about itself.
        var chosen = tint.On;
        var anyTargeted = chosen && Sections.Any(s => tint.Targets(s.MaterialName));
        if (RecordTintDraw) LastTintDraw = "";

        var pick = tint.Colours is { Length: >= 3 } ? tint.Colours : null;

        _gl.BindVertexArray(_vao);
        foreach (var s in Sections)
        {
            if (!s.Visible || s.IndexCount <= 0) continue;

            // THE MASK, PER SECTION. Bound here rather than once per mesh for two reasons: the
            // units below are rebound every section anyway, so a mask set outside this loop was
            // read from whatever unit 0 held; and a fragment paints the slots it NAMES, so one
            // mask over every section tinted a bodysuit the game leaves alone.
            // THE CALLER'S MASK WHERE IT NAMED ONE, otherwise this section's own. A section
            // with neither is NOT painted: it carries no tintable regions, and painting it
            // through a neighbour's mask is how an untinted piece came out coloured.
            var mask = tint.Mask != 0 ? tint.Mask : s.TintMask;

            // NOBODY CHOSE, SO THE MATERIAL DECIDES. That is the default look and it is what the
            // game draws; leaving it off is what made the phase 2 visor pale.
            var own = DefaultTintEnabled && !chosen && s.TintOwn;
            var paint = mask != 0
                     && (own || (chosen && (!anyTargeted || tint.Targets(s.MaterialName))));

            shader.Set("uHasTint", paint ? 1 : 0);
            shader.Set("uTintDebug", TintDebug ? 1 : 0);
            if (paint)
            {
                // PER SECTION, because each material carries its own three. Set inside the loop
                // rather than once for the mesh, which is what a single set assumed.
                shader.Set("uTintR", own ? s.TintC1 : pick?[0] ?? Vector3.One);
                shader.Set("uTintG", own ? s.TintC2 : pick?[1] ?? Vector3.One);
                shader.Set("uTintB", own ? s.TintC3 : pick?[2] ?? Vector3.One);

                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(TextureTarget.Texture2D, mask);
            }
            if (RecordTintDraw)
                LastTintDraw += (LastTintDraw.Length > 0 ? ", " : "") +
                    $"{(s.MaterialName.Length > 0 ? s.MaterialName : "(unnamed)")}" +
                    (paint
                        ? $"->mask#{mask} " +
                          $"R={(own ? s.TintC1 : pick?[0] ?? Vector3.One)} " +
                          $"G={(own ? s.TintC2 : pick?[1] ?? Vector3.One)} " +
                          $"B={(own ? s.TintC3 : pick?[2] ?? Vector3.One)}" +
                          (own ? " (its own)" : " (chosen)")
                        : "->not painted");

            // Per section, because one mesh mixes both: a body can be one-sided armour
            // plus a two-sided neck shell, and culling the whole mesh either way is wrong.
            if (CullBackFaces && !s.TwoSided) _gl.Enable(EnableCap.CullFace);
            else _gl.Disable(EnableCap.CullFace);

            // THE PLACEMENT'S OWN MATERIAL FOR THIS SLOT, where it has one. Looked up by
            // MaterialIndex rather than by the loop counter, because the two are only
            // accidentally the same. A slot the placement does not override keeps the mesh's.
            var skin = skins is not null && s.MaterialIndex >= 0 && s.MaterialIndex < skins.Length
                ? skins[s.MaterialIndex] : null;
            var diffuse = skin?.Diffuse ?? s.Diffuse;
            var normal = skin?.NormalMap ?? s.NormalMap;
            var orme = skin?.Orme ?? s.Orme;
            var emissive = skin?.Emissive ?? s.Emissive;
            var emissiveTint = skin?.EmissiveTint ?? s.EmissiveTint;
            var udim = skin is { } sk ? sk.Udim : s.Udim;

            // THE TRIM SHEET, per section and before the albedo is read, because on a
            // tint-only material it REPLACES that albedo rather than colouring it.
            //
            // UNIT 7. 0-2 are the surface maps, 3 the bone palette, 4 the tint mask, 5 the shadow
            // cascade array and 6 the emissive -- and a sampler2D sharing a unit with the
            // sampler2DArrayShadow is undefined, which is why these are counted rather than
            // reused.
            // NOT GATED ON SurfaceMaps. That flag turns off normals, ORME and emissive for the
            // cheaper shading modes -- surface DETAIL. The trim sheet is the base COLOUR on a
            // tint-only material, so gating it there would make "Material" mode draw the one
            // texture the game does not use, which is the exact fault this fixes.
            var trim = TrimEnabled && s.TrimMask != 0;
            shader.Set("uHasTrim", trim ? 1 : 0);
            if (trim)
            {
                shader.Set("uTrimUv1", TrimUvForce >= 0 ? TrimUvForce : (s.TrimUv1 ? 1 : 0));
                shader.Set("uTrimDebug", TrimDebug);
                shader.Set("uTrimOrder", TrimOrder);
                shader.Set("uTrimUseA", s.TrimUseA ? 1 : 0);
                shader.Set("uTrimNoDif", TrimBaseForce >= 0 ? TrimBaseForce : (s.TrimNoDif ? 1 : 0));
                shader.Set("uTrimBase", s.TrimBase);
                shader.Set("uTrimR", s.TrimR);
                shader.Set("uTrimG", s.TrimG);
                shader.Set("uTrimB", s.TrimB);
                shader.Set("uTrimA", s.TrimA);
                shader.Set("uTrimStrength", s.TrimStrength);
                shader.Set("uTrimTile", s.TrimTile);
                shader.Set("uTrimBright", s.TrimBright);
                shader.Set("uTrimRough", s.TrimRough);
                shader.Set("uTrimRoughStr", s.TrimRoughStrength);
                shader.Set("uTrimRoughRemap", s.TrimRoughRemap);
                shader.Set("uTrimMetal", s.TrimMetal);
                shader.Set("uTrimMetalStr", s.TrimMetalStrength);
                shader.Set("uTrimMetalRemap", s.TrimMetalRemap);
                shader.Set("uTrimMask", 7);
                _gl.ActiveTexture(TextureUnit.Texture7);
                _gl.BindTexture(TextureTarget.Texture2D, s.TrimMask);
            }

            shader.Set("uUdim", udim);
            shader.Set("uHasDiffuse", diffuse != 0 ? 1 : 0);

            // THE SAMPLER UNITS ARE SET HERE, not once per viewport. The Studio's viewport
            // assigned uNormalMap to unit 1 at startup and the level editor never assigned it at
            // all -- so the moment the shader started reading it, the level editor would have
            // sampled the DIFFUSE as a normal map and lit the whole scene off its own albedo.
            // Setting it beside the flag that turns it on is the only arrangement where the two
            // cannot disagree.
            shader.Set("uNormalMap", 1);
            shader.Set("uHasNormalMap", NormalMapping && SurfaceMaps && normal != 0 ? 1 : 0);
            shader.Set("uNormalStrength", NormalStrength);
            shader.Set("uHasDetailNormal", NormalMapping && SurfaceMaps && s.DetailNormal != 0 ? 1 : 0);
            if (NormalMapping && SurfaceMaps && s.DetailNormal != 0)
            {
                shader.Set("uDetailNormal", 8);
                shader.Set("uDetailTile", s.DetailTile);
                shader.Set("uDetailIntensity", s.DetailIntensity);
                _gl.ActiveTexture(TextureUnit.Texture8);
                _gl.BindTexture(TextureTarget.Texture2D, s.DetailNormal);
            }
            shader.Set("uHasUvTint", TrimEnabled && s.UvTint ? 1 : 0);
            if (TrimEnabled && s.UvTint)
            {
                shader.Set("uUvTintPN", s.UvTintPN);
                shader.Set("uUvTintNP", s.UvTintNP);
                shader.Set("uUvTintNN", s.UvTintNN);
                shader.Set("uUvTintStr", s.UvTintStrength);
            }

            shader.Set("uHasEmSheet", SurfaceMaps && s.EmSheet != 0 && s.EmMask != 0 ? 1 : 0);
            if (SurfaceMaps && s.EmSheet != 0 && s.EmMask != 0)
            {
                shader.Set("uEmSheet", 9);
                shader.Set("uEmMask", 10);
                shader.Set("uEmSheetColor", s.EmSheetColor);
                shader.Set("uEmSheetTile", s.EmSheetTile);
                shader.Set("uEmSheetStrength", s.EmSheetStrength);
                _gl.ActiveTexture(TextureUnit.Texture9);
                _gl.BindTexture(TextureTarget.Texture2D, s.EmSheet);
                _gl.ActiveTexture(TextureUnit.Texture10);
                _gl.BindTexture(TextureTarget.Texture2D, s.EmMask);
            }

            shader.Set("uHasProj", SurfaceMaps && s.ProjColor != 0 ? 1 : 0);
            if (SurfaceMaps && s.ProjColor != 0)
            {
                // UE SPACE TO GL SPACE. The axis is authored against UE's Z-up; the shader
                // compares it with a normal in GL's Y-up. Converting on the CPU keeps the one
                // basis change this renderer has in the one place it already lives.
                var axis = Vector3.TransformNormal(s.ProjAxis, UeToGl);
                if (axis.LengthSquared() > 1e-8f) axis = Vector3.Normalize(axis);
                shader.Set("uProjColor", 11);
                shader.Set("uProjAxis", axis);
                shader.Set("uProjTint", s.ProjTint);
                shader.Set("uProjSize", s.ProjSize);
                shader.Set("uProjIntensity", s.ProjIntensity);
                shader.Set("uProjSlopeMin", s.ProjSlopeMin);
                shader.Set("uProjSlopeMax", s.ProjSlopeMax);
                shader.Set("uProjNrmIntensity", s.ProjNrmIntensity);
                shader.Set("uProjRough", s.ProjRough);
                _gl.ActiveTexture(TextureUnit.Texture11);
                _gl.BindTexture(TextureTarget.Texture2D, s.ProjColor);

                shader.Set("uHasProjNrm", s.ProjNrm != 0 ? 1 : 0);
                if (s.ProjNrm != 0)
                {
                    shader.Set("uProjNrm", 12);
                    _gl.ActiveTexture(TextureUnit.Texture12);
                    _gl.BindTexture(TextureTarget.Texture2D, s.ProjNrm);
                }

                shader.Set("uHasProjAlpha", s.ProjAlpha != 0 ? 1 : 0);
                if (s.ProjAlpha != 0)
                {
                    shader.Set("uProjAlpha", 13);
                    shader.Set("uProjAlphaSize", s.ProjAlphaSize);
                    shader.Set("uProjAlphaContrast", s.ProjAlphaContrast);
                    _gl.ActiveTexture(TextureUnit.Texture13);
                    _gl.BindTexture(TextureTarget.Texture2D, s.ProjAlpha);
                }
            }
            else { shader.Set("uHasProjNrm", 0); shader.Set("uHasProjAlpha", 0); }

            // THE CUTOUT NOW HAS TWO SOURCES. A decal with its own artwork cuts on the
            // diffuse alpha as before; one whose artwork is a placeholder cuts on the channel
            // mask, which is the only shape it has. Without the second clause the 80 instances
            // with no diffuse keep their alpha of 1.0 and keep drawing as full rectangles.
            shader.Set("uAlphaCut", s.AlphaCut && (diffuse != 0 || s.DecalMask != 0) ? 0.333f : -1f);

            shader.Set("uHasDecalMask", SurfaceMaps && s.DecalMask != 0 ? 1 : 0);
            if (SurfaceMaps && s.DecalMask != 0)
            {
                shader.Set("uDecalMask", 14);
                shader.Set("uDecalChannel", s.DecalChannel);
                shader.Set("uDecalMaskPower", s.DecalMaskPower);
                shader.Set("uDecalMaskStrength", s.DecalMaskStrength);
                shader.Set("uDecalAlpha", s.DecalAlpha);
                shader.Set("uDecalTint", s.DecalTint);
                shader.Set("uDecalTintStrength", s.DecalTintStrength);
                _gl.ActiveTexture(TextureUnit.Texture14);
                _gl.BindTexture(TextureTarget.Texture2D, s.DecalMask);
            }
            shader.Set("uHasFuzz", SurfaceMaps && s.Fuzz ? 1 : 0);
            if (SurfaceMaps && s.Fuzz) shader.Set("uFuzzColor", s.FuzzColor);

            shader.Set("uHasOrme", SurfaceMaps && orme != 0 ? 1 : 0);
            // Whether the mask's RED is an occlusion map or an unused channel. See
            // RenderSection.MaskHasAo: zero is a valid occlusion value, so a mask without one
            // must say so or it reads as fully occluded.
            shader.Set("uAoFromMask", s.MaskHasAo ? 1 : 0);
            shader.Set("uEmissiveScale", SurfaceMaps ? s.EmissiveScale : 1f);
            shader.Set("uEmissiveFlat", SurfaceMaps && s.EmissiveFlat && emissive == 0 ? 1 : 0);
            if (SurfaceMaps && s.EmissiveFlat && emissive == 0)
                shader.Set("uEmissiveTint", emissiveTint);
            shader.Set("uHasEmissive", SurfaceMaps && emissive != 0 ? 1 : 0);
            if (SurfaceMaps && emissive != 0)
            {
                shader.Set("uEmissive", 6);
                shader.Set("uEmissiveTint", emissiveTint);
                // UNIT 6. Units 0-2 and 4 are the surface maps and 5 is the shadow cascade array,
                // and a sampler2D sharing a unit with the sampler2DArrayShadow is undefined.
                _gl.ActiveTexture(TextureUnit.Texture6);
                _gl.BindTexture(TextureTarget.Texture2D, emissive);
            }

            // THE UNIT EVERY SAMPLER READS FROM, DECLARED WHERE THE UNIT IS BOUND.
            //
            // A sampler needs its unit INDEX as a uniform; ActiveTexture alone only says where
            // the texture went. An unset sampler reads unit 0, which is the diffuse, so a missing
            // declaration does not fail -- it silently samples the albedo and calls it something
            // else.
            //
            // THESE WERE DECLARED IN THE ImGui VIEWPORT AND NOWHERE ELSE, so the Avalonia Studio
            // got uDiffuse right by luck (it wants 0) and got the other two wrong:
            //
            //   uOrme     read the DIFFUSE as roughness, metal and AO
            //   uTintMask read the DIFFUSE as the tint mask
            //
            // The second is the author's whole report. A tint mask that is really the albedo is
            // bright in all three channels, so all three colours mix at nearly full weight and
            // the last one wins everywhere -- which is "it just colors the whole model instead of
            // following the tint texture", and why the phase 2 visor's black Color 03 darkened an
            // entire helmet instead of a visor. Measured with --tintdebug, which draws the mask
            // the shader samples: it drew the helmet's scratches.
            //
            // Declared HERE, beside the binds, rather than in either host: a third host cannot
            // then repeat the omission, and RenderMesh already declares uNormalMap, uTrimMask and
            // uEmissive this way.
            shader.Set("uDiffuse", 0);
            shader.Set("uOrme", 2);
            shader.Set("uTintMask", 4);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, diffuse);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, normal);
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, orme);

            _gl.DrawElements(PrimitiveType.Triangles, (uint) s.IndexCount,
                DrawElementsType.UnsignedInt, (void*) (s.FirstIndex * sizeof(uint)));
        }
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        // ONCE. A second Dispose used to delete the same three names again, which GL ignores until the driver has
        // handed one of them to a newer buffer; and the live counts above would go negative.
        if (_disposed) return;
        _disposed = true;
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        Interlocked.Decrement(ref _liveMeshes);
        Interlocked.Increment(ref _disposedMeshes);
        Interlocked.Add(ref _liveVertexBytes, -_vertexBytes);
        Interlocked.Add(ref _liveIndexBytes, -_indexBytes);
    }
}

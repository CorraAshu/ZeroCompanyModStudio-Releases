using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using Silk.NET.OpenGL;
using UEToolkit.App.Rendering;
using Shader = UEToolkit.App.Rendering.Shader;   // Silk.NET has one of its own

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE MODEL, IN THE GAME'S OWN SHADER.
///
/// NOT A COPY OF THE LEVEL EDITOR'S VIEWPORT. That one holds a scene: hundreds of placements, a
/// gizmo, picking, sublevel visibility, shadow cascades and a map's own light rig, and none of
/// that means anything with a single mesh on screen. What the two share is the RENDERER —
/// RenderMesh, TextureCache, Shader, Camera — which is linked into this project rather than
/// copied, so the material families, the trim sheet and the emissive rules are the same code in
/// both.
///
/// So "Default Lit" is not an approximation written here. It is the shader that has been
/// sharpened against the game for weeks; this control just points it at one mesh.
///
/// THE STUDIO RIG, DELIBERATELY. `uLevelLit = 0` is the shader's own fallback — a three-point
/// studio rig — and it is the right answer for a model on its own: a level's lighting belongs to
/// the level, and a helmet viewed in a cave's rig would be judged in light it will never wear.
/// </summary>
public sealed unsafe class ModelView : OpenGlControlBase
{
    private GL? _gl;
    private Shader? _shader;
    private TextureCache? _textures;
    private RenderMesh? _mesh;

    /// <summary>
    /// THE GROUND GRID AND A REFERENCE BOX, on the line shader the ImGui viewport already uses.
    ///
    /// Ported rather than written: `Shaders.GridVertex`/`GridFragment` and the 21x21 grid at 50cm
    /// spacing are in the shared Rendering folder this project links, and a second grid could
    /// only ever disagree with that one about what a metre looks like.
    ///
    /// The box is ONE METRE on a side, sitting on the grid at the origin. A character stands
    /// about 180cm in these units, so a metre reads as a scale reference at a glance without
    /// filling the view -- which a bounding-box-sized cube would.
    /// </summary>
    private Shader? _lineShader;
    private Shader? _solidShader;
    private Shader? _depthShader;

    private uint _shadowFbo, _shadowTex;
    private readonly Matrix4x4[] _shadowMat = new Matrix4x4[3];
    private float _shadowTexel;
    private bool _shadowReady;
    private bool _shadowSaid;

    /// <summary>
    /// One map, 2048 square. The Level Editor fits THREE cascades to the view frustum because it
    /// is looking at a kilometre of landscape; this is one object on a five metre slab, so one
    /// cascade fitted to the model's own bounds puts every texel where it is wanted.
    /// </summary>
    private const uint ShadowSize = 2048;

    private bool _shadows = true;

    /// <summary>
    /// Whether the model casts. The author's ask: "any model loaded into it cast a shadow (with a
    /// toggle to turn it off or on)".
    ///
    /// ON BY DEFAULT because it is what he asked for, and it costs one extra geometry pass over a
    /// single mesh -- the cost the Level Editor worries about is a pass over 26,000 placements.
    /// </summary>
    public bool ShowShadows
    {
        get => _shadows;
        set
        {
            if (_shadows == value) return;
            _shadows = value;
            _shadowReady = false;
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// MESHES HUNG ON THE LOADED ONE -- a helmet, arms, legs and boots on a torso.
    ///
    /// The primary stays <see cref="_mesh"/> rather than becoming element zero of a list. It is
    /// the asset the tab was opened on: the LOD chain, the material rows, the rig the animation
    /// list is built from and the bounds the camera framed are all ITS answers, and making it one
    /// of several would mean every one of those deciding which mesh it meant.
    /// </summary>
    private readonly List<Attachment> _attached = new();
    private readonly List<(string Key, string Name, UObject Asset)> _pendingAttach = new();

    /// <param name="Key">The package path, which is what a caller removes it by.</param>
    /// <param name="Asset">Kept, for the same reason the primary's is: a tab switch destroys the
    /// GL context and the mesh with it, and rebuilding from the object beats a second package
    /// read through the gate.</param>
    private sealed record Attachment(string Key, string Name, UObject Asset, RenderMesh Mesh,
                                     int Shared, int Total);
    private uint _gridVao, _gridVbo, _boxVao, _boxVbo;
    private int _gridVerts, _boxVerts;

    /// <summary>The asset waiting to become geometry. Built on the GL thread, where the GL is.</summary>
    private UObject? _pending;

    /// <summary>
    /// The loaded asset, KEPT after the first build so a LOD change can rebuild from it.
    ///
    /// Without this a LOD switch would have to go back through the package read — slow, gated,
    /// and a second chance for the cache to race — to get an object the tab already had.
    /// </summary>
    private UObject? _asset;

    /// <summary>Which LOD to build. -1 is the renderer's own default choice.</summary>
    private int _wantLod = -1;
    private bool _framed;
    private bool _said;

    public Camera Camera { get; } = new();

    public ModelView()
    {
        // FOCUSABLE, OR THE KEYBOARD NEVER ARRIVES. OpenGlControlBase is not focusable by
        // default, and a control that cannot take focus is never sent a key at all — so the
        // handlers below can be perfect and simply never run. The level editor lost an evening
        // to exactly this, reported as "I can't use W A S D to move around".
        Focusable = true;
    }

    // ---------------------------------------------------------------- view modes

    /// <summary>
    /// ONE MODE, ITS SHADER NUMBER, AND WHETHER IT DRAWS AS LINES.
    ///
    /// The numbers are <see cref="Shaders.MeshFragment"/>'s uMode, which is shared with the level
    /// editor and the toolkit's own preview — so the list is a view OF that shader rather than a
    /// second opinion about what the modes are. A mode this application invented would be a
    /// number the shader does not implement, and it would silently fall through to lit.
    /// </summary>
    public sealed record ViewMode(string Name, int Mode, bool Wire = false)
    {
        // What the ComboBox shows. Without it a row reads as the record's ToString().
        public override string ToString() => Name;
    }

    /// <summary>
    /// The dropdown, in the order somebody actually walks it: the two you compare against each
    /// other first, then geometry, then the surface channels that answer "why does it look like
    /// that".
    /// </summary>
    public static readonly ViewMode[] Modes =
    [
        new("Default Lit", 0),
        new("Base Color", 1),
        new("Wireframe", 9, Wire: true),
        new("Normals", 2),
        new("UV Checker", 3),
        new("Material AO", 4),
        new("Roughness", 5),
        new("Smoothness", 6),
        new("Metallic", 7),
        new("Reflectance", 8),
    ];

    private ViewMode _mode = Modes[0];

    public ViewMode Mode
    {
        get => _mode;
        set { _mode = value; RequestNextFrameRendering(); }
    }

    /// <summary>What went wrong, for the panel to say rather than show an empty box.</summary>
    public string? Fault { get; private set; }

    /// <summary>Sections drawn, and how many of them got their textures.</summary>
    public int SectionCount { get; private set; }
    public int TexturedSections { get; private set; }

    /// <summary>
    /// The loaded mesh's BONE NAMES, which is what decidesable animations — not its skeleton
    /// asset.
    ///
    /// A part's own skeleton has no animations on it: measured across this whole game, the
    /// armour kits hold five sequences between them while SKEL_Humanoid_01 holds 1,734 and no
    /// wearable part references it. So asking the library for the skeleton this mesh names
    /// returns an empty list for exactly the meshes an author cares about. Bone names are what
    /// playback actually matches on, so they are what the list has to be built from.
    /// </summary>
    public IReadOnlyList<string> RigBones => _mesh?.Skeleton?.Names ?? [];

    /// <summary>What is hung on the loaded mesh, and how well each one's rig matched.</summary>
    public IReadOnlyList<(string Key, string Name, int Shared, int Total)> Attached =>
        _attached.Select(a => (a.Key, a.Name, a.Shared, a.Total)).ToList();

    /// <summary>Raised once an attachment has really been built, on the GL thread.</summary>
    public event Action? AttachedChanged;

    /// <summary>
    /// Queue a mesh to hang on this one. Built on the GL thread like everything else here.
    ///
    /// Re-attaching the same package is ignored rather than doubling it -- two copies of one
    /// helmet in the same place is invisible and costs a draw.
    /// </summary>
    public void Attach(string key, string name, UObject asset)
    {
        if (_attached.Any(a => a.Key == key) || _pendingAttach.Any(q => q.Key == key)) return;
        _pendingAttach.Add((key, name, asset));
        RequestNextFrameRendering();
    }

    /// <summary>
    /// A detach disposed an attachment's mesh, but the textures its materials uploaded stay in this view's cache. Detach
    /// runs on the UI thread and deleting a texture is GL, so it only sets this; the next <see cref="Build"/> (render
    /// thread, context current) deletes what no part still in the view points at. Trying twenty helmets in turn on one
    /// torso otherwise kept all twenty helmets' pictures until the tab closed.
    /// </summary>
    private volatile bool _releaseAfterDetach;

    /// <summary>
    /// Meshes a detach took off the view. Deleting a mesh's buffers is GL, and Detach runs on the UI thread where no
    /// context is current: a first-ever buffer delete there failed outright ("Native symbol not found: glDeleteVertexArrays")
    /// and took the whole program down (found by the attach sequence). So they wait here and are disposed at the
    /// top of the next <see cref="Build"/>.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<RenderMesh> _disposeLater = new();

    public void Detach(string key)
    {
        // A view that is hidden (its tab is not the shown one) has handed its attachments back to the pending list, so a
        // part removed then is removed from there too, or it would be rebuilt when the tab is shown again.
        var wasPending = _pendingAttach.RemoveAll(q => q.Key == key) > 0;
        var at = _attached.FindIndex(a => a.Key == key);
        if (at < 0 && !wasPending) return;
        if (at >= 0)
        {
            _disposeLater.Enqueue(_attached[at].Mesh);      // its buffers AND the textures go in the next Build, on the render thread
            _attached.RemoveAt(at);
        }
        _releaseAfterDetach = true;
        _framed = false;
        AttachedChanged?.Invoke();
        RequestNextFrameRendering();
    }

    public void DetachAll()
    {
        foreach (var a in _attached) _disposeLater.Enqueue(a.Mesh);
        _attached.Clear();
        _pendingAttach.Clear();
        _releaseAfterDetach = true;
        _framed = false;
        AttachedChanged?.Invoke();
        RequestNextFrameRendering();
    }

    /// <summary>The ground grid. On by default -- a model floating in nothing has no scale.</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { if (_showGrid != value) { _showGrid = value; RequestNextFrameRendering(); } }
    }
    private bool _showGrid = true;

    /// <summary>The floor slab. OFF by default -- it is a backdrop, and not every look wants one.</summary>
    public bool ShowFloor
    {
        get => _showFloor;
        set { if (_showFloor != value) { _showFloor = value; RequestNextFrameRendering(); } }
    }
    private bool _showFloor;

    /// <summary>
    /// ONE MATERIAL SLOT, AND WHICH MAPS ACTUALLY REACHED IT.
    ///
    /// The three flags are the difference between a model that loaded and one that loaded
    /// CORRECTLY, and nothing in the picture separates them: a material with no mask draws with
    /// the shader's constants and looks like a material that works. This is the readout that
    /// caught the game's own mask going unbound in all three applications.
    /// </summary>
    public sealed record MaterialRow(string Name, bool Diffuse, bool Normal, bool Mask)
    {
        public string Maps =>
            (Diffuse ? "D" : "–") + " " + (Normal ? "N" : "–") + " " + (Mask ? "MSK" : "–");
    }

    /// <summary>The material slots of the loaded mesh, in section order.</summary>
    public IReadOnlyList<MaterialRow> Materials { get; private set; } = [];

    // ---------------------------------------------------------------- animation

    private readonly AnimPlayer _anim = new();
    private uint _boneTex;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastTick;

    /// <summary>What is playing, and where it has got to — for the panel to say.</summary>
    public string? AnimName => _anim.Name;
    public bool Playing => _anim.Playing;
    public float AnimFrame => _anim.Frame;
    public int AnimFrames => _anim.FrameCount;
    public bool HasAnim => _anim.HasAnim;

    /// <summary>
    /// Whether it runs round again at the end, and how fast.
    ///
    /// A TOGGLE RATHER THAN A CONSTANT. Playback forced Loop on, which is right for watching a
    /// walk cycle and wrong for everything that happens once — a death, a reload, a melee — where
    /// the only way to see the end was to catch it. Speed is the same question at the other end:
    /// a 0.2x attack is how you tell whether a sword actually connects.
    /// </summary>
    public bool Loop
    {
        get => _anim.Loop;
        set { _anim.Loop = value; RequestNextFrameRendering(); }
    }

    /// <summary>0.05x to 3x, the range the ImGui Studio's slider offers.</summary>
    public float Speed
    {
        get => _anim.Speed;
        set { _anim.Speed = Math.Clamp(value, 0.05f, 3f); RequestNextFrameRendering(); }
    }

    /// <summary>Raised every frame while playing, so a readout can follow it.</summary>
    public event Action? Ticked;

    /// <summary>
    /// Play a sequence on this mesh's own rig.
    ///
    /// BOUND BY BONE NAME, which is what makes an animation authored on SKEL_Humanoid_01 drive a
    /// kit skeleton at all — Skeleton.BuildTrackMap maps tracks by name, and the armour kits are
    /// copies of the humanoid rig. It is the same reason the LIST is built by bone name: a part's
    /// own skeleton carries no sequences.
    /// </summary>
    public bool PlayAnimation(UAnimSequence anim, out string message)
    {
        message = "";
        if (_mesh?.Skeleton is not { } skel)
        { message = "this mesh has no rig to animate"; return false; }

        if (!_anim.Load(anim, skel, out message)) return false;

        _anim.Playing = true;
        // LOOP AND SPEED ARE THE VIEWER'S, NOT THE SEQUENCE'S. Forcing Loop on here meant the
        // checkbox could be ticked off and silently came back on with the next sequence, so
        // anything that happens ONCE still could not be watched to its end. AnimPlayer already
        // defaults Loop to true and Clear() leaves both alone, so the settings simply persist.
        _lastTick = _clock.Elapsed.TotalSeconds;
        RequestNextFrameRendering();
        return true;
    }

    /// <summary>
    /// Hold one frame of the loaded sequence.
    ///
    /// For a still. A playing animation photographs a different pose every run, so comparing two
    /// shots would be comparing two moments as much as two builds.
    /// </summary>
    public void PoseAt(float frame)
    {
        if (!_anim.HasAnim) return;
        _anim.Frame = Math.Clamp(frame, 0, Math.Max(0, _anim.FrameCount - 1));
        _anim.Playing = false;
        RequestNextFrameRendering();
    }

    public void StopAnimation()
    {
        _anim.Clear();
        RequestNextFrameRendering();
    }

    /// <summary>Pause without forgetting the pose, so a frame can be looked at.</summary>
    public void TogglePlay()
    {
        if (!_anim.HasAnim) return;
        _anim.Playing = !_anim.Playing;
        _lastTick = _clock.Elapsed.TotalSeconds;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// The bone palette, as a texture.
    ///
    /// A TEXTURE AND NOT UNIFORMS: 256 mat4s is 1024 vec4 registers, past the vertex shader's
    /// constant limit, and characters here exceed 128 bones. Four texels wide, one row per bone,
    /// RGBA32F — the shape MeshVertex.boneMatrix reads with texelFetch.
    /// </summary>
    private void UploadBones(Matrix4x4[] palette)
    {
        if (_gl is null || palette.Length == 0) return;

        if (_boneTex == 0)
        {
            _boneTex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _boneTex);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int) GLEnum.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int) GLEnum.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int) GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int) GLEnum.ClampToEdge);
        }
        else _gl.BindTexture(TextureTarget.Texture2D, _boneTex);

        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        fixed (Matrix4x4* p = palette)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f,
                           4, (uint) palette.Length, 0, PixelFormat.Rgba, PixelType.Float, p);
    }

    // ---------------------------------------------------------------- tint

    /// <summary>
    /// Whether this mesh paints through a tint mask at all — which decides whether a colour
    /// picker would do anything.
    ///
    /// A material either names `Tint Mask VT` or it does not, and offering colours on a model
    /// that cannot wear them is a control that appears broken rather than absent.
    /// </summary>
    public bool CanTint => _mesh?.Sections.Any(s => s.TintMask != 0) ?? false;

    /// <summary>
    /// WHICH SECTIONS CARRY A TINT MASK OF THEIR OWN, for a driver flag to read back.
    ///
    /// The question worth asking is not "does tinting happen" but "does it happen where the
    /// artwork says it should". A count of sections PAINTED against a count of sections that own
    /// a mask is the whole of that, and neither number is visible in a screenshot.
    /// </summary>
    /// <summary>
    /// WHAT EACH SECTION HAS TO SHADE WITH, for a driver flag to read back.
    ///
    /// The author's report is "you have the visor completely white when it should be black by
    /// default not reading the textures properly", and the first thing worth knowing is whether
    /// there IS a texture to read. The shader falls back to a flat mid grey when a section has no
    /// diffuse, and lit, mid grey on a bright surface is indistinguishable from white — so an
    /// unbound map and a misread one look identical on screen and completely different here.
    /// </summary>
    public string ShadingCensus
    {
        get
        {
            if (_mesh is null) return "no mesh";
            var rows = _mesh.Sections.Select(s =>
                $"{(s.MaterialName.Length > 0 ? s.MaterialName : "(unnamed)")}: " +
                $"dif={(s.Diffuse != 0 ? "yes" : "NO")} " +
                $"nrm={(s.NormalMap != 0 ? "yes" : "no")} " +
                $"orme={(s.Orme != 0 ? "yes" : "no")} " +
                $"emi={(s.Emissive != 0 ? "yes" : "no")} " +
                $"family='{s.Family}'" +
                (s.DiffusePath.Length > 0 ? $" from {s.DiffusePath}" : ""));
            return string.Join(Environment.NewLine + "    ", rows);
        }
    }

    public string TintCensus
    {
        get
        {
            if (_mesh is null) return "no mesh";
            var own = _mesh.Sections.Count(s => s.TintMask != 0);
            var rows = _mesh.Sections.Select(s =>
                (s.MaterialName.Length > 0 ? s.MaterialName : "(unnamed)") +
                (s.TintMask != 0 ? " [mask]" : " [none]"));
            return $"{own} of {_mesh.Sections.Count} section(s) own a tint mask: " +
                   string.Join(", ", rows);
        }
    }

    private readonly Vector3[] _tint = [Vector3.One, Vector3.One, Vector3.One];
    private bool _tinting;

    /// <summary>
    /// Colour 01, 02 and 03 — Primary, Secondary and Tertiary.
    ///
    /// The mask's red, green and blue channels select them in that order, which is not inferred:
    /// the game's own `T_DefaultTintMSK` has "COLOR 1", "COLOR 2" and "COLOR 3" painted into
    /// those three channels. Which SWATCH a part offers is a separate question that
    /// TintSwatches settled by measurement.
    /// </summary>
    public void SetTint(int slot, Vector3 colour)
    {
        if (slot < 0 || slot > 2) return;
        _tint[slot] = colour;
        _tinting = true;
        RequestNextFrameRendering();
    }

    /// <summary>Back to the artwork as the game ships it.</summary>
    public void ClearTint()
    {
        _tint[0] = _tint[1] = _tint[2] = Vector3.One;
        _tinting = false;
        RequestNextFrameRendering();
    }

    /// <summary>Raised once the mesh is up, so the tab can report what it got.</summary>
    public event Action? Loaded;

    /// <summary>
    /// Show an asset. The package read has already happened; this takes the loaded object.
    ///
    /// The GEOMETRY is built on the render thread because it uploads buffers, and the GL only
    /// exists there. Handing it the object rather than a path keeps the package read — which is
    /// slow and must be gated — off this control entirely.
    /// </summary>
    public void Show(UObject asset)
    {
        _pending = asset;
        _asset = asset;
        _wantLod = -1;
        _framed = false;
        Fault = null;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// SHOW AN FBX OF THE AUTHOR'S, skinned to the game mesh it replaces.
    ///
    /// The rig comes from the mesh being replaced because an FBX names its bones and a render
    /// vertex needs their positions — and it is the SAME mapping the build makes, so a part that
    /// animates here animates in game. Without it the file draws at rest.
    ///
    /// RenderMesh.FromFbx already existed in the linked renderer; this is a call, not a reader.
    /// </summary>
    public void ShowFbx(string path, string? mappingMeshPath, Skeleton? rig)
    {
        _pendingFbx = (path, mappingMeshPath, rig);
        _pending = null;
        _asset = null;
        _wantLod = -1;
        _framed = false;
        Fault = null;
        RequestNextFrameRendering();
    }

    private (string Path, string? Mapping, Skeleton? Rig)? _pendingFbx;

    /// <summary>
    /// Rebuild what is on screen from whatever it was last given.
    ///
    /// THE POINT OF THIS IS THE ROUND TRIP: look at an imported model, tab back to the part,
    /// import its textures, tab back here and see them on it. Without a rebuild the viewport
    /// keeps the textures it bound the first time and quietly shows yesterday's answer.
    /// </summary>
    public void Refresh()
    {
        if (_pendingFbx is { } f) { _pendingFbx = f; RequestNextFrameRendering(); return; }
        if (_asset is { } a) { _pending = a; RequestNextFrameRendering(); }
    }

    // ---------------------------------------------------------------- LOD

    /// <summary>The LOD indices this mesh actually ships, for the picker.</summary>
    public IReadOnlyList<int> LodsOffered => _mesh?.LodsOffered ?? [];

    /// <summary>Which LOD is on screen.</summary>
    public int Lod => _mesh?.LodIndex ?? 0;

    /// <summary>Raised when the geometry changes, so a picker can follow what was really built.</summary>
    public event Action? LodChanged;

    /// <summary>
    /// Rebuild at another LOD.
    ///
    /// THE CAMERA STAYS WHERE IT IS. A LOD is the same model at another density, so re-framing
    /// on every switch would throw away the angle somebody chose in order to compare two of
    /// them — which is the only reason to switch at all.
    /// </summary>
    public void ShowLod(int lod)
    {
        if (_asset is null || lod == Lod) return;
        _wantLod = lod;
        _pending = _asset;
        RequestNextFrameRendering();
    }


    /// <summary>
    /// A TRANSPARENT FILL, SO THE MOUSE CAN FIND THIS CONTROL AT ALL.
    ///
    /// Avalonia hit-tests against what a visual DREW, and an OpenGlControlBase draws through a
    /// composition operation the hit test cannot see — so the pointer goes to whatever is behind
    /// it and every mouse handler here is dead code. Not orbit, not pan, not the wheel.
    ///
    /// THE LEVEL EDITOR'S Viewport.cs ALREADY CARRIES THIS, with a note saying it went unnoticed
    /// because every test drove the control DIRECTLY and so proved the maths while saying nothing
    /// about whether an event arrives. I then added --orbit, which calls Camera.Orbit directly,
    /// and reported the camera working. Same fault, same blind spot, one file over.
    ///
    /// A transparent rectangle is geometry, so the hit test can see it. --hittest asks Avalonia
    /// rather than assuming.
    /// </summary>
    public override void Render(Avalonia.Media.DrawingContext context)
    {
        context.FillRectangle(Avalonia.Media.Brushes.Transparent, new Rect(Bounds.Size));
        base.Render(context);
    }

    /// <summary>
    /// Somewhere the AUTHOR can read a diagnostic.
    ///
    /// Console.WriteLine goes nowhere he can see: this is a WinExe with no console, so every
    /// trace added to this file was invisible unless stdout happened to be redirected by a test
    /// harness. He asked what the log said and it said nothing, because it could not. Wired to
    /// the in-app log by the view model.
    /// </summary>
    public Action<string>? Trace;

    private void Note(string line)
    {
        Console.WriteLine(line);
        Trace?.Invoke(line);
    }

    // ---------------------------------------------------------------- GL lifetime

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = GL.GetApi(gl.GetProcAddress);
            _shader = new Shader(_gl, Shaders.MeshVertex, Shaders.MeshFragment);
            _lineShader = new Shader(_gl, Shaders.GridVertex, Shaders.GridFragment);
            // THE SLAB RECEIVES SHADOWS, which is the only reason it is a separate shader from
            // the grid: a grid line has no business sampling a shadow map.
            _solidShader = new Shader(_gl, Shaders.GridVertex, Shaders.ShadowFloorFragment);
            _depthShader = new Shader(_gl, Shaders.MeshVertex, Shaders.PickFragment);
            _textures = new TextureCache(_gl);
            BuildGrid();
            BuildFloor();
            Console.WriteLine($"[viewport] GL bound: {_gl.GetStringS(StringName.Version)}");

            // AND PUT BACK WHAT THE LAST DEINIT TOOK.
            //
            // His report: "tab away ... and come back to it, it de-renders the model and never
            // reloads it and you have to exit out of the tab and open the model again."
            //
            // Avalonia destroys the GL context when the control leaves the visual tree, which is
            // what switching document tabs does, and OnOpenGlDeinit takes every buffer, texture
            // and shader with it. Coming back re-ran this method and rebuilt the shaders -- but
            // _pending was consumed by the FIRST build, so Build() returned at its first line and
            // there was nothing on screen and no way to ask for it again.
            //
            // _asset survived the whole time. The fix is a re-queue, not a package read.
            if (_pendingFbx is null && _asset is { } again && _mesh is null) _pending = again;
        }
        catch (Exception e) { Fault = e.Message; Console.WriteLine("[viewport] GL FAILED: " + e); }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        // QUEUED, NOT DISCARDED, for the same reason as the primary above: this runs on a tab
        // switch as well as on a close, and the two are indistinguishable from in here.
        foreach (var a in _attached)
        {
            if (!_pendingAttach.Any(q => q.Key == a.Key))
                _pendingAttach.Add((a.Key, a.Name, a.Asset));
            a.Mesh.Dispose();
        }
        _attached.Clear();
        while (_disposeLater.TryDequeue(out var gone)) gone.Dispose();      // detached, and never drawn again since
        _mesh?.Dispose();
        _mesh = null;
        _shader?.Dispose();
        _shader = null;
        _lineShader?.Dispose();
        _lineShader = null;
        _solidShader?.Dispose();
        _solidShader = null;
        _depthShader?.Dispose();
        _depthShader = null;
        if (_gl is not null && _shadowTex != 0)
        {
            _gl.DeleteFramebuffer(_shadowFbo);
            _gl.DeleteTexture(_shadowTex);
            _shadowFbo = 0;
            _shadowTex = 0;
            _shadowReady = false;
        }
        if (_gl is not null)
        {
            if (_gridVao != 0) { _gl.DeleteVertexArray(_gridVao); _gl.DeleteBuffer(_gridVbo); }
            if (_boxVao != 0) { _gl.DeleteVertexArray(_boxVao); _gl.DeleteBuffer(_boxVbo); }
        }
        _gridVao = _gridVbo = _boxVao = _boxVbo = 0;
        _textures?.Dispose();
        _textures = null;
        if (_gl is not null && _boneTex != 0) _gl.DeleteTexture(_boneTex);
        _boneTex = 0;
        _gl = null;
    }

    // ---------------------------------------------------------------- grid and reference box

    /// <summary>
    /// The ground plane, 21x21 lines at 50cm, out to five metres.
    ///
    /// Built in GL space directly, like the ImGui viewport's: the line shader takes uView and
    /// uProj only, so there is no model matrix to convert through and UeToGl does not apply.
    /// </summary>
    private unsafe void BuildGrid()
    {
        if (_gl is null) return;
        const int half = 10;
        const float step = 50f;
        var pts = new List<float>();
        for (var i = -half; i <= half; i++)
        {
            float d = i * step, e = half * step;
            pts.AddRange([d, 0, -e, d, 0, e]);
            pts.AddRange([-e, 0, d, e, 0, d]);
        }
        _gridVerts = pts.Count / 3;
        (_gridVao, _gridVbo) = Lines(pts);
    }

    /// <summary>
    /// A SOLID FLOOR SLAB, five metres square, for the model to stand on.
    ///
    /// His correction, with a picture of Max: *"on the 3d square part, i believe i said that
    /// wrong, i wanted something like this"* -- a broad flat surface, not the one-metre wireframe
    /// cube I built from "a 3d square that's a pretty good size". A cube measures; a floor gives
    /// the eye a ground to read the silhouette against, which is the different job.
    ///
    /// TWO DRAW RANGES, NOT LIGHTING. The line shader carries a flat uColor and no normals, so
    /// the lid and the sides are drawn as separate ranges in two tones. That is what gives the
    /// slab its edge in the reference picture, and it costs neither a second shader nor a normal
    /// per vertex.
    ///
    /// ITS TOP SITS HALF A CENTIMETRE BELOW THE GRID, so the two cannot z-fight. At a scale where
    /// a character is 180cm that gap is invisible, and the grid reads as lying ON the slab --
    /// which is how the reference looks.
    /// </summary>
    private unsafe void BuildFloor()
    {
        if (_gl is null) return;
        const float s = 250f;         // five metres across, the middle ten cells of the grid
        const float top = -0.5f;      // just under the grid plane
        const float bottom = -9f;     // a slab, not a plane: the edge is what reads as solid

        Vector3[] c =
        [
            new(-s, top, -s), new(s, top, -s), new(s, top, s), new(-s, top, s),
            new(-s, bottom, -s), new(s, bottom, -s), new(s, bottom, s), new(-s, bottom, s),
        ];
        var pts = new List<float>();
        void Quad(int a, int b, int d, int e)
        {
            foreach (var i in new[] { a, b, d, a, d, e })
                pts.AddRange([c[i].X, c[i].Y, c[i].Z]);
        }

        Quad(0, 1, 2, 3);                       // the lid, drawn first and on its own
        _floorLid = pts.Count / 3;
        Quad(4, 7, 6, 5);                       // the underside
        Quad(0, 4, 5, 1); Quad(1, 5, 6, 2);     // and the four edges
        Quad(2, 6, 7, 3); Quad(3, 7, 4, 0);
        _boxVerts = pts.Count / 3;
        (_boxVao, _boxVbo) = Lines(pts);
    }

    /// <summary>Where the lid's triangles end and the sides begin, for the two-tone draw.</summary>
    private int _floorLid;

    /// <summary>A position-only line buffer — the one thing both of the above need.</summary>
    private unsafe (uint Vao, uint Vbo) Lines(List<float> pts)
    {
        var vao = _gl!.GenVertexArray();
        var vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        var arr = pts.ToArray();
        fixed (float* p = arr)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint) (arr.Length * sizeof(float)),
                           p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), null);
        _gl.BindVertexArray(0);
        return (vao, vbo);
    }

    /// <summary>
    /// Both, BEFORE the mesh, so a model drawn over them reads as standing on the floor rather
    /// than being striped by it. Blended, because a hard grid line fights the silhouette.
    /// </summary>
    /// <summary>
    /// The depth target: one layer of a 2D ARRAY, because the shader samples an array.
    ///
    /// The array is three deep to match `uShadowMat[3]` and only layer 0 is ever written; the
    /// splits are sent so wide that a fragment always chooses cascade 0. Keeping the shader's
    /// shape rather than narrowing it means the mesh fragment is the SAME shader the Level Editor
    /// uses, which is the whole reason the lighting matches between the two.
    /// </summary>
    private unsafe void BuildShadowTargets()
    {
        if (_gl is null || _shadowTex != 0) return;

        _shadowTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2DArray, _shadowTex);
        _gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.DepthComponent24,
                       ShadowSize, ShadowSize, 3, 0,
                       PixelFormat.DepthComponent, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter,
                         (int) TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter,
                         (int) TextureMagFilter.Linear);
        // A WHITE BORDER, so anything sampling outside the map comes back LIT. The other way
        // round puts a black wall around the edge of the shadowed region.
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS,
                         (int) TextureWrapMode.ClampToBorder);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT,
                         (int) TextureWrapMode.ClampToBorder);
        var white = stackalloc float[] { 1f, 1f, 1f, 1f };
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureBorderColor, white);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode,
                         (int) TextureCompareMode.CompareRefToTexture);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc,
                         (int) DepthFunction.Lequal);

        _shadowFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer,
                                    FramebufferAttachment.DepthAttachment, _shadowTex, 0, 0);
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
        var ok = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (ok != GLEnum.FramebufferComplete)
            Console.WriteLine($"[viewport] shadow framebuffer incomplete: {ok}");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Render the model's depth from the key light, fitted to what is actually drawn.
    ///
    /// THE SAME KEY THE SHADER LIGHTS WITH. The three-point rig's key is
    /// normalize(0.5, 0.8, 0.6) pointing TOWARDS the light, so the sun points the other way. A
    /// shadow cast from any other direction is a shadow that does not belong to the highlight.
    ///
    /// FITTED TO A SPHERE, not a box: a box fitted to the corners changes size as the light
    /// direction is composed and the edges crawl. Here the light never moves, so this is really
    /// about being rotation-independent of the MODEL's own bounds.
    ///
    /// CALLED IMMEDIATELY BEFORE THE MAIN DRAW, on purpose. The bone palette for this frame is
    /// already uploaded and bound to unit 3 by then, so the depth pass poses the mesh exactly as
    /// the colour pass will. Run earlier it would cast the previous frame's pose, which on a
    /// playing animation is a shadow that lags the model.
    /// </summary>
    private unsafe void BuildShadow(int restoreFbo, int vw, int vh, bool skinned)
    {
        if (_gl is null || _depthShader is null || _mesh is null) { _shadowReady = false; return; }
        if (!_shadows) { _shadowReady = false; return; }

        BuildShadowTargets();
        if (_shadowTex == 0) { _shadowReady = false; return; }

        // THE SAME KEY THE SHADER LIGHTS WITH. Shaders' three-point rig uses
        // normalize(0.5, 0.8, 0.6) pointing TOWARDS the light, so light travels the other way.
        var sun = Vector3.Normalize(new Vector3(-0.5f, -0.8f, -0.6f));

        var (lo, hi) = DrawnBounds();

        // THE BOX HAS TO HOLD THE RECEIVER, NOT JUST THE CASTER, and that is what the first
        // attempt got wrong. Fitted to the model alone the box was 82 units across centred at
        // y 165 -- a helmet at head height -- while the slab it casts onto is at y -0.5. The
        // floor was 165 units outside the map, sampled past the white border and came back LIT,
        // which looks exactly like a shadow pass that never ran.
        //
        // So the model's own corners are PROJECTED DOWN THE LIGHT onto the slab and included.
        // That is precisely the region a shadow can occupy: no larger, so the texels stay where
        // they are wanted, and no smaller, so nothing that casts is clipped out.
        var flo = lo;
        var fhi = hi;
        if (ShowFloor && sun.Y < -1e-3f)
        {
            const float floorTop = -0.5f;      // BuildFloor's lid, just under the grid plane
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? lo.X : hi.X,
                                         (i & 2) == 0 ? lo.Y : hi.Y,
                                         (i & 4) == 0 ? lo.Z : hi.Z);
                var travel = (floorTop - corner.Y) / sun.Y;
                if (travel <= 0f) continue;
                var lands = corner + sun * travel;
                flo = Vector3.Min(flo, lands);
                fhi = Vector3.Max(fhi, lands);
            }
        }

        var centre = (flo + fhi) * 0.5f;
        // A SPHERE, so the box does not change size with the model's rotation. A tenth of padding
        // keeps the 3x3 tap inside the map at the very edge of the fit.
        var radius = MathF.Max((fhi - flo).Length() * 0.5f, 1f) * 1.1f;
        var pull = radius;
        var up = MathF.Abs(sun.Y) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;
        var lightView = Matrix4x4.CreateLookAt(centre - sun * (radius + pull), centre, up);
        var proj = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, 1f,
                                                2f * radius + 2f * pull);

        // ALL THREE THE SAME. The shader picks a cascade by view distance and the splits below
        // are sent wide enough that it always picks the first.
        for (var c = 0; c < 3; c++) _shadowMat[c] = lightView * proj;
        _shadowTexel = 2f * radius / ShadowSize;

        // SAID ONCE, AND IT SAYS SO. The fit is the whole of whether a shadow lands: if the
        // receiver is outside the ortho box it is clipped and comes back lit, which looks exactly
        // like a map that was never drawn.
        //
        // "FIRST" IS LOAD BEARING IN THAT LABEL. This fires on the first frame, which is before
        // the driver flags have applied a floor or an attachment -- and reading it as the current
        // fit cost a wrong conclusion about exactly this bug.
        if (!_shadowSaid)
        {
            _shadowSaid = true;
            Console.WriteLine($"[viewport] FIRST shadow fit: bounds {lo} to {hi}, centre {centre}, " +
                              $"radius {radius:0.###}, texel {_shadowTexel:0.####}, " +
                              $"ortho depth 1 to {2f * radius + 2f * pull:0.###}, " +
                              $"light at {centre - sun * (radius + pull)}");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer,
                                    FramebufferAttachment.DepthAttachment, _shadowTex, 0, 0);
        _gl.Viewport(0, 0, ShadowSize, ShadowSize);
        _gl.Clear((uint) ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);

        _depthShader.Use();
        _depthShader.Set("uView", lightView);
        _depthShader.Set("uProj", proj);
        _depthShader.Set("uModel", RenderMesh.UeToGl);
        _depthShader.Set("uSkinned", skinned ? 1 : 0);
        _depthShader.Set("uBoneTex", 3);
        _depthShader.Set("uPickColor", Vector3.One);
        _mesh.DrawGeometry(_depthShader);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint) restoreFbo);
        _gl.Viewport(0, 0, (uint) Math.Max(vw, 1), (uint) Math.Max(vh, 1));
        _shadowReady = true;
    }

    /// <summary>Hand a shader the map and the matrices, or tell it there is none.</summary>
    private void BindShadow(Shader shader)
    {
        if (_gl is null) return;
        if (!_shadowReady || !_shadows || _shadowTex == 0)
        { shader.Set("uShadowOn", 0); return; }

        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2DArray, _shadowTex);
        shader.Set("uShadowMap", 5);
        shader.Set("uShadowOn", 1);
        for (var c = 0; c < 3; c++) shader.Set($"uShadowMat[{c}]", _shadowMat[c]);
        // WIDE ENOUGH THAT CASCADE 0 ALWAYS WINS, and the last split never cuts the shadow off.
        shader.Set("uShadowSplit", new Vector3(1e9f, 1e9f, 1e9f));
        shader.Set("uShadowTexel", new Vector3(_shadowTexel, _shadowTexel, _shadowTexel));
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    private void DrawFloor(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_gl is null || _lineShader is null) return;
        if (!ShowGrid && !ShowFloor) return;

        // THE SLAB FIRST AND OPAQUE, then the grid blended over it. The other order draws
        // half-transparent lines against the clear colour and then paints the floor across them.
        if (ShowFloor && _boxVao != 0 && _solidShader is not null)
        {
            _solidShader.Use();
            _solidShader.Set("uView", view);
            _solidShader.Set("uProj", proj);
            BindShadow(_solidShader);
            _gl.BindVertexArray(_boxVao);
            _solidShader.Set("uColor", new Vector3(0.62f, 0.68f, 0.78f));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint) _floorLid);
            // The sides darker, which is the whole of the shading and enough of it.
            _solidShader.Set("uColor", new Vector3(0.38f, 0.43f, 0.52f));
            _gl.DrawArrays(PrimitiveType.Triangles, _floorLid, (uint) (_boxVerts - _floorLid));
        }

        if (ShowGrid && _gridVao != 0)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _lineShader.Use();
            _lineShader.Set("uView", view);
            _lineShader.Set("uProj", proj);
            _lineShader.Set("uColor", new Vector3(0.42f, 0.44f, 0.50f));
            _gl.BindVertexArray(_gridVao);
            _gl.DrawArrays(PrimitiveType.Lines, 0, (uint) _gridVerts);
            _gl.Disable(EnableCap.Blend);
        }
        _gl.BindVertexArray(0);
    }

    // ---------------------------------------------------------------- the frame

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || _shader is null) return;

        var w = (float) Bounds.Width;
        var h = (float) Bounds.Height;
        if (!_said) { _said = true; Console.WriteLine($"[viewport] first frame {w}x{h}"); }
        _gl.Viewport(0, 0, (uint) Math.Max(1, w), (uint) Math.Max(1, h));

        _gl.ClearColor(0.10f, 0.11f, 0.13f, 1f);
        _gl.Clear((uint) (ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
#if DEV
        if (Environment.GetCommandLineArgs().Contains("--nodepth")) _gl.Disable(EnableCap.DepthTest);
        else _gl.Enable(EnableCap.DepthTest);
#else
        _gl.Enable(EnableCap.DepthTest);
#endif

        Build();

        // RE-FRAME HERE, NOT IN Build. Build's two framing calls sit inside the paths that build
        // a PRIMARY, and it returns at "is there a pending asset?" long before them -- so an
        // attachment could clear _framed and nothing would ever read it. Measured: a torso with a
        // helmet and legs on it still reported radius 57.74, the torso alone.
        //
        // A flag set and never acted on is the shape this Studio keeps producing, and it looks
        // identical to one that works.
        if (!_framed && _mesh is not null)
            Frame(Bounds.Height <= 0 ? 16f / 9f : (float) (Bounds.Width / Bounds.Height));

#if DEV
        // THE ARGUMENTS AS THIS PROCESS ACTUALLY RECEIVED THEM, said once. PowerShell's
        // Start-Process -ArgumentList has split an unquoted argument on its spaces twice in this
        // project, and both times the flag simply never arrived while the run looked normal — so
        // a diagnostic switch that appears to do nothing must be able to prove it was passed.
        var args = Environment.GetCommandLineArgs();
        if (!_saidArgs) { _saidArgs = true; Console.WriteLine("[viewport] args: " + string.Join(" ", args)); }

        if (args.Contains("--tri")) DrawTestTriangle();
#endif

        if (_mesh is not null)
        {
            _shader.Use();
            _shader.Set("uView", Camera.View);
            _shader.Set("uProj", Camera.Projection(h <= 0 ? 1f : w / h));
            _shader.Set("uCamPos", Camera.Position);
            // --unlit stays as an override because it is one of the two switches that struck
            // lighting off the list when the model would not draw at all.
#if DEV
            _shader.Set("uMode", args.Contains("--unlit") ? 1 : _mode.Mode);
#else
            _shader.Set("uMode", _mode.Mode);
#endif

            // THE FRAME IS THE CLOCK. A DispatcherTimer at 16ms delivered a tenth of a second's
            // worth of time per second in the level editor, starved by the very rendering it was
            // animating — and every missed tick clamped away as a stall. Driving it from the
            // render loop cannot starve itself, and asking for the next frame is what keeps it
            // running.
            var now = _clock.Elapsed.TotalSeconds;
            var dt = Math.Clamp(now - _lastTick, 0, 0.25);
            _lastTick = now;

            var skinned = _anim.HasAnim && _mesh.Skeleton is not null;
            if (skinned)
            {
                if (_anim.Playing) _anim.Advance(dt);
                UploadBones(_anim.Palette(_mesh.Skeleton!));
                _gl.ActiveTexture(TextureUnit.Texture3);
                _gl.BindTexture(TextureTarget.Texture2D, _boneTex);
                _shader.Set("uBoneTex", 3);
            }

            // WIREFRAME IS A RASTERISER STATE, not a shading one, which is why it rides along
            // with a mode rather than being one. RESET EVERY FRAME regardless: leaving the
            // polygon mode on Line would draw every later frame as lines, and a mode switch that
            // half-works is worse than one that does not.
            _gl.PolygonMode(TriangleFace.FrontAndBack,
                            _mode.Wire ? PolygonMode.Line : PolygonMode.Fill);
            _shader.Set("uSkinned", skinned ? 1 : 0);
            _shader.Set("uSelected", 0);

            // THE SHADER'S OWN STUDIO RIG, and no shadows: a cascade needs a scene to cast onto
            // and there is one object here.
            _shader.Set("uLevelLit", 0);
            _shader.Set("uSunCount", 0);
            _shader.Set("uShadowOn", 0);
            _shader.Set("uShadowDebug", 0);

#if DEV
            // --flatmesh: THE REAL MESH, FITTED STRAIGHT INTO CLIP SPACE, camera bypassed.
            //
            // The triangle test proved the shader and the GL state rasterise, so what is left is
            // "the vertices do not land on screen" or "the draw never puts vertices through".
            // This separates those two: scale the model into NDC by its own bounds and make view
            // and projection identity. If it appears, the geometry and the draw are sound and
            // the camera matrices are the fault. If it does not, the fault is in the draw.
            if (args.Contains("--flatmesh"))
            {
                // DrawnBounds, not the raw bounds — this switch reported 0% for exactly the
                // same reason the camera did, because it centred on the same wrong point.
                var (lo, hi) = DrawnBounds();
                var mid = (lo + hi) * 0.5f;
                var rad = MathF.Max((hi - lo).Length() * 0.5f, 1e-4f);
                // Row-vector composition, the System.Numerics order: v * UeToGl, then centred,
                // then scaled to half of NDC.
                var fit = RenderMesh.UeToGl
                          * Matrix4x4.CreateTranslation(-mid)
                          * Matrix4x4.CreateScale(0.5f / rad);
                _shader.Set("uModel", fit);
                _shader.Set("uView", Matrix4x4.Identity);
                _shader.Set("uProj", Matrix4x4.Identity);
                _shader.Set("uMode", 2);   // normals: bright, and needs no texture
            }
            else
            {
                _shader.Set("uModel", RenderMesh.UeToGl);
            }
#else
            _shader.Set("uModel", RenderMesh.UeToGl);
#endif

#if DEV
            // --probe: WHERE THE BOUNDS ACTUALLY LAND, computed on the CPU through the very
            // matrices the shader was handed. A corner whose NDC is outside [-1,1] is off
            // screen, and that is a number rather than a guess about matrix conventions.
            if (args.Contains("--probe") && !_probed)
            {
                _probed = true;
                var vp = Camera.View * Camera.Projection(h <= 0 ? 1f : w / h);
                var (lo, hi) = DrawnBounds();
                for (var i = 0; i < 8; i++)
                {
                    var p = new Vector3((i & 1) == 0 ? lo.X : hi.X,
                                        (i & 2) == 0 ? lo.Y : hi.Y,
                                        (i & 4) == 0 ? lo.Z : hi.Z);
                    var clip = Vector4.Transform(new Vector4(p, 1f), vp);
                    var ndc = clip.W != 0f
                        ? new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W)
                        : new Vector3(float.NaN);
                    var onScreen = clip.W > 0f && MathF.Abs(ndc.X) <= 1 && MathF.Abs(ndc.Y) <= 1
                                   && ndc.Z >= -1 && ndc.Z <= 1;
                    Console.WriteLine($"[probe] corner {p} -> clip {clip} ndc {ndc} on={onScreen}");
                }
            }

            // --vbo: THE POSITIONS THAT ARE ACTUALLY ON THE GPU, read back and measured.
            //
            // Every reading so far has come from BoundsMin/BoundsMax, which the loader computes
            // from the asset — not from the buffer the draw call reads. If those two disagree
            // about anything, including which axis is up, the camera is framed on one and the
            // triangles are somewhere else entirely, and every number printed so far would still
            // look right. This is the one measurement that compares them.
            //
            // The buffer id is found by scanning rather than exposed, so that a diagnostic in
            // this application does not put a debug accessor on the shared renderer.
            if (args.Contains("--vbo") && !_vboSaid)
            {
                _vboSaid = true;
                var want = _mesh.VertexCount * 18 * sizeof(float);
                Console.WriteLine($"[vbo] looking for a buffer of {want} bytes " +
                                  $"({_mesh.VertexCount} verts x 18 floats)");
                for (uint id = 1; id <= 64; id++)
                {
                    if (!_gl.IsBuffer(id)) continue;
                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, id);
                    _gl.GetBufferParameter(BufferTargetARB.ArrayBuffer,
                                           BufferPNameARB.BufferSize, out int size);
                    if (size != want) { Console.WriteLine($"[vbo] id={id} size={size} (not it)"); continue; }

                    var floats = new float[_mesh.VertexCount * 18];
                    fixed (float* q = floats)
                        _gl.GetBufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint) size, q);

                    var bmin = new Vector3(float.MaxValue);
                    var bmax = new Vector3(float.MinValue);
                    var bad = 0;
                    for (var vi = 0; vi < _mesh.VertexCount; vi++)
                    {
                        var p = new Vector3(floats[vi * 18], floats[vi * 18 + 1], floats[vi * 18 + 2]);
                        if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z)) { bad++; continue; }
                        bmin = Vector3.Min(bmin, p);
                        bmax = Vector3.Max(bmax, p);
                    }
                    Console.WriteLine($"[vbo] id={id} size={size}  ACTUAL positions {bmin} .. {bmax}  nan={bad}");
                    Console.WriteLine($"[vbo] loader says BoundsMin/Max {_mesh.BoundsMin} .. {_mesh.BoundsMax}");
                    Console.WriteLine($"[vbo] first three verts: " +
                                      $"({floats[0]},{floats[1]},{floats[2]}) " +
                                      $"({floats[18]},{floats[19]},{floats[20]}) " +
                                      $"({floats[36]},{floats[37]},{floats[38]})");
                }
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            }

            // --nocut: TURN THE ALPHA CLIP OFF AND SEE WHAT COMES BACK.
            //
            // Draw sets uAlphaCut per section, and the shader's ONE discard is
            // `uAlphaCut >= 0 && cutAlpha < uAlphaCut`, which runs before every mode branch. A
            // masked material whose diffuse alpha reads zero therefore throws away every
            // fragment of a mesh that is loaded, framed, on screen and drawing — which is
            // exactly the state this viewport is in.
            if (args.Contains("--nocut"))
                foreach (var sec in _mesh.Sections) sec.AlphaCut = false;
#endif

            // THE TINT, when one is chosen. The mask comes from the SECTION, because one mesh can
            // mix a tinted piece with an untinted one; Draw looks it up per section and leaves
            // the rest on their own albedo.
            // MASK 0 MEANS "EACH SECTION'S OWN", which is what this comment always claimed and
            // what the code did not do: it took the FIRST section with a mask and Draw bound that
            // one for every section, so on a three-section torso the strap kit and the helmet
            // were painted through the torso's mask with their own UVs.
            var tint = _tinting && CanTint
                ? new RenderMesh.Tint(true, 0, _tint, null)
                : default;

            // THE SHADOW PASS, HERE AND NOT EARLIER. The bone palette for this frame is already
            // uploaded and bound to unit 3, so the depth pass poses the mesh exactly as the line
            // below will. Run at the top of the frame it would cast the previous pose, which on a
            // playing animation is a shadow that lags its own model.
            //
            // Uniform values live with the PROGRAM, so switching to the depth shader and back
            // costs nothing that was set above: only the active program changes.
            BuildShadow(fb, (int) w, (int) h, skinned);
            _shader.Use();
            BindShadow(_shader);

            _mesh.Draw(_shader, tint);

            // THE ATTACHMENTS, AFTER THE PRIMARY AND FOR A REASON: PaletteOn reads the host's
            // LastModel, which the line above has just written by posing it. Drawn first they
            // would follow the previous frame, which on a playing animation is a limb-width of
            // lag between a torso and its own arms.
            //
            // Each divides by its OWN bind and multiplies by the HOST's current model transform.
            // That is the rule the ImGui viewport already uses and the one a neck seam between a body
            // and its collar proved: parts can be bound in different poses, and a part sitting at its own bind
            // while the body is posed comes apart at the join. See Skeleton.PaletteOn.
            foreach (var extra in _attached)
            {
                var host = _mesh.Skeleton;
                var mine = extra.Mesh.Skeleton;
                var rigged = mine is { Count: > 0 } && host is { Count: > 0 };
                if (rigged)
                {
                    UploadBones(mine!.PaletteOn(host!));
                    _gl.ActiveTexture(TextureUnit.Texture3);
                    _gl.BindTexture(TextureTarget.Texture2D, _boneTex);
                    _shader.Set("uBoneTex", 3);
                }
                _shader.Set("uSkinned", rigged ? 1 : 0);
                // UeToGl even under --flatmesh: that switch fits the PRIMARY into clip space by
                // its own bounds, and an attachment put through the same matrix would be framed
                // as though it were the whole model.
                _shader.Set("uModel", RenderMesh.UeToGl);

                // THE SAME TREATMENT THE PRIMARY GETS, his ask: "i would like the added parts to
                // be fully shaded with all their materials applied". The maps were bound section
                // by section when it was built, so the albedo, normal and ORME are already on it;
                // what was missing was the TINT, which was passed as default -- every attachment
                // drew on its raw albedo while the torso beside it wore the chosen colours.
                //
                // FROM ITS OWN MASK, not the host's. A tint is aimed at the section that carries
                // the mask, and handing a helmet the torso's would paint through the wrong UVs.
                // Its own sections' own masks, for the same reason. A hung piece with no mask
                // of its own is now left alone rather than painted through its neighbour's.
                var extraTint = _tinting
                    ? new RenderMesh.Tint(true, 0, _tint, null)
                    : default;
                extra.Mesh.Draw(_shader, extraTint);
            }
        }

        // THE BACKDROP LAST, AFTER THE MODEL HAS WRITTEN ITS DEPTH -- and this is not a
        // preference, it is the only order that works here.
        //
        // MEASURED: --probe puts all eight of the torso's corners on screen at an NDC z between
        // 0.9921 and 0.9965. The camera's near plane is 0.697 and its far is 7787, an 11,000:1
        // ratio, so a perspective divide crushes the ENTIRE model into the last 0.4% of the depth
        // range -- and the slab, 177cm to 520cm from the eye, lands in that same sliver. Drawn
        // first it wrote depth the model could not then beat, and a five-metre floor covering half
        // the frame hid the whole torso.
        //
        // The grid got away with it because a few lost pixels on a thin line are invisible, which
        // is exactly why this shipped looking fine until the slab was drawn the same way.
        //
        // Drawing it afterwards costs nothing and is the usual order for a backdrop anyway: the
        // model's depth is already down, so the floor fills in around it and loses every tie.
        DrawFloor(Camera.View, Camera.Projection(h <= 0 ? 1f : w / h));

        // WAIT FOR THE GPU BEFORE AVALONIA COMPOSITES. Without it the frame is composited while
        // draws are still in flight, and pieces of the model come and go between frames — the
        // Level Editor spent a long time on "meshes keep rendering and de-rendering" before this
        // line was the answer. Finish, not Flush: Flush only promises the commands were issued,
        // and the compositor may be another context entirely.
        _gl.Finish();

        // THE GRAB IS OUTSIDE THE MESH CHECK. It used to sit behind an early return on a null
        // mesh, so the one case most worth photographing — an asset that failed to load — took
        // the shot request and silently never answered it.
        if (_shotTo is not null) Grab((int) w, (int) h);

        if (_maskAsk) CountMasks();

        // KEEP ASKING while something is moving. The control draws on demand, so a playing
        // animation that does not request the next frame plays exactly one.
        if (_anim.HasAnim && _anim.Playing)
        {
            Ticked?.Invoke();
            RequestNextFrameRendering();
        }
    }

#if DEV
    // ---------------------------------------------------------------- the triangle test

    private bool _saidArgs;
    private bool _probed;
    private bool _vboSaid;
    private uint _triVao, _triVbo, _triProg;

    /// <summary>
    /// TWO HARDCODED TRIANGLES IN CLIP SPACE — the one experiment that separates "no fragments"
    /// from "no vertices" without reading another line of the renderer.
    ///
    /// LEFT, magenta, through a three-line program of its own and NO matrices at all. If this
    /// does not appear then nothing this control draws can rasterise, and the fault is GL state
    /// rather than anything about the mesh.
    ///
    /// RIGHT, through THE MESH SHADER with identity uModel/uView/uProj, so aPos is already clip
    /// space and the same three corners are used. If the left appears and the right does not,
    /// the fault is inside that shader or the uniforms it was handed.
    ///
    /// uMode 2 for the right-hand one deliberately: the normals view outputs N*0.5+0.5 and
    /// samples no texture at all, so it cannot come out black for want of a binding and be
    /// misread as a triangle that never drew.
    /// </summary>
    private void DrawTestTriangle()
    {
        if (_gl is null || _shader is null) return;

        if (_triVao == 0)
        {
            // Ordinary strings and not raw ones: a line that BEGINS with "#version" inside a raw
            // string is read as a preprocessor directive when the surrounding "#if DEV" is off,
            // and the release build then fails on shader source.
            const string vs = "#version 330 core\n" +
                              "layout (location = 0) in vec3 aPos;\n" +
                              "void main() { gl_Position = vec4(aPos, 1.0); }";
            const string fs = "#version 330 core\n" +
                              "out vec4 FragColor;\n" +
                              "void main() { FragColor = vec4(1.0, 0.0, 1.0, 1.0); }";

            var v = _gl.CreateShader(ShaderType.VertexShader);
            _gl.ShaderSource(v, vs);
            _gl.CompileShader(v);
            var f = _gl.CreateShader(ShaderType.FragmentShader);
            _gl.ShaderSource(f, fs);
            _gl.CompileShader(f);
            _triProg = _gl.CreateProgram();
            _gl.AttachShader(_triProg, v);
            _gl.AttachShader(_triProg, f);
            _gl.LinkProgram(_triProg);
            _gl.GetProgram(_triProg, ProgramPropertyARB.LinkStatus, out var linked);
            Console.WriteLine($"[tri] trivial program linked={linked} log=\"{_gl.GetProgramInfoLog(_triProg)}\"");
            _gl.DeleteShader(v);
            _gl.DeleteShader(f);

            // Six vertices in the MESH layout exactly — 18 floats, aPos 0, aNormal 3, aUV 6,
            // aBoneIdx 8, aBoneWeight 12, aUV1 16 — so one VAO serves both programs. Vertices
            // 0-2 are the left triangle, 3-5 the right.
            var pts = new[]
            {
                (-0.9f, -0.5f), (-0.1f, -0.5f), (-0.5f, 0.5f),
                ( 0.1f, -0.5f), ( 0.9f, -0.5f), ( 0.5f, 0.5f),
            };
            var data = new float[6 * 18];
            for (var i = 0; i < 6; i++)
            {
                var o = i * 18;
                data[o + 0] = pts[i].Item1;
                data[o + 1] = pts[i].Item2;
                data[o + 2] = 0f;
                data[o + 5] = 1f;    // aNormal = (0,0,1) -> the normals view shows (0.5,0.5,1)
                data[o + 12] = 1f;   // aBoneWeight.x, harmless with uSkinned = 0
            }

            _triVao = _gl.GenVertexArray();
            _gl.BindVertexArray(_triVao);
            _triVbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _triVbo);
            fixed (float* p = data)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint) (data.Length * sizeof(float)),
                               p, BufferUsageARB.StaticDraw);

            const uint stride = 18 * sizeof(float);
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
        }

        // WHAT THE STATE ACTUALLY IS when a frame is drawn, read back from the driver rather
        // than assumed from the code. Culling, scissor and the colour mask each turn a perfect
        // draw call into an empty frame, and none of them says anything when it does.
        _gl.GetInteger(GetPName.CullFace, out var cull);
        _gl.GetInteger(GetPName.CullFaceMode, out var cullMode);
        _gl.GetInteger(GetPName.FrontFace, out var front);
        _gl.GetInteger(GetPName.ScissorTest, out var scissor);
        var box = new int[4];
        fixed (int* b = box) _gl.GetInteger(GetPName.ScissorBox, b);
        var mask = new int[4];
        fixed (int* m = mask) _gl.GetInteger(GetPName.ColorWritemask, m);
        _gl.GetInteger(GetPName.DepthFunc, out var depthFunc);
        _gl.GetInteger(GetPName.Blend, out var blend);
        _gl.GetInteger(GetPName.DrawFramebufferBinding, out var fbo);
        var vp = new int[4];
        fixed (int* p = vp) _gl.GetInteger(GetPName.Viewport, p);
        Console.WriteLine($"[tri] state cull={cull} mode={cullMode} front={front} " +
                          $"scissor={scissor} box=[{box[0]},{box[1]},{box[2]},{box[3]}] " +
                          $"colourMask=[{mask[0]},{mask[1]},{mask[2]},{mask[3]}] " +
                          $"depthFunc={depthFunc} blend={blend} fbo={fbo} " +
                          $"viewport=[{vp[0]},{vp[1]},{vp[2]},{vp[3]}]");

        _gl.BindVertexArray(_triVao);

        // LEFT: no matrices, no mesh shader, nothing but three corners and a colour.
        _gl.UseProgram(_triProg);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // RIGHT: the mesh shader, identity everywhere, so aPos IS clip space.
        _shader.Use();
        _shader.Set("uModel", Matrix4x4.Identity);
        _shader.Set("uView", Matrix4x4.Identity);
        _shader.Set("uProj", Matrix4x4.Identity);
        _shader.Set("uMode", 2);
        _shader.Set("uSkinned", 0);
        _shader.Set("uSelected", 0);
        _shader.Set("uLevelLit", 0);
        _shader.Set("uSunCount", 0);
        _shader.Set("uShadowOn", 0);
        _shader.Set("uShadowDebug", 0);
        _gl.DrawArrays(PrimitiveType.Triangles, 3, 3);

        var err = _gl.GetError();
        if (err != GLEnum.NoError) Console.WriteLine($"[tri] GL error after the draws: {err}");
    }
#endif

    // ---------------------------------------------------------------- photographing it

    private string? _shotTo;

    private bool _maskAsk;

    /// <summary>What the last mask count found. Empty until one has been asked for.</summary>
    public string MaskReport { get; private set; } = "";

    /// <summary>
    /// Count the UPLOADED tint mask's channels, on the GL thread.
    ///
    /// THE POINT IS "UPLOADED". The same count was already done by extracting the mask to TGA and
    /// reading it with an image library, and it produced a clean, disjoint answer that did not
    /// match the screen. TGA stores BGR, so that reading could not distinguish "the visor is the
    /// blue channel" from "the exporter swapped my red and blue" — and the whole question is
    /// which channel the SHADER sees. So this asks the texture the shader samples, through the
    /// same context, and no file format sits in between.
    /// </summary>
    public void AskMaskChannels()
    {
        _maskAsk = true;
        RequestNextFrameRendering();
    }

    private unsafe void CountMasks()
    {
        _maskAsk = false;
        if (_gl is null || _mesh is null) { MaskReport = "no mesh"; return; }

        var lines = new List<string>();
        foreach (var s in _mesh.Sections)
        {
            if (s.TintMask == 0) { lines.Add($"{s.MaterialName}: no tint mask"); continue; }

            _gl.BindTexture(TextureTarget.Texture2D, s.TintMask);
            _gl.GetTexLevelParameter(TextureTarget.Texture2D, 0,
                                     GetTextureParameter.TextureWidth, out int tw);
            _gl.GetTexLevelParameter(TextureTarget.Texture2D, 0,
                                     GetTextureParameter.TextureHeight, out int th);
            if (tw <= 0 || th <= 0)
            { lines.Add($"{s.MaterialName}: mask #{s.TintMask} has no level 0"); continue; }

            var pixels = new byte[(long) tw * th * 4];
            fixed (byte* p = pixels)
                _gl.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba,
                                PixelType.UnsignedByte, p);

            // SAMPLED ON A GRID. A 2048 square is four million reads and the answer is wanted to
            // a fraction of a percent, not exactly.
            var step = Math.Max(1, tw / 512);
            long tot = 0, r = 0, g = 0, b = 0, rg = 0, rb = 0, gb = 0, none = 0;
            for (var y = 0; y < th; y += step)
                for (var x = 0; x < tw; x += step)
                {
                    var i = ((long) y * tw + x) * 4;
                    bool R = pixels[i] > 127, G = pixels[i + 1] > 127, B = pixels[i + 2] > 127;
                    tot++;
                    if (R) r++;
                    if (G) g++;
                    if (B) b++;
                    if (R && G) rg++;
                    if (R && B) rb++;
                    if (G && B) gb++;
                    if (!(R || G || B)) none++;
                }

            double P(long n) => tot == 0 ? 0 : 100.0 * n / tot;

            // AND THE MEANS, which is the number a threshold hides. Counting "B > 127" said the
            // blue channel was 2.20% of the mask and the whole helmet still went dark: a channel
            // can sit just under half everywhere and never be counted once, while the shader
            // mixes by it as a WEIGHT at every pixel. These channels are weights, not regions.
            double sr = 0, sg = 0, sb = 0;
            for (var y = 0; y < th; y += step)
                for (var x = 0; x < tw; x += step)
                {
                    var i = ((long) y * tw + x) * 4;
                    sr += pixels[i]; sg += pixels[i + 1]; sb += pixels[i + 2];
                }
            lines.Add($"{s.MaterialName}: mask #{s.TintMask} {tw}x{th}  " +
                      $"MEAN r={sr / tot / 255.0:F3} g={sg / tot / 255.0:F3} b={sb / tot / 255.0:F3}");
            lines.Add($"{s.MaterialName}: over half  " +
                      $"R={P(r):F2}% G={P(g):F2}% B={P(b):F2}%  " +
                      $"overlap rg={P(rg):F2}% rb={P(rb):F2}% gb={P(gb):F2}%  " +
                      $"untinted={P(none):F2}%");
        }
        MaskReport = string.Join(Environment.NewLine + "    ", lines);
    }

    /// <summary>
    /// Write the next frame to a PNG.
    ///
    /// NECESSARY, not a convenience. Avalonia draws GL into a surface the window compositor owns,
    /// and PrintWindow cannot read it — a window-targeted grab of this control comes back BLACK
    /// however well it is drawing. The only honest picture of this viewport is the framebuffer
    /// itself, which is what the Level Editor's --glshot exists for and why this is here too.
    /// </summary>
    public void ShotTo(string path)
    {
        _shotTo = path;
        RequestNextFrameRendering();
    }

    private void Grab(int w, int h)
    {
        var path = _shotTo;
        _shotTo = null;
        if (_gl is null || path is null || w <= 0 || h <= 0) return;

        try
        {
            var pixels = new byte[w * h * 4];
            fixed (byte* b = pixels)
                _gl.ReadPixels(0, 0, (uint) w, (uint) h, PixelFormat.Rgba, PixelType.UnsignedByte, b);

            var info = new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888,
                                                 SkiaSharp.SKAlphaType.Opaque);
            using var bitmap = new SkiaSharp.SKBitmap(info);
            var dst = (byte*) bitmap.GetPixels();
            // GL's origin is bottom-left and a PNG's is top-left, so the rows go back the other way.
            for (var y = 0; y < h; y++)
            {
                var src = (h - 1 - y) * w * 4;
                for (var x = 0; x < w * 4; x += 4)
                {
                    dst[y * w * 4 + x + 0] = pixels[src + x + 0];
                    dst[y * w * 4 + x + 1] = pixels[src + x + 1];
                    dst[y * w * 4 + x + 2] = pixels[src + x + 2];
                    dst[y * w * 4 + x + 3] = 255;
                }
            }

            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 95);
            using var file = File.OpenWrite(path);
            data.SaveTo(file);

            // HOW MUCH OF THE FRAME IS THE MODEL, as a number. "It looks wrong" is a judgement
            // only he can make, but "the model covers 0.3% of the frame" is a fact that says the
            // camera is in the wrong place before anybody has to look.
            //
            // MEASURED AS A DIFFERENCE FROM THE CLEAR COLOUR, IN EITHER DIRECTION. The first
            // version of this counted only pixels BRIGHTER than the background, which cannot see
            // a model that draws black — and black is exactly what an unlit shader with no
            // texture bound produces. It would have reported 0% for a model that was drawing
            // perfectly well in the wrong colour, and that is the same shape of fault as every
            // other instrument in this project that measured the wrong thing and was believed.
            int clearR = (int) (0.10f * 255), clearG = (int) (0.11f * 255), clearB = (int) (0.13f * 255);
            var differs = 0;
            long sum = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var d = Math.Abs(pixels[i] - clearR)
                      + Math.Abs(pixels[i + 1] - clearG)
                      + Math.Abs(pixels[i + 2] - clearB);
                if (d > 8) differs++;
                sum += (pixels[i] * 299 + pixels[i + 1] * 587 + pixels[i + 2] * 114) / 1000;
            }
            Console.WriteLine($"[viewport] shot {w}x{h} -> {path}, " +
                              $"{differs * 100.0 / (w * h):0.##}% of the frame differs from the clear " +
                              $"colour, mean luma {sum / (double) (w * h) / 255.0:0.###}");
        }
        catch (Exception e) { Console.WriteLine("[viewport] shot failed: " + e.Message); }
    }

    /// <summary>
    /// Delete every texture in this view's cache that no section on screen -- the model or anything hung on it --
    /// still points at. GL thread (Build runs inside the render callback). Every texture handle this view holds lives
    /// in a RenderSection (the tint colours are plain numbers and the shadow map is not the cache's), so the sections
    /// of the model and its attachments ARE the live set.
    /// </summary>
    private void ReleaseUnusedTextures(string why)
    {
        if (_textures is null) return;
        var sections = new List<RenderSection>();
        if (_mesh is not null) sections.AddRange(_mesh.Sections);
        foreach (var a in _attached) sections.AddRange(a.Mesh.Sections);
        var (n, bytes) = _textures.ReleaseUnused(sections);
        if (n > 0) Console.WriteLine($"[viewport] released {n} texture(s), {bytes / 1048576.0:N0} MB (estimate) that nothing on screen uses ({why})");
    }

    /// <summary>
    /// Turn the waiting asset into geometry, once, on the thread that owns the GL.
    ///
    /// The MATERIALS are bound here too. TextureCache.BindMaterial resolves the material's
    /// parameters and uploads its textures — the diffuse, the mask and the normal, and whatever
    /// else the family wants — which is what "with all of it applied" means. Its own notes say a
    /// level editor has thousands of meshes where the model viewer has one, so doing all of them
    /// in a single frame is exactly the case it was written for.
    /// </summary>
    private void Build()
    {
        while (_disposeLater.TryDequeue(out var gone)) gone.Dispose();

        // AN IMPORTED FILE, when one is waiting. Same place as a game asset, because both end as
        // a RenderMesh and everything after this point is identical.
        if (_pendingFbx is { } want && _gl is not null)
        {
            _pendingFbx = null;
            _mesh?.Dispose();
            _mesh = null;
            SectionCount = TexturedSections = 0;
            Fault = null;

            _mesh = RenderMesh.FromFbx(_gl, want.Path, want.Mapping, 0f, want.Rig, out var fbxWhy);
            if (_mesh is null)
            {
                Fault = fbxWhy ?? "that file holds no geometry this renderer can read";
                Note($"[viewport] fbx {want.Path}: {Fault}");
                Loaded?.Invoke();
                return;
            }

            foreach (var section in _mesh.Sections)
            {
                try { _textures?.BindMaterial(section); }
                catch (Exception e) { Fault ??= e.Message; }
                SectionCount++;
                if (section.Diffuse != 0) TexturedSections++;
            }

            ReleaseUnusedTextures("the imported file replaced the model");

            Materials = _mesh.Sections
                .Select(s => new MaterialRow(s.MaterialName, s.Diffuse != 0,
                                             s.NormalMap != 0, s.Orme != 0))
                .ToList();

            if (!_framed)
                Frame(Bounds.Height <= 0 ? 16f / 9f : (float) (Bounds.Width / Bounds.Height));
            Note($"[viewport] fbx {System.IO.Path.GetFileName(want.Path)}: " +
                 $"{SectionCount} section(s), {_mesh.VertexCount} verts");
            Loaded?.Invoke();
            LodChanged?.Invoke();
            return;
        }

        // ATTACHMENTS FIRST, and BEFORE the early return below -- they arrive without a new
        // primary, so building them after "is there a pending asset?" would mean a helmet only
        // appeared the next time the torso itself was reloaded.
        if (_pendingAttach.Count > 0 && _gl is not null)
        {
            var queued = _pendingAttach.ToArray();
            _pendingAttach.Clear();
            foreach (var (key, name, queuedAsset) in queued)
            {
                RenderMesh? built = null;
                try
                {
                    built = queuedAsset switch
                    {
                        USkeletalMesh sk => RenderMesh.FromSkeletal(_gl, sk, 0),
                        UStaticMesh st => RenderMesh.FromStatic(_gl, st, 0),
                        _ => null,
                    };
                }
                catch (Exception e) { Note($"[viewport] attach {name}: {e.Message}"); }

                if (built is null) { Note($"[viewport] attach {name}: no geometry"); continue; }

                var textured = 0;
                foreach (var section in built.Sections)
                {
                    try { _textures?.BindMaterial(section); }
                    catch (Exception e) { Note($"[viewport] attach {name}: {e.Message}"); }
                    if (section.Diffuse != 0) textured++;
                }

                // HOW WELL IT REALLY FITS, measured now that the bones are here rather than
                // guessed from the name. The list that offered it went on the SK_<rig>_ token,
                // which is the game's own convention and still only a convention.
                var host = _mesh?.Skeleton;
                var mine = built.Skeleton;
                var shared = 0;
                if (host is { Count: > 0 } && mine is { Count: > 0 })
                    shared = mine.Names.Count(n => host.IndexOf(n) >= 0);

                _attached.Add(new Attachment(key, name, queuedAsset, built, shared,
                                             mine?.Count ?? 0));
                Note($"[viewport] attached {name}: {built.Sections.Count} section(s), " +
                     $"{textured} textured, {shared}/{mine?.Count ?? 0} bones shared with the " +
                     "host rig");
            }
            // AND FRAME AGAIN, or the wider bounds are computed and never used: the primary
            // was framed on its first build, long before any of this arrived. A helmet and a pair
            // of legs move the top and bottom of the scene, which is exactly what the camera was
            // fitted to.
            _framed = false;
            AttachedChanged?.Invoke();
        }

        if (_releaseAfterDetach && _gl is not null)
        {
            _releaseAfterDetach = false;
            ReleaseUnusedTextures("an attachment was detached");
        }

        if (_pending is null || _gl is null) return;

        var asset = _pending;
        _pending = null;

        _mesh?.Dispose();
        _mesh = null;
        SectionCount = 0;
        TexturedSections = 0;

        try
        {
            _mesh = asset switch
            {
                USkeletalMesh sk => RenderMesh.FromSkeletal(_gl, sk, _wantLod),
                UStaticMesh st => RenderMesh.FromStatic(_gl, st, _wantLod),
                _ => null,
            };

            if (_mesh is null)
            {
                Fault = "that asset has no geometry this renderer can read";
                Loaded?.Invoke();
                return;
            }

            foreach (var section in _mesh.Sections)
            {
                try { _textures?.BindMaterial(section); }
                // NAMED PER SECTION, not fatal. One material that will not resolve leaves that
                // piece grey; taking the whole model down for it would hide the rest of a mesh
                // that is otherwise fine.
                catch (Exception e) { Fault ??= e.Message; }

                SectionCount++;
                if (section.Diffuse != 0) TexturedSections++;
            }

            // THE PREVIOUS MODEL'S PICTURES GO. Its mesh was disposed above, which deleted its buffers; its textures
            // sit in this view's TextureCache, which keeps everything it ever uploaded, so showing one part after
            // another in the same view piled every part's textures onto the GPU. Done AFTER the new model is dressed,
            // so a LOD switch (the same asset, the same textures) deletes nothing and decodes nothing again.
            ReleaseUnusedTextures("another model replaced the last");

            Materials = _mesh.Sections
                .Select(s => new MaterialRow(s.MaterialName, s.Diffuse != 0,
                                             s.NormalMap != 0, s.Orme != 0))
                .ToList();

            // ONLY ON THE FIRST BUILD of an asset. A LOD switch keeps the camera; see ShowLod.
            if (!_framed)
                Frame(Bounds.Height <= 0 ? 16f / 9f : (float) (Bounds.Width / Bounds.Height));
            foreach (var sec in _mesh.Sections)
                Console.WriteLine($"[viewport] section first={sec.FirstIndex} count={sec.IndexCount} " +
                                  $"visible={sec.Visible} twoSided={sec.TwoSided} " +
                                  $"diffuse={sec.Diffuse} mat={sec.MaterialName} " +
                                  $"alphaCut={sec.AlphaCut} decalMask={sec.DecalMask} " +
                                  // WHICH MAPS ARE ACTUALLY BOUND. Without these the surface
                                  // channel views cannot be read: with no ORME the shader falls
                                  // back to ao=1, rough=0.6, metal=0, and Material AO draws a
                                  // flat white silhouette that looks exactly like a mode that
                                  // works — and is showing a constant, not the asset.
                                  $"normal={sec.NormalMap} orme={sec.Orme} trim={sec.TrimMask} " +
                                  $"verts={_mesh.VertexCount} tris={_mesh.TriangleCount}");
        }
        catch (Exception e) { Fault = e.Message; }

        // WHICH MATERIAL PARAMETER EACH MAP CAME FROM, in TextureCache's own words.
        //
        // The difference between "this material has no ORME" and "the ORME did not resolve" is
        // invisible in the picture — both leave the surface channel views showing the shader's
        // constants, which look like a mode that works. BoundLog names the parameter it matched,
        // so an empty ORME= is the material's answer rather than a guess about it.
        if (_textures is not null)
            foreach (var line in _textures.BoundLog.TakeLast(8))
                Console.WriteLine($"[viewport] bound {line}");
#if DEV
        if (_textures is not null)
            foreach (var line in _textures.BlockReport())
                Console.WriteLine($"[viewport] blocks: {line}");
#endif

        Console.WriteLine($"[viewport] built: {SectionCount} section(s), " +
                          $"{TexturedSections} textured, lod={Lod} " +
                          $"offered=[{string.Join(",", LodsOffered)}] fault={Fault ?? "none"}");
        Loaded?.Invoke();
        LodChanged?.Invoke();
    }

    /// <summary>
    /// THE MODEL'S EXTENT IN THE SPACE IT IS ACTUALLY DRAWN IN — one definition, used by the
    /// camera framing and by the --probe readout both.
    ///
    /// THE BOUNDS AND THE VERTICES ARE NOT IN THE SAME SPACE, and this is where that cost a day.
    /// RenderMesh.WithSections says it outright — "bounds are kept in GL space for camera
    /// framing" — while the VERTICES it uploads are UE space, which is why the shader is handed
    /// UeToGl at all. Transforming the bounds by UeToGl as well therefore converts something
    /// that was already converted: it framed the camera on (0, -1.86, 165.5) while the geometry
    /// drew at (0, 165.5, 1.86), so a 25-unit helmet sat 165 units off screen while every other
    /// reading — sections, indices, materials, near and far, the shader, the GL state — stayed
    /// perfect. Nothing reported a fault, because nothing was faulty.
    ///
    /// So undo the bounds' own conversion and then apply exactly what the shader applies. The
    /// round trip is checkable rather than reasoned: it reproduces the vertex buffer's measured
    /// range to the decimal, which is how this was found. --vbo is that measurement.
    ///
    /// DERIVED IN ONE PLACE ON PURPOSE. The first version of this computed the framing here and
    /// the probe's corners separately, so after the fix the probe went on insisting the model was
    /// off screen while it was plainly drawn — a second copy of one fact, disagreeing. Same
    /// lesson as the level editor's three records of one move.
    /// </summary>
    private (Vector3 Min, Vector3 Max) DrawnBounds()
    {
        if (_mesh is null) return (Vector3.Zero, Vector3.Zero);

        static Vector3 ToUe(Vector3 v) => new(v.X, -v.Z, v.Y);

        // EVERY MESH ON SCREEN, not only the primary. The name is DrawnBounds and it was reading
        // one of the things drawn: a torso framed alone leaves its helmet above the top of the
        // view and its legs below the bottom, and the whole point of the Meshes tab is that there
        // is more here than the asset the tab was opened on.
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var m in _attached.Select(a => a.Mesh).Prepend(_mesh))
        {
            var a = Vector3.Transform(ToUe(m.BoundsMin), RenderMesh.UeToGl);
            var b = Vector3.Transform(ToUe(m.BoundsMax), RenderMesh.UeToGl);
            // COMPONENTWISE, because a swizzle with a negation reorders the corners: after the two
            // transforms the "min" corner holds the larger value on one axis, and subtracting them
            // in the wrong order gives a negative extent and a camera inside the model.
            lo = Vector3.Min(lo, Vector3.Min(a, b));
            hi = Vector3.Max(hi, Vector3.Max(a, b));
        }
        return (lo, hi);
    }

    /// <summary>
    /// Put the whole model on screen, whatever size it is.
    ///
    /// A helmet is twenty units and a dropship is three thousand, so a fixed camera distance
    /// shows one of them and loses the other. The bounds decide it.
    ///
    /// FIT THE BOX, NOT ITS ENCLOSING SPHERE. A torso is 93 wide, 58 tall and 35 deep, so the
    /// sphere around it has a radius of 58 against a silhouette only 29 high — backing off far
    /// enough to fit that sphere vertically wastes half the frame, and a 1.35 pad on top wasted
    /// half of what was left. The model came out a small blob in a field of background.
    ///
    /// Width still uses the horizontal DIAGONAL, because this is an orbit camera: swing round to
    /// the side and what was depth becomes width, so fitting the front view alone would clip the
    /// model the moment it was turned.
    /// </summary>
    public void Frame(float aspect = 16f / 9f)
    {
        if (_mesh is null) return;

        var (min, max) = DrawnBounds();
        var centre = (min + max) * 0.5f;
        var half = (max - min) * 0.5f;

        if (aspect <= 0f || float.IsNaN(aspect)) aspect = 16f / 9f;
        var radius = MathF.Max(half.Length(), 1f);

        // FIT THE ENCLOSING SPHERE TO THE NARROWER HALF-ANGLE, and note it is r/sin, not r/tan.
        //
        // r/tan puts the sphere's CENTRE plane across the frame; the sphere bulges nearer than
        // that, where the cone is narrower, so r/tan always clips a little. r/sin is the exact
        // tangent solution.
        //
        // The sphere rather than the box because this is an orbit camera and the sphere is the
        // one shape that does not change as the model turns. Two box fits were tried first: the
        // plain one cut the torso's shoulders off, and offsetting it by the orbit radius still
        // left a corner outside, because the default camera is PITCHED and a pitched view
        // projects a mix of all three half-extents rather than the height alone. --probe caught
        // both with on=False readings. A sphere has no such special angle.
        //
        // The narrower of the two half-angles, so a tall window fits the width and a wide one —
        // which this viewport is — fits the height.
        var halfV = Camera.Fov * 0.5f;
        var halfH = MathF.Atan(MathF.Tan(halfV) * aspect);
        var need = radius / MathF.Sin(MathF.Min(halfV, halfH));

        Camera.Target = centre;
        Camera.Distance = MathF.Max(need * 1.02f, 0.05f);

        // THE PLANES, FITTED TO THIS MODEL RATHER THAN TO A LEVEL.
        //
        // Camera's defaults are the Level Editor's: far = Distance*20 + 5000 and near =
        // Distance/200, which on a 1.8m character gave near 0.697 against far 7787 -- a 78-METRE
        // far plane for a scene 2 metres across, and an 11,000:1 ratio. Those numbers are right
        // where they come from: a map has real geometry kilometres out, and a far plane that
        // followed the zoom would cut the vista off as you moved in.
        //
        // Nothing in THIS viewport is ever more than a few radii from the target. So the far
        // plane only has to clear the far side of the model plus room to orbit and for a five
        // metre floor, and the near plane can then be pulled far enough out to keep the ratio
        // near 1000:1 instead of 11,000:1 -- while staying closer than anything can be drawn,
        // because Distance never falls below the fitted radius.
        var reach = MathF.Max(radius, 1f);
        Camera.FarOverride = Camera.Distance + reach * 8f + 600f;
        Camera.NearOverride = MathF.Max(Camera.FarOverride.Value / 1000f, 0.05f);
        Console.WriteLine($"[viewport] bounds ue {_mesh.BoundsMin} .. {_mesh.BoundsMax} | " +
                          $"gl {min} .. {max} | centre {centre} radius {radius:0.##} " +
                          $"dist {Camera.Distance:0.##} eye {Camera.Position} " +
                          $"near {Camera.NearPlane:0.###} far {Camera.FarPlane:0.#}");
        _framed = true;
        RequestNextFrameRendering();
    }

    // ---------------------------------------------------------------- the mouse

    private Point? _drag;
    private bool _panning;
    private bool _saidInput;
    private bool _saidDrag;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Clicking the viewport takes the keyboard back. Clicking the property grid or a text
        // box silently gives it away, and the keys then go wherever focus went.
        Focus();
        var p = e.GetCurrentPoint(this);
        _drag = p.Position;
        // LEFT TURNS AROUND THE MODEL; middle or right slides it sideways. The same hands every
        // DCC this sits beside puts them in, and it is what the author asked for by name.
        _panning = p.Properties.IsMiddleButtonPressed || p.Properties.IsRightButtonPressed;

        // SAID ONCE, because "the camera does not move" has two completely different causes and
        // no picture separates them: either the control is not being sent the input at all, or it
        // is and the camera is ignoring it. One line answers which. The level editor chased the
        // wrong one of those for an evening.
        if (!_saidInput)
        {
            _saidInput = true;
            Note($"[viewport] pointer reached the viewport at {p.Position}, panning={_panning}");
        }
        e.Pointer.Capture(this);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } was) { base.OnPointerMoved(e); return; }

        var now = e.GetPosition(this);
        var dx = (float) (now.X - was.X);
        var dy = (float) (now.Y - was.Y);
        _drag = now;

        if (_panning) Camera.Pan(dx, dy);
        else Camera.Orbit(dx, dy);

        if (!_saidDrag && (dx != 0f || dy != 0f))
        {
            _saidDrag = true;
            Note($"[viewport] first drag d=({dx},{dy}) {(_panning ? "pan" : "orbit")} -> " +
                 $"yaw {Camera.Yaw:0.###} pitch {Camera.Pitch:0.###} eye {Camera.Position}");
        }

        RequestNextFrameRendering();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = null;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    /// <summary>
    /// FLYING, on top of the orbit.
    ///
    /// Orbit/pan/zoom is the right scheme for inspecting one object and is what the mouse does.
    /// It cannot get you INSIDE a helmet or behind a pauldron, which is the whole point of
    /// looking around a mesh rather than at it — so W A S D walks the camera, Q and E lift it,
    /// and F puts the model back on screen when the flying has gone wrong.
    ///
    /// PROPORTIONAL TO THE DISTANCE, like the wheel. A fixed step crawls across a dropship and
    /// jumps straight past a helmet; a fraction of the current framing feels the same at either
    /// scale.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = MathF.Max(Camera.Distance * 0.08f, 0.02f);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step *= 3f;

        switch (e.Key)
        {
            case Key.W: Camera.Move(step, 0, 0); break;
            case Key.S: Camera.Move(-step, 0, 0); break;
            case Key.A: Camera.Move(0, -step, 0); break;
            case Key.D: Camera.Move(0, step, 0); break;
            case Key.Q: Camera.Move(0, 0, -step); break;
            case Key.E: Camera.Move(0, 0, step); break;
            case Key.F:
                _framed = false;
                Frame(Bounds.Height <= 0 ? 16f / 9f : (float) (Bounds.Width / Bounds.Height));
                break;
            default: base.OnKeyDown(e); return;
        }

        e.Handled = true;
        RequestNextFrameRendering();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // PROPORTIONAL, not a fixed step. A fixed step crawls across a dropship and jumps past a
        // helmet; a fraction of the current distance feels the same at either scale.
        Camera.Distance = Math.Clamp(Camera.Distance * (e.Delta.Y > 0 ? 0.88f : 1.136f),
                                     0.05f, 500_000f);
        RequestNextFrameRendering();
        base.OnPointerWheelChanged(e);
    }
}

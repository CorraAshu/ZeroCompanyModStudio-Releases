using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using Silk.NET.OpenGL;
using UEToolkit.App.Rendering;
using Shader = UEToolkit.App.Rendering.Shader;   // Silk.NET has one of its own
using UEToolkit.Core;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// THE LEVEL, DRAWN.
///
/// The renderer is the toolkit's own, linked in unchanged -- RenderMesh, Shader, Camera. What
/// this adds is the two things a level needs and a single-asset viewer does not: a SCENE of many
/// meshes at many transforms, and loading that does not stall the window while it happens.
///
/// MESHES ARE LOADED A FEW PER FRAME. A sublevel is 81 unique meshes and about 280 ms of loading
/// (--meshcost), which is nothing as a total and quite enough to drop frames if it happens in
/// one. A handful per frame means the level fills in over about a second instead, and the camera
/// stays live throughout.
///
/// ONE RenderMesh PER UNIQUE MESH, drawn once per placement. The measurement that decided that:
/// 743 placements share 81 meshes, so nine of every ten draws is a matrix change and nothing more
/// -- which is the work instancing would have done, for the price of a dictionary.
/// </summary>
public class Viewport : OpenGlControlBase
{
    /// <summary>
    /// A TRANSPARENT FILL, SO THE MOUSE CAN FIND THIS CONTROL AT ALL.
    ///
    /// Avalonia hit-tests against what a visual DREW, and an OpenGlControlBase draws through a
    /// composition operation the hit test cannot see -- so the pointer went to the Border behind
    /// it and every mouse handler in this file was dead code. Not orbit, not the wheel, not
    /// right-drag, not click-to-select: none of it had ever run from a real mouse.
    ///
    /// It went unnoticed because every test drove the control DIRECTLY -- RequestPick, TestDrag,
    /// RaiseEvent -- which proves the maths and says nothing about whether an event arrives.
    /// Confirmed by asking Avalonia instead: InputHitTest at the middle of the viewport returned
    /// the Border. A transparent rectangle is geometry, so now it returns this.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        base.Render(context);
    }

    /// <summary>
    /// WITHOUT Focusable THERE IS NO KEYBOARD. A control that cannot take focus never sees a key
    /// at all, which is the whole of "W A S D does nothing" -- the handlers can be perfect and
    /// still never run.
    /// </summary>
    public Viewport()
    {
        Focusable = true;

        // Losing focus mid-flight must not leave a key stuck down for ever. Subscribed rather
        // than overridden: OnLostFocus is not virtual on this base.
        LostFocus += (_, _) => StopFlying();
    }

    private GL? _gl;
    private Shader? _shader;
    private Shader? _pick;
    private Editor? _editor;
    private int _builtVersion = -1;
    private int _seenMapVersion = -1;

    /// <summary>
    /// MATERIALS ARE A SECOND, SLOWER PASS. A mesh is useful the moment its shape is on screen;
    /// its textures are not, and decoding them is far more expensive than reading the geometry.
    /// So a mesh is drawn grey as soon as it loads and dressed afterwards, on the same time
    /// budget and the same "not while the camera is moving" rule the geometry uses.
    /// </summary>
    private TextureCache? _textures;

    /// <summary>
    /// Meshes still waiting for their materials, BY PATH -- because the order they get them in is
    /// chosen against the camera rather than fixed, so this is a set and not a queue.
    /// </summary>
    private readonly HashSet<string> _toDress = new(StringComparer.OrdinalIgnoreCase);
    private int _dressed;
    public int MaterialsBound => _dressed;

    /// <summary>True while materials are still being decoded and uploaded.</summary>
    public bool Dressing => _toDress.Count > 0;

    /// <summary>How much of the dressing is REPEATED work: sections against distinct materials.</summary>
    public string MaterialSpread()
    {
        var sections = 0;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var m in _meshes.Values)
        {
            if (m is null) continue;
            foreach (var s in m.Sections)
            {
                sections++;
                // BY PATH, not by object. The material is released the moment it is bound, so an
                // identity count here would report zero distinct materials for a dressed level and
                // look like the binder had stopped working.
                if (s.MaterialPath.Length > 0) seen.Add(s.MaterialPath);
            }
        }
        return $"MATERIALS {sections:N0} section(s) across {_meshes.Count:N0} mesh(es), " +
               $"{seen.Count:N0} DISTINCT material(s)";
    }
    public double DressMs { get; private set; }

    private readonly Dictionary<string, RenderMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _toLoad = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Meshes that did NOT load, and why.
    ///
    /// The loader used to swallow every failure into a null and carry on, so a mesh that could
    /// not be read was indistinguishable from one that had not been reached yet -- and a whole
    /// building could be missing from the level with nothing anywhere saying so.
    /// </summary>
    private readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);

    public string FailedMeshReport()
    {
        var lines = new List<string> { $"MESHFAIL {_failed.Count:N0} of {_meshes.Count:N0} unique mesh(es) did not load" };
        foreach (var g in _failed.GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).Take(6))
        {
            lines.Add($"MESHFAIL   {g.Count(),6:N0}  {g.Key}");
            foreach (var kv in g.Take(3)) lines.Add($"MESHFAIL           {kv.Key[(kv.Key.LastIndexOf('/') + 1)..]}");
        }
        return string.Join(Environment.NewLine, lines);
    }
    private List<LevelReader.Placement> _scene = new();

    private bool _depthAsked;

    /// <summary>Bits of depth the host's framebuffer actually gave us. 0 until the first frame.</summary>
    public int DepthBits { get; private set; }

    public readonly Camera Camera = new();

    /// <summary>Fired with the index into <see cref="_scene"/> that a click landed on, or -1.</summary>
    public event Action<int>? Picked;

    /// <summary>Which placement is highlighted, as an index into the scene.</summary>
    public int Selected { get; set; } = -1;

    private Point? _pickAt;

    /// <summary>True while meshes are still being pulled out of the containers.</summary>
    public bool Loading => _toLoad.Count > 0;

    // ---- the translate gizmo ----------------------------------------------------------------
    //
    // UE's axes in GL space, which is the swap RenderMesh.UeToGl makes: UE X stays X, UE Y
    // becomes GL Z, UE Z becomes GL Y. Dragging the arrow labelled X therefore changes
    // RelativeLocation.X, which is what the property panel and the mod file both mean.
    private static readonly Vector3[] AxesGl = { new(1, 0, 0), new(0, 0, 1), new(0, 1, 0) };

    /// <summary>
    /// Pick ids for the three arrows, far above any placement index so the two can never be
    /// confused -- a scene of a million placements would still not reach here.
    /// </summary>
    private const int GizmoId = 0xFF0000;

    private Shader? _line;
    private uint _axisVao, _axisVbo;
    private uint _ringVao, _ringVbo;
    private uint _handleVao, _handleVbo;
    private const int RingSegments = 64;
    private int _axis;                 // 1..3 while an arrow is being dragged, else 0
    private bool _awaitingGizmo;       // a press has asked whether it landed on an arrow
    private enum Purpose { Gizmo, Select }
    private Purpose _purpose = Purpose.Select;

    /// <summary>Pick at a point, without a mouse -- how the pick path is tested headlessly.</summary>
    public void RequestPick(Point at) { _pickAt = at; RequestNextFrameRendering(); }

    /// <summary>
    /// Put the camera on the selected actor.
    ///
    /// Needed for more than convenience: an actor's PIVOT is not where its geometry looks. A
    /// terrain tile fills the screen while its origin sits thousands of units behind the camera,
    /// so the gizmo -- which lives at the pivot -- is nowhere to be seen and a drag has nothing
    /// to project onto. Framing first is what makes a selection workable at all.
    /// </summary>
    public void FrameSelected()
    {
        if (GizmoAt() is not { } at) return;
        Camera.Target = at;
        Camera.Distance = 2000f;
        CameraTouched();          // a deliberate placement of the camera; stop auto-framing over it
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Drag an arrow without a mouse -- how the gizmo maths is tested headlessly.
    ///
    /// <paramref name="steps"/> is not decoration: a real drag is dozens of small moves inside
    /// ONE batch, and a one-move test would pass whether the batching worked or not.
    /// </summary>
    public void TestDrag(int axis, Point from, Point to, int steps = 1)
    {
        if (Selected < 0 || Selected >= _scene.Count || axis < 1 || axis > 3) return;
        if (steps < 1) steps = 1;

        _editor?.BeginBatch("gizmo drag");
        var last = from;
        for (var i = 1; i <= steps; i++)
        {
            // The anchor is re-read each step, exactly as the live drag does, so the arrow being
            // projected is the one where the actor now IS rather than where it started.
            if (GizmoAt() is not { } at) break;

            var t = (double) i / steps;
            var now = new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

            // THE SAME PATH THE MOUSE TAKES, tool and all. Nothing is written to the placement
            // here: the editor recomputes every offset and warp from the change list.
            ApplyDrag(axis, at, last, now);
            last = now;
        }
        _editor?.EndBatch();
        RequestNextFrameRendering();
    }
    private string? _fault;

    /// <summary>Why the viewport is blank, when it is. Null means GL came up.</summary>
    public string? Fault => _fault;
    private bool _framed;

    /// <summary>
    /// How long to spend pulling meshes out of the containers per frame, and how long to leave
    /// the loading alone after the camera has been touched.
    ///
    /// A COUNT WAS THE WRONG BUDGET. Four per frame was chosen when a sublevel was 81 meshes;
    /// a merged map is five hundred, and each costs about 80 ms to read, convert and upload --
    /// so four of them is a 320 ms frame. Measured: 3 frames per second for the half-minute a
    /// map takes to load, which is not slow, it is indistinguishable from broken, and is most of
    /// why the camera seemed not to answer at all.
    ///
    /// So: a TIME budget rather than a count, and nothing at all for a moment after the camera
    /// moves. The same meshes get loaded either way and the total is unchanged -- what changes is
    /// that the frames somebody is actually looking through are the fast ones.
    /// </summary>
    private const double LoadBudgetMs = 10;
    private const long QuietAfterCameraMs = 250;

    /// <summary>When the camera was last moved, so loading can keep out of the way.</summary>
    private long _cameraTouchedAt;

    private void CameraTouched() => Volatile.Write(ref _cameraTouchedAt, Environment.TickCount64);

    public void Attach(Editor editor)
    {
        _editor = editor;
        editor.PropertyChanged += (_, e) =>
        {
            // ONCE SOMEBODY HAS MOVED THE CAMERA, STOP MOVING IT FOR THEM. A map arrives as
            // twenty sublevels over half a minute and each one used to re-frame -- so starting to
            // fly during a load meant being teleported back, repeatedly, with no way to tell why.
            // Opening a map or a sublevel is a deliberate "show me this" and does frame; another
            // sublevel of the one already open only frames if the camera is still untouched.
            if (e.PropertyName == nameof(Editor.MapVersion))
                Dispatcher.UIThread.Post(() =>
                {
                    Volatile.Write(ref _cameraTouchedAt, 0);
                    _framed = false;
                    RequestNextFrameRendering();
                });
            else if (e.PropertyName == nameof(Editor.SceneVersion))
                Dispatcher.UIThread.Post(() =>
                {
                    if (Volatile.Read(ref _cameraTouchedAt) == 0) _framed = false;
                    RequestNextFrameRendering();
                });
        };

        // A MOVE CHANGED WHERE SOMETHING IS DRAWN -- typed, dragged, reverted or undone, it
        // reaches here the same way, because the editor recomputes every placement's offset from
        // the change list rather than anyone accumulating it in place.
        editor.Repaint += () => Dispatcher.UIThread.Post(RequestNextFrameRendering);
        editor.Reswap += () => Dispatcher.UIThread.Post(Rescan);
        editor.Remark += () => Dispatcher.UIThread.Post(MarkBackdrop);
        editor.Reshade += () => Dispatcher.UIThread.Post(Redress);

        // TURNING A SUBLEVEL OFF has to reach Rebuild, which otherwise only runs when the scene
        // version moves -- and hiding a sublevel deliberately does not move it.
        editor.Resublevel += () => Dispatcher.UIThread.Post(() =>
        {
            _resublevel = true;
            RequestNextFrameRendering();
        });
    }

    // ---------------------------------------------------------------- GL lifetime

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = GL.GetApi(gl.GetProcAddress);
            _shader = new Shader(_gl, Shaders.MeshVertex, Shaders.MeshFragment);
            _pick = new Shader(_gl, Shaders.MeshVertex, Shaders.PickFragment);
            _line = new Shader(_gl, LineVertex, LineFragment);
            _textures = new TextureCache(_gl);
#if DEV
            if (TextureCap > 0) _textures.MaxPreviewSize = TextureCap;
#endif
            BuildAxes();
            Console.WriteLine($"GL bound: {_gl.GetStringS(StringName.Version)} | {_gl.GetStringS(StringName.Renderer)}");
        }
        catch (Exception e) { _fault = e.Message; Console.WriteLine("GL FAILED: " + e); }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        foreach (var m in _meshes.Values) m?.Dispose();
        _meshes.Clear();
        // The cache deletes its textures and takes them out of the process-wide counters (a re-init with the old cache
        // left in place counted every one of them twice); what is dropped after it is our memo of which handle goes
        // with which material.
        _textures?.Dispose();
        _textures = null;
        _skins.Clear();
        _skinSets.Clear();
        SkinsBound = 0;
        SkinsFailed = 0;
        SkinFailures.Clear();
        _shader?.Dispose();
        _pick?.Dispose();
        _line?.Dispose();
        if (_axisVbo != 0) _gl?.DeleteBuffer(_axisVbo);
        if (_axisVao != 0) _gl?.DeleteVertexArray(_axisVao);
        _gl = null;
    }

    // ---------------------------------------------------------------- the scene

    private void Rebuild()
    {
        if (_editor is null) return;

        // A SUBLEVEL BEING TURNED OFF IS NOT A NEW SCENE, and the version guard cannot see it:
        // the placement list is the same list, in the same order, by design. So the toggle asks
        // for one rebuild of its own, which is what gives the hidden sublevel's meshes back.
        if (_editor.SceneVersion == _builtVersion && !_resublevel) return;
        _resublevel = false;
        // A MAP OR A SUBLEVEL WAS OPENED since the last time the scene was built: the editor counts them (MapVersion), and
        // this is the first build to see the new count. Everything else that rebuilds the scene -- a sublevel streaming
        // in, a swap, a spawn, an undo -- leaves it as it was.
        var opened = _editor.MapVersion != _seenMapVersion;
        _seenMapVersion = _editor.MapVersion;
        _builtVersion = _editor.SceneVersion;
        _scene = _editor.Placements;

        // KEEP WHAT IS STILL DRAWN. This used to dispose every mesh and re-queue the lot on every
        // scene version, which is once per streamed sublevel -- 45 of them in 26 seconds on the
        // hub. Loading then refilled over the next few seconds, and the next sublevel threw it
        // away again, so the level flashed for the whole load.
        //
        // And LoadSome stands down while the camera is moving, which is the right rule on its own
        // but turns this into "the scene is empty for as long as you fly" -- the author's report
        // was "everything disappears and reappears over and over and moving the camera doesn't
        // change anything". Moving the camera WAS the thing stopping it coming back.
        //
        // So: queue what is new, dispose only what nothing draws any more, and leave everything
        // else exactly where it is. A streamed sublevel now costs its own meshes and nothing else.
        // WHAT IS WANTED IS WHAT IS DRAWN, so turning a sublevel off gives its meshes back.
        //
        // A hidden sublevel's geometry is dead weight -- the hub's vista backdrops alone are
        // 25,621 placements -- and this is the one place a saving can be taken without touching
        // the placement list that picking indexes into. A mesh shared with a sublevel that IS
        // shown stays, because that sublevel still wants it.
        var hiddenSubs = _editor?.HiddenSublevels;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _scene)
        {
            // HideKey, NOT Sublevel -- see LevelReader.Placement.RootSublevel. Sublevel names
            // the package a thing lives in, which for anything inside a level instance is the
            // prefab and never a name any tick box carries.
            if (hiddenSubs is { Count: > 0 } && hiddenSubs.Contains(p.HideKey)) continue;
            if (p.Shown is { } shown) wanted.Add(shown);
        }

        var dropped = 0;
        foreach (var gone in _meshes.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _meshes[gone]?.Dispose();
            _meshes.Remove(gone);
            _toDress.Remove(gone);
            dropped++;
        }
        // `_toDress` holds what is WAITING to be dressed, so removing from it says nothing about
        // how many were dressed -- clamp the counter instead of guessing at it. It is a
        // diagnostic, and Redress recomputes it from scratch.
        if (_dressed > _meshes.Count) _dressed = _meshes.Count;
        var kept = _meshes.Count;

        // THE PICTURES GO WITH THE MESHES. Disposing a mesh above deletes its vertex and index buffers, but the
        // textures its materials uploaded live in the TextureCache, which kept every one of them for the rest of
        // the session -- so opening a second map left the first map's pictures on the GPU for good. When the scene
        // was REPLACED -- a map or sublevel was opened and this is the build that dropped the old one's meshes -- the
        // textures nothing in the new scene uses are deleted (SweepWhenLoaded), but NOT HERE: the first build of a
        // new map holds only its root, so almost everything the old map had looks unused, and two maps that share
        // their terrain (SK_Anaxes_040 and 045 do) would decode every shared picture a second time. Measured: that
        // made the second load 45% slower. So the sweep waits until the new scene has finished arriving and
        // dressing, and deletes what is STILL unused then.
        //
        // NOT on a swap, a spawn or an undo, which drop a mesh too: `opened` is what tells a map or sublevel OPEN from
        // those, and it is also false for a tick box on its own (unticking is a look, and ticking it again should not
        // decode every picture a second time). A tick box toggled in the same pass as an open does not cancel the sweep.
        if (dropped > 0 && opened && !KeepTextures) _sweepAfterLoad = true;

        // Anything already in _meshes is loaded or queued -- a pending path sits there as null --
        // so this adds each new mesh exactly once and never re-queues one in flight.
        foreach (var path in wanted)
            if (!_meshes.ContainsKey(path))
            {
                _meshes[path] = null;
                _toLoad.Add(path);
            }
        MeasureCore();
        MarkBlockout();
        // KEPT AND DROPPED, said out loud. The flashing fault was invisible in this line while it
        // only counted what the scene wants -- the number was right the whole time and every mesh
        // behind it had just been thrown away and re-queued.
        Console.WriteLine($"scene: {_scene.Count:N0} placements, {_meshes.Count:N0} unique mesh(es), " +
                          $"{_backdrop.Count(b => b):N0} backdrop  " +
                          $"(kept {kept:N0}, queued {_meshes.Count - kept:N0}, dropped {dropped:N0})" +
                          $"  greybox {_blockout.Count(b => b):N0}");
    }

    /// <summary>A map or sublevel replaced the scene and its old textures have not been swept yet (see <see cref="Rebuild"/>).</summary>
    private bool _sweepAfterLoad;

    /// <summary>
    /// Once the replacing scene has finished arriving (the map is read, every mesh is on the GPU and every material
    /// bound), delete the textures nothing in it uses. Called every frame; does nothing until then.
    ///
    /// A texture is LIVE while a section of a mesh still here points at it, or an override skin the scene still names
    /// does. The skins are caches by material path: those no placement names any more are dropped and the rest keep
    /// their textures; the per-slot arrays built from them (<c>_skinSets</c>) hold only copies of the handles and are
    /// rebuilt on demand without decoding anything.
    /// </summary>
    private void SweepWhenLoaded()
    {
        if (!_sweepAfterLoad || _textures is null || _editor is null) return;
        if (_editor.Busy || _toLoad.Count > 0 || _toDress.Count > 0) return;
        _sweepAfterLoad = false;

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _scene)
            if (p.Materials is { } materials)
                foreach (var m in materials) if (!string.IsNullOrEmpty(m)) named.Add(m);
        foreach (var stale in _skins.Keys.Where(k => !named.Contains(k)).ToList()) _skins.Remove(stale);
        _skinSets.Clear();

        var skinHandles = new HashSet<uint>();
        foreach (var skin in _skins.Values) skin?.AddTextureHandles(skinHandles);
        var (n, bytes) = _textures.ReleaseUnused(_meshes.Values.Where(m => m is not null).SelectMany(m => m!.Sections), skinHandles);
        Console.WriteLine($"textures: the new scene is loaded; released {n:N0} texture(s), {bytes / 1048576.0:N0} MB (estimate), that nothing in it uses");
#if DEV
        SweepCheck("after the sweep");
#endif
    }

    /// <summary>
    /// A MESH THE SCENE DID NOT HAVE A MOMENT AGO -- ask for it to be queued, do not queue it here.
    ///
    /// Swapping a chair for a crate can name a mesh no placement in this sublevel draws: the donor
    /// it was copied from might be a component that is never placed. The load queue is filled once
    /// when the scene is built, so without this the new mesh is simply absent from the dictionary
    /// and the actor silently draws nothing -- the exact failure this whole feature exists to stop
    /// being invisible.
    ///
    /// ONLY A FLAG IS SET HERE, because this is called from the UI thread and `_meshes` and
    /// `_toLoad` belong to the RENDER thread -- Rebuild, LoadSome and DressSome all run inside
    /// OnOpenGlRender. Writing a Dictionary while another thread reads it does not fail loudly; it
    /// corrupts or spins. A bool is written atomically and read one frame later, on the thread
    /// that owns the collections.
    /// </summary>
    public void Rescan()
    {
        _rescan = true;
        RequestNextFrameRendering();
    }

    private volatile bool _rescan;

    /// <summary>Set when a sublevel is shown or hidden, so Rebuild runs once more. See Rebuild.</summary>
    private volatile bool _resublevel;

    /// <summary>Queue anything a swap has named since the last frame. RENDER THREAD ONLY.</summary>
    private void QueueSwapped()
    {
        if (!_rescan) return;
        _rescan = false;

        var added = 0;
        foreach (var p in _scene)
            if (p.Shown is { } shown && !_meshes.ContainsKey(shown))
            {
                _meshes[shown] = null;
                _toLoad.Add(shown);
                added++;
            }
        if (added > 0) Console.WriteLine($"scene: {added} new mesh(es) queued for a swap");
    }

    // ---------------------------------------------------------------- where the level actually is
    //
    // ONE STATISTIC, TWO USES. The camera needs to know where the level is to frame it, and the
    // backdrop filter needs the same answer to decide what is not part of it. Measuring it twice
    // would let the two disagree, and then hiding the backdrop would move the camera.

    private Vector3 _core;                 // UE space
    private float _coreRadius = 1f;
    private float _allRadius = 1f;         // to the FARTHEST placement, for the far plane
    private Vector3 _coreReach = Vector3.One;
    private bool[] _backdrop = Array.Empty<bool>();
    private int _hiddenThisFrame;

    /// <summary>Placements the last frame actually drew. Diagnostic.</summary>
    public int LastDrawn { get; private set; }

    /// <summary>
    /// NAME THE MESH THAT VANISHED.
    ///
    /// The author: "meshes keep disappearing and reappearing and i'm not even moving the camera,
    /// you can see 1 mesh is there 1 second and then gone." A total that goes up cannot witness
    /// that -- one mesh leaving while two arrive still counts as progress, which is exactly what
    /// the earlier draw-count instrument reported and exactly why it found nothing.
    ///
    /// So this watches each PATH across frames and says which one stopped being drawable, and
    /// whether the entry went null, was removed outright, or had its geometry replaced.
    /// </summary>
    public bool TraceMeshes { get; set; }

    private readonly Dictionary<string, RenderMesh> _wasReady = new(StringComparer.OrdinalIgnoreCase);
    private int _lastHiddenSeen = -1;

    private void Trace()
    {
        if (!TraceMeshes) return;

        foreach (var path in _wasReady.Keys.ToList())
        {
            if (!_meshes.TryGetValue(path, out var now))
            {
                Console.WriteLine($"MESH GONE      {Short(path)}   (removed from the table)");
                _wasReady.Remove(path);
            }
            else if (now is null)
            {
                Console.WriteLine($"MESH NULLED    {Short(path)}   (entry set back to null)");
                _wasReady.Remove(path);
            }
            else if (!ReferenceEquals(now, _wasReady[path]))
            {
                Console.WriteLine($"MESH REPLACED  {Short(path)}   (loaded a second time)");
                _wasReady[path] = now;
            }
        }
        foreach (var kv in _meshes)
            if (kv.Value is not null && !_wasReady.ContainsKey(kv.Key)) _wasReady[kv.Key] = kv.Value;

        if (_hiddenThisFrame != _lastHiddenSeen)
        {
            if (_lastHiddenSeen >= 0)
                Console.WriteLine($"HIDDEN CHANGED {_lastHiddenSeen:N0} -> {_hiddenThisFrame:N0} " +
                                  $"placements skipped as backdrop");
            _lastHiddenSeen = _hiddenThisFrame;
        }
    }

    private static string Short(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>What the binder found in the emissive slots. Counted, not rendered -- yet.</summary>
    public IEnumerable<string> EmissiveReport()
    {
        if (_textures is null) { yield return "no texture cache"; yield break; }
        yield return $"BOUND {_textures.EmissiveBound:N0} section(s) glow; " +
                     $"{_textures.EmissiveRejected:N0} refused as placeholders";
        var total = _textures.EmissiveTextured + _textures.EmissivePlaceholder +
                    _textures.EmissiveColourOnly + _textures.EmissiveNone;
        yield return $"{total:N0} section(s) measured: " +
                     $"{_textures.EmissiveTextured:N0} with a real emissive texture, " +
                     $"{_textures.EmissivePlaceholder:N0} resolving only a PLACEHOLDER, " +
                     $"{_textures.EmissiveColourOnly:N0} with an emissive colour and no texture, " +
                     $"{_textures.EmissiveNone:N0} with neither";
        foreach (var line in _textures.EmissiveLog.Take(40)) yield return "   " + line;
    }

    /// <summary>
    /// Hand the shader the level's own lights, once a frame.
    ///
    /// ONLY IN "Rendered". Solid is flat grey and costs nothing, Material is base colour so an
    /// author can read the artwork -- lighting either would be answering a question they did not
    /// ask. Rendered is the mode whose whole purpose is "what it looks like in the game", which is
    /// where the author asked for this.
    ///
    /// DIRECTIONS ARE CONVERTED. Normals reach the shader through mat3(uModel), so they are in GL
    /// space, and a light direction left in UE space lights the level from the wrong side -- the
    /// kind of wrong that looks like an art choice. UeToGl swaps Y and Z and flips handedness;
    /// using that one matrix rather than a hand-written swap is what keeps this honest.
    /// </summary>
    private void SetRig()
    {
        if (_shader is null) return;
        var rig = _editor?.Lights;
        // A LEVEL WITH NO SUN IS STILL A LEVEL. This used to require a directional light, which
        // sent MM_02_030_TheDarkGrotto -- a cave with no sun, a sky at 0.01 and 230 lamps -- back
        // to the studio three-point rig, i.e. to the one look the author said was not close.
        var lit = _editor?.Shading == "Rendered" && rig is not null &&
                  (rig.Suns.Count > 0 || rig.Spots.Count > 0);

        _shader.Set("uLevelLit", lit ? 1 : 0);
        if (!lit || rig is null) { _shader.Set("uSunCount", 0); return; }

        // RELATIVE, NOT ABSOLUTE. UE intensities are photometric and the tonemapper downstream
        // has its own exposure, so the key light is normalised to 1 and everything else keeps its
        // ratio to it. That preserves the thing the rig actually encodes -- a violet sky at 1.75
        // against a pale sun at 8.4 -- without pretending this renderer knows what a lux is.
        var key = MathF.Max(rig.Key?.Intensity ?? 1f, 0.001f);

        var suns = rig.Suns.OrderByDescending(x => x.Intensity).Take(4).ToList();
        _shader.Set("uSunCount", suns.Count);
        for (var i = 0; i < suns.Count; i++)
        {
            var d = Vector3.TransformNormal(suns[i].Direction, RenderMesh.UeToGl);
            if (d.LengthSquared() > 1e-8f) d = Vector3.Normalize(d);
            _shader.Set($"uSunDir[{i}]", d);
            _shader.Set($"uSunRadiance[{i}]", suns[i].Colour * (suns[i].Intensity / key));
        }

        var skyUp = (rig.Sky?.Colour ?? new Vector3(0.6f, 0.7f, 0.9f)) *
                    ((rig.Sky?.Intensity ?? 1f) / key);

        // TIMES THE KEY SUN'S OWN IndirectLightingIntensity, which was read, stored, printed in
        // the rig description, and never once used to light anything.
        //
        // THIS GAME BAKES NOTHING. Zero MapBuildDataRegistry packages ship, zero shadow maps,
        // 0.2 MB of lightmaps in the whole game -- so every interior is lit by Lumen at runtime,
        // and there is no baked product to read instead. The hangar proves the consequence: its
        // own sublevel holds 2 lights, the nearest fixture in the map's 138 is 7,295 units away
        // with a 2,281 radius, and NOT ONE light reaches it. In the game that room is lit by
        // bounce. Under a shadowing sun with no GI it renders at mean luma 0.133, near black --
        // correct for what we simulate and useless to look at.
        //
        // `IndirectLightingIntensity` is the engine's own statement of how much bounce that sun
        // contributes, and the hub's says **2**. Scaling the hemisphere by it is still a stand-in
        // for GI rather than GI, but it is a stand-in with a number the level chose rather than
        // one I picked to make a screenshot look better -- which is the whole difference between
        // this and a brightness dial.
        var indirect = MathF.Max(rig.Key?.Indirect ?? 1f, 0.05f);
        skyUp *= indirect;
        _shader.Set("uSkyUp", skyUp);

        // Ground bounce, dimmer and without the sky's blue. Not GI -- a two-colour hemisphere is
        // the cheapest thing that stops undersides reading as holes, and saying that plainly is
        // better than implying this is indirect lighting.
        _shader.Set("uSkyDown", skyUp * 0.35f);
        _shader.Set("uExposure", Exposure);
        _shader.Set("uEmissiveStrength", EmissiveStrength);
        SetLocalLights(rig, key);

        if (rig.Fog is { } fog)
        {
            _shader.Set("uHasFog", 1);
            _shader.Set("uFogColour", fog.Colour);
            _shader.Set("uFogDensity", fog.Density);
            _shader.Set("uFogFalloff", fog.HeightFalloff);
            _shader.Set("uFogHeight", fog.Height);

            // UE's density is per its own unit; this bridges to centimetres. Picked so Umbara's
            // 0.1 reads as haze over a few hundred metres rather than a wall at arm's length,
            // and left adjustable because it is the one number here with no source in the data.
            _shader.Set("uFogScale", FogScale);
        }
        else _shader.Set("uHasFog", 0);
    }

    /// <summary>How many local lights the shader can hold. Matches uLocalPos[32] exactly.</summary>
    private const int MaxLocal = 32;

    /// <summary>
    /// The level's own fixtures: spots and point lights, the 32 most relevant sent each frame.
    ///
    /// WHY NOT ALL OF THEM. The Dark Grotto has 230 and the hub 179; a fragment loop over every
    /// light in the level costs more than the whole rest of the frame and most of them are behind
    /// the camera in another room.
    ///
    /// WHY NOT THE 32 NEAREST. A light's importance is not its distance, it is how far the eye is
    /// INSIDE its reach: the hub hangs 500-candela floodlights with a 600-metre radius over the
    /// Ring of Kafrene, and a desk lamp two rooms away with a 34-unit radius would out-rank them
    /// on distance alone while lighting nothing the camera can see.
    ///
    /// AND WHY NOT (distance - reach), WHICH IS WHAT THIS DID AND WAS WRONG. That subtraction is
    /// unnormalised, so it does not rank by how far inside a light you are -- it ranks by how BIG
    /// the light is. This map's radii run from 34 to 800,000: a light with a 800,000 radius scores
    /// about -800,000 from anywhere in the level and can never be displaced, so a handful of
    /// enormous vista floodlights held the 32 slots permanently and the hangar's own fixtures --
    /// radius 1,000, the ones actually lighting the room -- never reached the shader at all. The
    /// hangar rendered at mean luma 0.133, near black, with 138 lights available and the wrong 32
    /// chosen.
    ///
    /// (distance / reach) is the fraction of the way to the edge of a light's reach, which is
    /// scale-free: the desk lamp scores 59, the vista floodlight 0.83, the hangar fixture 0.3.
    ///
    /// MEASURED FROM THE NEARER OF EYE AND TARGET, not from the eye, and that correction is the
    /// one that actually mattered. Lighting is evaluated at the SURFACE; the eye is just where
    /// you stand. Framing a sublevel puts the camera on its bounding sphere -- 5,839 units back
    /// from the hangar -- so measured from the eye the nearest fixture scored 2.21 and **not one
    /// of the 138 lights was within its own reach**, while every one of them was plainly lighting
    /// the surfaces on screen. Ranking from the eye alone works only when the camera is inside
    /// the room, which in an editor it usually is not.
    ///
    /// `Camera.Target` is what the view is filled with, so the nearer of the two covers both
    /// standing in the room and looking into it from outside.
    ///
    /// This is a whole-frame choice, not a per-object one. Per-object would be more accurate and
    /// would mean re-sending 13 uniforms times 32 lights for each of 26,000 placements, every
    /// frame -- roughly eleven million uniform writes to light a level that fits in one upload.
    /// </summary>
    private int _lightPickLogged = -1;

    private void SetLocalLights(LevelLights.Rig rig, float key)
    {
        if (_shader is null) return;
        if (rig.Spots.Count == 0) { _shader.Set("uLocalCount", 0); return; }

        var eye = Camera.Position;
        var look = Camera.Target;
        var chosen = rig.Spots
            .Select(x => (Light: x, At: Vector3.Transform(x.At, RenderMesh.UeToGl)))
            .Select(x => (x.Light, x.At,
                          Rank: MathF.Min((x.At - eye).Length(), (x.At - look).Length())
                                / MathF.Max(x.Light.Radius, 1f)))
            // OUTSIDE ITS REACH IS NOT DIM, IT IS NOTHING. A light whose radius does not span the
            // gap contributes zero in the shader anyway, so keeping it would spend a slot on a
            // guaranteed no-op. Generous by a quarter, because the camera moves and a light
            // popping in at the boundary is worse than one arriving early.
            .OrderBy(x => x.Rank)
            .Take(MaxLocal)
            .ToList();

        // WHAT THE RANKING ACTUALLY PICKED, printed once per open rather than per frame. Culling
        // everything outside reach looked correct and returned ZERO lights on the hangar, which
        // is either a camera nowhere near any fixture or a mistake in the ranking -- and the two
        // are indistinguishable from a luma figure. So the numbers get printed.
        if (_lightPickLogged != rig.Spots.Count)
        {
            _lightPickLogged = rig.Spots.Count;
            Console.WriteLine($"LIGHTPICK eye {eye.X:0},{eye.Y:0},{eye.Z:0}  " +
                              $"target {look.X:0},{look.Y:0},{look.Z:0} - nearest by distance/reach:");
            foreach (var (l, at, rank) in chosen.Take(6))
                Console.WriteLine($"LIGHTPICK   ratio {rank,8:0.00}  dist {(at - eye).Length(),10:0}  " +
                                  $"radius {l.Radius,9:0}  {(l.IsPoint ? "point" : "spot ")}  " +
                                  $"{l.Intensity,8:0.#} {l.Units}");
            var reach = chosen.Count(x => x.Rank <= 1f);
            Console.WriteLine($"LIGHTPICK   {reach:N0} of {chosen.Count:N0} sent are within their own reach");
        }

        _shader.Set("uLocalCount", chosen.Count);
        for (var i = 0; i < chosen.Count; i++)
        {
            var (light, at, _) = chosen[i];
            var dir = Vector3.TransformNormal(light.Direction, RenderMesh.UeToGl);
            if (dir.LengthSquared() > 1e-8f) dir = Vector3.Normalize(dir);

            _shader.Set($"uLocalPos[{i}]", at);
            _shader.Set($"uLocalDir[{i}]", dir);
            _shader.Set($"uLocalColour[{i}]", light.Colour * (light.LuxAtOneMetre / key));

            // A POINT LIGHT GETS cos(outer) = -1, so the cone term is 1 in every direction and the
            // shader needs no branch. Reading IsPoint rather than trusting the cone angles matters:
            // a PointLightComponent has no cone properties at all, so it would inherit whatever
            // default the reader supplies and light a 44-degree wedge of the room.
            var cosOuter = light.IsPoint ? -1f : MathF.Cos(Deg(light.Outer));
            var cosInner = light.IsPoint ? 1f : MathF.Cos(Deg(MathF.Min(light.Inner, light.Outer - 0.5f)));
            var band = MathF.Max(cosInner - cosOuter, 0.001f);

            _shader.Set($"uLocalShape[{i}]", new System.Numerics.Vector4(
                MathF.Max(light.Radius, 1f), cosOuter, 1f / band,
                light.InverseSquared ? 0f : MathF.Max(light.FalloffExp, 0.01f)));
        }

        LocalLit = chosen.Count;
        LocalKnown = rig.Spots.Count;
    }

    private static float Deg(float d) => d * MathF.PI / 180f;

    /// <summary>Local lights sent to the shader this frame, and how many the level has.</summary>
    public int LocalLit { get; private set; }
    public int LocalKnown { get; private set; }

    /// <summary>Scales the whole rig before the tonemapper. Adjustable because taste is involved.</summary>
    public float Exposure { get; set; } = 0.7f;

    /// <summary>
    /// How hard an emissive map glows. A DIAL, not a reading -- the second one in this renderer,
    /// beside FogScale, and for the same reason: UE multiplies emissive by a constant inside the
    /// material graph, and a cooked instance does not carry it.
    /// </summary>
    public float EmissiveStrength { get; set; } = 1f;

    /// <summary>
    /// Centimetres-to-UE-fog-units. THE ONE NUMBER HERE WITH NO SOURCE IN THE DATA.
    ///
    /// Everything else in the rig is read from the level. UE's fog density is in its own units
    /// against its own integral, and reproducing that exactly means reproducing the engine's
    /// formula; this is a fit. At 0.00015 Umbara's density of 0.1 reads as roughly a third
    /// obscured at 300 metres, which is about where that planet sits. If it looks wrong it IS
    /// wrong, and it is one constant to move.
    /// </summary>
    public float FogScale { get; set; } = 0.00015f;

    /// <summary>
    /// The colour the sky reads as where nothing is drawn.
    ///
    /// A level with fog in it has no empty background: the fog IS what you see at infinity, so
    /// clearing to a fixed dark blue puts a hard edge around every silhouette and tells you the
    /// haze stops at the geometry. Clearing to the fog's own tonemapped colour makes distance
    /// close continuously, which is most of why a foggy level reads as deep rather than flat.
    /// </summary>
    private Vector3 Background()
    {
        var rig = _editor?.Lights;
        if (_editor?.Shading != "Rendered" || rig?.Fog is not { } fog) return new Vector3(0.07f, 0.09f, 0.12f);
        var c = Aces(fog.Colour * Exposure);
        return new Vector3(MathF.Pow(c.X, 1 / 2.2f), MathF.Pow(c.Y, 1 / 2.2f), MathF.Pow(c.Z, 1 / 2.2f));
    }

    /// <summary>The same ACES fit the shader uses, so the background cannot drift from the fog.</summary>
    private static Vector3 Aces(Vector3 x)
    {
        var n = x * (2.51f * x + new Vector3(0.03f));
        var d = x * (2.43f * x + new Vector3(0.59f)) + new Vector3(0.14f);
        return Vector3.Clamp(new Vector3(n.X / d.X, n.Y / d.Y, n.Z / d.Z), Vector3.Zero, Vector3.One);
    }

    /// <summary>Placements the last frame skipped as hidden backdrop. Diagnostic.</summary>
    public int LastHidden { get; private set; }

    /// <summary>How long loading meshes has cost, and what the worst single one was.</summary>
    public int LoadCount { get; private set; }
    public double LoadMs { get; private set; }
    public double SlowestMs { get; private set; }
    public string Slowest { get; private set; } = "";

    /// <summary>What the binder recorded for every section it dressed, reasons included.</summary>
    public IReadOnlyList<string> BoundTextures =>
        _textures?.BoundLog ?? (IReadOnlyList<string>) Array.Empty<string>();

    /// <summary>Sections that dressed without error and still have no base colour.</summary>
    public int NoDiffuse { get; private set; }
    public List<string> BareMeshes { get; } = new();

    /// <summary>Material binds that threw, and the first one, so grey can be explained.</summary>
    public int DressFailed { get; private set; }
    public string LastDressFail { get; private set; } = "";

    /// <summary>Meshes still waiting for their materials.</summary>
    public int DressPending => _toDress.Count;

    /// <summary>
    /// HOW MUCH OF THE SCENE ACTUALLY GOT A BASE COLOUR, counted over the sections that exist.
    ///
    /// "tons of stuff that doesn't get textures" was his report months ago and it has never had
    /// a number. A mesh with no diffuse draws WHITE and throws nothing, so a level can be most
    /// of the way untextured and every count in the app still reads full: meshes known, meshes
    /// ready and materials bound are all about work ATTEMPTED, not about the picture.
    ///
    /// Sections, not meshes, because one mesh can have a textured hull and a bare interior.
    /// </summary>
    public (int Textured, int Bare) SectionTexturing()
    {
        int textured = 0, bare = 0;
        foreach (var m in _meshes.Values)
        {
            if (m is null) continue;
            foreach (var s in m.Sections)
                if (s.Diffuse != 0) textured++; else bare++;
        }
        return (textured, bare);
    }

    /// <summary>
    /// HOW MANY SECTIONS WEAR AN RGB TRIM SHEET, and how many of those replace their diffuse.
    ///
    /// The witness for that feature. Without it the change is only a shift in mean luma, which
    /// cannot tell "the blend is working" from "something else moved" -- and a shift is exactly
    /// what a wrongly-signed or wrongly-spaced colour would also produce.
    /// </summary>
    /// <summary>
    /// HOW MANY SECTIONS ACTUALLY GOT A NORMAL MAP, and how many a detail normal.
    ///
    /// Written because the detail-normal binder logged only its REJECTIONS, so "0 log lines"
    /// could not tell "nothing asked for one" from "every one worked" -- and the whole reason
    /// normal mapping sat unsampled for months is that nothing ever counted it. A count of work
    /// ATTEMPTED is not a count of work done; this counts what the drawn sections hold.
    ///
    /// Scoped to what is drawn, for the reason SectionTrim is.
    /// </summary>
    /// <summary>
    /// DRAWN SECTIONS PER MATERIAL FAMILY -- the ranking that shader work should actually follow.
    ///
    /// `--matfamilies` ranks families by how many material INSTANCES exist, which is a proxy that
    /// can be flatly wrong: MM_Rocks is third with 396 instances and no map in the game places
    /// one, so its whole projection branch draws nothing. This counts what is on screen.
    /// </summary>
    public List<(string Family, int Sections)> SectionFamilies()
    {
        var by = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, m) in _meshes)
        {
            if (m is null) continue;
            if (SoloMesh.Length > 0 && !path.Contains(SoloMesh, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var s in m.Sections)
            {
                var k = s.Family.Length > 0 ? s.Family : "(unresolved)";
                by[k] = by.GetValueOrDefault(k) + 1;
            }
        }
        return by.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public (int WithNormal, int WithDetail, int WithUvTint, int WithEmSheet,
            int WithProj, int WithProjNrm, int WithProjAlpha, int WithFuzz, int Total) SectionNormals()
    {
        int withNormal = 0, withDetail = 0, withUvTint = 0, withEmSheet = 0, total = 0;
        int withProj = 0, withProjNrm = 0, withProjAlpha = 0, withFuzz = 0;
        foreach (var (path, m) in _meshes)
        {
            if (m is null) continue;
            if (SoloMesh.Length > 0 && !path.Contains(SoloMesh, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var s in m.Sections)
            {
                total++;
                if (s.NormalMap != 0) withNormal++;
                if (s.DetailNormal != 0) withDetail++;
                if (s.UvTint) withUvTint++;
                if (s.EmSheet != 0 && s.EmMask != 0) withEmSheet++;
                if (s.ProjColor != 0) withProj++;
                if (s.ProjColor != 0 && s.ProjNrm != 0) withProjNrm++;
                if (s.ProjColor != 0 && s.ProjAlpha != 0) withProjAlpha++;
                if (s.Fuzz) withFuzz++;
            }
        }
        return (withNormal, withDetail, withUvTint, withEmSheet,
                withProj, withProjNrm, withProjAlpha, withFuzz, total);
    }

    /// <summary>
    /// MM_EMISSIVE, COUNTED OVER WHAT IS DRAWN. Four numbers rather than one, because the whole
    /// point of this family is that most of it glows with no map: a count of bound emissive
    /// TEXTURES reports near zero on a level full of glowing panels and reads as a broken binder.
    /// `Off` is the eleven instances whose authored strength or colour works out to nothing --
    /// those are correct zeroes and have to be distinguishable from missing ones.
    /// </summary>
    public (int Mapped, int Flat, int Tinted, int Warmed, int Total) SectionEmissive()
    {
        int mapped = 0, flat = 0, tinted = 0, warmed = 0, total = 0;
        foreach (var (path, m) in _meshes)
        {
            if (m is null) continue;
            if (SoloMesh.Length > 0 && !path.Contains(SoloMesh, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var s in m.Sections)
            {
                total++;
                if (s.Emissive != 0) mapped++;
                if (s.EmissiveFlat) flat++;
                // A tint that is not white is the family's colour parameter having been READ --
                // the fault this replaces bound the name 9 of 205 instances set.
                if ((s.Emissive != 0 || s.EmissiveFlat) &&
                    MathF.Abs(s.EmissiveTint.X - 1f) + MathF.Abs(s.EmissiveTint.Y - 1f) +
                    MathF.Abs(s.EmissiveTint.Z - 1f) > 0.02f) tinted++;
                if (MathF.Abs(s.EmissiveScale - 1f) > 0.001f) warmed++;
            }
        }
        return (mapped, flat, tinted, warmed, total);
    }

    /// <summary>
    /// WHAT THE BINDER ACTUALLY RESOLVED, per glowing section, so the reading can be checked
    /// against the authored value rather than against the picture.
    ///
    /// The counters say how many sections glow and the pixels say what colour they came out; the
    /// step between -- did the right PARAMETER reach this section -- is the one that can silently
    /// be wrong, and neither of the other two can see it. A frame whose emissive pixels measured
    /// pink when nothing in the family's authored palette is pink could not be diagnosed from
    /// either end.
    /// </summary>
    public List<string> EmissiveResolved(int take)
    {
        var rows = new List<string>();
        foreach (var (path, m) in _meshes)
        {
            if (m is null) continue;
            if (SoloMesh.Length > 0 && !path.Contains(SoloMesh, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var s in m.Sections)
            {
                if (s.Emissive == 0 && !s.EmissiveFlat) continue;
                // THE MESH TOO, not just the material. A row that names only the material
                // cannot be pointed a camera at, and looking at the thing is the last check.
                rows.Add($"{path[(path.LastIndexOf('/') + 1)..],-42} " +
                         $"{s.MaterialName,-40} {(s.EmissiveFlat ? "FLAT" : "MAP "),-5} " +
                         $"tint {s.EmissiveTint.X:0.###},{s.EmissiveTint.Y:0.###},{s.EmissiveTint.Z:0.###}" +
                         $"  scale {s.EmissiveScale:0.###}  family {s.Family}");
                if (rows.Count >= take) return rows;
            }
        }
        return rows;
    }

    /// <summary>
    /// Decals whose shape comes from a channel mask, and -- the number that matters -- how many
    /// sections would otherwise draw as a FULL RECTANGLE because they have neither a diffuse nor
    /// a mask to cut them out with. That second count is the one that was invisible: a decal with
    /// no artwork reports no error, binds nothing, and quietly paints a grey quad.
    /// </summary>
    public (int Masked, int NoDiffuseRescued, int StillSolid, int Decals) SectionDecalMask()
    {
        int masked = 0, rescued = 0, solid = 0, decals = 0;
        foreach (var (path, m) in _meshes)
        {
            if (m is null) continue;
            if (SoloMesh.Length > 0 && !path.Contains(SoloMesh, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var s in m.Sections)
            {
                if (!s.Family.Equals("MM_Decals", StringComparison.OrdinalIgnoreCase)) continue;
                decals++;
                if (s.DecalMask != 0) masked++;
                if (s.DecalMask != 0 && s.Diffuse == 0) rescued++;
                if (s.DecalMask == 0 && s.Diffuse == 0) solid++;
            }
        }
        return (masked, rescued, solid, decals);
    }

    public (int Trimmed, int Replacing, int Uv1) SectionTrim()
    {
        int trimmed = 0, replacing = 0, uv1 = 0;
        // SCOPED TO WHAT IS DRAWN. --solo hides at draw time and still LOADS everything -- 437 of
        // 437 meshes with one soloed -- so counting over _meshes reported the whole level while
        // the frame showed one asset. A count that does not measure what the picture shows is the
        // fault this codebase keeps re-finding; here it hid whether the soloed mesh wore a sheet
        // at all, which was the entire question the solo run existed to answer.
        foreach (var (path, m) in _meshes)
        {
            if (m is null) continue;
            if (SoloMesh.Length > 0 && !path.Contains(SoloMesh, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var s in m.Sections)
                if (s.TrimMask != 0)
                {
                    trimmed++;
                    if (s.TrimNoDif) replacing++;
                    if (s.TrimUv1) uv1++;
                }
        }
        return (trimmed, replacing, uv1);
    }

    /// <summary>Meshes with geometry on the GPU right now, and how many are still queued.</summary>
    public int MeshesReady => _meshes.Values.Count(m => m is not null);
    public int MeshesQueued => _toLoad.Count;
    public int MeshesKnown => _meshes.Count;

    /// <summary>
    /// Where the default cut-off sits, in multiples of the level's own typical radius.
    ///
    /// ADJUSTABLE BECAUSE THERE IS NO CLEAN LINE TO FIND. I expected the vista to separate from
    /// the level, and measured instead that it does not: on SK_Anaxes_020 the hidden count slides
    /// 2,407 / 445 / 334 / 237 / 158 / 139 / 110 across factors 1.5 to 8 with no plateau anywhere,
    /// and the SAME MESHES fall on both sides -- `SM_OTR_ANA_BasaltColumnA_Cap` is 40 hidden and
    /// 15 kept at x4. The distant scenery is not a separate backdrop layer, it is the same cliffs
    /// receding, so any fixed threshold cuts somewhere arbitrary.
    ///
    /// The game's own naming does not rescue it either: only 80 placements of 13,828 have "Vista"
    /// anywhere in the mesh path or sublevel, while the things actually in the way are basalt
    /// caps and cooled-lava floors that carry no such label.
    ///
    /// So the author gets the dial rather than my guess, and the count of what it hides.
    /// </summary>
    private const float DefaultBackdropFactor = 4f;

    private float BackdropFactor => (float) (_editor?.BackdropFactor ?? DefaultBackdropFactor);

    private void MeasureCore()
    {
        _backdrop = new bool[_scene.Count];
        if (_scene.Count == 0) { _core = Vector3.Zero; _coreRadius = 1f; return; }

        // MEASURE WHAT IS SHOWN, NOT WHAT IS LOADED.
        //
        // The far plane is derived from the farthest placement, and a backdrop eleven kilometres
        // out drags it to 1,133,413 -- which at a camera 467 units in makes the near plane 5.67
        // and the ratio 200,000:1. A 24-bit depth buffer has nothing left at that spread, and
        // surfaces a hand's width apart start trading places. Forcing near small in an experiment
        // did exactly this on purpose and the picture fell apart; the level does it by accident
        // whenever somebody zooms in with a vista loaded.
        //
        // So turning a sublevel off has to shrink the SCENE, not just stop it drawing. Hiding the
        // Ring of Kafrene should buy back the depth precision it was costing.
        var hiddenSubs = _editor?.HiddenSublevels;
        var shown = hiddenSubs is { Count: > 0 }
            ? _scene.Where(p => !hiddenSubs.Contains(p.HideKey)).ToList()
            : _scene;
        if (shown.Count == 0) shown = _scene;      // everything hidden: measure what there is

        var xs = shown.Select(p => p.At.X).ToArray();
        var ys = shown.Select(p => p.At.Y).ToArray();
        var zs = shown.Select(p => p.At.Z).ToArray();

        static float Middle(float[] v)
        {
            var t = v.Order().ToArray();
            return t[t.Length / 2];
        }

        // Half-width on one axis: the median distance from the median, widened a little. 2 MAD is
        // the exact extent of an even spread, so the extra 0.5 is headroom for the props at the
        // edge rather than a fudge factor.
        static float Reach(float[] v, float middle)
        {
            var d = v.Select(x => MathF.Abs(x - middle)).Order().ToArray();
            return MathF.Max(d[d.Length / 2] * 2.5f, 100f);
        }

        _core = new Vector3(Middle(xs), Middle(ys), Middle(zs));
        _coreReach = new Vector3(Reach(xs, _core.X), Reach(ys, _core.Y), Reach(zs, _core.Z));

        var radii = shown.Select(p => (p.At - _core).Length()).Order().ToArray();
        _coreRadius = MathF.Max(radii[radii.Length / 2], 1f);

        // ALMOST the whole scene. Everything above is median-based on purpose -- outliers are what
        // the backdrop filter exists to find -- but a far plane that ignores outliers clips them,
        // and they are the vista and the far side of the map.
        //
        // NOT the single farthest, though. Umbara's is SM_SkyDome_ZenithFlattened_Mirrored at
        // 999,378 units, where the farthest real thing is a tree at 65,183. Letting one sky dome
        // set the depth range costs a factor of fifteen of near-plane precision for every other
        // placement -- and the dome sat outside the old far plane at any normal zoom anyway, so
        // nothing that was visible becomes invisible.
        //
        // Rejected on the GAP rather than a percentile. A flat 99.9% cut clipped 28 real trees to
        // exclude one dome; anything within 5x the 99th percentile is level, and the dome is 72x.
        var p99 = radii[(int) (radii.Length * 0.99)];
        var cap = MathF.Max(p99 * 5f, 1000f);
        var farthestReal = radii.LastOrDefault(r => r <= cap);
        _allRadius = MathF.Max(farthestReal, _coreRadius);

        MarkBackdrop();
    }

    /// <summary>Re-flag what counts as backdrop. Cheap, so the dial can move live.</summary>
    public void MarkBackdrop()
    {
        if (_backdrop.Length != _scene.Count) _backdrop = new bool[_scene.Count];
        var edge = _coreRadius * BackdropFactor;
        for (var i = 0; i < _scene.Count; i++) _backdrop[i] = (_scene[i].At - _core).Length() > edge;

        if (_editor is null) return;
        var n = _backdrop.Count(b => b);
        Dispatcher.UIThread.Post(() => _editor.BackdropCount = n);
        RequestNextFrameRendering();
    }

    /// <summary>
    /// How the level separates from its backdrop at several cut-offs, so the constant above is
    /// chosen from the data rather than picked. A clean separation shows as a RANGE of factors
    /// that all hide the same count; a factor sitting on a slope is one that will cut a level in
    /// half on some other map.
    /// </summary>
    public string BackdropReport()
    {
        if (_scene.Count == 0) return "BACKDROP no scene";
        var d = _scene.Select(p => (p.At - _core).Length()).ToArray();
        var lines = new List<string>
        {
            $"BACKDROP core {_core.X:N0},{_core.Y:N0},{_core.Z:N0} radius {_coreRadius:N0}",
        };
        foreach (var f in new[] { 1.5f, 2f, 3f, 4f, 5f, 6f, 8f, 12f })
            lines.Add($"BACKDROP   x{f,-5:0.#} hides {d.Count(v => v > _coreRadius * f),7:N0} of {_scene.Count:N0}");

        // Does the GAME label its own backdrop? A name the artists wrote is worth more than a
        // threshold I picked, if it turns out to be used consistently.
        var named = _scene.Count(p => p.MeshPath.Contains("Vista", StringComparison.OrdinalIgnoreCase)
                                   || p.Sublevel.Contains("Vista", StringComparison.OrdinalIgnoreCase));
        lines.Add($"BACKDROP   named 'Vista' anywhere: {named:N0} of {_scene.Count:N0}");

        lines.Add("BACKDROP   what x4 hides:");
        foreach (var g in _scene.Where((_, i) => d[i] > _coreRadius * BackdropFactor)
                                .GroupBy(p => p.MeshPath[(p.MeshPath.LastIndexOf('/') + 1)..])
                                .OrderByDescending(g => g.Count()).Take(10))
            lines.Add($"BACKDROP      {g.Count(),6:N0}  {g.Key}");

        lines.Add("BACKDROP   what x4 KEEPS that is still far out (x2..x4):");
        foreach (var g in _scene.Where((_, i) => d[i] > _coreRadius * 2 && d[i] <= _coreRadius * BackdropFactor)
                                .GroupBy(p => p.MeshPath[(p.MeshPath.LastIndexOf('/') + 1)..])
                                .OrderByDescending(g => g.Count()).Take(10))
            lines.Add($"BACKDROP      {g.Count(),6:N0}  {g.Key}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Is this placement hidden right now? Backdrop, greybox, or a sublevel turned off.
    ///
    /// THE SUBLEVEL TEST IS BY NAME AND NOT BY AN INDEX ARRAY, on purpose: `_scene` and the
    /// editor's Placements have to stay the SAME list in the same order, because a pick hands
    /// back an index into one and the window reads it out of the other. Filtering the list would
    /// have shifted every index after the first hidden sublevel and silently selected the wrong
    /// actor. The mesh saving is taken in Rebuild instead, where nothing is indexed.
    /// </summary>
    /// <summary>
    /// SHOW ONLY PLACEMENTS DRAWING THIS MESH, or "" for everything.
    ///
    /// Built because I judged a material against a close-up of a hangar floor and called a
    /// regression on it. A weathering mask lays thin seams, rust and grime at a few per cent
    /// coverage; two metres from a blown-out white surface every dark fleck is maximum contrast,
    /// and the one thing the frame could not show is whether the surface is RIGHT. One asset,
    /// framed, against itself with the feature off, is the comparison that means something.
    /// </summary>
    public string SoloMesh = "";

    private bool Hidden(int i) =>
        _editor is not null &&
        ((SoloMesh.Length > 0 && i < _scene.Count &&
          _scene[i].Shown?.Contains(SoloMesh, StringComparison.OrdinalIgnoreCase) != true) ||
         (i < _backdrop.Length && _backdrop[i] && !_editor.ShowBackdrop) ||
         (i < _blockout.Length && _blockout[i] && !_editor.ShowBlockout) ||
         (_editor.HiddenSublevels.Count > 0 && i < _scene.Count &&
          _editor.HiddenSublevels.Contains(_scene[i].HideKey)));

    /// <summary>
    /// What the "Export what's loaded to FBX" scope left out, and why, so the Log can say it instead of the file
    /// quietly being smaller than the picture. See <see cref="DrawnPlacements(out DrawnInfo)"/>.
    /// </summary>
    /// <param name="Scene">Placements in the viewport's scene.</param>
    /// <param name="Hidden">Turned off right now: a sublevel unticked, the backdrop or the greybox switched off.</param>
    /// <param name="Spawned">Unsaved new actors (previews). They are not in the game's files, so they are not exported.</param>
    /// <param name="StillLoading">Placements whose mesh the viewport has not finished loading: not on screen yet, so not exported.</param>
    /// <param name="NotLoadable">Placements whose mesh the viewport tried and could not load: not on screen, so not exported.</param>
    /// <param name="Edited">Included placements that carry an unsaved swap or removal; they are exported as the game ships them.</param>
    /// <param name="NotLoadableMeshes">Each mesh behind <paramref name="NotLoadable"/>, with the loader's reason and how many placements use it.</param>
    public readonly record struct DrawnInfo(int Scene, int Hidden, int Spawned, int StillLoading, int NotLoadable,
                                            int Edited, IReadOnlyList<string> NotLoadableMeshes);

    /// <summary>
    /// THE PLACEMENTS THE CURRENT FRAME DRAWS, as the game ships them -- what "Export what's loaded to FBX" is
    /// made of.
    ///
    /// The same three tests the frame makes, in the same order: <see cref="Hidden"/> (a sublevel unticked, the
    /// backdrop or the greybox off), then "has a mesh", then "that mesh is loaded". Nothing else is a scope rule,
    /// and no position is computed here: the list is the viewport's own <c>_scene</c> (the SAME list, in the same
    /// order, the frame and the picker index into), so the file cannot hold a placement the picture does not -- bar the
    /// stock placement behind an unsaved removal or swap, below.
    ///
    /// WHAT THE PICTURE SHOWS THAT THE FILE MUST NOT. The export is the stock map: an unsaved MOVE lives in
    /// <c>Offset</c>/<c>Warp</c> and is simply not read by the caller; an unsaved SWAP or REMOVAL changes
    /// <c>Shown</c> but not <c>MeshPath</c>, so a swapped or removed placement is kept here (as the game ships it)
    /// and is not held to the loaded test, because the mesh the viewport has loaded for it is the replacement, not
    /// the original; an unsaved SPAWN is a preview of something the game does not have and is dropped.
    ///
    /// THREAD. <c>_scene</c>, <c>_meshes</c>, <c>_backdrop</c> and <c>_blockout</c> belong to the thread that
    /// renders, and that is the UI thread (measured on Avalonia 12.1.2: <c>OnOpenGlRender</c> answers
    /// <c>Dispatcher.UIThread.CheckAccess()</c> with true), so call this from the UI thread and nowhere else. Called
    /// from a worker it would read collections the render pass is writing.
    /// </summary>
    public IReadOnlyList<LevelReader.Placement> DrawnPlacements() => DrawnPlacements(out _);

    /// <inheritdoc cref="DrawnPlacements()"/>
    public IReadOnlyList<LevelReader.Placement> DrawnPlacements(out DrawnInfo info)
    {
        var drawn = new List<LevelReader.Placement>(_scene.Count);
        int hidden = 0, spawned = 0, loading = 0, failed = 0, edited = 0;
        var failedMeshes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _scene.Count; i++)
        {
            if (Hidden(i)) { hidden++; continue; }
            var p = _scene[i];
            if (p.Spawned is not null) { spawned++; continue; }

            if (p.Draws is not null)
            {
                // An unsaved swap or removal: the frame draws something else (or nothing), and the stock mesh may
                // not be loaded at all. The file is the stock map, so it stays.
                edited++;
                drawn.Add(p);
                continue;
            }

            if (!_meshes.TryGetValue(p.MeshPath, out var mesh)) { loading++; continue; }
            if (mesh is null)
            {
                // Null is "queued" until the loader has had its turn, and "failed" afterwards.
                if (_failed.ContainsKey(p.MeshPath)) { failed++; failedMeshes[p.MeshPath] = failedMeshes.GetValueOrDefault(p.MeshPath) + 1; }
                else loading++;
                continue;
            }
            drawn.Add(p);
        }
        info = new DrawnInfo(_scene.Count, hidden, spawned, loading, failed, edited,
                             failedMeshes.Select(kv => $"{kv.Key} ({kv.Value:N0} placement(s)): {_failed[kv.Key]}").ToList());
        return drawn;
    }

#if DEV
    /// <summary>DEV: where the selected placement sits, in GL space (<c>--camshots</c> frames a picked object with it).</summary>
    public System.Numerics.Vector3? SelectedPivot => GizmoAt();

    /// <summary>The distinct materials of the given loaded meshes, for the export driver's pre-flight estimate.</summary>
    public IReadOnlyList<string> LoadedMaterialPaths(IEnumerable<string> meshPaths) =>
        meshPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                 .Select(p => _meshes.TryGetValue(p, out var m) ? m : null).Where(m => m is not null)
                 .SelectMany(m => m!.Sections).Select(s => s.MaterialPath)
                 .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// WHAT THE VIEWPORT HOLDS ON THE GPU, for the memory report: meshes and their buffers, the textures the cache
    /// owns and how many of them any live section or override skin still points at. Bytes are the ESTIMATES the
    /// counters keep (width * height * 4 plus a third for the mips; vertex and index bytes as handed to the driver).
    /// Read on the UI thread, which is the thread the control renders on (measured).
    /// </summary>
    public IEnumerable<string> MemoryReport()
    {
        var ready = _meshes.Values.Count(m => m is not null);
        var sections = _meshes.Values.Where(m => m is not null).SelectMany(m => m!.Sections).ToList();
        var used = new HashSet<uint>();
        foreach (var s in sections) s.AddTextureHandles(used);
        var fromSkins = new HashSet<uint>();
        foreach (var skin in _skins.Values) skin?.AddTextureHandles(fromSkins);
        var all = new HashSet<uint>(used); all.UnionWith(fromSkins);

        yield return $"VIEWPORT scene {_scene.Count:N0} placements, {_meshes.Count:N0} meshes known, {ready:N0} on the GPU, " +
                     $"{sections.Count:N0} sections, {_skins.Count:N0} override skins, {_failed.Count:N0} failed";
        var (meshes, vb, ib, made, disposed) = RenderMesh.LiveBuffers;
        yield return $"VIEWPORT mesh buffers alive: {meshes:N0} (made {made:N0}, disposed {disposed:N0}), vertex {vb / 1048576.0:N0} MB + index {ib / 1048576.0:N0} MB = {(vb + ib) / 1048576.0:N0} MB";
        if (_textures is null) { yield return "VIEWPORT no texture cache"; yield break; }
        var held = _textures.GlTextureBytes;
        var live = _textures.BytesOf(all);
        yield return $"VIEWPORT textures held by the cache: {_textures.GlTextureCount:N0} = {held / 1048576.0:N0} MB (estimate); " +
                     $"{used.Count:N0} used by sections + {fromSkins.Except(used).Count():N0} more only by skins = {all.Count:N0} still pointed at = {live / 1048576.0:N0} MB; " +
                     $"NOT pointed at by anything: {_textures.GlTextureCount - all.Count:N0} = {(held - live) / 1048576.0:N0} MB";
        yield return $"VIEWPORT textures by decoded size: {_textures.SizeClasses()}";
        foreach (var line in _textures.FormatReport()) yield return "VIEWPORT   format " + line;
        foreach (var line in _textures.BlockReport()) yield return "VIEWPORT   blocks: " + line;
        var cap2 = _textures.EstimateAtCap(2048); var cap1 = _textures.EstimateAtCap(1024); var cap4 = _textures.EstimateAtCap(4096);
        yield return $"VIEWPORT the same {cap4.Count:N0} textures at a cap of 4096 (today) {cap4.Bytes / 1048576.0:N0} MB, 2048 {cap2.Bytes / 1048576.0:N0} MB, 1024 {cap1.Bytes / 1048576.0:N0} MB (estimates, as if every one were decoded to RGBA8)";
        var (tl, tb, up, del) = TextureCache.LiveTextures;
        yield return $"VIEWPORT textures process-wide: alive {tl:N0} = {tb / 1048576.0:N0} MB; ever uploaded {up:N0}, deleted {del:N0}; " +
                     $"released by the sweep {_textures.Released:N0} ({_textures.ReleasedBytes / 1048576.0:N0} MB), uploaded again afterwards {_textures.ReUploaded:N0}; " +
                     $"{_textures.EmptyEntries:N0} cache entries hold no texture (would not decode); BoundLog {_textures.BoundLog.Count:N0} lines";
    }

    /// <summary>
    /// DEV: ASK THE DRIVER, NOT OUR OWN BOOKKEEPING. Every non-zero texture handle of every section of every mesh still
    /// here, and of every override skin, is put to <c>glIsTexture</c>; a name the driver does not know is a texture that
    /// was deleted while something still points at it. The count of those must be 0. (The report's "not pointed at by
    /// anything" line is computed from the same holders the sweep reads, so a holder missing from both would not show in it.)
    /// Printed as <c>SWEEPCHECK ... dead N</c>; also callable from the memory report. GL thread only.
    /// </summary>
    private void SweepCheck(string when)
    {
        if (_textures is null) return;
        var handles = new HashSet<uint>();
        var sections = 0;
        foreach (var m in _meshes.Values) if (m is not null) foreach (var s in m.Sections) { s.AddTextureHandles(handles); sections++; }
        var sectionHandles = handles.Count;
        foreach (var skin in _skins.Values) skin?.AddTextureHandles(handles);
        var dead = _textures.CountUnknownToGl(handles);
        Console.WriteLine($"SWEEPCHECK {when}: {handles.Count:N0} texture handle(s) ({sectionHandles:N0} from {sections:N0} sections of {_meshes.Count(kv => kv.Value is not null):N0} meshes, " +
                          $"{handles.Count - sectionHandles:N0} more from {_skins.Count:N0} skins) asked of the driver; dead {dead:N0}");
    }

    /// <summary>DEV: <c>--keeptextures</c> turns the release on a map switch OFF, so one binary measures both ways.</summary>
    public static bool KeepTextures;

    /// <summary>
    /// DEV: <c>--texcap N</c> uploads every viewport texture no larger than N pixels on its longest side. NOT a shipped
    /// setting (the author decides texture quality): it is how the report's "what a cap would save" is photographed, and
    /// how a whole level is loaded on a PC that cannot hold the uncapped one. 0 = the cache's own limit (4096).
    /// </summary>
    public static int TextureCap;
#else
    private const bool KeepTextures = false;
#endif

    private bool[] _blockout = Array.Empty<bool>();

    /// <summary>
    /// Flag the greybox. Done once per scene rather than per frame: the answer is a property of
    /// the placement, not of where the camera is, unlike the backdrop.
    /// </summary>
    private void MarkBlockout()
    {
        if (_blockout.Length != _scene.Count) _blockout = new bool[_scene.Count];
        var n = 0;
        for (var i = 0; i < _scene.Count; i++)
        {
            _blockout[i] = LevelReader.IsBlockout(_scene[i].MeshPath, _scene[i].Actor, _scene[i].Sublevel);
            if (_blockout[i]) n++;
        }
        if (_editor is not null) _editor.BlockoutCount = n;
    }

    private void LoadSome()
    {
        if (_gl is null || _editor?.Provider is null || _toLoad.Count == 0) return;

        // Hands off while somebody is flying or dragging. Loading resumes a quarter of a second
        // after they stop, which is far too short to notice and long enough to keep every frame
        // of a camera move fast.
        if (Environment.TickCount64 - Volatile.Read(ref _cameraTouchedAt) < QuietAfterCameraMs) return;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            // NEAREST FIRST, the way dressing already worked.
            //
            // Loading in scene order means the room you are looking at finishes whenever its
            // meshes happen to come up, and until then the view keeps rearranging as distant
            // geometry arrives in front of it. On the hub that is eleven minutes of churn, because
            // a Nanite mesh has no cheap LOD to load instead -- the war table is 1.4 million
            // vertices and 2.3 seconds on its own.
            //
            // This does not make the level load faster. It makes the part you are looking at load
            // FIRST and then stop changing, which is the actual complaint.
            var path = NearestPending();
            if (path is null) { _toLoad.Clear(); break; }
            _toLoad.Remove(path);
            var one = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // EITHER KIND OF MESH. Asking only for a UStaticMesh made every skinned prop in
                // a level load as null and report "loaded as null" in the failure list, which
                // reads like a broken asset rather than a question never asked. The static case
                // is tried first because it is the overwhelming majority and costs one load.
                var asset = AssetLoader.Load<UStaticMesh>(_editor.Provider, path);
                var read = one.Elapsed.TotalMilliseconds;
                if (asset is not null)
                    _meshes[path] = RenderMesh.FromStatic(_gl, asset);
                else
                {
                    var skinned = AssetLoader.Load<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>(
                        _editor.Provider, path);
                    read = one.Elapsed.TotalMilliseconds;
                    _meshes[path] = skinned is null ? null : RenderMesh.FromSkeletal(_gl, skinned);
                }
                LoadCount++;
                LoadMs += one.Elapsed.TotalMilliseconds;
                if (one.Elapsed.TotalMilliseconds > SlowestMs)
                {
                    SlowestMs = one.Elapsed.TotalMilliseconds;
                    Slowest = $"{path[(path.LastIndexOf('/') + 1)..]} {one.Elapsed.TotalMilliseconds:N0}ms " +
                              $"(read {read:N0}ms, upload {one.Elapsed.TotalMilliseconds - read:N0}ms)  " +
                              RenderMesh.LastConvertReport;
                }
                if (_meshes[path] is null) _failed[path] = "loaded as null";
                else if (_editor.Shading != "Solid") _toDress.Add(path);
            }
            catch (Exception e) { _meshes[path] = null; _failed[path] = e.GetType().Name + ": " + e.Message; }
        }
        while (_toLoad.Count > 0 && clock.Elapsed.TotalMilliseconds < LoadBudgetMs);
    }

    /// <summary>
    /// The unloaded mesh nearest the camera, or null when the queue holds nothing the scene
    /// still places -- which happens after a swap or a sublevel change.
    /// </summary>
    private string? NearestPending()
    {
        if (_toLoad.Count == 0) return null;
        var eye = Camera.Position;
        string? best = null;
        var bestD = float.MaxValue;

        for (var i = 0; i < _scene.Count; i++)
        {
            var p = _scene[i];
            if (p.Shown is not { } shown || !_toLoad.Contains(shown)) continue;
            // UE space to GL, the same swap the renderer makes, so "near" means near on screen.
            var at = new Vector3(p.At.X, p.At.Z, p.At.Y);
            var d = Vector3.DistanceSquared(at, eye);
            if (d >= bestD) continue;
            bestD = d;
            best = shown;
        }

        // A mesh nothing places any more -- take any, so the loop always makes progress rather
        // than spinning on a scan that can never match.
        return best ?? _toLoad.First();
    }

    /// <summary>
    /// Give some meshes their materials, on the same budget the geometry loads on.
    ///
    /// `BindMaterial` is where the cost is: it resolves the material's parameters, then decodes
    /// and uploads up to three textures. Doing it for a whole level in one go would stall the
    /// window for as long as the decode takes, which on a level of 1,400 meshes is not a pause.
    /// </summary>
    /// <summary>
    /// LET GO OF THE MATERIAL. The section keeps its GL handles, which is all drawing needs.
    ///
    /// This is the whole of the 20 GB. A dressed section held a UMaterialInstanceConstant, which
    /// roots the IoPackage that declared its texture parameters, which holds an unevaluated
    /// `Lazy&lt;IoPackage[]&gt;` whose factory closure still captures the FAssetArchive -- and that
    /// owns the ENTIRE PACKAGE FILE as a byte[]. gcroot on two independent 9.6 MB arrays gave the
    /// same chain both times, ending at this dictionary.
    ///
    /// LGT_Dropship -- 324 meshes, a small map -- carried 5.82 GB of byte[] on a 6.4 GB managed
    /// heap, 5.79 GB of it on the Large Object Heap with 15 MB of fragmentation. Live, not
    /// garbage. A level editor has thousands of meshes where the model viewer has one, which is
    /// why this is done HERE and the shared binder is untouched: the toolkit swaps materials in
    /// and binds them immediately, and releasing under it would undo the swap.
    /// </summary>
    private static void Release(RenderSection section)
    {
        if (section.MaterialPath.Length > 0) section.Material = null;
    }

    /// <summary>
    /// Fetch the material back, for a section that has already been dressed once.
    ///
    /// Only Redress asks for this -- the author changing Solid/Material/Rendered -- so it is paid
    /// on a deliberate click rather than on every frame, and it costs one package load against the
    /// several megabytes that package would otherwise sit on for the rest of the session.
    /// </summary>
    private void Rematerialise(RenderSection section)
    {
        if (section.Material is not null || section.MaterialPath.Length == 0) return;
        if (_editor?.Provider is not { } provider) return;

        // COUNTED, because a failure here is INVISIBLE. BindMaterial returns immediately on a null
        // material, which leaves the texture handles from the first dress still bound -- so the
        // picture looks perfect whether the reload worked or not, and so does every count taken
        // from the drawn result. The first version of this test could not have failed.
        Refetched++;
        try
        {
            section.Material = AssetLoader.Read(
                () => provider.LoadPackageObject<UMaterialInterface>(section.MaterialPath));
            if (section.Material is null) RefetchFailed++;
        }
        catch { RefetchFailed++; }
    }

    /// <summary>Materials fetched back for a re-dress, and how many of those came back null.</summary>
    public int Refetched { get; private set; }
    public int RefetchFailed { get; private set; }

    // ===================== PER-PLACEMENT MATERIALS =====================

    /// <summary>
    /// One bound material, shared by every placement that names it. 559 distinct materials are
    /// named by overrides across the hub, against 3,204 placements naming them.
    /// </summary>
    private readonly Dictionary<string, RenderMesh.SectionSkin?> _skins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The per-slot array handed to <see cref="RenderMesh.Draw"/>, cached by the override set.
    ///
    /// Built once per distinct set rather than per placement per frame: the draw loop runs over
    /// every placement on every frame, and allocating an array in it would put 10,000 arrays a
    /// frame through the GC for a level the size of the hangar.
    /// </summary>
    private readonly Dictionary<string, RenderMesh.SectionSkin?[]> _skinSets = new(StringComparer.Ordinal);

    public int SkinsBound { get; private set; }
    public int SkinsFailed { get; private set; }

    /// <summary>The first few override materials that would not bind, and why.</summary>
    public List<string> SkinFailures { get; } = new();

    /// <summary>
    /// The textures for one material path, bound once.
    ///
    /// Reuses TextureCache.BindMaterial through a throwaway section, because that binder is the
    /// proven one -- it knows about UDIM atlases, emissive placeholders and the ORME packing, and
    /// a second implementation here would be a second set of those bugs. Null is cached too, so
    /// a material that will not load is not retried every frame.
    /// </summary>
    private RenderMesh.SectionSkin? Skin(string path)
    {
        if (_skins.TryGetValue(path, out var known)) return known;

        RenderMesh.SectionSkin? made = null;
        var why = "material did not load";
        if (_textures is not null && _editor?.Provider is { } provider)
        {
            try
            {
                var probe = new RenderSection { MaterialPath = path, Udim = new Vector4(1, 1, 0, 0) };
                // THROUGH LevelExport.LoadMaterial, because 228 of the hub's override materials
                // are subobjects inside a level package and LoadPackageObject cannot take one.
                probe.Material = AssetLoader.Read(() => LevelExport.LoadMaterial(provider, path));
                if (probe.Material is not null)
                {
                    _textures.BindMaterial(probe);
                    made = new RenderMesh.SectionSkin
                    {
                        Diffuse = probe.Diffuse,
                        NormalMap = probe.NormalMap,
                        Orme = probe.Orme,
                        Emissive = probe.Emissive,
                        EmissiveTint = probe.EmissiveTint,
                        Udim = probe.Udim,
                    };
                    // LET THE MATERIAL GO. A loaded UMaterialInstanceConstant roots the package
                    // that declared its textures, and 559 of them held open is the 20 GB fault
                    // this repo already paid for once. The GL handles are all that is needed.
                    probe.Material = null;
                }
            }
            catch (Exception ex) { why = $"{ex.GetType().Name} {ex.Message}"; }
        }

        if (made is null)
        {
            SkinsFailed++;
            // WHY, NOT JUST HOW MANY. 170 of 304 failed on the author's hub and the count alone
            // cannot tell a material that does not exist from one that throws on load from one
            // this binder simply will not dress -- and a failed skin falls back to the mesh's
            // own material, so the picture looks exactly as it did before. Silent, and more
            // common than success.
            if (SkinFailures.Count < 12)
                SkinFailures.Add($"{path.Split('/')[^1]}  {why}");
        }
        else SkinsBound++;
        _skins[path] = made;
        return made;
    }

    /// <summary>
    /// The slot-indexed skins for one placement, or null when it overrides nothing.
    ///
    /// A slot the placement leaves null keeps the mesh's own material, which is what UE does and
    /// why the nulls are preserved rather than packed out.
    /// </summary>
    private RenderMesh.SectionSkin?[]? SkinsFor(LevelReader.Placement p)
    {
        if (p.Materials is not { Length: > 0 } over) return null;

        var key = string.Join("0001", over.Select(x => x ?? ""));
        if (_skinSets.TryGetValue(key, out var set)) return set;

        set = new RenderMesh.SectionSkin?[over.Length];
        for (var i = 0; i < over.Length; i++)
            if (over[i] is { Length: > 0 } path) set[i] = Skin(path);
        _skinSets[key] = set;
        return set;
    }

    private void DressSome()
    {
        if (_textures is null || _toDress.Count == 0 || _editor is null) return;
        if (_editor.Shading == "Solid") { _toDress.Clear(); return; }
        if (Environment.TickCount64 - Volatile.Read(ref _cameraTouchedAt) < QuietAfterCameraMs) return;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var path = NearestUndressed();
            if (path is null) { _toDress.Clear(); break; }
            _toDress.Remove(path);

            if (_meshes.TryGetValue(path, out var mesh) && mesh is not null)
            {
                foreach (var section in mesh.Sections)
                {
                    // A SECTION THAT WILL NOT DRESS STAYS GREY, which is honest -- but silently
                    // grey is indistinguishable from not-yet-dressed, and the author could not
                    // tell which he was looking at: "also noticed not all textures load either".
                    // So the failures are counted and shown.
                    try
                    {
                        Rematerialise(section);
                        _textures.BindMaterial(section);
                    }
                    catch (Exception e)
                    {
                        DressFailed++;
                        if (LastDressFail.Length == 0)
                            LastDressFail = $"{Short(path)}: {e.GetType().Name} {e.Message}";
                    }
                    finally { Release(section); }

                    // DRESSED BUT STILL GREY. A section whose material bound without throwing and
                    // yet has no base-colour texture renders exactly like one we simply have not
                    // reached, and exactly like one whose look the game gets from a shader we do
                    // not run. Counting them separates the first case from the other two.
                    if (section.Diffuse == 0)
                    {
                        NoDiffuse++;
                        // NAME THE MATERIAL, not just the mesh. "This mesh is grey" does not say
                        // whether the material was missing, resolved to nothing, or names a
                        // texture by a parameter this binder does not look for.
                        var note = $"{Short(path)}  slot '{section.MaterialName}'  " +
                                   $"material {(section.MaterialPath.Length == 0 ? "NOT RESOLVED" : Short(section.MaterialPath))}" +
                                   (section.DiffusePath.Length > 0 ? $"  wanted {section.DiffusePath}" : "  no diffuse path");
                        if (BareMeshes.Count < 14 && !BareMeshes.Contains(note)) BareMeshes.Add(note);
                    }
                }
                _dressed++;
            }
        }
        while (_toDress.Count > 0 && clock.Elapsed.TotalMilliseconds < LoadBudgetMs);
        DressMs += clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// WHAT YOU ARE LOOKING AT, FIRST.
    ///
    /// Dressing a whole level is 131 seconds of work on SK_Anaxes_020 and measurement says almost
    /// none of it is avoidable: 2,789 sections resolve to 2,767 DISTINCT materials, so there is
    /// essentially nothing to share and a cache would save about one per cent. What CAN be fixed
    /// is the order. Textures arriving nearest-first means the building in front of you is dressed
    /// in a second or two and the far side of the map finishes whenever it finishes.
    ///
    /// The scan is over every placement rather than every mesh on purpose: a foliage mesh is used
    /// eight hundred times all over the map, so its "distance" is only meaningful per instance.
    /// Eighteen thousand distance compares is a fraction of a millisecond, against a budget of ten.
    /// </summary>
    private string? NearestUndressed()
    {
        var eye = Camera.Position;
        string? best = null;
        var bestD = float.MaxValue;

        for (var i = 0; i < _scene.Count; i++)
        {
            var p = _scene[i];
            if (p.Shown is not { } shown || !_toDress.Contains(shown)) continue;
            // UE space to GL, the same swap the renderer makes, so "near" means near on screen.
            var at = new Vector3(p.At.X, p.At.Z, p.At.Y);
            var d = Vector3.DistanceSquared(at, eye);
            if (d >= bestD) continue;
            bestD = d;
            best = shown;
        }

        // A mesh nothing places any more -- the scene changed under it. Take any, so the loop
        // always makes progress rather than spinning on an empty scan.
        return best ?? _toDress.FirstOrDefault();
    }

    /// <summary>Re-dress everything, because the shading mode changed.</summary>
    public void Redress()
    {
        _toDress.Clear();
        _dressed = 0;
        DressMs = 0;
        if (_editor is null || _editor.Shading == "Solid")
        {
            RenderMesh.SurfaceMaps = true;
            RequestNextFrameRendering();
            return;
        }
        RenderMesh.SurfaceMaps = _editor.Shading == "Rendered";
        foreach (var (path, m) in _meshes) if (m is not null) _toDress.Add(path);
        RequestNextFrameRendering();
    }

    /// <summary>
    /// PUT THE LEVEL ON SCREEN, WHERE "THE LEVEL" MEANS THE DENSE PART OF IT.
    ///
    /// Percentiles were not enough, and the hub is why. Its single BIGGEST sublevel is the vista:
    /// 1,780 of 6,779 placements are a Ring of Kafrene backdrop of cargo docks and asteroids
    /// centred 32,000 units out, with more rooms -- the customisation diorama, the mug-shot set,
    /// hologram rigs -- parked half a million units away where the player never walks. Far
    /// geometry is not an outlier to be clipped here, it is the PLURALITY, so framing the middle
    /// 90% still put the camera 327,526 units back and the hub itself was a speck.
    ///
    /// The median and the MEDIAN ABSOLUTE DEVIATION do not care how far the other cluster is or
    /// how many are in it, only that most placements are somewhere else. Both land inside the
    /// biggest cluster, which on every map here is the part somebody walks around in. And where
    /// the old rule was already right -- a skirmish map that really is one even block -- +/-2 MAD
    /// is close to its true extent, so nothing is lost.
    /// </summary>
    private void FrameAll()
    {
        if (_framed || _scene.Count == 0) return;
        _framed = true;

        // UE space to GL space, the same axis swap the renderer makes, so the camera frames what
        // is actually drawn rather than where the numbers say it is.
        Camera.Frame(new Vector3(_core.X - _coreReach.X, _core.Z - _coreReach.Z, _core.Y - _coreReach.Y),
                     new Vector3(_core.X + _coreReach.X, _core.Z + _coreReach.Z, _core.Y + _coreReach.Y));

        // AND DO NOT STAND FURTHER OUT THAN THE LEVEL ITSELF. Fitting the whole box on screen is
        // the right answer for a map you look down at and the wrong one for an INTERIOR: the hub
        // is a station, so a camera far enough back to see all of it is outside the hull, and
        // every pixel is the inside face of the vista shell. Measured: a flat grey screen at
        // 27,784 units, while the same level at 2,000 shows the Pit in full detail.
        Camera.Distance = MathF.Min(Camera.Distance, MathF.Max(_coreRadius, 500f));

        Console.WriteLine($"FRAME {_scene.Count:N0} placements -> centre {_core.X:N0},{_core.Y:N0},{_core.Z:N0} " +
                          $"radius {_coreRadius:N0} dist {Camera.Distance:N0}");
    }

    // ---------------------------------------------------------------- drawing

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || _shader is null || _fault is not null) return;
        RenderFrames++;

        // HOW MANY BITS OF DEPTH DID WE ACTUALLY GET?
        //
        // Asked once, on the real framebuffer, because this is the one number that decides whether
        // two surfaces a few centimetres apart can be told apart at all -- and nothing has ever
        // looked at it. Avalonia hands us ITS framebuffer; what depth attachment that carries is
        // its choice, not ours. At near 143 and far 578,380 a 24-bit buffer resolves about 4mm at
        // 300m and a 16-bit one about a metre, which is the difference between a solid level and
        // one where every surface fights its neighbour as the camera moves.
        if (!_depthAsked)
        {
            _depthAsked = true;
            try
            {
                // WHICH ATTACHMENT TO ASK ABOUT DEPENDS ON WHICH FRAMEBUFFER THIS IS.
                //
                // GL_DEPTH (0x1801) names the DEFAULT framebuffer's depth; a framebuffer OBJECT
                // uses GL_DEPTH_ATTACHMENT (0x8D00) or GL_DEPTH_STENCIL_ATTACHMENT (0x821A).
                // Avalonia hands us fb 1, so asking about 0x1801 returned 0 bits -- which is the
                // query being wrong, not the buffer being absent, and reads identically. So each
                // is asked, and OBJECT_TYPE (0x8CD0) says whether anything is attached at all
                // before any size is believed.
                var names = new (string What, uint At)[]
                            { ("GL_DEPTH", 0x1801), ("DEPTH_ATTACHMENT", 0x8D00),
                              ("DEPTH_STENCIL_ATTACHMENT", 0x821A) };
                foreach (var (what, at) in names)
                {
                    _gl.GetFramebufferAttachmentParameter(
                        FramebufferTarget.Framebuffer, (FramebufferAttachment) at,
                        (FramebufferAttachmentParameterName) 0x8CD0, out var kind);
                    if (kind == 0) { Console.WriteLine($"GL depth: {what,-26} nothing attached"); continue; }

                    _gl.GetFramebufferAttachmentParameter(
                        FramebufferTarget.Framebuffer, (FramebufferAttachment) at,
                        (FramebufferAttachmentParameterName) 0x8214, out var bits);
                    _gl.GetFramebufferAttachmentParameter(
                        FramebufferTarget.Framebuffer, (FramebufferAttachment) at,
                        (FramebufferAttachmentParameterName) 0x8215, out var stencil);
                    // ASK THE RENDERBUFFER ITSELF. The attachment query answers 0 on this driver
                    // even though it has just said GL_RENDERBUFFER is attached, and a 0 from a
                    // query that does not apply is indistinguishable from a buffer with no bits.
                    // The internal format is the fact worth having: 0x81A5 is DEPTH_COMPONENT16,
                    // 0x81A6 is 24, 0x88F0 is DEPTH24_STENCIL8, 0x8DAB is 32F.
                    var format = 0;
                    if (kind == 0x8D41)
                    {
                        _gl.GetFramebufferAttachmentParameter(
                            FramebufferTarget.Framebuffer, (FramebufferAttachment) at,
                            (FramebufferAttachmentParameterName) 0x8CD1, out var name);
                        if (name != 0)
                        {
                            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, (uint) name);
                            _gl.GetRenderbufferParameter(RenderbufferTarget.Renderbuffer,
                                                         (RenderbufferParameterName) 0x8D44, out format);
                            _gl.GetRenderbufferParameter(RenderbufferTarget.Renderbuffer,
                                                         (RenderbufferParameterName) 0x8D54, out var rbBits);
                            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
                            if (rbBits > bits) bits = rbBits;
                        }
                    }

                    if (bits > DepthBits) DepthBits = bits;
                    Console.WriteLine($"GL depth: {what,-26} kind 0x{kind:X} " +
                                      $"depth {bits} bit(s) stencil {stencil} " +
                                      $"format 0x{format:X}");
                }
                Console.WriteLine($"GL depth buffer: {DepthBits} bit(s) on framebuffer {fb}");
            }
            catch (Exception ex) { Console.WriteLine("GL depth buffer: could not ask -- " + ex.Message); }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Rebuild();
        QueueSwapped();

        // The far plane has to clear the whole level from wherever the eye is, or zooming in
        // clips the map away. Cheap: two vectors, once a frame.
        var coreGl = new Vector3(_core.X, _core.Z, _core.Y);
        Camera.FarFloor = (Camera.Position - coreGl).Length() + _allRadius * 1.1f + 1000f;
        // And let the camera pull back far enough to see what the far plane can draw.
        Camera.MaxDistance = MathF.Max(_allRadius * 2.5f, 100_000f);

        LoadSome();
        Trace();
        DressSome();
        SweepWhenLoaded();
        FrameAll();
        Fly();

        // PIXELS, NOT DIPS. Bounds are logical units and the framebuffer is physical; on a
        // scaled display the two differ, and a pick would read the wrong pixel.
        var scale = (float) ((VisualRoot as Avalonia.Controls.TopLevel)?.RenderScaling ?? 1.0);
        var w = (float) Bounds.Width * scale;
        var h = (float) Bounds.Height * scale;
        _aspect = h <= 0 ? 1f : w / h;

        if (_pickAt is { } want) DoPick(want, w, h);

        BuildShadows(fb);

        _gl.Viewport(0, 0, (uint) w, (uint) h);
        var bg = Background();
        _gl.ClearColor(bg.X, bg.Y, bg.Z, 1f);
        _gl.Clear((uint) (ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.DepthTest);

        _shader.Use();
        _shader.Set("uView", Camera.View);
        _shader.Set("uProj", Camera.Projection(h <= 0 ? 1f : w / h));
        _shader.Set("uCamPos", Camera.Position);
        _shader.Set("uMode", 0);
        _shader.Set("uSkinned", 0);
        _shader.Set("uSelected", 0);
        SetRig();
        SetShadowUniforms();

        // COUNT, DO NOT SEARCH. This asked `_scene.IndexOf(p)` for every placement, and a
        // Placement is a RECORD -- so IndexOf compared every field of every earlier placement,
        // strings and a 4x4 matrix included, to find an index the loop already knew. Quadratic
        // in the size of the level, on every frame, for a selection highlight.
        var drawn = 0;
        _hiddenThisFrame = 0;
        for (var i = 0; i < _scene.Count; i++)
        {
            if (Hidden(i)) { _hiddenThisFrame++; continue; }
            var p = _scene[i];
            if (p.Shown is not { } shown) continue;          // swapped to nothing -- do not draw it
            if (!_meshes.TryGetValue(shown, out var mesh) || mesh is null) continue;
            _shader.Set("uModel", Placed(p) * RenderMesh.UeToGl);
            _shader.Set("uSelected", i == Selected ? 1 : 0);
            // THIS PLACEMENT'S OWN MATERIALS where it has them. Null for the great majority, and
            // then Draw behaves exactly as it did.
            mesh.Draw(_shader, default, _editor?.Shading == "Solid" ? null : SkinsFor(p));
            drawn++;
        }

        DrawGizmo(Camera.View * Camera.Projection(_aspect), picking: false);

        // FINISH THE FRAME BEFORE HANDING IT OVER.
        //
        // This is the flicker. We draw into the framebuffer Avalonia gives us and return, and
        // Avalonia composites it -- but nothing here ever waited for the GPU to be DONE. A frame
        // taken mid-flight is missing whatever had not finished yet, which on a level of nine
        // thousand draws is a different handful of meshes every time. Hence "meshes keep
        // rendering and de-rendering", hence it happening at any angle and any movement, and
        // hence `drawn 8,848 of 8,849` never moving: every one of them WAS submitted.
        //
        // HOW IT WAS FOUND, and it is the only reason it was: the flicker measurement made the
        // flicker stop. Compare() reads the framebuffer back, glReadPixels waits for the pipeline,
        // and that accidental wait was doing the job this line now does on purpose. His words:
        // "wait, turning that on causes the flicker to stop".
        //
        // WHICH ALSO MEANS THE MEASUREMENT CANNOT SEE THIS BUG -- it suppresses it. Any future
        // reading taken with the watch on will say 0% whether or not it is fixed. That is a check
        // that cannot fail, and it is written here because the next person to reach for that
        // number will be me.
        //
        // Finish, not Flush: Flush only promises the commands are issued, and the compositor may
        // be another context entirely. The cost is a stall the size of one frame's GPU work, which
        // is what every other frame was already paying in stutter.
        _gl.Finish();

        if (_shotTo is not null) Grab((int) w, (int) h);
        if (Watching) Compare((int) w, (int) h);

        // WHAT THE LAST FRAME ACTUALLY PUT ON SCREEN, kept so a harness can watch it over time.
        // "Everything disappears and reappears" is a claim about this number moving, and nothing
        // in this class was reporting it per frame -- the line below fires ONCE, ever.
        LastDrawn = drawn;
        LastHidden = _hiddenThisFrame;

        if (_toLoad.Count == 0 && drawn > 0 && !_saidDrawn)
        {
            _saidDrawn = true;
            Console.WriteLine($"drew {drawn:N0} placement(s) of {_scene.Count:N0}");
        }

        // Keep asking for frames while meshes are still arriving or a fly key is down; otherwise
        // the control only redraws when something invalidates it, and a level would stop filling
        // in half-loaded -- or the camera would take one step and stop.
        RenderMs = clock.Elapsed.TotalMilliseconds;

        if (_toLoad.Count > 0 || _toDress.Count > 0 || Volatile.Read(ref _flyMask) != 0)
            Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
    }

    private bool _saidDrawn;

    // ---------------------------------------------------------------- shadows

    private const int Cascades = 3;
    private const int ShadowSize = 2048;

    private uint _shadowFbo, _shadowTex;
    private readonly Matrix4x4[] _shadowMat = new Matrix4x4[Cascades];
    private readonly float[] _shadowFar = new float[Cascades];
    private readonly float[] _shadowTexel = new float[Cascades];
    private Vector3 _shadowBuiltAt = new(float.NaN, 0, 0);
    private Vector3 _shadowBuiltSun;
    private int _shadowBuiltMeshes = -1;
    private int _shadowBuiltCull = -1;
    private bool _shadowReady;

    /// <summary>Shadows off is a real setting: they cost a geometry pass per cascade.</summary>
    public bool Shadows { get; set; } = true;

    /// <summary>0 normal, 1 factor, 2 cascade, 3 uv in the map, 4 reference depth, 5 map content.</summary>
    public int ShadowDebug { get; set; }

    /// <summary>Where to write a picture of each cascade's depth, when debugging.</summary>
    public string? CascadeDir { get; set; }

    /// <summary>
    /// Which faces fill the shadow map: 0 both, 1 back only (cull front), 2 front only.
    ///
    /// BOTH, and front-face culling was actively wrong here. The usual advice -- cull front faces
    /// so the depth recorded is the caster's BACK and the acne hides inside it -- assumes closed
    /// solids that float. A crate RESTING ON THE GROUND has its back face at ground level, so the
    /// map says the nearest occluder along that ray is the ground itself and the ground is not
    /// shadowed. Umbara showed exactly that: every crate on the landing pad reduced to a hairline
    /// outline while the trees, which are dense and deep, shadowed correctly.
    ///
    /// It also assumes closed geometry at all, and this level is built from single-sided shells.
    /// Both faces, with the normal-offset bias doing the anti-acne work -- measured on the
    /// dropship as no acne and a slightly darker frame, 0.201 -> 0.196.
    /// </summary>
    public int ShadowCull { get; set; }

    /// <summary>
    /// Whether to cull casters against the cascade. Off is slow and is the CONTROL: it decides
    /// whether an empty-looking map is a projection fault or simply the cull throwing away the
    /// geometry that should have filled it -- and an actor's pivot is famously not where its
    /// geometry sits, which is exactly what this test is aimed at.
    /// </summary>
    public bool CullCasters { get; set; } = true;

    /// <summary>How many placements the cascades drew last time they were built.</summary>
    public int ShadowDrawn { get; private set; }
    public double ShadowMs { get; private set; }

    /// <summary>
    /// A DEPTH-ONLY TEXTURE ARRAY, one layer per cascade.
    ///
    /// No colour attachment at all -- glDrawBuffer(NONE) -- so the fragment shader's output goes
    /// nowhere and the pass costs only rasterisation. COMPARE_REF_TO_TEXTURE is what makes
    /// sampler2DArrayShadow do the depth test inside the sampler, which is how a 3x3 PCF becomes
    /// nine filtered taps instead of nine compares in the shader.
    /// </summary>
    private unsafe void BuildShadowTargets()
    {
        if (_gl is null || _shadowTex != 0) return;

        _shadowTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2DArray, _shadowTex);
        _gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.DepthComponent24,
                       ShadowSize, ShadowSize, Cascades, 0,
                       PixelFormat.DepthComponent, PixelType.Float, null);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter,
                         (int) TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter,
                         (int) TextureMagFilter.Linear);
        // CLAMP TO A WHITE BORDER, so anything sampling outside a cascade comes back LIT. The
        // other way round puts a black wall around the edge of every cascade.
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
        _gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                                    _shadowTex, 0, 0);
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
        var ok = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (ok != GLEnum.FramebufferComplete) Console.WriteLine($"SHADOW framebuffer incomplete: {ok}");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Fit the cascades to the view frustum and render the level's depth into each.
    ///
    /// REBUILT ONLY WHEN SOMETHING MOVED. Three extra geometry passes over a level of 26,000
    /// placements is the most expensive thing this viewport does, and the render loop asks for
    /// frames continuously while meshes are still streaming in. A still camera over a finished
    /// level pays for the shadow pass once.
    ///
    /// SPHERES, NOT BOXES. Each cascade is fitted to the bounding SPHERE of its slice of the
    /// frustum, which is rotation-invariant: a box fitted to the corners changes size as the
    /// camera turns and the shadow edges crawl. Snapping the centre to whole texels removes what
    /// is left of that.
    /// </summary>
    private void BuildShadows(int fb)
    {
        if (_gl is null || _pick is null) return;
        var rig = _editor?.Lights;
        var key = rig?.Key;
        if (!Shadows || key is null || _editor?.Shading != "Rendered")
        { _shadowReady = false; return; }

        var sun = Vector3.TransformNormal(key.Direction, RenderMesh.UeToGl);
        if (sun.LengthSquared() < 1e-8f) { _shadowReady = false; return; }
        sun = Vector3.Normalize(sun);

        // REBUILD WHEN MESHES HAVE ACTUALLY ARRIVED, not when the scene has decided it wants them.
        //
        // This asked `_meshes.Count`, which is what the level WANTS -- Rebuild puts every wanted
        // path in the dictionary as a null the moment the scene changes, so that number reaches
        // its final value in the first second and then never moves again. With the camera also
        // still, the early-out below held for the rest of the run and the cascades kept whatever
        // had been rasterised into them when twenty-three meshes existed. The pass reported
        // casters, the map had content, the compare worked -- and none of it was the geometry on
        // screen. With the caster cull disabled entirely as a control, the count went 46 -> 69
        // against 572 actually drawn, which is what finally gave it away.
        //
        // This is the same mistake as the flashing diagnostic that printed `_meshes.Count` and
        // stayed correct all the way through the fault: a counter that reports the INTENTION
        // cannot witness the work.
        var eye = Camera.Position;
        var ready = MeshesReady;
        if (_shadowReady && _shadowBuiltCull == ShadowCull && _shadowBuiltMeshes == ready &&
            (eye - _shadowBuiltAt).LengthSquared() < 1f &&
            (sun - _shadowBuiltSun).LengthSquared() < 1e-6f) return;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _shadowBuiltAt = eye;
        _shadowBuiltSun = sun;
        _shadowBuiltMeshes = ready;
        _shadowBuiltCull = ShadowCull;

        // HOW FAR SHADOWS REACH. Tied to how far out the camera is rather than to the level, so
        // an author inspecting one crate gets shadows accurate to centimetres and one looking at
        // the whole of Umbara gets shadows over the whole of Umbara -- coarse, but present.
        // Past this the shader returns "lit", which is the right way to fail: no shadow beats a
        // wrong one.
        var reach = Math.Clamp(Camera.Distance * 3f, 2000f, 80000f);
        var near = Camera.NearPlane;

        BuildShadowTargets();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.Viewport(0, 0, ShadowSize, ShadowSize);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);

        // WHICH FACES FILL THE MAP, switchable, because the usual answer assumes closed solids.
        if (ShadowCull == 0) _gl.Disable(EnableCap.CullFace);
        else
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(ShadowCull == 1 ? TriangleFace.Front : TriangleFace.Back);
        }

        _pick.Use();
        var drawn = 0;

        var view = Camera.View;
        Matrix4x4.Invert(view, out var toWorld);

        for (var c = 0; c < Cascades; c++)
        {
            // Practical split: a geometric progression, which puts most of the resolution close
            // in where perspective magnifies every texel.
            var n = c == 0 ? near : reach * MathF.Pow(0.06f, (Cascades - c) / (float) Cascades);
            var f = reach * MathF.Pow(0.06f, (Cascades - 1 - c) / (float) Cascades);
            _shadowFar[c] = f;

            var (centre, radius) = SliceSphere(toWorld, n, f);
            var texel = 2f * radius / ShadowSize;
            _shadowTexel[c] = texel;

            var up = MathF.Abs(sun.Y) > 0.98f ? new Vector3(0, 0, 1) : new Vector3(0, 1, 0);

            // PULLED WELL BACK, because a caster does not have to be inside the slice. Umbara's
            // canopy and the hub's ceilings sit thousands of units above the ground the camera is
            // looking at; parking the light just outside the sphere puts them in front of the near
            // plane, and the thing casting the shadow is clipped away while its shadow is not.
            const float Pull = 20000f;
            var lightView = Matrix4x4.CreateLookAt(centre - sun * (radius + Pull), centre, up);

            // Snap the centre to whole texels along the light's own axes, or the map slides under
            // the scene by a fraction of a texel each frame and every edge shimmers.
            var snapped = Vector3.Transform(centre, lightView);
            snapped.X = MathF.Floor(snapped.X / texel) * texel;
            snapped.Y = MathF.Floor(snapped.Y / texel) * texel;
            Matrix4x4.Invert(lightView, out var back);
            centre = Vector3.Transform(snapped, back);
            lightView = Matrix4x4.CreateLookAt(centre - sun * (radius + Pull), centre, up);

            var proj = Matrix4x4.CreateOrthographic(2f * radius, 2f * radius, 1f, 2f * radius + 2f * Pull);
            _shadowMat[c] = lightView * proj;

            _gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer,
                                        FramebufferAttachment.DepthAttachment, _shadowTex, 0, c);
            _gl.Clear((uint) ClearBufferMask.DepthBufferBit);

            _pick.Set("uView", lightView);
            _pick.Set("uProj", proj);
            _pick.Set("uSkinned", 0);
            _pick.Set("uPickColor", Vector3.One);

            for (var i = 0; i < _scene.Count; i++)
            {
                if (Hidden(i)) continue;
                var p = _scene[i];
                if (p.Shown is not { } shown) continue;
                if (!_meshes.TryGetValue(shown, out var mesh) || mesh is null) continue;

                // CULLED IN THE LIGHT'S OWN SPACE, sideways only.
                //
                // The cascade is a box: r by r across, and as deep as the ortho range. Testing
                // distance to the CENTRE instead would have to be widened by the pull-back to
                // avoid dropping a caster high above the slice -- which widens it sideways too,
                // and then the near cascade redraws the whole level to fill a sphere twenty metres
                // across. Two absolute values get both right: anything outside the square casts
                // nothing into it, and anything above it still does.
                var model = Placed(p) * RenderMesh.UeToGl;
                if (CullCasters)
                {
                    // HOW FAR THE GEOMETRY REACHES FROM ITS OWN ORIGIN -- not half the diagonal of
                    // its bounding box.
                    //
                    // The cull measures from the placement's position, which is the mesh's LOCAL
                    // ORIGIN in the world. Half the box diagonal is the radius about the box's
                    // CENTRE, and those are only the same thing when the pivot sits inside the
                    // geometry. It frequently does not -- "an actor's pivot is not where its
                    // geometry looks" cost an hour on the gizmo and it cost this pass 45% of its
                    // casters: Umbara drew 42,794 with this cull and 78,351 without, and the
                    // missing ones were exactly the large pieces whose shadows should have fallen
                    // across the landing pad.
                    //
                    // Scale matters too: the bounds are the mesh's, the placement may enlarge it.
                    var reachX = MathF.Max(MathF.Abs(mesh.BoundsMin.X), MathF.Abs(mesh.BoundsMax.X));
                    var reachY = MathF.Max(MathF.Abs(mesh.BoundsMin.Y), MathF.Abs(mesh.BoundsMax.Y));
                    var reachZ = MathF.Max(MathF.Abs(mesh.BoundsMin.Z), MathF.Abs(mesh.BoundsMax.Z));
                    var scale = MathF.Max(new Vector3(model.M11, model.M12, model.M13).Length(),
                                MathF.Max(new Vector3(model.M21, model.M22, model.M23).Length(),
                                          new Vector3(model.M31, model.M32, model.M33).Length()));
                    var span = new Vector3(reachX, reachY, reachZ).Length() * MathF.Max(scale, 0.001f);

                    var here = new Vector3(p.At.X, p.At.Z, p.At.Y);
                    var ls = Vector3.Transform(here, lightView);
                    if (MathF.Abs(ls.X) > radius + span || MathF.Abs(ls.Y) > radius + span) continue;
                }

                _pick.Set("uModel", model);
                mesh.DrawGeometry(_pick);
                drawn++;
            }
        }

        if (ShadowDebug > 0) ProbeCascades();

        _gl.CullFace(TriangleFace.Back);
        _gl.Disable(EnableCap.CullFace);        // the main pass draws two-sided
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint) fb);
        ShadowDrawn = drawn;
        ShadowMs = clock.Elapsed.TotalMilliseconds;
        _shadowReady = true;
    }

    /// <summary>
    /// READ THE DEPTH BACK. Did the pass write anything, or is the map still cleared?
    ///
    /// Everything up to here has been inference about a buffer nobody has looked at. Two probes
    /// already came back "the map is empty" and one hypothesis -- that front-face culling was
    /// discarding single-sided shells -- was tested and WRONG: with culling off the map is still
    /// empty. So read the actual texels rather than reason about what should be in them.
    /// </summary>
    private unsafe void ProbeCascades()
    {
        if (_gl is null) return;
        var px = new float[ShadowSize * ShadowSize];
        for (var c = 0; c < Cascades; c++)
        {
            _gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer,
                                        FramebufferAttachment.DepthAttachment, _shadowTex, 0, c);
            fixed (float* f = px)
                _gl.ReadPixels(0, 0, ShadowSize, ShadowSize, PixelFormat.DepthComponent,
                               PixelType.Float, f);

            float lo = 1f, hi = 0f;
            var written = 0;
            foreach (var v in px)
            {
                if (v < lo) lo = v;
                if (v > hi) hi = v;
                if (v < 0.9999f) written++;
            }
            Console.WriteLine($"CASCADE {c}: depth {lo:0.000000}..{hi:0.000000}  " +
                              $"{written:N0} of {px.Length:N0} texels written " +
                              $"({written / (double) px.Length:P2})  far {_shadowFar[c]:N0}  " +
                              $"texel {_shadowTexel[c]:0.###}");

            // PROJECT A POINT WE KNOW IS IN VIEW, on the CPU, with the SAME matrix the shader
            // uses -- and read the depth stored there. The camera's target sits in the middle of
            // whatever is on screen, so that texel must hold the geometry we are looking at. If it
            // is still 1.0 while the map has content elsewhere, the matrix that SAMPLES disagrees
            // with the matrix that FILLED, and no amount of comparing the two on paper has found
            // it. If it holds a real depth, the fault is in the shader instead.
            var probe = Camera.Target;
            var lp = Vector4.Transform(new Vector4(probe, 1f), _shadowMat[c]);
            var ndc = new Vector3(lp.X, lp.Y, lp.Z) / (MathF.Abs(lp.W) < 1e-6f ? 1f : lp.W);
            var uv = ndc * 0.5f + new Vector3(0.5f);
            var inside = uv.X is >= 0 and <= 1 && uv.Y is >= 0 and <= 1;
            var at = inside
                ? px[Math.Clamp((int) (uv.Y * (ShadowSize - 1)), 0, ShadowSize - 1) * ShadowSize +
                     Math.Clamp((int) (uv.X * (ShadowSize - 1)), 0, ShadowSize - 1)]
                : -1f;
            Console.WriteLine($"CASCADE {c}  target {probe.X:0},{probe.Y:0},{probe.Z:0} -> " +
                              $"uv {uv.X:0.0000},{uv.Y:0.0000} ref {uv.Z:0.0000}  " +
                              (inside ? $"stored {at:0.000000}  {(at < 0.9999f ? "CONTENT" : "EMPTY")}"
                                      : "OUTSIDE the map"));

            // AND LOOK AT IT. Counting texels says the map is not empty; it cannot say WHERE the
            // content sits, and "not empty" plus "we sample a cleared texel" only makes sense if
            // the two disagree about where things are. A picture of the map settles that.
            if (CascadeDir is null) continue;
            Directory.CreateDirectory(CascadeDir);
            const int Small = 512, Step = ShadowSize / Small;
            var info = new SkiaSharp.SKImageInfo(Small, Small, SkiaSharp.SKColorType.Rgba8888,
                                                 SkiaSharp.SKAlphaType.Opaque);
            using var bmp = new SkiaSharp.SKBitmap(info);
            var dst = (byte*) bmp.GetPixels();
            for (var y = 0; y < Small; y++)
            for (var x = 0; x < Small; x++)
            {
                // Rows flipped: GL's origin is bottom-left and an image reads top-down.
                var v = px[(ShadowSize - 1 - y * Step) * ShadowSize + x * Step];
                // Stretched across the range actually present, or every texel reads as white.
                var t = hi > lo ? (v - lo) / (hi - lo) : 1f;
                var b = (byte) Math.Clamp((int) (t * 255f), 0, 255);
                var o = (y * Small + x) * 4;
                dst[o] = b; dst[o + 1] = b; dst[o + 2] = b; dst[o + 3] = 255;
            }
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 95);
            using var fs = File.OpenWrite(Path.Combine(CascadeDir, $"cascade{c}.png"));
            data.SaveTo(fs);
        }
    }

    /// <summary>The bounding sphere of one slice of the view frustum, in world space.</summary>
    private (Vector3 Centre, float Radius) SliceSphere(Matrix4x4 toWorld, float near, float far)
    {
        var tan = MathF.Tan(Camera.Fov * 0.5f);
        var corners = new Vector3[8];
        var k = 0;
        foreach (var z in new[] { near, far })
        {
            var hh = tan * z;
            var hw = hh * _aspect;
            foreach (var sx in new[] { -1f, 1f })
            foreach (var sy in new[] { -1f, 1f })
                corners[k++] = Vector3.Transform(new Vector3(sx * hw, sy * hh, -z), toWorld);
        }

        var centre = Vector3.Zero;
        foreach (var c in corners) centre += c;
        centre /= 8f;

        var radius = 0f;
        foreach (var c in corners) radius = MathF.Max(radius, (c - centre).Length());
        return (centre, MathF.Max(radius, 1f));
    }

    /// <summary>Hand the cascades to the mesh shader. Unit 5 -- 0..4 are the material maps.</summary>
    private void SetShadowUniforms()
    {
        if (_shader is null || _gl is null) return;
        if (!_shadowReady) { _shader.Set("uShadowOn", 0); _shader.Set("uShadowDebug", 0); return; }

        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2DArray, _shadowTex);
        _shader.Set("uShadowMap", 5);
        _shader.Set("uShadowOn", 1);
        _shader.Set("uShadowDebug", ShadowDebug);
        for (var c = 0; c < Cascades; c++) _shader.Set($"uShadowMat[{c}]", _shadowMat[c]);
        _shader.Set("uShadowSplit", new Vector3(_shadowFar[0], _shadowFar[1], _shadowFar[2]));
        _shader.Set("uShadowTexel", new Vector3(_shadowTexel[0], _shadowTexel[1], _shadowTexel[2]));
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    // ---------------------------------------------------------------- photographing the viewport

    private string? _shotTo;

    /// <summary>Set once the requested capture has been written. The harness waits on this.</summary>
    public bool ShotTaken { get; private set; }

    /// <summary>Ask for the next drawn frame to be written to a PNG.</summary>
    public void RequestShot(string path)
    {
        _shotTo = path;
        ShotTaken = false;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// THE 3D VIEWPORT, PHOTOGRAPHED. Read straight out of GL, after the frame is drawn.
    ///
    /// I had this written down as impossible. It is not, and the thing that disproves it has been
    /// in this file the whole time: picking reads the framebuffer with glReadPixels and has always
    /// worked. What cannot capture the viewport is Avalonia's RenderTargetBitmap -- an
    /// OpenGlControlBase composites its output and is not part of the visual tree's raster, which
    /// is the same reason it is not hit-testable. That is a fact about AVALONIA, and I had
    /// generalised it into a fact about the viewport, then built three instruments around the
    /// belief and asked the author to look at the screen for me every time.
    ///
    /// Rows come back bottom-up and the file wants them top-down, hence the flip. Alpha is forced
    /// opaque: the level shader never writes alpha, so the buffer carries whatever was there.
    /// </summary>
    /// <summary>
    /// ON WHILE A FLICKER MEASUREMENT IS RUNNING. See <see cref="Compare"/>.
    /// </summary>
    public bool Watching;

    private byte[]? _lastFrame;
    private int _lastW, _lastH;
    private Vector3 _lastEye;

    /// <summary>The most recent frame-to-frame change, as a percentage of the picture.</summary>
    public double LastChanged { get; private set; }

    /// <summary>The worst seen since watching began, and how far the camera moved for it.</summary>
    public double WorstChanged { get; private set; }
    public float WorstMoved { get; private set; }

    /// <summary>Raised when a frame changes far more than the camera moved can explain.</summary>
    public event Action<string>? Flicked;

    /// <summary>How much of the picture CHANGED, frame by frame, newest last.</summary>
    public readonly List<double> Changed = new();

    /// <summary>
    /// HOW MUCH OF THE PICTURE CHANGED SINCE THE FRAME BEFORE IT.
    ///
    /// "Meshes keep rendering and de-rendering as you move the camera" is a claim about the
    /// PICTURE changing more than the camera did, and nothing in this codebase could witness it.
    /// Every instrument so far counts what was SUBMITTED -- and `drawn 8,882 of 8,883` never moves,
    /// because no placement is ever frustum-culled, so that number cannot see this at all. It is
    /// a counter of intent, the same trap as the shadow cascades.
    ///
    /// So this reads the frame back and compares it against the last one. Nudge the camera by a
    /// twentieth of a degree and the honest answer is a percent or two -- edges, half a pixel of
    /// parallax. A spike to a third of the screen is geometry appearing or vanishing, which is
    /// the complaint, as a number, at last.
    /// </summary>
    private unsafe void Compare(int w, int h)
    {
        if (_gl is null || w <= 0 || h <= 0) return;

        var now = new byte[w * h * 4];
        fixed (byte* b = now)
            _gl.ReadPixels(0, 0, (uint) w, (uint) h, PixelFormat.Rgba, PixelType.UnsignedByte, b);

        if (_lastFrame is { } was && _lastW == w && _lastH == h)
        {
            // THE THRESHOLD HAS TO SUIT A FLAT GREY LEVEL, and the first one did not.
            //
            // It was 24 of 255, chosen to sit above dither and edge shimmer. But nothing here is
            // textured or lit: a wall panel at 0.65 luma vanishing to show the scaffolding behind
            // it at 0.60 is a difference of THIRTEEN, and the measurement that was supposed to
            // witness exactly that event scored it as no change at all. His screenshots are two
            // near-identical framings with the big flat panels present in one and gone in the
            // other, so the thing being missed is the thing being reported.
            //
            // 8 still clears compression and rounding in a buffer we read raw, and this is a
            // deliberate instrument rather than a passive counter -- being too sensitive costs a
            // number that needs interpreting, being too blunt costs the whole measurement.
            var moved = 0L;
            for (var i = 0; i < now.Length; i += 4)
            {
                var a = (now[i] * 299 + now[i + 1] * 587 + now[i + 2] * 114) / 1000;
                var b2 = (was[i] * 299 + was[i + 1] * 587 + was[i + 2] * 114) / 1000;
                if (Math.Abs(a - b2) > 8) moved++;
            }
            var pct = moved * 100.0 / (w * h);
            Changed.Add(pct);
            LastChanged = pct;

            // HOW FAR THE CAMERA MOVED FOR IT. The whole complaint is that the PICTURE changes
            // more than the camera does, so the two numbers only mean anything together -- and a
            // scripted sweep measured the honest rate they trade at: inside clutter, 40cm of
            // movement turns over about a tenth of the screen, and it grows smoothly from there.
            // Several times that, for a hand's width of movement, is the thing he is reporting.
            var eye = Camera.Position;
            var step = (eye - _lastEye).Length();
            if (pct > WorstChanged) { WorstChanged = pct; WorstMoved = step; }

            if (pct > 25 && step < 60)
                Flicked?.Invoke($"{pct:0.#}% of the picture changed while the camera moved " +
                                $"{step:0.#} cm — at {eye.X:N0},{eye.Z:N0},{eye.Y:N0}, " +
                                $"near {Camera.NearPlane:0.#} far {Camera.FarPlane:N0}");
            _lastEye = eye;
        }
        else _lastEye = Camera.Position;

        _lastFrame = now;
        _lastW = w; _lastH = h;
    }

    /// <summary>Forget the previous frame, so a new sweep does not diff against an old one.</summary>
    public void WatchReset()
    {
        _lastFrame = null;
        Changed.Clear();
        LastChanged = 0; WorstChanged = 0; WorstMoved = 0;
        _lastEye = Camera.Position;
    }

    private unsafe void Grab(int w, int h)
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

            // THE EXPOSURE, AS A NUMBER. "Not even remotely close" was a judgement about
            // brightness and contrast, and both are measurable: what fraction of the frame is
            // blown to white, what fraction is crushed to black, and where the middle sits.
            double sum = 0;
            int blown = 0, black = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var l = (pixels[i] * 0.2126 + pixels[i + 1] * 0.7152 + pixels[i + 2] * 0.0722) / 255.0;
                sum += l;
                if (l > 0.98) blown++;
                else if (l < 0.02) black++;
            }
            var n = pixels.Length / 4.0;
            // THE SHADOW PASS IS REPORTED WITH THE PICTURE, because "the render looks the same"
            // has two causes that are impossible to tell apart by eye: nothing in view casts, or
            // the pass never ran. The numbers separate them.
            Console.WriteLine($"GLSHOT {path}  {w}x{h}  mean luma {sum / n:0.000}  " +
                              $"blown {blown / n:P1}  black {black / n:P1}  " +
                              (Shadows
                                  ? $"shadows {(_shadowReady ? $"{ShadowDrawn:N0} casters in {ShadowMs:N0} ms" : "NOT BUILT")}"
                                  : "shadows off") +
                              $"  lights {LocalLit}/{LocalKnown}");
        }
        catch (Exception e) { Console.WriteLine("GLSHOT failed: " + e.Message); }
        ShotTaken = true;
    }

    private const string LineVertex = """
        #version 330 core
        layout (location = 0) in vec3 aPos;
        uniform mat4 uMvp;
        void main() { gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    private const string LineFragment = """
        #version 330 core
        uniform vec3 uColour;
        out vec4 FragColour;
        void main() { FragColour = vec4(uColour, 1.0); }
        """;

    /// <summary>One unit line along +X; the three arrows are this, rotated by the axis matrix.</summary>
    private unsafe void BuildAxes()
    {
        if (_gl is null) return;
        float[] line = { 0, 0, 0, 1, 0, 0 };
        _axisVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_axisVao);
        _axisVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _axisVbo);
        fixed (float* f = line)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint) (line.Length * sizeof(float)), f,
                           BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*) 0);
        _gl.EnableVertexAttribArray(0);

        // A unit circle in the XY plane, drawn as a loop and rotated onto each axis's plane. One
        // buffer, three draws, the same trick the arrows use.
        var ring = new float[RingSegments * 3];
        for (var i = 0; i < RingSegments; i++)
        {
            var a = i * MathF.Tau / RingSegments;
            ring[i * 3] = MathF.Cos(a);
            ring[i * 3 + 1] = MathF.Sin(a);
            ring[i * 3 + 2] = 0;
        }
        _ringVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_ringVao);
        _ringVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _ringVbo);
        fixed (float* f = ring)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint) (ring.Length * sizeof(float)), f,
                           BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*) 0);
        _gl.EnableVertexAttribArray(0);

        // A scale handle: the same axis line with a cross at its tip, so it cannot be mistaken
        // for the move arrow at a glance.
        float[] handle =
        {
            0, 0, 0,   1, 0, 0,
            1, -0.08f, 0,   1, 0.08f, 0,
            1, 0, -0.08f,   1, 0, 0.08f,
        };
        _handleVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_handleVao);
        _handleVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _handleVbo);
        fixed (float* f = handle)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint) (handle.Length * sizeof(float)), f,
                           BufferUsageARB.StaticDraw);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*) 0);
        _gl.EnableVertexAttribArray(0);
    }

    /// <summary>Which gizmo the armed tool wants. Select shows the move one, so a click still
    /// has something to grab without changing tool first.</summary>
    private string Armed => _editor?.Tool switch
    {
        "Rotate" => "Rotate",
        "Scale" => "Scale",
        _ => "Move",
    };

    /// <summary>Where the selected actor is, in GL space, including any unsaved move.</summary>
    /// <summary>
    /// Where a placement actually is right now: its shipped transform, then any unsaved rotation
    /// or scale about the actor's root, then any unsaved move.
    ///
    /// The order is not arbitrary. The warp is a WORLD transform about a pivot, so it composes
    /// after the model matrix; the offset is a plain world translation and has to come last or a
    /// rotation would swing it round the pivot too.
    /// </summary>
    private static Matrix4x4 Placed(LevelReader.Placement p) =>
        p.Model * p.Warp * Matrix4x4.CreateTranslation(p.Offset);

    private Vector3? GizmoAt()
    {
        if (Selected < 0 || Selected >= _scene.Count) return null;
        var m = Placed(_scene[Selected]) * RenderMesh.UeToGl;
        return new Vector3(m.M41, m.M42, m.M43);
    }

    /// <summary>
    /// The arrows, at a length that is constant ON SCREEN rather than in the world.
    ///
    /// A fixed world length is unusable in a tool that looks at both a whole skirmish map and a
    /// single crate: it is either invisible or it swallows the level. Scaling with the camera
    /// distance keeps it the same size under the cursor at every zoom.
    /// </summary>
    private void DrawGizmo(Matrix4x4 viewProj, bool picking)
    {
        if (_gl is null || GizmoAt() is not { } at) return;
        var shader = picking ? _pick! : _line!;
        shader.Use();

        var length = Camera.Distance * 0.12f;
        var tool = Armed;
        _gl.Disable(EnableCap.DepthTest);            // always reachable, never buried in terrain
        _gl.LineWidth(picking ? 14f : 3f);
        _gl.BindVertexArray(tool switch
        {
            "Rotate" => _ringVao,
            "Scale" => _handleVao,
            _ => _axisVao,
        });

        for (var i = 0; i < 3; i++)
        {
            // ONE UNIT SHAPE, ROTATED ONTO EACH AXIS. An arrow and a scale handle both start as a
            // +X line; a ring starts in the XY plane and turns to face along the axis instead,
            // because a ring is named by the axis it turns ABOUT rather than the one it lies on.
            var rot = tool == "Rotate"
                ? i switch
                {
                    0 => Matrix4x4.CreateRotationY(MathF.PI / 2),    // ring about +X
                    1 => Matrix4x4.Identity,                         // ring about +Z
                    _ => Matrix4x4.CreateRotationX(-MathF.PI / 2),   // ring about +Y
                }
                : i switch
                {
                    0 => Matrix4x4.Identity,
                    1 => Matrix4x4.CreateRotationY(-MathF.PI / 2),   // +X onto +Z
                    _ => Matrix4x4.CreateRotationZ(MathF.PI / 2),    // +X onto +Y
                };
            var model = Matrix4x4.CreateScale(length) * rot * Matrix4x4.CreateTranslation(at);

            if (picking)
            {
                var id = GizmoId + i + 1;
                _pick!.Set("uModel", model);
                _pick.Set("uView", Camera.View);
                _pick.Set("uProj", Camera.Projection(_aspect));
                _pick.Set("uSkinned", 0);
                _pick.Set("uPickColor", new Vector3((id & 0xFF) / 255f, ((id >> 8) & 0xFF) / 255f,
                                                    ((id >> 16) & 0xFF) / 255f));
            }
            else
            {
                _line!.Set("uMvp", model * viewProj);
                _line.Set("uColour", i == 0 ? new Vector3(0.90f, 0.32f, 0.28f)
                                    : i == 1 ? new Vector3(0.42f, 0.78f, 0.38f)
                                             : new Vector3(0.33f, 0.60f, 0.92f));
            }
            switch (tool)
            {
                case "Rotate": _gl.DrawArrays(PrimitiveType.LineLoop, 0, RingSegments); break;
                case "Scale": _gl.DrawArrays(PrimitiveType.Lines, 0, 6); break;
                default: _gl.DrawArrays(PrimitiveType.Lines, 0, 2); break;
            }
        }

        _gl.LineWidth(1f);
        _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>
    /// ONE DRAG, THREE MEANINGS, ONE PATH INTO A CHANGE.
    ///
    /// Move, rotate and scale all end at the same property setters the typed fields use, so a
    /// drag and a typed number produce the same pending move, the same "was" check and the same
    /// dock entry. The only thing the tool changes is what the mouse movement is read AS.
    ///
    /// Arrow 1 is UE X, 2 is UE Y, 3 is UE Z -- and a rotation about those is Roll, Pitch and Yaw
    /// respectively, which is the one place the two vocabularies do not line up.
    /// </summary>
    private void ApplyDrag(int axis, Vector3 at, Point from, Point to)
    {
        if (_editor is null || axis < 1 || axis > 3) return;
        var ax = AxesGl[axis - 1];

        if (Armed == "Rotate")
        {
            // THE ANGLE SWEPT AROUND THE GIZMO ON SCREEN. Projecting the ring itself and finding
            // the nearest point on it is more work and no more usable: what a hand does is circle
            // the pointer around the middle, and that is exactly what this measures.
            if (!Project(at, out var c)) return;
            var a0 = MathF.Atan2((float) from.Y - c.Y, (float) from.X - c.X);
            var a1 = MathF.Atan2((float) to.Y - c.Y, (float) to.X - c.X);
            var d = a1 - a0;
            if (d > MathF.PI) d -= MathF.Tau;
            if (d < -MathF.PI) d += MathF.Tau;

            // An axis pointing away from the camera turns the other way on screen.
            var facing = Vector3.Dot(ax, Vector3.Normalize(Camera.Position - at)) >= 0 ? 1f : -1f;
            var deg = float.RadiansToDegrees(d) * facing;
            if (MathF.Abs(deg) < 0.0001f) return;

            _editor.Spin(axis == 2 ? deg : 0,      // UE Y is PITCH
                         axis == 3 ? deg : 0,      // UE Z is YAW
                         axis == 1 ? deg : 0);     // UE X is ROLL
            return;
        }

        var along = AlongAxis(at, ax, from, to);
        if (Math.Abs(along) < 0.0001) return;

        if (Armed == "Scale")
        {
            // Dragging one handle's length doubles that axis, at any zoom, because the handle is
            // drawn at a fixed size on screen and `length` is what it measures.
            var f = (float) (along / Math.Max(Camera.Distance * 0.12f, 0.001f));
            _editor.Resize(axis == 1 ? f : 0, axis == 2 ? f : 0, axis == 3 ? f : 0);
            return;
        }

        _editor.Nudge(axis == 1 ? along : 0, axis == 2 ? along : 0, axis == 3 ? along : 0);
    }

    private float _aspect = 1f;

    /// <summary>
    /// WHAT IS UNDER THE CURSOR, by drawing the scene once in index colours and reading one pixel.
    ///
    /// Straight into the framebuffer Avalonia hands us, not an offscreen one: the real pass
    /// clears and redraws immediately afterwards in the same frame, so nothing of this reaches
    /// the screen and there is no framebuffer to create, size, or leak.
    ///
    /// An index is encoded as a colour and decoded back, which is why it is +1: colour zero is
    /// the cleared background, and without the offset the first placement in the scene would be
    /// indistinguishable from empty space.
    /// </summary>
    private unsafe void DoPick(Point at, float w, float h)
    {
        _pickAt = null;
        if (_gl is null || _pick is null) return;

        var scale = (float) ((VisualRoot as Avalonia.Controls.TopLevel)?.RenderScaling ?? 1.0);
        var px = (int) (at.X * scale);
        var py = (int) (at.Y * scale);
        if (px < 0 || py < 0 || px >= w || py >= h) return;

        _gl.Viewport(0, 0, (uint) w, (uint) h);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear((uint) (ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.DepthTest);

        _pick.Use();
        _pick.Set("uView", Camera.View);
        _pick.Set("uProj", Camera.Projection(h <= 0 ? 1f : w / h));
        _pick.Set("uSkinned", 0);

        for (var i = 0; i < _scene.Count; i++)
        {
            // WHAT YOU CANNOT SEE, YOU CANNOT CLICK. Picking a hidden backdrop mesh through the
            // level in front of it would be worse than not hiding it at all.
            if (Hidden(i)) continue;
            if (_scene[i].Shown is not { } shown) continue;  // and what is not drawn is not pickable
            if (!_meshes.TryGetValue(shown, out var mesh) || mesh is null) continue;
            var id = i + 1;
            _pick.Set("uModel", Placed(_scene[i]) * RenderMesh.UeToGl);
            _pick.Set("uPickColor", new Vector3((id & 0xFF) / 255f,
                                                ((id >> 8) & 0xFF) / 255f,
                                                ((id >> 16) & 0xFF) / 255f));
            mesh.DrawGeometry(_pick);
        }
        if (_purpose == Purpose.Gizmo) DrawGizmo(Matrix4x4.Identity, picking: true);

        var rgba = new byte[4];
        // GL reads bottom-up; the pointer works top-down.
        fixed (byte* b = rgba)
            _gl.ReadPixels(px, (int) h - 1 - py, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, b);

        var decoded = rgba[0] | (rgba[1] << 8) | (rgba[2] << 16);

        // AN ARROW IS TESTED BEFORE A PLACEMENT, and before the clamp: clamping first would fold
        // a gizmo id down into a scene index and silently select whatever happened to be last.
        if (decoded > GizmoId)
        {
            _axis = decoded - GizmoId;
            _awaitingGizmo = false;
            return;
        }

        if (_purpose == Purpose.Gizmo) { _axis = 0; _awaitingGizmo = false; return; }

        var index = decoded == 0 ? -1 : Math.Min(decoded - 1, _scene.Count - 1);
        Selected = index;
        Dispatcher.UIThread.Post(() => Picked?.Invoke(index));
    }

    // ---------------------------------------------------------------- the camera, by hand

    private Point _last;
    private Point _pressed;
    private bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _last = _pressed = e.GetPosition(this);
        _dragging = true;
        e.Pointer.Capture(this);
        Focus();                      // arm the flying keys; a click is how you say "I mean here"

        // ONE DRAG IS ONE UNDO STEP. A drag calls Nudge once per mouse move, so without a batch
        // around it a single shove of one crate would need forty presses of undo to take back.
        // Opened for every press: one that turns out to be an orbit changes nothing, and a batch
        // that saw no change pushes no step.
        _editor?.BeginBatch("gizmo drag");

        // ASK FIRST WHETHER THIS LANDED ON AN ARROW, and hold the camera still for the one frame
        // that takes. Orbiting immediately and finding out afterwards would jolt the view every
        // time somebody grabs the gizmo.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Selected >= 0)
        {
            _axis = 0;
            _awaitingGizmo = true;
            _purpose = Purpose.Gizmo;
            _pickAt = _pressed;
            RequestNextFrameRendering();
        }
        base.OnPointerPressed(e);
    }

    /// <summary>
    /// A pixel of pointer movement, as a distance along the dragged axis.
    ///
    /// The axis is projected to the screen and the mouse delta measured along it. That is the
    /// behaviour people expect from a gizmo -- drag along the arrow and it follows, drag across
    /// it and almost nothing happens -- and it degrades sensibly when the arrow points nearly at
    /// the camera, where a world-ray intersection goes wild instead.
    /// </summary>
    private double AlongAxis(Vector3 at, Vector3 axis, Point from, Point to)
    {
        var reach = Camera.Distance * 0.12f;
        if (!Project(at, out var a) || !Project(at + axis * reach, out var b)) return 0;

        var screen = b - a;
        var length = screen.Length();
        if (length < 0.0001f) return 0;                        // the arrow points at the camera

        var move = new Vector2((float) (to.X - from.X), (float) (to.Y - from.Y));
        return Vector2.Dot(move, screen / length) * (reach / length);
    }

    /// <summary>
    /// A world point as a screen point, done EXPLICITLY rather than through a combined matrix.
    ///
    /// Two attempts at the matrix route both came out wrong -- once with a negative w for a point
    /// plainly on screen, once with a w of 5.9 billion -- because the renderer's matrices reach
    /// GLSL through an untransposed upload, so the convention that works in the shader is not the
    /// one that works in System.Numerics. Transforming to view space and dividing is three lines,
    /// has no convention to get backwards, and is checkable by eye: a visible point has a
    /// NEGATIVE view-space Z.
    /// </summary>
    private bool Project(Vector3 world, out Vector2 screen)
    {
        screen = default;
        var v = Vector3.Transform(world, Camera.View);
        if (v.Z > -0.001f) return false;                       // behind the camera

        var t = MathF.Tan(Camera.Fov * 0.5f);
        var ndcX = v.X / (-v.Z * t * _aspect);
        var ndcY = v.Y / (-v.Z * t);
        screen = new Vector2((ndcX * 0.5f + 0.5f) * (float) Bounds.Width,
                             (0.5f - ndcY * 0.5f) * (float) Bounds.Height);
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging)
        {
            // One frame of stillness while the press asks whether it grabbed an arrow.
            if (_awaitingGizmo) { base.OnPointerMoved(e); return; }

            var now = e.GetPosition(this);

            if (_axis > 0 && GizmoAt() is { } at)
            {
                // CALLED, not posted. Input already arrives on the UI thread, and the editor
                // recomputes the drawn transform inside this call -- so the next mouse move
                // projects from where the actor now is. Posting it left the anchor a frame behind
                // and made a fast drag drift. (The PICK path is different: it runs on the render
                // thread and must post.)
                ApplyDrag(_axis, at, _last, now);
                _last = now;
                RequestNextFrameRendering();
                base.OnPointerMoved(e);
                return;
            }

            var dx = (float) (now.X - _last.X);
            var dy = (float) (now.Y - _last.Y);
            _last = now;

            // RIGHT-DRAG LOOKS, left-drag ORBITS, and they are not the same thing: look turns
            // the camera on the spot, which is the only rotation that pairs with flying, while
            // orbit swings it around whatever you are inspecting. Shift-drag pans -- it took
            // right-drag's old job, since right-drag was asked for by name. MMB dollies, as
            // asked for last time, and so does the wheel. Every rate is scaled by the camera
            // speed so a skirmish map and a single prop both feel controllable.
            var speed = (float) (_editor?.CameraSpeed ?? 1.0);
            var props = e.GetCurrentPoint(this).Properties;

            if (props.IsMiddleButtonPressed) Camera.Dolly(-dy * 0.06f * speed);
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Camera.Pan(dx * speed, dy * speed);
            else if (props.IsRightButtonPressed) Camera.Look(dx * speed, dy * speed);
            else Camera.Orbit(dx * speed, dy * speed);
            CameraTouched();
            RequestNextFrameRendering();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
        _editor?.EndBatch();

        // LETTING GO ALWAYS ENDS THE WAIT. While _awaitingGizmo is set every pointer move is
        // ignored, so a pick that never came back would leave the camera frozen with nothing
        // to say why. It is one bool; it should not be able to strand the whole viewport.
        _awaitingGizmo = false;

        // A CLICK, NOT A DRAG. The left button both orbits and selects, so the two are told
        // apart by how far the pointer moved -- three logical pixels of slack, because a hand
        // on a mouse never presses and releases at exactly the same point.
        var up = e.GetPosition(this);
        if (_axis > 0) { _axis = 0; base.OnPointerReleased(e); return; }   // finished a gizmo drag

        if (e.InitialPressMouseButton == MouseButton.Left &&
            Math.Abs(up.X - _pressed.X) < 3 && Math.Abs(up.Y - _pressed.Y) < 3)
        {
            _purpose = Purpose.Select;
            _pickAt = up;
            RequestNextFrameRendering();
        }
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // MARKED HANDLED so nothing above can also act on it, and 1.5 rather than 1 because a
        // notch that moves 10% is hard to feel at all when the level fills the screen.
        Camera.Dolly((float) e.Delta.Y * 1.5f * (float) (_editor?.CameraSpeed ?? 1.0));
        CameraTouched();
        e.Handled = true;
        RequestNextFrameRendering();
        base.OnPointerWheelChanged(e);
    }

    // ---------------------------------------------------------------- flying, by the keyboard
    //
    // HELD KEYS AND A CLOCK, not one step per key event: a key repeat stutters at the start and
    // then runs at whatever rate the operating system happens to be set to. Moving by ELAPSED
    // TIME gives the same travel per second on every machine, and diagonals that are not faster
    // than straight lines.
    //
    // DRIVEN BY THE RENDER LOOP, not a DispatcherTimer. A 16 ms timer delivered 300 units of a
    // 3,000-unit second, measured: at Normal priority it is starved by the very rendering it is
    // trying to animate, and each tick it misses is time the stall clamp then throws away. The
    // frame is already the clock here, and it cannot be starved by itself.
    //
    // The mask is written on the UI thread and read on the render thread, which is why it is an
    // int behind Volatile rather than the HashSet this started as.

    private const int FlyForward = 1, FlyBack = 2, FlyLeft = 4, FlyRight = 8, FlyDown = 16, FlyUp = 32;

    private int _flyMask;
    private bool _boost;
    public int FlyFrames; public double FlySeconds; public int RenderFrames; public double RenderMs;   // headless diagnostics, reported by --fly
    private readonly System.Diagnostics.Stopwatch _flyClock = new();

    private static int BitFor(Key k) => k switch
    {
        Key.W => FlyForward, Key.S => FlyBack, Key.A => FlyLeft,
        Key.D => FlyRight,   Key.Q => FlyDown, Key.E => FlyUp,
        _ => 0,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl belongs to the undo shortcut, not to us -- do not fly on Ctrl+W.
        var bit = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0 : BitFor(e.Key);
        if (bit != 0)
        {
            _boost = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            Volatile.Write(ref _flyMask, Volatile.Read(ref _flyMask) | bit);
            if (!_flyClock.IsRunning) _flyClock.Restart();
            RequestNextFrameRendering();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _boost = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var bit = BitFor(e.Key);
        if (bit != 0) Volatile.Write(ref _flyMask, Volatile.Read(ref _flyMask) & ~bit);
        base.OnKeyUp(e);
    }

    private void StopFlying() => Volatile.Write(ref _flyMask, 0);

    /// <summary>One frame's worth of flight. Called from the render, which is the clock.</summary>
    private void Fly()
    {
        var mask = Volatile.Read(ref _flyMask);
        if (mask == 0) { _flyClock.Reset(); return; }

        // A frame that took longer than a tenth of a second was a stall, not flight.
        var dt = (float) Math.Clamp(_flyClock.Elapsed.TotalSeconds, 0, 0.1);
        _flyClock.Restart();
        if (dt <= 0) return;
        FlyFrames++; FlySeconds += dt;

        var move = new Vector3(
            ((mask & FlyRight)   != 0 ? 1 : 0) - ((mask & FlyLeft) != 0 ? 1 : 0),
            ((mask & FlyUp)      != 0 ? 1 : 0) - ((mask & FlyDown) != 0 ? 1 : 0),
            ((mask & FlyForward) != 0 ? 1 : 0) - ((mask & FlyBack) != 0 ? 1 : 0));
        if (move == Vector3.Zero) return;

        // SPEED FOLLOWS THE ZOOM. Distance is how far out you are looking, so tying the rate to
        // it makes one set of keys work on a whole skirmish map and on a single crate -- the same
        // reason panning scales with it. Shift is the usual three times faster.
        // SPEED FOLLOWS THE LEVEL AS WELL AS THE ORBIT DISTANCE.
        //
        // It was Distance alone, which is right for inspecting a prop and useless for crossing a
        // map: stand close to something in the hub and Distance is small, so the camera crawls
        // even at the slider's maximum -- his report was "you can't even move the camera even at
        // max speed". The level's own typical radius is the floor, so being near an object no
        // longer means being slow in a place that is enormous.
        var reach = MathF.Max(Camera.Distance, _coreRadius * 0.02f);
        var speed = reach * 1.5f * (float) (_editor?.CameraSpeed ?? 1.0) * dt;
        if (_boost) speed *= 3f;

        move = Vector3.Normalize(move) * speed;   // a diagonal is not faster than a straight line
        Camera.Move(move.Z, move.X, move.Y);
        CameraTouched();
    }
}

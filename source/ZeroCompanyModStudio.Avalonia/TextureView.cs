using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Silk.NET.OpenGL;
using UEToolkit.App.Rendering;
using Shader = UEToolkit.App.Rendering.Shader;   // Silk.NET has one of its own

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE TEXTURE ON SCREEN, THROUGH THE TOOLKIT'S OWN TEXTURE SHADER.
///
/// NOT A DECODED PNG IN AN Image CONTROL, which was the tempting shortcut. Four things that
/// matter here are already solved in <see cref="Shaders.TextureFragment"/> and would each have
/// to be rebuilt, worse, on the CPU: the channel masks, the rule that a LONE channel renders as
/// GREY rather than through a red/green/blue filter, the checkerboard that tells transparent
/// from black, and clamping outside the image so a zoomed map does not smear its edge texel and
/// look seamless when it is not.
///
/// The shader and TextureCache are LINKED into this project, not copied — the same line the mesh
/// viewport takes. So a texture is judged here through exactly the code that binds it to a model
/// one tab over, and the two cannot drift.
/// </summary>
public sealed unsafe class TextureView : OpenGlControlBase
{
    private GL? _gl;
    private Shader? _shader;
    private TextureCache? _textures;
    private uint _quadVao, _quadVbo;

    /// <summary>The texture waiting to be uploaded. Done on the GL thread, where the GL is.</summary>
    private UTexture? _pending;
    private uint _handle;

    /// <summary>What went wrong, for the panel to say rather than show an empty box.</summary>
    public string? Fault { get; private set; }

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    /// <summary>Raised once the texture is up, so the tab can report what it got.</summary>
    public event Action? Loaded;

    // ---------------------------------------------------------------- what is shown

    /// <summary>Which channels reach the screen; see <see cref="Shaders.TextureFragment"/>.</summary>
    private bool _r = true, _g = true, _b = true, _a;

    public bool ShowR { get => _r; set { _r = value; RequestNextFrameRendering(); } }
    public bool ShowG { get => _g; set { _g = value; RequestNextFrameRendering(); } }
    public bool ShowB { get => _b; set { _b = value; RequestNextFrameRendering(); } }
    public bool ShowAlpha { get => _a; set { _a = value; RequestNextFrameRendering(); } }

    /// <summary>Screen pixels per texel. 0 means "fit", recomputed on every resize.</summary>
    public float Zoom;
    public Vector2 Pan;

    /// <summary>The zoom the last frame actually used, so the panel reports what was drawn.</summary>
    public float LastZoom { get; private set; } = 1f;

    /// <summary>Raised after a frame, so the panel's zoom readout can follow a wheel or a fit.</summary>
    public event Action? Rendered;

    public void Show(UTexture texture)
    {
        _pending = texture;
        Fault = null;
        Zoom = 0f;
        Pan = Vector2.Zero;
        RequestNextFrameRendering();
    }

    /// <summary>Back to fitting the window, which is also what a newly opened texture does.</summary>
    public void Fit()
    {
        Zoom = 0f;
        Pan = Vector2.Zero;
        RequestNextFrameRendering();
    }

    /// <summary>One screen pixel per texel — the only honest way to judge sharpness.</summary>
    public void Actual()
    {
        Zoom = 1f;
        Pan = Vector2.Zero;
        RequestNextFrameRendering();
    }


    /// <summary>
    /// A TRANSPARENT FILL, SO THE MOUSE CAN FIND THIS CONTROL AT ALL.
    ///
    /// Avalonia hit-tests against what a visual DREW, and an OpenGlControlBase draws through a
    /// composition operation the hit test cannot see — so the pointer goes to whatever is behind
    /// it and every mouse handler here is dead code. Here that is drag-to-pan and wheel-to-zoom.
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
            _shader = new Shader(_gl, Shaders.TextureVertex, Shaders.TextureFragment);
            _textures = new TextureCache(_gl);
            BuildQuad();
        }
        catch (Exception e) { Fault = e.Message; Console.WriteLine("[texture] GL FAILED: " + e); }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is not null && _quadVao != 0)
        {
            _gl.DeleteVertexArray(_quadVao);
            _gl.DeleteBuffer(_quadVbo);
        }
        _quadVao = _quadVbo = 0;
        _shader?.Dispose();
        _shader = null;
        // The cache owns the uploaded texture, so disposing it releases the handle too.
        _textures?.Dispose();
        _textures = null;
        _handle = 0;
        _gl = null;
    }

    private void BuildQuad()
    {
        if (_gl is null) return;
        float[] quad = [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f];
        _quadVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_quadVao);
        _quadVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* p = quad)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint) (quad.Length * sizeof(float)),
                           p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), null);
        _gl.BindVertexArray(0);
    }

    // ---------------------------------------------------------------- the frame

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || _shader is null) return;

        var w = (int) Math.Max(1, Bounds.Width);
        var h = (int) Math.Max(1, Bounds.Height);

        _gl.Viewport(0, 0, (uint) w, (uint) h);
        _gl.ClearColor(0.09f, 0.09f, 0.11f, 1f);
        _gl.Clear((uint) ClearBufferMask.ColorBufferBit);
        // A flat image needs neither, and leaving depth on would test fragments against a buffer
        // nothing in this control ever writes.
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);

        Upload();

        if (_handle != 0 && ImageWidth > 0 && ImageHeight > 0)
        {
            var zoom = Zoom > 0f ? Zoom : Fitted(w, h);
            LastZoom = zoom;

            // Quad-space scale: how much of the rectangle the image covers, in 0..1.
            var sx = ImageWidth * zoom / w;
            var sy = ImageHeight * zoom / h;
            // Centre, then pan. Pan is in screen pixels so dragging tracks the cursor exactly.
            var ox = (1f - sx) * 0.5f + Pan.X / w;
            var oy = (1f - sy) * 0.5f - Pan.Y / h;

            _shader.Use();
            _shader.Set("uXform", new Vector4(sx, sy, ox, oy));
            _shader.Set("uMask", new Vector4(_r ? 1f : 0f, _g ? 1f : 0f, _b ? 1f : 0f, _a ? 1f : 0f));
            _shader.Set("uAlphaOnly", _a && !_r && !_g && !_b ? 1 : 0);
            _shader.Set("uChecker", 8f);
            _shader.Set("uTex", 0);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _handle);
            // Nearest above 1:1. Linear magnification invents texels, and at that point you are
            // inspecting the filter rather than the map.
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                             zoom >= 1f ? (int) GLEnum.Nearest : (int) GLEnum.Linear);

            _gl.BindVertexArray(_quadVao);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            _gl.BindVertexArray(0);
        }

        // WAIT FOR THE GPU BEFORE AVALONIA COMPOSITES. Cheap here — one quad — and the rule is
        // the control's, not the scene's: without it the frame is composited with the draw still
        // in flight. See the mesh viewport for the long version.
        _gl.Finish();

        if (_shotTo is not null) Grab(w, h);
        Rendered?.Invoke();
    }

    private float Fitted(int w, int h)
        => ImageWidth <= 0 || ImageHeight <= 0
            ? 1f
            : MathF.Min((float) w / ImageWidth, (float) h / ImageHeight);

    /// <summary>
    /// Upload the waiting texture, once, on the thread that owns the GL.
    ///
    /// THE SHIPPED SIZE OFF PlatformData, not ImportedSize. This game cooks textures down — a
    /// 6144x4096 source ships at 3072x2048 — so a viewer that reports the authoring size is
    /// reporting a number that is not in the file the mod will actually load.
    /// </summary>
    private void Upload()
    {
        if (_pending is null || _gl is null || _textures is null) return;

        var texture = _pending;
        _pending = null;
        _handle = 0;
        ImageWidth = ImageHeight = 0;

        try
        {
            _handle = _textures.GetOrUpload(texture, exact: true);   // a viewer shows the CPU decode, channel for channel
            if (_handle == 0)
            {
                Fault = _textures.Failures.LastOrDefault() ?? $"{texture.Name} decoded to nothing";
            }
            else
            {
                ImageWidth = texture.PlatformData?.SizeX ?? 0;
                ImageHeight = texture.PlatformData?.SizeY ?? 0;
            }
        }
        catch (Exception e) { Fault = e.Message; }

        Console.WriteLine($"[texture] {texture.Name}: handle={_handle} " +
                          $"{ImageWidth}x{ImageHeight} fault={Fault ?? "none"}");
        Loaded?.Invoke();
    }

    // ---------------------------------------------------------------- photographing it

    private string? _shotTo;

    /// <summary>
    /// Write the next frame to a PNG.
    ///
    /// NECESSARY, not a convenience — the same reason the mesh viewport has one. Avalonia draws
    /// GL into a surface the window compositor owns and PrintWindow reads it as BLACK however
    /// well it is drawing, so the framebuffer is the only honest picture of this control.
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

            // THE IMAGE'S OWN AREA, not "how much is not background" — the background here is a
            // CHECKERBOARD, so a difference-from-clear count would report the checker as content
            // and read high for a texture that never uploaded. What says this drew is the
            // handle, the size and the zoom, so print those beside the picture.
            long sum = 0;
            for (var i = 0; i < pixels.Length; i += 4)
                sum += (pixels[i] * 299 + pixels[i + 1] * 587 + pixels[i + 2] * 114) / 1000;
            Console.WriteLine($"[texture] shot {w}x{h} -> {path}, image {ImageWidth}x{ImageHeight} " +
                              $"handle={_handle} zoom {LastZoom:0.###} " +
                              $"mean luma {sum / (double) (w * h) / 255.0:0.###}");
        }
        catch (Exception e) { Console.WriteLine("[texture] shot failed: " + e.Message); }
    }

    // ---------------------------------------------------------------- the mouse

    private Point? _drag;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } was) { base.OnPointerMoved(e); return; }

        var now = e.GetPosition(this);
        Pan += new Vector2((float) (now.X - was.X), (float) (now.Y - was.Y));
        _drag = now;
        RequestNextFrameRendering();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = null;
        e.Pointer.Capture(null);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // FROM WHATEVER WAS ON SCREEN, which on the first notch is the fitted zoom rather than
        // 1.0 — otherwise the first scroll on a 4K texture jumps from fitting the window to
        // full size and loses the thing being looked at.
        var from = Zoom > 0f ? Zoom : LastZoom;
        Zoom = Math.Clamp(from * (e.Delta.Y > 0 ? 1.15f : 1f / 1.15f), 0.01f, 64f);
        RequestNextFrameRendering();
        base.OnPointerWheelChanged(e);
    }
}

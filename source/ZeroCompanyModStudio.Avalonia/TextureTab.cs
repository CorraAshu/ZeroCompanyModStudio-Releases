using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A TEXTURE, OPEN AS A TAB, with the 2D viewer in it.
///
/// The same shape as <see cref="MeshTab"/> and for the same reasons: the control is held here
/// rather than created by the template because it owns a GL context and an uploaded texture, and
/// rebuilding it every time the tab is switched away from and back would re-read the package and
/// re-decode the image to show the same thing again.
/// </summary>
public sealed class TextureTab : StudioTab
{
    private readonly string _filePath;
    private string _status = "reading the package…";
    private string _format = "";
    private string _size = "";
    private string _zoom = "";

    public TextureTab(string title, string filePath, string type)
    {
        _title = title;
        _filePath = filePath;
        Type = type;
        View.Loaded += Report;
        // The zoom readout follows the frame that was actually drawn rather than the value asked
        // for, because "fit" is a number only the render knows — it depends on the control's
        // size, which the panel cannot see. The ImGui viewer reported 0% on the frame a texture
        // opened for exactly this reason.
        View.Rendered += () =>
        {
            var z = $"{View.LastZoom * 100:0.#}%";
            if (z != _zoom) Zoom = z;
        };
    }

    private readonly string _title;
    public override string Title => Mine is null ? _title : _title + "  (your import)";

    /// <summary>The asset's own name, without "(your import)" -- for file names, dialogs and the log.</summary>
    public string BaseTitle => _title;
    public override string Tooltip => _filePath;

    /// <summary>
    /// The author's image this tab is showing OVER the game's texture, or null when it shows the game's own.
    ///
    /// "Import does nothing" was this tab going on showing the game's file after an import the project had recorded --
    /// and showing it again when reopened, which read as the log lying. A tab on a game texture the open mod replaces
    /// shows the replacement; the view model sets it on open, after Import and after Revert.
    /// </summary>
    public string? Mine { get; private set; }

    /// <summary>Show the author's import over this game texture.</summary>
    public void ShowMine(string file)
    {
        if (string.Equals(Mine, file, StringComparison.OrdinalIgnoreCase)) return;
        Mine = file;
        Raise(nameof(Title));
        LoadFile(file);
    }

    /// <summary>Back to the game's own texture, once the mod no longer replaces it.</summary>
    public void ShowGame()
    {
        if (Mine is null) return;
        Mine = null;
        Raise(nameof(Title));
        if (Texture is { } texture)
        {
            Format = texture.PlatformData?.PixelFormat.ToString() ?? "?";
            Status = "decoding…";
            View.Show(texture);
        }
    }
    public string Type { get; }
    public string FilePath => _filePath;

    /// <summary>The viewer itself. One per tab, kept for the life of the tab.</summary>
    public TextureView View { get; } = new();

    /// <summary>
    /// The loaded texture, KEPT so Export has something to write.
    ///
    /// The viewer only needs it long enough to upload; exporting needs the object again, and
    /// going back through the package read for a file already in hand is slow, gated, and
    /// another chance for CUE4Parse's cache to race.
    /// </summary>
    public UTexture? Texture { get; private set; }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// One writable format. DDS IS ABSENT ON PURPOSE — CUE4Parse's own enum labels it
    /// "Not Implemented", and a format that silently writes nothing is worse than one that is
    /// not offered.
    /// </summary>
    public sealed record ExportFormat(string Name, ETextureFormat Format)
    {
        public override string ToString() => Name;
    }

    public static ExportFormat[] Formats { get; } =
    [
        new("PNG", ETextureFormat.Png),
        new("TGA", ETextureFormat.Tga),
        new("JPEG", ETextureFormat.Jpeg),
    ];

    private ExportFormat _pickedFormat = Formats[0];

    /// <summary>Which format Export will write. Named apart from <see cref="Format"/>, which is
    /// the texture's own PIXEL format and a different thing entirely.</summary>
    public ExportFormat SelectedFormat
    {
        get => _pickedFormat;
        set => Set(ref _pickedFormat, value);
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Format { get => _format; private set => Set(ref _format, value); }
    public string Size { get => _size; private set => Set(ref _size, value); }
    public string Zoom { get => _zoom; private set => Set(ref _zoom, value); }

    // THE CHANNEL TOGGLES, FORWARDED. The view holds the truth because the shader reads it from
    // there every frame; these exist so the checkboxes have something to bind to, and they
    // deliberately do not keep a second copy of the answer.
    public bool ShowR
    {
        get => View.ShowR;
        set { View.ShowR = value; Raise(nameof(ShowR)); }
    }

    public bool ShowG
    {
        get => View.ShowG;
        set { View.ShowG = value; Raise(nameof(ShowG)); }
    }

    public bool ShowB
    {
        get => View.ShowB;
        set { View.ShowB = value; Raise(nameof(ShowB)); }
    }

    public bool ShowAlpha
    {
        get => View.ShowAlpha;
        set { View.ShowAlpha = value; Raise(nameof(ShowAlpha)); }
    }

    /// <summary>Every channel back on, which is what "colour" means and what opening one does.</summary>
    public void Colour()
    {
        ShowR = ShowG = ShowB = true;
        ShowAlpha = false;
    }

    /// <summary>
    /// Alpha ALONE, which the shader then draws as grey rather than through a filter.
    ///
    /// Worth a button of its own: masks in this game carry meaning per channel — green is
    /// roughness — and an alpha of zero premultiplied the colour away on more than one of them.
    /// Being able to see the alpha by itself is how that gets noticed.
    /// </summary>
    public void AlphaOnly()
    {
        ShowR = ShowG = ShowB = false;
        ShowAlpha = true;
    }

    public void Fit() => View.Fit();
    public void Actual() => View.Actual();

    /// <summary>Show the author's own image file instead of the game's texture. There is no game asset behind it, so
    /// <see cref="Texture"/> stays null.</summary>
    public void LoadFile(string file)
    {
        Format = "your import (" + System.IO.Path.GetExtension(file).TrimStart('.').ToUpperInvariant() + ")";
        Status = "reading the file…";
        View.ShowFile(file);
    }

    /// <summary>
    /// Read the asset OFF THE UI THREAD, then hand the texture to the viewer.
    ///
    /// The split matters for the same reason it does for a mesh: the package read is slow and
    /// has to be gated — CUE4Parse's cache is not safe to race on and the failure is a native
    /// AccessViolationException .NET cannot catch — while the DECODE and the upload have to
    /// happen on the render thread, because that is where the GL is.
    /// </summary>
    public async Task LoadAsync(IFileProvider provider)
    {
        var path = _filePath;

        var texture = await Task.Run<UTexture?>(() =>
        {
            try { return AssetLoader.Load<UTexture2D>(provider, path); }
            catch (Exception) { return null; }
        });

        if (texture is null)
        {
            Status = "could not read a texture out of that package";
            return;
        }

        Texture = texture;
        // THE AUTHOR'S IMPORT, if it went up while the package was still being read, stays on screen: the game's
        // texture is kept for Export and for a Revert.
        if (Mine is not null) return;
        Format = texture.PlatformData?.PixelFormat.ToString() ?? "?";
        Status = "decoding…";
        View.Show(texture);
    }

    /// <summary>
    /// What the viewer ended up with, said plainly.
    ///
    /// THE SHIPPED SIZE, which is not the imported one — this game cooks textures down, so the
    /// authoring dimensions are a number that is not in the file a mod will load.
    /// </summary>
    private void Report()
    {
        if (View.ImageWidth <= 0 || View.ImageHeight <= 0)
        {
            Size = "";
            Status = View.Fault is { Length: > 0 } f ? f : "nothing to show";
            return;
        }

        Size = $"{View.ImageWidth} x {View.ImageHeight}";
        Status = View.Fault is { Length: > 0 } fault ? fault : Format;
    }
}

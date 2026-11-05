using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// EXTRACT A WHOLE FOLDER out of the Data Explorer, with the formats chosen first.
///
/// The author's ask, in full: "right click a folder in the data explorer and see a extract...
/// Then a window pops up that will let you pick the location you want to extract and give you
/// options for mesh format, PSK,FBX....etc and same for textures and animations. Then it will
/// dump the full folder to your selected folder with the same file structure shown in the data
/// explorer".
///
/// WHAT IS ACTUALLY NEW HERE IS THE ASKING. <see cref="BatchExporter.ExportPaths"/> already walks
/// a list with progress and a cancel, <see cref="AppSettings.ToRequest"/> already turns the format
/// choices into an <c>ExportRequest</c>, and the exporter already writes by package path, so the
/// mirrored structure he asked for falls out for free rather than being built.
///
/// THE SETTINGS ARE SEEDED FROM HIS, NOT WRITTEN BACK TO THEM. A one-off extraction in glTF
/// should not quietly change what the Export panel and the CLI do next time; the shared answer
/// stays in <see cref="AppSettings"/> and this borrows a copy of it.
/// </summary>
public partial class ExtractWindow : Window
{
    private readonly IFileProvider? _provider;
    private readonly List<string> _paths = new();
    private readonly Action<string>? _say;

    private CancellationTokenSource? _cancel;
    private bool _running;

    // THE GENERATED FIELDS ARE REAL HERE, unlike MainWindow's. That window loads its XAML with
    // AvaloniaXamlLoader directly, so its Name= fields stay null and it looks every control up;
    // this one calls the generated InitializeComponent, which assigns them. Declaring lookups of
    // the same names beside them is a duplicate-member build error, not a fallback.

    public ExtractWindow() => InitializeComponent();

    public ExtractWindow(string folderPath, IReadOnlyList<AssetNode> files,
                         IFileProvider provider, AppSettings settings, Action<string> say) : this()
    {
        _provider = provider;
        _say = say;
        _paths.AddRange(files.Select(f => f.FullPath));

        FolderText.Text = folderPath;

        // WHAT IS IN THERE, BEFORE THE BUTTON IS PRESSED. A folder here is anything from four
        // assets to fourteen thousand, and a number that large is the whole decision.
        var byKind = files.GroupBy(f => f.Kind)
                          .OrderByDescending(g => g.Count())
                          .Select(g => $"{g.Count():N0} {GameNode.TypeName(g.Key)}")
                          .ToList();
        TallyText.Text = files.Count == 0
            ? "Nothing in this folder to extract."
            : $"{files.Count:N0} file(s), everything at or below this folder" +
              (byKind.Count > 0 ? "  ·  " + string.Join(", ", byKind.Take(6)) : "");

        DestBox.Text = settings.OutputDirectory.Length > 0
            ? Path.Combine(settings.OutputDirectory, SafeLeaf(folderPath))
            : "";

        MeshBox.ItemsSource = Enum.GetValues<ExportTarget>();
        MeshBox.SelectedItem = settings.MeshFormat;
        LodBox.ItemsSource = Enum.GetValues<ELodFormat>();
        LodBox.SelectedItem = settings.LodFormat;
        NaniteBox.ItemsSource = Enum.GetValues<ENaniteMeshFormat>();
        NaniteBox.SelectedItem = settings.NaniteFormat;
        TexBox.ItemsSource = Enum.GetValues<ETextureFormat>();
        TexBox.SelectedItem = settings.TextureFormat;
        MatsBox.IsChecked = settings.ExportMaterials;
        MorphBox.IsChecked = settings.ExportMorphTargets;

        GoBtn.IsEnabled = files.Count > 0;
    }

    /// <summary>The folder's own name, usable as a directory. "[root]" and separators are not.</summary>
    private static string SafeLeaf(string path)
    {
        var leaf = path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "Extract";
        foreach (var bad in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(bad, '_');
        return leaf.Trim('[', ']', ' ').Length == 0 ? "Extract" : leaf.Trim('[', ']', ' ');
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Extract into",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { Length: > 0 } local)
            DestBox.Text = Path.Combine(local, SafeLeaf(FolderText.Text ?? ""));
    }

    private async void OnExtract(object? sender, RoutedEventArgs e) => await Extract();

    /// <summary>Point it somewhere, for a driver flag. The box is what Extract reads.</summary>
    public void SetDestination(string dest) => DestBox.Text = dest;

    /// <summary>
    /// THE WHOLE JOB, callable. Split out of the Click handler so a driver flag can press this
    /// button: a dialog nothing can drive is a dialog whose only test is somebody opening it, and
    /// the half that writes files is the half worth witnessing.
    /// </summary>
    public async Task Extract()
    {
        // THE SAME BUTTON STOPS IT. A run of fourteen thousand assets that cannot be called off is
        // one somebody kills the application to escape, which loses the partial result too.
        if (_running) { _cancel?.Cancel(); StatusText.Text = "stopping…"; return; }

        var dest = (DestBox.Text ?? "").Trim();
        if (dest.Length == 0) { StatusText.Text = "pick a folder to extract into"; return; }
        if (_provider is null || _paths.Count == 0) { StatusText.Text = "nothing to extract"; return; }

        try { Directory.CreateDirectory(dest); }
        catch (Exception ex) { StatusText.Text = $"cannot write there — {ex.Message}"; return; }

        var request = new AppSettings
        {
            OutputDirectory = dest,
            MeshFormat = MeshBox.SelectedItem is ExportTarget mf ? mf : ExportTarget.ActorX,
            LodFormat = LodBox.SelectedItem is ELodFormat lf ? lf : ELodFormat.FirstLod,
            NaniteFormat = NaniteBox.SelectedItem is ENaniteMeshFormat nf ? nf
                                                                          : ENaniteMeshFormat.OnlyNaniteLOD,
            TextureFormat = TexBox.SelectedItem is ETextureFormat tf ? tf : ETextureFormat.Tga,
            ExportMaterials = MatsBox.IsChecked == true,
            ExportMorphTargets = MorphBox.IsChecked == true,
        }.ToRequest(dest);

        _cancel = new CancellationTokenSource();
        _running = true;
        GoBtn.Content = new TextBlock { Text = "Stop" };
        Bar.IsVisible = true;
        Bar.Value = 0;
        Errors.IsVisible = false;
        StatusText.Text = $"reading {_paths.Count:N0} asset(s)…";
        _say?.Invoke($"extract: {_paths.Count:N0} asset(s) from {FolderText.Text} into {dest}");

        var started = DateTime.UtcNow;
        var paths = _paths.ToList();
        var token = _cancel.Token;

        var result = await Task.Run(() => BatchExporter.ExportPaths(
            _provider, paths, request,
            p => Dispatcher.UIThread.Post(() => Report(p, started)), token));

        _running = false;
        GoBtn.Content = new TextBlock { Text = "Extract" };
        Bar.Value = 1;

        var took = DateTime.UtcNow - started;
        var line = $"extract: {result.Exported:N0} written, {result.Failed:N0} failed, " +
                   $"{Took(took)} — {dest}";
        StatusText.Text = token.IsCancellationRequested
            ? $"stopped after {result.Exported:N0} of {paths.Count:N0}"
            : $"{result.Exported:N0} written in {Took(took)}";
        _say?.Invoke(token.IsCancellationRequested ? line + " (stopped)" : line);

        // THE FAILURES ARE SHOWN, not counted and thrown away. "412 failed" with no reason is a
        // number somebody can do nothing with; the first few reasons are usually all one reason.
        if (result.Errors.Count > 0)
        {
            Errors.IsVisible = true;
            Errors.Text = $"{result.Errors.Count:N0} problem(s):\n" +
                          string.Join("\n", result.Errors.Take(8)) +
                          (result.Errors.Count > 8 ? $"\n… and {result.Errors.Count - 8:N0} more" : "");
            foreach (var err in result.Errors.Take(40)) _say?.Invoke($"  {err}");
        }
    }

    private void Report(ExportProgress p, DateTime started)
    {
        if (p.Total > 0) Bar.Value = (double)p.Done / p.Total;

        var msg = $"{p.Done:N0} of {p.Total:N0}  ·  {p.Current}";

        // MEASURED FROM THIS RUN, not guessed at from a rate written down somewhere. A folder of
        // Nanite meshes and a folder of icons are orders of magnitude apart, so the only estimate
        // worth showing is the one this run has earned. Twenty assets in before it says anything.
        if (p.Done >= 20 && p.Done < p.Total)
        {
            var per = (DateTime.UtcNow - started).TotalSeconds / p.Done;
            var left = TimeSpan.FromSeconds(per * (p.Total - p.Done));
            msg += $"  ·  about {Took(left)} left";
        }
        StatusText.Text = msg;
    }

    private static string Took(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:F1} hr"
        : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:F0} min"
        : $"{t.TotalSeconds:F0}s";

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        Close();
    }
}

using UEToolkit.App.Rendering;
using UEToolkit.Core;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// EXPORT WHAT IS LOADED TO FBX, from the editor.
///
/// The engine is <see cref="LevelFbxExport"/> in Core, which takes a neutral list of (mesh, world matrix, actor,
/// sublevel, material overrides) and writes one FBX, a manifest and a Textures folder. This file is the editor's half:
/// WHICH placements, and how a person is told what happened.
///
/// THE LIST IS THE VIEWPORT'S OWN, never Core's <c>LevelExport.Read</c>. The editor's reader applies the fact and rule
/// gates, hidden actor and component flags, the variant choice, the de-duplication and the level-instance walk, and the
/// viewport then applies its three visibility switches; for SK_Anaxes_030 the editor draws 5,233 placements where Core's
/// reader finds 198. A second way of finding positions would be a second set of bugs.
///
/// THE STOCK MAP, NOT THE EDITED ONE. Every value read off a placement is the one the game ships: <c>MeshPath</c> (not
/// <c>Shown</c>, which a pending swap rewrites), <c>Model</c> (not <c>Offset</c>/<c>Warp</c>, which a pending move
/// lives in), <c>Materials</c>; a pending spawn is a preview of something that does not exist yet and is skipped. The
/// export is for taking the game's own level into another program, and a change of yours, saved in a project or not,
/// is not in the game until it is built and installed.
/// </summary>
public sealed partial class Editor
{
    // ---------------------------------------------------------------- what the window binds to

    private bool _exporting;

    /// <summary>True while an export is running; drives the progress line and disables a second one.</summary>
    public bool Exporting
    {
        get => _exporting;
        private set { if (Set(ref _exporting, value)) Raise(nameof(CanExport)); }
    }

    /// <summary>
    /// The menu entry is enabled: a game is mounted, a map or sublevel is open, nothing is being read and no export is
    /// running. Not while a map is still being READ (<see cref="Busy"/>): the list is then partial in a way no count in
    /// the Log could explain. Not with nothing open: there is no scene to ask the viewport about, and the entry used to
    /// walk a person through the tab switch and the folder picker before saying so.
    /// </summary>
    public bool CanExport => _provider is not null && _openedName.Length > 0 && !Busy && !_exporting && !_viewportLoading;

    private bool _viewportLoading;

    /// <summary>
    /// The viewport is still loading meshes or dressing them with textures. An export started then holds the package
    /// gate per mesh and per texture while the render pass waits on it (a stall of over a second was measured), and it
    /// writes a partly loaded scene. The window sets this from the viewport; the entry stays disabled until it clears.
    /// </summary>
    public bool ViewportLoading
    {
        get => _viewportLoading;
        set { if (Set(ref _viewportLoading, value)) { Raise(nameof(CanExport)); Raise(nameof(ExportTip)); } }
    }

    private const string ExportTipText =
        "Writes what the viewport is drawing right now - the game's own map, with your unticked sublevels, backdrop and greybox left out - as one FBX file with its textures, into a new folder inside the one you pick. Unsaved moves, swaps and new actors are not in it.";

    /// <summary>The entry's tooltip: what it writes, or why it is greyed out while the viewport is still loading.</summary>
    public string ExportTip => _viewportLoading
        ? "Not yet: the viewport is still loading and dressing the level. This entry is enabled when it has finished, so the file holds the whole level and the window stays responsive while it is written."
        : ExportTipText;

    /// <summary>
    /// Why there is nothing to export right now, in a plain sentence, or null when there is something. Asked BEFORE the
    /// folder picker, so a person is never made to choose a folder for an export that cannot happen.
    /// </summary>
    public string? WhyNothingToExport()
    {
        if (_openedName.Length == 0) return "export: open a map, or one of its sublevels, first - nothing is loaded to export";
        if (DrawnSource is null) return "export: nothing is open";
        var (drawn, info) = DrawnSource();
        return drawn.Count == 0 ? NothingDrawnSentence(info) : null;
    }

    private static string NothingDrawnSentence(Viewport.DrawnInfo info) =>
        "export: the viewport is drawing no placements, so there is nothing to export;" + ScopeSentence(info);

    private string _exportLine = "";

    // Cancelling has been asked for: the progress line says so and the next progress report must not take it back.
    private bool _cancelling;

    /// <summary>The progress line: what phase, how far, and which mesh or material is being worked on.</summary>
    public string ExportLine { get => _exportLine; private set => Set(ref _exportLine, value); }

    private double _exportFraction;

    /// <summary>0..1, for the bar. It steps back once, when the materials are discovered after the meshes.</summary>
    public double ExportFraction { get => _exportFraction; private set => Set(ref _exportFraction, value); }

    /// <summary>
    /// The viewport's drawn set, supplied by the window (the viewport is a control and the editor does not hold one).
    /// Called on the UI thread, which is where the viewport renders (see <see cref="Viewport.DrawnPlacements()"/>).
    /// </summary>
    public Func<(IReadOnlyList<LevelReader.Placement> Drawn, Viewport.DrawnInfo Info)>? DrawnSource { get; set; }

    /// <summary>What the open map or sublevel is called, set by <see cref="OpenMap"/> and <see cref="OpenSublevelAsync"/>.</summary>
    private string _openedName = "";

    /// <summary>The default file name: the open map or sublevel, made safe as a file name.</summary>
    public string ExportName => SafeFileName(_openedName.Length > 0 ? _openedName : "Level");

    // ---------------------------------------------------------------- where it goes

    /// <summary>
    /// A FOLDER OF ITS OWN inside the one the person picked: <c>picked\name\</c>, or <c>name_2</c>, <c>name_3</c>… when
    /// that is taken. Nothing already on disk is ever overwritten.
    ///
    /// Why not the picked folder itself: the textures go in one flat <c>Textures</c> folder named after the texture
    /// ASSET, and two different assets can share a name (the exporter then writes <c>T_X.png</c> and <c>T_X__2.png</c>).
    /// Two exports into one folder would write over each other's <c>T_X.png</c> with a different texture, and the first
    /// FBX would silently point at the second one's picture.
    /// </summary>
    public static string UniqueExportFolder(string picked, string name, out string unique)
    {
        unique = name;
        var folder = Path.Combine(picked, unique);
        for (var n = 2; Directory.Exists(folder) || File.Exists(folder); n++)
        {
            unique = $"{name}_{n}";
            folder = Path.Combine(picked, unique);
        }
        return folder;
    }

    private static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Level" : clean;
    }

    // ---------------------------------------------------------------- the export

    /// <summary>
    /// Write what the viewport is drawing, as the game ships it, into <paramref name="folder"/> (created if missing):
    /// <c>name.fbx</c>, <c>name.materials.json</c>, <c>Textures\</c> and, only when the level has a cut-out material, the 3ds Max script <c>name.3dsmax-cutouts.ms</c>. <paramref name="name"/> defaults to
    /// <see cref="ExportName"/>.
    ///
    /// Everything it has to say goes to the Log: the counts, the time, the scope (what the viewport hides or is still
    /// loading, so the file being smaller than the picture is never silent) and every item the file does not contain
    /// with the reason. Returns the engine's result, or null when it was cancelled or failed (the Log says which, and
    /// nothing of the export is left on disk then: the engine removes what it created).
    ///
    /// Must be called on the UI thread: it reads the viewport's drawn set, and the progress it raises is marshalled back
    /// here. The export itself runs on a worker and takes the package gate per read, never on this thread.
    /// </summary>
    public async Task<LevelFbxExport.Result?> ExportLoadedAsync(
        string folder, IProgress<(int Done, int Total, string What)>? progress, CancellationToken cancel,
        string? name = null)
    {
        if (_provider is not { } provider) { Status = "export: no game is mounted"; return null; }
        if (_exporting) { Status = "export: one is already running"; return null; }
        if (DrawnSource is null) { Status = "export: nothing is open"; return null; }

        name = SafeFileName(name ?? ExportName);

        // THE DRAWN SET, taken now, on this thread: the viewport's collections belong to the thread that renders.
        var (drawn, info) = DrawnSource();
        if (drawn.Count == 0)
        {
            Status = "export: nothing is drawn, so there is nothing to export";
            Log.Add(NothingDrawnSentence(info));
            return null;
        }

        var items = ItemsFrom(drawn, out var spellings, out var meshes);
        var folderIsNew = !Directory.Exists(folder);

        _cancelling = false;
        Exporting = true;
        ExportFraction = 0;
        ExportLine = $"Exporting {drawn.Count:N0} placements to FBX…";
        Status = ExportLine;
        Log.Add($"export: {drawn.Count:N0} placements of {meshes:N0} meshes to {Path.Combine(folder, name + ".fbx")}");
        if (spellings != meshes)
            Log.Add($"export: note: {spellings:N0} spellings of the mesh paths folded into {meshes:N0} meshes " +
                    "(the viewport treats paths that differ only in case as one mesh)");
        Log.Add("export:" + ScopeSentence(info));
        foreach (var why in info.NotLoadableMeshes.Take(10)) Log.Add("export: not loadable in the viewport — " + why);

        // THE BAR NEVER STEPS BACK. The engine counts meshes first and adds the materials to the total once it has met
        // them, so Done/Total on its own falls back at that moment. The meshes are the first fifth of the bar (they were
        // 1.9 s of an 11 s export when measured) and the materials, which include the texture writing, the rest.
        var meshCount = -1;
        var sink = new Progress<(int Done, int Total, string What)>(p =>
        {
            if (meshCount < 0) meshCount = p.Total;
            ExportFraction = p.Total <= meshCount
                ? 0.2 * Math.Clamp((double) p.Done / Math.Max(1, p.Total), 0, 1)
                : 0.2 + 0.8 * Math.Clamp((double) (p.Done - meshCount) / Math.Max(1, p.Total - meshCount), 0, 1);
            if (!_cancelling) ExportLine = $"Exporting to FBX · {p.Done:N0} of {p.Total:N0} · {p.What}";
            progress?.Report(p);
        });

        try
        {
            var result = await Task.Run(() => LevelFbxExport.Export(
                provider, items, folder, name,
                choose: m => TextureCache.Choose(m) is { } c ? new LevelFbxExport.Textures(c.BaseColor, c.Normal, c.Params) : null,
                sink, cancel), cancel);

            // THE SUMMARY IS NOT THE EXPORT. The file is complete here, so a summary that throws (an unreadable
            // size, a full Log) must not be reported as a failed export: the catch below says "FAILED ... nothing of
            // it was left on disk", which would be a lie about a finished file.
            try { Summarise(result); }
            catch (Exception e)
            {
                Log.Add($"export: the file is complete, but its summary could not be written - {e.GetType().Name}: {e.Message}");
                Status = $"exported {result.Placements:N0} placements - {result.FbxPath}";
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            RemoveIfEmpty(folder, folderIsNew);
            Status = "export cancelled — nothing was left on disk";
            Log.Add("export: cancelled; the partly written file, its manifest, the 3ds Max script (if one had been written) and the textures this run wrote were removed");
            return null;
        }
        catch (Exception e)
        {
            RemoveIfEmpty(folder, folderIsNew);
            Status = "export failed: " + e.Message;
            Log.Add($"export: FAILED — {e.GetType().Name}: {e.Message}; nothing of it was left on disk");
            return null;
        }
        finally
        {
            Exporting = false;
            _cancelling = false;
            ExportLine = "";
        }
    }

    /// <summary>The engine removes the files it made; the folder it was given is ours to remove too, if we made it and it is empty.</summary>
    private static void RemoveIfEmpty(string folder, bool made)
    {
        try { if (made && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch { /* an empty folder left behind is harmless; the Log already says what happened */ }
    }

    /// <summary>Said by the Cancel button: the engine stops at the next mesh or material, which can be a moment on a big mesh.</summary>
    public void NoteCancelling()
    {
        if (!_exporting) return;
        _cancelling = true;
        ExportLine = "Cancelling… finishing the item in hand, then removing what was written";
        Status = "cancelling the export…";
    }

    /// <summary>One <see cref="LevelFbxExport.Item"/> per drawn placement. Stock values only: see the class note.</summary>
    private static List<LevelFbxExport.Item> ItemsFrom(IReadOnlyList<LevelReader.Placement> drawn,
                                                       out int spellings, out int meshes)
    {
        // ONE SPELLING PER MESH. The engine shares a mesh's geometry by the RAW path string, so a mesh named two ways
        // would be written twice. The viewport already treats paths that differ only in case as one mesh (its mesh
        // table is case-insensitive), so the first spelling seen stands for all of them.
        var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var raw = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<LevelFbxExport.Item>(drawn.Count);
        foreach (var p in drawn)
        {
            raw.Add(p.MeshPath);
            if (!first.TryGetValue(p.MeshPath, out var path)) first[p.MeshPath] = path = p.MeshPath;

            // The group is the name a tick box in the Maps panel carries: the sublevel of the MAP, which for something
            // inside a level instance is not the package it lives in (see Placement.RootSublevel).
            items.Add(new LevelFbxExport.Item(path, p.Model, p.Actor, p.HideKey, p.Materials));
        }
        spellings = raw.Count;
        meshes = first.Count;
        return items;
    }

    /// <summary>What the viewport's three switches and its loader left out of the scope, in one sentence.</summary>
    private static string ScopeSentence(Viewport.DrawnInfo i)
    {
        static string Are(int n) => n == 1 ? "1 placement is" : $"{n:N0} placements are";
        static string Have(int n) => n == 1 ? "1 placement has" : $"{n:N0} placements have";
        var parts = new List<string> { $" the scene holds {i.Scene:N0} placements" };
        if (i.Hidden > 0) parts.Add($"{Are(i.Hidden)} switched off (a sublevel unticked, the backdrop or the greybox off) and not exported");
        if (i.Spawned > 0) parts.Add($"{Are(i.Spawned)} a new actor you placed and have not built; the game does not have it, so it is not exported");
        if (i.StillLoading > 0) parts.Add($"{Are(i.StillLoading)} not on screen yet because the mesh is still loading, so not exported — export again when the viewport has finished");
        if (i.NotLoadable > 0) parts.Add($"{Are(i.NotLoadable)} not on screen because the mesh would not load, so not exported");
        if (i.Edited > 0) parts.Add($"{Have(i.Edited)} a mesh swap or removal in your changes (in the open project or not yet saved) and {(i.Edited == 1 ? "is" : "are")} exported as the game ships {(i.Edited == 1 ? "it" : "them")}");
        return string.Join("; ", parts);
    }

    private void Summarise(LevelFbxExport.Result r)
    {
        var textures = Path.Combine(Path.GetDirectoryName(r.FbxPath) ?? "", LevelFbxExport.TexturesFolder);
        long textureBytes = 0;
        try { textureBytes = Directory.Exists(textures) ? new DirectoryInfo(textures).EnumerateFiles().Sum(f => f.Length) : 0; }
        catch { /* the size is a courtesy; the export is done */ }

        var skeletal = r.SkeletalMeshes > 0
            ? $" ({r.SkeletalMeshes:N0} skeletal, written in their reference pose as plain geometry)" : "";
        Log.Add($"export: wrote {r.FbxPath} — {new FileInfo(r.FbxPath).Length / 1048576.0:N1} MB");
        Log.Add($"export: {r.Placements:N0} placements in {r.Sublevels:N0} sublevel group(s), {r.Meshes:N0} distinct meshes{skeletal}, " +
                $"{r.Materials:N0} materials, {r.TexturesWritten:N0} textures ({textureBytes / 1048576.0:N1} MB) in {r.Took.TotalSeconds:N1} s; " +
                $"process peak working set {r.PeakWorkingSetBytes / 1048576.0:N0} MB");
        Log.Add($"export: manifest {r.ManifestPath} — every material's textures by game parameter, plus its scalar and vector values");
        if (r.MaxScriptPath is { } script)
            Log.Add($"export: for 3ds Max, run {script} once after importing the FBX (Scripting > Run Script...): it switches on the cut-out of the {r.CutOutMaterials:N0} material(s) it lists, which Max's FBX importer cannot bring in by itself");
        Log.Add("export: this is the stock map: your changes (moves, swaps, removals and new actors, in the open project or not yet saved) are NOT in the file");

        if (r.PlacementsSkipped > 0)
            Log.Add($"export: {r.PlacementsSkipped:N0} placement(s) are not in the file (named below)");
        const int show = 40;
        foreach (var line in r.Missing.Take(show)) Log.Add("export: missing — " + line);
        if (r.Missing.Count > show)
            Log.Add($"export: …and {r.Missing.Count - show:N0} more missing items, all named in the manifest");

        Status = r.Missing.Count == 0
            ? $"exported {r.Placements:N0} placements in {r.Took.TotalSeconds:N1} s — {r.FbxPath}"
            : $"exported {r.Placements:N0} placements in {r.Took.TotalSeconds:N1} s with {r.Missing.Count:N0} missing item(s) (see the Log) — {r.FbxPath}";
    }
}

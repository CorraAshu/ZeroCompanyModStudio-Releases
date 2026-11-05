using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CUE4Parse.UE4.Assets.Exports;
using UEToolkit.Core;
// The windows this program shares with the Mod Studio (linked, never copied: see the csproj).
#if DEV
using Avalonia.VisualTree;
#endif
using AboutWindow = ZeroCompanyModStudioAvalonia.AboutWindow;
using GameAsk = ZeroCompanyModStudioAvalonia.GameAsk;
using GameBuildWindow = ZeroCompanyModStudioAvalonia.GameBuildWindow;
using GameFolderWindow = ZeroCompanyModStudioAvalonia.GameFolderWindow;

namespace ZeroCompanyLevelEditor;

public partial class MainWindow : Window
{
    private readonly Editor _editor = new();
    private SublevelEntry? _sublevel;

    /// <summary>The profile the start decided on (GameStart), or null to open without the game.</summary>
    private readonly string? _profilePath;

    /// <summary>Opens without the game. The XAML loader and the designer want a constructor with no arguments.</summary>
    public MainWindow() : this(null) { }

    public MainWindow(string? profilePath)
    {
        _profilePath = profilePath;
        InitializeComponent();
        DataContext = _editor;
        Gl.Attach(_editor);
        Closing += OnWindowClosing;
        // WHAT THE VIEWPORT IS DRAWING, for Project > Export what's loaded to FBX. Asked for on the UI thread, which is
        // where the viewport renders (see Viewport.DrawnPlacements).
        _editor.DrawnSource = () => (Gl.DrawnPlacements(out var drawnInfo), drawnInfo);
        _editor.DonorSettled += OnDonorSettled;

        // A frame that changed far more than the camera moved writes itself down, with the camera
        // delta beside it, so the evidence exists at the moment it happens rather than being
        // recalled afterwards.
        Gl.Flicked += line => Dispatcher.UIThread.Post(() => _editor.Log.Add("flicker: " + line));
        // A click in the viewport selects the same actor the tree would.
        Gl.Picked += index =>
        {
            if (index < 0 || index >= _editor.Placements.Count) { _editor.ClearActor(); Console.WriteLine("PICKED: nothing"); return; }
            var p = _editor.Placements[index];

            // A NEW ACTOR IS NOT ITS DONOR. A spawn preview borrows the donor's export to draw
            // with, so the ordinary route here would select the actor it was copied FROM -- and
            // then put a gizmo on it, whose drag would file a move against the wrong thing. It
            // gets its own row in Pending changes instead, and the gizmo stays away until a spawn
            // has an export of its own to select.
            if (p.Spawned is PendingSpawn made)
            {
                // THE GIZMO GOES ON IT, exactly as it would on an actor the level shipped. What
                // it drags is the spawn's own record rather than a property in a package, which
                // Editor.Nudge sorts out; from here a new actor and an old one are the same
                // thing to click on.
                _editor.ClearActor();
                _editor.SelectSpawn(made);
                Gl.Selected = index;
                _editor.Status = $"{made.Name} - a new actor; drag it, it is not in the game yet";
                Console.WriteLine($"PICKED #{index}: pending spawn {made.Name} at {made.Summary}");
                return;
            }

            _editor.SelectActor(p.Owner, p.Scope, p.Package, p.Sublevel);

            // THE PLACEMENT THAT WAS CLICKED, not the actor's first. An actor can draw several
            // meshes at several transforms and an instanced one draws hundreds; SelectActor can
            // only guess, and this is the one case where guessing is unnecessary.
            _editor.Explain(p);
            _editor.Status = $"picked {p.Actor} in {p.Sublevel}";
            Console.WriteLine($"PICKED #{index}: {p.Actor} ({p.Owner.ExportType}) in {p.Sublevel}, mesh {p.MeshPath[(p.MeshPath.LastIndexOf(char.Parse("/")) + 1)..]}");
        };

        // SELECTING SOMETHING GIVES THE TOP OF THE PANEL TO IT, and the browser keeps the rest.
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Editor.HasActor))
                Dispatcher.UIThread.Post(() => ShowDetail(_editor.HasActor));
        };

        // The live readout in the corner of the viewport. Four times a second is often enough to
        // see something cycling and cheap enough to leave on.
        var stats = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        stats.Tick += (_, _) =>
        {
            // The export entry waits for the viewport to finish loading and dressing. Only while the 3D tab is showing:
            // the render pass does the loading, so from another tab nothing is loading and a click shows the tab first.
            _editor.ViewportLoading = ReferenceEquals(Tabs.SelectedItem, ViewTab) && (Gl.Loading || Gl.Dressing);
            if (!ReferenceEquals(Tabs.SelectedItem, ViewTab)) { ViewStats.Text = ""; return; }

            // THE OVERRIDE MATERIALS THAT WOULD NOT BIND, once, into the Log where he can read
            // them. The overlay has room for a count and not for twelve reasons, and a count
            // alone cannot tell a missing material from one that throws.
            if (Gl.SkinFailures.Count > _skinFailuresLogged)
            {
                for (var i = _skinFailuresLogged; i < Gl.SkinFailures.Count; i++)
                    _editor.Log.Add("override material would not bind: " + Gl.SkinFailures[i]);
                _skinFailuresLogged = Gl.SkinFailures.Count;
            }
            // OFF UNLESS ASKED FOR (View > Show viewport statistics): the failures above still reach the Log, but the
            // block itself is a developer's readout and costs a string of this size four times a second.
            if (!_showStats) return;

            ViewStats.Text =
                $"drawn {Gl.LastDrawn:N0} of {_editor.Placements.Count:N0} placements\n" +
                $"meshes {Gl.MeshesReady:N0} ready · {Gl.MeshesQueued:N0} still loading · " +
                $"{Gl.MeshesKnown:N0} known\n" +
                $"hidden {Gl.LastHidden:N0}  ({_editor.BlockoutCount:N0} of it greybox)\n" +
                $"textures {Gl.MaterialsBound:N0} bound · {Gl.DressPending:N0} pending" +
                (Gl.DressFailed > 0 ? $" · {Gl.DressFailed:N0} FAILED" : "") + "\n" +
                // PER-PLACEMENT MATERIALS, ON SCREEN. The author turned Material on, looked, and
                // said "they all seemed the same" -- which is either the fix not working or him
                // not knowing where to look, and no amount of staring separates those two.
                // Whether a barrel SHOULD be black beside a blue one is a judgement only he can
                // make; whether anything was BOUND is a number, and it belongs here.
                $"overrides {_editor.OverriddenPlacements:N0} placement(s) · " +
                $"{Gl.SkinsBound:N0} material(s) bound" +
                (Gl.SkinsFailed > 0 ? $" · {Gl.SkinsFailed:N0} FAILED" : "") + "\n" +
                (Gl.Watching
                    ? $"flicker {Gl.LastChanged:0.#}% this frame · worst {Gl.WorstChanged:0.#}% " +
                      $"over {Gl.WorstMoved:0.#} cm" + "\n"
                    : "") +
                $"lights {Gl.LocalLit:N0} of {Gl.LocalKnown:N0} local · " +
                (Gl.Shadows ? $"shadows {Gl.ShadowDrawn:N0} casters, {Gl.ShadowMs:N0} ms" : "no shadows") +
                $"   ·   camera {Gl.Camera.Distance:N0} out" + "\n" +
                // NEAR AND FAR, ON SCREEN. "Something keeps flickering in front of the camera" is
                // a claim about one of these or about z-fighting, and neither can be told apart by
                // eye. The ratio is the number that matters: a depth buffer spread over 200,000:1
                // cannot separate two surfaces in the same place, and this level has 720 spots
                // where different actors put the same mesh.
                $"clip {Gl.Camera.NearPlane:0.##} .. {Gl.Camera.FarPlane:N0}  " +
                $"ratio {Gl.Camera.FarPlane / MathF.Max(Gl.Camera.NearPlane, 0.0001f):N0}:1";
        };
        stats.Start();

        // WHO MADE IT, in the menu bar, as the Mod Studio prints it: "<program> by <author>". Blank when the
        // build names no author, and then there is nothing to draw. A click on it opens About.
        CreditItem.Header = Branding.Author.Length == 0 ? "" : $"{Branding.Product} by {Branding.Author}";
        CreditItem.IsVisible = Branding.Author.Length > 0;

        // The level editor wants the material's own light; the other two apps do not, so this is asked for
        // here rather than defaulted on in shared code. IN THE RELEASE TOO: it used to be set by the driver's
        // view-state step (ApplyViewState, below), which is a developer flag handler and is compiled out of
        // a release -- and a release with this left at its default draws every emissive material dark.
        UEToolkit.App.Rendering.TextureCache.WantEmissive = true;

#if DEV
        var args = Environment.GetCommandLineArgs();

        // --showstats: start with View > Show viewport statistics ticked, so the block can be photographed on.
        if (args.Contains("--showstats")) SetShowStats(true);

        // --size <w> <h>: open at a given size, so a driven screenshot can be taken at the
        // resolution somebody actually runs at. The default 1680x1000 is a window that fits
        // anywhere; judging a layout at it, when the author works maximised on a 2560 screen,
        // reads crowding into a picture that is not the one they see.
        var size = Array.FindIndex(args, a => a == "--size");
        if (size >= 0 && size + 2 < args.Length &&
            int.TryParse(args[size + 1], out var wantW) && int.TryParse(args[size + 2], out var wantH))
        {
            Width = wantW; Height = wantH;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
#endif

        Opened += async (_, _) =>
        {
#if DEV
            // AFTER the template has applied: setting SelectedIndex in the constructor is silently
            // undone when the TabControl realises its items.
            var tab = System.Array.FindIndex(args, a => a == "--tab");
            if (tab >= 0 && tab + 1 < args.Length)
            {
                var want = args[tab + 1];
                if (int.TryParse(want, out var which)) Tabs.SelectedIndex = which;
                else
                {
                    // BY NAME, as `--tab` means in the toolkit and the Studio. An index was the
                    // only form here, and inserting the Assets tab in front of the viewport moved
                    // every one of those numbers by one -- which no harness would have reported,
                    // because a screenshot of the wrong tab is still a screenshot.
                    var hit = Tabs.Items.OfType<TabItem>().FirstOrDefault(
                        t => (t.Header as string ?? "").StartsWith(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is not null) Tabs.SelectedItem = hit;
                    else Console.WriteLine($"--tab {want}: no tab starts with that");
                }
            }
#endif
            // THE GAME THE START DECIDED ON (GameStart/GameSetup: the Mod Studio's remembered answer, or the
            // one the question window just got). Null is "open without the game".
            await _editor.MountAsync(_profilePath);

#if DEV
            // --about / --findgame: open those two windows for a photograph. They are menu entries, and a menu
            // cannot be driven without a mouse (its items expose nothing to UI Automation).
            if (args.Contains("--about")) OnAbout(null, new RoutedEventArgs());
            if (args.Contains("--findgame")) OnFindGame(null, new RoutedEventArgs());

            // --openmenu <header>: open a top-level menu, so it can be photographed open. Its entries are also
            // printed: a popup cannot always be photographed, and a list in text is the proof that an entry is
            // wired into the menu. NOT activated first - this program is run with ZCMS_BACKGROUND=1, which
            // forbids a window from taking the foreground, and a menu opens fine without it.
            var openMenu = Array.IndexOf(args, "--openmenu");
            if (openMenu >= 0 && openMenu + 1 < args.Length)
            {
                var wanted = args[openMenu + 1];
                var menu = this.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(
                    m => string.Equals((m.Header as string)?.Replace("_", ""), wanted,
                                       StringComparison.OrdinalIgnoreCase));
                if (menu is not null)
                {
                    Console.WriteLine($"menu {wanted}: " + string.Join(" | ",
                        menu.Items.OfType<MenuItem>().Select(m => m.Header as string ?? "?")));
                    await Task.Delay(400);
                    menu.IsSubMenuOpen = true;
                }
                else Console.WriteLine($"--openmenu {wanted}: no such menu");
            }

            await LightScan(args);
            await AutoDrive(args);
            await ToolbarBounds(args);
            foreach (var what in args.SkipWhile(a => a != "--collapse").Skip(1)
                                     .TakeWhile(a => !a.StartsWith("--")))
                switch (what)
                {
                    case "maps": OnToggleMaps(null, null!); break;
                    case "props": OnToggleProps(null, null!); break;
                    case "dock": OnToggleDock(null, null!); break;
                }
            Shot(args);

            // --quit ends the run once the driven steps are done. Without it a headless check
            // that wants no picture has to wait for --shot to finish dressing the whole level,
            // which on the hub is three minutes of loading nobody is going to look at.
            if (args.Contains("--quit")) Environment.Exit(0);
#endif
        };
    }

#if DEV
    /// <summary>
    /// `--why &lt;actor substring&gt; [count]`: the provenance readout, headless.
    ///
    /// The panel and this print THE SAME ROWS from the same call, which is the only reason this
    /// is worth having: a headless check that formats the answer itself proves the formatter
    /// works and nothing about the product. Three fixes in a row needed the author to confirm
    /// them because the harness could not see what the panel saw; this is that gap closed for
    /// the one readout built to settle arguments.
    /// </summary>
    private async Task Why(string[] args)
    {
        var at = Array.FindIndex(args, a => a == "--why");
        if (at < 0 || at + 1 >= args.Length) return;

        for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
        await Task.Delay(500);

        var want = args[at + 1];
        var many = at + 2 < args.Length && int.TryParse(args[at + 2], out var wn) ? wn : 3;

        // A MECHANISM, NOT A NAME. Asking for "the ones on a socket" is the question a check
        // actually has -- hunting for an actor name that happens to exercise the branch is how
        // three runs in a row came back "0 of 77,517" and proved nothing about anything.
        Func<LevelReader.Placement, bool> match = want switch
        {
            "?socket" => p => p.Component is not null &&
                              LevelExport.Provenance(p.Scope, p.Component).Any(s => s.Socket is not null),
            "?through" => p => p.Through is not null,
            // THE WITNESS FOR THE LEVEL-INSTANCE MATERIAL BUG. This was necessarily ZERO for two
            // days, because the re-emit rebuilt the record field by field and dropped Materials.
            // Keeping it as a selector rather than a one-off count is the difference between
            // proving the fix once and being able to prove it again.
            "?limat" => p => p.Through is not null && p.Materials is { Length: > 0 },
            "?material" => p => p.Materials is { Length: > 0 },
            "?instance" => p => p.Instance >= 0,
            "?archetype" => p => p.Component is not null &&
                                 LevelExport.Provenance(p.Scope, p.Component)
                                            .Any(s => s.LocFrom is not "own" and not "default"),
            _ => p => p.Actor.Contains(want, StringComparison.OrdinalIgnoreCase),
        };

        // HOW MANY MATCHED, not how many were printed. The first version counted the taken list
        // and reported "2 of 77,517" for a selector that matches thousands -- a number shaped
        // exactly like a measurement and carrying none, which is the fault this codebase has
        // now made three times. `Take` limits the READING, never the counting.
        var all = _editor.Placements.Select((p, i) => (p, i)).Where(x => match(x.p)).ToList();
        var hits = all.Take(many).ToList();

        Console.WriteLine($"WHY '{want}': {all.Count:N0} of {_editor.Placements.Count:N0} placement(s)" +
                          (all.Count > hits.Count ? $", showing {hits.Count}" : ""));
        foreach (var (p, i) in hits)
        {
            Console.WriteLine($"");
            Console.WriteLine($"  #{i}  {p.Actor}   <{p.Sublevel}>");
            Console.WriteLine($"      draws {p.MeshPath}");
            // THE WHOLE PICK, in the order the click makes it: SelectActor fills the panel and
            // Explain overwrites its guess with the placement actually asked about. Driving only
            // the second would leave the panel hidden -- ShowDetail follows HasActor -- so a
            // --shot of this run would photograph an empty inspector and prove nothing.
            if (p.Spawned is null) _editor.SelectActor(p.Owner, p.Scope, p.Package, p.Sublevel);
            _editor.Explain(p);
            foreach (var r in _editor.Why)
                Console.WriteLine($"      {r.Name,-16}  {r.Value}");
        }
        Console.WriteLine($"WHY   panel: HasActor={_editor.HasActor} HasWhy={_editor.HasWhy} " +
                          $"rows={_editor.Why.Count} actor='{_editor.ActorName}' " +
                          $"detailVisible={DetailBody.IsVisible}");

        if (hits.Count == 0)
        {
            // AND WHAT THE NAMES ACTUALLY LOOK LIKE. "nothing matched" on its own cannot tell a
            // wrong guess from a broken reader, and I burned three runs on exactly that.
            Console.WriteLine("      nothing matched - the name is the ACTOR's, as the browser lists it.");
            Console.WriteLine("      try ?socket, ?through, ?instance or ?archetype, or one of these:");
            foreach (var n in _editor.Placements.Select(p => p.Actor).Distinct(StringComparer.OrdinalIgnoreCase)
                                     .Take(8))
                Console.WriteLine($"         {n}");
        }
    }


    // ---------------------------------------------------------------- selection

    /// <summary>
    /// `--opensub &lt;map&gt; &lt;sublevel&gt;`: open ONE sublevel without opening its map first.
    ///
    /// His question: "is there no way to just render the sublevel only instead of the whole
    /// level?" There is, and it is the same click a person makes -- the sublevel rows are in the
    /// tree before anything is read, so the whole-map load can be skipped entirely. Driven here
    /// because --openmap loads the map first by design, which measures the wrong thing.
    /// </summary>
    private async Task<bool> OpenSublevelDirect(string mapId, string want)
    {
        var mapNode = Find(_editor.MapTree, n => n.Payload is MapEntry m && m.Id == mapId);
        if (mapNode is null) { Console.WriteLine($"OPENSUB no map called '{mapId}'"); return false; }

        // OPEN THE ARROW FIRST, which is what makes the real list appear: the folder listing a
        // map shows before anything is read is a guess, and the root is what actually knows.
        var guessed = mapNode.Children.Count;
        mapNode.IsExpanded = true;
        for (var waited = 0; mapNode.Detail == "reading…" && waited < 120_000; waited += 100)
            await Task.Delay(100);
        await Task.Delay(100);
        if (mapNode.Children.Count != guessed)
            Console.WriteLine($"OPENSUB the folder said {guessed} sublevel(s), " +
                              $"the root says {mapNode.Children.Count}");

        // EXACT FIRST. "HUB_Hangar" is a substring of "HUB_Hangar_Scaffolding", which sorts
        // earlier -- so a substring match opened a 543-placement sublevel and reported it as the
        // 8,883-placement one somebody asked for. Every number after that was about the wrong
        // level, and nothing said so.
        var subNode = Find(mapNode.Children,
                           n => n.Title.Equals(want, StringComparison.OrdinalIgnoreCase))
                   ?? Find(mapNode.Children,
                           n => n.Title.Contains(want, StringComparison.OrdinalIgnoreCase));
        if (subNode?.Payload is not SublevelEntry s)
        {
            Console.WriteLine($"OPENSUB '{want}' is not one of {mapNode.Children.Count} sublevel(s)");
            return false;
        }

        _drivenHeadless = true;
        LevelReader.PlanVariants((MapEntry) mapNode.Payload!, _editor.Index, null);
        _sublevel = s;
        await _editor.OpenSublevelAsync(s);
        return true;
    }
#endif

    private async void OnMapSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not Node node) return;
        switch (node.Payload)
        {
            case MapEntry m: await OpenMapAsked(node, m); break;
            case SublevelEntry s: _sublevel = s; _ = _editor.OpenSublevelAsync(s); break;
        }
    }

    /// <summary>
    /// Ask which version of the map to load, when it HAS versions, then open it.
    ///
    /// The groups come from the file index, so the dialog is up before anything is read and
    /// cancelling costs nothing. A map with no variants opens straight away -- a dialog with one
    /// row and no decision in it is a dialog nobody should be shown.
    /// </summary>
    private async Task OpenMapAsked(Node node, MapEntry m)
    {
        var groups = LevelReader.Variants(m, _editor.Index);

        // AND THE SETS OF WHOLE SUBLEVELS, which `Variants` cannot see: it groups level
        // instances sharing a spot, and the hub's Ring of Kafrene is three sublevels against the
        // nebula's one.
        // The ROOT decides what streams, and it is one package to read. The sets are filtered
        // against it so the dialog cannot offer a choice the map never loads.
        var streamedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_editor.Provider is { } prov)
                foreach (var pkg in LevelReader.StreamedPackages(LevelReader.Read(prov, m.RootPath)))
                    streamedNames.Add(LevelReader.Leaf(pkg));
        }
        catch { /* an unreadable root is handled by the open below; the sets just go unfiltered */ }

        var sets = LevelReader.SublevelSets(m, _editor.Index,
                                            streamedNames.Count > 0 ? streamedNames : null);

        if ((groups.Count > 0 || sets.Count > 0) && !_drivenHeadless)
        {
            var dialog = new VariantDialog(m.Id, groups, sets);
            await dialog.ShowDialog(this);
            if (dialog.Chosen is null) return;                 // cancelled: nothing is opened
            LevelReader.PlanVariants(m, _editor.Index, dialog.Chosen);

            // Through the same set the tick boxes use, so an excluded set releases its meshes and
            // pulls the far plane in rather than merely being skipped at draw time.
            _editor.HiddenSublevels.Clear();
            foreach (var name in dialog.HiddenSublevels ?? []) _editor.HiddenSublevels.Add(name);

            var dropped = dialog.HiddenSublevels?.Count ?? 0;
            _editor.VariantSummary = $"{groups.Count} area(s) have versions - " +
                                     $"{dialog.Chosen.Count} chosen" +
                                     (dropped > 0 ? $"; {dropped} sublevel(s) left out" : "");
        }
        else
        {
            LevelReader.PlanVariants(m, _editor.Index, null);  // decide by name
            _editor.VariantSummary = groups.Count > 0
                ? $"{groups.Count} area(s) have versions - chosen by name"
                : "";
        }
        _editor.OpenMap(node, m);
    }

    /// <summary>
    /// True when a command line is driving this window, so no dialog may block it.
    ///
    /// A harness that hangs on a modal nobody can click is not a failing test, it is a run that
    /// never finishes -- and two of those were left loading the hub earlier today.
    /// </summary>
#if DEV
    private bool _drivenHeadless;
#else
    // Nothing drives a release from a command line, so no dialog is ever suppressed.
    private const bool _drivenHeadless = false;
#endif

    private void OnActorSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not Node node) return;
        if (node.Payload is UObject export) _editor.SelectActor(export);
    }

    /// <summary>
    /// A row of the level browser was chosen.
    ///
    /// It carries its OWN sublevel and scope, so picking something from another part of the map
    /// than the tree has open still files a move against the right package -- and the camera is
    /// put on it, because finding a named thing in a level you cannot see is the whole point of
    /// having the list.
    /// </summary>
    private void OnBrowseSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not BrowseRow r) return;
        _editor.SelectBrowse(r);

        var index = _editor.PlacementIndexOf(r.Export);
        Gl.Selected = index;
        if (index >= 0)
        {
            Gl.FrameSelected();
            _editor.Status = $"{r.Name} in {r.Sublevel}";
        }
        else
            // Cover, spawners, volumes and objectives have nothing to draw. Saying so beats
            // framing empty space and letting somebody think the pick failed.
            _editor.Status = $"{r.Name} has no mesh to draw — its properties are on the right";
    }

    // ---------------------------------------------------------------- undo

    private void OnUndo(object? sender, RoutedEventArgs e) => Step(_editor.Undo());
    private void OnRedo(object? sender, RoutedEventArgs e) => Step(_editor.Redo());

    private void Step(string said)
    {
        _editor.Status = said;
        Gl.RequestNextFrameRendering();
    }

    /// <summary>
    /// Ctrl+Z and Ctrl+Y, bubbling ON PURPOSE.
    ///
    /// A text box gets the key first and undoes typing, which is what Ctrl+Z should do while the
    /// caret is in a filter field. Everywhere else it reaches here and undoes a move.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            switch (e.Key)
            {
                case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift): Step(_editor.Redo()); e.Handled = true; break;
                case Key.Z: Step(_editor.Undo()); e.Handled = true; break;
                case Key.Y: Step(_editor.Redo()); e.Handled = true; break;
            }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Opening the 3D tab gives it the keyboard.
    ///
    /// Otherwise the flying keys need a click nobody knows to make, and the first thing the
    /// viewport does is look broken. Guarded on sender, because a TabControl in the bottom dock
    /// raises this event too and must not steal focus from the panel somebody is reading.
    /// </summary>
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, Tabs)) return;
        if (ReferenceEquals(Tabs.SelectedItem, ViewTab)) Dispatcher.UIThread.Post(() => Gl.Focus());
    }

    /// <summary>
    /// Map &gt; hide the cinematic sets and the vista backdrops.
    ///
    /// The single most useful preset, because the alternative is ticking sixteen boxes: the hub
    /// streams 60 sublevels and 50,546 placements, of which 11 are cinematic sets for specific
    /// story beats and 5 are backdrops worth 25,621 placements between them.
    /// </summary>
    private void OnHideStaged(object? sender, RoutedEventArgs e)
    {
        var said = _editor.HideStagedSublevels(AllMapNodes());
        _editor.Status = said;
        _editor.Log.Add("sublevels: " + said);
    }

    private void OnShowAllSublevels(object? sender, RoutedEventArgs e)
    {
        var shown = 0;
        foreach (var node in AllMapNodes())
            foreach (var child in node.Children)
                if (child.Payload is SublevelEntry && !child.Shown) { child.Shown = true; shown++; }
        _editor.Status = shown == 0 ? "every sublevel was already shown" : $"showing {shown} again";
    }

    /// <summary>Every MAP node in the tree, whichever group it sits under.</summary>
    private IEnumerable<Node> AllMapNodes()
    {
        foreach (var group in _editor.MapTree)
        {
            if (group.Payload is MapEntry) yield return group;
            foreach (var child in group.Children)
                if (child.Payload is MapEntry) yield return child;
        }
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        if (_sublevel is not null) _ = _editor.OpenSublevelAsync(_sublevel);
    }

    private void OnTheme(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Header is string name) AppTheme.Apply(name);
    }

    // ---------------------------------------------------------------- the mod project
    //
    // Every edit lands in a mod project file first, and nothing here touches the game's own
    // containers -- the game folder is mounted read-only. Build and install then writes ONE thing:
    // a mod folder under the game's Mods, through the same Core calls the Studio uses, in the same
    // order. See EditorBuild for why that is a second real route rather than a bypass of
    // build-mods-through-the-studio.

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a mod project",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Mod project") { Patterns = new[] { "*.json" } },
            },
        });
        if (picked.Count > 0) _editor.Status = _editor.OpenProject(picked[0].Path.LocalPath);
    }

    private void OnSaveMoves(object? sender, RoutedEventArgs e) => _editor.Status = _editor.SaveMoves();

    private void OnTool(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Content: string name }) _editor.Tool = name;
    }

    /// <summary>
    /// One button that CYCLES, because three more buttons do not fit beside the tab strip and a
    /// label that names the current mode says more than three icons would anyway.
    /// </summary>
    private void OnShading(object? sender, RoutedEventArgs e) => _editor.Shading = _editor.Shading switch
    {
        "Solid" => "Material",
        "Material" => "Rendered",
        _ => "Solid",
    };

    private void OnShadingPick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Header: string name }) _editor.Shading = name;
    }

    private void OnExposure(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Gl.Exposure = (float) e.NewValue;
        _editor.ExposureText = $"{e.NewValue:0.0}";
        Gl.RequestNextFrameRendering();
    }

    private void OnShadows(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: { } on }) Gl.Shadows = on;
        Gl.RequestNextFrameRendering();
    }

    private void OnBlockout(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: { } on }) _editor.ShowBlockout = !on;
    }

    private void OnBackdrop(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: { } on }) _editor.ShowBackdrop = !on;
    }

    private void OnCameraSpeed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        _editor.CameraSpeed = e.NewValue;

    private void OnRevert(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PendingMove move }) _editor.Revert(move);
    }

    private void OnRevertAll(object? sender, RoutedEventArgs e)
    {
        // ONE STEP, not one per move: throwing away forty moves and needing forty undos to get
        // them back would make the button something nobody dares press.
        _editor.BeginBatch("revert all");
        foreach (var move in _editor.Changes.ToList()) _editor.Revert(move);
        foreach (var swap in _editor.Swaps.ToList()) _editor.RevertSwap(swap);
        _editor.EndBatch();
    }

    // ---------------------------------------------------------------- what it draws

    private void OnRevertSwap(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PendingSwap swap }) _editor.RevertSwap(swap);
    }

    // ---------------------------------------------------------------- the asset list

    /// <summary>
    /// WHERE A NEW ACTOR LANDS: whatever the viewport is looking at, in UE centimetres.
    ///
    /// The camera's Target is the point it orbits and the point it flies toward, so it is "here"
    /// in the only sense the author has. The axis swap is the same one RenderMesh.UeToGl makes on
    /// the way out -- UE Y and Z trade places -- and doing it here rather than deeper is
    /// deliberate: the editor has never known about the camera, and a placement written in GL
    /// space would be wrong in a way that looks like an art mistake.
    /// </summary>
    // Qualified, not `using System.Numerics`: this file already uses Avalonia's own Vector for
    // the DPI of a screenshot, and the two names collide.
    private System.Numerics.Vector3 CameraSpot()
    {
        var t = Gl.Camera.Target;
        return new System.Numerics.Vector3(t.X, t.Z, t.Y);
    }

    /// <summary>
    /// Set between picking an asset and its donor landing, so the arming happens once.
    /// </summary>
    private bool _arming;

#if DEV
    /// <summary>
    /// The tree node for one asset, with the list narrowed to it first.
    ///
    /// A driven run cannot click through four folders, and a TreeView will not select a node it
    /// has not realised -- so this does what a person does instead: type the name, then click the
    /// one result. Which also means the driven path exercises the filter, and a filter that
    /// stopped flattening would be caught here rather than in a screenshot.
    /// </summary>
    private AssetTreeNode? PickAsset(AssetRow row)
    {
        _editor.AssetFilter = row.Name;
        return _editor.AssetTree.FirstOrDefault(n => ReferenceEquals(n.Asset, row))
            ?? _editor.AssetTree.FirstOrDefault(n => n.Asset is not null);
    }
#endif

    /// <summary>
    /// Switch the asset list between what this level loads and everything in the game.
    ///
    /// Reads the button's own text rather than a two-way binding, for the reason the layer chips
    /// do: a RadioButton group writes IsChecked on the one going OFF as well as the one coming
    /// ON, so a TwoWay binding fires twice and the second one undoes the first.
    /// </summary>
    private void OnAssetScope(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Content: string what })
            _editor.WholeGame = what.StartsWith("Whole", StringComparison.OrdinalIgnoreCase);
    }

    private void OnAssetSelected(object? sender, SelectionChangedEventArgs e)
    {
        // ONLY ON A REAL PICK. Typing in the filter rebuilds the list, which drops the selection
        // and would otherwise close the panel he is reading mid-keystroke.
        //
        // AND ONLY ON AN ASSET. Clicking a folder opens it -- it is not a thing anybody could
        // place, and arming one would put whatever was last picked back in the scene.
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not AssetTreeNode { Asset: { } row }) return;
        _arming = true;
        _editor.SelectAsset(row);
        _editor.Status = $"{row.Name} - {row.Detail}";
    }

    /// <summary>
    /// PICKING AN ASSET PUTS IT IN THE SCENE, ON THE GIZMO.
    ///
    /// His words: "when someone selects a object from that list it should auto attach to the
    /// gizmo and let them move it around the scene without placing it, like unreal engine would".
    /// So a click is not "arm a button somewhere else" -- the thing appears where the camera is
    /// looking, selected, with Move armed, and the next thing he does is drag it.
    ///
    /// It only becomes HIS once he touches it. Clicking down the list swaps what the one on the
    /// gizmo is rather than leaving a trail of actors behind, which is the difference between
    /// browsing a list and authoring from one.
    /// </summary>
    private void OnDonorSettled()
    {
        if (!_arming) return;
        _arming = false;
        if (!_editor.CanPlace) return;
        _editor.Status = _editor.Place(CameraSpot(), keep: false);
        AttachGizmo();
    }

    private void OnPlaceAsset(object? sender, RoutedEventArgs e)
    {
        var said = _editor.Place(CameraSpot());
        _editor.Status = said;
        AttachGizmo();

    }

    /// <summary>
    /// HAND THE NEW ACTOR TO THE GIZMO, so it can be dragged the moment it appears.
    ///
    /// The point of placing from a list is not the button -- it is that the thing is now in the
    /// scene and you push it where you want it, the way any map editor works. Arriving selected
    /// with the Move tool armed is what makes that true; arriving as a row in a dock with three
    /// number boxes is not the same feature.
    /// </summary>
    private void AttachGizmo()
    {
        if (_editor.PickedSpawn is not { } spawn) return;

        var index = _editor.Placements.FindIndex(p => ReferenceEquals(p.Spawned, spawn));
        if (index < 0) return;
        Gl.Selected = index;
        _editor.Tool = "Move";
        ToolMove.IsChecked = true;

        if (!ReferenceEquals(Tabs.SelectedItem, ViewTab)) Tabs.SelectedItem = ViewTab;
        Dispatcher.UIThread.Post(() => Gl.Focus());
    }

    private void OnSpawnToCamera(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PendingSpawn spawn }) return;
        _editor.SelectSpawn(spawn);
        _editor.MoveSpawn(spawn, CameraSpot());
        AttachGizmo();
        _editor.Status = $"{spawn.Name} moved to {spawn.Summary}";
    }

    private void OnRevertSpawn(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PendingSpawn spawn }) _editor.RevertSpawn(spawn);
    }

    /// <summary>Map &gt; "Add actor" -- unfold the asset list rather than change tabs.</summary>
    /// <summary>
    /// The write half, in one press: save, build, read back, install.
    ///
    /// Everything it says goes to the Log tab, so the dock opens on it -- a build that reports
    /// only a one-line verdict is a build whose reasons are somewhere nobody found.
    /// </summary>
    private async void OnBuildAndInstall(object? sender, RoutedEventArgs e)
    {
        // THE LOG, OPEN, BEFORE IT STARTS. A build that reports only its one-line verdict is a
        // build whose reasons are somewhere nobody found -- and the reasons are the whole point
        // of reading the container back.
        if (!DockFull.IsVisible) OnToggleDock(null, null!);
        DockTabs.SelectedItem = LogTab;

        await _editor.BuildAndInstallAsync();
        Console.WriteLine("BUILD " + _editor.BuildVerdict);
    }

    // ---------------------------------------------------------------- export what is loaded to FBX
    //
    // The menu entry asks for a folder (a system dialog: the only part a driven run cannot exercise) and hands it to
    // RunExportFbx, which is everything after the dialog and is what the DEV driver `--exportfbx <folder>` calls. So the
    // driven run takes the same road a person's click does, minus the picker.

    /// <summary>The running export's cancel switch; null when none is running.</summary>
    private CancellationTokenSource? _exportCts;

    /// <summary>The running export, so closing the window can wait for its clean-up; null when none is running.</summary>
    private Task<LevelFbxExport.Result?>? _exportRun;

    // The window is closing and is waiting for the export's clean-up to finish.
    private bool _closingForExport;

    private async void OnExportFbx(object? sender, RoutedEventArgs e)
    {
        // AN ASYNC VOID that nothing awaits: whatever it throws would reach the dispatcher and end the program. The
        // folder picker is a system dialog and can fail in ways of its own, so the whole entry says what went wrong.
        try
        {
            if (!_editor.CanExport) return;

            // THE VIEWPORT LOADS ITS MESHES ONLY WHILE IT IS ON SCREEN (the render pass does the loading), so from another
            // tab "what's loaded" is nothing or a stale picture. Show it and say so, rather than export an empty scene.
            if (!ReferenceEquals(Tabs.SelectedItem, ViewTab))
            {
                Tabs.SelectedItem = ViewTab;
                _editor.Status = "the 3D viewport only loads the level while it is showing - it is showing now; let it finish loading, then export again";
                return;
            }

            // NOTHING DRAWN IS ASKED BEFORE THE PICKER, not after it: a person should not choose a folder for an export that
            // cannot happen.
            if (_editor.WhyNothingToExport() is { } why)
            {
                _editor.Status = why;
                _editor.Log.Add(why);
                return;
            }

#if DEV
            // --exportclick drives this very method WITHOUT the system folder dialog (a modal window on the real desktop).
            if (_devNoPicker)
            {
                _devPickerReached = true;
                Console.WriteLine("EXPORTCLICK the folder picker would open here (stubbed)");
                return;
            }
#endif
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Export what's loaded to FBX — choose a folder",
                AllowMultiple = false,
            });
            if (picked.Count > 0) await RunExportFbx(picked[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            _editor.Status = "export: could not start - " + ex.Message;
            _editor.Log.Add($"export: could not start - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Everything after the folder is chosen: open the Log (the summary goes there), make the export's own folder inside
    /// the chosen one, run it with the progress line and Cancel button showing, and say how it went.
    /// </summary>
    private Task<LevelFbxExport.Result?> RunExportFbx(string pickedFolder) => _exportRun = RunExportFbxCore(pickedFolder);

    private async Task<LevelFbxExport.Result?> RunExportFbxCore(string pickedFolder)
    {
        // THE LOG, OPEN, BEFORE IT STARTS -- the same rule as Build and install: the reasons are the point.
        if (!DockFull.IsVisible) OnToggleDock(null, null!);
        DockTabs.SelectedItem = LogTab;

        var folder = Editor.UniqueExportFolder(pickedFolder, _editor.ExportName, out var name);
        _exportCts = new CancellationTokenSource();
        try { return await _editor.ExportLoadedAsync(folder, null, _exportCts.Token, name); }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            _exportRun = null;
            // The summary is the last lines of the Log; the list does not follow its end by itself.
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Loaded);
        }
    }

    private void OnCancelExport(object? sender, RoutedEventArgs e) => CancelExport();

    /// <summary>Ask the running export to stop (and say so). False when none is running or it was already asked.</summary>
    private bool CancelExport()
    {
        if (_exportCts is not { IsCancellationRequested: false } cts) return false;
        cts.Cancel();
        _editor.NoteCancelling();
        return true;
    }

    /// <summary>
    /// CLOSING THE WINDOW MID-EXPORT cancels the export, and waits for it to take back what it wrote. The export runs on a
    /// worker; if the window simply closed, the process would end with the half-written <c>.fbx.part</c> and the textures
    /// written so far still on disk, which is the one thing Cancel promises never to leave. So the close is held, the
    /// export is cancelled exactly as the Cancel button does it, and the window closes once the clean-up is done.
    /// </summary>
    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingForExport) { e.Cancel = true; return; }          // a second close while waiting: keep waiting
        if (_exportRun is not { } run || !_editor.Exporting) return;

        e.Cancel = true;
        _closingForExport = true;
        try
        {
            CancelExport();
            try { await run; }
            catch { /* the export reports its own failure; the window is closing either way */ }
        }
        finally { _closingForExport = false; }
        Close();
    }

    private void OnGoToAssets(object? sender, RoutedEventArgs e)
    {
        if (!_assetsOpen) OnToggleAssets(null, null!);
        _editor.Status = "pick an asset on the right, then Place at the camera";
    }

    private void OnRemoveMesh(object? sender, RoutedEventArgs e) =>
        _editor.Status = _editor.SetDraws("");

    private void OnMeshPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string mesh) return;
        _editor.Status = _editor.SetDraws(mesh);

        // CLEAR THE SELECTION so the same mesh can be picked again after an undo. A ListBox will
        // not raise SelectionChanged for the row that is already selected, which would make the
        // second attempt look like a dead control.
        if (sender is ListBox list) list.SelectedIndex = -1;
    }

    // ---------------------------------------------------------------- collapsing
    //
    // The column widths are set directly rather than bound, ON PURPOSE: a GridSplitter writes the
    // ColumnDefinition's Width itself, and a binding there would be replaced by the first drag and
    // never recover. Remembering the width and putting it back is both simpler and drag-safe.

    private double _mapsWidth = 296, _propsWidth = 436, _dockHeight = 206;
    private const double Strip = 26;

    // How much of the properties panel the selected thing gets, remembered across selections so
    // a splitter drag is not undone by clicking on something else.
    // 1.15 was set when this section held a name and nine number boxes. It now also carries the
    // provenance chain, which for a socket-attached prop three components deep is four wrapped
    // rows -- and at 1.15 exactly one of them was visible without scrolling. The author drags the
    // splitter wherever he likes; this is only what it opens at.
    private double _detailShare = 1.9;

    /// <summary>
    /// FOLD ONE SECTION OF THE RIGHT-HAND COLUMN.
    ///
    /// Its height is remembered rather than reset, because a GridSplitter writes the row height
    /// itself: a section dragged to two thirds of the column and then folded should come back at
    /// two thirds, not at whatever the markup happened to say. A folded section takes its splitter
    /// with it, so two headers do not end up with a draggable gap between them that resizes
    /// nothing.
    /// </summary>
    private void Fold(int bodyRow, ref double share, TextBlock chevron, Control? splitter,
                      Control? body, bool show)
    {
        var row = PropsBody.RowDefinitions[bodyRow];
        if (!show && row.Height.IsStar && row.Height.Value > 0) share = row.Height.Value;
        row.Height = show ? new GridLength(share, GridUnitType.Star) : new GridLength(0);
        chevron.Text = show ? "▾" : "▸";
        if (splitter is not null) splitter.IsVisible = show;

        // A ZERO-HEIGHT ROW DOES NOT HIDE WHAT IS IN IT. Avalonia does not clip a Grid cell to
        // its own bounds, so the folded section kept drawing over the header below it -- his
        // words: "when you collapse these on the right they don't hide the search bar". The row
        // still goes to zero so the others get the height; this is what makes it actually gone,
        // and it stops the list measuring itself while nobody can see it.
        if (body is not null) body.IsVisible = show;
    }

    private double _assetShare = 1, _browseShare = 1;
    private bool _assetsOpen = true, _detailOpen, _browseOpen = true;

    /// <summary>
    /// The SELECTED section opens itself when something is picked, and takes its share back from
    /// whatever the other two had.
    /// </summary>
    private void ShowDetail(bool show)
    {
        _detailOpen = show;
        Fold(4, ref _detailShare, DetailChevron, BrowseSplitter, DetailBody, show);
    }

    private void OnToggleAssets(object? sender, PointerPressedEventArgs e)
    {
        _assetsOpen = !_assetsOpen;
        Fold(1, ref _assetShare, AssetsChevron, AssetsSplitter, AssetsBody, _assetsOpen);
    }

    private void OnToggleDetail(object? sender, PointerPressedEventArgs e)
    {
        // NOTHING SELECTED IS NOTHING TO OPEN. Folding an empty section down to a header is what
        // it already looks like, so the click would read as broken.
        if (!_editor.HasActor) { _editor.Status = "nothing is selected"; return; }
        ShowDetail(!_detailOpen);
    }

    private void OnToggleBrowse(object? sender, PointerPressedEventArgs e)
    {
        _browseOpen = !_browseOpen;
        Fold(7, ref _browseShare, BrowseChevron, null, BrowseBody, _browseOpen);
    }

    private void OnToggleMaps(object? sender, RoutedEventArgs e) => Collapse(
        MapsPanel, MapsStrip, MapsSplitter, BodyGrid.ColumnDefinitions[0], ref _mapsWidth, horizontal: true);

    private void OnToggleProps(object? sender, RoutedEventArgs e) => Collapse(
        PropsPanel, PropsStrip, PropsSplitter, BodyGrid.ColumnDefinitions[4], ref _propsWidth, horizontal: true);

    /// <summary>
    /// TURN THE FLICKER INTO A NUMBER WHILE HE FLIES.
    ///
    /// Three scripted cameras all behaved honestly -- the picture changed in proportion to the
    /// movement, and 0% when the camera held still -- so whatever he is seeing needs HIM at the
    /// controls. This puts the measurement where the reproduction is: fly until it happens, and
    /// the frame that does it writes itself into the log with the camera delta beside it.
    ///
    /// Off by default because it reads the whole framebuffer back every frame.
    /// </summary>
    /// <summary>
    /// SHOW THE PIECES THE GAME WOULD NOT HAVE LOADED.
    ///
    /// Two flags the package writes down and the viewport ignored until now: a LevelInstance with
    /// `bAutomaticallyLoad=false`, and an Actor with `bHidden=true`. All 30 instances in
    /// HUB_Hangar_Scaffolding are the first kind, and drawing them is why the author had
    /// "elevator support blocking the hangar entrance".
    ///
    /// A REREAD, not a redraw. The decision is taken while the placements are built, so flipping
    /// this has to re-open the sublevel; hiding at draw time would leave the counts, the asset
    /// list and any export disagreeing with the picture.
    /// </summary>
    /// <summary>View > Show viewport statistics: the counters in the viewport's corner. Off at every start.</summary>
    private bool _showStats;

    private void OnShowStats(object? sender, RoutedEventArgs e) => SetShowStats(!_showStats);

    private void SetShowStats(bool on)
    {
        _showStats = on;
        ShowStatsItem.Header = on ? "Show viewport statistics  \u2713" : "Show viewport statistics";
        ViewStats.IsVisible = on;
        if (!on) ViewStats.Text = "";
    }

    private void OnShowUnloaded(object? sender, RoutedEventArgs e)
    {
        LevelReader.ShowUnloaded = !LevelReader.ShowUnloaded;
        if (ShowUnloadedItem is not null)
            ShowUnloadedItem.Header = LevelReader.ShowUnloaded
                ? "Show what the game would not load  \u2713"
                : "Show what the game would not load";

        _editor.Log.Add(LevelReader.ShowUnloaded
            ? "game visibility: OFF. Showing everything the map contains, including pieces the " +
              "game loads only in particular story states."
            : "game visibility: ON. Showing what the game would load — the package's own " +
              "bAutomaticallyLoad and bHidden flags are honoured.");
        _editor.Status = LevelReader.ShowUnloaded
            ? "Showing everything in the map, not just what the game loads"
            : $"Game visibility on — last read skipped {LevelReader.SkippedNotAutoLoaded:N0} " +
              $"level instance(s) and {LevelReader.SkippedHidden:N0} hidden actor(s)";

        if (_sublevel is not null) _ = _editor.OpenSublevelAsync(_sublevel);
    }

    /// <summary>
    /// ONE STORY FACT SWITCHED, AND THE LEVEL READ AGAIN.
    ///
    /// The author's ask: "i would like to be able to load each variant whenever i want." So each
    /// fact the level is gated on is a switch, and flipping one re-evaluates the game's own
    /// conditions rather than guessing at a name.
    ///
    /// The checkbox has already written its own IsChecked by the time Click arrives, so the row
    /// is the truth and the editor is told to match it. Reading `sender` rather than a selected
    /// item matters: several rows are visible and only the clicked one changed.
    /// </summary>
    /// <summary>How many override-material failures have already reached the Log.</summary>
    private int _skinFailuresLogged;

    private async void OnFactToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: FactRow row }) return;
        await _editor.SetFactAsync(row.Tag, row.On);
    }

    private async void OnFactsNone(object? sender, RoutedEventArgs e) =>
        await _editor.SetAllFactsAsync(false);

    private async void OnFactsAll(object? sender, RoutedEventArgs e) =>
        await _editor.SetAllFactsAsync(true);

    private void OnWatchFlicker(object? sender, RoutedEventArgs e)
    {
        Gl.Watching = !Gl.Watching;
        if (Gl.Watching)
        {
            Gl.WatchReset();
            _editor.Log.Add("flicker: watching. Fly around; frames that change far more than the " +
                            "camera moved are written here.");
            _editor.Status = "Measuring flicker — fly around, and anything odd lands in the Log tab";
        }
        else
        {
            _editor.Log.Add($"flicker: stopped. Worst frame changed {Gl.WorstChanged:0.#}% " +
                            $"while the camera moved {Gl.WorstMoved:0.#} cm.");
            _editor.Status = $"Flicker watch off — worst was {Gl.WorstChanged:0.#}%";
        }
    }

    private void OnToggleDock(object? sender, RoutedEventArgs e) => Collapse(
        DockFull, DockStrip, DockSplitter, OuterGrid.RowDefinitions[2], ref _dockHeight, horizontal: false);

    private static void Collapse(Control full, Control strip, Control splitter, DefinitionBase def,
                                 ref double remembered, bool horizontal)
    {
        var collapsing = full.IsVisible;
        if (collapsing) remembered = horizontal ? ((ColumnDefinition)def).Width.Value
                                               : ((RowDefinition)def).Height.Value;

        var size = new GridLength(collapsing ? Strip : remembered, GridUnitType.Pixel);
        if (horizontal) ((ColumnDefinition)def).Width = size; else ((RowDefinition)def).Height = size;

        full.IsVisible = !collapsing;
        strip.IsVisible = collapsing;
        splitter.IsVisible = !collapsing;
    }

    // ---------------------------------------------------------------- About, and the game

    /// <summary>
    /// About > About Zero Company Level Editor…, and a click on the credit at the right of the menu bar: one
    /// window, two ways in. It is the Mod Studio's own window (linked). It is opened with NO settings, so the
    /// Studio's "look for a newer version" switch is not drawn here - this program never looks for one.
    /// </summary>
    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow(null, line => _editor.Log.Add(line))
        {
            // This program has no Handbook menu. The button opens the guides page, and the handbook there has a
            // section on this program.
            GuidesTooltip = "The guides, in your browser. The Complete Handbook has a section on the Level Editor.",
        };
        await about.ShowDialog(this);
    }

    /// <summary>About > Downloads and updates: the releases page, in the browser. Opening a page is the
    /// whole of it - nothing is asked of that page, and nothing is downloaded.</summary>
    private void OnOpenDownloads(object? sender, RoutedEventArgs e)
    {
        if (Branding.OpenLink(Branding.Home) is { } why)
            _editor.Log.Add($"could not open {Branding.Home}: {why}");
    }

    /// <summary>
    /// Project > Find the game again…: the same question the first start asks, with what is in use already
    /// chosen, for when the game moved, was never found, or the wrong install was picked - the Mod Studio's
    /// entry and window, and the SAME answer: it is saved where the Studio keeps it, so each program finds
    /// what the other was told.
    ///
    /// WHAT HAPPENS AFTER "Use this install" DEPENDS ON WHETHER A GAME IS MOUNTED, as in the Studio:
    ///   * NOTHING MOUNTED (opened without the game, or the mount failed): there is nothing to tear down, so
    ///     the new answer is checked and mounted straight away. This is the recovery path.
    ///   * A GAME MOUNTED: the window says plainly that the editor must be opened again. A live swap is not
    ///     safe - the index, the meshes in the viewport and the open map all hold the old install - and
    ///     quietly mixing two installs in one session is the wrong-values failure this tool exists to prevent.
    /// </summary>
    private async void OnFindGame(object? sender, RoutedEventArgs e)
    {
        try
        {
            var mounted = _editor.GameInUse;
            var setup = GameStart.Resolve();

            // What is in use: the mounted install, else whatever the settings say now. What is offered:
            // Steam's list, with the one in use first even if the list does not know it.
            var current = _editor.GameInstallInUse ?? (setup.Ready ? setup.Install : null);
            var offered = setup.NeedsAnswer ? setup.Candidates.ToList()
                                            : GameSetup.Installs(GameStart.Finder, out _);

            var window = new GameFolderWindow(new GameAsk(offered)
            {
                Selected = current,
                Gone = current is null ? setup.Gone : null,
                Problem = setup.Problem,
                Again = true,
                RestartAfter = mounted,
                Program = "the Level Editor",
            });
            await window.ShowDialog(this);

            if (window.Result is not { } answer) { _editor.Status = "game: left as it was."; return; }

            _editor.Log.Add(answer.Say);
            if (mounted)
            {
                var said = string.Equals(answer.Install, current, StringComparison.OrdinalIgnoreCase)
                    ? "game: that is the install already in use - nothing changed."
                    : "game: the new install is used the next time the Level Editor is opened.";
                _editor.Log.Add(said);
                _editor.Status = said;
                return;
            }

            // Nothing was mounted, so mount it now - checking the build first, as at the start.
            if (answer.ProfilePath is { } profile)
                await GameBuildWindow.RunAsync(profile, this, "the Level Editor");
            await _editor.MountAsync(answer.ProfilePath);
        }
        catch (Exception ex)
        {
            _editor.Log.Add($"game: could not change the install - {ex.Message}");
            _editor.Status = $"game: could not change the install - {ex.Message}";
        }
    }

#if DEV
    // ---------------------------------------------------------------- screenshots
    //
    // The same harness the concept used: Avalonia renders its own window to a PNG, so what gets
    // reviewed is what the toolkit actually drew rather than a description of it.

    /// <summary>
    /// --toolbarbounds: MEASURE the viewport toolbar instead of looking at it. For each of the two sliders
    /// (cam, light) it prints the slider's rectangle, its round thumb's rectangle and the toolbar row's
    /// rectangle (all in the row's coordinates), which ancestors of the thumb clip to their bounds, and how much of
    /// the thumb survives every one of those clips. A thumb "that looks a bit cut off" is a judgement; a visible
    /// share under 100% is a number. Runs once the layout has settled, prints and carries on (add --quit).
    /// </summary>
    private async Task ToolbarBounds(string[] args)
    {
        if (!args.Contains("--toolbarbounds")) return;
        await Task.Delay(2500);
        var row = SpeedSlider.GetVisualAncestors().OfType<WrapPanel>().First();
        Rect InRow(Visual v) => new((v.TranslatePoint(new Point(0, 0), row) ?? default), v.Bounds.Size);
        static string R(Rect r) => $"x {r.X:0.#} y {r.Y:0.#} w {r.Width:0.#} h {r.Height:0.#} (bottom {r.Bottom:0.#})";

        Console.WriteLine($"TOOLBAR window {Bounds.Width:0}x{Bounds.Height:0}; row {R(new Rect(row.Bounds.Size))}; {row.Children.Count} entries");
        var lines = new SortedDictionary<double, int>();
        foreach (var child in row.Children)
        {
            var r = InRow(child);
            lines[Math.Round(r.Y)] = lines.GetValueOrDefault(Math.Round(r.Y)) + 1;
            Console.WriteLine($"TOOLBAR   entry {child.GetType().Name,-11} {R(r)}");
        }
        Console.WriteLine($"TOOLBAR lines in the row (y: entries): {string.Join(", ", lines.Select(l => $"{l.Key:0}: {l.Value}"))}");

        foreach (var slider in new[] { SpeedSlider, ExposureSlider })
        {
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
            if (thumb is null) { Console.WriteLine($"TOOLBAR {slider.Name}: no thumb found"); continue; }
            var t = InRow(thumb);
            var visible = t;
            var clips = new List<string>();
            for (Visual? a = thumb.GetVisualParent(); a is not null && !ReferenceEquals(a, row.GetVisualParent()); a = a.GetVisualParent())
            {
                if (a is Control { ClipToBounds: true } c)
                {
                    var r = InRow(c);
                    visible = visible.Intersect(r);
                    clips.Add($"{c.GetType().Name}{(string.IsNullOrEmpty(c.Name) ? "" : "#" + c.Name)} {R(r)}");
                }
            }
            // The slider's own template, so a size that does not fit can be traced to the part that asked for it.
            if (args.Contains("--toolbartree"))
                foreach (var node in slider.GetVisualDescendants().OfType<Control>())
                {
                    var depth = node.GetVisualAncestors().TakeWhile(x => !ReferenceEquals(x, slider)).Count();
                    Console.WriteLine($"TOOLBAR {slider.Name}:   {new string(' ', depth * 2)}{node.GetType().Name}{(string.IsNullOrEmpty(node.Name) ? "" : "#" + node.Name)} " +
                                      $"{R(InRow(node))} margin {node.Margin} minH {node.MinHeight} H {node.Height} valign {node.VerticalAlignment} clip {node.ClipToBounds}" +
                                      (node is Grid g ? $" rows [{string.Join(", ", g.RowDefinitions.Select(r => $"{r.Height} = {r.ActualHeight:0.#}"))}]" : "") +
                                      (node.Parent is Grid ? $" row {Grid.GetRow(node)}" : ""));
                }
            var s0 = InRow(slider);
            var share = t.Width * t.Height <= 0 ? 0 : visible.Width * visible.Height / (t.Width * t.Height);
            Console.WriteLine($"TOOLBAR {slider.Name}: slider {R(s0)}");
            Console.WriteLine($"TOOLBAR {slider.Name}: thumb  {R(t)}");
            Console.WriteLine($"TOOLBAR {slider.Name}: thumb inside the slider's own rectangle: {s0.Contains(t.TopLeft) && s0.Contains(new Point(t.Right - 0.01, t.Bottom - 0.01))}");
            Console.WriteLine($"TOOLBAR {slider.Name}: clipping ancestors: {(clips.Count == 0 ? "none" : string.Join(" | ", clips))}");
            Console.WriteLine($"TOOLBAR {slider.Name}: thumb visible after every clip: {share:P1} ({visible.Width:0.#} x {visible.Height:0.#} of {t.Width:0.#} x {t.Height:0.#})");
        }
    }

    /// <summary>
    /// --lightscan &lt;map&gt; [&lt;map&gt; ...] -- read every sublevel's LIGHT RIG and print it, without
    /// loading a single mesh.
    ///
    /// WHY THIS EXISTS RATHER THAN --lights. `--lights` reports the rig of a map the viewport has
    /// opened, and opening the hub is eleven minutes of mesh loading to read thirty numbers. The
    /// rig lives in the exports; placements are what cost the time. Skipping them turns the
    /// measurement from a coffee break into seconds, which is the difference between measuring
    /// three maps before building and guessing from one.
    ///
    /// It also dumps the FULL property table of the first local light it meets. Every field this
    /// reader asks for by name -- IntensityUnits, bUseInverseSquaredFalloff -- is a name I might
    /// have wrong, and a wrong name silently returns the fallback. Printing what is actually on
    /// the component is how that gets caught rather than shaded with.
    /// </summary>
    private async Task LightScan(string[] args)
    {
        var at = Array.FindIndex(args, a => a == "--lightscan");
        if (at < 0) return;
        var maps = args.Skip(at + 1).TakeWhile(a => !a.StartsWith("--")).ToList();
        if (maps.Count == 0) return;
        if (_editor.Provider is not { } provider) { Console.WriteLine("LIGHTSCAN no provider"); return; }

        var dumped = false;
        foreach (var mapId in maps)
        {
            if (Find(_editor.MapTree, n => n.Payload is MapEntry me && me.Id == mapId)?.Payload is not MapEntry map)
            {
                Console.WriteLine($"LIGHTSCAN {mapId}: no such map. These exist:");
                foreach (var n in _editor.MapTree.SelectMany(g => g.Children.Prepend(g))
                                         .Where(n => n.Payload is MapEntry))
                    Console.WriteLine($"LIGHTSCAN     {((MapEntry) n.Payload!).Id}");
                continue;
            }

            var root = await Task.Run(() =>
            { try { return LevelReader.Read(provider, map.RootPath); } catch { return Array.Empty<UObject>(); } });
            var parts = new List<string> { map.RootPath };
            parts.AddRange((await Task.Run(() => AssetLoader.Read(() => LevelReader.StreamedPackages(root))))
                           .Select(LevelReader.ContainerOf));

            // THE SCAN DECIDES THE SAME WAY THE EDITOR DOES. Planning the variants here means the
            // hub's Day/Dawn/Night collapse in the scan exactly as they do on open, so a ten-second
            // run verifies the rule instead of an eleven-minute one.
            LevelReader.PlanVariants(map, _editor.Index, null);
            var read = new List<(string, LevelLights.Rig)>();
            Console.WriteLine($"LIGHTSCAN ===== {mapId}  ({parts.Count} sublevel(s)) =====");

            foreach (var container in parts)
            {
                var exports = container == map.RootPath
                    ? root
                    : await Task.Run(() =>
                      { try { return LevelReader.Read(provider, container); } catch { return Array.Empty<UObject>(); } });
                if (exports.Length == 0) continue;

                var rig = await Task.Run(() => AssetLoader.Read(() => LevelLights.Read(exports)));
                if (rig.Suns.Count + rig.Spots.Count == 0 && rig.Sky is null && rig.Fog is null) continue;

                Console.WriteLine($"LIGHTSCAN   {LevelReader.Leaf(container),-52} " +
                                  $"{rig.Suns.Count} sun  {rig.Spots.Count} local  " +
                                  $"{(rig.Sky is null ? "" : "sky ")}{(rig.Fog is null ? "" : "fog")}");
                read.Add((LevelReader.Bare(container), rig));

                if (!dumped && rig.Spots.Count > 0)
                {
                    dumped = true;
                    var one = exports.FirstOrDefault(e => e is not null && (
                        e.ExportType?.Contains("SpotLightComponent", StringComparison.Ordinal) == true ||
                        e.ExportType?.Contains("PointLightComponent", StringComparison.Ordinal) == true));
                    if (one is not null)
                    {
                        Console.WriteLine($"LIGHTSCAN   ---- every property on {one.Name} ({one.ExportType}) ----");
                        foreach (var q in one.Properties)
                            Console.WriteLine($"LIGHTSCAN     .{q.Name,-34} = {q.Tag?.GenericValue}");
                    }
                }
            }

            var notes = new List<string>();
            var final = LevelLights.Choose(read, LevelReader.Family, notes);
            foreach (var n in notes) Console.WriteLine("LIGHTSCAN   * " + n);
            foreach (var line in final.Describe()) Console.WriteLine("LIGHTSCAN   " + line);
            var all = final.Spots;

            // The distribution, not six examples: what matters for shading is whether these are
            // all one kind of light at one brightness or a spread, and six rows cannot say.
            Console.WriteLine($"LIGHTSCAN   {all.Count} local light(s): " +
                              $"{all.Count(x => x.IsPoint)} point, {all.Count(x => !x.IsPoint)} spot, " +
                              $"{all.Count(x => !x.InverseSquared)} with a falloff exponent");
            foreach (var g in all.GroupBy(x => x.Units).OrderByDescending(g => g.Count()))
                Console.WriteLine($"LIGHTSCAN     units {g.Key,-12} {g.Count(),4}  " +
                                  $"intensity {g.Min(x => x.Intensity):0.#}..{g.Max(x => x.Intensity):0.#}  " +
                                  $"radius {g.Min(x => x.Radius):0}..{g.Max(x => x.Radius):0}");
            foreach (var x in all.OrderByDescending(x => x.LuxAtOneMetre).Take(10))
                Console.WriteLine("LIGHTSCAN     " + x);
        }
    }

    /// <summary>
    /// EVERY VIEW SETTING, APPLIED BEFORE ANY DIAGNOSTIC RUNS.
    ///
    /// SIX TIMES an instrument has measured a state the run had not reached, because flags were
    /// handled in the order they appear in this method rather than the order they matter:
    ///   * --bare reported "0 meshes dressed" for a level that dresses 1,944;
    ///   * --lights reported "0 local lights", the level-lit path running only in Rendered;
    ///   * --shadowdebug painted the cascades AFTER --glshot had taken the picture, so the debug
    ///     render came back identical to the normal one and looked like the debug mode was broken.
    ///
    /// Moving one flag fixes one run. This is the rule instead: anything that changes WHAT IS
    /// DRAWN is applied here, once, before the map is opened. Flags that WAIT for something and
    /// then report it stay where they are -- --shading still does its own timing below, and
    /// --reshade deliberately runs after the level has been dressed once.
    /// </summary>
    private void ApplyViewState(string[] args)
    {
        // The level editor wants the material's own light; the other two apps do not, so this is
        // asked for here rather than defaulted on in shared code.
        UEToolkit.App.Rendering.TextureCache.WantEmissive = !args.Contains("--noemissive");

        var es = Array.FindIndex(args, a => a == "--emissivestrength");
        if (es >= 0 && es + 1 < args.Length && float.TryParse(args[es + 1], out var esv))
            Gl.EmissiveStrength = esv;

        // --nonormalmap: the other half of an A/B that had no switch.
        //
        // "Normal mapping moves 56.5% of the pixels" was measured by editing a static and
        // rebuilding, which is both unrepeatable and -- as it turned out -- taken through a
        // screenshot gate that was photographing half-dressed scenes. Every other branch on this
        // renderer has a flag (--noemissive, --noshadows, --trim); this one did not, so the claim
        // could not be re-checked without a code change. Now it can.
        if (args.Contains("--nonormalmap")) UEToolkit.App.Rendering.RenderMesh.NormalMapping = false;

        // --trimorder <0|1>: reverse the trim sheet's channel layering, for the same reason.
        var tord = Array.FindIndex(args, a => a == "--trimorder");
        if (tord >= 0 && tord + 1 < args.Length && int.TryParse(args[tord + 1], out var tov))
        {
            UEToolkit.App.Rendering.RenderMesh.TrimOrder = tov;
            Console.WriteLine($"TRIMORDER {(tov == 1 ? "B,G,R" : "R,G,B")}");
        }

        if (args.Contains("--noshadows")) Gl.Shadows = false;


        var cull = Array.FindIndex(args, a => a == "--shadowcull");
        if (cull >= 0 && cull + 1 < args.Length && int.TryParse(args[cull + 1], out var cv))
            Gl.ShadowCull = cv;

        if (args.Contains("--shadownocull")) Gl.CullCasters = false;

        var cdir = Array.FindIndex(args, a => a == "--cascadedir");
        if (cdir >= 0 && cdir + 1 < args.Length) Gl.CascadeDir = args[cdir + 1];

        var dbg = Array.FindIndex(args, a => a == "--shadowdebug");
        if (dbg >= 0 && dbg + 1 < args.Length && int.TryParse(args[dbg + 1], out var mode))
            Gl.ShadowDebug = mode;
    }

    private async Task AutoDrive(string[] args)
    {
        // VIEW STATE FIRST, FOR BOTH PATHS -- and the sixteenth ordering fault in this method was
        // the one that proved hoisting flags one at a time does not work.
        //
        // `ApplyViewState` used to be called only from `AutoDriveMap`, below the `--openmap` early
        // return. Every `--opensub` run therefore left `TextureCache.WantEmissive` at its default
        // of FALSE, so `BindEmissive` returned on its first line for every section of every
        // sublevel render this tool has ever taken. A whole material family's worth of work
        // measured 0 of 645 sections -- not because the binder was wrong, but because the switch
        // that enables it never flipped. `--noshadows`, `--shadowcull`, `--shadownocull`,
        // `--cascadedir` and `--shadowdebug` were dead on the same path for the same reason.
        //
        // Two earlier flags were fixed by moving THEM up here individually, each time with a
        // comment saying so. That is the wrong repair: it fixes one flag and leaves the trap. The
        // whole block runs for both paths now, and nothing in it touches the scene -- it is
        // flag-to-state only, which is exactly why it was safe up here all along.
        ApplyViewState(args);

        // SHADING FIRST, BEFORE ANYTHING OPENS A SCENE.
        //
        // Changing it tears the mesh set down and rebuilds it, so setting it AFTER --opensub
        // wiped a level that had just finished loading: `scene ready: 0 of 0 mesh(es)` after
        // five and a half minutes, in a run whose whole purpose was to check that materials
        // bind. Solid also skips dressing entirely, so the mode has to be right before the
        // scene exists or the measurement cannot come out true either way.
        //
        // Twelfth ordering fault in this method. The rule the others taught was "a flag that
        // needs no map goes above the early return"; this one adds "a flag that CHANGES how the
        // scene is built goes above the flag that builds it".
        var shading = Array.FindIndex(args, a => a == "--shading");
        if (shading >= 0 && shading + 1 < args.Length) _editor.Shading = args[shading + 1];

        // --trimuv <0|1>: force which UV set the RGB trim mask samples -- RenderMesh.TrimUvForce.
        //
        // UP HERE, not in ApplyViewState, which only the --openmap path calls: --opensub returns
        // from AutoDrive long before it. Put there first, the flag printed nothing and the shot
        // came back byte-identical, which reads exactly like "UV0 and UV1 are the same" -- the
        // wrong conclusion, from a flag that never ran. Fifteenth ordering fault in this method.
        var tdbg = Array.FindIndex(args, a => a == "--trimdebug");
        if (tdbg >= 0 && tdbg + 1 < args.Length && int.TryParse(args[tdbg + 1], out var tdv))
        {
            UEToolkit.App.Rendering.RenderMesh.TrimDebug = tdv;
            Console.WriteLine($"TRIMDEBUG {tdv}");
        }

        // --solo <meshSubstring>: draw only the placements of one mesh, and frame them.
        var solo = Array.FindIndex(args, a => a == "--solo");
        if (solo >= 0 && solo + 1 < args.Length && !args[solo + 1].StartsWith("--"))
        {
            Gl.SoloMesh = args[solo + 1];
            Console.WriteLine($"SOLO '{args[solo + 1]}'");
        }

        if (args.Contains("--trim"))
        {
            UEToolkit.App.Rendering.RenderMesh.TrimEnabled = true;
            Console.WriteLine("TRIM on: the RGB trim blend is UNFINISHED - see RenderMesh.TrimEnabled");
        }

        var tbf = Array.FindIndex(args, a => a == "--trimbase");
        if (tbf >= 0 && tbf + 1 < args.Length)
        {
            UEToolkit.App.Rendering.RenderMesh.TrimBaseForce = args[tbf + 1] == "tint" ? 1 : 0;
            Console.WriteLine($"TRIMBASE forced to {args[tbf + 1]}");
        }

        var tuv = Array.FindIndex(args, a => a == "--trimuv");
        if (tuv >= 0 && tuv + 1 < args.Length && int.TryParse(args[tuv + 1], out var tuvv))
        {
            UEToolkit.App.Rendering.RenderMesh.TrimUvForce = tuvv;
            Console.WriteLine($"TRIMUV forced to UV{tuvv}");
        }

        // --showunloaded: draw what the game would not load, for an A/B against the gating.
        //
        // Exists because a sublevel stalled after the visibility work landed and there was no way
        // to ask "is this mine?" without editing code. A switch that turns a feature off is the
        // cheapest bisect there is.
        if (args.Contains("--showunloaded"))
        {
            LevelReader.ShowUnloaded = true;
            Console.WriteLine("SHOWUNLOADED on: game visibility rules are NOT applied");
        }

        // --hideset <mapId> <folder>: exclude one sublevel SET exactly the way the dialog does.
        //
        // Exists to test the dialog's wiring without the dialog, which a driven run suppresses.
        // The author unticked Vista_RingOfKafrene and still saw its asteroids, and there was no
        // way to tell a broken picker from a stale build without this.
        var hideSetAt = Array.FindIndex(args, a => a == "--hideset");
        if (hideSetAt >= 0 && hideSetAt + 2 < args.Length)
        {
            var hideMapId = args[hideSetAt + 1];
            var folder = args[hideSetAt + 2];
            var hm = _editor.Maps.FirstOrDefault(m => m.Id.Equals(hideMapId, StringComparison.OrdinalIgnoreCase));
            if (hm is null) Console.WriteLine($"HIDESET no map called '{hideMapId}'");
            else
            {
                var streamedForSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (_editor.Provider is { } hp)
                        foreach (var pkg in LevelReader.StreamedPackages(LevelReader.Read(hp, hm.RootPath)))
                            streamedForSet.Add(LevelReader.Leaf(pkg));
                }
                catch { }

                var all = LevelReader.SublevelSets(hm, _editor.Index,
                                                   streamedForSet.Count > 0 ? streamedForSet : null);
                var chosenSet = all.FirstOrDefault(x => x.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
                if (chosenSet is null)
                    Console.WriteLine($"HIDESET '{folder}' is not one of {all.Count} set(s): " +
                                      string.Join(", ", all.Select(x => x.Folder)));
                else
                {
                    _editor.HiddenSublevels.Clear();
                    foreach (var n in chosenSet.Sublevels) _editor.HiddenSublevels.Add(n);
                    Console.WriteLine($"HIDESET {chosenSet.Label}: {chosenSet.Sublevels.Count} sublevel(s) " +
                                      $"marked hidden -> {string.Join(", ", chosenSet.Sublevels)}");
                }
            }
        }

        // --sets <mapId>: the sets of whole sublevels, from the index alone.
        //
        // ABOVE the early return on purpose: it needs no map loaded, and eleven flags have now
        // been written below that line and silently never run.
        var setsAt = Array.FindIndex(args, a => a == "--sets");
        if (setsAt >= 0 && setsAt + 1 < args.Length)
        {
            var id = args[setsAt + 1];
            var setMap = _editor.Maps.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (setMap is null) Console.WriteLine($"SETS no map called '{id}'");
            else
            {
                // AND WHETHER THE MAP ACTUALLY STREAMS THEM. The sets come from the file index,
                // which is a FOLDER listing and therefore a guess -- the hub's folder holds 15
                // sublevels while its root streams 60. Reading the root is one package, and it is
                // the only authoritative list. Without this the picker could offer a set the map
                // never loads, or miss one it does.
                var streamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var rootExports = LevelReader.Read(_editor.Provider!, setMap.RootPath);
                    foreach (var pkg in LevelReader.StreamedPackages(rootExports))
                        streamed.Add(LevelReader.Leaf(pkg));
                }
                catch (Exception ex) { Console.WriteLine($"SETS   could not read the root: {ex.Message}"); }

                var found = LevelReader.SublevelSets(setMap, _editor.Index,
                                                     streamed.Count > 0 ? streamed : null);
                Console.WriteLine($"SETS {id}: {found.Count} set(s) of whole sublevels; " +
                                  $"the root streams {streamed.Count:N0}");
                foreach (var one in found)
                {
                    var live = one.Sublevels.Count(n => streamed.Contains(n));
                    Console.WriteLine($"SETS   {one.Label,-40} {live,3} of {one.Sublevels.Count,3} streamed : " +
                                      string.Join(", ", one.Sublevels.Select(
                                          n => streamed.Contains(n) ? n : n + "(not streamed)")));
                }
            }
        }

        // --opensub <map> <sublevel>: the one click that skips the whole-map load.
        var sub = Array.FindIndex(args, a => a == "--opensub");
        if (sub >= 0 && sub + 2 < args.Length)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (await OpenSublevelDirect(args[sub + 1], args[sub + 2]))
            {
                for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250)
                    await Task.Delay(250);
                clock.Stop();
                Console.WriteLine($"OPENSUB {_sublevel?.Name ?? args[sub + 2]}: " +
                                  $"{_editor.Placements.Count:N0} placement(s), " +
                                  $"{_editor.Assets.Count:N0} asset(s), " +
                                  $"{clock.ElapsedMilliseconds:N0} ms");
                // WHAT THE GAME WOULD NOT HAVE LOADED, said out loud. Hiding part of a level
                // without saying so is the quiet lie this tool exists to avoid.
                Console.WriteLine($"OPENSUB   game visibility: skipped " +
                                  $"{LevelReader.SkippedNotAutoLoaded:N0} level instance(s) " +
                                  $"marked not-auto-load, {LevelReader.SkippedHidden:N0} hidden actor(s), " +
                                  $"{LevelReader.SkippedInvisible:N0} hidden component(s)");
                // AND WHAT A PREFAB'S OWN PIVOT MOVED. Fifty of the hub's level-instance
                // packages carry a LevelInstancePivotOffset, up to fifty metres, and we ignored
                // every one of them until 2026-09-17. Zero on a hub sublevel means the read is
                // wrong however right the picture looks.
                Console.WriteLine($"OPENSUB   level instances: {LevelReader.Pivoted:N0} placement(s) " +
                                  "moved by the prefab's own pivot offset");
                // PER-PLACEMENT MATERIALS, said out loud. The override support would look
                // identical to no override support if the placements carried none or the skins
                // never bound, and both are silent failures.
                var withMats = _editor.Placements.Count(p => p.Materials is { Length: > 0 });
                // AND HOW MANY OF THEM CAME THROUGH A LEVEL INSTANCE, which for two days was
                // necessarily ZERO: the re-emit rebuilt the record field by field and dropped
                // Materials. The hub's crew stations, LAAT bays and ring are all level
                // instances, so this number is most of what the override fix was FOR.
                var throughLi = _editor.Placements.Count(p => p.Materials is { Length: > 0 } && p.Through is not null);
                Console.WriteLine($"OPENSUB   override materials: {withMats:N0} placement(s) carry one " +
                                  $"({throughLi:N0} through a level instance), " +
                                  $"{Gl.SkinsBound:N0} material(s) bound, {Gl.SkinsFailed:N0} failed");
                Console.WriteLine($"OPENSUB   game facts: {LevelReader.Facts.Count:N0} set, " +
                                  $"{LevelReader.RulesSeen:N0} rule(s) read, " +
                                  $"{LevelReader.AllowedByFacts:N0} actor(s) admitted, " +
                                  $"{LevelReader.SkippedByFacts:N0} excluded" +
                                  (LevelReader.RulesUnreadable > 0
                                      ? $", {LevelReader.RulesUnreadable:N0} UNREADABLE" : ""));

                // AND WHETHER THE VIEWPORT TOOK IT. A sublevel opened one second after the mount
                // arrives before the GL control has drawn anything, and the editor holding 4,486
                // placements while the viewport holds none is the difference between "it loaded"
                // and "you can see it" -- which no count on the editor's side can tell apart.
                await Task.Delay(1500);
                Console.WriteLine($"OPENSUB   viewport: {Gl.MeshesKnown:N0} mesh(es) known, " +
                                  $"{Gl.MeshesReady:N0} ready, {Gl.LastDrawn:N0} drawn");

                // SOLO FRAMES ITSELF. Framing the whole sublevel puts the camera where the
                // AVERAGE of 10,534 placements is, which for one isolated mesh is nowhere near it.
                if (Gl.SoloMesh.Length > 0)
                {
                    var hits = _editor.Placements.Count(
                        p => p.Shown?.Contains(Gl.SoloMesh, StringComparison.OrdinalIgnoreCase) == true);
                    Console.WriteLine($"SOLO   {hits:N0} placement(s) draw it");
                    if (hits == 0)
                    {
                        // WHAT IS ACTUALLY HERE. "0 placements" alone cannot tell a wrong guess
                        // from a broken filter, and I have now spent two runs on exactly that
                        // ambiguity in this session -- once on --why and once here.
                        Console.WriteLine("SOLO   nothing matched; the most-drawn meshes here are:");
                        foreach (var g in _editor.Placements.Where(p => p.Shown is not null)
                                                  .GroupBy(p => LevelReader.Leaf(p.Shown!),
                                                           StringComparer.OrdinalIgnoreCase)
                                                  .OrderByDescending(g => g.Count()).Take(10))
                            Console.WriteLine($"SOLO     {g.Count(),6:N0}  {g.Key}");
                    }
                    if (hits > 0)
                    {
                        Gl.Selected = _editor.Placements.FindIndex(
                            p => p.Shown?.Contains(Gl.SoloMesh, StringComparison.OrdinalIgnoreCase) == true);
                        Gl.FrameSelected();
                        await Task.Delay(300);
                    }
                }

                // HERE TOO, because --opensub returns from AutoDrive long before the --openmap
                // path reaches it -- and one sublevel is the cheap way to ask this question.
                // Thirteenth ordering fault in this method, and the first one caught by looking
                // for it rather than by a run that came out empty.
                await Why(args);
            }
        }

        // --clip <near> <far>: force the clip planes, so a picture can be compared against the
        // same picture with different ones. Either number may be "auto" to leave that one alone.
        var clip = Array.FindIndex(args, a => a == "--clip");
        if (clip >= 0 && clip + 2 < args.Length)
        {
            Gl.Camera.NearOverride = float.TryParse(args[clip + 1], out var cn) ? cn : null;
            Gl.Camera.FarOverride = float.TryParse(args[clip + 2], out var cf) ? cf : null;
            Console.WriteLine($"CLIP forced near={args[clip + 1]} far={args[clip + 2]}");
        }

        // --camera <x> <y> <z> <distance> <yaw> <pitch>: put the camera somewhere exactly, in GL
        // space, so two runs can be compared. --fly raises real key events and cannot land twice
        // on the same spot, which is fine for testing input and useless for comparing pictures.
        var cam = Array.FindIndex(args, a => a == "--camera");
        if (cam >= 0 && cam + 6 < args.Length &&
            float.TryParse(args[cam + 1], out var cx) && float.TryParse(args[cam + 2], out var cy) &&
            float.TryParse(args[cam + 3], out var cz) && float.TryParse(args[cam + 4], out var cd) &&
            float.TryParse(args[cam + 5], out var cyaw) && float.TryParse(args[cam + 6], out var cpitch))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            Gl.Camera.Target = new System.Numerics.Vector3(cx, cy, cz);
            Gl.Camera.Distance = cd;
            Gl.Camera.Yaw = cyaw;
            Gl.Camera.Pitch = cpitch;
            // THE EYE, not the target. Target plus a distance plus two angles is where the eye
            // ENDS UP, and getting that wrong put the camera outside the level looking away from
            // it -- two frames of empty space, identical, which read as "the near plane makes no
            // difference". A comparison between two blank pictures agrees about nothing.
            var eye = Gl.Camera.Position;
            Console.WriteLine($"CAMERA target {cx:N0},{cy:N0},{cz:N0} distance {cd:N0} " +
                              $"yaw {cyaw:0.##} pitch {cpitch:0.##}  ->  " +
                              $"eye {eye.X:N0},{eye.Y:N0},{eye.Z:N0}  " +
                              $"near {Gl.Camera.NearPlane:0.##} far {Gl.Camera.FarPlane:N0} " +
                              $"ratio {Gl.Camera.FarPlane / MathF.Max(Gl.Camera.NearPlane, 0.0001f):N0}:1");
            // AND WHAT THAT VIEW ACTUALLY DRAWS. An empty picture from a camera standing in the
            // middle of eight thousand placements is either a cull that is wrong or a level that
            // is not where the numbers say -- and the drawn count is the one thing that separates
            // them. Looking at the picture cannot.
            for (var i = 0; i < 12; i++) { Gl.RequestNextFrameRendering(); await Task.Delay(120); }
            Console.WriteLine($"CAMERA   drew {Gl.LastDrawn:N0} of {_editor.Placements.Count:N0}, " +
                              $"hid {Gl.LastHidden:N0}, {Gl.MeshesReady:N0} of {Gl.MeshesKnown:N0} " +
                              $"mesh(es) ready");
        }

        // --dupes: count placements that draw THE SAME MESH AT THE SAME PLACE.
        //
        // "One second it looks normal and the next, if I move the camera, stuff just disappears,
        // and as you keep moving meshes keep rendering and de-rendering." Everything is loaded
        // (437 of 437) and the draw count never moves (8,882 of 8,883), so nothing is being culled
        // or streamed -- which leaves two surfaces fighting for the same pixels, and which one
        // wins is not stable under a camera move. That is exactly what he described once before:
        // "meshes keep disappearing and reappearing and i'm not even moving the camera", 369
        // actors in the hub emitting their mesh twice.
        //
        // Placements is DEDUPED on (owner, mesh, position rounded to 2dp), so a survivor here is a
        // pair that key does not catch -- two different owners, or positions that differ in the
        // third decimal. This counts them without rendering anything, which is the only way to
        // tell "they are fighting" from "you moved".
        if (args.Contains("--dupes"))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);

            var groups = _editor.Placements
                .Where(p => p.Spawned is null)
                .GroupBy(p => (p.MeshPath,
                               X: MathF.Round(p.At.X, 1), Y: MathF.Round(p.At.Y, 1),
                               Z: MathF.Round(p.At.Z, 1)))
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .ToList();

            var doubled = groups.Sum(g => g.Count() - 1);
            Console.WriteLine($"DUPES {_editor.Placements.Count:N0} placement(s), " +
                              $"{groups.Count:N0} spot(s) drawn more than once, " +
                              $"{doubled:N0} surplus draw(s)");
            // AND HOW DIFFERENT THEIR ORIENTATIONS ARE. Two panels at one origin facing opposite
            // ways are two real surfaces; two at the same orientation are one surface drawn twice.
            // Position alone cannot tell those apart, and the fix depends on which it is.
            foreach (var g in groups.Take(10))
            {
                var owners = g.Select(p => p.Actor).Distinct().Take(2).ToList();
                var first = g.First().Model;
                var spread = 0f;
                foreach (var p in g.Skip(1))
                {
                    var other = p.Model;
                    foreach (var (a, b) in new[]
                             { (other.M11, first.M11), (other.M12, first.M12), (other.M13, first.M13),
                               (other.M21, first.M21), (other.M22, first.M22), (other.M23, first.M23),
                               (other.M31, first.M31), (other.M32, first.M32), (other.M33, first.M33) })
                        spread = MathF.Max(spread, MathF.Abs(a - b));
                }
                Console.WriteLine($"DUPES   x{g.Count()}  {g.Key.MeshPath[(g.Key.MeshPath.LastIndexOf('/') + 1)..],-42} " +
                                  $"rot/scale differs by {spread:0.###}  " +
                                  $"owner(s) {string.Join(", ", owners)}");
            }
        }

        // --flicker <steps> <degrees>: turn the camera by a hair, repeatedly, and report how much
        // of the PICTURE changed each time.
        //
        // The complaint is that the picture changes more than the camera does, and every number in
        // this app so far counts what was submitted rather than what landed. A twentieth of a
        // degree at this range moves the image by about a pixel; anything past a few percent is
        // geometry appearing or vanishing. Run it before and after a candidate fix and the series
        // says whether anything actually changed.
        var fl = Array.FindIndex(args, a => a == "--flicker");
        if (fl >= 0 && fl + 2 < args.Length &&
            int.TryParse(args[fl + 1], out var sweepSteps) && float.TryParse(args[fl + 2], out var degs))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Loading && waited < 900_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Dressing && waited < 900_000; waited += 250) await Task.Delay(250);
            await Task.Delay(800);

            Gl.WatchReset();
            Gl.Watching = true;
            // AND OPTIONALLY FLY, because turning and moving are not the same test: a turn
            // changes what is in frame, a move changes what is in FRONT of what. He was flying
            // through the hangar, and it is the second that makes coincident surfaces swap.
            var forward = fl + 3 < args.Length && float.TryParse(args[fl + 3], out var cm) ? cm : 0f;

            var radians = degs * MathF.PI / 180f;
            for (var i = 0; i < sweepSteps; i++)
            {
                if (radians != 0) Gl.Camera.Yaw += radians;
                if (forward != 0) Gl.Camera.Move(forward, 0, 0);
                Gl.RequestNextFrameRendering();
                await Task.Delay(140);
            }
            Gl.Watching = false;

            var series = Gl.Changed;
            if (series.Count == 0) Console.WriteLine("FLICKER no frames were compared");
            else
            {
                Console.WriteLine($"FLICKER {series.Count} frame pair(s) at {degs:0.###} deg apart, " +
                                  $"{Gl.MeshesReady:N0} of {Gl.MeshesKnown:N0} mesh(es) ready");
                Console.WriteLine($"FLICKER   changed: min {series.Min():0.0}%  " +
                                  $"median {series.OrderBy(x => x).ElementAt(series.Count / 2):0.0}%  " +
                                  $"max {series.Max():0.0}%");
                Console.WriteLine("FLICKER   " + string.Join(" ", series.Select(x => $"{x:0.0}")));
            }
        }

        // --flickerscan <spots> <cm>: stand the camera in N places spread through the level, fly
        // the same short distance at each, and report where the picture changes MOST.
        //
        // Guessing cameras found a clean one three times running, which proves nothing about a
        // level of nine thousand placements. The scaling law from --flicker is the yardstick: at
        // 40cm inside clutter, about 10% of the picture changing is honest parallax, and it grows
        // smoothly with the step. A spot at three or four times that is not parallax, and this is
        // how to find one without being told where to stand.
        var fs = Array.FindIndex(args, a => a == "--flickerscan");
        if (fs >= 0 && fs + 2 < args.Length &&
            int.TryParse(args[fs + 1], out var spots) && float.TryParse(args[fs + 2], out var fsStep))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Loading && waited < 900_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Dressing && waited < 900_000; waited += 250) await Task.Delay(250);
            await Task.Delay(600);

            // SPREAD THROUGH THE PLACEMENTS, not through the bounding box: an even grid over a
            // hangar spends most of its samples in the air outside it.
            var all = _editor.Placements.Where(p => p.Spawned is null).ToList();
            var picked = new List<LevelReader.Placement>();
            if (all.Count > 0)
                for (var i = 0; i < spots; i++)
                    picked.Add(all[(int) ((long) i * all.Count / Math.Max(spots, 1))]);

            var worst = new List<(double Max, double Median, LevelReader.Placement At)>();
            foreach (var p in picked)
            {
                // Eye AT the placement, looking along +X, with an orbit distance large enough that
                // the near plane is the 143cm his overlay reported.
                var eye = new System.Numerics.Vector3(p.At.X, p.At.Z, p.At.Y);
                Gl.Camera.Distance = 28669f;
                Gl.Camera.Yaw = -1.5708f;
                Gl.Camera.Pitch = 0f;
                Gl.Camera.Target = eye + new System.Numerics.Vector3(Gl.Camera.Distance, 0, 0);

                Gl.WatchReset();
                Gl.Watching = true;
                for (var i = 0; i < 6; i++)
                {
                    Gl.Camera.Move(fsStep, 0, 0);
                    Gl.RequestNextFrameRendering();
                    await Task.Delay(130);
                }
                Gl.Watching = false;
                if (Gl.Changed.Count == 0) continue;
                worst.Add((Gl.Changed.Max(),
                           Gl.Changed.OrderBy(x => x).ElementAt(Gl.Changed.Count / 2), p));
            }

            Console.WriteLine($"FLICKERSCAN {worst.Count} spot(s) at {fsStep:0} cm/step, " +
                              $"{Gl.MeshesReady:N0} of {Gl.MeshesKnown:N0} mesh(es) ready");
            foreach (var (max, median, at) in worst.OrderByDescending(x => x.Max).Take(10))
                Console.WriteLine($"FLICKERSCAN   max {max,5:0.0}%  median {median,5:0.0}%  " +
                                  $"at {at.At.X:N0},{at.At.Y:N0},{at.At.Z:N0}  {at.Actor}");
        }

        // --fold <assets|browse|detail> ...: collapse sections of the right-hand column, so a
        // screenshot shows what a fold actually leaves behind. A zero-height Grid row does not
        // clip its contents, so a section can be "collapsed" and still be drawing over the one
        // under it -- which is exactly what happened, and what a verdict of "the row height is 0"
        // would have reported as working.
        var fold = Array.FindIndex(args, a => a == "--fold");
        if (fold >= 0)
        {
            for (var waited = 0; _editor.Busy && waited < 120_000; waited += 250) await Task.Delay(250);
            foreach (var what in args.Skip(fold + 1).TakeWhile(a => !a.StartsWith("--")))
                switch (what.ToLowerInvariant())
                {
                    case "assets": OnToggleAssets(null, null!); break;
                    case "browse": OnToggleBrowse(null, null!); break;
                    case "detail": OnToggleDetail(null, null!); break;
                }
            await Task.Delay(200);
            Console.WriteLine($"FOLD assets body visible={AssetsBody.IsVisible} " +
                              $"row={PropsBody.RowDefinitions[1].Height}");
            Console.WriteLine($"FOLD browse body visible={BrowseBody.IsVisible} " +
                              $"row={PropsBody.RowDefinitions[7].Height}");
            Console.WriteLine($"FOLD detail body visible={DetailBody.IsVisible} " +
                              $"row={PropsBody.RowDefinitions[4].Height}");
        }

        // --glshot <path> [seconds] photographs the 3D VIEWPORT, which --shot cannot: Avalonia's
        // RenderTargetBitmap never sees an OpenGlControlBase's output. This reads the GL
        // framebuffer instead, the way picking already did, and prints the frame's exposure.
        // THE MAP PATH FIRST, THEN THE PHOTOGRAPH. Both orderings matter and both were wrong
        // once: the scene has to exist before it can be measured, and --opensub (handled above)
        // must reach the camera flags as well, which is why the map half is its own method
        // rather than an early return in the middle of this one.
        await AutoDriveMap(args);
        await MemDriven(args);
        await ExportDriven(args, "--exportfbx");
        await ExportClick(args);
        await Glshot(args);
        MemReportNow("end");
    }

    // ---------------------------------------------------------------- the memory driver

    private System.Diagnostics.Stopwatch? _mapOpenedAt;
    private Task? _memA;

    /// <summary>
    /// The memory flags, in one place (all behind DEV, each with a needle in tools\stagecheck.ps1):
    ///
    ///   --memreport          follow the run: start the collection listener (Program.Main), wait for each map the driver opens to
    ///                        finish loading (read, meshes, materials) and print what the process, the collector and the GPU hold.
    ///   --thenopen &lt;map&gt;     once the first map has loaded and been reported, open THIS map in the same process -- the switch a
    ///                        person makes by clicking another map -- and report again.
    ///   --keeptextures       turn the release of a replaced map's textures OFF (Viewport.KeepTextures), so one binary measures both ways.
    ///   --forcegc            put CUE4Parse's forced full collection per Nanite mesh BACK (Program.Main turns it off), for the same reason.
    ///
    /// SAFETY: the process exits if its private bytes pass 17.5 GB.
    /// </summary>
    private async Task MemDriven(string[] args)
    {
        if (!args.Contains("--memreport") || _mapOpenedAt is null) return;

        var guard = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        guard.Tick += (_, _) =>
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            if (p.PrivateMemorySize64 <= 17_500_000_000L) return;
            Console.WriteLine($"MEM SAFETY: private bytes {p.PrivateMemorySize64 / 1073741824.0:N2} GB passed 17.5 GB; exiting");
            Environment.Exit(3);
        };
        guard.Start();
        Console.WriteLine($"MEM maps ({_editor.Maps.Count}): {string.Join(", ", _editor.Maps.Select(m => m.Id))}");

        await (_memA ?? WaitLoaded("A", _mapOpenedAt));
        MemReportNow("A loaded", true);

        var then = Array.FindIndex(args, a => a == "--thenopen");
        if (then < 0 || then + 1 >= args.Length) { Retained("A"); return; }
        var mapId = args[then + 1];
        if (Find(_editor.MapTree, n => n.Payload is MapEntry me && me.Id == mapId) is not { } node || node.Payload is not MapEntry map)
        {
            Console.WriteLine($"MEM --thenopen {mapId}: no such map");
            return;
        }
        LevelReader.PlanVariants(map, _editor.Index, null);
        Console.WriteLine($"MEM switching to {mapId}");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _editor.OpenMap(node, map);
        await WaitLoaded("B", clock);
        MemReportNow("B loaded (after the switch)", true);
        Retained("B");
    }

    /// <summary>
    /// What the managed heap really RETAINS: collect everything that can be collected and read the heap again. The
    /// "at the last GC" generation sizes in the other lines are the heap as of whenever the collector last ran, which
    /// after a load can be a long way from what is alive. Run AFTER the load it describes, never before the next one.
    /// </summary>
    private void Retained(string tag)
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        MemReportNow($"{tag} after a forced full collection (what is really kept)");
    }

    /// <summary>
    /// Wait until the map has been READ, its meshes are on the GPU and its materials are bound, pumping frames
    /// (a mesh is uploaded during a render), and say when each of the three happened, how the window behaved while
    /// it waited, and what the process held once a second.
    /// </summary>
    private async Task WaitLoaded(string tag, System.Diagnostics.Stopwatch since)
    {
        double? read = null, meshes = null, dressed = null;
        var gaps = new List<double>();
        var last = since.Elapsed.TotalMilliseconds;
        var tick = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        tick.Tick += (_, _) => { var now = since.Elapsed.TotalMilliseconds; gaps.Add(now - last); last = now; };
        tick.Start();
        var nextLine = 0.0; var stable = 0;
        var gc0 = MemReport.GcTotals();
        var pause0 = GC.GetTotalPauseDuration();
        var frames0 = Gl.RenderFrames;
        while (since.Elapsed.TotalSeconds < 1800)
        {
            Gl.RequestNextFrameRendering();
            await Task.Delay(100);
            var t = since.Elapsed.TotalMilliseconds;
            if (t >= nextLine)
            {
                nextLine = t + 5000;
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                var g = MemReport.GcTotals();
                var (tl, tb, _, _) = UEToolkit.App.Rendering.TextureCache.LiveTextures;
                var (bm, bv, bi, _, _) = UEToolkit.App.Rendering.RenderMesh.LiveBuffers;
                Console.WriteLine($"MEMTICK {tag} {t / 1000,6:N1}s  private {p.PrivateMemorySize64 / 1073741824.0,5:N2} GB  ws {p.WorkingSet64 / 1073741824.0,5:N2} GB  " +
                                  $"gen2 induced {g.InducedGen2 - gc0.InducedGen2:N0}  paused {(GC.GetTotalPauseDuration() - pause0).TotalSeconds:N1} s  " +
                                  $"textures {tl:N0} ({tb / 1048576.0:N0} MB)  buffers {bm:N0} ({(bv + bi) / 1048576.0:N0} MB)  " +
                                  $"meshes {Gl.MeshesReady:N0}/{Gl.MeshesKnown:N0}  busy {_editor.Busy} loading {Gl.Loading} dressing {Gl.Dressing}");
            }
            if (read is null && !_editor.Busy) read = t;
            var done = read is not null && Gl.MeshesKnown > 0 && !Gl.Loading && !Gl.Dressing && !_editor.Busy;
            if (done) { stable++; meshes ??= t; dressed ??= t; } else { stable = 0; dressed = null; }
            if (stable >= 15) break;                // 1.5 s of nothing left to do
        }
        tick.Stop();
        var wall = since.Elapsed.TotalSeconds;
        var gc1 = MemReport.GcTotals();
        Console.WriteLine($"MEMLOAD {tag}: map read {(read ?? -1) / 1000:N1} s, everything on the GPU and dressed {(dressed ?? -1) / 1000:N1} s (settled {wall:N1} s)");
        Console.WriteLine($"MEMLOAD {tag}: GC while loading: induced gen2 {gc1.InducedGen2 - gc0.InducedGen2:N0}, induced all {gc1.InducedAll - gc0.InducedAll:N0}, " +
                          $"paused by induced {(gc1.InducedPauseMs - gc0.InducedPauseMs) / 1000:N1} s of {(GC.GetTotalPauseDuration() - pause0).TotalSeconds:N1} s paused in all, " +
                          $"over a wall clock of {(dressed ?? since.Elapsed.TotalMilliseconds) / 1000:N1} s");
        var over = (double) gaps.Count(x => x > 200);
        Console.WriteLine($"MEMLOAD {tag}: window: {gaps.Count:N0} ticks of 50 ms, worst gap {(gaps.Count == 0 ? 0 : gaps.Max()):N0} ms, over 200 ms {over:N0}, over 1 s {gaps.Count(x => x > 1000):N0}, " +
                          $"time lost beyond the 50 ms {gaps.Where(x => x > 100).Sum(x => x - 50) / 1000:N1} s; {Gl.RenderFrames - frames0:N0} frames rendered");
    }

    private void MemReportNow(string tag, bool addressSpace = false)
    {
        if (!MemReport.Running) return;
        Console.WriteLine($"MEM[{tag}] {MemReport.Line()}");
        Console.WriteLine($"MEM[{tag}] GC {MemReport.Gc()}");
        foreach (var line in Gl.MemoryReport()) Console.WriteLine($"MEM[{tag}] {line}");
        if (addressSpace) foreach (var line in MemReport.AddressSpace()) Console.WriteLine($"MEM[{tag}] {line}");
    }

    // ---------------------------------------------------------------- the export driver

    private bool _devNoPicker, _devPickerReached;

    /// <summary>
    /// --exportclick: press Project &gt; Export what's loaded to FBX the way a click does (the entry's Click event, so
    /// <see cref="OnExportFbx"/> runs whole), with the system folder picker STUBBED OUT so nothing modal can open on the
    /// real desktop. Prints whether the entry is enabled, what the click said and whether it got as far as the picker.
    /// Runs after the map flags and the settling the export driver does.
    /// </summary>
    private async Task ExportClick(string[] args)
    {
        if (!args.Contains("--exportclick")) return;
        for (var waited = 0; _editor.Busy && waited < 600_000; waited += 250) await Task.Delay(250);
        if (args.Contains("--exportearly"))
        {
            // The click while the viewport is still loading: the entry must be greyed out with its reason, and enabled once done.
            for (var waited = 0; Gl.MeshesKnown == 0 && waited < 60_000; waited += 100) await Task.Delay(100);
            await Task.Delay(600);
            Console.WriteLine($"EXPORTCLICK early: loading={Gl.Loading} dressing={Gl.Dressing} meshes {Gl.MeshesReady:N0}/{Gl.MeshesKnown:N0}, " +
                              $"entry enabled={ExportFbxItem.IsEnabled}, CanExport={_editor.CanExport}, tip starts \"{_editor.ExportTip[..28]}\"");
            var seenGap = 0;
            for (var waited = 0; (Gl.Loading || Gl.Dressing || !ExportFbxItem.IsEnabled) && waited < 900_000; waited += 250)
            {
                if (ExportFbxItem.IsEnabled) seenGap++;
                await Task.Delay(250);
            }
            Console.WriteLine($"EXPORTCLICK early: settled, entry enabled={ExportFbxItem.IsEnabled} (enabled while still loading: {seenGap} sample(s)), tip starts \"{_editor.ExportTip[..28]}\"");
        }
        for (var waited = 0; Gl.Loading && waited < 900_000; waited += 250) await Task.Delay(250);
        await Task.Delay(400);
        for (var i = 0; i < 20; i++) { Gl.RequestNextFrameRendering(); await Task.Delay(80); }

        // --exportclick hideall: untick every sublevel first, so the map is open and NOTHING is drawn.
        if (args.SkipWhile(a => a != "--exportclick").Skip(1).FirstOrDefault() == "hideall")
        {
            foreach (var key in _editor.Placements.Select(pl => pl.HideKey).Distinct().ToList()) _editor.ShowSublevel(key, false);
            await Task.Delay(400);
            for (var i = 0; i < 20; i++) { Gl.RequestNextFrameRendering(); await Task.Delay(80); }
        }

        _devNoPicker = true;
        _devPickerReached = false;
        var logFrom = _editor.Log.Count;
        var drawn = Gl.DrawnPlacements(out _).Count;
        Console.WriteLine($"EXPORTCLICK before: entry enabled={ExportFbxItem.IsEnabled}, CanExport={_editor.CanExport}, " +
                          $"tab='{(Tabs.SelectedItem as TabItem)?.Header}', open='{_editor.ExportName}', busy={_editor.Busy}, drawn={drawn:N0}");
        ExportFbxItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(600);
        Console.WriteLine($"EXPORTCLICK after: picker reached={_devPickerReached}, tab='{(Tabs.SelectedItem as TabItem)?.Header}', exporting={_editor.Exporting}");
        Console.WriteLine($"EXPORTCLICK status: {_editor.Status}");
        foreach (var line in _editor.Log.Skip(logFrom)) Console.WriteLine($"EXPORTCLICK   LOG {line}");
        Console.WriteLine("EXPORTCLICK done");
    }

    /// <summary>
    /// The export flags, in one place (all behind DEV, each with a needle in tools\stagecheck.ps1):
    ///
    ///   --exportfbx &lt;folder&gt;     export what the viewport is drawing, the way the menu entry does after its folder picker
    ///                            (RunExportFbx), then print the result. Runs after --openmap / --opensub and every editing flag.
    ///   --exportbefore &lt;folder&gt;  the same, but RIGHT AFTER THE MAP HAS LOADED and before any editing flag acts: the "no edits"
    ///                            half of "edits do not change the file", taken in the SAME process as the edited one, so mesh
    ///                            conversion order cannot differ between the two.
    ///   --exportestimate         count the distinct meshes, materials and textures of the drawn set and estimate the time and
    ///                            the disk the export would take, BEFORE it runs (reads texture headers; writes nothing).
    ///   --exportcancel &lt;ms&gt;      press the progress line's Cancel button this long after the export starts.
    ///   --exportclose &lt;ms&gt; [raw]  close the WINDOW this long after the export starts: the close must be held until the export has
    ///                            taken back what it wrote, and the program then ends on its own. With "raw" the process is simply
    ///                            ended instead (the negative control: what a close did before the handler existed).
    ///   --exportclick [hideall]  press the menu entry itself (Click), the folder picker stubbed; says what the click did.
    ///                            "hideall" unticks every sublevel first, so the map is open and nothing is drawn.
    ///   --exportearly            with --exportfbx: start the export as soon as the map is read, while the viewport is still loading
    ///                            (the case the entry is greyed out for); with --exportclick: check the entry is disabled then.
    ///   --showblockout           switch the greybox ON before taking the drawn set (the toolbar's Blockout toggle).
    ///
    /// SAFETY: the driver cancels the export (through the same button) if the output folder passes 12 GB.
    /// </summary>
    private async Task ExportDriven(string[] args, string flag)
    {
        var at = Array.FindIndex(args, a => a == flag);
        var folder = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        var estimate = flag == "--exportfbx" && args.Contains("--exportestimate");
        if (folder is null && !estimate) return;
        var tag = flag == "--exportbefore" ? "EXPORTBEFORE" : "EXPORTFBX";

        var early = args.Contains("--exportearly");
        for (var waited = 0; _editor.Busy && waited < 600_000; waited += 250) await Task.Delay(250);
        if (early) { for (var waited = 0; Gl.MeshesKnown == 0 && waited < 60_000; waited += 100) await Task.Delay(100); Console.WriteLine($"{tag} EARLY: loading={Gl.Loading} dressing={Gl.Dressing} meshes {Gl.MeshesReady:N0}/{Gl.MeshesKnown:N0}"); }
        else {
        for (var waited = 0; Gl.Loading && waited < 900_000; waited += 250) await Task.Delay(250);
        await Task.Delay(400);
        }
        if (args.Contains("--showblockout") && !_editor.ShowBlockout) { _editor.ShowBlockout = true; await Task.Delay(400); }
        for (var i = 0; i < 20; i++) { Gl.RequestNextFrameRendering(); await Task.Delay(80); }

        // THE SCOPE, against what the frame itself reports. LastDrawn is the renderer's own count of what it submitted;
        // DrawnPlacements is the list the export is made of. In a scene with no new actors they must be equal.
        var drawn = Gl.DrawnPlacements(out var info);
        Console.WriteLine($"{tag} scene {info.Scene:N0} placements; the viewport drew {Gl.LastDrawn:N0} and hid {Gl.LastHidden:N0}; " +
                          $"DrawnPlacements {drawn.Count:N0} (hidden {info.Hidden:N0}, spawned {info.Spawned:N0}, loading {info.StillLoading:N0}, " +
                          $"not loadable {info.NotLoadable:N0}, edited-kept {info.Edited:N0})");
        foreach (var why in info.NotLoadableMeshes) Console.WriteLine($"{tag} not loadable: {why}");
        // The frame also draws the previews of new actors, and does not draw a placement whose unsaved edit removed it; the
        // list has the stock placement of the second and not the first (see Viewport.DrawnPlacements).
        var removed = drawn.Count(p => p.Draws is { Length: 0 });
        var expected = drawn.Count - removed + info.Spawned;
        Console.WriteLine($"{tag} drawn count vs list: frame {Gl.LastDrawn:N0}; list {drawn.Count:N0} - {removed:N0} removed + {info.Spawned:N0} preview(s) = {expected:N0}: " +
                          (Gl.LastDrawn == expected ? "EQUAL" : "DIFFERENT"));
        Console.WriteLine($"{tag} switches: backdrop {(_editor.ShowBackdrop ? "shown" : "HIDDEN")} (x{_editor.BackdropFactor:0.#}), " +
                          $"blockout {(_editor.ShowBlockout ? "SHOWN" : "hidden")}, sublevels hidden {_editor.HiddenSublevels.Count} " +
                          $"[{string.Join(", ", _editor.HiddenSublevels)}]");
        Console.WriteLine($"{tag} by sublevel group: " + string.Join("; ",
            drawn.GroupBy(p => p.HideKey, StringComparer.Ordinal).OrderByDescending(g => g.Count())
                 .Select(g => $"{g.Key} {g.Count():N0}")));
        Console.WriteLine($"{tag} Placement.Sublevel vs HideKey: " + string.Join("; ",
            drawn.Select(p => (p.Sublevel, p.HideKey)).Distinct().Take(8).Select(x => $"'{x.Sublevel}' / '{x.HideKey}'")));

        // ONE SPELLING PER MESH? The engine shares geometry by the raw path string.
        var raw = drawn.Select(p => p.MeshPath).Distinct(StringComparer.Ordinal).ToList();
        var folded = drawn.Select(p => p.MeshPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var objectForm = raw.Count(r => r.LastIndexOf('.') > r.LastIndexOf('/'));
        Console.WriteLine($"{tag} mesh paths: {raw.Count:N0} distinct as written, {folded:N0} ignoring case, " +
                          $"{objectForm:N0} written as package.object; sample {raw.FirstOrDefault()}");

        if (estimate) await ExportEstimate(drawn);
        if (folder is null) return;

        // THE RUN. A 50 ms tick measures whether the window stays alive; a 500 ms sampler shows the progress line moving.
        var gaps = new List<double>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastTick = clock.Elapsed.TotalMilliseconds;
        var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        tick.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; gaps.Add(now - lastTick); lastTick = now; };
        var lines = 0;
        var sampler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        sampler.Tick += (_, _) =>
        {
            if (!_editor.Exporting || lines++ > 40) return;
            Console.WriteLine($"{tag}   progress {_editor.ExportFraction * 100,5:0.0}%  strip visible={ExportStrip.IsVisible}  \"{_editor.ExportLine}\"");
        };
        var capped = false;
        var cap = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        cap.Tick += (_, _) =>
        {
            if (capped || !_editor.Exporting || !Directory.Exists(folder)) return;
            long size = 0;
            try { size = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            if (size > 12L * 1024 * 1024 * 1024)
            {
                capped = true;
                Console.WriteLine($"{tag} SAFETY: the output passed 12 GB ({size:N0} bytes); pressing Cancel");
                ExportCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
        // --exportclose <ms>: CLOSE THE WINDOW this long after the export starts (the title bar's close, i.e. Window.Close).
        // The program must wait for the export to take back what it wrote and then end on its own.
        var closeAt = Array.FindIndex(args, a => a == "--exportclose");
        if (flag == "--exportfbx" && closeAt >= 0 && closeAt + 1 < args.Length && int.TryParse(args[closeAt + 1], out var closeMs))
            _ = Task.Delay(closeMs).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                var rawEnd = closeAt + 2 < args.Length && args[closeAt + 2] == "raw";
                Console.WriteLine($"{tag} {(rawEnd ? "ENDING THE PROCESS (no close handler: what closing did before it existed)" : "closing the window")} after {closeMs:N0} ms " +
                                  $"(exporting={_editor.Exporting}, bar {_editor.ExportFraction * 100:0.0}%)");
                if (rawEnd) Environment.Exit(0);
                Close();
            }));

        var cancelAt = Array.FindIndex(args, a => a == "--exportcancel");
        if (flag == "--exportfbx" && cancelAt >= 0 && cancelAt + 1 < args.Length && int.TryParse(args[cancelAt + 1], out var cancelMs))
            _ = Task.Delay(cancelMs).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                Console.WriteLine($"{tag} pressing Cancel after {cancelMs:N0} ms (strip visible={ExportStrip.IsVisible}, bar {_editor.ExportFraction * 100:0.0}%)");
                ExportCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));

        var logFrom = _editor.Log.Count;
        Console.WriteLine($"{tag} started into {folder}");
        tick.Start(); sampler.Start(); cap.Start();
        var run = System.Diagnostics.Stopwatch.StartNew();
        var result = await RunExportFbx(folder);
        run.Stop();
        tick.Stop(); sampler.Stop(); cap.Stop();

        Console.WriteLine($"{tag} ui: {gaps.Count:N0} ticks of 50 ms in {run.Elapsed.TotalSeconds:N1} s, worst gap {(gaps.Count == 0 ? 0 : gaps.Max()):N0} ms, " +
                          $"over 200 ms: {gaps.Count(g => g > 200):N0}, over 1 s: {gaps.Count(g => g > 1000):N0}");
        foreach (var line in _editor.Log.Skip(logFrom)) Console.WriteLine($"{tag}   LOG {line}");
        Console.WriteLine($"{tag} status: {_editor.Status}");
        Console.WriteLine($"{tag} after: exporting={_editor.Exporting}, strip visible={ExportStrip.IsVisible}, log tab selected={ReferenceEquals(DockTabs.SelectedItem, LogTab)}");

        if (result is null)
        {
            var left = Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Select(f => f.FullName).ToList()
                : new List<string>();
            Console.WriteLine($"{tag} NO RESULT; left on disk under {folder}: {left.Count:N0} item(s)" + (left.Count > 0 ? " -> " + string.Join(" | ", left.Take(10)) : ""));
            Console.WriteLine($"{tag} done");
            return;
        }

        var dir = Path.GetDirectoryName(result.FbxPath)!;
        var tex = Path.Combine(dir, LevelFbxExport.TexturesFolder);
        var texFiles = Directory.Exists(tex) ? new DirectoryInfo(tex).GetFiles() : Array.Empty<FileInfo>();
        Console.WriteLine($"{tag} RESULT fbx={result.FbxPath}");
        Console.WriteLine($"{tag} RESULT placements={result.Placements:N0} meshes={result.Meshes:N0} (skeletal {result.SkeletalMeshes:N0}, placements {result.SkeletalPlacements:N0}) " +
                          $"materials={result.Materials:N0} textures={result.TexturesWritten:N0} sublevels={result.Sublevels:N0} skipped={result.PlacementsSkipped:N0} missing={result.Missing.Count:N0}");
        Console.WriteLine($"{tag} RESULT took={result.Took.TotalSeconds:N1}s peakWorkingSet={result.PeakWorkingSetBytes / 1048576.0:N0} MB (process-wide)");
        Console.WriteLine($"{tag} RESULT bytes: fbx {new FileInfo(result.FbxPath).Length:N0}, manifest {new FileInfo(result.ManifestPath).Length:N0}, " +
                          $"textures {texFiles.Length:N0} files {texFiles.Sum(f => f.Length):N0}");
        Console.WriteLine($"{tag} RESULT vs drawn: exported {result.Placements:N0} + skipped {result.PlacementsSkipped:N0} = {result.Placements + result.PlacementsSkipped:N0} of {drawn.Count:N0} drawn " +
                          ((result.Placements + result.PlacementsSkipped == drawn.Count) ? "(ALL ACCOUNTED FOR)" : "(DOES NOT ADD UP)"));
        foreach (var m in result.Missing) Console.WriteLine($"{tag} RESULT missing: {m}");
        Console.WriteLine($"{tag} done");
    }

    /// <summary>
    /// What the export WILL cost, from the real distinct counts, before it runs: meshes, materials (the meshes' own plus the
    /// placements' overrides) and the textures those materials name, with each texture's size from its header. Time uses the
    /// rate measured on a sublevel of the same map (34 meshes, 51 textures, 10.9 s: about 0.056 s a mesh and 0.18 s a texture);
    /// disk uses uncompressed RGBA as the UPPER bound (a PNG is smaller) and half of that as the usual figure.
    /// </summary>
    private async Task ExportEstimate(IReadOnlyList<LevelReader.Placement> drawn)
    {
        var provider = _editor.Provider!;
        var meshes = drawn.Select(p => p.MeshPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var materialPaths = Gl.LoadedMaterialPaths(drawn.Select(p => p.MeshPath)).Concat(drawn.SelectMany(p => p.Materials ?? Array.Empty<string?>()))
                              .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (textures, pixels, unread) = await Task.Run(() =>
        {
            var seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var unreadable = 0;
            foreach (var path in materialPaths)
                AssetLoader.Read(() =>
                {
                    try
                    {
                        var material = AssetLoader.Load<CUE4Parse.UE4.Assets.Exports.Material.UMaterialInterface>(provider, path);
                        if (material is null || UEToolkit.App.Rendering.TextureCache.Choose(material) is not { } c) return 0;
                        foreach (var t in c.Params.Textures.Values.OfType<CUE4Parse.UE4.Assets.Exports.Texture.UTexture>())
                        {
                            var key = t.GetPathName();
                            if (seen.ContainsKey(key)) continue;
                            long w = 0, h = 0;
                            if (t is CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D t2) { w = t2.ImportedSize.X; h = t2.ImportedSize.Y; }
                            if (w * h == 0 && t.PlatformData?.Mips is { Length: > 0 } mips) { w = mips[0].SizeX; h = mips[0].SizeY; }
                            seen[key] = w * h;
                        }
                    }
                    catch { unreadable++; }
                    return 0;
                });
            return (seen.Count, seen.Values.Sum(), unreadable);
        });
        var rgba = pixels * 4;
        var seconds = meshes * 0.056 + textures * 0.18;
        var worst = rgba + drawn.Count * 4096L + meshes * 262144L;
        Console.WriteLine($"EXPORTESTIMATE distinct: {meshes:N0} meshes, {materialPaths.Count:N0} materials ({unread:N0} unreadable), {textures:N0} textures " +
                          $"({pixels / 1e6:N1} megapixels in total), counted in {clock.Elapsed.TotalSeconds:N1} s");
        Console.WriteLine($"EXPORTESTIMATE time: about {seconds:N0} s (0.056 s a mesh + 0.18 s a texture, the rate measured on a sublevel of this map); " +
                          $"expect {seconds / 2:N0} to {seconds * 3:N0} s");
        Console.WriteLine($"EXPORTESTIMATE disk: textures at most {rgba / 1048576.0:N0} MB uncompressed, usually about {rgba / 2097152.0:N0} MB as PNG; " +
                          $"the FBX about {drawn.Count * 0.004 + meshes * 0.2:N0} MB (4 KB a placement, 0.2 MB a mesh)");
        Console.WriteLine($"EXPORTESTIMATE limit: the worst case {worst / 1073741824.0:N2} GB against the 20 GB ceiling -> " +
                          (worst < 20L * 1073741824 ? "UNDER, safe to run" : "OVER, DO NOT RUN"));
    }

    /// <summary>
    /// `--glshot &lt;path&gt; [settleSeconds]`: photograph the 3D viewport.
    ///
    /// CALLED FROM BOTH OPEN PATHS. It used to sit above the `--openmap` early return, which is
    /// right for `--opensub` -- that opens its sublevel higher up, so the scene exists by the
    /// time this runs -- and WRONG for `--openmap`, which opens the map BELOW that line. So a
    /// whole-map shot waited on a `Busy` that was still false, pumped 300 seconds at an empty
    /// scene and wrote a black frame: `0 of 0 mesh(es)`, mean luma 0.088. The counts said so and
    /// I read the picture first anyway.
    ///
    /// Fourteenth ordering fault in this method. The rules the others left were "a flag that
    /// needs no map goes above the early return" and "a flag that changes HOW the scene is built
    /// goes above the flag that builds it". This adds the third: A FLAG THAT MEASURES THE SCENE
    /// RUNS AFTER EVERY PATH THAT BUILDS ONE -- which means being called from each of them, not
    /// placed once and hoped over.
    /// </summary>
    private async Task Glshot(string[] args)
    {
        var gs = Array.FindIndex(args, a => a == "--glshot");
        if (gs >= 0 && gs + 1 < args.Length && !Gl.ShotTaken)
        {
            var settle = gs + 2 < args.Length && int.TryParse(args[gs + 2], out var secs) ? secs : 0;

            // THE VIEWPORT TAB, FIRST. An Avalonia control on an unselected tab is not in the
            // visual tree, so it never renders -- and every count this block prints comes out of
            // a render: meshes are queued by what a frame decides it can see, materials are bound
            // lazily by the draw loop, and the shot is the framebuffer.
            //
            // So without it the report is `0 of 0 mesh(es)` and `never drew a frame` on a level
            // that had just loaded 10,534 placements. I recorded that as a harness fault of
            // unknown cause and worked around it by asking HIM to confirm three fixes in the app.
            // It was a missing --tab, and one run that happened to pass hid it.
            //
            // Done here rather than left to the caller because a photograph of a tab that is not
            // the viewport is useless by definition, so there is no case where wanting --glshot
            // and not wanting this one is a thing somebody means.
            if (!ReferenceEquals(Tabs.SelectedItem, ViewTab))
            {
                Tabs.SelectedItem = ViewTab;
                Console.WriteLine("GLSHOT   selected the 3D viewport tab (it renders only when shown)");
                await Task.Delay(300);
            }

            for (var waited = 0; _editor.Busy && waited < 900_000; waited += 250) await Task.Delay(250);

            // PUMP FRAMES WHILE WAITING. A mesh is queued by what a RENDER decides it can see and
            // uploaded during a render, so waiting with Task.Delay renders nothing, queues
            // nothing, and `Loading` is still false when it is read. All three waits fell
            // straight through and the shot was taken on the first frame this method ever
            // pumped, with the scene still empty.
            //
            // Measured on a freshly opened HUB_Hangar: `queued 437, drawn 0 of 8,849`, a picture
            // of nothing, and eight seconds of settle that could not have helped because nothing
            // rendered during them. The instrument could not photograph a scene it had just
            // opened, which is the only kind a driver ever asks it to photograph.
            async Task Pump(Func<bool> done, int ms)
            {
                for (var waited = 0; !done() && waited < ms; waited += 100)
                { Gl.RequestNextFrameRendering(); await Task.Delay(100); }
            }

            // Done is "nothing left queued or dressing, and at least one mesh is known" -- NOT a
            // ready count. A mesh that fails to load leaves the queue without ever becoming
            // ready, so `Ready >= Known` would hang until the timeout on any level with one bad
            // asset, and time out is exactly when this gets used.
            await Pump(() => Gl.MeshesKnown > 0 && !Gl.Loading && !Gl.Dressing, 300_000);

            // AND THEN WAIT FOR THE PICTURE ITSELF TO STOP CHANGING.
            //
            // `MeshesKnown/Loading/Dressing` describe the ASSET pipeline. A placement is drawn when
            // a frame decides it can see it, and that runs on the render thread BEHIND the
            // pipeline, so all three read done while the frame still holds a fraction of the level.
            // Measured over eleven shots of ONE unchanged command, the background covered 0.9%,
            // 2.4%, 25%, 43%, 50% and -- once -- **100%** of the frame, every one of them printing
            // `scene ready: 439 of 439 mesh(es)` underneath. That 100% exposure was a photograph of
            // empty sky, and it was used as a measurement. A 30-second settle did not fix it:
            // two shots with the same settle came out at 2.4% and 25%.
            //
            // GATING ON `LastDrawn` DOES NOT WORK EITHER, and this file's own Compare() says why in
            // as many words -- "no placement is ever frustum-culled, so that number cannot see this
            // at all. It is a counter of intent." Tried anyway: two runs agreed to 3 pixels in
            // 725,582 and a third reported the identical 10,536 placements drawn over a frame that
            // was 41% empty sky. A placement counts as drawn before its mesh is on the GPU.
            //
            // So gate on the FRAMEBUFFER, which is the only thing that can witness a picture.
            // Compare() already reads it back and reports how much of it moved since the last
            // frame -- it was built for the flicker hunt, for exactly this reason. Wait until that
            // number stays under a twentieth of a percent for six consecutive frames.
            // A FRAME THAT HAS NOT STARTED IS NOT CHANGING EITHER, which is the trap a naive
            // stability gate walks into. Six quiet frames -- 600 ms -- accepted a shot that was
            // 50% empty sky, because the level streams in BURSTS and the gaps between them are
            // longer than the window. "Stopped changing" and "finished" are only the same
            // question if you wait longer than the pauses.
            //
            // So: a warm-up before quiet may be believed at all, and a quiet window wide enough
            // to span a gap. Still adaptive and still far cheaper than a blind settle -- the 30
            // second one did not work, and this converges in a few seconds when the scene is
            // genuinely done.
            const int quietNeeded = 25;   // 2.5 s of an unchanged picture
            const int warmUp      = 50;   // 5 s pumped before quiet counts for anything
            var wasWatching = Gl.Watching;
            Gl.Watching = true;
            Gl.WatchReset();
            var quiet = 0; var frames = 0;
            while (frames < 3_000 && (frames < warmUp || quiet < quietNeeded))
            {
                Gl.RequestNextFrameRendering();
                await Task.Delay(100);
                frames++;
                if (frames > 1 && Gl.LastChanged < 0.05) quiet++; else quiet = 0;
            }
            Gl.Watching = wasWatching;
            Console.WriteLine($"GLSHOT   frame settled: {Gl.LastChanged:0.###}% of the picture moved " +
                              $"in the last frame, quiet for {quiet * 100:N0} ms after {frames * 100:N0} ms" +
                              (quiet < quietNeeded
                                  ? "   [TIMED OUT STILL CHANGING -- do not compare this shot]" : ""));

            if (settle > 0) await Pump(() => false, settle * 1000);

            // SAY WHAT IS ABOUT TO BE PHOTOGRAPHED. An empty frame has been read as a result
            // three times in this project; a count beside the exposure is what makes that
            // impossible to miss.
            Console.WriteLine($"GLSHOT   scene ready: {Gl.MeshesReady:N0} of {Gl.MeshesKnown:N0} mesh(es)");
            // WHERE THE CAMERA IS, in the form --camera takes, so a close-up of what a framing found can be asked for next.
            Console.WriteLine($"GLSHOT   camera: --camera {Gl.Camera.Target.X:0.##} {Gl.Camera.Target.Y:0.##} {Gl.Camera.Target.Z:0.##} " +
                              $"{Gl.Camera.Distance:0.##} {Gl.Camera.Yaw:0.###} {Gl.Camera.Pitch:0.###}");
            // AFTER FRAMES HAVE BEEN PUMPED, which is the only point at which this can be true:
            // a placement's materials are bound lazily by the draw loop, so the count taken
            // before the first frame is always zero and says nothing.
            Console.WriteLine($"GLSHOT   override materials: {Gl.SkinsBound:N0} bound, " +
                              $"{Gl.SkinsFailed:N0} failed");

            // AND WHETHER THE PICTURE IS TEXTURED AT ALL. Every other number here is about work
            // attempted; a section with no diffuse draws white and reports nothing wrong, so a
            // level can be most of the way untextured with all of them reading full.
            var (textured, bare) = Gl.SectionTexturing();
            var total = textured + bare;
            Console.WriteLine($"GLSHOT   base colour: {textured:N0} of {total:N0} section(s) textured" +
                              (bare > 0 ? $", {bare:N0} draw WHITE ({100.0 * bare / Math.Max(1, total):0.#}%)" : ""));
            Console.WriteLine($"GLSHOT   second UV set: {UEToolkit.App.Rendering.RenderMesh.MeshesWithUv1:N0} " +
                              $"mesh(es) arrived with one, " +
                              $"{UEToolkit.App.Rendering.RenderMesh.MeshesWithoutUv1:N0} without");
            var fams = Gl.SectionFamilies();
            var famTotal = fams.Sum(f => f.Sections);
            Console.WriteLine($"GLSHOT   material families ON SCREEN ({famTotal:N0} section(s)):");
            foreach (var (fam, n) in fams.Take(8))
                Console.WriteLine($"GLSHOT     {n,6:N0}  {100.0 * n / Math.Max(1, famTotal),5:F1}%  {fam}");

            var (withN, withD, withUv, withEm, withPr, withPrN, withPrA, withFz, nTotal) = Gl.SectionNormals();
            // MM_Rocks, counted for the same reason everything else here is: a projection that
            // binds nothing and a level that simply has no rocks in it look identical otherwise.
            Console.WriteLine($"GLSHOT   MM_Rocks branches: {withPr:N0} section(s) projected " +
                              $"({withPrN:N0} with a projected normal, {withPrA:N0} with a break-up mask), " +
                              $"{withFz:N0} fuzzed");
            Console.WriteLine($"GLSHOT   MM_Tiled_RGB branches: {withUv:N0} section(s) UV-tinted, " +
                              $"{withEm:N0} carry an emissive sheet");
            Console.WriteLine($"GLSHOT   normal maps: {withN:N0} of {nTotal:N0} section(s) carry one, " +
                              $"{withD:N0} also carry a DETAIL normal" +
                              (UEToolkit.App.Rendering.RenderMesh.NormalMapping ? "" : "   [normal mapping OFF]"));
            var (emMapped, emFlat, emTinted, emScaled, emTotal) = Gl.SectionEmissive();
            Console.WriteLine($"GLSHOT   MM_Emissive branches: {emMapped:N0} of {emTotal:N0} section(s) " +
                              $"glow from a MAP, {emFlat:N0} glow a FLAT authored colour; " +
                              $"{emTinted:N0} carry a colour that is not white, " +
                              $"{emScaled:N0} an authored strength that is not the family's modal 6");

            foreach (var row in Gl.EmissiveResolved(24))
                Console.WriteLine($"GLSHOT     EM {row}");

            var (dMasked, dRescued, dSolid, dTotal) = Gl.SectionDecalMask();
            Console.WriteLine($"GLSHOT   MM_Decals shape: {dMasked:N0} of {dTotal:N0} decal section(s) " +
                              $"cut out by a channel mask ({dRescued:N0} of them had NO diffuse and " +
                              $"would have drawn as a full rectangle); {dSolid:N0} still have neither");

            var (trimmed, replacing, uv1) = Gl.SectionTrim();
            if (trimmed > 0)
                Console.WriteLine($"GLSHOT   RGB trim: {trimmed:N0} section(s) wear a sheet, " +
                                  $"{replacing:N0} REPLACE their diffuse, {uv1:N0} sample it on UV1");
            else
                Console.WriteLine("GLSHOT   RGB trim: no section wears a sheet");

            foreach (var line in Gl.BoundTextures.Where(l => l.Contains("TRIM ")).Take(4))
                Console.WriteLine($"GLSHOT   {line.Trim()}");

            foreach (var line in Gl.BoundTextures.Where(l => l.Contains("unmatched params") ||
                                                     l.Contains("NO textures at all")).Take(5))
                Console.WriteLine($"GLSHOT   {line.Trim()}");

            Gl.RequestShot(args[gs + 1]);
            await Pump(() => Gl.ShotTaken, 60_000);
            if (!Gl.ShotTaken) Console.WriteLine("GLSHOT never drew a frame");

            // --camshots <dir> <name=x,y,z,distance,yaw,pitch | name=@px,py,distance>[;...]: AFTER that photograph, move the camera and
            // photograph the SAME loaded scene again, one <name>.png per camera (the numbers are --camera's, in GL space). A close-up of a
            // 4096 texture needs the whole sublevel loaded, which costs a minute: this takes every close-up of one run for that one load.
            // "@px,py,distance" PICKS the object under that pixel of the first photograph's view and puts the camera `distance` from its
            // pivot, looking the way the first view did; the line it prints is a --camera, so the same close-up can be asked for again.
            var cs = Array.IndexOf(args, "--camshots");
            if (cs >= 0 && cs + 2 < args.Length && Gl.ShotTaken)
            {
                System.IO.Directory.CreateDirectory(args[cs + 1]);
                var (homeTarget, homeDistance, homeYaw, homePitch) = (Gl.Camera.Target, Gl.Camera.Distance, Gl.Camera.Yaw, Gl.Camera.Pitch);
                foreach (var one in args[cs + 2].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = one.IndexOf('=');
                    var picked = eq > 0 && one[eq + 1] == '@';
                    var n = one[(eq + 1 + (picked ? 1 : 0))..].Split(',').Select(t => float.Parse(t.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    if (eq < 1 || n.Length != (picked ? 3 : 6)) { Console.WriteLine($"CAMSHOT bad camera '{one}'"); continue; }
                    if (picked)
                    {
                        Gl.Camera.Target = homeTarget; Gl.Camera.Distance = homeDistance; Gl.Camera.Yaw = homeYaw; Gl.Camera.Pitch = homePitch;
                        await Pump(() => false, 600);
                        Gl.RequestPick(new Avalonia.Point(n[0], n[1]));
                        await Pump(() => false, 1500);
                        if (Gl.SelectedPivot is not { } pivot) { Console.WriteLine($"CAMSHOT {one[..eq]}: nothing under pixel {n[0]},{n[1]}"); continue; }
                        Gl.Selected = -1;     // the selection tints the object blue: a picture of textures wants none
                        n = new[] { pivot.X, pivot.Y, pivot.Z, n[2], homeYaw, homePitch };
                        Console.WriteLine($"CAMSHOT {one[..eq]} camera: --camera {n[0]:0.##} {n[1]:0.##} {n[2]:0.##} {n[3]:0.##} {n[4]:0.###} {n[5]:0.###}");
                    }
                    Gl.Camera.Target = new System.Numerics.Vector3(n[0], n[1], n[2]);
                    Gl.Camera.Distance = n[3];
                    Gl.Camera.Yaw = n[4];
                    Gl.Camera.Pitch = n[5];
                    var camQuiet = 0;
                    Gl.Watching = true;
                    Gl.WatchReset();
                    for (var f = 0; f < 600 && (f < 20 || camQuiet < 15); f++)
                    {
                        Gl.RequestNextFrameRendering();
                        await Task.Delay(100);
                        if (f > 1 && Gl.LastChanged < 0.05) camQuiet++; else camQuiet = 0;
                    }
                    Gl.Watching = false;
                    var file = System.IO.Path.Combine(args[cs + 1], one[..eq] + ".png");
                    Gl.RequestShot(file);
                    await Pump(() => Gl.ShotTaken, 60_000);
                    Console.WriteLine($"CAMSHOT {one[..eq]}: {(Gl.ShotTaken ? "photographed" : "NO FRAME")} (target {n[0]:0.#},{n[1]:0.#},{n[2]:0.#} distance {n[3]:0.#} yaw {n[4]:0.##} pitch {n[5]:0.##}; drew {Gl.LastDrawn:N0})");
                }
            }
        }
    }

    private async Task AutoDriveMap(string[] args)
    {
        // EVERYTHING ABOVE THIS LINE MUST NOT NEED A MAP NAMED BY --openmap.
        //
        // That is the whole reason these blocks live here. The return below gives up when no map
        // was named, and TEN flags have now been written underneath it and silently never run --
        // --opensub, --fold, --clip, --camera, --glshot. Each one looked like a broken feature
        // rather than a driver that never executed, and two of them cost a diagnosis each.
        //
        // The rule, for the next one: if a flag does not need `mapNode`, it goes above here.

        var m = Array.FindIndex(args, a => a == "--openmap");
        if (m < 0 || m + 1 >= args.Length) return;

        var mapId = args[m + 1];
        // The sublevel is optional: opening the MAP loads every sublevel it streams into one
        // scene, which is the normal thing to want and the thing worth testing.
        var sublevel = m + 2 < args.Length && !args[m + 2].StartsWith("--") ? args[m + 2] : null;
        var actor = args.Length > m + 3 && !args[m + 3].StartsWith("--") ? args[m + 3] : null;

        var project = Array.FindIndex(args, a => a == "--project");
        if (project >= 0 && project + 1 < args.Length)
        {
            // SAY SO. This went through Status and the in-app log only, so a driven run whose
            // project failed to open looked exactly like one whose project opened and had nothing
            // to save -- and the save step reported "no mod project is open" with no cause.
            _editor.Status = _editor.OpenProject(args[project + 1]);
            Console.WriteLine($"PROJECT {args[project + 1]}: {_editor.Status}");
        }

        var mapNode = Find(_editor.MapTree, n => n.Payload is MapEntry me && me.Id == mapId);
        if (mapNode?.Payload is not MapEntry map) return;
        _drivenHeadless = true;

        // --variant <leaf> [<leaf> ...] picks versions for a driven run, the way the dialog does
        // for a person. Without it the name heuristic decides, which is what every run before
        // today got.
        var vi = Array.FindIndex(args, a => a == "--variant");
        if (vi >= 0)
        {
            var want = args.Skip(vi + 1).TakeWhile(a => !a.StartsWith("--")).ToList();
            LevelReader.PlanVariants(map, _editor.Index,
                want.Count > 0 ? want.ToHashSet(StringComparer.OrdinalIgnoreCase) : null);
            Console.WriteLine($"VARIANT chose {want.Count}: {string.Join(", ", want)}");
        }
        else LevelReader.PlanVariants(map, _editor.Index, null);

        foreach (var g in LevelReader.Variants(map, _editor.Index))
            Console.WriteLine($"VARIANT group {g.Prefix}*  {g.Options.Count} option(s), " +
                              $"preferred {g.Preferred}");

        _mapOpenedAt = System.Diagnostics.Stopwatch.StartNew();     // --memreport times the load from here
        _editor.OpenMap(mapNode, map);
        // The watch starts NOW, not when MemDriven is reached: the flags between here and there (--shading waits for the
        // whole level to dress) would otherwise swallow the load and leave nothing to count.
        if (args.Contains("--memreport")) _memA = WaitLoaded("A", _mapOpenedAt);

        // The "no edits" export, taken now, before any flag below edits anything (see ExportDriven).
        await ExportDriven(args, "--exportbefore");

        // --browse <text> filters the level browser and selects the first hit, which is the same
        // path a click takes: SelectBrowse, then frame it.
        var browse = Array.FindIndex(args, a => a == "--browse");
        if (browse >= 0 && browse + 1 < args.Length)
        {
            // WAIT FOR THE LOAD, do not guess at it. A fixed delay passed on a warm cache and
            // silently tested an eighth of the map on a cold one -- "0 of 80 match" where the
            // finished map holds 7,503.
            for (var waited = 0; _editor.Busy && waited < 120_000; waited += 250) await Task.Delay(250);
            await Task.Delay(500);                  // and let the last publish reach the bindings
            _editor.BrowseFilter = args[browse + 1];
            Console.WriteLine($"BROWSE '{args[browse + 1]}': {_editor.Browse.Count:N0} row(s), " +
                              $"{_editor.BrowseTitle}");
            foreach (var r in _editor.Browse.Take(6))
                Console.WriteLine($"   {r.Name}  [{r.Class}]  {r.What}  <{r.Sublevel}>");
            if (_editor.Browse.Count > 0)
            {
                // The first row that can be moved AND is drawn: a harness that lands on an actor
                // with no serialised transform, or on a Blueprint the viewport cannot draw, proves
                // nothing about dragging one.
                var first = _editor.Browse.FindIndex(r => r.Movable && _editor.PlacementIndexOf(r.Export) >= 0);
                if (first < 0) first = _editor.Browse.FindIndex(r => r.Movable);
                if (first < 0) first = 0;
                BrowseList.SelectedIndex = first;
                await Task.Delay(300);
                var row0 = _editor.Browse[first];
                Console.WriteLine($"SELECTED: {_editor.ActorName}  ({_editor.ActorSub}), " +
                                  $"placement {_editor.PlacementIndexOf(row0.Export)}, " +
                                  $"movable {row0.Movable}, transform '{_editor.TransformSource}', " +
                                  $"at {_editor.LocX:0.###}, {_editor.LocY:0.###}, {_editor.LocZ:0.###}");
            }
        }

        await Why(args);

        // --subs: what is actually in the scene, grouped by the name the HIDE check uses.
        //
        // His asteroids: he unticked the Ring of Kafrene and it loaded anyway. The tick box
        // writes the sublevel's name into HiddenSublevels and the viewport skips placements
        // whose `Sublevel` matches -- so anything whose Sublevel says something ELSE survives,
        // and nothing anywhere says which those are.
        if (args.Contains("--subs"))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            await Task.Delay(500);

            // WHAT THE ROOT STREAMS, not the folder listing. The folder guess says 15 for the
            // hub and the root says 60 -- the window prints that disagreement on every open --
            // so measuring against the folder would have reported all 77,517 placements as
            // unreachable and been a fault in the instrument, not a finding.
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in map.Sublevels.Select(LevelReader.Leaf)) own.Add(n);
            try
            {
                if (_editor.Provider is { } sp)
                    foreach (var pkg in LevelReader.StreamedPackages(LevelReader.Read(sp, map.RootPath)))
                        own.Add(LevelReader.Leaf(pkg));
            }
            catch { }
            // BY THE HIDE KEY, which is the whole point: this measures whether a tick box can
            // REACH a placement, and the only name that can answer is the one the check uses.
            var groups = _editor.Placements.GroupBy(p => p.HideKey, StringComparer.OrdinalIgnoreCase)
                                .OrderByDescending(g => g.Count()).ToList();
            var strays = groups.Where(g => !own.Contains(g.Key)).ToList();

            Console.WriteLine($"SUBS {groups.Count} distinct sublevel name(s) across " +
                              $"{_editor.Placements.Count:N0} placement(s); " +
                              $"the map owns {own.Count}");
            if (strays.Count > 0)
                Console.WriteLine($"SUBS   {strays.Count} name(s) / {strays.Sum(g => g.Count()):N0} placement(s) " +
                                  "are NOT sublevels of this map, so no tick box can reach them:");

            // AND WHAT THE CURRENT SELECTION ACTUALLY REMOVES. "N sublevels hidden" is a count of
            // INTENT; this is the count of work, and the two disagreed by 36,047 placements.
            if (_editor.HiddenSublevels.Count > 0)
            {
                var removes = _editor.Placements.Count(p => _editor.HiddenSublevels.Contains(p.HideKey));
                // WHAT THE OLD CHECK WOULD HAVE MATCHED, so the fix has a number rather than a
                // claim: the old one compared against `Sublevel`, the package a thing lives in.
                var old = _editor.Placements.Count(p => _editor.HiddenSublevels.Contains(p.Sublevel));
                Console.WriteLine($"SUBS   hidden set holds {_editor.HiddenSublevels.Count} name(s) " +
                                  $"and removes {removes:N0} placement(s) " +
                                  $"({old:N0} under the old Sublevel check, {removes - old:N0} were escaping): " +
                                  $"{string.Join(", ", _editor.HiddenSublevels)}");
            }
            foreach (var g in strays.Take(10))
                Console.WriteLine($"SUBS   {g.Count(),8:N0}  {g.Key}  <- via {g.First().Through}");
        }

        // BEFORE the sublevel early-out: opening a whole map names no sublevel, and that is
        // exactly the case a pick wants to be tested in.
        if (Array.FindIndex(args, a => a == "--pickat") is var pick && pick >= 0 && pick + 2 < args.Length &&
            double.TryParse(args[pick + 1], out var px) && double.TryParse(args[pick + 2], out var py))
        {
            await Task.Delay(6000);                 // let the meshes arrive before clicking on them
            Gl.RequestPick(new Avalonia.Point(px, py));
        }

        // --tool <Select|Move|Rotate|Scale> arms a tool before the drag, so the headless test can
        // exercise all three the same way a person switches them.
        var tool = Array.FindIndex(args, a => a == "--tool");
        if (tool >= 0 && tool + 1 < args.Length)
        {
            _editor.Tool = args[tool + 1];

            // AND THE BUTTON, or the toolbar says Select while the viewport draws rings. The
            // radios are driven by clicks rather than bound, so anything that arms a tool without
            // one has to say so on screen too -- a screenshot that disagrees with the tool is how
            // a real divergence would get waved through.
            (_editor.Tool switch
            {
                "Move" => ToolMove, "Rotate" => ToolRotate, "Scale" => ToolScale, _ => ToolSelect,
            }).IsChecked = true;
        }

        var drag = Array.FindIndex(args, a => a == "--dragaxis");
        if (drag >= 0 && drag + 3 < args.Length && int.TryParse(args[drag + 1], out var ax) &&
            double.TryParse(args[drag + 2], out var ddx) && double.TryParse(args[drag + 3], out var ddy))
        {
            await Task.Delay(1200);
            Gl.FrameSelected();
            await Task.Delay(400);
            var before = (_editor.LocX, _editor.LocY, _editor.LocZ);
            // TWELVE STEPS, not one: a drag is many small moves and the point of the test is that
            // they collapse into a single undo.
            Gl.TestDrag(ax, new Avalonia.Point(400, 400), new Avalonia.Point(400 + ddx, 400 + ddy), 12);
            Console.WriteLine($"DRAG axis {ax} by {ddx},{ddy}px: " +
                              $"{before} -> ({_editor.LocX:0.###}, {_editor.LocY:0.###}, {_editor.LocZ:0.###}), " +
                              $"{_editor.Changes.Count} pending change(s)");
            Dump("after drag");
        }

        // --fly <keys> <ms> raises REAL key events at the viewport, so what is tested is the
        // whole path: is the control focusable, did it get focus, does the handler see the key,
        // does the timer run, does the camera move. Calling Camera.Move directly would have
        // passed with the control still unable to take a keystroke.
        var fly = Array.FindIndex(args, a => a == "--fly");
        if (fly >= 0 && fly + 2 < args.Length && int.TryParse(args[fly + 2], out var ms))
        {
            var keys = args[fly + 1].ToUpperInvariant()
                                    .Select(c => Enum.Parse<Key>(c.ToString())).ToArray();
            // WAIT FOR THE MESHES TOO. `Busy` is the map read; the viewport pulls meshes out of
            // the containers a few per frame long after that, and measuring the frame rate during
            // it would time the loading rather than the flying.
            //
            // Unless asked not to -- "nowait" measures the frame rate DURING loading, which is
            // the case that was three frames a second and the one worth guarding.
            if (!(fly + 3 < args.Length && args[fly + 3] == "nowait"))
            {
                for (var waited = 0; Gl.Loading && waited < 120_000; waited += 250) await Task.Delay(250);
                await Task.Delay(400);
            }
            Console.WriteLine($"FLY meshes still loading: {Gl.Loading}");
            Gl.RenderFrames = 0;
            Console.WriteLine($"FLY focused={Gl.IsFocused} keys={args[fly + 1]} " +
                              $"eye={Gl.Camera.Position} target={Gl.Camera.Target} dist={Gl.Camera.Distance:N0}");

            foreach (var k in keys)
                Gl.RaiseEvent(new KeyEventArgs { Key = k, RoutedEvent = InputElement.KeyDownEvent });
            await Task.Delay(ms);
            foreach (var k in keys)
                Gl.RaiseEvent(new KeyEventArgs { Key = k, RoutedEvent = InputElement.KeyUpEvent });
            await Task.Delay(120);

            Console.WriteLine($"FLY after {ms}ms  eye={Gl.Camera.Position} target={Gl.Camera.Target}");
            Console.WriteLine($"FLY {Gl.FlyFrames} fly frame(s) / {Gl.RenderFrames} render frame(s), " +
                              $"{Gl.FlySeconds:0.###}s of flight counted in {ms}ms real, " +
                              $"last frame {Gl.RenderMs:0.##}ms");

            var yaw = Gl.Camera.Yaw;
            var eye = Gl.Camera.Position;
            Gl.Camera.Look(60, 0);
            Console.WriteLine($"LOOK yaw {yaw:0.###} -> {Gl.Camera.Yaw:0.###}, " +
                              $"eye moved {(Gl.Camera.Position - eye).Length():0.###} units (should be ~0)");

            var d = Gl.Camera.Distance;
            Gl.Camera.Dolly(1.5f);
            Console.WriteLine($"WHEEL one notch: distance {d:N0} -> {Gl.Camera.Distance:N0}");
        }

        // --coverage answers "is everything in the level actually being drawn" with counts
        // rather than an opinion: what carries a mesh, what got a placement, and what did not.
        if (args.Contains("--coverage"))
        {
            for (var waited = 0; _editor.Busy && waited < 180_000; waited += 250) await Task.Delay(250);
            await Task.Delay(500);
            Coverage();
            Console.WriteLine(Gl.BackdropReport());
            Console.WriteLine(Gl.FailedMeshReport());
        }

        // --bpprobe opens the FIRST Blueprint actor's class and dumps it, to find out where a
        // cooked Blueprint keeps the mesh it places -- if it keeps one at all.
        var bp = Array.FindIndex(args, a => a == "--bpprobe");
        if (bp >= 0)
        {
            for (var waited = 0; _editor.Busy && waited < 180_000; waited += 250) await Task.Delay(250);
            var want = bp + 1 < args.Length && !args[bp + 1].StartsWith("--") ? args[bp + 1] : "_C";
            // EVERY EXPORT, not just the browse list. That list holds actors that passed a layer
            // filter, so the thing somebody is asking about is routinely not in it.
            var row = _editor.AllBrowse.FirstOrDefault(r => r.Class.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (row is null)
                foreach (var part in _editor.AllBrowse.GroupBy(r => r.Package))
                {
                    var scope = part.First().Scope;
                    var hit = scope.FirstOrDefault(e =>
                        (e.ExportType ?? "").Contains(want, StringComparison.OrdinalIgnoreCase) ||
                        (e.Name ?? "").Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) continue;
                    row = new BrowseRow(hit, hit.Name ?? "?", hit.ExportType ?? "?", "", "",
                                        part.First().Sublevel, part.Key, scope, false);
                    break;
                }
            if (row is null) { Console.WriteLine("BPPROBE nothing matched " + want); }
            else
            {
                var cls = row.Export.Class;
                Console.WriteLine($"BPPROBE actor {row.Name} class '{cls?.GetPathName()}' ({cls?.GetType().Name})");
                foreach (var p in row.Export.Properties)
                {
                    var idx = row.Export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex?>(p.Name.Text, null);
                    var to = idx is null ? null : LevelReader.Resolve(idx, row.Scope);
                    Console.WriteLine($"BPPROBE   actor.{p.Name} -> {(to is null ? "(not an export in this map)" : to.ExportType + " " + to.Name)}");
                    if (to is null) continue;
                    Console.WriteLine($"BPPROBE        props [{string.Join(", ", to.Properties.Select(q => q.Name.Text))}]");
                    Console.WriteLine($"BPPROBE        template '{to.Template?.GetPathName() ?? "(none)"}'");
                    var tmpl = to.Template?.Load();
                    if (tmpl is not null)
                        Console.WriteLine($"BPPROBE        template props [{string.Join(", ", tmpl.Properties.Select(q => q.Name.Text))}]");
                }

                var path = cls?.GetPathName() ?? "";
                var pkg = path.Contains('.') ? path[..path.LastIndexOf('.')] : path;
                Console.WriteLine($"BPPROBE loading package '{pkg}'");
                try
                {
                    var exports = LevelReader.Read(_editor.Provider!, pkg);
                    Console.WriteLine($"BPPROBE {exports.Length} export(s)");
                    foreach (var e in exports)
                        Console.WriteLine($"BPPROBE   {e.ExportType,-42} {e.Name,-40} " +
                                          $"[{string.Join(", ", e.Properties.Select(p => p.Name.Text).Take(12))}]");
                }
                catch (Exception ex) { Console.WriteLine("BPPROBE failed: " + ex.Message); }
            }
        }

        // --chain <actor> prints one actor's whole component tree with locals, parents and the
        // composed world transform, so a prop that is in the wrong place can be argued about with
        // numbers instead of a screenshot.
        var chain = Array.FindIndex(args, a => a == "--chain");
        if (chain >= 0 && chain + 1 < args.Length)
        {
            for (var waited = 0; _editor.Busy && waited < 180_000; waited += 250) await Task.Delay(250);
            var want = args[chain + 1];
            // The browse list holds ACTORS; the viewport can pick anything drawn. Search both, or
            // the one thing somebody clicked on is the one thing this cannot explain.
            var row = _editor.AllBrowse.FirstOrDefault(r => r.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
            var hit = _editor.Placements.FirstOrDefault(p => p.Actor.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (row is null && hit is null) Console.WriteLine("CHAIN nothing named " + want);
            else
            {
                row ??= new BrowseRow(hit!.Owner, hit.Actor, hit.Owner.ExportType ?? "?", "",
                                      "", hit.Sublevel, hit.Package, hit.Scope, false);
                var scope = row.Scope;
                var w = LevelReader.Chains(scope);
                Console.WriteLine($"CHAIN {row.Name} [{row.Class}] in {row.Sublevel}");

                // Everything whose chain reaches this actor's root, plus the root itself.
                var rootIdx = Array.FindIndex(scope, x => ReferenceEquals(x,
                    LevelReader.Resolve(row.Export.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex?>("RootComponent", null), scope)));
                Console.WriteLine($"CHAIN root export index {rootIdx}");

                for (var i = 0; i < scope.Length; i++)
                {
                    var top = i;
                    for (var d = 0; d < 32 && w.Parent[top] >= 0; d++) top = w.Parent[top];
                    if (top != rootIdx || rootIdx < 0) continue;

                    var e = scope[i];
                    var mesh = LevelReader.Holder(e, "StaticMesh")?
                               .GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex?>("StaticMesh", null)?
                               .ResolvedObject?.Name.Text;
                    Console.WriteLine($"CHAIN   [{i,5}] d{w.Depth[i]} {e.ExportType,-34} {e.Name,-38} " +
                                      $"parent {(w.Parent[i] < 0 ? "(root)" : scope[w.Parent[i]].Name)}");
                    Console.WriteLine($"CHAIN          world {w.Loc[i].X:0.#},{w.Loc[i].Y:0.#},{w.Loc[i].Z:0.#}  " +
                                      $"rot {w.Rot[i].X:0.###},{w.Rot[i].Y:0.###},{w.Rot[i].Z:0.###},{w.Rot[i].W:0.###}  " +
                                      $"scale {w.Scale[i].X:0.##},{w.Scale[i].Y:0.##},{w.Scale[i].Z:0.##}" +
                                      (mesh is null ? "" : $"  MESH {mesh}"));
                }
            }
        }

        // --hidebackdrop [factor] presses the toolbar toggle and optionally moves the dial.
        var hide = Array.FindIndex(args, a => a == "--hidebackdrop");
        if (hide >= 0)
        {
            for (var waited = 0; _editor.Busy && waited < 180_000; waited += 250) await Task.Delay(250);
            await Task.Delay(600);
            if (hide + 1 < args.Length && double.TryParse(args[hide + 1], out var f)) _editor.BackdropFactor = f;
            _editor.ShowBackdrop = false;
            await Task.Delay(400);
            Console.WriteLine($"HIDEBACKDROP factor {_editor.BackdropFactor:0.#}x, " +
                              $"hiding {_editor.BackdropCount:N0} of {_editor.Placements.Count:N0}");
        }

        // --gcprobe opens the first GeometryCollectionComponent it finds and follows it to the
        // asset, to see whether the geometry is readable at all or only referenced.
        if (args.Contains("--gcprobe"))
        {
            for (var waited = 0; _editor.Busy && waited < 180_000; waited += 250) await Task.Delay(250);
            var scope = _editor.AllBrowse.FirstOrDefault()?.Scope ?? Array.Empty<UObject>();
            foreach (var part in _editor.AllBrowse.GroupBy(r => r.Package))
            {
                var found = part.First().Scope.FirstOrDefault(e =>
                    (e.ExportType ?? "").Contains("GeometryCollection", StringComparison.Ordinal));
                if (found is null) continue;
                scope = part.First().Scope;
                Console.WriteLine($"GCPROBE {found.ExportType} {found.Name} in {part.First().Sublevel}");
                foreach (var p in found.Properties) Console.WriteLine($"GCPROBE   .{p.Name}");

                // Through the ARCHETYPE, of course: the map-side component carries RelativeLocation
                // and nothing else, exactly like every Blueprint prop did.
                var rest = LevelReader.Holder(found, "RestCollection")?
                           .GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex?>("RestCollection", null);
                Console.WriteLine($"GCPROBE   RestCollection -> '{rest?.ResolvedObject?.GetPathName() ?? "(none)"}'");
                var asset = rest?.ResolvedObject?.Load();
                if (asset is null) { Console.WriteLine("GCPROBE   asset would not load"); break; }
                Console.WriteLine($"GCPROBE   asset {asset.GetType().Name} '{asset.Name}' ({asset.ExportType})");
                foreach (var p in asset.Properties) Console.WriteLine($"GCPROBE      .{p.Name}");
                break;
            }
            if (scope.Length == 0) Console.WriteLine("GCPROBE nothing to look at");
        }

        // --near <x> <y> <z> <radius> lists what IS drawn around a point and, more to the point,
        // what is positioned there and is NOT. "A building is missing" is answerable only by
        // comparing those two lists at the place the author is standing.
        var near = Array.FindIndex(args, a => a == "--near");
        if (near >= 0 && near + 4 < args.Length &&
            float.TryParse(args[near + 1], out var nx) && float.TryParse(args[near + 2], out var ny) &&
            float.TryParse(args[near + 3], out var nz) && float.TryParse(args[near + 4], out var nr))
        {
            for (var waited = 0; _editor.Busy && waited < 180_000; waited += 250) await Task.Delay(250);
            var at = new System.Numerics.Vector3(nx, ny, nz);
            Console.WriteLine($"NEAR within {nr:N0} of {nx:N0},{ny:N0},{nz:N0}");

            foreach (var g in _editor.Placements.Where(p => (p.At - at).Length() <= nr)
                                     .GroupBy(p => p.MeshPath[(p.MeshPath.LastIndexOf('/') + 1)..])
                                     .OrderByDescending(g => g.Count()).Take(12))
                Console.WriteLine($"NEAR   DRAWN {g.Count(),5:N0}  {g.Key}");

            foreach (var part in _editor.AllBrowse.GroupBy(r => r.Package))
            {
                var scope = part.First().Scope;
                var w = LevelReader.Chains(scope);
                var missing = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < scope.Length; i++)
                {
                    var here = new System.Numerics.Vector3((float) w.Loc[i].X, (float) w.Loc[i].Y, (float) w.Loc[i].Z);
                    if ((here - at).Length() > nr) continue;
                    if (LevelReader.Holder(scope[i], "StaticMesh") is not null) continue;
                    var cls = scope[i].ExportType ?? "?";
                    missing[cls] = missing.GetValueOrDefault(cls) + 1;
                }
                foreach (var g in missing.OrderByDescending(kv => kv.Value).Take(8))
                    Console.WriteLine($"NEAR   NOT DRAWN {g.Value,5:N0}  {g.Key,-42} [{part.First().Sublevel}]");
            }
        }

        // --shading <mode> [waitMs] arms a shading mode and then reports what dressing the whole
        // level actually cost, which is the number that decides whether this is usable.
        // --draws <mesh|none> presses the REAL list row, so the whole path is tested: does the
        // panel find a mesh to change, is the choice offered at all, does picking it record a
        // swap, and does the viewport draw the new mesh. Calling SetDraws directly would pass
        // with the list empty and the control unclickable.
        // --variantcheck builds the dialog WITHOUT showing it, because a modal cannot be clicked
        // in a driven run and an exception in a dialog constructor reaches the author as the app
        // dying when they click a map. Constructing it is most of what can go wrong.
        if (args.Contains("--variantcheck"))
        {
            var groups = LevelReader.Variants(map, _editor.Index);
            try
            {
                var dlg = new VariantDialog(map.Id, groups);
                Console.WriteLine($"VARIANTCHECK built ok: {groups.Count} group(s), title '{dlg.Title}'");

                // AND LOOK AT IT. This dialog shipped twice without being seen, and both times the
                // author had to say it was unreadable. It is ordinary controls, no GL, so unlike
                // the viewport it really can be photographed.
                var shotAt = Array.FindIndex(args, a => a == "--variantcheck");
                if (shotAt >= 0 && shotAt + 1 < args.Length && !args[shotAt + 1].StartsWith("--"))
                {
                    dlg.Show(this);
                    await Task.Delay(900);
                    if (dlg.Content is Visual v && v.Bounds.Width > 1)
                    {
                        var size = new PixelSize((int) v.Bounds.Width, (int) v.Bounds.Height);
                        using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
                        bmp.Render(v);
                        bmp.Save(args[shotAt + 1]);
                        Console.WriteLine($"VARIANTCHECK wrote {args[shotAt + 1]}  {size.Width}x{size.Height}");
                    }
                    else Console.WriteLine("VARIANTCHECK dialog had no measured size to photograph");
                    dlg.Close();
                }
                foreach (var g in groups)
                    Console.WriteLine($"VARIANTCHECK   {g.Prefix}*  ->  {g.Preferred}   " +
                                      $"of {string.Join(", ", g.Options)}");
            }
            catch (Exception ex) { Console.WriteLine("VARIANTCHECK THREW: " + ex); }
        }

        var draws = Array.FindIndex(args, a => a == "--draws");
        if (draws >= 0 && draws + 1 < args.Length)
        {
            for (var waited = 0; _editor.Busy && waited < 120_000; waited += 250) await Task.Delay(250);
            // AND WAIT FOR THE PACKAGE READ, which is a separate task from the map load. Busy
            // going false only means the level is open, not that the panel knows what this actor
            // draws -- the first version of this measured an empty list and said so.
            for (var waited = 0; _editor.DrawsBusy && waited < 120_000; waited += 100) await Task.Delay(100);
            await Task.Delay(300);
            var want = args[draws + 1];
            Console.WriteLine($"DRAWS on {_editor.ActorName}: canSwap={_editor.CanSwap} " +
                              $"now='{_editor.DrawsNow}' choices={_editor.MeshChoices.Count:N0} " +
                              $"why='{_editor.SwapWhy}'");

            Console.WriteLine($"DRAWS offered: {string.Join(", ", _editor.MeshChoices.Take(4))}" +
                              (_editor.MeshChoices.Count > 4 ? ", ..." : ""));
            if (want.Equals("none", StringComparison.OrdinalIgnoreCase))
                OnRemoveMesh(null, null!);
            else
            {
                var at = _editor.MeshChoices.FindIndex(
                    m => m.Equals(want, StringComparison.OrdinalIgnoreCase));
                if (at < 0) Console.WriteLine($"DRAWS '{want}' is not offered - this level cannot point at it");
                else { MeshList.SelectedIndex = at; await Task.Delay(250); }
            }

            Console.WriteLine($"DRAWS after: '{_editor.DrawsNow}', {_editor.Swaps.Count} swap(s), " +
                              $"status '{_editor.Status}'");
            foreach (var x in _editor.Swaps)
                Console.WriteLine($"DRAWS   {x.Address}.{x.Path}: {x.Summary}   [{x.Sublevel}]");

            // AND WHAT THE PICTURE NOW SAYS. The list and the viewport are two different things,
            // and a swap that is recorded but never reaches the placement is exactly the kind of
            // half-working that looks fine in a changes dock.
            foreach (var p in _editor.Placements.Where(p => p.Draws is not null).Take(4))
                Console.WriteLine($"DRAWS   DRAWN {p.Actor} -> " +
                                  $"{(p.Shown is null ? "nothing" : p.Shown[(p.Shown.LastIndexOf('/') + 1)..])}");
        }

        // --place <asset> [x y z]: pick an asset out of the list and put one down, without a
        // mouse. The whole chain the Assets tab runs, reported at every joint -- how many assets
        // the map offered, whether a donor VERIFIED, which tier it came from, and last of all
        // whether the preview actually reached the placement list.
        //
        // That last line is the one worth having. A spawn recorded in the dock and never drawn is
        // exactly the shape of half-working that a screenshot of the dock cannot tell from the
        // real thing -- and this editor has already shipped one of those, when the shadow pass
        // ran and changed nothing.
        var place = Array.FindIndex(args, a => a == "--place");
        if (place >= 0 && place + 1 < args.Length)
        {
            for (var waited = 0; _editor.Busy && waited < 120_000; waited += 250) await Task.Delay(250);
            await Task.Delay(300);

            var want = args[place + 1];
            Console.WriteLine($"PLACE {_editor.Assets.Count:N0} asset(s) in the list, " +
                              $"{_editor.Placements.Count:N0} placement(s) behind them");

            var row = _editor.Assets.FirstOrDefault(
                          a => a.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
                   ?? _editor.Assets.FirstOrDefault(
                          a => a.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                Console.WriteLine($"PLACE no asset matches '{want}'. First few: " +
                                  string.Join(", ", _editor.Assets.Take(5).Select(a => a.Name)));
            }
            else
            {
                Console.WriteLine($"PLACE picked {row.Name}  ({row.Detail})");
                Console.WriteLine($"PLACE       {row.Path}");

                // THROUGH THE LIST, not through the editor. Selecting the row is what a person
                // does, and everything the feature claims hangs off that one event -- the donor
                // read, the arming, the gizmo. Calling SelectAsset directly would test the half
                // that was never in doubt.
                //
                // FILTER FIRST, because the list is a folder tree until something is typed into
                // it, and a node four folders deep is not realised for a selection to land on.
                // Typing a name and clicking the result is also what a person does.
                var before = _editor.Placements.Count;
                AssetList.SelectedItem = PickAsset(row);

                // THE DONOR SEARCH IS A BACKGROUND READ, like the Draws panel's. Measuring before
                // it lands is how the first version of --draws reported a working feature broken.
                for (var waited = 0; _editor.PlaceBusy && waited < 120_000; waited += 100)
                    await Task.Delay(100);
                await Task.Delay(150);
                Console.WriteLine($"PLACE donor: canPlace={_editor.CanPlace}");
                Console.WriteLine($"PLACE   how='{_editor.PlaceHow}'");
                if (!_editor.CanPlace) Console.WriteLine($"PLACE   why='{_editor.PlaceWhy}'");
                Console.WriteLine($"PLACE armed: spawn={(_editor.PickedSpawn?.Name ?? "none")} " +
                                  $"gizmo on placement #{Gl.Selected}, tool {_editor.Tool}");

                // Explicit coordinates move the armed one, the way "To camera" does.
                if (place + 4 < args.Length &&
                    float.TryParse(args[place + 2], out var spotX) &&
                    float.TryParse(args[place + 3], out var spotY) &&
                    float.TryParse(args[place + 4], out var spotZ) &&
                    _editor.PickedSpawn is { } armed)
                {
                    _editor.MoveSpawn(armed, new System.Numerics.Vector3(spotX, spotY, spotZ));
                    Console.WriteLine($"PLACE moved to {spotX:0.#}, {spotY:0.#}, {spotZ:0.#}");
                }

                await Task.Delay(250);
                foreach (var sp in _editor.Spawns)
                    Console.WriteLine($"PLACE   SPAWN {sp.Name} at {sp.Summary} in {sp.Sublevel} " +
                                      $"[{sp.How}] donor={sp.Donor} mesh='{sp.Mesh}' id={sp.Id}");

                // AND THE PICTURE. Placements growing by one, with the preview in it, is the
                // difference between a record of an intention and a thing that is there.
                var drawn = _editor.Placements.Where(p => p.Spawned is not null).ToList();
                Console.WriteLine($"PLACE   placements {before:N0} -> {_editor.Placements.Count:N0}, " +
                                  $"{drawn.Count} of them previews");
                foreach (var p in drawn)
                    Console.WriteLine($"PLACE   DRAWN {p.Actor} at {p.At.X:0.#},{p.At.Y:0.#},{p.At.Z:0.#} " +
                                      $"-> {p.MeshPath[(p.MeshPath.LastIndexOf('/') + 1)..]}");

                // --placedrag <axis> <dx> <dy>: drag the new actor with the GIZMO.
                //
                // "Auto attach to the gizmo and let them move it around the scene" is a claim
                // about the mouse, and the only thing that can witness it is a mouse. Nudge has a
                // second path for a spawn -- there is no selected export to write a property
                // through -- so a drag that silently did nothing would look exactly like a drag
                // that worked, right up until the numbers in the dock did not change.
                var pd = Array.FindIndex(args, a => a == "--placedrag");
                if (pd >= 0 && pd + 3 < args.Length && int.TryParse(args[pd + 1], out var pax) &&
                    double.TryParse(args[pd + 2], out var pdx) && double.TryParse(args[pd + 3], out var pdy))
                {
                    Gl.FrameSelected();
                    await Task.Delay(500);
                    var sp = _editor.PickedSpawn;
                    var was = sp is null ? "none" : sp.Summary;
                    var steps = _editor.CanUndo ? 1 : 0;
                    Gl.TestDrag(pax, new Avalonia.Point(400, 400),
                                new Avalonia.Point(400 + pdx, 400 + pdy), 12);
                    await Task.Delay(200);
                    Console.WriteLine($"PLACEDRAG axis {pax} by {pdx},{pdy}px: {was} -> " +
                                      $"{(_editor.PickedSpawn?.Summary ?? "none")}");
                    Console.WriteLine($"PLACEDRAG   {_editor.Changes.Count} move(s) filed " +
                                      $"(must be 0 - a new actor is not a property edit), " +
                                      $"undo now '{_editor.UndoHint}'");

                    // AND WHERE THE PLACEMENT THINKS IT IS. `At` is what decides backdrop and
                    // blockout, so a preview whose At stayed at the spot it was created gets
                    // filed by where it WAS and can vanish when a filter comes on.
                    Console.WriteLine($"PLACEDRAG   {_editor.Placements.Count:N0} placement(s), " +
                                      $"{_editor.Placements.Count(x => x.Spawned is not null)} preview(s), " +
                                      $"{_editor.Spawns.Count} spawn(s)");
                    foreach (var pv in _editor.Placements.Where(x => x.Spawned is not null))
                        Console.WriteLine($"PLACEDRAG   placement At {pv.At.X:0.#},{pv.At.Y:0.#},{pv.At.Z:0.#} " +
                                          $"(must match the spawn above)");
                    _ = steps;
                }

                // --placebrowse <other>: click a SECOND asset without touching the first.
                //
                // The untouched one must be replaced rather than kept, or clicking down a list of
                // two thousand assets authors two thousand actors. This counts them.
                var pb = Array.FindIndex(args, a => a == "--placebrowse");
                if (pb >= 0 && pb + 1 < args.Length)
                {
                    // CLEAR THE FILTER FIRST. PickAsset narrows the list to the one it selected,
                    // so searching Assets again only ever sees the previous pick.
                    _editor.AssetFilter = "";
                    var second = _editor.Assets.FirstOrDefault(
                        a => a.Name.Contains(args[pb + 1], StringComparison.OrdinalIgnoreCase));
                    if (second is null) Console.WriteLine($"PLACEBROWSE no asset matches '{args[pb + 1]}'");
                    else
                    {
                        AssetList.SelectedItem = PickAsset(second);
                        for (var waited = 0; _editor.PlaceBusy && waited < 120_000; waited += 100)
                            await Task.Delay(100);
                        await Task.Delay(200);
                        Console.WriteLine($"PLACEBROWSE picked {second.Name} - " +
                                          $"{_editor.Spawns.Count} spawn(s) now, showing " +
                                          string.Join(", ", _editor.Spawns.Select(x => x.Name)));
                    }
                }

                // SAVING IS THE OTHER HALF and the only one the build ever sees.
                //
                // TWICE, because that is the claim a spawn's Id exists to make. A move is keyed by
                // the actor and the property it writes, so saving it again replaces it; two
                // identical spawns are two actors, which is a legitimate thing to ask for, so
                // there is nothing to key on and a second save would quietly add a second actor.
                // Pressing the button twice is the most ordinary thing an author does.
                if (args.Contains("--placesave"))
                {
                    Console.WriteLine("PLACE save: " + _editor.SaveMoves());
                    Console.WriteLine("PLACE save again: " + _editor.SaveMoves());
                }
            }
        }

        // --placescan <n>: try the first n assets in the list and tally how each one would be
        // placed. One asset working proves the route exists; it cannot tell a donor rule that is
        // right from one that is so strict almost nothing passes it, and both look identical from
        // a single success. This is that number.
        var scan = Array.FindIndex(args, a => a == "--placescan");
        if (scan >= 0)
        {
            for (var waited = 0; _editor.Busy && waited < 120_000; waited += 250) await Task.Delay(250);
            await Task.Delay(300);
            var many = scan + 1 < args.Length && int.TryParse(args[scan + 1], out var n) ? n : 40;

            int copied = 0, imported = 0, refused = 0;
            var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var row in _editor.Assets.Take(many))
            {
                _editor.SelectAsset(row);
                for (var waited = 0; _editor.PlaceBusy && waited < 60_000; waited += 50)
                    await Task.Delay(50);
                if (!_editor.CanPlace)
                {
                    refused++;
                    reasons[_editor.PlaceWhy] = reasons.GetValueOrDefault(_editor.PlaceWhy) + 1;
                }
                else if (_editor.PlaceHow.Contains("imports the mesh")) imported++;
                else copied++;
            }
            clock.Stop();

            var tried = copied + imported + refused;
            Console.WriteLine($"PLACESCAN {tried} asset(s) of {_editor.Assets.Count:N0}, " +
                              $"{clock.ElapsedMilliseconds:N0} ms");
            Console.WriteLine($"PLACESCAN   {copied} copied outright (no import), " +
                              $"{imported} needed an import, {refused} refused");
            foreach (var (why, count) in reasons.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"PLACESCAN   refused x{count}: {why}");
        }

        // --assetscope <this|game> [name]: switch the asset list to the whole game and try to
        // place something the level has never heard of. That is the half his question was about:
        // "what about props/assets from other maps, since we confirmed we can load umbara plants
        // into other maps". The route is the one the plant took -- a donor plus an import.
        var scopeAt = Array.FindIndex(args, a => a == "--assetscope");
        if (scopeAt >= 0 && scopeAt + 1 < args.Length)
        {
            for (var waited = 0; _editor.Busy && waited < 120_000; waited += 250) await Task.Delay(250);
            _editor.WholeGame = args[scopeAt + 1].StartsWith("game", StringComparison.OrdinalIgnoreCase);
            for (var waited = 0; _editor.GameAssetsBusy && waited < 300_000; waited += 250)
                await Task.Delay(250);
            await Task.Delay(200);
            Console.WriteLine($"SCOPE whole-game={_editor.WholeGame}, " +
                              $"{_editor.Assets.Count:N0} asset(s), {_editor.AssetTree.Count} top row(s)");
            foreach (var n in _editor.AssetTree.Take(8))
                Console.WriteLine($"SCOPE   {(n.IsAsset ? "  " : "[]")} {n.Title,-46} {n.Detail,-12} " +
                                  $"expanded={n.IsExpanded} children={n.Children.Count}");

            if (scopeAt + 2 < args.Length && !args[scopeAt + 2].StartsWith("--"))
            {
                var want = args[scopeAt + 2];
                var row = _editor.Assets.FirstOrDefault(
                              a => a.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
                       ?? _editor.Assets.FirstOrDefault(
                              a => a.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
                if (row is null) Console.WriteLine($"SCOPE no asset matches '{want}'");
                else
                {
                    Console.WriteLine($"SCOPE picked {row.Name}  ({row.Detail})");
                    AssetList.SelectedItem = PickAsset(row);
                    for (var waited = 0; _editor.PlaceBusy && waited < 120_000; waited += 100)
                        await Task.Delay(100);
                    await Task.Delay(200);
                    Console.WriteLine($"SCOPE canPlace={_editor.CanPlace}");
                    Console.WriteLine($"SCOPE   how='{_editor.PlaceHow}'");
                    if (!_editor.CanPlace) Console.WriteLine($"SCOPE   why='{_editor.PlaceWhy}'");
                    foreach (var sp in _editor.Spawns)
                        Console.WriteLine($"SCOPE   SPAWN {sp.Name} [{sp.How}] mesh='{sp.Mesh}'");
                }
            }
        }

        // --hidestaged: press Map > "Hide cinematic and backdrop sublevels" and report what it cost
        // the picture. His question was whether the editor shows the level as the GAME shows it,
        // and the honest answer was no -- so the number that matters is how much of what was on
        // screen is not the room you are standing in.
        if (args.Contains("--hidestaged"))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Loading && waited < 900_000; waited += 250) await Task.Delay(250);
            await Task.Delay(400);

            var before = _editor.Placements.Count;
            for (var i = 0; i < 20; i++) { Gl.RequestNextFrameRendering(); await Task.Delay(80); }
            var drawnBefore = Gl.LastDrawn;
            Console.WriteLine($"HIDESTAGED clip {Gl.Camera.NearPlane:0.##} .. {Gl.Camera.FarPlane:N0}  " +
                              $"ratio {Gl.Camera.FarPlane / MathF.Max(Gl.Camera.NearPlane, 0.0001f):N0}:1  " +
                              "(before)");

            // EVERY SUBLEVEL THE VIEWPORT IS DRAWING, biggest first -- streamed and instanced
            // alike. This is the list his question was really about: which of these is the game
            // actually showing at any one time.
            var drawnBy = _editor.Placements.Where(p => p.Spawned is null)
                                 .GroupBy(p => p.Sublevel, StringComparer.OrdinalIgnoreCase)
                                 .OrderByDescending(g => g.Count()).ToList();
            Console.WriteLine($"HIDESTAGED {drawnBy.Count} sublevel(s) are drawing something:");
            foreach (var g in drawnBy.Take(14))
                Console.WriteLine($"HIDESTAGED   {g.Count(),7:N0}  {g.Key}");

            Console.WriteLine("HIDESTAGED " + _editor.HideStagedSublevels(AllMapNodes()));
            await Task.Delay(500);
            for (var i = 0; i < 20; i++) { Gl.RequestNextFrameRendering(); await Task.Delay(80); }

            Console.WriteLine($"HIDESTAGED placements {before:N0} (unchanged, so picking still " +
                              $"lines up), drawn {drawnBefore:N0} -> {Gl.LastDrawn:N0}, " +
                              $"hidden {Gl.LastHidden:N0}");
            Console.WriteLine($"HIDESTAGED meshes {Gl.MeshesKnown:N0} known, {Gl.MeshesReady:N0} ready");

            // THE DEPTH RATIO, which is what a backdrop really costs. The far plane comes from the
            // farthest thing SHOWN, so hiding an eleven-kilometre station should buy precision
            // back for everything near the camera.
            Console.WriteLine($"HIDESTAGED clip {Gl.Camera.NearPlane:0.##} .. {Gl.Camera.FarPlane:N0}  " +
                              $"ratio {Gl.Camera.FarPlane / MathF.Max(Gl.Camera.NearPlane, 0.0001f):N0}:1");
            Console.WriteLine("HIDESTAGED " + _editor.SublevelHint);
        }

        // --hidesub <name>: turn ONE sublevel off by name and report what the picture lost.
        //
        // Isolates the mechanism from the preset that drives it. The preset hid rows and the drawn
        // count did not move, and those are two different failures: a tick that does not reach the
        // viewport, or a preset that ticked the wrong rows.
        var hs = Array.FindIndex(args, a => a == "--hidesub");
        if (hs >= 0 && hs + 1 < args.Length)
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Loading && waited < 900_000; waited += 250) await Task.Delay(250);
            await Task.Delay(400);
            for (var i = 0; i < 15; i++) { Gl.RequestNextFrameRendering(); await Task.Delay(80); }

            var want = args[hs + 1];
            var mine = _editor.Placements.Count(p => p.Sublevel.Equals(want, StringComparison.OrdinalIgnoreCase));
            var wasDrawn = Gl.LastDrawn;
            var wasMeshes = Gl.MeshesKnown;
            var wasFar = Gl.Camera.FarPlane;
            var wasRatio = wasFar / MathF.Max(Gl.Camera.NearPlane, 0.0001f);

            _editor.ShowSublevel(want, false);
            await Task.Delay(400);
            for (var i = 0; i < 15; i++) { Gl.RequestNextFrameRendering(); await Task.Delay(80); }

            Console.WriteLine($"HIDESUB '{want}' holds {mine:N0} placement(s)");
            Console.WriteLine($"HIDESUB   drawn {wasDrawn:N0} -> {Gl.LastDrawn:N0}, " +
                              $"hidden {Gl.LastHidden:N0}, meshes {wasMeshes:N0} -> {Gl.MeshesKnown:N0}");
            Console.WriteLine($"HIDESUB   far {wasFar:N0} -> {Gl.Camera.FarPlane:N0}, " +
                              $"depth ratio {wasRatio:N0}:1 -> " +
                              $"{Gl.Camera.FarPlane / MathF.Max(Gl.Camera.NearPlane, 0.0001f):N0}:1");
            Console.WriteLine($"HIDESUB   {_editor.SublevelHint}");
        }

        // --build: press Build and install, and report what came back.
        //
        // The WHOLE write half in one flag, through the same method the button calls -- the point
        // of putting build and install in this app was that a map edit stops needing a second
        // program, and a driven check that skipped to ModBuild.Build directly would be testing the
        // route nobody takes. That is the reason "build mods through the Studio, never the CLI"
        // exists in the first place.
        if (args.Contains("--build"))
        {
            for (var waited = 0; _editor.Busy && waited < 120_000; waited += 250) await Task.Delay(250);
            var before = _editor.Log.Count;
            await _editor.BuildAndInstallAsync();

            Console.WriteLine($"BUILD failed={_editor.BuildFailed}");
            Console.WriteLine("BUILD verdict: " + _editor.BuildVerdict);
            foreach (var line in _editor.Log.Skip(before))
                Console.WriteLine("BUILD   " + line);
        }

        // --drawlog <ms> samples what the viewport is ACTUALLY drawing, four times a second, for
        // as long as it is told. "Everything disappears and reappears over and over" is a claim
        // about one number moving, and nothing reported that number over time -- the viewport said
        // "drew N placements" exactly once and never again, which cannot witness an oscillation.
        var dlog = Array.FindIndex(args, a => a == "--drawlog");
        if (dlog >= 0 && dlog + 1 < args.Length && int.TryParse(args[dlog + 1], out var forMs))
        {
            Gl.TraceMeshes = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var last = -1;
            while (clock.ElapsedMilliseconds < forMs)
            {
                await Task.Delay(250);
                Gl.RequestNextFrameRendering();
                var d = Gl.LastDrawn;
                var flag = last >= 0 && d < last / 2 ? "   <<<< COLLAPSED" : "";

                // THE CLIP PLANES, because they are derived from the camera's orbit distance and
                // the scene is re-framed on every streamed sublevel. If they move, geometry
                // crosses them, and a mesh that is still loaded and still in the table simply
                // stops being inside the frustum -- which is what "it disappears and something
                // else comes back" looks like.
                // THE CAMERA'S OWN NUMBERS. This used to recompute the formula here, so it kept
                // reporting the old planes after they were fixed -- a check testing its own copy
                // of the thing under test.
                var dist = Gl.Camera.Distance;
                var nearPlane = Gl.Camera.NearPlane;
                var farPlane = Gl.Camera.FarPlane;
                var eye = Gl.Camera.Position;
                var tooNear = 0;
                var tooFar = 0;
                foreach (var pl in _editor.Placements)
                {
                    var away = (new System.Numerics.Vector3(pl.At.X, pl.At.Z, pl.At.Y) - eye).Length();
                    if (away < nearPlane) tooNear++;
                    else if (away > farPlane) tooFar++;
                }
                Console.WriteLine($"DRAWLOG {clock.ElapsedMilliseconds,7:N0}ms  drew {d,7:N0}  " +
                                  $"ready {Gl.MeshesReady,6:N0}  scene {_editor.Placements.Count,7:N0}  " +
                                  $"dist {dist,10:N0}  near {nearPlane,8:N0}  far {farPlane,12:N0}  " +
                                  $"clipped near {tooNear,6:N0} far {tooFar,6:N0}{flag}");
                if (Gl.LoadCount > 0)
                    Console.WriteLine($"DRAWLOG   loaded {Gl.LoadCount:N0} mesh(es) in {Gl.LoadMs / 1000:N1}s " +
                                      $"= {Gl.LoadMs / Gl.LoadCount:N0}ms each; worst {Gl.Slowest}");
                last = d;
            }
        }

        // --lights reads the level's light rig and prints it, before anything shades with it. The
        // numbers can then be checked against the map's own values rather than trusted.
        if (args.Contains("--lights"))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            foreach (var line in _editor.LightReport()) Console.WriteLine("LIGHTS " + line);

            // WHAT THE VIEWPORT ACTUALLY SENT, not what the rig holds. The two differ on purpose
            // -- only 32 local lights fit -- and a shader that failed to compile reports zero here
            // while the rig above still prints a full page of lights. Asking the reader rather
            // than the renderer is how the last three instruments managed to measure the wrong end.
            Gl.RequestNextFrameRendering();
            await Task.Delay(400);
            Console.WriteLine($"LIGHTS viewport sent {Gl.LocalLit} of {Gl.LocalKnown} local light(s)" +
                              (Gl.Fault is { } f ? $"   GL FAULT: {f}" : ""));
        }

        // --zoom <distance> puts the camera where a scroll wheel would, so the clip planes can be
        // checked at the range the author actually hit. "Zooming in makes the map disappear" is a
        // claim about a CLOSE camera, and every measurement before this one was taken from far out
        // -- which is why the mechanism was found and then wrongly ruled out.
        var zoom = Array.FindIndex(args, a => a == "--zoom");
        if (zoom >= 0 && zoom + 1 < args.Length && float.TryParse(args[zoom + 1], out var zdist))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            await Task.Delay(800);
            Gl.Camera.Distance = zdist;
            Gl.RequestNextFrameRendering();
            await Task.Delay(500);

            var eye = Gl.Camera.Position;
            int tooNear = 0, tooFar = 0;
            foreach (var pl in _editor.Placements)
            {
                var away = (new System.Numerics.Vector3(pl.At.X, pl.At.Z, pl.At.Y) - eye).Length();
                if (away < Gl.Camera.NearPlane) tooNear++;
                else if (away > Gl.Camera.FarPlane) tooFar++;
            }
            Console.WriteLine($"ZOOM dist {zdist:N0}  near {Gl.Camera.NearPlane:N1}  " +
                              $"far {Gl.Camera.FarPlane:N0}  floor {Gl.Camera.FarFloor:N0}  " +
                              $"clipped near {tooNear:N0} far {tooFar:N0} of {_editor.Placements.Count:N0}");

            // HOW FAR OUT DOES THIS LEVEL ACTUALLY GO? The floor is taken from the farthest
            // placement, and one stray sky-sphere a million units out would set the depth range
            // for everything -- which costs near-plane precision at the other end. The spread says
            // whether that is what is happening.
            var away2 = _editor.Placements
                .Select(pl => (new System.Numerics.Vector3(pl.At.X, pl.At.Z, pl.At.Y) - eye).Length())
                .OrderBy(d => d).ToArray();
            foreach (var q in new[] { 0.50, 0.90, 0.99, 0.999, 1.0 })
                Console.WriteLine($"ZOOM   {q * 100,6:0.#}%  {away2[(int) Math.Min(away2.Length - 1, q * away2.Length)]:N0} units");

            // AND WHAT ARE THE FAR ONES? Whether the outliers are a sky that must not be clipped
            // or strays nobody would miss is not a thing to guess at.
            foreach (var pl in _editor.Placements
                         .OrderByDescending(pl => (new System.Numerics.Vector3(pl.At.X, pl.At.Z, pl.At.Y) - eye).Length())
                         .Take(6))
                Console.WriteLine($"ZOOM   FAR {(new System.Numerics.Vector3(pl.At.X, pl.At.Z, pl.At.Y) - eye).Length(),10:N0}  " +
                                  $"{pl.MeshPath[(pl.MeshPath.LastIndexOf('/') + 1)..]}  [{pl.Sublevel}]");
        }

        // --dupes: HOW MUCH OF THIS SCENE IS THE SAME THING TWICE?
        //
        // Nothing is being unloaded -- the per-mesh trace proves the table only grows -- so a mesh
        // that is "there one second and gone the next" with a still camera is a mesh that got
        // COVERED. Two copies of one surface in one place do not both survive a depth test.
        // This counts them and, more usefully, says which sublevels they came from.
        if (args.Contains("--dupes"))
        {
            for (var waited = 0; _editor.Busy && waited < 300_000; waited += 250) await Task.Delay(250);
            await Task.Delay(500);

            var byPlace = _editor.Placements
                .GroupBy(p => (p.MeshPath,
                               X: MathF.Round(p.At.X, 0), Y: MathF.Round(p.At.Y, 0), Z: MathF.Round(p.At.Z, 0)))
                .Where(g => g.Count() > 1)
                .ToList();

            var extra = byPlace.Sum(g => g.Count() - 1);
            Console.WriteLine($"DUPES {byPlace.Count:N0} place(s) hold the same mesh more than once, " +
                              $"{extra:N0} redundant placement(s) of {_editor.Placements.Count:N0}");

            // SAME OWNER OR DIFFERENT OWNERS? The answer decides whose bug this is. Two actors
            // placing one mesh in one spot is the level's own doing and the game draws it that way
            // too; ONE actor producing the same placement twice is this reader counting a
            // component down two paths, and is ours to fix.
            var sameOwner = byPlace.Count(g => g.Select(p => p.Owner).Distinct().Count() == 1);
            Console.WriteLine($"DUPES   {sameOwner:N0} of those are ONE actor emitted more than once " +
                              $"(this reader's fault), {byPlace.Count - sameOwner:N0} are different actors " +
                              $"(the level's own)");
            foreach (var g in byPlace.Where(g => g.Select(p => p.Owner).Distinct().Count() == 1).Take(5))
                Console.WriteLine($"DUPES     {g.Count()}x  {g.First().Actor}  " +
                                  $"({g.First().Owner.ExportType})  " +
                                  $"{g.Key.MeshPath[(g.Key.MeshPath.LastIndexOf('/') + 1)..]}  " +
                                  $"[{g.First().Sublevel}]");

            foreach (var pair in byPlace
                         .SelectMany(g => g.Select(p => p.Sublevel).Distinct().OrderBy(x => x)
                                           .Aggregate(new List<string>(), (acc, x) => { acc.Add(x); return acc; })
                                           .Where(_ => true)
                                           .Select(_ => string.Join(" + ", g.Select(p => p.Sublevel).Distinct().OrderBy(x => x))))
                         .GroupBy(x => x).OrderByDescending(x => x.Count()).Take(12))
                Console.WriteLine($"DUPES   {pair.Count(),7:N0}  {pair.Key}");
        }

        var shade = Array.FindIndex(args, a => a == "--shading");
        if (shade >= 0 && shade + 1 < args.Length)
        {
            for (var waited = 0; _editor.Busy && waited < 180_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Loading && waited < 240_000; waited += 250) await Task.Delay(250);

            var t0 = DateTime.UtcNow;
            _editor.Shading = args[shade + 1];      // already set above; re-set so the timing below is honest

            // Redress is POSTED, so the queue is still empty for a moment. Polling `Dressing`
            // straight away reported "0 dressed" for a level that then went on to dress 1,450.
            await Task.Delay(1000);
            var cap = shade + 2 < args.Length && int.TryParse(args[shade + 2], out var cutoff) ? cutoff : 240_000;
            for (var waited = 0; Gl.Dressing && waited < cap; waited += 250) await Task.Delay(250);

            Console.WriteLine(Gl.MaterialSpread());
            Console.WriteLine($"SHADING {_editor.Shading}: {Gl.MaterialsBound:N0} mesh(es) dressed, " +
                              $"{Gl.DressMs:N0} ms of binding over {(DateTime.UtcNow - t0).TotalSeconds:0.#}s wall, " +
                              $"still queued {Gl.Dressing}");
        }

        // --reshade <mode> changes the shading mode AFTER everything has been dressed once, which
        // is the only way to exercise the path that fetches a released material back. Dressing
        // releases the UMaterialInterface as soon as it is bound; if reloading it by path did not
        // work, the FIRST dress would still look perfect and the second would come back grey. A
        // test that only ever dresses once cannot tell those apart.
        var re = Array.FindIndex(args, a => a == "--reshade");
        if (re >= 0 && re + 1 < args.Length)
        {
            for (var waited = 0; Gl.Dressing && waited < 900_000; waited += 250) await Task.Delay(250);
            var before = Gl.MaterialsBound;
            _editor.Shading = args[re + 1];
            await Task.Delay(1000);
            for (var waited = 0; Gl.Dressing && waited < 900_000; waited += 250) await Task.Delay(250);
            Console.WriteLine($"RESHADE to {args[re + 1]}: {before:N0} dressed before, " +
                              $"{Gl.MaterialsBound:N0} after, {Gl.NoDiffuse:N0} with NO base colour, " +
                              $"{Gl.DressFailed:N0} bind(s) threw, " +
                              $"{Gl.Refetched:N0} material(s) fetched back and {Gl.RefetchFailed:N0} of those FAILED");
            foreach (var bare in Gl.BareMeshes.Take(3)) Console.WriteLine($"RESHADE   {bare}");
        }

        // --emissive counts what the binder finds in the emissive slots WITHOUT rendering any of
        // it. A Default* texture bound as emissive is a constant glow on everything, so the
        // question "is there a feature here at all" is asked first and in numbers.
        if (args.Contains("--emissive"))
        {
            for (var waited = 0; _editor.Busy && waited < 900_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Loading && waited < 900_000; waited += 250) await Task.Delay(250);
            for (var waited = 0; Gl.Dressing && waited < 900_000; waited += 250) await Task.Delay(250);
            foreach (var line in Gl.EmissiveReport()) Console.WriteLine("EMISSIVE " + line);
        }

        // --glshots <dir> photographs the SAME frame in several shadow debug modes, in ONE process.
        //
        // Two runs of Umbara with an identical camera reported 56,211 casters and 42,794, and one
        // showed heavy shadowing while the other showed none -- the only difference between them
        // being the debug flag itself. Comparing pictures from two processes cannot settle that,
        // because the thing under test is also what differs between the processes. This takes the
        // colour frame and the debug frames back to back, with the same meshes loaded and the same
        // cascades in memory.
        var gss = Array.FindIndex(args, a => a == "--glshots");
        if (gss >= 0 && gss + 1 < args.Length)
        {
            var dir = args[gss + 1];
            System.IO.Directory.CreateDirectory(dir);
            for (var waited = 0; (_editor.Busy || Gl.Loading || Gl.Dressing) && waited < 900_000; waited += 250)
                await Task.Delay(250);
            await Task.Delay(5000);

            foreach (var mode in new[] { 0, 1, 2, 5 })
            {
                Gl.ShadowDebug = mode;
                Gl.RequestShot(System.IO.Path.Combine(dir, $"mode{mode}.png"));
                for (var waited = 0; !Gl.ShotTaken && waited < 60_000; waited += 100)
                { Gl.RequestNextFrameRendering(); await Task.Delay(100); }
            }
            Gl.ShadowDebug = 0;
        }

        // --bare: which meshes dressed and still have no base colour. "It looks untextured" has
        // three causes that look identical -- not reached yet, bound nothing, or a shader we do
        // not run -- and only the middle one is ours.
        //
        // AFTER --shading, because it has to be. Placed before it, this waited on a dress queue
        // that was empty because the mode was still Solid, and reported "0 meshes dressed" for a
        // level that dresses 1,944. That is the third time today a check has asked too early.
        if (args.Contains("--bare"))
        {
            for (var waited = 0; (_editor.Busy || Gl.Dressing || Gl.MeshesQueued > 0) &&
                                 waited < 900_000; waited += 500) await Task.Delay(500);
            await Task.Delay(1000);
            Console.WriteLine($"BARE {Gl.NoDiffuse:N0} section(s) dressed with NO base colour; " +
                              $"{Gl.MaterialsBound:N0} mesh(es) dressed, {Gl.DressFailed:N0} bind(s) threw");
            foreach (var bare in Gl.BareMeshes) Console.WriteLine($"BARE   {bare}");
            if (Gl.LastDressFail.Length > 0) Console.WriteLine($"BARE   first failure: {Gl.LastDressFail}");

            // THE BINDER ALREADY WROTE DOWN WHY. When nothing binds, BindMaterial logs either
            // "material exposes NO textures at all" or the parameter names it did not match --
            // which is the difference between a material that carries no base colour and one
            // whose base colour is under a name this lookup has never heard of. That evidence was
            // sitting in BoundLog unread; nothing in this app ever printed it.
            var why = Gl.BoundTextures
                .Where(l => l.Contains("unmatched params", StringComparison.Ordinal) ||
                            l.Contains("NO textures at all", StringComparison.Ordinal))
                .GroupBy(l => l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..])
                .OrderByDescending(g => g.Count())
                .ToList();
            Console.WriteLine($"WHY {why.Sum(g => g.Count()):N0} unbound section(s), " +
                              $"{why.Count:N0} distinct reason(s):");
            foreach (var g in why.Take(14))
                Console.WriteLine($"WHY   {g.Count(),6:N0}x  {g.Key}");
        }

        // --hittest asks AVALONIA what is under the middle of the viewport. Every camera and
        // picking test so far has called methods directly or raised events at the control, which
        // proves the maths and says nothing about whether a real mouse ever reaches it.
        if (args.Contains("--hittest"))
        {
            var centre = Gl.TranslatePoint(new Avalonia.Point(Gl.Bounds.Width / 2, Gl.Bounds.Height / 2), this);
            Console.WriteLine($"HITTEST viewport bounds {Gl.Bounds}, centre in window {centre}");
            if (centre is { } pt)
            {
                var hit = this.InputHitTest(pt);
                Console.WriteLine($"HITTEST -> {hit?.GetType().Name ?? "NOTHING"}");
                for (var v = hit as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                    Console.WriteLine($"   in {v.GetType().Name}");

                // In SCREEN pixels, so a real mouse can be driven at it without anyone guessing
                // at title-bar heights or display scaling.
                var a = Gl.PointToScreen(new Avalonia.Point(0, 0));
                var b = Gl.PointToScreen(new Avalonia.Point(Gl.Bounds.Width, Gl.Bounds.Height));
                Console.WriteLine($"HITTEST screen {a.X},{a.Y} .. {b.X},{b.Y}");
            }
        }

        // --undo / --redo press the same buttons the toolbar does, so what is tested is the
        // path a person takes.
        if (args.Contains("--undo")) { Console.WriteLine("UNDO: " + _editor.Undo()); Dump("after undo"); }
        if (args.Contains("--redo")) { Console.WriteLine("REDO: " + _editor.Redo()); Dump("after redo"); }

        // SAVING IS NOT ABOUT ONE ACTOR, so it belongs above the two early-outs below rather than
        // after them. It used to sit at the very end, which meant --save did nothing at all unless
        // the run also named a sublevel AND an actor -- and a save that silently does not happen
        // reads exactly like a save that failed.
        if (args.Contains("--save")) Console.WriteLine("SAVE: " + (_editor.Status = _editor.SaveMoves()));

        if (sublevel is null) return;
        var subNode = Find(mapNode.Children, n => n.Title.EndsWith(sublevel, StringComparison.OrdinalIgnoreCase));
        if (subNode?.Payload is not SublevelEntry s) return;
        _sublevel = s;
        await _editor.OpenSublevelAsync(s);

        if (actor is null) return;
        var actorNode = Find(_editor.ActorTree, n => n.Title.Equals(actor, StringComparison.OrdinalIgnoreCase));
        if (actorNode?.Payload is not UObject) return;
        ActorsTree.SelectedItem = actorNode;           // fires OnActorSelected, and highlights the row
        ActorsTree.ScrollIntoView(actorNode);

        // --nudge <axis> <delta> drives the property field the way a person would, through the
        // same setter, so what is tested is the real path and not a shortcut past it.
        var nudge = Array.FindIndex(args, a => a == "--nudge");
        if (nudge >= 0 && nudge + 2 < args.Length &&
            decimal.TryParse(args[nudge + 2], out var delta))
            switch (args[nudge + 1].ToUpperInvariant())
            {
                case "X": _editor.LocX += delta; break;
                case "Y": _editor.LocY += delta; break;
                case "Z": _editor.LocZ += delta; break;
                case "YAW": _editor.RotY += delta; break;
            }

    }

    /// <summary>
    /// WHAT THE VIEWPORT IS NOT DRAWING, counted.
    ///
    /// A level that looks nearly right is the hardest kind to trust. This walks every export of
    /// every sublevel of the open map and says which ones carry geometry, which ones became a
    /// placement, and what the difference is made of.
    /// </summary>
    private void Coverage()
    {
        var byPackage = _editor.Placements.GroupBy(p => p.Package)
                               .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var drawnActors = _editor.Placements.Select(p => p.Actor).ToHashSet(StringComparer.Ordinal);
        int meshed = 0, placed = 0, noLoc = 0, direct = 0, blueprint = 0, hlod = 0, instanced = 0, instances = 0;
        int outside = 0, cycles = 0, sockets = 0, absolutes = 0;
        var missingClasses = new Dictionary<string, int>(StringComparer.Ordinal);
        var sublevels = 0;

        foreach (var part in _editor.AllBrowse.GroupBy(r => r.Package))
        {
            sublevels++;
            var scope = part.First().Scope;
            placed += byPackage.GetValueOrDefault(part.Key);

            // What the shared attach-chain walk could NOT settle. These are where a prop that is
            // still in the wrong place has to come from, so they are worth naming individually
            // rather than hiding inside a total.
            var w = LevelReader.Chains(scope);
            outside += w.ParentOutside;
            cycles += w.Cycles;
            sockets += w.Sockets;
            absolutes += w.AbsoluteChannels;

            foreach (var e in scope)
            {
                var cls = e.ExportType ?? "?";
                var props = e.Properties.Select(p => p.Name.Text).ToHashSet(StringComparer.Ordinal);

                if (cls.Contains("HLOD", StringComparison.OrdinalIgnoreCase) ||
                    (e.Outer?.Name.Text ?? "").Contains("HLOD", StringComparison.OrdinalIgnoreCase))
                    hlod++;
                if (e is CUE4Parse.UE4.Assets.Exports.Component.StaticMesh.UInstancedStaticMeshComponent
                    { PerInstanceSMData: { Length: > 0 } rows })
                { instanced++; instances += rows.Length; }

                // ASK THE SAME WAY THE VIEWPORT ASKS -- through the archetype chain. Counting
                // only what the export itself serialises is exactly the mistake that made the
                // Blueprint props invisible, and a report that repeats it is not a check.
                if (LevelReader.Holder(e, "StaticMesh") is not null)
                {
                    meshed++;
                    if (props.Contains("StaticMesh")) direct++;
                    if (LevelReader.Holder(e, "RelativeLocation") is null) noLoc++;
                }
                else if (cls.EndsWith("Component", StringComparison.Ordinal) &&
                         (props.Contains("RelativeLocation") || props.Contains("AttachParent")))
                {
                    // A positioned component that resolves to no mesh. Naming the CLASS is what
                    // says whether something is missing because we do not read that kind of
                    // geometry at all -- a GeometryCollection, a skeletal mesh, a spline.
                    Bump(missingClasses, "component with no mesh: " + cls);
                }
                else if (cls.EndsWith("_C", StringComparison.Ordinal) && props.Contains("RootComponent")
                         && !drawnActors.Contains(e.Name ?? ""))
                {
                    // A Blueprint ACTOR never carries a mesh itself -- its components do. So the
                    // only honest count is of the ones that produced no placement AT ALL.
                    blueprint++;
                    Bump(missingClasses, "nothing drawn for: " + cls);
                }
            }
        }

        Console.WriteLine($"COVERAGE {sublevels} sublevel(s)");
        Console.WriteLine($"COVERAGE   {meshed,7:N0} exports resolve to a mesh (through the archetype chain)");
        Console.WriteLine($"COVERAGE   {direct,7:N0} of those name the mesh on the export itself -- the rest inherit it");
        Console.WriteLine($"COVERAGE   {placed,7:N0} drawn placements");
        Console.WriteLine($"COVERAGE   {noLoc,7:N0} have no transform of their own -- they take their parent's, which is correct");
        Console.WriteLine($"COVERAGE   {instanced,7:N0} instanced components expanded into {instances:N0} drawn instances");
        Console.WriteLine($"COVERAGE   {hlod,7:N0} HLOD proxy exports skipped -- merged stand-ins for geometry already drawn");
        Console.WriteLine($"COVERAGE   {blueprint,7:N0} Blueprint actors that produced NO placement at all");

        // THE CHECK THAT MATTERS FOR THE INHERITED ONES. A component with no transform of its
        // own is meant to sit where its attach parent puts it; if the chain does not resolve it
        // lands on the world origin instead, and thousands of props in a heap at 0,0,0 would
        // look like a fuller level in a screenshot and be nonsense.
        Console.WriteLine($"COVERAGE   {outside,7:N0} components attach to a parent in ANOTHER package -- treated as roots, so they float");
        Console.WriteLine($"COVERAGE   {sockets,7:N0} attach to a SOCKET, whose offset is not applied");
        Console.WriteLine($"COVERAGE   {cycles,7:N0} attach cycles broken, {absolutes:N0} bAbsolute channel(s) honoured");

        // THE ONES THAT LOOK LIKE THEY ARE FLYING. Not a proof of wrongness -- a map can have
        // real overhangs -- but it names them, which a screenshot cannot.
        var med = _editor.Placements.Select(p => p.At.Z).Order().ToArray();
        var ground = med.Length == 0 ? 0 : med[med.Length / 2];
        Console.WriteLine($"COVERAGE   median Z {ground:N0}; highest placements:");
        foreach (var p in _editor.Placements.OrderByDescending(p => p.At.Z).Take(12))
            Console.WriteLine($"COVERAGE      Z {p.At.Z,9:N0}  {p.Actor,-44} {p.MeshPath[(p.MeshPath.LastIndexOf('/') + 1)..],-40} {p.Sublevel}");

        var atOrigin = _editor.Placements.Count(p => p.At.Length() < 10f);
        var far = _editor.Placements.Count(p => p.At.Length() >= 10f);
        Console.WriteLine($"COVERAGE   {atOrigin,7:N0} drawn AT THE WORLD ORIGIN (chain did not resolve)");
        Console.WriteLine($"COVERAGE   {far,7:N0} drawn somewhere else");
        foreach (var (what, n) in missingClasses.OrderByDescending(kv => kv.Value).Take(12))
            Console.WriteLine($"COVERAGE      {n,6:N0}  {what}");
    }

    private static void Bump(Dictionary<string, int> d, string key) =>
        d[key] = d.GetValueOrDefault(key) + 1;

    private void Dump(string when)
    {
        // SWAPS COUNT TOO. Reporting only the moves would let an undo that failed to put a swap
        // back read as "0 pending change(s)" and pass.
        Console.WriteLine($"{when}: {_editor.Changes.Count} pending move(s), " +
                          $"{_editor.Swaps.Count} pending swap(s), " +
                          $"can undo {_editor.CanUndo}, can redo {_editor.CanRedo}");
        Console.WriteLine($"   loc {_editor.LocX:0.###}, {_editor.LocY:0.###}, {_editor.LocZ:0.###}   " +
                          $"rot {_editor.RotP:0.###}, {_editor.RotY:0.###}, {_editor.RotR:0.###}   " +
                          $"scale {_editor.SclX:0.###}, {_editor.SclY:0.###}, {_editor.SclZ:0.###}");
        foreach (var c in _editor.Changes)
            Console.WriteLine($"   {c.Actor}.{c.Path} {c.Was:0.###} -> {c.Now:0.###} [{c.Sublevel}]");
        foreach (var x in _editor.Swaps)
            Console.WriteLine($"   {x.Actor}.{x.Path} {x.Summary} [{x.Sublevel}]");

        var swapped = _editor.Placements.Where(p => p.Draws is not null).ToList();
        foreach (var p in swapped.Take(4))
            Console.WriteLine($"   DRAWN {p.Actor} now draws " +
                              $"{(p.Shown is null ? "nothing" : p.Shown[(p.Shown.LastIndexOf('/') + 1)..])}");

        var moved = _editor.Placements.Where(p => p.Offset != System.Numerics.Vector3.Zero).ToList();
        foreach (var p in moved.Take(4))
            Console.WriteLine($"   DRAWN {p.Actor} offset {p.Offset.X:0.###}, {p.Offset.Y:0.###}, {p.Offset.Z:0.###}");
        if (moved.Count == 0) Console.WriteLine("   DRAWN: nothing is offset");
    }

    private static Node? Find(IEnumerable<Node> nodes, Func<Node, bool> want)
    {
        foreach (var n in nodes)
        {
            if (want(n)) return n;
            if (Find(n.Children, want) is { } hit) return hit;
        }
        return null;
    }

    private void Shot(string[] args)
    {
        var i = Array.FindIndex(args, a => a == "--shot");
        if (i < 0 || i + 1 >= args.Length) return;
        var path = args[i + 1];

        // THIS CANNOT PHOTOGRAPH THE 3D VIEWPORT. Measured, not assumed: a capture taken with
        // 56,491 placements and 2,083 meshes loaded comes out with the viewport a black rectangle
        // and every panel around it correct.
        //
        // RenderTargetBitmap walks the Avalonia visual tree, and OpenGlControlBase does not draw
        // into it -- it hands a surface to the compositor. That is the same reason the control is
        // not hit-testable and needs a transparent FillRectangle to receive a click. So a black
        // viewport in one of these pictures says nothing at all about whether the level drew, and
        // reading it as "the level failed to draw" would be a conclusion about the screenshotting
        // rather than about the renderer. To see the level, run the editor and look at it.
        //
        // What it IS good for is the panels: the browser, the property panel, the pending-changes
        // dock. Those fill in as the map streams, so the wait below is still worth having --
        // `--shot <path> [waitMs]` waits for the map read and the mesh loading, up to waitMs,
        // rather than guessing a delay. Default 5,000 keeps the old behaviour for a window that
        // loads nothing.
        var budget = i + 2 < args.Length && !args[i + 2].StartsWith("--") &&
                     int.TryParse(args[i + 2], out var ms) ? ms : 5000;

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var waited = 0;
            while ((_editor.Busy || Gl.Loading) && waited < budget) { await Task.Delay(250); waited += 250; }
            await Task.Delay(800);                  // and let one full frame land
            Console.WriteLine($"SHOT waited {waited:N0}ms, busy={_editor.Busy} loading={Gl.Loading}");
            if (ReferenceEquals(Tabs.SelectedItem, ViewTab))
                Console.WriteLine("SHOT the 3D viewport comes out BLACK in these captures -- it is " +
                                  "composited, not drawn into the visual tree. That is not the " +
                                  "level failing to draw.");
            try
            {
                var size = new PixelSize((int)Root.Bounds.Width, (int)Root.Bounds.Height);
                using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
                bmp.Render(Root);
                bmp.Save(path);
                Console.WriteLine($"wrote {path}  {size.Width}x{size.Height}");
            }
            catch (Exception e) { Console.WriteLine("shot failed: " + e); }
            Environment.Exit(0);
        });
    }
#endif
}

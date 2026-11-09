using System.Collections.ObjectModel;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A row of the animation panel — either a heading or a sequence.
///
/// TWO RECORD TYPES RATHER THAN A FLAG, so the list has one DataTemplate per shape and the
/// template selection is the type. A bool plus two IsVisible bindings puts both shapes in every
/// row and lets them disagree.
/// </summary>
public abstract record AnimListItem;

/// <summary>
/// A CATEGORY HEADING, from where the game keeps the sequence — 2HRifle, 1HPistol, Unarmed,
/// Cinematics.
///
/// The torso's rig can play 2,720 of them, and the library's own note says what that means
/// without headings: "a flat list of 2,720 names is not a list, it is a search box with
/// scenery". The game has already sorted them and the folder tree says so.
/// </summary>
public sealed record AnimHeading(string Text, int Count, bool Closed) : AnimListItem
{
    public string Label => $"{Text}  ({Count})";

    public Avalonia.Media.Geometry Chevron => Chevrons.For(!Closed);
}

/// <summary>
/// AN ACTION INSIDE A CATEGORY -- the second heading level, and the one that makes a category
/// navigable rather than merely labelled.
///
/// A category on its own was not enough: this rig's 1HPistol set is 998 sequences in ONE flat
/// folder, so "grouped" meant a single heading over a list nobody can scan. The action is read
/// out of the name by <see cref="AnimationLibrary.ActionOf"/>.
///
/// CLOSED IS CARRIED HERE BUT OWNED BY THE TAB. The records are rebuilt whenever the list is
/// refilled, so a closed group would spring open on every rebuild if the flag lived only in the
/// row; <see cref="MeshTab"/> keeps the set of closed keys and stamps it back on.
/// </summary>
public sealed record AnimGroup(string Key, string Text, int Count, bool Closed) : AnimListItem
{
    public string Label => $"{Text}  ({Count})";

    /// <summary>The same two glyphs the Data Explorer's tree uses, so they read the same.</summary>
    public Avalonia.Media.Geometry Chevron => Chevrons.For(!Closed);
}

/// <summary>
/// ONE MESH HUNG ON THE LOADED ONE -- a helmet on a torso.
///
/// <paramref name="Fits"/> is the bone overlap MEASURED after it was built, not the guess the
/// picker made from its name. The list offers candidates on the game's SK_&lt;rig&gt;_ naming, which
/// is a convention and nothing stronger; this is the answer.
/// </summary>
public sealed record AttachedRow(string Key, string Name, string Fits);

/// <summary>One animation this mesh can play.</summary>
public sealed record AnimRow(string Name, string Category, int Frames, float Duration, string Path)
    : AnimListItem
{
    /// <summary>Frames and seconds, which is what tells a loop from a one-shot at a glance.</summary>
    public string Detail => $"{Frames} f · {Duration:0.0}s";
}

/// <summary>
/// A MODEL, OPEN AS A TAB, with the viewport in it.
///
/// The control is held here rather than created by the template because it owns a GL context and
/// a loaded mesh: rebuilding it every time the tab is switched away from and back would re-read
/// the package and re-upload the buffers to show the same thing again.
/// </summary>
public sealed class MeshTab : StudioTab
{
    private readonly string _filePath;
    private string _status = "reading the package…";

    public MeshTab(string title, string filePath, string type)
    {
        _title = title;
        _filePath = filePath;
        Type = type;
        Props = new AssetTab(title, filePath, type);
        View.Loaded += () => Report();
        // The picker follows what was actually BUILT, not what was asked for. A requested LOD the
        // asset does not ship falls back to the default, and a dropdown still showing the request
        // would be the one thing on screen insisting it happened.
        // The readout follows the frame that was drawn, not a timer — the render loop is the
        // clock, so this is the only thing that knows where the animation really is.
        View.Ticked += () =>
        {
            if (View.AnimFrames <= 0) return;
            PlayNote = $"{View.AnimName}  ·  frame {View.AnimFrame:0} / {View.AnimFrames}";

            // THE SLIDER FOLLOWS THE ANIMATION, and _scrubbing guards the other direction: the
            // setter below moves the player, so a tick that moved the slider would look like a
            // drag and pause playback on its own first frame.
            _scrubbing = true;
            Raise(nameof(Frame));
            Raise(nameof(Frames));
            Raise(nameof(HasAnim));
            _scrubbing = false;
        };
        // ON THE GL THREAD, so it has to be marshalled: ObservableCollection raises
        // CollectionChanged straight through to the bindings, and Avalonia will not have it from
        // anywhere but the UI thread.
        View.AttachedChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshAttached);
        View.LodChanged += () =>
        {
            Lods.Clear();
            foreach (var i in View.LodsOffered) Lods.Add(i);
            Raise(nameof(Lod));
            Raise(nameof(HasLods));
        };
    }

    private readonly string _title;
    public override string Title => Mine is null ? _title : _title + "  (your import)";

    /// <summary>The asset's own name, without "(your import)" -- for file names, dialogs and the log.</summary>
    public string BaseTitle => _title;
    public override string Tooltip => _filePath;
    public string Type { get; }
    public string FilePath => _filePath;

    /// <summary>
    /// The author's FBX this tab is showing OVER the game's mesh, or null when it shows the game's own -- set by the view
    /// model on open, after Import and after Revert. A tab that went on showing the game's mesh after an import the
    /// project had recorded is what "Import does nothing" was.
    /// </summary>
    public string? Mine { get; private set; }

    /// <summary>Say which the viewport now holds; the view model has already handed it the file or the asset.</summary>
    public void MarkMine(string? file)
    {
        if (string.Equals(Mine, file, StringComparison.OrdinalIgnoreCase)) return;
        Mine = file;
        Raise(nameof(Title));
    }

    /// <summary>
    /// The mesh PACKAGE this tab stands for, when it was opened from a part's mesh row.
    ///
    /// Keyed on the package rather than the file path so that viewing the game's mesh and
    /// viewing the author's replacement of it are the SAME tab — which is what makes "import
    /// textures, tab back, see them" work instead of opening a second viewport.
    /// </summary>
    public string MeshKey { get; init; } = "";

    /// <summary>Which artwork version this viewport was last drawn against. See StudioVm.Tab.</summary>
    public int ArtVersion { get; set; }

    /// <summary>The viewport itself. One per tab, kept for the life of the tab.</summary>
    public ModelView View { get; } = new();

    /// <summary>The loaded mesh object, kept so Export has something to write.</summary>
    public UObject? Asset { get; private set; }

    /// <summary>
    /// The sequence currently loaded, if any — the thing a second export writes.
    ///
    /// The UObject, not AnimPlayer's converted CAnimSequence: the exporter wants the asset.
    /// </summary>
    public UAnimSequence? PlayingAnim { get; set; }

    /// <summary>
    /// THE MESH'S OWN PROPERTIES, read by an AssetTab held inside this one.
    ///
    /// Not a second property reader. An AssetTab already owns the whole of this behaviour — the
    /// tree, the filter, per-row expansion that rebuilds the flat list, the status line — and
    /// every one of those would otherwise be written again slightly differently. The reference
    /// shows the same thing: a model open in the viewport with the asset's property grid beside
    /// it, not instead of it.
    /// </summary>
    public AssetTab Props { get; }

    /// <summary>Called once the property grid is filled, so a mod's own edits can be laid over it.</summary>
    public Action<AssetTab>? PropsFilled { get; set; }

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The dropdown's rows — the shader's own modes, not a list kept beside them.</summary>
    public static ModelView.ViewMode[] Modes => ModelView.Modes;

    /// <summary>
    /// The ground grid and the one-metre reference box. FORWARDED, not mirrored: the view is
    /// what the render loop reads every frame, and a second copy here could disagree with it.
    /// </summary>
    public bool ShowGrid
    {
        get => View.ShowGrid;
        set { View.ShowGrid = value; Raise(nameof(ShowGrid)); }
    }

    public bool ShowFloor
    {
        get => View.ShowFloor;
        set { View.ShowFloor = value; Raise(nameof(ShowFloor)); }
    }

    /// <summary>
    /// Forwarded to the view, which holds the truth because the shader reads it from there every
    /// frame. Deliberately not a second copy of the answer.
    /// </summary>
    public ModelView.ViewMode Mode
    {
        get => View.Mode;
        set { View.Mode = value; Raise(nameof(Mode)); }
    }

    /// <summary>The animations this mesh's rig can drive, grouped by the game's own folders.</summary>
    public ObservableCollection<AnimListItem> Animations { get; } = new();

    /// <summary>
    /// THE WHOLE LIST, kept so collapsing a group is a rebuild rather than a re-read.
    ///
    /// Re-deriving it would mean matching rigs and walking the library again -- seconds of work
    /// for a chevron. <see cref="Animations"/> is the VISIBLE projection of this.
    /// </summary>
    private readonly List<(string Category, string Key, string Action, List<AnimRow> Rows)> _animSets = new();

    /// <summary>
    /// Groups the author has closed, BY KEY, surviving every rebuild.
    ///
    /// His rule for the section lists, applied here for the same reason: a list that forgets what
    /// you folded away is a list you fold away again every time you touch it.
    /// </summary>
    private readonly HashSet<string> _closedAnims = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SETS the author has folded away -- the outer level, and the one that makes this tab
    /// readable at all: six rigs fit a torso and between them they carry 2,720 sequences in 196
    /// action groups, so the useful first view is the handful of SET names, not any of that.
    ///
    /// Kept apart from <see cref="_closedAnims"/> rather than sharing one set with prefixed keys.
    /// The two are folded and unfolded by different buttons and asked different questions, and
    /// one collection answering both would have to be filtered at every use.
    /// </summary>
    private readonly HashSet<string> _closedCats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets already seeded as folded, so a refill does not re-fold what was opened.</summary>
    private readonly HashSet<string> _seededCats = new(StringComparer.OrdinalIgnoreCase);

    private string _animNote = "waiting for the mesh…";
    public string AnimNote { get => _animNote; private set => Set(ref _animNote, value); }

    // 1 is Animations: Properties, Animations, Materials. Opening there is the author's ask —
    // it is the tab with something to DO in it, where Properties is something to read.
    private int _sidePanel = 1;

    /// <summary>
    /// Which side tab is showing. HELD ON THE TAB, not left to the control.
    ///
    /// The panel is built from a DataTemplate, so switching to another document tab and back
    /// recreates it — and a plain SelectedIndex would snap back to Animations every time,
    /// throwing away the Properties page somebody was halfway down. Kept here, it opens where he
    /// asked and then stays where it was put.
    /// </summary>
    public int SidePanel { get => _sidePanel; set => Set(ref _sidePanel, value); }

    private AnimListItem? _pickedAnim;

    /// <summary>
    /// The row the list has selected. ROWS ONLY: a heading is not playable, so one arriving here
    /// is ignored rather than clearing what is playing.
    ///
    /// THE FOLD NO LONGER COMES THROUGH HERE, and that is the author's "once you collapse them
    /// and then reopen you can't collapse it again".
    ///
    /// A selection is the wrong channel for a fold, twice over. A ListBox raises it only when the
    /// selection CHANGES, so a second click on a row it already regards as selected sends nothing
    /// at all. And it PUSHES a selection back when a container is realised, which a setter cannot
    /// tell from a click. The second bought a latch to paper over it, and the latch then ATE REAL
    /// CLICKS: it was released at DispatcherPriority.Background, which is strictly below Input, so
    /// a click already queued when the list refilled was dispatched FIRST, found the latch up and
    /// was discarded with no trace. That is the "sometimes the arrow doesn't open or collapse"
    /// half of the same report, and it is why the latch is gone rather than re-prioritised.
    ///
    /// The headings carry their own Button now, which raises Click on every release whether or not
    /// the row was already selected; keyboard folds through the list's own key handler. Neither
    /// can be swallowed by a race, and with the fold off this channel a realisation push-back is
    /// simply not an AnimRow and falls out of the bottom.
    /// </summary>
    public AnimListItem? PickedAnim
    {
        get => _pickedAnim;
        set { if (value is AnimRow) Set(ref _pickedAnim, value); }
    }

    private string _playNote = "";

    /// <summary>What is playing, and where it has got to.</summary>
    public string PlayNote { get => _playNote; private set => Set(ref _playNote, value); }

    public void SetPlayNote(string note) => PlayNote = note;

    /// <summary>Pause or resume without forgetting the pose, so one frame can be looked at.</summary>
    public void TogglePlay() => View.TogglePlay();

    /// <summary>Whether it runs round again at the end. Off is how anything that happens ONCE gets seen.</summary>
    public bool Loop
    {
        get => View.Loop;
        set { View.Loop = value; Raise(nameof(Loop)); }
    }

    /// <summary>0.05x to 3x. Slow is how you tell whether a swing actually connects.</summary>
    public double Speed
    {
        get => View.Speed;
        set { View.Speed = (float) value; Raise(nameof(Speed)); Raise(nameof(SpeedSays)); }
    }

    public string SpeedSays => $"{View.Speed:0.00}x";

    /// <summary>Whether there is anything to drive — the transport is dead without a sequence.</summary>
    public bool HasAnim => View.HasAnim;

    public int Frames => Math.Max(View.AnimFrames - 1, 0);

    /// <summary>
    /// Where it has got to, and where to put it.
    ///
    /// SETTING IT PAUSES, which is what dragging a scrub bar means everywhere: the frame you
    /// dragged to is the frame you want to look at, and a player that carried on would take it
    /// away again before you had seen it.
    /// </summary>
    public double Frame
    {
        get => View.AnimFrame;
        set
        {
            if (_scrubbing) return;
            View.PoseAt((float) value);
            Raise(nameof(Frame));
        }
    }
    private bool _scrubbing;

    /// <summary>Back to the bind pose.</summary>
    public void StopAnim()
    {
        View.StopAnimation();
        PlayNote = "";
        Raise(nameof(HasAnim));
        Raise(nameof(Frames));
        Raise(nameof(Frame));
    }

    public void SetAnimations(
        IReadOnlyList<(string Category, string Key, string Action, List<AnimRow> Rows)> sets,
        string note)
    {
        _animSets.Clear();
        _animSets.AddRange(sets);

        // A SET LANDS FOLDED THE FIRST TIME IT IS SEEN, and only the first time. The tab opens on
        // the list of sets rather than on two thousand rows -- which is the view that was asked
        // for -- while a set the author has opened stays open across every later refill.
        foreach (var cat in _animSets.Select(s => s.Category)
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
            if (_seededCats.Add(cat)) _closedCats.Add(cat);

        ShowAnimations();
        AnimNote = note;
        Raise(nameof(AnimsClosed));
        Raise(nameof(FoldWord));
    }

    /// <summary>
    /// Flatten the model into the visible list: category heading, action heading, rows.
    ///
    /// A CATEGORY HEADING ONLY WHERE IT CHANGES, so a rig that fits one set does not get a
    /// heading over every group repeating the same word. The action heading is always drawn,
    /// because it is the thing being collapsed and there has to be something to click.
    /// </summary>
    private void ShowAnimations()
    {
        Animations.Clear();
        var category = "";
        var catClosed = false;
        foreach (var (cat, key, action, rows) in _animSets)
        {
            if (!cat.Equals(category, StringComparison.OrdinalIgnoreCase))
            {
                catClosed = _closedCats.Contains(cat);
                Animations.Add(new AnimHeading(cat, _animSets
                    .Where(s => s.Category.Equals(cat, StringComparison.OrdinalIgnoreCase))
                    .Sum(s => s.Rows.Count), catClosed));
                category = cat;
            }

            // A FOLDED SET TAKES ITS GROUPS WITH IT. Leaving the action headings visible would
            // be a half-fold that saves nothing -- 196 of them is most of the length.
            if (catClosed) continue;

            var closed = _closedAnims.Contains(key);
            Animations.Add(new AnimGroup(key, action, rows.Count, closed));
            if (closed) continue;
            foreach (var r in rows) Animations.Add(r);
        }
    }

    /// <summary>
    /// The visible list in one string, for a driver flag to READ BACK what is on screen.
    ///
    /// A fold is invisible to every other check: the project does not change, nothing is written,
    /// and the only witness is a picture. This is the witness that is not a picture.
    /// </summary>
    /// <summary>How many sequences this tab holds, folded or not — what a reader waits on.</summary>
    public int AnimTotal => _animSets.Sum(s => s.Rows.Count);

    /// <summary>The set names, in the order they are shown.</summary>
    public IEnumerable<string> SetNames =>
        _animSets.Select(s => s.Category).Distinct(StringComparer.OrdinalIgnoreCase);

    public string DumpAnimations(int take) =>
        string.Join(Environment.NewLine, Animations.Take(take).Select(i => i switch
        {
            AnimHeading h => $"SET   {(h.Closed ? "[+]" : "[-]")} {h.Label}",
            AnimGroup g   => $"  GRP {(g.Closed ? "[+]" : "[-]")} {g.Label}",
            AnimRow r     => $"      {r.Name}",
            _             => "?",
        })) + Environment.NewLine +
        // THE NOTE TOO, because it carries the RIG COUNT and the total varies between runs of
        // the same mesh -- 2,061 / 2,393 / 2,720 measured on one torso. Without the rig count a
        // reader cannot tell "fewer rigs matched" from "the same rigs carried fewer sequences",
        // and those are different faults.
        $"({Animations.Count} visible of {_animSets.Sum(s => s.Rows.Count)} row(s))" +
        Environment.NewLine + $"note: {AnimNote}";

    /// <summary>Fold a whole set away, or open it.</summary>
    public void ToggleAnimCategory(string category)
    {
        if (!_closedCats.Remove(category)) _closedCats.Add(category);
        ShowAnimations();
        Raise(nameof(AnimsClosed));
        Raise(nameof(FoldWord));
    }

    /// <summary>
    /// Fold a group away, or open it. The selection is NOT moved -- clicking a heading is
    /// navigation, and losing the animation you had picked to do it would be a poor trade.
    /// </summary>
    public void ToggleAnimGroup(string key)
    {
        if (!_closedAnims.Remove(key)) _closedAnims.Add(key);
        ShowAnimations();
        // FOLDING THE LAST ONE BY HAND completes the set, and the button has to agree -- a
        // control that contradicts what is on screen beside it is worse than no control.
        Raise(nameof(AnimsClosed));
        Raise(nameof(FoldWord));
    }

    /// <summary>
    /// Fold every SET away, or open them all.
    ///
    /// THE OUTER LEVEL, not the inner one. Folding the 196 action groups still leaves 196 rows on
    /// screen; folding the sets leaves the handful of names the author asked to see -- "i wanna
    /// collapse 1HPistol and the other animation sets so i can see them all". The inner state is
    /// left alone, so opening a set again finds it as it was left.
    /// </summary>
    public void AllAnimGroups(bool closed)
    {
        _closedCats.Clear();
        if (closed)
            foreach (var c in _animSets.Select(s => s.Category)
                                       .Distinct(StringComparer.OrdinalIgnoreCase))
                _closedCats.Add(c);
        ShowAnimations();
        Raise(nameof(AnimsClosed));
        Raise(nameof(FoldWord));
    }

    /// <summary>Whether every set is folded, for the button to say which way it goes.</summary>
    public bool AnimsClosed
    {
        get
        {
            var cats = _animSets.Select(s => s.Category)
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return cats.Count > 0 && cats.All(_closedCats.Contains);
        }
    }

    /// <summary>
    /// The button's word. A PROPERTY RATHER THAN A CONVERTER -- one line here against a class,
    /// a resource entry and a binding that reads backwards, for the same two words.
    /// </summary>
    public string FoldWord => AnimsClosed ? "Open all" : "Fold all";

    // ------------------------------------------------------------------ attached meshes

    /// <summary>The name of the mesh this tab was opened on, for the Meshes tab to head with.</summary>
    public string LoadedName => Title;

    /// <summary>What is hung on it. A VIEW of ModelView's list, rebuilt when that changes.</summary>
    public ObservableCollection<AttachedRow> AttachedMeshes { get; } = new();

    public bool HasAttached => AttachedMeshes.Count > 0;

    /// <summary>
    /// Re-read what the viewport is actually holding.
    ///
    /// READ BACK, never remembered from the act that asked for it: an attach can fail on the GL
    /// thread -- no geometry, a package that will not read -- and a row added optimistically here
    /// would be a helmet the panel insists is on a model that has never had one.
    /// </summary>
    public void RefreshAttached()
    {
        AttachedMeshes.Clear();
        foreach (var (key, name, shared, total) in View.Attached)
            AttachedMeshes.Add(new AttachedRow(
                key, name,
                total == 0 ? "no rig of its own"
                           : shared == 0 ? "shares no bones with this rig — it will not follow it"
                                         : $"{shared}/{total} bones"));
        Raise(nameof(HasAttached));
    }

    /// <summary>The material slots and which maps reached them. Filled when the mesh builds.</summary>
    public ObservableCollection<ModelView.MaterialRow> Materials { get; } = new();

    // ------------------------------------------------------------------ tint

    /// <summary>The game's own colours, handed over once the palette has been read.</summary>
    public ObservableCollection<TintPalette.Swatch> Swatches { get; } = new();

    public void SetSwatches(IReadOnlyList<TintPalette.Swatch> all)
    {
        if (Swatches.Count > 0) return;
        foreach (var s in all) Swatches.Add(s);
    }

    /// <summary>
    /// Whether this model paints through a tint mask at all.
    ///
    /// The picker HIDES when it does not. Offering the game's colours on a mesh whose material
    /// names no `Tint Mask VT` is a control that looks broken rather than one that is absent —
    /// and plenty of meshes here are exactly that.
    /// </summary>
    public bool CanTint => View.CanTint;

    /// <summary>Which sections own a tint mask. See <see cref="ModelView.TintCensus"/>.</summary>
    public string TintCensus => View.TintCensus;

    /// <summary>
    /// Whether the model casts a shadow onto the slab. His toggle.
    ///
    /// A SHADOW WITH NOTHING UNDER IT IS INVISIBLE, so turning this on turns the floor on too --
    /// the slab is the only thing in the scene for a shadow to land on, and a toggle that
    /// appears to do nothing is worse than one that is off.
    /// </summary>
    public bool ShowShadows
    {
        get => View.ShowShadows;
        set
        {
            View.ShowShadows = value;
            if (value && !View.ShowFloor) { View.ShowFloor = true; Raise(nameof(ShowFloor)); }
            Raise(nameof(ShowShadows));
        }
    }

    /// <summary>Ask the GL thread to count the uploaded tint mask's channels.</summary>
    public void AskMaskChannels() => View.AskMaskChannels();

    /// <summary>What that count found.</summary>
    public string MaskReport => View.MaskReport;

    /// <summary>What each section has to shade with. See <see cref="ModelView.ShadingCensus"/>.</summary>
    public string ShadingCensus => View.ShadingCensus;

    /// <summary>What the last tinted draw actually bound, per section.</summary>
    public string TintDraw => UEToolkit.App.Rendering.RenderMesh.LastTintDraw;

    private TintPalette.Swatch? _c1, _c2, _c3;

    // Colour 01, 02 and 03 -- Primary, Secondary, Tertiary. The mask's red, green and blue pick
    // them in that order; the game's own T_DefaultTintMSK has the words painted into the channels.
    public TintPalette.Swatch? Colour1
    {
        get => _c1;
        set { if (Set(ref _c1, value) && value is not null) View.SetTint(0, value.Rgb); }
    }

    public TintPalette.Swatch? Colour2
    {
        get => _c2;
        set { if (Set(ref _c2, value) && value is not null) View.SetTint(1, value.Rgb); }
    }

    public TintPalette.Swatch? Colour3
    {
        get => _c3;
        set { if (Set(ref _c3, value) && value is not null) View.SetTint(2, value.Rgb); }
    }

    /// <summary>Back to the artwork as the game ships it.</summary>
    public void ClearTint()
    {
        _c1 = _c2 = _c3 = null;
        Raise(nameof(Colour1)); Raise(nameof(Colour2)); Raise(nameof(Colour3));
        View.ClearTint();
    }

    // ------------------------------------------------------------------ LOD

    /// <summary>The LOD indices this mesh ships. Usually one — a Nanite mesh has no chain.</summary>
    public ObservableCollection<int> Lods { get; } = new();

    /// <summary>Hide the picker on a mesh with nothing to pick.</summary>
    public bool HasLods => Lods.Count > 1;

    public int Lod
    {
        get => View.Lod;
        set { View.ShowLod(value); Raise(nameof(Lod)); }
    }

    /// <summary>
    /// Read the asset OFF THE UI THREAD, then hand the loaded object to the viewport.
    ///
    /// The split matters: the package read is slow and has to be gated — CUE4Parse's cache is not
    /// safe to race on and the failure is a native AccessViolationException .NET cannot catch —
    /// while the GEOMETRY has to be built on the render thread, because that is where the GL is.
    /// AssetLoader.Load holds the gate; ModelView.Show does the rest on its next frame.
    /// </summary>
    public async Task LoadAsync(IFileProvider provider)
    {
        var path = _filePath;

        var asset = await Task.Run<UObject?>(() =>
        {
            try
            {
                return AssetLoader.Load<USkeletalMesh>(provider, path)
                       ?? (UObject?) AssetLoader.Load<UStaticMesh>(provider, path);
            }
            catch (Exception) { return null; }
        });

        if (asset is null)
        {
            Status = "could not read a mesh out of that package";
            return;
        }

        Asset = asset;
        Status = "building the geometry…";
        // THE AUTHOR'S IMPORT, if it went up while the package was being read, stays on screen; the asset is kept for
        // Export, the properties and a Revert.
        if (Mine is null) View.Show(asset);

        // THE PROPERTIES, READ SEPARATELY AND OFF THE UI THREAD. The viewport wants the mesh
        // object; the grid wants the package's exports and their values, which is a different
        // read of the same file. Done after the geometry is handed over so the picture arrives
        // first — a property grid nobody has looked at yet is not worth delaying a model for.
        var read = await Task.Run(() => AssetProps.Read(provider, path));
        Props.Fill(read);

        // AND WHATEVER THIS MOD HAS ALREADY CHANGED, laid over it. A MeshTab knows nothing about
        // a mod — deliberately, it is a viewport — so the view model hands it this when it makes
        // one. Without it the Properties panel beside the model showed the game's numbers with
        // the author's own edits invisible, which is the same fault the asset reader had.
        PropsFilled?.Invoke(Props);
    }

    /// <summary>
    /// What the viewport ended up with, said plainly.
    ///
    /// SECTIONS AND HOW MANY GOT TEXTURES, because "it drew" and "it drew correctly" are
    /// different claims and only the second one is worth making. A model whose sections are all
    /// grey has loaded and is still wrong, and the count is what tells them apart.
    /// </summary>
    private void Report()
    {
        if (View.Fault is { Length: > 0 } fault && View.SectionCount == 0)
        { Status = fault; return; }

        var textured = View.TexturedSections == View.SectionCount
            ? "all textured"
            : $"{View.TexturedSections} of {View.SectionCount} textured";

        Status = View.SectionCount == 0
            ? "nothing to draw"
            : $"{View.SectionCount} section(s)   ·   {textured}"
              + (View.Fault is { Length: > 0 } f ? $"   ·   {f}" : "");

        Materials.Clear();
        foreach (var m in View.Materials) Materials.Add(m);
        Raise(nameof(CanTint));
    }
}

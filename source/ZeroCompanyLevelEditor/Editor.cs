using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using UEToolkit.Core;

namespace ZeroCompanyLevelEditor;

public abstract class Obs : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

/// <summary>One row in either tree: a title, an optional right-hand detail, an optional dot.</summary>
public sealed class Node : Obs
{
    public string Title { get; init; } = "";
    public string Class { get; init; } = "";
    public string Mesh { get; init; } = "";
    public IBrush? Dot { get; init; }
    public object? Payload { get; init; }
    public ObservableCollection<Node> Children { get; } = new();

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private bool _expanded;
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    private bool _edited;
    public bool Edited { get => _edited; set => Set(ref _edited, value); }

    /// <summary>
    /// THE SUBLEVEL THIS ROW DRAWS, or empty for a map or a group.
    ///
    /// A string rather than `Payload is SublevelEntry`, because half of what a map draws is not a
    /// streamed sublevel at all. `Hub_Environment_Hangar_StillwatchLAATs` -- the parked gunships --
    /// is a LEVEL INSTANCE, followed recursively by the placement reader and never listed among
    /// the 60 the root says it streams. Keying the tick on the payload would have shipped a
    /// feature that could not turn off the one thing he asked about.
    /// </summary>
    public string SublevelName { get; init; } = "";
    public bool IsSublevel => SublevelName.Length > 0;

    private bool _shown = true;
    public bool Shown { get => _shown; set => Set(ref _shown, value); }

    public bool HasDot => Dot is not null;
}

public sealed class LayerChip : Obs
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public IBrush? Dot { get; init; }
    public string Label => $"{Name} {Count}";

    private bool _on = true;
    public bool IsOn { get => _on; set => Set(ref _on, value); }
}

/// <summary>
/// ONE EDIT THE AUTHOR HAS MADE AND NOT YET SAVED.
///
/// It carries the TARGET PACKAGE as well as the actor, because a map is several sublevels and a
/// person moves things in more than one of them before saving. Saving groups by target and writes
/// one override per package -- which is exactly the shape the build already reads.
/// </summary>
public sealed class PendingMove : Obs
{
    public string Target { get; init; } = "";
    public string Sublevel { get; init; } = "";
    public string Actor { get; init; } = "";

    /// <summary>
    /// HOW THE BUILD FINDS THIS ACTOR -- the name when it is unique in the package, "#427" when
    /// it is not.
    ///
    /// Separate from <see cref="Actor"/>, which is what a person reads. UE reuses export names
    /// freely: HUB_Environment_Pit holds THREE exports called BitReactorStaticMeshActor_5, and a
    /// move filed against that name is refused at build time with nothing said in the editor.
    /// Keeping both means the dock stays readable and the edit still lands.
    /// </summary>
    public string Address { get; init; } = "";

    public string Path { get; init; } = "";
    public double Was { get; init; }

    private double _now;
    public double Now { get => _now; set { if (Set(ref _now, value)) Raise(nameof(Summary)); } }

    private bool _saved;
    public bool Saved { get => _saved; set => Set(ref _saved, value); }

    public string Field => Path[(Path.LastIndexOf('.') + 1)..];
    public string Summary => $"{Was:0.###} -> {Now:0.###}";
    public string Key => Target + "|" + Actor + "|" + Path;

    public PendingMove Copy() => new()
    {
        Target = Target, Sublevel = Sublevel, Actor = Actor, Address = Address,
        Path = Path, Was = Was, Now = Now, Saved = Saved,
    };
}

/// <summary>
/// WHAT AN ACTOR DRAWS, changed and not yet saved -- or removed entirely.
///
/// The other half of "delete this and put that there", and the same shape of edit as a
/// <see cref="PendingMove"/>: four bytes for four bytes, so the package never grows. See
/// ModOverride.Swap for why a cooked .umap does not have to.
///
/// <see cref="Now"/> empty means NOTHING: the component draws no mesh at all, which is how an
/// actor is removed from a shipped level.
/// </summary>
public sealed class PendingSwap : Obs
{
    public string Target { get; init; } = "";
    public string Sublevel { get; init; } = "";
    public string Actor { get; init; } = "";
    public string Address { get; init; } = "";
    public string Path { get; init; } = "";
    public string Was { get; init; } = "";

    private string _now = "";
    public string Now { get => _now; set { if (Set(ref _now, value)) Raise(nameof(Summary)); } }

    private bool _saved;
    public bool Saved { get => _saved; set => Set(ref _saved, value); }

    public string Field => Path[(Path.LastIndexOf('.') + 1)..];
    public string Summary => $"{Was} -> {(Now.Length == 0 ? "nothing" : Now)}";
    public string Key => Target + "|" + Address + "|" + Path;

    public PendingSwap Copy() => new()
    {
        Target = Target, Sublevel = Sublevel, Actor = Actor, Address = Address,
        Path = Path, Was = Was, Now = Now, Saved = Saved,
    };
}

/// <summary>
/// ONE UNDO STEP: the whole pending-move set as it stood BEFORE a change.
///
/// A move already records was -> now, so a step needs nothing cleverer than a copy of the list,
/// and undoing is putting the old list back. Replaying individual deltas in reverse would be
/// smaller and would let the dock, the fields and the picture drift apart; a snapshot cannot.
/// </summary>
public sealed record UndoStep(string Label, List<PendingMove> Moves, List<PendingSwap> Swaps,
                              List<PendingSpawn> Spawns);

public sealed class ClassRow
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string Layer { get; init; } = "";
    public IBrush? Dot { get; init; }
}

/// <summary>
/// One thing in a level worth listing, with the three names a person needs: what it is CALLED
/// (the actor, not its component), what CLASS it is, and what MESH it wears.
/// </summary>
public sealed record ActorRow(UObject Export, string Layer, string Name, string Class, string Mesh);

/// <summary>
/// One line of the LEVEL BROWSER: something placeable anywhere in the open map, not just in the
/// sublevel the tree happens to be showing.
///
/// It carries its own scope and package because that is what decides which file a move is
/// written into -- a map is eleven sublevels merged, and filing an override against the one the
/// tree has open is an override the build accepts and the game ignores.
/// </summary>
public sealed record BrowseRow(UObject Export, string Name, string Class, string Mesh,
                               string Layer, string Sublevel, string Package, UObject[] Scope,
                               bool Movable)
{
    public IBrush? Dot => Layers.For(Layer);

    /// <summary>The mesh if it wears one, else the class -- whichever actually names the thing.</summary>
    public string What => Mesh.Length > 0 ? Mesh : Class;
}

/// <summary>
/// ONE ASSET THE LEVEL LOADS, however many times it is placed.
///
/// The level browser lists ACTORS -- BitReactorStaticMeshActor_44, four hundred of them, named
/// after the machinery rather than after anything anyone can see. This lists the other axis: the
/// meshes themselves, once each, which is the vocabulary somebody actually shops in. "Give me
/// another one of those crates" is a sentence about an asset, not about an actor.
///
/// <paramref name="Users"/> is every placement of it, which is also where its DONOR comes from:
/// copying an actor that already draws this mesh means the copy needs no import at all.
/// </summary>
public sealed record AssetRow(string Path, string Name, string Folder, int Count, string Where,
                              List<LevelReader.Placement> Users)
{
    /// <summary>
    /// FALSE FOR SOMETHING FROM ANOTHER MAP.
    ///
    /// An asset the level already places has an actor to copy that already draws it, so a spawn
    /// of it needs no import. One from elsewhere in the game needs a donor plus an import -- the
    /// route the Umbaran plant took into the hub -- and needs checking that the package really
    /// holds a mesh of its own name, because the index classifies by path and a FoliageType data
    /// asset sitting beside the meshes it references reads as a mesh.
    /// </summary>
    public bool InLevel => Count > 0;

    /// <summary>Both facts on one line, for the place bar. The row shows them separately.</summary>
    public string Detail => !InLevel ? Where
                          : Count == 1 ? "once  ·  " + Where
                          : $"{Count:N0} placed  ·  {Where}";

    public string Tally => !InLevel ? "" : Count == 1 ? "once" : $"{Count:N0} placed";
}

/// <summary>
/// ONE ROW OF THE ASSET TREE: a folder, or an asset sitting in one.
///
/// A flat list of two thousand assets is a list where four hundred consecutive rows are
/// `Dropship_hatch_front_lower_r_piston_back_upper_geo` and its neighbours, and finding anything
/// else means scrolling past all of them. The folder a mesh lives in is the game's own taxonomy
/// and is already the best answer to "what kind of thing is this" -- so it is the tree.
/// </summary>
public sealed class AssetTreeNode : Obs
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>The second line: the sublevel for an asset, and nothing for a folder.</summary>
    public string Sub { get; init; } = "";

    /// <summary>Null on a folder. Clicking a folder opens it; clicking an asset arms it.</summary>
    public AssetRow? Asset { get; init; }
    public bool IsAsset => Asset is not null;

    public ObservableCollection<AssetTreeNode> Children { get; } = new();

    private bool _expanded;
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

/// <summary>
/// ONE STORY FACT THIS LEVEL IS GATED ON, as a switch.
///
/// The author's ask, verbatim: "i don't want the level to load what i'm looking at in my save
/// everytime, i would like to be able to load each variant whenever i want." So the level's own
/// conditions become switches rather than a snapshot of one playthrough, and the hub can be seen
/// docked at Kafrene or adrift in space on demand.
///
/// `Rule` is UE's OWN rendering of the condition this fact appears in -- the AutoDescription it
/// serialises beside every compiled query. Showing the engine's words rather than a paraphrase
/// means the label cannot drift from what is actually evaluated.
/// </summary>
public sealed class FactRow : Obs
{
    public string Tag { get; init; } = "";

    /// <summary>`Facts.Hub.HS_04_030_b` reads as `Hub.HS_04_030_b`; the prefix is on every row.</summary>
    public string Title => Tag.StartsWith("Facts.", StringComparison.OrdinalIgnoreCase)
        ? Tag["Facts.".Length..] : Tag;

    public string Rule { get; init; } = "";

    private bool _on;
    public bool On { get => _on; set => Set(ref _on, value); }
}

/// <summary>
/// AN ACTOR THAT IS NOT IN THE LEVEL YET.
///
/// A move overwrites eight bytes and a swap overwrites four; this one makes the .umap GROW, by
/// two export entries, two bundle entries and a dependency header. That was the gate every note
/// in this repo called unproven until 2026-09-16, when an Umbaran plant appeared in the hub.
///
/// It is still a COPY and never an authored actor: the new export is a byte copy of a donor the
/// level already holds, so nothing here composes a transform or invents a class. Three numbers
/// are then overwritten by the ordinary move path, which is why the position is editable after
/// the fact and why it is the same eight bytes a gizmo drag writes.
/// </summary>
public sealed class PendingSpawn : Obs
{
    /// <summary>The package the new actor is appended to -- never a level instance. See DonorsFor.</summary>
    public string Target { get; init; } = "";
    public string Sublevel { get; init; } = "";

    /// <summary>How the build finds the donor: its name, or "#427" when the name is not unique.</summary>
    public string Donor { get; init; } = "";
    public string DonorName { get; init; } = "";

    /// <summary>The asset placed, as a full package path.</summary>
    public string Asset { get; init; } = "";

    /// <summary>
    /// The mesh to IMPORT, or "" when the donor already draws this asset.
    ///
    /// Empty is the better case by a distance: a copy inherits its donor's mesh pointer, so
    /// copying something that already draws the asset adds no import, needs no SetPointerTo and
    /// leaves the import map exactly as the game shipped it.
    /// </summary>
    public string Mesh { get; init; } = "";

    /// <summary>A stable key so saving twice replaces this spawn instead of adding a second one.</summary>
    public string Id { get; init; } = "";

    /// <summary>The donor, kept only to draw the preview -- its mesh, rotation and scale.</summary>
    public UObject? Shape { get; init; }
    public UObject[] Scope { get; init; } = Array.Empty<UObject>();

    /// <summary>The donor's model matrix. The preview replaces its translation and keeps the rest.</summary>
    public Matrix4x4 Basis { get; init; } = Matrix4x4.Identity;

    private decimal _x, _y, _z;
    public decimal X { get => _x; set { if (Set(ref _x, value)) Raise(nameof(Summary)); } }
    public decimal Y { get => _y; set { if (Set(ref _y, value)) Raise(nameof(Summary)); } }
    public decimal Z { get => _z; set { if (Set(ref _z, value)) Raise(nameof(Summary)); } }

    private bool _saved;
    public bool Saved { get => _saved; set => Set(ref _saved, value); }

    public string Name => Asset[(Asset.LastIndexOf('/') + 1)..] is var leaf && leaf.Contains('.')
                        ? leaf[(leaf.LastIndexOf('.') + 1)..] : Asset[(Asset.LastIndexOf('/') + 1)..];
    public string Summary => $"{_x:0} , {_y:0} , {_z:0}";
    /// <summary>
    /// HAS HE DONE ANYTHING TO IT YET?
    ///
    /// Clicking an asset puts one in the scene on the gizmo, which is how a map editor behaves
    /// and is the point of the list -- but browsing a list is not authoring twenty actors. So an
    /// untouched one is the thing he is currently pointing at and the next click replaces it; the
    /// first drag, typed number or explicit Place makes it his, and nothing replaces it after
    /// that.
    /// </summary>
    public bool Touched { get; set; }

    public string How => Mesh.Length == 0
        ? "copy of " + DonorName
        : "copy of " + DonorName + ", + 1 import";

    public PendingSpawn Copy() => new()
    {
        Target = Target, Sublevel = Sublevel, Donor = Donor, DonorName = DonorName,
        Asset = Asset, Mesh = Mesh, Id = Id, Shape = Shape, Scope = Scope, Basis = Basis,
        X = X, Y = Y, Z = Z, Saved = Saved, Touched = Touched,
    };
}

/// <summary>Layer colours, as live brushes so a theme change repaints them in place.</summary>
public static class Layers
{
    public static readonly Dictionary<string, SolidColorBrush> Brushes = new()
    {
        [LevelReader.LayerCover] = new(Color.Parse("#49B9A3")),
        [LevelReader.LayerSpawns] = new(Color.Parse("#E8A33D")),
        [LevelReader.LayerVolumes] = new(Color.Parse("#9B7BD4")),
        [LevelReader.LayerObjectives] = new(Color.Parse("#5B9BD5")),
        [LevelReader.LayerEncounter] = new(Color.Parse("#D8794C")),
        [LevelReader.LayerProps] = new(Color.Parse("#6E7887")),
        [LevelReader.LayerScript] = new(Color.Parse("#4C5666")),
    };

    public static IBrush For(string layer) => Brushes.TryGetValue(layer, out var b) ? b : Brushes[LevelReader.LayerScript];

    public static void Refresh(Avalonia.Application app)
    {
        foreach (var (layer, brush) in Brushes)
        {
            var key = "Layer" + layer.Split(' ')[0];
            if (app.Resources.TryGetValue(key, out var v) && v is SolidColorBrush src) brush.Color = src.Color;
        }
    }
}

/// <summary>
/// THE WHOLE APPLICATION STATE. Reading is done on a background thread and posted back, because
/// a 964-export sublevel takes long enough to freeze a window if it is read on the UI thread.
/// </summary>
public sealed partial class Editor : Obs
{
    // A CAP THAT CUTS THINGS OFF IS WORSE THAN A SLOW LIST. 300 turned an environment sublevel
    // into "... 881 more" -- a row that names what it is hiding and offers no way to see it.
    // The TreeView virtualises, so the honest number is however many there are.
    public const int MaxInstancesPerClass = 20000;

    private IFileProvider? _provider;

    /// <summary>The mounted game, for the viewport to load meshes from.</summary>
    public IFileProvider? Provider => _provider;

    /// <summary>The mounted file index, kept so a map's variants can be found without a read.</summary>
    public AssetIndex? Index { get; private set; }

    /// <summary>What the viewport should draw, and a token that changes when it does.</summary>
    public List<LevelReader.Placement> Placements { get; private set; } = new();
    public int SceneVersion { get; private set; }

    /// <summary>
    /// Bumped once when a MAP or a SUBLEVEL is opened, which SceneVersion does not distinguish:
    /// that ticks again for every one of a map's twenty sublevels as they stream in. The viewport
    /// frames on this, so a level arriving in pieces does not keep yanking the camera back.
    /// </summary>
    public int MapVersion { get; private set; }
    private List<MapEntry> _maps = new();

    /// <summary>The maps the mount found, for a driver that wants one by id without clicking.</summary>
    public IReadOnlyList<MapEntry> Maps => _maps;
    private UObject[] _exports = Array.Empty<UObject>();
    private List<ActorRow> _rows = new();

    public ObservableCollection<Node> MapTree { get; } = new();
    public ObservableCollection<Node> ActorTree { get; } = new();
    public ObservableCollection<LayerChip> LayerChips { get; } = new();
    public ObservableCollection<PropRow> Props { get; } = new();

    /// <summary>
    /// WHY THE SELECTED THING IS WHERE IT IS -- see <see cref="LevelReader.Provenance"/>.
    ///
    /// Separate from <see cref="Props"/> on purpose. Props is what the export SAYS; this is what
    /// the level DID with it, and for half the things in a cooked map those are not the same
    /// list -- the export says almost nothing and an archetype in another package says the rest.
    /// </summary>
    public ObservableCollection<PropRow> Why { get; } = new();

    public bool HasWhy { get => _hasWhy; private set => Set(ref _hasWhy, value); }
    private bool _hasWhy;

    /// <summary>
    /// Explain a placement, or clear the readout when there is nothing to explain.
    ///
    /// Takes the PLACEMENT rather than the actor because an actor can draw several meshes at
    /// several transforms, and the whole value of this is being told about the one that was
    /// clicked. Selecting from the tree falls back to the actor's first placement, which is the
    /// best that can be meant by a click on a name.
    /// </summary>
    public void Explain(LevelReader.Placement? p)
    {
        Why.Clear();
        if (p is null) { HasWhy = false; return; }
        foreach (var r in LevelReader.Provenance(p)) Why.Add(r);
        HasWhy = Why.Count > 0;
    }
    public ObservableCollection<ClassRow> Classes { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<PendingMove> Changes { get; } = new();

    private string _status = "starting…";
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>
    /// The right-hand end of the status bar: WHICH PROGRAM AND VERSION this is. It used to name the UI toolkit
    /// and the .NET runtime ("Avalonia 12.1.2 · .NET 10"), which is what a developer wants and nothing a
    /// person who downloaded this can use; the version is the one About shows and the one the Mod Studio ships under.
    /// </summary>
    private static string AppName => $"{Branding.Product} {Branding.Version}";

    private string _statusRight = AppName;
    public string StatusRight { get => _statusRight; set => Set(ref _statusRight, value); }

    private string _mapCount = "";
    public string MapCount { get => _mapCount; set => Set(ref _mapCount, value); }

    private string _sublevelTitle = "no sublevel selected";
    public string SublevelTitle { get => _sublevelTitle; set => Set(ref _sublevelTitle, value); }

    private string _actorFilterHint = "Filter actors…";
    public string ActorFilterHint { get => _actorFilterHint; set => Set(ref _actorFilterHint, value); }

    private string _projectName = "no mod project open";
    public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }

    private string _projectDetail = "open one to record changes into it";
    public string ProjectDetail { get => _projectDetail; set => Set(ref _projectDetail, value); }

    private bool _hasProject;
    public bool HasProject
    {
        get => _hasProject;
        set { if (Set(ref _hasProject, value)) Raise(nameof(CanBuild)); }
    }

    private string _changesTitle = "PENDING CHANGES";
    public string ChangesTitle { get => _changesTitle; set => Set(ref _changesTitle, value); }

    /// <summary>
    /// WHICH TOOL IS ARMED. Select and Move work; rotate and scale have fields but no gizmo, so
    /// choosing one says so in the status bar rather than arming something that silently does
    /// nothing. A control with no visible consequence is the fault this whole tool exists to
    /// avoid repeating.
    /// </summary>
    private string _tool = "Select";
    public string Tool
    {
        get => _tool;
        set
        {
            if (!Set(ref _tool, value)) return;
            Status = value switch
            {
                "Select" => "Select - click an actor in the viewport, or pick one from the list on the right",
                "Move"   => "Move - drag an arrow. One drag is one undo step",
                "Rotate" => "Rotate - drag a ring; it turns about the actor's own root",
                "Scale"  => "Scale - drag a handle; one handle length doubles that axis",
                _        => Status,
            };
        }
    }

    /// <summary>
    /// Whether the level's BACKDROP is drawn.
    ///
    /// A vista is real geometry that genuinely hangs in space -- SK_Anaxes_020's bloom trees sit
    /// 13,000 units above its ground, and the hub is wrapped in a Ring of Kafrene shell -- because
    /// in game it is only ever seen from below with sky behind it. Truthful, and in the way while
    /// editing. Shown by default: hiding part of a level without being asked is exactly the kind
    /// of quiet lie this tool exists to avoid.
    /// </summary>
    /// <summary>
    /// GREYBOX IS HIDDEN BY DEFAULT, unlike the backdrop.
    ///
    /// The backdrop is real level and hiding it unasked would be a quiet lie, so it ships shown.
    /// Blockout is the opposite: it is scaffolding the game does not draw, it stands in the middle
    /// of the level rather than around it, and leaving it on means every map opens full of grey
    /// boxes. The author asked for this directly. The count is on the button, so it is hidden
    /// visibly rather than silently.
    /// </summary>
    private bool _showBlockout;
    public bool ShowBlockout
    {
        get => _showBlockout;
        set
        {
            if (!Set(ref _showBlockout, value)) return;
            Status = value ? "blockout shown" : "blockout hidden - greybox the game does not draw";
            Remark?.Invoke();
        }
    }

    private int _blockoutCount;
    public int BlockoutCount { get => _blockoutCount; set => Set(ref _blockoutCount, value); }

    private bool _showBackdrop = true;
    public bool ShowBackdrop
    {
        get => _showBackdrop;
        set
        {
            if (!Set(ref _showBackdrop, value)) return;
            Raise(nameof(BackdropHint));
            // No count here: the overlay carries a LIVE one, and a second number that was true
            // when the button was pressed disagrees with it the moment the dial moves.
            Status = value ? "backdrop shown" : "backdrop hidden - the dial under the viewport sets how far out that starts";
            Repaint?.Invoke();
        }
    }

    /// <summary>
    /// How far out something has to be to count as backdrop, in multiples of the level's own
    /// typical radius. A dial and not a constant -- see the note in Viewport.MeasureCore for the
    /// measurement that says there is no clean line to pick.
    /// </summary>
    private double _backdropFactor = 4;
    public double BackdropFactor
    {
        get => _backdropFactor;
        set
        {
            if (!Set(ref _backdropFactor, value)) return;
            Raise(nameof(BackdropHint));
            Remark?.Invoke();          // the viewport re-flags what counts and redraws
        }
    }

    private int _backdropCount;
    public int BackdropCount
    {
        get => _backdropCount;
        set { if (Set(ref _backdropCount, value)) Raise(nameof(BackdropHint)); }
    }

    public string BackdropHint =>
        $"Hide everything more than {BackdropFactor:0.#}x the level's own radius from its middle — " +
        $"{BackdropCount:N0} placement(s) right now. There is no clean line between a level and its " +
        "backdrop: on SK_Anaxes_020 the distant cliffs use the same meshes as the near ones, so " +
        "this is a dial rather than a rule, and the number tells you what it is taking away.";

    /// <summary>
    /// HOW MUCH OF A MATERIAL TO SHOW. Blender's three, because each answers a different question.
    ///
    ///   Solid     — flat grey. What SHAPE is this, and where is it? Costs nothing, and stays the
    ///               right choice while flying around a level looking for something.
    ///   Material  — base colour. WHAT is this?
    ///   Rendered  — base colour, normals and ORM. What does it LOOK like?
    ///
    /// MATERIAL IS NOT THE CHEAP ONE, and it would be easy to assume it is. Measured on
    /// SK_Anaxes_020: 2,789 sections resolve to 2,767 DISTINCT materials, and the cost is
    /// RESOLVING a material rather than decoding the extra two textures — 131 seconds for the
    /// level either way. What separates them is what you see, not what you wait for. Solid is the
    /// only one that is free, because it never resolves anything.
    /// </summary>
    private string _shading = "Solid";
    public string Shading
    {
        get => _shading;
        set
        {
            if (!Set(ref _shading, value)) return;
            Raise(nameof(ShadingHint));
            Status = value switch
            {
                "Material" => "Material preview - base colour only, loading in the background",
                "Rendered" => "Rendered - base colour, normals and ORM, loading in the background",
                _ => "Solid - flat grey, and the fastest thing to fly around in",
            };
            Reshade?.Invoke();
        }
    }

    public string ShadingHint =>
        $"Shading: {Shading}. Click to cycle Solid → Material → Rendered. Solid is flat " +
        "grey and costs nothing. Material adds base colour and Rendered adds normals and ORM — " +
        "they cost the same to load, because resolving a material is the expense, not the extra " +
        "textures. Either way it loads in the background, nearest to the camera first, and pauses " +
        "while you are moving.";

    private double _cameraSpeed = 1.0;
    public double CameraSpeed { get => _cameraSpeed; set => Set(ref _cameraSpeed, value); }

    private string _classesTitle = "CLASSES";
    public string ClassesTitle { get => _classesTitle; set => Set(ref _classesTitle, value); }

    // ---- the level browser ---------------------------------------------
    //
    // EVERYTHING IN THE OPEN MAP, across all of its sublevels -- which is the one thing the
    // actor tree cannot show, because the tree always means the single sublevel it has open.
    // Replaced wholesale rather than mutated item by item: the ListBox virtualises, so one
    // property change costs far less than twenty thousand collection notifications.
    private List<BrowseRow> _allBrowse = new();
    public List<BrowseRow> Browse { get; private set; } = new();

    /// <summary>Everything, whatever the filter box currently shows.</summary>
    public List<BrowseRow> AllBrowse => _allBrowse;

    private string _browseFilter = "";
    public string BrowseFilter { get => _browseFilter; set { if (Set(ref _browseFilter, value)) RaiseBrowse(); } }

    private string _browseTitle = "EVERYTHING IN THIS LEVEL";
    public string BrowseTitle { get => _browseTitle; private set => Set(ref _browseTitle, value); }

    private string _browseHint = "open a map on the left and it is listed here";
    public string BrowseHint { get => _browseHint; private set => Set(ref _browseHint, value); }

    private bool _browseEmpty = true;
    public bool BrowseEmpty { get => _browseEmpty; private set => Set(ref _browseEmpty, value); }

    private int _movableCount;
    public int MovableCount { get => _movableCount; private set => Set(ref _movableCount, value); }

    private bool _busy = true;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(CanExport)); } }

    // --- selected actor -------------------------------------------------
    private string _actorName = "";
    public string ActorName { get => _actorName; set => Set(ref _actorName, value); }

    private string _actorSub = "";
    public string ActorSub { get => _actorSub; set => Set(ref _actorSub, value); }

    private string _transformSource = "";
    public string TransformSource { get => _transformSource; set => Set(ref _transformSource, value); }

    private bool _hasActor;
    public bool HasActor { get => _hasActor; set => Set(ref _hasActor, value); }

    private bool _hasTransform;
    public bool HasTransform { get => _hasTransform; set => Set(ref _hasTransform, value); }

    // EACH FIELD IS AN EDIT. Typing in one records a pending move rather than changing anything
    // on disk -- nothing is written until the author asks for it, and then only into the mod
    // project. The game's own files are never touched by this application.
    private decimal _lx, _ly, _lz, _rp, _ry, _rr;
    public decimal LocX { get => _lx; set { if (Set(ref _lx, value)) Touch("RelativeLocation", "X", value); } }
    public decimal LocY { get => _ly; set { if (Set(ref _ly, value)) Touch("RelativeLocation", "Y", value); } }
    public decimal LocZ { get => _lz; set { if (Set(ref _lz, value)) Touch("RelativeLocation", "Z", value); } }
    public decimal RotP { get => _rp; set { if (Set(ref _rp, value)) Touch("RelativeRotation", "Pitch", value); } }
    public decimal RotY { get => _ry; set { if (Set(ref _ry, value)) Touch("RelativeRotation", "Yaw", value); } }
    public decimal RotR { get => _rr; set { if (Set(ref _rr, value)) Touch("RelativeRotation", "Roll", value); } }

    // SCALE NEEDED NO NEW MACHINERY. `RelativeScale3D` is three doubles like the other two, and
    // LevelEdit.Locate walks a property path rather than knowing the names -- so the build, the
    // verifier and the mod file already understood it before there was anywhere to type it.
    private decimal _sx = 1, _sy = 1, _sz = 1;
    public decimal SclX { get => _sx; set { if (Set(ref _sx, value)) Touch("RelativeScale3D", "X", value); } }
    public decimal SclY { get => _sy; set { if (Set(ref _sy, value)) Touch("RelativeScale3D", "Y", value); } }
    public decimal SclZ { get => _sz; set { if (Set(ref _sz, value)) Touch("RelativeScale3D", "Z", value); } }

    // Set while a selection is being loaded into those fields, so filling them in does not read
    // as the author typing. Without it, clicking an actor records six moves of nothing.
    private bool _loading;
    private string _prefix = "";
    private string _selectedPackage = "";
    private string _selectedSublevel = "";
    private readonly Dictionary<string, double> _original = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node> _actorNodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the drawn level should be redrawn because a move changed it.</summary>
    public event Action? Repaint;

    /// <summary>Raised when the backdrop cut-off moved and the scene must be re-flagged.</summary>
    public event Action? Remark;

    /// <summary>Raised when the shading mode changed and the scene must be re-dressed.</summary>
    public event Action? Reshade;

    // ---- undo ----------------------------------------------------------
    private const int UndoDepth = 50;
    private readonly List<UndoStep> _undo = new();
    private readonly List<UndoStep> _redo = new();
    private int _batch;
    private string _batchLabel = "";
    private UndoStep? _batchStart;

    private bool _canUndo, _canRedo;
    public bool CanUndo { get => _canUndo; private set => Set(ref _canUndo, value); }
    public bool CanRedo { get => _canRedo; private set => Set(ref _canRedo, value); }

    private string _undoHint = "Nothing to undo yet";
    public string UndoHint { get => _undoHint; private set => Set(ref _undoHint, value); }

    private string _redoHint = "Nothing to redo";
    public string RedoHint { get => _redoHint; private set => Set(ref _redoHint, value); }

    private void Touch(string property, string component, decimal value)
    {
        if (_loading || _selectedPackage.Length == 0 || ActorName.Length == 0) return;

        var path = _prefix + property + "." + component;
        var key = _selectedPackage + "|" + _selectedAddress + "|" + path;
        var now = (double) value;
        var existing = Changes.FirstOrDefault(c => c.Key == key);
        var was = existing?.Was ?? _original.GetValueOrDefault(property + "." + component, now);
        var back = Math.Abs(now - was) < 0.0005;

        // RE-ENTERING THE VALUE THAT IS ALREADY THERE IS NOT A STEP. Without this an undo step
        // could sit on the stack having changed nothing, and pressing it would look broken.
        if (back && existing is null) return;
        Record(component + " on " + ActorName);

        // BACK TO WHERE IT STARTED IS NOT AN EDIT. Dragging a value away and back should leave
        // no trace, or the changes list fills with entries that do nothing and the author stops
        // reading it.
        if (back)
        {
            Changes.Remove(existing!);
        }
        else if (existing is not null) { existing.Now = now; existing.Saved = false; }
        else
            Changes.Add(new PendingMove
            {
                Target = _selectedPackage, Sublevel = _selectedSublevel,
                Actor = ActorName, Address = _selectedAddress,
                Path = path, Was = was, Now = now,
            });

        MarkEdited();
        RaiseChangeCount();
        SyncOffsets();
    }

    private void RaiseBrowse()
    {
        var f = BrowseFilter.Trim();
        Browse = f.Length == 0
            ? _allBrowse
            : _allBrowse.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                    r.Class.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                    r.Mesh.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                    r.Sublevel.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Raise(nameof(Browse));

        BrowseEmpty = Browse.Count == 0;
        MovableCount = _allBrowse.Count(r => r.Movable);
        BrowseTitle = _allBrowse.Count == 0
            ? "EVERYTHING IN THIS LEVEL"
            : f.Length == 0
                ? $"EVERYTHING IN THIS LEVEL - {_allBrowse.Count:N0}, {MovableCount:N0} MOVABLE"
                : $"{Browse.Count:N0} OF {_allBrowse.Count:N0} MATCH";
        BrowseHint = _allBrowse.Count == 0
            ? "open a map on the left and it is listed here"
            : "nothing here matches - the filter reads names, classes, meshes and sublevels";
    }

    /// <summary>
    /// The placeable things in one sublevel: actors, plus components that really do stand on
    /// their own.
    ///
    /// Not a mesh list. Cover points, spawners, volumes and objectives are actors with nothing to
    /// draw, so they are invisible in the viewport and have never had anywhere to be picked FROM
    /// -- this list is that place.
    /// </summary>
    private static List<BrowseRow> BrowseRows(UObject[] exports, string package, string sublevel) =>
        AssetLoader.Read(() =>
        {
            var owned = LevelReader.OwnedComponents(exports);
            var rows = new List<BrowseRow>();
            foreach (var e in exports)
            {
                var isActor = LevelReader.IsActor(e);
                if (!isActor && owned.Contains(e)) continue;      // somebody else's component

                var cls = e.ExportType ?? "?";
                var layer = isActor ? LevelReader.LayerOfActor(e, exports) : LevelReader.LayerOf(cls);
                if (layer == LevelReader.LayerScript) continue;

                // CAN IT BE MOVED AT ALL? A cooked actor whose root component sits at its
                // archetype's position serialises no RelativeLocation, and there is no number in
                // the file to write over -- ADDING one to a .umap is a gate we have not passed.
                // The list still shows it, because it is in the level and worth finding; it just
                // says so, rather than letting somebody pick it and meet an empty panel.
                var movable = LevelReader.Transform(e, exports).Found;

                rows.Add(new BrowseRow(e, e.Name ?? "?", cls, LevelReader.MeshOf(e, exports),
                                       layer, sublevel, package, exports, movable));
            }
            return rows;
        });

    /// <summary>Which placement draws this actor, or -1 when it has no mesh to draw.</summary>
    public int PlacementIndexOf(UObject export) =>
        Placements.FindIndex(p => ReferenceEquals(p.Owner, export));

    /// <summary>Select a row of the level browser, wherever in the map it came from.</summary>
    public void SelectBrowse(BrowseRow r) => SelectActor(r.Export, r.Scope, r.Package, r.Sublevel);

    private void MarkEdited()
    {
        var edited = Changes.Select(c => c.Actor).Concat(Swaps.Select(x => x.Actor))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, node) in _actorNodes) node.Edited = edited.Contains(name);
    }

    private void RaiseChangeCount()
    {
        var n = Changes.Count;
        var sw = Swaps.Count;
        var sp = Spawns.Count;
        var packages = Changes.Select(c => c.Target)
                              .Concat(Swaps.Select(x => x.Target))
                              .Concat(Spawns.Select(x => x.Target)).Distinct().Count();

        // BYTES, because that was the claim this editor kept making about itself: a move
        // overwrites eight (a double) and a swap four (an FPackageIndex), so the package never
        // grew. A SPAWN DOES GROW IT, and saying "12 bytes" over an edit that appends an export
        // pair, two bundle entries and a dependency header would be the tool lying about the one
        // number it has always been careful about. So the two are counted apart and named apart.
        var parts = new List<string>();
        if (n > 0) parts.Add($"{n} move(s)");
        if (sw > 0) parts.Add($"{sw} swap(s)");
        if (sp > 0) parts.Add($"{sp} new actor(s)");
        var what = string.Join(" and ", parts);

        var overwrites = n * 8 + sw * 4;
        var cost = sp == 0
            ? $"{overwrites} bytes"
            : overwrites == 0
                ? "the .umap GROWS"
                : $"{overwrites} bytes overwritten and the .umap GROWS";

        var total = n + sw + sp;
        ChangesTitle = total == 0
            ? "PENDING CHANGES"
            : $"PENDING CHANGES - {what} in {packages} package(s), {cost}";
        HasChanges = total > 0;
        if (HasProject) ProjectDetail = total == 0 ? "no unsaved edits" : $"{what} unsaved";
    }

    private bool _hasChanges;
    public bool HasChanges { get => _hasChanges; private set => Set(ref _hasChanges, value); }

    public void Revert(PendingMove move)
    {
        Record("revert " + move.Field + " on " + move.Actor);
        Changes.Remove(move);
        if (move.Address.Equals(_selectedAddress, StringComparison.OrdinalIgnoreCase) &&
            _original.TryGetValue(move.Path[_prefix.Length..], out var was))
        {
            _loading = true;
            switch (move.Path[(move.Path.LastIndexOf('.') + 1)..])
            {
                case "X" when move.Path.Contains("Scale", StringComparison.Ordinal): SclX = (decimal) was; break;
                case "Y" when move.Path.Contains("Scale", StringComparison.Ordinal): SclY = (decimal) was; break;
                case "Z" when move.Path.Contains("Scale", StringComparison.Ordinal): SclZ = (decimal) was; break;
                case "X": LocX = (decimal) was; break;
                case "Y": LocY = (decimal) was; break;
                case "Z": LocZ = (decimal) was; break;
                case "Pitch": RotP = (decimal) was; break;
                case "Yaw": RotY = (decimal) was; break;
                case "Roll": RotR = (decimal) was; break;
            }
            _loading = false;
        }
        MarkEdited();
        RaiseChangeCount();
        SyncOffsets();
    }

    // ================================================================== what an actor draws
    //
    // THE OTHER HALF OF "DELETE THIS AND PUT THAT THERE". A component's mesh is an FPackageIndex:
    // four bytes naming an import the package already has. Point it somewhere else and the chair
    // is a crate; point it at zero and the chair is gone. Nothing grows, so this is the same kind
    // of edit as a move and it reuses all of the same machinery -- one pending list, one undo
    // stack, one save.
    //
    // Confirmed in game 2026-09-14 on the hub holotable: one piece became a locker, one became a
    // crate, one disappeared.

    /// <summary>The property a swap writes. An actor whose mesh is not here cannot be swapped.</summary>
    public const string DrawsPath = "RootComponent.StaticMesh";

    public ObservableCollection<PendingSwap> Swaps { get; } = new();

    private string _selectedAddress = "";
    private string _wasMesh = "";
    private int _drawsToken;

    private readonly Dictionary<string, byte[]?> _rawCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<UObject[], Dictionary<UObject, string>> _addresses = new();

    private bool _canSwap;
    public bool CanSwap { get => _canSwap; private set { if (Set(ref _canSwap, value)) Raise(nameof(NoSwap)); } }
    public bool NoSwap => !_canSwap;

    private string _swapWhy = "";
    public string SwapWhy { get => _swapWhy; private set => Set(ref _swapWhy, value); }

    /// <summary>
    /// TRUE WHILE THE PACKAGE IS BEING READ for the selected actor.
    ///
    /// The read is half a megabyte and every export in it, so it cannot be done on the click.
    /// Anything that wants to know what an actor draws has to wait for THIS rather than for a
    /// delay it guessed -- a harness that guessed measured an empty list and reported the feature
    /// broken when it had simply asked too early.
    /// </summary>
    private bool _drawsBusy;
    public bool DrawsBusy { get => _drawsBusy; private set => Set(ref _drawsBusy, value); }

    private string _drawsNow = "";
    public string DrawsNow { get => _drawsNow; private set => Set(ref _drawsNow, value); }

    private List<string> _allMeshes = new();
    public List<string> MeshChoices { get; private set; } = new();

    private string _meshFilter = "";
    public string MeshFilter { get => _meshFilter; set { if (Set(ref _meshFilter, value)) RaiseMeshes(); } }

    private string _meshTitle = "";
    public string MeshTitle { get => _meshTitle; private set => Set(ref _meshTitle, value); }

    /// <summary>Raised when a swap names a mesh, so the viewport can go and load it.</summary>
    public event Action? Reswap;

    /// <summary>
    /// Raised once the donor search for the picked asset has finished, whatever it found.
    ///
    /// The window waits for THIS rather than for a delay, because putting the thing on the gizmo
    /// is the first half of the click and the donor read is half a megabyte of package. A window
    /// that guessed at the wait would arm nothing and look broken -- which is what the Draws
    /// panel's own harness did, for exactly this reason.
    /// </summary>
    public event Action? DonorSettled;

    /// <summary>
    /// HOW THE BUILD WILL FIND THIS EXPORT: its name, or "#427" when the name is not unique.
    ///
    /// Cached per scope, because this is called for every placement on every sync and the naive
    /// version is a scan of 18,000 exports inside a loop over 18,000 placements.
    /// </summary>
    private string AddressOf(UObject export, UObject[] scope)
    {
        if (!_addresses.TryGetValue(scope, out var map))
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in scope)
                if (e?.Name is { } n) counts[n] = counts.GetValueOrDefault(n) + 1;

            map = new Dictionary<UObject, string>();
            for (var i = 0; i < scope.Length; i++)
            {
                if (scope[i] is not { } e) continue;
                var n = e.Name ?? "?";
                map[e] = counts.GetValueOrDefault(n) > 1 ? "#" + i : n;
            }
            _addresses[scope] = map;
        }
        return map.TryGetValue(export, out var a) ? a : export.Name ?? "?";
    }

    /// <summary>
    /// The package's PRISTINE bytes, cached.
    ///
    /// Pristine and not the mounted copy even when the editor was started with --withmods: every
    /// edit is addressed against what the GAME shipped, and reading a mod's own output back as if
    /// it were the game is the trap in zero-company-authoring-reads-your-own-mod.
    /// </summary>
    private byte[]? RawOf(string package)
    {
        if (_rawCache.TryGetValue(package, out var hit)) return hit;
        byte[]? raw = null;
        try
        {
            if (_provider is not null)
                raw = AssetCopy.PristineRead(_provider, package, out _)?
                               .Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        }
        catch { raw = null; }
        _rawCache[package] = raw;
        return raw;
    }

    /// <summary>
    /// What the selected actor draws, and what else it COULD draw -- read off the package on a
    /// background thread, because it opens half a megabyte and every export in it.
    ///
    /// Both answers come from the writer's own code (LevelEdit.LocatePointer and LevelEdit.Donors)
    /// rather than from the browser's mesh column. An actor that inherits its mesh from a
    /// Blueprint archetype SHOWS a mesh and has no four bytes of its own to overwrite; offering it
    /// here would be a control that does nothing.
    /// </summary>
    private void LoadDraws(UObject export, UObject[] scope, string package)
    {
        var token = ++_drawsToken;
        var address = _selectedAddress;
        _ = export; _ = scope;
        DrawsBusy = true;

        CanSwap = false;
        DrawsNow = "";
        _wasMesh = "";
        _allMeshes = new List<string>();
        RaiseMeshes();

        if (_provider is null || package.Length == 0)
        { SwapWhy = "no package is open"; DrawsBusy = false; return; }
        SwapWhy = "reading the package...";
        DrawsBusy = true;

        _ = Task.Run(() =>
        {
            string? why = null;
            var now = "";
            var choices = new List<string>();
            var ok = false;
            try
            {
                var raw = RawOf(package);
                if (raw is null) why = "could not read " + package;
                else
                {
                    string? locateWhy = null;
                    var ptr = AssetLoader.Read(() =>
                        LevelEdit.LocatePointer(_provider!, raw, package, address, DrawsPath, out locateWhy));
                    why = locateWhy;
                    if (ptr is not null)
                    {
                        ok = true;
                        now = ptr.Value == 0 ? "" : Leaf(ptr.Points);
                        choices = AssetLoader.Read(() =>
                            LevelEdit.Donors(_provider!, raw, package, "StaticMesh", out _));
                    }
                }
            }
            catch (Exception e) { why = e.Message; }

            Dispatcher.UIThread.Post(() =>
            {
                // THE AUTHOR HAS PROBABLY CLICKED SOMETHING ELSE BY NOW. Publishing a stale read
                // would put one actor's mesh list on another actor's panel, which is a swap aimed
                // at the wrong thing.
                if (token != _drawsToken) return;
                DrawsBusy = false;
                CanSwap = ok;
                SwapWhy = ok ? "" : why ?? "this actor has no mesh of its own to change";
                DrawsNow = now;
                _wasMesh = now;
                _allMeshes = choices;
                RaiseMeshes();
                ApplyPendingToDraws();
            });
        });
    }

    private static string Leaf(string path)
    {
        var slash = path[(path.LastIndexOf('/') + 1)..];
        return slash[(slash.LastIndexOf('.') + 1)..];
    }

    private void RaiseMeshes()
    {
        var f = MeshFilter.Trim();
        MeshChoices = f.Length == 0
            ? _allMeshes
            : _allMeshes.Where(m => m.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Raise(nameof(MeshChoices));
        MeshTitle = _allMeshes.Count == 0
            ? "NOTHING TO CHOOSE FROM"
            : f.Length == 0
                ? $"{_allMeshes.Count:N0} MESH(ES) THIS SUBLEVEL ALREADY USES"
                : $"{MeshChoices.Count:N0} OF {_allMeshes.Count:N0} MATCH";
    }

    /// <summary>Show the pending swap, if this actor has one, over the package's own answer.</summary>
    private void ApplyPendingToDraws()
    {
        var pending = Swaps.FirstOrDefault(
            x => x.Address.Equals(_selectedAddress, StringComparison.OrdinalIgnoreCase) &&
                 x.Target.Equals(_selectedPackage, StringComparison.OrdinalIgnoreCase));
        if (pending is not null) DrawsNow = pending.Now;
    }

    /// <summary>Point the selected actor at a mesh, or at NOTHING when it is empty.</summary>
    public string SetDraws(string mesh)
    {
        if (!CanSwap) return SwapWhy.Length > 0 ? SwapWhy : "this actor cannot be swapped";
        if (_selectedPackage.Length == 0 || _selectedAddress.Length == 0) return "nothing is selected";

        var key = _selectedPackage + "|" + _selectedAddress + "|" + DrawsPath;
        var existing = Swaps.FirstOrDefault(x => x.Key == key);
        var back = string.Equals(mesh, _wasMesh, StringComparison.OrdinalIgnoreCase);

        // PUTTING BACK WHAT WAS ALREADY THERE IS NOT A STEP -- the same rule a move follows, or
        // the undo stack fills with entries that change nothing.
        if (back && existing is null) return "that is what it already draws";
        Record((mesh.Length == 0 ? "remove " : "draws on ") + ActorName);

        if (back) Swaps.Remove(existing!);
        else if (existing is not null) { existing.Now = mesh; existing.Saved = false; }
        else
            Swaps.Add(new PendingSwap
            {
                Target = _selectedPackage, Sublevel = _selectedSublevel,
                Actor = ActorName, Address = _selectedAddress,
                Path = DrawsPath, Was = _wasMesh, Now = mesh,
            });

        DrawsNow = mesh;
        MarkEdited();
        RaiseChangeCount();
        SyncDraws();
        return mesh.Length == 0 ? $"{ActorName} draws nothing" : $"{ActorName} draws {mesh}";
    }

    public void RevertSwap(PendingSwap swap)
    {
        Record("revert draws on " + swap.Actor);
        Swaps.Remove(swap);
        if (swap.Address.Equals(_selectedAddress, StringComparison.OrdinalIgnoreCase)) DrawsNow = _wasMesh;
        MarkEdited();
        RaiseChangeCount();
        SyncDraws();
    }

    /// <summary>
    /// WHAT EACH PLACEMENT DRAWS IS DERIVED FROM THE SWAP LIST, never accumulated -- the same
    /// discipline as SyncOffsets, and for the same reason: two records of one fact is how an undo
    /// puts one back without the other.
    /// </summary>
    private void SyncDraws()
    {
        if (Placements.Count > 0)
        {
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in Swaps)
                wanted[x.Target + "|" + x.Address] = x.Now.Length == 0 ? "" : MeshPathOf(x.Now);

            foreach (var p in Placements)
            {
                if (p.Spawned is not null) continue;      // a preview wears its donor's address
                var want = wanted.Count == 0
                    ? null
                    : wanted.TryGetValue(p.Package + "|" + AddressOf(p.Owner, p.Scope), out var m) ? m : null;
                if (p.Draws != want) p.Draws = want;
            }
        }
        Reswap?.Invoke();
        Repaint?.Invoke();
    }

    /// <summary>
    /// A mesh's FULL path, which is what the viewport loads by -- found from a placement that
    /// already draws it rather than assembled from the name, because the folder a mesh lives in
    /// is not derivable from what it is called.
    /// </summary>
    private string MeshPathOf(string leaf)
    {
        foreach (var p in Placements)
            if (Leaf(p.MeshPath).Equals(leaf, StringComparison.OrdinalIgnoreCase))
                return p.MeshPath;
        return leaf;                          // nothing places it; the viewport will simply miss it
    }

    // ================================================================== the asset list
    //
    // WHAT THE LEVEL LOADS, AS A LIST YOU CAN PLACE FROM.
    //
    // His ask: "all the assets that are loaded into a level and it gives us a list and we can
    // select stuff from that list and place it into the scene". Everything under it was proven on
    // 2026-09-16 -- see zero-company-add-an-actor-works -- so what is left here is the shopping:
    // the level browser lists four hundred actors called BitReactorStaticMeshActor_N, and this
    // lists the couple of hundred THINGS those actors are, once each.
    //
    // THE DONOR IS NOT A QUESTION HE SHOULD BE ASKED. A spawn is a copy of an actor the level
    // already holds, and which actor that is is a detail of the technique rather than a choice
    // with a meaning. It is chosen here, verified by actually performing the copy on a throwaway
    // buffer, and only then does the Place button light up -- so a press either works or was
    // never offered.

    /// <summary>
    /// SUBLEVELS THE VIEWPORT IS NOT DRAWING, by name.
    ///
    /// A map streams its sublevels CONDITIONALLY and this editor loads all of them at once -- the
    /// hub is 60 of them and 50,546 placements, of which 11 are cinematic sets for specific story
    /// beats and 5 are vista backdrops worth 25,621 placements on their own. His report: "i'm
    /// seeing things that are not there ingame", and he was right.
    ///
    /// This is the same disease [[zero-company-level-rigs-are-variants]] found in the LIGHTING --
    /// the hub lit by four suns because four rigs loaded together -- and the geometry half was
    /// never addressed. LevelLights.Choose picks one rig automatically; geometry cannot be chosen
    /// automatically, because which sublevels are live is a question about the GAME's state, so
    /// this is a switch rather than a rule.
    /// </summary>
    public HashSet<string> HiddenSublevels { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the set changes, so the viewport can drop what it no longer draws.</summary>
    public event Action? Resublevel;

    public void ShowSublevel(string name, bool shown)
    {
        var changed = shown ? HiddenSublevels.Remove(name) : HiddenSublevels.Add(name);
        if (!changed) return;
        Resublevel?.Invoke();
        Repaint?.Invoke();
        RaiseHiddenCount();
    }

    private string _sublevelHint = "";
    public string SublevelHint { get => _sublevelHint; private set => Set(ref _sublevelHint, value); }

    private void RaiseHiddenCount()
    {
        if (HiddenSublevels.Count == 0) { SublevelHint = ""; return; }
        var hidden = Placements.Count(p => HiddenSublevels.Contains(p.HideKey));
        SublevelHint = $"{HiddenSublevels.Count} sublevel(s) hidden, {hidden:N0} placement(s)";
    }

    /// <summary>
    /// SHOW ROUGHLY WHAT THE GAME SHOWS: drop the cinematic sets and the vista backdrops.
    ///
    /// A guess by NAME, and it says so. Which sublevels are live at any moment is the game's
    /// business and nothing here can read it; what this can do is turn off the two kinds that are
    /// obviously not the room you are standing in. The author can tick any of them back.
    /// </summary>
    public string HideStagedSublevels(IEnumerable<Node> mapNodes)
    {
        // FROM THE PLACEMENTS, NOT FROM THE TREE.
        //
        // Walking the tree hid 124 rows and removed nothing from the picture: the tree carries a
        // folder guess for every one of 79 maps, so it ticked cinematic sublevels belonging to
        // maps nobody had opened while missing the ones actually on screen. The placements are the
        // only authoritative list of what is being DRAWN -- and they are the list that includes
        // level instances, which have no folder listing to guess from at all.
        // BY THE HIDE KEY, so what this WRITES is what the viewport CHECKS. Selecting the
        // package names instead wrote prefab names into a set the viewport matched on prefab
        // names too -- consistent, and consistent with the wrong thing, because the tick boxes
        // and the set picker write the MAP's sublevel names into the same set.
        var names = Placements.Where(p => p.Spawned is null)
                              .Select(p => p.HideKey)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .Where(n => Staged(n) && !HiddenSublevels.Contains(n))
                              .ToList();
        if (names.Count == 0) return "nothing on screen looks staged";

        var lost = Placements.Count(p => names.Contains(p.HideKey, StringComparer.OrdinalIgnoreCase));
        foreach (var n in names) ShowSublevel(n, false);

        // The rows follow the decision rather than making it, so a tick box always says what the
        // viewport is doing.
        foreach (var node in mapNodes)
            foreach (var child in node.Children)
                if (child.IsSublevel && HiddenSublevels.Contains(child.SublevelName)) child.Shown = false;

        return $"hid {names.Count} cinematic or backdrop sublevel(s), {lost:N0} placement(s)";
    }

    /// <summary>Cinematic sets and distant backdrops, by the names this game gives them.</summary>
    public static bool Staged(string name) =>
        name.StartsWith("CIN_", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Cinematic", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Vista", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Nebula", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Asteroid", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Blockout", StringComparison.OrdinalIgnoreCase) ||
        // THE RING OF KAFRENE IS A BACKDROP and its name says none of the above. It is the
        // station the hub hangs off, eleven kilometres of cargo docks and asteroids, and it costs
        // more than it looks: the far plane is derived from the farthest thing shown, so leaving
        // it on at a close zoom takes the depth ratio to 200,000:1 and the level starts fighting
        // itself. Named outright because a rule cannot guess it.
        name.Contains("Kafrene", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every asset in the open map, grouped. Rebuilt whenever the browser is.</summary>
    private List<AssetRow> _levelAssets = new();

    /// <summary>
    /// EVERY STATIC MESH IN THE GAME, built once and kept.
    ///
    /// His question: "what about props/assets from other maps, since we confirmed we can load
    /// umbara plants into other maps". The list started as what a level already loads because
    /// that is what he asked for -- but the import half is proven, so the level is not the limit
    /// and pretending it is would be the tool being smaller than the technique.
    /// </summary>
    private List<AssetRow> _gameAssets = new();
    private bool _gameAssetsAsked;

    private List<AssetRow> _allAssets => WholeGame ? _gameAssets : _levelAssets;
    public List<AssetRow> Assets { get; private set; } = new();

    private bool _wholeGame;
    public bool WholeGame
    {
        get => _wholeGame;
        set
        {
            if (!Set(ref _wholeGame, value)) return;
            Raise(nameof(ThisLevelOnly));
            if (value) LoadGameAssets();
            RaiseAssets();
        }
    }

    public bool ThisLevelOnly => !_wholeGame;

    private bool _gameAssetsBusy;
    public bool GameAssetsBusy { get => _gameAssetsBusy; private set => Set(ref _gameAssetsBusy, value); }

    /// <summary>
    /// Read the index once, on a background thread. 180,000 files walked and about a tenth of
    /// them kept, which is a second the first time and free after that.
    /// </summary>
    private void LoadGameAssets()
    {
        if (_gameAssetsAsked || Index is null) return;
        _gameAssetsAsked = true;
        GameAssetsBusy = true;

        var index = Index;
        _ = Task.Run(() =>
        {
            var made = new List<AssetRow>();
            try
            {
                foreach (var f in index.Root.DescendantFiles())
                {
                    if (f.Kind != AssetKind.StaticMesh) continue;
                    var package = LevelReader.PackageNameOf(f.FullPath);
                    if (package.Length == 0 || !package.StartsWith('/')) continue;
                    var leaf = package[(package.LastIndexOf('/') + 1)..];
                    made.Add(new AssetRow(package + "." + leaf, leaf, FolderOf(package), 0,
                                          "not in this level", new List<LevelReader.Placement>()));
                }
                made = made.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) { Dispatcher.UIThread.Post(() => Log.Add("asset index: " + ex.Message)); }

            Dispatcher.UIThread.Post(() =>
            {
                _gameAssets = made;
                GameAssetsBusy = false;
                Log.Add($"asset list: {made.Count:N0} static mesh(es) in the game");
                if (WholeGame) RaiseAssets();
            });
        });
    }

    private string _assetFilter = "";
    public string AssetFilter { get => _assetFilter; set { if (Set(ref _assetFilter, value)) RaiseAssets(); } }

    private string _assetTitle = "ASSETS IN THIS LEVEL";
    public string AssetTitle { get => _assetTitle; private set => Set(ref _assetTitle, value); }

    private string _assetHint = "open a map on the left and everything it loads is listed here";
    public string AssetHint { get => _assetHint; private set => Set(ref _assetHint, value); }

    private bool _assetsEmpty = true;
    public bool AssetsEmpty { get => _assetsEmpty; private set => Set(ref _assetsEmpty, value); }

    private AssetRow? _picked;
    public AssetRow? PickedAsset { get => _picked; private set => Set(ref _picked, value); }

    private bool _hasAsset;
    public bool HasAsset { get => _hasAsset; private set { if (Set(ref _hasAsset, value)) Raise(nameof(NoAsset)); } }
    public bool NoAsset => !_hasAsset;

    private string _assetName = "";
    public string AssetName { get => _assetName; private set => Set(ref _assetName, value); }

    private string _assetPath = "";
    public string AssetPath { get => _assetPath; private set => Set(ref _assetPath, value); }

    private string _assetWhere = "";
    public string AssetWhere { get => _assetWhere; private set => Set(ref _assetWhere, value); }

    private bool _canPlace;
    public bool CanPlace { get => _canPlace; private set => Set(ref _canPlace, value); }

    private bool _placeBusy;
    public bool PlaceBusy { get => _placeBusy; private set => Set(ref _placeBusy, value); }

    private string _placeWhy = "";
    public string PlaceWhy { get => _placeWhy; private set => Set(ref _placeWhy, value); }

    private string _placeHow = "";
    public string PlaceHow { get => _placeHow; private set => Set(ref _placeHow, value); }

    /// <summary>The actors this session has added, none of which are in the game yet.</summary>
    public ObservableCollection<PendingSpawn> Spawns { get; } = new();

    private PendingSpawn? _spawnPick;
    public PendingSpawn? PickedSpawn { get => _spawnPick; private set => Set(ref _spawnPick, value); }

    /// <summary>A donor, already verified against the package it would be copied in.</summary>
    private sealed record DonorPick(LevelReader.Placement At, string Address, bool NeedsImport);

    private DonorPick? _donor;
    private int _donorToken;

    /// <summary>The asset list as a tree of folders, or flat when a filter is narrowing it.</summary>
    public ObservableCollection<AssetTreeNode> AssetTree { get; } = new();

    // ===================== STORY STATE =====================

    /// <summary>The facts this level's own visibility rules are gated on, as switches.</summary>
    public ObservableCollection<FactRow> FactRows { get; } = new();

    /// <summary>How many placements in the open level carry their own materials.</summary>
    public int OverriddenPlacements => Placements.Count(p => p.Materials is { Length: > 0 });

    public string FactsTitle => FactRows.Count == 0
        ? "STORY STATE — nothing in this level is gated"
        : $"STORY STATE — {FactRows.Count:N0} fact(s) decide what is here";

    /// <summary>What the last read did with those rules, so a switch is never a silent no-op.</summary>
    public string FactsVerdict =>
        LevelReader.RulesSeen == 0
            ? "No visibility rules in this level."
            : $"{LevelReader.RulesSeen:N0} rule(s): {LevelReader.AllowedByFacts:N0} actor(s) in, " +
              $"{LevelReader.SkippedByFacts:N0} out. " +
              $"{LevelReader.SkippedNotAutoLoaded:N0} instance(s) nothing switches on, " +
              $"{LevelReader.SkippedHidden:N0} hidden.";

    /// <summary>
    /// Rebuild the switch list from what the level was actually gated on.
    ///
    /// The ticks are read back off <see cref="LevelReader.Facts"/> rather than remembered here,
    /// so the panel cannot disagree with what was evaluated. A panel that keeps its own copy of
    /// the state is how a switch ends up showing one thing and meaning another.
    /// </summary>
    private void PublishFacts()
    {
        FactRows.Clear();
        foreach (var (tag, rule) in LevelReader.FactsInLevel)
            FactRows.Add(new FactRow { Tag = tag, Rule = rule, On = LevelReader.Facts.Contains(tag) });
        Raise(nameof(FactRows));
        Raise(nameof(FactsTitle));
        Raise(nameof(FactsVerdict));
    }

    /// <summary>
    /// Set or clear one fact and read the level again.
    ///
    /// A REREAD, not a redraw: the rules are evaluated while the placements are built, so the
    /// asset list, the counts and any export have to be rebuilt with them. Hiding at draw time
    /// would leave every number disagreeing with the picture.
    /// </summary>
    public async Task SetFactAsync(string tag, bool on)
    {
        if (on) LevelReader.Facts.Add(tag);
        else LevelReader.Facts.Remove(tag);
        Log.Add($"story state: {tag} {(on ? "set" : "cleared")}");
        if (_sublevel is { } s) await OpenSublevelAsync(s);
    }

    /// <summary>Every fact this level names, on or off at once.</summary>
    public async Task SetAllFactsAsync(bool on)
    {
        foreach (var tag in LevelReader.FactsInLevel.Keys)
            if (on) LevelReader.Facts.Add(tag); else LevelReader.Facts.Remove(tag);
        Log.Add(on
            ? $"story state: all {LevelReader.FactsInLevel.Count:N0} of this level's fact(s) set"
            : "story state: cleared — this is a new game");
        if (_sublevel is { } s) await OpenSublevelAsync(s);
    }

    private void RaiseAssets()
    {
        var f = AssetFilter.Trim();
        Assets = f.Length == 0
            ? _allAssets
            : _allAssets.Where(a => a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                    a.Folder.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                    a.Where.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Raise(nameof(Assets));

        // BROWSE IS A TREE, SEARCH IS A LIST.
        //
        // Folders are what makes two thousand assets navigable, and they are exactly in the way
        // once somebody has typed three letters: a match four folders deep is a match you still
        // have to go and open. So a filter flattens -- which is also what a person means by
        // typing into a filter box.
        AssetTree.Clear();
        if (f.Length == 0)
        {
            var roots = FolderTree(_allAssets);
            Reveal(roots);
            foreach (var n in roots) AssetTree.Add(n);
        }
        else foreach (var a in Assets) AssetTree.Add(Leafy(a, a.Folder));

        AssetsEmpty = Assets.Count == 0;
        AssetTitle = _allAssets.Count == 0
            ? "ASSETS IN THIS LEVEL"
            : f.Length == 0
                ? $"{_allAssets.Count:N0} ASSET(S) THIS LEVEL LOADS"
                : $"{Assets.Count:N0} OF {_allAssets.Count:N0} MATCH";
        AssetHint = _allAssets.Count == 0
            ? "open a map on the left and everything it loads is listed here"
            : "nothing here matches - the filter reads the name, the folder and the sublevel";
    }

    private static AssetTreeNode Leafy(AssetRow a, string sub) =>
        new() { Title = a.Name, Detail = a.Tally, Sub = sub, Asset = a };

    /// <summary>
    /// OPEN THE TREE UNTIL IT SAYS SOMETHING.
    ///
    /// Fully collapsed, the hub's two thousand assets are two rows: "Engine, 9" and "Game,
    /// 1,959". That is the flood solved and nothing else -- a first screen where every answer is
    /// one level further down is barely better than the flood. So folders are opened breadth
    /// first while the rows they reveal stay under a screenful, which lands on the real
    /// categories and stops before it becomes a list again.
    /// </summary>
    private static void Reveal(List<AssetTreeNode> roots)
    {
        const int Budget = 34;
        var shown = roots.Count;
        var queue = new Queue<AssetTreeNode>(roots);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.IsAsset || node.Children.Count == 0) continue;
            if (shown + node.Children.Count > Budget) continue;

            node.IsExpanded = true;
            shown += node.Children.Count;
            foreach (var child in node.Children) queue.Enqueue(child);
        }
    }

    /// <summary>
    /// THE ASSETS GROUPED BY THE FOLDER THEY LIVE IN, one node per folder.
    ///
    /// Two things stop this being a six-click-deep trie. The shared root is dropped, because
    /// every asset in the game starts "/Game/Game" and a node holding everything is a node that
    /// says nothing. And a folder with ONE child folder and no assets of its own is merged into
    /// it, so "Vehicles > Dropship > Meshes > LandingGear" is one row reading
    /// "Vehicles/Dropship/Meshes/LandingGear" rather than four rows to open in turn.
    /// </summary>
    private static List<AssetTreeNode> FolderTree(List<AssetRow> assets)
    {
        if (assets.Count == 0) return new List<AssetTreeNode>();

        var root = new Branch();
        var strip = SharedRoot(assets);
        foreach (var a in assets)
        {
            var rest = a.Folder.Length > strip ? a.Folder[strip..] : "";
            var here = root;
            foreach (var part in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!here.Folders.TryGetValue(part, out var next))
                    here.Folders[part] = next = new Branch();
                here = next;
            }
            here.Assets.Add(a);
        }
        return Flatten(root);
    }

    /// <summary>How much of every folder path is the same, cut at a slash.</summary>
    private static int SharedRoot(List<AssetRow> assets)
    {
        var first = assets[0].Folder;
        var n = first.Length;
        foreach (var a in assets)
        {
            var i = 0;
            while (i < n && i < a.Folder.Length &&
                   char.ToLowerInvariant(first[i]) == char.ToLowerInvariant(a.Folder[i])) i++;
            n = i;
            if (n == 0) break;
        }
        // BACK TO A SLASH. Cutting mid-segment would leave every folder named "ronments" the day
        // two roots happen to share a few letters.
        while (n > 0 && first[n - 1] != '/') n--;
        return n;
    }

    private sealed class Branch
    {
        public readonly Dictionary<string, Branch> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<AssetRow> Assets = new();
        public int Count => Assets.Count + Folders.Values.Sum(f => f.Count);
    }

    private static List<AssetTreeNode> Flatten(Branch branch)
    {
        var made = new List<AssetTreeNode>();
        // THE BIGGEST GROUP FIRST, not the alphabetical one.
        //
        // Alphabetically the hub opens on "Engine", which is nine editor primitives, and pushes
        // "Game" and its nineteen hundred off the bottom of the first screen -- so the opening
        // view is spent on the one folder nobody came for. Sorting by what is inside puts the
        // real categories at the top, and finding a folder BY NAME is what the filter is for.
        foreach (var (name, child) in branch.Folders
                                            .OrderByDescending(kv => kv.Value.Count)
                                            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            // MERGE A CHAIN OF ONES. A folder that only holds one folder is a click that reveals
            // one row, which is the whole reason a path-shaped tree is usually unusable.
            var title = name;
            var walk = child;
            while (walk.Assets.Count == 0 && walk.Folders.Count == 1)
            {
                var (onlyName, only) = walk.Folders.First();
                title += "/" + onlyName;
                walk = only;
            }

            var node = new AssetTreeNode
            {
                Title = title,
                Detail = walk.Count == 1 ? "1 asset" : $"{walk.Count:N0} assets",
            };
            foreach (var n in Flatten(walk)) node.Children.Add(n);
            made.Add(node);
        }

        foreach (var a in branch.Assets.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            made.Add(Leafy(a, a.Where));

        return made;
    }

    /// <summary>
    /// The map's placements, grouped by what they DRAW.
    ///
    /// Off the placements and not off the browser rows, because a placement carries the mesh's
    /// full package path and a browser row only carries its leaf -- and the path is the half that
    /// matters. It is what an import names and what the viewport loads by, and the folder a mesh
    /// lives in is not derivable from what it is called.
    /// </summary>
    private static List<AssetRow> AssetRows(List<LevelReader.Placement> placements) =>
        placements.Where(p => p.Spawned is null && p.MeshPath.Length > 0)
                  .GroupBy(p => p.MeshPath, StringComparer.OrdinalIgnoreCase)
                  .Select(g =>
                  {
                      var subs = g.Select(p => p.Sublevel)
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                      return new AssetRow(
                          g.Key, Leaf(g.Key), FolderOf(g.Key), g.Count(),
                          subs.Count == 1 ? subs[0] : $"{subs.Count} sublevels", g.ToList());
                  })
                  .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                  .ToList();

    private static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "" : path[..slash];
    }

    /// <summary>
    /// THE PACKAGE, out of an object path -- "/Game/.../Chair.Chair" becomes "/Game/.../Chair".
    ///
    /// A placement carries the OBJECT path, because that is what the viewport loads by. An import
    /// names the PACKAGE: the build hashes it for a package id, opens it for its export names and
    /// takes its leaf as the export to look for. Handing it the object path gives a wrong id and
    /// an export called "Chair.Chair" that no package contains -- so the build would refuse with a
    /// message about a missing export, which reads as the asset being wrong rather than as the
    /// path having one word too many.
    /// </summary>
    private static string PackageOf(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot > path.LastIndexOf('/') ? path[..dot] : path;
    }

    public void SelectAsset(AssetRow? asset)
    {
        PickedAsset = asset;
        HasAsset = asset is not null;
        AssetName = asset?.Name ?? "";
        AssetPath = asset is null ? "" : PackageOf(asset.Path);
        AssetWhere = asset?.Detail ?? "";
        PickedSpawn = null;
        if (asset is null)
        {
            _donorToken++;
            _donor = null; CanPlace = false; PlaceBusy = false; PlaceWhy = ""; PlaceHow = "";
            return;
        }
        FindDonor(asset);
    }

    /// <summary>
    /// FIND SOMETHING TO COPY, and prove it can be copied before offering the button.
    ///
    /// The proof is the copy itself, run on a throwaway buffer: exactly the call the build will
    /// make, against exactly the bytes it will make it against. Anything cheaper is a guess about
    /// four conditions that no property dump shows -- whether the donor has one component whose
    /// outer is it, whether the level's Actors array can be found unambiguously, whether the
    /// dependency bundle copies, and whether the export map moves cleanly. Every one of those has
    /// already been a fault, and three of them passed every arithmetic check while being wrong.
    /// </summary>
    private void FindDonor(AssetRow asset)
    {
        var token = ++_donorToken;
        _donor = null;
        CanPlace = false;
        PlaceHow = "";
        if (_provider is null) { PlaceWhy = "no game is mounted"; PlaceBusy = false; return; }
        PlaceBusy = true;
        PlaceWhy = "looking for an actor to copy...";

        _ = Task.Run(() =>
        {
            DonorPick? pick = null;
            var why = "nothing in this map can be copied";
            try { (pick, why) = ChooseDonor(asset); }
            catch (Exception e) { why = $"{e.GetType().Name}: {e.Message}"; }

            Dispatcher.UIThread.Post(() =>
            {
                if (token != _donorToken) return;      // he has clicked something else since
                _donor = pick;
                PlaceBusy = false;
                CanPlace = pick is not null;
                PlaceWhy = pick is null ? why : "";
                PlaceHow = pick is null ? "" :
                    pick.NeedsImport
                        ? $"copies {pick.At.Actor} in {pick.At.Sublevel} and imports the mesh - " +
                          "the .umap grows by one actor and one import"
                        : $"copies {pick.At.Actor} in {pick.At.Sublevel}, which already draws this - " +
                          "the .umap grows by one actor and nothing else";
                DonorSettled?.Invoke();
            });
        });
    }

    /// <summary>
    /// Donor candidates in preference order, each one tried FOR REAL until one works.
    ///
    /// TIER ONE is an actor that ALREADY DRAWS THIS ASSET. A copy inherits its donor's mesh
    /// pointer, so this needs no import at all -- the import map comes out of the build byte for
    /// byte as the game shipped it, which is both less to go wrong and one fewer thing ModVerify
    /// has to be talked out of.
    ///
    /// TIER TWO is any actor in the same sublevel that draws anything, plus an import naming the
    /// asset. That is the route the Umbaran plant took into the hub.
    ///
    /// A LEVEL INSTANCE IS NEVER A DONOR. Its package is a real .umap and could be grown, but its
    /// actors' coordinates are local to the instance while everything on screen is world -- so a
    /// position picked in the viewport would be written into a frame it does not belong to, and
    /// the actor would turn up wherever that building happens to sit. That is not a limit worth
    /// discovering in game.
    /// </summary>
    private (DonorPick? Pick, string Why) ChooseDonor(AssetRow asset)
    {
        var tried = 0;
        var why = "nothing in this map can be copied";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // SOMETHING FROM ANOTHER MAP HAS TO BE IMPORTABLE, and that is a question about the ASSET
        // rather than about any donor -- so it is asked once, first, rather than being discovered
        // on the twenty-fourth candidate.
        //
        // The index classifies by path, so a `SM_UMB_TentaclePlantA_L_Curl_A_FoliageType` sitting
        // in the folder beside the meshes it references is listed as a mesh. The import needs an
        // export of the package's OWN NAME to point at -- exactly what ModBuild looks for -- and
        // a FoliageType has none. Without this the build would refuse much later with "has no
        // export of its own name to import", which reads as the asset being broken.
        if (!asset.InLevel)
        {
            var package = PackageOf(asset.Path);
            var leaf = package[(package.LastIndexOf('/') + 1)..];

            string[] names;
            try { names = AssetLoader.Read(() => PropertyAdd.ExportNamesOf(_provider!, package)); }
            catch (Exception ex) { return (null, $"{leaf}: {ex.Message}"); }

            if (!names.Contains(leaf, StringComparer.OrdinalIgnoreCase))
                return (null, $"{leaf} has no export of its own name, so there is nothing to " +
                              $"import -- it holds " +
                              (names.Length == 0 ? "nothing" : string.Join(", ", names.Take(3))) +
                              (names.Length > 3 ? " and others" : ""));

            // AND IT HAS TO BE A MESH. Having an export of its own name is not enough, which one
            // FoliageType proved by sailing through that check: the index classifies by PATH, so
            // `SM_UMB_TentaclePlantA_L_Curl_A_FoliageType` is filed as a mesh for living in the
            // folder with the meshes it references -- and it does hold an export of its own name,
            // it is simply a FoliageType. Pointing a component's StaticMesh at one builds, reads
            // back, installs, and draws nothing.
            //
            // ASK THE PACKAGE, not the path. RealClass is the reader the browser already uses for
            // exactly this, and it costs one package open on a click.
            var cls = AssetIndex.RealClass(_provider!, package);
            if (cls is null)
                return (null, $"{leaf} could not be read, so there is no telling what it holds");
            if (!cls.Equals("StaticMesh", StringComparison.Ordinal))
                return (null, $"{leaf} is a {cls}, not a StaticMesh -- it is listed here because " +
                              "of the folder it lives in. A component can only be pointed at a mesh.");
        }

        foreach (var p in asset.Users.Where(Plain))
            if (Verify(p, false) is { } hit) return (hit, "");

        // Nothing that draws it can be copied, so fall back to copying anything and importing the
        // mesh. Prefer the sublevel the asset is already in: it is the one he is looking at.
        var home = asset.Users.FirstOrDefault(Plain)?.Package ?? "";
        foreach (var p in Placements.Where(Plain)
                                    .OrderByDescending(p => p.Package.Equals(home, StringComparison.OrdinalIgnoreCase))
                                    .ThenBy(Plainness))
            if (Verify(p, true) is { } hit) return (hit, "");

        if (tried == 0)
            why = "this map holds nothing that can be copied - every candidate is inside a level " +
                  "instance, or carries no transform of its own to place a copy with";
        return (null, why);

        static bool Plain(LevelReader.Placement p) =>
            p.Spawned is null &&
            !p.Package.Contains("/LevelInstances/", StringComparison.OrdinalIgnoreCase);

        // HOW MUCH OF ITSELF A DONOR IMPOSES ON THE COPY.
        //
        // A copy keeps its donor's rotation and its scale -- only the three location doubles are
        // overwritten -- so copying a crate that ships at a third of its size, lying on its side,
        // hands back a plant at a third of its size, lying on its side. That reads as the asset
        // being wrong rather than as the donor being unusual, and there are hundreds of donors to
        // choose from. This is the 3x3's distance from the identity: one number that is small for
        // an upright, unscaled actor and large for anything turned or resized.
        //
        // It only ORDERS the candidates. Tier one never consults it, because an actor that
        // already draws the asset is drawing it at the size the level intends.
        static float Plainness(LevelReader.Placement p)
        {
            var m = p.Model;
            var d = 0f;
            foreach (var (a, b) in new[] { (m.M11, 1f), (m.M12, 0f), (m.M13, 0f),
                                           (m.M21, 0f), (m.M22, 1f), (m.M23, 0f),
                                           (m.M31, 0f), (m.M32, 0f), (m.M33, 1f) })
                d += (a - b) * (a - b);
            return d;
        }

        DonorPick? Verify(LevelReader.Placement p, bool needsImport)
        {
            // THE POSITION HAS TO BE ON A ROOT COMPONENT. The build writes
            // "RootComponent.RelativeLocation.X"; an actor carrying its transform directly has no
            // such path, and the spawn would land wearing its donor's coordinates.
            var t = LevelReader.Transform(p.Owner, p.Scope);
            if (!t.Found || t.Prefix != "RootComponent.") return null;

            // THE NUMBER IN THE FILE HAS TO BE THE POSITION ON SCREEN.
            //
            // The build writes a RelativeLocation, and the author picks a spot in the world -- so
            // a donor whose relative and world positions differ is a donor whose copy lands
            // somewhere else. That happens whenever the root component is attached to something,
            // and it would happen for any level transform or component frame I have not thought
            // of, so this MEASURES the two rather than arguing about which cases exist. A donor
            // that fails it is skipped, not corrected: there is always another one.
            var rel = new Vector3((float) t.Location.X, (float) t.Location.Y, (float) t.Location.Z);
            var world = new Vector3(p.Model.M41, p.Model.M42, p.Model.M43);
            if ((rel - world).Length() > 0.5f) return null;

            var address = AddressOf(p.Owner, p.Scope);
            if (!seen.Add(p.Package + "|" + address)) return null;
            if (tried++ >= MaxDonorTries) return null;

            var raw = RawOf(p.Package);
            if (raw is null) { why = "could not read " + p.Package; return null; }

            // THE DRY RUN. The same call the build makes, on the same bytes, thrown away.
            string? addWhy = null;
            var grown = AssetLoader.Read(() =>
                LevelAdd.AddCopy(_provider!, raw, p.Package, address, out _, out addWhy));
            if (grown is null) { why = addWhy ?? "the copy refused"; return null; }

            // An import is only worth adding if there is a pointer to aim at it afterwards.
            if (needsImport)
            {
                string? pointWhy = null;
                var ptr = AssetLoader.Read(() =>
                    LevelEdit.LocatePointer(_provider!, raw, p.Package, address, DrawsPath, out pointWhy));
                if (ptr is null) { why = pointWhy ?? "nothing here to point at the mesh"; return null; }
            }

            return new DonorPick(p, address, needsImport);
        }
    }

    /// <summary>
    /// How many donors to TRY before giving up.
    ///
    /// Each try opens a package and copies an actor inside it, so this is a real cost rather than
    /// a paranoia limit. It is far more than enough in practice -- the first candidate has worked
    /// every time -- and it bounds the pathological case where a whole sublevel is Blueprint props
    /// whose transforms live on an archetype in another file.
    /// </summary>
    private const int MaxDonorTries = 24;

    /// <summary>
    /// PUT THE PICKED ASSET HERE, in UE centimetres.
    ///
    /// The caller supplies the position because the camera belongs to the viewport and this class
    /// has never known about it -- the same separation a gizmo drag goes through.
    /// </summary>
    public string Place(Vector3 at, bool keep = true)
    {
        if (PickedAsset is not { } asset) return "pick an asset first";
        if (_donor is not { } donor)
            return PlaceBusy ? "still looking for an actor to copy" :
                   PlaceWhy.Length > 0 ? PlaceWhy : "nothing here can be copied";

        // ONE UNTOUCHED ONE AT A TIME. Clicking down a list of two thousand assets should leave
        // two thousand nothings behind it, not two thousand actors -- so the previous one goes
        // unless he did something to it. An explicit Place always keeps, because that is somebody
        // asking for a second copy on purpose.
        if (Spawns.FirstOrDefault(x => !x.Touched) is { } loose) Spawns.Remove(loose);

        // Browsing is not an edit, so it does not fill the undo stack. The first drag takes its
        // own snapshot, and that snapshot has the spawn in it.
        if (keep) Record("place " + asset.Name);

        // The donor's model with its translation taken out, so the copy keeps the rotation and
        // the scale it inherits from the bytes it is a copy of -- which is what the build
        // produces, because only the three location doubles are overwritten afterwards.
        var basis = donor.At.Model;
        basis.M41 = 0; basis.M42 = 0; basis.M43 = 0;

        var spawn = new PendingSpawn
        {
            Target = donor.At.Package,
            Sublevel = donor.At.Sublevel,
            Donor = donor.Address,
            DonorName = donor.At.Actor,
            Asset = asset.Path,
            Mesh = donor.NeedsImport ? PackageOf(asset.Path) : "",
            Id = NewSpawnId(),
            Shape = donor.At.Owner,
            Scope = donor.At.Scope,
            Basis = basis,
            X = Round(at.X), Y = Round(at.Y), Z = Round(at.Z),
            Touched = keep,
        };
        Watch(spawn);
        Spawns.Add(spawn);
        PickedSpawn = spawn;

        RaiseChangeCount();
        SyncSpawns();
        Log.Add($"placed {asset.Name} in {spawn.Sublevel} at {spawn.Summary} ({spawn.How})");
        return keep
            ? $"placed {asset.Name} at {spawn.Summary} - it is a copy of {spawn.DonorName}"
            : $"{asset.Name} - drag it where you want it, or pick another asset";
    }

    /// <summary>Short, unique and stable: it only has to tell this tool's own spawns apart.</summary>
    private static string NewSpawnId() => "lvl-" + Guid.NewGuid().ToString("N")[..8];

    private static decimal Round(float v) => Math.Round((decimal) v, 3);

    /// <summary>
    /// A typed number moves the preview, exactly as typing into the transform fields moves a real
    /// actor.
    ///
    /// Subscribed here rather than called from a setter because a PendingSpawn is a plain record
    /// of an edit and has never known about the editor -- and because every route a spawn enters
    /// the list by, including an undo restoring copies of it, has to behave the same way. A copy
    /// is a NEW object with no subscribers, which is exactly the kind of thing that works until
    /// the first undo.
    /// </summary>
    private void Watch(PendingSpawn spawn) =>
        spawn.PropertyChanged += (_, e) =>
        {
            if (_spawnQuiet) return;
            if (e.PropertyName is nameof(PendingSpawn.X) or nameof(PendingSpawn.Y)
                               or nameof(PendingSpawn.Z)) SpawnEdited(spawn);
        };

    /// <summary>Set while a whole move is being written, so three fields are one repaint.</summary>
    private bool _spawnQuiet;

    public void SelectSpawn(PendingSpawn? spawn)
    {
        if (spawn is null) { PickedSpawn = null; return; }
        SelectAsset(_allAssets.FirstOrDefault(
            a => a.Path.Equals(spawn.Asset, StringComparison.OrdinalIgnoreCase)));
        PickedSpawn = spawn;              // SelectAsset clears it; this is the one that stands
    }

    /// <summary>Move a spawn that is already placed -- to the camera, or by a typed number.</summary>
    public void MoveSpawn(PendingSpawn spawn, Vector3 to)
    {
        Record("move " + spawn.Name);
        spawn.Touched = true;
        _spawnQuiet = true;
        spawn.X = Round(to.X);
        spawn.Y = Round(to.Y);
        spawn.Z = Round(to.Z);
        _spawnQuiet = false;
        spawn.Saved = false;
        RaiseChangeCount();
        SyncSpawns();
    }

    /// <summary>
    /// A GIZMO DRAG ON A NEW ACTOR, in UE centimetres.
    ///
    /// Inside the drag's batch, so forty mouse moves are one undo step -- the same treatment a
    /// real actor's drag gets, and for the same reason.
    /// </summary>
    private void NudgeSpawn(PendingSpawn spawn, double dx, double dy, double dz)
    {
        Record("move " + spawn.Name);
        spawn.Touched = true;
        _spawnQuiet = true;
        spawn.X += Math.Round((decimal) dx, 3);
        spawn.Y += Math.Round((decimal) dy, 3);
        spawn.Z += Math.Round((decimal) dz, 3);
        _spawnQuiet = false;
        spawn.Saved = false;
        RaiseChangeCount();
        SyncSpawns();
    }

    /// <summary>Typed into one of a spawn row's three fields.</summary>
    public void SpawnEdited(PendingSpawn spawn)
    {
        spawn.Touched = true;
        spawn.Saved = false;
        RaiseChangeCount();
        SyncSpawns();
    }

    public void RevertSpawn(PendingSpawn spawn)
    {
        Record("remove " + spawn.Name);
        Spawns.Remove(spawn);
        if (ReferenceEquals(PickedSpawn, spawn)) PickedSpawn = null;
        RaiseChangeCount();
        SyncSpawns();
    }

    /// <summary>
    /// THE PREVIEW IS DERIVED FROM THE SPAWN LIST, never accumulated -- the same discipline as
    /// SyncOffsets and SyncDraws, and for the same reason: two records of one fact is how an undo
    /// puts one back without the other.
    ///
    /// Rebuilt as a whole list rather than mutated in place, because the viewport takes
    /// <see cref="Placements"/> and diffs it against what it has loaded; a new list plus a bumped
    /// SceneVersion is the one signal it already understands.
    /// </summary>
    private void SyncSpawns()
    {
        // MOVING ONE IS NOT REBUILDING ALL OF THEM. A gizmo drag calls this once per mouse move
        // and the hub holds fifty thousand placements; rebuilding that list forty times a second
        // is the difference between a drag and a slideshow. When the previews already match the
        // spawn list, nothing structural has changed and each one can be replaced where it
        // stands.
        //
        // REPLACED, and not nudged with Offset. Offset draws in the right place and leaves `At`
        // behind at the spot the thing was created -- and `At` is what decides whether a
        // placement counts as backdrop or as blockout. A crate dragged out to the edge of a map
        // would have gone on being filed as near geometry, and one dragged in from the edge would
        // vanish the moment the Backdrop filter came on, which reads as the editor losing it.
        var showing = Placements.Where(p => p.Spawned is not null).ToList();
        if (showing.Count > 0 && showing.Count == Spawns.Count(x => x.Shape is not null) &&
            showing.All(p => p.Spawned is PendingSpawn sp && Spawns.Contains(sp)))
        {
            foreach (var p in showing)
            {
                var sp = (PendingSpawn) p.Spawned!;
                if (MathF.Abs(p.At.X - (float) sp.X) < 0.001f &&
                    MathF.Abs(p.At.Y - (float) sp.Y) < 0.001f &&
                    MathF.Abs(p.At.Z - (float) sp.Z) < 0.001f) continue;
                var at = Placements.IndexOf(p);
                if (at >= 0) Placements[at] = Preview(sp);
            }
            Repaint?.Invoke();
            return;
        }

        var real = Placements.Where(p => p.Spawned is null).ToList();

        // ONLY DRAW A SPAWN INTO THE MAP IT BELONGS TO. The pending list survives opening another
        // map -- it is an edit filed against a named package, not against whatever is on screen --
        // but its preview borrows the donor's exports, which are a different level's objects once
        // the map has changed. Drawing it anyway would put the hub's crate in Anaxes.
        var loaded = real.Select(p => p.Package).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sp in Spawns)
            if (sp.Shape is not null && loaded.Contains(sp.Target)) real.Add(Preview(sp));

        Placements = real;
        SceneVersion++;
        Raise(nameof(SceneVersion));
        Repaint?.Invoke();
    }

    /// <summary>One pending spawn, as something the viewport can draw.</summary>
    private static LevelReader.Placement Preview(PendingSpawn s)
    {
        var m = s.Basis;
        m.M41 = (float) s.X; m.M42 = (float) s.Y; m.M43 = (float) s.Z;
        return new LevelReader.Placement(
            s.Asset, m, s.Name + " (new)",
            new Vector3((float) s.X, (float) s.Y, (float) s.Z),
            s.Shape!, s.Scope, s.Target, s.Sublevel) { Spawned = s };
    }

    // ================================================================== undo
    //
    // WHAT AN UNDO HAS TO PUT BACK is three things that describe the same move: the row in the
    // pending-changes dock, the number in the property field, and where the mesh is drawn. They
    // are kept honest by making the last two DERIVED -- the fields are rebuilt from the change
    // list, and so is the drawn offset -- so there is one record of a move and undo only has to
    // restore that one.

    /// <summary>
    /// Take the snapshot for a change that is ABOUT TO HAPPEN.
    ///
    /// Inside a batch only the first call snapshots: a gizmo drag calls Nudge once per mouse
    /// move, and forty undo steps for one drag of one crate is not an undo anybody can use.
    /// </summary>
    private void Record(string label)
    {
        if (_batch > 0)
        {
            // THE NAME OF WHAT IS BEING DRAGGED. ActorName is empty for a new actor -- it has no
            // export to be named after yet -- and "gizmo drag on   " is an undo entry nobody can
            // read.
            var what = ActorName.Length > 0 ? ActorName : PickedSpawn?.Name ?? "";
            _batchStart ??= Snapshot(_batchLabel.Length > 0 && what.Length > 0
                                         ? _batchLabel + " on " + what : label);
            return;
        }
        Push(Snapshot(label));
    }

    private UndoStep Snapshot(string label) =>
        new(label, Changes.Select(c => c.Copy()).ToList(), Swaps.Select(x => x.Copy()).ToList(),
            Spawns.Select(x => x.Copy()).ToList());

    private void Push(UndoStep step)
    {
        _undo.Add(step);
        while (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _redo.Clear();                       // a new change abandons the branch redo was holding
        RaiseUndo();
    }

    /// <summary>Open a group of changes that undo together as ONE step -- a gizmo drag above all.</summary>
    public void BeginBatch(string label)
    {
        if (_batch++ == 0) { _batchStart = null; _batchLabel = label; }
    }

    public void EndBatch()
    {
        if (_batch == 0 || --_batch > 0) return;
        if (_batchStart is { } start)
        {
            // A drag that ended where it began leaves no step behind.
            if (!Same(start.Moves, Changes) || !Same(start.Swaps, Swaps) ||
                !Same(start.Spawns, Spawns)) Push(start);
            _batchStart = null;
        }
        _batchLabel = "";
    }

    private static bool Same(List<PendingMove> a, IList<PendingMove> b) =>
        a.Count == b.Count &&
        a.Zip(b).All(p => p.First.Key == p.Second.Key && Math.Abs(p.First.Now - p.Second.Now) < 0.0005);

    private static bool Same(List<PendingSwap> a, IList<PendingSwap> b) =>
        a.Count == b.Count &&
        a.Zip(b).All(p => p.First.Key == p.Second.Key &&
                          string.Equals(p.First.Now, p.Second.Now, StringComparison.OrdinalIgnoreCase));

    private static bool Same(List<PendingSpawn> a, IList<PendingSpawn> b) =>
        a.Count == b.Count &&
        a.Zip(b).All(p => p.First.Id == p.Second.Id &&
                          Math.Abs(p.First.X - p.Second.X) < 0.0005m &&
                          Math.Abs(p.First.Y - p.Second.Y) < 0.0005m &&
                          Math.Abs(p.First.Z - p.Second.Z) < 0.0005m);

    public string Undo()
    {
        if (_undo.Count == 0) return "nothing to undo";
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(Snapshot(step.Label));
        Restore(step);
        return "undid " + step.Label;
    }

    public string Redo()
    {
        if (_redo.Count == 0) return "nothing to redo";
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Snapshot(step.Label));
        Restore(step);
        return "redid " + step.Label;
    }

    private void Restore(UndoStep step)
    {
        Changes.Clear();
        foreach (var m in step.Moves) Changes.Add(m.Copy());
        Swaps.Clear();
        foreach (var x in step.Swaps) Swaps.Add(x.Copy());

        // A SPAWN IS PUT BACK THE SAME WAY, and the picked one is dropped rather than re-found:
        // the row it pointed at is a copy that no longer exists, and holding the old object would
        // edit something the list does not contain.
        Spawns.Clear();
        foreach (var x in step.Spawns) { var c = x.Copy(); Watch(c); Spawns.Add(c); }
        PickedSpawn = null;
        ApplyPendingToFields();

        // The panel has to be put back too, not just the list -- an undo that leaves the Draws row
        // reading the mesh it no longer draws is the drift this whole derived-state discipline is
        // here to stop.
        DrawsNow = _wasMesh;
        ApplyPendingToDraws();

        MarkEdited();
        RaiseChangeCount();
        SyncOffsets();
        SyncDraws();
        SyncSpawns();
        RaiseUndo();
    }

    private void RaiseUndo()
    {
        CanUndo = _undo.Count > 0;
        CanRedo = _redo.Count > 0;
        UndoHint = CanUndo ? "Undo " + _undo[^1].Label + "   (Ctrl+Z)" : "Nothing to undo yet";
        RedoHint = CanRedo ? "Redo " + _redo[^1].Label + "   (Ctrl+Y)" : "Nothing to redo";
    }

    /// <summary>
    /// A PLACEMENT'S OFFSET IS DERIVED, NEVER ACCUMULATED.
    ///
    /// The gizmo used to add its delta straight onto the placement it was dragging, which made
    /// the picture and the change list two separate records of one fact -- and an undo could put
    /// one back without the other. Recomputing the offset from the moves makes the drawn level a
    /// VIEW of the change list, so it cannot disagree with it. Typing into the property fields
    /// moves the mesh too now, which it never did before.
    /// </summary>
    private void SyncOffsets()
    {
        if (Placements.Count == 0) return;

        var wanted = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var turned = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var sized = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in Changes)
        {
            var key = c.Target + "|" + c.Address;
            var d = (float) (c.Now - c.Was);

            if (c.Path.Contains("RelativeLocation", StringComparison.Ordinal))
            {
                var v = c.Field switch
                {
                    "X" => new Vector3(d, 0, 0),
                    "Y" => new Vector3(0, d, 0),
                    "Z" => new Vector3(0, 0, d),
                    _   => Vector3.Zero,
                };
                if (v != Vector3.Zero) wanted[key] = wanted.GetValueOrDefault(key) + v;
            }
            else if (c.Path.Contains("RelativeRotation", StringComparison.Ordinal))
            {
                var v = c.Field switch
                {
                    "Pitch" => new Vector3(d, 0, 0),
                    "Yaw"   => new Vector3(0, d, 0),
                    "Roll"  => new Vector3(0, 0, d),
                    _       => Vector3.Zero,
                };
                if (v != Vector3.Zero) turned[key] = turned.GetValueOrDefault(key) + v;
            }
            else if (c.Path.Contains("RelativeScale3D", StringComparison.Ordinal))
            {
                // A SCALE IS A RATIO, not a difference. Dividing is what makes 2 -> 4 read as
                // "twice as big" whatever the shipped value happened to be.
                var f = Math.Abs(c.Was) < 1e-6 ? 1f : (float) (c.Now / c.Was);
                var v = c.Field switch
                {
                    "X" => new Vector3(f, 1, 1),
                    "Y" => new Vector3(1, f, 1),
                    "Z" => new Vector3(1, 1, f),
                    _   => Vector3.One,
                };
                if (v != Vector3.One) sized[key] = sized.GetValueOrDefault(key, Vector3.One) * v;
            }
        }

        foreach (var p in Placements)
        {
            if (p.Spawned is not null) continue;          // see Placement.Spawned
            var key = p.Package + "|" + AddressOf(p.Owner, p.Scope);
            var v = wanted.Count == 0 ? Vector3.Zero : wanted.GetValueOrDefault(key);
            if (p.Offset != v) p.Offset = v;

            var warp = Matrix4x4.Identity;
            if (turned.Count > 0 || sized.Count > 0)
            {
                var r = turned.GetValueOrDefault(key);
                var k = sized.GetValueOrDefault(key, Vector3.One);
                if (r != Vector3.Zero || k != Vector3.One)
                {
                    // ABOUT THE ACTOR'S ROOT, not about each mesh's own middle: turning every
                    // component of an actor about itself takes the actor apart.
                    //
                    // UE's own rotator-to-quaternion, borrowed from the exporter rather than
                    // written again -- two copies of a rotation is how the preview ends up
                    // disagreeing with the number in the box.
                    var q = LevelExport.FromRotator(r.X, r.Y, r.Z);
                    warp = Matrix4x4.CreateTranslation(-p.Pivot)
                         * Matrix4x4.CreateScale(k)
                         * Matrix4x4.CreateFromQuaternion(q)
                         * Matrix4x4.CreateTranslation(p.Pivot);
                }
            }
            if (p.Warp != warp) p.Warp = warp;
        }
        Repaint?.Invoke();
    }

    // ================================================================== the mod project

    /// <summary>When this executable was built, for telling a stale window from a fresh one.</summary>
    public static string BuildStamp
    {
        get
        {
            try
            {
                var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
                    exe = Environment.ProcessPath;
                return exe is not null && System.IO.File.Exists(exe)
                    ? System.IO.File.GetLastWriteTime(exe).ToString("dd MMM HH:mm")
                    : "unknown";
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>How many areas of the open map had versions, and what was done about it.</summary>
    private string _variantSummary = "";
    public string VariantSummary
    {
        get => _variantSummary;
        set { if (Set(ref _variantSummary, value)) Raise(nameof(HasVariants)); }
    }
    public bool HasVariants => _variantSummary.Length > 0;

    /// <summary>
    /// THE OPEN MAP'S LIGHT RIG, merged across every sublevel that carries one.
    ///
    /// A map keeps its lights in their own sublevel -- MM_05_010_UmbaraRescue_Lighting, HUB_Lighting
    /// -- so this is filled in as those stream, the same way placements are.
    /// </summary>
    public LevelLights.Rig Lights { get; private set; } =
        new(new List<LevelLights.Sun>(), null, new List<LevelLights.Spot>(), null, false);

    private string _exposureText = "0.7";       // must match the slider AND Viewport.Exposure
    public string ExposureText { get => _exposureText; set => Set(ref _exposureText, value); }

    /// <summary>What the rig says, for a harness or the Log panel to print.</summary>
    public IEnumerable<string> LightReport() => RigNotes.Concat(Lights.Describe());

    /// <summary>Every sublevel's rig as it was read, kept so the choice can be redone.</summary>
    private readonly List<(string Sublevel, LevelLights.Rig Rig)> _rigs = new();

    /// <summary>What Choose decided and why, for the Log panel and for --lights.</summary>
    public List<string> RigNotes { get; } = new();

    /// <summary>
    /// A SUBLEVEL'S RIG ARRIVES; RE-DECIDE WHICH ONE THE LEVEL IS USING.
    ///
    /// This used to CONCATENATE, which lit the hub with four suns at once -- see
    /// LevelLights.Choose for the measurement. Re-deciding from the whole list on every arrival
    /// rather than folding one in is deliberate: sublevels stream in an order nobody controls, and
    /// a fold makes the answer depend on that order. Dozens of entries, once per sublevel.
    /// </summary>
    private void MergeLights(string sublevel, LevelLights.Rig add)
    {
        _rigs.Add((sublevel, add));
        RigNotes.Clear();
        Lights = LevelLights.Choose(_rigs, LevelReader.Family, RigNotes);
    }

    private ModProject? _project;

    /// <summary>
    /// The spawn ids THIS EDITOR has written into the open project.
    ///
    /// Saving rewrites exactly these and leaves every other spawn in the file alone -- so a
    /// project that already had spawns in it, from a hand-written .json or from the Studio, keeps
    /// them, and one reverted here is removed rather than left behind.
    /// </summary>
    private readonly HashSet<string> _spawnIds = new(StringComparer.OrdinalIgnoreCase);

    public string OpenProject(string path)
    {
        try
        {
            _project = ModProject.Load(path);
            HasProject = true;
            // A DIFFERENT FILE'S SPAWN IDS ARE NOT OURS TO REWRITE. Forgetting them here is what
            // makes "open another project and save" append rather than overwrite somebody else's
            // actors, which is the only safe reading of a file this session has not seen.
            _spawnIds.Clear();
            ProjectName = _project.DisplayName.Length > 0 ? _project.DisplayName : _project.Name;
            RaiseChangeCount();
            if (Changes.Count + Swaps.Count + Spawns.Count == 0)
            {
                var already = _project.Overrides.Sum(o => o.Spawns.Count);
                ProjectDetail = $"{_project.Overrides.Count} override(s) already in it" +
                                (already > 0 ? $", {already} of them new actor(s)" : "");
            }
            Log.Add($"opened project {path}");
            return $"opened {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception e) { Log.Add("project: " + e.Message); return "could not open: " + e.Message; }
    }

    /// <summary>
    /// Write every pending move into the project, grouped into one override per package.
    ///
    /// MERGED, not replaced: a move already in the file for an actor this session did not touch
    /// stays exactly where it is. Overwriting the list would silently drop somebody's earlier
    /// work the first time they opened a second map.
    /// </summary>
    public string SaveMoves()
    {
        if (_project is null) return "no mod project is open";
        if (Changes.Count + Swaps.Count + Spawns.Count == 0) return "nothing to save";

        try
        {
            foreach (var group in Changes.GroupBy(c => c.Target, StringComparer.OrdinalIgnoreCase))
            {
                var ov = Override(group.Key);
                foreach (var c in group)
                {
                    ov.Moves.RemoveAll(m => m.Actor.Equals(c.Address, StringComparison.OrdinalIgnoreCase) &&
                                            m.Path.Equals(c.Path, StringComparison.OrdinalIgnoreCase));
                    ov.Moves.Add(new ModOverride.Move(c.Address, c.Path, c.Was, c.Now));
                }
            }

            // SWAPS, beside the moves and merged the same way. `Was` goes into the file as well as
            // the new mesh: the build checks it before writing, so an edit that has drifted onto
            // the wrong actor refuses instead of landing.
            foreach (var group in Swaps.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase))
            {
                var ov = Override(group.Key);
                foreach (var x in group)
                {
                    ov.Swaps.RemoveAll(m => m.Actor.Equals(x.Address, StringComparison.OrdinalIgnoreCase) &&
                                            m.Path.Equals(x.Path, StringComparison.OrdinalIgnoreCase));
                    ov.Swaps.Add(new ModOverride.Swap(x.Address, x.Path, x.Was, x.Now));
                }
            }

            // SPAWNS, which are the one kind of edit with no natural key.
            //
            // A move is identified by the actor and the property it writes, so saving twice
            // replaces it. Two identical spawns are two actors, which somebody may well want, so
            // there is nothing to merge on -- and a second save would silently double every actor
            // this editor had added. Each spawn carries an Id instead, and this rewrites exactly
            // the ones it minted: a spawn somebody else put in the file is never touched, and one
            // reverted here goes out of the file on the next save because its Id is no longer in
            // the pending list.
            foreach (var group in Spawns.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase))
                Override(group.Key);                      // so a package with only spawns gets one

            foreach (var ov in _project.Overrides)
            {
                var mine = Spawns.Where(x => x.Target.Equals(ov.Target, StringComparison.OrdinalIgnoreCase))
                                 .ToList();
                ov.Spawns.RemoveAll(s => s.Id.Length > 0 && _spawnIds.Contains(s.Id));
                foreach (var x in mine)
                    ov.Spawns.Add(new ModOverride.Spawn(x.Donor, (double) x.X, (double) x.Y,
                                                        (double) x.Z, false, x.Mesh, x.Id));
            }
            _spawnIds.Clear();
            foreach (var x in Spawns) _spawnIds.Add(x.Id);

            _project.Save();
            foreach (var c in Changes) c.Saved = true;
            foreach (var x in Swaps) x.Saved = true;
            foreach (var x in Spawns) { x.Saved = true; x.Touched = true; }

            var parts = new List<string>();
            if (Changes.Count > 0) parts.Add($"{Changes.Count} move(s)");
            if (Swaps.Count > 0) parts.Add($"{Swaps.Count} swap(s)");
            if (Spawns.Count > 0) parts.Add($"{Spawns.Count} new actor(s)");
            var what = string.Join(" and ", parts);
            var where = System.IO.Path.GetFileName(_project.FilePath);
            Log.Add($"saved {what} to {where}");
            // NOT "build it in the Mod Studio" any more. This app builds and installs now, and an
            // instruction to go and open another program is the split he asked to be rid of.
            ProjectDetail = $"{what} saved";
            return $"saved {what} to {where} - Build and install puts it in the game";
        }
        catch (Exception e) { Log.Add("save: " + e.Message); return "could not save: " + e.Message; }
    }

    /// <summary>The override for one package, made if the project does not have one yet.</summary>
    private ModOverride Override(string target)
    {
        var ov = _project!.Overrides.FirstOrDefault(
            o => o.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (ov is null) _project.Overrides.Add(ov = new ModOverride { Target = target });
        return ov;
    }

    // --- filters --------------------------------------------------------
    private string _mapFilter = "";
    public string MapFilter { get => _mapFilter; set { if (Set(ref _mapFilter, value)) BuildMapTree(); } }

    private string _actorFilter = "";
    public string ActorFilter { get => _actorFilter; set { if (Set(ref _actorFilter, value)) BuildActorTree(); } }

    private string? _openMapId;

    // ================================================================== mount

    /// <summary>
    /// What to do when the game cannot be opened: the way back is a menu entry, named here once so every message
    /// that points at it says the same thing (the Mod Studio's wording, for the same entry).
    /// </summary>
    internal const string FindAgainHint = "use Project > Find the game again… to say where it is";

    /// <summary>For a failed mount that is not about where the game is (a failed download, say).</summary>
    internal const string TryAgainHint = "try again (Project > Find the game again… mounts it once more)";

    private bool _mounting;
    private string? _gameInstall;

    /// <summary>The install folder the game was last opened from, or null before it has been.</summary>
    public string? GameInstallInUse => _gameInstall;

    /// <summary>
    /// True once the game is mounted, or while it is being. Changing the install then needs the program opened
    /// again - the mount is held by the index, the viewport's meshes and every open map - whereas with nothing
    /// mounted there is nothing to tear down and a new answer can be mounted straight away.
    /// </summary>
    public bool GameInUse => _mounting || _provider is not null;

    /// <summary>
    /// Open the game from the profile the start decided on (GameStart: the remembered install, a profile the
    /// Mod Studio left, or the answer to the question window). A null profile is "open without the game" -
    /// the person closed the question - and the editor says where the way back is.
    /// </summary>
    public async Task MountAsync(string? profilePath)
    {
        if (profilePath is null)
        {
            Log.Add("game: not found - " + FindAgainHint);
            Status = "the game has not been found — " + FindAgainHint;
            Busy = false;
            return;
        }
        if (GameInUse) return;     // a second call while one is running (or after one worked) mounts nothing twice

        _mounting = true;
        var lines = new List<string>();
        IDisposable? opened = null;
        try
        {
            var path = profilePath;

            var mounted = await Task.Run(() => GameMount.OpenAsync(path, s => lines.Add(s)));
            opened = mounted.Provider as IDisposable;

            // THE MAPS ARE READ BEFORE ANYTHING IS KEPT. GameInUse is "a provider is held", so a provider stored
            // before this read could fail would leave a game that is not usable looking mounted: a retry would be
            // ignored and Find the game again would say "reopen". Only a mount that has worked is stored.
            var maps = await Task.Run(() => LevelReader.Maps(mounted.Index));

            _provider = mounted.Provider;
            opened = null;
            Index = mounted.Index;
            // KEPT FOR THE INSTALL. The profile knows where the game's Paks folder is, which is
            // the one thing Build and install needs that the project does not carry.
            _profile = mounted.Profile;
            _maps = maps;

            foreach (var l in lines) Log.Add(l);
            Status = mounted.Summary;
            MapCount = $"{_maps.Count} levels";
            // WHICH BUILD IS THIS? An hour was spent on a fault that had already been fixed,
            // because the editor that was open had been started before the fix and there was no
            // way to tell by looking. The binary's own timestamp settles it at a glance.
            _gameInstall = GameFinder.InstallOf(mounted.Profile.PaksDirectory);
            // THE PROGRAM AND ITS VERSION, then the game. The build time only in a developer build, where
            // telling a stale window from a fresh one is the point; for a person who downloaded this it is
            // the time their copy was unpacked, which says nothing.
            StatusRight = $"{AppName}   ·   {mounted.Profile.Name}" +
#if DEV
                          $"   ·   built {BuildStamp}" +
#endif
                          "";
            BuildMapTree();
        }
        catch (Exception e)
        {
            // A mount that fails leaves nothing behind (the fields above are only set once it has worked, and
            // a provider opened for a mount that then failed is let go), so the editor can simply try again.
            try { opened?.Dispose(); } catch { /* it failed already; a second failure helps nobody */ }
            foreach (var l in lines) Log.Add(l);

            // THE FIND-THE-GAME HINT ONLY WHERE IT HELPS. A folder that is not there, a file that is missing or a
            // path that cannot be read are about WHERE THE GAME IS; the first mount also downloads two libraries,
            // and a failed download is not, so for anything else the advice is to try again (Find the game again
            // mounts it once more as well).
            var hint = e is IOException or UnauthorizedAccessException ? FindAgainHint : TryAgainHint;
            Log.Add("FAILED: " + e.Message + " - " + hint);
            Status = "mount failed — " + e.Message + " — " + hint;
        }
        finally { _mounting = false; Busy = false; }
    }

    // ================================================================== maps

    private static readonly string[] GroupOrder = { "Skirmish", "Story Missions", "Hero Missions", "Other" };

    private void BuildMapTree()
    {
        MapTree.Clear();
        var f = MapFilter.Trim();
        var visible = _maps.Where(m => f.Length == 0 || m.Id.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var group in GroupOrder)
        {
            var inGroup = visible.Where(m => m.Group == group).ToList();
            if (inGroup.Count == 0) continue;

            var g = new Node { Title = group, Detail = inGroup.Count.ToString(), IsExpanded = group == "Skirmish" || f.Length > 0 };
            foreach (var m in inGroup)
            {
                // EVERY MAP LISTS ITS SUBLEVELS STRAIGHT AWAY, not only the open one.
                //
                // They came from the file index, so this costs nothing -- and without it the only
                // way to reach a sublevel was to open the whole map first, which is exactly what
                // opening a sublevel is FOR. His words: "i can't just load that, it's not on the
                // list". The hub is 60 sublevels and 50,745 placements and takes minutes; the one
                // holding the holotable is 2,259 exports and arrives in seconds.
                //
                // LoadMapAsync still replaces these with what the root says it actually STREAMS,
                // which is the better list -- a folder listing is a guess. This is the one
                // available before anything has been read.
                // SAY HOW MANY ARE INSIDE. His words: "it's unclear how to load a sublevel" --
                // and the row gave no reason to think there was anything under the arrow, because
                // a map and a sublevel were two identical lines of text. The count is the one
                // fact that makes the arrow mean something before it is opened. LoadMapAsync
                // replaces this with the export total once the map is actually read.
                var node = new Node
                {
                    Title = m.Id, Payload = m, IsExpanded = m.Id == _openMapId,
                    Detail = m.Sublevels.Count == 1 ? "1 sublevel" : $"{m.Sublevels.Count} sublevels",
                };
                FillSublevels(node, m);
                WatchExpand(node, m);
                g.Children.Add(node);
            }
            MapTree.Add(g);
        }
    }

    /// <summary>
    /// OPENING A MAP'S ARROW READS ITS ROOT, and nothing else.
    ///
    /// The list a map shows before anything is read comes from the FOLDER, and a folder listing is
    /// a guess: the hub's folder holds 15 while the map actually streams 60, and
    /// `HUB_Environment_Pit` -- the one with the holotable in it -- is not among the 15. So "open
    /// the arrow and click a sublevel" reached a quarter of them, and the rest needed the whole
    /// 50,745-placement map loaded first, which is the exact thing somebody opening one sublevel
    /// is trying to avoid. His words, twice now: "i can't just load that, it's not on the list",
    /// and "is there no way to just render the sublevel only instead of the whole level?"
    ///
    /// The truthful list costs ONE package: the root says what it streams. That is a second or
    /// two against minutes, and it happens when the arrow is opened rather than up front, because
    /// there are 79 maps and nobody opens all of them.
    /// </summary>
    private void WatchExpand(Node node, MapEntry m)
    {
        node.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(Node.IsExpanded) || !node.IsExpanded) return;
            if (!_streamedAsked.Add(m.Id)) return;
            _ = FillStreamedAsync(node, m);
        };
    }

    /// <summary>Maps whose root has been read for the real sublevel list.</summary>
    private readonly HashSet<string> _streamedAsked = new(StringComparer.OrdinalIgnoreCase);

    private async Task FillStreamedAsync(Node node, MapEntry m)
    {
        if (_provider is null) return;
        var was = node.Detail;
        node.Detail = "reading…";
        try
        {
            var streamed = await Task.Run(() =>
            {
                var rootExports = LevelReader.Read(_provider, m.RootPath);
                return AssetLoader.Read(() => LevelReader.StreamedPackages(rootExports));
            });

            // The root is a sublevel in its own right AND the only thing that knows the rest.
            var parts = new List<(string Container, string Name)>
                        { (m.RootPath, LevelReader.Bare(m.RootPath)) };
            foreach (var package in streamed)
                parts.Add((LevelReader.ContainerOf(package), LevelReader.Leaf(package)));

            node.Children.Clear();
            foreach (var (container, name) in parts)
            {
                var child = new Node
                {
                    Title = name,
                    SublevelName = name,
                    Payload = new SublevelEntry(m, container, LevelReader.PackageNameOf(container),
                                                name,
                                                container.Contains("/LevelInstances/", StringComparison.OrdinalIgnoreCase)),
                    Shown = !HiddenSublevels.Contains(name),
                };
                WatchShown(child);
                node.Children.Add(child);
            }
            node.Detail = parts.Count == 1 ? "1 sublevel" : $"{parts.Count} sublevels";
            node.IsExpanded = true;
        }
        catch (Exception ex)
        {
            node.Detail = was;
            Log.Add($"{m.Id}: could not read what it streams - {ex.Message}");
            _streamedAsked.Remove(m.Id);          // so opening it again tries once more
        }
    }

    /// <summary>
    /// GIVE EVERY SUBLEVEL THAT IS DRAWN A ROW, and every row its placement count.
    ///
    /// The streamed ones are already there; this adds the level instances underneath them, marked
    /// as such, so the whole of what the viewport draws can be switched off a piece at a time. The
    /// count is on every row because it is the number that tells him which one is worth turning
    /// off -- the hub's biggest is 21,657 placements of cargo dock.
    /// </summary>
    private void AddDrawnSublevels(Node node, IEnumerable<string> streamed)
    {
        var known = streamed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = Placements.Where(p => p.Spawned is null)
                               .GroupBy(p => p.Sublevel, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var child in node.Children)
            if (child.IsSublevel && counts.TryGetValue(child.SublevelName, out var n))
                child.Detail = $"{n:N0}";

        foreach (var (name, n) in counts.OrderByDescending(kv => kv.Value))
        {
            if (known.Contains(name)) continue;
            var child = new Node
            {
                Title = name,
                SublevelName = name,
                Detail = $"{n:N0}",
                Class = "instance",
                Shown = !HiddenSublevels.Contains(name),
            };
            WatchShown(child);
            node.Children.Add(child);
        }
    }

    /// <summary>
    /// A sublevel row's tick box reports back here.
    ///
    /// Subscribed rather than bound through a command, for the reason the spawn rows are: a Node
    /// is a plain record of a row and has never known about the editor, and every route a sublevel
    /// node is MADE by has to behave the same -- the folder guess, and the real list the root
    /// hands back when the arrow is opened.
    /// </summary>
    private void WatchShown(Node child)
    {
        child.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(Node.Shown)) return;
            if (child.IsSublevel) ShowSublevel(child.SublevelName, child.Shown);
        };
    }

    private void FillSublevels(Node node, MapEntry m)
    {
        node.Children.Clear();
        foreach (var s in m.Sublevels)
        {
            var isInstance = s.Contains("/LevelInstances/", StringComparison.OrdinalIgnoreCase);
            var child = new Node
            {
                Title = LevelReader.Bare(s),
                SublevelName = LevelReader.Bare(s),
                Payload = new SublevelEntry(m, s, LevelReader.PackageNameOf(s), LevelReader.Bare(s), isInstance),
            };
            node.Children.Add(child);
            child.Shown = !HiddenSublevels.Contains(child.Title);
            WatchShown(child);
            _ = isInstance;
        }
    }

    /// <summary>
    /// A map was clicked: read its ROOT, ask the root which sublevels it streams, then load all of
    /// them into one scene.
    ///
    /// The folder listing is only a guess at what a map contains, and on this game it is often
    /// wrong -- SK_Anaxes_045 streams SK_Anaxes_040's terrain and a shared fort. The root's
    /// LevelStreaming actors are the game's own answer, so they replace the guess as soon as they
    /// are known.
    /// </summary>
    public void OpenMap(Node node, MapEntry m)
    {
        _openMapId = m.Id;
        _openedName = m.Id;             // what "Export what's loaded to FBX" calls the file (EditorExport)
        Raise(nameof(CanExport));       // ... and the entry is enabled from here on
        Lights = new LevelLights.Rig(new List<LevelLights.Sun>(), null,
                                     new List<LevelLights.Spot>(), null, false);
        _rigs.Clear();
        RigNotes.Clear();
        MapVersion++;
        Raise(nameof(MapVersion));
        FillSublevels(node, m);
        node.IsExpanded = true;
        _ = LoadMapAsync(node, m);
    }

    private async Task LoadMapAsync(Node node, MapEntry m)
    {
        if (_provider is null) return;

        Busy = true;
        var merged = new List<LevelReader.Placement>();
        var browse = new List<BrowseRow>();
        _allBrowse = browse;
        RaiseBrowse();
        var total = 0;

        // The root first: it is both a sublevel in its own right and the only thing that knows
        // what else belongs to this map.
        var rootExports = await Task.Run(() =>
        { try { return LevelReader.Read(_provider, m.RootPath); } catch { return Array.Empty<UObject>(); } });
        if (_openMapId != m.Id) { Busy = false; return; }

        var streamed = await Task.Run(() => AssetLoader.Read(() => LevelReader.StreamedPackages(rootExports)));
        var parts = new List<(string Container, string Name)> { (m.RootPath, LevelReader.Bare(m.RootPath)) };
        foreach (var package in streamed)
            parts.Add((LevelReader.ContainerOf(package), LevelReader.Leaf(package)));

        // The tree now shows what the map STREAMS rather than what sits in its folder.
        node.Children.Clear();
        foreach (var (container, name) in parts)
            node.Children.Add(new Node
            {
                Title = name,
                Payload = new SublevelEntry(m, container, LevelReader.PackageNameOf(container), name,
                                            container.Contains("/LevelInstances/", StringComparison.OrdinalIgnoreCase)),
            });

        // ONE FRESH READING FOR THE WHOLE MAP, not one per sublevel: the counters and the fact
        // list are cumulative on purpose here, because what the author is looking at is all 60
        // sublevels at once and the interesting number is the total.
        LevelReader.ResetVisibility();

        for (var i = 0; i < parts.Count; i++)
        {
            var exports = i == 0
                ? rootExports
                : await Task.Run(() =>
                  { try { return LevelReader.Read(_provider, parts[i].Container); } catch { return Array.Empty<UObject>(); } });
            if (_openMapId != m.Id) { Busy = false; return; }

            node.Children[i].Detail = exports.Length == 0 ? "unreadable" : exports.Length.ToString("N0");
            total += exports.Length;
            node.Detail = total.ToString("N0");

            if (exports.Length == 0) continue;
            var before = merged.Count;
            merged.AddRange(await Task.Run(() => AssetLoader.Read(() => LevelReader.Placements(
                exports, LevelReader.PackageNameOf(parts[i].Container), parts[i].Name, _provider, 0, new()))));
            MergeLights(parts[i].Name, await Task.Run(() => AssetLoader.Read(() => LevelLights.Read(exports))));
            Console.WriteLine($"PART {parts[i].Name,-46} {exports.Length,7:N0} exports  " +
                              $"{merged.Count - before,7:N0} placements" +
                              (parts[i].Container.Contains("/LevelInstances/", StringComparison.OrdinalIgnoreCase)
                                  ? "   [a LEVEL INSTANCE package, streamed in its own right]" : ""));

            // Published as each sublevel lands, so the level builds up on screen rather than
            // appearing all at once after a wait with nothing to look at.
            Placements = merged.ToList();
            _levelAssets = AssetRows(Placements);
            RaiseAssets();
            PublishFacts();
            SyncOffsets();
            SyncDraws();            // and a pending SWAP survives it too, for the same reason
            SyncSpawns();           // and so does a pending SPAWN, which is drawn from its record
            SceneVersion++;
            Raise(nameof(SceneVersion));

            // THE BROWSER FILLS IN AS THE MAP LOADS, for the same reason: a list that appears
            // only when the last of eleven sublevels has been read is a list you wait for.
            browse.AddRange(await Task.Run(() => BrowseRows(
                exports, LevelReader.PackageNameOf(parts[i].Container), parts[i].Name)));
            _allBrowse = browse.OrderBy(r => r.Sublevel, StringComparer.OrdinalIgnoreCase)
                               .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RaiseBrowse();
        }

        // WHAT IS ACTUALLY BEING DRAWN, which is not the same list as what the root streams.
        //
        // A LEVEL INSTANCE is a whole building in another file, followed recursively by the
        // placement reader -- so the hub draws `Hub_Environment_Hangar_StillwatchLAATs` and a
        // hundred others that are nowhere in the 60 it streams. Every one of them is a thing that
        // may or may not be in the game at any moment, and this is the only list that has them.
        AddDrawnSublevels(node, parts.Select(p => p.Name));

        SublevelTitle = $"{m.Id}   ·   {parts.Count} sublevel(s)   ·   {total:N0} exports   ·   " +
                        $"{merged.Count:N0} placements";
        Busy = false;
    }

    // ================================================================== actors

    /// <summary>
    /// A SUBLEVEL'S LIGHTS ARE USUALLY IN ANOTHER SUBLEVEL, so opening one on its own lit it
    /// wrong -- and every judgement made in that view was made under the wrong light.
    ///
    /// `HUB_Hangar.umap` holds **2** lights. `HUB_Lighting.umap` holds **131**. Open the hangar by
    /// itself and the viewport reported `lights 2/2`, which reads as a complete rig and is not
    /// one: the hangar's floodlights live in the lighting sublevel, the sun and sky live in
    /// `HUB_Lighting_EXT_Day`, and neither is loaded. Whole nights of material work -- the RGB
    /// trim, the normal maps, the detail normals -- were assessed in a room lit by two incidental
    /// fixtures, which is the single most misleading thing this viewport has done.
    ///
    /// So a single-sublevel open now borrows the MAP's rig. Geometry stays as it was: one
    /// sublevel, quick to load, which is the whole point of opening one.
    ///
    /// THE CANDIDATES ARE NAME-FILTERED and that is a heuristic, stated rather than hidden. The
    /// authoritative sublevel list comes from the root's streaming actors, but which of those
    /// sixty is a LIGHTING sublevel is not a thing the package says -- reading all sixty for
    /// their lights would cost most of the load time this path exists to avoid. So: the root
    /// itself, plus any sublevel whose name carries "Lighting" or "_LGT". That finds
    /// HUB_Lighting, HUB_Lighting_Construction and the three EXT variants, and
    /// LevelLights.Choose then does the real work -- it already discards the Construction rig as
    /// a development one and collapses Day/Dawn/Night to the chosen variant.
    ///
    /// What it misses is a light in a sublevel named something else, and the count printed below
    /// is what makes that visible instead of silent.
    /// </summary>
    private async Task BorrowMapRig(SublevelEntry s)
    {
        if (_provider is not { } provider) return;

        var rootExports = await Task.Run(() =>
        { try { return LevelReader.Read(provider, s.Map.RootPath); } catch { return Array.Empty<UObject>(); } });
        if (rootExports.Length == 0) return;
        if (_sublevel != s) return;

        // The variants must be planned before Choose runs, exactly as --lightscan does it, or the
        // hub arrives lit by Day AND Dawn AND Night at once.
        LevelReader.PlanVariants(s.Map, Index, null);

        var streamed = await Task.Run(() => AssetLoader.Read(() => LevelReader.StreamedPackages(rootExports)));
        var candidates = streamed.Select(LevelReader.ContainerOf)
            .Where(c => LevelReader.Leaf(c).Contains("Lighting", StringComparison.OrdinalIgnoreCase) ||
                        LevelReader.Leaf(c).Contains("_LGT", StringComparison.OrdinalIgnoreCase))
            .Where(c => !string.Equals(c, s.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        MergeLights(LevelReader.Bare(s.Map.RootPath),
                    await Task.Run(() => AssetLoader.Read(() => LevelLights.Read(rootExports))));

        var borrowed = 0;
        foreach (var container in candidates)
        {
            var ex = await Task.Run(() =>
            { try { return LevelReader.Read(provider, container); } catch { return Array.Empty<UObject>(); } });
            if (_sublevel != s) return;
            if (ex.Length == 0) continue;
            MergeLights(LevelReader.Leaf(container),
                        await Task.Run(() => AssetLoader.Read(() => LevelLights.Read(ex))));
            borrowed++;
        }

        Console.WriteLine($"RIG   borrowed from {borrowed:N0} lighting sublevel(s) of {s.Map.Id} " +
                          $"plus its root; {Lights.Spots.Count:N0} local light(s), " +
                          $"{Lights.Suns.Count:N0} sun(s)" +
                          (Lights.Sky is null ? ", no sky" : ", sky") +
                          (Lights.Fog is null ? ", no fog" : ", fog"));
        SceneVersion++;
        Raise(nameof(SceneVersion));
    }

    private SublevelEntry? _sublevel;

    public async Task OpenSublevelAsync(SublevelEntry s)
    {
        if (_provider is null) return;
        _sublevel = s;
        _openedName = s.Name;           // see OpenMap
        Raise(nameof(CanExport));
        MapVersion++;
        Raise(nameof(MapVersion));
        Busy = true;
        SublevelTitle = "reading " + s.Name + "…";
        ActorTree.Clear();
        ClearActor();

        // A FRESH READING. The skip counters and the fact list accumulate across the recursive
        // walk into level instances, so they are cleared by whoever begins a read.
        LevelReader.ResetVisibility();

        UObject[] exports;
        try { exports = await Task.Run(() => LevelReader.Read(_provider, s.Path)); }
        catch (Exception e)
        {
            SublevelTitle = "could not read " + s.Name;
            Log.Add($"{s.Name}: {e.Message}");
            Busy = false;
            return;
        }
        if (_sublevel != s) return;

        _exports = exports;
        _rows = await Task.Run(() => Rows(exports));
        Placements = await Task.Run(() => AssetLoader.Read(() => LevelReader.Placements(exports, _sublevel?.Package ?? "", _sublevel?.Name ?? "", _provider, 0, new())));
        _levelAssets = AssetRows(Placements);
        RaiseAssets();
        PublishFacts();
        MergeLights(s.Name, await Task.Run(() => AssetLoader.Read(() => LevelLights.Read(exports))));
        await BorrowMapRig(s);
        SyncOffsets();                  // a pending move survives a reload and still draws moved
        SyncDraws();                    // and so does a pending swap -- new Placement objects
        SyncSpawns();                   // and a pending spawn, which is drawn from its own record
        SceneVersion++;
        Raise(nameof(SceneVersion));
        var classes = exports.GroupBy(e => e.ExportType ?? "?").ToList();
        SublevelTitle = $"{exports.Length:N0} exports   ·   {classes.Count} classes";
        ActorFilterHint = $"Filter {exports.Length:N0} exports in {s.Name}…";

        Classes.Clear();
        foreach (var c in classes.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var layer = LevelReader.LayerOf(c.Key);
            Classes.Add(new ClassRow { Name = c.Key, Count = c.Count(), Layer = layer, Dot = Layers.For(layer) });
        }
        ClassesTitle = $"CLASSES IN {s.Name.ToUpperInvariant()}";

        LayerChips.Clear();
        foreach (var layer in LevelReader.LayerOrder)
        {
            var n = _rows.Count(r => r.Layer == layer);
            if (n == 0) continue;
            var chip = new LayerChip
            {
                Name = layer, Count = n, Dot = Layers.For(layer),
                IsOn = layer != LevelReader.LayerScript,
            };
            chip.PropertyChanged += (_, _) => BuildActorTree();
            LayerChips.Add(chip);
        }

        BuildActorTree();
        MarkEdited();
        Busy = false;
    }

    /// <summary>
    /// ACTORS FIRST, COMPONENTS FOLDED AWAY.
    ///
    /// Listing raw exports meant fifty rows called `StaticMeshComponent0` and four called `Root`,
    /// which is the package's own vocabulary and useless as a label -- and worse, it is not even
    /// the thing you would move: a component's position belongs to the actor that owns it.
    ///
    /// So anything an actor owns, or that hangs off something else, goes into "Script +
    /// components" and is off by default. What is left is actors and the free-standing components
    /// that really are placed on their own, each labelled by its own name and the mesh it wears.
    /// </summary>
    private static List<ActorRow> Rows(UObject[] exports) => AssetLoader.Read(() =>
    {
        var owned = LevelReader.OwnedComponents(exports);
        var rows = new List<ActorRow>(exports.Length);
        foreach (var e in exports)
        {
            var cls = e.ExportType ?? "?";
            string layer;
            if (LevelReader.IsActor(e)) layer = LevelReader.LayerOfActor(e, exports);
            else if (owned.Contains(e)) layer = LevelReader.LayerScript;
            else layer = LevelReader.LayerOf(cls);

            rows.Add(new ActorRow(e, layer, e.Name ?? "?", cls, LevelReader.MeshOf(e, exports)));
        }
        return rows;
    });

    private void BuildActorTree()
    {
        ActorTree.Clear();
        _actorNodes.Clear();
        if (_exports.Length == 0) return;

        var on = LayerChips.Where(c => c.IsOn).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var f = ActorFilter.Trim();

        foreach (var layer in LevelReader.LayerOrder)
        {
            if (!on.Contains(layer)) continue;

            var members = _rows.Where(r => r.Layer == layer).ToList();
            // The MESH is searchable too, because it is the name a person actually knows: you
            // look for the locker, not for BitReactorStaticMeshActor_4.
            if (f.Length > 0)
                members = members.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                             r.Class.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                             r.Mesh.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (members.Count == 0) continue;

            var dot = Layers.For(layer);
            var node = new Node { Title = layer, Detail = members.Count.ToString("N0"), Dot = dot };
            var byClass = members.GroupBy(r => r.Class)
                                 .OrderByDescending(g => g.Count())
                                 .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();

            // ONE CLASS IN A LAYER READS BETTER FLAT. Cover is 35 TacticalPoints and nothing
            // else, so a "TacticalPoint (35)" row between the layer and its instances is a
            // click that tells you nothing.
            if (byClass.Count == 1) AddInstances(node, byClass[0], dot);
            else
                foreach (var c in byClass)
                {
                    var cn = new Node { Title = c.Key, Detail = c.Count().ToString("N0"), Dot = dot };
                    AddInstances(cn, c, dot);
                    node.Children.Add(cn);
                }

            node.IsExpanded = ActorTree.Count == 0 || (f.Length > 0 && members.Count <= 40);
            ActorTree.Add(node);
        }
    }

    private void AddInstances(Node parent, IGrouping<string, ActorRow> group, IBrush dot)
    {
        var shown = 0;
        foreach (var r in group.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (shown++ >= MaxInstancesPerClass)
            {
                parent.Children.Add(new Node { Title = $"… {group.Count() - MaxInstancesPerClass:N0} more", Detail = "" });
                break;
            }
            var node = new Node { Title = r.Name, Class = r.Class, Mesh = r.Mesh, Dot = dot, Payload = r.Export };
            _actorNodes[node.Title] = node;
            parent.Children.Add(node);
        }
    }

    // ================================================================== properties

    /// <summary>
    /// Move the selected actor by a world delta, as a gizmo drag does.
    ///
    /// Goes through the SAME property setters the typed fields use, so a drag and a typed number
    /// produce the same pending move, the same "was" check and the same entry in the dock. There
    /// is one path into a change, and this is not a second one.
    /// </summary>
    public void Nudge(double dx, double dy, double dz)
    {
        // A NEW ACTOR IS DRAGGED LIKE ANY OTHER, and this is the only route it has: a spawn is a
        // position in a project file rather than eight bytes in a package, so there is no
        // selected export and no property field for the ordinary path to write through. Same
        // gizmo, same batch, same undo step -- only the record on the far end differs.
        if (PickedSpawn is { } spawn && !HasActor) { NudgeSpawn(spawn, dx, dy, dz); return; }
        if (!HasTransform) return;
        LocX += (decimal) dx;
        LocY += (decimal) dy;
        LocZ += (decimal) dz;
    }

    /// <summary>Turn the selected actor, in degrees. Same one path into a change as Nudge.</summary>
    public void Spin(double dPitch, double dYaw, double dRoll)
    {
        // A SPAWN HAS NO ROTATION OF ITS OWN. It inherits its donor's, because the build copies
        // the donor's component bytes and overwrites only the three location doubles. Turning the
        // ring would move a number nothing writes, which is worse than a control that refuses.
        if (PickedSpawn is not null && !HasActor)
        { Status = "a new actor keeps its donor's rotation - only its position is written"; return; }
        if (!HasTransform) return;
        if (dPitch != 0) RotP += (decimal) dPitch;
        if (dYaw != 0) RotY += (decimal) dYaw;
        if (dRoll != 0) RotR += (decimal) dRoll;
    }

    /// <summary>
    /// Resize the selected actor. Clamped away from zero: a scale of 0 is a mesh that vanishes
    /// and cannot be dragged back, because every later drag multiplies nothing by something.
    /// </summary>
    public void Resize(double dx, double dy, double dz)
    {
        if (PickedSpawn is not null && !HasActor)
        { Status = "a new actor keeps its donor's scale - only its position is written"; return; }
        if (!HasTransform) return;
        if (dx != 0) SclX = Math.Max(0.01m, SclX + (decimal) dx);
        if (dy != 0) SclY = Math.Max(0.01m, SclY + (decimal) dy);
        if (dz != 0) SclZ = Math.Max(0.01m, SclZ + (decimal) dz);
    }

    public void ClearActor()
    {
        Props.Clear();
        Explain(null);
        HasActor = false;
        HasTransform = false;
        ActorName = "";
        ActorSub = "";
        TransformSource = "";
    }

    public void SelectActor(UObject export) =>
        SelectActor(export, _exports, _sublevel?.Package ?? "", _sublevel?.Name ?? "");

    /// <summary>
    /// Select an actor, saying WHICH SUBLEVEL it belongs to.
    ///
    /// The tree always means the open sublevel; the viewport can mean any of the eleven a map
    /// merged. Carrying the scope and the package with the selection is what stops a picked
    /// actor's move being filed against the wrong package.
    /// </summary>
    public void SelectActor(UObject export, UObject[] scope, string package, string sublevel)
    {
        ActorName = export.Name ?? "?";
        ActorSub = $"{export.ExportType}   ·   {sublevel}";
        HasActor = true;
        _selectedPackage = package;
        _selectedSublevel = sublevel;
        _selectedAddress = AddressOf(export, scope);
        LoadDraws(export, scope, package);

        Props.Clear();
        foreach (var r in LevelReader.Properties(export, scope)) Props.Add(r);

        // The actor's FIRST placement, which is all a click on a name can mean. A click in the
        // viewport knows exactly which one and overwrites this straight after.
        var placed = PlacementIndexOf(export);
        Explain(placed >= 0 ? Placements[placed] : null);

        var t = LevelReader.Transform(export, scope);
        TransformSource = t.Source;
        HasTransform = t.Found;
        _prefix = t.Prefix;

        // THE PRISTINE VALUES, kept so an edit knows what it changed FROM. The build checks that
        // number before it writes, so it has to be the package's own and not whatever happens to
        // be in the box.
        _original.Clear();
        if (!t.Found) return;

        _original["RelativeLocation.X"] = t.Location.X;
        _original["RelativeLocation.Y"] = t.Location.Y;
        _original["RelativeLocation.Z"] = t.Location.Z;
        _original["RelativeRotation.Pitch"] = t.Rotation.Pitch;
        _original["RelativeRotation.Yaw"] = t.Rotation.Yaw;
        _original["RelativeRotation.Roll"] = t.Rotation.Roll;
        _original["RelativeScale3D.X"] = t.Scale.X;
        _original["RelativeScale3D.Y"] = t.Scale.Y;
        _original["RelativeScale3D.Z"] = t.Scale.Z;

        ApplyPendingToFields();
    }

    /// <summary>
    /// Put the selected actor's fields back to ORIGINAL, then lay any pending move over the top.
    ///
    /// One description of what the boxes should say, used both when an actor is selected and
    /// after an undo -- so the two cannot drift apart, which is exactly how an undo ends up
    /// showing a number that no longer matches the change list.
    /// </summary>
    private void ApplyPendingToFields()
    {
        if (!HasTransform || _original.Count == 0) return;

        _loading = true;
        LocX = (decimal) _original["RelativeLocation.X"];
        LocY = (decimal) _original["RelativeLocation.Y"];
        LocZ = (decimal) _original["RelativeLocation.Z"];
        RotP = (decimal) _original["RelativeRotation.Pitch"];
        RotY = (decimal) _original["RelativeRotation.Yaw"];
        RotR = (decimal) _original["RelativeRotation.Roll"];
        SclX = (decimal) _original["RelativeScale3D.X"];
        SclY = (decimal) _original["RelativeScale3D.Y"];
        SclZ = (decimal) _original["RelativeScale3D.Z"];

        foreach (var c in Changes.Where(c => c.Address.Equals(_selectedAddress, StringComparison.OrdinalIgnoreCase) &&
                                             c.Target.Equals(_selectedPackage, StringComparison.OrdinalIgnoreCase)))
            // SCALE FIRST. Location and scale share the component names X/Y/Z, so the guarded
            // cases have to be tested before the bare ones or they can never match.
            switch (c.Field)
            {
                case "X" when c.Path.Contains("Scale", StringComparison.Ordinal): SclX = (decimal) c.Now; break;
                case "Y" when c.Path.Contains("Scale", StringComparison.Ordinal): SclY = (decimal) c.Now; break;
                case "Z" when c.Path.Contains("Scale", StringComparison.Ordinal): SclZ = (decimal) c.Now; break;
                case "X": LocX = (decimal) c.Now; break;
                case "Y": LocY = (decimal) c.Now; break;
                case "Z": LocZ = (decimal) c.Now; break;
                case "Pitch": RotP = (decimal) c.Now; break;
                case "Yaw": RotY = (decimal) c.Now; break;
                case "Roll": RotR = (decimal) c.Now; break;
            }
        _loading = false;
    }
}

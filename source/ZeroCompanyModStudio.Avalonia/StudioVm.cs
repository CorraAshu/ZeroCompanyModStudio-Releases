using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse_Conversion.Meshes;
using UEToolkit.App.Rendering;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// One row of the asset list under the tree. Name and Type, as the Data Explorer has it.
///
/// ONE ROW TYPE FOR ALL THREE THINGS THE LIST CAN BE SHOWING — a folder's contents, the matches
/// for what was typed in Filter, or this mod's own changes — so the list has one template and one
/// selection, and switching between them cannot leave the panel bound to the wrong shape.
/// </summary>
public sealed class AssetRow
{
    public required string Name { get; init; }
    public required string Type { get; init; }

    /// <summary>Where it lives. A game asset's package path, or where a mod item came from.</summary>
    public string Path { get; init; } = "";

    /// <summary>This mod touches it. Modified rows are drawn in the accent colour.</summary>
    public bool Modified { get; init; }

    /// <summary>What opening it would show.</summary>
    public string Opens { get; init; } = "";

    /// <summary>The game asset, or the project object, behind the row.</summary>
    public object? Payload { get; init; }
}

/// <summary>
/// THE STUDIO'S STATE, as a view model rather than as fields on a window.
///
/// The left side is ONE Data Explorer, at the author's direction: the game as a folder tree with
/// the selected folder's contents listed underneath, a Filter box over both, and a Show only
/// modified box that turns the list into what THIS MOD changes. That box is why there is no
/// separate Mod Content panel any more, and it is the better shape: a mod's changes and the
/// assets they were made from are then read in one place instead of two.
/// </summary>
public sealed partial class StudioVm : Obs
{
    private ModProject? _mod;
    private string _modPath = "";
    private bool _panelOpen = true;
    private string _gameStatus = "game content not loaded yet";
    private GameNode? _folder;
    private AssetRow? _selected;
    private string _gameFilter = "";
    private bool _onlyModified;
    private bool _flat;
    private bool _descending;
    private string _listCount = "";
    private AssetIndex? _index;

    /// <summary>The game's index, for diagnostics that need to look across the whole of it.</summary>
    public AssetIndex? GameIndex => _index;
    private CUE4Parse.FileProvider.IFileProvider? _provider;

    /// <summary>The mounted game, for the few things that read it directly. Null until it mounts.</summary>
    public CUE4Parse.FileProvider.IFileProvider? Provider => _provider;

    /// <summary>
    /// The game's sounds, indexed once. Its own read: the paks' Wwise media plus the manifests.
    /// </summary>
    private readonly UEToolkit.Core.SoundLibrary _sounds = new();

    /// <summary>
    /// ONE PLAYER FOR THE APPLICATION. Two would play over each other, and Stop would have to
    /// find whichever of them was making the noise.
    /// </summary>
    private readonly UEToolkit.Core.AudioPlayer _audio = new();

    /// <summary>
    /// Where decoded previews are kept. THE SAME FOLDER THE ImGui STUDIO USES, so a sound
    /// decoded in either application is instant in the other rather than decoded twice.
    /// </summary>
    private static string SoundCache =>
        Path.Combine(StudioSettings.DataDir, "sound-preview");

    private UEToolkit.Core.AppSettings? _exportSettings;

    /// <summary>
    /// How the author wants assets WRITTEN: formats, and where exports go.
    ///
    /// SHARED WITH THE TOOLKIT AND THE CLI on purpose. It lives beside profiles/ rather than
    /// inside one, so a mesh format chosen in either place is the format both use; re-picking it
    /// per application is how two exports end up in different formats with nobody deciding that.
    /// Loaded once and kept, because reading it per right click is a file read per right click.
    /// </summary>
    public UEToolkit.Core.AppSettings ExportSettings
        => _exportSettings ??= UEToolkit.Core.AppSettings.Load(Say);
    private StudioTab? _tab;

    /// <summary>
    /// The parts catalogue, shared with whatever needs it.
    ///
    /// ONE INSTANCE, built once. It is a package read per part, which is the most expensive thing
    /// the Studio does on a background thread, and every screen that wants a part wants the same
    /// answer. EnsureBuilt is idempotent, so asking twice is free.
    /// </summary>
    private readonly PartsCatalog _catalog = new();
    private GameProfile? _profile;
    private readonly HashSet<string> _modified = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<LogLine> Log { get; } = new();

    /// <summary>
    /// THE TREE, whichever tree is showing.
    ///
    /// One bound collection refilled from either source rather than two TreeViews swapped by
    /// visibility, because two trees means two selections, and the panel underneath would then
    /// be reading from whichever one it happened to be bound to last.
    /// </summary>
    public ObservableCollection<GameNode> Tree { get; } = new();

    /// <summary>The game's top rows, and the modified tree's. Built once each, kept.</summary>
    private readonly List<GameNode> _gameTop = new();
    private readonly List<GameNode> _modTop = new();

    /// <summary>The prepared row for each leaf of the modified tree, by its node.</summary>
    private readonly Dictionary<AssetNode, AssetRow> _modRows = new();

    /// <summary>Kept so the Studio can say what it is showing.</summary>
    public ObservableCollection<GameNode> GameContent => Tree;

    /// <summary>
    /// What the list under the tree is showing: a folder's assets, the filter's matches, or this
    /// mod's own changes. Capped, because a single letter matches most of 179,966 files and
    /// nobody reads the eighty-thousandth.
    /// </summary>
    public ObservableCollection<AssetRow> Assets { get; } = new();

    public bool HasMod => _mod is not null;
    public ModProject? Mod => _mod;
    public string ModPath => _modPath;
    public string ModTitle => _mod is null ? "No mod open"
        : string.IsNullOrWhiteSpace(_mod.DisplayName) ? _mod.Name : _mod.DisplayName;

    /// <summary>
    /// Whether the left column is showing. The whole area collapses to the left, at his request,
    /// so the editing side can have the window when it needs it.
    /// </summary>
    public bool PanelOpen { get => _panelOpen; set { if (Set(ref _panelOpen, value)) Raise(nameof(PanelToggleText)); } }
    public string PanelToggleText => _panelOpen ? "«" : "»";

    public string GameStatus { get => _gameStatus; set => Set(ref _gameStatus, value); }

    // ------------------------------------------------------------------ the explorer

    /// <summary>The folder picked in the tree. Picking one re-fills the list underneath.</summary>
    public GameNode? Folder
    {
        get => _folder;
        set { if (Set(ref _folder, value)) { Raise(nameof(Where)); RebuildList(); } }
    }

    /// <summary>
    /// The filter text. It narrows the TREE, and the list still waits to be told which folder.
    ///
    /// His correction, and he was right: *"whenever i search something atm like say Anakin ... it
    /// makes everything appear in the name/type window, it shouldn't do that unless i click a
    /// folder and also filter the folders and only show me everything that i searched"*. Dumping
    /// every match into the panel throws away the one thing the explorer is for — knowing WHERE
    /// something is. Pruned folders answer that; a flat list of 400 rows does not.
    ///
    /// Understands <c>Type: DataAsset</c> as well as a name, and the two combine.
    /// </summary>
    public string GameFilter
    {
        get => _gameFilter;
        set
        {
            if (!Set(ref _gameFilter, value)) return;
            Raise(nameof(Filtering));
            Raise(nameof(Where));
            Refilter();
        }
    }

    public bool Filtering => _gameFilter.Trim().Length > 0;

    /// <summary>
    /// Split the box into a TYPE and a NAME.
    ///
    /// "Type: DataAsset" and "type:DataAsset" both work, with or without the space, and anything
    /// else in the box is the name — so "Type: Texture2D anakin" is both at once. A bare "type:"
    /// with nothing after it filters nothing, because narrowing to no type while somebody is
    /// still typing the word empties the tree in front of them.
    /// </summary>
    private static (string Name, string Type) Parse(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = new List<string>();
        var type = "";

        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (!w.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) { name.Add(w); continue; }

            var rest = w[5..];
            // "type:" on its own takes the next word; "type:X" carries its own.
            if (rest.Length == 0 && i + 1 < words.Length) { type = words[++i]; continue; }
            type = rest;
        }

        return (string.Join(' ', name), type);
    }

    /// <summary>
    /// Work out which folders are worth drawing, ONCE, and hand the answer to the tree.
    ///
    /// One walk of the index per change to the box, rather than each folder asking the same
    /// question about its own subtree — which for a tree this shape is the same walk again at
    /// every level. Ancestors are added as the walk unwinds, so a folder is kept exactly when
    /// something beneath it matched.
    /// </summary>
    private void Refilter()
    {
        var (name, type) = Parse(_gameFilter.Trim());

        if (_index is null || (name.Length == 0 && type.Length == 0))
        {
            _filter = null;
            BuildGameTop();
            ShowTree();
            RebuildList();
            return;
        }

        bool Matches(AssetNode f)
            => (name.Length == 0 || f.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
               (type.Length == 0 || GameNode.TypeName(f.Kind)
                                            .Contains(type, StringComparison.OrdinalIgnoreCase));

        var keep = new HashSet<AssetNode>();
        var hits = 0;
        Walk(Container(_index.Root));

        bool Walk(AssetNode folder)
        {
            var any = false;
            foreach (var c in folder.Children)
            {
                if (c.IsFolder) { if (Walk(c)) { keep.Add(c); any = true; } }
                else if (Matches(c)) { hits++; any = true; }
            }
            return any;
        }

        _filter = new NodeFilter { Folders = keep, FileMatches = Matches };

        var said = type.Length > 0 && name.Length > 0 ? $"{type} named “{name}”"
                 : type.Length > 0 ? $"type {type}"
                 : $"“{name}”";
        var found = hits == 0 ? $"{said} — nothing matches"
                  : $"{said} — {hits:N0} in {keep.Count:N0} folder(s)";

        BuildGameTop();
        // OPENED, so the pruned tree shows where things are rather than one shut row. Capped,
        // because a loose filter can keep thousands of folders and expanding those is a wall.
        if (keep.Count <= ExpandCap) foreach (var n in _gameTop) OpenAll(n);
        else found += ", too many to open — narrow it";

        SayFilter(found);
        ShowTree();

        // The picked folder may have been pruned away. Its rows would still be listed under a
        // name no longer in the tree, which is the panel describing somewhere you cannot see.
        if (_folder is not null && !keep.Contains(_folder.Node))
        { _folder = null; Raise(nameof(Folder)); Raise(nameof(Where)); }

        RebuildList();

        static void OpenAll(GameNode n)
        {
            n.IsExpanded = true;
            foreach (var c in n.Folders) OpenAll(c);
        }
    }

    private const int ExpandCap = 400;
    private NodeFilter? _filter;

    /// <summary>
    /// Show only what this mod changes — this Studio's replacement for a
    /// separate Mod Content panel.
    ///
    /// It takes over BOTH panels, as the reference does: the tree becomes the folders this mod
    /// touches, opened all the way down, and the list under it their contents.
    ///
    /// THAT TREE IS BUILT FROM THE MOD'S OWN PACKAGE PATHS, not by pruning the game's tree to
    /// them. Pruning would mean matching an override's PACKAGE path against the index's FILE
    /// path, and a prefix match between two path spellings is the exact shape of check that
    /// quietly matches nothing and then reports an empty tree as a clean bill of health. Building
    /// the tree from the paths themselves cannot do that: every item the project holds puts a row
    /// on screen, or the count says how many did not.
    /// </summary>
    public bool OnlyModified
    {
        get => _onlyModified;
        set
        {
            if (!Set(ref _onlyModified, value)) return;
            // The selected folder belongs to the tree being replaced. Left alone it would keep
            // filling the list from a folder that is no longer on screen anywhere.
            _folder = null;
            Raise(nameof(Folder));
            ShowTree();
            Raise(nameof(Where));
            RebuildList();
        }
    }

    /// <summary>Put the right tree on screen for the mode.</summary>
    private void ShowTree()
    {
        Tree.Clear();
        foreach (var n in _onlyModified ? _modTop : _gameTop) Tree.Add(n);
    }

    /// <summary>
    /// List everything BENEATH the picked folder rather than only what is directly in it.
    ///
    /// The reference's own toolbar button. It earns its place on a tree of this shape: a game
    /// folder that holds nothing but sixteen subfolders otherwise lists nothing at all, and the
    /// person who picked it wanted to see what is in there.
    /// </summary>
    public bool Flat
    {
        get => _flat;
        set { if (Set(ref _flat, value)) RebuildList(); }
    }

    /// <summary>Which way the Name column sorts. The arrow in the header is this, not decoration.</summary>
    public bool Descending
    {
        get => _descending;
        private set { if (Set(ref _descending, value)) Raise(nameof(SortArrow)); }
    }

    public string SortArrow => _descending ? "▲" : "▼";
    public void ToggleSort() { Descending = !_descending; RebuildList(); }

    public string ListCount => _listCount;

    /// <summary>What the list is currently showing, said in the header above it.</summary>
    public string Where
        => _onlyModified ? (HasMod ? ModTitle + " — changes" : "no mod open")
         : _folder is null ? (Filtering ? "pick one of the folders above" : "nothing picked")
         : Filtering ? $"{_folder.Name} — matching" : _folder.Name;

    // ------------------------------------------------------------------ the sections

    /// <summary>
    /// The strip's fixed tabs: Home Page, then the nine sections.
    ///
    /// These REPLACE the row of nine buttons he disliked — *"i just don't like how those are in a
    /// row like that"* — by moving the same nine choices into the reference's own tab strip,
    /// beside the assets he has open. It is why the strip was built as a collection on the view
    /// model rather than TabItems in the XAML: it always had to hold both kinds.
    /// </summary>
    public ObservableCollection<SectionTab> SectionTabs { get; } = new(
        Sections.All.Select(s => new SectionTab { Title = s.Title, Kind = s.Kind }));

    /// <summary>What the LEFT column is showing. Home means the Data Explorer.</summary>
    public SectionKind Section { get => _section; private set => Set(ref _section, value); }
    private SectionKind _section = SectionKind.Home;

    public bool OnHome => _section == SectionKind.Home;
    public bool InSection => _section != SectionKind.Home;

    /// <summary>
    /// What the pane says while nothing is open. In a section the rows are in the list on the left
    /// and open on a double click; the Data Explorer is not what the person is looking at, and
    /// "double-click an asset in the Data Explorer" sent them to a column that was not there.
    /// </summary>
    public string EmptyPaneHint => InSection
        ? $"Nothing open.\nDouble-click a row in the {SectionTitle} list."
        : "Nothing open.\nDouble-click an asset in the Data Explorer.";

    /// <summary>
    /// Whether "Only what a mod can use" would hide anything in this section. A box that changes
    /// nothing is a box that looks broken, so it is not shown where every row is usable.
    /// </summary>
    public bool HasUnusable => _sectionAll.Any(i => !i.IsHeader && !i.Usable);

    /// <summary>
    /// Duplicate is offered where a row can be copied. The 64 weapon modifications are edited in
    /// place and never copied: whether a mod can add a 65th is not settled, so the item is greyed
    /// out rather than answering in the log alone.
    /// </summary>
    public bool DuplicateOffered => _section is not (SectionKind.Attachments or SectionKind.Abilities or
                                                     SectionKind.Tables or SectionKind.Enemies or SectionKind.Sounds);

    /// <summary>Why Duplicate is greyed out, and nothing at all where it is not: a tip on an enabled item would say the wrong thing.</summary>
    public string? DuplicateTip => DuplicateOffered ? null
        : _section == SectionKind.Attachments ? "Not offered for an attachment: the 64 are changed in place"
        : "Not offered for this list: there is nothing to copy. Open one instead.";

    /// <summary>
    /// Edit and Revert, offered where they do something. Before, every section showed both and the ones that had
    /// nothing to do answered "is not built for {Section} yet" in the log -- developer wording in a public menu.
    /// Edit is the game's own part, attachment, ability or table; Revert puts back a part or an attachment.
    /// </summary>
    public bool EditOffered => _section is SectionKind.Home or SectionKind.Clothing or SectionKind.Classes or
                                          SectionKind.Specs or SectionKind.Attachments or SectionKind.Abilities or
                                          SectionKind.Tables;

    public string? EditTip => EditOffered ? null
        : _section is SectionKind.Weapons or SectionKind.Utilities or SectionKind.Recruits or SectionKind.Missions
            ? "Not offered on the game's own: Duplicate it, then edit your copy."
            : "Not offered for this list. Open one instead.";

    public bool RevertOffered => _section is SectionKind.Home or SectionKind.Clothing or SectionKind.Classes or
                                            SectionKind.Specs or SectionKind.Attachments;

    public string? RevertTip => RevertOffered ? null
        : _section is SectionKind.Abilities or SectionKind.Tables
            ? "Nothing to put back from the list. Open one and press Reset beside a number you changed."
            : "Not offered for this list.";
    public string SectionTitle => Sections.All.FirstOrDefault(s => s.Kind == _section).Title ?? "";

    /// <summary>The rows of the section currently open in the left column.</summary>
    public ObservableCollection<SectionItem> SectionRows { get; } = new();

    private SectionItem? _sectionPick;
    public SectionItem? SectionPick
    {
        get => _sectionPick;
        set
        {
            // WHATEVER THE LIST HAS, HEADING OR NOT. This used to refuse a heading, and the
            // refusal was the bug: the ListBox went on holding the heading as its selection while
            // this kept the row picked before it, so one row was lit and every context menu
            // command acted on a different one, with nothing on screen saying so.
            //
            // Two attempts to correct the ListBox from here were MEASURED AND FAILED. Raising the
            // change inline does nothing, because the ListBox is part way through its own
            // selection change and discards a source update that arrives during one; posting it
            // does nothing either. Fighting a control over what it has selected is the wrong
            // shape of answer anyway. It has selected a heading. That is true, and the list should
            // go on showing it so the keyboard can walk past one.
            //
            // So the disagreement is removed instead of arbitrated: this stays truthful, and
            // <see cref="Chosen"/> is what every command reads.
            if (!Set(ref _sectionPick, value)) return;
            Raise(nameof(Chosen));
            if (value is { IsHeader: false }) Say($"{SectionTitle}: {value.Name}");
        }
    }

    /// <summary>
    /// The picked row when it is a ROW, and null when what is picked is a heading.
    ///
    /// EVERY COMMAND READS THIS, not <see cref="SectionPick"/>. A heading is a class name, not a
    /// weapon; Edit, Revert, Duplicate and the rest have nothing to act on when one is selected,
    /// and the honest answer to that is nothing rather than whatever happened to be picked
    /// before. Every consumer already guards with `is { }`, so null does the right thing at all
    /// seventeen of them without any of them knowing about headings.
    /// </summary>
    public SectionItem? Chosen => _sectionPick is { IsHeader: false } ? _sectionPick : null;

    /// <summary>
    /// Open what is picked in a section, as a tab.
    ///
    /// *"... you can click it and it will be put in the
    /// tabs area with a x at the end"* — so this goes into the same strip, beside the assets, and
    /// closes the same way.
    /// </summary>
    public void OpenSectionItem(SectionItem item)
    {
        if (item.IsHeader) return;

        switch (item.Payload)
        {
            // A GROUP OF ENEMIES opens straight into its numbers. There is nothing to read about
            // one that is not the thing being changed — a tactical spec's own properties are
            // plumbing, and what somebody opened it for is the stats its tables hold.
            case EnemyStats.Group group:
                _ = OpenEnemyGroup(group);
                return;

            // A SOUND OPENS AS A TAB, because it has a transport and a transport needs somewhere
            // to live. Already open means bring it forward, same as every other kind.
            case UEToolkit.Core.SoundLibrary.Entry sound:
            {
                if (Tabs.OfType<SoundTab>().FirstOrDefault(
                        t => t.Entry.MediaId == sound.MediaId &&
                             string.Equals(t.Entry.Name, sound.Name, StringComparison.Ordinal))
                    is { } open)
                { Tab = open; return; }

                var soundTab = new SoundTab(sound);
                Tabs.Add(soundTab);
                Tab = soundTab;
                Raise(nameof(AnyTabs));
                Say($"opened {soundTab.Title}");
                return;
            }

            case PartsCatalog.Entry part:
            {
                // ALREADY OPEN? Bring it forward. Two tabs on one part is two places an edit
                // could later disagree, which is the same reason an asset cannot open twice.
                // A TAB EDITING A COPY IS NOT THIS PART'S TAB, even though it reads this part.
                // Without that, opening the game's helmet found the tab already editing a copy
                // of it and handed it back — so the game part could not be opened at all once a
                // copy of it was open, and the copy's editor looked like the part's.
                if (Tabs.OfType<PartTab>()
                        .FirstOrDefault(t => !t.IsCopy && t.Entry.Part.Path == part.Part.Path)
                    is { } already)
                { Tab = already; return; }

                var tab = new PartTab(part);
                Tabs.Add(tab);
                Tab = tab;
                Raise(nameof(AnyTabs));
                Say($"opened {tab.Title}");
                return;
            }

            case WeaponIndex.Weapon w:
            {
                if (Tabs.OfType<WeaponTab>()
                        .FirstOrDefault(t => t.Weapon.Key == w.Key) is { } open)
                { Tab = open; return; }

                var tab = new WeaponTab(w);
                Tabs.Add(tab);
                Tab = tab;
                Raise(nameof(AnyTabs));
                Say($"opened {tab.Title}");
                return;
            }

            case UtilityIndex.Utility u:
            {
                if (Tabs.OfType<UtilityTab>()
                        .FirstOrDefault(t => t.Utility.Key == u.Key) is { } open)
                { Tab = open; return; }

                var tab = new UtilityTab(u);
                Tabs.Add(tab);
                Tab = tab;
                Raise(nameof(AnyTabs));
                Say($"opened {tab.Title}");
                return;
            }

            case TableIndex.Entry t2:
            {
                if (Tabs.OfType<TableTab>()
                        .FirstOrDefault(t => t.Entry.Package == t2.Package) is { } open)
                { Tab = open; return; }

                var tab = NewTableTab(t2);
                Tabs.Add(tab);
                Tab = tab;
                Raise(nameof(AnyTabs));

                // The ROWS are a package read, so they arrive after the tab does. The sheet says
                // it is still reading rather than showing an empty table that looks like an answer.
                if (_provider is { } p2) _ = LoadTableAsync(tab, p2);
                Say($"opened {tab.Title}");
                return;
            }

            // A WEAPON MODIFICATION opens to its sheet: what it does, what it costs, and the four
            // tiers of its style. The numbers are read already (Modifications.All), so there is no
            // package load to wait for.
            case Modifications.Entry mod:
            {
                if (Tabs.OfType<ModificationTab>()
                        .FirstOrDefault(t => t.Entry.Asset == mod.Asset) is { } open)
                { Tab = open; return; }
                var modTab = new ModificationTab(mod, ModificationList());
                Show(modTab);
                NoteModification(modTab);
                return;
            }

            case MissionIndex.Mission m:
            {
                if (Tabs.OfType<MissionTab>()
                        .FirstOrDefault(t => t.Mission.Package == m.Package) is { } open)
                { Tab = open; return; }
                Show(new MissionTab(m));
                return;
            }

            case CharacterIndex.Recruit r:
            {
                if (Tabs.OfType<RecruitTab>()
                        .FirstOrDefault(t => t.Recruit.Pool == r.Pool &&
                                             t.Recruit.Row == r.Row) is { } open)
                { Tab = open; return; }
                Show(new RecruitTab(r));
                RemarkRecruitTabs();
                return;
            }

            case AbilityIndex.Entry a:
            {
                if (Tabs.OfType<AbilityTab>()
                        .FirstOrDefault(t => t.Ability.Package == a.Package) is { } open)
                { Tab = open; return; }
                Show(new AbilityTab(a));
                return;
            }

            default:
                // NAMED, not silent. A row that does nothing when clicked is indistinguishable
                // from one that is broken.
                Say($"{SectionTitle}: {item.Name} has nothing to open here");
                return;
        }

        void Show(StudioTab tab)
        {
            Tabs.Add(tab);
            Tab = tab;
            Raise(nameof(AnyTabs));
            Say($"opened {tab.Title}");
        }
    }

    private bool _sectionLoading;
    public bool SectionLoading { get => _sectionLoading; private set => Set(ref _sectionLoading, value); }

    /// <summary>
    /// Switch the left column to a section, or back to the Data Explorer.
    ///
    /// *"it would turn the data explorer into that section you select ... and if we hit Home page
    /// it'd take us back to what we see now, which is the normal data explorer."*
    /// </summary>
    public async Task ShowSectionAsync(SectionKind kind)
    {
        Section = kind;
        foreach (var t in SectionTabs) t.IsActive = t.Kind == kind;
        Raise(nameof(OnHome));
        Raise(nameof(InSection));
        Raise(nameof(SectionTitle));
        Raise(nameof(EmptyPaneHint));
        Raise(nameof(DuplicateOffered));
        Raise(nameof(DuplicateTip));
        Raise(nameof(EditOffered));
        Raise(nameof(EditTip));
        Raise(nameof(RevertOffered));
        Raise(nameof(RevertTip));

        if (kind == SectionKind.Home) { SectionRows.Clear(); return; }
        if (_provider is null) { Say("the game is not open yet"); return; }

        SectionRows.Clear();
        SectionPick = null;
        SectionLoading = true;

        // OFF THE UI THREAD. Every one of these indexes walks the file index, and some of them
        // load packages on the way — which is the same unsafe cache the property panel gates.
        var provider = _provider;
        var catalog = _catalog;
        var sounds = _sounds;
        _sectionAll = await Task.Run(() =>
        {
            // WAIT FOR THE CATALOGUE rather than showing an empty Clothing tab. It is started at
            // mount and is usually done; when it is not, an empty list here would read as "the
            // game has no clothing" instead of "it is still being read".
            if (Sections.NeedsCatalog(kind))
                while (catalog.Building) Thread.Sleep(50);
            // SCANNED ON FIRST ASK, not at mount: it is a read of every pak's Wwise media and
            // nobody who never opens this section should pay for it.
            if (Sections.NeedsSounds(kind) && !sounds.Ready) sounds.Build(provider, Say);
            return Sections.Load(kind, provider, catalog, sounds);
        });
        SectionLoading = false;

        ShowSectionRows();
        Say($"{SectionTitle}: {_sectionAll.Count:N0} read");
    }

    /// <summary>Everything the section holds, before the two filters over it.</summary>
    private List<SectionItem> _sectionAll = new();

    private string _sectionFilter = "";
    private bool _onlyUsable = true;

    /// <summary>Narrow the section by name or group. Its own box, over its own list.</summary>
    public string SectionFilter
    {
        get => _sectionFilter;
        set { if (Set(ref _sectionFilter, value)) ShowSectionRows(); }
    }

    /// <summary>
    /// Only what a mod can start from. ON by default, because that is the list he works in.
    ///
    /// 102 weapons ship and 79 of them carry a note saying why they are not a starting point —
    /// mostly enemy variants sharing another weapon's chain. Showing all of them first makes the
    /// section look four times bigger than the part of it anybody uses.
    /// </summary>
    public bool OnlyUsable
    {
        get => _onlyUsable;
        set { if (Set(ref _onlyUsable, value)) ShowSectionRows(); }
    }

    /// <summary>
    /// Build what is on screen: the filtered items, with a heading in front of each run.
    ///
    /// THE HEADINGS ARE MADE HERE rather than by the loader, because a filter can empty a group
    /// entirely — and a heading left standing over nothing says the section has a Repeater class
    /// with no repeaters in it.
    /// </summary>
    private void ShowSectionRows()
    {
        RemarkSectionRows();
        SectionRows.Clear();
        var want = _sectionFilter.Trim();
        var group = "";
        var shown = 0;
        var open = true;

        foreach (var it in _sectionAll)
        {
            if (_onlyUsable && !it.Usable) continue;
            if (want.Length > 0 &&
                !it.Name.Contains(want, StringComparison.OrdinalIgnoreCase) &&
                !it.Group.Contains(want, StringComparison.OrdinalIgnoreCase) &&
                !it.Keywords.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;

            if (it.Group != group)
            {
                group = it.Group;
                if (group.Length > 0)
                {
                    // The heading carries the group's OWN open state, kept across rebuilds so a
                    // filter or a toggle does not silently re-open everything somebody shut.
                    open = !_shut.Contains(group);
                    SectionRows.Add(new SectionItem
                    { Name = group, IsHeader = true, IsOpen = open });
                }
            }

            shown++;
            if (open) SectionRows.Add(it);
        }

        _sectionCount = _sectionAll.Count == 0 ? ""
            : shown == _sectionAll.Count ? $"{shown:N0}"
            : $"{shown:N0} of {_sectionAll.Count:N0}";
        Raise(nameof(SectionCount));
        Raise(nameof(SectionEmpty));
        Raise(nameof(SectionHasIcons));
        Raise(nameof(HasUnusable));
    }

    /// <summary>Groups the author has collapsed. By name, so it survives a reload of the list.</summary>
    private readonly HashSet<string> _shut = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Open or shut one category.
    ///
    /// 1,178 wearable parts across a few dozen slots is a list nobody scrolls; being able to shut
    /// the slots you are not working on is what makes it readable, which is why he asked for it.
    /// </summary>
    public void ToggleGroup(SectionItem header)
    {
        if (!header.IsHeader) return;
        if (!_shut.Add(header.Name)) _shut.Remove(header.Name);
        ShowSectionRows();
    }

    /// <summary>Shut every category, or open every one.</summary>
    public void AllGroups(bool open)
    {
        _shut.Clear();
        if (!open)
            foreach (var g in _sectionAll.Select(i => i.Group).Distinct()) _shut.Add(g);
        ShowSectionRows();
    }

    /// <summary>
    /// Whether this section has pictures at all.
    ///
    /// PER SECTION, not per row. Weapons and tables have none, so reserving the frame on every
    /// row leaves a column of empty boxes; hiding it per ROW instead would leave the clothing
    /// list ragged wherever one part of twenty has no icon. A section either shows the column or
    /// does not.
    /// </summary>
    public bool SectionHasIcons => _sectionAll.Any(i => i.IconPath.Length > 0);

    private string _sectionCount = "";
    public string SectionCount => _sectionCount;
    public bool SectionEmpty => SectionRows.Count == 0;

    // ------------------------------------------------------------------ find in data

    /// <summary>
    /// WHICH PACKAGE A SECTION ROW IS, for revealing it in the Data Explorer.
    ///
    /// Several of these are chains rather than single assets, so the choice is "the one somebody
    /// means when they point at the row": a weapon is its GEAR-KIT PART, because that is what the
    /// Armoury enumerates and what every other link hangs off; a utility is its shop item for the
    /// same reason; a recruit is its definition rather than the pool table it is a row of.
    /// </summary>
    private static string? PackageOf(SectionItem item) => item.Payload switch
    {
        PartsCatalog.Entry e => e.Part.Path,
        WeaponIndex.Weapon w => First(w.GearKitPart, w.Actor, w.Loadout, w.TypePart),
        UtilityIndex.Utility u => First(u.Item, u.GearKit, u.Actor),
        TableIndex.Entry t => t.Package,
        Modifications.Entry mod => mod.Package,
        MissionIndex.Mission m => m.Package,
        CharacterIndex.Recruit r => First(r.Definition, r.Pawn, r.Pool),
        AbilityIndex.Entry a => a.Package,
        _ => null,
    };

    private static string? First(params string[] candidates)
        => candidates.FirstOrDefault(c => c.Length > 0);

    /// <summary>
    /// Whether this mod changes the thing a section row points at, or ships a copy of it.
    ///
    /// ONE PLACE, off PackageOf — which already knows what every kind of row points at, and was
    /// written for "show me where this lives". Marking rows in eight builders instead would be
    /// eight chances for a kind to be forgotten, and the one forgotten would look untouched.
    ///
    /// A DUPLICATE COUNTS TOO. The game's part is not modified by one, but the row is the place
    /// somebody looks to ask "have I done anything with this?" — and the honest answer after
    /// duplicating it is yes.
    /// </summary>
    private bool ChangedByMod(SectionItem item)
    {
        if (_mod is null || item.IsHeader) return false;

        if (PackageOf(item) is { Length: > 0 } package &&
            _mod.Overrides.Any(o => o.Target.Equals(package, StringComparison.OrdinalIgnoreCase)))
            return true;

        // A MODIFICATION IS TWO ASSETS: the ability holds what it does, the shop item what it is
        // called and costs. Either override is this mod changing it.
        if (item.Payload is Modifications.Entry { Shop: { } modShop } &&
            _mod.Overrides.Any(o => o.Target.Equals(modShop.Package, StringComparison.OrdinalIgnoreCase)))
            return true;

        return item.Payload is PartsCatalog.Entry e &&
               _mod.Parts.Any(p => p.Template.Equals(e.Part.Name, StringComparison.OrdinalIgnoreCase) ||
                                   p.Id.EndsWith("_" + e.Part.Name, StringComparison.OrdinalIgnoreCase) ||
                                   p.Id.Equals("CPD_Mod_" + e.Part.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Re-ask the question for every row on screen, after anything is written.</summary>
    private void RemarkSectionRows()
    {
        foreach (var it in _sectionAll) it.Changed = ChangedByMod(it);
    }

    /// <summary>
    /// Go back to the Data Explorer and show exactly where this thing lives.
    ///
    /// His ask, and it is the join between the two routes the Studio offers: somebody browsing
    /// the guided side finds the piece they want, and this puts them on it in the raw side
    /// without their having to know the path.
    ///
    /// ANYTHING THAT WOULD HIDE IT IS CLEARED FIRST — a filter prunes the tree and Show only
    /// modified replaces it, and revealing into a tree that cannot draw the folder would look
    /// like the command did nothing.
    /// </summary>
    public async Task FindInDataAsync(SectionItem item)
    {
        if (PackageOf(item) is not { } package)
        { Say($"{item.Name}: nothing here names a package to find"); return; }

        string file;
        try { file = ModBuild.ContainerPathFor(package); }
        catch (Exception e) { Say($"{item.Name}: {e.Message}"); return; }

        if (_index is null) { Say("the game is not open yet"); return; }

        if (Filtering) { _gameFilter = ""; Raise(nameof(GameFilter)); Raise(nameof(Filtering)); Refilter(); }
        if (OnlyModified) OnlyModified = false;
        await ShowSectionAsync(SectionKind.Home);

        await RevealAsync(file);
    }

    /// <summary>
    /// Open the tree down to a file and select it.
    ///
    /// The expansion has to happen BEFORE the selection and the selection has to happen after the
    /// tree has had a turn to build the containers: Avalonia can only select a row whose container
    /// exists. Assigning straight away selects nothing and the tree falls back to its first row,
    /// which then fills the list with the wrong folder's contents and says nothing about it.
    /// </summary>
    public async Task RevealAsync(string filePath)
    {
        var steps = filePath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (steps.Length < 2) { Say($"{filePath}: not a path this tree can walk"); return; }

        GameNode? at = null;
        IEnumerable<GameNode> level = Tree;

        // Every step but the last is a folder; the last is the file, which lives in the list.
        for (var i = 0; i < steps.Length - 1; i++)
        {
            at = level.FirstOrDefault(
                n => string.Equals(n.Name, steps[i], StringComparison.OrdinalIgnoreCase));
            if (at is null) { Say($"could not find {steps[i]} in the game tree"); return; }
            level = at.Folders;
            if (level.Any()) at.IsExpanded = true;
        }

        if (at is null) return;

        var folder = at;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => Folder = folder, Avalonia.Threading.DispatcherPriority.Background);

        // AND THE FILE ITSELF, not just the folder holding it. "Show me where it is" is answered
        // by a highlighted row, not by a folder somebody then has to search by eye.
        var leaf = steps[^1];
        var row = Assets.FirstOrDefault(
            r => string.Equals(System.IO.Path.GetFileName(r.Path), leaf,
                               StringComparison.OrdinalIgnoreCase));

        if (row is null) { Say($"{folder.Name} is open, but {leaf} is not listed in it"); return; }

        Selected = row;
        Say($"found {leaf} in {folder.FullPath}");
    }

    // ------------------------------------------------------------------ acting on a part

    // ------------------------------------------------------------------ what an override changes

    /// <summary>
    /// WHICH GAME ASSET THIS OVERRIDE REPLACES, whether the game really has it, and who else
    /// changes it.
    ///
    /// The last of those is the reason this card exists at all. The clash -- two installed mods
    /// claiming one package, both building, both verifying, and the game reading whichever it
    /// mounted last -- is invisible from inside one project, and it cost most of a day when
    /// KalVestra and Enemy_Recruits both claimed DT_InitialCharacterPool. The build says it; this
    /// says it while it can still be a different decision.
    /// </summary>
    private void FillChanges(PartTab tab, string target)
    {
        tab.SecChanges.Present = true;
        tab.TargetEdited = null;             // set last, so filling the box is not an edit
        tab.EditTarget = target;

        var leaf = SlotEdit.LeafOf(target);
        var known = target.Length == 0 || TargetIsInTheGame(target);
        var clash = known ? ClashesOn(target) : [];

        tab.ChangesSays = target.Length == 0 ? "nothing chosen yet"
                        : !known ? $"{leaf} — the game has no file there, so a build would refuse it"
                        : clash.Count > 0 ? $"{leaf} — and {clash.Count} other mod(s) change it too"
                        : leaf;

        tab.ChangesClash = clash.Count == 0 ? "" : ModConflicts.Sentence(clash);

        tab.TargetEdited = typed =>
        {
            var ov = _mod?.Overrides.FirstOrDefault(
                o => string.Equals(o.Target, target, StringComparison.OrdinalIgnoreCase));
            if (ov is null || typed.Trim().Length == 0) return;
            ov.Target = typed.Trim();
            _mod!.Save();
            Say($"this override now changes {SlotEdit.LeafOf(ov.Target)} — open that asset to " +
                "edit it here.");
            FillChanges(tab, ov.Target);
            AfterEditsChanged();
        };
    }

    /// <summary>Whether the pristine game really ships a package at this name. Cached: it opens a container.</summary>
    private bool TargetIsInTheGame(string target)
    {
        if (_targetInGame.TryGetValue(target, out var have)) return have;
        var found = false;
        try
        {
            if (_provider is { } p) found = AssetCopy.CueHeaderFor(p, target, pristine: true) is not null;
        }
        catch (Exception) { found = false; }
        _targetInGame[target] = found;
        return found;
    }
    private readonly Dictionary<string, bool> _targetInGame = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which OTHER installed mods change this package.
    ///
    /// Asked of the FOLDER, not of the mount: the Studio mounts the game without installed mods
    /// on purpose, so a scan of what is mounted can only ever answer "nobody".
    /// </summary>
    private List<ModConflicts.Holder> ClashesOn(string target)
    {
        if (_clashes.TryGetValue(target, out var have)) return have;
        var found = _provider is null || _mod is null
            ? []
            : ModConflicts.Besides(_provider, target, null, _mod.Name, _profile?.PaksDirectory);
        _clashes[target] = found;
        return found;
    }
    private readonly Dictionary<string, List<ModConflicts.Holder>> _clashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// DUPLICATE A GAME PART INTO THIS MOD — the whole thing, so the author can point it at their
    /// own mesh, textures, icon and text.
    ///
    /// ASKED THROUGH THE CONVERSION ITSELF rather than by re-testing its conditions here. The
    /// ImGui panel makes the same call for the same reason it writes down: "two copies of 'can
    /// this be a mod part?' drift, and the copy in the UI is the one that decides whether the
    /// button appears". FromGamePart refuses a slot that is not a wearable outfit slot, and
    /// refuses a missing mesh where the slot needs one, and it says which.
    /// </summary>
    public bool DuplicatePart(SectionItem item)
    {
        // A WEAPON IS NOT A PART and is not duplicated like one: it is a CHAIN — the armoury
        // part, its type, its loadout, its actor and its gear kit — and the mod gets a copy of
        // each, pointed at one another instead of at the game's. WeaponAuthor is what knows that.
        if (item.Payload is WeaponIndex.Weapon weapon) return DuplicateWeapon(weapon);

        // A UTILITY ITEM IS A CHAIN TOO — the shop entry, the gear kit and the actor — and it is
        // not a wearable part, so FromGamePart refused it and the Utilities section's Duplicate
        // did nothing at all.
        if (item.Payload is UtilityIndex.Utility utility) return DuplicateUtility(utility);

        // A MISSION IS ONE COOKED ASSET WITH THREE SUB-OBJECTS, duplicated rather than written:
        // the game ships 125 of them and every field is seeded from the one it copies, so a
        // build straight afterwards produces the donor's mission under a new name.
        if (item.Payload is MissionIndex.Mission mission)
        { _ = DuplicateMission(mission, inPlace: false); return true; }

        // A RECRUIT IS A ROW, not a part: what it names is a character and the pool that offers
        // them. Duplicating it takes that character into the mod under their own name and ADDS a
        // row, so the game's original stays in the pool beside the copy.
        if (item.Payload is CharacterIndex.Recruit recruit)
        { _ = DuplicateRecruit(recruit); return true; }

        // NOT OFFERED, and said plainly. This edits the game's 64 in place: nothing here uses a
        // modification up, and a duplicate would be a 65th, which is a separate question that has
        // not been settled in the game (so it is not built, rather than built and guessed at).
        if (item.Payload is Modifications.Entry)
        {
            Say("Duplicate is not offered for an attachment: the 64 are changed in place, and " +
                "whether a mod can add a 65th is not settled yet");
            return false;
        }

        if (item.Payload is not PartsCatalog.Entry e)
        {
            Say($"Duplicate is not offered for {SectionTitle}: there is nothing to copy, so open one instead");
            return false;
        }
        if (_mod is null) { Say("open a mod first — a duplicate has to go somewhere"); return false; }

        // A SPECIALIZATION IS ASKED ABOUT FIRST, and this order is the whole of the fix.
        //
        // FromGamePart does not REFUSE one: a spec's slot is a slot that needs no mesh, so the
        // conversion succeeds and produces a part — which builds, installs, and has no focus
        // tree, because a part has nowhere to put one. The duplicate came out as
        // CPD_Mod_CPD_TacticalSpec_Assault and the tree was simply gone.
        //
        // So "can this be a part?" is the wrong question to ask first about something that is
        // already a specialization. SpecAuthor is what builds one, and ModSpec is what it reads.
        if (Sections.LooksLikeSpec(e)) return DuplicateSpec(e);

        var made = ModPart.FromGamePart(e.Part.Name, e.Label, e.Part.Slot, e.Detail.MeshPath,
                                        e.Detail.IconPath, e.Detail.AnimClassPath,
                                        e.Detail.HidesHead, out var why);
        if (made is null)
        {
            Say($"{e.Label} cannot be made into a mod part: {why}");
            return false;
        }

        // TWO PARTS FROM ONE BASE OVERWROTE EACH OTHER once, because copies were named from the
        // source. The id is the source's, deliberately, so a second duplicate of the same part is
        // a collision worth catching HERE rather than at cook time.
        // ALREADY DUPLICATED? OPEN THAT ONE.
        //
        // It used to say "already has X" and stop, which is true and useless: somebody pressing
        // Duplicate on a part they have already copied wants the copy, and refusing left them to
        // find it themselves with no hint of where. The collision guard still matters — two
        // copies from one base overwrote each other once, because copies were named from the
        // source — so this does not make a second one, it takes them to the first.
        var already = _mod.Parts.FirstOrDefault(
            x => string.Equals(x.Id, made.Id, StringComparison.OrdinalIgnoreCase));

        if (already is not null)
        {
            made = already;
            Say($"{ModTitle} already has a copy of {e.Label} — opening it");
        }
        else
        {
            _mod.Parts.Add(made);
            _mod.Save();
            Say($"duplicated {e.Label} into {ModTitle} as {made.Id}");
        }

        AfterEditsChanged();

        // AND OPEN THE SAME EDITOR ON IT — "this should all translate to duplication as well".
        //
        // A duplicate used to be made and then left: the panel that can change its name, its
        // mesh, its picture, its textures and its colours existed and the one act most in need
        // of it did not reach it. It is the same panel; only the place a change lands differs.
        OpenCopyEditor(e, made);
        return true;
    }

    /// <summary>
    /// Drop whatever this mod does to a part: its override of it, and its duplicate of it.
    ///
    /// BOTH, because "revert" on a row means "put this back how the game has it", and a person
    /// who duplicated a helmet and then overrode it would otherwise have to guess which of the
    /// two the menu meant.
    /// </summary>
    public bool RevertPart(SectionItem item)
    {
        // A MODIFICATION'S TWO OVERRIDES, the ability and the shop item, come off together.
        if (item.Payload is Modifications.Entry modification) return RevertModification(modification);

        if (item.Payload is not PartsCatalog.Entry e)
        {
            Say($"Revert is not offered for {SectionTitle}: open one and press Reset beside a number you changed");
            return false;
        }
        if (_mod is null) return false;

        var gone = new List<string>();

        var copyId = "CPD_Mod_" + e.Part.Name;
        if (_mod.Parts.RemoveAll(
                x => string.Equals(x.Id, copyId, StringComparison.OrdinalIgnoreCase)) > 0)
            gone.Add("the duplicate");

        if (_mod.Overrides.FirstOrDefault(
                o => string.Equals(o.Target, e.Part.Path, StringComparison.OrdinalIgnoreCase)) is { } ov)
        {
            // THE ICON IS A SECOND OVERRIDE, on the texture the part points at, so reverting the
            // part has to take it too — otherwise the name and the mesh go back and the picture
            // stays changed, which is the confusing half of a half-revert. NAMED in the message
            // rather than removed quietly, because it is a different package from the one the
            // author right-clicked.
            if (ov.Icon.Length > 0 && IconTextureOf(e.Part.Path) is { } iconPkg &&
                _mod.Overrides.FirstOrDefault(
                    o => string.Equals(o.Target, iconPkg, StringComparison.OrdinalIgnoreCase)) is { } iconOv)
            {
                _mod.Overrides.Remove(iconOv);
                gone.Add("its icon");
            }

            _mod.Overrides.Remove(ov);
            gone.Add("the changes to the game's own copy");
        }

        if (gone.Count == 0) { Say($"{e.Label}: this mod does nothing to it"); return false; }

        _mod.Save();
        Say($"reverted {e.Label} — removed {string.Join(" and ", gone)}");
        AfterEditsChanged();
        return true;
    }

    /// <summary>
    /// Open a part for editing — the GAME'S copy, not a duplicate.
    ///
    /// Opens the tab first, so whoever pressed Edit is looking at the thing they are about to
    /// change. Nothing is written into the project until they save something: an override with
    /// nothing in it ships a byte-for-byte rewrite of a game package and conflicts with every
    /// other mod touching that part, for no change at all.
    /// </summary>
    public void EditPart(SectionItem item)
    {
        // AN ABILITY IS EDITED THROUGH ITS TIERS. It has no name to change and no mesh to
        // replace — what it has is a progression, and that is a card of its own rather than an
        // absence in a panel shaped for parts.
        if (item.Payload is AbilityIndex.Entry)
        {
            OpenSectionItem(item);
            if (Tab is AbilityTab ability) _ = OpenAbilityProgression(ability);
            return;
        }

        // A MODIFICATION IS EDITED THROUGH ITS NUMBER AND ITS SHOP LISTING: what it does, and what
        // the Black Market calls it, charges for it and rates it. Recorded in the mod, so there
        // has to be one.
        if (item.Payload is Modifications.Entry)
        {
            if (_mod is null) { Say("open a mod first — an edit has to be recorded somewhere"); return; }
            OpenSectionItem(item);
            if (Tab is ModificationTab modTab) OpenModificationEditor(modTab);
            return;
        }

        // A TABLE IS EDITED THROUGH ITS NUMBERS: the card that lists every number a mod can
        // write, with a box each. The edit is recorded in the mod, so there has to be one.
        if (item.Payload is TableIndex.Entry)
        {
            if (_mod is null) { Say("open a mod first — an edit has to be recorded somewhere"); return; }
            OpenSectionItem(item);
            if (Tab is TableTab table) OpenTableEditor(table);
            return;
        }

        if (item.Payload is not PartsCatalog.Entry e)
        {
            // SAID, not ignored. A menu item that does nothing on some rows and works on others
            // teaches people it is broken.
            Say($"Edit is not offered for {SectionTitle}: open one, or Duplicate it and edit your copy");
            return;
        }
        if (_mod is null) { Say("open a mod first — an edit has to be recorded somewhere"); return; }

        OpenSectionItem(item);
        if (Tab is not PartTab tab) return;

        // WHAT AN OVERRIDE HAS. Fewer than a copy: it cannot be placed, it swaps no abilities,
        // and it cooks to the game's own asset name rather than one of its own. Deeper IS here,
        // because a re-point is the thing only an override can do.
        tab.ShowOnly(tab.SecIdentity, tab.SecChanges, tab.SecMeshes, tab.SecIcon, tab.SecOffered,
                     tab.SecDeeper);

        // WHAT THE MOD ALREADY SAYS, where it says anything, so editing twice does not start
        // over from the game's text and quietly undo the first edit.
        var ov = _mod.Overrides.FirstOrDefault(
            o => string.Equals(o.Target, e.Part.Path, StringComparison.OrdinalIgnoreCase));

        FillChanges(tab, e.Part.Path);

        tab.BeginEdit(
            ov is { DisplayName.Length: > 0 } ? ov.DisplayName : e.Label,
            ov is { Description.Length: > 0 } ? ov.Description : e.Detail.ShortDescription ?? "",
            ov?.MeshFile ?? "", ov?.Icon ?? "");

        tab.EditAnySize = ov?.MeshAnySize ?? false;
        tab.EditRemoveTags = string.Join(Environment.NewLine, ov?.RemoveTags ?? []);
        tab.EditAddTags = string.Join(Environment.NewLine, ov?.AddTags ?? []);
        FillSwatches(tab, e.Part.Path, ov);
        FillTags(tab, ov?.RemoveTags ?? []);
        FillMeshRows(tab, e.Detail, ov);

        // HOW AN IMPORTED MESH FITS, on the override side too. This path never called it, so the
        // card could not appear for a game part however plainly one of its meshes carried an
        // import — the five fields existed on ModOverride and had no way in. AFTER the mesh rows,
        // because the piece list is read off what carries a file.
        FillFit(tab);
        // Underneath the open editor, because it is package loads — see FillTextureRowsAsync.
        _ = FillTextureRowsAsync(tab, e.Detail);
        _ = FillNumbersAsync(tab);
        FillDeepRows(tab);
        NoteMod(tab, ov);

        Say($"editing the game's own {e.Label} — changes here override it for every character wearing it");
    }

    /// <summary>
    /// Open the editor on a copy this project already holds.
    ///
    /// THE SOURCE PART IS FOUND BY ITS TEMPLATE, which every copy records — the game part it was
    /// made from. The panel reads that part for its meshes, its textures, its swatches and its
    /// tags, because those are what the copy inherits and what it can change.
    ///
    /// A copy whose template is gone is SAID rather than opened half-empty: the game may have
    /// been patched under the project, and a panel showing a part's meshes as blank would look
    /// like the copy had lost them.
    /// </summary>
    /// <summary>The copies this project ships, for anything that needs to reach one by name.</summary>
    public IReadOnlyList<ModPart> CopiesInMod => _mod?.Parts ?? [];

    public async Task EditCopy(ModPart copy)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        // WAIT FOR THE CATALOGUE, exactly as the Clothing tab does. It is started at mount and
        // is usually done, but "not read yet" and "not in the game" are different answers and
        // this told me the second when the truth was the first — "the game has no part called
        // CPD_H_Outfit_Clo001_TORS_TintB any more", about a part that is plainly there.
        if (_catalog.Building)
        {
            Say($"{copy.Id}: waiting for the parts catalogue…");
            var catalog = _catalog;
            await Task.Run(() => { while (catalog.Building) Thread.Sleep(50); });
        }

        if (copy.Template.Length == 0)
        {
            Say($"{copy.Id}: this copy does not record which game part it came from, so its " +
                "meshes and colours cannot be read — it was made before that was written down");
            return;
        }

        var e = _catalog.Entries.FirstOrDefault(
            x => string.Equals(x.Part.Name, copy.Template, StringComparison.OrdinalIgnoreCase));

        if (e is null)
        {
            Say($"{copy.Id}: the game has no part called {copy.Template} any more — " +
                "it may have been patched since this copy was made");
            return;
        }

        // ALREADY OPEN? Keyed on the COPY, not on the part it was made from — two copies of one
        // helmet are two tabs, and handing back the wrong one would edit the wrong part.
        if (Tabs.OfType<PartTab>().FirstOrDefault(
                t => t.Copy is { } c && c.Id.Equals(copy.Id, StringComparison.OrdinalIgnoreCase))
            is { } open)
        { Tab = open; return; }

        OpenCopyEditor(e, copy);
    }

    /// <summary>
    /// OPEN THE EDIT PANEL ON A COPY — one made a moment ago, or one made months ago.
    ///
    /// His report: "if i open a existing mod that was created by the old mod studio, i can't
    /// edit anything in it, i can see them but can't edit them." This was the whole of it: the
    /// panel could only ever be reached through Duplicate, so a copy already in a project had
    /// nothing to open into and the Data Explorer said "this mod's own item — there is nothing
    /// built to read yet". Every project made by the old studio is in that state.
    ///
    /// ONE ROUTE, shared with Duplicate, because "edit this copy" is the same act whenever the
    /// copy was made.
    /// </summary>
    private void OpenCopyEditor(PartsCatalog.Entry e, ModPart copy)
    {
        // ITS OWN TAB, ALWAYS. Going through OpenSectionItem would hand back the tab already
        // open on the GAME part and turn that into a copy's editor — so opening a copy would
        // silently take over the window showing the thing it was copied from.
        var tab = new PartTab(e);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        tab.EditingCopy(copy);

        // A COPY HAS NO DEEPER CHANGES and no properties of its own yet: re-points rewrite a
        // GAME package, and the numbers grid reads the game part this was copied from, which
        // would invite somebody to edit the original believing they were editing the copy.
        // Absent, not empty — the rule every other section here follows.
        tab.SecNumbers.Present = false;

        // WHAT A COPY HAS. SecWhere is in it because only a copy can be PLACED — an override
        // changes the game's own part where it already is, and cannot move it to another slot,
        // hide a different head or hand it to another character.
        //
        // The cards that decide for themselves — the textures once the materials are read, the
        // tints once the swatches are, the slot lock once the target is known — turn themselves
        // on below. Everything not named here and not one of those is absent.
        tab.ShowOnly(tab.SecIdentity, tab.SecWhere, tab.SecMeshes, tab.SecIcon, tab.SecTint,
                     tab.SecOffered, tab.SecDoes, tab.SecFiles, tab.SecDeeper);

        tab.EditSlot = copy.Slot;
        tab.EditHidesHead = copy.HidesHead;
        tab.EditForCharacter = copy.ForCharacter;
        tab.Characters = _catalog.Characters;

        // THE TEMPLATE'S OWN CLAIMS, one tick each — there is nothing to untick that the part
        // does not already do. The clone legs replace the wearer's boots, and a duplicate of them
        // inherits that: an author who wanted the legs got a part that silently deletes whatever
        // boots the player picked, and had no way to say otherwise.
        var claims = new List<SheetTab.TakeoverRow>();
        foreach (var slot in e.Detail.OverrideSlots)
            claims.Add(new SheetTab.TakeoverRow(slot, "wears its own here",
                                                !copy.TakesOver.TryGetValue(slot, out var a) || a));
        foreach (var slot in e.Detail.HiddenSlots)
            claims.Add(new SheetTab.TakeoverRow(slot, "hides the menu's",
                                                !copy.TakesOver.TryGetValue(slot, out var b) || b));
        tab.SetTakeovers(claims);

        // WHAT IT DOES, and what it cooks to.
        tab.SecDoes.Present = true;
        tab.SecFiles.Present = true;
        tab.EditAnimClass = copy.AnimClass;
        tab.EditId = copy.Id;
        tab.SetAbilities(copy.Abilities.Select(kv => new SheetTab.AbilityRow(kv.Key, kv.Value)));

        // RENAMES DO APPLY TO A COPY — only re-points do not, because those rewrite a GAME
        // package. Saying the whole card was inapplicable took the escape hatch with it: a
        // specialisation's icon is a gameplay TAG rather than a texture path, and renaming is
        // the only thing that can reach one.
        tab.SecDeeper.Present = true;
        tab.SetDeepRows(copy.RenameNames.Select(kv => new SheetTab.DeepRow(
            "renames", kv.Key, kv.Value, "everywhere in this copy")));

        FillTags(tab, copy.RemoveTags);

        // THE COPY'S OWN WORDS WHERE IT HAS THEM. ModPart.Text is what a copy says about itself
        // — the template's description until the author writes one — and opening the panel on
        // the template's every time is the same fault as the header carrying the template's name.
        tab.BeginEdit(copy.Display.Length > 0 ? copy.Display : e.Label,
                      copy.Text.TryGetValue("ShortDescription", out var said)
                          ? said : e.Detail.ShortDescription ?? "",
                      copy.MeshFile, copy.Icon);

        ShowCopyPicture(tab, copy);

        tab.EditLocksOwnSlot = copy.KeepsOwnSlotLock;
        FillSwatches(tab, e.Part.Path, null);
        foreach (var s in tab.Swatches)
            if (copy.HidesFromMenu.TryGetValue(s.Tag, out var hidden)) s.Offered = !hidden;
        tab.EditRemoveTags = string.Join(Environment.NewLine, copy.RemoveTags);

        FillMeshRows(tab, e.Detail, null);
        _ = FillTextureRowsAsync(tab, e.Detail);

        // THE TINT, which is what makes one part a colourway of another. Read from the copy AND
        // from the template, so the row can name the mask it falls back to rather than saying
        // "the template's" about a texture nobody can then go and look at.
        FillTint(tab, copy, e.Detail);

        // HOW THE IMPORT FITS, where there is one. The card turns itself off when there is not:
        // the three fields describe how to place a file, and with no file they do nothing.
        FillFit(tab);

        NoteMod(tab, null);
        Say($"editing your copy — the game's own {e.Label} is untouched");
    }

    /// <summary>
    /// EVERY BODY-TYPE MESH THIS PART CARRIES, as editable rows.
    ///
    /// All of them at once. A helmet with an Average/Feminine and an Average/Masculine mesh is
    /// one part with two rows here, where the old studio made you click into each mesh to reach
    /// its own editor.
    ///
    /// AN IMPORT IS AIMED AT THE MESH PACKAGE, not at the part — the rule a replacement follows
    /// — so each row reads its own override rather than sharing one. A re-point is the part's
    /// business and lives on the part's override, because it is the part's reference that moves.
    /// </summary>
    private void FillMeshRows(PartTab tab, PartDetails.Detail detail, ModOverride? ov)
    {
        var rows = new List<SheetTab.MeshRow>();

        // WHAT A COPY WEARS IS THE BUILD'S OWN ANSWER, asked rather than worked out again.
        //
        // His report: "it tells me that everything is unchanged the meshes and the textures".
        // A copy records nothing as an override — it names its geometry on ITSELF, per piece —
        // so reading overrides for a copy could only ever come back empty, however much the
        // author had replaced. ModBuild.PlanRegions is the function the BUILD uses to decide
        // which mesh each piece wears and which entry replaces it, so asking it means the panel
        // and the thing that ships cannot disagree.
        var plans = tab.Copy is { } forPlan ? ModBuild.PlanRegions(detail, forPlan, out _) : null;

        foreach (var m in detail.Meshes)
        {
            var pkg = m.MeshPath;
            if (pkg is not { Length: > 0 }) continue;

            // Everything before the object suffix: "/Game/A/SK_X.SK_X" names package "/Game/A/SK_X".
            var meshPkg = PackageOf(pkg);

            if (tab.Copy is { } copy)
            {
                var plan = plans!.FirstOrDefault(p => p.Fragment == m.Fragment);

                // The file is the PIECE's when the copy authors pieces and the PART's when it
                // does not — the same fork PlanRegions documents for the mesh itself.
                var file = plan?.Entry?.MeshFile ?? "";
                if (file.Length == 0 && !copy.HasRegions) file = copy.MeshFile;

                // And the game mesh it actually wears, which is a re-point when it is not this
                // row's own. A part with two body types and no pieces wears ONE of them for
                // both, and saying so is the difference between a panel and a wish.
                var wears = PackageOf(plan?.Mesh ?? "");

                // PER-HEAD OPTIONS THE BUILD LEAVES ALONE are not re-pointed, so a row for one says
                // nothing: an unedited brow or beard wears each head's own cut, and "another of the
                // game's" under all 28 of them would claim the opposite of what ships. Own copy,
                // paint or an imported file make the row the author's, as ModBuild treats them.
                var ownGeometry = file.Length > 0 || copy.OwnMesh ||
                                  (copy.HasRegions
                                      ? plan?.Entry?.HasPaint == true || plan?.Entry?.HasNamedMaterial == true
                                      : copy.HasPaint || copy.HasNamedMaterial);
                if (!ownGeometry && plan is not null &&
                    (copy.HasRegions
                        ? ModBuild.KeepsHeadCuts(detail.MeshFragments.First(g => g.Key == m.Fragment), plan.Mesh,
                              PartDetails.RegionMeshes(detail, detail.MeshPath)
                                  .FirstOrDefault(x => x.Region == plan.Key).Mesh)
                        : ModBuild.KeepsHeadCuts(detail.Meshes, copy.Mesh, detail.MeshPath)))
                    wears = "";

                rows.Add(new SheetTab.MeshRow(
                    m.Variant.Length > 0 ? m.Variant : "any", meshPkg, file,
                    wears.Length > 0 && !wears.Equals(meshPkg, StringComparison.OrdinalIgnoreCase)
                        ? wears : "")
                {
                    // ONLY WHEN THERE IS MORE THAN ONE PIECE. A one-piece garment names its mesh
                    // on the part itself, which is the shape every existing project is in and the
                    // one PlanRegions falls back to — giving it a region key would move that write
                    // somewhere else for no gain.
                    Region = detail.MeshFragments.Count() > 1 ? plan?.Key ?? "" : "",
                });
                continue;
            }

            var meshOv = _mod?.Overrides.FirstOrDefault(
                o => o.Target.Equals(meshPkg, StringComparison.OrdinalIgnoreCase));

            // A re-point of THIS mesh's reference, if the part's override carries one — by
            // either half, for the reason PickedOn explains.
            var picked = PickedOn(tab.Entry.Part.Path, meshPkg);

            rows.Add(new SheetTab.MeshRow(
                m.Variant.Length > 0 ? m.Variant : "any",
                meshPkg, meshOv?.MeshFile ?? "", picked));
        }

        tab.MeshesPerHead = PartDetails.KeyedPerHead(detail.Meshes);
        tab.SetMeshRows(rows);
    }

    /// <summary>
    /// EVERY TEXTURE THE PART'S MESHES WEAR, as editable rows — the old studio's Materials step.
    ///
    /// OFF THE UI THREAD, because it is package loads: one material chain per mesh, each of which
    /// opens the material instance and its parent. The meshes are already known by the time this
    /// runs, so the editor draws immediately and the textures arrive underneath it rather than
    /// the whole panel waiting on them.
    ///
    /// DISTINCT BY TEXTURE PACKAGE. A kit's five regions share one diffuse, and listing it five
    /// times would be five rows writing to one override — each showing whatever the last one set,
    /// which is the fault the old studio's own comment warns about.
    /// </summary>
    /// <summary>
    /// THE PAINT CARD, for anything that has meshes and an owner for their artwork.
    ///
    /// Shared because it is the same question for both: which textures do these meshes wear,
    /// which of them does this mod replace, and on which material slot. A part's meshes come
    /// from its pieces and a weapon's from its palettes, so the LIST is the caller's business
    /// and everything after it is not. The picture is the caller's too — a part names one and
    /// a weapon names one, and they are not the same field.
    /// </summary>
    private async Task FillPaintAsync(
        SheetTab tab,
        List<(string Pkg, IMeshAuthoring? Paint, string Piece)> meshes,
        bool copyMode)
    {
        if (_provider is null) return;

        var provider = _provider;
        var found = await Task.Run(() =>
        {
            var rows = new List<(string Slot, string Package, string Leaf, string Describe,
                                int Width, int Height, string Holder, string Mine,
                                string MineKey, string MinePiece, int MineSlot, string Piece)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var used = 0;

            // WHAT EACH PIECE'S MESH CALLS ITS SLOTS, and what each one wears today. Both are
            // what ModBuild.PreviewSlotNames needs to say which name a slot will END UP with —
            // the tie-break that quietly renames a second slot to MI_X_ADD and takes the tint
            // off it. Collected here because this is the only place the chains are open.
            var pieces = new List<(string Piece, int SlotCount,
                                   List<(int Slot, string Name, string Material)> Slots)>();

            foreach (var (meshPkg, paint, pieceName) in meshes)
            {
                // EVERY MATERIAL SLOT THE MESH HAS, not only the first that loads.
                //
                // A helmet in his own project paints slot TWO — the visor — and this read slot
                // one and reported the lot as unchanged. The build paints by slot index
                // (ModBuild reads the mesh again per slot to do it), so the panel has to read
                // the same way or it can only ever see one material's worth of a part.
                var chains = new List<MaterialSkin.Chain>();
                try
                {
                    if (MaterialSkin.Read(provider, meshPkg, out _) is { } first)
                    {
                        chains.Add(first);
                        for (var s = 0; s < first.SlotCount; s++)
                            if (s != first.Slot &&
                                MaterialSkin.Read(provider, meshPkg, out _, s) is { } more)
                                chains.Add(more);
                    }
                }
                catch (Exception) { /* a material that will not load is not a reason to show none */ }

                // ONE ENTRY PER PIECE, not per mesh.
                //
                // A part carries a mesh per body type — four on this torso — and they are one
                // garment wearing one set of materials. Adding an entry each drew Material 1
                // four times over, with the same four rows in every copy of it, because the rows
                // are deduped by texture package and then matched into every group that claims
                // slot zero. Merged first-wins, which is the rule the row dedupe already follows.
                var at = pieces.FindIndex(x => x.Piece.Equals(pieceName, StringComparison.OrdinalIgnoreCase));
                if (at < 0)
                    pieces.Add((pieceName,
                                chains.Count > 0 ? chains[0].SlotCount : 0,
                                chains.Select(c => (c.Slot, c.SlotName, c.MaterialPackage)).ToList()));
                else
                {
                    var have = pieces[at];
                    foreach (var c in chains)
                        if (!have.Slots.Any(s => s.Slot == c.Slot))
                            have.Slots.Add((c.Slot, c.SlotName, c.MaterialPackage));
                    pieces[at] = (have.Piece,
                                  Math.Max(have.SlotCount, chains.Count > 0 ? chains[0].SlotCount : 0),
                                  have.Slots);
                }

                foreach (var chain in chains)
                {
                    // SLOT BY SLOT, AND NEVER Material(i) — that GROWS the list to reach the
                    // index, so reading a part would quietly add empty material slots to the
                    // author's project and the next Save would write them out.
                    var dict = paint is not null && chain.Slot < paint.Materials.Count
                        ? paint.Materials[chain.Slot].Textures : null;

                    // THE COPY'S OWN KEYS, RESOLVED THE WAY THE BUILD RESOLVES THEM.
                    //
                    // A key is a material parameter, a texture's asset name OR the suffix — his
                    // project uses the suffixes (D, MSK, N) and the panel's own import writes
                    // the parameter, so `Textures[row.Slot]` finds one style and misses the
                    // other. MaterialSkin.Match is the one matcher; it also refuses an ambiguous
                    // key rather than painting the wrong channel, and a key that resolves to
                    // nothing here is one the build would not apply either.
                    var mine = new Dictionary<string, (string Art, string Key)>(
                        StringComparer.OrdinalIgnoreCase);
                    if (dict is not null)
                        foreach (var (key, value) in dict)
                            if (value is { Length: > 0 } &&
                                MaterialSkin.Match(chain, key) is { } hit)
                            { mine[hit.TexturePackage] = (value, key); used++; }

                    foreach (var skin in chain.Skins)
                    {
                        if (!seen.Add(skin.TexturePackage)) continue;
                        rows.Add((
                            skin.Parameters.Count > 0 ? skin.Parameters[0]
                            : skin.Suffix.Length > 0 ? skin.Suffix : "Texture",
                            skin.TexturePackage,
                            skin.TextureName,
                            $"{skin.Width}×{skin.Height}  " +
                            skin.Format.ToString().Replace("PF_", "") +
                            (skin.IsVirtual ? "  ·  virtual" : ""),
                            skin.Width, skin.Height,
                            // THE MATERIAL HOLDS THE REFERENCE, not the part — a part has never
                            // heard of a diffuse map. So a re-point of this texture is an
                            // override on the material, a different target from the import.
                            chain.MaterialPackage,
                            mine.TryGetValue(skin.TexturePackage, out var art) ? art.Art : "",
                            art.Key ?? "",
                            art.Art is { Length: > 0 } ? pieceName : "",
                            chain.Slot,
                            pieceName));
                    }
                }
            }

            // AND WHAT THIS COULD NOT PLACE. Counting what was shown says nothing about what was
            // missed, and "every texture reads unchanged" is precisely what a panel looks like
            // when it has read the wrong half of a project — so the arithmetic is said out loud
            // rather than left to be noticed months later.
            var held = meshes.Select(m => m.Paint).Distinct()
                             .Where(p => p is not null)
                             .Sum(p => p!.Materials.Sum(m => m.Textures.Count(t => t.Value.Length > 0)));

            return (Rows: rows, Missed: Math.Max(0, held - used), Pieces: pieces);
        });

        // THE MENU PICTURE, asked for separately. It is a texture like any other — it just is not
        // one the MESH wears, so following the material chain never reaches it. Read here rather
        // than on the UI thread because it is another package open.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var made = found.Rows.Select(r => new SheetTab.TextureRow(
                r.Slot, r.Package, r.Leaf, r.Describe,
                // A COPY'S PAINT IS EITHER, and which one it is decides which half of the row
                // it fills: a value starting with a slash names one of the GAME's textures, and
                // anything else is a file in this mod's own ModSource. The same field takes both
                // — see ModPart.Textures — so the fork is here rather than in the project.
                copyMode ? (r.Mine.StartsWith('/') ? "" : r.Mine)
                : _mod?.Overrides.FirstOrDefault(
                    o => o.Target.Equals(r.Package, StringComparison.OrdinalIgnoreCase))?.Image ?? "",
                r.Width, r.Height,
                // The holder is what Pick is offered on. For the game's own part it is also
                // where the re-point is written; a copy has no reference to move and names the
                // texture on itself instead, which PickTextureRow forks on.
                r.Holder,
                // A re-point already recorded on the holder, so the row opens saying what it
                // actually points at rather than forgetting last week's choice.
                copyMode ? (r.Mine.StartsWith('/') ? r.Mine : "")
                         : PickedOn(r.Holder, r.Package),
                r.MineKey, r.Piece, r.MineSlot)).ToList();

            tab.SetTextureRows(made);
            var placedOnOwnSlots = FillMaterialGroups(tab, made, found.Pieces, meshes, copyMode);

            if (found.Rows.Count == 0)
                Say($"{tab.Title}: could not read what its meshes are painted with, so there is " +
                    "no textures section — the material chain is what names them");

            // WHAT NOTHING COULD ACCOUNT FOR. A slot the game's mesh has not got is now a card
            // of its own, so what is left here is a key that matches no texture of its material
            // or matches two of them — which the build will not apply either.
            if (found.Missed - placedOnOwnSlots > 0)
                Say($"{tab.Title}: {found.Missed - placedOnOwnSlots} texture(s) this copy names " +
                    "match no texture on their material, or match more than one — the build " +
                    "applies neither. Check the key against the row names above.");

        });
    }

    /// <summary>
    /// EVERY TEXTURE THE PART'S MESHES WEAR, and its menu picture.
    ///
    /// The list of meshes is the part's own answer — ModBuild.PlanRegions for a copy, the
    /// template's meshes otherwise — and the rest of the card is shared with the weapon
    /// editor, which asks the same question of a different list.
    /// </summary>
    private async Task FillTextureRowsAsync(PartTab tab, PartDetails.Detail detail)
    {
        if (_provider is null) return;

        var provider = _provider;

        // WHICH MESHES, AND WHOSE PAINT IS ON EACH.
        //
        // For the game's own part it is the part's meshes and no paint — an override records its
        // artwork against the texture package, and that is read on the UI thread below.
        //
        // For a COPY it is the meshes the COPY wears, piece by piece, each carrying the entry
        // that paints it. Both come from ModBuild.PlanRegions, which is the build's own answer
        // to that question: a piece the author named is painted from its entry, a piece they did
        // not is left as the game has it, and a copy authoring no pieces paints through itself.
        // Reading `_mod.Overrides` for a copy — which is what this did — could only ever come
        // back empty, which is why every texture read "unchanged".
        var meshes = new List<(string Pkg, IMeshAuthoring? Paint, string Piece)>();

        if (tab.Copy is { } forPaint)
            foreach (var plan in ModBuild.PlanRegions(detail, forPaint, out _))
            {
                var pkg = PackageOf(plan.Mesh);
                if (pkg.Length == 0 ||
                    meshes.Any(w => w.Pkg.Equals(pkg, StringComparison.OrdinalIgnoreCase))) continue;
                meshes.Add((pkg,
                            plan.Entry is { } piece ? piece
                            : forPaint.HasRegions ? null
                            : forPaint,
                            forPaint.HasRegions ? plan.Key : ""));
            }

        // The template's own, which is also the fallback for a copy that names no mesh: showing
        // the textures it inherits beats an empty card that reads as "this part has none".
        if (meshes.Count == 0)
            meshes.AddRange(detail.Meshes
                                  .Select(m => m.MeshPath)
                                  .Concat([detail.MeshPath])
                                  .Where(p => !string.IsNullOrWhiteSpace(p))
                                  .Select(p => PackageOf(p!))
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .Select(p => (p, (IMeshAuthoring?)
                                                   (tab.Copy is { HasRegions: false } c ? c : null),
                                                "")));

        await FillPaintAsync(tab, meshes, tab.Copy is not null);

        var iconPkg = await Task.Run(() => IconTextureOf(tab.Entry.Part.Path));
        var iconDescribe = iconPkg is null ? "" : await Task.Run(() => DescribeTexture(iconPkg));

        // BACK ON THE UI THREAD. A bound ObservableCollection mutated from a worker thread kills
        // the application on the spot — this one has already cost a startup crash once.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var copy = tab.Copy;

            if (copy is not null)
            {
                // A COPY NAMES ITS OWN PICTURE, and that is the whole of the row: no override,
                // no re-point, and no image-bank problem — ModPart.Icon takes a game texture's
                // path or a file of the author's, which is why a copy can have a picture even
                // when the part it was made from names its own by tag.
                var own = copy.Icon;
                var ownPkg = PackageOf(own);
                var fromGame = own.StartsWith('/');
                var shape = iconPkg is null ? (W: 0, H: 0) : DescribeShape(iconPkg);

                tab.SetIconRow(new SheetTab.TextureRow(
                    "Menu picture",
                    iconPkg ?? ownPkg,
                    iconPkg is not null ? Leaf(iconPkg) : ownPkg.Length > 0 ? Leaf(ownPkg) : "none",
                    iconPkg is not null ? iconDescribe : "",
                    fromGame ? "" : own,
                    // The part is the holder, as it is for an override — not because anything
                    // is re-pointed here, but because that is what offers Pick, and choosing
                    // one of the game's thumbnails is the commonest thing a copy does with its
                    // picture. PickPartIcon is where it lands.
                    shape.W, shape.H, tab.Entry.Part.Path,
                    fromGame && !ownPkg.Equals(iconPkg ?? "", StringComparison.OrdinalIgnoreCase)
                        ? ownPkg : ""), "");

                ShowCopyPicture(tab, copy);
            }
            else if (iconPkg is null)
                tab.SetIconRow(null,
                    "This part names its picture with an image-bank tag rather than a texture of " +
                    "its own, so there is nothing to import over. Changing it means changing what " +
                    "the bank holds, which is a different edit.");
            else
            {
                var mine = _mod?.Overrides.FirstOrDefault(
                    o => o.Target.Equals(iconPkg, StringComparison.OrdinalIgnoreCase))?.Image ?? "";

                var iconShape = DescribeShape(iconPkg);
                tab.SetIconRow(
                    new SheetTab.TextureRow("Menu picture", iconPkg, Leaf(iconPkg), iconDescribe,
                                            mine, iconShape.W, iconShape.H,
                                            // The PART names its own picture, so it is the holder.
                                            tab.Entry.Part.Path,
                                            PickedOn(tab.Entry.Part.Path, iconPkg)),
                    "");

                // Re-opening the editor shows YOUR picture, not the one you replaced.
                if (mine.Length > 0) tab.SetIconPreview(LoadPreview(mine));
            }
        });
    }

    /// <summary>
    /// One of this mod's own images as a bitmap, or null when it cannot be decoded.
    ///
    /// PNG only in practice: Avalonia reads PNG and not TGA, and a TGA falling back to the game's
    /// thumbnail is better than a blank frame — the row beside it still says what is applied.
    /// </summary>
    /// <summary>
    /// The material list a slot writes into — the piece's, the copy's, or the override's.
    ///
    /// <paramref name="create"/> is the same rule <see cref="PaintHolder"/> follows and extends
    /// it to the override: an override is invented only when something is actually being
    /// written, never to look at one.
    /// </summary>
    private List<ModMaterial>? MaterialsFor(SheetTab tab, string piece, bool create)
    {
        // WHATEVER THIS TAB PAINTS ONTO. The material surface is shared by every kind of thing
        // that wears a mesh, and typing these four methods to PartTab meant a mesh copy could
        // draw the card, fill it, and refuse every button on it.
        switch (tab)
        {
            case ReplacementTab own:
                return own.Mesh.Materials;

            case WeaponTab { Copy: { } weapon } when _mod is not null && _provider is not null:
                // BY THE PIECE'S OWN MESH ENTRY, as PaintTarget does it: the entry is keyed on
                // the game asset the piece copies, not on the slot's name.
                return _mod.Meshes.FirstOrDefault(
                    m => WeaponAuthor.PiecesOf(_provider, _mod, weapon.Id)
                        .Any(pc => pc.Slot.Equals(piece, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(m.Source, pc.Mesh, StringComparison.OrdinalIgnoreCase)))
                    ?.Materials;

            case PartTab { Copy: { } copy }:
                return PaintHolder(copy, piece, create)?.Materials;

            case PartTab part when _mod is not null:
                var ov = _mod.Overrides.FirstOrDefault(
                    o => o.Target.Equals(part.Entry.Part.Path, StringComparison.OrdinalIgnoreCase));
                if (ov is null && create)
                { ov = new ModOverride { Target = part.Entry.Part.Path }; _mod.Overrides.Add(ov); }
                return ov?.Materials;

            default:
                return null;
        }
    }

    /// <summary>
    /// WEAR ONE OF THE GAME'S MATERIALS on this slot, whole.
    ///
    /// The other half of Materials, and the cheap half: naming one costs a single package import
    /// and copies nothing. Painting the same result by hand means exporting four 4K virtual
    /// textures and importing them back — minutes of work, seventy megabytes of mod, and a worse
    /// result than the original.
    /// </summary>
    public void WearMaterial(SheetTab tab, SheetTab.MaterialGroup group, string package)
    {
        if (_mod is null) { Say("open a mod first — a change has to be recorded somewhere"); return; }

        var list = MaterialsFor(tab, group.Piece, create: true);
        if (list is null) { Say($"{group.Title}: nothing here can wear a material"); return; }

        ModMaterial.At(list, group.Slot).Material = package;
        _mod.Save();
        group.Material = package;
        BuildModifiedSet();
        _artVersion++;
        Say($"{group.Title}: wears the game's {Leaf(package)}");
        AfterEditsChanged();
    }

    /// <summary>Back to whatever the template's own mesh wears on this slot.</summary>
    public void ClearMaterial(SheetTab tab, SheetTab.MaterialGroup group)
    {
        if (_mod is null) return;

        var list = MaterialsFor(tab, group.Piece, create: false);
        if (list is null || group.Slot >= list.Count || list[group.Slot].Material.Length == 0)
        { Say($"{group.Title}: already wears the template's own"); return; }

        list[group.Slot].Material = "";
        DropEmptyOverride(tab);
        _mod.Save();
        group.Material = "";
        BuildModifiedSet();
        _artVersion++;
        Say($"{group.Title}: back to the template's own material");
        AfterEditsChanged();
    }

    /// <summary>
    /// WHAT THE MESH CALLS THIS SLOT — the one field that decides whether the tints reach it.
    /// </summary>
    public void SetMaterialSlotName(SheetTab tab, SheetTab.MaterialGroup group, string name)
    {
        if (_mod is null) return;

        var wanted = (name ?? "").Trim();
        var list = MaterialsFor(tab, group.Piece, create: wanted.Length > 0);
        if (list is null) return;
        if (wanted.Length == 0 && group.Slot >= list.Count) return;
        if (ModMaterial.At(list, group.Slot).SlotName == wanted) return;

        ModMaterial.At(list, group.Slot).SlotName = wanted;
        DropEmptyOverride(tab);
        _mod.Save();
        group.SlotName = wanted;
        Say(wanted.Length > 0
            ? $"{group.Title}: the mesh calls this slot {wanted}"
            : $"{group.Title}: the slot's name is worked out by the build again");
        AfterEditsChanged();
    }

    /// <summary>
    /// ONE MORE MATERIAL SLOT than the game's mesh has, for a model that needs it.
    ///
    /// The old studio's "Add another material". A replacement model with more material groups
    /// than the thing it stands in for gets the slots made for it by the build — what could not
    /// be said from here was what those extra slots should WEAR, so they fell back to the
    /// template's first material and the result was a model painted entirely in one piece's paint.
    /// </summary>
    public void AddMaterialSlot(SheetTab tab, string piece)
    {
        if (_mod is null) { Say("open a mod first — a slot has to belong to one"); return; }

        var list = MaterialsFor(tab, piece, create: true);
        if (list is null) { Say("only a copy or an override carries material slots"); return; }

        var at = list.Count;
        ModMaterial.At(list, at);
        _mod.Save();

        tab.AddMaterialGroup(new SheetTab.MaterialGroup(at, piece, "", "", onMesh: false, ""));
        Say($"material {at + 1}: added — name a material for it, or paint it");
        AfterEditsChanged();
    }

    /// <summary>
    /// An override left saying nothing must not ship: it is a byte-for-byte rewrite of a game
    /// package that changes nothing and conflicts with every other mod touching that part.
    /// </summary>
    private void DropEmptyOverride(SheetTab tab)
    {
        // ONLY AN OVERRIDE CAN BE LEFT EMPTY. A copy and a mesh copy have nothing of the game's
        // to rewrite, so there is no byte-for-byte rewrite to avoid shipping.
        if (_mod is null || tab is not PartTab { Copy: null } part) return;
        DropEmptyOverrideOn(part);
    }

    private void DropEmptyOverrideOn(PartTab tab)
    {
        if (_mod is null || tab.Copy is not null) return;
        if (_mod.Overrides.FirstOrDefault(
                o => o.Target.Equals(tab.Entry.Part.Path, StringComparison.OrdinalIgnoreCase))
            is { } ov && !ov.HasEdit && ov.Icon.Length == 0)
            _mod.Overrides.Remove(ov);
    }

    /// <summary>
    /// THE MATERIAL SLOTS, each holding the texture rows that belong to it.
    ///
    /// The old studio's Materials step, which this panel had only the paint half of. A slot can
    /// wear a GAME material outright — one package import, no copies, no textures — and that is
    /// what a piece borrowed from another kit wants. It can also be a slot the game's mesh has
    /// NOT got: a replacement model with more material groups than the thing it stands in for,
    /// which the build makes the slots for and which had nowhere to be named from here.
    ///
    /// The planned slot name comes from the BUILD's own planner, so the warning about a second
    /// slot being renamed away from the tint is the warning the build will actually act on.
    /// </summary>
    private int FillMaterialGroups(
        SheetTab tab,
        IReadOnlyList<SheetTab.TextureRow> rows,
        IReadOnlyList<(string Piece, int SlotCount,
                       List<(int Slot, string Name, string Material)> Slots)> pieces,
        IReadOnlyList<(string Pkg, IMeshAuthoring? Paint, string Piece)> meshes,
        bool copyMode)
    {
        var groups = new List<SheetTab.MaterialGroup>();
        var placed = 0;

        // A copy names its materials on the thing that owns each piece's artwork — which the
        // mesh list already carries, so this needs no second opinion about it. An override names
        // them on the override, and only a PART has one of those.
        var ov = !copyMode && _mod is not null && tab is PartTab pt
            ? _mod.Overrides.FirstOrDefault(
                o => o.Target.Equals(pt.Entry.Part.Path, StringComparison.OrdinalIgnoreCase))
            : null;

        foreach (var (piece, onMesh, slots) in pieces)
        {
            var held = copyMode
                ? meshes.FirstOrDefault(
                    m => m.Piece.Equals(piece, StringComparison.OrdinalIgnoreCase)).Paint?.Materials
                : ov?.Materials;

            var count = Math.Max(onMesh, held?.Count ?? 0);
            if (count == 0) continue;

            // THE NAME EACH SLOT WILL END UP WITH, asked of ModBuild rather than derived here.
            var explicitNames = new List<string>();
            var named = new List<string>();
            var templateNames = new List<string>();
            var sectionMaterials = new List<string?>();
            for (var s = 0; s < count; s++)
            {
                explicitNames.Add(held is not null && s < held.Count ? held[s].SlotName : "");
                named.Add(held is not null && s < held.Count ? held[s].Material : "");
                var onIt = slots.FirstOrDefault(x => x.Slot == s);
                templateNames.Add(onIt.Name ?? "");
                sectionMaterials.Add(onIt.Material);
            }

            List<string?> planned;
            try
            {
                planned = ModBuild.PreviewSlotNames(explicitNames, named,
                                                    templateNames.Where(n => n.Length > 0).ToList(),
                                                    sectionMaterials);
            }
            catch (Exception) { planned = [.. Enumerable.Repeat((string?)null, count)]; }

            for (var s = 0; s < count; s++)
            {
                var group = new SheetTab.MaterialGroup(
                    s, piece, named[s], explicitNames[s], s < onMesh,
                    s < planned.Count ? planned[s] ?? "" : "");

                foreach (var r in rows.Where(r => r.MineSlot == s &&
                                                  r.Piece.Equals(piece, StringComparison.OrdinalIgnoreCase)))
                    group.Rows.Add(r);

                // A SLOT THE GAME'S MESH HAS NOT GOT still shows what the author painted on it,
                // as the keys they wrote — there is no game texture to describe, which is why
                // these were reported only as "could not be placed" before.
                if (s >= onMesh && held is not null && s < held.Count)
                    foreach (var (key, file) in held[s].Textures.Where(t => t.Value.Length > 0))
                    {
                        placed++;
                        group.Rows.Add(new SheetTab.TextureRow(
                            key, "", "", "your model's own slot — the game's mesh has no material here",
                            file.StartsWith('/') ? "" : file, 0, 0, "",
                            file.StartsWith('/') ? file : "", key, piece, s));
                    }

                groups.Add(group);
            }
        }

        tab.SetMaterialGroups(groups, copyMode || ov is not null || _mod is not null);
        return placed;
    }

    /// <summary>
    /// SHOW THE COPY'S OWN PICTURE, wherever that picture comes from.
    ///
    /// His report: "it still carries the wrong icon". The header drew the thumbnail of the GAME
    /// part the copy was made from, which is right until somebody changes it — and his torso
    /// names a different one of the game's thumbnails altogether.
    ///
    /// Two shapes, because <c>ModPart.Icon</c> takes two: an object path names one of the game's
    /// own textures and is decoded through the same thumbnail cache every list here uses;
    /// anything else is a file in this mod's ModSource. A picture that cannot be decoded leaves
    /// the template's showing rather than a blank frame — the row beside it still says what is
    /// named, which is the rule LoadPreview already follows for a TGA.
    /// </summary>
    private void ShowCopyPicture(PartTab tab, ModPart copy)
    {
        if (copy.Icon.Length == 0) { tab.SetIconPreview(null); return; }

        if (copy.Icon.StartsWith('/'))
            IconLoader.Want(copy.Icon, tab.SetIconPreview, 96);
        else
            tab.SetIconPreview(LoadPreview(copy.Icon));
    }

    private Bitmap? LoadPreview(string leaf)
    {
        try
        {
            var path = ModBuild.FindImage(leaf, _modPath);
            return path is not null && File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// What a holder already re-points this reference at, or empty.
    ///
    /// Read back so the editor opens saying what the part actually points at. A row that forgot
    /// last week's choice would invite somebody to make it a second time.
    /// </summary>
    private string PickedOn(string holder, string fromPackage)
    {
        if (_mod is null || holder.Length == 0) return "";

        // EITHER HALF COUNTS. A reference moves as a package path, as an object name, or as both,
        // and which of those a given holder uses is a property of the package — the part named
        // its mesh by PATH 35 times and the material names its texture by NAME 8 times. Looking
        // only for the path made a re-pointed row read "unchanged" while the project held it.
        var leaf = Leaf(fromPackage);

        return _mod.Overrides
                   .FirstOrDefault(o => o.Target.Equals(holder, StringComparison.OrdinalIgnoreCase))
                   ?.Repoints
                   .FirstOrDefault(r => r.From.Equals(fromPackage, StringComparison.OrdinalIgnoreCase) ||
                                        r.From.Equals(leaf, StringComparison.Ordinal))
                   ?.To ?? "";
    }

    /// <summary>A texture's pixel size, for the "will be resized" check on the picture row.</summary>
    private (int W, int H) DescribeShape(string package)
    {
        if (_provider is null) return (0, 0);
        try
        {
            if (VirtualTexture.Describe(_provider, package, out _) is { } vt) return (vt.Width, vt.Height);
            if (TextureImport.Describe(_provider, package, out _) is { } plain) return (plain.Width, plain.Height);
        }
        catch (Exception) { /* nothing beats a wrong number */ }
        return (0, 0);
    }

    /// <summary>
    /// Point a texture — or the menu picture — at another of the game's own.
    ///
    /// ON THE HOLDER'S OVERRIDE, which is the material for a texture the mesh wears and the part
    /// for the menu picture. Same machinery as a mesh: the pair of re-points is measured against
    /// the pristine holder, rehearsed, and only then written.
    /// </summary>
    public void PickTextureRow(SheetTab.TextureRow row, string toPackage)
    {
        if (_mod is null) { Say("open a mod first — a change has to be recorded somewhere"); return; }

        // A COPY NAMES THE TEXTURE, by the same material parameter an import uses. The value is
        // an object path rather than a file of the author's, which the build already accepts in
        // this field — the picture and the paint both take either.
        if (PaintTarget(row, forWriting: true) is var (onto, ownerId))
        {
            // PAINTING IT WITH ITS OWN TEXTURE IS PAINTING NOTHING, and an entry saying so is
            // an entry the build has to resolve for no reason. Same rule as the picture.
            if (PackageOf(toPackage).Equals(PackageOf(row.Package), StringComparison.OrdinalIgnoreCase))
            {
                if (row.MineKey.Length > 0 && row.MineSlot < onto.Materials.Count)
                    onto.Materials[row.MineSlot].Textures.Remove(row.MineKey);
                _mod.Save();
                row.Replacement = "";
                row.Picked = "";
                row.MineKey = "";
                row.Mark();
                _artVersion++;
                Say($"{ownerId}: {row.Slot} is the game's own {row.GameLeaf}");
                AfterEditsChanged();
                return;
            }

            // The key and the material slot it is already painted under, for the reasons
            // ImportTextureRow gives.
            var key = row.MineKey.Length > 0 ? row.MineKey : row.Slot;
            onto.Material(row.MineSlot).Textures[key] = toPackage;
            row.MineKey = key;
            _mod.Save();
            row.Picked = toPackage;
            row.Mark();
            _artVersion++;
            Say($"{ownerId}: {row.Slot} wears the game's {Leaf(toPackage)}");
            AfterEditsChanged();
            return;
        }

        if (!row.CanPick)
        { Say($"{row.Slot}: nothing here names it in a way this can move"); return; }

        if (PlanRepoint(row.Holder, row.Package, toPackage, out var why) is not { } plan)
        { Say($"{row.Slot}: {why}"); return; }

        var ov = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(row.Holder, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = row.Holder }; _mod.Overrides.Add(ov); }

        var fromObject = Leaf(row.Package);
        ov.Repoints.RemoveAll(r => r.From.Equals(row.Package, StringComparison.OrdinalIgnoreCase) ||
                                   r.From.Equals(fromObject, StringComparison.Ordinal));
        ov.Repoints.AddRange(plan);
        _mod.Save();

        row.Picked = toPackage;
        row.Mark();
        BuildModifiedSet();
        _artVersion++;
        Say($"{row.Slot}: {Leaf(row.Holder)} now points at {Leaf(toPackage)} — " +
            string.Join(", ", plan.Select(p =>
                $"{p.Expect} by {(p.From.StartsWith('/') ? "path" : "name")}")));
        AfterEditsChanged();
    }

    /// <summary>
    /// A texture's size and format in one short line — what a replacement has to fit.
    ///
    /// Both kinds asked in the same order RecordTextureOverride asks them, so the line beside a
    /// row and the rehearsal an import runs cannot disagree about which a texture is.
    /// </summary>
    private string DescribeTexture(string package)
    {
        if (_provider is null) return "";
        try
        {
            if (VirtualTexture.Describe(_provider, package, out _) is { } vt)
                return $"{vt.Width}×{vt.Height}  {vt.Format.ToString().Replace("PF_", "")}  ·  virtual";
            if (TextureImport.Describe(_provider, package, out _) is { } plain)
                return $"{plain.Width}×{plain.Height}  {plain.Format.ToString().Replace("PF_", "")}";
        }
        catch (Exception) { /* a blank line is better than a wrong one */ }
        return "";
    }

    /// <summary>
    /// Put an image over the part's menu picture.
    ///
    /// IMMEDIATE, like every other import in this editor. It was the one that waited for Save,
    /// which made the picture the odd one out in a panel where a mesh and a texture both land the
    /// moment they are chosen.
    ///
    /// BOTH HALVES ARE WRITTEN. The pixels go into the texture the part points at, which is the
    /// mechanism the build already has for artwork on a game asset; and ModOverride.Icon records
    /// what was chosen, which is what makes reverting the PART take its picture with it rather
    /// than leaving the name and the mesh back and the picture changed.
    /// </summary>
    public void ImportPartIcon(PartTab tab, string file)
    {
        if (_mod is null) { Say("open a mod first — an import has to belong to one"); return; }

        // A COPY NAMES ITS OWN PICTURE. Nothing is written into a game texture at all, which is
        // why this one works even for a part that names its picture by bank tag — there is no
        // texture to import over, and a copy does not need one.
        if (tab.Copy is { } copy)
        {
            var into = ImportArt(file, "Icons", out var copyWhy);
            if (into is null) { Say($"{tab.Title}: {copyWhy}"); return; }

            copy.Icon = into;
            _mod.Save();
            tab.EditIcon = into;
            tab.SetIconPreview(LoadPreview(into));
            if (tab.IconRow is { } iconRow) { iconRow.Replacement = into; iconRow.Mark(); }
            tab.MarkIconSaved();
            Say($"{copy.Id}: your copy's picture is {into}");
            AfterEditsChanged();
            return;
        }

        if (tab.IconRow is not { } row)
        { Say($"{tab.Title}: {tab.IconNote}"); return; }

        if (!RecordTextureOverride(row.Package, file, $"{tab.Title}'s picture", "Icons")) return;

        var leaf = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(row.Package, StringComparison.OrdinalIgnoreCase))?.Image ?? "";

        var ov = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(tab.Entry.Part.Path, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = tab.Entry.Part.Path }; _mod.Overrides.Add(ov); }
        ov.Icon = leaf;
        _mod.Save();

        row.Replacement = leaf;
        tab.EditIcon = leaf;
        tab.SetIconPreview(LoadPreview(leaf));
        tab.MarkIconSaved();
        AfterEditsChanged();
    }

    /// <summary>
    /// Put the menu picture back to the game's own — every way it might have been changed.
    ///
    /// THREE PLACES NOW: the pixels on the icon texture, the record of the choice on the part,
    /// and a re-point of the part's icon reference. A revert that takes two of three leaves the
    /// picture changed while saying it put it back.
    /// </summary>
    /// <summary>
    /// POINT A COPY AT ONE OF THE GAME'S OWN PICTURES.
    ///
    /// Its own route because a copy's picture is not a re-point: <c>ModPart.Icon</c> names the
    /// texture outright, and the same field takes a file of the author's. Sending it through
    /// PickTextureRow — which is what the picker did — wrote a TEXTURE entry keyed "Menu
    /// picture" that painted nothing and left the picture unchanged, while saying it worked.
    /// </summary>
    public void PickPartIcon(PartTab tab, string toPackage)
    {
        if (_mod is null) { Say("open a mod first — a change has to be recorded somewhere"); return; }
        if (tab.Copy is not { } copy) { Say($"{tab.Title}: only a copy names its own picture"); return; }

        // CHOOSING THE PICTURE IT ALREADY HAS IS CHOOSING NONE. Left as a written value the row
        // reads "T_Thumb_X instead of T_Thumb_X", which is a sentence about nothing, and the
        // project carries an entry that pins a picture it would have had anyway.
        var same = tab.IconRow is { } have &&
                   PackageOf(toPackage).Equals(PackageOf(have.Package), StringComparison.OrdinalIgnoreCase);

        copy.Icon = same ? "" : toPackage;
        _mod.Save();

        if (tab.IconRow is { } row)
        {
            row.Replacement = "";
            row.Picked = same ? "" : toPackage;
            row.Mark();
        }

        tab.EditIcon = copy.Icon;
        tab.MarkIconSaved();
        ShowCopyPicture(tab, copy);
        _artVersion++;
        Say(same
            ? $"{copy.Id}: that is the picture it already shows — left as {tab.Entry.Label}'s own"
            : $"{copy.Id}: your copy's picture is the game's {Leaf(toPackage)}");
        AfterEditsChanged();
    }

    public void RevertPartIcon(PartTab tab)
    {
        // A COPY'S PICTURE IS ONE FIELD ON THE PART. Emptying it puts the copy back on the
        // template's own picture, which is what it had the moment it was duplicated.
        if (tab.Copy is { } copy)
        {
            if (copy.Icon.Length == 0)
            { Say($"{copy.Id}: your copy already shows the picture it was made with"); return; }

            copy.Icon = "";
            _mod?.Save();

            if (tab.IconRow is { } iconRow)
            { iconRow.Replacement = ""; iconRow.Picked = ""; iconRow.Mark(); }

            tab.EditIcon = "";
            tab.SetIconPreview(null);
            tab.MarkIconSaved();
            _artVersion++;
            Say($"{copy.Id}: back to {tab.Entry.Label}'s own picture");
            AfterEditsChanged();
            return;
        }

        if (_mod is null || tab.IconRow is not { } row) return;

        var undid = false;
        if (row.Replacement.Length > 0) undid |= RevertPackage(row.Package, row.GameLeaf);
        undid |= DropRepointsOn(row.Holder, row.Package, row.Slot);

        if (_mod.Overrides.FirstOrDefault(
                o => o.Target.Equals(tab.Entry.Part.Path, StringComparison.OrdinalIgnoreCase))
            is { } ov && ov.Icon.Length > 0)
        {
            ov.Icon = "";
            if (!ov.HasEdit) _mod.Overrides.Remove(ov);
            _mod.Save();
            undid = true;
        }

        if (!undid) { Say($"{tab.Title}: this mod does nothing to its picture"); return; }

        row.Replacement = "";
        row.Picked = "";
        tab.EditIcon = "";
        tab.SetIconPreview(null);
        tab.MarkIconSaved();
        _artVersion++;
        AfterEditsChanged();
    }

    /// <summary>
    /// Put an image over ONE texture the part wears.
    ///
    /// THE SAME CALL THE DATA EXPLORER'S IMPORT MAKES. RecordTextureOverride rehearses the image
    /// against the real target first — its size, format and mip layout come from the texture
    /// being replaced, not from the file — so a picture that cannot be written is refused here
    /// rather than becoming noise the GPU samples. One route, so "import a texture" means one
    /// thing wherever it is done.
    /// </summary>
    public void ImportTextureRow(SheetTab.TextureRow row, string file)
    {
        if (_mod is null) { Say("open a mod first — an import has to belong to one"); return; }

        if (PaintTarget(row, forWriting: true) is var (onto, ownerId))
        {
            // ONTO THE PIECE THE ROW CAME FROM. This used to be refused for a garment with
            // pieces, because the only place the panel could write was the part's own list and
            // the build ignores that one — see PaintHolder. A weapon's goes on the ModMesh for
            // that piece, which is the same interface and a different object.
            var into = ImportArt(file, "Textures", out var copyWhy);
            if (into is null) { Say($"{row.Slot}: {copyWhy}"); return; }

            // KEYED BY THE MATERIAL PARAMETER, which is what the row already reads — "Base BSC
            // VT", "Base Mask 1 VT". The build paints a copy by naming parameters, so the key
            // here and the name on the row have to be the same string.
            //
            // UNLESS THE COPY ALREADY PAINTS IT under another of the three spellings, which his
            // own project does (D, MSK, N). Writing the parameter beside an existing suffix
            // would leave two entries aiming at one texture and let the build pick either.
            //
            // AND INTO THE ROW'S OWN MATERIAL SLOT. A helmet's visor is a second material, and
            // paint written into slot one for a texture the mesh wears on slot two is applied
            // to the wrong material or to nothing.
            var key = row.MineKey.Length > 0 ? row.MineKey : row.Slot;
            onto.Material(row.MineSlot).Textures[key] = into;
            row.MineKey = key;
            _mod.Save();
            row.Replacement = into;
            row.Mark();
            _artVersion++;
            Say($"{ownerId}: {(row.Piece.Length > 0 ? row.Piece + "'s " : "")}" +
                $"{row.Slot} is painted with {into}");
            AfterEditsChanged();
            return;
        }

        if (!RecordTextureOverride(row.Package, file, row.Slot + " on " + row.GameLeaf, "Textures"))
            return;

        row.Replacement = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(row.Package, StringComparison.OrdinalIgnoreCase))?.Image ?? "";
        row.Mark();
        AfterEditsChanged();
    }

    /// <summary>
    /// THIS MOD'S OWN IMPORTED IMAGES, by file name — what "Use…" offers.
    ///
    /// Icons and Textures together, because an image is an image and the rehearsal an import runs
    /// is what decides whether one fits a given target. A picture imported as an icon that is not
    /// offered for a texture would be a rule the author never made and cannot see.
    ///
    /// PER MOD, never a shared pool: ModBuild.FindImage carries the author's own requirement in
    /// its comment — "never overlap with any other mod source unless i import them into that mod
    /// myself" — and this lists the same folders it resolves from.
    /// </summary>
    public IReadOnlyList<string> MyImages()
    {
        if (_mod is null) return [];

        try
        {
            var modDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_modPath)) ?? ".";
            var project = System.IO.Path.GetFileNameWithoutExtension(_modPath);

            return new[] { "Icons", "Textures" }
                   .Select(k => System.IO.Path.Combine(modDir, "ModSource", project, k))
                   .Where(Directory.Exists)
                   .SelectMany(Directory.EnumerateFiles)
                   .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                   .Select(System.IO.Path.GetFileName)
                   .OfType<string>()
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                   .ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Whether there is anything for "Use…" to offer. No images, no button.</summary>
    public bool HasMyImages => MyImages().Count > 0;

    /// <summary>
    /// THE GATE TAGS A PART ACTUALLY CARRIES, read from its AllowedSlots.
    ///
    /// A LIST, NOT A TEXT BOX, and the old studio says why: "A gate tag is 50-odd characters of
    /// dotted namespace, and getting one character wrong fails the build". The free-text Remove
    /// box this replaces asked somebody to type one from memory.
    ///
    /// Cached per part: it is a package read, and the answer cannot change while the game is
    /// mounted.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _partTags = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> TagsOfPart(string path)
    {
        if (_partTags.TryGetValue(path, out var have)) return have;

        IReadOnlyList<string> tags = [];
        try
        {
            if (_provider is { } p)
            {
                var exports = AssetLoader.Read(
                    () => p.LoadPackage(AssetLoader.PackagePath(path)).GetExports().ToArray());
                tags = PartDetails.TagsIn(exports, "AllowedSlots");
            }
        }
        catch (Exception e) { Say($"could not read {Leaf(path)}'s tags: {e.Message}"); }

        _partTags[path] = tags;
        return tags;
    }

    /// <summary>The tag rows for a part, with whatever this mod already takes off unticked.</summary>
    private void FillTags(PartTab tab, IEnumerable<string> removed)
    {
        var off = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
        tab.SetTags(TagsOfPart(tab.Entry.Part.Path).Select(t => new SheetTab.TagRow(t, !off.Contains(t))));
    }

    /// <summary>
    /// WHAT THIS PART ALREADY CARRIES THAT NO OTHER SECTION SHOWS.
    ///
    /// Re-points and renames. A re-point made by Pick appears here as well, deliberately: it is
    /// the only place that says a part's reference has moved WITHOUT knowing which row made it
    /// move, which is what somebody opening a project made by another author needs.
    /// </summary>
    private void FillDeepRows(PartTab tab)
    {
        var ov = _mod?.Overrides.FirstOrDefault(
            o => o.Target.Equals(tab.Entry.Part.Path, StringComparison.OrdinalIgnoreCase));

        if (ov is null) { tab.SetDeepRows([]); return; }

        tab.SetDeepRows(
            ov.Repoints.Select(r => new SheetTab.DeepRow(
                    "points at", r.From, r.To, $"{r.Expect} reference(s)"))
              .Concat(ov.RenameNames.Select(kv => new SheetTab.DeepRow(
                    "renames", kv.Key, kv.Value, "everywhere in this asset"))));
    }

    /// <summary>
    /// Rewrite one name everywhere it appears in this part.
    ///
    /// THE BLUNT INSTRUMENT, and the old studio says so in its own tooltip: for a reference that
    /// cannot be found by scanning. Unlike a re-point it takes no count and makes no promise
    /// about how many it moved, which is exactly why it is in "deeper changes" and not beside
    /// the mesh rows.
    /// </summary>
    public void AddRename(PartTab tab, string from, string to)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        from = from.Trim();
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0) { Say("a rename needs both a name and a new one"); return; }
        if (string.Equals(from, to, StringComparison.Ordinal))
        { Say("those are the same name"); return; }

        var part = tab.Entry.Part.Path;
        var ov = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(part, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = part }; _mod.Overrides.Add(ov); }

        ov.RenameNames[from] = to;
        _mod.Save();

        tab.RenameFrom = "";
        tab.RenameTo = "";
        FillDeepRows(tab);
        BuildModifiedSet();
        Say($"{tab.Title}: '{from}' will be written as '{to}' everywhere in this part");
        AfterEditsChanged();
    }

    /// <summary>Take one deeper change back off, whichever kind it is.</summary>
    public void DropDeepRow(PartTab tab, SheetTab.DeepRow row)
    {
        if (_mod is null) return;

        var part = tab.Entry.Part.Path;
        if (_mod.Overrides.FirstOrDefault(
                o => o.Target.Equals(part, StringComparison.OrdinalIgnoreCase)) is not { } ov)
            return;

        var gone = row.Kind == "renames"
            ? ov.RenameNames.Remove(row.From)
            : ov.Repoints.RemoveAll(r => r.From.Equals(row.From, StringComparison.Ordinal)) > 0;

        if (!gone) { Say($"{row.FromLeaf}: nothing of this mod's to take off"); return; }

        if (!ov.HasEdit && ov.Icon.Length == 0) _mod.Overrides.Remove(ov);
        _mod.Save();

        FillDeepRows(tab);
        // A re-point dropped here is the same change a mesh or texture row shows, so the rows
        // have to be read again or one of them keeps claiming a choice that is gone.
        FillMeshRows(tab, tab.Entry.Detail, ov);
        _ = FillTextureRowsAsync(tab, tab.Entry.Detail);
        BuildModifiedSet();
        _artVersion++;
        Say($"{tab.Title}: took off the {row.Kind} {row.FromLeaf}");
        AfterEditsChanged();
    }

    /// <summary>
    /// THE PART'S OWN PROPERTIES, read off the UI thread and laid into its embedded grid.
    ///
    /// Same read as opening the asset in a tab, so the editor's numbers and the asset reader's
    /// cannot disagree about a part — and the same edit path underneath, which rehearses every
    /// change against the build's own write before the project hears about it.
    /// </summary>
    private async Task FillNumbersAsync(PartTab tab)
    {
        if (_provider is null) return;

        var provider = _provider;
        var path = tab.Props.FilePath;

        try
        {
            var read = await Task.Run(() => AssetProps.Read(provider, path));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                tab.Props.Fill(read);
                ApplyModEdits(tab.Props);
                if (!tab.IsCopy) tab.NumbersRead();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => Say($"{tab.Title}: could not read its properties — {ex.Message}"));
        }
    }

    /// <summary>
    /// THE GAME'S MENU PICTURES, and which of them belong to one slot.
    ///
    /// PartsCatalog.Icons is the old studio's own list and it says why in its comment: "an icon
    /// is a picture of a specific garment: offering all 1,296 of them when someone is making a
    /// helmet is technically complete and practically useless." That is the "Legs only" tick the
    /// author's screenshot shows, and it already lived in Core — the first version of this picker
    /// offered nine and a half thousand raw textures instead, including the galaxy map's UI
    /// atlas, because it asked the asset index rather than the catalogue.
    ///
    /// BOTH LISTS AT ONCE: every icon as the candidates, and the slot's own as a set, so the tick
    /// box narrows what is already loaded instead of re-reading the catalogue per keystroke.
    /// </summary>
    public (List<PickAssetWindow.Candidate> All, HashSet<string> InSlot) IconChoices(string? friendlySlot)
    {
        var all = new List<PickAssetWindow.Candidate>();
        var inSlot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var i in _catalog.Icons(null))
            all.Add(new PickAssetWindow.Candidate(
                i.Name,
                i.UsedByCount > 1 ? $"worn by {i.UsedBy} and {i.UsedByCount - 1} more"
                                  : $"worn by {i.UsedBy}",
                PackageOf(i.Path)));

        if (friendlySlot is not null)
            foreach (var i in _catalog.Icons(friendlySlot)) inSlot.Add(PackageOf(i.Path));

        return (all, inSlot);
    }

    /// <summary>
    /// The copy the open tab is editing, when it is editing one.
    ///
    /// A row hands back no tab — a MeshRow and a TextureRow know their own package and nothing
    /// about who is showing them — so the ones reached from a row ask here instead.
    /// </summary>
    private ModPart? CopyOpen => (_tab as PartTab)?.Copy;

    /// <summary>The part tab a row belongs to, for the same reason <see cref="CopyOpen"/> exists.</summary>
    private PartTab? PartTabOpen => _tab as PartTab;

    /// <summary>
    /// WHERE A ROW'S PAINT IS WRITTEN: the piece when the garment authors pieces, the part when
    /// it does not.
    ///
    /// This is what closes "per-piece painting is not in this panel yet". It was refused out
    /// loud (5ee5455) because the only place the panel could write was the PART's own material
    /// list, and ModBuild ignores that entirely for a garment with pieces — anything imported
    /// there was copied into ModSource, drawn as applied, and silently dropped at build time.
    /// The rows know which piece they came from now, so there is somewhere correct to put it.
    ///
    /// <paramref name="forWriting"/> decides whether a piece the copy has no entry for is
    /// CREATED. Never on a read: RegionFor appends, and a panel that grew an entry just by being
    /// looked at would put pieces into a project nobody authored.
    /// </summary>
    private static IMeshAuthoring? PaintHolder(ModPart? copy, string piece, bool forWriting)
    {
        if (copy is null) return null;
        if (piece.Length == 0 || !copy.HasRegions) return copy;
        return forWriting ? copy.RegionFor(piece) : copy.RegionNamed(piece);
    }

    /// <summary>
    /// WHAT A TEXTURE ROW'S EDIT IS WRITTEN ON, whichever kind of copy is open.
    ///
    /// A part's artwork lives on the part or on one of its pieces; a WEAPON's lives on the
    /// ModMesh for that piece, because a weapon part names no mesh at all. Both are an
    /// IMeshAuthoring, which is why one card can serve them — and the row already knows which
    /// piece it came from, so this is a lookup rather than a decision.
    ///
    /// Null when the open tab is not editing a copy: an override records its artwork against the
    /// TEXTURE package instead, which is a different mechanism and a different call.
    /// </summary>
    private (IMeshAuthoring Onto, string Id)? PaintTarget(SheetTab.TextureRow row, bool forWriting)
    {
        switch (_tab)
        {
            case PartTab { Copy: { } copy }:
                return PaintHolder(copy, row.Piece, forWriting) is { } onto
                    ? (onto, copy.Id) : null;

            // A MESH COPY IS ITSELF THE THING THAT HOLDS THE PAINT. A part hands in its
            // region and a weapon hands in the ModMesh for the piece; here the tab IS one.
            case ReplacementTab { Mesh: { } own }:
                return (own, Leaf(own.Source));

            case WeaponTab { Copy: { } weapon } when _mod is not null:
                // BY THE ROW'S OWN TEXTURE, not by the piece name: the mesh entry is keyed on the
                // game asset it copies, and the row carries the material that asset wears.
                var mine = _mod.Meshes.FirstOrDefault(
                    m => WeaponAuthor.PiecesOf(_provider!, _mod, weapon.Id)
                        .Any(p => p.Slot.Equals(row.Piece, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(m.Source, p.Mesh, StringComparison.OrdinalIgnoreCase)));
                return mine is null ? null : (mine, weapon.Id);

            default:
                return null;
        }
    }

    /// <summary>
    /// READ THE MESH ROWS BACK OUT OF THE PROJECT after a copy's geometry changed.
    ///
    /// A copy names ONE mesh and one import for the whole garment, so a change made on one row
    /// is a change to every row — and setting only the row that was clicked left its siblings
    /// showing the author's file after it had been reverted. Re-filling asks the project rather
    /// than trying to work out which other rows to touch, which is the same answer the panel
    /// gives when it opens.
    /// </summary>
    private void RefillCopyMeshRows(PartTab? tab)
    {
        if (tab?.Copy is not null) FillMeshRows(tab, tab.Entry.Detail, null);
    }

    /// <summary>Where one of this mod's own images actually is, for a thumbnail off disk.</summary>
    public string? PathOfMyImage(string leaf)
    {
        try { return ModBuild.FindImage(leaf, _modPath); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// One game texture's shape, cached — the picker asks it for every candidate it scopes.
    ///
    /// CACHED BECAUSE THE SCOPE ASKS NINE THOUSAND TIMES. Describing a texture opens its package;
    /// doing that per candidate on every keystroke would make the filter box unusable, and the
    /// answer cannot change while the game is mounted.
    /// </summary>
    public (int W, int H) ShapeOfPackage(string package)
    {
        if (_shapes.TryGetValue(package, out var have)) return have;
        var shape = DescribeShape(package);
        _shapes[package] = shape;
        return shape;
    }

    private readonly Dictionary<string, (int W, int H)> _shapes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One of this mod's images, measured — so the menu can say which of them fits.
    ///
    /// Null for anything that cannot be decoded here, which today means TGA: no size shown
    /// rather than a guessed one, because a wrong number beside a file name is worse than none.
    /// </summary>
    public (int W, int H)? ShapeOf(string leaf)
    {
        try
        {
            var path = ModBuild.FindImage(leaf, _modPath);
            if (path is null || !File.Exists(path)) return null;
            using var bmp = new Bitmap(path);
            return ((int)bmp.PixelSize.Width, (int)bmp.PixelSize.Height);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Put an image this mod ALREADY HOLDS over one of the part's textures.
    ///
    /// The same call as a fresh import, on the file already in the mod's own folder — so it is
    /// rehearsed against THIS target rather than assumed to fit because it fitted another one.
    /// A 4096 mask and a 256 thumbnail are both "one of my images" and only one of them belongs
    /// on a given row.
    /// </summary>
    public void UseTextureRow(SheetTab.TextureRow row, string leaf)
    {
        if (_mod is null) { Say("open a mod first"); return; }
        if (ModBuild.FindImage(leaf, _modPath) is not { } file || !File.Exists(file))
        { Say($"cannot find {leaf} in this mod's own images"); return; }

        if (!RecordTextureOverride(row.Package, file, row.Slot + " on " + row.GameLeaf, "Textures"))
            return;

        row.Replacement = leaf;
        row.Mark();
        AfterEditsChanged();
    }

    /// <summary>The same, for the menu picture — both halves, as an icon import writes both.</summary>
    public void UsePartIcon(PartTab tab, string leaf)
    {
        if (_mod is null || tab.IconRow is null) return;
        if (ModBuild.FindImage(leaf, _modPath) is not { } file || !File.Exists(file))
        { Say($"cannot find {leaf} in this mod's own images"); return; }

        ImportPartIcon(tab, file);
    }

    /// <summary>
    /// Take a re-point of one reference off its holder, and tidy up after it.
    ///
    /// SHARED by every Revert, because the alternative is three copies of this and three chances
    /// for one of them to forget the empty-override rule.
    /// </summary>
    private bool DropRepointsOn(string holder, string fromPackage, string label)
    {
        if (_mod is null || holder.Length == 0) return false;

        if (_mod.Overrides.FirstOrDefault(
                o => o.Target.Equals(holder, StringComparison.OrdinalIgnoreCase)) is not { } ov)
            return false;

        var fromObject = Leaf(fromPackage);
        if (ov.Repoints.RemoveAll(r => r.From.Equals(fromPackage, StringComparison.OrdinalIgnoreCase) ||
                                       r.From.Equals(fromObject, StringComparison.Ordinal)) == 0)
            return false;

        // An override with nothing left in it ships a byte-for-byte rewrite of a game package
        // that changes nothing and conflicts with every other mod touching it.
        if (!ov.HasEdit) _mod.Overrides.Remove(ov);
        _mod.Save();
        BuildModifiedSet();
        Say($"{label}: back to {Leaf(fromPackage)}");
        return true;
    }

    /// <summary>
    /// Put one texture row back: its own pixels, and whatever it was pointed at.
    ///
    /// BOTH PLACES, as on a mesh row. An import is an override on the TEXTURE; a pick is a
    /// re-point on the MATERIAL. Taking only the first left a re-pointed row reporting success
    /// and changing nothing.
    /// </summary>
    public void RevertTextureRow(SheetTab.TextureRow row)
    {
        // A COPY'S PAINT IS AN ENTRY IN THE PART, so putting a texture back is removing that
        // entry — nothing was ever written into a game package to revert.
        if (PaintTarget(row, forWriting: false) is var (from, ownerId))
        {
            // Out of the piece and the slot it was written into — see TextureRow.MineSlot.
            // Nothing is GROWN to reach either: there is nothing in a slot that does not exist.
            if (row.MineKey.Length == 0 || row.MineSlot >= from.Materials.Count ||
                !from.Materials[row.MineSlot].Textures.Remove(row.MineKey))
            { Say($"{row.Slot}: your copy paints it with the game's own"); return; }

            _mod?.Save();
            row.Replacement = "";
            row.Picked = "";
            row.MineKey = "";
            row.Mark();
            _artVersion++;
            Say($"{ownerId}: {row.Slot} is the game's {row.GameLeaf} again");
            AfterEditsChanged();
            return;
        }

        var undid = false;
        if (row.Replacement.Length > 0) undid |= RevertPackage(row.Package, row.GameLeaf);
        undid |= DropRepointsOn(row.Holder, row.Package, row.Slot);

        if (!undid) { Say($"{row.Slot}: this mod does nothing to it"); return; }

        row.Replacement = "";
        row.Picked = "";
        row.Mark();
        _artVersion++;
        AfterEditsChanged();
    }

    /// <summary>
    /// Open one of the part's textures in the 2D viewer.
    ///
    /// Where the channels can be split apart, which is the only way to check a mask: this game
    /// carries a different meaning per channel, and a mask read as a picture looks like noise
    /// whether it is right or wrong.
    /// </summary>
    public async Task ViewTextureRowAsync(SheetTab.TextureRow row)
    {
        if (_provider is null) { Say("the game is not open yet"); return; }

        var path = ContainerPathOf(row.Package);

        // ALREADY OPEN? Forward and refresh, rather than stacking tabs on one texture.
        if (Tabs.OfType<TextureTab>().FirstOrDefault(t => t.FilePath == path) is { } open)
        { Tab = open; return; }

        var tex = new TextureTab(row.GameLeaf, path, "Texture2D");
        tex.View.Trace = Say;
        Tabs.Add(tex);
        Tab = tex;
        Raise(nameof(AnyTabs));
        await tex.LoadAsync(_provider);
    }

    /// <summary>
    /// The game's own assets of one kind, as picker candidates.
    /// </summary>
    public IReadOnlyList<PickAssetWindow.Candidate> GameAssets(AssetKind kind)
    {
        if (_index is null) return [];

        return _index.Root.DescendantFiles()
                     .Where(f => f.Kind == kind)
                     .Select(f => new
                     {
                         f.Name,
                         Package = PropertyEdit.PackageForFile(f.FullPath),
                     })
                     .Where(x => x.Package is not null)
                     .Select(x => new PickAssetWindow.Candidate(
                         System.IO.Path.GetFileNameWithoutExtension(x.Name),
                         x.Package!,
                         x.Package!))
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    /// <summary>
    /// THE TWO RE-POINTS THAT MOVE ONE ASSET REFERENCE, or the reason there are none.
    ///
    /// A soft object reference is serialised as TWO FNames — the package path and the object name
    /// — so pointing an asset at a different asset is two re-points that must BOTH land, or the
    /// reference is left half rewritten and resolves to nothing. ModOverride.Repoint says exactly
    /// this in its own comment; nothing until now computed the pair.
    ///
    /// COUNTED AGAINST THE PRISTINE PACKAGE, never through the plain provider. The build refuses
    /// a re-point whose reference count does not match, and the Studio mounts the game's whole
    /// folder — every installed mod included, this tool's own output among them. Counting through
    /// `LoadPackage` would count the author's LAST BUILD and write an Expect that only holds
    /// until they uninstall their own mod. That exact confusion is on record here.
    ///
    /// EXPECT IS MEASURED, not assumed to be one. A part naming the same mesh twice is two
    /// references, and a plan that said one would be refused by the build with a number the
    /// author then has to go and find.
    /// </summary>
    private List<ModOverride.Repoint>? PlanRepoint(string holderPackage, string fromPackage,
                                                   string toPackage, out string? why)
    {
        why = null;
        if (_provider is null) { why = "the game is not open yet"; return null; }

        if (string.Equals(fromPackage, toPackage, StringComparison.OrdinalIgnoreCase))
        { why = "that is the one it already points at"; return null; }

        if (AssetCopy.PristineRead(_provider, holderPackage, out var readWhy) is not { } src)
        { why = readWhy ?? $"could not read the game's own {Leaf(holderPackage)}"; return null; }

        if (src.Pieces.FirstOrDefault(p => p.IsPackage)?.Data is not { } raw)
        { why = $"{Leaf(holderPackage)} has no package data to read"; return null; }

        var fromObject = Leaf(fromPackage);
        var toObject = Leaf(toPackage);

        // COUNTED IN SEQUENCE, AS THE BUILD WILL APPLY THEM — not both against the original.
        //
        // This is a fault the rehearsal caught in its own plan. Counting the package path and the
        // object name on the same pristine bytes gave "1 and 1"; applying the first then made the
        // second read zero, and the build would have refused the whole override with
        // "'SK_HAAF_Clo001_HELM' is referenced 0 time(s), not 1". The build runs re-points one
        // after another, so each Expect has to be the count at ITS point in the sequence.
        //
        // REHEARSED, not predicted: every step is really applied to a copy here and the
        // references are counted back out, which is the same promise-checking the build does. A
        // re-point that cannot work is refused where it was asked for, with a reason, instead of
        // minutes later in a build log under lines about other things.
        var plan = new List<ModOverride.Repoint>();

        try
        {
            var working = raw;

            // The package path, then the object name — the two halves of a soft object
            // reference. Either may be absent, and a part whose replacement has the same leaf
            // name moves on the path alone.
            foreach (var (from, to) in new[] { (fromPackage, toPackage), (fromObject, toObject) })
            {
                if (string.Equals(from, to, StringComparison.Ordinal)) continue;

                var hits = NameRefEdit.Count(working, from);
                if (hits == 0) continue;

                var after = NameRefEdit.Repoint(working, from, to, hits, out var stepWhy);
                if (stepWhy is not null)
                { why = $"'{Leaf(from)}' could not be moved: {stepWhy}"; return null; }

                var left = NameRefEdit.Count(after, from);
                if (left != 0)
                { why = $"{left} reference(s) to '{Leaf(from)}' would survive"; return null; }

                plan.Add(new ModOverride.Repoint(from, to, hits));
                working = after;
            }
        }
        catch (Exception e) { why = $"the re-point could not be rehearsed: {e.Message}"; return null; }

        if (plan.Count == 0)
        {
            why = $"{Leaf(holderPackage)} does not name {fromObject} in its export data — " +
                  "nothing here points at it, so there is nothing to move";
            return null;
        }

        return plan;
    }

    /// <summary>
    /// Point one of a part's meshes at ANOTHER OF THE GAME'S, rather than at a file of your own.
    ///
    /// ON THE PART'S OWN OVERRIDE, because it is the PART that holds the reference — the mesh
    /// package knows nothing about who names it. That is the same place FillMeshRows reads a
    /// re-point back from, so what is written here is what the row shows next time.
    /// </summary>
    public void PickMeshRow(PartTab tab, SheetTab.MeshRow row, string toPackage)
    {
        if (_mod is null) { Say("open a mod first — a change has to be recorded somewhere"); return; }

        // A COPY JUST NAMES THE OTHER MESH. No re-point, no reference counting, nothing rewritten
        // — the part this mod ships says which of the game's meshes it wears, so pointing it
        // somewhere else is one field.
        if (tab.Copy is { } copy)
        {
            // PER PIECE when the garment has pieces. Part-wide, one torso pick made every piece
            // wear the torso's mesh.
            if (row.PerPiece) PieceOf(copy, row).Mesh = toPackage;
            else copy.Mesh = toPackage;

            _mod.Save();
            row.Picked = toPackage;
            row.Mark();
            RefillCopyMeshRows(tab);
            _artVersion++;
            Say(row.PerPiece
                ? $"{copy.Id}: its {row.Region} wears the game's {Leaf(toPackage)}"
                : $"{copy.Id}: your copy wears the game's {Leaf(toPackage)}");
            AfterEditsChanged();
            return;
        }

        var part = tab.Entry.Part.Path;
        if (PlanRepoint(part, row.GamePath, toPackage, out var why) is not { } plan)
        { Say($"{row.Variant}: {why}"); return; }

        var ov = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(part, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = part }; _mod.Overrides.Add(ov); }

        // REPLACE THIS MESH'S OWN PAIR, leave everybody else's alone. A part with two body types
        // re-pointed one at a time must not have the first one dropped by the second.
        var fromObject = Leaf(row.GamePath);
        ov.Repoints.RemoveAll(r => r.From.Equals(row.GamePath, StringComparison.OrdinalIgnoreCase) ||
                                   r.From.Equals(fromObject, StringComparison.Ordinal));
        ov.Repoints.AddRange(plan);
        _mod.Save();

        row.Picked = toPackage;
        row.Mark();
        BuildModifiedSet();
        _artVersion++;
        Say($"{row.Variant}: now points at {Leaf(toPackage)} — " +
            string.Join(", ", plan.Select(p =>
                $"{p.Expect} by {(p.From.StartsWith('/') ? "path" : "name")}")));
        AfterEditsChanged();
    }

    /// <summary>
    /// Put an FBX over ONE of a part's meshes.
    ///
    /// Its own override, aimed at that mesh package. Two body types can therefore carry two
    /// different files, which is the whole reason the rows are separate.
    /// </summary>
    /// <summary>
    /// THE PIECE THIS ROW STANDS FOR, made if the part does not carry it yet.
    ///
    /// A garment of several pieces names a mesh PER PIECE, and until now Import and Pick both
    /// wrote the part-wide field — so replacing the torso also replaced the boots, silently, with
    /// the same file. The entry records the game mesh it stands in for as well, because that is
    /// what an import is measured against when the build checks it fits.
    /// </summary>
    private static ModRegion PieceOf(ModPart part, SheetTab.MeshRow row)
    {
        var have = part.Regions.FirstOrDefault(
            r => r.Region.Equals(row.Region, StringComparison.OrdinalIgnoreCase));
        if (have is not null) return have;

        // AN OBJECT PATH, NOT A PACKAGE. A piece's Mesh is a reference the build writes into the
        // asset, and a soft reference is "/A/B/C.C" — the row carries the package, because that
        // is what its comparisons want. Stored without the suffix the build refuses it:
        // "piece 'ARMS' mesh ... has no object suffix - it should end in .AssetName".
        var leaf = row.GamePath[(row.GamePath.LastIndexOf('/') + 1)..];
        var made = new ModRegion
        {
            Region = row.Region,
            Mesh = row.GamePath.Contains('.') ? row.GamePath : $"{row.GamePath}.{leaf}",
        };
        part.Regions.Add(made);
        return made;
    }

    public void ImportMeshRow(SheetTab.MeshRow row, string file)
    {
        if (_mod is null) { Say("open a mod first — an import has to belong to one"); return; }

        var leaf = ImportArt(file, "Meshes", out var why);
        if (leaf is null) { Say($"{row.Variant}: {why}"); return; }

        // A MESH COPY WEARS IT DIRECTLY. Same rule as a part's copy one line down, and the
        // same reason: there is no game package to put geometry into, because the mod ships its
        // own copy of the mesh and the author's file IS that copy's geometry.
        if (_tab is ReplacementTab own)
        {
            own.Mesh.MeshFile = leaf;
            _mod.Save();
            row.Replacement = leaf;
            row.Mark();
            RefillReplacement(own);
            FillFit(own);
            _artVersion++;
            Say($"{Leaf(own.Mesh.Source)}: your copy now carries {leaf}");
            AfterEditsChanged();
            return;
        }

        // A COPY WEARS THE FILE ITSELF. There is no game package to put geometry into: the part
        // this mod ships names the author's mesh directly, which is the whole difference between
        // adding a piece beside the game's and rewriting the game's.
        if (CopyOpen is { } copy)
        {
            // PER PIECE when the garment has pieces, part-wide when it does not.
            if (row.PerPiece) PieceOf(copy, row).MeshFile = leaf;
            else copy.MeshFile = leaf;

            _mod.Save();
            row.Replacement = leaf;
            row.Mark();
            RefillCopyMeshRows(PartTabOpen);
            if (PartTabOpen is { } pt) FillFit(pt);
            _artVersion++;
            Say($"{copy.Id}: your copy now wears {leaf}");
            AfterEditsChanged();
            return;
        }

        var ov = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(row.GamePath, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = row.GamePath }; _mod.Overrides.Add(ov); }
        ov.MeshFile = leaf;
        _mod.Save();

        row.Replacement = leaf;
        // The project is already written, so this is a change FROM THE GAME rather than one
        // waiting on Save. The save bar counts what Save would write.
        row.Mark();
        BuildModifiedSet();
        _artVersion++;
        Say($"{row.Variant}: {row.GameLeaf} now uses {leaf} — the build checks it fits, per LOD");
        AfterEditsChanged();
    }

    /// <summary>
    /// Put one mesh row back to the game's own geometry.
    ///
    /// The same act as right-clicking the asset in the Data Explorer and choosing Revert, put
    /// where the change was made — and it goes through the SAME RevertPackage, so there is one
    /// answer to what reverting means rather than two that can drift. The imported FBX stays in
    /// ModSource: another override may point at it, and deleting a shared file to tidy up after
    /// one of them breaks the others silently.
    /// </summary>
    /// <summary>
    /// Put a MESH COPY's geometry back to the game's.
    ///
    /// Its own method rather than a branch of the part one, because there is nothing else it can
    /// mean: a mesh copy has no re-point to drop and no override to remove. Emptying the file is
    /// the whole of it, and the entry stays — the entry is what makes the mod's copy exist, and
    /// removing it would take the paint with it.
    /// </summary>
    public void RevertReplacementRow(ReplacementTab tab, SheetTab.MeshRow row)
    {
        if (_mod is null) return;

        if (tab.Mesh.MeshFile.Length == 0)
        { Say($"{row.GameLeaf}: your copy already carries the game's own geometry"); return; }

        var was = tab.Mesh.MeshFile;
        tab.Mesh.MeshFile = "";
        _mod.Save();

        row.Replacement = "";
        row.Mark();
        RefillReplacement(tab);
        _artVersion++;
        Say($"{row.GameLeaf}: back to the game's geometry — {was} is still in ModSource");
        AfterEditsChanged();
    }

    public void RevertMeshRow(PartTab tab, SheetTab.MeshRow row)
    {
        if (_mod is null) return;

        // A COPY UNDOES THE OPPOSITE OF WHAT IT SET. Import and Pick both write one field on the
        // part this mod ships — there is no override to remove and no re-point to drop — so
        // taking the override route for a copy would report "this mod does nothing to it" about
        // a row plainly showing the author's own mesh.
        if (tab.Copy is { } copy)
        {
            // PER PIECE, which is what this refused to do until now: "putting one back is
            // per-piece, which is not in this panel yet". The entry GOES rather than being
            // emptied — an entry naming neither a mesh nor a file is one more thing for
            // PlanRegions to match and report as unmatched.
            if (row.PerPiece)
            {
                var piece = copy.Regions.FirstOrDefault(
                    r => r.Region.Equals(row.Region, StringComparison.OrdinalIgnoreCase));
                if (piece is null)
                { Say($"{row.Region}: it wears the game's own {row.GameLeaf}"); return; }

                copy.Regions.Remove(piece);
                _mod.Save();
                row.Replacement = "";
                row.Picked = "";
                row.Mark();
                RefillCopyMeshRows(tab);
                FillFit(tab);
                _artVersion++;
                Say($"{copy.Id}: its {row.Region} is the game's own {row.GameLeaf} again");
                AfterEditsChanged();
                return;
            }

            var had = copy.MeshFile.Length > 0 ||
                      !PackageOf(copy.Mesh).Equals(row.GamePath, StringComparison.OrdinalIgnoreCase);
            if (!had) { Say($"{row.Variant}: your copy wears the game's own {row.GameLeaf}"); return; }

            copy.MeshFile = "";
            copy.Mesh = row.GamePath;
            _mod.Save();

            row.Replacement = "";
            row.Picked = "";
            row.Mark();
            RefillCopyMeshRows(tab);
            _artVersion++;
            Say($"{copy.Id}: back to the game's {row.GameLeaf}");
            AfterEditsChanged();
            return;
        }

        // TWO KINDS OF CHANGE LIVE IN TWO PLACES, and Revert has to take whichever is there.
        //
        // An IMPORT is an override on the MESH package — the geometry goes into that asset. A
        // PICK is a re-point on the PART's own override, because it is the part's reference that
        // moves. Reverting only the first left a re-pointed row saying "this mod does nothing to
        // it" while the re-point sat in the project untouched: a Revert button that reports
        // success and changes nothing.
        var undid = false;

        if (row.Replacement.Length > 0)
            undid |= RevertPackage(row.GamePath, row.GameLeaf);

        var fromObject = Leaf(row.GamePath);
        if (_mod.Overrides.FirstOrDefault(
                o => o.Target.Equals(tab.Entry.Part.Path, StringComparison.OrdinalIgnoreCase))
            is { } partOv &&
            partOv.Repoints.RemoveAll(
                r => r.From.Equals(row.GamePath, StringComparison.OrdinalIgnoreCase) ||
                     r.From.Equals(fromObject, StringComparison.Ordinal)) > 0)
        {
            // The part's override may hold nothing else; leaving an empty one ships a
            // byte-for-byte rewrite of a game package that changes nothing and conflicts with
            // every other mod touching that part.
            if (!partOv.HasEdit) _mod.Overrides.Remove(partOv);
            _mod.Save();
            BuildModifiedSet();
            Say($"{row.Variant}: back to {row.GameLeaf}");
            undid = true;
        }

        if (!undid) { Say($"{row.Variant}: this mod does nothing to it"); return; }

        row.Replacement = "";
        row.Picked = "";
        row.Mark();
        _artVersion++;
        AfterEditsChanged();
    }

    /// <summary>
    /// Open one of a part's meshes in the 3D viewport — the game's, or the author's own file.
    ///
    /// THE RIG COMES FROM THE GAME MESH EITHER WAY. An FBX names its bones; a render vertex needs
    /// their positions, and only the mesh being replaced can supply that mapping — which is the
    /// same mapping the build makes, so a part that animates here animates in game. Built from
    /// the converted reference skeleton rather than from a whole RenderMesh, because the bones
    /// are all that is wanted and the rest would be a GPU upload thrown away.
    /// </summary>
    public async Task ViewMeshRowAsync(SheetTab.MeshRow row)
    {
        if (_provider is null) { Say("the game is not open yet"); return; }

        var provider = _provider;
        var gamePkg = row.GamePath;
        var file = row.Replacement.Length > 0 ? ModBuild.FindImage(row.Replacement, _modPath) : null;

        // ALREADY OPEN? Bring it forward and refresh it rather than stacking tabs on one mesh.
        var open = Tabs.OfType<MeshTab>().FirstOrDefault(t => t.MeshKey == gamePkg);
        if (open is not null) { Tab = open; open.View.Refresh(); return; }

        var title = row.GameLeaf + (file is null ? "" : "  (your import)");
        var tab = new MeshTab(title, ContainerPathOf(gamePkg), "SkeletalMesh") { MeshKey = gamePkg };
        tab.PropsFilled = ApplyModEdits;
        tab.View.Trace = Say;
        if (Swatches.Count > 0) tab.SetSwatches(Swatches);
        tab.View.Loaded += () => Dispatcher.UIThread.Post(() => _ = FillAnimationsAsync(tab));
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        if (file is null || !File.Exists(file)) { await tab.LoadAsync(provider); return; }

        var rig = await Task.Run<Skeleton?>(() =>
        {
            try
            {
                return AssetLoader.Load<USkeletalMesh>(provider, ContainerPathOf(gamePkg))
                       is { } sk && sk.TryConvert(out var converted) && converted.RefSkeleton.Count > 0
                    ? new Skeleton(converted.RefSkeleton)
                    : null;
            }
            catch (Exception) { return null; }
        });

        // NAMED WHEN THERE IS NO RIG, because the file then draws at rest and looks like an
        // import whose weights were dropped — which is a different fault entirely.
        if (rig is null) Say($"{row.GameLeaf}: could not read the game rig, so it draws at rest");
        tab.View.ShowFbx(file, gamePkg, rig);
        Say($"showing {System.IO.Path.GetFileName(file)} on {row.GameLeaf}'s rig");
    }

    /// <summary>A package name back to the file path the containers key by.</summary>
    private static string ContainerPathOf(string package)
    {
        try { return ModBuild.ContainerPathFor(package); }
        catch (Exception) { return package; }
    }

    /// <summary>
    /// THE FOUR COLOUR SWATCHES OF THIS PART'S REGION, and which the menu offers.
    ///
    /// Read from the GAME, then whatever the mod has already said about them laid on top — so
    /// opening the editor twice does not start over from the game's answer and quietly undo the
    /// first edit, which is the same rule the name and description already follow.
    ///
    /// Only for a target the catalogue actually read; an override aimed at a table or a weapon
    /// grows no empty heading.
    /// </summary>
    private void FillSwatches(PartTab tab, string package, ModOverride? ov)
    {
        tab.SetSwatches([]);
        tab.HasOwnSlotLock = false;
        tab.EditLocksOwnSlot = ov?.KeepsOwnSlotLock ?? true;
        if (_provider is null) return;

        try
        {
            var read = TintSwatches.Read(_provider, package);
            if (read.Why is not null || read.Region.Length == 0) return;

            var rows = read.Swatches.Select(s =>
            {
                // The mod's own answer wins where it has one: HidesFromMenu stores a FALSE for a
                // swatch handed back, exactly as ModPart does it.
                var offered = ov is not null && ov.HidesFromMenu.TryGetValue(s.Tag, out var hidden)
                    ? !hidden
                    : s.Offered;
                return new SheetTab.SwatchRow(s.Tag, s.Name, s.Parameter, offered,
                                              s.Live, s.Known, s.Offered);
            });
            tab.SetSwatches(rows);

            tab.HasOwnSlotLock = PartDetails.Read(_provider, package).LocksOwnSlot;
        }
        catch (Exception ex) { Say($"could not read the colour swatches: {ex.Message}"); }
    }

    /// <summary>
    /// Write what the edit panel holds into THE COPY this mod ships.
    ///
    /// The same fields, a different destination. A duplicate needs no "is this empty, remove it"
    /// question — it is a part the mod ships on purpose, and a copy with the game's own name and
    /// nothing changed is still a copy somebody asked for.
    /// </summary>
    private bool SaveCopyEdit(PartTab tab, ModPart copy)
    {
        if (_mod is null) return false;

        var name = tab.EditName.Trim();
        if (name.Length > 0) copy.Display = name;
        copy.KeepsOwnSlotLock = tab.EditLocksOwnSlot;

        // AND WHAT IT SAYS ABOUT ITSELF. The box was filled from the template and never read
        // back out, so typing a description into a copy saved nothing and said "your copy saved"
        // — the fourth time this panel has had a field it draws, accepts and drops. Written only
        // when it DIFFERS from the template's, because a copy repeating its template's words
        // needs no entry to do that.
        var said = tab.EditDescription.Trim();
        if (said.Length > 0 &&
            !string.Equals(said, (tab.Entry.Detail.ShortDescription ?? "").Trim(),
                           StringComparison.Ordinal))
            copy.Text["ShortDescription"] = said;
        else
            copy.Text.Remove("ShortDescription");

        // The part-level mesh field, for a copy. A duplicate wears ONE mesh, so the box that
        // appears when a part carries no body-type rows is the same box here — and leaving it
        // out meant typing a file name into it saved nothing, which is the shape of fault this
        // panel has already paid for three times.
        if (tab.EditMesh.Trim().Length > 0) copy.MeshFile = tab.EditMesh.Trim();

        // WHERE IT GOES.
        if (tab.EditSlot.Length > 0) copy.Slot = tab.EditSlot;
        // Only where the box is DRAWN. A copy moved out of a slot that can hide the head keeps
        // its tick in the editor (so moving it back finds it) but must not write it: a hidden
        // box deciding what the build does is the fault this guards against, and it is also how
        // a Head copy saved with the tick comes clean the next time it is edited.
        copy.HidesHead = tab.ShowsHidesHead && tab.EditHidesHead;
        copy.ForCharacter = tab.EditForCharacter;

        // ONLY THE OFF STATE IS STORED: a tick means "as the template has it", which is exactly
        // what no entry means. Writing every claim would fill a project with entries that change
        // nothing and make a diff unreadable — the same rule the colours follow.
        copy.TakesOver.Clear();
        foreach (var t in tab.Takeovers.Where(t => !t.Kept))
            copy.TakesOver[t.Tag] = false;

        // TAGS COME OFF THE TICKS NOW, not off a text box. Only what is UNTICKED is stored,
        // which is the same "write down the difference" rule the colours and takeovers follow.
        copy.RemoveTags.Clear();
        copy.RemoveTags.AddRange(tab.Tags.Where(t => !t.Kept).Select(t => t.Tag));

        if (tab.EditId.Trim().Length > 0) copy.Id = tab.EditId.Trim();
        copy.AnimClass = tab.EditAnimClass.Trim();

        copy.Abilities.Clear();
        foreach (var swap in tab.Abilities.Where(x => x.To.Trim().Length > 0))
            copy.Abilities[swap.From] = swap.To.Trim();

        // Same rule as an override's: only what DIFFERS from the game is written down.
        copy.HidesFromMenu.Clear();
        foreach (var s in tab.Swatches.Where(s => s.Offered != s.GameOffered))
            copy.HidesFromMenu[s.Tag] = !s.Offered;

        _mod.Save();

        var wrote = new List<string>();
        if (copy.Display.Length > 0) wrote.Add($"called “{copy.Display}”");
        if (copy.MeshFile.Length > 0) wrote.Add($"the mesh {copy.MeshFile}");
        if (copy.Icon.Length > 0) wrote.Add($"the picture {Leaf(copy.Icon)}");
        if (copy.Text.ContainsKey("ShortDescription")) wrote.Add("its own description");
        if (copy.HidesFromMenu.Count > 0)
            wrote.Add(copy.HidesFromMenu.Count == 1 ? "one colour" : $"{copy.HidesFromMenu.Count} colours");
        if (copy.RemoveTags.Count > 0) wrote.Add($"{copy.RemoveTags.Count} tag(s) taken off");
        if (copy.Abilities.Count > 0) wrote.Add($"{copy.Abilities.Count} ability swap(s)");
        if (copy.AnimClass.Length > 0) wrote.Add("its anim class");
        if (copy.TakesOver.Count > 0) wrote.Add($"{copy.TakesOver.Count} takeover(s) off");
        if (!copy.KeepsOwnSlotLock) wrote.Add("its slot lock");

        // The header and the tab carry the copy's name and its asset id, and Save is where both
        // can move — renaming a copy and watching the tab keep the old name reads as a rename
        // that did not take.
        tab.CopyRenamed();

        // AND THE BAR GOES QUIET. It was still counting what had just been written, which is a
        // save button reporting success beside a bar saying nothing was saved.
        tab.MarkSaved();

        Say($"{copy.Id}: your copy saved — " + (wrote.Count > 0 ? string.Join(", ", wrote) : "as it was"));
        AfterEditsChanged();
        return true;
    }

    /// <summary>Write what the edit panel holds into an override, or remove one left empty.</summary>
    public bool SavePartEdit(PartTab tab)
    {
        if (_mod is null) return false;

        // A COPY IS SAVED SOMEWHERE ELSE. Everything above this line writes a ModOverride, which
        // rewrites the GAME's package; a duplicate is a part this mod ships beside it.
        if (tab.Copy is { } copy) return SaveCopyEdit(tab, copy);

        var e = tab.Entry;

        var name = tab.EditName.Trim();
        var text = tab.EditDescription.Trim();

        var sameName = string.Equals(name, e.Label, StringComparison.Ordinal);
        var sameText = string.Equals(text, (e.Detail.ShortDescription ?? "").Trim(),
                                     StringComparison.Ordinal);

        var ov = _mod.Overrides.FirstOrDefault(
            o => string.Equals(o.Target, e.Part.Path, StringComparison.OrdinalIgnoreCase));

        // WRITE FIRST, THEN ASK WHETHER IT SAYS ANYTHING.
        //
        // There used to be a gate here that returned "nothing changed" unless the NAME, the
        // DESCRIPTION, the mesh or the icon had moved. Four of the panel's sections write
        // nothing else — the colours, the tags, the slot lock and the size rule — so handing a
        // swatch back, editing who a part is offered to, or unticking its slot lock all saved
        // NOTHING and said so in a sentence that read like success. That is why "the swatch save
        // is drawn but never witnessed writing to a project" stayed unexplained for days.
        //
        // The same gate's removal branch listed Repoints, Numbers, AddRows, MeshFile and Image
        // and no others, so clearing a name from a part that carried TAGS deleted the tags with
        // the override.
        //
        // Asking ModOverride.HasEdit once the override has been written is the version that
        // cannot drift: it is Core's own answer to "does this ask for anything", maintained
        // beside the fields it lists. Icon is not in it on purpose — an icon's pixels travel as
        // a texture override and a part override carrying only the RECORD of one asks the build
        // for nothing — so it is tested separately rather than pushed into Core's meaning.
        var invented = ov is null;
        if (ov is null) { ov = new ModOverride { Target = e.Part.Path }; _mod.Overrides.Add(ov); }
        ov.DisplayName = sameName ? "" : name;
        ov.Description = sameText ? "" : text;
        ov.MeshFile = tab.EditMesh.Trim();

        // ITS OWN FIELD NOW. ov.Image is the asset's own PIXELS and ModBuild reads it in exactly
        // one place, the texture branch — so an icon written there made the build ask whether a
        // PART was a texture, get no, and `continue`, silently dropping this whole override: the
        // name and the description and the mesh with it.
        ov.Icon = tab.EditIcon.Trim();
        ov.MeshAnySize = tab.EditAnySize;
        ov.KeepsOwnSlotLock = tab.EditLocksOwnSlot;

        // TAGS, one per line. Cleared and rewritten rather than merged: the box IS the list, so
        // deleting a line has to delete the tag or the panel and the project disagree.
        // SPLIT ON BOTH, because a text box on Windows gives CRLF and a project written on
        // another machine may not. Trimmed, so a stray carriage return never becomes part of a
        // gameplay tag — a tag with an invisible character on the end matches nothing and looks
        // exactly like a tag that should have worked.
        static IEnumerable<string> Lines(string s) =>
            s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
             .Select(x => x.Trim())
             .Where(x => x.Length > 0);
        // OFF THE TICKS, for the same reason a copy's are: the tags a part carries are read from
        // it, and typing a fifty-character dotted namespace from memory is not something to ask
        // of anybody. The Add box below still takes tags the part does NOT carry.
        ov.RemoveTags.Clear();
        ov.RemoveTags.AddRange(tab.Tags.Where(t => !t.Kept).Select(t => t.Tag));
        ov.AddTags.Clear(); ov.AddTags.AddRange(Lines(tab.EditAddTags));

        // A SWATCH HANDED BACK IS STORED AS FALSE, and one the game already offers is not stored
        // at all — writing every swatch would fill the project with entries that change nothing
        // and make a diff unreadable.
        ov.HidesFromMenu.Clear();
        foreach (var s in tab.Swatches.Where(s => s.Offered != s.GameOffered))
            ov.HidesFromMenu[s.Tag] = !s.Offered;

        // NOW ask it. An override that asks for nothing would ship a byte-for-byte rewrite of a
        // game package and conflict with every other mod touching that part.
        if (!ov.HasEdit && ov.Icon.Length == 0)
        {
            _mod.Overrides.Remove(ov);

            // One invented a moment ago and immediately empty was never a change at all; one
            // that already existed has just been emptied, which IS a change worth reporting.
            if (invented) { Say($"{e.Label}: nothing changed"); return false; }

            _mod.Save();
            tab.ModNote = "";
            Say($"{e.Label}: back to the game's own, override removed");
            AfterEditsChanged();
            return true;
        }

        _mod.Save();

        // AND THE ICON IS AN OVERRIDE OF THE TEXTURE THE PART POINTS AT. That is the mechanism
        // this build already has for artwork on a game asset, and the field above only records
        // what was chosen so the editor can show it back.
        if (ov.Icon.Length > 0)
        {
            var iconPkg = IconTextureOf(e.Part.Path);
            if (iconPkg is null)
                Say($"{e.Label}: this part names its picture by bank tag rather than a texture " +
                    "of its own, so there is nothing to import over — the icon was not applied");
            else
            {
                var file = ModBuild.FindImage(ov.Icon, _modPath) ?? ov.Icon;
                if (!File.Exists(file)) Say($"{e.Label}: cannot find the image '{ov.Icon}'");
                else RecordTextureOverride(iconPkg, file, $"{e.Label}'s icon", "Icons");
            }
        }

        NoteMod(tab, ov);

        // WHAT WAS ACTUALLY WRITTEN, not a sentence about the name. It said "now reads <name>"
        // whatever had been saved, so a colours-only save reported a rename that had not
        // happened — and, while the gate above was dropping those saves entirely, it never got
        // the chance to be wrong out loud.
        var wrote = new List<string>();
        if (ov.DisplayName.Length > 0) wrote.Add($"called “{ov.DisplayName}”");
        if (ov.Description.Length > 0) wrote.Add("its description");
        if (ov.MeshFile.Length > 0) wrote.Add($"the mesh {ov.MeshFile}");
        if (ov.Icon.Length > 0) wrote.Add($"the picture {ov.Icon}");
        if (ov.HidesFromMenu.Count > 0)
            wrote.Add(ov.HidesFromMenu.Count == 1 ? "one colour" : $"{ov.HidesFromMenu.Count} colours");
        if (ov.RemoveTags.Count + ov.AddTags.Count > 0) wrote.Add("its tags");
        if (!ov.KeepsOwnSlotLock) wrote.Add("its slot lock");

        tab.MarkSaved();
        Say($"{e.Label}: saved — " + (wrote.Count > 0 ? string.Join(", ", wrote) : "an override"));
        AfterEditsChanged();
        return true;
    }

    /// <summary>
    /// TAKE A FILE INTO THE MOD'S OWN SOURCE FOLDER, and hand back the name to store.
    ///
    /// ModBuild.FindImage looks in `&lt;mod folder&gt;/ModSource/&lt;project&gt;/&lt;kind&gt;/`,
    /// so a bare file name is the portable form — the project keeps building when it is moved or
    /// shared. Storing the path the picker returned would work today on this machine and nowhere
    /// else, which is precisely the state two of his projects had to be migrated out of: they
    /// pointed at a Desktop folder, and the fix had to rewrite fourteen references.
    ///
    /// So the file is COPIED IN rather than referenced where it lies. Copying somebody's art into
    /// their own project folder is what "import" means here, and it is the difference between a
    /// mod that survives being sent to somebody else and one that does not.
    /// </summary>
    public string? ImportArt(string sourceFile, string kind, out string? why)
    {
        why = null;
        if (_mod is null) { why = "no mod is open"; return null; }
        if (!File.Exists(sourceFile)) { why = $"{sourceFile} is not there"; return null; }

        try
        {
            var modDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_modPath)) ?? ".";
            var project = System.IO.Path.GetFileNameWithoutExtension(_modPath);
            var home = System.IO.Path.Combine(modDir, "ModSource", project, kind);
            var leaf = System.IO.Path.GetFileName(sourceFile);
            var landing = System.IO.Path.Combine(home, leaf);

            // ALREADY THERE AND THE SAME FILE? Nothing to copy, and copying a file over itself
            // throws rather than doing nothing.
            if (System.IO.Path.GetFullPath(sourceFile).Equals(System.IO.Path.GetFullPath(landing),
                    StringComparison.OrdinalIgnoreCase))
                return leaf;

            // ALREADY SOMEWHERE IN THIS MOD'S OWN SOURCE? Also nothing to copy.
            //
            // "Use…" hands back a file that is already in the mod, and it may have arrived under
            // Icons while this call names the kind Textures — which left TWO copies of one image
            // under one name, and an author who edits one of them then cannot tell which the
            // build reads. FindImage searches all three folders, so one copy is enough for
            // anything that resolves it later.
            var mine = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(modDir, "ModSource", project)) +
                System.IO.Path.DirectorySeparatorChar;
            if (System.IO.Path.GetFullPath(sourceFile)
                    .StartsWith(mine, StringComparison.OrdinalIgnoreCase))
                return leaf;

            Directory.CreateDirectory(home);

            // NAMED, NOT OVERWRITTEN SILENTLY. Two different files under one name is how a helmet
            // ends up wearing somebody else's artwork with nothing anywhere saying why.
            if (File.Exists(landing))
            {
                why = $"{kind}/{leaf} already exists in this mod — rename the file you are importing";
                return null;
            }

            File.Copy(sourceFile, landing);
            Say($"imported {leaf} into {project}/{kind}");
            return leaf;
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    /// <summary>Say on the sheet what this mod changes about the part it is showing.</summary>
    private void NoteMod(SheetTab tab, ModOverride? ov)
    {
        if (ov is null) { tab.ModNote = ""; return; }

        var bits = new List<string>();
        if (ov.DisplayName.Length > 0) bits.Add($"renames it to “{ov.DisplayName}”");
        if (ov.Description.Length > 0) bits.Add("changes its description");
        if (ov.MeshFile.Length > 0) bits.Add("replaces its mesh");
        if (ov.Image.Length > 0) bits.Add("replaces its icon");
        if (ov.Numbers.Count > 0) bits.Add($"changes {ov.Numbers.Count} number(s)");

        tab.ModNote = bits.Count == 0
            ? $"{ModTitle} overrides this part"
            : $"{ModTitle} " + string.Join(", ", bits);
    }

    // ------------------------------------------------------------------ the tabs

    /// <summary>
    /// The open tabs, in the strip across the top.
    ///
    /// A COLLECTION ON THE VIEW MODEL, not TabItems in the XAML, because the guided editors have
    /// to be able to open in this same strip later. He wants both routes — "I still wanna make it
    /// easy to mod like our mod studio, but also have stuff like this if people want to do more
    /// advanced types of modding" — and a strip that can only hold assets would send the feature
    /// editors back to a permanent row of buttons.
    /// </summary>
    public ObservableCollection<StudioTab> Tabs { get; } = new();

    public StudioTab? Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            Raise(nameof(HasTab));

            // WHICH TAB THE STRIP LIGHTS. Here rather than at the call sites, because a tab
            // becomes the open one by half a dozen routes -- opened from the explorer, opened
            // from a section, closed so its neighbour takes over -- and a flag maintained at
            // each of those is a flag that is wrong at whichever one gets added next.
            foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, value);

            // COMING BACK TO A VIEWPORT REBUILDS IT, if anything has been imported since it was
            // last drawn. That is the whole round trip: look at an imported model, tab back to
            // the part, import its textures, tab back here and they are ON it. Without this the
            // viewport keeps the textures it bound the first time and shows yesterday's answer.
            //
            // GATED ON A VERSION rather than refreshing on every click: a rebuild re-uploads the
            // geometry, and doing that each time somebody flicks between tabs would make moving
            // around cost as much as opening something.
            if (_tab is MeshTab mt && mt.ArtVersion != _artVersion)
            {
                mt.ArtVersion = _artVersion;
                mt.View.Refresh();
            }
        }
    }

    /// <summary>
    /// Bumped whenever this mod's ARTWORK changes — an imported mesh, icon or texture.
    ///
    /// A number rather than a flag per tab, so a viewport opened long ago and one opened a
    /// moment ago are judged against the same fact.
    /// </summary>
    private int _artVersion;

    /// <summary>The open tab when it is an asset, for the things that only asset tabs can do.</summary>
    /// <summary>
    /// The property grid an edit belongs to — including one EMBEDDED in another tab.
    ///
    /// It used to be `_tab as AssetTab` and nothing else, so the property list inside the model
    /// viewport and the one in a part's editor were both read-only by accident: double-clicking
    /// a value opened the box, typing a number found no tab to apply it to, and nothing was said.
    /// A grid that is shown is a grid that can be edited, wherever it is shown.
    /// </summary>
    public AssetTab? AssetTabOpen => _tab switch
    {
        AssetTab a => a,
        MeshTab m => m.Props,
        PartTab p => p.Props,
        _ => null,
    };

    public bool HasTab => _tab is not null;
    public bool AnyTabs => Tabs.Count > 0;

    /// <summary>
    /// Open an asset in a tab, reading its properties OFF THE UI THREAD.
    ///
    /// Off the thread for two reasons and both are real: a package read is slow enough to be felt,
    /// and CUE4Parse's cache is not safe to race on — the failure there is an
    /// AccessViolationException from native code that .NET will not let anyone catch, and it has
    /// taken this project down twice. AssetProps.Read holds the one gate; this just must not be
    /// the thing that blocks the window while it waits.
    /// </summary>
    public async Task OpenAssetAsync(AssetRow row)
    {
        // A COPY THIS MOD SHIPS OPENS ITS EDITOR. It is not a package on disk and never will be
        // until a build runs, but it is the thing somebody most wants to change — and until now
        // double-clicking one said "there is nothing built to read yet", which is true of the
        // FILE and useless about the part. Every project the old studio made is a list of these.
        if (row.Payload is ModPart copy) { await EditCopy(copy); return; }

        // AND THE OTHER THREE KINDS A PROJECT HOLDS. Every one of these listed under "show only
        // modified" as a row that opened nothing but a sentence saying it had no editor — which
        // was true, and is the fault this pair of editors closes. A spec has had one for days
        // and was never wired to the list.
        if (row.Payload is ModCopy made) { await EditGenericCopy(made); return; }
        if (row.Payload is ModMesh own) { await EditReplacement(own); return; }
        if (row.Payload is ModSpec spec) { await EditSpec(spec); return; }
        if (row.Payload is ModMission ownMission) { await EditMission(ownMission); return; }

        if (row.Payload is not AssetNode file)
        {
            // Another mod item with no editor of its own yet. Saying so beats a tab that opens
            // empty, and naming what it is beats saying "this mod's own item".
            Say($"{row.Name} is this mod's own {row.Type.ToLowerInvariant()} — " +
                "it has no editor here yet");
            return;
        }

        if (_provider is null) { Say("the game is not open yet"); return; }

        // ALREADY OPEN? Bring it forward rather than reading it a second time. Two
        // tabs on one asset is two places an edit could later disagree.
        if (Tabs.OfType<AssetTab>().FirstOrDefault(t => t.FilePath == file.FullPath) is { } already)
        { Tab = already; return; }

        // A MESH OPENS THE VIEWPORT, not a property list. It is what somebody double-clicking a
        // model means, and the properties of a mesh are a page of LOD distances and lightmap
        // settings that answer nothing about whether it is the right model.
        if (file.Kind is AssetKind.SkeletalMesh or AssetKind.StaticMesh)
        {
            if (Tabs.OfType<MeshTab>()
                    .FirstOrDefault(t => t.FilePath == file.FullPath) is { } openMesh)
            { Tab = openMesh; return; }

            var mesh = new MeshTab(System.IO.Path.GetFileNameWithoutExtension(file.Name),
                                   file.FullPath, row.Type);
            mesh.PropsFilled = ApplyModEdits;
            // THE RIG ONLY EXISTS ONCE THE GEOMETRY IS BUILT, which happens on the render thread
            // a frame or more after LoadAsync returns — Show only queues the asset. Asking for
            // the bones straight after the await gets an empty list and an empty animation
            // panel, on a mesh that has one.
            mesh.View.Loaded += () =>
                Dispatcher.UIThread.Post(() => _ = FillAnimationsAsync(mesh));
            // THE VIEWPORT'S DIAGNOSTICS GO TO THE LOG HE CAN SEE. This is a WinExe with no
            // console, so a Console.WriteLine trace is invisible unless a harness redirects
            // stdout — which is why "the log didn't say" was the correct answer and not a
            // missing line.
            mesh.View.Trace = Say;
            if (Swatches.Count > 0) mesh.SetSwatches(Swatches);
            Tabs.Add(mesh);
            Tab = mesh;
            Raise(nameof(AnyTabs));
            Say($"opened {mesh.Title} in the viewport");
            await mesh.LoadAsync(_provider);
            return;
        }

        // A TEXTURE OPENS THE 2D VIEWER, for the same reason a mesh opens the 3D one: it is what
        // somebody double-clicking an image means. A texture's properties are compression
        // settings and an address mode, and none of them answer "is this the right artwork" —
        // whereas the channel toggles do, because the masks in this game carry a different
        // meaning per channel.
        if (file.Kind is AssetKind.Texture)
        {
            if (Tabs.OfType<TextureTab>()
                    .FirstOrDefault(t => t.FilePath == file.FullPath) is { } openTex)
            { Tab = openTex; return; }

            var tex = new TextureTab(System.IO.Path.GetFileNameWithoutExtension(file.Name),
                                     file.FullPath, row.Type);
            tex.View.Trace = Say;
            Tabs.Add(tex);
            Tab = tex;
            Raise(nameof(AnyTabs));
            Say($"opened {tex.Title} in the texture viewer");
            await tex.LoadAsync(_provider);
            return;
        }

        var tab = new AssetTab(System.IO.Path.GetFileNameWithoutExtension(file.Name), file.FullPath, row.Type);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        var provider = _provider;
        var read = await Task.Run(() => AssetProps.Read(provider, file.FullPath));
        tab.Fill(read);
        ApplyModEdits(tab);

        Say(read.Error is null
            ? $"opened {tab.Title} — {read.Exports.Count} export(s), {read.PropertyCount:N0} properties" +
              (read.DefaultCount == 0 ? "" : $", {read.DefaultCount:N0} at default")
            : $"could not read {tab.Title}: {read.Error}");
    }

    /// <summary>
    /// Open the best match for a name. For <c>--open</c>, and for anything later that wants to
    /// reveal an asset it only knows by name.
    ///
    /// PrimaryFirst, because an optional `.o.uasset` segment can win the match and
    /// LoadPackage throws on a path that is not a package. Solved once in AssetIndex; not solved
    /// again here.
    /// </summary>
    // ------------------------------------------------------------------ textures

    /// <summary>
    /// Write the open texture out, in the format the bar names.
    ///
    /// TextureExport.Write mirrors the game's folder structure under the chosen directory, so a
    /// dozen exports do not land in one heap with names that collide.
    /// </summary>
    public void ExportTexture(TextureTab tab, string directory)
    {
        if (tab.Texture is null) { Say("that texture has not finished loading"); return; }

        var package = PropertyEdit.PackageForFile(tab.FilePath) ?? tab.Title;
        var (path, error) = TextureExport.Write(tab.Texture, directory, package,
                                                tab.SelectedFormat.Format);

        Say(path is not null
            ? $"exported {tab.Title} as {tab.SelectedFormat.Name} to {path}"
            : $"could not export {tab.Title}: {error}");
    }

    /// <summary>
    /// Put the author's own image over a GAME texture, as an override of that package.
    ///
    /// THE ORDER IS THE WHOLE POINT, exactly as it is for a property edit. The file is not
    /// recorded and hoped for: TextureImport reads the target's real shape and ENCODES the image
    /// to it first, and the project is only touched when that came back clean. A recorded
    /// replacement the build cannot make would sit in the project looking applied, build, install
    /// and change nothing.
    ///
    /// This game is exactly where that matters. A mask whose alpha is zero has had its colour
    /// premultiplied away by a resize, silently, and the only sign was in the game — so a
    /// texture import that does not rehearse is an invitation to ship one.
    ///
    /// ModOverride{Target, Image} IS THE REPRESENTATION, not one invented here: ModBuild already
    /// reads exactly that pair, handles the virtual and the plain case separately because this
    /// game ships both, and BuildModifiedSet already marks an override's target as modified —
    /// which is what makes the file go bold in the Data Explorer with nothing further to wire.
    /// </summary>
    public void ImportTexture(TextureTab tab, string imageFile)
    {
        if (_mod is null) { Say("open a mod first — an import has to belong to one"); return; }
        if (_provider is null) { Say("the game is not open yet"); return; }

        var package = PropertyEdit.PackageForFile(tab.FilePath);
        if (package is null) { Say($"could not work out the package name for {tab.Title}"); return; }

        if (RecordTextureOverride(package, imageFile, tab.Title, "Textures")) AfterEditsChanged();
    }

    /// <summary>
    /// Rehearse an image against one texture package, then record it — the whole of an import.
    ///
    /// SHARED, because a part's icon goes through here too. A part's icon IS a texture the part
    /// points at, and ModBuild's own words are that "a replacement's textures are already
    /// separate overrides that repaint the game's own texture in place, so a copy would be a
    /// second mechanism for a job that has one". Routing the icon here rather than teaching the
    /// build a second way is that rule applied.
    ///
    /// <paramref name="kind"/> is which ModSource folder the file lands in. The build's FindImage
    /// searches them all, so an icon under Icons/ is found exactly as a texture under Textures/.
    /// </summary>
    private bool RecordTextureOverride(string package, string imageFile, string label, string kind)
    {
        if (_mod is null || _provider is null) return false;

        // REHEARSED AGAINST THE REAL TARGET, before anything is written down.
        var isVirtual = VirtualTexture.Describe(_provider, package, out _) is not null;
        string? why;
        if (isVirtual)
        {
            var vt = VirtualTexture.Describe(_provider, package, out why);
            if (vt is null) { Say($"{label}: {why}"); return false; }
            if (VirtualTexture.Author(vt, imageFile, null, out why) is null)
            { Say($"{label}: {why}"); return false; }
        }
        else
        {
            var plain = TextureImport.Describe(_provider, package, out why);
            if (plain is null) { Say($"{label}: {why}"); return false; }
            if (TextureImport.Author(plain, imageFile, null, out why) is null)
            { Say($"{label}: {why}"); return false; }
        }

        // Only now does the file come into the mod's own source folder.
        var leaf = ImportArt(imageFile, kind, out var copyWhy);
        if (leaf is null) { Say($"{label}: {copyWhy}"); return false; }

        var ov = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(package, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = package }; _mod.Overrides.Add(ov); }
        ov.Image = leaf;
        _mod.Save();

        BuildModifiedSet();
        _artVersion++;
        Say($"{label} now uses {leaf} in this mod" + (isVirtual ? " (virtual texture)" : ""));
        return true;
    }

    /// <summary>
    /// The texture a part draws its icon from, or null when it names one by bank tag instead.
    /// </summary>
    private string? IconTextureOf(string partPath)
    {
        if (_provider is null) return null;
        var detail = PartDetails.Read(_provider, partPath);
        if (detail.IconPath is not { Length: > 0 } path) return null;
        // An object path is "/Game/A/T_X.T_X"; the package is everything before the last dot.
        var dot = path.LastIndexOf('.');
        return dot > 0 ? path[..dot] : path;
    }

    /// <summary>
    /// Undo whatever this mod does to one game package.
    ///
    /// THE SAME SHAPE AS RevertPart, and for the same reasons: it removes the whole override
    /// rather than just the one field, it says WHAT it removed, and it says so plainly when the
    /// mod does nothing to that asset instead of pretending to have acted.
    ///
    /// IT DOES NOT DELETE THE IMPORTED FILE from ModSource. Another override can point at the
    /// same image — one repaint reused across several textures is a normal thing to do — and
    /// deleting a shared file to tidy up after one of them would break the others silently.
    /// RevertPart leaves them too.
    /// </summary>
    public bool RevertPackage(string package, string label)
    {
        if (_mod is null) { Say("no mod is open"); return false; }

        var ov = _mod.Overrides.FirstOrDefault(
            o => string.Equals(o.Target, package, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { Say($"{label}: this mod does nothing to it"); return false; }

        var had = ov.Image.Length > 0 ? $"the imported {ov.Image}" : "the changes to it";
        _mod.Overrides.Remove(ov);
        _mod.Save();

        BuildModifiedSet();
        Say($"reverted {label} — removed {had}");
        AfterEditsChanged();
        return true;
    }

    /// <summary>Revert a row of the Data Explorer, which is where the author asked for it.</summary>
    public bool RevertAsset(AssetRow row)
    {
        if (row.Payload is not AssetNode file)
        { Say($"{row.Name} is this mod's own item — there is nothing of the game's to put back"); return false; }

        var package = PropertyEdit.PackageForFile(file.FullPath);
        if (package is null) { Say($"could not work out the package name for {row.Name}"); return false; }

        return RevertPackage(package, row.Name);
    }

    /// <summary>
    /// Load a sequence and play it on the open model.
    ///
    /// READ OFF THE UI THREAD, like every other package read here: CUE4Parse's cache is not safe
    /// to race on and the failure is a native AccessViolationException .NET cannot catch. The
    /// POSE is then built on the render thread, where the bone palette is uploaded.
    /// </summary>
    public async Task PlayAnimationAsync(MeshTab tab, AnimRow row)
    {
        if (_provider is null) { Say("the game is not open yet"); return; }

        var provider = _provider;
        var seq = await Task.Run<UAnimSequence?>(() =>
        {
            try { return AssetLoader.Load<UAnimSequence>(provider, row.Path); }
            catch (Exception) { return null; }
        });

        if (seq is null) { Say($"{row.Name}: could not read that animation"); return; }

        if (tab.View.PlayAnimation(seq, out var why))
        {
            tab.PlayingAnim = seq;
            Say($"playing {row.Name} — {row.Frames} frames");
        }
        else
        {
            // NAMES WHY. A sequence that will not bind to this rig is the normal case worth
            // explaining — the list is built by bone-name compatibility, not by certainty.
            Say($"{row.Name}: {why}");
            tab.SetPlayNote(why);
        }
    }

    // ------------------------------------------------------------------ model export

    /// <summary>
    /// WHICH FORMAT A CHOSEN FILE NAME MEANS.
    ///
    /// Avalonia's save picker does not report which filter the user chose, so the EXTENSION they
    /// ended up with is the answer — which is also the one a person would expect to be obeyed if
    /// they typed ".psk" over a suggested ".fbx".
    /// </summary>
    private static ExportTarget? TargetFor(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".fbx" => ExportTarget.Fbx,
            ".psk" or ".pskx" or ".psa" => ExportTarget.ActorX,
            ".glb" or ".gltf" => ExportTarget.Gltf,
            ".uemodel" or ".ueanim" => ExportTarget.UEFormat,
            _ => null,
        };

    /// <summary>
    /// Write one asset out and put it at the name that was chosen.
    ///
    /// The exporter takes a DIRECTORY and names the file after the asset, mirroring the game's
    /// tree — right for a whole-game dump and not for "save this, here, called that". So it is
    /// pointed at the chosen folder and the file it reports is then moved to the chosen name.
    /// Anything else it wrote beside the model — materials, textures — keeps the names the game
    /// gave them, because those are what the main file REFERS to.
    /// </summary>
    private bool ExportOne(UObject asset, string chosenPath, string label)
    {
        if (TargetFor(chosenPath) is not { } target)
        { Say($"{label}: '{System.IO.Path.GetExtension(chosenPath)}' is not a format this writes"); return false; }

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(chosenPath));
        if (dir is null) { Say($"{label}: that is not a folder"); return false; }

        var result = AssetExporter.Export(asset, new ExportRequest(target, dir));
        if (!result.Success || result.SavedPath is null)
        { Say($"{label}: {result.Error ?? "the exporter wrote nothing"}"); return false; }

        var landed = result.SavedPath;
        try
        {
            if (!string.Equals(System.IO.Path.GetFullPath(landed),
                               System.IO.Path.GetFullPath(chosenPath), StringComparison.OrdinalIgnoreCase))
            {
                // The exporter picks the extension it actually wrote — .pskx for a mesh with more
                // than one UV set, say — so the chosen NAME is honoured and its extension is not
                // forced onto a file of a different kind.
                var want = System.IO.Path.Combine(
                    dir, System.IO.Path.GetFileNameWithoutExtension(chosenPath) +
                         System.IO.Path.GetExtension(landed));
                if (File.Exists(want)) File.Delete(want);

                // RETRY, THEN COPY. CUE4Parse's generic exporter still holds the file when it
                // returns — "the process cannot access the file because it is being used by
                // another process" — so the very first animation export landed in the mirrored
                // tree under its game name instead of the one that was chosen. Our own FBX
                // writer closes cleanly, which is exactly why only the .psa missed and the
                // .fbx did not.
                var moved = false;
                for (var go = 0; go < 12 && !moved; go++)
                {
                    try { File.Move(landed, want); moved = true; }
                    catch (IOException) { Thread.Sleep(80); }
                }
                // A copy succeeds against a handle opened for sharing where a move cannot, and
                // leaving the original behind is better than losing the export.
                if (!moved) { File.Copy(landed, want, true); }

                landed = want;
            }
        }
        catch (Exception e) { Say($"{label}: written to {landed} but could not be renamed — {e.Message}"); }

        Say($"exported {label} to {landed}");
        return true;
    }

    public bool ExportModel(MeshTab tab, string path)
        => tab.Asset is { } a && ExportOne(a, path, tab.Title);

    /// <summary>
    /// The loaded animation, as its own file.
    ///
    /// SEPARATE FROM THE MESH because it is a separate asset and the formats differ: FBX here is
    /// MESHES ONLY, by AssetExporter's own statement, so an animation comes out as ActorX .psa or
    /// UEFormat .ueanim. Offering FBX on this dialog would be offering something that cannot be
    /// written.
    /// </summary>
    public bool ExportAnimation(MeshTab tab, string path)
        => tab.PlayingAnim is { } a && ExportOne(a, path, a.Name);

    /// <summary>
    /// Put the author's geometry over the GAME's own mesh, as an override of that package.
    ///
    /// THE SAME SHAPE AS A TEXTURE IMPORT — the file comes into the mod's source folder and the
    /// override names it — and ModOverride.MeshFile is read by ModBuild, which is where the real
    /// refusals live: every LOD's vertex and index counts have to fit what the package already
    /// declares, and a weight on a bone the section cannot address is dropped.
    ///
    /// NOT REHEARSED HERE, and that is a difference from the texture path worth being plain
    /// about. A texture rehearsal is an encode of one image; a mesh rehearsal is the importer's
    /// whole per-LOD fit against the cooked package, which is the build. So the build is where
    /// it is checked, and it says so per LOD rather than silently shipping something wrong.
    /// </summary>
    public void ImportModel(MeshTab tab, string file)
    {
        if (_mod is null) { Say("open a mod first — an import has to belong to one"); return; }

        var package = PropertyEdit.PackageForFile(tab.FilePath);
        if (package is null) { Say($"could not work out the package name for {tab.Title}"); return; }

        var leaf = ImportArt(file, "Meshes", out var why);
        if (leaf is null) { Say($"{tab.Title}: {why}"); return; }

        var ov = _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(package, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = package }; _mod.Overrides.Add(ov); }
        ov.MeshFile = leaf;
        _mod.Save();

        BuildModifiedSet();
        _artVersion++;
        Say($"{tab.Title} now uses {leaf} in this mod — the build checks it fits, per LOD");
        AfterEditsChanged();
    }

    // ------------------------------------------------------------------ tint palette

    /// <summary>
    /// The game's 456 colours, read once. See <see cref="TintPalette"/> for why they are parts
    /// rather than a palette asset.
    /// </summary>
    public List<TintPalette.Swatch> Swatches { get; private set; } = [];

    /// <summary>
    /// Read the palette in the background, then hand it to any model tab already open.
    ///
    /// A PACKAGE LOAD PER COLOUR, so not on the way to showing a window. Nothing waits for it:
    /// a viewport opened first gets its picker filled when it arrives, the same bargain the
    /// animation index makes.
    /// </summary>
    private void StartTintPalette(IFileProvider provider, AssetIndex index)
    {
        if (Swatches.Count > 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                var read = TintPalette.Read(provider, index);
                OnUi(() =>
                {
                    Swatches = read;
                    Raise(nameof(Swatches));
                    foreach (var t in Tabs.OfType<MeshTab>().ToArray()) t.SetSwatches(read);
                });
                Say($"tint palette: {read.Count} of the game's colours");
            }
            catch (Exception e) { Say($"tint palette failed: {e.Message}"); }
        });
    }

    // ------------------------------------------------------------------ animations

    /// <summary>
    /// Every animation sequence in the game, indexed by the skeleton it targets.
    ///
    /// Kept here rather than on the tab because it is one index over the whole game, it takes a
    /// while to build, and every mesh tab opened afterwards reads the same one.
    /// </summary>
    public AnimationLibrary Animations { get; } = new();

    private string _animLibStatus = "not started";
    public string AnimLibStatus { get => _animLibStatus; private set => Set(ref _animLibStatus, value); }

    /// <summary>
    /// Load the animation index from cache, or build it once in the background.
    ///
    /// THE CACHE IS THE NORMAL PATH and the build is the exception, because building loads every
    /// AnimSequence package in the game — thousands of them — while loading is a single JSON
    /// read. The signature covers the profile, the file count and the Core build id, so a game
    /// patch or a change to the resolution rules invalidates it rather than quietly serving
    /// results from the old ones.
    ///
    /// BACKGROUND AND NEVER AWAITED. Nothing on screen depends on it: a mesh tab opened before
    /// it finishes says so and fills in when it arrives, which is the same bargain the parts
    /// catalogue already makes.
    /// </summary>
    private void StartAnimationLibrary(IFileProvider provider, AssetIndex index, GameProfile profile)
    {
        if (Animations.IsBuilt) return;

        _ = Task.Run(() =>
        {
            try
            {
                var signature = LibraryCache.Signature(profile, index.FileCount);
                if (LibraryCache.TryLoad(GameMount.DataDir, signature, out var anims, out var chars)
                    && anims.Count > 0)
                {
                    Animations.LoadFrom(anims);
                    OnUi(() => AnimLibStatus = $"{anims.Count:N0} animations");
                    Say($"animation index loaded from cache: {anims.Count:N0} sequences");
                    RefillOpenMeshTabs();
                    return;
                }

                // SAY IT IS HAPPENING AND ROUGHLY WHAT IT COSTS, before it starts. A minutes-long
                // scan that announces itself only when it finishes is indistinguishable from the
                // feature being broken.
                OnUi(() => AnimLibStatus = "indexing…");
                Say("animation index: reading every sequence in the game, a few minutes. " +
                    "It is cached afterwards, and nothing else waits for it.");
                var started = DateTime.Now;

                Animations.Build(provider, index, Say);
                LibraryCache.Save(GameMount.DataDir, signature, Animations.All, chars);

                OnUi(() => AnimLibStatus = $"{Animations.Count:N0} animations");
                Say($"animation index built in {(DateTime.Now - started).TotalMinutes:0.#} min and cached");
                RefillOpenMeshTabs();
            }
            catch (Exception e)
            {
                OnUi(() => AnimLibStatus = "unavailable");
                Say($"animation index failed: {e.Message}");
            }
        });
    }

    /// <summary>
    /// Fill in any mesh tab that was opened BEFORE the index finished.
    ///
    /// Without this, opening a model during the first run leaves its panel reading "indexing..."
    /// for the life of the tab, and the only way to see the list is to close and reopen it — a
    /// feature that works but appears not to.
    /// </summary>
    private void RefillOpenMeshTabs()
        => Dispatcher.UIThread.Post(() =>
        {
            foreach (var t in Tabs.OfType<MeshTab>().ToArray()) _ = FillAnimationsAsync(t);
        });

    /// <summary>
    /// The animations a loaded mesh can actually play, found BY BONE NAME.
    ///
    /// NOT `ForSkeleton(mesh.Skeleton)`, which is the obvious call and silently returns nothing:
    /// a wearable part's own skeleton carries no sequences in this game. RigCompatibility asks
    /// which of the skeletons animations DO target share this rig's bones, which is the same
    /// question playback answers when it maps tracks by name.
    /// </summary>
    public async Task FillAnimationsAsync(MeshTab tab)
    {
        if (_provider is null) return;

        var bones = tab.View.RigBones;
        if (bones.Count == 0) { tab.SetAnimations([], "this mesh has no rig"); return; }

        if (!Animations.IsBuilt)
        { tab.SetAnimations([], $"animation index {AnimLibStatus}"); return; }


        var provider = _provider;
        var candidates = Animations.SkeletonPaths.ToArray();

        var (rows, note) = await Task.Run(() =>
        {
            // Loads a few dozen USkeletons, not thousands of sequences — which is the whole
            // reason matching by bone name is affordable.
            var fits = RigCompatibility.For(provider, candidates, bones);
            if (fits.Count == 0)
                return (new List<(string Category, string Key, string Action, List<AnimRow> Rows)>(),
                        $"no rig in the game matches these {bones.Count} bones");

            var found = new List<AnimRow>();
            foreach (var fit in fits)
                foreach (var a in Animations.ForSkeleton(fit.SkeletonPath))
                    found.Add(new AnimRow(a.Name, AnimationLibrary.CategoryOf(a.Path),
                                          a.Frames, a.Duration, a.Path));

            // One sequence can arrive through two skeletons that both fit; the author wants a
            // list of animations, not of matches.
            var unique = found.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                              .Select(g => g.First())
                              .ToList();

            // TWO LEVELS, biggest first at both. The category is the game's own folder; the
            // action is read out of the name, because these folders are FLAT -- 1HPistol is 998
            // sequences in one of them, so a category heading alone labelled the list without
            // making it navigable. See AnimationLibrary.ActionOf.
            var list = new List<(string Category, string Key, string Action, List<AnimRow> Rows)>();
            foreach (var group in unique.GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                                        .OrderByDescending(g => g.Count())
                                        .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var byAction = group
                    .GroupBy(r => AnimationLibrary.ActionOf(r.Path, group.Key) is { Length: > 0 } a
                                      ? a : "Other",
                             StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var action in byAction)
                    list.Add((group.Key, $"{group.Key}/{action.Key}", action.Key,
                              action.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                                    .ToList()));
            }

            var best = fits[0];
            return (list, $"{unique.Count:N0} on {fits.Count} rig(s) in {list.Count} group(s) · " +
                          $"best {best.Leaf} {best.Shared}/{best.Total} bones");
        });

        tab.SetAnimations(rows, note);
    }

#if DEV
    public async Task<bool> OpenByNameAsync(string want)
    {
        if (_index is null) { Say($"--open {want}: the game is not open"); return false; }

        var all = AssetIndex.PrimaryFirst(_index.Root.DescendantFiles().Where(f =>
            f.Name.Contains(want, StringComparison.OrdinalIgnoreCase) &&
            f.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))).ToList();

        if (all.Count == 0) { Say($"--open {want}: nothing in the game matching that"); return false; }

        // AN EXACT LEAF WINS. This matched on CONTAINS and took whichever the index ordered
        // first, so `--open SA_01_01` opened MI_SA_01_01 -- the MATERIAL for that movie -- and the
        // edit aimed at it reported "no property URL on SA_01_01", which is true of a material
        // instance and reads exactly like a missing feature.
        var hit = all.FirstOrDefault(f => string.Equals(
                      f.Name[..f.Name.LastIndexOf('.')], want, StringComparison.OrdinalIgnoreCase))
               ?? all[0];

        if (all.Count > 1 && !ReferenceEquals(hit, all[0]) || all.Count > 1)
            Say($"--open {want}: {all.Count:N0} match, opening {hit.Name} — " +
                string.Join(", ", all.Take(8).Select(f => f.Name)));

        await OpenAssetAsync(FromAsset(hit));
        return true;
    }
#endif

    /// <summary>
    /// Record a change that is not a number.
    ///
    /// THE SAME SHAPE AS ApplyEdit AND FOR THE SAME REASONS: the rehearsal is the build's own
    /// write run for real and thrown away, and one entry per property so a second edit does not
    /// stack two whose `was` values disagree.
    /// </summary>
    public bool ApplyValue(AssetTab tab, PropRow row, string kind, string now)
        => ApplyValue(tab, row, kind, row.Value, now);

    /// <summary>
    /// The same, when WAS is not simply what the row currently reads.
    ///
    /// Removing an array entry is the case: the subject is the entry being taken OUT, which is
    /// one of several things the row holds rather than the whole of what it reads.
    /// </summary>
    public bool ApplyValue(AssetTab tab, PropRow row, string kind, string was, string now)
    {
        if (_mod is null) { Say("open a mod first — an edit has to be recorded somewhere"); return false; }
        if (_provider is null) { Say("the game is not open"); return false; }

        if (PropertyEdit.PackageForFile(tab.FilePath) is not { } package)
        { Say($"{tab.Title}: that file is not in the game's content tree"); return false; }

        // THE ROW'S OWN ADDRESS, not its export and name.
        //
        // Only a top-level property carries an export; an array's element, a struct's field, an
        // array inside one of those all carry none -- so an edit aimed at one was written against
        // ("", "[0]") and could never be found again. That is why the array buttons only ever
        // appeared on top-level arrays. Locate has taken a dotted indexed path all along.
        if (row.Address is not var (rowExport, rowPath) || rowExport is null)
        { Say($"{row.Name} does not sit inside any export, so an edit has no address"); return false; }

        // AN FText CAN NEED REWRITING TO THE SAME WORDS.
        //
        // Every other kind holds a value, so writing what is already there is a no-op worth
        // refusing. An FText holds a value AND A SHAPE: the game's enemy abilities read their
        // names out of Enemy_Ability_Strings, a table the player UI does not load, and the words
        // come out as <MISSING STRING TABLE ENTRY> on screen. Rewriting "Bind" as "Bind" replaces
        // the lookup with a LITERAL and fixes it -- and the guard below refused exactly that,
        // saying the name was already what it plainly was not showing.
        if (kind is not ("softadd" or "softremove" or "structdup" or "structdrop" or "text") &&
            string.Equals(now, was, StringComparison.Ordinal))
        { Say($"{row.Name} is already “{now}”"); return false; }

        if (!PropertyEdit.TryValue(_provider, package, rowExport, rowPath, kind, was, now, out var why))
        { Say($"{rowExport}.{rowPath} cannot be changed: {why}"); return false; }

        var ov = _mod.Overrides.FirstOrDefault(
            o => string.Equals(o.Target, package, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = package }; _mod.Overrides.Add(ov); }

        // A SCALAR EDIT REPLACES THE PROPERTY; AN ARRAY EDIT NAMES ONE ENTRY OF IT.
        //
        // Dropping every earlier change to the same field is right for a scalar -- a property holds
        // one value, so the last word wins. It is WRONG for an array: two adds are two different
        // entries, and clearing the field would make the second silently delete the first. A mod
        // that grants an operator two abilities would ship with one, and nothing would say so.
        //
        // So an array edit supersedes only an earlier record about the SAME entry -- add then
        // remove of one path cancels, add then add of two paths accumulates.
        var subject = kind is "softremove" or "structdrop" ? was : now;
        ov.Values.RemoveAll(v =>
            string.Equals(v.Row, rowExport, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(v.Field, rowPath, StringComparison.OrdinalIgnoreCase) &&
            (kind is not ("softadd" or "softremove" or "structdup" or "structdrop") ||
             string.Equals(v.Kind is "softremove" or "structdrop" ? v.Was : v.Now, subject,
                           StringComparison.OrdinalIgnoreCase)));
        ov.Values.Add(new ModOverride.ValueChange(rowExport, rowPath, kind, was, now));
        _mod.Save();

        // AN ARRAY EDIT DOES NOT REPLACE THE ROW'S VALUE, it changes what is under it, so marking
        // the row as reading the added path would be a lie on screen.
        if (kind is "softadd" or "softremove" or "structdup" or "structdrop")
            Say($"{rowExport}.{rowPath}: {(kind is "softadd" or "structdup" ? "+" : "-")} " +
                $"{(kind is "softadd" or "structdup" ? now : was)}");
        else
        {
            row.MarkEdited(now);
            Say($"{rowExport}.{rowPath}: “{was}” -> “{now}”");
        }
        return true;
    }

    /// <summary>
    /// --softlist: what an array of soft references currently holds.
    ///
    /// An add or a remove has to NAME an entry, and there was no way to read the entries out
    /// without opening the fold and reading them off the screen. The removal especially: it
    /// matches on the path, so guessing at one is guessing at a refusal.
    /// </summary>
    /// <summary>
    /// What a + on this array should offer to put in it.
    ///
    /// NOT EVERY ASSET IN THE GAME. A hundred and eighty thousand rows is a haystack, not a
    /// picker. An array of references holds one KIND of thing and the game names that kind by a
    /// leaf prefix -- `GA_` for abilities, `AbilitySet_` for ability sets -- so when every entry
    /// already there shares one, that is the filter, read off the file rather than guessed at.
    /// An empty array has nothing to read, so it offers everything and says so.
    /// </summary>
    public (IReadOnlyList<PickAssetWindow.Candidate> Candidates, string Note)
        RefCandidatesFor(AssetTab tab, PropRow row)
    {
        if (_index is null) return ([], "the game is not open");

        var held = tab.ChildrenOf(row)
                      .Select(k => k.Target)
                      .Where(x => x is not null)
                      .Select(x => x![(x!.LastIndexOf('/') + 1)..])
                      .ToList();

        // THE PREFIX ONLY IF THEY ALL SHARE IT. One entry of a differently named kind means the
        // array is not the single-kind list this assumes, and filtering on the majority would
        // hide exactly the odd one out that somebody is trying to match.
        var prefix = "";
        if (held.Count > 0 && held[0].IndexOf('_') is var cut and > 0)
        {
            var candidate = held[0][..(cut + 1)];
            if (held.All(h => h.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
                prefix = candidate;
        }

        var all = _index.Root.DescendantFiles()
            .Where(f => f.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Select(f => new { Leaf = System.IO.Path.GetFileNameWithoutExtension(f.Name),
                               Package = PropertyEdit.PackageForFile(f.FullPath) })
            .Where(x => x.Package is not null)
            .Where(x => prefix.Length == 0 ||
                        x.Leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => new PickAssetWindow.Candidate(x.Leaf, x.Package!, x.Package!))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var note = prefix.Length > 0
            ? $"Everything in the game named {prefix}…, which is what this array already holds. " +
              (held.Count == 1 ? $"It holds one: {held[0]}."
                               : $"It holds {held.Count}: {string.Join(", ", held.Take(4))}" +
                                 (held.Count > 4 ? ", …" : "") + ".")
            : "The array is empty, so there is nothing to read a kind from — this is every " +
              "asset in the game. Whatever you choose has to be something this property can hold.";

        return (all, note);
    }

    /// <summary>
    /// The OBJECT path a soft reference to this package wants, measured rather than assumed.
    ///
    /// `/Game/A/B` is a package and a soft reference stores the package AND the object in it. For
    /// most assets the object is the package's own leaf; for a BLUEPRINT the thing referenced is
    /// its generated CLASS, which is the leaf plus `_C`. Getting that wrong names the Blueprint
    /// asset where the game asked for a class, so the reference resolves to nothing and whatever
    /// it was for silently does not happen -- which is precisely the fault that made "you cannot
    /// grant npc skills to operators" look true.
    ///
    /// So this OPENS the chosen package and looks for the `_C` export, rather than pattern
    /// matching on the name.
    /// </summary>
    public string ObjectPathFor(string package)
    {
        var leaf = package[(package.LastIndexOf('/') + 1)..];
        try
        {
            if (_provider is not null &&
                _provider.LoadPackage(package).GetExports()
                         .Any(e => string.Equals(e.Name, leaf + "_C", StringComparison.Ordinal)))
                return $"{package}.{leaf}_C";
        }
        catch (Exception) { /* not readable is not a Blueprint; the plain leaf is the safe answer */ }
        return $"{package}.{leaf}";
    }

    /// <summary>
    /// Put back onto the rows what this mod has already done to their arrays.
    ///
    /// Called when a tab fills, because the grid reads the GAME's bytes and knows nothing about
    /// the project. Without it, reopening an asset shows an array with no sign of the entry that
    /// was added to it -- and a change that vanishes when you look away is one nobody trusts.
    /// </summary>
    public void StampPending(AssetTab tab)
    {
        if (_mod is null) return;
        if (PropertyEdit.PackageForFile(tab.FilePath) is not { } package) return;

        if (_mod.Overrides.FirstOrDefault(
                o => string.Equals(o.Target, package, StringComparison.OrdinalIgnoreCase)) is not { } ov)
            return;

        foreach (var r in tab.AllRows.Where(r => r.IsRefArray || r.IsStructArray))
            r.Pending = PendingOn(ov, r);
    }

    /// <summary>What the project records for one array, as the row shows it.</summary>
    private static string PendingOn(ModOverride ov, PropRow row)
    {
        if (row.Address is not var (export, path) || export is null) return "";

        var mine = ov.Values.Where(
            v => v.Kind is "softadd" or "softremove" or "structdup" or "structdrop" &&
                 string.Equals(v.Row, export, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(v.Field, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return "";

        return string.Join("  ", mine.Select(v =>
        {
            // A STRUCT EDIT NAMES AN INDEX, not a path. There is nothing to take a leaf off.
            if (v.Kind is "structdup" or "structdrop")
                return v.Kind == "structdup" ? $"+ a copy of [{v.Now}]" : $"− [{v.Was}]";

            // THE ASSET'S NAME, not the whole reference. A soft class reference is written
            // `/Game/A/GA_Thing.GA_Thing_C`, so taking everything after the last slash puts the
            // name on the row twice with a dot between. Split it properly and keep the package's
            // own leaf, which is what a person calls the thing.
            var path = v.Kind == "softadd" ? v.Now : v.Was;
            var package = PropertyEdit.SplitRef(path).Package ?? path;
            var leaf = package[(package.LastIndexOf('/') + 1)..];
            return $"{(v.Kind == "softadd" ? "+" : "−")} {leaf}";
        }));
    }

    /// <summary>
    /// Add one entry to an array of soft references, and show on the row that it happened.
    ///
    /// The window shows the picker; this takes what came back, works out the object path it
    /// needs, and records the change.
    /// </summary>
    public bool AddRefEntry(AssetTab tab, PropRow row, string package)
    {
        if (!ApplyValue(tab, row, "softadd", "", ObjectPathFor(package))) return false;
        StampPending(tab);
        return true;
    }

    /// <summary>
    /// Grow a struct array by COPYING its last element.
    ///
    /// There is no picker and no blank entry, and that is the honest shape rather than a missing
    /// feature: a struct element carries an unversioned header naming which of its properties are
    /// present, and one written from nothing is a guess at a shape the cooker would have chosen.
    /// A copy of an element the game itself wrote has a header that is known good; its values are
    /// then edited field by field, through rows that already take edits.
    /// </summary>
    public bool DupStructEntry(AssetTab tab, PropRow array)
    {
        var kids = tab.ChildrenOf(array);
        if (kids.Count == 0)
        { Say($"{array.Name} is empty, and a copy needs something to copy"); return false; }

        var last = kids.Count - 1;
        if (!ApplyValue(tab, array, "structdup", "", last.ToString())) return false;
        StampPending(tab);
        return true;
    }

    /// <summary>Drop one element of a struct array, named by the row the × was pressed on.</summary>
    public bool DropStructEntry(AssetTab tab, PropRow entry)
    {
        if (entry.Parent is not { IsStructArray: true } array)
        { Say("that row is not an element of a struct array"); return false; }
        if (entry.ElementIndex < 0)
        { Say($"{entry.Name} does not name an element index"); return false; }

        if (!ApplyValue(tab, array, "structdrop", entry.ElementIndex.ToString(), "")) return false;
        StampPending(tab);
        return true;
    }

    /// <summary>Take one entry back out, named by the entry row the × was pressed on.</summary>
    public bool DropRefEntry(AssetTab tab, PropRow entry)
    {
        if (entry.OwningArray is not { } array)
        { Say("that row is not an entry of an array this can edit"); return false; }
        if (entry.Value.Length == 0)
        { Say($"{entry.Name} has no value to take out"); return false; }

        if (!ApplyValue(tab, array, "softremove", entry.Value, "")) return false;
        StampPending(tab);
        return true;
    }

    public void ListSoftRefs(AssetTab tab, PropRow row)
    {
        var kids = tab.ChildrenOf(row);
        if (kids.Count == 0) { Say($"{row.Export}.{row.Name} holds nothing"); return; }

        Say($"{row.Export}.{row.Name} — {kids.Count:N0} entr(ies):");
        foreach (var k in kids) Say($"    {k.Value}");
    }

    /// <summary>
    /// Open the asset a property points at.
    ///
    /// THE HALF OF "FOLLOWABLE REFERENCES" THAT WAS MISSING. `Find in Data` reveals a mesh
    /// section's material in the explorer; this opens whatever any property names, which is the
    /// thing the author asked for -- "it doesn't let you reference other files to
    /// edit them".
    ///
    /// The index is matched on the LEAF first and the package name confirmed afterwards, because
    /// comparing a converted package name against 180,000 files is a scan and comparing leaves is
    /// a filter. A reference that resolves to nothing is NAMED rather than silently doing
    /// nothing: a mod's own asset, or one in a container this profile does not mount, is exactly
    /// the case somebody needs told about.
    /// </summary>
    public async Task<bool> FollowAsync(PropRow row)
    {
        if (_index is null) { Say("the game is not open"); return false; }
        if (row.Target is not { } want) { Say($"{row.Name} does not name an asset"); return false; }

        var leaf = want[(want.LastIndexOf('/') + 1)..];
        var hit = _index.Root.DescendantFiles()
            .Where(f => f.Name.StartsWith(leaf + ".", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(f => string.Equals(
                PackageId.PackageNameFor(f.FullPath, ModBuild.ProjectName), want,
                StringComparison.OrdinalIgnoreCase));

        if (hit is null) { Say($"{row.Name} points at {want}, which is not in the mounted game"); return false; }

        await OpenAssetAsync(FromAsset(hit));
        return true;
    }

#if DEV
    /// <summary>
    /// --addoverlay &lt;table fragment&gt; [row field now]: lay one of the game's tables over.
    ///
    /// With the fragment alone it LISTS the table's rows and which of their fields are actually
    /// stored, because a field taking its default has no bytes at all and cannot be written --
    /// choosing one of those is the easiest way to get a mod that builds and does nothing.
    ///
    /// With a row, a field and a value it records the overlay on the open project and saves it.
    /// The build does the rest: the copy, the action, and the feature that runs it.
    /// </summary>
    public void AddOverlay(string want, string? row, string? field, double? now)
    {
        if (_provider is null) { Say("--addoverlay: the game is not open"); return; }
        if (_mod is null) { Say("--addoverlay: no mod is open"); return; }

        var tables = TableIndex.Of(_provider);
        var hits = tables.Where(t => t.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(t => t.Name.Length).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        if (hits.Count == 0) { Say($"--addoverlay: no table matching '{want}'"); return; }
        if (hits.Count > 1)
            Say($"--addoverlay: {hits.Count:N0} tables match, taking the shortest name - " +
                string.Join(", ", hits.Take(8).Select(h => h.Name)));

        var target = hits[0];
        var read = TableRows.Read(_provider, target.Package);
        Say($"--addoverlay: {target.Name} - {read.TotalRows:N0} row(s) of {read.RowStruct}");

        if (row is null || field is null || now is null)
        {
            // WHAT CAN ACTUALLY BE WRITTEN, said out loud. "stored" is the whole question.
            foreach (var r in read.Rows.Take(12))
            {
                Say($"    {r.Name}");
                foreach (var c in r.Cells.Where(c => c.Numeric))
                    Say($"        {c.Field,-28} {c.Number,12}   {(c.Stored ? "stored" : "NOT STORED - cannot be written")}");
            }
            return;
        }

        var cell = read.Rows.FirstOrDefault(r => string.Equals(r.Name, row, StringComparison.OrdinalIgnoreCase))
                       ?.Cells.FirstOrDefault(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        if (cell is null) { Say($"--addoverlay: {target.Name} has no {row}.{field}"); return; }
        if (!cell.Stored)
        { Say($"--addoverlay: {row}.{field} is not stored, so there are no bytes to write"); return; }

        _mod.TableOverlays.Add(new ModTableOverlay
        {
            Target = target.Package, Row = row, Field = field, Was = cell.Number, Now = now.Value,
        });
        _mod.Save();
        Say($"--addoverlay: {target.Name} {row}.{field} {cell.Number} -> {now.Value}, " +
            $"{_mod.TableOverlays.Count} overlay(s) on this mod");
    }

    /// <summary>
    /// --uefidelity &lt;fragment&gt;: WHAT UE5 WOULD SHOW FOR THIS ASSET, BESIDE WHAT WE SHOW.
    ///
    /// A measurement, not a feature. The author asked how hard it would be to present a data
    /// asset the way the editor presents one, and the honest answer needs three numbers this
    /// prints: how many properties the file carries, how many the CLASS declares, and whether
    /// UE5's own labels survived cooking anywhere in the package.
    /// </summary>
    public void UeFidelityReport(string want)
    {
        if (_index is null || _provider is null) { Say($"--uefidelity {want}: the game is not open"); return; }

        var all = AssetIndex.PrimaryFirst(_index.Root.DescendantFiles().Where(f =>
            f.Name.Contains(want, StringComparison.OrdinalIgnoreCase) &&
            f.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))).ToList();

        if (all.Count == 0) { Say($"--uefidelity {want}: nothing in the game matching that"); return; }

        // THE OTHER CANDIDATES, NAMED. A fragment that matches 200 assets reports on whichever
        // one PrimaryFirst put in front, and reading a report without knowing that is how a
        // measurement of the wrong asset gets believed.
        if (all.Count > 1)
            Say($"--uefidelity {want}: {all.Count:N0} matches, reporting on the first - " +
                string.Join(", ", all.Take(12).Select(f => f.Name)));

        var hit = all[0];

        try
        {
            foreach (var line in UeFidelity.Report(_provider, AssetLoader.PackagePath(hit.FullPath)))
                Say(line);
        }
        catch (Exception ex)
        {
            Say($"--uefidelity {hit.Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
#endif

    // ------------------------------------------------------------------ meshes on a mesh

    /// <summary>
    /// THE RIG A MESH IS BUILT FOR, out of the game's own naming: SK_&lt;rig&gt;_&lt;what it is&gt;.
    ///
    /// Measured across the game's skeletal meshes: SK_HAAF_ (66), SK_HAAM_ (60), SK_Astromech_,
    /// SK_HTAM_, SK_B1_, SK_B2_ and so on. Every one of them carries the rig in that slot.
    ///
    /// IT IS A CONVENTION, WHICH IS WHY IT ONLY ORDERS THE LIST. Deciding what a person may
    /// attach with a rule of my own is the mistake this project has made before -- "has a mesh"
    /// once labelled all 151 specialisations broken. So a mismatch is listed lower, not withheld,
    /// and the real answer comes from the bones once it is loaded.
    /// </summary>
    private static string RigTokenOf(string leaf)
    {
        var parts = leaf.Split('_');
        return parts.Length >= 3 && parts[0].Equals("SK", StringComparison.OrdinalIgnoreCase)
            ? parts[1] : "";
    }

    /// <summary>
    /// Every skeletal mesh in the game, the ones built for this rig first.
    ///
    /// Read from the ASSET INDEX rather than the parts catalogue, because the index already
    /// carries the container path a load wants and the catalogue carries package paths that would
    /// have to be converted -- a conversion is a second opinion about where a file is.
    /// </summary>
    public IReadOnlyList<(string Path, string Name, bool SameRig)> AttachCandidates(MeshTab tab)
    {
        if (_index is null) return [];

        var mine = RigTokenOf(System.IO.Path.GetFileNameWithoutExtension(tab.FilePath));
        var already = tab.View.Attached.Select(a => a.Key)
                         .Append(tab.FilePath)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _index.Root.DescendantFiles()
            .Where(f => f.Kind == AssetKind.SkeletalMesh &&
                        f.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        !already.Contains(f.FullPath))
            .Select(f =>
            {
                var leaf = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                return (f.FullPath, leaf,
                        mine.Length > 0 &&
                        RigTokenOf(leaf).Equals(mine, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(x => x.Item3)
            .ThenBy(x => x.leaf, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Hang another mesh on the one in the viewport.
    ///
    /// The package read is off the UI thread and through AssetLoader's gate, as every other read
    /// in this Studio is; the GEOMETRY is built by the viewport on its own thread, where the GL
    /// is. So this hands over an object and says nothing about whether it worked -- the viewport
    /// reports that, and the panel reads it back.
    /// </summary>
    public async Task AttachMeshAsync(MeshTab tab, string path, string name)
    {
        if (_provider is not { } provider) { SayBad("the game is not mounted"); return; }

        var asset = await Task.Run<UObject?>(() =>
        {
            try
            {
                return AssetLoader.Load<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>(
                           provider, path)
                       ?? (UObject?) AssetLoader
                           .Load<CUE4Parse.UE4.Assets.Exports.StaticMesh.UStaticMesh>(provider, path);
            }
            catch (Exception) { return null; }
        });

        if (asset is null) { SayBad($"{name}: no mesh could be read out of that package"); return; }
        tab.View.Attach(path, name, asset);
        Say($"attaching {name} to {tab.LoadedName}…");
    }

    public void DetachMesh(MeshTab tab, string key)
    {
        tab.View.Detach(key);
        Say($"removed {System.IO.Path.GetFileNameWithoutExtension(key)} from {tab.LoadedName}");
    }

    // ------------------------------------------------------------------ editing

    /// <summary>
    /// Record an edit to a property — but only once the build has agreed it can make it.
    ///
    /// THE ORDER IS THE WHOLE POINT. The number is not written into the project and then hoped
    /// for; PropertyEdit.Try performs the real edit on the game's own bytes through the same
    /// ScalarEdit the build uses, and the project is only touched when that came back clean. A
    /// change recorded against a property with no bytes behind it would build, install, and do
    /// nothing, with no error anywhere — which is the failure this whole project keeps paying
    /// for, and the reason a box that accepts anything is worse than no box.
    /// </summary>
    /// <summary>
    /// LAY THIS MOD'S CHANGES OVER THE GAME'S VALUES, every time a grid is filled.
    ///
    /// His report: "any changes i do make to say a real game asset i don't see the changes get
    /// updated in the tool". This is why. A property grid is read from the GAME — correctly, it
    /// is the only pristine source — and a number this mod changes lives in the project, not in
    /// that package. So a value typed in, saved, verified against the game's own bytes and shown
    /// in the accent colour reverted to the game's number the moment the asset was opened again,
    /// with nothing anywhere saying the edit still existed.
    ///
    /// MarkEdited is what draws a row as changed, and it was only ever called on the row the
    /// person had just typed into — live state rather than something the panel could reconstruct.
    /// </summary>
    private void ApplyModEdits(AssetTab tab)
    {
        if (_mod is null) return;
        if (PropertyEdit.PackageForFile(tab.FilePath) is not { } package) return;

        if (_mod.Overrides.FirstOrDefault(
                o => o.Target.Equals(package, StringComparison.OrdinalIgnoreCase)) is not { } ov)
            return;

        // EVERY ROW, not the visible ones. `Rows` is what the open folds happen to emit, and a
        // change recorded against a property inside a shut fold would find nothing and be
        // reported as matching no property -- which reads exactly like the game having been
        // patched underneath the project. The same trap the --softadd flag walked into.
        PropRow? Find(string export, string field)
            => tab.AllRows.FirstOrDefault(
                   r => r.Export.Equals(export, StringComparison.OrdinalIgnoreCase) &&
                        r.Name.Equals(field, StringComparison.OrdinalIgnoreCase));

        var marked = 0;
        var wanted = ov.Numbers.Count;

        foreach (var n in ov.Numbers)
        {
            if (Find(n.Row, n.Field) is not { } row) continue;
            row.MarkEdited(n.Now.ToString());
            marked++;
        }

        // THE TYPED LIST TOO, which this did not know about. A name or a string recorded here
        // came back showing the FILE's value on reopen, so the panel disagreed with the project
        // about what the mod does -- and the project is the one that builds.
        foreach (var v in ov.Values.Where(v => v.Kind is not ("softadd" or "softremove")))
        {
            wanted++;
            if (Find(v.Row, v.Field) is not { } row) continue;
            row.MarkEdited(v.Now);
            marked++;
        }

        // An array edit does not replace the row's value -- it changes what is under it -- so it
        // is stamped beside the count rather than marked over it.
        foreach (var r in tab.AllRows.Where(r => r.IsRefArray || r.IsStructArray))
            r.Pending = PendingOn(ov, r);

        // SAID, not silent. A change recorded against a property the grid cannot find is a
        // change that will never be seen and may never build — worth a line rather than a
        // quietly incomplete list.
        if (marked < wanted)
            Say($"{tab.Title}: {wanted - marked} recorded change(s) match no property " +
                "in this asset — the game may have been patched since they were made");
    }

    public bool ApplyEdit(AssetTab tab, PropRow row, string text)
    {
        if (_mod is null) { Say("open a mod first — an edit has to be recorded somewhere"); return false; }
        if (_provider is null) { Say("the game is not open"); return false; }
        // A BOOL AT ITS DEFAULT IS STILL OFFERED, and the REHEARSAL is what refuses it. Editable
        // means "there are bytes to overwrite", which is false for every property left at its
        // default -- so gating the checkbox on it would hide a box on exactly the bools somebody
        // most wants to turn on, and refuse with a sentence about numbers that is not the reason.
        if (!row.CanEdit && !row.IsBool && !row.IsEnum)
        { Say($"{row.Name} [{row.Node.Type}] is not something this can type a value into"); return false; }

        // A NAME TAKES THE OTHER PATH. Everything below this point is about four or eight bytes
        // written over four or eight bytes; an FName is a name-table index, so it is a different
        // write, a different rehearsal and a different entry in the project.
        if (row.IsName) return ApplyValue(tab, row, "name", text.Trim());

        // NOT TRIMMED. A name cannot hold a leading space and an FString can, so trimming one is
        // tidying and trimming the other is changing the value the author typed.
        if (row.IsString) return ApplyValue(tab, row, "string", text);

        // Nor is an FText, for the same reason and rather more so: this is the text a PLAYER
        // reads, and the spacing in it is the author's business.
        if (row.IsText) return ApplyValue(tab, row, "text", text);

        // INVARIANT (NumberText): the grid's own boxes hand this "0.5" from an enum's number and
        // from whatever was typed, and double.TryParse read the PC's regional format -- "0.5" was
        // five on a German machine. A decimal comma is still accepted.
        if (!NumberText.TryParse(text, out var now, out var parseWhy))
        { SayBad($"{row.Name}: {parseWhy}"); return false; }

        if (PropertyEdit.PackageForFile(tab.FilePath) is not { } package)
        { Say($"{tab.Title}: that file is not in the game's content tree"); return false; }

        if (Math.Abs(now - row.Was) < double.Epsilon) { Say($"{row.Name} is already {now}"); return false; }

        // THE REHEARSAL. Not a check of our own devising — the build's own write, run for real
        // and thrown away.
        if (!PropertyEdit.Try(_provider, package, row.Export, row.Name, row.Was, now, out var why))
        { Say($"{row.Export}.{row.Name} cannot be changed: {why}"); return false; }

        var ov = _mod.Overrides.FirstOrDefault(
            o => string.Equals(o.Target, package, StringComparison.OrdinalIgnoreCase));
        if (ov is null) { ov = new ModOverride { Target = package }; _mod.Overrides.Add(ov); }

        // ONE CHANGE PER PROPERTY. Editing the same number twice must not stack two entries whose
        // `was` values disagree — the second would be checked against a value the first replaced,
        // and the build would refuse it having already applied the first.
        ov.Numbers.RemoveAll(n => string.Equals(n.Row, row.Export, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(n.Field, row.Name, StringComparison.OrdinalIgnoreCase));
        ov.Numbers.Add(new ModOverride.NumberChange(row.Export, row.Name, row.Was, now));
        _mod.Save();

        row.MarkEdited(now.ToString());
        Say($"{row.Export}.{row.Name}: {row.Was} → {now}, verified against the game's own bytes");
        AfterEditsChanged();
        return true;
    }

    /// <summary>Drop this mod's change to a property, putting the game's own value back.</summary>
    public bool RevertEdit(AssetTab tab, PropRow row)
    {
        if (_mod is null) return false;
        if (PropertyEdit.PackageForFile(tab.FilePath) is not { } package) return false;

        var ov = _mod.Overrides.FirstOrDefault(
            o => string.Equals(o.Target, package, StringComparison.OrdinalIgnoreCase));
        if (ov is null) return false;

        var gone = ov.Numbers.RemoveAll(
            n => string.Equals(n.Row, row.Export, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(n.Field, row.Name, StringComparison.OrdinalIgnoreCase));
        if (gone == 0) { Say($"{row.Name} has no change of this mod's to revert"); return false; }

        // AN OVERRIDE WITH NOTHING LEFT IN IT IS REMOVED. Left behind it would rewrite a game
        // package byte for byte and ship it, which is a mod that changes nothing and conflicts
        // with every other mod touching that asset.
        //
        // ASKED OF THE OVERRIDE, not of a list of fields kept here. This test used to name seven
        // of them and miss five — so reverting one number on a part that also carried tags, a
        // handed-back colour or a slot lock deleted all of it. That is the same fault the edit
        // panel's save gate had, in a second place, and the same answer: HasEdit is maintained
        // beside the fields it lists, and a hand-written subset is not.
        if (!ov.HasEdit && ov.Icon.Length == 0) _mod.Overrides.Remove(ov);

        _mod.Save();
        row.ClearEdit();
        Say($"reverted {row.Export}.{row.Name}");
        AfterEditsChanged();
        return true;
    }

    /// <summary>The tree, the list and the bold marks all follow the project, so all three refresh.</summary>
    private void AfterEditsChanged()
    {
        // The first import into a fresh mod is what makes "Use…" have anything to offer, so the
        // button appears on the same act that fills it.
        Raise(nameof(HasMyImages));
        RemarkSectionRows();
        BuildModTree();
        BuildModifiedSet();
        if (_onlyModified) { _folder = null; Raise(nameof(Folder)); ShowTree(); }
        RebuildList();
        RemarkRecruitTabs();
        RecheckProblems();
    }

    /// <summary>
    /// Tell every open recruit sheet whether this mod takes its row over.
    ///
    /// READ BACK FROM THE PROJECT, never remembered from the act that wrote it -- so a swap made
    /// on another tab, an undo, or a project opened with one already in it all show the same way.
    /// Both shapes are asked, because a project written before 2026-09-21 put the rename on an
    /// override of the game's table and still builds.
    /// </summary>
    private void RemarkRecruitTabs()
    {
        foreach (var tab in Tabs.OfType<RecruitTab>())
        {
            if (_mod is null) { tab.TakenBy = ""; continue; }

            var swap = CharacterAuthor.PoolSwaps(_mod).FirstOrDefault(
                s => string.Equals(s.From, tab.Recruit.Definition,
                                   StringComparison.OrdinalIgnoreCase));
            tab.TakenBy = swap is null
                ? ""
                : $"This row names {Leaf(swap.To)} instead of {Leaf(swap.From)}, so " +
                  $"{(tab.Recruit.Name.Length > 0 ? tab.Recruit.Name : tab.Recruit.Row)} is not " +
                  "offered while this mod is installed. " +
                  (swap.ShippedAs.Length > 0
                       ? $"Through {swap.ShippedAs}, a table this mod ships — so it does not fight " +
                         "other recruit mods."
                       : "Through an override of the GAME's own table, which only one mod can own " +
                         "— make them again to move it onto a table of this mod's own.");
        }
    }

    /// <summary>
    /// Which files in the GAME tree this mod changes, in the index's own spelling.
    ///
    /// ModBuild.ContainerPathFor is the project's own converter from a package name to the path
    /// the containers key their files by, so this is not a guess at how two path forms line up —
    /// it is the function the build itself uses. That is what makes the bold marks trustworthy.
    /// </summary>
    private void BuildModifiedSet()
    {
        _modified.Clear();
        if (_mod is null) return;
        foreach (var o in _mod.Overrides)
        {
            if (o.Target.Length == 0) continue;
            try { _modified.Add(ModBuild.ContainerPathFor(o.Target)); }
            catch (Exception) { /* a bold mark is a convenience; never take the panel down for one */ }
        }
    }

    public void CloseTab(StudioTab tab)
    {
        var at = Tabs.IndexOf(tab);
        if (at < 0) return;
        Tabs.Remove(tab);
        Raise(nameof(AnyTabs));
        // The neighbour, so closing a tab does not dump somebody on a blank pane while others
        // are still open.
        Tab = Tabs.Count == 0 ? null : Tabs[Math.Min(at, Tabs.Count - 1)];
    }

    // ------------------------------------------------------------------ the selection

    /// <summary>The row picked in the list, and what the editing side is looking at.</summary>
    public AssetRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasAsset));
            Raise(nameof(AssetName));
            Raise(nameof(AssetPath));
            Raise(nameof(AssetOpens));
            // NO LONGER NARRATES what it would open: it opens, on a double click. A log line
            // per selection was noise the moment the panel became real.
        }
    }

    public bool HasAsset => _selected is not null;
    public string AssetName => _selected?.Name ?? "";
    public string AssetPath => _selected?.Path ?? "";
    public string AssetOpens => _selected?.Opens ?? "";

    /// <summary>
    /// One line into the log.
    ///
    /// MARSHALLED, because Log is an ObservableCollection bound to a list on screen and Say is
    /// handed out as a callback to things that run in the BACKGROUND — the parts catalogue, the
    /// animation index. Adding from a worker thread throws inside Avalonia's container
    /// generator, and the stack trace lands in the list rather than at the caller, so it reads
    /// as a bug in the log. It crashed the whole application on startup exactly once, from the
    /// animation index, before this existed.
    /// </summary>
    /// <summary>
    /// Say something that went WRONG, so the log can draw it in red.
    ///
    /// The validator's complaints used to live in the build bar, where three of them share one
    /// trimmed line and the rest are a tooltip — the author's note: "it should be inside the log
    /// in red". The bar keeps the count, which is a status; the sentences belong where every
    /// other sentence this tool says already goes.
    /// </summary>
    public void SayBad(string line) => Say(line, bad: true);

    public void Say(string line) => Say(line, bad: false);

    private void Say(string line, bool bad)
    {
        // BOTH WAYS, and this is not redundancy. The in-app log is the only thing the AUTHOR can
        // see — this is a WinExe with no console. Console.WriteLine is the only thing a redirected
        // test run can READ. Writing to one of them was how "the log didn't say" and "the trace
        // proves it works" could both be true at once: my diagnostics went where he could not see
        // them, and his log went where I could not read it.
        Console.WriteLine(line);
        var entry = new LogLine(line, bad);
        if (Dispatcher.UIThread.CheckAccess()) Log.Add(entry);
        else Dispatcher.UIThread.Post(() => Log.Add(entry));
    }

    /// <summary>Run something on the UI thread whether or not we are already on it.</summary>
    private static void OnUi(Action work)
    {
        if (Dispatcher.UIThread.CheckAccess()) work();
        else Dispatcher.UIThread.Post(work);
    }

    /// <summary>
    /// The filter's own line in the log, REPLACED rather than appended.
    ///
    /// Every keystroke re-runs the search, so appending would put one line per character typed
    /// into a log somebody reads to find out what the build did. One line that updates says the
    /// same thing and stays readable.
    /// </summary>
    private const string FilterMark = "filter: ";

    private void SayFilter(string line)
    {
        var text = FilterMark + line;
        if (Log.Count > 0 && Log[^1].Text.StartsWith(FilterMark, StringComparison.Ordinal))
            Log[^1] = new LogLine(text, bad: false);
        else Log.Add(new LogLine(text, bad: false));
    }

    // ------------------------------------------------------------------ the mod

    public void OpenMod(string path)
    {
        try
        {
            _mod = ModProject.Load(path);
            _modPath = path;
            Say($"opened {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception e)
        {
            _mod = null;
            _modPath = "";
            // NAMED, not swallowed. A project that fails to open and says nothing is
            // indistinguishable from one that opened and is empty.
            Say($"could not open {System.IO.Path.GetFileName(path)}: {e.Message}");
        }
        AfterModChanged();
    }

    public void CloseMod()
    {
        if (_mod is null) return;
        Say($"unloaded {System.IO.Path.GetFileName(_modPath)}");
        _mod = null;
        _modPath = "";
        AfterModChanged();
    }

    private void AfterModChanged()
    {
        // A DETAILS TAB BELONGS TO ONE PROJECT. Nothing else in the strip is tied to the open mod
        // — an asset tab reads the game, a part tab holds its own copy — but this one edits the
        // project itself, so one left open over a mod that has been unloaded or replaced is a
        // panel describing something nobody is looking at. Its save refuses in that state
        // anyway; closing it means the state never arises.
        foreach (var stale in Tabs.OfType<ModTab>()
                                  .Where(t => !ReferenceEquals(t.Mod, _mod)).ToList())
            CloseTab(stale);

        // A TABLE CARD THAT IS OPEN SHOWS THE MOD IT WAS FILLED FROM. Left alone over another
        // mod, its boxes still carry the first mod's green numbers, and leaving one runs the
        // save against the SECOND mod with a number the second never had -- which records the
        // first mod's edit into it. Refilled, the card shows what this mod holds (nothing, or
        // its own), and the line about the last number typed is dropped with the mod it was about.
        foreach (var open in Tabs.OfType<TableTab>().ToList())
        {
            open.Feedback("");
            RefillTable(open);
        }

        // AND A MODIFICATION'S, for the same reason: its boxes carry the mod they were filled from.
        RefillModificationTabs();

        // AND AN ABILITY'S, which was left showing the first mod's green numbers, tiers and gates,
        // and wrote the next number typed into the SECOND mod beside values it never had. Each tab
        // refills from ITS OWN reading (the game's numbers do not change with the mod).
        foreach (var open in Tabs.OfType<AbilityTab>().Where(t => t.Reading is { Why: null or "" }).ToList())
        {
            open.Feedback("");
            RefillEffects(open);
            if (open.Family is { Tiers.Count: > 0 } family) FillProgression(open, family);
        }

        Raise(nameof(HasMod));
        Raise(nameof(ModTitle));
        Raise(nameof(ModPath));
        Raise(nameof(Mod));
        Raise(nameof(Where));

        // AND THE BUILD BAR. Everything it says is computed off _mod, and nothing raised it when
        // a mod was OPENED — only when something was edited. So the bar sat reading "no mod
        // open" with its button disabled over a mod plainly named in the header above it, and
        // the only way to wake it was to change something first.
        RecheckProblems();
        RefreshShare();
        Raise(nameof(BuildStatus));
        BuildModTree();
        BuildModifiedSet();
        if (_onlyModified) { _folder = null; Raise(nameof(Folder)); ShowTree(); }
        RebuildList();
    }

    private static string Describe(ModPart p)
    {
        var bits = new List<string>();
        if (p.MeshFile.Length > 0) bits.Add(Leaf(p.MeshFile));
        if (p.Regions.Count > 0) bits.Add($"{p.Regions.Count} region(s)");
        return string.Join("   ", bits);
    }

    private static string Leaf(string s)
    {
        if (s.Length == 0) return "";
        var cut = s.LastIndexOfAny(new[] { '/', '\\', '.' });
        return cut >= 0 && cut < s.Length - 1 ? s[(cut + 1)..] : s;
    }

    /// <summary>
    /// The PACKAGE half of an object path: "/Game/A/SK_X.SK_X" names package "/Game/A/SK_X".
    ///
    /// A project stores a mesh or a picture as an object path and every override targets a
    /// package, so the two forms of the same asset have to be reduced to one before they can be
    /// compared. Doing it by hand at each site is how a row ends up saying a copy points
    /// somewhere else when it points at exactly what it always did.
    /// </summary>
    private static string PackageOf(string objectPath)
    {
        if (objectPath.Length == 0) return "";
        var dot = objectPath.LastIndexOf('.');
        return dot > 0 && dot > objectPath.LastIndexOf('/') ? objectPath[..dot] : objectPath;
    }

    // ------------------------------------------------------------------ the game

    /// <summary>
    /// Where installs come from for the first-run question; null is Steam's own library list.
    /// Set only by the developer flag that pretends Steam holds nothing, so the "where is the
    /// game" screens can be walked through on a machine that has the game (see App.Start).
    /// </summary>
    internal static Func<List<string>>? Finder;

    /// <summary>
    /// Mount the game in the background and show its top level.
    ///
    /// OFF THE UI THREAD and after the window is up, because mounting reads six containers and
    /// indexes 180,000 files. Doing it before the first frame would mean the Studio appears to
    /// take several seconds to start, which is the wrong thing to spend a first impression on.
    /// </summary>
    public async Task LoadGameAsync()
    {
        // ONE MOUNT AT A TIME, and none over an existing one. "Find the game again" calls this when
        // nothing is mounted (the Studio was opened without the game, or the mount failed), and a
        // second call while the first is still reading six containers would mount the game twice.
        if (_gameLoading || _provider is not null) return;
        _gameLoading = true;
        GameStatus = "opening the game…";
        try
        {
            // WHERE THE GAME IS is GameSetup's question (UEToolkit.Core), asked here again rather
            // than handed over by whoever opened the window: it is cheap, it writes nothing when
            // nothing changed, and the paths that skip the startup screens (a project given on the
            // command line) would otherwise mount nothing. The ASKING happens before the Projects
            // window (App.Start); by the time this runs, an answer is on disk or there is none.
            var setup = GameSetup.Resolve(find: Finder);
            if (setup.ProfilePath is not { } path)
            {
                // Something found and waiting to be confirmed is not an error; nothing found, or a
                // setup that could not be completed, is.
                if (setup.NeedsAnswer && setup.Candidates.Count > 0) Say(setup.Say); else SayBad(setup.Say);
                Say("game: " + FindAgainHint);
                GameStatus = !setup.NeedsAnswer ? $"could not open the game: {setup.Say} - {FindAgainHint}"
                           : setup.Candidates.Count > 0 ? $"the game has not been confirmed yet - {FindAgainHint}"
                           : $"the game has not been found - {FindAgainHint}";
                return;
            }
            Say(setup.Say);
            _gameInstall = setup.Install;

            var lines = new List<string>();
            var mounted = await Task.Run(() => GameMount.OpenAsync(path, s => lines.Add(s)));
            foreach (var l in lines) Say(l);

            _index = mounted.Index;
            // KEPT. Reading a property needs the provider, and throwing it away at
            // mount is what made the panel impossible rather than merely unbuilt.
            _provider = mounted.Provider;
            _profile = mounted.Profile;
            // Off this thread: it looks for the game's executable under Binaries, and nothing
            // downstream waits on the answer. It cannot throw (it catches everything) and Say is
            // safe from any thread, so a verdict simply arrives in the log when it is ready.
            var checkedProfile = mounted.Profile;
            _ = Task.Run(() => CheckGameBuild(checkedProfile));
            // The thumbnail loader needs it too, and nothing decodes before this is set.
            IconLoader.Provider = mounted.Provider;

            // AND THE BUILD BAR, whose state depends on the provider as much as on the mod.
            // Mounting finishes AFTER a project opens, so a bar raised only on mod-open sat
            // reading "the game is not mounted" over a game that had finished mounting — the
            // same fault as "no mod open", one input further along.
            RecheckProblems();
            RefreshShare();   // the profile is known now, so the game folder can be asked

            // STARTED HERE, not when the Clothing tab is first opened. It takes a while, and
            // beginning it at mount means the tab is usually ready by the time anybody presses it
            // rather than starting a wait when they do.
            _catalog.EnsureBuilt(mounted.Provider,
                mounted.Profile.Readers.CustomizationPartType ?? "CustomizationPartDefinition",
                Say);
            StartAnimationLibrary(mounted.Provider, mounted.Index, mounted.Profile);
            StartTintPalette(mounted.Provider, mounted.Index);
            BuildGameTop();
            ShowTree();

            GameStatus = mounted.Summary;
            Say($"game content: {_gameTop.Count - 1} top-level folder(s)");
        }
        catch (Exception e)
        {
            // A mount that fails leaves nothing behind (the fields below are only set once it has
            // worked), so pointing the Studio at the game again can simply try again.
            GameStatus = $"could not open the game: {e.Message} - {FindAgainHint}";
            SayBad(GameStatus);
        }
        finally
        {
            _gameLoading = false;
        }
    }

    /// <summary>What to do when the game cannot be opened: the way back is a menu entry, named
    /// here once so every message that points at it says the same thing.</summary>
    internal const string FindAgainHint = "use Project > Find the game again… to say where it is";

    private bool _gameLoading;
    private string? _gameInstall;

    /// <summary>The install folder the game was last opened from, or null before it has been.</summary>
    public string? GameInstallInUse => _gameInstall;

    /// <summary>True once the game is mounted, or while it is being. Changing the install then needs
    /// a restart - the mount is held by dozens of views (see MainWindow.OnFindGame) - whereas with
    /// nothing mounted there is nothing to tear down and the new answer can be mounted straight away.</summary>
    public bool GameInUse => _gameLoading || _provider is not null;

    /// <summary>
    /// Has the game been updated since the usmap was made? The ImGui Studio asked this at startup
    /// and put up a screen; here it is a red line in the log, because the Studio is already open.
    ///
    /// It matters because a usmap describes one build's property layout, and reading a newer build
    /// through an older map does not fail - it returns WRONG VALUES, and a mod built on them
    /// installs cleanly and misbehaves in game. A warning, never a refusal: the layout often did
    /// not move, and being locked out by a check that is only probably right is worse than being
    /// told. What it remembers (settings/gamebuilds.json) is the old Studio's file, so an upgraded
    /// folder carries on from the build it last saw.
    /// </summary>
    private void CheckGameBuild(GameProfile profile)
    {
        try
        {
            var dir = GameSetup.SettingsDir();
            // Asked BEFORE remembering, or "did it change" compares this run against itself.
            var verdict = GameInstall.Check(profile, GameInstall.Remembered(dir, profile.Name));
            GameInstall.Remember(dir, profile.Name, verdict.Identity ?? "");
            // ONE SHORT LINE: the log does not wrap, and the verdict already says what to do. The
            // longer account the old screen gave (a mod built on wrong values installs cleanly and
            // is wrong in game) is what the red colour is for.
            if (verdict.WorthSaying) SayBad("game: " + verdict.Say);
        }
        catch (Exception) { /* a build we cannot identify is not worth stopping for */ }
    }

    /// <summary>
    /// The game's top rows, built against whatever the filter currently allows.
    ///
    /// REBUILT rather than filtered in place, because a GameNode caches the children it made and
    /// those children were made under the old filter. Rebuilding drops the lot, which is cheap:
    /// it is one level of wrappers, and the levels below are made again only when opened.
    /// </summary>
    private void BuildGameTop()
    {
        _gameTop.Clear();
        if (_index is null) return;

        var top = Container(_index.Root);
        // [root] first, exactly as the Data Explorer has it: the container itself, holding
        // whatever sits loose at the top, with the folders listed after it as siblings.
        _gameTop.Add(new GameNode(top, "[root]", leaf: true, filter: _filter, open: _openFolders));
        foreach (var node in top.Children.Where(c => c.IsFolder &&
                                                     (_filter is null || _filter.Folders.Contains(c))))
            _gameTop.Add(new GameNode(node, filter: _filter, open: _openFolders));
    }

    /// <summary>
    /// Which folders of the game tree are open, by path.
    ///
    /// OUTSIDE THE NODES, because <see cref="BuildGameTop"/> throws every node away and makes new
    /// ones each time the Filter box changes. Held here it survives that, and survives the
    /// show-only-modified swap as well, which until now only kept its folds by the accident of
    /// re-adding the same instances.
    /// </summary>
    private readonly HashSet<string> _openFolders = new(StringComparer.OrdinalIgnoreCase);

    private const int Cap = 500;

    /// <summary>
    /// Fill the list under the tree from whichever of the three states is in force.
    ///
    /// ONE PLACE, rather than three collections that each go stale on their own. Show only
    /// modified wins over Filter, because it is a mode and the other is a search within one.
    /// </summary>
    private void RebuildList()
    {
        Assets.Clear();
        var found = 0;
        IEnumerable<AssetRow> rows;

        if (_onlyModified)
        {
            // Everything until a folder is picked, then that folder's share of it. Ticking the
            // box and being shown an empty panel would read as "this mod changes nothing".
            rows = _folder is null
                ? _modRows.Values
                : (_flat ? _folder.AllAssets : _folder.Assets)
                    .Select(a => _modRows.TryGetValue(a, out var r) ? r : FromAsset(a));
        }
        else
        {
            // NO SPECIAL CASE FOR FILTERING ANY MORE. A filter narrows the TREE and narrows what
            // a folder holds; it does not decide on its own that you wanted to see everything at
            // once. The folder is still what says which rows.

            rows = _folder is null
                ? Enumerable.Empty<AssetRow>()
                : (_flat ? _folder.AllAssets : _folder.Assets).Select(FromAsset);
        }

        var kept = new List<AssetRow>();
        foreach (var r in rows)
        {
            found++;
            if (kept.Count < Cap) kept.Add(r);
        }

        kept.Sort((a, b) => _descending
            ? string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var r in kept) Assets.Add(r);

        // SAYS WHEN IT IS SHOWING YOU LESS THAN IT FOUND. A list that silently stops at 500 is a
        // list somebody scrolls to the bottom of and concludes their asset is not in the game.
        _listCount = found == 0 ? ""
            : found > kept.Count ? $"{kept.Count:N0} of {found:N0} — narrow it to see the rest"
            : found == 1 ? "1 item" : $"{found:N0} items";
        Raise(nameof(ListCount));
        Raise(nameof(ListEmpty));
        Raise(nameof(EmptyBecause));
    }

    public bool ListEmpty => Assets.Count == 0;

    /// <summary>Why the list is empty, which is never the same reason twice.</summary>
    public string EmptyBecause
        => _onlyModified ? (!HasMod ? "No mod is open."
                            : _folder is null ? "This mod has not changed anything yet."
                            : "Nothing of this mod's in that folder.")
         : _folder is null ? (Filtering
                ? "The tree above now holds only folders with a match in them. Pick one."
                : "Pick a folder above.")
         : _flat ? "Nothing beneath this folder."
         : Filtering ? "Nothing in this folder matches — the matches are in the folders under it."
         : "This folder holds only other folders — tick the folder button above to list what is inside them.";

    /// <summary>
    /// A row for one game asset.
    ///
    /// THE EXTENSION COMES OFF THE NAME. Nine files in ten here are .uasset, so the extension
    /// distinguishes nothing while eating the width that the actual name needs — and the Type
    /// column beside it says what the thing is far better than ".uasset" ever did. The full path,
    /// extension and all, is still on the row and still shown when one is picked.
    /// </summary>
    private AssetRow FromAsset(AssetNode f) => new()
    {
        Name = System.IO.Path.GetFileNameWithoutExtension(f.Name) is { Length: > 0 } stem
            ? stem : f.Name,
        Type = GameNode.TypeName(f.Kind),
        Path = f.FullPath,
        Opens = GameNode.OpensFor(f.Kind),
        Payload = f,
        Modified = _modified.Contains(f.FullPath),
    };

    // ------------------------------------------------------------------ the modified tree

    /// <summary>One thing this mod changes or adds, and where its package lands.</summary>
    private readonly record struct ModItem(string Package, AssetKind Kind, AssetRow Row);

    /// <summary>
    /// WHERE EACH ITEM'S PACKAGE GOES, taken from the project rather than from a habit.
    ///
    /// <see cref="ModProject.Folder"/> is the project's own answer to "where do this mod's
    /// packages live, relative to /Game/Game/", so every built item is under it. An override is
    /// the exception and needs no rule: it already names the game package it replaces.
    /// </summary>
    private IEnumerable<ModItem> ModItems()
    {
        if (_mod is null) yield break;
        var home = $"/Game/Game/{_mod.Folder.Replace('\\', '/').Trim('/')}";

        // THE DISPLAY NAME WHERE THERE IS ONE, because the ids do not distinguish these rows.
        // Eight parts of one outfit share thirty characters of prefix and differ only in the last
        // word, so a trimmed column shows eight identical lines. The id is still the row's path.
        foreach (var p in _mod.Parts)
            yield return Item($"{home}/{p.Id}", AssetKind.Data,
                p.Display.Length > 0 ? p.Display : p.Id.Length > 0 ? p.Id : "(unnamed part)",
                "Part", "part editor", p);

        foreach (var c in _mod.Copies)
            yield return Item($"{home}/{c.Id}", AssetKind.Data,
                AbilityText.Shown(c), "Copy",
                WeaponAuthor.IdentityOf(c.Id) is not null ? "weapon editor" : "copy editor", c);

        foreach (var o in _mod.Overrides)
            yield return Item(o.Target, AssetKind.Data,
                Leaf(o.Target), "Override", "property view", o);

        foreach (var m in _mod.Meshes)
            // NAMED BY WHAT IT COPIES, not by the file put over it. A mesh entry with no
            // import yet — which is every one made for a weapon piece before the author brings
            // an FBX — had an empty name and an empty path, so it was one of the rows the tree
            // counted as "with no package path" and dropped.
            yield return Item($"{home}/{Leaf(m.Source)}", AssetKind.SkeletalMesh,
                Leaf(m.Source), "Mesh", "mesh editor", m);

        foreach (var sp in _mod.Specs)
            yield return Item($"{home}/{sp.Id}", AssetKind.Data,
                sp.Display.Length > 0 ? sp.Display : sp.Id.Length > 0 ? sp.Id : "(unnamed spec)",
                "Spec", "specialization editor", sp);

        foreach (var mi in _mod.Missions)
            yield return Item($"{home}/{mi.Id}", AssetKind.Data,
                mi.Id.Length > 0 ? mi.Id : "(unnamed mission)",
                mi.InPlace ? "Mission (in place)" : "Mission", "mission editor", mi);

        static ModItem Item(string package, AssetKind kind, string name, string type,
                            string opens, object payload)
            => new(package, kind, new AssetRow
            {
                Name = name, Type = type, Path = package,
                Opens = opens, Payload = payload, Modified = true,
            });
    }

    /// <summary>
    /// The folders a package path passes through, content relative.
    ///
    /// The leading /Game/ is the mount and says nothing, so it comes off — which is why the tree
    /// reads Game / Customizations / Mods rather than repeating the project name at every level.
    /// A trailing .Object is the object INSIDE the package, not another folder: keeping it would
    /// draw every asset twice, once as a folder and once as the thing in it.
    /// </summary>
    private static string[] Segments(string package)
    {
        var p = package.Replace('\\', '/').Trim('/');
        if (p.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) p = p[5..];

        var dot = p.LastIndexOf('.');
        if (dot > p.LastIndexOf('/')) p = p[..dot];

        return p.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Build the tree of what this mod touches, from the mod's own paths.
    ///
    /// Opened all the way down, because a pruned tree that arrives shut shows one row and hides
    /// the answer; and because there is nothing in it that was not put there on purpose, so
    /// there is nothing to collapse away.
    /// </summary>
    private void BuildModTree()
    {
        _modTop.Clear();
        _modRows.Clear();
        if (_mod is null) return;

        var root = new AssetNode { Name = "", FullPath = "", IsFolder = true };
        var placed = 0;
        var dropped = 0;

        foreach (var it in ModItems())
        {
            var segs = Segments(it.Package);
            // COUNTED, not skipped quietly. An item with no usable path is one the panel cannot
            // show, and the person needs to know the panel is not the whole story.
            if (segs.Length == 0) { dropped++; continue; }

            var at = root;
            for (var i = 0; i < segs.Length - 1; i++) at = Child(at, segs[i], true);

            var leaf = Child(at, segs[^1], false);
            leaf.Kind = it.Kind;
            _modRows[leaf] = it.Row;
            placed++;
        }

        root.SortRecursive();

        _modTop.Add(new GameNode(root, "[root]", leaf: true));
        foreach (var c in root.Children.Where(c => c.IsFolder))
        {
            var node = new GameNode(c);
            OpenAll(node);
            _modTop.Add(node);
        }

        if (dropped > 0)
            Say($"show only modified: {placed} item(s) placed, {dropped} with no package path");

        static void OpenAll(GameNode n)
        {
            n.IsExpanded = true;
            foreach (var c in n.Folders) OpenAll(c);
        }

        static AssetNode Child(AssetNode at, string name, bool folder)
        {
            foreach (var c in at.Children)
                if (c.IsFolder == folder &&
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c;

            var made = new AssetNode
            {
                Name = name,
                IsFolder = folder,
                Parent = at,
                FullPath = at.FullPath.Length == 0 ? name : at.FullPath + "/" + name,
            };
            at.Children.Add(made);
            return made;
        }
    }

    /// <summary>
    /// The first level worth showing. The real root is a single container folder with everything
    /// under it, so showing THAT is one row that tells nobody anything.
    /// </summary>
    private static AssetNode Container(AssetNode root)
    {
        var at = root;
        while (at.Children.Count == 1 && at.Children[0].IsFolder) at = at.Children[0];
        return at;
    }
}

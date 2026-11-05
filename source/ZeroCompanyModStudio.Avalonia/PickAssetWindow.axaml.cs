using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// Choose one of the game's own assets — seen, when it is a picture.
///
/// The author put the old studio's icon picker beside the first version of this one: "The old mod
/// studio's icon picker was so much better." It was. It showed the pictures, it narrowed to what
/// could plausibly be used, it kept your own images in their own section, and it put Import in
/// the same window. All four are here now.
///
/// It hands back a package name, or one of YOUR files, and decides nothing about what is done
/// with either — pointing a part at another mesh and putting your pixels into a texture are the
/// same choice made in two different places, and both callers own their own half.
/// </summary>
public partial class PickAssetWindow : Window
{
    /// <summary>
    /// One candidate. <see cref="OwnFile"/> is set for the author's own images, and then
    /// <see cref="Package"/> is its file name rather than a game package.
    /// </summary>
    public sealed class Candidate : Obs
    {
        public Candidate(string name, string where, string package, string? ownFile = null)
        {
            Name = name;
            Where = where;
            Package = package;
            OwnFile = ownFile;
        }

        public string Name { get; }
        public string Where { get; }
        public string Package { get; }
        public string? OwnFile { get; }

        private Bitmap? _shot;

        /// <summary>
        /// The picture, once it has been decoded.
        ///
        /// ASKED FOR WHEN THE TILE IS BUILT, not up front: IconLoader throttles to two at a time
        /// and caches, including its failures, so a grid of a hundred fills in rather than
        /// freezing — and scrolling back asks the cache.
        /// </summary>
        public Bitmap? Shot { get => _shot; set => Set(ref _shot, value); }
    }

    /// <summary>Enough tiles to browse, few enough to build at once. Filter to see more.</summary>
    private const int Tiles = 200;
    private const int Rows = 400;

    private readonly List<Candidate> _all = new();
    private readonly List<Candidate> _own = new();
    private readonly Func<Candidate, bool>? _scope;
    private readonly bool _images;

    public PickAssetWindow() : this("Choose from the game", "", [], false) { }

    public PickAssetWindow(string heading, string note, IEnumerable<Candidate> candidates,
                           bool images, string scopeLabel = "",
                           Func<Candidate, bool>? scope = null,
                           IEnumerable<Candidate>? own = null,
                           Func<Task<string?>>? import = null)
    {
        AvaloniaXamlLoader.Load(this);

        _all.AddRange(candidates);
        if (own is not null) _own.AddRange(own);
        _scope = scope;
        _images = images;

        this.FindControl<TextBlock>("Heading")!.Text = heading;
        var noteBlock = this.FindControl<TextBlock>("Note")!;
        noteBlock.Text = note;
        noteBlock.IsVisible = note.Length > 0;

        var filter = this.FindControl<TextBox>("Filter")!;
        var scopeBox = this.FindControl<CheckBox>("Scope")!;
        var count = this.FindControl<TextBlock>("Count")!;
        var grid = this.FindControl<ListBox>("Grid")!;
        var list = this.FindControl<ListBox>("List")!;
        var ownBlock = this.FindControl<StackPanel>("OwnBlock")!;
        var ownList = this.FindControl<ItemsControl>("Own")!;
        var ownEmpty = this.FindControl<TextBlock>("OwnEmpty")!;
        var importButton = this.FindControl<Button>("Import")!;
        var picked = this.FindControl<TextBlock>("Picked")!;
        var accept = this.FindControl<Button>("Accept")!;
        var cancel = this.FindControl<Button>("Cancel")!;

        grid.IsVisible = images;
        list.IsVisible = !images;

        // A scope with no label is no scope: the box would be a control that does nothing.
        scopeBox.Content = scopeLabel;
        scopeBox.IsVisible = scopeLabel.Length > 0 && scope is not null;

        // Your own section only where this kind of thing can BE your own. A mesh picker offers
        // the game's meshes; importing an FBX is the row's other button.
        ownBlock.IsVisible = images;
        importButton.IsVisible = import is not null;

        void ShowOwn()
        {
            foreach (var c in _own) Fetch(c);
            ownList.ItemsSource = _own.ToList();
            ownList.IsVisible = _own.Count > 0;
            ownEmpty.IsVisible = _own.Count == 0;
        }

        void Refill()
        {
            var text = filter.Text ?? "";
            IEnumerable<Candidate> hits = _all;

            if (_scope is not null && scopeBox.IsChecked == true) hits = hits.Where(_scope);
            if (text.Length > 0)
                hits = hits.Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                       c.Where.Contains(text, StringComparison.OrdinalIgnoreCase));

            var found = hits.ToList();
            var cap = images ? Tiles : Rows;
            var shown = found.Take(cap).ToList();

            if (images) foreach (var c in shown) Fetch(c);

            if (images) grid.ItemsSource = shown; else list.ItemsSource = shown;

            // THE NUMBER THAT MATCHED, not the number drawn — and the total, so the scope box
            // reads as the narrowing it is. The old studio said "128 of 1,167" and that is the
            // sentence somebody needs.
            count.Text = found.Count > cap
                ? $"{found.Count:N0} of {_all.Count:N0} — showing the first {cap}. Type more to narrow it."
                : $"{found.Count:N0} of {_all.Count:N0}";
        }

        ShowOwn();
        Refill();

        filter.TextChanged += (_, _) => Refill();
        scopeBox.IsCheckedChanged += (_, _) => Refill();

        // ONE SELECTION ACROSS THREE LISTS. Choosing in one clears the others, because two
        // highlighted tiles and one "Use this one" is a window that cannot say what it will do.
        void Chose(ListBox? from, ItemsControl? fromOwn)
        {
            if (!ReferenceEquals(from, grid)) grid.SelectedItem = null;
            if (!ReferenceEquals(from, list)) list.SelectedItem = null;
            if (fromOwn is null) _chosenOwn = null;

            var c = (grid.SelectedItem ?? list.SelectedItem) as Candidate ?? _chosenOwn;
            _chosen = c;
            accept.IsEnabled = c is not null;
            picked.Text = c is null ? "" : c.OwnFile is not null ? c.Name : c.Package;
        }

        grid.SelectionChanged += (_, _) => Chose(grid, null);
        list.SelectionChanged += (_, _) => Chose(list, null);
        grid.DoubleTapped += (_, _) => Take();
        list.DoubleTapped += (_, _) => Take();

        // The own tiles are an ItemsControl, so selection is by click on the tile itself.
        ownList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is Candidate c)
            { _chosenOwn = c; Chose(null, ownList); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        importButton.Click += async (_, _) =>
        {
            if (import is null) return;
            if (await import() is not { } file) return;
            _chosen = new Candidate(System.IO.Path.GetFileName(file), file, file, file);
            Close();
        };

        accept.Click += (_, _) => Take();
        cancel.Click += (_, _) => { _chosen = null; Close(); };

        void Take() { if (_chosen is not null) Close(); }
    }

    private Candidate? _chosen;
    private Candidate? _chosenOwn;

    /// <summary>What they chose, or null when they cancelled.</summary>
    public Candidate? Result => _chosen;

    /// <summary>The package chosen, or null — for a game asset only.</summary>
    public string? Chosen => _chosen is { OwnFile: null } c ? c.Package : null;

    /// <summary>The author's own file, when that is what they chose.</summary>
    public string? ChosenOwn => _chosen?.OwnFile;

    private static void Fetch(Candidate c)
    {
        if (c.Shot is not null) return;

        if (c.OwnFile is { } file)
        {
            // Off disk, not out of the game. TGA does not decode here and stays blank — the name
            // under the tile still says which file it is.
            try { if (System.IO.File.Exists(file)) c.Shot = new Bitmap(file); }
            catch (Exception) { /* a blank tile beats a broken window */ }
            return;
        }

        // The object path is the package plus its leaf again, which is how a texture is named.
        var leaf = c.Package[(c.Package.LastIndexOf('/') + 1)..];
        IconLoader.Want($"{c.Package}.{leaf}", shot => c.Shot = shot, 96);
    }
}

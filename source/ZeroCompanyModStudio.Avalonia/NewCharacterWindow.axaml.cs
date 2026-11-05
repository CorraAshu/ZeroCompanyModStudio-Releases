using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// "A character of your own": who to start from, their name and story, and which pool.
///
/// THE DIALOG COLLECTS AND DECIDES NOTHING. It hands back four strings and a pool, and
/// <see cref="StudioVm.MakeCharacter"/> does the writing — the same method the driver flag calls,
/// so there is one path into the project rather than one for a person and one for a test.
/// </summary>
public sealed class NewCharacterVm : Obs
{
    /// <summary>One of the game's 794, as a row: what it is called and which folder it sits in.</summary>
    public sealed record Row(string Name, string Where, string Package);

    private readonly List<Row> _all;
    private readonly Func<string, Task<(string Rig, bool Bespoke)>> _ask;

    /// <summary>The pools, as the label a person reads and the row a new one is copied from.</summary>
    public IReadOnlyList<(string Label, CharacterIndex.Recruit Template)> PoolRows { get; }

    public NewCharacterVm(IEnumerable<CharacterIndex.Character> characters,
                          IReadOnlyList<(string Label, CharacterIndex.Recruit Template)> pools,
                          Func<string, Task<(string Rig, bool Bespoke)>> ask)
    {
        _all = characters.Select(c => new Row(c.Name, c.Where, c.Package)).ToList();
        _ask = ask;
        PoolRows = pools;
        Pools = pools.Select(p => p.Label).ToList();
        Refilter();
    }

    public IReadOnlyList<string> Pools { get; }

    public ObservableCollection<Row> Matches { get; } = new();

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) Refilter(); }
    }
    private string _filter = "";

    /// <summary>
    /// CAPPED AT 60. Typing one letter matches most of 794, and a list box holding all of them
    /// is slower to draw than the keystroke that follows it. The count is said underneath.
    /// </summary>
    private void Refilter()
    {
        var needle = _filter.Trim();
        var hits = needle.Length == 0
            ? _all
            : _all.Where(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                              r.Where.Contains(needle, StringComparison.OrdinalIgnoreCase))
                  .ToList();

        _found = hits.Count;
        Matches.Clear();
        foreach (var r in hits.Take(60)) Matches.Add(r);
        Raise(nameof(Verdict));
    }
    private int _found;

    public Row? Picked
    {
        get => _picked;
        set
        {
            if (!Set(ref _picked, value)) return;
            Raise(nameof(PickedSays));
            Raise(nameof(CanMake));
            _ = AskTheRig();
        }
    }
    private Row? _picked;

    public string PickedSays => _picked is null
        ? "Type a name to choose who to start from."
        : "Starting from " + _picked.Name;

    public string First
    {
        get => _first;
        set { if (Set(ref _first, value)) { Raise(nameof(CanMake)); Raise(nameof(Verdict)); } }
    }
    private string _first = "";

    public string Last { get => _last; set => Set(ref _last, value); }
    private string _last = "";

    public string Story { get => _story; set => Set(ref _story, value); }
    private string _story = "";

    public int PoolIndex
    {
        get => _poolIndex;
        set { if (Set(ref _poolIndex, value)) Raise(nameof(RigSays)); }
    }
    private int _poolIndex;

    /// <summary>A one-piece body: the game's own databank will show them as NOT EDITABLE.</summary>
    public bool IsBespoke { get => _bespoke; set => Set(ref _bespoke, value); }
    private bool _bespoke;

    public string BespokeSays =>
        "A one piece body: the game's databank will show them as NOT EDITABLE, with no headwear " +
        "or tops to pick. That is how the game treats every such character; their look is " +
        "changed on their own page instead.";

    private string _rig = "";

    /// <summary>
    /// THE RIG DECIDES THE POOL. Read once per template, the pool set from it, and a person may
    /// still override — with the warning below, which is a measurement and not a guess.
    /// </summary>
    private async Task AskTheRig()
    {
        if (_picked is null) { _rig = ""; IsBespoke = false; Raise(nameof(RigSays)); return; }

        var (rig, bespoke) = await _ask(_picked.Package);
        _rig = rig;
        IsBespoke = bespoke;

        if (rig.Length > 0)
        {
            var wants = CharacterAuthor.PawnFor(rig);
            var fit = PoolRows.ToList().FindIndex(
                p => p.Template.Kind.Contains(wants, StringComparison.OrdinalIgnoreCase));
            if (fit >= 0) PoolIndex = fit;
        }

        Raise(nameof(RigSays));
        Raise(nameof(RigWarn));
    }

    public bool RigWarn
    {
        get
        {
            if (_rig.Length == 0 || PoolIndex < 0 || PoolIndex >= PoolRows.Count) return false;
            var wants = CharacterAuthor.PawnFor(_rig);
            return !PoolRows[PoolIndex].Template.Kind
                                       .Contains(wants, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string RigSays
    {
        get
        {
            if (_rig.Length == 0 || PoolIndex < 0 || PoolIndex >= PoolRows.Count) return "";
            var wants = CharacterAuthor.PawnFor(_rig);
            var pool = PoolRows[PoolIndex];
            return RigWarn
                ? $"Their rig is {_rig}, which lives on the {wants.ToLowerInvariant()} pawn, and " +
                  $"{pool.Label} spawns {pool.Template.Kind}. A B1 made this way was INVISIBLE on " +
                  "the squad screen — a floating weapon and no body."
                : $"Chosen for you: their rig is {_rig}, which lives on the " +
                  $"{wants.ToLowerInvariant()} pawn.";
        }
    }

    public bool CanMake => _picked is not null && _first.Trim().Length > 0 && PoolRows.Count > 0;

    public string Verdict => PoolRows.Count == 0
        ? "The hiring pool could not be read, so there is nowhere to put them."
        : _picked is null
            ? $"{_found} character(s) match" + (_found > 60 ? " — showing the first 60" : "")
            : _first.Trim().Length == 0
                ? "Give them a first name."
                : $"{_found} character(s) match" + (_found > 60 ? " — showing the first 60" : "");
}

public partial class NewCharacterWindow : Window
{
    private readonly NewCharacterVm _vm;

    /// <summary>What was chosen, or null when the dialog was dismissed.</summary>
    public (string Template, string First, string Last, string Story,
            CharacterIndex.Recruit Pool)? Chosen { get; private set; }

    public NewCharacterWindow(IEnumerable<CharacterIndex.Character> characters,
                              IReadOnlyList<(string, CharacterIndex.Recruit)> pools,
                              Func<string, Task<(string, bool)>> ask)
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new NewCharacterVm(characters, pools, ask);
        DataContext = _vm;
        Opened += (_, _) => this.FindControl<TextBox>("FindBox")?.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnMake(object? sender, RoutedEventArgs e)
    {
        if (_vm.Picked is not { } picked || !_vm.CanMake) return;
        var at = Math.Clamp(_vm.PoolIndex, 0, _vm.PoolRows.Count - 1);
        Chosen = (picked.Package, _vm.First.Trim(), _vm.Last.Trim(), _vm.Story,
                  _vm.PoolRows[at].Template);
        Close();
    }
}

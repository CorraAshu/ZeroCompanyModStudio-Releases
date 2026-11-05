using System.Collections.ObjectModel;
using Avalonia;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// One line of the Properties panel.
///
/// FLAT, WITH ITS OWN DEPTH, rather than a TreeView. A TreeView indents the whole row, so every
/// nesting level drags the value column right with it and the panel reads as a staircase — and
/// the reference this is modelled on keeps its values in one column whatever the depth. So the
/// indent is a margin on the NAME, the value sits in a column of its own, and what is on screen
/// is rebuilt from the expanded state.
/// </summary>
public sealed class PropRow : Obs
{
    private readonly PropNode _node;
    private bool _expanded;

    public PropRow(PropNode node, int depth, string trail)
    {
        _node = node;
        Depth = depth;
        Trail = trail;
        // Exports open, their contents shut. Opening everything on a character Blueprint is six
        // hundred rows nobody asked for; opening nothing is a panel that looks empty.
        _expanded = depth == 0;
    }

    public PropNode Node => _node;
    public int Depth { get; }

    /// <summary>
    /// The row this one hangs under, or null for an export.
    ///
    /// ON THE ROW rather than only in the tab's map of children, because the thing that needs it
    /// is an ENTRY asking which array it is in: a removal is written against the ARRAY's export
    /// and property, and an entry carries neither -- its own Export is empty, because only a
    /// depth-zero node names one.
    /// </summary>
    public PropRow? Parent { get; internal set; }

    /// <summary>Where this row sits, for the flat list the filter produces.</summary>
    public string Trail { get; }

    /// <summary>
    /// Show the trail beside the name — true only while a filter has flattened the tree.
    ///
    /// ON THE ROW, not read off the owning tab. The template used to reach up with
    /// `$parent[ListBox].((local:AssetTab)DataContext).Filtering`, and that cast is what tied one
    /// property grid to one tab type: the mesh viewport's Properties tab could not use the same
    /// markup without pretending to be an AssetTab. A row knowing whether it is being shown as
    /// part of a flat search result is the row's own business anyway.
    /// </summary>
    private bool _showTrail;
    public bool ShowTrail { get => _showTrail; set => Set(ref _showTrail, value); }

    private bool _showChevron;

    /// <summary>
    /// Whether to draw a fold arrow at all.
    ///
    /// NOT SIMPLY <see cref="HasChildren"/>. While the filter box has anything in it the list is
    /// FLAT -- every matching row, wherever it came from -- so there is no tree there to fold and
    /// <see cref="AssetTab.Rebuild"/>'s filtering branch never reads IsExpanded. The arrow still
    /// flipped, because the glyph follows IsExpanded and that still toggled: an arrow that
    /// answered a click by changing shape and doing nothing. That is the author's "sometimes the
    /// arrow doesn't open or collapse", and the honest answer is not to offer one.
    /// </summary>
    public bool ShowChevron { get => _showChevron; set => Set(ref _showChevron, value); }

    /// <summary>The raw identifier — what a mod project and an edit refer to.</summary>
    public string Name => _node.Name;

    /// <summary>
    /// What the NAME COLUMN shows: UE5's own display name, with the raw one on hover.
    ///
    /// The editor prints "Slot Can Be Empty" and a modder reading a guide sees that; the
    /// identifier is still what an edit is written against, so it is a tooltip rather than gone.
    /// </summary>
    public string Label => _node.Label;
    public string Value => _node.Value;
    public string Note => _node.Note;
    public bool HasChildren => _node.HasChildren;

    public bool IsExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) Raise(nameof(Chevron)); }
    }

    public Avalonia.Media.Geometry? Chevron => !HasChildren ? null : Chevrons.For(_expanded);

    /// <summary>
    /// The faint word beside the NAME: an export's CLASS, or a property's TYPE.
    ///
    /// A property's note is a count, and a count belongs on the value side — that is where the
    /// reference puts "(4 items)", and it is the reason its value column is never blank on a row
    /// that holds something. This space was therefore empty on every row but an export's.
    ///
    /// THE TYPE IS WHAT BELONGS THERE. Somebody who had opened this asset in UE5 would be looking
    /// at a checkbox, a dropdown or an asset picker, and the type is the first half of telling
    /// them apart — so it is spelled UE's way, `FGameplayTag` and `TArray&lt;TSoftObjectPtr&gt;`
    /// rather than the serialiser's `StructProperty` and `ArrayProperty`.
    /// </summary>
    public string Tag => Depth == 0 ? Note : _node.UeType;

    /// <summary>
    /// The value column: the value itself, or what is in there when there is no value.
    ///
    /// `(default)` is NOT "empty". A cooked asset serialises only what differs from its class
    /// default, so a property left alone has no bytes at all — and until now that was
    /// indistinguishable from a property the class does not have, which is to say invisible. The
    /// row is drawn because the property EXISTS; the word says the file is silent about it.
    /// </summary>
    public string Shown => _edited is { } e ? e
        : !Stored ? "(default)"
        : Value.Length > 0 ? Value
        : Depth > 0 && Note.Length > 0 ? $"({Note})"
        : "";

    /// <summary>Whether the file carries this property at all. See <see cref="PropNode.Stored"/>.</summary>
    public bool Stored => _node.Stored;

    /// <summary>
    /// The class that declares this property, when it is NOT the export's own.
    ///
    /// UE5 groups a Details panel by CATEGORY, and no category survived cooking — there is no
    /// UClassCookedMetaData in any package this game ships, and a usmap carries no metadata at
    /// all. The declaring class is the honest substitute: it is a real fact about the property
    /// rather than a grouping invented here, and it is what the ordering follows.
    /// </summary>
    public string DeclaredBy => _node.DeclaredBy;

    public bool ShowDeclaredBy => _node.DeclaredBy.Length > 0;

    // ------------------------------------------------------------------ editing

    private string? _edited;
    private bool _editing;
    private string _draft = "";

    /// <summary>Worth offering a box for. See PropNode.Editable — it is a candidate, not a promise.</summary>
    public bool Editable => _node.Editable;

    /// <summary>The number an edit has to say was there, for ScalarEdit to check before writing.</summary>
    public double Was => _node.Number ?? 0;

    public string Export => _node.Export;

    /// <summary>This mod changes it. Drawn in the accent colour, as the reference draws its own.</summary>
    public bool Edited => _edited is not null;

    /// <summary>Show the edited value, and remember it, once the build has agreed it can be made.</summary>
    public void MarkEdited(string value)
    {
        _edited = value;
        Raise(nameof(Edited));
        Raise(nameof(Shown));
        Raise(nameof(ShowPlain));
        Raise(nameof(ShowEdited));
        Raise(nameof(Ticked));
        Raise(nameof(Chosen));
    }

    public void ClearEdit()
    {
        _edited = null;
        Raise(nameof(Edited));
        Raise(nameof(Shown));
        Raise(nameof(ShowPlain));
        Raise(nameof(ShowEdited));
        Raise(nameof(Ticked));
        Raise(nameof(Chosen));
    }

    /// <summary>Whether the value cell is a box right now.</summary>
    public bool Editing
    {
        get => _editing;
        set
        {
            if (!Set(ref _editing, value)) return;
            Raise(nameof(NotEditing));
            Raise(nameof(ShowPlain));
            Raise(nameof(ShowEdited));
        }
    }

    public bool NotEditing => !_editing;

    /// <summary>
    /// Which of the two value labels shows. TWO COMPUTED FLAGS rather than a MultiBinding: the
    /// converter would need its own xmlns, and a binding nobody can read at a glance is how a
    /// panel ends up showing two labels at once.
    /// </summary>
    public bool ShowPlain => !_editing && _edited is null && !IsBool && !IsEnum;
    public bool ShowEdited => !_editing && _edited is not null && !IsBool && !IsEnum;

    // ------------------------------------------------------------------ typed editors

    /// <summary>
    /// A BOOL GETS A CHECKBOX, and it needs no new machinery to do it.
    ///
    /// A true bool is a single byte on disk, which is how the write side has always seen one --
    /// PropNode.AsNumber maps it to 1 or 0 and ScalarEdit writes the byte. So a checkbox is an
    /// affordance over the number path that already works, not a new kind of change, and it
    /// records exactly the ModOverride.NumberChange a typed box would have.
    ///
    /// FALSE -> TRUE OFTEN CANNOT BE WRITTEN and the box is offered anyway. Unversioned
    /// serialisation stores no bytes at all for a property at its default, so there is nothing to
    /// overwrite; the rehearsal is what decides, and it says so in the log. Hiding the box would
    /// hide the property.
    /// </summary>
    public bool IsBool => _node.Type == "BoolProperty" && Address is not null;

    /// <summary>What the box shows: the edit if there is one, otherwise the file's value.</summary>
    public bool Ticked
    {
        get => _edited is { } e ? e is "1" or "True" or "true" : Was != 0;
        set { _ = value; }   // the click handler does the work; this keeps the binding two-way
    }

    /// <summary>
    /// AN ENUM GETS A DROPDOWN, and like the checkbox it is an affordance over the number path
    /// rather than a new kind of change: the member's value is the byte on disk.
    /// </summary>
    public bool IsEnum => _node.Choices.Count > 0 && Address is not null;

    public IReadOnlyList<string> Options
        => _node.Choices.Select(c => AssetProps.LeafOfEnum(c.Name)).ToList();

    /// <summary>Which member is showing: the edit if there is one, otherwise the file's.</summary>
    public string? Chosen
    {
        get
        {
            var want = AssetProps.LeafOfEnum(_edited ?? Value);
            return Options.FirstOrDefault(o => string.Equals(o, want, StringComparison.OrdinalIgnoreCase));
        }
        set { _ = value; }   // the handler does the work, as with the checkbox
    }

    /// <summary>The number behind a member name, for the change the build records.</summary>
    public long? ValueOf(string member)
        => _node.Choices.FirstOrDefault(c => string.Equals(
               AssetProps.LeafOfEnum(c.Name), member, StringComparison.OrdinalIgnoreCase)) is
           { Name.Length: > 0 } hit ? hit.Value : null;

    /// <summary>An FName, which is written as a value rather than as a number.</summary>
    public bool IsName => _node.Type == "NameProperty" && Address is not null;

    /// <summary>An FString — the one edit whose LENGTH changes, so the one that splices.</summary>
    public bool IsString => _node.Type == "StrProperty" && Address is not null;

    /// <summary>
    /// An FText: a display name, a description, anything the game puts in front of a player in
    /// words.
    ///
    /// EDITING ONE REPLACES ITS SHAPE, not its characters. Almost every FText the game ships is a
    /// StringTableEntry -- a table and a key, with no text in it at all -- so there is nothing to
    /// type over. Writing one turns it into a literal that carries its own words. See
    /// PropertyAdd.SetText for the bytes and for why the key has to be replaced along with them.
    /// </summary>
    public bool IsText => _node.Type == "TextProperty" && Address is not null;

    /// <summary>The package this row's value names, if it names one.</summary>
    public string? Target => AssetProps.PackagePathIn(Value);

    /// <summary>
    /// An array of SOFT references — the one array shape that can be grown or shrunk, so the one
    /// that gets a + button.
    ///
    /// SOFT, NOT HARD. A soft entry is always the same twenty bytes: a package FName, an asset
    /// FName and an empty sub-path. So the count and the entries are all there is and adding one
    /// is arithmetic. A hard ObjectProperty array holds 4-byte package indices that each need an
    /// import map entry, and a struct array's entry size depends on which of its properties
    /// happen to be stored -- both are different writers, and neither exists. Offering a button
    /// those writers cannot honour would be worse than offering none.
    /// </summary>
    public bool IsRefArray => _node.Type == "ArrayProperty" &&
                              _node.Detail == "SoftObjectProperty" &&
                              Address is not null;

    /// <summary>One entry of such an array — what a × takes out.</summary>
    public bool IsRefEntry => Parent is { IsRefArray: true } && Target is not null;

    /// <summary>
    /// An array of STRUCTS, which can be grown by COPYING an element and shrunk by dropping one.
    ///
    /// No picker and no + that invents an entry: a struct element carries an unversioned header
    /// saying which of its properties are present, and one written from nothing is a guess at a
    /// shape the cooker would have chosen. Copying an element the game itself wrote gives a
    /// header that is known good, and the copy is then edited field by field like any other row.
    /// See PropertyAdd.SetStructEntry.
    /// </summary>
    public bool IsStructArray => _node.Type == "ArrayProperty" &&
                                 _node.Detail == "StructProperty" &&
                                 Address is not null;

    /// <summary>One element of a struct array — what a × drops and a + copies.</summary>
    public bool IsStructEntry => Parent is { IsStructArray: true };

    /// <summary>Which element this is, read off its own name: "[2]" is 2.</summary>
    public int ElementIndex
        => Name.Length > 2 && Name[0] == '[' && Name[^1] == ']' &&
           int.TryParse(Name[1..^1], out var n) ? n : -1;

    /// <summary>
    /// The export this row belongs to and the PATH to it inside that export, or null when it
    /// belongs to no export at all.
    ///
    /// WHY A PATH AND NOT A NAME. Only a top-level property carries an export; everything under
    /// one -- an array's elements, a struct's fields, an array inside one of those -- carries
    /// nothing, so an edit aimed at it had no address to be written against. That is why the
    /// soft-reference buttons only ever appeared on top-level arrays.
    ///
    /// Locate has taken a dotted, indexed path all along -- `A[0].B[1].C` is how an ability's
    /// numbers are reached -- so the address exists as soon as the row can spell it. Walking up
    /// the parents until one carries an export spells it.
    /// </summary>
    public (string Export, string Path)? Address
    {
        get
        {
            var bits = new List<string>();
            for (var r = this; r is not null; r = r.Parent)
            {
                bits.Insert(0, r.Name);
                if (r.Export.Length == 0) continue;

                // An index is part of the name before it -- "A" then "[0]" is "A[0]" -- and
                // anything else is a step down, so a dot.
                var path = bits[0];
                foreach (var b in bits.Skip(1))
                    path += b.StartsWith('[') ? b : "." + b;
                return (r.Export, path);
            }
            return null;
        }
    }

    /// <summary>The array an entry belongs to, which is what an edit is written against.</summary>
    public PropRow? OwningArray => IsRefEntry ? Parent : null;

    private string _pending = "";

    /// <summary>
    /// What this mod has added to or taken out of this array, shown on the array's own row.
    ///
    /// WITHOUT THIS THE BUTTON LOOKS BROKEN. The grid reads the GAME's bytes, so an entry added
    /// to a mod does not appear under the array — click +, choose an asset, and nothing on screen
    /// moves. That is the shape of every fault this project keeps finding: work done correctly
    /// and nothing to witness it. The count beside it stays the file's, because it IS the file's.
    /// </summary>
    public string Pending
    {
        get => _pending;
        set { if (Set(ref _pending, value)) Raise(nameof(ShowPending)); }
    }

    public bool ShowPending => _pending.Length > 0;

    /// <summary>
    /// Whether to offer the follow arrow.
    ///
    /// The author's word for what was missing: *"it doesn't let you reference other
    /// files to edit them"*. A reference that cannot be followed is a dead end in a reader whose
    /// whole job is to let somebody find the thing they actually want to change.
    /// </summary>
    public bool CanFollow => Target is not null;

    public string Draft { get => _draft; set => Set(ref _draft, value); }

    /// <summary>
    /// Whether this row takes a typed edit — the ROW's answer, not the node's.
    ///
    /// PropNode.Editable asks whether the node carries an export, and only a top-level property
    /// does. So every property INSIDE a struct — and that is where a great deal of what anyone
    /// wants to change lives — reported itself uneditable and refused with a sentence about
    /// numbers that was not the reason. Address is the row's real address and works at any depth.
    /// </summary>
    public bool CanEdit => Address is not null &&
                           (Node.Number is not null ||
                            Node.Type is "NameProperty" or "StrProperty" or "TextProperty");

    public void BeginEdit()
    {
        if (!CanEdit) return;
        Draft = Shown;
        Editing = true;
    }

    /// <summary>The indent, as a margin on the name so the value column does not move.</summary>
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
}

/// <summary>
/// ONE OPEN ASSET, as a tab.
///
/// The author's shape, from the reference: *"if you click something that is data … it'll open it
/// as new tab as you can see in the top above the data explorer"*. Tabs are a collection on the
/// view model rather than TabItems in the XAML, because feature editors have to be able to open
/// in the same strip later — he wants the guided route and this one side by side, and a strip
/// that can only hold assets puts the row of nine buttons back.
/// </summary>
public sealed class AssetTab : StudioTab
{
    private AssetRead? _read;
    private string _filter = "";
    private bool _loading = true;
    private string _status = "reading…";

    /// <summary>What the status line says when nothing is typed in Filter.</summary>
    private string _summary = "";

    public AssetTab(string title, string filePath, string type)
    {
        Title = title;
        FilePath = filePath;
        Type = type;
    }

    public override string Title { get; }

    /// <summary>The whole path, on the tab's tooltip — the title is only the leaf.</summary>
    public override string Tooltip => FilePath;

    public string FilePath { get; }
    public string Type { get; }

    /// <summary>What is on screen: the tree honouring expansion, or the filter's matches.</summary>
    public ObservableCollection<PropRow> Rows { get; } = new();

    public bool Loading { get => _loading; private set => Set(ref _loading, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The package, and the container it was actually served from.</summary>
    public string Provenance => _read is null ? FilePath
        : _read.Container is { } c ? $"{_read.PackagePath}   ·   read from {c}"
        : _read.PackagePath;

    public bool Failed => _read?.Error is not null;
    public string Error => _read?.Error ?? "";

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) { Raise(nameof(Filtering)); Rebuild(); } }
    }

    public bool Filtering => _filter.Trim().Length > 0;

    /// <summary>
    /// The row picked in the panel. Held here because a ContextMenu's DataContext is the LIST's,
    /// not the row's, so a menu item cannot reach the row it was opened over any other way.
    /// </summary>
    public PropRow? SelectedRow { get => _selectedRow; set => Set(ref _selectedRow, value); }
    private PropRow? _selectedRow;

    /// <summary>All rows, flattened once at load, so expanding costs no re-walk.</summary>
    private readonly List<PropRow> _all = new();

    /// <summary>The children of each row, by row. Held here so PropRow stays a view of a node.</summary>
    private readonly Dictionary<PropRow, List<PropRow>> _kids = new();

    /// <summary>
    /// EVERY row, not the ones scrolled into view.
    ///
    /// `Rows` is the VISIBLE list — what the tree emits for the folds that happen to be open —
    /// and an edit aimed at a property must not depend on that. Looking for one in `Rows`
    /// reported "no rows carry that export at all" for an export whose property was read
    /// perfectly well, which reads exactly like the reader having missed it.
    /// </summary>
    public IReadOnlyList<PropRow> AllRows => _all;

    /// <summary>
    /// What is UNDER a row, whether or not it is open.
    ///
    /// Rows holds only what is on screen, so a fold that is shut has its contents nowhere a
    /// caller can reach them — and an array's entries are exactly what an add or a remove has to
    /// name.
    /// </summary>
    public IReadOnlyList<PropRow> ChildrenOf(PropRow row)
        => _kids.TryGetValue(row, out var kids) ? kids : [];

    public void Fill(AssetRead read)
    {
        _read = read;
        _all.Clear();
        _kids.Clear();

        foreach (var e in read.Exports) Build(e, 0, "", null);

        Loading = false;
        _summary = read.Error is not null
            ? $"could not read it: {read.Error}"
            : read.Exports.Count == 1
                ? $"1 export   ·   {read.PropertyCount:N0} properties{AtDefault(read)}"
                : $"{read.Exports.Count} exports   ·   {read.PropertyCount:N0} properties{AtDefault(read)}";
        Status = _summary;

        Raise(nameof(Provenance));
        Raise(nameof(Failed));
        Raise(nameof(Error));
        Rebuild();
    }

    /// <summary>How many rows are the class talking rather than the file. Silent when none are.</summary>
    private static string AtDefault(AssetRead read)
        => read.DefaultCount == 0 ? "" : $"   ·   {read.DefaultCount:N0} at default";

    private PropRow Build(PropNode node, int depth, string trail, PropRow? parent)
    {
        var row = new PropRow(node, depth, trail) { Parent = parent };
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PropRow.IsExpanded)) Rebuild(); };
        _all.Add(row);

        if (parent is not null)
        {
            if (!_kids.TryGetValue(parent, out var list)) _kids[parent] = list = new List<PropRow>();
            list.Add(row);
        }

        var under = trail.Length == 0 ? node.Name : $"{trail} › {node.Name}";
        foreach (var c in node.Children) Build(c, depth + 1, under, row);
        return row;
    }

    /// <summary>
    /// What to show: the visible part of the tree, or — while something is typed — every row that
    /// matches, flat and carrying where it came from.
    ///
    /// Flat rather than a pruned tree for the same reason the explorer's filter is: somebody
    /// typing a property name did not want to navigate to it.
    /// </summary>
    private void Rebuild()
    {
        Rows.Clear();

        if (Filtering)
        {
            var want = _filter.Trim();
            var hits = 0;
            foreach (var r in _all)
            {
                // EITHER NAME. Showing "Slot Can Be Empty" while matching only `bSlotCanBeEmpty`
                // would make the filter fail on the words actually on screen.
                if (r.Name.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                    r.Label.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                    r.Value.Contains(want, StringComparison.OrdinalIgnoreCase))
                { r.ShowTrail = true; r.ShowChevron = false; Rows.Add(r); hits++; }
            }
            Status = hits == 0 ? $"nothing matching “{want}”"
                   : hits == 1 ? "1 match" : $"{hits:N0} matches";
            return;
        }

        // BACK TO THE SUMMARY when the box is cleared. Leaving "3 matches" up after the filter is
        // gone describes a list that is no longer on screen.
        Status = _summary;

        foreach (var r in _all) { r.ShowTrail = false; r.ShowChevron = r.HasChildren; }
        foreach (var e in _all.Where(r => r.Depth == 0)) Emit(e);

        void Emit(PropRow row)
        {
            Rows.Add(row);
            if (!row.IsExpanded || !_kids.TryGetValue(row, out var kids)) return;
            foreach (var k in kids) Emit(k);
        }
    }
}

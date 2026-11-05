using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE MISSION, open as a tab.
///
/// A mission is DATA — planet, faction, objective tag, squad composition, era — which is the
/// finding this whole area rests on, so the sheet is that data rather than a summary of it.
/// Read from <see cref="MissionIndex"/>, which the ImGui mission screen also reads.
/// </summary>
public sealed class MissionTab : SheetTab
{
    private readonly MissionIndex.Mission _m;

    public MissionTab(MissionIndex.Mission mission, ModMission? mine = null)
    {
        _m = mission;
        Mine = mine;
        _title = mission.Display.Length > 0 ? mission.Display : mission.Leaf;
    }

    private readonly string _title;

    /// <summary>
    /// THE MOD'S OWN MISSION, when this tab is editing one rather than reading the game's.
    ///
    /// ONE TAB TYPE FOR BOTH, as PartTab already does for a part and its copy. What differs is
    /// which cards are present and where a change lands; the sheet on the right is the thing it
    /// was COPIED FROM either way, which is exactly what somebody editing a copy wants beside
    /// them.
    /// </summary>
    public ModMission? Mine { get; }

    public override string Title =>
        Mine is { } mine && mine.Display.Length > 0 ? mine.Display
        : Mine is { } m2 && m2.Id.Length > 0 ? m2.Id
        : _title;

    public override string Tooltip => Mine is { } mine
        ? $"{mine.Id} — {(mine.InPlace ? "editing the game's own" : "your own copy")} of {_m.Package}"
        : _m.Package;

    public override string Subtitle => Mine is { } mine ? mine.Id : _m.Leaf;

    public override string Description => Strip(_m.ShortDescription);

    public override string Aside => Mine is null ? ""
        : Mine.InPlace ? $"editing the game's own {_m.Leaf} in place"
                       : $"your own copy of {_m.Leaf}";

    public override bool IsCopy => Mine is not null;
    public override bool CanEdit => Mine is not null;

    public override string EditBadge =>
        Mine is null ? "EDITING THE GAME'S OWN"
        : Mine.InPlace ? "REPLACING THE GAME'S OWN" : "YOUR OWN MISSION";

    public override string EditWarning =>
        Mine is null ? base.EditWarning
        : Mine.InPlace
            ? "This REPLACES a mission the game ships. While your mod is installed that mission " +
              "is yours, not theirs — which is what makes it the quickest thing to test, because " +
              "it is already on the galaxy map and needs no roll to appear."
            : "A mission of your own, beside the game's. Nothing of the game's changes — but " +
              "until something offers it, nothing will draw it: the galaxy map picks from " +
              "weighted tables and turn slots, not from what exists.";

    /// <summary>Title and subtitle follow the fields, so a save re-reads them.</summary>
    public void Renamed()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(Tooltip));
        Raise(nameof(Aside));
        Raise(nameof(EditBadge));
        Raise(nameof(EditWarning));
    }

    public MissionIndex.Mission Mission => _m;

    public override string Headline
    {
        get
        {
            var bits = new List<string>();
            if (_m.Faction.Length > 0) bits.Add(_m.Faction);
            if (_m.Type.Length > 0) bits.Add(_m.Type);
            if (_m.Planet is { Length: > 0 } p) bits.Add(p);
            return string.Join("   ·   ", bits);
        }
    }

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Faction", _m.Faction);
            Fact("Type", _m.Type);
            Fact("Planet", _m.Planet);
            Fact("Region", _m.RegionTag);
            Fact("Level", Leaf(_m.Level));
            // A SQUAD OF 0 TO 0 IS NOT A SQUAD SIZE, it is a mission that does not say. Printing
            // it as a range would invent a fact the asset does not carry.
            if (_m.MinSquad > 0 || _m.MaxSquad > 0)
                Fact("Squad", _m.MinSquad == _m.MaxSquad
                    ? $"{_m.MinSquad}" : $"{_m.MinSquad} to {_m.MaxSquad}");
            if (_m.LifeSpan > 0) Fact("Lifespan", $"{_m.LifeSpan} turn(s)");
            blocks.Add(new SheetFact { Label = "Package", Value = _m.Package, Wide = true });

            if (_m.LongDescription.Length > 0)
            {
                blocks.Add(new SheetBand { Text = "Briefing" });
                blocks.Add(new SheetBullet { Text = Strip(_m.LongDescription) });
            }

            if (_m.Objectives.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Objectives ({_m.Objectives.Count})" });
                foreach (var o in _m.Objectives) blocks.Add(new SheetBullet { Text = ShortTag(o) });
            }

            if (_m.MissionTags.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Tags ({_m.MissionTags.Count})" });
                foreach (var t in _m.MissionTags) blocks.Add(new SheetBullet { Text = ShortTag(t) });
            }

            var rules = new List<string>();
            if (_m.BlockActiveDuplicates) rules.Add("cannot run twice at once");
            if (_m.BlockCompletedDuplicates) rules.Add("cannot run again once completed");
            if (rules.Count > 0)
            {
                blocks.Add(new SheetBand { Text = "Rules" });
                foreach (var r in rules) blocks.Add(new SheetBullet { Text = r });
            }

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }
        }
    }
}

/// <summary>
/// ONE RECRUIT ROW, open as a tab.
///
/// A recruit is a ROW in a pool table pointing at a definition and a pawn — which is why the
/// hiring pool can roll a specialization away whatever the data says, and why what this sheet
/// shows is the row's chain rather than a character.
/// </summary>
public sealed class RecruitTab : SheetTab
{
    private readonly CharacterIndex.Recruit _r;

    public RecruitTab(CharacterIndex.Recruit recruit)
    {
        _r = recruit;
        Title = recruit.Name.Length > 0 ? recruit.Name : recruit.Row;
    }

    public override string Title { get; }
    public override string Tooltip => _r.Pool;
    public override string Subtitle => _r.Row;
    public override string Description => Strip(_r.Story);
    public CharacterIndex.Recruit Recruit => _r;

    /// <summary>
    /// What THIS MOD does to this row, or "" -- the reader half of "take their place".
    ///
    /// FILLED BY THE VIEW MODEL, because a tab knows the game's row and nothing about the open
    /// project. And refreshed on every edit rather than only when the tab is opened: a swap
    /// written while this tab is in front would otherwise leave the sheet saying nothing had
    /// happened, which is the one fault shape this Studio keeps finding.
    /// </summary>
    public string TakenBy
    {
        get => _takenBy;
        set { if (Set(ref _takenBy, value)) Raise(nameof(Sheet)); }
    }
    private string _takenBy = "";

    public override string Headline
    {
        get
        {
            var bits = new List<string>();
            if (_r.Species.Length > 0) bits.Add(_r.Species);
            if (_r.CharacterClass.Length > 0) bits.Add(_r.CharacterClass);
            if (!_r.Active) bits.Add("not in the rotation");
            return string.Join("   ·   ", bits);
        }
    }

    public override string Problem => _r.Complete
        ? ""
        : $"This row points at {_r.Missing.Count} thing(s) that are not there, so there is "
          + "nothing complete to duplicate.";

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Pool", _r.PoolLeaf);
            Fact("Row", _r.Row);
            Fact("Species", _r.Species);
            Fact("Class", _r.CharacterClass);
            Fact("Kind", _r.Kind);
            Fact("In the rotation", _r.Active ? "yes" : "no");
            blocks.Add(new SheetFact { Label = "Pool table", Value = _r.Pool, Wide = true });

            blocks.Add(new SheetBand { Text = "The chain" });
            Link("Definition", _r.Definition);
            Link("Pawn", _r.Pawn);
            Link("Name part", _r.NamePart);

            if (_takenBy.Length > 0)
            {
                blocks.Add(new SheetBand { Text = "This mod takes this place", Accent = true });
                blocks.Add(new SheetBullet { Text = _takenBy });
            }

            if (_r.Missing.Count > 0)
            {
                blocks.Add(new SheetBand
                { Text = $"Missing from the chain ({_r.Missing.Count})", Accent = true });
                foreach (var m in _r.Missing) blocks.Add(new SheetBullet { Text = m });
            }

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }

            void Link(string label, string package)
            {
                blocks.Add(new SheetPair
                {
                    Left = label,
                    Right = package.Length > 0 ? Leaf(package) : "— none",
                    Band = band,
                });
                band = !band;
            }
        }
    }
}

/// <summary>
/// ONE ABILITY, open as a tab.
///
/// The index carries a name, a folder and a package and nothing else — so this sheet is short and
/// says so, rather than padding itself with fields the index never read. What an ability actually
/// DOES lives in the GE_ effect it wraps, and reaching that is its own piece of work.
/// </summary>
public sealed class AbilityTab : SheetTab
{
    private readonly AbilityIndex.Entry _a;

    public AbilityTab(AbilityIndex.Entry entry)
    {
        _a = entry;
        Title = entry.Name;
    }

    public override string Title { get; }
    public override string Tooltip => _a.Package;
    public override string Subtitle => _a.Folder;
    public AbilityIndex.Entry Ability => _a;

    /// <summary>
    /// WHAT WAS READ FOR THIS ABILITY, kept on its own tab. It used to be one field on the window,
    /// overwritten by whichever ability was filled last, so with two ability tabs open a number
    /// typed on the first was recorded against the SECOND's package (measured: Stacks typed on
    /// GA_Ambush_T1 became a row naming Default__GA_Ambush_T1_C on GA_Aftershock_T1's override,
    /// with a "was" of 0). A box writes through the reading of the tab it is on.
    /// </summary>
    public AbilityEffects.Reading? Reading { get; set; }

    /// <summary>The tiers read for this ability, kept on its tab for the same reason as <see cref="Reading"/>.</summary>
    public AbilityProgression.Family? Family { get; set; }

    /// <summary>
    /// An ability is editable through its PROGRESSION — every tier and every number behind it.
    ///
    /// Not through a name or a mesh: an ability has neither. What it has is a family of tiers,
    /// and what an author wants is to change what tier four of Firestorm costs.
    /// </summary>
    public override bool CanEdit => true;

    public override IReadOnlyList<object> Sheet => new object[]
    {
        new SheetFact { Label = "Folder", Value = _a.Folder },
        new SheetFact { Label = "Package", Value = _a.Package, Wide = true },
        new SheetBand { Text = "What it does" },
        new SheetBullet
        {
            Text = "A GA_ ability wraps a GE_ effect, and the numbers live in the effect — "
                 + "which is why Progression reaches into both.",
        },
    };
}

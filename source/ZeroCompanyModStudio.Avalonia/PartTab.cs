using Avalonia.Media.Imaging;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE CLOTHING PART, OPEN AS A TAB.
///
/// The author's shape: *"... you can click it and it
/// will be put in the tabs area with a x at the end"*. So a part opens beside the assets and
/// several can be open at once.
///
/// THE SHEET IS THE IMGUI STUDIO'S, ARRANGED BETTER — he said so: *"you showed it a bit better in
/// the old mod studio"*. That panel's own decisions are kept rather than approximated: a slot tag
/// shown as its last two segments ("Helmet / Mesh"), the gate written as a sentence somebody can
/// read instead of the one-word code, Family beside Class, the meshes as a body-type table, and
/// the raw tags kept but folded away at the bottom.
///
/// What it READS is PartDetails' answer, not a second reading of the asset — the same source the
/// ImGui part screens use, so the two cannot disagree about a part.
/// </summary>
public sealed class PartTab : SheetTab
{
    private readonly PartsCatalog.Entry _entry;
    private Bitmap? _icon;
    private bool _tagsOpen;

    public PartTab(PartsCatalog.Entry entry)
    {
        _entry = entry;
        _title = entry.Label.Length > 0 ? entry.Label : entry.Part.Name;

        // THE PART'S OWN PROPERTIES, as the same grid the asset reader uses.
        //
        // "Its numbers" is not the old studio's numbers box: that one reads attribute tables and
        // ability effects, and a clothing part is neither, so it opened empty. A part's numbers
        // are the properties on its own export — and editing those already exists here, rehearsed
        // against the build's own write by PropertyEdit.Try. One grid, one meaning.
        _props = new AssetTab(_title, ModBuild.ContainerPathFor(entry.Part.Path), "Part");

        if (entry.Detail.IconPath is { Length: > 0 } icon)
            IconLoader.Want(icon, b =>
            {
                _icon = b;
                Raise(nameof(Icon));
                Raise(nameof(HasIcon));
                Raise(nameof(HasPicture));
            });
    }

    /// <summary>The part's own properties, filled when the editor opens.</summary>
    private readonly AssetTab _props;
    public override AssetTab? Props => _props;

    /// <summary>
    /// THE COPY THIS TAB IS EDITING, when it is editing one.
    ///
    /// His words: "This should all translate to duplication as well." The panel is the same
    /// panel — the same cards, the same rows, the same buttons — and what changes is WHERE a
    /// change is written. Editing the game's part writes a ModOverride that rewrites the game's
    /// package; editing a duplicate writes the ModPart the mod ships beside it.
    ///
    /// The seam is here rather than in the markup on purpose: two copies of the sheet would be
    /// two places for the cards to drift, and the difference is not what the author sees, it is
    /// where it lands.
    /// </summary>
    public ModPart? Copy { get; private set; }

    public override bool IsCopy => Copy is not null;

    /// <summary>
    /// THE TAB BECOMES THE COPY'S, so everything that identifies it has to change with it.
    ///
    /// His report: "it's called Clone Scout Torso (mod) but the very top just says clone". The
    /// panel opened on the copy and then went on naming the GAME part it was made from — on the
    /// tab, in the title, and in the asset id under it. Nothing the author had done was visible
    /// anywhere in the header, which reads as a copy that lost its edits.
    /// </summary>
    public void EditingCopy(ModPart copy)
    {
        Copy = copy;
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
        Raise(nameof(Aside));
        Raise(nameof(HasAside));
        Raise(nameof(Description));
        Raise(nameof(HasDescription));
        Raise(nameof(Tooltip));
        Raise(nameof(IsCopy));
        Raise(nameof(EditBadge));
        Raise(nameof(PictureBlurb));
    }

    /// <summary>The copy's own name and id, raised after a save that changed either.</summary>
    public void CopyRenamed()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
    }

    /// <summary>
    /// The properties have arrived: light the section, and say how many there are.
    ///
    /// ABSENT WHEN THERE ARE NONE — a part whose export this cannot read contributes no numbers
    /// card rather than an empty one, which is the rule every other section here follows.
    /// </summary>
    public void NumbersRead()
    {
        SecNumbers.Present = _props.Rows.Count > 0;
        SecNumbers.Count = _props.Rows.Count > 0 ? _props.Rows.Count.ToString() : "";
        Raise(nameof(HasNumbers));
    }

    public bool HasNumbers => _props.Rows.Count > 0;

    /// <summary>What the copy calls itself, or the game part's label when this is not one.</summary>
    public override string Title => Copy is { Display.Length: > 0 } c ? c.Display : _title;

    private readonly string _title;

    public override string Tooltip =>
        Copy is { } copy ? $"{copy.Id} — your own copy of {_entry.Part.Path}" : _entry.Part.Path;

    public PartsCatalog.Entry Entry => _entry;

    public override Bitmap? Icon => _icon;

    /// <summary>A part is the one kind of thing Edit can change today.</summary>
    public override bool CanEdit => true;

    /// <summary>Its paint is read out of the game after the panel opens.</summary>
    public override bool ReadsTextures => true;

    /// <summary>
    /// The ASSET NAME under the title — the copy's own when this is a copy.
    ///
    /// A copy cooks to its own package named by <c>Id</c>, and showing the game part's id there
    /// says the mod rewrites the game's part, which is the one thing a copy specifically does not
    /// do.
    /// </summary>
    public override string Subtitle => Copy is { } c ? c.Id : _entry.Part.Name;

    public string Name => _entry.Part.Name;

    /// <summary>Said plainly, because a part whose name is its asset id is worth knowing about.</summary>
    public override string Aside =>
        Copy is not null ? $"your own copy of {_entry.Part.Name}"
        : _entry.Detail.HasName ? "" : "(no display name — this is the asset name)";

    /// <summary>The copy's own words where it has written any, the template's until then.</summary>
    public override string Description =>
        Strip(Copy is { } c && c.Text.TryGetValue("ShortDescription", out var said)
                  ? said : _entry.Detail.ShortDescription ?? "");

    /// <summary>
    /// Why you can or cannot wear it, AS A SENTENCE.
    ///
    /// The index reports a gate in one word — "dev", "edition", "story" — which is the right
    /// shape for a list column and the wrong shape for the one line at the top of a sheet. These
    /// are the ImGui panel's own words.
    /// </summary>
    public override string Headline => Gated ? Gate : "";

    public string Gate => _entry.Part.Gate switch
    {
        "dev" => "Developer only — hidden from the menu (AuthoredOnly)",
        "edition" => "Deluxe / preorder edition content",
        "story" => "Unlocked through play",
        _ => "Available",
    };

    public bool Gated => _entry.Part.Gate != "open";

    /// <summary>
    /// WHY A MOD CANNOT START FROM THIS ONE, said before anything else.
    ///
    /// A part with no geometry is the trap the section's filter exists for: it looks like an
    /// outfit, it duplicates, it builds, it installs, and nothing appears.
    /// </summary>
    public override string Problem
    {
        get
        {
            if (_entry.Detail.Error is { Length: > 0 } e) return $"Could not read this part: {e}";

            // THE CONVERSION'S OWN VERDICT, not a mesh count. A specialization carries no
            // geometry by design, and warning that one "would install and show nothing" is this
            // asking the wrong question rather than the part being broken.
            return Sections.CanUse(_entry, out var why)
                ? ""
                : $"A mod cannot start from this one: {why}.";
        }
    }

    /// <summary>The raw tags, folded away. Kept because they are the truth the rest is read from.</summary>
    public bool TagsOpen
    {
        get => _tagsOpen;
        set { if (Set(ref _tagsOpen, value)) { Raise(nameof(TagsChevron)); Raise(nameof(Sheet)); } }
    }

    public Avalonia.Media.Geometry TagsChevron => Chevrons.For(_tagsOpen);
    public void ToggleTags() => TagsOpen = !TagsOpen;

    /// <summary>
    /// The sheet, as a list of blocks the view draws by type.
    ///
    /// BLOCKS RATHER THAN A FIXED FORM, so a part with nothing to hide contributes no "Hides from
    /// the menu" heading. The ImGui Studio gives every field permanent space whether the part has
    /// it or not, and leaving that habit behind is half of why this port exists.
    /// </summary>
    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var d = _entry.Detail;
            var p = _entry.Part;
            var blocks = new List<object>();
            var band = false;

            Fact("Slot", ShortTag(p.Slot));
            Fact("Rig", p.Rigs.Count == 0 ? "-" : string.Join(", ", p.Rigs));
            if (p.Characters.Count > 0) Fact("Locked to", p.Character);
            if (p.SpeciesLock.Length > 0) Fact("Species", p.SpeciesLock);
            Fact("Class", p.Class);
            Fact("Family", p.Family);
            blocks.Add(new SheetFact { Label = "Path", Value = p.Path, Wide = true });

            if (d.Error is null)
            {
                blocks.Add(new SheetBand { Text = $"Meshes ({d.Meshes.Count})" });
                if (d.Meshes.Count == 0)
                    blocks.Add(new SheetBullet { Text = "none — this part adds no geometry" });
                else
                {
                    var mband = false;
                    foreach (var m in d.Meshes)
                    {
                        blocks.Add(new SheetPair
                        {
                            Left = m.Variant.Length > 0 ? m.Variant : "any",
                            Right = Leaf(m.MeshPath),
                            Band = mband,
                        });
                        mband = !mband;
                    }
                }

                if (d.ReplacesBaseMesh)
                    blocks.Add(new SheetBullet { Text = "replaces the character's base mesh" });

                if (d.OverrideSlots.Count > 0)
                {
                    // "including the head" is a promise worth making explicitly: a part that
                    // takes over the head slot replaces a face, which is not what somebody
                    // expects from a hat.
                    blocks.Add(new SheetBand
                    {
                        Text = d.HidesHead ? "Takes over (including the head)" : "Takes over",
                        Accent = true,
                    });
                    foreach (var slot in d.OverrideSlots)
                        blocks.Add(new SheetBullet { Text = ShortTag(slot) });
                }

                if (d.HiddenSlots.Count > 0)
                {
                    blocks.Add(new SheetBand { Text = "Hides from the menu" });
                    foreach (var slot in d.HiddenSlots)
                        blocks.Add(new SheetBullet { Text = ShortTag(slot) });
                }

                if (d.AnimClassPath is { Length: > 0 } anim)
                {
                    blocks.Add(new SheetBand { Text = "Animation" });
                    blocks.Add(new SheetBullet { Text = Leaf(anim) });
                }

                if (d.Abilities.Count > 0)
                {
                    blocks.Add(new SheetBand { Text = $"Abilities ({d.Abilities.Count})" });
                    foreach (var a in d.Abilities) blocks.Add(new SheetBullet { Text = Leaf(a) });
                }

                if (d.AttributeTables.Count > 0)
                {
                    blocks.Add(new SheetBand { Text = $"Attribute tables ({d.AttributeTables.Count})" });
                    foreach (var t in d.AttributeTables) blocks.Add(new SheetBullet { Text = Leaf(t) });
                }
            }

            blocks.Add(new SheetBand
            { Text = $"All tags ({p.Tags.Count})", Toggles = true, Chevron = TagsChevron });
            if (_tagsOpen)
                foreach (var t in p.Tags) blocks.Add(new SheetBullet { Text = t });

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

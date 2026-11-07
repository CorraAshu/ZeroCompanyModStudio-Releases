using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>A heading across a sheet — "Meshes (1)", "The chain", "Hides from the menu".</summary>
public sealed class SheetBand
{
    public required string Text { get; init; }

    /// <summary>Drawn in the accent colour, where the section is a WARNING about reach.</summary>
    public bool Accent { get; init; }

    /// <summary>
    /// The band opens and shuts what is under it.
    ///
    /// A band reading "All tags (5)" that does nothing when pressed is the UI telling somebody a
    /// lie about itself — the same fault as a sort arrow on a header that does not sort.
    /// </summary>
    public bool Toggles { get; init; }

    public Avalonia.Media.Geometry? Chevron { get; init; }
}

/// <summary>One label-and-value line.</summary>
public sealed class SheetFact
{
    public required string Label { get; init; }
    public required string Value { get; init; }

    /// <summary>Every other row carries a faint band, which is what makes a long sheet scannable.</summary>
    public bool Band { get; init; }

    /// <summary>The value goes under the label, full width. For a path.</summary>
    public bool Wide { get; init; }
}

/// <summary>One bulleted entry.</summary>
public sealed class SheetBullet
{
    public required string Text { get; init; }
}

/// <summary>A two-column row inside a section — a body type and its mesh, a field and its type.</summary>
public sealed class SheetPair
{
    public required string Left { get; init; }
    public required string Right { get; init; }
    public bool Band { get; init; }
}

/// <summary>
/// ANYTHING WITH A SHEET, open as a tab.
///
/// A clothing part, a weapon, a utility and a data table all answer the same four questions —
/// what is it, what is wrong with it, what is it made of, and what does this mod do to it — so
/// they share one layout and one set of block types, and differ only in the blocks they emit.
/// That is why there is ONE DataTemplate for all of them instead of four that drift apart.
///
/// THE EDITING SURFACE LIVES HERE TOO, and today only <see cref="PartTab"/> turns it on. That is
/// a deliberate loan rather than a design: the moment a weapon needs fields a part does not, this
/// splits. Carrying four unused strings is cheaper than four copies of the sheet markup, and the
/// split is a rename away.
/// </summary>
public abstract class SheetTab : StudioTab
{
    /// <summary>The line under the title — the asset id, usually.</summary>
    public virtual string Subtitle => "";
    public bool HasSubtitle => Subtitle.Length > 0;

    /// <summary>Prose under that, where the thing has any.</summary>
    public virtual string Description => "";
    public bool HasDescription => Description.Length > 0;

    /// <summary>One accent line: a gate, a rarity, a reason it is not playable.</summary>
    public virtual string Headline => "";
    public bool HasHeadline => Headline.Length > 0;

    /// <summary>
    /// WHY A MOD CANNOT START FROM THIS ONE, said before anything else.
    ///
    /// Every one of these sections has a version of the same trap: something that looks usable,
    /// duplicates, builds, installs and does nothing. Saying so at the top is cheaper than the
    /// hour somebody spends finding out.
    /// </summary>
    public virtual string Problem => "";
    public bool HasProblem => Problem.Length > 0;

    /// <summary>A faint aside under the subtitle — "(no display name)", and the like.</summary>
    public virtual string Aside => "";
    public bool HasAside => Aside.Length > 0;

    public virtual Bitmap? Icon => null;

    /// <summary>
    /// ON THE BASE, so the header can hide the picture frame entirely for the things that have
    /// none. A weapon and a table have no thumbnail, and a 92-pixel empty box beside their titles
    /// is a hole where the eye expects something.
    /// </summary>
    public bool HasIcon => Icon is not null;

    /// <summary>
    /// Whether there is ANY picture to put beside the title: the thing's own thumbnail, or the
    /// one this mod shows in its place.
    ///
    /// Two sources, one frame. A copy can name a picture the part it was made from never had —
    /// one of the game's other thumbnails, or a PNG of the author's — and gating the frame on
    /// the part's own thumbnail alone hides it.
    /// </summary>
    public bool HasPicture => HasIcon || HasIconPreview;

    /// <summary>What this mod does to it, stated beside the thing rather than swapped in over it.</summary>
    public string ModNote { get => _modNote; set { if (Set(ref _modNote, value)) Raise(nameof(HasModNote)); } }
    private string _modNote = "";
    public bool HasModNote => _modNote.Length > 0;

    /// <summary>The blocks, in order. See <see cref="SheetBand"/> and friends.</summary>
    public abstract IReadOnlyList<object> Sheet { get; }

    /// <summary>Still being read. A table's rows are a package load.</summary>
    public bool Loading { get => _loading; protected set => Set(ref _loading, value); }
    private bool _loading;

    // ------------------------------------------------------------------ editing

    private bool _editing;
    private string _editName = "";
    private string _editDescription = "";
    private string _editLongDescription = "";
    private bool _showsLongDescription;
    private string _editMesh = "";
    private string _editIcon = "";

    public bool Editing
    {
        get => _editing;
        protected set
        {
            if (!Set(ref _editing, value)) return;
            // The read sheet moves house on this, and the header grows a button.
            Raise(nameof(SheetColumn));
            Raise(nameof(SheetSpan));
            Raise(nameof(SheetWidth));
            Raise(nameof(SheetShown));
        }
    }

    public string EditName
    {
        get => _editName;
        set { if (Set(ref _editName, value)) Retally(); }
    }

    public string EditDescription
    {
        get => _editDescription;
        set { if (Set(ref _editDescription, value)) Retally(); }
    }

    /// <summary>
    /// An ABILITY's third text, UIData.LongDescription -- the longer write-up the game draws on the
    /// Armoury's item details and the character-creation specialization panel. "" on every other
    /// kind of tab, where it compares equal to its saved value and counts for nothing, the way the
    /// mod's own fields already sit in this panel.
    /// </summary>
    public string EditLongDescription
    {
        get => _editLongDescription;
        set { if (Set(ref _editLongDescription, value)) Retally(); }
    }

    /// <summary>Whether the NAME card shows the long-description box: an ability copy only.</summary>
    public bool ShowsLongDescription
    {
        get => _showsLongDescription;
        set => Set(ref _showsLongDescription, value);
    }

    /// <summary>
    /// An FBX whose geometry replaces the game's, by NAME rather than by full path.
    ///
    /// A bare name is the portable form: ModBuild.FindImage looks in the mod's own
    /// ModSource/&lt;mod&gt;/Meshes folder, so a project that stores one keeps working when it
    /// moves or is shared. A path into somebody's Desktop does not.
    /// </summary>
    public string EditMesh
    {
        get => _editMesh;
        set { if (Set(ref _editMesh, value)) Retally(); }
    }

    /// <summary>A PNG or TGA whose pixels replace the picture. Same naming rule as the mesh.</summary>
    public string EditIcon
    {
        get => _editIcon;
        set { if (Set(ref _editIcon, value)) { Raise(nameof(IconShown)); Retally(); } }
    }

    /// <summary>What the picture card reads on its line: the import, or the game's own.</summary>
    public string IconShown => _editIcon.Length > 0 ? _editIcon : "the game's own picture";

    /// <summary>Whether Edit does anything for this kind of thing yet.</summary>
    public virtual bool CanEdit => false;

    /// <summary>
    /// Whether this tab's textures arrive on a BACKGROUND read, rather than with the panel.
    ///
    /// Anything that waits for them has to know which tabs actually have any: a check that waited
    /// on a tab with no texture card sat for its whole timeout and then reported on a panel that
    /// had been complete the entire time — which reads as a slow editor, or a hung one.
    /// </summary>
    public virtual bool ReadsTextures => false;

    /// <summary>
    /// The thing's OWN properties, where it has a grid for them.
    ///
    /// On the base for the same reason the four edit strings are: the sheet's markup is shared,
    /// so anything the shared markup binds has to be reachable from the shared type. Null for
    /// everything except a part today, and the numbers card is absent when it is null.
    /// </summary>
    public virtual AssetTab? Props => null;

    /// <summary>Whether this tab is editing a COPY this mod ships rather than the game's own.</summary>
    public virtual bool IsCopy => false;

    /// <summary>Whether a mesh row's Pick does anything here: it moves a PART's reference to another game mesh, and only
    /// a part has one to move (OnPickMeshRow). Elsewhere the button was shown and did nothing.</summary>
    public bool CanPickMesh => this is PartTab;

    /// <summary>
    /// The badge beside the title, and the sentence under it.
    ///
    /// SAID DIFFERENTLY FOR A COPY, because the two acts have opposite consequences and the
    /// panel is identical. "Every character already wearing this changes too" is true of an
    /// override and false of a duplicate — printing it over a copy would frighten somebody out
    /// of the safe one.
    /// </summary>
    /// <remarks>
    /// VIRTUAL, because the copy/override pair is not the only thing this panel edits any more.
    /// The mod's OWN details are neither — there is no game asset underneath them to be a copy
    /// of — and "This is a part your mod adds" printed over a project's name and author is the
    /// panel describing something it is not looking at.
    /// </remarks>
    public virtual string EditBadge => IsCopy ? "YOUR OWN COPY" : "EDITING THE GAME'S OWN";

    public virtual string EditWarning => IsCopy
        ? "This is a part your mod adds. The game's own is untouched, and nothing already wearing it changes."
        : "Every character already wearing this changes too. To leave the game's copy alone, stop editing and use Duplicate instead.";

    /// <summary>
    /// Let the replacement be a very different size from what it replaces.
    ///
    /// The importer refuses a wild size change by default, because one is almost always an
    /// export at the wrong unit scale rather than an intention. This is the way to say it was
    /// meant.
    /// </summary>
    private bool _editAnySize;
    public bool EditAnySize
    {
        get => _editAnySize;
        set { if (Set(ref _editAnySize, value)) Retally(); }
    }

    /// <summary>
    /// Whether this part locks the slot it sits in once the player picks it.
    ///
    /// The game does this to named voices and to the clone and bespoke appearances — which is
    /// why a clone's face cannot be changed. Unticking lets the player change their mind, and a
    /// REPLACEMENT is the only side that can unlock one for a character already wearing it.
    /// </summary>
    private bool _editLocksOwnSlot = true;
    public bool EditLocksOwnSlot
    {
        get => _editLocksOwnSlot;
        set { if (Set(ref _editLocksOwnSlot, value)) Retally(); }
    }

    /// <summary>Only shown for a target that actually sets the lock; otherwise it means nothing.</summary>
    private bool _hasOwnSlotLock;
    public bool HasOwnSlotLock
    {
        get => _hasOwnSlotLock;
        set { if (Set(ref _hasOwnSlotLock, value)) SecSlot.Present = value; }
    }

    /// <summary>
    /// ONE COLOUR SWATCH OF THIS PART'S REGION, and whether the menu offers it.
    ///
    /// Every outfit region has FOUR, and a part decides how many the menu shows by putting the
    /// rest in its LockedSlots and HiddenSlots containers. Handing one back is what answers the
    /// report that a duplicated commando helmet could only reach two tints — it was never a
    /// limit in the tool.
    ///
    /// <see cref="Live"/> is the material's own "Color NN Intensity": a swatch whose intensity is
    /// zero would come back as a picker that does nothing, and that distinction is the difference
    /// between a fix and a wasted evening.
    /// </summary>
    public sealed class SwatchRow : Obs
    {
        public SwatchRow(string tag, string name, string parameter, bool offered, bool live,
                         bool known, bool gameOffered)
        {
            Tag = tag; Name = name; Parameter = parameter; _offered = offered;
            Live = live; Known = known; GameOffered = gameOffered;
        }

        public string Tag { get; }
        public string Name { get; }
        public string Parameter { get; }
        public bool Live { get; }

        /// <summary>
        /// Whether the material was read at all. FALSE MEANS "NO ANSWER", NOT "NO".
        ///
        /// The difference decides what this row is allowed to claim: saying a swatch is switched
        /// off when nothing was read would send somebody looking for a material problem that may
        /// not exist.
        /// </summary>
        public bool Known { get; }

        /// <summary>
        /// What the GAME does with this swatch, kept so a save can store only what CHANGED.
        ///
        /// Writing every swatch would fill the project with entries that change nothing and make
        /// a diff unreadable — and it would also record the game's current answer as if it were
        /// the author's decision, which is what makes a project stop tracking a patched game.
        /// </summary>
        public bool GameOffered { get; }

        private bool _offered;
        public bool Offered
        {
            get => _offered;
            set { if (Set(ref _offered, value)) Raise(nameof(Unsaved)); }
        }

        // As on a mesh row: what it read when the editor opened, so the count is this sitting's.
        private bool _wasOffered;

        internal void Mark() => _wasOffered = _offered;

        internal bool Unsaved => _offered != _wasOffered;

        /// <summary>
        /// What the row says beside the tick.
        ///
        /// Silent when the material was not read: a swatch handed back that the material
        /// switches off comes back as a picker that does nothing, and that warning is the
        /// difference between a fix and a wasted evening — but only when it is actually known.
        /// </summary>
        public string Detail => Parameter +
            (!Known ? "" : Live ? "" : "  ·  the material switches this one off");
    }

    /// <summary>The four swatches of this part's region, or empty when it has none.</summary>
    public ObservableCollection<SwatchRow> Swatches { get; } = new();

    public bool HasSwatches => Swatches.Count > 0;

    /// <summary>
    /// ONE BODY-TYPE MESH OF THIS PART, editable where it stands.
    ///
    /// A part can carry several — Average/Feminine and Average/Masculine on the same helmet —
    /// and the old studio made you click into each one to get its own editor. They all belong in
    /// the Meshes section at once, each with its own three choices.
    ///
    /// THE THREE ARE DIFFERENT MECHANISMS, not three ways to do one thing:
    ///   PICK   re-points this part's reference at another mesh the game already ships.
    ///   IMPORT overrides THAT MESH PACKAGE with an FBX — aimed at the mesh, not the part,
    ///          which is the rule a replacement follows.
    ///   VIEW   opens it in the 3D viewport, which is the only way to check an import arrived
    ///          as the thing you meant.
    /// </summary>
    public sealed class MeshRow : Obs
    {
        public MeshRow(string variant, string gamePath, string replacement, string pickedPath)
        {
            Variant = variant;
            GamePath = gamePath;
            _replacement = replacement;
            _picked = pickedPath;
        }

        /// <summary>"Average/Feminine", or "any" when the part draws no distinction.</summary>
        public string Variant { get; }

        /// <summary>
        /// WHICH PIECE OF THE GARMENT THIS IS — BOOT, TORS, PACK, LEGS, ARMS — or "" when the
        /// part is one piece and has no regions to speak of.
        ///
        /// Per FRAGMENT, not per row: two rows can be the Average and Feminine builds of ONE
        /// piece, and they share a region. A key here is what lets an import land on the torso
        /// without also replacing the boots, which is what a part-wide mesh file did.
        /// </summary>
        public string Region { get; init; } = "";

        /// <summary>Whether this row belongs to a garment made of several pieces.</summary>
        public bool PerPiece => Region.Length > 0;

        /// <summary>The mesh the GAME names here, which is what an import is measured against.</summary>
        public string GamePath { get; }

        public string GameLeaf
        {
            get
            {
                var cut = GamePath.LastIndexOfAny(['/', '.']);
                return cut >= 0 && cut < GamePath.Length - 1 ? GamePath[(cut + 1)..] : GamePath;
            }
        }

        private string _replacement;
        /// <summary>An FBX of the author's, by name. Empty means the game's own geometry.</summary>
        public string Replacement
        {
            get => _replacement;
            set { if (Set(ref _replacement, value)) Changed(); }
        }

        private string _picked;
        /// <summary>Another game mesh to point at instead. Empty means this part's own.</summary>
        public string Picked
        {
            get => _picked;
            set { if (Set(ref _picked, value)) Changed(); }
        }

        private void Changed()
        {
            Raise(nameof(Shown));
            Raise(nameof(Mine));
            Raise(nameof(Detail));
            Raise(nameof(Unsaved));
        }

        /// <summary>
        /// What the row reads as: the author's file, a re-point, or the game's own mesh BY NAME.
        ///
        /// Named rather than "the game's own geometry", because the name is the thing somebody
        /// needs when they go looking for it — and a row that says only "unchanged" cannot be
        /// checked against anything.
        /// </summary>
        public string Shown =>
            _replacement.Length > 0 ? _replacement
            : _picked.Length > 0 ? Leaf(_picked)
            : GameLeaf;

        /// <summary>
        /// Beside it: what this is standing in for. Silent on an unchanged row.
        ///
        /// BOTH AT ONCE IS A REAL STATE, and it is the one worth saying out loud. A copy that
        /// names one mesh wears it for EVERY body type the template carried — the build
        /// redirects every option in the fragment onto the one the part names — so a feminine
        /// row on a part built from a masculine mesh is wearing geometry cut for the other body.
        /// Reading "over SK_HAAF_Clo001_TORS" there would say the author's file went onto the
        /// feminine mesh, which is the opposite of what ships.
        /// </summary>
        public string Detail =>
            _replacement.Length > 0 && _picked.Length > 0
                ? $"over {Leaf(_picked)}, in place of {GameLeaf}"
            : _replacement.Length > 0 ? "over " + GameLeaf
            : _picked.Length > 0 ? "instead of " + GameLeaf
            : "";

        /// <summary>Drawn as changed: the accent edge, the word, and the way back.</summary>
        public bool Mine => _replacement.Length > 0 || _picked.Length > 0;

        /// <summary>The word beside the variant: what KIND of change this is.</summary>
        public string Kind =>
            _replacement.Length > 0 && _picked.Length > 0 ? "your own mesh, on another of the game's"
            : _replacement.Length > 0 ? "your own mesh"
            : _picked.Length > 0 ? "another of the game's"
            : "unchanged";

        // WHAT IT WAS WHEN THE EDITOR OPENED, so "2 changes not saved" counts what this sitting
        // has done rather than everything the project has ever recorded. A row loaded with an
        // override saved last week is not an unsaved change.
        private string _wasReplacement = "", _wasPicked = "";

        /// <summary>
        /// Take this row's current state as the saved one.
        ///
        /// An import WRITES THE PROJECT as it happens — it copies the file into ModSource and
        /// saves — so the row is changed from the game but not waiting on the Save button, and
        /// counting it as unsaved would leave a bar that never goes quiet. Raising here is what
        /// makes the tab recount.
        /// </summary>
        internal void Mark()
        {
            _wasReplacement = _replacement;
            _wasPicked = _picked;
            Raise(nameof(Unsaved));
        }

        internal bool Unsaved => _replacement != _wasReplacement || _picked != _wasPicked;
    }

    /// <summary>
    /// ONE TEXTURE THE PART WEARS, and whatever this mod puts over it.
    ///
    /// The old studio called this step Materials and listed exactly this: every texture the
    /// part's meshes sample, by name, size and format. It is a separate row per TEXTURE PACKAGE
    /// and not per material, because that is the shape of the edit — the pixels go into the
    /// package, so everything sampling it changes, and a kit whose five regions share one diffuse
    /// is ONE row rather than five fields all writing to the same override.
    ///
    /// <see cref="Slot"/> is the material's own parameter name — "Base Color", "Base Mask 1 VT",
    /// "Normal" — because that is what says what the texture is FOR. A file name cannot: this
    /// game's masks are named by suffix and the suffix lies often enough to matter.
    /// </summary>
    public sealed class TextureRow : Obs
    {
        public TextureRow(string slot, string package, string leaf, string describe,
                          string replacement, int width = 0, int height = 0,
                          string holder = "", string picked = "",
                          string mineKey = "", string piece = "", int mineSlot = 0)
        {
            MineSlot = mineSlot;
            Slot = slot;
            Package = package;
            GameLeaf = leaf;
            Describe = describe;
            _replacement = replacement;
            Width = width;
            Height = height;
            Holder = holder;
            _picked = picked;
            MineKey = mineKey;
            Piece = piece;
        }

        /// <summary>
        /// THE KEY A COPY ALREADY PAINTS THIS TEXTURE UNDER, when it does.
        ///
        /// A copy names its artwork by material parameter, by texture name OR by suffix, and all
        /// three are equally valid — his own project uses the suffixes (D, MSK, N) while this
        /// panel's import writes the parameter. Writing a second key for a texture that already
        /// has one would leave the project painting it twice under two names, with the build
        /// free to apply either. So an edit re-uses the key that is there, and this is it.
        ///
        /// Empty means nothing paints this row yet, and a new key is written from
        /// <see cref="Slot"/>.
        /// </summary>
        public string MineKey { get; internal set; }

        /// <summary>
        /// THE PIECE THIS ROW BELONGS TO, for a garment the author dresses piece by piece.
        /// Empty when the part paints through itself, which is every single-garment part.
        ///
        /// It decides WHERE an edit to this row is written: the piece's own material list, or
        /// the part's. The build reads a regioned part's paint per piece and ignores the part's
        /// own, so a row that does not know its piece can only write somewhere that is dropped.
        /// </summary>
        public string Piece { get; }

        /// <summary>
        /// WHICH MATERIAL SLOT of the mesh this texture belongs to.
        ///
        /// A part's artwork is stored per slot and the build paints per slot, so a row from the
        /// visor's material has to write into the visor's entry. Writing every row into slot
        /// zero is how paint lands in the project, shows as applied, and is never worn.
        /// </summary>
        public int MineSlot { get; }

        /// <summary>
        /// THE PACKAGE THAT NAMES THIS TEXTURE, which is not the texture.
        ///
        /// Importing pixels aims at the texture itself. POINTING somewhere else aims at whoever
        /// holds the reference — and for a texture that is the MATERIAL, not the part: the part
        /// has never heard of a diffuse map. For the menu picture it IS the part. Empty means
        /// nothing here can be re-pointed, and Pick is not offered.
        /// </summary>
        public string Holder { get; }

        public bool CanPick => Holder.Length > 0;

        private string _picked;

        /// <summary>Another of the game's textures to point at instead. Empty means its own.</summary>
        public string Picked
        {
            get => _picked;
            set
            {
                if (!Set(ref _picked, value)) return;
                Raise(nameof(Shown));
                Raise(nameof(Detail));
                Raise(nameof(Kind));
                Raise(nameof(Mine));
                Raise(nameof(Unsaved));
            }
        }

        /// <summary>
        /// The target's shape, kept as numbers and not only in <see cref="Describe"/>.
        ///
        /// Because the importer RESIZES rather than refusing: a 256 icon dropped on a 2,048
        /// diffuse is accepted and scaled, and on a MASK that is destructive — resizing one is
        /// already recorded here as the thing that erased it. So the "Use…" menu has to be able
        /// to compare shapes and say which of your images does not match.
        /// </summary>
        public int Width { get; }
        public int Height { get; }

        /// <summary>The material parameter that binds it, which is what it is for.</summary>
        public string Slot { get; }

        /// <summary>The texture package an import is aimed at.</summary>
        public string Package { get; }

        public string GameLeaf { get; }

        /// <summary>"2048x2048 BC7 · virtual" — what a replacement has to fit.</summary>
        public string Describe { get; }

        private string _replacement;

        /// <summary>The author's image, by name. Empty means the game's own pixels.</summary>
        public string Replacement
        {
            get => _replacement;
            set
            {
                if (!Set(ref _replacement, value)) return;
                Raise(nameof(Shown));
                Raise(nameof(Detail));
                Raise(nameof(Kind));
                Raise(nameof(Mine));
                Raise(nameof(Unsaved));
            }
        }

        public bool Mine => _replacement.Length > 0 || _picked.Length > 0;

        public string Shown =>
            _replacement.Length > 0 ? _replacement
            : _picked.Length > 0 ? Leaf(_picked)
            : GameLeaf;

        /// <summary>
        /// Beside it: what this stands in for.
        ///
        /// A slot the game's mesh has not got has no texture to stand in for, so "over " with
        /// nothing after it is the whole of what that row could say. It says what the slot IS
        /// instead.
        /// </summary>
        public string Detail =>
            GameLeaf.Length == 0 ? Describe
            : _replacement.Length > 0 ? "over " + GameLeaf
            : _picked.Length > 0 ? "instead of " + GameLeaf
            : Describe;

        public string Kind =>
            _replacement.Length > 0 ? "your own image"
            : _picked.Length > 0 ? "another of the game's"
            : "unchanged";

        // As on a mesh row: an import writes the project as it happens, so this is what says the
        // row is changed from the game rather than waiting on Save.
        private string _wasReplacement = "", _wasPicked = "";

        internal void Mark()
        {
            _wasReplacement = _replacement;
            _wasPicked = _picked;
            Raise(nameof(Unsaved));
        }

        internal bool Unsaved => _replacement != _wasReplacement || _picked != _wasPicked;
    }

    /// <summary>Every texture the part's meshes wear.</summary>
    public ObservableCollection<TextureRow> TextureRows { get; } = new();

    public bool HasTextureRows => TextureRows.Count > 0;

    public void SetTextureRows(IEnumerable<TextureRow> rows)
    {
        TextureRows.Clear();
        foreach (var r in rows)
        {
            r.Mark();
            r.PropertyChanged += (_, _) => Retally();
            TextureRows.Add(r);
        }

        Raise(nameof(HasTextureRows));
        // ABSENT, NOT EMPTY, for a part whose materials could not be read — the same rule the
        // read sheet follows. A heading over nothing is a section that looks broken.
        SecTextures.Present = TextureRows.Count > 0 || MaterialGroups.Count > 0;
        SecTextures.Count = TextureRows.Count > 0 ? TextureRows.Count.ToString() : "";
        Retally();
    }

    /// <summary>
    /// ONE MATERIAL SLOT of the thing being edited: what it wears, what the mesh calls it, and
    /// the textures that material carries.
    ///
    /// The old studio's Materials step, which this panel had only half of. A slot can be painted
    /// — rows of the author's own images, which is what was already here — or it can simply NAME
    /// one of the game's materials and wear it whole, which costs one package import and no
    /// copies at all. A piece borrowed from another kit wants the second; exporting four 4K
    /// virtual textures to reproduce it by hand is minutes of work and a worse result.
    ///
    /// <see cref="Rows"/> holds the SAME objects as <see cref="TextureRows"/>, so the flat list
    /// the save bar counts and the grouped list the card draws cannot disagree.
    /// </summary>
    public sealed class MaterialGroup : Obs
    {
        public MaterialGroup(int slot, string piece, string material, string slotName,
                             bool onMesh, string plannedSlotName)
        {
            Slot = slot;
            Piece = piece;
            _material = material;
            _slotName = slotName;
            OnMesh = onMesh;
            PlannedSlotName = plannedSlotName;
        }

        /// <summary>The material slot's index, which is what the build paints by.</summary>
        public int Slot { get; }

        /// <summary>The piece it belongs to on a multi-piece garment; empty on a single one.</summary>
        public string Piece { get; }

        /// <summary>
        /// Whether the GAME's mesh actually has this slot.
        ///
        /// False is not a fault: a replacement model may have more material groups than the thing
        /// it stands in for, and the build makes the extra slots for it. It does mean there is no
        /// game material to read, so the slot has no texture rows of its own — only what the
        /// author names or paints.
        /// </summary>
        public bool OnMesh { get; }

        public ObservableCollection<TextureRow> Rows { get; } = new();

        public string Title =>
            (Piece.Length > 0 ? $"{Piece}  ·  " : "") + $"Material {Slot + 1}" +
            (OnMesh ? "" : "  (your model's own)");

        private string _material;

        /// <summary>A game material this slot wears whole, by package. Empty = the template's.</summary>
        public string Material
        {
            get => _material;
            set
            {
                if (!Set(ref _material, value)) return;
                Raise(nameof(Named));
                Raise(nameof(Wears));
            }
        }

        public bool Named => _material.Length > 0;

        /// <summary>What the slot wears, in one line — the old studio's own wording.</summary>
        public string Wears =>
            Named && Rows.Any(r => r.Mine)
                ? $"{Leaf(_material)}, repainted with {Rows.Count(r => r.Mine)} of your own"
            : Named ? Leaf(_material)
            : Rows.Any(r => r.Mine) ? $"your own textures ({Rows.Count(r => r.Mine)})"
            : "the template's own material";

        private string _slotName;

        /// <summary>
        /// WHAT THE MESH CALLS THIS SLOT, which is the one control deciding whether the tints
        /// reach it.
        ///
        /// A tint fragment names its target slots in an array on a GAME Blueprint shared by every
        /// part of a region, and a mod cannot add a name to it. Making a second slot ANSWER to the
        /// name already there is the only route there is.
        /// </summary>
        public string SlotName
        {
            get => _slotName;
            set { if (Set(ref _slotName, value?.Trim() ?? "")) Raise(nameof(TakesNoTint)); }
        }

        /// <summary>The name the build will give it when this is left blank.</summary>
        public string PlannedSlotName { get; }

        /// <summary>
        /// The warning nobody could see: a second slot quietly renamed is a slot the tint misses.
        /// </summary>
        public bool TakesNoTint =>
            _slotName.Length == 0 && PlannedSlotName.Contains("_ADD", StringComparison.Ordinal);

        public string PlannedShown =>
            PlannedSlotName.Length > 0 ? PlannedSlotName : "named for you";

        private static string Leaf(string s)
        {
            var cut = s.LastIndexOfAny(['/', '.']);
            return cut >= 0 && cut < s.Length - 1 ? s[(cut + 1)..] : s;
        }
    }

    /// <summary>The material slots, in order, each holding its own texture rows.</summary>
    public ObservableCollection<MaterialGroup> MaterialGroups { get; } = new();

    public bool HasMaterialGroups => MaterialGroups.Count > 0;

    /// <summary>
    /// Whether this panel can add a material slot at all.
    ///
    /// Only a copy and an override carry a material list. A game part being browsed has no
    /// project object to put one on, and a button that writes nowhere is the fault this panel has
    /// paid for four times.
    /// </summary>
    public bool CanAddMaterial { get; private set; }

    public void SetMaterialGroups(IEnumerable<MaterialGroup> groups, bool canAdd)
    {
        MaterialGroups.Clear();
        foreach (var g in groups) MaterialGroups.Add(g);
        CanAddMaterial = canAdd;
        Raise(nameof(HasMaterialGroups));
        Raise(nameof(CanAddMaterial));
        SecTextures.Present = TextureRows.Count > 0 || MaterialGroups.Count > 0;

        // COUNTED OFF THE CARD, not off the flat list. A slot the game's mesh has not got
        // contributes rows that no material chain produced, so the rail read four beside a card
        // showing eight — a number that disagrees with what is under it is worse than none.
        var shown = MaterialGroups.Sum(g => g.Rows.Count);
        SecTextures.Count = shown > 0 ? shown.ToString() : "";
    }

    /// <summary>
    /// One more slot, without re-reading the part.
    ///
    /// Re-filling would re-open every material chain the part wears — several package loads to
    /// show one empty slot that nothing has been said about yet.
    /// </summary>
    public void AddMaterialGroup(MaterialGroup group)
    {
        MaterialGroups.Add(group);
        Raise(nameof(HasMaterialGroups));
        SecTextures.Present = true;
    }

    /// <summary>The piece a new slot would belong to: the one the last group named.</summary>
    public string LastPiece => MaterialGroups.Count > 0 ? MaterialGroups[^1].Piece : "";

    /// <summary>
    /// THE MENU PICTURE, as a texture row like any other.
    ///
    /// It IS a texture — it just is not one the MESH wears, so following the material chain never
    /// reaches it and it needs asking for separately. Giving it the same row as the textures
    /// above is what makes "replace the picture" one act with one meaning: the pixels go into the
    /// package the part points at.
    ///
    /// Null when the part names its picture by an image-bank TAG instead of a texture of its own.
    /// That is not a failure to read anything — there is genuinely nothing to import over — so
    /// <see cref="IconNote"/> says which it is rather than the card offering a button that
    /// silently does nothing.
    /// </summary>
    private TextureRow? _iconRow;
    public TextureRow? IconRow
    {
        get => _iconRow;
        private set { if (Set(ref _iconRow, value)) Raise(nameof(HasIconRow)); }
    }

    public bool HasIconRow => _iconRow is not null;

    /// <summary>
    /// What replacing the picture actually does — which is not the same act on both sides of
    /// this panel.
    ///
    /// An override writes pixels INTO the game's texture, so everything that shows it changes. A
    /// copy names its own picture and the game's texture is never touched. Same button, opposite
    /// promise, so the line under it cannot be one sentence.
    /// </summary>
    public string PictureBlurb => IsCopy
        ? "Your copy names its own picture. The game's texture is left alone, so nothing else " +
          "that shows it changes."
        : "A PNG or TGA of your own goes into this texture itself, so it changes wherever the " +
          "game shows it.";

    private string _iconNote = "";
    public string IconNote { get => _iconNote; private set => Set(ref _iconNote, value); }

    /// <summary>
    /// The author's own picture, shown in place of the game's on the picture card.
    ///
    /// Because a card called "its picture" showing the picture you just replaced is a small lie
    /// that costs an evening: the row said yours was applied and the thumbnail said it was not,
    /// and only one of them can be believed.
    ///
    /// Null for a TGA, which Avalonia cannot decode — the game's thumbnail stays and the row
    /// still says what is applied. Never a silent swap to the wrong image.
    /// </summary>
    private Bitmap? _iconPreview;
    public Bitmap? IconPreview
    {
        get => _iconPreview;
        private set
        {
            if (!Set(ref _iconPreview, value)) return;
            Raise(nameof(HasIconPreview));
            Raise(nameof(HasPicture));
        }
    }

    public bool HasIconPreview => _iconPreview is not null;

    public void SetIconPreview(Bitmap? shot) => IconPreview = shot;

    public void SetIconRow(TextureRow? row, string note)
    {
        if (row is not null)
        {
            row.Mark();
            row.PropertyChanged += (_, _) => Retally();
        }

        IconRow = row;
        IconNote = note;
        Retally();
    }

    /// <summary>Every body-type mesh this part carries.</summary>
    public ObservableCollection<MeshRow> MeshRows { get; } = new();

    public bool HasMeshRows => MeshRows.Count > 0;

    public void SetMeshRows(IEnumerable<MeshRow> rows)
    {
        MeshRows.Clear();
        foreach (var r in rows)
        {
            r.Mark();
            r.PropertyChanged += (_, _) => Retally();
            MeshRows.Add(r);
        }

        Raise(nameof(HasMeshRows));
        SecMeshes.Count = MeshRows.Count > 1 ? MeshRows.Count.ToString() : "";
        Retally();
    }

    /// <summary>
    /// HOW MANY PIECES OF THE MODEL ARE MISSING, and what they are.
    ///
    /// A weapon assembled from one part per slot needs the mod's own copy of each. Missing one
    /// shows nothing in that slot — a receiver floating with no barrel — and nothing on screen
    /// said so until the build. Zero means the model is whole and the card says nothing.
    /// </summary>
    public int ModelGap { get => _modelGap; private set { if (Set(ref _modelGap, value)) Raise(nameof(ModelIncomplete)); } }
    private int _modelGap;

    public bool ModelIncomplete => _modelGap > 0;

    public string ModelGapSays
    {
        get => _modelGapSays;
        private set => Set(ref _modelGapSays, value);
    }
    private string _modelGapSays = "";

    public void SetModelGap(int missing, string what)
    {
        ModelGap = missing;
        ModelGapSays = missing == 0 ? "" :
            $"{missing} piece(s) missing — in the game this weapon is incomplete:  {what}";
    }

    // ------------------------------------------------------------------ a weapon's own cards

    /// <summary>One colour a weapon's bolts can be, as the game's own list has it.</summary>
    public sealed record BoltChoice(int Index, string Label);

    public ObservableCollection<BoltChoice> Bolts { get; } = new();

    public bool HasBolts => Bolts.Count > 0;

    private BoltChoice? _bolt;

    /// <summary>
    /// The colour chosen. Set by the view model when the card fills; setting it from the UI is
    /// what the handler watches, so the write goes through the view model rather than from here.
    /// </summary>
    public BoltChoice? Bolt { get => _bolt; set => Set(ref _bolt, value); }

    public string BoltNote { get => _boltNote; private set => Set(ref _boltNote, value); }
    private string _boltNote = "";

    public void SetBolts(IEnumerable<BoltChoice> choices, int chosen, string note)
    {
        Bolts.Clear();
        foreach (var c in choices) Bolts.Add(c);
        _bolt = Bolts.FirstOrDefault(c => c.Index == chosen) ?? Bolts.FirstOrDefault();
        BoltNote = note;
        Raise(nameof(HasBolts));
        Raise(nameof(Bolt));
        SecBolts.Present = Bolts.Count > 0;
    }

    /// <summary>Where in the Armoury it appears, and what that costs.</summary>
    public string ShelfIs { get => _shelfIs; private set => Set(ref _shelfIs, value); }
    private string _shelfIs = "";

    public string ShelfNote { get => _shelfNote; private set => Set(ref _shelfNote, value); }
    private string _shelfNote = "";

    /// <summary>On a shelf THIS MOD defines, which is neither the game's nor the shared one.</summary>
    public bool ShelfIsOwn { get => _shelfOwn; private set => Set(ref _shelfOwn, value); }
    private bool _shelfOwn;

    public bool CanMoveShelf => _shelfIs.Length > 0 && !_shelfOwn;
    public bool OnGameShelf => _shelfIs.Contains("game's", StringComparison.Ordinal);


    // A SHELF OF THE AUTHOR'S OWN, offered BELOW the two shared ones rather than instead of them.
    // The shipped shelves need no setup and no dependency of the author's, so they are the right
    // default; this is for somebody who wants their own name on the Armoury's list.

    /// <summary>What to call it. Empty is the only thing that stops the button.</summary>
    public string EditShelfName
    {
        get => _shelfName;
        set { if (Set(ref _shelfName, value)) Raise(nameof(CanMakeShelf)); }
    }
    private string _shelfName = "";

    public bool CanMakeShelf => _shelfName.Trim().Length > 0;

    /// <summary>
    /// WHAT IT BEHAVES LIKE, which is not the same question as what it is called.
    ///
    /// This picks how the weapon FEELS: the animations, the abilities and the numbers it starts
    /// from all come from the class it is modelled on. A shelf that behaves like a Repeater holds
    /// repeaters, whatever the name on it says. Core refuses anything outside its own list, so
    /// the list is Core's rather than one written here.
    /// </summary>
    public IReadOnlyList<string> ShelfLikes { get; } = WeaponAuthor.MirroredClasses;

    public string EditShelfLike { get => _shelfLike; set => Set(ref _shelfLike, value); }
    private string _shelfLike = WeaponAuthor.MirroredClasses[0];

    public void SetShelf(string what, string note, bool onOwn)
    {
        ShelfIs = what;
        ShelfNote = note;
        ShelfIsOwn = onOwn;
        Raise(nameof(CanMoveShelf));
        Raise(nameof(OnGameShelf));
        SecShelf.Present = what.Length > 0;
    }

    /// <summary>One of a weapon's numbers — damage, rate, range.</summary>
    public sealed class StatRow : Obs
    {
        public StatRow(string row, string label, float value, bool changed)
        {
            Row = row;
            Label = label;
            _text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Changed = changed;
        }

        public string Row { get; }
        public string Label { get; }

        /// <summary>Whether this mod has moved it off what the game's table says.</summary>
        public bool Changed { get; }

        private string _text;
        public string Text { get => _text; set => Set(ref _text, value); }
    }

    public ObservableCollection<StatRow> WeaponStats { get; } = new();

    public bool HasWeaponStats => WeaponStats.Count > 0;

    /// <summary>
    /// Why there are no numbers to edit — which is never a failure. A weapon SHARES its class's
    /// table until it is given one of its own, and the card says so with the button that splits it.
    /// </summary>
    public string StatsNote { get => _statsNote; private set => Set(ref _statsNote, value); }
    private string _statsNote = "";

    public bool CanOwnStats => _statsNote.Length > 0;

    public void SetWeaponStats(IEnumerable<StatRow> rows, string note)
    {
        WeaponStats.Clear();
        foreach (var r in rows) WeaponStats.Add(r);
        StatsNote = note;
        Raise(nameof(HasWeaponStats));
        Raise(nameof(CanOwnStats));
        SecStats.Present = WeaponStats.Count > 0 || note.Length > 0;
        SecStats.Count = WeaponStats.Count > 0 ? WeaponStats.Count.ToString() : "";
    }

    /// <summary>What fits this weapon, and whether that is the donor's answer or its own.</summary>
    public string FitsIs { get => _fitsIs; private set => Set(ref _fitsIs, value); }
    private string _fitsIs = "";

    public string FitsNote { get => _fitsNote; private set => Set(ref _fitsNote, value); }
    private string _fitsNote = "";

    /// <summary>True while it answers to the donor's type, which is what a duplicate starts as.</summary>
    public bool KeepsDonorType { get => _keepsDonor; private set => Set(ref _keepsDonor, value); }
    private bool _keepsDonor;

    public void SetFits(string what, string note, bool keepsDonor)
    {
        FitsIs = what;
        FitsNote = note;
        KeepsDonorType = keepsDonor;
        SecFits.Present = what.Length > 0;
    }

    /// <summary>One attachment palette a weapon can wear, and who owns it.</summary>
    /// <summary>
    /// One attachment palette a weapon can wear, and who owns it.
    ///
    /// <see cref="Mine"/> is what this mod actually SUPPLIES; <see cref="Warn"/> is a state that
    /// looks like success in the project and reaches nothing in the game. They are separate
    /// because the project cannot tell them apart and the game cannot confuse them.
    /// </summary>
    public sealed record AttachRow(string Palette, string Leaf, string Slot, string Style,
                                   string Wearing, bool Mine, bool Warn)
    {
        /// <summary>
        /// Neither applied nor wrong — the donor's, untouched.
        ///
        /// Its own property because a binding cannot say "not one AND not the other", and
        /// without it a warning row drew its sentence twice: once faint and once in red.
        /// </summary>
        public bool Plain => !Mine && !Warn;
    }

    public ObservableCollection<AttachRow> Attachments { get; } = new();

    public bool HasAttachments => Attachments.Count > 0;

    public void SetAttachments(IEnumerable<AttachRow> rows)
    {
        Attachments.Clear();
        foreach (var r in rows) Attachments.Add(r);
        Raise(nameof(HasAttachments));
        SecAttach.Present = Attachments.Count > 0;
        SecAttach.Count = Attachments.Count > 0 ? Attachments.Count.ToString() : "";
    }

    // ------------------------------------------------------------------ a spec's own cards

    /// <summary>Who may take it, and which specialization it claims to BE.</summary>
    public string EditForWho
    {
        get => _editForWho;
        set { if (Set(ref _editForWho, value)) Retally(); }
    }
    private string _editForWho = "";

    /// <summary>
    /// The identity tag — the one that says which specialization this IS, as opposed to where it
    /// may go. A copy KEEPS its template's by default, and that is measured rather than assumed:
    /// an authored spec carrying its template's identity shipped and reached both the picker and
    /// the game's own recruit generator. A fresh tag is one the game's tag table does not define.
    /// </summary>
    public string EditIdentity
    {
        get => _editIdentity;
        set { if (Set(ref _editIdentity, value)) Retally(); }
    }
    private string _editIdentity = "";

    /// <summary>The image-bank TAG its crest is drawn from — not an asset path.</summary>
    public string EditCrest
    {
        get => _editCrest;
        set { if (Set(ref _editCrest, value)) { Raise(nameof(CrestShown)); Retally(); } }
    }
    private string _editCrest = "";

    public string CrestShown => _editCrest.Length > 0 ? _editCrest : "its template's crest";

    /// <summary>
    /// WHICH PIECE THE FIT CARD IS PLACING, when the garment is made of several and more than one
    /// carries an import.
    ///
    /// The five fields describe how to put ONE file where the game's mesh was, so with two
    /// imported pieces there are two answers and the card has to say which it is giving. A
    /// selector rather than a card per piece: the card is shared by four kinds already, and
    /// growing one per piece would be four more of everything in it.
    /// </summary>
    public string FitPiece
    {
        get => _fitPiece;
        set { if (Set(ref _fitPiece, value)) FitPieceChanged?.Invoke(value); }
    }
    private string _fitPiece = "";

    /// <summary>Raised when the piece is switched, so the view model can refill the fields.</summary>
    public Action<string>? FitPieceChanged;

    /// <summary>The pieces that carry an import, for the selector. One or none hides it.</summary>
    public ObservableCollection<string> FitPieces { get; } = new();

    public bool HasFitPieces => FitPieces.Count > 1;

    /// <summary>"placing its ARMS", or "" when there is only one thing it could be placing.</summary>
    public string FitPieceSays => FitPieces.Count > 1 ? $"placing its {_fitPiece}" : "";

    /// <summary>
    /// THE IMAGE-BANK TAG THIS COPY'S PICTURE IS DRAWN FROM — a weapon's armoury icon.
    ///
    /// Not an asset path. A gear kit names its picture by TAG through a shared bank, and the only
    /// way to change it was to know that, know the tag its donor carries, and type a re-point by
    /// hand. Empty when the template names no icon by tag at all, which is most things — then the
    /// row is absent rather than dead.
    /// </summary>
    public string BankIcon { get => _bankIcon; set { if (Set(ref _bankIcon, value)) Raise(nameof(HasBankIcon)); } }
    private string _bankIcon = "";
    public bool HasBankIcon => _bankIcon.Length > 0;

    /// <summary>What it draws now — the donor's tag unless a re-point has moved it.</summary>
    public string BankIconSays { get => _bankIconSays; set => Set(ref _bankIconSays, value); }
    private string _bankIconSays = "";

    // ------------------------------------------------------------------ the mod's own cards

    // THE PROJECT ITSELF IS A THING THAT CAN BE EDITED, and until now it was the one thing in
    // this Studio that could not. Every part, weapon and specialization in a mod had a panel;
    // the mod carrying them had a read-only line at the top of the window.
    //
    // Three of these fields are SHIPPED — the title, the description and the author are written
    // into the .uplugin, which is what a player sees in the game's own mod list. The old studio
    // never offered the description at all, so every mod either shipped an empty one or whatever
    // a hand-edited JSON happened to hold.

    /// <summary>Who made it. Ships as the plugin's <c>CreatedBy</c>.</summary>
    public string EditAuthor
    {
        get => _editAuthor;
        set { if (Set(ref _editAuthor, value)) Retally(); }
    }
    private string _editAuthor = "";

    /// <summary>The author's own version string. The project's bookkeeping, not the engine's.</summary>
    public string EditVersion
    {
        get => _editVersion;
        set { if (Set(ref _editVersion, value)) Retally(); }
    }
    private string _editVersion = "";

    /// <summary>
    /// A picture for this mod's card on the shelf — a PNG or TGA in ModSource/Icons, by name.
    ///
    /// It is NEVER SHIPPED and the game never sees it. The card finds one on its own when this is
    /// empty, and that default is what sent the author looking: "you'll have to explain to me how
    /// someone adds a photo to their mod in the mods panel, i can't figure it out."
    /// </summary>
    public string EditPicture
    {
        get => _editPicture;
        set { if (Set(ref _editPicture, value)) { Raise(nameof(PictureShown)); Retally(); } }
    }
    private string _editPicture = "";

    public string PictureShown => _editPicture.Length > 0 ? _editPicture : "it picks one itself";

    /// <summary>The picture, decoded, or null while it names nothing this project can find.</summary>
    public Bitmap? ModPicture { get => _modPicture; private set { Set(ref _modPicture, value); Raise(nameof(HasModPicture)); } }
    private Bitmap? _modPicture;
    public bool HasModPicture => _modPicture is not null;

    public void SetModPicture(Bitmap? b) => ModPicture = b;

    /// <summary>
    /// WHAT THIS MOD SHIPS AS: the name every file it builds is derived from.
    ///
    /// It decides the container id, the pak's file name and the folder the game reads it out of,
    /// which is why it is in the same card as the asset folder rather than filed with the title.
    /// Changing it is not a rename of something already installed — the old copy stays where it
    /// is under its old name, so the save refuses while one is there and says so.
    /// </summary>
    public string EditShipsAs
    {
        get => _editShipsAs;
        set { if (Set(ref _editShipsAs, value)) { Raise(nameof(ShipsAsFiles)); Retally(); } }
    }
    private string _editShipsAs = "";

    /// <summary>
    /// Where this mod's packages are filed, relative to /Game/Game/.
    ///
    /// NOT FREE-FORM, and the reason is worth carrying next to the box: the game's DefaultGame.ini
    /// registers the part scan under /Game/Game/Customizations, so a part outside that tree ships,
    /// mounts and is never looked at — with no error anywhere.
    /// </summary>
    public string EditFolder
    {
        get => _editFolder;
        set
        {
            if (!Set(ref _editFolder, value)) return;
            Raise(nameof(FolderIs));
            Raise(nameof(FolderWhy));
            Raise(nameof(FolderBad));
            Retally();
        }
    }
    private string _editFolder = "";

    /// <summary>The package root it resolves to, said while it can still be changed.</summary>
    public string FolderIs => "/Game/Game/" + _editFolder.Replace('\\', '/').Trim('/');

    /// <summary>The three files it builds, named. Core's rule, asked rather than re-derived.</summary>
    public string ShipsAsFiles
    {
        get
        {
            var safe = new string((_editShipsAs.Trim().Length == 0 ? "Mod" : _editShipsAs.Trim())
                .Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c)
                .ToArray()).Trim('_');
            if (safe.Length == 0) safe = "Mod";
            if (!safe.EndsWith("_P", StringComparison.Ordinal)) safe += "_P";
            return $"Mods\\{_editShipsAs.Trim()}\\   —   {safe}.pak, .ucas, .utoc";
        }
    }

    /// <summary>Why this folder cannot be used, asked of the project's own validator.</summary>
    public string FolderWhy => ModProject.FolderProblem(_editFolder) ?? "";
    public bool FolderBad => FolderWhy.Length > 0;

    // RECRUIT BY SCRIPT. Only for a mod that has characters on an enemy pawn — the hiring pool
    // rolls a recruit's class, tactical spec and weapon spec away whatever the data says, and the
    // one route that keeps them is a UE4SS script recruiting straight from the pawn class.
    public bool EditByScript
    {
        get => _editByScript;
        set { if (Set(ref _editByScript, value)) Retally(); }
    }
    private bool _editByScript;

    public string EditRecruitKey
    {
        get => _editRecruitKey;
        set { if (Set(ref _editRecruitKey, value)) Retally(); }
    }
    private string _editRecruitKey = "";

    public string EditRepairKey
    {
        get => _editRepairKey;
        set { if (Set(ref _editRepairKey, value)) Retally(); }
    }
    private string _editRepairKey = "";

    /// <summary>What the two keys will do in the Den, with this mod's own characters named.</summary>
    public string RecruitBlurb { get => _recruitBlurb; set => Set(ref _recruitBlurb, value); }
    private string _recruitBlurb = "";


    // ------------------------------------------------------------------ what is inside a copy

    /// <summary>
    /// ONE REFERENCE THE COPIED ASSET HOLDS, and what this mod points it at instead.
    ///
    /// THE TWO MAPS ARE NOT INTERCHANGEABLE, and which one moves a reference is a property of the
    /// reference rather than a choice. A soft object path is two FNames and a rename moves it; a
    /// HARD import is a package id in the container's store entry and no rename can reach it.
    /// Until Core learnt the difference the editor wrote a rename for both, so a weapon's firing
    /// sound could be re-pointed in the panel, reported as changed, built, installed — and still
    /// play the original. The build said so and nobody was reading the build log.
    ///
    /// So the row carries <see cref="Hard"/> and says which it is, and the write asks the row.
    /// </summary>
    public sealed class InsideRow : Obs
    {
        public InsideRow(string property, string export, string path, string package,
                         string kind, bool hard, string mine)
        {
            Property = property;
            Export = export;
            Path = path;
            Package = package;
            Kind = kind;
            Hard = hard;
            _mine = mine;
        }

        /// <summary>The property's name, as the game calls it.</summary>
        public string Property { get; }

        /// <summary>Which export inside the package holds it. In the tooltip, not the row.</summary>
        public string Export { get; }

        /// <summary>The full object path it points at — "/A/B/C.C".</summary>
        public string Path { get; }

        /// <summary>The package half — "/A/B/C". What a re-point is keyed on.</summary>
        public string Package { get; }

        /// <summary>Sound, Mesh, Texture, Material, Ability, Animation, Part, Other.</summary>
        public string Kind { get; }

        /// <summary>See the class remarks. Decides WHICH map the write goes into.</summary>
        public bool Hard { get; }

        /// <summary>The asset name half — what the row reads as while nothing has moved it.</summary>
        public string GameLeaf => Leaf(Path);

        private string _mine;

        /// <summary>What this mod points it at, by package. Empty is the game's own.</summary>
        public string Mine
        {
            get => _mine;
            set
            {
                if (!Set(ref _mine, value)) return;
                Raise(nameof(Shown));
                Raise(nameof(Detail));
                Raise(nameof(IsMine));
            }
        }

        public bool IsMine => _mine.Length > 0;

        public string Shown => _mine.Length > 0 ? Leaf(_mine) : GameLeaf;

        /// <summary>Silent on an unchanged row; names what it stands in for on a changed one.</summary>
        public string Detail => _mine.Length > 0 ? "instead of " + GameLeaf : "";

        /// <summary>The tooltip: where it lives, and which map moves it.</summary>
        public string How => $"{Export}\n{Path}\n" +
            (Hard ? "a hard import — moved by re-pointing"
                  : "a soft path — moved by renaming");

        private string _typed = "";

        /// <summary>The paste box. The escape hatch, not the way in — Choose is the way in.</summary>
        public string Typed { get => _typed; set => Set(ref _typed, value); }

        /// <summary>Whether the filter keeps it. Held on the row so a group can count its own.</summary>
        private bool _kept = true;
        public bool Kept { get => _kept; set => Set(ref _kept, value); }
    }

    /// <summary>
    /// One kind of thing the copy points at, folded.
    ///
    /// GROUPED BECAUSE THE LISTS ARE LONG WHERE IT MATTERS MOST. CPD_WP_Base_01_Default carries
    /// 231 references and 45 of them are meshes — one foregrip for every weapon in the game — so
    /// the row somebody wants is buried among everybody else's by design.
    /// </summary>
    public sealed class InsideGroup : Obs
    {
        public InsideGroup(string kind, IEnumerable<InsideRow> rows)
        {
            Kind = kind;
            foreach (var r in rows) Rows.Add(r);
        }

        public string Kind { get; }
        public ObservableCollection<InsideRow> Rows { get; } = new();

        public string Title => $"{Kind}   ({Rows.Count})";

        /// <summary>How many of them this mod has moved, for the heading.</summary>
        public string Changed =>
            Rows.Count(r => r.IsMine) is var n && n > 0 ? $"{n} changed" : "";

        public bool HasChanged => Rows.Any(r => r.IsMine);

        private bool _open;
        public bool Open
        {
            get => _open;
            set { if (Set(ref _open, value)) Raise(nameof(Chevron)); }
        }

        public Avalonia.Media.Geometry Chevron => Chevrons.For(_open);
    }

    public ObservableCollection<InsideGroup> InsideGroups { get; } = new();

    public bool HasInside => InsideGroups.Count > 0;

    /// <summary>Every row, before the filter. The filter never re-reads the asset.</summary>
    private readonly List<InsideRow> _inside = new();

    /// <summary>Why there is nothing to show, where there is nothing.</summary>
    private string _insideNote = "";
    public string InsideNote { get => _insideNote; set => Set(ref _insideNote, value); }

    private string _insideFilter = "";

    /// <summary>
    /// Narrows the list by property, by asset name, or by path.
    ///
    /// FILTERING OPENS EVERYTHING. A match hidden behind a folded heading is, to the person
    /// looking, a search that found nothing.
    /// </summary>
    public string InsideFilter
    {
        get => _insideFilter;
        set { if (Set(ref _insideFilter, value)) RegroupInside(); }
    }

    /// <summary>
    /// WHAT THE AUTHOR HAS DECIDED about a group, by Kind, kept OUTSIDE the collection that
    /// <see cref="RegroupInside"/> throws away and rebuilds.
    ///
    /// The fold used to live on the group object alone, and RegroupInside runs on every keystroke
    /// in the filter box: it cleared the lot and computed Open from a rule, so a folded group
    /// sprang open by itself, and while any filter text was present the rule said "open" and the
    /// group could not be folded AT ALL. That is the author's "once you collapse them and then
    /// reopen you can't collapse it again", on this surface.
    ///
    /// A DICTIONARY AND NOT A SET OF CLOSED KEYS, because the rule opens some kinds and shuts
    /// others, so both answers are a decision worth keeping: a set could only remember one of the
    /// two directions and the other would be overwritten by the rule on the next keystroke.
    /// </summary>
    private readonly Dictionary<string, bool> _insideFold = new(StringComparer.Ordinal);

    /// <summary>Fold one group AND remember it, which is the half the click used to skip.</summary>
    public void FoldInside(InsideGroup g)
    {
        g.Open = !g.Open;
        _insideFold[g.Kind] = g.Open;
    }

    public void SetInside(IEnumerable<InsideRow> rows)
    {
        // A NEW ASSET IS A NEW SET OF FOLDS. Carrying them over would apply one asset's decisions
        // to another asset's groups, which share nothing but the word "Mesh".
        _insideFold.Clear();
        _inside.Clear();
        foreach (var r in rows)
        {
            r.PropertyChanged += (_, _) => Retally();
            _inside.Add(r);
        }
        RegroupInside();
    }

    /// <summary>One row's new state, without re-reading the asset or losing the filter.</summary>
    public InsideRow? Inside(string path) =>
        _inside.FirstOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<InsideRow> InsideRows => _inside;

    private void RegroupInside()
    {
        var find = _insideFilter.Trim();

        foreach (var r in _inside)
            r.Kept = find.Length == 0 ||
                     r.Property.Contains(find, StringComparison.OrdinalIgnoreCase) ||
                     r.GameLeaf.Contains(find, StringComparison.OrdinalIgnoreCase) ||
                     r.Shown.Contains(find, StringComparison.OrdinalIgnoreCase) ||
                     r.Path.Contains(find, StringComparison.OrdinalIgnoreCase);

        InsideGroups.Clear();
        foreach (var g in _inside.Where(r => r.Kept)
                                 .GroupBy(r => r.Kind)
                                 .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // OPEN WHEN FILTERING, and open for the two kinds somebody is nearly always after.
            // A folded group over a filter's only match is a search that appears to have failed.
            //
            // BUT A DECISION BEATS THE RULE. The rule is a default for a group nobody has touched;
            // once the author has folded or opened one by hand, that answer stands through every
            // later keystroke. Without this the rule fired again on each refill and the fold was
            // undone the moment it was made.
            var group = new InsideGroup(g.Key, g)
            {
                Open = _insideFold.TryGetValue(g.Key, out var chosen)
                    ? chosen
                    : find.Length > 0 || g.Key is "Mesh" or "Sound" || g.Any(r => r.IsMine)
            };
            InsideGroups.Add(group);
        }

        SecInside.Count = _inside.Count == 0 ? ""
            : find.Length > 0 ? $"{_inside.Count(r => r.Kept)} of {_inside.Count}"
            : _inside.Count.ToString();

        Raise(nameof(HasInside));
        Raise(nameof(InsideSays));
        Retally();
    }

    /// <summary>What the list is showing, said rather than left to be counted.</summary>
    public string InsideSays
    {
        get
        {
            if (_inside.Count == 0) return _insideNote;
            var find = _insideFilter.Trim();
            var mine = _inside.Count(r => r.IsMine);
            var moved = mine == 0 ? "" : mine == 1 ? "   ·   1 pointed somewhere else"
                                                   : $"   ·   {mine} pointed somewhere else";
            if (find.Length == 0) return $"{_inside.Count} reference(s){moved}";
            var kept = _inside.Count(r => r.Kept);
            return kept == 0 ? $"nothing in this asset matches “{find}”"
                             : $"{kept} of {_inside.Count} shown{moved}";
        }
    }

    public EditSection SecInside { get; } = new("Inside", "What is inside it");


    // ------------------------------------------------------------------ the tint it paints

    // THE MASK AND THE COLOUR, which together are what makes one part a COLOURWAY of another.
    //
    // The game's eleven Clo000 helmets are eleven PARTS sharing one mesh, each carrying its own
    // mask and its own colour, and nothing links them to a base. So a mod makes a set the same
    // way: duplicate the part and change these two. Neither had an editor here, and TintColor had
    // no reference anywhere in this project at all.
    //
    // A COPY'S ONLY. Both fields live on ModPart, and an override rewrites the GAME's part in
    // place — there is nowhere on it for a tint of the author's to go.

    /// <summary>The mask this part paints through, by package. Empty keeps the template's.</summary>
    public string EditTintMask
    {
        get => _editTintMask;
        set
        {
            if (!Set(ref _editTintMask, value)) return;
            Raise(nameof(TintMaskShown));
            Raise(nameof(HasOwnTintMask));
            Retally();
        }
    }
    private string _editTintMask = "";

    /// <summary>What the template paints through, so the row can name it rather than say "none".</summary>
    public string TemplateTintMask { get; set; } = "";

    public bool HasOwnTintMask => _editTintMask.Length > 0;

    /// <summary>
    /// What the row reads as — YOUR mask, or the template's BY NAME.
    ///
    /// Named rather than "the template's", because the name is the thing somebody needs when they
    /// go looking for it, and because a mask is drawn for ONE kit's UVs: a part built from
    /// another kit's template paints the wrong panels, and that is invisible until the game is
    /// launched. Seeing which mask is in play is the whole of the check.
    /// </summary>
    public string TintMaskShown =>
        _editTintMask.Length > 0 ? Leaf(_editTintMask)
        : TemplateTintMask.Length > 0 ? Leaf(TemplateTintMask)
        : "its template paints no tint";

    /// <summary>
    /// Whether the template carries a material colour at all.
    ///
    /// Measured rather than assumed: a part with no Color parameter has nothing for a hex to be
    /// written into, so the box would take a value that goes nowhere.
    /// </summary>
    public bool TakesTintColour { get => _takesTint; set => Set(ref _takesTint, value); }
    private bool _takesTint;

    /// <summary>Six hex digits. Empty keeps the template's colour.</summary>
    public string EditTintColour
    {
        get => _editTintColour;
        set
        {
            if (!Set(ref _editTintColour, value)) return;
            Raise(nameof(TintColourShown));
            Raise(nameof(HasOwnTintColour));
            Raise(nameof(TintSwatch));
            Raise(nameof(HasTintSwatch));
            Retally();
        }
    }
    private string _editTintColour = "";

    public bool HasOwnTintColour => _editTintColour.Length > 0;

    public string TintColourShown => _editTintColour.Length > 0 ? "#" + _editTintColour
                                                                : "its template's colour";

    /// <summary>The swatch beside the box, or null while the text is not a colour yet.</summary>
    public IBrush? TintSwatch => TintColour.Parse(_editTintColour) is { } c
        ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) : null;

    public bool HasTintSwatch => TintSwatch is not null;

    /// <summary>What this part's tint actually does, filled once the template has been read.</summary>
    public string TintNote { get => _tintNote; set => Set(ref _tintNote, value); }
    private string _tintNote = "";

    public EditSection SecTint { get; } = new("Tint", "Tint");

    // ------------------------------------------------------------------ an enemy group's numbers

    /// <summary>
    /// ONE STAT OF AN ENEMY GROUP, and the multiplier this mod puts on it.
    ///
    /// A MULTIPLIER, NOT A NUMBER, and the reason is the whole shape of the feature: a group is up
    /// to 32 tables and a health change alone is 16 of them, one per tier and difficulty. A
    /// multiplier keeps the game's own curve — a T4 Insane enemy stays harder than a T1 Easy one
    /// — which is what "make Outlaws weaker" means and is not what typing sixteen numbers would
    /// produce.
    ///
    /// A ZERO CANNOT BE SCALED. Anything times zero is zero, so ×2 on a stat the game ships at 0
    /// is still 0 — and a control that silently does nothing is worse than one that says why.
    /// </summary>
    public sealed class EnemyRow : Obs
    {
        public EnemyRow(string row, double low, double high, double multiplier)
        {
            Row = row;
            Low = low;
            High = high;
            _text = multiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            _multiplier = multiplier;
        }

        /// <summary>The full row name — "BitReactorHealthSet.MaxHealth".</summary>
        public string Row { get; }

        /// <summary>Its own name. The set prefix is the same on every row and costs half the column.</summary>
        public string Shown => Row.Contains('.') ? Row[(Row.LastIndexOf('.') + 1)..] : Row;

        /// <summary>The lowest and highest the game ships it at, across the group's tables.</summary>
        public double Low { get; }
        public double High { get; }

        public string Ships => Spread(Low, High);

        /// <summary>Whether scaling it can do anything at all.</summary>
        public bool CanScale => Math.Abs(High) > 0.0001;

        private double _multiplier;
        public double Multiplier => _multiplier;

        private string _text;

        /// <summary>The box. Typed, then read back off the project — never the other way round.</summary>
        public string Text
        {
            get => _text;
            set
            {
                if (!Set(ref _text, value)) return;
                Raise(nameof(Becomes));
                Raise(nameof(Changed));
            }
        }

        /// <summary>The project took this value; take it as what the row holds.</summary>
        public void Took(double m)
        {
            _multiplier = m;
            _text = m.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            Raise(nameof(Text));
            Raise(nameof(Becomes));
            Raise(nameof(Changed));
        }

        public bool Changed => Math.Abs(_multiplier - 1.0) > 0.0001;

        /// <summary>What the game will ship it at once this mod is installed.</summary>
        public string Becomes =>
            !CanScale ? "0 — cannot scale"
            : !Changed ? "unchanged"
            : Spread(Math.Round(Low * _multiplier), Math.Round(High * _multiplier));

        private static string Spread(double lo, double hi) =>
            Math.Abs(lo - hi) < 0.0001 ? $"{lo:0.##}" : $"{lo:0.##} .. {hi:0.##}";
    }

    public ObservableCollection<EnemyRow> EnemyRows { get; } = new();

    public bool HasEnemyRows => EnemyRows.Count > 0;

    /// <summary>Who this group affects, and what copying its tables keeps out of it.</summary>
    public string EnemyNote { get => _enemyNote; set => Set(ref _enemyNote, value); }
    private string _enemyNote = "";

    public void SetEnemyRows(IEnumerable<EnemyRow> rows)
    {
        EnemyRows.Clear();
        foreach (var r in rows)
        {
            r.PropertyChanged += (_, _) => RetallyEnemy();
            EnemyRows.Add(r);
        }
        Raise(nameof(HasEnemyRows));
        RetallyEnemy();
    }

    private void RetallyEnemy()
    {
        var n = EnemyRows.Count(r => r.Changed);
        SecEnemy.Count = EnemyRows.Count == 0 ? ""
            : n > 0 ? $"{n} of {EnemyRows.Count}" : EnemyRows.Count.ToString();
        SecEnemy.Changed = n > 0;
    }

    public EditSection SecEnemy { get; } = new("Enemy", "Their numbers");


    // ------------------------------------------------------------------ how an import fits

    // THE THREE FIELDS THAT MAKE AN IMPORT LINE UP, and the measurement that says what to put in
    // them. They were on the read sheet and nothing wrote them.
    //
    // WITHOUT THE MEASUREMENT THE LOOP IS: export, build, install, launch, squint, guess again —
    // minutes per attempt for one bit of information. Every number here is knowable before the
    // game runs, so it is shown before the game runs.

    /// <summary>
    /// How the file's axes map onto Unreal's. Empty means XZY, which is what the build assumes.
    ///
    /// Stored empty rather than as "XZY" so a project does not carry a field saying the default.
    /// </summary>
    public string EditBasis
    {
        get => _editBasis;
        set { if (Set(ref _editBasis, value)) Retally(); }
    }
    private string _editBasis = "";

    /// <summary>Centimetres per unit in the file. Empty means the build's own default.</summary>
    public string EditScale
    {
        get => _editScale;
        set { if (Set(ref _editScale, value)) Retally(); }
    }
    private string _editScale = "";

    public string EditMoveX { get => _moveX; set { if (Set(ref _moveX, value)) Retally(); } }
    public string EditMoveY { get => _moveY; set { if (Set(ref _moveY, value)) Retally(); } }
    public string EditMoveZ { get => _moveZ; set { if (Set(ref _moveZ, value)) Retally(); } }
    private string _moveX = "0", _moveY = "0", _moveZ = "0";

    /// <summary>True once any of the three carries something other than the default.</summary>
    public bool HasOwnFit { get => _hasOwnFit; set => Set(ref _hasOwnFit, value); }
    private bool _hasOwnFit;

    /// <summary>"12,480 verts, 8,112 tris   scale 2.54   axes XZY", or why it could not be read.</summary>
    public string FitCounts { get => _fitCounts; set => Set(ref _fitCounts, value); }
    private string _fitCounts = "";

    /// <summary>The mesh being replaced, measured.</summary>
    public string FitTemplate { get => _fitTemplate; set => Set(ref _fitTemplate, value); }
    private string _fitTemplate = "";

    /// <summary>The author's own, measured, with how many times the size it is.</summary>
    public string FitYours { get => _fitYours; set => Set(ref _fitYours, value); }
    private string _fitYours = "";

    /// <summary>
    /// Whether the two are more than a quarter apart on any axis.
    ///
    /// Not a refusal — a barrel replacing a barrel need not be the same length — but it is the
    /// number somebody should look at before spending an evening on the model.
    /// </summary>
    public bool FitOff { get => _fitOff; set => Set(ref _fitOff, value); }
    private bool _fitOff;

    /// <summary>
    /// THE BAND THE BUILD REFUSES, said here rather than only in the build log.
    ///
    /// ModBuild throws out an import more than 5× or less than 0.2× the height of the mesh it
    /// replaces, ships the GAME's mesh in its place, and prints the number to set — one line in
    /// the middle of a long build. An author who does not read it gets a mod that builds clean
    /// and has none of their model in it. That has happened here, on a cell mesh that came out
    /// 102× too big.
    /// </summary>
    public string FitRefuse { get => _fitRefuse; set { if (Set(ref _fitRefuse, value)) Raise(nameof(FitRefused)); } }
    private string _fitRefuse = "";

    public bool FitRefused => _fitRefuse.Length > 0;

    /// <summary>The scale that would make this mesh the same height as the one it replaces.</summary>
    public string FitWants { get => _fitWants; set { if (Set(ref _fitWants, value)) Raise(nameof(HasFitWants)); } }
    private string _fitWants = "";

    public bool HasFitWants => _fitWants.Length > 0;

    /// <summary>Whatever else the measurement noticed — usually about bones.</summary>
    public ObservableCollection<string> FitNotes { get; } = new();
    public bool HasFitNotes => FitNotes.Count > 0;

    /// <summary>Whether there is anything to measure at all: an import, and a mesh to compare to.</summary>
    public bool CanFit { get => _canFit; set => Set(ref _canFit, value); }
    private bool _canFit;

    public void SetFitNotes(IEnumerable<string> notes)
    {
        FitNotes.Clear();
        foreach (var n in notes) FitNotes.Add(n);
        Raise(nameof(HasFitNotes));
    }

    public EditSection SecFit { get; } = new("Fit", "How it fits");


    // ------------------------------------------------------------------ selling it in the Armoury

    /// <summary>
    /// ONE SLOT ON THE ARMOURY'S SHELVES, as the chooser offers it.
    ///
    /// NAMED BY WHAT IT SELLS TODAY, not by an index. "Tier 1 — currently the Ion Blaster" is a
    /// decision somebody can make; "entry 15" is not.
    /// </summary>
    public sealed record ShopSlot(string Describe, string ItemLeaf, string Tier)
    {
        public override string ToString() => Describe;
    }

    public ObservableCollection<ShopSlot> ShopSlots { get; } = new();

    public bool HasShopSlots => ShopSlots.Count > 0;

    private ShopSlot? _sellSlot;
    public ShopSlot? SellSlot
    {
        get => _sellSlot;
        set { if (Set(ref _sellSlot, value)) Raise(nameof(CanSell)); }
    }

    public bool CanSell => _sellSlot is not null;

    public void SetShopSlots(IEnumerable<ShopSlot> slots)
    {
        ShopSlots.Clear();
        foreach (var s in slots) ShopSlots.Add(s);
        SellSlot = ShopSlots.FirstOrDefault();
        Raise(nameof(HasShopSlots));
    }

    /// <summary>Where it sells now, or empty. Read back out of the project, never remembered.</summary>
    public string OnSaleAs
    {
        get => _onSale;
        private set
        {
            // ONE FLAG, AND THE MARKUP NEGATES IT WITH ITS OWN ! OPERATOR.
            //
            // This briefly had a NotOnSale twin, and raising IsOnSale did not raise it — so the
            // card drew the on-sale block AND the chooser at once, with "on sale" printed over
            // "nothing offers it yet" in the same cell. A binding's ! re-evaluates when the
            // property it negates is raised, so there is no second raise to forget. Every other
            // negated state in this markup is already written that way; this was the one that
            // was not, and it is the only one that broke.
            if (!Set(ref _onSale, value)) return;
            Raise(nameof(IsOnSale));
        }
    }
    private string _onSale = "";

    public bool IsOnSale => _onSale.Length > 0;

    /// <summary>What that costs, or what to do about not being on sale at all.</summary>
    public string SellNote { get => _sellNote; set => Set(ref _sellNote, value); }
    private string _sellNote = "";

    public void SetOnSale(string where, string note)
    {
        OnSaleAs = where;
        SellNote = note;
        SecSell.Count = where.Length > 0 ? "on sale" : "";
        SecSell.Changed = where.Length > 0;
    }

    public EditSection SecSell { get; } = new("Sell", "Sell it in the Armoury");


    // ------------------------------------------------------------------ a mission's own cards

    // A MISSION IS DATA, and this is that data: where it is fought, who you fight, what you do,
    // how big the squad is, what it pays and what it remembers. The one part that is NOT data is
    // the map — objective actors, spawner placements and reinforcement pins live in the level —
    // so the map is a thing you CHOOSE rather than a thing you make.
    //
    // EVERY VALUE THE GAME KEEPS AS A TAG, A ROW NAME OR AN ASSET PATH IS A CHOOSER over what the
    // game actually has. The old studio shipped seven of these as empty text boxes and the author
    // opened it and said he did not know what was supposed to go in them — which is the only
    // possible response to a blank box labelled "Image bank tag". Nobody types
    // `/Game/Game/GameData/Rewards/SharedRewards/Rewards_Missions_Shared` from memory, and a near
    // miss is not an error: it is a mission that builds clean and gives no reward.

    /// <summary>One option in a mission chooser: what is stored, what is shown, and an aside.</summary>
    public sealed record Choice(string Value, string Shown, string Aside = "")
    {
        public bool HasAside => Aside.Length > 0;
        public override string ToString() => Shown;
    }

    /// <summary>
    /// One line of a list a mission carries — an objective, a reward, an extra tag.
    ///
    /// A ROW RATHER THAN A JOINED STRING, because these are separate values and a text box full
    /// of newlines is a value nobody can pick from a list.
    /// </summary>
    public sealed class LineRow : Obs
    {
        public LineRow(string kind, string value)
        {
            Kind = kind;
            Value = value;
        }

        /// <summary>Which list it belongs to — "objective", "reward", "tag".</summary>
        public string Kind { get; }
        public string Value { get; }

        /// <summary>The last dotted segment, which is what a gameplay tag is actually called.</summary>
        public string Shown => Value.Contains('.') ? Value[(Value.LastIndexOf('.') + 1)..]
                             : Value.Contains('/') ? Value[(Value.LastIndexOf('/') + 1)..]
                             : Value;

        public bool Long => Shown != Value;
    }

    public ObservableCollection<LineRow> Objectives { get; } = new();
    public ObservableCollection<LineRow> Rewards { get; } = new();
    public ObservableCollection<LineRow> MissionTags { get; } = new();

    public bool HasObjectives => Objectives.Count > 0;
    public bool HasRewards => Rewards.Count > 0;
    public bool HasMissionTags => MissionTags.Count > 0;

    public void SetMissionLists(IEnumerable<string> objectives, IEnumerable<string> rewards,
                                IEnumerable<string> tags)
    {
        Objectives.Clear();
        foreach (var o in objectives) Objectives.Add(new LineRow("objective", o));
        Rewards.Clear();
        foreach (var r in rewards) Rewards.Add(new LineRow("reward", r));
        MissionTags.Clear();
        foreach (var t in tags) MissionTags.Add(new LineRow("tag", t));

        Raise(nameof(HasObjectives));
        Raise(nameof(HasRewards));
        Raise(nameof(HasMissionTags));
        SecBriefing.Count = Objectives.Count > 0 ? Objectives.Count.ToString() : "";
    }

    // ---- the briefing

    /// <summary>The long description, which is the paragraph the briefing screen prints.</summary>
    public string EditBriefing
    {
        get => _editBriefing;
        set { if (Set(ref _editBriefing, value)) Retally(); }
    }
    private string _editBriefing = "";

    // ---- where

    public ObservableCollection<Choice> PlanetChoices { get; } = new();
    public ObservableCollection<Choice> LevelChoices { get; } = new();
    public ObservableCollection<Choice> FactionChoices { get; } = new();
    public ObservableCollection<Choice> TypeChoices { get; } = new();
    public ObservableCollection<Choice> DifficultyChoices { get; } = new();

    private Choice? _planet;

    /// <summary>
    /// THE PLANET IS A ROW NAME, not free text — a row of a region location table, and a name the
    /// table does not have is a mission on no planet.
    /// </summary>
    public Choice? PickedPlanet { get => _planet; set => Set(ref _planet, value); }

    private Choice? _level;

    /// <summary>
    /// The map. The one part of a mission nobody can author — the objective actors, the enemy
    /// spawn points and the reinforcement pins are placed in the level itself — so this is a
    /// thing you choose. The game runs 125 missions on 74 maps and one Kaller map carries a
    /// Separatist Capture, an Imperial Rescue and an Imperial Sabotage.
    /// </summary>
    public Choice? PickedLevel { get => _level; set => Set(ref _level, value); }

    private Choice? _faction;

    /// <summary>
    /// WHO SPAWNS AGAINST YOU — and only the factions that CAN.
    ///
    /// The tag list holds twelve; five have a FactionData asset, and that asset owns the spawn
    /// sets a level draws from. A mission naming one of the other seven has no roster at all and
    /// spawns nobody — silently, with a clean build.
    /// </summary>
    public Choice? PickedFaction { get => _faction; set => Set(ref _faction, value); }

    private Choice? _type;
    public Choice? PickedType { get => _type; set => Set(ref _type, value); }

    private Choice? _difficulty;
    public Choice? PickedDifficulty { get => _difficulty; set => Set(ref _difficulty, value); }

    /// <summary>What the game itself files this planet under, when the mission disagrees.</summary>
    public string PlanetNote { get => _planetNote; set { if (Set(ref _planetNote, value)) Raise(nameof(HasPlanetNote)); } }
    private string _planetNote = "";
    public bool HasPlanetNote => _planetNote.Length > 0;

    /// <summary>Whose map this already is. The most surprising thing about this editor.</summary>
    public string LevelNote { get => _levelNote; set { if (Set(ref _levelNote, value)) Raise(nameof(HasLevelNote)); } }
    private string _levelNote = "";
    public bool HasLevelNote => _levelNote.Length > 0;

    /// <summary>Said in the danger colour: this faction puts nobody on the map.</summary>
    public string FactionWarn { get => _factionWarn; set { if (Set(ref _factionWarn, value)) Raise(nameof(HasFactionWarn)); } }
    private string _factionWarn = "";
    public bool HasFactionWarn => _factionWarn.Length > 0;

    /// <summary>The location table the planet belongs to. Read-only: it follows the planet.</summary>
    public string LocationTableIs { get => _locTable; set => Set(ref _locTable, value); }
    private string _locTable = "";

    public string EditRegionTag { get => _regionTag; set { if (Set(ref _regionTag, value)) Retally(); } }
    private string _regionTag = "";

    public string EditRegionAsset { get => _regionAsset; set { if (Set(ref _regionAsset, value)) Retally(); } }
    private string _regionAsset = "";

    public string EditMapConfig { get => _mapConfig; set { if (Set(ref _mapConfig, value)) Retally(); } }
    private string _mapConfig = "";

    // ---- how big, and how long

    public string EditMinSquad { get => _minSquad; set { if (Set(ref _minSquad, value)) Retally(); } }
    public string EditMaxSquad { get => _maxSquad; set { if (Set(ref _maxSquad, value)) Retally(); } }
    public string EditLifeSpan { get => _lifeSpan; set { if (Set(ref _lifeSpan, value)) Retally(); } }
    private string _minSquad = "4", _maxSquad = "4", _lifeSpan = "4";

    public bool EditBlockActive { get => _blockActive; set { if (Set(ref _blockActive, value)) Retally(); } }
    public bool EditBlockDone { get => _blockDone; set { if (Set(ref _blockDone, value)) Retally(); } }
    private bool _blockActive = true, _blockDone = true;

    // ---- what it pays, and what it remembers

    public string EditRewardTable { get => _rewardTable; set { if (Set(ref _rewardTable, value)) Retally(); } }
    private string _rewardTable = "";

    public bool EditPreRoll { get => _preRoll; set { if (Set(ref _preRoll, value)) Retally(); } }
    private bool _preRoll = true;

    /// <summary>
    /// THE FACT THIS MISSION RAISES WHEN IT IS DONE — the whole of "appears only after that one".
    ///
    /// A mission's rewards can carry CustomReward_IncrementFactValue, and another mission's TABLE
    /// ROW can require it. That pair is the lever an era gate uses, and it is the reason the fact
    /// chooser offers facts missions RAISE and facts rows TEST together: a gate needs one of each
    /// and they are not the same set.
    /// </summary>
    public string EditFactTag { get => _factTag; set { if (Set(ref _factTag, value)) Retally(); } }
    private string _factTag = "";

    public string EditFactIncrement { get => _factInc; set { if (Set(ref _factInc, value)) Retally(); } }
    private string _factInc = "1";

    /// <summary>The picture on the mission card, and the bank tag it falls back to.</summary>
    public string EditThumbnail { get => _thumb; set { if (Set(ref _thumb, value)) Retally(); } }
    private string _thumb = "";

    public string EditBankTag { get => _bankTag; set { if (Set(ref _bankTag, value)) Retally(); } }
    private string _bankTag = "";

    /// <summary>
    /// EDITING THE GAME'S OWN MISSION instead of shipping one beside it.
    ///
    /// The author's own idea, and a better first test than the alternative: a NEW mission has to
    /// be drawn from a weighted table before anyone can look at it, so a failure could be the
    /// mission, the table, the pull schedule or the save's position — four suspects and a
    /// campaign turn per attempt. Editing one already on the map has one suspect and needs no
    /// turn at all.
    /// </summary>
    public bool EditsInPlace { get => _inPlace; set => Set(ref _inPlace, value); }
    private bool _inPlace;

    /// <summary>What that choice costs, said where it is made.</summary>
    public string InPlaceNote { get => _inPlaceNote; set => Set(ref _inPlaceNote, value); }
    private string _inPlaceNote = "";


    // ------------------------------------------------------------------ when a mission turns up

    // THREE ROUTES, AND THEY COST DIFFERENT THINGS. A mission that exists is offered to nobody:
    // the galaxy map places some missions directly and draws the rest from weighted tables, and
    // neither consults what a mod happens to ship. So the card is the three ways in, in the order
    // of what they take away:
    //
    //   add it to a turn   the turn keeps everything it already placed AND gains this one.
    //                      Costs nothing and cannot lose. The author's own ask, after being
    //                      offered three that each traded something: "Wish there was a way to
    //                      just make it appear without doing something like that."
    //   take a slot        the campaign places 20 missions itself with no roll. Taking one is
    //                      certain, and costs the game the mission that sat there.
    //   a table row        one weighted candidate among several, drawn only on the turns that
    //                      pull from that table. Cheapest, and it may never come up.

    /// <summary>One campaign turn this mission could be ADDED to. Nothing is displaced.</summary>
    public sealed class TurnRow : Obs
    {
        public TurnRow(string entry, string arc, int number, string already, bool alone, bool taken)
        {
            Entry = entry;
            Arc = arc;
            Number = number;
            Already = already;
            Alone = alone;
            _taken = taken;
        }

        /// <summary>`Arc|Turn`, the form ModMission stores. A pipe because a package holds slashes.</summary>
        public string Entry { get; }
        public string Arc { get; }
        public int Number { get; }
        public string Turn => $"turn {Number}";

        /// <summary>What the turn already does, so an empty one can be recognised.</summary>
        public string Already { get; }

        /// <summary>Nothing placed and nothing pulled — yours would arrive alone.</summary>
        public bool Alone { get; }

        private bool _taken;
        public bool Taken { get => _taken; set => Set(ref _taken, value); }
    }

    /// <summary>
    /// One campaign slot this mission could TAKE, named by what it would displace.
    ///
    /// WHAT IT COSTS IS THE GAME'S OWN NAMING: MM_ is main story, HM_ a hero mission, SK_ an
    /// ordinary optional skirmish. Only the last is cheap, and only two of the shipped twenty are
    /// one.
    /// </summary>
    public sealed class SlotRow : Obs
    {
        public SlotRow(string package, string leaf, string placedBy, string costs,
                       string kind, bool finale, bool taken)
        {
            Package = package;
            Leaf = leaf;
            PlacedBy = placedBy;
            Costs = costs;
            Kind = kind;
            Finale = finale;
            _taken = taken;
        }

        public string Package { get; }
        public string Leaf { get; }

        /// <summary>"A1 turn 0, A1 turn 2" — every arc and turn that places it.</summary>
        public string PlacedBy { get; }

        public string Costs { get; }

        /// <summary>"story", "hero" or "cheap" — which colour the cost is drawn in.</summary>
        public string Kind { get; }
        public bool Cheap => Kind == "cheap";
        public bool Dear => Kind is "story" or "finale";
        public bool Middling => Kind == "hero";

        /// <summary>
        /// THE FINALE IS NOT ON OFFER. Displacing the mission the campaign ends on is the one
        /// choice here that can leave a save with no way forward, and an author cannot find that
        /// out until they have played to it.
        /// </summary>
        public bool Finale { get; }
        public bool CanTake => !Finale;

        private bool _taken;
        public bool Taken { get => _taken; set => Set(ref _taken, value); }
    }

    /// <summary>One numeric gate on a table row — "this fact is under two".</summary>
    public sealed class GateRow : Obs
    {
        public GateRow(object held, string tag, string operation, string value, bool missingIsZero)
        {
            Held = held;
            _tag = tag;
            _operation = operation;
            _value = value;
            _zero = missingIsZero;
        }

        /// <summary>The ModFactCondition behind it. Held as object so SheetTab needs no Core type.</summary>
        public object Held { get; }

        private string _tag;
        public string Tag { get => _tag; set => Set(ref _tag, value); }

        public string TagShown => _tag.Length == 0 ? "(pick a fact)"
                               : _tag.Contains('.') ? _tag[(_tag.LastIndexOf('.') + 1)..] : _tag;

        private string _operation;
        public string Operation { get => _operation; set => Set(ref _operation, value); }

        private string _value;
        public string Value { get => _value; set => Set(ref _value, value); }

        private bool _zero;

        /// <summary>A fact nobody has raised reads as zero rather than failing the test.</summary>
        public bool MissingIsZero { get => _zero; set => Set(ref _zero, value); }
    }

    /// <summary>
    /// ONE ROW IN ONE WEIGHTED TABLE — the lottery route.
    ///
    /// NOTHING TIES A TABLE TO A FACTION. The game lines them up by naming convention alone, so a
    /// Separatist mission whose row sits in the Imperial table, gated on a fact only the late
    /// campaign raises, is "Separatist holdouts in the Empire era". Both axes have always been
    /// there; the game simply never crossed them.
    /// </summary>
    public sealed class LotteryRow : Obs
    {
        public LotteryRow(object held, string table, string weight, bool addsRow, int whichRow)
        {
            Held = held;
            _table = table;
            _weight = weight;
            _addsRow = addsRow;
            _whichRow = whichRow;
        }

        /// <summary>The ModMissionRow behind it.</summary>
        public object Held { get; }

        private string _table;
        public string Table { get => _table; set { if (Set(ref _table, value)) Raise(nameof(TableLeaf)); } }

        public string TableLeaf => _table.Length == 0 ? "(pick a table)"
                                 : _table[(_table.LastIndexOf('/') + 1)..];

        private string _weight;
        public string Weight { get => _weight; set => Set(ref _weight, value); }

        private bool _addsRow;

        /// <summary>
        /// ADD A ROW, or take one over.
        ///
        /// PREFER ADDING, and that is not a style preference: taking over costs the game the
        /// mission that sat there, and no table has a spare row — bEmptyOption is false on all 65
        /// of them. Adding is proven machinery now that PropertyAdd.AddWeightedMission exists.
        /// </summary>
        public bool AddsRow
        {
            get => _addsRow;
            set
            {
                if (!Set(ref _addsRow, value)) return;
                Raise(nameof(TakesOver));
            }
        }

        public bool TakesOver => !_addsRow;

        private int _whichRow;
        public int WhichRow { get => _whichRow; set => Set(ref _whichRow, value); }

        /// <summary>The rows of the chosen table, for the take-over chooser.</summary>
        public ObservableCollection<Choice> Rows { get; } = new();

        private Choice? _pickedRow;
        public Choice? PickedRow { get => _pickedRow; set => Set(ref _pickedRow, value); }

        /// <summary>What this row would do to the table, in the table's own numbers.</summary>
        public string Effect { get => _effect; set => Set(ref _effect, value); }
        private string _effect = "";

        public ObservableCollection<LineRow> Requires { get; } = new();
        public ObservableCollection<LineRow> Ignores { get; } = new();
        public ObservableCollection<GateRow> Gates { get; } = new();

        public bool HasRequires => Requires.Count > 0;
        public bool HasIgnores => Ignores.Count > 0;
        public bool HasGates => Gates.Count > 0;

        public void Remark()
        {
            Raise(nameof(HasRequires));
            Raise(nameof(HasIgnores));
            Raise(nameof(HasGates));
        }
    }

    public ObservableCollection<TurnRow> Turns { get; } = new();
    public ObservableCollection<SlotRow> CampaignSlots { get; } = new();
    public ObservableCollection<LotteryRow> LotteryRows { get; } = new();

    public bool HasTurns => Turns.Count > 0;
    public bool HasCampaignSlots => CampaignSlots.Count > 0;
    public bool HasLotteryRows => LotteryRows.Count > 0;

    /// <summary>Every weighted table the game has, for the lottery chooser.</summary>
    public ObservableCollection<Choice> TableChoices { get; } = new();

    /// <summary>The comparisons a numeric gate can make. The enum's own names.</summary>
    public IReadOnlyList<string> GateOperations { get; } =
        ["LessThan", "LessThanOrEqual", "Equal", "NotEqual", "GreaterThanOrEqual", "GreaterThan"];

    /// <summary>
    /// WHETHER ANYTHING WILL EVER OFFER THIS MISSION, said as plainly as it can be.
    ///
    /// A mission in no table, on no turn and in no slot builds, installs, reads back clean and is
    /// never drawn — which from the outside is a mod that did not work.
    /// </summary>
    public string WhenSays { get => _whenSays; set { if (Set(ref _whenSays, value)) Raise(nameof(NeverOffered)); } }
    private string _whenSays = "";

    public bool NeverOffered { get => _never; set => Set(ref _never, value); }
    private bool _never;

    public void SetWhen(IEnumerable<TurnRow> turns, IEnumerable<SlotRow> slots,
                        IEnumerable<LotteryRow> rows)
    {
        Turns.Clear();
        foreach (var t in turns) Turns.Add(t);
        CampaignSlots.Clear();
        foreach (var s in slots) CampaignSlots.Add(s);
        LotteryRows.Clear();
        foreach (var r in rows) LotteryRows.Add(r);

        Raise(nameof(HasTurns));
        Raise(nameof(HasCampaignSlots));
        Raise(nameof(HasLotteryRows));
    }

    public EditSection SecWhen { get; } = new("When", "When it turns up");

    public EditSection SecBriefing { get; } = new("Briefing", "Briefing");
    public EditSection SecPlace { get; } = new("Place", "Where it is fought");
    public EditSection SecFight { get; } = new("Fight", "The fight");
    public EditSection SecPays { get; } = new("Pays", "What it pays");

    public EditSection SecMade { get; } = new("Made", "Who made it");
    public EditSection SecPicture { get; } = new("Picture", "Picture");
    public EditSection SecShips { get; } = new("Ships", "Where it ships");
    public EditSection SecRecruit { get; } = new("Recruit", "Recruit by script");

    // ------------------------------------------------------------------ what an ability does

    public EditSection SecEffects { get; } = new("Effects", "Numbers");
    public EditSection SecPoints { get; } = new("Points", "What it points at");
    public EditSection SecGates { get; } = new("Gates", "Tag containers");

    /// <summary>
    /// ONE NUMBER AN ABILITY OR ITS EFFECT CARRIES.
    ///
    /// TWO KINDS IN ONE LIST, and the difference is the whole of the right-hand cell. A number
    /// the asset CARRIES is four bytes written over four. A property it does NOT carry is absent
    /// because it sits at the class default, and giving it a value INSERTS the property and
    /// rebuilds the export's unversioned header around it. That is how the game itself does
    /// tiers — GE_Cooldown_Medkit_T2 carries UsesPerCooldown 2 and the once-per-encounter ones
    /// simply leave it out.
    ///
    /// And a third state that is neither: a zero INSIDE an array element. A cooked file leaves a
    /// zero out entirely, and an array element cannot be grown the way a whole property can, so
    /// there is nothing to write over and the row says so rather than offering a box that fails.
    /// </summary>
    public sealed class EffectRow : Obs
    {
        public string Export { get; init; } = "";
        public string Path { get; init; } = "";
        public string Label { get; init; } = "";
        public string Detail { get; init; } = "";

        /// <summary>The tooltip: which export and which property this actually is.</summary>
        public string Where { get; init; } = "";

        /// <summary>What the game ships. "—" when the file does not carry the property.</summary>
        public string Now { get; init; } = "";

        /// <summary>The asset carries it, so there are bytes to write over.</summary>
        public bool Stored { get; init; }

        /// <summary>The asset leaves it at the default, so giving it a value ADDS the property.</summary>
        public bool Adds { get; init; }

        /// <summary>Neither: a zero inside an array element, which cannot be reached.</summary>
        public bool CanType => Stored || Adds;

        /// <summary>Where else this number is read from, when the reading found somewhere.</summary>
        public string Note { get; init; } = "";
        public bool HasNote => Note.Length > 0;

        /// <summary>
        /// A button beside the row, where the row has an act of its own that a box cannot say.
        /// "Give it one", on a modification that carries no number of its own: the box is not
        /// offered, because a box pre-filled with a value that is not in the asset invites an
        /// edit whose "was" is a guess.
        /// </summary>
        public string ActionLabel { get; init; } = "";
        public bool HasAction => ActionLabel.Length > 0;

        /// <summary>What is in the box. A string, because a half-typed number is not a double.</summary>
        public string Value { get => _value; set => Set(ref _value, value); }
        private string _value = "";

        /// <summary>This mod changes it.</summary>
        public bool Mine { get => _mine; set => Set(ref _mine, value); }
        private bool _mine;

        /// <summary>The right-hand cell when nothing has been typed: why, in the author's words.</summary>
        public string State { get => _state; set => Set(ref _state, value); }
        private string _state = "";
        public bool HasState => _state.Length > 0;
    }

    public ObservableCollection<EffectRow> EffectRows { get; } = new();
    public bool HasEffectRows => EffectRows.Count > 0;

    /// <summary>
    /// A CLASS-VALUED PROPERTY: which other asset does the work.
    ///
    /// A cooldown effect holds the uses per combat, not the ability. Where one is INHERITED every
    /// tier shares it, so changing that asset changes all of them — which is said on the row,
    /// because it is the difference between editing one tier and editing five.
    /// </summary>
    public sealed class PointRow : Obs
    {
        public string Export { get; init; } = "";
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string Where { get; init; } = "";

        /// <summary>"uses GE_Cooldown_Medkit, inherited from GA_Medkit_T1".</summary>
        public string Uses { get; init; } = "";

        /// <summary>The native class the registry narrows candidates by. Not shown; used to ask.</summary>
        public string Kind { get; init; } = "";

        /// <summary>What that kind is called in words, for the picker's heading.</summary>
        public string KindName { get; init; } = "";

        public string Mine { get => _mine; set { if (Set(ref _mine, value)) Raise(nameof(HasMine)); } }
        private string _mine = "";
        public bool HasMine => _mine.Length > 0;

        /// <summary>
        /// THE ESCAPE HATCH, and deliberately not checked against the picker's list.
        ///
        /// The narrowing is derived from what the game does, which is a good default and a bad
        /// law. The build still refuses a target with no class export to import, which is the
        /// check that protects anything.
        /// </summary>
        public string Typed { get => _typed; set => Set(ref _typed, value); }
        private string _typed = "";
    }

    public ObservableCollection<PointRow> PointRows { get; } = new();
    public bool HasPointRows => PointRows.Count > 0;

    /// <summary>
    /// A TAG CONTAINER — a gate, mostly: what has to be true for this to fire.
    ///
    /// THE ONE TRAP: a container is a SET, and giving one to this asset REPLACES whatever the
    /// chain held rather than joining it. Every Rocket Strike tier inherits five
    /// ActivationBlockedTags from T1, so giving T3 a container holding one tag quietly drops the
    /// other four for that tier alone, and the asset shows no sign of it because an inherited
    /// value is written nowhere in the asset that uses it.
    ///
    /// So Add STARTS FROM THE INHERITED SET. Somebody adding one tag to a gate adds one tag;
    /// somebody replacing the set can still clear the box. Neither has to know the difference
    /// existed.
    /// </summary>
    public sealed class TagBoxRow : Obs
    {
        public string Export { get; init; } = "";
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string Where { get; init; } = "";

        /// <summary>"holds Delayed, Disabled, Dazed — inherited from GA_RocketStrike_T1".</summary>
        public string Holds { get; init; } = "";

        /// <summary>How many tags Add would start from, so the button can say so.</summary>
        public string StartsFrom { get; init; } = "";
        public bool HasStartsFrom => StartsFrom.Length > 0;

        /// <summary>This mod gives the asset its own copy of this container.</summary>
        public bool Added { get => _added; set => Set(ref _added, value); }
        private bool _added;

        /// <summary>One tag per line. Kept when empty, so the box does not vanish mid-retype.</summary>
        public string Text { get => _text; set => Set(ref _text, value); }
        private string _text = "";
    }

    public ObservableCollection<TagBoxRow> TagBoxRows { get; } = new();
    public bool HasTagBoxRows => TagBoxRows.Count > 0;

    /// <summary>What the reading found, or why it found nothing. Shown above the three.</summary>
    public string EffectsNote
    {
        get => _effectsNote;
        set { if (Set(ref _effectsNote, value)) Raise(nameof(HasEffectsNote)); }
    }
    private string _effectsNote = "";
    public bool HasEffectsNote => _effectsNote.Length > 0;

    /// <summary>Where the REST of an ability's numbers live: the GE_ packages it applies.</summary>
    public string EffectsElsewhere
    {
        get => _effectsElsewhere;
        set { if (Set(ref _effectsElsewhere, value)) Raise(nameof(HasEffectsElsewhere)); }
    }
    private string _effectsElsewhere = "";
    public bool HasEffectsElsewhere => _effectsElsewhere.Length > 0;

    /// <summary>
    /// ONE LINE ABOUT THE NUMBER JUST TYPED, drawn in the card where the person is looking.
    ///
    /// The log has the same sentence, but the log is a strip along the foot of the window that
    /// may be scrolled or shut: a box that quietly returns to its old value, with the reason
    /// only down there, reads as a box that does not work. Red when the typing was refused,
    /// amber when it was accepted with a change the person should know about (a rounding).
    /// Cleared by the next number typed.
    /// </summary>
    public string EffectsFeedback { get => _effectsFeedback; private set => Set(ref _effectsFeedback, value); }
    private string _effectsFeedback = "";

    /// <summary>Whether <see cref="EffectsFeedback"/> is a refusal (red) rather than a note (amber).</summary>
    public bool EffectsFeedbackBad { get => _effectsFeedbackBad; private set => Set(ref _effectsFeedbackBad, value); }
    private bool _effectsFeedbackBad;

    public bool HasEffectsRefusal => _effectsFeedback.Length > 0 && _effectsFeedbackBad;
    public bool HasEffectsFeedbackNote => _effectsFeedback.Length > 0 && !_effectsFeedbackBad;

    /// <summary>Say something about the number just typed, or (empty) clear the last thing said.</summary>
    public void Feedback(string text, bool bad = false)
    {
        EffectsFeedback = text;
        EffectsFeedbackBad = bad;
        Raise(nameof(HasEffectsRefusal));
        Raise(nameof(HasEffectsFeedbackNote));
    }

    /// <summary>
    /// What "Now" means and what the right-hand cell can say, under the note.
    ///
    /// A PROPERTY, because the card is shared and its cells do not all mean the same thing: an
    /// ability's number can be "not in file" (the asset leaves it at the class default), a table's
    /// can only be "zero, not stored", and a line that names the wrong one is a line that sends
    /// somebody looking for a row that cannot exist.
    /// </summary>
    public string EffectsHelp { get => _effectsHelp; set => Set(ref _effectsHelp, value); }
    private string _effectsHelp =
        "'Now' is what the game ships. Anything you leave alone is not shipped, and a row marked " +
        "'not in file' is one the asset leaves at its default, so giving it a value ADDS the property.";

    /// <summary>
    /// The card has a find box. A table can hold thousands of numbers and a list that shows the
    /// first few hundred and says nothing about the rest has hidden them, so the box is how the
    /// rest are reached. Off for an ability, whose handful of numbers needs no search.
    /// </summary>
    public bool EffectsFilterable { get => _effectsFilterable; set => Set(ref _effectsFilterable, value); }
    private bool _effectsFilterable;

    /// <summary>What is typed in the find box. The view model refills the list when it changes.</summary>
    public string EffectsFilter
    {
        get => _effectsFilter;
        set { if (Set(ref _effectsFilter, value ?? "")) EffectsFilterChanged?.Invoke(); }
    }
    private string _effectsFilter = "";

    /// <summary>Set by whoever fills this card, so a typed filter reaches the list it narrows.</summary>
    public Action? EffectsFilterChanged { get; set; }

    /// <summary>
    /// The reading has finished, so the three cards hold what the ability actually has.
    ///
    /// A FLAG RATHER THAN A SENTINEL STRING: without it the only way to know was comparing
    /// EffectsNote against "reading what it does", which is a test that passes the day somebody
    /// rewords a note.
    /// </summary>
    public bool EffectsRead { get => _effectsRead; set => Set(ref _effectsRead, value); }
    private bool _effectsRead;

    /// <summary>All three at once, under one fill.</summary>
    public void EffectsSay(IEnumerable<EffectRow> numbers, IEnumerable<PointRow> points,
                           IEnumerable<TagBoxRow> gates)
    {
        EffectRows.Clear();
        foreach (var r in numbers) EffectRows.Add(r);
        PointRows.Clear();
        foreach (var r in points) PointRows.Add(r);
        TagBoxRows.Clear();
        foreach (var r in gates) TagBoxRows.Add(r);

        Raise(nameof(HasEffectRows));
        Raise(nameof(HasPointRows));
        Raise(nameof(HasTagBoxRows));
    }

    // ------------------------------------------------------------------ what a modification sells for

    /// <summary>
    /// THE OTHER HALF OF A MODIFICATION: what the Black Market calls it, what it says, how rare it
    /// is and what it costs. A modification is TWO assets -- the ability holds what it does, the
    /// shop item holds all of this -- so this is its own card, and it writes to a second override.
    /// </summary>
    public EditSection SecShop { get; } = new("Shop", "In the Black Market");

    /// <summary>The name the mod gives it, or empty for the game's own (the grey text in the box).</summary>
    public string ShopName { get => _shopName; set => Set(ref _shopName, value ?? ""); }
    private string _shopName = "";

    /// <summary>The sentence under the name in the shop, or empty for the game's own.</summary>
    public string ShopText { get => _shopText; set => Set(ref _shopText, value ?? ""); }
    private string _shopText = "";

    /// <summary>What the game calls it and says about it: shown as the boxes' grey hint.</summary>
    public string ShopNameGame { get => _shopNameGame; set => Set(ref _shopNameGame, value); }
    private string _shopNameGame = "";
    public string ShopTextGame { get => _shopTextGame; set => Set(ref _shopTextGame, value); }
    private string _shopTextGame = "";

    /// <summary>The four rarities the game ships, in the order it uses them, as one word each.</summary>
    public ObservableCollection<string> ShopRarities { get; } = new();

    /// <summary>False for a shop item that carries no rarity tag: there is nothing to choose between.</summary>
    public bool HasRarities => ShopRarities.Count > 0;

    /// <summary>The rarity chosen (the game's own when the mod has not changed it).</summary>
    public string? ShopRarity { get => _shopRarity; set => Set(ref _shopRarity, value); }
    private string? _shopRarity;

    /// <summary>What the game ships, and whether the mod changes it.</summary>
    public string ShopRarityGame { get => _shopRarityGame; set => Set(ref _shopRarityGame, value); }
    private string _shopRarityGame = "";

    public bool ShopRarityMine
    {
        get => _shopRarityMine;
        set { if (Set(ref _shopRarityMine, value)) Raise(nameof(ShopRarityNotMine)); }
    }
    private bool _shopRarityMine;
    public bool ShopRarityNotMine => !_shopRarityMine;

    /// <summary>The two prices, as number rows: what it costs and what it sells for.</summary>
    public ObservableCollection<EffectRow> ShopRows { get; } = new();

    /// <summary>Why the card has nothing to offer (no shop item paired), or empty.</summary>
    public string ShopProblem { get => _shopProblem; set { if (Set(ref _shopProblem, value)) Raise(nameof(HasShopProblem)); } }
    private string _shopProblem = "";
    public bool HasShopProblem => _shopProblem.Length > 0;
    public bool HasShop => _shopProblem.Length == 0;

    /// <summary>What the shop override carries that this card has no control for. Named, not counted.</summary>
    public string ShopBeyond { get => _shopBeyond; set { if (Set(ref _shopBeyond, value)) Raise(nameof(HasShopBeyond)); } }
    private string _shopBeyond = "";
    public bool HasShopBeyond => _shopBeyond.Length > 0;

    /// <summary>A refusal or a note about the last thing typed into the shop card, in the card.</summary>
    public string ShopFeedback { get => _shopFeedback; private set => Set(ref _shopFeedback, value); }
    private string _shopFeedback = "";
    public bool ShopFeedbackBad { get => _shopFeedbackBad; private set { if (Set(ref _shopFeedbackBad, value)) { Raise(nameof(HasShopRefusal)); Raise(nameof(HasShopNote)); } } }
    private bool _shopFeedbackBad;
    public bool HasShopRefusal => _shopFeedback.Length > 0 && _shopFeedbackBad;
    public bool HasShopNote => _shopFeedback.Length > 0 && !_shopFeedbackBad;

    public void ShopSay(string text, bool bad = false)
    {
        ShopFeedback = text;
        ShopFeedbackBad = bad;
        Raise(nameof(HasShopRefusal));
        Raise(nameof(HasShopNote));
    }

    /// <summary>"Put this one back" is offered only while the mod changes something about it.</summary>
    public bool Altered { get => _altered; set => Set(ref _altered, value); }
    private bool _altered;


    // ------------------------------------------------------------------ how a character plays

    public EditSection SecPlays { get; } = new("Plays", "How they play");
    public EditSection SecRemake { get; } = new("Remake", "Make them again");
    public EditSection SecOffer { get; } = new("Offer", "Offer them again");
    public EditSection SecChanges { get; } = new("Changes", "What it changes");

    /// <summary>
    /// EVERY BOX HERE IS A MEASURED RECIPE, and several are still untested in game.
    ///
    /// They are separate properties rather than a list because they are not alike: each appears
    /// only when the game data says it applies, two of them nest, one carries a number, and the
    /// blurb under each is the measurement that earned it. A generic row of toggles would have to
    /// throw all of that away.
    ///
    /// BIND `Click`, NOT the checked state alone. A fill that sets one of these must not look
    /// like somebody ticking it — see BeginFill for what that cost on the mission schedule.
    /// </summary>
    public string RigName
    {
        get => _rigName;
        set { if (Set(ref _rigName, value)) RaiseBoxLabels(); }
    }
    private string _rigName = "";

    /// <summary>
    /// THE WHOLE LABEL, not a sentence spliced around a bound Run.
    ///
    /// Three of these boxes name the rig mid-sentence, and splicing dropped the possessive —
    /// "List only the B1 own gun in it". A Run carries no grammar, so the grammar has to be
    /// built where the name is known.
    /// </summary>
    public string RifleBox =>
        $"Also ship a weapon for the Armoury that carries the {Owns} animations";

    public string OwnGunBox => $"List only the {Owns} own gun in it";

    public string TacticalBox =>
        $"Keep the {Owns} own tactical spec (action points for a pool recruit)";

    public string RigAnimBox => $"Give them the {Owns} own rifle animation table (experimental)";

    public string PawnBox => _pawnFrom.Length > 0
        ? $"Spawn on a pawn of the mod's own, made from {_pawnFrom}"
        : "Spawn on a pawn of the mod's own";

    /// <summary>"BX's" — and just "the rig's" when nothing has been read yet.</summary>
    private string Owns => _rigName.Length == 0 ? "rig's" : _rigName + "'s";

    private void RaiseBoxLabels()
    {
        Raise(nameof(RifleBox));
        Raise(nameof(OwnGunBox));
        Raise(nameof(TacticalBox));
        Raise(nameof(RigAnimBox));
        Raise(nameof(PawnBox));
    }

    /// <summary>"A BX rig on a hero pawn stands and moves like a human." Empty for a hero rig.</summary>
    public string RigWarn
    {
        get => _rigWarn;
        set { if (Set(ref _rigWarn, value)) Raise(nameof(HasRigWarn)); }
    }
    private string _rigWarn = "";
    public bool HasRigWarn => _rigWarn.Length > 0;

    public bool ShowEnemyKit { get => _showEnemyKit; set => Set(ref _showEnemyKit, value); }
    private bool _showEnemyKit;
    public bool PlayEnemyKit { get => _playEnemyKit; set => Set(ref _playEnemyKit, value); }
    private bool _playEnemyKit;

    public bool ShowEnemyPawn { get => _showEnemyPawn; set => Set(ref _showEnemyPawn, value); }
    private bool _showEnemyPawn;
    public bool PlayEnemyPawn { get => _playEnemyPawn; set => Set(ref _playEnemyPawn, value); }
    private bool _playEnemyPawn;

    /// <summary>The pawn the copy is made from, named in the box so it is not "a pawn".</summary>
    public string PawnFrom
    {
        get => _pawnFrom;
        set { if (Set(ref _pawnFrom, value)) Raise(nameof(PawnBox)); }
    }
    private string _pawnFrom = "";

    public bool ShowStory { get => _showStory; set => Set(ref _showStory, value); }
    private bool _showStory;
    public bool PlayStory
    {
        get => _playStory;
        set { if (Set(ref _playStory, value)) Raise(nameof(ShowStoryCount)); }
    }
    private bool _playStory;

    /// <summary>A recipe runs ONCE, so each one that can join is a recipe of its own. 1 to 20.</summary>
    public string StoryCount { get => _storyCount; set => Set(ref _storyCount, value); }
    private string _storyCount = "1";
    public bool ShowStoryCount => _showStory && _playStory;

    public bool ShowRifle { get => _showRifle; set => Set(ref _showRifle, value); }
    private bool _showRifle;
    public bool PlayRifle
    {
        get => _playRifle;
        set { if (Set(ref _playRifle, value)) Raise(nameof(ShowOwnGun)); }
    }
    private bool _playRifle;

    /// <summary>Only meaningful once there IS a weapon to narrow. Nested under it for that reason.</summary>
    public bool ShowOwnGun => _showRifle && _playRifle;
    public bool PlayOwnGun { get => _playOwnGun; set => Set(ref _playOwnGun, value); }
    private bool _playOwnGun;

    public bool ShowTactical { get => _showTactical; set => Set(ref _showTactical, value); }
    private bool _showTactical;
    public bool PlayTactical { get => _playTactical; set => Set(ref _playTactical, value); }
    private bool _playTactical;

    public bool ShowRigAnim { get => _showRigAnim; set => Set(ref _showRigAnim, value); }
    private bool _showRigAnim;
    public bool PlayRigAnim { get => _playRigAnim; set => Set(ref _playRigAnim, value); }
    private bool _playRigAnim;

    public bool ShowSpareSpec { get => _showSpareSpec; set => Set(ref _showSpareSpec, value); }
    private bool _showSpareSpec;
    public bool PlaySpareSpec { get => _playSpareSpec; set => Set(ref _playSpareSpec, value); }
    private bool _playSpareSpec;

    /// <summary>The spec that carries the basics, and the slot the hiring roll never touches.</summary>
    public string SpareSays { get => _spareSays; set => Set(ref _spareSays, value); }
    private string _spareSays = "";

    /// <summary>Nothing about this character's rig or kit gives it anything to offer.</summary>
    public bool NoPlays => !_showEnemyKit && !_showEnemyPawn && !_showStory && !_showRifle &&
                           !_showTactical && !_showRigAnim && !_showSpareSpec;

    /// <summary>Raised after a fill, because every one of the seven feeds it.</summary>
    public void PlaysSettled()
    {
        Raise(nameof(NoPlays));
        Raise(nameof(ShowOwnGun));
        Raise(nameof(ShowStoryCount));
    }

    // ------------------------------------------------------------------ making them again

    /// <summary>
    /// "Built by an older version of this tool: the pawn they spawn on and the weapon."
    ///
    /// Empty when nothing is behind, and the whole card is hidden then — a button offering to
    /// rebuild what is already current is a button whose only possible outcome is doubt.
    /// </summary>
    public string RemakeSays
    {
        get => _remakeSays;
        set { if (Set(ref _remakeSays, value)) Raise(nameof(HasRemake)); }
    }
    private string _remakeSays = "";
    public bool HasRemake => _remakeSays.Length > 0;

    /// <summary>
    /// NOT WHILE THE GAME IS STILL BEING READ.
    ///
    /// Remaking reads the game for every recipe, and a package read before the mount finishes
    /// comes back EMPTY rather than failing — so an early press built a blaster whose stance set
    /// "could not be read" and reported success, which is precisely the silent partial this
    /// button exists to end.
    /// </summary>
    public bool RemakeReady { get => _remakeReady; set => Set(ref _remakeReady, value); }
    private bool _remakeReady;

    /// <summary>
    /// What offering them again would rename — the plan, before the button is pressed.
    ///
    /// Shown rather than summarised because this renames assets, and an author who cannot see
    /// which names move has no way to tell a rename that worked from one that half-worked. Set
    /// again after the press, so the same box reports what it did.
    /// </summary>
    public string OfferSays { get => _offerSays; set => Set(ref _offerSays, value); }
    private string _offerSays = "";


    // ------------------------------------------------------------------ what an override changes

    /// <summary>
    /// The GAME package this override replaces, by full package name.
    ///
    /// A FIELD RATHER THAN A LABEL because an override can be repointed, and until now the only
    /// way to change which asset one changes was to edit the project file by hand. The build
    /// refuses a target the game has no file at, so a typo cannot ship as a package the game
    /// never asks for -- but it can waste a build, which is why <see cref="ChangesSays"/> says so
    /// before the build does.
    /// </summary>
    public string EditTarget
    {
        get => _editTarget;
        set { if (Set(ref _editTarget, value)) TargetEdited?.Invoke(value); }
    }
    private string _editTarget = "";

    /// <summary>Raised when the box is committed, so the view model can write and re-check it.</summary>
    public Action<string>? TargetEdited;

    /// <summary>"CPD_H_Outfit_Clo000_HELM -- 2 other mods change it too", or why it is not usable.</summary>
    public string ChangesSays { get => _changesSays; set => Set(ref _changesSays, value); }
    private string _changesSays = "";

    /// <summary>The clash sentence, empty when nobody else claims it.</summary>
    public string ChangesClash
    {
        get => _changesClash;
        set { if (Set(ref _changesClash, value)) Raise(nameof(HasChangesClash)); }
    }
    private string _changesClash = "";
    public bool HasChangesClash => _changesClash.Length > 0;


    // ------------------------------------------------------------------ a character of the mod's own

    public EditSection SecPerson { get; } = new("Person", "Who they are");
    public EditSection SecLook { get; } = new("Look", "What they look like");

    /// <summary>
    /// THE NAME AND THE STORY LIVE ONE COPY OVER, on the character's NAME PART, and they are
    /// edited here because this is where a person looks for them — not on a second tab for an
    /// asset whose existence is an implementation detail of how the game stores a name.
    /// </summary>
    public string CharFirst
    {
        get => _charFirst;
        set { if (Set(ref _charFirst, value)) CharTally(); }
    }
    private string _charFirst = "";

    public string CharLast
    {
        get => _charLast;
        set { if (Set(ref _charLast, value)) CharTally(); }
    }
    private string _charLast = "";

    public string CharStory
    {
        get => _charStory;
        set { if (Set(ref _charStory, value)) { CharTally(); Raise(nameof(NoStoryYet)); } }
    }
    private string _charStory = "";

    /// <summary>The databank prints an empty biography until somebody writes one.</summary>
    public bool NoStoryYet => _charStory.Trim().Length == 0;

    private string _wasFirst = "", _wasLast = "", _wasStory = "";

    private void CharTally()
        => SecPerson.Changed = !string.Equals(_charFirst, _wasFirst, StringComparison.Ordinal) ||
                               !string.Equals(_charLast, _wasLast, StringComparison.Ordinal) ||
                               !string.Equals(_charStory, _wasStory, StringComparison.Ordinal);

    /// <summary>After a save: what is on screen is now what is stored, so the dot goes out.</summary>
    public void CharKept()
    {
        _wasFirst = _charFirst;
        _wasLast = _charLast;
        _wasStory = _charStory;
        SecPerson.Changed = false;
    }

    /// <summary>
    /// Put the three fields up WITHOUT arming the unsaved dot.
    ///
    /// Assigning the properties would call CharTally against the previous character's values and
    /// light the dot on a panel nobody has touched — the same shape as every other fill in here.
    /// </summary>
    public void CharSay(string first, string last, string story)
    {
        _charFirst = first;
        _charLast = last;
        _charStory = story;
        Raise(nameof(CharFirst));
        Raise(nameof(CharLast));
        Raise(nameof(CharStory));
        Raise(nameof(NoStoryYet));
        CharKept();
    }

    /// <summary>"No name part of their own is in this mod", when there is not one.</summary>
    public string CharNameWarn
    {
        get => _charNameWarn;
        set { if (Set(ref _charNameWarn, value)) Raise(nameof(HasCharNameWarn)); }
    }
    private string _charNameWarn = "";
    public bool HasCharNameWarn => _charNameWarn.Length > 0;

    /// <summary>Which hiring-pool row offers them — or that none does yet.</summary>
    public string CharOffered { get => _charOffered; set => Set(ref _charOffered, value); }
    private string _charOffered = "";

    public bool CharOfferedWarn { get => _charOfferedWarn; set => Set(ref _charOfferedWarn, value); }
    private bool _charOfferedWarn;

    /// <summary>Who they were started from, said once at the top.</summary>
    public string CharFrom { get => _charFrom; set => Set(ref _charFrom, value); }
    private string _charFrom = "";

    // ------------------------------------------------------------------ what they look like

    /// <summary>
    /// One slot of the character's definition — or a heading over the group it starts.
    ///
    /// A HEADING IS A ROW because the table is one flat list: Appearance, Outfit and Info each
    /// want a line of their own above them, and two collections that have to stay interleaved in
    /// the right order is the kind of thing that goes wrong once and stays wrong.
    /// </summary>
    public sealed class LookRow : Obs
    {
        /// <summary>The full slot tag, which is the key everything is written under.</summary>
        public string Tag { get; init; } = "";

        /// <summary>"Head / Face / Mesh" — the tag with its plumbing spelled out of it.</summary>
        public string Shown { get; init; } = "";

        /// <summary>The tag, and what the game's own developers wrote beside it.</summary>
        public string Tip { get; init; } = "";

        public bool IsHeader { get; init; }
        public bool IsSlot => !IsHeader;

        /// <summary>One of the umbrellas that decide species and rig. Hidden unless asked for.</summary>
        public bool Plumbing { get; init; }

        private string _value = "";
        public string Value
        {
            get => _value;
            set { if (Set(ref _value, value)) Raise(nameof(Bare)); }
        }

        /// <summary>Nothing in it. Drawn as "(empty)" rather than as a blank cell.</summary>
        public bool Bare => _value.Length == 0;

        /// <summary>The whole path, for the tooltip.</summary>
        public string Path { get => _path; set => Set(ref _path, value); }
        private string _path = "";

        /// <summary>The author put this here; the template had something else.</summary>
        public bool Mine { get => _mine; set => Set(ref _mine, value); }
        private bool _mine;

        /// <summary>A mesh or a pattern, which are the only two a person may leave bare.</summary>
        public bool CanEmpty { get => _canEmpty; set => Set(ref _canEmpty, value); }
        private bool _canEmpty;

        /// <summary>
        /// THE GAME'S PARTS HAVE BEEN READ, so Choose has something to offer.
        ///
        /// On the ROW rather than reached for on the card through a parent binding, because a
        /// cast up the visual tree is the kind of expression that fails silently at runtime and
        /// leaves the button permanently one way or the other.
        /// </summary>
        public bool Ready { get; init; }
    }

    public ObservableCollection<LookRow> LookRows { get; } = new();
    public bool HasLookRows => LookRows.Count > 0;

    /// <summary>
    /// Show the umbrellas too.
    ///
    /// OFF BY DEFAULT AND THAT IS NOT TIDINESS: changing Species without changing Head, Face,
    /// FacialDetails and BodyArt to match is a character that does not load, and filling a
    /// Tactical slot is what broke somebody else's mod.
    /// </summary>
    public bool ShowEverySlot { get => _showEverySlot; set => Set(ref _showEverySlot, value); }
    private bool _showEverySlot;

    /// <summary>What the last slot change did, in the author's own words back to them.</summary>
    public string SlotNote
    {
        get => _slotNote;
        set { if (Set(ref _slotNote, value)) Raise(nameof(HasSlotNote)); }
    }
    private string _slotNote = "";
    public bool HasSlotNote => _slotNote.Length > 0;

    /// <summary>A one-piece body: the game's own databank shows them as NOT EDITABLE.</summary>
    public bool Bespoke { get => _bespoke; set => Set(ref _bespoke, value); }
    private bool _bespoke;

    /// <summary>Why the template's slots could not be read at all.</summary>
    public string LookProblem
    {
        get => _lookProblem;
        set { if (Set(ref _lookProblem, value)) Raise(nameof(HasLookProblem)); }
    }
    private string _lookProblem = "";
    public bool HasLookProblem => _lookProblem.Length > 0;

    /// <summary>
    /// The game's parts are still being read, so there is nothing to choose from yet.
    ///
    /// A READ THAT ARRIVES EARLY COMES BACK EMPTY RATHER THAN FAILING, which is the worst of
    /// both: the Choose button works, the list holds one thing, and nothing says the catalogue
    /// was still loading. Seen on the very first drive of this card — 3,518 parts finished
    /// indexing nine lines of log AFTER the lookup that found one.
    /// </summary>
    public bool PartsWaiting { get => _partsWaiting; set => Set(ref _partsWaiting, value); }
    private bool _partsWaiting;

    /// <summary>Put the whole table up in one go, under the fill latch.</summary>
    public void LookSays(IEnumerable<LookRow> rows)
    {
        LookRows.Clear();
        foreach (var r in rows) LookRows.Add(r);
        Raise(nameof(HasLookRows));
    }


    /// <summary>One thing a specialization grants outright — an ability, or a gear kit.</summary>
    public sealed class GrantRow : Obs
    {
        public GrantRow(string path, bool isGear)
        {
            _path = path;
            IsGear = isGear;
        }

        public bool IsGear { get; }

        private string _path;
        public string Path
        {
            get => _path;
            set { if (Set(ref _path, value)) Raise(nameof(Leafed)); }
        }

        public string Leafed
        {
            get
            {
                var cut = _path.LastIndexOfAny(['/', '.']);
                return cut >= 0 && cut < _path.Length - 1 ? _path[(cut + 1)..] : _path;
            }
        }
    }

    public ObservableCollection<GrantRow> Grants { get; } = new();

    public bool HasGrants => Grants.Count > 0;

    public void SetGrants(IEnumerable<GrantRow> rows)
    {
        Grants.Clear();
        foreach (var r in rows) Grants.Add(r);
        Raise(nameof(HasGrants));
        SecGrants.Count = Grants.Count > 0 ? Grants.Count.ToString() : "";
    }

    /// <summary>
    /// ONE RUNG of a focus tree: what it grants, and what reaching it takes.
    ///
    /// Level is when a recruit may take it; Focus is its place in the line; Cost is what it
    /// charges. A number the game ships as 0 cannot be RAISED — it takes no space in the file, so
    /// there is nothing to write over — and the build says so rather than failing quietly.
    /// </summary>
    public sealed class RungRow : Obs
    {
        public RungRow(int line, int index, ModSpec.Rung rung)
        {
            Line = line;
            Index = index;
            Rung = rung;
            _ability = rung.Ability;
            _level = rung.MinLevel.ToString();
            _focus = rung.FocusLevel.ToString();
            _cost = rung.FocusCost.ToString();
        }

        public int Line { get; }
        public int Index { get; }

        /// <summary>The project's own rung, which the writes go through the view model to touch.</summary>
        public ModSpec.Rung Rung { get; }

        public string Where => $"Line {Line + 1} · rung {Index + 1}";

        private string _ability;
        public string Ability
        {
            get => _ability;
            set { if (Set(ref _ability, value)) Raise(nameof(AbilityLeaf)); }
        }

        public string AbilityLeaf
        {
            get
            {
                var cut = _ability.LastIndexOfAny(['/', '.']);
                return cut >= 0 && cut < _ability.Length - 1 ? _ability[(cut + 1)..] : _ability;
            }
        }

        private string _level, _focus, _cost;
        public string Level { get => _level; set => Set(ref _level, value); }
        public string Focus { get => _focus; set => Set(ref _focus, value); }
        public string Cost { get => _cost; set => Set(ref _cost, value); }
    }

    public ObservableCollection<RungRow> Rungs { get; } = new();

    public bool HasRungs => Rungs.Count > 0;

    public void SetRungs(IEnumerable<RungRow> rows)
    {
        Rungs.Clear();
        foreach (var r in rows) Rungs.Add(r);
        Raise(nameof(HasRungs));
        SecRungs.Present = Rungs.Count > 0;
        SecRungs.Count = Rungs.Count > 0 ? Rungs.Count.ToString() : "";
    }

    // ------------------------------------------------------------------ an ability's tiers

    /// <summary>
    /// ONE NUMBER OF ONE TIER of an ability — on the ability, on the effect it applies, or on
    /// its cooldown, wherever that number actually lives.
    ///
    /// <see cref="Shared"/> is the one that matters: a number shared by several tiers cannot be
    /// changed for one of them without either moving all of them or giving this tier its own
    /// copy of the asset. Typing into it silently would do the first while an author expected
    /// the second.
    /// </summary>
    public sealed class ProgRow : Obs
    {
        public ProgRow(int tier, string key, string label, string role, double value,
                       bool edited, int shared)
        {
            Tier = tier;
            Key = key;
            Label = label;
            Role = role;
            _text = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            Edited = edited;
            Shared = shared;
        }

        public int Tier { get; }
        public string Key { get; }
        public string Label { get; }
        public string Role { get; }

        /// <summary>This mod has moved it.</summary>
        public bool Edited { get; }

        /// <summary>How many OTHER tiers name the same number. Zero means it is this tier's own.</summary>
        public int Shared { get; }

        public bool IsShared => Shared > 0;

        public string Where => $"Tier {Tier}";

        public string Note => Shared > 0
            ? $"shared with {Shared} other tier(s)"
            : Role.Length > 0 ? Role : "";

        private string _text;
        public string Text { get => _text; set => Set(ref _text, value); }
    }

    /// <summary>
    /// What the tier table is, or why it has nothing to show.
    ///
    /// ON THE BASE, like every other thing the shared markup binds. The card lives in the one
    /// SheetTab template, and a property on the derived tab is one the compiled binding cannot
    /// see — AVLN2000, which is the third time this rule has been learnt here.
    /// </summary>
    public string ProgressionNote { get => _progNote; set => Set(ref _progNote, value); }
    private string _progNote = "";

    public ObservableCollection<ProgRow> Progression { get; } = new();

    public bool HasProgression => Progression.Count > 0;

    public void SetProgression(IEnumerable<ProgRow> rows)
    {
        Progression.Clear();
        foreach (var r in rows) Progression.Add(r);
        Raise(nameof(HasProgression));
        SecProg.Present = Progression.Count > 0;
        SecProg.Count = Progression.Count > 0 ? Progression.Count.ToString() : "";
        SecProg.Changed = Progression.Any(r => r.Edited);
    }

    /// <summary>Gate tags to take off, and to put on. Free text, one per line.</summary>
    private string _editRemoveTags = "", _editAddTags = "";

    public string EditRemoveTags
    {
        get => _editRemoveTags;
        set { if (Set(ref _editRemoveTags, value)) Retally(); }
    }

    public string EditAddTags
    {
        get => _editAddTags;
        set { if (Set(ref _editAddTags, value)) Retally(); }
    }

    // ------------------------------------------------------------------ the editor's sections

    /// <summary>
    /// ONE SECTION OF THE EDITOR, as the rail on the left lists it.
    ///
    /// The rail is what lets this panel keep growing. The first version of the editor was a
    /// single column of small-caps headings, and every section still owed — the textures, the
    /// numbers, the deeper changes — would have been another screenful of scroll under the last
    /// one. Here each is a line in a list and a card that folds.
    ///
    /// <see cref="Present"/> is not the same as folded: a part with no swatches contributes no
    /// colours section AT ALL, rather than an empty heading, which is the rule the read sheet
    /// already follows.
    /// </summary>
    public sealed class EditSection : Obs
    {
        public EditSection(string key, string title)
        {
            Key = key;
            Title = title;
        }

        /// <summary>Matches the Tag on the card, which is how the rail scrolls to it.</summary>
        public string Key { get; }

        public string Title { get; }

        private string _count = "";
        public string Count
        {
            get => _count;
            set { if (Set(ref _count, value)) Raise(nameof(HasCount)); }
        }

        public bool HasCount => _count.Length > 0;

        /// <summary>The accent dot: this section holds a change that is not saved.</summary>
        private bool _changed;
        public bool Changed { get => _changed; set => Set(ref _changed, value); }

        private bool _selected;
        public bool Selected { get => _selected; set => Set(ref _selected, value); }

        private bool _open = true;
        public bool Open
        {
            get => _open;
            set { if (Set(ref _open, value)) Raise(nameof(Chevron)); }
        }

        public Avalonia.Media.Geometry Chevron => Chevrons.For(_open);

        private bool _present = true;
        public bool Present { get => _present; set => Set(ref _present, value); }
    }

    public EditSection SecIdentity { get; } = new("Identity", "Name");
    public EditSection SecMeshes { get; } = new("Meshes", "Meshes");

    /// <summary>
    /// What the mesh rows ARE, which is not the same thing for the two kinds of copy.
    ///
    /// A part's rows are one garment's body types; a weapon's are the pieces it is assembled
    /// from. "every body type, at once" over a barrel and a scope is a caption describing
    /// something else entirely. And a brow's or a beard's rows are HEADS, not body types: 29 of
    /// them on Thick 1, each cut to its head.
    /// </summary>
    public virtual string MeshesAre => MeshesPerHead ? "every head, at once" : "every body type, at once";

    /// <summary>Whether the rows are one per HEAD (eyebrows, facial hair) rather than per body type.</summary>
    public bool MeshesPerHead
    {
        get => _meshesPerHead;
        set { if (Set(ref _meshesPerHead, value)) Raise(nameof(MeshesAre)); }
    }
    private bool _meshesPerHead;
    public EditSection SecIcon { get; } = new("Icon", "Icon");
    public EditSection SecTextures { get; } = new("Textures", "Textures");
    public EditSection SecColours { get; } = new("Colours", "Tints");
    public EditSection SecOffered { get; } = new("Offered", "Tags");
    public EditSection SecSlot { get; } = new("Slot", "Slot lock");
    public EditSection SecNumbers { get; } = new("Numbers", "Numbers");
    public EditSection SecDeeper { get; } = new("Deeper", "Advanced");
    public EditSection SecWhere { get; } = new("Where", "Slot");
    public EditSection SecDoes { get; } = new("Does", "Abilities");
    public EditSection SecFiles { get; } = new("Files", "Asset name");

    // A WEAPON'S OWN THREE. Absent for everything else, which is the rule every card here
    // follows — a part has no bolts, no Armoury shelf and no attribute table of its own.
    public EditSection SecBolts { get; } = new("Bolts", "Bolts");
    public EditSection SecShelf { get; } = new("Shelf", "Shelf");
    public EditSection SecStats { get; } = new("Stats", "Numbers");
    public EditSection SecFits { get; } = new("Fits", "What fits it");

    // A SPECIALIZATION'S OWN. It carries no geometry at all, so its cards are about who may take
    // it, what it hands out, and the ladder they climb.
    public EditSection SecCrest { get; } = new("Crest", "Crest");
    public EditSection SecWho { get; } = new("Who", "Who can take it");
    public EditSection SecGrants { get; } = new("Grants", "What it grants");
    public EditSection SecRungs { get; } = new("Rungs", "Its rungs");

    /// <summary>An ability's: every tier and every number behind it.</summary>
    public EditSection SecProg { get; } = new("Progression", "Progression");
    public EditSection SecAttach { get; } = new("Attach", "Attachments");

    // ------------------------------------------------------------------ the tags it carries

    /// <summary>
    /// ONE GATE TAG THE PART ACTUALLY CARRIES, and whether it is kept.
    ///
    /// A LIST, NOT A TEXT BOX — the old studio's own words for why: "A gate tag is 50-odd
    /// characters of dotted namespace, and getting one character wrong fails the build". Typing
    /// one from memory is not a thing anybody can do.
    ///
    /// This is how a hero's gear becomes everybody's: a copy inherits the name tag that ties it
    /// to them, and dropping that tag is the whole difference.
    /// </summary>
    public sealed class TagRow : Obs
    {
        public TagRow(string tag, bool kept)
        {
            Tag = tag;
            _kept = kept;
        }

        public string Tag { get; }

        /// <summary>The last two segments — the rest is namespace nobody reads.</summary>
        public string Shown => Tag.Split('.') is { Length: > 2 } parts
            ? string.Join(" / ", parts[^2..]) : Tag;

        private bool _kept;
        public bool Kept { get => _kept; set => Set(ref _kept, value); }
    }

    public ObservableCollection<TagRow> Tags { get; } = new();

    public bool HasTags => Tags.Count > 0;

    public void SetTags(IEnumerable<TagRow> rows)
    {
        Tags.Clear();
        foreach (var r in rows) Tags.Add(r);
        Raise(nameof(HasTags));
        SecOffered.Count = Tags.Count > 0 ? Tags.Count.ToString() : "";
    }

    // ------------------------------------------------------------------ what a copy does

    /// <summary>One ability the copy swaps for another.</summary>
    public sealed class AbilityRow : Obs
    {
        public AbilityRow(string from, string to) { From = from; _to = to; }

        public string From { get; }
        public string FromShown => Leaf(From);

        private string _to;
        public string To { get => _to; set => Set(ref _to, value); }
    }

    public ObservableCollection<AbilityRow> Abilities { get; } = new();

    public bool HasAbilities => Abilities.Count > 0;

    public void SetAbilities(IEnumerable<AbilityRow> rows)
    {
        Abilities.Clear();
        foreach (var r in rows) Abilities.Add(r);
        Raise(nameof(HasAbilities));
        SecDoes.Count = Abilities.Count > 0 ? Abilities.Count.ToString() : "";
    }

    /// <summary>The animation class a copy uses, where it names one of its own.</summary>
    private string _editAnimClass = "";
    public string EditAnimClass { get => _editAnimClass; set => Set(ref _editAnimClass, value); }

    /// <summary>
    /// The asset name this copy cooks to.
    ///
    /// Must be unique across every mod installed at once: two mods sharing one id cook to the
    /// same package and one wins. And a trailing _N is its own trap — UE splits it into an FName
    /// number and the part does not load.
    /// </summary>
    private string _editId = "";
    public string EditId { get => _editId; set => Set(ref _editId, value); }

    // ------------------------------------------------------------------ where a copy goes

    /// <summary>The slot this copy sits in. Only a COPY can change it — an override cannot.</summary>
    private string _editSlot = "";
    public string EditSlot
    {
        get => _editSlot;
        set
        {
            if (Set(ref _editSlot, value))
            {
                Raise(nameof(SlotLimit)); Raise(nameof(HasSlotLimit)); Raise(nameof(ShowsHidesHead));
            }
        }
    }

    public IReadOnlyList<string> Slots => ModProject.Slots;

    /// <summary>What an author should know about the chosen slot before building, or "".</summary>
    public string SlotLimit => ModProject.SlotLimit(_editSlot);
    public bool HasSlotLimit => SlotLimit.Length > 0;

    /// <summary>
    /// Whether the head is hidden while this is worn.
    ///
    /// A helmet needs it. Without it the character's face and hair render through the visor,
    /// which is the old studio's own tooltip and the kind of thing nobody diagnoses from a
    /// screenshot of their own character.
    /// </summary>
    private bool _editHidesHead;
    public bool EditHidesHead { get => _editHidesHead; set => Set(ref _editHidesHead, value); }

    /// <summary>
    /// Whether the box above is drawn at all: only in a slot where it can mean something, and
    /// the build agrees (<see cref="ModProject.SlotCanHideHead"/>). It was drawn for every slot,
    /// so a Devaronian head arrived with it ticked -- and a box that is hidden must not go on
    /// deciding what the build does, which is why the editor also stops writing the tick back
    /// for a slot that hides it.
    /// </summary>
    public bool ShowsHidesHead => ModProject.SlotCanHideHead(_editSlot);

    /// <summary>Who may wear it. Empty means everyone, which is almost always right.</summary>
    private string _editForCharacter = "";
    public string EditForCharacter
    {
        get => _editForCharacter;
        set => Set(ref _editForCharacter, value);
    }

    private IReadOnlyList<string> _characters = [];
    public IReadOnlyList<string> Characters
    {
        get => _characters;
        set => Set(ref _characters, value);
    }

    /// <summary>
    /// ONE SLOT THE TEMPLATE TAKES OVER, and whether this copy keeps doing it.
    ///
    /// The clone legs replace the wearer's boots — and a duplicate of them inherits that, so an
    /// author who wanted the legs got a part that silently deletes whatever boots the player
    /// picked. There was no way to say otherwise; this is that way. The rows are the TEMPLATE's,
    /// so there is nothing to untick that the part does not already do.
    /// </summary>
    public sealed class TakeoverRow : Obs
    {
        public TakeoverRow(string tag, string kind, bool kept)
        {
            Tag = tag;
            Kind = kind;
            _kept = kept;
        }

        /// <summary>The full slot tag, which is what the project stores.</summary>
        public string Tag { get; }

        /// <summary>The last two segments, which is what a person reads.</summary>
        public string Slot => Tag.Split('.') is { Length: > 1 } parts
            ? string.Join(" / ", parts[^2..]) : Tag;

        public string Kind { get; }

        private bool _kept;

        /// <summary>Ticked means "as the template has it", which is what no entry means.</summary>
        public bool Kept { get => _kept; set => Set(ref _kept, value); }
    }

    public ObservableCollection<TakeoverRow> Takeovers { get; } = new();

    public bool HasTakeovers => Takeovers.Count > 0;

    public void SetTakeovers(IEnumerable<TakeoverRow> rows)
    {
        Takeovers.Clear();
        foreach (var r in rows) Takeovers.Add(r);
        Raise(nameof(HasTakeovers));
    }

    /// <summary>
    /// ONE DEEPER CHANGE THIS PART ALREADY CARRIES — a re-point, or a name rewritten everywhere.
    ///
    /// Listed rather than only offered, because these are the edits that are invisible in every
    /// other section: a re-point made by Pick lives here too, and a project made by somebody else
    /// may carry renames nothing else in this panel would ever show.
    /// </summary>
    public sealed class DeepRow : Obs
    {
        public DeepRow(string kind, string from, string to, string detail)
        {
            Kind = kind;
            From = from;
            To = to;
            Detail = detail;
        }

        public string Kind { get; }
        public string From { get; }
        public string To { get; }
        public string Detail { get; }

        public string FromLeaf => Leaf(From);
        public string ToLeaf => Leaf(To);
    }

    public ObservableCollection<DeepRow> DeepRows { get; } = new();

    public bool HasDeepRows => DeepRows.Count > 0;

    public void SetDeepRows(IEnumerable<DeepRow> rows)
    {
        DeepRows.Clear();
        foreach (var r in rows) DeepRows.Add(r);
        Raise(nameof(HasDeepRows));
        SecDeeper.Count = DeepRows.Count > 0 ? DeepRows.Count.ToString() : "";
        SecDeeper.Changed = DeepRows.Count > 0;
    }

    /// <summary>Two boxes at the foot of the deeper card: rewrite a name everywhere.</summary>
    private string _renameFrom = "", _renameTo = "";
    public string RenameFrom { get => _renameFrom; set => Set(ref _renameFrom, value); }
    public string RenameTo { get => _renameTo; set => Set(ref _renameTo, value); }

    /// <summary>The rail, in the order the cards appear.</summary>
    public IReadOnlyList<EditSection> EditSections => _sections;
    private readonly List<EditSection> _sections = new();

    protected SheetTab()
    {
        ShopRarities.CollectionChanged += (_, _) => Raise(nameof(HasRarities));
        // THE ORDER IS THE ORDER OF THE CARDS, and "where does it go" is the first question a
        // copy asks — a part in the wrong slot never appears at all.
        _sections.AddRange([SecIdentity, SecMade, SecPicture, SecShips, SecRecruit,
                            SecChanges, SecPerson, SecLook, SecPlays, SecRemake, SecOffer, SecWhere,
                            SecBriefing, SecPlace, SecFight, SecPays, SecWhen,
                            SecMeshes, SecFit, SecIcon, SecTextures, SecSell, SecInside,
                            SecTint, SecColours,
                            SecEnemy,
                            SecOffered, SecSlot, SecDoes, SecBolts, SecShelf, SecStats,
                            SecFits, SecAttach, SecCrest, SecWho, SecGrants, SecRungs,
                            SecProg, SecEffects, SecShop, SecPoints, SecGates,
                            SecNumbers, SecDeeper, SecFiles]);
        SecIdentity.Selected = true;
        SecSlot.Present = false;       // only the 49 parts that lock their own slot
        SecBolts.Present = false;      // a weapon's, and only once its actor has been read
        SecShelf.Present = false;      // a weapon's
        SecStats.Present = false;      // a weapon's
        SecFits.Present = false;       // a weapon's
        SecAttach.Present = false;     // a weapon's
        SecCrest.Present = false;      // a specialization's
        SecWho.Present = false;        // a specialization's
        SecGrants.Present = false;     // a specialization's
        SecRungs.Present = false;      // a specialization's
        SecProg.Present = false;       // an ability's
        SecTextures.Present = false;   // until the part's materials have been read
        SecNumbers.Present = false;    // until its own properties have been read
        SecWhere.Present = false;      // only a COPY can be placed; an override cannot move
        SecDoes.Present = false;       // a copy swaps abilities; an override has other routes
        SecFiles.Present = false;      // only a copy cooks to an asset name of its own
        SecMade.Present = false;       // the MOD's own, and nothing else has an author
        SecPicture.Present = false;    // the mod's own card picture
        SecShips.Present = false;      // the mod's own name and asset folder
        SecRecruit.Present = false;    // only a mod with characters on an enemy pawn
        SecInside.Present = false;     // a duplicated asset's, read out of the thing it copies
        SecTint.Present = false;       // a COPY's: both fields live on ModPart
        SecFit.Present = false;        // only once there is an import to measure
        SecSell.Present = false;       // a duplicated UTILITY ITEM's, and nothing else's
        SecBriefing.Present = false;   // a mission's
        SecPlace.Present = false;      // a mission's
        SecFight.Present = false;      // a mission's
        SecPays.Present = false;       // a mission's
        SecWhen.Present = false;       // a mission's
        SecEnemy.Present = false;      // an enemy group's
        SecPerson.Present = false;     // a character of the mod's own
        SecLook.Present = false;       // a character of the mod's own
        SecEffects.Present = false;    // an ability's, once its effects have been read
        SecShop.Present = false;       // a weapon modification's
        SecPoints.Present = false;     // an ability's
        SecGates.Present = false;      // an ability's
        SecPlays.Present = false;      // a character of the mod's own
        SecRemake.Present = false;     // and only when one of its recipes is behind
        SecOffer.Present = false;      // only a character this mod OFFERS in a hiring pool
        SecChanges.Present = false;    // an OVERRIDE's: a copy cooks to a name of its own
    }

    /// <summary>
    /// DECLARE WHICH CARDS THIS PANEL HAS. Everything else goes absent.
    ///
    /// The ctor above defaults six cards to present, which was right while a part was the only
    /// thing with an editor and has been a slow leak ever since: every new kind of tab had to
    /// remember to turn each of them OFF, and the weapon editor's list of six `Present = false`
    /// lines is that memory written out. A specialization never got one, so it drew Meshes, Icon,
    /// Tints, Tags and Advanced over an asset that carries no geometry at all — five cards whose
    /// every control does nothing, on the panel of the one thing in this game that has no mesh.
    ///
    /// THE INVERSION IS THE POINT. An exclusion list goes stale the moment a card is added and
    /// the symptom is a card appearing where it does not belong, which nobody reports because it
    /// looks like a feature. An inclusion list goes stale the same way and the symptom is a card
    /// MISSING, which the person using it notices immediately.
    ///
    /// Cards that decide for themselves — the textures once the materials are read, the tints
    /// once the swatches are, a weapon's bolts once its actor is — are turned on by their own
    /// setters afterwards, so naming them here is unnecessary and harmless.
    /// </summary>
    public void ShowOnly(params EditSection[] has)
    {
        foreach (var s in _sections) s.Present = has.Contains(s);
    }

    /// <summary>
    /// WHILE THIS PANEL IS BEING FILLED, a control's own change events mean nothing.
    ///
    /// A ComboBox raises SelectionChanged when its ItemsSource is replaced and again when its
    /// SelectedItem is set — so a fill that writes a selection looks exactly like a person
    /// choosing one. For the mission choosers that only wrote the value that was already there;
    /// for the take-over chooser it was fatal, because its handler WRITES and then RE-FILLS, and
    /// the re-fill raised the event again. The stack trace was three thousand frames of
    /// SelectionChanged to SetLottery to FillLottery to SelectionChanged.
    ///
    /// A COUNTER, NOT A BOOL: fills nest — the mission fill runs the schedule fill runs a
    /// lottery fill — and a bool would be cleared by the inner one while the outer was still
    /// going.
    /// </summary>
    public bool Filling => _filling > 0;
    private int _filling;

    public void BeginFill() => _filling++;

    /// <summary>
    /// RELEASED AFTER THE LAYOUT PASS, not at the end of the method.
    ///
    /// A row pushed into a bound collection is not realised there and then: Avalonia builds its
    /// container on the next layout pass, and the ComboBox inside it selects something and
    /// writes that back through its TwoWay binding — after the fill has returned and the latch
    /// would already have been clear.
    ///
    /// That is not theoretical. "Take every row" set eleven rows to indices 0..10, saved them,
    /// rebuilt the card, and every one came back as row 0: eleven freshly realised ComboBoxes
    /// each pushing their own selection over the model. Posting the release at Background
    /// priority puts it behind Layout, so realisation happens while the latch is still up.
    /// </summary>
    public void EndFill()
        => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { if (_filling > 0) _filling--; },
            Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>
    /// Raise one property from outside.
    ///
    /// NARROW ON PURPOSE. Obs.Raise is protected and should stay so; what the view model needs is
    /// to tell a tab that a collection it owns has grown, which is the one case a caller cannot
    /// do for itself.
    /// </summary>
    public void Raise2(string name) => Raise(name);

    /// <summary>Light one line of the rail. The card it names is scrolled to by the view.</summary>
    public void SelectSection(EditSection which)
    {
        foreach (var s in _sections) s.Selected = ReferenceEquals(s, which);
    }

    // ------------------------------------------------------------------ what is not saved

    // WHAT THE FIELDS READ WHEN THE EDITOR OPENED. The save bar counts THIS SITTING'S changes,
    // so a part already carrying an override from last week opens at "nothing to save" rather
    // than announcing work nobody has just done.
    private string _wasName = "", _wasDescription = "", _wasLongDescription = "", _wasMesh = "", _wasIcon = "";
    private bool _wasAnySize, _wasLocksOwnSlot;

    // AND THE MOD'S OWN. Snapshotted by the same BeginEdit, so a details panel opens at "nothing
    // changed yet" over a project that has carried an author and a folder since the day it was
    // made — which is what the bar counts everywhere else.
    private string _wasAuthor = "", _wasVersion = "", _wasPicture = "";
    private string _wasShipsAs = "", _wasFolder = "";
    private string _wasRecruitKey = "", _wasRepairKey = "";
    private bool _wasByScript;

    private int _dirty;
    private string _dirtyNote = "";

    /// <summary>How many separate things are changed and unsaved.</summary>
    public int Dirty { get => _dirty; private set { if (Set(ref _dirty, value)) Raise(nameof(HasDirty)); } }

    public bool HasDirty => _dirty > 0;

    /// <summary>"2 changes not saved", for the bar along the bottom.</summary>
    public string DirtyCount =>
        _dirty == 0 ? "no unsaved changes" :
        _dirty == 1 ? "1 change not saved" : $"{_dirty} changes not saved";

    /// <summary>"— the masculine mesh, and one texture". Named, not just counted.</summary>
    public string DirtyNote { get => _dirtyNote; private set => Set(ref _dirtyNote, value); }

    /// <summary>
    /// Recount what is changed, and light the dots on the rail.
    ///
    /// Called from every field's setter and from every row's PropertyChanged, because a count
    /// that is recomputed only on save is a count that is wrong for the whole time anybody is
    /// looking at it.
    /// </summary>
    protected void Retally()
    {
        if (!_editing) return;

        var what = new List<string>();

        var identity = _editName != _wasName || _editDescription != _wasDescription ||
                       _editLongDescription != _wasLongDescription;
        if (_editName != _wasName) what.Add("the name");
        if (_editDescription != _wasDescription) what.Add("the description");
        if (_editLongDescription != _wasLongDescription) what.Add("the long description");

        var meshes = MeshRows.Count(r => r.Unsaved);
        var meshField = _editMesh != _wasMesh;
        if (meshField) what.Add("the mesh");
        if (meshes == 1) what.Add("one mesh");
        else if (meshes > 1) what.Add($"{meshes} meshes");
        if (_editAnySize != _wasAnySize) what.Add("the size rule");

        // THE ROW, not the field. An icon import writes the project as it happens, exactly as a
        // texture or a mesh does, so the row is what knows whether anything is still owed —
        // counting the field would leave "the picture" unsaved forever after a write that
        // already happened.
        var icon = _iconRow?.Unsaved == true || (_iconRow is null && _editIcon != _wasIcon);
        if (icon) what.Add("the picture");

        var textures = TextureRows.Count(r => r.Unsaved);
        if (textures == 1) what.Add("one texture");
        else if (textures > 1) what.Add($"{textures} textures");

        var colours = Swatches.Count(s => s.Unsaved);
        if (colours == 1) what.Add("one colour");
        else if (colours > 1) what.Add($"{colours} colours");

        var tags = _editRemoveTags.Trim().Length > 0 || _editAddTags.Trim().Length > 0;
        if (tags) what.Add("its tags");

        var slot = _hasOwnSlotLock && _editLocksOwnSlot != _wasLocksOwnSlot;
        if (slot) what.Add("the slot lock");

        // THE MOD'S OWN. Every one of these is "" or false on every other kind of tab, so they
        // compare equal and contribute nothing — the same way the spec and weapon fields already
        // sit in this count without a flag saying which tab is asking.
        var made = _editAuthor != _wasAuthor || _editVersion != _wasVersion;
        if (_editAuthor != _wasAuthor) what.Add("the author");
        if (_editVersion != _wasVersion) what.Add("the version");

        var picture = _editPicture != _wasPicture;
        if (picture) what.Add("its picture");

        var ships = _editShipsAs != _wasShipsAs || _editFolder != _wasFolder;
        if (_editShipsAs != _wasShipsAs) what.Add("what it ships as");
        if (_editFolder != _wasFolder) what.Add("its asset folder");

        var recruit = _editByScript != _wasByScript ||
                      _editRecruitKey != _wasRecruitKey || _editRepairKey != _wasRepairKey;
        if (recruit) what.Add("the recruit script");

        // WHAT IS INSIDE IT IS WRITTEN AS IT HAPPENS, as an import is — the row is set from the
        // project after the write, never before. So this lights the rail's dot and never adds to
        // the "not saved" count, which would otherwise never go down.
        SecInside.Changed = _inside.Any(r => r.IsMine);

        // THE TINT IS THE SAME: chosen from a picker or written on leaving the box, so the dot
        // means "this part carries its own", not "unsaved".
        SecTint.Changed = _editTintMask.Length > 0 || _editTintColour.Length > 0;
        SecFit.Changed = _hasOwnFit;

        SecIdentity.Changed = identity;
        SecMeshes.Changed = meshes > 0 || meshField || _editAnySize != _wasAnySize;
        SecIcon.Changed = icon;
        SecTextures.Changed = textures > 0;
        SecColours.Changed = colours > 0;
        SecOffered.Changed = tags;
        SecSlot.Changed = slot;
        SecMade.Changed = made;
        SecPicture.Changed = picture;
        SecShips.Changed = ships;
        SecRecruit.Changed = recruit;

        Dirty = what.Count;
        DirtyNote = what.Count switch
        {
            0 => "",
            1 => "— " + what[0],
            2 => $"— {what[0]}, and {what[1]}",
            _ => $"— {what[0]}, {what[1]}, and {what.Count - 2} more",
        };

        Raise(nameof(DirtyCount));
    }

    public void BeginEdit(string name, string description, string mesh, string icon)
    {
        _wasName = name;
        _wasDescription = description;
        _wasMesh = mesh;
        _wasIcon = icon;

        EditName = name;
        EditDescription = description;
        EditMesh = mesh;
        EditIcon = icon;
        _wasLongDescription = _editLongDescription;

        _wasAnySize = _editAnySize;
        _wasLocksOwnSlot = _editLocksOwnSlot;

        _wasAuthor = _editAuthor;
        _wasVersion = _editVersion;
        _wasPicture = _editPicture;
        _wasShipsAs = _editShipsAs;
        _wasFolder = _editFolder;
        _wasByScript = _editByScript;
        _wasRecruitKey = _editRecruitKey;
        _wasRepairKey = _editRepairKey;

        Editing = true;
        Retally();
    }

    /// <summary>The swatch rows, once the target has been read.</summary>
    public void SetSwatches(IEnumerable<SwatchRow> rows)
    {
        Swatches.Clear();
        foreach (var r in rows)
        {
            r.Mark();
            r.PropertyChanged += (_, _) => Retally();
            Swatches.Add(r);
        }

        Raise(nameof(HasSwatches));
        SecColours.Present = Swatches.Count > 0;
        SecColours.Count = Swatches.Count > 0 ? Swatches.Count.ToString() : "";
        Retally();
    }

    /// <summary>
    /// The picture is written and recorded; take it as the saved state.
    ///
    /// Both halves, because an icon import touches both: the TEXTURE override that carries the
    /// pixels, and <c>ModOverride.Icon</c>, which records what was chosen so reverting the part
    /// takes its picture with it.
    /// </summary>
    public void MarkIconSaved()
    {
        _wasIcon = _editIcon;
        _iconRow?.Mark();
        Retally();
    }

    /// <summary>
    /// EVERYTHING IN THE PANEL IS NOW WHAT THE PROJECT HOLDS.
    ///
    /// Called by Save. Without it the bar went on counting a change that had just been written —
    /// "2 changes not saved" beside a log line saying it saved — and the only way to quiet it was
    /// to close the editor and open it again. The rows already do this for themselves on an
    /// import, because an import writes the project as it happens; the typed fields had nobody
    /// doing it for them.
    /// </summary>
    public void MarkSaved()
    {
        _wasName = _editName;
        _wasDescription = _editDescription;
        _wasLongDescription = _editLongDescription;
        _wasMesh = _editMesh;
        _wasIcon = _editIcon;
        _wasAnySize = _editAnySize;
        _wasLocksOwnSlot = _editLocksOwnSlot;

        _wasAuthor = _editAuthor;
        _wasVersion = _editVersion;
        _wasPicture = _editPicture;
        _wasShipsAs = _editShipsAs;
        _wasFolder = _editFolder;
        _wasByScript = _editByScript;
        _wasRecruitKey = _editRecruitKey;
        _wasRepairKey = _editRepairKey;

        foreach (var r in MeshRows) r.Mark();
        foreach (var r in TextureRows) r.Mark();
        foreach (var s in Swatches) s.Mark();
        _iconRow?.Mark();

        Retally();
    }

    /// <summary>Everything in this sitting put back, without leaving the editor.</summary>
    public void UndoEdit()
    {
        EditName = _wasName;
        EditDescription = _wasDescription;
        EditLongDescription = _wasLongDescription;
        EditMesh = _wasMesh;
        EditIcon = _wasIcon;
        EditAnySize = _wasAnySize;
        EditLocksOwnSlot = _wasLocksOwnSlot;
        EditAuthor = _wasAuthor;
        EditVersion = _wasVersion;
        EditPicture = _wasPicture;
        EditShipsAs = _wasShipsAs;
        EditFolder = _wasFolder;
        EditByScript = _wasByScript;
        EditRecruitKey = _wasRecruitKey;
        EditRepairKey = _wasRepairKey;
        EditRemoveTags = "";
        EditAddTags = "";
        foreach (var s in Swatches) s.Offered = s.GameOffered;
        Retally();
    }

    public void EndEdit() => Editing = false;

    // ------------------------------------------------------------------ where the sheet sits

    /// <summary>
    /// THE READ SHEET HAS TWO PLACES AND ONE INSTANCE.
    ///
    /// Reading a part it fills the pane; editing one it becomes a narrow column beside the
    /// editor, so a field can be checked against what the part actually says without leaving
    /// the editor to go and look. A control has one parent, so rather than a second copy of the
    /// sheet — and a second place for its four templates to drift — the sheet stays where it is
    /// and its GRID CELL moves.
    /// </summary>
    public int SheetColumn => _editing ? 4 : 2;

    public int SheetSpan => _editing ? 1 : 3;

    /// <summary>NaN is Avalonia's "auto", which in the wide column means fill it.</summary>
    public double SheetWidth => _editing ? 330 : double.NaN;

    private bool _sheetWanted = true;

    /// <summary>Folded away from the header button, and only while editing.</summary>
    public bool SheetShown => !_editing || _sheetWanted;

    public string SheetToggle => _sheetWanted ? "What it is now  ▸" : "What it is now  ◂";

    /// <summary>
    /// The read sheet is folded away the first time the editor opens, and the header button brings
    /// it back. For an editor whose cards need the width: the sheet is a 330-pixel column beside
    /// them, and at an ordinary window width it is what draws over the boxes.
    /// </summary>
    public void FoldSheetOnce()
    {
        if (_foldedForEdit) return;
        _foldedForEdit = true;
        if (SheetShown) ToggleSheet();
    }
    private bool _foldedForEdit;

    public void ToggleSheet()
    {
        _sheetWanted = !_sheetWanted;
        Raise(nameof(SheetShown));
        Raise(nameof(SheetToggle));
    }

    // ------------------------------------------------------------------ shared helpers

    /// <summary>The last name in a path. A full object path is not what anybody reads a sheet for.</summary>
    protected static string Leaf(string? path)
    {
        if (path is not { Length: > 0 }) return "";
        var cut = path.LastIndexOfAny(new[] { '/', '.' });
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    /// <summary>
    /// A gameplay tag, as its last two segments.
    ///
    /// The ImGui panel's own rule: the tags read
    /// br.Customization.Slot.Character.Outfit.Helmet.Mesh, and everything before "Helmet / Mesh"
    /// is on every other tag too.
    /// </summary>
    protected static string ShortTag(string tag)
    {
        if (tag.Length == 0 || tag == "(none)") return "(none)";
        var parts = tag.Split('.');
        return parts.Length >= 2 ? string.Join(" / ", parts[^2..]) : tag;
    }

    /// <summary>
    /// Text without the game's own rich-text markup.
    ///
    /// The strings carry &lt;tags&gt; the game's UI understands and this panel does not, and
    /// showing them raw puts angle brackets in the middle of a sentence.
    /// </summary>
    protected static string Strip(string text)
    {
        if (text.Length == 0 || !text.Contains('<')) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        var inTag = false;
        foreach (var c in text)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}

using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A GAME MESH THIS MOD REWRITES, open as a tab: an override carrying the author's geometry.
///
/// Importing over a game mesh opened straight from the tree -- Mass Deffect's Runa, SK_MoiraVeigl -- records an
/// override, and the override listed in the mod as a row that opened nothing but "it has no editor here yet". So
/// there was no way back to the import, its size and axes, or the textures it wears.
///
/// THE MESH COPY'S SCREEN, because the work is the same: a mesh row (View, Import, Revert), the fit card, and the
/// textures its materials wear. What differs is where each edit lands, and that is the reason for a tab of its own
/// rather than a flag on <see cref="ReplacementTab"/>: an override rewrites the GAME's package at the game's own id,
/// so everything wearing the mesh changes, and its paint is not the override's to hold -- a replacement's textures are
/// separate overrides that repaint the game's own texture in place (ModBuild refuses textures on an override's slot
/// for exactly that reason). The texture rows therefore import the way a game texture's own Import does.
///
/// KEYED BY THE TARGET, not by the override object. Revert can take the override out of the project and an import
/// afterwards makes a new one; a tab holding the old object would edit a ghost.
/// </summary>
public sealed class OverrideTab : SheetTab
{
    public OverrideTab(string target)
    {
        Target = target;
    }

    /// <summary>The game package this mod rewrites.</summary>
    public string Target { get; }

    /// <summary>How to find the override in the project, given by the view model. ASKED EVERY TIME rather than held: a
    /// Revert can take the override out and a Wear or an Import elsewhere makes a new one, and a held object went on
    /// describing the old.</summary>
    public Func<ModOverride?> Find { get; set; } = () => null;

    /// <summary>The override as the project holds it now; null when this mod no longer rewrites the mesh.</summary>
    public ModOverride? Override => Find();

    public override string Title => Leaf(Target);

    public override string Subtitle => Target;

    public override string Tooltip => $"your changes to the game's own {Leaf(Target)}";

    public override string Aside => Override?.MeshFile is { Length: > 0 }
        ? $"your geometry over the game's {Leaf(Target)}"
        : $"the game's own {Leaf(Target)}";

    public override bool CanEdit => true;

    /// <summary>Its paint is read out of the game after the panel opens.</summary>
    public override bool ReadsTextures => true;

    public override string MeshesAre => "the game's own mesh, rewritten by this mod";

    public override string EditBadge => "EDITING THE GAME'S OWN MESH";

    public override string EditWarning =>
        "Your FBX replaces the game's own mesh, so everything that wears it changes. The textures below are the game's " +
        "own textures too: anything else that wears them changes with them.";

    public void Reread()
    {
        Raise(nameof(Sheet));
        Raise(nameof(Aside));
        Raise(nameof(Problem));
        Raise(nameof(HasProblem));
    }

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;
            var ov = Override;

            Fact("Replaces", Target);
            Fact("Your geometry", ov?.MeshFile is { Length: > 0 } file ? file : "the game's own");
            if (ov is not null)
            {
                if (ov.MeshScale != 0) Fact("Scale", ov.MeshScale.ToString("0.###"));
                if (ov.MeshBasis.Length > 0) Fact("Basis", ov.MeshBasis);
                if (ov.MeshOffsetX != 0 || ov.MeshOffsetY != 0 || ov.MeshOffsetZ != 0)
                    Fact("Offset", $"{ov.MeshOffsetX:0.###}, {ov.MeshOffsetY:0.###}, {ov.MeshOffsetZ:0.###}");
            }

            // WHAT A SLOT WEARS, where the override names one -- the one kind of paint an override itself carries.
            var named = ov?.Materials.Count(m => m.Material.Length > 0) ?? 0;
            if (named > 0)
            {
                blocks.Add(new SheetBand { Text = $"Materials — {named} slot(s) wear a material you named" });
                for (var i = 0; i < ov!.Materials.Count; i++)
                    if (ov.Materials[i].Material is { Length: > 0 } m)
                        blocks.Add(new SheetBullet { Text = $"Material {i + 1}  ·  wears {Leaf(m)}" });
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

using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A MESH THIS MOD SHIPS ITS OWN COPY OF, open as a tab.
///
/// A <see cref="ModMesh"/> is the standalone form of the thing a part and a weapon both do
/// inside themselves: the mod keeps its own copy of one of the game's meshes, and the author's
/// FBX and the author's textures go into the copy. The game's own mesh is never touched, so the
/// weapon that was copied still has its model — which is the whole reason a copy exists rather
/// than an override.
///
/// It carries geometry and paint and nothing else. No name, no slot, no icon, no tags: those
/// belong to the PART that wears the mesh, not to the mesh. So this tab draws two cards and the
/// rest are absent, which is the rule every card in this panel follows.
///
/// WHY IT HAS ITS OWN TAB AT ALL. A weapon's pieces reach their meshes through the weapon
/// editor, and a part's through the part editor — but a ModMesh can exist without either. Every
/// one made by the old studio's mesh replacement is one of these, and in the new Studio they
/// listed under "show only modified" as a row that opened nothing.
/// </summary>
public sealed class ReplacementTab : SheetTab
{
    public ReplacementTab(ModMesh mesh)
    {
        Mesh = mesh;
    }

    /// <summary>The mesh copy this tab edits.</summary>
    public ModMesh Mesh { get; }

    public override string Title => Leaf(Mesh.Source);

    public override string Subtitle => Mesh.Source;

    public override string Tooltip => $"your own copy of {Mesh.Source}";

    public override string Aside => Mesh.MeshFile.Length > 0
        ? $"your geometry over {Leaf(Mesh.Source)}"
        : $"your own copy of {Leaf(Mesh.Source)}";

    public override bool IsCopy => true;
    public override bool CanEdit => true;

    /// <summary>Its paint is read out of the game after the panel opens.</summary>
    public override bool ReadsTextures => true;

    /// <summary>
    /// A MESH'S ROWS ARE NOT BODY TYPES. The shared caption says "every body type, at once",
    /// which is true of a garment and meaningless over a single mesh copy.
    /// </summary>
    public override string MeshesAre => "the geometry this copy carries";

    public override string EditBadge => "YOUR OWN COPY OF A MESH";

    public override string EditWarning =>
        "The mod keeps its own copy of this mesh. Your FBX and your textures go into the copy — " +
        "the game's own mesh is never touched, so anything else wearing it is unchanged.";

    /// <summary>
    /// WHO WEARS IT, filled by the view model.
    ///
    /// A mesh copy nothing points at builds, installs and is never loaded. That is not a fault
    /// worth refusing — a copy made before the part that will wear it is a normal order to work
    /// in — but it is worth saying, because the failure looks identical to a broken import.
    /// </summary>
    public string WornBy { get; set; } = "";

    public override string Problem => WornBy.Length > 0 ? "" :
        "Nothing in this mod points at this copy yet, so the game will never load it. A weapon " +
        "piece or a part has to name it before your work here reaches the screen.";

    public void Reread()
    {
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

            Fact("Copied from", Mesh.Source);
            Fact("Your geometry", Mesh.MeshFile.Length > 0 ? Mesh.MeshFile : "the game's own");
            if (Mesh.MeshScale != 0) Fact("Scale", Mesh.MeshScale.ToString("0.###"));
            if (Mesh.MeshBasis.Length > 0) Fact("Basis", Mesh.MeshBasis);
            if (Mesh.MeshOffsetX != 0 || Mesh.MeshOffsetY != 0 || Mesh.MeshOffsetZ != 0)
                Fact("Offset", $"{Mesh.MeshOffsetX:0.###}, {Mesh.MeshOffsetY:0.###}, " +
                               $"{Mesh.MeshOffsetZ:0.###}");
            Fact("Worn by", WornBy.Length > 0 ? WornBy : "nothing in this mod yet");

            // PER SLOT, because that is how the build paints. A model with four material groups
            // and paint on the second is a different thing from one with paint on all four, and
            // a single count cannot tell them apart.
            var painted = Mesh.Materials.Count(m => m.Textures.Count > 0);
            blocks.Add(new SheetBand
            {
                Text = Mesh.Materials.Count == 0 ? "Paint — none yet"
                     : $"Paint — {Mesh.Materials.Count} material slot(s), {painted} with artwork"
            });

            for (var i = 0; i < Mesh.Materials.Count; i++)
            {
                var m = Mesh.Materials[i];
                var bits = new List<string>();
                if (m.Material.Length > 0) bits.Add("wears " + Leaf(m.Material));
                if (m.SlotName.Length > 0) bits.Add("tint slot " + m.SlotName);
                if (m.Textures.Count > 0) bits.Add($"{m.Textures.Count} texture(s)");
                blocks.Add(new SheetBullet
                {
                    Text = $"Material {i + 1}" +
                           (bits.Count > 0 ? "  ·  " + string.Join(", ", bits) : "  ·  unchanged")
                });
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

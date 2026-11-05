using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// One folder in the Data Explorer tree, or one file behind a row of the list under it.
///
/// A WRAPPER RATHER THAN BINDING TO <see cref="AssetNode"/> DIRECTLY, for two reasons. AssetNode
/// exposes public FIELDS, and Avalonia's bindings want properties. And its children are a plain
/// List that is fully built at mount — 179,966 of them — so a tree bound straight to it would
/// have the view ask about every node in the game.
///
/// <see cref="Folders"/> materialises ONE level, ON FIRST ACCESS, which for a TreeView means when
/// a folder is actually expanded. Opening the Studio therefore costs a few dozen wrappers, not
/// two hundred thousand.
/// </summary>
/// <summary>
/// What the tree is allowed to show while the Filter box has something in it.
///
/// ONE OBJECT SHARED BY EVERY NODE, rather than a predicate each node evaluates for itself.
/// Deciding whether a folder is worth showing means asking whether anything beneath it matches,
/// and a node cannot answer that without walking its whole subtree — 179,966 files, once per
/// folder, per keystroke. So the walk happens ONCE in the view model and lands here as a set.
/// </summary>
public sealed class NodeFilter
{
    /// <summary>Folders on the path to at least one match. Everything else is not drawn.</summary>
    public required HashSet<AssetNode> Folders { get; init; }

    /// <summary>Whether one file matches. The list under the tree shows only these.</summary>
    public required Func<AssetNode, bool> FileMatches { get; init; }
}

public sealed class GameNode : Obs
{
    private readonly AssetNode _node;
    private readonly string? _label;
    private readonly bool _leaf;
    private readonly NodeFilter? _filter;
    private List<GameNode>? _folders;

    /// <param name="label">Shown instead of the node's own name. Only [root] uses it.</param>
    /// <param name="leaf">Never offer children, however many it has. Only [root] uses it.</param>
    /// <param name="filter">What the Filter box allows through, or null for everything.</param>
    /// <param name="open">
    /// The set of folder paths the tree has open, SHARED by every node of that tree.
    ///
    /// The fold used to live on the node and nowhere else, and BuildGameTop discards every node
    /// and builds new ones on each keystroke in the Filter box, so every folder the author had
    /// opened by hand slammed shut as he typed. Holding it by path outside the nodes means the
    /// rebuild restores what was open instead of forgetting it.
    ///
    /// Null for a tree that does not want the memory. The mod tree passes nothing because it
    /// opens everything by hand anyway.
    /// </param>
    public GameNode(AssetNode node, string? label = null, bool leaf = false,
                    NodeFilter? filter = null, HashSet<string>? open = null)
    {
        _node = node;
        _label = label;
        _leaf = leaf;
        _filter = filter;
        _open = open;
        _expanded = open is not null && open.Contains(node.FullPath);
    }

    private readonly HashSet<string>? _open;

    public AssetNode Node => _node;
    public string Name => _label ?? _node.Name;
    public string FullPath => _node.FullPath;
    public bool IsFolder => _node.IsFolder;
    public AssetKind Kind => _node.Kind;

    /// <summary>
    /// THE TREE HOLDS FOLDERS ONLY, which is the whole shape of a Data Explorer: navigate above,
    /// read the contents below. A tree carrying both makes somebody scroll past 900 files to
    /// reach the next folder, and that is the thing the split pane exists to stop.
    /// </summary>
    public IReadOnlyList<GameNode> Folders
        => _folders ??= _leaf
            ? new List<GameNode>()
            : _node.Children
                   .Where(c => c.IsFolder && (_filter is null || _filter.Folders.Contains(c)))
                   .Select(c => new GameNode(c, filter: _filter, open: _open))
                   .ToList();

    /// <summary>
    /// Whether the tree has this folder open.
    ///
    /// ON THE NODE RATHER THAN LEFT TO THE TreeView, because selection from outside cannot work
    /// without it: Avalonia can only select a row whose container exists, and a container only
    /// exists once its ancestors are expanded. Asking for a folder five levels down while they
    /// were shut did not fail — the tree quietly selected its FIRST row instead, and the panel
    /// underneath filled with that folder's contents as though nothing had gone wrong.
    /// </summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!Set(ref _expanded, value) || _open is null) return;
            // REPORTED BY PATH, because the node itself will not survive the next rebuild.
            if (value) _open.Add(FullPath); else _open.Remove(FullPath);
        }
    }

    private bool _expanded;

    /// <summary>
    /// A PAYLOAD, not an asset of its own.
    ///
    /// UE splits one asset across several files: the .uasset carries the exports and a .ubulk or
    /// .uptnl carries the bulk data beside it. Listing them as separate rows showed every texture
    /// TWICE — once typed Texture2D and once as a bare "Asset", because ResolveKinds only reads a
    /// class out of a .uasset or a .umap and leaves the payload unresolved. The author's own
    /// screenshot of the Textures folder said "32 items" for sixteen textures.
    ///
    /// SUBTRACTIVE on purpose. An allow-list of .uasset and .umap would also be right for these
    /// containers, and is the line ResolveKinds draws — but it would silently hide any other file
    /// the game ships, and something unexpected disappearing from the explorer is a worse failure
    /// than a duplicate appearing in it.
    /// </summary>
    private static bool IsPayload(AssetNode n) =>
        n.FullPath.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase) ||
        n.FullPath.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase) ||
        n.FullPath.EndsWith(".uptnl", StringComparison.OrdinalIgnoreCase);

    /// <summary>The files directly in this folder — the rows of the list underneath.</summary>
    public IEnumerable<AssetNode> Assets
        => _node.Children.Where(c => !c.IsFolder && !IsPayload(c)
                                     && (_filter is null || _filter.FileMatches(c)));

    /// <summary>Every file at or beneath it, for the toolbar's flatten toggle.</summary>
    public IEnumerable<AssetNode> AllAssets
        => _node.DescendantFiles().Where(c => !IsPayload(c)
                                              && (_filter is null || _filter.FileMatches(c)));

    /// <summary>How many folders it holds, for the dim number at the end of a tree row.</summary>
    public string Detail
    {
        get
        {
            // WHAT IS ACTUALLY SHOWN, not what the folder holds. While a filter is on, a count of
            // everything in the folder describes rows that are not on screen.
            // COUNTS WHAT THE LIST WILL SHOW, filter or no filter. The unfiltered branch used
            // the raw child count, which includes the .ubulk payloads the list now hides — so a
            // Textures folder read "32" in the tree and "16 items" underneath it, about the same
            // folder, at the same moment.
            var n = Folders.Count + Assets.Count();
            return n == 0 ? "" : $"{n:N0}";
        }
    }

    /// <summary>
    /// The Type column, and the type shown anywhere else an asset is named.
    ///
    /// Taken from the index's own Kind rather than derived from the file extension, because the
    /// extension is .uasset for nine of these ten and would tell nobody anything.
    /// </summary>
    public static string TypeName(AssetKind kind) => kind switch
    {
        AssetKind.SkeletalMesh => "SkeletalMesh",
        AssetKind.StaticMesh   => "StaticMesh",
        AssetKind.Animation    => "AnimSequence",
        AssetKind.Texture      => "Texture2D",
        AssetKind.Material     => "Material",
        AssetKind.Sound        => "SoundWave",
        AssetKind.Level        => "World",
        AssetKind.Blueprint    => "Blueprint",
        AssetKind.Data         => "DataAsset",
        _ => "Asset",
    };

    /// <summary>
    /// What clicking this WOULD open. The viewport and the texture view are not built yet, so
    /// this is the routing decision written down where it can be read — and it is the real
    /// decision, taken from the index's own Kind rather than from the file extension.
    /// </summary>
    public static string OpensFor(AssetKind kind) => kind switch
    {
        AssetKind.SkeletalMesh => "3D viewport — skeletal mesh",
        AssetKind.StaticMesh   => "3D viewport — static mesh",
        AssetKind.Animation    => "3D viewport — animation",
        AssetKind.Texture      => "texture view",
        AssetKind.Material     => "material view",
        AssetKind.Sound        => "sound player",
        AssetKind.Level        => "level",
        _ => "property view",
    };
}

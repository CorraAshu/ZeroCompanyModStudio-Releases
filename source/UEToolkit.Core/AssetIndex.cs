using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

public enum AssetKind
{
    Unknown, SkeletalMesh, StaticMesh, Animation, Texture, Material, Sound, Blueprint, Level, Data
}

public sealed class AssetNode
{
    public string Name = "";
    public string FullPath = "";
    public bool IsFolder;
    public AssetKind Kind = AssetKind.Unknown;
    public long Size;

    public readonly List<AssetNode> Children = new();
    public AssetNode? Parent;

    /// <summary>Folders sort first, then alphabetically — matches how people expect a tree to read.</summary>
    public void SortRecursive()
    {
        Children.Sort((a, b) =>
            a.IsFolder != b.IsFolder
                ? (a.IsFolder ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var c in Children) c.SortRecursive();
    }

    public IEnumerable<AssetNode> DescendantFiles()
    {
        foreach (var c in Children)
        {
            if (!c.IsFolder) yield return c;
            else foreach (var d in c.DescendantFiles()) yield return d;
        }
    }
}

/// <summary>
/// Builds the browsable tree from a mounted provider. Kind is inferred from path/extension
/// only — cheap, and good enough for icons and filtering. Real class resolution happens
/// lazily when an asset is opened.
/// </summary>
public sealed class AssetIndex
{
    public AssetNode Root { get; } = new() { Name = "", IsFolder = true };
    public int FileCount { get; private set; }

    private readonly Dictionary<string, AssetNode> _folders = new(StringComparer.OrdinalIgnoreCase);

    public static AssetIndex Build(IFileProvider provider, IReadOnlyCollection<string>? excludePaths = null)
    {
        var idx = new AssetIndex();
        idx._folders[""] = idx.Root;

        foreach (var (path, file) in provider.Files)
        {
            if (excludePaths is { Count: > 0 } &&
                excludePaths.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase)))
                continue;

            var slash = path.LastIndexOf('/');
            var dir = slash < 0 ? "" : path[..slash];
            var name = slash < 0 ? path : path[(slash + 1)..];

            var parent = idx.GetOrCreateFolder(dir);
            var node = new AssetNode
            {
                Name = name,
                FullPath = path,
                IsFolder = false,
                Kind = Classify(path),
                Size = file.Size,
                Parent = parent
            };
            parent.Children.Add(node);
            idx.FileCount++;
        }

        idx.Root.SortRecursive();
        return idx;
    }

    private AssetNode GetOrCreateFolder(string dir)
    {
        if (_folders.TryGetValue(dir, out var existing)) return existing;

        var slash = dir.LastIndexOf('/');
        var parentDir = slash < 0 ? "" : dir[..slash];
        var name = slash < 0 ? dir : dir[(slash + 1)..];

        var parent = GetOrCreateFolder(parentDir);
        var node = new AssetNode { Name = name, FullPath = dir, IsFolder = true, Parent = parent };
        parent.Children.Add(node);
        _folders[dir] = node;
        return node;
    }

    /// <summary>
    /// Path/extension heuristics. Deliberately conservative: anything uncertain stays
    /// Unknown rather than being mislabelled in the UI.
    /// </summary>
    /// <summary>The active game's naming conventions; the defaults are the common Unreal prefixes (see <see cref="ClassifyRules"/>).</summary>
    public static ClassifyRules Rules { get; set; } = new();

    public static AssetKind Classify(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".wem" or ".bnk") return AssetKind.Sound;
        if (ext == ".umap") return AssetKind.Level;
        if (ext is ".ubulk" or ".uptnl" or ".uexp") return AssetKind.Unknown;
        if (ext != ".uasset") return AssetKind.Unknown;

        var name = Path.GetFileNameWithoutExtension(path);
        var lower = path.ToLowerInvariant();
        var r = Rules;

        bool Pre(List<string> ps) => ps.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));
        bool Fold(List<string> fs) => fs.Any(x => lower.Contains(x.ToLowerInvariant()));
        bool Suf(List<string> ss) => ss.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase));

        if (Pre(r.SkeletalMeshPrefixes)) return AssetKind.SkeletalMesh;
        if (Pre(r.StaticMeshPrefixes)) return AssetKind.StaticMesh;
        if (Pre(r.TexturePrefixes) || Suf(r.TextureSuffixes)) return AssetKind.Texture;
        if (Pre(r.MaterialPrefixes)) return AssetKind.Material;
        if (Pre(r.AnimationPrefixes) || Fold(r.AnimationFolders)) return AssetKind.Animation;
        if (Pre(r.BlueprintPrefixes)) return AssetKind.Blueprint;
        if (Fold(r.TextureFolders)) return AssetKind.Texture;
        if (Fold(r.MaterialFolders)) return AssetKind.Material;
        // The Skeleton and the PhysicsAsset sit in the rig folder beside the mesh, so they have
        // to be taken out before the folder can stand for "mesh".
        bool Has(List<string> cs) => cs.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));

        if (Suf(r.DataSuffixes) || Pre(r.DataPrefixes) || Has(r.DataContains)) return AssetKind.Data;
        if (Fold(r.SkeletalMeshFolders)) return AssetKind.SkeletalMesh;

        return AssetKind.Data;
    }

    /// <summary>
    /// The class each package actually declares, read from its export map. Names are a guess;
    /// this is the answer. Resolving does not deserialise the export, so it costs about as much
    /// as opening the package header — cheap per asset, far too slow for all 860,000 of them,
    /// which is why <see cref="Classify"/> still exists and this is applied to a scoped set.
    ///
    /// Returns how many nodes changed kind. A caller that gets a non-zero count from a folder it
    /// cares about has just learned its profile's naming rules are wrong for this game.
    /// </summary>
    /// <summary>
    /// Is this the OPTIONAL SEGMENT of a package rather than an asset in its own right?
    ///
    /// Some games ship a `.o.uasset` beside almost every `.uasset` — the same package's optional
    /// data, not a second asset. It ends in `.uasset` like anything else, so every substring
    /// command that takes the first match can land on it, and `LoadPackage` then throws
    /// `KeyNotFoundException: There is no game file with the path "....o"`. `--props` on a
    /// Blueprint crashed exactly that way, and `--meshdiag` reported the same failure more quietly.
    ///
    /// They stay in the index because they are real files a search should be able to find. What
    /// they must not be is the thing a command silently *picks*.
    /// </summary>
    public static bool IsOptionalSegment(string path) =>
        path.EndsWith(".o.uasset", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".o.umap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prefer a real asset over its optional segment when both match a query.</summary>
    public static IEnumerable<AssetNode> PrimaryFirst(IEnumerable<AssetNode> nodes) =>
        nodes.OrderBy(n => IsOptionalSegment(n.FullPath) ? 1 : 0);

    public static int ResolveKinds(IFileProvider provider, IEnumerable<AssetNode> nodes)
    {
        var changed = 0;
        foreach (var n in nodes)
        {
            if (n.IsFolder) continue;
            if (!n.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                !n.FullPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            var cls = RealClass(provider, n.FullPath);
            if (cls is null) continue;
            var kind = KindFromClass(cls);
            if (kind == n.Kind) continue;
            n.Kind = kind;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// The class of a package's primary object, or null if it would not open.
    ///
    /// "Primary" is the export named after the package, NOT export 0: a `..._rig_Skeleton`
    /// asset can lead with a SkeletalMeshSocket, and taking the first export made it look like the game
    /// stored sockets as top-level assets.
    /// </summary>
    public static string? RealClass(IFileProvider provider, string path)
    {
        // Under the package lock: KindResolver calls this from a BACKGROUND thread every time a
        // folder is expanded, while the render thread may be loading a mesh from the same cache.
        // Racing it raises an AccessViolationException that cannot be caught -- see AssetLoader.
        try
        {
            return AssetLoader.Read<string?>(() =>
            {
                var pkg = provider.LoadPackage(path);
                if (pkg.ExportMapLength == 0) return null;
                var want = Path.GetFileNameWithoutExtension(path);
                var idx = pkg.GetExportIndex(want, StringComparison.OrdinalIgnoreCase);
                var resolved = new CUE4Parse.UE4.Objects.UObject.FPackageIndex(pkg, (idx >= 0 ? idx : 0) + 1)
                    .ResolvedObject;
                return resolved?.Class?.Name.Text;
            });
        }
        catch { return null; }
    }

    /// <summary>
    /// UE class name to the tool's coarser kinds. Anything unrecognised is Data rather than
    /// Unknown: the class WAS read, so "we do not handle this" is the honest label, not "we
    /// could not tell".
    /// </summary>
    public static AssetKind KindFromClass(string cls) => cls switch
    {
        "SkeletalMesh" or "DestructibleMesh" => AssetKind.SkeletalMesh,
        "StaticMesh" => AssetKind.StaticMesh,
        "Texture2D" or "TextureCube" or "TextureRenderTarget2D" or "VolumeTexture"
            or "Texture2DArray" => AssetKind.Texture,
        "World" or "Level" => AssetKind.Level,
        "SoundWave" or "SoundCue" => AssetKind.Sound,
        _ when cls.StartsWith("AnimSequence") || cls is "AnimMontage" or "AnimComposite"
            || cls.StartsWith("BlendSpace") || cls.StartsWith("AimOffset")
            || cls == "PoseAsset" => AssetKind.Animation,
        _ when cls.StartsWith("Material") => AssetKind.Material,
        _ when cls.StartsWith("AkEvent") || cls.StartsWith("AkAudio") => AssetKind.Sound,
        _ when cls.EndsWith("BlueprintGeneratedClass") || cls == "Function" => AssetKind.Blueprint,
        _ => AssetKind.Data
    };

    /// <summary>Substring search over full paths. Cheap enough to run per keystroke.</summary>
    public List<AssetNode> Search(string query, AssetKind? kind = null, int limit = 500)
    {
        var results = new List<AssetNode>();
        if (string.IsNullOrWhiteSpace(query) && kind is null) return results;

        foreach (var f in Root.DescendantFiles())
        {
            if (kind is not null && f.Kind != kind) continue;
            if (!string.IsNullOrWhiteSpace(query) &&
                !f.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(f);
            if (results.Count >= limit) break;
        }
        return results;
    }
}

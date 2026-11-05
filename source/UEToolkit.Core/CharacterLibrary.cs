using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;

namespace UEToolkit.Core;

/// <summary>Where a part's meshes came from, so a stated fact is never mistaken for a guess.</summary>
public enum PartSource
{
    /// <summary>A region table the game itself ships said so.</summary>
    RegionTable,
    /// <summary>The game said this region loads nothing.</summary>
    DeclaredEmpty,
    /// <summary>No region data for this model; resolved by filename convention.</summary>
    Heuristic,
    /// <summary>A head chosen from a head table the game ships, which its region table does not carry.</summary>
    HeadTable,
    /// <summary>
    /// The game ships no model tags at all; the character is the FOLDER its parts live in.
    /// Weaker than a region table and honestly so -- nothing here is the game's own statement
    /// about which parts belong together, only where an artist filed them.
    /// </summary>
    FolderGrouped,
    Unresolved,
}

public sealed record CharacterPart(
    string Region, string Permutation, IReadOnlyList<MeshRef> Meshes, PartSource Source)
{
    public string Display => Meshes.Count == 0
        ? $"{Region}: {Permutation} ({(Source == PartSource.DeclaredEmpty ? "none" : "no mesh")})"
        : $"{Region}: {string.Join(" + ", Meshes.Select(m => m.DisplayName))}";
}

public sealed record CharacterVariant(string Name, IReadOnlyList<CharacterPart> Parts)
{
    public int ResolvedParts => Parts.Count(p => p.Meshes.Count > 0);
}

public sealed record CharacterModel(string Name, string TagPath,
    IReadOnlyList<CharacterVariant> Variants, bool HasRegionData = false);

/// <summary>
/// The characters of the mounted game: models, each with named variants, each variant a set of
/// parts, each part the meshes it loads. A game that states this in its own data feeds the
/// library through a registered <see cref="IGameModule"/>; one that does not is grouped by
/// the folders its meshes sit in.
/// </summary>
public sealed class CharacterLibrary
{
    private readonly List<CharacterModel> _models = new();

    public bool IsBuilt { get; private set; }
    public IReadOnlyList<CharacterModel> Models => _models;

    /// <summary>Populate from a cached list, skipping the expensive scan.</summary>
    public void LoadFrom(IReadOnlyList<CharacterModel> models)
    {
        _models.Clear();
        _models.AddRange(models);
        IsBuilt = true;
    }

    /// <summary>
    /// Folder names that hold a character's meshes rather than name the character. When the
    /// meshes sit in one of these, the character is the folder ABOVE:
    /// `Characters/Humanoid/Anakin/Model/SK_..._ARMS` is Anakin, not "Model".
    /// </summary>
    private static readonly string[] MeshContainerFolders =
        { "model", "models", "mesh", "meshes", "rig", "skeletalmeshes" };

    public void Build(IFileProvider provider, AssetIndex index, Action<string>? log = null,
                      IReadOnlyList<string>? characterPaths = null)
    {
        _models.Clear();

        // Characters the game states outright, read by whichever game modules the application
        // registered. Where a module has an answer it is used verbatim; the folder grouping
        // below is only reached when none of them found anything.
        foreach (var module in GameModules.All)
            _models.AddRange(module.ReadCharacterModels(provider, index, log));

        // Most games file a character as a folder of parts and state nothing about which belong
        // together, so without this the tab was empty for them. It runs ONLY when no module
        // found anything: where the game states which parts belong together, that statement
        // wins outright and a folder name never gets a vote.
        if (_models.Count == 0 && characterPaths is { Count: > 0 })
            BuildFromFolders(index, characterPaths, log);

        _models.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        IsBuilt = true;

        var parts = _models.SelectMany(m => m.Variants).SelectMany(v => v.Parts).ToArray();
        log?.Invoke($"Character library: {_models.Count} models " +
                    $"({_models.Count(m => m.HasRegionData)} with game region data), " +
                    $"{_models.Sum(m => m.Variants.Count)} variants; parts from game data " +
                    $"{parts.Count(p => p.Source is PartSource.RegionTable or PartSource.DeclaredEmpty)}, " +
                    // FolderGrouped counts as GUESSED. It is a filing convention, not the game's
                    // own answer, and a summary that reported "guessed 0" for a library built
                    // entirely from folder names would be stating the opposite of the truth.
                    $"guessed {parts.Count(p => p.Source is PartSource.Heuristic or PartSource.HeadTable or PartSource.FolderGrouped)}, " +
                    $"unresolved {parts.Count(p => p.Source == PartSource.Unresolved)}");
    }

    /// <summary>
    /// Build characters from folder layout, for games that ship no model tags.
    ///
    /// The rule is deliberately dull: every skeletal/static mesh under one of the profile's
    /// CharacterPaths is grouped by the folder that owns it, stepping up one level when the
    /// meshes sit in a `Model/`-style container. A group needs at least one SKELETAL mesh to
    /// count -- otherwise every prop folder under Characters/ becomes a "character".
    ///
    /// It reports itself as FolderGrouped rather than borrowing the region table's name, because
    /// this is a filing convention and not the game's own answer. Zero Company is tidy about it
    /// (Humanoid/Anakin/Model/, Droid/B1_BattleDroid/Model/); a game that is not will produce
    /// odd groupings, and the source tag is what makes that visible instead of plausible.
    /// </summary>
    private void BuildFromFolders(AssetIndex index, IReadOnlyList<string> roots, Action<string>? log)
    {
        var groups = new Dictionary<string, List<AssetNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in index.Root.DescendantFiles())
        {
            if (f.Kind is not (AssetKind.SkeletalMesh or AssetKind.StaticMesh)) continue;
            if (!roots.Any(r => f.FullPath.Contains(r.Replace('\\', '/'),
                                                    StringComparison.OrdinalIgnoreCase))) continue;

            var slash = f.FullPath.LastIndexOf('/');
            if (slash <= 0) continue;
            var dir = f.FullPath[..slash];

            // Cut at the HIGHEST container folder, not just the last one. Parts are often nested
            // another level down for grouping -- B1_BattleDroid/Model/Coil/SK_..._Coil_01 -- and
            // stepping up only once files that coil as a character named "Coil".
            var segs = dir.Split('/');
            var cut = -1;
            for (var i = 0; i < segs.Length; i++)
                if (MeshContainerFolders.Contains(segs[i].ToLowerInvariant())) { cut = i; break; }
            if (cut > 0) dir = string.Join('/', segs.Take(cut));

            if (!groups.TryGetValue(dir, out var list)) groups[dir] = list = new List<AssetNode>();
            list.Add(f);
        }

        // Folder names collide: Humanoid/Anakin and Humanoid/_Hair/Anakin are both "Anakin", and
        // a lookup then silently answers with whichever sorted first -- which is how Anakin came
        // back carrying nothing but his hair. Where a leaf name is not unique, qualify it with the
        // parent folder so both are addressable and the difference is visible in the list.
        var leafCounts = groups.Keys
            .GroupBy(d => d[(d.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var kept = 0;
        foreach (var (dir, nodes) in groups)
        {
            if (!nodes.Any(n => n.Kind == AssetKind.SkeletalMesh)) continue;

            var parts = nodes
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => new CharacterPart(
                    RegionFromName(Path.GetFileNameWithoutExtension(n.Name)),
                    "default",
                    new[]
                    {
                        new MeshRef(n.FullPath, n.Kind == AssetKind.SkeletalMesh,
                                    Array.Empty<string>(), Array.Empty<MaterialOverride>(),
                                    null, null),
                    },
                    PartSource.FolderGrouped))
                .ToList();

            var leaf = dir[(dir.LastIndexOf('/') + 1)..];
            var name = leaf;
            if (leafCounts.TryGetValue(leaf, out var n) && n > 1)
            {
                // "Leaf (Parent)", NOT "Parent/Leaf". A '/' in a model name collides with
                // --character's own model/variant separator, so "Humanoid/Anakin" parsed as
                // model "Humanoid", variant "Anakin" and matched nothing. This form also keeps
                // the two Anakins adjacent in an alphabetical list instead of filing one under
                // "_Hair", which is where a reader will look for them.
                var up = dir[..dir.LastIndexOf('/')];
                name = $"{leaf} ({up[(up.LastIndexOf('/') + 1)..]})";
            }
            _models.Add(new CharacterModel(name, dir,
                new[] { new CharacterVariant("default", parts) }, HasRegionData: false));
            kept++;
        }

        var dupes = leafCounts.Count(kv => kv.Value > 1);
        log?.Invoke($"Character library: no model tags in this game, grouped by folder instead " +
                    $"-- {kept:N0} character(s) from {groups.Count:N0} folder(s) under " +
                    $"{string.Join(", ", roots)}" +
                    (dupes > 0 ? $"; {dupes} name(s) qualified by parent folder to stay unique" : ""));
    }

    /// <summary>
    /// A part's region, guessed from the trailing name segment: SK_HTAM_AnakinA_ARMS -> ARMS.
    /// Only a label for the UI -- nothing downstream places a mesh by it.
    /// </summary>
    private static string RegionFromName(string stem)
    {
        var cut = stem.LastIndexOf('_');
        var tail = cut > 0 && cut < stem.Length - 1 ? stem[(cut + 1)..] : "";
        return tail.Length is > 0 and <= 12 ? tail : "part";
    }
}

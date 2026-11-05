using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Animation;

namespace UEToolkit.Core;

public sealed record AnimEntry(string Path, string Name, int Frames, float Fps, string SkeletonPath)
{
    public float Duration => Fps > 0 ? Frames / Fps : 0f;
}

/// <summary>
/// Indexes every AnimSequence by the skeleton it targets, so selecting a mesh can show
/// exactly the animations that will actually play on it. Built once in the background —
/// the alternative is loading hundreds of packages on every selection.
/// </summary>
public sealed class AnimationLibrary
{
    private readonly Dictionary<string, List<AnimEntry>> _bySkeleton = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AnimEntry> _all = new();

    public bool IsBuilt { get; private set; }
    public int Count => _all.Count;
    public int Skipped { get; private set; }

    public IReadOnlyList<AnimEntry> All => _all;

    /// <summary>
    /// Every skeleton asset some animation targets. A few dozen against thousands of sequences,
    /// which is what makes <see cref="RigCompatibility"/> affordable -- see the note there for
    /// why a mesh's own skeleton is usually not among them.
    /// </summary>
    public IReadOnlyCollection<string> SkeletonPaths => _bySkeleton.Keys;

    /// <summary>
    /// WHAT KIND OF ANIMATION THIS IS, from where the game keeps it.
    ///
    /// A flat list of 2,720 names is not a list, it is a search box with scenery. The game has
    /// already sorted them and the folder tree says so: everything under `.../Anim/<set>` is one
    /// weapon set, and this game's 1,734 humanoid sequences fall into twelve groups that read
    /// exactly as an author would name them -- 2HRifle, 1HPistol, 1HMelee, Unarmed, 1HThrowR,
    /// Cinematics, and a few small ones.
    ///
    /// Derived rather than configured, so it degrades on a game whose tree is shaped differently:
    /// no `Anim` folder just means the containing folder is the group.
    /// </summary>
    public static string CategoryOf(string assetPath)
    {
        var folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "";
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "Other";

        for (var i = 0; i < parts.Length; i++)
        {
            if (!parts[i].Equals("Anim", StringComparison.OrdinalIgnoreCase)) continue;
            // The set, not the sub-folder inside it: 2HRifle/DeathPrototype is still 2HRifle,
            // and a reader looking for a rifle animation should not have to know that.
            if (i + 1 < parts.Length) return parts[i + 1];
            // Sitting directly IN an Anim folder: the owner is the useful name. Without this,
            // 632 animations from a dozen different characters all landed in one group called
            // "Anim", which names nothing.
            if (i > 0) return parts[i - 1];
        }
        // Every cinematic is its own scene folder -- 60-odd of them here -- which is a hundred
        // groups of fifteen. One group.
        foreach (var p in parts)
            if (p.Equals("Cinematics", StringComparison.OrdinalIgnoreCase)) return "Cinematics";

        return parts[^1];
    }

    /// <summary>
    /// THE ACTION a sequence performs, for the second heading level inside a category.
    ///
    /// A category is a FOLDER and these folders are flat -- all 998 sequences of 1HPistol sit
    /// directly in it, no sub-folders -- so the only thing left to read is the NAME, and the
    /// names carry the structure:
    ///
    ///     A_1HPistol_Aim_Enter_0                  a plain sequence
    ///     AM_1HPistol_Crouch_Aim_Exit_90R         an additive
    ///     A_Kabb_1HPistol_HitReact_Hit_Light_Add  a character's own, with the name in the middle
    ///     PSD_1HPistol_...  AO_  BS_  CHT_        four more prefixes
    ///
    /// Five naming styles and the prefix cannot be relied on -- but every one of them names the
    /// CATEGORY, and what follows it is the action. So this finds the category token and takes
    /// the next segment, which grouped 998 of 998 with nothing left over; reading the first
    /// segment instead put 624 of them in a bucket called "A" or "AM".
    ///
    /// "" when the name does not carry the category at all, which the caller shows as one group
    /// rather than dropping -- a sequence that cannot be classified still has to be playable.
    /// </summary>
    public static string ActionOf(string assetPath, string category)
    {
        if (category.Length == 0) return "";
        var leaf = Path.GetFileNameWithoutExtension(assetPath);
        if (leaf.Length == 0) return "";

        var parts = leaf.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Equals(category, StringComparison.OrdinalIgnoreCase))
                return i + 1 < parts.Length ? parts[i + 1] : "";

        // NOT EVERY CATEGORY IS IN ITS SEQUENCES' NAMES, and Cinematics is the one that matters:
        // 523 of them fit a torso rig and not one is called anything_Cinematics_something -- they
        // are A_CIN_2450_TelRecruited_01_0010_FemHawks_rig. The rule above put all 523 in a single
        // group, which on screen is indistinguishable from no grouping at all.
        //
        // So fall back to the TREE, which for these is deep and well organised:
        //
        //     .../Cinematics/Scenes/Shipping/StoryContent/CIN_2450/CIN_2450_TelRecruited/01/FemHawks/
        //
        // THE LONGEST FOLDER BELOW THE CATEGORY, skipping take numbers. Measured over all 1,147
        // cinematics: 36 groups, biggest 96, one singleton, and every group is a real scene.
        //
        // Two narrower rules were tried and REJECTED on that measurement, both of which look
        // right until they are counted:
        //
        //   deepest folder            -> "FemHawks", 463 in one group. The character, not the scene.
        //   longest folder THE NAME   -> 63 groups, but a leftover "FemHawks" of 59 and ~30 groups
        //   CARRIES                      called "0010", "0180", "0300". A file may ABBREVIATE its
        //                                scene (A_CIN_4400_luunataIn__02 under
        //                                CIN_4400_LuunataInfiltration), so requiring the folder to
        //                                appear in the name drops exactly those, and lets a
        //                                four-digit take folder win because the shot number IS in
        //                                the name.
        //
        // Scoping BELOW the category keeps "StoryContent" and "Shipping" out of it without naming
        // them, and a take folder is all digits, which a scene never is.
        var folders = assetPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var from = 0;
        for (var i = 0; i < folders.Length - 1; i++)
            if (folders[i].Equals(category, StringComparison.OrdinalIgnoreCase)) { from = i + 1; break; }

        var best = "";
        for (var i = from; i < folders.Length - 1; i++)
            if (folders[i].Length > best.Length && !folders[i].All(char.IsDigit))
                best = folders[i];
        return best;
    }

    public IReadOnlyList<AnimEntry> ForSkeleton(string? skeletonPath) =>
        skeletonPath is not null && _bySkeleton.TryGetValue(skeletonPath, out var list)
            ? list
            : Array.Empty<AnimEntry>();

    /// <summary>Populate from a cached list, skipping the expensive scan.</summary>
    public void LoadFrom(IReadOnlyList<AnimEntry> entries)
    {
        _bySkeleton.Clear();
        _all.Clear();
        _all.AddRange(entries);
        foreach (var e in entries)
        {
            if (!_bySkeleton.TryGetValue(e.SkeletonPath, out var list))
                _bySkeleton[e.SkeletonPath] = list = new List<AnimEntry>();
            list.Add(e);
        }
        IsBuilt = true;
    }

    public void Build(IFileProvider provider, AssetIndex index, Action<string>? log = null)
    {
        _bySkeleton.Clear();
        _all.Clear();
        Skipped = 0;

        var candidates = index.Root.DescendantFiles()
            .Where(f => f.Kind == AssetKind.Animation && f.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var node in candidates)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(node.Name);
                if (AssetLoader.Load<UAnimSequence>(provider, node.FullPath) is not { } seq)
                { Skipped++; continue; }

                var skel = seq.Skeleton?.ResolvedObject?.GetPathName() ?? "";
                var entry = new AnimEntry(node.FullPath, name, seq.NumFrames,
                    seq.NumFrames > 0 && seq.SequenceLength > 0 ? seq.NumFrames / seq.SequenceLength : 30f,
                    skel);

                _all.Add(entry);
                if (!_bySkeleton.TryGetValue(skel, out var list))
                    _bySkeleton[skel] = list = new List<AnimEntry>();
                list.Add(entry);
            }
            catch { Skipped++; }
        }

        foreach (var list in _bySkeleton.Values)
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        IsBuilt = true;
        log?.Invoke($"Animation library: {_all.Count:N0} sequences across {_bySkeleton.Count} skeleton(s)" +
                    (Skipped > 0 ? $", {Skipped} skipped" : ""));
    }
}

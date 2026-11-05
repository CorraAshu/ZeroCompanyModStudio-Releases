using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.GameplayTags;
using System.Numerics;
using CUE4Parse.UE4.Objects.UObject;
using UEToolkit.Core;

namespace ZeroCompanyLevelEditor;

public sealed record MapEntry(string Id, string Group, string Folder, string RootPath, List<string> Sublevels);

/// <summary>One property of one export, as the panel shows it. Top level so XAML can name it.</summary>
public sealed record PropRow(string Name, string Value, string Kind);

/// <summary>
/// One sublevel, by BOTH of its names.
///
/// <paramref name="Path"/> is the container path the index uses
/// (`SWZeroCompany/Content/Game/Maps/.../X_Gameplay.umap`) and <paramref name="Package"/> is the
/// package name the ENGINE uses (`/Game/Game/Maps/.../X_Gameplay`). A mod override is addressed
/// by the second: the build resolves it with `PackageId.Of(name)` and derives the container path
/// back from it. Writing the container path into a project produces an override the build cannot
/// find, which is exactly the mistake this pair exists to stop.
/// </summary>
public sealed record SublevelEntry(MapEntry Map, string Path, string Package, string Name, bool IsLevelInstance);

/// <summary>
/// READING A LEVEL. No UI in here, and no writing either -- this whole file is read-only by
/// design, because the first build has to be incapable of damaging a save.
/// </summary>
public static class LevelReader
{
    // ---------------------------------------------------------------- maps

    private static readonly Dictionary<string, string> GroupNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Skirmish"] = "Skirmish",
        ["StoryMissions"] = "Story Missions",
        ["HeroMissions"] = "Hero Missions",
    };

    /// <summary>
    /// Every level in the game, from the INDEX alone -- no package is opened, so this is
    /// instant. A map is a folder holding a `*_Root.umap`; its sublevels are the other .umap
    /// files beside it plus anything under LevelInstances/.
    /// </summary>
    public static List<MapEntry> Maps(AssetIndex index)
    {
        var umaps = index.Root.DescendantFiles()
            .Where(f => f.FullPath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullPath)
            .ToList();

        var byFolder = umaps.GroupBy(Folder, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var maps = new List<MapEntry>();
        foreach (var root in umaps.Where(p => Leaf(p).EndsWith("_Root.umap", StringComparison.OrdinalIgnoreCase)))
        {
            var id = Leaf(root)[..^"_Root.umap".Length];
            var folder = Folder(root);

            var siblings = byFolder.TryGetValue(folder, out var s) ? s : new List<string> { root };
            var instances = byFolder.TryGetValue(folder + "/LevelInstances", out var li) ? li : new List<string>();

            var subs = siblings.OrderBy(p => SublevelOrder(p, id)).ThenBy(Leaf, StringComparer.OrdinalIgnoreCase)
                               .Concat(instances.OrderBy(Leaf, StringComparer.OrdinalIgnoreCase))
                               .ToList();

            maps.Add(new MapEntry(id, GroupOf(root), folder, root, subs));
        }

        return maps.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GroupOf(string path)
    {
        var i = path.IndexOf("/Maps/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "Other";
        var parts = path[(i + 6)..].Split('/');
        return parts.Length >= 2 && GroupNames.TryGetValue(parts[0], out var name) ? name : "Other";
    }

    /// <summary>_Root first, then the sublevels in the order a designer thinks about them.</summary>
    private static int SublevelOrder(string path, string mapId)
    {
        var leaf = Leaf(path);
        var tail = leaf.StartsWith(mapId, StringComparison.OrdinalIgnoreCase)
            ? leaf[mapId.Length..].TrimStart('_')
            : leaf;
        return tail.ToLowerInvariant() switch
        {
            "root.umap" => 0,
            "gameplay.umap" => 1,
            "environment.umap" => 2,
            "fx.umap" => 3,
            "lighting.umap" => 4,
            "audio.umap" => 5,
            _ => 6,
        };
    }

    public static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string Folder(string path) => path[..path.LastIndexOf('/')];
    /// <summary>The engine's name for a level package, from the index's container path.</summary>
    public static string PackageNameOf(string containerPath) =>
        PackageId.PackageNameFor(containerPath, "SWZeroCompany") ?? containerPath;

    public static string Bare(string path) => Leaf(path) is var l && l.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
        ? l[..^5] : l;

    // ---------------------------------------------------------------- exports

    /// <summary>
    /// Everything in one sublevel. `AssetLoader.Read` is the toolkit's global package gate --
    /// CUE4Parse is not safe to call from two threads at once, and this app reads from a
    /// background thread while the UI runs.
    /// </summary>
    public static UObject[] Read(IFileProvider provider, string umapPath) =>
        AssetLoader.Read(() => provider.LoadPackage(AssetLoader.PackagePath(umapPath)).GetExports().ToArray());

    // ---------------------------------------------------------------- layers

    public const string LayerCover = "Cover";
    public const string LayerSpawns = "Spawns";
    public const string LayerVolumes = "Volumes";
    public const string LayerObjectives = "Objectives";
    public const string LayerEncounter = "Encounter";
    public const string LayerProps = "Props";
    public const string LayerScript = "Script + components";

    public static readonly string[] LayerOrder =
        { LayerCover, LayerSpawns, LayerVolumes, LayerObjectives, LayerEncounter, LayerProps, LayerScript };

    /// <summary>
    /// Which layer a class belongs to. ORDER MATTERS: `SpawnLocationVolume` is a spawn, not a
    /// volume, and `BrunoNoCoverVolume` is a volume rather than cover despite the name -- so
    /// cover is an exact match and spawns are tested before volumes.
    /// </summary>
    public static string LayerOf(string cls)
    {
        if (cls.Equals("TacticalPoint", StringComparison.OrdinalIgnoreCase)) return LayerCover;
        if (cls.Contains("Spawn", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("Reinforcement", StringComparison.OrdinalIgnoreCase)) return LayerSpawns;
        if (cls.Contains("Objective", StringComparison.OrdinalIgnoreCase)) return LayerObjectives;
        if (cls.Contains("EncounterActor", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("MissionActor", StringComparison.OrdinalIgnoreCase)) return LayerEncounter;
        if (cls.EndsWith("Volume", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains("Area", StringComparison.OrdinalIgnoreCase)) return LayerVolumes;
        if (cls.Contains("StaticMesh", StringComparison.OrdinalIgnoreCase)) return LayerProps;
        return LayerScript;
    }

    // ---------------------------------------------------------------- actors vs components

    /// <summary>
    /// WHAT A PERSON CALLS A THING IN A LEVEL.
    ///
    /// A sublevel's exports are mostly COMPONENTS, and their names are useless as labels: fifty
    /// `StaticMeshComponent0`, four `Root`, a dozen `SceneComponent`. The name worth showing is
    /// the ACTOR that owns them -- `BitReactorStaticMeshActor_4` -- and the MESH it wears,
    /// `SM_Hub_LockerLarge_A`, which is the only one of the three a person can actually see in
    /// the game.
    ///
    /// An actor is told from a component by the one property only actors carry: `RootComponent`.
    /// </summary>
    public static bool IsActor(UObject e) => e.Properties.Any(p => p.Name.Text == "RootComponent");

    /// <summary>
    /// DRAW THE THINGS THE GAME ITSELF WOULD NOT LOAD. Off, because the author's complaint is
    /// that the viewport shows what the game does not.
    ///
    /// Two flags, both sitting in the package, both ignored until now:
    ///   * `LevelInstance.bAutomaticallyLoad = false` -- 44 of the hub's 269 instances. The game
    ///     loads none of them on its own; something in play has to ask.
    ///   * `Actor.bHidden = true` -- 60 exports across the hub. Placed, and not rendered.
    ///
    /// This is the only part of the state problem that needs no condition evaluated, so it is
    /// worth having on its own: it is certain, and it is what the package says rather than what a
    /// name suggests. A tick box rather than a rule, for the same reason the sublevel switches
    /// are, and ON means "show me everything" rather than "show me the game".
    /// </summary>
    public static bool ShowUnloaded;

    /// <summary>What the last read skipped because the game would not have loaded it.</summary>
    public static int SkippedNotAutoLoaded;
    public static int SkippedHidden;
    public static int SkippedByFacts;
    public static int AllowedByFacts;
    public static int SkippedInvisible;

    /// <summary>
    /// Placements a prefab's own `LevelInstancePivotOffset` moved, on the last read.
    ///
    /// A fix that silently stops running looks exactly like a fix that worked, and this file has
    /// been bitten by that twice -- the socket counter dropped in the aggregation layer, and the
    /// draw counter that only ever went up. Fifty of the hub's prefabs carry a pivot and one
    /// sublevel has 4,003 placements riding on it, so if this reads zero on the hub something is
    /// wrong whatever the picture looks like.
    /// </summary>
    public static int Pivoted;

    /// <summary>
    /// How many visibility RULES were found, against how many were APPLIED.
    ///
    /// The one number that catches this whole feature being a silent no-op: the rules are keyed
    /// on the component's owner resolved through `Outer.Object`, and if that were not the very
    /// instance the placement loop holds, nothing would ever match and the viewport would look
    /// exactly as it did before. Rules found with nothing applied is the tell.
    /// </summary>
    public static int RulesSeen;

    /// <summary>Rules this reader could not evaluate. Never silent: a level full of them would
    /// otherwise look identical to a level with none.</summary>
    public static int RulesUnreadable;

    /// <summary>
    /// THE FACTS THAT GATE THE LEVEL CURRENTLY READ, and the rule each one appears in.
    ///
    /// Collected while the gates are built, so it is the facts this map actually cares about
    /// rather than the game's whole tag registry. That distinction is the difference between a
    /// panel with four switches in it and a panel with several hundred.
    ///
    /// The author's ask, in his words: "i don't want the level to load what i'm looking at in my
    /// save everytime, i would like to be able to load each variant whenever i want." So these
    /// are switches, not a snapshot.
    /// </summary>
    public static readonly SortedDictionary<string, string> FactsInLevel =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Start a fresh reading. The counters are cumulative across the recursive walk into level
    /// instances, so they have to be cleared by whoever begins a read rather than by the walk.
    /// </summary>
    public static void ResetVisibility()
    {
        SkippedNotAutoLoaded = 0;
        SkippedHidden = 0;
        SkippedByFacts = 0;
        AllowedByFacts = 0;
        SkippedInvisible = 0;
        Pivoted = 0;
        RulesSeen = 0;
        RulesUnreadable = 0;
        FactsInLevel.Clear();
    }

    /// <summary>
    /// THE GAME FACTS TREATED AS SET. Empty is a new game, which is a real state and the honest
    /// default: nothing here is a guess about the author's save.
    ///
    /// A `GameFactVisibilityComponent` states, for its own actor, the condition under which that
    /// actor is in the level -- `ALL( ALL( Facts.Hub.HS_05_000_a ), NONE( Facts.Hub.HS_05_005_a ) )`
    /// on a level instance, or a crew member's belongings appearing once their operation is
    /// complete and they are neither dismissed nor dead. 57 facts gate the hub.
    ///
    /// This replaces the name heuristic that picked `_ON` over `_OFF` and the shortest name of a
    /// shared prefix. That guess was the author's complaint: "i don't want them to just be
    /// hidden, i want it all to be as identical as i can see it ingame".
    /// </summary>
    public static readonly HashSet<string> Facts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WHICH ACTORS THE GAME'S OWN RULES ADMIT, for <see cref="Facts"/>.
    ///
    /// Keyed on the component's OWNER, because every one of the 45 conditions in the hub leaves
    /// `ActorsToHide` empty and so speaks for the actor it hangs off. Measured before it was
    /// written: a condition evaluated perfectly and applied to the wrong actor would look exactly
    /// like a working feature.
    ///
    /// Both forms are honoured. The compiled `TagQuery` is evaluated by CUE4Parse's own token
    /// stream reader, which `--factcheck` races against the `AutoDescription` UE serialises
    /// beside it -- 260 cases over every combination of every query's facts in the hub, all
    /// agreeing. That is why this is trusted at all.
    /// </summary>
    private static Dictionary<UObject, bool> Gates(UObject[] exports, out int seen)
    {
        seen = 0;
        var gates = new Dictionary<UObject, bool>(
            (IEqualityComparer<UObject>) ReferenceEqualityComparer.Instance);

        var held = new FGameplayTagContainer(
            Facts.Select(f => new FGameplayTag(new FName(f))).ToArray());

        foreach (var c in exports)
        {
            if (c.ExportType is not { } type ||
                !type.Contains("GameFactVisibility", StringComparison.OrdinalIgnoreCase)) continue;
            // THE OWNER AS AN EXPORT, NOT AS A NAME. `Outer` is a ResolvedObject; its `Object`
            // is the lazy that backs the package's own export array, so this is the very
            // instance the placement loop will hold. Matching on the NAME instead is how this
            // codebase once reported a 180-degree error on a correct scene, because UE reuses
            // component names across actors freely.
            if (c.Outer?.Object?.Value is not { } owner) continue;

            foreach (var p in c.Properties)
            {
                if (p.Name.Text != "VisibilityFactRequirements") continue;
                if (Unwrap(p.Tag?.GenericValue) is not FStructFallback sf) continue;

                bool pass;
                try
                {
                    if (sf.Properties.FirstOrDefault(x => x.Name.Text == "TagQuery") is { } q)
                    {
                        if (Unwrap(q.Tag?.GenericValue) is not FStructFallback qf) continue;
                        var query = new FGameplayTagQuery(qf);
                        if (query.QueryTokenStream is null || query.QueryTokenStream.Length == 0) continue;
                        pass = query.Matches(held);
                    }
                    else
                    {
                        pass = held.HasAll(Tags(sf, "RequireTags")) &&
                               !held.HasAny(Tags(sf, "IgnoreTags"));
                    }
                }
                catch
                {
                    // AN UNREADABLE RULE MUST NOT DELETE THE LEVEL, and this is not theoretical.
                    // The first version left this branch uncaught and a null tag array took the
                    // whole read down; the author got a hub stuck on "working..." with four
                    // sublevels in it. Counted rather than swallowed, so a level full of rules
                    // this cannot read says so instead of quietly drawing everything.
                    RulesUnreadable++;
                    continue;
                }

                seen++;
                // REMEMBER WHAT THIS MAP IS GATED ON, with UE's own wording where it has one.
                // AutoDescription is the compiled query rendered in words by the engine, which
                // is a better label than anything this could compose from the token stream.
                var label = Describe(sf);
                foreach (var f in FactsOf(sf))
                    FactsInLevel[f] = label;

                // SEVERAL RULES CAN GUARD ONE ACTOR, and every one of them has to be satisfied.
                gates[owner] = gates.TryGetValue(owner, out var had) ? had && pass : pass;
            }
        }
        return gates;
    }

    /// <summary>A StructProperty wraps an FScriptStruct wrapping the fallback. Unwrap BOTH.</summary>
    private static object? Unwrap(object? v) => v is FScriptStruct fs ? fs.StructType : v;

    /// <summary>
    /// A tag container that is safe to ASK THINGS OF.
    ///
    /// `new FGameplayTagContainer()` on a struct leaves its readonly GameplayTags array NULL --
    /// only the array constructor fills it -- and `IsEmpty()` dereferences that array. So a rule
    /// that writes RequireTags and no IgnoreTags handed HasAll a null and threw, which took down
    /// the whole level read. HUB_Hangar survived because it has no rules at all;
    /// HUB_Environment_Pit has them, and that is exactly where the hub load died.
    ///
    /// Never construct one of these with `new()` again. Always an explicit empty array.
    /// </summary>
    private static FGameplayTagContainer Tags(FStructFallback sf, string name)
    {
        var c = sf.GetOrDefault(name, new FGameplayTagContainer([]));
        return c.GameplayTags is null ? new FGameplayTagContainer([]) : c;
    }

    /// <summary>The rule in words: UE's own AutoDescription, or the require/ignore pair.</summary>
    private static string Describe(FStructFallback req)
    {
        if (req.Properties.FirstOrDefault(x => x.Name.Text == "TagQuery") is { } q &&
            Unwrap(q.Tag?.GenericValue) is FStructFallback qf &&
            qf.GetOrDefault<string>("AutoDescription") is { Length: > 0 } auto)
            return auto.Trim();

        // THROUGH Tags, LIKE EVERY OTHER READ OF ONE. This function was the second site of the
        // same null and it is the reason the fix did not take the first time: I wrote "never
        // construct one with new() again" in a comment and left this line doing it, four lines
        // below. A rule written down once is worth less than a helper that cannot be bypassed.
        var need = Tags(req, "RequireTags");
        var deny = Tags(req, "IgnoreTags");
        var parts = new List<string>();
        if (need.GameplayTags.Length > 0) parts.Add($"ALL( {need} )");
        if (deny.GameplayTags.Length > 0) parts.Add($"NONE( {deny} )");
        return parts.Count > 0 ? string.Join(" and ", parts) : "(no requirement)";
    }

    /// <summary>Every fact named by a requirement, in either of the two forms it is written.</summary>
    private static IEnumerable<string> FactsOf(FStructFallback req)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(req, found, 0);
        return found;

        static void Collect(object? v, ISet<string> into, int depth)
        {
            if (depth > 8) return;
            switch (Unwrap(v))
            {
                case FGameplayTagContainer c:
                    // The array can be null on a container that wrote no tags.
                    foreach (var g in c.GameplayTags ?? []) into.Add(g.TagName.Text);
                    return;
                case FStructFallback sf:
                    foreach (var p in sf.Properties) Collect(p.Tag?.GenericValue, into, depth + 1);
                    return;
                case UScriptArray a:
                    foreach (var el in a.Properties) Collect(el?.GenericValue, into, depth + 1);
                    return;
                case FName n when n.Text.StartsWith("Facts.", StringComparison.OrdinalIgnoreCase):
                    into.Add(n.Text);
                    return;
            }
        }
    }

    /// <summary>
    /// The actor says it is hidden. `bHidden` lives on the ACTOR, not the component, so this has
    /// to be asked of the owner a placement was attributed to; asking the mesh component finds
    /// nothing and the flag reads as absent everywhere.
    ///
    /// Through the archetype, because a cooked Blueprint actor writes down only what it
    /// overrides, exactly as its mesh and its transform do.
    /// </summary>
    private static bool SaysHidden(UObject? actor)
    {
        if (actor is null) return false;

        // ITS OWN PROPERTY FIRST, and that costs nothing: an actor that overrides bHidden writes
        // it down in its own export.
        if (actor.Properties.Any(p => p.Name.Text == "bHidden"))
            return actor.GetOrDefault("bHidden", false);

        // Otherwise the ARCHETYPE decides, and the answer is a fact about the CLASS rather than
        // about this actor -- so it is asked once per class and remembered.
        //
        // MEASURED, not assumed: Holder walks Template.Load() up to eight deep, and calling it
        // per placement turned a 1,245 ms sublevel open into minutes of nothing. A correctness
        // fix that makes the tool unusable is not a fix, and a hot loop is the one place a
        // convenience helper must not go.
        var cls = actor.ExportType;
        if (string.IsNullOrEmpty(cls)) return false;
        if (HiddenByClass.TryGetValue(cls, out var known)) return known;

        var v = LevelExport.Holder(actor, "bHidden")?.GetOrDefault("bHidden", false) ?? false;
        HiddenByClass[cls] = v;
        return v;
    }

    /// <summary>Whether a CLASS hides itself by default, asked of the archetype once.</summary>
    private static readonly Dictionary<string, bool> HiddenByClass = new(StringComparer.Ordinal);

    /// <summary>Whether a CLASS auto-loads by default, asked of the archetype once.</summary>
    private static readonly Dictionary<string, bool> AutoLoadByClass = new(StringComparer.Ordinal);

    /// <summary>Whether a CLASS hides its own component by default, asked of the archetype once.</summary>
    private static readonly Dictionary<string, bool> InvisibleByClass = new(StringComparer.Ordinal);

    /// <summary>
    /// THE COMPONENT HIDES ITSELF, which is not the same as its actor hiding it.
    ///
    /// UE renders a primitive only when the component is visible AND not hidden in game AND its
    /// actor is not hidden. We honoured the actor and neither of the other two, so 525 components
    /// across the hub were drawn that the game does not draw:
    ///
    ///     bVisible        477 written, and all 477 are FALSE
    ///     bHiddenInGame    52 written, 48 of them TRUE
    ///
    /// bVisible is 477 of 477 because UE only writes a property when it differs from the default,
    /// and the default is visible. The polarity had to be measured rather than assumed: `bHidden`
    /// is written 92 times and only 60 of those hide anything, so counting writes would have
    /// overstated that one by half.
    ///
    /// NO PROPAGATION TO CHILDREN, deliberately. UE propagates visibility when SetVisibility is
    /// CALLED, not as a standing rule, so a hidden parent does not imply a hidden child in the
    /// serialised state. Walking the chain here would hide geometry the game shows, which is the
    /// worse error of the two: a prop wrongly present can be seen and reported, and one wrongly
    /// absent cannot.
    ///
    /// Own property first, then the archetype once per CLASS - the same shape as SaysHidden, and
    /// for the same reason: Holder walks Template.Load() eight deep and this is a hot loop.
    /// </summary>
    private static bool ComponentHidden(UObject e)
    {
        if (e.Properties.Any(p => p.Name.Text == "bVisible") && !e.GetOrDefault("bVisible", true))
            return true;
        if (e.Properties.Any(p => p.Name.Text == "bHiddenInGame") &&
            e.GetOrDefault("bHiddenInGame", false))
            return true;

        var cls = e.ExportType;
        if (string.IsNullOrEmpty(cls)) return false;
        if (InvisibleByClass.TryGetValue(cls, out var known)) return known;

        var v = !(LevelExport.Holder(e, "bVisible")?.GetOrDefault("bVisible", true) ?? true) ||
                (LevelExport.Holder(e, "bHiddenInGame")?.GetOrDefault("bHiddenInGame", false) ?? false);
        InvisibleByClass[cls] = v;
        return v;
    }

    /// <summary>
    /// Whether this level instance loads on its own.
    ///
    /// THE SAME SHAPE AS <see cref="SaysHidden"/>, AND THE SAME FAULT TWICE. The first version
    /// called LevelExport.Holder straight from the loop, and Holder walks Template.Load() up to
    /// eight deep. LevelInstances recurses into every nested instance, so the cost compounded and
    /// a sublevel that opened in about a second stopped finishing inside a minute.
    ///
    /// I had already fixed exactly this in SaysHidden an hour earlier and did not think to look
    /// for a second call site. When a convenience helper turns out to be too expensive for a hot
    /// loop, the next move is to grep for its other callers, not to fix the one that hurt.
    ///
    /// Defaults to TRUE when nothing writes it down, because an instance that says nothing is an
    /// ordinary one that loads.
    /// </summary>
    private static bool AutoLoads(UObject e)
    {
        if (e.Properties.Any(p => p.Name.Text == "bAutomaticallyLoad"))
            return e.GetOrDefault("bAutomaticallyLoad", true);

        var cls = e.ExportType;
        if (string.IsNullOrEmpty(cls)) return true;
        if (AutoLoadByClass.TryGetValue(cls, out var known)) return known;

        var v = LevelExport.Holder(e, "bAutomaticallyLoad")?
                    .GetOrDefault("bAutomaticallyLoad", true) ?? true;
        AutoLoadByClass[cls] = v;
        return v;
    }

    /// <summary>Every export that some actor owns, so the tree can fold them into it.</summary>
    public static HashSet<UObject> OwnedComponents(UObject[] all)
    {
        var owned = new HashSet<UObject>(ReferenceEqualityComparer.Instance as IEqualityComparer<UObject>);
        foreach (var e in all)
        {
            if (Resolve(e.GetOrDefault<FPackageIndex?>("RootComponent", null), all) is { } root)
                owned.Add(root);
            // Attached things belong to whatever they hang off, wherever that is.
            if (e.GetOrDefault<FPackageIndex?>("AttachParent", null) is { IsNull: false })
                owned.Add(e);
        }
        return owned;
    }

    /// <summary>The mesh an actor wears, looked through its root component.</summary>
    public static string MeshOf(UObject e, UObject[] all)
    {
        var root = Resolve(e.GetOrDefault<FPackageIndex?>("RootComponent", null), all) ?? e;
        return root.GetOrDefault<FPackageIndex?>("StaticMesh", null)?.ResolvedObject?.Name.Text
               ?? root.GetOrDefault<FPackageIndex?>("SkeletalMesh", null)?.ResolvedObject?.Name.Text
               ?? "";
    }

    /// <summary>
    /// The layer an ACTOR belongs to. Its own class first, and its root component's class when
    /// that says nothing -- a `BP_UNI_CrateR8B_DS_C` is a prop, and only its component says so.
    /// </summary>
    public static string LayerOfActor(UObject e, UObject[] all)
    {
        var byClass = LayerOf(e.ExportType ?? "?");
        if (byClass != LayerScript) return byClass;
        var root = Resolve(e.GetOrDefault<FPackageIndex?>("RootComponent", null), all);
        return root is null ? LayerScript : LayerOf(root.ExportType ?? "?");
    }

    // ---------------------------------------------------------------- what a map really is

    /// <summary>
    /// THE SUBLEVELS A MAP ACTUALLY LOADS, which is not the files sitting beside it.
    ///
    /// Measured on SK_Anaxes_045_Root: ten LevelStreamingAlwaysLoaded actors, and they name
    /// SK_Anaxes_040's terrain and organic environment, a shared Outlaw Fort from Maps/Skirmish/
    /// Forts, and only three of 045's own. That is why 045's own _Environment looks nearly empty
    /// in the viewport -- its ground belongs to another map, and a folder listing cannot know it.
    ///
    /// No LevelTransform is serialised on any of them, so the contents are already in world
    /// space and merging them needs no offset. If one ever carries a transform this will place it
    /// wrongly and visibly, which is the right way round.
    /// </summary>
    public static List<string> StreamedPackages(UObject[] rootExports)
    {
        var found = new List<string>();
        foreach (var e in rootExports)
        {
            if (!(e.ExportType ?? "").Contains("LevelStreaming", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var prop in e.Properties)
            {
                if (prop.Name.Text != "WorldAsset") continue;
                var text = prop.Tag?.GenericValue?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                // A soft object path reads "/Game/X/Y.Y"; the package is everything before the dot.
                var dot = text.LastIndexOf('.');
                var package = dot > 0 ? text[..dot] : text;
                if (package.StartsWith('/') && !found.Contains(package, StringComparer.OrdinalIgnoreCase))
                    found.Add(package);
            }
        }
        return found;
    }

    /// <summary>A package name back to the container path the index holds, as a level.</summary>
    public static string ContainerOf(string packageName)
    {
        var trimmed = packageName.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var root = slash < 0 ? trimmed : trimmed[..slash];
        var rest = slash < 0 ? "" : trimmed[(slash + 1)..];
        return $"{(root.Equals("Game", StringComparison.OrdinalIgnoreCase) ? "SWZeroCompany" : root)}/Content/{rest}.umap";
    }

    // ---------------------------------------------------------------- the scene

    /// <summary>
    /// One thing to draw, and everything needed to act on it once it is clicked.
    ///
    /// <paramref name="Owner"/> and <paramref name="Scope"/> are the point: a placement is drawn
    /// from a COMPONENT, but the thing a person selects and moves is the ACTOR that owns it, and
    /// reading that actor's properties needs the export array of the sublevel it came from.
    ///
    /// <paramref name="Package"/> matters because a map is eleven sublevels merged into one
    /// scene. Without it a move picked in the viewport would be recorded against whichever
    /// sublevel happened to be open in the tree -- an override aimed at the wrong package, which
    /// the build would accept and the game would ignore.
    /// </summary>
    public sealed record Placement(string MeshPath, Matrix4x4 Model, string Actor, Vector3 At,
                                   UObject Owner, UObject[] Scope, string Package, string Sublevel)
    {
        /// <summary>
        /// An UNSAVED move, in UE space, applied on top of the shipped transform.
        ///
        /// Kept on the placement rather than baked into Model so the original is still there to
        /// compare against -- and so reverting is subtraction rather than a re-read of the
        /// package.
        /// </summary>
        public Vector3 Offset { get; set; }

        /// <summary>
        /// An UNSAVED rotation or scale, as a world-space transform about <see cref="Pivot"/>.
        ///
        /// Separate from the offset because it applies on the other side of the model matrix: a
        /// rotation has to happen about the actor's own origin, and a translation does not care.
        /// </summary>
        public Matrix4x4 Warp { get; set; } = Matrix4x4.Identity;

        /// <summary>
        /// The actor's ROOT component in world space -- what a rotation or a scale turns about.
        ///
        /// Not this placement's own position: an actor's mesh components hang off its root, and
        /// turning each about its own centre would pull the actor apart instead of rotating it.
        /// </summary>
        /// <summary>
        /// THE MATERIALS THIS PLACEMENT USES INSTEAD OF THE MESH'S OWN, by slot index.
        ///
        /// `OverrideMaterials` belongs to the COMPONENT, so the same crate mesh is painted
        /// several different ways in one level. The hub writes it 3,204 times across 82 of its
        /// 118 levels and the viewport read none of them, which is the author's "weird textures
        /// being applied to crates and tons of stuff that doesn't get textures" -- a report
        /// dismissed as a base-colour-only viewport until --levelaudit counted it.
        ///
        /// Null entries are UE for "keep the mesh's own for that slot" and are PRESERVED, because
        /// the array is indexed by slot and closing the gaps would shift every material after it
        /// onto the wrong part of the model.
        /// </summary>
        public string?[]? Materials { get; init; }

        public Vector3 Pivot { get; init; }

        /// <summary>
        /// An UNSAVED mesh swap: what this placement draws INSTEAD, or "" for nothing at all.
        ///
        /// Laid over <see cref="MeshPath"/> rather than replacing it, for the same reason
        /// <see cref="Offset"/> is: the shipped value has to still be there to write a `was`
        /// against and to revert to, and reverting should be removing an override rather than
        /// re-reading the package. Null means untouched.
        /// </summary>
        public string? Draws { get; set; }

        /// <summary>
        /// WHAT TO ACTUALLY DRAW -- null when this placement has been removed.
        ///
        /// Every drawing site reads this and not <see cref="MeshPath"/>, so the picture is a VIEW
        /// of the pending edits the same way the gizmo's offset is. A site that still read
        /// MeshPath would keep drawing a chair somebody had deleted.
        /// </summary>
        public string? Shown => Draws is null ? MeshPath : Draws.Length == 0 ? null : Draws;

        /// <summary>
        /// THE COMPONENT WHOSE TRANSFORM THIS IS -- not the actor, which usually carries none.
        ///
        /// Kept so a click can be answered with WHY the thing is there. Everything else on this
        /// record is the ANSWER composition arrived at; this is the question it was asked, and
        /// without it the readout would have to find the component again by name -- which is how
        /// the exporter's own cross-check once reported a 180-degree error on a correct scene,
        /// because UE reuses component names across actors freely.
        /// </summary>
        public UObject? Component { get; init; }

        /// <summary>
        /// WHICH ROW OF `PerInstanceSMData` THIS IS, or -1 for an ordinary placement.
        ///
        /// An instanced component draws one mesh hundreds of times and the rows are the only
        /// thing telling them apart, so "the 340th tile of the hangar deck" is a sentence the
        /// readout has to be able to say.
        /// </summary>
        public int Instance { get; init; } = -1;

        /// <summary>
        /// THE INSTANCE ROW'S OWN OFFSET, in the component's space.
        ///
        /// Carried because without it the readout does not ADD UP: the chain ends at the
        /// component's origin and the thing is drawn two thousand centimetres away, with nothing
        /// on screen accounting for the difference. A readout whose numbers do not reconcile is
        /// worse than none -- it invites exactly the hunt for a bug in the half that is right
        /// that the readout exists to prevent.
        /// </summary>
        public Vector3 InstanceAt { get; init; }

        /// <summary>
        /// THE LEVEL INSTANCES THIS WAS CARRIED THROUGH, outermost first, or null for something
        /// the open package places itself.
        ///
        /// A prop inside a prefab inside a sublevel has THREE transforms multiplied together and
        /// only one of them is in the package you are looking at. Without this the readout would
        /// confidently describe a chain that accounts for a fraction of where the thing ended up.
        /// </summary>
        public string? Through { get; init; }

        /// <summary>
        /// THE MAP SUBLEVEL THAT ULTIMATELY BROUGHT THIS IN -- the name a tick box carries.
        ///
        /// <see cref="Sublevel"/> is the package the thing actually LIVES in, which for anything
        /// inside a level instance is the prefab, not the sublevel. Hiding checked that, and the
        /// tick boxes write the map's own sublevel names, so the two could never match: on the
        /// hub, 36,047 of 77,517 placements -- 46 per cent -- carried a name no tick box could
        /// reach. He unticked the Ring of Kafrene and it loaded anyway, and the 7,952 placements
        /// of LI_Hub_Vista_Crane_A alone would have kept the backdrop on screen.
        ///
        /// THE INVARIANT: the name WRITTEN into HiddenSublevels and the name CHECKED against it
        /// must be the same key. Display can use whichever reads better; hiding cannot choose.
        /// </summary>
        public string RootSublevel { get; init; } = "";

        /// <summary>The hide key, falling back to the package's own name at the top level.</summary>
        public string HideKey => RootSublevel.Length > 0 ? RootSublevel : Sublevel;

        /// <summary>
        /// THE PENDING SPAWN THIS PLACEMENT IS A PICTURE OF -- null for everything the package
        /// actually holds.
        ///
        /// A spawn has no export yet, so it cannot be drawn the way everything else is; the
        /// preview borrows its donor's mesh, scale and rotation and stands where the spawn says.
        /// Borrowing the donor's OWNER too is what makes this field necessary: without it the
        /// derived-state passes would see the donor's address on two placements and apply the
        /// donor's pending move to the copy as well, and a click on the new actor would select
        /// the old one.
        /// </summary>
        public object? Spawned { get; init; }
    }

    /// <summary>
    /// EVERYTHING IN A SUBLEVEL THAT HAS A SHAPE, ready to draw.
    ///
    /// The transform is read off the component that carries it -- the actor has none, as
    /// everything else here has had to learn -- and composed in UE's own space. The basis change
    /// to GL is NOT applied: the renderer does that, with `RenderMesh.UeToGl`, and doing it twice
    /// would mirror the level back.
    ///
    /// PROPERTIES ARE INHERITED, AND ATTACH CHAINS ARE FOLLOWED -- the latter by the level
    /// exporter's own composition, which had already learned the four things a hand-rolled walk
    /// gets wrong. See the note inside.
    /// </summary>
    public static List<Placement> Placements(UObject[] exports, string package, string sublevel) =>
        Placements(exports, package, sublevel, null, 0, new Dictionary<string, List<Placement>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// As above, and following LEVEL INSTANCES when a provider is supplied.
    /// </summary>
    public static List<Placement> Placements(UObject[] exports, string package, string sublevel,
                                             IFileProvider? provider, int depth,
                                             Dictionary<string, List<Placement>> cache)
    {
        var refs = ReferenceEqualityComparer.Instance as IEqualityComparer<UObject>;

        // Which actor owns which ROOT component, so a click lands on the actor rather than on
        // "StaticMeshComponent0" -- the same map the tree is built from.
        var owner = new Dictionary<UObject, UObject>(refs);
        foreach (var a in exports)
            if (Resolve(a.GetOrDefault<FPackageIndex?>("RootComponent", null), exports) is { } root)
                owner.TryAdd(root, a);

        // ONE DESCRIPTION OF WHERE THINGS ARE, shared with the level exporter.
        //
        // This was a second, worse implementation for about an hour: a matrix multiply up the
        // AttachParent chain. It drew far more of the level than before and still put things in
        // the wrong place, because composing an attach chain correctly needs four things it did
        // not do, all of which LevelExport.ComposeAll had already been taught:
        //
        //   * bAbsoluteLocation / bAbsoluteRotation / bAbsoluteScale, PER CHANNEL -- a component
        //     can inherit its parent's position and keep world rotation.
        //   * UE's own FTransform composition. A plain matrix multiply SHEARS a child under a
        //     non-uniformly scaled parent; UE does not.
        //   * Parents resolved BY EXPORT INDEX. Resolving by name is how the exporter's own
        //     cross-check once reported a 180-degree error on a correct scene, because UE reuses
        //     component names across actors freely -- and `Resolve` here falls back to a name.
        //   * A cycle guard, because cooked data contains cycles that valid data cannot.
        //
        // What the viewport adds is `inherit`: reading each local through the archetype chain,
        // because half the components in a cooked map carry no transform of their own.
        var w = LevelExport.Compose(exports, inherit: true);

        // WHAT THE GAME'S OWN RULES ADMIT, for the facts currently treated as set. Built once per
        // package rather than per placement, because it walks every export looking for the
        // visibility components.
        var rules = 0;
        var gates = ShowUnloaded
            ? new Dictionary<UObject, bool>((IEqualityComparer<UObject>) ReferenceEqualityComparer.Instance)
            : Gates(exports, out rules);
        RulesSeen += rules;
        int hlod = 0, instanced = 0;

        var made = new List<Placement>();
        for (var i = 0; i < exports.Length; i++)
        {
            var e = exports[i];

            // HLOD PROXIES ARE NOT LEVEL GEOMETRY, and drawing them is the reason a map can look
            // like it has floating rubble in it. UE generates them to draw distant scenery
            // cheaply: every one is a MERGED, reduced stand-in for meshes that are also in the
            // level at full detail, so including them draws the scene twice -- the low-detail
            // copy sitting over and around the real thing, which reads as art rather than as
            // duplication. The level exporter has excluded them since it was written; the
            // viewport never learned to.
            //
            // ASK THE ACTOR, NOT JUST THE COMPONENT CLASS. UE also emits proxies as an ordinary
            // `StaticMeshComponent` owned by a generated `HLOD_A_<hex>` actor, and none of those
            // match on the class -- they are the harder half to notice, because they do not pile
            // up anywhere obvious.
            var ownerName = e.Outer?.Name.Text ?? "";
            if (e.ExportType?.Contains("HLOD", StringComparison.OrdinalIgnoreCase) == true ||
                ownerName.Contains("HLOD", StringComparison.OrdinalIgnoreCase))
            { hlod++; continue; }

            // ONE MESH LOOKUP, SHARED WITH THE EXPORTER. This was a second copy of it -- the
            // typed accessor plus an archetype walk for `StaticMesh` -- and a second copy of a
            // lookup is how the viewport and the exporter drifted apart before: two LOD rules,
            // two HLOD tests, two atlas keys. It also meant the exporter learning to resolve a
            // SKELETAL mesh left the viewport still blind to one.
            //
            // THE MESH MAY BE ON THE ARCHETYPE, and asking the export alone is what hid every
            // Blueprint prop in the game. LevelExport.MeshPathOf does that walk for static and
            // skinned meshes both.
            var path = LevelExport.MeshPathOf(e);
            if (string.IsNullOrEmpty(path)) continue;

            // THE COMPONENT HIDES ITSELF. Checked on the component rather than on the actor,
            // because that is where bVisible and bHiddenInGame live.
            if (!ShowUnloaded && ComponentHidden(e)) { SkippedInvisible++; continue; }

            // THE PLACEMENT'S OWN MATERIALS, read once for both emitters below. Through the
            // archetype, like the mesh and the transform.
            var overrides = LevelExport.OverrideMaterialsOf(e);

            var q = w.Rot[i];
            var t = w.Loc[i];
            var k = w.Scale[i];

            // The actor is whatever owns the TOP of this component's attach chain -- a child
            // actor's mesh hangs several components below the thing you would click on.
            var top = i;
            for (var d = 0; d < 32 && w.Parent[top] >= 0; d++) top = w.Parent[top];
            var actor = owner.GetValueOrDefault(exports[top], owner.GetValueOrDefault(e, e));
            var name = actor.Name ?? e.Name ?? "?";

            // THE GAME'S OWN CONDITION FOR THIS ACTOR, where it states one. A rule beats
            // bHidden either way: an actor the rules admit is in the level, and one they exclude
            // is not, whatever else it says about itself.
            if (gates.TryGetValue(actor, out var admitted))
            {
                if (!admitted) { SkippedByFacts++; continue; }
                AllowedByFacts++;
            }
            // THE ACTOR SAYS IT IS HIDDEN, and the game honours that. Checked here rather than
            // at the top of the loop because `actor` is only known once the attach chain has been
            // walked to its top -- a child actor's mesh hangs several components below the thing
            // whose flag matters.
            else if (!ShowUnloaded && SaysHidden(actor)) { SkippedHidden++; continue; }

            // AN INSTANCED COMPONENT DRAWS ITS MESH MANY TIMES, and its own transform is
            // frequently the identity -- so reading it as one placement is both a massive
            // undercount and a wrong position. This is every scrap of foliage in the game.
            //
            // `PerInstanceSMData` is a TYPED FIELD that CUE4Parse deserialises, NOT a UE
            // property: `GetOrDefault<...>("PerInstanceSMData")` finds nothing and reports zero
            // instances for a component holding thousands, which is exactly what the first
            // coverage report said.
            //
            // AND IT MAY BE ON THE ARCHETYPE. A cooked ISM that sets `bInheritPerInstanceData`
            // serialises none of its own and inherits the Blueprint component template's, so the
            // export in the level holds ZERO instances and UE rebuilds them on spawn. Asking the
            // export alone drew the hangar's 51 floor-group actors as 51 single tiles stacked on
            // their own origins -- the author's "the hanger floors are all misaligned" and "there
            // is no walkway under the boxes". See LevelExport.InstancesOf.
            if (LevelExport.InstancesOf(e) is { Length: > 0 } data)
            {
                instanced++;
                var row32 = 0;
                foreach (var row in data)
                {
                    // Instance transforms are relative to the component, so they compose exactly
                    // the way an attachment does.
                    var it = row.TransformData;
                    var ir = q * it.Rotation;
                    var isc = k * it.Scale3D;
                    var il = q.RotateVector(k * it.Translation) + t;
                    made.Add(new Placement(path, Model(il, ir, isc), name,
                                           new Vector3((float) il.X, (float) il.Y, (float) il.Z),
                                           actor, exports, package, sublevel)
                    {
                        Pivot = PivotOf(w, top), Materials = overrides, Component = e, Instance = row32,
                        InstanceAt = new Vector3((float) it.Translation.X, (float) it.Translation.Y,
                                                 (float) it.Translation.Z),
                        RootSublevel = sublevel,
                    });
                    row32++;
                }
                continue;
            }

            made.Add(new Placement(path, Model(t, q, k), name,
                                   new Vector3((float) t.X, (float) t.Y, (float) t.Z),
                                   actor, exports, package, sublevel)
            { Pivot = PivotOf(w, top), Materials = overrides, Component = e, RootSublevel = sublevel });
        }

        _ = hlod; _ = instanced;      // counted properly by the coverage audit, across the whole map

        // ONE ACTOR DRAWING ONE MESH IN ONE PLACE IS ONE PLACEMENT.
        //
        // This loop asks EVERY export what it draws, and for a cooked Blueprint prop two of them
        // answer the same way: the component carries the mesh, and the ACTOR resolves the very
        // same mesh through its archetype -- which is the lookup that made Blueprint props appear
        // at all. So BP_UNI_Wares_Bowl_Ragtag_B_C_3 emitted its bowl twice, at one transform.
        //
        // Two identical surfaces in one place do not both survive a depth test, and which one wins
        // is not stable -- which is what the author was watching: "meshes keep disappearing and
        // reappearing and i'm not even moving the camera". 369 actors in the hub were doing it.
        //
        // The position is part of the key on purpose: an instanced component draws one mesh for
        // one actor hundreds of times, and every one of those is a real placement somewhere else.
        if (provider is not null && depth < MaxLevelInstanceDepth)
            made.AddRange(LevelInstances(exports, w, provider, depth, cache, gates, sublevel));

        // AFTER the level instances, not before, so it covers them too: a prefab read once and
        // dropped twice at the same transform hands back the SAME export objects, so its contents
        // key identically and collapse the same way an actor's double emission does.
        // THE OWNER IS NOT PART OF THE KEY, and leaving it in was the second half of this bug.
        //
        // The first half was one ACTOR emitting its mesh twice, which this caught. The rest is two
        // DIFFERENT actors drawing one mesh at one transform: `BP_UNI_Greeble_ModularTile_Solo_B_C_0`
        // and `..._C_4` hand back the same instance set at the same places, 154 times over in
        // HUB_Hangar alone. Different owners, so the key let both through -- and two coincident
        // surfaces in a flat-shaded view do not average out, they FIGHT, and which one wins is not
        // stable under a camera move. His words, now twice: "meshes keep rendering and
        // de-rendering".
        //
        // THE WHOLE TRANSFORM, not just the position. A modular kit really does put two copies of
        // one panel at one origin facing opposite ways, and a position-only key would drop a
        // surface somebody can see. Two draws of one mesh at one MATRIX are indistinguishable, so
        // dropping one of those is always safe -- the only thing lost is which of two actors a
        // click selects, and one of them was unreachable anyway.
        static (float, float, float, float, float, float, float, float, float, float, float, float)
            Frame(Matrix4x4 m) => (
                MathF.Round(m.M11, 3), MathF.Round(m.M12, 3), MathF.Round(m.M13, 3),
                MathF.Round(m.M21, 3), MathF.Round(m.M22, 3), MathF.Round(m.M23, 3),
                MathF.Round(m.M31, 3), MathF.Round(m.M32, 3), MathF.Round(m.M33, 3),
                MathF.Round(m.M41, 2), MathF.Round(m.M42, 2), MathF.Round(m.M43, 2));

        var seen = new HashSet<(string, (float, float, float, float, float, float,
                                         float, float, float, float, float, float))>();
        var once = new List<Placement>(made.Count);
        foreach (var p in made)
            if (seen.Add((p.MeshPath, Frame(p.Model)))) once.Add(p);
        return once;
    }

    private const int MaxLevelInstanceDepth = 4;

    /// <summary>
    /// A LEVEL INSTANCE IS A WHOLE BUILDING IN ANOTHER FILE.
    ///
    /// UE5 lets a designer build a structure once as its own .umap and drop it into a level as a
    /// single actor. Nothing of it lives in the map you opened: the actor is an
    /// `/Script/Engine.LevelInstance` carrying a `CookedWorldAsset` path and a root component
    /// holding where to put it. So a map can be missing an entire building and every count in
    /// this file still says the level was read correctly -- which is exactly what the author
    /// walked into: a room with its ladders and its door frame and no walls.
    ///
    /// The instanced package is read ONCE and reused: SK_Anaxes_020 drops nineteen of them and
    /// re-reading a prefab per copy would cost nineteen package reads for one answer.
    ///
    /// Each placement keeps the PREFAB's package and name rather than the host map's, because
    /// that is the file a move would have to be written into -- and it is worth the author seeing
    /// that a wall inside a level instance belongs to the prefab, not to the map, since editing
    /// it moves the wall in every copy.
    /// </summary>
    /// <summary>What a variant group chose and what it left out, for the Log panel.</summary>
    public static readonly List<string> VariantNotes = new();

    /// <summary>
    /// ONE AREA OF A MAP THAT SHIPS IN SEVERAL STORY STATES.
    ///
    /// <see cref="Options"/> are the whole set, leaf names; <see cref="Preferred"/> is the one to
    /// show unless the author says otherwise.
    /// </summary>
    public sealed record VariantGroup(string Prefix, List<string> Options, string Preferred);

    /// <summary>One set of whole sublevels, as the game's own folders separate them.</summary>
    public sealed record SublevelSet(string Category, string Folder, List<string> Sublevels)
    {
        public string Label => $"{Category} / {Folder}";
    }

    /// <summary>
    /// SETS OF WHOLE SUBLEVELS, which <see cref="Variants"/> cannot see.
    ///
    /// The hub is docked at the Ring of Kafrene for most of the game and adrift in open space
    /// once the Imperial era starts, and those are THREE sublevels against ONE:
    ///
    ///     Sublevels/Environment/Vista_RingOfKafrene/  ROK_Asteroid, ROK_CargoDock, ROK_City
    ///     Sublevels/Environment/Vista_Nebula/         Hub_Env_Vista_Nebula
    ///
    /// `Variants` groups LEVEL INSTANCES placed at one spot and so never meets them. Worse, the
    /// name rule it uses would be actively wrong here: all four share the prefix
    /// `Hub_Env_Vista_`, so it would offer them as four alternatives when the three Kafrene
    /// pieces are shown TOGETHER and only the folder separates the two states.
    ///
    /// SO THIS INFERS NOTHING ABOUT WHICH SETS ARE ALTERNATIVES. It reports every child folder
    /// that has a sibling, and the author chooses. There was no way to prove a prefix rule safe
    /// across 2,422 maps, and the author's ask was to be able to load each variant whenever he
    /// wants rather than to have one picked for him -- so a choice is the feature, not a
    /// shortcoming.
    ///
    /// Index-only, no package opened, so it can front the dialog before any loading starts.
    /// </summary>
    public static List<SublevelSet> SublevelSets(MapEntry map, AssetIndex? index,
                                                IReadOnlySet<string>? streamed = null)
    {
        if (index is null) return [];

        // Category is the folder under Sublevels/ (Environment, Facilities, Lighting ...), and a
        // SET is a folder inside one of those. A sublevel sitting directly in a category is not
        // part of any set and is left alone.
        var sets = new Dictionary<(string Category, string Folder), List<string>>();
        var marker = map.Folder + "/Sublevels/";

        foreach (var f in index.Root.DescendantFiles())
        {
            var path = f.FullPath;
            if (!path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            if (!path.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = path[marker.Length..].Split('/');
            if (rest.Length < 3) continue;          // category/leaf.umap -- not in a set

            // ONLY WHAT THE MAP ACTUALLY STREAMS, when the caller knows. The folder listing is a
            // GUESS -- the hub's folder holds 15 sublevels while its root streams 59 -- and
            // measuring it showed both Facilities sets, twelve sublevels of soundstage lighting,
            // are not in the hub's streamed list at all. Offering them would be twelve rows that
            // do nothing, which is worse than not offering them.
            var bare = Bare(path);
            if (streamed is not null && !streamed.Contains(bare)) continue;

            var key = (rest[0], rest[1]);
            if (!sets.TryGetValue(key, out var list)) sets[key] = list = [];
            list.Add(bare);
        }

        // A FOLDER WITH NO SIBLING IS NOT A CHOICE. Offering it would be a row that can only be
        // turned off, which is what the per-sublevel tick boxes already are.
        //
        // NOTE THAT SIBLINGS NEED NOT BE ALTERNATIVES. Armory_ENV and Customization_ENV are two
        // different rooms and both are present; Vista_RingOfKafrene and Vista_Nebula are the same
        // window in two eras. Nothing here can tell those apart, so nothing here claims to: they
        // are offered as sets to include or leave out, and the author decides which are
        // exclusive. Calling them alternatives would be the guess this replaced.
        return sets
            .Where(kv => sets.Keys.Count(k => string.Equals(k.Category, kv.Key.Category,
                                                            StringComparison.OrdinalIgnoreCase)) > 1)
            .Select(kv => new SublevelSet(kv.Key.Category, kv.Key.Folder,
                                          kv.Value.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The bare primitive names a greybox is built from. EXACT matches only: "Cube" is a
    /// placeholder, a longer name that merely contains it is a crate somebody modelled.
    /// </summary>
    private static readonly HashSet<string> Primitives = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cube", "Cubo", "Cylinder", "Sphere", "Plane", "Cone", "Torus", "BlockoutCube",
    };

    /// <summary>
    /// IS THIS A GREYBOX PLACEHOLDER rather than art?
    ///
    /// Shipped levels keep their blockout in place -- Umbara has 117 meshes with "blockout" in the
    /// name plus 82 called Cube, 20 Cylinder and 4 Plane -- and the game never draws them. In the
    /// editor they are grey boxes and cylinders standing in the middle of the level, which is what
    /// the author saw: "you also load the blockout meshes which cause a lot of just random square
    /// and cylinders".
    ///
    /// Matched on the MESH first, then the actor and the sublevel, because a Blueprint ramp
    /// (BP_Blockout_Ramp_4x2_A_C) and a whole HUB_Blockout sublevel are the same intent said in
    /// different places.
    /// </summary>
    public static bool IsBlockout(string meshPath, string actor, string sublevel)
    {
        var leaf = Leaf(meshPath);
        var dot = leaf.LastIndexOf('.');
        if (dot >= 0) leaf = leaf[(dot + 1)..];

        return leaf.Contains("blockout", StringComparison.OrdinalIgnoreCase)
            || Primitives.Contains(leaf)
            || actor.Contains("blockout", StringComparison.OrdinalIgnoreCase)
            || sublevel.Contains("blockout", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The variant groups in a map, FROM THE FILE INDEX ALONE -- no package is opened, so this is
    /// instant and can front a dialog before any loading starts.
    ///
    /// Grouping is by shared name prefix rather than by position, because position is only known
    /// after reading every level instance, which is most of the cost of opening the map. The names
    /// are unambiguous enough: LI_Station_Trick_ON / _OFF1 / _HURT, LI_NSDCommandCenterBefore /
    /// After, six LI_Hub_HangarState_Lift08_A..F.
    /// </summary>
    public static List<VariantGroup> Variants(MapEntry map, AssetIndex? index)
    {
        // EVERY .umap UNDER THE MAP'S FOLDER, not just the ones beside the root.
        //
        // The hub keeps its story states three folders down --
        // Maps/Hub/Assets/AuthoredHeroStations/Cly/LI_Station_Cly_ON and friends -- so a list
        // built from the map's own siblings finds the girders and misses every crew station,
        // which are the ones that actually sit on top of each other.
        var names = (index is null
                ? map.Sublevels
                : index.Root.DescendantFiles()
                       .Select(f => f.FullPath)
                       .Where(f => f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
                                   f.StartsWith(map.Folder + "/", StringComparison.OrdinalIgnoreCase)))
            .Select(Bare).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var groups = new List<VariantGroup>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (taken.Contains(name)) continue;
            var family = names.Where(o => !taken.Contains(o) && SameThingDifferentState(name, o)).ToList();
            if (family.Count < 2) continue;

            var preferred = family
                .OrderByDescending(o => o.EndsWith("_ON", StringComparison.OrdinalIgnoreCase))
                // DAY, when the group is a time of day. "Shortest name" would otherwise hand the
                // hub to _Dawn on an alphabetical tie, and a map's default state is its daylight
                // one -- that is what the loading screens and the art direction show.
                .ThenByDescending(o => o.EndsWith("_Day", StringComparison.OrdinalIgnoreCase))
                .ThenBy(o => o.Length)
                .ThenBy(o => o, StringComparer.OrdinalIgnoreCase)
                .First();

            groups.Add(new VariantGroup(CommonPrefix(family), family, preferred));
            foreach (var o in family) taken.Add(o);
        }
        return groups;
    }

    private static string[] Tokens(string name) =>
        name.Split('_', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// THE WORDS THIS GAME USES FOR "the same place, later in the story".
    ///
    /// Spelled out rather than inferred, because every inference tried first was wrong in one
    /// direction or the other: a character-prefix test cut through the middle of a number and
    /// paired Girders_00 with Girders_01_Scaffold, and a "most of the tokens match" test paired
    /// HUB_Environment with HUB_Environment_Pit, which are two sublevels that both belong on
    /// screen at once. A list of fourteen words is duller and it is right.
    ///
    /// DEACEASED is the game's own spelling, in Cly's folder. Both are here on purpose.
    /// </summary>
    private static readonly HashSet<string> StateWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ON", "OFF", "OFF1", "OFF2", "HURT", "DECEASED", "DEACEASED", "FIRST_TURN",
        "BEFORE", "AFTER", "SCAFFOLD", "HOLLOWED", "HDN", "DURING", "NEUTRAL",

        // TIME OF DAY. The hub streams HUB_Lighting_EXT_Day, _Dawn and _Night side by side, each
        // with its own sun, sky and fog -- three mutually exclusive rigs that were all being
        // summed. They belong in the same dialog as the story states: one is on, and which one is
        // the author's to choose.
        "DAY", "DAWN", "DUSK", "NIGHT", "NOON", "MORNING", "EVENING",
    };

    private static bool IsState(string tail) =>
        tail.Length == 0 ||
        StateWords.Contains(tail) ||
        // A single letter is how the hangar lifts number their six positions, Lift08_A to _F.
        (tail.Length == 1 && char.IsLetter(tail[0]));

    /// <summary>
    /// ARE THESE TWO THE SAME AREA IN DIFFERENT STATES, or two different pieces?
    ///
    /// The names have to agree token for token until they stop, and whatever each has LEFT has to
    /// be a state word. LI_Station_Cly_ON and LI_Station_Cly_FIRST_TURN leave "ON" and
    /// "FIRST_TURN"; LI_Hub_Office_Astromech and LI_Hub_Office_Ceiling_00 leave "Astromech" and
    /// "Ceiling_00", which are a droid and a ceiling.
    ///
    /// A wrong answer here is unhelpful, not dangerous: what actually gets collapsed is decided by
    /// POSITION when the level instances are expanded. This only decides what the dialog offers,
    /// and an offer that never applies costs a row nobody reads.
    /// </summary>
    private static bool SameThingDifferentState(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;

        var ta = Tokens(a);
        var tb = Tokens(b);
        var min = Math.Min(ta.Length, tb.Length);

        var k = 0;
        while (k < min && ta[k].Equals(tb[k], StringComparison.OrdinalIgnoreCase)) k++;
        if (k < 2) return false;                            // not even the same family

        var restA = string.Join('_', ta.Skip(k));
        var restB = string.Join('_', tb.Skip(k));
        if (IsState(restA) && IsState(restB)) return true;

        // NO SEPARATOR AT ALL: LI_NSDCommandCenterBefore and LI_NSDCommandCenterAfter are one
        // token that ends in a state word. Only worth trying when exactly one token differs.
        if (ta.Length != tb.Length || k != ta.Length - 1) return false;
        var shared = CommonPrefix(new List<string> { restA, restB }).Length;
        return shared >= 6 && IsState(restA[shared..]) && IsState(restB[shared..]);
    }

    /// <summary>
    /// Which variants to draw, by leaf name. Null means "decide by name", which is what a headless
    /// run and the first open of a map both get.
    /// </summary>
    public static HashSet<string>? Chosen { get; set; }

    /// <summary>
    /// EVERY VARIANT NAME IN THE OPEN MAP, MAPPED TO THE ONE THAT WINS.
    ///
    /// Position was the wrong key, and the measurement said so: LI_Station_Luco_ON and
    /// LI_Station_Luco_OFF were BOTH being drawn, overlapping at 140 places, because their
    /// LevelInstance actors do not sit at the same transform to within a tenth of a centimetre --
    /// while LI_Station_Luco_DECEASED did coincide and was correctly hidden. One station, two
    /// visible states, and the collapse that was supposed to stop exactly that reported success.
    ///
    /// The name is the reliable key: the game shows ONE state of Luco's station, wherever the
    /// instance happens to be placed and whichever sublevel places it. Position still handles
    /// families whose names this cannot see -- the hangar lifts live outside the map folder.
    /// </summary>
    public static Dictionary<string, string> Family { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Work out the winner for every variant name in this map, once, before it loads.</summary>
    public static void PlanVariants(MapEntry map, AssetIndex? index, HashSet<string>? chosen)
    {
        Chosen = chosen;
        var plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Variants(map, index))
        {
            var want = chosen?.FirstOrDefault(c => g.Options.Contains(c, StringComparer.OrdinalIgnoreCase))
                       ?? g.Preferred;

            // EVERY option maps to the winner, the winner included, so a lookup answers both
            // "is this a variant?" and "is this the one to draw?" in one go.
            foreach (var o in g.Options) plan[o] = want;
        }
        Family = plan;
        lock (VariantNotes) VariantNotes.Clear();
    }

    private static IEnumerable<Placement> LevelInstances(UObject[] exports, LevelExport.Composed w,
                                                         IFileProvider provider, int depth,
                                                         Dictionary<string, List<Placement>> cache,
                                                         Dictionary<UObject, bool> gates, string outer)
    {
        var made = new List<Placement>();

        // ONE VARIANT PER PLACE. A hub station ships as LI_Station_Trick_ON, _OFF1 and _HURT, all
        // placed at the SAME transform, and the game shows exactly one depending on the story.
        // Drawing all three puts three near-identical copies in the same space -- which is not
        // "more of the level", it is z-fighting, and it reads as the whole level flashing.
        //
        // The author spotted this from the symptom alone: "are you loading every instance of this
        // map at once into the viewport? this map has different variants throughout the story".
        // The evidence had been sitting in --levelprops the whole time: LevelInstance_20, _24 and
        // _26 all at X=-564.545 Y=4655, three different world assets.
        var found = new List<(int At, string Path, Matrix4x4 Place, Vector3 Pos)>();
        for (var i = 0; i < exports.Length; i++)
        {
            var e = exports[i];
            if (Holder(e, "CookedWorldAsset") is not { } holder) continue;

            var path = WorldAssetPath(holder);
            if (string.IsNullOrEmpty(path)) continue;

            // A RULE ON THE INSTANCE OVERRIDES bAutomaticallyLoad, AND THAT ORDER IS THE WHOLE
            // POINT. `bAutomaticallyLoad=false` does not mean "never": it means nothing loads
            // this on its own, and the GameFactVisibilityComponent hanging off it is the thing
            // that does. LevelInstance_10 in the hub is exactly this shape --
            // ALL( ALL(Facts.Hub.HS_05_000_a), NONE(Facts.Hub.HS_05_005_a) ) on an instance that
            // does not auto-load. Reading the flag first would hide it in every story state,
            // including the ones where the game plainly shows it.
            if (gates.TryGetValue(e, out var admitted))
            {
                if (!admitted) { SkippedByFacts++; continue; }
                AllowedByFacts++;
            }
            // THE GAME DOES NOT LOAD THIS ONE BY ITSELF, and nothing says when it would. All 30
            // instances in HUB_Hangar_Scaffolding are this, and the viewport drew every one --
            // the author's "meshes called elevator support blocking the hangar entrance".
            // Defaults to TRUE when absent, because an instance that writes nothing down is an
            // ordinary one that loads.
            else if (!ShowUnloaded && !AutoLoads(e))
            { SkippedNotAutoLoaded++; continue; }

            // Where the instance sits: its ROOT component's composed transform, which the walk
            // above has already worked out.
            var root = Resolve(e.GetOrDefault<FPackageIndex?>("RootComponent", null), exports) ?? e;
            var at = Array.FindIndex(exports, x => ReferenceEquals(x, root));
            var placeAt = at >= 0
                ? Model(w.Loc[at], w.Rot[at], w.Scale[at])
                : Matrix4x4.Identity;

            // THE NAME DECIDES FIRST. A losing state is skipped wherever it sits and whatever
            // places it, which is the part position grouping could not do.
            var leaf = Bare(path!);
            if (Family.TryGetValue(leaf, out var winner) &&
                !leaf.Equals(winner, StringComparison.OrdinalIgnoreCase))
            {
                var note = $"variant {winner} is drawn, {leaf} is not";
                lock (VariantNotes)
                    if (!VariantNotes.Contains(note)) { VariantNotes.Add(note); Console.WriteLine(note); }
                continue;
            }

            found.Add((i, path!, placeAt, new Vector3(placeAt.M41, placeAt.M42, placeAt.M43)));
        }

        foreach (var spot in found.GroupBy(f => (MathF.Round(f.Pos.X, 1), MathF.Round(f.Pos.Y, 1),
                                                 MathF.Round(f.Pos.Z, 1))))
        {
            var here = spot.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                           .Select(g => g.First()).ToList();
            var chosen = here;

            if (here.Count > 1)
            {
                // ONLY when the names say they are the same thing in different states. Two
                // genuinely different instances that happen to share an origin must both be
                // drawn, so a shared prefix is required rather than position alone.
                var leaves = here.Select(h => Leaf(h.Path)).ToList();
                var prefix = CommonPrefix(leaves);
                if (prefix.Length >= 6)
                {
                    // THE AUTHOR'S CHOICE FIRST, the name heuristic only when there is none. If
                    // they picked something in this group, that is what is drawn; if they picked
                    // nothing here, fall back rather than drawing nothing at all.
                    var pick = Chosen is not null && here.Any(h => Chosen.Contains(Leaf(h.Path)))
                        ? here.First(h => Chosen.Contains(Leaf(h.Path)))
                        : here.OrderByDescending(h => Leaf(h.Path).EndsWith("_ON", StringComparison.OrdinalIgnoreCase))
                              .ThenBy(h => Leaf(h.Path).Length)
                              .ThenBy(h => Leaf(h.Path), StringComparer.OrdinalIgnoreCase)
                              .First();
                    chosen = new List<(int, string, Matrix4x4, Vector3)> { pick };

                    // SAID OUT LOUD, not chosen quietly. Hiding part of a level without saying so
                    // is the quiet lie this tool exists to avoid -- the same reason the backdrop
                    // is shown by default. Deduped because a cached sublevel is walked more than
                    // once and the author does not need to read it twice.
                    var note = $"variant {prefix}*: showing {Leaf(pick.Path)}, hiding " +
                               string.Join(", ", leaves.Where(l => l != Leaf(pick.Path)));
                    lock (VariantNotes)
                        if (!VariantNotes.Contains(note))
                        {
                            VariantNotes.Add(note);
                            Console.WriteLine(note);
                        }
                }
            }

            foreach (var one in chosen) Expand(one.Path, one.Place, made, provider, depth, cache, outer);
        }
        return made;
    }

    /// <summary>How much of the start of these names is shared, case-insensitively.</summary>
    private static string CommonPrefix(List<string> names)
    {
        if (names.Count == 0) return "";
        var first = names[0];
        var n = first.Length;
        foreach (var other in names)
        {
            n = Math.Min(n, other.Length);
            var k = 0;
            while (k < n && char.ToUpperInvariant(first[k]) == char.ToUpperInvariant(other[k])) k++;
            n = k;
        }
        return first[..n];
    }

    private static void Expand(string path, Matrix4x4 placeAt, List<Placement> made,
                               IFileProvider provider, int depth,
                               Dictionary<string, List<Placement>> cache, string outer)
    {
        {
            // THE PREFAB'S OWN PIVOT. A Level Instance's actors are NOT authored around the pivot
            // the designer chose -- they stay where they were, and the difference is recorded in
            // the prefab's world settings for UE to apply on load. Fifty of the hub's prefabs
            // carry one, up to fifty metres, and this expansion ignored every one of them.
            //
            // Applied in the PREFAB's space, so it goes between the inner transform and the outer
            // one: a rotated instance must be offset along its own axes. See
            // LevelExport.LevelInstancePivot for the measurement that settled both whether it is
            // live and which way round it goes.
            var pivot = LevelExport.LevelInstancePivot(provider, path);
            var into = Matrix4x4.CreateTranslation(
                           new Vector3((float)pivot.X, (float)pivot.Y, (float)pivot.Z)) * placeAt;

            var note = $"{Leaf(path)} placed at " +
                       $"({placeAt.M41:0}, {placeAt.M42:0}, {placeAt.M43:0})" +
                       // THE NUMBERS HAVE TO RECONCILE. The provenance readout exists so a prop
                       // in the wrong place can be accounted for line by line, and a silent
                       // offset between two printed steps is what made its first version useless.
                       (pivot.X != 0 || pivot.Y != 0 || pivot.Z != 0
                           ? $" + its own pivot ({pivot.X:0.##}, {pivot.Y:0.##}, {pivot.Z:0.##})" : "");
            if (!cache.TryGetValue(path, out var inner))
            {
                try
                {
                    var read = Read(provider, path);
                    inner = Placements(read, PackageNameOf(path), Leaf(path), provider, depth + 1, cache);
                }
                catch { inner = new List<Placement>(); }
                cache[path] = inner;
            }

            var pivotIsReal = pivot.X != 0 || pivot.Y != 0 || pivot.Z != 0;

            foreach (var p in inner)
            {
                if (pivotIsReal) Pivoted++;
                var m = p.Model * into;

                // MATERIALS ARE CARRIED THROUGH, and for two days they were not.
                //
                // This re-emits a prefab's contents at the outer transform, and it rebuilt the
                // record field by field -- so `Materials` was silently dropped for EVERYTHING
                // inside a level instance. The override-material fix landed and then applied to
                // the open package only, which in the hub is a small minority of what is drawn:
                // the crew stations, the LAAT bays and the whole ring are level instances, and
                // those are precisely the lamps the game paints `Emissive_Off` and we drew lit.
                //
                // The same shape of fault as the first: a value read correctly, then dropped on
                // the way to the thing that uses it. Found by reading this function while
                // building the provenance readout, not by a test -- nothing checks it.
                made.Add(new Placement(p.MeshPath, m, p.Actor,
                                       new Vector3(m.M41, m.M42, m.M43),
                                       p.Owner, p.Scope, p.Package, p.Sublevel)
                {
                    Pivot = Vector3.Transform(p.Pivot, into),
                    Materials = p.Materials,
                    Component = p.Component,
                    Instance = p.Instance,
                    InstanceAt = p.InstanceAt,
                    // THE OUTERMOST WINS, and it does so for free: Expand runs as the recursion
                    // unwinds, so a prefab three deep is stamped by its own container first and
                    // overwritten by each container above it, ending on the map's sublevel.
                    RootSublevel = outer,
                    // Outermost FIRST, so the chain reads the way the level is assembled.
                    Through = p.Through is null ? note : note + "  →  " + p.Through,
                });
            }
        }
    }

    /// <summary>
    /// The package a LevelInstance points at. Cooked builds write it as a soft object path, so it
    /// arrives as text rather than as an import to resolve.
    /// </summary>
    private static string? WorldAssetPath(UObject holder)
    {
        var soft = holder.GetOrDefault<FSoftObjectPath?>("CookedWorldAsset", null);
        var text = soft?.AssetPathName.Text;
        if (string.IsNullOrEmpty(text))
            text = holder.GetOrDefault<FPackageIndex?>("CookedWorldAsset", null)?.ResolvedObject?.GetPathName();
        if (string.IsNullOrEmpty(text)) return null;

        // "/Game/.../Thing.Thing" names the object inside the package; the package is the part in
        // front of the dot.
        var dot = text.LastIndexOf('.');
        return dot > 0 ? text[..dot] : text;
    }

    private static Vector3 PivotOf(LevelExport.Composed w, int top) =>
        new((float) w.Loc[top].X, (float) w.Loc[top].Y, (float) w.Loc[top].Z);

    private static Matrix4x4 Model(FVector loc, FQuat rot, FVector scale) =>
        Matrix4x4.CreateScale((float) scale.X, (float) scale.Y, (float) scale.Z)
      * Matrix4x4.CreateFromQuaternion(new Quaternion(rot.X, rot.Y, rot.Z, rot.W))
      * Matrix4x4.CreateTranslation((float) loc.X, (float) loc.Y, (float) loc.Z);

    /// <summary>How an attach chain came out, for the coverage report to tell the truth with.</summary>
    public static LevelExport.Composed Chains(UObject[] exports) =>
        LevelExport.Compose(exports, inherit: true);

    /// <summary>See <see cref="LevelExport.Holder"/>.</summary>
    public static UObject? Holder(UObject? e, string name) => LevelExport.Holder(e, name);

    // ---------------------------------------------------------------- transform

    public sealed record TransformInfo(UObject? Holder, string Source, string Prefix, bool Found,
                                       FVector Location, FRotator Rotation, FVector Scale);

    /// <summary>How a move names this transform, e.g. "RootComponent.RelativeLocation.Z".</summary>
    public static string PathOf(TransformInfo t, string property, string component) =>
        t.Prefix + property + "." + component;

    /// <summary>
    /// WHERE AN ACTOR ACTUALLY IS.
    ///
    /// Measured on the shipped game: a `TacticalPoint` serialises only `OwningTeam` and
    /// `RootComponent` -- the position is on the component, as `RelativeLocation`. So a panel
    /// that reads the transform off "the actor" finds nothing at all on this game's cover, and
    /// an editor that writes there would write to the wrong export. Follow RootComponent first,
    /// fall back to the export itself for things that are already components.
    /// </summary>
    public static TransformInfo Transform(UObject export, UObject[] all)
    {
        var root = Resolve(export.GetOrDefault<FPackageIndex?>("RootComponent", null), all);
        if (root is not null && Has(root))
            return Read(root, $"on RootComponent → {root.ExportType} \"{root.Name}\"", "RootComponent.");

        if (Has(export)) return Read(export, "on this export", "");

        return new TransformInfo(null, "no transform on this export", "", false,
                                 FVector.ZeroVector, FRotator.ZeroRotator, FVector.OneVector);

        static bool Has(UObject o) =>
            o.Properties.Any(p => p.Name.Text is "RelativeLocation" or "RelativeRotation" or "RelativeScale3D");

        static TransformInfo Read(UObject o, string source, string prefix) => new(
            o, source, prefix, true,
            o.GetOrDefault("RelativeLocation", FVector.ZeroVector),
            o.GetOrDefault("RelativeRotation", FRotator.ZeroRotator),
            o.GetOrDefault("RelativeScale3D", FVector.OneVector));
    }

    /// <summary>
    /// An object reference inside the same package. `ResolvedObject.Object` is the direct route;
    /// matching by name over the exports we already hold is the fallback, because a Zen package
    /// does not always hand back a loaded object for an export index.
    /// </summary>
    public static UObject? Resolve(FPackageIndex? index, UObject[] all)
    {
        if (index is null || index.IsNull) return null;
        var resolved = index.ResolvedObject;
        if (resolved?.Object?.Value is { } obj) return obj;
        var name = resolved?.Name.Text;
        return name is null ? null : all.FirstOrDefault(e => e.Name == name);
    }

    // ---------------------------------------------------------------- provenance

    /// <summary>
    /// WHY THIS THING IS WHERE IT IS, in the order the level assembles it.
    ///
    /// The author's method for finding a misplaced prop was to look at the game, look at us, and
    /// say "that is wrong" -- which is a far better detector than anything automatic, and left
    /// me an hour of eliminating mechanisms one at a time to turn each report into a diagnosis.
    /// Five mechanisms can put a thing in the wrong place and the audit can only see the ones we
    /// never read; a value we read and COMPOSE wrongly looks identical from outside.
    ///
    /// So this states all five for one placement: the level instances it was carried through,
    /// every link of the attach chain, which object each channel was actually read from, the
    /// socket if there is one, and the instance row. A report becomes a diagnosis in the time it
    /// takes to read it.
    ///
    /// It describes the SHIPPED placement, not the pending edits laid over it -- the question
    /// being asked is always "why did it ship here", and mixing an unsaved drag into the answer
    /// would make the one readout that is supposed to settle arguments the hardest to trust.
    /// </summary>
    public static List<PropRow> Provenance(Placement p)
    {
        var rows = new List<PropRow>();

        // OUTERMOST FIRST. A prop inside a prefab inside a sublevel has three transforms
        // multiplied together and only the innermost is in the package on screen; saying so
        // first stops the chain below reading as though it were the whole story.
        if (p.Through is { } through)
            rows.Add(new PropRow("carried through", through, "level instance"));

        var chain = p.Component is null
            ? new List<LevelExport.Step>()
            : LevelExport.Provenance(p.Scope, p.Component);

        if (chain.Count == 0 && p.Component is not null)
            rows.Add(new PropRow("attach chain", "not resolvable - the component is not in this package's " +
                                                 "export list, so the chain cannot be walked", "?"));

        for (var i = 0; i < chain.Count; i++)
        {
            var s = chain[i];
            var bits = new List<string> { s.Type };

            // A SOCKET IS A LINK, and an unresolved one is damage: the offset silently became
            // zero, which is what collapsed a whole dropship onto one point.
            if (s.Socket is { } sock)
                bits.Add(s.SocketFound
                    ? $"on socket '{sock}' (+{s.SocketOffset.X:0.#}, {s.SocketOffset.Y:0.#}, {s.SocketOffset.Z:0.#})"
                    : $"!! socket '{sock}' NOT FOUND - its offset was dropped");

            // Only say where a value came from when it is NOT the level's own export. "own" on
            // every line is noise, and noise is what makes a readout stop being read.
            var from = new List<string>();
            void Note(string what, string whose) { if (whose != "own") from.Add($"{what} from {whose}"); }
            Note("location", s.LocFrom);
            Note("rotation", s.RotFrom);
            Note("scale", s.ScaleFrom);
            if (from.Count > 0) bits.Add(string.Join(", ", from));

            if (s.AbsLoc) bits.Add("bAbsoluteLocation - ignores its parent's position");
            if (s.AbsRot) bits.Add("bAbsoluteRotation - ignores its parent's rotation");
            if (s.AbsScale) bits.Add("bAbsoluteScale - ignores its parent's scale");

            bits.Add($"+({s.Loc.X:0.#}, {s.Loc.Y:0.#}, {s.Loc.Z:0.#})");
            if (Math.Abs(s.Scale.X - 1) > 0.001 || Math.Abs(s.Scale.Y - 1) > 0.001 ||
                Math.Abs(s.Scale.Z - 1) > 0.001)
                bits.Add($"x({s.Scale.X:0.###}, {s.Scale.Y:0.###}, {s.Scale.Z:0.###})");
            bits.Add($"=> ({s.World.X:0}, {s.World.Y:0}, {s.World.Z:0})");

            // ASCII ON PURPOSE. The first version separated these with a middle dot, which the
            // panel rendered and the console turned into a replacement character -- so the
            // headless readout, the one built so a fix does not need the author to confirm it,
            // was the harder of the two to read. A prettier glyph is not worth that.
            rows.Add(new PropRow($"{i + 1} > {s.Name}", string.Join("  |  ", bits), s.Type));
        }

        // AN INSTANCE ROW IS NOT A COMPONENT, so it cannot appear in the chain above -- and for
        // the hangar deck it is the only thing telling 340 identical tiles apart.
        if (p.Instance >= 0)
        {
            var inherited = p.Component is not null &&
                            !ReferenceEquals(LevelExport.Holder(p.Component, "PerInstanceSMData"), p.Component);
            rows.Add(new PropRow("instance row",
                $"#{p.Instance} of PerInstanceSMData  |  " +
                $"+({p.InstanceAt.X:0.#}, {p.InstanceAt.Y:0.#}, {p.InstanceAt.Z:0.#}) in the component's space" +
                (inherited ? "  |  INHERITED from the component template, not written in this level" : ""),
                "instance"));
        }

        // WHAT IT WEARS INSTEAD OF THE MESH'S OWN. Half of "that is wrong" is about the
        // picture rather than the position, and OverrideMaterials is where the game states it --
        // the lamps painted `Emissive_Off` and drawn lit were invisible to every readout we had.
        //
        // BY SLOT, with the gaps kept. A null entry is UE for "keep the mesh's own", and closing
        // the gaps would renumber every material after it onto the wrong part of the model --
        // which would make this readout lie in exactly the way it is meant to catch.
        if (p.Materials is { Length: > 0 } mats)
        {
            var named = new List<string>();
            for (var s = 0; s < mats.Length; s++)
                if (mats[s] is { Length: > 0 } one) named.Add($"slot {s} -> {Leaf(one)}");
            rows.Add(new PropRow("overrides",
                named.Count > 0
                    ? string.Join("  |  ", named)
                    : $"{mats.Length} slot(s), all null - the mesh's own materials are kept",
                "material"));
        }

        rows.Add(new PropRow("drawn at",
            $"({p.At.X:0.##}, {p.At.Y:0.##}, {p.At.Z:0.##})", "world"));

        return rows;
    }

    // ---------------------------------------------------------------- properties

    public static List<PropRow> Properties(UObject export, UObject[] all)
    {
        var rows = new List<PropRow>();
        foreach (var p in export.Properties)
        {
            var tag = p.Tag;
            var value = tag?.GenericValue;
            string text;

            switch (value)
            {
                case FPackageIndex pi:
                    var target = Resolve(pi, all);
                    text = target is not null ? $"{target.ExportType} \"{target.Name}\""
                                              : pi.ResolvedObject?.Name.Text ?? "(unresolved)";
                    break;
                case null:
                    text = "(none)";
                    break;
                default:
                    text = Short(tag?.ToString() ?? "");
                    break;
            }

            rows.Add(new PropRow(p.Name.Text, text, tag?.GetType().Name.Replace("Property", "") ?? ""));
        }
        return rows;
    }

    private static string Short(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 110 ? s[..107] + "..." : s;
    }
}

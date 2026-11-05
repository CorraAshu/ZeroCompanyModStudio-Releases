namespace UEToolkit.Core;

/// <summary>
/// A project's slot sets, resolved to the packages they land in. One place for the build and the
/// read-back, so the two can never disagree about where a slot was meant to go.
/// </summary>
public static class SlotSets
{
    /// <summary>Where slots go: a game part's package (IsGamePart -- the build overrides it), or one of
    /// this mod's parts or copies by id. PartExport is the part's own export in that package.</summary>
    public sealed record Landing(string Target, bool IsGamePart, string PartExport, List<SlotAdd.Slot> Slots);

    public static List<Landing> Resolve(ModProject mod, out List<string> problems)
    {
        problems = [];
        // I-1: the top-level `slotSets` key was present but not an array of objects (an object --
        // a natural slip -- or an array holding something that is not an object). Load left
        // mod.SlotSets empty rather than guess at a partial reading, so this is the only place left
        // that can say so -- and it must, or the build would silently ship without the slots asked
        // for and nothing would ever refuse it.
        if (mod.SlotSetsUnreadable) problems.Add("this Studio does not know or cannot read 'slotSets'");
        var byTarget = new Dictionary<string, Landing>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in mod.SlotSets)
        {
            // R18: a key this Studio does not know, and a KNOWN key it could not read (the wrong
            // JSON type), both land in UnknownKeys -- so one wording covers both without the build
            // having to ask which kind of problem it is.
            foreach (var k in set.UnknownKeys) problems.Add($"slot set {set.Id}: this Studio does not know or cannot read '{k}'");
            foreach (var s in set.Slots)
                foreach (var k in s.UnknownKeys) problems.Add($"slot set {set.Id}, slot {s.Id}: this Studio does not know or cannot read '{k}'");
            if (set.Targets.Count == 0) problems.Add($"slot set {set.Id} has no targets");
            if (set.Slots.Count == 0) problems.Add($"slot set {set.Id} has no slots");

            // A target named twice in ONE set used to reach SlotAdd.Add as its slots duplicated in
            // the landing's own list, and fail there as "<tag> is asked for twice" -- a true
            // statement that names the symptom, not the mistake. Caught here, by name, before it
            // ever reaches that far.
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in set.Targets)
            {
                if (!seenTargets.Add(target))
                { problems.Add($"slot set {set.Id} lists target {target} twice"); continue; }

                var isGame = target.StartsWith('/');
                // A string that is BOTH a part id and a copy id is ambiguous, not a free double
                // stamp: refusing it by name is simpler and clearer than the alternative, which is
                // stamping the same slots onto two unrelated assets and documenting the surprise.
                var isPart = !isGame && mod.Parts.Any(p => p.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
                var isCopy = !isGame && mod.Copies.Any(c => c.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (isPart && isCopy)
                { problems.Add($"slot set {set.Id}: '{target}' is both a part id and a copy id -- name one of them differently"); continue; }
                if (!isGame && !isPart && !isCopy)
                { problems.Add($"slot set {set.Id}: '{target}' is neither a game part's path nor one of this mod's parts or copies"); continue; }
                var export = isGame ? target[(target.LastIndexOf('/') + 1)..] : target;
                if (!byTarget.TryGetValue(target, out var landing))
                    byTarget[target] = landing = new Landing(target, isGame, export, []);
                landing.Slots.AddRange(set.Slots.Select(s => s.ToSlot()));
            }
        }
        return byTarget.Values.ToList();
    }

    /// <summary>
    /// A slot's default may name one of this mod's parts or copies by ID -- the build knows where
    /// it writes them, the author need not. Resolved ONCE, before anything is stamped, from the
    /// same package naming the parts and copies loops use; a default that names nothing is refused
    /// by name.
    /// </summary>
    public static void ResolveDefaults(List<Landing> landings, IReadOnlyDictionary<string, string> objectPathById,
                                       List<string> problems)
    {
        for (var l = 0; l < landings.Count; l++)
            for (var i = 0; i < landings[l].Slots.Count; i++)
            {
                var slot = landings[l].Slots[i];
                if (string.IsNullOrEmpty(slot.Default) || slot.Default.StartsWith('/')) continue;
                if (objectPathById.TryGetValue(slot.Default, out var path))
                    landings[l].Slots[i] = slot with { Default = path };
                else
                    problems.Add($"{landings[l].Target}: slot {slot.Id}: default '{slot.Default}' names no part or copy of this mod");
            }
    }

    /// <summary>Every tag the slot sets use -- slot tags, what fits, what paints, the camera tag --
    /// shipped in the mod's own tag config. Registering a tag the game already has is harmless;
    /// forgetting a new one is a mod that works on the author's machine only.</summary>
    public static IEnumerable<string> Tags(ModProject mod) =>
        mod.SlotSets.SelectMany(s => s.Slots)
           .SelectMany(s => new[] { s.Tag, s.Camera ?? "" }.Concat(s.Fits).Concat(s.Paints is { } p ? [p.Slot] : []))
           .Where(t => t.Length > 0)
           .Distinct(StringComparer.Ordinal);
}

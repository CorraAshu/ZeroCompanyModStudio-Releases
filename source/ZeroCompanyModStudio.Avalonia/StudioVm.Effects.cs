using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// WHAT AN ABILITY DOES, in the three shapes it is written in.
///
///   numbers      damage, cost, radius, uses per combat. Typed in.
///   what it      which OTHER asset does the work — a cooldown effect holds the uses per combat,
///   points at    not the ability. Chosen from what the game's own registry says fits the slot.
///   tag          gates: what has to be true for this to fire, and what it blocks while it runs.
///   containers   A SET, and adding one REPLACES the inherited set for this asset alone.
///
/// They are three cards rather than one because they are three different acts. A number is typed,
/// a class is chosen from a narrowed list, and a container is a set with an inheritance trap in
/// it. The old studio drew all three under one heading and the last two were unreachable for two
/// releases because a scrolling table above them ate the height.
///
/// ALL THREE WRITE TO A <see cref="ModOverride"/> on the ability's own package: the mod ships a
/// changed copy of the game's asset at the game's own path. Nothing is duplicated.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// Read the ability's effects and put all three cards up.
    ///
    /// OFF THE UI THREAD: it opens the ability, every effect it applies, and walks the class
    /// chain for each inherited property.
    /// </summary>
    public async Task FillEffectsAsync(AbilityTab tab)
    {
        if (_provider is not { } provider)
        { tab.EffectsNote = "the game is not mounted yet"; return; }

        var package = tab.Ability.Package;

        tab.EffectsRead = false;
        tab.EffectsNote = "reading what it does…";
        var reading = await Task.Run(() =>
        {
            try { return AbilityEffects.Read(provider, package); }
            catch (Exception e)
            {
                return new AbilityEffects.Reading(package, AbilityEffects.Leaf(package), "", "",
                                                  [], [], e.Message);
            }
        });

        tab.Reading = reading;

        if (reading.Why is { Length: > 0 } why)
        {
            tab.EffectsNote = why;
            tab.EffectsSay([], [], []);
            tab.SecEffects.Present = false;
            tab.SecPoints.Present = false;
            tab.SecGates.Present = false;
            tab.EffectsRead = true;
            Say($"{tab.Title}: {why}");
            return;
        }

        // WHERE THE REST OF THE NUMBERS LIVE. An author who opens a GA_ and finds three numbers
        // needs telling that the rest are in the effects it applies, rather than concluding
        // there are none.
        tab.EffectsElsewhere = reading.Effects.Count == 0
            ? ""
            : "It applies " + string.Join(", ", reading.Effects.Select(AbilityEffects.Leaf)) +
              " — most of the numbers live there, and each opens as its own page.";

        tab.EffectsNote = reading.Summary;
        RefillEffects(tab);
        tab.EffectsRead = true;
        Say($"{tab.Title}: {reading.Numbers.Count} number(s), {reading.References.Count} " +
            $"reference(s), {reading.Containers.Count} container(s)");
    }

    /// <summary>
    /// Rebuild the three lists off the reading and the override as it stands.
    ///
    /// Every write calls this rather than patching a row, because a row's state is a function of
    /// BOTH — and a right-hand cell that outlives the value it described is the fault this whole
    /// card exists to avoid.
    /// </summary>
    public void RefillEffects(AbilityTab tab)
    {
        if (tab.Reading is not { } reading) return;

        var ov = FindOverride(reading.Package);

        // ---------------------------------------------------------------- the numbers
        var numbers = new List<SheetTab.EffectRow>();
        foreach (var n in reading.Numbers)
        {
            var edit = ov?.Numbers.FirstOrDefault(
                x => Same(x.Row, n.Export) && Same(x.Field, n.Path));
            var put = n.Add
                ? ov?.AddScalars.FirstOrDefault(x => Same(x.Export, n.Export) && Same(x.Name, n.Path))
                : null;

            var mine = edit is not null || put is not null;
            var value = edit?.Now ?? put?.Value ?? n.Value;

            numbers.Add(new SheetTab.EffectRow
            {
                Export = n.Export,
                Path = n.Path,
                Label = n.Label,
                Detail = n.Detail,
                Where = $"{n.Export}.{n.Path}",
                Now = n.Add ? "—" : NumberText.Show(n.Value, "0.###"),
                Stored = n.Stored,
                Adds = n.Add,
                Note = n.Note.Replace("; ", "\n"),
                Value = NumberText.Show(value, "0.###"),
                Mine = mine,
                State = mine ? ""
                      : n.Note.Length > 0 ? "read elsewhere"
                      : n.Add ? "not in file"
                      : !n.Stored ? "zero, not stored"
                      : "",
            });
        }

        // ---------------------------------------------------------------- what it points at
        var points = new List<SheetTab.PointRow>();
        foreach (var f in reading.References)
        {
            var picked = ov?.AddClasses.FirstOrDefault(
                x => Same(x.Export, f.Export) && Same(x.Name, f.Name));

            points.Add(new SheetTab.PointRow
            {
                Export = f.Export,
                Name = f.Name,
                Label = f.Label,
                Where = $"{f.Export}.{f.Name}",
                Kind = f.Slot.Kind,
                KindName = ClassReferences.KindName(f.Slot.Kind),
                Uses = $"uses {SlotEdit.LeafOf(f.Slot.Inherited)}" +
                       (f.Slot.InheritedFrom.Length > 0
                           ? $", inherited from {SlotEdit.LeafOf(f.Slot.InheritedFrom)} — so every "
                             + "tier that inherits it shares this one asset"
                           : ""),
                Mine = picked is null ? "" : SlotEdit.LeafOf(picked.Target),
            });
        }

        // ---------------------------------------------------------------- the gates
        var gates = new List<SheetTab.TagBoxRow>();
        foreach (var c in reading.Containers)
        {
            var held = ov?.AddTagContainers.FirstOrDefault(
                x => Same(x.Export, c.Export) && Same(x.Name, c.Name));

            gates.Add(new SheetTab.TagBoxRow
            {
                Export = c.Export,
                Name = c.Name,
                Label = c.Label,
                Where = $"{c.Export}.{c.Name}",
                Holds = c.Slot.Tags.Count == 0
                    ? "nothing in the chain sets this"
                    : $"holds {string.Join(", ", c.Slot.Tags)}" +
                      (c.Slot.InheritedFrom.Length > 0
                          ? $" — inherited from {SlotEdit.LeafOf(c.Slot.InheritedFrom)}"
                          : ""),
                StartsFrom = c.Slot.Tags.Count > 0
                    ? $"starts from the {c.Slot.Tags.Count} tag(s) it inherits, so adding one does "
                      + "not drop the rest"
                    : "",
                Added = held is not null,
                Text = held is not null ? string.Join("\n", held.Tags)
                                        : string.Join("\n", c.Slot.Tags),
            });
        }

        tab.EffectsSay(numbers, points, gates);

        tab.SecEffects.Present = numbers.Count > 0;
        tab.SecPoints.Present = points.Count > 0;
        tab.SecGates.Present = gates.Count > 0;

        var changed = (ov?.Numbers.Count ?? 0) + (ov?.AddScalars.Count ?? 0);
        tab.SecEffects.Count = changed == 0 ? "" : $"{changed} changed";
        tab.SecPoints.Count = ov?.AddClasses.Count is > 0 and var p ? $"{p} changed" : "";
        var added = ov?.AddTagContainers.Count(x => x.Tags.Count > 0) ?? 0;
        tab.SecGates.Count = added == 0 ? "" : $"{added} added";
    }

    // ------------------------------------------------------------------ the numbers

    /// <summary>
    /// Record, replace or drop one number.
    ///
    /// SETTING IT BACK TO THE GAME'S OWN REMOVES THE ENTRY rather than storing "17 becomes 17". A
    /// no-op edit would still be shipped, still be checked against the asset, and still be one
    /// more thing that can fail a build for no gain.
    /// </summary>
    public void SetEffectNumber(AbilityTab tab, SheetTab.EffectRow row)
    {
        if (_mod is null || tab.Reading is not { } reading) return;

        tab.Feedback("");

        var source = reading.Numbers.FirstOrDefault(
            n => Same(n.Export, row.Export) && Same(n.Path, row.Path));

        // AN UNCHANGED BOX RECORDS NOTHING. The box shows the number rounded to three places, so a
        // game value of 0.0625 reads 0.063 and tabbing through it would parse back as 0.063 -- a
        // change of 0.0005 that nobody typed, recorded as an edit. Compared as TEXT, against what
        // the box showed before it was typed in: the mod's own number if it holds one, else the
        // game's. (The modification boxes have had this guard since 9b.)
        var held = FindOverride(reading.Package);
        var heldNow = held?.Numbers.FirstOrDefault(x => Same(x.Row, row.Export) && Same(x.Field, row.Path))?.Now
                      ?? (row.Adds
                          ? held?.AddScalars.FirstOrDefault(x => Same(x.Export, row.Export) && Same(x.Name, row.Path))?.Value
                          : null);
        var shown = NumberText.Show(heldNow ?? source?.Value ?? 0, "0.###");
        if ((row.Value ?? "").Trim() == shown) { row.Value = shown; return; }

        // INVARIANT, like every number box here (NumberText): double.TryParse on its own reads
        // the PC's regional format, so "0.5" was five on a German machine. The reason is said
        // in the card as well as the log, because the refill below puts the old number back.
        if (!NumberText.TryParse(row.Value, out var now, out var parseWhy))
        {
            var line = $"{row.Label}: {parseWhy} -- the box is back to {shown}";
            SayBad($"{tab.Title}: {line}");
            RefillEffects(tab);
            tab.Feedback(line, bad: true);
            return;
        }

        // A WHOLE NUMBER TAKES NO FRACTION, and is said AT THE BOX. Stacks, uses per cooldown and
        // the like are integers: 2.5 would be stored as 2, so the build refuses it -- but at build
        // time, naming the property, long after the author typed it. Known from the property's own
        // type (AbilityEffects.Number.Whole), never from how the game's value looks.
        if (source is { Whole: true } && Math.Abs(now - Math.Round(now)) > 1e-9)
        {
            var line = $"{row.Label}: this number is a whole number, so {NumberText.Show(now)} cannot be " +
                       $"stored (it would read back {NumberText.Show(Math.Round(now))}) -- the box is back to {shown}";
            SayBad($"{tab.Title}: {line}");
            RefillEffects(tab);
            tab.Feedback(line, bad: true);
            return;
        }

        var was = source?.Value ?? 0;

        var ov = MakeOverride(reading.Package);

        if (row.Adds)
        {
            // ZERO REMOVES IT, and that is not arbitrary: unversioned serialisation cannot write
            // a zero at all — a zero-valued property is stored as no bytes — so "add this
            // property with the value zero" is not a thing the format can express.
            ov.AddScalars.RemoveAll(x => Same(x.Export, row.Export) && Same(x.Name, row.Path));
            if (Math.Abs(now) > 0.0001)
                ov.AddScalars.Add(new ModOverride.ScalarAdd(row.Export, row.Path, now));
        }
        else
        {
            ov.Numbers.RemoveAll(x => Same(x.Row, row.Export) && Same(x.Field, row.Path));
            if (Math.Abs(now - was) > 0.0001)
                ov.Numbers.Add(new ModOverride.NumberChange(row.Export, row.Path, was, now));
        }

        _mod.Save();
        RefillEffects(tab);
        Say($"{tab.Title}: {row.Label} = {NumberText.Show(now, "0.###")}" +
            (row.Adds ? " (the property is added)" : $", was {NumberText.Show(was, "0.###")}"));
        AfterEditsChanged();
    }

    /// <summary>Put the game's own number back, and drop the entry.</summary>
    public void ResetEffectNumber(AbilityTab tab, SheetTab.EffectRow row)
    {
        if (_mod is null || tab.Reading is not { } reading) return;
        if (FindOverride(reading.Package) is not { } ov) return;

        tab.Feedback("");
        ov.Numbers.RemoveAll(x => Same(x.Row, row.Export) && Same(x.Field, row.Path));
        ov.AddScalars.RemoveAll(x => Same(x.Export, row.Export) && Same(x.Name, row.Path));

        _mod.Save();
        RefillEffects(tab);
        Say($"{tab.Title}: {row.Label} is the game's own again");
        AfterEditsChanged();
    }

    // ------------------------------------------------------------------ what it points at

    /// <summary>
    /// The assets of the right KIND, as the picker's list.
    ///
    /// NARROWED RATHER THAN COMPLETE: the registry says every candidate derives from the same
    /// native class the property already points at — 67 for a cooldown effect out of 35,000
    /// assets — so the list is short enough to read and everything on it is something the game
    /// itself puts in this slot. The typed box beside it is the escape hatch.
    /// </summary>
    public async Task<List<PickAssetWindow.Candidate>> PointCandidatesAsync(SheetTab.PointRow row)
    {
        if (_provider is not { } provider) return [];
        var kind = row.Kind;

        var found = await Task.Run(() =>
        {
            try { return ClassReferences.OfKind(provider, kind); }
            catch (Exception) { return new List<ClassReferences.Candidate>(); }
        });

        return found.Select(c => new PickAssetWindow.Candidate(c.Name, row.KindName, c.Package))
                    .ToList();
    }

    /// <summary>Point one property at another asset — or, with an empty target, back at the game's.</summary>
    public void SetPoint(AbilityTab tab, SheetTab.PointRow row, string target)
    {
        if (_mod is null || tab.Reading is not { } reading) return;

        var ov = MakeOverride(reading.Package);
        ov.AddClasses.RemoveAll(x => Same(x.Export, row.Export) && Same(x.Name, row.Name));

        target = target.Trim();
        if (target.Length > 0)
            ov.AddClasses.Add(new ModOverride.ClassAdd(row.Export, row.Name, target));

        _mod.Save();
        RefillEffects(tab);
        Say(target.Length > 0
                ? $"{tab.Title}: {row.Label} now points at {SlotEdit.LeafOf(target)}"
                : $"{tab.Title}: {row.Label} points where the game points it");
        AfterEditsChanged();
    }

    // ------------------------------------------------------------------ the gates

    /// <summary>
    /// Give this asset its own copy of a tag container, STARTING FROM WHAT IT INHERITS.
    ///
    /// Starting empty is the trap: a container REPLACES the inherited set for this asset alone,
    /// so an author adding one tag to a gate that inherits five would silently lose four, and
    /// nothing in the asset would show it.
    /// </summary>
    public void AddTagBox(AbilityTab tab, SheetTab.TagBoxRow row)
    {
        if (_mod is null || tab.Reading is not { } reading) return;

        var start = row.Text.Replace("\r", "").Split('\n')
                       .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        var ov = MakeOverride(reading.Package);
        ov.AddTagContainers.RemoveAll(x => Same(x.Export, row.Export) && Same(x.Name, row.Name));
        ov.AddTagContainers.Add(new ModOverride.ContainerAdd(row.Export, row.Name, start));

        _mod.Save();
        RefillEffects(tab);
        Say($"{tab.Title}: {row.Label} is this asset's own, holding {start.Count} tag(s)");
        AfterEditsChanged();
    }

    /// <summary>
    /// Rewrite the tags in one container. An EMPTY one is KEPT rather than dropped, so the box
    /// does not vanish under somebody who cleared it to retype; the build skips it.
    /// </summary>
    public void SetTagBox(AbilityTab tab, SheetTab.TagBoxRow row)
    {
        if (_mod is null || tab.Reading is not { } reading) return;
        if (FindOverride(reading.Package) is not { } ov) return;

        var tags = row.Text.Replace("\r", "").Split('\n')
                      .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        var at = ov.AddTagContainers.FindIndex(
            x => Same(x.Export, row.Export) && Same(x.Name, row.Name));
        if (at < 0) return;

        ov.AddTagContainers[at] = new ModOverride.ContainerAdd(row.Export, row.Name, tags);
        _mod.Save();
        RefillEffects(tab);
        Say($"{tab.Title}: {row.Label} holds {tags.Count} tag(s)");
        AfterEditsChanged();
    }

    /// <summary>Take the container back out, so the asset inherits again.</summary>
    public void DropTagBox(AbilityTab tab, SheetTab.TagBoxRow row)
    {
        if (_mod is null || tab.Reading is not { } reading) return;
        if (FindOverride(reading.Package) is not { } ov) return;

        ov.AddTagContainers.RemoveAll(x => Same(x.Export, row.Export) && Same(x.Name, row.Name));
        _mod.Save();
        RefillEffects(tab);
        Say($"{tab.Title}: {row.Label} is inherited again");
        AfterEditsChanged();
    }

    // ------------------------------------------------------------------ the override underneath

    private ModOverride? FindOverride(string package)
        => _mod?.Overrides.FirstOrDefault(o => Same(o.Target, package));

    /// <summary>Find the override on this package, or start one. Made only when something is written.</summary>
    private ModOverride MakeOverride(string package)
    {
        if (FindOverride(package) is { } found) return found;
        var made = new ModOverride { Target = package };
        _mod!.Overrides.Add(made);
        return made;
    }

    /// <summary>
    /// AN OVERRIDE IS NEVER TAKEN BACK OUT HERE, and that is deliberate.
    ///
    /// The tempting cleanup — drop it once these three cards have left nothing on it — cannot be
    /// written safely: an override also carries a display name, a description, a mesh, an icon,
    /// renames, re-points, added rows, recipes, missions, arc entries and tag edits, any of which
    /// somebody else's card put there. A guess at "empty" that misses one of those deletes work
    /// nobody asked to delete, which is far worse than shipping a copy that changes nothing.
    ///
    /// The ImGui Studio has left them in place for a year; matching that is the conservative
    /// answer, not an oversight. Removing one is the right-click menu's job, where it is asked
    /// for explicitly.
    /// </summary>

    private static bool Same(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

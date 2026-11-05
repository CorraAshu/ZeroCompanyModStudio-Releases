namespace UEToolkit.Core;

using CUE4Parse.FileProvider;

/// <summary>
/// ONE ABILITY FAMILY AS A TABLE: a row per tier, a column per value that matters, and the asset
/// each value actually lives on.
///
/// Daelen, 2026-09-09: *"It might be worth trying to isolate all the key values into a custom
/// ability progression table (Level, Focus, Cost, then adding Resource Cost, Uses per Combat,
/// Cooldown, Damage, additional effects etc)"*. The author chose to build it as a SCREEN over the
/// game's own data rather than as a table the mod ships, and was right to: nothing says the game
/// reads such a table, `AttributeMetaData` is the only kind proven to be read at runtime, and a
/// version that needed UE4SS would make the Mod Studio depend on a second tool.
///
/// **The values are not in one place, and that is the whole problem.** For Rocket Strike:
///
///     Level / Focus / Focus cost   the SPECIALIZATION's focus-tree rung naming this tier
///     Min roster level, range      the ability            GA_RocketStrike_T3
///     Damage                       the result effect      GE_RocketStrike_T3
///     Uses per combat, cooldown    the cooldown effect    GE_Cooldown_RocketStrike
///     Resource cost                a table, by DynamicCostTag
///
/// So an author who wants "tier 3 gets two uses" has to know that uses-per-combat is not on the
/// ability, find the cooldown effect it points at, and notice that the property is absent because
/// a cooked package omits anything still at its class default. Every one of those was a separate
/// discovery in this project. The table collapses them into a row.
///
/// ## What it has to say out loud: WHICH CELLS ARE SHARED
///
/// All six Rocket Strike tiers inherit ONE `CooldownGameplayEffectClass` from `GA_RocketStrike_T1`,
/// so there is one cooldown effect behind all six rows. Typing 2 into tier 3's "uses per combat"
/// changes tiers 1 through 6. That is exactly the wall Daelen hit, and a table that hid it would
/// be worse than no table -- it would make the mistake easy and invisible.
///
/// So every cell carries <see cref="Cell.SharedWith"/>: how many OTHER tiers resolve that column to
/// the same package. A caller that offers an edit on a shared cell has to say so, and the answer
/// when the author wants per-tier values is to copy the effect and re-point that tier at its own --
/// which is what `copies` + `addClasses` already do (see
/// <see cref="PropertyAdd.AddClass"/>).
///
/// ## The columns are READ, not listed here
///
/// Naming the properties would be a guess that goes stale with the next patch, and it would only
/// ever describe the families somebody thought of. Instead every number the ability and its
/// effects declare becomes a column, keyed by role and property path, and a column exists when any
/// tier has it. A family with no cooldown effect simply has no cooldown columns.
/// </summary>
public static class AbilityProgression
{
    /// <summary>Which asset a column's value lives on, which is what an author has to understand.</summary>
    public const string RoleUnlock = "unlock";
    public const string RoleAbility = "ability";
    public const string RoleCooldown = "cooldown";
    public const string RoleCost = "cost";
    public const string RoleResult = "result";

    /// <summary>One column: a value that some tier of this family carries.</summary>
    /// <param name="Key">Role and property path. Stable across tiers, which is what makes a column a column.</param>
    /// <param name="Label">What to show. Made unique within a role, because "Damage" appears twice.</param>
    public sealed record Column(string Key, string Label, string Role, string Detail);

    /// <summary>One value on one tier, and the asset it actually lives on.</summary>
    /// <param name="SharedWith">
    /// How many OTHER tiers resolve this column to the same package. Zero means editing it affects
    /// this tier alone; anything else means an edit here changes those tiers too, and the caller
    /// must say so before writing.
    /// </param>
    /// <param name="Addable">The asset does not carry the property and setting it ADDS one.</param>
    public sealed record Cell(string Package, string Export, string Path, double Value,
                              bool Stored, bool Addable, int SharedWith, string Note);

    /// <summary>One tier: its assets, its unlock rung, and its values.</summary>
    public sealed record Tier(int Index, string Ability, string Cooldown, string Cost,
                              IReadOnlyList<string> Results, string Spec, SpecDetails.Rung? Rung,
                              IReadOnlyDictionary<string, Cell> Cells);

    /// <summary>A whole family, ready to draw.</summary>
    public sealed record Family(string Name, IReadOnlyList<Tier> Tiers,
                                IReadOnlyList<Column> Columns, string? Why);

    /// <summary>The tier suffix: "GA_RocketStrike_T3" -> ("GA_RocketStrike", 3).</summary>
    private static (string Base, int Tier) Split(string leaf)
    {
        var cut = leaf.LastIndexOf("_T", StringComparison.Ordinal);
        if (cut <= 0 || cut + 2 >= leaf.Length) return (leaf, 0);
        var tail = leaf[(cut + 2)..];
        return tail.All(char.IsAsciiDigit) && int.TryParse(tail, out var n)
            ? (leaf[..cut], n) : (leaf, 0);
    }

    /// <summary>An object path's package half, and its leaf.</summary>
    private static string PackageOf(string objectPath)
    {
        if (objectPath.Length == 0) return "";
        var dot = objectPath.IndexOf('.');
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    private static string Leaf(string package) =>
        package.Length == 0 ? "" : package[(package.LastIndexOf('/') + 1)..];

    // ------------------------------------------------------------------ the specs, read once

    /// <summary>
    /// Which specialization grants each ability, and on what rung.
    ///
    /// Read once per provider and kept: the question is asked for every tier of every family an
    /// author opens, and the answer is a scan of every specialization in the game. Reading forty
    /// packages to draw one table is fine; reading them again per row is not.
    /// </summary>
    private static readonly object Gate = new();
    private static IFileProvider? _for;
    private static Dictionary<string, (string Spec, SpecDetails.Rung Rung)>? _rungs;

    private static Dictionary<string, (string Spec, SpecDetails.Rung Rung)> Rungs(IFileProvider provider)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_for, provider) && _rungs is { } had) return had;

            var found = new Dictionary<string, (string, SpecDetails.Rung)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var part in PartsIndex.Read(provider)
                                               .Where(x => SpecDetails.LooksLikeSpec(x.Slot)))
                {
                    var spec = SpecDetails.Read(provider, part.Path);
                    if (spec.Error is not null) continue;
                    foreach (var line in spec.Tree)
                        foreach (var rung in line)
                        {
                            // FIRST ONE WINS rather than last. A tier granted by two specializations
                            // is a real shape in this game -- the shared trees -- and picking
                            // whichever came last would make the table change with the index order.
                            var key = rung.Leaf;
                            if (key.Length > 0 && !found.ContainsKey(key))
                                found[key] = (spec.Path, rung);
                        }
                }
            }
            catch (Exception) { /* no specs is "no unlock columns", which draws fine */ }

            _for = provider;
            _rungs = found;
            return found;
        }
    }

    // ------------------------------------------------------------------ reading a family

    /// <summary>
    /// Every tier of the family <paramref name="ability"/> belongs to, as a table.
    ///
    /// <paramref name="ability"/> may be any tier or the family's base name -- an author opens the
    /// one they were looking at, and which tier that happened to be should not decide what they
    /// get to see.
    /// </summary>
    public static Family Read(IFileProvider provider, string ability)
    {
        var leaf = Leaf(PackageOf(ability));
        if (leaf.Length == 0) leaf = ability;
        var (root, _) = Split(leaf);

        List<AbilityIndex.Entry> all;
        try { all = AbilityIndex.Of(provider, AbilityIndex.AbilityPrefix); }
        catch (Exception e) { return new Family(root, [], [], e.Message); }

        // Every ability whose name is this root plus a tier number, and the root itself when it
        // carries no number -- a family of one is still worth a row, and refusing it would make
        // the screen appear and disappear depending on how the game happened to name things.
        var members = all
            .Select(x => (Entry: x, Split: Split(x.Name)))
            .Where(x => string.Equals(x.Split.Base, root, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Split.Tier)
            .ToList();

        if (members.Count == 0)
            return new Family(root, [], [], $"no abilities named {root} or {root}_T<n>");

        var rungs = Rungs(provider);
        var tiers = new List<Tier>();
        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        var labelsSeen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (entry, split) in members)
        {
            var pkg = PackageOf(entry.Package);
            var read = AbilityEffects.Read(provider, pkg);
            if (read.Why is not null) continue;

            var cells = new Dictionary<string, Cell>(StringComparer.Ordinal);

            // WHERE THE COOLDOWN AND COST ACTUALLY ARE, resolved through the Blueprint chain
            // rather than taken from what this tier serialises. T2..T6 carry neither and inherit
            // both from T1, so a version that read only this asset would report a family whose
            // upper tiers have no cooldown at all.
            //
            // The EXPORT NAME IS EMPTY on purpose, and getting that wrong is what the first
            // version did: these are class defaults, so the export to read is the CDO at every
            // step of the chain, and `Describe` uses the CDO exactly when it is given no name.
            // Passing the Reading's own name -- the leaf, `GA_RocketStrike_T1` -- matched no
            // export at all, so T1 reported NO cooldown and no cost and both of its effects fell
            // through into the results. The upper tiers looked right only because they carry
            // neither and the walk up to T1 does pass an empty name.
            var cooldown = PackageOf(ClassReferences
                .Describe(provider, pkg, "", "CooldownGameplayEffectClass").Inherited);
            var cost = PackageOf(ClassReferences
                .Describe(provider, pkg, "", "CostGameplayEffectClass").Inherited);

            // Everything else it applies is a RESULT: the damage, the status, whatever it does.
            // Taken by subtraction rather than by name, because a family that applies two result
            // effects is ordinary and a list of the ones we thought of is not.
            var results = read.Effects.Select(PackageOf)
                .Where(x => x.Length > 0 &&
                            !string.Equals(x, cooldown, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(x, cost, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            Take(read, pkg, RoleAbility);
            if (cooldown.Length > 0) Take(AbilityEffects.Read(provider, cooldown), cooldown, RoleCooldown);
            if (cost.Length > 0) Take(AbilityEffects.Read(provider, cost), cost, RoleCost);
            foreach (var r in results) Take(AbilityEffects.Read(provider, r), r, RoleResult);

            // THE UNLOCK ROW, which is the half that is not an asset property at all. Level, focus
            // and focus cost live on the specialization's rung, which is why they were editable
            // in a different screen from everything else in this table.
            var spec = "";
            if (rungs.TryGetValue(entry.Name, out var got))
            {
                spec = got.Spec;
                Unlock("MinLevel", "Level", got.Rung.MinLevel, "roster level this rung needs");
                Unlock("FocusLevel", "Focus", got.Rung.FocusLevel, "how deep in the tree it sits");
                Unlock("FocusCost", "Focus cost", got.Rung.FocusCost, "focus points to buy it");
            }

            tiers.Add(new Tier(split.Tier, pkg, cooldown, cost, results, spec,
                               spec.Length > 0 ? rungs[entry.Name].Rung : null, cells));

            void Unlock(string path, string label, int value, string detail)
            {
                var key = RoleUnlock + "|" + path;
                columns.TryAdd(key, new Column(key, label, RoleUnlock, detail));
                cells[key] = new Cell(spec, "", path, value, true, false, 0, "");
            }

            void Take(AbilityEffects.Reading from, string package, string role)
            {
                if (from.Why is not null) return;
                foreach (var n in from.Numbers)
                {
                    var key = role + "|" + n.Path;
                    if (!columns.ContainsKey(key))
                    {
                        // "Damage" twice in one effect is normal -- two calculation modifiers --
                        // and two columns with one name is a table nobody can read.
                        var label = n.Label;
                        var seen = labelsSeen.GetValueOrDefault(role + "|" + label);
                        labelsSeen[role + "|" + label] = seen + 1;
                        if (seen > 0) label = $"{label} #{seen + 1}";
                        columns[key] = new Column(key, label, role, n.Detail);
                    }
                    // FIRST writer wins within a tier: an ability and its effect can both declare
                    // StackLimitCount, and the role in the key already keeps those apart.
                    cells.TryAdd(key, new Cell(package, n.Export, n.Path, n.Value, n.Stored,
                                               n.Add, 0, n.Note));
                }
            }
        }

        if (tiers.Count == 0) return new Family(root, [], [], $"none of {root}'s tiers could be read");

        return new Family(root, WithSharing(tiers), Ordered(columns.Values), null);
    }

    /// <summary>
    /// Fill in <see cref="Cell.SharedWith"/>: how many OTHER tiers use the same asset for a column.
    ///
    /// Counted per column and per package rather than per tier pair, because that is the question
    /// an author is really asking -- "if I change this, what else moves?" -- and the answer is the
    /// number of rows that resolve to the same file.
    /// </summary>
    private static List<Tier> WithSharing(List<Tier> tiers)
    {
        var users = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tiers)
            foreach (var (key, cell) in t.Cells)
                users[key + "\0" + cell.Package] = users.GetValueOrDefault(key + "\0" + cell.Package) + 1;

        return tiers.Select(t => t with
        {
            Cells = t.Cells.ToDictionary(
                kv => kv.Key,
                kv => kv.Value with
                {
                    SharedWith = users.GetValueOrDefault(kv.Key + "\0" + kv.Value.Package) - 1,
                },
                StringComparer.Ordinal),
        }).ToList();
    }

    /// <summary>
    /// The columns in the order an author reads them: what it costs to get, then what it does.
    ///
    /// Daelen's own order -- "Level, Focus, Cost, then adding Resource Cost, Uses per Combat,
    /// Cooldown, Damage" -- which is the order somebody planning a progression thinks in, not the
    /// order the properties happen to be declared in.
    /// </summary>
    private static List<Column> Ordered(IEnumerable<Column> columns)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RoleUnlock] = 0, [RoleCost] = 1, [RoleCooldown] = 2,
            [RoleResult] = 3, [RoleAbility] = 4,
        };
        return columns
            .OrderBy(c => rank.GetValueOrDefault(c.Role, 9))
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ------------------------------------------------------------------ writing one cell back

    /// <summary>The property on an ability that names each kind of effect it uses.</summary>
    private static string SlotFor(string role) => role switch
    {
        RoleCooldown => "CooldownGameplayEffectClass",
        RoleCost => "CostGameplayEffectClass",
        _ => "",
    };

    /// <summary>What a cell reads as RIGHT NOW, counting what the mod has already changed.</summary>
    /// <param name="Edited">The mod changes this cell; <paramref name="Value"/> is the mod's number.</param>
    /// <param name="OwnCopy">The id of this tier's own copy, when the mod has given it one.</param>
    public sealed record Showing(double Value, bool Edited, string OwnCopy);

    /// <summary>
    /// What one cell should DRAW as, which is not the same as what the game ships.
    ///
    /// A table that showed only the pristine value would say 0 for a cell the author set to 2 a
    /// moment ago, and the edit would look like it had not landed. Worse, it would keep offering
    /// "give this tier its own" for a tier that already has one.
    ///
    /// So the mod is read back the same way the build will: if this tier has been pointed at its
    /// own copy, the value lives on the COPY; otherwise on an override of the shared asset. Both
    /// carry two lists, because a number that is present is CHANGED and one that is absent is
    /// ADDED, and those are different edits in different places.
    /// </summary>
    public static Showing Current(ModProject mod, Tier tier, Column column)
    {
        if (!tier.Cells.TryGetValue(column.Key, out var cell))
            return new Showing(0, false, "");

        // POINTED SOMEWHERE ELSE? Then that is where the number is, and the pristine asset's value
        // is no longer this tier's at all.
        var slot = SlotFor(column.Role);
        var own = "";
        if (slot.Length > 0)
        {
            var abilityCdo = "Default__" + Leaf(tier.Ability) + "_C";
            own = mod.Overrides
                .FirstOrDefault(o => Same(o.Target, tier.Ability))?.AddClasses
                .FirstOrDefault(a => Same(a.Export, abilityCdo) && Same(a.Name, slot))?.Target ?? "";
        }

        if (own.Length > 0 &&
            mod.Copies.FirstOrDefault(c => Same(c.Id, own)) is { } copy)
        {
            var n = copy.Numbers.FirstOrDefault(x => Same(x.Row, cell.Export) && Same(x.Field, cell.Path));
            if (n is not null) return new Showing(n.Now, true, own);
            var a = copy.AddScalars.FirstOrDefault(x => Same(x.Export, cell.Export) && Same(x.Name, cell.Path));
            if (a is not null) return new Showing(a.Value, true, own);
            return new Showing(cell.Value, false, own);
        }

        if (mod.Overrides.FirstOrDefault(o => Same(o.Target, cell.Package)) is { } ov)
        {
            var n = ov.Numbers.FirstOrDefault(x => Same(x.Row, cell.Export) && Same(x.Field, cell.Path));
            if (n is not null) return new Showing(n.Now, true, "");
            var a = ov.AddScalars.FirstOrDefault(x => Same(x.Export, cell.Export) && Same(x.Name, cell.Path));
            if (a is not null) return new Showing(a.Value, true, "");
        }
        return new Showing(cell.Value, false, "");
    }

    /// <summary>Undo one cell: take the mod's edit out and leave the game's value.</summary>
    public static void Clear(ModProject mod, Tier tier, Column column)
    {
        if (!tier.Cells.TryGetValue(column.Key, out var cell)) return;
        var showing = Current(mod, tier, column);

        if (showing.OwnCopy.Length > 0 &&
            mod.Copies.FirstOrDefault(c => Same(c.Id, showing.OwnCopy)) is { } copy)
        {
            copy.Numbers.RemoveAll(x => Same(x.Row, cell.Export) && Same(x.Field, cell.Path));
            copy.AddScalars.RemoveAll(x => Same(x.Export, cell.Export) && Same(x.Name, cell.Path));

            // A COPY THAT NOW CHANGES NOTHING is a copy of a game asset for no reason, which the
            // build refuses as a loose copy -- so the pointer to it goes too, and the tier falls
            // back to whatever the game had it using.
            if (copy.Numbers.Count == 0 && copy.AddScalars.Count == 0)
            {
                mod.Copies.Remove(copy);
                var slot = SlotFor(column.Role);
                var abilityCdo = "Default__" + Leaf(tier.Ability) + "_C";
                foreach (var o in mod.Overrides.Where(o => Same(o.Target, tier.Ability)))
                    o.AddClasses.RemoveAll(a => Same(a.Export, abilityCdo) && Same(a.Name, slot) &&
                                                Same(a.Target, showing.OwnCopy));
            }
            return;
        }

        foreach (var ov in mod.Overrides.Where(o => Same(o.Target, cell.Package)))
        {
            ov.Numbers.RemoveAll(x => Same(x.Row, cell.Export) && Same(x.Field, cell.Path));
            ov.AddScalars.RemoveAll(x => Same(x.Export, cell.Export) && Same(x.Name, cell.Path));
        }
    }

    /// <summary>What an edit did, so the caller can say it rather than guess.</summary>
    public sealed record Done(string Summary, string Package, string CopyId);

    /// <summary>
    /// Put <paramref name="value"/> into one cell of the table, as project entries.
    ///
    /// THREE OUTCOMES, and which one happens is the whole reason this is not a setter:
    ///
    ///  * the cell is this tier's alone -- an override on that asset, a number CHANGED if the
    ///    asset carries one and a property ADDED if it does not,
    ///  * the cell is shared and <paramref name="ownCopy"/> is false -- the same edit, and it
    ///    moves every tier that names the asset. Legitimate, and the caller has to have said so,
    ///  * the cell is shared and <paramref name="ownCopy"/> is true -- the asset is COPIED into
    ///    the mod, the value goes on the copy, and this tier alone is pointed at it.
    ///
    /// That last one is the answer to the thing Daelen actually asked for, and it is three project
    /// entries an author would otherwise have to know to write by hand: a copy, a property added
    /// to the copy, and a class-valued property on the ability pointing at it. It is exactly the
    /// shape of the RocketTiers probe that proved `addClasses` in the first place.
    ///
    /// Only the cooldown and the cost can be given a copy, because those are the two an ability
    /// names through a property of its own. A RESULT effect is one entry in an array, and moving
    /// one tier onto its own copy of that means editing the array -- refused here with a reason
    /// rather than half-done.
    /// </summary>
    public static Done? Apply(ModProject mod, Tier tier, Column column, double value,
                              bool ownCopy, out string? why)
    {
        why = null;
        if (!tier.Cells.TryGetValue(column.Key, out var cell))
        { why = $"tier {tier.Index} has no {column.Label}"; return null; }

        if (column.Role == RoleUnlock)
        {
            // Level, focus and focus cost are rungs on the SPECIALIZATION, not properties on an
            // asset, and they are edited where a mod's own copy of that spec is edited. Saying so
            // beats writing an override that would do nothing.
            why = $"{column.Label} is a rung on {Leaf(cell.Package)}. Duplicate that " +
                  "specialization into this mod on the Specs tab and edit its focus tree there.";
            return null;
        }

        if (ownCopy && cell.SharedWith == 0)
        { why = $"{column.Label} is already this tier's alone"; return null; }

        if (!ownCopy)
        {
            SetOn(mod, cell.Package, cell.Export, cell.Path, cell.Value, value, cell.Addable);
            return new Done(
                cell.SharedWith == 0
                    ? $"{column.Label} = {value:0.###} on {Leaf(cell.Package)}"
                    : $"{column.Label} = {value:0.###} on {Leaf(cell.Package)} -- which is " +
                      $"{cell.SharedWith + 1} tiers, not just this one",
                cell.Package, "");
        }

        var slot = SlotFor(column.Role);
        if (slot.Length == 0)
        { why = $"{AbilityProgression.RoleName(column.Role)} is not something an ability names " +
                "through a property of its own, so this tier cannot be given a copy of it"; return null; }

        // The copy is named for the template AND the tier, which is what keeps six of them apart.
        // Not `<template>_<n>`: an id ending in an underscore and digits is read by UE as an FName
        // NUMBER and the package mounts without resolving -- see the FName suffix note.
        var copyId = $"{Leaf(cell.Package)}_T{tier.Index}";
        var copy = mod.Copies.FirstOrDefault(c => string.Equals(c.Id, copyId, StringComparison.OrdinalIgnoreCase));
        if (copy is null)
        {
            copy = new ModCopy { Id = copyId, Template = cell.Package, NewGuid = true };
            mod.Copies.Add(copy);
        }

        // ON THE TEMPLATE'S EXPORT NAME. A copy does not rename its exports, so three copies of
        // GE_Cooldown_RocketStrike are three packages all still holding
        // Default__GE_Cooldown_RocketStrike_C.
        if (cell.Addable)
        {
            copy.Numbers.RemoveAll(x => Same(x.Row, cell.Export) && Same(x.Field, cell.Path));
            copy.AddScalars.RemoveAll(x => Same(x.Export, cell.Export) && Same(x.Name, cell.Path));
            copy.AddScalars.Add(new ModOverride.ScalarAdd(cell.Export, cell.Path, value));
        }
        else
        {
            copy.AddScalars.RemoveAll(x => Same(x.Export, cell.Export) && Same(x.Name, cell.Path));
            copy.Numbers.RemoveAll(x => Same(x.Row, cell.Export) && Same(x.Field, cell.Path));
            copy.Numbers.Add(new ModOverride.NumberChange(cell.Export, cell.Path, cell.Value, value));
        }

        // And the tier points at it. The export here is the ABILITY's CDO, not the effect's.
        var ov = OverrideFor(mod, tier.Ability);
        var abilityCdo = "Default__" + Leaf(tier.Ability) + "_C";
        ov.AddClasses.RemoveAll(x => Same(x.Export, abilityCdo) && Same(x.Name, slot));
        ov.AddClasses.Add(new ModOverride.ClassAdd(abilityCdo, slot, copyId));

        return new Done($"tier {tier.Index} now has its own {copyId}, with {column.Label} = " +
                        $"{value:0.###}; the other tiers keep {Leaf(cell.Package)}",
                        cell.Package, copyId);
    }

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The mod's override for one package, made if it has none yet.</summary>
    private static ModOverride OverrideFor(ModProject mod, string package)
    {
        var had = mod.Overrides.FirstOrDefault(o => Same(o.Target, package));
        if (had is not null) return had;
        var made = new ModOverride { Target = package };
        mod.Overrides.Add(made);
        return made;
    }

    /// <summary>
    /// One number onto one asset, as the right KIND of edit.
    ///
    /// A number the asset carries is CHANGED -- four bytes over four -- and one it does not is
    /// ADDED, which rebuilds the export's unversioned header. They are different edits, they live
    /// in different lists, and putting one in the other's list is a build that skips with a reason
    /// about a property that is not there.
    /// </summary>
    private static void SetOn(ModProject mod, string package, string export, string path,
                              double was, double now, bool addable)
    {
        var ov = OverrideFor(mod, package);
        ov.Numbers.RemoveAll(x => Same(x.Row, export) && Same(x.Field, path));
        ov.AddScalars.RemoveAll(x => Same(x.Export, export) && Same(x.Name, path));
        if (addable) ov.AddScalars.Add(new ModOverride.ScalarAdd(export, path, now));
        else ov.Numbers.Add(new ModOverride.NumberChange(export, path, was, now));
    }

    /// <summary>What to call a role on screen.</summary>
    public static string RoleName(string role) => role switch
    {
        RoleUnlock => "to unlock",
        RoleAbility => "the ability",
        RoleCooldown => "cooldown effect",
        RoleCost => "cost effect",
        RoleResult => "result effect",
        _ => role,
    };
}

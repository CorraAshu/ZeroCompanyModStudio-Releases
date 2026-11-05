using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;

namespace UEToolkit.Core;

/// <summary>
/// What an ability actually DOES, as numbers somebody can change.
///
/// Asked for by a user of the released tool, 2026-09-08: "wondering if we could get access to GA_
/// assets directly so we can alter progression/effects of the abilities themselves instead of
/// needing retoc." The answer is that no unpacking is needed, because an ability is DATA -- but
/// finding the data takes two hops and the interesting numbers sit inside an array.
///
/// **THE SHAPE, read off the game rather than assumed.**
///
/// A <c>GA_</c> asset is a wrapper. Its class default object carries <c>ApplyEffectClasses[]</c>,
/// one entry per effect the ability applies, each naming a <c>GE_</c> class and saying how many
/// <c>Stacks</c> of it to apply. The ability itself holds no magnitudes at all -- which is why
/// pointing somebody at "the GA_ asset" and stopping there would have them editing a signpost.
///
/// A <c>GE_</c> asset is the gameplay. <c>DurationPolicy</c> says whether it is instant, lasts a
/// number of turns, or never ends; <c>DurationMagnitude</c> is that number; and
/// <c>Modifiers[]</c> holds one struct per attribute it changes -- the attribute's name, whether
/// it adds or multiplies, and the magnitude.
///
/// **AND A THIRD SHAPE, which reading one asset would have missed entirely.** Anything that deals
/// DAMAGE does not use Modifiers at all: it carries <c>Executions[]</c>, each naming a calculation
/// class and holding <c>CalculationModifiers[]</c> -- an array INSIDE an array element, two deep.
/// The first version of this read `Modifiers` only and reported that every damage effect in the
/// game had no numbers, which is exactly the shape of answer that reads like a fact. The sweep
/// over all 2,000-odd of them is what said otherwise.
///
/// **WHY THIS COULD NOT BE DONE BEFORE.** Every magnitude is inside an array element, and
/// <see cref="PropertyAdd.Locate"/> walked names only. Indexing was added for exactly this, and it
/// is what makes an ability reachable by the SAME override machinery that already edits weapons
/// and specialisations -- no new build path, no new file format, nothing to prove twice.
///
/// **THREE WAYS A NUMBER IS NOT WHAT IT LOOKS LIKE**, all of which this reports rather than
/// letting an author discover at build time:
///
///  1. **A curve table decides it.** <c>GE_PersonalDefenseShield_T1</c> reads a magnitude of 0
///     with <c>Curve.RowName = "Defense Increase"</c> beside it: the number in the asset is
///     ignored and the real value is a row in a curve table. Changing the 0 changes nothing.
///  2. **The calculation is not a scalable float.** A magnitude can be derived from another
///     attribute, set by the caller at runtime, or computed by a class. The scalable value is
///     still serialised and still editable, and still has no effect.
///  3. **Zero is not stored.** A cooked package leaves a zero-valued property out entirely, so a
///     magnitude that reads 0 has no bytes to overwrite. Raising one is a bigger edit than
///     changing one, and the difference is worth saying out loud.
///
/// Nothing here writes. It reads, and hands back paths the existing override editor already knows
/// how to write through.
/// </summary>
public static class AbilityEffects
{
    /// <summary>
    /// One number an author could change, and everything needed to change it: which export, the
    /// dotted path inside it, and what it currently reads.
    ///
    /// <paramref name="Note"/> is empty when the number does what it appears to do. When it is
    /// not empty it is the reason changing it may not, in the author's words rather than the
    /// engine's.
    /// </summary>
    /// <param name="Add">
    /// The asset does not carry this property at all and setting it ADDS one.
    ///
    /// Not the same as a stored zero and not the same as an unreachable one, which is why it is
    /// its own flag rather than a shade of <paramref name="Stored"/>. A cooked package serialises
    /// only what differs from the class default, so a property at its default is missing from the
    /// file entirely -- and the class still declares it. That is where the cooldown request went:
    /// <c>GE_Cooldown_RocketStrike</c> reads as a tag and nothing else, while its class declares
    /// <c>UsesPerCooldown</c> first of all, and eighteen of the game's own cooldown effects set
    /// that very property to 2, 3 or 4.
    ///
    /// The build writes these through <see cref="ModOverride.AddScalars"/> rather than
    /// <see cref="ModOverride.Numbers"/>: one rebuilds the export's unversioned header, the other
    /// writes four bytes over four.
    /// </param>
    /// <param name="Whole">
    /// The property is an integer (an IntProperty), so a fraction cannot be stored in it: typed
    /// 2.5, it would read back 2. Known from the property's own type, never from how its value
    /// looks -- a float damage of 15 is a whole VALUE and still takes 15.5. A screen refuses a
    /// fraction in such a box where it is typed; the build refuses it anyway, but later and
    /// without saying what the author did wrong.
    /// </param>
    public sealed record Number(string Export, string Path, string Label, string Detail,
                                double Value, string Note, bool Stored, bool Add = false,
                                bool Whole = false);

    /// <summary>
    /// One asset's worth of ability data: what it is, what it applies, and what can be changed.
    ///
    /// <paramref name="Effects"/> is the ability's onward references -- the GE_ packages it
    /// applies -- because an author who opens a GA_ and finds three numbers needs to be told
    /// where the rest of them live rather than concluding there are none.
    /// </summary>
    /// <summary>
    /// A CLASS-VALUED property the asset could be given: which one, what it uses today, and what
    /// it may be pointed at.
    ///
    /// Kept apart from <see cref="Number"/> because it is not one. A number is typed in; this is
    /// chosen from a list the game's own registry decides -- see <see cref="ClassReferences"/> --
    /// and the thing an author most needs to see is what it ALREADY uses, which for an inherited
    /// property is not written in the asset at all.
    /// </summary>
    public sealed record Reference(string Export, string Name, string Label,
                                   ClassReferences.Slot Slot);

    /// <summary>
    /// A TAG CONTAINER the asset could be given: which one, and what the chain holds today.
    ///
    /// Its own kind for the same reason <see cref="Reference"/> is: a number is typed in, a class
    /// is chosen from a narrowed list, and a container is a SET of tags that may already have
    /// members further up the chain. That last part is why the slot is carried rather than just
    /// the name -- adding `WeaponTypeTags` to one tier when the base tier already has three
    /// replaces all three for that tier, and an author who cannot see them will not expect it.
    /// </summary>
    public sealed record Container(string Export, string Name, string Label,
                                   ClassReferences.TagSlot Slot);

    public sealed record Reading(string Package, string Name, string Kind, string Summary,
                                 List<Number> Numbers, List<string> Effects, string? Why)
    {
        /// <summary>Class-valued properties this asset does not carry and could. See <see cref="Reference"/>.</summary>
        public List<Reference> References { get; init; } = new();

        /// <summary>Tag containers this asset does not carry and could. See <see cref="Container"/>.</summary>
        public List<Container> Containers { get; init; } = new();

        /// <summary>What the game calls it on screen. Empty for an effect, which has no UI of its own.</summary>
        public string Title { get; init; } = "";

        /// <summary>The tooltip the player reads, keyword markup and all.</summary>
        public string Description { get; init; } = "";

        /// <summary>The ability's own tag -- br.AbilityID.* -- which is how the game names it.</summary>
        public string Id { get; init; } = "";
    }

    /// <summary>Whether this package name is an ability wrapper.</summary>
    public static bool IsAbility(string package) =>
        Leaf(package).StartsWith("GA_", StringComparison.Ordinal);

    /// <summary>Whether this package name is a gameplay effect -- where the numbers are.</summary>
    public static bool IsEffect(string package) =>
        Leaf(package).StartsWith("GE_", StringComparison.Ordinal);

    /// <summary>The asset name at the end of a package path.</summary>
    public static string Leaf(string package) =>
        package.Length == 0 ? "" : package[(package.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Read one ability or effect.
    ///
    /// PRISTINE, like every other authoring read: the Studio mounts the author's own installed
    /// mods beside the game, and a plain provider lookup finds a mod's copy of an asset it has
    /// overridden. Reading an ability the author has already modified as if it were the game's is
    /// how a second edit gets built on top of the first -- see
    /// <see cref="AssetCopy.PristineExports"/>.
    /// </summary>
    public static Reading Read(IFileProvider provider, string package)
    {
        var name = Leaf(package);
        var numbers = new List<Number>();
        var effects = new List<string>();

        // Through PropertyAdd rather than AssetCopy, because this needs IMPORTS RESOLVED: an
        // ability names its effects with hard object references, and those resolve only when the
        // package is opened with its container header. Opened without one the ability read
        // perfectly and reported "applies no effects" -- true of nothing in the game.
        var raw = AssetCopy.PristineRead(provider, package, out var readWhy)?.Pieces
                           .FirstOrDefault(x => x.IsPackage)?.Data;
        if (raw is null)
            return new Reading(package, name, "", "", numbers, effects,
                               readWhy ?? $"{name} could not be read out of the game");

        var exports = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, raw, package));
        if (exports.Length == 0)
            return new Reading(package, name, "", "", numbers, effects,
                               $"{name} could not be read out of the game");

        // THE CLASS DEFAULT OBJECT, not the class. A Blueprint package holds the generated class,
        // its functions, and one export holding the values -- named Default__<Class>. Everything
        // an author wants to change is on that one, and taking whichever export came first would
        // land on the class, which carries none of it.
        var cdo = exports.FirstOrDefault(
            e => e is not null &&
                 e.Name.StartsWith("Default__", StringComparison.Ordinal));
        if (cdo is null)
            return new Reading(package, name, "", "", numbers, effects,
                               $"{name} has no Default__ export, so it carries no values");

        var kind = IsEffect(package) ? "effect" : IsAbility(package) ? "ability" : "asset";
        var props = cdo.Properties;
        var summary = "";
        var title = "";
        var description = "";
        var id = "";

        if (kind == "ability")
        {
            // Each entry names an effect and says how many stacks of it to apply. The stack count
            // is the one number an ability itself owns.
            if (PartDetails.Unwrap(PartDetails.Value(props, "ApplyEffectClasses")) is UScriptArray applies)
                for (var i = 0; i < applies.Properties.Count; i++)
                {
                    if (PartDetails.Unwrap(applies.Properties[i].GenericValue) is not FStructFallback one)
                        continue;
                    var effect = PathOf(PartDetails.Value(one.Properties, "GameplayEffect"));
                    if (effect.Length > 0) effects.Add(effect);

                    var stacks = PartDetails.Value(one.Properties, "Stacks");
                    if (stacks is not null && Numeric(stacks) is { } n)
                        numbers.Add(new Number(cdo.Name, $"ApplyEffectClasses[{i}].Stacks",
                                               "Stacks",
                                               Leaf(AssetLoader.PackagePath(effect) ?? effect),
                                               n, "", Math.Abs(n) > 0.0001,
                                               Whole: IsWhole(stacks)));
                }

            foreach (var also in new[] { "CostGameplayEffectClass", "CooldownGameplayEffectClass" })
                if (PathOf(PartDetails.Value(props, also)) is { Length: > 0 } path)
                    effects.Add(path);

            summary = effects.Count == 0
                ? "applies no effects"
                : $"applies {effects.Count} effect(s)";

            // HOW OFTEN IT CAN BE USED, which is a TAG and not a number -- the thing three days
            // of looking for a cooldown value would have found last. PerTurn, PerEncounter,
            // PerMission and Move are the four the game uses, on 52 of the abilities that name
            // one at all. How MANY uses each of those allows is a separate property on the
            // cooldown effect, and that one is a number: see the absent-scalar block below.
            if (Tag(props, "PerPlayCooldownType") is { Length: > 0 } when &&
                !when.Equals("None", StringComparison.OrdinalIgnoreCase))
                summary += ", " + Cadence(Short(when));

            // WHAT THE PLAYER CALLS IT. An asset name is not it: GA_Firestorm_T1 is "Firestorm",
            // and the tier suffix is the rung of the focus tree it sits on. Listing abilities by
            // asset name would be listing them by a name nobody in the game has ever seen.
            if (PartDetails.Unwrap(PartDetails.Value(props, "UIData")) is FStructFallback ui)
            {
                title = Text(ui.Properties, "Name");
                description = Text(ui.Properties, "Description");
            }
            id = Tag(props, "AbilityID");
        }

        if (kind == "effect")
        {
            var policy = Enum(props, "DurationPolicy");
            summary = policy.Length > 0 ? policy : "instant";

            // WHAT "INFINITE, -1" MEANS, said in words rather than left as an engine enumerator.
            //
            // This is the half of the cooldown request that is not a number and never will be. A
            // cooldown effect stops an ability by putting a tag on the character; how long the tag
            // stays is the policy, and 22 of the game's 69 cooldown effects say Infinite with a
            // duration of -1, which reads like a value somebody forgot to fill in and is in fact
            // the marker for "until the encounter ends". Reporting "Infinite, 0 numbers found" was
            // technically true and told an author nothing, which is how a feature gets reported
            // as broken when it is only silent.
            if (policy.Contains("Infinite", StringComparison.OrdinalIgnoreCase))
                summary = "lasts until it is removed -- for a cooldown, the rest of the encounter";
            else if (policy.Length == 0 || policy.Contains("Instant", StringComparison.OrdinalIgnoreCase))
                summary = "instant -- it applies once and is gone";

            // HOW LONG IT LASTS. Only meaningful when the effect actually has a duration -- an
            // infinite one stores -1 here as a marker and raising it would end the effect.
            if (policy.Contains("HasDuration", StringComparison.OrdinalIgnoreCase) &&
                Scalable(props, "DurationMagnitude") is { } dur)
                numbers.Add(dur with
                {
                    Export = cdo.Name,
                    Path = "DurationMagnitude." + dur.Path,
                    Label = "How long it lasts",
                    Detail = "turns",
                });

            if (Scalable(props, "Period") is { } period)
                numbers.Add(period with
                {
                    Export = cdo.Name,
                    Path = "Period." + period.Path,
                    Label = "How often it ticks",
                    Detail = "turns between applications",
                });

            if (PartDetails.Unwrap(PartDetails.Value(props, "Modifiers")) is UScriptArray mods)
                for (var i = 0; i < mods.Properties.Count; i++)
                {
                    if (PartDetails.Unwrap(mods.Properties[i].GenericValue) is not FStructFallback one)
                        continue;

                    var attribute = "an attribute";
                    if (PartDetails.Unwrap(PartDetails.Value(one.Properties, "Attribute"))
                        is FStructFallback attr &&
                        PartDetails.Value(attr.Properties, "AttributeName")?.ToString() is
                        { Length: > 0 } named)
                        attribute = named;

                    var op = Enum(one.Properties, "ModifierOp");
                    if (op.Length == 0) op = "Additive";

                    if (Scalable(one.Properties, "ModifierMagnitude") is not { } mag) continue;
                    numbers.Add(mag with
                    {
                        Export = cdo.Name,
                        Path = $"Modifiers[{i}].ModifierMagnitude.{mag.Path}",
                        Label = attribute,
                        Detail = op.ToLowerInvariant(),
                    });
                }

            // DAMAGE. An execution runs a calculation class and feeds it modifiers, and those
            // modifiers are where a weapon's or an ability's damage number actually is. Two array
            // levels deep, which is the whole reason indexing had to nest.
            if (PartDetails.Unwrap(PartDetails.Value(props, "Executions")) is UScriptArray runs)
                for (var i = 0; i < runs.Properties.Count; i++)
                {
                    if (PartDetails.Unwrap(runs.Properties[i].GenericValue) is not FStructFallback run)
                        continue;
                    if (PartDetails.Unwrap(PartDetails.Value(run.Properties, "CalculationModifiers"))
                        is not UScriptArray calcs) continue;

                    for (var j = 0; j < calcs.Properties.Count; j++)
                    {
                        if (PartDetails.Unwrap(calcs.Properties[j].GenericValue)
                            is not FStructFallback one) continue;
                        if (Scalable(one.Properties, "ModifierMagnitude") is not { } mag) continue;

                        var label = Short(Tag(one.Properties, "TransientAggregatorIdentifier"));
                        if (label.Length == 0) label = Captured(one.Properties);
                        if (label.Length == 0) label = "a calculation";

                        var op = Enum(one.Properties, "ModifierOp");
                        if (op.Length == 0) op = "Additive";

                        // WHEN it applies. A damage effect carries one calculation modifier per
                        // weapon kind, gated by a source tag, and they are otherwise identical --
                        // so without this the list reads as seven rows called "Damage".
                        var when = Requires(one.Properties, "SourceTags");
                        if (when.Length == 0) when = Requires(one.Properties, "TargetTags");

                        numbers.Add(mag with
                        {
                            Export = cdo.Name,
                            Path = $"Executions[{i}].CalculationModifiers[{j}].ModifierMagnitude.{mag.Path}",
                            Label = label,
                            Detail = when.Length > 0
                                     ? $"{op.ToLowerInvariant()}, {when}"
                                     : op.ToLowerInvariant(),
                        });
                    }
                }

        }

        // ------------------------------------------------------------------ its OWN numbers
        //
        // Everything above reads a KNOWN SHAPE -- a magnitude inside a modifier, a stack count
        // inside an apply entry. This reads whatever else is there, and a sweep is what showed how
        // much that is: GrenadeBlastRadius, GrenadeBaseDamage, BlastBaseDamage, MaxThrowRange,
        // OverwatchShotCount, "Total Shots", "Base Stacks", MinRosterLevel -- hundreds of plain
        // numbers sitting at the top of ability class defaults that nothing here listed, because
        // the reader only ever looked where it already knew to look.
        //
        // BY THE PROPERTY'S TYPE, never by what the value looks like. An object reference reads
        // back as a number -- CooldownGameplayEffectClass came out of a dump as "0" -- and offering
        // an author a box that overwrites a class pointer with a float is the worst kind of edit
        // this tool could ship.
        foreach (var tag in props)
        {
            if (tag.Name.Text is not { Length: > 0 } own) continue;
            if (!IsScalar(tag.PropertyType.Text)) continue;
            if (tag.ArraySize is > 1) continue;                  // a fixed-size C array, not one value
            if (numbers.Any(n => string.Equals(n.Path, own, StringComparison.Ordinal))) continue;
            if (Numeric(PartDetails.Unwrap(tag.Tag?.GenericValue)) is not { } value) continue;

            // ZERO IS PRESENT-BUT-EMPTY, not missing, and the difference decides which edit it
            // takes. A property serialised as zero occupies a header slot and no bytes, so there
            // is nothing to write over -- but the header can say it has a value, which is the
            // same splice as adding one. So a zero goes down the ADD path and everything else
            // over the bytes it already has.
            var has = Math.Abs(value) > 0.0001;
            numbers.Add(new Number(cdo.Name, own, Spaced(own), Explain(own), value, "", has, !has,
                                   Whole: tag.PropertyType.Text == "IntProperty"));
        }

        // ------------------------------------------------------- numbers it does not carry YET
        //
        // THE ANSWER TO "MAKE THE COOLDOWN EFFECTS EDITABLE", and it took believing that a "no"
        // needed proving. GE_Cooldown_RocketStrike carries one tag and a -1: read it and there is
        // nothing to change, which is what this reported. But a cooked package leaves out every
        // property still at its class default, and that class declares UsesPerCooldown as its
        // FIRST property -- the one 18 of the game's own cooldown effects set to 2, 3 or 4, with
        // Medkit_T2 at 2 beside Medkit_T3 at 3. A tiered increase in uses per encounter is not
        // only possible, it is how the game already does it.
        //
        // Scalars only, and that keeps the list short and honest: of the 40 properties a gameplay
        // effect class declares, all but a handful are structs, arrays, tag containers and enums,
        // none of which this can add.
        var carried = props.Select(t => t.Name.Text).Where(x => x is { Length: > 0 })
                           .ToHashSet(StringComparer.Ordinal);

        // A CLASS POINTER CARRIED AS NULL IS NOT THE SAME AS ONE THAT IS ABSENT, and it is not the
        // same as one that is set. It is how this game TURNS A THING OFF for one tier:
        //
        //     GA_EasyMark_T1   CooldownGameplayEffectClass = GE_Cooldown_EasyMark_C
        //     GA_EasyMark_T6   CooldownGameplayEffectClass = 0      <- drops the cooldown at max rank
        //
        // And it fell between both sweeps. The one below skips anything the asset CARRIES, and T6
        // carries it; the number sweep above skips anything that is not a SCALAR, and a class
        // pointer is not. So the single property an author asked to put back -- to trade the
        // once-per-turn cooldown against a lower cost -- had no row on the screen at all, and no
        // way to reach it.
        //
        // This is exactly the fault the numbers had, recorded then as "present and zero was refused
        // by BOTH paths for a year", and it takes the same answer: a null pointer is nothing to
        // write over, so it goes down the ADD path beside everything that is absent.
        var emptyClass = props
            .Where(t => t.Name.Text is { Length: > 0 } &&
                        t.PropertyType.Text is "ClassProperty" or "ObjectProperty" &&
                        EmptyReference(PartDetails.Unwrap(t.Tag?.GenericValue)))
            .Select(t => t.Name.Text)
            .ToHashSet(StringComparer.Ordinal);

        var references = new List<Reference>();
        foreach (var (declared, type) in PropertyAdd.Declared(provider, raw, package, cdo.Name, out _))
        {
            if (carried.Contains(declared) && !emptyClass.Contains(declared)) continue;

            // AND THE CLASS-VALUED ONES, which this filtered away for as long as it existed.
            //
            // The scalar filter was right when a number was the only value that could be written.
            // PropertyAdd.AddClass changed that, and the property behind Daelen's request --
            // CooldownGameplayEffectClass, which is how uses-per-combat is reached -- is an
            // ObjectProperty and was therefore invisible in the one screen built to offer it.
            //
            // Only ones the registry can narrow. A slot with no Kind is one where nothing in the
            // Blueprint chain sets the property, so there is no evidence of what belongs in it and
            // a picker would be a list of everything; those are left out rather than guessed at,
            // and the project file still takes a typed path for anyone who knows better.
            if (type is "ClassProperty" or "ObjectProperty")
            {
                var slot = ClassReferences.Describe(provider, package, cdo.Name, declared);
                if (slot.Kind.Length > 0)
                    references.Add(new Reference(cdo.Name, declared, Spaced(declared), slot));
                continue;
            }

            if (!IsScalar(type)) continue;
            if (numbers.Any(n => string.Equals(n.Path, declared, StringComparison.Ordinal))) continue;
            numbers.Add(new Number(cdo.Name, declared, Spaced(declared), Explain(declared),
                                   0, "", false, true, Whole: type == "IntProperty"));
        }

        // AND THE TAG CONTAINERS, the third kind of value an override can now set. Declared
        // separately because Declared() reports one as "StructProperty" and says nothing about
        // WHICH struct -- see PropertyAdd.DeclaredContainers.
        //
        // Only the ones the asset does not already carry: a container that IS serialised is
        // edited through tagEdits, which finds it by its contents, and offering both routes for
        // one property is how an author writes two edits that fight.
        var containers = new List<Container>();
        var declaredContainers = PropertyAdd.DeclaredContainers(provider, raw, package, cdo.Name, out _)
                                            .Where(x => !carried.Contains(x)).ToList();
        if (declaredContainers.Count > 0)
            foreach (var slot in ClassReferences.DescribeTags(provider, package, cdo.Name, declaredContainers))
                containers.Add(new Container(cdo.Name, slot.Property, Spaced(slot.Property), slot));

        if (numbers.Count > 0)
            summary += $", {numbers.Count} number(s)";
        if (references.Count > 0)
            summary += $", {references.Count} thing(s) it points at";
        if (containers.Count > 0)
            summary += $", {containers.Count} tag container(s)";

        return new Reading(package, name, kind, summary, numbers, effects, null)
        {
            References = references,
            Containers = containers,
            Title = title,
            Description = description,
            Id = id,
        };
    }

    /// <summary>
    /// An ability description with the game's own markup taken out.
    ///
    /// The stored text is
    /// <c>dealing &lt;Keyword id="UI.Keyword.Attack.Melee"&gt;Melee Damage&lt;/&gt; and ...</c> --
    /// the game turns those into the underlined words a player can hover. Shown raw they are more
    /// markup than sentence, and the sentence is the whole reason the description is there.
    /// </summary>
    public static string Plain(string text)
    {
        if (text.Length == 0) return text;
        var made = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '<') { depth++; continue; }
            if (c == '>') { if (depth > 0) depth--; continue; }
            if (depth == 0) made.Append(c);
        }
        return made.ToString();
    }

    /// <summary>An FText property as the string a player would see.</summary>
    private static string Text(List<FPropertyTag> props, string name) =>
        PartDetails.Value(props, name) switch
        {
            CUE4Parse.UE4.Objects.Core.i18N.FText t => t.Text ?? "",
            string str => str,
            _ => "",
        };

    /// <summary>
    /// The scalable-float number inside a magnitude struct, with the reasons it might be ignored.
    ///
    /// Returns a <see cref="Number"/> whose Path is RELATIVE to the struct it was found in -- the
    /// caller prefixes it, because only the caller knows whether it came out of an array element.
    /// Label and Detail are the caller's too, for the same reason.
    /// </summary>
    private static Number? Scalable(List<FPropertyTag> props, string structName)
    {
        if (PartDetails.Unwrap(PartDetails.Value(props, structName)) is not FStructFallback outer)
            return null;

        // A magnitude struct carries the scalable float one level down; a plain scalable float
        // (Period) IS the struct. Both shapes appear, so both are looked for.
        var inner = PartDetails.Unwrap(PartDetails.Value(outer.Properties, "ScalableFloatMagnitude"))
                    as FStructFallback ?? outer;
        var prefix = ReferenceEquals(inner, outer) ? "" : "ScalableFloatMagnitude.";

        var value = Numeric(PartDetails.Value(inner.Properties, "Value")) ?? 0d;

        var notes = new List<string>();

        // A CURVE TABLE DECIDES IT. The row name beside the value is what the game actually reads;
        // the value itself is a fallback nothing uses. This is the commonest reason an edit that
        // built cleanly does nothing in game.
        if (PartDetails.Unwrap(PartDetails.Value(inner.Properties, "Curve")) is FStructFallback curve &&
            PartDetails.Value(curve.Properties, "RowName")?.ToString() is { Length: > 0 } row &&
            !row.Equals("None", StringComparison.OrdinalIgnoreCase))
            notes.Add($"a curve table row (\"{row}\") decides this, so changing the number here " +
                      "will not change the ability");

        // NOT A SCALABLE FLOAT AT ALL. The magnitude can be derived from another attribute, handed
        // in at runtime, or computed by a class -- and in each case this number is still there,
        // still editable, and still ignored.
        var how = Enum(outer.Properties, "MagnitudeCalculationType");
        if (how.Length > 0 && !how.Contains("ScalableFloat", StringComparison.OrdinalIgnoreCase))
            notes.Add($"this magnitude is worked out by {Readable(how)}, not by the number here");

        return new Number("", prefix + "Value", "", "", value,
                          string.Join("; ", notes), Math.Abs(value) > 0.0001,
                          Whole: IsWhole(PartDetails.Value(inner.Properties, "Value")));
    }

    /// <summary>
    /// Whether a property type is ONE NUMBER this can read and the build can write.
    ///
    /// The three the writer handles, and no more. A bool is a number and one byte and cannot be
    /// set false at all (unversioned serialisation writes a false as no bytes); a byte-backed enum
    /// is a number whose meaning is an enumerator index, and offering "3" for one is offering
    /// somebody a way to make an asset mean something nobody chose. Both are left out rather than
    /// shown and refused later.
    /// </summary>
    private static bool IsScalar(string? type) =>
        type is "FloatProperty" or "DoubleProperty" or "IntProperty";

    /// <summary>
    /// Whether a carried class or object pointer points at NOTHING.
    ///
    /// Three shapes, because one reader's null is another's zero and this decides whether an
    /// author is offered a row at all. A serialised null arrives as a null reference, as a package
    /// index that resolves to nothing -- which reads back as the number 0, the very thing the
    /// number sweep above warns about -- or as the name "None". Treating only one of those as
    /// empty would leave the other two invisible, which is the fault this exists to fix.
    /// </summary>
    private static bool EmptyReference(object? value) =>
        value is null ||
        (Numeric(value) is { } n && Math.Abs(n) < 0.0001) ||
        (value.ToString() is { } s &&
         (s.Length == 0 || s == "0" || s.Equals("None", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A property name as words: "GrenadeBlastRadius" -> "Grenade blast radius".
    ///
    /// Some of these are already spaced -- a Blueprint variable can be called "Total Shots" and
    /// several are -- so a run of capitals is split and anything already separated is left alone.
    /// </summary>
    private static string Spaced(string name)
    {
        var made = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && name[i - 1] != ' ' &&
                (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                made.Append(' ');
            made.Append(c);
        }
        var text = made.ToString();
        if (text.StartsWith("b ", StringComparison.Ordinal)) text = text[2..];
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }

    /// <summary>
    /// What a property means, for the few whose name does not say it.
    ///
    /// Deliberately short. Every other number gets its own name spaced out, which for
    /// GrenadeBlastRadius is the whole story -- a table of translations for hundreds of properties
    /// would be a table nobody keeps up to date, and a stale explanation is worse than none.
    /// </summary>
    private static string Explain(string name) => name switch
    {
        "UsesPerCooldown"   => "times it can be used before the cooldown applies",
        "StackLimitCount"   => "how many can be on a character at once",
        "MinRosterLevel"    => "roster level before this tier unlocks",
        "OverwatchShotCount" => "shots per overwatch",
        _ => "",
    };

    /// <summary>How often an ability comes back, from the tag that decides it.</summary>
    private static string Cadence(string leaf) => leaf switch
    {
        "PerTurn"      => "usable again each turn",
        "PerEncounter" => "usable again once the encounter ends",
        "PerMission"   => "usable again once the mission ends",
        "Move"         => "spent by moving",
        _ => "cooldown: " + leaf,
    };

    /// <summary>A single-tag struct's tag name, as written.</summary>
    private static string Tag(List<FPropertyTag> props, string name)
    {
        if (PartDetails.Unwrap(PartDetails.Value(props, name)) is not FStructFallback one) return "";
        return PartDetails.Value(one.Properties, "TagName")?.ToString() ?? "";
    }

    /// <summary>The attribute a calculation modifier captures, if it names one.</summary>
    private static string Captured(List<FPropertyTag> props)
    {
        if (PartDetails.Unwrap(PartDetails.Value(props, "CapturedAttribute"))
            is not FStructFallback cap) return "";
        if (PartDetails.Unwrap(PartDetails.Value(cap.Properties, "AttributeToCapture"))
            is not FStructFallback attr) return "";
        return PartDetails.Value(attr.Properties, "AttributeName")?.ToString() ?? "";
    }

    /// <summary>
    /// What a modifier's tag requirement says, in a few words: "only with Rifle".
    ///
    /// The LEAF of each tag, not the whole path. A requirement reads
    /// br.Customization.Part.Character.Specializations.Weapon.Blaster.Rifle, and putting that in a
    /// table column would push everything else off the edge of the panel.
    /// </summary>
    private static string Requires(List<FPropertyTag> props, string group)
    {
        if (PartDetails.Unwrap(PartDetails.Value(props, group)) is not FStructFallback one) return "";
        var tags = PartDetails.TagsOf(one.Properties, "RequireTags")
                              .Select(Short).Where(x => x.Length > 0).ToList();
        return tags.Count == 0 ? "" : "only with " + string.Join(" + ", tags);
    }

    /// <summary>The last piece of a dotted tag name: "Calculation.Damage" -> "Damage".</summary>
    private static string Short(string tag) =>
        tag.Length == 0 ? "" : tag[(tag.LastIndexOf('.') + 1)..];

    /// <summary>An enum property's enumerator name, without the type in front of it.</summary>
    private static string Enum(List<FPropertyTag> props, string name)
    {
        var text = PartDetails.Value(props, name)?.ToString() ?? "";
        var colon = text.LastIndexOf(':');
        return colon >= 0 && colon < text.Length - 1 ? text[(colon + 1)..] : text;
    }

    /// <summary>An engine enumerator name said the way a person would say it.</summary>
    private static string Readable(string enumerator) => enumerator switch
    {
        "AttributeBased" => "another attribute",
        "SetByCaller" => "whatever asks for it, while the game runs",
        "CustomCalculationClass" => "code, not data",
        _ => enumerator,
    };

    /// <summary>An integer as it was read, which is the only evidence of a property's width a value carries.</summary>
    private static bool IsWhole(object? value) => value is int or short or byte or long or uint;

    /// <summary>Any numeric property as a double, whatever width it was stored at.</summary>
    private static double? Numeric(object? value) => value switch
    {
        float f => f,
        double d => d,
        int i => i,
        short s => s,
        byte b => b,
        long l => l,
        uint u => u,
        _ => null,
    };

    /// <summary>An object or class reference as its full path, however it was stored.</summary>
    private static string PathOf(object? value) =>
        PartDetails.SoftPathOf(value) ?? "";
}

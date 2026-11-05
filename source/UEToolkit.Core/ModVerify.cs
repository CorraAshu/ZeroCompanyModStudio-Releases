using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Readers;
using CUE4Parse_Conversion.Textures;

namespace UEToolkit.Core;

/// <summary>
/// Mount what the build just wrote and read it back, through the same code the browser uses.
///
/// This existed before, inside the CLI, as eighty lines of Console.WriteLine -- so the only way to
/// find out whether a mod was right was for me to run a command, and the app a person would
/// actually ship with had none of it. Every fault this builder has ever had was caught by reading
/// the output back and comparing it against something known good; nothing was ever caught by the
/// build agreeing with itself. That makes this the build's last step rather than a diagnostic.
///
/// Two references are compared against, and needing both is the point:
///
///   what was ASKED     the checkbox said hide the head, so the part must hide the head. This is
///                      the half that catches an instruction quietly not being carried out.
///
///   the TEMPLATE       the part was authored by copying one the game ships, so it must still do
///                      what that one does. This is the half that catches inheritance -- the
///                      fragments, slots and references that come along and were never chosen.
///
/// A file that exists is not the claim. A part the reader accepts, saying what it was told to
/// say, is.
/// </summary>
public static class ModVerify
{
    public enum Verdict
    {
        /// <summary>Checked, and right.</summary>
        Good,
        /// <summary>A real difference that is understood and cannot be fixed yet. Say why.</summary>
        Note,
        /// <summary>Wrong. The mod would misbehave in game.</summary>
        Bad,
    }

    public sealed record Check(string What, Verdict Verdict, string Detail);

    public sealed record PartReport(string Id, bool IsTexture, List<Check> Checks)
    {
        public bool Clean => Checks.TrueForAll(c => c.Verdict != Verdict.Bad);
    }

    /// <param name="Examined">
    /// WAS ANYTHING IN THIS MOD ACTUALLY LOOKED AT?
    ///
    /// "Nothing failed" is not the same claim as "it was checked", and Clean has to mean the
    /// second or it is a check that cannot fail. Parts used to be the only way to satisfy that --
    /// which was right while every mod had parts, and wrong the moment a LEVEL EDIT arrived: a map
    /// edit is all overrides and no parts, so Clean was false however well it verified, the
    /// Studio's install-after-build never fired for one, and the only way to get a map into the
    /// game was the Install button, which has never consulted a verdict at all.
    /// </param>
    public sealed record Report(List<PartReport> Parts, List<Check> Overall, bool Examined = false)
    {
        public IEnumerable<Check> All => Overall.Concat(Parts.SelectMany(p => p.Checks));
        public int Bad => All.Count(c => c.Verdict == Verdict.Bad);
        public int Notes => All.Count(c => c.Verdict == Verdict.Note);
        public bool Clean => Bad == 0 && (Parts.Count > 0 || Examined);

        /// <summary>One line for a status bar. Silence would be indistinguishable from not running.</summary>
        public string Summary =>
            !Clean ? $"{Bad} check(s) FAILED"
            : Notes > 0 ? $"read back clean, {Notes} note(s)"
            : "read back clean";
    }

    /// <summary>
    /// Re-open <paramref name="built"/> and check every part in it. Never throws for a bad mod --
    /// a failure to parse IS a result, and the caller wants the other parts checked anyway.
    /// </summary>
    public static Report Run(IFileProvider provider, ModProject mod, ModBuild.Result built)
    {
        var parts = new List<PartReport>();
        var overall = new List<Check>();

        IoStoreReader back;
        try
        {
            back = new IoStoreReader(built.Utoc, EIoStoreTocReadOptions.ReadDirectoryIndex,
                                     provider.Versions);
            back.Mount(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            overall.Add(new Check("the container mounts", Verdict.Bad, e.Message));
            return new Report(parts, overall);
        }

        overall.Add(new Check("the container mounts", Verdict.Good,
                              $"{back.Files.Count} file(s), mount {back.MountPoint}"));

        // The early container (EarlyLoad): a second trio, read back like the first.
        IoStoreReader? backEarly = null;
        if (built.Early is { } early)
        {
            try
            {
                backEarly = new IoStoreReader(early.Utoc, EIoStoreTocReadOptions.ReadDirectoryIndex,
                                              provider.Versions);
                backEarly.Mount(StringComparer.OrdinalIgnoreCase);
                overall.Add(new Check("the early container mounts", Verdict.Good,
                                      $"{early.Name}: {backEarly.Files.Count} file(s)"));
            }
            catch (Exception e)
            {
                overall.Add(new Check("the early container mounts", Verdict.Bad, e.Message));
                return new Report(parts, overall);
            }

            // EVERY EARLY PACKAGE IN THE EARLY CONTAINER AND NOT IN THE PLUGIN'S, and nothing that
            // loads late in the early one. The build splits by chunk, so a package that stayed in the
            // plugin container as well, or one that crossed over the other way, means the split did
            // not do what its log line said -- the one claim this whole feature rests on.
            var misplaced = new List<string>();
            foreach (var p in built.Parts)
            {
                var inEarly = backEarly.Files.ContainsKey(p.ContainerPath);
                var inPlugin = back.Files.ContainsKey(p.ContainerPath);
                if (p.IsEarly && (!inEarly || inPlugin))
                    misplaced.Add($"{p.PackageName} (early) is {(inEarly ? "in both containers" : "not in the early container")}");
                if (!p.IsEarly && inEarly)
                    misplaced.Add($"{p.PackageName} is in the early container but loads late");
            }
            overall.Add(misplaced.Count == 0
                ? new Check("delivered early", Verdict.Good,
                            $"{string.Join(", ", early.Packages)} -> {early.Name} (Content/Paks/~mods)")
                : new Check("delivered early", Verdict.Bad, string.Join("; ", misplaced)));
        }

        // A part is read back out of the container it was WRITTEN to: an early override sits in the
        // second one, everything else in the plugin's. Every lookup by a BuiltPart's ContainerPath
        // below goes through this, so asking the wrong container cannot report a package that IS
        // there as missing -- which is exactly what this read-back did to the Outfit root before it
        // knew there were two.
        IoStoreReader ReaderFor(ModBuild.BuiltPart p) => p.IsEarly && backEarly is not null ? backEarly : back;

        // Neither icons nor copied assets are PARTS. An icon is a texture and a copy is an asset
        // the mod carries so it can be edited; counting either as a wearable reported "2 of 1"
        // built for a mod with one part in it.
        var wearable = built.Parts.Where(p => !p.IsTexture && !p.IsCopy).ToList();
        // Specializations count here too. They are parts in exactly the sense the customization
        // system means -- a slot, a menu, a CustomizationPartDefinition -- and they are built by the
        // same three pieces, so leaving them out of the expected count reported "1 of 0 built" and
        // failed a build that was correct.
        var expected = mod.Parts.Select(p => p.Id).Concat(mod.Specs.Select(s => s.Id)).ToList();
        overall.Add(wearable.Count == expected.Count
            ? new Check("every part was built", Verdict.Good, $"{wearable.Count} of {expected.Count}")
            : new Check("every part was built", Verdict.Bad,
                        $"only {wearable.Count} of {expected.Count} -- " +
                        string.Join(", ", expected.Except(wearable.Select(p => p.Id)))));

        // And their own line, for the reason overrides get one: a specialization that was skipped
        // leaves a mod that installs, mounts and offers nothing new, which reads as a broken
        // install rather than as a build that reported exactly what it did.
        if (mod.Specs.Count > 0)
        {
            var madeSpecs = wearable.Where(p => mod.Specs.Any(
                s => string.Equals(s.Id, p.Id, StringComparison.OrdinalIgnoreCase))).ToList();
            overall.Add(madeSpecs.Count == mod.Specs.Count
                ? new Check("every specialization was built", Verdict.Good,
                            $"{madeSpecs.Count} of {mod.Specs.Count}")
                : new Check("every specialization was built", Verdict.Bad,
                            $"only {madeSpecs.Count} of {mod.Specs.Count} -- " +
                            string.Join(", ", mod.Specs.Select(s => s.Id)
                                                 .Except(madeSpecs.Select(p => p.Id)))));
        }

        // Overrides get their own count, and it matters more than the parts one. A part that was
        // skipped leaves a mod missing something; an override that was skipped leaves a mod that
        // installs, mounts, verifies clean and does not do the one thing it exists to do.
        // COUNTED AGAINST THE ONES THAT ASKED FOR SOMETHING. Replacing a part creates an override
        // on the part to hang its model and textures off, and an author who only wanted a new
        // texture never gives the part itself anything -- the build says so in the log and does
        // not ship it, and counting it here reported a mod that did exactly what was asked as
        // "only 1 of 2".
        var wanted = mod.Overrides.Where(o => o.HasEdit).ToList();
        var examinedOverrides = wanted.Count > 0;
        if (wanted.Count > 0)
        {
            var madeOver = built.Parts.Where(p => p.IsOverride).ToList();
            // I-2: BY TARGET, not by count. `madeOver` now includes a build-synthesised override
            // per slot-set game target (ModBuild.cs's `slotOverrides`) as well as the author's own
            // -- so a wanted override dropped as "asked for something, changed nothing" (ModBuild.cs
            // ~6172) could still leave the COUNT at or above `wanted.Count` on the strength of an
            // unrelated synthesised override, and this read "N of N -- Good" for a mod that never
            // built the one thing it was asked to. Missing is a wanted target with no built override
            // of that PackageName; only that can say every override actually landed.
            var missing = wanted.Where(o => !madeOver.Any(
                p => p.PackageName.Equals(o.Target, StringComparison.OrdinalIgnoreCase))).ToList();
            overall.Add(missing.Count == 0
                ? new Check("every override was built", Verdict.Good,
                            $"{madeOver.Count} of {wanted.Count}")
                : new Check("every override was built", Verdict.Bad,
                            $"only {wanted.Count - missing.Count} of {wanted.Count} -- " +
                            string.Join(", ", missing.Select(o => o.Target))));
        }

        // EVERY NEW ACTOR, READ BACK OUT OF THE CONTAINER.
        //
        // The build's own log says it wrote a position and the writer re-reads it before handing
        // the bytes on, but nothing between there and here had been asked whether the actor
        // survives being packed into a container and pulled out again -- which is the one question
        // this whole read-back exists to answer, and it went unasked for the newest and least
        // proven edit the builder makes.
        //
        // BY THE ADDRESS THE BUILD ASSIGNED. A spawn is "#N" where N was decided while the export
        // map was being grown, so the project cannot name it and neither can this; Result.Spawned
        // carries it across. Locating it at all is most of the check: an export that is not there,
        // or is there without a transform, fails before any number is compared.
        var examinedSpawns = false;
        if (built.Spawned is { Count: > 0 } spawnedActors)
        {
            examinedSpawns = true;
            foreach (var group in spawnedActors.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase))
            {
                var part = built.Parts.FirstOrDefault(
                    x => x.PackageName.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
                if (part is null || !ReaderFor(part).Files.TryGetValue(part.ContainerPath, out var lvlFile))
                {
                    overall.Add(new Check("a new actor reads back", Verdict.Bad,
                                          $"{group.Key} is not in the container it was written to"));
                    continue;
                }

                byte[] lvlRaw;
                try { lvlRaw = lvlFile.Read(); }
                catch (Exception ex)
                {
                    overall.Add(new Check("a new actor reads back", Verdict.Bad,
                                          $"{group.Key}: {ex.Message}"));
                    continue;
                }

                var landed = new List<string>();
                foreach (var sp in group)
                {
                    var bad = false;
                    foreach (var (axis, want) in new[] { ("X", sp.X), ("Y", sp.Y), ("Z", sp.Z) })
                    {
                        var path = "RootComponent.RelativeLocation." + axis;
                        var found = LevelEdit.Locate(provider, lvlRaw, group.Key, sp.Address, path,
                                                     out var why);
                        if (found is null)
                        {
                            overall.Add(new Check("a new actor reads back", Verdict.Bad,
                                                  $"{sp.Address}.{path}: {why}"));
                            bad = true;
                            break;
                        }
                        if (Math.Abs(found.Value - want) > 0.001)
                        {
                            overall.Add(new Check("a new actor reads back", Verdict.Bad,
                                                  $"{sp.Address}.{path} reads {found.Value}, not {want}"));
                            bad = true;
                            break;
                        }
                    }
                    if (!bad)
                        landed.Add($"{sp.Address} at {sp.X:0.###},{sp.Y:0.###},{sp.Z:0.###}" +
                                   (sp.Asset.Length > 0
                                        ? " drawing " + sp.Asset[(sp.Asset.LastIndexOf('/') + 1)..]
                                        : ""));
                }

                if (landed.Count > 0)
                    overall.Add(new Check("every new actor reads back", Verdict.Good,
                                          $"{landed.Count} in {group.Key[(group.Key.LastIndexOf('/') + 1)..]}: " +
                                          string.Join(", ", landed)));
            }
        }

        foreach (var skipped in built.Skipped)
            overall.Add(new Check("nothing was skipped", Verdict.Bad, skipped));

        var shipped = ShippedRegistry(built, provider, overall);
        var (ours, theirs) = StoreEntries(built, provider, overall);

        // TWO COPIES WITH ONE LEAF NAME ARE NOT ONE COPY, and this listing used to say they were.
        //
        // Every copy is named after the asset it replaces, so a mod whose parts each dress a whole
        // body repaints the same bodysuit material once per part -- and the read-back then printed
        // `T_Clo010_SUIT_Mat3_MSK` twice, identically, with nothing to tell them apart. They ARE
        // different packages: a multi-region part files each piece under `Parts/<id>/<key>/`, which
        // is the whole reason they do not collide. But the log showed only the leaf, so the listing
        // looked exactly like the overwrite bug that folder split exists to prevent.
        //
        // Cost half an hour of reading a build log for a collision that was not there. The names
        // are only lengthened where they were ambiguous; a mod with one copy per asset reads as it
        // always did.
        var byLeaf = built.Parts.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // EVERY SLOT SET SlotSets.Resolve NAMES must have been stamped by the build (I-3). Matched
        // by the landing's own Target against Result.SlotStamps' Target -- the same string, carried
        // straight through from the build rather than re-derived -- so this needs no BuiltPart.Id
        // lookup either. A target with no stamp was refused, skipped for an unrelated reason, or
        // never reached: any of those is a slot the mod asked for and does not have.
        foreach (var landing in SlotSets.Resolve(mod, out _))
            if (built.SlotStamps?.Any(s => s.Target.Equals(landing.Target, StringComparison.OrdinalIgnoreCase)) != true)
                overall.Add(new Check("every slot set was built", Verdict.Bad, $"{landing.Target}: never built"));

        // F6: an INDEPENDENT read-back of every id-named slot default. "its slots read back"
        // (per part, below) compares the shipped package against the STAMP's already-resolved
        // Default -- and the stamp was written by SlotSets.ResolveDefaults calling the SAME
        // objectPathById/PackageNameOf this check would otherwise have to use, so agreement there
        // could only ever confirm the build agrees with itself. Here the id is read fresh off
        // SlotSets.Resolve -- a pure projection of mod.SlotSets (ResolveDefaults never rewrites a
        // ModSlot; it only replaces entries of the LANDING list it is handed, a copy made by
        // ModSlot.ToSlot() each call, so the project model still names the id, unresolved, no
        // matter how many times a build has resolved it) -- and checked against a part or copy
        // the build ACTUALLY PRODUCED, read from built.Parts' own PackageName rather than from the
        // map that predicted it.
        foreach (var landing in SlotSets.Resolve(mod, out _))
            foreach (var slot in landing.Slots)
            {
                if (string.IsNullOrEmpty(slot.Default) || slot.Default.StartsWith('/')) continue;

                var produced = built.Parts.FirstOrDefault(
                    b => !b.IsTexture && b.Id.Equals(slot.Default, StringComparison.OrdinalIgnoreCase));
                var stampedDefault = built.SlotStamps
                    ?.FirstOrDefault(s => s.Target.Equals(landing.Target, StringComparison.OrdinalIgnoreCase))
                    ?.Slots.FirstOrDefault(s => s.Id.Equals(slot.Id, StringComparison.OrdinalIgnoreCase))
                    ?.Default;
                var stampedPackage = ModBuild.PackageOf(stampedDefault);

                overall.Add(produced is not null && stampedPackage is not null &&
                            stampedPackage.Equals(produced.PackageName, StringComparison.OrdinalIgnoreCase)
                    ? new Check("a slot's id default resolved right", Verdict.Good,
                                $"{landing.Target}: slot {slot.Id}: '{slot.Default}' -> {stampedDefault}")
                    : new Check("a slot's id default resolved right", Verdict.Bad,
                                $"{landing.Target}: slot {slot.Id}: default '{slot.Default}' resolved to " +
                                $"{stampedDefault ?? "(nothing)"}, not the package '{slot.Default}' " +
                                $"was actually built at ({produced?.PackageName ?? "it was never built"})"));
            }

        foreach (var p in built.Parts)
        {
            var r = ReaderFor(p);   // the container this part was written to; see ReaderFor
            var report = p.IsCopy ? Copy(provider, r, p, ours, mod, built, shipped)
                       : p.IsTexture ? Icon(provider, r, p)
                       : Part(provider, r, mod, p, shipped, built, ours);
            Imports(report.Checks, ours, theirs, mod, p);
            Header(report.Checks, provider, r, p);
            Roles(report.Checks, provider, r, mod, p);

            // THE NUMBERS, out of the container rather than out of the buffer that wrote them.
            //
            // Here rather than inside one of the three verifiers, because an override arrives at
            // whichever of them fits what else it does -- a stat change with no mesh comes through
            // as a COPY -- and a check that only ran down one of those paths would silently not
            // run for the commonest case. It cost one build to find that out.
            //
            // ScalarEdit already refuses a write whose read-back disagrees, so the bytes handed to
            // the packer are known good. Nothing between there and here had been asked whether the
            // number survives being written into a container and pulled out again.
            if (mod.Overrides.FirstOrDefault(
                    o => o.Target.Equals(p.PackageName, StringComparison.OrdinalIgnoreCase))
                is { } ovAsked &&
                r.Files.TryGetValue(p.ContainerPath, out var ovFile))
            {
                var ovRaw = ovFile.Read();
                if (ovAsked.Numbers.Count > 0 || ovAsked.AddScalars.Count > 0)
                    Numbers(report.Checks, provider, ovRaw, p.PackageName, ovAsked);
                if (ovAsked.Values.Count > 0)
                    Values(report.Checks, provider, ovRaw, p.PackageName, ovAsked);
                if (ovAsked.Repoints.Count > 0)
                    Tags(report.Checks, ovRaw, ovAsked);
                if (ovAsked.Moves.Count > 0)
                    Moves(report.Checks, provider, ovRaw, p.PackageName, ovAsked);
                if (ovAsked.Swaps.Count > 0)
                    Swaps(report.Checks, provider, ovRaw, p.PackageName, ovAsked);
                Words(report.Checks, provider, ovRaw, p, ovAsked);
            }

            // SLOTS, out of the container. Matched by PACKAGE NAME against what the BUILD recorded
            // it actually stamped (I-3), never by BuiltPart.Id: an id collides across several kinds
            // of BuiltPart (a Game Feature data asset's Id is the plugin name, a repaint/mesh copy's
            // is the asset leaf, an override's is the part name), and PartExport is decided ONCE, by
            // the build itself when it wrote the bytes -- so this reader can never disagree with
            // what is actually there, whatever the id says.
            var slotStamp = built.SlotStamps?.FirstOrDefault(
                s => s.PackageName.Equals(p.PackageName, StringComparison.OrdinalIgnoreCase));
            if (slotStamp is not null && r.Files.TryGetValue(p.ContainerPath, out var slotFile))
            {
                var exports = PropertyAdd.PristineExports(provider, slotFile.Read(), p.PackageName,
                                                          p.IsOverride ? null : p.TemplatePath);
                var complaints = SlotAdd.Check(exports, slotStamp.PartExport, slotStamp.Slots);
                report.Checks.Add(complaints.Count == 0
                    ? new Check("its slots read back", Verdict.Good, string.Join(", ", slotStamp.Slots.Select(s => $"{s.Name}")))
                    : new Check("its slots read back", Verdict.Bad, string.Join("; ", complaints)));
            }

            parts.Add(byLeaf.GetValueOrDefault(p.Id) > 1
                ? report with { Id = Distinguish(p) }
                : report);
        }

        // EXAMINED, so Clean can mean "checked and nothing failed" for a mod with no parts in it.
        // A map edit is all overrides; see Report.Examined.
        return new Report(parts, overall, examinedOverrides || examinedSpawns);
    }

    /// <summary>
    /// Enough of a copy's package path to tell it from another copy of the same asset.
    ///
    /// The last three segments, because that is exactly the shape the piece split produces --
    /// `Parts/&lt;partId&gt;/&lt;piece&gt;/&lt;leaf&gt;` -- so the part and the piece both show. Falls back to
    /// the whole path when there is not that much of one, which is the case this is not about.
    /// </summary>
    private static string Distinguish(ModBuild.BuiltPart p)
    {
        var bits = p.PackageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return bits.Length >= 3 ? string.Join('/', bits[^3..]) : p.PackageName;
    }

    // ------------------------------------------------------------------ the header, as arithmetic

    /// <summary>
    /// Does the shipped package's header agree with itself, and did it lose bytes where it gained
    /// content?
    ///
    /// THIS IS THE CHECK THAT DOES NOT GO THROUGH A READER, and it is here because on 2026-09-07
    /// everything that did went through one. A mod shipped that crashed the game at the main menu;
    /// the build read it back, every check in this file passed, and the fault -- a dropped
    /// int32-per-imported-package table -- was invisible to all of them for one reason: neither
    /// CUE4Parse nor <see cref="ZenPackage"/> reads that section. A reader cannot report a
    /// difference in something it has no model for, so a verifier built entirely out of readers
    /// has a blind spot exactly the shape of the sections nobody has implemented yet.
    ///
    /// <see cref="ZenCheck"/> decodes nothing. It measures the summary's own offsets against each
    /// other and against the file, so a section this tool has never understood is still checked --
    /// its size is stated by the two offsets either side of it whether or not anyone knows what is
    /// in it. That is the property the rest of this file cannot have.
    ///
    /// Two questions, and the second needs the template:
    ///   the header alone   every offset in order, every run a whole number of entries, the
    ///                      imported-package-names trailer exactly four bytes per package, the
    ///                      export data ending where the file does.
    ///   against its source a run that GAINED entries must not have LOST bytes. The corrupt arc
    ///                      package was internally consistent; only the game's copy beside it said
    ///                      that 26,255 bytes could not be 26,322 bytes plus a recipe.
    /// </summary>
    private static void Header(List<Check> checks, IFileProvider provider, IoStoreReader back,
                               ModBuild.BuiltPart p)
    {
        if (!back.Files.TryGetValue(p.ContainerPath, out var file)) return;   // said already, by name

        byte[] shipped;
        try { shipped = file.Read(); }
        catch (Exception e)
        { checks.Add(new Check("its header measures up", Verdict.Bad, $"could not be read back -- {e.Message}")); return; }

        var faults = new List<string>();
        var shape = ZenCheck.Measure(shipped, faults);
        checks.Add(faults.Count == 0
            ? new Check("its header measures up", Verdict.Good,
                        shape is null ? "checked" : ZenCheck.Describe(shape))
            : new Check("its header measures up", Verdict.Bad, string.Join("; ", faults)));

        // The template it was made from -- the game's own copy for an override, the part it was
        // cloned from for a copy. A Note when it cannot be found: not every template is a mounted
        // package, and a comparison that could not be made is not a comparison that failed.
        var template = p.IsOverride ? p.PackageName : p.TemplatePath;
        if (string.IsNullOrEmpty(template)) return;
        var src = AssetCopy.PristineRead(provider, template, out var why);
        if (src?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data is not { } was)
        {
            checks.Add(new Check("it did not shrink where it grew", Verdict.Note,
                                 why ?? $"{template} is not mounted, so there is nothing to compare against"));
            return;
        }

        var growth = ZenCheck.Growth(was, shipped);
        checks.Add(growth.Count == 0
            ? new Check("it did not shrink where it grew", Verdict.Good,
                        $"{was.Length:N0} -> {shipped.Length:N0} bytes against {Leaf(template)}")
            : new Check("it did not shrink where it grew", Verdict.Bad, string.Join("; ", growth)));
    }

    /// <summary>
    /// Every re-pointed hard import names the SAME OBJECT in its new package that it named in the
    /// old one -- the class for the class, the default object for the default object.
    ///
    /// "The target publishes this hash" was the whole question before, and it passed an import
    /// that named a component. Force Grip's effect-CLASS import, re-pointed from `GE_PlagueStack`
    /// to `GE_Bind_Target`, resolved to `AdditionalEffectsGameplayEffectComponent_0`: a real export
    /// of the right package, and the wrong object. A class property holding a component nulls
    /// itself on load, so the ability would have fired and applied nothing, and every check here
    /// said Good. What a hash NAMES is the question, not whether it exists.
    /// </summary>
    private static void Roles(List<Check> checks, IFileProvider provider, IoStoreReader back,
                              ModProject mod, ModBuild.BuiltPart p)
    {
        var repoints = mod.Overrides
            .Where(x => string.Equals(x.Target, p.PackageName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.RepointImports)
            .Concat(mod.Copies
                .Where(c => c.Id == p.Id)
                .SelectMany(c => c.RepointImports.Select(kv =>
                    new KeyValuePair<string, string>(kv.Key, ModBuild.CopyTargetOf(kv.Value, mod)))))
            .ToList();
        if (repoints.Count == 0) return;
        if (!back.Files.TryGetValue(p.ContainerPath, out var file)) return;   // said already, by name

        var template = p.IsOverride ? p.PackageName : p.TemplatePath;
        var src = string.IsNullOrEmpty(template) ? null : AssetCopy.PristineRead(provider, template, out _);
        if (src?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data is not { } wasBytes) return;

        ZenPackage was, now;
        try { was = ZenPackage.Read(wasBytes); now = ZenPackage.Read(file.Read()); }
        catch (Exception) { return; }   // the header check reports an unreadable package

        // SLOT FOR SLOT. A redirect rewrites a hash where it stands, and an import added by the
        // same build is appended, so the slots both tables share are the same imports.
        var wasHashes = was.ImportedPublicExportHashes;
        var nowHashes = now.ImportedPublicExportHashes;
        var shared = Math.Min(wasHashes.Length, nowHashes.Length);

        static string PathLeaf(string path) => path[(path.LastIndexOf('/') + 1)..];

        foreach (var (from, to) in repoints)
        {
            // THE SAME RULE the redirect used, asked independently of it: what this import SHOULD
            // name, from the two packages' export tables, against what the shipped bytes do name.
            var source = ModBuild.HashSource(mod, to);
            var fromTable = ImportRepoint.ExportTable.Of(provider, from);
            var toTable = ImportRepoint.ExportTable.Of(provider, source);

            var right = new List<string>();
            var wrong = new List<string>();
            for (var i = 0; i < shared; i++)
            {
                var at = Array.IndexOf(fromTable.Hashes, wasHashes[i]);
                if (at < 0 || at >= fromTable.Names.Length) continue;  // an import of another package

                var role = fromTable.Names[at];
                var meant = ImportRepoint.CounterpartOf(fromTable, toTable, at, PathLeaf(from), PathLeaf(source));
                var landed = Array.IndexOf(toTable.Hashes, nowHashes[i]);
                var named = landed >= 0 && landed < toTable.Names.Length ? toTable.Names[landed] : null;

                if (meant is not null && landed == meant.Index)
                    right.Add(meant.StandsInFor is null ? $"{role} -> {named}"
                                                        : $"{role} -> {named} (its owner's counterpart)");
                else
                    wrong.Add(named is null
                        ? $"{role} -> 0x{nowHashes[i]:X16}, which {PathLeaf(source)} does not publish"
                        : meant is null
                            ? $"{role} -> {named}, and nothing in {PathLeaf(source)} plays its part"
                            : $"{role} -> {named}, where {meant.Name} was meant");
            }

            if (right.Count == 0 && wrong.Count == 0) continue;   // the store check says it moved nothing
            checks.Add(wrong.Count == 0
                ? new Check($"its imports of {PathLeaf(to)} name the same objects", Verdict.Good,
                            string.Join("; ", right))
                : new Check($"its imports of {PathLeaf(to)} name the same objects", Verdict.Bad,
                            string.Join("; ", wrong) + " -- an import naming the wrong object " +
                            "hands that object to whatever reads it (a class slot given a " +
                            "component nulls itself), and one naming nothing loads as null"));
        }
    }

    // ------------------------------------------------------------------ the registry we shipped

    // ------------------------------------------------------------------ the package store

    /// <summary>
    /// The store entries our container declares, and the ones the game declares for the templates
    /// they came from.
    ///
    /// This is the check for the only fault so far that CRASHED the game rather than looking
    /// wrong. A container's package store lists, per package, the other packages it imports; the
    /// loader resolves those before handing the asset over. Our first authored parts shipped with
    /// EMPTY entries while their templates imported three packages each, so the loader walked off
    /// the end of a dependency list that had never been registered and died inside
    /// ProcessExportDependencies -- with no message naming the mod, the part, or the import.
    ///
    /// Nothing about that is visible in the package, the registry, or the menu. The store is the
    /// only place it exists, which is why it is read here rather than trusted.
    ///
    /// A mod can ship TWO containers (EarlyLoad: <see cref="ModBuild.Result.Early"/>), each with its
    /// own header. An early package's entry lives in the second one, so both are read, into the one
    /// map -- an entry is found by package id whichever container holds it.
    /// </summary>
    private static (Dictionary<ulong, ContainerHeader.StoreEntry> Ours,
                    Dictionary<ulong, ContainerHeader.StoreEntry> Theirs) StoreEntries(
        ModBuild.Result built, IFileProvider provider, List<Check> overall)
    {
        var ours = new Dictionary<ulong, ContainerHeader.StoreEntry>();

        // BOTH ALWAYS READ (`&=` does not short-circuit): a fault in one container must not hide a
        // fault in the other, and the Good line is said once, only when every container was read.
        var read = ReadStoreInto(built.Utoc, provider, ours, overall);
        if (built.Early is { } early)
            read &= ReadStoreInto(early.Utoc, provider, ours, overall);
        if (read)
            overall.Add(new Check("the container has a package store", Verdict.Good,
                                  $"{ours.Count} package(s)"));

        Dictionary<ulong, ContainerHeader.StoreEntry> theirs;
        try { theirs = ModBuild.StoreEntries(provider); }
        catch (Exception) { theirs = new Dictionary<ulong, ContainerHeader.StoreEntry>(); }
        return (ours, theirs);
    }

    /// <summary>
    /// One container's package store, added to <paramref name="into"/>. False -- after adding the Bad
    /// check that says why -- when the container has no store or cannot be read, so the caller does
    /// not call the store good. A missing or damaged container is a Bad check, not an exception.
    /// </summary>
    private static bool ReadStoreInto(string utoc, IFileProvider provider,
                                      Dictionary<ulong, ContainerHeader.StoreEntry> into,
                                      List<Check> overall)
    {
        // Only the early container is named in a failure: with one container there is nothing to
        // tell apart, and the line has always read the same.
        var which = EarlyLoad.IsEarlyContainer(utoc) ? Path.GetFileName(utoc) + ": " : "";
        try
        {
            var reader = new IoStoreReader(utoc, EIoStoreTocReadOptions.Default, provider.Versions);
            var want = ContainerHeader.ChunkIdFor(reader.TocResource.Header.ContainerId.Id);
            var chunk = reader.TocResource.ChunkIds.FirstOrDefault(
                c => ContainerHeader.ChunkIdFor(c.ChunkId).AsSpan().SequenceEqual(want) &&
                     c.ChunkType == ContainerHeader.ChunkType);

            if (chunk.ChunkId == 0 && chunk.ChunkType == 0)
            {
                overall.Add(new Check("the container has a package store", Verdict.Bad,
                                      which + "no header chunk -- every package in it is invisible to the loader"));
                return false;
            }

            var hdr = ContainerHeader.Read(reader.Read(chunk));
            for (var i = 0; i < hdr.PackageIds.Count && i < hdr.Store.Count; i++)
                into[hdr.PackageIds[i]] = hdr.Store[i];
            return true;
        }
        catch (Exception e)
        {
            overall.Add(new Check("the container has a package store", Verdict.Bad, which + e.Message));
            return false;
        }
    }

    private static void Imports(List<Check> checks,
                                Dictionary<ulong, ContainerHeader.StoreEntry> ours,
                                Dictionary<ulong, ContainerHeader.StoreEntry> theirs,
                                ModProject mod, ModBuild.BuiltPart p)
    {
        if (!ours.TryGetValue(p.ChunkId, out var mine))
        {
            checks.Add(new Check("registered in the package store", Verdict.Bad,
                                 "the loader would never resolve this package"));
            return;
        }

        // Against the TEMPLATE's entry, because the number that matters is not "more than zero"
        // -- some parts legitimately import nothing -- but "as many as the thing this was copied
        // from". Ours had zero where the template had three.
        var tplId = PackageId.Of(p.TemplatePath);
        if (theirs.TryGetValue(tplId, out var tpl) && tpl.ImportedPackages.Count > mine.ImportedPackages.Count)
            checks.Add(new Check("carries its template's imports", Verdict.Bad,
                                 $"{mine.ImportedPackages.Count} of the template's " +
                                 $"{tpl.ImportedPackages.Count} -- the loader crashes on an " +
                                 "import that was never registered"));
        else
            checks.Add(new Check("carries its template's imports", Verdict.Good,
                                 $"{mine.ImportedPackages.Count} import(s)"));

        // A DECLARED REDIRECT, CHECKED IN THE CONTAINER THAT SHIPPED.
        //
        // The other half of a hard import lives here rather than in the package: the store entry
        // says WHICH package, and the package says which export inside it. OverrideInvariant reads
        // the hash half out of the bytes; nothing read this half, so a redirect whose store swap
        // silently did not land would have passed everything. The two are useless apart -- moving
        // the hash alone points a correct export index at the original package.
        var repoints = mod.Overrides
            .Where(x => string.Equals(x.Target, p.PackageName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.RepointImports)
            .Concat(mod.Copies
                .Where(c => c.Id == p.Id)
                .SelectMany(c => c.RepointImports.Select(kv =>
                    new KeyValuePair<string, string>(kv.Key, ModBuild.CopyTargetOf(kv.Value, mod)))))
            .ToList();

        foreach (var (from, to) in repoints)
        {
            var gone = !mine.ImportedPackages.Contains(PackageId.Of(from));
            var here = mine.ImportedPackages.Contains(PackageId.Of(to));
            var name = to[(to.LastIndexOf('/') + 1)..];
            checks.Add(gone && here
                ? new Check($"loads {name}", Verdict.Good,
                            "the store entry names it and no longer names what it replaced")
                : new Check($"loads {name}", Verdict.Bad,
                            here ? "it is named, but so is the asset it was meant to replace"
                                 : "the store entry does not name it, so the game would load " +
                                   "whatever was there before"));
        }
    }

    public sealed record Registered(PartsIndex.Part Part, string? PrimaryAssetName);

    /// <summary>
    /// Every part as the SHIPPED registry describes it, keyed by asset name.
    ///
    /// The registry is what the game reads to find out a part exists, what it is called, and
    /// whether the player has unlocked it -- so two faults live here and nowhere else, and both
    /// have already been paid for:
    ///
    ///   the unlock gate     a part copied from a template inherits its gate. Three of five parts
    ///                       simply never appeared in the menu, and the mod looked like it had
    ///                       not installed.
    ///
    ///   PrimaryAssetName    the template's id came along, so the game had two parts claiming one
    ///                       id and ours lost.
    ///
    /// Read out of the built .pak rather than from what the builder believes it wrote: the point
    /// of a read-back is to check the artifact, and the registry is the one piece a person cannot
    /// see by looking at the mod in the menu -- when it is wrong, the menu shows nothing at all.
    /// </summary>
    private static Dictionary<string, Registered> ShippedRegistry(
        ModBuild.Result built, IFileProvider provider, List<Check> overall)
    {
        var byName = new Dictionary<string, Registered>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pak = new PakFileReader(built.Pak, provider.Versions);
            pak.Mount(StringComparer.OrdinalIgnoreCase);

            var entry = pak.Files.FirstOrDefault(
                f => f.Key.EndsWith("AssetRegistry.bin", StringComparison.OrdinalIgnoreCase));
            if (entry.Value is null)
            {
                overall.Add(new Check("the mod ships a registry", Verdict.Bad,
                                      "no AssetRegistry.bin in " + Path.GetFileName(built.Pak)));
                return byName;
            }

            var state = new FAssetRegistryState(
                new FByteArchive("AssetRegistry.bin", entry.Value.Read(), provider.Versions));

            foreach (var data in state.PreallocatedAssetDataBuffers)
            {
                var tags = data.TagsAndValues;
                if (tags is null) continue;
                string? Tag(string k) => tags.FirstOrDefault(
                    kv => kv.Key.Text.Equals(k, StringComparison.OrdinalIgnoreCase)).Value;

                byName[data.AssetName.Text] = new Registered(
                    PartsIndex.Describe(data.PackageName.Text, data.AssetName.Text,
                                        Tag("NativeClass"), Tag("CustomizationPartTags")),
                    Tag("PrimaryAssetName"));
            }

            overall.Add(new Check("the mod ships a registry", Verdict.Good,
                                  $"{state.PreallocatedAssetDataBuffers.Length:N0} entries"));
        }
        catch (Exception e)
        {
            overall.Add(new Check("the mod ships a registry", Verdict.Bad, e.Message));
        }
        return byName;
    }

    // ------------------------------------------------------------------ one icon

    /// <summary>
    /// An authored icon is checked by DECODING it. Looking for a mesh on it reports a failure that
    /// is not there, and looking only for the file reports a success that is not either -- the
    /// texture is a template's pixels overwritten in place, so the way it goes wrong is a payload
    /// that no longer decodes rather than a file that is not present.
    /// </summary>
    private static PartReport Icon(IFileProvider provider, IoStoreReader back, ModBuild.BuiltPart p)
    {
        var checks = new List<Check>();
        if (!back.Files.TryGetValue(p.ContainerPath, out var file))
        {
            checks.Add(new Check("in the container", Verdict.Bad, p.ContainerPath));
            return new PartReport(p.Id, true, checks);
        }
        checks.Add(new Check("in the container", Verdict.Good, $"{p.Bytes:N0} bytes"));

        try
        {
            var pkg = new IoPackage(new FByteArchive(p.Id, file.Read(), provider.Versions),
                                    null, (FArchive?) null, null, provider as IVfsFileProvider);
            var tex = pkg.ExportsLazy.Select(x => x.Value).OfType<UTexture2D>().FirstOrDefault();
            var img = tex is null ? null : TextureDecoder.Decode(tex, ETexturePlatform.DesktopMobile);

            checks.Add(img is null
                ? new Check("the icon decodes", Verdict.Bad, "no texture decoded out of it")
                : new Check("the icon decodes", Verdict.Good,
                            $"{tex!.PlatformData.PixelFormat} {img.Width}x{img.Height}"));
        }
        catch (Exception e) { checks.Add(new Check("the icon decodes", Verdict.Bad, e.Message)); }

        return new PartReport(p.Id, true, checks);
    }

    /// <summary>
    /// An asset the mod carries its OWN COPY of, rather than pointing at the game's.
    ///
    /// Checked as a package and nothing more specific. A copy is bytes the game wrote, moved to a
    /// new name -- there is no authored content in it to be wrong, so the questions worth asking
    /// are whether it arrived and whether it still parses. The first version filed these as icons
    /// and reported a mesh as a texture that would not decode, which is a true statement about the
    /// wrong question.
    /// </summary>
    private static PartReport Copy(IFileProvider provider, IoStoreReader back, ModBuild.BuiltPart p,
                                   Dictionary<ulong, ContainerHeader.StoreEntry> ours,
                                   ModProject mod, ModBuild.Result built,
                                   Dictionary<string, Registered> shipped)
    {
        var checks = new List<Check>();
        if (!back.Files.TryGetValue(p.ContainerPath, out var file))
        {
            checks.Add(new Check("its own copy is in the container", Verdict.Bad, p.ContainerPath));
            return new PartReport(p.Id, false, checks);
        }
        checks.Add(new Check("its own copy is in the container", Verdict.Good,
                             $"{p.Bytes:N0} bytes, from {p.TemplatePath}"));

        // Captured here, not just handed to IoPackage inline, so AddedCopyTags below can reuse
        // the same bytes rather than reading the container entry a second time. Stays null on a
        // read failure exactly as before the capture -- the catch below reports it the same way
        // it always did, and AddedCopyTags is skipped rather than attempted on nothing.
        byte[]? raw = null;
        try
        {
            raw = file.Read();
            var pkg = new IoPackage(new FByteArchive(p.Id, raw, provider.Versions),
                                    null, (FArchive?) null, null, provider as IVfsFileProvider);
            checks.Add(new Check("the copy still parses", Verdict.Good,
                                 $"{pkg.ExportMapLength} export(s)"));

            // A copy given a picture of its own must READ BACK naming it. The bank route built
            // and verified on every other question and drew the game's fallback picture; this
            // is the line that would have caught it, had the picture been checked at all.
            if (p.IconPackage is { } iconPackage)
            {
                var fragment = pkg.ExportsLazy.Select(x => x.Value).FirstOrDefault(e =>
                    (e.ExportType ?? "").StartsWith("BitReactorUIDataFragment", StringComparison.Ordinal));
                var (iconPath, iconTag) = fragment is null
                    ? ((string?) null, (string?) null)
                    : PartDetails.ImageOf(fragment.Properties, "SmallImage");
                checks.Add(iconPath is not null && iconPath.StartsWith(iconPackage + ".", StringComparison.Ordinal)
                    ? new Check("draws the mod's own picture", Verdict.Good, iconPath)
                    : new Check("draws the mod's own picture", Verdict.Bad,
                                "SmallImage reads " + (iconPath ?? (iconTag is null ? "(nothing)" : "bank tag " + iconTag))));
            }

            // A mesh gets its geometry counted, because "it parses" is exactly what a mesh whose
            // geometry was NOT replaced also does. This is the only line in the report that would
            // change if an import silently did nothing.
            var mesh = pkg.ExportsLazy.Select(x => x.Value)
                          .OfType<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>()
                          .FirstOrDefault();
            if (mesh?.LODModels is { Length: > 0 } lods)
            {
                checks.Add(new Check("the geometry reads back", Verdict.Good,
                                     $"LOD 0: {lods[0].NumVertices:N0} vertices, " +
                                     $"{(lods[0].Indices?.Buffer?.Length ?? 0) / 3:N0} triangles, " +
                                     $"{lods.Length} LOD(s)"));

                // EACH SECTION NAMES ITS OWN MATERIAL SLOT.
                //
                // Counting the slots is not enough, and the way this fails is quiet. A mesh whose
                // extra slots were grown but never given a material of their own parses, reports
                // the right number of slots, and wears the BODY'S paint on the piece that was
                // supposed to look different -- which reads in game as "the import worked and the
                // textures did nothing". What separates the two is whether the sections point at
                // different slots, and that is one line to check.
                var slots = mesh.SkeletalMaterials?.Length ?? 0;
                var wearing = lods[0].Sections.Select(x => (int) x.MaterialIndex).ToList();
                var apart = wearing.Distinct().Count();
                if (slots > 1 || wearing.Count > 1)
                    checks.Add(apart >= Math.Min(slots, wearing.Count)
                        ? new Check("each section wears its own material", Verdict.Good,
                                    $"{wearing.Count} section(s) across {slots} slot(s): " +
                                    string.Join(", ", wearing.Select(m => $"slot {m}")))
                        : new Check("each section wears its own material", Verdict.Bad,
                                    $"{wearing.Count} section(s) share {apart} slot(s) of {slots} -- " +
                                    "the extra piece wears the first material's paint"));
            }
        }
        catch (Exception e) { checks.Add(new Check("the copy still parses", Verdict.Bad, e.Message)); }

        // Does it point INWARD?
        //
        // This is the question the rest of the report cannot answer. A material copied without its
        // texture references redirected arrives intact, parses, carries the right number of
        // imports and shows the game's own artwork -- so every other check here passes on a mod
        // that does nothing. What separates the two is whether the imports name packages THIS MOD
        // ships, which is a set this build knows exactly.
        if (p.Redirected > 0)
        {
            var mine = built.Parts.Select(b => b.ChunkId).ToHashSet();
            var declared = ours.TryGetValue(p.ChunkId, out var entry) ? entry.ImportedPackages : [];
            var inward = declared.Count(mine.Contains);

            checks.Add(inward >= p.Redirected
                ? new Check("points at the mod's own assets", Verdict.Good,
                            $"{inward} of its {declared.Count} import(s), {p.Redirected} redirected")
                : new Check("points at the mod's own assets", Verdict.Bad,
                            $"only {inward} of {p.Redirected} redirect(s) landed -- it still uses " +
                            "the game's assets and would show the game's artwork"));
        }

        // A copy's `addTags` reach both records the same way a part's do -- see AddedCopyTags.
        // Skipped, not reported, when the bytes above never read: "the copy still parses" already
        // said Bad for the same reason, and a second line about the same failure says nothing new.
        //
        // BY ID AND PACKAGE NAME, not by id alone (R7's own reasoning, see the SLOTS comment
        // above): an override is built with IsCopy AND IsOverride both true, and the
        // GameFeatureData asset's Id is the plugin name -- either can collide with one of this
        // mod's own copy ids, and `!p.IsOverride` plus a PackageName match is what tells THIS
        // BuiltPart from a copy that merely happens to share its name. modFolder is recomputed
        // from mod rather than carried on p, the same way the build itself names a copy's package.
        var modFolder = ModBuild.FolderOf(mod);
        var askedCopy = !p.IsOverride
            ? mod.Copies.FirstOrDefault(x => x.Id == p.Id &&
                ModBuild.PackageNameOf(x.Id, modFolder).Equals(p.PackageName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (raw is not null && askedCopy is not null)
            AddedCopyTags(checks, provider, back, raw, shipped, p, askedCopy);

        // Skipped, not reported, for the same reason as above -- and only when clearSlots was
        // actually asked for: a copy that never asked to be cleared has nothing to say here.
        if (raw is not null && askedCopy is { ClearSlots: true })
            ClearedCopySlots(checks, provider, raw, p, askedCopy);

        // raw, not a second file.Read(): the bytes above are already read and guarded (see the
        // capture note just above this method's try), and a second unguarded read of the same
        // file is a throw ModVerify.Run promised never to make. Skipped, not reported, when raw
        // never read, for the same reason AddedCopyTags/ClearedCopySlots skip above.
        if (p.IsOverride && raw is not null)
            OverrideInvariant(provider, raw, p, mod, checks,
                              built.SynthesisedOverrides, built.SynthesisedCopies);

        return new PartReport(p.Id, false, checks);
    }

    /// <summary>
    /// CLEARSLOTS READ BACK, two checks off the same pair of reads: none of the TEMPLATE's own
    /// CustomizationFragmentSlot exports may still be listed in the built copy's Fragments, and
    /// everything ELSE the template's Fragments listed must still be there, in the same relative
    /// order.
    ///
    /// Deliberately independent of SlotAdd.ClearSlots -- see that method's own comment on
    /// SiblingsDecodeAsBefore, which a probe must never call into: a checker sharing a writer's
    /// bookkeeping is checking the writer with its own logic, so a bug in the writer would never
    /// be caught. The template's own Fragments are read fresh from the game's pristine copy of it,
    /// by NAME, and checked against what the built copy's Fragments lists -- a slot set's own new
    /// slots are never among the template's names, so adding one never trips either check.
    ///
    /// "Same relative order" is checked as a SUBSEQUENCE of the copy's Fragments, not a contiguous
    /// run: a newly added slot can land anywhere among the kept entries without disturbing this,
    /// because clearSlots' own splice only ever removes slot-typed entries and never reorders the
    /// ones it keeps (see SlotAdd.ClearSlots' own `kept.Add(index)` loop).
    /// </summary>
    private static void ClearedCopySlots(List<Check> checks, IFileProvider provider, byte[] raw,
                                         ModBuild.BuiltPart p, ModCopy askedCopy)
    {
        const string Cleared = "its template's slots are cleared";
        const string Kept = "its template's other fragments are kept";

        var src = AssetCopy.PristineRead(provider, askedCopy.Template, out var why);
        if (src?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data is not { } templateRaw)
        {
            var note = why ?? $"{askedCopy.Template} is not mounted, so there is nothing to compare against";
            checks.Add(new Check(Cleared, Verdict.Note, note));
            checks.Add(new Check(Kept, Verdict.Note, note));
            return;
        }

        try
        {
            static List<string> FragmentNames(UObject? part) =>
                part is not null && SlotAdd.Value(part, "Fragments") is UScriptArray frags
                    ? frags.Properties.Select(x => (x.GenericValue as FPackageIndex)?.Name)
                           .Where(n => n is { Length: > 0 } && n != "None").Select(n => n!).ToList()
                    : [];

            var templateLeaf = askedCopy.Template[(askedCopy.Template.LastIndexOf('/') + 1)..];
            var templateExports = PropertyAdd.PristineExports(provider, templateRaw, askedCopy.Template);
            var templatePart = templateExports.FirstOrDefault(e => e?.Name == templateLeaf);
            var templateFragNames = FragmentNames(templatePart);
            var templateSlots = templateFragNames
                .Where(n => templateExports.FirstOrDefault(e => e?.Name == n)?.ExportType == SlotAdd.SlotClassName)
                .ToHashSet(StringComparer.Ordinal);
            // EVERYTHING ELSE the template's own Fragments listed -- a UI data fragment, whatever
            // else a container carries -- in the template's OWN order, which is what "still
            // listed... in order" is checked against below. clearSlots only ever filters on
            // ExportType == CustomizationFragmentSlot, so this is exactly its complement.
            var templateNonSlots = templateFragNames.Where(n => !templateSlots.Contains(n)).ToList();

            var copyExports = PropertyAdd.PristineExports(provider, raw, p.PackageName,
                                                           p.IsOverride ? null : p.TemplatePath);
            var copyPart = copyExports.FirstOrDefault(e => e?.Name == p.Id);
            // Missing is a FAILURE to report, not nothing to complain about -- an absent export
            // reads as zero names listed, which would otherwise pass both checks by finding
            // nothing to object to. SlotAdd.Check treats the same situation as a complaint.
            if (copyPart is null)
            {
                checks.Add(new Check(Cleared, Verdict.Bad, $"no export {p.Id}"));
                checks.Add(new Check(Kept, Verdict.Bad, $"no export {p.Id}"));
                return;
            }
            var copyFragNames = FragmentNames(copyPart);

            if (templateSlots.Count == 0)
                checks.Add(new Check(Cleared, Verdict.Note, $"{templateLeaf} lists no {SlotAdd.SlotClassName} of its own"));
            else
            {
                var stillListed = copyFragNames.Where(templateSlots.Contains).ToList();
                checks.Add(stillListed.Count == 0
                    ? new Check(Cleared, Verdict.Good, $"{templateSlots.Count} of {templateLeaf}'s slot(s) gone from Fragments")
                    : new Check(Cleared, Verdict.Bad, "still lists " + string.Join(", ", stillListed)));
            }

            if (templateNonSlots.Count == 0)
                checks.Add(new Check(Kept, Verdict.Note, $"{templateLeaf} lists no other fragments"));
            else
            {
                // The SUBSEQUENCE of the copy's own Fragments made of names the template's
                // non-slot entries hold -- anything else in between (the mod's own new slot) is
                // simply not one of these names, so it drops out of this comparison for free.
                var keptInOrder = copyFragNames.Where(templateNonSlots.Contains).ToList();
                if (keptInOrder.SequenceEqual(templateNonSlots, StringComparer.Ordinal))
                    checks.Add(new Check(Kept, Verdict.Good, string.Join(", ", templateNonSlots)));
                else
                {
                    var missing = templateNonSlots.Except(copyFragNames).ToList();
                    checks.Add(new Check(Kept, Verdict.Bad,
                        missing.Count > 0
                            ? "missing: " + string.Join(", ", missing)
                            : $"out of order: expected {string.Join(", ", templateNonSlots)}, " +
                              $"has {string.Join(", ", keptInOrder)}"));
                }
            }
        }
        catch (Exception e)
        {
            checks.Add(new Check(Cleared, Verdict.Bad, $"cannot reparse: {e.Message}"));
            checks.Add(new Check(Kept, Verdict.Bad, $"cannot reparse: {e.Message}"));
        }
    }

    /// <summary>
    /// An override must not change what the package IMPORTS.
    ///
    /// Taken from a working, released community mod rather than invented: its two grown overrides
    /// have byte-identical import maps and imported-public-export hashes. Everything functional
    /// travels by NAME INDEX, so a changed import means the edit reached past names into the
    /// package's dependencies -- which is corruption about to replace a real game asset.
    ///
    /// The EXPORT map is deliberately excluded, and getting that wrong first is instructive: this
    /// check originally covered it and failed every working override, because removing a tag
    /// shrinks an export and SpliceExportData then rewrites the offsets of everything after it.
    /// That is the machinery behaving correctly. An invariant that fires on correct behaviour is
    /// worse than none, because the next person turns it off.
    ///
    /// Compared as a SLICE rather than by offset, since the name batch legitimately resizes and
    /// slides every following structure; the CONTENT is what must not change.
    /// </summary>
    /// <summary>
    /// A swatch tag as a person reads it. NOT SlotLeaf, which on this side of the file takes a
    /// chunk id -- two things called the same thing in one file, and the compiler caught it.
    /// </summary>
    private static string SwatchLeaf(string tag)
    {
        var dot = tag.LastIndexOf('.');
        return dot > 0 && dot < tag.Length - 1 ? tag[(dot + 1)..] : tag;
    }

    private static void OverrideInvariant(IFileProvider provider, byte[] made,
                                          ModBuild.BuiltPart p, ModProject mod, List<Check> checks,
                                          IReadOnlyList<ModOverride>? synthesised = null,
                                          IReadOnlyList<ModCopy>? synthesisedCopies = null)
    {
        var src = AssetCopy.PristineRead(provider, p.PackageName, out var why);
        if (src is null)
        {
            checks.Add(new Check("compared against the game's copy", Verdict.Note,
                                 why ?? "the pristine copy is not mounted"));
            return;
        }

        // COLOUR SWATCHES HANDED BACK, checked on this side too.
        //
        // The duplicate path has had this check since slot takeovers were built and the override
        // path had neither the edit nor the check. Both halves of the edit are checked, because
        // half of it is the failure that reads as "the tick did nothing": a swatch out of
        // HiddenSlots but still in LockedSlots is on the menu and permanently greyed out.
        var swatchesBack = mod.Overrides
            .FirstOrDefault(o => string.Equals(o.Target, p.PackageName, StringComparison.OrdinalIgnoreCase))
            ?.HidesFromMenu.Where(kv => !kv.Value).Select(kv => kv.Key).ToList() ?? [];
        if (swatchesBack.Count > 0)
        {
            var reads = PartDetails.ReadExports(
                AssetLoader.Read(() => PropertyAdd.PristineExports(provider, made, p.PackageName)),
                p.PackageName);

            var stuck = swatchesBack
                .Where(t => reads.HiddenSlots.Contains(t, StringComparer.OrdinalIgnoreCase) ||
                            reads.LockedSlots?.Contains(t, StringComparer.OrdinalIgnoreCase) == true)
                .ToList();
            checks.Add(stuck.Count == 0
                ? new Check("shows the swatches you unticked", Verdict.Good,
                            string.Join(", ", swatchesBack.Select(SwatchLeaf)) +
                            " -- in neither HiddenSlots nor LockedSlots")
                : new Check("shows the swatches you unticked", Verdict.Bad,
                            "still hidden or locked: " + string.Join(", ", stuck.Select(SwatchLeaf))));
        }

        static byte[]? Maps(byte[] raw)
        {
            // ImportedPublicExportHashesOffset (24) .. ExportMapOffset (32): the public export
            // hashes and the import map, and nothing after them.
            if (raw.Length < 52) return null;
            var from = BitConverter.ToInt32(raw, 24);
            var to = BitConverter.ToInt32(raw, 32);
            return from <= 0 || to <= from || to > raw.Length ? null : raw[from..to];
        }

        var a = Maps(src.Pieces[0].Data);
        var b = Maps(made);
        if (a is null || b is null)
        {
            checks.Add(new Check("compared against the game's copy", Verdict.Note,
                                 "could not read the map region of one of the two copies"));
            return;
        }

        // The override this part came from, so the check can tell a REQUESTED redirect from an
        // accident. Matched on the target package, which is the only thing the two share.
        //
        // THE BUILD'S OWN SYNTHESISED OVERRIDES COUNT TOO. A mission's table edit is created at
        // build time and is not in the project, so this found nothing, fell through to the "asks
        // for no redirect" branch below, and reported a correct eleven-row repoint as "corruption
        // about to replace a real game asset". The import map had gone 288 -> 288 bytes -- the
        // same size, ids replaced in place, which is exactly what a repoint does and nothing like
        // what corruption looks like.
        //
        // That branch's comment already said "third time this rule has outlived its premise".
        // This was the fourth, and the premise was never the rule's fault: it is that `asked` has
        // to mean "what the BUILD was asked for", not "what the project file happens to list".
        var asked = mod.Overrides.Concat(synthesised ?? []).FirstOrDefault(
            x => string.Equals(x.Target, p.PackageName, StringComparison.OrdinalIgnoreCase));

        // A COLOUR SLOT'S TRAITS ARE NEW SCRIPT IMPORTS -- taken out of `b` HERE, before the ladder
        // below runs, rather than judged by a branch of their own. R20 (review): a branch of its own
        // is a TERMINAL branch, and every branch below already returns on its own guard -- so an
        // override that asks for a colour slot AND one of the ladder's other edits (a repaint is the
        // documented case: "when the project already overrides ... that part for other edits, the
        // slots are added to that SAME package") was judged by whichever sibling matched FIRST, whose
        // own bound knows nothing about the slot's extra growth, and a correct build read as
        // corruption. MEASURED EXACT here, not bounded: SlotAdd's colour-slot imports
        // (CustomizationTraitMaterialTarget, and CustomizationTraitUIData for the small tile) are the
        // only two hashes this can be, so counting how many of those two are new says EXACTLY how
        // many entries to remove, whatever else the override also changed. Safe to trim off the
        // TAIL: slots run LAST in the build (see the override loop's own "SLOTS LAST" comment), so
        // every ZenPackage.AddScriptImport call they make -- always at the CURRENT export-map offset
        // -- lands after every other edit's own import-map growth, never inside it.
        //
        // HELD IN A VARIABLE THE REST OF THIS METHOD CAN SEE (R20 round 2): the declared-redirect
        // branch further down re-parses both packages and compares `ImportMap` itself rather than
        // `a`/`b`, for a REPOINT's own reasons (the hash-by-hash walk needs the structured form) --
        // but it is the same map, on the same two packages, and it must not count these same
        // trailing entries as a redirect that broke. One count, computed once, used in both places.
        var slotScriptImports = 0;
        if (asked is { AddSlots.Count: > 0 })
        {
            try
            {
                var wasMap = ZenPackage.Read(src.Pieces[0].Data).ImportMap;
                var nowMap = ZenPackage.Read(made).ImportMap;
                slotScriptImports = new[] { SlotAdd.MaterialTargetStruct, SlotAdd.UIDataStruct }
                    .Count(h => nowMap.Contains(h) && !wasMap.Contains(h));
                if (slotScriptImports > 0) b = b[..^(slotScriptImports * 8)];
            }
            catch { /* left untrimmed -- a package that will not even parse here fails below anyway */ }
        }

        // ROWS ASKED FOR, FOUND IN THE CONTAINER THAT SHIPPED.
        //
        // AddRow already reads its own result back before returning it, and that is not the same
        // claim: it checks the bytes it produced, this checks the bytes that were WRITTEN. Between
        // the two sit the splice fixups, the chunk write and the container header -- and a row
        // that survives the first and not the second is a mod that installs and offers nothing.
        if (asked is { AddRows.Count: > 0 })
        {
            var table = DataTableEdit.Measure(provider, made, p.PackageName, out var rowsWhy);
            if (table is null)
                checks.Add(new Check("the rows it adds", Verdict.Bad,
                                     $"the shipped table does not walk -- {rowsWhy}"));
            else
            {
                var absent = asked.AddRows.Select(r => r.Row)
                                  .Where(r => table.Find(r) is null).ToList();
                checks.Add(absent.Count == 0
                    ? new Check("the rows it adds", Verdict.Good,
                                $"{asked.AddRows.Count} added, {table.NumRows} row(s) in the " +
                                $"shipped table, {table.Trailing} trailing byte(s)")
                    : new Check("the rows it adds", Verdict.Bad,
                                $"{string.Join(", ", absent)} is not in the shipped table, which " +
                                $"has {table.NumRows} row(s)"));
            }
        }

        // A RECIPE ADDED IS A NEW IMPORT, on purpose: one FPackageObjectIndex and one hash per
        // recipe class, appended to the map. So the map is expected to GROW by exactly that and
        // nothing else. The build read the entry back through its own index; this is what shipped.
        if (asked is { AddRecipes.Count: > 0 } && asked.RepointImports.Count == 0)
        {
            var recipes = asked.AddRecipes.Select(r => r.Recipe).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var expected = recipes * 16;
            checks.Add(b.Length == a.Length + expected
                ? new Check("imports nothing new", Verdict.Good,
                            $"{recipes} recipe(s) imported: the map grew by {expected} bytes on the game's {a.Length:N0}")
                : new Check("imports nothing new", Verdict.Bad,
                            $"the import map is {b.Length:N0} bytes, not the game's {a.Length:N0} plus " +
                            $"{expected} for the recipe(s) -- something else changed its dependencies"));
            return;
        }

        // A MISSION ADDED IS A NEW IMPORT, for exactly the reason a recipe is: the row's `Mission`
        // IS an FPackageIndex, so the mission has to be one of the table's imports. One entry per
        // DISTINCT mission, because a second row naming the same one reuses the first's slot --
        // which is what the build's `imports.IndexOf(missionId)` is for.
        //
        // FOURTH TIME THIS RULE HAS OUTLIVED ITS PREMISE. It was written when an override could
        // only rename, and has been corrected for recipes, for material slots and for class-valued
        // properties. Here it called a table that grew a row "corruption about to replace a real
        // game asset" -- correct reasoning, stale premise, again. Worth noticing that the pattern
        // is not the rule being wrong: it is right every time for everything that existed when it
        // was written, and every new kind of ADD needs its own exemption written deliberately.
        if (asked is not null && asked.RepointImports.Count == 0 &&
            (asked.AddMissions.Count > 0 || asked.AddArcMissions.Count > 0))
        {
            // BOTH KINDS, COUNTED TOGETHER AND DISTINCTLY. A table row and a campaign-turn
            // placement import the mission the same way, and a mod doing both to one target would
            // reuse the first import -- which is what the build's `imports.IndexOf` is for.
            var missions = asked.AddMissions.Select(x => x.Mission)
                                .Concat(asked.AddArcMissions.Select(x => x.Mission))
                                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var expected = missions * 16;
            checks.Add(b.Length == a.Length + expected
                ? new Check("imports nothing new", Verdict.Good,
                            $"{missions} mission(s) imported: the map grew by {expected} bytes on " +
                            $"the game's {a.Length:N0}, and nothing in the table was displaced")
                : new Check("imports nothing new", Verdict.Bad,
                            $"the import map is {b.Length:N0} bytes, not the game's {a.Length:N0} plus " +
                            $"{expected} for the mission(s) -- something else changed its dependencies"));
            return;
        }

        // A SPAWNED ACTOR THAT DRAWS SOMETHING NEW IS A NEW IMPORT, for the same reason again: a
        // component's mesh IS an FPackageIndex, so a mesh the level never referenced has to become
        // one of its imports. One entry per DISTINCT mesh package, because a second actor drawing
        // the same thing reuses the first's slot -- which is what the build's `IndexOf(meshId)`
        // is for.
        //
        // FIFTH TIME, and the note above called it: the rule is right every time for everything
        // that existed when it was written, and every new kind of ADD needs its own exemption
        // written deliberately. Here it called a level that gained an Umbaran plant "corruption
        // about to replace a real game asset" -- correct reasoning, stale premise, again.
        //
        // Worth saying what it would take to stop paying this: the rule asks "did the import map
        // change when no redirect was requested", and every exemption is a different way of saying
        // "yes, and here is how much". A rule phrased as "the map grew by exactly what this mod's
        // adds account for" would need no exemption at all. Not changed now, because rewriting a
        // guard that has caught real corruption five times is its own risk and this is not the
        // moment for it.
        if (asked is { Spawns.Count: > 0 } && asked.RepointImports.Count == 0)
        {
            var meshes = asked.Spawns.Where(x => x.Mesh.Length > 0).Select(x => x.Mesh)
                              .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var expected = meshes * 16;
            checks.Add(b.Length == a.Length + expected
                ? new Check("imports nothing new", Verdict.Good,
                            $"{meshes} mesh(es) imported for {asked.Spawns.Count} spawned actor(s): " +
                            $"the map grew by {expected} bytes on the game's {a.Length:N0}")
                : new Check("imports nothing new", Verdict.Bad,
                            $"the import map is {b.Length:N0} bytes, not the game's {a.Length:N0} plus " +
                            $"{expected} for the spawned actor(s) -- something else changed its dependencies"));
            return;
        }

        // A CLASS-VALUED PROPERTY ADDED IS A NEW IMPORT, on purpose and for the same reason: its
        // value IS an import, so the asset it names has to be one. One entry per DISTINCT target,
        // because a second property pointed at the same asset reuses the row the first added --
        // which is what the build's `imports.IndexOf(targetId)` is for.
        //
        // Third time this rule has outlived its premise. It was written when an override could
        // only rename, was corrected for recipes, corrected again for material slots, and would
        // have called Daelen's per-tier cooldowns "corruption about to replace a real game asset"
        // -- correct reasoning, stale premise, again.
        if (asked is { AddClasses.Count: > 0 } && asked.RepointImports.Count == 0)
        {
            var targets = asked.AddClasses.Select(c => c.Target)
                               .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var grew = b.Length - a.Length;
            var whole = grew >= 0 && grew % 16 == 0;
            checks.Add(whole && grew <= targets * 16
                ? new Check("imports nothing new", Verdict.Good,
                            $"{grew / 16} class import(s) added for {targets} target(s): " +
                            $"the map grew by {grew} bytes on the game's {a.Length:N0}")
                : new Check("imports nothing new", Verdict.Bad,
                            $"the import map went {a.Length:N0} -> {b.Length:N0} bytes, which is not " +
                            $"a whole number of imports within the {targets} asset(s) it points at " +
                            "-- something else changed its dependencies"));
            return;
        }

        // A MATERIAL SLOT THAT NAMES A GAME MATERIAL IS A NEW IMPORT, on purpose, exactly as a
        // recipe is: one FPackageObjectIndex and one hash appended per named slot.
        //
        // This check said an override never changes its dependencies, which was true when it was
        // written and became false the moment a replacement could say what its material slots
        // wear. It read the growth as "corruption about to replace a real game asset" -- correct
        // reasoning about the wrong premise, and the second time in two days that a rule outlived
        // the thing it was describing.
        //
        // It is bounded rather than exact, and the reason is worth stating: an import is skipped
        // when the material declares no public export hash, and two slots naming the SAME material
        // still take an entry each (the writer adds one per slot, which is what the part path has
        // always done). So the honest expectation is "a whole number of import entries, and no
        // more than were asked for" -- which still catches a map that grew by anything else.
        var namedSlots = asked?.Materials.Count(m => m.Material.Length > 0) ?? 0;
        if (asked is not null && namedSlots > 0 && asked.RepointImports.Count == 0)
        {
            var grew = b.Length - a.Length;
            var whole = grew >= 0 && grew % 16 == 0;
            checks.Add(whole && grew <= namedSlots * 16
                ? new Check("imports nothing new", Verdict.Good,
                            $"{grew / 16} material import(s) added for {namedSlots} named slot(s): " +
                            $"the map grew by {grew} bytes on the game's {a.Length:N0}")
                : new Check("imports nothing new", Verdict.Bad,
                            $"the import map went {a.Length:N0} -> {b.Length:N0} bytes, which is not " +
                            $"a whole number of imports within the {namedSlots} slot(s) that named a " +
                            "material -- something else changed its dependencies"));
            return;
        }

        // NO BRANCH OF ITS OWN FOR SLOTS: they are already taken out of `b` above, by name and
        // exactly, before this ladder runs -- so a colour-slot-only override falls through to the
        // "identical" branch just below (its trimmed `b` equals `a`), and one paired with a repaint,
        // a recipe or any other sibling below is judged by that sibling's own unmodified, un-weakened
        // formula against the same trimmed `b`. A plain (non-colour) slot needed no branch here even
        // before this fix: SlotAdd's new export row writes the class and its default object straight
        // into ClassIndex/TemplateIndex as the script hash itself (ExportAdd.Spec's own words: "a
        // script import is its hash"), the same inline encoding DonorFor already reads there, with
        // no local import-map entry at all.

        if (asked is null || asked.RepointImports.Count == 0)
        {
            checks.Add(a.AsSpan().SequenceEqual(b)
                ? new Check("imports nothing new", Verdict.Good,
                            $"{a.Length:N0} bytes of import map identical to the game's")
                : new Check("imports nothing new", Verdict.Bad,
                            $"the import map differs ({a.Length:N0} -> {b.Length:N0} bytes) -- an " +
                            "override that asks for no redirect changes names, never dependencies, " +
                            "so this is corruption about to replace a real game asset"));
            return;
        }

        // A DECLARED REDIRECT IS EXPECTED TO CHANGE THIS REGION, so the invariant becomes a
        // stronger question rather than being waived: the IMPORT MAP must still be identical --
        // a redirect changes which package a slot names (in the store entry) and which hash it
        // looks for, never the map's own entries -- and every hash that moved must now be one the
        // TARGET actually publishes. A hash that matches nothing resolves to nothing, which is
        // exactly the silent failure repointImports exists to prevent.
        ZenPackage was, now;
        try { was = ZenPackage.Read(src.Pieces[0].Data); now = ZenPackage.Read(made); }
        catch (Exception e)
        {
            checks.Add(new Check("the redirect is the only change", Verdict.Note,
                                 $"one of the two copies would not parse -- {e.Message}"));
            return;
        }

        // THE SAME COLOUR-SLOT TRIM AS ABOVE, REUSED, not recomputed a second way (R20 round 2): a
        // redirect's own invariant is that the import MAP never changes size, and this is a
        // DIFFERENT view of the same map on the same two packages -- ImportMap here rather than the
        // Maps() byte slice above -- so the trailing entries a colour slot adds are still exactly
        // `slotScriptImports` of them, at the tail, for the reason given where that count was made.
        var nowImports = slotScriptImports > 0 ? now.ImportMap[..^slotScriptImports] : now.ImportMap;

        if (!was.ImportMap.AsSpan().SequenceEqual(nowImports))
        {
            checks.Add(new Check("imports nothing new", Verdict.Bad,
                                 "the import MAP differs, and a redirect never touches it -- " +
                                 "something else changed the package's dependencies"));
            return;
        }

        // THROUGH ModBuild.HashSource, for the reason it exists: a redirect aimed at one of the
        // mod's OWN assets names a package that is not mounted, and a copy's hashes are its
        // template's. Reading the target directly gets an empty set and reports every moved hash
        // as stray -- which it did, on a redirect that was entirely correct.
        var wanted = new HashSet<ulong>();
        foreach (var to in asked.RepointImports.Values)
            foreach (var h in PropertyAdd.PublicExportHashesOf(
                         provider, ModBuild.HashSource(mod, to, synthesisedCopies)))
                if (h != 0) wanted.Add(h);

        var wasHashes = was.ImportedPublicExportHashes;
        var nowHashes = now.ImportedPublicExportHashes;
        if (wasHashes.Length != nowHashes.Length)
        {
            checks.Add(new Check("imports nothing new", Verdict.Bad,
                                 $"the hash table changed length ({wasHashes.Length} -> " +
                                 $"{nowHashes.Length}), which a redirect never does"));
            return;
        }

        var moved = 0;
        var stray = new List<string>();
        for (var i = 0; i < wasHashes.Length; i++)
        {
            if (wasHashes[i] == nowHashes[i]) continue;
            if (wanted.Contains(nowHashes[i])) moved++;
            else stray.Add($"[{i}] 0x{nowHashes[i]:X16}");
        }

        checks.Add(stray.Count == 0 && moved > 0
            ? new Check("the redirect is the only change", Verdict.Good,
                        $"{moved} export hash(es) moved, every one of them published by the " +
                        $"asset it now points at; the import map is byte-identical")
            : moved == 0 && stray.Count == 0
                ? new Check("the redirect is the only change", Verdict.Bad,
                            "no export hash moved at all -- the override still looks up the " +
                            "original's export, so it would load the original")
                : new Check("the redirect is the only change", Verdict.Bad,
                            $"{stray.Count} hash(es) changed to something the target does not " +
                            $"publish ({string.Join(", ", stray.Take(4))}) -- those resolve to " +
                            "nothing"));
    }

    // ------------------------------------------------------------------ one part

    private static PartReport Part(IFileProvider provider, IoStoreReader back, ModProject mod,
                                   ModBuild.BuiltPart p,
                                   Dictionary<string, Registered> shipped, ModBuild.Result built,
                                   Dictionary<ulong, ContainerHeader.StoreEntry> ours)
    {
        var checks = new List<Check>();
        if (!back.Files.TryGetValue(p.ContainerPath, out var file))
        {
            checks.Add(new Check("in the container", Verdict.Bad, p.ContainerPath));
            return new PartReport(p.Id, false, checks);
        }
        checks.Add(new Check("in the container", Verdict.Good, $"{p.Bytes:N0} bytes"));

        // Hoisted out of the parse try below so AddedTags can reuse the same bytes -- but a read
        // failure has to report exactly what it did before that hoist (this same check, Bad, the
        // exception message) rather than escape Run(), which promises never to throw for a bad mod
        // (see its own doc comment). Same guard as lvlFile.Read() a few hundred lines up.
        byte[] raw;
        try { raw = file.Read(); }
        catch (Exception e)
        {
            checks.Add(new Check("reads back as a part", Verdict.Bad, e.Message));
            return new PartReport(p.Id, false, checks);
        }

        PartDetails.Detail made;
        try
        {
            var pkg = new IoPackage(new FByteArchive(p.Id, raw, provider.Versions),
                                    null, (FArchive?) null, null, provider as IVfsFileProvider);
            made = PartDetails.ReadExports(pkg.ExportsLazy.Select(x => x.Value).ToArray(), p.PackageName);
            checks.Add(new Check("reads back as a part", Verdict.Good, $"{pkg.ExportMapLength} exports"));
        }
        catch (Exception e)
        {
            checks.Add(new Check("reads back as a part", Verdict.Bad, e.Message));
            return new PartReport(p.Id, false, checks);
        }

        var asked = mod.Parts.FirstOrDefault(x => x.Id == p.Id);
        if (asked is not null) AgainstWhatWasAsked(checks, asked, made, p, built);
        if (asked is not null) WearsChosenMaterials(checks, provider, back, ours, asked, made, built);
        TintsThroughOwnMasks(checks, made, p);
        InTheRegistry(checks, shipped, p, asked);
        AddedTags(checks, provider, back, raw, shipped, p, asked);

        // The template is a part that demonstrably works in the game. Ours came from it, so a
        // difference is either a decision or an accident, and only the ones below are decisions.
        try
        {
            var tpl = PartDetails.Read(provider, p.TemplatePath);
            AgainstTheTemplate(checks, tpl, made, asked);
        }
        catch (Exception e)
        {
            checks.Add(new Check("compared against its template", Verdict.Note,
                                 $"could not read {p.TemplatePath}: {e.Message}"));
        }

        return new PartReport(p.Id, false, checks);
    }

    /// <summary>
    /// The NAME and the DESCRIPTION an override asked for, read out of the container.
    ///
    /// Same argument as the numbers, and the same hole: the build printed `display name:
    /// "Overclocked Coil"` on a package it had spliced and nothing afterwards ever asked the
    /// package what it was called. A splice that lands in the wrong FText, or in an export the
    /// game does not read, produces exactly that line.
    ///
    /// Which EXPORT carries the text is not fixed -- an item keeps its name on a UI fragment
    /// beside it -- so every export is searched and the check passes if any of them now reads what
    /// was asked for.
    /// </summary>
    private static void Words(List<Check> checks, IFileProvider provider, byte[] raw,
                              ModBuild.BuiltPart p, ModOverride ov)
    {
        var wantName = ov.DisplayName?.Trim() ?? "";
        var wantText = ov.Description?.Trim() ?? "";
        var wantTags = ov.RemoveTags.Count > 0 || ov.AddTags.Count > 0;
        if (wantName.Length == 0 && wantText.Length == 0 && !wantTags) return;

        List<UObject> exports;
        try
        {
            // THROUGH THE CONTAINER HEADER, the way every other reader in this project opens a
            // package. Constructing IoPackage with a null container reads top-level properties
            // fine and gives NOTHING for a nested struct -- so UIData.Description came back null
            // and three correct writes were reported as "nothing in the package carries" it.
            // PropertyAdd.Locate, which passes the header, reads the same path without trouble.
            var pkg = PropertyAdd.Open(provider, raw, p.Id,
                                       AssetCopy.CueHeaderFor(provider, p.PackageName, pristine: true));
            exports = pkg.ExportsLazy.Select(x => x.Value).ToList();
        }
        catch (Exception e)
        {
            checks.Add(new Check("its words read back", Verdict.Bad, $"cannot reparse: {e.Message}"));
            return;
        }

        void One(string what, string want, params string[] properties)
        {
            if (want.Length == 0) return;
            var seen = new List<string>();
            foreach (var e in exports)
                foreach (var property in properties)
                    if (TextOf(e, property) is { } got)
                    {
                        if (got.Trim() == want) { checks.Add(new Check(what, Verdict.Good, $"\"{got}\"")); return; }
                        seen.Add($"{e.Name}.{property} = \"{got}\"");
                    }

            checks.Add(new Check(what, Verdict.Bad,
                                 seen.Count == 0
                                     ? $"nothing in the package carries {string.Join(" or ", properties)}"
                                     : $"asked for \"{want}\"; found {string.Join(", ", seen)}"));
        }

        One("the name it reads back", wantName, "DisplayName", "UIData.Name");
        One("the description it reads back", wantText,
            "ShortDescription", "LongDescription", "UIData.Description");

        // THE TAGS, out of the container in the built package. A tag splice is located by the
        // container's full contents and refuses when that is not unique, which makes the WRITE
        // sound -- it says nothing about whether the result is the list that was wanted.
        if (!wantTags) return;

        // The list the build should have produced, rebuilt the same way the build builds it: the
        // ORIGINAL container, minus what was removed, plus what was added. Read from the pristine
        // package rather than through the mount, for the reason PristineExports exists -- with a
        // previous version of this same mod installed, the mount answers with the mod's copy and
        // the check would be comparing the build against itself.
        var pristine = AssetCopy.PristineRead(provider, p.PackageName, out var pristineWhy);
        var pristineRaw = pristine?.Pieces.FirstOrDefault(x => x.IsPackage)?.Data;
        if (pristineRaw is null)
        {
            checks.Add(new Check($"the tags in {ov.TagsProperty}", Verdict.Note,
                                 $"could not read the original to compare: {pristineWhy}"));
            return;
        }

        var before = PartDetails.TagsIn(
            AssetLoader.Read(() => PropertyAdd.PristineExports(provider, pristineRaw, p.PackageName)),
            ov.TagsProperty);
        var tagsWanted = before.Where(t => !ov.RemoveTags.Contains(t, StringComparer.Ordinal)).ToList();
        foreach (var t in ov.AddTags)
            if (!tagsWanted.Contains(t, StringComparer.Ordinal)) tagsWanted.Add(t);

        // NOT through the reparse. See GameplayTagEdit.Reads: an export whose class is a Blueprint
        // from another package comes back with no properties at all here, so asking the parsed
        // object for its tags answers "none" on an asset that is perfectly correct.
        checks.Add(GameplayTagEdit.Reads(raw, tagsWanted, out var tagWhy)
            ? new Check($"the tags in {ov.TagsProperty}", Verdict.Good,
                        $"{tagsWanted.Count} tag(s), {ov.RemoveTags.Count} out and {ov.AddTags.Count} in")
            : new Check($"the tags in {ov.TagsProperty}", Verdict.Bad, tagWhy ?? "not found"));
    }

    /// <summary>
    /// The text a property holds, for a plain name OR a dotted path one struct down.
    ///
    /// An ABILITY does not name itself the way a part does. A part carries DisplayName,
    /// ShortDescription and LongDescription at the top level; an ability carries `UIData.Name` and
    /// `UIData.Description` inside a struct. Checking only the part shape reported five perfectly
    /// good Precision Shot tiers as "nothing in the package carries ShortDescription or
    /// LongDescription" -- after the build had just written them correctly.
    /// </summary>
    private static string? TextOf(UObject export, string property)
    {
        var cut = property.IndexOf('.');
        if (cut > 0)
        {
            var inner = PartDetails.Unwrap(PartDetails.Value(export.Properties, property[..cut]));
            return inner is FStructFallback st ? TextIn(st.Properties, property[(cut + 1)..]) : null;
        }
        return TextIn(export.Properties, property);
    }

    private static string? TextIn(IEnumerable<FPropertyTag> props, string property) =>
        props.FirstOrDefault(x => x.Name.Text == property)?.Tag?.GenericValue switch
        {
            FText t => t.Text ?? "",
            string s => s,
            _ => null,
        };

    /// <summary>
    /// Every number this override asked for, read out of the container it shipped in.
    ///
    /// Reported as ONE check rather than one per number, with the values in it: four lines saying
    /// "ok" is four lines nobody reads, and the values are what a person actually wants to see.
    /// Anything wrong gets a line of its own, because that one does want reading.
    ///
    /// Numbers aimed at a DataTable ROW are not properties on an export and are not found this
    /// way. That is a check that does not apply rather than a check that failed -- the build's row
    /// walk is what wrote those -- so they are counted and named, never marked bad.
    /// </summary>
    private static void Numbers(List<Check> checks, IFileProvider provider, byte[] raw,
                                string packageName, ModOverride ov)
    {
        var read = new List<string>();
        var skipped = 0;

        void One(string export, string property, double expected)
        {
            switch (ScalarEdit.Reads(provider, raw, packageName, export, property, expected, out var why))
            {
                case ScalarEdit.Reading.Right: read.Add($"{property} = {expected:0.####}"); break;
                case ScalarEdit.Reading.Wrong:
                    checks.Add(new Check("a number it was asked to change", Verdict.Bad,
                                         why ?? $"{export}.{property}")); break;
                default: skipped++; break;
            }
        }

        foreach (var n in ov.Numbers) One(n.Row, n.Field, n.Now);
        foreach (var s in ov.AddScalars) One(s.Export, s.Name, s.Value);

        if (read.Count > 0)
            checks.Add(new Check("its numbers read back", Verdict.Good, string.Join(", ", read)));
        else if (skipped > 0)
            checks.Add(new Check("its numbers read back", Verdict.Good,
                                 $"{skipped} written into table rows, which the row walk checked"));
    }

    /// <summary>
    /// Every TYPED change this override asked for, read out of the container it shipped in.
    ///
    /// Numbers, moves, swaps and words each had this check and the typed value list did not. A
    /// name, a string or an array entry that silently failed to land would still have been
    /// reported "read back clean" -- and an ability that does not appear in game reads exactly
    /// like the game refusing to grant it, which is the wrong thing to go and investigate. The
    /// whole point of the Discord question this was written for is that somebody DID investigate
    /// the wrong thing and concluded the game forbade it.
    ///
    /// PATHS ARE COMPARED SPLIT, NOT AS TEXT. `/Game/A/B` and `/Game/A/B.B` are the same soft
    /// reference written two ways, and only one of them is what the array reads back as.
    /// </summary>
    private static void Values(List<Check> checks, IFileProvider provider, byte[] raw,
                               string packageName, ModOverride ov)
    {
        var p0 = packageName;
        var exports = PropertyAdd.PristineExports(provider, raw, packageName);
        var read = new List<string>();

        foreach (var v in ov.Values)
        {
            // A CONSTANT IN A BLUEPRINT'S CODE names a function and a type, not a property -- no
            // property is called "double" -- so asking the export for one could only miss. It is
            // read out of the function's decoded bytecode instead and compared with the game's own
            // copy: one fewer of the old value, one more of the new. Counted against the original
            // rather than "is the new value there", because a function can already hold the new
            // value somewhere else and would then pass without the edit ever landing.
            if (v.Kind == "scriptconst")
            {
                var built = ScriptPatch.Constants(provider, raw, p0, v.Row, v.Field);
                var original = AssetCopy.PristineRead(provider, p0, out _)?
                    .Pieces.FirstOrDefault(x => x.IsPackage)?.Data is { } pristineCode
                    ? ScriptPatch.Constants(provider, pristineCode, p0, v.Row, v.Field)
                    : null;
                if (built is null || original is null)
                {
                    checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                         $"{v.Row} does not decode as a Blueprint function " +
                                         (built is null ? "in the container" : "in the game's copy")));
                    continue;
                }

                static int Count(List<double> values, string text) =>
                    double.TryParse(text, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var d)
                        ? values.Count(x => x == d) : -1;
                if (Count(built, v.Was) != Count(original, v.Was) - 1 ||
                    Count(built, v.Now) != Count(original, v.Now) + 1)
                {
                    checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                         $"{v.Row} holds {Count(built, v.Was)} {v.Field} {v.Was} and " +
                                         $"{Count(built, v.Now)} {v.Field} {v.Now}; the game's copy holds " +
                                         $"{Count(original, v.Was)} and {Count(original, v.Now)}"));
                    continue;
                }
                read.Add($"{v.Row}: {v.Field} {v.Was} -> {v.Now}");
                continue;
            }

            var export = exports.FirstOrDefault(
                x => x is not null && string.Equals(x.Name, v.Row, StringComparison.OrdinalIgnoreCase));
            var prop = export?.Properties.FirstOrDefault(x => x.Name.Text == v.Field);

            // A DOTTED FIELD IS A PATH, NOT A NAME. `UIData.Name` is a property inside a struct
            // and no property is CALLED that, so the lookup above can only miss -- and this
            // reported the miss as "not on the container's copy" about a value the writer had
            // just written and read back. Third time today a checker refused correct work by
            // asking a narrower question than the writer answered. An ENTRY is a path too --
            // `Fail Text Map[...]` names no property, dotted key or not.
            if (prop is null && v.Field.IndexOfAny(['.', '[']) >= 0)
            {
                if (!PropertyAdd.Locate(provider, raw, p0, v.Row, v.Field,
                                        out var at, out var len, out var locWhy))
                {
                    checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                         $"{v.Row}.{v.Field} cannot be found in the container: {locWhy}"));
                    continue;
                }

                // Only text can be read straight out of the bytes without the name map, and text
                // is what nested edits are in practice. Anything else is reported as located
                // rather than claimed correct, because a check that cannot run must not pass.
                if (v.Kind == "text")
                {
                    if (PropertyAdd.ReadsAsText(raw, at, len) is not { } nested)
                    {
                        checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                             $"{v.Row}.{v.Field} does not read back as an FText"));
                        continue;
                    }
                    if (!string.Equals(nested, v.Now, StringComparison.Ordinal))
                    {
                        checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                             $"{v.Row}.{v.Field} reads “{nested}”, not “{v.Now}”"));
                        continue;
                    }
                    read.Add($"{v.Field} = “{v.Now}”");
                    continue;
                }

                // A NAME is eight bytes -- an index into the package's own name table, and a
                // number -- so it can be read back as surely as a text: Force Grip's status line
                // gets its tag written INTO its own entry this way.
                if (v.Kind == "name" && len == 8)
                {
                    var names = ZenPackage.Read(raw).Names;
                    var index = BitConverter.ToUInt32(raw, at);
                    var number = BitConverter.ToInt32(raw, at + 4);
                    var named = index < names.Count
                        ? names[(int) index] + (number > 0 ? $"_{number - 1}" : "")
                        : $"#{index}";
                    if (!string.Equals(named, v.Now, StringComparison.Ordinal))
                    {
                        checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                             $"{v.Row}.{v.Field} reads “{named}”, not “{v.Now}”"));
                        continue;
                    }
                    read.Add($"{v.Field} = {v.Now}");
                    continue;
                }

                checks.Add(new Check("a value it was asked to change", Verdict.Note,
                                     $"{v.Row}.{v.Field} is {len} byte(s) at {at}, but a {v.Kind} " +
                                     "inside a struct is not read back here"));
                continue;
            }

            if (prop is null)
            {
                checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                     $"{v.Row}.{v.Field} is not on the container's copy"));
                continue;
            }

            // A STRUCT ARRAY EDIT IS CHECKED BY COUNTING, because there is nothing to compare
            // against: a duplicated element holds exactly what the one beside it holds, so
            // "is my entry in there" cannot tell a successful copy from no copy at all. What
            // CAN be checked is that the array is one longer or one shorter than the game's.
            if (v.Kind is "structdup" or "structdrop")
            {
                if (prop.Tag?.GenericValue is not UScriptArray sArr)
                {
                    checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                         $"{v.Row}.{v.Field} is not an array in the container"));
                    continue;
                }
                read.Add($"{v.Field} {(v.Kind == "structdup" ? "+1" : "-1")} " +
                         $"({sArr.Properties.Count:N0} now)");
                continue;
            }

            // A MAP ENTRY ADDED is checked by its KEY, read by CUE4Parse's own map reader: the
            // container's map holds it. What the entry says is the business of the values after it.
            if (v.Kind == "mapadd")
            {
                if (prop.Tag?.GenericValue is not UScriptMap map)
                {
                    checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                         $"{v.Row}.{v.Field} is not a map in the container"));
                    continue;
                }
                var keys = map.Properties.Keys.Select(k => PropertyAdd.KeyText(k.GenericValue)).ToList();
                if (!keys.Contains(v.Now, StringComparer.Ordinal))
                {
                    checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                         $"{v.Row}.{v.Field} has no key [{v.Now}]"));
                    continue;
                }
                read.Add($"{v.Field} + [{v.Now}] ({keys.Count:N0} now)");
                continue;
            }

            if (v.Kind is "softadd" or "softremove")
            {
                if (prop.Tag?.GenericValue is not UScriptArray arr)
                {
                    checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                         $"{v.Row}.{v.Field} is not an array in the container"));
                    continue;
                }

                var want = PropertyEdit.SplitRef(v.Kind == "softadd" ? v.Now : v.Was);
                var holds = arr.Properties.Any(x =>
                {
                    var got = PropertyEdit.SplitRef(x?.GenericValue?.ToString() ?? "");
                    return got.Package is not null &&
                           string.Equals(got.Package, want.Package, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(got.Asset, want.Asset, StringComparison.OrdinalIgnoreCase);
                });

                var meant = v.Kind == "softadd";
                if (holds != meant)
                {
                    checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                         $"{v.Row}.{v.Field} {(meant ? "does not hold" : "still holds")} " +
                                         $"{want.Package}.{want.Asset} — it holds {arr.Properties.Count:N0}: " +
                                         string.Join(", ", arr.Properties.Select(x => x?.GenericValue?.ToString()))));
                    continue;
                }
                read.Add($"{v.Field} {(meant ? "+" : "-")} {want.Asset} ({arr.Properties.Count:N0} now)");
                continue;
            }

            // AN FText IS NOT ITS ToString. CUE4Parse's FText may print any of several things
            // depending on its history, and a checker that disagrees with its own writer refuses
            // correct work -- which this project has already paid for once, on SetName. So the
            // text is taken from the FText itself.
            var reads = prop.Tag?.GenericValue is FText ft
                ? ft.Text
                : prop.Tag?.GenericValue?.ToString() ?? "";
            if (!string.Equals(reads, v.Now, StringComparison.Ordinal))
            {
                checks.Add(new Check("a value it was asked to change", Verdict.Bad,
                                     $"{v.Row}.{v.Field} reads “{reads}”, not “{v.Now}”"));
                continue;
            }
            read.Add($"{v.Field} = “{v.Now}”");
        }

        if (read.Count > 0)
            checks.Add(new Check("its values read back", Verdict.Good, string.Join(", ", read)));
    }

    /// <summary>
    /// Every NAME this override re-pointed, counted in the container it shipped in.
    ///
    /// THIS CHECK WAS MISSING and its absence looked exactly like success. A repoint that
    /// silently did not land leaves a package that parses, carries the right imports, and has
    /// GROWN by the right number of bytes -- because the new name is appended to the name table
    /// whether or not anything ends up pointing at it. Every line the build printed about the
    /// repointed ability was true and none of them was the question.
    ///
    /// Counting is the right test rather than reading one property: a repoint does not know or
    /// care which properties named the old FName, only how many did, and `expect` is the number
    /// the author asserted. So afterwards the old name must be referenced ZERO times and the new
    /// one at least that many.
    /// </summary>
    private static void Tags(List<Check> checks, byte[] raw, ModOverride ov)
    {
        var read = new List<string>();
        foreach (var r in ov.Repoints)
        {
            var left = NameRefEdit.Count(raw, r.From);
            var landed = NameRefEdit.Count(raw, r.To);

            if (left > 0)
            {
                checks.Add(new Check("a name it was asked to re-point", Verdict.Bad,
                                     $"{r.From} is still referenced {left} time(s) — " +
                                     $"the re-point to {r.To} did not land"));
                continue;
            }
            if (landed < r.Expect)
            {
                checks.Add(new Check("a name it was asked to re-point", Verdict.Bad,
                                     $"{r.To} is referenced {landed} time(s), not the " +
                                     $"{r.Expect} that {r.From} was"));
                continue;
            }
            read.Add($"{TagLeaf(r.From)} -> {TagLeaf(r.To)} ({landed})");
        }

        if (read.Count > 0)
            checks.Add(new Check("its re-pointed names read back", Verdict.Good,
                                 string.Join(", ", read)));

        // The tail of a dotted name -- the part that differs between the two, and so the part
        // worth reading. Local because this class already has a Leaf that means something else.
        static string TagLeaf(string name)
            => name.LastIndexOf('.') is var at && at > 0 ? name[(at + 1)..] : name;
    }

    /// <summary>
    /// Every actor this override moved, read back OUT OF THE CONTAINER it shipped in.
    ///
    /// The build already read each move back out of the bytes it produced. This asks the same
    /// question of the packed container, which is the only copy the game will ever open -- the
    /// step between those two is where a number has been lost before.
    /// </summary>
    private static void Moves(List<Check> checks, IFileProvider provider, byte[] raw,
                              string packageName, ModOverride ov)
    {
        var read = new List<string>();
        foreach (var m in ov.Moves)
        {
            var found = LevelEdit.Locate(provider, raw, packageName, m.Actor, m.Path, out var why);
            if (found is null)
            {
                checks.Add(new Check("an actor it was asked to move", Verdict.Bad,
                                     $"{m.Actor}.{m.Path}: {why}"));
                continue;
            }
            if (Math.Abs(found.Value - m.Now) > 0.001)
            {
                checks.Add(new Check("an actor it was asked to move", Verdict.Bad,
                                     $"{m.Actor}.{m.Path} reads {found.Value}, not {m.Now}"));
                continue;
            }
            read.Add($"{m.Actor}.{m.Path} = {m.Now:0.###}");
        }

        if (read.Count > 0)
            checks.Add(new Check("what it moved reads back", Verdict.Good, string.Join(", ", read)));
    }

    /// <summary>
    /// Does the SHIPPED package draw what the swap asked for?
    ///
    /// Out of the packed container, through the same walk the build used -- because an
    /// FPackageIndex that resolves in a loose byte array and an FPackageIndex that resolves in a
    /// container the game will actually mount are the same number and not the same claim.
    /// </summary>
    private static void Swaps(List<Check> checks, IFileProvider provider, byte[] raw,
                              string packageName, ModOverride ov)
    {
        var read = new List<string>();
        foreach (var m in ov.Swaps)
        {
            var at = LevelEdit.LocatePointer(provider, raw, packageName, m.Actor, m.Path, out var why);
            if (at is null)
            {
                checks.Add(new Check("an actor it was asked to swap", Verdict.Bad,
                                     $"{m.Actor}.{m.Path}: {why}"));
                continue;
            }

            var leaf = at.Points[(at.Points.LastIndexOf('/') + 1)..];
            var bare = leaf[(leaf.LastIndexOf('.') + 1)..];
            var want = m.Mesh.Length == 0 ? "" : m.Mesh;

            // An empty swap means the component must draw NOTHING, and "nothing" has to be
            // checked as nothing rather than as a name that happens not to match.
            var ok = want.Length == 0
                ? at.Value == 0
                : bare.Equals(want, StringComparison.OrdinalIgnoreCase);

            if (!ok)
            {
                checks.Add(new Check("an actor it was asked to swap", Verdict.Bad,
                                     $"{m.Actor}.{m.Path} draws {(at.Value == 0 ? "nothing" : bare)}, " +
                                     $"not {(want.Length == 0 ? "nothing" : want)}"));
                continue;
            }
            read.Add($"{m.Actor} = {(want.Length == 0 ? "nothing" : want)}");
        }

        if (read.Count > 0)
            checks.Add(new Check("what it swapped reads back", Verdict.Good, string.Join(", ", read)));
    }

    /// <summary>An asset path as a person reads it: the leaf, without the object suffix.</summary>
    private static string Short(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(nothing)";
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var dot = leaf.IndexOf('.');
        return dot >= 0 ? leaf[..dot] : leaf;
    }

    private static void AgainstWhatWasAsked(List<Check> checks, ModPart asked,
                                            PartDetails.Detail made, ModBuild.BuiltPart p,
                                            ModBuild.Result built)
    {
        if (!string.IsNullOrWhiteSpace(asked.Display))
        {
            var label = made.Label(p.Id);
            checks.Add(label == asked.Display
                ? new Check("name in the menu", Verdict.Good, $"\"{label}\"")
                : new Check("name in the menu", Verdict.Bad,
                            $"shows \"{label}\", was asked for \"{asked.Display}\""));
        }

        // A PART THAT AUTHORS ITS PIECES IS GRADED PIECE BY PIECE.
        //
        // `made.MeshPath` is Meshes[0], which on a whole-body garment is the BOOTS. Grading a
        // correct five-piece build on its boots reports Bad on work that is right -- and, worse,
        // reports Good for a build where four pieces still wear the game's geometry. This gates
        // Install, so it has to be the thing actually checked.
        if (asked.HasRegions)
        {
            var keys = PartDetails.RegionKeys(made);
            foreach (var group in made.MeshFragments)
            {
                var key = keys.FirstOrDefault(k => k.Fragment == group.Key).Key ?? "";
                var entry = key.Length > 0 ? asked.RegionNamed(key) : null;

                // A piece the author did not name is meant to keep the template's own geometry.
                // Nothing to check: there was no request to satisfy.
                if (entry is null || string.IsNullOrWhiteSpace(entry.Mesh)) continue;

                var want = entry.Mesh.Split('.')[0];
                var wears = group.Select(m => m.MeshPath).ToList();
                var okPiece = wears.Any(w =>
                    w.StartsWith(want, StringComparison.OrdinalIgnoreCase) ||
                    built.Parts.Any(b => b.IsCopy &&
                        w.StartsWith(b.PackageName, StringComparison.OrdinalIgnoreCase) &&
                        b.TemplatePath.Equals(want, StringComparison.OrdinalIgnoreCase)));

                checks.Add(okPiece
                    ? new Check($"the {key} piece wears the chosen mesh", Verdict.Good,
                                string.Join(", ", wears.Select(Short)))
                    : new Check($"the {key} piece wears the chosen mesh", Verdict.Bad,
                                $"points at {string.Join(", ", wears.Select(Short))}, " +
                                $"was asked for {Short(want)}"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(asked.Mesh))
        {
            var wantedPackage = asked.Mesh.Split('.')[0];
            var wearing = made.MeshPath ?? "";

            // A part that carries its own copy points at the COPY, not at the game's asset, and
            // that is the whole point of asking for one. So the copy counts as the chosen mesh --
            // but only the copy OF THAT MESH, matched through the build's record of where each
            // copy came from, rather than by trusting any path inside our own folder.
            var throughCopy = built.Parts.Any(
                b => b.IsCopy &&
                     wearing.StartsWith(b.PackageName, StringComparison.OrdinalIgnoreCase) &&
                     b.TemplatePath.Equals(wantedPackage, StringComparison.OrdinalIgnoreCase));

            var ok = wearing.StartsWith(wantedPackage, StringComparison.OrdinalIgnoreCase) || throughCopy;
            checks.Add(ok
                ? new Check("wears the chosen mesh", Verdict.Good,
                            wearing + (throughCopy ? "   (the mod's own copy)" : ""))
                : new Check("wears the chosen mesh", Verdict.Bad,
                            $"points at {made.MeshPath ?? "(nothing)"}, was asked for {wantedPackage}"));
        }

        // The checkbox, checked against the part rather than against the template that was picked
        // to satisfy it. Selecting a template that happens to hide the head is not the same thing
        // as the part hiding the head, and that difference is exactly how this last went wrong.
        //
        // UNLESS the template was NAMED. Then head-hiding is inherited rather than requested, and
        // holding the copy to the checkbox makes the two settings fight: a stock clone helmet
        // hides the head by its own mechanism, so a faithful duplicate of it does too, and the
        // build was refused with "hides the head and was not asked to" -- a true sentence about a
        // part that is behaving exactly like the one it was copied from.
        if (!string.IsNullOrWhiteSpace(asked.Template))
            // GOOD ONLY WHEN IT ACTUALLY HIDES, or when nobody asked it to.
            //
            // This was Verdict.Good unconditionally -- it printed "no, inherited from X" and
            // passed. So a part whose author ticked "hides the head", built from a template that
            // does not, reported OK and shipped with the face showing through the helmet. The
            // relaxation above is still right for the case it was written for (a faithful
            // duplicate inheriting the original's behaviour must not be called a fault); it was
            // just applied to the case where the author asked for something and did not get it.
            //
            // AND IT ASKS ABOUT THE FACE, not the hair. HidesHead is true for either, so a
            // template hiding only `Hair.Hair.Mesh` satisfied this and reported "yes, inherited
            // from X" -- about a helmet the wearer's face renders straight through. That is the
            // whole fault the relaxation was supposed to stop shipping, passing itself.
            checks.Add(made.HidesFace || !asked.EffectiveHidesHead
                ? new Check("hides the head", Verdict.Good,
                            (made.HidesFace ? "yes" : "no") + $", inherited from {asked.Template}")
                : new Check("hides the head", Verdict.Bad,
                            $"asked to hide the head, and {asked.Template} hides only the hair -- " +
                            "the face will show through. The builder now splices the face tag in " +
                            "for a named template too, so a rebuild should clear this"));
        else
            checks.Add(made.HidesHead == asked.EffectiveHidesHead
                ? new Check("hides the head", Verdict.Good, asked.EffectiveHidesHead ? "yes, as asked" : "no, as asked")
                : new Check("hides the head", Verdict.Bad,
                            asked.EffectiveHidesHead
                                ? "was asked to hide the head and does not -- the face will show through"
                                : "hides the head and was not asked to"));
    }

    /// <summary>
    /// EACH PIECE OF A GARMENT PAINTS THROUGH ITS OWN TINT MASK, read off the part that was built.
    ///
    /// A legs-and-boots part carries one tint-mask fragment per piece, and the build used to point
    /// every one of them at the same mask, so the boot was painted through the legs' mask. Nothing
    /// throws and every other check passes, so this is the one that can tell.
    ///
    /// IT RE-DERIVES NOTHING. The build records what it did to each of the template's tint
    /// fragments (<see cref="ModBuild.BuiltPart.TintExpected"/>, in the template's fragment order,
    /// which the built part shares because it is the template's bytes with names substituted) and
    /// this reads the finished part back against exactly that. An earlier version worked out which
    /// piece each built fragment was FOR from its slot names and got a different answer from the
    /// build where a fragment names no slot, or only slots the tokeniser does not know, or shares
    /// its mask with a fragment for another piece -- and called a correct build Bad, which blocks
    /// the install.
    ///
    ///   - a fragment the build substituted must name the mask it was given;
    ///   - one it kept must not have ended up on a copy the build made for a piece.
    ///
    /// Only asked of a part the build substituted PIECE BY PIECE; a part that names one `tintMask`
    /// collapses every mask to it on purpose, and a template whose masks are for one piece has
    /// nothing to mix up.
    /// </summary>
    private static void TintsThroughOwnMasks(List<Check> checks, PartDetails.Detail made, ModBuild.BuiltPart p)
    {
        if (p.TintExpected is not { Count: > 0 } expected) return;

        const string What = "each piece tints through its own mask";
        var fragments = made.TintFragments ?? [];
        if (fragments.Count != expected.Count)
        {
            checks.Add(new Check(What, Verdict.Note,
                $"could not compare: the part has {fragments.Count} tint fragment(s), the build set " +
                $"{expected.Count}"));
            return;
        }

        var theirs = expected.Where(e => e.Substituted).Select(e => PackageHalf(e.Mask))
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var good = new List<string>();
        var bad = new List<string>();
        for (var i = 0; i < fragments.Count; i++)
        {
            var got = PackageHalf(fragments[i].Mask);
            var on = fragments[i].Slots.Count > 0 ? string.Join(", ", fragments[i].Slots) : $"fragment {i}";
            var piece = expected[i].Piece.Length > 0 ? expected[i].Piece : on;

            if (expected[i].Substituted)
            {
                if (string.Equals(got, PackageHalf(expected[i].Mask), StringComparison.OrdinalIgnoreCase))
                    good.Add($"{piece}: {Short(got)}");
                else
                    bad.Add($"the {piece} fragment ({on}) paints through {Short(got)}, not the " +
                            $"{Short(expected[i].Mask)} the build gave it");
            }
            else if (theirs.Contains(got))
                bad.Add($"the {piece} fragment ({on}) was kept as the template has it but paints " +
                        $"through {Short(got)}, a mask the build made for another piece");
        }

        checks.Add(bad.Count == 0
            ? new Check(What, Verdict.Good, string.Join(", ", good.Distinct()))
            : new Check(What, Verdict.Bad, string.Join("; ", bad)));
    }

    /// <summary>
    /// The package half of an object path, split where the OBJECT begins: at the first dot AFTER the
    /// last slash. A mod folder may contain a dot, and cutting at the first dot anywhere turned
    /// two different packages of one mod into the same string.
    /// </summary>
    private static string PackageHalf(string objectPath)
    {
        var dot = objectPath.IndexOf('.', objectPath.LastIndexOf('/') + 1);
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    /// <summary>
    /// THE MATERIAL EACH SLOT WAS TOLD TO WEAR, read off the mesh that was built.
    ///
    /// "Choose..." in the editor records a game material for a slot and nothing else, and until
    /// 2026-10-31 a part that kept the game's mesh built clean, verified clean and wore the base
    /// mesh's own material -- the choice was dropped on the floor with no symptom anywhere but the
    /// game. So the question is asked of the BUILT mesh: for every slot that names a material, what
    /// does the mesh the part points at import for that slot?
    ///
    /// A slot that names a material and paints nothing must import THAT material, resolved by
    /// CUE4Parse through the export hash as well as the package, so a redirect that moved the
    /// package and left the hash behind reads back as a slot that wears nothing.
    ///
    /// A slot that names a material AND is repainted wears a COPY of it, and the copy is the mod's
    /// own, which the mounted game cannot resolve. It is read off the bytes instead
    /// (<see cref="NamedMaterial.ImportOfSlot"/>) and compared with what the mod's copy publishes:
    /// the import must name that package AND one of its export hashes. "Points at the mod's own
    /// assets" counts packages and never saw a stale hash; this does.
    /// </summary>
    private static void WearsChosenMaterials(List<Check> checks, IFileProvider provider,
                                             IoStoreReader back,
                                             Dictionary<ulong, ContainerHeader.StoreEntry> store,
                                             ModPart asked, PartDetails.Detail made,
                                             ModBuild.Result built)
    {
        // WHICH MESH, per piece: a part with pieces authors a material list for each of them.
        var jobs = new List<(string Piece, List<ModMaterial> Materials, List<string> Meshes)>();
        if (asked.HasRegions)
        {
            var keys = PartDetails.RegionKeys(made);
            foreach (var group in made.MeshFragments)
            {
                var key = keys.FirstOrDefault(k => k.Fragment == group.Key).Key ?? "";
                var entry = key.Length > 0 ? asked.RegionNamed(key) : null;
                if (entry is null || !entry.Materials.Any(m => m.Material.Length > 0)) continue;
                jobs.Add((key, entry.Materials, group.Select(m => m.MeshPath).ToList()));
            }
        }
        else if (asked.Materials.Any(m => m.Material.Length > 0))
            jobs.Add(("", asked.Materials, made.Meshes.Select(m => m.MeshPath).ToList()));

        const string Fix = " -- clear the choice, or see the build's note for this slot";

        foreach (var (piece, materials, meshes) in jobs)
        {
            var prefix = piece.Length > 0 ? piece + " " : "";
            var wearing = meshes.Select(m => m.Split('.')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var ours = wearing.Select(w => built.Parts.FirstOrDefault(
                    b => b.IsCopy && !b.IsTexture && !b.IsOverride &&
                         w.Equals(b.PackageName, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(b => b is not null);

            // The game's own mesh, for the slots it already wears right (nothing was carried).
            List<NamedMaterial.Slot>? gameMesh = null;
            CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh? mesh = null;
            byte[]? raw = null;
            string? readWhy = null;

            for (var i = 0; i < materials.Count; i++)
            {
                if (materials[i].Material.Length == 0) continue;
                var want = ModBuild.PackageOf(materials[i].Material) ?? materials[i].Material;
                var painted = !materials[i].IsNamedOnly;
                var what = $"{prefix}wears the material you chose on material {i + 1}";

                if (ours is null)
                {
                    // A part that kept the game's mesh is right when the game's mesh already wears
                    // what was chosen -- the build carries no copy for that.
                    if (!painted && wearing.Count > 0)
                    {
                        gameMesh ??= NamedMaterial.SlotsOf(provider, wearing[0], out _) ?? [];
                        if (i < gameMesh.Count &&
                            string.Equals(gameMesh[i].Package, want, StringComparison.OrdinalIgnoreCase))
                        {
                            checks.Add(new Check($"{prefix}wears the material you chose on {gameMesh[i].Name}",
                                                 Verdict.Good,
                                                 $"material {i + 1}: {Short(want)}, which the game's own mesh already wears"));
                            continue;
                        }
                    }

                    checks.Add(new Check(what, Verdict.Bad,
                        $"{Short(want)} was chosen for it, but the part points at " +
                        $"{(wearing.Count > 0 ? Short(wearing[0]) : "no mesh")} itself, not at a copy " +
                        "of the mesh carrying the choice -- it wears the game's own material"));
                    continue;
                }

                try
                {
                    if (mesh is null)
                    {
                        if (!back.Files.TryGetValue(ours.ContainerPath, out var file))
                        {
                            checks.Add(new Check(what, Verdict.Bad, $"the mesh copy {ours.ContainerPath} is not in the container"));
                            continue;
                        }
                        raw = file.Read();
                        var pkg = PropertyAdd.Open(provider, raw, ours.Id, back.ContainerHeader);
                        var found = pkg.ExportsLazy.Select(x => x.Value)
                            .OfType<CUE4Parse.UE4.Assets.Exports.SkeletalMesh.USkeletalMesh>().FirstOrDefault();
                        if (found is null)
                        {
                            // A NOTE: nothing was READ and found wrong; this reader found nothing to read.
                            checks.Add(new Check(what, Verdict.Note,
                                "could not confirm what the mesh copy wears: it holds no skeletal mesh this reader can open"));
                            continue;
                        }
                        mesh = found;
                    }

                    var slots = NamedMaterial.SlotsOf(mesh);
                    if (i >= slots.Count)
                    {
                        checks.Add(new Check(what, Verdict.Bad,
                            $"{Short(want)} was chosen for it, but the built mesh has only {slots.Count} slot(s)" + Fix));
                        continue;
                    }

                    var slot = slots[i];
                    var title = $"{prefix}wears the material you chose on {slot.Name}";

                    if (!painted)
                    {
                        checks.Add(string.Equals(slot.Package, want, StringComparison.OrdinalIgnoreCase)
                            ? new Check(title, Verdict.Good, $"material {i + 1}: {Short(want)}")
                            : new Check(title, Verdict.Bad,
                                $"material {i + 1} wears " +
                                (slot.Package.Length == 0 ? "nothing that loads" : Short(slot.Package)) +
                                $", not the {Short(want)} you chose" + Fix));
                        continue;
                    }

                    // NAMED AND REPAINTED: the import must name one of THIS MOD'S copies of the
                    // chosen material, and a hash that copy actually publishes.
                    //
                    // BAD ONLY FOR WHAT WAS READ. A slot whose import could not be located is a
                    // note: the build applied the choice through the same import map, and a
                    // verifier that cannot see it must not refuse the install on its own blindness.
                    // The two Bads below are an import that WAS read and names the wrong thing.
                    if (!store.TryGetValue(ours.ChunkId, out var entry2))
                    {
                        checks.Add(new Check(title, Verdict.Note,
                            $"could not confirm that {slot.Name} wears this mod's repainted copy of {Short(want)}: " +
                            "the mesh copy has no store entry to read its imports from"));
                        continue;
                    }
                    var imported = NamedMaterial.ImportOfSlot(raw!, entry2, mesh, i, out var importWhy);
                    if (imported is null)
                    {
                        checks.Add(new Check(title, Verdict.Note,
                            $"could not confirm that {slot.Name} wears this mod's repainted copy of {Short(want)}: {importWhy}"));
                        continue;
                    }

                    var copies = built.Parts.Where(b => b.IsCopy && !b.IsTexture && !b.IsOverride &&
                                                        b.TemplatePath.Equals(want, StringComparison.OrdinalIgnoreCase))
                                           .ToList();
                    var landed = copies.FirstOrDefault(c => c.ChunkId == imported.Value.Package);
                    if (landed is null)
                    {
                        // BAD ONLY WITH A SECOND WITNESS. The structural finder (ImportOfSlot) read a
                        // package that is none of this mod's copies of the material, but it is a
                        // search over raw bytes, and a lone reading is not proof of a fault that
                        // refuses an install. It is Bad when CUE4Parse -- which resolves the import
                        // the way the game does -- independently sees a package on that slot, or
                        // when the package read is one of THIS build's own chunks (the mesh points
                        // at something the mod wrote that is not the repainted material). Otherwise
                        // the two readers disagree and the honest answer is "could not confirm".
                        var oneOfOurs = built.Parts.Any(b => b.ChunkId == imported.Value.Package);
                        if (slot.Package.Length == 0 && !oneOfOurs)
                        {
                            checks.Add(new Check(title, Verdict.Note,
                                $"the two readers disagree about {slot.Name}; could not confirm that it wears " +
                                $"this mod's repainted copy of {Short(want)}"));
                            continue;
                        }
                        checks.Add(new Check(title, Verdict.Bad,
                            $"material {i + 1} imports " +
                            (slot.Package.Length > 0 ? Short(slot.Package) : "a package that is not a copy of " + Short(want)) +
                            $", not this mod's repainted copy of {Short(want)}" + Fix));
                        continue;
                    }
                    ulong[] published;
                    try
                    {
                        published = back.Files.TryGetValue(landed.ContainerPath, out var copyFile)
                            ? ZenPackage.Read(copyFile.Read()).PublicExportHashes : [];
                    }
                    catch (Exception) { published = []; }

                    checks.Add(published.Contains(imported.Value.Hash)
                        ? new Check(title, Verdict.Good,
                            $"material {i + 1}: this mod's repainted copy of {Short(want)} ({Short(landed.PackageName)})")
                        : new Check(title, Verdict.Bad,
                            $"material {i + 1} imports this mod's copy {Short(landed.PackageName)} but names an " +
                            "export it does not have, so the slot would wear nothing"));
                }
                catch (Exception e)
                {
                    // A NOTE, like the unreadable import above: a reader that threw has not seen a
                    // wrong material, and must not refuse an install on its own failure.
                    checks.Add(new Check(what, Verdict.Note,
                        $"could not confirm what the built mesh wears: it could not be read ({e.Message})"));
                }
            }
        }
    }

    /// <summary>
    /// What the shipped registry says about this part. Everything here is invisible in the game
    /// when it is wrong: the part does not appear, and a missing part looks exactly like a mod
    /// that failed to install.
    /// </summary>
    private static void InTheRegistry(List<Check> checks, Dictionary<string, Registered> shipped,
                                      ModBuild.BuiltPart p, ModPart? asked)
    {
        if (shipped.Count == 0) return;   // already reported once, against the whole mod

        if (!shipped.TryGetValue(p.Id, out var entry))
        {
            checks.Add(new Check("listed in the registry", Verdict.Bad,
                                 "the game would never look at this part"));
            return;
        }
        var reg = entry.Part;
        checks.Add(new Check("listed in the registry", Verdict.Good, reg.Path));

        // The id the game files this part under. Inheriting the TEMPLATE's meant two parts
        // claiming one id, and ours was the one that lost.
        checks.Add(string.Equals(entry.PrimaryAssetName, p.Id, StringComparison.Ordinal)
            ? new Check("has an id of its own", Verdict.Good, p.Id)
            : new Check("has an id of its own", Verdict.Bad,
                        $"filed as '{entry.PrimaryAssetName ?? "(none)"}' rather than '{p.Id}' -- " +
                        "it would collide with whatever else claims that id"));

        // "open" is available to everyone. A part that kept its template's gate is there, and
        // correct, and still never shows up -- which is how three of five parts went missing.
        checks.Add(reg.Gate == "open"
            ? new Check("unlocked for everyone", Verdict.Good, "no gate")
            : new Check("unlocked for everyone", Verdict.Bad,
                        $"gated '{reg.Gate}' -- inherited from its template, and the player will " +
                        "not see this part unless they happen to satisfy it"));

        // A part restricted to specific characters was a template for one named character. Not a
        // failure -- someone may want that -- but never what an unasked-for restriction means.
        if (reg.Characters.Count > 0 && string.IsNullOrWhiteSpace(asked?.ForCharacter))
            checks.Add(new Check("offered to every character", Verdict.Note,
                                 "restricted to " + string.Join(", ", reg.Characters) +
                                 " -- inherited from its template"));
    }

    /// <summary>
    /// A part's `addTags` reach BOTH records the menu can filter on, not just the package.
    ///
    /// Measured in game 2026-09-24: the menu filters on the REGISTRY copy of a part's tags, and
    /// <see cref="ModBuild.PartTagsFor"/> strips every `br.Customization.Accepts.*` tag from that
    /// copy before the author's own additions go in. A tag present in AllowedSlots and missing from
    /// CustomizationPartTags (or the other way round) is a part that LOOKS tagged and is not
    /// offered -- the exact failure this exists to catch before it reaches the game.
    /// </summary>
    private static void AddedTags(List<Check> checks, IFileProvider provider, IoStoreReader back,
                                  byte[] raw, Dictionary<string, Registered> shipped,
                                  ModBuild.BuiltPart p, ModPart? asked)
    {
        if (asked is null || asked.AddTags.Count == 0) return;
        // registryOptional: false -- every customization PART is a primary asset and gets a
        // registry entry; a part missing one here would be a real fault, not a design choice, so
        // it stays Bad the way it always has.
        AddedTagsCheck(checks, provider, back, raw, shipped, p, asked.AddTags, registryOptional: false);
    }

    /// <summary>
    /// A copy's `addTags` reach BOTH records the menu can filter on, not just the package -- the
    /// same question <see cref="AddedTags"/> asks of a part's, and for the same reason: the menu
    /// filters on the REGISTRY copy of a part's tags, and a tag present in AllowedSlots and
    /// missing from CustomizationPartTags (or the other way round) is a copy that LOOKS tagged
    /// and is not offered.
    ///
    /// registryOptional: true, because a copy is registered only when its template is a primary
    /// asset (ModBuild.cs's copies loop: `if (!cpRegistered) continue;` skips every registry edit,
    /// including addTags', for one that is not) -- so a copy with no registry entry at all is not
    /// a fault this check can report as one; see <see cref="AddedTagsCheck"/>.
    /// </summary>
    private static void AddedCopyTags(List<Check> checks, IFileProvider provider, IoStoreReader back,
                                      byte[] raw, Dictionary<string, Registered> shipped,
                                      ModBuild.BuiltPart p, ModCopy asked)
    {
        if (asked.AddTags.Count == 0) return;
        AddedTagsCheck(checks, provider, back, raw, shipped, p, asked.AddTags, registryOptional: true);
    }

    /// <summary>
    /// Shared by <see cref="AddedTags"/> (a part) and <see cref="AddedCopyTags"/> (a copy) -- the
    /// two used to carry this same ~70 lines twice over, differing only in which project type's
    /// `AddTags` they read (both are plain `List&lt;string&gt;`, so there is nothing left to read
    /// off the caller's own type once that list is in hand) and, now, in whether a missing
    /// registry entry is expected.
    ///
    /// THROUGH THE CONTAINER HEADER, the way Words() reads a spliced property -- see its own
    /// comment on this. The plain null-header package Part()/Copy() already built above is enough
    /// for what each already checks: CustomizationFragmentMesh and its neighbours are engine-native
    /// classes and resolve without help. AllowedSlots lives on the part's or copy's OWN top-level
    /// export, whose class is an IMPORT from another package (the Blueprint base every
    /// CustomizationPart derives from); with no header to resolve that import the export comes
    /// back with no class and no properties at all. First measured here as "carries none of its
    /// added tags" on a build whose own log had just printed them into AllowedSlots -- the write
    /// was fine, this reader just was not given enough to see it.
    /// </summary>
    private static void AddedTagsCheck(List<Check> checks, IFileProvider provider, IoStoreReader back,
                                       byte[] raw, Dictionary<string, Registered> shipped,
                                       ModBuild.BuiltPart p, IReadOnlyList<string> addTags,
                                       bool registryOptional)
    {
        List<UObject> exports;
        try
        {
            var pkg = new IoPackage(new FByteArchive(p.Id, raw, provider.Versions),
                                    back.ContainerHeader, (FArchive?) null, null,
                                    provider as IVfsFileProvider);
            exports = pkg.ExportsLazy.Select(x => x.Value).ToList();
        }
        catch (Exception e)
        {
            checks.Add(new Check("its added tags read back", Verdict.Bad, $"cannot reparse: {e.Message}"));
            return;
        }

        var inPackage = PartDetails.TagsIn(exports, "AllowedSlots");
        var missingPackage = addTags.Where(t => !inPackage.Contains(t, StringComparer.Ordinal)).ToList();

        // shipped.Count == 0 means the registry itself failed to read, which "the mod ships a
        // registry" already reported once for the whole mod -- repeating that as a per-part
        // failure here would say nothing new and bury the one line that does.
        //
        // registryOptional (F2): a COPY whose template is not a primary asset gets no registry
        // entry at all, on purpose (see this method's own doc) -- asking the registry half of a
        // container the build never wrote is not a failure to report as one, so a missing entry
        // is folded into the same Note/Bad split a global registry-read failure already gets,
        // rather than falling through to "missing from the registry" below as if the tag had
        // simply not landed.
        var hasEntry = shipped.TryGetValue(p.Id, out var entry);
        if (shipped.Count == 0 || (registryOptional && !hasEntry))
        {
            var registryNote = shipped.Count == 0
                ? "the registry could not be read to check the other half"
                : "its template is not a primary asset, so it has no registry entry to check";
            checks.Add(missingPackage.Count == 0
                ? new Check("its added tags read back", Verdict.Note,
                            $"{addTags.Count} tag(s) in AllowedSlots; {registryNote}")
                : new Check("its added tags read back", Verdict.Bad,
                            "missing from the package's AllowedSlots: " +
                            string.Join(", ", missingPackage)));
            return;
        }

        var inRegistry = hasEntry ? entry.Part.Tags : (IReadOnlyList<string>) Array.Empty<string>();
        var missingRegistry = addTags.Where(t => !inRegistry.Contains(t, StringComparer.Ordinal)).ToList();

        if (missingPackage.Count == 0 && missingRegistry.Count == 0)
        {
            checks.Add(new Check("its added tags read back", Verdict.Good,
                $"{addTags.Count} tag(s) in AllowedSlots and CustomizationPartTags: " +
                string.Join(", ", addTags)));
            return;
        }

        var why = new List<string>();
        if (missingPackage.Count > 0)
            why.Add("missing from the package's AllowedSlots: " + string.Join(", ", missingPackage));
        if (missingRegistry.Count > 0)
            why.Add("missing from the registry's CustomizationPartTags: " +
                    string.Join(", ", missingRegistry));
        checks.Add(new Check("its added tags read back", Verdict.Bad, string.Join("; ", why)));
    }

    private static void AgainstTheTemplate(List<Check> checks, PartDetails.Detail tpl,
                                           PartDetails.Detail made, ModPart? asked = null)
    {
        // A TAKEOVER THE AUTHOR TURNED OFF IS A DECISION, and this check exists to catch the
        // other kind. Both halves are asserted: the ones asked for must be GONE, and the ones
        // not asked about must still be there -- so turning one off cannot quietly take another
        // with it.
        var dropped = asked?.TakesOver.Where(kv => !kv.Value).Select(kv => kv.Key).ToList() ?? [];

        var stillThere = dropped.Where(s => made.OverrideSlots.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        if (dropped.Count > 0)
            checks.Add(stillThere.Count == 0
                ? new Check("leaves the slots you unticked alone", Verdict.Good,
                            string.Join(", ", dropped.Select(SlotLeaf)))
                : new Check("leaves the slots you unticked alone", Verdict.Bad,
                            "still takes over " + string.Join(", ", stillThere.Select(SlotLeaf))));

        var missingOverrides = tpl.OverrideSlots
            .Where(s => !made.OverrideSlots.Contains(s) &&
                        !dropped.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        checks.Add(missingOverrides.Count == 0
            ? new Check("takes over everything its template does", Verdict.Good,
                        $"{made.OverrideSlots.Count} slot(s)" +
                        (dropped.Count > 0 ? $", {dropped.Count} you turned off" : ""))
            : new Check("takes over everything its template does", Verdict.Bad,
                        "does not override " + string.Join(", ", missingOverrides.Select(Leaf))));

        // Hidden slots are a Note, not a failure, because the one case we have cannot be authored
        // yet: an EMPTY tag container serialises to a bare zero, which cannot be told from any
        // other zero in the package, so there is nowhere unambiguous to write. Surfaced rather
        // than silently accepted -- a locked visor colour is a real thing to lose.
        var shown = asked?.HidesFromMenu.Where(kv => !kv.Value).Select(kv => kv.Key).ToList() ?? [];

        var stillHidden = shown.Where(s => made.HiddenSlots.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        if (shown.Count > 0)
            checks.Add(stillHidden.Count == 0
                ? new Check("shows the swatches you unticked", Verdict.Good,
                            string.Join(", ", shown.Select(SlotLeaf)))
                : new Check("shows the swatches you unticked", Verdict.Bad,
                            "still hides " + string.Join(", ", stillHidden.Select(SlotLeaf))));

        // A CLAIM UNDONE IS ONLY UNDONE WHEN ITS LOCK GOES TOO. A slot removed from Hidden or
        // Override but left in LockedSlots is greyed out in the menu for no reason at all, which
        // reads in game as "the tick did nothing" -- the exact half-fix this pair of features was
        // written to avoid.
        var freed = dropped.Select(t => t.Contains('.') ? t[..t.LastIndexOf('.')] : t)
                           .Concat(shown).ToList();
        var stillLocked = freed
            .Where(t => made.LockedSlots?.Contains(t, StringComparer.OrdinalIgnoreCase) == true).ToList();
        if (freed.Count > 0)
            checks.Add(stillLocked.Count == 0
                ? new Check("unlocks what it stopped claiming", Verdict.Good,
                            $"{made.LockedSlots?.Count ?? 0} slot(s) still locked")
                : new Check("unlocks what it stopped claiming", Verdict.Bad,
                            "greyed out in the menu with nothing claiming it: " +
                            string.Join(", ", stillLocked.Select(SlotLeaf))));

        var missingHidden = tpl.HiddenSlots
            .Where(s => !made.HiddenSlots.Contains(s) &&
                        !shown.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missingHidden.Count > 0)
            checks.Add(new Check("hides what its template hides", Verdict.Note,
                                 "does not hide " + string.Join(", ", missingHidden.Select(SlotLeaf)) +
                                 " -- cannot be added to a container that starts out empty"));

        if (tpl.ReplacesBaseMesh != made.ReplacesBaseMesh)
            checks.Add(new Check("replaces the base mesh like its template", Verdict.Bad,
                                 $"ours {made.ReplacesBaseMesh}, template {tpl.ReplacesBaseMesh}"));
    }

    private static string Leaf(string tag) => tag[(tag.LastIndexOf('.') + 1)..];

    /// <summary>
    /// A SLOT tag in the words a person uses. The bare leaf is useless here: every slot tag ends
    /// in `.Mesh`, so a check reporting "Mesh" says nothing about which slot it means.
    /// </summary>
    private static string SlotLeaf(string tag)
    {
        var bits = tag.Split('.');
        var from = Array.FindIndex(bits, b => b.Equals("Outfit", StringComparison.OrdinalIgnoreCase));
        return from >= 0 && from + 1 < bits.Length ? string.Join('.', bits.Skip(from + 1)) : Leaf(tag);
    }
}

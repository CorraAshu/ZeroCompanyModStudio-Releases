using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// Turn a <see cref="ModSpec"/> into a package the game will load.
///
/// A specialization is authored the same way a helmet is -- by copying one the game ships and
/// changing only what the mod varies -- and that route is already proved in game: the "Vanguard"
/// build appeared as an extra tile on the SPECIALIZATION tab and the game's own recruit generator
/// rolled it onto new recruits. What was missing was not the mechanism but the DESCRIPTION of what
/// to change: that build was hand-written as a ModPart with an `abilities` map, and nobody could
/// work out how to write one without being told.
///
/// So this reads the template, compares it against what the editor produced, and applies the
/// difference. Everything is a DIFF rather than a write, for one reason: a spec's abilities, gear
/// kits and focus tree are soft object references, and the only edit available without a cook is
/// to re-point a name that is already there. What that buys is exact reporting -- a field this
/// cannot change is a field it says it did not change, rather than one that silently stayed the
/// template's.
///
/// What it CANNOT do, and says so:
///   * remove an ability, or add or remove a gear kit or a rung -- re-pointing a name is the only
///     edit made to those. A granted ability the template does not have IS added, through
///     <see cref="PropertyAdd.SetSoftRefEntry"/> (see <see cref="Grant"/>).
///   * change a rung's level or focus cost -- those are ints inside a nested struct, reachable
///     only by walking the unversioned header, which no editor here does yet
/// </summary>
public static class SpecAuthor
{
    /// <param name="Substitutions">
    /// Exactly the name changes this author made, handed back so the DIFF can check the result
    /// against what was actually asked for rather than against a second guess at it. A
    /// specialization is authored as a diff against its template, so this list IS the intent.
    /// </param>
    public sealed record Built(byte[] Bytes, List<string> Warnings, List<string> Applied,
                               IReadOnlyDictionary<string, string> Substitutions);

    /// <summary>
    /// Author the package. Returns null with a reason only when nothing could be produced at all;
    /// a change that could not be applied is a warning on a package that is still worth shipping.
    /// </summary>
    public static Built? Author(IFileProvider provider, string templatePackage, ModSpec sp,
                                string newPackageName, out string? why, Action<string>? log = null)
    {
        why = null;
        var warn = new List<string>();
        var did = new List<string>();

        // The template as the EDITOR saw it. Every field below is a comparison against this, so a
        // value the author never touched is one this can prove it never touched.
        var was = SpecDetails.Read(provider, templatePackage);
        if (was.Error is not null) { why = $"cannot read the template: {was.Error}"; return null; }

        byte[]? raw = null;
        var got = AssetLoader.Read(() =>
            provider.TrySaveAsset(ModBuild.ContainerPathFor(templatePackage), out raw));
        if (!got || raw is null) { why = $"cannot read the template package {templatePackage}"; return null; }

        // Parsed from THESE bytes rather than loaded from the mount -- see PropertyAdd.PristineExports.
        // A previous build of this mod can be installed while the next one is being made, and a
        // template read through the mount would then be read through whatever that build left.
        var exports = AssetLoader.Read(() => PropertyAdd.PristineExports(provider, raw, templatePackage));
        if (exports.Length == 0)
        { why = $"no mounted container holds {templatePackage}"; return null; }

        // ---------------------------------------------------------------- the name
        if (!string.IsNullOrWhiteSpace(sp.Display) &&
            !string.Equals(sp.Display, was.DisplayName, StringComparison.Ordinal))
        {
            var renamed = DisplayNameEdit.SetDisplayName(raw, exports, sp.Display, out var nameWhy);
            if (nameWhy is null) { raw = renamed; did.Add($"name \"{sp.Display}\""); }
            else if (PropertyAdd.Locate(provider, raw, templatePackage, "BitReactorUIDataFragment",
                                        "DisplayName", out var at, out var len, out var placeWhy))
            {
                // The template's name is EMPTY, so there is no string to anchor on. Walked to
                // instead, which is the same fallback an outfit part gets.
                var placed = DisplayNameEdit.SetTextAt(raw, at, len, sp.Display, out var wroteWhy);
                if (wroteWhy is null) { raw = placed; did.Add($"name \"{sp.Display}\" (into an empty one)"); }
                else warn.Add($"the name stayed \"{was.DisplayName}\": {nameWhy}; and {wroteWhy}");
            }
            else warn.Add($"the name stayed \"{was.DisplayName}\": {nameWhy}; {placeWhy}");
        }

        // ---------------------------------------------------------------- both descriptions
        //
        // Located by WALKING rather than by searching for the text they hold. The two share a
        // string-table key, so the key occurs twice in the package and an anchor search cannot say
        // which FText is which -- it refuses, correctly, and would leave both untouched for ever.
        if (!string.IsNullOrWhiteSpace(sp.Description))
        {
            foreach (var which in new[] { "ShortDescription", "LongDescription" })
            {
                // THE SUMMARY SEPARATELY, when the author gave one. The game writes these two
                // differently -- a sentence for the list, the paragraph with the abilities spelled
                // out underneath -- and putting the paragraph in both is a first draft rather than
                // a finished specialization. Blank falls back to Description, which is what every
                // project written before this said and must keep meaning.
                var text = which == "ShortDescription" && !string.IsNullOrWhiteSpace(sp.ShortDescription)
                    ? sp.ShortDescription
                    : sp.Description;

                var owner = exports.FirstOrDefault(e => e.Properties.Any(p => p.Name.Text == which));
                string? descWhy;
                if (owner is not null && PropertyAdd.Locate(provider, raw, templatePackage,
                        owner.ExportType ?? "", which, out var at, out var len, out _))
                    raw = DisplayNameEdit.SetTextAt(raw, at, len, text, out descWhy);
                else
                    raw = DisplayNameEdit.SetText(raw, exports, which, text, out descWhy);

                // Reported, never silent: a spec that kept its template's description is telling
                // the player it IS the template, and the build would otherwise look clean.
                if (descWhy is not null) warn.Add($"{which} stayed the template's: {descWhy}");
                else did.Add(which.Replace("Description", " description").Trim().ToLowerInvariant());
            }
        }

        // ---------------------------------------------------------------- the identity tag
        //
        // Changed only when the author changed it, and warned about either way, because both
        // answers have a real cost and neither is obviously right.
        //
        // KEEPING the template's is what shipped: Vanguard carries Assault's identity tag and the
        // game offers both, so two parts sharing one is demonstrably tolerated.
        //
        // CHANGING it writes a tag the game's own tag table does not define. An unregistered tag is
        // not an error the loader reports -- it resolves to nothing -- so this cannot be verified
        // from here and is left as the author's call, with the risk stated.
        if (!string.IsNullOrWhiteSpace(sp.Identity) &&
            !string.Equals(sp.Identity, was.Identity, StringComparison.Ordinal))
        {
            var have = PartDetails.TagsIn(exports, "GameplayTags");
            if (have.Count == 0)
                warn.Add("the identity tag was not changed: this template carries no gameplay-tag " +
                         "container to change");
            else if (was.Identity is null)
                warn.Add($"the identity tag was not changed: the template carries no " +
                         $"'.Specializations.' tag to replace, so there is nothing to swap " +
                         $"'{sp.Identity}' for");
            else
            {
                var want = have.Select(t => string.Equals(t, was.Identity, StringComparison.Ordinal)
                                            ? sp.Identity : t).ToList();
                var written = GameplayTagEdit.Set(raw, have, want, out var tagWhy);
                if (tagWhy is not null) warn.Add($"the identity tag stayed {was.Identity}: {tagWhy}");
                else
                {
                    raw = written;
                    did.Add($"identity {Leaf(was.Identity)} -> {Leaf(sp.Identity)}");
                    warn.Add($"its identity tag is '{sp.Identity}', which the game's own tag table " +
                             "does not define -- if the specialization appears but behaves oddly, " +
                             "put the template's tag back");
                }
            }
        }

        // ---------------------------------------------------------------- who may pick it
        //
        // A hero specialization carries its owner's name tag beside its slot tag, and a part is
        // offered only to a character that satisfies EVERY tag listed for it. So a duplicate of the
        // Padawan spec is, without this, a brand-new specialization that only Tel-Rea can choose --
        // which is not a subtle failure but it is a silent one, and it looks exactly like a mod
        // that did not install.
        //
        // Stripped by default because that is what duplicating means here; kept and replaced when
        // the author names a character. Both are reported: this is the tag that decides whether
        // anybody ever sees the thing.
        {
            var have = PartDetails.TagsIn(exports, "AllowedSlots");
            var want = have.Where(t => !t.StartsWith(PartsIndex.NamePrefix, StringComparison.Ordinal))
                           .ToList();
            var dropped = have.Count - want.Count;
            if (!string.IsNullOrWhiteSpace(sp.ForCharacter))
                want.Add(PartsIndex.NamePrefix + sp.ForCharacter.Trim());

            if (have.Count == 0)
                warn.Add("this template lists no allowed slots, so nothing here can say who may " +
                         "pick it -- if it does not appear in the menu, that is where to look");
            else if (!want.SequenceEqual(have, StringComparer.Ordinal))
            {
                var written = GameplayTagEdit.Set(raw, have, want, out var slotWhy);
                if (slotWhy is not null)
                    warn.Add($"who may pick it is unchanged: {slotWhy}" +
                             (dropped > 0 ? " -- it stays restricted to the character its template " +
                                            "belongs to" : ""));
                else
                {
                    raw = written;
                    if (dropped > 0 && string.IsNullOrWhiteSpace(sp.ForCharacter))
                        did.Add($"dropped {dropped} character tag(s), so anyone can pick it");
                    if (!string.IsNullOrWhiteSpace(sp.ForCharacter))
                        did.Add($"restricted to {sp.ForCharacter.Trim()}");
                }
            }
        }

        // ---------------------------------------------------------------- references
        //
        // Everything left is a soft object reference, and the one edit available without a cook is
        // to re-point a name that is already in the map. So the whole of the rest of this is a
        // dictionary of old name -> new name, applied in one pass at the end.
        var subs = new Dictionary<string, string>(StringComparer.Ordinal);

        // The template's own name map, read once. Every substitution below is checked against it
        // rather than written hopefully and pruned afterwards, because the three names a soft
        // object reference could use are not all present: a rung names the package path and the
        // Blueprint's generated `_C` class, and never the bare asset name. Speculating on all three
        // and reporting the miss said "GA_Firestorm_T1 is not in the template's name map, so that
        // change was not made" about a change that had just been made correctly.
        var names = new HashSet<string>(ZenPackage.Read(raw).Names, StringComparer.Ordinal);

        // Two different rungs re-pointed FROM the same ability would each want a different
        // destination for one name entry, and the second would silently win. Refused instead.
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        void Point(string from, string to, string what)
        {
            if (from.Length == 0)
            { warn.Add($"{what}: the template has nothing there to re-point, so it stays empty"); return; }
            if (to.Length == 0)
            { warn.Add($"{what}: cannot be emptied -- clearing an entry means resizing the array, " +
                       "which this build cannot do; it stays " + Leaf(from)); return; }

            var pkgFrom = ModBuild.PackageOf(from) ?? from;
            if (claimed.TryGetValue(pkgFrom, out var already))
            {
                // Not merely a wrong result: a package names each asset ONCE, so both changes would
                // land on the same name entry and the second would silently overwrite the first.
                if (!string.Equals(already, to, StringComparison.Ordinal))
                    warn.Add($"{what}: {Leaf(from)} is already being re-pointed to {Leaf(already)}, " +
                             $"so it cannot also become {Leaf(to)} -- the same ability cannot become " +
                             "two different ones in one specialization");
                return;
            }

            var pkgTo = ModBuild.PackageOf(to) ?? to;
            var leafFrom = pkgFrom[(pkgFrom.LastIndexOf('/') + 1)..];
            var leafTo = pkgTo[(pkgTo.LastIndexOf('/') + 1)..];

            // Each form of the name, only if it is really there. A soft reference is stored as the
            // package path AND the object name, so moving one without the other resolves to
            // nothing -- which is why all the present forms are taken, not just the first.
            var hit = false;
            foreach (var (f, t) in new[] { (pkgFrom, pkgTo), (leafFrom + "_C", leafTo + "_C"),
                                           (leafFrom, leafTo) })
                if (f != t && names.Contains(f)) { subs[f] = t; hit = true; }

            if (!hit)
            { warn.Add($"{what}: {Leaf(from)} is not in the template's name map, so it was not changed"); return; }

            claimed[pkgFrom] = to;
            did.Add($"{what}: {Leaf(from)} -> {Leaf(to)}");
        }

        // THE TEMPLATE'S OWN are re-pointed in place; any the author lists BEYOND them are ADDED.
        Lists("granted ability", was.Abilities,
              sp.Abilities.Count > was.Abilities.Count ? sp.Abilities.Take(was.Abilities.Count).ToList() : sp.Abilities,
              Point, warn);
        raw = Grant(provider, raw, templatePackage, exports, sp.Abilities.Skip(was.Abilities.Count).ToList(), did, warn);
        Lists("gear kit", was.GearKits, sp.GearKits, Point, warn);

        // The focus tree, line by line and rung by rung. Its SHAPE is the template's -- three lines
        // of six, six and five on everything the game ships -- and the editor edits in place, so a
        // mismatch here means the mod file was hand-edited into a shape the build cannot make.
        if (sp.Tree.Count != was.Tree.Count)
            warn.Add($"its focus tree has {sp.Tree.Count} line(s) and the template has " +
                     $"{was.Tree.Count} -- lines cannot be added or removed, so the extra ones are " +
                     "ignored");
        for (var i = 0; i < Math.Min(sp.Tree.Count, was.Tree.Count); i++)
        {
            var mine = sp.Tree[i];
            var theirs = was.Tree[i];
            if (mine.Count != theirs.Count)
                warn.Add($"focus line {i + 1} has {mine.Count} rung(s) and the template has " +
                         $"{theirs.Count} -- rungs cannot be added or removed, so the extra ones " +
                         "are ignored");

            for (var r = 0; r < Math.Min(mine.Count, theirs.Count); r++)
            {
                if (!string.Equals(mine[r].Ability, theirs[r].Ability, StringComparison.Ordinal))
                    Point(theirs[r].Ability, mine[r].Ability, $"focus {i + 1}.{r + 1}");

                // The numbers, located by the rung's OWN ability reference and checked against
                // all three of its current values -- see RungEdit. Done here, before the rename
                // pass, for the reason every splice is: the offsets describe these bytes.
                //
                // Against the TEMPLATE's ability, not the mod's. A rung whose ability was just
                // re-pointed still holds the template's name in the file at this moment; the
                // rename happens at the end.
                if (mine[r].MinLevel != theirs[r].MinLevel ||
                    mine[r].FocusLevel != theirs[r].FocusLevel ||
                    mine[r].FocusCost != theirs[r].FocusCost)
                {
                    var spot = RungEdit.Locate(raw, theirs[r].Ability, i + 1, r + 1,
                                               theirs[r].MinLevel, theirs[r].FocusLevel,
                                               theirs[r].FocusCost, out var rungWhy);
                    if (spot is null)
                        warn.Add($"focus {i + 1}.{r + 1}: its numbers stay the template's " +
                                 $"({theirs[r].MinLevel}/{theirs[r].FocusLevel}/{theirs[r].FocusCost}) " +
                                 $"-- {rungWhy}");
                    else
                    {
                        raw = RungEdit.Set(raw, spot,
                                           [mine[r].MinLevel, mine[r].FocusLevel, mine[r].FocusCost],
                                           out var rungNotes);
                        foreach (var note in rungNotes)
                            if (note.Contains("stays", StringComparison.Ordinal))
                                warn.Add($"focus {i + 1}.{r + 1}: {note}");
                            else did.Add($"focus {i + 1}.{r + 1} {note}");
                    }
                }
            }
        }

        // ---------------------------------------------------------------- the icon
        //
        // A specialization names its icon by TAG, not by path -- which is why it shows no thumbnail
        // in a parts browser that looks for a texture. Some templates use a direct path instead,
        // and a rename written for one shape does not apply to the other.
        if (!string.IsNullOrWhiteSpace(sp.IconTag) &&
            !string.Equals(sp.IconTag, was.IconTag, StringComparison.Ordinal))
        {
            if (was.IconTag is { Length: > 0 } oldTag) subs[oldTag] = sp.IconTag;
            else if (was.IconPath is not null)
                warn.Add($"the icon stayed the template's: {Leaf(templatePackage)} names its icon " +
                         "by TEXTURE PATH, not by tag, so an icon tag has nothing to replace");
            else
                warn.Add("the icon stayed the template's: it names no icon at all");
        }

        // ---------------------------------------------------------------- rename and write
        var pkg = ZenPackage.Read(raw);

        var oldLeaf = templatePackage[(templatePackage.LastIndexOf('/') + 1)..];
        // The package's own name appears twice -- the full path and the bare asset name -- and
        // BOTH have to move. Renaming only the leaf leaves the new part in the game's own
        // directory, where the container has no chunk for it.
        subs[templatePackage] = newPackageName;
        subs[oldLeaf] = sp.Id;

        // A substitution whose source is not in the name map does nothing at all, and doing
        // nothing quietly is the failure this whole class exists to avoid.
        foreach (var from in subs.Keys.ToList())
        {
            if (pkg.Names.Contains(from, StringComparer.Ordinal)) continue;
            subs.Remove(from);
            warn.Add($"'{from}' is not in the template's name map, so that change was not made");
        }

        pkg.RenameNames(subs);
        return new Built(pkg.Write(), warn, did, subs);
    }

    /// <summary>
    /// Compare two reference lists position by position, re-pointing what differs and reporting
    /// what cannot be reached. Position, not content: the editor edits in place, so entry three is
    /// entry three whatever it now says.
    /// </summary>
    /// <summary>
    /// Abilities granted BEYOND the template's, appended to its abilities fragment.
    ///
    /// The Sith Assassin is why. Assault grants one ability outright, Strike the Flanks, and the Sith
    /// Assassin grants three: Force Grip and Force Drain beside it. Re-pointing a name can only turn
    /// one of the template's grants into another, so the array has to GROW -- which is
    /// <see cref="PropertyAdd.SetSoftRefEntry"/>'s job, the writer that grants an ability to an
    /// existing spec and put Grip and Drain on Assault in the first place. It reads its own write
    /// back; one it refuses is a warning, and the rest of the specialization still ships.
    /// </summary>
    private static byte[] Grant(IFileProvider provider, byte[] raw, string templatePackage,
                                IReadOnlyList<CUE4Parse.UE4.Assets.Exports.UObject> exports,
                                List<string> extra, List<string> did, List<string> warn)
    {
        if (extra.Count == 0) return raw;

        var fragment = exports.FirstOrDefault(e => e is not null && e.ExportType == "CustomizationFragmentAbilities");
        if (fragment is null)
        {
            warn.Add($"{extra.Count} granted ability(s) were not added: the template has no abilities " +
                     "fragment to add them to");
            return raw;
        }

        foreach (var add in extra)
        {
            var (refPackage, refAsset) = PropertyEdit.SplitRef(add);
            if (refPackage is null)
            { warn.Add($"granted ability '{add}' is not an asset path like /Game/Folder/GA_Thing.GA_Thing_C"); continue; }

            var grown = PropertyAdd.SetSoftRefEntry(provider, raw, templatePackage, fragment.Name, "Abilities",
                                                    refPackage, refAsset!, true, out var addWhy);
            if (addWhy is not null) { warn.Add($"granted ability {Leaf(add)} was not added: {addWhy}"); continue; }
            raw = grown;
            did.Add($"grants {Leaf(add)} as well");
        }
        return raw;
    }

    private static void Lists(string what, IReadOnlyList<string> was, IReadOnlyList<string> now,
                              Action<string, string, string> point, List<string> warn)
    {
        if (now.Count != was.Count)
            warn.Add($"this mod lists {now.Count} {what}(s) and its template has {was.Count} -- " +
                     $"one cannot be added or removed, so only the first {Math.Min(now.Count, was.Count)} " +
                     "are compared");

        for (var i = 0; i < Math.Min(now.Count, was.Count); i++)
            if (!string.Equals(now[i], was[i], StringComparison.Ordinal))
                point(was[i], now[i], $"{what} {i + 1}");
    }

    /// <summary>The readable end of a tag or an asset path, for a line that has to fit.</summary>
    private static string Leaf(string s)
    {
        var cut = s.LastIndexOfAny(['/', '.']);
        return cut < 0 || cut == s.Length - 1 ? s : s[(cut + 1)..];
    }
}

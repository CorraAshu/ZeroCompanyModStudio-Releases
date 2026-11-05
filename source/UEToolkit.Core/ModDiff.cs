using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;

using CUE4Parse.UE4.Objects.GameplayTags;

namespace UEToolkit.Core;

/// <summary>
/// How a built part differs from the game part it was copied from.
///
/// A mod part is authored by duplicating a stock part and changing three things: the name, the
/// mesh, the icon. So the useful question about any build is "what ELSE changed", and until now
/// nothing could answer it. Finding out meant dumping both packages by hand and reading a diff --
/// which is how an hour went on a helmet that floated, after a template swap silently brought six
/// other differences along with the one that was wanted.
///
/// The split is the point. A difference that matches a substitution the mod ASKED for is expected
/// and is reported as such; everything else is a change nobody requested, and those are the ones
/// that move a helmet off a head. "Duplicate this part, change only the mesh and the textures"
/// is a claim this makes checkable before the game runs, rather than a hope.
///
/// Not a verifier. <see cref="ModVerify"/> asks whether the build is correct against rules we
/// wrote down; this asks only how it differs from its own template, including in ways nobody has
/// thought to write a rule about yet. That is the half that catches the unknown.
/// </summary>
public static class ModDiff
{
    public enum Kind { Added, Removed, Changed }

    /// <summary>
    /// <paramref name="Asked"/> is true when the difference is explained by a substitution the mod
    /// requested -- the new name, the new mesh, the new icon. False is the interesting case.
    /// </summary>
    public sealed record Change(Kind Kind, string Where, string? Was, string? Now, bool Asked);

    public sealed record Result(List<Change> Changes, int TemplateLines, int MadeLines)
    {
        public IEnumerable<Change> Unasked => Changes.Where(c => !c.Asked);
        public IEnumerable<Change> Expected => Changes.Where(c => c.Asked);
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>
    /// A package as one line per leaf value, keyed by a path that does NOT include the asset's own
    /// name.
    ///
    /// Keying on the export's name would make every line differ, since renaming the asset is the
    /// one thing a copy always does. Exports are keyed by TYPE plus their index among exports of
    /// that type, which is stable between an original and a copy of it and needs no name at all.
    /// </summary>
    public static List<(string Path, string Value)> Render(IEnumerable<UObject> exports)
    {
        var lines = new List<(string, string)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var e in exports)
        {
            var type = e.ExportType ?? "?";
            seen.TryGetValue(type, out var n);
            seen[type] = n + 1;
            var root = $"{type}[{n}]";
            foreach (var p in e.Properties) Walk($"{root}/{p.Name.Text}", p.Tag?.GenericValue, lines, 0);
        }
        lines.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return lines;
    }

    private static void Walk(string path, object? value, List<(string, string)> into, int depth)
    {
        // The same depth cap the dump uses. These nest through material overrides, and a runaway
        // structure would otherwise turn one part into thousands of lines of noise.
        if (depth < 8)
            switch (value)
            {
                case FStructFallback sf:
                    foreach (var q in sf.Properties)
                        Walk($"{path}/{q.Name.Text}", q.Tag?.GenericValue, into, depth + 1);
                    return;
                case FScriptStruct fss:
                    Walk(path, fss.StructType, into, depth);
                    return;
                case FInstancedStruct fis:
                    Walk(path, fis.ScriptStruct?.StructType, into, depth);
                    return;
                // A gameplay tag CONTAINER is not an array and has no useful ToString, so without
                // this it rendered as its own type name -- identical no matter which tags were in
                // it. This diff reported "nothing changed" on an edit that had demonstrably
                // rewritten a tag, which in a game where almost every gate is a tag container is
                // the worst possible blind spot: it makes the verification look clean precisely
                // where it is not looking.
                //
                // Tags are sorted for the same reason array elements are -- a reorder does not
                // change what a part does, and reporting one buries the differences that do.
                case FGameplayTagContainer tagContainer:
                {
                    var tags = tagContainer.GameplayTags
                        .Select(t => t.TagName.Text)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToList();
                    for (var k = 0; k < tags.Count; k++) into.Add(($"{path}[{k}]", tags[k]));
                    // An EMPTY container still has to say so, or emptying one reads as the whole
                    // property vanishing rather than as a change to it.
                    if (tags.Count == 0) into.Add((path, "(no tags)"));
                    return;
                }
                case UScriptArray arr:
                {
                    // Arrays of tags come back in different ORDERS between two packages that mean
                    // the same thing -- the same three slots, written in a different sequence.
                    // Sorting the elements by their rendered content makes a reordering invisible,
                    // which is right: a reorder is not a change to what the part does, and
                    // reporting it as one buries the differences that are.
                    //
                    // Each element is rendered under its OWN placeholder root and re-rooted after,
                    // rather than by slicing the prefix back off a combined path. The slicing
                    // version assumed every element's path began with exactly `path` + two chars,
                    // which is false the moment an element is itself a struct -- it produced
                    // mangled keys, and those keys then read as whole properties appearing and
                    // disappearing. A diff that invents differences is worse than no diff.
                    var elements = new List<List<(string Path, string Value)>>();
                    for (var k = 0; k < arr.Properties.Count; k++)
                    {
                        var one = new List<(string, string)>();
                        Walk("#", arr.Properties[k]?.GenericValue, one, depth + 1);
                        one.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
                        elements.Add(one);
                    }
                    static string Key(List<(string Path, string Value)> lines) =>
                        string.Join("", lines.Select(l => l.Path + "=" + l.Value));
                    elements.Sort((a, b) => string.CompareOrdinal(Key(a), Key(b)));

                    for (var k = 0; k < elements.Count; k++)
                        foreach (var (rp, rv) in elements[k])
                            into.Add(($"{path}[{k}]{rp[1..]}", rv));
                    return;
                }
            }
        into.Add((path, value?.ToString()?.Replace('\n', ' ') ?? "(null)"));
    }

    // ------------------------------------------------------------------ comparing

    /// <summary>
    /// Compare a copy against its template.
    ///
    /// <paramref name="substitutions"/> is what the mod asked to change, as from -> to. A
    /// difference is marked Asked when applying them to the template's value produces the copy's,
    /// so a renamed mesh reads as expected and a vanished fragment does not.
    /// </summary>
    public static Result Between(List<(string Path, string Value)> template,
                                 List<(string Path, string Value)> made,
                                 IReadOnlyList<(string From, string To)> substitutions)
    {
        var t = Index(template);
        var m = Index(made);
        var changes = new List<Change>();

        foreach (var (path, was) in t)
        {
            if (!m.TryGetValue(path, out var now))
            { changes.Add(new Change(Kind.Removed, path, was, null, false)); continue; }
            if (string.Equals(was, now, StringComparison.Ordinal)) continue;
            changes.Add(new Change(Kind.Changed, path, was, now, Apply(was, substitutions) == now));
        }
        foreach (var (path, now) in m)
            if (!t.ContainsKey(path))
                changes.Add(new Change(Kind.Added, path, null, now, false));

        changes.Sort((a, b) => string.CompareOrdinal(a.Where, b.Where));
        return new Result(changes, template.Count, made.Count);
    }

    private static Dictionary<string, string> Index(List<(string Path, string Value)> lines)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, value) in lines)
        {
            // Two leaves under one path is possible in principle; keeping the first is enough for
            // a comparison and beats throwing on a duplicate key inside a diagnostic.
            if (!d.ContainsKey(path)) d[path] = value;
        }
        return d;
    }

    /// <summary>
    /// Every substitution applied to a value, longest FROM first.
    ///
    /// Longest first because these overlap: an asset name is a substring of its own object path,
    /// and replacing the short one first leaves the long one half-rewritten and matching nothing.
    /// </summary>
    private static string Apply(string value, IReadOnlyList<(string From, string To)> subs)
    {
        foreach (var (from, to) in subs.Where(s => s.From.Length > 0)
                                       .OrderByDescending(s => s.From.Length))
            value = value.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        return value;
    }

    // ------------------------------------------------------------------ the whole question

    /// <summary>
    /// Diff one built part against the template it came from, reading both from the provider and
    /// the built container.
    /// </summary>
    public static Result? Of(IFileProvider provider, UObject[] madeExports, string templatePath,
                             IReadOnlyList<(string From, string To)> substitutions, out string? why)
    {
        why = null;
        try
        {
            var pkg = provider.LoadPackage(AssetLoader.PackagePath(templatePath));
            var template = Render(pkg.GetExports());
            return Between(template, Render(madeExports), substitutions);
        }
        catch (Exception e) { why = $"{templatePath}: {e.Message}"; return null; }
    }

    /// <summary>
    /// What a part asked to change, as substitutions. Built from the mod's own request rather than
    /// from what the builder did, so a change the builder made and nobody asked for still shows up
    /// as unasked.
    /// </summary>
    /// <summary>
    /// The changes a COPY asked for, as substitutions to apply to its template before diffing.
    ///
    /// A copy says what it wants outright -- its rename map IS the list of intended changes -- so
    /// this is shorter than the part version and considerably more trustworthy. It exists because
    /// the diff could not see copies at all, and a duplicated weapon is nothing BUT copies: the
    /// one authoring path with six faults found by hand had no instrument pointed at it.
    /// </summary>
    public static List<(string From, string To)> SubstitutionsForCopy(
        ModCopy copy, string templatePath, string newPackageName, string? displayWas,
        IReadOnlyDictionary<string, string>? builtPackages = null)
    {
        static string Leaf(string p) => p.Contains('.') ? p[(p.LastIndexOf('.') + 1)..]
                                                        : p[(p.LastIndexOf('/') + 1)..];
        // BOTH the full path and the bare leaf are needed -- a sub-object reference is
        // "package.asset:Sub", so the asset half moves too -- but a copy KEEPS ITS TEMPLATE'S
        // CLASS NAME: BP_Longarm_Valken-38X_C lives on inside a package called BP_Longarm_AuditA.
        // Substituting the leaf blindly rewrites the class as well, and then every sub-object
        // reference reads as an unrequested change; dropping the leaf makes every one of them read
        // that way instead. Seven invented differences on one weapon actor, either way.
        //
        // So the class name is parked behind a placeholder, the two real substitutions run, and it
        // is put back. Substitutions are applied IN ORDER, which is what makes this work.
        var leaf = Leaf(templatePath);

        // EVERY asset name whose CLASS name must survive being substituted: this copy's own, and
        // the target of every hard-import redirect. A class keeps its template's name -- 
        // BP_Longarm_Valken-38X_C lives on inside a package called BP_Longarm_AuditA -- so a bare
        // leaf substitution rewrites the class as well and the reference then reads as a change
        // nobody asked for. Guarded first, restored last, and substitutions apply IN ORDER.
        // The sentinel must NOT contain the name it is protecting: the leaf substitution below
        // would rewrite it inside the sentinel and the restore would then match nothing. An index
        // has no such problem.
        var guarded = new List<string> { leaf };
        guarded.AddRange(copy.RepointImports.Keys.Select(Leaf));
        guarded = guarded.Distinct(StringComparer.Ordinal).ToList();
        var subs = new List<(string, string)>();
        for (var g = 0; g < guarded.Count; g++)
            subs.Add((guarded[g] + "_C", "" + g + ""));

        subs.Add((templatePath, newPackageName));
        subs.Add((leaf, copy.Id));
        foreach (var (from, to) in copy.RenameNames) subs.Add((from, to));
        foreach (var (from, to) in copy.RepointImports)
        {
            // A redirect names its target by ASSET NAME and the build decides which folder it lands
            // in, so the diff cannot reconstruct the value unless it is told where it went.
            var toLeaf = to.Contains('/') ? Leaf(to) : to;
            var landed = builtPackages is not null && builtPackages.TryGetValue(toLeaf, out var full)
                ? full : to;
            subs.Add((from, landed));
            subs.Add((Leaf(from), toLeaf));
        }

        for (var g = 0; g < guarded.Count; g++)
            subs.Add(("" + g + "", guarded[g] + "_C"));

        if (!string.IsNullOrEmpty(copy.Display) && !string.IsNullOrEmpty(displayWas))
            subs.Add((displayWas!, copy.Display));
        return subs;
    }

    public static List<(string From, string To)> SubstitutionsFor(
        PartDetails.Detail template, string templatePath, ModPart part, string newPackageName,
        string? meshOverride, string? iconObjectPath)
    {
        static string Leaf(string p) => p.Contains('.') ? p[(p.LastIndexOf('.') + 1)..]
                                                        : p[(p.LastIndexOf('/') + 1)..];
        var subs = new List<(string, string)>
        {
            (templatePath, newPackageName),
            (Leaf(templatePath), part.Id),
        };

        // Per-head options the author left alone are not redirected by the build (see
        // ModBuild.KeepsHeadCuts), so the diff must not expect them to be.
        var mesh = meshOverride ?? part.Mesh;
        if (!string.IsNullOrEmpty(mesh) && !ModBuild.KeepsHeadCuts(template.Meshes, mesh, template.MeshPath))
            foreach (var option in template.Meshes)
                if (!string.IsNullOrEmpty(option.MeshPath))
                { subs.Add((option.MeshPath, mesh)); subs.Add((Leaf(option.MeshPath), Leaf(mesh))); }

        if (!string.IsNullOrEmpty(iconObjectPath) && !string.IsNullOrEmpty(template.IconPath))
        { subs.Add((template.IconPath, iconObjectPath)); subs.Add((Leaf(template.IconPath), Leaf(iconObjectPath))); }

        if (!string.IsNullOrEmpty(part.Display) && !string.IsNullOrEmpty(template.DisplayName))
            subs.Add((template.DisplayName, part.Display));

        return subs;
    }
}

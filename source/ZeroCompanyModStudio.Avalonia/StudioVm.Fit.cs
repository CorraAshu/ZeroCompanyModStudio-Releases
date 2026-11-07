using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// HOW AN IMPORTED MESH FITS THE ONE IT REPLACES.
///
/// THE THREE FIELDS AND THE MEASUREMENT ARE ONE FEATURE, which is why they are one card. The
/// fields — the axis mapping, the centimetres per unit, and the nudge — were on the read sheet
/// and nothing wrote them. On their own they would be three numbers with no way to know what to
/// put in them; <see cref="MeshFit.Measure"/> is what makes them answerable.
///
/// Without it the loop is: export, build, install, launch, squint, guess again — minutes per
/// attempt for one bit of information. Every number here is knowable before the game runs.
///
/// THE BUILD'S OWN REFUSAL BAND IS DRAWN HERE, and drawn at exactly the same numbers. ModBuild
/// throws out an import more than 5× or under 0.2× the height of what it replaces, ships the
/// GAME's mesh instead, and prints one line in the middle of a long build. An author who misses
/// it gets a mod that builds clean with none of their model in it — which has happened here, on
/// a cell mesh that came out 102× too big. An editor drawing a different line from the one the
/// build enforces would be worse than no line at all.
///
/// SHARED BY EVERY KIND THAT WEARS A MESH, through <see cref="IMeshAuthoring"/>: a part copy, a
/// region of one, a standalone mesh copy and an override all carry the same five fields.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// A FRESHLY IMPORTED FILE'S AXES, where the entry names none and the file declares a system whose mapping has been
    /// measured (<see cref="MeshImport.DeclaredBasis"/>) -- today the Z-up one the Studio's own FBX export writes, which
    /// the default read as lying face down.
    ///
    /// WRITTEN INTO THE PROJECT, so the build, the View and the fit card read one answer and the author can see and
    /// change it on the fit card. Only when the basis is blank: one the author typed is theirs. A file that declares
    /// anything else keeps the default exactly as before, and so does every project already built.
    /// </summary>
    private void TakeDeclaredBasis(IMeshAuthoring onto, string file)
    {
        if (onto.MeshBasis.Length > 0 || file.Length == 0) return;
        var path = PathOfMyImage(file) ?? file;
        if (!File.Exists(path) || MeshImport.DeclaredBasis(path) is not { } basis) return;

        onto.MeshBasis = basis;
        Say($"{System.IO.Path.GetFileName(path)}: the file says it is Z-up, so its axes are set to {basis} — " +
            "change them on the fit card if it stands wrong");
    }

    /// <summary>
    /// What this tab's fit fields live on, or null when it has none.
    ///
    /// ASKED OF THE TAB, exactly as <see cref="PaintTarget"/> is, and for the same reason: the
    /// card is shared and the object behind it is not.
    /// </summary>
    private IMeshAuthoring? FitTarget(SheetTab tab)
    {
        switch (tab)
        {
            case ReplacementTab own:
                return own.Mesh;

            // A GAME MESH THIS MOD REWRITES: the override's own five fields, through Core's adapter.
            case OverrideTab rewrite when _mod is not null:
                return OverrideOf(rewrite) is { } ovOwn ? new OverrideMesh(ovOwn) : null;

            // A GARMENT OF PIECES FITS PER PIECE. The import lands on a ModRegion, so the part's
            // own five fields describe a file it does not carry — the card was simply absent, and
            // the only way to place a piece's geometry was to edit the project file by hand.
            case PartTab { Copy: { } copy }:
                if (copy.Regions.FirstOrDefault(
                        r => r.Region.Equals(tab.FitPiece, StringComparison.OrdinalIgnoreCase) &&
                             r.MeshFile.Length > 0) is { } piece)
                    return piece;
                return copy.Regions.Any(r => r.MeshFile.Length > 0) ? null : copy;

            // AND AN OVERRIDE, through the adapter Core keeps. Importing over a game mesh
            // wrote the file and left its scale, axes and offset unreachable: the same five
            // fields, on the one kind with no way to reach them.
            //
            // KEYED BY THE MESH, not by the part. The import targets the MESH package, and a part
            // with two body types has one override per mesh — so "which one am I placing" is the
            // same question a garment's pieces ask, and it is answered the same way.
            case PartTab pt when _mod is not null:
                var ov = _mod.Overrides.FirstOrDefault(
                    o => o.MeshFile.Length > 0 &&
                         Leaf(o.Target).Equals(tab.FitPiece, StringComparison.OrdinalIgnoreCase));
                return ov is null ? null : new OverrideMesh(ov);

            default:
                return null;
        }
    }

    /// <summary>
    /// Which pieces of this garment carry an import, and which one the card is placing.
    ///
    /// The chosen one is KEPT if it still carries a file, so refilling after an edit does not
    /// move the card off the piece somebody is working on.
    /// </summary>
    private void FillFitPieces(SheetTab tab)
    {
        // WHAT CARRIES AN IMPORT, in the two shapes a part comes in. A copy authors its own
        // pieces; a game part is overridden one MESH at a time. Both give the card a list of
        // things it could be placing, and both are read off what is actually there.
        var pieces = tab switch
        {
            PartTab { Copy: { } copy } =>
                copy.Regions.Where(r => r.Region.Length > 0 && r.MeshFile.Length > 0)
                            .Select(r => r.Region).ToList(),

            // FROM THE TEMPLATE'S OWN MESH LIST, not from MeshRows. The rows are filled by a
            // different pass and the order of the two is not guaranteed — keyed off the rows,
            // this came back empty on open and the card was absent about an override that
            // plainly carried an import.
            PartTab pt2 when _mod is not null =>
                _mod.Overrides.Where(o => o.MeshFile.Length > 0)
                    .Select(o => Leaf(o.Target))
                    .Where(n => pt2.Entry.Detail.Meshes.Any(
                        m => Leaf(PackageOf(m.MeshPath ?? "")).Equals(n, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),

            _ => [],
        };

        tab.FitPieceChanged = null;          // filling the list is not a choice

        // REBUILT ONLY WHEN IT REALLY CHANGED, and the choice put back afterwards.
        //
        // The list is a ComboBox's ItemsSource with SelectedItem bound TwoWay, so Clear() sets
        // the selection to null and the binding writes that back — and the repair below then put
        // the card on the FIRST piece. Choosing a piece refills the card, so choosing TORS moved
        // the card to TORS, cleared the list, lost the selection, and landed back on ARMS.
        // Measured: "--fitpiece TORS: now on 'ARMS' of [ARMS, TORS]".
        var same = tab.FitPieces.Count == pieces.Count &&
                   tab.FitPieces.Zip(pieces).All(
                       x => x.First.Equals(x.Second, StringComparison.OrdinalIgnoreCase));
        if (!same)
        {
            var keep = tab.FitPiece;
            tab.FitPieces.Clear();
            foreach (var r in pieces) tab.FitPieces.Add(r);
            if (pieces.Contains(keep, StringComparer.OrdinalIgnoreCase)) tab.FitPiece = keep;
        }

        if (!pieces.Contains(tab.FitPiece, StringComparer.OrdinalIgnoreCase))
            tab.FitPiece = pieces.FirstOrDefault() ?? "";

        tab.Raise2(nameof(SheetTab.HasFitPieces));
        tab.Raise2(nameof(SheetTab.FitPieceSays));
        tab.FitPieceChanged = _ => FillFit(tab);
    }

    /// <summary>
    /// Put the fit on the panel, and measure it.
    ///
    /// The card is ABSENT until there is an import: the three fields describe how to place a
    /// file, and with no file they are three boxes that do nothing.
    /// </summary>
    private void FillFit(SheetTab tab)
    {
        // THE PIECES THAT CARRY AN IMPORT, first — FitTarget reads FitPiece, so an empty or stale
        // one aims the card at a piece that is not there and the fields come back blank about a
        // file that is plainly imported.
        FillFitPieces(tab);

        if (FitTarget(tab) is not { } onto || onto.MeshFile.Length == 0)
        {
            tab.SecFit.Present = false;
            tab.CanFit = false;
            return;
        }

        tab.SecFit.Present = true;

        tab.EditBasis = onto.MeshBasis;
        tab.EditScale = onto.MeshScale > 0
            ? onto.MeshScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "";
        tab.EditMoveX = Num(onto.MeshOffsetX);
        tab.EditMoveY = Num(onto.MeshOffsetY);
        tab.EditMoveZ = Num(onto.MeshOffsetZ);

        tab.HasOwnFit = onto.MeshBasis.Length > 0 || onto.MeshScale > 0 ||
                        onto.MeshOffsetX != 0 || onto.MeshOffsetY != 0 || onto.MeshOffsetZ != 0;

        _ = MeasureFitAsync(tab);

        static string Num(float f) =>
            f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Read the FBX, place it the way the builder would, and compare.
    ///
    /// OFF THE UI THREAD: it parses the whole file and loads the template's geometry. The panel
    /// says it is measuring rather than going blank, because a card that empties for two seconds
    /// reads as one that failed.
    /// </summary>
    private async Task MeasureFitAsync(SheetTab tab)
    {
        if (_provider is null || FitTarget(tab) is not { } onto) { tab.CanFit = false; return; }

        var source = onto.MeshSource;
        if (source.Length == 0)
        {
            tab.CanFit = false;
            tab.FitCounts = "this names no mesh of the game's to be measured against";
            return;
        }

        tab.CanFit = false;
        tab.FitCounts = "measuring…";
        tab.FitTemplate = "";
        tab.FitYours = "";
        tab.FitRefuse = "";
        tab.FitWants = "";
        tab.SetFitNotes([]);

        var provider = _provider;
        var file = PathOfMyImage(onto.MeshFile) ?? onto.MeshFile;
        var package = source.Contains('.') ? source[..source.IndexOf('.')] : source;
        var basis = onto.MeshBasis;
        var scale = onto.MeshScale;
        var offset = (onto.MeshOffsetX, onto.MeshOffsetY, onto.MeshOffsetZ);

        var (report, why) = await Task.Run(() =>
        {
            try
            {
                var r = MeshFit.Measure(provider, package, file, basis, scale, offset, out var w);
                return (r, w);
            }
            catch (Exception e) { return (null, e.Message); }
        });

        if (report is null)
        {
            tab.FitCounts = why ?? "could not measure the mesh";
            tab.CanFit = false;
            return;
        }

        _fit[tab] = report;
        tab.CanFit = true;

        tab.FitCounts = $"{report.Vertices:N0} verts, {report.Triangles:N0} tris   " +
                        $"scale {report.Scale:0.###}   axes {report.Basis}";

        tab.FitTemplate = $"{report.Template.Describe}    " +
                          $"Z {report.Template.Z0:0.#}..{report.Template.Z1:0.#}";

        var (rd, rw, rh) = report.Ratio;
        tab.FitYours = $"{report.Imported.Describe}    " +
                       $"Z {report.Imported.Z0:0.#}..{report.Imported.Z1:0.#}" +
                       $"    ({rd:0.00}×, {rw:0.00}×, {rh:0.00}×)";

        // A QUARTER IS A LOOK-AT-THIS, not a refusal: a barrel replacing a barrel need not be the
        // same length, and a set of pieces deliberately is not.
        tab.FitOff = Math.Abs(rd - 1) > 0.25f || Math.Abs(rw - 1) > 0.25f ||
                     Math.Abs(rh - 1) > 0.25f;

        // THE SAME BAND THE BUILD ENFORCES. See the class remarks.
        if (rh > 5f || (rh > 0 && rh < 0.2f))
        {
            tab.FitRefuse =
                $"The build will REFUSE this: {rh:0.#}× the height of the mesh it replaces. Your " +
                "model is shipped as the game's own, so the piece is simply not yours.";
            var wants = rh > 0 ? report.Scale / rh : 0f;
            tab.FitWants = wants > 0
                ? wants.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        tab.SetFitNotes(report.Notes);
        Say($"fit: {tab.FitCounts}   yours {tab.FitYours}");
    }

    /// <summary>The last measurement per tab, for the buttons that place the mesh by it.</summary>
    private readonly Dictionary<SheetTab, MeshFit.Report> _fit = new();

    /// <summary>
    /// A PLACEMENT OF 0.0000002 CM IS A ZERO, and it prints as "-0".
    ///
    /// The measurement works in floats over thousands of vertices, so an offset that should be
    /// nothing comes out as float noise. Written as-is it fills the project with numbers that
    /// mean nothing and shows the author a minus sign in front of a zero, which reads as a bug
    /// in the thing that computed it.
    /// </summary>
    private static float Tidy(float v) =>
        Math.Abs(v) < 0.0005f ? 0f : (float)Math.Round(v, 3);

    /// <summary>
    /// Write one of the three, and measure again.
    ///
    /// WRITTEN ON LEAVING THE BOX rather than per keystroke: typing "2.54" one character at a
    /// time would be four saves and three re-measurements, each parsing the whole FBX.
    /// </summary>
    public void SetFit(SheetTab tab, string which, string text)
    {
        if (_mod is null) { Say("open a mod first"); return; }
        if (FitTarget(tab) is not { } onto) return;

        switch (which)
        {
            case "basis":
                // XZY IS THE DEFAULT AND IS STORED AS NOTHING, so a project does not carry a
                // field that says what would have happened anyway.
                var basis = text.Trim().ToUpperInvariant();
                onto.MeshBasis = basis is "XZY" or "" ? "" : basis;
                break;

            case "scale":
                if (text.Trim().Length == 0) onto.MeshScale = 0;
                else if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture,
                                         out var s) || s < 0)
                { Say($"cm per unit: '{text}' is not a number"); FillFit(tab); return; }
                else onto.MeshScale =
                    Math.Abs(s - ModBuild.DefaultMeshScale) < 0.001f ? 0 : s;
                break;

            case "x": case "y": case "z":
                if (!float.TryParse(text.Trim().Length == 0 ? "0" : text,
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                { Say($"move: '{text}' is not a number"); FillFit(tab); return; }
                if (which == "x") onto.MeshOffsetX = v;
                else if (which == "y") onto.MeshOffsetY = v;
                else onto.MeshOffsetZ = v;
                break;

            default: return;
        }

        _mod.Save();
        _artVersion++;
        Say($"{Leaf(onto.MeshSource)}: axes {(onto.MeshBasis.Length > 0 ? onto.MeshBasis : "XZY")}, " +
            $"scale {(onto.MeshScale > 0 ? onto.MeshScale.ToString("0.###") : "default")}, " +
            $"move {onto.MeshOffsetX:0.#}, {onto.MeshOffsetY:0.#}, {onto.MeshOffsetZ:0.#}");

        FillFit(tab);
        AfterEditsChanged();
    }

    /// <summary>
    /// Place it by the measurement: centred on the template, or resting on it.
    ///
    /// TWO BUTTONS BECAUSE A MESH OF A DIFFERENT SHAPE CANNOT DO BOTH, and which is right depends
    /// on the piece: a helmet rests on a head, a pauldron hangs off a shoulder.
    /// </summary>
    public void PlaceFit(SheetTab tab, string how)
    {
        if (_mod is null || FitTarget(tab) is not { } onto) return;

        switch (how)
        {
            case "centre" or "rest" when !_fit.TryGetValue(tab, out _):
                Say("the mesh has not been measured yet");
                return;

            case "centre":
            {
                var to = _fit[tab].ToCentre;
                (onto.MeshOffsetX, onto.MeshOffsetY, onto.MeshOffsetZ) = (Tidy(to.X), Tidy(to.Y), Tidy(to.Z));
                Say($"{Leaf(onto.MeshSource)}: centred on the template — " +
                    $"{onto.MeshOffsetX:0.###}, {onto.MeshOffsetY:0.###}, {onto.MeshOffsetZ:0.###}");
                break;
            }

            case "rest":
            {
                var to = _fit[tab].ToRest;
                (onto.MeshOffsetX, onto.MeshOffsetY, onto.MeshOffsetZ) = (Tidy(to.X), Tidy(to.Y), Tidy(to.Z));
                Say($"{Leaf(onto.MeshSource)}: resting on the template — " +
                    $"{onto.MeshOffsetX:0.###}, {onto.MeshOffsetY:0.###}, {onto.MeshOffsetZ:0.###}");
                break;
            }

            case "clear":
                onto.MeshOffsetX = onto.MeshOffsetY = onto.MeshOffsetZ = 0;
                Say($"{Leaf(onto.MeshSource)}: no move");
                break;

            // THE SCALE THE BUILD WOULD ACCEPT, and the other answer beside it. Which is right
            // depends on where the file came from, and the card says so — a set of pieces given
            // one number each no longer lines up with itself.
            case "fitscale" when tab.FitWants.Length > 0:
                onto.MeshScale = float.Parse(tab.FitWants,
                    System.Globalization.CultureInfo.InvariantCulture);
                Say($"{Leaf(onto.MeshSource)}: scale {onto.MeshScale:0.###} — the same height as " +
                    "the mesh it replaces");
                break;

            case "pinone":
                onto.MeshScale = 1f;
                Say($"{Leaf(onto.MeshSource)}: scale pinned to 1 — the tool's own multiplier is off");
                break;

            default: return;
        }

        _mod.Save();
        _artVersion++;
        FillFit(tab);
        AfterEditsChanged();
    }
}

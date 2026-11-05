using System.Text;

namespace UEToolkit.Core;

/// <summary>
/// THE 3DS MAX COMPANION OF A LEVEL FBX: a small MaxScript, written beside the .fbx, that switches on the
/// cut-outs of the level's cut-out materials (see CUT-OUT in <see cref="LevelFbxExport"/>).
///
/// WHY. 3ds Max's FBX importer reads the opacity texture the file connects (<see cref="LevelFbxExport.OpacityProperty"/>)
/// as a Physical Material's Transparency map, by its colour intensity, and a Physical Material's Transparency
/// weight is 0, so the leaves arrive solid. What does work in Max is a Cutout map reading the base colour image's
/// ALPHA, which an import cannot make but a script can. The script is run once by the person, after File &gt; Import.
///
/// WHAT IT DOES, per material it lists (matched by the name Max shows, which is the FBX material name): gives a
/// Physical Material a Cutout map -- or a Standard material an Opacity map -- that is a bitmap of the same image as the
/// material's base colour, reading its alpha; and clears the Transparency map the importer made from that same image.
/// Nothing outside the list is touched, running it twice changes nothing more, and no path is written into it: each
/// bitmap is made from the file name the material's own base colour bitmap already holds.
///
/// It is plain ASCII: a material name that is not is written as character codes, and the quote and the backslash are
/// escaped (<see cref="MaxString"/>), so any name is a correct MaxScript string.
/// </summary>
public static class LevelFbxMaxScript
{
    /// <summary>The script's file name is the FBX's name plus this.</summary>
    public const string Suffix = ".3dsmax-cutouts.ms";

    /// <summary>The script's text (Windows line endings, ASCII only) for the FBX <c>fbxName</c> (no extension) and the FBX names of its cut-out materials.</summary>
    public static string Build(string fbxName, IReadOnlyList<string> materialNames)
    {
        ArgumentNullException.ThrowIfNull(fbxName);
        ArgumentNullException.ThrowIfNull(materialNames);
        var names = materialNames.Distinct(StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        sb.Append($$"""
            -- ======================================================================
            -- {{Plain(fbxName + Suffix)}}
            --
            -- WHAT THIS IS FOR
            --   3ds Max's FBX importer cannot bring a texture's alpha channel in as a
            --   cut-out, so leaves, grass cards and other see-through materials arrive
            --   solid. This script switches the cut-out on for the {{names.Count}} material(s) of the level
            --   {{Plain(fbxName + ".fbx")}} that need one (they are listed below).
            --
            -- Written for 3ds Max 2017 and newer (the Physical Material it edits first appeared in 2017), tested in 3ds Max 2027;
            --   on an older Max the Standard materials in the list are still handled.
            --
            -- HOW TO RUN IT
            --   1. In 3ds Max, File > Import the .fbx that was written beside this file.
            --   2. Scripting > Run Script... and pick this file.
            --   3. Open the MAXScript Listener (F11): it says how many materials were
            --      switched on and names any listed material it did not find in the scene.
            --
            -- WHAT IT CHANGES
            --   Only the materials listed below, matched by name (also inside Multi/Sub-
            --   Object materials). Each gets a Cutout map (Physical Material) or an Opacity
            --   map (Standard material) that reads the ALPHA of the image its base colour
            --   uses, and the Transparency map the importer made from that same image is
            --   cleared. No other material is touched. Running it again changes nothing
            --   more. It holds no file paths: it reuses the image each material already has.
            -- ======================================================================
            (

            """);
        sb.Append("    local cutOutNames = #(\n");
        for (var i = 0; i < names.Count; i++)
            sb.Append("        ").Append(MaxString(names[i])).Append(i < names.Count - 1 ? ",\n" : "\n");
        sb.Append("    )\n");
        sb.Append(Body);
        return sb.ToString().ReplaceLineEndings("\r\n");
    }

    /// <summary>
    /// <paramref name="text"/> as a MaxScript expression that is that string: a quoted run for every stretch of printable
    /// ASCII (<c>"</c> and <c>\</c> escaped), and <c>(bit.intAsChar N)</c> for anything else, joined with <c>+</c>. It always
    /// starts with a quoted run, so every <c>+</c> has a string on its left.
    /// </summary>
    public static string MaxString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>();
        var run = new StringBuilder();
        void EndRun()
        {
            if (run.Length == 0) return;
            parts.Add("\"" + run + "\"");
            run.Clear();
        }

        foreach (var c in text)
        {
            if (c == '"') run.Append("\\\"");
            else if (c == '\\') run.Append("\\\\");
            else if (c >= ' ' && c <= '~') run.Append(c);
            else
            {
                if (parts.Count == 0 && run.Length == 0) parts.Add("\"\"");
                EndRun();
                parts.Add("(bit.intAsChar " + (int) c + ")");
            }
        }
        EndRun();
        return parts.Count switch
        {
            0 => "\"\"",
            1 => parts[0],
            _ => "(" + string.Join(" + ", parts) + ")",
        };
    }

    /// <summary>A name for a comment line: printable ASCII only, anything else is a question mark.</summary>
    private static string Plain(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(c >= ' ' && c <= '~' ? c : '?');
        return sb.ToString();
    }

    // The part of the script that does not depend on the level. Every variable is declared first, in the one block, and
    // there are no functions of its own: nothing in it relies on a nested function reaching the block's variables.
    // PhysicalMaterial and StandardMaterial differ only in the names of four properties, so one path serves both.
    private const string Body = """
            local found = #()            -- the listed names that a material in the scene carries
            local seen = #()             -- the materials already looked at (one material is shared by many objects)
            local todo = #()             -- the materials still to look at: the scene's, then each Multi/Sub-Object's
            local i = 1, m, cls
            local physicalClass = undefined, standardClass = undefined
            local baseProp, cutProp, onProp, clearProp
            local baseMap, imageFile, cutMap, plan, alphaMap, clearMap, nameNow, problem, foundCount, listedCount
            local switchedOn = 0, alreadyOn = 0, leftAlone = 0, noBitmap = 0
            local unsupported = 0, failed = 0, cleared = 0

            -- THE TWO CLASSES ARE LOOKED UP BY NAME, inside a try: written out as PhysicalMaterial, the word is an unknown
            -- variable in a 3ds Max that predates the Physical Material, and that aborts the whole script before it starts.
            try ( physicalClass = execute "PhysicalMaterial" ) catch ( physicalClass = undefined )
            try ( standardClass = execute "StandardMaterial" ) catch ( standardClass = undefined )

            for mat in sceneMaterials do append todo mat
            while i <= todo.count do
            (
                m = todo[i]
                i += 1
                if m != undefined and (findItem seen m) == 0 then
                (
                    append seen m
                    cls = classof m
                    if cls == Multimaterial then
                    (
                        for child in m.materialList do append todo child
                    )
                    else if (findItem cutOutNames m.name) != 0 then
                    (
                        appendIfUnique found m.name
                        baseProp = undefined
                        if physicalClass != undefined and cls == physicalClass then
                        (
                            baseProp = #base_color_map
                            cutProp = #cutout_map
                            onProp = #cutout_map_on
                            clearProp = #transparency_map
                        )
                        else if standardClass != undefined and cls == standardClass then
                        (
                            baseProp = #diffuseMap
                            cutProp = #opacityMap
                            onProp = #opacityMapEnable
                            clearProp = undefined
                        )

                        if baseProp == undefined or not (isProperty m baseProp) or not (isProperty m cutProp) or not (isProperty m onProp) then
                        (
                            unsupported += 1
                        )
                        else
                        (
                            try
                            (
                                baseMap = getProperty m baseProp
                                if classof baseMap == Bitmaptexture and baseMap.filename != undefined and baseMap.filename != "" then
                                (
                                    imageFile = baseMap.filename

                                    -- what is in the cut-out slot: nothing, or a bitmap of this image (make a fresh alpha one,
                                    -- or keep it if an earlier run made it), or the person's own different map (leave it)
                                    cutMap = getProperty m cutProp
                                    if cutMap == undefined then
                                    (
                                        plan = #make
                                    )
                                    else if classof cutMap != Bitmaptexture then
                                    (
                                        plan = #other
                                    )
                                    else if (filenameFromPath cutMap.filename) != (filenameFromPath imageFile) then
                                    (
                                        plan = #other
                                    )
                                    else if cutMap.monoOutput == 1 and cutMap.alphaSource == 0 and cutMap != baseMap then
                                    (
                                        plan = #keep
                                    )
                                    else
                                    (
                                        plan = #make
                                    )

                                    if plan == #make then
                                    (
                                        alphaMap = Bitmaptexture filename:imageFile
                                        alphaMap.monoOutput = 1      -- Mono Channel Output: Alpha
                                        alphaMap.alphaSource = 0     -- Alpha Source: Image Alpha
                                        setProperty m cutProp alphaMap
                                        setProperty m onProp true
                                        switchedOn += 1
                                    )
                                    else if plan == #keep then
                                    (
                                        if (getProperty m onProp) then
                                        (
                                            alreadyOn += 1
                                        )
                                        else
                                        (
                                            setProperty m onProp true
                                            switchedOn += 1
                                        )
                                    )
                                    else
                                    (
                                        leftAlone += 1
                                    )

                                    -- the Transparency map the importer made from this same image would read its colour as transparency
                                    if plan != #other and clearProp != undefined then
                                    (
                                        if isProperty m clearProp then
                                        (
                                            clearMap = getProperty m clearProp
                                            if classof clearMap == Bitmaptexture and (filenameFromPath clearMap.filename) == (filenameFromPath imageFile) then
                                            (
                                                setProperty m clearProp undefined
                                                cleared += 1
                                            )
                                        )
                                    )
                                )
                                else
                                (
                                    noBitmap += 1
                                )
                            )
                            catch
                            (
                                failed += 1
                                nameNow = m.name
                                problem = getCurrentException()
                                format "[cut-outs] could not change %: %\n" nameNow problem
                            )
                        )
                    )
                )
            )

            foundCount = found.count
            listedCount = cutOutNames.count
            format "[cut-outs] % of % listed material(s) are in the scene.\n" foundCount listedCount
            format "[cut-outs] cut-out switched on for % material(s); % already had it on.\n" switchedOn alreadyOn
            if cleared > 0 do format "[cut-outs] cleared the importer's transparency map on % material(s).\n" cleared
            if leftAlone > 0 do format "[cut-outs] % material(s) already have a different cut-out map of their own and were left alone.\n" leftAlone
            if noBitmap > 0 do format "[cut-outs] % material(s) have no base colour bitmap to take the alpha from.\n" noBitmap
            if unsupported > 0 do format "[cut-outs] % material(s) are neither Physical nor Standard materials and were skipped.\n" unsupported
            if failed > 0 do format "[cut-outs] % material(s) could not be changed (see above).\n" failed
            for nm in cutOutNames where (findItem found nm) == 0 do format "[cut-outs] not found in the scene: %\n" nm
        )

        """;
}

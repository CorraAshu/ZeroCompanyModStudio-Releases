namespace UEToolkit.Core;

/// <summary>
/// The Substance 3D Painter export preset for this game, and where somebody might want it.
///
/// Contributed by a Zero Company modder rather than written here, which is the whole reason it
/// ships as a FILE and is copied rather than generated: it is somebody else's work, it is correct
/// as it stands, and the tool's job is to put it where Painter can find it instead of asking
/// people to hunt for a download.
///
/// A <c>.spexp</c> is an export preset -- the rules Painter follows when it writes texture maps
/// out of a project: which channels go into which image, in what order, at what bit depth. Getting
/// those wrong is the commonest reason a texture looks right in Painter and wrong in the game, and
/// it is not something anybody should have to work out from a normal map that came out inverted.
/// </summary>
public static class SubstancePreset
{
    /// <summary>The folder it ships in, beside the executable.</summary>
    public const string Folder = "SubstancePreset";

    /// <summary>The file itself. The name is the preset's name as Painter will show it.</summary>
    public const string FileName = "Zero Company Export.spexp";

    /// <summary>
    /// The shipped preset, or null when this copy does not have one.
    ///
    /// Looked for beside the executable and then upwards, the same way the Blender add-on is
    /// found: a published build has it in the folder beside the exe, and a build run out of the
    /// source tree has it several levels up. One rule covers both rather than a release behaving
    /// differently from the thing that was tested.
    /// </summary>
    public static string? Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var up = 0; dir is not null && up < 8; up++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, Folder, FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Where Painter keeps export presets on this machine, if it can be found.
    ///
    /// Offered as the STARTING POINT of the folder picker rather than as a destination chosen for
    /// them -- the author asked for a picker, and a picker that opens somewhere useless is only
    /// half a picker. Several versions of Painter have shipped under different folder names, so
    /// each is tried and the first that exists wins; null when none does, which the picker reads
    /// as "start wherever you like".
    /// </summary>
    public static string? PainterShelf()
    {
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (documents.Length == 0) return null;

            foreach (var product in new[]
                     {
                         Path.Combine("Adobe", "Adobe Substance 3D Painter"),
                         Path.Combine("Allegorithmic", "Substance Painter"),
                         "Substance 3D Painter",
                     })
            {
                var shelf = Path.Combine(documents, product, "assets", "export-presets");
                if (Directory.Exists(shelf)) return shelf;

                // The product folder without the shelf under it still beats My Documents: it is
                // the right neighbourhood, and Painter makes the shelf on first use.
                var root = Path.Combine(documents, product);
                if (Directory.Exists(root)) return root;
            }
        }
        catch (Exception) { /* a picker that opens in the wrong place is not worth an exception */ }
        return null;
    }

    /// <summary>
    /// Copy the preset into <paramref name="folder"/>. Returns the path written, or null with a
    /// reason.
    ///
    /// It OVERWRITES a file of the same name deliberately: the only reason to run this twice is
    /// to replace an older copy, and a second file called "Zero Company Export (1).spexp" would
    /// show up in Painter's list beside the first with nothing to tell them apart.
    /// </summary>
    public static string? Install(string folder, out string? why)
    {
        why = null;
        try
        {
            if (Source() is not { } from)
            { why = $"this copy of the tool has no {Folder} folder beside it"; return null; }
            if (string.IsNullOrWhiteSpace(folder))
            { why = "no folder was chosen"; return null; }

            Directory.CreateDirectory(folder);
            var to = Path.Combine(folder, FileName);
            File.Copy(from, to, overwrite: true);
            return to;
        }
        catch (Exception e) { why = e.Message; return null; }
    }
}

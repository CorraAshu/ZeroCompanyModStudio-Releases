namespace UEToolkit.Core;

/// <summary>
/// The 3ds Max retarget script, and where somebody might want it.
///
/// `bf2_to_zerocompany.ms` scales and retargets a Battlefront II (2017) character onto this
/// game's rig: it fits on height, then aims and places fifteen bone chains -- torso, arms, legs
/// and all ten fingers -- checking each aim against the scene and flipping it if it went the
/// wrong way. Combined with the import-time weight translation, a port keeps its original
/// weighting all the way into the game.
///
/// IT SHIPS AS A FILE, LIKE THE SUBSTANCE PRESET, and for a stronger reason: it is MAXScript.
/// There is nothing for this program to run and nothing for it to check -- the script's whole job
/// happens inside Max, against a scene this program cannot see. The tool's part is to put it
/// where Max can find it rather than asking people to hunt for a download.
///
/// **1.6.0-rc3 PROMISED THIS FILE BY NAME AND DID NOT CONTAIN IT.** The release notes said
/// "`tools/bf2_to_zerocompany.ms` scales and retargets a Battlefront II (2017) character", and
/// the zip shipped the Blender add-on and the Substance preset and not this. So the one section
/// of those notes nobody could act on was the one describing a file -- which is how the author
/// came to be asked why the retarget did not work in the Mod Studio. It never could: half of it
/// is a Max script that was never delivered, and the other half is the weight translation on
/// import, which did ship.
/// </summary>
public static class MaxRetarget
{
    /// <summary>The folder it ships in, beside the executable.</summary>
    public const string Folder = "3dsMax";

    /// <summary>The file itself, named as the guides and the release notes name it.</summary>
    public const string FileName = "bf2_to_zerocompany.ms";

    /// <summary>
    /// The shipped script, or null when this copy does not have one.
    ///
    /// Beside the executable first and then upwards, the same rule the add-on and the preset
    /// use: a published build has it in the folder beside the exe, and a build run out of the
    /// source tree has it several levels up -- where it lives in `tools/` rather than `3dsMax/`,
    /// so both names are tried. One rule covers both rather than a release behaving differently
    /// from the thing that was tested.
    /// </summary>
    public static string? Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var up = 0; dir is not null && up < 8; up++, dir = dir.Parent)
            foreach (var folder in new[] { Folder, "tools" })
            {
                var candidate = Path.Combine(dir.FullName, folder, FileName);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>
    /// Where Max keeps user scripts on this machine, if it can be found.
    ///
    /// THE USER FOLDER, NEVER Program Files, and that is not only about needing administrator.
    /// A stale copy in `C:\Program Files\Autodesk\3ds Max 2012\Scripts\` shadowed a corrected one
    /// during this script's own development and failed at the same byte offset twice -- which
    /// looks exactly like a fix that did not work, and cost two rounds of debugging a file that
    /// was already right. Max searches the user scripts folder too, and putting it there leaves
    /// the install untouched.
    ///
    /// Offered as the STARTING POINT of the picker rather than as a destination chosen for them,
    /// the same as the Substance preset: Max has shipped a dozen years of differently-named
    /// folders and plenty of people keep scripts with the project instead.
    /// </summary>
    public static string? MaxScripts()
    {
        try
        {
            // Newest first, so a machine with several versions installed starts at the one most
            // likely to be in use. 2012 is in the list because that is the version this script
            // was written against and the author's own.
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0)
            {
                var root = Path.Combine(local, "Autodesk", "3dsMax");
                if (Directory.Exists(root))
                    foreach (var version in Directory.GetDirectories(root)
                                                     .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        var scripts = Path.Combine(version, "ENU", "scripts");
                        if (Directory.Exists(scripts)) return scripts;
                        if (Directory.Exists(version)) return version;
                    }
                if (Directory.Exists(root)) return root;
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (documents.Length > 0)
            {
                var mine = Path.Combine(documents, "3dsMax", "scripts");
                if (Directory.Exists(mine)) return mine;
                var maxDocs = Path.Combine(documents, "3dsMax");
                if (Directory.Exists(maxDocs)) return maxDocs;
            }
        }
        catch (Exception) { /* a picker that opens in the wrong place is not worth an exception */ }
        return null;
    }

    /// <summary>
    /// Copy the script into <paramref name="folder"/>. Returns the path written, or null with a
    /// reason.
    ///
    /// It OVERWRITES a file of the same name deliberately, and here that is the important case
    /// rather than a tidy one: the reason to run this again is to replace an older copy, and an
    /// older copy of THIS script is the single thing most likely to waste somebody's evening.
    /// The script prints its own version when it runs, which is how a stale one gets noticed.
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

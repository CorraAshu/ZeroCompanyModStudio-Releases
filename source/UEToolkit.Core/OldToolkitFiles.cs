using System.Diagnostics;

namespace UEToolkit.Core;

/// <summary>
/// THE DEAD 1.6.x TOOLKIT FILES, and taking them out of the Studio's own folder.
///
/// Before 2.0 the download carried a second program, <c>UEToolkit.App.exe</c>, and the Blender
/// add-on ran that. 2.0 ships none: the Studio answers the add-on itself. But unzipping a new
/// version over an old folder deletes nothing, so the old program stays, next to a replaced
/// <c>UEToolkit.Core.dll</c> it cannot load - and the old add-on that still calls it fails on every
/// import with a bare "the exporter failed" (measured 2026-11-01).
///
/// THE RULES ARE NARROW ON PURPOSE, because this deletes files:
///   - only the folder it is handed (the Studio's own), only plain files directly in it, never a
///     subfolder, never a link, never anything that merely looks similar;
///   - only names that begin <c>UEToolkit.App.</c> (the exe, the dll, its two json files, symbols);
///   - never inside a source checkout (a folder with <c>UEToolkit.slnx</c> above it). That, not the
///     version, is what keeps a developer's own toolkit build safe: the toolkit has never set a file
///     version, so every build of it, old or new, reads 1.0 and passes the check below;
///   - only when the program's own file version is BELOW 2.0, and never when it cannot be read;
///     the json and symbol files left behind once the exe and the dll are gone go too, since they
///     belong to nothing (a partial removal would otherwise be reported at every start);
///   - and every file removed, or refused, is SAID.
/// </summary>
public static class OldToolkitFiles
{
    public const string Exe = "UEToolkit.App.exe";
    public const string Prefix = "UEToolkit.App.";

    /// <summary>The first version that ships none of them.</summary>
    public static readonly Version FirstWithout = new(2, 0);

    /// <summary>The plain files in <paramref name="folder"/> that carry the old toolkit's name.</summary>
    public static List<string> Find(string folder)
    {
        var found = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, Prefix + "*", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                // A link could lead anywhere, and following it is how a delete leaves the folder.
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (!Path.GetFileName(path).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(path);
            }
        }
        catch (Exception) { /* a folder that cannot be listed has nothing to remove */ }
        return found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The toolkit's own file version: the highest the exe and the dll state, or null when neither
    /// can be read. The json files have no version, so the program decides for the whole set.
    /// </summary>
    public static Version? ProgramVersion(IEnumerable<string> files)
    {
        Version? best = null;
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f);
            if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(f);
                var version = new Version(v.FileMajorPart, v.FileMinorPart);
                if (v.FileMajorPart == 0 && v.FileMinorPart == 0 && v.FileBuildPart == 0) continue;
                if (best is null || version > best) best = version;
            }
            catch (Exception) { /* unreadable: it does not vote */ }
        }
        return best;
    }

    /// <summary>
    /// Remove the old toolkit's files from <paramref name="folder"/> and say what happened.
    /// Returns the files removed. Nothing is touched when none are there, so a clean folder costs
    /// one directory listing and prints nothing.
    /// </summary>
    public static List<string> Remove(string folder, Action<string>? say = null)
    {
        var removed = new List<string>();
        var files = Find(folder);
        if (files.Count == 0) return removed;

        if (InSourceCheckout(folder))
        {
            say?.Invoke($"old toolkit files: {folder} is inside a source checkout, so {Prefix}* was left alone");
            return removed;
        }

        var program = files.Any(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetExtension(f).Equals(".dll", StringComparison.OrdinalIgnoreCase));
        var version = ProgramVersion(files);
        if (!program)
        {
            say?.Invoke($"old toolkit files: only the leftovers of a removed {Exe} are in {folder}; removing:");
            Delete(files, removed, say);
            return removed;
        }
        if (version is null)
        {
            say?.Invoke($"old toolkit files: {files.Count} file(s) named {Prefix}* are in {folder}, " +
                        "but their version cannot be read, so they were left alone");
            return removed;
        }
        if (version >= FirstWithout)
        {
            say?.Invoke($"old toolkit files: {Prefix}* in {folder} is version {version}, " +
                        $"not older than {FirstWithout}, so it was left alone");
            return removed;
        }

        say?.Invoke($"old toolkit files: {Prefix}* in {folder} is version {version}. " +
                    "This version ships none of it and the Blender add-on no longer needs it; removing:");
        Delete(files, removed, say);
        return removed;
    }

    private static void Delete(List<string> files, List<string> removed, Action<string>? say)
    {
        foreach (var f in files)
        {
            try
            {
                File.Delete(f);
                removed.Add(f);
                say?.Invoke($"   removed {Path.GetFileName(f)}");
            }
            catch (Exception e)
            {
                say?.Invoke($"   could not remove {Path.GetFileName(f)}: {e.Message}");
            }
        }
    }

    /// <summary>True when <paramref name="folder"/> or a folder above it holds the solution file.</summary>
    public static bool InSourceCheckout(string folder)
    {
        try
        {
            for (var d = new DirectoryInfo(folder); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "UEToolkit.slnx"))) return true;
        }
        catch (Exception) { return true; /* cannot tell: treat it as one, and touch nothing */ }
        return false;
    }
}

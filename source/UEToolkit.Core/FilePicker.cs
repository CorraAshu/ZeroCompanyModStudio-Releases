using System.Diagnostics;

namespace UEToolkit.Core;

/// <summary>
/// Ask for a FILE, the way <see cref="FolderPicker"/> asks for a folder.
///
/// Written because the mod editor could list your own icons, meshes and textures but had no way to
/// GET one: the files had to be copied by hand into a folder the tool never named on screen, which
/// is a step nobody can be expected to guess. Same shape as FolderPicker, same reasons -- a
/// PowerShell dialog on an STA thread, because the WinForms dialogs will not open on a
/// multi-threaded apartment and PowerShell uses one by default.
/// </summary>
public static class FilePicker
{
    /// <summary>The chosen file, or null if the dialog was cancelled or could not be shown.</summary>
    public static string? Open(string? filter = null, string? title = null, string? initial = null)
    {
        try
        {
            var script =
                "Add-Type -AssemblyName System.Windows.Forms | Out-Null; " +
                "$f = New-Object System.Windows.Forms.OpenFileDialog; " +
                $"$f.Title = '{Escape(title ?? "Choose a file")}'; " +
                $"$f.Filter = '{Escape(filter ?? "All files (*.*)|*.*")}'; " +
                "$f.Multiselect = $false; " +
                "$f.CheckFileExists = $true; " +
                (string.IsNullOrWhiteSpace(initial) || !Directory.Exists(initial)
                    ? ""
                    : $"$f.InitialDirectory = '{Escape(initial!)}'; ") +
                "if ($f.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) " +
                "{ [Console]::Out.Write($f.FileName) }";

            var psi = new ProcessStartInfo("powershell")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-STA");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var chosen = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(600000);
            return chosen.Length > 0 && File.Exists(chosen) ? chosen : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Copy <paramref name="source"/> into <paramref name="folder"/>, creating it, and return the
    /// file NAME that was written.
    ///
    /// The name rather than the path, because that is what a mod stores: a bare filename the build
    /// resolves against the ModSource folders. Storing a full path would work on the machine it
    /// was authored on and nowhere else, which is the failure a mod author would find out about
    /// from somebody else's bug report.
    /// </summary>
    /// <summary>What a copy did, so the caller can say it rather than always saying "imported".</summary>
    public enum Outcome { Added, Replaced, Unchanged, AlreadyThere }

    /// <summary>
    /// The file's CONTENT, hashed.
    ///
    /// This was size plus last-write, and that test lies in both directions. File.Copy carries the
    /// SOURCE's timestamp onto the destination, so a real replacement can leave size and mtime
    /// looking untouched -- and an author's texture exports are all exactly the same size, which
    /// takes the other half of the test out too. It reported "nothing changed" over a copy that
    /// had changed everything, and the author lost an evening to a helmet wearing another
    /// helmet's normal map.
    ///
    /// Hashing costs a read of a file that was just written and is therefore in cache. A message
    /// about whether the bytes changed has to be about the bytes.
    /// </summary>
    private static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    public static string? CopyInto(string source, string folder, out string? why) =>
        CopyInto(source, folder, out why, out _);

    public static string? CopyInto(string source, string folder, out string? why,
                                   out Outcome outcome)
    {
        why = null;
        outcome = Outcome.Added;
        try
        {
            Directory.CreateDirectory(folder);
            var name = Path.GetFileName(source);
            var target = Path.Combine(folder, name);

            // Copying a file over itself throws, and choosing the file that is already there is a
            // reasonable thing to do -- it means "use this one".
            var same = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target),
                                     StringComparison.OrdinalIgnoreCase);

            // WHAT ACTUALLY HAPPENED, because "imported" covered three different outcomes and an
            // author could not tell them apart.
            //
            // An author who re-exports art under a name ModSource already holds needs to know
            // whether the bytes changed. One spent hours on a helmet wearing another helmet's
            // normal map, because the file in ModSource was never the one they thought they had
            // put there -- and every message along the way said "imported".
            var existed = File.Exists(target);
            var before = existed ? Fingerprint(target) : null;

            if (!same) File.Copy(source, target, overwrite: true);

            // THE COPY IS CHECKED, NOT ASSUMED.
            //
            // An author re-imported the same four files all evening and the two that mattered
            // never changed on disk; deleting them by hand and importing the identical files then
            // worked. Whatever swallowed those writes, the tool reported success for every one of
            // them. So the destination is read back and compared to the source, and a copy that
            // did not land is now a failure with a reason rather than a cheerful message.
            var after = Fingerprint(target);
            if (!same && after != Fingerprint(source))
            {
                why = $"{name} was copied into the folder but the file there does not match the " +
                      "one chosen -- something else is holding or replacing it. Delete it from " +
                      "the ModSource folder by hand and import again.";
                return null;
            }

            outcome = same ? Outcome.AlreadyThere
                    : !existed ? Outcome.Added
                    : before == after ? Outcome.Unchanged
                    : Outcome.Replaced;

            return name;
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    private static string Escape(string s) => s.Replace("'", "''");
}

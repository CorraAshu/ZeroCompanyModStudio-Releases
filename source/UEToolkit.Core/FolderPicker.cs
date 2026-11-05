using System.Diagnostics;

namespace UEToolkit.Core;

/// <summary>
/// A native "choose a folder" dialog, for pointing the toolkit at a game without typing a path.
///
/// Driven through PowerShell rather than linked, which is the same trade this toolkit already
/// makes for the FBX SDK and vgmstream: no COM interop, no WinForms reference, nothing added to
/// the build. The dialog is a Windows one, and so is this toolkit.
///
/// **It must never be called on the render thread.** `ShowDialog` blocks until the user answers,
/// which could be a minute; the UI would be frozen for all of it.
/// </summary>
public static class FolderPicker
{
    /// <summary>
    /// The chosen folder, or null if the dialog was cancelled or could not be shown.
    ///
    /// <paramref name="allowNewFolder"/> shows the dialog's own "Make New Folder" button, and is
    /// OFF by default because most callers here are asking where something ALREADY IS -- a game
    /// install, a Paks directory -- and offering to create one there invites an empty folder that
    /// then fails every check downstream. Turn it on when the question is where something should
    /// GO, which is the mods folder and nothing else so far.
    /// </summary>
    public static string? Browse(string? initial = null, string? description = null,
                                 bool allowNewFolder = false)
    {
        try
        {
            // -STA is required: the folder dialog is a COM/WinForms control and will not open on
            // a multi-threaded apartment, which is what PowerShell uses by default.
            var script =
                "Add-Type -AssemblyName System.Windows.Forms | Out-Null; " +
                "$f = New-Object System.Windows.Forms.FolderBrowserDialog; " +
                $"$f.Description = '{Escape(description ?? "Select the game's Paks folder")}'; " +
                $"$f.ShowNewFolderButton = ${(allowNewFolder ? "true" : "false")}; " +
                (string.IsNullOrWhiteSpace(initial) ? "" : $"$f.SelectedPath = '{Escape(initial!)}'; ") +
                "if ($f.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) " +
                "{ [Console]::Out.Write($f.SelectedPath) }";

            var psi = new ProcessStartInfo("powershell")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-STA");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(600000);
            return outp.Length > 0 && Directory.Exists(outp) ? outp : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// A game's Paks folder from any folder inside its install: picking `.../SomeGame` when
    /// the paks are three levels down is the obvious mistake, and one search is cheaper than
    /// making someone find it themselves. Returns the input unchanged when nothing better exists.
    /// </summary>
    public static string ResolvePaks(string chosen)
    {
        try
        {
            if (!Directory.Exists(chosen)) return chosen;
            if (HasPaks(chosen)) return chosen;

            // A "Paks" directory below the chosen one, shallowest first.
            var found = Directory.EnumerateDirectories(chosen, "Paks", SearchOption.AllDirectories)
                .Where(HasPaks)
                .OrderBy(d => d.Count(c => c is '/' or '\\'))
                .FirstOrDefault();
            return found ?? chosen;
        }
        catch { return chosen; }
    }

    /// <summary>Whether a directory holds containers. Public because "is this really the Paks
    /// folder" is also the question the installer has to answer before writing into it.</summary>
    public static bool HasPaks(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                          f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string Escape(string s) => s.Replace("'", "''");
}

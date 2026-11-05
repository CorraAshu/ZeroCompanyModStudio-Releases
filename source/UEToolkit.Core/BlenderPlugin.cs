namespace UEToolkit.Core;

/// <summary>
/// Install the Blender add-on into somebody's Blender, without them finding folders.
///
/// The add-on itself does not read the game. It shells out to the Mod Studio's own command line
/// (<see cref="AddonBackend"/> answers it, with no window) -- one call to list what exists, one per
/// import -- so installing it is three things: copy the folder, write down where the backend is,
/// and say the one step that cannot be automated.
///
/// THAT LAST STEP IS REAL AND IS NOT A GAP IN THIS CODE. Blender keeps its list of enabled add-ons
/// inside userpref.blend, its own binary preferences file, which is rewritten wholesale whenever
/// Blender exits. Editing it from outside is both undocumented and a good way to lose somebody's
/// preferences, and writing it while Blender is open is guaranteed to be overwritten. So the
/// install ends by telling them to tick a box, which takes five seconds and cannot go wrong.
/// </summary>
public static class BlenderPlugin
{
    /// <summary>The add-on's folder name, in the release and in Blender's extensions folder.</summary>
    public const string AddonId = "zerocompany_importer";

    /// <param name="Version">"5.0", "4.4" -- Blender keeps one settings tree per minor version.</param>
    /// <param name="Root">That version's settings folder, the parent of extensions/ and scripts/.</param>
    /// <param name="Extensions">
    /// True from 4.2, where add-ons became "extensions" with their own folder and a manifest.
    /// Before that they went in scripts/addons. Both are still loaded by 4.2+, but an extension
    /// installed the old way shows up in a different list and confuses the instruction to tick it.
    /// </param>
    public sealed record Install(string Version, string Root, bool Extensions)
    {
        public string Target => Extensions
            ? Path.Combine(Root, "extensions", "user_default", AddonId)
            : Path.Combine(Root, "scripts", "addons", AddonId);

        /// <summary>What Blender calls the add-on in its own preferences, for the instruction.</summary>
        public string Module => Extensions ? $"bl_ext.user_default.{AddonId}" : AddonId;

        public bool Present => File.Exists(Path.Combine(Target, "__init__.py"));

        /// <summary>
        /// When this copy was put there: the file's CREATION time, not its last-write time. A copy
        /// keeps the SOURCE's last-write time (so the Plugins window said an add-on installed today
        /// was installed the day it was written), but the copy itself is a new file made now.
        /// </summary>
        public DateTime? InstalledAt =>
            Present ? File.GetCreationTime(Path.Combine(Target, "__init__.py")) : null;

        /// <summary>
        /// The version the INSTALLED add-on's manifest states ("0.2.0"), or null when there is no
        /// readable one. Read off the installed copy, never remembered: the person may have
        /// updated it, removed it or put an older one back by hand since the Studio last looked.
        /// </summary>
        public string? AddonVersion =>
            Present ? ManifestVersion(Path.Combine(Target, "blender_manifest.toml")) : null;

        /// <summary>
        /// The program the installed add-on will run, as its backend.json names it, or null. The
        /// tell for an add-on installed by an older Studio: it names UEToolkit.App.exe, a program
        /// this Studio no longer ships.
        /// </summary>
        public string? BackendNamed
        {
            get
            {
                try
                {
                    var file = Path.Combine(Target, "backend.json");
                    if (!File.Exists(file)) return null;
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    return doc.RootElement.TryGetProperty("backend", out var b) &&
                           b.ValueKind == System.Text.Json.JsonValueKind.String ? b.GetString() : null;
                }
                catch (Exception) { return null; }
            }
        }
    }

    /// <summary>
    /// DEVELOPER RUNS ONLY: look for Blender's settings under THIS folder instead of
    /// <c>%APPDATA%\Blender Foundation\Blender</c>. The folder is the one that holds a folder per
    /// Blender version ("5.1", "4.2"), so a test can install, update and remove the add-on in a
    /// scratch tree and never touch the add-ons of a Blender somebody is using.
    ///
    /// Null in every release: only the Studio's developer build ever sets it (from an environment
    /// variable that does not exist in a published build), and nothing here reads an environment
    /// variable. When it IS set, <see cref="Installs"/> looks only there - a folder that is not
    /// there means "no Blender found", never a fall-back to the real one.
    /// </summary>
    public static string? RootOverride { get; set; }

    /// <summary>The folder that holds one settings folder per Blender version.</summary>
    public static string BaseDirectory()
    {
        if (RootOverride is { Length: > 0 } scratch) return scratch;
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, "Blender Foundation", "Blender");
    }

    /// <summary>The <c>version = "x.y.z"</c> line of an extension manifest, or null.</summary>
    public static string? ManifestVersion(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            foreach (var raw in File.ReadLines(manifestPath))
            {
                var line = raw.Trim();
                if (!line.StartsWith("version", StringComparison.Ordinal)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0 || line[..eq].Trim() != "version") continue;
                var v = line[(eq + 1)..].Trim().Trim('"', '\'');
                return v.Length > 0 ? v : null;
            }
        }
        catch (Exception) { /* an unreadable manifest is the same answer as none */ }
        return null;
    }

    /// <summary>The version of the add-on this Studio ships, from its own manifest.</summary>
    public static string? ShippedVersion() =>
        Source() is { } src ? ManifestVersion(Path.Combine(src, "blender_manifest.toml")) : null;

    /// <summary>Why an installed add-on should be updated, and what the numbers were.</summary>
    /// <param name="OldProgram">
    /// True only when the installed add-on's backend.json names UEToolkit.App.exe: it runs a program
    /// this Studio does not ship, so importing from Blender fails. An older add-on that names
    /// something else is merely older, and must not be told it is broken in that particular way.
    /// </param>
    public sealed record Stale(Install Install, string? Installed, string Shipped, string Why,
                               bool OldProgram = false);

    /// <summary>
    /// Whether the add-on installed for this Blender is OLDER than the one this Studio ships, and
    /// if so what to tell the person. Null when it is current, newer, absent, or when this Studio
    /// ships none to compare with.
    ///
    /// WHY THIS MATTERS, measured: an add-on installed by the 1.6.x Studio calls
    /// <c>UEToolkit.App.exe</c>. Unzipping 2.0 over that folder leaves the old program next to a
    /// newer library it cannot load, and every import from Blender then fails with nothing but
    /// "the exporter failed". The newer add-on calls the Studio itself, so updating it is the fix.
    /// A version that cannot be read counts as old: nothing that ships has been without one.
    /// </summary>
    public static Stale? StaleOf(Install install)
    {
        if (!install.Present || ShippedVersion() is not { } shipped) return null;

        var have = install.AddonVersion;
        var oldProgram = install.BackendNamed is { } named &&
                         Path.GetFileName(named).Equals(OldToolkitFiles.Exe, StringComparison.OrdinalIgnoreCase);

        // NO MANIFEST VERSION AT ALL counts as old (nothing that ships has been without one); a version
        // that is THERE counts as old only when it is provably older (see ProvablyOlder). A newer
        // pre-release ("0.3.0-rc1") parses as newer, and a string nobody can read is left alone: an
        // "update" that might be a downgrade is worse than none.
        var older = have is null || ProvablyOlder(have, shipped);
        if (!older) return null;

        var why = have is null
            ? $"its version cannot be read; this Studio has {shipped}"
            : $"it is version {have}; this Studio has {shipped}";
        if (oldProgram) why += $", and it still runs {OldToolkitFiles.Exe}, which this Studio does not ship";
        return new Stale(install, have, shipped, why, oldProgram);
    }

    /// <summary>
    /// Whether <paramref name="have"/> is provably OLDER than <paramref name="shipped"/>, reading
    /// both as "x.y.z" with an optional "-suffix" (a pre-release) and "+build" (ignored). The core
    /// numbers decide; with equal cores a pre-release is older than the plain version, and two
    /// pre-releases of one core are not ordered (neither is offered over the other). A string that
    /// does not read as a version is never provably older, from either side.
    /// </summary>
    public static bool ProvablyOlder(string have, string shipped)
    {
        if (!TrySemver(have, out var hc, out var hSuffix) || !TrySemver(shipped, out var sc, out var sSuffix))
            return false;
        var by = hc.CompareTo(sc);
        if (by != 0) return by < 0;
        return hSuffix && !sSuffix;
    }

    private static bool TrySemver(string text, out Version core, out bool preRelease)
    {
        core = new Version(0, 0);
        preRelease = false;
        var t = text.Trim();
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        var dash = t.IndexOf('-');
        if (dash >= 0) { preRelease = dash < t.Length - 1; t = t[..dash]; }
        if (t.Length == 0) return false;
        if (!t.Contains('.')) t += ".0";   // "2" is 2.0
        return Version.TryParse(t, out core!);
    }

    /// <summary>Whether a Blender is running right now (by process name, which is all it needs).</summary>
    public static bool BlenderRunning()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("blender"))
            {
                p.Dispose();
                return true;
            }
        }
        catch (Exception) { /* cannot tell: say nothing rather than guess */ }
        return false;
    }

    /// <summary>
    /// Every Blender whose settings folder exists, newest first.
    ///
    /// Found through the SETTINGS folder rather than by looking for blender.exe, deliberately. The
    /// settings folder is where an add-on has to go, it is the same place whether Blender came
    /// from the installer, a zip, or Steam -- and Steam's copy is not under Program Files at all,
    /// so hunting for the executable finds one Blender and misses the one somebody actually uses.
    /// The cost is that a Blender installed and never once launched has no folder and is not
    /// listed, which is the right trade: it also has no preferences to enable the add-on in.
    /// </summary>
    public static List<Install> Installs()
    {
        var found = new List<Install>();
        var baseDir = BaseDirectory();
        if (!Directory.Exists(baseDir)) return found;

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            var name = Path.GetFileName(dir);
            if (!Version.TryParse(name.Contains('.') ? name : name + ".0", out var v)) continue;
            // 2.79 and friends predate the add-on API this uses and cannot run it. Listing them
            // would offer an install that installs successfully and never works.
            //
            // NOR DOES 4.0 OR 4.1: the add-on is an EXTENSION (blender_manifest.toml, blender_version_min
            // 4.2.0, and no bl_info on purpose), which those versions do not read. The old window listed
            // them and installed into scripts/addons, where Blender would never load it.
            if (v < new Version(4, 2)) continue;
            found.Add(new Install(name, dir, Extensions: v >= new Version(4, 2)));
        }

        found.Sort((a, b) => Version.Parse(b.Version).CompareTo(Version.Parse(a.Version)));
        return found;
    }

    /// <summary>
    /// Where the add-on ships: beside the executable in a release, up in the repo in a dev build.
    ///
    /// WALKED UP RATHER THAN COUNTED. A fixed number of "../" is wrong the moment the two builds
    /// nest differently -- the Studio sits under bin/Release/net10.0-windows/win-x64 and the
    /// command line under bin/Release/net10.0/win-x64, which are not the same depth from the
    /// repository root, and a guess that is one level short simply reports the add-on as missing.
    /// </summary>
    public static string? Source() => FindUpwards(Path.Combine("BlenderAddon", AddonId), "__init__.py");

    /// <summary>
    /// Look beside the executable, then in each folder above it, for <paramref name="relative"/>.
    /// </summary>
    private static string? FindUpwards(string relative, string mustContain)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var up = 0; dir is not null && up < 8; up++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(Path.Combine(candidate, mustContain))) return candidate;
            if (mustContain.Length == 0 && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The Mod Studio's executable, which answers the add-on's two flags itself.</summary>
    public const string BackendExe = "ZeroCompanyModStudio.exe";

    /// <summary>
    /// The exe the add-on will call: the Mod Studio, which ships as the one program in a release.
    /// </summary>
    public static string? Backend()
    {
        // A release is one folder, so this is the answer nearly always.
        var here = Path.Combine(AppContext.BaseDirectory, BackendExe);
        if (File.Exists(here)) return here;

        // A developer build: the Studio's build is in a sibling bin folder. Walked up for the same
        // reason Source() is.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var up = 0; dir is not null && up < 8; up++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ZeroCompanyModStudio.Avalonia", "bin", "Release",
                                         "net10.0", "win-x64", BackendExe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Copy the add-on in and point it at the backend. Returns null on success, or why not.
    /// </summary>
    public static string? Apply(Install into)
    {
        if (Source() is not { } source)
            return "The add-on is missing from this installation -- expected a BlenderAddon folder " +
                   "beside the program.";
        if (Backend() is not { } backend)
            return $"Could not find {BackendExe}, which the add-on needs in order to read the " +
                   "game. It should sit beside this program.";

        // BUILT BESIDE, THEN SWAPPED IN. The new add-on is assembled in a folder next to the target and
        // the old one is renamed away before it moves in, so a file Blender has open stops the swap
        // BEFORE anything has changed - the old add-on is still whole and the message is true. (This
        // used to delete the old folder first: a locked file part-way through left half an add-on, a
        // manifest gone and an __init__.py still there, which is how this was found.)
        var parent = Path.GetDirectoryName(into.Target)!;
        var staging = Path.Combine(parent, "." + AddonId + ".staging");
        var replaced = Path.Combine(parent, "." + AddonId + ".replaced");

        try
        {
            // What an interrupted earlier run left behind.
            foreach (var leftover in new[] { staging, replaced })
                if (Directory.Exists(leftover)) Directory.Delete(leftover, recursive: true);

            Directory.CreateDirectory(staging);

            // REPLACED, not merged. A previous version's file that this one no longer ships would
            // otherwise stay behind and be imported, and a half-old add-on fails in ways that
            // belong to neither version.
            foreach (var file in Directory.GetFiles(source))
            {
                // __pycache__ is skipped by the directory filter below; backend.json is written
                // fresh rather than copied, because the one in a source tree names another machine.
                var leaf = Path.GetFileName(file);
                if (leaf.Equals("backend.json", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(staging, leaf), overwrite: true);
            }

            File.WriteAllText(Path.Combine(staging, "backend.json"),
                              "{\n  \"backend\": " + System.Text.Json.JsonSerializer.Serialize(backend) + "\n}\n");

            if (Directory.Exists(into.Target)) Directory.Move(into.Target, replaced);
            try { Directory.Move(staging, into.Target); }
            catch
            {
                // Put the old one back rather than leave nothing.
                if (!Directory.Exists(into.Target) && Directory.Exists(replaced)) Directory.Move(replaced, into.Target);
                throw;
            }

            // The new one is in place. A leftover old copy is harmless and is removed next time.
            // The same goes for a ".removed" folder a failed Remove left behind: it only ever got
            // cleared by the next Remove, so an add-on that was updated rather than removed kept it.
            var removed = Path.Combine(parent, "." + AddonId + ".removed");
            foreach (var old in new[] { replaced, removed })
            {
                try { if (Directory.Exists(old)) Directory.Delete(old, recursive: true); }
                catch (Exception) { }
            }
            return null;
        }
        catch (UnauthorizedAccessException e)
        {
            return "Blender's add-on folder refused the write: " + e.Message;
        }
        catch (IOException e)
        {
            // The overwhelmingly likely cause, and worth naming rather than reporting a file lock.
            return "Could not write the add-on -- close Blender and try again. (" + e.Message + ")";
        }
        catch (Exception e)
        {
            return e.Message;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception) { /* a leftover staging folder is removed by the next run */ }
        }
    }

    /// <summary>Take it out again, for somebody who wants it gone.</summary>
    public static string? Remove(Install from)
    {
        try
        {
            if (!Directory.Exists(from.Target)) return null;

            // RENAMED AWAY FIRST, then deleted: a file Blender has open stops the rename while
            // everything is still in place, where deleting in place could stop half way and leave
            // half an add-on.
            var gone = Path.Combine(Path.GetDirectoryName(from.Target)!, "." + AddonId + ".removed");
            if (Directory.Exists(gone)) Directory.Delete(gone, recursive: true);
            Directory.Move(from.Target, gone);
            try { Directory.Delete(gone, recursive: true); }
            catch (Exception) { /* it is out of Blender's sight; the next run clears it */ }
            return null;
        }
        catch (Exception e) { return e.Message; }
    }
}

using System.Diagnostics;

namespace UEToolkit.Core;

/// <summary>
/// Drives the FBX conversion as a subprocess.
///
/// The Autodesk FBX SDK has no supported .NET binding, and the Python one is already installed on
/// any machine with Max. Shelling out keeps a native, versioned, licence-encumbered SDK out of
/// this build entirely: nothing here links it, and a machine without it loses FBX and nothing
/// else. The cost is a process launch per mesh, which is nothing beside decoding one.
/// </summary>
public static class FbxBridge
{
    /// <summary>
    /// A Python that can `import fbx`. The SDK installs into ONE interpreter, usually not the
    /// one on PATH — here it is 3.10 while `python` resolves to a 3.11 Store stub — so the check
    /// is whether the module imports, never which interpreter looks most official.
    /// </summary>
    public static string? FindPython()
    {
        var candidates = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var progs = Path.Combine(local, "Programs", "Python");
        if (Directory.Exists(progs))
            candidates.AddRange(Directory.GetDirectories(progs)
                .Select(d => Path.Combine(d, "python.exe")).Where(File.Exists));
        foreach (var root in new[] { @"C:\Python310", @"C:\Python39", @"C:\Python37" })
            if (File.Exists(Path.Combine(root, "python.exe")))
                candidates.Add(Path.Combine(root, "python.exe"));
        candidates.Add("python");

        foreach (var c in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(c, "-c \"import fbx\"")
                {
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.WaitForExit(20000);
                if (p.ExitCode == 0) return c;
            }
            catch { /* a candidate that will not launch is simply not the one */ }
        }
        return null;
    }

    /// <summary>The converter, looked up beside the binary or up the tree from it.</summary>
    public static string? FindScript(string name = "psk2fbx.py")
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Write the merge manifest for one exported character, and convert it to a single FBX.
    ///
    /// The placement decision comes from <see cref="PlacementScript"/> — the same code that emits
    /// the MaxScript — so the FBX and the MaxScript put every part in the same place by
    /// construction rather than by two implementations happening to agree.
    /// </summary>
    public static (bool Ok, string Output) MergeCharacter(
        string python, string script, IReadOnlyList<string> partFiles,
        string destination, string? psa = null, bool ascii = false,
        IReadOnlyDictionary<string, (string Bone, bool OriginAuthored)>? attachments = null,
        string? version = null)
    {
        var usable = partFiles.Where(File.Exists).ToList();
        if (usable.Count == 0) return (false, "no readable parts");

        // The rig comes from the part with the most bones; a helmet's copy is only as good as
        // the body's when they agree, and a character can ship two generations that do not.
        var host = usable
            .Select(f => (File: f, Bones: PlacementScript.BonePositions(f)))
            .OrderByDescending(x => x.Bones.Count).First();

        var manifest = Path.ChangeExtension(destination, ".merge.tsv");

        // Prefer a manifest written from the game's own region data. Guessing a part's attach
        // bone by proximity does not work and must not be attempted: origin-authored parts all
        // sit near (0,0,0), so "nearest bone" put a backpack on a knee helper bone,
        // a chest on a toe bone and a head on the world root. The game's region table states the answer, and
        // BatchExporter.WritePlacement writes it out beside the export.
        var supplied = attachments ?? FindManifestFor(usable);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);

        // dx/dy/dz/move are INERT. psk2fbx.py parses them and never reads them again -- the merge
        // ships parts unplaced by design, and says so on the console. They are written because the
        // bone column beside them is real and because placement may return, but nothing downstream
        // moves anything.
        //
        // Left as a trap sprung on 2026-08-05: seeing PskSkeleton.Positions feeding these columns,
        // I concluded merged FBX exports were silently carrying an FK error and made it the top
        // priority of a session. They carry nothing. **A value that is written and parsed still is
        // not a value that is USED** -- grep the consumer before believing an output is affected.
        var lines = new List<string> { "# psk\tbone\tdx\tdy\tdz\tmove   (dx..move are INERT, see note)" };
        foreach (var f in usable)
        {
            var key = Path.GetFileNameWithoutExtension(f);
            if (supplied is not null && supplied.TryGetValue(key, out var a) &&
                host.Bones.TryGetValue(a.Bone, out var bonePos))
            {
                lines.Add(string.Join('\t', f, a.Bone,
                    bonePos.X.ToString("0.######"), bonePos.Y.ToString("0.######"),
                    bonePos.Z.ToString("0.######"), a.OriginAuthored ? "1" : "0"));
                continue;
            }
            // No stated bone: leave the part at the scene root, where it was authored. Wrong in
            // an obvious way if it needed attaching, rather than wrong in a plausible way.
            lines.Add(string.Join('\t', f, "", "0", "0", "0", "0"));
        }
        File.WriteAllLines(manifest, lines);

        try
        {
            var psi = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(manifest);
            psi.ArgumentList.Add(destination);
            if (psa is { Length: > 0 }) psi.ArgumentList.Add(psa);
            psi.ArgumentList.Add("--merge");
            if (ascii) psi.ArgumentList.Add("--ascii");
            if (version is { Length: > 0 }) { psi.ArgumentList.Add("--fbxver"); psi.ArgumentList.Add(version); }

            using var p2 = Process.Start(psi);
            if (p2 is null) return (false, "could not start python");
            var so = p2.StandardOutput.ReadToEnd();
            var se = p2.StandardError.ReadToEnd();
            p2.WaitForExit(600_000);
            return p2.ExitCode == 0 ? (true, so) : (false, se.Length > 0 ? se : so);
        }
        catch (Exception e) { return (false, e.Message); }
    }

    /// <summary>
    /// A `*-attach.tsv` written beside the exported parts, if a character export left one.
    /// Format: mesh name, bone, origin-authored — produced by
    /// <see cref="BatchExporter.WritePlacement"/> from the game's own region data.
    /// </summary>
    private static Dictionary<string, (string Bone, bool OriginAuthored)>? FindManifestFor(
        IReadOnlyList<string> parts)
    {
        var dirs = parts.Select(Path.GetDirectoryName).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            var dir = new DirectoryInfo(d);
            // Deep enough to reach the export root from a part: the game tree is mirrored under
            // it, so Mesh/Variant/Foo/Characters/Content/<Project> is already six levels.
            for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            {
                var hit = Directory.EnumerateFiles(dir.FullName, "*-attach.tsv").FirstOrDefault();
                if (hit is null) continue;
                var map = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadLines(hit).Skip(1))
                {
                    var f = line.Split('\t');
                    if (f.Length >= 3) map[f[0]] = (f[1], f[2] == "1");
                }
                if (map.Count > 0) return map;
            }
        }
        return null;
    }

    /// <param name="split">
    /// One object per material id instead of one mesh carrying a Multi/Sub-Object material.
    ///
    /// This is the step the user was doing by hand after every ActorX import — a B1 battle droid
    /// arrives as a single object and has to be detached by material id before anything can be
    /// textured. Doing it during conversion costs nothing and removes the step entirely.
    /// </param>
    public static (bool Ok, string Output) Convert(string python, string script,
                                                   string source, string destination,
                                                   bool ascii = false, string? psa = null,
                                                   string? version = null, bool split = false,
                                                   string? rotate = null)
    {
        try
        {
            var psi = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add(destination);
            // Positional third argument, so it must come before any flag.
            if (psa is { Length: > 0 }) psi.ArgumentList.Add(psa);
            if (ascii) psi.ArgumentList.Add("--ascii");
            if (split) psi.ArgumentList.Add("--split");
            // Takes a VALUE, so psk2fbx's own arg parser has to know about it too --
            // otherwise "-90,0,-90" falls through and is read as the output path.
            if (rotate is { Length: > 0 }) { psi.ArgumentList.Add("--rotate"); psi.ArgumentList.Add(rotate); }
            if (version is { Length: > 0 }) { psi.ArgumentList.Add("--fbxver"); psi.ArgumentList.Add(version); }

            using var p = Process.Start(psi);
            if (p is null) return (false, "could not start python");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(300_000);
            return p.ExitCode == 0 ? (true, stdout) : (false, stderr.Length > 0 ? stderr : stdout);
        }
        catch (Exception e) { return (false, e.Message); }
    }
}

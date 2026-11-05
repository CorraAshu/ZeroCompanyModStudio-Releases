using System.Diagnostics;

namespace UEToolkit.Core;

/// <summary>
/// Drives vgmstream as a subprocess to turn Wwise `.wem` into `.wav`.
///
/// CUE4Parse extracts `.wem` but does not decode it — `SoundDecoder` has a literal
/// `case "WEM": return input;`, handing the bytes straight through. Decoding them properly means
/// Wwise Vorbis, whose codebooks are stripped out of the header and have to be rebuilt from a
/// lookup table, plus Opus, ATRAC9, XMA and a handful of ADPCM variants. vgmstream already does
/// all of that and is the tool every other extractor in this space leans on.
///
/// Same shape as <see cref="FbxBridge"/>, and for the same reason: a native, codec-encumbered
/// dependency stays out of this build entirely. Nothing here links it; a machine without it
/// loses WAV conversion and nothing else — the naming and extraction still work and write `.wem`.
/// </summary>
public static class WwiseBridge
{
    /// <summary>
    /// Explicit path from the profile or the command line, tried before anything is searched for.
    /// </summary>
    public static string? ConfiguredPath { get; set; }

    private static string? _cached;

    /// <summary>
    /// vgmstream-cli, or null. Verified by RUNNING it, not by finding a file with the right
    /// name: a stub, a wrong architecture or a missing codec DLL all produce a path that exists
    /// and an executable that cannot decode anything.
    /// </summary>
    public static string? Find()
    {
        if (_cached is not null) return _cached;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(ConfiguredPath)) candidates.Add(ConfiguredPath!);

        // Beside the toolkit, the way a bundled tool would sit.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "tools", "vgmstream", "vgmstream-cli.exe"));
            candidates.Add(Path.Combine(dir.FullName, "tools", "vgmstream-cli.exe"));
        }

        candidates.Add("vgmstream-cli");   // PATH
        candidates.Add("test.exe");        // what older builds called it

        foreach (var c in candidates)
        {
            if (Probe(c)) { _cached = c; return c; }
        }
        return null;
    }

    private static bool Probe(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "-h")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return stdout.Contains("vgmstream", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The banner line, for the log. Knowing WHICH build ran matters when output is odd.</summary>
    public static string Version()
    {
        var exe = Find();
        if (exe is null) return "vgmstream: not found";
        try
        {
            var psi = new ProcessStartInfo(exe, "-h")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(15000);
            return $"{line.Trim()}  [{exe}]";
        }
        catch { return $"vgmstream at {exe} (version unreadable)"; }
    }

    public sealed record Result(bool Success, string? Error);

    /// <summary>
    /// Decode one `.wem` on disk to `.wav`.
    ///
    /// <c>-i</c> is not optional for extraction. vgmstream's DEFAULT is to play the stream twice
    /// and fade out over ten seconds, because it is built for listening; applied to an asset dump
    /// that silently doubles the length of every looping sound and fades the end of it. What we
    /// want is one faithful pass.
    /// </summary>
    public static Result ToWav(string wemPath, string wavPath)
    {
        var exe = Find();
        if (exe is null) return new Result(false, "vgmstream-cli not found");

        try
        {
            // vgmstream is a native CLI and cannot open a path past Windows' 260-character limit,
            // so it reports "failed to open ... for output" and we count a failure. Zero Company's
            // VO conversation tree reaches ~270 characters on its own -- 102 lines lost that way,
            // all of them deep under VO_Conversations, none of them anything to do with the audio.
            // Convert beside the .wem, where the path is short, then move the result into place.
            //
            // The move needs the \\?\ prefix, and that prefix is strict: it must be a FULL path
            // with backslashes only, so GetFullPath first. A malformed one does not fail loudly,
            // it fails as "The filename, directory name, or volume label syntax is incorrect".
            var target = wavPath;
            var viaShortPath = wavPath.Length > 250;
            if (viaShortPath) target = Path.ChangeExtension(wemPath, ".wav");

            Directory.CreateDirectory(LongPath(Path.GetDirectoryName(Path.GetFullPath(wavPath))!));

            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(target);
            psi.ArgumentList.Add(wemPath);

            using var p = Process.Start(psi);
            if (p is null) return new Result(false, "could not start vgmstream");
            var err = p.StandardError.ReadToEnd();
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120000);

            // Exit code alone is not enough -- vgmstream returns 0 for some files it did not
            // write. The artefact is the claim: if there is no .wav, it failed whatever it said.
            if (!File.Exists(target) || new FileInfo(target).Length <= 44)
                return new Result(false, Trim(err) is { Length: > 0 } e ? e : Trim(outp));

            if (viaShortPath) File.Move(target, LongPath(wavPath), overwrite: true);
            return new Result(true, null);
        }
        catch (Exception e) { return new Result(false, e.Message); }
    }

    /// <summary>
    /// A path the Win32 APIs will accept past 260 characters.
    ///
    /// <c>\\?\</c> opts out of MAX_PATH, but only for a fully-qualified path using backslashes --
    /// hand it the mixed separators that come out of joining an output root to a game tree and it
    /// reports "syntax is incorrect" rather than anything about length.
    /// </summary>
    private static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + full;
    }

    /// <summary>
    /// Make a Wwise output path safe for the filesystem while KEEPING its folder structure --
    /// the folders are the point, so separators must survive and only illegal characters go.
    /// </summary>
    public static string Sanitise(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var parts = s.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Path.DirectorySeparatorChar,
            parts.Select(p => new string(p.Select(c => bad.Contains(c) ? '_' : c).ToArray())));
    }

    private static string Trim(string s) =>
        string.Join(" | ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3));
}

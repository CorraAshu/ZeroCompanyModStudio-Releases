namespace UEToolkit.Core;

/// <summary>
/// Writes a crash to disk before the process dies.
///
/// Exists because a crash nobody can reproduce is a crash nobody can fix. One game
/// crashed twice on first load and never again once its libraries were cached; by
/// then the only evidence was a memory of the order things happened in. Everything the toolkit
/// does headlessly ran clean, so there was nothing to attach a debugger to.
///
/// **`AccessViolationException` cannot be caught in .NET Core**, and this toolkit has already
/// been killed by one — CUE4Parse's package cache raced by two threads, 74 minutes into an
/// export. A `try/catch` would not have saved that run and will not save this one. What a
/// handler CAN do is say what was running when it happened, which is the difference between a
/// hypothesis and a fix.
/// </summary>
public static class CrashLog
{
    private static string _dir = "";
    private static readonly Lock Gate = new();

    /// <summary>What the app believes it is doing, stamped into any crash report.</summary>
    public static volatile string Activity = "starting";

    private static StreamWriter? _trail;

    /// <summary>
    /// Record what is about to happen, flushed to disk immediately.
    ///
    /// <see cref="Activity"/> and the handlers below only help when something THROWS. A native
    /// fault — a GPU driver reset, a bad free inside SkiaSharp, CUE4Parse's package cache raced by
    /// two threads — takes the process without unwinding, so no handler runs and nothing is
    /// written. That is the shape of a crash a user reported: they opened a
    /// mesh, rotated it, the app vanished, and there was no crash log because nothing was thrown.
    ///
    /// A breadcrumb flushed BEFORE each risky step survives that, because it is already on disk
    /// when the process dies. The last line in the file is the operation that killed it.
    ///
    /// Kept cheap on purpose: called at operation boundaries, never per frame.
    /// </summary>
    public static void Trail(string what)
    {
        try
        {
            Activity = what;
            if (_trail is null) return;
            lock (Gate)
            {
                _trail.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {what}");
            }
        }
        catch { /* a breadcrumb that throws is worse than no breadcrumb */ }
    }

    public static void Install(string dataDir)
    {
        _dir = dataDir;
        try
        {
            Directory.CreateDirectory(dataDir);
            // Truncated per session: the interesting run is the one that just died, and a file
            // that grows forever is one nobody opens.
            _trail = new StreamWriter(Path.Combine(dataDir, "breadcrumb.log"), append: false)
                { AutoFlush = true };
            _trail.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  session start");
        }
        catch { _trail = null; }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

        // A faulted background task that nobody awaited. BuildLibraries and the sound scan both
        // run detached, so this is the only place their failures would ever surface.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UnobservedTaskException", e.Exception, false);
            e.SetObserved();
        };

        // Not a crash, but the same evidence problem: a first-chance exception inside a native
        // library often precedes the fatal one by milliseconds.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("ProcessExit", null, false, quiet: true);
    }

    /// <summary>An exception that ended the run. Recorded before anything else is attempted.</summary>
    public static void Fatal(Exception e) => Write("fatal", e, terminating: true);

    public static void Write(string kind, Exception? e, bool terminating, bool quiet = false)
    {
        if (quiet && e is null) return;
        try
        {
            if (_dir.Length == 0) return;
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "crash.log");
            lock (Gate)
            {
                using var w = new StreamWriter(path, append: true);
                w.WriteLine(new string('-', 78));
                w.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {kind}" +
                            (terminating ? "  (TERMINATING)" : ""));
                w.WriteLine($"activity : {Activity}");
                w.WriteLine($"os       : {Environment.OSVersion}  cores={Environment.ProcessorCount}");
                w.WriteLine($"memory   : {Environment.WorkingSet / (1024 * 1024)} MB working set");
                if (e is null) w.WriteLine("(no exception object — likely a native fault)");
                else
                {
                    for (var x = e; x is not null; x = x.InnerException)
                    {
                        w.WriteLine($"{x.GetType().FullName}: {x.Message}");
                        w.WriteLine(x.StackTrace);
                    }
                }
            }
            if (!quiet) Console.Error.WriteLine($"[crash] {kind} during '{Activity}' — written to {path}");
        }
        catch { /* a logger that throws while logging a crash helps nobody */ }
    }
}

using Avalonia;

namespace ZeroCompanyModStudioAvalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if DEV
        // DEV: `--culture de-DE` runs the whole process as a PC with that regional format, so a
        // number typed into a box can be tried the way a German or French author types it. The
        // setting changes the default for every thread (the build runs on a worker) as well as
        // this one. Not shipped: stagecheck looks for the flag.
        var culture = Array.IndexOf(args, "--culture");
        if (culture >= 0 && culture + 1 < args.Length)
        {
            var ci = System.Globalization.CultureInfo.GetCultureInfo(args[culture + 1]);
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = ci;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = ci;
            System.Globalization.CultureInfo.CurrentCulture = ci;
            System.Globalization.CultureInfo.CurrentUICulture = ci;
            Console.WriteLine($"[culture] running as {ci.Name}: 0.5 prints as '{0.5.ToString(ci)}'");
        }

        // DEV: how viewport textures reach the GPU, so one binary draws it every way and a picture can be compared with itself.
        // `--cputex` decodes every texture to RGBA8 on the CPU, as before block-compressed textures were sent as blocks;
        // `--cpumips` sends the game's own mips but decoded; `--vtcheck` proves each virtual texture's blocks against the CPU decode.
        if (args.Contains("--vtcheck")) UEToolkit.App.Rendering.TextureCache.VtCheck = true;
        if (args.Contains("--cpumips")) UEToolkit.App.Rendering.TextureCache.CpuMips = true;
        if (args.Contains("--cputex")) UEToolkit.App.Rendering.TextureCache.DecodeOnCpu = true;
#endif

        // THE SAME BRANDING THE IMGUI STUDIO SETS, and it is not cosmetic: Branding.ModsFolder is
        // what ModLibrary.DefaultFolder is built from, so without it this application looks for
        // the author's mods in 'UEToolkit Mods' and finds an empty directory. That exact mistake
        // cost an hour today from the command line, where it reported four zeroes that read as a
        // clean bill of health.
        //
        // The DEFAULT only. The folder is configurable and the author's is on another drive
        // entirely; picking it up properly belongs with the folder chooser, not here.
        UEToolkit.Core.Branding.SingleGame = true;
        UEToolkit.Core.Branding.Product = "Zero Company Mod Studio";
        // The release version, and it has to match the csproj's <Version> and <InformationalVersion>:
        // publish.ps1 reads this line and those two elements and refuses to build if they differ.
        UEToolkit.Core.Branding.Version = "2.0.2";
        UEToolkit.Core.Branding.AuthorFirst = true;
        UEToolkit.Core.Branding.ModsFolder = "Zero Company Mods";

        // THE REST OF THE ImGui STUDIO'S BRANDING, which this Main used to leave at the toolkit's
        // blanks. Set HERE, before the add-on verbs below and before Avalonia exists, because the
        // update check derives its repository from Home and the credit reads Author the moment the
        // menu bar is drawn; a value set after either has been read is a value that was never set.
        //
        // The name on the credit, in the menu bar and in About. Public text carries this and not
        // the account the repository lives under.
        UEToolkit.Core.Branding.Author = "Clonetrooper163";
        // Where the releases are. UpdateCheck.RepoOf(Home) is the owner/repository the startup
        // check asks, and About's "Downloads and updates" opens it. It has to stay a github.com
        // address: anything else switches the update check off without a word.
        UEToolkit.Core.Branding.Home = "https://github.com/CorraAshu/ZeroCompanyModStudio-Releases";
        // The friendly page, for somebody who wants to READ about it. Not Home, on purpose: see
        // Branding.Landing. About's "Guides on the web" and the Handbook menu's fallback.
        UEToolkit.Core.Branding.Landing = "https://corraashu.github.io/ZeroCompanyModStudio-Releases/";
        // What the "the game has changed" screen and the log tell a person to do. The default says to
        // dump a new usmap, which nobody who downloaded this Studio can do; they can look for a newer
        // version, and the About menu is where that lives.
        UEToolkit.Core.Branding.StaleMappingsAdvice =
            "Look for a newer version of this Studio: the About menu's Downloads and updates " +
            "opens the page where new versions are published.";
        // Default export folder under Documents. The Studio's Extract window asks where to write
        // and prefills nothing, so this is for anything that does take the default (the toolkit's
        // AppState does, and this Studio does not reference it).
        UEToolkit.Core.Branding.ExportsFolder = "Zero Company Exports";

        // NO FORCED COLLECTION PER NANITE MESH. CUE4Parse ends every Nanite conversion with GC.Collect(), which is right
        // for FModel (one asset per click) and ruinous for anything that converts many: each is a full blocking gen-2
        // whose cost is the whole live heap. The Studio opens one part at a time but its "Dump game assets" and the
        // Level Editor's FBX export convert hundreds; the toolkit has run without it since the atlas scan. The page
        // references are dropped either way; only the timing of the collection is left to the collector.
        CUE4Parse_Conversion.Meshes.MeshConverter.AggressiveGarbageCollection = false;
        // The two guides, in the order somebody should meet them: the walkthrough first. File is
        // the copy that ships beside the executable (blank when this build does not carry it);
        // Url is the published page. GuideReader stays null: that is the ImGui Studio's WebView2
        // window, and this one opens a guide in whatever the machine opens .html with.
        var guides = Path.Combine(AppContext.BaseDirectory, "Guides");
        string GuideFile(string leaf)
            => File.Exists(Path.Combine(guides, leaf)) ? Path.Combine(guides, leaf) : "";
        UEToolkit.Core.Branding.Guides =
        [
            new UEToolkit.Core.Branding.Guide("Your First Mod - start here",
                GuideFile("first-mod.html"),
                UEToolkit.Core.Branding.Landing + "first-mod.html"),
            new UEToolkit.Core.Branding.Guide("The Complete Handbook",
                GuideFile("handbook.html"),
                UEToolkit.Core.Branding.Landing + "handbook.html"),
        ];

        // THE BLENDER ADD-ON'S TWO VERBS (--catalog, --import1) ARE ANSWERED HERE, BEFORE ANYTHING
        // OF AVALONIA EXISTS: no window, no application object, and the process exits with the
        // verb's own code. They are user-facing, so no "#if DEV". See AddonVerbs.
        if (AddonVerbs.Wants(args))
        {
            Environment.Exit(AddonVerbs.Run(args));
            return;
        }

#if !DEV
        // ZCMS_BACKGROUND=1 MEANS "NOBODY MAY BE INTERRUPTED", and this build cannot keep a window out of sight: the
        // background mode that does is a developer feature, compiled out of the release. So under that variable it opens
        // nothing at all - exactly what the Level Editor's release does, and what tools/unzipcheck.ps1 asserts of both
        // (a test run is the only thing that sets it; a person double-clicking the program never does). AFTER the add-on
        // verbs on purpose: the Blender add-on's --catalog and --import1 still run under the variable.
        if (Environment.GetEnvironmentVariable("ZCMS_BACKGROUND") == "1")
        {
            Environment.Exit(AddonVerbs.RefuseWindow());
            return;
        }
#endif

        RunStudio(args);
    }

    // Separate and never inlined, so the headless path above does not so much as resolve the
    // Avalonia types this method names.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunStudio(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Referenced by name from the csproj's Avalonia targets as well as from Main, which is why it
    // is public and separate.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // ASK FOR NATIVE DESKTOP GL FIRST.
            //
            // Avalonia on Windows defaults to ANGLE, which is GLES 3.0 over Direct3D 11 — and the
            // renderer linked into this project is desktop GL, #version 330 core. Those shaders
            // do not compile on an ES context: the viewport came up black and the log said
            // "VertexShader failed: ERROR: 0:1: 'core' : invalid version directive", which is
            // that and nothing else. WGL asks for the real thing; ANGLE and software stay behind
            // it as fallbacks, so a machine without it still starts.
            //
            // The Level Editor's Program.cs has carried this for the same reason since the day it
            // first hosted the renderer. Linking the renderer without also taking this is half a
            // port.
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Software,
                },
#if DEV
                // ZCMS_BACKGROUND=1: a menu's popup is its OWN native window, and BackgroundMode
                // only reaches Window - so an open menu was clamped onto the nearest monitor, in
                // the top-left corner of the author's screen, while its parent sat off-screen
                // (measured 2026-11-02: 268x109 at 0,0). Drawn in the main window's overlay layer
                // instead, it is part of the window PrintWindow photographs and is never on screen.
                OverlayPopups = BackgroundMode.On,
#endif
            })
            .LogToTrace();
}

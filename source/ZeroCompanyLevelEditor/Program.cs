using Avalonia;

namespace ZeroCompanyLevelEditor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // WHO THIS IS, before anything that reads it. The Mod Studio sets the same set in its own Main, and
        // for the same reasons: the update-free About window and the credit in the menu bar read Author the
        // moment they are drawn, and Branding.Version is the 2.0.0 both programs ship under (publish.ps1
        // reads the Studio's Program.cs and compares this project's two version elements against it).
        UEToolkit.Core.Branding.SingleGame = true;
        UEToolkit.Core.Branding.Product = "Zero Company Level Editor";
        UEToolkit.Core.Branding.Version = "2.0.2";
        UEToolkit.Core.Branding.AuthorFirst = true;
        // The folder both programs keep mod projects in by default - ONE name, so a level edit made here
        // and a mod made in the Studio sit in the same place.
        UEToolkit.Core.Branding.ModsFolder = "Zero Company Mods";
        // The name on the credit and in About. Public text carries this and not the account the
        // repository lives under.
        UEToolkit.Core.Branding.Author = "Clonetrooper163";
        // Where the releases are, and the friendly page: About's two buttons open them in the browser.
        // Nothing here ever ASKS the release page anything - the Mod Studio has the one update check.
        UEToolkit.Core.Branding.Home = "https://github.com/CorraAshu/ZeroCompanyModStudio-Releases";
        UEToolkit.Core.Branding.Landing = "https://corraashu.github.io/ZeroCompanyModStudio-Releases/";
        // What the "the game has changed" window tells a person to do. The default says to dump a new
        // usmap, which nobody who downloaded this can do; they can look for a newer version.
        UEToolkit.Core.Branding.StaleMappingsAdvice =
            "Look for a newer version: the About menu's Downloads and updates opens the page where " +
            "new versions are published.";

        // THE HEADLESS VERBS ARE ANSWERED HERE, BEFORE ANYTHING OF AVALONIA EXISTS: no window, no
        // application object, and the process exits with the verb's own code. User-facing, so no "#if DEV".
        if (LevelEditorVerbs.Wants(args))
        {
            Environment.Exit(LevelEditorVerbs.Run(args));
            return;
        }

#if !DEV
        // ZCMS_BACKGROUND=1 MEANS "NOBODY MAY BE INTERRUPTED", and this build cannot keep a window out of
        // sight: the background mode that does is a developer feature, compiled out of the release. So under
        // that variable it opens nothing at all. (A test run is the only thing that sets it; a person
        // double-clicking the program never does.)
        if (Environment.GetEnvironmentVariable("ZCMS_BACKGROUND") == "1")
        {
            Environment.Exit(LevelEditorVerbs.RefuseWindow(args));
            return;
        }
#endif

#if DEV
        // --withmods mounts the player's installed Mods beside the game, so the editor draws the
        // level as the GAME will see it rather than as it shipped.
        //
        // OFF BY DEFAULT, and a DEVELOPER FLAG in the release (decided for 2.0.0). Every
        // edit this app records is addressed against the PRISTINE bytes -- an editor that quietly showed a
        // modded level would be measuring its own output, which is the trap in
        // zero-company-authoring-reads-your-own-mod -- and a provider holding an installed mod's containers
        // open MAY make this program's OWN Build and install fail with "file in use" (ProviderFactory.IncludeMods
        // says so). That second reason is a "may": ModInstall.CanWrite opens with FileShare.ReadWrite on purpose,
        // so this program's own reading does not stop an install by itself, and whether InstallPlugin's delete and
        // overwrite of the mod's files fails with a provider holding them has not been measured. The first reason
        // alone decides it. A person who does not know it would be handed a flag that can mislead them. With it
        // asked for by name, a developer still has the one way to SEE whether a mod did what it claimed without
        // launching the game.
        UEToolkit.Core.ProviderFactory.IncludeMods = args.Contains("--withmods");
#endif

        // A LEVEL IS NOT ONE MESH. The renderer's per-mesh UV diagnostics walk the vertex array
        // twice more for numbers only the toolkit's single-mesh inspector prints, and this app
        // loads two thousand meshes, one of which has 1.4 million vertices.
        UEToolkit.App.Rendering.RenderMesh.MeasureUvs = false;

        // NO FORCED COLLECTION PER NANITE MESH. CUE4Parse ends every Nanite conversion with GC.Collect(): right for
        // FModel (one asset per click, memory back at once) and ruinous here, where a level is hundreds of them in a row
        // and each collection is a full blocking gen-2 whose cost is the whole live heap. Measured in this program
        // while SK_Anaxes_040 loaded, three quarters of the time was spent paused in collections
        // somebody asked for. The page references are dropped either way; only WHEN the memory is collected changes, and
        // the collector is better placed than a loop over meshes to choose that. The toolkit has done this since the
        // atlas scan (UEToolkit.App\Program.cs); the Level Editor and the Studio never did.
        CUE4Parse_Conversion.Meshes.MeshConverter.AggressiveGarbageCollection = false;

#if DEV
        // The memory flags (see MainWindow.MemDriven): --forcegc puts the forced collection back, --keeptextures turns the
        // release of a replaced map's textures off, --memreport starts the collection listener before anything loads.
        if (args.Contains("--forcegc")) CUE4Parse_Conversion.Meshes.MeshConverter.AggressiveGarbageCollection = true;
        if (args.Contains("--keeptextures")) Viewport.KeepTextures = true;
        // How textures reach the GPU so one binary draws every way: --cputex decodes every texture to RGBA8 as before the
        // block upload, --cpumips sends the game's own mips decoded, --vtcheck proves each virtual texture's blocks against the CPU decode.
        if (args.Contains("--vtcheck")) UEToolkit.App.Rendering.TextureCache.VtCheck = true;
        if (args.Contains("--cpumips")) UEToolkit.App.Rendering.TextureCache.CpuMips = true;
        if (args.Contains("--cputex")) UEToolkit.App.Rendering.TextureCache.DecodeOnCpu = true;
        if (args.Contains("--memreport")) MemReport.Start();
        var texcap = Array.IndexOf(args, "--texcap");
        if (texcap >= 0 && texcap + 1 < args.Length && int.TryParse(args[texcap + 1], out var capPixels)) Viewport.TextureCap = capPixels;
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // ASK FOR NATIVE DESKTOP GL FIRST.
            //
            // Avalonia on Windows defaults to ANGLE, which is GLES 3.0 over Direct3D 11 -- and
            // the toolkit's renderer is desktop GL, #version 330 core. Those shaders do not
            // compile on an ES context, so the default would mean porting every shader rather
            // than reusing the renderer. WGL asks for the real thing; ANGLE and software stay
            // behind it as fallbacks, so a machine without it still starts.
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[]
                {
                    Win32RenderingMode.Wgl,
                    Win32RenderingMode.AngleEgl,
                    Win32RenderingMode.Software,
                },
#if DEV
                // ZCMS_BACKGROUND=1: a menu's, drop-down's or tooltip's popup is its OWN native window, and
                // BackgroundMode only reaches Window - so an open menu was clamped onto the nearest monitor,
                // in the top-left corner of the author's screen, while its parent sat off-screen (measured
                // 2026-11-02 in the Studio: 268x109 at 0,0). Drawn in the main window's overlay layer
                // instead, it is part of the window PrintWindow photographs and is never on screen.
                OverlayPopups = ZeroCompanyModStudioAvalonia.BackgroundMode.On,
#endif
            })
            .LogToTrace();
}

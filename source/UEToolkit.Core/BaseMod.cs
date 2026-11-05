using CUE4Parse.FileProvider;

namespace UEToolkit.Core;

/// <summary>
/// The mods this tool SHIPS, that other people's mods depend on.
///
/// There is one so far -- <c>CustomWeaponClasses</c>, which adds the four custom Armoury shelves a
/// duplicated weapon is routed onto. It has to be ONE canonical copy: two authors each shipping
/// their own would define the same package names and the same gameplay tags, and whichever mounted
/// last would win. That is the whole reason it is a base mod rather than something the weapon
/// builder emits into each project.
///
/// WHAT IS BUNDLED IS THE PROJECT, NOT A BUILT PAK. A pak is tied to one game build -- the
/// containers, the registry and the mappings all are -- so a shipped pak would break on the first
/// patch, silently, in someone else's game. The project is a few hundred bytes of JSON and builds
/// against whatever the user actually has installed, which is the same guarantee every other mod
/// this tool makes gets.
/// </summary>
public static class BaseMod
{
    /// <summary>The mod that defines the custom weapon classes.</summary>
    public const string WeaponClasses = WeaponAuthor.BaseModName;

    /// <summary>Where the bundled projects sit beside the executable.</summary>
    public const string FolderName = "BaseMods";

    /// <summary>
    /// The bundled project file for one base mod, or null when this build does not carry it.
    ///
    /// Null is a real case rather than an error: the toolkit can be run from a checkout whose
    /// output folder predates the bundle, and a caller that treats "not bundled" as a crash makes
    /// every unrelated feature depend on this one.
    /// </summary>
    public static string? ProjectFile(string name = WeaponClasses)
    {
        var path = Path.Combine(AppContext.BaseDirectory, FolderName, name + ".json");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Whether anything mounted already defines the custom weapon classes.
    ///
    /// Asked of the PROVIDER rather than of the Mods folder, because what matters is whether the
    /// class exists to the game, and a class could in principle arrive from a container this tool
    /// did not install. Checked by the class part's own package name, which is the thing a routed
    /// weapon's tag has to match.
    /// </summary>
    public static bool ClassesPresent(IFileProvider provider) =>
        WeaponAuthor.MirroredClasses.All(w => provider.Files.Keys.Any(
            k => k.EndsWith($"/CPD_WeaponSpec_Custom_{w}.uasset", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Whether this mod is the tool's own base mod, by name.</summary>
    public static bool IsBaseMod(string modName) =>
        modName.Equals(WeaponClasses, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a project has anything that NEEDS the custom classes -- a weapon routed onto one.
    ///
    /// Read from the project rather than from the built result so it can be asked before a build,
    /// and by the same rule the build's own warning uses: the armoury part's class tag renamed to
    /// the mirrored one.
    /// </summary>
    public static bool NeedsWeaponClasses(ModProject mod) =>
        mod.Copies.Any(c => WeaponAuthor.IdentityOf(c.Id) is { } who &&
                            WeaponAuthor.CustomClassTag(who.Class) is { } custom &&
                            c.RenameNames.TryGetValue(WeaponAuthor.ClassTag(who.Class), out var to) &&
                            to.Equals(custom, StringComparison.Ordinal));

    /// <summary>What happened when a base mod was asked for.</summary>
    public enum Outcome { AlreadyThere, Installed, NotBundled, Failed }

    /// <summary>
    /// Make sure the custom weapon classes exist, building and installing them if they do not.
    ///
    /// Built against the mounted game like any other mod, and installed into its own plugin folder,
    /// so it shares no file with anybody -- including with the mod whose install triggered it.
    ///
    /// <paramref name="why"/> carries the reason for every outcome except AlreadyThere, including
    /// the successful one, because "the tool quietly installed a second mod" is something a person
    /// should be told rather than discover in their Mods folder.
    /// </summary>
    public static Outcome Ensure(IFileProvider provider, string paks, out string? why,
                                 Action<string>? log = null)
    {
        why = null;
        if (ClassesPresent(provider)) return Outcome.AlreadyThere;

        var project = ProjectFile();
        if (project is null)
        {
            why = $"this build does not carry the '{WeaponClasses}' project, so the custom weapon " +
                  "classes cannot be installed automatically";
            return Outcome.NotBundled;
        }

        try
        {
            var mod = ModProject.Load(project);

            // The registry has to be the GAME's. A mod pak ships an AssetRegistry.bin at the same
            // path and, being a _P patch, wins the lookup -- so building on "the" registry with any
            // mod installed stacks this one on top of a mod's tiny registry instead of the game's.
            var registry = ModBuild.PristineRegistry(provider, out _);
            if (registry is null)
            {
                why = "no unpatched AssetRegistry.bin is mounted, so the base mod cannot be built";
                return Outcome.Failed;
            }
            var registryFile = Path.Combine(Path.GetTempPath(), "zcms-base-registry.bin");
            File.WriteAllBytes(registryFile, registry);

            var outDir = Path.Combine(Path.GetTempPath(), "ZeroCompanyModStudioBuild", WeaponClasses);
            Directory.CreateDirectory(outDir);

            var built = ModBuild.Build(provider, mod, registryFile, outDir, mod.ContainerName, log);

            if (!ModInstall.CanWrite(paks, WeaponClasses, out var blockedBy))
            {
                why = $"the custom weapon classes are needed but something has " +
                      $"{Path.GetFileName(blockedBy ?? "their old copy")} open -- if the game is " +
                      "running, close it and install again";
                return Outcome.Failed;
            }

            // The CONTAINER name, not the mod name. ModInstall names the pak after what it is
            // given, and a container without the "_P" suffix is not a patch -- it mounts at base
            // priority, so the class parts land but the widget OVERRIDE loses to the game's own
            // copy. Passing the mod name here produced CustomWeaponClasses.pak beside seven other
            // mods that all correctly read <name>_P.pak, which is how it was spotted.
            var into = ModInstall.Install(built, paks, mod.ContainerName, log);
            why = $"installed '{WeaponClasses}', which defines the custom weapon shelves this mod's " +
                  $"weapons appear on, into {into}";
            return Outcome.Installed;
        }
        catch (Exception e)
        {
            why = $"the custom weapon classes could not be installed: {e.Message}";
            return Outcome.Failed;
        }
    }
}

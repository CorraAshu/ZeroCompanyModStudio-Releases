using Avalonia.Media.Imaging;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE MOD ITSELF, open as a tab.
///
/// Every other thing a project holds has had an editor for weeks — a part, a weapon, a
/// specialization, an ability's progression. The project CARRYING them had a read-only line
/// across the top of the window, and the only way to change what it was called, who made it or
/// where its assets were filed was to close the Studio and edit the JSON.
///
/// Three of these fields are SHIPPED. The title, the description and the author are written into
/// the mod's .uplugin as FriendlyName, Description and CreatedBy, which is what the game's own
/// plugin list shows a player — so the description nobody could edit went out empty on every mod
/// this tool has ever built.
///
/// The other two are how a mod stays out of another mod's way:
///
///   what it ships as   the container id, the pak's name and the folder the game reads it from
///                      are all derived from it, so two mods sharing one name cannot be installed
///                      together
///   its asset folder   two mods both taking the shared default file their parts at the same
///                      package paths, and a part id used by both is then a coin toss
///
/// The edit surface is <see cref="SheetTab"/>'s, as it is for everything else. What differs is
/// which cards it fills, and that a mod has no mesh, no tint, no slot and no icon of the kind the
/// game reads — the picture here is the shelf label, and is never shipped.
/// </summary>
public sealed class ModTab : SheetTab
{
    /// <summary>
    /// The project this tab edits, held so a save can refuse to write into a DIFFERENT one.
    ///
    /// Tabs outlive the mod that opened them — nothing closes the strip when a project is
    /// unloaded — so a details tab left open over a newly opened mod would otherwise write one
    /// mod's title into another's file.
    /// </summary>
    public ModProject Mod { get; }

    public ModTab(ModProject mod, string path)
    {
        Mod = mod;
        ProjectPath = path;
    }

    /// <summary>The project file, for the subtitle and for telling two open mods apart.</summary>
    public string ProjectPath { get; }

    public override string Title => "Mod details";

    public override string Subtitle =>
        Mod.DisplayName.Length > 0 ? Mod.DisplayName : Mod.Name;

    public override string Tooltip => ProjectPath;

    public override string Aside => System.IO.Path.GetFileName(ProjectPath);

    public override bool CanEdit => true;

    /// <summary>
    /// This IS the author's own work, all of it — there is no game asset underneath to compare
    /// it against, and no "the game's own" state to warn about.
    /// </summary>
    public override bool IsCopy => true;

    /// <summary>
    /// NEITHER A COPY NOR AN OVERRIDE. The pair the panel usually draws describes what happens to
    /// the GAME's own asset, and there is no game asset under a project's name and author — so
    /// the line says the one thing about these fields that is easy to get wrong instead.
    /// </summary>
    public override string EditBadge => "THIS PROJECT";

    public override string EditWarning =>
        "The name, the description and the author are shipped: they go into the plugin the game " +
        "reads, and are what its own mod list shows. The picture is yours alone — it is never " +
        "shipped and the game never sees it.";

    /// <summary>The picture, which is the mod's own and not a part's thumbnail.</summary>
    public override Bitmap? Icon => ModPicture;

    public override string Headline =>
        Mod.HasContent ? "" : "Nothing in it yet — it will not build until it holds something";

    /// <summary>
    /// The one thing here that silently breaks a mod.
    ///
    /// A folder outside the roots the game scans is not an error anywhere: the mod builds, it
    /// installs, it mounts, and its parts are never looked at. Said at the top because by the
    /// time somebody is hunting for it they have already been through the game's menus twice.
    /// </summary>
    public override string Problem => ModProject.FolderProblem(Mod.Folder) ?? "";

    /// <summary>Title and subtitle both follow the fields, so a save re-reads them.</summary>
    public void Renamed()
    {
        Raise(nameof(Subtitle));
        Raise(nameof(Headline));
        Raise(nameof(Problem));
    }

    /// <summary>
    /// WHAT THIS PROJECT ACTUALLY HOLDS, counted from the lists themselves.
    ///
    /// <see cref="ModProject.HasContent"/> carries a standing warning that every new list has to
    /// be added to it, and the same applies here — a project whose only content was a kind this
    /// sheet forgot would read as empty beside a build bar that was perfectly willing to build
    /// it. So the counts come from one array that names every list, and a missing one shows up
    /// as a disagreement between this sheet and the bar rather than as silence.
    /// </summary>
    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Project file", ProjectPath);
            Fact("Ships as", Mod.Name);
            Fact("Assets under", "/Game/Game/" + Mod.Folder.Replace('\\', '/').Trim('/'));
            Fact("Builds", Mod.ContainerName + ".pak");
            Fact("Author", Mod.Author.Length > 0 ? Mod.Author : "—");
            Fact("Version", Mod.Version.Length > 0 ? Mod.Version : "—");

            var counts = new (string What, int N)[]
            {
                ("clothing part", Mod.Parts.Count),
                ("changed game asset", Mod.Overrides.Count),
                ("duplicated asset", Mod.Copies.Count),
                ("specialization", Mod.Specs.Count),
                ("mesh replacement", Mod.Meshes.Count),
                ("icon bank", Mod.Icons.Count),
                ("enemy stat change", Mod.EnemyStats.Count),
                ("mission", Mod.Missions.Count),
            };

            var held = counts.Where(c => c.N > 0).ToList();
            blocks.Add(new SheetBand
            {
                Text = held.Count == 0 ? "What it holds — nothing yet"
                                       : $"What it holds ({held.Sum(c => c.N)})"
            });
            foreach (var c in held)
                blocks.Add(new SheetBullet { Text = c.N == 1 ? $"1 {c.What}" : $"{c.N} {c.What}s" });

            if (Mod.RecruitByScript)
                blocks.Add(new SheetBullet
                { Text = "ships a UE4SS recruit script — needs UE4SS installed to work" });

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }
        }
    }
}

using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE MOD'S OWN DETAILS: what it is called, who made it, and where it lands.
///
/// The last thing in this Studio with no editor. Everything a project HOLDS could be edited; the
/// project itself was a read-only line across the top of the window, and the old studio was no
/// better — it offered a title, an author, a version, a folder and a picture, and never the
/// description, which is shipped.
///
/// WHAT MAKES THIS DIFFERENT FROM EVERY OTHER SAVE HERE: two of these fields decide where files
/// go on somebody's disk. The name is the container id, the pak's file name and the folder the
/// game reads the mod out of; the asset folder decides whether the game ever scans the parts at
/// all. So both are checked against Core's own rules before anything is written, and the name is
/// refused outright while a copy is installed under the old one — a rename there does not move
/// the installed mod, it orphans it, and the next build quietly puts a second copy beside it.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>
    /// Open the details panel on the project that is open.
    ///
    /// One tab, not one per press: the details are a single thing and two tabs editing them would
    /// be two panels that disagree the moment one of them saves.
    /// </summary>
    public void OpenModDetails()
    {
        if (_mod is null) { Say("open a mod first — there are no details without one"); return; }

        if (Tabs.OfType<ModTab>().FirstOrDefault(t => ReferenceEquals(t.Mod, _mod)) is { } open)
        { Tab = open; return; }

        var tab = new ModTab(_mod, _modPath);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        FillModDetails(tab);
    }

    /// <summary>
    /// Put the project on the panel.
    ///
    /// EVERY FIELD IS READ OFF THE PROJECT, including the ones a fresh mod leaves empty. The
    /// recurring fault in this editor has been a panel showing a state nobody read back, and the
    /// cheapest guard against it is that there is no other source for anything drawn here.
    /// </summary>
    private void FillModDetails(ModTab tab)
    {
        var mod = tab.Mod;

        // A MOD HAS NONE OF THE REST. Absent rather than present and inert, which is the rule
        // every card in this panel follows — and declared as what it HAS, so a card added later
        // cannot arrive here uninvited.
        tab.ShowOnly(tab.SecIdentity, tab.SecMade, tab.SecPicture, tab.SecShips);

        tab.EditAuthor = mod.Author;
        tab.EditVersion = mod.Version;
        tab.EditPicture = mod.Image;
        tab.EditShipsAs = mod.Name;
        tab.EditFolder = mod.Folder;

        // RECRUIT BY SCRIPT IS OFFERED ONLY WHERE IT DOES ANYTHING. RecruitScript.Applies asks
        // whether this mod has characters on an enemy pawn at all; a tick that ships a script
        // with nothing to recruit is a setting somebody turns on and then cannot explain.
        var applies = RecruitScript.Applies(mod);
        tab.SecRecruit.Present = applies;
        tab.EditByScript = mod.RecruitByScript;
        tab.EditRecruitKey = mod.RecruitKey;
        tab.EditRepairKey = mod.RepairKey;
        if (applies) tab.RecruitBlurb = RecruitSays(mod);

        tab.SetModPicture(mod.Image.Length > 0 ? LoadPreview(mod.Image) : null);

        tab.BeginEdit(mod.DisplayName.Length > 0 ? mod.DisplayName : mod.Name,
                      mod.Description, "", "");

        Say($"{mod.Name}: its own details — the title, the description and the author are shipped " +
            "in the plugin the game reads");
    }

    /// <summary>What the two keys do in the Den, with this mod's own characters named.</summary>
    private static string RecruitSays(ModProject mod)
    {
        var who = string.Join(", ", RecruitScript.EntriesOf(mod).Select(e => e.Name));
        return $"In the Den, {RecruitScript.KeyOrDefault(mod.RecruitKey, RecruitScript.DefaultRecruitKey)} " +
               $"recruits the next of: {who}. " +
               $"{RecruitScript.KeyOrDefault(mod.RepairKey, RecruitScript.DefaultRepairKey)} " +
               "re-applies their definition and team if one spawns wrong. UE4SS is not installed " +
               "by this — the install log says whether it is there.";
    }

    /// <summary>
    /// Write the panel into the project.
    ///
    /// THE TWO REFUSALS ARE THE POINT. Everything else here is a string going into a field; the
    /// folder and the name reach outside the project file, and a save that wrote them blindly
    /// would produce a mod that builds, installs and never appears, or two copies of one mod in
    /// somebody's game. Each refusal keeps the OLD value, says what it saw, and leaves the box
    /// holding what was typed so it can be corrected rather than retyped.
    /// </summary>
    public bool SaveModDetails(ModTab tab)
    {
        var mod = tab.Mod;

        // A TAB OUTLIVES THE MOD THAT OPENED IT — nothing closes the strip when a project is
        // unloaded — so this would otherwise write one mod's title into another's file.
        if (!ReferenceEquals(_mod, mod))
        { Say("this details panel belongs to a project that is no longer open"); return false; }

        var wrote = new List<string>();

        var name = tab.EditName.Trim();
        if (name != mod.DisplayName) { mod.DisplayName = name; wrote.Add($"called “{name}”"); }

        var said = tab.EditDescription.Trim();
        if (said != mod.Description)
        {
            mod.Description = said;
            wrote.Add(said.Length > 0 ? "its own description" : "no description");
        }

        var author = tab.EditAuthor.Trim();
        if (author != mod.Author)
        { mod.Author = author; wrote.Add(author.Length > 0 ? $"by {author}" : "nobody credited"); }

        var version = tab.EditVersion.Trim();
        if (version != mod.Version) { mod.Version = version; wrote.Add($"v{version}"); }

        var picture = tab.EditPicture.Trim();
        if (picture != mod.Image)
        {
            mod.Image = picture;
            tab.SetModPicture(picture.Length > 0 ? LoadPreview(picture) : null);
            wrote.Add(picture.Length > 0 ? $"its card shows {picture}" : "its card picks a picture");
        }

        // THE ASSET FOLDER, asked of the project's own validator. The rule is the game's, out of
        // its DefaultGame.ini, and a second opinion written here is how two parts of one tool
        // come to disagree about whether a mod can work.
        var folder = tab.EditFolder.Trim();
        if (folder != mod.Folder)
        {
            if (ModProject.FolderProblem(folder) is { } why)
                Say($"{mod.Name}: the asset folder is unchanged — {why}");
            else { mod.Folder = folder; wrote.Add($"filed under /Game/Game/{folder}"); }
        }

        // WHAT IT SHIPS AS. Renaming this does not rename anything already in the game: the old
        // copy stays where it is, under its old name, and the next build installs a second one
        // beside it. Refused while that copy is there, with the one step that clears it.
        var ships = tab.EditShipsAs.Trim();
        if (ships.Length > 0 && !ships.Equals(mod.Name, StringComparison.Ordinal))
        {
            if (InstalledUnder(mod.ContainerName) is { } where)
                Say($"{mod.Name}: it is still installed at {where}. Renaming it here would leave " +
                    "that copy behind under the old name and install a second one beside it — " +
                    "take it back out of the game first (Project ▸ Take it back out of the game), " +
                    "then rename it.");
            else
            {
                mod.Name = ships;
                wrote.Add($"ships as {ships} ({mod.ContainerName}.pak)");
            }
        }

        // ONLY WHERE THE CARD IS. A mod with nothing on an enemy pawn never shows these, and a
        // save that wrote them anyway would put a setting into the project that the panel has no
        // way to show, let alone turn off — which is the same fault as a panel showing a state
        // nobody read back, pointed the other way.
        if (tab.SecRecruit.Present)
        {
            var byScript = tab.EditByScript;
            if (byScript != mod.RecruitByScript)
            {
                mod.RecruitByScript = byScript;
                wrote.Add(byScript ? "ships a recruit script" : "no recruit script");
            }

            var rk = tab.EditRecruitKey.Trim();
            if (rk != mod.RecruitKey) { mod.RecruitKey = rk; wrote.Add($"recruit on {rk}"); }

            var pk = tab.EditRepairKey.Trim();
            if (pk != mod.RepairKey) { mod.RepairKey = pk; wrote.Add($"repair on {pk}"); }
        }

        mod.Save();

        // THE BOXES GO BACK TO WHAT THE PROJECT HOLDS, which is not always what was typed: a
        // refused folder or a refused rename left the old value in the file, and a box still
        // showing the rejected text beside a log line saying it was not taken is the panel
        // describing a state the project does not have.
        tab.EditFolder = mod.Folder;
        tab.EditShipsAs = mod.Name;
        if (RecruitScript.Applies(mod)) tab.RecruitBlurb = RecruitSays(mod);

        tab.MarkSaved();
        tab.Renamed();

        Say($"{mod.Name}: its details saved — " +
            (wrote.Count > 0 ? string.Join(", ", wrote) : "as they were"));

        // THE WHOLE WINDOW FOLLOWS. The header reads DisplayName, the build bar says where it
        // will go by Name, and both are computed — neither hears about a change to the project
        // unless something raises them.
        AfterModChanged();
        AfterEditsChanged();
        return true;
    }

    /// <summary>
    /// Where a mod of this name is installed in the game, or null when none is.
    ///
    /// Asked of the installer rather than guessed at, and BOTH shapes, because a plugin mod lives
    /// in its own folder under Mods and a classic one has its containers beside the game's.
    /// </summary>
    /// <remarks>
    /// Takes the CONTAINER name, with its `_P`. PluginDirectory strips it to find the folder, and
    /// the legacy fallback compares against a name that still has it -- so the mod's own Name
    /// answers the first question and quietly fails the second.
    /// </remarks>
    private string? InstalledUnder(string modName)
    {
        try
        {
            var paks = InstallPaks();
            if (paks is null) return null;

            if (ModInstall.PluginDirectory(paks, modName) is { } plugin &&
                Directory.Exists(plugin)) return plugin;

            return ModInstall.Installed(paks)
                       .FirstOrDefault(p => p.Name.Equals(modName, StringComparison.OrdinalIgnoreCase))
                       ?.Root;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// A picture for the mod's card, taken into the project's own source folder.
    ///
    /// COPIED IN, not referenced where it lies — <see cref="ImportArt"/> carries the reason, and
    /// it is the difference between a project that survives being sent to somebody else and one
    /// that points at a folder on this machine's Desktop.
    /// </summary>
    public void SetModPicture(ModTab tab, string file)
    {
        if (_mod is null || !ReferenceEquals(_mod, tab.Mod)) return;

        var leaf = ImportArt(file, "Icons", out var why);
        if (leaf is null) { Say($"its picture: {why}"); return; }

        tab.EditPicture = leaf;
        tab.SetModPicture(LoadPreview(leaf));
        Say($"{_mod.Name}: its card will show {leaf} — press Save to record it");
    }

    /// <summary>
    /// One of this mod's own images, chosen by name.
    ///
    /// RESOLVED THROUGH ModBuild.FindImage rather than taken as a path: the menu offers bare
    /// leaf names, which is what the project stores, and a leaf is not a file anything can open.
    /// The same step UsePartIcon takes, for the same reason.
    /// </summary>
    public void UseModPicture(ModTab tab, string leaf)
    {
        if (_mod is null || !ReferenceEquals(_mod, tab.Mod)) return;

        if (ModBuild.FindImage(leaf, _modPath) is not { } file || !File.Exists(file))
        { Say($"cannot find {leaf} in this mod's own images"); return; }

        SetModPicture(tab, file);
    }

    /// <summary>Back to the card picking one itself. The file stays in ModSource.</summary>
    public void ClearModPicture(ModTab tab)
    {
        tab.EditPicture = "";
        tab.SetModPicture(null);
        Say("its card will pick a picture itself — press Save to record it");
    }
}

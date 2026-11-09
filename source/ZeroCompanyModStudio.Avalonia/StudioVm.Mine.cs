using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A GAME ASSET'S TAB SHOWS WHAT THE OPEN MOD MAKES OF IT.
///
/// People imported a texture or a model on its own tab in the Data Explorer, the log said the mod now used their file,
/// and the tab went on showing the game's -- and showed the game's again when reopened, which read as the log lying
/// ("Import does nothing"). The project had it all along; nothing on screen did. So a tab on a game texture or a game
/// mesh that the open mod replaces shows the replacement, titled "(your import)": on open, straight after Import, and
/// back to the game's own after a Revert. The game's asset stays loaded behind it for Export and the properties.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>The override the open mod holds on the game package behind this file path, if any.</summary>
    private ModOverride? OverrideOn(string filePath)
    {
        if (_mod is null) return null;
        var package = PropertyEdit.PackageForFile(filePath);
        return package is null ? null : _mod.Overrides.FirstOrDefault(
            o => o.Target.Equals(package, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A texture tab on a GAME texture: the author's image when the mod replaces it, else the game's own.</summary>
    private void ShowMineOn(TextureTab tab)
    {
        // A tab opened ON the author's own file (a texture row's View) has no game texture behind it.
        if (File.Exists(tab.FilePath)) return;

        var image = OverrideOn(tab.FilePath)?.Image is { Length: > 0 } leaf ? ModBuild.FindImage(leaf, _modPath) : null;
        if (image is not null && File.Exists(image))
        {
            if (tab.Mine is null)
                Say($"{tab.Title}: showing your {System.IO.Path.GetFileName(image)} — the game's own is untouched until you build");
            tab.ShowMine(image);
        }
        else tab.ShowGame();
    }

    /// <summary>A mesh tab on a GAME mesh: the author's FBX when the mod replaces it, else the game's own.
    /// <paramref name="redraw"/> draws the import again even when it is the file already shown -- its placement or its
    /// textures changed.</summary>
    private async Task ShowMineOn(MeshTab tab, bool redraw = false)
    {
        // ONLY A TAB OPENED ON THE GAME MESH ITSELF. A mesh row's View tab carries a key and shows what its row says --
        // a part copy's import over a mesh the mod does not override, which this would put back to the game's.
        if (tab.MeshKey.Length > 0) return;

        var package = PropertyEdit.PackageForFile(tab.FilePath);
        var leaf = OverrideOn(tab.FilePath)?.MeshFile ?? "";
        var file = leaf.Length > 0 ? ModBuild.FindImage(leaf, _modPath) : null;

        if (package is not null && file is not null && File.Exists(file))
        {
            var same = string.Equals(tab.Mine, file, StringComparison.OrdinalIgnoreCase);
            if (same && !redraw) return;
            tab.MarkMine(file);
            if (!same)
                Say($"{Leaf(package)}: showing your {System.IO.Path.GetFileName(file)} — the game's own is untouched until you build");
            try { await ShowFbxOn(tab, package, leaf, file, Leaf(package)); }
            catch (Exception e)
            {
                // NOT LEFT BLANK UNDER A TITLE SAYING "your import": the game's mesh goes back up, and the next refresh
                // tries the import again because Mine no longer says it is shown.
                Say($"{Leaf(package)}: could not show your import ({e.Message}) — showing the game's own");
                tab.MarkMine(null);
                if (tab.Asset is { } game) tab.View.Show(game);
            }
        }
        else if (tab.Mine is not null)
        {
            tab.MarkMine(null);
            if (tab.Asset is { } game) tab.View.Show(game);
        }
    }

    /// <summary>
    /// Every open game texture and game mesh tab, after the project changed -- from whichever tab changed it. Each
    /// does nothing when what it shows is already right, so this is cheap to call on every change.
    /// </summary>
    private void RefreshMineTabs()
    {
        foreach (var tab in Tabs.ToList())
        {
            try
            {
                if (tab is TextureTab tex) ShowMineOn(tex);
                else if (tab is MeshTab mesh) _ = ShowMineOn(mesh);
            }
            catch (Exception e) { Say($"{tab.Title}: could not show your import ({e.Message})"); }
        }
    }
}

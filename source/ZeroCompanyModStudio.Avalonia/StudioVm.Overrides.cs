using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// AN OVERRIDE OPENS SOMETHING.
///
/// Every override listed in the mod as a row whose click said "it has no editor here yet" -- which is how Mass Deffect
/// lost the way back to the Runa mesh he had imported over the game's SK_MoiraVeigl, and found nowhere to put her
/// textures. Two kinds now open: an override carrying a MESH opens <see cref="OverrideTab"/> (the mesh copy's screen,
/// writing to the override), and one carrying only an IMAGE opens its texture tab on the import.
/// </summary>
public sealed partial class StudioVm
{
    /// <summary>The override this tab edits, as the project holds it now -- null once a Revert took the last of it out.</summary>
    private ModOverride? OverrideOf(OverrideTab tab) => _mod?.Overrides.FirstOrDefault(
        o => o.Target.Equals(tab.Target, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether an override is about a SKELETAL MESH: its target is one, and it carries an import or names materials for
    /// its slots (what is left of one after its import is reverted).
    ///
    /// THE TARGET DECIDES, not the fields. A game PART's override carries MeshFile too -- the part editor's Save writes
    /// it -- and routing that here opened a mesh screen on a data asset, with no textures and a fit card measuring a
    /// part. A part override keeps the route it had.
    /// </summary>
    private static bool IsMeshOverride(ModOverride ov) =>
        (ov.MeshFile.Length > 0 || ov.HasMaterials) &&
        AssetIndex.Classify(ContainerPathOf(ov.Target)) == AssetKind.SkeletalMesh;

    /// <summary>
    /// Open the editor on a game mesh this mod rewrites: its mesh row, the fit card, and every texture its materials
    /// wear.
    /// </summary>
    public async Task EditOverride(ModOverride ov)
    {
        if (_mod is null) { Say("open a mod first"); return; }

        if (Tabs.OfType<OverrideTab>()
                .FirstOrDefault(t => t.Target.Equals(ov.Target, StringComparison.OrdinalIgnoreCase)) is { } open)
        { Tab = open; return; }

        var tab = new OverrideTab(ov.Target);
        Tabs.Add(tab);
        Tab = tab;
        Raise(nameof(AnyTabs));

        // GEOMETRY AND PAINT, and nothing else -- as on a mesh copy. The textures card turns itself on once the
        // materials have been read.
        tab.ShowOnly(tab.SecMeshes);
        tab.Find = () => OverrideOf(tab);
        RefillOverride(tab);
        // THE SIZE RULE sits on the meshes card, so it is read from the override before the edit starts -- or the box
        // opens unticked over an override that allows any size, and the first Save turns it off.
        tab.EditAnySize = ov.MeshAnySize;
        tab.BeginEdit("", "", ov.MeshFile, "");
        NoteMod(tab, null);
        Say($"editing your changes to the game's {Leaf(ov.Target)} — everything that wears it changes");

        await FillOverridePaintAsync(tab);
    }

    /// <summary>
    /// The paint surface: every texture the target's materials wear, each importing the way a game texture's own Import
    /// does. <see cref="FillPaintAsync"/> in its OVERRIDE mode -- no paint holder, rows reading the mod's texture
    /// overrides -- which is the mode a game part already uses, for the same reason: the build repaints an override's
    /// textures in place, and refuses textures on an override's own slots.
    /// </summary>
    private async Task FillOverridePaintAsync(OverrideTab tab)
    {
        if (_provider is null)
        {
            tab.SetTextureRows([]);
            tab.SetMaterialGroups([], canAdd: false);
            return;
        }

        await FillPaintAsync(tab, [(tab.Target, null, "")], copyMode: false);
    }

    /// <summary>Read the mesh row, the sheet and the fit card back from the project after the geometry changed.</summary>
    public void RefillOverride(OverrideTab tab)
    {
        tab.SetMeshRows([new SheetTab.MeshRow("this mesh", tab.Target, OverrideOf(tab)?.MeshFile ?? "", "")]);
        tab.Reread();
        FillFit(tab);
    }

    /// <summary>
    /// EVERY OPEN OVERRIDE TAB, brought up to date with the project after something changed it from ANOTHER tab -- a
    /// game mesh's own Import, a part's Revert, the Data Explorer's. Only a tab whose file actually moved is refilled:
    /// the fit card re-measures the whole FBX, which is not a cost to pay on every change to the project.
    /// </summary>
    private void RefreshOverrideTabs()
    {
        foreach (var tab in Tabs.OfType<OverrideTab>())
        {
            var file = OverrideOf(tab)?.MeshFile ?? "";
            if (!string.Equals(tab.MeshRows.FirstOrDefault()?.Replacement ?? "", file, StringComparison.OrdinalIgnoreCase))
                RefillOverride(tab);
            else tab.Reread();
        }
    }

    /// <summary>
    /// SAVE on a mesh override: the one thing its meshes card holds that is not written as it happens -- the size rule,
    /// "let a replacement be a very different size". The import, the fit, the paint and the materials are all on disk
    /// already, as everywhere else on this screen.
    /// </summary>
    public void SaveOverrideEdit(OverrideTab tab)
    {
        if (_mod is null) return;

        if (OverrideOf(tab) is not { } ov)
        {
            // NOTHING TO ALLOW: the rule qualifies an import, and there is none. Saying so beats inventing an override
            // that asks the build for nothing.
            if (tab.EditAnySize) Say($"{tab.Title}: import a mesh first — the size rule is about an import");
            tab.EditAnySize = false;
            tab.BeginEdit("", "", "", "");
            return;
        }

        ov.MeshAnySize = tab.EditAnySize;
        _mod.Save();
        tab.BeginEdit("", "", ov.MeshFile, "");
        Say(ov.MeshAnySize
            ? $"{tab.Title}: your mesh may be a very different size from the game's — the build will not refuse it for that"
            : $"{tab.Title}: the build refuses a mesh far bigger or smaller than the game's again");
        AfterEditsChanged();
    }

    /// <summary>
    /// Put the game's own geometry back under an override.
    ///
    /// THE IMPORT GOES, the override stays while it still says anything -- a slot naming a material, say. An override
    /// left saying nothing is taken out of the project, as everywhere else: it would ship a byte-for-byte rewrite of a
    /// game package. The imported file stays in ModSource, as on every other Revert.
    /// </summary>
    public void RevertOverrideMeshRow(OverrideTab tab, SheetTab.MeshRow row)
    {
        if (_mod is null) return;

        if (OverrideOf(tab) is not { MeshFile.Length: > 0 } ov)
        { Say($"{row.GameLeaf}: this mod already leaves the game's geometry alone"); return; }

        var was = ov.MeshFile;
        ov.MeshFile = "";
        if (!ov.HasEdit && ov.Icon.Length == 0) _mod.Overrides.Remove(ov);
        _mod.Save();

        row.Replacement = "";
        row.Mark();
        RefillOverride(tab);
        BuildModifiedSet();
        _artVersion++;
        Say($"{row.GameLeaf}: back to the game's geometry — {was} is still in ModSource");
        AfterEditsChanged();
    }

    /// <summary>
    /// An override carrying only an image opens the IMPORT in the texture viewer -- what Export, the channel split and
    /// the zoom are for -- rather than a sentence saying there is nothing to open.
    /// </summary>
    private void OpenTextureOverride(ModOverride ov)
    {
        var file = ModBuild.FindImage(ov.Image, _modPath);
        if (file is null || !File.Exists(file))
        { Say($"{Leaf(ov.Target)}: cannot find your image '{ov.Image}' in this mod's ModSource"); return; }

        if (Tabs.OfType<TextureTab>().FirstOrDefault(t => t.FilePath == file) is { } open)
        { Tab = open; return; }

        var tex = new TextureTab($"{Leaf(ov.Target)}  (your import)", file, "Texture2D");
        tex.View.Trace = Say;
        Tabs.Add(tex);
        Tab = tex;
        Raise(nameof(AnyTabs));
        tex.LoadFile(file);
    }
}

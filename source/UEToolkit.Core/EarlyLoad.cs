namespace UEToolkit.Core;

/// <summary>
/// GAME FILES THE GAME LOADS BEFORE IT MOUNTS THE MODS FOLDER -- and so files a mod's plugin
/// container can never change.
///
/// The mod runtime mounts Mods/&lt;Plugin&gt;/ when the game instance starts. Anything already loaded
/// by then stays the game's own copy for the whole session: the override is mounted, and never read.
/// Content/Paks/~mods is mounted by the ENGINE's own pak scan, before anything loads, so an override
/// of one of these files is built into a second container (<see cref="ContainerName"/>) and installed
/// there instead. New parts, copies and the asset registry stay in the plugin.
///
/// THE RULE: an entry needs in-game proof that a Mods-folder override of it is ignored, and carries
/// that proof here. Nothing is added on suspicion. A wrong entry costs only an unneeded early file;
/// a missing one fails exactly as the ATTACHMENTS category did before this existed.
/// </summary>
public static class EarlyLoad
{
    private static readonly Dictionary<string, string> Proven = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/Game/Game/Customizations/Characters/Humanoid/Outfit/CPD_H_Outfit"] =
            "2026-10-11, UE4SS SlotCheck probe on a new character: with the override in Mods/SlotProof the " +
            "root in memory had 6 entries ('Tops') while the same mod's parts loaded; moved to ~mods it had 7 " +
            "('TOPS MOD', 'ATTACHMENTS') and the ATTACHMENTS button appeared in game.",

        // The four category containers and the Headwear Blueprint, measured 2026-10-31 with the
        // EarlyProbe test mod (a label suffix on each package's style slot). Read on a NEW clone: from
        // Mods/EarlyProbe all five still showed plain "STYLE"; the same files moved by hand into
        // Content/Paks/~mods (the positive control) showed the suffix. So each loads before Mods mounts.
        ["/Game/Game/Customizations/Characters/Humanoid/Outfit/CPD_H_Outfit_Torso"] =
            "2026-10-31, EarlyProbe label 'STYLE TORSO' ignored from Mods (plain 'STYLE' shown), shown from " +
            "~mods (positive control), on a new clone, build 199049.",
        ["/Game/Game/Customizations/Characters/Humanoid/Outfit/CPD_H_Outfit_Arms"] =
            "2026-10-31, EarlyProbe label 'STYLE ARMS' ignored from Mods (plain 'STYLE' shown), shown from " +
            "~mods (positive control), on a new clone, build 199049.",
        ["/Game/Game/Customizations/Characters/Humanoid/Outfit/CPD_H_Outfit_Legs"] =
            "2026-10-31, EarlyProbe label 'STYLE LEGS' ignored from Mods (plain 'STYLE' shown), shown from " +
            "~mods (positive control), on a new clone, build 199049.",
        ["/Game/Game/Customizations/Characters/Humanoid/Outfit/CPD_H_Outfit_Boots"] =
            "2026-10-31, EarlyProbe label 'STYLE BOOTS' ignored from Mods (plain 'STYLE' shown), shown from " +
            "~mods (positive control), on a new clone, build 199049.",
        ["/Game/Game/Customizations/Characters/Humanoid/Outfit/BP_CPD_H_Outfit_Helmet_Base"] =
            "2026-10-31, EarlyProbe label 'STYLE HELMET' ignored from Mods (plain 'STYLE' shown), shown from " +
            "~mods (positive control), on a new clone, build 199049.",

        // NOT LISTED, AND UNMEASURED: /Game/Game/Customizations/Characters/Humanoid/Outfit/CPD_H_Outfit_Helmet
        // (the Headwear container itself). Its slot exports serialise no properties -- the labels are
        // inherited from BP_CPD_H_Outfit_Helmet_Base above -- so a values edit cannot probe it, and
        // nothing has shown whether it loads early. Do not add it on suspicion; the rule above wants proof.
    };

    private const string Suffix = "_Early_P";

    public static IReadOnlyCollection<string> Packages => Proven.Keys;
    public static bool IsEarly(string packageName) => Proven.ContainsKey(packageName);
    public static string? Evidence(string packageName) => Proven.TryGetValue(packageName, out var e) ? e : null;

    /// <summary>"SlotProof_P" -> "SlotProof_Early_P": its own name, so its container id differs from the plugin's.</summary>
    public static string ContainerName(string containerName) => ModInstall.PluginName(containerName) + Suffix;

    /// <summary>
    /// Whether this is an early half: a bare container name ("SlotProof_Early_P") or a path to one of
    /// its three files ("...\~mods\SlotProof_Early_P.utoc").
    /// </summary>
    public static bool IsEarlyContainer(string nameOrLeaf) =>
        Stem(nameOrLeaf).EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"SlotProof_Early_P" -> "SlotProof". Only meaningful when <see cref="IsEarlyContainer"/> is true.</summary>
    public static string PluginOf(string earlyContainer)
    {
        var leaf = Stem(earlyContainer);
        return leaf[..^Suffix.Length];
    }

    private static readonly string[] ContainerExtensions = [".pak", ".utoc", ".ucas"];

    /// <summary>
    /// A container's own name: the leaf of a path, with a container extension taken off and NOTHING
    /// ELSE.
    ///
    /// Not Path.GetFileNameWithoutExtension. These are often BARE container names, and a mod's name
    /// may carry dots ("Sith Assassin v1.2" becomes "Sith_Assassin_v1.2_P"); that call reads the last
    /// dot of a bare name as an extension. Measured: IsEarlyContainer("Foo.Bar_Early_P") was false
    /// while ("Foo.Bar_Early_P.utoc") was true, so the reserved-name refusal in
    /// <see cref="ModProject.Validate"/> had a hole, and PluginOf("Sith_Assassin_v1.2_Early_P")
    /// returned "Sith_Ass". Only the three files of a container have an extension to take off.
    /// </summary>
    private static string Stem(string nameOrLeaf)
    {
        var leaf = Path.GetFileName(nameOrLeaf);
        foreach (var extension in ContainerExtensions)
            if (leaf.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return leaf[..^extension.Length];
        return leaf;
    }

    /// <summary>The one file in the early container's .pak: what it is and where the rest lives.</summary>
    public static string Readme(string plugin, IEnumerable<string> packages) =>
        $"Made by the Zero Company Mod Studio for the mod '{plugin}'.\r\n" +
        "These files change game files the game loads before it reads the Mods folder:\r\n" +
        string.Concat(packages.Select(p => $"    {p}\r\n")) +
        $"so they live in SWZeroCompany\\Content\\Paks\\~mods. The rest of the mod is in SWZeroCompany\\Mods\\{plugin}.\r\n" +
        "Uninstall both together.\r\n";
}

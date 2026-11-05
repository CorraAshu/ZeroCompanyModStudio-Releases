using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// THE WHOLE LOOK OF THE APP, AS DATA.
///
/// Every colour in MainWindow.axaml is a {DynamicResource} key, and a theme is nothing but a
/// table of those keys. Swapping the table restyles the app -- at runtime, with no rebuild and
/// without touching a single line of layout or behaviour. That is the thing ImGui could not do:
/// there the layout IS the code, so "change how it looks" meant editing the code that draws it.
///
/// Adding a theme = adding one entry to <see cref="All"/>. Nothing else changes.
/// </summary>
public static class AppTheme
{
    public static readonly Dictionary<string, Dictionary<string, string>> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dark"] = new()
        {
            ["Ground"] = "#12151A", ["Chrome"] = "#171B21", ["Panel"] = "#191D24", ["Raised"] = "#20252E",
            ["Line"] = "#2B323C", ["RowLine"] = "#22272F", ["SelBg"] = "#2C3440", ["SelBgHover"] = "#333C4A",
            ["Subtle"] = "#16191F", ["ViewportBg"] = "#141820",
            ["Fore"] = "#DCE3EC", ["Muted"] = "#828D9C", ["Faint"] = "#5D6775",
            ["Accent"] = "#E8A33D", ["OnAccent"] = "#1A1408", ["AccentBg"] = "#1C1710",
            ["AccentLine"] = "#4A3520", ["AccentText"] = "#C9A47A", ["AccentRule"] = "#3A2C1C",
            ["NoteBg"] = "#251C14",
            ["Edited"] = "#49B9A3", ["EditedBg"] = "#1E2A28", ["EditedLine"] = "#2E4B45", ["InfoBg"] = "#141C1B",
            ["Danger"] = "#C9564B", ["DangerText"] = "#B3877F", ["Warn"] = "#D8794C",
            ["AxisX"] = "#C96B5E", ["AxisY"] = "#7FB069", ["AxisZ"] = "#5B9BD5",
            ["LayerCover"] = "#49B9A3", ["LayerSpawns"] = "#E8A33D", ["LayerVolumes"] = "#9B7BD4",
            ["LayerObjectives"] = "#5B9BD5", ["LayerEncounter"] = "#D8794C", ["LayerProps"] = "#6E7887",
        },

        ["Light"] = new()
        {
            ["Ground"] = "#EFF1F4", ["Chrome"] = "#FFFFFF", ["Panel"] = "#FFFFFF", ["Raised"] = "#E9ECF0",
            ["Line"] = "#D7DCE3", ["RowLine"] = "#E6E9ED", ["SelBg"] = "#DCE4F0", ["SelBgHover"] = "#CFD9E8",
            ["Subtle"] = "#F6F7F9", ["ViewportBg"] = "#E8EAEE",
            ["Fore"] = "#1B2129", ["Muted"] = "#56606E", ["Faint"] = "#8792A0",
            ["Accent"] = "#A8620A", ["OnAccent"] = "#FFFFFF", ["AccentBg"] = "#FBF2E3",
            ["AccentLine"] = "#E3CBA3", ["AccentText"] = "#7A4708", ["AccentRule"] = "#EADCC2",
            ["NoteBg"] = "#FBF2E3",
            ["Edited"] = "#0C7D6D", ["EditedBg"] = "#E6F5F1", ["EditedLine"] = "#A6D6CC", ["InfoBg"] = "#EFF8F6",
            ["Danger"] = "#A93027", ["DangerText"] = "#8C2F27", ["Warn"] = "#B4602C",
            ["AxisX"] = "#A8433A", ["AxisY"] = "#4C7A3A", ["AxisZ"] = "#2C6BA3",
            ["LayerCover"] = "#0C7D6D", ["LayerSpawns"] = "#A8620A", ["LayerVolumes"] = "#6644A8",
            ["LayerObjectives"] = "#2C6BA3", ["LayerEncounter"] = "#A8552C", ["LayerProps"] = "#6B7482",
        },

        ["Holotable"] = new()
        {
            ["Ground"] = "#0A1018", ["Chrome"] = "#0E1620", ["Panel"] = "#101A25", ["Raised"] = "#16232F",
            ["Line"] = "#1E3244", ["RowLine"] = "#17252F", ["SelBg"] = "#1B3444", ["SelBgHover"] = "#224055",
            ["Subtle"] = "#0D141C", ["ViewportBg"] = "#0B121A",
            ["Fore"] = "#CFE6F2", ["Muted"] = "#7A9AAE", ["Faint"] = "#53707F",
            ["Accent"] = "#4FC3E8", ["OnAccent"] = "#04131A", ["AccentBg"] = "#0C1D26",
            ["AccentLine"] = "#1E4C5E", ["AccentText"] = "#8FD4EC", ["AccentRule"] = "#163944",
            ["NoteBg"] = "#0C1D26",
            ["Edited"] = "#67D9A8", ["EditedBg"] = "#0D2119", ["EditedLine"] = "#1E4E3B", ["InfoBg"] = "#0B1A16",
            ["Danger"] = "#E8705F", ["DangerText"] = "#C99187", ["Warn"] = "#E8A36C",
            ["AxisX"] = "#E07A6C", ["AxisY"] = "#8FCB6E", ["AxisZ"] = "#6FB6E8",
            ["LayerCover"] = "#67D9A8", ["LayerSpawns"] = "#4FC3E8", ["LayerVolumes"] = "#A98CE8",
            ["LayerObjectives"] = "#6FB6E8", ["LayerEncounter"] = "#E8A36C", ["LayerProps"] = "#6B8394",
        },
    };

    /// <summary>Themes whose base is light, so Fluent's own controls flip with them.</summary>
    private static readonly HashSet<string> LightThemes = new(StringComparer.OrdinalIgnoreCase) { "Light" };

    public static string Current { get; private set; } = "Dark";

    public static void Apply(string name)
    {
        if (!All.TryGetValue(name, out var table)) return;
        Current = name;

        var app = Application.Current!;
        foreach (var (key, hex) in table)
        {
            var colour = Color.Parse(hex);
            // Mutate the brush in place where one already exists: anything already bound to it
            // -- including the layer dots handed to the tree as objects -- repaints itself.
            if (app.Resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
                brush.Color = colour;
            else
                app.Resources[key] = new SolidColorBrush(colour);
        }

        app.RequestedThemeVariant = LightThemes.Contains(name) ? ThemeVariant.Light : ThemeVariant.Dark;
        Layers.Refresh(app);
    }
}

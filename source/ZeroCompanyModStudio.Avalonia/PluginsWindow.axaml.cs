using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// Add-ons and presets: the Blender add-on's installer, the Substance Painter export preset and the
/// 3ds Max retarget script - the ImGui Studio's Plugins window (<c>UEToolkit.App\PluginsWindow.cs</c>),
/// in its words, with the logic where it already lived: <see cref="BlenderPlugin"/>,
/// <see cref="SubstancePreset"/> and <see cref="MaxRetarget"/>.
///
/// Every write goes through <see cref="StudioVm.InstallAddon"/> / <see cref="StudioVm.RemoveAddon"/>,
/// which apply the one rule that keeps a test away from a real Blender (see
/// <c>StudioVm.AddonWriteAllowed</c>). The buttons' work is in methods of their own so a driver flag
/// can press them: the file dialogs cannot be driven, and a window whose only test is a person
/// opening it is a window whose writes were never witnessed.
/// </summary>
public partial class PluginsWindow : Window
{
    private readonly StudioVm? _vm;
    private List<BlenderPlugin.Install> _found = [];
    private string _justInstalled = "";

    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#FBBF24"));

    public PluginsWindow() : this(null) { }

    public PluginsWindow(StudioVm? vm)
    {
        AvaloniaXamlLoader.Load(this);
        _vm = vm;

        // A section is ABSENT when this build does not carry what it offers, said in red, rather
        // than a button that opens onto nothing.
        var preset = SubstancePreset.Source() is not null;
        this.FindControl<TextBlock>("SubstanceMissing")!.IsVisible = !preset;
        this.FindControl<Button>("SubstanceButton")!.IsVisible = preset;
        var max = MaxRetarget.Source() is not null;
        this.FindControl<TextBlock>("MaxMissing")!.IsVisible = !max;
        this.FindControl<Button>("MaxButton")!.IsVisible = max;

        ToolTip.SetTip(this.FindControl<Button>("SubstanceButton")!, string.Join(Environment.NewLine,
            "Copies one file where you choose.",
            "",
            "Painter finds presets in its own shelf, under Documents. If that folder exists on this machine the picker opens there; otherwise put it wherever you keep your presets and point Painter at it.",
            "",
            "In Painter it appears in the preset list on the export window, named \"Zero Company Export\"."));
        ToolTip.SetTip(this.FindControl<Button>("MaxButton")!, string.Join(Environment.NewLine,
            "Copies one file where you choose.",
            "",
            "The picker opens at your 3ds Max user scripts folder when this machine has one. It deliberately does NOT offer the folder inside Program Files: Max reads both, and a stale copy in the install folder shadowed a corrected one during this script's own development, which looks exactly like a fix that did not work.",
            "",
            "In Max: Scripting > Run Script, and pick the file. It prints its own version, so an old copy says so."));

        // HEIGHT TO FIT, up to what the screen can hold: the window grows when the "one more step"
        // and the saved-to notes appear, and scrolls (instead of running off the screen) when it
        // cannot. The limit is read once the window is on a screen, which is why it is set here.
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
            if (screen is not null)
                MaxHeight = Math.Max(420, screen.WorkingArea.Height / screen.Scaling * 0.92);
        };

        Rescan();
    }

    // ------------------------------------------------------------------ Blender

    private void Rescan()
    {
        _found = BlenderPlugin.Installs();

        var missing = BlenderPlugin.Source() is null;
        this.FindControl<TextBlock>("BlenderMissing")!.IsVisible = missing;
        this.FindControl<StackPanel>("NoBlender")!.IsVisible = !missing && _found.Count == 0;

        var rows = this.FindControl<StackPanel>("BlenderRows")!;
        rows.Children.Clear();
        if (missing) return;

        foreach (var install in _found) rows.Children.Add(Row(install));
    }

    /// <summary>One Blender: its version, what is installed, and the two buttons.</summary>
    private Control Row(BlenderPlugin.Install install)
    {
        var stale = BlenderPlugin.StaleOf(install);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*,Auto,Auto"), Margin = new Thickness(0, 2) };

        grid.Children.Add(new TextBlock { Text = "Blender " + install.Version, FontSize = 12.5,
                                          VerticalAlignment = VerticalAlignment.Center });

        TextBlock status;
        if (install.InstalledAt is { } when)
        {
            var have = install.AddonVersion is { } v ? $"version {v}, " : "";
            status = new TextBlock
            {
                Text = stale is null ? $"installed ({have}{when:d MMM, HH:mm})"
                                     : $"OUT OF DATE ({have}{when:d MMM, HH:mm}) - this Studio has {stale.Shipped}",
                Foreground = stale is null ? Good : Warn,
            };
        }
        else status = new TextBlock { Text = "not installed", Foreground = (IBrush?)this.FindResource("Muted") };
        status.FontSize = 12.5;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextWrapping = TextWrapping.Wrap;
        ToolTip.SetTip(status, install.Target);
        Grid.SetColumn(status, 1);
        grid.Children.Add(status);

        var label = !install.Present ? "Install" : stale is not null ? "Update" : "Reinstall";
        var go = new Button { Content = label, Padding = new Thickness(16, 5), FontSize = 12, MinWidth = 84,
                              HorizontalContentAlignment = HorizontalAlignment.Center,
                              Margin = new Thickness(8, 0, 0, 0) };
        go.Click += (_, _) => DoInstall(install);
        Grid.SetColumn(go, 2);
        grid.Children.Add(go);

        var remove = new Button { Content = "Remove", Padding = new Thickness(14, 5), FontSize = 12, MinWidth = 80,
                                  HorizontalContentAlignment = HorizontalAlignment.Center,
                                  IsEnabled = install.Present, Margin = new Thickness(8, 0, 0, 0) };
        remove.Click += (_, _) => DoRemove(install);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        return grid;
    }

    private void OnRescan(object? sender, RoutedEventArgs e) => Rescan();

    /// <summary>Install / Reinstall / Update one Blender's add-on. What its button does.</summary>
    public void DoInstall(BlenderPlugin.Install install)
    {
        var why = _vm is null ? BlenderPlugin.Apply(install) : _vm.InstallAddon(install);
        _justInstalled = why is null ? install.Module : "";
        Note("BlenderNote", why, bad: why is not null);
        this.FindControl<Border>("OneMoreStep")!.IsVisible = why is null;
        this.FindControl<TextBlock>("ModuleText")!.Text = _justInstalled;
        Rescan();
    }

    /// <summary>Take one Blender's add-on out again. What its Remove button does.</summary>
    public void DoRemove(BlenderPlugin.Install install)
    {
        var why = _vm is null ? BlenderPlugin.Remove(install) : _vm.RemoveAddon(install);
        _justInstalled = "";
        Note("BlenderNote", why ?? "Removed.", bad: why is not null);
        this.FindControl<Border>("OneMoreStep")!.IsVisible = false;
        Rescan();
    }

    /// <summary>Driver flag: press Install / Update / Reinstall on the row for this Blender version.</summary>
    public bool PressInstall(string version)
    {
        if (_found.FirstOrDefault(i => i.Version == version) is not { } install) return false;
        DoInstall(install);
        return true;
    }

    /// <summary>Driver flag: press Remove on the row for this Blender version.</summary>
    public bool PressRemove(string version)
    {
        if (_found.FirstOrDefault(i => i.Version == version) is not { } install) return false;
        DoRemove(install);
        return true;
    }

    private async void OnCopyName(object? sender, RoutedEventArgs e)
    {
        if (_justInstalled.Length == 0) return;
        if (Clipboard is { } clip) await clip.SetTextAsync(_justInstalled);
        _vm?.Say("copied " + _justInstalled);
    }

    // ------------------------------------------------------------------ Substance + Max

    private async void OnSavePreset(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Where should the preset go?", SubstancePreset.PainterShelf()) is { } into)
            SavePresetInto(into);
    }

    private async void OnSaveScript(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Where should the script go?", MaxRetarget.MaxScripts()) is { } into)
            SaveScriptInto(into);
    }

    /// <summary>What the preset button does once a folder is chosen.</summary>
    public void SavePresetInto(string folder)
    {
        var to = SubstancePreset.Install(folder, out var why);
        Note("SubstanceNote", to is null ? why ?? "could not write it there" : "Saved to " + to, bad: to is null);
        if (to is not null) _vm?.Say("Substance preset saved to " + to);
    }

    /// <summary>What the script button does once a folder is chosen.</summary>
    public void SaveScriptInto(string folder)
    {
        var to = MaxRetarget.Install(folder, out var why);
        Note("MaxNote", to is null ? why ?? "could not write it there" : "Saved to " + to, bad: to is null);
        if (to is not null) _vm?.Say("Max retarget script saved to " + to);
    }

    /// <summary>A picker that STARTS at the place the tool is usually kept, when this machine has one.</summary>
    private async Task<string?> PickFolder(string title, string? start)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (start is not null && Directory.Exists(start))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    // ------------------------------------------------------------------ shared

    private void Note(string name, string? text, bool bad)
    {
        var box = this.FindControl<TextBlock>(name)!;
        box.Text = text ?? "";
        box.IsVisible = !string.IsNullOrEmpty(text);
        box.Foreground = bad ? Bad : (name == "BlenderNote" ? (IBrush?)this.FindResource("Fore") : Good);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

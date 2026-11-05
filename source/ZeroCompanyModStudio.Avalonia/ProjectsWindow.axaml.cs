using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>One mod on disk, as the list shows it.</summary>
public sealed class ModRow
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Path { get; init; }
    public required string When { get; init; }
}

/// <summary>
/// THE WINDOW THE STUDIO OPENS WITH: create a mod, or carry on with one.
///
/// A window rather than a screen inside the main one, at the author's request, and it earns that:
/// the main window has no meaning until a project is chosen, and the Project menu can bring this
/// back at any time without the Studio having to hold a "no mod open" layout it otherwise never
/// shows.
/// </summary>
public partial class ProjectsWindow : Window, INotifyPropertyChanged
{
    private ModRow? _selected;

    public new event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public ObservableCollection<ModRow> Mods { get; } = new();

    /// <summary>The project the person chose, or null when they closed without choosing.</summary>
    public string? Chosen { get; private set; }

    /// <summary>True when they asked for a new mod rather than an existing one.</summary>
    public bool WantsNew { get; private set; }

    public string FolderLine { get; private set; } = "";
    public string CountLine { get; private set; } = "";
    public bool IsEmpty => Mods.Count == 0;
    public bool HasAny => Mods.Count > 0;
    public bool HasSelection => _selected is not null;

    public ModRow? Selected
    {
        get => _selected;
        set { _selected = value; Raise(); Raise(nameof(HasSelection)); }
    }

    private string _folder;

    /// <summary>
    /// Opens on the folder this person last chose. <see cref="StudioSettings.ModFolder"/> returns
    /// null when they never have, and Resolve turns that into the branded default -- which is
    /// very likely not where their mods are, hence the Change button beside it.
    /// </summary>
    public ProjectsWindow() : this(ModLibrary.Resolve(StudioSettings.ModFolder())) { }

    public ProjectsWindow(string folder)
    {
        _folder = folder;
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
        Reload();
    }

    private void Reload()
    {
        Mods.Clear();
        List<ModLibrary.Entry> entries;
        try { entries = ModLibrary.List(_folder); }
        catch (Exception) { entries = new List<ModLibrary.Entry>(); }

        foreach (var e in entries)
        {
            // A BROKEN PROJECT IS LISTED, NAMED, AND SAYS WHAT IS WRONG. ModLibrary sets
            // Unreadable rather than dropping the entry for exactly this reason: a project that
            // silently fails to appear cannot be told from one that was never saved.
            var detail = e.Unreadable is not null
                ? e.Describe
                : string.Join("   ", new[]
                    {
                        e.Author.Length > 0 ? $"by {e.Author}" : "",
                        e.Version.Length > 0 ? $"v{e.Version}" : "",
                        e.Parts == 1 ? "1 part" : $"{e.Parts} parts",
                        e.What
                    }.Where(s => s.Length > 0));

            Mods.Add(new ModRow
            {
                Title = e.Title,
                Detail = detail,
                Path = e.Path,
                When = e.Modified.ToString("d MMM yyyy  HH:mm"),
            });
        }

        FolderLine = _folder;
        CountLine = Mods.Count == 1 ? "1 mod project" : $"{Mods.Count:N0} mod projects";
        Raise(nameof(FolderLine));
        Raise(nameof(CountLine));
        Raise(nameof(IsEmpty));
        Raise(nameof(HasAny));
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        WantsNew = true;
        Close();
    }

    private void OnOpenSelected(object? sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        Chosen = Selected.Path;
        Close();
    }

    private void OnListDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        Chosen = Selected.Path;
        Close();
    }

    private async void OnChangeFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where do you keep your mods?",
            AllowMultiple = false,
        });
        if (picked.Count == 0) return;                       // cancelled means nothing happened
        if (picked[0].TryGetLocalPath() is not { } folder) return;

        _folder = folder;
        StudioSettings.SetModFolder(folder);
        Reload();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        // THE PLATFORM'S OWN PICKER, which is one of the things that stops being a compromise the
        // moment this is not ImGui: a person's recent folders, typed paths and network drives all
        // work because it is the real dialog rather than a list drawn by this application.
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a mod project",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Mod project") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;
        Chosen = files[0].TryGetLocalPath();
        if (Chosen is not null) Close();
    }
}

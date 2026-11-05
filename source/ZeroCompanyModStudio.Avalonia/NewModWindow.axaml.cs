using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// Name a new mod, and say on every keystroke whether that name can be used.
///
/// THE RULES ARE NOT RESTATED HERE. <c>ModLibrary.PathFor</c> decides what a mod may be called and
/// why not — empty, nothing a file system can use, or a name already taken — and this asks it
/// rather than keeping a second opinion. A dialog that validates by its own rules eventually
/// accepts something the thing behind it refuses, and the person finds out after pressing the
/// button that said Create.
/// </summary>
public sealed class NewModVm : Obs
{
    private readonly string _folder;
    private string _modName = "";
    private string _verdict = "";
    private IBrush _verdictBrush = Brushes.Gray;
    private bool _canCreate;

    public NewModVm(string folder)
    {
        _folder = folder;
        Recheck();
    }

    public string Folder => _folder;

    /// <summary>The project that was created, or null when they cancelled.</summary>
    public string? CreatedPath { get; private set; }

    public string ModName
    {
        get => _modName;
        set { if (Set(ref _modName, value)) Recheck(); }
    }

    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public IBrush VerdictBrush { get => _verdictBrush; private set => Set(ref _verdictBrush, value); }
    public bool CanCreate { get => _canCreate; private set => Set(ref _canCreate, value); }

    private void Recheck()
    {
        var path = ModLibrary.PathFor(_folder, _modName, out var why);
        CanCreate = path is not null;

        if (_modName.Trim().Length == 0)
        {
            // Nothing typed is not an error yet. Saying "give the mod a name" before they have had
            // a chance to is nagging, so the empty state just shows where it will go.
            Verdict = $"will be created in {_folder}";
            VerdictBrush = Res("Faint", Brushes.Gray);
        }
        else if (path is not null)
        {
            Verdict = path;
            VerdictBrush = Res("Edited", Brushes.MediumSeaGreen);
        }
        else
        {
            Verdict = why ?? "that name cannot be used";
            VerdictBrush = Res("Danger", Brushes.IndianRed);
        }
    }

    /// <summary>Tries to create it; returns false and leaves the reason in <see cref="Verdict"/>.</summary>
    public bool Create()
    {
        var name = _modName.Trim();
        if (ModLibrary.PathFor(_folder, name, out var why) is not { } path)
        {
            Verdict = why ?? "that name cannot be used";
            VerdictBrush = Res("Danger", Brushes.IndianRed);
            return false;
        }

        try
        {
            // ModProject.Create gives a new mod its OWN asset folder rather than the shared
            // default, so two mods cannot build their parts to the same package paths. Core's
            // job, not this window's.
            var mod = ModProject.Create(path, name);
            mod.Save();
            CreatedPath = path;
            return true;
        }
        catch (Exception ex)
        {
            Verdict = ex.Message;
            VerdictBrush = Res("Danger", Brushes.IndianRed);
            return false;
        }
    }

    private static IBrush Res(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out var found) == true && found is IBrush b
            ? b : fallback;
}

public partial class NewModWindow : Window
{
    private readonly NewModVm _vm;

    /// <summary>The project that was created, or null when they cancelled.</summary>
    public string? CreatedPath => _vm.CreatedPath;

    /// <summary>Type into the box from outside, for <c>--newmod &lt;name&gt;</c>.</summary>
    public string ModName { get => _vm.ModName; set => _vm.ModName = value; }

    public NewModWindow() : this(ModLibrary.Resolve(StudioSettings.ModFolder())) { }

    public NewModWindow(string folder)
    {
        _vm = new NewModVm(folder);
        AvaloniaXamlLoader.Load(this);
        DataContext = _vm;
        Opened += (_, _) => this.FindControl<TextBox>("NameBox")?.Focus();
    }

    private void OnNameKey(object? sender, KeyEventArgs e)
    {
        // Escape cancels, which is what Escape does everywhere. IsDefault on the Create button
        // already gives Enter, and only while it is enabled.
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnCreate(object? sender, RoutedEventArgs e) => Press();

    /// <summary>
    /// What the Create button does, callable without one. Used by <c>--newmod &lt;name&gt; --create</c>
    /// so the path AFTER Create — write the project, open the Studio on it — can be checked.
    /// Synthesising a mouse click would send input at whatever happens to have focus, which is
    /// not something a check should ever risk on somebody's desktop.
    /// </summary>
    public void Press() { if (_vm.Create()) Close(); }
}

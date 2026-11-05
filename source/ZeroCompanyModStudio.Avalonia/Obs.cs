using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// A bindable object. The same base the Level Editor uses, and for the same reason.
///
/// A WINDOW CANNOT BE ITS OWN VIEW MODEL HERE, which cost an hour to find out. Setting
/// `DataContext = this` and declaring `public new event PropertyChangedEventHandler PropertyChanged`
/// compiles, binds, and shows correct INITIAL values — because those are read once, before any
/// notification is needed. Everything after that is silent: Avalonia's `Window` already carries a
/// `PropertyChanged` member, `new` HIDES it rather than implementing the interface, and the
/// binding system subscribes to the one it can see. Nothing throws. Nothing logs. The first
/// symptom was a text box that would not show a value assigned to it a moment after construction,
/// and the second would have been the Open button never enabling when a row was selected.
///
/// So view models are separate objects, as they are over in the Level Editor, and a window's job
/// is to load its XAML and hand one over.
///
/// Duplicated from `ZeroCompanyLevelEditor/Editor.cs` rather than shared, deliberately, and the
/// line is worth stating because this repository is strict about second copies: `SplashBmp` moved
/// into Core because two copies of a decoder can DISAGREE about an asset, which is a bug waiting
/// to happen. This is language boilerplate with no knowledge of anything in it — two copies cannot
/// disagree about a fact, because neither holds one.
/// </summary>
public abstract class Obs : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

/// <summary>
/// ONE LINE OF THE LOG, and whether it is a complaint.
///
/// A string list could not say which lines are problems, so the validator's sentences lived in
/// the build bar instead — where three of them share one trimmed line and the rest are a tooltip.
/// The author: "it should be inside the log in red".
/// </summary>
public sealed class LogLine(string text, bool bad)
{
    public string Text { get; } = text;
    public bool Bad { get; } = bad;
    public bool Ok => !bad;
}

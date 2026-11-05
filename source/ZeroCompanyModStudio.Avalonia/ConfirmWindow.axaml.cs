using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ASK BEFORE SOMETHING CANNOT BE UNDONE.
///
/// The Studio has no undo yet — Edit ▸ Undo and Redo are drawn and disabled — so the one act that
/// removes work has to ask first. And it asks by NAMING what it is about to remove: "are you
/// sure?" over a dialog that covers the thing being removed is a question nobody can answer.
///
/// <see cref="AvaloniaXamlLoader.Load"/> leaves Name= fields null, which has cost this project an
/// afternoon before now, so every control is reached through FindControl.
/// </summary>
public partial class ConfirmWindow : Window
{
    /// <summary>True when they pressed the button that does the thing.</summary>
    public bool Confirmed { get; private set; }

    public ConfirmWindow() : this("Are you sure?", "", "", "", "Yes") { }

    public ConfirmWindow(string heading, string detail, string warn, string aside, string yes)
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<TextBlock>("Heading")!.Text = heading;

        var detailBlock = this.FindControl<TextBlock>("Detail")!;
        detailBlock.Text = detail;
        detailBlock.IsVisible = detail.Length > 0;

        // ABSENT WHERE THERE IS NOTHING TO WARN ABOUT. A red box holding no text is a shape that
        // says "danger" about nothing, which is how a warning stops being read.
        this.FindControl<TextBlock>("Warn")!.Text = warn;
        this.FindControl<Border>("WarnBox")!.IsVisible = warn.Length > 0;

        var asideBlock = this.FindControl<TextBlock>("Aside")!;
        asideBlock.Text = aside;
        asideBlock.IsVisible = aside.Length > 0;

        this.FindControl<Button>("Yes")!.Content = yes;

        // CANCEL TAKES THE FOCUS, so Enter and Space do the safe thing. A dialog that removes
        // work on the key somebody is already pressing removes work by accident.
        Opened += (_, _) => this.FindControl<Button>("No")?.Focus();
    }

    /// <summary>Press Yes from outside, for a check that runs without a mouse.</summary>
    public void PressYes() { Confirmed = true; Close(); }

    private void OnYes(object? sender, RoutedEventArgs e) => PressYes();

    private void OnNo(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Ask, and hand back what they said. The one call site shape this has.</summary>
    public static async Task<bool> AskAsync(Window owner, string heading, string detail,
                                            string warn, string aside, string yes)
    {
        var w = new ConfirmWindow(heading, detail, warn, aside, yes);
        await w.ShowDialog(owner);
        return w.Confirmed;
    }
}

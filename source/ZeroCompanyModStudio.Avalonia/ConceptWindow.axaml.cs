using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// The edit panel, drawn as a concept rather than described as one.
///
/// WHY THIS EXISTS AT ALL. The author has said twice that the edit panel looks wrong, and asked
/// to be shown something before it is built. A picture made outside the tool is worth very little
/// here: a design he liked once already turned out to be one the framework of the day could not
/// draw. So this is the framework drawing it — same colours, same controls, same window — with
/// static content in place of a project. Nothing in it saves, and nothing behind it is wired.
///
/// TWO SHAPES, ONE SET OF CARDS. A shows every section in one scroll with the rail as a jump
/// list; B shows one section at a time and the rail chooses it. That is the only real question
/// the two layouts differ on, so they share everything else and differ in what is visible.
///
/// Reached with <c>--concept</c>, or <c>--concept b</c> to open on the second shape, so both can
/// be photographed without a mouse.
/// </summary>
public partial class ConceptWindow : Window
{
    /// <summary>Rail key ⇄ card name; "Rail" + key and "Card" + key are the control names.</summary>
    private static readonly string[] Sections =
    {
        "Identity", "Meshes", "Icon", "Textures", "Colours", "Offered", "Slot", "Numbers", "Deeper",
    };

    private bool _oneAtATime;
    private string _chosen = "Identity";
    private bool _sheetShown = true;

    public ConceptWindow(bool oneAtATime = false)
    {
        // The name generator wires `Name=` fields inside a generated InitializeComponent, and
        // every window in this application loads its XAML directly instead — so those fields are
        // null here too and everything below goes through FindControl. Noted because it is the
        // trap MainWindow.axaml.cs already carries a comment about.
        AvaloniaXamlLoader.Load(this);
        _oneAtATime = oneAtATime;
        // B opens on the meshes rather than the name, because one section at a time is only
        // worth looking at on a section with something in it.
        if (oneAtATime) _chosen = "Meshes";
        Apply();
    }

    private void OnLayoutA(object? sender, RoutedEventArgs e) { _oneAtATime = false; Apply(); }

    private void OnLayoutB(object? sender, RoutedEventArgs e) { _oneAtATime = true; Apply(); }

    private void OnRail(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: string key }) { _chosen = key; Apply(); }
    }

    private void OnToggleSheet(object? sender, RoutedEventArgs e)
    {
        _sheetShown = !_sheetShown;
        Apply();
    }

    /// <summary>
    /// Draw the state: which rail line is lit, which cards are visible, and whether the read
    /// sheet is folded away.
    /// </summary>
    private void Apply()
    {
        foreach (var key in Sections)
        {
            if (this.FindControl<Border>("Rail" + key) is { } rail)
            {
                rail.Classes.Set("on", key == _chosen);
            }

            // In A every card is drawn and the rail only says where you are; in B the rail is a
            // chooser and one card fills the column.
            if (this.FindControl<Border>("Card" + key) is { } card)
            {
                card.IsVisible = !_oneAtATime || key == _chosen;
            }
        }

        if (this.FindControl<Border>("SheetCol") is { } sheet) sheet.IsVisible = _sheetShown;
        if (this.FindControl<Button>("BtnSheet") is { } toggle)
            toggle.Content = _sheetShown ? "What it is now  ▸" : "What it is now  ◂";

        // The chosen shape, said plainly, because two buttons that both look pressable say
        // nothing about which one you are looking at.
        if (this.FindControl<Button>("BtnA") is { } a)
            a.Content = _oneAtATime ? "A — every section, one scroll" : "▸ A — every section, one scroll";
        if (this.FindControl<Button>("BtnB") is { } b)
            b.Content = _oneAtATime ? "▸ B — one section at a time" : "B — one section at a time";
    }
}

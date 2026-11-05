using Avalonia.Media.Imaging;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE SPECIALIZATION THIS MOD SHIPS, open as a tab.
///
/// A specialization is not a part and not a weapon: it carries no geometry at all. What it IS is a
/// name, a crest, who may take it, what it grants outright, and a FOCUS TREE — three lines of
/// rungs that a recruit spends focus climbing. The tree is what makes one selectable, so a copy
/// that dropped it would be a copy of the name alone.
///
/// The edit surface is <see cref="SheetTab"/>'s, as it is for a part and a weapon; what differs is
/// the cards it fills and where they land. A spec's land on <see cref="ModSpec"/>.
/// </summary>
public sealed class SpecTab : SheetTab
{
    private readonly SpecDetails.Spec _src;
    private Bitmap? _icon;

    public SpecTab(SpecDetails.Spec source, ModSpec spec)
    {
        _src = source;
        Spec = spec;
        _title = source.DisplayName ?? source.Name;

        if (source.IconPath is { Length: > 0 } icon)
            IconLoader.Want(icon, b =>
            {
                _icon = b;
                Raise(nameof(Icon));
                Raise(nameof(HasIcon));
                Raise(nameof(HasPicture));
            });
    }

    private readonly string _title;

    /// <summary>The specialization this tab edits. A spec tab never opens on anything else.</summary>
    public ModSpec Spec { get; }

    /// <summary>What it was copied from, for the sheet and for reading its tree back.</summary>
    public SpecDetails.Spec Source => _src;

    public override string Title => Spec.Display.Length > 0 ? Spec.Display : _title;
    public override string Subtitle => Spec.Id;
    public override string Tooltip => $"{Spec.Id} — your own copy of {_src.Name}";
    public override string Aside => $"your own copy of {_src.Name}";

    public override Bitmap? Icon => _icon;
    public override bool IsCopy => true;
    public override bool CanEdit => true;

    public override string Headline =>
        _src.IsPrimary ? "Primary — the picker offers these"
        : _src.IsSecondary ? "Secondary — a primary's partner"
        : "";

    /// <summary>
    /// The one thing that stops a specialization being offered at all.
    ///
    /// The picker draws a three-line tree, and a specialization that cannot fill one is never
    /// shown. The hero kits are like this, and copying one inherits it — so it is said here
    /// rather than discovered in a menu that simply does not list the result.
    /// </summary>
    public override string Problem =>
        Spec.Tree.Count == 0
            ? "No focus tree. The picker draws a three-line tree and never offers a "
              + "specialization that cannot fill one — the hero kits (Rex, Anakin) are like this, "
              + "and a copy inherits it."
            : "";

    public void SpecRenamed()
    {
        Raise(nameof(Title));
        Raise(nameof(Subtitle));
    }

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Copied from", Leaf(_src.Path));
            Fact("Identity", Spec.Identity.Length > 0 ? Spec.Identity : _src.Identity ?? "—");
            Fact("Crest", Spec.IconTag.Length > 0 ? Spec.IconTag : _src.IconTag ?? "—");

            if (_src.AllowedSlots.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Where it may go ({_src.AllowedSlots.Count})" });
                foreach (var s in _src.AllowedSlots) blocks.Add(new SheetBullet { Text = ShortTag(s) });
            }

            blocks.Add(new SheetBand
            { Text = $"Focus tree — {Spec.Tree.Count} line(s), {Spec.RungCount} rung(s)" });

            if (_src.AttributeTables.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Attribute tables ({_src.AttributeTables.Count})" });
                foreach (var t in _src.AttributeTables) blocks.Add(new SheetBullet { Text = Leaf(t) });
            }

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }
        }
    }
}

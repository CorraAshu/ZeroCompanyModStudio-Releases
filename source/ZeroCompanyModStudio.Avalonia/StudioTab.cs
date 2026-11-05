namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE THING OPEN IN THE STRIP.
///
/// An asset opened out of the Data Explorer and a clothing part opened out of a section are the
/// same thing to the strip: a title, a tooltip, and an ✕. What differs is only what the right
/// pane draws for it, and that is a DataTemplate's job rather than a flag this class carries.
///
/// This is the shape the strip was built for. From the first Data Explorer commit: "the tab strip
/// must hold BOTH kinds of tab ... build it as asset tabs only and the guided editors need a
/// separate home later, which puts the row of nine buttons back". Adding the second kind cost a
/// base class and no rework, which is the whole return on that decision.
/// </summary>
public abstract class StudioTab : Obs
{
    /// <summary>What the tab says.</summary>
    public abstract string Title { get; }

    /// <summary>What hovering it says. A path, usually.</summary>
    public virtual string Tooltip => "";

    private bool _active;

    /// <summary>
    /// Whether this is the tab the right pane is showing.
    ///
    /// THE SAME FLAG <see cref="SectionTab"/> HAS CARRIED ALL ALONG, and its absence here is the
    /// author's "when i am in a tab say missions or weapons they highlight yellow, but don't if
    /// you are in the tab looking at a model or texture or even data". The strip was built to
    /// hold both kinds of tab and the accent rule was only ever bound on one of them, so half the
    /// strip could not say where you were.
    /// </summary>
    public bool IsActive { get => _active; set => Set(ref _active, value); }
}

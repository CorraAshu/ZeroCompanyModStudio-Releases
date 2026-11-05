using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// WHICH VERSION OF THIS MAP?
///
/// A shipped map does not hold one level. The hub holds Trick's station in three states, Cly's in
/// four, and six positions of every hangar lift, all placed at the SAME spot, because the game
/// shows exactly one of each depending on where the story has got to.
///
/// Loading them all was the editor's first behaviour and it is wrong twice over: the picture is
/// near-identical geometry fighting for the same pixels, which reads as the whole level flashing,
/// and it is not a view of anything the game ever renders. The author found it from the symptom:
///
///     "are you loading every instance of this map at once into the viewport? Because that would
///      be bad and cause a lot of problems i feel like, this map has different variants throughout
///      the story."
///
/// and then asked for this:
///
///     "When you click a map you should have a popup come up asking you which one you wanna load?
///      since a lot of these maps have different versions of them."
///
/// Built in code rather than XAML because it is a list of rows whose length is decided at runtime,
/// and a dialog that appears for some maps and not others is not worth a second compiled view.
/// </summary>
public sealed class VariantDialog : Window
{
    private readonly List<(string Prefix, ComboBox Box)> _rows = new();

    /// <summary>
    /// One option, shown as the STATE ALONE.
    ///
    /// The first version of this dialog put the whole package name in the box, so every row read
    /// LI_Station_Cly_ON under a label reading LI_Station_Cly -- the same string twice, with the
    /// one character that differs at the far end of it. The author's words: "How do i know which
    /// one of these i have selected? it's not very clear."
    ///
    /// What differs is the state, so the state is what the box shows: ON, OFF, FIRST TURN,
    /// DEACEASED. The full name is underneath in case it is wanted, and it follows the selection.
    /// </summary>
    private sealed record Choice(string Display, string Value)
    {
        public override string ToString() => Display;
    }

    /// <summary>The leaf names to draw, or null when the dialog was cancelled.</summary>
    public HashSet<string>? Chosen { get; private set; }

    /// <summary>
    /// Where the shared part of these names ends, ON A TOKEN BOUNDARY.
    ///
    /// ON and OFF share their O, so the raw common prefix of LI_Station_Kabb_ON and _OFF is
    /// "LI_Station_Kabb_O" and the states come out as "N" and "FF". Backing up to the last
    /// underscore gives ON and OFF. Unless one option IS the shared part -- Astromech beside
    /// Astromech_After -- where the boundary is already right and backing up would lose a word.
    /// </summary>
    private static string Stem(LevelReader.VariantGroup g)
    {
        var prefix = g.Prefix;
        if (g.Options.Any(o => o.Equals(prefix, StringComparison.OrdinalIgnoreCase))) return prefix;
        var cut = prefix.LastIndexOf('_');
        return cut > 0 ? prefix[..(cut + 1)] : prefix;
    }

    private static string Pretty(string stem)
    {
        var name = stem.TrimEnd('_').Replace('_', ' ');
        return name.StartsWith("LI ", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
    }

    /// <summary>
    /// The sublevels the author chose NOT to draw, by name, or null when nothing was excluded.
    ///
    /// Separate from <see cref="Chosen"/> because the two act on different machinery: Chosen
    /// picks between LEVEL INSTANCES sharing a spot, and this hides whole SUBLEVELS through the
    /// same set the per-sublevel tick boxes use -- which already releases their meshes and pulls
    /// the far plane in, rather than merely skipping the draw.
    /// </summary>
    public HashSet<string>? HiddenSublevels { get; private set; }

    private readonly List<(LevelReader.SublevelSet Set, CheckBox Box)> _sets = new();

    public VariantDialog(string mapId, List<LevelReader.VariantGroup> groups)
        : this(mapId, groups, new List<LevelReader.SublevelSet>()) { }

    public VariantDialog(string mapId, List<LevelReader.VariantGroup> groups,
                         List<LevelReader.SublevelSet> sets)
    {
        Title = "Which version of " + mapId + "?";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var accent = new SolidColorBrush(Color.Parse("#E8A33D"));
        var list = new StackPanel { Spacing = 14 };

        foreach (var g in groups)
        {
            var stem = Stem(g);
            var choices = g.Options.Select(o =>
            {
                var rest = o.Length > stem.Length ? o[stem.Length..].Trim('_') : "";
                return new Choice(rest.Length == 0 ? "as built" : rest.Replace('_', ' '), o);
            }).ToList();

            var box = new ComboBox
            {
                ItemsSource = choices,
                SelectedItem = choices.First(c => c.Value.Equals(g.Preferred, StringComparison.OrdinalIgnoreCase)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = accent,
            };
            _rows.Add((g.Prefix, box));

            var full = new TextBlock
            {
                Text = g.Preferred,
                FontSize = 11,
                Opacity = 0.5,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            };
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is Choice c) full.Text = c.Value;
            };

            // THREE LINES PER ROW, not four. The instruction line was repeated once per group and
            // said the same thing every time, which pushed the list past its own scroller and cut
            // the last row in half. The count belongs on the heading; the instruction belongs at
            // the top of the dialog, once.
            var row = new StackPanel { Spacing = 3 };
            row.Children.Add(new TextBlock
            {
                // "LI_" is the game's tag for a level instance and it heads every row, so it
                // carries no information here. The exact package name is the dim line below.
                Text = Pretty(stem) + "   ·   " + g.Options.Count + " versions",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
            });
            row.Children.Add(box);
            row.Children.Add(full);
            list.Children.Add(row);
        }

        // ALTERNATIVE SETS OF WHOLE SUBLEVELS, which the groups above cannot express.
        //
        // The hub is docked at the Ring of Kafrene for most of the game and adrift in space after
        // the Imperial era begins, and that is three sublevels against one. Nothing here guesses
        // which sets are alternatives: every folder that has a sibling gets a tick, all on, and
        // the author turns off the state he does not want. The author's ask was to load each
        // variant whenever he wants, so choosing IS the feature.
        if (sets.Count > 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "Sets of whole sublevels",
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
            });
            list.Children.Add(new TextBlock
            {
                Text = "These are separated by the game's own folders, not by a guess at their " +
                       "names. Untick a set to leave it out.",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });

            foreach (var s in sets)
            {
                var box = new CheckBox
                {
                    Content = $"{s.Label}   ·   {s.Sublevels.Count} sublevel(s)",
                    IsChecked = true,
                    FontSize = 13,
                };
                _sets.Add((s, box));

                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(box);
                row.Children.Add(new TextBlock
                {
                    Text = string.Join(", ", s.Sublevels),
                    FontSize = 11,
                    Opacity = 0.5,
                    Margin = new Thickness(26, 0, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                });
                list.Children.Add(row);
            }
        }

        var ok = new Button { Content = "Load", IsDefault = true, Padding = new Thickness(18, 4) };
        ok.Click += (_, _) =>
        {
            Chosen = _rows.Select(r => (r.Box.SelectedItem as Choice)?.Value ?? "")
                          .Where(v => v.Length > 0)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HiddenSublevels = _sets.Where(s => s.Box.IsChecked != true)
                                   .SelectMany(s => s.Set.Sublevels)
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Close();
        };

        // SHOW EVERYTHING is kept, and labelled honestly rather than hidden. It is the only way to
        // see a state the heuristic would not pick, and it is how the level looked before any of
        // this -- so the warning is the thing that has to be loud, not the option.
        var all = new Button { Content = "Show every version", Padding = new Thickness(14, 4) };
        all.Click += (_, _) =>
        {
            Chosen = groups.SelectMany(g => g.Options).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Close();
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4) };
        cancel.Click += (_, _) => { Chosen = null; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(all);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        body.Children.Add(new TextBlock
        {
            Text = "Parts of this map ship in several story states, placed on top of one another.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = "The game shows one of each at a time, so pick a state below. Loading them all "
                 + "draws every version in the same space, which flickers and is not a view of "
                 + "anything the game renders.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 560,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        });
        body.Children.Add(buttons);

        Content = body;
    }
}

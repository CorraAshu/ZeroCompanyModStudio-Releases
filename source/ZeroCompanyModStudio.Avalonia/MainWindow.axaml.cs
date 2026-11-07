using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

public partial class MainWindow : Window
{
    private readonly StudioVm _vm = new();

    /// <summary>
    /// Looked up rather than taken from a generated field.
    ///
    /// The Avalonia name generator wires `Name=` controls to fields inside `InitializeComponent`,
    /// and this window calls `AvaloniaXamlLoader.Load` directly — so those fields stay null and
    /// touching one is a NullReferenceException at the first click, not at build time. FindControl
    /// asks the loaded tree, which is the thing that actually exists.
    /// </summary>
    private Grid Body => _body ??= this.FindControl<Grid>("BodyGrid")!;
    private Grid? _body;

    public MainWindow() : this(null) { }

    public MainWindow(string? projectPath)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _vm;

#if DEV
        // --max: open maximised.
        //
        // NOT a convenience. Every screenshot of this application is judged at the size the
        // author actually runs it, and a layout that looks tidy in a 1,500-pixel window can open
        // a field of empty in a 2,500-pixel one — which is exactly the fault he caught in the
        // editor's first draft, in a picture I had taken of a small window.
        if (Environment.GetCommandLineArgs().Contains("--max")) WindowState = WindowState.Maximized;

        // ZCMS_BLENDER_ROOT=<folder>: look for Blender's settings (and install, update and remove the
        // add-on) under THIS folder instead of the real Blender Foundation folder in AppData. The same
        // job --installto does for the game, for the same reason, and fail-closed in the same way: a
        // background test run that did not set it is refused every write (StudioVm.AddonWriteAllowed).
        // An environment variable because a background Blender needs the same scratch root, and
        // read here so nothing asks the real folder first.
        if (Environment.GetEnvironmentVariable("ZCMS_BLENDER_ROOT") is { Length: > 0 } scratchBlender)
            BlenderPlugin.RootOverride = scratchBlender;

        // --installto <Paks folder>: every install, un-install and "is it installed" question goes
        // to THIS folder instead of the mounted game's (the build still reads the game). Read here,
        // before the project opens, so nothing is ever asked of the real game folder first.
        var installTo = Array.IndexOf(Environment.GetCommandLineArgs(), "--installto");
        if (installTo >= 0 && installTo + 1 < Environment.GetCommandLineArgs().Length)
            _vm.PaksOverride = Environment.GetCommandLineArgs()[installTo + 1];
#endif

        // WHICH BUILD THIS LOG IS FROM, as a person would name it: "Zero Company Mod Studio 2.0.0".
        // It used to say "- Avalonia", the engineering label for the port, which means nothing to
        // anybody reading a log they were asked to send.
        _vm.Say(UpdateNotice.Running.Length > 0
            ? $"{Branding.Product} {UpdateNotice.Running}"
            : Branding.Product);
        if (projectPath is not null) _vm.OpenMod(projectPath);
        else _vm.Say("No mod open.");
        Retitle();

        // THE SHARE BUTTON FOLLOWS THE GAME FOLDER, which can change from outside the Studio (a
        // mod installed or removed by hand, or by another copy of this window). Coming back to the
        // window is the moment somebody could have done that.
        Activated += (_, _) => _vm.RefreshShare();

        // THE UPDATE NOTICE follows the shared answer. The check itself is started once the
        // window is up (see Opened below); this only redraws the bar when it lands, and the event
        // comes from the thread the request ran on.
        Action updateChanged = () => Dispatcher.UIThread.Post(_vm.UpdateChanged);
        UpdateNotice.Changed += updateChanged;
        Closed += (_, _) => UpdateNotice.Changed -= updateChanged;

        // The game is opened AFTER the window is up, never before it. Six containers and 180,000
        // files is several seconds, and spending them on a blank screen is the wrong first
        // impression for a tool that is otherwise instant.
        Opened += async (_, _) =>
        {
#if DEV
            // The driver flags that stand in for the network or for a window size, read BEFORE the
            // check starts: --fakeupdate must suppress the real request, and --runningas must
            // change what the real request says it is.
            var early = Environment.GetCommandLineArgs();
            var ru = Array.IndexOf(early, "--runningas");
            if (ru >= 0 && ru + 1 < early.Length) UpdateNotice.RunAs(early[ru + 1]);
            var fu = Array.IndexOf(early, "--fakeupdate");
            if (fu >= 0 && fu + 1 < early.Length && !early[fu + 1].StartsWith("--"))
                UpdateNotice.Pretend(early[fu + 1], Branding.Home);

            // --winsize <W>x<H>: the window's client size, in DIPs. The notice and the credit have
            // to fit side by side at the NARROWEST the window may be (MinWidth, 900), and a picture
            // of a 1,500-pixel window says nothing about that.
            var ws = Array.IndexOf(early, "--winsize");
            if (ws >= 0 && ws + 1 < early.Length)
            {
                var wh = early[ws + 1].Split('x', 'X');
                if (wh.Length == 2 && double.TryParse(wh[0], out var winW) && double.TryParse(wh[1], out var winH))
                { Width = winW; Height = winH; }
            }
#endif
            // ASKED ONCE PER LAUNCH, in the background, when the window is up and not before: the
            // answer is worth a line in the menu bar and nothing if getting it delays the first
            // frame. Gated by the user's switch inside StartOnce. The log line (dev builds only)
            // is marshalled, since the request finishes on another thread.
            UpdateNotice.StartOnce(_vm.ExportSettings,
                                   line => Dispatcher.UIThread.Post(() => _vm.Say(line)));

            // THE OLD TOOLKIT'S FILES, then the Blender add-on. Both are LOOKS at files beside the
            // program or in Blender's settings and neither waits on the game: the removal is the
            // Studio's own folder only (and said in the log), the add-on check changes nothing.
            _vm.RemoveOldToolkitFiles();
            _vm.CheckAddon();

            await _vm.LoadGameAsync();
#if DEV

            // --find <text>: type into the Game Content filter once the game is open. The tree,
            // the kind tags and the match cap are all things a still picture cannot demonstrate,
            // and this is the only way to see them without a mouse.
            var args = Environment.GetCommandLineArgs();

            // WHAT THIS RUN WAS ACTUALLY ASKED FOR. Every flag here is reached by name, and a
            // flag that silently did nothing has cost two rounds of guessing already — once when
            // an argument with a space in it split into two, and once when a block appeared not
            // to run at all. One line at the top ends both arguments.
            if (args.Length > 1) _vm.Say("args: " + string.Join(" ", args.Skip(1)));

            var at = Array.IndexOf(args, "--find");
            if (at >= 0 && at + 1 < args.Length) _vm.GameFilter = args[at + 1];

            // --findgame [--findtype <path>] [--findpress]: run Project > Find the game again…
            // (the real handler, not a copy of it), optionally typing a path into its box and
            // pressing "Use this install". A menu cannot be reached without a mouse, and what
            // happens AFTER the press - the files written, the remount or the restart message - is
            // the part worth checking.
            if (args.Contains("--findgame"))
            {
                var ft = Array.IndexOf(args, "--findtype");
                var typedPath = ft >= 0 && ft + 1 < args.Length ? args[ft + 1] : null;
                var press = args.Contains("--findpress");
                FindGameHook = w => Dispatcher.UIThread.Post(() =>
                {
                    if (typedPath is not null) w.TypePath(typedPath);
                    if (press) w.Press();
                }, DispatcherPriority.Background);
                OnFindGame(null, new RoutedEventArgs());
            }

            // --openmenu <header>: open a top-level menu, so it can be photographed open.
            var om2 = Array.IndexOf(args, "--openmenu");
            if (om2 >= 0 && om2 + 1 < args.Length)
            {
                var wanted = args[om2 + 1];
                var menu = this.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(
                    m => string.Equals((m.Header as string)?.Replace("_", ""), wanted,
                                       StringComparison.OrdinalIgnoreCase));
                if (menu is not null)
                {
                    // The entries, by name, in the log: a popup cannot always be photographed, and a
                    // list in text is the proof that an entry is wired into the menu.
                    _vm.Say($"menu {wanted}: " + string.Join(" | ", menu.Items.OfType<MenuItem>()
                        .Select(m => m.Header as string ?? "?")));
                    Activate();
                    await Task.Delay(400);
                    menu.IsSubMenuOpen = true;
                }
                else _vm.Say($"--openmenu {wanted}: no such menu");
            }

            // --about: open the About window through the menu entry's own handler. It fits or it
            // does not, and only a picture says which. SHOWN, NOT AWAITED, so the run carries on.
            // --aboutcheck on|off presses the update switch in it, as a click would, so what the
            // press WRITES (profiles\settings.json) and what the next start then does can be checked.
            if (args.Contains("--about"))
            {
                var ac = Array.IndexOf(args, "--aboutcheck");
                if (ac >= 0 && ac + 1 < args.Length)
                {
                    var wantOn = args[ac + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
                    AboutHook = w => Dispatcher.UIThread.Post(() => w.PressCheck(wantOn),
                                                              DispatcherPriority.Background);
                }
                OnAbout(null, new RoutedEventArgs());
            }

            // --whatsnew: open the release-notes window through the notice's own handler, once
            // there is a release to have notes. Pair it with --fakeupdate (instant), or with
            // --runningas to wait for the real check to find one (up to 20 s).
            if (args.Contains("--whatsnew"))
            {
                for (var waited = 0; UpdateNotice.Found is null && waited < 200; waited++)
                    await Task.Delay(100);
                if (UpdateNotice.Found is null)
                    _vm.Say("--whatsnew: there is no newer release to show (pair it with --fakeupdate or --runningas)");
                else OnUpdateNotice(null, new RoutedEventArgs());
            }

            // --clickmenu <CreditItem|UpdateNoticeItem|AboutItem>: raise Click on that menu item,
            // which runs the handler the XAML wired to it - the proof that the credit, the notice
            // and the About entry are WIRED, not merely drawn. Only these three: the other two
            // About entries open the browser, which no test may do to the author's screen.
            var cm = Array.IndexOf(args, "--clickmenu");
            if (cm >= 0 && cm + 1 < args.Length)
            {
                var itemName = args[cm + 1];
                var item = itemName is "CreditItem" or "UpdateNoticeItem" or "AboutItem"
                    ? Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(this).OfType<MenuItem>()
                          .FirstOrDefault(m => m.Name == itemName)
                    : null;
                if (item is null) _vm.Say($"--clickmenu {itemName}: refused, or no such menu item");
                else
                {
                    _vm.Say($"--clickmenu {itemName}: raising Click on \"{item.Header}\"");
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
            }

            // --folder <name>: pick a top-level folder, so the list UNDER the tree can be
            // photographed holding something. A still picture of an explorer with nothing
            // selected shows the one state that proves least about it.
            var f = Array.IndexOf(args, "--folder");
            if (f >= 0 && f + 1 < args.Length)
            {
                var want = args[f + 1];
                GameNode? hit = null;
                IEnumerable<GameNode> level = _vm.GameContent;
                foreach (var step in want.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    hit = level.FirstOrDefault(
                        n => string.Equals(n.Name, step, StringComparison.OrdinalIgnoreCase));
                    // NAMES THE STEP THAT MISSED, not the whole path. "no folder Characters" sent
                    // me looking for Characters when the fault was three levels above it.
                    if (hit is null) { _vm.Say($"--folder {want}: no folder named {step}"); break; }
                    // Open it on the way down. The LAST one is left shut, because expanding the
                    // folder you asked to look inside is not what clicking it does.
                    level = hit.Folders;
                    if (level.Any()) hit.IsExpanded = true;
                }

                if (hit is not null)
                {
                    // AFTER the ancestors are open, and after the tree has had a turn to build
                    // their containers. Assigning straight away selects nothing and the tree
                    // falls back to its first row, which then fills the list with the wrong
                    // folder's contents and says nothing about it.
                    var pick = hit;
                    await Dispatcher.UIThread.InvokeAsync(
                        () => _vm.Folder = pick, DispatcherPriority.Background);
                }
            }

            // --open <name>: open an asset in a tab, so the Properties panel can be checked
            // without a mouse. Same reasoning as every other flag here.
            var o = Array.IndexOf(args, "--open");
            if (o >= 0 && o + 1 < args.Length) await _vm.OpenByNameAsync(args[o + 1]);

            // --partsseq "<name>,<name>,...": open each asset in turn the way --open does (a tab each), wait for its
            // viewport to finish building, and print how many GL mesh buffers and textures are alive after each; then
            // close the tabs one by one the way their X does and print the counts again. A viewport that leaks across
            // parts shows it as counts that climb with every part and do not fall when the tabs are closed.
            // --texseq "<name>,<name>,...": open each texture in its own tab the way View does, give each two seconds, and
            // say whether its viewer ever uploaded it (ImageWidth > 0). A viewer that misses its first frame stays blank
            // and says nothing, which is what the author saw on some textures and not others.
            var tseq = Array.IndexOf(args, "--texseq");
            if (tseq >= 0 && tseq + 1 < args.Length)
            {
                var drew = 0; var total = 0;
                var opened = new List<TextureTab>();
                foreach (var name in args[tseq + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    await _vm.OpenByNameAsync(name);
                    await Task.Delay(2000);
                    total++;
                    if (_vm.Tab is TextureTab seen) opened.Add(seen);
                    if (_vm.Tab is TextureTab shown && shown.View.HasImage) drew++;
                    else _vm.Say($"[texseq] {name}: NOT DRAWN ({(_vm.Tab as TextureTab)?.Status ?? "no texture tab"})");
                }
                _vm.Say($"[texseq] {drew} of {total} drew");

                // AND BACK TO EACH, as a person returns to a tab: switching away tears the viewer's GL down.
                var back = 0;
                foreach (var t in opened)
                {
                    _vm.Tab = t;
                    await Task.Delay(1500);
                    if (t.View.HasImage) back++;
                    else _vm.Say($"[texseq] back to {t.Title}: NOT DRAWN");
                }
                _vm.Say($"[texseq] on return {back} of {opened.Count} drew");
            }

            var seq = Array.IndexOf(args, "--partsseq");
            if (seq >= 0 && seq + 1 < args.Length) await PartsSequenceAsync(args[seq + 1].Split(',', StringSplitOptions.RemoveEmptyEntries));

            // --attachseq "<base>,<part>,<part>,...[@<folder>]": open <base> in a tab, then hang each part on it and take it off
            // again in turn, as the author does trying helmets on one torso, printing the live GL textures after every step;
            // with a folder, the base's framebuffer before and after the whole sequence goes to <folder>efore.png and
            // after.png, and the tab is closed at the end. Textures alive must stay at one view's worth.
            var aseq = Array.IndexOf(args, "--attachseq");
            if (aseq >= 0 && aseq + 1 < args.Length) await AttachSequenceAsync(args[aseq + 1]);

            // --tableedit "<table>|<row>|<field>|<value>[|<find>]": choose Edit on a table and type a value
            // into a cell. The value goes into the REAL box on screen and the box is then left, so the
            // XAML's LostFocus and OnEffectNumber's dispatch to a table are what is witnessed, not just
            // the view model method behind them.
            var tedit = Array.IndexOf(args, "--tableedit");
            if (tedit >= 0 && tedit + 1 < args.Length)
                await _vm.DevTableEditAsync(args[tedit + 1], async (cell, text) =>
                {
                    await Task.Delay(1000);   // a layout pass, so the row's box exists

                    // "<reset>": press the row's own Reset button, as a click does.
                    if (text == "<reset>")
                    {
                        var reset = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(
                            b => ReferenceEquals(b.DataContext, cell) && (b.Content as string) == "Reset");
                        if (reset is null) return false;
                        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(400);
                        return true;
                    }

                    var box = this.GetVisualDescendants().OfType<TextBox>()
                                  .FirstOrDefault(t => ReferenceEquals(t.DataContext, cell));
                    if (box is null) return false;
                    box.Focus();
                    await Task.Delay(150);
                    box.Text = text;
                    await Task.Delay(150);
                    // LEAVE THE BOX, as clicking elsewhere does: focus goes to the find box.
                    (this.GetVisualDescendants().OfType<TextBox>()
                         .FirstOrDefault(t => t.Watermark == "Find a row or a field") as Control ?? this).Focus();
                    await Task.Delay(400);
                    return true;
                });

            // --attachedit "<asset fragment>|op|op...": choose Edit on a weapon modification in the
            // Attachments list and do what a person does with its controls: type a number, a name,
            // a text, a price, choose a rarity, press Give it one / Reset / Put it back. Each goes
            // into the REAL control on screen and the control is then left, so the XAML's own
            // handlers (LostFocus, SelectionChanged, Click) are what is witnessed.
            // (More than one is allowed, in order: one flag per modification.)
            for (var aedit = 0; aedit + 1 < args.Length; aedit++)
                if (args[aedit] == "--attachedit")
                    await _vm.DevAttachEditAsync(args[aedit + 1], DriveAttachmentControl);

            // --tabswitch "<mod|ability|table>|<A>|<B>|<text>|<click|focus|list>": open A and B in their
            // editors, type <text> into A's first number box WITHOUT leaving it, then change tab to B
            // the way the strip does (click: the Button's Click alone; focus: the button takes focus
            // first, as a pressed pointer does; list: the tab is set by the code that opens a row),
            // and say where the typed number landed. M-8b: a half-typed box and a tab switch.
            for (var tsw = 0; tsw + 1 < args.Length; tsw++)
                if (args[tsw] == "--tabswitch")
                    await DriveTabSwitchAsync(args[tsw + 1]);

            // --abbox "<asset>|<row fragment>|<text or ~>": type into (or tab through) one number box
            // of an ability's card, and say what the card and the project hold afterwards.
            for (var abx = 0; abx + 1 < args.Length; abx++)
                if (args[abx] == "--abbox")
                    await _vm.DevAbilityBoxAsync(args[abx + 1]);

            // --abmodswitch "<GA_ asset>|<number>|<project.json>": an ability card with a number typed
            // on it, then ANOTHER mod opened under it (the 9a leftover: the card kept the first mod's).
            for (var abs = 0; abs + 1 < args.Length; abs++)
                if (args[abs] == "--abmodswitch")
                    await _vm.DevAbilitySwitchModAsync(args[abs + 1]);

            // --switchmod <project.json>: after --tableedit, open ANOTHER mod with the table card still
            // up, and say what the card shows and what leaving its boxes records into the new mod.
            var swmod = Array.IndexOf(args, "--switchmod");
            if (swmod >= 0 && swmod + 1 < args.Length)
                await _vm.DevSwitchModAsync(args[swmod + 1]);

            // --softlist / --softadd / --softremove <Export>.<Property>[=<asset path>]:
            // read and edit an array of soft references from outside.
            //
            // The list comes first and is not an afterthought: a removal matches on the PATH, so
            // without a way to read the entries out, naming one is guessing at a refusal.
            foreach (var flag in new[] { "--softlist", "--softadd", "--softremove",
                                         "--entrychoices", "--addentry", "--dropentry",
                                         "--expandprop", "--beginedit",
                                         "--dupstruct", "--dropstruct" })
            {
                var sa = Array.IndexOf(args, flag);
                if (sa < 0 || sa + 1 >= args.Length) continue;
                if (_vm.AssetTabOpen is not { } softTab) { _vm.Say($"{flag}: no asset open"); continue; }

                // WAIT FOR Loading, NOT FOR Rows. A tab shows a few rows while its package is
                // still being read, so "Rows is not empty" is true long before the tree is the
                // whole asset -- and a lookup in that window reports "no rows carry that export",
                // which reads exactly like the export not existing.
                for (var spin = 0; spin < 100 && softTab.Loading; spin++)
                    await Task.Delay(100);

                var spec = args[sa + 1];
                var eq = spec.IndexOf('=');
                var head = eq < 0 ? spec : spec[..eq];
                var value = eq < 0 ? "" : spec[(eq + 1)..];
                var dot = head.LastIndexOf('.');
                if (dot < 0) { _vm.Say($"{flag} {spec}: expected <Export>.<Property>"); continue; }

                var softRow = softTab.AllRows.FirstOrDefault(
                    r => r.Export == head[..dot] && r.Name == head[(dot + 1)..]);
                if (softRow is null)
                {
                    // SAY WHAT WOULD HAVE WORKED. "No property X on Y" about a property that is
                    // plainly on screen sends somebody looking for a bug in the writer, which is
                    // exactly the hour --open's contains-match cost earlier today.
                    var onIt = softTab.AllRows.Where(r => r.Export == head[..dot]).ToList();
                    _vm.Say($"{flag} {spec}: no property {head[(dot + 1)..]} on {head[..dot]} — " +
                            (onIt.Count > 0
                                 ? $"that export has {string.Join(", ", onIt.Select(r => r.Name))}"
                                 : $"no rows carry that export, of {softTab.AllRows.Count:N0}; the exports are " + string.Join(", ", softTab.AllRows.Select(r => r.Export).Where(x => x.Length > 0).Distinct().Take(12))));
                    continue;
                }

                // --entrychoices / --addentry / --dropentry press the + and the × that the
                // PROPERTY GRID now carries, rather than calling the writer as --softadd does.
                //
                // THEY ARE NOT THE SAME PATH and that is the whole point. The buttons work out
                // the object path a soft CLASS reference needs by opening the chosen asset, and
                // they stamp the array row with what the mod did to it -- neither of which the
                // flags beside them touch. A button only a mouse can reach is a button no check
                // ever exercises, which is this file's oldest rule.
                switch (flag)
                {
                    case "--softlist": _vm.ListSoftRefs(softTab, softRow); break;

                    // DOUBLE-CLICKING A VALUE, from outside. --set skips this entirely: it calls
                    // ApplyEdit with a string, so a row that cannot be PUT into edit mode still
                    // takes an edit from a flag and looks fine. The box is the part a person
                    // actually uses, and it has its own gate.
                    case "--beginedit":
                        softRow.BeginEdit();
                        _vm.Say($"{softRow.Export}.{softRow.Name} [{softRow.Node.Type}]: " +
                                (softRow.Editing
                                     ? $"editing, box reads “{softRow.Draft}”"
                                     : "REFUSED the edit box"));
                        break;

                    // OPEN THE FOLD. An array's ENTRIES are where the × lives, and a shut fold
                    // emits no rows for them -- so without this there is no way to photograph
                    // the remove button, or to click it from outside.
                    // --dupstruct <Export>.<ArrayPath> / --dropstruct <Export>.<ArrayPath>=<i>:
                    // the struct array buttons, from outside.
                    case "--dupstruct":
                        _vm.DupStructEntry(softTab, softRow);
                        break;

                    case "--dropstruct" when value.Length > 0:
                    {
                        // THE ELEMENT ROW, not the array -- the × is pressed on the thing being
                        // dropped, and DropStructEntry reads the index off that row's own name.
                        var element = softTab.ChildrenOf(softRow)
                            .FirstOrDefault(k => k.Name == $"[{value}]");
                        if (element is null)
                            _vm.Say($"{flag} {spec}: no element [{value}] — it holds " +
                                    $"{softTab.ChildrenOf(softRow).Count:N0}");
                        else _vm.DropStructEntry(softTab, element);
                        break;
                    }

                    case "--expandprop":
                        softRow.IsExpanded = true;
                        _vm.Say($"{softRow.Export}.{softRow.Name}: open, " +
                                $"{softTab.ChildrenOf(softRow).Count:N0} entr(ies)");
                        break;

                    case "--entrychoices":
                    {
                        var (choices, note) = _vm.RefCandidatesFor(softTab, softRow);
                        _vm.Say($"{softRow.Export}.{softRow.Name}: {choices.Count:N0} candidate(s)");
                        _vm.Say($"    {note}");
                        foreach (var c in choices.Take(5)) _vm.Say($"    {c.Name}");
                        break;
                    }

                    case "--dropentry" when value.Length > 0:
                    {
                        // THE ENTRY, not the array. A × is pressed on the row being removed,
                        // and that row is a CHILD -- its own Export is empty, so it can only be
                        // found underneath the array it belongs to.
                        var entry = softTab.ChildrenOf(softRow).FirstOrDefault(
                            k => string.Equals(k.Value, value, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(k.Target, value, StringComparison.OrdinalIgnoreCase));
                        if (entry is null)
                            _vm.Say($"{flag} {spec}: no entry reads '{value}' — it holds " +
                                    string.Join(", ", softTab.ChildrenOf(softRow).Select(k => k.Value)));
                        else _vm.DropRefEntry(softTab, entry);
                        break;
                    }

                    case "--addentry" when value.Length > 0:
                        _vm.AddRefEntry(softTab, softRow, value);
                        break;

                    case "--softadd" when value.Length > 0:
                        _vm.ApplyValue(softTab, softRow, "softadd", "", value);
                        break;

                    case "--softremove" when value.Length > 0:
                        _vm.ApplyValue(softTab, softRow, "softremove", value, "");
                        break;

                    default: _vm.Say($"{flag} {spec}: expected =<asset path>"); break;
                }

                // WHAT THE ROW NOW SAYS, so a flag run reports the same thing a person would see.
                if (softRow.ShowPending) _vm.Say($"    row reads: {softRow.Note}   {softRow.Pending}");
            }

            // --follow <property fragment>: press the follow arrow on the open asset's row.
            //
            // The whole point of the arrow is that it RESOLVES, and a string that looks like a
            // path is not the same as a package the game actually holds. This walks the real
            // chain -- extract, match a leaf, confirm the converted package name -- and opens a
            // tab, so a photograph shows the destination rather than the intent.
            var fol = Array.IndexOf(args, "--follow");
            if (fol >= 0 && fol + 1 < args.Length)
            {
                var want = args[fol + 1];
                if (_vm.AssetTabOpen is not { } ft) _vm.Say("--follow: no asset open");
                else if (ft.Rows.FirstOrDefault(
                             r => r.CanFollow &&
                                  r.Name.Contains(want, StringComparison.OrdinalIgnoreCase)) is not { } fr)
                    _vm.Say($"--follow: no followable row matching '{want}' among {ft.Rows.Count:N0}");
                else
                {
                    _vm.Say($"--follow: {fr.Name} -> {fr.Target}");
                    await _vm.FollowAsync(fr);
                }
            }

            // --addoverlay <table fragment> [row field now]: lay one of the game's tables over,
            // which is the game's own mechanism and the one thing no mod we have built uses.
            //
            // REPEATS HONOURED, like --attach and --play. One overlay proves the array can hold
            // one; TWO is what proves the action carries a list, and a mod that covers two tables
            // is the first thing anyone will actually want.
            for (var aov = 0; aov < args.Length; aov++)
            {
                if (args[aov] != "--addoverlay" || aov + 1 >= args.Length) continue;
                var haveAll = aov + 4 < args.Length && !args[aov + 2].StartsWith("--") &&
                              !args[aov + 3].StartsWith("--") &&
                              double.TryParse(args[aov + 4], out _);
                _vm.AddOverlay(args[aov + 1],
                               haveAll ? args[aov + 2] : null,
                               haveAll ? args[aov + 3] : null,
                               haveAll ? double.Parse(args[aov + 4]) : null);
            }

            // --uefidelity <fragment>: the UE5 Details panel this asset would get, beside the
            // property view we show. Repeats honoured, because the interesting comparison is a
            // native DataAsset against a Blueprint-generated one.
            for (var fi = 0; fi < args.Length; fi++)
                if (args[fi] == "--uefidelity" && fi + 1 < args.Length)
                    _vm.UeFidelityReport(args[fi + 1]);

            // --attach <fragment> [more...]: hang meshes on the open one, left to right.
            //
            // Repeats honoured, like --play and --partpiece, because the thing worth proving is a
            // TORSO WEARING FOUR PIECES rather than one -- and the second attach is where a
            // shared bone texture or a stale palette would show.
            for (var ai = 0; ai < args.Length; ai++)
            {
                if (args[ai] != "--attach" || ai + 1 >= args.Length) continue;
                if (_vm.Tab is not MeshTab am)
                { _vm.Say("--attach: no mesh open (--open <mesh name>)"); break; }

                var want = args[ai + 1];
                var hit = _vm.AttachCandidates(am).FirstOrDefault(
                    c => c.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
                if (hit.Path is null)
                    _vm.Say($"--attach: nothing matching '{want}' to hang on {am.LoadedName}");
                else await _vm.AttachMeshAsync(am, hit.Path, hit.Name);
            }

            // --dumpmeshes: what the Meshes tab is really holding, with the MEASURED bone
            // overlap. The only witness an attachment has that is not a picture -- and the
            // picture cannot tell a helmet that is posed with the body from one sitting at its
            // own bind in the same place.
            if (args.Contains("--dumpmeshes") && _vm.Tab is MeshTab dm)
            {
                var waited = 0;
                while (dm.View.Attached.Count < args.Count(a => a == "--attach") && waited < 30_000)
                { await Task.Delay(500); waited += 500; }

                dm.RefreshAttached();
                _vm.Say($"loaded: {dm.LoadedName}");
                foreach (var row in dm.AttachedMeshes) _vm.Say($"  + {row.Name}  [{row.Fits}]");
                if (dm.AttachedMeshes.Count == 0) _vm.Say($"  (nothing attached after {waited / 1000}s)");
            }

            // --grid on|off, --floor on|off: the two scene toggles, for a capture.
            //
            // Worth driving because neither writes anything: a toggle that does nothing looks
            // exactly like one that works, and the only witness either has is a picture.
            foreach (var (flag, floor) in new[] { ("--grid", false), ("--floor", true) })
            {
                var sceneAt = Array.IndexOf(args, flag);
                if (sceneAt < 0 || _vm.Tab is not MeshTab gm) continue;
                var on = !(sceneAt + 1 < args.Length &&
                           args[sceneAt + 1].Equals("off", StringComparison.OrdinalIgnoreCase));
                if (floor) gm.ShowFloor = on; else gm.ShowGrid = on;
                _vm.Say($"{flag}: {(on ? "on" : "off")}");
            }

            // --animopen <fragment>: unfold a set, so --dumpanims can see inside one.
            //
            // Without it the readback can only ever witness the folded view, which is the half
            // that was already working.
            var ao = Array.IndexOf(args, "--animopen");
            if (ao >= 0 && ao + 1 < args.Length && _vm.Tab is MeshTab openMesh)
            {
                var want = args[ao + 1];
                var waited = 0;
                while (openMesh.AnimTotal == 0 && waited < 90_000)
                { await Task.Delay(500); waited += 500; }

                if (openMesh.SetNames.FirstOrDefault(
                        s => s.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } setName)
                    openMesh.ToggleAnimCategory(setName);
                else
                    _vm.Say($"--animopen: no set matching '{want}' among " +
                            string.Join(", ", openMesh.SetNames));
            }

            // --dumpanims [n]: print the VISIBLE animation list, folds and all.
            //
            // The only witness a fold has. Nothing is written to the project by collapsing a set,
            // so every other check in this harness passes whether it worked or not -- and the one
            // time it silently did not, a screenshot was the only thing that showed it.
            var da = Array.IndexOf(args, "--dumpanims");
            if (da >= 0)
            {
                var take = da + 1 < args.Length && int.TryParse(args[da + 1], out var dn) ? dn : 40;
                if (_vm.Tab is MeshTab dumpMesh)
                {
                    // IT WAITS, because the list is filled off a background rig match that starts
                    // when the mesh finishes loading -- and reading it the instant --open returns
                    // printed "0 visible of 0", which is not "the fold is broken", it is "the
                    // answer was not there yet". Exactly the read-before-the-data shape this
                    // harness has been caught by before, so the timeout SAYS so rather than
                    // printing an empty list as though it were a result.
                    var waited = 0;
                    while (dumpMesh.AnimTotal == 0 && waited < 90_000)
                    { await Task.Delay(500); waited += 500; }

                    if (dumpMesh.AnimTotal == 0)
                        _vm.Say($"--dumpanims: nothing after {waited / 1000}s — {dumpMesh.AnimNote}");
                    else _vm.Say(dumpMesh.DumpAnimations(take));
                }
                else _vm.Say("--dumpanims: no mesh open (--open <mesh name>)");
            }

            // --animfold [row] [times]: fold ONE heading, several times over, THROUGH THE CONTROL,
            // printing the visible list after each.
            //
            // THE CHECK THIS HARNESS COULD NOT MAKE, and the reason a broken fold shipped. Every
            // other animation flag calls ToggleAnimCategory directly, which is the half that
            // always worked; the half that broke was the CLICK never arriving at it. So this goes
            // the whole way round: it finds the list, asks it for the container of a row, finds
            // the Button the template puts there and raises Click on that. A fold that cannot be
            // repeated shows up here as a list that stops changing on the second press, which is
            // exactly what the author reported and what nothing here could see.
            var af = Array.IndexOf(args, "--animfold");
            if (af >= 0)
            {
                if (_vm.Tab is not MeshTab foldMesh)
                    _vm.Say("--animfold: no mesh open (--open <mesh name>)");
                else
                {
                    var rowAt = af + 1 < args.Length && int.TryParse(args[af + 1], out var fi) ? fi : 0;
                    var times = af + 2 < args.Length && int.TryParse(args[af + 2], out var ft) ? ft : 3;

                    var waited = 0;
                    while (foldMesh.AnimTotal == 0 && waited < 90_000)
                    { await Task.Delay(500); waited += 500; }

                    var list = this.GetVisualDescendants().OfType<ListBox>()
                                   .FirstOrDefault(l => ReferenceEquals(l.ItemsSource,
                                                                        foldMesh.Animations));
                    if (foldMesh.AnimTotal == 0)
                        _vm.Say($"--animfold: nothing after {waited / 1000}s — {foldMesh.AnimNote}");
                    else if (list is null)
                        _vm.Say("--animfold: the animation list is not on screen");
                    else
                        for (var i = 1; i <= times; i++)
                        {
                            // Layout has to have run or there is no container to click.
                            await Task.Delay(400);
                            var row = list.ContainerFromIndex(rowAt);
                            var hit = row?.GetVisualDescendants().OfType<Button>().FirstOrDefault();
                            if (hit is null)
                            { _vm.Say($"--animfold press {i}: row {rowAt} has no button"); break; }

                            // THE DEAD STRIP, MEASURED. The button is what folds; the row is
                            // what the mouse aims at. Any difference between the two is a band
                            // that looks clickable and is not, which is what the author caught in
                            // the hover overlay. Reported every press because a style change can
                            // silently give it back.
                            if (row is Visual rv)
                                _vm.Say($"--animfold row {rowAt}: button {hit.Bounds.Width:F0}x" +
                                        $"{hit.Bounds.Height:F0} in row {rv.Bounds.Width:F0}x" +
                                        $"{rv.Bounds.Height:F0}, dead strip " +
                                        $"{(rv.Bounds.Width - hit.Bounds.Width) / 2:F0}px each side, " +
                                        $"{(rv.Bounds.Height - hit.Bounds.Height) / 2:F0}px top and bottom");

                            hit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            await Task.Delay(400);
                            _vm.Say($"--animfold press {i} on row {rowAt}:");
                            _vm.Say(foldMesh.DumpAnimations(3));
                        }
                }
            }

            // --animhit: WHAT A CLICK AT EACH X ACTUALLY LANDS ON, across the left edge of a
            // fold row, plus the whole visual chain from the list down to the button.
            //
            // The author reported a dead strip twice. The first time a bounds comparison found it
            // and fixing it reported "0px each side" -- and he then photographed the SAME strip
            // still there. So the bounds comparison was answering a narrower question than the one
            // being asked: it compared the button against ITS OWN container, which says nothing
            // about the inset that container sits in. This asks the real question instead, the
            // only way it can be asked without a mouse: hand a point to the hit tester and print
            // what comes back.
            if (args.Contains("--animhit"))
            {
                if (_vm.Tab is not MeshTab hitMesh)
                    _vm.Say("--animhit: no mesh open (--open <mesh name>)");
                else
                {
                    var waited = 0;
                    while (hitMesh.AnimTotal == 0 && waited < 90_000)
                    { await Task.Delay(500); waited += 500; }
                    await Task.Delay(600);

                    var list = this.GetVisualDescendants().OfType<ListBox>()
                                   .FirstOrDefault(l => ReferenceEquals(l.ItemsSource,
                                                                        hitMesh.Animations));
                    if (list?.ContainerFromIndex(0) is not Visual row)
                        _vm.Say("--animhit: no row 0 to measure");
                    else
                    {
                        var fold = row.GetVisualDescendants().OfType<Button>().FirstOrDefault();

                        _vm.Say("--animhit CHAIN (each bounds is relative to its own parent):");
                        for (Visual? v = fold; v is not null; v = v.GetVisualParent())
                        {
                            var pad = v is Avalonia.Controls.Primitives.TemplatedControl tpl ? $" padding {tpl.Padding}" : "";
                            var mar = v is Avalonia.Layout.Layoutable la ? $" margin {la.Margin}" : "";
                            _vm.Say($"    {v.GetType().Name,-22} " +
                                    $"x{v.Bounds.X:F0} w{v.Bounds.Width:F0} h{v.Bounds.Height:F0}" +
                                    pad + mar);
                            if (v is Window) break;
                        }

                        // SELECTED, so a screenshot shows the highlight the author has been
                        // photographing. It is drawn to the row's real bounds, which makes it the
                        // one visible witness this fault has.
                        list.SelectedIndex = 0;

                        _vm.Say("--animhit WHAT A CLICK LANDS ON, by x within the row:");
                        foreach (var x in new[] { -16, -12, -8, -6, -4, -3, -2, -1, 0, 2, 6, 30 })
                        {
                            var win = row.TranslatePoint(new Point(x, row.Bounds.Height / 2), this);
                            if (win is not { } p) { _vm.Say($"    x={x,-4} (cannot translate)"); continue; }

                            var got = this.InputHitTest(p) as Visual;
                            // Walk up: what the mouse hits is usually a presenter INSIDE the
                            // button, and what matters is whether the button is on that path.
                            var folds = false;
                            for (Visual? v = got; v is not null; v = v.GetVisualParent())
                                if (ReferenceEquals(v, fold)) { folds = true; break; }

                            _vm.Say($"    x={x,-4} {got?.GetType().Name ?? "nothing",-22} " +
                                    (folds ? "FOLDS" : "does not fold"));
                        }
                    }
                }
            }

            // --treehit: the Data Explorer's expander, MEASURED rather than guessed at.
            //
            // The obvious fix for a 12px arrow in a 36px column is a style on the theme's own
            // template part, and the obvious way to write one is to name the part from memory.
            // A selector that names a part which does not exist matches nothing and reports
            // nothing, so the wrong guess and the right one look identical from here. This prints
            // the real names, the real boxes, and what a click at each x actually reaches.
            if (args.Contains("--treehit"))
            {
                await Task.Delay(2000);
                var tree = this.GetVisualDescendants().OfType<TreeView>().FirstOrDefault();
                // BOTH KINDS OF ROW. A folder that can be opened reserves the expander; one
                // that cannot may not, and then its name starts further left than its siblings'.
                // The only way to know is to measure the header of each and compare.
                foreach (var kind in new[] { "branch", "leaf" })
                {
                    var probe = tree?.GetVisualDescendants().OfType<TreeViewItem>()
                                    .FirstOrDefault(t => kind == "branch" ? t.ItemCount > 0
                                                                           : t.ItemCount == 0);
                    var head = probe?.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                                    .FirstOrDefault(c => c.Name == "PART_HeaderPresenter");
                    var box = probe?.GetVisualDescendants().OfType<Panel>()
                                   .FirstOrDefault(c => c.Name == "PART_ExpandCollapseChevronContainer");
                    _vm.Say($"--treehit {kind,-7} header starts x{head?.Bounds.X ?? -1:F0}, " +
                            $"chevron box w{box?.Bounds.Width ?? -1:F0}");
                    if (probe is not null)
                        foreach (var v in probe.GetVisualDescendants().Take(10))
                        {
                            var nm = (v as Control)?.Name;
                            var vis = v is Visual vv ? (vv.IsVisible ? "shown" : "HIDDEN") : "?";
                            _vm.Say($"      {kind,-7} {v.GetType().Name,-18} " +
                                    $"{(nm ?? "(unnamed)"),-34} x{v.Bounds.X:F0} " +
                                    $"w{v.Bounds.Width:F0} {vis}");
                        }
                }

                var item = tree?.GetVisualDescendants().OfType<TreeViewItem>()
                               .FirstOrDefault(t => t.ItemCount > 0);
                if (item is null)
                    _vm.Say("--treehit: no expandable tree row on screen");
                else
                {
                    _vm.Say($"--treehit row: {item.Bounds.Width:F0}x{item.Bounds.Height:F0}");
                    _vm.Say("--treehit PARTS (name is what a /template/ selector must match):");
                    foreach (var v in item.GetVisualDescendants().Take(24))
                    {
                        var nm = (v as Control)?.Name;
                        _vm.Say($"    {v.GetType().Name,-22} {(nm ?? "(unnamed)"),-32} " +
                                $"x{v.Bounds.X:F0} y{v.Bounds.Y:F0} " +
                                $"w{v.Bounds.Width:F0} h{v.Bounds.Height:F0}");
                    }

                    _vm.Say("--treehit WHAT A CLICK LANDS ON, by x across the row:");
                    foreach (var x in new[] { 1, 3, 6, 9, 12, 15, 18, 22, 26, 30, 36, 44 })
                    {
                        var p = item.TranslatePoint(new Point(x, 10), this);
                        if (p is not { } where) { _vm.Say($"    x={x,-4} (cannot translate)"); continue; }

                        var got = this.InputHitTest(where) as Visual;
                        var expands = false;
                        for (Visual? v = got; v is not null; v = v.GetVisualParent())
                            if (v is Avalonia.Controls.Primitives.ToggleButton) { expands = true; break; }

                        _vm.Say($"    x={x,-4} {got?.GetType().Name ?? "nothing",-22} " +
                                (expands ? "EXPANDS" : "does not expand"));
                    }
                }
            }

            // --deftint: apply each material's OWN tint colours, which is off by default. See
            // RenderMesh.DefaultTintEnabled for the channel-order question that keeps it off.
            if (args.Contains("--deftint"))
            {
                UEToolkit.App.Rendering.RenderMesh.DefaultTintEnabled = true;
                _vm.Say("--deftint: materials paint their own tint colours");
            }

            // --maskchannels: count the UPLOADED tint mask's channels, through the GL context
            // the shader samples it from. See ModelView.CountMasks for why a TGA could not settle
            // this: the question is which channel the SHADER sees, and TGA stores BGR.
            if (args.Contains("--maskchannels"))
            {
                if (_vm.Tab is not MeshTab maskMesh) _vm.Say("--maskchannels: no mesh open");
                else
                {
                    maskMesh.AskMaskChannels();
                    var waited = 0;
                    while (maskMesh.MaskReport.Length == 0 && waited < 15_000)
                    { await Task.Delay(250); waited += 250; }
                    _vm.Say("--maskchannels:");
                    _vm.Say("    " + (maskMesh.MaskReport.Length > 0
                        ? maskMesh.MaskReport
                        : $"(no answer after {waited / 1000}s — did a frame draw?)"));
                }
            }

            // --tintdebug: draw the tint mask as the SHADER samples it, rather than shading.
            if (args.Contains("--tintdebug"))
            {
                UEToolkit.App.Rendering.RenderMesh.TintDebug = true;
                _vm.Say("--tintdebug: drawing the sampled tint mask");
            }

            // --shadows on|off: the toggle, for a capture. Like --grid and --floor it writes
            // nothing, so a toggle that does nothing looks exactly like one that works.
            var sh = Array.IndexOf(args, "--shadows");
            if (sh >= 0 && _vm.Tab is MeshTab shTab)
            {
                var on = !(sh + 1 < args.Length &&
                           args[sh + 1].Equals("off", StringComparison.OrdinalIgnoreCase));
                shTab.ShowShadows = on;
                _vm.Say($"--shadows: {(on ? "on" : "off")}");
            }

            // --shading: what each section of the open mesh has to shade WITH.
            //
            // The first question a "renders wrong" report needs answered is whether there is
            // anything to read. An unbound diffuse and a misread one look the same lit and are
            // completely different faults.
            if (args.Contains("--shading"))
            {
                if (_vm.Tab is not MeshTab shadeMesh) _vm.Say("--shading: no mesh open");
                else
                {
                    // AND WHAT THE DEFAULT TINT DID, which is the whole of the visor question:
                    // with nobody choosing a colour the material paints itself, and the only way
                    // to see which colours it used is to record the draw.
                    UEToolkit.App.Rendering.RenderMesh.RecordTintDraw = true;
                    shadeMesh.View.RequestNextFrameRendering();
                    await Task.Delay(800);
                    _vm.Say("--shading:");
                    _vm.Say("    " + shadeMesh.ShadingCensus);
                    _vm.Say("    default tint: " +
                            (shadeMesh.TintDraw.Length > 0 ? shadeMesh.TintDraw : "(nothing painted)"));
                }
            }

            // --set <Export>.<Property>=<value>: make an edit without a mouse.
            //
            // The ONE flag that can witness stage two. Everything else here proves what is on
            // screen; this proves that a number typed into the panel reaches the project only
            // after the build's own write has agreed to it, and that is not a thing a screenshot
            // can show.
            var st = Array.IndexOf(args, "--set");
            // SAY WHY IT DID NOT RUN. This block produced no output at all when the guard was
            // false -- not a success, not a failure, nothing -- and a flag that is silent when it
            // cannot act is indistinguishable from one that worked. An hour went into reading the
            // parsing inside it while the actual answer was that it never ran.
            if (st >= 0 && (st + 1 >= args.Length || _vm.AssetTabOpen is null))
                _vm.Say($"--set: {(st + 1 >= args.Length ? "nothing after the flag" : "no asset tab is open")}");
            if (st >= 0 && st + 1 < args.Length && _vm.AssetTabOpen is { } setTab)
            {
                // WAIT FOR Loading, not for the first row. A tab's properties are a package read
                // and arrive after the tab does, and it shows some of them while still reading --
                // so "Rows is not empty" is true long before the tree is the whole asset. A --set
                // landing in that window reports "no property X on Y" about a property that is
                // there, which reads as a missing feature rather than as a race.
                for (var spin = 0; spin < 100 && setTab.Loading; spin++)
                    await Task.Delay(100);

                var spec = args[st + 1];
                var eq = spec.LastIndexOf('=');
                // THE FIRST DOT, not the last one before the '='. An export name cannot contain a
                // dot and a property PATH can -- `UIData.Name` is a property inside a struct, and
                // splitting at the last dot made the export "Default__..._C.UIData", which is
                // nowhere, and reported it as a missing property.
                var dot = eq > 0 ? spec.IndexOf('.') : -1;
                if (dot > eq) dot = -1;
                if (eq < 0 || dot < 0)
                    _vm.Say($"--set {spec}: expected <Export>.<Property>=<value>");
                else
                {
                    var export = spec[..dot];
                    var prop = spec[(dot + 1)..eq];
                    // BY ADDRESS, not by export and name. A row's Name is its own last segment
                    // -- an element is "[0]", a struct's field is "Name" -- so a nested property
                    // could never be found by (Export, Name) and was reported missing. Address
                    // spells the whole path the way Locate takes it.
                    //
                    // ALL of them, not the visible ones, for the same reason as everywhere else:
                    // a property inside a shut fold is not in Rows.
                    var row = setTab.AllRows.FirstOrDefault(
                        r => r.Address is var (rx, rp) && rx is not null &&
                             string.Equals(rx, export, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(rp, prop, StringComparison.OrdinalIgnoreCase));
                    if (row is null) _vm.Say($"--set {spec}: no property {prop} on {export}");
                    else _vm.ApplyEdit(setTab, row, spec[(eq + 1)..]);
                }
            }

            // --flat / --modified: the two toolbar controls, from outside.
            if (args.Contains("--flat")) _vm.Flat = true;
            if (args.Contains("--modified")) _vm.OnlyModified = true;

            // --section <Name>: open one of the strip's sections, so its list can be
            // photographed. Home Page is "Home".
            var sec = Array.IndexOf(args, "--section");
            if (sec >= 0 && sec + 1 < args.Length)
            {
                var want = args[sec + 1];
                var hit = _vm.SectionTabs.FirstOrDefault(
                    t => t.Kind.ToString().Equals(want, StringComparison.OrdinalIgnoreCase));
                if (hit is null) _vm.Say($"--section {want}: no section by that name");
                else await _vm.ShowSectionAsync(hit.Kind);
            }

            // --hdrpick: does the list and the view model AGREE about what is selected?
            //
            // SectionPick used to REFUSE a heading, in silence. The ListBox did not know that:
            // it went on holding the heading as its selection while the view model held the row
            // picked before it, and every context menu command read the view model. One row lit,
            // a different row acted on, and nothing on screen saying so.
            //
            // Two corrections were tried from the setter and this probe REFUTED BOTH, which is
            // the whole reason it exists: it asks the list AND the view model AND what a command
            // would actually reach, rather than trusting any one of them.
            if (args.Contains("--hdrpick"))
            {
                await Task.Delay(1200);
                var list = this.GetVisualDescendants().OfType<ListBox>()
                               .FirstOrDefault(l => ReferenceEquals(l.ItemsSource, _vm.SectionRows));
                var header = _vm.SectionRows.FirstOrDefault(r => r.IsHeader);
                var plain = _vm.SectionRows.FirstOrDefault(r => !r.IsHeader);

                if (list is null || header is null || plain is null)
                    _vm.Say($"--hdrpick: nothing to test (list {(list is null ? "no" : "yes")}, " +
                            $"{_vm.SectionRows.Count} row(s), heading {(header is null ? "no" : "yes")})");
                else
                {
                    // Land on a real row first, so there is something to be wrongly acted upon.
                    list.SelectedItem = plain;
                    await Task.Delay(300);
                    _vm.Say($"--hdrpick picked a row: list='{(list.SelectedItem as SectionItem)?.Name}' " +
                            $"vm='{_vm.SectionPick?.Name}'");

                    list.SelectedItem = header;
                    await Task.Delay(300);
                    var onList = (list.SelectedItem as SectionItem)?.Name ?? "(none)";
                    var onVm = _vm.SectionPick?.Name ?? "(none)";
                    var acts = _vm.Chosen?.Name ?? "(nothing)";
                    _vm.Say($"--hdrpick then a heading: list='{onList}' vm='{onVm}' -> " +
                            (onList == onVm ? "AGREE" : "DISAGREE"));
                    _vm.Say($"--hdrpick the menu would act on: '{acts}' -> " +
                            (acts == "(nothing)" ? "SAFE" : "WRONG ROW"));
                }
            }

            // --extractwin <fragment>: select a folder in the tree and open the extract window.
            //
            // SHOWN, NOT SHOWN MODALLY, so the flag can carry on and a screenshot can catch it.
            // The window is the whole of what is new here: BatchExporter.ExportPaths is long
            // proven and the exporter has always written by package path, so what needed
            // witnessing is that this dialog loads, counts the folder and seeds the formats.
            var ex = Array.IndexOf(args, "--extractwin");
            if (ex >= 0 && ex + 1 < args.Length)
            {
                var want = args[ex + 1];
                var node = _vm.Tree.SelectMany(Walk)
                                   .FirstOrDefault(n => n.FullPath.Contains(want,
                                       StringComparison.OrdinalIgnoreCase));
                if (node is null) _vm.Say($"--extractwin: no folder matching '{want}'");
                else if (_vm.Provider is not { } prov) _vm.Say("--extractwin: game not open");
                else
                {
                    _vm.Folder = node;
                    var files = node.AllAssets.ToList();
                    _vm.Say($"--extractwin: {node.FullPath} holds {files.Count:N0} file(s)");
                    var win = new ExtractWindow(node.FullPath, files, prov,
                                                _vm.ExportSettings, _vm.Say);
                    win.Show(this);

                    // A SECOND ARGUMENT RUNS IT. Opening the window proves it loads; only this
                    // proves it writes, which is the half a screenshot cannot show.
                    if (ex + 2 < args.Length && !args[ex + 2].StartsWith("--"))
                    { win.SetDestination(args[ex + 2]); await win.Extract(); }
                }

                static IEnumerable<GameNode> Walk(GameNode n)
                {
                    yield return n;
                    foreach (var c in n.Folders)
                        foreach (var d in Walk(c)) yield return d;
                }
            }

            // --sound <fragment> [dest]: open the Sounds section, open one sound, PLAY it, and
            // with a destination keep it.
            //
            // PLAYING IS THE ONLY WITNESS THAT MATTERS and it is the one a screenshot cannot
            // give: the panel looks identical whether the decode worked or the Wwise subprocess
            // failed, because the failure lands in a note. So this reports the decoded path, the
            // duration the player read off the file, and whether the copy landed.
            var snd = Array.IndexOf(args, "--sound");
            if (snd >= 0 && snd + 1 < args.Length)
            {
                await _vm.ShowSectionAsync(SectionKind.Sounds);
                _vm.Say($"--sound: {_vm.SoundStatus}");

                var want = args[snd + 1];
                if (_vm.FindSound(want) is not { } entry)
                    _vm.Say($"--sound: nothing matching '{want}'");
                else
                {
                    _vm.OpenSectionItem(new SectionItem { Name = entry.Name, Payload = entry });
                    if (_vm.Tab is not SoundTab sndTab) _vm.Say("--sound: no sound tab opened");
                    else
                    {
                        await _vm.PlaySound(sndTab);
                        await Task.Delay(700);
                        _vm.Say($"--sound: {sndTab.Entry.Name} playing={sndTab.Playing} " +
                                $"clock={(sndTab.Clock.Length > 0 ? sndTab.Clock : "(none)")} " +
                                $"wav={(sndTab.Wav.Length > 0 ? sndTab.Wav : "(none)")}");
                        _vm.Say($"--sound: note is '{sndTab.Note}'");

                        if (snd + 2 < args.Length && !args[snd + 2].StartsWith("--"))
                        {
                            _vm.ExtractSound(sndTab, args[snd + 2]);
                            _vm.Say($"--sound: kept={File.Exists(args[snd + 2])}");
                        }
                    }
                }
            }

            // --details: open the panel on the PROJECT itself. No argument — a Studio has one
            // mod open, and the one it has open is the one whose details these are.
            if (args.Contains("--details"))
            {
                _vm.OpenModDetails();
                if (_vm.Tab is ModTab opened) DriveMod(opened);
            }

            // --editmission <id fragment>: open a mission this project holds.
            var emi = Array.IndexOf(args, "--editmission");
            if (emi >= 0 && emi + 1 < args.Length)
            {
                var want = args[emi + 1];
                if (_vm.MissionsInMod.FirstOrDefault(
                        m => m.Id.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                             m.Display.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit)
                {
                    await _vm.EditMission(hit);
                    if (_vm.Tab is MissionTab { Mine: not null } opened) await DriveMission(opened);
                }
                else
                    _vm.Say($"--editmission: no mission matching '{want}' — " +
                            string.Join(", ", _vm.MissionsInMod.Select(m => m.Id)));
            }

            // --newmission <leaf fragment> [inplace]: duplicate one of the game's.
            var nmi = Array.IndexOf(args, "--newmission");
            if (nmi >= 0 && nmi + 1 < args.Length)
            {
                var want = args[nmi + 1];
                var inPlace = nmi + 2 < args.Length && args[nmi + 2] == "inplace";
                var all = await _vm.GameMissionsAsync();
                var hit = all.FirstOrDefault(
                              m => m.Leaf.Contains(want, StringComparison.OrdinalIgnoreCase))
                          ?? all.FirstOrDefault(
                              m => m.Display.Contains(want, StringComparison.OrdinalIgnoreCase));

                if (hit is null)
                    _vm.Say($"--newmission: no mission matching '{want}' among {all.Count}");
                else
                {
                    await _vm.DuplicateMission(hit, inPlace);
                    if (_vm.Tab is MissionTab { Mine: not null } opened) await DriveMission(opened);
                }
            }

            // --editenemy <group fragment>: open a group of enemies on its numbers.
            //
            // MATCHED ON THE DISPLAY NAME, which is what the section list shows — "Marauder
            // (Generic)" rather than CPD_TacticalSpec_Enemy_Generic_Marauder. A check should
            // name a row the way somebody clicking it would.
            var ee = Array.IndexOf(args, "--editenemy");
            if (ee >= 0 && ee + 1 < args.Length)
            {
                var want = args[ee + 1];
                var groups = await _vm.EnemyGroupsAsync();
                var hit = groups.FirstOrDefault(
                              g => g.Display.Contains(want, StringComparison.OrdinalIgnoreCase))
                          ?? groups.FirstOrDefault(
                              g => g.Leaf.Contains(want, StringComparison.OrdinalIgnoreCase));

                if (hit is null)
                    _vm.Say($"--editenemy: no group matching '{want}' among {groups.Count}");
                else
                {
                    await _vm.OpenEnemyGroup(hit);
                    if (_vm.Tab is EnemyTab opened) await DriveEnemy(opened);
                }
            }

            // --editmade <id fragment>: the GENERIC copy editor, on a duplicated asset.
            //
            // Its own flag rather than a branch of --editweapon for the same reason that one is
            // not a branch of --editcopy: a weapon's id and one of its four plumbing links share
            // most of their spelling, and a fragment matching both would open whichever came
            // first. This one matches every copy, weapon links included, which is the point.
            var em2 = Array.IndexOf(args, "--editmade");
            if (em2 >= 0 && em2 + 1 < args.Length)
            {
                var want = args[em2 + 1];
                if (_vm.AllCopiesInMod.FirstOrDefault(
                        c => c.Id.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit)
                {
                    await _vm.EditGenericCopy(hit);
                    if (_vm.Tab is CopyTab opened) await DriveMade(opened);
                }
                else
                    _vm.Say($"--editmade: no copy matching '{want}' — " +
                            string.Join(", ", _vm.AllCopiesInMod.Select(c => c.Id)));
            }

            // --newchar <template>|<first>|<last>[|<pool>]: the dialog's own outcome, without
            // the dialog. One token with pipes because PowerShell splits an argument on spaces.
            var nc = Array.IndexOf(args, "--newchar");
            if (nc >= 0 && nc + 1 < args.Length) await _vm.MakeCharacterFromKey(args[nc + 1]);

            // --editchar <id fragment>: a character of the mod's own.
            //
            // Its own flag rather than a branch of --editmade, although --editmade does open one:
            // the character's followers (--slot, --charfirst) have nothing to say to a duplicated
            // asset, and a flag that silently does nothing on the wrong kind of tab is the exact
            // fault the arg line at the top of this method exists to end.
            var ec2 = Array.IndexOf(args, "--editchar");
            if (ec2 >= 0 && ec2 + 1 < args.Length)
            {
                var want = args[ec2 + 1];
                var mine = _vm.AllCopiesInMod.Where(StudioVm.IsCharacter).ToList();
                if (mine.FirstOrDefault(
                        c => c.Id.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit)
                {
                    await _vm.EditCharacter(hit);
                    if (_vm.Tab is CharacterTab opened) await DriveCharacter(opened);
                }
                else
                    _vm.Say($"--editchar: no character matching '{want}' among " +
                            (mine.Count == 0 ? "none in this mod"
                                             : string.Join(", ", mine.Select(c => c.Id))));
            }

            // --editmesh <source fragment>: the mesh copy editor, matched on what it copies.
            // Matched on the SOURCE rather than on the imported file, because an entry made for
            // a weapon piece has no imported file until somebody brings one — and that is the
            // state this editor most needs to be reachable in.
            var em3 = Array.IndexOf(args, "--editmesh");
            if (em3 >= 0 && em3 + 1 < args.Length)
            {
                var want = args[em3 + 1];
                if (_vm.MeshesInMod.FirstOrDefault(
                        m => m.Source.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit)
                {
                    await _vm.EditReplacement(hit);
                    if (_vm.Tab is ReplacementTab opened) await DriveMesh(opened);
                }
                else
                    _vm.Say($"--editmesh: no mesh copy matching '{want}' — " +
                            string.Join(", ", _vm.MeshesInMod.Select(m => m.Source)));
            }

            // --editspec <id fragment>: open the editor on a specialization this project holds,
            // and drive it. Its own flag for the reason --editweapon has one: the three kinds of
            // copy are three different objects and an id fragment could match more than one.
            var es = Array.IndexOf(args, "--editspec");
            if (es >= 0 && es + 1 < args.Length)
            {
                var want = args[es + 1];
                if (_vm.SpecsInMod.FirstOrDefault(
                        s => s.Id.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit)
                {
                    await _vm.EditSpec(hit);
                    if (_vm.Tab is SpecTab opened) DriveSpec(opened);
                }
                else
                    _vm.Say($"--editspec: no specialization matching '{want}' — " +
                            string.Join(", ", _vm.SpecsInMod.Select(s => s.Id)));
            }

            // --editweapon <id fragment>: open the editor on a WEAPON copy this project holds.
            //
            // Its own flag rather than a branch of --editcopy, because the two reach different
            // things: a part copy is a ModPart and a weapon copy is a ModCopy at the head of a
            // chain, and an id fragment that matched both would open whichever came first.
            var ew = Array.IndexOf(args, "--editweapon");
            if (ew >= 0 && ew + 1 < args.Length)
            {
                var want = args[ew + 1];
                if (_vm.WeaponsInMod.FirstOrDefault(
                        c => c.Id.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit)
                {
                    await _vm.EditWeaponCopy(hit);
                    if (_vm.Tab is WeaponTab opened) await DrivePanel2(opened);
                }
                else
                    _vm.Say($"--editweapon: no weapon copy matching '{want}' — " +
                            string.Join(", ", _vm.WeaponsInMod.Select(c => c.Id)));
            }

            // --editcopy <id fragment>: open the editor on a copy this project ALREADY holds,
            // which is every part in a project the old studio made. The route it exercises is
            // the one that did not exist: until now the panel could only be reached through
            // Duplicate, so an existing copy had nothing to open into.
            // --viewmeshall [<folder>]: open every part of the mod that carries an imported mesh and press View on it, one
            // after another in one session the way the author does, then name the mesh tabs that are open; with a
            // folder, each view is photographed as <n>.png. Parts built over one game mesh must each get their own tab.
            var vall = Array.IndexOf(args, "--viewmeshall");
            if (vall >= 0)
            {
                var shots = vall + 1 < args.Length && !args[vall + 1].StartsWith("--") ? args[vall + 1] : null;
                var n = 0;
                foreach (var part in _vm.CopiesInMod.Where(p => p.MeshFile.Length > 0).ToList())
                {
                    await _vm.EditCopy(part);
                    if (_vm.Tab is not PartTab vpt) continue;
                    for (var spin = 0; spin < 40 && vpt.MeshRows.Count == 0; spin++) await Task.Delay(250);
                    if (vpt.MeshRows.FirstOrDefault(r => r.Replacement.Length > 0) is not { } vrow) continue;
                    await _vm.ViewMeshRowAsync(vrow);
                    await Task.Delay(3000);
                    n++;
                    if (shots is not null && _vm.Tab is MeshTab vmt)
                    {
                        vmt.View.ShotTo(System.IO.Path.Combine(shots, $"{n}.png"));
                        await Task.Delay(1500);
                    }
                }
                _vm.Say($"[viewall] {n} viewed; mesh tabs open: " +
                        string.Join(" | ", _vm.Tabs.OfType<MeshTab>().Select(t => t.Title)));

                // AND BACK TO EACH, as a person returns to a tab: switching away tears the viewport's GL down, and the
                // mesh must be rebuilt on return, imported file or game mesh alike.
                var meshTabs = _vm.Tabs.OfType<MeshTab>().ToList();
                var back = 0;
                foreach (var t in meshTabs)
                {
                    _vm.Tab = t;
                    await Task.Delay(2500);
                    if (t.View.HasMesh) back++;
                    else _vm.Say($"[viewall] back to {t.Title}: NOT DRAWN");
                }
                _vm.Say($"[viewall] on return {back} of {meshTabs.Count} drew");
            }

            var ec = Array.IndexOf(args, "--editcopy");
            if (ec >= 0 && ec + 1 < args.Length)
            {
                var want = args[ec + 1];
                if (_vm.CopiesInMod.FirstOrDefault(
                        c => c.Id.Contains(want, StringComparison.OrdinalIgnoreCase)) is { } hit)
                {
                    await _vm.EditCopy(hit);

                    // AND THEN DRIVE IT, with the same flags a duplicate takes. An existing copy
                    // is the only thing that can exercise the paths that read a project BACK —
                    // importing over paint that is already there, reverting it, changing a
                    // picture that was chosen months ago.
                    if (_vm.Tab is PartTab opened) await DrivePanel(opened);

                    // --meshbuttons: the mesh rows' buttons as drawn, read off the visual tree.
                    if (args.Contains("--meshbuttons") && _vm.Tab is PartTab drawn)
                    {
                        for (var spin = 0; spin < 40 && drawn.MeshRows.Count == 0; spin++) await Task.Delay(250);
                        await Task.Delay(500);
                        _vm.Say("[override] part mesh row buttons drawn: " + string.Join(", ",
                            this.GetVisualDescendants().OfType<Button>()
                                .Where(b => b.DataContext is SheetTab.MeshRow && b.IsEffectivelyVisible)
                                .Select(b => b.Content as string ?? "?")));
                    }
                }
                else
                    _vm.Say($"--editcopy: no copy matching '{want}' — " +
                            string.Join(", ", _vm.CopiesInMod.Select(c => c.Id)));
            }

            // --secfind <text>: type into the SECTION list's own filter, which is not the Data
            // Explorer's. Two filter boxes, two flags — using --find for this showed an
            // unfiltered section list and looked like the filter had done nothing.
            var sf = Array.IndexOf(args, "--secfind");
            if (sf >= 0 && sf + 1 < args.Length) _vm.SectionFilter = args[sf + 1];

            // --pick <text>: open a row of the current section as a tab, so the sheet can be
            // photographed. Matches the name, then the id beside it.
            var pk = Array.IndexOf(args, "--pick");
            if (pk >= 0 && pk + 1 < args.Length)
            {
                var want = args[pk + 1];
                var row = _vm.SectionRows.FirstOrDefault(
                    r => !r.IsHeader &&
                         (r.Name.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                          r.Detail.Contains(want, StringComparison.OrdinalIgnoreCase)));
                if (row is null) _vm.Say($"--pick {want}: nothing in this section matches");
                else { _vm.SectionPick = row; _vm.OpenSectionItem(row); }
            }

            // --openlistmenu: open the section list's right-click menu (after --section and --pick), say
            // which entries are enabled, and leave it open so it can be photographed.
            if (args.Contains("--openlistmenu"))
            {
                await Task.Delay(1500);
                var menuList = this.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(
                    l => l.ContextMenu?.Items.OfType<MenuItem>().Any(m => (m.Header as string) == "Duplicate") == true);
                if (menuList?.ContextMenu is { } listMenu)
                {
                    // OPENED FIRST: a menu's bindings are not applied until it is shown, so asked
                    // before that every entry reports itself enabled.
                    listMenu.Open(menuList);
                    await Task.Delay(800);
                    _vm.Say("[listmenu] " + string.Join(" | ", listMenu.Items.OfType<MenuItem>()
                        .Select(m => $"{m.Header}{(m.IsEnabled ? "" : " (disabled)")}")));
                }
                else _vm.Say("[listmenu] no section list with a menu on screen");
            }

            // --addtopool <character fragment>: the second recruit action, without its dialog.
            //
            // Its own flag because the real path goes through a PICKER, and a flag that opened
            // one would stop dead waiting for a mouse. What it skips is the choosing; the write
            // is the same AddToPool the picker calls.
            var atp = Array.IndexOf(args, "--addtopool");
            if (atp >= 0 && atp + 1 < args.Length)
            {
                if (_vm.Chosen?.Payload is not CharacterIndex.Recruit atpPool)
                    _vm.Say("--addtopool: pick a recruit first (--section Recruits --pick <row>)");
                else
                {
                    var want = args[atp + 1];
                    var all = await _vm.CharacterTemplatesAsync();
                    var hit = all.FirstOrDefault(
                        c => c.Name.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                             c.Package.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--addtopool: no character matching '{want}' among {all.Count}");
                    else await _vm.AddToPool(hit.Package, atpPool);
                }
            }

            // --takeplace <character fragment>: the third recruit action, without its dialog.
            //
            // Same shape as --addtopool and for the same reason. Worth driving rather than only
            // clicking, because what it writes is a rename on a table copy -- invisible on screen
            // until a build reads it back, which is exactly the kind of edit that has looked
            // finished and done nothing before now.
            var tkp = Array.IndexOf(args, "--takeplace");
            if (tkp >= 0 && tkp + 1 < args.Length)
            {
                if (_vm.Chosen?.Payload is not CharacterIndex.Recruit tkpRow)
                    _vm.Say("--takeplace: pick a recruit first (--section Recruits --pick <row>)");
                else
                {
                    var want = args[tkp + 1];
                    var all = await _vm.CharacterTemplatesAsync();
                    var hit = all.FirstOrDefault(
                        c => c.Name.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                             c.Package.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--takeplace: no character matching '{want}' among {all.Count}");
                    else await _vm.TakeTheirPlace(hit.Package, tkpRow);
                }
            }

            // --putback: the undo, on the picked recruit.
            if (args.Contains("--putback"))
            {
                if (_vm.Chosen?.Payload is not CharacterIndex.Recruit pbRow)
                    _vm.Say("--putback: pick a recruit first (--section Recruits --pick <row>)");
                else _vm.PutRowBack(pbRow);
            }

            // --do <open|edit|revert|duplicate>: the right-click menu, from outside. The only
            // way to witness that Duplicate really writes a part into the project.
            var act = Array.IndexOf(args, "--do");
            if (act >= 0 && act + 1 < args.Length && _vm.Chosen is { } picked)
                switch (args[act + 1].ToLowerInvariant())
                {
                    // EDIT AND DUPLICATE OPEN THE SAME PANEL, so they share every flag below.
                    // These were written inside `case "edit"`, which left all of them unreachable
                    // for a duplicate — the half most in need of witnessing, because a copy's
                    // changes land somewhere else entirely.
                    case "edit":
                    case "duplicate":
                        if (args[act + 1].Equals("duplicate", StringComparison.OrdinalIgnoreCase))
                            _vm.DuplicatePart(picked);
                        else _vm.EditPart(picked);
                        // --rename <text>: type into the edit panel and press Save, so the whole
                        // path is witnessed rather than the panel merely being seen to open.
                        if (_vm.Tab is PartTab pt) await DrivePanel(pt);

                        // --progression <tier> <label> <value> [own]: one cell of an ability's
                        // tier table. Its own flag because the write forks on `own` — move the
                        // number everywhere that names it, or give this tier its own copy — and
                        // that fork is the whole reason this is not a grid of boxes.
                        if (_vm.Tab is AbilityTab abt)
                        {
                            for (var spin = 0; spin < 80 && !abt.HasProgression; spin++)
                                await Task.Delay(250);

                            // AND FOR THE EFFECTS, which are read after the tiers and take
                            // longer: the reading opens every effect the ability applies and
                            // walks the class chain for each inherited property. Without this
                            // the dump printed "reading what it does" and three empty lists.
                            for (var spin = 0; spin < 120 && !abt.EffectsRead; spin++)
                                await Task.Delay(250);

                            var pg = Array.IndexOf(args, "--progression");
                            if (pg >= 0 && pg + 3 < args.Length &&
                                int.TryParse(args[pg + 1], out var pgTier))
                            {
                                var hit = abt.Progression.FirstOrDefault(
                                    r => r.Tier == pgTier &&
                                         r.Label.Contains(args[pg + 2], StringComparison.OrdinalIgnoreCase));
                                if (hit is null)
                                    _vm.Say($"--progression: tier {pgTier} has no '{args[pg + 2]}' — " +
                                            string.Join(", ", abt.Progression.Where(r => r.Tier == pgTier)
                                                                             .Select(r => r.Label)));
                                else
                                    _vm.SetProgression(abt, hit, args[pg + 3],
                                                       ownCopy: pg + 4 < args.Length &&
                                                                args[pg + 4] == "own");
                            }

                            // --abnum <label fragment> <value>: one number, on the ability
                            // or on an effect it applies.
                            var an = Array.IndexOf(args, "--abnum");
                            if (an >= 0 && an + 2 < args.Length)
                            {
                                if (Num(abt, args[an + 1]) is not { } hit)
                                    _vm.Say($"--abnum: no number matching '{args[an + 1]}' among " +
                                            $"{abt.EffectRows.Count}");
                                else { hit.Value = args[an + 2]; _vm.SetEffectNumber(abt, hit); }
                            }

                            var ar = Array.IndexOf(args, "--abreset");
                            if (ar >= 0 && ar + 1 < args.Length)
                            {
                                if (Num(abt, args[ar + 1]) is { } hit)
                                    _vm.ResetEffectNumber(abt, hit);
                                else _vm.Say($"--abreset: no number matching '{args[ar + 1]}'");
                            }

                            // --abpoint <label fragment> <target fragment>: chosen from the
                            // narrowed list, as the picker does.
                            var ap = Array.IndexOf(args, "--abpoint");
                            if (ap >= 0 && ap + 2 < args.Length)
                            {
                                var hit = abt.PointRows.FirstOrDefault(
                                    r => r.Label.Contains(args[ap + 1],
                                                          StringComparison.OrdinalIgnoreCase));
                                if (hit is null)
                                    _vm.Say($"--abpoint: no reference matching '{args[ap + 1]}' " +
                                            $"among {abt.PointRows.Count}");
                                else
                                {
                                    var all = await _vm.PointCandidatesAsync(hit);
                                    var want = all.FirstOrDefault(
                                        c => c.Name.Contains(args[ap + 2],
                                                             StringComparison.OrdinalIgnoreCase));
                                    if (want is null)
                                        _vm.Say($"--abpoint: nothing matching '{args[ap + 2]}' " +
                                                $"among {all.Count} candidate(s)");
                                    else _vm.SetPoint(abt, hit, want.Package);
                                }
                            }

                            // --abgate <label fragment> [tag,tag]: add the container, and with a
                            // list, set its tags. No list keeps what it inherits.
                            var ag = Array.IndexOf(args, "--abgate");
                            if (ag >= 0 && ag + 1 < args.Length)
                            {
                                var hit = abt.TagBoxRows.FirstOrDefault(
                                    r => r.Label.Contains(args[ag + 1],
                                                          StringComparison.OrdinalIgnoreCase));
                                if (hit is null)
                                    _vm.Say($"--abgate: no container matching '{args[ag + 1]}' " +
                                            $"among {abt.TagBoxRows.Count}");
                                else
                                {
                                    if (ag + 2 < args.Length && !args[ag + 2].StartsWith("--"))
                                        hit.Text = args[ag + 2].Replace(",", "\n");
                                    _vm.AddTagBox(abt, hit);
                                }
                            }

                            var ad = Array.IndexOf(args, "--abdrop");
                            if (ad >= 0 && ad + 1 < args.Length)
                            {
                                var hit = abt.TagBoxRows.FirstOrDefault(
                                    r => r.Label.Contains(args[ad + 1],
                                                          StringComparison.OrdinalIgnoreCase));
                                if (hit is null) _vm.Say($"--abdrop: no container matching '{args[ad + 1]}'");
                                else _vm.DropTagBox(abt, hit);
                            }

                            foreach (var r in abt.Progression)
                                _vm.Say($"[prog] tier {r.Tier}  {r.Label} = {r.Text}" +
                                        (r.IsShared ? $"   ({r.Note})" : "") +
                                        (r.Edited ? "   <- yours" : ""));

                            // --card <key>: fold every card but this one. The progression
                            // table is fourteen tiers tall and pushes the other three off the
                            // bottom of any screenshot.
                            var abc = Array.IndexOf(args, "--card");
                            if (abc >= 0 && abc + 1 < args.Length)
                            {
                                var key = args[abc + 1];
                                var hit = abt.EditSections.FirstOrDefault(
                                    s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                                if (hit is null)
                                    _vm.Say($"--card {key}: no section by that name — " +
                                            string.Join(", ", abt.EditSections
                                                                 .Where(s => s.Present)
                                                                 .Select(s => s.Key)));
                                else
                                {
                                    foreach (var s in abt.EditSections)
                                        s.Open = ReferenceEquals(s, hit);
                                    abt.SelectSection(hit);
                                }
                            }

                            if (args.Contains("--dumpability"))
                            {
                                _vm.Say($"[ab] note        {abt.EffectsNote}");
                                if (abt.HasEffectsElsewhere)
                                    _vm.Say($"[ab] elsewhere   {abt.EffectsElsewhere}");
                                _vm.Say($"[ab] cards       numbers={abt.SecEffects.Present} " +
                                        $"points={abt.SecPoints.Present} gates={abt.SecGates.Present}");
                                foreach (var r in abt.EffectRows)
                                    _vm.Say($"[ab] number      {r.Label} = {r.Value}  " +
                                            $"(now {r.Now})" + (r.Mine ? "  <- yours" : "") +
                                            (r.HasState ? $"   [{r.State}]" : ""));
                                foreach (var r in abt.PointRows)
                                    _vm.Say($"[ab] points at   {r.Label}: {r.Uses}" +
                                            (r.HasMine ? $"   <- yours: {r.Mine}" : ""));
                                foreach (var r in abt.TagBoxRows)
                                    _vm.Say($"[ab] container   {r.Label}: {r.Holds}" +
                                            (r.Added ? "   <- yours: " +
                                                       r.Text.Replace("\n", ", ") : ""));
                            }

                            static SheetTab.EffectRow? Num(AbilityTab at, string want)
                                => at.EffectRows.FirstOrDefault(
                                    r => r.Label.Contains(want, StringComparison.OrdinalIgnoreCase));
                        }
                        break;
                    case "revert": _vm.RevertPart(picked); break;
                    case "find": await _vm.FindInDataAsync(picked); break;
                    default: _vm.OpenSectionItem(picked); break;
                }

            // --dumpedit: WHAT THE PANEL IS SHOWING, row by row, to the console.
            //
            // A screenshot says the card is there; this says what is written in it, and that is
            // the half his report was about — "it tells me that everything is unchanged the
            // meshes and the textures". Every row is printed, changed or not: a dump that lists
            // only what it found changed cannot tell "nothing is changed" from "nothing was
            // read", which is the shape of instrument that has already hidden working code here
            // four times. The textures arrive on a background read, so this waits for them.
            // --piece <n> <file|revert>: import onto ONE piece of a garment, or put that piece
            // back. The per-row buttons need a mouse, and the whole point of a piece is that the
            // write must land on it and NOT on its neighbours — which is exactly what a flag can
            // witness and a screenshot cannot.
            // --bankicon <tag fragment>: a weapon's armoury picture, without its picker.
            //
            // AFTER --do, not before it: the tab this acts on is the one `--do duplicate` opens,
            // and up there it ran against whatever was open at startup and silently did nothing.
            // The real path opens a picker, and a flag that opened one would stop dead waiting
            // for a mouse; the write is the same SetBankIcon the picker calls.
            // The
            // real path opens one, and a flag that opened one would stop dead waiting for a
            // mouse; the write is the same SetBankIcon the picker calls.
            var bi = Array.IndexOf(args, "--bankicon");
            if (bi >= 0 && bi + 1 < args.Length && _vm.Tab is WeaponTab biTab && biTab.Copy is { } biCopy)
            {
                var want = args[bi + 1];
                var all = await _vm.BankIconsAsync(UEToolkit.Core.ImageBanks.WeaponFamily);
                var hit = all.FirstOrDefault(
                    c => c.Name.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                         c.Package.Contains(want, StringComparison.OrdinalIgnoreCase));
                if (hit is null) _vm.Say($"--bankicon: nothing matching '{want}' among {all.Count}");
                else _vm.SetBankIcon(biTab, biCopy, hit.Package);
            }

            if (args.Contains("--dumpedit") && _vm.Tab is SheetTab dump)
            {
                // ASKED OF THE TAB. Three kinds read their paint out of the game after the
                // panel opens and the rest are complete the moment they are drawn — so naming
                // the ones that are not is a list that goes stale every time a kind is added,
                // and the symptom is a twenty-second wait on a panel that was already finished.
                for (var spin = 0; spin < 80 && dump.ReadsTextures && !dump.HasTextureRows; spin++)
                    await Task.Delay(250);

                // AND FOR THE MEASUREMENT. It runs again after every field written, so a dump
                // that did not wait printed "measuring…" about a panel that had finished before
                // the line reached the log — the instrument reporting a state nobody read back,
                // which is the exact fault it exists to catch.
                for (var spin = 0; spin < 40 && dump.SecFit.Present &&
                                   dump.FitCounts.StartsWith("measuring", StringComparison.Ordinal);
                     spin++)
                    await Task.Delay(250);

                // AND FOR THE ARMOURY'S SHELVES, which are a walk of six shop tables. Same
                // fault, third card: it printed "shop slots 0" in the same run whose log said
                // the shelves held fifty-one.
                for (var spin = 0; spin < 40 && dump.SecSell.Present && !dump.IsOnSale &&
                                   !dump.HasShopSlots; spin++)
                    await Task.Delay(250);

                if (dump.HasBankIcon)
                    _vm.Say($"[edit] bank icon  donor={dump.BankIcon}  {dump.BankIconSays}");
                if (dump.SecChanges.Present)
                    _vm.Say($"[edit] changes     target={dump.EditTarget}  says={dump.ChangesSays}" +
                            (dump.HasChangesClash ? $"  CLASH: {dump.ChangesClash}" : ""));
                _vm.Say($"[edit] title       {dump.Title}");
                _vm.Say($"[edit] subtitle    {dump.Subtitle}");
                _vm.Say($"[edit] aside       {dump.Aside}");
                _vm.Say($"[edit] badge       {dump.EditBadge}");
                // "game=True" over a MOD's picture would be the dump describing the wrong thing:
                // a mod's picture is its own and there is no game thumbnail behind it. The mod
                // block below prints its own line.
                if (dump is not ModTab)
                    _vm.Say($"[edit] picture     yours={dump.HasIconPreview} game={dump.HasIcon}");
                _vm.Say($"[edit] name box    {dump.EditName}");
                _vm.Say($"[edit] description {dump.EditDescription}");
                if (dump.ShowsLongDescription)
                    _vm.Say($"[edit] long desc   {dump.EditLongDescription}");
                _vm.Say(dump.IconRow is { } ir
                    ? $"[edit] icon row    {ir.Slot}: {ir.Kind} — {ir.Shown} ({ir.Detail})"
                    : $"[edit] icon row    none — {dump.IconNote}");

                foreach (var m in dump.MeshRows)
                    _vm.Say($"[edit] mesh        {m.Variant}: {m.Kind} — {m.Shown} ({m.Detail})" +
                            (m.PerPiece ? $"   [piece {m.Region}]" : ""));

                foreach (var b in dump.Bolts)
                    _vm.Say($"[edit] bolt        {b.Label}" +
                            (dump.Bolt?.Index == b.Index ? "   <- chosen" : ""));
                if (dump.ShelfIs.Length > 0)
                    _vm.Say($"[edit] shelf       {dump.ShelfIs}" +
                            (dump.ShelfIsOwn ? "   <- this mod's own" : ""));
                if (dump.FitsIs.Length > 0) _vm.Say($"[edit] fits        {dump.FitsIs}");
                if (dump.SecFit.Present)
                    _vm.Say($"[edit] fit on      " +
                            (dump.FitPieces.Count > 0 ? $"piece {dump.FitPiece}" : "the whole thing") +
                            $"  of {dump.FitPieces.Count} piece(s) with an import");
                foreach (var s in dump.WeaponStats.Take(6))
                    _vm.Say($"[edit] number      {s.Label} = {s.Text}" +
                            (s.Changed ? "   <- yours" : ""));
                foreach (var a in dump.Attachments)
                    _vm.Say($"[edit] attachment  {a.Slot}: {a.Wearing}" +
                            (a.Mine ? "   <- yours" : "") + $"   [{a.Leaf}]");

                if (dump.EditCrest.Length > 0) _vm.Say($"[edit] crest       {dump.EditCrest}");
                if (dump.EditForWho.Length > 0) _vm.Say($"[edit] for who     {dump.EditForWho}");
                if (dump.EditIdentity.Length > 0) _vm.Say($"[edit] identity    {dump.EditIdentity}");
                foreach (var g in dump.Grants)
                    _vm.Say($"[edit] grants      {(g.IsGear ? "gear kit" : "ability")}  {g.Leafed}");
                foreach (var r in dump.Rungs)
                    _vm.Say($"[edit] rung        {r.Where}: {r.AbilityLeaf}  " +
                            $"level {r.Level}, focus {r.Focus}, cost {r.Cost}");

                if (dump is ModTab)
                {
                    _vm.Say($"[edit] author      {dump.EditAuthor}");
                    _vm.Say($"[edit] version     {dump.EditVersion}");
                    _vm.Say($"[edit] picture     {dump.EditPicture}   (decoded={dump.HasModPicture})");
                    _vm.Say($"[edit] ships as    {dump.EditShipsAs}");
                    _vm.Say($"[edit] builds      {dump.ShipsAsFiles}");
                    _vm.Say($"[edit] folder      {dump.EditFolder}   ->   {dump.FolderIs}");
                    if (dump.FolderBad) _vm.Say($"[edit] folder BAD  {dump.FolderWhy}");
                    _vm.Say($"[edit] by script   {dump.EditByScript}" +
                            (dump.SecRecruit.Present
                                ? $"   keys {dump.EditRecruitKey}/{dump.EditRepairKey}"
                                : "   (card absent — nothing to recruit)"));
                }

                if (dump.SecFit.Present)
                {
                    _vm.Say($"[edit] fit counts  {dump.FitCounts}");
                    if (dump.FitTemplate.Length > 0)
                        _vm.Say($"[edit] fit template {dump.FitTemplate}");
                    if (dump.FitYours.Length > 0)
                        _vm.Say($"[edit] fit yours   {dump.FitYours}" +
                                (dump.FitOff ? "   <- more than a quarter out" : ""));
                    if (dump.FitRefused) _vm.Say($"[edit] fit REFUSED {dump.FitRefuse}");
                    _vm.Say($"[edit] fit fields  axes '{dump.EditBasis}'  " +
                            $"cm/unit '{dump.EditScale}'  " +
                            $"move {dump.EditMoveX}, {dump.EditMoveY}, {dump.EditMoveZ}" +
                            (dump.HasOwnFit ? "   <- yours" : "   (all default)"));
                    foreach (var n in dump.FitNotes) _vm.Say($"[edit]   fit note  {n}");
                }

                if (dump.SecTint.Present)
                {
                    // THE FULL PACKAGE TOO. A leaf is what the row shows and is not enough to
                    // check anything against — it is not a path anything can be pointed at.
                    _vm.Say($"[edit] tint mask   {dump.TintMaskShown}" +
                            (dump.HasOwnTintMask ? "   <- yours" : "   (the template's)") +
                            $"   {(dump.HasOwnTintMask ? dump.EditTintMask : dump.TemplateTintMask)}");
                    _vm.Say($"[edit] tint colour {dump.TintColourShown}" +
                            (dump.TakesTintColour ? "" : "   (its template carries none)"));
                }

                if (dump is EnemyTab et2)
                {
                    _vm.Say($"[edit] group       {et2.Group.Leaf}");
                    _vm.Say($"[edit] used by     {et2.Group.Users.Count} — " +
                            string.Join(", ", et2.Group.Factions));
                    _vm.Say($"[edit] tables      {et2.Group.Tables.Count}");
                    _vm.Say($"[edit] note        {dump.EnemyNote}");
                    foreach (var r in dump.EnemyRows)
                        _vm.Say($"[edit]   stat      {r.Shown,-28} ships {r.Ships,-16} " +
                                $"x{r.Text,-6} becomes {r.Becomes}" +
                                (r.Changed ? "   <- yours" : ""));
                }

                if (dump is MissionTab { Mine: { } mm })
                {
                    _vm.Say($"[edit] mission     {mm.Id}   from {mm.From}");
                    _vm.Say($"[edit] in place    {mm.InPlace}");
                    _vm.Say($"[edit] briefing    " +
                            (dump.EditBriefing.Length > 90
                                ? dump.EditBriefing[..90] + "…" : dump.EditBriefing));
                    _vm.Say($"[edit] planet      {mm.Planet}   table {Leaf(mm.LocationTable)}" +
                            (dump.HasPlanetNote ? $"   <- {dump.PlanetNote}" : ""));
                    _vm.Say($"[edit] region      {mm.RegionTag} / {mm.RegionAsset}");
                    _vm.Say($"[edit] map         {Leaf(mm.Level)}" +
                            (dump.HasLevelNote ? $"   <- {dump.LevelNote}" : ""));
                    _vm.Say($"[edit] map rules   {Leaf(mm.MapConfiguration)}");
                    _vm.Say($"[edit] you fight   {mm.Faction}" +
                            (dump.HasFactionWarn ? $"   <- {dump.FactionWarn}" : ""));
                    _vm.Say($"[edit] objective   {mm.Type}");
                    _vm.Say($"[edit] squad       {mm.MinSquad} to {mm.MaxSquad}, " +
                            $"stays {mm.LifeSpan} turn(s), " +
                            $"blocks active={mm.BlockActiveDuplicates} done={mm.BlockCompletedDuplicates}");
                    _vm.Say($"[edit] pays        {mm.Rewards.Count} reward(s), " +
                            $"table {Leaf(mm.RewardTable)}, pre-roll {mm.RewardPreRoll}");
                    _vm.Say($"[edit] raises      {mm.FactTag} by {mm.FactIncrement}");
                    foreach (var ob in dump.Objectives)
                        _vm.Say($"[edit]   objective {ob.Value}");
                    foreach (var r in dump.Rewards)
                        _vm.Say($"[edit]   reward    {r.Value}");
                    foreach (var g in dump.MissionTags)
                        _vm.Say($"[edit]   tag       {g.Value}");
                    _vm.Say($"[edit] choosers    planets {dump.PlanetChoices.Count}, " +
                            $"maps {dump.LevelChoices.Count}, factions {dump.FactionChoices.Count}");

                    _vm.Say($"[edit] when        {dump.WhenSays}");
                    _vm.Say($"[edit] campaign    {dump.Turns.Count} turn(s), " +
                            $"{dump.CampaignSlots.Count} placed mission(s), " +
                            $"{dump.TableChoices.Count} table(s)");
                    foreach (var tr in dump.Turns.Where(x => x.Taken))
                        _vm.Say($"[edit]   added to  {tr.Arc} {tr.Turn}   ({tr.Already})");
                    foreach (var sr in dump.CampaignSlots.Where(x => x.Taken))
                        _vm.Say($"[edit]   takes     {sr.Leaf}   {sr.PlacedBy}   costs {sr.Costs}");
                    foreach (var lr in dump.LotteryRows)
                    {
                        _vm.Say($"[edit]   lottery   {lr.TableLeaf}  weight {lr.Weight}  " +
                                (lr.AddsRow ? "ADDS a row" : $"takes over {lr.PickedRow?.Shown}") +
                                $"   {lr.Effect}");
                        foreach (var rq in lr.Requires)
                            _vm.Say($"[edit]     only when {rq.Value}");
                        foreach (var ig in lr.Ignores)
                            _vm.Say($"[edit]     never when {ig.Value}");
                        foreach (var gt in lr.Gates)
                            _vm.Say($"[edit]     gate      {gt.Tag} {gt.Operation} {gt.Value}" +
                                    (gt.MissingIsZero ? "   (unset = 0)" : ""));
                    }
                }

                if (dump.SecSell.Present)
                {
                    _vm.Say($"[edit] on sale     " +
                            (dump.IsOnSale ? dump.OnSaleAs : "NOWHERE — nothing offers it"));
                    _vm.Say($"[edit] shop slots  {dump.ShopSlots.Count}");
                    if (dump.SellSlot is { } wouldTake)
                        _vm.Say($"[edit] slot picked {wouldTake.Describe}");
                }

                if (dump is CopyTab made2)
                {
                    _vm.Say($"[edit] copy of     {made2.Copy.Template}");
                    _vm.Say($"[edit] ships as    {made2.EditId}");
                    if (made2.PartOf.Length > 0) _vm.Say($"[edit] link of     {made2.PartOf}");
                    _vm.Say($"[edit] inside      {dump.InsideSays}");
                    if (dump.InsideNote.Length > 0) _vm.Say($"[edit] inside note {dump.InsideNote}");
                    // WITH THE PACKAGE PATH. A leaf is what the row shows and is not enough to
                    // check anything against: two assets in different folders share a leaf, and
                    // the path is what has to be typed back in to move one.
                    foreach (var r in dump.InsideRows)
                        _vm.Say($"[edit]   ref       {r.Kind,-9} {r.Property} = {r.Shown}" +
                                (r.Detail.Length > 0 ? $"  ({r.Detail})" : "") +
                                (r.Hard ? "   [hard]" : "   [soft]") +
                                (r.IsMine ? "   <- yours" : "") +
                                $"   {r.Package}");
                }

                if (dump is ReplacementTab own2)
                {
                    _vm.Say($"[edit] copy of     {own2.Mesh.Source}");
                    _vm.Say($"[edit] geometry    " +
                            (own2.Mesh.MeshFile.Length > 0 ? own2.Mesh.MeshFile : "the game's own"));
                    _vm.Say($"[edit] worn by     " +
                            (own2.WornBy.Length > 0 ? own2.WornBy : "NOTHING in this mod"));
                }

                foreach (var g in dump.MaterialGroups)
                {
                    _vm.Say($"[edit] material    {g.Title}: wears {g.Wears}" +
                            $"  [tint slot {(g.SlotName.Length > 0 ? g.SlotName : "—")}" +
                            $", planned {g.PlannedShown}{(g.TakesNoTint ? ", TAKES NO TINT" : "")}]");
                    foreach (var t in g.Rows)
                        _vm.Say($"[edit]   texture   {t.Slot}: {t.Kind} — {t.Shown} ({t.Detail})" +
                                (t.MineKey.Length > 0 ? $"  [key {t.MineKey}]" : "") +
                                (t.Piece.Length > 0 ? $"  [piece {t.Piece}]" : ""));
                }
            }

            // --glshot <path> [seconds]: photograph the VIEWPORT, which a window grab cannot.
            // Avalonia draws GL into a surface the compositor owns and PrintWindow reads it as
            // black however well it is drawing, so the framebuffer is the only honest picture.
            // --texchannels rgba: pick the channels without a mouse, because isolation is
            // otherwise only checkable by clicking four boxes and looking, which does not survive
            // a refactor. The rule that a lone channel renders GREY rather than as a
            // red/green/blue tint is exactly the sort of thing that reverts unnoticed — a tinted
            // map still looks like a map.
            //
            // NOT NESTED UNDER --glshot, which is where this started and where it silently did
            // nothing for a window grab. Which instrument takes the picture is no business of
            // the thing being photographed.
            var tc = Array.IndexOf(args, "--texchannels");
            if (tc >= 0 && tc + 1 < args.Length && _vm.Tab is TextureTab texTab)
            {
                var want = args[tc + 1];
                texTab.ShowR = want.Contains('r');
                texTab.ShowG = want.Contains('g');
                texTab.ShowB = want.Contains('b');
                texTab.ShowAlpha = want.Contains('a');
                _vm.Say($"--texchannels {want}");
            }

            // --viewmode <name>: pick a mesh view mode by its dropdown label, matched loosely so
            // "basecolor", "Base Color" and "base color" all land. Every one of these modes is a
            // picture and nothing else, so a still is the only thing that can check one — and a
            // mode that silently falls through to lit looks exactly like a mode that works.
            var vm = Array.IndexOf(args, "--viewmode");
            if (vm >= 0 && vm + 1 < args.Length && _vm.Tab is MeshTab modeTab)
            {
                var want = args[vm + 1].Replace(" ", "");
                var hit = ModelView.Modes.FirstOrDefault(
                    m => m.Name.Replace(" ", "").Equals(want, StringComparison.OrdinalIgnoreCase));
                // NAMES THE MODES when it misses, rather than leaving a silent lit frame that
                // reads as the mode having been applied.
                if (hit is null)
                    _vm.Say($"--viewmode {args[vm + 1]}: no such mode. " +
                            string.Join(", ", ModelView.Modes.Select(m => m.Name)));
                else { modeTab.Mode = hit; _vm.Say($"--viewmode {hit.Name}"); }
            }

            // --modeshots <dir>: photograph EVERY view mode in one run.
            //
            // The cost here is the game mount — six containers and 180,000 files, about a minute
            // — and it is paid once whether one mode is checked or ten. Ten separate runs is
            // thirteen minutes to answer a question that is really one question: does each mode
            // draw the thing it names. Each mode is a picture and nothing else, so there is no
            // number that can stand in for looking.
            var ms = Array.IndexOf(args, "--modeshots");
            if (ms >= 0 && ms + 1 < args.Length && _vm.Tab is MeshTab shotTab)
            {
                var dir = args[ms + 1];
                Directory.CreateDirectory(dir);
                // Let the mesh settle once before the first mode, so the first picture is not
                // the only one taken of a half-built viewport.
                await Task.Delay(TimeSpan.FromSeconds(4));

                foreach (var mode in ModelView.Modes)
                {
                    shotTab.Mode = mode;
                    var file = Path.Combine(dir, $"mode_{mode.Name.Replace(" ", "")}.png");
                    if (File.Exists(file)) File.Delete(file);
                    shotTab.View.ShotTo(file);

                    // WAIT FOR THE FILE, not for a fixed delay. The grab happens on the render
                    // thread and a fixed sleep is a coin toss — the same fault as the harness
                    // that tested an eighth of a streamed level and reported a real number about
                    // it. Capped, so a mode that never draws fails loudly instead of hanging.
                    for (var waited = 0; waited < 60 && !File.Exists(file); waited++)
                        await Task.Delay(100);

                    _vm.Say(File.Exists(file)
                        ? $"--modeshots {mode.Name}: written"
                        : $"--modeshots {mode.Name}: NO FRAME after 6s");
                }
            }

            // --orbit <dx> <dy> / --lod <n>: drive the camera and the picker from outside.
            //
            // THROUGH THE SAME CALLS THE MOUSE AND THE DROPDOWN MAKE, not a private back door,
            // so a run proves the path a person uses rather than one built for the test. Camera
            // movement is otherwise only checkable by dragging and looking, which does not
            // survive a refactor — and a viewport that has stopped responding looks exactly like
            // one nobody has dragged yet.
            var orb = Array.IndexOf(args, "--orbit");
            if (orb >= 0 && orb + 2 < args.Length && _vm.Tab is MeshTab camTab
                && float.TryParse(args[orb + 1], out var odx)
                && float.TryParse(args[orb + 2], out var ody))
            {
                camTab.View.Camera.Orbit(odx, ody);
                camTab.View.RequestNextFrameRendering();
                _vm.Say($"--orbit {odx} {ody} -> eye {camTab.View.Camera.Position}");
            }

            // --dist <factor>: move the camera closer (under 1) or further (over 1), as the scroll wheel does. A close
            // look is where a texture's largest mips are drawn, which a framed-to-fit picture never reaches.
            var distAt = Array.IndexOf(args, "--dist");
            if (distAt >= 0 && distAt + 1 < args.Length && _vm.Tab is MeshTab distTab
                && float.TryParse(args[distAt + 1], System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var distFactor) && distFactor > 0)
            {
                distTab.View.Camera.Distance *= distFactor;
                distTab.View.RequestNextFrameRendering();
                _vm.Say($"--dist {distFactor} -> distance {distTab.View.Camera.Distance}");
            }

            var lodAt = Array.IndexOf(args, "--lod");
            if (lodAt >= 0 && lodAt + 1 < args.Length && _vm.Tab is MeshTab lodTab
                && int.TryParse(args[lodAt + 1], out var wantLod))
            {
                lodTab.Lod = wantLod;
                // NAMES WHAT IT ACTUALLY BUILT. A requested LOD the asset does not ship falls
                // back, and a log line echoing the request would hide that.
                await Task.Delay(TimeSpan.FromSeconds(1));
                _vm.Say($"--lod {wantLod} -> built {lodTab.View.Lod} " +
                        $"of [{string.Join(",", lodTab.View.LodsOffered)}]");
            }

            // --hittest: ASK AVALONIA WHICH CONTROL THE POINTER WOULD LAND ON, at the middle of
            // the viewport.
            //
            // This is the check that was missing when "the camera works" was reported. --orbit
            // calls Camera.Orbit directly and so proves the MATHS; it cannot tell you whether a
            // real mouse event ever reaches the control. An OpenGlControlBase draws through a
            // composition operation the hit test cannot see, so without a transparent fill the
            // pointer goes straight to whatever is behind it and every handler is dead code --
            // which is exactly what happened, and what the level editor's own Viewport.cs had
            // already written down.
            if (args.Contains("--hittest") && _vm.Tab is MeshTab hitTab)
            {
                var view = hitTab.View;
                var mid = view.Bounds.Size / 2;
                var inWindow = view.TranslatePoint(new Point(mid.Width, mid.Height), this);
                var got = inWindow is { } pt ? this.InputHitTest(pt) : null;
                var name = got?.GetType().Name ?? "nothing";
                _vm.Say(got is ModelView
                    ? $"--hittest: the pointer reaches the viewport ({name}) — drag will work"
                    : $"--hittest: the pointer lands on {name}, NOT the viewport — every mouse " +
                      "handler is dead code");
            }

            // --texexport <dir> / --teximport <file>: the two texture buttons, without a mouse.
            //
            // A file picker cannot be driven by a harness, so these call the SAME view-model
            // methods the buttons do. --teximport is the one that writes to a project, which is
            // why it is only ever pointed at a scratch copy.
            var tx = Array.IndexOf(args, "--texexport");
            if (tx >= 0 && tx + 1 < args.Length && _vm.Tab is TextureTab expTab)
            {
                var fmt = Array.IndexOf(args, "--texformat");
                if (fmt >= 0 && fmt + 1 < args.Length &&
                    TextureTab.Formats.FirstOrDefault(f =>
                        f.Name.Equals(args[fmt + 1], StringComparison.OrdinalIgnoreCase)) is { } pickFmt)
                    expTab.SelectedFormat = pickFmt;
                _vm.ExportTexture(expTab, args[tx + 1]);
            }

            var ti = Array.IndexOf(args, "--teximport");
            if (ti >= 0 && ti + 1 < args.Length && _vm.Tab is TextureTab impTab)
                _vm.ImportTexture(impTab, args[ti + 1]);

            // --texrevert: the right-click Revert, without a mouse. Reverting is the half that
            // decides whether an import is safe to try, so it needs proving as much as the import.
            if (args.Contains("--texrevert") && _vm.Tab is TextureTab revTab)
            {
                var pkg = PropertyEdit.PackageForFile(revTab.FilePath);
                if (pkg is null) _vm.Say($"--texrevert: no package name for {revTab.Title}");
                else _vm.RevertPackage(pkg, revTab.Title);
            }

            // --list <text> [max]: NAME the assets matching a term, into the log.
            //
            // --find sets the filter and reports a COUNT, which answers "are there any" and not
            // "which". Finding where the game keeps something — a colour palette, say — needs the
            // names, and reading them off a screenshot is not reading them.
            var ls = Array.IndexOf(args, "--list");
            if (ls >= 0 && ls + 1 < args.Length && _vm.GameIndex is { } gi)
            {
                // SEVERAL TERMS IN ONE MOUNT, and the ENGINE's own content skipped. The mount is
                // the whole cost of a run, and a first search for "Color" filled its cap with
                // Engine editor materials before reaching a single asset of the game's.
                var terms = args[ls + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
                var max = ls + 2 < args.Length && int.TryParse(args[ls + 2], out var m) ? m : 40;
                foreach (var want in terms)
                {
                    var hits = gi.Root.DescendantFiles()
                        .Where(f => !f.FullPath.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase) &&
                                    f.Name.Contains(want, StringComparison.OrdinalIgnoreCase) &&
                                    f.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                        .Take(max).ToList();
                    _vm.Say($"--list {want}: {hits.Count} shown");
                    foreach (var h in hits) _vm.Say($"   {h.Kind}  {h.FullPath}");
                }
            }

            // --tint <name>[,<name>,<name>]: pick colours by their in-game names, for Colour
            // 01/02/03. A tint is a picture and nothing else, so a still is the only check.
            var tn = Array.IndexOf(args, "--tint");
            if (tn >= 0 && tn + 1 < args.Length && _vm.Tab is MeshTab tintTab)
            {
                // The palette is read in the background; a run that asks for a colour has to wait
                // for it rather than quietly tint nothing.
                for (var waited = 0; waited < 120 && tintTab.Swatches.Count == 0; waited++)
                    await Task.Delay(250);

                var names = args[tn + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < names.Length && i < 3; i++)
                {
                    var hit = tintTab.Swatches.FirstOrDefault(
                        s => s.Name.Equals(names[i].Trim(), StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                    {
                        // NAMES SOME, because "no colour named X" against a palette read out of
                        // the game is unactionable without knowing what it does hold.
                        _vm.Say($"--tint: no colour named '{names[i]}'. e.g. " +
                                string.Join(", ", tintTab.Swatches.Take(12).Select(s => s.Name)));
                        continue;
                    }
                    if (i == 0) tintTab.Colour1 = hit;
                    else if (i == 1) tintTab.Colour2 = hit;
                    else tintTab.Colour3 = hit;
                    _vm.Say($"--tint {i + 1}: {hit.Name} #{hit.Hex}");
                }
                _vm.Say($"--tint: canTint={tintTab.CanTint}, palette {tintTab.Swatches.Count}");
                _vm.Say($"--tint: {tintTab.TintCensus}");

                // ASKED FOR, because it is off by default: it allocates per section per frame.
                // A frame also has to be drawn AFTER it is switched on, or this reports nothing
                // and reads exactly like a tint that bound nothing.
                UEToolkit.App.Rendering.RenderMesh.RecordTintDraw = true;
                tintTab.View.RequestNextFrameRendering();
                await Task.Delay(600);
                _vm.Say($"--tint: last draw bound {tintTab.TintDraw}");
            }

            // --playanim <name> [frame]: play a sequence on the open model, and hold a frame so
            // a still is deterministic. A playing animation photographs a different pose every
            // run, which would make two shots differ for reasons that are not the build.
            // --animloop <on|off> / --animspeed <x>: the two transport settings, which decide
            // whether anything that happens ONCE can be watched at all. Set before --playanim so
            // a sequence starts as asked rather than being changed a frame in.
            var al = Array.IndexOf(args, "--animloop");
            if (al >= 0 && al + 1 < args.Length && _vm.Tab is MeshTab loopTab)
                loopTab.Loop = args[al + 1].Equals("on", StringComparison.OrdinalIgnoreCase);

            var asp = Array.IndexOf(args, "--animspeed");
            if (asp >= 0 && asp + 1 < args.Length && _vm.Tab is MeshTab spTab &&
                double.TryParse(args[asp + 1], out var wantSpeed))
                spTab.Speed = wantSpeed;

            var pa = Array.IndexOf(args, "--playanim");
            if (pa >= 0 && pa + 1 < args.Length && _vm.Tab is MeshTab animTab)
            {
                for (var waited = 0; waited < 160 && animTab.Animations.Count == 0; waited++)
                    await Task.Delay(250);

                var want = args[pa + 1];
                var row = animTab.Animations.OfType<AnimRow>().FirstOrDefault(
                    r => r.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                    _vm.Say($"--playanim: nothing named '{want}' among {animTab.Animations.Count} rows");
                else
                {
                    await _vm.PlayAnimationAsync(animTab, row);
                    if (pa + 2 < args.Length && float.TryParse(args[pa + 2], out var atFrame))
                    {
                        animTab.View.PoseAt(atFrame);
                        _vm.Say($"--playanim {row.Name} held at frame {atFrame} of {animTab.View.AnimFrames}");
                    }

                    // WHAT THE TRANSPORT IS SET TO, because a checkbox and a slider prove they
                    // drew and this proves they reached the player.
                    _vm.Say($"[anim] loop={animTab.Loop}  speed={animTab.SpeedSays}  " +
                            $"frame={animTab.Frame:0}/{animTab.Frames}  has={animTab.HasAnim}");
                }
            }

            // --exportmodel <path> / --exportanim <path>: the two save dialogs, without a
            // mouse. Same view-model calls the buttons make.
            var em = Array.IndexOf(args, "--exportmodel");
            if (em >= 0 && em + 1 < args.Length && _vm.Tab is MeshTab expModel)
                _vm.ExportModel(expModel, args[em + 1]);

            var ea = Array.IndexOf(args, "--exportanim");
            if (ea >= 0 && ea + 1 < args.Length && _vm.Tab is MeshTab expAnim)
                _vm.ExportAnimation(expAnim, args[ea + 1]);

            // --importmodel <fbx>: the Import button on a GAME mesh's tab, which is how an override of a mesh is made (Mass
            // Deffect's Runa). Only ever pointed at a scratch project.
            var imo = Array.IndexOf(args, "--importmodel");
            if (imo >= 0 && imo + 1 < args.Length && _vm.Tab is MeshTab impModel)
                _vm.ImportModel(impModel, args[imo + 1]);

            // --editoverride <target fragment> [--ovtex <row fragment> <image>] [--ovview] [--ovrevert]: click an override in
            // the mod's list, as the author does, and say what opened -- its mesh row, its texture rows, the fit card. Then
            // optionally import an image over one texture row, press View on the mesh row, or Revert the mesh row, and say
            // what the project holds after each.
            var eo = Array.IndexOf(args, "--editoverride");
            if (eo >= 0 && eo + 1 < args.Length && _vm.Mod is { } ovMod)
            {
                var ovWant = args[eo + 1];
                if (ovMod.Overrides.FirstOrDefault(o => o.Target.Contains(ovWant, StringComparison.OrdinalIgnoreCase))
                    is not { } ovHit)
                    _vm.Say($"[override] no override matching '{ovWant}' among {ovMod.Overrides.Count}");
                else
                {
                    var before = _vm.Tab;
                    var leafOf = ovHit.Target[(ovHit.Target.LastIndexOf('/') + 1)..];
                    await _vm.OpenAssetAsync(new AssetRow { Name = leafOf, Type = "Override", Path = ovHit.Target,
                                                            Payload = ovHit, Modified = true });
                    var ovTab = _vm.Tab;
                    if (ReferenceEquals(ovTab, before) || ovTab is null)
                        _vm.Say("[override] nothing opened");
                    else if (ovTab is SheetTab ovSheet)
                    {
                        for (var spin = 0; spin < 80 && ovSheet.ReadsTextures && !ovSheet.HasTextureRows; spin++)
                            await Task.Delay(250);
                        _vm.Say($"[override] opened {ovTab.GetType().Name} '{ovTab.Title}'; mesh rows " +
                                string.Join(", ", ovSheet.MeshRows.Select(r => $"{r.Variant} = {r.GamePath} <- " +
                                    (r.Replacement.Length > 0 ? r.Replacement : "the game's own"))) +
                                $"; {ovSheet.TextureRows.Count} texture row(s); fit card {(ovSheet.SecFit.Present ? "shown" : "absent")}");
                        foreach (var r in ovSheet.TextureRows)
                            _vm.Say($"[override]    {r.Slot} {r.GameLeaf} <- " +
                                    (r.Replacement.Length > 0 ? r.Replacement : "the game's own"));

                        // THE MESH ROW'S BUTTONS AS DRAWN, read off the visual tree: a binding that fails shows the button.
                        await Task.Delay(500);
                        _vm.Say("[override] mesh row buttons drawn: " + string.Join(", ",
                            this.GetVisualDescendants().OfType<Button>()
                                .Where(b => b.DataContext is SheetTab.MeshRow && b.IsEffectivelyVisible)
                                .Select(b => b.Content as string ?? "?")));

                        var ovt = Array.IndexOf(args, "--ovtex");
                        if (ovt >= 0 && ovt + 2 < args.Length)
                        {
                            var rowWant = args[ovt + 1];
                            if (ovSheet.TextureRows.FirstOrDefault(
                                    r => r.GameLeaf.Contains(rowWant, StringComparison.OrdinalIgnoreCase)) is not { } texHit)
                                _vm.Say($"[override] no texture row matching '{rowWant}'");
                            else
                            {
                                _vm.ImportTextureRow(texHit, args[ovt + 2]);
                                _vm.Say($"[override] after the texture import: {texHit.GameLeaf} <- " +
                                        (texHit.Replacement.Length > 0 ? texHit.Replacement : "the game's own"));
                            }
                        }

                        if (args.Contains("--ovview") && ovSheet.MeshRows.Count > 0)
                        {
                            await _vm.ViewMeshRowAsync(ovSheet.MeshRows[0]);
                            await Task.Delay(4000);
                            _vm.Say($"[override] view: {(_vm.Tab as MeshTab)?.Title ?? "no mesh tab"} drew " +
                                    ((_vm.Tab as MeshTab)?.View.HasMesh == true ? "a mesh" : "NOTHING"));
                            var ovs = Array.IndexOf(args, "--ovshot");
                            if (ovs >= 0 && ovs + 1 < args.Length && _vm.Tab is MeshTab ovShotTab)
                            {
                                ovShotTab.View.ShotTo(args[ovs + 1]);
                                await Task.Delay(1500);
                            }
                            _vm.Tab = ovSheet;
                        }

                        if (args.Contains("--ovrevert") && ovSheet.MeshRows.Count > 0)
                        {
                            RevertMeshRowOn(ovSheet.MeshRows[0]);
                            _vm.Say($"[override] after Revert: mesh row <- " +
                                    (ovSheet.MeshRows.FirstOrDefault()?.Replacement is { Length: > 0 } back ? back : "the game's own"));
                        }

                        // --ovwear <slot> <material package>: Wear a game material on one slot, as the material card does.
                        var ovw = Array.IndexOf(args, "--ovwear");
                        if (ovw >= 0 && ovw + 2 < args.Length && int.TryParse(args[ovw + 1], out var ovwSlot) &&
                            ovwSlot >= 0 && ovwSlot < ovSheet.MaterialGroups.Count)
                            _vm.WearMaterial(ovSheet, ovSheet.MaterialGroups[ovwSlot], args[ovw + 2]);

                        // --ovanysize: tick "Let a replacement be a very different size" and press Save, as the author does.
                        if (args.Contains("--ovanysize"))
                        {
                            _vm.Say($"[override] size rule before: box {ovSheet.EditAnySize}, project " +
                                    (ovMod.Overrides.FirstOrDefault(o => o.Target == ovHit.Target)?.MeshAnySize.ToString() ?? "no override"));
                            ovSheet.EditAnySize = true;
                            SaveEditOn();
                            _vm.Say("[override] size rule after Save: project " +
                                    (ovMod.Overrides.FirstOrDefault(o => o.Target == ovHit.Target)?.MeshAnySize.ToString() ?? "no override") +
                                    $", unsaved changes {ovSheet.DirtyCount}");
                        }

                        // WHAT THE SHEET SAYS NOW, after any of the above.
                        _vm.Say("[override] sheet: " + string.Join(" | ", ovSheet.Sheet.Select(b => b switch
                        {
                            SheetFact f => $"{f.Label}={f.Value}",
                            SheetBand band => band.Text,
                            SheetBullet bullet => bullet.Text,
                            _ => b.GetType().Name,
                        })));
                    }
                    else _vm.Say($"[override] opened {ovTab.GetType().Name} '{ovTab.Title}'");

                    _vm.Say("[override] project now: " + string.Join(" | ", ovMod.Overrides.Select(o =>
                        o.Target[(o.Target.LastIndexOf('/') + 1)..] +
                        (o.MeshFile.Length > 0 ? " mesh=" + o.MeshFile : "") +
                        (o.Image.Length > 0 ? " image=" + o.Image : ""))));
                }
            }

            var gs = Array.IndexOf(args, "--glshot");
            if (gs >= 0 && gs + 1 < args.Length)
            {
                var settle = gs + 2 < args.Length && double.TryParse(args[gs + 2], out var waitFor)
                    ? waitFor : 3.0;

                await Task.Delay(TimeSpan.FromSeconds(settle));

                // EITHER VIEWPORT. Both draw GL into a surface PrintWindow reads as black, so
                // both need the framebuffer route; keying this to the mesh tab alone left the
                // texture viewer with no way to be photographed at all.
                switch (_vm.Tab)
                {
                    case MeshTab shot: shot.View.ShotTo(args[gs + 1]); break;
                    case TextureTab tex: tex.View.ShotTo(args[gs + 1]); break;
                }
            }

            // --collapsed: start with the column shut, so the state can be photographed.
            if (args.Contains("--collapsed")) SetPanel(false);

            // --uninstall / --build: the bar's two buttons, without a mouse.
            //
            // The ONE flag pair that can witness the whole of it. A build writes six files, reads
            // them back and copies them into a game folder, and none of those three is visible in
            // a screenshot of the bar — what a picture shows is a sentence claiming they happened.
            //
            // Uninstall runs FIRST when both are asked for, because an installed copy of this mod
            // makes every AssetRegistry.bin in the game a _P patch and the build refuses to stack
            // itself on one. Two flags in one run is how that sequence gets tested.
            if (args.Contains("--uninstall")) await _vm.UninstallAsync();
            if (args.Contains("--build"))
            {
                await _vm.BuildAndInstallAsync();
                _vm.Say($"[build] says={_vm.BuildSays}  failed={_vm.BuildFailed}");
                _vm.Say($"[build] {_vm.BuildStatus}");
            }

            // --share: the "Zip it for sharing" button, without a mouse (the real handler's work:
            // the same ShareAsync). Runs AFTER --build, so one launch can build, install and zip.
            // The [share] lines are what a test reads; the zip's own entries are in the log above.
            if (args.Contains("--share"))
            {
                ShowLog();   // as the button's own handler does: the packing lines are what to read
                _vm.Say($"[share] button live before pressing: {_vm.CanShare}");
                await _vm.ShareAsync();
                _vm.Say($"[share] failed={_vm.BuildFailed}  zip={_vm.ShareZip ?? "(none)"}");
                _vm.Say($"[share] {_vm.BuildStatus}");
            }

            // --listblenders: what the Plugins window would find, and what is stale. The proof, before
            // any write in a test, that the scratch root is the ONLY root in play.
            if (args.Contains("--listblenders"))
            {
                _vm.Say($"[blenders] searching only: {BlenderPlugin.BaseDirectory()}  (override {(BlenderPlugin.RootOverride is null ? "NOT set" : "set")})");
                var all = BlenderPlugin.Installs();
                _vm.Say($"[blenders] found {all.Count}");
                foreach (var b in all)
                    _vm.Say($"[blenders]   Blender {b.Version}: {b.Target}  addon={(b.Present ? b.AddonVersion ?? "no version" : "none")}  backend={b.BackendNamed ?? "-"}  " +
                            $"stale={(BlenderPlugin.StaleOf(b) is { } staleInfo ? staleInfo.Why : "no")}");
                _vm.Say($"[blenders] this Studio ships add-on {BlenderPlugin.ShippedVersion() ?? "(none)"}; blender running: {BlenderPlugin.BlenderRunning()}");
            }

            // --addonguard: ask the write guard its question and print the answer, WRITING NOTHING.
            // The way to prove the guard without pointing a possibly-broken one at a real Blender.
            if (args.Contains("--addonguard"))
            {
                var allowed = StudioVm.AddonWriteAllowed(out var why);
                _vm.Say($"[addonguard] background={BackgroundMode.On}  root override={(BlenderPlugin.RootOverride is null ? "NOT set" : "set")}  -> writes {(allowed ? "ALLOWED" : "REFUSED: " + why)}");
            }

            // --addonupdate: the notice's button ("Update the Blender add-on"), pressed through its
            // own handler. --addoninstall <ver> / --addonremove <ver>: the Plugins window's row
            // buttons, on a window that is built and closed again (no window is left up).
            if (args.Contains("--addonupdate")) OnUpdateAddon(null, new RoutedEventArgs());
            foreach (var flag in new[] { "--addoninstall", "--addonremove" })
            {
                var fi = Array.IndexOf(args, flag);
                if (fi < 0 || fi + 1 >= args.Length) continue;
                var win = new PluginsWindow(_vm);
                var ok = flag == "--addoninstall" ? win.PressInstall(args[fi + 1]) : win.PressRemove(args[fi + 1]);
                _vm.Say($"[plugins] {flag} {args[fi + 1]}: {(ok ? "pressed" : "no such Blender row")}");
                win.Close();
            }

            // --plugins: open the Plugins window through the menu entry's handler and leave it up
            // (SHOWN, not awaited), so it can be photographed. --pluginssave <folder> presses both
            // "Save the ..." buttons' work into that folder, as a chosen folder would;
            // --pluginsbuttons <ver> presses that Blender row's Install/Update button in it.
            if (args.Contains("--plugins"))
            {
                var ps = Array.IndexOf(args, "--pluginssave");
                var saveInto = ps >= 0 && ps + 1 < args.Length ? args[ps + 1] : null;
                var pb = Array.IndexOf(args, "--pluginsbuttons");
                PluginsHook = w => Dispatcher.UIThread.Post(() =>
                {
                    if (saveInto is not null) { w.SavePresetInto(saveInto); w.SaveScriptInto(saveInto); }
                    if (pb >= 0 && pb + 1 < args.Length) w.PressInstall(args[pb + 1]);
                }, DispatcherPriority.Background);
                OnOpenPlugins(null, new RoutedEventArgs());
            }

            // --showtip <ControlName>: open that control's tooltip and leave it open, so it can be
            // photographed (no mouse to hover with). Popups draw inside the window in a background
            // run, so the picture is the whole window. Needs the control to be laid out first.
            var tipAt = Array.IndexOf(args, "--showtip");
            if (tipAt >= 0 && tipAt + 1 < args.Length)
            {
                await Task.Delay(1500);
                if (this.FindControl<Control>(args[tipAt + 1]) is { } tipOn)
                {
                    ToolTip.SetIsOpen(tipOn, true);
                    _vm.Say($"--showtip {args[tipAt + 1]}: tooltip opened");
                }
                else _vm.Say($"--showtip {args[tipAt + 1]}: no such control");
            }

            // --removeplan / --removemine / --removeask <fragment>: taking something back OUT.
            //
            // THREE FLAGS BECAUSE THERE ARE THREE THINGS TO CHECK, and the middle one is the
            // one a screenshot cannot reach. --removeplan prints the sentence the dialog will
            // show and changes nothing; --removemine calls the same removal the dialog's button
            // calls; --removeask opens the real dialog and leaves it up, so what he will
            // actually read can be photographed.
            var rmp = Array.IndexOf(args, "--removeplan");
            var rmd = Array.IndexOf(args, "--removemine");
            var rma = Array.IndexOf(args, "--removeask");

            if (rmp >= 0 || rmd >= 0 || rma >= 0)
            {
                var named = rmp >= 0 ? rmp : rmd >= 0 ? rmd : rma;
                var want = named + 1 < args.Length ? args[named + 1] : "";

                if (_vm.FindMine(want) is not { } mine)
                    _vm.Say($"--remove: nothing of this mod's matches '{want}'");
                else if (_vm.WhatRemoving(mine) is not { } plan)
                    _vm.Say($"--remove: {mine.GetType().Name} has no removal built for it");
                else
                {
                    _vm.Say($"[remove] heading   {plan.Heading}");
                    _vm.Say($"[remove] detail    {plan.Detail}");
                    _vm.Say($"[remove] warn      {(plan.Warn.Length > 0 ? plan.Warn : "—")}");
                    _vm.Say($"[remove] aside     {(plan.Aside.Length > 0 ? plan.Aside : "—")}");
                    _vm.Say($"[remove] button    {plan.Button}");

                    if (rmd >= 0) _vm.Say($"[remove] done={_vm.RemoveFromMod(plan)}");

                    if (rma >= 0)
                    {
                        // SHOWN, NOT AWAITED. ShowDialog would block this whole block until
                        // somebody clicked, and the thing taking the picture is downstream of
                        // it — so the run would hang and the photograph would never be taken.
                        var ask = new ConfirmWindow(plan.Heading, plan.Detail, plan.Warn,
                                                    plan.Aside, plan.Button);
                        ask.Show(this);
                    }
                }
            }

            // THE SPEC PANEL'S FLAGS. A specialization has no meshes and no textures, so the
            // flags that reach those would be aimed at cards that are deliberately absent.
            // THE GENERIC COPY'S FLAGS. What is inside it is the whole reason this panel
            // exists, so the flags reach a reference BY PROPERTY NAME — which is what somebody
            // reading the dump has in front of them.
            async Task DriveMade(CopyTab ct)
            {
                // The references arrive on a background read; nothing below can find a row until
                // they do, and a driver that raced them would report "no reference matching" on
                // a panel that was about to be full of them.
                for (var spin = 0; spin < 80 && !ct.HasInside &&
                                   ct.InsideNote.Length == 0; spin++)
                    await Task.Delay(250);

                var rnc = Array.IndexOf(args, "--rename");
                if (rnc >= 0 && rnc + 1 < args.Length) ct.EditName = args[rnc + 1];

                var dsc = Array.IndexOf(args, "--setsays");
                if (dsc >= 0 && dsc + 1 < args.Length) ct.EditDescription = args[dsc + 1];

                var sic = Array.IndexOf(args, "--setid");
                if (sic >= 0 && sic + 1 < args.Length) ct.EditId = args[sic + 1];

                // --setlong <text>: an ability copy's third box, UIData.LongDescription.
                var slc = Array.IndexOf(args, "--setlong");
                if (slc >= 0 && slc + 1 < args.Length) ct.EditLongDescription = args[slc + 1];

                if (rnc >= 0 || dsc >= 0 || sic >= 0 || slc >= 0) _vm.SaveCopyEdit(ct);

                var fil = Array.IndexOf(args, "--insidefind");
                if (fil >= 0 && fil + 1 < args.Length) ct.InsideFilter = args[fil + 1];

                // --pointat <property fragment> <package>: move one reference.
                var pat = Array.IndexOf(args, "--pointat");
                if (pat >= 0 && pat + 2 < args.Length)
                {
                    var hit = ct.InsideRows.FirstOrDefault(
                        r => r.Property.Contains(args[pat + 1], StringComparison.OrdinalIgnoreCase) ||
                             r.GameLeaf.Contains(args[pat + 1], StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--pointat: no reference matching '{args[pat + 1]}' among " +
                                $"{ct.InsideRows.Count}");
                    else _vm.PointInside(ct, hit, args[pat + 2]);
                }

                var unp = Array.IndexOf(args, "--unpoint");
                if (unp >= 0 && unp + 1 < args.Length)
                {
                    var hit = ct.InsideRows.FirstOrDefault(
                        r => r.Property.Contains(args[unp + 1], StringComparison.OrdinalIgnoreCase) ||
                             r.GameLeaf.Contains(args[unp + 1], StringComparison.OrdinalIgnoreCase));
                    if (hit is null) _vm.Say($"--unpoint: no reference matching '{args[unp + 1]}'");
                    else _vm.RevertInside(ct, hit);
                }

                // --sell <slot fragment>: take the first slot whose description matches.
                // Named by what it sells today, which is how the chooser names it too.
                var sl = Array.IndexOf(args, "--sell");
                if (sl >= 0 && sl + 1 < args.Length)
                {
                    for (var spin = 0; spin < 60 && !ct.HasShopSlots && !ct.IsOnSale; spin++)
                        await Task.Delay(250);

                    var want = args[sl + 1];
                    var hit = ct.ShopSlots.FirstOrDefault(
                        s => s.Describe.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--sell: no slot matching '{want}' among {ct.ShopSlots.Count}");
                    else { ct.SellSlot = hit; await _vm.PutOnSale(ct); }
                }

                if (args.Contains("--unsell")) await _vm.TakeOffSale(ct);

                if (args.Contains("--removecopy")) _vm.RemoveCopy(ct);

                var cc = Array.IndexOf(args, "--ccard");
                if (cc >= 0 && cc + 1 < args.Length)
                    foreach (var s in ct.EditSections)
                        s.Open = s.Key.Equals(args[cc + 1], StringComparison.OrdinalIgnoreCase);
            }

            // THE MESH COPY'S. Two cards, so two things to drive: the geometry, and the paint —
            // and the paint flags are the part editor's own, because the surface is the same one.
            async Task DriveMesh(ReplacementTab rt)
            {
                for (var spin = 0; spin < 80 && !rt.HasTextureRows; spin++) await Task.Delay(250);

                var imp = Array.IndexOf(args, "--meshimport");
                if (imp >= 0 && imp + 1 < args.Length && rt.MeshRows.Count > 0)
                    _vm.ImportMeshRow(rt.MeshRows[0], args[imp + 1]);

                if (args.Contains("--meshrevert") && rt.MeshRows.Count > 0)
                    _vm.RevertReplacementRow(rt, rt.MeshRows[0]);

                var wm = Array.IndexOf(args, "--wearmat");
                if (wm >= 0 && wm + 2 < args.Length &&
                    int.TryParse(args[wm + 1], out var wmSlot) &&
                    wmSlot >= 0 && wmSlot < rt.MaterialGroups.Count)
                    _vm.WearMaterial(rt, rt.MaterialGroups[wmSlot], args[wm + 2]);

                var rsn = Array.IndexOf(args, "--slotname");
                if (rsn >= 0 && rsn + 2 < args.Length &&
                    int.TryParse(args[rsn + 1], out var rsnSlot) &&
                    rsnSlot >= 0 && rsnSlot < rt.MaterialGroups.Count)
                    _vm.SetMaterialSlotName(rt, rt.MaterialGroups[rsnSlot], args[rsn + 2]);

                if (args.Contains("--addmat")) _vm.AddMaterialSlot(rt, "");

                for (var spin = 0; spin < 40 && rt.SecFit.Present && !rt.CanFit &&
                                   rt.FitCounts.StartsWith("measuring", StringComparison.Ordinal);
                     spin++)
                    await Task.Delay(250);
                DriveFit(rt);

                var mc2 = Array.IndexOf(args, "--ccard");
                if (mc2 >= 0 && mc2 + 1 < args.Length)
                    foreach (var s in rt.EditSections)
                        s.Open = s.Key.Equals(args[mc2 + 1], StringComparison.OrdinalIgnoreCase);
            }

            // THE FIT FLAGS, SHARED. The card is on SheetTab and two kinds of tab draw it, so
            // a copy of this in each driver would be two chances for one of them to drift.
            void DriveFit(SheetTab ft)
            {
                // --fitpiece <key>: which piece the card is placing, BEFORE anything is set.
                // The five fields write to whatever FitTarget resolves to, so choosing the piece
                // afterwards would put the values on the wrong one.
                var fp = Array.IndexOf(args, "--fitpiece");
                if (fp >= 0 && fp + 1 < args.Length)
                {
                    if (ft.FitPieces.Contains(args[fp + 1], StringComparer.OrdinalIgnoreCase))
                    {
                        ft.FitPiece = args[fp + 1];
                        // WHAT IT LANDED ON, not what it was asked for. Setting the piece refills
                        // the card, and a refill that finds the piece gone puts the card back on
                        // the first one — silently, which read as the flag being ignored.
                        _vm.Say($"--fitpiece {args[fp + 1]}: now on '{ft.FitPiece}' " +
                                $"of [{string.Join(", ", ft.FitPieces)}]");
                    }
                    else
                        _vm.Say($"--fitpiece: no piece '{args[fp + 1]}' with an import — " +
                                (ft.FitPieces.Count == 0 ? "none have one"
                                                         : string.Join(", ", ft.FitPieces)));
                }

                var fb = Array.IndexOf(args, "--fitbasis");
                if (fb >= 0 && fb + 1 < args.Length)
                    _vm.SetFit(ft, "basis", args[fb + 1] == "none" ? "" : args[fb + 1]);

                var fs = Array.IndexOf(args, "--fitscale");
                if (fs >= 0 && fs + 1 < args.Length)
                    _vm.SetFit(ft, "scale", args[fs + 1] == "none" ? "" : args[fs + 1]);

                // ONE FLAG, THREE VALUES, because a move is one act — setting X and coming back
                // for Y would re-measure the FBX between them for no reason.
                var fm = Array.IndexOf(args, "--fitmove");
                if (fm >= 0 && fm + 3 < args.Length)
                {
                    _vm.SetFit(ft, "x", args[fm + 1]);
                    _vm.SetFit(ft, "y", args[fm + 2]);
                    _vm.SetFit(ft, "z", args[fm + 3]);
                }

                if (args.Contains("--fitcentre")) _vm.PlaceFit(ft, "centre");
                if (args.Contains("--fitrest")) _vm.PlaceFit(ft, "rest");
                if (args.Contains("--fitnomove")) _vm.PlaceFit(ft, "clear");
                if (args.Contains("--fitmatch")) _vm.PlaceFit(ft, "fitscale");
                if (args.Contains("--fitpin1")) _vm.PlaceFit(ft, "pinone");
            }

            // A MISSION'S FLAGS. Its own driver: a mission has no mesh, no tint and no icon
            // of the kind a part wears, and what it does have is five choosers and a dozen
            // fields that no other panel here carries.
            async Task DriveMission(MissionTab mt)
            {
                var rnm = Array.IndexOf(args, "--rename");
                if (rnm >= 0 && rnm + 1 < args.Length) mt.EditName = args[rnm + 1];

                var brf = Array.IndexOf(args, "--briefing");
                if (brf >= 0 && brf + 1 < args.Length) mt.EditBriefing = args[brf + 1];

                var sq = Array.IndexOf(args, "--squad");
                if (sq >= 0 && sq + 2 < args.Length)
                { mt.EditMinSquad = args[sq + 1]; mt.EditMaxSquad = args[sq + 2]; }

                var lf = Array.IndexOf(args, "--lifespan");
                if (lf >= 0 && lf + 1 < args.Length) mt.EditLifeSpan = args[lf + 1];

                var sid = Array.IndexOf(args, "--setid");
                if (sid >= 0 && sid + 1 < args.Length) mt.EditId = args[sid + 1];

                if (rnm >= 0 || brf >= 0 || sq >= 0 || lf >= 0 || sid >= 0)
                    _vm.SaveMissionEdit(mt);

                // THE CHOOSERS WRITE ON SELECTION, so the flag sets the selection and lets the
                // same path run — a flag that called PickMission directly would exercise a route
                // the mouse never takes.
                await Choose("--planet", mt.PlanetChoices, c => mt.PickedPlanet = c);
                await Choose("--map", mt.LevelChoices, c => mt.PickedLevel = c);
                await Choose("--youfight", mt.FactionChoices, c => mt.PickedFaction = c);
                await Choose("--objective", mt.TypeChoices, c => mt.PickedType = c);
                await Choose("--difficulty", mt.DifficultyChoices, c => mt.PickedDifficulty = c);

                if (args.Contains("--matchplanet")) await _vm.MatchThePlanet(mt);

                // --addturn <arc> <turn>: the route that costs nothing.
                var at2 = Array.IndexOf(args, "--addturn");
                if (at2 >= 0 && at2 + 2 < args.Length)
                {
                    var hit = mt.Turns.FirstOrDefault(
                        x => x.Arc.Contains(args[at2 + 1], StringComparison.OrdinalIgnoreCase) &&
                             x.Number.ToString() == args[at2 + 2]);
                    if (hit is null)
                        _vm.Say($"--addturn: no {args[at2 + 1]} turn {args[at2 + 2]} among {mt.Turns.Count}");
                    else { hit.Taken = true; _vm.SetArcAdd(mt, hit); }
                }

                // --takeslot <mission fragment>: the route that displaces one.
                var ts = Array.IndexOf(args, "--takeslot");
                if (ts >= 0 && ts + 1 < args.Length)
                {
                    var hit = mt.CampaignSlots.FirstOrDefault(
                        x => x.Leaf.Contains(args[ts + 1], StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--takeslot: nothing matching '{args[ts + 1]}' among " +
                                $"{mt.CampaignSlots.Count}");
                    else { hit.Taken = true; _vm.SetPlacement(mt, hit); }
                }

                // --lottery <table fragment> [weight]: the route that may never be drawn.
                var lt = Array.IndexOf(args, "--lottery");
                if (lt >= 0 && lt + 1 < args.Length)
                {
                    await _vm.AddLottery(mt);
                    if (mt.LotteryRows.LastOrDefault() is { } row)
                    {
                        var want = mt.TableChoices.FirstOrDefault(
                            c => c.Shown.Contains(args[lt + 1], StringComparison.OrdinalIgnoreCase));
                        if (want is null)
                            _vm.Say($"--lottery: no table matching '{args[lt + 1]}'");
                        else { row.Table = want.Value; _vm.SetLottery(mt, row, "table"); }

                        if (lt + 2 < args.Length && double.TryParse(args[lt + 2],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out _))
                        { row.Weight = args[lt + 2]; _vm.SetLottery(mt, row, "weight"); }

                        if (args.Contains("--takeover"))
                        { row.AddsRow = false; _vm.SetLottery(mt, row, "how"); }
                        if (args.Contains("--everyrow")) await _vm.TakeEveryRow(mt, row);
                    }
                }

                var mc = Array.IndexOf(args, "--mscard");
                if (mc >= 0 && mc + 1 < args.Length)
                    foreach (var s in mt.EditSections)
                        s.Open = s.Key.Equals(args[mc + 1], StringComparison.OrdinalIgnoreCase);

                async Task Choose(string flag,
                                  System.Collections.ObjectModel.ObservableCollection<SheetTab.Choice> from,
                                  Action<SheetTab.Choice> onto)
                {
                    var at = Array.IndexOf(args, flag);
                    if (at < 0 || at + 1 >= args.Length) return;
                    var want = args[at + 1];
                    var hit = from.FirstOrDefault(
                        c => c.Shown.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                             c.Value.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                    { _vm.Say($"{flag}: nothing matching '{want}' among {from.Count}"); return; }

                    // SETTING THE SELECTION IS THE WHOLE ACT. SelectionChanged fires and the
                    // handler writes, exactly as it does for a mouse — calling PickMission here
                    // as well wrote every change twice, which the log said plainly.
                    onto(hit);
                    await Task.Delay(120);
                }
            }

            // AN ENEMY GROUP'S FLAGS. Its own driver: a group has no name, no mesh and no id,
            // and the one thing it does have is a table of multipliers.
            async Task DriveEnemy(EnemyTab et)
            {
                for (var spin = 0; spin < 80 && !et.HasEnemyRows &&
                                   !et.EnemyNote.StartsWith("none", StringComparison.Ordinal);
                     spin++)
                    await Task.Delay(250);

                // --enemymult <stat fragment> <x>: one stat. Repeatable is not needed — one
                // write exercises the whole path, and the dump prints every row either way.
                var em = Array.IndexOf(args, "--enemymult");
                if (em >= 0 && em + 2 < args.Length)
                {
                    var hit = et.EnemyRows.FirstOrDefault(
                        r => r.Shown.Contains(args[em + 1], StringComparison.OrdinalIgnoreCase) ||
                             r.Row.Contains(args[em + 1], StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--enemymult: no stat matching '{args[em + 1]}' among " +
                                $"{et.EnemyRows.Count}");
                    else _vm.SetEnemyMultiplier(et, hit, args[em + 2]);
                }

                if (args.Contains("--enemyclear")) _vm.ClearEnemyGroup(et);
            }

            // THE PROJECT'S OWN PANEL. Its own driver, because none of the other three reach
            // any of these fields — a mod has no id, no crest and no pieces, and what it does
            // have reaches outside the project file, which is exactly what has to be checkable
            // without a mouse.
            void DriveMod(ModTab mt)
            {
                var rnm = Array.IndexOf(args, "--rename");
                if (rnm >= 0 && rnm + 1 < args.Length) mt.EditName = args[rnm + 1];

                var dsc = Array.IndexOf(args, "--setsays");
                if (dsc >= 0 && dsc + 1 < args.Length) mt.EditDescription = args[dsc + 1];

                var aut = Array.IndexOf(args, "--setauthor");
                if (aut >= 0 && aut + 1 < args.Length) mt.EditAuthor = args[aut + 1];

                var ver = Array.IndexOf(args, "--setversion");
                if (ver >= 0 && ver + 1 < args.Length) mt.EditVersion = args[ver + 1];

                // --setfolder takes a BAD one as readily as a good one, deliberately: the refusal
                // is the thing worth checking, and a driver that only ever passed folders the
                // validator likes would never exercise it.
                var fld = Array.IndexOf(args, "--setfolder");
                if (fld >= 0 && fld + 1 < args.Length) mt.EditFolder = args[fld + 1];

                var shp = Array.IndexOf(args, "--shipsas");
                if (shp >= 0 && shp + 1 < args.Length) mt.EditShipsAs = args[shp + 1];

                var bys = Array.IndexOf(args, "--byscript");
                if (bys >= 0 && bys + 1 < args.Length)
                    mt.EditByScript = args[bys + 1] is "on" or "yes" or "true";

                var rky = Array.IndexOf(args, "--recruitkey");
                if (rky >= 0 && rky + 1 < args.Length) mt.EditRecruitKey = args[rky + 1];

                var pky = Array.IndexOf(args, "--repairkey");
                if (pky >= 0 && pky + 1 < args.Length) mt.EditRepairKey = args[pky + 1];

                // --modpic <file>: import a picture for the card. Written as it happens, like
                // every other import here, so it needs no save of its own.
                var pic = Array.IndexOf(args, "--modpic");
                if (pic >= 0 && pic + 1 < args.Length) _vm.SetModPicture(mt, args[pic + 1]);

                if (args.Contains("--modpicclear")) _vm.ClearModPicture(mt);

                if (rnm >= 0 || dsc >= 0 || aut >= 0 || ver >= 0 || fld >= 0 || shp >= 0 ||
                    bys >= 0 || rky >= 0 || pky >= 0 || pic >= 0 || args.Contains("--modpicclear"))
                    _vm.SaveModDetails(mt);

                var mc = Array.IndexOf(args, "--mcard");
                if (mc >= 0 && mc + 1 < args.Length)
                    foreach (var s in mt.EditSections)
                        s.Open = s.Key.Equals(args[mc + 1], StringComparison.OrdinalIgnoreCase);
            }

            void DriveSpec(SpecTab st)
            {
                var rn3 = Array.IndexOf(args, "--rename");
                if (rn3 >= 0 && rn3 + 1 < args.Length) st.EditName = args[rn3 + 1];

                var si3 = Array.IndexOf(args, "--setid");
                if (si3 >= 0 && si3 + 1 < args.Length) st.EditId = args[si3 + 1];

                var cw = Array.IndexOf(args, "--crest");
                if (cw >= 0 && cw + 1 < args.Length) st.EditCrest = args[cw + 1];

                var fw = Array.IndexOf(args, "--forwho");
                if (fw >= 0 && fw + 1 < args.Length) st.EditForWho = args[fw + 1];

                if (rn3 >= 0 || si3 >= 0 || cw >= 0 || fw >= 0) _vm.SaveSpecEdit(st);

                // --rung <n> <which> <value>: one number on one rung, by its place in the flat
                // list. The build refuses to RAISE a number the game ships as zero, so a test
                // that only checked the panel would miss the half that matters.
                var rg = Array.IndexOf(args, "--rung");
                if (rg >= 0 && rg + 3 < args.Length && int.TryParse(args[rg + 1], out var rgAt) &&
                    rgAt >= 0 && rgAt < st.Rungs.Count)
                    _vm.SetRung(st, st.Rungs[rgAt], args[rg + 2], args[rg + 3]);

                // --grant <path> [gear]: hand it something outright.
                var gr = Array.IndexOf(args, "--grant");
                if (gr >= 0 && gr + 1 < args.Length)
                    _vm.SetGrant(st, null, args[gr + 1],
                                 gear: gr + 2 < args.Length && args[gr + 2] == "gear");

                // --ungrant <leaf>: take one away.
                var ug = Array.IndexOf(args, "--ungrant");
                if (ug >= 0 && ug + 1 < args.Length)
                {
                    var hit = st.Grants.FirstOrDefault(
                        g => g.Leafed.Contains(args[ug + 1], StringComparison.OrdinalIgnoreCase));
                    if (hit is null) _vm.Say($"--ungrant: nothing matching '{args[ug + 1]}'");
                    else _vm.SetGrant(st, hit, "", hit.IsGear);
                }

                var sc = Array.IndexOf(args, "--scard");
                if (sc >= 0 && sc + 1 < args.Length)
                    foreach (var s in st.EditSections)
                        s.Open = s.Key.Equals(args[sc + 1], StringComparison.OrdinalIgnoreCase);
            }

            // THE WEAPON PANEL'S FLAGS. Its own driver rather than a branch of DrivePanel: a
            // weapon has no swatches, no gate tags, no takeovers and no slot, and the flags that
            // reach those would be flags aimed at cards that are deliberately absent.
            async Task DrivePanel2(WeaponTab wt)
            {
                var rn2 = Array.IndexOf(args, "--rename");
                if (rn2 >= 0 && rn2 + 1 < args.Length) wt.EditName = args[rn2 + 1];

                var si2 = Array.IndexOf(args, "--setid");
                if (si2 >= 0 && si2 + 1 < args.Length) wt.EditId = args[si2 + 1];

                // --mesh <file>: an FBX onto a PIECE, named by its slot. A weapon has one row
                // per slot, so the flag needs to say which — unlike a part, where the rows are
                // body types of one garment.
                var wm2 = Array.IndexOf(args, "--piece");
                if (wm2 >= 0 && wm2 + 2 < args.Length)
                {
                    for (var spin = 0; spin < 60 && wt.MeshRows.Count == 0; spin++)
                        await Task.Delay(100);

                    var onSlot = args[wm2 + 1];
                    var hit = wt.MeshRows.FirstOrDefault(
                        r => r.Variant.Contains(onSlot, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--piece: no slot matching '{onSlot}' — " +
                                string.Join(", ", wt.MeshRows.Select(r => r.Variant)));
                    else _vm.ImportWeaponPiece(wt, hit, args[wm2 + 2]);
                }

                if (args.Contains("--completemodel")) _vm.CompleteWeaponModel(wt);

                // --wtex <row> <file>: paint one of the weapon's textures. The card is shared
                // with the part editor and so is the write, so this is here only because the
                // rows arrive on a background read and the flag has to wait for them.
                var wt2 = Array.IndexOf(args, "--wtex");
                if (wt2 >= 0 && wt2 + 2 < args.Length)
                {
                    for (var spin = 0; spin < 80 && wt.TextureRows.Count == 0; spin++)
                        await Task.Delay(250);

                    var want = args[wt2 + 1];
                    var hit = wt.TextureRows.FirstOrDefault(
                        r => r.Slot.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                             r.GameLeaf.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--wtex: no texture matching '{want}' — " +
                                string.Join(", ", wt.TextureRows.Select(r => r.Slot)));
                    else _vm.ImportTextureRow(hit, args[wt2 + 2]);
                }

                var wtr = Array.IndexOf(args, "--wtexrevert");
                if (wtr >= 0 && wtr + 1 < args.Length)
                {
                    var want = args[wtr + 1];
                    var hit = wt.TextureRows.FirstOrDefault(
                        r => r.Slot.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) _vm.Say($"--wtexrevert: no texture matching '{want}'");
                    else _vm.RevertTextureRow(hit);
                }

                // --bolt <n> / --shelf custom|game / --ownstats / --stat <label> <value>:
                // the three cards a weapon has and a part does not. Each writes into a different
                // part of the chain — the actor, the armoury part, an attribute table — and
                // none of those is visible in a picture of the card.
                // --ownshelf <name> [behaves-like]: a shelf of this mod's own.
                var osh = Array.IndexOf(args, "--ownshelf");
                if (osh >= 0 && osh + 1 < args.Length)
                {
                    wt.EditShelfName = args[osh + 1];
                    if (osh + 2 < args.Length && wt.ShelfLikes.Contains(args[osh + 2],
                                                     StringComparer.OrdinalIgnoreCase))
                        wt.EditShelfLike = wt.ShelfLikes.First(
                            x => x.Equals(args[osh + 2], StringComparison.OrdinalIgnoreCase));
                    _vm.MakeOwnShelf(wt);
                }

                var bl = Array.IndexOf(args, "--bolt");
                if (bl >= 0 && bl + 1 < args.Length && int.TryParse(args[bl + 1], out var boltAt))
                    _vm.SetWeaponBolt(wt, boltAt);

                var sh = Array.IndexOf(args, "--shelf");
                if (sh >= 0 && sh + 1 < args.Length)
                    _vm.SetWeaponShelf(wt, args[sh + 1].Equals("custom", StringComparison.OrdinalIgnoreCase));

                if (args.Contains("--ownstats")) _vm.GiveWeaponOwnStats(wt);

                var st2 = Array.IndexOf(args, "--stat");
                if (st2 >= 0 && st2 + 2 < args.Length)
                {
                    var want = args[st2 + 1];
                    var hit = wt.WeaponStats.FirstOrDefault(
                        s => s.Label.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--stat: no number matching '{want}' — " +
                                string.Join(", ", wt.WeaponStats.Take(10).Select(s => s.Label)));
                    else _vm.SetWeaponStat(wt, hit, args[st2 + 2]);
                }

                var ft = Array.IndexOf(args, "--fits");
                if (ft >= 0 && ft + 1 < args.Length)
                    _vm.SetWeaponFits(wt, args[ft + 1].Equals("own", StringComparison.OrdinalIgnoreCase));

                var att = Array.IndexOf(args, "--attach");
                if (att >= 0 && att + 2 < args.Length)
                {
                    var want = args[att + 1];
                    var hit = wt.Attachments.FirstOrDefault(
                        a => a.Slot.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                             a.Leaf.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--attach: nothing matching '{want}' — " +
                                string.Join(", ", wt.Attachments.Take(10).Select(a => a.Slot)));
                    else
                        _vm.SetWeaponAttachment(
                            wt, hit, args[att + 2].Equals("own", StringComparison.OrdinalIgnoreCase));
                }

                // --wcard <key>: fold every card but one, as --card does for a part.
                var wc = Array.IndexOf(args, "--wcard");
                if (wc >= 0 && wc + 1 < args.Length)
                {
                    var key = args[wc + 1];
                    foreach (var s in wt.EditSections)
                        s.Open = s.Key.Equals(key, StringComparison.OrdinalIgnoreCase);
                }

                var wr2 = Array.IndexOf(args, "--revertpiece");
                if (wr2 >= 0 && wr2 + 1 < args.Length)
                {
                    var onSlot = args[wr2 + 1];
                    var hit = wt.MeshRows.FirstOrDefault(
                        r => r.Variant.Contains(onSlot, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) _vm.Say($"--revertpiece: no slot matching '{onSlot}'");
                    else _vm.RevertWeaponPiece(wt, hit);
                }

                if (rn2 >= 0 || si2 >= 0) _vm.SaveWeaponEdit(wt);
            }

            // THE PANEL'S OWN FLAGS, WHICHEVER WAY IT WAS OPENED.
            //
            // These lived inside `--do edit`, so a copy opened with --editcopy could be
            // photographed and not driven — and every write path only a COPY takes (its
            // textures, its picture, its mesh) is reachable from no other flag. Hoisted into
            // one function rather than copied beside the other one: a harness with two copies
            // of itself is a harness that can disagree about what it proved.
            async Task DrivePanel(PartTab pt)
            {
                var rn = Array.IndexOf(args, "--rename");
                if (rn >= 0 && rn + 1 < args.Length) pt.EditName = args[rn + 1];

                // --viewmeshrow: press View on the first mesh row carrying the mod's own import, the way the author
                // checks an import arrived as meant. What the viewport makes of it is in the log.
                if (args.Contains("--viewmeshrow"))
                {
                    for (var spin = 0; spin < 40 && pt.MeshRows.Count == 0; spin++) await Task.Delay(250);
                    if (pt.MeshRows.FirstOrDefault(r => r.Replacement.Length > 0) is { } viewRow)
                        await _vm.ViewMeshRowAsync(viewRow);
                    else _vm.Say($"--viewmeshrow: none of {pt.MeshRows.Count} mesh row(s) carries an import");
                }

                // --viewtexrow: the same for the first texture row carrying an import.
                if (args.Contains("--viewtexrow"))
                {
                    for (var spin = 0; spin < 40 && pt.TextureRows.Count == 0; spin++) await Task.Delay(250);
                    if (pt.TextureRows.FirstOrDefault(r => r.Replacement.Length > 0) is { } texRow)
                        await _vm.ViewTextureRowAsync(texRow);
                    else _vm.Say($"--viewtexrow: none of {pt.TextureRows.Count} texture row(s) carries an import");
                }

                // --mesh / --icon go through ImportArt, the same call Browse makes,
                // so the COPY is exercised and not just the field assignment.
                Import("--mesh", "Meshes", n => pt.EditMesh = n);
                Import("--icon", "Icons", n => pt.EditIcon = n);

                // --swatchback <name>: hand a hidden colour swatch back to the
                // menu, which is the one edit here that cannot be checked by looking
                // at the panel — it has to reach the project as a hidesFromMenu
                // entry or it did nothing.
                var sb = Array.IndexOf(args, "--swatchback");
                if (sb >= 0 && sb + 1 < args.Length)
                {
                    var hit = pt.Swatches.FirstOrDefault(
                        s => s.Name.Equals(args[sb + 1], StringComparison.OrdinalIgnoreCase));
                    // EVERY SWATCH, AS IT STANDS. The save writes only where Offered
                    // differs from what the GAME does, so the two flags side by side
                    // are the whole answer to "why did that write nothing".
                    foreach (var s in pt.Swatches)
                        _vm.Say($"--swatchback: {s.Name} [{s.Tag}] " +
                                $"offered={s.Offered} game={s.GameOffered} " +
                                $"live={s.Live} known={s.Known}");

                    if (hit is null)
                        _vm.Say($"--swatchback: no swatch named '{args[sb + 1]}' — " +
                                string.Join(", ", pt.Swatches.Select(s => s.Name)));
                    else
                    {
                        hit.Offered = true;
                        _vm.Say($"--swatchback: set {hit.Name} offered=true " +
                                $"(game={hit.GameOffered}) — differs={hit.Offered != hit.GameOffered}");
                        _vm.SavePartEdit(pt);
                    }
                }

                // --untag <fragment> / --setid <id>: take a gate tag off, and name
                // what a copy cooks to. The tag one is the mechanism that turns a
                // hero's gear into everybody's, so it is worth its own witness.
                var utg = Array.IndexOf(args, "--untag");
                if (utg >= 0 && utg + 1 < args.Length)
                {
                    var want = args[utg + 1];
                    var hit = pt.Tags.FirstOrDefault(
                        t => t.Tag.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--untag: no tag matching '{want}' — " +
                                string.Join(", ", pt.Tags.Select(t => t.Shown)));
                    else { hit.Kept = false; _vm.SavePartEdit(pt); }
                }

                var sid = Array.IndexOf(args, "--setid");
                if (sid >= 0 && sid + 1 < args.Length)
                {
                    pt.EditId = args[sid + 1];
                    _vm.SavePartEdit(pt);
                }

                // --setslot <slot> / --untake <slot fragment>: the placement card,
                // from outside. Only a copy has one.
                var sl = Array.IndexOf(args, "--setslot");
                if (sl >= 0 && sl + 1 < args.Length)
                {
                    pt.EditSlot = args[sl + 1];
                    _vm.SavePartEdit(pt);
                }

                var ut = Array.IndexOf(args, "--untake");
                if (ut >= 0 && ut + 1 < args.Length)
                {
                    var want = args[ut + 1];
                    var hit = pt.Takeovers.FirstOrDefault(
                        t => t.Tag.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--untake: no claim matching '{want}' — " +
                                string.Join(", ", pt.Takeovers.Select(t => t.Slot)));
                    else { hit.Kept = false; _vm.SavePartEdit(pt); }
                }

                // --renamename <from> <to>: the deeper card's blunt instrument.
                var rnm = Array.IndexOf(args, "--renamename");
                if (rnm >= 0 && rnm + 2 < args.Length)
                    _vm.AddRename(pt, args[rnm + 1], args[rnm + 2]);

                // --setnum <Export>.<Property>=<value>: change one of the part's own
                // numbers from the editor's grid.
                //
                // NOT the same as --set, which runs before any part is open and aims
                // at a stand-alone asset tab. This one goes through the grid EMBEDDED
                // in the editor, which is the path that was read-only by accident
                // until AssetTabOpen learned about embedded grids.
                var sn = Array.IndexOf(args, "--setnum");
                if (sn >= 0 && sn + 1 < args.Length && pt.Props is { } grid)
                {
                    for (var spin = 0; spin < 60 && grid.Rows.Count == 0; spin++)
                        await Task.Delay(100);

                    var spec = args[sn + 1];
                    var eq = spec.LastIndexOf('=');
                    var dot = eq > 0 ? spec.LastIndexOf('.', eq - 1) : -1;
                    if (eq < 0 || dot < 0)
                        _vm.Say($"--setnum {spec}: expected <Export>.<Property>=<value>");
                    else
                    {
                        var export = spec[..dot];
                        var prop = spec[(dot + 1)..eq];
                        var hit = grid.Rows.FirstOrDefault(
                            r => r.Export == export && r.Name == prop);
                        if (hit is null)
                            _vm.Say($"--setnum: no {prop} on {export} — " +
                                    string.Join(", ", grid.Rows.Where(r => r.Editable)
                                                          .Take(8)
                                                          .Select(r => $"{r.Export}.{r.Name}")));
                        else _vm.ApplyEdit(grid, hit, spec[(eq + 1)..]);
                    }
                }

                // --addtag <tag> / --unlockslot: the other two sections the old save
                // gate dropped on the floor. Both write nothing but a tag list and a
                // flag, which is exactly why they were invisible to a gate that
                // watched the name and the mesh.
                var tg = Array.IndexOf(args, "--addtag");
                if (tg >= 0 && tg + 1 < args.Length)
                {
                    pt.EditAddTags = args[tg + 1];
                    _vm.SavePartEdit(pt);
                }

                if (args.Contains("--unlockslot"))
                {
                    pt.EditLocksOwnSlot = false;
                    _vm.SavePartEdit(pt);
                }

                // --type <text>: put something in the name box and DO NOT save, so
                // the bar along the bottom can be seen counting it. The counter is
                // the one part of this panel that a picture of an untouched editor
                // cannot show, and "2 changes not saved" being wrong is exactly the
                // kind of fault only the author would ever notice.
                var ty = Array.IndexOf(args, "--type");
                if (ty >= 0 && ty + 1 < args.Length) pt.EditName = args[ty + 1];

                // --teximport <slot or texture name> <file>: put an image over one of
                // the part's textures, through the row's own call.
                //
                // THE ONLY WAY TO WITNESS IT. A screenshot of the textures card shows
                // four rows whether or not Import does anything, and the rehearsal it
                // runs — the image against the real target's size, format and mip
                // layout — is the half that can fail. The rows arrive on a background
                // read, so this waits for them rather than racing them.
                var tex = Array.IndexOf(args, "--teximport");
                if (tex >= 0 && tex + 2 < args.Length)
                {
                    var want = args[tex + 1];
                    for (var spin = 0; spin < 60 && pt.TextureRows.Count == 0; spin++)
                        await Task.Delay(100);

                    var hit = pt.TextureRows.FirstOrDefault(
                        r => r.Slot.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                             r.GameLeaf.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--teximport: no texture matching '{want}' — " +
                                string.Join(", ", pt.TextureRows.Select(r => r.Slot)));
                    else _vm.ImportTextureRow(hit, args[tex + 2]);
                }

                // --pickmesh <row> <name>: point one of the part's meshes at another
                // of the game's, by name. The picker is a dialog and a dialog is a
                // mouse away, so this is the only way to witness that the pair of
                // re-points is computed, rehearsed and written.
                var pm = Array.IndexOf(args, "--pickmesh");
                if (pm >= 0 && pm + 2 < args.Length)
                try
                {
                    var onRow = args[pm + 1];
                    var wantMesh = args[pm + 2];
                    _vm.Say($"--pickmesh: looking for row '{onRow}' and mesh '{wantMesh}'");

                    var hit = pt.MeshRows.FirstOrDefault(
                        r => r.Variant.Contains(onRow, StringComparison.OrdinalIgnoreCase) ||
                             r.GameLeaf.Contains(onRow, StringComparison.OrdinalIgnoreCase));
                    var target = _vm.GameAssets(AssetKind.SkeletalMesh).FirstOrDefault(
                        c => c.Name.Contains(wantMesh, StringComparison.OrdinalIgnoreCase));

                    if (hit is null) _vm.Say($"--pickmesh: no mesh row matching '{onRow}'");
                    else if (target is null) _vm.Say($"--pickmesh: no game mesh matching '{wantMesh}'");
                    else _vm.PickMeshRow(pt, hit, target.Package);
                }
                catch (Exception ex) { _vm.Say($"--pickmesh threw: {ex}"); }

                // --showpicker <row>: open the picker on a row and leave it open, so
                // the window itself can be photographed. It is a modal dialog, which
                // is its own top-level window — no screenshot of the Studio contains
                // one, and its whole job is to be looked at.
                var sp = Array.IndexOf(args, "--showpicker");
                if (sp >= 0 && sp + 1 < args.Length)
                {
                    var onRow = args[sp + 1];
                    for (var spin = 0; spin < 60 && pt.TextureRows.Count == 0; spin++)
                        await Task.Delay(100);

                    var rows = pt.TextureRows.Concat(
                        pt.IconRow is null ? [] : new[] { pt.IconRow });
                    if (rows.FirstOrDefault(
                            r => r.Slot.Contains(onRow, StringComparison.OrdinalIgnoreCase))
                        is { } showOn)
                    {
                        var fake = new Button { DataContext = showOn };
                        OnPickTextureRow(fake, new RoutedEventArgs());
                    }
                    else _vm.Say($"--showpicker: no row matching '{onRow}'");
                }

                // --picktex <row> <name>: point one of the part's textures, or its
                // menu picture, at another of the game's. Searches both lists because
                // the picture is a texture row like any other — which is the whole
                // point of it being one.
                var px = Array.IndexOf(args, "--picktex");
                if (px >= 0 && px + 2 < args.Length)
                {
                    var onRow = args[px + 1];
                    var wantTex = args[px + 2];
                    for (var spin = 0; spin < 60 && pt.TextureRows.Count == 0; spin++)
                        await Task.Delay(100);

                    var rows = pt.TextureRows.Concat(
                        pt.IconRow is null ? [] : new[] { pt.IconRow });
                    var hit = rows.FirstOrDefault(
                        r => r.Slot.Contains(onRow, StringComparison.OrdinalIgnoreCase) ||
                             r.GameLeaf.Contains(onRow, StringComparison.OrdinalIgnoreCase));
                    var target = _vm.GameAssets(AssetKind.Texture).FirstOrDefault(
                        c => c.Name.Contains(wantTex, StringComparison.OrdinalIgnoreCase));

                    if (hit is null) _vm.Say($"--picktex: no row matching '{onRow}'");
                    else if (target is null) _vm.Say($"--picktex: no game texture matching '{wantTex}'");
                    // THE SAME FORK THE PICKER TAKES. A copy's picture is a field on the part,
                    // and sending it through PickTextureRow wrote a texture entry keyed "Menu
                    // picture" that painted nothing.
                    else if (ReferenceEquals(hit, pt.IconRow) && pt.Copy is not null)
                        _vm.PickPartIcon(pt, target.Package);
                    else _vm.PickTextureRow(hit, target.Package);
                }

                // --reverttex <row>: put one texture row, or the picture, back —
                // whichever kind of change it carries. Its own flag for the reason
                // --revertmesh has one: Revert reaches two different packages and
                // the failure is that it reports success having done nothing.
                var rt = Array.IndexOf(args, "--reverttex");
                if (rt >= 0 && rt + 1 < args.Length)
                {
                    var onRow = args[rt + 1];
                    for (var spin = 0; spin < 60 && pt.TextureRows.Count == 0; spin++)
                        await Task.Delay(100);

                    var rows = pt.TextureRows.Concat(
                        pt.IconRow is null ? [] : new[] { pt.IconRow });
                    var hit = rows.FirstOrDefault(
                        r => r.Slot.Contains(onRow, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) _vm.Say($"--reverttex: no row matching '{onRow}'");
                    else if (ReferenceEquals(hit, pt.IconRow)) _vm.RevertPartIcon(pt);
                    else _vm.RevertTextureRow(hit);
                }

                // --revertmesh <row>: put one mesh row back, whichever kind of change
                // it carries. Its own flag because Revert has to reach two different
                // places — an import on the mesh package, a pick on the part's — and
                // a Revert that reports success while changing nothing is the exact
                // failure this catches.
                var rm = Array.IndexOf(args, "--revertmesh");
                if (rm >= 0 && rm + 1 < args.Length)
                {
                    var onRow = args[rm + 1];
                    var hit = pt.MeshRows.FirstOrDefault(
                        r => r.Variant.Contains(onRow, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) _vm.Say($"--revertmesh: no row matching '{onRow}'");
                    else _vm.RevertMeshRow(pt, hit);
                }

                // --texuse <row> <leaf>: apply an image this mod ALREADY HOLDS to one
                // of the part's textures — the "Use…" menu, from outside. Worth its
                // own flag because the interesting half is the rehearsal: the same
                // file is not right for every target it could be offered for.
                var tu = Array.IndexOf(args, "--texuse");
                if (tu >= 0 && tu + 2 < args.Length)
                {
                    var want = args[tu + 1];
                    for (var spin = 0; spin < 60 && pt.TextureRows.Count == 0; spin++)
                        await Task.Delay(100);

                    var hit = pt.TextureRows.FirstOrDefault(
                        r => r.Slot.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                             r.GameLeaf.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--texuse: no texture matching '{want}'");
                    else _vm.UseTextureRow(hit, args[tu + 2]);
                }

                // --usemenu: open the "Use…" list on the first texture row that has
                // the button, so the menu itself can be photographed. A flyout is its
                // own window and only exists while it is up — there is no other way
                // to see what it offers or how it marks a size that does not fit.
                var um = Array.IndexOf(args, "--usemenu");
                if (um >= 0)
                {
                    // An optional row to aim at, because the picture card's row comes
                    // first in the tree and a texture row is the interesting case.
                    var onRow = um + 1 < args.Length && !args[um + 1].StartsWith("--")
                                ? args[um + 1] : "";
                    for (var spin = 0; spin < 80; spin++)
                    {
                        await Task.Delay(100);
                        var button = this.GetVisualDescendants().OfType<Button>()
                            .FirstOrDefault(b => b.Content as string == "Use…" &&
                                                 b.IsVisible &&
                                                 b.DataContext is SheetTab.TextureRow r &&
                                                 (onRow.Length == 0 ||
                                                  r.Slot.Contains(onRow, StringComparison.OrdinalIgnoreCase)));
                        if (button is null) continue;
                        OnUseTextureRow(button, new RoutedEventArgs());

                        // AND SAY WHAT IT OFFERED. A flyout is its own window, so no
                        // screenshot of this application contains one — the lines it
                        // built are the only thing that can be checked.
                        if (button.DataContext is SheetTab.TextureRow on)
                            foreach (var (header, _) in ImageMenuLabels(on))
                                _vm.Say($"Use… on {on.Slot}: {header}");
                        break;
                    }
                }

                // --iconimport <file>: put a picture over the part's menu icon,
                // through the card's own call. Same reasoning as --teximport: the
                // rehearsal against the real icon texture is the half that can fail,
                // and a screenshot of the row cannot show whether it ran.
                var ic = Array.IndexOf(args, "--iconimport");
                if (ic >= 0 && ic + 1 < args.Length)
                {
                    for (var spin = 0; spin < 60 && pt.IconRow is null &&
                                       pt.IconNote.Length == 0; spin++)
                        await Task.Delay(100);
                    _vm.ImportPartIcon(pt, args[ic + 1]);
                }

                // --iconrevert: put the picture back. Its own flag because Revert
                // clears BOTH halves — the pixels on the icon texture and the record
                // on the part — and half a revert is the failure worth catching.
                if (args.Contains("--iconrevert"))
                {
                    for (var spin = 0; spin < 60 && pt.IconRow is null; spin++)
                        await Task.Delay(100);
                    _vm.RevertPartIcon(pt);
                }

                // --wearmat <slot> <name> / --clearmat <slot> / --slotname <slot> <name> /
                // --addmat: the materials card, without a mouse.
                //
                // Naming a game material is the half of Materials this panel never had, and it
                // is the half a screenshot cannot witness: the result is one line in the project
                // and one package import in the build, neither of which is on screen.
                for (var spin = 0; spin < 80 && pt.MaterialGroups.Count == 0; spin++)
                    await Task.Delay(250);

                // --tintmask <package> / --tintcolour <hex> / --dupecolour: the tint pair,
                // and the press that makes a colourway of it.
                var tm = Array.IndexOf(args, "--tintmask");
                if (tm >= 0 && tm + 1 < args.Length)
                    _vm.SetTintMask(pt, args[tm + 1] == "none" ? "" : args[tm + 1]);

                var tcl = Array.IndexOf(args, "--tintcolour");
                if (tcl >= 0 && tcl + 1 < args.Length)
                    _vm.SetTintColour(pt, args[tcl + 1] == "none" ? "" : args[tcl + 1]);

                if (args.Contains("--dupecolour")) _vm.DuplicateAsColour(pt);

                // --partpiece <n> <file|revert>: an FBX onto ONE piece of a garment, or that
                // piece put back.
                //
                // NOT --piece, which a WEAPON's driver already uses for a slot name. And HERE
                // rather than beside --dumpedit, because DriveFit runs below and the fit fields
                // write to whatever piece carries an import — run the imports after it and the
                // scale lands on the part instead, which is exactly what happened.
                //
                // LEFT TO RIGHT WITH REPEATS, the rule --play follows: the interesting case is a
                // sequence, and IndexOf finds only the first.
                for (var pc = 0; pc < args.Length - 2; pc++)
                {
                    if (!args[pc].Equals("--partpiece", StringComparison.Ordinal)) continue;
                    if (!int.TryParse(args[pc + 1], out var pcN) ||
                        pcN < 0 || pcN >= pt.MeshRows.Count)
                    { _vm.Say($"--partpiece: no row {args[pc + 1]} among {pt.MeshRows.Count}"); continue; }

                    var pcRow = pt.MeshRows[pcN];
                    if (args[pc + 2].Equals("revert", StringComparison.OrdinalIgnoreCase))
                        _vm.RevertMeshRow(pt, pcRow);
                    else _vm.ImportMeshRow(pcRow, args[pc + 2]);
                }

                // AFTER THE MEASUREMENT, because the buttons that place a mesh by it need the
                // report — and a check that pressed them early would report "not measured yet"
                // about a panel that measures in under a second.
                for (var spin = 0; spin < 40 && pt.SecFit.Present && !pt.CanFit &&
                                   pt.FitCounts.StartsWith("measuring", StringComparison.Ordinal);
                     spin++)
                    await Task.Delay(250);
                DriveFit(pt);

                SheetTab.MaterialGroup? Slot(int at) =>
                    pt.MaterialGroups.FirstOrDefault(g => g.Slot == at) ??
                    (at < pt.MaterialGroups.Count ? pt.MaterialGroups[at] : null);

                var wm = Array.IndexOf(args, "--wearmat");
                if (wm >= 0 && wm + 2 < args.Length && int.TryParse(args[wm + 1], out var wmAt))
                {
                    var want = args[wm + 2];
                    var target = _vm.GameAssets(AssetKind.Material).FirstOrDefault(
                        c => c.Name.Contains(want, StringComparison.OrdinalIgnoreCase));
                    if (Slot(wmAt) is not { } g) _vm.Say($"--wearmat: no material slot {wmAt}");
                    else if (target is null) _vm.Say($"--wearmat: no game material matching '{want}'");
                    else _vm.WearMaterial(pt, g, target.Package);
                }

                var cm = Array.IndexOf(args, "--clearmat");
                if (cm >= 0 && cm + 1 < args.Length && int.TryParse(args[cm + 1], out var cmAt))
                {
                    if (Slot(cmAt) is { } g) _vm.ClearMaterial(pt, g);
                    else _vm.Say($"--clearmat: no material slot {cmAt}");
                }

                var tsn = Array.IndexOf(args, "--slotname");
                if (tsn >= 0 && tsn + 2 < args.Length && int.TryParse(args[tsn + 1], out var snAt))
                {
                    if (Slot(snAt) is { } g) _vm.SetMaterialSlotName(pt, g, args[tsn + 2]);
                    else _vm.Say($"--slotname: no material slot {snAt}");
                }

                if (args.Contains("--addmat")) _vm.AddMaterialSlot(pt, pt.LastPiece);

                // --card <key>: fold every card but this one, so a section far down
                // the column can be photographed without a mouse.
                var cd = Array.IndexOf(args, "--card");
                if (cd >= 0 && cd + 1 < args.Length)
                {
                    var key = args[cd + 1];
                    var hit = pt.EditSections.FirstOrDefault(
                        s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (hit is null)
                        _vm.Say($"--card {key}: no section by that name — " +
                                string.Join(", ", pt.EditSections.Select(s => s.Key)));
                    else
                    {
                        foreach (var s in pt.EditSections) s.Open = ReferenceEquals(s, hit);
                        pt.SelectSection(hit);
                    }
                }

                // ONLY SAVE WHEN SOMETHING WAS TYPED. `--do edit` on its own used to
                // save and close immediately, so the one thing it could not do was
                // show the edit panel — which is the thing a screenshot of it needs.
                if (rn >= 0 || Array.IndexOf(args, "--mesh") >= 0 ||
                    Array.IndexOf(args, "--icon") >= 0)
                    _vm.SavePartEdit(pt);

                void Import(string flag, string kind, Action<string> onto)
                {
                    var at2 = Array.IndexOf(args, flag);
                    if (at2 < 0 || at2 + 1 >= args.Length) return;
                    if (_vm.ImportArt(args[at2 + 1], kind, out var why2) is { } n) onto(n);
                    else _vm.Say($"{flag}: {why2}");
                }
            }
#endif
        };
    }

    /// <summary>One line into the log, from outside.</summary>
    public void Say(string line) => _vm.Say(line);

    private void Retitle()
        => Title = _vm.HasMod
            ? $"Zero Company Mod Studio — {_vm.ModTitle}"
            : "Zero Company Mod Studio";

    /// <summary>
    /// The width the column had before it was collapsed, so re-opening restores whatever the
    /// splitter had been dragged to rather than snapping back to the default.
    /// </summary>
    private GridLength _savedLeft = new(330);

    /// <summary>
    /// The narrowest the side column may be dragged. Below this the collapse button inside it is
    /// clipped away and the column can no longer be operated at all, so it is not a width anybody
    /// can have meant.
    /// </summary>
    private const double Floor = 180;

    private void OnTogglePanel(object? sender, RoutedEventArgs e) => SetPanel(!_vm.PanelOpen);

    private void SetPanel(bool open)
    {
        if (open)
        {
            Body.ColumnDefinitions[0].MinWidth = Floor;
            Body.ColumnDefinitions[0].Width = _savedLeft;
            Body.ColumnDefinitions[1].Width = new GridLength(5);
        }
        else
        {
            // Read it back from the COLUMN, not from a field this class set: the splitter drags
            // the ColumnDefinition directly, so anything remembered at toggle time is the only
            // value that reflects where the author actually left it.
            //
            // AND CLAMP IT, because "whatever it was" included nothing. A column dragged down to a
            // sliver and then collapsed reopened as a sliver: PanelOpen went true, so the button
            // offered to close something already invisible, and the button itself lives INSIDE
            // that column, clipped to nothing. Neither the toggle nor the splitter could get it
            // back. That is "you collapse it and then reopen and you can't collapse it again".
            var was = Body.ColumnDefinitions[0].Width;
            _savedLeft = was.IsAbsolute && was.Value >= Floor ? was : new GridLength(330);
            // The floor has to come off first or the column cannot reach zero.
            Body.ColumnDefinitions[0].MinWidth = 0;
            Body.ColumnDefinitions[0].Width = new GridLength(0);
            Body.ColumnDefinitions[1].Width = new GridLength(0);
        }
        _vm.PanelOpen = open;
    }

    /// <summary>
    /// The Name column header. It carries a sort arrow, so it sorts — an arrow drawn on a header
    /// that does nothing is the UI telling somebody a lie about itself.
    /// </summary>
    private void OnSortByName(object? sender, RoutedEventArgs e) => _vm.ToggleSort();

    // ------------------------------------------------------------------ Tabs

    /// <summary>
    /// Open whatever is selected in the list, in a tab.
    ///
    /// Shared by the double click and the Open menu item, so the two cannot drift apart — which
    /// is the whole reason the menu exists as a second route rather than a second implementation.
    /// </summary>
    private async void OnOpenAsset(object? sender, RoutedEventArgs e)
    {
        if (_vm.Selected is { } row) await _vm.OpenAssetAsync(row);
    }

    /// <summary>
    /// Home Page, or one of the nine sections.
    ///
    /// The Home Page tab is not a special case here: it is the section whose list is the Data
    /// Explorer, so switching to it goes through the same call as every other tab and cannot fall
    /// out of step with them.
    /// </summary>
    private async void OnPickSection(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SectionTab tab) await _vm.ShowSectionAsync(tab.Kind);
    }

    /// <summary>Open the picked section row in the strip, beside the assets.</summary>
    private void OnOpenSectionItem(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen is { } item) _vm.OpenSectionItem(item);
    }

    /// <summary>Back to the Data Explorer, on the exact file this row names.</summary>
    private async void OnFindInData(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen is { } item) await _vm.FindInDataAsync(item);
    }

    private void OnEditSectionItem(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen is { } item) _vm.EditPart(item);
    }

    private void OnRevertSectionItem(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen is { } item) _vm.RevertPart(item);
    }

    private void OnDuplicateSectionItem(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen is { } item) _vm.DuplicatePart(item);
    }

    /// <summary>
    /// "Add somebody to this pool…": the picker over every character the game has, into the pool
    /// of the recruit that was right-clicked.
    ///
    /// THE ROW IS THE POOL, not the person. What a new recruit takes from it is the table it
    /// lands in and the pawn it spawns on, and every complete row in a pool agrees about both --
    /// so any recruit in the databank is as good a donor as any other, and the one under the
    /// cursor is the one the person is already looking at.
    ///
    /// The name is NOT asked for. They arrive under the name the game gives them, which is the
    /// whole difference between this and "a character of your own"; renaming is a field on their
    /// own page afterwards.
    /// </summary>
    private async void OnAddToPool(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen?.Payload is not CharacterIndex.Recruit pool)
        { _vm.Say("Add somebody to this pool is for a recruit — pick one in Recruits first"); return; }

        var characters = await _vm.CharacterTemplatesAsync();
        if (characters.Count == 0)
        { _vm.Say("the game's characters could not be read, so there is nobody to add"); return; }

        var picker = new PickAssetWindow(
            $"Add somebody to {(pool.PoolLeaf.Length > 0 ? pool.PoolLeaf : "the pool")}",
            $"Any of the game's {characters.Count} characters, and nobody is displaced — this " +
            "ADDS a row rather than taking one. They keep the name the game gives them, and land " +
            $"on the pawn {pool.Describe} spawns. Rename them on their own page afterwards.",
            characters.Select(c => new PickAssetWindow.Candidate(c.Name, c.Where, c.Package)),
            images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } definition) await _vm.AddToPool(definition, pool);
    }

    /// <summary>
    /// "Take their place…": the same picker, but the chosen character takes over the row that was
    /// right-clicked instead of getting one of its own.
    ///
    /// THE DIALOG SAYS WHO IS DISPLACED, by name, before anything happens. That is the whole
    /// difference from "add somebody" and the only part that cannot be guessed from the menu
    /// item -- a roster of the same length, with one of the game's recruits no longer in it.
    /// </summary>
    private async void OnTakeTheirPlace(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen?.Payload is not CharacterIndex.Recruit row)
        { _vm.Say("Take their place is for a recruit — pick one in Recruits first"); return; }

        var characters = await _vm.CharacterTemplatesAsync();
        if (characters.Count == 0)
        { _vm.Say("the game's characters could not be read, so there is nobody to put there"); return; }

        var whose = row.Name.Length > 0 ? row.Name : row.Row;
        var picker = new PickAssetWindow(
            $"Take {whose}'s place",
            $"Any of the game's {characters.Count} characters. Whoever you pick is copied into " +
            $"this mod and the '{row.Row}' row is pointed at them, so {whose} is NO LONGER " +
            "OFFERED while this mod is installed — the roster stays the same length. Use “Add " +
            "somebody to this pool” instead if you want them as well as everyone else. The row " +
            "keeps its name and rides a table this mod ships, so other recruit mods still work.",
            characters.Select(c => new PickAssetWindow.Candidate(c.Name, c.Where, c.Package)),
            images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } definition) await _vm.TakeTheirPlace(definition, row);
    }

    /// <summary>
    /// "Put this row back": undo a take-their-place on the row under the cursor.
    ///
    /// No picker and no dialog -- there is exactly one thing it can mean. It says so plainly when
    /// the row is not one this mod takes over, rather than being hidden, because a menu item that
    /// appears and disappears is harder to find than one that explains itself.
    /// </summary>
    private void OnPutRowBack(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen?.Payload is not CharacterIndex.Recruit row)
        { _vm.Say("Put this row back is for a recruit — pick one in Recruits first"); return; }
        _vm.PutRowBack(row);
    }

    /// <summary>
    /// "+": hang another of the game's meshes on this one.
    ///
    /// THE PICKER IS NOT FILTERED DOWN TO A GUESS. Meshes built for this rig come first, every
    /// other skeletal mesh in the game follows, and the second column says which is which. A
    /// list that silently withheld the rest would be my naming rule deciding what is allowed,
    /// and the bone count after the fact is the only thing here entitled to an opinion.
    /// </summary>
    private async void OnAttachMesh(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not MeshTab mesh) return;

        var all = _vm.AttachCandidates(mesh);
        if (all.Count == 0) { _vm.Say("no other skeletal mesh in the game to attach"); return; }

        var fits = all.Count(c => c.SameRig);
        var picker = new PickAssetWindow(
            $"Attach to {mesh.LoadedName}",
            fits > 0
                ? $"{fits} mesh(es) are built for this rig and are listed first; the other " +
                  $"{all.Count - fits} follow. How well one really fitted is measured from its " +
                  "bones once it is on the model."
                : $"All {all.Count} skeletal meshes in the game — this one's name carries no rig " +
                  "to sort them by.",
            all.Select(c => new PickAssetWindow.Candidate(
                c.Name, c.SameRig ? "this rig" : "another rig", c.Path)),
            images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } path)
            await _vm.AttachMeshAsync(mesh, path,
                                      System.IO.Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Take one back off.</summary>
    private void OnDetachMesh(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is MeshTab mesh && sender is Control { Tag: string key })
            _vm.DetachMesh(mesh, key);
    }

    /// <summary>Fold every animation group away, or open them all.</summary>
    private void OnAllAnimGroups(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is MeshTab mesh) mesh.AllAnimGroups(!mesh.AnimsClosed);
    }

    /// <summary>
    /// Fold one animation set, or one action group inside it.
    ///
    /// ON THE HEADING OWN BUTTON, not on the list selection. The fold used to ride the TwoWay
    /// SelectedItem binding, which cost nothing to write and two real faults to own: a ListBox
    /// raises that only when the selection CHANGES, so folding a row, reopening it and folding it
    /// again sent exactly one event; and a realised container pushes its selection back, which
    /// the setter could not tell from a click, so it needed a latch that then dropped clicks of
    /// its own. A Button raises Click on every release. See <see cref="MeshTab.PickedAnim"/>.
    /// </summary>
    private void OnToggleAnimFold(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not MeshTab mesh) return;
        switch ((sender as Control)?.Tag)
        {
            case AnimHeading h: mesh.ToggleAnimCategory(h.Text); break;
            case AnimGroup g:   mesh.ToggleAnimGroup(g.Key);     break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// The keyboard half of the same fold, because a Button only answers a mouse.
    ///
    /// The selection binding did carry both, and that convenience is exactly what bought the bug.
    /// This costs four lines and cannot be swallowed by a selection that did not change.
    /// </summary>
    private void OnAnimKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (_vm.Tab is not MeshTab mesh) return;
        switch ((sender as ListBox)?.SelectedItem)
        {
            case AnimHeading h: mesh.ToggleAnimCategory(h.Text); break;
            case AnimGroup g:   mesh.ToggleAnimGroup(g.Key);     break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Pick an FBX, and IMPORT it — the file is copied into the mod's own source folder and the
    /// project stores its name. A path into somebody's Desktop builds here and nowhere else.
    /// </summary>
    private async void OnPickPartMesh(object? sender, RoutedEventArgs e)
        => await PickArt("Meshes", "Choose a mesh", "FBX mesh", new[] { "*.fbx" },
                         (tab, name) => tab.EditMesh = name);

    /// <summary>
    /// Put a picture over the part's menu icon.
    ///
    /// Through the VIEW MODEL, not through PickArt: the picture is written the moment it is
    /// chosen now, like a mesh and like a texture, and that means rehearsing the image against
    /// the real icon texture first. PickArt only copies a file and fills a field in.
    /// </summary>
    private async void OnPickPartIcon(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not PartTab tab) return;
        if (tab.IconRow is null) { _vm.Say($"{tab.Title}: {tab.IconNote}"); return; }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Menu picture for {tab.Title}  ({tab.IconRow.Describe})",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.tga"] }],
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is { } path) _vm.ImportPartIcon(tab, path);
    }

    /// <summary>Put the menu picture back to the game's own — the pixels and the record of it.</summary>
    private void OnRevertPartIcon(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is PartTab tab) _vm.RevertPartIcon(tab);
    }




    // ------------------------------------------------------------------ a character of their own

    /// <summary>
    /// "A character of your own": the dialog, and nothing else. What it hands back goes straight
    /// to MakeCharacter, which is the same method the driver flag calls.
    /// </summary>
    private async void OnNewCharacter(object? sender, RoutedEventArgs e)
    {
        var characters = await _vm.CharacterTemplatesAsync();
        if (characters.Count == 0)
        { _vm.Say("the game's characters could not be read, so there is nobody to start from"); return; }

        var pools = await _vm.PoolChoicesAsync();
        if (pools.Count == 0)
        { _vm.Say("the hiring pool could not be read, so there is nowhere to put them"); return; }

        var dialog = new NewCharacterWindow(
            characters,
            pools.Select(p => (p.Label, p.Template)).ToList(),
            async t => await _vm.RigAndBespokeAsync(t));

        await dialog.ShowDialog(this);
        if (dialog.Chosen is { } made)
            await _vm.MakeCharacter(made.Template, made.First, made.Last, made.Story, made.Pool);
    }

    private void OnSaveCharacterName(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CharacterTab tab) _vm.SaveCharacterName(tab);
    }

    /// <summary>
    /// The umbrellas, shown or hidden.
    ///
    /// The table is REBUILT rather than filtered in place: a slot's row has three states and a
    /// Reset button that outlives the value it described is worse than a slower redraw.
    /// </summary>
    private void OnShowEverySlot(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CharacterTab tab) _vm.RefillLook(tab);
    }

    private static SheetTab.LookRow? LookOf(object? sender)
        => (sender as Control)?.DataContext as SheetTab.LookRow;

    private void OnResetSlot(object? sender, RoutedEventArgs e)
    {
        if (LookOf(sender) is { } row && _vm.Tab is CharacterTab tab) _vm.ResetSlot(tab, row);
    }

    private void OnEmptySlot(object? sender, RoutedEventArgs e)
    {
        if (LookOf(sender) is { } row && _vm.Tab is CharacterTab tab) _vm.EmptySlot(tab, row);
    }

    /// <summary>
    /// What may go in one slot.
    ///
    /// THE OTHER SPECIES ARE A SCOPE, NOT A SECTION BREAK. The old studio drew a rule across the
    /// list with "other species" over it, which the picker here cannot do because it sorts by
    /// what it shows. A box that narrows the list says the same thing and can be turned off,
    /// which is what somebody dressing a Twi'lek in a clone helmet actually wants.
    /// </summary>
    private async void OnChooseSlot(object? sender, RoutedEventArgs e)
    {
        if (LookOf(sender) is not { } row || _vm.Tab is not CharacterTab tab) return;

        var (all, same) = _vm.SlotCandidates(row);
        if (all.Count == 0)
        { _vm.Say($"nothing in the game says it fits {row.Shown}"); return; }

        var picker = new PickAssetWindow(
            "Choose: " + row.Shown,
            "Parts of the template's own species are listed first. A face from another species " +
            "may not fit the body, and a slot that nothing claims to fit is offered the parts " +
            "named like the one in it now.",
            all, images: false,
            scopeLabel: "this species only",
            scope: c => same.Contains(c.Package));

        await picker.ShowDialog(this);
        if (picker.Chosen is { } package) _vm.SetSlot(tab, row, package);
    }

#if DEV
    /// <summary>
    /// --editchar and its followers, so a character can be authored without a mouse.
    ///
    /// EVERY VALUE IS ONE TOKEN, because PowerShell's Start-Process splits an argument list on
    /// spaces — so a story arrives with underscores where its spaces go.
    /// </summary>
    private async Task DriveCharacter(CharacterTab tab)
    {
        var args = Environment.GetCommandLineArgs();

        var cf = Array.IndexOf(args, "--charfirst");
        if (cf >= 0 && cf + 1 < args.Length) tab.CharFirst = args[cf + 1];
        var cl = Array.IndexOf(args, "--charlast");
        if (cl >= 0 && cl + 1 < args.Length) tab.CharLast = args[cl + 1];
        var cs = Array.IndexOf(args, "--charstory");
        if (cs >= 0 && cs + 1 < args.Length) tab.CharStory = args[cs + 1].Replace('_', ' ');
        if (cf >= 0 || cl >= 0 || cs >= 0) _vm.SaveCharacterName(tab);

        if (args.Contains("--everyslot")) { tab.ShowEverySlot = true; _vm.RefillLook(tab); }

        var sl = Array.IndexOf(args, "--slot");
        if (sl >= 0 && sl + 2 < args.Length)
        {
            if (Row(args[sl + 1]) is not { } row)
                _vm.Say($"--slot: no slot matching '{args[sl + 1]}' among {tab.LookRows.Count}");
            else
            {
                var (all, _) = _vm.SlotCandidates(row);
                var want = all.FirstOrDefault(
                    c => c.Name.Contains(args[sl + 2], StringComparison.OrdinalIgnoreCase));
                if (want is null)
                    _vm.Say($"--slot: nothing matching '{args[sl + 2]}' among {all.Count} " +
                            $"candidate(s) for {row.Shown}");
                else _vm.SetSlot(tab, row, want.Package);
            }
        }

        var rs = Array.IndexOf(args, "--resetslot");
        if (rs >= 0 && rs + 1 < args.Length)
        {
            if (Row(args[rs + 1]) is { } row) _vm.ResetSlot(tab, row);
            else _vm.Say($"--resetslot: no slot matching '{args[rs + 1]}'");
        }

        var es = Array.IndexOf(args, "--emptyslot");
        if (es >= 0 && es + 1 < args.Length)
        {
            if (Row(args[es + 1]) is { } row) _vm.EmptySlot(tab, row);
            else _vm.Say($"--emptyslot: no slot matching '{args[es + 1]}'");
        }

        // --play / --unplay <key[,key...]>: tick or untick the boxes on HOW THEY PLAY.
        //
        // One flag taking a list rather than eight flags: what these are for is exercising the
        // combinations, and the interesting ones are pairs. Keys are the same strings SetPlay
        // forks on, so a key that is wrong here is wrong everywhere.
        // LEFT TO RIGHT, AND REPEATS ALLOWED, so a sequence can be driven rather than a state.
        // `--play rifle,owngun --unplay rifle --play rifle` is the one case that matters most
        // here: it is the untick and re-tick dance that silently threw away a choice.
        for (var i = 0; i < args.Length - 1; i++)
        {
            var on = args[i].Equals("--play", StringComparison.Ordinal);
            if (!on && !args[i].Equals("--unplay", StringComparison.Ordinal)) continue;
            foreach (var key in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                await Box(tab, key.Trim(), on);
        }

        var sc = Array.IndexOf(args, "--storycount");
        if (sc >= 0 && sc + 1 < args.Length)
        { tab.StoryCount = args[sc + 1]; await _vm.SetStoryCount(tab); }

        if (args.Contains("--remake")) await _vm.RemakeCharacter(tab);

        // --offeragain: the rename that brings back a recruit the game has stopped drawing.
        // Its whole result is WHICH NAMES MOVED, so --dumpchar prints them.
        if (args.Contains("--offeragain")) await _vm.OfferAgain(tab);

        // --card <key>: fold every card but this one, so a card far down the column can be
        // photographed without a mouse. The look table is 36 rows tall and pushes everything
        // above it off the top of a screenshot.
        var cd = Array.IndexOf(args, "--card");
        if (cd >= 0 && cd + 1 < args.Length)
        {
            var key = args[cd + 1];
            var hit = tab.EditSections.FirstOrDefault(
                s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                _vm.Say($"--card {key}: no section by that name — " +
                        string.Join(", ", tab.EditSections.Where(s => s.Present).Select(s => s.Key)));
            else
            {
                foreach (var s in tab.EditSections) s.Open = ReferenceEquals(s, hit);
                tab.SelectSection(hit);
            }
        }

        // WHAT IS ON THE TWO CARDS, read back out. A screenshot proves the panel drew; this
        // proves what it drew came off the project, which is the half a picture cannot show.
        if (args.Contains("--dumpchar"))
        {
            _vm.Say($"[char] who         '{tab.CharFirst}' '{tab.CharLast}'");
            _vm.Say($"[char] story       {tab.CharStory.Length} char(s)" +
                    (tab.NoStoryYet ? "   (none yet)" : ""));
            _vm.Say($"[char] from        {tab.Copy.Template}");
            _vm.Say($"[char] rig         {tab.CharFrom}");
            _vm.Say($"[char] name part   {tab.NamePart?.Id ?? "NONE"}" +
                    (tab.HasCharNameWarn ? "   " + tab.CharNameWarn : ""));
            _vm.Say($"[char] offered     {tab.CharOffered}");
            _vm.Say($"[char] bespoke     {tab.Bespoke}" +
                    (tab.HasLookProblem ? "   PROBLEM " + tab.LookProblem : ""));
            _vm.Say($"[char] slots       {tab.LookRows.Count(r => r.IsSlot)} shown, " +
                    $"{tab.Copy.Slots.Count} changed, every={tab.ShowEverySlot}");
            foreach (var r in tab.LookRows.Where(r => r.IsSlot && r.Mine))
                _vm.Say($"[char]   yours     {r.Shown} = " +
                        (r.Bare ? "(empty)" : r.Value));

            _vm.Say($"[char] rig warn    {(tab.HasRigWarn ? tab.RigWarn : "none — a hero rig")}");
            _vm.Say("[char] plays       " + string.Join("  ", new[]
            {
                Says("kit", tab.ShowEnemyKit, tab.PlayEnemyKit),
                Says("pawn", tab.ShowEnemyPawn, tab.PlayEnemyPawn),
                Says("story", tab.ShowStory, tab.PlayStory),
                Says("rifle", tab.ShowRifle, tab.PlayRifle),
                Says("owngun", tab.ShowOwnGun, tab.PlayOwnGun),
                Says("tactical", tab.ShowTactical, tab.PlayTactical),
                Says("riganim", tab.ShowRigAnim, tab.PlayRigAnim),
                Says("sparespec", tab.ShowSpareSpec, tab.PlaySpareSpec),
            }));
            if (tab.ShowStoryCount) _vm.Say($"[char] story count {tab.StoryCount}");
            _vm.Say($"[char] remake      " +
                    (tab.HasRemake ? tab.RemakeSays : "nothing is behind") +
                    $"   ready={tab.RemakeReady}");
            _vm.Say($"[char] offer       present={tab.SecOffer.Present}  " +
                    (tab.OfferSays.Length > 0 ? tab.OfferSays : "not offered in a pool"));
        }

        static string Says(string name, bool shown, bool on)
            => shown ? $"{name}={(on ? "ON" : "off")}" : $"{name}=n/a";

        async Task Box(CharacterTab at, string key, bool on)
        {
            switch (key)
            {
                case "kit": at.PlayEnemyKit = on; break;
                case "pawn": at.PlayEnemyPawn = on; break;
                case "story": at.PlayStory = on; break;
                case "rifle": at.PlayRifle = on; break;
                case "owngun": at.PlayOwnGun = on; break;
                case "tactical": at.PlayTactical = on; break;
                case "riganim": at.PlayRigAnim = on; break;
                case "sparespec": at.PlaySpareSpec = on; break;
                default: _vm.Say($"--play: no box called '{key}'"); return;
            }
            await _vm.SetPlay(at, key);
        }

        // A REAL SLOT BEATS ITS UMBRELLA. "Helmet" names both Outfit.Helmet, which decides what
        // KIND of thing may go there, and Outfit.Helmet.Mesh, which is the thing — and with
        // --everyslot on, the umbrella is in the list and sorts first. A fragment matching both
        // means the one you can put a part into.
        SheetTab.LookRow? Row(string want)
        {
            var hits = tab.LookRows
                          .Where(r => r.IsSlot &&
                                      (r.Shown.Contains(want, StringComparison.OrdinalIgnoreCase) ||
                                       r.Tag.Contains(want, StringComparison.OrdinalIgnoreCase)))
                          .ToList();
            return hits.FirstOrDefault(r => !r.Plumbing) ?? hits.FirstOrDefault();
        }
    }

    /// <summary>
    /// DEV, for --attachedit: do to the modification's REAL controls what a person does.
    ///
    /// A number row is typed into its own box and the box is left; "&lt;reset&gt;" and "&lt;action&gt;"
    /// press that row's Reset and "Give it one" buttons; "name" and "text" are the two text boxes of
    /// the Black Market card; "rarity" chooses in its drop-down; "&lt;resetrarity&gt;" and "&lt;putback&gt;"
    /// press the card's own buttons. False when the control is not on screen, and the caller then
    /// says so and calls the view model's own method instead.
    /// </summary>
    private async Task<bool> DriveAttachmentControl(object target, string text)
    {
        await Task.Delay(700);   // a layout pass, so the boxes exist
        var all = this.GetVisualDescendants().ToList();
        var tab = _vm.Tab as ModificationTab;

        async Task Leave(TextBox typedIn)
        {
            // LEAVE THE BOX, as clicking elsewhere does: focus goes to ANOTHER text box (the
            // section list's filter, which is on screen whenever a section is), and the box that
            // was typed in loses it.
            var elsewhere = all.OfType<TextBox>().FirstOrDefault(
                                t => !ReferenceEquals(t, typedIn) && t.IsVisible && t.IsEffectivelyVisible &&
                                     (t.Name == "ShopNameBox" || t.Name == "ShopTextBox"))
                            ?? all.OfType<TextBox>().FirstOrDefault(
                                t => !ReferenceEquals(t, typedIn) && t.IsEffectivelyVisible);
            var ok = elsewhere?.Focus();
            await Task.Delay(500);
            var now = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            _vm.Say($"[attachedit] left the box: focus asked of {(elsewhere?.Name ?? elsewhere?.Watermark ?? "(nothing)")} -> {ok}; " +
                    $"focus is now on {now?.GetType().Name} {(now as Control)?.Name}");
        }

        async Task<bool> Press(Button? b)
        {
            if (b is null) return false;
            b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(500);
            return true;
        }

        async Task<bool> Type(TextBox? box)
        {
            if (box is null) return false;
            box.BringIntoView();
            box.Focus();
            await Task.Delay(150);
            box.Text = text;
            await Task.Delay(150);
            await Leave(box);
            return true;
        }

        switch (target)
        {
            case SheetTab.EffectRow row when text == "<reset>":
                return await Press(all.OfType<Button>().FirstOrDefault(
                    b => ReferenceEquals(b.DataContext, row) && (b.Content as string) == "Reset"));

            case SheetTab.EffectRow row when text == "<action>":
                return await Press(all.OfType<Button>().FirstOrDefault(
                    b => ReferenceEquals(b.DataContext, row) && (b.Content as string) == row.ActionLabel));

            case SheetTab.EffectRow row:
                return await Type(all.OfType<TextBox>().FirstOrDefault(t => ReferenceEquals(t.DataContext, row)));

            case "name":
                return await Type(all.OfType<TextBox>().FirstOrDefault(t => t.Name == "ShopNameBox"));
            case "text":
                return await Type(all.OfType<TextBox>().FirstOrDefault(t => t.Name == "ShopTextBox"));

            case "rarity":
            {
                var combo = all.OfType<ComboBox>().FirstOrDefault(c => c.Name == "ShopRarityBox");
                if (combo is null) return false;
                combo.SelectedItem = text;
                await Task.Delay(500);
                return true;
            }

            case "<resetrarity>":
                return await Press(all.OfType<Button>().FirstOrDefault(
                    b => ReferenceEquals(b.DataContext, tab) && (b.Content as string) == "Reset"));

            case "<putback>":
                return await Press(all.OfType<Button>().FirstOrDefault(
                    b => ReferenceEquals(b.DataContext, tab) &&
                         (b.Content as string) == "Put everything about this one back"));
        }
        return false;
    }

    private async Task AttachSequenceAsync(string spec)
    {
        var halves = spec.Split('@');
        var names = halves[0].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var folder = halves.Length > 1 ? halves[1] : null;
        static string Live()
        {
            var (meshes, _, _, made, disposed) = UEToolkit.App.Rendering.RenderMesh.LiveBuffers;
            var (tex, tb, up, del) = UEToolkit.App.Rendering.TextureCache.LiveTextures;
            return $"mesh buffers alive {meshes:N0} (made {made:N0}, disposed {disposed:N0})  textures alive {tex:N0} ({tb / 1048576.0:N0} MB; uploaded {up:N0}, deleted {del:N0})";
        }
        if (names.Length < 2 || !await _vm.OpenByNameAsync(names[0])) { Console.WriteLine("[attach] need a base and at least one part, and the base must open"); return; }
        if (_vm.Tab is not MeshTab tab) { Console.WriteLine("[attach] the base did not open as a model"); return; }
        for (var waited = 0; tab.View.SectionCount == 0 && tab.View.Fault is null && waited < 90_000; waited += 250) await Task.Delay(250);
        await Task.Delay(2500);
        Console.WriteLine($"[attach] base {names[0]}: {tab.View.SectionCount} section(s) | {Live()}");
        if (folder is not null) { Directory.CreateDirectory(folder); tab.View.ShotTo(Path.Combine(folder, "before.png")); await Task.Delay(1000); }
        foreach (var rawPart in names.Skip(1))
        {
            // A "~" before a part name: take it off while the base's tab is NOT the shown one (another tab is in front, so the
            // base's view has handed its parts back to the queue), then come back. The part must stay gone.
            var hidden = rawPart.StartsWith('~');
            var part = rawPart.TrimStart('~');
            var cand = _vm.AttachCandidates(tab).FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (cand.Path is null) { Console.WriteLine($"[attach] {part}: not a skeletal mesh in the game"); continue; }
            var before = tab.View.Attached.Count;
            await _vm.AttachMeshAsync(tab, cand.Path, cand.Name);
            for (var waited = 0; tab.View.Attached.Count == before && waited < 60_000; waited += 250) await Task.Delay(250);
            await Task.Delay(2000);
            Console.WriteLine($"[attach] attached {part}: {tab.View.Attached.Count} attached | {Live()}");
            MeshTab? front = null;
            if (hidden)
            {
                if (!await _vm.OpenByNameAsync(part) || _vm.Tab is not MeshTab other || ReferenceEquals(other, tab)) { Console.WriteLine("[attach] could not put another tab in front"); continue; }
                front = other;
                for (var waited = 0; other.View.SectionCount == 0 && other.View.Fault is null && waited < 90_000; waited += 250) await Task.Delay(250);
                await Task.Delay(2500);
                Console.WriteLine($"[attach] {part}: the base's tab is hidden now");
            }
            _vm.DetachMesh(tab, cand.Path);
            if (hidden)
            {
                _vm.Tab = tab;
                await Task.Delay(6000);
                Console.WriteLine($"[attach] {part}: removed while hidden, tab shown again: {tab.View.Attached.Count} attached (must be 0) -> {(tab.View.Attached.Count == 0 ? "STAYS GONE" : "CAME BACK")}");
                _vm.CloseTab(front!);
                await Task.Delay(1500);
                continue;
            }
            await Task.Delay(2000);
            Console.WriteLine($"[attach] detached {part}: {tab.View.Attached.Count} attached | {Live()}");
        }
        if (folder is not null) { tab.View.ShotTo(Path.Combine(folder, "after.png")); await Task.Delay(1000); }
        _vm.CloseTab(tab);
        await Task.Delay(2500);
        Console.WriteLine($"[attach] closed the tab | {Live()}");
    }

    private async Task PartsSequenceAsync(string[] names)
    {
        static string Live()
        {
            var (meshes, vb, ib, made, disposed) = UEToolkit.App.Rendering.RenderMesh.LiveBuffers;
            var (tex, tb, up, del) = UEToolkit.App.Rendering.TextureCache.LiveTextures;
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return $"mesh buffers alive {meshes:N0} ({(vb + ib) / 1048576.0:N1} MB; made {made:N0}, disposed {disposed:N0})  " +
                   $"textures alive {tex:N0} ({tb / 1048576.0:N0} MB; uploaded {up:N0}, deleted {del:N0})  private {p.PrivateMemorySize64 / 1073741824.0:N2} GB";
        }

        Console.WriteLine($"[parts] before anything: {Live()}");
        var opened = new List<MeshTab>();
        foreach (var name in names)
        {
            var before = _vm.Tabs.Count;
            if (!await _vm.OpenByNameAsync(name)) { Console.WriteLine($"[parts] {name}: not opened"); continue; }
            if (_vm.Tab is not MeshTab tab) { Console.WriteLine($"[parts] {name}: opened as {_vm.Tab?.GetType().Name ?? "nothing"}, not a model"); continue; }
            for (var waited = 0; tab.View.SectionCount == 0 && tab.View.Fault is null && waited < 90_000; waited += 250) await Task.Delay(250);
            await Task.Delay(2000);                     // the last frame, and the previous tab's viewport taking itself down
            opened.Add(tab);
            Console.WriteLine($"[parts] after {name}: tabs {_vm.Tabs.Count} (was {before}), {tab.View.SectionCount} section(s), {tab.View.TexturedSections} textured | {Live()}");

            // THE SAME VIEW SHOWING ONE MODEL AFTER ANOTHER, which a tab per asset never does: hand this view the PREVIOUS
            // part's model, and see whether the textures of the one it showed first stay on the GPU beside the new ones.
            if (opened.Count >= 2 && opened[^2].Asset is { } previous)
            {
                var built = new TaskCompletionSource();
                void Done() => built.TrySetResult();
                tab.View.Loaded += Done;
                tab.View.Show(previous);
                await Task.WhenAny(built.Task, Task.Delay(60_000));
                tab.View.Loaded -= Done;
                await Task.Delay(1500);
                Console.WriteLine($"[parts] the same view, now showing {opened[^2].Title}: {tab.View.SectionCount} section(s), {tab.View.TexturedSections} textured | {Live()}");
            }
        }
        foreach (var tab in opened.AsEnumerable().Reverse())
        {
            _vm.CloseTab(tab);
            await Task.Delay(2500);
            Console.WriteLine($"[parts] closed a tab, {_vm.Tabs.Count} left | {Live()}");
        }
    }

    private async Task DriveTabSwitchAsync(string spec)
    {
        var p = spec.Split('|');
        if (p.Length is not (5 or 6)) { _vm.Say("--tabswitch: kind|A|B|text|mode[|tabbox]"); return; }
        var weapon = p[0] == "weapon";                 // a weapon copy's NUMBERS box (SheetTab.StatRow)
        var enemy = p[0] == "enemy";                   // an enemy group's multiplier box (SheetTab.EnemyRow)
        var spec1 = p[0] == "spec";                    // a specialization copy's first rung (SheetTab.RungRow)
        var tabLevel = p.Length == 6 && p[5] == "tabbox"; // a box that binds the TAB itself, not one of its rows

        var a = await _vm.DevOpenInEditorAsync(p[0], p[1]);
        var b = await _vm.DevOpenInEditorAsync(p[0], p[2]);
        if (a is null || b is null) { _vm.Say("[tabswitch] a tab did not open"); return; }

        _vm.Tab = a;
        await Task.Delay(1200);
        var all = this.GetVisualDescendants().ToList();
        var row = weapon ? null : a.EffectRows.FirstOrDefault(r => r.CanType && !r.HasAction);
        var stat = weapon ? a.WeaponStats.FirstOrDefault() : null;
        var foe = enemy ? a.EnemyRows.FirstOrDefault(r => r.CanScale) : null;
        var rung = spec1 ? a.Rungs.FirstOrDefault() : null;
        var label = row?.Label ?? stat?.Label ?? foe?.Shown ?? rung?.Where ?? "";
        TextBox? box;
        if (tabLevel)
            // The first visible, writable box whose DataContext IS the tab (it inherits it from the panel): the shape
            // Fit, the tint colour, the slot name and the story count share.
            box = all.OfType<TextBox>().FirstOrDefault(t => ReferenceEquals(t.DataContext, a) && t.IsEffectivelyVisible && !t.IsReadOnly);
        else
            box = all.OfType<TextBox>().FirstOrDefault(t => ReferenceEquals(t.DataContext, (object?) row ?? (object?) stat ?? (object?) foe ?? rung) && (row ?? (object?) stat ?? (object?) foe ?? rung) is not null);
        var strip = all.OfType<Button>().FirstOrDefault(x => ReferenceEquals(x.DataContext, b) && x.Content is TextBlock);
        if ((row is null && stat is null && foe is null && rung is null && !tabLevel) || box is null || strip is null)
        { _vm.Say($"[tabswitch] no box ({label}/{box is null}) or no strip button ({strip is null})"); return; }

        string Held() => weapon ? _vm.DevWeaponHeld() : enemy ? _vm.DevEnemyHeld() : spec1 ? _vm.DevSpecHeld() : _vm.DevHeld();
        string Shown() => tabLevel ? $"box text '{box.Text}'" : weapon ? $"{stat!.Label}='{stat.Text}'" : enemy ? $"{foe!.Shown}='{foe.Text}'" : spec1 ? $"{rung!.Where}: level '{rung.Level}'" : $"{row!.Label}='{row.Value}' (mine={row.Mine})";

        _vm.Say($"[tabswitch] before: project {Held()}; A {a.Title}: {StudioVm.DevBoxes(a)}; B {b.Title}: {StudioVm.DevBoxes(b)}");
        box.Focus();
        await Task.Delay(300);
        box.LostFocus += (_, _) =>
            _vm.Say($"[tabswitch] LostFocus fired on {a.Title}'s box; the open tab is {_vm.Tab?.Title}; box text '{box.Text}'; " +
                    $"the box's DataContext is {(ReferenceEquals(box.DataContext, a) ? "A (its own tab)" : ReferenceEquals(box.DataContext, b) ? "B (the other tab)" : box.DataContext?.GetType().Name ?? "null")}");
        box.Text = p[3];
        await Task.Delay(300);
        _vm.Say($"[tabswitch] typed '{p[3]}' into {a.Title} / {label} and did NOT leave it; focus is on " +
                $"{(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control)?.GetType().Name}");

        switch (p[4])
        {
            case "list": _vm.Tab = b; break;
            case "focus":
                strip.Focus();
                await Task.Delay(400);
                _vm.Say($"[tabswitch] the strip button took focus; the open tab is still {_vm.Tab?.Title}");
                strip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
            default: strip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
        }
        await Task.Delay(1500);
        _vm.Say($"[tabswitch] after: the open tab is {_vm.Tab?.Title}; project {Held()}; " +
                $"A: {StudioVm.DevBoxes(a)}; B: {StudioVm.DevBoxes(b)}");

        // BACK TO A: is the typed number still in its box, and is it recorded?
        _vm.Tab = a;
        await Task.Delay(1000);
        _vm.Say($"[tabswitch] back on {a.Title}: its box shows {Shown()}; project {Held()}");
    }

#endif

    // ------------------------------------------------------------------ what an ability does

    private static SheetTab.EffectRow? EffectOf(object? sender)
        => (sender as Control)?.DataContext as SheetTab.EffectRow;

    /// <summary>
    /// A number, ON LOSING FOCUS rather than per keystroke.
    ///
    /// Typing "12" passes through "1", and writing that would record a change to 1 and then a
    /// change to 12, each of which saves the project and refills the card under the cursor.
    /// </summary>
    private void OnEffectNumber(object? sender, RoutedEventArgs e)
    {
        if (EffectOf(sender) is not { } row) return;
        switch (OwnerOf(row))
        {
            case AbilityTab tab: _vm.SetEffectNumber(tab, row); break;
            case TableTab ttab: _vm.SetTableNumber(ttab, row); break;
            case ModificationTab mtab: _vm.SetModificationNumber(mtab, row); break;
        }
    }

    private void OnResetEffectNumber(object? sender, RoutedEventArgs e)
    {
        if (EffectOf(sender) is not { } row) return;
        switch (OwnerOf(row))
        {
            case AbilityTab tab: _vm.ResetEffectNumber(tab, row); break;
            case TableTab ttab: _vm.ResetTableNumber(ttab, row); break;
            case ModificationTab mtab: _vm.ResetModificationNumber(mtab, row); break;
        }
    }

    /// <summary>A row's own act: "Give it one" on a modification that carries no number of its own.</summary>
    private void OnEffectAction(object? sender, RoutedEventArgs e)
    {
        if (EffectOf(sender) is { } row && OwnerOf(row) is ModificationTab tab)
            _vm.GiveModificationNumber(tab, row);
    }

    /// <summary>
    /// THE TAB A ROW BELONGS TO -- not the tab on screen.
    ///
    /// A box writes when it is LEFT, and leaving it can be the very act that changes the tab (a
    /// tab clicked with no focus move, a row opened from the list, a menu command): the event then
    /// arrives with the new tab already showing. Asking "which tab is open" at that moment wrote
    /// the number typed on one modification onto the next one's (measured, --tabswitch). The row
    /// knows whose it is, so the handler asks the row. A row that no tab holds any more (its card
    /// was refilled under it) belongs to nobody, and nothing is written for it.
    /// </summary>
    private SheetTab? OwnerOf(SheetTab.EffectRow row)
        => _vm.Tabs.OfType<SheetTab>().FirstOrDefault(
            t => t.EffectRows.Contains(row) || t.ShopRows.Contains(row));

    private AbilityTab? OwnerOf(SheetTab.PointRow row)
        => _vm.Tabs.OfType<AbilityTab>().FirstOrDefault(t => t.PointRows.Contains(row));

    private AbilityTab? OwnerOf(SheetTab.TagBoxRow row)
        => _vm.Tabs.OfType<AbilityTab>().FirstOrDefault(t => t.TagBoxRows.Contains(row));

    private AbilityTab? OwnerOf(SheetTab.ProgRow row)
        => _vm.Tabs.OfType<AbilityTab>().FirstOrDefault(t => t.Progression.Contains(row));

    // THE SAME QUESTION FOR THE REST OF THE BOXES THAT WRITE ON LOSING FOCUS (final review I-3). Each of these
    // used to ask `_vm.Tab`, which is the tab on screen WHEN THE EVENT ARRIVES, and a tab changed by code (a list
    // row, the menu) arrives before the box is left: the number typed on one weapon was written onto the next one's
    // copy (measured, --tabswitch weapon). The row knows whose it is; a row no tab holds any more belongs to
    // nobody and nothing is written for it.
    private WeaponTab? OwnerOf(SheetTab.StatRow row)
        => _vm.Tabs.OfType<WeaponTab>().FirstOrDefault(t => t.WeaponStats.Contains(row));

    private EnemyTab? OwnerOf(SheetTab.EnemyRow row)
        => _vm.Tabs.OfType<EnemyTab>().FirstOrDefault(t => t.EnemyRows.Contains(row));

    private SpecTab? OwnerOf(SheetTab.RungRow row)
        => _vm.Tabs.OfType<SpecTab>().FirstOrDefault(t => t.Rungs.Contains(row));

    private SheetTab? OwnerOf(SheetTab.MaterialGroup group)
        => _vm.Tabs.OfType<SheetTab>().FirstOrDefault(t => t.MaterialGroups.Contains(group));

    private MissionTab? OwnerOf(SheetTab.LotteryRow row)
        => _vm.Tabs.OfType<MissionTab>().FirstOrDefault(t => t.LotteryRows.Contains(row));

    /// <summary>
    /// A box that binds the TAB ITSELF (the fit fields, the tint colour, the story count) belongs to the tab it
    /// inherits as its DataContext, which is the tab the panel was drawn for -- not the one that is open now.
    /// Measured with --tabswitch: left by a pointer press, LostFocus arrives with the box still on its own tab;
    /// moved by code, LostFocus did not arrive at all, so the typed text stays in the old tab's own property.
    /// </summary>
    private static T? TabOf<T>(object? sender) where T : SheetTab
        => (sender as Control)?.DataContext as T;

    // ------------------------------------------------------------------ a modification's shop half

    /// <summary>The name, ON LEAVING THE BOX, as the numbers are: a half-typed name is not a name.</summary>
    private void OnShopName(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModificationTab tab) _vm.SetModificationText(tab, name: true);
    }

    private void OnShopText(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModificationTab tab) _vm.SetModificationText(tab, name: false);
    }

    /// <summary>
    /// A rarity chosen. The drop-down also raises this when the card is FILLED (its items are
    /// replaced and its selection written), and that is not a person choosing one: the tab's
    /// fill latch says so, and the view model ignores it.
    /// </summary>
    private void OnShopRarity(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModificationTab tab && (sender as ComboBox)?.SelectedItem is string word)
            _vm.SetModificationRarity(tab, word);
    }

    private void OnResetShopRarity(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModificationTab tab) _vm.ResetModificationRarity(tab);
    }

    /// <summary>Everything the mod does to this modification, off: its ability's override and its shop item's.</summary>
    private void OnPutModificationBack(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ModificationTab tab) _vm.RevertModification(tab.Entry);
    }

    private static SheetTab.PointRow? PointOf(object? sender)
    {
        // THE ROW IS NOT ALWAYS THE SENDER'S OWN CONTEXT: a button inside the nested stack sits
        // on whatever that stack was given, so the row is found by walking out to the template.
        var at = sender as Control;
        while (at is not null)
        {
            if (at.DataContext is SheetTab.PointRow row) return row;
            at = at.Parent as Control;
        }
        return null;
    }

    /// <summary>
    /// The assets the game's own registry says fit this slot.
    ///
    /// NARROWED, not complete — 67 candidates for a cooldown effect out of 35,000 assets — so
    /// everything offered is something the game itself puts here. The typed box beside the button
    /// is the escape hatch, and is deliberately not checked against this list.
    /// </summary>
    private async void OnChoosePoint(object? sender, RoutedEventArgs e)
    {
        if (PointOf(sender) is not { } row || OwnerOf(row) is not { } tab) return;

        var choices = await _vm.PointCandidatesAsync(row);
        if (choices.Count == 0)
        { _vm.Say($"the registry found nothing of the right kind for {row.Label}"); return; }

        var picker = new PickAssetWindow(
            "Choose: " + row.Label,
            $"{choices.Count} {row.KindName} in the game. Every one of these derives from the " +
            "same native class the property already points at, which is how the list gets short " +
            "enough to read. To use something else, type it in the box beside the button.",
            choices, images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } package) _vm.SetPoint(tab, row, package);
    }

    private void OnTypedPoint(object? sender, RoutedEventArgs e)
    {
        if (PointOf(sender) is not { } row || OwnerOf(row) is not { } tab) return;
        if (row.Typed.Trim().Length == 0)
        { _vm.Say("type a package path or a copy id first"); return; }
        _vm.SetPoint(tab, row, row.Typed);
    }

    private void OnResetPoint(object? sender, RoutedEventArgs e)
    {
        if (PointOf(sender) is { } row && OwnerOf(row) is { } tab) _vm.SetPoint(tab, row, "");
    }

    private static SheetTab.TagBoxRow? TagBoxOf(object? sender)
    {
        var at = sender as Control;
        while (at is not null)
        {
            if (at.DataContext is SheetTab.TagBoxRow row) return row;
            at = at.Parent as Control;
        }
        return null;
    }

    private void OnAddTagBox(object? sender, RoutedEventArgs e)
    {
        if (TagBoxOf(sender) is { } row && OwnerOf(row) is { } tab) _vm.AddTagBox(tab, row);
    }

    private void OnSetTagBox(object? sender, RoutedEventArgs e)
    {
        if (TagBoxOf(sender) is { } row && OwnerOf(row) is { } tab) _vm.SetTagBox(tab, row);
    }

    private void OnDropTagBox(object? sender, RoutedEventArgs e)
    {
        if (TagBoxOf(sender) is { } row && OwnerOf(row) is { } tab) _vm.DropTagBox(tab, row);
    }

    // ------------------------------------------------------------------ how a character plays

    /// <summary>
    /// The eight boxes, each one line.
    ///
    /// `Click` AND NOT the checked state alone. Click is raised by a person pressing the control;
    /// a fill that assigns IsChecked through the binding does not raise it. Binding the write to
    /// the state instead is what cost the mission schedule two rounds.
    /// </summary>
    private async void OnPlayEnemyKit(object? sender, RoutedEventArgs e) => await Play("kit");
    private async void OnPlayEnemyPawn(object? sender, RoutedEventArgs e) => await Play("pawn");
    private async void OnPlayStory(object? sender, RoutedEventArgs e) => await Play("story");
    private async void OnPlayRifle(object? sender, RoutedEventArgs e) => await Play("rifle");
    private async void OnPlayOwnGun(object? sender, RoutedEventArgs e) => await Play("owngun");
    private async void OnPlayTactical(object? sender, RoutedEventArgs e) => await Play("tactical");
    private async void OnPlayRigAnim(object? sender, RoutedEventArgs e) => await Play("riganim");
    private async void OnPlaySpareSpec(object? sender, RoutedEventArgs e) => await Play("sparespec");

    private async Task Play(string which)
    {
        if (_vm.Tab is CharacterTab tab) await _vm.SetPlay(tab, which);
    }

    /// <summary>
    /// How many story recipes, on LOSING FOCUS rather than per keystroke.
    ///
    /// Each recipe is several assets and three table entries, so rebuilding them while somebody
    /// is still typing "12" would build one, then twelve, then throw both away.
    /// </summary>
    private async void OnStoryCount(object? sender, RoutedEventArgs e)
    {
        if (TabOf<CharacterTab>(sender) is { } tab) await _vm.SetStoryCount(tab);
    }

    private async void OnRemakeCharacter(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CharacterTab tab) await _vm.RemakeCharacter(tab);
    }

    private async void OnOfferAgain(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CharacterTab tab) await _vm.OfferAgain(tab);
    }

    // ------------------------------------------------------------------ when a mission turns up

    /// <summary>
    /// Add this mission to a campaign turn, or take it off one.
    ///
    /// THE ONLY ROUTE THAT TAKES NOTHING AWAY: the turn keeps every mission it already placed and
    /// gains this one.
    /// </summary>
    private void OnSetArcAdd(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.TurnRow row &&
            _vm.Tab is MissionTab tab)
            _vm.SetArcAdd(tab, row);
    }

    /// <summary>Take a campaign slot, or give it back. This one DOES displace a shipped mission.</summary>
    private void OnSetPlacement(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.SlotRow row &&
            _vm.Tab is MissionTab tab)
            _vm.SetPlacement(tab, row);
    }

    private async void OnAddLottery(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is MissionTab tab) await _vm.AddLottery(tab);
    }

    private static SheetTab.LotteryRow? LotteryOf(object? sender)
    {
        // THE ROW IS NOT ALWAYS THE SENDER'S OWN CONTEXT. A control inside a nested template —
        // a gate's Remove, a require tag's x — sits on the gate or the line, so the lottery row
        // has to be found by walking out to the template that owns it.
        var at = sender as Control;
        while (at is not null)
        {
            if (at.DataContext is SheetTab.LotteryRow row) return row;
            at = at.Parent as Control;
        }
        return null;
    }

    private async void OnDropLottery(object? sender, RoutedEventArgs e)
    {
        if (LotteryOf(sender) is { } row && _vm.Tab is MissionTab tab)
            await _vm.DropLottery(tab, row);
    }

    private void OnPickLotteryTable(object? sender, SelectionChangedEventArgs e)
        => Lottery(sender, "table");

    private void OnPickLotteryRow(object? sender, SelectionChangedEventArgs e)
        => Lottery(sender, "whichrow");

    private void OnLotteryHow(object? sender, RoutedEventArgs e) => Lottery(sender, "how");

    private void OnLotteryWeight(object? sender, RoutedEventArgs e) => Lottery(sender, "weight");

    private void Lottery(object? sender, string which)
    {
        // WHILE THE CARD IS STILL FILLING every combo raises SelectionChanged as its list and
        // its selection arrive. For the take-over chooser that was not merely wasteful: its
        // handler writes and then RE-FILLS, and the re-fill raised the event again — three
        // thousand frames of SelectionChanged to SetLottery to FillLottery before the stack ran
        // out. See SheetTab.Filling.
        if (LotteryOf(sender) is { } row && OwnerOf(row) is { Mine: not null } tab &&
            tab.Editing && !tab.Filling)
            _vm.SetLottery(tab, row, which);
    }

    private async void OnTakeEveryRow(object? sender, RoutedEventArgs e)
    {
        if (LotteryOf(sender) is { } row && _vm.Tab is MissionTab tab)
            await _vm.TakeEveryRow(tab, row);
    }

    private async void OnAddRequire(object? sender, RoutedEventArgs e) => await GateTag(sender, "require");
    private async void OnAddIgnore(object? sender, RoutedEventArgs e) => await GateTag(sender, "ignore");

    /// <summary>
    /// A fact the row is gated on, chosen from the ones the game itself uses.
    ///
    /// FROM BOTH ENDS: the facts missions RAISE and the facts table rows TEST. An era gate needs
    /// one of each and they are not the same set.
    /// </summary>
    private async Task GateTag(object? sender, string kind)
    {
        if (LotteryOf(sender) is not { } row || _vm.Tab is not MissionTab tab) return;

        var choices = await _vm.MissionChoicesAsync("fact");
        if (choices.Count == 0) { _vm.Say("the game's missions raise no facts this can read"); return; }

        var picker = new PickAssetWindow(
            kind == "require" ? "A fact this row requires" : "A fact that blocks this row",
            kind == "require"
                ? "The row is only drawn once every fact listed here is set. A mission's own " +
                  "reward can raise one, which is the whole of “appears only after that one”."
                : "The row is never drawn while any fact listed here is set.",
            choices, images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } tag) _vm.AddGateTag(tab, row, kind, tag);
    }

    private void OnDropGateTag(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.LineRow line &&
            LotteryOf(sender) is { } row && _vm.Tab is MissionTab tab)
            _vm.DropGateTag(tab, row, line);
    }

    private void OnAddGate(object? sender, RoutedEventArgs e)
    {
        if (LotteryOf(sender) is { } row && _vm.Tab is MissionTab tab) _vm.AddGate(tab, row);
    }

    private static SheetTab.GateRow? GateOf(object? sender)
        => (sender as Control)?.DataContext as SheetTab.GateRow;

    private async void OnPickGateFact(object? sender, RoutedEventArgs e)
    {
        if (GateOf(sender) is not { } gate || LotteryOf(sender) is not { } row ||
            _vm.Tab is not MissionTab tab) return;

        var choices = await _vm.MissionChoicesAsync("fact");
        if (choices.Count == 0) { _vm.Say("the game's missions raise no facts this can read"); return; }

        var picker = new PickAssetWindow(
            "The fact this condition tests",
            "The game's own crisis table gates every row on " +
            "Facts.Values.Strategy.Skirmish.TableCoilCrisis < 2.",
            choices, images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } tag) { gate.Tag = tag; _vm.SetGate(tab, row, gate, "tag"); }
    }

    private void OnDropGate(object? sender, RoutedEventArgs e)
    {
        if (GateOf(sender) is { } gate && LotteryOf(sender) is { } row &&
            _vm.Tab is MissionTab tab)
            _vm.DropGate(tab, row, gate);
    }

    private void OnGateOperation(object? sender, SelectionChangedEventArgs e) => Gate(sender, "op");
    private void OnGateValue(object? sender, RoutedEventArgs e) => Gate(sender, "value");
    private void OnGateZero(object? sender, RoutedEventArgs e) => Gate(sender, "zero");

    private void Gate(object? sender, string which)
    {
        if (GateOf(sender) is { } gate && LotteryOf(sender) is { } row &&
            OwnerOf(row) is { Mine: not null } tab && tab.Editing && !tab.Filling)
            _vm.SetGate(tab, row, gate, which);
    }

    // ------------------------------------------------------------------ a mission

    /// <summary>
    /// EDIT THE GAME'S OWN MISSION IN PLACE rather than shipping one beside it.
    ///
    /// The author's own idea and the quickest thing to test: a NEW mission has to be drawn from
    /// a weighted table before anyone can look at it, so a failure could be the mission, the
    /// table, the pull schedule or the save's position — four suspects and a campaign turn per
    /// attempt. Editing one already on the map has one suspect and needs no turn at all.
    /// </summary>
    private async void OnEditInPlace(object? sender, RoutedEventArgs e)
    {
        if (_vm.Chosen is not { } picked) return;
        if (picked.Payload is not MissionIndex.Mission mission)
        {
            _vm.Say($"{picked.Name} is not a mission — editing in place is a mission's own route");
            return;
        }
        await _vm.DuplicateMission(mission, inPlace: true);
    }

    /// <summary>One of the five choosers. The choice IS the edit, so it writes on selection.</summary>
    private async void OnPickPlanet(object? sender, SelectionChangedEventArgs e) => await Pick("planet");
    private async void OnPickLevel(object? sender, SelectionChangedEventArgs e) => await Pick("level");
    private async void OnPickFaction(object? sender, SelectionChangedEventArgs e) => await Pick("faction");
    private async void OnPickType(object? sender, SelectionChangedEventArgs e) => await Pick("type");
    private async void OnPickDifficulty(object? sender, SelectionChangedEventArgs e) => await Pick("difficulty");

    private async Task Pick(string which)
    {
        // WHILE THE PANEL IS STILL FILLING, every combo raises SelectionChanged as its list
        // and its selection arrive — and writing then would save the value that was already
        // there, five times, before anybody has touched anything.
        if (_vm.Tab is MissionTab { Mine: not null } tab && tab.Editing && !tab.Filling)
            await _vm.PickMission(tab, which);
    }

    private async void OnMatchThePlanet(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is MissionTab tab) await _vm.MatchThePlanet(tab);
    }

    /// <summary>
    /// The free-text fields, chosen from what the game's own missions use.
    ///
    /// EVERY ONE OF THESE WAS AN EMPTY BOX IN THE OLD STUDIO and the author said he did not know
    /// what went in them. Nobody types a reward table's path from memory, and a near miss is not
    /// an error — it is a mission that builds clean and pays nothing.
    /// </summary>
    private async void OnPickThumbnail(object? sender, RoutedEventArgs e)
        => await PickInto("thumbnail", "A picture for the mission card",
                          t => t.EditThumbnail = t.EditThumbnail);

    private async void OnPickBankTag(object? sender, RoutedEventArgs e)
        => await PickInto("bank", "The image bank tag the card falls back to", null);

    private async void OnPickRegionTag(object? sender, RoutedEventArgs e)
        => await PickInto("regiontag", "Which part of the galaxy map the card sits in", null);

    private async void OnPickRegionAsset(object? sender, RoutedEventArgs e)
        => await PickInto("regionasset", "The map region this belongs to", null);

    private async void OnPickMapConfig(object? sender, RoutedEventArgs e)
        => await PickInto("mapconfig", "The Blueprint that sets the map's rules", null);

    private async void OnPickRewardTable(object? sender, RoutedEventArgs e)
        => await PickInto("rewardtable", "The table this mission's reward is rolled from", null);

    private async void OnPickFact(object? sender, RoutedEventArgs e)
        => await PickInto("fact", "The fact this mission raises when it is completed", null);

    private async Task PickInto(string field, string heading, Action<MissionTab>? _)
    {
        if (_vm.Tab is not MissionTab { Mine: not null } tab) return;

        var choices = await _vm.MissionChoicesAsync(field);
        if (choices.Count == 0)
        { _vm.Say($"{heading}: the game's own missions use none, so there is nothing to offer"); return; }

        var picker = new PickAssetWindow(
            heading,
            "Every one of these is a value the game's own missions already use. Typing one from " +
            "memory is how a mission builds clean and does nothing.",
            choices, images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is not { } chosen) return;

        switch (field)
        {
            case "thumbnail": tab.EditThumbnail = chosen; break;
            case "bank": tab.EditBankTag = chosen; break;
            case "regiontag": tab.EditRegionTag = chosen; break;
            case "regionasset": tab.EditRegionAsset = chosen; break;
            case "mapconfig": tab.EditMapConfig = chosen; break;
            case "rewardtable": tab.EditRewardTable = chosen; break;
            case "fact": tab.EditFactTag = chosen; break;
        }

        // WRITTEN, not left in the box. A picker's answer is a decision, and a decision that
        // needs a second press to stick is one somebody will lose.
        _vm.SaveMissionEdit(tab);
    }

    private async void OnAddObjective(object? sender, RoutedEventArgs e)
        => await AddLine("objective", "An objective for the briefing");

    private async void OnAddReward(object? sender, RoutedEventArgs e)
        => await AddLine("reward", "A reward this mission pays");

    private async void OnAddMissionTag(object? sender, RoutedEventArgs e)
        => await AddLine("tag", "A tag for this mission");

    private async Task AddLine(string kind, string heading)
    {
        if (_vm.Tab is not MissionTab { Mine: not null } tab) return;

        var field = kind switch
        {
            "objective" => "objective",
            "reward" => "reward",
            _ => "missiontag",
        };

        var choices = await _vm.MissionChoicesAsync(field);
        if (choices.Count == 0)
        { _vm.Say($"{heading}: the game's own missions use none"); return; }

        var picker = new PickAssetWindow(
            heading,
            "What the game's own missions carry. A value beyond what the mission you copied has " +
            "cannot be written, and the build reports it.",
            choices, images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } chosen) _vm.AddMissionLine(tab, kind, chosen);
    }

    private void OnDropMissionLine(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.LineRow row &&
            _vm.Tab is MissionTab tab)
            _vm.DropMissionLine(tab, row);
    }

    // ------------------------------------------------------------------ the Armoury's shelves

    /// <summary>
    /// Take a shop slot for this utility item.
    ///
    /// The Armoury sells from a fixed list, so without this a duplicated item is offered to
    /// nobody — and nothing in the game says so.
    /// </summary>
    private async void OnPutOnSale(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CopyTab tab) await _vm.PutOnSale(tab);
    }

    private async void OnTakeOffSale(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CopyTab tab) await _vm.TakeOffSale(tab);
    }

    // ------------------------------------------------------------------ how an import fits

    /// <summary>
    /// The three fields, WRITTEN ON LEAVING THE BOX rather than per keystroke.
    ///
    /// Typing "2.54" one character at a time would be four saves and three re-measurements, and
    /// each measurement parses the whole FBX and loads the template's geometry.
    ///
    /// SHARED BY EVERY TAB THAT WEARS A MESH. The card is on SheetTab, so gating these on
    /// PartTab would be the same fault the material card had: a panel that draws a card, fills
    /// it, and refuses every control on it.
    /// </summary>
    private void OnFitBasis(object? sender, RoutedEventArgs e) => Fit(sender, "basis");
    private void OnFitScaleTyped(object? sender, RoutedEventArgs e) => Fit(sender, "scale");
    private void OnFitMoveX(object? sender, RoutedEventArgs e) => Fit(sender, "x");
    private void OnFitMoveY(object? sender, RoutedEventArgs e) => Fit(sender, "y");
    private void OnFitMoveZ(object? sender, RoutedEventArgs e) => Fit(sender, "z");

    private void Fit(object? sender, string which)
    {
        if (sender is TextBox box && TabOf<SheetTab>(sender) is { } tab) _vm.SetFit(tab, which, box.Text ?? "");
    }

    private void OnFitCentre(object? sender, RoutedEventArgs e) => Place("centre");
    private void OnFitRest(object? sender, RoutedEventArgs e) => Place("rest");
    private void OnFitClear(object? sender, RoutedEventArgs e) => Place("clear");
    private void OnFitScale(object? sender, RoutedEventArgs e) => Place("fitscale");
    private void OnFitPinOne(object? sender, RoutedEventArgs e) => Place("pinone");

    private void Place(string how)
    {
        if (_vm.Tab is SheetTab tab) _vm.PlaceFit(tab, how);
    }

    /// <summary>A shelf of this mod's own, with a name and numbers nobody else's weapons share.</summary>
    private void OnMakeOwnShelf(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is WeaponTab tab) _vm.MakeOwnShelf(tab);
    }

    // ------------------------------------------------------------------ a part's tint

    /// <summary>
    /// Choose the mask this part paints its tint through.
    ///
    /// NARROWED TO TINT MASKS, which is what the old studio's picker did and for the same
    /// reason: the game ships 9,500 textures and 666 of them are masks, so offering all of them
    /// is offering a list in which the answer cannot be found. The scope is a tick, not a
    /// hard filter — a mod's own mask need not be called _Tint.
    /// </summary>
    private async void OnPickTintMask(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not PartTab tab) return;

        var picker = new PickAssetWindow(
            "A tint mask for this part",
            tab.TemplateTintMask.Length > 0
                ? $"Its template paints through {Leaf(tab.TemplateTintMask)}. A mask is drawn for " +
                  "ONE kit's UVs, so one taken from another kit paints the wrong panels — which " +
                  "does not show until the game is launched."
                : "Its template paints no tint. Naming a mask here gives it one.",
            _vm.GameAssets(AssetKind.Texture),
            images: true,
            scopeLabel: "tint masks only",
            scope: c => c.Name.Contains("_Tint", StringComparison.OrdinalIgnoreCase));

        await picker.ShowDialog(this);
        if (picker.Chosen is { } package) _vm.SetTintMask(tab, package);
    }

    private void OnClearTintMask(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is PartTab tab) _vm.SetTintMask(tab, "");
    }

    /// <summary>
    /// Choose the colour, out of the game's own 456.
    ///
    /// WITH THE COLOUR SHOWN, because a hex is not a colour to anybody looking at it. The
    /// palette is already read at mount for the viewport, so this costs nothing.
    /// </summary>
    private async void OnPickTintColour(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not PartTab tab) return;

        if (_vm.Swatches.Count == 0)
        { _vm.Say("the game's colour palette has not been read yet"); return; }

        var picker = new PickAssetWindow(
            "A colour for this part",
            "The colour it paints through its mask. These are the game's own — the same values " +
            "its menus offer — and the hex box beside the button takes any colour you like.",
            _vm.Swatches.Select(s => new PickAssetWindow.Candidate(s.Name, "#" + s.Hex, s.Hex)),
            images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } hex) _vm.SetTintColour(tab, hex);
    }

    private void OnClearTintColour(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is PartTab tab) _vm.SetTintColour(tab, "");
    }

    /// <summary>
    /// The hex box, WRITTEN ON LEAVING IT rather than per keystroke.
    ///
    /// Six digits typed one at a time would be six saves of the project and five of them would
    /// be refused for being too short — a box that argues with somebody halfway through typing.
    /// </summary>
    private void OnTintColourTyped(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && TabOf<PartTab>(sender) is { } tab)
            _vm.SetTintColour(tab, box.Text ?? "");
    }

    private void OnDuplicateAsColour(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is PartTab tab) _vm.DuplicateAsColour(tab);
    }

    // ------------------------------------------------------------------ an enemy group

    /// <summary>
    /// One multiplier, written when the box loses focus.
    ///
    /// Per keystroke would save the project on every character and make "1.25" four writes, two
    /// of which ("1", "1.2") are legitimate values that would each be recorded.
    /// </summary>
    private void OnSetEnemyMultiplier(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SheetTab.EnemyRow row } box &&
            OwnerOf(row) is { } tab)
            _vm.SetEnemyMultiplier(tab, row, box.Text ?? "");
    }

    private void OnClearEnemyGroup(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is EnemyTab tab) _vm.ClearEnemyGroup(tab);
    }

    // ------------------------------------------------------------------ what is inside a copy

    /// <summary>Fold one kind of reference. The group carries its own state, as a card does.</summary>
    private void OnToggleInsideGroup(object? sender, RoutedEventArgs e)
    {
        // THROUGH THE TAB, not straight onto the group. Flipping Open here left the tab knowing
        // nothing about it, and the next keystroke in the filter box rebuilt the group from a
        // rule and undid it. See SheetTab.FoldInside.
        if ((sender as Control)?.Tag is SheetTab.InsideGroup g && _vm.Tab is SheetTab sheet)
            sheet.FoldInside(g);
    }

    private static SheetTab.InsideRow? InsideOf(object? sender)
        => (sender as Control)?.DataContext as SheetTab.InsideRow;

    /// <summary>
    /// Pick a replacement out of what the game actually contains.
    ///
    /// THE LIST BEFORE THE BOX. Pasting a path is the escape hatch, not the way in — nobody
    /// knows what the game's sound events are called, and the index already holds every one of
    /// them. The candidates are narrowed by the SAME rule that labelled the row, so "Choose" on
    /// an Ability offers abilities rather than every data asset in the game.
    /// </summary>
    private async void OnChooseInside(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not CopyTab tab || InsideOf(sender) is not { } row) return;

        var choices = _vm.InsideCandidates(row.Kind);
        if (choices.Count == 0)
        {
            _vm.Say($"{row.Property}: nothing in the game classifies as a {row.Kind.ToLowerInvariant()} " +
                    "— paste a path into the box instead");
            return;
        }

        var picker = new PickAssetWindow(
            $"A {row.Kind.ToLowerInvariant()} for {row.Property}",
            $"It points at {row.GameLeaf} today. Choosing one rewrites both halves of the " +
            "reference — the package and the asset name — because a soft path is two names and " +
            "moving one of them leaves a reference that resolves to nothing." +
            (row.Hard ? "  This one is a hard import, so it is re-pointed rather than renamed." : ""),
            choices, images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } package) _vm.PointInside(tab, row, package);
    }

    private void OnSetInside(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CopyTab tab && InsideOf(sender) is { } row)
            _vm.PointInside(tab, row, row.Typed);
    }

    private void OnRevertInside(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CopyTab tab && InsideOf(sender) is { } row) _vm.RevertInside(tab, row);
    }

    private void OnRemoveCopy(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is CopyTab tab) _vm.RemoveCopy(tab);
    }

    // ------------------------------------------------------------------ the mod's own details

    /// <summary>
    /// Open the panel on the PROJECT, from the menu or from the button beside its name.
    ///
    /// The one thing in this Studio that had no editor: every part, weapon, specialization and
    /// ability could be changed, and the mod carrying them was a read-only line at the top of
    /// the window.
    /// </summary>
    private void OnModDetails(object? sender, RoutedEventArgs e) => _vm.OpenModDetails();

    private async void OnPickModPicture(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not ModTab tab) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "A picture for this mod's card",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.tga"] }],
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is { } path) _vm.SetModPicture(tab, path);
    }

    /// <summary>
    /// The images already in this mod, offered by name.
    ///
    /// ITS OWN MENU RATHER THAN ShowImageMenu, which exists to warn that an import will be
    /// RESIZED to the target texture's shape. There is no target here — the card picture is a
    /// file on disk that nothing cooks — so that menu would print a size comparison against a
    /// texture this picture will never be written into.
    /// </summary>
    private void OnUseModPicture(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not ModTab tab || sender is not Control at) return;

        var menu = new MenuFlyout();
        var mine = _vm.MyImages();

        if (mine.Count == 0)
            menu.Items.Add(new MenuItem
            { Header = "nothing imported into this mod yet", IsEnabled = false });

        foreach (var leaf in mine)
        {
            var shape = _vm.ShapeOf(leaf);
            var item = new MenuItem
            { Header = shape is null ? leaf : $"{leaf}      {shape.Value.W}×{shape.Value.H}" };
            var name = leaf;
            item.Click += (_, _) => _vm.UseModPicture(tab, name);
            menu.Items.Add(item);
        }

        menu.ShowAt(at);
    }

    private void OnClearModPicture(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is ModTab tab) _vm.ClearModPicture(tab);
    }

    /// <summary>Double-click an animation to play it on the model.</summary>
    private async void OnPlayAnimation(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is MeshTab tab && tab.PickedAnim is AnimRow row)
            await _vm.PlayAnimationAsync(tab, row);
    }

    // ------------------------------------------------------------------ A part's meshes

    /// <summary>The row a button in the Meshes list belongs to.</summary>
    private static SheetTab.MeshRow? RowOf(object? sender) =>
        (sender as Control)?.DataContext as SheetTab.MeshRow;

    private async void OnImportMeshRow(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Geometry for {row.Variant} — replacing {row.GameLeaf}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Models") { Patterns = ["*.fbx"] }],
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not { } path) return;

        // A WEAPON'S GEOMETRY GOES SOMEWHERE ELSE. Its pieces come from palettes, so the mod's
        // own mesh is a ModMesh entry keyed by source — not an override on a mesh package, which
        // is what a part's import writes.
        if (_vm.Tab is WeaponTab weapon) _vm.ImportWeaponPiece(weapon, row, path);
        else _vm.ImportMeshRow(row, path);
    }

    /// <summary>
    /// A bolt colour chosen.
    ///
    /// SelectionChanged fires when the card FILLS as well as when somebody picks, so this checks
    /// the choice against what the project already says — otherwise opening a weapon would write
    /// its own current colour back and mark the project changed.
    /// </summary>
    private void OnPickBolt(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm.Tab is not WeaponTab weapon || e.AddedItems.Count == 0) return;
        if (e.RemovedItems.Count == 0) return;              // the fill, not a choice
        if (e.AddedItems[0] is SheetTab.BoltChoice pick) _vm.SetWeaponBolt(weapon, pick.Index);
    }

    private void OnShelfCustom(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is WeaponTab weapon) _vm.SetWeaponShelf(weapon, custom: true);
    }

    private void OnShelfGame(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is WeaponTab weapon) _vm.SetWeaponShelf(weapon, custom: false);
    }

    private void OnGiveOwnStats(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is WeaponTab weapon) _vm.GiveWeaponOwnStats(weapon);
    }

    /// <summary>
    /// One number written, ON LOSING FOCUS rather than per keystroke — typing "120" would
    /// otherwise write 1, then 12, then 120, and each of those is a rehearsed edit.
    /// </summary>
    private void OnSetWeaponStat(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SheetTab.StatRow row } box &&
            OwnerOf(row) is { } weapon)
            _vm.SetWeaponStat(weapon, row, box.Text ?? "");
    }

    // ------------------------------------------------------------------ An ability's tiers

    /// <summary>
    /// One number of one tier, ON LOSING FOCUS. Moves the number everywhere that names it, which
    /// is what the box means: the other act has its own button beside it.
    /// </summary>
    private void OnSetProgression(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SheetTab.ProgRow row } box &&
            OwnerOf(row) is { } ability)
            _vm.SetProgression(ability, row, box.Text ?? "", ownCopy: false);
    }

    /// <summary>
    /// GIVE THIS TIER ITS OWN COPY of the asset holding a shared number, and put the typed value
    /// on the copy — so the other tiers keep theirs.
    ///
    /// Three project entries an author would otherwise have to know to write by hand: a copy, a
    /// property added to it, and a class-valued property on the ability pointing at it.
    /// </summary>
    private void OnProgressionOwnCopy(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SheetTab.ProgRow row ||
            OwnerOf(row) is not { } ability) return;

        _vm.SetProgression(ability, row, row.Text, ownCopy: true);
    }

    private void OnClearProgression(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.ProgRow row &&
            OwnerOf(row) is { } ability)
            _vm.ClearProgression(ability, row);
    }

    // ------------------------------------------------------------------ A specialization

    /// <summary>
    /// Pick a crest out of the game's image banks.
    ///
    /// WITH PICTURES, because a crest is a picture and its tag is not one — the tag is what the
    /// game wants and the worst possible thing to ask a person to know. The bank entry carries
    /// the texture it draws, so the grid shows what each tag actually looks like.
    /// </summary>
    /// <summary>
    /// "From the game's icons": a weapon's armoury picture, chosen out of the game's own banks.
    ///
    /// The Weapon family only. A crest in an armoury slot is drawn at the wrong shape — the
    /// weapon icons are 512x256 and nearly everything else is square — and offering all 497 would
    /// make choosing the right one an exercise in knowing which family a tag belongs to.
    /// </summary>
    private async void OnPickBankIcon(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not WeaponTab wt || wt.Copy is not { } copy) return;

        var icons = await _vm.BankIconsAsync(UEToolkit.Core.ImageBanks.WeaponFamily);
        if (icons.Count == 0)
        { _vm.Say("the game's image banks would not read, so there are no icons to offer"); return; }

        var picker = new PickAssetWindow(
            "An armoury picture for this weapon",
            "A weapon names its icon by TAG out of a shared image bank rather than by asset " +
            "path. The picture beside each is what it draws. Choosing its template's own takes " +
            "your change back out rather than storing one that changes nothing.",
            icons, images: true);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } tag) _vm.SetBankIcon(wt, copy, tag);
    }

    private async void OnPickCrest(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not SpecTab spec) return;

        var crests = await _vm.CrestsAsync();
        if (crests.Count == 0)
        { _vm.Say("the game's image banks would not read, so there are no crests to offer"); return; }

        var picker = new PickAssetWindow(
            "A crest for this specialization",
            "A specialization names its icon by TAG out of an image bank rather than by asset " +
            "path. Any of these will do; the picture beside each is what it draws.",
            crests, images: true);

        await picker.ShowDialog(this);

        if (picker.Chosen is { } tag)
        {
            spec.EditCrest = tag;
            _vm.SaveSpecEdit(spec);
        }
    }

    private async void OnChangeGrant(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SheetTab.GrantRow row ||
            _vm.Tab is not SpecTab spec) return;

        if (await PickGranted(row.IsGear, row.Leafed) is { } path)
            _vm.SetGrant(spec, row, path, row.IsGear);
    }

    private void OnDropGrant(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.GrantRow row && _vm.Tab is SpecTab spec)
            _vm.SetGrant(spec, row, "", row.IsGear);
    }

    private async void OnAddAbilityGrant(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is SpecTab spec && await PickGranted(gear: false, "") is { } path)
            _vm.SetGrant(spec, null, path, gear: false);
    }

    private async void OnAddGearGrant(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is SpecTab spec && await PickGranted(gear: true, "") is { } path)
            _vm.SetGrant(spec, null, path, gear: true);
    }

    /// <summary>One ability or gear kit, out of the game's own index of them.</summary>
    private async Task<string?> PickGranted(bool gear, string today)
    {
        var picker = new PickAssetWindow(
            gear ? "A gear kit to grant" : "An ability to grant",
            today.Length > 0
                ? $"It grants {today} today."
                : gear ? "Starting equipment, handed over without spending focus."
                       : "Granted from the start, without spending focus.",
            _vm.Granted(gear),
            images: false);

        await picker.ShowDialog(this);
        return picker.Chosen;
    }

    private async void OnChangeRung(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SheetTab.RungRow row ||
            _vm.Tab is not SpecTab spec) return;

        if (await PickGranted(gear: false, row.AbilityLeaf) is { } path)
            _vm.SetRungAbility(spec, row, path);
    }

    /// <summary>
    /// One of a rung's three numbers, ON LOSING FOCUS. Which one is on the box's Tag, so three
    /// identical controls need one handler rather than three that can drift.
    /// </summary>
    private void OnSetRung(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SheetTab.RungRow row, Tag: string which } box &&
            OwnerOf(row) is { } spec)
            _vm.SetRung(spec, row, which, box.Text ?? "");
    }

    private void OnOwnType(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is WeaponTab weapon) _vm.SetWeaponFits(weapon, ownType: true);
    }

    private void OnDonorType(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is WeaponTab weapon) _vm.SetWeaponFits(weapon, ownType: false);
    }

    private void OnOwnAttachment(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.AttachRow row &&
            _vm.Tab is WeaponTab weapon)
            _vm.SetWeaponAttachment(weapon, row, own: true);
    }

    private void OnDropAttachment(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.AttachRow row &&
            _vm.Tab is WeaponTab weapon)
            _vm.SetWeaponAttachment(weapon, row, own: false);
    }

    /// <summary>Add the pieces a duplicated weapon's model is missing.</summary>
    private void OnCompleteWeaponModel(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is WeaponTab weapon) _vm.CompleteWeaponModel(weapon);
    }

    /// <summary>Open this row's mesh in the 3D viewport — the game's, or the author's import.</summary>
    private async void OnViewMeshRow(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) await _vm.ViewMeshRowAsync(row);
    }

    /// <summary>
    /// Point one of a part's meshes at another of the game's own.
    ///
    /// SKELETAL MESHES ONLY in the list. A part wearing a static mesh would name one, and the
    /// rows here come from PartDetails.Meshes which is the skinned kind — offering the other
    /// would be offering a choice the build then refuses.
    /// </summary>
    private async void OnPickMeshRow(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || _vm.Tab is not PartTab tab) return;

        var picker = new PickAssetWindow(
            $"A mesh for {row.Variant}",
            $"It wears {row.GameLeaf} today. Choosing another of the game's points this part at " +
            "it — the game's own mesh is left alone, and nothing new ships.",
            _vm.GameAssets(AssetKind.SkeletalMesh),
            // A MESH HAS NO PICTURE, so this one stays a name list. The grid would be a wall of
            // empty tiles, which says less than the names do.
            images: false);

        await picker.ShowDialog(this);
        if (picker.Chosen is { } package) _vm.PickMeshRow(tab, row, package);
    }

    // ------------------------------------------------------------------ Models

    /// <summary>
    /// Export the model on screen, then — if an animation is loaded — the animation, separately.
    ///
    /// TWO DIALOGS, which is what he asked for and is also what the formats force: FBX is MESHES
    /// ONLY by AssetExporter's own statement, so an animation is an ActorX .psa or a UEFormat
    /// .ueanim and cannot ride inside the model's file. The second dialog only appears when
    /// there is something to put in it.
    /// </summary>
    private async void OnExportModel(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not MeshTab tab || tab.Asset is null) return;

        var model = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {tab.Title}",
            SuggestedFileName = tab.Title,
            DefaultExtension = "fbx",
            FileTypeChoices =
            [
                new FilePickerFileType("Autodesk FBX") { Patterns = ["*.fbx"] },
                new FilePickerFileType("ActorX PSK") { Patterns = ["*.psk", "*.pskx"] },
                new FilePickerFileType("glTF 2.0") { Patterns = ["*.glb"] },
                new FilePickerFileType("UEFormat") { Patterns = ["*.uemodel"] },
            ],
        });

        if (model?.TryGetLocalPath() is not { } modelPath) return;
        if (!_vm.ExportModel(tab, modelPath)) return;

        if (tab.PlayingAnim is null) return;

        var anim = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export the animation — {tab.PlayingAnim.Name}",
            SuggestedFileName = tab.PlayingAnim.Name,
            DefaultExtension = "psa",
            // NO FBX HERE. The exporter writes animation as ActorX or UEFormat; listing a format
            // that cannot be written is a dialog that promises a file it will not produce.
            FileTypeChoices =
            [
                new FilePickerFileType("ActorX PSA") { Patterns = ["*.psa"] },
                new FilePickerFileType("UEFormat") { Patterns = ["*.ueanim"] },
            ],
        });

        if (anim?.TryGetLocalPath() is { } animPath) _vm.ExportAnimation(tab, animPath);
    }

    /// <summary>Import geometry over the game's own mesh.</summary>
    private async void OnImportModel(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not MeshTab tab) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Geometry to use instead of {tab.Title}",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Models") { Patterns = ["*.fbx"] }],
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is { } path) _vm.ImportModel(tab, path);
    }

    // ------------------------------------------------------------------ Textures

    /// <summary>
    /// Import an image over the texture the Data Explorer has selected.
    ///
    /// OPENS IT FIRST. Replacing artwork without having looked at what is being replaced is how
    /// the wrong texture gets overwritten, and the tab is where the channels and the shipped size
    /// are — so this is the same two steps somebody would take by hand, in one menu item.
    /// </summary>
    private async void OnImportOverAsset(object? sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } row) return;
        await _vm.OpenAssetAsync(row);
        if (_vm.Tab is not TextureTab tab)
        { _vm.Say($"{row.Name} is not a texture — import is for textures"); return; }
        await ImportOver(tab);
    }

    /// <summary>Undo whatever this mod does to the selected asset.</summary>
    private void OnRevertAsset(object? sender, RoutedEventArgs e)
    {
        if (_vm.Selected is { } row) _vm.RevertAsset(row);
    }

    /// <summary>
    /// Take one of the mod's own things back out of it.
    ///
    /// THE PLAN IS BUILT ONCE and both the dialog and the removal use it, so the sentence
    /// somebody reads and the thing that happens cannot describe different objects.
    ///
    /// A GAME ROW IS NOT AN ERROR, it is the other menu item. Saying which one reaches it beats
    /// a command that does nothing on most rows and works on some, which is how people learn a
    /// menu is broken.
    /// </summary>
    private async void OnRemoveFromMod(object? sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } row) return;

        if (_vm.WhatRemoving(row.Payload) is not { } plan)
        {
            _vm.Say($"{row.Name} is the game's own, not something this mod adds — " +
                    "Revert is what takes your changes off it");
            return;
        }

        if (await ConfirmWindow.AskAsync(this, plan.Heading, plan.Detail, plan.Warn,
                                         plan.Aside, plan.Button))
            _vm.RemoveFromMod(plan);
        else
            _vm.Say("nothing was removed");
    }


    /// <summary>
    /// Export the open texture: pick the folder, the format is already chosen in the bar.
    ///
    /// A FOLDER, not a file name. TextureExport mirrors the game's folder structure underneath
    /// it, so the export of a dozen textures keeps the names the game gave them apart instead of
    /// asking the author to invent one each time.
    /// </summary>
    private async void OnExportTexture(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not TextureTab tab) return;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Where to export {tab.Title}",
            AllowMultiple = false,
        });

        if (picked.Count == 0) return;                       // cancelled means nothing happened
        if (picked[0].TryGetLocalPath() is not { } dir) return;

        _vm.ExportTexture(tab, dir);
    }

    /// <summary>
    /// Import an image OVER this game texture, which makes the package a modified file of the
    /// open mod.
    /// </summary>
    private async void OnImportTexture(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is TextureTab tab) await ImportOver(tab);
    }

    /// <summary>The picker and the import, shared by the toolbar button and the right-click.</summary>
    private async Task ImportOver(TextureTab tab)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"An image to use instead of {tab.Title}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.tga", "*.jpg", "*.jpeg", "*.dds"] },
            },
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is not { } path) return;

        _vm.ImportTexture(tab, path);
    }

    private async Task PickArt(string kind, string title, string what, string[] patterns,
                               Action<PartTab, string> onto)
    {
        if (_vm.Tab is not PartTab tab) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(what) { Patterns = patterns },
            },
        });

        if (files.Count == 0) return;                        // cancelled means nothing happened
        if (files[0].TryGetLocalPath() is not { } path) return;

        if (_vm.ImportArt(path, kind, out var why) is { } name) onto(tab, name);
        else _vm.Say($"could not import {System.IO.Path.GetFileName(path)}: {why}");
    }

    private void OnSavePartEdit(object? sender, RoutedEventArgs e) => SaveEditOn();

    /// <summary>Save, by what kind of tab is open -- the button's own dispatch.</summary>
    private void SaveEditOn()
    {
        if (_vm.Tab is PartTab part) _vm.SavePartEdit(part);
        else if (_vm.Tab is WeaponTab weapon) _vm.SaveWeaponEdit(weapon);
        else if (_vm.Tab is SpecTab spec) _vm.SaveSpecEdit(spec);
        else if (_vm.Tab is ModTab details) _vm.SaveModDetails(details);
        else if (_vm.Tab is OverrideTab rewrite) _vm.SaveOverrideEdit(rewrite);
        // A MESH COPY HAS NOTHING TO SAVE. Its geometry and its paint are written as they
        // happen, as every import here is, so a Save that reported success would be reporting
        // work that was already on disk before the button existed.
        else if (_vm.Tab is CopyTab made) _vm.SaveCopyEdit(made);
        else if (_vm.Tab is MissionTab { Mine: not null } mission) _vm.SaveMissionEdit(mission);
        // AN ABILITY'S AND A TABLE'S NUMBERS ARE WRITTEN WHEN THE BOX IS LEFT, so a press here has
        // nothing to save. Saying so beats a button that does nothing and looks broken -- and
        // says WHEN, because a number still being typed in a box has not been recorded yet.
        else if (_vm.Tab is AbilityTab or TableTab or ModificationTab)
            _vm.Say("these changes are written into the project when you leave a box - nothing is waiting to be saved");
    }

    private void OnStopPartEdit(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is SheetTab sheet) sheet.EndEdit();
    }

    /// <summary>Everything typed in this sitting put back, without leaving the editor.</summary>
    private void OnUndoPartEdit(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is SheetTab sheet) sheet.UndoEdit();
    }

    /// <summary>The read sheet folded away from the header, and back.</summary>
    private void OnToggleEditSheet(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is SheetTab sheet) sheet.ToggleSheet();
    }

    /// <summary>Fold one section's card. The head is the button, so the whole width works.</summary>
    private void OnToggleCard(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is SheetTab.EditSection s) s.Open = !s.Open;
    }

    /// <summary>
    /// A line of the rail: light it, and scroll its card into view.
    ///
    /// THE CARD IS FOUND BY ITS TAG, not by a generated field. These cards live inside a
    /// DataTemplate, so they are not in the window's namescope and FindControl cannot see them —
    /// the same trap MainWindow's own name fields carry a comment about. Walking the visual tree
    /// for the Tag works wherever the template happens to have been instantiated.
    /// </summary>
    private void OnEditRail(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control rail || rail.DataContext is not SheetTab.EditSection s) return;
        if (_vm.Tab is SheetTab sheet) sheet.SelectSection(s);

        var card = this.GetVisualDescendants()
                       .OfType<Border>()
                       .FirstOrDefault(b => b.Classes.Contains("card") && (b.Tag as string) == s.Key);
        card?.BringIntoView();
    }

    // ------------------------------------------------------------------ Deeper changes

    /// <summary>Take one re-point or rename back off this part.</summary>
    private void OnDropDeepRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SheetTab.DeepRow row && _vm.Tab is PartTab tab)
            _vm.DropDeepRow(tab, row);
    }

    /// <summary>Rewrite a name everywhere in this part.</summary>
    private void OnAddRename(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is PartTab tab) _vm.AddRename(tab, tab.RenameFrom, tab.RenameTo);
    }

    /// <summary>Put one mesh row back to the game's own geometry.</summary>
    private void OnRevertMeshRow(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) RevertMeshRowOn(row);
    }

    /// <summary>Revert on a mesh row, by what kind of tab it sits on -- the button's own dispatch.</summary>
    private void RevertMeshRowOn(SheetTab.MeshRow row)
    {
        if (_vm.Tab is WeaponTab weapon) _vm.RevertWeaponPiece(weapon, row);
        else if (_vm.Tab is ReplacementTab own) _vm.RevertReplacementRow(own, row);
        else if (_vm.Tab is OverrideTab rewrite) _vm.RevertOverrideMeshRow(rewrite, row);
        else if (_vm.Tab is PartTab tab) _vm.RevertMeshRow(tab, row);
    }

    // ------------------------------------------------------------------ A part's textures

    private static SheetTab.TextureRow? TexOf(object? sender) =>
        (sender as Control)?.DataContext as SheetTab.TextureRow;

    private static SheetTab.MaterialGroup? MatOf(object? sender) =>
        (sender as Control)?.DataContext as SheetTab.MaterialGroup;

    /// <summary>
    /// Build the mod, read it back, and if it is clean put it in the game.
    ///
    /// The log is where it reports: the build lines, the read-back's findings and the install all
    /// land there in the order they happened, so there is one account of what was done rather
    /// than a bar and a log that can disagree about it.
    /// </summary>
    private async void OnBuildAndInstall(object? sender, RoutedEventArgs e)
    {
        ShowLog();
        await _vm.BuildAndInstallAsync();
    }

    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        ShowLog();
        await _vm.UninstallAsync();
    }

    /// <summary>
    /// "Zip it for sharing". The log is brought forward because that is where the packing lines
    /// and the full path of the zip go; the one-line verdict is also on the build bar.
    /// </summary>
    private async void OnShare(object? sender, RoutedEventArgs e)
    {
        ShowLog();
        await _vm.ShareAsync();
    }

    /// <summary>Plugins > Add-ons and presets…: the Blender add-on's installer and the two presets.</summary>
    private async void OnOpenPlugins(object? sender, RoutedEventArgs e)
    {
        var plugins = new PluginsWindow(_vm);
#if DEV
        PluginsHook?.Invoke(plugins);
#endif
        await plugins.ShowDialog(this);

        // What the window installed or removed may change whether the notice is still true.
        _vm.CheckAddon();
    }

#if DEV
    /// <summary>Developer flags --pluginssave / --pluginsbuttons: what to do to the window once it exists.</summary>
    private static Action<PluginsWindow>? PluginsHook;
#endif

    /// <summary>The notice's button: the ONE click that updates the Blender add-on(s).</summary>
    private void OnUpdateAddon(object? sender, RoutedEventArgs e)
    {
        ShowLog();
        _vm.UpdateAddon();
    }

    private void OnDismissAddon(object? sender, RoutedEventArgs e) => _vm.DismissAddonOffer();

    /// <summary>Bring the log forward, because that is where a build says what it did.</summary>
    private void ShowLog()
    {
        if (this.FindControl<TabControl>("BottomTabs") is { } tabs &&
            this.FindControl<TabItem>("LogTab") is { } log)
            tabs.SelectedItem = log;

        // AND FOLLOW IT DOWN. A build writes several hundred lines and the newest is the one
        // worth reading; a log pinned to its first line after a build is a log that reports the
        // mounting and hides the verdict.
        if (_followLog is null && this.FindControl<ScrollViewer>("LogScroll") is { } scroll)
        {
            _followLog = scroll;
            _vm.Log.CollectionChanged += (_, _) =>
                Dispatcher.UIThread.Post(() => _followLog?.ScrollToEnd(),
                                         DispatcherPriority.Background);
        }

        _followLog?.ScrollToEnd();
    }

    /// <summary>The log's scroller, once it has been found. Subscribed to exactly once.</summary>
    private ScrollViewer? _followLog;

    /// <summary>
    /// Choose one of the GAME's materials for a slot to wear whole.
    ///
    /// The same picker every other choice here uses — a name list rather than thumbnails,
    /// because a material has no picture and the folder it sits in is what identifies it.
    /// </summary>
    private async void OnChooseMaterial(object? sender, RoutedEventArgs e)
    {
        if (MatOf(sender) is not { } group || _vm.Tab is not SheetTab tab) return;

        var picker = new PickAssetWindow(
            $"A material for {group.Title}",
            group.Named
                ? $"It wears {group.Material} today. Naming one of the game's materials costs a " +
                  "single package import and copies nothing — the game's asset is untouched."
                : "Naming one of the game's materials makes this slot wear it whole: one package " +
                  "import, no copies, no textures. A piece borrowed from another kit wants this " +
                  "rather than four textures exported and imported back.",
            _vm.GameAssets(AssetKind.Material),
            images: false);

        await picker.ShowDialog(this);

        if (picker.Chosen is { } package) _vm.WearMaterial(tab, group, package);
    }

    private void OnClearMaterial(object? sender, RoutedEventArgs e)
    {
        if (MatOf(sender) is { } group && _vm.Tab is SheetTab tab) _vm.ClearMaterial(tab, group);
    }

    /// <summary>
    /// The tint slot, WRITTEN WHEN THE BOX LOSES FOCUS rather than per keystroke.
    ///
    /// Typing a name one letter at a time would save the project on every character and make an
    /// eight-letter name eight entries in the undo history.
    /// </summary>
    private void OnMaterialSlotName(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SheetTab.MaterialGroup group } box &&
            OwnerOf(group) is { } tab)
            _vm.SetMaterialSlotName(tab, group, box.Text ?? "");
    }

    private void OnAddMaterial(object? sender, RoutedEventArgs e)
    {
        // A PART REMEMBERS WHICH PIECE THE PRESS CAME FROM; a mesh copy is one piece and
        // has no such thing. Empty is the right answer there, not a missing button.
        if (_vm.Tab is PartTab part) _vm.AddMaterialSlot(part, part.LastPiece);
        else if (_vm.Tab is SheetTab tab) _vm.AddMaterialSlot(tab, "");
    }

    /// <summary>
    /// Put an image over one texture the part wears.
    ///
    /// The filter is PNG and TGA, which is what TextureImport and VirtualTexture read. DDS is
    /// absent on purpose: CUE4Parse's own exporter labels it Not Implemented, so offering it
    /// would be a file type that fails after the picker rather than in it.
    /// </summary>
    private async void OnImportTextureRow(object? sender, RoutedEventArgs e)
    {
        if (TexOf(sender) is not { } row) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Image for {row.Slot} — replacing {row.GameLeaf}  ({row.Describe})",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.tga"] }],
        });

        if (files.Count == 0) return;
        if (files[0].TryGetLocalPath() is { } path) _vm.ImportTextureRow(row, path);
    }

    /// <summary>
    /// Point a texture — or the menu picture — at another of the game's own.
    ///
    /// ONE HANDLER for both, because both rows are a TextureRow carrying the package that holds
    /// their reference: the material for a texture the mesh wears, the part for the picture.
    /// The difference is data, so it should not be two copies of this.
    /// </summary>
    private async void OnPickTextureRow(object? sender, RoutedEventArgs e)
    {
        // ON ANY SHEET WITH TEXTURE ROWS, not only a part's. The button shows on every row that has a holder, and on a
        // mesh override or a mesh copy it returned here and did nothing; PickTextureRow and ImportTextureRow already
        // know where each kind of tab writes. Only the menu PICTURE is a part's own.
        if (TexOf(sender) is not { } row || _vm.Tab is not SheetTab) return;
        var tab = _vm.Tab as PartTab;

        // WHAT COULD PLAUSIBLY GO HERE, ticked by default — the old studio's "Legs only".
        //
        // The menu picture wants the game's part THUMBNAILS, not nine thousand textures
        // including the galaxy map's UI atlas. A texture the mesh wears wants one of the same
        // SHAPE, because anything else is silently resized and on a mask that is destructive.
        var picture = tab is not null && ReferenceEquals(row, tab.IconRow);

        // THE MENU PICTURE ASKS THE CATALOGUE, not the asset index: pictures parts actually
        // wear, and the tick narrows them to this part's own slot — which is the "Legs only" in
        // his screenshot, and 1,296 icons rather than 9,590 raw textures to begin with.
        var slot = picture ? ModProject.FromSlotTag(tab!.Entry.Part.Slot) : null;
        var icons = picture ? _vm.IconChoices(slot) : default;

        var scopeLabel = picture
            ? (slot is null || icons.InSlot.Count == 0 ? "" : $"{slot} pictures only")
            : row.Width > 0 ? $"{row.Width}×{row.Height} only"
            : "";

        Func<PickAssetWindow.Candidate, bool>? scope =
            picture ? c => icons.InSlot.Contains(c.Package)
            : row.Width > 0 ? c => _vm.ShapeOfPackage(c.Package) == (row.Width, row.Height)
            : null;

        var picker = new PickAssetWindow(
            $"A texture for {row.Slot}",
            $"{Leaf(row.Holder)} points at {row.GameLeaf} today ({row.Describe}). Choosing " +
            "another of the game's moves that reference — the texture itself is left alone, and " +
            "nothing new ships. Importing puts your pixels into it instead.",
            picture ? icons.All : _vm.GameAssets(AssetKind.Texture),
            images: true,
            scopeLabel, scope,
            own: _vm.MyImages().Select(m => new PickAssetWindow.Candidate(
                     m, _vm.PathOfMyImage(m) ?? m, m, _vm.PathOfMyImage(m))),
            import: async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = $"Image for {row.Slot} — replacing {row.GameLeaf}  ({row.Describe})",
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.tga"] }],
                });
                return files.Count == 0 ? null : files[0].TryGetLocalPath();
            });

        await picker.ShowDialog(this);

        // A GAME ASSET MOVES THE REFERENCE; ONE OF YOURS GOES INTO THE TEXTURE. Two different
        // edits from one window, which is what makes it one decision rather than two menus.
        // A COPY'S PICTURE IS A FIELD ON THE PART, not a reference to move, so it takes its own
        // route — one that writes ModPart.Icon rather than a texture entry nothing reads.
        if (picker.Chosen is { } package)
        {
            if (picture && tab!.Copy is not null) _vm.PickPartIcon(tab, package);
            else _vm.PickTextureRow(row, package);
        }
        else if (picker.ChosenOwn is { } file)
        {
            if (picture) _vm.ImportPartIcon(tab!, file);
            else _vm.ImportTextureRow(row, file);
        }
    }

    private static string Leaf(string path)
    {
        var cut = path.LastIndexOfAny(['/', '.']);
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    /// <summary>Open this row's texture in the 2D viewer, where the channels come apart.</summary>
    private async void OnViewTextureRow(object? sender, RoutedEventArgs e)
    {
        if (TexOf(sender) is { } row) await _vm.ViewTextureRowAsync(row);
    }

    private void OnRevertTextureRow(object? sender, RoutedEventArgs e)
    {
        if (TexOf(sender) is { } row) _vm.RevertTextureRow(row);
    }

    /// <summary>
    /// The list of images this mod already holds, hung off the button that asked for it.
    ///
    /// BUILT IN CODE rather than bound. The list belongs to the MOD and the button lives inside
    /// a row's template, so binding it would be a walk up through two data contexts to reach a
    /// collection that changes on every import — where a menu built at the moment of the click
    /// is always current and reads as what it is.
    /// </summary>
    /// <summary>
    /// WHAT THE "Use…" MENU OFFERS, as the lines it will show.
    ///
    /// EACH ONE'S SHAPE, AND WHETHER IT MATCHES. The importer RESIZES rather than refusing — a
    /// 256 thumbnail dropped on a 2,048 diffuse is accepted and scaled, which this project has
    /// already recorded as how a mask got erased. The menu is the last moment that can be seen,
    /// so it says the size and marks what does not fit rather than letting the answer arrive as
    /// a blurred helmet with nothing anywhere saying why.
    ///
    /// SEPARATE FROM THE MENU ITSELF because a flyout is its own window: PrintWindow cannot
    /// photograph one, so this is the only way to check what it offers. One source of truth for
    /// the lines, read by both the menu and --usemenu.
    /// </summary>
    private List<(string Header, string Leaf)> ImageMenuLabels(SheetTab.TextureRow target)
    {
        var made = new List<(string, string)>();

        foreach (var leaf in _vm.MyImages())
        {
            var shape = _vm.ShapeOf(leaf);
            var header = shape is null ? leaf : $"{leaf}      {shape.Value.W}×{shape.Value.H}";
            if (shape is { } s && target.Width > 0 && (s.W != target.Width || s.H != target.Height))
                header += $"   — will be resized to {target.Width}×{target.Height}";
            made.Add((header, leaf));
        }

        return made;
    }

    private void ShowImageMenu(object? sender, SheetTab.TextureRow target,
                               Action<string> chose, Action? clear, string clearText)
    {
        if (sender is not Control at) return;

        var mine = _vm.MyImages();
        var menu = new MenuFlyout();

        if (mine.Count == 0)
            menu.Items.Add(new MenuItem { Header = "nothing imported into this mod yet", IsEnabled = false });

        foreach (var (header, leaf) in ImageMenuLabels(target))
        {
            var item = new MenuItem { Header = header };
            var name = leaf;
            item.Click += (_, _) => chose(name);
            menu.Items.Add(item);
        }

        if (clear is not null)
        {
            menu.Items.Add(new Separator());
            var back = new MenuItem { Header = clearText };
            back.Click += (_, _) => clear();
            menu.Items.Add(back);
        }

        menu.ShowAt(at);
    }

    private void OnUseTextureRow(object? sender, RoutedEventArgs e)
    {
        if (TexOf(sender) is not { } row) return;
        ShowImageMenu(sender, row,
                      leaf => _vm.UseTextureRow(row, leaf),
                      row.Mine ? () => _vm.RevertTextureRow(row) : null,
                      "Use the game's own");
    }

    private void OnUsePartIcon(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is not PartTab tab || tab.IconRow is null) return;
        ShowImageMenu(sender, tab.IconRow,
                      leaf => _vm.UsePartIcon(tab, leaf),
                      tab.IconRow.Mine ? () => _vm.RevertPartIcon(tab) : null,
                      "Use the game's own");
    }

    /// <summary>Open or shut the raw tag list at the foot of a part's sheet.</summary>
    private void OnToggleTags(object? sender, RoutedEventArgs e)
    {
        if (_vm.Tab is PartTab part) part.ToggleTags();
    }

    /// <summary>Open or shut one category in the section list.</summary>
    private void OnToggleGroup(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SectionItem h) _vm.ToggleGroup(h);
    }

    /// <summary>
    /// Open one of the two guides, preferring the copy that shipped beside the executable.
    ///
    /// THE SYSTEM BROWSER, not a reader of our own. The ImGui Studio hosts a WinForms window with
    /// a browser control in it and <see cref="UEToolkit.Core.Branding.GuideReader"/> exists so
    /// that the shared menu can use one where there is one. This application has no such window
    /// and does not want one for two static HTML pages: whatever opens .html on this machine is
    /// already a better reader than anything worth building here.
    ///
    /// The published copy is the fallback rather than the default, because a local file is never
    /// newer than the build it shipped with but it does work with the network off.
    /// </summary>
    private void OnOpenGuide(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string file) return;

        var local = Path.Combine(AppContext.BaseDirectory, "Guides", file);
        var have = File.Exists(local);
        // The published address comes from Branding (Program.cs), so it is written in one place;
        // the literal that used to sit here is Branding.Landing + the file's name.
        var guide = Branding.Guides.FirstOrDefault(
            g => g.Url.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase));
        var open = have
            ? local
            : guide?.Url ?? Branding.Landing + file;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(open) { UseShellExecute = true });
            _vm.Say(have ? $"handbook: opened {file}"
                         : $"handbook: no local copy of {file}, opened the published one");
        }
        catch (Exception ex)
        {
            _vm.SayBad($"handbook: could not open {open} — {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ About, and the update notice

    /// <summary>
    /// About > About Zero Company Mod Studio…, and a click on the credit at the right of the menu
    /// bar: one window, two ways in. Modal, like every other dialog here.
    /// </summary>
    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow(_vm.ExportSettings, _vm.Say);
#if DEV
        AboutHook?.Invoke(about);
#endif
        await about.ShowDialog(this);
    }

#if DEV
    /// <summary>Developer flag --aboutcheck: what to do to the About window once it exists.</summary>
    private static Action<AboutWindow>? AboutHook;
#endif

    /// <summary>About > Downloads and updates: the releases page, in the browser.</summary>
    private void OnOpenDownloads(object? sender, RoutedEventArgs e)
    {
        if (Branding.OpenLink(Branding.Home) is { } why)
            _vm.Say($"could not open {Branding.Home}: {why}");
    }

    /// <summary>
    /// The menu bar's notice: open WHAT CHANGED, not the download page. It used to go straight to
    /// the browser, which asks somebody to decide whether to update before telling them what
    /// updating would get them. The download is one button further on.
    /// </summary>
    private async void OnUpdateNotice(object? sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Found is not { } release) return;
        var notes = new WhatsNewWindow(release, _vm.Say);
        await notes.ShowDialog(this);
    }

    /// <summary>About > Get version X: straight to the release page.</summary>
    private void OnGetUpdate(object? sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Found is not { } release) return;
        if (UpdateNotice.OpenWebLink(release.Url) is { } why)
            _vm.Say($"could not open {release.Url}: {why}");
    }

    /// <summary>
    /// Extract everything at or below the selected folder, formats chosen first.
    ///
    /// THE COUNT IS THE WHOLE DECISION, so the window shows it before offering the button: these
    /// folders run from four assets to fourteen thousand and the difference is seconds against
    /// hours. <see cref="GameNode.AllAssets"/> already answers "everything beneath this", filter
    /// and payload files honoured, so what is offered is exactly what the explorer shows.
    /// </summary>
    private async void OnExtractFolder(object? sender, RoutedEventArgs e)
    {
        if (_vm.Folder is not { } folder) { _vm.Say("extract: pick a folder in the tree first"); return; }
        if (_vm.Provider is not { } provider) { _vm.Say("extract: the game is not open yet"); return; }

        var files = folder.AllAssets.ToList();
        if (files.Count == 0)
        { _vm.Say($"extract: {folder.FullPath} holds nothing to extract"); return; }

        var win = new ExtractWindow(folder.FullPath, files, provider, _vm.ExportSettings, _vm.Say);
        await win.ShowDialog(this);
    }

    private async void OnPlaySound(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SoundTab tab) await _vm.PlaySound(tab);
    }

    private void OnStopSound(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SoundTab tab) _vm.StopSound(tab);
    }

    /// <summary>
    /// Keep the sound that is playing. A SAVE picker rather than a folder one, because it is one
    /// file and its name is worth being able to change: the game's own is a Wwise event name.
    /// </summary>
    private async void OnExtractSound(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SoundTab tab) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Keep this sound",
            SuggestedFileName = tab.Entry.Name + ".wav",
            DefaultExtension = "wav",
            FileTypeChoices = new[] { new FilePickerFileType("Wave audio") { Patterns = new[] { "*.wav" } } },
        });
        if (file?.TryGetLocalPath() is { Length: > 0 } path) _vm.ExtractSound(tab, path);
    }

    private void OnCollapseGroups(object? sender, RoutedEventArgs e) => _vm.AllGroups(false);
    private void OnExpandGroups(object? sender, RoutedEventArgs e) => _vm.AllGroups(true);

    private void OnPickTab(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StudioTab tab) _vm.Tab = tab;
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is StudioTab tab) _vm.CloseTab(tab);
    }

    /// <summary>
    /// Open or shut one property row.
    ///
    /// A BUTTON RATHER THAN A TreeView, because the panel shows values in a column of their own
    /// and a TreeView indents the entire row — every level of nesting would drag the value right
    /// and the panel would read as a staircase instead of a table.
    /// </summary>
    private void OnTogglePropRow(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is PropRow row) row.IsExpanded = !row.IsExpanded;
    }

    // ------------------------------------------------------------------ Editing a property

    /// <summary>
    /// Turn a value cell into a box.
    ///
    /// Reached from a double click on the value, where the sender IS the row, and from the
    /// context menu, where the sender is a menu item whose DataContext is the LIST's — so the row
    /// is read off the tab's own selection in that case. One method either way, because two would
    /// eventually disagree about which row was meant.
    /// </summary>
    private void OnBeginPropEdit(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        if (!row.Editable)
        {
            _vm.Say($"{row.Name} is not a number sitting directly on an export — see the log note above");
            return;
        }
        row.BeginEdit();
    }

    private void OnRevertProp(object? sender, RoutedEventArgs e)
    {
        if (_vm.AssetTabOpen is { } tab && Row(sender) is { } row) _vm.RevertEdit(tab, row);
    }

    /// <summary>
    /// Enter commits, Escape cancels. The box stays open when the edit is REFUSED, so the number
    /// somebody typed is still there to correct rather than gone along with the reason.
    /// </summary>
    private void OnPropEditKey(object? sender, KeyEventArgs e)
    {
        if (Row(sender) is not { } row) return;

        if (e.Key == Key.Escape) { row.Editing = false; e.Handled = true; return; }
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (_vm.AssetTabOpen is { } tab && _vm.ApplyEdit(tab, row, row.Draft)) row.Editing = false;
    }

    /// <summary>
    /// A bool's checkbox. The click is what writes, not the binding.
    ///
    /// Two-way binding to a property that calls back into the view model would fire on every
    /// rebuild of the list, which is every filter keystroke -- and each one would look like an
    /// edit. The box shows state; the click makes the change.
    /// </summary>
    private void OnPropBoolToggled(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row || _vm.AssetTabOpen is not { } tab) return;

        // WHAT IT WAS, not what the box now shows: Avalonia has already flipped it.
        var now = row.Was != 0 ? "0" : "1";
        if (row.Edited) now = row.Ticked ? "1" : "0";

        if (!_vm.ApplyEdit(tab, row, now) && sender is CheckBox box)
            box.IsChecked = row.Ticked;   // refused, so put the box back
    }

    /// <summary>
    /// An enum's dropdown. Like the checkbox, the SELECTION is what writes.
    ///
    /// The member is turned back into its number before anything is recorded, so what lands in
    /// the project is the same ModOverride.NumberChange a typed box would have produced and the
    /// build needs to know nothing new.
    /// </summary>
    private void OnPropEnumChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not string member) return;
        if (Row(sender) is not { } row || _vm.AssetTabOpen is not { } tab) return;

        // A LIST THAT IS FILLING ITSELF IS NOT A CHOICE. Setting SelectedItem to show the file's
        // current member raises this too, and treating that as an edit would record a change
        // nobody made on every asset that has an enum in it.
        if (string.Equals(member, row.Chosen, StringComparison.OrdinalIgnoreCase)) return;

        if (row.ValueOf(member) is not { } value)
        { _vm.Say($"{row.Name}: {member} is not a member of {row.Node.Detail}"); return; }

        if (!_vm.ApplyEdit(tab, row, value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            box.SelectedItem = row.Chosen;   // refused, so put the list back
    }

    private async void OnFollowProp(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) await _vm.FollowAsync(row);
    }

    /// <summary>
    /// The + on an array of soft references: choose an asset, and the entry is recorded.
    ///
    /// The picker is opened from here rather than the view model because showing a dialog needs
    /// a window, which is this class's business and not the model's.
    /// </summary>
    private async void OnAddRefEntry(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { IsRefArray: true } row) return;
        if (_vm.AssetTabOpen is not { } tab) return;

        var (candidates, note) = _vm.RefCandidatesFor(tab, row);
        if (candidates.Count == 0) { _vm.Say($"nothing to offer for {row.Name}"); return; }

        var picker = new PickAssetWindow($"Add to {row.Label}", note, candidates, images: false);
        await picker.ShowDialog(this);
        if (picker.Chosen is { } package) _vm.AddRefEntry(tab, row, package);
    }

    /// <summary>The + on a struct array: copy its last element.</summary>
    private void OnDupStructEntry(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { IsStructArray: true } array) return;
        if (_vm.AssetTabOpen is { } tab) _vm.DupStructEntry(tab, array);
    }

    /// <summary>The × on one element of a struct array: drop it.</summary>
    private void OnDropStructEntry(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { IsStructEntry: true } entry) return;
        if (_vm.AssetTabOpen is { } tab) _vm.DropStructEntry(tab, entry);
    }

    /// <summary>The × on one entry of such an array: take that entry back out.</summary>
    private void OnDropRefEntry(object? sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { IsRefEntry: true } entry) return;
        if (_vm.AssetTabOpen is { } tab) _vm.DropRefEntry(tab, entry);
    }

    /// <summary>The row a handler is about: the sender's own, or the panel's selection.</summary>
    private PropRow? Row(object? sender)
        => (sender as Control)?.DataContext as PropRow ?? _vm.AssetTabOpen?.SelectedRow;

    // ------------------------------------------------------------------ Project

    private async void OnOpenProjects(object? sender, RoutedEventArgs e)
    {
        var picker = new ProjectsWindow();   // opens on the saved mods folder
        await picker.ShowDialog(this);

        if (picker.WantsNew) { await NewMod(); return; }
        if (picker.Chosen is { } path) { _vm.OpenMod(path); Retitle(); }
    }

    /// <summary>
    /// Project > Find the game again…: the same question the first run asks, with what is in use
    /// already chosen, for when the game moved, was never found, or the wrong install was picked.
    /// "Delete settings\install.json" does NOT do this - the profile saved beside it is reused
    /// without asking - so this is the one real way to change the answer.
    ///
    /// WHAT HAPPENS AFTER "Use this install" DEPENDS ON WHETHER A GAME IS MOUNTED (measured, then
    /// chosen):
    ///   * NOTHING MOUNTED (opened without the game, or the mount failed): there is nothing to tear
    ///     down, so the new answer is mounted straight away. This is the recovery path.
    ///   * A GAME MOUNTED: the Studio says plainly that it must be opened again. A live swap is not
    ///     safe - the mount is held by some 200 references across 24 files (the explorer, every
    ///     open tab, the viewport, the icon and texture caches, the animation and tint libraries, the
    ///     character catalogue), the provider keeps the old install's containers open, and a person
    ///     may be part-way through an edit that points at assets of the install being dropped.
    ///     Quietly mixing two installs in one session is exactly the wrong-values failure this tool
    ///     exists to prevent, so it restarts instead.
    /// Either way both files are written the same way as at the first run, so the NEXT start - and
    /// the Blender add-on and the command line - use the new answer.
    /// </summary>
#if DEV
    /// <summary>Developer flag --findgame: what to do to the question window once it exists.</summary>
    private static Action<GameFolderWindow>? FindGameHook;
#endif

    private async void OnFindGame(object? sender, RoutedEventArgs e)
    {
        try
        {
            var mounted = _vm.GameInUse;
            var setup = GameSetup.Resolve(find: StudioVm.Finder);

            // What is in use: the mounted install, else whatever the settings say now. What is
            // offered: Steam's list, with the one in use first even if the list does not know it.
            var current = _vm.GameInstallInUse ?? (setup.Ready ? setup.Install : null);
            var offered = setup.NeedsAnswer ? setup.Candidates.ToList()
                                            : GameSetup.Installs(StudioVm.Finder, out _);

            var window = new GameFolderWindow(new GameAsk(offered)
            {
                Selected = current,
                Gone = current is null ? setup.Gone : null,
                Problem = setup.Problem,
                Again = true,
                RestartAfter = mounted,
            });
#if DEV
            FindGameHook?.Invoke(window);
#endif
            await window.ShowDialog(this);

            if (window.Result is not { } answer) { _vm.Say("game: left as it was."); return; }

            _vm.Say(answer.Say);
            if (mounted)
            {
                _vm.Say(string.Equals(answer.Install, current, StringComparison.OrdinalIgnoreCase)
                    ? "game: that is the install already in use - nothing changed."
                    : "game: the new install is used the next time the Studio is opened.");
                return;
            }

            // Nothing was mounted, so mount it now - checking the build first, as at the start.
            if (answer.ProfilePath is { } profile) await GameBuildWindow.RunAsync(profile, this);
            await _vm.LoadGameAsync();
        }
        catch (Exception ex)
        {
            _vm.SayBad($"game: could not change the install - {ex.Message}");
        }
    }

    private async void OnCreateNew(object? sender, RoutedEventArgs e) => await NewMod();

    private void OnUnload(object? sender, RoutedEventArgs e) { _vm.CloseMod(); Retitle(); }

    private async Task NewMod()
    {
        var dialog = new NewModWindow();
        await dialog.ShowDialog(this);
        if (dialog.CreatedPath is not { } made) { _vm.Say("Create New Mod — cancelled."); return; }

        _vm.Say($"created {Path.GetFileName(made)}");
        _vm.OpenMod(made);
        Retitle();
    }

    // ------------------------------------------------------------------ Edit

    private void OnUndo(object? sender, RoutedEventArgs e) => _vm.Say("Undo — not wired yet.");
    private void OnRedo(object? sender, RoutedEventArgs e) => _vm.Say("Redo — not wired yet.");
    private void OnRevertAll(object? sender, RoutedEventArgs e) => _vm.Say("Revert all changes — not wired yet.");
}

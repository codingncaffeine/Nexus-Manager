using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Actions;

namespace NexusManager.Editor;

/// <summary>
/// iCUE's macro editor: a numbered list of key events and waits.
///
/// Its own window rather than a card in the Appearance column - that column is
/// 290 units wide and already holds eight sections, a row needs ~500 before the
/// third chip, and a card added to the bottom of a scrolling column is the
/// exact mistake the Visualizers editor had to be moved out of. iCUE does the
/// same thing: the macro editor takes over a surface of its own.
///
/// ⛔ It edits a DEEP COPY and commits on Save (D72). MacroSpec is shared by
/// reference with the ButtonSpec, so editing in place would mean Cancel had
/// already changed the config - and edits autosave, so "already changed" means
/// already written to disk.
///
/// Three deliberate divergences from the screenshot, each because the feature
/// cannot exist here rather than to save work:
///   - no Recording tab. Wayland has no global key capture, which is the same
///     wall that forces uinput to SEND. A tab labelled Recording that cannot
///     record is a lie in the most prominent place on the card.
///   - no Advanced tab. Its documented content is "Retain Original Key
///     Output", which is meaningless for a touch cell: there is no original
///     key output to retain. An empty tab is worse than a missing one.
///   - no mouse events. UinputKeyboard creates a keyboard-only device and the
///     .cuescreens importer already drops them, so a mouse row would offer
///     something the runner cannot execute.
/// </summary>
public sealed class MacroEditorWindow : Window
{
    /// <summary>
    /// The copy/paste buffer, held here rather than in the system clipboard.
    /// A MacroStep has no sensible text form, and round-tripping it through
    /// JSON in the X clipboard would let anything on the desktop paste
    /// arbitrary key sequences into a macro.
    /// </summary>
    private static MacroStep? _clipboard;

    private readonly MacroSpec _macro;
    private readonly ActionRunner _runner;
    private MacroSpec? _result;
    private int _selected = -1;
    private int _tab;
    private bool _building;

    private readonly StackPanel _rows = new();
    private readonly ContentControl _tabHeader = new();
    private readonly ContentControl _tabBody = new();
    /// <summary>Built once each. See ShowTab - rebuilding the Events body gave
    /// the row panel a second visual parent and crashed the application.</summary>
    private Control? _eventsTab;
    private Control? _generalTab;
    private readonly TextBlock _status;
    private readonly Button _test;
    private bool _testing;
    private bool _cancelTest;
    private SystemAction? _testAction;

    private MacroEditorWindow(MacroSpec macro, ActionRunner runner, string buttonName)
    {
        _macro = macro;
        _runner = runner;

        Title = string.IsNullOrWhiteSpace(buttonName) ? "Macro" : $"Macro — {buttonName}";
        Width = 640; Height = 560;
        MinWidth = 640; MaxWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Style.PageBrush;

        _status = new TextBlock
        {
            Foreground = Style.TextDimBrush,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 15,
        };

        _test = new Button
        {
            Content = "Test",
            Padding = new Thickness(14, 4, 14, 4),
            CornerRadius = new CornerRadius(4),
        };
        ToolTip.SetTip(_test, "Runs the macro once, into whatever window has focus");
        _test.Click += async (_, _) => await TestAsync();

        Content = BuildShell();
        ShowTab();
    }

    // ---- shell -------------------------------------------------------------

    private Control BuildShell()
    {
        var title = new TextBlock
        {
            Text = "Macro",
            Foreground = Style.TextBrush,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(title);
        Grid.SetColumn(_tabHeader, 1);
        header.Children.Add(_tabHeader);
        DockPanel.SetDock(header, Dock.Top);

        var testRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                new TextBlock
                {
                    // ⛔ Permanent, not a warning shown afterwards. An
                    // "until pressed again" macro tested from a dialog would
                    // otherwise never stop.
                    Text = "one pass only, whatever Repeat says",
                    Foreground = Style.TextDimBrush,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                _test,
            },
        };
        DockPanel.SetDock(testRow, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);

        var card = new Border
        {
            Background = MacroStyle.CardBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { header, _status, testRow, _tabBody },
            },
        };
        DockPanel.SetDock(card, Dock.Top);

        var save = new Button
        {
            Content = "Save",
            Padding = new Thickness(16, 6, 16, 6),
            Background = Style.AccentBrush,
            Foreground = new SolidColorBrush(Style.Page),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            FontWeight = FontWeight.SemiBold,
            IsDefault = true,
        };
        save.Click += (_, _) => { _result = _macro; Close(); };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => { _result = null; Close(); };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { cancel, save },
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        return new DockPanel
        {
            Margin = new Thickness(16),
            LastChildFill = true,
            Children = { footer, card },
        };
    }

    /// <summary>
    /// Swaps the tab body.
    ///
    /// ⛔ EACH BODY IS BUILT ONCE AND CACHED, and that is not an optimisation.
    /// `_rows` is a field, so rebuilding the Events tab wrapped the SAME
    /// StackPanel in a SECOND ScrollViewer - and a control cannot have two
    /// visual parents. Going Events -> General -> Events therefore killed the
    /// application on the next layout pass:
    ///
    ///   The control StackPanel already has a visual parent
    ///   ScrollContentPresenter ... while trying to add it as a child of
    ///   ScrollContentPresenter ...
    ///
    /// This is the same double-parent crash the Panel tab shipped with once.
    /// Re-parenting a CACHED control back into the same ContentControl is the
    /// normal path and is fine; building a second owner for a field is not.
    /// </summary>
    private void ShowTab()
    {
        _tabHeader.Content = MacroStyle.Segmented(
            ["Events", "General"], _tab, i => { _tab = i; ShowTab(); });
        _tabBody.Content = _tab == 0
            ? _eventsTab ??= BuildEvents()
            : _generalTab ??= BuildGeneral();
        if (_tab == 0) RebuildRows();
    }

    // ---- events tab --------------------------------------------------------

    private Control BuildEvents()
    {
        var well = new Border
        {
            Background = MacroStyle.WellBrush,
            BorderBrush = MacroStyle.WellBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(MacroStyle.Radius),
            Padding = new Thickness(MacroStyle.WellPad),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new ScrollViewer
            {
                Content = _rows,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            },
        };

        var add = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        add.Children.Add(SmallButton("+ Key", "Add a key event after the selected one",
            () => Insert(new MacroStep { Kind = MacroStepKind.Tap, Key = "" })));
        add.Children.Add(SmallButton("+ Delay", "Add a wait after the selected event",
            () => Insert(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = 50 })));
        DockPanel.SetDock(add, Dock.Bottom);

        return new DockPanel { LastChildFill = true, Children = { add, well } };
    }

    private static Button SmallButton(string text, string tip, Action click)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(4),
            FontSize = 12,
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>
    /// Rebuilds every row.
    ///
    /// ⛔ Guarded. Populating controls raises the same events a user edit does,
    /// which has produced two runaway loops in this codebase already; and the
    /// rows are built fresh each time rather than held in fields, so no handler
    /// can stack up across rebuilds holding a stale step.
    /// </summary>
    private void RebuildRows()
    {
        if (_building) return;
        _building = true;
        try
        {
            _rows.Children.Clear();
            if (_macro.Steps.Count == 0)
            {
                _rows.Children.Add(Style.Note(
                    "No events yet. Add a key or a wait below, then click a chip to set it."));
                return;
            }
            for (int i = 0; i < _macro.Steps.Count; i++)
            {
                int index = i;
                var step = _macro.Steps[i];
                _rows.Children.Add(new MacroRow(i + 1, step, i == _selected, new MacroRow.Hooks(
                    Select: () => Select(index),
                    Menu: anchor => ShowRowMenu(anchor, index),
                    Changed: RebuildRows,
                    PickKey: (anchor, s) => KeyPickerFlyout.Show(anchor, s.Key, name =>
                    {
                        s.Key = name;
                        RebuildRows();
                    }))));
            }
        }
        finally { _building = false; }
    }

    private void Select(int index)
    {
        if (index == _selected) return;
        _selected = index;
        RebuildRows();
    }

    /// <summary>
    /// The row menu, verbatim from iCUE including the accelerator column and
    /// the greyed Paste entries.
    ///
    /// ⛔ ONE factory, used by the kebab and by right-click alike. Two menus
    /// built in two places is how an edit path drifts out of step with the one
    /// beside it.
    /// </summary>
    private void ShowRowMenu(Control anchor, int index)
    {
        bool canPaste = _clipboard is not null;
        var menu = Style.Menu(
            new Style.MenuEntry("Add Above", () => InsertAt(index, Like(index))),
            new Style.MenuEntry("Add Below", () => InsertAt(index + 1, Like(index))),
            new Style.MenuEntry("Edit Event", () => EditRow(index)),
            new Style.MenuEntry("Copy Event", () => Copy(index), "Ctrl+C"),
            new Style.MenuEntry("Paste Below", () => Paste(index + 1), "Ctrl+V", canPaste),
            new Style.MenuEntry("Paste Above", () => Paste(index), null, canPaste),
            new Style.MenuEntry("Delete Event", () => Delete(index), "Del"));
        menu.ShowAt(anchor);
    }

    /// <summary>A new step of the same kind as the row it is being added
    /// around: adding above or below a wait means another wait, and around a
    /// key means another key. Inserting a fixed kind would make "Add Below" on
    /// a delay row produce something the user then has no way to convert.</summary>
    private MacroStep Like(int index)
    {
        var at = _macro.Steps.ElementAtOrDefault(index);
        return at?.Kind == MacroStepKind.Delay
            ? new MacroStep { Kind = MacroStepKind.Delay, DelayMs = 50 }
            : new MacroStep { Kind = MacroStepKind.Tap, Key = "" };
    }

    /// <summary>Inserts after the selected row, or appends when nothing is
    /// selected - so a wait can be put between two keys without adding it at
    /// the end and then walking it up the list.</summary>
    private void Insert(MacroStep step) =>
        InsertAt(_selected < 0 ? _macro.Steps.Count : _selected + 1, step);

    private void InsertAt(int index, MacroStep step)
    {
        index = Math.Clamp(index, 0, _macro.Steps.Count);
        _macro.Steps.Insert(index, step);
        _selected = index;
        RebuildRows();
        Status("");
    }

    private void EditRow(int index)
    {
        if (_rows.Children.ElementAtOrDefault(index) is MacroRow row) row.EditValue();
    }

    private void Copy(int index)
    {
        if (_macro.Steps.ElementAtOrDefault(index) is not { } s) return;
        _clipboard = new MacroStep { Kind = s.Kind, Key = s.Key, DelayMs = s.DelayMs };
        Status($"Copied: {s}");
    }

    private void Paste(int index)
    {
        if (_clipboard is not { } c) return;
        InsertAt(index, new MacroStep { Kind = c.Kind, Key = c.Key, DelayMs = c.DelayMs });
    }

    private void Delete(int index)
    {
        if (index < 0 || index >= _macro.Steps.Count) return;
        _macro.Steps.RemoveAt(index);
        _selected = Math.Min(index, _macro.Steps.Count - 1);
        RebuildRows();
    }

    private void Move(int delta)
    {
        int from = _selected, to = _selected + delta;
        if (from < 0 || to < 0 || to >= _macro.Steps.Count) return;
        (_macro.Steps[from], _macro.Steps[to]) = (_macro.Steps[to], _macro.Steps[from]);
        _selected = to;
        RebuildRows();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // ⛔ Never take a key from a text box. The delay editor and the two
        // repeat fields are text boxes, and Del inside one of them means delete
        // a character, not delete the event.
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        switch (e.Key)
        {
            case Key.Delete when _selected >= 0: Delete(_selected); e.Handled = true; break;
            case Key.C when ctrl && _selected >= 0: Copy(_selected); e.Handled = true; break;
            case Key.V when ctrl && _clipboard is not null:
                Paste(_selected < 0 ? _macro.Steps.Count : _selected + 1); e.Handled = true; break;
            case Key.Up when alt: Move(-1); e.Handled = true; break;
            case Key.Down when alt: Move(+1); e.Handled = true; break;
        }
        if (!e.Handled) base.OnKeyDown(e);
    }

    // ---- general tab -------------------------------------------------------

    private Control BuildGeneral()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        sp.Children.Add(Style.Section("Repeat"));

        var count = new TextBox
        {
            Text = _macro.RepeatCount.ToString(),
            Width = 70,
            Margin = new Thickness(8, 0, 0, 0),
        };
        count.TextChanged += (_, _) =>
        {
            if (_building) return;
            if (int.TryParse(count.Text, out int n)) _macro.RepeatCount = Math.Clamp(n, 1, 999);
        };

        sp.Children.Add(Radio("Once", MacroRepeat.Once, null));
        sp.Children.Add(Radio("A number of times", MacroRepeat.Count, count));
        sp.Children.Add(Radio("Until pressed again", MacroRepeat.UntilPressedAgain, null));

        var between = new TextBox { Text = _macro.RepeatDelayMs.ToString(), Width = 70 };
        between.TextChanged += (_, _) =>
        {
            if (_building) return;
            if (int.TryParse(between.Text, out int n))
                _macro.RepeatDelayMs = Math.Clamp(n, 0, 60_000);
        };
        sp.Children.Add(Style.Section("Between runs"));
        sp.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { between, new TextBlock
            {
                Text = "ms",
                Foreground = Style.TextDimBrush,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            } },
        });

        // Both of these are facts about the hardware and the runner that
        // nobody could guess from the controls, so they are stated rather than
        // left to be discovered.
        sp.Children.Add(Style.Note(
            "There is no “while held”: the panel reports taps, not a held contact, "
            + "so a hold-to-repeat macro cannot be expressed on this hardware."));
        sp.Children.Add(Style.Note(
            "Pressing the button again always stops a running macro, whatever the "
            + "repeat mode — it is the only way out of one that repeats."));
        return sp;
    }

    private Control Radio(string text, MacroRepeat mode, Control? extra)
    {
        var r = new RadioButton
        {
            Content = text,
            GroupName = "repeat",
            IsChecked = _macro.Repeat == mode,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        r.IsCheckedChanged += (_, _) =>
        {
            if (_building || r.IsChecked != true) return;
            _macro.Repeat = mode;
        };
        if (extra is null) return r;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { r, extra },
        };
    }

    // ---- test --------------------------------------------------------------

    /// <summary>
    /// Runs the macro once, after a countdown.
    ///
    /// ⛔ The countdown is not politeness. A macro types into whatever holds
    /// focus, and at the moment Test is pressed that is this dialog - possibly
    /// a text box in it. A macro containing ctrl+a then delete would edit the
    /// configuration being written. So the list and the tabs are disabled for
    /// the duration and the user is told, in the status line, to go and focus
    /// the window they actually meant.
    ///
    /// ⛔ And it runs ONE pass whatever Repeat says: an UntilPressedAgain macro
    /// tested from a dialog would otherwise never stop.
    /// </summary>
    private async Task TestAsync()
    {
        if (_testing)
        {
            _cancelTest = true;
            // RunMacro toggles: a second Run on a running macro cancels it.
            if (_testAction is { } running) _runner.Run(running);
            Status("Stopped.");
            return;
        }
        if (_macro.Steps.Count == 0) { Status("Nothing to test: this macro has no events."); return; }
        if (_macro.Steps.FirstOrDefault(s => s.Kind != MacroStepKind.Delay
                                          && UinputKeyboard.Resolve(s.Key) is null) is { } bad)
        {
            Status($"Cannot run: '{bad.Key}' is not a key this can send.");
            return;
        }

        _testing = true;
        _cancelTest = false;
        _tabBody.IsEnabled = false;
        _tabHeader.IsEnabled = false;
        _test.Content = "Stop";
        try
        {
            for (int n = 3; n >= 1 && !_cancelTest; n--)
            {
                Status($"Testing in {n}… focus the window you want the keys to land in.");
                await Task.Delay(1000);
            }
            if (_cancelTest) return;

            var once = Clone(_macro);
            once.Repeat = MacroRepeat.Once;
            _testAction = new SystemAction { Kind = ActionKind.Macro, Macro = once };
            Status("Running…");
            _runner.Run(_testAction);
            // ⛔ Run() is fire and forget: it returns before a single key has
            // been sent, so polling a flag here would report success on a macro
            // that had not started.
            await _runner.MacroCompletion;
            Status(_runner.LastError is { } err
                ? $"Failed: {err}"
                : $"Sent {once.Steps.Count} event(s).");
        }
        finally
        {
            _testing = false;
            _testAction = null;
            _tabBody.IsEnabled = true;
            _tabHeader.IsEnabled = true;
            _test.Content = "Test";
        }
    }

    private void Status(string text) => _status.Text = text;

    // ---- entry point -------------------------------------------------------

    /// <summary>A deep copy: every step, not just the list.</summary>
    public static MacroSpec Clone(MacroSpec s) => new()
    {
        Repeat = s.Repeat,
        RepeatCount = s.RepeatCount,
        RepeatDelayMs = s.RepeatDelayMs,
        Steps = s.Steps
            .Select(x => new MacroStep { Kind = x.Kind, Key = x.Key, DelayMs = x.DelayMs })
            .ToList(),
    };

    /// <summary>
    /// Opens the editor over a copy of <paramref name="current"/> and returns
    /// the edited macro, or null if it was cancelled. The caller assigns the
    /// result; nothing is written to the original either way.
    /// </summary>
    public static async Task<MacroSpec?> EditAsync(
        Window owner, MacroSpec? current, ActionRunner runner, string buttonName = "")
    {
        var dialog = new MacroEditorWindow(
            Clone(current ?? new MacroSpec()), runner, buttonName);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    /// <summary>Builds the dialog without showing it, for --selftest. Nothing is
    /// run: a self test that fired a macro would type into this machine.</summary>
    public static MacroEditorWindow ForTest(MacroSpec macro, ActionRunner runner) =>
        new(Clone(macro), runner, "probe");

    /// <summary>The realised rows, so a probe can read what the list actually
    /// drew rather than a count it set itself.</summary>
    public IReadOnlyList<MacroRow> Rows => _rows.Children.OfType<MacroRow>().ToList();

    /// <summary>The macro under edit, for a probe to compare against.</summary>
    public MacroSpec Editing => _macro;

    /// <summary>The control currently in the tab body, so a probe can assert it
    /// is the SAME one after a round trip rather than a rebuilt copy.</summary>
    public Control? TabBody => _tabBody.Content as Control;

    /// <summary>Drives the tab strip from a probe.</summary>
    public void SelectTab(int tab) { _tab = tab; ShowTab(); }
}

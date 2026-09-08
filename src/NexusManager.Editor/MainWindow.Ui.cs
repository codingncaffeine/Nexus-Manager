using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Render;
using NexusManager.Sensors;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    private Control BuildPanelEditor()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(14),
        };

        // ---- top bar ------------------------------------------------------
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 12) };

        // ⛔ No brand row here. This used to draw an accent dot plus
        // "NEXUS MANAGER" at the top of the page body. iCUE never repeats the
        // application name inside a page - the top bar owns identity - and in
        // the reference the accent appears exactly twice in the whole window,
        // on the selected profile and on nothing else. Spending it on a
        // decorative dot is what made this tab read as a different program.
        // Removing it also gives the row back to a preview that wants width.

        _screenList.Height = 30;
        _screenList.Background = Brushes.Transparent;
        _screenList.BorderThickness = new Thickness(0);
        _screenList.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
        _screenList.SelectionChanged += (_, _) =>
        {
            if (_building || _screenList.SelectedIndex < 0) return;
            _screenIndex = _screenList.SelectedIndex; _moduleIndex = 0;
            StructureChanged(); RefreshThemePanel();
        };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        tabs.Children.Add(_screenList);
        tabs.Children.Add(Flat("+", "Add a screen", () =>
        {
            _set.Screens.Add(new ScreenSpec { Name = $"Screen {_set.Screens.Count + 1}" });
            _screenIndex = _set.Screens.Count - 1;
            StructureChanged(); RefreshScreenList(); RefreshThemePanel();
        }));
        tabs.Children.Add(Flat("−", "Remove this screen", () =>
        {
            if (_set.Screens.Count <= 1) return;
            _set.Screens.RemoveAt(_screenIndex);
            _screenIndex = Math.Max(0, _screenIndex - 1);
            StructureChanged(); RefreshScreenList(); RefreshThemePanel();
        }));
        Grid.SetColumn(tabs, 1); bar.Children.Add(tabs);

        // "Live to panel" belongs to the shell top bar, which owns it. Adding
        // it here too gave the control a second logical parent, which Avalonia
        // rejects - it crashed the Panel tab on first open.
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Accent("Save", async () =>
        {
            try { await Config.SaveAsync(_set, null); _status.Text = $"saved · {Config.Path}"; }
            catch (Exception ex) { _status.Text = $"save failed: {ex.Message}"; }
        }));
        Grid.SetColumn(right, 2); bar.Children.Add(right);
        Grid.SetRow(bar, 0); root.Children.Add(bar);

        // ---- preview + drag-to-resize strip --------------------------------
        _preview.Changed += () => { Changed(); RefreshModuleProps(); };
        _preview.Selected += i =>
        {
            _moduleIndex = i;
            Building(() => _moduleList.SelectedIndex = i);
            RefreshModuleProps();
        };
        // Clicking a BUTTON segment selects it in the screen column, so the same
        // strip drives both kinds of cell rather than only the readouts.
        _preview.ButtonSelected += i =>
        {
            _btnIndex = i;
            RefreshThemePanel();
        };

        var previewHint = new TextBlock
        {
            Foreground = Style.TextDimBrush, FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // The factor is reported rather than asserted: it is chosen from the
        // width actually available, so a fixed "shown at 2x" caption would be
        // wrong at most window sizes.
        void SayScale(int s) => previewHint.Text =
            $"640 × 48, shown at {s}× · hover to see cell boundaries, drag one to resize · "
            + "a cell turns red below its minimum width";
        SayScale(_preview.Scale);
        _preview.ScaleChanged += SayScale;

        var previewInner = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        previewInner.Children.Add(_preview);
        previewInner.Children.Add(previewHint);

        var previewCard = Style.CardPanel("Panel", "the strip, exactly as the hardware draws it", previewInner);
        previewCard.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(previewCard, 1); root.Children.Add(previewCard);

        // ---- three columns -------------------------------------------------
        var cols = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*,290") };

        _moduleList.Background = Brushes.Transparent;
        _moduleList.BorderThickness = new Thickness(0);
        _moduleList.SelectionChanged += (_, _) =>
        {
            if (_building || _moduleList.SelectedIndex < 0) return;
            _moduleIndex = _moduleList.SelectedIndex;
            _preview.SelectedButtonIndex = -1;
            _preview.SelectedIndex = _moduleIndex; _preview.InvalidateVisual();
            RefreshModuleProps();
        };

        var listDock = new DockPanel { LastChildFill = true };
        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        listButtons.Children.Add(Flat("+", "Add module", () =>
        {
            Screen.Modules.Add(new ModuleSpec { Label = "New", Source = "cpu.load", Kind = SensorKind.Load, Unit = "%" });
            _moduleIndex = Screen.Modules.Count - 1; StructureChanged();
        }));
        listButtons.Children.Add(Flat("−", "Remove module", () =>
        {
            if (Screen.Modules.Count == 0) return;
            Screen.Modules.RemoveAt(_moduleIndex);
            _moduleIndex = Math.Max(0, _moduleIndex - 1); StructureChanged();
        }));
        listButtons.Children.Add(Flat("◀", "Move left", () => Move(-1)));
        listButtons.Children.Add(Flat("▶", "Move right", () => Move(+1)));
        DockPanel.SetDock(listButtons, Dock.Bottom);
        listDock.Children.Add(listButtons);
        listDock.Children.Add(_moduleList);

        // Column 0 holds the CELLS on the strip: readouts on top, visualizers
        // beneath them. ⛔ The visualizer editor first went into the Appearance
        // column, where it landed seventh of eight sections and nobody could
        // find it. A visualizer is a cell, so it belongs with the cells.
        var cellCol = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };

        var modulesCard = Style.CardPanel("Modules", "left to right on the strip", listDock);
        modulesCard.Margin = new Thickness(0, 0, 10, 8);
        Grid.SetRow(modulesCard, 0); cellCol.Children.Add(modulesCard);

        var visCard = Style.CardPanel("Visualizers",
            "music, sharing the strip by weight",
            new ScrollViewer { Content = _visProps, MaxHeight = 260 });
        visCard.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetRow(visCard, 1); cellCol.Children.Add(visCard);

        Grid.SetColumn(cellCol, 0); cols.Children.Add(cellCol);

        var propsCard = Style.CardPanel("Module", "", new ScrollViewer { Content = _moduleProps });
        propsCard.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(propsCard, 1); cols.Children.Add(propsCard);

        var themeCard = Style.CardPanel("Appearance", "applies to this screen", new ScrollViewer { Content = _themeProps });
        Grid.SetColumn(themeCard, 2); cols.Children.Add(themeCard);

        Grid.SetRow(cols, 2); root.Children.Add(cols);

        _status.FontSize = 11;
        _status.Foreground = Style.TextDimBrush;
        _status.Margin = new Thickness(2, 10, 0, 0);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        return root;

        void Move(int delta)
        {
            int i = _moduleIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= Screen.Modules.Count) return;
            (Screen.Modules[i], Screen.Modules[j]) = (Screen.Modules[j], Screen.Modules[i]);
            _moduleIndex = j; StructureChanged();
        }
    }

    private static Button Flat(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = glyph, Width = 30, Height = 30, Padding = new Thickness(0),
            Background = Style.TileBrush, Foreground = Style.TextBrush,
            BorderBrush = Style.LineBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private static Button Accent(string text, Func<Task> click)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(16, 6, 16, 6),
            Background = Style.AccentBrush, Foreground = new SolidColorBrush(Style.Page),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            FontWeight = FontWeight.SemiBold,
        };
        b.Click += async (_, _) => await click();
        return b;
    }

    /// <summary>
    /// Styles the screen tabs like the shell's own nav, which is styled like
    /// iCUE's: plain text, dim when inactive, white when active, no pill and no
    /// underline. In the reference the only filled selection in the whole
    /// window is the profile tile in the rail, and it is an accent OUTLINE
    /// rather than a fill - so a default ListBox row, which paints a solid
    /// highlight behind the selected item, reads as a different application.
    ///
    /// The container theme is applied once: it is a control theme, not content,
    /// so rebuilding the list must not re-add it.
    /// </summary>
    private void ApplyScreenTabTheme()
    {
        if (_screenTabsThemed) return;
        _screenTabsThemed = true;

        var theme = new ControlTheme(typeof(ListBoxItem))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.ForegroundProperty, Style.TextDimBrush),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 6, 12, 6)),
                new Setter(TemplatedControl.FontSizeProperty, 13.0),
            },
        };
        // The Fluent ListBoxItem paints its highlight on the ContentPresenter
        // inside its template, so clearing the item's own Background is not
        // enough - the selected and hovered fills have to be cleared there.
        foreach (string state in new[] { ":selected", ":pointerover", ":selected:pointerover" })
        {
            var s = new Avalonia.Styling.Style(x => x.Nesting().Class(state).Template().OfType<ContentPresenter>());
            s.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent));
            theme.Add(s);
        }
        var selected = new Avalonia.Styling.Style(x => x.Nesting().Class(":selected"));
        selected.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Style.TextBrush));
        theme.Add(selected);

        _screenList.ItemContainerTheme = theme;
    }
    private bool _screenTabsThemed;

    private void RefreshScreenList()
    {
        Building(() =>
        {
            ApplyScreenTabTheme();
            _screenList.ItemsSource = _set.Screens.Select(s => s.Name).ToList();
            _screenList.SelectedIndex = _screenIndex;
        });
    }

    /// <summary>One row of the screen's readout list.</summary>
    private sealed record ModuleRow(string Text, Avalonia.Media.Color Tint, SensorKind Kind);

    /// <summary>
    /// Built in one place because two callers refresh this list, and when they
    /// disagreed about the item type the template silently rendered the
    /// record's ToString().
    /// </summary>
    private List<ModuleRow> ModuleRows() =>
        Screen.Modules.Select((m, i) => new ModuleRow(
            $"{i + 1}. {(string.IsNullOrEmpty(m.Label) ? m.Source : m.Label)}",
            Avalonia.Media.Color.Parse(SensorPalette.DefaultColor(m.Kind)),
            m.Kind)).ToList();

    /// <summary>
    /// iCUE colour-codes a sensor by KIND and applies it to the icon, the value
    /// and the chart together - temperature amber, load green, fan blue, volts
    /// purple. It is the strongest single signal that a window belongs to that
    /// application, and SensorPalette already holds those exact colours, so
    /// adopting it costs no new colour decisions.
    ///
    /// The same IconGlyph the Home view's tiles use, so the two views speak one
    /// visual language instead of two.
    /// </summary>
    private void ApplyModuleListTemplate()
    {
        if (_moduleTemplated) return;
        _moduleTemplated = true;
        _moduleList.ItemTemplate = new FuncDataTemplate<ModuleRow>((row, _) =>
        {
            // ⛔ A RECYCLING TEMPLATE IS HANDED A NULL ITEM while containers
            // are reused, so this must tolerate one. Dereferencing it crashed
            // the app on sight - after every self-test check had already
            // reported PASS, because the checks build views without ever
            // rendering a list item.
            if (row is null) return new TextBlock();
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var icon = IconGlyph.For(row.Kind, row.Tint);
            icon.Width = 13; icon.Height = 13;
            icon.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(icon);
            sp.Children.Add(new TextBlock
            {
                Text = row.Text, Foreground = Style.TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return sp;
        }, true);
    }
    private bool _moduleTemplated;

    private void RefreshModuleList()
    {
        Building(() =>
        {
            ApplyModuleListTemplate();
            _moduleList.ItemsSource = ModuleRows();
            _moduleList.SelectedIndex = Math.Clamp(_moduleIndex, 0, Math.Max(0, Screen.Modules.Count - 1));
        });
        RefreshModuleProps();
    }

    // ---- small builders ----------------------------------------------------

    private static TextBlock Head(string t) => new()
    {
        Text = t, FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 10, 0, 2),
    };

    private static Control Row(string label, Control editor)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
        Grid.SetColumn(l, 0); g.Children.Add(l);
        // ⛔ The editor column is a star, so a field used to stretch all the way
        // to the right edge of the window - a box holding "24" ran for hundreds
        // of pixels. iCUE sizes a control to the value it holds; a full-width
        // box reads as a bar, not as a value. Anything that has not asked for
        // its own width gets a cap here, so one rule covers every field rather
        // than each one having to remember.
        if (double.IsNaN(editor.Width) && double.IsPositiveInfinity(editor.MaxWidth))
            editor.MaxWidth = FieldWidth;
        Grid.SetColumn(editor, 1); g.Children.Add(editor);
        return g;
    }

    /// <summary>Widest a property field grows. Long enough for a screen name or
    /// a shell command, short enough that a row still reads as a value.</summary>
    private const double FieldWidth = 240;

    /// <summary>Number fields are narrower again: every value the editor takes -
    /// a weight, a percentage, a temperature bound - fits inside this, so
    /// matching the text fields' width would only add whitespace.</summary>
    private const double NumberWidth = 80;

    private TextBox Text(string? value, Action<string> set)
    {
        var tb = new TextBox { Text = value ?? "" };
        tb.TextChanged += (_, _) => { if (_building) return; set(tb.Text ?? ""); };
        return tb;
    }

    private Control Num(double value, Action<double> set)
    {
        var tb = new TextBox
        {
            Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = NumberWidth,
        };
        tb.TextChanged += (_, _) =>
        {
            if (_building) return;
            if (double.TryParse(tb.Text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double d)) set(d);
        };
        return tb;
    }

    /// <summary>
    /// Colour field: a hex box AND a circular wheel, either of which works.
    /// See <see cref="ColourField"/> - the wheel is not decoration here, because
    /// the panel renders green yellow-shifted (D12) and a hex value reasoned about
    /// on a monitor does not predict what lands on the glass.
    /// </summary>
    private Control Colour(string? value, Action<string?> set)
    {
        var field = new ColourField(value, set);
        field.Changed += () => { if (!_building) Changed(); };
        return field;
    }
}

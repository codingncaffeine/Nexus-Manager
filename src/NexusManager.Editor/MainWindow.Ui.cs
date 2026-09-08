using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
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

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
            Background = Style.AccentBrush, VerticalAlignment = VerticalAlignment.Center,
        });
        brand.Children.Add(new TextBlock
        {
            Text = "NEXUS MANAGER", Foreground = Style.TextBrush,
            FontSize = 13, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.2,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(brand, 0); bar.Children.Add(brand);

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

        var previewInner = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        previewInner.Children.Add(_preview);
        previewInner.Children.Add(new TextBlock
        {
            Text = "Hover the panel to see cell boundaries; drag one to resize. "
                 + "A cell turns red below its minimum width.",
            Foreground = Style.TextDimBrush, FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var previewCard = Style.CardPanel("Panel", "640 × 48, shown at 2×", previewInner);
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

        var modulesCard = Style.CardPanel("Modules", "left to right on the strip", listDock);
        modulesCard.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(modulesCard, 0); cols.Children.Add(modulesCard);

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

    private void RefreshScreenList()
    {
        Building(() =>
        {
            _screenList.ItemsSource = _set.Screens.Select(s => s.Name).ToList();
            _screenList.SelectedIndex = _screenIndex;
        });
    }

    private void RefreshModuleList()
    {
        Building(() =>
        {
            _moduleList.ItemsSource = Screen.Modules
                .Select((m, i) => $"{i + 1}. {(string.IsNullOrEmpty(m.Label) ? m.Source : m.Label)}")
                .ToList();
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
        Grid.SetColumn(editor, 1); g.Children.Add(editor);
        return g;
    }

    private TextBox Text(string? value, Action<string> set)
    {
        var tb = new TextBox { Text = value ?? "" };
        tb.TextChanged += (_, _) => { if (_building) return; set(tb.Text ?? ""); };
        return tb;
    }

    private Control Num(double value, Action<double> set)
    {
        var tb = new TextBox { Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
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

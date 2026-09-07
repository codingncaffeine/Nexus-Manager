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
    private Control BuildLayout()
    {
        var grid = new Grid
        {
            Margin = new Thickness(10),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
        };

        // --- toolbar -------------------------------------------------------
        var addScreen = new Button { Content = "+ Screen" };
        addScreen.Click += (_, _) =>
        {
            _set.Screens.Add(new ScreenSpec { Name = $"Screen {_set.Screens.Count + 1}" });
            _screenIndex = _set.Screens.Count - 1;
            StructureChanged(); RefreshScreenList(); RefreshThemePanel();
        };

        var delScreen = new Button { Content = "− Screen" };
        delScreen.Click += (_, _) =>
        {
            if (_set.Screens.Count <= 1) return;
            _set.Screens.RemoveAt(_screenIndex);
            _screenIndex = Math.Max(0, _screenIndex - 1);
            StructureChanged(); RefreshScreenList(); RefreshThemePanel();
        };

        var save = new Button { Content = "Save" };
        save.Click += async (_, _) =>
        {
            try
            {
                await Config.SaveAsync(_set, null);
                _status.Text = $"saved to {Config.Path}";
            }
            catch (Exception ex) { _status.Text = $"save failed: {ex.Message}"; }
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
        };
        toolbar.Children.Add(new TextBlock { Text = "Screens:", VerticalAlignment = VerticalAlignment.Center });
        _screenList.Height = 32;
        _screenList.Width = 320;
        _screenList.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
        _screenList.SelectionChanged += (_, _) =>
        {
            if (_building || _screenList.SelectedIndex < 0) return;
            _screenIndex = _screenList.SelectedIndex; _moduleIndex = 0;
            StructureChanged(); RefreshThemePanel();
        };
        toolbar.Children.Add(_screenList);
        toolbar.Children.Add(addScreen);
        toolbar.Children.Add(delScreen);
        toolbar.Children.Add(save);
        toolbar.Children.Add(_liveToDevice);
        Grid.SetRow(toolbar, 0);
        grid.Children.Add(toolbar);

        // --- preview -------------------------------------------------------
        var previewBox = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24)),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    _preview,
                    new TextBlock
                    {
                        Text = "640 × 48, shown at 2×. This is the real renderer and the real frame.",
                        Foreground = Brushes.Gray, FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        Grid.SetRow(previewBox, 1);
        grid.Children.Add(previewBox);

        // --- three panes ---------------------------------------------------
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,260") };

        // modules
        var modulePane = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        var moduleButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var addMod = new Button { Content = "+" };
        addMod.Click += (_, _) => { Screen.Modules.Add(new ModuleSpec { Label = "New", Source = "cpu.load", Kind = SensorKind.Load, Unit = "%" }); _moduleIndex = Screen.Modules.Count - 1; StructureChanged(); };
        var delMod = new Button { Content = "−" };
        delMod.Click += (_, _) => { if (Screen.Modules.Count > 0) { Screen.Modules.RemoveAt(_moduleIndex); _moduleIndex = Math.Max(0, _moduleIndex - 1); StructureChanged(); } };
        var upMod = new Button { Content = "▲" };
        upMod.Click += (_, _) => Move(-1);
        var dnMod = new Button { Content = "▼" };
        dnMod.Click += (_, _) => Move(+1);
        moduleButtons.Children.Add(addMod); moduleButtons.Children.Add(delMod);
        moduleButtons.Children.Add(upMod);  moduleButtons.Children.Add(dnMod);
        DockPanel.SetDock(moduleButtons, Dock.Bottom);
        modulePane.Children.Add(moduleButtons);
        _moduleList.SelectionChanged += (_, _) =>
        {
            if (_building || _moduleList.SelectedIndex < 0) return;
            _moduleIndex = _moduleList.SelectedIndex; RefreshModuleProps();
        };
        modulePane.Children.Add(new HeaderedContentControl { Content = _moduleList });
        Grid.SetColumn(modulePane, 0);
        panes.Children.Add(modulePane);

        // module properties
        var propScroll = new ScrollViewer { Content = _moduleProps, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(propScroll, 1);
        panes.Children.Add(propScroll);

        // theme
        var themeScroll = new ScrollViewer { Content = _themeProps };
        Grid.SetColumn(themeScroll, 2);
        panes.Children.Add(themeScroll);

        Grid.SetRow(panes, 2);
        grid.Children.Add(panes);

        Grid.SetRow(_status, 3);
        _status.Margin = new Thickness(0, 8, 0, 0);
        grid.Children.Add(_status);
        return grid;

        void Move(int delta)
        {
            int i = _moduleIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= Screen.Modules.Count) return;
            (Screen.Modules[i], Screen.Modules[j]) = (Screen.Modules[j], Screen.Modules[i]);
            _moduleIndex = j; StructureChanged();
        }
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

    /// <summary>Hex field with a live swatch. A full colour wheel would be nicer,
    /// but the config stores hex and the panel's colour rendition is skewed
    /// (D12) — so the value you type is the value to trust, not the one the
    /// monitor shows you.</summary>
    private Control Colour(string? value, Action<string?> set)
    {
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1),
        };
        void Paint(string? hex)
        {
            var c = NexusManager.Render.Theme.Parse(hex, SkiaSharp.SKColors.Transparent);
            swatch.Background = new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
        }
        Paint(value);

        var tb = new TextBox { Text = value ?? "", PlaceholderText = "inherit" };
        tb.TextChanged += (_, _) =>
        {
            if (_building) return;
            string t = tb.Text ?? "";
            set(string.IsNullOrWhiteSpace(t) ? null : t);
            Paint(t);
            Changed();
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(swatch);
        tb.Width = 130;
        sp.Children.Add(tb);
        return sp;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Sensors;

namespace NexusManager.Editor;

/// <summary>
/// The settings window behind the menu bar's gear, laid out as iCUE lays out
/// theirs: a section list down the left, the chosen section's rows on the right,
/// each row a label on the left and its control on the right.
///
/// Sensor Logging is the section Corsair's guide documents, and it is reproduced
/// row for row - location, interval in seconds, an optional duration limit in
/// minutes, and the sensor selection. Their guide's warning is reproduced too,
/// because it is a real hazard: logging with no limit writes to the drive until
/// somebody stops it.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly SensorRegistry _reg;
    private readonly DashboardLayout _layout;
    private readonly AppSettings _settings;
    private readonly SensorLogger _logger;
    private readonly ContentControl _pane = new();
    private readonly StackPanel _nav = new();
    private string _section = "logging";

    private SettingsWindow(SensorRegistry reg, DashboardLayout layout, AppSettings settings, SensorLogger logger)
    {
        _reg = reg; _layout = layout; _settings = settings; _logger = logger;

        Title = "Settings";
        Width = 900; Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Style.PageBrush;

        var head = new Border
        {
            Background = Style.RailBrush,
            Height = 48,
            Child = new TextBlock
            {
                Text = "Settings", Foreground = Style.TextBrush, FontSize = 16,
                Margin = new Thickness(20, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var navPanel = new StackPanel { Margin = new Thickness(16, 16, 16, 16), Spacing = 2, Width = 230 };
        navPanel.Children.Add(new TextBlock
        {
            Text = "Software Settings", Foreground = Style.TextBrush,
            FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 10),
        });
        navPanel.Children.Add(NavItem("General", "general"));
        navPanel.Children.Add(NavItem("Sensor Logging", "logging"));
        navPanel.Children.Add(new TextBlock
        {
            Text = "Device Settings", Foreground = Style.TextBrush,
            FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 18, 0, 10),
        });
        navPanel.Children.Add(NavItem("Panel", "panel"));
        navPanel.Children.Add(NavItem("About", "about"));
        _nav.Children.Add(navPanel);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var navBorder = new Border
        {
            Child = _nav,
            BorderBrush = Style.LineBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        Grid.SetColumn(navBorder, 0); body.Children.Add(navBorder);
        Grid.SetColumn(_pane, 1); body.Children.Add(_pane);

        var footer = new Border
        {
            BorderBrush = Style.LineBrush, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 10, 20, 10),
            Child = new TextBlock
            {
                Text = "Settings are applied automatically.",
                Foreground = Style.TextDimBrush, FontSize = 11,
            },
        };

        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        dock.Children.Add(body);
        Content = dock;

        Show("logging");
        Closed += (_, _) => _settings.Save();
    }

    private Button NavItem(string text, string id)
    {
        var b = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Style.TextMidBrush,
            FontSize = 13,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(4),
            Tag = id,
        };
        b.Click += (_, _) => Show(id);
        return b;
    }

    private void Show(string id)
    {
        _section = id;
        foreach (var c in ((StackPanel)_nav.Children[0]).Children)
        {
            if (c is not Button b) continue;
            bool on = (string?)b.Tag == id;
            b.Background = on ? Style.RailBrush : Brushes.Transparent;
            b.Foreground = on ? Style.TextBrush : Style.TextMidBrush;
        }
        _pane.Content = new ScrollViewer
        {
            Padding = new Thickness(24, 20, 24, 20),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = id switch
            {
                "general" => BuildGeneral(),
                "panel"   => BuildPanel(),
                "about"   => BuildAbout(),
                _         => BuildLogging(),
            },
        };
    }

    // --- rows -----------------------------------------------------------------

    /// <summary>A settings row: label on the left, control on the right, which is
    /// the shape every row in iCUE's settings takes.</summary>
    private static Control Row(string label, Control control)
    {
        var g = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("280,*"),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var t = new TextBlock
        {
            Text = label, Foreground = Style.TextBrush, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(t, 0); g.Children.Add(t);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1); g.Children.Add(control);
        return g;
    }

    private static TextBox Field(string text, double width)
        => new() { Text = text, Width = width, FontSize = 12 };

    // --- sections -------------------------------------------------------------

    private Control BuildGeneral()
    {
        var sp = new StackPanel();

        var unit = new ComboBox
        {
            ItemsSource = new[] { "Celsius (°C)", "Fahrenheit (°F)", "Kelvin (K)" },
            SelectedIndex = (int)_reg.Scale, Width = 200, FontSize = 12,
        };
        unit.SelectionChanged += (_, _) =>
        {
            _reg.Scale = (TemperatureScale)Math.Max(0, unit.SelectedIndex);
            _settings.Scale = _reg.Scale;
        };
        sp.Children.Add(Row("Temperature unit", unit));

        var autostart = new CheckBox
        {
            IsChecked = Autostart.IsEnabled, Foreground = Style.TextBrush, FontSize = 12,
            Content = "Start Nexus Manager when I log in",
        };
        autostart.IsCheckedChanged += (_, _) => Autostart.Set(autostart.IsChecked == true);
        sp.Children.Add(Row("Start with the system", autostart));

        var tray = new CheckBox
        {
            IsChecked = _settings.StartMinimised, Foreground = Style.TextBrush, FontSize = 12,
            Content = "Start hidden, with only the tray icon",
        };
        tray.IsCheckedChanged += (_, _) => _settings.StartMinimised = tray.IsChecked == true;
        sp.Children.Add(Row("Start minimised", tray));

        var close = new CheckBox
        {
            IsChecked = _settings.CloseToTray, Foreground = Style.TextBrush, FontSize = 12,
            Content = "Closing the window keeps the panel running",
        };
        close.IsCheckedChanged += (_, _) => _settings.CloseToTray = close.IsChecked == true;
        sp.Children.Add(Row("Close to tray", close));

        sp.Children.Add(Style.Note(
            "With both of these on, the panel comes up at login and keeps running "
            + "after the window is dismissed - so it is never left blank."));
        return sp;
    }

    private Control BuildPanel()
    {
        var sp = new StackPanel();

        var brightness = new Slider
        {
            Minimum = 0, Maximum = 100, Value = _settings.Brightness, Width = 280,
            TickFrequency = 10, IsSnapToTickEnabled = false,
        };
        var readout = new TextBlock
        {
            Text = $"{_settings.Brightness}%", Foreground = Style.TextDimBrush,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        brightness.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _settings.Brightness = (int)brightness.Value;
            readout.Text = $"{_settings.Brightness}%";
            BrightnessChanged?.Invoke(_settings.Brightness);
        };
        sp.Children.Add(Row("Brightness", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { brightness, readout },
        }));

        var fps = new ComboBox
        {
            ItemsSource = new[] { "12 fps", "24 fps", "30 fps", "60 fps" },
            SelectedIndex = _settings.TargetFps switch { 12 => 0, 30 => 2, 60 => 3, _ => 1 },
            Width = 140, FontSize = 12,
        };
        fps.SelectionChanged += (_, _) =>
            _settings.TargetFps = fps.SelectedIndex switch { 0 => 12, 2 => 30, 3 => 60, _ => 24 };
        sp.Children.Add(Row("Frame rate", fps));
        sp.Children.Add(Style.Note(
            "24 fps matches the rate Corsair play animations at and costs about a "
            + "third of the panel's measured 65 fps ceiling."));
        return sp;
    }

    private Control BuildAbout()
    {
        var sp = new StackPanel { Spacing = 8 };
        sp.Children.Add(new TextBlock
        {
            Text = "Nexus Manager", Foreground = Style.TextBrush,
            FontSize = 20, FontWeight = FontWeight.SemiBold,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "Sensor display and screen editor for the Corsair iCUE NEXUS on Linux.",
            Foreground = Style.TextDimBrush, FontSize = 12,
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"{_reg.All.Count} sensors discovered on this machine.",
            Foreground = Style.TextDimBrush, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
        });
        return sp;
    }

    public event Action<int>? BrightnessChanged;

    // --- sensor logging -------------------------------------------------------

    private Control BuildLogging()
    {
        var log = _settings.Logging;
        var sp = new StackPanel();

        var startBtn = new Button
        {
            Content = _logger.IsRunning ? "Stop Logging" : "Start Logging",
            Background = Style.RailBrush, Foreground = Style.TextBrush,
            Padding = new Thickness(22, 7, 22, 7), FontSize = 13,
        };
        var status = new TextBlock
        {
            Foreground = Style.TextDimBrush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };

        void Sync()
        {
            startBtn.Content = _logger.IsRunning ? "Stop Logging" : "Start Logging";
            status.Text = _logger.CurrentFile is null
                ? ""
                : $"{_logger.RowsWritten} rows · {System.IO.Path.GetFileName(_logger.CurrentFile)}";
        }

        _logger.Progressed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Sync);

        startBtn.Click += (_, _) =>
        {
            if (_logger.IsRunning) { _logger.Stop(); Sync(); return; }
            try { _logger.Start(log); _settings.Save(); }
            catch (Exception ex) { status.Text = ex.Message; }
            Sync();
        };
        sp.Children.Add(Row("Sensor Logging", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { startBtn, status },
        }));

        var dir = Field(log.Directory, 380);
        dir.TextChanged += (_, _) => log.Directory = dir.Text ?? log.Directory;
        sp.Children.Add(Row("Location", dir));

        var interval = Field(log.IntervalSeconds.ToString(), 90);
        interval.TextChanged += (_, _) =>
        {
            if (int.TryParse(interval.Text, out int v) && v > 0) log.IntervalSeconds = v;
        };
        sp.Children.Add(Row("Logging Interval (sec)", interval));

        var limitToggle = new ToggleSwitch
        {
            IsChecked = log.LimitDuration, OnContent = "", OffContent = "",
            VerticalAlignment = VerticalAlignment.Center,
        };
        var minutes = Field(log.DurationMinutes.ToString(), 90);
        minutes.IsEnabled = log.LimitDuration;
        minutes.TextChanged += (_, _) =>
        {
            if (int.TryParse(minutes.Text, out int v) && v > 0) log.DurationMinutes = v;
        };
        limitToggle.IsCheckedChanged += (_, _) =>
        {
            log.LimitDuration = limitToggle.IsChecked == true;
            minutes.IsEnabled = log.LimitDuration;
        };
        sp.Children.Add(Row("Limit Log Duration (min)", new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { limitToggle, minutes },
        }));

        sp.Children.Add(Style.Note(
            "With no duration limit, logging keeps writing to the chosen drive "
            + "until it is stopped by hand."));

        // --- Select Group / Select Sensors, side by side as iCUE has them ------
        var split = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("280,*"),
            Margin = new Thickness(0, 18, 0, 0),
            MinHeight = 260,
        };

        var groupList = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            FontSize = 12, MaxHeight = 260,
        };
        var deviceNames = new List<string> { "All" };
        deviceNames.AddRange(_reg.ByDevice().Select(g => g.Key.Name));
        groupList.ItemsSource = deviceNames;
        groupList.SelectedIndex = 0;

        var sensorPanel = new StackPanel();
        var sensorScroll = new ScrollViewer
        {
            Content = sensorPanel, MaxHeight = 260,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        void FillSensors()
        {
            sensorPanel.Children.Clear();
            string? filter = groupList.SelectedIndex <= 0 ? null : (string?)groupList.SelectedItem;
            foreach (var s in _reg.All.Where(s => filter is null || s.Device == filter)
                                      .OrderBy(s => s.Device).ThenBy(s => s.Label))
            {
                var box = new CheckBox
                {
                    Content = $"{s.Device} · {_layout.NameFor(s.Key) ?? s.Label}",
                    IsChecked = log.Sensors.Contains(s.Key),
                    Foreground = Style.TextBrush, FontSize = 12,
                };
                string key = s.Key;
                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true) { if (!log.Sensors.Contains(key)) log.Sensors.Add(key); }
                    else log.Sensors.Remove(key);
                    _settings.Save();
                };
                sensorPanel.Children.Add(box);
            }
        }
        groupList.SelectionChanged += (_, _) => FillSensors();
        FillSensors();

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "Select Group", Foreground = Style.TextBrush, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        left.Children.Add(groupList);
        Grid.SetColumn(left, 0); split.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = "Select Sensors", Foreground = Style.TextBrush, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        right.Children.Add(sensorScroll);
        Grid.SetColumn(right, 1); split.Children.Add(right);

        sp.Children.Add(split);
        Sync();
        return sp;
    }

    public static async Task ShowAsync(
        Window owner, SensorRegistry reg, DashboardLayout layout,
        AppSettings settings, SensorLogger logger, Action<int>? onBrightness = null)
    {
        var w = new SettingsWindow(reg, layout, settings, logger);
        if (onBrightness is not null) w.BrightnessChanged += onBrightness;
        await w.ShowDialog(owner);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NexusManager.Render;
using NexusManager.Sensors;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    private void RefreshModuleProps()
    {
        _building = true;
        try {
        _moduleProps.Children.Clear();
        var m = Module;
        if (m is null)
        {
            _moduleProps.Children.Add(new TextBlock { Text = "No modules. Use + to add one.", Foreground = Brushes.Gray });
            return;
        }

        _moduleProps.Children.Add(Head("Module"));
        _moduleProps.Children.Add(Row("Label", Text(m.Label, v => { m.Label = v; Changed(); RefreshModuleListLabels(); })));

        // sensor picker
        var srcRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var srcBox = new TextBlock
        {
            Text = m.Source, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Width = 200,
        };
        var pick = new Button { Content = "Pick…" };
        pick.Click += async (_, _) =>
        {
            if (_reg is not { } reg) return;
            var chosen = await SensorPickerDialog.PickAsync(this, reg);
            if (chosen is null) return;
            m.Source = chosen.Key;
            m.Kind = chosen.Kind;
            m.Unit = Units.Symbol(chosen.Unit, reg.Scale);
            m.Min = chosen.SuggestedMin;
            m.Max = chosen.SuggestedMax;
            if (string.IsNullOrWhiteSpace(m.Label)) m.Label = chosen.Label;
            srcBox.Text = m.Source;
            StructureChanged(); RefreshModuleProps();
        };
        srcRow.Children.Add(srcBox); srcRow.Children.Add(pick);
        _moduleProps.Children.Add(Row("Sensor", srcRow));

        var kindBox = new ComboBox { ItemsSource = Enum.GetValues<SensorKind>(), SelectedItem = m.Kind };
        kindBox.SelectionChanged += (_, _) =>
        {
            if (kindBox.SelectedItem is SensorKind k) { m.Kind = k; Changed(); }
        };
        _moduleProps.Children.Add(Row("Type", kindBox));

        _moduleProps.Children.Add(Row("Unit", Text(m.Unit, v => { m.Unit = v; Changed(); })));
        _moduleProps.Children.Add(Row("Decimals", Num(m.EffectiveDecimals, v => { m.Decimals = (int)v; Changed(); })));

        _moduleProps.Children.Add(Head("Chart range"));
        _moduleProps.Children.Add(Row("Min", Num(m.Min, v => { m.Min = v; Changed(); })));
        _moduleProps.Children.Add(Row("Max", Num(m.Max, v => { m.Max = v; Changed(); })));
        _moduleProps.Children.Add(new TextBlock
        {
            Text = "Fixed, never auto-ranged: an auto-scaled chart makes an idle\n" +
                   "CPU look identical to a busy one.",
            Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
        });

        _moduleProps.Children.Add(Head("Size"));
        var weight = new Slider { Minimum = 0.25, Maximum = 4, Value = m.Weight, TickFrequency = 0.25, IsSnapToTickEnabled = true };
        var weightText = new TextBlock { Text = $"{m.Weight:0.00}", Foreground = Brushes.Gray };
        weight.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            m.Weight = weight.Value;
            weightText.Text = $"{m.Weight:0.00}";
            Changed();
        };
        _moduleProps.Children.Add(Row("Weight", weight));
        _moduleProps.Children.Add(Row("", weightText));
        _moduleProps.Children.Add(new TextBlock
        {
            Text = "Share of the strip. {2,1,1} gives the first module half.\n" +
                   "Below 72px a value collides with its own unit; the status\n" +
                   "bar warns rather than rendering something unreadable.",
            Foreground = Brushes.Gray, FontSize = 11,
        });

        _moduleProps.Children.Add(Head("Colours (blank = use theme)"));
        _moduleProps.Children.Add(Row("Value", Colour(m.ValueColor, v => m.ValueColor = v)));
        _moduleProps.Children.Add(Row("Chart", Colour(m.ChartColor, v => m.ChartColor = v)));
        _moduleProps.Children.Add(Row("Label", Colour(m.LabelColor, v => m.LabelColor = v)));

        _moduleProps.Children.Add(Head("Show"));
        _moduleProps.Children.Add(Check("Chart", m.ShowChart, v => { m.ShowChart = v; Changed(); }));
        _moduleProps.Children.Add(Check("Value", m.ShowValue, v => { m.ShowValue = v; Changed(); }));
        _moduleProps.Children.Add(Check("Type icon", m.ShowIcon, v => { m.ShowIcon = v; Changed(); }));

        } finally { _building = false; }
    }

    private void RefreshThemePanel()
    {
        _building = true;
        try {
        _themeProps.Children.Clear();
        var t = Screen.Theme;

        _themeProps.Children.Add(Head("Screen"));
        _themeProps.Children.Add(Row("Name", Text(Screen.Name, v => { Screen.Name = v; RefreshScreenList(); })));

        _themeProps.Children.Add(Head("Type"));
        var fonts = new ComboBox
        {
            ItemsSource = SkiaSharp.SKFontManager.Default.GetFontFamilies().OrderBy(f => f).ToList(),
            SelectedItem = t.FontFamily,
        };
        fonts.SelectionChanged += (_, _) =>
        {
            if (fonts.SelectedItem is string f) { t.FontFamily = f; Changed(); }
        };
        _themeProps.Children.Add(Row("Font", fonts));
        _themeProps.Children.Add(Row("Label size", Num(t.CaptionSize, v => { t.CaptionSize = (float)v; Changed(); })));
        _themeProps.Children.Add(Row("Value size", Num(t.ValueSize, v => { t.ValueSize = (float)v; Changed(); })));
        _themeProps.Children.Add(Check("Bold labels", t.CaptionBold, v => { t.CaptionBold = v; Changed(); }));
        _themeProps.Children.Add(Check("Bold values", t.ValueBold, v => { t.ValueBold = v; Changed(); }));

        _themeProps.Children.Add(Head("Colours"));
        _themeProps.Children.Add(Row("Background", Colour(t.Background, v => t.Background = v ?? "#000000")));
        _themeProps.Children.Add(Row("Label", Colour(t.Caption, v => t.Caption = v ?? "#FFFFFF")));
        _themeProps.Children.Add(Row("Divider", Colour(t.Divider, v => t.Divider = v ?? "#2C2C34")));
        _themeProps.Children.Add(Row("Min/max", Colour(t.MinMax, v => t.MinMax = v ?? "#8A8A96")));
        _themeProps.Children.Add(Row("Warn", Colour(t.Warn, v => t.Warn = v ?? "#FFAA28")));
        _themeProps.Children.Add(Row("Hot", Colour(t.Hot, v => t.Hot = v ?? "#FF463C")));

        // --- Background ------------------------------------------------------
        // Custom animation lives here, and it needs no new file format: Corsair
        // express animation as nothing more than "the background file is a GIF".
        // Their .cuescreens carries no frame, duration or loop field anywhere -
        // every element across all six official packs was enumerated to check.
        var bg = Screen.Background;
        _themeProps.Children.Add(Head("Background"));

        var pathBox = Text(bg.Image, v =>
        {
            bg.Image = string.IsNullOrWhiteSpace(v) ? null : v;
            _crop.SetImage(bg.Image, bg.Fit, bg.Zoom, bg.FocusX, bg.FocusY);
            Changed(); RefreshBackgroundInfo();
        });
        var browse = new Button
        {
            Content = "…", Width = 30, Padding = new Thickness(0),
            [ToolTip.TipProperty] = "Choose an image or animation",
        };
        browse.Click += async (_, _) => await PickBackgroundAsync(pathBox);
        var clear = new Button
        {
            Content = "✕", Width = 30, Padding = new Thickness(0),
            [ToolTip.TipProperty] = "Use the background colour instead",
        };
        clear.Click += (_, _) =>
        {
            bg.Image = null;
            _crop.SetImage(null, bg.Fit, bg.Zoom, bg.FocusX, bg.FocusY);
            Building(() => pathBox.Text = "");
            Changed(); RefreshBackgroundInfo();
        };
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(pathBox, 0); pathRow.Children.Add(pathBox);
        Grid.SetColumn(browse, 1);  pathRow.Children.Add(browse);
        Grid.SetColumn(clear, 2);   pathRow.Children.Add(clear);
        _themeProps.Children.Add(Row("Image", pathRow));

        _zoomSlider = new Slider
        {
            Minimum = 1, Maximum = 8, Value = Math.Clamp(bg.Zoom, 1, 8),
            Width = 200, SmallChange = 0.1, LargeChange = 0.5,
        };
        _zoomSlider.PropertyChanged += (_, e) =>
        {
            if (_building || e.Property != RangeBase.ValueProperty) return;
            bg.Zoom = _zoomSlider.Value;
            _crop.SetGeometry(bg.Fit, bg.Zoom, bg.FocusX, bg.FocusY);
            Changed(); RefreshBackgroundInfo();
        };

        var fit = new ComboBox
        {
            ItemsSource = Enum.GetValues<BackgroundFit>().ToList(),
            SelectedItem = bg.Fit,
        };
        fit.SelectionChanged += (_, _) =>
        {
            if (_building || fit.SelectedItem is not BackgroundFit f) return;
            bg.Fit = f;
            _crop.SetImage(bg.Image, bg.Fit, bg.Zoom, bg.FocusX, bg.FocusY);
            Changed(); RefreshBackgroundInfo();
        };
        _themeProps.Children.Add(Row("Fit", fit));

        // Drag-to-position. A 640x48 strip is a 13:1 sliver, so for anything
        // that is not already that shape, WHICH part shows is the whole choice.
        _crop.SetImage(bg.Image, bg.Fit, bg.Zoom, bg.FocusX, bg.FocusY);
        WireCropOnce();
        _themeProps.Children.Add(_crop);
        _themeProps.Children.Add(Style.Note(
            "Drag the bright box to choose what reaches the panel. Scroll to zoom. "
            + "Animated .gif and .webp both play."));
        _themeProps.Children.Add(Row("Zoom", _zoomSlider));
        _themeProps.Children.Add(Row("Opacity", Num(bg.Opacity, v =>
            { bg.Opacity = (byte)Math.Clamp(v, 0, 255); Changed(); })));
        _themeProps.Children.Add(Row("Speed", Num(bg.Speed, v =>
            { bg.Speed = Math.Clamp(v, 0.05, 20); Changed(); })));
        _themeProps.Children.Add(Row("Scroll px/s", Num(bg.ScrollSpeed, v =>
            { bg.ScrollSpeed = Math.Clamp(v, 0, 600); Changed(); })));
        _themeProps.Children.Add(Check("Loop animation", bg.Loop, v => { bg.Loop = v; Changed(); }));
        _themeProps.Children.Add(_bgInfo);
        RefreshBackgroundInfo();

        _themeProps.Children.Add(Head("Chart"));
        _themeProps.Children.Add(Row("Fill alpha", Num(t.ChartFillAlpha, v => { t.ChartFillAlpha = (byte)Math.Clamp(v, 0, 255); Changed(); })));
        _themeProps.Children.Add(Row("Line alpha", Num(t.ChartLineAlpha, v => { t.ChartLineAlpha = (byte)Math.Clamp(v, 0, 255); Changed(); })));
        _themeProps.Children.Add(Check("Show min/max", t.ShowMinMax, v => { t.ShowMinMax = v; Changed(); }));

        _themeProps.Children.Add(Head("Panel"));
        _themeProps.Children.Add(Row("Brightness", Num(_set.Brightness, v =>
        {
            _set.Brightness = (int)Math.Clamp(v, 0, 100);
            try { _device?.SetBrightness(_set.Brightness); } catch (Exception) { }
        })));
        _themeProps.Children.Add(Check("Page dots", _set.ShowPageIndicator, v => { _set.ShowPageIndicator = v; Changed(); }));
        WireButtonList();
        BuildButtonSection();

        _themeProps.Children.Add(new TextBlock
        {
            Text = "Judge colours on the PANEL, not here: its green primary is\n" +
                   "yellow-shifted, so a pure green renders lime and blue+green\n" +
                   "reads white rather than cyan.",
            Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        });

        } finally { _building = false; }
    }

    private void RefreshModuleListLabels()
    {
        // Assigning ItemsSource raises SelectionChanged, which rebuilds the
        // property panel, which raises TextChanged, which lands back here.
        // That was the second loop.
        Building(() =>
        {
            int keep = _moduleList.SelectedIndex;
            _moduleList.ItemsSource = Screen.Modules
                .Select((m, i) => $"{i + 1}. {(string.IsNullOrEmpty(m.Label) ? m.Source : m.Label)}").ToList();
            _moduleList.SelectedIndex = keep;
        });
    }

    private CheckBox Check(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.IsCheckedChanged += (_, _) => { if (_building) return; set(cb.IsChecked == true); };
        return cb;
    }
}

public sealed partial class MainWindow
{
    /// <summary>Reports what the background actually decoded to. A mistyped path
    /// or an unsupported file otherwise presents as "the feature does nothing",
    /// which is the hardest kind of failure to diagnose from the outside.</summary>
    /// <summary>Drag-to-choose-the-crop, for images that are not already 13:1.</summary>
    private readonly CropPicker _crop = new() { Margin = new Thickness(0, 6, 0, 4) };
    private Slider? _zoomSlider;

    /// <summary>
    /// Subscribes the crop picker ONCE.
    ///
    /// _crop is a field that outlives the property panel, but the panel is torn
    /// down and rebuilt on every screen change - so subscribing there stacks a
    /// fresh handler each time, and every one of them holds the BackgroundSpec
    /// of the screen that was selected when it was created. One drag would then
    /// write focus into several screens at once, most of them not on display.
    /// </summary>
    private bool _cropWired;

    private void WireCropOnce()
    {
        if (_cropWired) return;
        _cropWired = true;

        // Reads Screen.Background at the moment of the drag rather than
        // capturing one, so it always edits the screen actually shown.
        _crop.FocusChanged += (fx, fy) =>
        {
            Screen.Background.FocusX = fx;
            Screen.Background.FocusY = fy;
            Changed(); RefreshBackgroundInfo();
        };
        _crop.ZoomChanged += z =>
        {
            Screen.Background.Zoom = z;
            if (_zoomSlider is not null) Building(() => _zoomSlider.Value = z);
            Changed(); RefreshBackgroundInfo();
        };
    }

    private readonly TextBlock _bgInfo = new()
    {
        Foreground = Brushes.Gray, FontSize = 11,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
    };

    private void RefreshBackgroundInfo()
    {
        var bg = Screen.Background;
        if (!bg.HasImage)
        {
            _bgInfo.Text = "No image: the background colour is used. "
                         + "Point this at a .gif (or animated .webp) and it plays, "
                         + "which is exactly how iCUE does animation.";
            return;
        }

        // Read from the RENDERER's decode, not by decoding again here: reporting
        // on a second, separate decode could disagree with what is on the panel.
        var img = _renderer?.BackgroundImage;
        string? err = _renderer?.BackgroundError;

        if (img is null)
        {
            _bgInfo.Text = err is null ? "Not loaded yet." : $"⚠ {err}";
            return;
        }

        string what = img.IsAnimated
            ? $"{img.FrameCount} frames · {img.Duration.TotalMilliseconds:F0} ms · "
              + $"{img.FrameCount / Math.Max(0.001, img.Duration.TotalSeconds):F1} fps"
            : "still image";
        _bgInfo.Text = $"{what} · {img.Size.Width}×{img.Size.Height} · "
                     + $"{img.BytesUsed / 1024.0:F0} KB"
                     + (err is null ? "" : $"\n⚠ {err}");
    }

    private async Task PickBackgroundAsync(TextBox pathBox)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a background image or animation",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images and animations")
                    {
                        Patterns = ["*.gif", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"],
                    },
                ],
            });
            if (files.Count == 0) return;
            string? path = files[0].TryGetLocalPath();
            if (path is null) return;

            Screen.Background.Image = path;
            _crop.SetImage(path, Screen.Background.Fit, Screen.Background.Zoom,
                           Screen.Background.FocusX, Screen.Background.FocusY);
            Building(() => pathBox.Text = path);
            Changed();
            RefreshBackgroundInfo();
        }
        catch (Exception ex)
        {
            _bgInfo.Text = $"⚠ {ex.Message}";
        }
    }
}

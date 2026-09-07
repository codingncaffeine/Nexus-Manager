using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
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

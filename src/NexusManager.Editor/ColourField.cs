using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Render;
using NexusManager.Sensors;
using SkiaSharp;

namespace NexusManager.Editor;

/// <summary>
/// A colour field offering BOTH ways in: type a hex value, or drag around a
/// colour wheel.
///
///     [swatch] [#E8913C]     <- click the swatch for the wheel
///
/// Both are live and always agree. Neither is the "real" one - the hex box is
/// there for anyone who would rather paste a known value, and the wheel is there
/// so nobody has to look one up.
///
/// The wheel matters more here than in most applications, because of D12: this
/// panel's green primary is yellow-shifted, so a colour chosen by reasoning about
/// its hex value can land somewhere quite different on the glass. Dragging while
/// watching the strip is the only reliable way to pick, and dragging pushes to
/// the panel within one editor tick.
/// </summary>
public sealed class ColourField : StackPanel
{
    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly ColorSpectrum _wheel;
    private readonly ColorSlider _brightness;
    private readonly TextBox _wheelHex;
    private readonly Action<string?> _set;

    /// <summary>Guards the two-way sync. Assigning to the wheel raises the same
    /// ColorChanged a drag does, and assigning to the box raises TextChanged -
    /// so without this each one drives the other forever.</summary>
    private bool _syncing;

    /// <summary>Raised after the value changes by any route, so the caller can
    /// re-render. Kept separate from the setter so a caller cannot forget it.</summary>
    public event Action? Changed;

    public ColourField(string? value, Action<string?> set)
    {
        _set = set;
        Orientation = Orientation.Horizontal;
        Spacing = 6;

        _swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            [ToolTip.TipProperty] = "Pick a colour",
        };

        _hex = new TextBox { Text = value ?? "", PlaceholderText = "inherit", Width = 104 };
        _wheelHex = new TextBox { Text = value ?? "", PlaceholderText = "#RRGGBB", Width = 120 };

        _wheel = new ColorSpectrum
        {
            // Ring, not Box: the request was specifically for the circular one you
            // drag around. Hue runs around the ring, saturation across it.
            Shape = ColorSpectrumShape.Ring,
            Components = ColorSpectrumComponents.HueSaturation,
            // ThirdComponent is derived from Components, not set: with
            // HueSaturation on the ring, brightness is what is left over, and it
            // becomes the slider below.
            Width = 196, Height = 196,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _brightness = new ColorSlider
        {
            ColorModel = ColorModel.Hsva,
            ColorComponent = ColorComponent.Component3,
            Orientation = Orientation.Horizontal,
            Width = 196, Height = 22,
            IsPerceptive = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // --- wiring, each route guarded against driving the others -------------
        _hex.TextChanged += (_, _) => { if (!_syncing) Commit(_hex.Text, from: _hex); };
        _wheelHex.TextChanged += (_, _) => { if (!_syncing) Commit(_wheelHex.Text, from: _wheelHex); };
        _wheel.ColorChanged += (_, e) => { if (!_syncing) Commit(ToHex(e.NewColor), from: _wheel); };
        _brightness.ColorChanged += (_, e) => { if (!_syncing) Commit(ToHex(e.NewColor), from: _brightness); };

        _swatch.PointerPressed += (_, _) => FlyoutBase.ShowAttachedFlyout(_swatch);
        FlyoutBase.SetAttachedFlyout(_swatch, BuildFlyout());

        Children.Add(_swatch);
        Children.Add(_hex);

        Apply(value, from: null);
    }

    private Flyout BuildFlyout()
    {
        var body = new StackPanel { Spacing = 10, Width = 212 };
        body.Children.Add(_wheel);
        body.Children.Add(_brightness);

        body.Children.Add(new TextBlock
        {
            Text = "PANEL COLOURS", Foreground = Style.TextDimBrush, FontSize = 9,
            FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
        });
        var presets = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (string hex in PresetHexes())
            presets.Children.Add(Preset(hex));
        body.Children.Add(presets);

        var hexRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hexRow.Children.Add(new TextBlock
        {
            Text = "Hex", Foreground = Style.TextDimBrush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        hexRow.Children.Add(_wheelHex);
        body.Children.Add(hexRow);

        body.Children.Add(new TextBlock
        {
            // Not a disclaimer - a measured fact about this hardware, and the
            // single most useful thing to know while choosing a colour for it.
            Text = "This panel's green is yellow-shifted: pure green reads lime and "
                 + "blue+green reads white. Watch the strip, not this wheel.",
            Foreground = Style.TextDimBrush, FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
        });

        return new Flyout
        {
            Content = new Border { Padding = new Thickness(12), Child = body },
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };
    }

    /// <summary>The palette this application actually uses, so the common choices
    /// are one click rather than a hunt around the wheel.</summary>
    private static IEnumerable<string> PresetHexes()
    {
        foreach (var kind in new[]
                 {
                     SensorKind.Temperature, SensorKind.Load, SensorKind.Fan,
                     SensorKind.Voltage, SensorKind.Power, SensorKind.Memory,
                     SensorKind.Network, SensorKind.Frequency,
                 })
            yield return SensorPalette.DefaultColor(kind);

        yield return "#FFFFFF";
        yield return "#8A8A96";
        yield return "#FFAA28";
        yield return "#FF463C";
    }

    private Control Preset(string hex)
    {
        var c = NexusManager.Render.Theme.Parse(hex, SKColors.Black);
        var b = new Border
        {
            Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 4),
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue)),
            BorderBrush = Style.LineBrush, BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            [ToolTip.TipProperty] = hex,
        };
        b.PointerPressed += (_, _) => Commit(hex, from: null);
        return b;
    }

    private static string ToHex(Color c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Pushes a new value out to the model and into every control except
    /// the one it came from.</summary>
    private void Commit(string? text, object? from)
    {
        string? stored = string.IsNullOrWhiteSpace(text) ? null : text;
        _set(stored);
        Apply(stored, from);
        Changed?.Invoke();
    }

    private void Apply(string? hex, object? from)
    {
        var c = NexusManager.Render.Theme.Parse(hex, SKColors.Transparent);
        var colour = Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

        _syncing = true;
        try
        {
            _swatch.Background = new SolidColorBrush(colour);
            if (!ReferenceEquals(from, _hex)) _hex.Text = hex ?? "";
            if (!ReferenceEquals(from, _wheelHex)) _wheelHex.Text = hex ?? "";

            // An unset ("inherit") value has no position on a wheel, so leave the
            // wheel where it was rather than snapping it to transparent black.
            if (hex is null) return;
            if (!ReferenceEquals(from, _wheel)) _wheel.Color = colour;
            if (!ReferenceEquals(from, _brightness)) _brightness.Color = colour;
        }
        finally { _syncing = false; }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Render;
using NexusManager.Sensors;

namespace NexusManager.Editor;

/// <summary>Which of iCUE's two tile shapes to draw.</summary>
public enum TileVariant
{
    /// <summary>Dashboard: name, reading, then the session low/high pair.</summary>
    Dashboard,
    /// <summary>Home: name, the DEVICE it belongs to, then the reading. Home
    /// tiles are not grouped by device, so each one has to name its own.</summary>
    Home,
}

/// <summary>
/// One sensor, laid out as iCUE lays out a sensor tile. The two variants differ
/// in their third line, and that difference is not cosmetic - on the Dashboard
/// the device is already the group header, so the row is spent on min/max
/// instead:
///
///     Dashboard            Home
///     Package      [ic]    CPU TEMP           [ic]
///     44.13 C              AMD Ryzen 9 7950X
///     v43   ^71            45.75 C
///     [==== chart ====]    [==== chart ====]
///
/// Colour comes from the sensor TYPE, not an alarm state - the palette says what
/// you are looking at before you read the number.
///
/// No border: a measured scan across iCUE's own tile edge steps from the card
/// colour to the tile colour through one antialiased pixel. The fill contrast is
/// the entire edge.
/// </summary>
public sealed class SensorTile : Border
{
    private readonly TextBlock _value = new() { FontSize = 17 };
    private readonly TextBlock _unit  = new() { FontSize = 11, Margin = new Thickness(3, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _label;
    private readonly TextBlock _third = new() { FontSize = 10, Foreground = Style.TextDimBrush };
    private readonly MiniChart _chart = new() { Height = 30 };
    private readonly History _history = new(240);
    private readonly SensorDescriptor _desc;
    private readonly Color _tint;
    private readonly int _decimals;
    private readonly TileVariant _variant;
    private string _labelFull = "";
    private readonly Control _icon;
    private readonly Button _kebab;

    public SensorDescriptor Descriptor => _desc;
    public TileVariant Variant => _variant;

    /// <summary>Name shown on the tile. Defaults to the sensor's own label,
    /// overridden by a user rename.</summary>
    public string DisplayLabel
    {
        get => _labelFull;
        // Middle-trimmed like every other name in iCUE, and re-applied here
        // rather than at construction because a rename arrives after it.
        set { _labelFull = value; TextFit.Middle(_label, value); }
    }

    public bool ShowChart
    {
        get => _chart.IsVisible;
        set => _chart.IsVisible = value;
    }

    /// <summary>The three-dot menu iCUE puts in the corner. Assigning it also
    /// wires the hover behaviour: the dots replace the type icon on hover, they
    /// are not both visible at once.</summary>
    public MenuFlyout? Menu
    {
        get => _kebab.Flyout as MenuFlyout;
        set { _kebab.Flyout = value; ContextFlyout = value; }
    }

    public SensorTile(SensorDescriptor desc, TemperatureScale scale, TileVariant variant = TileVariant.Dashboard)
    {
        _desc = desc;
        _variant = variant;
        var kindColor = NexusManager.Render.Theme.Parse(SensorPalette.DefaultColor(desc.Kind), SkiaSharp.SKColors.White);
        _tint = Color.FromRgb(kindColor.Red, kindColor.Green, kindColor.Blue);
        _decimals = SensorPalette.DefaultDecimals(desc.Kind);

        Background = Style.TileBrush;
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        // The chart runs flush to the bottom edge, so it has to be clipped to the
        // rounded corners or it squares them off again.
        ClipToBounds = true;
        Width = 194;
        Height = variant == TileVariant.Home ? 96 : 92;

        _value.Foreground = new SolidColorBrush(_tint);
        _unit.Foreground = new SolidColorBrush(_tint);
        _chart.Tint = _tint;
        _chart.Min = desc.SuggestedMin;
        _chart.Max = desc.SuggestedMax;
        _chart.History = _history;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _label = new TextBlock
        {
            Text = desc.Label, Foreground = Style.TextBrush, FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_label, 0); head.Children.Add(_label);
        _labelFull = desc.Label;
        TextFit.Middle(_label, desc.Label);

        _icon = IconGlyph.For(desc.Kind, _tint);
        Grid.SetColumn(_icon, 1); head.Children.Add(_icon);

        _kebab = new Button
        {
            Content = "⋮",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Style.TextBrush,
            Padding = new Thickness(4, 0, 0, 0),
            FontSize = 14,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -4, 0),
        };
        Grid.SetColumn(_kebab, 1); head.Children.Add(_kebab);

        // Hover swaps the type glyph for the dots, which is what iCUE does and
        // why their guide says the dots "appear" - there is no permanent affordance.
        PointerEntered += (_, _) => { if (_kebab.Flyout is not null) { _icon.IsVisible = false; _kebab.IsVisible = true; } };
        PointerExited  += (_, _) => { if (!_kebab.IsPointerOver) { _kebab.IsVisible = false; _icon.IsVisible = true; } };

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
        valueRow.Children.Add(_value);
        valueRow.Children.Add(_unit);

        var body = new StackPanel { Margin = new Thickness(9, 7, 9, 0) };
        body.Children.Add(head);
        if (variant == TileVariant.Home)
        {
            // Device name sits between the label and the reading, middle-trimmed
            // the way iCUE does it ("NVIDIA G...RTX 3080") - sensor names differ
            // at the end as often as at the start.
            TextFit.Middle(_third, desc.Device);
            _third.Margin = new Thickness(0, 1, 0, 0);
            body.Children.Add(_third);
            valueRow.Margin = new Thickness(0, 1, 0, 0);
            body.Children.Add(valueRow);
        }
        else
        {
            valueRow.Margin = new Thickness(0, 2, 0, 0);
            body.Children.Add(valueRow);
            body.Children.Add(_third);
        }

        var dock = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(body, Dock.Top);
        dock.Children.Add(body);
        DockPanel.SetDock(_chart, Dock.Bottom);
        dock.Children.Add(_chart);
        Child = dock;

        _unit.Text = Units.Symbol(desc.Unit, scale);
    }

    public void Update(double value, TemperatureScale scale)
    {
        if (double.IsNaN(value)) { _value.Text = "--"; return; }
        _history.Add(value);

        _value.Text = value.ToString("F" + _decimals, System.Globalization.CultureInfo.InvariantCulture);
        _unit.Text = Units.Symbol(_desc.Unit, scale);
        if (_variant == TileVariant.Dashboard)
            _third.Text = _history.HasRange
                ? $"↓ {_history.Min:F0}   ↑ {_history.Max:F0}"
                : "";

        if (_desc.Unit == Unit.Celsius)
        {
            var (lo, hi) = Units.ConvertRange(_desc.SuggestedMin, _desc.SuggestedMax, _desc.Unit, scale);
            _chart.Min = lo; _chart.Max = hi;
        }
        _chart.InvalidateVisual();
    }
}

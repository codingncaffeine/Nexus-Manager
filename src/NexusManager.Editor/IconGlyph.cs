using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NexusManager.Sensors;

namespace NexusManager.Editor;

/// <summary>
/// The small type glyph in a tile's corner. Shapes mirror the ones drawn on the
/// panel itself (SensorIcon), so a sensor looks the same in both places.
/// </summary>
public static class IconGlyph
{
    public static Control For(SensorKind kind, Color tint) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(PathFor(kind)),
        Stroke = new SolidColorBrush(tint),
        StrokeThickness = 1.3,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Fill = Filled(kind) ? new SolidColorBrush(tint) : null,
        Width = 12, Height = 12,
        Stretch = Stretch.Uniform,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
    };

    private static bool Filled(SensorKind k) => k is SensorKind.Voltage or SensorKind.Power;

    private static string PathFor(SensorKind kind) => kind switch
    {
        // thermometer: stem and bulb
        SensorKind.Temperature =>
            "M6,1 L6,7 M4.2,7.4 A2.1,2.1 0 1,0 7.8,7.4 A2.1,2.1 0 1,0 4.2,7.4",
        // processor die with pins
        SensorKind.Load =>
            "M3.2,3.2 H8.8 V8.8 H3.2 Z M4.6,1.2 V3.2 M7.4,1.2 V3.2 " +
            "M4.6,8.8 V10.8 M7.4,8.8 V10.8 M1.2,4.6 H3.2 M1.2,7.4 H3.2 " +
            "M8.8,4.6 H10.8 M8.8,7.4 H10.8",
        // lightning bolt
        SensorKind.Voltage or SensorKind.Power =>
            "M7,1 L3.6,6.5 H5.8 L4.8,11 L8.4,5 H6.2 Z",
        // fan
        SensorKind.Fan =>
            "M6,1.4 A4.6,4.6 0 1,1 5.99,1.4 M6,4.6 L6,2.2 M6,7.4 L4,9 M6,7.4 L8,9",
        // memory module
        SensorKind.Memory =>
            "M1.4,3.2 H10.6 V8 H1.4 Z M3,8 V10.4 M5,8 V10.4 M7,8 V10.4 M9,8 V10.4",
        // drive platters
        SensorKind.Disk =>
            "M1.4,3.4 A4.6,1.6 0 1,0 10.6,3.4 A4.6,1.6 0 1,0 1.4,3.4 " +
            "M1.4,3.4 V8.2 A4.6,1.6 0 0,0 10.6,8.2 V3.4",
        // up/down arrows
        SensorKind.Network =>
            "M3.8,10.4 V1.8 M2,4 L3.8,1.8 L5.6,4 M8.2,1.6 V10.2 M6.4,8 L8.2,10.2 L10,8",
        // waveform
        SensorKind.Frequency =>
            "M1.2,6 C2.6,1 3.6,11 5,6 C6.4,1.6 7.6,10.4 9,6 C9.8,4 10.4,6 10.8,6",
        _ => "M6,1.6 A4.4,4.4 0 1,0 6.01,1.6",
    };
}

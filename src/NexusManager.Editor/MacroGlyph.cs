using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NexusManager.Actions;

namespace NexusManager.Editor;

/// <summary>
/// The small glyphs on a macro row's chips.
///
/// Vector, like every other icon in this app (SensorIcon, IconGlyph) - no image
/// assets, and nothing that depends on a font carrying U+2328 or U+231B. A
/// missing glyph in a font renders as a tofu box, silently, and the strip
/// invites people to install their own faces.
///
/// The type glyphs copy iCUE's: an outlined keyboard and an hourglass. The
/// STATE glyphs deliberately do not. iCUE draws two keycap silhouettes, one
/// squashed and one raised, which take a 5x zoom to tell apart even in
/// Corsair's own screenshot - and it has only two states to show. We have
/// three, because Tap is ours (D70), so the state chip carries an arrow pair
/// that reads at 14px AND the word beside it.
/// </summary>
public static class MacroGlyph
{
    /// <summary>The type chip's glyph: what KIND of event this row is.</summary>
    public static Control Type(MacroStepKind kind) => kind == MacroStepKind.Delay
        ? Stroked("M2.5,1 H11.5 M2.5,11 H11.5 "
                + "M3.5,1 V3 L7,6 L3.5,9 V11 M10.5,1 V3 L7,6 L10.5,9 V11")
        : Keyboard();

    /// <summary>The state chip's glyph: press, release, or both.</summary>
    public static Control State(MacroStepKind kind) => kind switch
    {
        // down onto the baseline: the key goes down and stays there
        MacroStepKind.KeyDown => Stroked("M7,2 V7.4 M4.6,5 L7,7.4 L9.4,5 M1.5,10 H12.5"),
        // up off the baseline
        MacroStepKind.KeyUp   => Stroked("M7,7.4 V2 M4.6,4.4 L7,2 L9.4,4.4 M1.5,10 H12.5"),
        // down and up: press and release, which is what a Tap is
        _ => Stroked("M4.5,2 V6.6 M2.9,4.6 L4.5,6.6 L6.1,4.6 "
                   + "M9.5,6.6 V2 M7.9,4 L9.5,2 L11.1,4 M1.5,10 H12.5"),
    };

    private static Control Stroked(string data) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(data),
        Stroke = MacroStyle.ChipTextBrush,
        StrokeThickness = 1.2,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Width = 14, Height = 12,
        Stretch = Stretch.Uniform,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
    };

    /// <summary>
    /// An outlined keyboard: a 1px frame with keys cut into it.
    ///
    /// Drawn as ONE even-odd geometry rather than a stroked outline plus filled
    /// dots, so the frame stays exactly one unit thick at any scale. Even-odd
    /// counts crossings: a point in a key is inside the outer rect, the inner
    /// rect and the key itself - three, so odd, so filled. A point in the
    /// blank field is inside two, so even, so a hole.
    /// </summary>
    private static Control Keyboard()
    {
        var sb = new System.Text.StringBuilder("M0,0 H16 V11 H0 Z M1,1 H15 V10 H1 Z");
        foreach (double y in new[] { 2.4, 4.7 })
            for (double x = 2.4; x < 14; x += 2.4)
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $" M{x},{y} H{x + 1.4} V{y + 1.4} H{x} Z");
        sb.Append(" M4.6,7 H11.4 V8.4 H4.6 Z");     // the space bar

        return new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(sb.ToString()),
            Fill = MacroStyle.ChipTextBrush,
            Width = 15, Height = 11,
            Stretch = Stretch.Uniform,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
    }

    /// <summary>The kebab, drawn rather than typed for the same reason.</summary>
    public static Control Kebab() => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse("M1,0.6 A0.85,0.85 0 1,0 1.01,0.6 M1,4 A0.85,0.85 0 1,0 1.01,4 "
                            + "M1,7.4 A0.85,0.85 0 1,0 1.01,7.4"),
        Fill = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
        Width = 3, Height = 10,
        Stretch = Stretch.Uniform,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
    };
}

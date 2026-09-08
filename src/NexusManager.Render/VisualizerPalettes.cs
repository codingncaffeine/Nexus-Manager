using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Per-band colour ramps.
///
/// ⛔ EVERY ramp here is provisional until it has been seen on the panel. This
/// display's green primary is yellow-shifted - full green reads LIME and
/// blue+green reads WHITE rather than cyan (D12/D54) - so a ramp that is
/// balanced on a monitor is not balanced on the glass. Judge on the hardware.
/// </summary>
public static class VisualizerPalettes
{
    /// <summary>
    /// Builds the per-band colours for a palette. Called on resize or palette
    /// change, never per frame.
    /// </summary>
    public static SKColor[] Build(VisualizerPalette palette, int bands, SKColor solid, Theme theme)
    {
        var colors = new SKColor[bands];
        for (int b = 0; b < bands; b++)
        {
            float t = bands <= 1 ? 0f : (float)b / (bands - 1);
            colors[b] = palette switch
            {
                // Stops at 200 rather than wrapping past blue, so the top end
                // cannot loop back to red and read as bass.
                VisualizerPalette.Frequency => SKColor.FromHsl(200f * t, 85f, 55f),

                // WMP's "Ocean Mist": deep blue into cyan, nothing warm.
                VisualizerPalette.OceanMist => Lerp(
                    new SKColor(0x10, 0x3C, 0xA0), new SKColor(0x50, 0xE0, 0xE8), t),

                // WMP's "Fire Storm": red through orange to yellow.
                VisualizerPalette.FireStorm => t < 0.5f
                    ? Lerp(new SKColor(0xB0, 0x10, 0x08), new SKColor(0xF0, 0x70, 0x00), t * 2f)
                    : Lerp(new SKColor(0xF0, 0x70, 0x00), new SKColor(0xFF, 0xE0, 0x30), (t - 0.5f) * 2f),

                // Flat green, as WMP's own "Bars" draws it - checked against a
                // WMP 11 screenshot, where every bar is one colour and only the
                // caps differ.
                VisualizerPalette.Emerald => new SKColor(0x3C, 0xD8, 0x3C),

                // Meter colouring is by HEIGHT, not by band, so the per-band
                // array is uniform and the renderer tints per row instead.
                VisualizerPalette.Meter => new SKColor(0x00, 0xC8, 0x18),

                VisualizerPalette.Theme => theme.ValueColor,
                _ => solid,
            };
        }
        return colors;
    }

    /// <summary>Height tint for <see cref="VisualizerPalette.Meter"/>: green,
    /// amber, red as a bar climbs. Returns null for every other palette so the
    /// caller keeps its per-band colour.</summary>
    public static SKColor? ByHeight(VisualizerPalette palette, float t, Theme theme)
    {
        if (palette != VisualizerPalette.Meter) return null;
        return t > 0.85f ? theme.HotColor
             : t > 0.65f ? theme.WarnColor
             : new SKColor(0x00, 0xC8, 0x18);
    }

    public static SKColor Lerp(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red   + (b.Red   - a.Red)   * t),
            (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue  + (b.Blue  - a.Blue)  * t));
    }

    /// <summary>Fully saturated hue wheel, for the effect modes that cycle.</summary>
    public static SKColor Hue(float degrees, float lightness = 55f) =>
        SKColor.FromHsl(((degrees % 360f) + 360f) % 360f, 90f, lightness);
}

using System.Text.Json.Serialization;
using NexusManager.Sensors;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>One sensor readout, modelled on an iCUE sensor tile.</summary>
public sealed class ModuleSpec
{
    /// <summary>Sensor key, e.g. "cpu.temp", "cpu.load", "gpu.temp".</summary>
    public string Source { get; set; } = "cpu.load";

    /// <summary>Drives the default colour, unit and decimal places.</summary>
    public SensorKind Kind { get; set; } = SensorKind.Other;

    /// <summary>Metric name — iCUE's first line, e.g. "Package", "Temp #1", "Load".</summary>
    public string Label { get; set; } = "";

    /// <summary>Device name — iCUE's second line, e.g. "AMD Ryzen 9 9950X3D".
    /// Middle-truncated when it will not fit. Only drawn if ShowDevice.</summary>
    public string Device { get; set; } = "";

    /// <summary>Null takes the type default (°C, %, V ...).</summary>
    public string? Unit { get; set; }

    /// <summary>Null takes the type default: 2 for temperature and voltage, else 0.</summary>
    public int? Decimals { get; set; }

    /// <summary>Fixed chart scale. Never auto-ranged — see <see cref="Sparkline"/>.</summary>
    public double Min { get; set; }
    public double Max { get; set; } = 100;

    /// <summary>Optional alarm thresholds. null (the default) disables them, so a
    /// module keeps its type colour at all times, as iCUE does.</summary>
    public double? Warn { get; set; }
    public double? Hot { get; set; }

    /// <summary>Share of the strip. Widths are distributed in proportion to weight,
    /// so {2,1,1} gives the first module half the strip.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Overrides. Null falls back to the type colour, then the theme.</summary>
    public string? ValueColor { get; set; }
    public string? ChartColor { get; set; }
    public string? LabelColor { get; set; }

    public bool ShowChart { get; set; } = true;

    /// <summary>
    /// Range the CHART follows the data instead of the fixed Min/Max above.
    ///
    /// ⛔ This reverses an earlier decision, and the earlier reasoning was not
    /// wrong so much as incomplete. Fixed scale was chosen so that a CPU idling
    /// between 3% and 5% could not look like one swinging 0-100%: magnitude is
    /// the point when monitoring. True — but on a 48px strip a drive moving
    /// 36.8 to 37.1 degrees inside a 20-95 range moves ONE FIFTH OF A PIXEL, so
    /// every chart was a flat line and told the user nothing at all.
    ///
    /// Measured against iCUE: a tile stating min 41 / max 45 - four degrees -
    /// has a curve travelling THIRTEEN pixels. On a fixed 20-95 scale those four
    /// degrees are 1.4px. iCUE auto-ranges, and that is why its charts have
    /// shape. Magnitude is not lost either: the reading and its min/max are
    /// printed right beside the chart.
    /// </summary>
    public bool AutoScale { get; set; } = true;

    /// <summary>
    /// Floor on the auto-ranged span, as a fraction of Max-Min. Without it a
    /// perfectly still sensor would have its last digit of noise stretched to
    /// full height and the chart would look like a seismograph.
    /// </summary>
    public double MinSpanFraction { get; set; } = 0.06;
    public bool ShowValue { get; set; } = true;
    public bool ShowDevice { get; set; }

    /// <summary>Type glyph in the corner, as iCUE puts on each sensor tile.</summary>
    public bool ShowIcon { get; set; } = true;

    [JsonIgnore] public string EffectiveUnit => Unit ?? "";
    [JsonIgnore] public int EffectiveDecimals => Decimals ?? SensorPalette.DefaultDecimals(Kind);
    [JsonIgnore] public string EffectiveColor => ValueColor ?? SensorPalette.DefaultColor(Kind);
}

/// <summary>A full screen: one theme, N modules.</summary>
public sealed class ScreenSpec
{
    public string Name { get; set; } = "default";
    public Theme Theme { get; set; } = new();
    public List<ModuleSpec> Modules { get; set; } = [];

    /// <summary>Colour, still image, or animation behind the modules. Corsair
    /// express animation as nothing more than "the background file is a GIF",
    /// and pointing this at one does exactly that.</summary>
    public BackgroundSpec Background { get; set; } = new();

    /// <summary>Touch buttons on this screen. Share the strip with Modules.</summary>
    public List<ButtonSpec> Buttons { get; set; } = [];

    /// <summary>Music visualizers on this screen. Share the strip's single
    /// weight budget with Modules and Buttons, so a screen with three readouts
    /// and one visualizer gives the visualizer a quarter, not a half.
    ///
    /// A screen carrying one of these makes the renderer want audio, which is
    /// what tells the daemon and the editor to open a capture at all - nothing
    /// spawns parec unless a screen actually asks for it.</summary>
    public List<VisualizerSpec> Visualizers { get; set; } = [];

    [JsonIgnore] public bool NeedsAudio => Visualizers.Count > 0;

    /// <summary>
    /// Narrowest module that stays readable. A 3-digit value with decimals at the
    /// default type size needs roughly this much before the number collides with
    /// its own unit. The layout reports violations rather than silently rendering
    /// something illegible.
    /// </summary>
    /// <summary>
    /// Frame rate for THIS screen, overriding the set's. Null takes the global
    /// value.
    ///
    /// 24 fps is right for a readout that changes once a second and marginal
    /// for audio transients, so a screen carrying a visualizer wants 30 while
    /// the sensor screens stay where they are and pay nothing.
    /// </summary>
    public int? TargetFps { get; set; }

    public int EffectiveFps(int fallback) =>
        Math.Clamp(TargetFps ?? (NeedsAudio ? 30 : fallback), 1, 65);

    public int MinModuleWidth { get; set; } = 72;

    /// <summary>
    /// Narrowest BUTTON that stays usable. Lower than the readout minimum on
    /// purpose: these are different constraints. A readout has to fit a
    /// three-digit value beside its unit; a button only has to be hittable, and
    /// an icon-only one is fine well below that. D11 puts a comfortable target
    /// near 106px, so this is the floor, not the recommendation.
    /// </summary>
    public int MinButtonWidth { get; set; } = 56;

    /// <summary>
    /// Empty strip before the first cell and after the last, in the same weight
    /// units as the cells.
    ///
    /// Without these a screen's cells always fill all 640px, so a screen with a
    /// SINGLE button had to stretch it edge to edge and there was no boundary to
    /// drag - one cell has no neighbour to resize against. iCUE has the same
    /// shape for the same reason: six fixed slots, and a screen using fewer
    /// leaves the rest as background.
    /// </summary>
    public double LeadWeight { get; set; }
    public double TrailWeight { get; set; }

    [JsonIgnore] public int MaxModules => NexusCanvas.Width / MinModuleWidth;
}

public readonly record struct ModuleLayout(ModuleSpec Spec, SKRect Rect, int Index);

public readonly record struct VisualizerLayout(VisualizerSpec Spec, SKRect Rect, int Index);

public static class ScreenLayout
{
    /// <summary>Distributes the strip across modules in proportion to their weights.</summary>
    /// <param name="tooNarrow">Modules that came out below the minimum width. They
    /// are still returned — the caller decides whether to warn or refuse.</param>
    /// <summary>
    /// Lays out modules AND buttons across the one 640px strip.
    ///
    /// They share a single weight budget rather than getting half the strip
    /// each: a screen with four readouts and one button should give the button
    /// a fifth, not a half.
    /// </summary>
    public static (List<ModuleLayout> Modules, List<ButtonLayout> Buttons,
                  List<VisualizerLayout> Visualizers) ComputeAll(
        ScreenSpec screen, out List<string> tooNarrow)
    {
        tooNarrow = [];
        var modules = new List<ModuleLayout>();
        var buttons = new List<ButtonLayout>();
        var visuals = new List<VisualizerLayout>();
        int cells = screen.Modules.Count + screen.Buttons.Count + screen.Visualizers.Count;
        if (cells == 0) return (modules, buttons, visuals);

        // Lead and trail are part of the same budget, so cells keep their
        // proportions as the surrounding space grows.
        double lead = Math.Max(0, screen.LeadWeight);
        double trail = Math.Max(0, screen.TrailWeight);
        double total = lead + trail
                     + screen.Modules.Sum(m => Math.Max(0.0001, m.Weight))
                     + screen.Buttons.Sum(b => Math.Max(0.0001, b.Weight))
                     + screen.Visualizers.Sum(v => Math.Max(0.0001, v.Weight));
        if (total <= 0) return (modules, buttons, visuals);
        // Cells start after the leading gap.
        float x = (float)(NexusCanvas.Width * lead / total);
        int placed = 0;
        // The last CELL only absorbs the rounding when there is no trailing gap;
        // otherwise it would swallow the gap it is supposed to leave.
        bool lastFills = trail <= 0;

        // Visualizers first, so a screen reads left-to-right as spectacle then
        // data. ⛔ Cells are still grouped BY TYPE rather than by an explicit
        // order index, so a module cannot sit between two visualizers - the
        // inherited limitation noted in D50, now one group wider.
        foreach (var v in screen.Visualizers)
        {
            float w = Advance(ref x, v.Weight, total, lastFills && ++placed == cells);
            visuals.Add(new VisualizerLayout(v, Cell(x, w), visuals.Count));
        }
        foreach (var m in screen.Modules)
        {
            float w = Advance(ref x, m.Weight, total, lastFills && ++placed == cells);
            modules.Add(new ModuleLayout(m, Cell(x, w), modules.Count));
            Narrow(tooNarrow, screen, w, string.IsNullOrEmpty(m.Label) ? m.Source : m.Label);
        }
        foreach (var b in screen.Buttons)
        {
            float w = Advance(ref x, b.Weight, total, lastFills && ++placed == cells);
            buttons.Add(new ButtonLayout(b, Cell(x, w), buttons.Count));
            NarrowButton(tooNarrow, screen, w, string.IsNullOrEmpty(b.Label) ? "button" : b.Label);
        }
        return (modules, buttons, visuals);
    }

    /// <summary>Advances the cursor and returns the cell width. The LAST cell
    /// absorbs rounding so the strip is filled to the pixel.</summary>
    private static float Advance(ref float x, double weight, double total, bool last)
    {
        float w = last
            ? NexusCanvas.Width - x
            : (float)Math.Round(NexusCanvas.Width * Math.Max(0.0001, weight) / total);
        x += w;
        return w;
    }

    private static SKRect Cell(float xAfter, float w) =>
        new(xAfter - w, 0, xAfter, NexusCanvas.Height);

    /// <summary>
    /// Which button a tap at <paramref name="x"/> hits, or -1. X is the only
    /// axis the panel reports and buttons are full-height cells, so this is a
    /// range check - but it lives here, shared by the tray app and the daemon,
    /// so the two can never disagree about where a press landed.
    /// </summary>
    public static int HitTest(IReadOnlyList<ButtonLayout> buttons, float x)
    {
        for (int i = 0; i < buttons.Count; i++)
            if (x >= buttons[i].Rect.Left && x < buttons[i].Rect.Right) return i;
        return -1;
    }

    private static void NarrowButton(List<string> into, ScreenSpec screen, float w, string name)
    {
        if (w < screen.MinButtonWidth)
            into.Add($"button '{name}' is {w:0}px, below the {screen.MinButtonWidth}px minimum");
    }

    private static void Narrow(List<string> into, ScreenSpec screen, float w, string name)
    {
        if (w < screen.MinModuleWidth)
            into.Add($"'{name}' is {w:0}px, below the {screen.MinModuleWidth}px minimum");
    }

    /// <summary>Modules only. Kept for callers that do not draw buttons.</summary>
    public static List<ModuleLayout> Compute(ScreenSpec screen, out List<string> tooNarrow)
        => ComputeAll(screen, out tooNarrow).Modules;

}

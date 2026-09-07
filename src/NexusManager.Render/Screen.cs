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

    /// <summary>
    /// Narrowest module that stays readable. A 3-digit value with decimals at the
    /// default type size needs roughly this much before the number collides with
    /// its own unit. The layout reports violations rather than silently rendering
    /// something illegible.
    /// </summary>
    public int MinModuleWidth { get; set; } = 72;

    [JsonIgnore] public int MaxModules => NexusCanvas.Width / MinModuleWidth;
}

public readonly record struct ModuleLayout(ModuleSpec Spec, SKRect Rect, int Index);

public static class ScreenLayout
{
    /// <summary>Distributes the strip across modules in proportion to their weights.</summary>
    /// <param name="tooNarrow">Modules that came out below the minimum width. They
    /// are still returned — the caller decides whether to warn or refuse.</param>
    public static List<ModuleLayout> Compute(ScreenSpec screen, out List<string> tooNarrow)
    {
        tooNarrow = [];
        var result = new List<ModuleLayout>();
        if (screen.Modules.Count == 0) return result;

        double total = screen.Modules.Sum(m => Math.Max(0.0001, m.Weight));
        float x = 0;

        for (int i = 0; i < screen.Modules.Count; i++)
        {
            var m = screen.Modules[i];
            // Last module absorbs rounding so the strip is filled exactly.
            float w = i == screen.Modules.Count - 1
                ? NexusCanvas.Width - x
                : (float)Math.Round(NexusCanvas.Width * Math.Max(0.0001, m.Weight) / total);

            result.Add(new ModuleLayout(m, new SKRect(x, 0, x + w, NexusCanvas.Height), i));

            if (w < screen.MinModuleWidth)
                tooNarrow.Add($"'{(string.IsNullOrEmpty(m.Label) ? m.Source : m.Label)}' " +
                              $"is {w:0}px, below the {screen.MinModuleWidth}px minimum");
            x += w;
        }
        return result;
    }
}

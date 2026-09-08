using System.Globalization;
using NexusManager.Sensors;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Draws a <see cref="ScreenSpec"/> onto a <see cref="NexusCanvas"/>.
///
/// Modelled on an iCUE sensor tile, compressed into a 48px strip:
///
///     Temp #1                    [icon]
///     40.00 °C  v40 ^42
///     [========= chart =========]
///
/// iCUE stacks label / value / min-max / chart as four rows in a ~100px tile.
/// At 48px there is no room for that, so the min-max pair sits inline beside the
/// reading and the chart runs flush to the bottom edge.
/// </summary>
public sealed class ScreenRenderer : IDisposable
{
    private readonly Theme _theme;
    private readonly SKTypeface _face;
    private readonly SKTypeface _faceVal;
    private readonly SKFont _label;
    private readonly SKFont _device;
    private readonly SKFont _value;
    private readonly SKFont _valueCompact;
    private readonly SKFont _unit;
    private readonly SKFont _minmax;
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly SKPaint _flat = new() { IsAntialias = false };
    /// <summary>Holds the decoded background between frames. Decoding a GIF
    /// per frame costs far more than the whole 41 ms frame budget.</summary>
    private readonly BackgroundPainter _background = new();
    private readonly ButtonRenderer _buttons;

    public ScreenRenderer(Theme theme)
    {
        _theme   = theme;
        _face    = Face(theme.FontFamily, theme.CaptionBold);
        _faceVal = Face(theme.FontFamily, theme.ValueBold);

        _buttons = new ButtonRenderer(theme);
        _label        = new SKFont(_face,    theme.CaptionSize);
        _device       = new SKFont(_face,    theme.CaptionSize - 2f);
        _value        = new SKFont(_faceVal, theme.ValueSize);
        _valueCompact = new SKFont(_faceVal, theme.ValueSize * 0.72f);
        _unit         = new SKFont(_faceVal, theme.CaptionSize);
        _minmax       = new SKFont(_face,    theme.CaptionSize - 3f);
    }

    public void Draw(
        NexusCanvas canvas,
        IReadOnlyList<ModuleLayout> layout,
        IReadOnlyList<History> histories,
        Func<string, double> read,
        BackgroundSpec? background = null,
        TimeSpan elapsed = default,
        IReadOnlyList<ButtonLayout>? buttons = null,
        int pressedButton = -1,
        TimeSpan flashUntil = default)
    {
        // Background first: colour, still image, or a running animation. The
        // elapsed time is wall-clock, so a GIF plays at ITS rate rather than
        // being tied to whatever the panel frame rate is set to.
        _background.Draw(canvas.Canvas, background, _theme.BackgroundColor, elapsed);

        for (int i = 0; i < layout.Count; i++)
        {
            var (spec, rect, _) = layout[i];
            double reading = read(spec.Source);
            History? hist = i < histories.Count ? histories[i] : null;

            // Colour says what KIND of sensor this is, as iCUE does. Alarm
            // thresholds are opt-in and override only when configured.
            SKColor tint = Theme.Parse(spec.EffectiveColor, _theme.ValueColor);
            if (spec.Hot is { } hot && reading >= hot) tint = _theme.HotColor;
            else if (spec.Warn is { } warn && reading >= warn) tint = _theme.WarnColor;

            const float IconSize = 9f;
            bool roomForIcon = spec.ShowIcon && rect.Width >= 56;
            if (roomForIcon)
                SensorIcon.Draw(canvas.Canvas, spec.Kind,
                    new SKRect(rect.Right - IconSize - 5, 2, rect.Right - 5, 2 + IconSize),
                    tint);

            // Chart. It starts at ChartTop rather than the old 34px, because the
            // reading now sits BESIDE the label instead of under it and that
            // whole band is free - which nearly doubles the height a curve has
            // to move in.
            if (spec.ShowChart && hist is not null)
            {
                SKColor chart = spec.ChartColor is not null
                    ? Theme.Parse(spec.ChartColor, tint)
                    : _theme.TintChartWithState ? tint : _theme.ChartLineColor;

                var (lo, hi) = ChartRange(hist, spec);
                Sparkline.Draw(canvas.Canvas, hist,
                    new SKRect(rect.Left + 3, ChartTop, rect.Right - 3, NexusCanvas.Height),
                    lo, hi,
                    chart.WithAlpha(_theme.ChartLineAlpha),
                    _theme.ChartFillAlpha);
            }

            // Label and reading share one baseline. The icon keeps its corner,
            // so the run is budgeted around it.
            float budget = rect.Width - 12 - (roomForIcon ? IconSize + 4 : 0);
            float right = rect.Left + 6 + budget;
            float x = rect.Left + 6;

            if (!string.IsNullOrEmpty(spec.Label))
            {
                _text.Color = spec.LabelColor is not null
                    ? Theme.Parse(spec.LabelColor, _theme.CaptionColor)
                    : _theme.CaptionColor;
                // The label yields first: a long sensor name must not push the
                // number it labels off the cell.
                string label = Fit(spec.Label, _label, budget * 0.5f);
                canvas.Canvas.DrawText(label, x, TextBaseline, SKTextAlign.Left, _label, _text);
                x += _label.MeasureText(label) + 6;
            }

            if (spec.ShowValue)
            {
                string shown = reading.ToString(
                    "F" + spec.EffectiveDecimals.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture);
                string unit = spec.EffectiveUnit;

                var face = _value;
                if (x + face.MeasureText(shown) + _unit.MeasureText(unit) > right)
                    face = _valueCompact;

                _text.Color = tint;
                canvas.Canvas.DrawText(shown, x, TextBaseline, SKTextAlign.Left, face, _text);
                x += face.MeasureText(shown) + 2;

                if (!string.IsNullOrEmpty(unit))
                {
                    // Unit takes the value's colour, as iCUE does — not grey.
                    canvas.Canvas.DrawText(unit, x, TextBaseline, SKTextAlign.Left, _unit, _text);
                    x += _unit.MeasureText(unit) + 5;
                }

                if (_theme.ShowMinMax && hist is { HasRange: true })
                {
                    string range = $"↓{Round(hist.Min, spec)} ↑{Round(hist.Max, spec)}";
                    if (x + _minmax.MeasureText(range) <= right)
                    {
                        _text.Color = _theme.MinMaxColor;
                        canvas.Canvas.DrawText(range, x, TextBaseline, SKTextAlign.Left, _minmax, _text);
                    }
                }
            }

            // Divider between cells, skipped before the first.
            if (i > 0)
            {
                _flat.Color = _theme.DividerColor;
                canvas.Canvas.DrawRect(rect.Left, 4, 1, NexusCanvas.Height - 8, _flat);
            }
        }

        // Buttons last, so they sit above the readouts and their outline is
        // never clipped by a neighbouring chart.
        if (buttons is { Count: > 0 })
            _buttons.Draw(canvas.Canvas, buttons, _theme, pressedButton, elapsed, flashUntil);
    }

    /// <summary>Baseline shared by a cell's label and its reading. They sit on
    /// one line so the band underneath belongs to the chart.</summary>
    private const float TextBaseline = 18f;

    /// <summary>Top of the chart band. Was 34 when the reading had its own row,
    /// which left a curve 14px to move in on a 48px panel.</summary>
    private const float ChartTop = 22f;

    /// <summary>
    /// The value range a chart is drawn against.
    ///
    /// ⛔ Auto-ranging follows the WINDOW, never History.Min/Max: those are the
    /// lifetime extremes, and a chart scaled to them goes permanently flat the
    /// first time a spike widens them.
    ///
    /// The floor is what stops the other failure. A sensor sitting still still
    /// jitters in its last digit, and stretching that to full height would draw
    /// a seismograph out of nothing - so the span can never fall below a
    /// fraction of the configured range, and a still sensor stays visibly still.
    /// </summary>
    public static (double Lo, double Hi) ChartRange(History hist, ModuleSpec spec)
    {
        if (!spec.AutoScale) return (spec.Min, spec.Max);

        var (lo, hi) = hist.Window();
        if (double.IsNaN(lo) || double.IsNaN(hi)) return (spec.Min, spec.Max);

        double floor = Math.Abs(spec.Max - spec.Min)
                       * Math.Clamp(spec.MinSpanFraction, 0.001, 1.0);
        double span = hi - lo;
        if (span < floor)
        {
            double mid = (lo + hi) / 2;
            return (mid - floor / 2, mid + floor / 2);
        }
        // Headroom, so a peak does not sit exactly on the top edge and read as
        // clipped when it is merely the highest sample so far.
        double pad = span * 0.15;
        return (lo - pad, hi + pad);
    }

    private static string Round(double v, ModuleSpec spec) =>
        v.ToString("F" + Math.Min(1, spec.EffectiveDecimals).ToString(CultureInfo.InvariantCulture),
                   CultureInfo.InvariantCulture);

    /// <summary>
    /// Middle-truncates to fit, as iCUE does ("AMD Rad...raphics"). Keeping both
    /// ends beats a trailing ellipsis, because sensor names differ at the end
    /// ("Temp #1" vs "Temp #2") as often as at the start.
    /// </summary>
    private static string Fit(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.MeasureText(text) <= maxWidth) return text;
        if (maxWidth <= 0) return "";
        for (int keep = text.Length - 1; keep >= 2; keep--)
        {
            int head = (keep + 1) / 2, tail = keep - head;
            string c = string.Concat(text.AsSpan(0, head), "...", text.AsSpan(text.Length - tail));
            if (font.MeasureText(c) <= maxWidth) return c;
        }
        return "";
    }

    private static SKTypeface Face(string family, bool bold) =>
        SKTypeface.FromFamilyName(family,
            bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        ?? SKTypeface.Default;

    /// <summary>Why the background failed to load, if it did. Surfaced so a bad
    /// path reports itself instead of looking like a feature that does nothing.
    /// </summary>
    public string? BackgroundError => _background.LastError;

    /// <summary>The decoded background, for the editor to report frame count and
    /// memory against.</summary>
    public AnimatedImage? BackgroundImage => _background.Image;

    public void Dispose()
    {
        _label.Dispose(); _device.Dispose(); _value.Dispose(); _valueCompact.Dispose();
        _unit.Dispose(); _minmax.Dispose();
        _text.Dispose(); _flat.Dispose(); _face.Dispose(); _faceVal.Dispose();
        _buttons.Dispose();
        _background.Dispose();
    }
}

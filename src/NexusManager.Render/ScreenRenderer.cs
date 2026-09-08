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

            // Chart runs flush to the bottom edge, full tile width.
            if (spec.ShowChart && hist is not null)
            {
                SKColor chart = spec.ChartColor is not null
                    ? Theme.Parse(spec.ChartColor, tint)
                    : _theme.TintChartWithState ? tint : _theme.ChartLineColor;

                Sparkline.Draw(canvas.Canvas, hist,
                    new SKRect(rect.Left + 3, 34, rect.Right - 3, NexusCanvas.Height),
                    spec.Min, spec.Max,
                    chart.WithAlpha(_theme.ChartLineAlpha),
                    _theme.ChartFillAlpha);
            }

            float inner = rect.Width - 12 - (roomForIcon ? IconSize + 4 : 0);

            if (!string.IsNullOrEmpty(spec.Label))
            {
                _text.Color = spec.LabelColor is not null
                    ? Theme.Parse(spec.LabelColor, _theme.CaptionColor)
                    : _theme.CaptionColor;
                canvas.Canvas.DrawText(Fit(spec.Label, _label, inner), rect.Left + 6, 11,
                                       SKTextAlign.Left, _label, _text);
            }

            if (spec.ShowValue)
            {
                string shown = reading.ToString(
                    "F" + spec.EffectiveDecimals.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture);
                string unit = spec.EffectiveUnit;

                var face = _value;
                if (face.MeasureText(shown) + _unit.MeasureText(unit) + 10 > rect.Width - 12)
                    face = _valueCompact;

                float x = rect.Left + 6;
                _text.Color = tint;
                canvas.Canvas.DrawText(shown, x, 31, SKTextAlign.Left, face, _text);
                x += face.MeasureText(shown) + 2;

                if (!string.IsNullOrEmpty(unit))
                {
                    // Unit takes the value's colour, as iCUE does — not grey.
                    canvas.Canvas.DrawText(unit, x, 31, SKTextAlign.Left, _unit, _text);
                    x += _unit.MeasureText(unit) + 5;
                }

                if (_theme.ShowMinMax && hist is { HasRange: true })
                {
                    string range = $"\u2193{Round(hist.Min, spec)} \u2191{Round(hist.Max, spec)}";
                    if (x + _minmax.MeasureText(range) <= rect.Right - 4)
                    {
                        _text.Color = _theme.MinMaxColor;
                        canvas.Canvas.DrawText(range, x, 31, SKTextAlign.Left, _minmax, _text);
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

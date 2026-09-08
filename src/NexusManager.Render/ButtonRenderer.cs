using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Draws touch buttons on the strip.
///
/// A button has to read as PRESSABLE at a glance, on a 48px band, next to
/// sensor readouts that are not pressable. The distinction is carried by an
/// outlined, rounded cell with a centred label - readouts are left-aligned text
/// with a chart under them and no frame at all.
///
/// A press flashes the fill for a moment. On a panel with no travel and no
/// click, that flash is the only confirmation the touch registered.
/// </summary>
public sealed class ButtonRenderer : IDisposable
{
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly SKFont _label;
    private readonly SKTypeface _face;

    /// <summary>How long the press flash lasts. Long enough to see at 24 fps,
    /// short enough not to lag the finger.</summary>
    public static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(160);

    public ButtonRenderer(Theme theme)
    {
        _face = SKTypeface.FromFamilyName(theme.FontFamily, SKFontStyleWeight.SemiBold,
                                          SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                ?? SKTypeface.Default;
        _label = new SKFont(_face, Math.Max(9f, theme.CaptionSize));
    }

    /// <param name="flashUntil">When the pressed button's flash expires, keyed by
    /// button index; null for none.</param>
    public void Draw(
        SKCanvas canvas, IReadOnlyList<ButtonLayout> buttons, Theme theme,
        int pressedIndex, TimeSpan now, TimeSpan flashUntil)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            var (spec, rect, _) = buttons[i];
            bool lit = i == pressedIndex && now < flashUntil;

            var inner = new SKRect(rect.Left + 3, 4, rect.Right - 3, NexusCanvas.Height - 4);
            SKColor textColor = Theme.Parse(spec.Color, theme.CaptionColor);
            SKColor border = spec.Border is not null
                ? Theme.Parse(spec.Border, textColor)
                : textColor.WithAlpha(110);

            if (spec.Background is not null || lit)
            {
                SKColor bg = spec.Background is not null
                    ? Theme.Parse(spec.Background, SKColors.Transparent)
                    : textColor.WithAlpha(0);
                // The flash brightens whatever fill the button already has rather
                // than replacing it, so a coloured button stays recognisable.
                _fill.Color = lit ? Blend(bg, textColor, 0.45f) : bg;
                canvas.DrawRoundRect(inner, 4, 4, _fill);
            }

            _stroke.Color = lit ? textColor : border;
            canvas.DrawRoundRect(inner, 4, 4, _stroke);

            float centreY = NexusCanvas.Height / 2f;
            float textY = centreY + _label.Size * 0.36f;

            if (spec.Icon != ButtonIcon.None)
            {
                // Icon left, label right of it: stacking them would leave under
                // 20px for each on a 48px strip.
                const float IconSize = 13f;
                float iconX = inner.Left + 7;
                ButtonGlyph.Draw(canvas, spec.Icon,
                    new SKRect(iconX, centreY - IconSize / 2, iconX + IconSize, centreY + IconSize / 2),
                    textColor);

                if (!string.IsNullOrEmpty(spec.Label))
                {
                    _text.Color = textColor;
                    float available = inner.Right - (iconX + IconSize + 5) - 5;
                    canvas.DrawText(Fit(spec.Label, _label, available),
                        iconX + IconSize + 5, textY, SKTextAlign.Left, _label, _text);
                }
            }
            else if (!string.IsNullOrEmpty(spec.Label))
            {
                _text.Color = textColor;
                canvas.DrawText(Fit(spec.Label, _label, inner.Width - 10),
                    inner.MidX, textY, SKTextAlign.Center, _label, _text);
            }
        }
    }

    private static SKColor Blend(SKColor under, SKColor over, float amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Clamp(a + (b - a) * amount, 0, 255);
        return new SKColor(Mix(under.Red, over.Red), Mix(under.Green, over.Green),
                           Mix(under.Blue, over.Blue),
                           under.Alpha > 150 ? under.Alpha : (byte)150);
    }

    private static string Fit(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.MeasureText(text) <= maxWidth) return text;
        if (maxWidth <= 0) return "";
        for (int keep = text.Length - 1; keep >= 1; keep--)
        {
            string candidate = text[..keep] + "…";
            if (font.MeasureText(candidate) <= maxWidth) return candidate;
        }
        return "";
    }

    public void Dispose()
    {
        _fill.Dispose(); _stroke.Dispose(); _text.Dispose();
        _label.Dispose(); _face.Dispose();
    }
}

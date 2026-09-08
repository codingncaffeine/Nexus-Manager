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

            // A user image takes precedence over the vector glyph: someone who
            // supplied artwork meant it to be used.
            SKBitmap? art = string.IsNullOrWhiteSpace(spec.Image) ? null : ImageFor(spec.Image!);

            if (art is not null && string.IsNullOrEmpty(spec.Label))
            {
                // Picture-only: fill the cell. Inset by 2 so the outline still
                // reads as a button rather than the image having a hard edge.
                DrawImage(canvas, art, new SKRect(inner.Left + 2, inner.Top + 2,
                                                 inner.Right - 2, inner.Bottom - 2),
                          spec.ImageCover);
            }
            else if (art is not null)
            {
                const float ArtSize = 20f;
                float artX = inner.Left + 5;
                DrawImage(canvas, art,
                    new SKRect(artX, centreY - ArtSize / 2, artX + ArtSize, centreY + ArtSize / 2),
                    spec.ImageCover);
                _text.Color = textColor;
                float room = inner.Right - (artX + ArtSize + 5) - 5;
                canvas.DrawText(Fit(spec.Label, _label, room),
                    artX + ArtSize + 5, textY, SKTextAlign.Left, _label, _text);
            }
            else if (spec.Icon != ButtonIcon.None)
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


    /// <summary>
    /// Decoded button images, keyed by path AND last-write time so an edited file
    /// is picked up without a restart.
    ///
    /// ⛔ Cached because decoding is far too slow for a frame budget, and disposed
    /// on eviction and on Dispose: an SKBitmap wraps native memory the collector
    /// cannot see, so a cache that only ever grows is a leak with no GC pressure
    /// behind it.
    /// </summary>
    private readonly Dictionary<string, (long Stamp, SKBitmap? Image)> _images = new(StringComparer.Ordinal);

    private SKBitmap? ImageFor(string path)
    {
        long stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Remember(path, 0, null);
            stamp = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception)
        {
            return Remember(path, 0, null);
        }

        if (_images.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Image;

        SKBitmap? decoded = null;
        try
        {
            using var stream = File.OpenRead(path);
            decoded = SKBitmap.Decode(stream);
        }
        catch (Exception)
        {
            // A file that will not decode is remembered as "no image" against its
            // timestamp, so a broken path is not re-read every single frame.
            decoded = null;
        }
        return Remember(path, stamp, decoded);
    }

    private SKBitmap? Remember(string path, long stamp, SKBitmap? image)
    {
        if (_images.TryGetValue(path, out var old)) old.Image?.Dispose();
        _images[path] = (stamp, image);
        return image;
    }

    /// <summary>Draws an image into <paramref name="into"/>, preserving aspect.</summary>
    private void DrawImage(SKCanvas canvas, SKBitmap image, SKRect into, bool cover)
    {
        if (image.Width <= 0 || image.Height <= 0 || into.Width <= 0 || into.Height <= 0) return;

        float sx = into.Width / image.Width, sy = into.Height / image.Height;
        float scale = cover ? MathF.Max(sx, sy) : MathF.Min(sx, sy);
        float w = image.Width * scale, h = image.Height * scale;
        var dest = new SKRect(into.MidX - w / 2, into.MidY - h / 2,
                              into.MidX + w / 2, into.MidY + h / 2);

        // Cover overflows the cell by design, so it is clipped to it. Without the
        // clip a wide image paints straight over the neighbouring buttons.
        canvas.Save();
        canvas.ClipRect(into);
        canvas.DrawBitmap(image, dest, ButtonSampling, _fill);
        canvas.Restore();
    }

    private static readonly SKSamplingOptions ButtonSampling =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);

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
        foreach (var entry in _images.Values) entry.Image?.Dispose();
        _images.Clear();
        _label.Dispose(); _face.Dispose();
    }
}

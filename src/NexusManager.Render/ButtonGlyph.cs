using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Vector marks for buttons. Drawn, never loaded: at 13px on an 18-bit panel a
/// bitmap icon is mush, and shipping image assets would mean shipping artwork.
/// </summary>
public static class ButtonGlyph
{
    public static void Draw(SKCanvas canvas, ButtonIcon icon, SKRect box, SKColor color)
    {
        if (icon == ButtonIcon.None) return;

        using var stroke = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.3f, StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round, Color = color,
        };
        using var fill = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Fill, Color = color,
        };

        float x = box.Left, y = box.Top, w = box.Width, h = box.Height;
        float cx = box.MidX, cy = box.MidY;

        switch (icon)
        {
            case ButtonIcon.VolumeUp:
            case ButtonIcon.VolumeDown:
            case ButtonIcon.Mute:
            {
                Speaker(canvas, box, fill);
                if (icon == ButtonIcon.Mute)
                {
                    canvas.DrawLine(x + w * 0.62f, y + h * 0.30f, x + w, y + h * 0.72f, stroke);
                    canvas.DrawLine(x + w, y + h * 0.30f, x + w * 0.62f, y + h * 0.72f, stroke);
                }
                else
                {
                    // One arc for down, two for up - a count reads faster than a
                    // size difference at this scale.
                    canvas.DrawArc(new SKRect(x + w * 0.42f, y + h * 0.24f, x + w * 0.86f, y + h * 0.76f),
                                   -55, 110, false, stroke);
                    if (icon == ButtonIcon.VolumeUp)
                        canvas.DrawArc(new SKRect(x + w * 0.30f, y + h * 0.10f, x + w * 1.05f, y + h * 0.90f),
                                       -55, 110, false, stroke);
                }
                break;
            }

            case ButtonIcon.Play:
                using (var path = new SKPathBuilder())
                {
                    path.MoveTo(x + w * 0.28f, y + h * 0.16f);
                    path.LineTo(x + w * 0.84f, cy);
                    path.LineTo(x + w * 0.28f, y + h * 0.84f);
                    path.Close();
                    using var p = path.Detach();
                    canvas.DrawPath(p, fill);
                }
                break;

            case ButtonIcon.Next:
            case ButtonIcon.Previous:
            {
                float dir = icon == ButtonIcon.Next ? 1 : -1;
                float baseX = icon == ButtonIcon.Next ? x + w * 0.22f : x + w * 0.78f;
                using (var path = new SKPathBuilder())
                {
                    path.MoveTo(baseX, y + h * 0.20f);
                    path.LineTo(baseX + dir * w * 0.42f, cy);
                    path.LineTo(baseX, y + h * 0.80f);
                    path.Close();
                    using var p = path.Detach();
                    canvas.DrawPath(p, fill);
                }
                float barX = icon == ButtonIcon.Next ? x + w * 0.74f : x + w * 0.22f;
                canvas.DrawRect(new SKRect(barX, y + h * 0.20f, barX + 1.6f, y + h * 0.80f), fill);
                break;
            }

            case ButtonIcon.Launch:
                // A window with an arrow leaving it.
                canvas.DrawRect(new SKRect(x + w * 0.12f, y + h * 0.24f, x + w * 0.66f, y + h * 0.86f), stroke);
                canvas.DrawLine(x + w * 0.48f, y + h * 0.52f, x + w * 0.92f, y + h * 0.14f, stroke);
                canvas.DrawLine(x + w * 0.62f, y + h * 0.14f, x + w * 0.92f, y + h * 0.14f, stroke);
                canvas.DrawLine(x + w * 0.92f, y + h * 0.14f, x + w * 0.92f, y + h * 0.44f, stroke);
                break;

            case ButtonIcon.Screen:
                // The panel itself, in its own 13:1 proportion.
                canvas.DrawRoundRect(new SKRect(x + w * 0.06f, cy - h * 0.20f, x + w * 0.94f, cy + h * 0.20f),
                                     2, 2, stroke);
                canvas.DrawLine(cx, cy - h * 0.20f, cx, cy + h * 0.20f, stroke);
                break;

            case ButtonIcon.Key:
                canvas.DrawRoundRect(new SKRect(x + w * 0.12f, y + h * 0.18f, x + w * 0.88f, y + h * 0.82f),
                                     2.5f, 2.5f, stroke);
                canvas.DrawLine(x + w * 0.32f, y + h * 0.62f, x + w * 0.68f, y + h * 0.62f, stroke);
                break;

            case ButtonIcon.Brightness:
                canvas.DrawCircle(cx, cy, h * 0.20f, stroke);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    canvas.DrawLine(
                        cx + (float)Math.Cos(a) * h * 0.32f, cy + (float)Math.Sin(a) * h * 0.32f,
                        cx + (float)Math.Cos(a) * h * 0.45f, cy + (float)Math.Sin(a) * h * 0.45f,
                        stroke);
                }
                break;
        }
    }

    private static void Speaker(SKCanvas canvas, SKRect box, SKPaint fill)
    {
        float x = box.Left, y = box.Top, w = box.Width, h = box.Height;
        // MUST be disposed: SKPathBuilder wraps native memory the GC cannot see,
        // and this runs once per button per frame.
        using var path = new SKPathBuilder();
        path.MoveTo(x + w * 0.06f, y + h * 0.36f);
        path.LineTo(x + w * 0.20f, y + h * 0.36f);
        path.LineTo(x + w * 0.38f, y + h * 0.16f);
        path.LineTo(x + w * 0.38f, y + h * 0.84f);
        path.LineTo(x + w * 0.20f, y + h * 0.64f);
        path.LineTo(x + w * 0.06f, y + h * 0.64f);
        path.Close();
        using var p = path.Detach();
        canvas.DrawPath(p, fill);
    }
}

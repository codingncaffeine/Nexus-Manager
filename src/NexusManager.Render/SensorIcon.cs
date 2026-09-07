using NexusManager.Sensors;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Small type glyphs, as iCUE puts in the corner of each sensor tile. Drawn as
/// vectors rather than bitmaps: they must stay legible at about 9px, they follow
/// the module's colour, and shipping no image assets keeps the repo clean.
///
/// Every shape is authored in a 0..1 unit square and scaled into the target rect.
/// </summary>
public static class SensorIcon
{
    public static void Draw(SKCanvas canvas, SensorKind kind, SKRect box, SKColor color)
    {
        float s = Math.Min(box.Width, box.Height);
        if (s < 4) return;                       // below this nothing reads; skip it

        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, s * 0.13f),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = color,
        };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };

        float x = box.Left, y = box.Top;
        float P(float u) => u * s;

        switch (kind)
        {
            case SensorKind.Temperature:
                // thermometer: stem plus bulb
                canvas.DrawLine(x + P(0.5f), y + P(0.12f), x + P(0.5f), y + P(0.62f), stroke);
                canvas.DrawCircle(x + P(0.5f), y + P(0.78f), P(0.20f), fill);
                break;

            case SensorKind.Load:
                // processor: die with pins
                canvas.DrawRect(x + P(0.26f), y + P(0.26f), P(0.48f), P(0.48f), stroke);
                for (float u = 0.36f; u <= 0.66f; u += 0.14f)
                {
                    canvas.DrawLine(x + P(u), y + P(0.10f), x + P(u), y + P(0.26f), stroke);
                    canvas.DrawLine(x + P(u), y + P(0.74f), x + P(u), y + P(0.90f), stroke);
                    canvas.DrawLine(x + P(0.10f), y + P(u), x + P(0.26f), y + P(u), stroke);
                    canvas.DrawLine(x + P(0.74f), y + P(u), x + P(0.90f), y + P(u), stroke);
                }
                break;

            case SensorKind.Voltage:
            case SensorKind.Power:
                // lightning bolt
                {
                using var bolt = new SKPathBuilder();
                bolt.MoveTo(x + P(0.58f), y + P(0.08f));
                bolt.LineTo(x + P(0.30f), y + P(0.54f));
                bolt.LineTo(x + P(0.48f), y + P(0.54f));
                bolt.LineTo(x + P(0.40f), y + P(0.92f));
                bolt.LineTo(x + P(0.70f), y + P(0.42f));
                bolt.LineTo(x + P(0.52f), y + P(0.42f));
                bolt.Close();
                using (var p = bolt.Detach()) canvas.DrawPath(p, fill);
                }
                break;

            case SensorKind.Fan:
                canvas.DrawCircle(x + P(0.5f), y + P(0.5f), P(0.38f), stroke);
                canvas.DrawCircle(x + P(0.5f), y + P(0.5f), P(0.10f), fill);
                for (int i = 0; i < 3; i++)
                {
                    double a = i * 2 * Math.PI / 3;
                    canvas.DrawLine(
                        x + P(0.5f) + (float)(Math.Cos(a) * P(0.12f)),
                        y + P(0.5f) + (float)(Math.Sin(a) * P(0.12f)),
                        x + P(0.5f) + (float)(Math.Cos(a) * P(0.34f)),
                        y + P(0.5f) + (float)(Math.Sin(a) * P(0.34f)), stroke);
                }
                break;

            case SensorKind.Memory:
                // DIMM: body with contact pins along the bottom
                canvas.DrawRect(x + P(0.12f), y + P(0.24f), P(0.76f), P(0.44f), stroke);
                for (float u = 0.24f; u <= 0.76f; u += 0.17f)
                    canvas.DrawLine(x + P(u), y + P(0.68f), x + P(u), y + P(0.86f), stroke);
                break;

            case SensorKind.Disk:
                canvas.DrawOval(new SKRect(x + P(0.10f), y + P(0.22f), x + P(0.90f), y + P(0.46f)), stroke);
                canvas.DrawLine(x + P(0.10f), y + P(0.34f), x + P(0.10f), y + P(0.66f), stroke);
                canvas.DrawLine(x + P(0.90f), y + P(0.34f), x + P(0.90f), y + P(0.66f), stroke);
                canvas.DrawOval(new SKRect(x + P(0.10f), y + P(0.54f), x + P(0.90f), y + P(0.78f)), stroke);
                break;

            case SensorKind.Network:
                // up and down arrows
                canvas.DrawLine(x + P(0.32f), y + P(0.86f), x + P(0.32f), y + P(0.16f), stroke);
                canvas.DrawLine(x + P(0.18f), y + P(0.32f), x + P(0.32f), y + P(0.16f), stroke);
                canvas.DrawLine(x + P(0.46f), y + P(0.32f), x + P(0.32f), y + P(0.16f), stroke);
                canvas.DrawLine(x + P(0.68f), y + P(0.14f), x + P(0.68f), y + P(0.84f), stroke);
                canvas.DrawLine(x + P(0.54f), y + P(0.68f), x + P(0.68f), y + P(0.84f), stroke);
                canvas.DrawLine(x + P(0.82f), y + P(0.68f), x + P(0.68f), y + P(0.84f), stroke);
                break;

            case SensorKind.Frequency:
                // one cycle of a wave
                {
                using var wave = new SKPathBuilder();
                wave.MoveTo(x + P(0.10f), y + P(0.50f));
                wave.CubicTo(x + P(0.28f), y + P(0.06f), x + P(0.38f), y + P(0.94f), x + P(0.52f), y + P(0.50f));
                wave.CubicTo(x + P(0.64f), y + P(0.14f), x + P(0.76f), y + P(0.86f), x + P(0.90f), y + P(0.50f));
                using (var p = wave.Detach()) canvas.DrawPath(p, stroke);
                }
                break;

            default:
                canvas.DrawCircle(x + P(0.5f), y + P(0.5f), P(0.30f), stroke);
                break;
        }
    }
}

using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Draws a <see cref="History"/> as a filled strip chart: newest sample at the
/// right edge, scrolling left as samples arrive.
/// </summary>
public static class Sparkline
{
    /// <summary>
    /// Scale is FIXED, never auto-ranged. Auto-scaling makes a chart that always
    /// fills its box, so a CPU idling between 3% and 5% looks identical to one
    /// swinging 0-100%. For monitoring, magnitude is the point.
    /// </summary>
    public static void Draw(
        SKCanvas canvas,
        History history,
        SKRect rect,
        double min,
        double max,
        SKColor line,
        byte fillAlpha = 60)
    {
        if (history.Count < 2 || max <= min) return;

        int columns = Math.Min((int)rect.Width, history.Count);
        if (columns < 2) return;

        float range = (float)(max - min);
        // MUST be disposed: SKPathBuilder wraps native memory the GC cannot see.
        using var stroke = new SKPathBuilder();
        using var area   = new SKPathBuilder();

        // Newest sample sits at the right edge; age increases leftwards.
        for (int c = 0; c < columns; c++)
        {
            float norm = Math.Clamp((float)((history[c] - min) / range), 0f, 1f);
            float x = rect.Right - c;
            float y = rect.Bottom - norm * rect.Height;
            if (c == 0) { stroke.MoveTo(x, y); area.MoveTo(x, y); }
            else        { stroke.LineTo(x, y); area.LineTo(x, y); }
        }

        if (fillAlpha > 0)
        {
            area.LineTo(rect.Right - (columns - 1), rect.Bottom);
            area.LineTo(rect.Right, rect.Bottom);
            area.Close();
            using var areaPath = area.Detach();
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = line.WithAlpha(fillAlpha),
            };
            canvas.DrawPath(areaPath, fill);
        }

        using var linePath = stroke.Detach();
        using var pen = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            Color = line,
        };
        canvas.DrawPath(linePath, pen);
    }
}

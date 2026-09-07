using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Dots showing which screen of how many is displayed. Without it there is no
/// way to know the strip has more screens — there is no chrome, no scrollbar,
/// and swipe is invisible until discovered.
/// </summary>
public static class PageIndicator
{
    public static void Draw(SKCanvas canvas, int index, int count, SKColor color)
    {
        if (count < 2) return;

        const float R = 1.6f, Gap = 6f;
        float total = count * (R * 2) + (count - 1) * (Gap - R * 2);
        float x = (NexusCanvas.Width - total) / 2 + R;
        float y = NexusCanvas.Height - 2.5f;

        using var on  = new SKPaint { IsAntialias = true, Color = color };
        using var off = new SKPaint { IsAntialias = true, Color = color.WithAlpha(70) };

        for (int i = 0; i < count; i++)
        {
            canvas.DrawCircle(x, y, i == index ? R : R * 0.75f, i == index ? on : off);
            x += Gap;
        }
    }
}

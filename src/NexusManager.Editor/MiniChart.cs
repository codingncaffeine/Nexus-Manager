using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NexusManager.Render;

namespace NexusManager.Editor;

/// <summary>
/// The filled sparkline along the bottom of a sensor tile, as iCUE draws it.
/// Scale is fixed rather than auto-ranged, for the same reason it is on the
/// panel: an auto-scaled chart makes an idle sensor look identical to a busy one.
/// </summary>
public sealed class MiniChart : Control
{
    public History? History { get; set; }
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public Color Tint { get; set; } = Colors.White;

    public override void Render(DrawingContext ctx)
    {
        var h = History;
        if (h is null || h.Count < 2 || Max <= Min || Bounds.Width < 2) return;

        int cols = Math.Min((int)Bounds.Width, h.Count);
        if (cols < 2) return;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            double range = Max - Min;
            Point First(int c)
            {
                double norm = Math.Clamp((h[c] - Min) / range, 0, 1);
                return new Point(Bounds.Width - c, Bounds.Height - norm * Bounds.Height);
            }
            var p0 = First(0);
            lc.BeginFigure(p0, false);
            ac.BeginFigure(p0, true);
            for (int c = 1; c < cols; c++)
            {
                var p = First(c);
                lc.LineTo(p); ac.LineTo(p);
            }
            ac.LineTo(new Point(Bounds.Width - (cols - 1), Bounds.Height));
            ac.LineTo(new Point(Bounds.Width, Bounds.Height));
            ac.EndFigure(true);
            lc.EndFigure(false);
        }

        ctx.DrawGeometry(new SolidColorBrush(Tint, 0.50), null, area);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Tint), 1.2), line);
    }
}

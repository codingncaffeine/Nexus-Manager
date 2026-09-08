using NexusManager.Audio;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// The scope family: everything driven by the raw waveform rather than by the
/// spectrum. Stateless - each frame is a pure function of the current window -
/// so unlike the effect modes this owns nothing but its paints.
/// </summary>
public sealed class WaveformRenderer : IDisposable
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _line = new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f,
    };
    private SKPoint[] _trace = [];

    /// <summary>Sample for column <paramref name="x"/> of <paramref name="w"/>.</summary>
    private static float At(float[] wave, int x, int w) =>
        wave.Length < 2 || w < 2 ? 0f : wave[(int)((long)x * (wave.Length - 1) / (w - 1))];

    /// <summary>A thin antialiased trace. The plainest scope, and the one that
    /// reads most cleanly at 48px because a 1px line has no interior to lose.</summary>
    public void Oscilloscope(SKCanvas canvas, SKRect rect, AudioFrame frame, SKColor color)
    {
        int w = (int)rect.Width;
        if (w < 2 || frame.Waveform.Length < 2) return;

        if (_trace.Length != w) _trace = new SKPoint[w];
        float mid = rect.Top + rect.Height / 2f, half = rect.Height / 2f - 1f;
        for (int x = 0; x < w; x++)
            _trace[x] = new SKPoint(
                rect.Left + x, mid - Math.Clamp(At(frame.Waveform, x, w), -1f, 1f) * half);

        _line.Color = color;
        canvas.DrawPoints(SKPointMode.Polygon, _trace, _line);
    }

    /// <summary>The trace filled back to the centre line.</summary>
    public void FilledScope(SKCanvas canvas, SKRect rect, AudioFrame frame, SKColor color)
    {
        int w = (int)rect.Width;
        if (w < 2 || frame.Waveform.Length < 2) return;

        float mid = rect.Top + rect.Height / 2f, half = rect.Height / 2f - 1f;
        _fill.Color = color;
        for (int x = 0; x < w; x++)
        {
            float y = mid - Math.Clamp(At(frame.Waveform, x, w), -1f, 1f) * half;
            float top = MathF.Min(mid, y), bot = MathF.Max(mid, y);
            canvas.DrawRect(rect.Left + x, top, 1f, MathF.Max(1f, bot - top), _fill);
        }
    }

    /// <summary>
    /// The peak ENVELOPE reflected about the centre - the shape a waveform is
    /// usually drawn as in an editor.
    ///
    /// It takes the maximum absolute sample per column rather than one sample,
    /// which is the whole difference: at 640 columns over a 640-sample window a
    /// point sample lands on whatever phase it lands on and the outline
    /// flickers, while the envelope is stable.
    /// </summary>
    public void EnvelopeMirror(SKCanvas canvas, SKRect rect, AudioFrame frame, SKColor color)
    {
        int w = (int)rect.Width;
        int n = frame.Waveform.Length;
        if (w < 2 || n < 2) return;

        float mid = rect.Top + rect.Height / 2f, half = rect.Height / 2f - 1f;
        _fill.Color = color;
        for (int x = 0; x < w; x++)
        {
            int lo = (int)((long)x * n / w), hi = (int)((long)(x + 1) * n / w);
            float peak = 0f;
            for (int i = lo; i < hi && i < n; i++) peak = MathF.Max(peak, MathF.Abs(frame.Waveform[i]));
            float h = MathF.Max(1f, Math.Clamp(peak, 0f, 1f) * half);
            canvas.DrawRect(rect.Left + x, mid - h, 1f, h * 2f, _fill);
        }
    }

    /// <summary>
    /// A scope drawn as unconnected dots. Media players have shipped this as a
    /// preset for decades, and it suits this panel better
    /// than it suits a desktop window - at 48px a connected trace at speed is
    /// nearly solid, while dots keep the shape legible.
    /// </summary>
    public void DotScope(SKCanvas canvas, SKRect rect, AudioFrame frame, SKColor color, int step = 3)
    {
        int w = (int)rect.Width;
        if (w < 2 || frame.Waveform.Length < 2) return;

        float mid = rect.Top + rect.Height / 2f, half = rect.Height / 2f - 1f;
        _fill.Color = color;
        for (int x = 0; x < w; x += Math.Max(1, step))
        {
            float y = mid - Math.Clamp(At(frame.Waveform, x, w), -1f, 1f) * half;
            canvas.DrawRect(rect.Left + x, y, 2f, 2f, _fill);
        }
    }

    /// <summary>
    /// Stereo goniometer, rotated into MID/SIDE rather than plotted as raw L
    /// against R.
    ///
    /// A textbook vectorscope is square and would waste nine tenths of a 13.3:1
    /// strip. Rotating it 45 degrees - side (L-R) horizontally, mid (L+R)
    /// vertically - puts STEREO WIDTH on the axis this panel actually has, and
    /// mono content collapses to a vertical line in the centre. That is the same
    /// information a broadcast goniometer shows, laid out for the hardware.
    /// </summary>
    public void Vectorscope(SKCanvas canvas, SKRect rect, AudioFrame frame, SKColor color)
    {
        var left = frame.WaveformLeft;
        var right = frame.WaveformRight;
        int n = Math.Min(left.Length, right.Length);
        if (n < 2) return;

        float cx = rect.Left + rect.Width / 2f, cy = rect.Top + rect.Height / 2f;
        float sx = rect.Width / 2f - 1f, sy = rect.Height / 2f - 1f;

        // Centre reference: without it a silent passage is an empty rectangle
        // and there is no way to see the scope is alive.
        _fill.Color = color.WithAlpha(50);
        canvas.DrawRect(cx, rect.Top, 1f, rect.Height, _fill);

        _fill.Color = color;
        for (int i = 0; i < n; i++)
        {
            float mid = (left[i] + right[i]) * 0.5f;
            float side = (left[i] - right[i]) * 0.5f;
            float x = cx + Math.Clamp(side * 2f, -1f, 1f) * sx;
            float y = cy - Math.Clamp(mid, -1f, 1f) * sy;
            canvas.DrawRect(x, y, 1f, 1f, _fill);
        }
    }

    public void Dispose()
    {
        _fill.Dispose();
        _line.Dispose();
    }
}

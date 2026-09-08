using NexusManager.Audio;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Draws an <see cref="AudioFrame"/> into a cell. One instance per visualizer
/// cell; it owns its paints and any per-mode history.
///
/// ⛔ EVERY native Skia object here is allocated ONCE and reused. The known trap
/// on this project is that undisposed SkiaSharp handles leak with zero GC
/// pressure - an SKPathBuilder churned per frame leaked ~192/sec and got the
/// editor OOM-killed. Bar modes are the worst case in the whole app: 64 bars at
/// 30 fps is 1920 draws a second, so a per-bar allocation would be fatal rather
/// than merely wasteful.
/// </summary>
public sealed class VisualizerRenderer : IDisposable
{
    private readonly SKPaint _bar = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _cap = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _base = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _blit = new() { IsAntialias = false };
    /// <summary>Nearest, always: this is a 1:1 pixel blit and any filtering
    /// would smear a spectrogram column into its neighbours.</summary>
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>Per-band colours, rebuilt only when the band count or palette
    /// changes rather than per frame.</summary>
    private SKColor[] _colors = [];
    private int _colorBands = -1;
    private VisualizerPalette _colorPalette = (VisualizerPalette)(-1);
    private SKColor _colorBase;

    /// <summary>Spectrogram history. A bitmap written one column at a time with
    /// a circular cursor, rather than a per-frame scroll: shifting 640x48 pixels
    /// every frame to advance by one column is the same needless memmove the
    /// audio ring buffer exists to avoid.</summary>
    /// <summary>The stateful Winamp-heritage modes. Created lazily: a
    /// screen with no Winamp cell should not carry a feedback buffer.</summary>
    private WinampRenderer? _winamp;

    private SKBitmap? _gram;
    private int _gramCol;
    private long _gramSeq = -1;

    /// <summary>
    /// The modes that actually have a draw routine today.
    ///
    /// ⛔ This list is the authority, not the enum. Every unimplemented
    /// VisualizerKind falls through to Bars, so without an explicit set a
    /// cycle through the enum would show Bars ten times over and report ten
    /// different mode names while doing it - a display that lies about what
    /// it is drawing.
    /// </summary>
    public static readonly VisualizerKind[] Implemented =
    [
        VisualizerKind.Bars,
        VisualizerKind.MirroredBars,
        VisualizerKind.SegmentedVu,
        VisualizerKind.VuMeters,
        VisualizerKind.Spectrogram,
        VisualizerKind.WinampSpectrum,
        VisualizerKind.WinampScope,
        VisualizerKind.Feedback,
        VisualizerKind.Fire,
    ];

    public void Draw(SKCanvas canvas, VisualizerSpec spec, SKRect rect, AudioFrame frame, Theme theme)
    {
        switch (spec.Kind)
        {
            case VisualizerKind.MirroredBars: DrawBars(canvas, spec, rect, frame, theme, mirrored: true); break;
            case VisualizerKind.SegmentedVu:  DrawSegmented(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.VuMeters:     DrawVu(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.Spectrogram:  DrawSpectrogram(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.WinampSpectrum: Winamp.Spectrum(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.WinampScope:    Winamp.Scope(canvas, rect, frame, theme); break;
            case VisualizerKind.Feedback:       Winamp.Feedback(canvas, rect, frame, theme); break;
            case VisualizerKind.Fire:           Winamp.Fire(canvas, rect, frame, theme); break;
            case VisualizerKind.Bars:
            default:                          DrawBars(canvas, spec, rect, frame, theme, mirrored: false); break;
        }
    }

    // ---------------------------------------------------------------- bars --

    private void DrawBars(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                          AudioFrame frame, Theme theme, bool mirrored)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;
        EnsureColors(spec, theme, n);
        Baseline(canvas, spec, rect, theme);

        // Mirrored draws each band TWICE, outward from the centre, so the strip
        // holds n bands either side rather than n across. On a 13.3:1 panel the
        // symmetry reads much more strongly than the extra resolution would.
        int slots = mirrored ? n * 2 : n;
        float slot = rect.Width / slots;
        float gap = Math.Clamp(spec.Gap, 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);

        for (int b = 0; b < n; b++)
        {
            float h = frame.Bands[b] * rect.Height;
            float p = frame.Peaks[b] * rect.Height;
            SKColor c = _colors[b];

            if (mirrored)
            {
                float mid = rect.Left + rect.Width / 2f;
                Bar(canvas, mid + b * slot, barW, h, p, c, rect, spec);
                Bar(canvas, mid - (b + 1) * slot, barW, h, p, c, rect, spec);
            }
            else
            {
                Bar(canvas, rect.Left + b * slot, barW, h, p, c, rect, spec);
            }
        }
    }

    private void Bar(SKCanvas canvas, float x, float w, float h, float p,
                     SKColor c, SKRect rect, VisualizerSpec spec)
    {
        if (h >= 1f)
        {
            _bar.Color = c;
            canvas.DrawRect(x, rect.Bottom - h, w, h, _bar);
        }
        if (!spec.ShowPeaks || p < 2f) return;

        // The cap does most of the work at this size: 48px of height is 1.25 dB
        // per pixel, so the bar alone cannot show a transient the cap makes
        // obvious (D49).
        _cap.Color = c.WithAlpha(255);
        canvas.DrawRect(x, MathF.Max(rect.Top, rect.Bottom - p - 1f), w, 1f, _cap);
    }

    // ----------------------------------------------------------- segmented --

    /// <summary>
    /// Quantised bars, hi-fi style. Not merely a retro affectation: the panel is
    /// 18-bit and gradients band, so a display made of discrete lit segments
    /// sidesteps the quantisation entirely instead of dithering around it (D49).
    /// </summary>
    private void DrawSegmented(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                               AudioFrame frame, Theme theme)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;
        EnsureColors(spec, theme, n);

        const int Segments = 12;
        float slot = rect.Width / n;
        float gap = Math.Clamp(spec.Gap, 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);
        float segH = rect.Height / Segments;
        float segDraw = MathF.Max(1f, segH - 1f);

        for (int b = 0; b < n; b++)
        {
            float x = rect.Left + b * slot;
            int lit = (int)MathF.Round(frame.Bands[b] * Segments);
            int peakSeg = (int)MathF.Round(frame.Peaks[b] * Segments);

            for (int s = 0; s < Segments; s++)
            {
                bool on = s < lit;
                bool isPeak = spec.ShowPeaks && s == peakSeg - 1 && peakSeg > lit;
                if (!on && !isPeak) continue;

                // Green / amber / red by HEIGHT, the meter convention, rather
                // than by frequency. ⛔ Provisional on this panel: green reads
                // lime here (D54).
                float f = (float)s / (Segments - 1);
                SKColor c = f > 0.85f ? theme.HotColor
                          : f > 0.65f ? theme.WarnColor
                          : _colors[b];
                _bar.Color = isPeak ? c.WithAlpha(160) : c;
                canvas.DrawRect(x, rect.Bottom - (s + 1) * segH, barW, segDraw, _bar);
            }
        }
    }

    // ------------------------------------------------------------------ vu --

    /// <summary>
    /// Two horizontal bars, L over R. Uses the strip's WIDTH for level instead
    /// of its height, which is the one thing 640x48 has in abundance - 640
    /// pixels of resolution on a meter instead of 48.
    /// </summary>
    private void DrawVu(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                        AudioFrame frame, Theme theme)
    {
        float h = rect.Height / 2f;
        Row(canvas, spec, new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + h),
            frame.RmsLeft, frame.PeakLeft, theme);
        Row(canvas, spec, new SKRect(rect.Left, rect.Top + h, rect.Right, rect.Bottom),
            frame.RmsRight, frame.PeakRight, theme);
    }

    private void Row(SKCanvas canvas, VisualizerSpec spec, SKRect r,
                     float rms, float peak, Theme theme)
    {
        const float Pad = 2f;
        float top = r.Top + Pad, bot = r.Bottom - Pad;
        if (bot <= top) return;

        _base.Color = theme.DividerColor;
        canvas.DrawRect(r.Left, top, r.Width, bot - top, _base);

        float w = rms * r.Width;
        if (w >= 1f)
        {
            _bar.Color = rms > 0.9f ? theme.HotColor
                       : rms > 0.75f ? theme.WarnColor
                       : Theme.Parse(spec.Color, theme.ValueColor);
            canvas.DrawRect(r.Left, top, w, bot - top, _bar);
        }

        if (!spec.ShowPeaks) return;
        float px = r.Left + peak * r.Width;
        _cap.Color = theme.ValueColor;
        canvas.DrawRect(MathF.Min(px, r.Right - 2f), top, 2f, bot - top, _cap);
    }

    // --------------------------------------------------------- spectrogram --

    /// <summary>
    /// Time on X, frequency on Y, magnitude as brightness. The mode this panel's
    /// shape suits best: 640 columns at one column per analysis hop is roughly
    /// 14 seconds of audio visible at once.
    /// </summary>
    private void DrawSpectrogram(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                                 AudioFrame frame, Theme theme)
    {
        int w = Math.Max(1, (int)rect.Width);
        int h = Math.Max(1, (int)rect.Height);
        if (_gram is null || _gram.Width != w || _gram.Height != h)
        {
            _gram?.Dispose();
            _gram = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            _gram.Erase(theme.BackgroundColor);
            _gramCol = 0;
        }

        // Advance ONLY on a new analysis hop. Advancing per frame would stretch
        // the time axis by the ratio of frame rate to hop rate and make the
        // scroll speed depend on the display setting rather than on the audio.
        if (frame.Sequence != _gramSeq && frame.Bands.Length > 0)
        {
            _gramSeq = frame.Sequence;
            int n = frame.Bands.Length;
            for (int y = 0; y < h; y++)
            {
                // Bottom row = lowest band, so it matches the bar modes.
                int b = Math.Clamp((int)((float)(h - 1 - y) / h * n), 0, n - 1);
                float v = frame.Bands[b];
                _gram.SetPixel(_gramCol, y, Heat(v, theme));
            }
            _gramCol = (_gramCol + 1) % w;
        }

        // Unwrap the ring in two blits so the newest column lands at the right
        // edge, with no per-frame pixel shifting.
        int tail = w - _gramCol;
        canvas.DrawBitmap(_gram,
            new SKRect(_gramCol, 0, w, h),
            new SKRect(rect.Left, rect.Top, rect.Left + tail, rect.Bottom), Nearest, _blit);
        if (_gramCol > 0)
            canvas.DrawBitmap(_gram,
                new SKRect(0, 0, _gramCol, h),
                new SKRect(rect.Left + tail, rect.Top, rect.Right, rect.Bottom), Nearest, _blit);
    }

    /// <summary>Magnitude to colour. Dark background, through the value colour,
    /// to hot at full scale.</summary>
    private static SKColor Heat(float v, Theme theme)
    {
        v = Math.Clamp(v, 0f, 1f);
        if (v <= 0.001f) return theme.BackgroundColor;
        SKColor a = theme.BackgroundColor, b = theme.ValueColor, c = theme.HotColor;
        return v < 0.6f ? Lerp(a, b, v / 0.6f) : Lerp(b, c, (v - 0.6f) / 0.4f);
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red   + (b.Red   - a.Red)   * t),
            (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue  + (b.Blue  - a.Blue)  * t));
    }

    // --------------------------------------------------------------- misc --

    private void Baseline(SKCanvas canvas, VisualizerSpec spec, SKRect rect, Theme theme)
    {
        // A 1px seat under every bar. Without it a silent strip is simply blank
        // and gives no sign the visualizer is alive rather than crashed - which
        // on this panel is otherwise indistinguishable.
        if (!spec.ShowBaseline) return;
        _base.Color = theme.CaptionColor.WithAlpha(70);
        canvas.DrawRect(rect.Left, rect.Bottom - 1f, rect.Width, 1f, _base);
    }

    private void EnsureColors(VisualizerSpec spec, Theme theme, int n)
    {
        SKColor base_ = spec.Palette switch
        {
            VisualizerPalette.Theme => theme.ValueColor,
            _ => Theme.Parse(spec.Color, theme.ValueColor),
        };

        if (_colorBands == n && _colorPalette == spec.Palette && _colorBase == base_) return;

        _colorBands = n;
        _colorPalette = spec.Palette;
        _colorBase = base_;
        _colors = new SKColor[n];

        for (int b = 0; b < n; b++)
        {
            _colors[b] = spec.Palette switch
            {
                // Hue sweep across the band axis. Stops at 200 rather than
                // wrapping past blue so the top end cannot loop back to red and
                // read as bass.
                //
                // ⛔ PROVISIONAL. The panel's green primary is yellow-shifted -
                // full green reads LIME and blue+green reads WHITE, not cyan -
                // so this ramp will NOT land where sRGB says (D54). It is set by
                // eye on the hardware, and only the user can make that call.
                VisualizerPalette.Frequency =>
                    SKColor.FromHsl(200f * b / MathF.Max(1, n - 1), 85f, 55f),
                _ => base_,
            };
        }
    }

    private WinampRenderer Winamp => _winamp ??= new WinampRenderer();

    public void Dispose()
    {
        _bar.Dispose();
        _cap.Dispose();
        _base.Dispose();
        _blit.Dispose();
        _winamp?.Dispose();
        _gram?.Dispose();
    }
}

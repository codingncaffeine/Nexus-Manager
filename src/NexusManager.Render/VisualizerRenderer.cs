using NexusManager.Audio;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// Draws an <see cref="AudioFrame"/> into a cell, and owns the per-family
/// renderers. One instance per visualizer cell.
///
/// ⛔ EVERY native Skia object is allocated ONCE and reused. The known trap on
/// this project is that undisposed SkiaSharp handles leak with zero GC pressure
/// - an SKPathBuilder churned per frame leaked ~192/sec and got the editor
/// OOM-killed. Bar modes are the worst case in the app: 64 bars at 30 fps is
/// 1920 draws a second, so a per-bar allocation would be fatal, not merely
/// wasteful.
/// </summary>
public sealed class VisualizerRenderer : IDisposable
{
    private readonly SKPaint _bar = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _cap = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _base = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _blit = new() { IsAntialias = false };
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    private SKColor[] _colors = [];
    private int _colorBands = -1;
    private VisualizerPalette _colorPalette = (VisualizerPalette)(-1);
    private SKColor _colorBase;

    private SKBitmap? _gram;
    private int _gramCol;
    private long _gramSeq = -1;

    private ClassicRenderer? _classic;
    private WaveformRenderer? _wave;
    private EffectRenderer? _effects;

    private ClassicRenderer Classic => _classic ??= new ClassicRenderer();
    private WaveformRenderer Wave => _wave ??= new WaveformRenderer();
    private EffectRenderer Effects => _effects ??= new EffectRenderer();

    /// <summary>
    /// The modes that actually have a draw routine today.
    ///
    /// ⛔ This list is the authority, not the enum. An unimplemented
    /// VisualizerKind would fall through to Bars, so without an explicit set a
    /// cycle through the enum shows Bars while reporting other mode names - a
    /// display that lies about what it is drawing (D55).
    /// </summary>
    public static readonly VisualizerKind[] Implemented =
    [
        VisualizerKind.Bars,
        VisualizerKind.MirroredBars,
        VisualizerKind.GradientBars,
        VisualizerKind.SpectrumCurve,
        VisualizerKind.DualChannelSpectrum,
        VisualizerKind.SegmentedVu,
        VisualizerKind.DotMatrix,
        VisualizerKind.Oscilloscope,
        VisualizerKind.FilledScope,
        VisualizerKind.EnvelopeMirror,
        VisualizerKind.VuMeters,
        VisualizerKind.LevelBar,
        VisualizerKind.Spectrogram,
        VisualizerKind.ReactiveBackground,
        VisualizerKind.BeatPulse,
        VisualizerKind.ClassicSpectrum,
        VisualizerKind.ClassicScope,
        VisualizerKind.Feedback,
        VisualizerKind.Fire,
        VisualizerKind.Superscope,
        VisualizerKind.Starfield,
        VisualizerKind.Plasma,
        VisualizerKind.EmeraldBars,
        VisualizerKind.DotScope,
        VisualizerKind.Particles,
        VisualizerKind.Ambience,
        VisualizerKind.Kaleidoscope,
        VisualizerKind.Vectorscope,
        VisualizerKind.ReflectedBars,
        VisualizerKind.GlowPills,
        VisualizerKind.Blobs,
        VisualizerKind.Terrain,
    ];

    public void Draw(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                     AudioFrame frame, Theme theme, float dt = 1f / 30f)
    {
        SKColor tint = Theme.Parse(spec.Color, theme.ValueColor);

        switch (spec.Kind)
        {
            case VisualizerKind.GlowPills: DrawGlowPills(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.Blobs:
                EnsureColors(spec, theme, Math.Max(1, frame.Bands.Length), spec.Palette);
                Effects.Advance(dt, frame); Effects.Blobs(canvas, rect, frame, _colors); break;
            case VisualizerKind.Terrain:
                EnsureColors(spec, theme, Math.Max(1, frame.Bands.Length), spec.Palette);
                Effects.Advance(dt, frame); Effects.Terrain(canvas, rect, frame, _colors); break;
            case VisualizerKind.ReflectedBars: DrawReflected(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.MirroredBars: DrawBars(canvas, spec, rect, frame, theme, mirrored: true); break;
            case VisualizerKind.GradientBars: DrawGradientBars(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.SpectrumCurve: DrawCurve(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.DualChannelSpectrum: DrawDual(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.SegmentedVu: DrawSegmented(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.DotMatrix: DrawDotMatrix(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.VuMeters: DrawVu(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.LevelBar: DrawLevelBar(canvas, spec, rect, frame, theme); break;
            case VisualizerKind.Spectrogram: DrawSpectrogram(canvas, rect, frame, theme); break;

            // Flat one-colour bars, detached white caps. Forced to the
            // Emerald palette because that IS the preset - it is not a colour
            // choice the user made, it is what the mode is.
            case VisualizerKind.EmeraldBars: DrawBars(canvas, spec, rect, frame, theme,
                                                  mirrored: false, force: VisualizerPalette.Emerald); break;

            case VisualizerKind.Oscilloscope: Wave.Oscilloscope(canvas, rect, frame, tint); break;
            case VisualizerKind.FilledScope: Wave.FilledScope(canvas, rect, frame, tint); break;
            case VisualizerKind.EnvelopeMirror: Wave.EnvelopeMirror(canvas, rect, frame, tint); break;
            case VisualizerKind.DotScope: Wave.DotScope(canvas, rect, frame, tint); break;
            case VisualizerKind.Vectorscope: Wave.Vectorscope(canvas, rect, frame, tint); break;

            case VisualizerKind.ClassicSpectrum: Classic.Spectrum(canvas, spec, rect, frame); break;
            case VisualizerKind.ClassicScope: Classic.Scope(canvas, rect, frame); break;
            case VisualizerKind.Feedback: Effects.Advance(dt, frame); Classic.Feedback(canvas, rect, frame); break;
            case VisualizerKind.Fire: Classic.Fire(canvas, rect, frame); break;

            case VisualizerKind.Superscope: Effects.Advance(dt, frame); Effects.Superscope(canvas, rect, frame, tint); break;
            case VisualizerKind.Starfield: Effects.Advance(dt, frame); Effects.Starfield(canvas, rect, frame, dt); break;
            case VisualizerKind.Plasma: Effects.Advance(dt, frame); Effects.Plasma(canvas, rect, frame); break;
            case VisualizerKind.Ambience: Effects.Advance(dt, frame); Effects.Ambience(canvas, rect, frame); break;
            case VisualizerKind.Particles: Effects.Advance(dt, frame); Effects.Particles(canvas, rect, frame, dt); break;
            case VisualizerKind.Kaleidoscope: Effects.Advance(dt, frame); Effects.Kaleidoscope(canvas, rect, frame); break;
            case VisualizerKind.BeatPulse: Effects.Advance(dt, frame); Effects.BeatPulse(canvas, rect, frame, theme); break;
            case VisualizerKind.ReactiveBackground: Effects.Advance(dt, frame); Effects.ReactiveBackground(canvas, rect, spec, frame, theme); break;

            case VisualizerKind.Bars:
            default: DrawBars(canvas, spec, rect, frame, theme, mirrored: false); break;
        }
    }


    /// <summary>
    /// Bars above a mirrored, fading reflection, separated by a bright horizon.
    /// Modelled on a reference the user supplied.
    ///
    /// ⛔ The reflection is NOT free height - it is height taken FROM the bars.
    /// The horizon sits at 62% so the bars keep most of the strip and the
    /// reflection gets the remainder, and the reflection is compressed to about
    /// half the bar's height on top of that. Splitting 50/50, the obvious
    /// choice, halves the dynamic range of the actual meter to decorate it.
    ///
    /// Three details carry the effect, and it reads as a plain bar chart without
    /// any of them:
    ///  - the reflection FADES with distance from the horizon, it is not a
    ///    uniform translucent copy;
    ///  - the horizon line is BRIGHTER than the bars, which is what makes the
    ///    lower half read as a surface rather than as more chart;
    ///  - a soft glow sits above tall bars, so peaks bloom instead of stopping
    ///    at a hard edge.
    /// </summary>
    private void DrawReflected(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                               AudioFrame frame, Theme theme)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;
        EnsureColors(spec, theme, n, spec.Palette);

        const float HorizonAt = 0.62f;
        const float ReflectScale = 0.5f;
        const int ReflectSteps = 6;
        const int GlowSteps = 3;

        float horizon = rect.Top + rect.Height * HorizonAt;
        float above = horizon - rect.Top;
        float below = rect.Bottom - horizon;
        float slot = rect.Width / n;
        float gap = Math.Clamp(spec.Gap, 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);

        for (int b = 0; b < n; b++)
        {
            float x = rect.Left + b * slot;
            float h = frame.Bands[b] * above;
            SKColor c = _colors[b];
            if (h < 0.5f) continue;

            _bar.Color = c;
            canvas.DrawRect(x, horizon - h, barW, h, _bar);

            // Bloom above the tip. Cheap, and the difference between "modern"
            // and "a bar chart".
            for (int g = 1; g <= GlowSteps; g++)
            {
                byte a = (byte)(70 / (g + 1));
                _bar.Color = c.WithAlpha(a);
                canvas.DrawRect(x, horizon - h - g, barW, 1f, _bar);
            }

            // The reflection, in steps of decreasing alpha. Stepped rather than
            // shader-filled because a shader is a native object and one per bar
            // per frame is exactly the leak this project has already been bitten
            // by.
            float rh = MathF.Min(h * ReflectScale, below);
            if (rh < 1f) continue;
            float stepH = rh / ReflectSteps;
            for (int s = 0; s < ReflectSteps; s++)
            {
                byte a = (byte)(110 * (1f - (s + 0.5f) / ReflectSteps));
                if (a == 0) continue;
                _bar.Color = c.WithAlpha(a);
                canvas.DrawRect(x, horizon + s * stepH, barW, MathF.Max(1f, stepH), _bar);
            }
        }

        // Horizon last, so it sits over both halves.
        _base.Color = Theme.Parse(spec.PeakColor, SKColors.White).WithAlpha(220);
        canvas.DrawRect(rect.Left, horizon - 0.5f, rect.Width, 1f, _base);

        if (!spec.ShowPeaks) return;
        SKColor capColor = Theme.Parse(spec.PeakColor, SKColors.White);
        for (int b = 0; b < n; b++)
        {
            float p = frame.Peaks[b] * above;
            if (p < 2f) continue;
            _cap.Color = capColor;
            canvas.DrawRect(rect.Left + b * slot, MathF.Max(rect.Top, horizon - p - 1f), barW, 1f, _cap);
        }
    }

    // ---------------------------------------------------------------- bars --

    private void DrawBars(SKCanvas canvas, VisualizerSpec spec, SKRect rect, AudioFrame frame,
                          Theme theme, bool mirrored, VisualizerPalette? force = null)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;
        var palette = force ?? spec.Palette;
        EnsureColors(spec, theme, n, palette);
        Baseline(canvas, spec, rect, theme);

        // Mirrored draws each band TWICE, outward from the centre. On a 13.3:1
        // panel the symmetry reads more strongly than the extra resolution would.
        int slots = mirrored ? n * 2 : n;
        float slot = rect.Width / slots;
        float gap = Math.Clamp(spec.Gap, 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);
        SKColor capColor = Theme.Parse(spec.PeakColor, SKColors.White);

        for (int b = 0; b < n; b++)
        {
            float h = frame.Bands[b] * rect.Height;
            float p = frame.Peaks[b] * rect.Height;
            SKColor c = VisualizerPalettes.ByHeight(palette, frame.Bands[b], theme) ?? _colors[b];

            if (mirrored)
            {
                float mid = rect.Left + rect.Width / 2f;
                Bar(canvas, mid + b * slot, barW, h, p, c, capColor, rect, spec);
                Bar(canvas, mid - (b + 1) * slot, barW, h, p, c, capColor, rect, spec);
            }
            else
            {
                Bar(canvas, rect.Left + b * slot, barW, h, p, c, capColor, rect, spec);
            }
        }
    }

    private void Bar(SKCanvas canvas, float x, float w, float h, float p,
                     SKColor c, SKColor capColor, SKRect rect, VisualizerSpec spec)
    {
        if (h >= 1f)
        {
            _bar.Color = c;
            canvas.DrawRect(x, rect.Bottom - h, w, h, _bar);
        }
        if (!spec.ShowPeaks || p < 2f) return;

        // The cap does most of the work at this size: 48px of height is 1.25 dB
        // per pixel, so the bar alone cannot show a transient the cap makes
        // obvious (D49). Its colour is the spec's, not the band's (D58).
        _cap.Color = capColor;
        canvas.DrawRect(x, MathF.Max(rect.Top, rect.Bottom - p - 1f), w, 1f, _cap);
    }

    /// <summary>
    /// Bars filled with a vertical ramp from the band colour up to a pale tip.
    ///
    /// Drawn as horizontal SLICES rather than with a per-bar shader: a shader
    /// is a native object, and one per bar per frame is 64 native allocations
    /// at 30 fps in the mode most likely to be left running.
    /// </summary>
    private void DrawGradientBars(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                                  AudioFrame frame, Theme theme)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;
        EnsureColors(spec, theme, n, spec.Palette);
        Baseline(canvas, spec, rect, theme);

        const int Slices = 8;
        float slot = rect.Width / n;
        float gap = Math.Clamp(spec.Gap, 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);
        SKColor capColor = Theme.Parse(spec.PeakColor, SKColors.White);

        for (int b = 0; b < n; b++)
        {
            float h = frame.Bands[b] * rect.Height;
            float x = rect.Left + b * slot;
            if (h >= 1f)
            {
                var baseColor = _colors[b];
                var tip = VisualizerPalettes.Lerp(baseColor, SKColors.White, 0.65f);
                float sliceH = h / Slices;
                for (int s = 0; s < Slices; s++)
                {
                    _bar.Color = VisualizerPalettes.Lerp(baseColor, tip, s / (float)(Slices - 1));
                    canvas.DrawRect(x, rect.Bottom - (s + 1) * sliceH, barW, MathF.Max(1f, sliceH), _bar);
                }
            }

            if (!spec.ShowPeaks) continue;
            float p = frame.Peaks[b] * rect.Height;
            if (p < 2f) continue;
            _cap.Color = capColor;
            canvas.DrawRect(x, MathF.Max(rect.Top, rect.Bottom - p - 1f), barW, 1f, _cap);
        }
    }

    /// <summary>
    /// A filled envelope instead of discrete bars, interpolated across columns.
    /// Softer than bars, and the mode that best hides a low band count.
    /// </summary>
    private void DrawCurve(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                           AudioFrame frame, Theme theme)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        int w = (int)rect.Width;
        if (n < 2 || w < 2) return;
        EnsureColors(spec, theme, n, spec.Palette);

        for (int x = 0; x < w; x++)
        {
            float u = x / (float)(w - 1) * (n - 1);
            int i = Math.Clamp((int)u, 0, n - 2);
            float f = u - i;
            float v = frame.Bands[i] * (1f - f) + frame.Bands[i + 1] * f;
            float h = v * rect.Height;
            if (h < 1f) continue;

            _bar.Color = _colors[Math.Clamp((int)MathF.Round(u), 0, n - 1)];
            canvas.DrawRect(rect.Left + x, rect.Bottom - h, 1f, h, _bar);
        }
    }

    /// <summary>
    /// Spectrum rising from the floor and falling from the ceiling, each half
    /// scaled by one channel's level.
    ///
    /// ⛔ Both halves are the MONO spectrum scaled per channel, not two
    /// independent FFTs. That is a deliberate trade - a second analysis chain
    /// would double the DSP cost for a difference invisible at 24 pixels per
    /// half - but it means this shows channel BALANCE, not per-channel content.
    /// Do not describe it as a stereo analyser. <see cref="VisualizerKind.Vectorscope"/>
    /// is the mode that genuinely reads the two channels apart.
    /// </summary>
    private void DrawDual(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                          AudioFrame frame, Theme theme)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;
        EnsureColors(spec, theme, n, spec.Palette);

        float slot = rect.Width / n;
        float gap = Math.Clamp(spec.Gap, 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);
        float half = rect.Height / 2f;
        float lGain = Math.Clamp(frame.RmsLeft * 1.4f, 0f, 1f);
        float rGain = Math.Clamp(frame.RmsRight * 1.4f, 0f, 1f);

        for (int b = 0; b < n; b++)
        {
            float x = rect.Left + b * slot;
            _bar.Color = _colors[b];
            float hl = frame.Bands[b] * lGain * half;
            if (hl >= 1f) canvas.DrawRect(x, rect.Top + half - hl, barW, hl, _bar);

            _bar.Color = _colors[b].WithAlpha(190);
            float hr = frame.Bands[b] * rGain * half;
            if (hr >= 1f) canvas.DrawRect(x, rect.Top + half, barW, hr, _bar);
        }

        _base.Color = theme.DividerColor;
        canvas.DrawRect(rect.Left, rect.Top + half, rect.Width, 1f, _base);
    }

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
        EnsureColors(spec, theme, n, spec.Palette);

        const int Segments = 12;
        float slot = rect.Width / n;
        float gap = Math.Clamp(spec.Gap, 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);
        float segH = rect.Height / Segments;
        float segDraw = MathF.Max(1f, segH - 1f);
        SKColor capColor = Theme.Parse(spec.PeakColor, SKColors.White);

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
                _bar.Color = isPeak ? capColor : c;
                canvas.DrawRect(x, rect.Bottom - (s + 1) * segH, barW, segDraw, _bar);
            }
        }
    }

    /// <summary>
    /// A grid of dots lit by band level. Near-binary, so the 18-bit panel costs
    /// it nothing at all.
    /// </summary>
    private void DrawDotMatrix(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                               AudioFrame frame, Theme theme)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;
        EnsureColors(spec, theme, n, spec.Palette);

        const int Rows = 10;
        float slot = rect.Width / n;
        float rowH = rect.Height / Rows;
        float dot = MathF.Max(1f, MathF.Min(slot - 1f, rowH - 1f));

        for (int b = 0; b < n; b++)
        {
            float x = rect.Left + b * slot + (slot - dot) / 2f;
            int lit = (int)MathF.Round(frame.Bands[b] * Rows);
            for (int r = 0; r < Rows; r++)
            {
                bool on = r < lit;
                // Unlit dots stay faintly visible so the matrix reads as a grid
                // rather than as scattered marks appearing from nowhere.
                _bar.Color = on ? _colors[b] : theme.CaptionColor.WithAlpha(28);
                canvas.DrawRect(x, rect.Bottom - (r + 1) * rowH + (rowH - dot) / 2f, dot, dot, _bar);
            }
        }
    }


    /// <summary>
    /// Capsule bars with a bloom halo - the clean glowing look most current web
    /// and video visualizers use.
    ///
    /// ⛔ The corner radius is capped at half the bar WIDTH and half its HEIGHT.
    /// Without the height cap a short bar with a fixed radius is drawn as a
    /// lens rather than a capsule, and the bottom of the meter turns into a row
    /// of floating lozenges that no longer touch the baseline.
    /// </summary>
    private void DrawGlowPills(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                               AudioFrame frame, Theme theme)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;
        EnsureColors(spec, theme, n, spec.Palette);

        float slot = rect.Width / n;
        float gap = Math.Clamp(Math.Max(1, spec.Gap), 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);
        SKColor capColor = Theme.Parse(spec.PeakColor, SKColors.White);

        for (int b = 0; b < n; b++)
        {
            float h = frame.Bands[b] * rect.Height;
            if (h < 1f) continue;
            float x = rect.Left + b * slot;
            float y = rect.Bottom - h;
            SKColor c = _colors[b];
            float r = MathF.Min(barW / 2f, h / 2f);

            // Halo first, so the solid capsule sits on top of it.
            for (int g = 2; g >= 1; g--)
            {
                _bar.Color = c.WithAlpha((byte)(46 / g));
                canvas.DrawRoundRect(
                    new SKRoundRect(new SKRect(x - g, y - g, x + barW + g, rect.Bottom + g), r + g), _bar);
            }

            _bar.Color = c;
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(x, y, x + barW, rect.Bottom), r), _bar);

            if (!spec.ShowPeaks) continue;
            float p = frame.Peaks[b] * rect.Height;
            if (p < 3f) continue;
            // The cap is a capsule too, or it reads as a stray line across an
            // otherwise entirely rounded display.
            float capY = MathF.Max(rect.Top, rect.Bottom - p - 2f);
            _cap.Color = capColor;
            canvas.DrawRoundRect(
                new SKRoundRect(new SKRect(x, capY, x + barW, capY + 2f), 1f), _cap);
        }
    }

    // --------------------------------------------------------------- level --

    /// <summary>
    /// Two horizontal bars, L over R. Uses the strip's WIDTH for level instead
    /// of its height, which is the one thing 640x48 has in abundance - 640
    /// pixels of meter resolution instead of 48.
    /// </summary>
    private void DrawVu(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                        AudioFrame frame, Theme theme)
    {
        float h = rect.Height / 2f;
        VuRow(canvas, spec, new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + h),
              frame.RmsLeft, frame.PeakLeft, theme);
        VuRow(canvas, spec, new SKRect(rect.Left, rect.Top + h, rect.Right, rect.Bottom),
              frame.RmsRight, frame.PeakRight, theme);
    }

    private void VuRow(SKCanvas canvas, VisualizerSpec spec, SKRect r,
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
        _cap.Color = Theme.Parse(spec.PeakColor, SKColors.White);
        canvas.DrawRect(MathF.Min(px, r.Right - 2f), top, 2f, bot - top, _cap);
    }

    /// <summary>One wide bar for the summed level, with a peak marker. The
    /// minimal mode, and the most legible from across a room.</summary>
    private void DrawLevelBar(SKCanvas canvas, VisualizerSpec spec, SKRect rect,
                              AudioFrame frame, Theme theme)
    {
        float rms = (frame.RmsLeft + frame.RmsRight) * 0.5f;
        float peak = MathF.Max(frame.PeakLeft, frame.PeakRight);

        _base.Color = theme.DividerColor;
        canvas.DrawRect(rect, _base);

        float w = Math.Clamp(rms, 0f, 1f) * rect.Width;
        if (w >= 1f)
        {
            _bar.Color = rms > 0.9f ? theme.HotColor
                       : rms > 0.75f ? theme.WarnColor
                       : Theme.Parse(spec.Color, theme.ValueColor);
            canvas.DrawRect(rect.Left, rect.Top, w, rect.Height, _bar);
        }

        if (!spec.ShowPeaks) return;
        _cap.Color = Theme.Parse(spec.PeakColor, SKColors.White);
        canvas.DrawRect(MathF.Min(rect.Left + peak * rect.Width, rect.Right - 3f),
                        rect.Top, 3f, rect.Height, _cap);
    }

    // --------------------------------------------------------- spectrogram --

    /// <summary>
    /// Time on X, frequency on Y, magnitude as brightness. The mode this panel's
    /// shape suits best: one column per analysis hop across 640 columns is
    /// roughly 14 seconds of audio visible at once.
    /// </summary>
    private void DrawSpectrogram(SKCanvas canvas, SKRect rect, AudioFrame frame, Theme theme)
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
        // the time axis by the ratio of frame rate to hop rate, making the
        // scroll speed depend on the display setting rather than on the audio.
        if (frame.Sequence != _gramSeq && frame.Bands.Length > 0)
        {
            _gramSeq = frame.Sequence;
            int n = frame.Bands.Length;
            for (int y = 0; y < h; y++)
            {
                int b = Math.Clamp((int)((float)(h - 1 - y) / h * n), 0, n - 1);
                _gram.SetPixel(_gramCol, y, Heat(frame.Bands[b], theme));
            }
            _gramCol = (_gramCol + 1) % w;
        }

        // Unwrap the ring in two blits so the newest column lands at the right
        // edge, with no per-frame pixel shifting.
        int tail = w - _gramCol;
        canvas.DrawBitmap(_gram, new SKRect(_gramCol, 0, w, h),
            new SKRect(rect.Left, rect.Top, rect.Left + tail, rect.Bottom), Nearest, _blit);
        if (_gramCol > 0)
            canvas.DrawBitmap(_gram, new SKRect(0, 0, _gramCol, h),
                new SKRect(rect.Left + tail, rect.Top, rect.Right, rect.Bottom), Nearest, _blit);
    }

    private static SKColor Heat(float v, Theme theme)
    {
        v = Math.Clamp(v, 0f, 1f);
        if (v <= 0.001f) return theme.BackgroundColor;
        return v < 0.6f
            ? VisualizerPalettes.Lerp(theme.BackgroundColor, theme.ValueColor, v / 0.6f)
            : VisualizerPalettes.Lerp(theme.ValueColor, theme.HotColor, (v - 0.6f) / 0.4f);
    }

    // ---------------------------------------------------------------- misc --

    private void Baseline(SKCanvas canvas, VisualizerSpec spec, SKRect rect, Theme theme)
    {
        // A 1px seat under every bar. Without it a silent strip is simply blank
        // and gives no sign the visualizer is alive rather than crashed - which
        // on this panel is otherwise indistinguishable.
        if (!spec.ShowBaseline) return;
        _base.Color = theme.CaptionColor.WithAlpha(70);
        canvas.DrawRect(rect.Left, rect.Bottom - 1f, rect.Width, 1f, _base);
    }

    private void EnsureColors(VisualizerSpec spec, Theme theme, int n, VisualizerPalette palette)
    {
        SKColor base_ = Theme.Parse(spec.Color, theme.ValueColor);
        if (_colorBands == n && _colorPalette == palette && _colorBase == base_) return;

        _colorBands = n;
        _colorPalette = palette;
        _colorBase = base_;
        _colors = VisualizerPalettes.Build(palette, n, base_, theme);
    }

    public void Dispose()
    {
        _bar.Dispose();
        _cap.Dispose();
        _base.Dispose();
        _blit.Dispose();
        _gram?.Dispose();
        _classic?.Dispose();
        _wave?.Dispose();
        _effects?.Dispose();
    }
}

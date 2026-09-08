using System.Runtime.InteropServices;
using NexusManager.Audio;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// The stateful effect modes: field effects, particle systems and anything with
/// a simulation behind it.
///
/// ⛔ Every full-pixel effect here composes into a byte buffer and is pushed
/// with ONE Marshal.Copy. SKBitmap.SetPixel per pixel is 30720 managed-to-native
/// calls a frame at this size, which measured 27.7 fps against a 30 fps target
/// while every other mode made 30.0 exactly (D57).
///
/// ⛔ Constants borrowed from full-screen visualizers are re-derived against 48
/// rows. A field tuned for 300 rows of falloff has none at all here (D56).
/// </summary>
public sealed class EffectRenderer : IDisposable
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _blit = new() { IsAntialias = false };
    private readonly SKPaint _line = new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f,
    };
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    private SKBitmap? _bmp;
    private byte[]? _bgra;
    private int _w, _h;

    private float _time;
    private float _hue;
    private SKPoint[] _trace = [];

    private readonly BackgroundPainter _background = new();

    // Particles and stars share one pool shape; separate arrays keep the two
    // simulations from quietly inheriting each other's state.
    private Particle[] _particles = [];
    private Star[] _stars = [];
    private readonly Random _rng = new();

    private struct Particle { public float X, Y, Vx, Vy, Life, Hue; }
    private struct Star { public float X, Y, Z, Speed; }

    /// <summary>Ridgeline history for Terrain: [row, band], newest at row 0.</summary>
    private float[,]? _terrain;
    private long _terrainSeq = -1;

    /// <summary>Advances shared animation clocks. Called once per frame by the
    /// dispatcher so two effects on one screen cannot run at different speeds.</summary>
    public void Advance(float dt, AudioFrame frame)
    {
        _time += dt;
        // Hue drifts continuously and JUMPS on a beat. Continuous alone is
        // hypnotic but unrelated to the music; jump-only reads as a fault.
        _hue += dt * 12f + frame.BeatIntensity * 6f;
    }

    private bool EnsureSurface(SKRect rect)
    {
        int w = Math.Max(1, (int)rect.Width), h = Math.Max(1, (int)rect.Height);
        if (_bmp is not null && _w == w && _h == h) return true;

        _bmp?.Dispose();
        _w = w; _h = h;
        _bgra = new byte[w * h * 4];
        _bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        return true;
    }

    private void Push(SKCanvas canvas, SKRect rect)
    {
        Marshal.Copy(_bgra!, 0, _bmp!.GetPixels(), _bgra!.Length);
        _blit.Color = SKColors.White;
        canvas.DrawBitmap(_bmp, rect, Nearest, _blit);
    }

    private void Set(int x, int y, SKColor c)
    {
        int p = (y * _w + x) * 4;
        _bgra![p] = c.Blue; _bgra[p + 1] = c.Green; _bgra[p + 2] = c.Red; _bgra[p + 3] = 0xFF;
    }

    // -------------------------------------------------------------- plasma --

    /// <summary>
    /// The demoscene sine field: several travelling sinusoids summed and mapped
    /// through a cycling palette.
    ///
    /// ⛔ The spatial frequencies are expressed as a number of cycles ACROSS THE
    /// CELL rather than as pixel divisors. A divisor tuned for 1024x768 gives
    /// less than a tenth of one cycle over 48 rows, so the vertical axis becomes
    /// a flat wash and the whole thing looks like a horizontal gradient.
    /// </summary>
    public void Plasma(SKCanvas canvas, SKRect rect, AudioFrame frame)
    {
        EnsureSurface(rect);
        float t = _time;
        float energy = 0f;
        for (int b = 0; b < frame.Bands.Length; b++) energy += frame.Bands[b];
        energy = frame.Bands.Length > 0 ? energy / frame.Bands.Length : 0f;

        // Cycles across the cell, not pixel divisors.
        float fx = 3.0f + energy * 3f, fy = 2.0f;
        for (int y = 0; y < _h; y++)
        {
            float v = y / (float)_h;
            for (int x = 0; x < _w; x++)
            {
                float u = x / (float)_w;
                float s = MathF.Sin((u * fx + t * 0.7f) * MathF.PI * 2f)
                        + MathF.Sin((v * fy - t * 0.5f) * MathF.PI * 2f)
                        + MathF.Sin(((u + v) * 2.0f + t * 0.9f) * MathF.PI * 2f)
                        + MathF.Sin((MathF.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)) * 6f
                                     - t * 1.3f) * MathF.PI * 2f);
                s = (s + 4f) / 8f;                                   // 0..1
                Set(x, y, VisualizerPalettes.Hue(_hue + s * 220f, 30f + s * 40f));
            }
        }
        Push(canvas, rect);
    }

    // ------------------------------------------------------------ ambience --

    /// <summary>
    /// WMP's "Ambience" lineage: slow drifting colour, no hard edges. Distinct
    /// from <see cref="Plasma"/> by being layered horizontal flow rather than an
    /// interference field, and by taking its colour from the SPECTRUM - the hue
    /// at a column follows the band that sits over it, so the picture is a soft
    /// read of the music rather than an unrelated animation.
    /// </summary>
    public void Ambience(SKCanvas canvas, SKRect rect, AudioFrame frame)
    {
        EnsureSurface(rect);
        int bands = frame.Bands.Length;
        float t = _time * 0.35f;

        for (int x = 0; x < _w; x++)
        {
            float u = x / (float)_w;
            float band = bands > 0 ? frame.Bands[Math.Clamp(x * bands / _w, 0, bands - 1)] : 0f;

            // Two slow waves in antiphase give a drifting vertical centre the
            // eye reads as flow rather than as scrolling.
            float centre = 0.5f + 0.25f * MathF.Sin((u * 1.5f + t) * MathF.PI * 2f)
                                + 0.12f * MathF.Sin((u * 3.1f - t * 1.7f) * MathF.PI * 2f);
            float spread = 0.18f + band * 0.55f;

            for (int y = 0; y < _h; y++)
            {
                float v = y / (float)_h;
                float d = MathF.Abs(v - centre) / spread;
                float glow = MathF.Exp(-d * d * 2.2f);
                Set(x, y, VisualizerPalettes.Hue(_hue + u * 140f + band * 60f, 8f + glow * 52f));
            }
        }
        Push(canvas, rect);
    }

    // ----------------------------------------------------------- starfield --

    /// <summary>
    /// Perspective dots flying at the viewer, accelerated by the beat.
    ///
    /// ⛔ Depth is what makes a starfield: a star's brightness AND speed both
    /// scale with 1/z. Moving dots at a uniform rate produces rain, not a
    /// tunnel.
    /// </summary>
    public void Starfield(SKCanvas canvas, SKRect rect, AudioFrame frame, float dt)
    {
        const int Count = 220;
        if (_stars.Length != Count)
        {
            _stars = new Star[Count];
            for (int i = 0; i < Count; i++) _stars[i] = NewStar(reset: true);
        }

        _fill.Color = SKColors.Black;
        canvas.DrawRect(rect, _fill);

        float cx = rect.Left + rect.Width / 2f, cy = rect.Top + rect.Height / 2f;
        float boost = 1f + frame.BeatIntensity * 4f;

        for (int i = 0; i < _stars.Length; i++)
        {
            ref var s = ref _stars[i];
            s.Z -= s.Speed * boost * dt;
            if (s.Z <= 0.05f) { s = NewStar(reset: false); continue; }

            float k = 1f / s.Z;
            // The strip is 13.3:1, so x is spread far wider than y or every star
            // exits through the top and bottom edges within a few frames and the
            // tunnel never forms.
            float px = cx + s.X * k * rect.Width * 0.5f;
            float py = cy + s.Y * k * rect.Height * 0.5f;
            if (px < rect.Left || px >= rect.Right || py < rect.Top || py >= rect.Bottom) continue;

            byte b = (byte)Math.Clamp(255f * (1f - s.Z), 40, 255);
            _fill.Color = new SKColor(b, b, (byte)Math.Min(255, b + 30));
            float size = s.Z < 0.35f ? 2f : 1f;
            canvas.DrawRect(px, py, size, size, _fill);
        }
    }

    private Star NewStar(bool reset) => new()
    {
        X = (float)_rng.NextDouble() * 2f - 1f,
        Y = (float)_rng.NextDouble() * 2f - 1f,
        Z = reset ? (float)_rng.NextDouble() : 1f,
        Speed = 0.25f + (float)_rng.NextDouble() * 0.5f,
    };

    // ---------------------------------------------------------- superscope --

    /// <summary>
    /// AVS's superscope: a parametric curve whose radius is displaced by the
    /// waveform. The AVS idea that survives being 48px tall, because a closed
    /// figure can be stretched to any aspect ratio without losing its shape.
    /// </summary>
    public void Superscope(SKCanvas canvas, SKRect rect, AudioFrame frame, SKColor color)
    {
        const int Points = 320;
        if (_trace.Length != Points) _trace = new SKPoint[Points];

        var wave = frame.Waveform;
        float cx = rect.Left + rect.Width / 2f, cy = rect.Top + rect.Height / 2f;
        float rx = rect.Width / 2f - 2f, ry = rect.Height / 2f - 2f;

        for (int i = 0; i < Points; i++)
        {
            float u = i / (float)(Points - 1);
            float angle = u * MathF.PI * 2f;
            float s = wave.Length < 2 ? 0f : wave[(int)(u * (wave.Length - 1))];
            // Two lobes, so the figure has structure across a wide cell rather
            // than being one flat ellipse.
            float r = 0.55f + 0.35f * MathF.Sin(angle * 2f + _time) + s * 0.45f;
            _trace[i] = new SKPoint(cx + MathF.Cos(angle) * rx * r, cy + MathF.Sin(angle) * ry * r);
        }

        _line.Color = color;
        canvas.DrawPoints(SKPointMode.Polygon, _trace, _line);
    }

    // ----------------------------------------------------------- particles --

    /// <summary>
    /// WMP's "Particle" lineage: a fountain thrown from the floor, each emitter
    /// fed by the band above it, under gravity.
    /// </summary>
    public void Particles(SKCanvas canvas, SKRect rect, AudioFrame frame, float dt)
    {
        const int Max = 400;
        if (_particles.Length != Max) _particles = new Particle[Max];

        _fill.Color = SKColors.Black;
        canvas.DrawRect(rect, _fill);

        int bands = frame.Bands.Length;
        int spawned = 0;
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0f)
            {
                // Spawn budget per frame, or a loud transient empties the pool
                // into one instant and the fountain stutters.
                if (spawned >= 12 || bands == 0 || frame.Silent) continue;
                int b = _rng.Next(bands);
                float energy = frame.Bands[b];
                if (energy < 0.25f) continue;

                spawned++;
                p.X = rect.Left + (b + 0.5f) / bands * rect.Width;
                p.Y = rect.Bottom - 1f;
                p.Vx = ((float)_rng.NextDouble() - 0.5f) * 24f;
                p.Vy = -energy * (float)(28 + _rng.NextDouble() * 34);
                p.Life = 0.6f + (float)_rng.NextDouble() * 0.7f;
                p.Hue = b / (float)bands * 200f;
                continue;
            }

            p.Life -= dt;
            p.Vy += 62f * dt;                     // gravity, in cell units/s^2
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            if (p.Y >= rect.Bottom || p.X < rect.Left || p.X >= rect.Right) { p.Life = 0f; continue; }

            _fill.Color = VisualizerPalettes.Hue(_hue * 0.3f + p.Hue, 40f + Math.Clamp(p.Life, 0f, 1f) * 35f);
            canvas.DrawRect(p.X, p.Y, 1f, 1f, _fill);
        }
    }


    // -------------------------------------------------------------- blobs --

    /// <summary>
    /// Metaballs: one blob per band, each merging into its neighbours as it
    /// grows. The lava-lamp look.
    ///
    /// ⛔ Only bands within a few slots of a column contribute. A full
    /// all-pairs field is 640 x 48 x 64 = two million evaluations a frame, and
    /// a blob more than a few slots away contributes less than a colour step on
    /// an 18-bit panel - so the window costs nothing visible and turns the mode
    /// from unaffordable into free.
    /// </summary>
    public void Blobs(SKCanvas canvas, SKRect rect, AudioFrame frame, SKColor[] colors)
    {
        EnsureSurface(rect);
        int bands = frame.Bands.Length;
        if (bands == 0) return;

        const int Window = 3;
        float slot = _w / (float)bands;

        for (int x = 0; x < _w; x++)
        {
            int centreBand = Math.Clamp((int)(x / slot), 0, bands - 1);
            int lo = Math.Max(0, centreBand - Window);
            int hi = Math.Min(bands - 1, centreBand + Window);

            for (int y = 0; y < _h; y++)
            {
                float field = 0f;
                for (int b = lo; b <= hi; b++)
                {
                    float energy = frame.Bands[b];
                    if (energy < 0.04f) continue;

                    float bx = (b + 0.5f) * slot;
                    // The blob rides UP as its band gets louder, and grows.
                    float by = _h - 2f - energy * (_h - 6f) * 0.55f;
                    float r = 2.5f + energy * _h * 0.45f;
                    float dx = x - bx, dy = y - by;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < 1f) d2 = 1f;
                    field += r * r / d2;
                }

                if (field < 0.85f) { Set(x, y, SKColors.Black); continue; }
                var c = colors[centreBand];
                // Above the threshold the field keeps rising, so the interior
                // brightens towards the centre of each blob and the merge points
                // read as joins rather than as flat paint.
                float t = Math.Clamp((field - 0.85f) * 0.8f, 0f, 1f);
                Set(x, y, VisualizerPalettes.Lerp(c, SKColors.White, t * 0.7f));
            }
        }
        Push(canvas, rect);
    }

    // ------------------------------------------------------------ terrain --

    /// <summary>
    /// A stack of past spectra as receding ridgelines: the newest at the
    /// bottom, older ones drawn higher, smaller and dimmer.
    ///
    /// ⛔ Rows advance on the analysis HOP, not the frame. Advancing per frame
    /// ties the scroll speed to the display setting instead of to the audio, so
    /// the same music would recede at different rates at 24 and 30 fps.
    /// </summary>
    public void Terrain(SKCanvas canvas, SKRect rect, AudioFrame frame, SKColor[] colors)
    {
        const int Rows = 7;
        int bands = frame.Bands.Length;
        if (bands == 0) return;

        if (_terrain is null || _terrain.GetLength(1) != bands)
        {
            _terrain = new float[Rows, bands];
            _terrainSeq = -1;
        }

        if (frame.Sequence != _terrainSeq)
        {
            _terrainSeq = frame.Sequence;
            for (int r = Rows - 1; r > 0; r--)
                for (int b = 0; b < bands; b++) _terrain[r, b] = _terrain[r - 1, b];
            for (int b = 0; b < bands; b++) _terrain[0, b] = frame.Bands[b];
        }

        _fill.Color = SKColors.Black;
        canvas.DrawRect(rect, _fill);

        // Back to front, so nearer ridges occlude the ones behind them.
        for (int r = Rows - 1; r >= 0; r--)
        {
            float depth = r / (float)(Rows - 1);
            float baseY = rect.Bottom - 2f - depth * (rect.Height * 0.55f);
            float scale = (1f - depth * 0.6f) * rect.Height * 0.42f;
            float inset = depth * rect.Width * 0.06f;
            byte alpha = (byte)(255 - depth * 180);

            float prevY = baseY;
            int w = (int)(rect.Width - inset * 2f);
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)Math.Max(1, w - 1) * (bands - 1);
                int i = Math.Clamp((int)u, 0, bands - 2);
                float f = u - i;
                float v = _terrain[r, i] * (1f - f) + _terrain[r, i + 1] * f;
                float y = baseY - v * scale;

                float top = MathF.Min(prevY, y), bot = MathF.Max(prevY, y);
                if (bot - top < 1f) bot = top + 1f;
                prevY = y;

                _fill.Color = colors[Math.Clamp((int)MathF.Round(u), 0, bands - 1)].WithAlpha(alpha);
                canvas.DrawRect(rect.Left + inset + x, top, 1f, bot - top, _fill);
            }
        }
    }

    // -------------------------------------------------------- kaleidoscope --

    /// <summary>
    /// The "Battery" lineage: the spectrum folded into a figure with two axes of
    /// symmetry. Four-fold symmetry is what a 13.3:1 strip can actually carry -
    /// a true radial kaleidoscope needs height this panel does not have.
    /// </summary>
    public void Kaleidoscope(SKCanvas canvas, SKRect rect, AudioFrame frame)
    {
        int bands = frame.Bands.Length;
        if (bands == 0) return;

        _fill.Color = SKColors.Black;
        canvas.DrawRect(rect, _fill);

        float cx = rect.Left + rect.Width / 2f, cy = rect.Top + rect.Height / 2f;
        int halfW = (int)(rect.Width / 2f);

        for (int i = 0; i < halfW; i++)
        {
            float u = i / (float)halfW;
            float band = frame.Bands[Math.Clamp((int)(u * bands), 0, bands - 1)];
            float h = band * (rect.Height / 2f);
            if (h < 0.5f) continue;

            var c = VisualizerPalettes.Hue(_hue + u * 180f, 35f + band * 30f);
            _fill.Color = c;
            // Mirrored left/right AND up/down from the centre.
            canvas.DrawRect(cx + i, cy - h, 1f, h * 2f, _fill);
            canvas.DrawRect(cx - i - 1, cy - h, 1f, h * 2f, _fill);
        }
    }

    // ---------------------------------------------------------- beat pulse --

    /// <summary>
    /// A colour wash that flashes on detected onsets and decays.
    ///
    /// ⛔ It fills on <see cref="AudioFrame.BeatIntensity"/>, not on
    /// <see cref="AudioFrame.Beat"/>. Beat is true for exactly one hop; painting
    /// on that alone gives a one-frame flash that reads as a glitch. Intensity
    /// is the same event with a decay, which is what a pulse is.
    /// </summary>
    public void BeatPulse(SKCanvas canvas, SKRect rect, AudioFrame frame, Theme theme)
    {
        float level = Math.Clamp(frame.BeatIntensity, 0f, 1f);
        _fill.Color = VisualizerPalettes.Lerp(theme.BackgroundColor,
                                              VisualizerPalettes.Hue(_hue, 55f), level);
        canvas.DrawRect(rect, _fill);

        // A level bar under the wash, so a quiet passage between beats still
        // shows the visualizer is running.
        float w = Math.Clamp(frame.RmsLeft * 0.5f + frame.RmsRight * 0.5f, 0f, 1f) * rect.Width;
        if (w < 1f) return;
        _fill.Color = theme.ValueColor.WithAlpha(180);
        canvas.DrawRect(rect.Left, rect.Bottom - 2f, w, 2f, _fill);
    }

    // ------------------------------------------------- reactive background --

    /// <summary>
    /// The screen's own still or animation, with its zoom and opacity driven by
    /// the music. Reuses <see cref="BackgroundPainter"/> rather than decoding
    /// anything itself - decoding a GIF per frame costs more than the entire
    /// frame budget.
    /// </summary>
    public void ReactiveBackground(SKCanvas canvas, SKRect rect, VisualizerSpec spec,
                                   AudioFrame frame, Theme theme)
    {
        float level = Math.Clamp(frame.RmsLeft * 0.5f + frame.RmsRight * 0.5f, 0f, 1f);
        var src = spec.Background;

        // A copy, because the spec is the user's saved configuration and must not
        // drift because a loud passage went past.
        var pulsed = new BackgroundSpec
        {
            Image = src.Image,
            Fit = src.Fit,
            Loop = src.Loop,
            Speed = src.Speed,
            ScrollSpeed = src.ScrollSpeed,
            FocusX = src.FocusX,
            FocusY = src.FocusY,
            Zoom = src.Zoom * (1.0 + level * 0.10 + frame.BeatIntensity * 0.06),
            Opacity = (byte)Math.Clamp(src.Opacity * (0.55 + level * 0.45), 0, 255),
        };

        canvas.Save();
        canvas.ClipRect(rect);
        canvas.Translate(rect.Left, rect.Top);
        _background.Draw(canvas, pulsed, theme.BackgroundColor, TimeSpan.FromSeconds(_time));
        canvas.Restore();
    }

    public void Dispose()
    {
        _fill.Dispose();
        _blit.Dispose();
        _line.Dispose();
        _bmp?.Dispose();
        _background.Dispose();
    }
}

using NexusManager.Audio;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// The Winamp-heritage modes.
///
/// Kept out of <see cref="VisualizerRenderer"/> because these carry real state -
/// a feedback buffer, a fire grid, a cached gradient - while the spectrum modes
/// are pure functions of the current frame. Mixing the two made one class that
/// was half stateless and half not, which is how a buffer ends up shared between
/// two modes that both think they own it.
///
/// ⛔ Same native-handle rule as everywhere else: allocate once, dispose in
/// <see cref="Dispose"/>. The feedback mode is the single heaviest thing in the
/// app - it touches every pixel of the cell every frame - so nothing in its path
/// may allocate.
/// </summary>
public sealed class WinampRenderer : IDisposable
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _blit = new() { IsAntialias = false };
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>Winamp's vertical ramp, rebuilt only when the cell resizes.</summary>
    private SKShader? _ramp;
    private SKRect _rampRect;

    // Feedback (MilkDrop in miniature).
    private SKBitmap? _fb, _fbBack;
    private SKCanvas? _fbCanvas, _fbBackCanvas;
    private float _fbAngle;

    // Fire.
    private byte[]? _fire;
    private int _fireW, _fireH;
    private SKBitmap? _fireBmp;
    private byte[]? _fireBgra;
    private readonly uint[] _firePalette = new uint[256];
    private bool _firePaletteBuilt;

    /// <summary>Polyline scratch. SKCanvas.DrawPoints takes an ARRAY, not a
    /// span, so this is sized to the cell width and reused - resized only
    /// when the cell changes, never per frame. Passing a sliced copy each
    /// frame would allocate 640 points 30 times a second.</summary>
    private SKPoint[] _trace = [];

    // ------------------------------------------------------------ spectrum --

    /// <summary>
    /// The main-window analyser. What makes it read as Winamp rather than as a
    /// generic bar chart is that the colour ramp is anchored to the CELL, not to
    /// each bar: every bar shares one green-at-the-bottom to red-at-the-top
    /// gradient, so a tall bar goes red at its tip while a short one stays green
    /// throughout. Colouring each bar by its own height instead - the obvious
    /// implementation - looks nothing like it.
    /// </summary>
    public void Spectrum(SKCanvas canvas, VisualizerSpec spec, SKRect rect, AudioFrame frame, Theme theme)
    {
        int n = Math.Min(spec.EffectiveBands, frame.Bands.Length);
        if (n <= 0 || rect.Width < n) return;

        if (_ramp is null || _rampRect != rect)
        {
            _ramp?.Dispose();
            _rampRect = rect;
            // One shader for every bar, in canvas space. Allocated on resize
            // only - creating it per frame would be a native leak with no GC
            // pressure to warn about it.
            _ramp = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Bottom),
                new SKPoint(rect.Left, rect.Top),
                [new SKColor(0x00, 0xC8, 0x18), new SKColor(0xC8, 0xC8, 0x00), new SKColor(0xC8, 0x28, 0x00)],
                [0f, 0.55f, 1f],
                SKShaderTileMode.Clamp);
        }

        float slot = rect.Width / n;
        float gap = Math.Clamp(spec.Gap, 0, (int)(slot / 2));
        float barW = MathF.Max(1f, slot - gap);

        _fill.Shader = _ramp;
        for (int b = 0; b < n; b++)
        {
            float h = frame.Bands[b] * rect.Height;
            if (h < 1f) continue;
            canvas.DrawRect(rect.Left + b * slot, rect.Bottom - h, barW, h, _fill);
        }
        _fill.Shader = null;

        if (!spec.ShowPeaks) return;
        _fill.Color = new SKColor(0xD0, 0xD0, 0xD8);
        for (int b = 0; b < n; b++)
        {
            float p = frame.Peaks[b] * rect.Height;
            if (p < 2f) continue;
            canvas.DrawRect(rect.Left + b * slot, MathF.Max(rect.Top, rect.Bottom - p - 1f), barW, 1f, _fill);
        }
    }

    // --------------------------------------------------------------- scope --

    /// <summary>
    /// The main-window scope: a CONNECTED trace, each column spanning from the
    /// previous sample to this one, coloured by its own amplitude. Drawing
    /// unconnected dots is the usual mistake and produces a dotted cloud at any
    /// signal level above a whisper.
    /// </summary>
    public void Scope(SKCanvas canvas, SKRect rect, AudioFrame frame, Theme theme)
    {
        int w = (int)rect.Width;
        if (w < 2 || frame.Waveform.Length < 2) return;

        float mid = rect.Top + rect.Height / 2f;
        float half = rect.Height / 2f - 1f;
        int n = frame.Waveform.Length;
        float prev = mid;

        for (int x = 0; x < w; x++)
        {
            float s = frame.Waveform[(int)((long)x * (n - 1) / (w - 1))];
            float y = mid - Math.Clamp(s, -1f, 1f) * half;

            float top = MathF.Min(prev, y), bot = MathF.Max(prev, y);
            if (bot - top < 1f) bot = top + 1f;
            prev = y;

            float amp = Math.Clamp(MathF.Abs(s) * 2.2f, 0f, 1f);
            _fill.Color = amp < 0.5f
                ? Lerp(new SKColor(0x18, 0xC8, 0x18), new SKColor(0xC8, 0xC8, 0x18), amp * 2f)
                : Lerp(new SKColor(0xC8, 0xC8, 0x18), new SKColor(0xC8, 0x28, 0x18), (amp - 0.5f) * 2f);
            canvas.DrawRect(rect.Left + x, top, 1f, bot - top, _fill);
        }
    }

    // ------------------------------------------------------------ feedback --

    /// <summary>
    /// MilkDrop's actual mechanic, at 640x48: keep the last frame, redraw it
    /// slightly zoomed and rotated with its brightness decayed, then draw this
    /// frame's waveform into it. Everything that looks like a "preset" in
    /// MilkDrop is a choice of warp and decay on top of exactly this loop.
    ///
    /// On a strip the zoom reads as a horizontal tunnel rather than the square
    /// vortex it makes on a desktop, which suits the panel better than it has
    /// any right to.
    /// </summary>
    public void Feedback(SKCanvas canvas, SKRect rect, AudioFrame frame, Theme theme)
    {
        int w = Math.Max(1, (int)rect.Width), h = Math.Max(1, (int)rect.Height);
        if (_fb is null || _fb.Width != w || _fb.Height != h)
        {
            DisposeFeedback();
            _fb = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            _fbBack = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            _fbCanvas = new SKCanvas(_fb);
            _fbBackCanvas = new SKCanvas(_fbBack);
            _fbCanvas.Clear(SKColors.Black);
            _fbBackCanvas.Clear(SKColors.Black);
        }

        // ⛔ PING-PONG, not a self-draw. Reading a bitmap while drawing into the
        // same bitmap is undefined, and the tempting fix - snapshotting with
        // SKImage.FromBitmap every frame - allocates a native image per frame in
        // the hottest path in the app.
        var src = _fb!;
        var dstCanvas = _fbBackCanvas!;

        float bass = 0f;
        for (int b = 0; b < Math.Min(4, frame.Bands.Length); b++) bass = MathF.Max(bass, frame.Bands[b]);
        float zoom = 1.010f + bass * 0.030f;
        _fbAngle += 0.15f + bass * 0.6f;

        // Decay by drawing the previous frame back dimmed. A translucent BLACK
        // wash instead would only ever approach black: on an 18-bit panel it
        // stalls on the low bits and leaves permanent ghosts.
        dstCanvas.Clear(SKColors.Black);
        dstCanvas.Save();
        dstCanvas.Translate(w / 2f, h / 2f);
        dstCanvas.Scale(zoom, zoom);
        dstCanvas.RotateDegrees(MathF.Sin(_fbAngle * 0.01f) * 1.2f);
        dstCanvas.Translate(-w / 2f, -h / 2f);
        _blit.Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE4);          // ~0.89 decay
        dstCanvas.DrawBitmap(src, new SKRect(0, 0, w, h), Linear, _blit);
        dstCanvas.Restore();

        // This frame's trace, bright enough to survive several decays.
        int n = frame.Waveform.Length;
        if (n >= 2 && w >= 2)
        {
            float mid = h / 2f, half = h / 2f - 1f;
            int count = w;
            if (_trace.Length != count) _trace = new SKPoint[count];
            for (int x = 0; x < count; x++)
            {
                float s = frame.Waveform[(int)((long)x * (n - 1) / (count - 1))];
                _trace[x] = new SKPoint(x, mid - Math.Clamp(s, -1f, 1f) * half);
            }
            _fill.Color = Heat(bass, theme);
            _fill.Style = SKPaintStyle.Stroke;
            _fill.StrokeWidth = 1f;
            dstCanvas.DrawPoints(SKPointMode.Polygon, _trace, _fill);
            _fill.Style = SKPaintStyle.Fill;
        }

        _blit.Color = SKColors.White;
        canvas.DrawBitmap(_fbBack!, rect, Nearest, _blit);

        // Swap: this frame becomes next frame's source.
        (_fb, _fbBack) = (_fbBack, _fb);
        (_fbCanvas, _fbBackCanvas) = (_fbBackCanvas, _fbCanvas);
    }

    private void DisposeFeedback()
    {
        _fbCanvas?.Dispose(); _fbBackCanvas?.Dispose();
        _fb?.Dispose(); _fbBack?.Dispose();
        _fbCanvas = _fbBackCanvas = null;
        _fb = _fbBack = null;
    }
    // ---------------------------------------------------------------- fire --

    /// <summary>
    /// The demoscene fire routine, bottom row seeded from bass. Each cell is the
    /// average of the three below it minus a decay, which is why it costs one
    /// pass over the grid and nothing else.
    /// </summary>
    /// <summary>
    /// The demoscene fire routine, bottom row seeded from the spectrum.
    ///
    /// ⛔ TWO bugs were fixed here after it shipped looking like a formless
    /// orange wash, and the first is the interesting one.
    ///
    /// 1. THE DECAY WAS TUNED FOR A SCREEN, NOT A STRIP. The classic routine
    ///    subtracts 1-2 per row, which is right when a flame has 300 rows to die
    ///    out in. This panel gives it 48. A full-strength seed of 255 losing
    ///    ~1.33 a row arrives at the top still at ~192 - which this palette maps
    ///    to near-white. Every pixel was therefore lit, the flame had no tip, and
    ///    the whole strip read as a flat wash with no structure. Decay is now
    ///    derived FROM THE HEIGHT so a flame always dies inside the panel,
    ///    whatever its size.
    ///
    /// 2. SetPixel per pixel cost more than the rest of the app combined:
    ///    30720 managed-to-native calls a frame held this mode to 27.7 fps
    ///    against a 30 fps target while every other mode made 30.0 exactly. The
    ///    grid is now composed into a byte buffer and pushed in ONE copy.
    /// </summary>
    public void Fire(SKCanvas canvas, SKRect rect, AudioFrame frame, Theme theme)
    {
        int w = Math.Max(1, (int)rect.Width), h = Math.Max(1, (int)rect.Height);
        if (_fire is null || _fireW != w || _fireH != h)
        {
            _fire = new byte[w * h];
            _fireBgra = new byte[w * h * 4];
            _fireW = w; _fireH = h;
            _fireBmp?.Dispose();
            _fireBmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        }
        if (!_firePaletteBuilt) BuildFirePalette();

        var fire = _fire!;
        var rng = Random.Shared;

        // Seed row: each column takes the band above it, so the flame is tallest
        // where the music has energy rather than uniformly random. That is the
        // difference between "fire" and "fire that is clearly on the track".
        int bands = frame.Bands.Length;
        for (int x = 0; x < w; x++)
        {
            float v = bands > 0 ? frame.Bands[Math.Clamp(x * bands / w, 0, bands - 1)] : 0f;
            fire[(h - 1) * w + x] = (byte)Math.Clamp((int)(v * 255f) - rng.Next(0, 40), 0, 255);
        }

        // Decay derived from the height: a seed at full scale must reach zero
        // within the panel or there is no flame tip, only a wash.
        float perRow = 255f / h;

        for (int y = 0; y < h - 1; y++)
        {
            int row = y * w, below = (y + 1) * w;
            for (int x = 0; x < w; x++)
            {
                int sum = fire[below + x]
                        + fire[below + (x > 0 ? x - 1 : x)]
                        + fire[below + (x < w - 1 ? x + 1 : x)]
                        + fire[Math.Min(h - 1, y + 2) * w + x];
                int v = sum / 4 - (int)(perRow * (0.8f + rng.NextSingle() * 1.2f));
                fire[row + x] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
            }
        }

        // One buffer, one copy. See note 2 above.
        var bgra = _fireBgra!;
        for (int i = 0, p = 0; i < fire.Length; i++, p += 4)
        {
            uint c = _firePalette[fire[i]];
            bgra[p]     = (byte)c;
            bgra[p + 1] = (byte)(c >> 8);
            bgra[p + 2] = (byte)(c >> 16);
            bgra[p + 3] = 0xFF;
        }
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, _fireBmp!.GetPixels(), bgra.Length);

        _blit.Color = SKColors.White;
        canvas.DrawBitmap(_fireBmp!, rect, Nearest, _blit);
    }

    /// <summary>Packed BGRA, so the composite loop writes bytes rather than
    /// constructing an SKColor per pixel.</summary>
    private void BuildFirePalette()
    {
        for (int i = 0; i < 256; i++)
        {
            // Black -> red -> orange -> yellow -> white.
            byte r = (byte)Math.Clamp(i * 4, 0, 255);
            byte g = (byte)Math.Clamp((i - 64) * 4, 0, 255);
            byte b = (byte)Math.Clamp((i - 160) * 5, 0, 255);
            _firePalette[i] = (uint)((r << 16) | (g << 8) | b);
        }
        _firePaletteBuilt = true;
    }
    private static SKColor Heat(float v, Theme theme)
    {
        v = Math.Clamp(v, 0f, 1f);
        return Lerp(theme.ValueColor, theme.HotColor, v);
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red   + (b.Red   - a.Red)   * t),
            (byte)(a.Green + (b.Green - a.Green) * t),
            (byte)(a.Blue  + (b.Blue  - a.Blue)  * t));
    }

    public void Dispose()
    {
        _fill.Dispose();
        _blit.Dispose();
        _ramp?.Dispose();
        DisposeFeedback();
        _fireBmp?.Dispose();
    }
}

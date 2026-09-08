using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// A still or animated image, decoded once and held as panel-sized frames.
///
/// This is the piece that makes custom animations possible, and it is worth
/// saying where the requirement came from: the .cuescreens format has NO
/// animation vocabulary at all. Every XML element across Corsair's six official
/// packs was enumerated and there is no frame, duration, fps or loop field
/// anywhere. The animation lives entirely INSIDE the image file - iCUE accepts
/// .bmp/.jpg/.png/.gif for a screen background, and an animated GIF simply plays.
/// Corsair's own FAQ puts that playback at 24 fps with no size limit, which is
/// where this project's 24 fps target came from in the first place.
///
/// So supporting animation needs no format extension: it needs a decoder that
/// honours per-frame delays, and a clock. SkiaSharp's SKCodec gives both for GIF
/// and animated WebP.
/// </summary>
public sealed class AnimatedImage : IDisposable
{
    private readonly SKBitmap[] _frames;
    private readonly int[] _endMs;      // cumulative end time of each frame

    /// <summary>Size the frames were rendered at. Not always the panel size: a
    /// scrolling background keeps its full width so it has something to scroll.
    /// </summary>
    public SKSizeI Size { get; }

    public int FrameCount => _frames.Length;
    public bool IsAnimated => _frames.Length > 1;

    /// <summary>Total length of one pass. Zero for a still image.</summary>
    public TimeSpan Duration => TimeSpan.FromMilliseconds(
        _frames.Length <= 1 ? 0 : _endMs[^1]);

    /// <summary>Decoded bytes held. A 640x48 frame is 120 KB, and Corsair place no
    /// limit on GIF length, so a long animation is worth being able to report.
    /// </summary>
    public long BytesUsed => (long)_frames.Length * Size.Width * Size.Height * 4;

    /// <summary>Source path, kept so a config reload can tell whether the cached
    /// decode is still the right one.</summary>
    public string Path { get; }

    private AnimatedImage(string path, SKBitmap[] frames, int[] endMs, SKSizeI size)
    {
        Path = path; _frames = frames; _endMs = endMs; Size = size;
    }

    /// <summary>
    /// Decodes <paramref name="path"/>, scaling every frame the way
    /// <paramref name="fit"/> asks for.
    /// </summary>
    /// <param name="maxFrames">Hard cap. A pathological GIF should degrade to its
    /// first N frames rather than exhausting memory on a machine whose panel is
    /// 640x48.</param>
    public static AnimatedImage? Load(
        string path, int panelWidth, int panelHeight, BackgroundFit fit,
        double zoom, double focusX, double focusY,
        out string? error, int maxFrames = 600)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) { error = $"no such file: {path}"; return null; }

            using var codec = SKCodec.Create(path);
            if (codec is null) { error = $"not a decodable image: {path}"; return null; }

            var src = codec.Info;
            if (src.Width <= 0 || src.Height <= 0) { error = "image has no pixels"; return null; }

            var target = TargetSize(src.Width, src.Height, panelWidth, panelHeight, fit, zoom);
            int count = Math.Max(1, codec.FrameCount);
            bool truncated = count > maxFrames;
            if (truncated) count = maxFrames;

            var infos = codec.FrameCount > 0 ? codec.FrameInfo : [];
            var frames = new SKBitmap[count];
            var endMs = new int[count];

            // One working bitmap at SOURCE size, decoded into repeatedly. GIF
            // frames are differential: frame N may only carry the pixels that
            // changed, with RequiredFrame naming what it composites onto. Decoding
            // each frame into a fresh buffer loses that and yields fragments on a
            // transparent field.
            var workInfo = new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var work = new SKBitmap(workInfo);

            int cumulative = 0;
            for (int i = 0; i < count; i++)
            {
                var options = new SKCodecOptions(i);
                if (i > 0 && infos.Length > i && infos[i].RequiredFrame == i - 1)
                    options = new SKCodecOptions(i, i - 1);

                var result = codec.GetPixels(workInfo, work.GetPixels(), work.RowBytes, options);
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    if (i == 0) { error = $"decode failed: {result}"; return null; }
                    // A truncated animation is still usable; keep what decoded.
                    Array.Resize(ref frames, i);
                    Array.Resize(ref endMs, i);
                    break;
                }

                frames[i] = Present(work, target, panelWidth, panelHeight, fit, zoom, focusX, focusY);

                // GIF delays of 0 or 10 ms mean "as fast as possible", which every
                // renderer since Netscape has shown at 100 ms. Honouring the raw
                // value instead makes such a GIF a strobe.
                int d = infos.Length > i ? infos[i].Duration : 0;
                if (d <= 10) d = 100;
                cumulative += d;
                endMs[i] = cumulative;
            }

            if (frames.Length == 0) { error = "no frames decoded"; return null; }
            if (truncated)
                error = $"animation truncated to {maxFrames} frames";

            return new AnimatedImage(path, frames, endMs, target);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Size the stored frames should be, given the fit.</summary>
    private static SKSizeI TargetSize(int w, int h, int panelW, int panelH, BackgroundFit fit, double zoom)
    {
        switch (fit)
        {
            case BackgroundFit.ScrollLeft:
            case BackgroundFit.ScrollRight:
            {
                // Height-matched, full width preserved: the extra width IS the
                // thing being scrolled. A source no wider than the panel still
                // works - the painter lays down as many copies as it takes to
                // cover the strip, so the wrap is seamless at any width.
                float scale = panelH / (float)h * (float)Clamp(zoom);
                int width = Math.Max(1, (int)MathF.Round(w * scale));
                return new SKSizeI(width, panelH);
            }
            case BackgroundFit.Tile:
                return new SKSizeI(w, h);
            default:
                return new SKSizeI(panelW, panelH);
        }
    }

    /// <summary>Renders the working frame into its stored presentation form.</summary>
    /// <summary>Zoom is clamped rather than validated: a config that has been
    /// hand-edited to 0 or to 500 should still draw something.</summary>
    private static double Clamp(double zoom) => Math.Clamp(zoom, 1.0, 12.0);

    private static SKBitmap Present(
        SKBitmap work, SKSizeI target, int panelW, int panelH, BackgroundFit fit,
        double zoom, double focusX, double focusY)
    {
        var bmp = new SKBitmap(new SKImageInfo(target.Width, target.Height,
                                               SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        float sw = work.Width, sh = work.Height;
        switch (fit)
        {
            case BackgroundFit.Stretch:
                canvas.DrawBitmap(work, new SKRect(0, 0, panelW, panelH), sampling, paint);
                break;

            case BackgroundFit.Cover:
            {
                // Cover, then zoom in further, then choose WHICH part shows.
                // On a 13:1 strip almost every real image is cropped hard, so
                // which part is not a detail - it is the whole decision.
                float scale = MathF.Max(panelW / sw, panelH / sh) * (float)Clamp(zoom);
                float dw = sw * scale, dh = sh * scale;
                // Slack is how far the image can travel behind the viewport;
                // focus says where in that travel it sits. 0 = left/top edge.
                float x = -(dw - panelW) * (float)Math.Clamp(focusX, 0, 1);
                float y = -(dh - panelH) * (float)Math.Clamp(focusY, 0, 1);
                canvas.DrawBitmap(work, new SKRect(x, y, x + dw, y + dh), sampling, paint);
                break;
            }

            case BackgroundFit.Contain:
            {
                float scale = MathF.Min(panelW / sw, panelH / sh);
                float dw = sw * scale, dh = sh * scale;
                canvas.DrawBitmap(work,
                    new SKRect((panelW - dw) / 2, (panelH - dh) / 2,
                               (panelW - dw) / 2 + dw, (panelH - dh) / 2 + dh),
                    sampling, paint);
                break;
            }

            case BackgroundFit.Center:
                canvas.DrawBitmap(work, (panelW - sw) / 2, (panelH - sh) / 2, sampling, paint);
                break;

            case BackgroundFit.ScrollLeft:
            case BackgroundFit.ScrollRight:
            {
                // Height-matched then zoomed, so a zoomed scroll has vertical
                // slack of its own and focusY chooses which band of the image
                // travels past. Drawing the source into the stored rect instead
                // would squash it, because the rect is only panelH tall.
                float scale = panelH / sh * (float)Clamp(zoom);
                float dw = sw * scale, dh = sh * scale;
                float y = -(dh - target.Height) * (float)Math.Clamp(focusY, 0, 1);
                canvas.DrawBitmap(work, new SKRect(0, y, dw, y + dh), sampling, paint);
                break;
            }

            default:  // Tile: native size, laid down repeatedly by the painter
                canvas.DrawBitmap(work, new SKRect(0, 0, target.Width, target.Height), sampling, paint);
                break;
        }
        return bmp;
    }

    /// <summary>
    /// The frame showing at <paramref name="elapsed"/>. Wall-clock driven rather
    /// than tick-counted, so playback runs at the animation's own rate whatever
    /// the panel's frame rate is set to - a 10 fps GIF does not speed up because
    /// the panel renders at 24.
    /// </summary>
    public SKBitmap FrameAt(TimeSpan elapsed, bool loop = true, double speed = 1.0)
    {
        if (_frames.Length == 1) return _frames[0];

        int total = _endMs[^1];
        if (total <= 0) return _frames[0];

        double ms = elapsed.TotalMilliseconds * (speed <= 0 ? 1.0 : speed);
        if (loop) ms %= total;
        else if (ms >= total) return _frames[^1];

        // Frames are few enough that a scan beats a binary search's branchiness,
        // and it starts where playback usually is.
        for (int i = 0; i < _endMs.Length; i++)
            if (ms < _endMs[i]) return _frames[i];
        return _frames[^1];
    }

    public void Dispose()
    {
        // SKBitmap wraps native memory the GC cannot see: undisposed, these leak
        // with zero heap pressure and nothing to show for it in a GC probe.
        foreach (var f in _frames) f?.Dispose();
    }
}

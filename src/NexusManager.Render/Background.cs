using System.Text.Json.Serialization;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>How a background image is mapped onto the 640x48 strip.</summary>
public enum BackgroundFit
{
    /// <summary>Squash to exactly 640x48. What Corsair's own 640x48 packs need.</summary>
    Stretch,
    /// <summary>Scale to fill, cropping the overflow. The sane default for a photo.</summary>
    Cover,
    /// <summary>Scale to fit entirely, letterboxing the rest.</summary>
    Contain,
    /// <summary>No scaling; centre it.</summary>
    Center,
    /// <summary>Repeat at native size.</summary>
    Tile,
    /// <summary>Height-matched and panned leftwards, looping. A 640x48 strip is a
    /// 13:1 letterbox, so an ordinary image has to move to be seen at all.</summary>
    ScrollLeft,
    ScrollRight,
}

/// <summary>
/// A screen's background: a colour, a still image, or an animation.
///
/// Corsair express animation purely as "the background file happens to be a
/// GIF" - their .cuescreens format carries no animation fields whatsoever. This
/// keeps that compatibility (point at a .gif and it plays) and adds the parts
/// they left out: playback speed, a scroll mode for images that are not already
/// 13:1, and an opacity so a background can sit UNDER live readings instead of
/// competing with them.
/// </summary>
public sealed class BackgroundSpec
{
    /// <summary>Path to a .png/.jpg/.bmp/.gif/.webp. Null means the theme colour
    /// alone. Animated GIF and animated WebP both play.</summary>
    public string? Image { get; set; }

    public BackgroundFit Fit { get; set; } = BackgroundFit.Cover;

    /// <summary>Repeat the animation. False holds the last frame.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Playback rate multiplier. 1.0 plays at the file's own timing.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Pixels per second for the scrolling fits.</summary>
    public double ScrollSpeed { get; set; } = 24;

    /// <summary>
    /// How far into the image to zoom before cropping. 1 shows as much as the
    /// strip can hold; higher shows less of the image, larger.
    /// </summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>
    /// Which part of an oversized image lands on the strip, as a fraction of
    /// the source: 0 is the left/top edge, 0.5 the middle, 1 the right/bottom.
    ///
    /// Stored normalised rather than in pixels so it survives swapping the image
    /// for one of a different size, and so it means the same thing whatever the
    /// zoom is. A 640x48 strip is a 13:1 letterbox, so almost every real image
    /// is cropped hard and WHICH part shows is the whole decision.
    /// </summary>
    public double FocusX { get; set; } = 0.5;
    public double FocusY { get; set; } = 0.5;

    /// <summary>0-255. Sensor readings have to stay legible on top, and a
    /// full-strength photograph behind white numbers is unreadable.</summary>
    public byte Opacity { get; set; } = 255;

    [JsonIgnore] public bool HasImage => !string.IsNullOrWhiteSpace(Image);

    /// <summary>Identity of the decode this spec would produce, so a painter can
    /// tell whether its cached frames are still the right ones.</summary>
    [JsonIgnore] public string CacheKey =>
        $"{Image}|{Fit}|{Zoom:F4}|{FocusX:F4}|{FocusY:F4}";
}

/// <summary>
/// Draws a <see cref="BackgroundSpec"/>, holding the decoded frames between
/// calls. Decoding per frame would be absurd - a 640x48 GIF decode costs far
/// more than the 41 ms frame budget it has to fit inside.
/// </summary>
public sealed class BackgroundPainter : IDisposable
{
    private AnimatedImage? _image;
    private string _key = "";

    /// <summary>Why the last load failed, or a warning from it. Surfaced rather
    /// than swallowed: a mistyped path must not present as "the feature does
    /// nothing".</summary>
    public string? LastError { get; private set; }

    public AnimatedImage? Image => _image;

    public void Draw(SKCanvas canvas, BackgroundSpec? spec, SKColor fallback, TimeSpan elapsed)
    {
        canvas.Clear(fallback);
        if (spec is null || !spec.HasImage) { Release(); return; }

        if (_key != spec.CacheKey)
        {
            Release();
            _image = AnimatedImage.Load(
                spec.Image!, NexusCanvas.Width, NexusCanvas.Height, spec.Fit,
                spec.Zoom, spec.FocusX, spec.FocusY, out string? err);
            LastError = err;
            _key = spec.CacheKey;
        }
        if (_image is null) return;

        var frame = _image.FrameAt(elapsed, spec.Loop, spec.Speed);
        using var paint = new SKPaint { IsAntialias = false };
        if (spec.Opacity < 255) paint.Color = SKColors.White.WithAlpha(spec.Opacity);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);

        switch (spec.Fit)
        {
            case BackgroundFit.Tile:
                for (int y = 0; y < NexusCanvas.Height; y += Math.Max(1, frame.Height))
                for (int x = 0; x < NexusCanvas.Width;  x += Math.Max(1, frame.Width))
                    canvas.DrawBitmap(frame, x, y, sampling, paint);
                break;

            case BackgroundFit.ScrollLeft:
            case BackgroundFit.ScrollRight:
            {
                int w = Math.Max(1, frame.Width);
                double travel = elapsed.TotalSeconds * spec.ScrollSpeed;
                // Wrapped with a positive modulo: the C# % of a negative left edge
                // is negative, which leaves a gap at the seam once per loop.
                double offset = spec.Fit == BackgroundFit.ScrollLeft
                    ? -(travel % w)
                    : (travel % w) - w;
                // Two copies cover the seam; a third is needed when the image is
                // narrower than the panel.
                for (double x = offset; x < NexusCanvas.Width; x += w)
                    canvas.DrawBitmap(frame, (float)x, 0, sampling, paint);
                break;
            }

            default:
                canvas.DrawBitmap(frame, 0, 0, sampling, paint);
                break;
        }
    }

    private void Release()
    {
        _image?.Dispose();
        _image = null;
        _key = "";
    }

    public void Dispose() => Release();
}

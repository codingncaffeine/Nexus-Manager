using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// The WHOLE first frame of an image, scaled to fit a box - deliberately not
/// cropped.
///
/// <see cref="AnimatedImage"/> stores frames already cropped to the panel, which
/// is the wrong thing to show someone who is choosing what to crop. This decodes
/// the source as it really is, so the editor can draw a crop rectangle over it.
/// </summary>
public static class ImagePreview
{
    public sealed record Result(SKBitmap Bitmap, int SourceWidth, int SourceHeight);

    public static Result? FirstFrame(string path, int maxWidth, int maxHeight, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) { error = $"no such file: {path}"; return null; }
            using var codec = SKCodec.Create(path);
            if (codec is null) { error = "not a decodable image"; return null; }

            var src = codec.Info;
            if (src.Width <= 0 || src.Height <= 0) { error = "image has no pixels"; return null; }

            var info = new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var work = new SKBitmap(info);
            var result = codec.GetPixels(info, work.GetPixels(), work.RowBytes, new SKCodecOptions(0));
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                error = $"decode failed: {result}";
                return null;
            }

            // Contain, never enlarged: a 64x64 source should not be blown up to
            // fill the box and imply detail that is not there.
            float scale = MathF.Min(1f, MathF.Min(maxWidth / (float)src.Width,
                                                  maxHeight / (float)src.Height));
            int w = Math.Max(1, (int)MathF.Round(src.Width * scale));
            int h = Math.Max(1, (int)MathF.Round(src.Height * scale));

            var shown = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(shown))
            {
                canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawBitmap(work, new SKRect(0, 0, w, h),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            }
            return new Result(shown, src.Width, src.Height);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The fraction of the source that reaches the panel, given the fit and zoom.
    /// The editor draws its crop rectangle from this, so the rectangle and the
    /// panel agree by construction rather than by two copies of the same sum.
    /// </summary>
    public static (double Width, double Height) VisibleFraction(
        int sourceWidth, int sourceHeight, BackgroundFit fit, double zoom)
    {
        double sw = Math.Max(1, sourceWidth), sh = Math.Max(1, sourceHeight);
        double z = Math.Clamp(zoom, 1.0, 12.0);

        switch (fit)
        {
            case BackgroundFit.Cover:
            {
                double scale = Math.Max(NexusCanvas.Width / sw, NexusCanvas.Height / sh) * z;
                return (Math.Clamp(NexusCanvas.Width / (sw * scale), 0.01, 1),
                        Math.Clamp(NexusCanvas.Height / (sh * scale), 0.01, 1));
            }
            case BackgroundFit.ScrollLeft:
            case BackgroundFit.ScrollRight:
            {
                // The whole width scrolls past, so only the height is a choice.
                double scale = NexusCanvas.Height / sh * z;
                return (1, Math.Clamp(NexusCanvas.Height / (sh * scale), 0.01, 1));
            }
            default:
                return (1, 1);   // Stretch, Contain, Center and Tile crop nothing
        }
    }
}

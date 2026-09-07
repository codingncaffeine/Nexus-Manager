using NexusManager.Device;
using SkiaSharp;

namespace NexusManager.Render;

/// <summary>
/// A 640x48 drawing surface that hands the NEXUS a ready-to-upload BGRA frame.
///
/// SkiaSharp's Bgra8888 memory layout is byte0=B, byte1=G, byte2=R, byte3=A on
/// little-endian, which is exactly the panel's wire order, so no channel swap
/// is needed on extraction.
/// </summary>
public sealed class NexusCanvas : IDisposable
{
    public const int Width  = NexusDevice.Width;
    public const int Height = NexusDevice.Height;

    private readonly SKBitmap _bitmap;

    /// <summary>
    /// Pre-dither before upload. The panel is 18-bit and truncates the low 2 bits
    /// of every channel in hardware (measured: bits 0-5 of a colour byte produce
    /// nothing visible). Without this, gradients band hard. See D9.
    /// </summary>
    public bool Dither { get; set; } = true;

    public SKCanvas Canvas { get; }

    public NexusCanvas()
    {
        _bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Canvas = new SKCanvas(_bitmap);
    }

    public void Clear(SKColor color) => Canvas.Clear(color);

    /// <summary>
    /// 8x8 ordered Bayer matrix, values 0-63. Ordered dithering is the right
    /// choice here over error diffusion: it is stable frame to frame, so a
    /// static readout does not shimmer, and it costs one add per channel.
    /// </summary>
    private static ReadOnlySpan<byte> Bayer8x8 =>
    [
         0, 32,  8, 40,  2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>
    /// Copies the surface into <paramref name="destination"/> in upload order,
    /// applying the dither offset so the panel's own 8-to-6 bit truncation lands
    /// on a dithered value rather than a hard-quantised one.
    /// </summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length != NexusDevice.FrameBytes)
            throw new ArgumentException(
                $"Destination must be {NexusDevice.FrameBytes} bytes, got {destination.Length}.",
                nameof(destination));

        ReadOnlySpan<byte> src = _bitmap.GetPixelSpan();
        src.CopyTo(destination);

        if (!Dither) return;

        for (int y = 0; y < Height; y++)
        {
            int row = (y & 7) << 3;
            int baseIdx = y * Width * 4;
            for (int x = 0; x < Width; x++)
            {
                // 0-63 scaled to 0-3: exactly the two bits the panel discards.
                int offset = Bayer8x8[row + (x & 7)] >> 4;
                int p = baseIdx + x * 4;
                destination[p]     = Saturate(destination[p]     + offset);   // B
                destination[p + 1] = Saturate(destination[p + 1] + offset);   // G
                destination[p + 2] = Saturate(destination[p + 2] + offset);   // R
                // byte 3 (alpha) is ignored by the panel; leave it.
            }
        }
    }

    private static byte Saturate(int v) => (byte)(v > 255 ? 255 : v);

    public void Dispose()
    {
        Canvas.Dispose();
        _bitmap.Dispose();
    }
}

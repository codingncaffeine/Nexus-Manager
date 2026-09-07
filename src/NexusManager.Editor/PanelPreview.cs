using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NexusManager.Device;
using NexusManager.Render;

namespace NexusManager.Editor;

/// <summary>
/// Shows exactly what the panel shows, scaled up.
///
/// Built on a plain <see cref="Image"/> rather than a custom Control with an
/// overridden Render: an exception inside Render fires every frame and is
/// swallowed into Trace, so the failure presents as a window that simply never
/// paints, with nothing on stderr to explain it.
/// </summary>
public sealed class PanelPreview : Border
{
    private readonly WriteableBitmap _bitmap = new(
        new PixelSize(NexusCanvas.Width, NexusCanvas.Height),
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Opaque);

    private readonly Image _image;
    private readonly byte[] _frame = new byte[NexusDevice.FrameBytes];

    public PanelPreview(int scale = 2)
    {
        _image = new Image
        {
            Source = _bitmap,
            Width = NexusCanvas.Width * scale,
            Height = NexusCanvas.Height * scale,
            Stretch = Stretch.Fill,
        };
        // Nearest-neighbour: the panel has hard pixels, so the preview should too.
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);

        Child = _image;
        BorderBrush = Brushes.DimGray;
        BorderThickness = new Thickness(1);
    }

    /// <summary>Latest frame, ready to hand to the device.</summary>
    public byte[] Frame => _frame;

    /// <summary>Must be called on the UI thread.</summary>
    public void Update(NexusCanvas canvas)
    {
        canvas.CopyTo(_frame);
        using (var buf = _bitmap.Lock())
        {
            for (int y = 0; y < NexusCanvas.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    _frame, y * NexusCanvas.Width * 4,
                    buf.Address + y * buf.RowBytes, NexusCanvas.Width * 4);
        }
        _image.InvalidateVisual();
    }
}

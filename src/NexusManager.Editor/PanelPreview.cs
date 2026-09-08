using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NexusManager.Device;
using NexusManager.Render;

namespace NexusManager.Editor;

/// <summary>
/// Shows exactly what the panel shows, scaled up - and lets you resize the
/// screen's cells by dragging directly on it.
///
/// Resizing used to live in a separate segmented bar underneath. That was wrong
/// twice over: it is a second drawing of a layout you are already looking at,
/// and on a screen with no readouts it rendered NOTHING while the caption below
/// still said "drag a divider to resize" - which is what the user hit. The
/// boundaries belong on the thing they divide.
///
/// Built on a plain <see cref="Image"/> plus a transparent overlay rather than a
/// custom Control with an overridden Render for the bitmap: an exception inside
/// Render fires every frame and is swallowed into Trace, so the failure presents
/// as a window that simply never paints, with nothing on stderr to explain it.
/// </summary>
public sealed class PanelPreview : Border
{
    private readonly WriteableBitmap _bitmap = new(
        new PixelSize(NexusCanvas.Width, NexusCanvas.Height),
        new Vector(96, 96),
        PixelFormat.Bgra8888,
        AlphaFormat.Opaque);

    private readonly Image _image;
    private readonly CellOverlay _overlay;
    private readonly byte[] _frame = new byte[NexusDevice.FrameBytes];

    /// <summary>Raised when a drag changes a weight.</summary>
    public event Action? Changed;
    /// <summary>A readout was clicked, by module index.</summary>
    public event Action<int>? Selected;
    /// <summary>A button was clicked, by button index.</summary>
    public event Action<int>? ButtonSelected;

    public PanelPreview(int scale = 2, bool interactive = false)
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

        _overlay = new CellOverlay
        {
            Width = _image.Width,
            Height = _image.Height,
            IsHitTestVisible = interactive,
            IsVisible = interactive,
        };
        _overlay.Changed += () => Changed?.Invoke();
        _overlay.Selected += i => Selected?.Invoke(i);
        _overlay.ButtonSelected += i => ButtonSelected?.Invoke(i);

        // A Viewbox, not a fixed size. At 2x the preview is 1280px wide inside a
        // ~900px card, so it overflowed by 188px on each side - the overlay was
        // arranged at X = -188 and the pointer never landed on it. DownOnly caps
        // it at the requested scale and shrinks it when the window is narrower.
        Child = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = new Grid { Children = { _image, _overlay } },
        };
        BorderBrush = Brushes.DimGray;
        BorderThickness = new Thickness(1);
    }

    /// <summary>Latest frame, ready to hand to the device.</summary>
    public byte[] Frame => _frame;

    /// <summary>The screen whose cells can be dragged. Null disables the overlay.</summary>
    public void SetScreen(ScreenSpec? screen)
    {
        _overlay.Screen = screen;
        _overlay.InvalidateVisual();
    }

    public int SelectedIndex
    {
        get => _overlay.SelectedIndex;
        set { _overlay.SelectedIndex = value; _overlay.InvalidateVisual(); }
    }

    /// <summary>Grabbable boundaries on the current screen, for the self test.</summary>
    public int DividerCount => _overlay.DividerCount;

    /// <summary>The overlay itself, for diagnostics and for a scripted drag.
    /// Exposed because reading the wiring proved nothing twice running.</summary>
    public CellOverlay Overlay => _overlay;

    /// <summary>True when the overlay can actually receive a drag.</summary>
    public bool Interactive => _overlay.IsHitTestVisible && _overlay.IsVisible;

    public int SelectedButtonIndex
    {
        get => _overlay.SelectedButtonIndex;
        set { _overlay.SelectedButtonIndex = value; _overlay.InvalidateVisual(); }
    }

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

